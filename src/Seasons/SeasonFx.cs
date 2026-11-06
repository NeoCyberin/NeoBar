using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// ======================================================================
//  Святкові ефекти на робочому столі (разом із запрошенням на тему):
//  Хелловін - кажани, привиди, павуки на нитках, гарбузи; Різдво - сніг, гірлянда, ялинки, подарунки;
//  Великдень - писанки, квіти, метелики, пелюстки; день народження - у BirthdayFx.cs.
//  Сцени лише лічать і малюють, тож їх можна перевірити без екрана.
// ======================================================================
abstract class FxScene : IFxScene
{
    protected readonly int w, h;
    protected readonly float sc;
    protected readonly Random rnd;

    protected FxScene(int w, int h, float dpi, int seed)
    {
        this.w = w; this.h = h; sc = Math.Max(0.75f, dpi);
        rnd = new Random(seed);
    }

    public float Total { get { return 10.6f; } }
    public float FadeFrom { get { return 9.6f; } }
    public virtual float Settle { get { return 99f; } }       // за замовчуванням рухається до кінця
    public virtual void Update(float t) { }
    public abstract void Draw(Graphics g, float t);

    protected float R() { return (float)rnd.NextDouble(); }
    protected float Ground { get { return h - 6f * sc; } }
    protected static Color A(Color c, float a) { return Color.FromArgb(Math.Max(0, Math.Min(255, (int)a)), c); }
    protected static float Clamp01(float x) { return Math.Max(0f, Math.Min(1f, x)); }
    protected static float Smooth(float x) { x = Clamp01(x); return x * x * (3 - 2 * x); }

    // падіння з відскоком: s - секунди від початку; повертає низ предмета; lift - підстрибування кришки, shadow - тінь
    protected static float Drop(float s, float ground, float size, out float lift, out float shadow)
    {
        const float F = 0.7f;
        lift = 0; shadow = 1f;
        if (s < F) { float u = s / F; shadow = u * u; return -size + (ground + size) * u * u; }
        float sl = s - F;
        float y = ground - (sl < 1.4f ? (float)Math.Abs(Math.Sin(Math.PI * sl / 0.42f)) * size * 0.32f * (float)Math.Exp(-sl * 3.2f) : 0f);
        return y;
    }

    protected void Shadow(Graphics g, float x, float bottom, float size, float k)
    {
        using (SolidBrush sb = new SolidBrush(A(Color.Black, 60 * k))) g.FillEllipse(sb, x - size * 0.62f, bottom - size * 0.05f, size * 1.24f, size * 0.12f);
    }
}

// ---------------------------------------------------------------- Хелловін
sealed class HalloweenScene : FxScene
{
    sealed class BatI { public float t0, dur, y, amp, size, ph; public int dir; }
    sealed class GhostI { public float t0, dur, x, size, ph; }
    sealed class SpiderI { public float t0, x, len, ph; }
    sealed class PumpI { public float t0, x, size; }

    readonly List<BatI> bats = new List<BatI>();
    readonly List<GhostI> ghosts = new List<GhostI>();
    readonly List<SpiderI> spiders = new List<SpiderI>();
    readonly List<PumpI> pumps = new List<PumpI>();

    public HalloweenScene(int w, int h, float dpi, int seed) : base(w, h, dpi, seed)
    {
        for (int i = 0; i < 10; i++)
        {
            BatI b = new BatI();
            b.t0 = R() * 5f; b.dur = 3.6f + R() * 2f; b.y = h * (0.08f + 0.5f * R()); b.amp = (30f + R() * 50f) * sc;
            b.size = (24f + R() * 18f) * sc; b.ph = R() * 6.28f; b.dir = i % 2 == 0 ? 1 : -1;
            bats.Add(b);
        }
        for (int i = 0; i < 4; i++)
        {
            GhostI g = new GhostI();
            g.t0 = 0.4f + i * 1.3f + R() * 0.5f; g.dur = 5.2f + R(); g.x = w * (0.12f + 0.76f * (i + R() * 0.6f) / 4f);
            g.size = (64f + R() * 30f) * sc; g.ph = R() * 6.28f;
            ghosts.Add(g);
        }
        for (int i = 0; i < 5; i++)
        {
            SpiderI s = new SpiderI();
            s.t0 = 0.8f + i * 0.7f + R() * 0.4f; s.x = w * (0.08f + 0.84f * (i + R() * 0.7f) / 5f); s.len = h * (0.14f + 0.26f * R()); s.ph = R() * 6.28f;
            spiders.Add(s);
        }
        for (int i = 0; i < 5; i++)
        {
            PumpI p = new PumpI();
            p.t0 = 0.6f + i * 0.55f + R() * 0.15f; p.size = (66f + R() * 24f) * sc; p.x = w * (i + 0.5f) / 5f + (R() - 0.5f) * w / 5f * 0.35f;
            pumps.Add(p);
        }
    }

