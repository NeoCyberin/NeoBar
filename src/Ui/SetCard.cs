using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// Картки налаштувань і сторінка, що прокручується

// ----------------------------------------------------------------------
//  Рядок картки: назва й пояснення ліворуч, елементи праворуч (або під назвою - Stack)
// ----------------------------------------------------------------------
sealed class SRow
{
    public string Title = "", Desc = "";
    public Func<string> TitleFn, DescFn;
    public Label Note;                      // змінний текст під поясненням (стан, підказка, помилка)
    public readonly List<Control> Right = new List<Control>();
    public Control Full;                    // на всю ширину картки під текстом (список, карта)
    public int FullH;
    public bool Stack;                      // елементи - під назвою, на всю ширину (перший розтягується)
    public int Flex;                        // у Stack: номер елемента, що розтягується (-1 - жоден)
    public bool Indent;                     // підпункт попереднього рядка
    public Func<bool> When;                 // коли рядок видно
    public CheckBox Toggle;                 // клік по рядку перемикає цей перемикач
    internal Rectangle Box, TextR;
    internal int TitleH, DescH;
    public bool Shown { get { try { return When == null || When(); } catch { return true; } } }
    public string TitleNow { get { return TitleFn != null ? (TitleFn() ?? "") : Title; } }
    public string DescNow { get { return DescFn != null ? (DescFn() ?? "") : Desc; } }
}

// ----------------------------------------------------------------------
//  Картка: заокруглений блок із рядками. Поля тексту всередині малюються як заокруглені «пігулки»
//  (сам TextBox - без рамки). Arrange - власна розкладка замість рядків.
// ----------------------------------------------------------------------
sealed class SCard : Control
{
    public readonly List<SRow> Rows = new List<SRow>();
    public Action<Rectangle> Arrange;
    public Func<bool> When;                 // коли картку (разом із заголовком) видно
    public Label Head;
    public event EventHandler NeedLayout;
    readonly List<int> seps = new List<int>();
    readonly Dictionary<Control, int> want = new Dictionary<Control, int>();
    readonly Dictionary<Control, Rectangle> fields = new Dictionary<Control, Rectangle>();
    bool layoutQueued;

