using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// ======================================================================
//  Будівельні блоки вікна налаштувань: бічна навігація й сегментовані вкладки з анімацією «серфінгу»,
//  сторінки з власною тонкою прокруткою (лише коли вміст не вміщується), картки з рядками
//  (назва й пояснення ліворуч, елемент керування праворуч), списки без системних смуг прокрутки.
//  Кольори, заокруглення, шрифт заголовків і особливості теми (неон, латунь, олівець) - з FormTheme.
//  Тут - спільне (SUi, піктограми G, анімація Surf, смуга ThinBar); навігація - SetNav.cs,
//  картки й сторінки - SetCard.cs, списки - SetLists.cs, елементи керування - SetCtl.cs.
// ======================================================================
static class SUi
{
    public static float Sc = 1f;
    public static int S(float v) { return (int)Math.Round(v * Sc); }
    // заокруглення за темою (урбаністична - майже прямі кути)
    public static int R(int extra) { return FormTheme.Square ? S(3) : S(Math.Min(FormTheme.Rad + extra, 10)); }

    static Font fBase, fSmall, fSemi, fHead, fPage, fNav;
    public static Font Base { get { return fBase ?? (fBase = new Font("Segoe UI", 9f)); } }
    public static Font Small { get { return fSmall ?? (fSmall = new Font("Segoe UI", 8.25f)); } }
    public static Font Semi { get { return fSemi ?? (fSemi = Util.MakeFont("Segoe UI Semibold|Segoe UI", 9f, FontStyle.Regular)); } }
    public static Font Nav { get { return fNav ?? (fNav = new Font("Segoe UI", 9.75f)); } }
    public static Font Head { get { return fHead ?? (fHead = FormTheme.HeadFont(10.5f)); } }
    public static Font Page { get { return fPage ?? (fPage = FormTheme.HeadFont(17f)); } }

