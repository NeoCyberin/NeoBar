using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// ======================================================================
//  Святкові форми (гарбуз, ялинкова кулька, писанка, повітряна кулька) - спільні для піктограм
//  панелі (IconFactory) і значка профілю (ProfileArt), градієнтне тло святкових тем (Theme.Grad)
//  і прикраси у вільних місцях вікон, віджетів на столі й нотаток (Decor).
// ======================================================================
interface IGradHost { }

static class Festive
{
    // контур писанки: еліпс, ширший унизу й вужчий угорі
    public static GraphicsPath EggPath(RectangleF r)
    {
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height * 0.53f, rx = r.Width / 2f, ry = r.Height / 2f;
        PointF[] pts = new PointF[40];
        for (int i = 0; i < pts.Length; i++)
        {
            double a = i * 2 * Math.PI / pts.Length, sn = Math.Sin(a);
            pts[i] = new PointF(cx + (float)(rx * Math.Cos(a) * (0.9 + 0.12 * sn)), cy + (float)(ry * sn));
        }
        GraphicsPath gp = new GraphicsPath();
        gp.AddClosedCurve(pts, 0.4f);
        return gp;
    }

    // верх (або низ) еліпса в точці x; NaN - еліпс тут відсутній
    static float Ell(float x, float cx, float cy, float rx, float ry, bool top)
    {
        float u = (x - cx) / rx;
        if (u < -1.0005f || u > 1.0005f) return float.NaN;
        u = Math.Max(-1f, Math.Min(1f, u));
        float d = ry * (float)Math.Sqrt(Math.Max(0, 1 - u * u));
        return top ? cy - d : cy + d;
    }

    static float Pick(bool top, params float[] v)
    {
        float r = float.NaN;
        foreach (float f in v)
        {
            if (float.IsNaN(f)) continue;
            if (float.IsNaN(r) || (top ? f < r : f > r)) r = f;
        }
        return r;
    }

    // силует: об'єднання кількох частин (еліпсів тощо), обчислене як «профіль» зверху й знизу вздовж x
    static GraphicsPath Silhouette(float xa, float xb, Func<float, bool, float> edge)
    {
        int n = 72;
        List<PointF> top = new List<PointF>(), bot = new List<PointF>();
        for (int i = 0; i <= n; i++)
        {
            float x = xa + (xb - xa) * (1f - (float)Math.Cos(Math.PI * i / n)) / 2f;   // частіше на краях, де крива крута
            float t = edge(x, true), b = edge(x, false);
            if (float.IsNaN(t) || float.IsNaN(b)) continue;
            top.Add(new PointF(x, t));
            bot.Add(new PointF(x, b));
        }
        bot.Reverse();
        top.AddRange(bot);
        GraphicsPath gp = new GraphicsPath();
        gp.AddPolygon(top.ToArray());
        return gp;
    }

    // точний контур значка (разом із ніжкою гарбуза, ковпачком кульки, вузликом) у квадраті r:
    // за ним малюється кільце наведення на значку профілю
    public static GraphicsPath Outline(IconStyle st, RectangleF r)
    {
        float s = r.Width, x0 = r.X, y0 = r.Y;
        if (st == IconStyle.Egg) return EggPath(new RectangleF(x0 + s * 0.14f, y0 + s * 0.04f, s * 0.72f, s * 0.92f));
        if (st == IconStyle.Pumpkin)
        {
            // три долі + ніжка
            float bx = x0 + s * 0.06f, bw = s * 0.88f, by = y0 + s * 0.2f, bh = s * 0.74f, lw = bw * 0.56f;
            float c1 = bx + lw / 2f, c2 = bx + bw - lw / 2f, c3 = bx + bw * 0.22f + lw / 2f;
            float stemL = x0 + s * 0.45f, stemR = stemL + Math.Max(2f, s * 0.11f), stemTop = y0 + s * 0.05f;
            return Silhouette(bx, bx + bw, delegate (float x, bool top)
            {
                float v = Pick(top, Ell(x, c1, by + bh / 2f, lw / 2f, bh / 2f, top), Ell(x, c2, by + bh / 2f, lw / 2f, bh / 2f, top),
                               Ell(x, c3, by + bh / 2f - s * 0.005f, lw / 2f, bh / 2f + s * 0.005f, top));
                if (top && x >= stemL && x <= stemR) v = Pick(true, v, stemTop);
                return v;
            });
        }
        if (st == IconStyle.Balloon)
        {
            float bx = x0 + s * 0.12f, bw = s * 0.76f, by = y0 + s * 0.02f, bh = s * 0.82f, kx = bx + bw / 2f, ky = by + bh - s * 0.01f;
            return Silhouette(bx, bx + bw, delegate (float x, bool top)
            {
                float v = Ell(x, kx, by + bh / 2f, bw / 2f, bh / 2f, top);
                if (!top && Math.Abs(x - kx) <= s * 0.05f) v = Pick(false, v, ky + s * 0.06f);
                return v;
            });
        }
        {
            // ялинкова кулька з ковпачком і петелькою
            float bx = x0 + s * 0.08f, bw = s * 0.84f, by = y0 + s * 0.18f, bh = s * 0.8f, cx = bx + bw / 2f;
            return Silhouette(bx, bx + bw, delegate (float x, bool top)
            {
                float v = Ell(x, cx, by + bh / 2f, bw / 2f, bh / 2f, top);
                if (top)
                {
                    if (x >= x0 + s * 0.38f && x <= x0 + s * 0.62f) v = Pick(true, v, y0 + s * 0.1f);
                    v = Pick(true, v, Ell(x, x0 + s * 0.5f, y0 + s * 0.06f, s * 0.06f, s * 0.05f, true));
                }
                return v;
            });
        }
    }

    // кільце навколо значка: контур, рівномірно розширений на off з усіх боків
    public static GraphicsPath Ring(IconStyle st, RectangleF r, float off)
    {
        GraphicsPath gp = Outline(st, r);
        RectangleF b = gp.GetBounds();
        if (b.Width > 0 && b.Height > 0)
            using (Matrix m = new Matrix())
            {
                float cx = b.X + b.Width / 2f, cy = b.Y + b.Height / 2f;
                m.Translate(cx, cy);
                m.Scale((b.Width + 2 * off) / b.Width, (b.Height + 2 * off) / b.Height);
                m.Translate(-cx, -cy);
                gp.Transform(m);
            }
        return gp;
    }