    public SCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        ForeColor = FormTheme.Fg;
        Font = SUi.Base;
    }

    public override Color BackColor { get { return FormTheme.Card; } set { base.BackColor = value; } }

    public static int Pad { get { return SUi.S(18); } }
    public static int FieldH { get { return SUi.S(32); } }

    // бажана ширина елемента в рядку (для поля тексту - разом із «пігулкою»)
    public void Want(Control c, int w) { want[c] = w; }
    int OuterW(Control c) { int w; return want.TryGetValue(c, out w) ? w : c.Width; }
    static int OuterH(Control c) { return c is TextBoxBase ? FieldH : c.Height; }

    public SRow Row(string title, string desc, params Control[] right)
    {
        SRow r = new SRow();
        r.Title = title ?? ""; r.Desc = desc ?? "";
        int checks = 0;
        foreach (Control c in right)
        {
            if (c == null) continue;
            r.Right.Add(c);
            Attach(c);
            if (c is CheckBox) { checks++; r.Toggle = (CheckBox)c; }
        }
        if (checks != 1) r.Toggle = null;
        Rows.Add(r);
        return r;
    }

    public SRow Stacked(string title, string desc, params Control[] items)
    {
        SRow r = Row(title, desc, items);
        r.Stack = true;
        r.Toggle = null;
        return r;
    }

    public SRow Full(Control c, int h)
    {
        SRow r = new SRow();
        r.Full = c; r.FullH = h;
        Attach(c);
        Rows.Add(r);
        return r;
    }

    public SRow NoteRow(Label l)
    {
        SRow r = new SRow();
        r.Note = l;
        Attach(l);
        Rows.Add(r);
        return r;
    }

    public SRow WithNote(SRow r, Label l)
    {
        r.Note = l;
        Attach(l);
        return r;
    }

    void Attach(Control c)
    {
        if (c.Parent != this) Controls.Add(c);
        if (c is Label)
        {
            Label l = (Label)c;
            l.AutoSize = false;
            l.BackColor = Color.Transparent;
            if (l.Font == null || l.Font == Font) l.Font = SUi.Small;
            l.TextChanged += delegate { Queue(); };
        }
        c.GotFocus += delegate { InvalidateField(c); };
        c.LostFocus += delegate { InvalidateField(c); };
        c.EnabledChanged += delegate { Invalidate(); };
        c.MouseEnter += delegate { InvalidateField(c); };
        c.MouseLeave += delegate { InvalidateField(c); };
    }

    void InvalidateField(Control c)
    {
        Rectangle r;
        if (fields.TryGetValue(c, out r)) Invalidate(Rectangle.Inflate(r, 3, 3));
    }

    // текст підказки змінився - розкладку перерахувати один раз після поточної події
    public void Queue()
    {
        if (layoutQueued) return;
        if (!IsHandleCreated) { Fire(); return; }
        layoutQueued = true;
        try { BeginInvoke(new Action(delegate { layoutQueued = false; Fire(); })); } catch { layoutQueued = false; }
    }

    void Fire() { EventHandler h = NeedLayout; if (h != null) h(this, EventArgs.Empty); else { LayoutRows(Width); Invalidate(); } }

    void Place(Control c, int x, int y, int w)
    {
        if (c is TextBoxBase)
        {
            int ih = c.Height;
            c.SetBounds(x + SUi.S(11), y + (FieldH - ih) / 2, Math.Max(10, w - SUi.S(22)), ih);
            fields[c] = new Rectangle(x, y, w, FieldH);
        }
        else c.SetBounds(x, y, w, c.Height);
    }

    // розкладка рядків для ширини w; повертає висоту картки
    public int LayoutRows(int w)
    {
        if (Arrange != null) return Height;
        seps.Clear();
        fields.Clear();
        int y = SUi.S(4), gap = SUi.S(8), vpad = SUi.S(13);
        bool first = true;
        foreach (SRow r in Rows)
        {
            bool show = r.Shown;
            // рядок лише з порожньою підказкою не займає місця
            if (show && r.Note != null && r.Right.Count == 0 && r.Full == null && r.Note.Text.Length == 0 && r.TitleNow.Length == 0 && r.DescNow.Length == 0) show = false;
            foreach (Control c in r.Right) c.Visible = show;
            if (r.Full != null) r.Full.Visible = show;
            if (r.Note != null) r.Note.Visible = show && r.Note.Text.Length > 0;
            if (!show) continue;
            if (!first) seps.Add(y);
            first = false;
            int x0 = Pad + (r.Indent ? SUi.S(26) : 0);
            int inner = w - x0 - Pad;
            int top = y;
            string title = r.TitleNow, desc = r.DescNow;
            bool hasText = title.Length > 0 || desc.Length > 0;
            int rw = 0, ch = 0;
            foreach (Control c in r.Right) { rw += OuterW(c) + gap; ch = Math.Max(ch, OuterH(c)); }
            if (rw > 0) rw -= gap;
            bool stack = r.Stack || (rw > 0 && hasText && inner - rw - SUi.S(20) < SUi.S(150));
            int textW = stack || rw == 0 ? inner : inner - rw - SUi.S(24);
            int th = SUi.TextH(title, SUi.Base, textW);
            int dh = SUi.TextH(desc, SUi.Small, textW);
            string nt = r.Note != null ? r.Note.Text : "";
            int nh = SUi.TextH(nt, r.Note != null ? r.Note.Font : SUi.Small, textW);
            int textH = th + (dh > 0 ? (th > 0 ? SUi.S(2) : 0) + dh : 0) + (nh > 0 ? (th + dh > 0 ? SUi.S(5) : 0) + nh : 0);
            r.TitleH = th; r.DescH = dh;
            if (!stack)
            {
                int h = Math.Max(Math.Max(textH, ch) + vpad * 2, hasText || rw > 0 ? SUi.S(50) : 0);
                if (!hasText && rw == 0 && nh > 0) h = nh + SUi.S(16);
                int ty = y + (h - textH) / 2;
                r.TextR = new Rectangle(x0, ty, textW, textH);
                int cx = w - Pad - rw;
                foreach (Control c in r.Right)
                {
                    int cw = OuterW(c);
                    Place(c, cx, y + (h - OuterH(c)) / 2, cw);
                    cx += cw + gap;
                }
                if (r.Note != null && nh > 0) r.Note.SetBounds(x0, ty + th + dh + (th + dh > 0 ? SUi.S(5) : 0), textW, nh);
                y += h;
            }
            else
            {
                int ty = y + vpad;
                r.TextR = new Rectangle(x0, ty, textW, th + (dh > 0 ? SUi.S(2) + dh : 0));
                int cy = ty + r.TextR.Height + (hasText ? SUi.S(9) : 0);
                int flex = r.Stack ? r.Flex : -1;      // рядок, що став стовпчиком лише через тісноту, нічого не розтягує
                int fixedW = 0;
                for (int i = 0; i < r.Right.Count; i++) if (i != flex) fixedW += OuterW(r.Right[i]);
                int flexW = Math.Max(SUi.S(80), inner - fixedW - gap * Math.Max(0, r.Right.Count - 1));
                int cx = x0;
                for (int i = 0; i < r.Right.Count; i++)
                {
                    Control c = r.Right[i];
                    int cw = i == flex ? flexW : OuterW(c);
                    Place(c, cx, cy + (ch - OuterH(c)) / 2, cw);
                    cx += cw + gap;
                }
                int ny = cy + (r.Right.Count > 0 ? ch + SUi.S(6) : 0);
                if (r.Note != null && nh > 0) { r.Note.SetBounds(x0, ny, inner, nh); ny += nh + SUi.S(2); }
                y = ny + vpad - (r.Right.Count > 0 ? SUi.S(6) : 0);
            }
            if (r.Full != null)
            {
                int fh = r.FullH;
                int fy = hasText || r.Right.Count > 0 ? y - SUi.S(4) : y + SUi.S(12);
                r.Full.SetBounds(x0, fy, inner, fh);
                y = fy + fh + SUi.S(14);
            }
            r.Box = new Rectangle(0, top, w, y - top);
        }
        y += SUi.S(4);
        if (Width != w || Height != y) Size = new Size(w, y);
        Invalidate();
        return y;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        foreach (SRow r in Rows)
            if (r.Toggle != null && r.Box.Contains(e.Location) && r.Toggle.Visible && r.Toggle.Enabled && !r.Toggle.Bounds.Contains(e.Location))
            {
                r.Toggle.Checked = !r.Toggle.Checked;
                r.Toggle.Focus();
                return;
            }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool hand = false;
        foreach (SRow r in Rows) if (r.Toggle != null && r.Toggle.Visible && r.Toggle.Enabled && r.Box.Contains(e.Location)) hand = true;
        Cursor c = hand ? Cursors.Hand : Cursors.Default;
        if (Cursor != c) Cursor = c;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        PaintChrome(g);
        PaintRows(g);
    }

    // тло під дочірнім елементом (area - у координатах картки): колір картки й роздільники рядків, що проходять крізь area.
    // Без контуру, заклепок і будь-яких текстів
    public void PaintBehind(Graphics g, Rectangle area)
    {
        Ctl.Floor(g, FormTheme.Card, area);
        using (Pen lp = new Pen(Util.Blend(FormTheme.Card, FormTheme.Fg, FormTheme.Dark ? 0.07f : 0.09f), 1f))
            foreach (int y in seps)
            {
                if (y < area.Y - 1 || y > area.Bottom) continue;
                if (FormTheme.Sketchy) Sk.Line(g, lp, Pad, y, Width - Pad, y, new Random(y), 0.8f);
                else g.DrawLine(lp, Pad, y, Width - Pad, y);
            }
    }

    // лише тло картки (заливка, контур, заклепки, роздільники) - без текстів
    public void PaintChrome(Graphics g)
    {
        Color outer = SUi.BgOf(this);
        Ctl.FloorOn(g, this, outer, ClientRectangle);          // на святковому тлі - градієнт вікна, а не пласка заливка (інакше кути картки квадратні)
        Rectangle box = new Rectangle(0, 0, Width - 1, Height - 1);
        int rad = Math.Min(SUi.R(4), Math.Min(box.Width, box.Height) / 2);
        // «Артистична»: картка - рівний аркуш із нерівним олівцевим контуром (папір із текстурою лише в навігації,
        // інакше елементи з однотонним тлом виглядали б латками)
        // тло картки - рівне: елементи всередині мають однотонне тло, і на градієнті було б видно їхні прямокутники
        Skin.Fill(g, box, rad, FormTheme.Card);
        Color edge = FormTheme.Neon ? Color.FromArgb(150, FormTheme.Acc) : Util.Blend(FormTheme.Card, FormTheme.Fg, FormTheme.Dark ? 0.09f : 0.12f);
        Skin.Edge(g, box, rad, edge, 1f);
        if (FormTheme.Plate)
            using (SolidBrush rv = new SolidBrush(Util.Blend(FormTheme.Card, FormTheme.Acc, 0.7f)))
            {
                int d = SUi.S(5), m = SUi.S(7);
                g.FillEllipse(rv, m, m, d, d); g.FillEllipse(rv, Width - m - d - 1, m, d, d);
                g.FillEllipse(rv, m, Height - m - d - 1, d, d); g.FillEllipse(rv, Width - m - d - 1, Height - m - d - 1, d, d);
            }

        using (Pen lp = new Pen(Util.Blend(FormTheme.Card, FormTheme.Fg, FormTheme.Dark ? 0.07f : 0.09f), 1f))
            foreach (int y in seps)
            {
                if (FormTheme.Sketchy) Sk.Line(g, lp, Pad, y, Width - Pad, y, new Random(y), 0.8f);
                else g.DrawLine(lp, Pad, y, Width - Pad, y);
            }
    }

    void PaintRows(Graphics g)
    {
        foreach (SRow r in Rows)
        {
            if (r.Box.Height <= 0 || !r.Shown) continue;
            bool dim = r.Right.Count > 0 && AllOff(r);
            Color tc = dim ? Util.Blend(FormTheme.Card, FormTheme.Fg, 0.45f) : FormTheme.Fg;
            Color dc = dim ? Util.Blend(FormTheme.Card, FormTheme.Sub, 0.6f) : FormTheme.Sub;
            string title = r.TitleNow, desc = r.DescNow;
            if (title.Length > 0) SUi.Text(g, title, SUi.Base, new Rectangle(r.TextR.X, r.TextR.Y, r.TextR.Width, r.TitleH + SUi.S(2)), tc);
            if (desc.Length > 0) SUi.Text(g, desc, SUi.Small, new Rectangle(r.TextR.X, r.TextR.Y + r.TitleH + (r.TitleH > 0 ? SUi.S(2) : 0), r.TextR.Width, r.DescH + SUi.S(2)), dc);
        }

        // поля тексту: заокруглена «пігулка» за темою
        foreach (KeyValuePair<Control, Rectangle> kv in fields)
        {
            Control c = kv.Key;
            if (!c.Visible) continue;
            Rectangle fr = new Rectangle(kv.Value.X, kv.Value.Y, kv.Value.Width - 1, kv.Value.Height - 1);
            int fr2 = Ctl.CR(fr.Height);
            Color fill = c.BackColor;        // як у самого TextBox (і вимкненого теж) - без прямокутника навколо тексту
            Skin.Fill(g, fr, fr2, fill);
            bool focus = c.Focused;
            bool hot = c.ClientRectangle.Contains(c.PointToClient(Cursor.Position));
            Color ec = focus ? FormTheme.Acc : (hot ? Util.Blend(FormTheme.Line, FormTheme.Fg, 0.25f) : Util.Blend(FormTheme.Card, FormTheme.Line, 0.85f));
            Skin.Edge(g, fr, fr2, ec, 1f);
            if (focus && !FormTheme.Sketchy)
                using (Pen p = new Pen(FormTheme.Acc, Math.Max(2f, SUi.S(2))))
                    g.DrawLine(p, fr.X + fr2, fr.Bottom, fr.Right - fr2, fr.Bottom);
        }
    }

    static bool AllOff(SRow r)
    {
        bool any = false;
        foreach (Control c in r.Right) { if (c is Label) continue; any = true; if (c.Enabled) return false; }
        return any;
    }
}

