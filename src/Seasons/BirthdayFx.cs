using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// ======================================================================
//  Святковий ефект на робочому столі, коли панель вітає з днем народження:
//  салюти, конфеті й подарунки, що падають і стрибають унизу екрана, + фанфари.
//  Вікно прозоре для кліків і без фокуса, живе ~10 с, потім зникає. Сцена (BirthdayScene) окремо від
//  вікна: вона лише лічить і малює, тож її можна перевірити без екрана.
// ======================================================================
interface IFxScene
{
    float Total { get; }
    float FadeFrom { get; }
    float Settle { get; }          // після цього кадр уже не змінюється (>= FadeFrom - рухається до самого кінця)
    void Update(float t);
    void Draw(Graphics g, float t);
}

sealed class BirthdayScene : IFxScene
{
    public float Total { get { return 10.6f; } }
    public float FadeFrom { get { return 9.6f; } }
    public float Settle { get { return 7.8f; } }

    sealed class Rocket { public float t0, dur, x0, y0, x1, y1; public Color c; public int kind; public bool burst; }
    sealed class Part { public float x, y, vx, vy, age, life, size, g; public Color c; public bool star; }
    sealed class Conf { public float t0, x, vy, sway, phase, rot, rotV, w, h; public Color c; }
    sealed class Gift { public float t0, x, size; public int look; public bool popped; }

    static readonly Color[] Fire = {
        Color.FromArgb(255, 214, 70), Color.FromArgb(255, 96, 160), Color.FromArgb(80, 212, 255), Color.FromArgb(124, 232, 120),
        Color.FromArgb(255, 150, 50), Color.FromArgb(184, 124, 255), Color.FromArgb(255, 255, 255), Color.FromArgb(255, 90, 90) };
    // коробка, кришка, стрічка
    static readonly Color[][] Looks = {
        new[] { Color.FromArgb(230, 70, 110), Color.FromArgb(250, 104, 144), Color.FromArgb(255, 232, 120) },
        new[] { Color.FromArgb(70, 150, 240), Color.FromArgb(104, 182, 255), Color.FromArgb(255, 255, 255) },
        new[] { Color.FromArgb(70, 190, 110), Color.FromArgb(110, 222, 146), Color.FromArgb(255, 92, 112) },
        new[] { Color.FromArgb(250, 170, 50), Color.FromArgb(255, 198, 96), Color.FromArgb(124, 72, 200) },
        new[] { Color.FromArgb(160, 100, 230), Color.FromArgb(188, 134, 252), Color.FromArgb(255, 222, 92) },
        new[] { Color.FromArgb(240, 90, 70), Color.FromArgb(255, 124, 104), Color.FromArgb(255, 255, 255) } };

    readonly int h;
    readonly float sc;
    readonly Random rnd;
    readonly List<Rocket> rockets = new List<Rocket>();
    readonly List<Part> parts = new List<Part>();
    readonly List<Conf> conf = new List<Conf>();
    readonly List<Gift> gifts = new List<Gift>();
    float last;
    const float Fall = 0.7f;

