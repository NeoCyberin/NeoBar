using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// ======================================================================
//  Спливні вікна: папка-група (плитка), заокруглені меню (вікно пошуку - SearchForm.cs)
// ======================================================================
static class PopStyle
{
    // однотонний непрозорий фон для спливних вікон у стилі теми
    public static Color Bg(Theme t)
    {
        if (t.Glass) return Color.FromArgb(28, 32, 44);
        return Util.Opaque(t.Bg);
    }

    public static Color Text(Theme t)
    {
        return Util.Opaque(t.Text.A < 255 ? Util.Blend(Bg(t), Util.Opaque(t.Text), t.Text.A / 255f) : t.Text);
    }

    public static void Backdrop(Graphics g, Theme t, Rectangle r) { Backdrop(g, t, r, 0, 0); }

    // head > 0 - велике вікно з картками й полями (розширена панель): градієнт теми в шапці такої висоти,
    // тло - той самий градієнт, але м'якший (ближчий до суцільного кольору полів), і прикраси у вільних місцях;
    // sc - масштаб вікна (для розміру прикрас)
    public static void Backdrop(Graphics g, Theme t, Rectangle r, int head, float sc)
    {
        if (Festive.Has(t))
        {
            if (head > 0)
            {
                Festive.Fill(g, t, r, r, 90f, 0.55f);
                Festive.Decor(g, t, Rectangle.FromLTRB(r.X, r.Y + head, r.Right, r.Bottom), sc > 0 ? sc : 1f, true);
                Festive.Fill(g, t, new RectangleF(r.X, r.Y, r.Width, head), new RectangleF(r.X, r.Y, r.Width, head), 0f, 0);
            }
            else Festive.Fill(g, t, r, r, 60f, 0);
            return;
        }
        // тло без згладжування: у режимі AntiAlias крайні рядки заливаються наполовину і крізь них проступає вміст буфера
        SmoothingMode sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        try
        {
            if (t.Sketchy)
            {
                using (TextureBrush tb = new TextureBrush(Sk.Paper(t.Bg))) g.FillRectangle(tb, r);
                return;
            }
            using (SolidBrush b = new SolidBrush(Bg(t))) g.FillRectangle(b, r);
        }
        finally { g.SmoothingMode = sm; }
    }
}

// Вміст групи, що відкривається біля папки на панелі
sealed class FlyoutForm : Form
{
    readonly NeoBar owner;
    readonly Theme t;
    readonly Font f;
    readonly float sc;
    readonly List<Entry> kids;
    public readonly Entry Head;
    int cols, rows, cellW, cellH, titleH, pad, hover = -1, sl;
    readonly bool labels;
    readonly System.Windows.Forms.Timer fade = new System.Windows.Forms.Timer();
    readonly bool anim;

    int Px(float v) { return (int)Math.Round(v * sc); }

