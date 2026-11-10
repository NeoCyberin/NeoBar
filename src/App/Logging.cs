using System;
using System.IO;
using System.Text;

// ======================================================================
//  Журнали для пошуку проблем (розділ «Діагностика» в налаштуваннях). Три перемикачі:
//  панель, ігровий оверлей, віджети й датчики. Файли - у теці logs поруч із програмою;
//  кожен не більший за 2 МБ (старіша частина переходить у .old). Вимкнений журнал нічого не пише
//  й майже нічого не коштує (лише перевірка прапорця). Журнал помилок (neobar-error.log) працює завжди.
// ======================================================================
static class SbLog
{
    // джерела записів; кожне належить до однієї з трьох груп
    public const int Panel = 0, Games = 1, Dll = 2, Widgets = 3, Sensors = 4, Fonts = 5;
    static readonly int[] GroupOf = { 0, 1, 1, 2, 2, 0 };
    static readonly string[] Tag = { "панель", "ігри", "бібліотека", "віджети", "датчики", "шрифти" };

    public static readonly string[] GroupNames = { "panel", "overlay", "widgets" };
    // тексти - щоразу заново (а не в статичному полі): журнал пишуть і з дуже раннього коду, ще до мови інтерфейсу
    public static string[] GroupTitles { get { return new string[] { L.T("Панель"), L.T("Ігровий оверлей"), L.T("Віджети й датчики") }; } }
    public static string[] GroupDescs { get { return new string[]
    {
        L.T("Запуск, профіль, режим закріплення, елементи, шрифти нотаток"),
        L.T("Оверлей, ліміт FPS, обгортки старих API, підрахунок кадрів; бібліотека в грі - з наступного запуску гри"),
        L.T("Погода, свята, час, місце, мережа; процесор, відеокарта, PawnIO")
    }; } }

    static readonly bool[] on = new bool[GroupNames.Length];
    static readonly object gate = new object();
    static bool loaded;

    public static string Dir { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs"); } }
    static string CfgPath { get { return Path.Combine(Dir, "logging.ini"); } }
    static string DllMark { get { return Path.Combine(Dir, "dll.on"); } }
    static string GroupFile(int g) { return Path.Combine(Dir, GroupNames[g] + ".log"); }
    public static string FileOf(int g) { return GroupFile(Math.Max(0, Math.Min(GroupNames.Length - 1, g))); }

    static void Load()
    {
        loaded = true;
        try
        {
            if (!File.Exists(CfgPath)) return;
            foreach (string l in File.ReadAllLines(CfgPath))
            {
                string t = l.Trim().ToLowerInvariant();
                if (!t.EndsWith("=1")) continue;
                string k = t.Substring(0, t.Length - 2).Trim();
                for (int gi = 0; gi < GroupNames.Length; gi++) if (k == GroupNames[gi]) on[gi] = true;
            }
        }
        catch { }
    }

    static void Ensure() { if (!loaded) lock (gate) if (!loaded) Load(); }

    public static bool On(int cat)
    {
        Ensure();
        return cat >= 0 && cat < GroupOf.Length && on[GroupOf[cat]];
    }

    public static bool GroupOn(int g) { Ensure(); return g >= 0 && g < on.Length && on[g]; }

    public static void SetGroup(int g, bool value)
    {
        Ensure();
        if (g < 0 || g >= on.Length) return;
        on[g] = value;
        try
        {
            Directory.CreateDirectory(Dir);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < GroupNames.Length; i++) sb.AppendLine(GroupNames[i] + "=" + (on[i] ? "1" : "0"));
            File.WriteAllText(CfgPath, sb.ToString());
            // бібліотека в грі читає позначку-файл під час підключення (спільної пам'яті ще може не бути)
            if (on[1]) File.WriteAllText(DllMark, "1"); else if (File.Exists(DllMark)) File.Delete(DllMark);
        }
        catch { }
        if (value) Write(g, "журнал", "увімкнено");
    }

    // проігнорована помилка (порожній catch): видно в журналі панелі, коли його ввімкнено;
    // одне й те саме місце - не частіше разу на хвилину (деякі місця спрацьовують щосекунди)
    static readonly System.Collections.Generic.Dictionary<string, int> skipped = new System.Collections.Generic.Dictionary<string, int>();

    public static void Skip(Exception ex,
        [System.Runtime.CompilerServices.CallerMemberName] string member = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (!On(Panel) || ex == null) return;
        // шлях до файлу не беремо (CallerFilePath вшив би в exe шлях теки, де збирали програму)
        string at = member + ":" + line + (ex.TargetSite != null && ex.TargetSite.DeclaringType != null ? " ← " + ex.TargetSite.DeclaringType.Name + "." + ex.TargetSite.Name : "");
        int now = Environment.TickCount, last;
        lock (skipped)
        {
            if (skipped.TryGetValue(at, out last) && unchecked(now - last) < 60000) return;
            skipped[at] = now;
        }
        Write(GroupOf[Panel], "пропущено", at + "  " + ex.GetType().Name + ": " + ex.Message);
    }

    public static void W(int cat, string msg)
    {
        if (!On(cat)) return;
        Write(GroupOf[cat], Tag[cat], msg);
    }

    static void Write(int g, string tag, string msg)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string p = GroupFile(g);
                FileInfo fi = new FileInfo(p);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                {
                    string old = p + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(p, old);
                }
                File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  [" + tag + "] " + msg + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }

    // розмір журналів групи (для оверлею - разом із журналом бібліотеки в грі)
    public static long GroupSize(int g)
    {
        long n = 0;
        try
        {
            FileInfo fi = new FileInfo(GroupFile(g));
            if (fi.Exists) n += fi.Length;
            if (g == 1) { fi = new FileInfo(Path.Combine(Dir, "dll.log")); if (fi.Exists) n += fi.Length; }
        }
        catch { }
        return n;
    }

    public static void Clear()
    {
        lock (gate)
        {
            try
            {
                if (!Directory.Exists(Dir)) return;
                foreach (string f in Directory.GetFiles(Dir, "*.log*")) try { File.Delete(f); } catch { }
            }
            catch { }
        }
    }
}
