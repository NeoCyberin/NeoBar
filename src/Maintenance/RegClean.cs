using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;

// ======================================================================
//  Очищення реєстру. Головне - безпека: краще лишити сміття, ніж зламати систему.
//   - Перевіряється кожен запис у відомих місцях; сміттям він вважається, лише коли ВСІ файли, на які він
//     посилається, точно відсутні (код помилки «файла чи шляху немає»; «немає доступу», недоступний диск,
//     знімний чи мережевий диск, папка Windows і пакети WindowsApps - це «невідомо», і запис не чіпається).
//   - Видаляються тільки записи в суворо визначених місцях і потрібної глибини, жодних «довільних» ключів.
//   - Перед будь-якою зміною все, що видаляється (ключ з усіма підключами, значення з типами), зберігається
//     у файл резервної копії; запис, який не вдалося безпечно зберегти, не видаляється.
//   - Безпосередньо перед видаленням запис перевіряється ще раз (за самим реєстром, а не за результатом
//     сканування); для HKLM помічник з правами адміністратора отримує файл завдання з контрольною сумою -
//     підміна файла між створенням і запуском не пройде.
//   - Усе зроблене можна повернути кнопкою «Відновити з копії».
// ======================================================================
static class ElevHash
{
    public static string Of(byte[] b)
    {
        using (SHA256 h = SHA256.Create())
        {
            StringBuilder sb = new StringBuilder();
            foreach (byte x in h.ComputeHash(b)) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }

    public static bool Same(byte[] b, string hex)
    {
        return hex != null && hex.Length == 64 && string.Equals(Of(b), hex, StringComparison.OrdinalIgnoreCase);
    }
}

sealed class RegItem
{
    public string Cat = "", Key = "", Kind = "key", Title = "", Detail = "";    // Kind: key - видалити ключ, val - значення, vals - усі значення ключа
    public int Hive;                    // 0 - HKCU, 1 - HKLM
    public bool V32, Skip;              // 32-бітний вигляд реєстру; користувач зняв позначку
    public List<string> Vals = new List<string>();
}

sealed class RegCat
{
    public string Id = "", Title = "", Desc = "", Caution = "", Note = "", Where = "";   // Where - де шукаємо й за яким критерієм (для підказки)
    public bool Default, On, Open;
    public List<RegItem> Items = new List<RegItem>();
}

static class RegClean
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint GetFileAttributesW(string path);

    public static readonly List<RegCat> Cats = new List<RegCat>();
    public static volatile bool Busy, Stop, Fixing;
    public static string Stage = "";
    public static DateTime LastAt = DateTime.MinValue;
    public static event Action Changed;

    static readonly bool Win = Environment.OSVersion.Platform == PlatformID.Win32NT;
    static readonly Dictionary<string, bool> drives = new Dictionary<string, bool>();

    const string RUninst = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall";
    const string RApp = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths";
    const string RShared = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\SharedDLLs";
    const string RRun = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
    const string RMui1 = "Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\Shell\\MuiCache";
    const string RMui2 = "Software\\Microsoft\\Windows\\ShellNoRoam\\MUICache";
    const string RClsid = "SOFTWARE\\Classes\\CLSID";
    const string RTlib = "SOFTWARE\\Classes\\TypeLib";
    const string RMru1 = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\RunMRU";
    const string RMru2 = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\TypedPaths";
    const string RWall = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Wallpapers";
    const string RCompatL = "Software\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Layers";
    const string RCompatS = "Software\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Compatibility Assistant\\Store";
    const string RCompatP = "Software\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Compatibility Assistant\\Persisted";
    const string RUA = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\UserAssist";
    const string RApps = "Software\\Classes\\Applications";
    const string RRegApps = "SOFTWARE\\RegisteredApplications";
    static readonly string[] AllRoots = { RUninst, RApp, RShared, RRun, RMui1, RMui2, RClsid, RTlib, RMru1, RMru2, RWall, RCompatL, RCompatS, RCompatP, RUA, RApps, RRegApps };
    // виробники, чиї розділи в Software ніколи не вважаються «залишками програми»
    static readonly string[] VendorSkip = { "Microsoft", "Classes", "Policies", "WOW6432Node", "Clients", "RegisteredApplications", "ODBC", "Windows", "NeoBar" };
    // значення, у яких програми зберігають папку встановлення
    static readonly string[] InstNames = { "InstallDir", "InstallPath", "Install_Dir", "InstallLocation", "InstallFolder", "Install Directory", "InstallationPath", "InstallationDirectory", "InstDir", "Inst_Dir", "Path", "Location", "AppPath", "AppDir", "ApplicationPath", "ExePath", "ProgramPath", "ProgramDir", "Directory", "Dir", "Home", "HomeDir", "BaseDir", "RootDir", "InstallRoot" };

    public static string BackupDir { get { return Path.Combine(Prof.Root, "registry-backups"); } }

    public static void Init()
    {
        if (Cats.Count > 0) return;
        Add("uninstall", L.T("Залишки видалених програм"), L.T("Записи в «Програми та засоби» для програм, файлів яких уже немає"), "", true,
            L.T("Де: розділ Uninstall у HKLM і HKCU. Критерій: немає ні папки програми, ні програми видалення, ні значка. Записи Windows Installer (MSI) і системні не чіпаються."));
        Add("leftover", L.T("Налаштування видалених програм"), L.T("Розділи Software\\виробник\\програма, де папки встановлення вже немає"), L.T("Обережніше: зникнуть і налаштування програм. Вимкнено за замовчуванням"), false,
            L.T("Де: HKCU\\Software і HKLM\\SOFTWARE (розділ виробника та програми). Критерій: у розділі є значення з папкою встановлення (InstallDir, Path тощо), її точно немає, і жоден інший шлях у розділі не існує. Розділи Microsoft, Classes і Policies не чіпаються. Якщо плануєте встановити програму знову й хочете зберегти її налаштування - не вмикайте."));
        Add("apppaths", L.T("Застарілі шляхи програм"), L.T("Записи «App Paths» на програми, яких уже немає на диску"), "", true,
            L.T("Де: App Paths у HKLM і HKCU. Критерій: файла, на який веде запис, точно немає."));
        Add("shareddlls", L.T("Спільні бібліотеки"), L.T("Лічильники спільних DLL, яких уже немає на диску"), "", true,
            L.T("Де: SharedDLLs у HKLM. Критерій: файла бібліотеки точно немає. Видаляється лише лічильник, не файл."));
        Add("muicache", L.T("Кеш назв програм"), L.T("Назви вже видалених програм, які Windows запам'ятала для своїх списків"), "", true,
            L.T("Де: MUICache у HKCU. Критерій: програми, чию назву запам'ятала Windows, точно немає."));
        Add("startup", L.T("Зламаний автозапуск"), L.T("Пункти автозапуску, чиї програми видалені: Windows шукає їх при кожному вході"), "", true,
            L.T("Де: Run у HKLM і HKCU. Критерій: програми автозапуску точно немає на диску."));
        Add("apps", L.T("Застарілі «Відкрити за допомогою»"), L.T("Програми зі списку «Відкрити за допомогою», яких уже немає на диску"), "", true,
            L.T("Де: Software\\Classes\\Applications у HKCU і HKLM. Критерій: усі програми в командах запису точно відсутні."));
        Add("regapps", L.T("Зайві «Програми за замовчуванням»"), L.T("Програми, що лишились у списку «Програми за замовчуванням», хоча їхніх параметрів у реєстрі вже немає"), "", true,
            L.T("Де: RegisteredApplications у HKLM і HKCU. Критерій: розділу з можливостями програми, на який веде запис, немає в реєстрі (в обох виглядах реєстру)."));
        Add("wallpaper", L.T("Сліди шпалер"), L.T("Історія робочого столу: шпалери, чиїх файлів уже немає (поточні шпалери не чіпаються)"), "", true,
            L.T("Де: Explorer\\Wallpapers у HKCU (BackgroundHistoryPath). Критерій: файла шпалер точно немає. Поточні шпалери не чіпаються."));
        Add("traces", L.T("Сліди запуску програм"), L.T("Режими сумісності й лічильник запусків програм, яких уже немає на диску"), "", true,
            L.T("Де: AppCompatFlags (режими сумісності та помічник сумісності) і UserAssist (лічильник запусків у меню «Пуск»). Критерій: програми, про яку запис, точно немає."));
        Add("clsid", L.T("Застарілі COM-компоненти"), L.T("Реєстрації компонентів, чиї файли точно відсутні (не в папці Windows)"), L.T("Обережніше: лише ті, чий файл точно відсутній. Вимкнено за замовчуванням"), false,
            L.T("Де: CLSID у HKLM і HKCU. Критерій: файли InprocServer32 і LocalServer32 точно відсутні; компоненти з TreatAs і Windows не чіпаються."));
        Add("typelib", L.T("Застарілі бібліотеки типів"), L.T("Описи інтерфейсів COM, чиї файли точно відсутні"), L.T("Обережніше: рідко дає користь. Вимкнено за замовчуванням"), false,
            L.T("Де: TypeLib у HKLM і HKCU. Критерій: файли бібліотек типів точно відсутні."));
        Add("mru", L.T("Історія «Виконати» й адресного рядка"), L.T("Не помилка, а слід користування: що вводили у Win+R та адресний рядок Провідника"), "", false,
            L.T("Де: RunMRU і TypedPaths у HKCU. Видаляються всі записи цієї історії."));
    }

