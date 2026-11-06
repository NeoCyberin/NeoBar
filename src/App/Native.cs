using System;
using System.Drawing;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

// ======================================================================
//  Win32: P/Invoke та безпечні обгортки (жоден виклик не кидає виняток)
// ======================================================================
static class Win
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }


    [DllImport("shell32.dll")] public static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO fi, uint size, uint flags);
    [DllImport("shell32.dll")]
    public static extern IntPtr SHGetFileInfo(IntPtr pidl, uint attr, ref SHFILEINFO fi, uint size, uint flags);
    [DllImport("shell32.dll")]
    public static extern int SHGetSpecialFolderLocation(IntPtr owner, int csidl, out IntPtr pidl);
    [DllImport("shell32.dll")]
    public static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string s);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLong")] public static extern int GetWindowLong32(IntPtr h, int idx);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] public static extern IntPtr GetWindowLongPtr64(IntPtr h, int idx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SystemParametersInfo(uint action, uint param, StringBuilder buf, uint ini);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfo")]
    public static extern bool SystemParametersInfoSet(uint action, uint param, string buf, uint ini);
    [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(IntPtr p, IntPtr min, IntPtr max);
    [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    // ---------- спільні оголошення (раніше кожен модуль мав власну копію) ----------
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    // вікна й повідомлення
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, int w, int l);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool ChangeWindowMessageFilterEx(IntPtr h, uint msg, uint action, IntPtr info);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr h, IntPtr rc, IntPtr rgn, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pos, ref SIZE size, IntPtr src, ref POINT srcPos, int key, ref BLENDFUNCTION bl, int flags);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

    // GDI
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPels, biYPels, biClrUsed, biClrImportant;
    }
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    // 32-бітна DIB «зверху вниз» (BGRA) - для вікон із попіксельною прозорістю
    public static IntPtr CreateDib32(int w, int h, out IntPtr bits)
    {
        BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
        bi.biSize = 40; bi.biWidth = w; bi.biHeight = -h; bi.biPlanes = 1; bi.biBitCount = 32;
        return CreateDIBSection(IntPtr.Zero, ref bi, 0, out bits, IntPtr.Zero, 0);
    }
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFontW(int h, int w, int esc, int ori, int weight, uint italic, uint underline, uint strike,
                                            uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);

    // процеси, бібліотеки, диски
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr LoadLibrary(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] public static extern IntPtr GetProcAddress(IntPtr mod, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetVolumeInformation(string root, StringBuilder label, int lLen, out uint serial, out uint maxComp, out uint flags, StringBuilder fs, int fLen);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int ret, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int ret, IntPtr ov);
}

static class Nat
{
    public static bool Is64 { get { return IntPtr.Size == 8; } }

    // Стан «користувач зайнятий»: 2 = повноекранна програма/презентація, 3 = повноекранна D3D-гра, 4 = режим презентації
    public static bool FullscreenBusy()
    {
        try
        {
            int st;
            if (Win.SHQueryUserNotificationState(out st) != 0) return false;
            return st == 2 || st == 3 || st == 4;
        }
        catch { return false; }
    }

    // Системні накладки, що на мить займають весь екран (знімок екрана - Print Screen, пошук, меню «Пуск»): це не гра й не повноекранна програма
    static readonly string[] OverlayExe = { "screenclippinghost.exe", "snippingtool.exe", "screensketch.exe", "shellexperiencehost.exe", "startmenuexperiencehost.exe", "searchhost.exe", "searchapp.exe", "textinputhost.exe" };

    public static bool IsTransientOverlay(IntPtr h)
    {
        try
        {
            int pid;
            Win.GetWindowThreadProcessId(h, out pid);
            if (pid <= 0) return false;
            string exe = Games.ExeOf(pid);
            if (string.IsNullOrEmpty(exe)) return false;
            string n = System.IO.Path.GetFileName(exe).ToLowerInvariant();
            return Array.IndexOf(OverlayExe, n) >= 0;
        }
        catch { return false; }
    }

    // Чи на передньому плані вікно, що займає весь монітор (або розгорнуте)
    public static bool ForegroundCovers(bool onlyFullscreen, IntPtr ignore)
    {
        try
        {
            IntPtr h = Win.GetForegroundWindow();
            if (h == IntPtr.Zero || h == ignore || !Win.IsWindowVisible(h)) return false;
            StringBuilder cls = new StringBuilder(64);
            Win.GetClassName(h, cls, 64);
            string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return false;
            Win.RECT r;
            if (!Win.GetWindowRect(h, out r)) return false;
            System.Windows.Forms.Screen sc = System.Windows.Forms.Screen.FromHandle(h);
            Rectangle b = sc.Bounds;
            bool full = r.left <= b.Left && r.top <= b.Top && r.right >= b.Right && r.bottom >= b.Bottom;
            bool covers = full || (!onlyFullscreen && Win.IsZoomed(h));
            return covers && !IsTransientOverlay(h);
        }
        catch { return false; }
    }

    public static int GetStyle(IntPtr h, int idx)
    {
        try
        {
            return Is64 ? (int)Win.GetWindowLongPtr64(h, idx).ToInt64() : Win.GetWindowLong32(h, idx);
        }
        catch { return 0; }
    }

    // ---------- системний монітор ----------

    static long pIdle, pKern, pUser;
    static bool havePrev;

    // навантаження ЦП, % (0-100); -1, якщо невідомо
    public static int CpuLoad()
    {
        try
        {
            long i, k, u;
            if (!Win.GetSystemTimes(out i, out k, out u)) return -1;
            int res = -1;
            if (havePrev)
            {
                long di = i - pIdle, dk = k - pKern, du = u - pUser;
                long tot = dk + du; // kernel вже містить idle
                if (tot > 0) res = (int)Math.Max(0, Math.Min(100, (tot - di) * 100 / tot));
            }
            pIdle = i; pKern = k; pUser = u; havePrev = true;
            return res;
        }
        catch { return -1; }
    }

    public static int RamLoad(out long totalMb, out long freeMb)
    {
        totalMb = freeMb = 0;
        try
        {
            Win.MEMORYSTATUSEX m = new Win.MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(Win.MEMORYSTATUSEX));
            if (!Win.GlobalMemoryStatusEx(ref m)) return -1;
            totalMb = (long)(m.ullTotalPhys / (1024 * 1024));
            freeMb = (long)(m.ullAvailPhys / (1024 * 1024));
            return (int)m.dwMemoryLoad;
        }
        catch { return -1; }
    }
}