    // Малює форму в квадраті r. Повертає прямокутник, по центру якого стоїть символ;
    // ink - колір символу, shadow - тінь під ним (Empty - без тіні), glow - сила світіння (0 - без)
    public static RectangleF Draw(Graphics g, IconStyle st, RectangleF r, Color tile, out Color ink, out Color shadow, out int glow)
    {
        float s = r.Width, x0 = r.X, y0 = r.Y;
        ink = Color.White;
        shadow = Color.FromArgb(110, 0, 0, 0);
        glow = 0;
        switch (st)
        {
            case IconStyle.Pumpkin:
            {
                RectangleF body = new RectangleF(x0 + s * 0.06f, y0 + s * 0.2f, s * 0.88f, s * 0.74f);
                Color dk = Util.Blend(tile, Color.Black, 0.3f);
                using (LinearGradientBrush pg = new LinearGradientBrush(new RectangleF(x0, y0 - 1, s, s + 2), Util.Blend(tile, Color.White, 0.15f), dk, 90f))
                {
                    // три «дольки», що трохи заходять одна на одну
                    g.FillEllipse(pg, body.X, body.Y, body.Width * 0.56f, body.Height);
                    g.FillEllipse(pg, body.Right - body.Width * 0.56f, body.Y, body.Width * 0.56f, body.Height);
                    g.FillEllipse(pg, body.X + body.Width * 0.22f, body.Y - s * 0.01f, body.Width * 0.56f, body.Height + s * 0.01f);
                }
                using (Pen rib = new Pen(Color.FromArgb(110, dk), Math.Max(1f, s * 0.03f)))
                {
                    g.DrawArc(rib, body.X + body.Width * 0.22f, body.Y, body.Width * 0.56f, body.Height, 100, 160);
                    g.DrawArc(rib, body.X + body.Width * 0.22f, body.Y, body.Width * 0.56f, body.Height, -80, 160);
                }
                using (SolidBrush stem = new SolidBrush(Color.FromArgb(70, 110, 40)))
                using (GraphicsPath sp = Util.Round(new RectangleF(x0 + s * 0.45f, y0 + s * 0.05f, Math.Max(2f, s * 0.11f), Math.Max(3f, s * 0.2f)), Math.Max(1f, s / 30f)))
                    g.FillPath(stem, sp);
                if (Util.Lum(tile) > 185) { ink = Color.FromArgb(78, 44, 12); shadow = Color.Empty; }     // світлий гарбуз - темний символ
                else
                {
                    ink = Color.FromArgb(255, 236, 140);
                    shadow = Color.FromArgb(150, 60, 20, 0);
                    glow = 70;
                }
                return new RectangleF(body.X, body.Y + body.Height * 0.02f, body.Width, body.Height);
            }

            // ялинкова кулька зі срібним ковпачком і відблиском
            case IconStyle.Ornament:
            {
                RectangleF ball = new RectangleF(x0 + s * 0.08f, y0 + s * 0.18f, s * 0.84f, s * 0.8f);
                using (GraphicsPath bp = new GraphicsPath())
                {
                    bp.AddEllipse(ball);
                    using (PathGradientBrush pgb = new PathGradientBrush(bp))
                    {
                        pgb.CenterPoint = new PointF(ball.X + ball.Width * 0.36f, ball.Y + ball.Height * 0.34f);
                        pgb.CenterColor = Util.Blend(tile, Color.White, 0.4f);
                        pgb.SurroundColors = new Color[] { Util.Blend(tile, Color.Black, 0.3f) };
                        g.FillPath(pgb, bp);
                    }
                }
                Color cap = Color.FromArgb(206, 214, 228);
                using (LinearGradientBrush cb = new LinearGradientBrush(new RectangleF(x0 + s * 0.38f, y0 + s * 0.1f, s * 0.24f, s * 0.11f), Color.White, Util.Blend(cap, Color.Black, 0.25f), 0f))
                    g.FillRectangle(cb, x0 + s * 0.38f, y0 + s * 0.1f, s * 0.24f, s * 0.11f);
                using (Pen lp = new Pen(cap, Math.Max(1f, s * 0.035f)))
                    g.DrawEllipse(lp, x0 + s * 0.44f, y0 + s * 0.01f, s * 0.12f, s * 0.1f);
                using (SolidBrush shine = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
                    g.FillEllipse(shine, ball.X + ball.Width * 0.18f, ball.Y + ball.Height * 0.12f, ball.Width * 0.22f, ball.Height * 0.16f);
                if (Util.Lum(tile) > 175) { ink = Color.FromArgb(26, 48, 96); shadow = Color.FromArgb(90, 255, 255, 255); }
                else shadow = Color.FromArgb(120, 0, 0, 0);
                return ball;
            }

            // писанка: яйце з зубчастими смугами
            case IconStyle.Egg:
            {
                RectangleF er = new RectangleF(x0 + s * 0.14f, y0 + s * 0.04f, s * 0.72f, s * 0.92f);
                using (GraphicsPath ep = EggPath(er))
                {
                    using (LinearGradientBrush eg = new LinearGradientBrush(new RectangleF(x0, y0 - 1, s, s + 2), Util.Blend(tile, Color.White, 0.3f), Util.Blend(tile, Color.Black, 0.12f), 90f))
                        g.FillPath(eg, ep);
                    GraphicsState gs = g.Save();
                    g.SetClip(ep, CombineMode.Intersect);
                    using (Pen zp = new Pen(Color.FromArgb(225, 255, 255, 255), Math.Max(1f, s * 0.035f)))
                    {
                        zp.LineJoin = LineJoin.Round;
                        foreach (float yy in new[] { 0.24f, 0.82f })
                        {
                            List<PointF> zz = new List<PointF>();
                            int n = 7;
                            for (int i = 0; i <= n; i++) zz.Add(new PointF(er.X + er.Width * i / n, er.Y + er.Height * yy + (i % 2 == 0 ? -s * 0.04f : s * 0.04f)));
                            g.DrawLines(zp, zz.ToArray());
                        }
                    }
                    g.Restore(gs);
                    using (Pen rim = new Pen(Util.Blend(tile, Color.Black, 0.25f), Math.Max(1f, s * 0.025f))) g.DrawPath(rim, ep);
                }
                ink = Util.Blend(tile, Color.Black, 0.62f);
                shadow = Color.Empty;
                return new RectangleF(er.X, er.Y + er.Height * 0.03f, er.Width, er.Height);
            }

            // повітряна кулька з вузликом і ниткою
            default:
            {
                RectangleF bl = new RectangleF(x0 + s * 0.12f, y0 + s * 0.02f, s * 0.76f, s * 0.82f);
                using (GraphicsPath bp = new GraphicsPath())
                {
                    bp.AddEllipse(bl);
                    using (PathGradientBrush pgb = new PathGradientBrush(bp))
                    {
                        pgb.CenterPoint = new PointF(bl.X + bl.Width * 0.38f, bl.Y + bl.Height * 0.32f);
                        pgb.CenterColor = Util.Blend(tile, Color.White, 0.3f);
                        pgb.SurroundColors = new Color[] { Util.Blend(tile, Color.Black, 0.18f) };
                        g.FillPath(pgb, bp);
                    }
                }
                float kx = bl.X + bl.Width / 2f, ky = bl.Bottom - s * 0.01f;
                using (SolidBrush kb = new SolidBrush(Util.Blend(tile, Color.Black, 0.2f)))
                    g.FillPolygon(kb, new PointF[] { new PointF(kx, ky - s * 0.02f), new PointF(kx - s * 0.05f, ky + s * 0.06f), new PointF(kx + s * 0.05f, ky + s * 0.06f) });
                using (Pen sp = new Pen(Color.FromArgb(170, 230, 230, 240), Math.Max(1f, s * 0.02f)))
                    g.DrawBezier(sp, kx, ky + s * 0.06f, kx - s * 0.06f, ky + s * 0.1f, kx + s * 0.05f, ky + s * 0.12f, kx - s * 0.01f, y0 + s * 0.99f);
                using (SolidBrush shine = new SolidBrush(Color.FromArgb(140, 255, 255, 255)))
                    g.FillEllipse(shine, bl.X + bl.Width * 0.2f, bl.Y + bl.Height * 0.12f, bl.Width * 0.2f, bl.Height * 0.18f);
                if (Util.Lum(tile) > 175) { ink = Util.Blend(tile, Color.Black, 0.65f); shadow = Color.Empty; }
                return bl;
            }
        }
    }

    // ---------- градієнтне тло святкових тем ----------

    // пензель градієнта теми, розтягнутого на area; angle - 90 уздовж висоти, 0 - уздовж ширини.
    // soft > 0 - кольори ближчі до суцільного тла теми; alpha - прозорість
    public static LinearGradientBrush Brush(Theme t, RectangleF area, float angle, float soft, int alpha)
    {
        RectangleF a = RectangleF.Inflate(area, 1, 1);      // GDI+ інакше кладе останній колір у перший рядок
        if (a.Width < 2) a.Width = 2;
        if (a.Height < 2) a.Height = 2;
        Color[] cs = new Color[t.Grad.Length];
        float[] ps = new float[t.Grad.Length];
        Color bg = Util.Opaque(t.Bg);
        int al = Math.Max(0, Math.Min(255, alpha));
        for (int i = 0; i < cs.Length; i++)
        {
            cs[i] = Color.FromArgb(al, soft > 0 ? Util.Blend(t.Grad[i], bg, soft) : t.Grad[i]);
            ps[i] = i / (float)(cs.Length - 1);
        }
        LinearGradientBrush lb = new LinearGradientBrush(a, cs[0], cs[cs.Length - 1], angle);
        ColorBlend cb = new ColorBlend(cs.Length);
        cb.Colors = cs;
        cb.Positions = ps;
        lb.InterpolationColors = cb;
        return lb;
    }

    // тло віджета: градієнт теми по діагоналі або суцільний колір
    public static Brush BgBrush(Theme t, Color solid, int alpha, RectangleF area)
    {
        if (Has(t)) return Brush(t, area, 45f, 0, alpha);
        return new SolidBrush(Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), solid));
    }

    // Вікно налаштувань у святкових темах: один градієнт на все вікно (від верху до низу), щоб навігація,
    // заголовок, сторінки й нижня смуга не різнились. Кожен елемент малює свою ділянку цього градієнта
    // за координатами вікна; контейнери, що так роблять, позначені IGradHost
    public static bool Backdrop(Graphics g, Control c, Rectangle clip)
    {
        Theme t = FormTheme.T;
        bool sk = FormTheme.Sketchy;
        if ((!Has(t) && !sk) || c == null) return false;
        try
        {
            Form f = c.FindForm();
            if (f == null || !f.IsHandleCreated || !c.IsHandleCreated) return false;
            Point o = c.PointToScreen(Point.Empty), fo = f.PointToScreen(Point.Empty);
            RectangleF area = new RectangleF(fo.X - o.X, fo.Y - o.Y, f.ClientSize.Width, f.ClientSize.Height);
            SmoothingMode sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            if (sk)
            {
                // артистична тема: папір із штрихами, тайл зсунуто так, щоб він був один на все вікно
                using (TextureBrush tb = new TextureBrush(Sk.Paper(FormTheme.Bg)))
                {
                    tb.TranslateTransform(fo.X - o.X, fo.Y - o.Y);
                    g.FillRectangle(tb, clip);
                }
            }
            else using (LinearGradientBrush lb = Brush(t, area, 90f, 0.3f, 255)) g.FillRectangle(lb, clip);
            g.SmoothingMode = sm;
            return true;
        }
        catch { return false; }
    }

    // елемент лежить просто на тлі вікна (а не на картці чи власному полі) - під ним той самий градієнт
    public static bool OnBackdrop(Control c)
    {
        if (!Has(FormTheme.T) && !FormTheme.Sketchy) return false;
        for (Control p = c.Parent; p != null; p = p.Parent)
        {
            if (p is IGradHost) return true;
            if (p.BackColor.A != 0 || p is SCard) return false;
        }
        return false;
    }

    // підписи на тлі вікна не повинні мати власної плями кольору: стають прозорими
    public static void Clear(Control c)
    {
        Label l = c as Label;
        if (l != null && OnBackdrop(l)) l.BackColor = Color.Transparent;
    }

    public static bool Has(Theme t) { return t != null && t.Grad != null && t.Grad.Length >= 2; }

    // прямокутна заливка (тло панелі, спливних вікон, шапки)
    public static void Fill(Graphics g, Theme t, RectangleF area, RectangleF fill, float angle, float soft)
    {
        if (!Has(t)) return;
        SmoothingMode sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        using (LinearGradientBrush lb = Brush(t, area, angle, soft, 255)) g.FillRectangle(lb, fill);
        g.SmoothingMode = sm;
    }

    // ---------- прикраси ----------
    // Малюються ПІД вмістом (одразу після тла), тож видно їх лише у вільних місцях: картки й поля їх закривають.
    // rich - велике вікно чи нотатка (більше прикрас); інакше - віджет на столі (лише куток).
    // Без випадковостей між кадрами: seed залежить від розміру, тож прикраси не «стрибають» під час перемальовування.

    public static bool HasDecor(string id) { return id == "halloween" || id == "xmas" || id == "easter" || id == "birthday"; }

    public static void Decor(Graphics g, Theme t, Rectangle r, float sc, bool rich)
    {
        if (t == null || !HasDecor(t.Id) || r.Width < 20 || r.Height < 20) return;
        SmoothingMode sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        GraphicsState st = g.Save();
        g.SetClip(r, CombineMode.Intersect);
        Random rnd = new Random(r.Width * 31 + r.Height * 17 + (rich ? 7 : 0));
        try
        {
            switch (t.Id)
            {
                case "halloween": Spooky(g, t, r, sc, rnd, rich); break;
                case "xmas": Winter(g, t, r, sc, rnd, rich); break;
                case "easter": Spring(g, t, r, sc, rnd, rich); break;
                case "birthday": Party(g, t, r, sc, rnd, rich); break;
            }
        }
        catch { }
        g.Restore(st);
        g.SmoothingMode = sm;
    }

    static Color Col(Theme t, int i) { return t.Palette != null && t.Palette.Length > 0 ? Util.Opaque(t.Palette[i % t.Palette.Length]) : Util.Opaque(t.Accent); }

    // Хелловін: павутина в нижньому лівому куті, кажани, гарбузи в правому куті
    static void Spooky(Graphics g, Theme t, Rectangle r, float sc, Random rnd, bool rich)
    {
        Color fg = Util.Opaque(t.Text);
        float R = Math.Min(Math.Min(r.Width, r.Height) * (rich ? 0.34f : 0.4f), 130 * sc);
        Web(g, r.X, r.Bottom, R, Color.FromArgb(rich ? 40 : 34, fg));
        int bats = rich ? 3 : 1;
        for (int i = 0; i < bats; i++)
        {
            float s = (rich ? 12 + rnd.Next(7) : 8) * sc;
            float x = r.X + r.Width * (0.35f + 0.5f * (float)rnd.NextDouble()), y = r.Y + r.Height * (rich ? 0.3f + 0.4f * (float)rnd.NextDouble() : 0.22f);
            Bat(g, x, y, s, Color.FromArgb(rich ? 60 : 50, Util.Opaque(t.Accent)));
        }
        float ps = (rich ? 24 : 15) * sc;
        Pumpkin(g, r.Right - ps * 1.25f, r.Bottom - ps * 0.95f, ps, Color.FromArgb(rich ? 90 : 70, Col(t, 0)));
        if (rich) { Pumpkin(g, r.Right - ps * 2.3f, r.Bottom - ps * 0.72f, ps * 0.7f, Color.FromArgb(80, Col(t, 2))); Scatter(g, t, r, sc, rnd, 96, 95); }
        else Sprite(g, t, 2, r.Right - 14 * sc, r.Y + 14 * sc, 12 * sc, 70);
    }

    public static void Web(Graphics g, float cx, float cy, float R, Color c)
    {
        // павутина з кута (cx, cy) вгору-праворуч
        using (Pen p = new Pen(c, 1f))
        {
            int spokes = 6;
            for (int i = 0; i <= spokes; i++)
            {
                double a = Math.PI / 2 * i / spokes;
                g.DrawLine(p, cx, cy, cx + (float)Math.Cos(a) * R, cy - (float)Math.Sin(a) * R);
            }
            for (int k = 1; k <= 4; k++)
            {
                float rr = R * k / 4.4f;
                PointF[] pts = new PointF[spokes + 1];
                for (int i = 0; i <= spokes; i++)
                {
                    double a = Math.PI / 2 * i / spokes;
                    float sag = i == 0 || i == spokes ? 1f : 0.93f;
                    pts[i] = new PointF(cx + (float)Math.Cos(a) * rr * sag, cy - (float)Math.Sin(a) * rr * sag);
                }
                g.DrawCurve(p, pts, 0.3f);
            }
        }
    }

    // силует кажана: тіло, голова з вухами й крила з «фестонами» знизу
    public static void Bat(Graphics g, float x, float y, float s, Color c)
    {
        float[] w = { 0.12f, -0.12f, 0.45f, -0.42f, 0.9f, -0.5f, 1.35f, -0.35f, 1.55f, -0.05f, 1.28f, 0.0f, 1.1f, 0.16f, 0.92f, 0.0f,
                      0.74f, 0.2f, 0.55f, 0.03f, 0.35f, 0.22f, 0.12f, 0.1f };
        int n = w.Length / 2;
        PointF[] rgt = new PointF[n], lft = new PointF[n];
        for (int i = 0; i < n; i++)
        {
            rgt[i] = new PointF(x + w[i * 2] * s, y + w[i * 2 + 1] * s);
            lft[i] = new PointF(x - w[i * 2] * s, y + w[i * 2 + 1] * s);
        }
        using (SolidBrush b = new SolidBrush(c))
        {
            g.FillPolygon(b, rgt);
            g.FillPolygon(b, lft);
            g.FillEllipse(b, x - s * 0.15f, y - s * 0.2f, s * 0.3f, s * 0.48f);
            g.FillEllipse(b, x - s * 0.12f, y - s * 0.34f, s * 0.24f, s * 0.22f);
            g.FillPolygon(b, new PointF[] { new PointF(x - s * 0.11f, y - s * 0.26f), new PointF(x - s * 0.08f, y - s * 0.46f), new PointF(x - s * 0.01f, y - s * 0.3f) });
            g.FillPolygon(b, new PointF[] { new PointF(x + s * 0.11f, y - s * 0.26f), new PointF(x + s * 0.08f, y - s * 0.46f), new PointF(x + s * 0.01f, y - s * 0.3f) });
        }
    }

    // напівпрозорий гарбуз (лівий верхній кут x, y; ширина s)
    public static void Pumpkin(Graphics g, float x, float y, float s, Color c)
    {
        float h = s * 0.8f;
        using (SolidBrush b = new SolidBrush(c))
        {
            g.FillEllipse(b, x, y, s * 0.58f, h);
            g.FillEllipse(b, x + s * 0.42f, y, s * 0.58f, h);
            g.FillEllipse(b, x + s * 0.2f, y - s * 0.02f, s * 0.6f, h + s * 0.02f);
        }
        using (SolidBrush b = new SolidBrush(Color.FromArgb(c.A, 90, 120, 50)))
            g.FillRectangle(b, x + s * 0.45f, y - s * 0.16f, s * 0.1f, s * 0.18f);
    }

    // Різдво: сніжинки й замет уздовж низу
    static void Winter(Graphics g, Theme t, Rectangle r, float sc, Random rnd, bool rich)
    {
        int n = rich ? 18 : 6;
        for (int i = 0; i < n; i++)
        {
            bool big = i < (rich ? 5 : 2);
            float s = big ? (rich ? 9 + rnd.Next(8) : 6 + rnd.Next(3)) * sc : (2 + rnd.Next(3)) * sc;
            float x = r.X + (float)rnd.NextDouble() * r.Width, y = r.Y + (float)rnd.NextDouble() * r.Height * 0.9f;
            Color c = Color.FromArgb(big ? 44 : 60, 255, 255, 255);
            if (!big) { using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, x, y, s, s); continue; }
            Flake(g, x, y, s, c, i);
        }
        // замет
        float dh = (rich ? 16 : 9) * sc;
        using (GraphicsPath gp = new GraphicsPath())
        {
            List<PointF> pts = new List<PointF>();
            int steps = Math.Max(4, (int)(r.Width / (40 * sc)));
            for (int i = 0; i <= steps; i++)
                pts.Add(new PointF(r.X + r.Width * i / (float)steps, r.Bottom - dh * (0.45f + 0.55f * (float)Math.Abs(Math.Sin(i * 1.3 + 0.5)))));
            gp.AddCurve(pts.ToArray(), 0.5f);
            gp.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
            gp.CloseFigure();
            using (SolidBrush b = new SolidBrush(Color.FromArgb(38, 255, 255, 255))) g.FillPath(b, gp);
        }
        if (rich) Scatter(g, t, r, sc, rnd, 96, 100);
        else Sprite(g, t, 2, r.Right - 14 * sc, r.Y + 14 * sc, 11 * sc, 80);
    }

    public static void Flake(Graphics g, float x, float y, float s, Color c, int seed)
    {
        using (Pen p = new Pen(c, Math.Max(1f, s * 0.12f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            for (int k = 0; k < 6; k++)
            {
                double a = k * Math.PI / 3 + seed;
                g.DrawLine(p, x, y, x + (float)Math.Cos(a) * s, y + (float)Math.Sin(a) * s);
                float mx = x + (float)Math.Cos(a) * s * 0.6f, my = y + (float)Math.Sin(a) * s * 0.6f;
                for (int sgn = -1; sgn <= 1; sgn += 2)
                {
                    double b2 = a + sgn * 0.6;
                    g.DrawLine(p, mx, my, mx + (float)Math.Cos(b2) * s * 0.3f, my + (float)Math.Sin(b2) * s * 0.3f);
                }
            }
    }

    // Великдень: травичка вздовж низу, писанки в правому куті, квіточки
    static void Spring(Graphics g, Theme t, Rectangle r, float sc, Random rnd, bool rich)
    {
        float gh = (rich ? 16 : 10) * sc;
        using (Pen p = new Pen(Color.FromArgb(rich ? 80 : 70, 120, 180, 100), Math.Max(1f, 1.6f * sc)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            for (float x = r.X + 2 * sc; x < r.Right; x += 5 * sc)
            {
                float h = gh * (0.5f + 0.5f * (float)rnd.NextDouble());
                g.DrawBezier(p, x, r.Bottom, x, r.Bottom - h * 0.5f, x + (float)(rnd.NextDouble() - 0.5) * 6 * sc, r.Bottom - h * 0.8f, x + (float)(rnd.NextDouble() - 0.5) * 8 * sc, r.Bottom - h);
            }
        int eggs = rich ? 3 : 2;
        float ex = r.Right - 8 * sc;
        for (int i = 0; i < eggs; i++)
        {
            float s = (rich ? 18 + rnd.Next(6) : 11 + rnd.Next(3)) * sc;
            ex -= s * 1.05f;
            GraphicsState st = g.Save();
            g.TranslateTransform(ex + s / 2f, r.Bottom - s * 0.62f - 2 * sc);
            g.RotateTransform(rnd.Next(-18, 18));
            using (GraphicsPath ep = EggPath(new RectangleF(-s / 2f, -s * 0.65f, s, s * 1.3f)))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(rich ? 130 : 110, Col(t, i)))) g.FillPath(b, ep);
                using (Pen zp = new Pen(Color.FromArgb(150, 255, 255, 255), Math.Max(1f, s * 0.06f)))
                {
                    PointF[] zz = new PointF[6];
                    for (int k = 0; k < zz.Length; k++) zz[k] = new PointF(-s / 2f + s * k / (zz.Length - 1), (k % 2 == 0 ? -1 : 1) * s * 0.06f);
                    g.SetClip(ep, CombineMode.Intersect);
                    g.DrawLines(zp, zz);
                }
            }
            g.Restore(st);
        }
        if (!rich) { Sprite(g, t, 1, r.Right - 14 * sc, r.Y + 14 * sc, 12 * sc, 90); return; }
        Scatter(g, t, r, sc, rnd, 96, 105);
        for (int i = 0; i < 8; i++)
        {
            float x = r.X + (float)rnd.NextDouble() * r.Width * 0.6f, y = r.Y + r.Height * (0.2f + 0.65f * (float)rnd.NextDouble()), d = 3.5f * sc;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(60, Col(t, i + 1))))
                for (int k = 0; k < 5; k++)
                {
                    double a = k * Math.PI * 2 / 5;
                    g.FillEllipse(b, x + (float)Math.Cos(a) * d - d / 2f, y + (float)Math.Sin(a) * d - d / 2f, d, d);
                }
            using (SolidBrush b = new SolidBrush(Color.FromArgb(80, 246, 208, 110))) g.FillEllipse(b, x - d * 0.35f, y - d * 0.35f, d * 0.7f, d * 0.7f);
        }
    }

    // День народження: конфеті й кульки в правому нижньому куті
    static void Party(Graphics g, Theme t, Rectangle r, float sc, Random rnd, bool rich)
    {
        int n = rich ? 34 : 10;
        for (int i = 0; i < n; i++)
        {
            float x = r.X + (float)rnd.NextDouble() * r.Width, y = r.Y + (float)rnd.NextDouble() * r.Height;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(rich ? 90 : 80, Col(t, i))))
            {
                if (i % 3 == 0) { float d = 4 * sc; g.FillEllipse(b, x, y, d, d); continue; }
                GraphicsState st = g.Save();
                g.TranslateTransform(x, y);
                g.RotateTransform(rnd.Next(180));
                g.FillRectangle(b, -4 * sc, -1.5f * sc, 8 * sc, 3 * sc);
                g.Restore(st);
            }
        }
        int balloons = rich ? 3 : 1;
        float bw = (rich ? 22 : 14) * sc;
        for (int i = 0; i < balloons; i++)
        {
            float bx = r.Right - bw * (1.4f + i * 0.9f), by = r.Bottom - bw * (2.6f - (i % 2) * 0.5f);
            Color c = Col(t, i * 2);
            using (Pen sp = new Pen(Color.FromArgb(90, Util.Opaque(t.Text)), Math.Max(1f, sc)))
                g.DrawBezier(sp, bx + bw / 2f, by + bw * 1.15f, bx + bw * 0.3f, by + bw * 1.6f, bx + bw * 0.8f, by + bw * 2.0f, r.Right - bw * 1.5f, r.Bottom + 2);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(rich ? 120 : 100, c))) g.FillEllipse(b, bx, by, bw, bw * 1.15f);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(90, 255, 255, 255))) g.FillEllipse(b, bx + bw * 0.2f, by + bw * 0.15f, bw * 0.22f, bw * 0.2f);
        }
        if (rich) Scatter(g, t, r, sc, rnd, 96, 105);
        else Sprite(g, t, 4, r.Right - 14 * sc, r.Y + 14 * sc, 11 * sc, 110);
    }

    // ---------- спрайти: додаткові шаблони, що розсипаються по вільних місцях ----------

    static readonly int[] kinds = { 7, 7, 7, 7 };       // скільки спрайтів у кожної теми (halloween, xmas, easter, birthday)

    // x, y - центр, s - розмір, a - прозорість
    static void Sprite(Graphics g, Theme t, int k, float x, float y, float s, int a)
    {
        switch (t.Id)
        {
            case "halloween": Spook(g, t, k % 7, x, y, s, a); break;
            case "xmas": Noel(g, t, k % 7, x, y, s, a); break;
            case "easter": Pasch(g, t, k % 7, x, y, s, a); break;
            default: Party(g, t, k % 7, x, y, s, a); break;
        }
    }

    // розкидає спрайти по прямокутнику: по одному в кожній клітинці сітки (з розкидом), тож без накладань
    static void Scatter(Graphics g, Theme t, Rectangle r, float sc, Random rnd, float cell, int a)
    {
        int n = t.Id == "halloween" ? 0 : t.Id == "xmas" ? 1 : t.Id == "easter" ? 2 : 3;
        float cw = cell * sc;
        int cols = Math.Max(1, (int)(r.Width / cw)), rows = Math.Max(1, (int)(r.Height / cw));
        float w = r.Width / (float)cols, h = r.Height / (float)rows;
        int last = -1;
        for (int j = 0; j < rows; j++)
            for (int i = 0; i < cols; i++)
            {
                if (rnd.NextDouble() < 0.28) continue;
                if (j == rows - 1 && rows > 1) continue;              // низ - у кутових прикрас
                int k = rnd.Next(kinds[n]);
                if (k == last) k = (k + 1) % kinds[n];
                last = k;
                float s = (17 + rnd.Next(10)) * sc;
                float x = r.X + i * w + w * (0.25f + 0.5f * (float)rnd.NextDouble()), y = r.Y + j * h + h * (0.25f + 0.5f * (float)rnd.NextDouble());
                Sprite(g, t, k, x, y, s, a);
            }
    }

    // один спрайт за номером (для кінців заголовків розділів тощо)
    public static void Mark(Graphics g, Theme t, int k, RectangleF box)
    {
        if (t == null || !HasDecor(t.Id)) return;
        SmoothingMode sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Sprite(g, t, k, box.X + box.Width / 2f, box.Y + box.Height / 2f, Math.Min(box.Width, box.Height), 150);
        g.SmoothingMode = sm;
    }

    static PointF[] StarPts(float cx, float cy, float R, float r0)
    {
        PointF[] p = new PointF[10];
        for (int i = 0; i < 10; i++)
        {
            double an = -Math.PI / 2 + i * Math.PI / 5;
            float rr = i % 2 == 0 ? R : r0;
            p[i] = new PointF(cx + (float)Math.Cos(an) * rr, cy + (float)Math.Sin(an) * rr);
        }
        return p;
    }

    static void Sparkle(Graphics g, float x, float y, float s, Color c)
    {
        using (SolidBrush b = new SolidBrush(c))
            g.FillPolygon(b, new PointF[] { new PointF(x, y - s / 2f), new PointF(x + s * 0.12f, y - s * 0.12f), new PointF(x + s / 2f, y), new PointF(x + s * 0.12f, y + s * 0.12f),
                                            new PointF(x, y + s / 2f), new PointF(x - s * 0.12f, y + s * 0.12f), new PointF(x - s / 2f, y), new PointF(x - s * 0.12f, y - s * 0.12f) });
    }

    static Color A(Color c, int a) { return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c.R, c.G, c.B); }

    // ---- Хелловін ----
    static void Spook(Graphics g, Theme t, int k, float x, float y, float s, int a)
    {
        switch (k)
        {
            case 0: Bat(g, x, y, s * 0.55f, A(Util.Opaque(t.Accent), a)); break;
            case 1:
            {
                // привид: куполом угору, хвилястий низ, очі
                float w = s * 0.8f, h = s * 1.0f, l = x - w / 2f, tp = y - h / 2f;
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddArc(l, tp, w, w, 180, 180);
                    gp.AddLine(l + w, tp + w / 2f, l + w, tp + h);
                    int n = 4;
                    for (int i = 0; i < n; i++)
                    {
                        float x1 = l + w - w * i / n, x2 = l + w - w * (i + 1) / n;
                        gp.AddBezier(x1, tp + h, (x1 + x2) / 2f, tp + h - (i % 2 == 0 ? -1 : 1) * s * 0.0f - s * 0.22f, (x1 + x2) / 2f, tp + h + s * 0.12f, x2, tp + h);
                    }
                    gp.CloseFigure();
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 240, 236, 250))) g.FillPath(b, gp);
                }
                using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Min(255, a + 80), 40, 24, 56)))
                {
                    g.FillEllipse(b, x - w * 0.28f, y - h * 0.18f, w * 0.18f, w * 0.24f);
                    g.FillEllipse(b, x + w * 0.1f, y - h * 0.18f, w * 0.18f, w * 0.24f);
                    g.FillEllipse(b, x - w * 0.1f, y + h * 0.06f, w * 0.2f, w * 0.16f);
                }
                break;
            }
            case 2:
            {
                // місяць-серп із зіркою
                float d = s * 0.9f;
                using (Region rg = new Region(new RectangleF(x - d / 2f, y - d / 2f, d, d)))
                using (GraphicsPath cp = new GraphicsPath())
                {
                    cp.AddEllipse(x - d / 2f, y - d / 2f, d, d);
                    using (Region c1 = new Region(cp))
                    using (GraphicsPath cut = new GraphicsPath())
                    {
                        cut.AddEllipse(x - d / 2f + d * 0.28f, y - d / 2f - d * 0.06f, d * 0.85f, d * 0.85f);
                        c1.Exclude(cut);
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 255, 226, 150))) g.FillRegion(b, c1);
                    }
                }
                Sparkle(g, x + d * 0.35f, y - d * 0.3f, d * 0.3f, Color.FromArgb(a, 255, 240, 190));
                break;
            }
            case 3:
            {
                // павучок на нитці
                float len = s * 0.9f;
                Color c = Color.FromArgb(a, Util.Opaque(t.Text));
                using (Pen p = new Pen(A(c, a / 2), 1f)) g.DrawLine(p, x, y - len, x, y);
                using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, x - s * 0.17f, y - s * 0.12f, s * 0.34f, s * 0.3f);
                using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, x - s * 0.1f, y - s * 0.26f, s * 0.2f, s * 0.18f);
                using (Pen p = new Pen(c, Math.Max(1f, s * 0.045f)))
                    for (int i = 0; i < 4; i++)
                        foreach (int sg in new[] { -1, 1 })
                        {
                            float oy = y - s * 0.04f + i * s * 0.07f;
                            g.DrawLine(p, x + sg * s * 0.14f, oy, x + sg * s * 0.34f, oy - s * 0.08f + (i % 2) * s * 0.02f);
                            g.DrawLine(p, x + sg * s * 0.34f, oy - s * 0.08f + (i % 2) * s * 0.02f, x + sg * s * 0.42f, oy + s * 0.12f);
                        }
                break;
            }
            case 4:
            {
                // цукерка в обгортці
                float w = s * 0.55f, h = s * 0.34f;
                Color c = Color.FromArgb(a, Col(t, 3));
                using (SolidBrush b = new SolidBrush(c))
                {
                    g.FillEllipse(b, x - w / 2f, y - h / 2f, w, h);
                    g.FillPolygon(b, new PointF[] { new PointF(x - w / 2f + 1, y), new PointF(x - w * 0.95f, y - h * 0.7f), new PointF(x - w * 0.95f, y + h * 0.7f) });
                    g.FillPolygon(b, new PointF[] { new PointF(x + w / 2f - 1, y), new PointF(x + w * 0.95f, y - h * 0.7f), new PointF(x + w * 0.95f, y + h * 0.7f) });
                }
                using (Pen p = new Pen(Color.FromArgb(a, 255, 160, 40), Math.Max(1f, s * 0.08f))) { g.DrawLine(p, x - w * 0.15f, y - h / 2f + 1, x - w * 0.15f, y + h / 2f - 1); g.DrawLine(p, x + w * 0.15f, y - h / 2f + 1, x + w * 0.15f, y + h / 2f - 1); }
                break;
            }
            case 5: Pumpkin(g, x - s * 0.45f, y - s * 0.3f, s * 0.9f, Color.FromArgb(a, Col(t, 0))); break;
            default: Sparkle(g, x, y, s * 0.7f, Color.FromArgb(a, 255, 190, 90)); break;
        }
    }

    // ---- Різдво ----
    static void Noel(Graphics g, Theme t, int k, float x, float y, float s, int a)
    {
        switch (k)
        {
            case 0: Flake(g, x, y, s * 0.5f, Color.FromArgb(a, 255, 255, 255), (int)(x + y)); break;
            case 1:
            {
                // ялинка з трьох ярусів і зіркою
                Color c = Color.FromArgb(a, 70, 170, 120);
                using (SolidBrush b = new SolidBrush(c))
                {
                    for (int i = 0; i < 3; i++)
                    {
                        float top = y - s * 0.5f + i * s * 0.26f, hw = s * (0.26f + i * 0.12f);
                        g.FillPolygon(b, new PointF[] { new PointF(x, top), new PointF(x + hw, top + s * 0.34f), new PointF(x - hw, top + s * 0.34f) });
                    }
                    g.FillRectangle(b, x - s * 0.05f, y + s * 0.5f - s * 0.12f, s * 0.1f, s * 0.12f);
                }
                using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Min(255, a + 60), 255, 224, 120))) g.FillPolygon(b, StarPts(x, y - s * 0.52f, s * 0.12f, s * 0.05f));
                break;
            }
            case 2:
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 255, 224, 120))) g.FillPolygon(b, StarPts(x, y, s * 0.45f, s * 0.19f));
                break;
            case 3:
            {
                // подарунок із бантом
                float w = s * 0.7f;
                Color box = Color.FromArgb(a, Col(t, 3)), rib = Color.FromArgb(a, 236, 242, 255);
                using (SolidBrush b = new SolidBrush(box)) g.FillRectangle(b, x - w / 2f, y - w * 0.3f, w, w * 0.8f);
                using (SolidBrush b = new SolidBrush(rib))
                {
                    g.FillRectangle(b, x - w * 0.07f, y - w * 0.3f, w * 0.14f, w * 0.8f);
                    g.FillRectangle(b, x - w / 2f, y + w * 0.08f, w, w * 0.12f);
                    g.FillEllipse(b, x - w * 0.3f, y - w * 0.52f, w * 0.3f, w * 0.24f);
                    g.FillEllipse(b, x, y - w * 0.52f, w * 0.3f, w * 0.24f);
                }
                break;
            }
            case 4:
            {
                // льодяник-тростинка
                float lw = Math.Max(2f, s * 0.12f);
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddLine(x - s * 0.08f, y + s * 0.5f, x - s * 0.08f, y - s * 0.2f);
                    gp.AddArc(x - s * 0.08f, y - s * 0.5f, s * 0.36f, s * 0.6f, 180, 180);
                    using (Pen p = new Pen(Color.FromArgb(a, 244, 248, 255), lw)) g.DrawPath(p, gp);
                    using (Pen p = new Pen(Color.FromArgb(a, 90, 140, 235), lw) { DashPattern = new float[] { 1.2f, 1.6f } }) g.DrawPath(p, gp);
                }
                break;
            }
            case 5:
            {
                // кулька на нитці
                float d = s * 0.5f;
                using (Pen p = new Pen(Color.FromArgb(a / 2, 240, 244, 255), 1f)) g.DrawLine(p, x, y - s * 0.55f, x, y - d / 2f);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, Col(t, 0)))) g.FillEllipse(b, x - d / 2f, y - d / 2f, d, d);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 200, 210, 228))) g.FillRectangle(b, x - d * 0.14f, y - d / 2f - d * 0.12f, d * 0.28f, d * 0.14f);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 255, 255, 255))) g.FillEllipse(b, x - d * 0.28f, y - d * 0.3f, d * 0.2f, d * 0.16f);
                break;
            }
            default: Sparkle(g, x, y, s * 0.7f, Color.FromArgb(a, 220, 236, 255)); break;
        }
    }

    // ---- Великдень ----
    static void Pasch(Graphics g, Theme t, int k, float x, float y, float s, int a)
    {
        switch (k)
        {
            case 0:
            {
                GraphicsState st = g.Save();
                g.TranslateTransform(x, y);
                g.RotateTransform(((int)(x * 3 + y) % 40) - 20);
                using (GraphicsPath ep = EggPath(new RectangleF(-s * 0.32f, -s * 0.5f, s * 0.64f, s * 0.9f)))
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(a, Col(t, (int)x % 5)))) g.FillPath(b, ep);
                    g.SetClip(ep, CombineMode.Intersect);
                    using (Pen zp = new Pen(Color.FromArgb(Math.Min(255, a + 60), 255, 255, 255), Math.Max(1f, s * 0.06f)))
                    {
                        PointF[] zz = new PointF[7];
                        for (int i = 0; i < 7; i++) zz[i] = new PointF(-s * 0.32f + s * 0.64f * i / 6f, s * 0.05f + (i % 2 == 0 ? -1 : 1) * s * 0.05f);
                        g.DrawLines(zp, zz);
                    }
                }
                g.Restore(st);
                break;
            }
            case 1:
            {
                // квітка
                Color c = Color.FromArgb(a, Col(t, 0 + (int)y % 4));
                using (SolidBrush b = new SolidBrush(c))
                    for (int i = 0; i < 5; i++)
                    {
                        double an = i * Math.PI * 2 / 5 - Math.PI / 2;
                        g.FillEllipse(b, x + (float)Math.Cos(an) * s * 0.22f - s * 0.17f, y + (float)Math.Sin(an) * s * 0.22f - s * 0.17f, s * 0.34f, s * 0.34f);
                    }
                using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Min(255, a + 40), 250, 210, 90))) g.FillEllipse(b, x - s * 0.12f, y - s * 0.12f, s * 0.24f, s * 0.24f);
                break;
            }
            case 2:
            {
                // метелик
                Color c1 = Color.FromArgb(a, Col(t, 3)), c2 = Color.FromArgb(a, Col(t, 4));
                using (SolidBrush b = new SolidBrush(c1)) { g.FillEllipse(b, x - s * 0.5f, y - s * 0.38f, s * 0.48f, s * 0.46f); g.FillEllipse(b, x + s * 0.02f, y - s * 0.38f, s * 0.48f, s * 0.46f); }
                using (SolidBrush b = new SolidBrush(c2)) { g.FillEllipse(b, x - s * 0.4f, y + s * 0.02f, s * 0.38f, s * 0.36f); g.FillEllipse(b, x + s * 0.02f, y + s * 0.02f, s * 0.38f, s * 0.36f); }
                using (Pen p = new Pen(Color.FromArgb(Math.Min(255, a + 60), Util.Opaque(t.Text)), Math.Max(1f, s * 0.07f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(p, x, y - s * 0.28f, x, y + s * 0.3f);
                    g.DrawLine(p, x, y - s * 0.28f, x - s * 0.16f, y - s * 0.46f);
                    g.DrawLine(p, x, y - s * 0.28f, x + s * 0.16f, y - s * 0.46f);
                }
                break;
            }
            case 3:
            {
                // зайчик (голова з вухами)
                Color c = Color.FromArgb(a, 250, 246, 252);
                using (SolidBrush b = new SolidBrush(c))
                {
                    g.FillEllipse(b, x - s * 0.3f, y - s * 0.52f, s * 0.2f, s * 0.58f);
                    g.FillEllipse(b, x + s * 0.1f, y - s * 0.52f, s * 0.2f, s * 0.58f);
                    g.FillEllipse(b, x - s * 0.34f, y - s * 0.1f, s * 0.68f, s * 0.56f);
                }
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 246, 164, 178))) { g.FillEllipse(b, x - s * 0.24f, y - s * 0.4f, s * 0.08f, s * 0.38f); g.FillEllipse(b, x + s * 0.16f, y - s * 0.4f, s * 0.08f, s * 0.38f); g.FillEllipse(b, x - s * 0.06f, y + s * 0.18f, s * 0.12f, s * 0.08f); }
                using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Min(255, a + 60), 70, 56, 84))) { g.FillEllipse(b, x - s * 0.17f, y + s * 0.06f, s * 0.07f, s * 0.07f); g.FillEllipse(b, x + s * 0.1f, y + s * 0.06f, s * 0.07f, s * 0.07f); }
                break;
            }
            case 4:
            {
                // курча
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 252, 218, 90))) { g.FillEllipse(b, x - s * 0.34f, y - s * 0.12f, s * 0.68f, s * 0.56f); g.FillEllipse(b, x - s * 0.22f, y - s * 0.5f, s * 0.44f, s * 0.44f); }
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 246, 150, 60))) g.FillPolygon(b, new PointF[] { new PointF(x + s * 0.2f, y - s * 0.3f), new PointF(x + s * 0.4f, y - s * 0.24f), new PointF(x + s * 0.2f, y - s * 0.18f) });
                using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Min(255, a + 80), 60, 50, 70))) g.FillEllipse(b, x - s * 0.04f, y - s * 0.34f, s * 0.07f, s * 0.07f);
                break;
            }
            case 5:
            {
                // тюльпан
                using (Pen p = new Pen(Color.FromArgb(a, 110, 176, 96), Math.Max(1.4f, s * 0.08f))) g.DrawLine(p, x, y + s * 0.5f, x, y + s * 0.05f);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, Col(t, 0))))
                    g.FillPolygon(b, new PointF[] { new PointF(x - s * 0.26f, y - s * 0.3f), new PointF(x - s * 0.12f, y - s * 0.05f), new PointF(x, y - s * 0.34f), new PointF(x + s * 0.12f, y - s * 0.05f), new PointF(x + s * 0.26f, y - s * 0.3f), new PointF(x + s * 0.22f, y + s * 0.1f), new PointF(x, y + s * 0.18f), new PointF(x - s * 0.22f, y + s * 0.1f) });
                break;
            }
            default: Sparkle(g, x, y, s * 0.7f, Color.FromArgb(a, 255, 214, 120)); break;
        }
    }

    // ---- День народження ----
    static void Party(Graphics g, Theme t, int k, float x, float y, float s, int a)
    {
        switch (k)
        {
            case 0:
            {
                float w = s * 0.55f, h = s * 0.64f;
                using (Pen p = new Pen(Color.FromArgb(a / 2 + 20, Util.Opaque(t.Text)), 1f)) g.DrawBezier(p, x, y + h / 2f, x - w * 0.2f, y + h * 0.8f, x + w * 0.2f, y + h * 1.0f, x, y + h * 1.3f);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, Col(t, (int)x % 6)))) g.FillEllipse(b, x - w / 2f, y - h / 2f, w, h);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a / 2 + 20, 255, 255, 255))) g.FillEllipse(b, x - w * 0.3f, y - h * 0.34f, w * 0.2f, h * 0.18f);
                break;
            }
            case 1:
            {
                float w = s * 0.7f;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, Col(t, 2)))) g.FillRectangle(b, x - w / 2f, y - w * 0.25f, w, w * 0.75f);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 255, 214, 72)))
                {
                    g.FillRectangle(b, x - w * 0.07f, y - w * 0.25f, w * 0.14f, w * 0.75f);
                    g.FillEllipse(b, x - w * 0.3f, y - w * 0.48f, w * 0.3f, w * 0.24f);
                    g.FillEllipse(b, x, y - w * 0.48f, w * 0.3f, w * 0.24f);
                }
                break;
            }
            case 2:
            {
                // святковий ковпак із смужками й помпоном
                PointF[] tri = { new PointF(x, y - s * 0.5f), new PointF(x + s * 0.34f, y + s * 0.42f), new PointF(x - s * 0.34f, y + s * 0.42f) };
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, Col(t, 5))))
                {
                    g.FillPolygon(b, tri);
                }
                using (Pen p = new Pen(Color.FromArgb(a, 255, 214, 72), Math.Max(1.5f, s * 0.08f)))
                {
                    g.DrawLine(p, x - s * 0.17f, y - s * 0.04f, x + s * 0.2f, y + s * 0.12f);
                    g.DrawLine(p, x - s * 0.26f, y + s * 0.2f, x + s * 0.28f, y + s * 0.36f);
                }
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, Col(t, 0)))) g.FillEllipse(b, x - s * 0.1f, y - s * 0.6f, s * 0.2f, s * 0.2f);
                break;
            }
            case 3:
            {
                // капкейк
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 250, 176, 20)))
                    g.FillPolygon(b, new PointF[] { new PointF(x - s * 0.28f, y), new PointF(x + s * 0.28f, y), new PointF(x + s * 0.2f, y + s * 0.42f), new PointF(x - s * 0.2f, y + s * 0.42f) });
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 255, 160, 200)))
                {
                    g.FillEllipse(b, x - s * 0.34f, y - s * 0.24f, s * 0.68f, s * 0.34f);
                    g.FillEllipse(b, x - s * 0.22f, y - s * 0.4f, s * 0.44f, s * 0.3f);
                }
                using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Min(255, a + 40), 226, 48, 80))) g.FillEllipse(b, x - s * 0.07f, y - s * 0.52f, s * 0.14f, s * 0.14f);
                break;
            }
            case 4:
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 250, 190, 40))) g.FillPolygon(b, StarPts(x, y, s * 0.45f, s * 0.19f));
                break;
            case 5:
            {
                // серпантин
                using (Pen p = new Pen(Color.FromArgb(a, Col(t, 2)), Math.Max(1.4f, s * 0.09f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    PointF[] pts = new PointF[9];
                    for (int i = 0; i < 9; i++) pts[i] = new PointF(x - s * 0.5f + s * i / 8f, y + (float)Math.Sin(i * 1.1 + x) * s * 0.18f);
                    g.DrawCurve(p, pts);
                }
                break;
            }
            default: Sparkle(g, x, y, s * 0.7f, Color.FromArgb(a, Col(t, (int)y % 6))); break;
        }
    }
}
