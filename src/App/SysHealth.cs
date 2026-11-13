using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// ======================================================================
//  Самоконтроль і «що сталося з комп'ютером»:
//   SelfUse   - скільки пам'яті, дескрипторів, об'єктів GDI/USER займає процес (NeoBar чи рушій шпалер);
//   UiWatch   - сторож: помічає, що інтерфейс NeoBar не відповідав або що стояла вся система, і записує це
//               в журнал помилок (коли саме й скільки тривало); раз на годину - ресурси в журнал панелі;
//   SysEvents - події Windows за тиждень: перезапуски відеодрайвера, нестача пам'яті, раптові вимкнення,
//               збої NeoBar і рушія шпалер (для «Перевірки системи»).
//  Усе - у фонових потоках; у потоці інтерфейсу нічого не чекає.
// ======================================================================
static class SelfUse
{
    [StructLayout(LayoutKind.Sequential)]
    struct MEM
    {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }
    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")] static extern bool GetProcessMemoryInfo(IntPtr h, ref MEM c, int size);
    [DllImport("kernel32.dll")] static extern bool GetProcessHandleCount(IntPtr h, out int n);
    [DllImport("user32.dll")] static extern int GetGuiResources(IntPtr h, int kind);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

    public sealed class Snap
    {
        public long Private;
        public int Handles, Gdi, User;
        public string Text()
        {
            return L.F("пам'ять {0}, дескрипторів {1}, об'єктів GDI {2}, USER {3}", Cleaner.Size(Private), Handles, Gdi, User);
        }
        // для журналу (без перекладу)
        public string Log() { return "пам'ять " + (Private >> 20) + " МБ, дескрипторів " + Handles + ", GDI " + Gdi + ", USER " + User; }
        // схоже на витік: до межі Windows (10 000 об'єктів GDI/USER на процес) лишилось небагато, або пам'яті забагато
        public bool Odd { get { return Private > (1200L << 20) || Handles > 20000 || Gdi > 7000 || User > 7000; } }
    }

    static Snap Of(IntPtr h)
    {
        Snap s = new Snap();
        MEM m = new MEM();
        m.cb = (uint)Marshal.SizeOf(typeof(MEM));
        if (GetProcessMemoryInfo(h, ref m, (int)m.cb)) s.Private = (long)m.PrivateUsage.ToUInt64();
        GetProcessHandleCount(h, out s.Handles);
        s.Gdi = GetGuiResources(h, 0);
        s.User = GetGuiResources(h, 1);
        return s;
    }

    public static Snap Me() { return Of(GetCurrentProcess()); }

    // інший процес (рушій шпалер); null - процес недоступний
    public static Snap OfPid(int pid)
    {
        if (pid <= 0) return null;
        IntPtr h = Win.OpenProcess(0x0400 | 0x1000, false, (uint)pid);       // QUERY_INFORMATION (об'єкти GDI/USER) | QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) h = Win.OpenProcess(0x1000, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try { return Of(h); }
        finally { Win.CloseHandle(h); }
    }
}