    static void Ghost(Graphics g, float x, float y, float s, int a, float t)
    {
        float hh = s * 1.25f, sw = s / 4f, baseY = y + hh - sw / 2f;
        using (GraphicsPath gp = new GraphicsPath())
        {
            gp.AddArc(x - s / 2f, y, s, s, 180, 180);
            gp.AddLine(x + s / 2f, y + s / 2f, x + s / 2f, baseY);
            for (int i = 0; i < 4; i++) gp.AddArc(x + s / 2f - (i + 1) * sw, baseY - sw / 2f + (i % 2 == 0 ? 0 : -sw * 0.18f), sw, sw, 0, 180);
            gp.CloseFigure();
            using (SolidBrush glow = new SolidBrush(Color.FromArgb(a / 5, 190, 230, 255))) g.FillEllipse(glow, x - s * 0.8f, y - s * 0.3f, s * 1.6f, s * 1.9f);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(a, 246, 248, 255))) g.FillPath(b, gp);
            using (Pen p = new Pen(Color.FromArgb(a / 2, 150, 170, 210), Math.Max(1f, s * 0.025f))) g.DrawPath(p, gp);
        }
        using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Min(255, a + 40), 34, 30, 56)))
        {
            g.FillEllipse(b, x - s * 0.27f, y + s * 0.32f, s * 0.15f, s * 0.22f);
            g.FillEllipse(b, x + s * 0.12f, y + s * 0.32f, s * 0.15f, s * 0.22f);
            float m = s * (0.12f + 0.05f * (float)Math.Sin(t * 3f));
            g.FillEllipse(b, x - m / 2f, y + s * 0.64f, m, m * 1.3f);
        }
    }

    static void Spider(Graphics g, float x, float y, float r)
    {
        using (Pen p = new Pen(Color.FromArgb(235, 28, 20, 34), Math.Max(1.5f, r * 0.2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            for (int side = -1; side <= 1; side += 2)
                for (int i = 0; i < 4; i++)
                {
                    float dy = (i - 1.5f) * r * 0.5f;
                    g.DrawLines(p, new PointF[] { new PointF(x + side * r * 0.5f, y + dy * 0.4f), new PointF(x + side * r * 1.7f, y + dy - r * 0.7f), new PointF(x + side * r * 2.2f, y + dy + r * 0.9f) });
                }
        using (SolidBrush b = new SolidBrush(Color.FromArgb(245, 30, 22, 38)))
        {
            g.FillEllipse(b, x - r * 0.8f, y - r * 0.1f, r * 1.6f, r * 1.7f);
            g.FillEllipse(b, x - r * 0.55f, y - r * 0.75f, r * 1.1f, r * 0.95f);
        }
        using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 255, 70, 60)))
        {
            g.FillEllipse(b, x - r * 0.4f, y - r * 0.5f, r * 0.3f, r * 0.3f);
            g.FillEllipse(b, x + r * 0.1f, y - r * 0.5f, r * 0.3f, r * 0.3f);
        }
        using (SolidBrush b = new SolidBrush(Color.FromArgb(150, 255, 140, 50))) g.FillEllipse(b, x - r * 0.25f, y + r * 0.35f, r * 0.5f, r * 0.7f);
    }

    public override void Draw(Graphics g, float t)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // павутиння в кутах
        float wa = 150f * Smooth(t / 1.2f);
        Festive.Web(g, 0, 0, 230f * sc, A(Color.White, wa));
        Festive.Web(g, w, 0, 230f * sc, A(Color.White, wa));
        // привиди плавно проступають і зникають
        foreach (GhostI gi in ghosts)
        {
            float s = t - gi.t0;
            if (s < 0 || s > gi.dur) continue;
            float u = s / gi.dur, a = 200f * Math.Min(Smooth(u / 0.2f), Smooth((1 - u) / 0.25f));
            Ghost(g, gi.x + (float)Math.Sin(s * 1.6f + gi.ph) * 40f * sc, h * 0.95f - (h * 0.95f) * u, gi.size, (int)a, t);
        }
        // павуки
        foreach (SpiderI sp in spiders)
        {
            float s = t - sp.t0;
            if (s < 0) continue;
            float down = Smooth(s / 1.3f), up = Smooth((s - 7.6f) / 1.2f);
            float y = sp.len * down * (1f - up) + (float)Math.Sin(s * 3f + sp.ph) * 7f * sc * down * (1f - up);
            if (y < 2) continue;
            using (Pen p = new Pen(A(Color.White, 190), Math.Max(1f, 1.3f * sc))) g.DrawLine(p, sp.x, 0, sp.x, y);
            Spider(g, sp.x, y + 10f * sc, 9f * sc);
        }
        // кажани
        foreach (BatI b in bats)
        {
            float s = (t - b.t0) / b.dur;
            if (s < 0 || s > 1) continue;
            float x = b.dir > 0 ? -60f * sc + (w + 120f * sc) * s : w + 60f * sc - (w + 120f * sc) * s;
            float y = b.y + (float)Math.Sin(s * 9f + b.ph) * b.amp;
            float flap = 0.3f + 0.7f * (float)Math.Abs(Math.Cos(t * 11f + b.ph));
            GraphicsState st = g.Save();
            g.TranslateTransform(x, y);
            g.ScaleTransform(b.dir, flap);
            Festive.Bat(g, 0, 0, b.size * 1.12f, A(Color.FromArgb(255, 140, 40), 80));
            Festive.Bat(g, 0, 0, b.size, Color.FromArgb(244, 26, 16, 40));
            g.Restore(st);
        }
        // гарбузи падають унизу й світяться
        foreach (PumpI p in pumps)
        {
            float s = t - p.t0;
            if (s < 0) continue;
            float lift, shadow, bottom = Drop(s, Ground, p.size, out lift, out shadow);
            Shadow(g, p.x, bottom, p.size, shadow);
            float sz = p.size, top = bottom - sz * 0.8f, x0 = p.x - sz / 2f;
            float flick = 0.75f + 0.25f * (float)Math.Sin(t * 9f + p.x);
            using (SolidBrush gl = new SolidBrush(A(Color.FromArgb(255, 150, 30), 55 * flick))) g.FillEllipse(gl, x0 - sz * 0.25f, top - sz * 0.2f, sz * 1.5f, sz * 1.2f);
            Festive.Pumpkin(g, x0, top, sz, Color.FromArgb(255, 238, 120, 24));
            using (SolidBrush b = new SolidBrush(A(Color.FromArgb(255, 226, 90), 255 * flick)))
            {
                float ey = top + sz * 0.22f, ew = sz * 0.17f;
                g.FillPolygon(b, new PointF[] { new PointF(x0 + sz * 0.25f, ey + ew), new PointF(x0 + sz * 0.25f + ew, ey + ew), new PointF(x0 + sz * 0.25f + ew / 2f, ey) });
                g.FillPolygon(b, new PointF[] { new PointF(x0 + sz * 0.58f, ey + ew), new PointF(x0 + sz * 0.58f + ew, ey + ew), new PointF(x0 + sz * 0.58f + ew / 2f, ey) });
                float my = top + sz * 0.46f, mw = sz * 0.5f, mx = x0 + sz * 0.25f;
                PointF[] mouth = new PointF[9];
                for (int i = 0; i < 9; i++) mouth[i] = new PointF(mx + mw * i / 8f, my + (i % 2 == 0 ? 0f : sz * 0.1f) + (float)Math.Sin(i / 8.0 * Math.PI) * sz * 0.05f);
                g.FillPolygon(b, new PointF[] { mouth[0], mouth[1], mouth[2], mouth[3], mouth[4], mouth[5], mouth[6], mouth[7], mouth[8], new PointF(mx + mw * 0.85f, my + sz * 0.22f), new PointF(mx + mw * 0.15f, my + sz * 0.22f) });
            }
        }
    }
}

