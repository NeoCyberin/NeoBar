using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// ======================================================================
//  Власні тематичні вікна віджетів (SbWindow), палітра вікна й векторні піктограми.
//  Поруч: Toast.cs - сповіщення, Chime.cs - звук сигналу, LayeredForm.cs - вікна з прозорістю.
// ======================================================================

// Палітра вікна з теми панелі
sealed class Pal
{
    public Theme T;
    public Color Bg, Fg, Sub, Acc, Card, Line, Field, Good, Warn, Bad;
    public int Rad;
    public string Face;

    public static Pal Of(Theme t, Font basis, float sc)
    {
        Pal p = new Pal();
        p.T = t;
        p.Bg = PopStyle.Bg(t);
        p.Fg = PopStyle.Text(t);
        p.Sub = Util.Blend(p.Bg, p.Fg, 0.62f);
        p.Acc = Util.Readable(Util.Opaque(t.Accent), p.Bg, 90);
        p.Card = Util.Blend(p.Bg, p.Fg, Util.Lum(p.Bg) > 140 ? 0.06f : 0.07f);
        p.Line = Util.Blend(p.Bg, p.Fg, 0.16f);
        p.Field = Util.Blend(p.Bg, p.Fg, 0.11f);
        bool light = Util.Lum(p.Bg) > 140;
        p.Good = light ? Color.FromArgb(30, 150, 80) : Color.FromArgb(80, 210, 120);
        p.Warn = light ? Color.FromArgb(200, 130, 10) : Color.FromArgb(245, 180, 50);
        p.Bad = light ? Color.FromArgb(205, 50, 50) : Color.FromArgb(240, 90, 90);
        p.Rad = Math.Max(4, (int)Math.Round(Math.Max(4, t.Radius) * sc));
        p.Face = basis != null ? basis.FontFamily.Name + "|Segoe UI" : "Segoe UI";
        return p;
    }

    // Рукописний шрифт теми («Артистична») - для звичайного й великого тексту вікон; дрібні підписи
    // (підказки, пояснення, «Колір: …») ним погано читаються, тож вони - звичайним шрифтом
    public const float HandMinPt = 9f;

    public Font Font(float pt, FontStyle st)
    {
        if (pt < HandMinPt && Util.IsHandFace(Face)) return Util.MakeFont("Segoe UI", pt, st);
        return Util.MakeFont(Face, pt, st);
    }
}

// Векторні піктограми для кнопок (не залежать від шрифтів)
static class Ico
{
    // t: 0 - замок відкритий, 1 - закритий; проміжні значення - кадри анімації (дужка опускається й стає на місце)
    static void LockShape(Graphics g, Pen p, Brush b, float cx, float cy, float h, float t)
    {
        float o = 1f - t;
        g.FillRectangle(b, cx - h * 0.62f, cy - h * 0.05f, h * 1.24f, h * 0.85f);
        float ax = cx - h * 0.4f + o * h * 0.38f, ay = cy - h * 0.85f - o * h * 0.15f;
        g.DrawArc(p, ax, ay, h * 0.8f, h * 0.8f, 180, 180);
        float ly = cy - h * 0.45f - o * h * 0.15f;
        g.DrawLine(p, ax + h * 0.8f, ly, ax + h * 0.8f, cy - o * h * 0.25f);
        float ll = h * 0.45f * t;
        if (ll > 0.5f) g.DrawLine(p, ax, ly, ax, ly + ll);
    }

    public static void DrawLock(Graphics g, RectangleF r, Color c, float t)
    {
        SmoothingMode sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Math.Min(r.Width, r.Height);
        using (Pen p = new Pen(c, Math.Max(1.4f, s * 0.11f)))
        using (SolidBrush b = new SolidBrush(c))
        {
            p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
            LockShape(g, p, b, r.X + r.Width / 2f, r.Y + r.Height / 2f, s * 0.5f, Math.Max(0f, Math.Min(1f, t)));
        }
        g.SmoothingMode = sm;
    }

