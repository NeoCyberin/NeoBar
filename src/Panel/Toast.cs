using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// ----------------------------------------------------------------------
//  Сповіщення в правому нижньому куті (у стилі теми панелі)
// ----------------------------------------------------------------------
sealed class ToastForm : Form
{
    static readonly List<ToastForm> open = new List<ToastForm>();
    readonly Pal p;
    readonly float sc;
    readonly string title, body, glyph;
    readonly bool alarm;
    readonly Action snooze;
    Action click;                     // клік по самому сповіщенню (не по кнопках)
    readonly Font ft, fb, fs;
    readonly Timer life = new Timer(), slide = new Timer();
    int targetX, hot = -1;
    Rectangle rSnooze, rClose, rX;
    int snoozeMin = 5;
    readonly Theme t;

    int Px(float v) { return (int)Math.Round(v * sc); }

    // сповіщення, клік по якому щось відкриває
    public static void ShowClick(NeoBar owner, string title, string body, string glyph, Action click)
    {
        try
        {
            ToastForm f = new ToastForm(owner, title, body, glyph, false, null);
            f.click = click;
            f.Cursor = Cursors.Hand;
            open.Add(f);
            f.Restack(true);
            Win.ShowWindow(f.Handle, 4);
            f.Visible = true;
            f.slide.Start();
            Chime.Play("chime", false, 100);
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    public static void Show(NeoBar owner, string title, string body, string glyph, bool alarm, Action snooze)
    {
        Show(owner, title, body, glyph, alarm, snooze, null);
    }

    public static void Show(NeoBar owner, string title, string body, string glyph, bool alarm, Action snooze, string sound)
    {
        Show(owner, title, body, glyph, alarm, snooze, sound, ClockData.SnoozeMin);
    }

    public static void Show(NeoBar owner, string title, string body, string glyph, bool alarm, Action snooze, string sound, int snoozeMin)
    {
        Show(owner, title, body, glyph, alarm, snooze, sound, snoozeMin, 100);
    }

    public static void Show(NeoBar owner, string title, string body, string glyph, bool alarm, Action snooze, string sound, int snoozeMin, int volume)
    {
        try
        {
            ToastForm f = new ToastForm(owner, title, body, glyph, alarm, snooze);
            f.snoozeMin = snoozeMin;
            open.Add(f);
            f.Restack(true);
            Win.ShowWindow(f.Handle, 4);   // SW_SHOWNOACTIVATE
            f.Visible = true;
            f.slide.Start();
            if (sound != "none") Chime.Play(sound ?? "chime", alarm, volume);      // "none" - без звуку (його вже грає ефект свята)
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    ToastForm(NeoBar owner, string title, string body, string glyph, bool alarm, Action snooze)
    {
        t = owner.CurrentTheme;
        sc = owner.Dpi;
        p = Pal.Of(t, owner.LabelFont, sc);
        this.title = title; this.body = body; this.glyph = glyph; this.alarm = alarm; this.snooze = snooze;
        ft = p.Font(11f, FontStyle.Bold);
        fb = p.Font(9.5f, FontStyle.Regular);
        fs = p.Font(9f, FontStyle.Bold);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FormBorderStyle = FormBorderStyle.None;
        AppIcon.Apply(this);
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = p.Bg;
        Size = new Size(Px(340), Px(alarm || snooze != null ? 132 : 96));
        // будильник і таймер звучать, поки сповіщення не закрити; звичайні зникають самі
        life.Interval = 9000;
        life.Tick += delegate { life.Stop(); Dismiss(); };
        if (!alarm) life.Start();
        slide.Interval = 15;
        slide.Tick += delegate
        {
            int dx = targetX - Left;
            if (Math.Abs(dx) <= 2) { Left = targetX; slide.Stop(); return; }
            Left += dx / 3 + Math.Sign(dx);
        };
        try
        {
            using (GraphicsPath gp = Util.Round(new Rectangle(0, 0, Width, Height), p.Rad + Px(4))) Util.SetRegion(this, gp);
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x08000000 | 0x8;   // TOOLWINDOW | NOACTIVATE | TOPMOST
            cp.ClassStyle |= 0x20000;
            return cp;
        }
    }

    void Restack(bool initial)
    {
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        int y = wa.Bottom - Px(12);
        for (int i = 0; i < open.Count; i++)
        {
            ToastForm f = open[i];
            y -= f.Height;
            f.targetX = wa.Right - f.Width - Px(12);
            if (f == this && initial) f.Location = new Point(wa.Right, y);
            else f.Top = y;
            if (!f.slide.Enabled && f.Left != f.targetX) f.slide.Start();
            y -= Px(10);
        }
    }

    void Dismiss()
    {
        if (alarm || open.Count <= 1) Chime.Stop();
        open.Remove(this);
        Close();
        foreach (ToastForm f in open) { f.Restack(false); break; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        PopStyle.Backdrop(g, t, ClientRectangle);
        Color acc = p.Acc;
        using (SolidBrush b = new SolidBrush(acc)) g.FillRectangle(b, 0, 0, Px(6), Height);
        Rectangle ic = new Rectangle(Px(20), Px(18), Px(34), Px(34));
        using (SolidBrush b = new SolidBrush(Util.Blend(p.Bg, acc, 0.3f))) g.FillEllipse(b, ic);
        Ico.Draw(g, glyph ?? "bell", new RectangleF(ic.X + Px(8), ic.Y + Px(8), ic.Width - Px(16), ic.Height - Px(16)), acc);
        int tx = ic.Right + Px(12);
        TextRenderer.DrawText(g, title, ft, new Rectangle(tx, Px(12), Width - tx - Px(36), Px(26)), p.Fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, body, fb, new Rectangle(tx, Px(38), Width - tx - Px(14), Px(44)), p.Sub,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        rX = new Rectangle(Width - Px(32), Px(8), Px(24), Px(24));
        if (hot == 0) using (SolidBrush b = new SolidBrush(p.Card)) g.FillEllipse(b, rX);
        Ico.Draw(g, "close", new RectangleF(rX.X + Px(7), rX.Y + Px(7), Px(10), Px(10)), hot == 0 ? p.Fg : p.Sub);
        rSnooze = rClose = Rectangle.Empty;
        if (alarm || snooze != null)
        {
            int bw = Px(130), bh = Px(30), by = Height - bh - Px(14);
            rClose = new Rectangle(Width - bw - Px(14), by, bw, bh);
            if (snooze != null) rSnooze = new Rectangle(rClose.X - bw - Px(10), by, bw, bh);
            if (snooze != null) DrawBtn(g, rSnooze, L.T("Відкласти ") + snoozeMin + L.T(" хв"), hot == 1, false);
            DrawBtn(g, rClose, alarm ? L.T("Вимкнути") : L.T("Закрити"), hot == 2, true);
        }
        using (Pen ep = new Pen(Util.Blend(p.Bg, Util.Opaque(t.Edge), 0.8f), 1f))
        using (GraphicsPath gp = Util.Round(new Rectangle(0, 0, Width - 1, Height - 1), p.Rad + Px(4)))
            g.DrawPath(ep, gp);
    }

    void DrawBtn(Graphics g, Rectangle r, string text, bool h, bool primary)
    {
        Color fill = primary ? Util.Blend(p.Bg, p.Acc, h ? 0.62f : 0.48f) : Util.Blend(p.Card, p.Fg, h ? 0.12f : 0.05f);
        using (SolidBrush b = new SolidBrush(fill)) Util.FillRound(g, b, r, Math.Min(p.Rad, r.Height / 2));
        TextRenderer.DrawText(g, text, fs, r, primary ? Util.Readable(p.Fg, fill, 110) : p.Fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    int HitAt(Point pt)
    {
        if (rX.Contains(pt)) return 0;
        if (rSnooze.Contains(pt)) return 1;
        if (rClose.Contains(pt)) return 2;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = HitAt(e.Location);
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        if (h != hot) { hot = h; Invalidate(); }
        if (!alarm) { life.Stop(); life.Start(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int h = HitAt(e.Location);
        if (h == 1 && snooze != null) { Chime.Stop(); snooze(); Dismiss(); return; }
        if (h < 0 && click != null) { Action a = click; Dismiss(); try { a(); } catch (Exception ex) { Util.ErrorLog(ex); } return; }
        if (h == 0 || h == 2 || !alarm) Dismiss();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { life.Dispose(); slide.Dispose(); ft.Dispose(); fb.Dispose(); fs.Dispose(); }
        base.Dispose(disposing);
    }
}