// ---------------------------------------------------------------- Різдво
sealed class XmasScene : FxScene
{
    sealed class FlakeI { public float x, y, v, sway, ph, size; public bool star; }
    sealed class PresentI { public float t0, x, size; public int look; }

    readonly List<FlakeI> snow = new List<FlakeI>();
    readonly List<PresentI> gifts = new List<PresentI>();
    readonly float[] treeX;

    public XmasScene(int w, int h, float dpi, int seed) : base(w, h, dpi, seed)
    {
        for (int i = 0; i < 150; i++)
        {
            FlakeI f = new FlakeI();
            f.x = R() * w; f.y = R() * (h + 40); f.v = (50f + R() * 90f) * sc; f.sway = (10f + R() * 30f) * sc; f.ph = R() * 6.28f;
            f.star = i < 26; f.size = f.star ? (7f + R() * 7f) * sc : (1.6f + R() * 3.4f) * sc;
            snow.Add(f);
        }
        int[] looks = { 0, 2, 3, 1, 5 };
        for (int i = 0; i < 5; i++)
        {
            PresentI p = new PresentI();
            p.t0 = 0.9f + i * 0.5f + R() * 0.15f; p.size = (58f + R() * 22f) * sc;
            p.x = w * (0.22f + 0.56f * (i + 0.5f) / 5f) + (R() - 0.5f) * 20f * sc; p.look = looks[i];
            gifts.Add(p);
        }
        treeX = new float[] { w * 0.07f, w * 0.93f };
    }

