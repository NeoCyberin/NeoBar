using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// ----------------------------------------------------------------------
//  Пошук античіта. Жодних списків «таких-то ігор»: дивимось на докази.
//    1. Файли в теці гри (на глибину до 3 рівнів): EasyAntiCheat, BattlEye, ACE, XIGNCODE, GameGuard тощо.
//    2. Steam: магазин позначає ігри з «Valve Anti-Cheat enabled» (VAC) - читається один раз і кешується.
//    3. Під час запуску гри: служби античіта, процеси, модулі всередині гри.
//  Знайдене - це попередження, а не заборона: користувач вирішує сам (наприклад, у Left 4 Dead 2 грають
//  і на локальному сервері без VAC). Підтверджений ризик запам'ятовується для кожної гри окремо.
// ----------------------------------------------------------------------
static class AntiCheat
{
    // фрагменти назв файлів і папок
    static readonly string[][] Marks =
    {
        new[] { "easyanticheat", "EasyAntiCheat" }, new[] { "eac_launcher", "EasyAntiCheat" }, new[] { "start_protected_game", "EasyAntiCheat" },
        new[] { "battleye", "BattlEye" }, new[] { "beclient", "BattlEye" }, new[] { "beservice", "BattlEye" },
        new[] { "eaanticheat", "EA AntiCheat" }, new[] { "anticheatexpert", "Anti-Cheat Expert" }, new[] { "ace-base", "Anti-Cheat Expert" },
        new[] { "xigncode", "XIGNCODE" }, new[] { "gameguard", "GameGuard" }, new[] { "nprotect", "nProtect" }, new[] { "npggnt", "GameGuard" },
        new[] { "pnkbstr", "PunkBuster" }, new[] { "punkbuster", "PunkBuster" }, new[] { "mhyprot", "miHoYo Protect" }, new[] { "hoyokprotect", "HoYoverse Protect" },
        new[] { "faceit", "FACEIT" }, new[] { "equ8", "EQU8" }, new[] { "hackshield", "HackShield" }, new[] { "wellbia", "XIGNCODE" },
        new[] { "anticheat", L.T("античіт") }, new[] { "anti-cheat", L.T("античіт") }, new[] { "anti_cheat", L.T("античіт") }
    };

    // процеси служб античіта
    static readonly string[] AcProcesses =
    {
        "easyanticheat", "easyanticheat_eos", "beservice", "beservice_x64", "faceitservice", "faceit", "eaanticheat.gameservice",
        "eaanticheat.gameservicelauncher", "sguard64", "ace-tray", "xigncode", "gameguard", "pnkbstra", "pnkbstrb", "mhyprot", "vgc"
    };

    // процес служби античіта (за назвою файла, з «.exe» чи без)
    public static bool IsService(string exeName)
    {
        string n = (exeName ?? "").ToLowerInvariant();
        if (n.EndsWith(".exe")) n = n.Substring(0, n.Length - 4);
        return Array.IndexOf(AcProcesses, n) >= 0;
    }

    public static int Version;       // збільшується, коли надійшли нові дані (Steam) - інтерфейс може оновити позначки

    static readonly Dictionary<string, string> dirCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // ---------- 1. файли в теці ----------
    public static string InDir(string dir, int depth)
    {
        if (string.IsNullOrEmpty(dir)) return "";
        string key = dir + "|" + depth, hit;
        lock (dirCache) if (dirCache.TryGetValue(key, out hit)) return hit;
        string r = "";
        try
        {
            Queue<KeyValuePair<string, int>> q = new Queue<KeyValuePair<string, int>>();
            if (Directory.Exists(dir)) q.Enqueue(new KeyValuePair<string, int>(dir, 0));
            int seen = 0;
            while (q.Count > 0 && r.Length == 0 && seen < 6000)
            {
                KeyValuePair<string, int> cur = q.Dequeue();
                foreach (string e in Directory.GetFileSystemEntries(cur.Key))
                {
                    seen++;
                    string n = Path.GetFileName(e).ToLowerInvariant();
                    foreach (string[] m in Marks) if (n.Contains(m[0])) { r = m[1]; break; }
                    if (r.Length > 0) break;
                    if (cur.Value < depth && Directory.Exists(e)) q.Enqueue(new KeyValuePair<string, int>(e, cur.Value + 1));
                    if (seen >= 6000) break;
                }
            }
        }
        catch { }
        lock (dirCache) dirCache[key] = r;
        return r;
    }

    // ---------- 2. Steam: VAC ----------
    static readonly Dictionary<string, int> vac = new Dictionary<string, int>();        // appid -> 1 є VAC, 0 немає, -1 невідомо
    static readonly Queue<string> vacQueue = new Queue<string>();
    static bool vacWorker;

