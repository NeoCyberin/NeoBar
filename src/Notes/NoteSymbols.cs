using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// Вибір емодзі й символів для нотаток

static class EmojiSets
{
    public const string Font = "Segoe UI Symbol";   // однотонний шрифт: RichEdit малює його надійно (кольорові емодзі він показує квадратиком)

    // чи потрібен символьний шрифт (літери малюються шрифтом теми)
    public static bool NeedsSym(char c)
    {
        if (char.IsSurrogate(c)) return true;
        switch (CharUnicodeInfo.GetUnicodeCategory(c))
        {
            case UnicodeCategory.UppercaseLetter:
            case UnicodeCategory.LowercaseLetter:
            case UnicodeCategory.TitlecaseLetter:
            case UnicodeCategory.ModifierLetter:
            case UnicodeCategory.OtherLetter:
            case UnicodeCategory.DecimalDigitNumber:
            case UnicodeCategory.SpaceSeparator:
                return false;
        }
        return true;
    }

    // колір звичайного (не емодзі) символу: колір тексту теми
    public static Color ColorFor(NoteTheme t)
    {
        return t.Fg;       // звичайні символи - кольором тексту; емодзі малюються власними кольорами
    }

    // ---- останні вставлені ----
    static readonly List<string> recent = new List<string>();
    public static List<string> Recent { get { return recent; } }
    public static void AddRecent(string s)
    {
        recent.Remove(s);
        recent.Insert(0, s);
        if (recent.Count > 42) recent.RemoveAt(recent.Count - 1);
    }

    // ---- категорії: емодзі та символи Unicode (діапазони кодів у hex) ----
    static List<SymCat> cats;
    public static List<SymCat> Cats
    {
        get
        {
            if (cats != null) return cats;
            cats = new List<SymCat>();
            // емодзі
            cats.Add(new SymCat("😀", L.T("Смайли"), "1F600-1F64F,1F910-1F92F,1F970-1F97A"));
            cats.Add(new SymCat("❤", L.T("Серця й емоції"), "2763-2764,2665,2661,1F493-1F49F,1F5A4,1F90D-1F90E,1F9E1,1F48B,1F4A2-1F4AD,1F4AF"));
            cats.Add(new SymCat("👍", L.T("Жести"), "261D,270A-270D,1F446-1F450,1F485,1F4AA,1F590,1F595-1F596,1F64C,1F64F,1F918-1F91F,1F932"));
            cats.Add(new SymCat("🧑", L.T("Люди"), "1F466-1F487,1F9D1-1F9DD"));
            cats.Add(new SymCat("🐱", L.T("Тварини"), "1F400-1F43E,1F980-1F9AE,1F54A,1F577-1F578"));
            cats.Add(new SymCat("🌿", L.T("Природа й погода"), "1F300-1F32C,1F331-1F335,1F337-1F344,1F490,2600-2604,2614,2744,26A1"));
            cats.Add(new SymCat("🍕", L.T("Їжа й напої"), "1F32D-1F330,1F336,1F345-1F37F,1F950-1F96B"));
            cats.Add(new SymCat("⚽", L.T("Активності"), "1F380-1F393,1F3A0-1F3CA,1F3CF-1F3D3,26BD-26BE,1F94A-1F94F"));
            cats.Add(new SymCat("🚀", L.T("Подорожі"), "1F3D4-1F3DF,1F3E0-1F3F0,1F5FA-1F5FF,1F680-1F6C5,1F6E0-1F6E5"));
            cats.Add(new SymCat("💡", L.T("Предмети"), "1F4A1,1F4B0-1F4FD,1F4FF,1F50A-1F52E,1F56F,1F5A5,1F5A8,1F5C2-1F5C4,1F5D1-1F5D3,1F5DC-1F5DE,1F5E1"));
            cats.Add(new SymCat("✅", L.T("Знаки"), "2705,2714,2716,274C,274E,2753-2755,2757,2795-2797,27B0,27BF,2B50,2B55,1F500-1F509,1F52F-1F53D"));
            cats.Add(new SymCat("🦄", L.T("Ще піктограми"), "1F900-1F9FF"));
            cats.Add(new SymCat("🪐", L.T("Нові емодзі"), "1FA70-1FA74,1FA78-1FA7A,1FA80-1FA86,1FA90-1FAA8,1FAB0-1FAB6,1FAC0-1FAC2,1FAD0-1FAD6"));
            cats.Add(new SymCat("🃏", L.T("Карти, доміно, маджонг"), "1F000-1F02B,1F030-1F093,1F0A0-1F0F5"));
            // символи Unicode
            cats.Add(new SymCat("→", L.T("Стрілки"), "2190-21FF,2B00-2B11,2B60-2B73,27F0-27FF,2900-297F"));
            cats.Add(new SymCat("∑", L.T("Математика"), "00B1,00D7,00F7,2200-22FF,2070-209F,00BC-00BE,2150-215F,2A00-2A3F"));
            cats.Add(new SymCat("◆", L.T("Фігури"), "25A0-25FF,2B1B-2B1C,1F7E0-1F7EB,2B12-2B2F"));
            cats.Add(new SymCat("☀", L.T("Різні символи"), "2600-26FF"));
            cats.Add(new SymCat("✂", L.T("Дингбати"), "2700-27BF"));
            cats.Add(new SymCat("⌘", L.T("Технічні"), "2300-23FF"));
            cats.Add(new SymCat("┌", L.T("Рамки й блоки"), "2500-259F"));
            cats.Add(new SymCat("€", L.T("Валюти"), "00A2-00A5,20A0-20CF"));
            cats.Add(new SymCat(L.Q("«"), L.T("Пунктуація"), "00A1,00A7,00A9,00AB,00AE,00B0,00B6,00BB,00BF,2010-2027,2030-205E"));
            cats.Add(new SymCat("①", L.T("Цифри в колах, римські"), "2460-24FF,2160-2188,2776-2793"));
            cats.Add(new SymCat("⠿", L.T("Шрифт Брайля"), "2800-28FF"));
            // літери
            cats.Add(new SymCat("Æ", L.T("Латиниця розширена"), "00C0-024F"));
            cats.Add(new SymCat("Ω", L.T("Грецька"), "0370-03FF"));
            cats.Add(new SymCat(L.T("Ж"), L.T("Кирилиця"), "0400-052F"));
            return cats;
        }
    }
}

