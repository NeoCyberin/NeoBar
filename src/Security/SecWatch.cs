using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// ======================================================================
//  Фонове стеження «Безпеки»: працює, коли воно ввімкнене, віджет «Безпека» хоч раз був на панелі (після
//  цього віджет можна й прибрати - стеження лишається, а в треї є значок) і не запущена гра. Ресурси бережуться: раз на хвилину - лише знімок списку процесів (нові процеси -
//  тоді їх аналіз), автозапуск - раз на 10 хвилин, служби - раз на 20, налаштування Windows - раз на годину.
//  Про нову підозрілу знахідку - одне сповіщення; та сама знахідка повторно не сповіщається до перезавантаження ПК.
// ======================================================================
static class SecWatch
{
    [DllImport("kernel32.dll")] static extern ulong GetTickCount64();

    static NeoBar bar;
    static System.Windows.Forms.Timer timer;
    static volatile bool busy;
    static DateTime nextAuto = DateTime.MinValue, nextSvc = DateTime.MinValue, nextSys = DateTime.MinValue;
    static bool first = true;
    static readonly HashSet<string> told = new HashSet<string>();
    static long boot;

    static string FilePath { get { return Path.Combine(Prof.Root, "security-notified.txt"); } }

    public static void Init(NeoBar b)
    {
        bar = b;
        Touch();
        SecTray.Sync(bar);
        try { boot = DateTime.UtcNow.AddMilliseconds(-(double)GetTickCount64()).Ticks; } catch { boot = 0; }
        LoadTold();
        timer = new System.Windows.Forms.Timer();
        timer.Interval = 3 * 60000;           // перший раз - через 3 хвилини, щоб не заважати запуску Windows
        timer.Tick += delegate { timer.Interval = 60000; Tick(); };
        timer.Start();
    }

    // уже показані сповіщення - до перезавантаження ПК (запис з іншим часом старту Windows не діє)
    static void LoadTold()
    {
        try
        {
            if (!SafeFile.Exists(FilePath)) return;
            string[] l = SafeFile.ReadLines(FilePath);
            long b;
            if (l.Length == 0 || !long.TryParse(l[0], out b) || Math.Abs(b - boot) > TimeSpan.FromMinutes(2).Ticks) return;
            for (int i = 1; i < l.Length; i++) if (l[i].Length > 0) told.Add(l[i]);
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    static void SaveTold()
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(boot).Append("\r\n");
            lock (told) foreach (string k in told) sb.Append(k.Replace("\r", " ").Replace("\n", " ")).Append("\r\n");
            SafeFile.Write(FilePath, sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    // стеження ввімкнене й «озброєне» (значок у треї є)
    public static bool Enabled
    {
        get
        {
            try { return bar != null && bar.Config.SecWatch && bar.Config.SecUsed; }
            catch { return false; }
        }
    }

    public static bool Active
    {
        get
        {
            try { return Enabled && Games.CurrentPid <= 0; }
            catch { return false; }
        }
    }

    static void SaveCfg()
    {
        try { Store.Save(bar.CfgPath, bar.Config, bar.Entries); } catch (Exception ex) { Util.ErrorLog(ex); }
    }

    // віджет «Безпека» з'явився на панелі - відтоді стеження «озброєне» (навіть коли віджет потім приберуть)
    public static void Touch()
    {
        try
        {
            if (bar == null || bar.Config.SecUsed || !bar.HasWidget("security")) return;
            bar.Config.SecUsed = true;
            SaveCfg();
            SecTray.Sync(bar);
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    // вмикання й вимикання (вікно «Безпеки», меню значка в треї)
    public static void SetWatch(bool on)
    {
        if (bar == null) return;
        bar.Config.SecWatch = on;
        if (on) bar.Config.SecUsed = true;
        SaveCfg();
        if (on) first = true;
        SecTray.Sync(bar);
    }

    static void Tick()
    {
        Touch();
        SecTray.Refresh();
        if (busy || SecScan.Busy || !Active) return;
        DateTime now = DateTime.Now;
        bool auto = now >= nextAuto, svc = now >= nextSvc, sys = now >= nextSys;
        bool procs = SecScan.NewProcesses() || first;
        if (!procs && !auto && !svc && !sys) return;
        string ac;
        if (procs && SecScan.AntiCheatRunning(out ac)) procs = false;       // працює служба античіта - процеси не чіпаємо взагалі
        if (auto) nextAuto = now.AddMinutes(10);
        if (svc) nextSvc = now.AddMinutes(20);
        if (sys) nextSys = now.AddMinutes(60);
        first = false;
        busy = true;
        Thread t = new Thread(delegate ()
        {
            List<Finding> fresh = new List<Finding>();
            try
            {
                SecScan.Background(procs, auto, svc, sys);
                foreach (List<Finding> l in new List<Finding>[] { SecScan.Proc, SecScan.Auto, SecScan.Svc, SecScan.Sys })
                    foreach (Finding f in l)
                    {
                        if (f.Ok || f.Off || f.Allowed || f.Level < 1) continue;
                        string k = f.Area + "|" + SecLists.KeyOf(f);
                        lock (told) if (told.Add(k)) fresh.Add(f);
                    }
                if (fresh.Count > 0) SaveTold();
            }
            catch (Exception ex) { Util.ErrorLog(ex); }
            finally { busy = false; }
            try { bar.BeginInvoke(new Action(delegate { Done(fresh); })); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        });
        t.IsBackground = true;
        t.Priority = ThreadPriority.Lowest;
        t.Start();
    }

    static void Done(List<Finding> fresh)
    {
        try { bar.WidgetRedraw("security"); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        if (fresh.Count == 0 || !Active) return;
        SecTray.Alert(true);
        fresh.Sort(delegate (Finding a, Finding b) { return b.Score.CompareTo(a.Score); });
        Finding f = fresh[0];
        bool bad = false;
        foreach (Finding x in fresh) if (x.Level == 2) bad = true;
        string why = f.Why.Count > 0 ? f.Why[0] : "";
        if (why.Length > 140) why = why.Substring(0, 137) + "…";
        string body = f.Title + (why.Length > 0 ? ": " + why : "") + (fresh.Count > 1 ? L.T("\nІ ще знахідок: ") + (fresh.Count - 1) + "." : "") + L.T("\nКлік - подробиці.");
        ToastForm.ShowClick(bar, bad ? L.T("Безпека: підозріла активність") : L.T("Безпека: варто перевірити"), body, "shield", delegate { bar.OpenSecurity(); });
    }
}
