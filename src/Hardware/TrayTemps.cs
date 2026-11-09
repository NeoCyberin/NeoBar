using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// ======================================================================
//  Температури в треї: для ЦП, ГП і hotspot ГП - окремий значок із числом (градуси) обраного кольору.
//  Вмикається в налаштуваннях («Панель»). Значки малюються лише коли число змінилося; у грі й повноекранній
//  програмі зникають разом з іншими датчиками панелі.
// ======================================================================
static class TrayTemps
{
    sealed class Slot
    {
        public string Name = "";
        public bool On;
        public Color Col;
        public NotifyIcon Ni;
        public Icon Ico;
        public string Shown = "";
        public int ColArgb;
    }

    static readonly Slot[] slots = { new Slot(), new Slot(), new Slot() };
    static NeoBar bar;
    static bool hooked, updHooked, running, one;

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    static float Value(int i)
    {
        return i == 0 ? Sensors.CpuTemp : (i == 1 ? Sensors.GpuTemp : Sensors.Hotspot);
    }

    // викликається при кожному перезавантаженні панелі й зміні паузи (гра на весь екран)
    public static void Configure(Cfg c, NeoBar b, bool run)
    {
        bar = b;
        slots[0].On = c.TrayCpu; slots[1].On = c.TrayGpu; slots[2].On = c.TrayHot;
        slots[0].Col = c.TrayCpuCol; slots[1].Col = c.TrayGpuCol; slots[2].Col = c.TrayHotCol;
        slots[0].Name = L.T("ЦП"); slots[1].Name = L.T("ГП"); slots[2].Name = L.T("ГП") + " hotspot";
        bool any = false, gpu = false;
        for (int i = 0; i < 3; i++) if (slots[i].On) { any = true; if (i > 0) gpu = true; }
        running = any && run;
        one = c.TrayOne;
        Sensors.Use("tray", running ? (gpu ? 2 : 1) : 0);
        if (!updHooked) { updHooked = true; Sensors.Updated += OnData; }
        if (!hooked) { hooked = true; Application.ApplicationExit += delegate { RemoveAll(); }; }
        for (int i = 0; i < 3; i++)
        {
            if (one) { if (i > 0) Remove(slots[i]); }
            else if (running && slots[i].On) Show(i);
            else Remove(slots[i]);
        }
        if (one) { if (running) ShowOne(); else Remove(slots[0]); }
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
        if (one) { if (running) ShowOne(); return; }
        for (int i = 0; i < 3; i++) if (running && slots[i].On) Show(i);
    }

    static string Num(float v) { return float.IsNaN(v) ? "–" : Math.Round(v).ToString(); }

