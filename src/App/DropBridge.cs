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
//  «заборонено». Тому поруч працює маленький помічник зі звичайними правами (NeoBarDrop.exe - нативний, близько
//  мегабайта пам'яті; якщо його немає - та сама NeoBar.exe з --drop). Він запускається лише при першому
//  перетягуванні і сам закривається після 2 хвилин без роботи.
//  Стежить сама NeoBar (процесу з вищими правами Windows показує стан миші завжди, а звичайному - ні, коли активне
//  вікно NeoBar): кнопку затиснули поза NeoBar і з нею зайшли на вікно, що приймає файли, - NeoBar просить помічника
//  поставити над цим вікном невидиме вікно-приймач. Помічник отримує файли звичайним перетягуванням і передає їхні
//  шляхи панелі (WM_COPYDATA, дозволене фільтром UIPI лише для власного приймача). Панель перевіряє, що надіслала
//  саме її копія. Без прав адміністратора помічник не потрібен.
// ======================================================================
static class DropBridge
{
    const int WM_COPYDATA = 0x4A, WM_DROPFILES = 0x233, MsgShow = 0x8000 + 0x61, MsgHide = 0x8000 + 0x62;
    static readonly IntPtr Magic = new IntPtr(0x4E42444E);
    static readonly IntPtr MagicTool = new IntPtr(0x4E42544C);       // команда з контекстного меню файлів

