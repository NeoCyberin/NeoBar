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
sealed class ProcRow
{
    public int Pid, Parent;
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

            Evaluate(rows, now);
            Rows = rows;
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
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
