using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// ======================================================================
//  Аналіз навантаження одного процесу протягом часу (Монітор системи → «Аналіз»).
//  ProcProbe - легкий замір одного процесу (час ЦП, пам'ять, введення-виведення, дескриптори, об'єкти GDI/USER),
//  без обходу всіх процесів. ProcRecorder - запис замірів з вибраним інтервалом у пам'ять і повний журнал
//  CSV у вибране місце (кожен замір дописується одразу - запис не губиться, навіть якщо NeoBar закриють),
//  підсумок (мін, макс, середнє, 95-й процентиль) - поруч у .txt. Запис працює й тоді, коли вікно монітора закрите.
// ======================================================================
sealed class ProcSample
{
    public DateTime T;          // місцевий час заміру
    public double Sec;          // секунд від початку запису
    public double[] V = new double[ProcRecorder.Count];
}

static class ProcProbe
{
    [StructLayout(LayoutKind.Sequential)]
    struct MEMCOUNTERS
    {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }
    [StructLayout(LayoutKind.Sequential)] struct IOCOUNTERS { public ulong ReadOps, WriteOps, OtherOps, Read, Write, Other; }

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")] static extern bool GetProcessMemoryInfo(IntPtr h, ref MEMCOUNTERS c, int size);
    [DllImport("kernel32.dll")] static extern bool GetProcessIoCounters(IntPtr h, out IOCOUNTERS c);
    [DllImport("kernel32.dll")] static extern bool GetProcessHandleCount(IntPtr h, out int n);
    [DllImport("user32.dll")] static extern int GetGuiResources(IntPtr h, int kind);

    public sealed class State
    {
        public PdhQuery Gpu;            // лічильники відеокарти цього процесу (Windows 10 1709+); немає - показники 0
        public string GpuPrefix = "";
        public IntPtr H;
        public long Cpu, Rd, Wr;
        public DateTime At;
        public bool First = true;
    }