    public static void Draw(Graphics g, string key, RectangleF r, Color c)
    {
        SmoothingMode sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Math.Min(r.Width, r.Height);
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        float lw = Math.Max(1.4f, s * 0.11f);
        using (Pen p = new Pen(c, lw))
        using (SolidBrush b = new SolidBrush(c))
        {
            p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
            float h = s * 0.5f;
            switch (key)
            {
                case "play":
                    g.FillPolygon(b, new PointF[] { new PointF(cx - h * 0.55f, cy - h * 0.75f), new PointF(cx + h * 0.8f, cy), new PointF(cx - h * 0.55f, cy + h * 0.75f) });
                    break;
                case "pause":
                    g.FillRectangle(b, cx - h * 0.6f, cy - h * 0.7f, h * 0.42f, h * 1.4f);
                    g.FillRectangle(b, cx + h * 0.18f, cy - h * 0.7f, h * 0.42f, h * 1.4f);
                    break;
                case "stop":
                    g.FillRectangle(b, cx - h * 0.6f, cy - h * 0.6f, h * 1.2f, h * 1.2f);
                    break;
                case "reset":
                    g.DrawArc(p, cx - h * 0.7f, cy - h * 0.7f, h * 1.4f, h * 1.4f, -60, 290);
                    g.FillPolygon(b, new PointF[] { new PointF(cx + h * 0.2f, cy - h * 1.0f), new PointF(cx + h * 0.75f, cy - h * 0.62f), new PointF(cx + h * 0.15f, cy - h * 0.25f) });
                    break;
                case "flag":
                    g.DrawLine(p, cx - h * 0.55f, cy - h * 0.8f, cx - h * 0.55f, cy + h * 0.85f);
                    g.FillPolygon(b, new PointF[] { new PointF(cx - h * 0.55f, cy - h * 0.8f), new PointF(cx + h * 0.75f, cy - h * 0.45f), new PointF(cx - h * 0.55f, cy - h * 0.05f) });
                    break;
                case "plus":
                    g.DrawLine(p, cx - h * 0.7f, cy, cx + h * 0.7f, cy);
                    g.DrawLine(p, cx, cy - h * 0.7f, cx, cy + h * 0.7f);
                    break;
                case "minus":
                    g.DrawLine(p, cx - h * 0.7f, cy, cx + h * 0.7f, cy);
                    break;
                case "close":
                    g.DrawLine(p, cx - h * 0.55f, cy - h * 0.55f, cx + h * 0.55f, cy + h * 0.55f);
                    g.DrawLine(p, cx + h * 0.55f, cy - h * 0.55f, cx - h * 0.55f, cy + h * 0.55f);
                    break;
                case "left":
                    g.DrawLines(p, new PointF[] { new PointF(cx + h * 0.3f, cy - h * 0.65f), new PointF(cx - h * 0.35f, cy), new PointF(cx + h * 0.3f, cy + h * 0.65f) });
                    break;
                case "right":
                    g.DrawLines(p, new PointF[] { new PointF(cx - h * 0.3f, cy - h * 0.65f), new PointF(cx + h * 0.35f, cy), new PointF(cx - h * 0.3f, cy + h * 0.65f) });
                    break;
                case "check":
                    g.DrawLines(p, new PointF[] { new PointF(cx - h * 0.65f, cy + h * 0.05f), new PointF(cx - h * 0.15f, cy + h * 0.55f), new PointF(cx + h * 0.7f, cy - h * 0.5f) });
                    break;
                case "trash":
                    g.DrawLine(p, cx - h * 0.75f, cy - h * 0.55f, cx + h * 0.75f, cy - h * 0.55f);
                    g.DrawLine(p, cx - h * 0.25f, cy - h * 0.8f, cx + h * 0.25f, cy - h * 0.8f);
                    g.DrawLines(p, new PointF[] { new PointF(cx - h * 0.55f, cy - h * 0.45f), new PointF(cx - h * 0.42f, cy + h * 0.8f), new PointF(cx + h * 0.42f, cy + h * 0.8f), new PointF(cx + h * 0.55f, cy - h * 0.45f) });
                    break;
                case "folder":   // тека: вушко й корпус
                    g.DrawPolygon(p, new PointF[] { new PointF(cx - h * 0.85f, cy + h * 0.6f), new PointF(cx - h * 0.85f, cy - h * 0.6f), new PointF(cx - h * 0.2f, cy - h * 0.6f),
                                                    new PointF(cx + h * 0.05f, cy - h * 0.3f), new PointF(cx + h * 0.85f, cy - h * 0.3f), new PointF(cx + h * 0.85f, cy + h * 0.6f) });
                    break;
                case "eye":    // око: показати введене
                case "eyeoff":
                    g.DrawEllipse(p, cx - h * 0.95f, cy - h * 0.55f, h * 1.9f, h * 1.1f);
                    g.FillEllipse(b, cx - h * 0.28f, cy - h * 0.28f, h * 0.56f, h * 0.56f);
                    if (key == "eyeoff") g.DrawLine(p, cx - h * 0.85f, cy + h * 0.7f, cx + h * 0.85f, cy - h * 0.7f);
                    break;
                case "lock":   // замок: корпус і дужка
                    LockShape(g, p, b, cx, cy, h, 1f);
                    break;
                case "unlock":   // відкритий замок: дужка піднята й відведена
                    LockShape(g, p, b, cx, cy, h, 0f);
                    break;
                case "desk":   // на робочий стіл: монітор зі стрілкою
                    g.DrawRectangle(p, cx - h * 0.8f, cy - h * 0.7f, h * 1.6f, h * 1.05f);
                    g.DrawLine(p, cx, cy + h * 0.35f, cx, cy + h * 0.7f);
                    g.DrawLine(p, cx - h * 0.45f, cy + h * 0.75f, cx + h * 0.45f, cy + h * 0.75f);
                    break;
                case "bell":
                    using (GraphicsPath bp = new GraphicsPath())
                    {
                        bp.AddArc(cx - h * 0.55f, cy - h * 0.75f, h * 1.1f, h * 1.1f, 180, 180);
                        bp.AddLine(cx + h * 0.55f, cy - h * 0.2f, cx + h * 0.75f, cy + h * 0.45f);
                        bp.AddLine(cx + h * 0.75f, cy + h * 0.45f, cx - h * 0.75f, cy + h * 0.45f);
                        bp.CloseFigure();
                        g.FillPath(b, bp);
                    }
                    g.FillEllipse(b, cx - h * 0.18f, cy + h * 0.5f, h * 0.36f, h * 0.36f);
                    break;
                case "sync":
                    g.DrawArc(p, cx - h * 0.7f, cy - h * 0.7f, h * 1.4f, h * 1.4f, 200, 140);
                    g.DrawArc(p, cx - h * 0.7f, cy - h * 0.7f, h * 1.4f, h * 1.4f, 20, 140);
                    g.FillPolygon(b, new PointF[] { new PointF(cx + h * 0.4f, cy - h * 0.95f), new PointF(cx + h * 0.95f, cy - h * 0.55f), new PointF(cx + h * 0.35f, cy - h * 0.25f) });
                    g.FillPolygon(b, new PointF[] { new PointF(cx - h * 0.4f, cy + h * 0.95f), new PointF(cx - h * 0.95f, cy + h * 0.55f), new PointF(cx - h * 0.35f, cy + h * 0.25f) });
                    break;
                case "eject":
                    g.FillPolygon(b, new PointF[] { new PointF(cx, cy - h * 0.75f), new PointF(cx + h * 0.75f, cy + h * 0.1f), new PointF(cx - h * 0.75f, cy + h * 0.1f) });
                    g.FillRectangle(b, cx - h * 0.75f, cy + h * 0.38f, h * 1.5f, h * 0.3f);
                    break;
                case "drive":   // диск: корпус із індикатором
                    using (GraphicsPath dp = Util.Round(Rectangle.Round(new RectangleF(cx - h * 0.85f, cy - h * 0.45f, h * 1.7f, h * 0.9f)), Math.Max(1, (int)(h * 0.2f))))
                        g.DrawPath(p, dp);
                    g.FillEllipse(b, cx + h * 0.4f, cy - h * 0.1f, h * 0.22f, h * 0.22f);
                    g.DrawLine(p, cx - h * 0.55f, cy, cx + h * 0.1f, cy);
                    break;
                case "usb":     // флешка: корпус і роз'єм
                    using (GraphicsPath up = Util.Round(Rectangle.Round(new RectangleF(cx - h * 0.4f, cy - h * 0.25f, h * 0.8f, h * 1.1f)), Math.Max(1, (int)(h * 0.18f))))
                        g.DrawPath(p, up);
                    g.DrawRectangle(p, cx - h * 0.25f, cy - h * 0.85f, h * 0.5f, h * 0.6f);
                    g.FillRectangle(b, cx - h * 0.13f, cy - h * 0.7f, h * 0.08f, h * 0.15f);
                    g.FillRectangle(b, cx + h * 0.05f, cy - h * 0.7f, h * 0.08f, h * 0.15f);
                    break;
                case "gear":   // гайковий ключ (налаштування віджета)
                case "wrench":
                {
                    GraphicsState ws = g.Save();
                    g.TranslateTransform(cx, cy);
                    g.RotateTransform(45f);
                    float u = h * 0.95f;
                    // голівка з відкритим зевом
                    g.DrawArc(p, -u * 0.5f, -u * 1.0f, u, u, 112, 123);
                    g.DrawArc(p, -u * 0.5f, -u * 1.0f, u, u, 305, 123);
                    g.DrawLine(p, -u * 0.287f, -u * 0.91f, -u * 0.17f, -u * 0.62f);
                    g.DrawLine(p, u * 0.287f, -u * 0.91f, u * 0.17f, -u * 0.62f);
                    g.DrawLine(p, -u * 0.17f, -u * 0.62f, u * 0.17f, -u * 0.62f);
                    // ручка
                    g.DrawLine(p, -u * 0.18f, -u * 0.034f, -u * 0.18f, u * 0.9f);
                    g.DrawLine(p, u * 0.18f, -u * 0.034f, u * 0.18f, u * 0.9f);
                    g.DrawArc(p, -u * 0.18f, u * 0.72f, u * 0.36f, u * 0.36f, 0, 180);
                    g.Restore(ws);
                    break;
                }
                case "pin":
                    g.DrawEllipse(p, cx - h * 0.45f, cy - h * 0.85f, h * 0.9f, h * 0.9f);
                    g.DrawLine(p, cx, cy + h * 0.05f, cx, cy + h * 0.85f);
                    break;
                case "shield":   // щит із галочкою
                    using (GraphicsPath sp = new GraphicsPath())
                    {
                        sp.AddLine(cx, cy - h * 0.92f, cx + h * 0.78f, cy - h * 0.62f);
                        sp.AddBezier(cx + h * 0.78f, cy - h * 0.62f, cx + h * 0.8f, cy + h * 0.15f, cx + h * 0.45f, cy + h * 0.62f, cx, cy + h * 0.92f);
                        sp.AddBezier(cx, cy + h * 0.92f, cx - h * 0.45f, cy + h * 0.62f, cx - h * 0.8f, cy + h * 0.15f, cx - h * 0.78f, cy - h * 0.62f);
                        sp.CloseFigure();
                        g.DrawPath(p, sp);
                    }
                    g.DrawLines(p, new PointF[] { new PointF(cx - h * 0.34f, cy + h * 0.02f), new PointF(cx - h * 0.08f, cy + h * 0.3f), new PointF(cx + h * 0.38f, cy - h * 0.24f) });
                    break;
                case "key":   // ключ: кільце й борідка
                    g.DrawEllipse(p, cx - h * 0.9f, cy - h * 0.62f, h * 0.9f, h * 0.9f);
                    g.DrawLine(p, cx, cy - h * 0.17f, cx + h * 0.9f, cy - h * 0.17f);
                    g.DrawLine(p, cx + h * 0.62f, cy - h * 0.17f, cx + h * 0.62f, cy + h * 0.2f);
                    g.DrawLine(p, cx + h * 0.88f, cy - h * 0.17f, cx + h * 0.88f, cy + h * 0.32f);
                    break;
            }
        }
        g.SmoothingMode = sm;
    }
}

