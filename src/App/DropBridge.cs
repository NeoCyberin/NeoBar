using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// ======================================================================
//  Перетягування файлів у NeoBar, запущений від імені адміністратора.
//  Windows (UIPI) не пускає перетягування зі звичайного Провідника у вікна з вищими правами - курсор стає
//  «заборонено». Тому поруч працює маленький помічник зі звичайними правами (та сама NeoBar.exe з --drop):
//  коли кнопку миші затиснули поза NeoBar і з нею заходять на вікно NeoBar, помічник ставить над цим вікном
//  невидиме вікно-приймач, отримує файли звичайним перетягуванням і передає їхні шляхи панелі (WM_COPYDATA,
//  дозволене фільтром UIPI лише для власного приймача). Панель перевіряє, що надіслала саме її копія.
//  Вікна, які приймають файли, позначені властивістю NeoBarDropZone. Без прав адміністратора помічник не потрібен.
// ======================================================================
static class DropBridge
{
    const string Prop = "NeoBarDropZone";
    const int WM_COPYDATA = 0x4A, WM_DROPFILES = 0x233;
    static readonly IntPtr Magic = new IntPtr(0x4E42444E);

    [StructLayout(LayoutKind.Sequential)] struct COPYDATA { public IntPtr Data; public int Size; public IntPtr Ptr; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetProp(IntPtr h, string name, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetProp(IntPtr h, string name);
    [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr h, uint msg, uint action, IntPtr info);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, ref COPYDATA l, int flags, int timeout, out IntPtr res);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder sb, ref int size);

    // ---------------- сторона NeoBar (адміністратор) ----------------
    static Receiver recv;
    static readonly HashSet<IntPtr> zones = new HashSet<IntPtr>();
    static bool launched;

