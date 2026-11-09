using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// ======================================================================
//  Монітор процесів (вкладка «Процеси» в «Моніторі системи»): знімок усіх процесів, навантаження, пам'ять,
//  дескриптори, мережа, хто керує процесом, служби. Аналіз підозрілої поведінки - у «Безпеці», тут лише показ.
//
//  Дані - одним системним викликом NtQuerySystemInformation (так робить і Диспетчер завдань): жодного
//  дескриптора процесу не відкривається. Шлях і командний рядок читаються один раз на процес із правом
//  PROCESS_QUERY_LIMITED_INFORMATION - пам'ять процесів не читається. Поки працює античіт, дескриптори
//  не відкриваються взагалі (шлях і підпис не показуються).
// ======================================================================
sealed class SvcInfo
{
    public string Name = "", Display = "";
    public int State;                       // 1 зупинено, 2 запускається, 3 зупиняється, 4 працює, 5-7 пауза
    public int Pid;
    public int Mode = -1;                   // тип запуску (ProcAct.StartMode), для списку служб
}

sealed class ProcRow
{
    public int Pid, Parent, Session;
    // вид процесу (як групи в Диспетчері завдань): програма з вікном, фоновий, стороння служба, процес Windows
    public const int KApp = 0, KBg = 1, KSvc = 2, KWin = 3;
    public int Kind = KBg;
    public bool HasWin;
    // хто керує процесом: служба, автозапуск, планувальник, користувач, інший процес, Windows
    public const int CSvc = 0, CAuto = 1, CTask = 2, CUser = 3, CProc = 4, CWin = 5, CNone = 6;
    public int Ctl = CNone;
    public string CtlText = "";
    public List<SvcInfo> Svcs = new List<SvcInfo>();          // служби, що працюють у цьому процесі
    public string Name = "", Path = "", Cmd = "";
    public long Created;                    // FILETIME (UTC)
    public float Cpu;                       // % від усього процесора
    public long Ws, Commit;                 // приватний робочий набір, виділена пам'ять
    public int Threads, Handles;
    public double Io;                       // байт/с (усі операції введення-виведення)
    public int NetEst, NetListen;           // з'єднання назовні, порти для вхідних
    internal bool PathTried;
    internal long LastCpu, LastIo;

    public DateTime Started { get { return Created > 0 ? DateTime.FromFileTimeUtc(Created).ToLocalTime() : DateTime.MinValue; } }
}

static class ProcMon
{
    public static List<ProcRow> Rows = new List<ProcRow>();
    public static bool Quiet;               // працює античіт: жодних дескрипторів процесів
    public static string QuietName = "";
    public static string Error = "";

