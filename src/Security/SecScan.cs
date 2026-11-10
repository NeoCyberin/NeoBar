using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

// одна знахідка: процес, запис автозапуску, служба або перевірка налаштувань
sealed class Finding
{
    public string Area = "";          // proc | auto | svc | sys
    public string Title = "", Path = "", Detail = "", Signer = "", Cmd = "";
    public int SigKind = Sig.Unknown, Score, Place = SecHeur.POther;
    public readonly List<string> Why = new List<string>();
    public readonly List<int> Pids = new List<int>();
    // автозапуск: як вимкнути й увімкнути (той самий механізм, що й у Диспетчері завдань)
    public string AutoKind = "";      // run | folder | task (інше - лише показ)
    public bool Hklm;
    public string ApprovedSub = "", ApprovedName = "", TaskPath = "";
    public bool Off;                  // вимкнено
    public bool Ok;                   // перевірка налаштувань пройдена
    public bool Allowed;              // у білому списку - більше не турбувати
    public bool Open, Busy;           // картка розгорнута «Детальніше»; триває аналіз файла
    public FileReport Rep;            // докладний аналіз файла (робиться за потреби)
    public string SvcName = "";       // служба: назва ключа
    public int SvcStart = 3;
    public readonly List<Fix> Fixes = new List<Fix>();              // що можна виправити (з відкатом)
    public readonly List<string[]> Links = new List<string[]>();    // (підпис кнопки, що відкрити) - налаштування Windows
    public int Level { get { return Score >= 5 ? 2 : (Score >= 3 ? 1 : 0); } }

    public void Add(int s, string why)
    {
        Score += s;
        if (why != null && !Why.Contains(why)) Why.Add(why);
    }
}

// ======================================================================
//  Сканер: процеси, автозапуск, служби, налаштування Windows. Лише на вимогу (кнопка «Сканувати»),
//  в окремому потоці зі зниженим пріоритетом. Нічого не змінює сам - зміни робить вікно після підтвердження.
// ======================================================================
static class SecScan
{
    public static volatile bool Busy, Stop;
    public static volatile string Stage = "";
    public static List<Finding> Proc = new List<Finding>(), Auto = new List<Finding>(), Svc = new List<Finding>(), Sys = new List<Finding>();
    public static int NoAccess, ProcCount;
    public static bool ProcSkipped;
    public static DateTime LastAt = DateTime.MinValue;
    public static event Action Changed;

    static void Fire() { Action a = Changed; if (a != null) try { a(); } catch (Exception skipEx) { SbLog.Skip(skipEx); } }

    // підсумок: скільки «підозріло» і «варто перевірити»
    public static void Count(out int bad, out int warn)
    {
        bad = 0; warn = 0;
        foreach (List<Finding> l in new List<Finding>[] { Proc, Auto, Svc, Sys })
            foreach (Finding f in l)
            {
                if (f.Off || f.Ok || f.Allowed) continue;
                if (f.Level == 2) bad++; else if (f.Level == 1) warn++;
            }
    }

    public static void Run(bool procs)
    {
        if (Busy) return;
        Busy = true; Stop = false; Stage = L.T("Підготовка…");
        Fire();
        Thread t = new Thread(delegate ()
        {
            List<Finding> p = new List<Finding>(), a = new List<Finding>(), s = new List<Finding>(), y = new List<Finding>();
            int na = 0, pc = 0;
            List<string> listen = new List<string>();
            try
            {
                if (procs) { Stage = L.T("Процеси…"); Fire(); p = Processes(out na, out pc, listen); }
                if (!Stop) { Stage = L.T("Автозапуск…"); Fire(); a = Autostart(); }
                if (!Stop) { Stage = L.T("Служби й драйвери…"); Fire(); s = Services(); }
                if (!Stop) { Stage = L.T("Налаштування Windows…"); Fire(); y = Settings(listen, procs); }
            }
            catch (Exception ex) { Util.ErrorLog(ex); }
            finally
            {
                try { SecLists.Mark(p); SecLists.Mark(a); SecLists.Mark(s); SecLists.Mark(y); } catch (Exception ex) { Util.ErrorLog(ex); }
                Sort(p); Sort(a); Sort(s);
                if (!Stop || p.Count > 0) { Proc = p; NoAccess = na; ProcCount = pc; ProcSkipped = !procs; }
                if (!Stop) { Auto = a; Svc = s; Sys = y; LastAt = DateTime.Now; }
                Busy = false; Stage = "";
                Fire();
            }
        });
        t.IsBackground = true;
        t.Priority = ThreadPriority.BelowNormal;
        t.Start();
    }

    static void Sort(List<Finding> l)
    {
        l.Sort(delegate (Finding x, Finding y)
        {
            int c = y.Score.CompareTo(x.Score);
            return c != 0 ? c : string.Compare(x.Title, y.Title, StringComparison.CurrentCultureIgnoreCase);
        });
    }

    // ---------- фонове стеження (без вікна): лише вибрані частини; результати - у ті самі списки ----------
    static List<string> lastListen = new List<string>();
    static HashSet<uint> lastPids = new HashSet<uint>();

    // чи з'явилися нові процеси з минулого разу (дешево: лише знімок списку)
    public static bool NewProcesses()
    {
        HashSet<uint> now = new HashSet<uint>();
        foreach (PInfo p in Snapshot()) now.Add(p.Pid);
        bool fresh = false;
        foreach (uint id in now) if (!lastPids.Contains(id)) { fresh = true; break; }
        lastPids = now;
        return fresh;
    }

    public static void Background(bool procs, bool auto, bool svc, bool sys)
    {
        List<Finding> p = null, a = null, s = null, y = null;
        int na = 0, pc = 0;
        if (procs) { List<string> listen = new List<string>(); p = Processes(out na, out pc, listen); lastListen = listen; SecLists.Mark(p); Sort(p); }
        if (auto) { a = Autostart(); SecLists.Mark(a); Sort(a); }
        if (svc) { s = Services(); SecLists.Mark(s); Sort(s); }
        if (sys) { y = Settings(lastListen, lastListen.Count > 0); SecLists.Mark(y); }
        if (Busy) return;                 // тим часом почалося ручне сканування - його результат новіший
        if (p != null) { Proc = p; NoAccess = na; ProcCount = pc; ProcSkipped = false; }
        if (a != null) Auto = a;
        if (s != null) Svc = s;
        if (y != null) Sys = y;
        if (p != null && a != null && s != null && y != null) LastAt = DateTime.Now;
        else if (LastAt == DateTime.MinValue && (a != null || p != null)) LastAt = DateTime.Now;
        Fire();
    }

    // ---------- спільний аналіз файла ----------
    // exe - файл програми (чи бібліотеки), args - решта команди; результат додається у f
    static void Analyze(Finding f, string exe, string args, bool sigAlways)
    {
        f.Path = exe ?? "";
        if (f.Path.Length == 0) return;
        string where;
        f.Place = SecHeur.Place(f.Path, out where);
        bool exists = File.Exists(f.Path);
        if (exists && (f.Place != SecHeur.PSys || sigAlways))
        {
            string sg;
            f.SigKind = Sig.Check(f.Path, out sg);
            f.Signer = sg;
        }
        int nr = SecHeur.NameRisk(f.Path, f.Why);
        f.Score += nr;
        if (f.SigKind == Sig.Bad) f.Add(4, L.T("Цифровий підпис недійсний: файл змінено після підписання або сертифікат не довірений."));
        if (exists && f.Place == SecHeur.PUser && (f.SigKind == Sig.None || f.SigKind == Sig.Expired))
        {
            string ext = Path.GetExtension(f.Path).ToLowerInvariant();
            bool script = Array.IndexOf(new string[] { ".vbs", ".vbe", ".js", ".jse", ".wsf", ".hta", ".ps1", ".bat", ".cmd" }, ext) >= 0;
            if (script) f.Add(2, L.T("Скрипт (") + ext + L.T(") з теки користувача (") + where + ").");
            else f.Add(3, L.T("Без цифрового підпису і лежить у теці користувача (") + where + L.T(") - сюди може писати будь-яка програма без прав адміністратора."));
            if (where == "Temp" || where == L.T("Кошик")) f.Add(1, L.T("Тека ") + where + L.T(" - незвичне місце для програми, що працює постійно."));
        }
        if (exists && f.SigKind != Sig.Ok && Path.GetExtension(f.Path).ToLowerInvariant() != ".sys")
        {
            PeInfo pe = PeCache(f.Path);
            if (pe != null && pe.Ok && pe.Packer.Length > 0 && f.Place != SecHeur.PSys)
                f.Add(f.Place == SecHeur.PUser ? 2 : 1, L.T("Запакований чи захищений (") + pe.Packer + L.T("): вміст приховано від аналізу. Так роблять і ігри, і шкідливі програми."));
        }
        // системна програма-посередник (powershell, wscript, rundll32…) запускає щось із теки користувача
        string full = (args ?? "");
        if (full.Length > 0)
        {
            f.Score += SecHeur.CmdRisk(Path.GetFileName(f.Path) + " " + full, f.Why);
            string fn = Path.GetFileNameWithoutExtension(f.Path).ToLowerInvariant();
            if (Array.IndexOf(new string[] { "powershell", "pwsh", "wscript", "cscript", "mshta", "cmd", "regsvr32", "conhost" }, fn) >= 0)
            {
                string w2;
                foreach (string part in full.Split('"', ' '))
                    if (part.Length > 3 && part.IndexOf('\\') > 0 && SecHeur.Place(part, out w2) == SecHeur.PUser)
                    {
                        f.Add(2, L.T("Через «") + Path.GetFileName(f.Path) + L.T("» запускається файл з теки користувача (") + w2 + ").");
                        break;
                    }
            }
        }
    }

