using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// ======================================================================
//  Елементи керування вікна налаштувань, які повністю малюють себе самі (без перехоплення малювання
//  системних елементів - звідти були «уламки» рамок у кутах): кнопка, перемикач, радіокнопка,
//  список вибору з власним випадним списком, повзунок, числове поле.
//  Поведінка та ж, що у стандартних (події, клавіатура, фокус), вигляд - за темою панелі.
// ======================================================================
interface ISelfPaint { }

static class Ctl
{
    // Самомальовані елементи при першому показі (і після зміни розміру) інколи лишали в куті шматок
    // чужого вмісту, який зникав лише після наведення миші (повного перемальовування).
    // Тож один раз за кадр запитуємо повне перемальовування, коли елемент з'явився або змінив розмір.
    // Заливка тла БЕЗ згладжування. У режимі AntiAlias GDI+ заливає цілий прямокутник лише наполовину по верхньому
    // й лівому краю (центри пікселів - на цілих координатах), і крізь ці напівпрозорі рядки проступає вміст
    // спільного буфера (уламки сусідніх елементів або чорне). Звідси були «хвостики» в кутах кнопок і привиди у перемикачах.
    public static void Floor(Graphics g, Color c, Rectangle r)
    {
        SmoothingMode sm = g.SmoothingMode;
        PixelOffsetMode po = g.PixelOffsetMode;
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.None;
        using (SolidBrush b = new SolidBrush(c)) g.FillRectangle(b, r);
        g.SmoothingMode = sm;
        g.PixelOffsetMode = po;
    }

    // Підкладка під самомальований елемент: на тлі святкової теми - той самий градієнт вікна, а не пласка заливка
    // (пласка заливка давала квадратні «плями» навколо заокруглених кнопок у Хелловіні, Різдві й Дні народження)
    public static void FloorOn(Graphics g, Control c, Color flat, Rectangle r)
    {
        if (Festive.OnBackdrop(c) && Festive.Backdrop(g, c, r)) return;
        Floor(g, flat, r);
    }

    static readonly HashSet<Control> settling = new HashSet<Control>();

    public static void Settle(Control c)
    {
        c.VisibleChanged += delegate { Kick(c); };
        c.SizeChanged += delegate { Kick(c); };
        c.HandleCreated += delegate { Kick(c); };
    }

    static void Kick(Control c)
    {
        if (!c.IsHandleCreated || c.IsDisposed || !settling.Add(c)) return;
        try
        {
            c.BeginInvoke(new Action(delegate
            {
                settling.Remove(c);
                if (!c.IsDisposed && c.Visible) c.Invalidate();
            }));
        }
        catch { settling.Remove(c); }
    }

    // заокруглення полів і кнопок: помірне, щоб прямокутні елементи не ставали «овалами»
    public static int CR(int h)
    {
        if (FormTheme.Square) return SUi.S(3);
        return Math.Max(SUi.S(2), Math.Min(SUi.S(Math.Min(FormTheme.Rad, 6)), h / 3));
    }

    public static void Field(Graphics g, Rectangle r, bool enabled, bool hover, bool focus, Color under)
    {
        Rectangle fr = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
        int rad = CR(fr.Height);
        Color fill = FormTheme.Input;      // як у вкладеного TextBox - інакше навколо тексту видно прямокутник
        Skin.Fill(g, fr, rad, fill);
        Color ec = !enabled ? Util.Blend(under, FormTheme.Line, 0.5f) : (focus ? Util.Blend(FormTheme.Line, FormTheme.Acc, 0.55f) : (hover ? Util.Blend(FormTheme.Line, FormTheme.Fg, 0.25f) : Util.Blend(under, FormTheme.Line, 0.85f)));
        Skin.Edge(g, fr, rad, ec, 1f);
        if (focus && !FormTheme.Sketchy)
            using (Pen p = new Pen(FormTheme.Acc, Math.Max(2f, SUi.S(2))))
                g.DrawLine(p, fr.X + rad, fr.Bottom - 0.5f, fr.Right - rad, fr.Bottom - 0.5f);
    }

