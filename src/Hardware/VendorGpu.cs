using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

// ======================================================================
//  Датчики відеокарт від виробника (те, чого Windows не віддає):
//   AMD - ADL (atiadlxx.dll, той самий драйвер, що й Adrenalin):
//         частоти ГП і пам'яті, hotspot, температура пам'яті, потужність, напруга, вентилятор;
//   NVIDIA - NVML (nvml.dll): частоти, температура, потужність, вентилятор;
//   Intel  - Intel Graphics Control Library (ControlLib.dll, ставиться з драйвером Intel):
//            температура, частоти ГП і пам'яті, потужність, напруга, вентилятор (Arc), завантаження.
//  Усе необов'язкове: якщо бібліотеки нема, лишаються дані Windows.
// ======================================================================
sealed class VendorStats
{
    public string Source = "";
    public float GfxClock = float.NaN, MemClock = float.NaN, Temp = float.NaN, Hotspot = float.NaN, MemTemp = float.NaN,
                 PowerW = float.NaN, VoltMv = float.NaN, FanRpm = float.NaN, FanPct = float.NaN, Load = float.NaN, MemLoad = float.NaN,
                 CpuTemp = float.NaN, CpuPowerW = float.NaN, CpuClock = float.NaN;
}

static class VendorGpu
{
    // ---------------- AMD ADL ----------------
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr AdlAlloc(int size);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AdlCreate(AdlAlloc cb, int connected, out IntPtr ctx);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AdlNum(IntPtr ctx, out int n);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AdlInfo(IntPtr ctx, IntPtr info, int size);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AdlActive(IntPtr ctx, int index, out int status);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AdlPmLog(IntPtr ctx, int index, IntPtr output);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryEx(string name, IntPtr file, uint flags);

    // бібліотеки драйвера лежать у System32: шукаємо лише там, щоб не підхопити підкладену поруч із програмою DLL з тим самим ім'ям.
    // Старі Windows 7 без оновлення KB2533623 не знають прапорця (ERROR_INVALID_PARAMETER) - тоді звичайний пошук
    static IntPtr LoadSys(string name)
    {
        IntPtr h = LoadLibraryEx(name, IntPtr.Zero, 0x800);       // LOAD_LIBRARY_SEARCH_SYSTEM32
        if (h == IntPtr.Zero && Marshal.GetLastWin32Error() == 87) h = Win.LoadLibrary(name);
        return h;
    }

    static bool adlTried, nvTried;
    static IntPtr adlCtx;
    static AdlAlloc adlAllocKeep;
    static AdlPmLog adlPm;
    static int adlIndex = -1;
    static readonly List<int> adlAll = new List<int>();      // усі AMD-адаптери (ГП і вбудоване ядро) - датчики ЦП можуть бути на будь-якому
    static IntPtr pmBuf;
    public static string AdlName = "", Status = "";

    static T Fn<T>(IntPtr lib, string name) where T : class
    {
        IntPtr f = Win.GetProcAddress(lib, name);
        return f == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(f, typeof(T)) as T;
    }

    static IntPtr Alloc(int size) { return Marshal.AllocHGlobal(Math.Max(1, size)); }