// категорія символів: діапазони кодів "1F600-1F64F,2764"
sealed class SymCat
{
    public readonly string Icon, Name;
    readonly string spec;
    List<string> items;

    public SymCat(string icon, string name, string spec) { Icon = icon; Name = name; this.spec = spec; }
    public SymCat(string icon, string name, List<string> preset) { Icon = icon; Name = name; items = preset; }

    public List<string> Items { get { if (items == null) items = Build(); return items; } }

    List<string> Build()
    {
        List<string> l = new List<string>();
        foreach (string part in spec.Split(','))
        {
            int lo, hi;
            string[] ab = part.Split('-');
            if (!int.TryParse(ab[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out lo)) continue;
            hi = lo;
            if (ab.Length > 1 && !int.TryParse(ab[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hi)) hi = lo;
            for (int cp = lo; cp <= hi; cp++)
            {
                if (cp < 0x10000)
                {
                    // у BMP відкидаємо невизначені, керівні, комбінівні, пробіли - у них нема що показувати
                    switch (CharUnicodeInfo.GetUnicodeCategory((char)cp))
                    {
                        case UnicodeCategory.OtherNotAssigned:
                        case UnicodeCategory.Control:
                        case UnicodeCategory.Surrogate:
                        case UnicodeCategory.PrivateUse:
                        case UnicodeCategory.NonSpacingMark:
                        case UnicodeCategory.EnclosingMark:
                        case UnicodeCategory.SpacingCombiningMark:
                        case UnicodeCategory.Format:
                        case UnicodeCategory.SpaceSeparator:
                        case UnicodeCategory.LineSeparator:
                        case UnicodeCategory.ParagraphSeparator:
                            continue;
                    }
                }
                l.Add(char.ConvertFromUtf32(cp));
            }
        }
        return l;
    }
}

// сітка символів з власною тонкою прокруткою (кілька тисяч клітинок без окремих кнопок)
sealed class GlyphGrid : Control
{
    public Color Bg, Hot, Bar, Outer;
    public Func<string, Color> Tint;
    public event Action<string> Picked;
    public event Action<string> Hover;
    List<string> items = new List<string>();
    readonly int cell, barW;
    int hot = -1, top;        // top - перший видимий рядок
    bool drag; int dragDy;
    readonly Font f;

    public GlyphGrid()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        cell = Notes.Px(38);
        barW = Notes.Px(10);
        f = new Font(EmojiSets.Font, Notes.Px(20), FontStyle.Regular, GraphicsUnit.Pixel);
    }

    int Cols { get { return Math.Max(1, (Width - barW) / cell); } }
    int VRows { get { return Math.Max(1, Height / cell); } }
    int Rows { get { return (items.Count + Cols - 1) / Cols; } }
    int MaxTop { get { return Math.Max(0, Rows - VRows); } }

    public void SetItems(List<string> l)
    {
        items = l ?? new List<string>();
        hot = -1; top = 0;
        Invalidate();
    }

    void SetTop(int v) { v = Math.Max(0, Math.Min(MaxTop, v)); if (v != top) { top = v; Invalidate(); } }

    Rectangle Thumb()
    {
        int rows = Rows, vr = VRows;
        if (rows <= vr) return Rectangle.Empty;
        int h = Math.Max(Notes.Px(24), Height * vr / rows);
        int y = (Height - h) * top / Math.Max(1, MaxTop);
        return new Rectangle(Width - barW + Notes.Px(2), y, barW - Notes.Px(4), h);
    }

    int IndexAt(int x, int y)
    {
        int cols = Cols;
        if (x < 0 || x >= cols * cell || y < 0) return -1;
        int idx = (y / cell + top) * cols + x / cell;
        return idx < items.Count ? idx : -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Outer);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        GraphicsPath panel = Util.Round(new Rectangle(0, 0, Width - 1, Height - 1), Notes.Px(12));
        using (SolidBrush pb = new SolidBrush(Bg)) g.FillPath(pb, panel);
        g.SetClip(panel);
        // м'яка внутрішня тінь згори
        using (LinearGradientBrush sh = new LinearGradientBrush(new Rectangle(0, 0, Width, Notes.Px(14)), Color.FromArgb(70, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 90f))
            g.FillRectangle(sh, 0, 0, Width, Notes.Px(14));
        int cols = Cols, vr = VRows + 1;
        TextFormatFlags fl = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        for (int r = 0; r < vr; r++)
            for (int c = 0; c < cols; c++)
            {
                int idx = (r + top) * cols + c;
                if (idx >= items.Count) goto bar;
                Rectangle rc = new Rectangle(c * cell, r * cell, cell, cell);
                if (idx == hot)
                    using (GraphicsPath gp = Util.Round(new Rectangle(rc.X + 1, rc.Y + 1, rc.Width - 2, rc.Height - 2), Notes.Px(8)))
                {
                    using (SolidBrush br = new SolidBrush(Hot)) g.FillPath(br, gp);
                    using (Pen hp = new Pen(Color.FromArgb(110, Bar))) g.DrawPath(hp, gp);
                }
                if (ColorEmoji.HasOne(items[idx]))
                {
                    int m = Notes.Px(6);
                    ColorEmoji.Draw(g, items[idx], new Rectangle(rc.X + m, rc.Y + m, rc.Width - m * 2, rc.Height - m * 2));
                }
                else TextRenderer.DrawText(g, items[idx], f, rc, Tint(items[idx]), fl);
            }
        bar:
        g.ResetClip();
        using (Pen bp = new Pen(Util.Blend(Bg, Bar, 0.25f))) g.DrawPath(bp, panel);
        panel.Dispose();
        Rectangle th = Thumb();
        if (!th.IsEmpty)
        {
            using (GraphicsPath gp = Util.Round(th, th.Width / 2))
            using (SolidBrush br = new SolidBrush(drag ? Bar : Util.Blend(Bg, Bar, 0.6f)))
                g.FillPath(br, gp);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Rectangle th = Thumb();
        if (th.IsEmpty || e.X < Width - barW) return;
        if (th.Contains(e.X, e.Y)) { drag = true; dragDy = e.Y - th.Y; Capture = true; }
        else SetTop(top + (e.Y < th.Y ? -VRows : VRows));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (drag) { drag = false; Capture = false; Invalidate(); } }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (drag)
        {
            Rectangle th = Thumb();
            int span = Math.Max(1, Height - th.Height);
            SetTop((int)Math.Round((double)(e.Y - dragDy) * MaxTop / span));
            return;
        }
        int i = IndexAt(e.X, e.Y);
        if (i != hot) { hot = i; Invalidate(); if (Hover != null) Hover(i >= 0 ? items[i] : null); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hot != -1) { hot = -1; Invalidate(); if (Hover != null) Hover(null); }
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); try { if (!Focused) Focus(); } catch (Exception skipEx) { SbLog.Skip(skipEx); } }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int i = IndexAt(e.X, e.Y);
        if (i >= 0 && Picked != null) Picked(items[i]);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        SetTop(top - Math.Sign(e.Delta) * 3);
    }

    protected override void Dispose(bool disposing) { if (disposing) f.Dispose(); base.Dispose(disposing); }
}