    [StructLayout(LayoutKind.Sequential)] struct COPYDATA { public IntPtr Data; public int Size; public IntPtr Ptr; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, ref COPYDATA l, int flags, int timeout, out IntPtr res);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr ChildWindowFromPointEx(IntPtr h, POINT p, uint flags);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr h);

    // ---------------- сторона NeoBar (адміністратор) ----------------
    static Receiver recv;
    static readonly HashSet<IntPtr> zones = new HashSet<IntPtr>();
    static bool launched;

    // вікно приймає файли: запам'ятати його як місце для скидання (викликається з DropFiles.Register, кількаразово - безпечно)
    public static void Add(Control c)
    {
        if (!Elevated.IsAdmin) return;
        try
        {
            IntPtr h = c.Handle;
            if (zones.Add(h)) { IntPtr hh = h; c.HandleDestroyed += delegate { zones.Remove(hh); }; }
            Start();
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    // ---------- приймач команд (завжди, коли працює панель) ----------
    // контекстне меню файлів запускає окремий процес NeoBar.exe --tool; якщо панель працює, він передає їй файл,
    // і той відкривається в розкритому вікні панелі (як інші віджети: одне розкрите вікно замінює інше)
    static Action<string, string> toolHandler;
    const string RemoteTitle = "NeoBarRemote";

    public static void InitRemote(Action<string, string> onTool)
    {
        toolHandler = onTool;
        try { if (recv == null) recv = new Receiver(); }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    // з процесу контекстного меню: true - панель прийняла файл
    public static bool SendTool(string verb, string path)
    {
        try
        {
            IntPtr w = Win.FindWindow(null, RemoteTitle);
            if (w == IntPtr.Zero || !FromSelf(w)) return false;
            uint pid;
            Win.GetWindowThreadProcessId(w, out pid);
            try { Win.AllowSetForegroundWindow((int)pid); } catch (Exception skipEx) { SbLog.Skip(skipEx); }      // щоб панель могла вивести своє вікно наперед
            string text = verb + "\n" + path;
            IntPtr mem = Marshal.StringToHGlobalUni(text);
            NativeWindow me = new NativeWindow();          // від кого: панель перевіряє, що це той самий NeoBar.exe
            try
            {
                me.CreateHandle(new CreateParams());
                COPYDATA cd = new COPYDATA();
                cd.Data = MagicTool;
                cd.Size = text.Length * 2;
                cd.Ptr = mem;
                IntPtr res;
                IntPtr ok = SendMessageTimeout(w, WM_COPYDATA, me.Handle, ref cd, 0x0002, 5000, out res);
                return ok != IntPtr.Zero && res != IntPtr.Zero;
            }
            finally { Marshal.FreeHGlobal(mem); try { me.DestroyHandle(); } catch (Exception skipEx) { SbLog.Skip(skipEx); } }
        }
        catch { return false; }
    }


    static bool TakeTool(IntPtr sender, IntPtr lp)
    {
        COPYDATA cd = (COPYDATA)Marshal.PtrToStructure(lp, typeof(COPYDATA));
        if (cd.Data != MagicTool || cd.Size <= 2 || cd.Size > 1 << 16 || cd.Ptr == IntPtr.Zero || toolHandler == null) return false;
        if (!FromSelf(sender)) return false;           // лише від процесу контекстного меню NeoBar
        string s = Marshal.PtrToStringUni(cd.Ptr, cd.Size / 2);
        int k = s.IndexOf('\n');
        if (k <= 0) return false;
        string verb = s.Substring(0, k), path = s.Substring(k + 1);
        if (verb != "hash" && verb != "enc" && verb != "dec" && verb != "scan") return false;
        try { if (!Path.IsPathRooted(path) || !File.Exists(path)) return false; } catch { return false; }
        toolHandler(verb, path);
        return true;
    }

    static void Start()
    {
        if (launched) return;
        launched = true;
        if (recv == null) recv = new Receiver();
        poll = new System.Windows.Forms.Timer();
        poll.Interval = IdlePoll;
        poll.Tick += delegate { Watch(); };
        poll.Start();
    }

    // кнопка миші відпущена - перевіряємо рідко; затиснута - часто (щоб устигнути поставити приймач)
    const int IdlePoll = 150, DragPoll = 40;
    static int lastLaunch = Environment.TickCount - 60000;

    static string DropExe { get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "NeoBarDrop.exe"); } }

    // помічник запускається, лише коли справді тягнуть файл на NeoBar (не частіше разу на 5 с)
    static void Launch()
    {
        if (unchecked(Environment.TickCount - lastLaunch) < 5000) return;
        lastLaunch = Environment.TickCount;
        string how = File.Exists(DropExe)
            ? UserLaunch.Run(DropExe, recv.Handle.ToInt64().ToString())
            : UserLaunch.Run(Application.ExecutablePath, "--drop " + recv.Handle.ToInt64());
        SbLog.W(SbLog.Panel, "перетягування від адміністратора: помічник зі звичайними правами - " + (how.Length > 0 ? how : "не запустився"));
    }

    // ---------- стеження за перетягуванням (у NeoBar) ----------
    static System.Windows.Forms.Timer poll;
    static IntPtr helper = IntPtr.Zero, shownZone = IntPtr.Zero;
    static bool wasDown, pressOutside;
    static readonly uint selfPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

    static IntPtr Helper()
    {
        if (helper != IntPtr.Zero && Win.IsWindow(helper)) return helper;
        helper = recv != null ? Win.FindWindow(null, "NeoBarDrop " + recv.Handle.ToInt64()) : IntPtr.Zero;
        return helper;
    }

    static IntPtr RootAt(POINT pt)
    {
        IntPtr w = WindowFromPoint(pt);
        if (w == IntPtr.Zero) return IntPtr.Zero;
        IntPtr root = Win.GetAncestor(w, 2);
        return root != IntPtr.Zero ? root : w;
    }

    // місце для скидання під курсором: найглибше вікно-приймач (галерея шпалер - дочірній елемент вікна
    // налаштувань, яке теж приймає файли). Шукаємо від верхнього вікна вниз, а не WindowFromPoint: над
    // приймачем уже може стояти невидиме вікно помічника, і тоді WindowFromPoint повертає його.
    static IntPtr ZoneAt(POINT pt) { return ZoneIn(RootAt(pt), pt); }

    static IntPtr ZoneIn(IntPtr root, POINT pt)
    {
        IntPtr w = root;
        for (int i = 0; i < 32 && w != IntPtr.Zero; i++)
        {
            POINT c = pt;
            if (!ScreenToClient(w, ref c)) break;
            IntPtr ch = ChildWindowFromPointEx(w, c, 0x0001 | 0x0004);     // CWP_SKIPINVISIBLE | CWP_SKIPTRANSPARENT
            if (ch == IntPtr.Zero || ch == w) break;
            w = ch;
        }
        for (int i = 0; i < 32 && w != IntPtr.Zero; i++)
        {
            if (zones.Contains(w)) return w;
            w = Win.GetAncestor(w, 1);          // GA_PARENT
        }
        return IntPtr.Zero;
    }

    static void Watch()
    {
        try
        {
            if (NeoBar.Paused) { Hide(); wasDown = false; if (poll.Interval != IdlePoll) poll.Interval = IdlePoll; return; }         // гра на весь екран - не стежимо
            bool down = (Win.GetAsyncKeyState(GetSystemMetrics(23) != 0 ? 0x02 : 0x01) & 0x8000) != 0;
            int want = down ? DragPoll : IdlePoll;
            if (poll.Interval != want) poll.Interval = want;
            POINT pt;
            if (!GetCursorPos(out pt)) return;
            if (down && !wasDown)
            {
                // натиснули на власному вікні NeoBar - це її дії (перетягування значків тощо), не чіпаємо
                uint pid;
                IntPtr root = RootAt(pt);
                pressOutside = shownZone == IntPtr.Zero && (root == IntPtr.Zero || Win.GetWindowThreadProcessId(root, out pid) == 0 || pid != selfPid);
            }
            wasDown = down;
            if (!down || !pressOutside) { Hide(); return; }
            if (shownZone != IntPtr.Zero)
            {
                // курсор усе ще над тим самим вікном: перевіряємо, чи не зайшов він у вкладений приймач (або вийшов
                // з нього в батьківський) - тоді невидиме вікно помічника переїжджає туди
                RECT rc;
                IntPtr top = Win.IsWindow(shownZone) && Win.IsWindowVisible(shownZone) ? Win.GetAncestor(shownZone, 2) : IntPtr.Zero;
                if (top != IntPtr.Zero && GetWindowRect(top, out rc) && pt.X >= rc.L && pt.X < rc.R && pt.Y >= rc.T && pt.Y < rc.B)
                {
                    IntPtr d = ZoneIn(top, pt);
                    if (d == shownZone) return;
                    if (d != IntPtr.Zero && Win.IsWindowVisible(d))
                    {
                        IntPtr hh = Helper();
                        if (hh != IntPtr.Zero && Win.PostMessage(hh, MsgShow, d, IntPtr.Zero)) shownZone = d;
                        return;
                    }
                }
                Hide();
            }
            IntPtr z = ZoneAt(pt);
            if (z == IntPtr.Zero || !Win.IsWindowVisible(z)) return;
            IntPtr h = Helper();
            if (h == IntPtr.Zero) { Launch(); return; }     // наступний тик покаже приймач, щойно помічник запуститься
            if (Win.PostMessage(h, MsgShow, z, IntPtr.Zero)) shownZone = z;
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    static void Hide()
    {
        if (shownZone == IntPtr.Zero) return;
        shownZone = IntPtr.Zero;
        IntPtr h = Helper();
        if (h != IntPtr.Zero) Win.PostMessage(h, MsgHide, IntPtr.Zero, IntPtr.Zero);
    }

    sealed class Receiver : NativeWindow
    {
        public Receiver()
        {
            CreateParams cp = new CreateParams();
            cp.Caption = RemoteTitle;
            CreateHandle(cp);
            try { Win.ChangeWindowMessageFilterEx(Handle, WM_COPYDATA, 1, IntPtr.Zero); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_COPYDATA)
            {
                try
                {
                    if (TakeTool(m.WParam, m.LParam)) { m.Result = new IntPtr(1); return; }
                    if (Take(m.WParam, m.LParam)) { m.Result = new IntPtr(1); return; }
                }
                catch (Exception ex) { Util.ErrorLog(ex); }
            }
            base.WndProc(ref m);
        }
    }

    // приймаємо лише від своєї ж програми (той самий файл NeoBar.exe; файли - ще й від NeoBarDrop.exe поруч)
    static bool FromSelf(IntPtr sender) { return FromSelf(sender, false); }

    static bool FromSelf(IntPtr sender, bool dropHelper)
    {
        uint pid;
        if (sender == IntPtr.Zero || Win.GetWindowThreadProcessId(sender, out pid) == 0 || pid == 0) return false;
        IntPtr h = Win.OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            StringBuilder sb = new StringBuilder(1024);
            int n = sb.Capacity;
            if (!Win.QueryFullProcessImageNameW(h, 0, sb, ref n)) return false;
            string p = Path.GetFullPath(sb.ToString());
            return string.Equals(p, Path.GetFullPath(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase) ||
                   (dropHelper && string.Equals(p, Path.GetFullPath(DropExe), StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
        finally { Win.CloseHandle(h); }
    }

    static bool Take(IntPtr sender, IntPtr lp)
    {
        COPYDATA cd = (COPYDATA)Marshal.PtrToStructure(lp, typeof(COPYDATA));
        if (cd.Data != Magic || cd.Size <= 2 || cd.Size > 1 << 20 || cd.Ptr == IntPtr.Zero) return false;
        string s = Marshal.PtrToStringUni(cd.Ptr, cd.Size / 2);
        if (!FromSelf(sender, true)) { SbLog.W(SbLog.Panel, "перетягування: відхилено повідомлення не від помічника"); return false; }
        string[] parts = s.Split('\n');
        long zl;
        if (parts.Length < 2 || !long.TryParse(parts[0], out zl)) return true;
        IntPtr zone = new IntPtr(zl);
        if (!zones.Contains(zone) || !Win.IsWindow(zone)) { SbLog.W(SbLog.Panel, "перетягування: вікно вже закрите"); return true; }
        List<string> files = new List<string>();
        for (int i = 1; i < parts.Length; i++)
        {
            string f = parts[i];
            try { if (f.Length > 0 && Path.IsPathRooted(f) && (File.Exists(f) || Directory.Exists(f))) files.Add(f); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
        if (files.Count == 0) return true;
        SbLog.W(SbLog.Panel, "перетягування через помічника: " + files.Count);
        POINT pt;
        GetCursorPos(out pt);
        ScreenToClient(zone, ref pt);
        IntPtr hd = HDrop(files, pt);
        if (hd != IntPtr.Zero && !Win.PostMessage(zone, WM_DROPFILES, hd, IntPtr.Zero)) GlobalFree(hd);
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
        if (!Win.IsWindow(target)) return 1;
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
        IntPtr zone = IntPtr.Zero;
        bool logged;
        int lastWork = Environment.TickCount;

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
            Text = "NeoBarDrop " + t.ToInt64();          // за цим заголовком NeoBar знаходить помічника
            DragEnter += OnDrag;
            DragOver += OnDrag;
            DragDrop += OnDrop;
            tm.Interval = 1000;
            tm.Tick += delegate
            {
                // NeoBar закрилась або 2 хв без роботи - помічник теж (NeoBar запустить знову за потреби)
                if (!Win.IsWindow(target) || (!Win.IsWindowVisible(Handle) && unchecked(Environment.TickCount - lastWork) > 120000)) { tm.Stop(); Application.ExitThread(); }
            };
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

        // команди від NeoBar: показати приймач над вікном / сховати
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == MsgShow) { On(m.WParam); return; }
            if (m.Msg == MsgHide) { Off(); return; }
            base.WndProc(ref m);
        }

        void On(IntPtr z)
        {
            RECT rc;
            lastWork = Environment.TickCount;
            if (!Win.IsWindow(z) || !GetWindowRect(z, out rc)) return;
            if (!logged) { logged = true; SbLog.W(SbLog.Panel, "помічник перетягування: перетягування над вікном NeoBar " + rc.L + "," + rc.T + " " + (rc.R - rc.L) + "x" + (rc.B - rc.T)); }
            zone = z;
            Win.SetWindowPos(Handle, new IntPtr(-1), rc.L, rc.T, rc.R - rc.L, rc.B - rc.T, 0x0010 | 0x0040);      // TOPMOST, NOACTIVATE | SHOWWINDOW
        }

        void Off()
        {
            Win.ShowWindow(Handle, 0);
        }

        static void OnDrag(object s, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effect = DragDropEffects.None; return; }
            e.Effect = (e.AllowedEffect & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : (e.AllowedEffect & DragDropEffects.Link);
        }

        void OnDrop(object s, DragEventArgs e)
        {
            string[] f = null;
            try { f = e.Data.GetData(DataFormats.FileDrop) as string[]; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
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
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
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
        if (!Win.OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out tok)) return;      // ADJUST_PRIVILEGES | QUERY
        try
        {
            long luid;
            if (!LookupPrivilegeValue(null, "SeIncreaseQuotaPrivilege", out luid)) return;
            TOKEN_PRIV tp = new TOKEN_PRIV();
            tp.Count = 1; tp.P.Luid = luid; tp.P.Attr = 2;          // SE_PRIVILEGE_ENABLED
            AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { Win.CloseHandle(tok); }
    }

    // процес користувача зі звичайними правами, чий токен беремо: Провідник (вікно робочого столу), а коли
    // Провідника немає (його перезапускають) - інший процес сеансу користувача без підвищення
    static uint UserPid()
    {
        IntPtr shell = Win.GetShellWindow();
        uint pid;
        if (shell != IntPtr.Zero && Win.GetWindowThreadProcessId(shell, out pid) != 0 && pid != 0) return pid;
        int session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        foreach (string n in new string[] { "sihost", "ctfmon", "taskhostw", "RuntimeBroker", "ShellExperienceHost", "StartMenuExperienceHost" })
            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName(n))
            {
                try { if (p.SessionId == session && !ProcAct.IsElevated(p.Id)) return (uint)p.Id; }
                catch (Exception skipEx) { SbLog.Skip(skipEx); }
                finally { p.Dispose(); }
            }
        return 0;
    }

    static bool ViaToken(string exe, string args)
    {
        uint pid = UserPid();
        if (pid == 0) return false;
        IntPtr hp = Win.OpenProcess(0x1000, false, pid);
        if (hp == IntPtr.Zero) return false;
        IntPtr tok = IntPtr.Zero, dup = IntPtr.Zero;
        try
        {
            if (!Win.OpenProcessToken(hp, 0x0002, out tok)) return false;                                 // TOKEN_DUPLICATE
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
            Win.CloseHandle(pi.hThread);
            Win.CloseHandle(pi.hProcess);
            return true;
        }
        finally
        {
            if (dup != IntPtr.Zero) Win.CloseHandle(dup);
            if (tok != IntPtr.Zero) Win.CloseHandle(tok);
            Win.CloseHandle(hp);
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
            try { if (Path.IsPathRooted(exe)) dir = Path.GetDirectoryName(exe) ?? ""; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            app.GetType().InvokeMember("ShellExecute", BindingFlags.InvokeMethod, null, app, new object[] { exe, args, dir, "", 1 });
            return true;
        }
        finally
        {
            foreach (object o in new object[] { app, fv, view, disp, windows })
                try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
    }
}