    public FlyoutForm(NeoBar owner, Entry head)
    {
        this.owner = owner;
        Head = head;
        t = owner.CurrentTheme;
        f = owner.LabelFont;
        sc = owner.Dpi;
        kids = head.Kids;
        labels = owner.Config.Labels;
        anim = owner.Config.Anim;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FormBorderStyle = FormBorderStyle.None;
        AppIcon.Apply(this);
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        BackColor = PopStyle.Bg(t);

        sl = Px(36);
        foreach (Entry k in kids) if (k.Icon != null) { sl = k.Icon.Width; break; }
        pad = Px(8);
        cellW = Math.Max(Px(74), sl + Px(16));
        cellH = Px(8) + sl + (labels ? 1 + f.Height + Px(2) : 0) + Px(5);
        int n = Math.Max(1, kids.Count);
        cols = Math.Max(1, Math.Min(5, (int)Math.Ceiling(Math.Sqrt(n * 1.3))));
        if (n >= 2) cols = Math.Max(2, cols);
        rows = (n + cols - 1) / cols;
        titleH = f.Height + Px(14);
        ClientSize = new Size(cols * cellW + pad * 2, titleH + rows * cellH + pad * 2);
        // заокруглені кути за радіусом теми (раніше вікно групи завжди було з гострими)
        int fr = Px(Math.Max(0, t.Radius)) + Px(2);
        if (t.Radius > 1 && !t.Sketchy)
        {
            try { using (GraphicsPath gp = Util.Round(new Rectangle(0, 0, Width, Height), fr)) Util.SetRegion(this, gp); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }

        fade.Interval = 16;
        fade.Tick += delegate
        {
            double o = Opacity + 0.16;
            if (o >= 1.0) { o = 1.0; fade.Stop(); }
            Opacity = o;
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x80; // TOOLWINDOW
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return cp;
        }
    }

    public void ShowAt(Rectangle tile, string side)
    {
        Rectangle wa = Screen.FromRectangle(tile).WorkingArea;
        int gap = Px(6), x, y;
        switch (side)
        {
            case "left": x = tile.Right + gap; y = tile.Top + tile.Height / 2 - Height / 2; break;
            case "top": y = tile.Bottom + gap; x = tile.Left + tile.Width / 2 - Width / 2; break;
            case "bottom": y = tile.Top - Height - gap; x = tile.Left + tile.Width / 2 - Width / 2; break;
            default: x = tile.Left - Width - gap; y = tile.Top + tile.Height / 2 - Height / 2; break;
        }
        x = Math.Max(wa.Left + 4, Math.Min(wa.Right - Width - 4, x));
        y = Math.Max(wa.Top + 4, Math.Min(wa.Bottom - Height - 4, y));
        Location = new Point(x, y);
        if (anim) { Opacity = 0.0; fade.Start(); }
        Show();
        Activate();
    }

    Rectangle CellRect(int i)
    {
        return new Rectangle(pad + (i % cols) * cellW, pad + titleH + (i / cols) * cellH, cellW, cellH);
    }

    int HitCell(Point p)
    {
        for (int i = 0; i < kids.Count; i++) if (CellRect(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle all = new Rectangle(0, 0, Width, Height);
        PopStyle.Backdrop(g, t, all);
        Color txt = PopStyle.Text(t);
        Color col = head_col();

        TextRenderer.DrawText(g, Head.Name + "  (" + kids.Count + ")", f, new Rectangle(pad + Px(4), pad, Width - pad * 2, titleH - Px(4)),
            Util.Blend(col, txt, 0.4f), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        using (Pen up = new Pen(col, Math.Max(1, Px(2))))
            g.DrawLine(up, pad + Px(4), pad + titleH - Px(4), Width - pad - Px(4), pad + titleH - Px(4));

        for (int i = 0; i < kids.Count; i++)
        {
            Entry k = kids[i];
            Rectangle r = CellRect(i);
            Rectangle cr = Rectangle.Inflate(r, -Px(3), -Px(1));
            if (i == hover)
                using (SolidBrush hb = new SolidBrush(t.Hover))
                    Util.FillRound(g, hb, cr, t.Radius);
            if (k.Icon != null)
                g.DrawImage(k.Icon, new Rectangle(r.X + (r.Width - sl) / 2, r.Y + Px(8), sl, sl));
            if (labels)
                TextRenderer.DrawText(g, k.Name, f, new Rectangle(cr.X + Px(3), r.Y + Px(8) + sl + 1, cr.Width - Px(6), f.Height + 2), txt,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
        if (dragStarted && dropSlot >= 0 && kids.Count > 0)
            using (SolidBrush lb = new SolidBrush(col)) g.FillRectangle(lb, SlotLine(dropSlot));
        using (Pen ep = new Pen(Color.FromArgb(200, t.Edge), Math.Max(1, Px(t.EdgeW))))
        {
            if (t.Radius > 1 && !t.Sketchy)
                using (GraphicsPath gp = Util.Round(new Rectangle(0, 0, Width - 1, Height - 1), Px(Math.Max(0, t.Radius)) + Px(2)))
                    g.DrawPath(ep, gp);
            else g.DrawRectangle(ep, 0, 0, Width - 1, Height - 1);
        }
    }

    Color head_col()
    {
        return Head.GroupColor.IsEmpty ? Util.Opaque(t.Accent) : Head.GroupColor;
    }

    int downCell = -1, dropSlot = -1;
    Point downAt;
    bool dragStarted;

    // слот вставки в межах групи (0..kids.Count) за положенням курсора
    int SlotAtPoint(Point p)
    {
        int n = kids.Count;
        if (n == 0) return 0;
        int row = Math.Max(0, Math.Min(rows - 1, (p.Y - pad - titleH) / cellH));
        int col = Math.Max(0, Math.Min(cols - 1, (p.X - pad) / cellW));
        int idx = row * cols + col;
        if (idx >= n) return n;
        Rectangle r = CellRect(idx);
        return p.X > r.X + r.Width / 2 ? idx + 1 : idx;
    }

    Rectangle SlotLine(int s)
    {
        int n = kids.Count;
        int idx = Math.Min(s, n - 1);
        Rectangle r = CellRect(Math.Max(0, idx));
        int x = s >= n ? r.Right : r.X;
        return new Rectangle(x - Px(1), r.Y + Px(4), Math.Max(2, Px(3)), r.Height - Px(8));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        downCell = e.Button == MouseButtons.Left ? HitCell(e.Location) : -1;
        downAt = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        // потягнули елемент з вікна групи - далі його несе панель: можна кинути в іншу групу або на інше місце
        if (downCell >= 0 && !dragStarted && (e.Button & MouseButtons.Left) != 0)
        {
            Size ds = SystemInformation.DragSize;
            if (Math.Abs(e.X - downAt.X) >= ds.Width || Math.Abs(e.Y - downAt.Y) >= ds.Height)
            {
                dragStarted = true;
                Capture = true;
                Cursor = Cursors.SizeAll;
            }
        }
        if (dragStarted && downCell >= 0 && downCell < kids.Count)
        {
            if (!ClientRectangle.Contains(e.Location))
            {
                // вийшли за межі вікна групи - далі елемент несе панель (інша група чи інше місце)
                Entry k = kids[downCell];
                owner.BeginDragFromFlyout(k);
                Close();
                return;
            }
            int s = SlotAtPoint(e.Location);
            if (s != dropSlot) { dropSlot = s; Invalidate(); }
            return;
        }
        int i = HitCell(e.Location);
        if (i == hover) return;
        hover = i;
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hover = -1;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (dragStarted && downCell >= 0 && downCell < kids.Count)
        {
            // відпустили всередині вікна групи - змінити порядок у групі
            Entry moved = kids[downCell];
            Entry before = dropSlot >= 0 && dropSlot < kids.Count ? kids[dropSlot] : null;
            int slot = dropSlot;
            downCell = -1; dragStarted = false; dropSlot = -1;
            Capture = false;
            Cursor = Cursors.Default;
            if (slot >= 0) owner.MoveWithinGroup(Head, moved, before);
            Invalidate();
            return;
        }
        downCell = -1;
        if (dragStarted) return;
        int i = HitCell(e.Location);
        if (i < 0) return;
        Entry k = kids[i];
        if (e.Button == MouseButtons.Right) { owner.ShowMenuFor(k, this, e.Location); return; }
        if (e.Button != MouseButtons.Left) return;
        Close();
        owner.LaunchEntry(k, false);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (owner.MenuIsOpen) return;
        BeginInvoke(new Action(delegate { if (!IsDisposed && !owner.MenuIsOpen) Close(); }));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) fade.Dispose();
        base.Dispose(disposing);
    }
}

// Список без мерехтіння: подвійна буферизація, без стирання фону
sealed class FastList : ListBox
{
    public FastList()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        DoubleBuffered = true;
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        using (SolidBrush b = new SolidBrush(BackColor)) e.Graphics.FillRectangle(b, e.ClipRectangle);
        for (int i = TopIndex; i < Items.Count; i++)
        {
            Rectangle r = GetItemRectangle(i);
            if (r.Top > ClientRectangle.Bottom) break;
            if (!r.IntersectsWith(e.ClipRectangle)) continue;
            DrawItemState st = DrawItemState.Default;
            if (SelectedIndices.Contains(i)) st |= DrawItemState.Selected;
            OnDrawItem(new DrawItemEventArgs(e.Graphics, Font, r, i, st, ForeColor, BackColor));
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x14) { m.Result = (IntPtr)1; return; }   // WM_ERASEBKGND
        base.WndProc(ref m);
    }
}

// Заокруглене контекстне меню в кольорах теми
sealed class RoundMenu : ToolStripProfessionalRenderer
{
    readonly Color bg, edge, acc, txt;
    readonly int rad;

    RoundMenu(Color bg, Color txt, Color acc, Color edge, int rad) : base(new Cols(Util.Blend(bg, txt, 0.08f), acc))
    {
        this.bg = Util.Blend(bg, txt, 0.08f);
        this.acc = acc;
        this.txt = txt;
        this.edge = edge;
        this.rad = rad;
        RoundedEdges = false;
    }

    sealed class Cols : ProfessionalColorTable
    {
        readonly Color b, a;
        public Cols(Color b, Color a) { this.b = b; this.a = a; UseSystemColors = false; }
        public override Color ToolStripDropDownBackground { get { return b; } }
        public override Color ImageMarginGradientBegin { get { return b; } }
        public override Color ImageMarginGradientMiddle { get { return b; } }
        public override Color ImageMarginGradientEnd { get { return b; } }
        public override Color MenuItemSelected { get { return Util.Blend(b, a, 0.35f); } }
        public override Color MenuItemBorder { get { return Util.Blend(b, a, 0.35f); } }
        public override Color MenuBorder { get { return b; } }
        public override Color CheckBackground { get { return Util.Blend(b, a, 0.5f); } }
        public override Color CheckSelectedBackground { get { return Util.Blend(b, a, 0.5f); } }
    }

    public static void Apply(ContextMenuStrip m, Color bg, Color txt, Color acc, Color edge, int rad)
    {
        m.Renderer = new RoundMenu(bg, txt, acc, edge, rad);
        m.Padding = new Padding(3);
        Rounded(m, rad);
        // підменю отримують той самий вигляд
        m.Opening += delegate { Theme(m, txt); };
    }

    static void Rounded(ToolStripDropDown m, int rad)
    {
        m.Opened += delegate
        {
            try
            {
                using (GraphicsPath gp = Util.Round(new Rectangle(0, 0, m.Width, m.Height), rad)) Util.SetRegion(m, gp);
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
        };
    }

    // колір тексту всіх пунктів і стиль підменю (викликається автоматично перед показом)
    public static void Theme(ToolStrip m, Color txt)
    {
        RoundMenu rm = m.Renderer as RoundMenu;
        foreach (ToolStripItem it in m.Items)
        {
            if (!(it is ToolStripSeparator)) it.ForeColor = txt;
            ToolStripMenuItem mi = it as ToolStripMenuItem;
            if (mi == null || !mi.HasDropDownItems) continue;
            if (rm != null && !(mi.DropDown.Renderer is RoundMenu))
            {
                mi.DropDown.Renderer = rm;
                mi.DropDown.Padding = new Padding(3);
                Rounded(mi.DropDown, rm.rad);
            }
            Theme(mi.DropDown, txt);
        }
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath gp = Util.Round(new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), rad))
        using (Pen p = new Pen(edge, 1.2f))
            e.Graphics.DrawPath(p, gp);
    }

    // смуга зліва (для позначок) зливається з фоном меню
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    // позначка «вибрано»: заокруглена плитка в акценті теми з галочкою, замість системного синього квадрата
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle ir = e.ImageRectangle;
        int s = Math.Max(10, Math.Min(Math.Min(ir.Width, ir.Height) + 2, 18));
        Rectangle r = new Rectangle(ir.X + (ir.Width - s) / 2, ir.Y + (ir.Height - s) / 2, s, s);
        Color fill = Util.Blend(bg, acc, 0.38f), line = Util.Blend(bg, acc, 0.85f);
        using (GraphicsPath gp = Util.Round(r, Math.Max(3, s / 4)))
        {
            using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, gp);
            using (Pen p = new Pen(line, 1.2f)) g.DrawPath(p, gp);
        }
        PointF[] ck = new PointF[] {
            new PointF(r.X + s * 0.25f, r.Y + s * 0.53f),
            new PointF(r.X + s * 0.43f, r.Y + s * 0.71f),
            new PointF(r.X + s * 0.76f, r.Y + s * 0.31f) };
        using (Pen p = new Pen(txt, Math.Max(1.6f, s / 8f)))
        {
            p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
            g.DrawLines(p, ck);
        }
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
        using (SolidBrush b = new SolidBrush(Util.Blend(bg, acc, 0.35f)))
            Util.FillRound(e.Graphics, b, r, rad / 2);
    }
}

// Контекстне меню вікна, що не отримує фокус (панель, віджети на столі), саме не закривається при кліку повз нього:
// стежимо за мишею й закриваємо меню, коли натиснули кнопку поза ним (робочий стіл, інша програма).
static class MenuGuard
{
    public static void Watch(ContextMenuStrip m)
    {
        System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
        t.Interval = 40;
        bool armed = false;
        IntPtr fg0 = IntPtr.Zero;
        try { fg0 = Win.GetForegroundWindow(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        t.Tick += delegate
        {
            if (m.IsDisposed || !m.Visible) { t.Stop(); t.Dispose(); return; }
            bool down = false;
            try { down = (Win.GetAsyncKeyState(1) & 0x8000) != 0 || (Win.GetAsyncKeyState(2) & 0x8000) != 0 || (Win.GetAsyncKeyState(4) & 0x8000) != 0; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            if (!down) { armed = true; }
            bool outside = false;
            if (armed && down)
            {
                Point p = Cursor.Position;
                outside = !Inside(m, p);
            }
            // інша програма отримала фокус (Alt+Tab, Win)
            try
            {
                IntPtr fg = Win.GetForegroundWindow();
                if (armed && fg != fg0 && fg != IntPtr.Zero && fg != m.Handle) { fg0 = fg; if (!down) outside = true; }
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
            if (outside) { t.Stop(); t.Dispose(); try { m.Close(ToolStripDropDownCloseReason.AppFocusChange); } catch (Exception skipEx) { SbLog.Skip(skipEx); } }
        };
        t.Start();
        m.Closed += delegate { try { t.Stop(); t.Dispose(); } catch (Exception skipEx) { SbLog.Skip(skipEx); } };
    }

    static bool Inside(ToolStripDropDown d, Point p)
    {
        if (d.Bounds.Contains(p)) return true;
        foreach (ToolStripItem it in d.Items)
        {
            ToolStripDropDownItem di = it as ToolStripDropDownItem;
            if (di != null && di.DropDown.Visible && Inside(di.DropDown, p)) return true;
        }
        return false;
    }
}