    // один значок: обрані температури одна під одною
    static void ShowOne()
    {
        Slot s = slots[0];
        List<string> tx = new List<string>();
        List<Color> cl = new List<Color>();
        string sig = "", tip = "";
        for (int i = 0; i < 3; i++)
        {
            if (!slots[i].On) continue;
            float v = Value(i);
            tx.Add(Num(v)); cl.Add(slots[i].Col);
            sig += Num(v) + "|" + slots[i].Col.ToArgb() + ";";
            tip += (tip.Length > 0 ? " · " : "") + slots[i].Name + " " + (float.IsNaN(v) ? "—" : Num(v) + "°");
        }
        if (tx.Count == 0) { Remove(s); return; }
        if (s.Ni != null && s.Shown == sig) return;
        try
        {
            Icon ico = Make(tx.ToArray(), cl.ToArray());
            if (s.Ni == null)
            {
                s.Ni = new NotifyIcon();
                ContextMenuStrip m = new ContextMenuStrip();
                ToolStripMenuItem open = new ToolStripMenuItem(L.T("Монітор системи"));
                open.Font = new Font(open.Font, FontStyle.Bold);
                open.Click += delegate { Open(); };
                m.Items.Add(open);
                s.Ni.ContextMenuStrip = m;
                s.Ni.MouseClick += delegate (object o, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Open(); };
            }
            s.Ni.Icon = ico;
            s.Ni.Text = tip.Length > 60 ? tip.Substring(0, 60) : tip;
            s.Ni.Visible = true;
            if (s.Ico != null) s.Ico.Dispose();
            s.Ico = ico;
            s.Shown = sig;
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    static void Show(int i)
    {
        Slot s = slots[i];
        float v = Value(i);
        string t = float.IsNaN(v) ? "–" : Math.Round(v).ToString();
        int col = s.Col.ToArgb();
        if (s.Ni != null && s.Shown == t && s.ColArgb == col) return;
        try
        {
            Icon ico = Make(new string[] { t }, new Color[] { s.Col });
            if (s.Ni == null)
            {
                s.Ni = new NotifyIcon();
                ContextMenuStrip m = new ContextMenuStrip();
                ToolStripMenuItem open = new ToolStripMenuItem(L.T("Монітор системи"));
                open.Font = new Font(open.Font, FontStyle.Bold);
                open.Click += delegate { Open(); };
                m.Items.Add(open);
                s.Ni.ContextMenuStrip = m;
                s.Ni.MouseClick += delegate (object o, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Open(); };
            }
            s.Ni.Icon = ico;
            s.Ni.Text = s.Name + ": " + (float.IsNaN(v) ? "—" : t + " °C");
            s.Ni.Visible = true;
            if (s.Ico != null) s.Ico.Dispose();
            s.Ico = ico;
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
            if (s.Ni != null)
            {
                s.Ni.Visible = false;
                if (s.Ni.ContextMenuStrip != null) s.Ni.ContextMenuStrip.Dispose();
                s.Ni.Dispose();
                s.Ni = null;
            }
            if (s.Ico != null) { s.Ico.Dispose(); s.Ico = null; }
            s.Shown = "";
        }
        catch { }
    }

    static void RemoveAll()
    {
        foreach (Slot s in slots) Remove(s);
    }

    // числа у своїх кольорах на прозорому тлі, одне під одним, з тонкою темною обводкою, щоб читалося і на світлій, і на темній панелі завдань
    static Icon Make(string[] text, Color[] col)
    {
        int s = Math.Max(16, SystemInformation.SmallIconSize.Width);
        int n = text.Length;
        using (Bitmap bmp = new Bitmap(s, s, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float rowH = s / (float)n;
            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                for (int i = 0; i < n; i++)
                {
                    string tx = text[i];
                    float px = n == 1 ? (tx.Length >= 3 ? s * 0.58f : s * 0.80f) : rowH * 1.02f;
                    Font f = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel);
                    try
                    {
                        // не ширше за значок
                        while (f.Size > 5 && g.MeasureString(tx, f, 1000, sf).Width > s)
                        {
                            Font nf = new Font("Segoe UI", f.Size - 1, FontStyle.Bold, GraphicsUnit.Pixel);
                            f.Dispose(); f = nf;
                        }
                        RectangleF r = new RectangleF(0, i * rowH - (n == 1 ? s * 0.04f : 0), s, rowH);
                        float o = n > 1 ? (s >= 32 ? 1f : 0.6f) : (s >= 24 ? 1.5f : 1f);
                        using (SolidBrush sh = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
                        {
                            g.DrawString(tx, f, sh, new RectangleF(r.X + o, r.Y, r.Width, r.Height), sf);
                            g.DrawString(tx, f, sh, new RectangleF(r.X - o, r.Y, r.Width, r.Height), sf);
                            g.DrawString(tx, f, sh, new RectangleF(r.X, r.Y + o, r.Width, r.Height), sf);
                            g.DrawString(tx, f, sh, new RectangleF(r.X, r.Y - o, r.Width, r.Height), sf);
                        }
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(255, col[i].R, col[i].G, col[i].B)))
                            g.DrawString(tx, f, b, r, sf);
                    }
                    finally { f.Dispose(); }
                }
            }
            IntPtr h = bmp.GetHicon();
            try { using (Icon tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone(); }
            finally { DestroyIcon(h); }
        }
    }
}
