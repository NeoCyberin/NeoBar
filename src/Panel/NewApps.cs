using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

// ======================================================================
//  Нова програма чи гра встановилась - запропонувати додати її на панель (у вибрану групу).
//  Ознака встановлення - новий ярлик у меню «Пуск» (загальному чи користувача). Стежить FileSystemWatcher:
//  жодного опитування, Windows сама повідомляє про новий файл.
//  Що НЕ пропонується: ярлики видалення, довідки, сайтів, файли не-exe, те, що вже є на панелі.
//  Поки грають, екран вимкнено чи відкриті налаштування - пропозиція чекає.
// ======================================================================
static class NewApps
{
    static readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
    static readonly List<string> pending = new List<string>();
    static readonly HashSet<string> offered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static Timer timer;
    static NeoBar bar;

    // слова в назві ярлика чи файлу, що означають «не програма для запуску»
    static readonly string[] Skip =
    {
        "uninstall", "uninst", "unins0", "remove", "deinstall", "видалити", "видалення", "удалить", "удаление", "deinstallieren", "désinstaller", "desinstalar",
        "readme", "read me", "help", "manual", "documentation", "license", "licence", "changelog", "release notes", "website", "web site", "homepage",
        "support", "довідка", "справка", "посібник", "updater", "update", "setup", "install", "repair", "crash", "report", "safe mode", "debug"
    };

    // увімкнути чи вимкнути стеження (з налаштувань)
    public static void Configure(NeoBar b, bool on)
    {
        bar = b;
        if (on == (watchers.Count > 0)) return;
        if (!on) { Stop(); return; }
        foreach (Environment.SpecialFolder sf in new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Programs })
        {
            try
            {
                string dir = Environment.GetFolderPath(sf);
                if (dir.Length == 0 || !Directory.Exists(dir)) continue;
                FileSystemWatcher w = new FileSystemWatcher(dir);
                w.IncludeSubdirectories = true;
                w.NotifyFilter = NotifyFilters.FileName;
                w.InternalBufferSize = 8192;
                w.Created += delegate (object s, FileSystemEventArgs e) { Add(e.FullPath); };
                w.Renamed += delegate (object s, RenamedEventArgs e) { Add(e.FullPath); };
                w.EnableRaisingEvents = true;
                watchers.Add(w);
            }
            catch (Exception ex) { SbLog.Skip(ex); }
        }
        SbLog.W(SbLog.Panel, "нові програми: стежу за меню «Пуск» (" + watchers.Count + ")");
    }

    public static void Stop()
    {
        foreach (FileSystemWatcher w in watchers) try { w.EnableRaisingEvents = false; w.Dispose(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        watchers.Clear();
        lock (pending) pending.Clear();
        if (timer != null) timer.Stop();
    }

    // з потоку FileSystemWatcher
    static void Add(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext != ".lnk" && ext != ".url") return;
        lock (pending) if (!pending.Contains(path)) pending.Add(path);
        NeoBar b = bar;
        if (b == null || b.IsDisposed || !b.IsHandleCreated) return;
        try { b.BeginInvoke(new Action(Arm)); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    // інсталятор створює ярлики пачкою - чекаємо кілька секунд тиші
    static void Arm()
    {
        if (timer == null)
        {
            timer = new Timer();
            timer.Tick += delegate { timer.Stop(); Process(); };
        }
        timer.Stop();
        timer.Interval = 6000;
        timer.Start();
    }

    static bool Busy
    {
        get
        {
            NeoBar b = bar;
            return b == null || b.IsDisposed || Games.InForeground || NeoBar.Paused || ScreenState.Away || b.SettingsOpen;
        }
    }

    static void Process()
    {
        if (Busy)
        {
            // пізніше: гра, повноекранна програма, вимкнений екран чи відкриті налаштування
            timer.Interval = 30000;
            lock (pending) if (pending.Count > 0) timer.Start();
            return;
        }
        List<string> l;
        lock (pending) { l = new List<string>(pending); pending.Clear(); }
        int shown = 0;
        foreach (string p in l)
        {
            if (shown >= 3) break;          // великий набір ярликів - не засипати сповіщеннями
            string name;
            if (!Worth(p, out name)) continue;
            offered.Add(p);
            shown++;
            string path = p;
            ToastForm.ShowClick(bar, L.F("Встановлено «{0}»", name), L.T("Клацніть, щоб додати на панель - у групу на вибір"), "\uE710",
                delegate { if (bar != null && !bar.IsDisposed) bar.OfferNewApp(path, name); });
        }
    }

    // чи варто пропонувати цей ярлик; name - назва для показу
    static bool Worth(string p, out string name)
    {
        name = Path.GetFileNameWithoutExtension(p);
        try
        {
            if (offered.Contains(p) || !File.Exists(p)) return false;
            if ((DateTime.Now - File.GetCreationTime(p)).TotalMinutes > 10) return false;        // старий ярлик, лише перейменований
            string low = name.ToLowerInvariant();
            foreach (string s in Skip) if (low.Contains(s)) return false;
            string target;
            if (Path.GetExtension(p).Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                // інтернет-ярлик - лише ігрові лаунчери (steam://, com.epicgames.launcher:// тощо), не сайти
                string url = "";
                foreach (string line in File.ReadAllLines(p))
                    if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) url = line.Substring(4).Trim();
                if (url.Length == 0 || url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return false;
                target = url;
            }
            else
            {
                target = IconHi.LinkTarget(p);
                if (string.IsNullOrEmpty(target)) return false;
                target = Environment.ExpandEnvironmentVariables(target);
                if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) return false;
                string exe = Path.GetFileNameWithoutExtension(target).ToLowerInvariant();
                foreach (string s in Skip) if (exe.Contains(s)) return false;
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (win.Length > 0 && target.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(target, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return !bar.HasTarget(p, target, name);
        }
        catch (Exception ex) { SbLog.Skip(ex); return false; }
    }
}