// рядки іконок категорій (з переносом), без системної прокрутки
sealed class CatBar : Control
{
    public List<SymCat> Cats = new List<SymCat>();
    public int Sel;
    public Color Bg, Fg, Acc;
    public event Action<int> Picked;
    public event Action<int> Hover;
    readonly int cell;
    readonly Font f;
    int hot = -1;

    public CatBar()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        cell = Notes.Px(36);
        f = new Font(EmojiSets.Font, Notes.Px(18), FontStyle.Regular, GraphicsUnit.Pixel);
    }

    int Cols { get { return Math.Max(1, Width / cell); } }
    public int NeededHeight(int width) { int cols = Math.Max(1, width / cell); return ((Cats.Count + cols - 1) / cols) * cell; }

    int IndexAt(int x, int y)
    {
        int cols = Cols;
        if (x < 0 || y < 0 || x >= cols * cell) return -1;
        int i = (y / cell) * cols + x / cell;
        return i < Cats.Count ? i : -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int cols = Cols;
        TextFormatFlags fl = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        for (int i = 0; i < Cats.Count; i++)
        {
            Rectangle rc = new Rectangle((i % cols) * cell, (i / cols) * cell, cell, cell);
            if (i == Sel || i == hot)
                using (GraphicsPath gp = Util.Round(new Rectangle(rc.X + 2, rc.Y + 2, rc.Width - 4, rc.Height - 4), Notes.Px(7)))
                using (SolidBrush br = new SolidBrush(Util.Blend(Bg, Acc, i == Sel ? 0.5f : 0.22f)))
                    g.FillPath(br, gp);
            if (ColorEmoji.HasOne(Cats[i].Icon))
            {
                int m = Notes.Px(8);
                ColorEmoji.Draw(g, Cats[i].Icon, new Rectangle(rc.X + m, rc.Y + m, rc.Width - m * 2, rc.Height - m * 2));
            }
            else TextRenderer.DrawText(g, Cats[i].Icon, f, rc, i == Sel ? Fg : Util.Blend(Bg, Fg, 0.8f), fl);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = IndexAt(e.X, e.Y);
        if (i != hot) { hot = i; Invalidate(); if (Hover != null) Hover(i); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hot != -1) { hot = -1; Invalidate(); if (Hover != null) Hover(-1); }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int i = IndexAt(e.X, e.Y);
        if (i >= 0 && Picked != null) Picked(i);
    }

    protected override void Dispose(bool disposing) { if (disposing) f.Dispose(); base.Dispose(disposing); }
}