    static string SteamId(Game g)
    {
        if (g == null) return "";
        if (g.AppId != null) return g.AppId;
        g.AppId = "";
        try
        {
            if (g.Source != "Steam" || g.Dir.Length == 0) return "";
            string dir = g.Dir.TrimEnd('\\');
            string common = Path.GetDirectoryName(dir);
            if (common == null || !string.Equals(Path.GetFileName(common), "common", StringComparison.OrdinalIgnoreCase)) return "";
            string apps = Path.GetDirectoryName(common);
            if (apps == null || !Directory.Exists(apps)) return "";
            string install = Path.GetFileName(dir);
            foreach (string acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
            {
                string t = File.ReadAllText(acf);
                if (string.Equals(Games.Vdf(t, "installdir"), install, StringComparison.OrdinalIgnoreCase)) { g.AppId = Games.Vdf(t, "appid"); break; }
            }
        }
        catch { }
        return g.AppId;
    }

    // 1 - у магазині Steam гра позначена «Valve Anti-Cheat enabled»; 0 - ні; -1 - ще невідомо
    static int VacOf(string id)
    {
        if (id.Length == 0) return 0;
        lock (vac)
        {
            int v;
            if (vac.TryGetValue(id, out v)) return v;
            vac[id] = -1;
            vacQueue.Enqueue(id);
            if (!vacWorker)
            {
                vacWorker = true;
                Util.Bg(delegate { VacWorker(); });
            }
        }
        return -1;
    }

    static void VacWorker()
    {
        while (true)
        {
            string id;
            lock (vac) { if (vacQueue.Count == 0) { vacWorker = false; return; } id = vacQueue.Dequeue(); }
            int res = -1;
            try
            {
                bool stale;
                byte[] b = NetCache.Get("https://store.steampowered.com/api/appdetails?appids=" + id + "&filters=categories&l=english", TimeSpan.FromDays(30), out stale);
                if (b != null) res = ParseVac(Encoding.UTF8.GetString(b));
            }
            catch { }
            lock (vac) vac[id] = res;
            if (res >= 0) Version++;
        }
    }

    public static int ParseVac(string json)
    {
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(json, "\"categories\"\\s*:\\s*\\[(.*?)\\]", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!m.Success) return json.IndexOf("\"success\":false", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : -1;
        string c = m.Groups[1].Value;
        if (System.Text.RegularExpressions.Regex.IsMatch(c, "\"id\"\\s*:\\s*8\\b") || c.IndexOf("Anti-Cheat", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
        return 0;
    }

    // ---------- підсумок для налаштувань ----------
    // що знайдено для гри зі списку (порожньо - нічого не відомо)
    public static string ForGame(Game g)
    {
        if (g == null) return "";
        string w = InDir(g.Dir, 3);
        if (w.Length > 0) return w;
        if (VacOf(SteamId(g)) == 1) return "Valve Anti-Cheat (VAC)";
        return g.AcSeen ?? "";
    }

    // ---------- 3. під час запуску ----------
    public static string Check(int pid, string exe, Game g)
    {
        // користувач уже прочитав попередження й підтвердив
        if (g != null && g.AcOk) return "";
        string w = g != null ? ForGame(g) : "";
        if (w.Length > 0) return w;
        if (g == null && exe.Length > 0) w = InDir(Path.GetDirectoryName(exe), 2);
        if (w.Length > 0) return w;
        try
        {
            Process[] all = Process.GetProcesses();
            try
            {
                foreach (Process p in all)
                {
                    string n;
                    try { n = p.ProcessName.ToLowerInvariant(); } catch { continue; }
                    foreach (string a in AcProcesses) if (n == a) { w = L.T("працює служба античіта (") + n + ")"; break; }
                    if (w.Length > 0) break;
                }
            }
            finally { foreach (Process p in all) p.Dispose(); }       // і ті, до яких цикл не дійшов
        }
        catch { }
        if (w.Length == 0)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                    foreach (ProcessModule m in p.Modules)
                    {
                        string n = m.ModuleName.ToLowerInvariant();
                        foreach (string[] mk in Marks) if (n.Contains(mk[0])) { w = L.T("у грі завантажено ") + mk[1]; break; }
                        if (w.Length > 0) break;
                    }
            }
            catch { }
        }
        // запам'ятати: у налаштуваннях біля гри з'явиться позначка й попередження
        if (w.Length > 0 && g != null && g.AcSeen != w) { g.AcSeen = w; try { Games.Save(); } catch { } }
        return w;
    }
}