    static void InitAdl()
    {
        adlTried = true;
        try
        {
            IntPtr lib = LoadSys(IntPtr.Size == 8 ? "atiadlxx.dll" : "atiadlxy.dll");
            if (lib == IntPtr.Zero) return;
            AdlCreate create = Fn<AdlCreate>(lib, "ADL2_Main_Control_Create");
            AdlNum num = Fn<AdlNum>(lib, "ADL2_Adapter_NumberOfAdapters_Get");
            AdlInfo info = Fn<AdlInfo>(lib, "ADL2_Adapter_AdapterInfo_Get");
            AdlActive active = Fn<AdlActive>(lib, "ADL2_Adapter_Active_Get");
            adlPm = Fn<AdlPmLog>(lib, "ADL2_New_QueryPMLogData_Get");
            if (create == null || num == null || info == null || adlPm == null) { Status = L.T("AMD: драйвер без потрібних функцій ADL"); return; }
            adlAllocKeep = Alloc;
            if (create(adlAllocKeep, 1, out adlCtx) != 0 || adlCtx == IntPtr.Zero) { Status = L.T("AMD: ADL не ініціалізувався"); return; }
            int n;
            if (num(adlCtx, out n) != 0 || n <= 0 || n > 256) return;
            const int infoSize = 1572;                          // розмір AdapterInfo (Windows)
            IntPtr arr = Marshal.AllocHGlobal(infoSize * n);
            pmBuf = Marshal.AllocHGlobal(4 + 256 * 8);
            try
            {
                for (int i = 0; i < n * infoSize; i++) Marshal.WriteByte(arr, i, 0);
                for (int i = 0; i < n; i++) Marshal.WriteInt32(arr, i * infoSize, infoSize);
                if (info(adlCtx, arr, infoSize * n) != 0) return;
                HashSet<int> buses = new HashSet<int>();
                int best = -1, bestCount = 0;
                for (int i = 0; i < n; i++)
                {
                    IntPtr a = new IntPtr(arr.ToInt64() + i * infoSize);
                    int idx = Marshal.ReadInt32(a, 4), bus = Marshal.ReadInt32(a, 264), vendor = Marshal.ReadInt32(a, 276);
                    if (vendor != 1002 && vendor != 0x1002) continue;
                    if (!buses.Add(bus)) continue;                  // кожен ГП трапляється кілька разів (за виходами)
                    int st;
                    if (active != null && active(adlCtx, idx, out st) == 0 && st == 0) { }
                    int cnt = CountSensors(idx);
                    adlAll.Add(idx);
                    LogSensors(idx, Marshal.PtrToStringAnsi(new IntPtr(a.ToInt64() + 280)), bus);
                    if (cnt > bestCount) { bestCount = cnt; best = idx; AdlName = Marshal.PtrToStringAnsi(new IntPtr(a.ToInt64() + 280)); }
                }
                adlIndex = best;
                Status = best >= 0 ? "AMD ADL: " + AdlName.Trim() : L.T("AMD: датчики ADL недоступні (потрібна RX 400 або новіша)");
            }
            finally { Marshal.FreeHGlobal(arr); }
        }
        catch (Exception ex) { Status = "AMD ADL: " + ex.Message; adlIndex = -1; }
    }

    static int CountSensors(int idx)
    {
        try
        {
            for (int i = 0; i < 4 + 256 * 8; i++) Marshal.WriteByte(pmBuf, i, 0);
            if (adlPm(adlCtx, idx, pmBuf) != 0) return 0;
            int c = 0;
            for (int s = 1; s < 64; s++) if (Marshal.ReadInt32(pmBuf, 4 + s * 8) != 0) c++;
            return c;
        }
        catch { return 0; }
    }