// ----------------------------------------------------------------------
//  Сторінка, що прокручується: заголовки розділів і картки одна під одною; власна тонка смуга
// ----------------------------------------------------------------------
sealed class SPage : Control, IGradHost
{
    sealed class Inner : Panel, IGradHost
    {
        public SPage Page;
        public int End;                 // де закінчується вміст (нижче - вільне місце)

        public Inner() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }
        public override Color BackColor { get { return FormTheme.Bg; } set { base.BackColor = value; } }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (!Festive.Backdrop(e.Graphics, this, e.ClipRectangle)) base.OnPaintBackground(e);
        }

        // святкові теми: прикраси у вільних місцях - праворуч від заголовків розділів і під вмістом
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Theme t = FormTheme.T;
            if (Page == null || t == null || !Festive.HasDecor(t.Id) || FormTheme.Sketchy) return;
            try
            {
                Graphics g = e.Graphics;
                float sc = SUi.S(100) / 100f;
                int n = 0;
                foreach (Control c in Controls)
                {
                    Label l = c as Label;
                    if (l == null || !l.Visible || l.Height > SUi.S(40)) continue;
                    float sz = Math.Min(l.Height, SUi.S(24)) * 0.9f;
                    Festive.Mark(g, t, n * 3 + 1, new RectangleF(Width - ThinBarGap - sz * 1.5f - (n % 3) * sz * 1.8f, l.Top + (l.Height - sz) / 2f, sz, sz));
                    n++;
                }
                int free = Height - End;
                if (free > SUi.S(90)) Festive.Decor(g, t, new Rectangle(0, End + SUi.S(6), Width, free - SUi.S(6)), sc, true);
            }
            catch { }
        }

        const int ThinBarGap = 6;
    }

    readonly Inner inner = new Inner();
    readonly List<Control> items = new List<Control>();
    readonly Dictionary<Control, int> fixedH = new Dictionary<Control, int>();
    readonly ThinBar bar = new ThinBar();
    readonly Timer anim = new Timer();
    int pos, target;
    bool relaying;
    public int MaxW = 0;            // 0 - на всю ширину
    public int Bottom2 = 0;         // запас унизу

    public SPage()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        inner.BackColor = FormTheme.Bg;
        inner.Page = this;
        Controls.Add(inner);
        anim.Interval = 15;
        anim.Tick += delegate
        {
            int d = target - pos;
            if (Math.Abs(d) <= 1) { pos = target; anim.Stop(); }
            else pos += (int)Math.Round(d * 0.32f) + Math.Sign(d);
            if (Math.Sign(target - pos) != Math.Sign(d)) pos = target;
            Apply();
        };
        inner.ControlAdded += delegate (object s, ControlEventArgs e) { Festive.Clear(e.Control); Hook(e.Control); };
    }

    void Hook(Control c)
    {
        // коліщатко над будь-яким елементом сторінки, що його не використовує, прокручує сторінку
        c.MouseWheel += delegate (object s, MouseEventArgs e)
        {
            HandledMouseEventArgs he = e as HandledMouseEventArgs;
            if (he != null && he.Handled) return;
            if (s is ComboBox || s is NumericUpDown || s is TrackBar || s is SList || s is MetricList || s is TextBoxBase) return;
            if (s is SNum && ((SNum)s).ContainsFocus) return;
            Wheel(e.Delta);
            if (he != null) he.Handled = true;
        };
        // фокус клавіатурою на елементі за межами видимої частини - прокрутити до нього
        // (лише для самих елементів: картка отримує Enter разом із ними, і сторінка стрибала б до її верху)
        if (!(c is SCard) && !(c is Panel) && !(c is SPage)) c.Enter += delegate { Reveal(c); };
        foreach (Control k in c.Controls) Hook(k);
        c.ControlAdded += delegate (object s, ControlEventArgs e) { Hook(e.Control); };
    }

    public Label Header(string text)
    {
        Label l = new Label();
        l.AutoSize = false;
        l.Text = text;
        l.Font = SUi.Head;
        l.ForeColor = FormTheme.Fg;
        l.BackColor = Color.Transparent;
        l.TextAlign = ContentAlignment.TopLeft;
        Add(l, SUi.S(24));
        return l;
    }

    public SCard Card(string header)
    {
        Label hd = !string.IsNullOrEmpty(header) ? Header(header) : null;
        SCard c = new SCard();
        c.Head = hd;
        c.NeedLayout += delegate { Relayout(); };
        Add(c, -1);
        return c;
    }

    public void Add(Control c, int h)
    {
        items.Add(c);
        if (h >= 0) fixedH[c] = h;
        inner.Controls.Add(c);
    }

    public int ContentW { get { int w = ClientSize.Width - ThinBar.W - SUi.S(6); return MaxW > 0 ? Math.Min(MaxW, w) : w; } }

    public void Relayout()
    {
        if (relaying) return;
        relaying = true;
        try
        {
            int w = Math.Max(SUi.S(200), ContentW);
            int y = 0;
            Control prev = null;
            // картки з умовою: прихована картка ховає й свій заголовок
            HashSet<Control> off = new HashSet<Control>();
            foreach (Control c in items)
            {
                SCard k = c as SCard;
                if (k == null || k.When == null) continue;
                bool show = true;
                try { show = k.When(); } catch { }
                k.Visible = show;
                if (k.Head != null) k.Head.Visible = show;
                if (!show) { off.Add(k); if (k.Head != null) off.Add(k.Head); }
            }
            foreach (Control c in items)
            {
                if (off.Contains(c)) continue;
                if (!c.Visible && c.Parent == inner && inner.Visible && Visible) { if (!(c is SCard)) continue; }
                int h;
                if (c is SCard && ((SCard)c).Arrange == null) h = ((SCard)c).LayoutRows(w);
                else if (!fixedH.TryGetValue(c, out h)) h = c.Height;
                if (c is Label && prev != null) y += SUi.S(14);
                c.SetBounds(c is Label ? SUi.S(2) : 0, y, w, h);
                if (c is SCard && ((SCard)c).Arrange != null) ((SCard)c).Arrange(new Rectangle(0, 0, w, h));
                y += h + (c is Label ? SUi.S(6) : SUi.S(10));
                prev = c;
            }
            inner.End = y;
            y += Bottom2;
            bar.View = ClientSize.Height;
            bar.Content = y;
            target = bar.Clamp(target);
            pos = bar.Clamp(pos);
            inner.SetBounds(0, -pos, ClientSize.Width - ThinBar.W, Math.Max(y, ClientSize.Height));
            bar.Pos = pos;
            Invalidate();
            inner.Invalidate();
        }
        finally { relaying = false; }
    }

    void Apply()
    {
        bar.Pos = pos;
        inner.Top = -pos;
        if (Festive.Has(FormTheme.T) || FormTheme.Sketchy) inner.Invalidate();     // тло прив'язане до вікна, а не до вмісту
        Invalidate(new Rectangle(ClientSize.Width - ThinBar.W, 0, ThinBar.W, ClientSize.Height));
    }

    public void ScrollTo(int p, bool smooth)
    {
        target = bar.Clamp(p);
        if (!smooth) { pos = target; anim.Stop(); Apply(); }
        else if (!anim.Enabled) anim.Start();
    }

    public void Wheel(int delta)
    {
        if (!bar.Need) return;
        ScrollTo(target - delta * SUi.S(96) / 120, true);
    }

    void Reveal(Control c)
    {
        if (!bar.Need) return;
        try
        {
            Point p = inner.PointToClient(c.PointToScreen(Point.Empty));
            int top = p.Y, bottom = p.Y + c.Height;
            if (top < pos + SUi.S(8)) ScrollTo(top - SUi.S(24), true);
            else if (bottom > pos + ClientSize.Height - SUi.S(8)) ScrollTo(bottom - ClientSize.Height + SUi.S(24), true);
        }
        catch { }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) Relayout(); }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Wheel(e.Delta); HandledMouseEventArgs he = e as HandledMouseEventArgs; if (he != null) he.Handled = true; }
    protected override void Dispose(bool disposing) { if (disposing) { anim.Stop(); anim.Dispose(); } base.Dispose(disposing); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        bar.MouseDown(this, e, ClientRectangle, delegate (int v) { ScrollTo(v, true); });
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bar.MouseMove(this, e, ClientRectangle, delegate (int v) { target = pos = v; Apply(); });
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); bar.MouseUp(this); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (bar.Hot && !bar.Dragging) { bar.Hot = false; Invalidate(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!Festive.Backdrop(e.Graphics, this, ClientRectangle)) using (SolidBrush b = new SolidBrush(FormTheme.Bg)) e.Graphics.FillRectangle(b, ClientRectangle);
        bar.Paint(e.Graphics, ClientRectangle);
    }
}

// ----------------------------------------------------------------------
//  Простий контейнер сторінки: власна розкладка дочірніх елементів (Arrange)
// ----------------------------------------------------------------------
sealed class SHost : Control, IGradHost
{
    public Action<Rectangle> Arrange;
    public SHost()
    {
        ControlAdded += delegate (object s, ControlEventArgs e) { Festive.Clear(e.Control); };
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    public override Color BackColor { get { return FormTheme.Bg; } set { base.BackColor = value; } }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (!Festive.Backdrop(e.Graphics, this, e.ClipRectangle)) base.OnPaintBackground(e);
    }
    public void Relayout() { if (Arrange != null && Width > 0 && Height > 0) try { Arrange(ClientRectangle); } catch (Exception ex) { Util.ErrorLog(ex); } }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) Relayout(); }
}
