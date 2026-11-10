using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

// ======================================================================
//  Частота кадрів у іграх.
//  Трасування подій Windows (ETW), як у PresentMon:
//    DXGI / Direct3D 9 (події Present) - DirectX 9-12;
//    DxgKrnl (ядро графіки) - Vulkan, OpenGL та все інше.
//  Потрібні права адміністратора.
// ======================================================================
sealed class FrameStats
{
    public double Fps = double.NaN, FrameMs = double.NaN, AvgMs = double.NaN, Low1 = double.NaN, Low01 = double.NaN,
                  MaxMs = double.NaN, StutterPct = double.NaN;
    public string Source = "", Api = "";
    public float[] Recent = new float[0];        // час останніх кадрів, мс (для графіка)
    public bool Valid { get { return !double.IsNaN(Fps); } }
}

static class Fps
{
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int StartTrace(out ulong h, string name, IntPtr props);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern int ControlTrace(ulong h, string name, IntPtr props, uint code);
    [DllImport("advapi32.dll")] static extern int EnableTraceEx2(ulong h, ref Guid prov, uint code, byte level, ulong any, ulong all, uint timeout, IntPtr p);
    // саме W-версія: назва сесії передається в Unicode (без цього Windows не знаходила сесію - код 4201)
    [DllImport("advapi32.dll", EntryPoint = "OpenTraceW", ExactSpelling = true, SetLastError = true)] static extern ulong OpenTrace(IntPtr logfile);
    [DllImport("advapi32.dll")] static extern int ProcessTrace(ulong[] h, uint n, IntPtr s, IntPtr e);
    [DllImport("advapi32.dll")] static extern int CloseTrace(ulong h);

    delegate void RecordCb(IntPtr rec);