    // у журнал: які датчики віддає драйвер (щоб знати, чого бракує)
    static void LogSensors(int idx, string name, int bus)
    {
        if (!SbLog.On(SbLog.Sensors)) return;
        try
        {
            if (adlPm(adlCtx, idx, pmBuf) != 0) { SbLog.W(SbLog.Sensors, "ADL адаптер " + idx + " (" + (name ?? "").Trim() + ", шина " + bus + "): датчики недоступні"); return; }
            StringBuilder sb = new StringBuilder();
            for (int sid = 1; sid < 64; sid++)
                if (Marshal.ReadInt32(pmBuf, 4 + sid * 8) != 0) sb.Append(sid + "=" + Marshal.ReadInt32(pmBuf, 4 + sid * 8 + 4) + " ");
            SbLog.W(SbLog.Sensors, "ADL адаптер " + idx + " (" + (name ?? "").Trim() + ", шина " + bus + "): " + (sb.Length > 0 ? sb.ToString() : "датчиків немає"));
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    static bool Sensor(int id, out float v)
    {
        v = float.NaN;
        if (Marshal.ReadInt32(pmBuf, 4 + id * 8) == 0) return false;
        v = Marshal.ReadInt32(pmBuf, 4 + id * 8 + 4);
        return true;
    }

    static bool ReadAdl(VendorStats s)
    {
        if (!adlTried) InitAdl();
        if (adlIndex < 0 || adlPm == null) return false;
        try
        {
            if (adlPm(adlCtx, adlIndex, pmBuf) != 0) return false;
            float v;
            // індекси ADL_PMLOG_*: 1 частота ГП, 2 пам'яті, 8 температура (край), 9 пам'ять, 14 вентилятор об/хв, 15 вентилятор %,
            // 19 завантаження ГП, 20 завантаження пам'яті, 21 напруга ГП, 23 потужність, 27 hotspot, 32 темп. ЦП, 33 потужн. ЦП, 34 частота ЦП
            if (Sensor(1, out v)) s.GfxClock = v;
            if (Sensor(2, out v)) s.MemClock = v;
            if (Sensor(8, out v)) s.Temp = v;
            if (Sensor(9, out v)) s.MemTemp = v;
            if (Sensor(14, out v)) s.FanRpm = v;
            if (Sensor(15, out v)) s.FanPct = v;
            if (Sensor(19, out v)) s.Load = v;
            if (Sensor(20, out v)) s.MemLoad = v;
            if (Sensor(21, out v)) s.VoltMv = v;
            if (Sensor(23, out v)) s.PowerW = v;
            if (Sensor(27, out v)) s.Hotspot = v;
            if (Sensor(32, out v) && v > 0) s.CpuTemp = v;
            if (Sensor(33, out v) && v > 0) s.CpuPowerW = v;
            if (Sensor(34, out v) && v > 0) s.CpuClock = v;
            if (float.IsNaN(s.Temp) && Sensor(28, out v)) s.Temp = v;
            // датчики ЦП (Ryzen) драйвер може віддавати з іншого адаптера - обходимо їх усі
            if (float.IsNaN(s.CpuTemp) || float.IsNaN(s.CpuClock) || float.IsNaN(s.CpuPowerW))
                foreach (int idx in adlAll)
                {
                    if (idx == adlIndex || adlPm(adlCtx, idx, pmBuf) != 0) continue;
                    if (float.IsNaN(s.CpuTemp) && Sensor(32, out v) && v > 0) s.CpuTemp = v;
                    if (float.IsNaN(s.CpuPowerW) && Sensor(33, out v) && v > 0) s.CpuPowerW = v;
                    if (float.IsNaN(s.CpuClock) && Sensor(34, out v) && v > 0) s.CpuClock = v;
                }
            s.Source = "AMD";
            return true;
        }
        catch { adlIndex = -1; return false; }
    }

    // ---------------- NVIDIA NVML ----------------
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NvInit();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NvHandle(uint index, out IntPtr dev);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NvU32Arg(IntPtr dev, int kind, out uint v);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NvU32(IntPtr dev, out uint v);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NvUtil(IntPtr dev, out ulong v);

    static IntPtr nvDev;
    static NvU32Arg nvTemp, nvClock;
    static NvU32 nvPower, nvFan;
    static NvUtil nvUtil;

    // nvml.dll: System32 (сучасні драйвери), NVSMI (старі драйвери до ~2019), сховище драйверів (DCH-драйвери від виробника
    // ноутбука або з Windows Update кладуть її лише туди). null - бібліотеки немає.
    static string nvmlPath; static bool nvmlLooked;
    public static string NvmlPath()
    {
        if (nvmlLooked) return nvmlPath;
        nvmlLooked = true;
        try
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string[] direct = { System.IO.Path.Combine(sys, "nvml.dll"),
                                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVSMI\nvml.dll") };
            foreach (string p in direct) if (System.IO.File.Exists(p)) { nvmlPath = p; return p; }
            string repo = System.IO.Path.Combine(sys, @"DriverStore\FileRepository");
            DateTime best = DateTime.MinValue;
            foreach (string d in System.IO.Directory.GetDirectories(repo, "nv*"))
            {
                string p = System.IO.Path.Combine(d, "nvml.dll");
                if (!System.IO.File.Exists(p)) continue;
                DateTime t = System.IO.File.GetLastWriteTimeUtc(p);
                if (t > best) { best = t; nvmlPath = p; }           // кілька версій драйвера - найновіша
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return nvmlPath;
    }

    static void InitNv()
    {
        nvTried = true;
        try
        {
            IntPtr lib = LoadSys("nvml.dll");
            if (lib == IntPtr.Zero)
            {
                string p = NvmlPath();
                if (p != null) lib = LoadLibraryEx(p, IntPtr.Zero, 0x8);    // LOAD_WITH_ALTERED_SEARCH_PATH: залежності - з теки драйвера
            }
            if (lib == IntPtr.Zero) return;
            NvInit init = Fn<NvInit>(lib, "nvmlInit_v2");
            NvHandle handle = Fn<NvHandle>(lib, "nvmlDeviceGetHandleByIndex_v2");
            nvTemp = Fn<NvU32Arg>(lib, "nvmlDeviceGetTemperature");
            nvClock = Fn<NvU32Arg>(lib, "nvmlDeviceGetClockInfo");
            nvPower = Fn<NvU32>(lib, "nvmlDeviceGetPowerUsage");
            nvFan = Fn<NvU32>(lib, "nvmlDeviceGetFanSpeed");
            nvUtil = Fn<NvUtil>(lib, "nvmlDeviceGetUtilizationRates");
            if (init == null || handle == null || init() != 0 || handle(0, out nvDev) != 0) { nvDev = IntPtr.Zero; return; }
            Status = "NVIDIA NVML";
        }
        catch { nvDev = IntPtr.Zero; }
    }

    static bool ReadNv(VendorStats s)
    {
        if (!nvTried) InitNv();
        if (nvDev == IntPtr.Zero) return false;
        // ноутбук (NVIDIA Optimus): запит до сплячої дискретної карти будить її, і вона марно їсть батарею.
        // Тож у простої (немає гри, карта не навантажена) датчики NVIDIA не опитуємо - вони потрібні під час гри.
        if (Battery.Present() && Games.CurrentPid <= 0 && !(Sensors.GpuLoad >= 1)) return false;
        try
        {
            uint v;
            if (nvTemp != null && nvTemp(nvDev, 0, out v) == 0) s.Temp = v;
            if (nvClock != null && nvClock(nvDev, 0, out v) == 0) s.GfxClock = v;      // NVML_CLOCK_GRAPHICS
            if (nvClock != null && nvClock(nvDev, 2, out v) == 0) s.MemClock = v;      // NVML_CLOCK_MEM
            if (nvPower != null && nvPower(nvDev, out v) == 0) s.PowerW = v / 1000f;
            if (nvFan != null && nvFan(nvDev, out v) == 0) s.FanPct = v;
            ulong u;
            if (nvUtil != null && nvUtil(nvDev, out u) == 0) { s.Load = (uint)(u & 0xFFFFFFFF); s.MemLoad = (uint)(u >> 32); }
            s.Source = "NVIDIA";
            return true;
        }
        catch { nvDev = IntPtr.Zero; return false; }
    }

    // ---------------- Intel IGCL ----------------
    // Розкладка структур - за igcl_api.h (Intel drivers.gpu.control-library); вказівників у них немає,
    // тож для 32- і 64-бітного процесу вона однакова.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CtlInit(IntPtr args, out IntPtr api);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CtlEnum(IntPtr api, ref uint count, IntPtr handles);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CtlTele(IntPtr dev, IntPtr tele);

    const int TeleSize = 1024;
    const int OffTime = 8, OffGpuEnergy = 32, OffGpuVolt = 56, OffGpuClock = 80, OffGpuTemp = 104, OffActivity = 128,
              OffMemClock = 256, OffMemTemp = 352, OffCardEnergy = 384, OffFan = 688;
    static bool igTried;
    static IntPtr igDev, teleBuf;
    static CtlTele igTele;
    static double igLastT = double.NaN, igLastE = double.NaN, igLastA = double.NaN;
    static float igPower = float.NaN, igLoad = float.NaN;
    public static string IntelStatus = "";

    static void InitIntel()
    {
        igTried = true;
        try
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            IntPtr lib = Win.LoadLibrary(System.IO.Path.Combine(sys, IntPtr.Size == 8 ? "ControlLib.dll" : "ControlLib32.dll"));
            if (lib == IntPtr.Zero) { IntelStatus = L.T("Intel: бібліотеки драйвера (ControlLib.dll) немає"); return; }
            CtlInit init = Fn<CtlInit>(lib, "ctlInit");
            CtlEnum en = Fn<CtlEnum>(lib, "ctlEnumerateDevices");
            igTele = Fn<CtlTele>(lib, "ctlPowerTelemetryGet");
            if (init == null || en == null || igTele == null) { IntelStatus = L.T("Intel: драйвер без функцій телеметрії"); return; }
            IntPtr api = IntPtr.Zero;
            IntPtr args = Marshal.AllocHGlobal(64);
            try
            {
                // спершу з Level Zero (так драйвер віддає більше датчиків), інакше - без нього
                foreach (int flags in new int[] { 1, 0 })
                {
                    for (int i = 0; i < 64; i++) Marshal.WriteByte(args, i, 0);
                    Marshal.WriteInt32(args, 0, 36);                     // Size
                    Marshal.WriteInt32(args, 8, (1 << 16) | 1);          // AppVersion = 1.1
                    Marshal.WriteInt32(args, 12, flags);
                    if (init(args, out api) == 0 && api != IntPtr.Zero) break;
                    api = IntPtr.Zero;
                }
            }
            finally { Marshal.FreeHGlobal(args); }
            if (api == IntPtr.Zero) { IntelStatus = L.T("Intel: бібліотека драйвера не ініціалізувалась"); return; }
            uint n = 0;
            if (en(api, ref n, IntPtr.Zero) != 0 || n == 0 || n > 16) { IntelStatus = L.T("Intel: відеоадаптерів Intel не знайдено"); return; }
            IntPtr hs = Marshal.AllocHGlobal(IntPtr.Size * (int)n);
            teleBuf = Marshal.AllocHGlobal(TeleSize + 64);
            try
            {
                if (en(api, ref n, hs) != 0) return;
                int best = 0;
                for (int i = 0; i < n; i++)
                {
                    IntPtr d = Marshal.ReadIntPtr(hs, i * IntPtr.Size);
                    if (d == IntPtr.Zero || !Tele(d)) continue;
                    int c = 0;
                    foreach (int off in new int[] { OffGpuTemp, OffGpuClock, OffGpuEnergy, OffMemClock, OffMemTemp, OffFan, OffActivity }) if (Ok(off)) c++;
                    if (Ok(OffGpuTemp)) c += 4;                   // дискретна Arc із температурою важливіша за вбудовану
                    if (c > best) { best = c; igDev = d; }
                }
            }
            finally { Marshal.FreeHGlobal(hs); }
            IntelStatus = igDev != IntPtr.Zero ? "Intel IGCL" : L.T("Intel: драйвер не віддає телеметрію");
            if (SbLog.On(SbLog.Sensors)) SbLog.W(SbLog.Sensors, "Intel IGCL: пристроїв " + n + ", " + IntelStatus);
        }
        catch (Exception ex) { IntelStatus = "Intel IGCL: " + ex.Message; igDev = IntPtr.Zero; }
    }

    static bool Tele(IntPtr dev)
    {
        for (int i = 0; i < TeleSize; i += 4) Marshal.WriteInt32(teleBuf, i, 0);
        Marshal.WriteInt32(teleBuf, 0, TeleSize);
        return igTele(dev, teleBuf) == 0;
    }

    static bool Ok(int off) { return Marshal.ReadByte(teleBuf, off) != 0; }

    // значення елемента телеметрії: тип 9 double, 8 float, 7/6 u64/i64, 5/4 u32/i32, 3/2 u16/i16, 1/0 u8/i8
    static double Val(int off)
    {
        if (!Ok(off)) return double.NaN;
        int type = Marshal.ReadInt32(teleBuf, off + 8);
        int v = off + 16;
        switch (type)
        {
            case 9: return BitConverter.Int64BitsToDouble(Marshal.ReadInt64(teleBuf, v));
            case 8: return BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(teleBuf, v)), 0);
            case 7: return (double)(ulong)Marshal.ReadInt64(teleBuf, v);
            case 6: return Marshal.ReadInt64(teleBuf, v);
            case 5: return (uint)Marshal.ReadInt32(teleBuf, v);
            case 4: return Marshal.ReadInt32(teleBuf, v);
            case 3: return (ushort)Marshal.ReadInt16(teleBuf, v);
            case 2: return Marshal.ReadInt16(teleBuf, v);
            case 1: return Marshal.ReadByte(teleBuf, v);
            case 0: return (sbyte)Marshal.ReadByte(teleBuf, v);
        }
        return double.NaN;
    }

    static float Sane(double v, double lo, double hi) { return double.IsNaN(v) || v < lo || v > hi ? float.NaN : (float)v; }

    static bool ReadIntel(VendorStats s)
    {
        if (!igTried) InitIntel();
        if (igDev == IntPtr.Zero) return false;
        try
        {
            if (!Tele(igDev)) return false;
            s.Temp = Sane(Val(OffGpuTemp), 1, 125);
            s.MemTemp = Sane(Val(OffMemTemp), 1, 125);
            s.GfxClock = Sane(Val(OffGpuClock), 1, 10000);
            s.MemClock = Sane(Val(OffMemClock), 1, 30000);
            double volt = Val(OffGpuVolt);
            if (!double.IsNaN(volt) && volt > 0.1 && volt < 3) s.VoltMv = (float)(volt * 1000);
            // вентилятор (лише дискретні Arc): об/хв або відсотки - за одиницями
            if (Ok(OffFan))
            {
                int units = Marshal.ReadInt32(teleBuf, OffFan + 4);
                float f = Sane(Val(OffFan), 0, 20000);
                if (units == 11) s.FanPct = f; else s.FanRpm = f;
            }
            // потужність і завантаження - різниця лічильників між двома зчитуваннями
            double t = Val(OffTime);
            double e = Ok(OffCardEnergy) ? Val(OffCardEnergy) : Val(OffGpuEnergy);
            double a = Val(OffActivity);
            if (!double.IsNaN(t) && !double.IsNaN(igLastT) && t - igLastT > 0.05)
            {
                double dt = t - igLastT;
                if (!double.IsNaN(e) && !double.IsNaN(igLastE) && e >= igLastE) igPower = Sane((e - igLastE) / dt, 0, 1000);
                if (!double.IsNaN(a) && !double.IsNaN(igLastA) && a >= igLastA) igLoad = Sane((a - igLastA) / dt * 100, 0, 100);
            }
            if (!double.IsNaN(t)) { igLastT = t; igLastE = e; igLastA = a; }
            s.PowerW = igPower;
            s.Load = igLoad;
            s.Source = "Intel";
            return true;
        }
        catch { igDev = IntPtr.Zero; return false; }
    }

    // Показники відеокарти від драйвера виробника; null - драйвер їх не віддає
    public static VendorStats Read()
    {
        VendorStats s = new VendorStats();
        // NVIDIA першою: бібліотека NVML є лише з драйвером NVIDIA (інакше вбудоване ядро AMD перебило б дискретну карту);
        // Intel - останнім: на ноутбуці з дискретною картою вбудоване ядро Intel не має її перебивати
        if (ReadNv(s)) return s;
        if (ReadAdl(s)) return s;
        s = new VendorStats();
        if (ReadIntel(s)) return s;
        return null;
    }
}

// ----------------------------------------------------------------------
//  Температура процесора. Windows сама її не віддає (потрібен драйвер ядра), тому, якщо запущено
//  LibreHardwareMonitor або OpenHardwareMonitor, беремо значення з їхнього WMI-інтерфейсу.
// ----------------------------------------------------------------------
static class CpuTempWmi
{
    public static string Source = "";
    static DateTime next = DateTime.MinValue;
    static float last = float.NaN;
    static readonly string[] Spaces = { @"root\LibreHardwareMonitor", @"root\OpenHardwareMonitor" };

