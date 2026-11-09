using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

// ======================================================================
//  Температури в треї: для ЦП, ГП і hotspot ГП - окремий значок із числом (градуси) обраного кольору.
//  Вмикається в налаштуваннях («Панель»). Значки малюються лише коли число змінилося; у грі й повноекранній
//  програмі зникають разом з іншими датчиками панелі.
//  Кожен значок має постійний ідентифікатор (номер і GUID, прив'язаний до шляху NeoBar.exe), тож Windows запам'ятовує
//  його як той самий значок: показ на панелі завдань і місце, куди його перетягнули, зберігаються між запусками.
//  Програма сама не може поставити значок на конкретне місце - це вирішує Windows; уперше вони додаються в порядку
//  ЦП, ГП, hotspot.
// ======================================================================
static class TrayTemps
{
    sealed class Slot
    {
        public string Name = "";
        public bool On, Added, UseGuid = true;
        public Color Col;
        public Icon Ico;
        public string Shown = "", Tip = "";
        public int ColArgb;
        public uint Id;
        public Guid G;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NID
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_GUID = 0x20;
    const int CallbackMsg = 0x8000 + 0x51;
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint msg, ref NID d);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string s);
    [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr h, uint msg, uint action, IntPtr info);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    static readonly Slot[] slots = { new Slot(), new Slot(), new Slot() };
    static NeoBar bar;
    static bool hooked, updHooked, running;
    static Wnd wnd;
    static ContextMenuStrip menu;
    static int taskbarCreated;

    // приховане вікно для повідомлень від значків (клік, меню) і перезапуску Провідника
    sealed class Wnd : NativeWindow
    {
        public Wnd()
        {
            CreateHandle(new CreateParams());
            taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            // NeoBar від адміністратора: дозволити повідомлення від Провідника (UIPI)
            try { ChangeWindowMessageFilterEx(Handle, (uint)taskbarCreated, 1, IntPtr.Zero); ChangeWindowMessageFilterEx(Handle, CallbackMsg, 1, IntPtr.Zero); } catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == CallbackMsg)
            {
                int ev = (int)(m.LParam.ToInt64() & 0xFFFF);
                if (ev == 0x0202) Open();                                   // WM_LBUTTONUP
                else if (ev == 0x0205) ShowMenu();                          // WM_RBUTTONUP
                return;
            }
            if (taskbarCreated != 0 && m.Msg == taskbarCreated)
            {
                // Провідник перезапустився - значки треба додати знову
                foreach (Slot s in slots) if (s.Added) { s.Added = false; Add(s); }
            }
            base.WndProc(ref m);
        }
    }

    static void ShowMenu()
    {
        try
        {
            if (menu == null)
            {
                menu = new ContextMenuStrip();
                ToolStripMenuItem open = new ToolStripMenuItem(L.T("Монітор системи"));
                open.Font = new Font(open.Font, FontStyle.Bold);
                open.Click += delegate { Open(); };
                menu.Items.Add(open);
            }
            SetForegroundWindow(wnd.Handle);
            menu.Show(Cursor.Position);
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    static float Value(int i)
    {
        return i == 0 ? Sensors.CpuTemp : (i == 1 ? Sensors.GpuTemp : Sensors.Hotspot);
    }

    // постійний GUID значка: свій для кожного значка й кожного розташування NeoBar.exe (Windows прив'язує GUID до шляху)
    static Guid GuidOf(int i)
    {
        try
        {
            using (MD5 md = MD5.Create())
            {
                byte[] h = md.ComputeHash(Encoding.UTF8.GetBytes("NeoBar.TrayTemp." + i + "|" + Application.ExecutablePath.ToLowerInvariant()));
                h[7] = (byte)((h[7] & 0x0F) | 0x30);
                h[8] = (byte)((h[8] & 0x3F) | 0x80);
                return new Guid(h);
            }
        }
        catch { return Guid.Empty; }            // MD5 заборонено політикою FIPS - тоді лише постійний номер
    }

    // викликається при кожному перезавантаженні панелі й зміні паузи (гра на весь екран)
    public static void Configure(Cfg c, NeoBar b, bool run)
    {
        bar = b;
        slots[0].On = c.TrayCpu; slots[1].On = c.TrayGpu; slots[2].On = c.TrayHot;
        slots[0].Col = c.TrayCpuCol; slots[1].Col = c.TrayGpuCol; slots[2].Col = c.TrayHotCol;
        slots[0].Name = L.T("ЦП"); slots[1].Name = L.T("ГП"); slots[2].Name = L.T("ГП") + " hotspot";
        bool any = false, gpu = false;
        for (int i = 0; i < 3; i++)
        {
            if (slots[i].Id == 0) { slots[i].Id = (uint)(101 + i); slots[i].G = GuidOf(i); slots[i].UseGuid = slots[i].G != Guid.Empty; }
            if (slots[i].On) { any = true; if (i > 0) gpu = true; }
        }
        running = any && run;
        Sensors.Use("tray", running ? (gpu ? 2 : 1) : 0);
        if (!updHooked) { updHooked = true; Sensors.Updated += OnData; }
        if (!hooked) { hooked = true; Application.ApplicationExit += delegate { RemoveAll(); }; }
        if (running && wnd == null) { try { wnd = new Wnd(); } catch (Exception ex) { Util.ErrorLog(ex); return; } }
        bool same = true;
        for (int i = 0; i < 3; i++) if ((running && slots[i].On) != slots[i].Added) same = false;
        if (!same)
        {
            // набір змінився - додаємо всі заново, з кінця: новий значок Windows ставить лівіше, тож зліва направо - ЦП, ГП, hotspot
            RemoveAll();
            for (int i = 2; i >= 0; i--) if (running && slots[i].On) Show(i);
        }
        else for (int i = 0; i < 3; i++) if (running && slots[i].On) Show(i);
    }

    static void OnData()
    {
        try
        {
            if (!running || bar == null || bar.IsDisposed) return;
            if (bar.InvokeRequired) bar.BeginInvoke(new Action(Refresh));
            else Refresh();
        }
        catch { }
    }

    static void Refresh()
    {
        for (int i = 0; i < 3; i++) if (running && slots[i].On) Show(i);
    }

    static NID Data(Slot s, uint flags)
    {
        NID d = new NID();
        d.cbSize = Marshal.SizeOf(typeof(NID));
        d.hWnd = wnd.Handle;
        d.uID = s.Id;
        d.uFlags = flags | (s.UseGuid ? NIF_GUID : 0);
        d.uCallbackMessage = CallbackMsg;
        d.hIcon = s.Ico != null ? s.Ico.Handle : IntPtr.Zero;
        d.szTip = s.Tip.Length > 127 ? s.Tip.Substring(0, 127) : s.Tip;
        d.szInfo = ""; d.szInfoTitle = "";
        d.guidItem = s.UseGuid ? s.G : Guid.Empty;
        return d;
    }

    static void Add(Slot s)
    {
        if (wnd == null || s.Added) return;
        NID d = Data(s, 0);
        Shell_NotifyIcon(NIM_DELETE, ref d);              // залишок після аварійного виходу
        d = Data(s, NIF_MESSAGE | NIF_ICON | NIF_TIP);
        if (!Shell_NotifyIcon(NIM_ADD, ref d) && s.UseGuid)
        {
            // GUID зайнятий іншою копією NeoBar.exe - без нього (значок однаково з постійним номером)
            s.UseGuid = false;
            d = Data(s, NIF_MESSAGE | NIF_ICON | NIF_TIP);
            if (!Shell_NotifyIcon(NIM_ADD, ref d)) return;
        }
        s.Added = true;
    }

    static void Show(int i)
    {
        Slot s = slots[i];
        float v = Value(i);
        string t = float.IsNaN(v) ? "–" : Math.Round(v).ToString();
        int col = s.Col.ToArgb();
        if (s.Added && s.Shown == t && s.ColArgb == col) return;
        try
        {
            Icon old = s.Ico;
            s.Ico = Make(t, s.Col);
            s.Tip = s.Name + ": " + (float.IsNaN(v) ? "—" : t + " °C");
            if (!s.Added) Add(s);
            else
            {
                NID d = Data(s, NIF_ICON | NIF_TIP);
                if (!Shell_NotifyIcon(NIM_MODIFY, ref d)) { s.Added = false; Add(s); }
            }
            if (old != null) old.Dispose();
            s.Shown = t;
            s.ColArgb = col;
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    static void Open() { try { if (bar != null) bar.OpenPerf(0); } catch (Exception ex) { Util.ErrorLog(ex); } }

    static void Remove(Slot s)
    {
        try
        {
            if (s.Added && wnd != null)
            {
                NID d = Data(s, 0);
                Shell_NotifyIcon(NIM_DELETE, ref d);
            }
            s.Added = false;
            if (s.Ico != null) { s.Ico.Dispose(); s.Ico = null; }
            s.Shown = "";
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    static void RemoveAll()
    {
        foreach (Slot s in slots) Remove(s);
    }

    // число свого кольору на прозорому тлі, з тонкою темною обводкою, щоб читалося і на світлій, і на темній панелі завдань
    static Icon Make(string tx, Color col)
    {
        int s = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using (Bitmap bmp = new Bitmap(s, s, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                Font f = new Font("Segoe UI", tx.Length >= 3 ? s * 0.58f : s * 0.80f, FontStyle.Bold, GraphicsUnit.Pixel);
                try
                {
                    // не ширше за значок
                    while (f.Size > 5 && g.MeasureString(tx, f, 1000, sf).Width > s)
                    {
                        Font nf = new Font("Segoe UI", f.Size - 1, FontStyle.Bold, GraphicsUnit.Pixel);
                        f.Dispose(); f = nf;
                    }
                    RectangleF r = new RectangleF(0, -s * 0.04f, s, s);
                    float o = s >= 24 ? 1.5f : 1f;
                    using (SolidBrush sh = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
                    {
                        g.DrawString(tx, f, sh, new RectangleF(r.X + o, r.Y, r.Width, r.Height), sf);
                        g.DrawString(tx, f, sh, new RectangleF(r.X - o, r.Y, r.Width, r.Height), sf);
                        g.DrawString(tx, f, sh, new RectangleF(r.X, r.Y + o, r.Width, r.Height), sf);
                        g.DrawString(tx, f, sh, new RectangleF(r.X, r.Y - o, r.Width, r.Height), sf);
                    }
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(255, col.R, col.G, col.B)))
                        g.DrawString(tx, f, b, r, sf);
                }
                finally { f.Dispose(); }
            }
            IntPtr h = bmp.GetHicon();
            try { using (Icon tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone(); }
            finally { DestroyIcon(h); }
        }
    }
}