    static void Tree(Graphics g, float x, float bottom, float s, float grow, float t)
    {
        s *= grow;
        if (s < 4) return;
        using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 92, 60, 36))) g.FillRectangle(b, x - s * 0.07f, bottom - s * 0.14f, s * 0.14f, s * 0.14f);
        for (int i = 0; i < 3; i++)
        {
            float tw = s * (0.62f - i * 0.14f), ty = bottom - s * 0.14f - i * s * 0.26f, th = s * 0.36f;
            PointF[] tri = { new PointF(x - tw / 2f, ty), new PointF(x + tw / 2f, ty), new PointF(x, ty - th) };
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 26 + i * 8, 118 + i * 10, 64))) g.FillPolygon(b, tri);
            using (Pen p = new Pen(Color.FromArgb(120, 210, 240, 200), Math.Max(1f, s * 0.012f))) g.DrawPolygon(p, tri);
            // вогники
            Color[] lc = { Color.FromArgb(255, 70, 70), Color.FromArgb(255, 214, 80), Color.FromArgb(90, 170, 255), Color.FromArgb(255, 255, 255) };
            for (int k = 0; k < 3; k++)
            {
                float lx = x + (k - 1) * tw * 0.28f, ly = ty - th * (0.18f + 0.1f * ((k + i) % 2));
                float br = 0.45f + 0.55f * (float)Math.Abs(Math.Sin(t * 3f + i * 1.7f + k * 2.1f));
                using (SolidBrush gl = new SolidBrush(Color.FromArgb((int)(90 * br), lc[(i + k) % 4]))) g.FillEllipse(gl, lx - s * 0.04f, ly - s * 0.04f, s * 0.08f, s * 0.08f);
                using (SolidBrush b2 = new SolidBrush(Color.FromArgb((int)(120 + 135 * br), lc[(i + k) % 4]))) g.FillEllipse(b2, lx - s * 0.02f, ly - s * 0.02f, s * 0.04f, s * 0.04f);
            }
        }
        float sy = bottom - s * 0.14f - 2 * s * 0.26f - s * 0.36f, sr = s * 0.09f * (0.85f + 0.15f * (float)Math.Sin(t * 4f));
        using (SolidBrush gl = new SolidBrush(Color.FromArgb(70, 255, 230, 120))) g.FillEllipse(gl, x - sr * 1.8f, sy - sr * 1.8f, sr * 3.6f, sr * 3.6f);
        using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 255, 226, 100)))
        {
            PointF[] st = new PointF[10];
            for (int i = 0; i < 10; i++) { double a = -Math.PI / 2 + i * Math.PI / 5; float rr = i % 2 == 0 ? sr : sr * 0.45f; st[i] = new PointF(x + (float)Math.Cos(a) * rr, sy + (float)Math.Sin(a) * rr); }
            g.FillPolygon(b, st);
        }
    }

    public override void Draw(Graphics g, float t)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // гірлянда вздовж верху: з'їжджає зверху, лампочки мерехтять
        float slide = (1f - Smooth(t / 1.0f)) * -60f * sc;
        int seg = Math.Max(4, (int)(w / (230f * sc)));
        float segW = w / (float)seg, sag = 34f * sc;
        List<PointF> wire = new List<PointF>();
        Color[] bc = { Color.FromArgb(255, 70, 70), Color.FromArgb(255, 214, 80), Color.FromArgb(90, 170, 255), Color.FromArgb(110, 230, 130) };
        int bi = 0;
        List<PointF> bulbs = new List<PointF>();
        for (int s2 = 0; s2 < seg; s2++)
            for (int k = 0; k <= 20; k++)
            {
                float u = k / 20f, x = s2 * segW + u * segW, y = 8f * sc + slide + sag * 4f * u * (1f - u);
                wire.Add(new PointF(x, y));
                if (k > 0 && k < 20 && k % 5 == 0) bulbs.Add(new PointF(x, y));
            }
        using (Pen p = new Pen(Color.FromArgb(200, 20, 60, 36), Math.Max(1.5f, 2f * sc))) g.DrawLines(p, wire.ToArray());
        foreach (PointF b in bulbs)
        {
            float br = 0.4f + 0.6f * (float)Math.Abs(Math.Sin(t * 2.6f + bi * 1.3f));
            Color c = bc[bi % 4];
            float r = 5.5f * sc;
            using (SolidBrush gl = new SolidBrush(Color.FromArgb((int)(80 * br), c))) g.FillEllipse(gl, b.X - r * 2.4f, b.Y - r * 1.4f, r * 4.8f, r * 4.8f);
            using (SolidBrush bb = new SolidBrush(Color.FromArgb((int)(150 + 105 * br), c))) g.FillEllipse(bb, b.X - r * 0.7f, b.Y, r * 1.4f, r * 2.1f);
            using (SolidBrush hi = new SolidBrush(Color.FromArgb(160, 255, 255, 255))) g.FillEllipse(hi, b.X - r * 0.25f, b.Y + r * 0.3f, r * 0.4f, r * 0.5f);
            bi++;
        }
        // ялинки по краях
        for (int i = 0; i < treeX.Length; i++)
            Tree(g, treeX[i], Ground, (150f + i * 20f) * sc, Smooth((t - 0.3f - i * 0.4f) / 1.2f), t);
        // подарунки
        foreach (PresentI p in gifts)
        {
            float s = t - p.t0;
            if (s < 0) continue;
            float lift, shadow, bottom = Drop(s, Ground, p.size, out lift, out shadow);
            float lu = Clamp01((s - 0.7f - 0.45f) / 0.55f);
            BirthdayScene.Present(g, p.x, bottom, p.size, p.look, (float)Math.Sin(Math.PI * lu) * p.size * 0.3f, shadow);
        }
        // сніг
        foreach (FlakeI f in snow)
        {
            float y = (f.y + f.v * t) % (h + 40f) - 20f, x = f.x + (float)Math.Sin(t * 1.3f + f.ph) * f.sway;
            if (f.star) Festive.Flake(g, x, y, f.size, Color.FromArgb(215, 255, 255, 255), (int)f.ph);
            else using (SolidBrush b = new SolidBrush(Color.FromArgb(220, 255, 255, 255))) g.FillEllipse(b, x - f.size / 2f, y - f.size / 2f, f.size, f.size);
        }
    }
}