    static volatile bool busy;

    // WMI повільний (з'єднання з простором імен - десятки-сотні мс, а коли LibreHardwareMonitor не запущено - щоразу марно),
    // тож опитуємо у фоновому потоці, а повертаємо останнє відоме значення
    public static float Read()
    {
        if (DateTime.Now < next || busy) return last;
        next = DateTime.Now.AddSeconds(3);
        busy = true;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            try { Refresh(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            finally { busy = false; }
        });
        return last;
    }

    static void Refresh()
    {
        float res = float.NaN; string src = "";
        Type lt = null;
        try { lt = Type.GetTypeFromProgID("WbemScripting.SWbemLocator"); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        if (lt == null) { next = DateTime.Now.AddSeconds(60); last = res; Source = src; return; }
        foreach (string ns in Spaces)
        {
            try
            {
                dynamic loc = Activator.CreateInstance(lt);
                dynamic svc = loc.ConnectServer(".", ns);
                float best = float.NaN, pkg = float.NaN;
                foreach (dynamic o in svc.ExecQuery("SELECT Name, Value, Identifier FROM Sensor WHERE SensorType='Temperature'"))
                {
                    string id = ((string)o.Identifier ?? "").ToLowerInvariant();
                    string name = ((string)o.Name ?? "").ToLowerInvariant();
                    if (id.IndexOf("cpu") < 0) continue;
                    float v = Convert.ToSingle((object)o.Value);
                    if (v < 5 || v > 125) continue;
                    if (name.IndexOf("package") >= 0 || name.IndexOf("tctl") >= 0 || name.IndexOf("tdie") >= 0 || name.IndexOf("cpu total") >= 0) pkg = v;
                    if (float.IsNaN(best) || v > best) best = v;
                }
                float r = !float.IsNaN(pkg) ? pkg : best;
                if (!float.IsNaN(r)) { last = r; Source = ns.Substring(ns.IndexOf('\\') + 1); return; }
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
        next = DateTime.Now.AddSeconds(30);       // нічого не запущено - не питати щосекунди
        last = res; Source = src;
    }
}