    // вікно приймає файли: позначити його для помічника (викликається з DropFiles.Register, кількаразово - безпечно)
    public static void Add(Control c)
    {
        if (!Elevated.IsAdmin) return;
        try
        {
            IntPtr h = c.Handle;
            SetProp(h, Prop, new IntPtr(1));
            if (zones.Add(h)) { IntPtr hh = h; c.HandleDestroyed += delegate { zones.Remove(hh); }; }
            Start();
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    static void Start()
    {
        if (launched) return;
        launched = true;
        recv = new Receiver();
        string exe = Application.ExecutablePath;
        string how = UserLaunch.Run(exe, "--drop " + recv.Handle.ToInt64());
        SbLog.W(SbLog.Panel, "перетягування від адміністратора: помічник зі звичайними правами - " + (how.Length > 0 ? how : "не запустився"));
    }

    sealed class Receiver : NativeWindow
    {
        public Receiver()
        {
            CreateParams cp = new CreateParams();
            cp.Caption = "NeoBarDropReceiver";
            CreateHandle(cp);
            try { ChangeWindowMessageFilterEx(Handle, WM_COPYDATA, 1, IntPtr.Zero); } catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_COPYDATA)
            {
                try { if (Take(m.WParam, m.LParam)) { m.Result = new IntPtr(1); return; } }
                catch (Exception ex) { Util.ErrorLog(ex); }
            }
            base.WndProc(ref m);
        }
    }

    // приймаємо лише від своєї ж програми (той самий файл NeoBar.exe)
    static bool FromSelf(IntPtr sender)
    {
        uint pid;
        if (sender == IntPtr.Zero || GetWindowThreadProcessId(sender, out pid) == 0 || pid == 0) return false;
        IntPtr h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            StringBuilder sb = new StringBuilder(1024);
            int n = sb.Capacity;
            if (!QueryFullProcessImageNameW(h, 0, sb, ref n)) return false;
            return string.Equals(Path.GetFullPath(sb.ToString()), Path.GetFullPath(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
        finally { CloseHandle(h); }
    }

    static bool Take(IntPtr sender, IntPtr lp)
    {
        COPYDATA cd = (COPYDATA)Marshal.PtrToStructure(lp, typeof(COPYDATA));
        if (cd.Data != Magic || cd.Size <= 2 || cd.Size > 1 << 20 || cd.Ptr == IntPtr.Zero) return false;
        string s = Marshal.PtrToStringUni(cd.Ptr, cd.Size / 2);
        if (!FromSelf(sender)) { SbLog.W(SbLog.Panel, "перетягування: відхилено повідомлення не від помічника"); return false; }
        string[] parts = s.Split('\n');
        long zl;
        if (parts.Length < 2 || !long.TryParse(parts[0], out zl)) return true;
        IntPtr zone = new IntPtr(zl);
        if (!zones.Contains(zone) || !IsWindow(zone)) { SbLog.W(SbLog.Panel, "перетягування: вікно вже закрите"); return true; }
        List<string> files = new List<string>();
        for (int i = 1; i < parts.Length; i++)
        {
            string f = parts[i];
            try { if (f.Length > 0 && Path.IsPathRooted(f) && (File.Exists(f) || Directory.Exists(f))) files.Add(f); } catch { }
        }
        if (files.Count == 0) return true;
        SbLog.W(SbLog.Panel, "перетягування через помічника: " + files.Count);
        POINT pt;
        GetCursorPos(out pt);
        ScreenToClient(zone, ref pt);
        IntPtr hd = HDrop(files, pt);
        if (hd != IntPtr.Zero && !PostMessage(zone, WM_DROPFILES, hd, IntPtr.Zero)) GlobalFree(hd);
        return true;
    }

    // HDROP (як від Провідника): вікно обробляє його тим самим DropFiles.Take, що й звичайне WM_DROPFILES
    static IntPtr HDrop(List<string> files, POINT pt)
    {
        StringBuilder sb = new StringBuilder();
        foreach (string f in files) { sb.Append(f); sb.Append('\0'); }
        sb.Append('\0');
        byte[] data = Encoding.Unicode.GetBytes(sb.ToString());
        const int head = 20;
        IntPtr h = GlobalAlloc(0x0042, new UIntPtr((uint)(head + data.Length)));       // GMEM_MOVEABLE | GMEM_ZEROINIT
        if (h == IntPtr.Zero) return IntPtr.Zero;
        IntPtr p = GlobalLock(h);
        if (p == IntPtr.Zero) { GlobalFree(h); return IntPtr.Zero; }
        Marshal.WriteInt32(p, 0, head);
        Marshal.WriteInt32(p, 4, pt.X);
        Marshal.WriteInt32(p, 8, pt.Y);
        Marshal.WriteInt32(p, 12, 0);
        Marshal.WriteInt32(p, 16, 1);           // імена в UTF-16
        Marshal.Copy(data, 0, new IntPtr(p.ToInt64() + head), data.Length);
        GlobalUnlock(h);
        return h;
    }

    // ---------------- помічник (звичайні права) ----------------
    public static int RunHelper(string[] args)
    {
        long r;
        if (args.Length < 2 || !long.TryParse(args[1], out r)) return 1;
        IntPtr target = new IntPtr(r);
        if (!IsWindow(target)) return 1;
        bool created;
        using (Mutex mx = new Mutex(true, "Local\\NeoBarDrop" + r, out created))
        {
            if (!created) { SbLog.W(SbLog.Panel, "помічник перетягування: уже працює"); return 0; }
            Overlay o = new Overlay(target);
            IntPtr h = o.Handle;            // вікно створено, але сховане до першого перетягування
            if (h == IntPtr.Zero) return 1;
            // напівпрозоре вікно без цього не приймає мишу - задаємо прозорість явно
            SetLayeredWindowAttributes(h, 0, 1, 0x2);
            SbLog.W(SbLog.Panel, "помічник перетягування: запущено (звичайні права), приймач " + r);
            Application.Run();
            o.Dispose();
        }
        return 0;
    }

    sealed class Overlay : Form
    {
        readonly IntPtr target;
        readonly System.Windows.Forms.Timer tm = new System.Windows.Forms.Timer();
        bool wasDown, pressOutside, shown;
        IntPtr zone = IntPtr.Zero;

        public Overlay(IntPtr t)
        {
            target = t;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(-32000, -32000, 8, 8);
            BackColor = Color.Black;
            Opacity = 0.01;                 // невидиме, але приймає мишу й перетягування
            AllowDrop = true;
            Text = "NeoBarDrop";
            DragEnter += OnDrag;
            DragOver += OnDrag;
            DragDrop += OnDrop;
            tm.Interval = 60;
            tm.Tick += delegate { Tick(); };
            tm.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000 | 0x8;         // TOOLWINDOW | NOACTIVATE | TOPMOST
                return cp;
            }
        }

        static IntPtr ZoneAt(POINT pt)
        {
            IntPtr w = WindowFromPoint(pt);
            if (w == IntPtr.Zero) return IntPtr.Zero;
            IntPtr root = GetAncestor(w, 2);
            if (root == IntPtr.Zero) root = w;
            return GetProp(root, Prop) != IntPtr.Zero && IsWindowVisible(root) ? root : IntPtr.Zero;
        }

        void Tick()
        {
            if (!IsWindow(target)) { tm.Stop(); Application.ExitThread(); return; }
            bool down = (GetAsyncKeyState(GetSystemMetrics(23) != 0 ? 0x02 : 0x01) & 0x8000) != 0;
            POINT pt;
            if (!GetCursorPos(out pt)) return;
            // натискання саме на вікні NeoBar - це її власні дії (перетягування значків тощо), не чіпаємо
            if (down && !wasDown) pressOutside = !shown && ZoneAt(pt) == IntPtr.Zero;
            wasDown = down;
            if (!down || !pressOutside) { if (shown) Off(); return; }
            if (shown)
            {
                RECT rc;
                if (IsWindow(zone) && IsWindowVisible(zone) && GetWindowRect(zone, out rc) && pt.X >= rc.L && pt.X < rc.R && pt.Y >= rc.T && pt.Y < rc.B) return;
                Off();
            }
            IntPtr z = ZoneAt(pt);
            if (z != IntPtr.Zero) On(z);
        }

        bool logged;

        void On(IntPtr z)
        {
            RECT rc;
            if (!GetWindowRect(z, out rc)) return;
            if (!logged) { logged = true; SbLog.W(SbLog.Panel, "помічник перетягування: перетягування над вікном NeoBar " + rc.L + "," + rc.T + " " + (rc.R - rc.L) + "x" + (rc.B - rc.T)); }
            zone = z;
            shown = true;
            SetWindowPos(Handle, new IntPtr(-1), rc.L, rc.T, rc.R - rc.L, rc.B - rc.T, 0x0010 | 0x0040);      // TOPMOST, NOACTIVATE | SHOWWINDOW
        }

        void Off()
        {
            shown = false;
            ShowWindow(Handle, 0);
        }

        static void OnDrag(object s, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effect = DragDropEffects.None; return; }
            e.Effect = (e.AllowedEffect & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : (e.AllowedEffect & DragDropEffects.Link);
        }

        void OnDrop(object s, DragEventArgs e)
        {
            string[] f = null;
            try { f = e.Data.GetData(DataFormats.FileDrop) as string[]; } catch { }
            IntPtr z = zone;
            Off();
            SbLog.W(SbLog.Panel, "помічник перетягування: скинуто файлів " + (f != null ? f.Length : 0));
            if (f == null || f.Length == 0 || z == IntPtr.Zero) return;
            string text = z.ToInt64() + "\n" + string.Join("\n", f);
            // після завершення перетягування: Провідник не чекає, поки панель обробить файли
            BeginInvoke(new Action(delegate { Send(text); }));
        }

        void Send(string text)
        {
            IntPtr mem = Marshal.StringToHGlobalUni(text);
            try
            {
                COPYDATA cd = new COPYDATA();
                cd.Data = Magic;
                cd.Size = text.Length * 2;
                cd.Ptr = mem;
                IntPtr res;
                IntPtr ok = SendMessageTimeout(target, WM_COPYDATA, Handle, ref cd, 0x0002, 5000, out res);       // SMTO_ABORTIFHUNG
                if (ok == IntPtr.Zero || res == IntPtr.Zero) SbLog.W(SbLog.Panel, "помічник перетягування: NeoBar не прийняла файли (" + Marshal.GetLastWin32Error() + ")");
            }
            catch { }
            finally { Marshal.FreeHGlobal(mem); }
        }
    }
}

// ======================================================================
//  Запуск програми зі звичайними правами з NeoBar, запущеної від адміністратора: з правами Провідника
//  (токен робочого столу), а якщо так не вийшло - через сам Провідник (спосіб, рекомендований Microsoft).
//  Повертає, яким способом запущено, або порожній рядок.
// ======================================================================
static class UserLaunch
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint access, out IntPtr tok);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr tok, uint access, IntPtr attr, int level, int type, out IntPtr dup);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessWithTokenW(IntPtr tok, int logon, string app, StringBuilder cmd, int flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    public static string Run(string exe, string args)
    {
        try { if (ViaToken(exe, args)) return "token"; } catch (Exception ex) { Util.ErrorLog(ex); }
        try { if (ViaShell(exe, args)) return "shell"; } catch (Exception ex) { Util.ErrorLog(ex); }
        return "";
    }

    // будь-що, що відкриває «Виконати» (документ, тека, адреса, команда) - через Провідник; програма - і з токеном
    public static string Shell(string file, string args)
    {
        try { if (ViaShell(file, args)) return "shell"; } catch (Exception ex) { Util.ErrorLog(ex); }
        try { if (ViaToken(file, args)) return "token"; } catch (Exception ex) { Util.ErrorLog(ex); }
        return "";
    }

    [StructLayout(LayoutKind.Sequential)] struct LUID_ATTR { public long Luid; public int Attr; }
    [StructLayout(LayoutKind.Sequential)] struct TOKEN_PRIV { public int Count; public LUID_ATTR P; }
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string sys, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr tok, bool all, ref TOKEN_PRIV np, int len, IntPtr prev, IntPtr ret);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

    // як у прикладі Microsoft (запуск від імені користувача робочого столу): увімкнути SeIncreaseQuotaPrivilege
    static void Quota()
    {
        IntPtr tok;
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out tok)) return;      // ADJUST_PRIVILEGES | QUERY
        try
        {
            long luid;
            if (!LookupPrivilegeValue(null, "SeIncreaseQuotaPrivilege", out luid)) return;
            TOKEN_PRIV tp = new TOKEN_PRIV();
            tp.Count = 1; tp.P.Luid = luid; tp.P.Attr = 2;          // SE_PRIVILEGE_ENABLED
            AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { CloseHandle(tok); }
    }

    static bool ViaToken(string exe, string args)
    {
        IntPtr shell = GetShellWindow();
        uint pid;
        if (shell == IntPtr.Zero || GetWindowThreadProcessId(shell, out pid) == 0 || pid == 0) return false;
        IntPtr hp = OpenProcess(0x1000, false, pid);
        if (hp == IntPtr.Zero) return false;
        IntPtr tok = IntPtr.Zero, dup = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(hp, 0x0002, out tok)) return false;                                 // TOKEN_DUPLICATE
            if (!DuplicateTokenEx(tok, 0x018B, IntPtr.Zero, 2, 1, out dup)) return false;              // первинний токен для нового процесу
            Quota();
            STARTUPINFO si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            si.lpDesktop = @"winsta0\default";            // інакше процес може опинитися на невидимому робочому столі
            PROCESS_INFORMATION pi;
            StringBuilder cmd = new StringBuilder("\"" + exe + "\" " + args);
            bool full = Path.IsPathRooted(exe) && File.Exists(exe);
            string dir = full ? Path.GetDirectoryName(exe) : null;
            if (!CreateProcessWithTokenW(dup, 0, full ? exe : null, cmd, 0, IntPtr.Zero, dir, ref si, out pi))
            {
                SbLog.W(SbLog.Panel, "запуск зі звичайними правами (токен): помилка " + Marshal.GetLastWin32Error());
                return false;
            }
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return true;
        }
        finally
        {
            if (dup != IntPtr.Zero) CloseHandle(dup);
            if (tok != IntPtr.Zero) CloseHandle(tok);
            CloseHandle(hp);
        }
    }

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface IShellWindows
    {
        int Count { get; }
        [return: MarshalAs(UnmanagedType.IDispatch)] object Item([In, MarshalAs(UnmanagedType.Struct)] object index);
        [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
        void Register([In, MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass, out int cookie);
        void RegisterPending(int threadId, [In, MarshalAs(UnmanagedType.Struct)] ref object loc, [In, MarshalAs(UnmanagedType.Struct)] ref object locRoot, int swClass, out int cookie);
        void Revoke(int cookie);
        void OnNavigate(int cookie, [In, MarshalAs(UnmanagedType.Struct)] ref object loc);
        void OnActivated(int cookie, bool active);
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW([In, MarshalAs(UnmanagedType.Struct)] ref object loc, [In, MarshalAs(UnmanagedType.Struct)] ref object locRoot, int swClass, out int hwnd, int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IComServiceProvider
    {
        void QueryService(ref Guid service, ref Guid iid, out IntPtr obj);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser
    {
        void GetWindow(out IntPtr hwnd);
        void ContextSensitiveHelp(bool enter);
        void InsertMenusSB(IntPtr menu, IntPtr widths);
        void SetMenuSB(IntPtr menu, IntPtr res, IntPtr active);
        void RemoveMenusSB(IntPtr menu);
        void SetStatusTextSB(IntPtr text);
        void EnableModelessSB(bool enable);
        void TranslateAcceleratorSB(IntPtr msg, ushort id);
        void BrowseObject(IntPtr pidl, uint flags);
        void GetViewStateStream(uint mode, out IntPtr stream);
        void GetControlWindow(uint id, out IntPtr hwnd);
        void SendControlMsg(uint id, uint msg, IntPtr w, IntPtr l, out IntPtr ret);
        void QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out IShellView view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellView
    {
        void GetWindow(out IntPtr hwnd);
        void ContextSensitiveHelp(bool enter);
        [PreserveSig] int TranslateAccelerator(IntPtr msg);
        void EnableModeless(bool enable);
        void UIActivate(uint state);
        void Refresh();
        void CreateViewWindow(IntPtr prev, IntPtr fs, IntPtr browser, IntPtr rect, out IntPtr hwnd);
        void DestroyViewWindow();
        void GetCurrentInfo(IntPtr fs);
        void AddPropertySheetPages(uint reserved, IntPtr fn, IntPtr lp);
        void SaveViewState();
        void SelectItem(IntPtr pidl, uint flags);
        void GetItemObject(uint item, ref Guid iid, out IntPtr obj);
    }

    // через Провідник: його робочий стіл → IShellDispatch2.ShellExecute (запускається в процесі Провідника, зі звичайними правами)
    static bool ViaShell(string exe, string args)
    {
        Type t = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
        object windows = Activator.CreateInstance(t);
        object disp = null, view = null, fv = null, app = null;
        try
        {
            object loc = 0, root = null;                    // CSIDL_DESKTOP
            int hwnd;
            disp = ((IShellWindows)windows).FindWindowSW(ref loc, ref root, 8, out hwnd, 1);       // SWC_DESKTOP, SWFO_NEEDDISPATCH
            if (disp == null) return false;
            Guid sid = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837"), iid = typeof(IShellBrowser).GUID;  // SID_STopLevelBrowser
            IntPtr pb;
            ((IComServiceProvider)disp).QueryService(ref sid, ref iid, out pb);
            if (pb == IntPtr.Zero) return false;
            IShellBrowser browser = (IShellBrowser)Marshal.GetObjectForIUnknown(pb);
            Marshal.Release(pb);
            IShellView sv;
            browser.QueryActiveShellView(out sv);
            view = sv;
            Guid idisp = new Guid("00020400-0000-0000-C000-000000000046");
            IntPtr pf;
            sv.GetItemObject(0, ref idisp, out pf);                 // SVGIO_BACKGROUND
            if (pf == IntPtr.Zero) return false;
            fv = Marshal.GetObjectForIUnknown(pf);
            Marshal.Release(pf);
            app = fv.GetType().InvokeMember("Application", BindingFlags.GetProperty, null, fv, null);
            string dir = "";
            try { if (Path.IsPathRooted(exe)) dir = Path.GetDirectoryName(exe) ?? ""; } catch { }
            app.GetType().InvokeMember("ShellExecute", BindingFlags.InvokeMethod, null, app, new object[] { exe, args, dir, "", 1 });
            return true;
        }
        finally
        {
            foreach (object o in new object[] { app, fv, view, disp, windows })
                try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch { }
        }
    }
}