    static void Add(string id, string title, string desc, string caution, bool def, string where)
    {
        RegCat c = new RegCat();
        c.Id = id; c.Title = title; c.Desc = desc; c.Caution = caution; c.Default = def; c.On = def; c.Where = where;
        Cats.Add(c);
    }

    // підказка до категорії: що це, де шукаємо, за яким критерієм
    public static string CatTip(RegCat c)
    {
        StringBuilder sb = new StringBuilder(c.Title);
        sb.Append("\n").Append(c.Desc);
        if (c.Where.Length > 0) sb.Append("\n\n").Append(c.Where);
        if (c.Caution.Length > 0) sb.Append("\n\n").Append(c.Caution);
        if (c.Note.Length > 0) sb.Append("\n\n").Append(c.Note);
        return sb.ToString();
    }

    static string HiveName(RegItem it) { return (it.Hive == 0 ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE") + (it.V32 ? L.T(" (32-бітний вигляд)") : ""); }

    // підказка до запису: що це і що саме буде видалено з реєстру
    public static string Describe(RegItem it)
    {
        StringBuilder sb = new StringBuilder(it.Title);
        if (it.Detail.Length > 0) sb.Append("\n").Append(it.Cat == "mru" ? it.Detail : (it.Cat == "traces" || it.Cat == "regapps" ? it.Detail : L.T("Чого немає на диску: ") + it.Detail));
        sb.Append(L.T("\n\nБуде видалено з реєстру:\n"));
        string where = HiveName(it) + "\\" + it.Key;
        if (it.Kind == "key") sb.Append(L.T("розділ ")).Append(where).Append(L.T(" разом з усіма підрозділами"));
        else if (it.Kind == "vals") sb.Append(L.T("усі значення розділу ")).Append(where);
        else
        {
            bool ua = it.Key.IndexOf("UserAssist", StringComparison.OrdinalIgnoreCase) >= 0;
            if (ua) sb.Append(L.T("запис лічильника запусків цієї програми в розділі ")).Append(where);
            else
            {
                sb.Append(it.Vals.Count == 1 ? L.T("значення ") : L.T("значення (") + it.Vals.Count + "): ");
                for (int i = 0; i < it.Vals.Count && i < 6; i++) { if (i > 0) sb.Append(", "); sb.Append(it.Vals[i].Length > 0 ? it.Vals[i] : L.T("(за замовчуванням)")); }
                if (it.Vals.Count > 6) sb.Append(L.T(" і ще ")).Append(it.Vals.Count - 6);
                sb.Append(L.T("\nу розділі ")).Append(where);
            }
        }
        sb.Append(L.T("\n\nПеред видаленням запис буде збережено в резервну копію."));
        return sb.ToString();
    }

    static void Fire() { Action a = Changed; if (a != null) try { a(); } catch { } }

    // ---------- кількість вибраного ----------
    public static int Selected()
    {
        int n = 0;
        foreach (RegCat c in Cats) if (c.On) foreach (RegItem it in c.Items) if (!it.Skip) n++;
        return n;
    }

    public static List<RegItem> SelectedItems()
    {
        List<RegItem> l = new List<RegItem>();
        foreach (RegCat c in Cats) if (c.On) foreach (RegItem it in c.Items) if (!it.Skip) l.Add(it);
        return l;
    }

    // ---------- перевірка шляхів: 0 є, 1 точно немає, 2 невідомо ----------
    static string Under0(string p) { return p == null ? "" : p.TrimEnd('\\', '/'); }

    static bool Under(string full, string dir)
    {
        dir = Under0(dir);
        if (dir.Length == 0) return false;
        return full.Equals(dir, StringComparison.OrdinalIgnoreCase) ||
               (full.Length > dir.Length && full.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && (full[dir.Length] == '\\' || full[dir.Length] == '/'));
    }

    static bool Prot(string full)
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (Under(full, win)) return true;
        foreach (string pf in new string[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetEnvironmentVariable("ProgramW6432") })
            if (!string.IsNullOrEmpty(pf) && Under(full, Path.Combine(pf, "WindowsApps"))) return true;
        return false;
    }

    static bool DriveOk(string root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        lock (drives) { bool v; if (drives.TryGetValue(root, out v)) return v; }
        bool ok = false;
        try { DriveInfo d = new DriveInfo(root); ok = d.DriveType == DriveType.Fixed && d.IsReady; } catch { ok = false; }
        lock (drives) drives[root] = ok;
        return ok;
    }

    static bool Qualified(string p)
    {
        if (Win) return p.Length >= 3 && p[1] == ':' && (p[2] == '\\' || p[2] == '/') && ((p[0] >= 'A' && p[0] <= 'Z') || (p[0] >= 'a' && p[0] <= 'z')) && p.IndexOf(':', 2) < 0;
        return p.Length > 1 && p[0] == '/';
    }

    public static int State(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return 2;
            path = path.Trim().Trim('"').Trim();
            if (path.IndexOf('%') >= 0) { path = Environment.ExpandEnvironmentVariables(path); if (path.IndexOf('%') >= 0) return 2; }
            if (path.Length < 2 || path.IndexOfAny(new char[] { '*', '?', '<', '>', '|', '"' }) >= 0) return 2;
            if (path.StartsWith("\\\\", StringComparison.Ordinal) || !Qualified(path)) return 2;
            string full = Path.GetFullPath(path);
            if (Prot(full)) return 2;
            if (!DriveOk(Path.GetPathRoot(full))) return 2;
            if (Win)
            {
                uint a = GetFileAttributesW(full);
                if (a != 0xFFFFFFFF) return 0;
                int e = Marshal.GetLastWin32Error();
                return e == 2 || e == 3 ? 1 : 2;
            }
            return File.Exists(full) || Directory.Exists(full) ? 0 : 1;
        }
        catch { return 2; }
    }