static class UiWatch
{
    [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool QueryUnbiasedInterruptTime(out ulong t);

    const int Period = 5000;
    static Timer timer;
    static IntPtr hwnd;
    static ulong lastAwake;                 // час без сну комп'ютера, 100 нс
    static bool hung, hungNoted, oddNoted;
    static DateTime hungAt;
    static int ticks, inTick;

    public static void Start(IntPtr barHandle)
    {
        hwnd = barHandle;
        if (timer != null) return;
        QueryUnbiasedInterruptTime(out lastAwake);
        timer = new Timer(Tick, null, Period, Period);
    }

    public static void Stop()
    {
        Timer t = timer;
        timer = null;
        if (t != null) t.Dispose();
    }

    static void Tick(object state)
    {
        if (Interlocked.Exchange(ref inTick, 1) == 1) return;
        try
        {
            // 1. уся система стояла: сторож прокинувся значно пізніше, ніж мав (час сну комп'ютера не рахується)
            ulong awake;
            QueryUnbiasedInterruptTime(out awake);
            double gap = (awake - lastAwake) / 1e7;
            lastAwake = awake;
            if (gap > 20)
                Util.ErrorNote("комп'ютер (або весь процес NeoBar) не працював близько " + (int)gap + " с - до " + DateTime.Now.ToString("HH:mm:ss") +
                               ". Це зависання всієї системи, а не лише панелі: дивіться «Перевірка системи» → «Стабільність системи».");

            // 2. інтерфейс NeoBar не обробляє повідомлення (так це визначає сама Windows - понад 5 с)
            bool h = hwnd != IntPtr.Zero && IsHungAppWindow(hwnd);
            if (h && !hung) { hung = true; hungNoted = false; hungAt = DateTime.Now.AddSeconds(-5); }
            else if (h && !hungNoted && (DateTime.Now - hungAt).TotalSeconds > 60)
            {
                hungNoted = true;
                Util.ErrorNote("інтерфейс NeoBar не відповідає вже понад хвилину (з " + hungAt.ToString("HH:mm:ss") + ")");
            }
            else if (!h && hung)
            {
                hung = false;
                Util.ErrorNote("інтерфейс NeoBar не відповідав " + (int)(DateTime.Now - hungAt).TotalSeconds + " с (з " + hungAt.ToString("HH:mm:ss") + ")");
            }

            // 3. ресурси самого NeoBar: щохвилини - перевірка на витік, щогодини - рядок у журнал панелі
            ticks++;
            if (ticks % 12 == 0)
            {
                SelfUse.Snap s = SelfUse.Me();
                if (s.Odd && !oddNoted)
                {
                    oddNoted = true;
                    Util.ErrorNote("NeoBar займає аномально багато ресурсів (" + s.Log() + ") - схоже на витік. Перезапустіть NeoBar і надішліть цей журнал.");
                }
                if (ticks % 720 == 12 && SbLog.On(SbLog.Panel)) SbLog.W(SbLog.Panel, "ресурси NeoBar: " + s.Log());
            }
        }
        catch (Exception ex) { SbLog.Skip(ex); }
        finally { Interlocked.Exchange(ref inTick, 0); }
    }
}

static class SysEvents
{
    public sealed class Sum
    {
        public DateTime At;
        public int Gpu, Mem, Power, Crash;
        public DateTime GpuAt, MemAt, PowerAt, CrashAt;
        public string MemTop = "", CrashWhat = "", Error = "";
        public bool Any { get { return Gpu + Mem + Power + Crash > 0; } }
    }

    public const int Days = 7;
    static volatile Sum cached;
    static int busy;

    public static Sum Cached { get { return cached; } }
    public static bool Fresh { get { Sum c = cached; return c != null && (DateTime.Now - c.At).TotalMinutes < 10; } }

    // перечитати у фоні; done викликається з фонового потоку
    public static void Refresh(Action done)
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        Util.Bg(delegate
        {
            try { cached = Read(); }
            finally { Interlocked.Exchange(ref busy, 0); }
            if (done != null) done();
        });
    }

    static string Since { get { return "TimeCreated[timediff(@SystemTime) <= " + (Days * 86400000L) + "]"; } }

    // Журнал подій Windows - напряму через wevtapi.dll (Windows Vista+), подія у вигляді XML
    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr EvtQuery(IntPtr session, string path, string query, int flags);
    [DllImport("wevtapi.dll", SetLastError = true)] static extern bool EvtNext(IntPtr set, int count, [Out] IntPtr[] events, int timeout, int flags, out int returned);
    [DllImport("wevtapi.dll", SetLastError = true)] static extern bool EvtRender(IntPtr ctx, IntPtr ev, int flags, int size, IntPtr buf, out int used, out int props);
    [DllImport("wevtapi.dll")] static extern bool EvtClose(IntPtr h);

    public sealed class Ev
    {
        public int Id;
        public string Provider = "", Xml = "";
        public DateTime At;
        public List<string> Data = new List<string>();
    }

    static readonly Regex rxId = new Regex(@"<EventID[^>]*>(\d+)</EventID>"), rxProv = new Regex("<Provider Name=['\"]([^'\"]+)['\"]"),
                          rxTime = new Regex("<TimeCreated SystemTime=['\"]([^'\"]+)['\"]"), rxData = new Regex(@"<Data(?: Name=['""][^'""]*['""])?>([^<]*)</Data>|<Data(?: Name=['""][^'""]*['""])?/>");