    static readonly Dictionary<string, PeInfo> peCache = new Dictionary<string, PeInfo>(StringComparer.OrdinalIgnoreCase);
    static PeInfo PeCache(string path)
    {
        PeInfo p;
        lock (peCache) if (peCache.TryGetValue(path, out p)) return p;
        p = PeInfo.Read(path);
        lock (peCache) peCache[path] = p;
        return p;
    }

    // ---------- процеси ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize; public uint cntUsage; public uint th32ProcessID; public IntPtr th32DefaultHeapID; public uint th32ModuleID;
        public uint cntThreads; public uint th32ParentProcessID; public int pcPriClassBase; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr h, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr h, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);

    sealed class PInfo { public uint Pid, Parent; public string Name = "", Path = "", Cmd = ""; public long Created; }

    static string CmdLine(IntPtr h)
    {
        int need;
        Win.NtQueryInformationProcess(h, 60, IntPtr.Zero, 0, out need);          // ProcessCommandLineInformation (Windows 8.1+)
        if (need <= 0 || need > 1 << 20) return "";
        IntPtr buf = Marshal.AllocHGlobal(need);
        try
        {
            if (Win.NtQueryInformationProcess(h, 60, buf, need, out need) != 0) return "";
            int len = Marshal.ReadInt16(buf);
            IntPtr s = Marshal.ReadIntPtr(buf, IntPtr.Size == 8 ? 8 : 4);
            return len > 0 && s != IntPtr.Zero ? Marshal.PtrToStringUni(s, len / 2) : "";
        }
        catch { return ""; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static List<PInfo> Snapshot()
    {
        List<PInfo> r = new List<PInfo>();
        IntPtr snap = CreateToolhelp32Snapshot(2, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return r;
        try
        {
            PROCESSENTRY32W e = new PROCESSENTRY32W();
            e.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W));
            if (!Process32FirstW(snap, ref e)) return r;
            do
            {
                PInfo p = new PInfo();
                p.Pid = e.th32ProcessID; p.Parent = e.th32ParentProcessID; p.Name = e.szExeFile ?? "";
                r.Add(p);
            } while (Process32NextW(snap, ref e));
        }
        finally { Win.CloseHandle(snap); }
        return r;
    }

    // мережа: для кожного процесу - порти, відкриті назовні, і кількість з'єднань не з цим комп'ютером
    internal static void Tcp(Dictionary<uint, int[]> map, List<KeyValuePair<int, uint>> listens)
    {
        foreach (int af in new int[] { 2, 23 })
        {
            int size = 0;
            Win.GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, 5, 0);       // TCP_TABLE_OWNER_PID_ALL
            if (size <= 0) continue;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (Win.GetExtendedTcpTable(buf, ref size, false, af, 5, 0) != 0) continue;
                int n = Marshal.ReadInt32(buf), rs = af == 2 ? 24 : 56;
                for (int i = 0; i < n; i++)
                {
                    IntPtr row = new IntPtr(buf.ToInt64() + 4 + (long)i * rs);
                    uint state, pid; int lport; bool lLoop, rLoop;
                    if (af == 2)
                    {
                        state = (uint)Marshal.ReadInt32(row, 0);
                        uint la = (uint)Marshal.ReadInt32(row, 4), ra = (uint)Marshal.ReadInt32(row, 12);
                        lport = Port(Marshal.ReadInt32(row, 8));
                        pid = (uint)Marshal.ReadInt32(row, 20);
                        lLoop = (la & 0xFF) == 127; rLoop = (ra & 0xFF) == 127 || ra == 0;
                    }
                    else
                    {
                        byte[] la = new byte[16], ra = new byte[16];
                        Marshal.Copy(row, la, 0, 16);
                        Marshal.Copy(new IntPtr(row.ToInt64() + 24), ra, 0, 16);
                        lport = Port(Marshal.ReadInt32(row, 20));
                        state = (uint)Marshal.ReadInt32(row, 48);
                        pid = (uint)Marshal.ReadInt32(row, 52);
                        lLoop = Loop6(la); rLoop = Loop6(ra) || Zero6(ra);
                    }
                    int[] v;
                    if (!map.TryGetValue(pid, out v)) { v = new int[2]; map[pid] = v; }
                    if (state == 2 && !lLoop) { v[0]++; listens.Add(new KeyValuePair<int, uint>(lport, pid)); }
                    else if (state == 5 && !rLoop) v[1]++;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    static int Port(int raw) { return ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF); }
    static bool Zero6(byte[] a) { foreach (byte b in a) if (b != 0) return false; return true; }
    static bool Loop6(byte[] a) { for (int i = 0; i < 15; i++) if (a[i] != 0) return false; return a[15] == 1; }

    static readonly string[] OfficeApps = { "winword", "excel", "powerpnt", "outlook", "onenote", "mspub", "msaccess", "visio", "acrord32", "acrobat", "foxitreader", "foxitpdfreader", "wordpad", "hwp", "eqnedt32" };
    static readonly string[] Shells = { "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "certutil", "bitsadmin", "schtasks", "wmic", "msiexec" };

    // системні процеси й те, хто їх має запускати; процеси, що в Windows бувають лише в одному екземплярі
    static readonly Dictionary<string, string> SysParents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "svchost.exe", "services.exe" }, { "services.exe", "wininit.exe" }, { "lsass.exe", "wininit.exe" },
        { "taskhostw.exe", "svchost.exe" }, { "runtimebroker.exe", "svchost.exe" }, { "sihost.exe", "svchost.exe" }
    };
    static readonly string[] Singles = { "lsass.exe", "services.exe", "wininit.exe" };

    static List<Finding> Processes(out int noAccess, out int count, List<string> listenOut)
    {
        noAccess = 0;
        List<PInfo> ps = Snapshot();
        count = ps.Count;
        Dictionary<uint, PInfo> byPid = new Dictionary<uint, PInfo>();
        uint self = (uint)Process.GetCurrentProcess().Id;
        foreach (PInfo p in ps)
        {
            byPid[p.Pid] = p;
            if (p.Pid == 0 || p.Pid == 4) continue;
            IntPtr h = Win.OpenProcess(0x1000, false, p.Pid);        // PROCESS_QUERY_LIMITED_INFORMATION - як у Диспетчера завдань, пам'ять не читається
            if (h == IntPtr.Zero) continue;
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int n = sb.Capacity;
                if (Win.QueryFullProcessImageNameW(h, 0, sb, ref n)) p.Path = sb.ToString();
                long c, x, k, u;
                if (GetProcessTimes(h, out c, out x, out k, out u)) p.Created = c;
                p.Cmd = CmdLine(h);
            }
            finally { Win.CloseHandle(h); }
        }
        HashSet<uint> visible = new HashSet<uint>();
        try
        {
            Win.EnumWindows(delegate (IntPtr w, IntPtr l)
            {
                if (Win.IsWindowVisible(w)) { uint pid; Win.GetWindowThreadProcessId(w, out pid); visible.Add(pid); }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        Dictionary<uint, int[]> net = new Dictionary<uint, int[]>();
        List<KeyValuePair<int, uint>> listens = new List<KeyValuePair<int, uint>>();
        try { Tcp(net, listens); } catch (Exception ex) { Util.ErrorLog(ex); }
        HashSet<string> seen = new HashSet<string>();
        listens.Sort(delegate (KeyValuePair<int, uint> a, KeyValuePair<int, uint> b) { return a.Key.CompareTo(b.Key); });
        foreach (KeyValuePair<int, uint> l in listens)
        {
            PInfo o;
            string nm = byPid.TryGetValue(l.Value, out o) ? o.Name : "PID " + l.Value;
            if (l.Value == 4) nm = L.T("System (служби Windows)");
            string s = "TCP " + l.Key + " - " + nm;
            if (seen.Add(s)) listenOut.Add(s);
        }

        Dictionary<string, int> names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (PInfo p in ps) { int c; names.TryGetValue(p.Name, out c); names[p.Name] = c + 1; }
        Dictionary<string, Finding> by = new Dictionary<string, Finding>(StringComparer.OrdinalIgnoreCase);
        foreach (PInfo p in ps)
        {
            if (Stop) break;
            if (p.Pid == 0 || p.Pid == 4 || p.Pid == self) continue;
            if (p.Path.Length == 0) { noAccess++; continue; }       // захищені системні процеси - Windows не дає навіть шлях
            Finding f;
            if (!by.TryGetValue(p.Path, out f))
            {
                f = new Finding();
                f.Area = "proc";
                f.Title = Path.GetFileName(p.Path);
                Analyze(f, p.Path, "", false);
                if (!File.Exists(p.Path)) f.Add(2, L.T("Файл, з якого запущено процес, уже видалено з диска - так іноді ховаються шкідливі програми (але й оновлення програм)."));
                by[p.Path] = f;
            }
            f.Pids.Add((int)p.Pid);
            // аргументи: лише якщо це не сама програма без параметрів
            string args = "";
            if (p.Cmd.Length > 0)
            {
                string a0;
                SecHeur.ExeOf(p.Cmd, out a0);
                args = a0;
            }
            if (args.Length > 0)
            {
                int before = f.Score;
                f.Score += SecHeur.CmdRisk(f.Title + " " + args, f.Why);
                if (f.Score > before && f.Cmd.Length == 0) f.Cmd = p.Cmd;
            }
            // хто запустив
            PInfo par;
            if (byPid.TryGetValue(p.Parent, out par) && (par.Created == 0 || p.Created == 0 || par.Created <= p.Created))
            {
                string pn = Path.GetFileNameWithoutExtension(par.Name).ToLowerInvariant(), cn = Path.GetFileNameWithoutExtension(p.Name).ToLowerInvariant();
                if (Array.IndexOf(OfficeApps, pn) >= 0 && Array.IndexOf(Shells, cn) >= 0)
                {
                    f.Add(5, L.T("Запущено з «") + par.Name + L.T("»: документ чи PDF запускає командний рядок або скрипт - типова ознака шкідливого макросу."));
                    if (f.Cmd.Length == 0) f.Cmd = p.Cmd;
                }
                // системний процес запущено не тим, ким треба (підроблений системний процес)
                string want;
                if (SysParents.TryGetValue(p.Name, out want) && !string.Equals(par.Name, want, StringComparison.OrdinalIgnoreCase) && SecHeur.InSys32(p.Path))
                    f.Add(5, L.F("«{0}» зазвичай запускає {1}, а тут - «{2}» (PID {3}).", p.Name, want, par.Name, par.Pid));
                if (f.Detail.Length == 0) f.Detail = L.T("запустив: ") + par.Name;
            }
            int same;
            if (Array.IndexOf(Singles, p.Name.ToLowerInvariant()) >= 0 && names.TryGetValue(p.Name, out same) && same > 1)
                f.Add(5, L.F("Другий екземпляр «{0}»: у Windows він один - підроблені копії часто видають себе за нього.", p.Name));
            int[] nv;
            bool shady = f.SigKind != Sig.Ok && f.Place == SecHeur.PUser;
            if (net.TryGetValue(p.Pid, out nv) && shady)
            {
                if (nv[0] > 0) f.Add(2, L.T("Відкрив порт для вхідних підключень з мережі."));
                if (nv[1] > 0) f.Add(1, L.T("Має з'єднання з іншими комп'ютерами (інтернет чи мережа)."));
            }
            if (shady && f.Score >= 3 && !visible.Contains(p.Pid)) f.Add(1, L.T("Працює без видимого вікна."));
        }
        List<Finding> r = new List<Finding>(by.Values);
        foreach (Finding f in r)
        {
            string pid = f.Pids.Count == 1 ? "PID " + f.Pids[0] : f.Pids.Count + L.T(" процеси");
            f.Detail = pid + (f.Detail.Length > 0 ? " · " + f.Detail : "");
        }
        return r;
    }

    public static bool AntiCheatRunning(out string name)
    {
        name = "";
        foreach (PInfo p in Snapshot())
            if (AntiCheat.IsService(p.Name)) { name = p.Name; return true; }
        return false;
    }

    // ---------- автозапуск ----------
    static RegistryKey LM { get { return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64); } }
    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    // увімкнено чи вимкнено в Диспетчері завдань (перший байт: непарний - вимкнено)
    static bool IsOff(bool hklm, string sub, string name)
    {
        try
        {
            using (RegistryKey root = hklm ? LM : RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default))
            using (RegistryKey k = root.OpenSubKey(Approved + sub))
            {
                byte[] b = k == null ? null : k.GetValue(name) as byte[];
                return b != null && b.Length > 0 && (b[0] & 1) != 0;
            }
        }
        catch { return false; }
    }

    // вимкнути/увімкнути, як це робить Диспетчер завдань (сам запис лишається на місці - нічого не видаляється)
    public static string SetApproved(Finding f, bool on)
    {
        try
        {
            using (RegistryKey root = f.Hklm ? LM : RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default))
            using (RegistryKey k = root.CreateSubKey(Approved + f.ApprovedSub))
            {
                byte[] b = new byte[12];
                b[0] = (byte)(on ? 2 : 3);
                if (!on) { long ft = DateTime.Now.ToFileTimeUtc(); for (int i = 0; i < 8; i++) b[4 + i] = (byte)(ft >> (8 * i)); }
                k.SetValue(f.ApprovedName, b, RegistryValueKind.Binary);
            }
            f.Off = !on;
            return "";
        }
        catch (UnauthorizedAccessException) { return L.T("Потрібні права адміністратора: запис зроблено для всіх користувачів комп'ютера."); }
        catch (Exception ex) { return ex.Message; }
    }

    public static string SetTask(Finding f, bool on)
    {
        try
        {
            Type t = Type.GetTypeFromProgID("Schedule.Service");
            dynamic svc = Activator.CreateInstance(t);
            svc.Connect();
            int cut = f.TaskPath.LastIndexOf('\\');
            dynamic folder = svc.GetFolder(cut <= 0 ? "\\" : f.TaskPath.Substring(0, cut));
            dynamic task = folder.GetTask(f.TaskPath.Substring(cut + 1));
            task.Enabled = on;
            f.Off = !on;
            return "";
        }
        catch (UnauthorizedAccessException) { return L.T("Потрібні права адміністратора: завдання створено від імені системи."); }
        catch (Exception ex) { return ex.Message; }
    }

    static List<Finding> Autostart()
    {
        List<Finding> r = new List<Finding>();
        // ключі Run: (корінь, шлях, назва для StartupApproved або "" - якщо Диспетчер завдань їх не вимикає)
        object[][] keys =
        {
            new object[] { false, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run" },
            new object[] { false, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "" },
            new object[] { false, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run", "" },
            new object[] { true, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run" },
            new object[] { true, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "" },
            new object[] { true, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32" },
            new object[] { true, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce", "" },
            new object[] { true, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run", "" }
        };
        foreach (object[] k in keys)
        {
            bool hklm = (bool)k[0];
            string sub = (string)k[1], appr = (string)k[2];
            try
            {
                using (RegistryKey root = hklm ? LM : RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default))
                using (RegistryKey key = root.OpenSubKey(sub))
                {
                    if (key == null) continue;
                    foreach (string name in key.GetValueNames())
                    {
                        string cmd = key.GetValue(name) as string;
                        if (string.IsNullOrEmpty(cmd)) continue;
                        Finding f = new Finding();
                        f.Area = "auto";
                        f.Title = name.Length > 0 ? name : L.T("(без назви)");
                        f.Cmd = cmd;
                        f.Detail = L.T("Реєстр ") + (hklm ? "HKLM" : "HKCU") + "\\…\\" + sub.Substring(sub.LastIndexOf('\\') + 1) + (sub.Contains("Policies") ? L.T(" (політики)") : "") + (sub.Contains("WOW6432") ? L.T(" (32-біт)") : "");
                        string args, exe = SecHeur.ExeOf(cmd, out args);
                        Analyze(f, exe, args, true);
                        if (exe.Length > 0 && !File.Exists(exe)) f.Add(0, L.T("Файл не знайдено - швидше за все, залишок видаленої програми."));
                        if (sub.Contains("Policies")) f.Add(1, L.T("Автозапуск через політики - незвичне місце, ним користуються рідко."));
                        if (appr.Length > 0) { f.AutoKind = "run"; f.Hklm = hklm; f.ApprovedSub = appr; f.ApprovedName = name; f.Off = IsOff(hklm, appr, name); }
                        else
                        {
                            // RunOnce і політики Диспетчер завдань не вимикає - лише прибрати запис (з відкатом)
                            Fix fx = new Fix().Reg(hklm, sub, name, "del", "");
                            fx.Button = L.T("Прибрати запис");
                            fx.Confirm = L.T("Запис «") + f.Title + L.T("» буде прибрано з автозапуску (") + f.Detail + L.T("). Сам файл програми не видаляється.");
                            fx.Done = L.T("Запис прибрано з автозапуску.");
                            f.Fixes.Add(fx);
                        }
                        r.Add(f);
                    }
                }
            }
            catch (Exception ex) { Util.ErrorLog(ex); }
        }
        // теки автозавантаження
        foreach (bool common in new bool[] { false, true })
        {
            string dir = Environment.GetFolderPath(common ? Environment.SpecialFolder.CommonStartup : Environment.SpecialFolder.Startup);
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.GetFiles(dir))
                {
                    string nm = Path.GetFileName(file);
                    if (nm.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    Finding f = new Finding();
                    f.Area = "auto";
                    f.Title = Path.GetFileNameWithoutExtension(nm);
                    f.Detail = L.T("Тека автозавантаження") + (common ? L.T(" (для всіх користувачів)") : "");
                    string exe = file, args = "";
                    if (nm.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) { string t = Shortcut(file, out args); if (t.Length > 0) exe = t; }
                    f.Cmd = "\"" + exe + "\"" + (args.Length > 0 ? " " + args : "");
                    Analyze(f, exe, args, true);
                    f.AutoKind = "folder"; f.Hklm = common; f.ApprovedSub = "StartupFolder"; f.ApprovedName = nm; f.Off = IsOff(common, "StartupFolder", nm);
                    r.Add(f);
                }
            }
            catch (Exception ex) { Util.ErrorLog(ex); }
        }
        if (Stop) return r;
        try { Tasks(r); } catch (Exception ex) { Util.ErrorLog(ex); }
        try { Winlogon(r); } catch (Exception ex) { Util.ErrorLog(ex); }
        try { Wmi(r); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return r;
    }

    static string Shortcut(string lnk, out string args)
    {
        args = "";
        try
        {
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic s = sh.CreateShortcut(lnk);
            string t = s.TargetPath;
            args = s.Arguments ?? "";
            return t ?? "";
        }
        catch { return ""; }
    }

    static void Tasks(List<Finding> r)
    {
        Type t = Type.GetTypeFromProgID("Schedule.Service");
        if (t == null) return;
        dynamic svc = Activator.CreateInstance(t);
        svc.Connect();
        WalkTasks((object)svc.GetFolder("\\"), r, 0);
    }

    static void WalkTasks(object folderObj, List<Finding> r, int depth)
    {
        dynamic folder = folderObj;
        if (depth > 8 || Stop) return;
        string fp = folder.Path;
        bool ms = fp.StartsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase);
        foreach (dynamic task in folder.GetTasks(1))           // TASK_ENUM_HIDDEN - разом із прихованими
        {
            try
            {
                string name = task.Name, path = task.Path;
                bool enabled = task.Enabled;
                dynamic def = task.Definition;
                bool hidden = false;
                try { hidden = def.Settings.Hidden; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                foreach (dynamic a in def.Actions)
                {
                    int type = a.Type;
                    if (type != 0) continue;                      // лише «запустити програму»
                    string p = a.Path ?? "", args = a.Arguments ?? "";
                    string ex2, exe = SecHeur.ExeOf((p.StartsWith("\"") ? p : "\"" + p + "\"") + " " + args, out ex2);
                    Finding f = new Finding();
                    f.Area = "auto";
                    f.Title = name;
                    f.Cmd = p + (args.Length > 0 ? " " + args : "");
                    f.Detail = L.T("Планувальник завдань: ") + path;
                    Analyze(f, exe, args, !ms);
                    if (hidden && !ms) f.Add(f.Score >= 2 ? 1 : 0, L.T("Завдання позначене як приховане."));
                    if (ms && f.Place != SecHeur.PSys && f.SigKind != Sig.Ok && File.Exists(exe)) f.Add(3, L.T("Завдання в розділі Microsoft, але запускає сторонній файл без підпису - так маскуються."));
                    if (ms && f.Score < 3) continue;              // звичайні завдання Windows не показуємо
                    f.AutoKind = "task"; f.TaskPath = path; f.Off = !enabled;
                    r.Add(f);
                    break;
                }
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
        foreach (dynamic sub in folder.GetFolders(0)) WalkTasks((object)sub, r, depth + 1);
    }

    static void Winlogon(List<Finding> r)
    {
        using (RegistryKey lm = LM)
        {
            using (RegistryKey k = lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"))
                if (k != null)
                {
                    string shell = (k.GetValue("Shell") as string ?? "").Trim(), ui = (k.GetValue("Userinit") as string ?? "").Trim().TrimEnd(',').Trim();
                    string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    if (shell.Length > 0 && !shell.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) && !shell.Equals(Path.Combine(win, "explorer.exe"), StringComparison.OrdinalIgnoreCase))
                    {
                        Finding f = new Finding(); f.Area = "auto"; f.Title = L.T("Оболонка Windows (Winlogon Shell)"); f.Cmd = shell; f.Detail = L.T("Реєстр HKLM\\…\\Winlogon");
                        f.Add(6, L.T("Замість Провідника (explorer.exe) під час входу запускається «") + shell + L.T("». Так закріплюються шкідливі програми."));
                        Fix fx = new Fix().Reg(true, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "Shell", "sz", "explorer.exe");
                        fx.Button = L.T("Повернути explorer.exe"); fx.Confirm = L.T("Оболонкою знову стане стандартний Провідник (explorer.exe). Діє з наступного входу в Windows."); fx.Done = L.T("Стандартну оболонку повернуто; діє з наступного входу.");
                        f.Fixes.Add(fx);
                        r.Add(f);
                    }
                    if (ui.Length > 0 && !ui.Equals(Path.Combine(win, "system32\\userinit.exe"), StringComparison.OrdinalIgnoreCase) && !ui.Equals("userinit.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        Finding f = new Finding(); f.Area = "auto"; f.Title = L.T("Програма входу (Winlogon Userinit)"); f.Cmd = ui; f.Detail = L.T("Реєстр HKLM\\…\\Winlogon");
                        f.Add(6, L.T("Під час входу, крім userinit.exe, запускається ще щось: «") + ui + L.Q("»."));
                        Fix fx = new Fix().Reg(true, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "Userinit", "sz", Path.Combine(win, "system32\\userinit.exe") + ",");
                        fx.Button = L.T("Повернути стандартне"); fx.Confirm = L.T("Під час входу запускатиметься лише стандартний userinit.exe. Діє з наступного входу в Windows."); fx.Done = L.T("Стандартне значення повернуто; діє з наступного входу.");
                        f.Fixes.Add(fx);
                        r.Add(f);
                    }
                }
            // «відладчик» замість програми (Image File Execution Options)
            using (RegistryKey k = lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options"))
                if (k != null)
                    foreach (string sub in k.GetSubKeyNames())
                        using (RegistryKey s = k.OpenSubKey(sub))
                        {
                            string dbg = s == null ? null : s.GetValue("Debugger") as string;
                            if (string.IsNullOrEmpty(dbg)) continue;
                            Finding f = new Finding(); f.Area = "auto"; f.Title = L.T("Підміна запуску: ") + sub; f.Cmd = dbg; f.Detail = L.T("Реєстр HKLM\\…\\Image File Execution Options");
                            string args, exe = SecHeur.ExeOf(dbg, out args);
                            Analyze(f, exe, args, true);
                            bool known = f.SigKind == Sig.Ok && (Sig.Microsoft(f.Signer) || f.Signer.IndexOf("Sysinternals", StringComparison.OrdinalIgnoreCase) >= 0);
                            f.Add(known ? 0 : 4, L.T("Коли запускають «") + sub + L.T("», Windows натомість запускає «") + Path.GetFileName(exe) + L.Q("».") +
                                  (known ? L.T(" Підписано ") + f.Signer + L.T(" - схоже на заміну Диспетчера завдань (Process Explorer).") : L.T(" Так блокують антивіруси чи підміняють програми.")));
                            Fix fx = new Fix().Reg(true, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\" + sub, "Debugger", "del", "");
                            fx.Button = L.T("Прибрати підміну"); fx.Confirm = L.Q("«") + sub + L.T("» знову запускатиметься сам, без «") + Path.GetFileName(exe) + L.Q("»."); fx.Done = L.T("Підміну запуску прибрано.");
                            f.Fixes.Add(fx);
                            r.Add(f);
                        }
            foreach (string wk in new string[] { @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows", @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Windows" })
                using (RegistryKey k = lm.OpenSubKey(wk))
                {
                    if (k == null) continue;
                    string dlls = (k.GetValue("AppInit_DLLs") as string ?? "").Trim();
                    object load = k.GetValue("LoadAppInit_DLLs");
                    if (dlls.Length > 0 && load is int && (int)load != 0)
                    {
                        Finding f = new Finding(); f.Area = "auto"; f.Title = "AppInit_DLLs"; f.Cmd = dlls; f.Detail = L.T("Реєстр HKLM\\…\\Windows");
                        f.Add(5, L.T("Бібліотеки «") + dlls + L.T("» підвантажуються в кожну програму з вікнами - застарілий механізм, яким користуються шкідливі програми."));
                        Fix fx = new Fix().Reg(true, wk, "LoadAppInit_DLLs", "dword", "0");
                        fx.Button = L.T("Вимкнути AppInit_DLLs"); fx.Confirm = L.T("Windows перестане підвантажувати ці бібліотеки в програми (сам список лишиться)."); fx.Done = L.T("AppInit_DLLs вимкнено; діє для нових програм.");
                        f.Fixes.Add(fx);
                        r.Add(f);
                    }
                }
        }
    }

    // постійні підписки WMI, що запускають команди чи скрипти (автозапуск «без файлів»)
    static void Wmi(List<Finding> r)
    {
        dynamic loc = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator"));
        dynamic svc = loc.ConnectServer(".", "root\\subscription");
        foreach (string cls in new string[] { "CommandLineEventConsumer", "ActiveScriptEventConsumer" })
            foreach (dynamic o in svc.ExecQuery("SELECT * FROM " + cls))
            {
                Finding f = new Finding(); f.Area = "auto"; f.Detail = "WMI: " + cls;
                try { f.Title = "WMI: " + (string)o.Properties_.Item("Name").Value; } catch { f.Title = L.T("WMI-підписка"); }
                try
                {
                    f.Cmd = cls.StartsWith("Command") ? (string)o.Properties_.Item("CommandLineTemplate").Value : L.T("скрипт ") + (string)o.Properties_.Item("ScriptingEngine").Value;
                }
                catch (Exception skipEx) { SbLog.Skip(skipEx); }
                f.Add(5, L.T("Постійна підписка WMI запускає ") + (cls.StartsWith("Command") ? L.T("команду") : L.T("скрипт")) + L.T(" за подією системи. Звичайні програми так майже не роблять; це відомий спосіб прихованого автозапуску."));
                f.Score += SecHeur.CmdRisk(f.Cmd, f.Why);
                r.Add(f);
            }
    }

    // ---------- служби й драйвери ----------

    static string Indirect(string s)
    {
        if (string.IsNullOrEmpty(s) || s[0] != '@') return s ?? "";
        try { StringBuilder b = new StringBuilder(512); if (Win.SHLoadIndirectString(s, b, b.Capacity, IntPtr.Zero) == 0) return b.ToString(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return "";
    }

    static List<Finding> Services()
    {
        List<Finding> r = new List<Finding>();
        using (RegistryKey lm = LM)
        using (RegistryKey root = lm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
        {
            if (root == null) return r;
            foreach (string name in root.GetSubKeyNames())
            {
                if (Stop) break;
                try
                {
                    using (RegistryKey k = root.OpenSubKey(name))
                    {
                        if (k == null) continue;
                        object to = k.GetValue("Type"), so = k.GetValue("Start");
                        int type = to is int ? (int)to : 0, start = so is int ? (int)so : 3;
                        string img = k.GetValue("ImagePath") as string;
                        if (string.IsNullOrEmpty(img) || start == 4 || (type & 0x33) == 0) continue;
                        bool driver = (type & 0x3) != 0;
                        string args, exe = SecHeur.ExeOf(img, out args);
                        if (driver && !exe.Contains("\\")) exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "system32\\drivers\\" + exe);
                        string dll = null;
                        using (RegistryKey pk = k.OpenSubKey("Parameters"))
                            if (pk != null) dll = pk.GetValue("ServiceDll") as string;
                        if (dll == null) dll = k.GetValue("ServiceDll") as string;
                        if (dll != null) dll = Environment.ExpandEnvironmentVariables(dll.Trim('"'));
                        string target = !string.IsNullOrEmpty(dll) ? dll : exe;
                        Finding f = new Finding();
                        f.Area = "svc";
                        string disp = Indirect(k.GetValue("DisplayName") as string);
                        f.Title = disp.Length > 0 ? disp : name;
                        f.Cmd = img + (dll != null ? "  [ServiceDll: " + dll + "]" : "");
                        string[] starts = { L.T("під час завантаження"), L.T("під час старту системи"), L.T("автоматично"), L.T("вручну") };
                        f.Detail = (driver ? L.T("Драйвер ") : L.T("Служба ")) + name + L.T(" · запуск ") + (start >= 0 && start < 4 ? starts[start] : "?");
                        Analyze(f, target, driver ? "" : args, false);
                        bool exists = File.Exists(target);
                        if (!exists && start <= 2 && target.Length > 0) f.Add(1, L.T("Файл не знайдено - залишок видаленої програми (служба не запуститься)."));
                        if (driver && exists && f.SigKind == Sig.None) f.Add(3, L.T("Драйвер без цифрового підпису: звичайна 64-бітна Windows такі не завантажує, якщо не ввімкнено тестовий режим."));
                        if (!driver && f.Place == SecHeur.PUser && exists)
                        {
                            if (f.SigKind != Sig.Ok) f.Add(1, L.T("Служба запускається з теки користувача - так закріплюються шкідливі програми."));
                        }
                        string acct = (k.GetValue("ObjectName") as string ?? "LocalSystem");
                        bool system = acct.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) || acct.Length == 0;
                        if (!driver && exists && f.Place != SecHeur.PSys && system)
                        {
                            bool wf = SecHeur.UserWritable(target, false), wd = !wf && SecHeur.UserWritable(Path.GetDirectoryName(target), true);
                            if (wf) f.Add(5, L.T("Вразливість: файл служби може змінити звичайний користувач, а служба працює з правами системи - так отримують повний контроль над комп'ютером."));
                            else if (wd) f.Add(3, L.T("Вразливість: у теку служби може писати звичайний користувач (підкласти бібліотеку, яку служба завантажить з правами системи)."));
                            if (wf || wd)
                            {
                                Fix fx = null;
                                try { fx = SecFix.StripWrite(wf ? target : Path.GetDirectoryName(target), !wf); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                                if (fx != null)
                                {
                                    fx.Button = L.T("Закрити запис");
                                    fx.Confirm = L.T("Звичайні користувачі більше не зможуть змінювати ") + (wf ? L.T("файл «") + Path.GetFileName(target) + L.Q("»") : L.T("теку «") + Path.GetDirectoryName(target) + L.Q("»")) +
                                                 L.T(" (читати й запускати - зможуть). Якщо програма оновлює себе без прав адміністратора, її оновлення може перестати працювати - тоді скасуйте виправлення.");
                                    fx.Done = L.T("Права на запис закрито.");
                                    f.Fixes.Add(fx);
                                }
                            }
                        }
                        // шлях без лапок із пробілами
                        if (!driver && !img.TrimStart().StartsWith("\"") && exe.Contains(" ") && f.Place != SecHeur.PSys)
                        {
                            bool weak = false;
                            for (int sp = exe.IndexOf(' '); sp > 0 && !weak; sp = exe.IndexOf(' ', sp + 1))
                            {
                                string d = Path.GetDirectoryName(exe.Substring(0, sp));
                                if (!string.IsNullOrEmpty(d) && Directory.Exists(d) && SecHeur.UserWritable(d, true)) weak = true;
                            }
                            if (weak) f.Add(4, L.T("Вразливість: шлях без лапок, а теку на цьому шляху відкрито для запису - можна підкласти файл, який служба запустить з правами системи."));
                            else f.Add(1, L.T("Шлях до файла служби без лапок (зараз не вразливо: теки захищені від запису)."));
                            if (File.Exists(exe))
                            {
                                bool expand = false;
                                try { expand = k.GetValueKind("ImagePath") == RegistryValueKind.ExpandString; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                                Fix fx = new Fix().Reg(true, @"SYSTEM\CurrentControlSet\Services\" + name, "ImagePath", expand ? "expand" : "sz", "\"" + exe + "\"" + (args.Length > 0 ? " " + args : ""));
                                fx.Button = L.T("Взяти шлях у лапки");
                                fx.Confirm = L.T("Шлях до файла служби буде записано в лапках:\n\"") + exe + "\"" + (args.Length > 0 ? " " + args : "") + L.T("\nСлужба працюватиме так само. Діє з наступного запуску служби.");
                                fx.Done = L.T("Шлях взято в лапки.");
                                f.Fixes.Add(fx);
                            }
                        }
                        f.SvcName = name; f.SvcStart = start;
                        // вимкнути можна сторонню службу (не з системної теки) і не завантажувальний драйвер - системні служби не чіпаємо
                        if ((!driver || start >= 2) && f.Place != SecHeur.PSys && exists)
                        {
                            string scx = Path.Combine(Environment.SystemDirectory, "sc.exe");
                            Fix fx = new Fix().Reg(true, @"SYSTEM\CurrentControlSet\Services\" + name, "Start", "dword", "4");
                            fx.Ops.Add(new FixOp("RUNQ", scx, "stop \"" + name + "\""));
                            if (start == 2) fx.UndoRun.Add(new FixOp("RUNQ", scx, "start \"" + name + "\""));
                            fx.Button = driver ? L.T("Вимкнути драйвер") : L.T("Вимкнути службу");
                            fx.Confirm = (driver ? L.T("Драйвер") : L.T("Службу")) + L.Q(" «") + f.Title + L.T("» буде зупинено й більше не запускатиметься. Програма, якій ") + (driver ? L.T("він") : L.T("вона")) +
                                         L.T(" належить, може працювати неповно (не оновлюватиметься, не бачитиме пристрій тощо).");
                            fx.Done = (driver ? L.T("Драйвер вимкнено (повністю - після перезавантаження).") : L.T("Службу зупинено й вимкнено."));
                            f.Fixes.Add(fx);
                        }
                        r.Add(f);
                    }
                }
                catch (Exception skipEx) { SbLog.Skip(skipEx); }
            }
        }
        return r;
    }

    // ---------- налаштування Windows ----------
    static Finding Check(List<Finding> r, string title)
    {
        Finding f = new Finding();
        f.Area = "sys"; f.Title = title;
        r.Add(f);
        return f;
    }

    static object Val(RegistryKey root, string sub, string name)
    {
        try { using (RegistryKey k = root.OpenSubKey(sub)) return k == null ? null : k.GetValue(name); }
        catch { return null; }
    }

    static int IntVal(RegistryKey root, string sub, string name, int def)
    {
        object o = Val(root, sub, name);
        return o is int ? (int)o : def;
    }

    static bool KeyExists(RegistryKey root, string sub)
    {
        try { using (RegistryKey k = root.OpenSubKey(sub)) return k != null; }
        catch { return false; }
    }

    // стан продуктів захисту з Центру безпеки Windows: (назва, увімкнено, бази актуальні)
    static List<object[]> SecurityCenter(string cls)
    {
        List<object[]> r = new List<object[]>();
        try
        {
            dynamic loc = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator"));
            dynamic svc = loc.ConnectServer(".", "root\\SecurityCenter2");
            foreach (dynamic o in svc.ExecQuery("SELECT displayName, productState FROM " + cls))
            {
                string n = o.Properties_.Item("displayName").Value;
                int st = Convert.ToInt32(o.Properties_.Item("productState").Value);
                r.Add(new object[] { n, ((st >> 12) & 0xF) == 1, ((st >> 4) & 0xF) == 0 });
            }
        }
        catch { return null; }
        return r;
    }

    static List<Finding> Settings(List<string> listen, bool procs)
    {
        List<Finding> r = new List<Finding>();
        using (RegistryKey lm = LM)
        {
            RegistryKey cu = Registry.CurrentUser;
            // антивірус
            {
                Finding f = Check(r, L.T("Антивірус"));
                List<object[]> av = SecurityCenter("AntiVirusProduct");
                if (av == null) f.Add(0, L.T("Стан не вдалося прочитати з Центру безпеки Windows."));
                else
                {
                    bool on = false;
                    foreach (object[] a in av)
                    {
                        bool en = (bool)a[1], fresh = (bool)a[2];
                        if (en) on = true;
                        f.Why.Add((string)a[0] + ": " + (en ? L.T("увімкнено") : L.T("вимкнено")) + (en && !fresh ? L.T(", бази застарілі") : ""));
                        if (en && !fresh) f.Score += 2;
                    }
                    if (!on) f.Add(5, L.T("Жоден антивірус не працює. Увімкніть Microsoft Defender: Параметри → Конфіденційність і захист → Безпека Windows."));
                }
                int pol = IntVal(lm, @"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", 0) | IntVal(lm, @"SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", "DisableRealtimeMonitoring", 0);
                if (pol != 0)
                {
                    f.Add(3, L.T("Захист Microsoft Defender вимкнено політикою (реєстр). Якщо ви цього не робили - це часта дія шкідливих програм."));
                    Fix fx = new Fix().Reg(true, @"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", "del", "")
                                      .Reg(true, @"SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", "DisableRealtimeMonitoring", "del", "");
                    fx.Button = L.T("Прибрати заборону");
                    fx.Confirm = L.T("Політики, що вимикають Microsoft Defender, буде прибрано. Сам захист увімкніть у «Безпеці Windows» (або він увімкнеться після перезавантаження).");
                    fx.Done = L.T("Заборону прибрано. Увімкніть захист у «Безпеці Windows».");
                    f.Fixes.Add(fx);
                }
                if (f.Score > 0) f.Links.Add(new string[] { L.T("Безпека Windows"), "windowsdefender:" });
                f.Ok = f.Score == 0;
            }
            // винятки Defender (читаються лише з правами адміністратора)
            try
            {
                using (RegistryKey k = lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender\Exclusions\Paths"))
                    if (k != null)
                    {
                        List<string> bad = new List<string>();
                        foreach (string p in k.GetValueNames())
                        {
                            string w;
                            string pl = p.TrimEnd('\\');
                            if (pl.Length <= 3 || SecHeur.Place(pl + "\\x", out w) == SecHeur.PUser || pl.EndsWith("\\Windows", StringComparison.OrdinalIgnoreCase)) bad.Add(p);
                        }
                        if (bad.Count > 0)
                        {
                            Finding f = Check(r, L.T("Винятки Microsoft Defender"));
                            f.Add(4, L.T("Defender не перевіряє: ") + string.Join("; ", bad.ToArray()) + L.T(". Цілий диск чи теки користувача у винятках - небезпечно; шкідливі програми так себе ховають."));
                            f.Why.Add(L.T("Прибрати зайві винятки: Безпека Windows → Захист від вірусів і загроз → Керування параметрами → Винятки (Defender не дає змінювати їх іншим програмам)."));
                            f.Links.Add(new string[] { L.T("Налаштування Defender"), "windowsdefender://threatsettings" });
                        }
                    }
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
            // брандмауер
            {
                Finding f = Check(r, L.T("Брандмауер"));
                string[][] prof = { new[] { "StandardProfile", "PrivateProfile", L.T("приватні мережі") }, new[] { "PublicProfile", "PublicProfile", L.T("публічні мережі") } };
                bool third = false;
                List<object[]> fw = SecurityCenter("FirewallProduct");
                if (fw != null) foreach (object[] a in fw) if ((bool)a[1]) { third = true; f.Why.Add((string)a[0] + L.T(": увімкнено")); }
                Fix fx = new Fix();
                fx.Admin = true;
                string netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
                foreach (string[] p in prof)
                {
                    object pv = Val(lm, @"SOFTWARE\Policies\Microsoft\WindowsFirewall\" + p[1], "EnableFirewall");
                    int en = pv is int ? (int)pv : IntVal(lm, @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\" + p[0], "EnableFirewall", 1);
                    if (en == 0 && !third)
                    {
                        f.Add(p[0] == "PublicProfile" ? 4 : 2, L.T("Брандмауер Windows вимкнено для: ") + p[2] + "." + (pv is int ? L.T(" Так задано груповою політикою - змінити можна лише в ній.") : ""));
                        if (!(pv is int))
                        {
                            string pn = p[1].ToLowerInvariant();
                            fx.Ops.Add(new FixOp("RUN", netsh, "advfirewall set " + pn + " state on"));
                            fx.UndoRun.Add(new FixOp("RUN", netsh, "advfirewall set " + pn + " state off"));
                        }
                    }
                }
                if (fx.Ops.Count > 0)
                {
                    fx.Button = L.T("Увімкнути брандмауер");
                    fx.Confirm = L.T("Брандмауер Windows буде ввімкнено для мереж, де він зараз вимкнений. Якщо якась гра чи програма перестане з'єднуватися, Windows запитає дозвіл (або скасуйте виправлення).");
                    fx.Done = L.T("Брандмауер увімкнено.");
                    f.Fixes.Add(fx);
                }
                f.Ok = f.Score == 0;
                if (f.Ok && f.Why.Count == 0) f.Why.Add(L.T("Брандмауер Windows увімкнено."));
            }
            // контроль облікових записів
            {
                Finding f = Check(r, L.T("Контроль облікових записів (UAC)"));
                const string pol = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
                Fix fx = new Fix();
                bool lua = IntVal(lm, pol, "EnableLUA", 1) == 0;
                if (lua) { f.Add(5, L.T("UAC вимкнено: будь-яка програма отримує права адміністратора без запиту.")); fx.Reg(true, pol, "EnableLUA", "dword", "1"); }
                if (IntVal(lm, pol, "ConsentPromptBehaviorAdmin", 5) == 0) { if (!lua) f.Add(3, L.T("UAC не питає підтвердження - програми підвищують права мовчки.")); fx.Reg(true, pol, "ConsentPromptBehaviorAdmin", "dword", "5"); }
                if (IntVal(lm, pol, "PromptOnSecureDesktop", 1) == 0) { if (f.Score == 0) f.Add(1, L.T("Запит UAC показується не на захищеному робочому столі - інша програма може натиснути «Так» замість вас.")); fx.Reg(true, pol, "PromptOnSecureDesktop", "dword", "1"); }
                if (fx.Ops.Count > 0)
                {
                    fx.Button = L.T("Повернути стандартний UAC");
                    fx.Confirm = L.T("Контроль облікових записів буде ввімкнено зі стандартним запитом на захищеному робочому столі.") + (lua ? L.T(" Потрібне перезавантаження.") : "");
                    fx.Done = lua ? L.T("UAC увімкнено - перезавантажте комп'ютер.") : L.T("Стандартний UAC повернуто.");
                    f.Fixes.Add(fx);
                }
                f.Ok = f.Score == 0;
                if (f.Ok) f.Why.Add(L.T("Увімкнено, з запитом."));
            }
            // SMBv1
            {
                Finding f = Check(r, L.T("Протокол SMBv1"));
                bool cli = KeyExists(lm, @"SYSTEM\CurrentControlSet\Services\mrxsmb10") && IntVal(lm, @"SYSTEM\CurrentControlSet\Services\mrxsmb10", "Start", 4) != 4;
                bool srv = IntVal(lm, @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "SMB1", KeyExists(lm, @"SYSTEM\CurrentControlSet\Services\srv") ? 1 : 0) != 0 &&
                           KeyExists(lm, @"SYSTEM\CurrentControlSet\Services\srv") && IntVal(lm, @"SYSTEM\CurrentControlSet\Services\srv", "Start", 4) != 4;
                if (srv) f.Add(5, L.T("Сервер SMBv1 увімкнено - через цей застарілий протокол поширювалися WannaCry і NotPetya. Вимкніть компонент «Підтримка SMB 1.0/CIFS»."));
                else if (cli) f.Add(3, L.T("Клієнт SMBv1 увімкнено - застарілий небезпечний протокол. Вимкніть компонент «Підтримка SMB 1.0/CIFS», якщо немає дуже старого мережевого диска."));
                if (srv || cli)
                {
                    string dism = Path.Combine(Environment.SystemDirectory, "dism.exe");
                    Fix fx = SecFix.Run(dism, "/online /disable-feature /featurename:SMB1Protocol /norestart", dism, "/online /enable-feature /featurename:SMB1Protocol /all /norestart");
                    fx.Button = L.T("Вимкнути SMBv1");
                    fx.Confirm = L.T("Компонент «Підтримка SMB 1.0/CIFS» буде вимкнено (займає до хвилини). Дуже старі мережеві диски й приставки, що вміють лише SMBv1, перестануть відкриватися. Потрібне перезавантаження.");
                    fx.Done = L.T("SMBv1 вимкнено - перезавантажте комп'ютер.");
                    f.Fixes.Add(fx);
                }
                f.Ok = f.Score == 0;
                if (f.Ok) f.Why.Add(L.T("Вимкнено."));
            }
            // віддалений робочий стіл
            {
                Finding f = Check(r, L.T("Віддалений робочий стіл (RDP)"));
                if (IntVal(lm, @"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections", 1) == 0)
                {
                    if (IntVal(lm, @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", "UserAuthentication", 1) == 0)
                    {
                        f.Add(4, L.T("RDP увімкнено без перевірки на рівні мережі (NLA) - відкрито для підбору паролів і старих вразливостей."));
                        Fix nla = new Fix().Reg(true, @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", "UserAuthentication", "dword", "1");
                        nla.Button = L.T("Увімкнути NLA"); nla.Confirm = L.T("Віддалене підключення лишиться, але вимагатиме входу ще до появи робочого столу (NLA). Дуже старі клієнти RDP не підключаться."); nla.Done = L.T("NLA увімкнено.");
                        f.Fixes.Add(nla);
                    }
                    else f.Add(1, L.T("RDP увімкнено. Якщо ви не підключаєтеся до цього комп'ютера віддалено - краще вимкнути."));
                    Fix off = new Fix().Reg(true, @"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections", "dword", "1");
                    off.Button = L.T("Вимкнути RDP"); off.Confirm = L.T("Віддалений робочий стіл буде вимкнено: підключитися до цього комп'ютера по RDP стане неможливо."); off.Done = L.T("RDP вимкнено.");
                    f.Fixes.Add(off);
                }
                f.Ok = f.Score == 0;
                if (f.Ok) f.Why.Add(L.T("Вимкнено."));
            }
            // оновлення Windows
            {
                Finding f = Check(r, L.T("Оновлення Windows"));
                if (IntVal(lm, @"SYSTEM\CurrentControlSet\Services\wuauserv", "Start", 3) == 4)
                {
                    f.Add(3, L.T("Службу оновлень вимкнено - виправлення вразливостей не встановлюються."));
                    string sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
                    Fix fx = SecFix.Run(sc, "config wuauserv start= demand", sc, "config wuauserv start= disabled");
                    fx.Button = L.T("Увімкнути службу оновлень");
                    fx.Confirm = L.T("Службу «Windows Update» буде повернуто до стандартного запуску (вручну, на вимогу Windows). Оновлення знову зможуть встановлюватися.");
                    fx.Done = L.T("Службу оновлень увімкнено.");
                    f.Fixes.Add(fx);
                }
                try
                {
                    dynamic s = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session"));
                    dynamic sr = s.CreateUpdateSearcher();
                    int n = sr.GetTotalHistoryCount();
                    DateTime last = DateTime.MinValue;
                    if (n > 0)
                        foreach (dynamic e in sr.QueryHistory(0, Math.Min(n, 60)))
                        {
                            int op = e.Operation, rc = e.ResultCode;
                            DateTime d = e.Date;
                            if (op == 1 && (rc == 2 || rc == 3) && d > last) last = d;
                        }
                    if (last != DateTime.MinValue)
                    {
                        int days = (int)(DateTime.UtcNow - last).TotalDays;
                        f.Why.Add(L.T("Останнє встановлене оновлення: ") + last.ToLocalTime().ToString("dd.MM.yyyy") + ".");
                        if (days > 90) f.Add(3, L.T("Оновлення не встановлювалися понад 3 місяці."));
                        else if (days > 45) f.Add(1, L.T("Оновлення не встановлювалися понад півтора місяця."));
                    }
                }
                catch (Exception skipEx) { SbLog.Skip(skipEx); }
                if (f.Score > 0) f.Links.Add(new string[] { "Windows Update", "ms-settings:windowsupdate" });
                f.Ok = f.Score == 0;
            }
            // hosts
            try
            {
                string hosts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers\\etc\\hosts");
                Finding f = Check(r, L.T("Файл hosts"));
                if (File.Exists(hosts))
                {
                    // оновлення Windows і антивіруси - їх блокують шкідливі програми; інші домени Microsoft часто блокують від телеметрії
                    string[] sec = { "windowsupdate", "update.microsoft", "download.microsoft", "delivery.mp.microsoft", "defender", "wdcp.microsoft", "smartscreen",
                                     "kaspersky", "eset", "avast", "avg.", "bitdefender", "malwarebytes", "norton", "mcafee", "virustotal", "drweb", "sophos", "trendmicro", "f-secure" };
                    int redir = 0;
                    StringBuilder keep = new StringBuilder();
                    bool drop = false;
                    foreach (string line in File.ReadAllLines(hosts))
                    {
                        bool bad = false;
                        string l = line.Trim();
                        int hash = l.IndexOf('#');
                        if (hash >= 0) l = l.Substring(0, hash).Trim();
                        if (l.Length == 0) continue;
                        string[] p = l.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length < 2) continue;
                        bool block = p[0] == "0.0.0.0" || p[0] == "127.0.0.1" || p[0] == "::1" || p[0] == "::";
                        for (int i = 1; i < p.Length; i++)
                        {
                            string host = p[i].ToLowerInvariant();
                            if (host == "localhost" || host.EndsWith(".localhost")) continue;
                            bool isSec = false;
                            foreach (string s in sec) if (host.Contains(s)) isSec = true;
                            bool ms = host.Contains("microsoft") || host.Contains("windows.com") || host.Contains("live.com");
                            if (isSec || (ms && !block)) { bad = true; f.Add(4, (block ? L.T("Заблоковано ") : L.T("Перенаправлено ")) + host + (block ? "" : " → " + p[0]) + L.T(" - так шкідливі програми не дають оновитися Windows чи антивірусу або підміняють сайт.")); }
                            else if (ms) f.Add(0, L.T("Заблоковано ") + host + L.T(" (схоже на вимкнення телеметрії - не загроза)."));
                            else if (!block) { redir++; if (redir <= 5) f.Add(1, L.T("Перенаправлення: ") + host + " → " + p[0]); }
                        }
                        if (bad) drop = true;
                        else keep.Append(line).Append("\r\n");
                    }
                    if (redir > 5) f.Why.Add(L.T("…і ще перенаправлень: ") + (redir - 5));
                    if (drop)
                    {
                        Fix fx = new Fix();
                        fx.Admin = true;
                        fx.Ops.Add(new FixOp("FILE", hosts, Convert.ToBase64String(Encoding.UTF8.GetBytes(keep.ToString()))));
                        fx.Button = L.T("Прибрати ці записи");
                        fx.Confirm = L.T("З файла hosts буде прибрано рядки, що блокують чи підміняють сайти оновлень Windows і антивірусів. Решта записів (ваші власні, блокування реклами чи телеметрії) лишиться.");
                        fx.Done = L.T("Записи прибрано з hosts.");
                        f.Fixes.Add(fx);
                    }
                }
                f.Ok = f.Score == 0;
                if (f.Ok && f.Why.Count == 0) f.Why.Add(L.T("Без підозрілих записів."));
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
            // проксі
            {
                Finding f = Check(r, L.T("Проксі для браузерів"));
                const string inet = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
                string pac = Val(cu, inet, "AutoConfigURL") as string;
                if (IntVal(cu, inet, "ProxyEnable", 0) != 0) f.Add(2, L.T("Увімкнено проксі ") + (Val(cu, inet, "ProxyServer") as string ?? "") + L.T(". Якщо ви його не налаштовували - увесь трафік може йти через чужий сервер."));
                if (!string.IsNullOrEmpty(pac)) f.Add(2, L.T("Задано скрипт налаштування проксі: ") + pac + L.T(". Якщо це не робоча мережа - варто перевірити."));
                if (f.Score > 0)
                {
                    Fix fx = new Fix().Reg(false, inet, "ProxyEnable", "dword", "0");
                    if (!string.IsNullOrEmpty(pac)) fx.Reg(false, inet, "AutoConfigURL", "del", "");
                    fx.Button = L.T("Вимкнути проксі");
                    fx.Confirm = L.T("Браузери з'єднуватимуться з інтернетом напряму. Якщо проксі потрібен у робочій мережі чи для VPN - не вимикайте (або скасуйте потім).");
                    fx.Done = L.T("Проксі вимкнено.");
                    f.Fixes.Add(fx);
                    f.Links.Add(new string[] { L.T("Налаштування проксі"), "ms-settings:network-proxy" });
                }
                f.Ok = f.Score == 0;
                if (f.Ok) f.Why.Add(L.T("Проксі не задано."));
            }
            // політика PowerShell
            {
                Finding f = Check(r, L.T("Політика запуску скриптів PowerShell"));
                foreach (RegistryKey root in new RegistryKey[] { lm, cu })
                {
                    string ep = Val(root, @"SOFTWARE\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell", "ExecutionPolicy") as string;
                    if (ep != null && (ep.Equals("Unrestricted", StringComparison.OrdinalIgnoreCase) || ep.Equals("Bypass", StringComparison.OrdinalIgnoreCase)))
                    {
                        f.Add(1, L.T("Дозволено запуск будь-яких скриптів (") + ep + (root == lm ? L.T(", для всіх") : L.T(", для вас")) + ").");
                        Fix fx = new Fix().Reg(root == lm, @"SOFTWARE\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell", "ExecutionPolicy", "sz", "RemoteSigned");
                        fx.Button = root == lm ? L.T("RemoteSigned для всіх") : L.T("RemoteSigned для вас");
                        fx.Confirm = L.T("Політика стане RemoteSigned: ваші власні скрипти запускатимуться, а завантажені з інтернету - лише підписані.");
                        fx.Done = L.T("Політику PowerShell змінено на RemoteSigned.");
                        f.Fixes.Add(fx);
                    }
                }
                f.Ok = f.Score == 0;
                if (f.Ok) f.Why.Add(L.T("Стандартна."));
            }
            // безпечне завантаження
            {
                object sb = Val(lm, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
                Finding f = Check(r, L.T("Безпечне завантаження (Secure Boot)"));
                if (sb is int && (int)sb == 0) f.Add(1, L.T("Вимкнено в UEFI: шкідливі завантажувачі (буткіти) не блокуються. Деякі античіти (Valorant, Battlefield) теж його вимагають."));
                else if (sb == null) f.Why.Add(L.T("Стан невідомий (старий BIOS без UEFI?)."));
                f.Ok = f.Score == 0;
                if (f.Ok && f.Why.Count == 0) f.Why.Add(L.T("Увімкнено."));
            }
            // відкриті порти
            if (procs)
            {
                Finding f = Check(r, L.T("Порти, відкриті для мережі"));
                f.Ok = true;
                if (listen.Count == 0) f.Why.Add(L.T("Немає."));
                foreach (string l in listen) f.Why.Add(l);
                f.Detail = L.T("Довідково: які програми чекають вхідних підключень. Брандмауер вирішує, чи пускати до них ззовні.");
            }
            if (!Elevated.IsAdmin)
            {
                Finding f = Check(r, L.T("Права перевірки"));
                f.Ok = true;
                f.Why.Add(L.T("NeoBar запущено без прав адміністратора: частину процесів і налаштувань (винятки Defender, WMI) не видно."));
            }
        }
        return r;
    }

    // ---------- дії ----------
    public static string Kill(Finding f)
    {
        int ok = 0;
        string err = "";
        foreach (int pid in f.Pids)
        {
            try { using (Process p = Process.GetProcessById(pid)) { p.Kill(); ok++; } }
            catch (ArgumentException) { ok++; }        // уже завершився
            catch (Exception ex) { err = ex is System.ComponentModel.Win32Exception ? L.T("Недостатньо прав (процес запущено від адміністратора чи системи).") : ex.Message; }
        }
        return ok == f.Pids.Count ? "" : err;
    }
}