    const string Session = "NeoBarFpsSession";
    static readonly Guid Dxgi = new Guid("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    static readonly Guid D3d9 = new Guid("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    static readonly Guid DxgKrnl = new Guid("802EC45A-1E99-4B83-9920-87C98277BA9D");

    // джерела кадрів за пріоритетом: події рушія гри точніші за події ядра
    const int SrcDxgi = 0, SrcD3d9 = 1, SrcK215 = 2, SrcK171 = 3, SrcK184 = 4, SrcCount = 5;
    static readonly string[] SrcName = { "DXGI", "Direct3D 9", L.T("ядро (DxgKrnl)"), L.T("ядро (DxgKrnl)"), L.T("ядро (DxgKrnl)") };

    sealed class Track
    {
        public readonly double[] Ts = new double[4096];
        public int Head, Count;
        public double Last;
        public double Arrived;      // коли надійшла остання подія (Windows віддає події пачками, тому судимо за надходженням, а не за часом кадру)
        public void Add(double t)
        {
            Arrived = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
            Ts[Head] = t;
            Head = (Head + 1) % Ts.Length;
            if (Count < Ts.Length) Count++;
            Last = t;
        }
        public double At(int fromEnd) { return Ts[(Head - 1 - fromEnd + Ts.Length * 2) % Ts.Length]; }
    }

    static readonly Dictionary<long, Track> tracks = new Dictionary<long, Track>();
    static readonly long[] srcEvents = new long[SrcCount];
    static RecordCb cb;
    static IntPtr logBuf, cbPtr;
    static ulong session, trace;
    static Thread th;
    static volatile bool running;
    static DateTime started;
    static bool kernelOn;
    static IntPtr loggerNamePtr;
    static DateTime failedAt = DateTime.MinValue;     // коли трасування востаннє завершилось помилкою (щоб не перезапускати щосекунди)

    // EVENT_TRACE_LOGFILEW і вкладені структури - описані повністю, щоб маршалер сам
    // обчислив зсув поля EventRecordCallback (інакше його легко вказати не туди, і подій не буде)
    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEMTIME { public ushort y, mo, dw, d, h, mi, s, ms; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TIME_ZONE_INFORMATION
    {
        public int Bias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string StandardName;
        public SYSTEMTIME StandardDate;
        public int StandardBias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DaylightName;
        public SYSTEMTIME DaylightDate;
        public int DaylightBias;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct EVENT_TRACE_HEADER
    {
        public ushort Size, FieldTypeFlags;
        public uint Version, ThreadId, ProcessId;
        public long TimeStamp;
        public Guid Guid;
        public uint KernelTime, UserTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct EVENT_TRACE
    {
        public EVENT_TRACE_HEADER Header;
        public uint InstanceId, ParentInstanceId;
        public Guid ParentGuid;
        public IntPtr MofData;
        public uint MofLength, ClientContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TRACE_LOGFILE_HEADER
    {
        public uint BufferSize, Version, ProviderVersion, NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution, MaximumFileSize, LogFileMode, BuffersWritten;
        public Guid LogInstanceGuid;                  // union (16 байт)
        public IntPtr LoggerNameU, LogFileNameU;
        public TIME_ZONE_INFORMATION TimeZone;
        public long BootTime, PerfFreq, StartTime;
        public uint ReservedFlags, BuffersLost;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct EVENT_TRACE_LOGFILE
    {
        public IntPtr LogFileName;
        public IntPtr LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        public EVENT_TRACE CurrentEvent;
        public TRACE_LOGFILE_HEADER LogfileHeader;
        public IntPtr BufferCallback;
        public uint BufferSize, Filled, EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }
    public static string Error = "";

    public static bool Running { get { return running; } }
    static double NowMs() { return Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency; }

    public static long Events
    {
        get { long n = 0; foreach (long v in srcEvents) n += v; return n; }
    }

    // короткий стан для інтерфейсу
    public static string Status
    {
        get
        {
            if (!Elevated.IsAdmin) return L.T("немає прав адміністратора");
            if (!running) return Error.Length > 0 ? Error : L.T("не запущено");
            long rt = srcEvents[SrcDxgi] + srcEvents[SrcD3d9], k = srcEvents[SrcK215] + srcEvents[SrcK171] + srcEvents[SrcK184];
            return L.T("працює · кадрів DirectX ") + rt + (kernelOn ? L.T(", ядро ") + k : L.T(", ядро вимкнено"));
        }
    }

    // журнал оверлею (logs\overlay.log): пише, лише коли його ввімкнено в розділі «Діагностика»
    public static void Log(string s) { SbLog.W(SbLog.Games, s); }

    static IntPtr Props()
    {
        int size = 120 + 1024 * 2;
        IntPtr p = Marshal.AllocHGlobal(size);
        for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, size);              // Wnode.BufferSize
        Marshal.WriteInt32(p, 40, 1);                // Wnode.ClientContext = QPC
        Marshal.WriteInt32(p, 44, 0x20000);          // WNODE_FLAG_TRACED_GUID
        Marshal.WriteInt32(p, 48, 16);               // BufferSize, КБ (менші буфери - події надходять частіше)
        Marshal.WriteInt32(p, 52, 8);                // MinimumBuffers
        Marshal.WriteInt32(p, 64, 0x100);            // LogFileMode = REAL_TIME
        Marshal.WriteInt32(p, 68, 1);                // FlushTimer, с
        Marshal.WriteInt32(p, 116, 120);             // LoggerNameOffset
        return p;
    }

    // Увімкнути провайдера з фільтром за номерами подій (менше навантаження)
    static int Enable(Guid g, byte level, ulong any, ushort[] ids)
    {
        IntPtr filt = IntPtr.Zero, desc = IntPtr.Zero, par = IntPtr.Zero;
        try
        {
            if (ids == null) return EnableTraceEx2(session, ref g, 1, level, any, 0, 0, IntPtr.Zero);
            int fsize = 4 + 2 * ids.Length;
            filt = Marshal.AllocHGlobal(fsize);
            Marshal.WriteByte(filt, 0, 1);                          // FilterIn = TRUE
            Marshal.WriteByte(filt, 1, 0);
            Marshal.WriteInt16(filt, 2, (short)ids.Length);
            for (int i = 0; i < ids.Length; i++) Marshal.WriteInt16(filt, 4 + i * 2, (short)ids[i]);
            desc = Marshal.AllocHGlobal(16);
            Marshal.WriteInt64(desc, 0, filt.ToInt64());
            Marshal.WriteInt32(desc, 8, fsize);
            Marshal.WriteInt32(desc, 12, unchecked((int)0x80000200));    // EVENT_FILTER_TYPE_EVENT_ID
            par = Marshal.AllocHGlobal(48);
            for (int i = 0; i < 48; i++) Marshal.WriteByte(par, i, 0);
            Marshal.WriteInt32(par, 0, 2);                          // ENABLE_TRACE_PARAMETERS_VERSION_2
            Marshal.WriteIntPtr(par, 32, desc);
            Marshal.WriteInt32(par, 40, 1);
            return EnableTraceEx2(session, ref g, 1, level, any, 0, 0, par);
        }
        finally
        {
            if (filt != IntPtr.Zero) Marshal.FreeHGlobal(filt);
            if (desc != IntPtr.Zero) Marshal.FreeHGlobal(desc);
            if (par != IntPtr.Zero) Marshal.FreeHGlobal(par);
        }
    }

    public static bool Start()
    {
        if (running) return true;
        if ((DateTime.Now - failedAt).TotalSeconds < 30) return false;
        Error = "";
        if (IntPtr.Size != 8) { Error = L.T("потрібна 64-бітна Windows"); return false; }
        if (!Elevated.IsAdmin) { Error = L.T("немає прав адміністратора"); return false; }
        try
        {
            IntPtr p = Props();
            try
            {
                ControlTrace(0, Session, p, 1);      // зупинити стару сесію, якщо лишилась після збою
                Marshal.FreeHGlobal(p);
                p = Props();
                int r = StartTrace(out session, Session, p);
                if (r != 0) { Error = L.T("трасування не запустилось (код ") + r + ")"; Log(Error); failedAt = DateTime.Now; return false; }
            }
            finally { Marshal.FreeHGlobal(p); }

            const ulong all = ulong.MaxValue;
            int e1 = Enable(Dxgi, 4, all, new ushort[] { 42, 55 });
            if (e1 != 0) e1 = Enable(Dxgi, 4, all, null);
            int e2 = Enable(D3d9, 4, all, new ushort[] { 1 });
            if (e2 != 0) e2 = Enable(D3d9, 4, all, null);
            // ядро: Base | Present; лише з фільтром подій, інакше потік подій занадто великий
            int e3 = Enable(DxgKrnl, 4, 0x8000001UL, new ushort[] { 215, 171, 184 });
            kernelOn = e3 == 0;
            Log(L.T("ETW запущено: DXGI=") + e1 + ", D3D9=" + e2 + ", DxgKrnl=" + e3);

            cb = OnRecord;
            cbPtr = Marshal.GetFunctionPointerForDelegate(cb);
            loggerNamePtr = Marshal.StringToHGlobalUni(Session);
            EVENT_TRACE_LOGFILE lf = new EVENT_TRACE_LOGFILE();
            lf.LoggerName = loggerNamePtr;
            // REAL_TIME | EVENT_RECORD | RAW_TIMESTAMP (час у тиках QPC, як Stopwatch)
            lf.ProcessTraceMode = 0x100 | 0x10000000 | 0x1000;
            lf.EventRecordCallback = cbPtr;
            Log(L.T("EVENT_TRACE_LOGFILE: розмір ") + Marshal.SizeOf(typeof(EVENT_TRACE_LOGFILE)) + ", callback @" + Marshal.OffsetOf(typeof(EVENT_TRACE_LOGFILE), "EventRecordCallback"));
            logBuf = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(EVENT_TRACE_LOGFILE)));
            Marshal.StructureToPtr(lf, logBuf, false);
            trace = OpenTrace(logBuf);
            if (trace == ulong.MaxValue || trace == 0)
            {
                Error = L.T("не вдалося відкрити трасування (код ") + Marshal.GetLastWin32Error() + ")";
                Log(Error);
                failedAt = DateTime.Now;
                Stop();
                return false;
            }
            running = true;
            started = DateTime.Now;
            for (int i = 0; i < SrcCount; i++) srcEvents[i] = 0;
            th = new Thread(delegate ()
            {
                int pr = -1;
                try { pr = ProcessTrace(new ulong[] { trace }, 1, IntPtr.Zero, IntPtr.Zero); }
                catch (Exception ex) { Log("ProcessTrace: " + ex.Message); }
                Log(L.T("ProcessTrace завершився (код ") + pr + L.T("), кадрів: ") + Events);
                if (pr != 0 && running) { Error = L.T("трасування зупинилось (код ") + pr + ")"; failedAt = DateTime.Now; }
                running = false;
            });
            th.IsBackground = true;
            th.Start();
            return true;
        }
        catch (Exception ex) { Error = ex.Message; Log("Start: " + ex); return false; }
    }

    public static void Stop()
    {
        try
        {
            if (trace != 0 && trace != ulong.MaxValue) CloseTrace(trace);
            IntPtr p = Props();
            ControlTrace(session, Session, p, 1);
            Marshal.FreeHGlobal(p);
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        if (running) Log(L.T("ETW зупинено, кадрів: ") + Events);
        try { if (logBuf != IntPtr.Zero) { Marshal.FreeHGlobal(logBuf); logBuf = IntPtr.Zero; } } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        try { if (loggerNamePtr != IntPtr.Zero) { Marshal.FreeHGlobal(loggerNamePtr); loggerNamePtr = IntPtr.Zero; } } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        trace = 0; session = 0;
        running = false;
    }

    static bool logged;
    public static void Diagnose()
    {
        if (!running || logged || (DateTime.Now - started).TotalSeconds < 20) return;
        logged = true;
        Log(L.T("За 20 с: DXGI ") + srcEvents[SrcDxgi] + ", D3D9 " + srcEvents[SrcD3d9] + L.T(", ядро ") + (srcEvents[SrcK215] + srcEvents[SrcK171] + srcEvents[SrcK184]));
    }

    static void OnRecord(IntPtr rec)
    {
        try
        {
            ushort id = (ushort)Marshal.ReadInt16(rec, 40);
            long g0 = Marshal.ReadInt64(rec, 24), g1 = Marshal.ReadInt64(rec, 32);
            int src;
            if (Eq(g0, g1, Dxgi)) { if (id != 42 && id != 55) return; src = SrcDxgi; }
            else if (Eq(g0, g1, D3d9)) { if (id != 1) return; src = SrcD3d9; }
            else if (Eq(g0, g1, DxgKrnl)) { if (id == 215) src = SrcK215; else if (id == 171) src = SrcK171; else if (id == 184) src = SrcK184; else return; }
            else return;
            int pid = Marshal.ReadInt32(rec, 12);
            double ts = Marshal.ReadInt64(rec, 16) * 1000.0 / Stopwatch.Frequency;
            Interlocked.Increment(ref srcEvents[src]);
            long key = (long)pid * 8 + src;
            lock (tracks)
            {
                Track t;
                if (!tracks.TryGetValue(key, out t)) { t = new Track(); tracks[key] = t; }
                t.Add(ts);
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    static readonly long[] guidCache = new long[6];
    static bool guidInit;
    static bool Eq(long a, long b, Guid g)
    {
        if (!guidInit)
        {
            Guid[] gs = { Dxgi, D3d9, DxgKrnl };
            for (int i = 0; i < 3; i++)
            {
                byte[] x = gs[i].ToByteArray();
                guidCache[i * 2] = BitConverter.ToInt64(x, 0);
                guidCache[i * 2 + 1] = BitConverter.ToInt64(x, 8);
            }
            guidInit = true;
        }
        int k = g == Dxgi ? 0 : (g == D3d9 ? 1 : 2);
        return guidCache[k * 2] == a && guidCache[k * 2 + 1] == b;
    }

    // ---------- статистика ----------

    public static FrameStats Stats(int pid)
    {
        FrameStats s = new FrameStats();
        if (pid <= 0) return s;
        double now = NowMs();
        Track best = null;
        int bestSrc = -1;
        lock (tracks)
        {
            for (int src = 0; src < SrcCount; src++)
            {
                Track t;
                if (!tracks.TryGetValue((long)pid * 8 + src, out t) || t.Count < 3) continue;
                if (now - t.Arrived > 3500) continue;
                best = t; bestSrc = src;
                break;
            }
            if (best != null) Fill(s, best, now);
        }
        if (best != null) { s.Source = SrcName[bestSrc]; s.Api = Api(pid, bestSrc); return s; }

        return s;
    }

    static void Fill(FrameStats s, Track t, double now)
    {
        // інтервали між кадрами за останні 10 с (не більше буфера)
        List<float> iv = new List<float>();
        double newest = t.At(0);
        for (int i = 0; i + 1 < t.Count; i++)
        {
            double a = t.At(i), b = t.At(i + 1);
            if (newest - b > 10000) break;
            double d = a - b;
            if (d <= 0 || d > 2000) continue;
            iv.Add((float)d);
        }
        if (iv.Count == 0) return;
        iv.Reverse();     // від старих до нових
        FromIntervals(s, iv);
    }

    // статистика з інтервалів між кадрами (від старих до нових), мс
    public static void FromIntervals(FrameStats s, List<float> iv)
    {
        if (iv.Count < 2) return;
        s.FrameMs = iv[iv.Count - 1];
        // FPS за останню секунду (точніше за середнє на коротких вибірках)
        double span1 = 0; int n1 = 0;
        for (int i = iv.Count - 1; i >= 0 && span1 < 1000; i--) { span1 += iv[i]; n1++; }
        s.Fps = span1 > 0 ? n1 * 1000.0 / span1 : double.NaN;
        double sum = 0; int n2 = 0;
        for (int i = iv.Count - 1; i >= 0 && sum < 2000; i--) { sum += iv[i]; n2++; }
        s.AvgMs = n2 > 0 ? sum / n2 : double.NaN;
        Lows(s, iv.ToArray());
        int take = Math.Min(240, iv.Count);
        s.Recent = iv.GetRange(iv.Count - take, take).ToArray();
    }

    // 1% і 0.1% low (середнє найповільніших кадрів), найдовший кадр, частка мікрозатримок
    static void Lows(FrameStats s, float[] iv)
    {
        if (iv.Length < 5) return;
        float[] sorted = (float[])iv.Clone();
        Array.Sort(sorted);
        int n = sorted.Length;
        s.MaxMs = sorted[n - 1];
        s.Low1 = 1000.0 / AvgTop(sorted, Math.Max(1, n / 100));
        s.Low01 = 1000.0 / AvgTop(sorted, Math.Max(1, n / 1000));
        double median = sorted[n / 2];
        int st = 0;
        foreach (float v in iv) if (v > median * 2 && v > 8) st++;
        s.StutterPct = st * 100.0 / n;
    }

    static double AvgTop(float[] sorted, int k)
    {
        double sum = 0;
        for (int i = sorted.Length - k; i < sorted.Length; i++) sum += sorted[i];
        return sum / k;
    }

    // ---------- графічний API ----------
    static readonly Dictionary<int, string> apiCache = new Dictionary<int, string>();
    static readonly Dictionary<int, DateTime> apiAt = new Dictionary<int, DateTime>();

    static string Api(int pid, int src)
    {
        string cached;
        DateTime at;
        if (apiCache.TryGetValue(pid, out cached) && cached.Length > 0) return cached;
        if (apiAt.TryGetValue(pid, out at) && (DateTime.Now - at).TotalSeconds < 10) return src == SrcD3d9 ? "DirectX 9" : (src == SrcDxgi ? "DirectX" : "");
        apiAt[pid] = DateTime.Now;
        bool d3d9 = false, d3d11 = false, d3d12 = false, d3d10 = false, vk = false, gl = false;
        try
        {
            using (Process p = Process.GetProcessById(pid))
                foreach (ProcessModule m in p.Modules)
                {
                    string n = m.ModuleName.ToLowerInvariant();
                    if (n == "d3d9.dll") d3d9 = true;
                    else if (n == "d3d11.dll") d3d11 = true;
                    else if (n == "d3d12.dll") d3d12 = true;
                    else if (n == "d3d10.dll" || n == "d3d10_1.dll") d3d10 = true;
                    else if (n == "vulkan-1.dll") vk = true;
                    else if (n == "opengl32.dll") gl = true;
                }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        string api = "";
        if (src == SrcD3d9) api = "DirectX 9";
        else if (src == SrcDxgi) api = d3d12 ? "DirectX 12" : (d3d11 ? "DirectX 11" : (d3d10 ? "DirectX 10" : "DirectX"));
        else if (vk && !d3d12) api = "Vulkan";
        else if (d3d12) api = "DirectX 12";
        else if (d3d11) api = "DirectX 11";
        else if (d3d9) api = "DirectX 9";
        else if (gl) api = "OpenGL";
        if (api.Length > 0 && api != "DirectX") apiCache[pid] = api;
        return api;
    }

    public static void Forget()
    {
        double now = NowMs();
        lock (tracks)
        {
            List<long> dead = new List<long>();
            foreach (KeyValuePair<long, Track> kv in tracks) if (now - kv.Value.Arrived > 15000) dead.Add(kv.Key);
            foreach (long k in dead) tracks.Remove(k);
        }
    }
}