// панель вибору символів: категорії-іконки зверху, сітка нижче, поле для довільного коду Unicode внизу
sealed class SymbolPicker : Form
{
    public static int LastClosed;
    readonly Action<string> pick;
    readonly Color bg, fg, acc, side;
    readonly CatBar cats = new CatBar();
    readonly GlyphGrid grid = new GlyphGrid();
    readonly Label status = new Label(), title = new Label();
    readonly RoundBox hexBox = new RoundBox();
    readonly RoundButton go = new RoundButton();
    readonly Font small, titleF, chipF;
    readonly List<SymCat> list = new List<SymCat>();
    Rectangle capsule, chip, rule;

    public SymbolPicker(NoteTheme t, Action<string> pick)
    {
        this.pick = pick;
        int W = Notes.Px(600), H = Notes.Px(450), pad = Notes.Px(10);
        bg = Util.Blend(Util.Opaque(t.Body), Util.Opaque(t.Fg), 0.07f);
        fg = Util.Opaque(t.Fg);
        acc = Util.Opaque(t.Border);
        side = Util.Blend(bg, Color.Black, 0.2f);
        FormBorderStyle = FormBorderStyle.None;
        AppIcon.Apply(this);
        ShowInTaskbar = false;
        BackColor = bg;
        KeyPreview = true;
        ClientSize = new Size(W, H);
        small = new Font("Segoe UI", Notes.Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
        titleF = new Font("Segoe UI", Notes.Px(13), FontStyle.Bold, GraphicsUnit.Pixel);
        chipF = new Font("Segoe UI", Notes.Px(12), FontStyle.Bold, GraphicsUnit.Pixel);

        if (EmojiSets.Recent.Count > 0) list.Add(new SymCat("🕘", L.T("Останні"), new List<string>(EmojiSets.Recent)));
        list.AddRange(EmojiSets.Cats);
        cats.Cats = list;
        cats.Bg = bg; cats.Fg = fg; cats.Acc = acc;
        int ch = cats.NeededHeight(W - pad * 2);
        cats.SetBounds(pad, pad, W - pad * 2, ch);
        Controls.Add(cats);

        title.ForeColor = fg; title.BackColor = Color.Transparent; title.Font = titleF;
        title.TextAlign = ContentAlignment.MiddleLeft;
        title.SetBounds(pad + Notes.Px(4), pad + ch + Notes.Px(2), W - pad * 2, Notes.Px(22));
        Controls.Add(title);
        rule = new Rectangle(pad, title.Bottom + Notes.Px(1), W - pad * 2, 2);

        int capH = Notes.Px(40), capY = H - pad - capH;
        capsule = new Rectangle(pad, capY, W - pad * 2, capH);
        int gy = title.Bottom + Notes.Px(6);
        grid.Outer = bg;
        grid.Bg = Util.Blend(bg, Color.Black, 0.14f);
        grid.Hot = Util.Blend(bg, acc, 0.4f);
        grid.Bar = Util.Blend(bg, fg, 0.55f);
        grid.Tint = delegate(string s) { return EmojiSets.ColorFor(t); };
        grid.SetBounds(pad, gy, W - pad * 2, capY - Notes.Px(8) - gy);
        grid.Picked += delegate(string s) { this.pick(s); };
        grid.Hover += delegate(string s) { status.Text = s == null ? Count() : "U+" + char.ConvertToUtf32(s, 0).ToString("X4"); };
        Controls.Add(grid);

        cats.Picked += delegate(int i) { PickCat(i); };
        cats.Hover += delegate(int i) { title.Text = i >= 0 ? list[i].Name : list[cats.Sel].Name; };

        // нижня капсула: мітка "Код U+", поле, кнопка, стан
        int cy = capY + Notes.Px(7), ih = capH - Notes.Px(14);
        chip = new Rectangle(capsule.X + Notes.Px(8), cy, Notes.Px(74), ih);
        int gm = Notes.Px(3);
        hexBox.Fill = side; hexBox.Edge = Util.Blend(side, acc, 0.45f); hexBox.Glow = acc;
        hexBox.ForeColor = fg;
        hexBox.Tb.Font = new Font("Segoe UI", Notes.Px(13), FontStyle.Regular, GraphicsUnit.Pixel);
        hexBox.SetBounds(chip.Right + Notes.Px(8) - gm, cy - gm, Notes.Px(150) + gm * 2, ih + gm * 2);
        hexBox.Tb.KeyDown += delegate(object o, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; e.Handled = true; InsertHex(); } };
        Controls.Add(hexBox);

        go.Text = L.T("Вставити");
        go.Font = chipF;
        go.ForeColor = fg;
        go.C1 = Util.Blend(bg, acc, 0.55f); go.C2 = Util.Blend(bg, acc, 0.22f);
        go.Edge = Util.Blend(bg, acc, 0.7f); go.Glow = acc;
        go.SetBounds(hexBox.Right + Notes.Px(6) - gm, cy - gm, Notes.Px(100) + gm * 2, ih + gm * 2);
        go.Click += delegate { InsertHex(); };
        Controls.Add(go);

        status.ForeColor = Util.Blend(bg, fg, 0.7f); status.BackColor = Color.Transparent; status.Font = small;
        status.TextAlign = ContentAlignment.MiddleRight;
        status.SetBounds(go.Right + Notes.Px(6), cy, capsule.Right - Notes.Px(12) - go.Right - Notes.Px(6), ih);
        Controls.Add(status);

        PickCat(0);
        Deactivate += delegate { if (!IsDisposed) Close(); };
        FormClosed += delegate { LastClosed = Environment.TickCount; };
        KeyDown += delegate(object o, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
        Resize += delegate { RoundCorners(); };
        RoundCorners();
    }

    void RoundCorners()
    {
        try
        {
            using (GraphicsPath gp = Util.Round(new Rectangle(0, 0, Width, Height), Notes.Px(16))) Util.SetRegion(this, gp);
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    string Count() { return list[cats.Sel].Items.Count + L.T(" символів"); }

    void PickCat(int i)
    {
        cats.Sel = i;
        cats.Invalidate();
        title.Text = list[i].Name;
        grid.SetItems(list[i].Items);
        status.Text = Count();
    }

    // "1F600", "U+2764", "0x2665 1F4A9" - кілька кодів через пробіл
    void InsertHex()
    {
        StringBuilder sb = new StringBuilder();
        foreach (string part in hexBox.Tb.Text.Split(new char[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string h = part.Trim();
            if (h.StartsWith("U+", StringComparison.OrdinalIgnoreCase) || h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h.Substring(2);
            int cp;
            if (!int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cp) || cp < 0x20 || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
            {
                status.Text = L.T("Невірний код");
                return;
            }
            sb.Append(char.ConvertFromUtf32(cp));
        }
        if (sb.Length == 0) return;
        pick(sb.ToString());
        status.Text = L.T("Вставлено");
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // тонка лінія під заголовком, що згасає
        using (LinearGradientBrush lb = new LinearGradientBrush(rule, acc, Color.FromArgb(0, acc), 0f))
            g.FillRectangle(lb, rule);
        // нижня капсула
        using (GraphicsPath gp = Util.Round(capsule, capsule.Height / 2))
        {
            using (SolidBrush br = new SolidBrush(side)) g.FillPath(br, gp);      // однотонна: поле вводу зливається з нею
            using (Pen p = new Pen(Util.Blend(bg, acc, 0.4f))) g.DrawPath(p, gp);
        }
        using (GraphicsPath gp = Util.Round(new Rectangle(capsule.X + 2, capsule.Y + 1, capsule.Width - 4, capsule.Height - 2), capsule.Height / 2 - 1))
        using (Pen p = new Pen(Color.FromArgb(26, 255, 255, 255)))
        { g.SetClip(new Rectangle(capsule.X, capsule.Y, capsule.Width, capsule.Height / 2)); g.DrawPath(p, gp); g.ResetClip(); }
        // мітка "Код U+"
        using (GraphicsPath gp = Util.Round(chip, chip.Height / 2))
        {
            using (LinearGradientBrush br = new LinearGradientBrush(chip, Util.Blend(bg, acc, 0.6f), Util.Blend(bg, acc, 0.28f), 90f))
                g.FillPath(br, gp);
            using (Pen p = new Pen(Util.Blend(bg, acc, 0.75f))) g.DrawPath(p, gp);
        }
        TextRenderer.DrawText(g, L.T("Код U+"), chipF, chip, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        // зовнішня рамка вікна
        using (GraphicsPath gp = Util.Round(new Rectangle(0, 0, Width - 1, Height - 1), Notes.Px(16)))
        using (Pen p = new Pen(acc, 1.5f)) g.DrawPath(p, gp);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { small.Dispose(); titleF.Dispose(); chipF.Dispose(); }
        base.Dispose(disposing);
    }
}

// ---------- тематичний фон: ледь помітний візерунок (не акцентує, не відволікає) ----------