    // шрифти заголовків залежать від теми: після зміни теми створюються наново
    public static void Init(float sc)
    {
        Sc = Math.Max(1f, sc);
        foreach (Font f in new Font[] { fHead, fPage }) if (f != null) try { f.Dispose(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        fHead = null; fPage = null;
    }

    public static int TextH(string s, Font f, int w)
    {
        if (string.IsNullOrEmpty(s) || w <= 0) return 0;
        return TextRenderer.MeasureText(s, f, new Size(w, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl).Height;
    }

    public static void Text(Graphics g, string s, Font f, Rectangle r, Color c)
    {
        TextRenderer.DrawText(g, s, f, r, c, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl | TextFormatFlags.Left | TextFormatFlags.Top);
    }

    public static void Line1(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags extra)
    {
        TextRenderer.DrawText(g, s, f, r, c, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | extra);
    }

    // тло під елементом: найближчий непрозорий батько
    public static Color BgOf(Control c)
    {
        Control p = c.Parent;
        while (p != null)
        {
            if (p is SCard) return FormTheme.Card;          // картка завжди малює колір теми, а не те, що було в BackColor під час створення
            if (p.BackColor.A == 255) break;
            p = p.Parent;
        }
        return p != null ? p.BackColor : FormTheme.Bg;
    }
}

// ----------------------------------------------------------------------
//  Векторні піктограми вікна налаштувань (однакова товщина ліній, без шрифтів)
// ----------------------------------------------------------------------
static class G
{
    public static void Draw(Graphics g, string key, RectangleF r, Color c)
    {
        SmoothingMode sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Math.Min(r.Width, r.Height), cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        float x0 = cx - s / 2f, y0 = cy - s / 2f;
        using (Pen p = new Pen(c, Math.Max(1.3f, s * 0.09f)))
        using (SolidBrush b = new SolidBrush(c))
        {
            p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
            switch (key)
            {
                case "items":       // плитки 2×2
                {
                    float q = s * 0.36f, gp = s * 0.12f, o = (s - 2 * q - gp) / 2f;
                    for (int i = 0; i < 4; i++)
                    {
                        RectangleF t = new RectangleF(x0 + o + (i % 2) * (q + gp), y0 + o + (i / 2) * (q + gp), q, q);
                        using (GraphicsPath gpth = Util.Round(t, q * 0.28f)) g.DrawPath(p, gpth);
                    }
                    break;
                }
                case "panel":       // вікно з бічною смугою
                {
                    RectangleF t = new RectangleF(x0 + s * 0.08f, y0 + s * 0.16f, s * 0.84f, s * 0.68f);
                    using (GraphicsPath gpth = Util.Round(t, s * 0.1f)) g.DrawPath(p, gpth);
                    g.FillRectangle(b, t.X + s * 0.08f, t.Y + s * 0.1f, s * 0.14f, t.Height - s * 0.2f);
                    break;
                }
                case "view":        // палітра з крапками фарби
                {
                    g.DrawArc(p, x0 + s * 0.08f, y0 + s * 0.1f, s * 0.84f, s * 0.8f, 110, 290);
                    g.DrawLine(p, cx - s * 0.13f, cy + s * 0.38f, cx + s * 0.02f, cy + s * 0.18f);
                    float d = s * 0.13f;
                    g.FillEllipse(b, cx - s * 0.24f, cy - s * 0.22f, d, d);
                    g.FillEllipse(b, cx + s * 0.02f, cy - s * 0.3f, d, d);
                    g.FillEllipse(b, cx + s * 0.2f, cy - s * 0.04f, d, d);
                    break;
                }
                case "wall":        // картина: рамка, гори й сонце
                {
                    RectangleF t = new RectangleF(x0 + s * 0.06f, y0 + s * 0.14f, s * 0.88f, s * 0.72f);
                    using (GraphicsPath gpth = Util.Round(t, s * 0.1f)) g.DrawPath(p, gpth);
                    g.DrawLines(p, new PointF[] { new PointF(t.X + s * 0.06f, t.Bottom - s * 0.1f), new PointF(x0 + s * 0.36f, y0 + s * 0.48f), new PointF(x0 + s * 0.54f, y0 + s * 0.66f),
                        new PointF(x0 + s * 0.68f, y0 + s * 0.54f), new PointF(t.Right - s * 0.06f, t.Bottom - s * 0.12f) });
                    g.FillEllipse(b, x0 + s * 0.62f, y0 + s * 0.26f, s * 0.14f, s * 0.14f);
                    break;
                }
                case "search":
                    g.DrawEllipse(p, x0 + s * 0.12f, y0 + s * 0.12f, s * 0.54f, s * 0.54f);
                    g.DrawLine(p, x0 + s * 0.6f, y0 + s * 0.6f, x0 + s * 0.88f, y0 + s * 0.88f);
                    break;
                case "game":        // геймпад
                {
                    RectangleF t = new RectangleF(x0 + s * 0.04f, y0 + s * 0.24f, s * 0.92f, s * 0.52f);
                    using (GraphicsPath gpth = Util.Round(t, s * 0.24f)) g.DrawPath(p, gpth);
                    float lx = x0 + s * 0.3f, ly = cy, a = s * 0.11f;
                    g.DrawLine(p, lx - a, ly, lx + a, ly);
                    g.DrawLine(p, lx, ly - a, lx, ly + a);
                    float d = s * 0.1f;
                    g.FillEllipse(b, x0 + s * 0.64f, cy - s * 0.12f, d, d);
                    g.FillEllipse(b, x0 + s * 0.74f, cy + s * 0.02f, d, d);
                    break;
                }
                case "place":       // мітка на карті
                {
                    using (GraphicsPath gpth = new GraphicsPath())
                    {
                        float w = s * 0.62f;
                        gpth.AddArc(cx - w / 2f, y0 + s * 0.06f, w, w, 150, 240);
                        gpth.AddLine(cx + w / 2f * 0.87f, y0 + s * 0.06f + w * 0.75f, cx, y0 + s * 0.94f);
                        gpth.CloseFigure();
                        g.DrawPath(p, gpth);
                    }
                    g.DrawEllipse(p, cx - s * 0.1f, y0 + s * 0.27f, s * 0.2f, s * 0.2f);
                    break;
                }
                case "diag":        // пульс у колі
                    g.DrawEllipse(p, x0 + s * 0.06f, y0 + s * 0.06f, s * 0.88f, s * 0.88f);
                    g.DrawLines(p, new PointF[] { new PointF(x0 + s * 0.2f, cy), new PointF(x0 + s * 0.38f, cy), new PointF(x0 + s * 0.47f, cy - s * 0.2f),
                        new PointF(x0 + s * 0.56f, cy + s * 0.2f), new PointF(x0 + s * 0.64f, cy), new PointF(x0 + s * 0.8f, cy) });
                    break;
                case "data":        // база даних
                {
                    float w = s * 0.7f, h = s * 0.2f, x = cx - w / 2f;
                    g.DrawEllipse(p, x, y0 + s * 0.1f, w, h);
                    g.DrawLine(p, x, y0 + s * 0.2f, x, y0 + s * 0.8f);
                    g.DrawLine(p, x + w, y0 + s * 0.2f, x + w, y0 + s * 0.8f);
                    g.DrawArc(p, x, y0 + s * 0.4f, w, h, 0, 180);
                    g.DrawArc(p, x, y0 + s * 0.7f, w, h, 0, 180);
                    break;
                }
                case "plus":
                    g.DrawLine(p, cx - s * 0.32f, cy, cx + s * 0.32f, cy);
                    g.DrawLine(p, cx, cy - s * 0.32f, cx, cy + s * 0.32f);
                    break;
                case "minus":
                    g.DrawLine(p, cx - s * 0.32f, cy, cx + s * 0.32f, cy);
                    break;
                case "close":
                    g.DrawLine(p, cx - s * 0.26f, cy - s * 0.26f, cx + s * 0.26f, cy + s * 0.26f);
                    g.DrawLine(p, cx + s * 0.26f, cy - s * 0.26f, cx - s * 0.26f, cy + s * 0.26f);
                    break;
                case "up":
                    g.DrawLines(p, new PointF[] { new PointF(cx - s * 0.3f, cy + s * 0.14f), new PointF(cx, cy - s * 0.16f), new PointF(cx + s * 0.3f, cy + s * 0.14f) });
                    break;
                case "down":
                    g.DrawLines(p, new PointF[] { new PointF(cx - s * 0.3f, cy - s * 0.14f), new PointF(cx, cy + s * 0.16f), new PointF(cx + s * 0.3f, cy - s * 0.14f) });
                    break;
                case "check":
                    g.DrawLines(p, new PointF[] { new PointF(cx - s * 0.3f, cy + s * 0.02f), new PointF(cx - s * 0.08f, cy + s * 0.24f), new PointF(cx + s * 0.32f, cy - s * 0.22f) });
                    break;
                case "trash":
                    g.DrawLine(p, x0 + s * 0.16f, y0 + s * 0.24f, x0 + s * 0.84f, y0 + s * 0.24f);
                    g.DrawLine(p, cx - s * 0.12f, y0 + s * 0.12f, cx + s * 0.12f, y0 + s * 0.12f);
                    g.DrawLines(p, new PointF[] { new PointF(x0 + s * 0.26f, y0 + s * 0.3f), new PointF(x0 + s * 0.32f, y0 + s * 0.88f), new PointF(x0 + s * 0.68f, y0 + s * 0.88f), new PointF(x0 + s * 0.74f, y0 + s * 0.3f) });
                    g.DrawLine(p, cx, y0 + s * 0.42f, cx, y0 + s * 0.74f);
                    break;
                case "file":        // програма: вікно з рядком заголовка
                {
                    RectangleF t = new RectangleF(x0 + s * 0.1f, y0 + s * 0.16f, s * 0.8f, s * 0.68f);
                    using (GraphicsPath gpth = Util.Round(t, s * 0.1f)) g.DrawPath(p, gpth);
                    g.DrawLine(p, t.X, t.Y + s * 0.2f, t.Right, t.Y + s * 0.2f);
                    break;
                }
                case "folder":
                    g.DrawLines(p, new PointF[] { new PointF(x0 + s * 0.08f, y0 + s * 0.8f), new PointF(x0 + s * 0.08f, y0 + s * 0.2f), new PointF(x0 + s * 0.38f, y0 + s * 0.2f),
                        new PointF(x0 + s * 0.48f, y0 + s * 0.32f), new PointF(x0 + s * 0.92f, y0 + s * 0.32f), new PointF(x0 + s * 0.92f, y0 + s * 0.8f), new PointF(x0 + s * 0.08f, y0 + s * 0.8f) });
                    break;
                case "globe":
                    g.DrawEllipse(p, x0 + s * 0.08f, y0 + s * 0.08f, s * 0.84f, s * 0.84f);
                    g.DrawEllipse(p, cx - s * 0.18f, y0 + s * 0.08f, s * 0.36f, s * 0.84f);
                    g.DrawLine(p, x0 + s * 0.1f, cy, x0 + s * 0.9f, cy);
                    break;
                case "group":       // заголовок групи з рядками
                    g.DrawLine(p, x0 + s * 0.12f, y0 + s * 0.22f, x0 + s * 0.88f, y0 + s * 0.22f);
                    g.DrawLine(p, x0 + s * 0.3f, cy, x0 + s * 0.88f, cy);
                    g.DrawLine(p, x0 + s * 0.3f, y0 + s * 0.78f, x0 + s * 0.88f, y0 + s * 0.78f);
                    g.FillEllipse(b, x0 + s * 0.1f, cy - s * 0.06f, s * 0.12f, s * 0.12f);
                    g.FillEllipse(b, x0 + s * 0.1f, y0 + s * 0.72f, s * 0.12f, s * 0.12f);
                    break;
                case "shortcut":    // стрілка з рамки
                    g.DrawLines(p, new PointF[] { new PointF(x0 + s * 0.5f, y0 + s * 0.14f), new PointF(x0 + s * 0.14f, y0 + s * 0.14f), new PointF(x0 + s * 0.14f, y0 + s * 0.86f),
                        new PointF(x0 + s * 0.86f, y0 + s * 0.86f), new PointF(x0 + s * 0.86f, y0 + s * 0.5f) });
                    g.DrawLine(p, x0 + s * 0.44f, y0 + s * 0.56f, x0 + s * 0.86f, y0 + s * 0.14f);
                    g.DrawLines(p, new PointF[] { new PointF(x0 + s * 0.62f, y0 + s * 0.14f), new PointF(x0 + s * 0.86f, y0 + s * 0.14f), new PointF(x0 + s * 0.86f, y0 + s * 0.38f) });
                    break;
                case "system":      // монітор
                {
                    RectangleF t = new RectangleF(x0 + s * 0.08f, y0 + s * 0.14f, s * 0.84f, s * 0.54f);
                    using (GraphicsPath gpth = Util.Round(t, s * 0.08f)) g.DrawPath(p, gpth);
                    g.DrawLine(p, cx, t.Bottom, cx, y0 + s * 0.84f);
                    g.DrawLine(p, cx - s * 0.2f, y0 + s * 0.86f, cx + s * 0.2f, y0 + s * 0.86f);
                    break;
                }
                case "sep":
                    g.DrawLine(p, x0 + s * 0.1f, cy, x0 + s * 0.9f, cy);
                    g.DrawLine(p, cx - s * 0.12f, cy - s * 0.24f, cx + s * 0.12f, cy - s * 0.24f);
                    g.DrawLine(p, cx - s * 0.12f, cy + s * 0.24f, cx + s * 0.12f, cy + s * 0.24f);
                    break;
                case "refresh":
                    g.DrawArc(p, x0 + s * 0.14f, y0 + s * 0.14f, s * 0.72f, s * 0.72f, -60, 290);
                    g.DrawLines(p, new PointF[] { new PointF(x0 + s * 0.88f, y0 + s * 0.12f), new PointF(x0 + s * 0.84f, y0 + s * 0.36f), new PointF(x0 + s * 0.6f, y0 + s * 0.3f) });
                    break;
                case "export":
                case "import":
                {
                    g.DrawLines(p, new PointF[] { new PointF(x0 + s * 0.14f, y0 + s * 0.6f), new PointF(x0 + s * 0.14f, y0 + s * 0.86f), new PointF(x0 + s * 0.86f, y0 + s * 0.86f), new PointF(x0 + s * 0.86f, y0 + s * 0.6f) });
                    g.DrawLine(p, cx, y0 + s * 0.1f, cx, y0 + s * 0.64f);
                    bool upw = key == "export";
                    float ay = upw ? y0 + s * 0.1f : y0 + s * 0.64f, d = upw ? 1f : -1f;
                    g.DrawLines(p, new PointF[] { new PointF(cx - s * 0.2f, ay + d * s * 0.2f), new PointF(cx, ay), new PointF(cx + s * 0.2f, ay + d * s * 0.2f) });
                    break;
                }
                case "reset":
                    g.DrawArc(p, x0 + s * 0.14f, y0 + s * 0.14f, s * 0.72f, s * 0.72f, 200, 290);
                    g.DrawLines(p, new PointF[] { new PointF(x0 + s * 0.1f, y0 + s * 0.16f), new PointF(x0 + s * 0.14f, y0 + s * 0.42f), new PointF(x0 + s * 0.4f, y0 + s * 0.38f) });
                    break;
                case "locate":      // приціл
                    g.DrawEllipse(p, x0 + s * 0.2f, y0 + s * 0.2f, s * 0.6f, s * 0.6f);
                    g.FillEllipse(b, cx - s * 0.09f, cy - s * 0.09f, s * 0.18f, s * 0.18f);
                    g.DrawLine(p, cx, y0 + s * 0.04f, cx, y0 + s * 0.2f); g.DrawLine(p, cx, y0 + s * 0.8f, cx, y0 + s * 0.96f);
                    g.DrawLine(p, x0 + s * 0.04f, cy, x0 + s * 0.2f, cy); g.DrawLine(p, x0 + s * 0.8f, cy, x0 + s * 0.96f, cy);
                    break;
                case "palette":     // крапля кольору
                {
                    using (GraphicsPath gpth = new GraphicsPath())
                    {
                        gpth.AddArc(cx - s * 0.3f, cy - s * 0.06f, s * 0.6f, s * 0.6f, -30, 240);
                        gpth.AddLine(cx - s * 0.26f, cy + s * 0.09f, cx, cy - s * 0.42f);
                        gpth.CloseFigure();
                        g.DrawPath(p, gpth);
                    }
                    break;
                }
                case "open":        // відкрити у провіднику: папка зі стрілкою
                    g.DrawLines(p, new PointF[] { new PointF(x0 + s * 0.08f, y0 + s * 0.8f), new PointF(x0 + s * 0.08f, y0 + s * 0.2f), new PointF(x0 + s * 0.36f, y0 + s * 0.2f),
                        new PointF(x0 + s * 0.46f, y0 + s * 0.32f), new PointF(x0 + s * 0.92f, y0 + s * 0.32f), new PointF(x0 + s * 0.92f, y0 + s * 0.8f), new PointF(x0 + s * 0.08f, y0 + s * 0.8f) });
                    g.DrawLine(p, x0 + s * 0.4f, y0 + s * 0.6f, x0 + s * 0.62f, y0 + s * 0.6f);
                    g.DrawLines(p, new PointF[] { new PointF(x0 + s * 0.54f, y0 + s * 0.52f), new PointF(x0 + s * 0.62f, y0 + s * 0.6f), new PointF(x0 + s * 0.54f, y0 + s * 0.68f) });
                    break;
                case "grip":        // тягнути рядок
                {
                    float d = Math.Max(2f, s * 0.13f);
                    for (int i = 0; i < 3; i++)
                        for (int j = 0; j < 2; j++)
                            g.FillEllipse(b, cx - s * 0.16f + j * s * 0.24f, y0 + s * 0.2f + i * s * 0.26f, d, d);
                    break;
                }
                case "chart":       // графік
                    g.DrawLines(p, new PointF[] { new PointF(x0 + s * 0.1f, y0 + s * 0.74f), new PointF(x0 + s * 0.36f, y0 + s * 0.44f), new PointF(x0 + s * 0.56f, y0 + s * 0.6f), new PointF(x0 + s * 0.9f, y0 + s * 0.22f) });
                    g.DrawLine(p, x0 + s * 0.1f, y0 + s * 0.9f, x0 + s * 0.9f, y0 + s * 0.9f);
                    break;
                case "user":
                    g.DrawEllipse(p, cx - s * 0.18f, cy - s * 0.4f, s * 0.36f, s * 0.36f);
                    g.DrawArc(p, cx - s * 0.36f, cy + s * 0.04f, s * 0.72f, s * 0.7f, 200, 140);
                    break;
                case "info":
                    g.DrawEllipse(p, x0 + s * 0.08f, y0 + s * 0.08f, s * 0.84f, s * 0.84f);
                    g.DrawLine(p, cx, cy - s * 0.04f, cx, cy + s * 0.24f);
                    g.FillEllipse(b, cx - s * 0.06f, cy - s * 0.28f, s * 0.12f, s * 0.12f);
                    break;
            }
        }
        g.SmoothingMode = sm;
    }

}

// ----------------------------------------------------------------------
//  Анімація «серфінгу»: два краї підсвітки рухаються з різною швидкістю - передній край
//  вирушає першим, задній наздоганяє, тож підсвітка витягується й «пливе» до нової вкладки
// ----------------------------------------------------------------------
sealed class Surf : IDisposable
{
    public float A, B;
    float ta, tb;
    bool init;
    readonly Timer t = new Timer();
    public event Action Tick;
    public bool Running { get { return t.Enabled; } }

    public Surf()
    {
        t.Interval = 15;
        t.Tick += delegate { Step(); };
    }

    public void Go(float a, float b, bool animate)
    {
        ta = a; tb = b;
        if (!init || !animate)
        {
            A = a; B = b; init = true;
            t.Stop();
            Action k = Tick; if (k != null) k();
            return;
        }
        if (!t.Enabled) t.Start();
    }

    void Step()
    {
        bool fwd = ta >= A;
        const float lead = 0.36f, trail = 0.16f;
        A += (ta - A) * (fwd ? trail : lead);
        B += (tb - B) * (fwd ? lead : trail);
        if (Math.Abs(ta - A) < 0.6f && Math.Abs(tb - B) < 0.6f) { A = ta; B = tb; t.Stop(); }
        Action k = Tick; if (k != null) k();
    }

    public void Dispose() { t.Stop(); t.Dispose(); }
}

// ----------------------------------------------------------------------
//  Тонка смуга прокрутки: видно лише коли вміст довший за вікно; під мишею ширшає
// ----------------------------------------------------------------------
sealed class ThinBar
{
    public int View, Content, Pos;
    public bool Hot, Dragging;
    int grab;

    public bool Need { get { return Content > View + 1; } }
    public int Max { get { return Math.Max(0, Content - View); } }
    public int Clamp(int v) { return Math.Max(0, Math.Min(Max, v)); }
    public static int W { get { return SUi.S(12); } }

    public Rectangle Track(Rectangle area) { return new Rectangle(area.Right - W, area.Y + SUi.S(4), W, Math.Max(0, area.Height - SUi.S(8))); }

    public Rectangle Thumb(Rectangle area)
    {
        if (!Need) return Rectangle.Empty;
        Rectangle t = Track(area);
        int th = Math.Max(SUi.S(32), (int)((long)t.Height * View / Math.Max(1, Content)));
        th = Math.Min(th, t.Height);
        int y = t.Y + (int)((long)(t.Height - th) * Pos / Math.Max(1, Max));
        return new Rectangle(t.X, y, t.Width, th);
    }

    public void Paint(Graphics g, Rectangle area)
    {
        if (!Need) return;
        Rectangle th = Thumb(area);
        bool big = Hot || Dragging;
        int w = big ? SUi.S(6) : SUi.S(3);
        Rectangle r = new Rectangle(th.Right - SUi.S(3) - w, th.Y, w, th.Height);
        Color c = Util.Blend(FormTheme.Bg, FormTheme.Fg, big ? 0.5f : 0.3f);
        if (FormTheme.Neon && big) c = FormTheme.Acc;
        SmoothingMode sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush b = new SolidBrush(c)) Util.FillRound(g, b, r, w / 2);
        g.SmoothingMode = sm;
    }

    // натиснення: true - на смузі (далі тягнути повзунок або сторінка вгору/вниз)
    public bool Down(Point p, Rectangle area, out int newPos)
    {
        newPos = Pos;
        if (!Need || !Track(area).Contains(p)) return false;
        Rectangle th = Thumb(area);
        if (th.Contains(p)) { Dragging = true; grab = p.Y - th.Y; return true; }
        newPos = Clamp(p.Y < th.Y ? Pos - View * 9 / 10 : Pos + View * 9 / 10);
        return true;
    }

    // рух миші: true - треба перемалювати; newPos - нове положення під час перетягування
    public bool Move(Point p, Rectangle area, out int newPos)
    {
        newPos = Pos;
        bool hot = Need && Track(area).Contains(p);
        bool ch = hot != Hot;
        Hot = hot;
        if (!Dragging) return ch;
        Rectangle t = Track(area), th = Thumb(area);
        int range = t.Height - th.Height;
        if (range > 0) newPos = Clamp((int)((long)(p.Y - grab - t.Y) * Max / range));
        return true;
    }

    public void Up() { Dragging = false; }

    // ---------- спільна обробка миші для елемента зі смугою ----------
    // scrollTo - як елемент застосовує нове положення прокрутки

    // натиснення лівою: true - натиснуто на смузі (сторінка вгору/вниз або початок перетягування)
    public bool MouseDown(Control owner, MouseEventArgs e, Rectangle area, Action<int> scrollTo)
    {
        int np;
        if (e.Button != MouseButtons.Left || !Down(e.Location, area, out np)) return false;
        if (!Dragging) scrollTo(np);
        owner.Capture = true;
        owner.Invalidate();
        return true;
    }

    // рух миші: true - повзунок тягнуть (решту обробки руху пропустити)
    public bool MouseMove(Control owner, MouseEventArgs e, Rectangle area, Action<int> scrollTo)
    {
        int np;
        if (!Move(e.Location, area, out np)) return false;
        if (Dragging) scrollTo(np);
        owner.Invalidate();
        return Dragging;
    }

    public void MouseUp(Control owner)
    {
        if (!Dragging) return;
        Up();
        owner.Capture = false;
        owner.Invalidate();
    }
}