    public BirthdayScene(int w, int h, float sc, int seed)
    {
        this.h = h; this.sc = Math.Max(0.75f, sc);
        rnd = new Random(seed);
        // салюти: кожен злітає знизу й розривається нагорі
        int n = 9;
        for (int i = 0; i < n; i++)
        {
            Rocket r = new Rocket();
            r.t0 = 0.15f + i * 0.46f + (float)rnd.NextDouble() * 0.15f;
            r.dur = 0.7f + (float)rnd.NextDouble() * 0.25f;
            r.x1 = w * (0.12f + 0.76f * ((i * 0.37f + (float)rnd.NextDouble() * 0.2f) % 1f));
            r.y1 = h * (0.1f + 0.38f * (float)rnd.NextDouble());
            r.x0 = r.x1 + ((float)rnd.NextDouble() - 0.5f) * 140 * this.sc;
            r.y0 = h;
            r.c = Fire[rnd.Next(Fire.Length)];
            r.kind = i % 4;
            rockets.Add(r);
        }
        // конфеті
        for (int i = 0; i < 110; i++)
        {
            Conf c = new Conf();
            c.t0 = (float)rnd.NextDouble() * 3f;
            c.x = (float)rnd.NextDouble() * w;
            c.vy = (300f + (float)rnd.NextDouble() * 180f) * this.sc;
            c.sway = (20f + (float)rnd.NextDouble() * 50f) * this.sc;
            c.phase = (float)rnd.NextDouble() * 6.28f;
            c.rot = (float)rnd.NextDouble() * 6.28f;
            c.rotV = ((float)rnd.NextDouble() - 0.5f) * 10f;
            c.w = (6f + (float)rnd.NextDouble() * 6f) * this.sc;
            c.h = c.w * (0.45f + (float)rnd.NextDouble() * 0.3f);
            c.c = Fire[rnd.Next(Fire.Length - 2)];
            conf.Add(c);
        }
        // подарунки: рядком унизу, падають по черзі
        int g = 6, shift = rnd.Next(Looks.Length);      // усі шість подарунків різних кольорів
        for (int i = 0; i < g; i++)
        {
            Gift gf = new Gift();
            gf.t0 = 0.5f + i * 0.5f + (float)rnd.NextDouble() * 0.12f;
            gf.size = (62f + (float)rnd.NextDouble() * 22f) * this.sc;
            gf.x = w * (i + 0.5f) / g + ((float)rnd.NextDouble() - 0.5f) * w / g * 0.4f;
            gf.look = (i + shift) % Looks.Length;
            gifts.Add(gf);
        }
    }

    void Burst(Rocket r)
    {
        int n = r.kind == 3 ? 48 : 64;
        for (int i = 0; i < n; i++)
        {
            Part p = new Part();
            double a = rnd.NextDouble() * Math.PI * 2;
            float sp;
            if (r.kind == 1) { a = Math.PI * 2 * i / n; sp = 300f; }                      // кільце
            else if (r.kind == 2) sp = (i % 2 == 0 ? 160f : 330f);                          // подвійна куля
            else if (r.kind == 3) sp = 120f + (float)rnd.NextDouble() * 140f;               // «верба»: повільні, довгі
            else sp = 120f + (float)rnd.NextDouble() * 240f;
            sp *= sc;
            p.x = r.x1; p.y = r.y1;
            p.vx = (float)Math.Cos(a) * sp; p.vy = (float)Math.Sin(a) * sp;
            p.life = r.kind == 3 ? 2.1f + (float)rnd.NextDouble() * 0.4f : 1.3f + (float)rnd.NextDouble() * 0.6f;
            p.g = (r.kind == 3 ? 150f : 190f) * sc;
            p.size = (r.kind == 3 ? 3.2f : 3.8f) * sc;
            p.c = r.kind == 2 && i % 2 == 1 ? Fire[(Array.IndexOf(Fire, r.c) + 3) % Fire.Length] : r.c;
            parts.Add(p);
        }
        // яскравий спалах у центрі
        Part f = new Part();
        f.x = r.x1; f.y = r.y1; f.life = 0.22f; f.size = 26f * sc; f.c = Color.White; f.star = true;
        parts.Add(f);
    }

    void Pop(Gift g, float ground)
    {
        for (int i = 0; i < 12; i++)
        {
            Part p = new Part();
            double a = -Math.PI * (0.1 + 0.8 * rnd.NextDouble());
            float sp = (140f + (float)rnd.NextDouble() * 200f) * sc;
            p.x = g.x; p.y = ground - g.size * 0.6f;
            p.vx = (float)Math.Cos(a) * sp; p.vy = (float)Math.Sin(a) * sp;
            p.life = 0.7f + (float)rnd.NextDouble() * 0.5f; p.g = 420f * sc; p.size = (6f + (float)rnd.NextDouble() * 5f) * sc;
            p.c = Looks[g.look][i % 3 == 0 ? 2 : 1]; p.star = true;
            parts.Add(p);
        }
    }