// ---------------------------------------------------------------- Великдень
sealed class EasterScene : FxScene
{
    sealed class EggI { public float t0, x, w; public int look; }
    sealed class FlowerI { public float t0, x, h; public int kind; public Color c; public float ph; }
    sealed class ButterI { public float t0, dur, y, amp, size, ph; public int dir; public Color c1, c2; }
    sealed class PetalI { public float x, y, v, sway, ph, size, rot, rotV; }

    readonly List<EggI> eggs = new List<EggI>();
    readonly List<FlowerI> flowers = new List<FlowerI>();
    readonly List<ButterI> butter = new List<ButterI>();
    readonly List<PetalI> petals = new List<PetalI>();

    static readonly Color[][] Eggs = {
        new[] { Color.FromArgb(255, 150, 190), Color.FromArgb(255, 255, 255) },
        new[] { Color.FromArgb(120, 200, 255), Color.FromArgb(255, 240, 120) },
        new[] { Color.FromArgb(160, 230, 140), Color.FromArgb(255, 255, 255) },
        new[] { Color.FromArgb(255, 210, 100), Color.FromArgb(230, 90, 130) },
        new[] { Color.FromArgb(190, 150, 250), Color.FromArgb(255, 240, 160) },
        new[] { Color.FromArgb(255, 160, 130), Color.FromArgb(255, 255, 255) } };

