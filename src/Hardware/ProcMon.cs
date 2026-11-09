using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// ======================================================================
//  Монітор процесів (вкладка «Процеси» в «Моніторі системи»): знімок усіх процесів, навантаження, пам'ять,
//  дескриптори, мережа і позначки аномальної поведінки. Лише показ: нічого не змінює й не блокує.
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
    public int Level;                       // 0 - звичайний, 1 - зверніть увагу, 2 - підозріло
    public readonly List<string> Why = new List<string>();
    public string Signer = "";
    public int SigKind = Sig.Unknown, Place = SecHeur.POther;
    public bool SigDone, Exists = true, Fresh;
    internal bool PathTried;
    internal long LastCpu, LastIo;
    internal DateTime HighCpuSince = DateTime.MinValue, HighIoSince = DateTime.MinValue, LastHist = DateTime.MinValue;
    internal readonly List<KeyValuePair<DateTime, long>> CommitHist = new List<KeyValuePair<DateTime, long>>();

    public double AgeSec { get { return Created > 0 ? Math.Max(0, (DateTime.UtcNow - DateTime.FromFileTimeUtc(Created)).TotalSeconds) : 0; } }
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

    // ---------- статичний аналіз файлів (підпис, місце, ім'я) - у фоні, по одному ----------
    sealed class SInfo
    {
        public volatile bool Done;
        public int SigKind = Sig.Unknown, Score, Place = SecHeur.POther;
        public string Signer = "";
        public bool Exists = true;
        public readonly List<string> Why = new List<string>();
    }
    static readonly Dictionary<string, SInfo> stat = new Dictionary<string, SInfo>(StringComparer.OrdinalIgnoreCase);
    static readonly Queue<string> queue = new Queue<string>();
    static Thread worker;

    static SInfo Static(string path)
    {
        SInfo s;
        lock (stat)
        {
            if (stat.TryGetValue(path, out s)) return s;
            if (stat.Count > 1500) return null;
            s = new SInfo();
            stat[path] = s;
            queue.Enqueue(path);
            if (worker == null || !worker.IsAlive)
            {
                worker = new Thread(Work);
                worker.IsBackground = true;
                worker.Priority = ThreadPriority.BelowNormal;
                worker.Start();
            }
        }
        return s;
    }

    static void Work()
    {
        while (true)
        {
            string p;
            lock (stat) { if (queue.Count == 0) { worker = null; return; } p = queue.Dequeue(); }
            SInfo s;
            lock (stat) s = stat[p];
            try
            {
                Finding f = SecScan.AnalyzeFile(p);
                s.SigKind = f.SigKind; s.Signer = f.Signer; s.Place = f.Place; s.Score = f.Score;
                s.Exists = File.Exists(p);
                foreach (string w in f.Why) s.Why.Add(w);
            }
            catch { }
            s.Done = true;
        }
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
            Evaluate(rows, now);
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
    static void Flag(ProcRow r, int lev, string why)
    {
        if (lev > r.Level) r.Level = lev;
        if (!r.Why.Contains(why)) r.Why.Add(why);
    }

    static readonly string[] Office = { "winword.exe", "excel.exe", "powerpnt.exe", "outlook.exe", "onenote.exe", "mspub.exe", "msaccess.exe", "visio.exe", "acrord32.exe", "acrobat.exe", "foxitreader.exe", "wordpad.exe" };
    static readonly string[] Shells = { "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe", "regsvr32.exe", "certutil.exe", "bitsadmin.exe", "wmic.exe" };
    static readonly string[] Single = { "lsass.exe", "services.exe", "wininit.exe" };         // у системі лише по одному
    static readonly Dictionary<string, string> Parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "svchost.exe", "services.exe" }, { "services.exe", "wininit.exe" }, { "lsass.exe", "wininit.exe" },
        { "taskhostw.exe", "svchost.exe" }, { "runtimebroker.exe", "svchost.exe" }, { "sihost.exe", "svchost.exe" }
    };

    static void Evaluate(List<ProcRow> rows, DateTime now)
    {
        Dictionary<int, ProcRow> by = new Dictionary<int, ProcRow>();
        Dictionary<string, int> cnt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (ProcRow r in rows)
        {
            by[r.Pid] = r;
            int c; cnt.TryGetValue(r.Name, out c); cnt[r.Name] = c + 1;
        }
        foreach (ProcRow r in rows)
        {
            r.Level = 0;
            r.Why.Clear();
            r.Fresh = r.AgeSec < 120;

            // файл: підпис, місце, ім'я, пакувальник (аналіз у фоні)
            if (r.Path.Length > 0)
            {
                SInfo s = Static(r.Path);
                if (s != null && s.Done)
                {
                    r.SigKind = s.SigKind; r.Signer = s.Signer; r.Place = s.Place; r.SigDone = true; r.Exists = s.Exists;
                    if (s.Score >= 3) foreach (string w in s.Why) Flag(r, s.Score >= 5 ? 2 : 1, w);
                }
            }

            string nm = r.Name.ToLowerInvariant();
            ProcRow par;
            bool parOk = by.TryGetValue(r.Parent, out par) && par.Created <= r.Created && r.Parent != r.Pid;
            r.Kind = KindOf(r, parOk ? par : null);
            Controller(r, parOk ? par : null);

            // системний процес запущено не тим, ким треба
            string want;
            if (parOk && Parents.TryGetValue(nm, out want) && !string.Equals(par.Name, want, StringComparison.OrdinalIgnoreCase) && (r.Path.Length == 0 || SecHeur.InSys32(r.Path)))
                Flag(r, 2, L.F("«{0}» зазвичай запускає {1}, а тут - «{2}» (PID {3}).", r.Name, want, par.Name, par.Pid));
            if (Array.IndexOf(Single, nm) >= 0 && cnt[r.Name] > 1)
                Flag(r, 2, L.F("Другий екземпляр «{0}»: у Windows він один - підроблені копії часто видають себе за нього.", r.Name));
            if (parOk && Array.IndexOf(Office, par.Name.ToLowerInvariant()) >= 0 && Array.IndexOf(Shells, nm) >= 0)
                Flag(r, 2, L.F("Запущено з «{0}»: документ чи PDF запускає командний рядок або скрипт - типова ознака шкідливого макросу.", par.Name));

            // поведінка в часі
            if (r.Cpu >= 60) { if (r.HighCpuSince == DateTime.MinValue) r.HighCpuSince = now; }
            else r.HighCpuSince = DateTime.MinValue;
            if (r.HighCpuSince != DateTime.MinValue && (now - r.HighCpuSince).TotalSeconds >= 120)
                Flag(r, 1, L.F("Понад дві хвилини навантажує процесор ({0}%).", Math.Round(r.Cpu)));

            if (r.Io >= 30 * 1048576.0) { if (r.HighIoSince == DateTime.MinValue) r.HighIoSince = now; }
            else r.HighIoSince = DateTime.MinValue;
            if (r.HighIoSince != DateTime.MinValue && (now - r.HighIoSince).TotalSeconds >= 60)
                Flag(r, 1, L.T("Хвилину й довше інтенсивно читає чи пише (понад 30 МБ/с)."));

            if ((now - r.LastHist).TotalSeconds >= 15)
            {
                r.LastHist = now;
                r.CommitHist.Add(new KeyValuePair<DateTime, long>(now, r.Commit));
                if (r.CommitHist.Count > 40) r.CommitHist.RemoveAt(0);
            }
            if (r.CommitHist.Count > 0)
            {
                KeyValuePair<DateTime, long> old = r.CommitHist[0];
                double mins = (now - old.Key).TotalMinutes;
                long grow = r.Commit - old.Value;
                if (mins >= 4 && grow > 300L * 1048576 && r.Commit > old.Value * 1.5)
                    Flag(r, 1, L.F("Пам'ять швидко росте: +{0} МБ за {1} хв (можливий витік).", grow / 1048576, Math.Round(mins)));
            }
            if (r.Handles >= 10000) Flag(r, 1, L.F("Дуже багато відкритих дескрипторів ({0}) - так виглядає витік ресурсів.", r.Handles));

            // мережа від непідписаного файла з теки користувача
            bool shady = r.SigDone && r.SigKind != Sig.Ok && r.Place == SecHeur.PUser;
            if (shady && r.NetListen > 0) Flag(r, 2, L.T("Не підписаний файл з теки користувача відкрив порт для вхідних підключень."));
            else if (shady && r.NetEst > 0) Flag(r, 1, L.T("Не підписаний файл з теки користувача має з'єднання з мережею."));
        }
    }
}
