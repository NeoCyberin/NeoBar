using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// ======================================================================
//  Обмежувач кадрів у іграх (без RivaTuner).
//  NeoBar впроваджує NeoBarFps64.dll / NeoBarFps32.dll у гру, для якої
//  користувач сам дозволив обмеження. Бібліотека перехоплює виведення кадру
//  (DirectX 9-12, OpenGL), чекає до потрібного часу й записує тривалість
//  кожного кадру в спільну пам'ять - звідти ж оверлей бере точний FPS.
//  Ігри з античітом не чіпаємо взагалі: впровадження там виглядає як чит.
// ======================================================================
sealed class LimSession
{
    public int Pid;
    public string Exe = "";
    public Game Game;
    public IntPtr Map, View;
    public IntPtr ImgMap, ImgView;  // картинка для показу в кадрі (Local\NeoBarOsd_<pid>)
    public int ImgSeq;
    public string Api = "";        // що використовує гра: dx8, dx9, dx10, dx11, dx12, opengl, vulkan, ddraw (за списком модулів)
    public DateTime ApiAt = DateTime.MinValue;
    public DateTime ApiSince = DateTime.MinValue;   // коли API востаннє змінився
    public DateTime PendSince = DateTime.MinValue;  // відколи чекаємо першого малювання в кадрі
    public bool Injected;          // бібліотеку вже завантажено в гру
    public volatile bool Injecting;  // впровадження триває у фоні (гра може відповідати кілька секунд - панель не чекає)
    public DateTime InjAt = DateTime.MinValue;       // коли бібліотеку завантажено
    public string ExclDbg = "";    // чому гру не визнано повноекранною (для рядка стану)
    public bool Excl;              // гра зараз у справжньому повноекрані (не вікно і не вікно без рамки)
    public DateTime ExclLast = DateTime.MinValue;
    public IntPtr Wnd;             // головне вікно гри (запам'ятане)
    public int Hz;                 // частота монітора, на якому гра
    public bool HzReal;            // вікно гри знайдено (а не взято основний монітор за замовчуванням)
    public DateTime HzAt = DateTime.MinValue;
    public string Blocked = "";    // причина, чому не чіпаємо (античіт тощо)
    public string Error = "";
    public string Others = "";     // інші оверлеї в процесі гри (Steam, RTSS, Discord...)
    public int Tries;
    public DateTime NextTry = DateTime.MinValue;
    public DateTime Born = DateTime.Now;               // відколи сесія відкрита
}

static class FpsLimiter
{
    const uint Magic = 0x31464253;            // 'SBF1'
    const int Size = 8192, Ring = 1024;
    // зсуви полів у спільній пам'яті (див. native/sbfps.cpp)
    const int OffMagic = 0, OffVersion = 4, OffEnabled = 8, OffTarget = 12, OffHooks = 16, OffApi = 20, OffStatus = 24,
              OffFrames = 28, OffLimiting = 32, OffD3d = 36, OffFreq = 40, OffLast = 48, OffFt = 56,
              OffBorderless = 4152, OffBdrState = 4156, OffBdrCount = 4160, OffBdrApi = 4164, OffBdrTarget = 4168, OffBdrWin = 4172, OffBdrFixes = 4176,
              OffOsdOn = 4180, OffOsdCorner = 4184, OffOsdScale = 4188, OffOsdDrawn = 4192, OffDdKind = 4196, OffOsdText = 4200, OffOsdErr = 4712, OffOsdApi = 4716, OffOsdFrameW = 4720, OffOsdFrameH = 4724, OffOsdImg = 4728, OffOsdExcl = 4732, OffDxgiVer = 4736, OffDdFail = 4740, OffDdHr = 4744, OffDdCalls = 4748, OffDdBox = 4752, OffCreateApi = 4776;

    static readonly Dictionary<int, LimSession> sessions = new Dictionary<int, LimSession>();
    public static string LastNote = "";       // останнє повідомлення для налаштувань

    // ---------- WinAPI ----------
    [DllImport("psapi.dll", CharSet = CharSet.Unicode)] static extern int GetModuleFileNameExW(IntPtr h, IntPtr m, StringBuilder sb, int n);
    static string lastMods = "", lastAllMods = "";
    static int wrapDiagPid; static DateTime wrapDiagBorn; static bool wrapDiagDone;