    public EasterScene(int w, int h, float dpi, int seed) : base(w, h, dpi, seed)
    {
        int ne = 7;
        for (int i = 0; i < ne; i++)
        {
            EggI e = new EggI();
            e.t0 = 0.7f + i * 0.42f + R() * 0.12f; e.w = (44f + R() * 16f) * sc; e.look = i;
            e.x = w * (i + 0.5f) / ne + (R() - 0.5f) * w / ne * 0.3f;
            eggs.Add(e);
        }
        Color[] fc = { Color.FromArgb(255, 110, 150), Color.FromArgb(255, 200, 70), Color.FromArgb(180, 130, 240), Color.FromArgb(255, 255, 255), Color.FromArgb(255, 140, 90) };
        for (int i = 0; i < 12; i++)
        {
            FlowerI f = new FlowerI();
            f.t0 = 0.3f + R() * 1.6f; f.x = w * (i + 0.5f) / 12f + (R() - 0.5f) * w / 12f * 0.6f; f.h = (60f + R() * 55f) * sc; f.kind = i % 2; f.c = fc[i % fc.Length]; f.ph = R() * 6.28f;
            flowers.Add(f);
        }
        Color[][] bw = { new[] { Color.FromArgb(255, 170, 60), Color.FromArgb(255, 220, 120) }, new[] { Color.FromArgb(110, 180, 255), Color.FromArgb(190, 225, 255) },
                         new[] { Color.FromArgb(255, 120, 170), Color.FromArgb(255, 190, 215) }, new[] { Color.FromArgb(170, 130, 240), Color.FromArgb(215, 190, 255) } };
        for (int i = 0; i < 6; i++)
        {
            ButterI b = new ButterI();
            b.t0 = R() * 4f; b.dur = 5.5f + R() * 2f; b.y = h * (0.15f + 0.5f * R()); b.amp = (40f + R() * 60f) * sc; b.size = (16f + R() * 10f) * sc; b.ph = R() * 6.28f; b.dir = i % 2 == 0 ? 1 : -1;
            b.c1 = bw[i % 4][0]; b.c2 = bw[i % 4][1];
            butter.Add(b);
        }
        for (int i = 0; i < 70; i++)
        {
            PetalI p = new PetalI();
            p.x = R() * w; p.y = R() * (h + 40); p.v = (45f + R() * 60f) * sc; p.sway = (20f + R() * 40f) * sc; p.ph = R() * 6.28f; p.size = (6f + R() * 5f) * sc; p.rot = R() * 6.28f; p.rotV = (R() - 0.5f) * 4f;
            petals.Add(p);
        }
    }

    static void Egg(Graphics g, float x, float bottom, float ew, int look)
    {
        float eh = ew * 1.28f;
        RectangleF r = new RectangleF(x - ew / 2f, bottom - eh, ew, eh);
        Color c1 = Eggs[look % Eggs.Length][0], c2 = Eggs[look % Eggs.Length][1];
        using (GraphicsPath path = Festive.EggPath(r))
        {
            using (LinearGradientBrush lb = new LinearGradientBrush(r, Color.FromArgb(255, Math.Min(255, c1.R + 40), Math.Min(255, c1.G + 40), Math.Min(255, c1.B + 40)), c1, 60f)) g.FillPath(lb, path);
            GraphicsState st = g.Save();
            g.SetClip(path, CombineMode.Intersect);
            using (Pen p = new Pen(c2, Math.Max(2f, ew * 0.07f)))
            using (SolidBrush b = new SolidBrush(c2))
                switch (look % 3)
                {
                    case 0:     // смуги
                        g.DrawLine(p, r.X, r.Y + eh * 0.38f, r.Right, r.Y + eh * 0.38f);
                        g.DrawLine(p, r.X, r.Y + eh * 0.58f, r.Right, r.Y + eh * 0.58f);
                        break;
                    case 1:     // зигзаг
                    {
                        PointF[] zz = new PointF[9];
                        for (int i = 0; i < 9; i++) zz[i] = new PointF(r.X + ew * i / 8f, r.Y + eh * 0.5f + (i % 2 == 0 ? -1 : 1) * eh * 0.07f);
                        g.DrawLines(p, zz);
                        break;
                    }
                    default:    // крапки
                        for (int i = 0; i < 7; i++) g.FillEllipse(b, r.X + ew * (0.2f + 0.6f * ((i * 37) % 10) / 10f), r.Y + eh * (0.25f + 0.55f * ((i * 53) % 10) / 10f), ew * 0.1f, ew * 0.1f);
                        break;
                }
            g.Restore(st);
            using (SolidBrush hi = new SolidBrush(Color.FromArgb(110, 255, 255, 255))) g.FillEllipse(hi, r.X + ew * 0.22f, r.Y + eh * 0.16f, ew * 0.16f, eh * 0.2f);
            using (Pen ep = new Pen(Color.FromArgb(90, 0, 0, 0), 1.2f)) g.DrawPath(ep, path);
        }
    }