// ----------------------------------------------------------------------
//  Базове вікно віджета: шапка з назвою, вкладками й кнопкою закриття; вміст малюється
//  кодом (кнопки, перемикачі, картки) у кольорах теми. Без системних елементів.
// ----------------------------------------------------------------------
class SbWindow : Form
{
    protected readonly NeoBar Bar;
    protected Pal P;
    protected Theme T;
    protected readonly float Sc;
    protected Font F, FB, FS, FH, FBig, FHuge;
    protected string Caption;
    protected string Glyph = "";      // символ вікна в шапці (Segoe MDL2 / Fluent Icons); порожньо - іконка програми
    protected string[] Tabs = new string[0];
    protected int Tab;
    protected int HeadH;
    protected readonly Timer Ticker = new Timer();

    sealed class Hit
    {
        public Rectangle R;
        public Action Click, RClick;
        public Action<int> Wheel;
        public Action<Point> Drag;      // повзунок: тягнути мишею
        public bool Hand = true;
        public Cursor Cur;              // свій курсор (напр. хрестик над палітрою)
    }
    readonly List<Hit> hits = new List<Hit>();
    readonly List<Hit> build = new List<Hit>();
    List<KeyValuePair<Rectangle, string>> tips = new List<KeyValuePair<Rectangle, string>>(), tipBuild = new List<KeyValuePair<Rectangle, string>>();
    Timer tipTimer;
    ToolTip tipBox;
    string tipNow = "", tipWait = "";
    int hot = -1, down = -1;