    // за кілька секунд після старту гри з обгорткою: чи справді гра її завантажила (журнал + позначка для списку ігор)
    static void WrapDiag(Game g, int pid)
    {
        if (g == null) return;
        if (wrapDiagPid != pid) { wrapDiagPid = pid; wrapDiagBorn = DateTime.Now; wrapDiagDone = false; }
        if (wrapDiagDone || (DateTime.Now - wrapDiagBorn).TotalSeconds < 8) return;
        wrapDiagDone = true;
        try
        {
            // чи справді гра завантажила нашу обгортку (а не системну бібліотеку) - позначка для списку ігор і один рядок журналу
            if (g.Wrap != "ddraw" || g.WrapFresh) { g.WrapMiss = false; return; }
            bool vk; DetectApi(pid, out vk);
            string dir = GameWrap.TargetDir(g);
            bool miss = false;
            foreach (string seg in lastMods.Split(';'))
            {
                string t = seg.Trim();
                if (t.StartsWith("ddraw=") && dir.Length > 0 && !t.Substring(6).StartsWith(dir, StringComparison.OrdinalIgnoreCase)) miss = true;
            }
            g.WrapMiss = miss;
            Fps.Log("Обгортка: " + g.Name + ": " + (miss ? "гра завантажила системну бібліотеку, а не обгортку" : "гра завантажила обгортку"));
        }
        catch { }
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool IsWow64Process(IntPtr h, out bool wow);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, UIntPtr size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, UIntPtr size, out UIntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, UIntPtr size, out UIntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateRemoteThread(IntPtr h, IntPtr attr, UIntPtr stack, IntPtr start, IntPtr param, uint flags, out uint tid);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeThread(IntPtr h, out uint code);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateFileMappingW(IntPtr file, ref SECURITY_ATTRIBUTES sa, uint protect, uint hi, uint lo, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint hi, uint lo, UIntPtr size);
    [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] static extern void MoveMem(IntPtr dst, IntPtr src, UIntPtr n);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
    [DllImport("kernel32.dll")] static extern bool QueryPerformanceCounter(out long t);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint rev, out IntPtr sd, IntPtr size);
    [DllImport("psapi.dll", SetLastError = true)] static extern bool EnumProcessModulesEx(IntPtr h, [Out] IntPtr[] mods, int cb, out int needed, uint filter);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode)] static extern uint GetModuleBaseNameW(IntPtr h, IntPtr mod, StringBuilder sb, int size);

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

    const uint PROCESS_INJECT = 0x0002 | 0x0400 | 0x0008 | 0x0020 | 0x0010;   // CREATE_THREAD | QUERY_INFORMATION | VM_OPERATION | VM_WRITE | VM_READ
    const uint FILE_MAP_ALL = 0x0002 | 0x0004;

    // ---------- частота монітора ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettingsW(string dev, int mode, ref DEVMODE dm);

    public static int HzOf(Screen scr)
    {
        try
        {
            DEVMODE dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            if (EnumDisplaySettingsW(scr.DeviceName, -1 /*ENUM_CURRENT_SETTINGS*/, ref dm) && dm.dmDisplayFrequency > 1) return dm.dmDisplayFrequency;
        }
        catch { }
        return 60;
    }

    public static int PrimaryHz() { return HzOf(Screen.PrimaryScreen); }

    // частоти всіх моніторів без повторів. Першою - частота монітора, на якому гра працювала востаннє (pref), якщо такий монітор зараз є;
    // інакше основного
    public static int[] MonitorRates(int pref)
    {
        List<int> r = new List<int>();
        try
        {
            List<int> all = new List<int>();
            all.Add(PrimaryHz());
            foreach (Screen sc in Screen.AllScreens) { int hz = HzOf(sc); if (!all.Contains(hz)) all.Add(hz); }
            if (pref > 0 && all.Contains(pref)) r.Add(pref);
            foreach (int hz in all) if (!r.Contains(hz)) r.Add(hz);
        }
        catch { }
        if (r.Count == 0) r.Add(60);
        return r.ToArray();
    }

    // true, якщо за першу частоту взято пам'ять про монітор гри (а не основний монітор)
    public static bool GameMonitorKnown(int pref)
    {
        try
        {
            if (pref <= 0) return false;
            foreach (Screen sc in Screen.AllScreens) if (HzOf(sc) == pref) return true;
        }
        catch { }
        return false;
    }

    // монітор, на якому вікно гри
    static int GameHz(LimSession s)
    {
        if (s.Hz > 0 && (DateTime.Now - s.HzAt).TotalSeconds < 5) return s.Hz;
        IntPtr w = IntPtr.Zero;
        try
        {
            IntPtr fg = Win.GetForegroundWindow();
            int fp;
            Win.GetWindowThreadProcessId(fg, out fp);
            if (fp == s.Pid) w = fg;
            else using (Process p = Process.GetProcessById(s.Pid)) w = p.MainWindowHandle;
        }
        catch { }
        s.Hz = HzOf(w != IntPtr.Zero ? Screen.FromHandle(w) : Screen.PrimaryScreen);
        s.HzReal = w != IntPtr.Zero;
        s.HzAt = DateTime.Now;
        if (s.HzReal && s.Game != null && s.Game.MonHz != s.Hz) { s.Game.MonHz = s.Hz; Games.Save(); }
        return s.Hz;
    }

    // ---------- налаштування ----------
    // свій ліміт гри або частота її монітора
    static double TargetFor(LimSession s)
    {
        double v = s != null && s.Game != null && s.Game.LimFps > 0 ? s.Game.LimFps : (s != null ? GameHz(s) : PrimaryHz());
        return Math.Max(10, Math.Min(1000, v));
    }

    static string FpsText(double v)
    {
        return v.ToString(Math.Abs(v - Math.Round(v)) < 0.005 ? "0" : "0.##", System.Globalization.CultureInfo.InvariantCulture);
    }

    static string TargetText(LimSession s)
    {
        bool auto = s == null || s.Game == null || s.Game.LimFps <= 0;
        return FpsText(TargetFor(s)) + (auto ? L.T(" (частота монітора)") : "");
    }

    public static bool ActiveFor(Game g)
    {
        return OverlayCfg.LimOn && g != null && g.Limit;
    }

    // ---------- API гри й спосіб показу оверлею: усе автоматично ----------
    // Автоматично: у вікні й вікні без рамки - окреме вікно поверх гри.
    // У справжньому повноекрані: DirectX 12 - підміна вікном без рамки (потім те саме вікно поверх),
    // Vulkan - лише вікно поверх (підміни поки немає); решта (OpenGL, DirectX 11, 10, 9, 8, DirectDraw) - показ у кадрі.
    // Запасних варіантів немає: якщо щось не вдалося, оверлей просто не показується - режим можна змінити вручну.
    static bool IsOld(string a) { return a == "dx9" || a == "dx10" || a == "dx11" || a == "opengl" || a == "dx8"; }

    static string Norm(string a) { return a == "dx" ? "" : (a ?? ""); }

    // бібліотеку потрібно впровадити: або обмеження кадрів, або оверлей (показ у кадрі / підміна повноекрану)
    public static bool NeedsLib(Game g, LimSession s)
    {
        if (g == null) return false;
        if (ActiveFor(g)) return true;
        if (!g.Ov) return false;
        int m = g.Mode;
        if (m == 2) return false;                             // лише вікно поверх гри: нічого не впроваджуємо
        string a = s != null ? ApiOf(s) : Norm(g.Api);
        if (a == "ddraw") return false;                       // DirectDraw: ні показу в кадрі, ні підміни повноекрану (лише обмеження кадрів, якщо ввімкнено)
        if (m == 0) return a.Length == 0 || CanSubst(a);      // підміна повноекрану є лише для DirectX
        return a != "vulkan" || m == 3;                       // у кадрі Vulkan бібліотеки не потребує; автоматично - потребує (підміна повноекрану)
    }

    static bool CanSubst(string a) { return a == "dx9" || a == "dx10" || a == "dx11" || a == "dx12" || a == "vulkan"; }

    // повноекран DirectX підміняти вікном без рамки
    public static bool AutoBdr(Game g, LimSession s)
    {
        if (g == null || !g.Ov || s == null) return false;
        int m = g.Mode;
        if (m == 2) return false;
        string a = ApiOf(s);
        if (m == 0) return CanSubst(a);
        return m == 3 && (a == "dx12" || a == "vulkan");           // автоматично: DirectX 12 і Vulkan - вікно + підміна повноекрану
    }

    // показники малюються прямо в кадрі гри
    public static bool InFrame(Game g, LimSession s)
    {
        int m = g != null ? g.Mode : 2;
        if (g == null || !g.Ov || s == null || !s.Injected || (m != 1 && m != 3)) return false;
        string a = ApiOf(s);
        if (a == "dx12") return m == 1;                              // автоматично DirectX 12 - вікно + підміна повноекрану
        if (a == "vulkan") return false;
        // DirectX 8: показники в кадрі лише коли перехоплено пристрій гри (а не запасний підрахунок на показі ядром)
        if (a == "dx8") { try { if ((Marshal.ReadInt32(s.View, OffHooks) & 64) == 0) return false; } catch { return false; } }
        return IsOld(a) && s.Excl;
    }

    // тека запущеного exe цієї гри ("" - гра не запущена)
    public static string ExePathOf(Game g)
    {
        foreach (LimSession s in sessions.Values)
            if (s.Game == g && s.Exe.Length > 0) return s.Exe;
        return "";
    }

    public static string ExeDirOf(Game g)
    {
        foreach (LimSession s in sessions.Values)
            if (s.Game == g && s.Exe.Length > 0) { try { return Path.GetDirectoryName(s.Exe) ?? ""; } catch { } }
        return "";
    }

    // оверлей (навіть окреме вікно) для гри підтримується: DirectDraw - ні
    public static bool OverlaySupported(int pid)
    {
        LimSession s;
        string a = "";
        if (pid > 0 && pid == noGfxPid) return false;
        if (pid > 0 && sessions.TryGetValue(pid, out s) && s != null) a = ApiOf(s);
        else { Game g = Games.ByPid(pid); if (g != null) a = Norm(g.Api); }
        return a != "ddraw";
    }

    // API гри: з бібліотеки (точно), інакше за списком завантажених модулів
    static string ApiOf(LimSession s)
    {
        if (s == null) return "";
        string d = DllApi(s);
        return d.Length > 0 ? d : s.Api;
    }

    // API, який назвала бібліотека всередині гри (точно)
    static string DllApi(LimSession s)
    {
        if (s != null && s.View != IntPtr.Zero && s.Injected)
        {
            try
            {
                int api = Marshal.ReadInt32(s.View, OffApi);
                if (api == 4) return "ddraw";
                if (api == 3) return "opengl";
                if (api == 2) return "dx9";
                if (api == 5) return "dx8";
                if (api == 6) return "vulkan";
                if (api == 1)
                {
                    int v = Marshal.ReadInt32(s.View, OffDxgiVer);
                    return v == 12 ? "dx12" : v == 10 ? "dx10" : v == 11 ? "dx11" : (s.Api.Length > 3 && s.Api.StartsWith("dx") ? s.Api : "dx11");
                }
                // кадрів ще не було: беремо те, що гра обрала під час створення пристрою / ланцюжка
                int ca = Marshal.ReadInt32(s.View, OffCreateApi);
                if (ca == 2) return "dx9";
                if (ca == 5) return "dx8";
                if (ca == 6) return "vulkan";
                if (ca == 10) return "dx10";
                if (ca == 11) return "dx11";
                if (ca == 12) return "dx12";
            }
            catch { }
        }
        return "";
    }

    // назва API для людей
    public static string ApiName(string a)
    {
        switch (a)
        {
            case "dx8": return "DirectX 8";
            case "dx9": return "DirectX 9";
            case "dx10": return "DirectX 10";
            case "dx11": return "DirectX 11";
            case "dx12": return "DirectX 12";
            case "dx": return "DirectX";
            case "opengl": return "OpenGL";
            case "vulkan": return "Vulkan";
            case "ddraw": return "DirectDraw";
        }
        return "";
    }

    // як оверлей показуватиметься в цій грі (для підпису в налаштуваннях)
    public static string ModeText(string a, int mode)
    {
        string old = L.T(" DirectDraw (старі 2D-ігри) - без оверлея.");
        if (mode == 3)
            return L.T("Автоматично: у вікні й вікні без рамки - окреме вікно поверх гри. У справжньому повноекрані DirectX 12 і Vulkan NeoBar підміняє вікном без рамки, а OpenGL і DirectX 8-11 малює прямо в кадрі гри.") + old;
        if (mode == 2) return L.T("Без інтеграції в гру: оверлей - окреме вікно поверх гри, видно у вікні й вікні без рамки (Borderless). У справжньому повноекрані його не буде - увімкніть в грі «Вікно без рамки».");
        if (mode == 1) return L.T("У вікні й вікні без рамки - окреме вікно поверх гри. У справжньому повноекрані показники малюються прямо в кадрі (OpenGL, DirectX 8-12).") + old;
        return L.T("Оверлей завжди окремим вікном поверх гри; справжній повноекран DirectX 9-12 і Vulkan NeoBar підміняє вікном без рамки.") + old;
    }

    // гра у справжньому повноекрані (OpenGL, DirectDraw): вікно займає весь монітор І режим екрана змінено грою
    // needMode: true - ще й режим екрана змінено грою; false - досить, що вікно займає весь монітор (DirectDraw: у кадрі малюємо і в безрамковому)
    static bool TrueFullscreen(LimSession s, bool needMode)
    {
        try
        {
            IntPtr fg = Win.GetForegroundWindow();
            int fp;
            Win.GetWindowThreadProcessId(fg, out fp);
            if (fp == s.Pid) s.Wnd = fg;
            else if (s.Wnd == IntPtr.Zero || !IsWindow(s.Wnd)) using (Process p = Process.GetProcessById(s.Pid)) s.Wnd = p.MainWindowHandle;
            if (s.Wnd == IntPtr.Zero) return false;
            Screen scr = Screen.FromHandle(s.Wnd);
            RECT r;
            if (!GetWindowRect(s.Wnd, out r)) return false;
            System.Drawing.Rectangle b = scr.Bounds;
            int wl = Nat.GetStyle(s.Wnd, -16);
            bool cover = Math.Abs(r.Left - b.Left) <= 2 && Math.Abs(r.Top - b.Top) <= 2 && Math.Abs(r.Right - b.Right) <= 2 && Math.Abs(r.Bottom - b.Bottom) <= 2;
            s.ExclDbg = L.T("вікно гри ") + (r.Right - r.Left) + "×" + (r.Bottom - r.Top) + L.T(" у (") + r.Left + "," + r.Top + L.T("), монітор ") + b.Width + "×" + b.Height + ", " + ((wl & 0x00C00000) != 0 ? L.T("з рамкою") : L.T("без рамки"));
            // DirectDraw / DirectX 8 у сучасній Windows: режим екрана часто лишається рідним, а гра 800×600 - у вікні без рамки зверху ліворуч
            // (його розтягує сама система). Тому тут досить вікна без рамки; вікно з рамкою - це «вікно».
            if (!needMode) return (wl & 0x00C00000) == 0 && (r.Right - r.Left) >= 320 && (r.Bottom - r.Top) >= 240 && Win.IsWindowVisible(s.Wnd);
            if (!cover) return false;
            DEVMODE cur = new DEVMODE(), reg = new DEVMODE();
            cur.dmSize = reg.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            if (!EnumDisplaySettingsW(scr.DeviceName, -1, ref cur) || !EnumDisplaySettingsW(scr.DeviceName, -2, ref reg)) return false;
            return cur.dmPelsWidth != reg.dmPelsWidth || cur.dmPelsHeight != reg.dmPelsHeight;       // гра змінила режим екрана
        }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);

    // раз на секунду: чи гра у справжньому повноекрані; чи не вийшов показ у кадрі / підміна
    static void UpdateMode(LimSession s)
    {
        bool ex = false;
        try
        {
            if (s.Injected && s.View != IntPtr.Zero && s.Game != null && s.Game.Ov)
            {
                string a = ApiOf(s);
                if (a == "dx9" || a == "dx10" || a == "dx11") ex = Marshal.ReadInt32(s.View, OffOsdExcl) == 1;
                else if (a == "opengl" || a == "dx8")
                {
                    // повноекран визначаємо із запасом: короткочасна зміна фокуса не має перемикати спосіб показу (інакше оверлей моргає)
                    if (TrueFullscreen(s, false)) s.ExclLast = DateTime.Now;
                    ex = s.ExclLast != DateTime.MinValue && (DateTime.Now - s.ExclLast).TotalSeconds < 20;
                }
                s.Excl = ex;
                return;
            }
        }
        catch { }
        s.Excl = ex;
    }

    // тека й розрядність запущеного exe гри (для обгорток DirectX 8 / DirectDraw)
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    sealed class ExitWatch { public IntPtr H; public string Name; public DateTime Start; }
    static readonly Dictionary<int, ExitWatch> exitWatch = new Dictionary<int, ExitWatch>();

    // код завершення гри: 0 - звичайний вихід, 0xC0000005 тощо - вилітання (потрібно для розбору збоїв; нічого не змінює в грі)
    static void WatchExit(Game g, int pid)
    {
        if (g == null || pid <= 0 || exitWatch.ContainsKey(pid)) return;
        IntPtr h = Win.OpenProcess(0x1000 | 0x100000, false, pid);       // QUERY_LIMITED_INFORMATION | SYNCHRONIZE
        if (h == IntPtr.Zero) return;
        ExitWatch w = new ExitWatch(); w.H = h; w.Name = g.Name; w.Start = DateTime.Now;
        exitWatch[pid] = w;
    }

    static void CheckExits()
    {
        if (exitWatch.Count == 0) return;
        List<int> done = null;
        foreach (KeyValuePair<int, ExitWatch> kv in exitWatch)
        {
            try
            {
                if (Win.WaitForSingleObject(kv.Value.H, 0) != 0) continue;
                uint code;
                if (GetExitCodeProcess(kv.Value.H, out code))
                    Fps.Log("Гра завершилась: " + kv.Value.Name + " (pid " + kv.Key + "), код 0x" + code.ToString("X8") +
                            (code == 0 ? L.T(" (звичайний вихід)") : code == 0xC0000005 ? L.T(" (порушення доступу - збій)") : code == 0xC0000409 || code == 0xC0000374 ? L.T(" (збій купи / стека)") : "") +
                            L.T(", працювала ") + Math.Round((DateTime.Now - kv.Value.Start).TotalSeconds) + L.T(" с"));
            }
            catch { }
            if (done == null) done = new List<int>();
            done.Add(kv.Key);
        }
        if (done != null) foreach (int p in done) { try { Win.CloseHandle(exitWatch[p].H); } catch { } exitWatch.Remove(p); }
    }

    static int exeNotedPid;
    static void NoteExe(Game g, int pid)
    {
        WatchExit(g, pid);
        if (g == null || exeNotedPid == pid) return;
        exeNotedPid = pid;
        if (g != null) { g.WrapFresh = false; g.WrapMiss = false; }      // нова гра - обгортка, покладена раніше, вже має діяти
        try
        {
            string exe = Games.ExeOf(pid);
            if (exe.Length == 0) return;
            string d = Path.GetDirectoryName(exe) ?? "";
            int bits = 64;
            IntPtr h = Win.OpenProcess(0x1000, false, pid);
            if (h != IntPtr.Zero)
            {
                try { bool wow = false; if (!Environment.Is64BitOperatingSystem) bits = 32; else if (IsWow64Process(h, out wow) && wow) bits = 32; }
                finally { Win.CloseHandle(h); }
            }
            g.ExeFile = exe;
            if (g.ExeDir != d || g.Bits != bits) { g.ExeDir = d; g.Bits = bits; Games.Save(); }
        }
        catch { }
    }

    public static bool IsRunning(Game g)
    {
        foreach (LimSession s in sessions.Values) if (s.Game == g) return true;
        return g != null && Games.CurrentPid > 0 && Games.ByPid(Games.CurrentPid) == g;
    }

    public static void LogWrap(Game g, string msg) { try { Fps.Log("Обгортка: " + g.Name + ": " + msg); } catch { } }

    // запам'ятати API гри: показується біля назви в списку ігор
    static void RememberApi(Game g, LimSession s)
    {
        if (g == null || s == null) return;
        string a = DllApi(s);
        if (a.Length == 0 && s.Api.Length > 0 && s.ApiSince != DateTime.MinValue && (DateTime.Now - s.ApiSince).TotalSeconds > 15) a = s.Api;
        if (a.Length == 0) return;
        if (g.Api != a) { g.Api = a; Games.Save(); }
    }

    // гра без ввімкнених можливостей: API дізнаємось один раз зі списку модулів, щоб показати його в списку
    static DateTime learnAt = DateTime.MinValue, learnBorn = DateTime.MinValue;
    static int learnPid;
    static void LearnApi(Game g, int pid)
    {
        if (learnPid != pid) { learnPid = pid; learnBorn = DateTime.Now; learnAt = DateTime.MinValue; }
        // невідомий API або перші секунди гри - перевіряємо часто; відомий - рідко (виправити помилкове визначення, коли гра довантажила графіку)
        bool young = (DateTime.Now - learnBorn).TotalSeconds < 60;
        if ((DateTime.Now - learnAt).TotalSeconds < (g.Api.Length == 0 || young ? 1.5 : 20)) return;
        learnAt = DateTime.Now;
        bool vk;
        string a = DetectApi(pid, out vk);
        if (a.Length > 0 && g.Api != a) { g.Api = a; Games.Save(); }
        // процес без жодного графічного API (лаунчер, налаштування) через 15 с після появи - оверлей для нього не показуємо
        if (a.Length == 0) { if ((DateTime.Now - learnBorn).TotalSeconds > 15 && lastAllMods.Length > 0) noGfxPid = pid; }
        else if (noGfxPid == pid) noGfxPid = 0;
    }
    static int noGfxPid;

    static void UpdateApi(LimSession s)
    {
        if ((DateTime.Now - s.ApiAt).TotalSeconds < (s.Api.Length == 0 || (DateTime.Now - s.Born).TotalSeconds < 60 ? 1.5 : 12)) return;
        s.ApiAt = DateTime.Now;
        bool vk;
        string a = DetectApi(s.Pid, out vk);
        s.Others = lastOthers;
        // Vulkan-ігри нерідко підтягують і DirectX-модулі: якщо бібліотека за 20 с не побачила жодного кадру, а vulkan-1.dll завантажено - це Vulkan
        try
        {
            if (vk && a.StartsWith("dx") && s.Injected && s.View != IntPtr.Zero && Marshal.ReadInt32(s.View, OffFrames) == 0 &&
                s.InjAt != DateTime.MinValue && (DateTime.Now - s.InjAt).TotalSeconds > 20) a = "vulkan";
        }
        catch { }
        if (a.Length > 0) { if (a != s.Api) s.ApiSince = DateTime.Now; s.Api = a; }
    }

    static string lastOthers = "";

    // інші програми, що малюють свій оверлей у грі (за завантаженими модулями): можливі подвійні показники й мерехтіння
    static string OtherOverlays(List<string> names)
    {
        List<string> r = new List<string>();
        if (names.Contains("gameoverlayrenderer64.dll") || names.Contains("gameoverlayrenderer.dll")) r.Add("Steam");
        if (names.Contains("rtsshooks64.dll") || names.Contains("rtsshooks.dll")) r.Add("RTSS/MSI Afterburner");
        if (names.Contains("discordhook64.dll") || names.Contains("discordhook.dll")) r.Add("Discord");
        if (names.Contains("nvspcap64.dll") || names.Contains("nvspcap.dll")) r.Add("NVIDIA");
        if (names.Contains("overwolf.gameoverlay.dll") || names.Contains("owclient.dll")) r.Add("Overwolf");
        return string.Join(", ", r.ToArray());
    }

    static string DetectApi(int pid, out bool vulkan)
    {
        vulkan = false;
        IntPtr h = Win.OpenProcess(0x0410, false, pid);          // QUERY_INFORMATION | VM_READ
        if (h == IntPtr.Zero) return "";
        List<string> names = new List<string>();
        List<string> paths = new List<string>();
        try
        {
            IntPtr[] mods = new IntPtr[2048];
            int need;
            if (!EnumProcessModulesEx(h, mods, mods.Length * IntPtr.Size, out need, 0x03)) return "";
            int n = Math.Min(mods.Length, need / IntPtr.Size);
            StringBuilder sb = new StringBuilder(260);
            for (int i = 0; i < n; i++)
            {
                sb.Length = 0;
                if (GetModuleBaseNameW(h, mods[i], sb, sb.Capacity) > 0)
                {
                    string nm = sb.ToString().ToLowerInvariant();
                    names.Add(nm);
                    if (nm == "ddraw.dll" || nm == "d3d8.dll" || nm == "d3d9.dll" || nm == "opengl32.dll")
                    {
                        StringBuilder pb = new StringBuilder(520);
                        if (GetModuleFileNameExW(h, mods[i], pb, pb.Capacity) > 0) paths.Add(nm.Replace(".dll", "") + "=" + pb.ToString());
                    }
                }
            }
        }
        catch { return ""; }
        finally { Win.CloseHandle(h); }
        vulkan = names.Contains("vulkan-1.dll");
        lastMods = string.Join("; ", paths.ToArray());
        lastAllMods = string.Join(" ", names.ToArray());
        lastOthers = OtherOverlays(names);
        if (names.Contains("d3d12.dll")) return "dx12";
        if (names.Contains("d3d11.dll")) return "dx11";
        if (names.Contains("d3d10.dll") || names.Contains("d3d10_1.dll") || names.Contains("d3d10core.dll")) return "dx10";
        if (names.Contains("d3d9.dll")) return "dx9";
        if (names.Contains("d3d8.dll")) return "dx8";          // DirectX 8 (Unreal Engine 2 тощо) підтягує ddraw.dll, але сам не DirectDraw
        if (vulkan) return "vulkan";
        if (names.Contains("opengl32.dll")) return "opengl";
        if (names.Contains("ddraw.dll")) return "ddraw";
        return "";
    }

    // ---------- щосекунди (з таймера ігор) ----------
    public static void Tick()
    {
        // закриті ігри
        List<int> dead = null;
        foreach (KeyValuePair<int, LimSession> kv in sessions)
            if (!Games.Alive(kv.Key)) { if (dead == null) dead = new List<int>(); dead.Add(kv.Key); }
        if (dead != null) foreach (int p in dead) Close(p);

        VkLayer.Tick();
        CheckExits();
        int pid = Games.CurrentPid;
        if (pid > 0)
        {
            Game g = Games.ByPid(pid);
            LimSession s;
            sessions.TryGetValue(pid, out s);
            NoteExe(g, pid);
            // для статистики FPS теж корисно мати сесію, але впроваджуємо лише туди, де дозволено обмеження
            if (s == null && NeedsLib(g, null)) s = Open(pid, g);
            if (s != null)
            {
                s.Game = g;
                UpdateApi(s);
                RememberApi(g, s);
                if (!s.Injected && !s.Injecting && s.Blocked.Length == 0 && NeedsLib(g, s) && DateTime.Now >= s.NextTry) TryInject(s);
            }
            else if (g != null) LearnApi(g, pid);
            GameWrap.Sync(g);
            WrapDiag(g, pid);
        }
        foreach (LimSession s in sessions.Values) Push(s);
        if ((DateTime.Now - syncAllAt).TotalSeconds > 30) { syncAllAt = DateTime.Now; try { GameWrap.SyncAll(); } catch { } }     // обгортки лягають у теки відомих ігор ще до запуску
        if (SbLog.On(SbLog.Games)) DiagLog();
    }

    static DateTime syncAllAt = DateTime.MinValue;

    static string lastDiag = "";
    static DateTime nextDiag = DateTime.MinValue;
    // діагностика в neobar-fps.log: стан бібліотеки поточної гри (лише при зміні, не частіше за раз на 2 с)
    static void DiagLog()
    {
        if (DateTime.Now < nextDiag) return;
        nextDiag = DateTime.Now.AddSeconds(2);
        try
        {
            LimSession s = null;
            if (Games.CurrentPid > 0 && (!sessions.TryGetValue(Games.CurrentPid, out s) || s == null || !s.Injected || s.View == IntPtr.Zero))
            {
                // бібліотеку в гру не впроваджено: записуємо, чому (щоб було видно у журналі)
                Game g0 = Games.ByPid(Games.CurrentPid);
                string nd = g0 == null ? L.T("гри немає у списку") :
                    g0.Name + L.T(": не впроваджено; api=") + g0.Api + L.T(" обгортка=") + (g0.Wrap.Length > 0 ? g0.Wrap : L.T("немає")) + (g0.WrapFresh ? L.T("(свіжа)") : "") + " mode=" + g0.Mode + " ov=" + g0.Ov + " lim=" + g0.Limit + " integr=" + g0.Integr + " ac=[" + g0.Ac + "] acOk=" + g0.AcOk
                    + L.T(" потрібна бібліотека=") + NeedsLib(g0, s) + (s != null && s.Blocked.Length > 0 ? L.T(" причина=") + s.Blocked : "") + (s != null && s.Error.Length > 0 ? L.T(" помилка=") + s.Error : "");
                if (nd != lastDiag) { lastDiag = nd; Fps.Log("Діагностика: " + nd); }
                return;
            }
            if (Games.CurrentPid <= 0 || s == null) return;
            IntPtr v = s.View;
            string d = (s.Game != null ? s.Game.Name : "?") + ": mode=" + (s.Game != null ? s.Game.Mode : -1) + " api=" + ApiOf(s) + " hooks=" + Marshal.ReadInt32(v, OffHooks)
                + " d3d=" + Marshal.ReadInt32(v, OffD3d) + " dxgi=" + Marshal.ReadInt32(v, OffDxgiVer) + " osdApi=" + Marshal.ReadInt32(v, OffOsdApi)
                + " osdOn=" + Marshal.ReadInt32(v, OffOsdOn) + " drawn=" + (Marshal.ReadInt32(v, OffOsdDrawn) > 0 ? L.T("є") : "0") + " err=" + Marshal.ReadInt32(v, OffOsdErr)
                + " excl(dll)=" + Marshal.ReadInt32(v, OffOsdExcl) + " excl=" + s.Excl + " fmt=" + Marshal.ReadInt32(v, 4764) + " msaa=" + Marshal.ReadInt32(v, 4768)
                + " frameW=" + Marshal.ReadInt32(v, OffOsdFrameW) + " frameH=" + Marshal.ReadInt32(v, OffOsdFrameH) + " vk=" + (Marshal.ReadInt32(v, 4756) > 0 ? L.T("є") : "0") + " dxFrames=" + (Marshal.ReadInt32(v, 4760) > 0 ? L.T("є") : "0")
                + L.T(" ліміт(ввімк=") + Marshal.ReadInt32(v, OffEnabled) + L.T(" ціль=") + (Marshal.ReadInt32(v, OffTarget) / 1000) + L.T(" кадри=") + (Marshal.ReadInt32(v, OffFrames) > 0 ? L.T("є") : L.T("немає")) + ")"
                + " created=" + Marshal.ReadInt32(v, OffCreateApi) + " sc(effect=" + Marshal.ReadInt32(v, 4780) + " flags=" + Marshal.ReadInt32(v, 4784) + " fmt=" + Marshal.ReadInt32(v, 4788)
                + " windowed=" + Marshal.ReadInt32(v, 4792) + " bufs=" + Marshal.ReadInt32(v, 4796) + ") others=" + s.Others + " d8hr=0x" + Marshal.ReadInt32(v, 4772).ToString("X8") + " inFrame=" + InFrame(s.Game, s) + " [" + s.ExclDbg + "]";
            if (d == lastDiag) return;
            lastDiag = d;
            Fps.Log("Діагностика: " + d);
        }
        catch { }
    }

    // записати в гру ліміт і стан
    static void Push(LimSession s)
    {
        if (s.View == IntPtr.Zero) return;
        UpdateMode(s);
        bool on = ActiveFor(s.Game);
        Marshal.WriteInt32(s.View, OffTarget, (int)Math.Round(TargetFor(s) * 1000));
        Marshal.WriteInt32(s.View, OffEnabled, on ? 1 : 0);
        Marshal.WriteInt32(s.View, OffBorderless, s.Game != null && AutoBdr(s.Game, s) ? 1 : 0);
        PushOsd(s);
    }

    static void PushOsd(LimSession s)
    {
        Marshal.WriteInt32(s.View, OffOsdOn, InFrame(s.Game, s) ? 1 : 0);
    }

    // ---------- картинка для показу в кадрі ----------
    const int ImgHdr = 64, ImgMax = 8 * 1024 * 1024;
    const uint ImgMagic = 0x4D494253;         // 'SBIM'

    // гру треба показувати в кадрі: даємо розмір її кадру (0, поки бібліотека ще нічого не намалювала)
    public static bool FrameWanted(int pid, out int fw, out int fh)
    {
        fw = fh = 0;
        LimSession s;
        if (pid <= 0 || !sessions.TryGetValue(pid, out s) || s.View == IntPtr.Zero || !s.Injected) return false;
        if (!InFrame(s.Game, s)) return false;
        try { fw = Marshal.ReadInt32(s.View, OffOsdFrameW); fh = Marshal.ReadInt32(s.View, OffOsdFrameH); }
        catch { fw = fh = 0; }
        return true;
    }

    static bool OpenImg(LimSession s)
    {
        string name = "Local\\NeoBarOsd_" + s.Pid;
        try
        {
            s.ImgMap = OpenFileMappingW(FILE_MAP_ALL, false, name);
            bool existed = s.ImgMap != IntPtr.Zero;
            if (!existed)
            {
                IntPtr sd;
                SECURITY_ATTRIBUTES sa = new SECURITY_ATTRIBUTES();
                sa.nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES));
                if (ConvertStringSecurityDescriptorToSecurityDescriptorW("D:(A;;GA;;;WD)S:(ML;;NW;;;LW)", 1, out sd, IntPtr.Zero)) sa.lpSecurityDescriptor = sd;
                else sd = IntPtr.Zero;
                s.ImgMap = CreateFileMappingW(new IntPtr(-1), ref sa, 0x04, 0, (uint)(ImgHdr + ImgMax), name);
                if (sd != IntPtr.Zero) LocalFree(sd);
            }
            if (s.ImgMap == IntPtr.Zero) return false;
            s.ImgView = MapViewOfFile(s.ImgMap, FILE_MAP_ALL, 0, 0, new UIntPtr((uint)(ImgHdr + ImgMax)));
            if (s.ImgView == IntPtr.Zero) { Win.CloseHandle(s.ImgMap); s.ImgMap = IntPtr.Zero; return false; }
            Marshal.WriteInt32(s.ImgView, 0, unchecked((int)ImgMagic));
            return true;
        }
        catch { return false; }
    }

    // передати грі готову картинку оверлею (null - нічого не малювати)
    public static void PushFrame(int pid, System.Drawing.Bitmap b, int corner, int mx, int my)
    {
        LimSession s;
        if (!sessions.TryGetValue(pid, out s) || s.View == IntPtr.Zero) return;
        if (s.ImgView == IntPtr.Zero && !OpenImg(s)) return;
        IntPtr v = s.ImgView;
        int seq = ++s.ImgSeq;
        if (seq == 0) seq = ++s.ImgSeq;
        Marshal.WriteInt32(v, 4, seq);                        // seqA: починаємо запис
        int show = 0;
        if (b != null && b.Width > 0 && b.Height > 0 && (long)b.Width * b.Height * 4 <= ImgMax)
        {
            System.Drawing.Imaging.BitmapData d = b.LockBits(new System.Drawing.Rectangle(0, 0, b.Width, b.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);      // прозорість попередньо помножена: так змішує DLL
            try
            {
                IntPtr dst = new IntPtr(v.ToInt64() + ImgHdr);
                int rowB = b.Width * 4;
                if (d.Stride == rowB) MoveMem(dst, d.Scan0, new UIntPtr((uint)(rowB * b.Height)));
                else for (int r = 0; r < b.Height; r++) MoveMem(new IntPtr(dst.ToInt64() + (long)r * rowB), new IntPtr(d.Scan0.ToInt64() + (long)r * d.Stride), new UIntPtr((uint)rowB));
                Marshal.WriteInt32(v, 8, b.Width);
                Marshal.WriteInt32(v, 12, b.Height);
                show = 1;
            }
            finally { b.UnlockBits(d); }
        }
        Marshal.WriteInt32(v, 16, corner);
        Marshal.WriteInt32(v, 20, Math.Max(0, mx));
        Marshal.WriteInt32(v, 24, Math.Max(0, my));
        Marshal.WriteInt32(v, 28, show);
        Marshal.WriteInt32(v, 36, Environment.TickCount);
        Marshal.WriteInt32(v, 32, seq);                       // seqB: запис завершено
    }

    // у DirectDraw кадр не перемальовується щоразу, тож прозорий напис накопичувався б: там суцільний фон
    public static bool FrameOpaque(int pid)
    {
        LimSession s;
        return sessions.TryGetValue(pid, out s) && s.Game != null && s.Game.Api == "ddraw";
    }

    // показники малюються (або от-от почнуть малюватися) в кадрі гри: окреме вікно оверлею тоді не потрібне.
    // Поки бібліотека ще не намалювала першого кадру, вікно не показуємо (десять секунд), щоб воно не з'являлось і не зникало.
    // вікно оверлею не показуємо, поки бібліотеку в гру ще підключають: вона має спершу з'ясувати, чи це справжній повноекран
    // (вікно поверх гри під час створення пристрою або зміни режиму змушує деякі старі DirectX-ігри згортатися). Не довше 20 с.
    public static bool WindowMustWait(int pid)
    {
        LimSession s;
        if (pid <= 0 || !sessions.TryGetValue(pid, out s) || s.Injected || s.Blocked.Length > 0) return false;
        Game g = s.Game;
        if (g == null || !NeedsLib(g, s)) return false;
        return (DateTime.Now - s.Born).TotalSeconds < 20;
    }

    public static bool EmbedDrawing(int pid)
    {
        LimSession s;
        if (pid <= 0 || !sessions.TryGetValue(pid, out s) || s.View == IntPtr.Zero || !s.Injected) return false;
        try
        {
            if (!InFrame(s.Game, s)) { s.PendSince = DateTime.MinValue; return false; }
            if (Marshal.ReadInt32(s.View, OffOsdErr) != 0) return false;
            if (Marshal.ReadInt32(s.View, OffOsdDrawn) <= 0)
            {
                if (s.PendSince == DateTime.MinValue) s.PendSince = DateTime.Now;
                return (DateTime.Now - s.PendSince).TotalSeconds < 10;
            }
            if (s.Game != null && s.Game.Api == "ddraw") return true;     // DirectDraw: гра може не показувати кадри по кілька секунд (меню) - вікно не повертаємо, щоб не мерехтіло
            long freq = Marshal.ReadInt64(s.View, OffFreq), last = Marshal.ReadInt64(s.View, OffLast), now;
            if (freq <= 0 || last == 0) return false;
            QueryPerformanceCounter(out now);
            return (now - last) * 1000.0 / freq < (s.Game != null && s.Game.Api == "ddraw" ? 6000 : 2000);
        }
        catch { return false; }
    }

    // гаряча клавіша: увімкнути / вимкнути обмеження
    public static string Toggle()
    {
        Game g = Games.ByPid(Games.CurrentPid);
        // у поточній грі обмеження ще не ввімкнено: гаряча клавіша сама ставить галку «Обмеження FPS» цієї гри
        // (у грі з античітом - ні: інтеграція там лише за явним вибором у налаштуваннях гри)
        bool auto = g != null && !g.LimitOn && g.Integr;
        if (auto)
        {
            g.LimitOn = true;
            OverlayCfg.LimOn = true;
            try { Games.Save(); } catch { }
        }
        else OverlayCfg.LimOn = !OverlayCfg.LimOn;
        foreach (LimSession s in sessions.Values) Push(s);
        if (OverlayCfg.LimOn) Tick();
        if (!OverlayCfg.LimOn) return L.T("Обмеження FPS вимкнено");
        if (g != null && !g.Integr) return L.T("Обмеження FPS не застосовано: у грі античіт. Увімкніть інтеграцію у налаштуваннях гри");
        LimSession cur = null;
        if (Games.CurrentPid > 0)
        {
            if (sessions.TryGetValue(Games.CurrentPid, out cur) && cur.Blocked.Length > 0) return L.T("Обмеження FPS недоступне: ") + cur.Blocked;
            if (!ActiveFor(g)) return L.T("Обмеження FPS увімкнено, але не для цієї гри");
        }
        return (auto ? L.T("Обмеження FPS для цієї гри увімкнено: ") : L.T("Обмеження FPS: ")) + (cur != null ? FpsText(TargetFor(cur)) : L.T("увімкнено"));
    }

    public static void Shutdown()
    {
        VkLayer.Shutdown();
        // гра лишається з бібліотекою, але без обмеження
        foreach (LimSession s in sessions.Values)
            try { if (s.View != IntPtr.Zero) Marshal.WriteInt32(s.View, OffEnabled, 0); } catch { }
        foreach (int p in new List<int>(sessions.Keys)) Close(p);
    }

    // ---------- спільна пам'ять ----------
    static LimSession Open(int pid, Game g)
    {
        LimSession s = new LimSession();
        s.Pid = pid;
        s.Game = g;
        s.Exe = Games.ExeOf(pid);
        string name = "Local\\NeoBarFps_" + pid;
        try
        {
            s.Map = OpenFileMappingW(FILE_MAP_ALL, false, name);
            bool existed = s.Map != IntPtr.Zero;
            if (!existed)
            {
                // доступ для всіх і низький рівень цілісності: гра без прав адміністратора теж відкриє пам'ять,
                // навіть якщо NeoBar запущено з правами адміністратора
                IntPtr sd;
                SECURITY_ATTRIBUTES sa = new SECURITY_ATTRIBUTES();
                sa.nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES));
                if (ConvertStringSecurityDescriptorToSecurityDescriptorW("D:(A;;GA;;;WD)S:(ML;;NW;;;LW)", 1, out sd, IntPtr.Zero)) sa.lpSecurityDescriptor = sd;
                else sd = IntPtr.Zero;
                s.Map = CreateFileMappingW(new IntPtr(-1), ref sa, 0x04 /*PAGE_READWRITE*/, 0, Size, name);
                if (sd != IntPtr.Zero) LocalFree(sd);
            }
            if (s.Map == IntPtr.Zero) { s.Error = L.T("спільна пам'ять недоступна"); s.Blocked = s.Error; }
            else
            {
                s.View = MapViewOfFile(s.Map, FILE_MAP_ALL, 0, 0, new UIntPtr((uint)Size));
                if (s.View == IntPtr.Zero) { s.Error = L.T("спільна пам'ять недоступна"); s.Blocked = s.Error; }
                else if ((uint)Marshal.ReadInt32(s.View, OffMagic) == Magic && existed)
                {
                    // бібліотека вже в грі (наприклад, NeoBar перезапускали)
                    s.Injected = true; s.InjAt = DateTime.Now;
                }
                else
                {
                    Marshal.WriteInt32(s.View, OffVersion, 1);
                    Marshal.WriteInt32(s.View, OffEnabled, 0);
                    Marshal.WriteInt32(s.View, OffTarget, 0);
                    Marshal.WriteInt32(s.View, OffMagic, unchecked((int)Magic));
                }
            }
        }
        catch (Exception ex) { s.Error = ex.Message; s.Blocked = s.Error; }
        sessions[pid] = s;
        return s;
    }

    static void Close(int pid)
    {
        LimSession s;
        if (!sessions.TryGetValue(pid, out s)) return;
        sessions.Remove(pid);
        try { if (s.View != IntPtr.Zero) UnmapViewOfFile(s.View); } catch { }
        try { if (s.Map != IntPtr.Zero) Win.CloseHandle(s.Map); } catch { }
        try { if (s.ImgView != IntPtr.Zero) UnmapViewOfFile(s.ImgView); } catch { }
        try { if (s.ImgMap != IntPtr.Zero) Win.CloseHandle(s.ImgMap); } catch { }
    }

    // ---------- впровадження ----------
    static void TryInject(LimSession s)
    {
        if (s.Injecting) return;
        s.Injecting = true;
        s.Tries++;
        s.NextTry = DateTime.Now.AddSeconds(s.Tries < 3 ? 5 : 60);
        // перевірка античіта й саме впровадження - у фоні: гра, що вантажиться, може не відповідати до 8 с
        Util.Bg(delegate
        {
            try { InjectNow(s); }
            finally { s.Injecting = false; }
        });
    }

    static void InjectNow(LimSession s)
    {
        string why = AntiCheat.Check(s.Pid, s.Exe, s.Game);
        if (why.Length > 0)
        {
            s.Blocked = why;
            LastNote = (s.Game != null ? s.Game.Name : Path.GetFileName(s.Exe)) + L.T(": бібліотеку не підключено - ") + why;
            Fps.Log("Обмежувач: " + LastNote);
            return;
        }
        string err = Inject(s.Pid);
        if (err.Length == 0)
        {
            s.Injected = true; s.InjAt = DateTime.Now;
            s.Error = "";
            LastNote = "";
            Fps.Log("Обмежувач: бібліотеку завантажено в " + Path.GetFileName(s.Exe) + " (pid " + s.Pid + ")");
        }
        else
        {
            s.Error = err;
            LastNote = (s.Game != null ? s.Game.Name : Path.GetFileName(s.Exe)) + ": " + err;
            Fps.Log("Обмежувач: " + LastNote);
            if (s.Tries >= 4) s.Blocked = err;
        }
    }

    static string Inject(int pid)
    {
        IntPtr h = Win.OpenProcess(PROCESS_INJECT, false, pid);
        if (h == IntPtr.Zero)
        {
            int e = Marshal.GetLastWin32Error();
            return e == 5 ? (Elevated.IsAdmin ? L.T("гра захищена від стороннього доступу") : L.T("гра запущена від імені адміністратора - запустіть так само й NeoBar")) : L.T("немає доступу до гри (код ") + e + ")";
        }
        IntPtr mem = IntPtr.Zero;
        try
        {
            bool wow = false;
            if (Environment.Is64BitOperatingSystem) IsWow64Process(h, out wow);
            bool target32 = !Environment.Is64BitOperatingSystem || wow;
            if (!target32 && IntPtr.Size == 4) return L.T("64-бітну гру не можна обробити з 32-бітного NeoBar");
            string dll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, target32 ? "NeoBarFps32.dll" : "NeoBarFps64.dll");
            if (!File.Exists(dll)) return L.T("немає файлу ") + Path.GetFileName(dll) + L.T(" поруч із NeoBar.exe");

            IntPtr loadLib;
            if (target32 == (IntPtr.Size == 4)) loadLib = Win.GetProcAddress(GetModuleHandleW("kernel32.dll"), "LoadLibraryW");
            else loadLib = RemoteProc32(h, "kernel32.dll", "LoadLibraryW", 0);
            if (loadLib == IntPtr.Zero) return L.T("не знайдено LoadLibraryW у грі (гра ще запускається?)");

            byte[] path = Encoding.Unicode.GetBytes(dll + "\0");
            mem = VirtualAllocEx(h, IntPtr.Zero, new UIntPtr((uint)path.Length), 0x3000 /*COMMIT|RESERVE*/, 0x04 /*READWRITE*/);
            if (mem == IntPtr.Zero) return L.T("не вдалося виділити пам'ять у грі");
            UIntPtr wr;
            if (!WriteProcessMemory(h, mem, path, new UIntPtr((uint)path.Length), out wr)) return L.T("не вдалося записати шлях у гру");
            uint tid;
            IntPtr th = CreateRemoteThread(h, IntPtr.Zero, UIntPtr.Zero, loadLib, mem, 0, out tid);
            if (th == IntPtr.Zero) return L.T("гра не дозволила запустити потік (код ") + Marshal.GetLastWin32Error() + ")";
            try
            {
                if (Win.WaitForSingleObject(th, 8000) != 0) return L.T("гра не відповіла вчасно");
                uint code;
                // код - лише нижні 32 біти адреси модуля, тож нуль ще не означає невдачу: перевіряємо список модулів
                if ((!GetExitCodeThread(th, out code) || code == 0) && !ModuleLoaded(h, Path.GetFileName(dll)))
                    return L.T("гра не завантажила бібліотеку (антивірус або захист гри; у Counter-Strike 2 бібліотеку пускає лише параметр запуску -allow_third_party_software, який вимикає Trusted Mode)");
            }
            finally { Win.CloseHandle(th); }
            return "";
        }
        catch (Exception ex) { return ex.Message; }
        finally
        {
            if (mem != IntPtr.Zero) VirtualFreeEx(h, mem, UIntPtr.Zero, 0x8000 /*RELEASE*/);
            Win.CloseHandle(h);
        }
    }

    // ---------- адреса функції в 32-бітній грі (з 64-бітного NeoBar) ----------
    static byte[] Read(IntPtr h, long addr, int n)
    {
        byte[] b = new byte[n];
        UIntPtr rd;
        if (!ReadProcessMemory(h, new IntPtr(addr), b, new UIntPtr((uint)n), out rd) || (int)rd.ToUInt32() != n) return null;
        return b;
    }

    static bool ModuleLoaded(IntPtr h, string name)
    {
        IntPtr[] mods = new IntPtr[2048];
        int need;
        if (!EnumProcessModulesEx(h, mods, mods.Length * IntPtr.Size, out need, 0x03 /*LIST_MODULES_ALL*/)) return false;
        int n = Math.Min(mods.Length, need / IntPtr.Size);
        StringBuilder sb = new StringBuilder(260);
        for (int i = 0; i < n; i++)
        {
            sb.Length = 0;
            if (GetModuleBaseNameW(h, mods[i], sb, sb.Capacity) > 0 && string.Equals(sb.ToString(), name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    static long Module32(IntPtr h, string name)
    {
        IntPtr[] mods = new IntPtr[1024];
        int need;
        if (!EnumProcessModulesEx(h, mods, mods.Length * IntPtr.Size, out need, 0x01 /*LIST_MODULES_32BIT*/)) return 0;
        int n = Math.Min(mods.Length, need / IntPtr.Size);
        StringBuilder sb = new StringBuilder(260);
        for (int i = 0; i < n; i++)
        {
            sb.Length = 0;
            if (GetModuleBaseNameW(h, mods[i], sb, sb.Capacity) == 0) continue;
            if (string.Equals(sb.ToString(), name, StringComparison.OrdinalIgnoreCase)) return mods[i].ToInt64();
        }
        return 0;
    }

    static IntPtr RemoteProc32(IntPtr h, string module, string func, int depth)
    {
        if (depth > 3) return IntPtr.Zero;
        long b = Module32(h, module);
        if (b == 0) return IntPtr.Zero;
        byte[] dos = Read(h, b, 0x40);
        if (dos == null) return IntPtr.Zero;
        int lfanew = BitConverter.ToInt32(dos, 0x3C);
        byte[] nt = Read(h, b + lfanew, 0x100);
        if (nt == null) return IntPtr.Zero;
        ushort magic = BitConverter.ToUInt16(nt, 24);
        int dirOff = 24 + (magic == 0x20b ? 112 : 96);
        uint expRva = BitConverter.ToUInt32(nt, dirOff), expSize = BitConverter.ToUInt32(nt, dirOff + 4);
        if (expRva == 0) return IntPtr.Zero;
        byte[] ex = Read(h, b + expRva, 40);
        if (ex == null) return IntPtr.Zero;
        int nNames = BitConverter.ToInt32(ex, 0x18);
        uint fRva = BitConverter.ToUInt32(ex, 0x1C), nRva = BitConverter.ToUInt32(ex, 0x20), oRva = BitConverter.ToUInt32(ex, 0x24);
        if (nNames <= 0 || nNames > 100000) return IntPtr.Zero;
        byte[] names = Read(h, b + nRva, nNames * 4), ords = Read(h, b + oRva, nNames * 2);
        if (names == null || ords == null) return IntPtr.Zero;
        for (int i = 0; i < nNames; i++)
        {
            uint nr = BitConverter.ToUInt32(names, i * 4);
            byte[] nm = Read(h, b + nr, func.Length + 1);
            if (nm == null || nm[func.Length] != 0 || Encoding.ASCII.GetString(nm, 0, func.Length) != func) continue;
            int ord = BitConverter.ToUInt16(ords, i * 2);
            byte[] fa = Read(h, b + fRva + ord * 4, 4);
            if (fa == null) return IntPtr.Zero;
            uint rva = BitConverter.ToUInt32(fa, 0);
            if (rva >= expRva && rva < expRva + expSize)
            {
                // переадресація на іншу бібліотеку: "KERNELBASE.LoadLibraryW"
                byte[] fw = Read(h, b + rva, 128);
                if (fw == null) return IntPtr.Zero;
                int z = Array.IndexOf(fw, (byte)0);
                string s = Encoding.ASCII.GetString(fw, 0, z < 0 ? fw.Length : z);
                int dot = s.IndexOf('.');
                if (dot <= 0) return IntPtr.Zero;
                return RemoteProc32(h, s.Substring(0, dot) + ".dll", s.Substring(dot + 1), depth + 1);
            }
            return new IntPtr(b + rva);
        }
        return IntPtr.Zero;
    }

    // ---------- статистика кадрів з гри ----------
    public static FrameStats Stats(int pid)
    {
        FrameStats st = new FrameStats();
        LimSession s;
        if (pid <= 0 || !sessions.TryGetValue(pid, out s) || s.View == IntPtr.Zero) return st;
        try
        {
            IntPtr v = s.View;
            if ((uint)Marshal.ReadInt32(v, OffMagic) != Magic) return st;
            long freq = Marshal.ReadInt64(v, OffFreq), last = Marshal.ReadInt64(v, OffLast), now;
            if (freq <= 0 || last == 0) return st;
            QueryPerformanceCounter(out now);
            if ((now - last) * 1000.0 / freq > 1500) return st;      // кадрів давно не було
            int frames = Marshal.ReadInt32(v, OffFrames);
            int take = Math.Min(Ring - 8, frames);
            if (take < 3) return st;
            List<float> iv = new List<float>(take);
            double sum = 0;
            for (int i = 0; i < take; i++)
            {
                int idx = (int)((uint)(frames - 1 - i) % Ring);
                float ms = BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(v, OffFt + idx * 4)), 0);
                if (ms <= 0 || ms > 2000 || float.IsNaN(ms)) continue;
                iv.Add(ms);
                sum += ms;
                if (sum > 10000) break;
            }
            iv.Reverse();
            Fps.FromIntervals(st, iv);
            if (!st.Valid) return st;
            st.Source = L.T("NeoBar (перехоплення кадрів)");
            int api = Marshal.ReadInt32(v, OffApi), d3d = Marshal.ReadInt32(v, OffD3d);
            st.Api = api == 4 ? "DirectDraw" : api == 2 ? "DirectX 9" : api == 5 ? "DirectX 8" : api == 6 ? "Vulkan" : (api == 3 ? "OpenGL" : (api == 1 ? (d3d >= 10 ? "DirectX " + d3d : "DirectX") : ""));
        }
        catch { st = new FrameStats(); }
        return st;
    }

    // ---------- стан для налаштувань ----------
    static string BdrText(IntPtr view)
    {
        int st = Marshal.ReadInt32(view, OffBdrState), n = Marshal.ReadInt32(view, OffBdrCount);
        if (st == 3) return L.T("повноекран не вдалося підмінити (кадри виводяться не з потоку вікна) - перемкніть режим екрана в грі");
        if (st == 2)
        {
            int api = Marshal.ReadInt32(view, OffBdrApi), tg = Marshal.ReadInt32(view, OffBdrTarget), ws = Marshal.ReadInt32(view, OffBdrWin), fx = Marshal.ReadInt32(view, OffBdrFixes);
            return L.T("повноекран підмінено вікном без рамки (") + (api == 3 ? "Vulkan" : api == 2 ? "DirectX 9" : "DirectX 10-12") + L.T(", ціль ") + (tg >> 16) + "×" + (tg & 0xFFFF) +
                   (ws != 0 ? L.T(", вікно зараз ") + (ws >> 16) + "×" + (ws & 0xFFFF) : "") + (fx > 0 ? L.T(", гра повертала вікно назад: ") + fx : "") + (n > 1 ? L.T(", разів: ") + n : "") + ")";
        }
        if (st == 1) return L.T("перехоплення стоять: якщо гра перейде в повноекран - його буде підмінено вікном без рамки (DirectX 9 - лише при створенні пристрою або зміні режиму екрана в грі; Vulkan - коли гра просить повноекран через розширення або зміну режиму екрана); якщо гра вже у вікні без рамки, нічого не змінюється");
        if (Marshal.ReadInt32(view, OffApi) == 3) return L.T("OpenGL: підмінити повноекран поки що не вміємо (лише DirectX 9-12)");
        return L.T("очікування графіки гри…");
    }

    static string EmbedStatus(IntPtr view, int hooks)
    {
        int drawn = Marshal.ReadInt32(view, OffOsdDrawn), api = Marshal.ReadInt32(view, OffOsdApi), err = Marshal.ReadInt32(view, OffOsdErr),
            kind = Marshal.ReadInt32(view, OffDdKind), cur = Marshal.ReadInt32(view, OffApi);
        string dd = "";
        if (cur == 4 || api == 4)
        {
            int calls = Marshal.ReadInt32(view, OffDdCalls), fail = Marshal.ReadInt32(view, OffDdFail), hr = Marshal.ReadInt32(view, OffDdHr), box = Marshal.ReadInt32(view, OffDdBox);
            dd = L.T(" [кадрів ") + calls + L.T(", малювань ") + drawn + L.T(", збоїв ") + fail + (fail > 0 ? L.T(", код 0x") + hr.ToString("X8") : "") + (box != 0 ? L.T(", область ") + (box >> 16) + "×" + (box & 0xFFFF) : "") + "]";
        }
        if (drawn > 0 && err == 0)
        {
            string n = api == 4 ? L.T("DirectDraw, кадр = ") + (kind == 1 ? "Flip" : kind == 2 ? "Blt" : "Unlock") : api == 2 ? "DirectX 9" : api == 3 ? "OpenGL" : api == 5 ? "DirectX 12" : api == 6 ? "DirectX 10" : api == 7 ? "DirectX 8" : "DirectX 11";
            return L.T("показники малюються в кадрі гри (") + n + ")" + dd;
        }
        if (err == 2) return L.T("показники в кадрі: невідомий пристрій відображення - оверлей не показується (змініть режим вручну)");
        if (err == 31) return L.T("показники в кадрі: не вдалося підготувати малювання DirectX 12 - оверлей не показується (змініть режим вручну)");
        if (err == 32) return L.T("показники в кадрі: DirectX 12 - черга команд гри ще невідома, чекаємо на кадри (оверлей поки не показується)");
        if (err == 33) return L.T("показники в кадрі: DirectX 12 - черга команд належить іншому пристрою - оверлей не показується (змініть режим вручну)");
        if (err == 1) return L.T("показники в кадрі: формат буфера кадру не підтримується (DXGI_FORMAT ") + Marshal.ReadInt32(view, 4764) + L.T(") - оверлей не показується (змініть режим вручну)");
        if (err == 3) return L.T("показники в кадрі: згладжування MSAA ×") + Marshal.ReadInt32(view, 4768) + L.T(" на буфері кадру - оверлей не показується (змініть режим вручну)");
        if (err == 4) return L.T("показники в кадрі: вікно гри замале - оверлей не показується (змініть режим вручну)");
        if (err == 11) return L.T("показники в кадрі: OpenGL не дав створити шейдери - оверлей не показується (змініть режим вручну)");
        if (err == 12) return L.T("показники в кадрі: у цьому OpenGL немає потрібних функцій - оверлей не показується (змініть режим вручну)");
        if (err != 0) return L.T("показники в кадрі: помилка 0x") + err.ToString("X8") + L.T(" - оверлей не показується (змініть режим вручну)") + dd;
        if (cur == 4 && kind == 0) return L.T("DirectDraw підключено, кадрів на первинній поверхні ще не було");
        return L.T("показники в кадрі: чекаємо на кадри гри");
    }

    public static string Status()
    {
        int pid = Games.CurrentPid;
        LimSession s = null;
        if (pid > 0) sessions.TryGetValue(pid, out s);
        bool bdr = s != null && s.Game != null && AutoBdr(s.Game, s);
        bool emb = s != null && InFrame(s.Game, s);
        if (!OverlayCfg.LimOn && !bdr && !emb && !(s != null && s.Game != null && s.Game.Ov)) return L.T("Обмеження FPS вимкнено гарячою клавішею.");
        if (pid <= 0 || s == null) return LastNote;
        string name = s.Game != null ? s.Game.Name : Path.GetFileName(s.Exe);
        if (s.Blocked.Length > 0) return name + L.T(": бібліотеку не підключено - ") + s.Blocked + ".";
        if (!s.Injected) return name + L.T(": підключення… ") + s.Error;
        int st = Marshal.ReadInt32(s.View, OffStatus), hooks = Marshal.ReadInt32(s.View, OffHooks);
        if (st == -2)
        {
            if (Marshal.ReadInt32(s.View, OffD3d) == 8)
                return name + L.T(": гра використовує DirectX 8, але перехопити його не вдалося (код 0x") + Marshal.ReadInt32(s.View, 4772).ToString("X8") + L.T("): обмеження кадрів недоступне; оверлей - окреме вікно.");
            if (Marshal.ReadInt32(s.View, OffD3d) == 7)
                return name + L.T(": гра виводить картинку через DirectDraw (старі 2D-ігри), але перехопити його не вдалося. Показ у кадрі для таких ігор потребує перехоплення, яке не вдалося. Якщо не допоможе - зазвичай беруть обгортку на кшталт cnc-ddraw або dgVoodoo2.");
            return name + L.T(": графічний інтерфейс гри не підтримується (DirectX 8-12, OpenGL, DirectDraw); для Vulkan показується звичайне вікно оверлею.");
        }
        if (st == -1) return name + L.T(": помилка ініціалізації перехоплення.");
        if (hooks == 0) return name + L.T(": очікування графіки гри…");
        List<string> parts = new List<string>();
        if (ActiveFor(s.Game))
        {
            FrameStats f = Stats(pid);
            parts.Add(L.T("ліміт ") + TargetText(s) + " FPS" + (f.Valid ? L.T(" · зараз ") + Math.Round(f.Fps) + " FPS (" + f.Api + ")" : L.T(" · кадрів поки немає")));
        }
        if (bdr) parts.Add(BdrText(s.View));
        if (emb) parts.Add(EmbedStatus(s.View, hooks));
        else if (s.Game != null && s.Game.Ov && !OverlaySupported(pid)) parts.Add(L.T("оверлей для DirectDraw не підтримується"));
        else if (s.Game != null && s.Game.Ov) parts.Add(L.T("оверлей - окреме вікно поверх гри (") + ApiName(ApiOf(s)) + (s.Game.Mode == 2 ? L.T(", показ у повноекрані вимкнено") : (s.Excl ? "" : L.T(", гра не в справжньому повноекрані") + (s.ExclDbg.Length > 0 ? " [" + s.ExclDbg + "]" : ""))) + ")");
        if (s.Game != null && s.Game.WrapFresh) parts.Add(L.T("обгортку ") + (s.Game.Wrap == "ddraw" ? "DirectDraw" : "DirectX 8 → 9") + L.T(" покладено в теку гри: перезапустіть гру, тоді працюватимуть показники й ліміт"));
        if ((hooks & 256) != 0 && (hooks & 64) == 0)
            parts.Add((ApiOf(s) == "dx9" ? "Direct3D 9" : "DirectX 8") + L.T(": пристрій гри не перехоплено (створений до підключення") + (ApiOf(s) == "dx9" ? L.T(", у грі є чужий оверлей") : "") + L.T("), кадри рахуються й обмежуються на показі ядром; показників у кадрі немає"));
        if ((hooks & 128) != 0)
            parts.Add(L.T("Vulkan: перехоплено кадрів ") + Marshal.ReadInt32(s.View, 4756) + L.T(", кадрів DXGI ") + Marshal.ReadInt32(s.View, 4760));
        if (s.Others.Length > 0 && (emb || bdr || (s.Game != null && s.Game.Ov))) parts.Add(L.T("у грі є й інший оверлей (") + s.Others + L.T("): показники можуть дублюватися"));
        if (parts.Count == 0) return name + L.T(": бібліотеку підключено, нічого не робить (обмеження, підміна повноекрану й показ у кадрі не потрібні)");
        return name + ": " + string.Join(" · ", parts.ToArray());
    }
}
