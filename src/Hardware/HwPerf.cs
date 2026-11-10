using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

// ======================================================================
//  Датчики: ЦП, ОЗП, ГП (як у Диспетчері завдань: PDH + D3DKMT), температури, FPS (ETW)
// ======================================================================

// Лічильники продуктивності Windows (англійські назви - незалежно від мови системи)
sealed class PdhQuery : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct FMT { public uint Status; public uint pad; public double Value; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string src, IntPtr user, out IntPtr q);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr q, string path, IntPtr user, out IntPtr c);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr q);
    [DllImport("pdh.dll")] static extern int PdhGetFormattedCounterValue(IntPtr c, uint fmt, IntPtr type, out FMT v);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArray(IntPtr c, uint fmt, ref uint size, out uint count, IntPtr buf);
    [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr q);

    const uint DOUBLE = 0x200, NOCAP100 = 0x8000;
    IntPtr q;
    readonly Dictionary<string, IntPtr> ctr = new Dictionary<string, IntPtr>();

    public bool Ok { get { return q != IntPtr.Zero; } }

    public PdhQuery()
    {
        try { if (PdhOpenQuery(null, IntPtr.Zero, out q) != 0) q = IntPtr.Zero; }
        catch { q = IntPtr.Zero; }
    }

    public bool Add(string key, string path)
    {
        if (q == IntPtr.Zero) return false;
        try
        {
            IntPtr c;
            if (PdhAddEnglishCounter(q, path, IntPtr.Zero, out c) != 0) return false;
            ctr[key] = c;
            return true;
        }
        catch { return false; }
    }

    public void Collect()
    {
        if (q == IntPtr.Zero) return;
        try { PdhCollectQueryData(q); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    public double Value(string key)
    {
        IntPtr c;
        if (!ctr.TryGetValue(key, out c)) return double.NaN;
        try
        {
            FMT v;
            if (PdhGetFormattedCounterValue(c, DOUBLE | NOCAP100, IntPtr.Zero, out v) != 0 || (v.Status != 0 && v.Status != 1)) return double.NaN;
            return v.Value;
        }
        catch { return double.NaN; }
    }

    // усі екземпляри лічильника з «*»: назва -> значення
    public Dictionary<string, double> Values(string key)
    {
        Dictionary<string, double> res = new Dictionary<string, double>();
        IntPtr c;
        if (!ctr.TryGetValue(key, out c)) return res;
        try
        {
            uint size = 0, count;
            int r = PdhGetFormattedCounterArray(c, DOUBLE | NOCAP100, ref size, out count, IntPtr.Zero);
            if (size == 0) return res;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                r = PdhGetFormattedCounterArray(c, DOUBLE | NOCAP100, ref size, out count, buf);
                if (r != 0) return res;
                // PDH_FMT_COUNTERVALUE_ITEM_W: вказівник на назву, далі (вирівняно на 8) статус і значення - 24 байти
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = new IntPtr(buf.ToInt64() + i * 24);
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                    uint st = (uint)Marshal.ReadInt32(new IntPtr(item.ToInt64() + 8));
                    double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(new IntPtr(item.ToInt64() + 16)));
                    if (name != null && (st == 0 || st == 1)) res[name] = v;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return res;
    }

    public void Dispose()
    {
        if (q != IntPtr.Zero) try { PdhCloseQuery(q); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        q = IntPtr.Zero;
    }
}

// Відеоадаптери через D3DKMT (ті самі дані, що й у Диспетчері завдань: температура ГП тощо)
sealed class GpuAdapter
{
    public uint Handle;
    public uint LuidLow;
    public int LuidHigh;
    public string Name = "";
    public ulong Dedicated, Shared;
    public bool Software;
    public double TempMax = double.NaN;

    public string LuidKey { get { return "luid_0x" + LuidHigh.ToString("X8") + "_0x" + LuidLow.ToString("X8"); } }
}

static class Gpu
{
    [StructLayout(LayoutKind.Sequential)] struct ENUM2 { public uint Num; public IntPtr Adapters; }
    [StructLayout(LayoutKind.Sequential)] struct QAI { public uint Adapter; public int Type; public IntPtr Data; public uint Size; }
    [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(ref ENUM2 e);
    [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref QAI q);

    static List<GpuAdapter> list;

    public static List<GpuAdapter> Adapters()
    {
        if (list != null) return list;
        list = new List<GpuAdapter>();
        try
        {
            ENUM2 e = new ENUM2();
            if (D3DKMTEnumAdapters2(ref e) != 0 || e.Num == 0) return list;
            IntPtr buf = Marshal.AllocHGlobal((int)(e.Num * 20));
            try
            {
                e.Adapters = buf;
                if (D3DKMTEnumAdapters2(ref e) != 0) return list;
                for (int i = 0; i < e.Num; i++)
                {
                    IntPtr p = new IntPtr(buf.ToInt64() + i * 20);
                    GpuAdapter a = new GpuAdapter();
                    a.Handle = (uint)Marshal.ReadInt32(p);
                    a.LuidLow = (uint)Marshal.ReadInt32(new IntPtr(p.ToInt64() + 4));
                    a.LuidHigh = Marshal.ReadInt32(new IntPtr(p.ToInt64() + 8));
                    byte[] reg = Query(a.Handle, 8, 2080);
                    if (reg != null) a.Name = Io.Wide(SubArr(reg, 0, 520));
                    byte[] ty = Query(a.Handle, 15, 4);
                    if (ty != null) a.Software = (BitConverter.ToUInt32(ty, 0) & 4) != 0;
                    byte[] seg = Query(a.Handle, 3, 24);
                    if (seg != null) { a.Dedicated = BitConverter.ToUInt64(seg, 0); a.Shared = BitConverter.ToUInt64(seg, 16); }
                    byte[] caps = Query(a.Handle, 63, 40);
                    if (caps != null)
                    {
                        uint tm = BitConverter.ToUInt32(caps, 28);
                        if (tm > 0 && tm < 2000) a.TempMax = tm / 10.0;
                    }
                    if (a.Name.Length == 0) a.Name = L.T("Відеоадаптер");
                    if (!a.Software && a.Name.IndexOf("Basic Render", StringComparison.OrdinalIgnoreCase) < 0) list.Add(a);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            // спершу дискретний (найбільше власної пам'яті)
            list.Sort(delegate (GpuAdapter x, GpuAdapter y) { return y.Dedicated.CompareTo(x.Dedicated); });
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return list;
    }

    static byte[] SubArr(byte[] b, int o, int n)
    {
        byte[] r = new byte[Math.Min(n, b.Length - o)];
        Array.Copy(b, o, r, 0, r.Length);
        return r;
    }

    static byte[] Query(uint adapter, int type, int size)
    {
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(buf, i, 0);
            QAI q = new QAI();
            q.Adapter = adapter; q.Type = type; q.Data = buf; q.Size = (uint)size;
            if (D3DKMTQueryAdapterInfo(ref q) != 0) return null;
            byte[] r = new byte[size];
            Marshal.Copy(buf, r, 0, size);
            return r;
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // температура (°C), потужність (% від номінальної), оберти вентилятора; NaN/-1 якщо драйвер не віддає
    public static bool Perf(GpuAdapter a, out double temp, out double power, out int fan)
    {
        temp = power = double.NaN; fan = -1;
        byte[] d = Query(a.Handle, 62, 64);
        if (d == null) return false;
        uint t = BitConverter.ToUInt32(d, 56), p = BitConverter.ToUInt32(d, 52), f = BitConverter.ToUInt32(d, 48);
        if (t > 0 && t < 2000) temp = t / 10.0;
        if (p > 0 && p <= 2000) power = p / 10.0;
        if (f > 0 && f < 20000) fan = (int)f;
        return true;
    }
}

// ----------------------------------------------------------------------
//  Збір показників (лише коли їх хтось показує)
// ----------------------------------------------------------------------
static class Sensors
{
    public static float Cpu = float.NaN, CpuMhz = float.NaN, CpuTemp = float.NaN, Ram = float.NaN,
                        GpuLoad = float.NaN, GpuTemp = float.NaN, GpuPower = float.NaN, Vram = float.NaN, FpsNow = float.NaN, FrameMs = float.NaN,
                        GpuClock = float.NaN, MemClock = float.NaN, Hotspot = float.NaN, MemTemp = float.NaN, GpuPowerW = float.NaN,
                        GpuVolt = float.NaN, GpuFanPct = float.NaN, CpuPowerW = float.NaN, CpuPeak = float.NaN;
    public const double BusyPct = 20;        // потік вважається працюючим, якщо завантажений понад цей відсоток
    public static int CpuBusy = -1;          // скільки логічних процесорів працює (понад BusyPct %)
    public static int CpuThreads;            // скільки логічних процесорів порахували
    public static double RamUsedGb, RamTotalGb, VramUsedGb, VramTotalGb;
    public static int GpuFan = -1, Cores;
    public static string CpuName = "", GpuName = "", FgName = "", VendorSource = "";
    public static bool TempIsAcpi;
    public static FrameStats Frame = new FrameStats();

    public const int HistLen = 600;
    public static readonly Dictionary<string, List<float>> Hist = new Dictionary<string, List<float>>();
    static readonly Dictionary<string, int> users = new Dictionary<string, int>();
    static readonly Dictionary<string, int> intervals = new Dictionary<string, int>();
    static System.Windows.Forms.Timer timer;
    static PdhQuery pdh;
    static bool pdhInit;
    static volatile bool warmDone;      // перший (дорогий) запуск лічильників, відеокарти й датчиків виробника - у фоні
    static double baseMhz;
    public static event Action Updated;

    // level: 1 - лише ЦП/ОЗП, 2 - усе (ГП, температури, частота, FPS)
    public static void Use(string who, int level)
    {
        if (level <= 0) { users.Remove(who); intervals.Remove(who); }
        else users[who] = level;
        int max = Level;
        if (max == 0) { if (timer != null) timer.Stop(); return; }
        if (timer == null)
        {
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += delegate { Sample(); };
        }
        ApplyInterval();
        if (!timer.Enabled) { timer.Start(); Sample(); }
    }

    // частота оновлення, мс (найменша з тих, кого просили)
    public static void SetInterval(string who, int ms)
    {
        if (ms <= 0) intervals.Remove(who); else intervals[who] = Math.Max(100, Math.Min(5000, ms));
        ApplyInterval();
    }

    static void ApplyInterval()
    {
        if (timer == null) return;
        int ms = 1000;
        foreach (KeyValuePair<string, int> kv in intervals) if (users.ContainsKey(kv.Key)) ms = Math.Min(ms, kv.Value);
        if (timer.Interval != ms) timer.Interval = ms;
    }

    public static int Level
    {
        get { int m = 0; foreach (int v in users.Values) m = Math.Max(m, v); return m; }
    }

    static void Push(string key, float v)
    {
        List<float> l;
        if (!Hist.TryGetValue(key, out l)) { l = new List<float>(); Hist[key] = l; }
        l.Add(v);
        if (l.Count > HistLen) l.RemoveAt(0);
    }

    public static List<float> History(string key)
    {
        List<float> l;
        return Hist.TryGetValue(key, out l) ? l : null;
    }

    static void InitPdh()
    {
        pdhInit = true;
        try
        {
            pdh = new PdhQuery();
            if (!pdh.Ok) { pdh = null; return; }
            pdh.Add("perf", "\\Processor Information(_Total)\\% Processor Performance");
            pdh.Add("pcore", "\\Processor(*)\\% Processor Time");
            if (!pdh.Add("tzh", "\\Thermal Zone Information(*)\\High Precision Temperature"))
                pdh.Add("tz", "\\Thermal Zone Information(*)\\Temperature");
            pdh.Add("gpu", "\\GPU Engine(*)\\Utilization Percentage");
            pdh.Add("vram", "\\GPU Adapter Memory(*)\\Dedicated Usage");
            pdh.Collect();
        }
        catch { pdh = null; }
        try
        {
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            {
                if (k != null)
                {
                    object mhz = k.GetValue("~MHz");
                    if (mhz is int) baseMhz = (int)mhz;
                    CpuName = (k.GetValue("ProcessorNameString") as string ?? "").Trim();
                    while (CpuName.Contains("  ")) CpuName = CpuName.Replace("  ", " ");
                }
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        Cores = Environment.ProcessorCount;
    }

    public static void Sample()
    {
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        SampleCore();
        // у журналі (якщо ввімкнено) видно, чи опитування датчиків затримує інтерфейс
        if (sw.ElapsedMilliseconds >= 30 && SbLog.On(SbLog.Sensors)) SbLog.W(SbLog.Sensors, "опитування датчиків: " + sw.ElapsedMilliseconds + " мс у потоці інтерфейсу (рівень " + Level + ")");
    }

    static void SampleCore()
    {
        int level = Level;
        try
        {
            int c = Nat.CpuLoad();
            Cpu = c < 0 ? float.NaN : c;
            long tot, free;
            int r = Nat.RamLoad(out tot, out free);
            Ram = r < 0 ? float.NaN : r;
            RamTotalGb = tot / 1024.0;
            RamUsedGb = (tot - free) / 1024.0;
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        Push("cpu", Cpu);
        Push("ram", Ram);
        if (level >= 2)
        {
            // перше опитування рівня 2 (лічильники PDH, перелік відеокарт, NVML/ADL) важке - у журналі було до 0,5 с
            // у потоці інтерфейсу під час запуску гри; тому прогріваємо все у фоновому потоці, а повне опитування - після нього
            if (!pdhInit)
            {
                pdhInit = true;
                Thread th = new Thread(delegate ()
                {
                    System.Diagnostics.Stopwatch w = System.Diagnostics.Stopwatch.StartNew();
                    try { InitPdh(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                    try { Gpu.Adapters(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                    try { VendorGpu.Read(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                    try { CpuTempPawn.Read(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }       // завантаження модуля PawnIO (~0,1 с)
                    try { CpuTempWmi.Read(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                    warmDone = true;
                    if (SbLog.On(SbLog.Sensors)) SbLog.W(SbLog.Sensors, "прогрів датчиків у фоні: " + w.ElapsedMilliseconds + " мс");
                });
                th.IsBackground = true;
                th.Priority = ThreadPriority.BelowNormal;
                th.Start();
            }
            if (warmDone) SampleFull();
        }
        Action u = Updated;
        if (u != null) try { u(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    static void SampleFull()
    {
        GpuAdapter ga = null;
        try { List<GpuAdapter> al = Gpu.Adapters(); if (al.Count > 0) ga = al[0]; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        GpuName = ga != null ? ga.Name : "";
        if (pdh != null)
        {
            pdh.Collect();
            double perf = pdh.Value("perf");
            // завантаження кожного логічного процесора: пік і скільки потоків працює (понад BusyPct %)
            Dictionary<string, double> pc = pdh.Values("pcore");
            double peak = -1; int busy = 0, thr = 0;
            foreach (KeyValuePair<string, double> kv in pc)
            {
                if (kv.Key.StartsWith("_")) continue;
                thr++;
                if (kv.Value > peak) peak = kv.Value;
                if (kv.Value > BusyPct) busy++;
            }
            CpuPeak = peak < 0 ? float.NaN : (float)Math.Min(100, peak);
            CpuBusy = peak < 0 ? -1 : busy;
            CpuThreads = thr;
            CpuMhz = !double.IsNaN(perf) && baseMhz > 0 ? (float)(baseMhz * perf / 100.0) : float.NaN;

            CpuTemp = float.NaN;
            Dictionary<string, double> tz = pdh.Values("tzh");
            double scale = 10.0;
            if (tz.Count == 0) { tz = pdh.Values("tz"); scale = 1.0; }
            foreach (double k in tz.Values)
            {
                double cdeg = k / scale - 273.15;
                if (cdeg > 5 && cdeg < 125 && (float.IsNaN(CpuTemp) || cdeg > CpuTemp)) CpuTemp = (float)cdeg;
            }
            TempIsAcpi = !float.IsNaN(CpuTemp);

            GpuLoad = float.NaN;
            Vram = float.NaN;
            if (ga != null)
            {
                string key = ga.LuidKey.ToLowerInvariant();
                Dictionary<string, double> eng = new Dictionary<string, double>();
                foreach (KeyValuePair<string, double> kv in pdh.Values("gpu"))
                {
                    string n = kv.Key.ToLowerInvariant();
                    if (n.IndexOf(key, StringComparison.Ordinal) < 0) continue;
                    int e = n.IndexOf("_eng_", StringComparison.Ordinal);
                    string ek = e >= 0 ? n.Substring(e) : n;
                    double s;
                    eng.TryGetValue(ek, out s);
                    eng[ek] = s + kv.Value;
                }
                double mx = 0;
                foreach (double v in eng.Values) mx = Math.Max(mx, v);
                if (eng.Count > 0) GpuLoad = (float)Math.Min(100, mx);
                double used = 0;
                bool any = false;
                foreach (KeyValuePair<string, double> kv in pdh.Values("vram"))
                    if (kv.Key.ToLowerInvariant().IndexOf(key, StringComparison.Ordinal) >= 0) { used += kv.Value; any = true; }
                if (any)
                {
                    VramUsedGb = used / 1073741824.0;
                    VramTotalGb = ga.Dedicated / 1073741824.0;
                    if (ga.Dedicated > 0) Vram = (float)(used * 100.0 / ga.Dedicated);
                }
            }
        }
        GpuTemp = GpuPower = float.NaN;
        GpuFan = -1;
        if (ga != null)
        {
            double t, p; int f;
            if (Gpu.Perf(ga, out t, out p, out f)) { GpuTemp = (float)t; GpuPower = (float)p; GpuFan = f; }
        }

        // датчики виробника відеокарти (AMD ADL / NVIDIA NVML)
        GpuClock = MemClock = Hotspot = MemTemp = GpuPowerW = GpuVolt = GpuFanPct = CpuPowerW = float.NaN;
        VendorStats vs = null;
        try { vs = VendorGpu.Read(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        VendorSource = vs != null ? vs.Source : "";
        if (vs != null)
        {
            GpuClock = vs.GfxClock; MemClock = vs.MemClock; Hotspot = vs.Hotspot; MemTemp = vs.MemTemp;
            GpuPowerW = vs.PowerW; GpuVolt = vs.VoltMv; GpuFanPct = vs.FanPct; CpuPowerW = vs.CpuPowerW;
            if (!float.IsNaN(vs.Temp)) GpuTemp = vs.Temp;
            if (!float.IsNaN(vs.FanRpm)) GpuFan = (int)vs.FanRpm;
            if (float.IsNaN(GpuLoad) && !float.IsNaN(vs.Load)) GpuLoad = vs.Load;
            if (float.IsNaN(CpuTemp) && !float.IsNaN(vs.CpuTemp)) { CpuTemp = vs.CpuTemp; TempIsAcpi = false; }
        }
        if (float.IsNaN(CpuTemp) || TempIsAcpi)
        {
            float pt = float.NaN;
            try { pt = CpuTempPawn.Read(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            if (!float.IsNaN(pt)) { CpuTemp = pt; TempIsAcpi = false; }
        }
        if (float.IsNaN(CpuPowerW) && !float.IsNaN(CpuTempPawn.PowerW)) CpuPowerW = CpuTempPawn.PowerW;
        if (float.IsNaN(CpuTemp) || TempIsAcpi)
        {
            float wt = float.NaN;
            try { wt = CpuTempWmi.Read(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            if (!float.IsNaN(wt)) { CpuTemp = wt; TempIsAcpi = false; }
        }

        // кадри поточної гри
        FpsNow = FrameMs = float.NaN;
        FgName = Games.CurrentName;
        int gpid = Games.CurrentPid;
        if (gpid <= 0)
        {
            try { IntPtr h = Win.GetForegroundWindow(); Win.GetWindowThreadProcessId(h, out gpid); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
        try
        {
            // точні кадри з бібліотеки обмежувача (якщо вона в грі), інакше - трасування Windows
            Frame = FpsLimiter.Stats(gpid);
            if (!Frame.Valid) Frame = Fps.Stats(gpid);
            if (Frame.Valid) { FpsNow = (float)Frame.Fps; FrameMs = (float)Frame.FrameMs; }
            Fps.Forget();
            Fps.Diagnose();
        }
        catch { Frame = new FrameStats(); }

        Push("creal", CpuPeak);
        Push("cbusy", CpuBusy < 0 ? float.NaN : CpuBusy);
        Push("cmhz", CpuMhz);
        Push("ctemp", CpuTemp);
        Push("cpow", CpuPowerW);
        Push("gpu", GpuLoad);
        Push("gclk", GpuClock);
        Push("mclk", MemClock);
        Push("gtemp", GpuTemp);
        Push("ghot", Hotspot);
        Push("mtemp", MemTemp);
        Push("gpow", !float.IsNaN(GpuPowerW) ? GpuPowerW : GpuPower);
        Push("gfan", GpuFan > 0 ? GpuFan : GpuFanPct);
        Push("gvolt", GpuVolt);
        Push("vram", Vram);
        Push("ping", GamePing.Current);
        Push("fps", FpsNow);
        Push("frame", FrameMs);
        Push("low1", (float)Frame.Low1);
    }

    public static string F0(float v, string unit)
    {
        return float.IsNaN(v) ? "—" : Math.Round(v).ToString(CultureInfo.InvariantCulture) + unit;
    }
}
