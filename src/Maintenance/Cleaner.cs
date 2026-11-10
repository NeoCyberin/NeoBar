using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// ======================================================================
//  Очищення Windows від сміття. Головне - безпека: лише кеші й тимчасові файли, які програми
//  й Windows відтворюють самі; зайняті файли пропускаються; посилання (junction, symlink) ніколи
//  не відкриваються; кеш програми чиститься лише коли вона закрита. Системні компоненти
//  (оновлення, WinSxS) не чіпаються - краще щось лишити, ніж зламати. Реєстр не чиститься: користі для
//  швидкодії це не дає, а ризик зламати програми є (Microsoft такі засоби не підтримує).
// ======================================================================
sealed class JunkTarget
{
    public string Dir = "", Mask = "*";
    public bool Recurse = true, Single;             // Single - Dir - це один файл
    public double MinAgeHours;                      // молодші файли не чіпаємо (можуть бути потрібні просто зараз)
    public string[] Skip = new string[0];           // підпапки, яких не торкаємося
}

sealed class JunkCat
{
    public string Id, Title, Desc;
    public bool Default;
    public string[] Procs = new string[0];          // програми, що мають бути закриті
    public Func<List<JunkTarget>> Targets;
    public bool Recycle;                            // кошик - окремим шляхом (Shell API)
    // результати
    public long Size; public int Files; public string Note = "";
}