    static void Flower(Graphics g, FlowerI f, float t, float bottom, float grow)
    {
        float top = bottom - f.h * grow, sway = (float)Math.Sin(t * 1.6f + f.ph) * 5f * grow;
        using (Pen p = new Pen(Color.FromArgb(235, 70, 160, 80), Math.Max(2f, f.h * 0.04f)) { StartCap = LineCap.Round })
            g.DrawBezier(p, f.x, bottom, f.x, bottom - f.h * grow * 0.5f, f.x + sway, bottom - f.h * grow * 0.7f, f.x + sway, top);
        float cx = f.x + sway, cy = top, r = Math.Max(4f, f.h * 0.15f) * Math.Min(1f, grow * 1.5f);
        if (f.kind == 0)
        {
            // тюльпан
            using (SolidBrush b = new SolidBrush(f.c))
            {
                g.FillEllipse(b, cx - r * 0.95f, cy - r * 1.4f, r * 1.1f, r * 2f);
                g.FillEllipse(b, cx - r * 0.15f, cy - r * 1.4f, r * 1.1f, r * 2f);
                g.FillEllipse(b, cx - r * 0.55f, cy - r * 1.8f, r * 1.1f, r * 2.3f);
            }
            using (Pen p = new Pen(Color.FromArgb(70, 0, 0, 0), 1f)) g.DrawEllipse(p, cx - r * 0.55f, cy - r * 1.8f, r * 1.1f, r * 2.3f);
        }
        else
        {
            // ромашка
            using (SolidBrush b = new SolidBrush(f.c))
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    g.FillEllipse(b, cx + (float)Math.Cos(a) * r * 0.9f - r * 0.4f, cy + (float)Math.Sin(a) * r * 0.9f - r * 0.4f, r * 0.8f, r * 0.8f);
                }
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 255, 200, 50))) g.FillEllipse(b, cx - r * 0.5f, cy - r * 0.5f, r, r);
        }
    }

    static void Butterfly(Graphics g, float x, float y, float s, float flap, Color c1, Color c2)
    {
        float fw = s * (0.25f + 0.75f * flap);
        using (SolidBrush b1 = new SolidBrush(c1))
        using (SolidBrush b2 = new SolidBrush(c2))
        using (Pen ep = new Pen(Color.FromArgb(120, 40, 30, 50), 1f))
        {
            for (int side = -1; side <= 1; side += 2)
            {
                RectangleF up = new RectangleF(side > 0 ? x : x - fw, y - s * 0.85f, fw, s * 0.95f), lo = new RectangleF(side > 0 ? x : x - fw * 0.8f, y - s * 0.05f, fw * 0.8f, s * 0.75f);
                g.FillEllipse(b1, up); g.DrawEllipse(ep, up);
                g.FillEllipse(b2, lo); g.DrawEllipse(ep, lo);
            }
            using (Pen bp = new Pen(Color.FromArgb(230, 50, 36, 56), Math.Max(2f, s * 0.12f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(bp, x, y - s * 0.6f, x, y + s * 0.6f);
                using (Pen an = new Pen(Color.FromArgb(230, 50, 36, 56), 1.2f)) { g.DrawLine(an, x, y - s * 0.6f, x - s * 0.3f, y - s * 0.95f); g.DrawLine(an, x, y - s * 0.6f, x + s * 0.3f, y - s * 0.95f); }
            }
        }
    }

    public override void Draw(Graphics g, float t)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // квіти ростуть знизу
        foreach (FlowerI f in flowers)
        {
            float s = t - f.t0;
            if (s < 0) continue;
            Flower(g, f, t, Ground + 4f * sc, Smooth(s / 1.1f));
        }
        // писанки
        foreach (EggI e in eggs)
        {
            float s = t - e.t0;
            if (s < 0) continue;
            float lift, shadow, bottom = Drop(s, Ground, e.w * 1.28f, out lift, out shadow);
            Shadow(g, e.x, bottom, e.w * 1.1f, shadow);
            Egg(g, e.x, bottom, e.w, e.look);
        }
        // пелюстки
        foreach (PetalI p in petals)
        {
            float y = (p.y + p.v * t) % (h + 40f) - 20f, x = p.x + (float)Math.Sin(t * 1.2f + p.ph) * p.sway;
            GraphicsState st = g.Save();
            g.TranslateTransform(x, y);
            g.RotateTransform((p.rot + p.rotV * t) * 57.3f);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(200, 255, 190, 210))) g.FillEllipse(b, -p.size / 2f, -p.size / 3f, p.size, p.size * 0.66f);
            g.Restore(st);
        }
        // метелики
        foreach (ButterI b in butter)
        {
            float s = (t - b.t0) / b.dur;
            if (s < 0 || s > 1) continue;
            float x = b.dir > 0 ? -40f * sc + (w + 80f * sc) * s : w + 40f * sc - (w + 80f * sc) * s;
            float y = b.y + (float)Math.Sin(s * 11f + b.ph) * b.amp;
            Butterfly(g, x, y, b.size, (float)Math.Abs(Math.Cos(t * 9f + b.ph)), b.c1, b.c2);
        }
    }
}

