using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

// ======================================================================
//  Ігри: встановлені ігри (Steam, Epic, GOG, Ubisoft, Xbox) і власні;
//  визначення, чи на передньому плані гра (для FPS і оверлея)
// ======================================================================
sealed class Game
{
    public string Name = "", Dir = "", Source = "";
    public bool On = true;
    // налаштування гри
    public bool OvOwn = true;       // показувати оверлей у цій грі (за замовчуванням увімкнено в усіх, і в іграх з античітом)
    public int IntRaw = -1;         // «Інтеграція в гру» (хуки, ліміт, показ у кадрі): -1 за замовчуванням (є, якщо в грі немає античіта), 0 вимкнено, 1 увімкнено явно
    public bool LimitOn;            // обмеження частоти кадрів (за замовчуванням вимкнено в усіх іграх)
    public double LimFps;           // ліміт; 0 - за частотою монітора
    public int ModeSel = 3;         // показ у справжньому повноекрані: 3 автоматично, 0 накладене вікно, 1 у кадрі гри
    public bool AcNote;             // про оверлей і античіт у цій грі користувача вже сповіщено

    string acC; DateTime acT = DateTime.MinValue; int acV = -1;
    // античіт у грі (кешується на кілька секунд: перевірка читає диск)
    public string Ac
    {
        get
        {
            if (acC == null || acV != AntiCheat.Version || (DateTime.Now - acT).TotalSeconds > 300) { acC = AntiCheat.ForGame(this) ?? ""; acT = DateTime.Now; acV = AntiCheat.Version; }
            return acC;
        }
    }
    // оверлей показується (загальний перемикач і перемикач гри)
    public bool Ov { get { return Games.GenOv && OvOwn; } }
    // інтеграція в гру дозволена: хуки, підміна повноекрану, показ у кадрі, ліміт кадрів. У іграх з античітом - лише за явним вибором
    public bool Integr { get { return IntRaw == 1 || (IntRaw < 0 && Ac.Length == 0); } }
    // користувач сам підтвердив інтеграцію (для гри з античітом - після попередження); тоді перевірка античіта під час запуску не потрібна
    public bool AcOk { get { return IntRaw == 1; } }
    public bool Limit { get { return LimitOn && Integr; } }
    // як показується оверлей у справжньому повноекрані: 3 - автоматично (за API гри), 0 - накладене вікно (повноекран підміняється вікном без рамки), 1 - у кадрі, 2 - без інтеграції (лише окреме вікно поверх гри)
    public int Mode
    {
        get
        {
            if (!Integr) return 2;
            return ModeSel == 0 || ModeSel == 1 ? ModeSel : 3;
        }
    }
    // вигляд оверлею тепер спільний для всіх ігор
    public string LookEnc = "";      // власний вигляд оверлею цієї гри (LookProfile.Encode); порожньо - загальний
    public LookProfile OwnLook()
    {
        if (string.IsNullOrEmpty(LookEnc)) return null;
        try { return LookProfile.Decode(LookEnc, OverlayCfg.Base); } catch { return null; }
    }
    public int MonHz;            // частота монітора, на якому гра працювала востаннє: за нею слідує ліміт «за монітором» у налаштуваннях
    public string Api = "";      // що використовує гра: dx (9-12), opengl, vulkan, ddraw - з останнього запуску
    public string AcSeen = "";  // античіт, знайдений у грі під час запуску
    public string Wrap = "";    // обгортка, яку NeoBar сам поклав у теку гри: d3d8 (d3d8to9) або ddraw (cnc-ddraw)
    public string ExeDir = "";  // тека запущеного exe (де лежать обгортки), відома після першого запуску
    public int Bits;            // розрядність гри: 32 або 64 (0 - невідомо)
    public string WrapKey = ""; // (розрахунок) за якого стану обгортку востаннє звіряли
    public DateTime WrapRetry = DateTime.MinValue;
    public string ExeFile = "";   // (розрахунок) повний шлях запущеного exe
    public bool WrapMiss;       // (розрахунок) обгортка лежить у теці, але гра завантажила системну DirectDraw
    public bool WrapFresh;      // (розрахунок) обгортку покладено, поки гра працює: до перезапуску гри вона не діє
    public string AppId;        // (розрахунок) Steam appid
}