    // подія з її XML: номер, джерело, час (місцевий), дані по порядку
    public static Ev Parse(string xml)
    {
        Ev e = new Ev();
        e.Xml = xml;
        Match m = rxId.Match(xml);
        if (m.Success) int.TryParse(m.Groups[1].Value, out e.Id);
        m = rxProv.Match(xml);
        if (m.Success) e.Provider = m.Groups[1].Value;
        m = rxTime.Match(xml);
        DateTime t;
        if (m.Success && DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t)) e.At = t.ToLocalTime();
        foreach (Match d in rxData.Matches(xml)) e.Data.Add(d.Groups[1].Value);
        return e;
    }

    // до 400 найновіших подій, що відповідають запиту
    static List<Ev> Query(string log, string xpath)
    {
        List<Ev> res = new List<Ev>();
        IntPtr q = EvtQuery(IntPtr.Zero, log, xpath, 0x1 | 0x200);          // EvtQueryChannelPath | EvtQueryReverseDirection
        if (q == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        IntPtr buf = IntPtr.Zero;
        int cap = 0;
        try
        {
            IntPtr[] evs = new IntPtr[16];
            int got;
            while (res.Count < 400 && EvtNext(q, evs.Length, evs, 2000, 0, out got) && got > 0)
            {
                for (int i = 0; i < got; i++)
                {
                    try
                    {
                        int used, props;
                        if (!EvtRender(IntPtr.Zero, evs[i], 1, cap, buf, out used, out props))          // EvtRenderEventXml
                        {
                            if (Marshal.GetLastWin32Error() != 122 || used <= 0 || used > (4 << 20)) continue;      // ERROR_INSUFFICIENT_BUFFER
                            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                            cap = used + 1024;
                            buf = Marshal.AllocHGlobal(cap);
                            if (!EvtRender(IntPtr.Zero, evs[i], 1, cap, buf, out used, out props)) continue;
                        }
                        res.Add(Parse(Marshal.PtrToStringUni(buf) ?? ""));
                    }
                    finally { EvtClose(evs[i]); }
                }
            }
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            EvtClose(q);
        }
        return res;
    }

    static Sum Read()
    {
        Sum s = new Sum();
        s.At = DateTime.Now;
        try
        {
            // журнал «Система»: 4101 - відеодрайвер перестав відповідати й був перезапущений; 2004 - нестача віртуальної
            // пам'яті (з переліком процесів, що зайняли найбільше); 41 - комп'ютер вимкнувся без завершення роботи
            foreach (Ev r in Query("System", "*[System[(EventID=4101 or EventID=2004 or EventID=41) and " + Since + "]]"))
            {
                if (r.Id == 4101 && r.Provider.Equals("Display", StringComparison.OrdinalIgnoreCase)) { s.Gpu++; if (r.At > s.GpuAt) s.GpuAt = r.At; }
                else if (r.Id == 2004 && r.Provider.IndexOf("Resource-Exhaustion", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    s.Mem++;
                    if (r.At > s.MemAt) { s.MemAt = r.At; s.MemTop = TopProcesses(r.Xml); }
                }
                else if (r.Id == 41 && r.Provider.IndexOf("Kernel-Power", StringComparison.OrdinalIgnoreCase) >= 0) { s.Power++; if (r.At > s.PowerAt) s.PowerAt = r.At; }
            }
            // журнал «Застосунок»: 1000 - збій програми, 1002 - програма зависла (лише NeoBar і рушій шпалер);
            // 1001 із LiveKernelEvent - Windows зафіксувала зависання відеокарти чи драйвера без синього екрана
            foreach (Ev r in Query("Application", "*[System[(EventID=1000 or EventID=1002 or EventID=1001) and " + Since + "]]"))
            {
                if (r.Id == 1001)
                {
                    if (r.Xml.IndexOf("LiveKernelEvent", StringComparison.OrdinalIgnoreCase) >= 0) { s.Gpu++; if (r.At > s.GpuAt) s.GpuAt = r.At; }
                    continue;
                }
                if (r.Xml.IndexOf("NeoBar", StringComparison.OrdinalIgnoreCase) < 0) continue;
                s.Crash++;
                if (r.At > s.CrashAt)
                {
                    s.CrashAt = r.At;
                    // збій: [0] програма, [3] модуль, де сталася помилка
                    string app = r.Data.Count > 0 ? r.Data[0] : "", mod = r.Id == 1000 && r.Data.Count > 3 ? r.Data[3] : "";
                    s.CrashWhat = app + (mod.Length > 0 && !mod.Equals(app, StringComparison.OrdinalIgnoreCase) ? " (" + mod + ")" : "") + (r.Id == 1002 ? L.T(" - зависла") : "");
                }
            }
        }
        catch (Exception ex) { s.Error = ex.Message; SbLog.Skip(ex); }
        return s;
    }

    // подія 2004: <Process_N><Name>…</Name>…<CommitCharge>байти</CommitCharge> - три процеси, що зайняли найбільше
    public static string TopProcesses(string xml)
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            foreach (Match m in Regex.Matches(xml, @"<Name>([^<]+)</Name>.*?<CommitCharge>(\d+)</CommitCharge>", RegexOptions.Singleline))
            {
                long bytes;
                if (!long.TryParse(m.Groups[2].Value, out bytes)) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(m.Groups[1].Value).Append(" - ").Append(Cleaner.Size(bytes));
            }
            return sb.ToString();
        }
        catch { return ""; }
    }
}