    public static void Chevron(Graphics g, float cx, float cy, float a, Color c, bool up)
    {
        float d = up ? -1f : 1f;
        using (Pen p = new Pen(c, Math.Max(1.4f, SUi.S(1.5f))) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(p, new PointF[] { new PointF(cx - a, cy - d * a * 0.45f), new PointF(cx, cy + d * a * 0.55f), new PointF(cx + a, cy - d * a * 0.45f) });
    }

    public static Color Under(Control c)
    {
        if (c is ButtonBase) return SUi.BgOf(c);        // власний BackColor самомальованих елементів після зміни теми застарілий
        Color b = c.BackColor;
        return b.A == 255 ? b : SUi.BgOf(c);
    }

    public static void Clear(Graphics g, Control c)
    {
        if (Festive.OnBackdrop(c) && Festive.Backdrop(g, c, c.ClientRectangle)) return;
        Floor(g, Under(c), c.ClientRectangle);
    }

    // Тло під заокругленим елементом - колір, яким батько справді малює (BgOf знає, що картка малює FormTheme.Card)
    public static void Back(Control c, PaintEventArgs e) { Clear(e.Graphics, c); }

    // заокруглені кути випадних вікон (Windows 11; на старіших тихо ігнорується)
    public static void RoundPopup(IntPtr h)
    {
        try { int v = FormTheme.Square ? 1 : 3; Win.DwmSetWindowAttribute(h, 33, ref v, 4); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }
}

// ----------------------------------------------------------------------
//  Кнопка. Позначки в Tag (через пробіл): primary - головна, danger - небезпечна, ghost - без тла,
//  g:<ключ> - піктограма. Кнопка, якій задано BackColor (зразок кольору), малюється цим кольором.
// ----------------------------------------------------------------------
sealed class SBtn : Button, ISelfPaint
{
    bool hover, down, custom;
    public Color Backdrop = Color.Empty;      // тло під кнопкою, якщо батько малює під нею не своїм кольором
    public LangInfo Flag;                     // прапорець мови замість символу

    public SBtn()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Ctl.Settle(this);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        custom = false;
    }

    // власний колір - лише якщо його задали саме цій кнопці (зміна тла батька сюди не належить)
    public override Color BackColor
    {
        get { return base.BackColor; }
        set { base.BackColor = value; custom = !value.IsEmpty && value.A == 255; Invalidate(); }
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (!Backdrop.IsEmpty) using (SolidBrush b = new SolidBrush(Backdrop)) e.Graphics.FillRectangle(b, ClientRectangle);
        else Ctl.Back(this, e);
    }

    static bool Has(string tag, string what) { return (" " + tag + " ").Contains(" " + what + " "); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = ClientRectangle;
        Color under = Backdrop.IsEmpty ? SUi.BgOf(this) : Backdrop;
        if (Backdrop.IsEmpty) Ctl.FloorOn(g, this, under, r); else Ctl.Floor(g, under, r);
        Rectangle br = new Rectangle(0, 0, r.Width - 1, r.Height - 1);
        string tag = Tag as string ?? "";
        bool danger = Has(tag, "danger"), ghost = Has(tag, "ghost");
        Form f = FindForm();
        bool accent = !custom && (Has(tag, "primary") || (f != null && f.AcceptButton == this));
        Color red = FormTheme.Bad;
        Color fill;
        if (custom) fill = Util.Opaque(BackColor);
        else if (accent) fill = Util.Blend(FormTheme.Bg, FormTheme.Acc, down ? 0.92f : (hover ? 0.82f : (FormTheme.Dark ? 0.7f : 0.86f)));
        else if (danger) fill = Util.Blend(under, red, down ? 0.42f : (hover ? 0.28f : 0.14f));
        else if (ghost) fill = down ? Util.Blend(under, FormTheme.Fg, 0.15f) : (hover ? Util.Blend(under, FormTheme.Fg, 0.08f) : under);
        else fill = Util.Blend(under, FormTheme.Fg, down ? 0.14f : (hover ? 0.1f : 0.06f));
        if (!Enabled) fill = Util.Blend(under, fill, 0.45f);
        int rad = Ctl.CR(br.Height);
        bool filled = fill.ToArgb() != under.ToArgb();
        if (filled)
        {
            if (FormTheme.Plate && !custom)
                using (GraphicsPath gp = Skin.Shape(br, rad))
                using (LinearGradientBrush lg = new LinearGradientBrush(new Rectangle(br.X, br.Y - 1, br.Width, br.Height + 2), Util.Blend(fill, Color.White, 0.14f), Util.Blend(fill, Color.Black, 0.16f), 90f))
                    g.FillPath(lg, gp);
            else Skin.Fill(g, br, rad, fill);
        }
        if (!ghost || hover || Focused)
        {
            Color edge = custom ? Util.Blend(fill, FormTheme.Fg, 0.3f)
                       : accent ? Util.Blend(fill, Color.White, 0.14f)
                       : danger ? Util.Blend(under, red, 0.45f)
                       : Util.Blend(under, FormTheme.Fg, hover ? 0.2f : 0.13f);
            if (FormTheme.Neon && !custom) edge = Util.Blend(edge, FormTheme.Acc, accent ? 0.2f : 0.45f);
            Skin.Edge(g, br, rad, edge, 1f);
        }
        if (Focused && ShowFocusCues && Enabled)
            using (GraphicsPath gp = Skin.Shape(Rectangle.Inflate(br, -2, -2), Math.Max(1, rad - 2)))
            using (Pen p = new Pen(Color.FromArgb(170, accent ? Util.Readable(Color.White, fill, 100) : FormTheme.Acc), 1.5f)) g.DrawPath(p, gp);

        Color tc = custom ? Util.Readable(Util.Lum(fill) > 140 ? Color.Black : Color.White, fill, 100)
                 : accent ? Util.Readable(Color.White, fill, 110) : (danger ? red : FormTheme.Fg);
        if (!Enabled) tc = Util.Blend(fill, tc, 0.45f);
        string gk = "";
        foreach (string t in tag.Split(' ')) if (t.StartsWith("g:")) gk = t.Substring(2);
        int gs = Math.Max(12, (int)Math.Round(Font.Height * 1.05));
        int tw = Text.Length > 0 ? TextRenderer.MeasureText(Text, Font).Width : 0;
        if (Flag != null)
        {
            int fw = gs * 3 / 2, total = fw + (tw > 0 ? SUi.S(8) + tw : 0);
            int ix = Math.Max(SUi.S(8), (r.Width - total) / 2);
            Flags.Draw(g, new Rectangle(ix, (r.Height - gs) / 2, fw, gs), Flag.Flag, Flag.Code);
            if (tw > 0)
                TextRenderer.DrawText(g, Text, Font, new Rectangle(ix + fw + SUi.S(8), 0, r.Width - ix - fw - SUi.S(10), r.Height), tc,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            return;
        }
        if (gk.Length > 0)
        {
            int total = gs + (tw > 0 ? SUi.S(6) + tw : 0);
            int ix = Math.Max(SUi.S(6), (r.Width - total) / 2);
            Color gc = danger || accent || custom ? tc : (Enabled ? Util.Blend(FormTheme.Sub, FormTheme.Fg, hover ? 0.85f : 0.5f) : tc);
            G.Draw(g, gk, new RectangleF(ix, (r.Height - gs) / 2f, gs, gs), gc);
            if (tw > 0)
                TextRenderer.DrawText(g, Text, Font, new Rectangle(ix + gs + SUi.S(6), 0, r.Width - ix - gs - SUi.S(8), r.Height), tc,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            return;
        }
        TextRenderer.DrawText(g, Text, accent ? SUi.Semi : Font, Rectangle.Inflate(r, -SUi.S(6), 0), tc,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}

// ----------------------------------------------------------------------
//  Перемикач (CheckBox). Без тексту - праворуч, ліворуч від нього «Увімкнено / Вимкнено»;
//  з текстом - перемикач і підпис. Ручка пересувається плавно.
// ----------------------------------------------------------------------
sealed class SToggle : CheckBox, ISelfPaint
{
    bool hover;
    float k = -1f;
    Timer t;

    public SToggle()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Ctl.Settle(this);
        FlatStyle = FlatStyle.Flat;
        AutoSize = false;
    }

    float Target { get { return CheckState == CheckState.Checked ? 1f : (CheckState == CheckState.Indeterminate ? 0.5f : 0f); } }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        if (k < 0f || !IsHandleCreated || !Visible) { k = Target; Invalidate(); return; }
        if (t == null)
        {
            t = new Timer();
            t.Interval = 15;
            t.Tick += delegate
            {
                float tg = Target;
                k += (tg - k) * 0.38f;
                if (Math.Abs(tg - k) < 0.02f) { k = tg; t.Stop(); }
                Invalidate();
            };
        }
        t.Start();
    }

    protected override void Dispose(bool disposing) { if (disposing && t != null) { t.Stop(); t.Dispose(); } base.Dispose(disposing); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    // тло малюється в OnPaint (не тут): буфер подвійної буферизації спільний для всіх елементів і не очищується,
    // тож будь-яка прогалина в тлі показувала шматки чужого вмісту (напр. текст сусіднього списку)
    protected override void OnPaintBackground(PaintEventArgs e) { }

    // тло під перемикачем: колір, яким батько справді малює (власний BackColor елемента не береться - після зміни теми він застарілий
    // і давав «плашку»), плюс роздільники рядків картки
    void PaintUnder(Graphics g)
    {
        Ctl.FloorOn(g, this, SUi.BgOf(this), ClientRectangle);
        SCard card = Parent as SCard;
        if (card == null) return;
        GraphicsState st = g.Save();
        try
        {
            g.TranslateTransform(-Left, -Top);
            card.PaintBehind(g, new Rectangle(Left, Top, Width, Height));
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        finally { g.Restore(st); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = ClientRectangle;
        PaintUnder(g);
        Color bg = SUi.BgOf(this);
        if (k < 0f) k = Target;
        int fh = Font.Height;
        bool bare = Text.Length == 0;
        int th = Math.Max(14, (int)Math.Round(fh * (bare ? 1.25 : 1.1)));
        int tw = (int)Math.Round(th * 2.0);
        Rectangle tr = bare ? new Rectangle(r.Right - tw - 2, (r.Height - th) / 2, tw, th) : new Rectangle(1, (r.Height - th) / 2, tw, th);
        bool on = CheckState != CheckState.Unchecked;
        Color acc = FormTheme.Acc;
        Color fill = Enabled ? Util.Blend(acc, FormTheme.Fg, hover ? 0.1f : 0f) : Util.Blend(bg, acc, 0.35f);
        Color offEdge = Util.Blend(bg, FormTheme.Fg, hover && Enabled ? 0.72f : 0.55f);
        int pr = Skin.Pill(th);
        if (on)
        {
            if (FormTheme.Plate)
                using (GraphicsPath gp = Skin.Shape(tr, pr))
                using (LinearGradientBrush lg = new LinearGradientBrush(new Rectangle(tr.X, tr.Y - 1, tr.Width, tr.Height + 2), Util.Blend(fill, Color.White, 0.18f), Util.Blend(fill, Color.Black, 0.18f), 90f))
                    g.FillPath(lg, gp);
            else Skin.Fill(g, tr, pr, fill);
            if (FormTheme.Neon) Skin.Edge(g, tr, pr, acc, 1f);
        }
        else
        {
            Skin.Fill(g, tr, pr, Util.Blend(bg, FormTheme.Fg, hover && Enabled ? 0.07f : 0.025f));
            using (GraphicsPath gp = Skin.Shape(new Rectangle(tr.X, tr.Y, tr.Width - 1, tr.Height - 1), pr))
            using (Pen p = new Pen(Enabled ? offEdge : Util.Blend(bg, FormTheme.Fg, 0.3f), 1.2f)) g.DrawPath(p, gp);
        }
        float kk = Math.Max(0f, Math.Min(1f, k));
        int kn = (int)Math.Round(th * (on ? 0.64f : 0.54f));
        float kx = tr.X + SUi.S(4) + (tr.Width - kn - SUi.S(8)) * kk;
        Color knob = on ? Util.Readable(Color.White, fill, 60) : (Enabled ? offEdge : Util.Blend(bg, FormTheme.Fg, 0.35f));
        using (SolidBrush b = new SolidBrush(knob))
        {
            if (FormTheme.Square) g.FillRectangle(b, kx, tr.Y + (th - kn) / 2f, kn, kn);
            else g.FillEllipse(b, kx, tr.Y + (th - kn) / 2f, kn, kn);
        }
        Color tc = Enabled ? FormTheme.Fg : Util.Blend(bg, FormTheme.Fg, 0.45f);
        if (bare)
            TextRenderer.DrawText(g, on ? L.T("Увімкнено") : L.T("Вимкнено"), Font, new Rectangle(0, 0, tr.X - SUi.S(10), r.Height), Enabled ? (on ? FormTheme.Fg : FormTheme.Sub) : Util.Blend(bg, FormTheme.Sub, 0.6f),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        else
            TextRenderer.DrawText(g, Text, Font, new Rectangle(tr.Right + SUi.S(10), 0, r.Width - tr.Right - SUi.S(10), r.Height), tc,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues && Enabled)
            using (GraphicsPath gp = Skin.Shape(Rectangle.Inflate(tr, 3, 3), pr + 3))
            using (Pen p = new Pen(Color.FromArgb(150, acc), 1.5f)) g.DrawPath(p, gp);
    }
}

// ----------------------------------------------------------------------
//  Радіокнопка
// ----------------------------------------------------------------------
sealed class SRadio : RadioButton, ISelfPaint
{
    bool hover;

    public SRadio()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Ctl.Settle(this);
        FlatStyle = FlatStyle.Flat;
        AutoSize = false;
    }

    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaintBackground(PaintEventArgs e) { Ctl.Back(this, e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color bg = Ctl.Under(this);
        Ctl.FloorOn(g, this, bg, ClientRectangle);     // страховка: фон мальовано й тут
        int d = Math.Max(14, (int)Math.Round(Font.Height * 1.1));
        Rectangle cr = new Rectangle(1, (Height - d) / 2, d, d);
        Color acc = Enabled ? FormTheme.Acc : Util.Blend(bg, FormTheme.Acc, 0.4f);
        if (Checked)
        {
            using (SolidBrush b = new SolidBrush(acc)) g.FillEllipse(b, cr);
            int k = (int)Math.Round(d * (hover ? 0.5f : 0.42f));
            using (SolidBrush b = new SolidBrush(Util.Readable(Color.White, acc, 60))) g.FillEllipse(b, cr.X + (d - k) / 2f, cr.Y + (d - k) / 2f, k, k);
        }
        else
        {
            using (SolidBrush b = new SolidBrush(Util.Blend(bg, FormTheme.Fg, hover ? 0.07f : 0.025f))) g.FillEllipse(b, cr);
            using (Pen p = new Pen(Util.Blend(bg, FormTheme.Fg, Enabled ? (hover ? 0.72f : 0.55f) : 0.3f), 1.2f)) g.DrawEllipse(p, cr.X, cr.Y, cr.Width - 1, cr.Height - 1);
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(cr.Right + SUi.S(8), 0, Width - cr.Right - SUi.S(8), Height),
            Enabled ? FormTheme.Fg : Util.Blend(bg, FormTheme.Fg, 0.45f), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        if (Focused && ShowFocusCues)
            using (Pen p = new Pen(Color.FromArgb(150, FormTheme.Acc), 1.5f)) g.DrawEllipse(p, Rectangle.Inflate(cr, 3, 3));
    }
}

// ----------------------------------------------------------------------
//  Список вибору з власним випадним списком (ті самі властивості й події, що в ComboBox)
// ----------------------------------------------------------------------
sealed class SCombo : Control, ISelfPaint
{
    public sealed class ItemList : IEnumerable
    {
        readonly SCombo o;
        readonly List<object> l = new List<object>();
        internal ItemList(SCombo owner) { o = owner; }
        public int Count { get { return l.Count; } }
        public object this[int i] { get { return l[i]; } set { l[i] = value; o.Invalidate(); } }
        public void Add(object x) { l.Add(x); o.Invalidate(); }
        public void AddRange(object[] xs) { if (xs != null) l.AddRange(xs); o.Invalidate(); }
        public void Clear() { l.Clear(); o.ResetSel(); }
        public int IndexOf(object x) { return l.IndexOf(x); }
        public IEnumerator GetEnumerator() { return l.GetEnumerator(); }
    }

    public readonly ItemList Items;
    public ComboBoxStyle DropDownStyle = ComboBoxStyle.DropDownList;     // лише для сумісності
    public FlatStyle FlatStyle = FlatStyle.Flat;
    public int MaxDropDownItems = 10;
    int sel = -1;
    bool hover, dropped;
    ToolStripDropDown dd;
    DateTime closedAt = DateTime.MinValue;
    public event EventHandler SelectedIndexChanged, DropDownClosed;

    public SCombo()
    {
        Items = new ItemList(this);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
        Ctl.Settle(this);
        TabStop = true;
        Height = SUi.S(32);
    }

    internal void ResetSel() { sel = -1; Invalidate(); }

    public int SelectedIndex
    {
        get { return sel; }
        set
        {
            int v = Math.Max(-1, Math.Min(Items.Count - 1, value));
            if (v == sel) return;
            sel = v;
            Invalidate();
            EventHandler h = SelectedIndexChanged; if (h != null) h(this, EventArgs.Empty);
        }
    }

    public object SelectedItem
    {
        get { return sel >= 0 && sel < Items.Count ? Items[sel] : null; }
        set { SelectedIndex = value == null ? -1 : Items.IndexOf(value); }
    }

    public override string Text { get { return sel >= 0 && sel < Items.Count ? GetItemText(Items[sel]) : ""; } set { } }
    public string GetItemText(object o) { return o == null ? "" : o.ToString(); }
    public void BeginUpdate() { }
    public void EndUpdate() { Invalidate(); }

    public int FindStringExact(string s)
    {
        for (int i = 0; i < Items.Count; i++) if (string.Equals(GetItemText(Items[i]), s, StringComparison.CurrentCultureIgnoreCase)) return i;
        return -1;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaintBackground(PaintEventArgs e) { Ctl.Back(this, e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        Focus();
        // клік по полю, коли список щойно закрився цим самим кліком, - не відкривати знову
        if ((DateTime.Now - closedAt).TotalMilliseconds < 250) return;
        if (dropped) CloseList(); else OpenList();
    }

    protected override bool IsInputKey(Keys k)
    {
        Keys c = k & Keys.KeyCode;
        return c == Keys.Up || c == Keys.Down || c == Keys.Home || c == Keys.End || base.IsInputKey(k);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.F4 || (e.Alt && e.KeyCode == Keys.Down) || e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { OpenList(); e.Handled = true; }
        else if (e.KeyCode == Keys.Up) { SelectedIndex = Math.Max(0, sel - 1); e.Handled = true; }
        else if (e.KeyCode == Keys.Down) { SelectedIndex = Math.Min(Items.Count - 1, sel + 1); e.Handled = true; }
        else if (e.KeyCode == Keys.Home) { SelectedIndex = 0; e.Handled = true; }
        else if (e.KeyCode == Keys.End) { SelectedIndex = Items.Count - 1; e.Handled = true; }
    }

    sealed class PopRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { using (SolidBrush b = new SolidBrush(FormTheme.Input)) e.Graphics.FillRectangle(b, e.AffectedBounds); }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (Pen p = new Pen(FormTheme.Neon ? FormTheme.Acc : Util.Blend(FormTheme.Input, FormTheme.Fg, 0.22f))) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
    }

    void OpenList()
    {
        if (Items.Count == 0 || dropped) return;
        SList l = new SList();
        l.Font = Font;
        l.BackColor = FormTheme.Input;
        l.ForeColor = FormTheme.Fg;
        l.ItemHeight = 30;
        int widest = Width;
        foreach (object o in Items)
        {
            l.Items.Add(GetItemText(o));
            widest = Math.Max(widest, TextRenderer.MeasureText(GetItemText(o), Font).Width + SUi.S(44));
        }
        Rectangle wa = Screen.FromControl(this).WorkingArea;
        int vis = Math.Min(Items.Count, MaxDropDownItems);
        l.Size = new Size(Math.Min(widest, wa.Width / 2), vis * l.ItemHeight + SUi.S(8));
        l.SelectedIndex = sel;
        ToolStripControlHost host = new ToolStripControlHost(l);
        host.Margin = Padding.Empty;
        host.Padding = Padding.Empty;
        host.AutoSize = false;
        host.Size = l.Size;
        dd = new ToolStripDropDown();
        dd.Padding = new Padding(1);
        dd.Renderer = new PopRenderer();
        dd.Items.Add(host);
        dd.HandleCreated += delegate { Ctl.RoundPopup(dd.Handle); };
        ToolStripDropDown me = dd;
        Action pick = delegate
        {
            int i = l.SelectedIndex;
            me.Close();
            if (i >= 0) SelectedIndex = i;
        };
        l.MouseUp += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left && l.IndexFromPoint(e.Location) >= 0) pick(); };
        l.KeyDown += delegate (object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { pick(); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) { me.Close(); e.Handled = true; }
        };
        dd.Closed += delegate
        {
            dropped = false;
            closedAt = DateTime.Now;
            Invalidate();
            EventHandler h = DropDownClosed; if (h != null) h(this, EventArgs.Empty);
            BeginInvoke(new Action(delegate { try { me.Dispose(); } catch (Exception skipEx) { SbLog.Skip(skipEx); } }));
            if (CanFocus) Focus();
        };
        dropped = true;
        Invalidate();
        dd.Show(this, new Point(0, Height + SUi.S(2)));
        l.EnsureVisible(sel);
        l.Focus();
    }

    void CloseList() { if (dd != null && dropped) dd.Close(); }

    protected override void Dispose(bool disposing) { if (disposing) CloseList(); base.Dispose(disposing); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color under = Ctl.Under(this);
        Ctl.FloorOn(g, this, under, ClientRectangle);     // страховка: фон мальовано й тут
        Ctl.Field(g, ClientRectangle, Enabled, hover || dropped, Focused || dropped, under);
        int aw = Math.Max(SUi.S(16), Height - SUi.S(8));
        Color tc = Enabled ? FormTheme.Fg : Util.Blend(under, FormTheme.Fg, 0.45f);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(SUi.S(11), 0, Width - aw - SUi.S(14), Height), tc,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        Ctl.Chevron(g, Width - aw / 2f - SUi.S(4), Height / 2f, Math.Max(3f, SUi.S(4)), Enabled ? Util.Blend(FormTheme.Input, FormTheme.Fg, 0.7f) : FormTheme.Sub, dropped);
    }
}

// ----------------------------------------------------------------------
//  Повзунок (властивості як у TrackBar); Ticks - позначки під доріжкою (частоти моніторів)
// ----------------------------------------------------------------------
sealed class SSlider : Control, ISelfPaint
{
    int min, max = 100, val;
    bool hover, drag;
    public int SmallChange = 1, LargeChange = 10, TickFrequency;
    public TickStyle TickStyle = TickStyle.None;
    public int[] Ticks = new int[0];
    public event EventHandler ValueChanged;

    public SSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
        Ctl.Settle(this);
        TabStop = true;
        Height = SUi.S(30);
    }

    public int Minimum { get { return min; } set { min = value; if (max < min) max = min; Value = val; Invalidate(); } }
    public int Maximum { get { return max; } set { max = Math.Max(min, value); Value = val; Invalidate(); } }
    public int Value
    {
        get { return val; }
        set
        {
            int v = Math.Max(min, Math.Min(max, value));
            if (v == val) return;
            val = v;
            Invalidate();
            EventHandler h = ValueChanged; if (h != null) h(this, EventArgs.Empty);
        }
    }

    int D { get { return SUi.S(20); } }
    int Pad { get { return D / 2 + 2; } }
    float Frac(int v) { return max > min ? (v - min) / (float)(max - min) : 0f; }
    float XOf(int v) { return Pad + Frac(v) * (Width - Pad * 2); }
    int ValAt(int x) { float f = (x - Pad) / (float)Math.Max(1, Width - Pad * 2); return min + (int)Math.Round(Math.Max(0f, Math.Min(1f, f)) * (max - min)); }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaintBackground(PaintEventArgs e) { Ctl.Back(this, e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && Enabled) { Focus(); drag = true; Capture = true; Value = ValAt(e.X); Invalidate(); }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (drag) Value = ValAt(e.X);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (drag) { drag = false; Capture = false; Invalidate(); }
        base.OnMouseUp(e);
    }

    protected override bool IsInputKey(Keys k)
    {
        Keys c = k & Keys.KeyCode;
        return c == Keys.Left || c == Keys.Right || c == Keys.Up || c == Keys.Down || base.IsInputKey(k);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left: case Keys.Down: Value = val - SmallChange; e.Handled = true; break;
            case Keys.Right: case Keys.Up: Value = val + SmallChange; e.Handled = true; break;
            case Keys.PageDown: Value = val - LargeChange; e.Handled = true; break;
            case Keys.PageUp: Value = val + LargeChange; e.Handled = true; break;
            case Keys.Home: Value = min; e.Handled = true; break;
            case Keys.End: Value = max; e.Handled = true; break;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color bg = Ctl.Under(this);
        Ctl.FloorOn(g, this, bg, ClientRectangle);     // страховка: фон мальовано й тут
        int th = Math.Max(3, SUi.S(4)), cy = Height / 2;
        Rectangle track = new Rectangle(Pad, cy - th / 2, Math.Max(1, Width - Pad * 2), th);
        Color acc = Enabled ? FormTheme.Acc : Util.Blend(bg, FormTheme.Acc, 0.35f);
        Skin.Fill(g, track, Skin.Pill(th), Util.Blend(bg, FormTheme.Fg, 0.2f));
        float x = XOf(val);
        Rectangle done = new Rectangle(track.X, track.Y, Math.Max(th, (int)Math.Round(x - track.X)), th);
        Skin.Fill(g, done, Skin.Pill(th), acc);
        if (Ticks != null)
            using (Pen p = new Pen(Util.Blend(bg, FormTheme.Fg, 0.45f), 1f))
                foreach (int tv in Ticks)
                {
                    if (tv <= min || tv >= max) continue;
                    float tx = XOf(tv);
                    g.DrawLine(p, tx, cy + th + SUi.S(3), tx, cy + th + SUi.S(7));
                }
        // ручка: кільце й крапка акценту (більшає під мишею, меншає під час перетягування)
        int d = D;
        RectangleF kn = new RectangleF(x - d / 2f, cy - d / 2f, d, d);
        Color ring = FormTheme.Dark ? Util.Blend(bg, FormTheme.Fg, 0.28f) : Color.White;
        if (FormTheme.Plate)
            using (LinearGradientBrush lg = new LinearGradientBrush(new RectangleF(kn.X, kn.Y - 1, kn.Width, kn.Height + 2), Util.Blend(acc, Color.White, 0.3f), Util.Blend(acc, Color.Black, 0.3f), 90f))
                g.FillEllipse(lg, kn);
        else using (SolidBrush b = new SolidBrush(ring)) g.FillEllipse(b, kn);
        using (Pen p = new Pen(Util.Blend(bg, FormTheme.Fg, 0.18f), 1f)) g.DrawEllipse(p, kn);
        float k = d * (drag ? 0.36f : (hover ? 0.56f : 0.46f));
        using (SolidBrush b = new SolidBrush(FormTheme.Plate ? Util.Blend(acc, Color.Black, 0.35f) : acc)) g.FillEllipse(b, x - k / 2f, cy - k / 2f, k, k);
        if (FormTheme.Neon && Enabled) using (Pen p = new Pen(Color.FromArgb(90, acc), 3f)) g.DrawEllipse(p, RectangleF.Inflate(kn, 1, 1));
        if (Focused && ShowFocusCues) using (Pen p = new Pen(Color.FromArgb(150, acc), 1.5f)) g.DrawEllipse(p, RectangleF.Inflate(kn, 3, 3));
    }
}

// ----------------------------------------------------------------------
//  Числове поле зі стрілками (властивості як у NumericUpDown)
// ----------------------------------------------------------------------
sealed class SNum : Control, ISelfPaint
{
    readonly TextBox tb = new TextBox();
    decimal min, max = 100, val, inc = 1;
    int dec;
    bool hover, inner;
    int hotHalf = -1;
    readonly Timer rep = new Timer();
    int repDir;
    public event EventHandler ValueChanged;

    public SNum()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Ctl.Settle(this);
        tb.BorderStyle = BorderStyle.None;
        tb.TextAlign = HorizontalAlignment.Center;
        tb.BackColor = FormTheme.Input;
        tb.ForeColor = FormTheme.Fg;
        Controls.Add(tb);
        Height = SUi.S(32);
        tb.Text = Format(val);
        tb.TextChanged += delegate
        {
            if (!inner)
            {
                decimal v;
                if (decimal.TryParse(tb.Text, out v) && v >= min && v <= max && v != val) { val = v; Fire(); }
            }
            OnTextChanged(EventArgs.Empty);
        };
        tb.KeyDown += delegate (object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Up) { Step(1); e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Down) { Step(-1); e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Enter) { Commit(); e.Handled = true; e.SuppressKeyPress = true; }
        };
        tb.Leave += delegate { Commit(); };
        tb.GotFocus += delegate { Invalidate(); };
        tb.LostFocus += delegate { Invalidate(); };
        tb.MouseEnter += delegate { hover = true; Invalidate(); };
        tb.MouseLeave += delegate { hover = false; Invalidate(); };
        tb.MouseWheel += delegate (object s, MouseEventArgs e)
        {
            HandledMouseEventArgs he = e as HandledMouseEventArgs;
            if (!tb.Focused) return;
            Step(e.Delta > 0 ? 1 : -1);
            if (he != null) he.Handled = true;
        };
        rep.Tick += delegate { rep.Interval = 60; Step(repDir); };
    }

    public decimal Minimum { get { return min; } set { min = value; if (max < min) max = min; Value = val; } }
    public decimal Maximum { get { return max; } set { max = Math.Max(min, value); Value = val; } }
    public decimal Increment { get { return inc; } set { inc = value; } }
    public int DecimalPlaces { get { return dec; } set { dec = Math.Max(0, value); SetText(); } }
    public HorizontalAlignment TextAlign { get { return tb.TextAlign; } set { tb.TextAlign = value; } }
    public override string Text { get { return tb.Text; } set { tb.Text = value; } }

    public decimal Value
    {
        get { return val; }
        set
        {
            decimal v = Math.Max(min, Math.Min(max, value));
            bool ch = v != val;
            val = v;
            SetText();
            if (ch) Fire();
        }
    }

    string Format(decimal v) { return v.ToString("F" + dec); }
    void SetText() { inner = true; string t = Format(val); if (tb.Text != t) tb.Text = t; inner = false; }
    void Fire() { Invalidate(); EventHandler h = ValueChanged; if (h != null) h(this, EventArgs.Empty); }
    void Step(int dir) { Value = val + inc * dir; tb.SelectionStart = tb.Text.Length; }

    void Commit()
    {
        decimal v;
        if (decimal.TryParse(tb.Text, out v)) Value = v; else SetText();
        SetText();
    }

    int AW { get { return SUi.S(24); } }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int h = tb.PreferredHeight;
        tb.SetBounds(SUi.S(8), (Height - h) / 2, Math.Max(10, Width - SUi.S(8) - AW - SUi.S(4)), h);
    }

    protected override void OnEnabledChanged(EventArgs e) { tb.Enabled = Enabled; Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; hotHalf = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaintBackground(PaintEventArgs e) { Ctl.Back(this, e); }

    Rectangle Arrows { get { return new Rectangle(Width - AW - SUi.S(3), SUi.S(3), AW, Height - SUi.S(6)); } }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Rectangle a = Arrows;
        int h = a.Contains(e.Location) ? (e.Y < a.Y + a.Height / 2 ? 0 : 1) : -1;
        if (h != hotHalf) { hotHalf = h; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        Rectangle a = Arrows;
        if (!a.Contains(e.Location)) { tb.Focus(); return; }
        tb.Focus();
        repDir = e.Y < a.Y + a.Height / 2 ? 1 : -1;
        Step(repDir);
        rep.Interval = 400;
        rep.Start();
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); rep.Stop(); }
    protected override void Dispose(bool disposing) { if (disposing) { rep.Stop(); rep.Dispose(); } base.Dispose(disposing); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color under = Ctl.Under(this);
        Ctl.FloorOn(g, this, under, ClientRectangle);     // страховка: фон мальовано й тут
        Ctl.Field(g, ClientRectangle, Enabled, hover, tb.Focused, under);
        if (tb.BackColor != FormTheme.Input && Enabled) tb.BackColor = FormTheme.Input;
        Rectangle a = Arrows;
        Color ac = Enabled ? Util.Blend(FormTheme.Input, FormTheme.Fg, 0.65f) : FormTheme.Sub;
        for (int i = 0; i < 2; i++)
        {
            Rectangle hr = new Rectangle(a.X, a.Y + i * a.Height / 2, a.Width, a.Height / 2);
            if (hotHalf == i && Enabled) Skin.Fill(g, Rectangle.Inflate(hr, -1, -1), SUi.S(3), Util.Blend(FormTheme.Input, FormTheme.Fg, 0.1f));
            Ctl.Chevron(g, hr.X + hr.Width / 2f, hr.Y + hr.Height / 2f, Math.Max(2.5f, SUi.S(3.5f)), ac, i == 0);
        }
    }
}