    float Ground { get { return h - 6f * sc; } }

    public void Update(float t)
    {
        float dt = Math.Max(0, Math.Min(0.1f, t - last));
        last = t;
        foreach (Rocket r in rockets)
            if (!r.burst && t >= r.t0 + r.dur) { r.burst = true; Burst(r); }
        foreach (Gift g in gifts)
            if (!g.popped && t >= g.t0 + Fall) { g.popped = true; Pop(g, Ground); }
        float drag = (float)Math.Pow(0.2, dt);       // повітря гальмує іскри
        for (int i = parts.Count - 1; i >= 0; i--)
        {
            Part p = parts[i];
            p.age += dt;
            if (p.age >= p.life) { parts.RemoveAt(i); continue; }
            p.vx *= drag; p.vy = p.vy * drag + p.g * dt;
            p.x += p.vx * dt; p.y += p.vy * dt;
        }
    }

    static Color A(Color c, float a) { return Color.FromArgb(Math.Max(0, Math.Min(255, (int)a)), c); }

    public void Draw(Graphics g, float t)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // конфеті
        foreach (Conf c in conf)
        {
            float s = t - c.t0;
            if (s < 0) continue;
            float y = -20f * sc + c.vy * s;
            if (y > h + 20) continue;
            float x = c.x + (float)Math.Sin(c.phase + s * 2.2f) * c.sway;
            float a = c.rot + c.rotV * s, flip = (float)Math.Abs(Math.Cos(s * 5f + c.phase));
            float hw = c.w / 2f * (0.25f + 0.75f * flip), hh = c.h / 2f;
            float ca = (float)Math.Cos(a), sa = (float)Math.Sin(a);
            PointF[] q = new PointF[4];
            float[] ux = { -hw, hw, hw, -hw }, uy = { -hh, -hh, hh, hh };
            for (int k = 0; k < 4; k++) q[k] = new PointF(x + ux[k] * ca - uy[k] * sa, y + ux[k] * sa + uy[k] * ca);
            using (SolidBrush b = new SolidBrush(c.c)) g.FillPolygon(b, q);
        }
        // ракети
        foreach (Rocket r in rockets)
        {
            float s = (t - r.t0) / r.dur;
            if (s < 0 || s >= 1) continue;
            float e = 1f - (1f - s) * (1f - s);
            float x = r.x0 + (r.x1 - r.x0) * e, y = r.y0 + (r.y1 - r.y0) * e;
            float tl = Math.Max(0, e - 0.1f);
            float tx = r.x0 + (r.x1 - r.x0) * tl, ty = r.y0 + (r.y1 - r.y0) * tl;
            using (Pen p = new Pen(A(Color.FromArgb(255, 236, 190), 220), 2.2f * sc)) { p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; g.DrawLine(p, tx, ty, x, y); }
        }
        // іскри
        foreach (Part p in parts)
        {
            float k = 1f - p.age / p.life;
            if (p.star)
            {
                float s = p.size * (0.4f + 0.6f * k);
                PointF[] st = { new PointF(p.x, p.y - s), new PointF(p.x + s * 0.28f, p.y - s * 0.28f), new PointF(p.x + s, p.y), new PointF(p.x + s * 0.28f, p.y + s * 0.28f),
                                new PointF(p.x, p.y + s), new PointF(p.x - s * 0.28f, p.y + s * 0.28f), new PointF(p.x - s, p.y), new PointF(p.x - s * 0.28f, p.y - s * 0.28f) };
                using (SolidBrush b = new SolidBrush(A(p.c, 255 * k))) g.FillPolygon(b, st);
                continue;
            }
            float al = 255f * (float)Math.Pow(k, 0.7);
            float wd = Math.Max(1.4f, p.size * (0.5f + 0.5f * k));
            // м'яке сяйво навколо іскри й яскравий слід
            using (Pen gl = new Pen(A(p.c, al * 0.28f), wd * 2.6f)) { gl.StartCap = LineCap.Round; gl.EndCap = LineCap.Round; g.DrawLine(gl, p.x - p.vx * 0.05f, p.y - p.vy * 0.05f, p.x, p.y); }
            using (Pen pn = new Pen(A(p.c, al), wd)) { pn.StartCap = LineCap.Round; pn.EndCap = LineCap.Round; g.DrawLine(pn, p.x - p.vx * 0.05f, p.y - p.vy * 0.05f, p.x, p.y); }
        }
        // подарунки
        foreach (Gift gf in gifts)
        {
            float s = t - gf.t0;
            if (s < 0) continue;
            float ground = Ground, y, lift = 0, shadow = 1f;
            if (s < Fall)
            {
                float u = s / Fall;
                y = -gf.size + (ground + gf.size) * u * u;
                shadow = u * u;
            }
            else
            {
                float sl = s - Fall;
                y = ground - (sl < 1.4f ? (float)Math.Abs(Math.Sin(Math.PI * sl / 0.42f)) * gf.size * 0.32f * (float)Math.Exp(-sl * 3.2f) : 0f);
                float lu = Math.Max(0, Math.Min(1, (sl - 0.45f) / 0.55f));
                lift = (float)Math.Sin(Math.PI * lu) * gf.size * 0.3f;
            }
            DrawGift(g, gf, y, lift, shadow);
        }
    }

    // подарунок для інших сцен: x - центр, bottom - низ коробки
    public static void Present(Graphics g, float x, float bottom, float size, int look, float lift, float shadow)
    {
        Gift gf = new Gift();
        gf.x = x; gf.size = size; gf.look = look;
        DrawGift(g, gf, bottom, lift, shadow);
    }

    static void DrawGift(Graphics g, Gift gf, float bottom, float lift, float shadow)
    {
        float s = gf.size, x = gf.x;
        Color box = Looks[gf.look][0], lid = Looks[gf.look][1], rib = Looks[gf.look][2];
        Color edge = A(Color.Black, 110);
        float bh = s * 0.78f, lidH = s * 0.25f, bodyTop = bottom - bh, lidTop = bodyTop - lidH * 0.8f - lift;
        using (SolidBrush sb = new SolidBrush(A(Color.Black, 60 * shadow))) g.FillEllipse(sb, x - s * 0.62f, bottom - s * 0.05f, s * 1.24f, s * 0.12f);
        RectangleF body = new RectangleF(x - s / 2f, bodyTop, s, bh);
        RectangleF cap = new RectangleF(x - s * 0.55f, lidTop, s * 1.1f, lidH);
        using (SolidBrush b = new SolidBrush(box)) g.FillRectangle(b, body);
        using (SolidBrush b = new SolidBrush(rib)) g.FillRectangle(b, x - s * 0.09f, bodyTop, s * 0.18f, bh);
        using (Pen p = new Pen(edge, 1.3f)) g.DrawRectangle(p, body.X, body.Y, body.Width, body.Height);
        using (SolidBrush b = new SolidBrush(lid)) g.FillRectangle(b, cap);
        using (SolidBrush b = new SolidBrush(rib)) g.FillRectangle(b, x - s * 0.09f, lidTop, s * 0.18f, lidH);
        using (Pen p = new Pen(edge, 1.3f)) g.DrawRectangle(p, cap.X, cap.Y, cap.Width, cap.Height);
        // бант
        using (SolidBrush b = new SolidBrush(rib))
        using (Pen p = new Pen(edge, 1.2f))
        {
            RectangleF l = new RectangleF(x - s * 0.36f, lidTop - s * 0.24f, s * 0.34f, s * 0.26f), r = new RectangleF(x + s * 0.02f, lidTop - s * 0.24f, s * 0.34f, s * 0.26f);
            g.FillEllipse(b, l); g.DrawEllipse(p, l);
            g.FillEllipse(b, r); g.DrawEllipse(p, r);
            RectangleF k = new RectangleF(x - s * 0.08f, lidTop - s * 0.16f, s * 0.16f, s * 0.16f);
            g.FillEllipse(b, k); g.DrawEllipse(p, k);
        }
    }
}