    static bool IsDigits(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s) if (c < '0' || c > '9') return false;
        return true;
    }

    // командний рядок: перший елемент - програма. У лапках - однозначно; без лапок кандидатів може бути кілька
    // (шлях із пробілами), тоді «є», якщо існує хоч один, і «немає», лише коли немає жодного.
    public static int CmdState(string cmd, out string shown)
    {
        shown = "";
        if (cmd == null) return 2;
        cmd = cmd.Trim();
        if (cmd.Length == 0) return 2;
        if (cmd[0] == '"')
        {
            int q = cmd.IndexOf('"', 1);
            if (q < 2) return 2;
            shown = cmd.Substring(1, q - 1);
            return State(shown);
        }
        List<string> c = new List<string>();
        foreach (string ext in new string[] { ".exe", ".bat", ".cmd", ".com" })
        {
            int i = cmd.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
            while (i >= 0)
            {
                int e = i + ext.Length;
                if (e == cmd.Length || cmd[e] == ' ') c.Add(cmd.Substring(0, e));
                i = cmd.IndexOf(ext, e, StringComparison.OrdinalIgnoreCase);
            }
        }
        if (c.Count == 0) return 2;
        int res = 1;
        foreach (string p in c)
        {
            int s = State(p);
            if (s == 0) { shown = p; return 0; }
            if (s == 2) res = 2;
            shown = p;
        }
        return res;
    }

    static string IconPath(string v)
    {
        v = v.Trim();
        int i = v.LastIndexOf(',');
        if (i > 0 && (IsDigits(v.Substring(i + 1).Trim()) || (v.Length > i + 2 && v[i + 1] == '-' && IsDigits(v.Substring(i + 2).Trim())))) v = v.Substring(0, i);
        return v.Trim().Trim('"');
    }

    // файл бібліотеки типів: «шлях\3» - це номер ресурсу всередині файла
    static int TlibState(string v)
    {
        string p = v.Trim().Trim('"');
        int i = p.LastIndexOf('\\');
        if (i > 3 && i < p.Length - 1 && IsDigits(p.Substring(i + 1)))
        {
            int s1 = State(p.Substring(0, i));
            if (s1 != 1) return s1;
        }
        return State(p);
    }

    // ---------- читання реєстру ----------
    static string Str(RegistryKey k, string name)
    {
        try
        {
            object v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            string s = v as string;
            return s == null ? "" : s.Trim();
        }
        catch { return ""; }
    }

    static int Dw(RegistryKey k, string name)
    {
        try { object v = k.GetValue(name); return v is int ? (int)v : -1; }
        catch { return -1; }
    }

    static string[] Names(RegistryKey k) { try { return k.GetSubKeyNames(); } catch { return new string[0]; } }
    static string[] ValNames(RegistryKey k) { try { return k.GetValueNames(); } catch { return new string[0]; } }
    static RegistryKey Sub(RegistryKey k, string name) { try { return k.OpenSubKey(name, false); } catch { return null; } }

    static RegistryKey OpenBase(int hive, bool v32)
    {
        try { return RegistryKey.OpenBaseKey(hive == 0 ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, v32 ? RegistryView.Registry32 : RegistryView.Registry64); }
        catch { return null; }
    }

    static RegistryKey Open(int hive, bool v32, string path, bool write)
    {
        RegistryKey b = OpenBase(hive, v32);
        if (b == null) return null;
        try
        {
            RegistryKey k = b.OpenSubKey(path, write);
            b.Close();
            return k;
        }
        catch { try { b.Close(); } catch { } return null; }
    }

    static bool[] Views(bool dual)
    {
        if (dual && Win && Environment.Is64BitOperatingSystem) return new bool[] { false, true };
        return new bool[] { false };
    }

    static bool Guid(string s)
    {
        if (s.Length != 38 || s[0] != '{' || s[37] != '}') return false;
        for (int i = 1; i < 37; i++)
        {
            char c = s[i];
            if (i == 9 || i == 14 || i == 19 || i == 24) { if (c != '-') return false; }
            else if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
        }
        return true;
    }

    static bool IsHex(string s)
    {
        if (s.Length == 0 || s.Length > 8) return false;
        foreach (char c in s) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
        return true;
    }

    // ---------- оцінка записів (одна й та сама під час сканування й перед видаленням) ----------
    static bool EvalUninstall(RegistryKey k, out string title, out string detail)
    {
        title = ""; detail = "";
        string dn = Str(k, "DisplayName");
        if (dn.Length == 0) return false;
        title = dn;
        if (Dw(k, "SystemComponent") == 1 || Dw(k, "WindowsInstaller") == 1) return false;
        if (Str(k, "ParentKeyName").Length > 0 || Str(k, "ParentDisplayName").Length > 0 || Str(k, "ReleaseType").Length > 0) return false;
        string us = Str(k, "UninstallString"), qs = Str(k, "QuietUninstallString");
        if (us.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0 || qs.IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        int n = 0;
        string loc = Str(k, "InstallLocation");
        if (loc.Length > 0) { if (State(loc) != 1) return false; n++; detail = loc; }
        foreach (string c in new string[] { us, qs })
        {
            if (c.Length == 0) continue;
            string sh;
            if (CmdState(c, out sh) != 1) return false;
            n++;
            if (detail.Length == 0) detail = sh;
        }
        string ic = Str(k, "DisplayIcon");
        if (ic.Length > 0) { if (State(IconPath(ic)) != 1) return false; n++; }
        return n > 0;
    }

    static bool EvalApp(RegistryKey k, out string detail)
    {
        detail = "";
        string v = Str(k, "");
        return v.Length > 0 && CmdState(v, out detail) == 1;
    }

    static bool EvalClsid(RegistryKey k, out string detail)
    {
        detail = "";
        foreach (string s in Names(k))
            if (s.Equals("TreatAs", StringComparison.OrdinalIgnoreCase) || s.Equals("AutoTreatAs", StringComparison.OrdinalIgnoreCase) || s.Equals("AutoConvertTo", StringComparison.OrdinalIgnoreCase)) return false;
        int n = 0;
        foreach (string sub in new string[] { "InprocServer32", "LocalServer32" })
        {
            using (RegistryKey s = Sub(k, sub))
            {
                if (s == null) continue;
                string v = Str(s, "");
                if (v.Length == 0) return false;
                string sh = v;
                int st = sub == "LocalServer32" ? CmdState(v, out sh) : State(v);
                if (st != 1) return false;
                n++;
                detail = sh;
            }
        }
        return n > 0;
    }

    static bool EvalTlib(RegistryKey ver, out string detail)
    {
        detail = "";
        int n = 0;
        foreach (string lc in Names(ver))
        {
            if (lc.Equals("FLAGS", StringComparison.OrdinalIgnoreCase) || lc.Equals("HELPDIR", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsHex(lc)) return false;
            using (RegistryKey l = Sub(ver, lc))
            {
                if (l == null) return false;
                bool any = false;
                foreach (string pl in new string[] { "win32", "win64" })
                    using (RegistryKey p = Sub(l, pl))
                    {
                        if (p == null) continue;
                        string v = Str(p, "");
                        if (v.Length == 0 || TlibState(v) != 1) return false;
                        any = true;
                        detail = v;
                    }
                if (any) n++;
            }
        }
        return n > 0;
    }

    static string MuiPath(string name)
    {
        foreach (string suf in new string[] { ".FriendlyAppName", ".ApplicationCompany" })
            if (name.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) return name.Substring(0, name.Length - suf.Length);
        return null;
    }

    static int MruCount(RegistryKey k)
    {
        int n = 0;
        foreach (string v in ValNames(k)) if (!v.Equals("MRUList", StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    static bool HasValue(RegistryKey k, string name)
    {
        foreach (string v in ValNames(k)) if (string.Equals(v, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ---------- нові категорії ----------
    static string Rot13(string s)
    {
        char[] c = s.ToCharArray();
        for (int i = 0; i < c.Length; i++)
        {
            char ch = c[i];
            if (ch >= 'a' && ch <= 'z') c[i] = (char)((ch - 'a' + 13) % 26 + 'a');
            else if (ch >= 'A' && ch <= 'Z') c[i] = (char)((ch - 'A' + 13) % 26 + 'A');
        }
        return new string(c);
    }

    // UserAssist зберігає шляхи в ROT13; розпізнаємо лише повні шляхи з буквою диска
    static string UaPath(string name)
    {
        string d = Rot13(name);
        if (d.StartsWith("UEME_RUNPATH:", StringComparison.Ordinal)) d = d.Substring(13);
        return Qualified(d) ? d : null;
    }

    static bool UaKey(string key)
    {
        if (!Child(key, RUA, 2)) return false;
        string[] seg = key.Substring(RUA.Length + 1).Split('\\');
        return Guid(seg[0]) && Eq(seg[1], "Count");
    }

    // шлях, який відповідає значенню запису категорії «Сліди запуску» (null - не шлях, не чіпаємо)
    static string TracePath(string key, string name)
    {
        if (key.StartsWith(RUA + "\\", StringComparison.OrdinalIgnoreCase)) return UaPath(name);
        return Qualified(name) ? name : null;
    }

    // 0 - розділ є, 1 - точно немає, 2 - невідомо (немає доступу тощо)
    static int KeyState(int hive, bool v32, string path)
    {
        RegistryKey b = OpenBase(hive, v32);
        if (b == null) return 2;
        try
        {
            using (b)
            using (RegistryKey k = b.OpenSubKey(path, false)) return k != null ? 0 : 1;
        }
        catch { return 2; }
    }

    // запис «Програми за замовчуванням» нікуди не веде: розділу можливостей немає (для HKLM - в жодному з видів реєстру)
    static bool RegAppGone(int hive, string v)
    {
        if (string.IsNullOrEmpty(v) || v.Length > 300 || v.IndexOf(':') >= 0 || v.IndexOf('/') >= 0) return false;
        if (!v.StartsWith("Software\\", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (bool v32 in Views(hive == 1)) if (KeyState(hive, v32, v) != 1) return false;
        return true;
    }

    static bool EvalApps(RegistryKey k, out string detail)
    {
        detail = "";
        using (RegistryKey sh = Sub(k, "shell"))
        {
            if (sh == null) return false;
            int n = 0;
            foreach (string verb in Names(sh))
                using (RegistryKey vk = Sub(sh, verb))
                {
                    if (vk == null) return false;
                    using (RegistryKey ck = Sub(vk, "command"))
                    {
                        if (ck == null) return false;
                        string c = Str(ck, ""), shown;
                        if (c.Length == 0 || CmdState(c, out shown) != 1) return false;
                        detail = shown;
                        n++;
                    }
                }
            return n > 0;
        }
    }

    static bool LooksCmd(string v)
    {
        foreach (string e in new string[] { ".exe", ".bat", ".cmd", ".com" }) if (v.IndexOf(e, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    // обережна оцінка значення-шляху: «немає» (1), лише коли немає за жодним із тлумачень (шлях, значок «шлях,N», команда з аргументами)
    static int LooseState(string v)
    {
        v = v.Trim();
        if (v.Length > 0 && v[0] == '"') { string sh; return CmdState(v, out sh); }
        int a = State(v);
        if (a == 0) return 0;
        int b = State(IconPath(v));
        if (b == 0) return 0;
        int res = a == 2 || b == 2 ? 2 : 1;
        if (LooksCmd(v))
        {
            string sh;
            int c = CmdState(v, out sh);
            if (c == 0) return 0;
            if (c != 1) res = 2;
        }
        return res;
    }

    static bool PathValue(string v)
    {
        if (v.Length < 3) return false;
        string ex = v.IndexOf('%') >= 0 ? Environment.ExpandEnvironmentVariables(v) : v;
        return Qualified(ex.Trim().TrimStart('"'));
    }

    static bool VendorSkipped(string name)
    {
        foreach (string s in VendorSkip) if (Eq(s, name)) return true;
        return false;
    }

    static bool IsInst(string name)
    {
        foreach (string s in InstNames) if (Eq(s, name)) return true;
        return false;
    }

    // усі шляхи в піддереві точно відсутні (будь-який існуючий чи невідомий шлях - зупиняємось)
    static bool TreeMissing(RegistryKey k, int depth, ref int budget)
    {
        if (--budget < 0 || depth > 6) return false;
        foreach (string n in ValNames(k))
        {
            string v = Str(k, n);
            if (v.Length == 0 || !PathValue(v)) continue;
            if (LooseState(v) != 1) return false;
        }
        foreach (string s in Names(k))
            using (RegistryKey c = Sub(k, s))
            {
                if (c == null) return false;
                if (!TreeMissing(c, depth + 1, ref budget)) return false;
            }
        return true;
    }

    // залишки програми: у розділі є папка встановлення, її точно немає, і жодного живого шляху в розділі більше немає
    static bool EvalLeftover(RegistryKey k, out string detail)
    {
        detail = "";
        int inst = 0;
        foreach (string n in ValNames(k))
        {
            if (!IsInst(n)) continue;
            string v = Str(k, n);
            if (v.Length == 0 || !PathValue(v)) continue;
            if (LooseState(v) != 1) return false;
            inst++;
            if (detail.Length == 0) detail = v;
        }
        if (inst == 0) return false;
        int budget = 200;
        return TreeMissing(k, 0, ref budget);
    }

    // чи запис і зараз - сміття (перевірка за самим реєстром)
    static bool Still(RegItem it)
    {
        try
        {
            RegistryKey k = Open(it.Hive, it.V32, it.Key, false);
            if (k == null) return false;
            using (k)
            {
                string t, d;
                switch (it.Cat)
                {
                    case "uninstall": return EvalUninstall(k, out t, out d);
                    case "apppaths": return EvalApp(k, out d);
                    case "clsid": return EvalClsid(k, out d);
                    case "typelib": return EvalTlib(k, out d);
                    case "mru": return MruCount(k) > 0;
                    case "apps": return EvalApps(k, out d);
                    case "leftover": return EvalLeftover(k, out d);
                    case "wallpaper":
                        foreach (string n in it.Vals)
                            if (!n.StartsWith("BackgroundHistoryPath", StringComparison.OrdinalIgnoreCase) || !HasValue(k, n) || State(Str(k, n)) != 1) return false;
                        return it.Vals.Count > 0;
                    case "traces":
                        foreach (string n in it.Vals)
                        {
                            string p = TracePath(it.Key, n);
                            if (p == null || !HasValue(k, n) || State(p) != 1) return false;
                        }
                        return it.Vals.Count > 0;
                    case "regapps":
                        foreach (string n in it.Vals) if (!HasValue(k, n) || !RegAppGone(it.Hive, Str(k, n))) return false;
                        return it.Vals.Count > 0;
                    case "startup":
                        foreach (string n in it.Vals)
                        {
                            if (!HasValue(k, n)) return false;
                            string sh;
                            if (CmdState(Str(k, n), out sh) != 1) return false;
                        }
                        return it.Vals.Count > 0;
                    case "shareddlls":
                        foreach (string n in it.Vals) if (!HasValue(k, n) || State(n) != 1) return false;
                        return it.Vals.Count > 0;
                    case "muicache":
                        foreach (string n in it.Vals)
                        {
                            string p = MuiPath(n);
                            if (p == null || !HasValue(k, n) || State(p) != 1) return false;
                        }
                        return it.Vals.Count > 0;
                }
            }
        }
        catch { }
        return false;
    }

    // ---------- допустимі місця ----------
    static bool Child(string key, string root, int depth)
    {
        if (!key.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        string[] seg = key.Substring(root.Length + 1).Split('\\');
        if (seg.Length != depth) return false;
        foreach (string s in seg) if (s.Length == 0) return false;
        return true;
    }

    static bool Eq(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

    static bool ValidKey(RegItem it)
    {
        if (it == null || (it.Hive != 0 && it.Hive != 1) || string.IsNullOrEmpty(it.Key)) return false;
        string k = it.Key;
        switch (it.Cat)
        {
            case "uninstall": return it.Kind == "key" && Child(k, RUninst, 1);
            case "apppaths": return it.Kind == "key" && Child(k, RApp, 1);
            case "clsid": return it.Kind == "key" && Child(k, RClsid, 1) && Guid(k.Substring(k.LastIndexOf('\\') + 1));
            case "typelib":
                if (it.Kind != "key" || !Child(k, RTlib, 2)) return false;
                return Guid(k.Substring(RTlib.Length + 1).Split('\\')[0]);
            case "shareddlls": return it.Kind == "val" && Eq(k, RShared);
            case "startup": return it.Kind == "val" && Eq(k, RRun);
            case "muicache": return it.Kind == "val" && it.Hive == 0 && (Eq(k, RMui1) || Eq(k, RMui2));
            case "mru": return it.Kind == "vals" && it.Hive == 0 && (Eq(k, RMru1) || Eq(k, RMru2));
            case "apps": return it.Kind == "key" && Child(k, RApps, 1) && k.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            case "regapps": return it.Kind == "val" && Eq(k, RRegApps);
            case "wallpaper": return it.Kind == "val" && it.Hive == 0 && Eq(k, RWall);
            case "traces":
                if (it.Kind != "val") return false;
                if (it.Hive == 0) return Eq(k, RCompatS) || Eq(k, RCompatP) || Eq(k, RCompatL) || UaKey(k);
                return Eq(k, RCompatL);
            case "leftover":
            {
                if (it.Kind != "key") return false;
                string root = it.Hive == 1 ? "SOFTWARE" : "Software";
                if (!Child(k, root, 1) && !Child(k, root, 2)) return false;
                return !VendorSkipped(k.Substring(root.Length + 1).Split('\\')[0]);
            }
        }
        return false;
    }

    // розділи, які можна відновлювати з копії програм-залишків: Software\виробник\..., крім виробників зі списку винятків
    static bool LeftoverRoot(int hive, string path)
    {
        string root = hive == 1 ? "SOFTWARE" : "Software";
        if (!path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        return !VendorSkipped(path.Substring(root.Length + 1).Split('\\')[0]);
    }

    static bool ValidRestore(int hive, string path)
    {
        if (hive != 0 && hive != 1) return false;
        foreach (string r in AllRoots)
            if (Eq(path, r) || path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        return LeftoverRoot(hive, path);
    }

    // ---------- сканування ----------
    static void Put(List<RegItem> l, string cat, int hive, bool v32, string key, string kind, string title, string detail, params string[] vals)
    {
        RegItem it = new RegItem();
        it.Cat = cat; it.Hive = hive; it.V32 = v32; it.Key = key; it.Kind = kind; it.Title = title; it.Detail = detail;
        foreach (string v in vals) it.Vals.Add(v);
        l.Add(it);
    }

    static void ScanUninstall(List<RegItem> l, ref int ex)
    {
        for (int hive = 1; hive >= 0; hive--)
            foreach (bool v32 in Views(hive == 1))
            {
                RegistryKey rk = Open(hive, v32, RUninst, false);
                if (rk == null) continue;
                using (rk)
                    foreach (string name in Names(rk))
                    {
                        if (Stop) return;
                        using (RegistryKey k = Sub(rk, name))
                        {
                            if (k == null) continue;
                            string t, d;
                            ex++;
                            if (EvalUninstall(k, out t, out d)) Put(l, "uninstall", hive, v32, RUninst + "\\" + name, "key", t, d);
                        }
                    }
            }
    }

    static void ScanApp(List<RegItem> l, ref int ex)
    {
        for (int hive = 1; hive >= 0; hive--)
            foreach (bool v32 in Views(hive == 1))
            {
                RegistryKey rk = Open(hive, v32, RApp, false);
                if (rk == null) continue;
                using (rk)
                    foreach (string name in Names(rk))
                    {
                        if (Stop) return;
                        using (RegistryKey k = Sub(rk, name))
                        {
                            if (k == null) continue;
                            string d;
                            ex++;
                            if (EvalApp(k, out d)) Put(l, "apppaths", hive, v32, RApp + "\\" + name, "key", name, d);
                        }
                    }
            }
    }

    static void ScanShared(List<RegItem> l, ref int ex)
    {
        foreach (bool v32 in Views(true))
        {
            RegistryKey rk = Open(1, v32, RShared, false);
            if (rk == null) continue;
            using (rk)
                foreach (string n in ValNames(rk))
                {
                    if (Stop) return;
                    if (n.Length == 0) continue;
                    ex++;
                    if (State(n) == 1) Put(l, "shareddlls", 1, v32, RShared, "val", n, "", n);
                }
        }
    }

    static void ScanMui(List<RegItem> l, ref int ex)
    {
        foreach (string root in new string[] { RMui1, RMui2 })
        {
            RegistryKey rk = Open(0, false, root, false);
            if (rk == null) continue;
            using (rk)
            {
                Dictionary<string, List<string>> by = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (string n in ValNames(rk))
                {
                    string p = MuiPath(n);
                    if (p == null) continue;
                    List<string> li;
                    if (!by.TryGetValue(p, out li)) { li = new List<string>(); by[p] = li; }
                    li.Add(n);
                }
                foreach (KeyValuePair<string, List<string>> kv in by)
                {
                    if (Stop) return;
                    ex++;
                    if (State(kv.Key) == 1) Put(l, "muicache", 0, false, root, "val", kv.Key, "", kv.Value.ToArray());
                }
            }
        }
    }

    static void ScanRun(List<RegItem> l, ref int ex)
    {
        for (int hive = 1; hive >= 0; hive--)
            foreach (bool v32 in Views(hive == 1))
            {
                RegistryKey rk = Open(hive, v32, RRun, false);
                if (rk == null) continue;
                using (rk)
                    foreach (string n in ValNames(rk))
                    {
                        if (Stop) return;
                        string v = Str(rk, n), sh;
                        if (v.Length == 0) continue;
                        ex++;
                        if (CmdState(v, out sh) == 1) Put(l, "startup", hive, v32, RRun, "val", n.Length > 0 ? n : L.T("(без назви)"), sh, n);
                    }
            }
    }

    static void ScanClsid(List<RegItem> l, ref int ex)
    {
        for (int hive = 1; hive >= 0; hive--)
            foreach (bool v32 in Views(true))
            {
                string root = hive == 1 ? RClsid : "Software\\Classes\\CLSID";
                RegistryKey rk = Open(hive, v32, root, false);
                if (rk == null) continue;
                using (rk)
                    foreach (string name in Names(rk))
                    {
                        if (Stop) return;
                        if (!Guid(name)) continue;
                        using (RegistryKey k = Sub(rk, name))
                        {
                            if (k == null) continue;
                            string d;
                            ex++;
                            if (EvalClsid(k, out d))
                            {
                                string nm = Str(k, "");
                                Put(l, "clsid", hive, v32, RClsid + "\\" + name, "key", nm.Length > 0 ? nm : name, d);
                            }
                        }
                    }
            }
    }

    static void ScanTlib(List<RegItem> l, ref int ex)
    {
        for (int hive = 1; hive >= 0; hive--)
        {
            RegistryKey rk = Open(hive, false, RTlib, false);
            if (rk == null) continue;
            using (rk)
                foreach (string g in Names(rk))
                {
                    if (Stop) return;
                    if (!Guid(g)) continue;
                    using (RegistryKey gk = Sub(rk, g))
                    {
                        if (gk == null) continue;
                        foreach (string ver in Names(gk))
                        {
                            using (RegistryKey vk = Sub(gk, ver))
                            {
                                if (vk == null) continue;
                                string d;
                                ex++;
                                if (EvalTlib(vk, out d))
                                {
                                    string nm = Str(vk, "");
                                    Put(l, "typelib", hive, false, RTlib + "\\" + g + "\\" + ver, "key", nm.Length > 0 ? nm : g + " " + ver, d);
                                }
                            }
                        }
                    }
                }
        }
    }

    static void ScanMru(List<RegItem> l, ref int ex)
    {
        string[] titles = { L.T("Історія вікна «Виконати» (Win+R)"), L.T("Адреси, введені в адресний рядок Провідника") };
        string[] roots = { RMru1, RMru2 };
        for (int i = 0; i < 2; i++)
        {
            RegistryKey k = Open(0, false, roots[i], false);
            if (k == null) continue;
            using (k)
            {
                int n = MruCount(k);
                ex++;
                if (n > 0) Put(l, "mru", 0, false, roots[i], "vals", titles[i], L.T("записів: ") + n);
            }
        }
    }

    static void ScanLeftover(List<RegItem> l, ref int ex)
    {
        for (int hive = 1; hive >= 0; hive--)
            foreach (bool v32 in Views(hive == 1))
            {
                string root = hive == 1 ? "SOFTWARE" : "Software";
                RegistryKey rk = Open(hive, v32, root, false);
                if (rk == null) continue;
                using (rk)
                    foreach (string vendor in Names(rk))
                    {
                        if (Stop) return;
                        if (VendorSkipped(vendor)) continue;
                        using (RegistryKey vk = Sub(rk, vendor))
                        {
                            if (vk == null) continue;
                            string d;
                            ex++;
                            if (EvalLeftover(vk, out d)) { Put(l, "leftover", hive, v32, root + "\\" + vendor, "key", vendor, d); continue; }
                            foreach (string app in Names(vk))
                                using (RegistryKey ak = Sub(vk, app))
                                {
                                    if (ak == null) continue;
                                    ex++;
                                    if (EvalLeftover(ak, out d)) Put(l, "leftover", hive, v32, root + "\\" + vendor + "\\" + app, "key", vendor + " › " + app, d);
                                }
                        }
                    }
            }
    }

    static void ScanApps(List<RegItem> l, ref int ex)
    {
        for (int hive = 1; hive >= 0; hive--)
        {
            RegistryKey rk = Open(hive, false, RApps, false);
            if (rk == null) continue;
            using (rk)
                foreach (string name in Names(rk))
                {
                    if (Stop) return;
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    using (RegistryKey k = Sub(rk, name))
                    {
                        if (k == null) continue;
                        string d;
                        ex++;
                        if (EvalApps(k, out d)) Put(l, "apps", hive, false, RApps + "\\" + name, "key", name, d);
                    }
                }
        }
    }

    static void ScanRegApps(List<RegItem> l, ref int ex)
    {
        for (int hive = 1; hive >= 0; hive--)
            foreach (bool v32 in Views(hive == 1))
            {
                RegistryKey rk = Open(hive, v32, RRegApps, false);
                if (rk == null) continue;
                using (rk)
                    foreach (string n in ValNames(rk))
                    {
                        if (Stop) return;
                        string v = Str(rk, n);
                        if (n.Length == 0 || v.Length == 0) continue;
                        ex++;
                        if (RegAppGone(hive, v)) Put(l, "regapps", hive, v32, RRegApps, "val", n, L.T("немає розділу ") + v, n);
                    }
            }
    }

    static void ScanWallpaper(List<RegItem> l, ref int ex)
    {
        RegistryKey k = Open(0, false, RWall, false);
        if (k == null) return;
        using (k)
            foreach (string n in ValNames(k))
            {
                if (!n.StartsWith("BackgroundHistoryPath", StringComparison.OrdinalIgnoreCase)) continue;
                string v = Str(k, n);
                if (v.Length == 0) continue;
                ex++;
                if (State(v) == 1) Put(l, "wallpaper", 0, false, RWall, "val", v, L.T("історія шпалер"), n);
            }
    }

    static void ScanTraces(List<RegItem> l, ref int ex)
    {
        string[] keys = { RCompatS, RCompatP, RCompatL, RCompatL };
        string[] labels = { L.T("помічник сумісності"), L.T("помічник сумісності"), L.T("режим сумісності"), L.T("режим сумісності (для всіх користувачів)") };
        for (int i = 0; i < keys.Length; i++)
        {
            int hive = i == 3 ? 1 : 0;
            foreach (bool v32 in Views(hive == 1))
            {
                RegistryKey rk = Open(hive, v32, keys[i], false);
                if (rk == null) continue;
                using (rk)
                    foreach (string n in ValNames(rk))
                    {
                        if (Stop) return;
                        if (!Qualified(n)) continue;
                        ex++;
                        if (State(n) == 1) Put(l, "traces", hive, v32, keys[i], "val", n, labels[i], n);
                    }
            }
        }
        RegistryKey ua = Open(0, false, RUA, false);
        if (ua == null) return;
        using (ua)
            foreach (string g in Names(ua))
            {
                if (Stop) return;
                if (!Guid(g)) continue;
                string path = RUA + "\\" + g + "\\Count";
                using (RegistryKey ck = Open(0, false, path, false))
                {
                    if (ck == null) continue;
                    foreach (string n in ValNames(ck))
                    {
                        string p = UaPath(n);
                        if (p == null) continue;
                        ex++;
                        if (State(p) == 1) Put(l, "traces", 0, false, path, "val", p, L.T("лічильник запусків"), n);
                    }
                }
            }
    }

    // запобіжник: якщо «сміттям» визнано підозріло багато записів - це збій перевірки, а не сміття
    static bool Plausible(int found, int examined) { return !(found > 200 && found * 2 > examined); }

    public static void Scan()
    {
        if (Busy || Fixing) return;
        Init();
        Busy = true; Stop = false;
        Thread th = new Thread(delegate ()
        {
            try
            {
                lock (drives) drives.Clear();
                foreach (RegCat c in Cats)
                {
                    if (Stop) break;
                    Stage = L.T("Перевірка: ") + c.Title;
                    Fire();
                    List<RegItem> l = new List<RegItem>();
                    int ex = 0;
                    try
                    {
                        switch (c.Id)
                        {
                            case "uninstall": ScanUninstall(l, ref ex); break;
                            case "apppaths": ScanApp(l, ref ex); break;
                            case "shareddlls": ScanShared(l, ref ex); break;
                            case "muicache": ScanMui(l, ref ex); break;
                            case "startup": ScanRun(l, ref ex); break;
                            case "clsid": ScanClsid(l, ref ex); break;
                            case "typelib": ScanTlib(l, ref ex); break;
                            case "mru": ScanMru(l, ref ex); break;
                            case "leftover": ScanLeftover(l, ref ex); break;
                            case "apps": ScanApps(l, ref ex); break;
                            case "regapps": ScanRegApps(l, ref ex); break;
                            case "wallpaper": ScanWallpaper(l, ref ex); break;
                            case "traces": ScanTraces(l, ref ex); break;
                        }
                    }
                    catch (Exception e) { Util.ErrorLog(e); }
                    c.Note = "";
                    if (!Plausible(l.Count, ex)) { c.Note = L.T("Пропущено: знайдено підозріло багато (") + l.Count + L.T(" із ") + ex + L.T(") - схоже на збій перевірки, нічого не чіпаємо"); l = new List<RegItem>(); }
                    c.Items = l;
                    Fire();
                }
            }
            catch (Exception e) { Util.ErrorLog(e); }
            finally { LastAt = DateTime.Now; Busy = false; Stage = ""; Fire(); }
        });
        th.IsBackground = true;
        th.Priority = ThreadPriority.BelowNormal;
        th.Start();
    }

    // ---------- резервна копія ----------
    static string B(string s) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? "")); }
    static string U(string s) { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }

    static string KeyLine(int hive, bool v32, string path) { return "KEY|" + hive + "|" + (v32 ? 1 : 0) + "|" + B(path) + "\r\n"; }

    static bool DumpVal(RegistryKey k, string name, StringBuilder sb)
    {
        RegistryValueKind kd;
        try { kd = k.GetValueKind(name); } catch { return false; }
        object v;
        try { v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames); } catch { return false; }
        if (v == null) return false;
        string data;
        switch (kd)
        {
            case RegistryValueKind.String:
            case RegistryValueKind.ExpandString: if (!(v is string)) return false; data = B((string)v); break;
            case RegistryValueKind.MultiString: if (!(v is string[])) return false; data = B(string.Join("\0", (string[])v)); break;
            case RegistryValueKind.Binary: if (!(v is byte[])) return false; data = Convert.ToBase64String((byte[])v); break;
            case RegistryValueKind.DWord: data = Convert.ToInt32(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture); break;
            case RegistryValueKind.QWord: data = Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture); break;
            default: return false;          // невідомий тип - не вдасться відновити точно, тож і не видаляємо
        }
        sb.Append("VAL|").Append(B(name)).Append('|').Append(kd.ToString()).Append('|').Append(data).Append("\r\n");
        return true;
    }

    static bool DumpTree(RegistryKey k, int hive, bool v32, string path, StringBuilder sb, int depth, ref int count)
    {
        sb.Append(KeyLine(hive, v32, path));
        foreach (string n in ValNames(k)) if (!DumpVal(k, n, sb)) return false;
        foreach (string s in Names(k))
        {
            if (depth > 10 || ++count > 600) return false;
            using (RegistryKey c = Sub(k, s))
            {
                if (c == null) return false;
                if (!DumpTree(c, hive, v32, path + "\\" + s, sb, depth + 1, ref count)) return false;
            }
        }
        return true;
    }

    static string Dump(RegItem it)
    {
        RegistryKey k = Open(it.Hive, it.V32, it.Key, false);
        if (k == null) return null;
        StringBuilder sb = new StringBuilder();
        using (k)
        {
            if (it.Kind == "key")
            {
                int cnt = 0;
                if (!DumpTree(k, it.Hive, it.V32, it.Key, sb, 0, ref cnt)) return null;
            }
            else
            {
                sb.Append(KeyLine(it.Hive, it.V32, it.Key));
                string[] names = it.Kind == "vals" ? ValNames(k) : it.Vals.ToArray();
                foreach (string n in names) if (!DumpVal(k, n, sb)) return null;
            }
        }
        return sb.ToString();
    }

    // ---------- виконання (процес користувача або помічник з підвищенням) ----------
    static string Plan(RegItem it)
    {
        StringBuilder v = new StringBuilder();
        foreach (string n in it.Vals) { if (v.Length > 0) v.Append(','); v.Append(B(n)); }
        return "D|" + it.Cat + "|" + it.Hive + "|" + (it.V32 ? 1 : 0) + "|" + B(it.Key) + "|" + it.Kind + "|" + v;
    }

    static RegItem ParsePlan(string line)
    {
        string[] p = line.Split('|');
        if (p.Length < 7 || p[0] != "D") return null;
        RegItem it = new RegItem();
        it.Cat = p[1]; it.Kind = p[5];
        int h;
        if (!int.TryParse(p[2], out h)) return null;
        it.Hive = h; it.V32 = p[3] == "1";
        it.Key = U(p[4]);
        if (p[6].Length > 0) foreach (string n in p[6].Split(',')) it.Vals.Add(U(n));
        return it;
    }

    static void ExecDel(IEnumerable<string> lines, TextWriter w)
    {
        lock (drives) drives.Clear();
        foreach (string line in lines)
        {
            RegItem it = null;
            try { it = ParsePlan(line.Trim()); } catch { }
            if (it == null) continue;
            try
            {
                if (!ValidKey(it)) { w.WriteLine("SKIP|" + it.Key + L.T("|недопустимий запис")); continue; }
                if (!Still(it)) { w.WriteLine("SKIP|" + it.Key + L.T("|уже змінився")); continue; }
                RegistryKey b = OpenBase(it.Hive, it.V32);
                if (b == null) { w.WriteLine("SKIP|" + it.Key + L.T("|немає доступу до реєстру")); continue; }
                using (b)
                {
                    if (it.Kind == "key")
                    {
                        b.DeleteSubKeyTree(it.Key, false);
                        if (it.Cat == "typelib")                // порожній розділ GUID без версій теж прибираємо
                        {
                            string par = it.Key.Substring(0, it.Key.LastIndexOf('\\'));
                            using (RegistryKey pk = b.OpenSubKey(par, false))
                            {
                                bool empty = pk != null && pk.SubKeyCount == 0 && pk.ValueCount == 0;
                                if (empty && Child(par, RTlib, 1)) { pk.Close(); b.DeleteSubKey(par, false); }
                            }
                        }
                    }
                    else
                    {
                        using (RegistryKey k = b.OpenSubKey(it.Key, true))
                        {
                            if (k == null) { w.WriteLine("SKIP|" + it.Key + L.T("|немає доступу")); continue; }
                            string[] names = it.Kind == "vals" ? ValNames(k) : it.Vals.ToArray();
                            foreach (string n in names) k.DeleteValue(n, false);
                        }
                    }
                }
                w.WriteLine("OK|" + it.Key);
            }
            catch (Exception ex) { w.WriteLine("SKIP|" + it.Key + "|" + ex.Message.Replace('\r', ' ').Replace('\n', ' ')); }
        }
    }

    static object ParseVal(string kind, string data)
    {
        switch (kind)
        {
            case "String": case "ExpandString": return U(data);
            case "MultiString": { string s = U(data); return s.Length == 0 ? new string[0] : s.Split('\0'); }
            case "Binary": return Convert.FromBase64String(data);
            case "DWord": return int.Parse(data, CultureInfo.InvariantCulture);
            case "QWord": return long.Parse(data, CultureInfo.InvariantCulture);
        }
        return null;
    }

    static void ExecRestore(IEnumerable<string> lines, TextWriter w)
    {
        RegistryKey cur = null;
        string curPath = "";
        try
        {
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                try
                {
                    if (line.StartsWith("KEY|", StringComparison.Ordinal))
                    {
                        if (cur != null) { cur.Close(); cur = null; }
                        string[] p = line.Split('|');
                        int h = int.Parse(p[1]);
                        string path = U(p[3]);
                        curPath = path;
                        if (!ValidRestore(h, path)) { w.WriteLine("SKIP|" + path + L.T("|недопустимий шлях")); continue; }
                        RegistryKey b = OpenBase(h, p[2] == "1");
                        if (b == null) { w.WriteLine("SKIP|" + path + L.T("|немає доступу до реєстру")); continue; }
                        using (b) cur = b.CreateSubKey(path);
                        w.WriteLine(cur != null ? "OK|" + path : "SKIP|" + path + L.T("|не вдалося створити ключ"));
                    }
                    else if (line.StartsWith("VAL|", StringComparison.Ordinal) && cur != null)
                    {
                        string[] p = line.Split('|');
                        object v = ParseVal(p[2], p[3]);
                        if (v == null) { w.WriteLine("SKIP|" + curPath + L.T("|невідомий тип значення")); continue; }
                        cur.SetValue(U(p[1]), v, (RegistryValueKind)Enum.Parse(typeof(RegistryValueKind), p[2]));
                    }
                }
                catch (Exception ex) { w.WriteLine("SKIP|" + curPath + "|" + ex.Message.Replace('\r', ' ').Replace('\n', ' ')); }
            }
        }
        finally { if (cur != null) cur.Close(); }
    }

    // викликається помічником з підвищенням: режим del/res, файл завдання, контрольна сума файла
    public static int RunFile(string mode, string file, string hash, TextWriter w)
    {
        byte[] raw = File.ReadAllBytes(file);
        try { File.Delete(file); } catch { }
        if (!ElevHash.Same(raw, hash)) { w.WriteLine(L.T("Файл завдання змінено після створення - нічого не виконано.")); return 1; }
        string[] lines = Encoding.UTF8.GetString(raw).Split('\n');
        if (mode == "del") ExecDel(lines, w);
        else if (mode == "res") ExecRestore(lines, w);
        else { w.WriteLine(L.T("Невідомий режим.")); return 1; }
        return 0;
    }

    // HKCU - у цьому процесі; HKLM - у цьому процесі, якщо вже є права, інакше через помічника (запит UAC).
    // Повертає вивід (рядки OK|... / SKIP|...) або null з поясненням у err.
    static string RunPlan(string mode, List<string> lines, bool lm, out string err)
    {
        err = "";
        StringWriter sw = new StringWriter();
        if (!lm || !Win || Elevated.IsAdmin)
        {
            if (mode == "del") ExecDel(lines, sw); else ExecRestore(lines, sw);
            return sw.ToString();
        }
        string planFile = Path.Combine(Path.GetTempPath(), "neobar-regclean-" + DateTime.Now.Ticks + ".txt");
        byte[] raw = new UTF8Encoding(false).GetBytes(string.Join("\r\n", lines.ToArray()) + "\r\n");
        File.WriteAllBytes(planFile, raw);
        string outFile = Elevated.Run("regclean", mode + " \"" + planFile + "\" " + ElevHash.Of(raw), out err);
        if (outFile == null) { try { File.Delete(planFile); } catch { } return null; }
        DateTime until = DateTime.Now.AddMinutes(10);
        while (DateTime.Now < until)
        {
            Thread.Sleep(300);
            bool done;
            string t = Elevated.ReadOut(outFile, out done);
            if (done) { try { File.Delete(outFile); } catch { } return t; }
        }
        err = L.T("Помічник не відповів.");
        return null;
    }

    static int CountOk(string t)
    {
        int n = 0;
        if (t != null) foreach (string l in t.Split('\n')) if (l.StartsWith("OK|", StringComparison.Ordinal)) n++;
        return n;
    }

    // Виправлення вибраного. Копія записується на диск ДО будь-яких змін. Викликати не з потоку інтерфейсу (UAC, час).
    // Повертає "" або пояснення; removed - скільки записів прибрано; skipped - скільки пропущено.
    public static string Fix(List<RegItem> items, out int removed, out int skipped, out string file)
    {
        removed = 0; skipped = 0; file = "";
        lock (drives) drives.Clear();
        StringBuilder bak = new StringBuilder("SBREG1\r\n");
        List<string> cu = new List<string>(), lm = new List<string>();
        foreach (RegItem it in items)
        {
            if (!ValidKey(it) || !Still(it)) { skipped++; continue; }
            string d = null;
            try { d = Dump(it); } catch { d = null; }
            if (d == null) { skipped++; continue; }
            bak.Append(d);
            (it.Hive == 0 ? cu : lm).Add(Plan(it));
        }
        if (cu.Count + lm.Count == 0) return L.T("Нічого виправляти: записи вже змінилися або їх не вдалося безпечно зберегти.");
        try
        {
            Directory.CreateDirectory(BackupDir);
            file = Path.Combine(BackupDir, "reg-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".nbreg");
            File.WriteAllText(file, bak.ToString(), new UTF8Encoding(false));
            if (new FileInfo(file).Length < 8) throw new IOException(L.T("порожній файл копії"));
        }
        catch (Exception ex) { file = ""; return L.T("Не вдалося зберегти резервну копію, тож нічого не змінено: ") + ex.Message; }
        string err = "";
        if (cu.Count > 0)
        {
            string t = RunPlan("del", cu, false, out err);
            int ok = CountOk(t);
            removed += ok; skipped += cu.Count - ok;
        }
        if (lm.Count > 0)
        {
            string t = RunPlan("del", lm, true, out err);
            int ok = CountOk(t);
            removed += ok; skipped += lm.Count - ok;
            if (t == null) return err;
        }
        Prune();
        return "";
    }

    static void Prune()
    {
        try
        {
            string[] f = Directory.GetFiles(BackupDir, "*.nbreg");
            Array.Sort(f, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < f.Length - 30; i++) try { File.Delete(f[i]); } catch { }
        }
        catch { }
    }

    public static string LastBackup()
    {
        try
        {
            if (!Directory.Exists(BackupDir)) return null;
            string[] f = Directory.GetFiles(BackupDir, "*.nbreg");
            if (f.Length == 0) return null;
            Array.Sort(f, StringComparer.OrdinalIgnoreCase);
            return f[f.Length - 1];
        }
        catch { return null; }
    }

    // скільки записів у копії (рахуються рядки KEY верхнього рівня не потрібні - просто ключі)
    public static int BackupSize(string file)
    {
        int n = 0;
        try { foreach (string l in File.ReadAllLines(file, Encoding.UTF8)) if (l.StartsWith("KEY|", StringComparison.Ordinal)) n++; }
        catch { }
        return n;
    }

    // Відновлення з копії; після успіху копія позначається як використана (розширення .done)
    public static string Restore(string file, out int n)
    {
        n = 0;
        string[] lines;
        try { lines = File.ReadAllLines(file, Encoding.UTF8); }
        catch (Exception ex) { return L.T("Не вдалося прочитати копію: ") + ex.Message; }
        if (lines.Length == 0 || lines[0].Trim() != "SBREG1") return L.T("Невідомий формат копії.");
        List<string> cu = new List<string>(), lm = new List<string>();
        List<string> cur = null;
        for (int i = 1; i < lines.Length; i++)
        {
            string l = lines[i];
            if (l.StartsWith("KEY|", StringComparison.Ordinal))
            {
                string[] p = l.Split('|');
                cur = p.Length > 3 && p[1] == "1" ? lm : (p.Length > 3 && p[1] == "0" ? cu : null);
            }
            if (cur != null) cur.Add(l);
        }
        string err = "";
        if (cu.Count > 0) n += CountOk(RunPlan("res", cu, false, out err));
        if (lm.Count > 0)
        {
            string t = RunPlan("res", lm, true, out err);
            if (t == null) return err;
            n += CountOk(t);
        }
        try { File.Move(file, file + ".done"); } catch { }
        return "";
    }
}