    static readonly Dictionary<int, ProcRow> byPid = new Dictionary<int, ProcRow>();
    static DateTime lastAt = DateTime.MinValue;
    static int cycle;
    static readonly int cores = Math.Max(1, Environment.ProcessorCount);
    static IntPtr buf = IntPtr.Zero;
    static int bufLen;

    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr b, int len, out int ret);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder sb, ref int size);

    // ---------- цифровий підпис - лише для вибраного процесу, у фоні ----------
    public sealed class SigInfo
    {
        public volatile bool Done;
        public int Kind = Sig.Unknown;
        public string Signer = "";
    }
    static readonly Dictionary<string, SigInfo> sigs = new Dictionary<string, SigInfo>(StringComparer.OrdinalIgnoreCase);

    public static SigInfo SigOf(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        SigInfo s;
        lock (sigs)
        {
            if (sigs.TryGetValue(path, out s)) return s;
            if (sigs.Count > 400) sigs.Clear();
            s = new SigInfo();
            sigs[path] = s;
        }
        SigInfo ss = s;
        ThreadPool.QueueUserWorkItem(delegate
        {
            try { string sg = ""; ss.Kind = File.Exists(path) ? Sig.Check(path, out sg) : Sig.Unknown; ss.Signer = sg ?? ""; }     // з каталогами Windows
            catch { }
            ss.Done = true;
        });
        return s;
    }

    public static void Reset()
    {
        Rows = new List<ProcRow>();
        byPid.Clear();
        lastAt = DateTime.MinValue;
        cycle = 0;
        if (buf != IntPtr.Zero) { Marshal.FreeHGlobal(buf); buf = IntPtr.Zero; }
    }

    // ---------- один знімок ----------
    public static void Sample()
    {
        try
        {
            if (IntPtr.Size != 8) { Error = L.T("Вкладка «Процеси» працює лише в 64-розрядному NeoBar."); return; }
            int ret = 0;
            if (buf == IntPtr.Zero) { bufLen = 1 << 20; buf = Marshal.AllocHGlobal(bufLen); }
            int st = NtQuerySystemInformation(5, buf, bufLen, out ret);
            int tries = 0;
            while (st == unchecked((int)0xC0000004) && tries++ < 6)        // STATUS_INFO_LENGTH_MISMATCH
            {
                Marshal.FreeHGlobal(buf);
                bufLen = Math.Max(bufLen * 2, ret + (1 << 16));
                buf = Marshal.AllocHGlobal(bufLen);
                st = NtQuerySystemInformation(5, buf, bufLen, out ret);
            }
            if (st != 0) { Error = "NtQuerySystemInformation: 0x" + st.ToString("X"); return; }
            Error = "";

            DateTime now = DateTime.UtcNow;
            double elapsed100 = lastAt == DateTime.MinValue ? 0 : (now - lastAt).Ticks;
            double elapsedSec = elapsed100 / 1e7;
            lastAt = now;
            cycle++;

            HashSet<int> seen = new HashSet<int>();
            List<ProcRow> rows = new List<ProcRow>();
            int off = 0;
            bool acSeen = false;
            string acName = "";
            while (true)
            {
                IntPtr p = new IntPtr(buf.ToInt64() + off);
                int next = Marshal.ReadInt32(p, 0);
                long pid64 = Marshal.ReadInt64(p, 80);
                int pid = (int)pid64;
                if (pid != 0)
                {
                    int nl = (ushort)Marshal.ReadInt16(p, 56);
                    IntPtr nb = Marshal.ReadIntPtr(p, 64);
                    string name = nl > 0 && nb != IntPtr.Zero ? Marshal.PtrToStringUni(nb, nl / 2) : (pid == 4 ? "System" : "");
                    long create = Marshal.ReadInt64(p, 32), user = Marshal.ReadInt64(p, 40), kern = Marshal.ReadInt64(p, 48);
                    ProcRow r;
                    if (!byPid.TryGetValue(pid, out r) || r.Created != create || !string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        r = new ProcRow();
                        r.Pid = pid; r.Name = name; r.Created = create;
                        r.LastCpu = user + kern;
                        r.LastIo = Marshal.ReadInt64(p, 232) + Marshal.ReadInt64(p, 240) + Marshal.ReadInt64(p, 248);
                        byPid[pid] = r;
                    }
                    else
                    {
                        long cpu = user + kern;
                        long io = Marshal.ReadInt64(p, 232) + Marshal.ReadInt64(p, 240) + Marshal.ReadInt64(p, 248);
                        if (elapsed100 > 0)
                        {
                            r.Cpu = (float)Math.Max(0, Math.Min(100, (cpu - r.LastCpu) / (elapsed100 * cores) * 100.0));
                            r.Io = Math.Max(0, (io - r.LastIo) / elapsedSec);
                        }
                        r.LastCpu = cpu; r.LastIo = io;
                    }
                    r.Parent = (int)Marshal.ReadInt64(p, 88);
                    r.Threads = Marshal.ReadInt32(p, 4);
                    r.Handles = Marshal.ReadInt32(p, 96);
                    r.Session = Marshal.ReadInt32(p, 100);
                    r.Ws = Marshal.ReadInt64(p, 8);
                    r.Commit = Marshal.ReadInt64(p, 200);
                    seen.Add(pid);
                    rows.Add(r);
                    if (AntiCheat.IsService(name)) { acSeen = true; acName = name; }
                }
                if (next == 0) break;
                off += next;
            }
            Quiet = acSeen;
            QuietName = acName;

            // зниклі процеси
            List<int> gone = new List<int>();
            foreach (int k in byPid.Keys) if (!seen.Contains(k)) gone.Add(k);
            foreach (int k in gone) byPid.Remove(k);

            // шлях і командний рядок: раз на процес, порціями, і не поки працює античіт
            if (!Quiet)
            {
                int budget = 40;
                foreach (ProcRow r in rows)
                {
                    if (budget <= 0) break;
                    if (r.PathTried || r.Pid == 4) continue;
                    r.PathTried = true;
                    budget--;
                    Details(r);
                }
            }

            // мережа: раз на кілька знімків
            if (cycle % 4 == 1)
            {
                try
                {
                    Dictionary<uint, int[]> net = new Dictionary<uint, int[]>();
                    SecScan.Tcp(net, new List<KeyValuePair<int, uint>>());
                    foreach (ProcRow r in rows)
                    {
                        int[] v;
                        if (net.TryGetValue((uint)r.Pid, out v)) { r.NetListen = v[0]; r.NetEst = v[1]; }
                        else { r.NetListen = 0; r.NetEst = 0; }
                    }
                }
                catch { }
            }

            // процеси з видимими вікнами (лише список вікон - у процеси нічого не відкривається)
            HashSet<int> wins = Windows();
            foreach (ProcRow r in rows) r.HasWin = wins.Contains(r.Pid);
            // служби й записи автозапуску - не щоразу (раз на ~10 і ~60 с)
            if (svcAt == DateTime.MinValue || (now - svcAt).TotalSeconds >= 10) Services();
            if (runAt == DateTime.MinValue || (now - runAt).TotalSeconds >= 60) RunKeys();
            foreach (ProcRow r in rows)
            {
                List<SvcInfo> sl;
                r.Svcs = svcByPid.TryGetValue(r.Pid, out sl) ? sl : new List<SvcInfo>();
            }
            Classify(rows);
            Rows = rows;
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
    static HashSet<int> winSet;
    static readonly EnumProc winCb = WinCb;

    static bool WinCb(IntPtr h, IntPtr l)
    {
        if (!IsWindowVisible(h) || GetWindow(h, 4) != IntPtr.Zero || GetWindowTextLength(h) == 0) return true;      // власник - діалог чи спливне вікно
        if ((GetWindowLong(h, -20) & 0x80) != 0) return true;                                                        // WS_EX_TOOLWINDOW
        int cloaked;
        if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;                           // приховане (інший робочий стіл, UWP)
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        if (pid != 0) winSet.Add((int)pid);
        return true;
    }

    static HashSet<int> Windows()
    {
        winSet = new HashSet<int>();
        try { EnumWindows(winCb, IntPtr.Zero); } catch { }
        return winSet;
    }

    static readonly string WinDir = (Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\").ToLowerInvariant();
    static readonly string[] Kernel = { "system", "registry", "memory compression", "secure system", "system idle process", "idle" };

    // ---------- служби (лише список від диспетчера служб; у процеси нічого не відкривається) ----------
    [StructLayout(LayoutKind.Sequential)]
    struct ESSP { public IntPtr Name, Display; public int Type, State, Accepted, Exit, SExit, Check, Wait, Pid, Flags; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenSCManager(string machine, string db, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool EnumServicesStatusEx(IntPtr scm, int level, uint type, uint state, IntPtr buf, int size, out int need, out int count, ref int resume, string group);
    [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(IntPtr h);
    static Dictionary<int, List<SvcInfo>> svcByPid = new Dictionary<int, List<SvcInfo>>();
    static DateTime svcAt = DateTime.MinValue, runAt = DateTime.MinValue;

    public static void RefreshServicesSoon() { svcAt = DateTime.MinValue; }

    static void Services()
    {
        svcAt = DateTime.UtcNow;
        Dictionary<int, List<SvcInfo>> map = new Dictionary<int, List<SvcInfo>>();
        IntPtr scm = OpenSCManager(null, null, 0x0005);              // CONNECT | ENUMERATE_SERVICE
        if (scm == IntPtr.Zero) return;
        IntPtr b = IntPtr.Zero;
        try
        {
            int need, count, resume = 0;
            EnumServicesStatusEx(scm, 0, 0x30, 1, IntPtr.Zero, 0, out need, out count, ref resume, null);     // лише активні Win32-служби
            if (need <= 0 || need > 8 << 20) return;
            need += 4096;
            b = Marshal.AllocHGlobal(need);
            resume = 0;
            if (!EnumServicesStatusEx(scm, 0, 0x30, 1, b, need, out need, out count, ref resume, null)) return;
            int sz = Marshal.SizeOf(typeof(ESSP));
            for (int i = 0; i < count; i++)
            {
                ESSP e = (ESSP)Marshal.PtrToStructure(new IntPtr(b.ToInt64() + (long)i * sz), typeof(ESSP));
                if (e.Pid == 0) continue;
                SvcInfo s = new SvcInfo();
                s.Name = Marshal.PtrToStringUni(e.Name) ?? "";
                s.Display = Marshal.PtrToStringUni(e.Display) ?? s.Name;
                s.State = e.State;
                s.Pid = e.Pid;
                List<SvcInfo> l;
                if (!map.TryGetValue(e.Pid, out l)) { l = new List<SvcInfo>(); map[e.Pid] = l; }
                l.Add(s);
            }
            svcByPid = map;
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
        finally
        {
            if (b != IntPtr.Zero) Marshal.FreeHGlobal(b);
            CloseServiceHandle(scm);
        }
    }

    // команди автозапуску з реєстру (Run): шлях файла → де записано
    static Dictionary<string, string> runMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    static void RunKeys()
    {
        runAt = DateTime.UtcNow;
        Dictionary<string, string> m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[][] keys =
        {
            new string[] { "CU", @"Software\Microsoft\Windows\CurrentVersion\Run" },
            new string[] { "LM", @"Software\Microsoft\Windows\CurrentVersion\Run" },
            new string[] { "LM", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run" }
        };
        foreach (string[] k in keys)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey root = Microsoft.Win32.RegistryKey.OpenBaseKey(k[0] == "CU" ? Microsoft.Win32.RegistryHive.CurrentUser : Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                using (Microsoft.Win32.RegistryKey rk = root.OpenSubKey(k[1]))
                {
                    if (rk == null) continue;
                    foreach (string n in rk.GetValueNames())
                    {
                        string cmd = Convert.ToString(rk.GetValue(n));
                        string exe = ExeOf(Environment.ExpandEnvironmentVariables(cmd ?? ""));
                        if (exe.Length > 0 && !m.ContainsKey(exe)) m[exe] = n;
                    }
                }
            }
            catch { }
        }
        runMap = m;
    }

    // шлях програми з командного рядка
    public static string ExeOf(string cmd)
    {
        string c = (cmd ?? "").Trim();
        if (c.Length == 0) return "";
        if (c[0] == '"') { int q = c.IndexOf('"', 1); return q > 1 ? c.Substring(1, q - 1) : ""; }
        int e = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return e > 0 ? c.Substring(0, e + 4) : c.Split(' ')[0];
    }

    // аргументи (усе після шляху програми)
    public static string ArgsOf(string cmd, string path)
    {
        string a = (cmd ?? "").TrimStart();
        if (a.StartsWith("\"")) { int q = a.IndexOf('"', 1); a = q > 0 ? a.Substring(q + 1) : ""; }
        else if (path.Length > 0 && a.StartsWith(path, StringComparison.OrdinalIgnoreCase)) a = a.Substring(path.Length);
        else { int sp = a.IndexOf(' '); a = sp > 0 ? a.Substring(sp + 1) : ""; }
        return a.Trim();
    }

    static bool Hosts(ProcRow r, string svc)
    {
        foreach (SvcInfo s in r.Svcs) if (string.Equals(s.Name, svc, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static void Controller(ProcRow r, ProcRow par)
    {
        string nm = r.Name.ToLowerInvariant();
        string pn = par != null ? par.Name.ToLowerInvariant() : "";
        string run;
        if (r.Svcs.Count > 0)
        {
            r.Ctl = ProcRow.CSvc;
            r.CtlText = r.Svcs.Count == 1 ? L.F("служба «{0}»", r.Svcs[0].Display) : L.F("служби ({0}): {1}…", r.Svcs.Count, r.Svcs[0].Display);
        }
        else if (r.Pid <= 4 || Array.IndexOf(Kernel, nm) >= 0 || (par == null && r.Session == 0) || pn == "smss.exe" || pn == "wininit.exe" || pn == "winlogon.exe" || pn == "csrss.exe")
        {
            r.Ctl = ProcRow.CWin; r.CtlText = L.T("Windows");
        }
        else if (r.Path.Length > 0 && runMap.TryGetValue(r.Path, out run))
        {
            r.Ctl = ProcRow.CAuto; r.CtlText = L.F("автозапуск «{0}»", run);
        }
        else if (par != null && (pn == "taskhostw.exe" || pn == "taskeng.exe" || Hosts(par, "Schedule")))
        {
            r.Ctl = ProcRow.CTask; r.CtlText = L.T("планувальник завдань");
        }
        else if (pn == "services.exe")
        {
            r.Ctl = ProcRow.CSvc; r.CtlText = L.T("диспетчер служб");
        }
        else if (pn == "explorer.exe" || pn == "sihost.exe" || pn == "startmenuexperiencehost.exe")
        {
            r.Ctl = ProcRow.CUser; r.CtlText = L.T("користувач");
        }
        else if (par != null)
        {
            r.Ctl = par.Svcs.Count > 0 ? ProcRow.CSvc : ProcRow.CProc;
            r.CtlText = par.Name;
        }
        else { r.Ctl = ProcRow.CNone; r.CtlText = "—"; }
    }

    static int KindOf(ProcRow r, ProcRow par)
    {
        string nm = r.Name.ToLowerInvariant();
        if (r.Pid <= 4 || Array.IndexOf(Kernel, nm) >= 0) return ProcRow.KWin;
        if (r.HasWin && r.Session != 0) return ProcRow.KApp;
        bool inWin = r.Path.Length > 0 ? r.Path.ToLowerInvariant().StartsWith(WinDir) : false;
        if (inWin) return ProcRow.KWin;
        bool bySvc = par != null && string.Equals(par.Name, "services.exe", StringComparison.OrdinalIgnoreCase);
        if (r.Path.Length == 0 && r.Session == 0 && !bySvc) return ProcRow.KWin;          // захищений системний (шлях Windows не показує)
        if (bySvc || r.Session == 0) return nm == "svchost.exe" ? ProcRow.KWin : ProcRow.KSvc;
        return ProcRow.KBg;
    }

    static void Details(ProcRow r)
    {
        IntPtr h = OpenProcess(0x1000, false, (uint)r.Pid);          // PROCESS_QUERY_LIMITED_INFORMATION, як у Диспетчера завдань
        if (h == IntPtr.Zero) return;
        try
        {
            StringBuilder sb = new StringBuilder(1024);
            int n = sb.Capacity;
            if (QueryFullProcessImageNameW(h, 0, sb, ref n)) r.Path = sb.ToString();
            r.Cmd = CmdLine(h);
        }
        finally { CloseHandle(h); }
    }

    static string CmdLine(IntPtr h)
    {
        int need;
        NtQueryInformationProcess(h, 60, IntPtr.Zero, 0, out need);          // ProcessCommandLineInformation (Windows 8.1+)
        if (need <= 0 || need > 1 << 20) return "";
        IntPtr b = Marshal.AllocHGlobal(need);
        try
        {
            if (NtQueryInformationProcess(h, 60, b, need, out need) != 0) return "";
            int len = (ushort)Marshal.ReadInt16(b);
            IntPtr s = Marshal.ReadIntPtr(b, 8);
            return len > 0 && s != IntPtr.Zero ? Marshal.PtrToStringUni(s, len / 2) : "";
        }
        catch { return ""; }
        finally { Marshal.FreeHGlobal(b); }
    }

    // ---------- аномалії ----------
    // вид і хто керує
    static void Classify(List<ProcRow> rows)
    {
        Dictionary<int, ProcRow> by = new Dictionary<int, ProcRow>();
        foreach (ProcRow r in rows) by[r.Pid] = r;
        foreach (ProcRow r in rows)
        {
            ProcRow par;
            bool parOk = by.TryGetValue(r.Parent, out par) && par.Created <= r.Created && r.Parent != r.Pid;
            r.Kind = KindOf(r, parOk ? par : null);
            Controller(r, parOk ? par : null);
        }
    }

    // усі служби Windows (і зупинені) - для списку служб
    public static List<SvcInfo> AllServices()
    {
        List<SvcInfo> r = new List<SvcInfo>();
        IntPtr scm = OpenSCManager(null, null, 0x0005);
        if (scm == IntPtr.Zero) return r;
        IntPtr b = IntPtr.Zero;
        try
        {
            int need, count, resume = 0;
            EnumServicesStatusEx(scm, 0, 0x30, 3, IntPtr.Zero, 0, out need, out count, ref resume, null);
            if (need <= 0 || need > 8 << 20) return r;
            need += 8192;
            b = Marshal.AllocHGlobal(need);
            resume = 0;
            if (!EnumServicesStatusEx(scm, 0, 0x30, 3, b, need, out need, out count, ref resume, null)) return r;
            int sz = Marshal.SizeOf(typeof(ESSP));
            for (int i = 0; i < count; i++)
            {
                ESSP e = (ESSP)Marshal.PtrToStructure(new IntPtr(b.ToInt64() + (long)i * sz), typeof(ESSP));
                SvcInfo s = new SvcInfo();
                s.Name = Marshal.PtrToStringUni(e.Name) ?? "";
                s.Display = Marshal.PtrToStringUni(e.Display) ?? s.Name;
                s.State = e.State;
                s.Pid = e.Pid;
                s.Mode = ProcAct.StartMode(s.Name);
                r.Add(s);
            }
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
        finally
        {
            if (b != IntPtr.Zero) Marshal.FreeHGlobal(b);
            CloseServiceHandle(scm);
        }
        return r;
    }
}
