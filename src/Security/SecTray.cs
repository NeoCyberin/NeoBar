using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// ======================================================================
//  Значок «Безпеки» в області сповіщень (треї): є, поки ввімкнено фонове стеження. Завдяки йому стеження
//  працює, навіть якщо віджет «Безпека» прибрано з панелі. Клік - відкрити «Безпеку»; меню: сканувати зараз,
//  вимкнути стеження. Коли з'являються нові знахідки, на значку з'являється червона крапка - до відкриття вікна.
// ======================================================================
static class SecTray
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    static NotifyIcon ni;
    static Icon icoOk, icoAlert;
    static NeoBar bar;
    static bool alert, hooked;
    static ToolStripMenuItem miStatus;

    // створити або прибрати значок залежно від того, чи ввімкнене стеження
    public static void Sync(NeoBar b)
    {
        bar = b;
        try
        {
            bool want = SecWatch.Enabled;
            if (want && ni == null) Create();
            else if (!want && ni != null) Remove();
            Refresh();
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    static Icon Make(bool red)
    {
        int s = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using (Bitmap bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float w = s;
                using (System.Drawing.Drawing2D.GraphicsPath gp = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    gp.AddLine(w * 0.5f, w * 0.03f, w * 0.92f, w * 0.17f);
                    gp.AddLine(w * 0.92f, w * 0.17f, w * 0.92f, w * 0.5f);
                    gp.AddBezier(w * 0.92f, w * 0.5f, w * 0.92f, w * 0.78f, w * 0.7f, w * 0.9f, w * 0.5f, w * 0.98f);
                    gp.AddBezier(w * 0.5f, w * 0.98f, w * 0.3f, w * 0.9f, w * 0.08f, w * 0.78f, w * 0.08f, w * 0.5f);
                    gp.AddLine(w * 0.08f, w * 0.5f, w * 0.08f, w * 0.17f);
                    gp.CloseFigure();
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 58, 140, 245))) g.FillPath(b, gp);
                }
                using (Pen p = new Pen(Color.White, Math.Max(1.5f, w * 0.11f)))
                {
                    p.StartCap = System.Drawing.Drawing2D.LineCap.Round; p.EndCap = System.Drawing.Drawing2D.LineCap.Round; p.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                    g.DrawLines(p, new PointF[] { new PointF(w * 0.29f, w * 0.5f), new PointF(w * 0.44f, w * 0.66f), new PointF(w * 0.72f, w * 0.35f) });
                }
                if (red)
                {
                    float d = s * 0.5f;
                    RectangleF dot = new RectangleF(s - d - 0.5f, s - d - 0.5f, d, d);
                    using (SolidBrush wb = new SolidBrush(Color.White)) g.FillEllipse(wb, dot.X - 1, dot.Y - 1, dot.Width + 2, dot.Height + 2);
                    using (SolidBrush r = new SolidBrush(Color.FromArgb(255, 225, 50, 50))) g.FillEllipse(r, dot);
                }
            }
            IntPtr h = bmp.GetHicon();
            try { using (Icon tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone(); }
            finally { DestroyIcon(h); }
        }
    }

    static void Create()
    {
        icoOk = Make(false);
        icoAlert = Make(true);
        ni = new NotifyIcon();
        ni.Icon = alert ? icoAlert : icoOk;
        ContextMenuStrip m = new ContextMenuStrip();
        ToolStripMenuItem open = new ToolStripMenuItem(L.T("Відкрити «Безпеку»"));
        open.Font = new Font(open.Font, FontStyle.Bold);
        open.Click += delegate { Open(); };
        ToolStripMenuItem scan = new ToolStripMenuItem(L.T("Сканувати зараз"));
        scan.Click += delegate { ScanNow(); };
        miStatus = new ToolStripMenuItem("");
        miStatus.Enabled = false;
        ToolStripMenuItem off = new ToolStripMenuItem(L.T("Вимкнути фонове стеження"));
        off.Click += delegate { Off(); };
        m.Items.Add(open);
        m.Items.Add(scan);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(miStatus);
        m.Items.Add(off);
        m.Opening += delegate { Refresh(); };
        ni.ContextMenuStrip = m;
        ni.MouseClick += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Open(); };
        ni.Visible = true;
        if (!hooked) { hooked = true; Application.ApplicationExit += delegate { Remove(); }; }
    }

    static void Remove()
    {
        try
        {
            if (ni != null) { ni.Visible = false; if (ni.ContextMenuStrip != null) ni.ContextMenuStrip.Dispose(); ni.Dispose(); ni = null; }
            if (icoOk != null) { icoOk.Dispose(); icoOk = null; }
            if (icoAlert != null) { icoAlert.Dispose(); icoAlert = null; }
        }
        catch { }
    }

    static void Open() { try { if (bar != null) bar.OpenSecurity(); } catch (Exception ex) { Util.ErrorLog(ex); } }

    static void ScanNow()
    {
        Open();
        string ac;
        try { if (!SecScan.Busy && !SecScan.AntiCheatRunning(out ac)) SecScan.Run(true); }       // з античітом - лише відкриваємо вікно: там є вибір
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    static void Off()
    {
        SecWatch.SetWatch(false);
        try { ToastForm.ShowClick(bar, L.T("Безпека: стеження вимкнено"), L.T("Значок прибрано. Увімкнути знову можна у вікні «Безпека» - клік по цьому повідомленню відкриє його."), "shield", delegate { bar.OpenSecurity(); }); }
        catch { }
    }

    // нові знахідки: червона крапка до відкриття вікна «Безпеки»
    public static void Alert(bool on)
    {
        if (alert == on) return;
        alert = on;
        Refresh();
    }

    public static void Refresh()
    {
        if (ni == null) return;
        try
        {
            Icon want = alert ? icoAlert : icoOk;
            if (want != null && !object.ReferenceEquals(ni.Icon, want)) ni.Icon = want;
            string st = Games.CurrentPid > 0 ? L.T("пауза (запущена гра)") : (alert ? L.T("є нові знахідки") : L.T("стеження ввімкнено"));
            string tip = L.T("NeoBar · Безпека: ") + st;
            if (ni.Text != tip) ni.Text = tip;
            if (miStatus != null)
                miStatus.Text = SecScan.LastAt == DateTime.MinValue ? st : st + L.T(" · перевірено о ") + SecScan.LastAt.ToString("HH:mm");
        }
        catch { }
    }
}