static class Cleaner
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBin(string root, ref SHQUERYRBINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, int flags);

    public static readonly List<JunkCat> Cats = new List<JunkCat>();
    public static volatile bool Busy, Stop;
    public static string Stage = "";
    public static long LastFound = -1, LastFreed = -1;
    public static DateTime LastAt = DateTime.MinValue, LastCleanAt = DateTime.MinValue;
    public static event Action Changed;

    static string Local { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
    static string Roam { get { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } }
    static string WinDir { get { return Environment.GetFolderPath(Environment.SpecialFolder.Windows); } }
    static string ProgData { get { return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); } }

    static JunkTarget T(string dir, double age) { JunkTarget t = new JunkTarget(); t.Dir = dir; t.MinAgeHours = age; return t; }
    static JunkTarget F(string file) { JunkTarget t = new JunkTarget(); t.Dir = file; t.Single = true; return t; }

    // кеші браузерів на рушії Chromium: папки профілів у «User Data»
    static void Chromium(List<JunkTarget> l, string userData)
    {
        if (!Directory.Exists(userData)) return;
        List<string> profiles = new List<string>();
        try
        {
            foreach (string d in Directory.GetDirectories(userData))
            {
                string n = Path.GetFileName(d);
                if (n == "Default" || n.StartsWith("Profile ", StringComparison.Ordinal) || n == "Guest Profile") profiles.Add(d);
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        if (File.Exists(Path.Combine(userData, "Cache", "Cache_Data", "index")) || Directory.Exists(Path.Combine(userData, "Code Cache"))) profiles.Add(userData);     // Opera: профіль у корені
        foreach (string p in profiles)
            foreach (string sub in new[] { "Cache", "Code Cache", "GPUCache" })
                l.Add(T(Path.Combine(p, sub), 0));
        l.Add(T(Path.Combine(userData, "ShaderCache"), 0));
        l.Add(T(Path.Combine(userData, "GrShaderCache"), 0));
    }

    public static void Init()
    {
        if (Cats.Count > 0) return;
        Add("usertemp", L.T("Тимчасові файли користувача"), L.T("Папка TEMP; файли, старші за добу"), true, null, delegate
        {
            return new List<JunkTarget> { T(Path.GetTempPath(), 24) };
        });
        Add("wintemp", L.T("Тимчасові файли Windows"), L.T("Windows\\Temp; файли, старші за добу (потрібні права адміністратора)"), true, null, delegate
        {
            return new List<JunkTarget> { T(Path.Combine(WinDir, "Temp"), 24) };
        });
        Add("browsers", L.T("Кеш браузерів"), L.T("Chrome, Edge, Brave, Opera, Vivaldi, Firefox. Лише кеш сторінок - паролі, історія й вхід на сайти не зачіпаються. Браузер має бути закритий"), true,
            new[] { "chrome", "msedge", "brave", "opera", "vivaldi", "firefox", "browser" }, delegate
        {
            List<JunkTarget> l = new List<JunkTarget>();
            Chromium(l, Path.Combine(Local, "Google\\Chrome\\User Data"));
            Chromium(l, Path.Combine(Local, "Microsoft\\Edge\\User Data"));
            Chromium(l, Path.Combine(Local, "BraveSoftware\\Brave-Browser\\User Data"));
            Chromium(l, Path.Combine(Local, "Vivaldi\\User Data"));
            Chromium(l, Path.Combine(Local, "Yandex\\YandexBrowser\\User Data"));
            Chromium(l, Path.Combine(Roam, "Opera Software\\Opera Stable"));
            Chromium(l, Path.Combine(Roam, "Opera Software\\Opera GX Stable"));
            Chromium(l, Path.Combine(Local, "Opera Software\\Opera Stable"));
            Chromium(l, Path.Combine(Local, "Opera Software\\Opera GX Stable"));
            string ff = Path.Combine(Local, "Mozilla\\Firefox\\Profiles");
            try { if (Directory.Exists(ff)) foreach (string d in Directory.GetDirectories(ff)) { l.Add(T(Path.Combine(d, "cache2"), 0)); l.Add(T(Path.Combine(d, "startupCache"), 0)); } } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            return l;
        });
        Add("apps", L.T("Кеш програм"), L.T("Discord, Steam (вбудований браузер), Microsoft Teams (класичний). Програма має бути закрита"), true,
            new[] { "discord", "steam", "steamwebhelper", "teams" }, delegate
        {
            List<JunkTarget> l = new List<JunkTarget>();
            foreach (string d in new[] { "discord", "discordptb", "discordcanary" })
                foreach (string sub in new[] { "Cache", "Code Cache", "GPUCache" }) l.Add(T(Path.Combine(Roam, d + "\\" + sub), 0));
            foreach (string sub in new[] { "Cache", "Code Cache", "GPUCache" }) l.Add(T(Path.Combine(Local, "Steam\\htmlcache\\" + sub), 0));
            foreach (string sub in new[] { "Cache", "Code Cache", "GPUCache", "tmp" }) l.Add(T(Path.Combine(Roam, "Microsoft\\Teams\\" + sub), 0));
            return l;
        });
        Add("inet", L.T("Тимчасові файли Інтернету"), L.T("Кеш Windows для вбудованих сторінок і Office (INetCache), старший за добу"), true, new[] { "outlook" }, delegate
        {
            JunkTarget t = T(Path.Combine(Local, "Microsoft\\Windows\\INetCache"), 24);
            t.Skip = new[] { "Content.Outlook", "Low" };
            return new List<JunkTarget> { t };
        });
        Add("wer", L.T("Звіти про помилки"), L.T("Звіти Windows про збої програм, уже надіслані або в черзі"), true, null, delegate
        {
            return new List<JunkTarget> {
                T(Path.Combine(ProgData, "Microsoft\\Windows\\WER\\ReportArchive"), 1), T(Path.Combine(ProgData, "Microsoft\\Windows\\WER\\ReportQueue"), 1),
                T(Path.Combine(ProgData, "Microsoft\\Windows\\WER\\Temp"), 1), T(Path.Combine(Local, "Microsoft\\Windows\\WER"), 1) };
        });
        Add("dumps", L.T("Дампи пам'яті"), L.T("Знімки пам'яті після збоїв (потрібні лише для діагностики «синіх екранів»)"), true, null, delegate
        {
            JunkTarget md = T(Path.Combine(WinDir, "Minidump"), 0); md.Mask = "*.dmp";
            return new List<JunkTarget> { md, F(Path.Combine(WinDir, "MEMORY.DMP")), T(Path.Combine(Local, "CrashDumps"), 0) };
        });
        Add("logs", L.T("Журнали Windows"), L.T("Старі журнали встановлення й обслуговування (*.log, старші за 30 днів)"), false, null, delegate
        {
            JunkTarget a = T(Path.Combine(WinDir, "Logs"), 24 * 30); a.Mask = "*.log";
            JunkTarget b = T(Path.Combine(WinDir, "Logs"), 24 * 30); b.Mask = "*.etl";
            return new List<JunkTarget> { a, b };
        });
        Add("shaders", L.T("Кеш шейдерів"), L.T("DirectX, AMD, NVIDIA, Intel. Безпечно, але перший запуск ігор після очищення може підвисати, поки кеш збирається знову"), false, null, delegate
        {
            List<JunkTarget> l = new List<JunkTarget>();
            l.Add(T(Path.Combine(Local, "D3DSCache"), 0));
            foreach (string s in new[] { "AMD\\DxCache", "AMD\\DxcCache", "AMD\\GLCache", "AMD\\VkCache", "NVIDIA\\DXCache", "NVIDIA\\GLCache", "NVIDIA Corporation\\NV_Cache", "Intel\\ShaderCache" })
                l.Add(T(Path.Combine(Local, s), 0));
            return l;
        });
        Add("thumbs", L.T("Кеш мініатюр"), L.T("Ескізи картинок і значки Провідника; Windows створить їх знову (зайняті файли пропускаються)"), false, null, delegate
        {
            JunkTarget a = T(Path.Combine(Local, "Microsoft\\Windows\\Explorer"), 0); a.Mask = "thumbcache_*.db"; a.Recurse = false;
            return new List<JunkTarget> { a };
        });
        JunkCat rb = Add("recycle", L.T("Кошик"), L.T("Видалені файли на всіх дисках. Після очищення їх уже не повернути з кошика"), false, null, null);
        rb.Recycle = true;
    }

    static JunkCat Add(string id, string title, string desc, bool def, string[] procs, Func<List<JunkTarget>> targets)
    {
        JunkCat c = new JunkCat();
        c.Id = id; c.Title = title; c.Desc = desc; c.Default = def; c.Targets = targets;
        if (procs != null) c.Procs = procs;
        Cats.Add(c);
        return c;
    }

    public static bool On(Cfg cfg, JunkCat c)
    {
        string flip = "," + (cfg != null ? cfg.CleanFlip : "") + ",";
        bool flipped = flip.Contains("," + c.Id + ",");
        return c.Default != flipped;
    }

    // скільки можна звільнити ВИБРАНИМ - саме це число показують і вікно, і віджети (категорії, що вимкнені, не рахуються)
    public static long Pending(Cfg cfg)
    {
        long s = 0;
        foreach (JunkCat c in Cats) if (On(cfg, c)) s += c.Size;
        return s;
    }

    public static void Toggle(Cfg cfg, JunkCat c)
    {
        List<string> l = new List<string>();
        foreach (string s in (cfg.CleanFlip ?? "").Split(',')) if (s.Length > 0 && s != c.Id) l.Add(s);
        if (On(cfg, c) == c.Default) l.Add(c.Id);
        cfg.CleanFlip = string.Join(",", l.ToArray());
        if (LastAt != DateTime.MinValue) { LastFound = Pending(cfg); Fire(); }
    }

    static string Running(JunkCat c)
    {
        foreach (string p in c.Procs)
            try
            {
                Process[] ps = Process.GetProcessesByName(p);
                bool any = ps.Length > 0;
                foreach (Process x in ps) x.Dispose();
                if (any) return p;
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return null;
    }

    // шлях, що його ми взагалі погоджуємося чистити: не корінь диска, не профіль, не Windows як такий
    static bool SafeRoot(string dir)
    {
        try
        {
            string full = Path.GetFullPath(dir).TrimEnd('\\');
            if (full.Length <= 3) return false;
            string[] never = { WinDir, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Local, Roam, ProgData,
                               Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                               Path.Combine(WinDir, "System32"), Path.Combine(WinDir, "SysWOW64") };
            foreach (string n in never)
                if (n.Length > 0 && string.Equals(full, n.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        catch { return false; }
    }

    static bool Reparse(FileSystemInfo fi) { return (fi.Attributes & FileAttributes.ReparsePoint) != 0; }

    // обхід файлів цілі (без переходу за посиланнями); act повертає false - зупинитися
    static void Walk(JunkTarget t, Action<FileInfo> act)
    {
        if (t.Single)
        {
            try { FileInfo f = new FileInfo(t.Dir); if (f.Exists && !Reparse(f)) act(f); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            return;
        }
        if (!SafeRoot(t.Dir)) return;
        DirectoryInfo root;
        try { root = new DirectoryInfo(t.Dir); if (!root.Exists || Reparse(root)) return; } catch { return; }
        DateTime limit = DateTime.UtcNow.AddHours(-t.MinAgeHours);
        Stack<DirectoryInfo> st = new Stack<DirectoryInfo>();
        st.Push(root);
        while (st.Count > 0 && !Stop)
        {
            DirectoryInfo d = st.Pop();
            try
            {
                foreach (FileInfo f in d.GetFiles(t.Mask))
                {
                    if (Stop) return;
                    if (Reparse(f)) continue;
                    DateTime w = f.LastWriteTimeUtc > f.CreationTimeUtc ? f.LastWriteTimeUtc : f.CreationTimeUtc;
                    if (t.MinAgeHours > 0 && w > limit) continue;
                    act(f);
                }
                if (!t.Recurse) continue;
                foreach (DirectoryInfo s in d.GetDirectories())
                {
                    if (Reparse(s)) continue;
                    bool skip = false;
                    foreach (string k in t.Skip) if (string.Equals(s.Name, k, StringComparison.OrdinalIgnoreCase) && d.FullName.TrimEnd('\\') == root.FullName.TrimEnd('\\')) skip = true;
                    if (!skip) st.Push(s);
                }
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }       // немає доступу - пропускаємо
        }
    }

    // порожні вкладені папки (сам корінь цілі лишається)
    static void Prune(JunkTarget t)
    {
        if (t.Single || !t.Recurse || !SafeRoot(t.Dir)) return;
        try
        {
            DirectoryInfo root = new DirectoryInfo(t.Dir);
            if (!root.Exists || Reparse(root)) return;
            List<DirectoryInfo> all = new List<DirectoryInfo>();
            Stack<DirectoryInfo> st = new Stack<DirectoryInfo>();
            st.Push(root);
            while (st.Count > 0)
            {
                DirectoryInfo d = st.Pop();
                try { foreach (DirectoryInfo s in d.GetDirectories()) if (!Reparse(s)) { all.Add(s); st.Push(s); } } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            }
            for (int i = all.Count - 1; i >= 0; i--)
                try { if (all[i].GetFileSystemInfos().Length == 0) all[i].Delete(false); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    static void Fire() { Action a = Changed; if (a != null) try { a(); } catch (Exception skipEx) { SbLog.Skip(skipEx); } }

    // аналіз (clean = false) або очищення ввімкнених категорій; у фоновому потоці
    public static void Run(Cfg cfg, bool clean)
    {
        if (Busy) return;
        Init();
        Busy = true; Stop = false;
        List<JunkCat> todo = new List<JunkCat>();
        foreach (JunkCat c in Cats) if (!clean || On(cfg, c)) todo.Add(c);
        Thread th = new Thread(delegate ()
        {
            long freedAll = 0;
            try
            {
                foreach (JunkCat c in todo)
                {
                    if (Stop) break;
                    Stage = (clean ? L.T("Очищення: ") : L.T("Аналіз: ")) + c.Title;
                    Fire();
                    long size = 0; int files = 0, skipped = 0;
                    c.Note = "";
                    if (c.Recycle)
                    {
                        SHQUERYRBINFO qi = new SHQUERYRBINFO(); qi.cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO));
                        try { if (SHQueryRecycleBin(null, ref qi) == 0) { size = qi.i64Size; files = (int)Math.Min(int.MaxValue, qi.i64NumItems); } } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                        if (clean && files > 0)
                        {
                            try { SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4); } catch (Exception skipEx) { SbLog.Skip(skipEx); }      // без підтвердження, вікна прогресу й звуку
                            freedAll += size; size = 0; files = 0;
                        }
                    }
                    else
                    {
                        string run = Running(c);
                        if (run != null)
                        {
                            c.Note = L.T("Пропущено: відкрито ") + run + L.T(" - закрийте й повторіть");
                            c.Size = 0; c.Files = 0;
                            Fire();
                            continue;
                        }
                        List<JunkTarget> ts;
                        try { ts = c.Targets(); } catch { ts = new List<JunkTarget>(); }
                        foreach (JunkTarget t in ts)
                        {
                            if (Stop) break;
                            Walk(t, delegate (FileInfo f)
                            {
                                long len = 0;
                                try { len = f.Length; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                                if (!clean) { size += len; files++; return; }
                                try
                                {
                                    if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
                                    f.Delete();
                                    freedAll += len;
                                }
                                catch { size += len; files++; skipped++; }       // зайнятий - лишається
                            });
                            if (clean) Prune(t);
                        }
                        if (clean && skipped > 0) c.Note = L.T("Зайняті й пропущені: ") + skipped;
                    }
                    c.Size = size; c.Files = files;
                    Fire();
                }
            }
            catch (Exception ex) { Util.ErrorLog(ex); }
            finally
            {
                if (clean) { LastFreed = freedAll; LastCleanAt = DateTime.Now; }
                LastFound = Pending(cfg);
                LastAt = DateTime.Now;
                Busy = false; Stage = "";
                Fire();
            }
        });
        th.IsBackground = true;
        th.Priority = ThreadPriority.BelowNormal;
        th.Start();
    }

    public static string Size(long b)
    {
        if (b < 0) return "—";
        if (b < 1024) return b + L.T(" Б");
        if (b < 1024L * 1024) return (b / 1024.0).ToString("0") + L.T(" КБ");
        if (b < 1024L * 1024 * 1024) return (b / 1048576.0).ToString("0.#") + L.T(" МБ");
        return (b / 1073741824.0).ToString("0.##") + L.T(" ГБ");
    }
}

sealed class CleanWindow : SbWindow
{
    int scroll;
    bool cleaned;

    public CleanWindow(NeoBar bar) : base(bar, L.T("Очищення"), 540, 800)
    { Glyph = "\uE74D";
        Cleaner.Init();
        Cleaner.Changed += OnData;
        if (!Cleaner.Busy && Cleaner.LastAt == DateTime.MinValue) Cleaner.Run(Bar.Config, false);     // перший аналіз - одразу
    }

    void OnData()
    {
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(delegate { Invalidate(); if (!Cleaner.Busy && Bar != null) Bar.CleanRedraw(); })); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    // зліва направо: «на робочий стіл» (віджет-кошик), закрити
    protected override void HeadButtons(Graphics g, ref int right)
    {
        Rectangle rr = new Rectangle(right - Px(32), Px(7), Px(32), Px(32));
        IconBtn(g, rr, "desk", delegate { ClockData.AddDesk("trash"); }, false);
        right = rr.Left - Px(4);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Cleaner.Changed -= OnData;
        base.OnFormClosed(e);
    }

    long Selected()
    {
        return Cleaner.Pending(Bar.Config);
    }

    void Clean()
    {
        long sel = Selected();
        bool rec = false;
        foreach (JunkCat c in Cleaner.Cats) if (c.Recycle && Cleaner.On(Bar.Config, c) && c.Files > 0) rec = true;
        string text = L.T("Видалити вибране сміття (") + Cleaner.Size(sel) + ")?" + (rec ? L.T("\n\nКошик буде очищено - звідти файли вже не повернути.") : "") +
                      L.T("\n\nЗайняті файли й кеш відкритих програм буде пропущено.");
        if (SbDialog.Show(Bar, this, L.T("Очищення"), text, L.T("Очистити"), L.T("Скасувати")) != 0) return;
        cleaned = true;
        Cleaner.Run(Bar.Config, true);
        Invalidate();
    }

    void Save()
    {
        try { Store.Save(Bar.CfgPath, Bar.Config, Bar.Entries); } catch (Exception ex) { Util.ErrorLog(ex); }
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        PaintFiles(g, r);
    }

    void PaintFiles(Graphics g, Rectangle r)
    {
        int y = r.Y;
        Rectangle top = new Rectangle(r.X, y, r.Width, Px(86));
        Card(g, top);
        bool busy = Cleaner.Busy;
        string big = busy ? "…" : (cleaned && Cleaner.LastFreed >= 0 ? Cleaner.Size(Cleaner.LastFreed) : Cleaner.Size(Selected()));
        string cap = busy ? Cleaner.Stage : (cleaned && Cleaner.LastFreed >= 0 ? L.T("Звільнено") : L.T("Можна звільнити (вибране)"));
        TxtL(g, cap, FS, new Rectangle(top.X + Px(14), top.Y + Px(10), top.Width - Px(180), Px(18)), P.Sub);
        TxtL(g, big, FBig, new Rectangle(top.X + Px(14), top.Y + Px(30), top.Width - Px(180), Px(44)), P.Fg);
        if (busy) Btn(g, new Rectangle(top.Right - Px(150), top.Y + (top.Height - Px(36)) / 2, Px(136), Px(36)), L.T("Зупинити"), delegate { Cleaner.Stop = true; }, 0, true);
        else
        {
            Btn(g, new Rectangle(top.Right - Px(150), top.Y + Px(10), Px(136), Px(30)), L.T("Аналіз"), delegate { cleaned = false; Cleaner.Run(Bar.Config, false); Invalidate(); }, 0, true);
            Btn(g, new Rectangle(top.Right - Px(150), top.Y + Px(46), Px(136), Px(30)), L.T("Очистити"), Clean, 1, Selected() > 0);
        }
        y = top.Bottom + Px(12);

        // категорії
        int rowH = Px(58);
        Rectangle list = new Rectangle(r.X, y, r.Width, r.Bottom - y - Px(46));
        int total = Cleaner.Cats.Count * rowH;
        int maxScroll = Math.Max(0, total - list.Height);
        scroll = Math.Max(0, Math.Min(maxScroll, scroll));
        GraphicsState st = g.Save();
        g.SetClip(list);
        for (int i = 0; i < Cleaner.Cats.Count; i++)
        {
            JunkCat c = Cleaner.Cats[i];
            Rectangle rr = new Rectangle(list.X, list.Y + i * rowH - scroll, list.Width, rowH - Px(6));
            if (rr.Bottom < list.Y || rr.Top > list.Bottom) continue;
            bool on = Cleaner.On(Bar.Config, c);
            Card(g, rr, Over(rr) ? Util.Blend(P.Card, P.Fg, 0.05f) : P.Card);
            int sw = Px(36), sh = Px(20);
            Rectangle tr = new Rectangle(rr.Right - sw - Px(12), rr.Y + Px(10), sw, sh);
            using (SolidBrush b = new SolidBrush(on ? Util.Blend(P.Bg, P.Acc, 0.7f) : Util.Blend(P.Bg, P.Fg, 0.22f))) Util.FillRound(g, b, tr, sh / 2);
            int k = sh - Px(6);
            using (SolidBrush b = new SolidBrush(on ? Color.White : Util.Blend(P.Bg, P.Fg, 0.7f))) g.FillEllipse(b, new Rectangle(on ? tr.Right - k - Px(3) : tr.X + Px(3), tr.Y + Px(3), k, k));
            string sz = c.Note.Length > 0 && c.Size == 0 ? "" : (c.Files > 0 || Cleaner.LastAt != DateTime.MinValue ? Cleaner.Size(c.Size) : "");
            int szw = Px(90);
            TxtR(g, sz, FB, new Rectangle(tr.X - szw - Px(8), rr.Y + Px(6), szw, Px(26)), c.Size > 0 ? P.Fg : P.Sub);
            TxtL(g, c.Title, F, new Rectangle(rr.X + Px(12), rr.Y + Px(6), tr.X - szw - Px(20) - rr.X, Px(26)), P.Fg);
            string d = c.Note.Length > 0 ? c.Note : c.Desc;
            TxtL(g, d, FS, new Rectangle(rr.X + Px(12), rr.Y + Px(30), rr.Width - Px(24), Px(18)), c.Note.Length > 0 ? P.Acc : P.Sub);
            JunkCat cc = c;
            Tip(Rectangle.Intersect(rr, list), c.Title + (sz.Length > 0 ? " - " + sz : "") + "\n" + d + (c.Note.Length > 0 ? "\n\n" + c.Desc : ""));      // повний текст: рядок вище обрізається трьома крапками
            if (!busy && Rectangle.Intersect(rr, list) == rr) Hot(rr, delegate { Cleaner.Toggle(Bar.Config, cc); Save(); Invalidate(); });
        }
        g.Restore(st);
        Wheel(list, delegate (int dd) { scroll -= dd > 0 ? rowH : -rowH; Invalidate(); });
        VBar(g, new Rectangle(list.Right - Px(6), list.Y + Px(2), Px(4), list.Height - Px(10)), total, list.Height, scroll, delegate (int v) { scroll = v; Invalidate(); });
        TxtW(g, L.T("Системні компоненти Windows (оновлення, WinSxS) не чіпаються - для безпеки. Зайняті файли й кеш відкритих програм пропускаються. Реєстр - на вкладці «Реєстр»."),
            FS, new Rectangle(r.X, r.Bottom - Px(40), r.Width, Px(40)), P.Sub);
    }
}