    // Перетягування файлів і папок із Провідника: вікно, що їх приймає, перевизначає ці два члени
    protected virtual bool AcceptsFiles { get { return false; } }
    protected virtual void FilesDropped(string[] files) { }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (AcceptsFiles) DropFiles.Register(this);           // від адміністратора файли приходять старим способом (WM_DROPFILES)
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        e.Effect = AcceptsFiles && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effect = AcceptsFiles && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        if (!AcceptsFiles) return;
        string[] f = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (f != null && f.Length > 0) BeginInvoke(new Action(delegate { FilesDropped(f); }));
    }

    protected override void WndProc(ref Message m)
    {
        if (AcceptsFiles)
        {
            string[] dropped = DropFiles.Take(ref m);
            if (dropped != null)
            {
                if (dropped.Length > 0) BeginInvoke(new Action(delegate { FilesDropped(dropped); }));
                return;
            }
        }
        base.WndProc(ref m);
    }
    Point mouse = new Point(-1, -1);
    bool moving;
    Point moveFrom;

    protected int Px(float v) { return (int)Math.Round(v * Sc); }

    // питання чи повідомлення в стилі теми; повертає номер кнопки (-1 - закрили)
    protected int Ask(string title, string text, params string[] buttons)
    {
        if (Bar != null) return SbDialog.Show(Bar, this, title, text, buttons);
        return SbDialog.Show(T, Sc, this, title, text, buttons);
    }

    public SbWindow(NeoBar owner, string caption, int w, int h) : this(owner, owner.CurrentTheme, owner.Dpi, owner.LabelFont, caption, w, h) { }

    // вікно без панелі (вибір профілю до запуску NeoBar): тема й масштаб задаються явно
    protected SbWindow(Theme theme, float scale, string caption, int w, int h) : this(null, theme, scale, null, caption, w, h) { }

    SbWindow(NeoBar owner, Theme theme, float scale, Font basis, string caption, int w, int h)
    {
        Bar = owner;
        T = theme;
        Sc = scale;
        P = Pal.Of(T, basis, Sc);
        Caption = caption;
        MakeFonts();
        if (owner != null) NeoBar.ThemeChanged += OnThemeChanged;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FormBorderStyle = FormBorderStyle.None;
        AppIcon.Apply(this);
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        if (AcceptsFiles) DropFiles.Prepare(this);
        BackColor = P.Bg;
        Text = caption;
        HeadH = Px(46);
        ClientSize = new Size(Px(w), Px(h));
        Ticker.Interval = 1000;
        Ticker.Tick += delegate { if (!NeoBar.Paused) OnTick(); };
    }

    void MakeFonts()
    {
        F = P.Font(9.5f, FontStyle.Regular);
        FB = P.Font(9.5f, FontStyle.Bold);
        FS = P.Font(8.25f, FontStyle.Regular);
        FH = P.Font(11.5f, FontStyle.Bold);
        FBig = P.Font(20f, FontStyle.Bold);
        FHuge = P.Font(34f, FontStyle.Bold);
    }

    // Тему панелі змінили: відкрите розширене вікно одразу переодягається (кольори, шрифт, заокруглення)
    void OnThemeChanged()
    {
        if (Bar == null || IsDisposed || !IsHandleCreated) return;
        Font[] old = new Font[] { F, FB, FS, FH, FBig, FHuge };
        T = Bar.CurrentTheme;
        P = Pal.Of(T, Bar.LabelFont, Sc);
        MakeFonts();
        BackColor = P.Bg;
        RefitInputs(old);
        foreach (Font f in old) if (f != null) f.Dispose();
        ThemeUpdated();
        OnResize(EventArgs.Empty);      // заокруглення за новим радіусом
        Invalidate();
    }

    protected virtual void ThemeUpdated() { }

    // поля введення тримали посилання на старий шрифт, який щойно знищено (звідси «Parameter is not valid» у GDI+):
    // переводимо їх на нові шрифти й кольори
    void RefitInputs(Font[] old)
    {
        foreach (Control c in Controls)
        {
            TextBoxBase tb = c as TextBoxBase;
            if (tb == null) continue;
            try
            {
                tb.Font = F;
                tb.BackColor = P.Field;
                tb.ForeColor = P.Fg;
            }
            catch (Exception ex) { Util.ErrorLog(ex); }
        }
    }

    // показати вікно в іншій темі (попередній перегляд, напр. у швидкому налаштуванні)
    protected void UseTheme(Theme t)
    {
        if (t == null) return;
        Font[] old = new Font[] { F, FB, FS, FH, FBig, FHuge };
        T = t;
        P = Pal.Of(T, null, Sc);
        MakeFonts();
        BackColor = P.Bg;
        RefitInputs(old);
        foreach (Font f in old) if (f != null) f.Dispose();
        ThemeUpdated();
        OnResize(EventArgs.Empty);
        Invalidate();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x80;          // WS_EX_TOOLWINDOW
            cp.ClassStyle |= 0x20000;    // CS_DROPSHADOW
            return cp;
        }
    }

    public DateTime LeftAt = DateTime.MinValue;   // коли вікно втратило фокус (для повторного кліку по віджету)

    // поява розширеної панелі: плавно проявляється й трохи підіймається
    System.Windows.Forms.Timer showAnim;
    int showTop;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Util.Buffer(this);
        try { if (Bar.Config.Anim) Opacity = 0; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Ticker.Start();
        OnTick();
        bool anim = false;
        try { anim = Bar.Config.Anim; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        if (!anim) { Opacity = 1; return; }
        showTop = Top;
        int lift = Px(14);
        Top = showTop + lift;
        DateTime t0 = DateTime.Now;
        showAnim = new System.Windows.Forms.Timer();
        showAnim.Interval = 15;
        showAnim.Tick += delegate
        {
            if (IsDisposed) { showAnim.Stop(); return; }
            double f = Math.Min(1.0, (DateTime.Now - t0).TotalMilliseconds / 180.0);
            double ez = 1 - (1 - f) * (1 - f) * (1 - f);
            Opacity = ez;
            Top = showTop + (int)Math.Round(lift * (1 - ez));
            if (f >= 1) { showAnim.Stop(); showAnim.Dispose(); showAnim = null; Opacity = 1; Top = showTop; }
        };
        showAnim.Start();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        LeftAt = DateTime.Now;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        try
        {
            if (P.Rad > 2 && Width > 0 && Height > 0)
                using (GraphicsPath gp = Util.Round(new Rectangle(0, 0, Width, Height), P.Rad + Px(2))) Util.SetRegion(this, gp);
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    // ширина вкладки: найдовший рядок (назва може бути у два рядки)
    int TabW(string t, Font f)
    {
        int w = 0;
        foreach (string ln in t.Split('\n')) w = Math.Max(w, TextRenderer.MeasureText(ln, f).Width);
        return w;
    }

    protected virtual void OnTick() { }
    protected virtual void PaintBody(Graphics g, Rectangle r) { }
    protected virtual void OnTab() { }
    protected virtual void OnDragEnd() { }
    protected virtual void HeadButtons(Graphics g, ref int right) { }

    // Розміщення посередині екрана (того, де панель)
    public void ShowCentered(Rectangle onScreen)
    {
        Rectangle wa = Screen.FromRectangle(onScreen).WorkingArea;
        if (Height > wa.Height - 12 || Width > wa.Width - 12)
            ClientSize = new Size(Math.Min(ClientSize.Width, wa.Width - 12), Math.Min(ClientSize.Height, wa.Height - 12));
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
        Show();
        Activate();
    }

    // Розміщення біля віджета на панелі
    public void ShowNear(Rectangle tile, string side) { ShowNear(tile, side, -1); }

    // gap: проміжок до плитки, px (-1 - стандартний)
    public void ShowNear(Rectangle tile, string side, int gapPx)
    {
        Rectangle wa = Screen.FromRectangle(tile).WorkingArea;
        // невеликий екран (ноутбук 1366×768): вікно не вище й не ширше за робочу область - інакше низ ховається за панеллю завдань
        if (Height > wa.Height - 12 || Width > wa.Width - 12)
            ClientSize = new Size(Math.Min(ClientSize.Width, wa.Width - 12), Math.Min(ClientSize.Height, wa.Height - 12));
        int gap = gapPx >= 0 ? gapPx : Px(8), x, y;
        switch (side)
        {
            case "left": x = tile.Right + gap; y = tile.Top; break;
            case "top": y = tile.Bottom + gap; x = tile.Left + tile.Width / 2 - Width / 2; break;
            case "bottom": y = tile.Top - Height - gap; x = tile.Left + tile.Width / 2 - Width / 2; break;
            default: x = tile.Left - Width - gap; y = tile.Top; break;
        }
        x = Math.Max(wa.Left + 6, Math.Min(wa.Right - Width - 6, x));
        y = Math.Max(wa.Top + 6, Math.Min(wa.Bottom - Height - 6, y));
        Location = new Point(x, y);
        Show();
        Activate();
    }

    // ---------- малювання ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        build.Clear();
        Rectangle all = ClientRectangle;
        PopStyle.Backdrop(g, T, all, HeadH, Px(100) / 100f);

        // шапка
        int pad = Px(14);
        int right = Width - Px(8);
        Rectangle xr = new Rectangle(right - Px(32), Px(7), Px(32), Px(32));
        IconBtn(g, xr, "close", delegate { Close(); }, false);
        right = xr.Left - Px(4);
        HeadButtons(g, ref right);

        int tx = pad;
        {
            int gs = Px(20);
            Rectangle gr = new Rectangle(tx, (HeadH - gs) / 2, gs, gs);
            if (!AppIcon.DrawGlyph(g, Glyph, gr, P.Acc))
            {
                Bitmap ab = AppIcon.Small();
                if (ab != null)
                {
                    InterpolationMode im = g.InterpolationMode;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(ab, gr);
                    g.InterpolationMode = im;
                }
            }
            tx += gs + Px(10);
        }
        Size ts = TextRenderer.MeasureText(Caption, FH);
        TextRenderer.DrawText(g, Caption, FH, new Rectangle(tx, 0, Math.Min(ts.Width + Px(4), right - tx), HeadH),
            P.Fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        int afterTitle = tx + ts.Width + Px(14);
        int top = HeadH;
        if (Tabs.Length > 0)
        {
            int tw = 0;
            int[] ws = new int[Tabs.Length];
            int tpad = Px(22);
            int avail = right - Px(4) - (tx + ts.Width + Px(14)) - Px(6);
            // вкладки поруч із назвою: якщо не вміщуються, спершу стискаємо відступи, потім шрифт
            Font tF = F, tFB = FB, own1 = null;
            bool fit = false;
            foreach (int tp in new int[] { 22, 16, 11, 7 })
            {
                tpad = Px(tp);
                int sum = 0;
                for (int i = 0; i < Tabs.Length; i++) sum += TabW(Tabs[i], tFB) + tpad;
                if (sum <= avail) { fit = true; break; }
            }
            int gapR = Px(4), gapT = Px(6);
            if (!fit)
            {
                // зовсім тісно: дрібніший шрифт без напівжирного, менші відступи
                own1 = new Font(F.FontFamily, Math.Max(7f, F.SizeInPoints - 1.5f), FontStyle.Regular);
                tF = own1; tFB = own1; tpad = Px(3);
                afterTitle = tx + ts.Width + Px(8); gapR = Px(2); gapT = Px(2);
            }
            for (int i = 0; i < Tabs.Length; i++) { ws[i] = TabW(Tabs[i], tFB) + tpad; tw += ws[i]; }
            if (!fit && afterTitle + tw + gapT > right - gapR)
            {
                // і дрібний шрифт не рятує - лишаємо звичайний, вкладки підуть окремим рядком
                own1.Dispose(); own1 = null; tF = F; tFB = FB; tpad = Px(7);
                afterTitle = tx + ts.Width + Px(14); gapR = Px(4); gapT = Px(6);
                tw = 0;
                for (int i = 0; i < Tabs.Length; i++) { ws[i] = TabW(Tabs[i], tFB) + tpad; tw += ws[i]; }
            }
            bool tall = false;
            foreach (string tn in Tabs) if (tn.IndexOf('\n') >= 0) tall = true;
            // дворядкова вкладка потребує двох рядків тексту (у рукописних шрифтах вони вищі, ніж у звичайних)
            int need2 = tall ? tFB.Height * 2 + Px(2) : 0;
            int th = tall ? Math.Max(Px(38), need2 + Px(4)) : Px(30);
            // не вміщаються біля назви (або дві стрічки не влізають у шапку) - окремий рядок під шапкою
            bool row2 = afterTitle + tw + gapT > right - gapR || (tall && need2 > HeadH - Px(12));
            int x0, ty;
            if (row2)
            {
                using (Pen lp0 = new Pen(P.Line, 1f)) g.DrawLine(lp0, Px(10), HeadH, Width - Px(10), HeadH);
                x0 = Math.Max(pad, (Width - tw) / 2);
                ty = HeadH + Px(8);
                top = ty + th + Px(8);
            }
            else
            {
                x0 = Math.Max(afterTitle, (Width - tw) / 2);
                if (x0 + tw > right - gapR) x0 = Math.Max(afterTitle, right - gapR - tw);
                th = Math.Min(th, HeadH - Px(12));
                ty = (HeadH - th) / 2;
            }
            Rectangle track = new Rectangle(x0 - Px(3), ty - Px(3), tw + Px(6), th + Px(6));
            using (SolidBrush b = new SolidBrush(P.Card)) Util.FillRound(g, b, track, Math.Min(P.Rad + Px(3), track.Height / 2));
            int x = x0;
            for (int i = 0; i < Tabs.Length; i++)
            {
                Rectangle r = new Rectangle(x, ty, ws[i], th);
                bool sel = i == Tab, h = Over(r);
                if (sel || h)
                    using (SolidBrush b = new SolidBrush(sel ? Util.Blend(P.Bg, P.Acc, 0.38f) : Util.Blend(P.Card, P.Fg, 0.08f)))
                        Util.FillRound(g, b, r, Math.Min(P.Rad, r.Height / 2));
                TextRenderer.DrawText(g, Tabs[i], sel ? tFB : tF, r, sel ? P.Fg : P.Sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | (Tabs[i].IndexOf('\n') >= 0 ? 0 : TextFormatFlags.SingleLine));
                int k = i;
                Hot(r, delegate { if (Tab != k) { Tab = k; OnTab(); Invalidate(); } });
                x += ws[i];
            }
            if (own1 != null) own1.Dispose();
        }
        using (Pen lp = new Pen(P.Line, 1f)) g.DrawLine(lp, Px(10), top, Width - Px(10), top);

        Rectangle body = new Rectangle(pad, top + Px(10), Width - pad * 2, Height - top - Px(10) - pad);
        try { PaintBody(g, body); }
        catch (Exception ex)
        {
            Util.ErrorLog(ex);
            TextRenderer.DrawText(g, ex.Message, FS, body, P.Bad, TextFormatFlags.WordBreak);
        }

        // рамка
        Color edge = T.Style == IconStyle.Neon ? P.Acc : Util.Blend(P.Bg, Util.Opaque(T.Edge), 0.8f);
        using (Pen ep = new Pen(edge, Math.Max(1f, Px(T.EdgeW))))
        using (GraphicsPath gp = Util.Round(new Rectangle(0, 0, Width - 1, Height - 1), P.Rad + Px(2)))
            g.DrawPath(ep, gp);

        // клавіатура: рамка навколо вибраного Tab-ом елемента
        if (kbMode)
        {
            List<int> fk = Focusable(build);
            if (fk.Count == 0) kbFocus = -1;
            else
            {
                kbFocus = Math.Max(0, Math.Min(fk.Count - 1, kbFocus));
                Rectangle fr = Rectangle.Inflate(build[fk[kbFocus]].R, Px(2), Px(2));
                using (Pen fp = new Pen(P.Acc, Math.Max(2f, Px(2))))
                using (GraphicsPath gp = Util.Round(fr, Math.Min(P.Rad + Px(2), fr.Height / 2)))
                    g.DrawPath(fp, gp);
            }
        }

        hits.Clear();
        hits.AddRange(build);
        tips = tipBuild;
        tipBuild = new List<KeyValuePair<Rectangle, string>>();
        hot = HitAt(mouse);
        if (tipNow.Length > 0 && TipAt(mouse) != tipNow) TipHide();       // вміст зсунувся (прокрутка) - підказка вже не про те
    }

    // ---------- підказки ----------
    // Tip викликається під час малювання (як Hot): над областю r, коли курсор постоїть, з'явиться підказка з текстом text
    protected void Tip(Rectangle r, string text)
    {
        if (text != null && text.Length > 0 && r.Width > 0 && r.Height > 0) tipBuild.Add(new KeyValuePair<Rectangle, string>(r, text));
    }

    string TipAt(Point p)
    {
        for (int i = tips.Count - 1; i >= 0; i--) if (tips[i].Key.Contains(p)) return tips[i].Value;
        return "";
    }

    void TipHide()
    {
        tipNow = ""; tipWait = "";
        if (tipTimer != null) tipTimer.Stop();
        if (tipBox != null) try { tipBox.Hide(this); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    void TipMove()
    {
        string t = TipAt(mouse);
        if (t == tipNow || (t == tipWait && tipNow.Length == 0)) return;
        TipHide();
        if (t.Length == 0 || dragging != null || moving) return;
        tipWait = t;
        if (tipTimer == null)
        {
            tipTimer = new Timer();
            tipTimer.Interval = 450;
            tipTimer.Tick += delegate
            {
                tipTimer.Stop();
                if (tipWait.Length == 0 || TipAt(mouse) != tipWait || !Visible) return;
                if (tipBox == null)
                {
                    tipBox = new ToolTip();
                    tipBox.OwnerDraw = true;
                    tipBox.Popup += delegate (object s, PopupEventArgs e)
                    {
                        Size sz = TextRenderer.MeasureText(tipWait, FS, new Size(Px(380), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                        e.ToolTipSize = new Size(sz.Width + Px(22), sz.Height + Px(16));
                    };
                    tipBox.Draw += delegate (object s, DrawToolTipEventArgs e)
                    {
                        Rectangle b = new Rectangle(0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                        using (SolidBrush br = new SolidBrush(Util.Opaque(Util.Blend(P.Bg, P.Fg, 0.08f)))) e.Graphics.FillRectangle(br, b);
                        using (Pen pn = new Pen(Util.Blend(P.Bg, P.Acc, 0.6f))) e.Graphics.DrawRectangle(pn, b);
                        TextRenderer.DrawText(e.Graphics, e.ToolTipText, FS, new Rectangle(Px(11), Px(8), b.Width - Px(22), b.Height - Px(16)), P.Fg, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                    };
                }
                tipNow = tipWait;
                try { tipBox.Show(tipNow, this, mouse.X + Px(14), mouse.Y + Px(22), 30000); } catch { tipNow = ""; }
            };
        }
        tipTimer.Start();
    }

    // Підігнати висоту вікна під вміст: contentBottom - нижній край вмісту в координатах вікна (як r.Bottom у PaintBody);
    // нижній відступ лишається таким, як у вікна зараз. Виклик - наприкінці PaintBody вікон, у яких вміст має різну висоту.
    bool fitBusy;
    protected void FitTo(Rectangle body, int contentBottom, int minH)
    {
        int pad = Height - body.Bottom;
        int want = Math.Max(minH, contentBottom + pad);
        try { want = Math.Min(want, Screen.FromControl(this).WorkingArea.Height - Px(40)); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        if (Math.Abs(want - Height) <= Px(3) || fitBusy || !IsHandleCreated) return;
        fitBusy = true;
        BeginInvoke(new Action(delegate
        {
            fitBusy = false;
            if (IsDisposed) return;
            Height = want;
            try
            {
                Rectangle wa = Screen.FromControl(this).WorkingArea;
                if (Bottom > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - Height);
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
            Invalidate();
        }));
    }

    // ---------- елементи ----------

    protected bool Over(Rectangle r) { return r.Contains(mouse); }

    protected void Hot(Rectangle r, Action click)
    {
        Hit h = new Hit(); h.R = r; h.Click = click; build.Add(h);
    }

    protected void HotR(Rectangle r, Action click, Action rclick)
    {
        Hit h = new Hit(); h.R = r; h.Click = click; h.RClick = rclick; build.Add(h);
    }

    Action<Point> dragging;

    // повзунок 0..100: клік чи перетягування по доріжці
    protected void Slider(Graphics g, Rectangle r, int value, Action<int> set)
    {
        int th = Math.Max(4, Px(5)), kn = Px(16);
        Rectangle tr = new Rectangle(r.X + kn / 2, r.Y + (r.Height - th) / 2, r.Width - kn, th);
        using (SolidBrush b = new SolidBrush(Util.Blend(P.Bg, P.Fg, 0.2f))) Util.FillRound(g, b, tr, th / 2);
        int kx = tr.X + (int)(tr.Width * Math.Max(0, Math.Min(100, value)) / 100.0);
        using (SolidBrush b = new SolidBrush(P.Acc)) Util.FillRound(g, b, new Rectangle(tr.X, tr.Y, Math.Max(th, kx - tr.X), th), th / 2);
        Rectangle kr = new Rectangle(kx - kn / 2, r.Y + (r.Height - kn) / 2, kn, kn);
        using (SolidBrush b = new SolidBrush(Over(r) ? Util.Blend(P.Acc, P.Fg, 0.25f) : P.Acc)) g.FillEllipse(b, kr);
        Hit h = new Hit(); h.R = r; h.Hand = true;
        h.Click = delegate { };
        h.Drag = delegate (Point pt)
        {
            int v = (int)Math.Round((pt.X - tr.X) * 100.0 / Math.Max(1, tr.Width));
            set(Math.Max(0, Math.Min(100, v)));
        };
        build.Add(h);
    }

    // область, яку тягнуть мишею (палітра, повзунок довільної форми)
    protected void DragArea(Rectangle r, Action<Point> drag) { DragArea(r, drag, Cursors.Cross); }

    protected void DragArea(Rectangle r, Action<Point> drag, Cursor cur)
    {
        Hit h = new Hit(); h.R = r; h.Click = delegate { }; h.Drag = drag; h.Cur = cur; build.Add(h);
    }

    // тонка смуга прокрутки в стилі теми (замість світлої системної). total/view/pos - в одних одиницях (пікселі чи рядки),
    // set(нове положення). Показується лише коли вміст більший за видиму частину; малювати після рядків списку (верхня за порядком зона перемагає).
    protected void VBar(Graphics g, Rectangle trk, int total, int view, int pos, Action<int> set)
    {
        if (total <= view || view <= 0 || trk.Height < Px(24)) return;
        int max = total - view;
        pos = Math.Max(0, Math.Min(max, pos));
        int th = Math.Min(trk.Height, Math.Max(Px(26), (int)((long)trk.Height * view / total)));
        int span = Math.Max(1, trk.Height - th);
        int ty = trk.Y + (int)((long)span * pos / max);
        Rectangle area = new Rectangle(trk.X - Px(5), trk.Y, trk.Width + Px(10), trk.Height);
        bool hot2 = Over(area);
        SmoothingMode sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush b = new SolidBrush(Color.FromArgb(36, P.Fg))) Util.FillRound(g, b, trk, trk.Width / 2);
        using (SolidBrush b = new SolidBrush(hot2 ? P.Acc : Color.FromArgb(120, P.Fg))) Util.FillRound(g, b, new Rectangle(trk.X, ty, trk.Width, th), trk.Width / 2);
        g.SmoothingMode = sm;
        Hit h = new Hit(); h.R = area; h.Hand = true;
        h.Click = delegate { };
        h.Drag = delegate (Point pt)
        {
            int want = (int)Math.Round((pt.Y - trk.Y - th / 2.0) * max / span);
            set(Math.Max(0, Math.Min(max, want)));
        };
        build.Add(h);
    }

    // багатолінійне поле введення зі смугою прокрутки в темі: власні поле й смуга, системна смуга Windows вимкнена
    readonly HashSet<TextBox> scrollHooked = new HashSet<TextBox>();

    int TbTotal(TextBox tb) { return Math.Max(1, (int)Win.SendMessage(tb.Handle, 0xBA, 0, 0)); }          // EM_GETLINECOUNT
    int TbFirst(TextBox tb) { return (int)Win.SendMessage(tb.Handle, 0xCE, 0, 0); }                       // EM_GETFIRSTVISIBLELINE

    int TbVisible(TextBox tb)
    {
        int lh;
        try { lh = Math.Max(1, tb.Font.Height); } catch { lh = Math.Max(1, F.Height); }
        return Math.Max(1, tb.ClientSize.Height / lh);
    }

    void TbScroll(TextBox tb, int d)
    {
        if (!tb.IsHandleCreated) return;
        int max = Math.Max(0, TbTotal(tb) - TbVisible(tb));
        int cur = TbFirst(tb);
        int to = Math.Max(0, Math.Min(max, cur + d));
        if (to != cur) Win.SendMessage(tb.Handle, 0xB6, 0, to - cur);                                     // EM_LINESCROLL
        Invalidate();
    }

    protected void MultiField(Graphics g, TextBox tb, Rectangle fr)
    {
        int rad = Math.Min(P.Rad, Px(10));
        using (SolidBrush b = new SolidBrush(P.Field)) Util.FillRound(g, b, fr, rad);
        if (tb.Focused)
            using (GraphicsPath gp = Util.Round(fr, rad))
            using (Pen pn = new Pen(Color.FromArgb(160, P.Acc), 1.2f)) g.DrawPath(pn, gp);
        Rectangle tr = Rectangle.Inflate(fr, -Px(10), -Px(6));
        tr.Width -= Px(10);                                         // праворуч - місце під смугу
        if (tb.ScrollBars != ScrollBars.None) tb.ScrollBars = ScrollBars.None;
        if (scrollHooked.Add(tb))
        {
            tb.MouseWheel += delegate (object o, MouseEventArgs me) { TbScroll(tb, me.Delta > 0 ? -3 : 3); };
            tb.KeyUp += delegate { Invalidate(); };
            tb.MouseUp += delegate { Invalidate(); };
            tb.TextChanged += delegate { Invalidate(); };
        }
        if (tb.Bounds != tr) tb.Bounds = tr;
        if (!tb.Visible) tb.Visible = true;
        try
        {
            if (tb.IsHandleCreated)
                VBar(g, new Rectangle(fr.Right - Px(11), fr.Y + Px(8), Px(5), fr.Height - Px(16)), TbTotal(tb), TbVisible(tb), TbFirst(tb),
                    delegate (int pos) { TbScroll(tb, pos - TbFirst(tb)); });
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
        Wheel(fr, delegate (int d) { TbScroll(tb, d > 0 ? -3 : 3); });        // коліщатко над полем без фокуса
    }

    protected void Wheel(Rectangle r, Action<int> wheel)
    {
        Hit h = new Hit(); h.R = r; h.Wheel = wheel; h.Hand = false; build.Add(h);
    }

    protected void Txt(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags fl)
    {
        TextRenderer.DrawText(g, s ?? "", f, r, c, fl | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping);
    }

    protected void TxtL(Graphics g, string s, Font f, Rectangle r, Color c)
    {
        Txt(g, s, f, r, c, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }

    protected void TxtR(Graphics g, string s, Font f, Rectangle r, Color c)
    {
        Txt(g, s, f, r, c, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }

    protected void TxtC(Graphics g, string s, Font f, Rectangle r, Color c)
    {
        Txt(g, s, f, r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }

    protected void TxtW(Graphics g, string s, Font f, Rectangle r, Color c)
    {
        Txt(g, s, f, r, c, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
    }

    protected void Card(Graphics g, Rectangle r)
    {
        Card(g, r, P.Card);
    }

    protected void Card(Graphics g, Rectangle r, Color fill)
    {
        if (T.Sketchy)
        {
            using (GraphicsPath gp = Sk.WobblyRound(r, P.Rad, new Random(r.X * 7 + r.Y), 1.2f))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(150, fill))) g.FillPath(b, gp);
                using (Pen pn = new Pen(Color.FromArgb(170, P.Fg), 1.2f)) g.DrawPath(pn, gp);
            }
            return;
        }
        using (SolidBrush b = new SolidBrush(fill)) Util.FillRound(g, b, r, P.Rad);
        if (T.Style == IconStyle.Neon)
            using (GraphicsPath gp = Util.Round(r, P.Rad))
            using (Pen pn = new Pen(Color.FromArgb(120, P.Acc), 1f)) g.DrawPath(pn, gp);
        else if (T.Style == IconStyle.Plate)
            using (GraphicsPath gp = Util.Round(Rectangle.Inflate(r, -Px(2), -Px(2)), Math.Max(2, P.Rad - Px(2))))
            using (Pen pn = new Pen(Color.FromArgb(90, P.Acc), 1f)) g.DrawPath(pn, gp);
    }

    // кнопка з текстом; style: 0 звичайна, 1 акцентна, 2 тиха (лише текст)
    protected void Btn(Graphics g, Rectangle r, string text, Action click, int style, bool enabled)
    {
        bool h = enabled && Over(r);
        Color fill = style == 1 ? Util.Blend(P.Bg, P.Acc, h ? 0.62f : 0.48f) : (style == 2 ? (h ? P.Card : Color.Empty) : Util.Blend(P.Card, P.Fg, h ? 0.12f : 0.05f));
        if (!fill.IsEmpty)
            using (SolidBrush b = new SolidBrush(enabled ? fill : Util.Blend(P.Bg, fill, 0.5f)))
                Util.FillRound(g, b, r, Math.Min(P.Rad, r.Height / 2));
        Color tc = !enabled ? Util.Blend(P.Bg, P.Fg, 0.35f) : (style == 1 ? Util.Readable(P.Fg, fill, 110) : P.Fg);
        TxtC(g, text, style == 1 ? FB : F, r, tc);
        if (enabled && click != null) Hot(r, click);
    }

    // кругла кнопка з піктограмою
    protected void IconBtn(Graphics g, Rectangle r, string icon, Action click, bool accent)
    {
        bool h = click != null && Over(r);
        if (accent || h)
            using (SolidBrush b = new SolidBrush(accent ? Util.Blend(P.Bg, P.Acc, h ? 0.62f : 0.48f) : P.Card))
                g.FillEllipse(b, r);
        Color c = accent ? Util.Readable(P.Fg, Util.Blend(P.Bg, P.Acc, 0.5f), 110) : (click == null ? Util.Blend(P.Bg, P.Sub, 0.45f) : (h ? P.Fg : P.Sub));
        float m = r.Width * 0.3f;
        Ico.Draw(g, icon, new RectangleF(r.X + m, r.Y + m, r.Width - m * 2, r.Height - m * 2), c);
        if (click != null) Hot(r, click);
    }

    // перемикач з підписом (уся смуга клікабельна)
    protected void Switch(Graphics g, Rectangle r, string label, bool on, Action click)
    {
        int sw = Px(36), sh = Px(20);
        Rectangle tr = new Rectangle(r.Right - sw, r.Y + (r.Height - sh) / 2, sw, sh);
        TxtL(g, label, F, new Rectangle(r.X, r.Y, r.Width - sw - Px(8), r.Height), P.Fg);
        using (SolidBrush b = new SolidBrush(on ? Util.Blend(P.Bg, P.Acc, 0.7f) : Util.Blend(P.Bg, P.Fg, 0.22f)))
            Util.FillRound(g, b, tr, sh / 2);
        int k = sh - Px(6);
        Rectangle kn = new Rectangle(on ? tr.Right - k - Px(3) : tr.X + Px(3), tr.Y + Px(3), k, k);
        using (SolidBrush b = new SolidBrush(on ? Color.White : Util.Blend(P.Bg, P.Fg, 0.7f))) g.FillEllipse(b, kn);
        if (click != null) Hot(r, click);
    }

    // «таблетка» вибору (як вкладка)
    // зона для перетягування файлів: пунктирна рамка, підпис, «або» й кнопка (висота ~112 px)
    protected void DropZone(Graphics g, Rectangle dz, string title, string button, Action click, bool enabled)
    {
        Card(g, dz);
        using (Pen pn = new Pen(Util.Blend(P.Card, P.Sub, 0.55f), 1.5f))
        using (GraphicsPath gp = Util.Round(Rectangle.Inflate(dz, -Px(7), -Px(7)), Px(10)))
        {
            pn.DashStyle = DashStyle.Dash;
            g.DrawPath(pn, gp);
        }
        int y0 = dz.Y + Math.Max(0, (dz.Height - Px(112)) / 2);        // висока зона - вміст посередині
        TxtC(g, title, FH, new Rectangle(dz.X, y0 + Px(16), dz.Width, Px(30)), P.Fg);
        TxtC(g, L.T("або"), FS, new Rectangle(dz.X, y0 + Px(46), dz.Width, Px(18)), P.Sub);
        int bw = Math.Max(Px(200), TextRenderer.MeasureText(button, F).Width + Px(32));
        Btn(g, new Rectangle(dz.X + (dz.Width - bw) / 2, y0 + Px(66), bw, Px(34)), button, click, 1, enabled);
    }

    protected void Chip(Graphics g, Rectangle r, string text, bool sel, Action click)
    {
        bool h = Over(r);
        Color fill = sel ? Util.Blend(P.Bg, P.Acc, 0.4f) : (h ? Util.Blend(P.Card, P.Fg, 0.1f) : P.Card);
        using (SolidBrush b = new SolidBrush(fill)) Util.FillRound(g, b, r, Math.Min(P.Rad, r.Height / 2));
        TxtC(g, text, sel ? FB : F, r, sel ? P.Fg : P.Sub);
        if (click != null) Hot(r, click);
    }

    // чип з піктограмою ліворуч від тексту
    protected void IconChip(Graphics g, Rectangle r, string icon, string text, bool sel, Action click)
    {
        Chip(g, r, "", sel, click);
        Color c = sel ? P.Fg : P.Sub;
        int isz = Px(14);
        int tw = TextRenderer.MeasureText(text, sel ? FB : F).Width;
        int x0 = r.X + (r.Width - isz - Px(4) - tw) / 2;
        Ico.Draw(g, icon, new RectangleF(x0, r.Y + (r.Height - isz) / 2f, isz, isz), c);
        TxtL(g, text, sel ? FB : F, new Rectangle(x0 + isz + Px(2), r.Y, tw + Px(6), r.Height), c);
    }

    protected void ProgressBar(Graphics g, Rectangle r, float frac, Color c)
    {
        frac = Math.Max(0f, Math.Min(1f, frac));
        using (SolidBrush b = new SolidBrush(Util.Blend(P.Bg, P.Fg, 0.14f))) Util.FillRound(g, b, r, r.Height / 2);
        int w = (int)(r.Width * frac);
        if (w <= 0) return;
        w = Math.Max(w, r.Height);
        using (SolidBrush b = new SolidBrush(c)) Util.FillRound(g, b, new Rectangle(r.X, r.Y, w, r.Height), r.Height / 2);
    }

    // графік історії (значення за порядком, найновіше в кінці)
    protected void Graph(Graphics g, Rectangle r, List<float> data, float max, Color c)
    {
        using (SolidBrush b = new SolidBrush(Util.Blend(P.Bg, P.Fg, 0.04f))) Util.FillRound(g, b, r, Math.Max(2, P.Rad / 2));
        using (Pen gp = new Pen(Util.Blend(P.Bg, P.Fg, 0.1f), 1f))
            for (int i = 1; i < 4; i++) g.DrawLine(gp, r.X, r.Y + r.Height * i / 4, r.Right, r.Y + r.Height * i / 4);
        if (data == null || data.Count < 2 || max <= 0) return;
        int n = data.Count;
        PointF[] pts = new PointF[n];
        for (int i = 0; i < n; i++)
        {
            float v = float.IsNaN(data[i]) ? 0f : Math.Max(0f, Math.Min(max, data[i]));
            pts[i] = new PointF(r.X + (float)i * (r.Width - 1) / (n - 1), r.Bottom - 1 - v / max * (r.Height - 2));
        }
        using (GraphicsPath fp = new GraphicsPath())
        {
            fp.AddLines(pts);
            fp.AddLine(pts[n - 1].X, pts[n - 1].Y, pts[n - 1].X, r.Bottom);
            fp.AddLine(pts[n - 1].X, r.Bottom, pts[0].X, r.Bottom);
            fp.CloseFigure();
            using (LinearGradientBrush lb = new LinearGradientBrush(new Rectangle(r.X, r.Y - 1, r.Width, r.Height + 2), Color.FromArgb(110, c), Color.FromArgb(10, c), 90f))
                g.FillPath(lb, fp);
        }
        using (Pen pn = new Pen(c, Math.Max(1.4f, Px(1.6f)))) { pn.LineJoin = LineJoin.Round; g.DrawLines(pn, pts); }
    }

    // поле для тексту в стилі теми (системний TextBox без рамки поверх намальованого поля)
    // вікно ще не показане (напр., створене з контекстного меню): фонові завдання повертають результат через
    // BeginInvoke, а він без дескриптора вікна кидає виняток - результат губився б («вічне обчислення»)
    public void EnsureHandle() { if (!IsHandleCreated && !IsDisposed) { IntPtr h = Handle; } }

    protected TextBox MakeInput()
    {
        TextBox tb = new TextBox();
        tb.BorderStyle = BorderStyle.None;
        tb.BackColor = P.Field;
        tb.ForeColor = P.Fg;
        tb.Font = F;
        tb.Visible = false;
        Controls.Add(tb);
        return tb;
    }

    protected void PlaceInput(Graphics g, TextBox tb, Rectangle r)
    {
        using (SolidBrush b = new SolidBrush(P.Field)) Util.FillRound(g, b, r, Math.Min(P.Rad, r.Height / 2));
        if (tb.Focused)
            using (GraphicsPath gp = Util.Round(r, Math.Min(P.Rad, r.Height / 2)))
            using (Pen pn = new Pen(Color.FromArgb(160, P.Acc), 1.2f)) g.DrawPath(pn, gp);
        int h = tb.PreferredHeight;
        Rectangle want = new Rectangle(r.X + Px(10), r.Y + (r.Height - h) / 2, r.Width - Px(20), h);
        if (tb.Bounds != want) tb.Bounds = want;
        if (!tb.Visible) tb.Visible = true;
    }

    // ---------- миша ----------

    int HitAt(Point p)
    {
        // лише клікабельні зони: області прокрутки коліщатком не перекривають кнопки
        for (int i = hits.Count - 1; i >= 0; i--)
            if ((hits[i].Click != null || hits[i].RClick != null) && hits[i].R.Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (moving)
        {
            Location = new Point(Location.X + e.X - moveFrom.X, Location.Y + e.Y - moveFrom.Y);
            return;
        }
        mouse = e.Location;
        if (kbMode) { kbMode = false; Invalidate(); }       // мишею - рамка клавіатури зникає
        if (dragging != null) { TipHide(); dragging(e.Location); Invalidate(); return; }
        TipMove();
        int h = HitAt(mouse);
        // кнопки - рука; порожнє місце шапки - курсор перетягування вікна
        Cursor = h >= 0 && hits[h].Cur != null ? hits[h].Cur : h >= 0 && hits[h].Hand && (hits[h].Click != null) ? Cursors.Hand : (h < 0 && mouse.Y < HeadH ? Cursors.SizeAll : Cursors.Default);
        if (h != hot) { hot = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        mouse = new Point(-1, -1);
        TipHide();
        if (hot != -1) { hot = -1; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        mouse = e.Location;
        TipHide();
        down = HitAt(e.Location);
        if (down >= 0 && e.Button == MouseButtons.Left && hits[down].Drag != null) { dragging = hits[down].Drag; dragging(e.Location); Capture = true; Invalidate(); return; }
        if (down < 0 && e.Button == MouseButtons.Left && e.Y < HeadH) { moving = true; moveFrom = e.Location; Capture = true; }
        else Focus();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (dragging != null) { dragging = null; Capture = false; OnDragEnd(); return; }
        if (moving) { moving = false; Capture = false; return; }
        int h = HitAt(e.Location);
        if (h < 0 || h != down) return;
        Hit hit = hits[h];
        if (e.Button == MouseButtons.Left && hit.Click != null) { hit.Click(); Invalidate(); }
        else if (e.Button == MouseButtons.Right && hit.RClick != null) { hit.RClick(); Invalidate(); }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        for (int i = hits.Count - 1; i >= 0; i--)
            if (hits[i].Wheel != null && hits[i].R.Contains(e.Location))
            {
                hits[i].Wheel(e.Delta > 0 ? 1 : -1);
                Invalidate();
                return;
            }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }

    // ---------- клавіатура: Tab / Shift+Tab - між кнопками, перемикачами, вкладками; Enter чи пробіл - натиснути ----------
    bool kbMode;
    int kbFocus = -1;

    List<int> Focusable(List<Hit> l)
    {
        List<int> r = new List<int>();
        Rectangle cl = ClientRectangle;
        for (int i = 0; i < l.Count; i++)
        {
            Hit h = l[i];
            if (h.Click == null || h.Drag != null || h.R.Width < 4 || h.R.Height < 4) continue;
            if (!cl.Contains(new Point(h.R.X + h.R.Width / 2, h.R.Y + h.R.Height / 2))) continue;
            r.Add(i);
        }
        return r;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys k = keyData & Keys.KeyCode;
        bool inText = ActiveControl is TextBoxBase;
        if (k == Keys.Tab && (keyData & (Keys.Control | Keys.Alt)) == 0)
        {
            List<int> fk = Focusable(hits);
            if (fk.Count > 0)
            {
                bool back = (keyData & Keys.Shift) != 0;
                if (!kbMode) kbFocus = back ? fk.Count - 1 : 0;
                else kbFocus = (kbFocus + (back ? -1 : 1) + fk.Count) % fk.Count;
                kbMode = true;
                if (inText) { ActiveControl = null; Focus(); }
                Invalidate();
                return true;
            }
        }
        if (kbMode && !inText && (k == Keys.Enter || k == Keys.Space))
        {
            List<int> fk = Focusable(hits);
            if (kbFocus >= 0 && kbFocus < fk.Count)
            {
                Hit h = hits[fk[kbFocus]];
                h.Click();
                Invalidate();
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // тематичне контекстне меню для вікна
    protected ContextMenuStrip ThemedMenu()
    {
        ContextMenuStrip m = new ContextMenuStrip();
        m.ForeColor = P.Fg;
        RoundMenu.Apply(m, P.Bg, P.Fg, P.Acc, Util.Blend(P.Bg, P.Fg, 0.22f), P.Rad);
        m.Closed += delegate { BeginInvoke(new Action(m.Dispose)); };
        return m;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            NeoBar.ThemeChanged -= OnThemeChanged;
            Ticker.Dispose();
            if (tipTimer != null) tipTimer.Dispose();
            if (tipBox != null) tipBox.Dispose();
            foreach (Font f in new Font[] { F, FB, FS, FH, FBig, FHuge }) if (f != null) f.Dispose();
        }
        base.Dispose(disposing);
    }
}