    // відкрити процес на весь час запису: дескриптор не дає PID перейти до іншого процесу
    public static State Open(int pid)
    {
        IntPtr h = Win.OpenProcess(0x1000 | 0x00100000, false, (uint)pid);      // QUERY_LIMITED_INFORMATION | SYNCHRONIZE
        if (h == IntPtr.Zero) return null;
        State s = new State();
        s.H = h;
        try
        {
            // спершу лише екземпляри цього процесу (pid_N_*); не прийнялось - усі, а потрібні відберемо за назвою
            PdhQuery q = new PdhQuery();
            bool a = q.Ok && (q.Add("eng", "\\GPU Engine(pid_" + pid + "_*)\\Utilization Percentage") || q.Add("eng", "\\GPU Engine(*)\\Utilization Percentage"));
            bool b = q.Ok && (q.Add("mem", "\\GPU Process Memory(pid_" + pid + "_*)\\Dedicated Usage") || q.Add("mem", "\\GPU Process Memory(*)\\Dedicated Usage"));
            if (a || b) { q.Collect(); s.Gpu = q; s.GpuPrefix = "pid_" + pid + "_"; } else q.Dispose();
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return s;
    }

    public static void Close(State s)
    {
        if (s == null) return;
        if (s.Gpu != null) { s.Gpu.Dispose(); s.Gpu = null; }
        if (s.H != IntPtr.Zero) { Win.CloseHandle(s.H); s.H = IntPtr.Zero; }
    }

    public static bool Alive(State s) { return s != null && s.H != IntPtr.Zero && Win.WaitForSingleObject(s.H, 0) != 0; }

    // один замір; перший - лише запам'ятовує лічильники (швидкості рахуються від попереднього)
    public static bool Take(State s, ProcSample o)
    {
        long cr, ex, k, u;
        if (!GetProcessTimes(s.H, out cr, out ex, out k, out u)) return false;
        DateTime now = DateTime.UtcNow;
        IOCOUNTERS io;
        long rd = 0, wr = 0;
        if (GetProcessIoCounters(s.H, out io)) { rd = (long)io.Read; wr = (long)io.Write; }
        double dt = s.First ? 0 : (now - s.At).TotalSeconds;
        long cpu = k + u;
        o.V[0] = dt > 0 ? Math.Max(0, Math.Min(100, (cpu - s.Cpu) / 1e7 / dt / Environment.ProcessorCount * 100.0)) : 0;
        MEMCOUNTERS m = new MEMCOUNTERS();
        m.cb = (uint)Marshal.SizeOf(typeof(MEMCOUNTERS));
        if (GetProcessMemoryInfo(s.H, ref m, (int)m.cb)) { o.V[1] = (double)m.WorkingSetSize.ToUInt64(); o.V[2] = (double)m.PrivateUsage.ToUInt64(); }
        o.V[3] = dt > 0 ? Math.Max(0, (rd - s.Rd) / dt) : 0;
        o.V[4] = dt > 0 ? Math.Max(0, (wr - s.Wr) / dt) : 0;
        int hc;
        o.V[5] = GetProcessHandleCount(s.H, out hc) ? hc : 0;
        o.V[6] = GetGuiResources(s.H, 0);
        o.V[7] = GetGuiResources(s.H, 1);
        if (s.Gpu != null)
        {
            // відеокарта: найзавантаженіший рушій процесу (3D, відеодекодер, копіювання…) - як у Диспетчері завдань;
            // відеопам'ять - сума виділеної на всіх адаптерах
            // рушії одного типу на одній відеокарті (наприклад, кілька 3D) складаються, з типів береться найзайнятіший
            s.Gpu.Collect();
            double top = 0, mem = 0;
            Dictionary<string, double> byType = new Dictionary<string, double>();
            foreach (KeyValuePair<string, double> kv in s.Gpu.Values("eng"))
            {
                if (!kv.Key.StartsWith(s.GpuPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                int li = kv.Key.IndexOf("_luid_", StringComparison.OrdinalIgnoreCase), ti = kv.Key.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
                int pi = kv.Key.IndexOf("_phys_", StringComparison.OrdinalIgnoreCase);
                string type = (li >= 0 && pi > li ? kv.Key.Substring(li, pi - li) : "") + (ti >= 0 ? kv.Key.Substring(ti) : kv.Key);
                double cur;
                byType.TryGetValue(type, out cur);
                byType[type] = cur + kv.Value;
            }
            foreach (double v in byType.Values) if (v > top) top = v;
            foreach (KeyValuePair<string, double> kv in s.Gpu.Values("mem"))
                if (kv.Key.StartsWith(s.GpuPrefix, StringComparison.OrdinalIgnoreCase)) mem += kv.Value;
            o.V[8] = Math.Max(0, Math.Min(100, top));
            o.V[9] = Math.Max(0, mem);
        }
        s.Cpu = cpu; s.Rd = rd; s.Wr = wr; s.At = now;
        bool first = s.First;
        s.First = false;
        return !first;
    }
}

static class ProcRecorder
{
    // показники (номер = місце у файлі журналу): 0 ЦП %, 1 оперативна пам'ять, 2 виділена пам'ять (байти), 3 читання,
    // 4 запис (байт/с), 5 дескриптори, 6 GDI, 7 USER, 8 відеокарта %, 9 відеопам'ять (байти)
    public const int Count = 10;
    // порядок показу: процесор, відеокарта і її пам'ять, оперативна пам'ять (зайнята й виділена), диск/мережа, об'єкти Windows
    public static readonly int[] Order = { 0, 8, 9, 1, 2, 3, 4, 5, 6, 7 };
    public const int TechFrom = 7;          // з цього місця в Order - технічні показники (дескриптори, GDI, USER)

    public static string Name(int i)
    {
        switch (i)
        {
            case 0: return L.T("Процесор");
            case 8: return L.T("Відеокарта");
            case 1: return L.T("Оперативна пам'ять");
            case 2: return L.T("Виділена пам'ять");
            case 9: return L.T("Відеопам'ять");
            case 3: return L.T("Читання даних");
            case 4: return L.T("Запис даних");
            case 5: return L.T("Дескриптори");
            case 6: return L.T("Об'єкти GDI");
            default: return L.T("Об'єкти USER");
        }
    }
    public static string Short(int i)
    {
        switch (i)
        {
            case 0: return L.T("ЦП");
            case 8: return L.T("ГП");
            case 1: return L.T("ОЗП");
            case 2: return L.T("Виділена");
            case 9: return L.T("Відеопам'ять");
            case 3: return L.T("Читання");
            case 4: return L.T("Запис");
            case 5: return L.T("Дескриптори");
            case 6: return "GDI";
            default: return "USER";
        }
    }
    // що означає показник - простими словами
    public static string Hint(int i)
    {
        switch (i)
        {
            case 0: return L.T("Яку частку всього процесора (усіх ядер) займає процес");
            case 8: return L.T("Наскільки процес навантажує відеокарту: найзайнятіша її частина (3D, декодування відео тощо), як у Диспетчері завдань");
            case 1: return L.T("Скільки оперативної пам'яті процес займає просто зараз. Windows може забрати частину, коли пам'яті бракує, тож число коливається");
            case 2: return L.T("Скільки пам'яті процес замовив лише для себе - разом із тим, що Windows винесла у файл підкачки. Якщо весь час росте й не спадає - у програмі витік пам'яті");
            case 9: return L.T("Скільки пам'яті відеокарти займає процес (текстури, кадри відео)");
            case 3: return L.T("Скільки даних за секунду процес читає: файли на дисках, мережа, пристрої");
            case 4: return L.T("Скільки даних за секунду процес записує: файли на дисках, мережа, пристрої");
            case 5: return L.T("Відкриті файли, ключі реєстру, потоки й інші об'єкти Windows. Якщо весь час росте - програма їх не закриває (витік)");
            case 6: return L.T("Об'єкти малювання: шрифти, пера, зображення. Windows дозволяє до 10 000 на процес - далі програма перестає малюватись");
            default: return L.T("Вікна, меню, курсори. Windows дозволяє до 10 000 на процес");
        }
    }

    public static string Fmt(int i, double v)
    {
        if (double.IsNaN(v)) return "—";
        switch (i)
        {
            case 0: case 8: return v.ToString(v < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " %";
            case 1: case 2: case 9: return Cleaner.Size((long)v);
            case 3: case 4: return Cleaner.Size((long)v) + L.T("/с");
            default: return ((long)Math.Round(v)).ToString("N0", CultureInfo.CurrentCulture);
        }
    }

    static readonly object gate = new object();
    static readonly List<ProcSample> samples = new List<ProcSample>();
    static ProcProbe.State probe;
    static Timer timer;
    static StreamWriter log;
    static DateTime started;
    const int MaxSamples = 200000;

    public static bool Recording { get; private set; }
    public static int Pid { get; private set; }
    public static string ProcName = "", ProcPath = "", LogPath = "", Note = "";
    public static int IntervalMs = 1000;
    public static bool FromFile;            // показується запис, відкритий з файлу
    public static event Action Changed;

    public static List<ProcSample> Snapshot()
    {
        lock (gate) return new List<ProcSample>(samples);
    }

    public static int Samples { get { lock (gate) return samples.Count; } }

    static void Fire() { Action a = Changed; if (a != null) try { a(); } catch (Exception skipEx) { SbLog.Skip(skipEx); } }

    public static string LogDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NeoBar", L.T("Аналіз")); } }

    public static string DefaultLog(string name)
    {
        string dir = LogDir;
        string n = Path.GetFileNameWithoutExtension(name ?? "process");
        foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return Path.Combine(dir, n + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + ".csv");
    }

    static string Sep { get { string s = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator; return s == "," ? ";" : ","; } }

    // "" - запис почався, інакше - причина
    public static string Start(int pid, string name, string path, int intervalMs, string logPath)
    {
        Stop(null);
        ProcProbe.State s = ProcProbe.Open(pid);
        if (s == null) return L.T("Процес не відкривається: він уже завершився або захищений.");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            log = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(true));
            string sp = Sep;
            log.WriteLine("sep=" + sp);
            log.WriteLine(string.Join(sp, new string[] { L.T("Час"), L.T("Секунд від початку"), L.T("ЦП, %"), L.T("Оперативна пам'ять, МБ"), L.T("Виділена пам'ять, МБ"),
                L.T("Читання, КБ/с"), L.T("Запис, КБ/с"), L.T("Дескриптори"), "GDI", "USER", L.T("Відеокарта, %"), L.T("Відеопам'ять, МБ") }));
            log.Flush();
        }
        catch (Exception ex) { ProcProbe.Close(s); log = null; return L.F("Не вдалося створити журнал: {0}", ex.Message); }
        lock (gate) samples.Clear();
        probe = s;
        Pid = pid; ProcName = name ?? ""; ProcPath = path ?? ""; LogPath = logPath; Note = "";
        IntervalMs = Math.Max(200, intervalMs);
        FromFile = false;
        started = DateTime.UtcNow;
        Recording = true;
        ProcProbe.Take(probe, new ProcSample());          // відлік для швидкостей
        timer = new Timer(Tick, null, IntervalMs, IntervalMs);
        SbLog.W(SbLog.Panel, "аналіз процесу: запис " + name + " (" + pid + ") кожні " + IntervalMs + " мс у " + logPath);
        PruneSoon(Path.GetDirectoryName(logPath), LogDir);
        Fire();
        return "";
    }

    static int ticking;

    static void Tick(object o)
    {
        if (Interlocked.Exchange(ref ticking, 1) == 1) return;
        try
        {
            if (!Recording || probe == null) return;
            if (!ProcProbe.Alive(probe)) { Stop(L.T("Процес завершився.")); return; }
            ProcSample s = new ProcSample();
            if (!ProcProbe.Take(probe, s)) return;
            s.T = DateTime.Now;
            s.Sec = (DateTime.UtcNow - started).TotalSeconds;
            int n;
            lock (gate) { samples.Add(s); n = samples.Count; }
            WriteLine(s);
            if (n >= MaxSamples) { Stop(L.T("Досягнуто найбільшої кількості замірів.")); return; }
            Fire();
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
        finally { Interlocked.Exchange(ref ticking, 0); }
    }

    static void WriteLine(ProcSample s)
    {
        StreamWriter w = log;
        if (w == null) return;
        CultureInfo c = CultureInfo.CurrentCulture;
        string sp = Sep;
        StringBuilder b = new StringBuilder();
        b.Append(s.T.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(sp);
        b.Append(s.Sec.ToString("0.###", c)).Append(sp);
        b.Append(s.V[0].ToString("0.##", c)).Append(sp);
        b.Append((s.V[1] / 1048576.0).ToString("0.##", c)).Append(sp);
        b.Append((s.V[2] / 1048576.0).ToString("0.##", c)).Append(sp);
        b.Append((s.V[3] / 1024.0).ToString("0.##", c)).Append(sp);
        b.Append((s.V[4] / 1024.0).ToString("0.##", c)).Append(sp);
        b.Append(((long)s.V[5]).ToString(c)).Append(sp);
        b.Append(((long)s.V[6]).ToString(c)).Append(sp);
        b.Append(((long)s.V[7]).ToString(c)).Append(sp);
        b.Append(s.V[8].ToString("0.##", c)).Append(sp);
        b.Append((s.V[9] / 1048576.0).ToString("0.##", c));
        lock (gate)
        {
            try { w.WriteLine(b.ToString()); w.Flush(); }
            catch (Exception ex) { Util.ErrorLog(ex); }
        }
    }

    public static void Stop(string why)
    {
        Timer t = timer;
        timer = null;
        if (t != null) t.Dispose();
        bool was = Recording;
        Recording = false;
        ProcProbe.Close(probe);
        probe = null;
        StreamWriter w = log;
        log = null;
        if (w != null)
        {
            lock (gate) { try { w.Dispose(); } catch (Exception skipEx) { SbLog.Skip(skipEx); } }
            WriteSummary();
        }
        if (why != null) Note = why;
        if (was) { SbLog.W(SbLog.Panel, "аналіз процесу: запис зупинено" + (why != null ? " (" + why + ")" : "")); Fire(); }
    }

    // прибрати показані результати з пам'яті (файли журналу й підсумку лишаються на диску)
    public static void Clear()
    {
        if (Recording) return;
        lock (gate) { samples.Clear(); samples.TrimExcess(); }
        FromFile = false;
        Pid = 0; ProcName = ""; ProcPath = ""; LogPath = ""; Note = "";
        Fire();
    }

    // ---------- підсумки ----------
    public sealed class Stat { public double Min = double.NaN, Max = double.NaN, Avg = double.NaN, P95 = double.NaN; public DateTime MaxAt; }

    public static Stat[] Stats(List<ProcSample> l)
    {
        Stat[] r = new Stat[Count];
        for (int i = 0; i < Count; i++)
        {
            Stat s = new Stat();
            r[i] = s;
            if (l.Count == 0) continue;
            double sum = 0, mn = double.MaxValue, mx = double.MinValue;
            double[] all = new double[l.Count];
            for (int j = 0; j < l.Count; j++)
            {
                double v = l[j].V[i];
                all[j] = v;
                sum += v;
                if (v < mn) mn = v;
                if (v > mx) { mx = v; s.MaxAt = l[j].T; }
            }
            Array.Sort(all);
            s.Min = mn; s.Max = mx; s.Avg = sum / l.Count;
            s.P95 = all[Math.Min(all.Length - 1, (int)Math.Ceiling(all.Length * 0.95) - 1)];
        }
        return r;
    }

    static void WriteSummary()
    {
        try
        {
            List<ProcSample> l = Snapshot();
            if (LogPath.Length == 0) return;
            Stat[] st = Stats(l);
            StringBuilder b = new StringBuilder();
            b.AppendLine(L.F("Аналіз процесу «{0}» (PID {1})", ProcName, Pid));
            if (ProcPath.Length > 0) b.AppendLine(ProcPath);
            if (l.Count > 0)
                b.AppendLine(L.F("Запис: {0} - {1}, замірів: {2}, інтервал: {3} мс", l[0].T.ToString("yyyy-MM-dd HH:mm:ss"), l[l.Count - 1].T.ToString("HH:mm:ss"), l.Count, IntervalMs));
            if (Note.Length > 0) b.AppendLine(Note);
            b.AppendLine();
            foreach (int i in Order)
                b.AppendLine(Name(i) + ": " + L.F("мін {0}, макс {1} (о {2}), середнє {3}, 95 % часу - не більше {4}",
                    Fmt(i, st[i].Min), Fmt(i, st[i].Max), st[i].MaxAt.ToString("HH:mm:ss"), Fmt(i, st[i].Avg), Fmt(i, st[i].P95)));
            File.WriteAllText(Path.ChangeExtension(LogPath, ".txt"), b.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    // ---------- відкрити збережений журнал (для перегляду в програвачі) ----------
    public static string Load(string file)
    {
        if (Recording) return L.T("Спершу зупиніть поточний запис.");
        List<ProcSample> l = new List<ProcSample>();
        try
        {
            string[] lines = File.ReadAllLines(file, Encoding.UTF8);
            string sp = ";";
            int i0 = 0;
            if (lines.Length > 0 && lines[0].StartsWith("sep=")) { sp = lines[0].Substring(4).Trim(); if (sp.Length == 0) sp = ";"; i0 = 1; }
            i0++;       // рядок заголовків
            for (int i = i0; i < lines.Length; i++)
            {
                string[] p = lines[i].Split(new string[] { sp }, StringSplitOptions.None);
                if (p.Length < 10) continue;
                ProcSample s = new ProcSample();
                if (!DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out s.T)) continue;
                s.Sec = Num(p[1]);
                s.V[0] = Num(p[2]);
                s.V[1] = Num(p[3]) * 1048576.0;
                s.V[2] = Num(p[4]) * 1048576.0;
                s.V[3] = Num(p[5]) * 1024.0;
                s.V[4] = Num(p[6]) * 1024.0;
                s.V[5] = Num(p[7]); s.V[6] = Num(p[8]); s.V[7] = Num(p[9]);
                if (p.Length >= 12) { s.V[8] = Num(p[10]); s.V[9] = Num(p[11]) * 1048576.0; }
                l.Add(s);
            }
        }
        catch (Exception ex) { return L.F("Не вдалося прочитати файл: {0}", ex.Message); }
        if (l.Count == 0) return L.T("У файлі немає замірів NeoBar.");
        lock (gate) { samples.Clear(); samples.AddRange(l); }
        FromFile = true;
        LogPath = file;
        ProcName = Path.GetFileNameWithoutExtension(file);
        ProcPath = ""; Pid = 0; Note = "";
        IntervalMs = l.Count > 1 ? (int)Math.Round((l[l.Count - 1].Sec - l[0].Sec) * 1000 / (l.Count - 1)) : 1000;
        Fire();
        return "";
    }

    // ---------- журнали в теці: перелік, видалення, прибирання старих ----------
    public static int LogsVer;                  // змінюється, коли журнали видалено (вікно перечитує перелік)

    // лише журнали NeoBar (за вмістом, не за назвою): перший рядок «sep=», другий - заголовки, дев'ятий і десятий - GDI і USER
    public static bool IsOurLog(string f)
    {
        try
        {
            using (StreamReader r = new StreamReader(f, Encoding.UTF8, true))
            {
                string a = r.ReadLine(), b = r.ReadLine();
                if (a == null || b == null || !a.StartsWith("sep=")) return false;
                string sp = a.Substring(4).Trim();
                if (sp.Length == 0) return false;
                string[] h = b.Split(new string[] { sp }, StringSplitOptions.None);
                return h.Length >= 10 && h[8] == "GDI" && h[9] == "USER";
            }
        }
        catch { return false; }
    }

    // журнали NeoBar у теці - від давніших до новіших
    public static List<string> Logs(string dir)
    {
        List<KeyValuePair<DateTime, string>> l = new List<KeyValuePair<DateTime, string>>();
        try
        {
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                foreach (string f in Directory.GetFiles(dir, "*.csv"))
                    if (IsOurLog(f)) l.Add(new KeyValuePair<DateTime, string>(File.GetLastWriteTimeUtc(f), f));
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        l.Sort(delegate (KeyValuePair<DateTime, string> a, KeyValuePair<DateTime, string> b)
        {
            int c = a.Key.CompareTo(b.Key);
            return c != 0 ? c : string.Compare(a.Value, b.Value, StringComparison.OrdinalIgnoreCase);
        });
        List<string> r = new List<string>();
        foreach (KeyValuePair<DateTime, string> p in l) r.Add(p.Value);
        return r;
    }

    public static bool Same(string a, string b) { return !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

    // журнал і його підсумок (.txt поруч): у Кошик або назавжди; показаний у вікні - прибирається
    public static bool DeleteLog(string csv, bool toBin)
    {
        if (Recording && Same(csv, LogPath)) return false;
        string txt = Path.ChangeExtension(csv, ".txt");
        bool ok;
        if (toBin) ok = WallLib.Remove(csv);
        else { try { File.Delete(csv); ok = true; } catch (Exception ex) { SbLog.Skip(ex); ok = false; } }
        if (ok && File.Exists(txt))
        {
            if (toBin) WallLib.Remove(txt);
            else { try { File.Delete(txt); } catch (Exception skipEx) { SbLog.Skip(skipEx); } }
        }
        if (ok) Interlocked.Increment(ref LogsVer);
        if (ok && Same(csv, LogPath)) Clear();
        return ok;
    }

    // прибрати старі журнали за налаштуванням (у фоні; поточний запис і показаний журнал не чіпаються)
    public static void PruneSoon(params string[] dirs)
    {
        char mode;
        int n;
        if (!LogKeep.Parse(LogKeep.Spec, out mode, out n)) return;
        ThreadPool.QueueUserWorkItem(delegate
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int gone = 0;
            foreach (string d in dirs)
            {
                if (string.IsNullOrEmpty(d) || !seen.Add(d)) continue;
                List<string> l = Logs(d);
                DateTime lim = DateTime.UtcNow.AddDays(-n);
                for (int i = 0; i < l.Count; i++)
                {
                    string f = l[i];
                    if (Same(f, LogPath)) continue;
                    bool old = mode == 'd' ? File.GetLastWriteTimeUtc(f) < lim : i < l.Count - n;
                    if (old && DeleteLog(f, false)) gone++;
                }
            }
            if (gone > 0) { SbLog.W(SbLog.Panel, "аналіз процесу: прибрано старих журналів - " + gone); Fire(); }
        });
    }

    static double Num(string s)
    {
        double v;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v)) return v;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
        return 0;
    }
}