// ---------------------------------------------------------------- вікно
sealed class SeasonFx : LayeredForm
{
    static SeasonFx cur;
    public static bool Playing { get { return cur != null && !cur.IsDisposed; } }
    readonly IFxScene scene;
    readonly Timer timer = new Timer();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly Rectangle area;
    bool settled;

    // запускає звук і ефект на столі; тихо нічого не робить, якщо на передньому плані гра або повноекранна програма
    public static void Start(NeoBar bar, string theme)
    {
        if (bar == null || NeoBar.Paused) return;
        try { Chime.PlayFx(theme, 60); } catch { }
        if (!bar.Config.Anim) return;
        try
        {
            if (cur != null && !cur.IsDisposed) cur.Close();
            cur = new SeasonFx(bar, theme);
            cur.Show();
            cur.timer.Start();
            cur.Step();
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    SeasonFx(NeoBar bar, string theme)
    {
        ExtraEx = 0x20 | 0x8 | 0x08000000;     // кліки крізь вікно, поверх усього, без фокуса
        TopMost = true;
        area = Screen.FromControl(bar).WorkingArea;
        int seed = Environment.TickCount;
        if (theme == "halloween") scene = new HalloweenScene(area.Width, area.Height, bar.Dpi, seed);
        else if (theme == "xmas") scene = new XmasScene(area.Width, area.Height, bar.Dpi, seed);
        else if (theme == "easter") scene = new EasterScene(area.Width, area.Height, bar.Dpi, seed);
        else scene = new BirthdayScene(area.Width, area.Height, bar.Dpi, seed);
        timer.Interval = 33;
        timer.Tick += delegate { try { Step(); } catch (Exception ex) { Util.ErrorLog(ex); try { Close(); } catch { } } };
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Stop(); timer.Dispose();
        if (cur == this) cur = null;
        base.OnFormClosed(e);
    }

    void DrawFrame(float t)
    {
        scene.Update(t);
        Graphics g = BeginFrame(area.Width, area.Height);
        if (g == null) return;
        using (g) scene.Draw(g, t);
    }

    void Step()
    {
        float t = (float)clock.Elapsed.TotalSeconds;
        if (t >= scene.Total || NeoBar.Paused) { if (NeoBar.Paused) Chime.Stop(); Close(); return; }       // у грі ефект одразу зникає
        if (t >= scene.Settle && t < scene.FadeFrom)
        {
            // усе влягло: один кадр і тиша (без перемальовування)
            if (!settled) { settled = true; DrawFrame(t); PushFrame(area.Location, area.Size); timer.Interval = 200; }
            return;
        }
        if (t >= scene.FadeFrom)
        {
            // сцени з рухом до кінця малюються й під час згасання, нерухомі - один раз
            if (scene.Settle >= scene.FadeFrom || !settled) { settled = true; DrawFrame(t); }
            float f = (t - scene.FadeFrom) / (scene.Total - scene.FadeFrom);
            PushAlpha = (byte)Math.Max(0, Math.Min(255, (int)(255 * (1 - f))));
            timer.Interval = 33;
            PushFrame(area.Location, area.Size);
            return;
        }
        DrawFrame(t);
        PushFrame(area.Location, area.Size);
    }
}