static class Games
{
    public static readonly List<Game> List = new List<Game>();
    // загальні налаштування для всіх ігор (крім ігор з античітом)
    public static bool GenOv = true;         // оверлей показується в іграх (головний перемикач вкладки «Загальне»)
    public static bool VkLayer = true;        // Vulkan-ігри підключаються як шар Vulkan (див. VkLayer.cs); налаштування користувачу не показується
    public static string CurrentName = "";   // поточна гра (на передньому плані або щойно була)
    public static string CurrentExe = "";    // назва файлу гри (для профілю RTSS)
    public static int CurrentPid;
    public static bool InForeground;         // гра зараз на передньому плані
    public static string LastExe = "";       // exe, знайдений останнім викликом Foreground
    public static Game LastGame;             // гра, знайдена останнім викликом Foreground
    public static Game Current;              // поточна гра (об'єкт зі списку)
    static string path;
    static bool loaded;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);

    // шлях exe за pid (кілька разів на секунду для тих самих процесів - кеш на 2 с)
    static readonly Dictionary<int, KeyValuePair<DateTime, string>> exeCache = new Dictionary<int, KeyValuePair<DateTime, string>>();
    public static string ExeOf(int pid)
    {
        KeyValuePair<DateTime, string> c;
        lock (exeCache)
        {
            if (exeCache.TryGetValue(pid, out c) && (DateTime.Now - c.Key).TotalSeconds < 2) return c.Value;
        }
        string r = ExeOfRaw(pid);
        lock (exeCache)
        {
            if (exeCache.Count > 256) exeCache.Clear();
            exeCache[pid] = new KeyValuePair<DateTime, string>(DateTime.Now, r);
        }
        return r;
    }

    static readonly int selfPid = Process.GetCurrentProcess().Id;

    static string ExeOfRaw(int pid)
    {
        IntPtr h = IntPtr.Zero;
        try
        {
            h = Win.OpenProcess(0x1000, false, pid);     // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return "";
            StringBuilder sb = new StringBuilder(1024);
            int n = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : "";
        }
        catch { return ""; }
        finally { if (h != IntPtr.Zero) Win.CloseHandle(h); }
    }

    // чи процес ще працює
    public static bool Alive(int pid)
    {
        if (pid <= 0) return false;
        IntPtr h = IntPtr.Zero;
        try
        {
            h = Win.OpenProcess(0x1000 | 0x100000, false, pid);      // QUERY_LIMITED_INFORMATION | SYNCHRONIZE
            if (h == IntPtr.Zero) return false;
            return Win.WaitForSingleObject(h, 0) != 0;                // 0 - процес завершився
        }
        catch { return false; }
        finally { if (h != IntPtr.Zero) Win.CloseHandle(h); }
    }


    static readonly string[] NotGames = { "explorer", "neobar", "applicationframehost", "searchhost", "startmenuexperiencehost", "shellexperiencehost", "lockapp", "textinputhost" };

    // Чи на передньому плані гра; name - її назва
    public static bool Foreground(out int pid, out string name)
    {
        pid = 0; name = "";
        Load();
        try
        {
            IntPtr h = Win.GetForegroundWindow();
            if (h == IntPtr.Zero) return false;
            Win.GetWindowThreadProcessId(h, out pid);
            if (pid <= 0 || pid == selfPid) return false;
            string exe = ExeOf(pid);
            LastExe = Path.GetFileName(exe);
            string proc = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
            if (proc.Length == 0) return false;
            foreach (string ng in NotGames) if (proc == ng) return false;
            foreach (Game g in List)
            {
                if (!(g.Ov || g.Limit || g.Api.Length == 0) || g.Dir.Length == 0) continue;     // гра, для якої нічого не ввімкнено й API відомий, не відстежується
                string d = g.Dir.TrimEnd('\\') + "\\";
                if (exe.StartsWith(d, StringComparison.OrdinalIgnoreCase)) { name = g.Name; LastGame = g; return true; }
            }
        }
        catch { }
        return false;
    }

    // гра зі списку, до якої належить процес
    public static Game ByPid(int pid)
    {
        if (pid <= 0) return null;
        Load();
        string exe = ExeOf(pid);
        if (exe.Length == 0) return null;
        Game best = null;
        foreach (Game g in List)
        {
            if (g.Dir.Length == 0) continue;
            string d = g.Dir.TrimEnd('\\') + "\\";
            if (exe.StartsWith(d, StringComparison.OrdinalIgnoreCase) && (best == null || g.Dir.Length > best.Dir.Length)) best = g;
        }
        return best;
    }

    // ---------- збереження ----------
    public static void Load()
    {
        if (loaded) return;
        loaded = true;
        path = Prof.File("games.ini");
        bool any = false;
        try
        {
            if (File.Exists(path))
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0 || raw.StartsWith("#")) continue;
                    string k = raw.Substring(0, eq).Trim(), v = raw.Substring(eq + 1);
                    if (k == "overlay") GenOv = v.Trim() != "0";
                    if (k == "game2")
                    {
                        // On|Source|Name|Dir|Ov|IntRaw|LimitOn|LimFps|ModeSel|Api|MonHz|AcSeen|AcNote
                        string[] p = v.Split('|');
                        if (p.Length < 4) continue;
                        Game g = new Game();
                        g.On = p[0] == "1"; g.Source = p[1]; g.Name = p[2]; g.Dir = p[3];
                        g.OvOwn = !(p.Length > 4 && p[4] == "0");
                        int ir;
                        g.IntRaw = p.Length > 5 && int.TryParse(p[5], out ir) && ir >= -1 && ir <= 1 ? ir : -1;
                        g.LimitOn = p.Length > 6 && p[6] == "1";
                        double lf2;
                        if (p.Length > 7 && double.TryParse(p[7], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lf2)) g.LimFps = Math.Max(0, Math.Min(1000, lf2));
                        int ms;
                        g.ModeSel = p.Length > 8 && int.TryParse(p[8], out ms) && (ms == 0 || ms == 1) ? ms : 3;
                        string ap2 = p.Length > 9 ? p[9] : "";
                        int mh2;
                        if (p.Length > 10 && int.TryParse(p[10], out mh2) && mh2 >= 20 && mh2 <= 1000) g.MonHz = mh2;
                        g.AcSeen = p.Length > 11 ? p[11] : "";
                        g.AcNote = p.Length > 12 && p[12] == "1";
                        g.Wrap = p.Length > 13 && (p[13] == "d3d8" || p[13] == "ddraw") ? p[13] : "";
                        g.ExeDir = p.Length > 14 ? p[14] : "";
                        int bt;
                        g.Bits = p.Length > 15 && int.TryParse(p[15], out bt) && (bt == 32 || bt == 64) ? bt : 0;
                        g.LookEnc = p.Length > 16 ? p[16] : "";
                        g.Api = ap2 == "dx" || ap2 == "dx8" || ap2 == "dx9" || ap2 == "dx10" || ap2 == "dx11" || ap2 == "dx12" || ap2 == "opengl" || ap2 == "vulkan" || ap2 == "ddraw" ? ap2 : "";
                        List.Add(g);
                        any = true;
                    }
                }
        }
        catch { }
        if (!any) Scan();
    }

    public static void Save()
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(L.T("# NeoBar - ігри для FPS і оверлея"));
            sb.AppendLine("overlay=" + (GenOv ? 1 : 0));
            foreach (Game g in List)
                sb.AppendLine("game2=" + (g.On ? 1 : 0) + "|" + g.Source + "|" + g.Name.Replace("|", "/") + "|" + g.Dir + "|" + (g.OvOwn ? 1 : 0) + "|" + g.IntRaw + "|" + (g.LimitOn ? 1 : 0) + "|" +
                    g.LimFps.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + g.ModeSel + "|" + (g.Api ?? "") + "|" + g.MonHz + "|" + (g.AcSeen ?? "").Replace("|", "/") + "|" + (g.AcNote ? 1 : 0) + "|" + (g.Wrap ?? "") + "|" + (g.ExeDir ?? "").Replace("|", "/") + "|" + g.Bits + "|" + (g.LookEnc ?? "").Replace("|", "/"));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }
        catch { }
    }

    // ---------- пошук встановлених ігор ----------
    public static int Scan()
    {
        Load();
        List<Game> found = new List<Game>();
        try { Steam(found); } catch { }
        try { Epic(found); } catch { }
        try { Gog(found); } catch { }
        try { Ubisoft(found); } catch { }
        try { Xbox(found); } catch { }
        int added = 0;
        foreach (Game f in found)
        {
            bool have = false;
            foreach (Game g in List) if (string.Equals(g.Dir.TrimEnd('\\'), f.Dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) { have = true; if (g.Name.Length == 0) g.Name = f.Name; }
            if (!have) { List.Add(f); added++; }
        }
        List.Sort(delegate (Game a, Game b) { return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); });
        Save();
        return added;
    }

    public static void AddManual(string dirOrExe)
    {
        Load();
        string dir = File.Exists(dirOrExe) ? Path.GetDirectoryName(dirOrExe) : dirOrExe;
        if (string.IsNullOrEmpty(dir)) return;
        Game g = new Game();
        g.Dir = dir; g.Source = L.T("вручну");
        g.Name = File.Exists(dirOrExe) ? Path.GetFileNameWithoutExtension(dirOrExe) : Path.GetFileName(dir.TrimEnd('\\'));
        List.Add(g);
        Save();
    }

    static void Add(List<Game> l, string name, string dir, string src)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        string n = (name ?? "").Trim();
        if (n.Length == 0) n = Path.GetFileName(dir.TrimEnd('\\'));
        foreach (string skip in new[] { "Redistributable", "Proton", "Steam Linux Runtime", "SteamVR", "Steamworks", "DirectX" })
            if (n.IndexOf(skip, StringComparison.OrdinalIgnoreCase) >= 0) return;
        Game g = new Game();
        g.Name = n; g.Dir = dir; g.Source = src;
        l.Add(g);
    }

    public static string Vdf(string text, string key)
    {
        Match m = Regex.Match(text, "\"" + key + "\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Replace("\\\\", "\\") : "";
    }

    // встановлені ігри Steam для рекомендацій у пошуку: {назва, steam://rungameid/id, іконка або ""}
    public static List<string[]> SteamApps()
    {
        List<string[]> res = new List<string[]>();
        try
        {
            string root = null;
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                if (k != null) root = (k.GetValue("SteamPath") as string ?? "").Replace('/', '\\');
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return res;
            List<string> libs = new List<string>();
            libs.Add(root);
            string vdf = Path.Combine(root, @"steamapps\libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase))
                {
                    string p = m.Groups[1].Value.Replace("\\\\", "\\");
                    if (!libs.Exists(delegate (string x) { return string.Equals(x.TrimEnd('\\'), p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); })) libs.Add(p);
                }
            string cache = Path.Combine(root, @"appcache\librarycache");
            foreach (string lib in libs)
            {
                string apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (string acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
                {
                    try
                    {
                        string t = File.ReadAllText(acf);
                        string name = Vdf(t, "name"), id = Vdf(t, "appid");
                        if (name.Length == 0 || id.Length == 0) continue;
                        bool skip = false;
                        foreach (string sk in new[] { "Redistributable", "Proton", "Steam Linux Runtime", "SteamVR", "Steamworks", "DirectX" })
                            if (name.IndexOf(sk, StringComparison.OrdinalIgnoreCase) >= 0) skip = true;
                        if (skip) continue;
                        res.Add(new string[] { name, "steam://rungameid/" + id, SteamIcon(cache, id) });
                    }
                    catch { }
                }
            }
        }
        catch { }
        return res;
    }

    // маленька іконка гри з кешу Steam (старий і новий формат папки librarycache)
    static string SteamIcon(string cache, string id)
    {
        try
        {
            string old = Path.Combine(cache, id + "_icon.jpg");
            if (File.Exists(old)) return old;
            string dir = Path.Combine(cache, id);
            if (Directory.Exists(dir))
                foreach (string f in Directory.GetFiles(dir, "*.jpg"))
                {
                    string n = Path.GetFileNameWithoutExtension(f);
                    if (n.Length >= 32 && n.IndexOf("library", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("header", StringComparison.OrdinalIgnoreCase) < 0) return f;
                }
        }
        catch { }
        return "";
    }

    static void Steam(List<Game> l)
    {
        string root = null;
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            if (k != null) root = (k.GetValue("SteamPath") as string ?? "").Replace('/', '\\');
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
        List<string> libs = new List<string>();
        libs.Add(root);
        string vdf = Path.Combine(root, @"steamapps\libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase))
            {
                string p = m.Groups[1].Value.Replace("\\\\", "\\");
                if (!libs.Exists(delegate (string x) { return string.Equals(x.TrimEnd('\\'), p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); })) libs.Add(p);
            }
        foreach (string lib in libs)
        {
            string apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (string acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
            {
                string t = File.ReadAllText(acf);
                Add(l, Vdf(t, "name"), Path.Combine(apps, "common\\" + Vdf(t, "installdir")), "Steam");
            }
        }
    }

    static void Epic(List<Game> l)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(dir)) return;
        foreach (string f in Directory.GetFiles(dir, "*.item"))
        {
            string t = File.ReadAllText(f);
            Add(l, Js.Str(t, "DisplayName"), Js.Str(t, "InstallLocation").Replace('/', '\\'), "Epic");
        }
    }

    static void Gog(List<Game> l)
    {
        using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games"))
        {
            if (k == null) return;
            foreach (string id in k.GetSubKeyNames())
                using (RegistryKey g = k.OpenSubKey(id))
                    if (g != null) Add(l, g.GetValue("gameName") as string, g.GetValue("path") as string, "GOG");
        }
    }

    static void Ubisoft(List<Game> l)
    {
        using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs"))
        {
            if (k == null) return;
            foreach (string id in k.GetSubKeyNames())
                using (RegistryKey g = k.OpenSubKey(id))
                    if (g != null) Add(l, null, (g.GetValue("InstallDir") as string ?? "").Replace('/', '\\'), "Ubisoft");
        }
    }

    static void Xbox(List<Game> l)
    {
        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                string x = Path.Combine(d.Name, "XboxGames");
                if (!Directory.Exists(x)) continue;
                foreach (string g in Directory.GetDirectories(x)) Add(l, Path.GetFileName(g), g, "Xbox");
            }
            catch { }
        }
    }
}
