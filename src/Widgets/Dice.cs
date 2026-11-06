using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// ======================================================================
//  Кубик: 1-6 кубиків по 2-100 граней; до чисел можна дописати свої назви (аніме, ігри…),
//  тоді кидок - це випадковий вибір із цього списку. Стан - у dice.ini профілю.
//  «Кинути» - кубики об'ємно котяться по робочому столу (прозоре вікно поверх усього, крізь яке
//  проходять кліки), зупиняються на випавшому числі й за кілька секунд зникають. Після цього
//  нічого не працює.
// ======================================================================
static class Dice
{
    public static int Count = 1, Faces = 6;
    public static bool Total = true;                 // кілька кубиків: true - один результат (сума), false - кожен кубик окремо
    public static readonly List<string> Labels = new List<string>();     // Labels[i] - назва для числа i + 1
    public static readonly List<string> History = new List<string>();   // останні 10 кидків, лише в пам'яті (найновіший - першим)
    public static int[] Last = new int[0];                                // останній кидок
    public static bool Rolling { get { return throwing; } }
    public static event Action Changed;

    static int[] shown = new int[0];        // що показувати на панелі (під час кидка - «?»)
    static bool throwing;
    static int seq;
    static string loadedFor;
    static readonly RandomNumberGenerator rng = RandomNumberGenerator.Create();

    public static int[] Shown { get { Load(); return shown; } }
    public static string Sig { get { Load(); return seq + "|" + throwing + "|" + Count + "|" + Faces; } }

    static string File_ { get { return Prof.File("dice.ini"); } }

    // ---------- стан ----------
    public static void Load()
    {
        string who = Prof.Dir;
        if (loadedFor == who) return;
        loadedFor = who;
        Count = 1; Faces = 6; Total = true;
        Labels.Clear(); Last = new int[0];
        try
        {
            if (File.Exists(File_))
                foreach (string raw in File.ReadAllLines(File_, Encoding.UTF8))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = raw.Substring(0, eq).Trim().ToLowerInvariant(), v = raw.Substring(eq + 1);
                    int n; int.TryParse(v.Trim(), out n);
                    if (k == "count") Count = Clamp(n, 1, 6);
                    else if (k == "faces") Faces = Clamp(n, 2, 100);
                    else if (k == "total") Total = v.Trim() != "0";
                    else if (k == "label") Labels.Add(v.Trim());
                    else if (k == "last")
                    {
                        List<int> l = new List<int>();
                        foreach (string p in v.Split(',')) { int x; if (int.TryParse(p.Trim(), out x) && x > 0) l.Add(x); }
                        Last = l.ToArray();
                    }
                }
        }
        catch { }
        while (Labels.Count > 0 && Labels[Labels.Count - 1].Length == 0) Labels.RemoveAt(Labels.Count - 1);
        if (Locked) { Faces = Math.Min(100, Labels.Count); Count = 1; }
        shown = (int[])Last.Clone();
    }

    public static void Save()
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(L.T("# NeoBar - кубик"));
            sb.AppendLine("count=" + Count);
            sb.AppendLine("faces=" + Faces);
            sb.AppendLine("total=" + (Total ? 1 : 0));
            string[] ls = new string[Last.Length];
            for (int i = 0; i < Last.Length; i++) ls[i] = Last[i].ToString();
            sb.AppendLine("last=" + string.Join(",", ls));
            foreach (string l in Labels) sb.AppendLine("label=" + l.Replace("\r", "").Replace("\n", " "));
            File.WriteAllText(File_, sb.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    static int Clamp(int v, int a, int b) { return Math.Max(a, Math.Min(b, v)); }

    public static void SetCount(int n) { Load(); if (Locked) return; Count = Clamp(n, 1, 6); Last = new int[0]; if (!throwing) shown = new int[0]; Save(); Fire(); }
    // коли є хоча б дві назви, граней стільки, скільки рядків із назвами, і вручну це не змінюється
    public static bool Locked { get { Load(); return Labels.Count >= 2; } }

    public static void SetFaces(int n) { Load(); if (Locked) return; Faces = Clamp(n, 2, 100); Last = new int[0]; if (!throwing) shown = new int[0]; Save(); Fire(); }
    public static void SetTotal(bool t) { Load(); Total = t; Save(); Fire(); }

    public static void SetLabels(string text)
    {
        Load();
        Labels.Clear();
        foreach (string l in (text ?? "").Replace("\r", "").Split('\n')) Labels.Add(l.Trim());
        while (Labels.Count > 0 && Labels[Labels.Count - 1].Length == 0) Labels.RemoveAt(Labels.Count - 1);
        if (Labels.Count > 100) Labels.RemoveRange(100, Labels.Count - 100);
        if (Labels.Count >= 2 && (Faces != Labels.Count || Count != 1)) { Faces = Labels.Count; Count = 1; Last = new int[0]; if (!throwing) shown = new int[0]; }
        Save();
        Fire();
    }

    public static string LabelOf(int v)
    {
        Load();
        return v >= 1 && v <= Labels.Count ? Labels[v - 1] : "";
    }

    public static bool HasLabels { get { Load(); foreach (string l in Labels) if (l.Length > 0) return true; return false; } }

    public static int Sum(int[] a) { int s = 0; foreach (int x in a) s += x; return s; }

    // текст результату. Окремо: «4 - Naruto; 2 - Bleach». Сумою: «3 + 5 = 8» (і назва числа-суми, якщо є)
    public static string Text(int[] a)
    {
        if (a == null || a.Length == 0) return "";
        if (a.Length > 1 && Total)
        {
            string t = string.Join(" + ", Array.ConvertAll(a, delegate (int x) { return x.ToString(); })) + " = " + Sum(a);
            string sl = LabelOf(Sum(a));
            return sl.Length > 0 ? t + " - " + sl : t;
        }
        List<string> parts = new List<string>();
        foreach (int v in a)
        {
            string l = LabelOf(v);
            parts.Add(l.Length > 0 ? v + " - " + l : v.ToString());
        }
        return string.Join(HasLabels ? "; " : ", ", parts.ToArray());
    }

    static void Fire() { seq++; Action h = Changed; if (h != null) { try { h(); } catch { } } }

    // чесне випадкове число 1..n (без перекосу від остачі)
    public static int Next(int n)
    {
        byte[] b = new byte[4];
        uint lim = uint.MaxValue - uint.MaxValue % (uint)n;
        uint x;
        do { rng.GetBytes(b); x = BitConverter.ToUInt32(b, 0); } while (x >= lim);
        return (int)(x % (uint)n) + 1;
    }

    // ---------- кидок ----------
    public static void Roll(NeoBar bar)
    {
        Load();
        if (throwing) return;
        int n = Count;
        int[] res = new int[n];
        for (int i = 0; i < n; i++)
        {
            int v;
            v = Next(Faces);                // кубики незалежні, числа можуть повторюватись
            res[i] = v;
        }
        Last = res;
        Save();
        throwing = true;
        shown = new int[n];         // поки кубики летять - на панелі «?»
        Fire();
        Action landed = delegate
        {
            if (!throwing) return;
            throwing = false;
            shown = (int[])Last.Clone();
            History.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "  " + Text(Last));
            while (History.Count > 10) History.RemoveAt(History.Count - 1);
            Fire();
        };
        try { DiceThrow.Start(bar, res, Faces, Text(res), landed); }
        catch (Exception ex) { Util.ErrorLog(ex); landed(); }
    }

    // ---------- плоска грань (віджет на панелі) ----------
    static readonly float[][] Pips =
    {
        new float[] { },
        new float[] { 0.5f, 0.5f },
        new float[] { 0.27f, 0.27f, 0.73f, 0.73f },
        new float[] { 0.27f, 0.27f, 0.5f, 0.5f, 0.73f, 0.73f },
        new float[] { 0.27f, 0.27f, 0.73f, 0.27f, 0.27f, 0.73f, 0.73f, 0.73f },
        new float[] { 0.27f, 0.27f, 0.73f, 0.27f, 0.5f, 0.5f, 0.27f, 0.73f, 0.73f, 0.73f },
        new float[] { 0.27f, 0.25f, 0.73f, 0.25f, 0.27f, 0.5f, 0.73f, 0.5f, 0.27f, 0.75f, 0.73f, 0.75f },
    };

    public static float[] PipsOf(int v) { return v >= 1 && v <= 6 ? Pips[v] : Pips[0]; }

    // грань кубика: точки для 1-6 (кубик на 6 граней), інакше число
    public static void DrawDie(Graphics g, RectangleF r, int v, Color face, Color ink, Font numFont, string text)
    {
        SmoothingMode sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float rad = r.Width * 0.2f;
        Rectangle ri = Rectangle.Round(r);
        int kind = Poly.KindFor(Faces);
        if (kind == 6)
        {
            using (SolidBrush b = new SolidBrush(face)) Util.FillRound(g, b, ri, (int)rad);
            using (Pen pn = new Pen(Color.FromArgb(70, ink), Math.Max(1f, r.Width * 0.03f)))
            using (GraphicsPath gp = Util.Round(ri, (int)rad)) g.DrawPath(pn, gp);
        }
        else
        {
            // силует форми: трикутник (к4), ромб (к8), дельтоїд (к10), п'ятикутник (к12), шестикутник (к20)
            using (GraphicsPath gp = new GraphicsPath())
            {
                Poly.Outline(gp, kind, r);
                using (SolidBrush b = new SolidBrush(face)) g.FillPath(b, gp);
                using (Pen pn = new Pen(Color.FromArgb(90, ink), Math.Max(1f, r.Width * 0.035f))) { pn.LineJoin = LineJoin.Round; g.DrawPath(pn, gp); }
            }
            if (kind == 20)
                using (Pen pn = new Pen(Color.FromArgb(45, ink), Math.Max(1f, r.Width * 0.025f)))
                    g.DrawPolygon(pn, new PointF[] { new PointF(r.X + r.Width * 0.5f, r.Y + r.Height * 0.2f), new PointF(r.X + r.Width * 0.82f, r.Y + r.Height * 0.71f), new PointF(r.X + r.Width * 0.18f, r.Y + r.Height * 0.71f) });
            // число - у центрі грані (у трикутника - нижче, де ширше)
            if (kind == 4) ri = Rectangle.Round(new RectangleF(r.X, r.Y + r.Height * 0.24f, r.Width, r.Height * 0.66f));
            else if (kind == 20) ri = Rectangle.Round(new RectangleF(r.X, r.Y + r.Height * 0.3f, r.Width, r.Height * 0.4f));
        }
        TextFormatFlags tf = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        if (text != null)
            TextRenderer.DrawText(g, text, numFont, ri, ink, tf);
        else if (v >= 1 && v <= 6 && Faces == 6)
        {
            float d = r.Width * 0.17f;
            float[] p = Pips[v];
            using (SolidBrush pb = new SolidBrush(ink))
                for (int i = 0; i + 1 < p.Length; i += 2)
                    g.FillEllipse(pb, r.X + r.Width * p[i] - d / 2, r.Y + r.Height * p[i + 1] - d / 2, d, d);
        }
        else if (numFont != null) TextRenderer.DrawText(g, v > 0 ? v.ToString() : "?", numFont, ri, v > 0 ? ink : Color.FromArgb(150, ink), tf);
        g.SmoothingMode = sm;
    }
}

// ----------------------------------------------------------------------
//  Форми кубиків, крім звичайного: к4 (тетраедр), к8 (октаедр), к10 (п'ятикутний трапецоедр), к12 (додекаедр), к20 (ікосаедр).
//  Вершини задаються числами, грані знаходяться самі (опукла оболонка) - і вже тоді нормалі, центри, протилежні грані.
// ----------------------------------------------------------------------
sealed class Poly
{
    public int Kind;                 // кількість граней форми
    public float[][] V;              // вершини (на одиничній сфері)
    public int[][] F;                // грані: вершини проти годинникової стрілки, якщо дивитися ззовні
    public float[][] N, C, Up;       // нормаль, центр, напрям «угору» для числа (до найдальшої вершини)
    public float[] Inr;              // радіус вписаного кола грані
    public int[] Opp;                // протилежна (паралельна) грань або -1
    public float Rest;               // відстань від центру до площини грані (висота, на якій лежить)
    public float Size;               // розмір відносно половини ребра звичайного кубика

    static readonly Dictionary<int, Poly> cache = new Dictionary<int, Poly>();

    // яка форма для кубика на n граней (2-6 граней без к4 - звичайний кубик)
    public static int KindFor(int faces)
    {
        if (faces <= 4 && faces != 2) return 4;
        if (faces <= 6) return 6;
        if (faces <= 8) return 8;
        if (faces <= 10) return 10;
        if (faces <= 12) return 12;
        return 20;
    }

    public static Poly Get(int kind)
    {
        if (kind == 6) return null;
        lock (cache)
        {
            Poly p;
            if (cache.TryGetValue(kind, out p)) return p;
            p = Make(kind);
            cache[kind] = p;
            return p;
        }
    }

    static float[] Nrm(float[] v) { float l = (float)Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]); return new float[] { v[0] / l, v[1] / l, v[2] / l }; }
    static float[] Sub(float[] a, float[] b) { return new float[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
    static float Dot(float[] a, float[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
    static float[] Cross(float[] a, float[] b) { return new float[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }

    static Poly Make(int kind)
    {
        List<float[]> v = new List<float[]>();
        float g = (1 + (float)Math.Sqrt(5)) / 2, ig = 1 / g;
        float size = 1.6f;
        switch (kind)
        {
            case 4:
                v.Add(new float[] { 1, 1, 1 }); v.Add(new float[] { 1, -1, -1 }); v.Add(new float[] { -1, 1, -1 }); v.Add(new float[] { -1, -1, 1 });
                size = 2.0f; break;
            case 8:
                foreach (int s in new int[] { 1, -1 }) { v.Add(new float[] { s, 0, 0 }); v.Add(new float[] { 0, s, 0 }); v.Add(new float[] { 0, 0, s }); }
                size = 1.7f; break;
            case 10:
            {
                // кільця вершин на ±c, полюси на ±1: при c = (1-cos36)/(1+cos36) грані-«дельтоїди» пласкі
                double c36 = Math.Cos(Math.PI / 5);
                float c = (float)((1 - c36) / (1 + c36));
                v.Add(new float[] { 0, 0, 1 }); v.Add(new float[] { 0, 0, -1 });
                for (int k = 0; k < 5; k++)
                {
                    double a = k * 2 * Math.PI / 5, b = a + Math.PI / 5;
                    v.Add(new float[] { (float)Math.Cos(a), (float)Math.Sin(a), c });
                    v.Add(new float[] { (float)Math.Cos(b), (float)Math.Sin(b), -c });
                }
                size = 1.65f; break;
            }
            case 12:
                foreach (int x in new int[] { 1, -1 }) foreach (int y in new int[] { 1, -1 }) foreach (int z in new int[] { 1, -1 }) v.Add(new float[] { x, y, z });
                foreach (int a in new int[] { 1, -1 }) foreach (int b in new int[] { 1, -1 })
                {
                    v.Add(new float[] { 0, a * ig, b * g }); v.Add(new float[] { a * ig, b * g, 0 }); v.Add(new float[] { a * g, 0, b * ig });
                }
                size = 1.55f; break;
            default:
                foreach (int a in new int[] { 1, -1 }) foreach (int b in new int[] { 1, -1 })
                {
                    v.Add(new float[] { 0, a, b * g }); v.Add(new float[] { a, b * g, 0 }); v.Add(new float[] { a * g, 0, b });
                }
                size = 1.55f; break;
        }
        Poly p = new Poly();
        p.Kind = kind; p.Size = size;
        // однаковий масштаб для всіх вершин (окреме нормування кожної зламало б пласкість граней к10)
        float maxL = 0;
        foreach (float[] q in v) maxL = Math.Max(maxL, (float)Math.Sqrt(Dot(q, q)));
        p.V = new float[v.Count][];
        for (int i = 0; i < v.Count; i++) p.V[i] = new float[] { v[i][0] / maxL, v[i][1] / maxL, v[i][2] / maxL };
        // грані: площини через трійки вершин, по один бік яких лежать усі інші
        List<int[]> faces = new List<int[]>();
        List<float[]> norms = new List<float[]>();
        HashSet<string> seen = new HashSet<string>();
        int n = p.V.Length;
        for (int a = 0; a < n; a++)
            for (int b = a + 1; b < n; b++)
                for (int c = b + 1; c < n; c++)
                {
                    float[] nn = Cross(Sub(p.V[b], p.V[a]), Sub(p.V[c], p.V[a]));
                    float l = (float)Math.Sqrt(Dot(nn, nn));
                    if (l < 1e-5f) continue;
                    nn = new float[] { nn[0] / l, nn[1] / l, nn[2] / l };
                    float d0 = Dot(nn, p.V[a]);
                    if (d0 < 0) { nn = new float[] { -nn[0], -nn[1], -nn[2] }; d0 = -d0; }
                    bool ok = true;
                    List<int> on = new List<int>();
                    for (int k = 0; k < n && ok; k++)
                    {
                        float dd = Dot(nn, p.V[k]) - d0;
                        if (dd > 1e-4f) ok = false;
                        else if (dd > -1e-4f) on.Add(k);
                    }
                    if (!ok) continue;
                    string key = string.Join(",", on.ConvertAll(delegate (int x) { return x.ToString(); }).ToArray());
                    if (!seen.Add(key)) continue;
                    faces.Add(on.ToArray());
                    norms.Add(nn);
                }
        int fc = faces.Count;
        p.F = new int[fc][]; p.N = new float[fc][]; p.C = new float[fc][]; p.Up = new float[fc][]; p.Inr = new float[fc]; p.Opp = new int[fc];
        for (int f = 0; f < fc; f++)
        {
            int[] vs = faces[f];
            float[] cc = new float[3];
            foreach (int k in vs) { cc[0] += p.V[k][0]; cc[1] += p.V[k][1]; cc[2] += p.V[k][2]; }
            cc = new float[] { cc[0] / vs.Length, cc[1] / vs.Length, cc[2] / vs.Length };
            float[] nn = norms[f];                // нормаль площини (у дельтоїда к10 вона не збігається з напрямом на центр грані)
            // упорядкувати вершини за кутом навколо центру
            float[] e1 = Nrm(Sub(p.V[vs[0]], cc)), e2 = Cross(nn, e1);
            int[] ord = (int[])vs.Clone();
            Array.Sort(ord, delegate (int x, int y)
            {
                float[] dx = Sub(p.V[x], cc), dy = Sub(p.V[y], cc);
                return Math.Atan2(Dot(dx, e2), Dot(dx, e1)).CompareTo(Math.Atan2(Dot(dy, e2), Dot(dy, e1)));
            });
            p.F[f] = ord; p.N[f] = nn; p.C[f] = cc;
            // «угору» - до найдальшої вершини (у дельтоїда к10 - до полюса)
            int far = ord[0]; float fd = -1;
            foreach (int k in ord) { float[] dv = Sub(p.V[k], cc); float dl = Dot(dv, dv); if (dl > fd + 1e-4f) { fd = dl; far = k; } }
            p.Up[f] = Nrm(Sub(p.V[far], cc));
            // вписане коло: найменша відстань від центру до ребра
            float inr = float.MaxValue;
            for (int i = 0; i < ord.Length; i++)
            {
                float[] a0 = p.V[ord[i]], b0 = p.V[ord[(i + 1) % ord.Length]];
                float[] ab = Sub(b0, a0), ac = Sub(cc, a0);
                float t = Dot(ac, ab) / Dot(ab, ab);
                float[] q = new float[] { a0[0] + ab[0] * t - cc[0], a0[1] + ab[1] * t - cc[1], a0[2] + ab[2] * t - cc[2] };
                inr = Math.Min(inr, (float)Math.Sqrt(Dot(q, q)));
            }
            p.Inr[f] = inr;
            p.Rest = Dot(nn, cc);
        }
        for (int f = 0; f < fc; f++)
        {
            p.Opp[f] = -1;
            for (int k = 0; k < fc; k++) if (k != f && Dot(p.N[f], p.N[k]) < -0.999f) p.Opp[f] = k;
        }
        return p;
    }

    // плоский силует для віджета на панелі (у квадраті r)
    public static void Outline(GraphicsPath gp, int kind, RectangleF r)
    {
        float x = r.X, y = r.Y, w = r.Width, h = r.Height;
        Func<float, float, PointF> P = delegate (float a, float b) { return new PointF(x + w * a, y + h * b); };
        switch (kind)
        {
            case 4: gp.AddPolygon(new PointF[] { P(0.5f, 0.04f), P(0.97f, 0.9f), P(0.03f, 0.9f) }); break;
            case 8: gp.AddPolygon(new PointF[] { P(0.5f, 0.02f), P(0.93f, 0.5f), P(0.5f, 0.98f), P(0.07f, 0.5f) }); break;
            case 10: gp.AddPolygon(new PointF[] { P(0.5f, 0.02f), P(0.97f, 0.46f), P(0.5f, 0.98f), P(0.03f, 0.46f) }); break;
            default:
            {
                int k = kind == 12 ? 5 : 6;
                PointF[] pts = new PointF[k];
                for (int i = 0; i < k; i++)
                {
                    double a = -Math.PI / 2 + i * 2 * Math.PI / k;
                    pts[i] = P(0.5f + 0.49f * (float)Math.Cos(a), (kind == 12 ? 0.54f : 0.5f) + 0.49f * (float)Math.Sin(a));
                }
                gp.AddPolygon(pts);
                break;
            }
        }
    }
}

// ----------------------------------------------------------------------
//  Об'ємний кидок по робочому столу: кожен кубик летить від панелі, підстрибує, котиться
//  й зупиняється випавшою гранню догори. Програмне 3D (поворотні матриці + перспектива),
//  вікно - лише навколо кубиків, тож перемальовується небагато.
// ----------------------------------------------------------------------
sealed class DiceThrow : LayeredForm
{
    static DiceThrow cur;

    sealed class Die
    {
        public float x0, y0, x1, y1, len, dirX, dirY;
        public float delay, dur, hop, tumble;
        public int style;                     // вид кидка (0-4)
        public float bounces, bounceEnd, tumEnd, rollK, spin, easeP;
        public float[] axisRoll, axisTumble, target;
        public int[] vals = new int[6];       // числа на гранях тіла: +X, -X, +Y, -Y, +Z, -Z
        public Poly poly;                     // інша форма (к4, к8, к10, к12, к20); null - звичайний кубик
        public int[] pv;                      // числа на гранях форми (у к4 - на вершинах)
        public float lift;                    // висота центру над столом, коли лежить
        public float x, y, z;
        public float[] rot;
    }

    // грані тіла кубика: нормаль і дотична (U); V = N × U
    static readonly float[][] FN = { new float[] { 1, 0, 0 }, new float[] { -1, 0, 0 }, new float[] { 0, 1, 0 }, new float[] { 0, -1, 0 }, new float[] { 0, 0, 1 }, new float[] { 0, 0, -1 } };
    static readonly float[][] FU = { new float[] { 0, 1, 0 }, new float[] { 0, -1, 0 }, new float[] { -1, 0, 0 }, new float[] { 1, 0, 0 }, new float[] { 1, 0, 0 }, new float[] { 1, 0, 0 } };
    // звичайний кубик: протилежні грані в сумі 7
    static readonly int[] D6 = { 2, 5, 3, 4, 1, 6 };

    readonly List<Die> dice = new List<Die>();
    readonly Timer timer = new Timer();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly Rectangle area;                 // робоча область монітора
    readonly float cx, cy, h, sinE, cosE, persp, sc;
    readonly int faces;
    // кольори кубика й підпису - з теми панелі
    readonly Color cBody = Color.FromArgb(250, 248, 242), cEdge = Color.FromArgb(206, 200, 188), cInk = Color.FromArgb(34, 34, 44), cOne = Color.FromArgb(200, 40, 50);
    readonly Color cCapBg = Color.FromArgb(24, 26, 34), cCapFg = Color.White, cCapEdge = Color.White;
    readonly string caption;
    readonly Action landed;
    float landAt;
    bool landedFired, still;
    volatile bool busy;
    Bitmap frame;
    Point frameAt;
    const float Hold = 3.6f, Fade = 0.5f;

    public static void Start(NeoBar bar, int[] res, int faces, string caption, Action landed)
    {
        if (cur != null && !cur.IsDisposed) cur.Finish();
        cur = new DiceThrow(bar, res, faces, caption, landed);
        cur.Show();
        cur.timer.Start();
        cur.Step();
    }

    DiceThrow(NeoBar bar, int[] res, int faces, string caption, Action landed)
    {
        ExtraEx = 0x20 | 0x8 | 0x08000000;     // кліки проходять крізь вікно, поверх усього, без фокуса
        TopMost = true;
        this.faces = faces;
        this.caption = caption;
        this.landed = landed;
        sc = bar != null ? bar.Dpi : 1f;
        if (bar != null)
        {
            try
            {
                Pal p = Pal.Of(bar.CurrentTheme, bar.LabelFont, sc);
                bool light = Util.Lum(p.Bg) > 140;
                cBody = Util.Blend(Util.Opaque(p.Bg), p.Fg, light ? 0.04f : 0.2f);
                cEdge = Util.Blend(cBody, p.Acc, 0.5f);
                cInk = p.Fg;
                cOne = p.Acc;
                cCapBg = Util.Opaque(p.Bg); cCapFg = p.Fg; cCapEdge = p.Acc;
            }
            catch { }
        }
        Screen scr = bar != null ? Screen.FromControl(bar) : Screen.PrimaryScreen;
        area = scr.WorkingArea;
        cx = area.X + area.Width / 2f;
        cy = area.Y + area.Height * 0.52f;
        h = 42f * sc;                          // половина ребра
        float elev = 58f * (float)Math.PI / 180f;
        sinE = (float)Math.Sin(elev); cosE = (float)Math.Cos(elev);
        persp = Math.Max(area.Width, area.Height) * 1.6f;
        string side = bar != null ? bar.Config.Side : "right";

        Random rnd = new Random(Dice.Next(int.MaxValue - 1));
        float halfW = area.Width / 2f - h * 3, halfY = (area.Height * 0.36f) / sinE;
        // де кубики зупиняться: у центрі столу, рядком під випадковим кутом
        float gap = h * 3.8f;
        double ang = rnd.NextDouble() * Math.PI;
        float bx = (float)(rnd.NextDouble() - 0.5) * halfW * 0.6f, by = (float)(rnd.NextDouble() - 0.5) * halfY * 0.5f;
        int n = res.Length;
        int[] styles = { 0, 1, 2, 3, 4 };
        for (int i = styles.Length - 1; i > 0; i--) { int j = rnd.Next(i + 1); int tmp = styles[i]; styles[i] = styles[j]; styles[j] = tmp; }
        for (int i = 0; i < n; i++)
        {
            Die d = new Die();
            float off = (i - (n - 1) / 2f) * gap;
            d.x1 = Math.Max(-halfW, Math.Min(halfW, bx + (float)Math.Cos(ang) * off + (float)(rnd.NextDouble() - 0.5) * h * 0.6f));
            d.y1 = Math.Max(-halfY * 0.8f, Math.Min(halfY * 0.8f, by + (float)Math.Sin(ang) * off + (float)(rnd.NextDouble() - 0.5) * h * 0.6f));
            // вид кидка: у кожного кубика свій (стилі перемішані, щоб на кількох кубиках не повторювались)
            d.style = styles[i % styles.Length];
            // звідки летить: від краю, де стоїть панель (деякі стилі - з іншого боку чи зверху)
            float spread = (float)(rnd.NextDouble() - 0.5);
            string from = d.style == 4 ? Opposite(side) : side;
            if (d.style == 1)
            {
                // падає майже вертикально поряд із місцем приземлення
                d.x0 = d.x1 + (float)(rnd.NextDouble() - 0.5) * h * 3.2f;
                d.y0 = d.y1 + (float)(rnd.NextDouble() - 0.5) * h * 2.4f;
            }
            else switch (from)
            {
                case "left": d.x0 = -area.Width / 2f + h * 2; d.y0 = d.y1 * 0.4f + spread * halfY * 0.6f; break;
                case "top": d.y0 = halfY * 1.15f; d.x0 = d.x1 * 0.4f + spread * halfW * 0.5f; break;
                case "bottom": d.y0 = -halfY * 1.05f; d.x0 = d.x1 * 0.4f + spread * halfW * 0.5f; break;
                default: d.x0 = area.Width / 2f - h * 2; d.y0 = d.y1 * 0.4f + spread * halfY * 0.6f; break;
            }
            float dx = d.x1 - d.x0, dy = d.y1 - d.y0;
            d.len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (d.len < 1) d.len = 1;
            d.dirX = dx / d.len; d.dirY = dy / d.len;
            d.delay = i * 0.035f + (float)rnd.NextDouble() * 0.03f;
            float rr = (float)rnd.NextDouble();
            switch (d.style)
            {
                case 1:  // падає зверху й кілька разів підскакує
                    d.dur = 1.35f + rr * 0.3f; d.hop = h * (7.5f + rr * 2.5f); d.bounces = 1.5f; d.bounceEnd = 0.55f; d.tumEnd = 0.6f;
                    d.tumble = (float)(Math.PI * 2 * (1.0 + rr)); d.rollK = 0.35f; d.spin = 0.6f + rr * 0.6f; d.easeP = 2.0f; break;
                case 2:  // ковзає низько й швидко крутиться навколо вертикалі
                    d.dur = 1.3f + rr * 0.3f; d.hop = h * (0.9f + rr * 0.6f); d.bounces = 1.5f; d.bounceEnd = 0.35f; d.tumEnd = 0.3f;
                    d.tumble = (float)(Math.PI * 2 * 0.25); d.rollK = 0.12f; d.spin = 2.2f + rr * 1.3f; d.easeP = 3.2f; break;
                case 3:  // високий навіс із багатьма обертами
                    d.dur = 1.8f + rr * 0.3f; d.hop = h * (9f + rr * 2.5f); d.bounces = 1.5f; d.bounceEnd = 0.78f; d.tumEnd = 0.75f;
                    d.tumble = (float)(Math.PI * 2 * (2.4 + rr * 1.2)); d.rollK = 0.5f; d.spin = 0.4f; d.easeP = 1.9f; break;
                case 4:  // з протилежного боку, жорсткі відскоки
                    d.dur = 1.6f + rr * 0.3f; d.hop = h * (5f + rr * 1.5f); d.bounces = 4.5f; d.bounceEnd = 0.82f; d.tumEnd = 0.6f;
                    d.tumble = (float)(Math.PI * 2 * (1.4 + rr * 0.6)); d.rollK = 0.6f; d.spin = 0f; d.easeP = 2.6f; break;
                default: // звичайне перекочування
                    d.dur = 1.55f + rr * 0.35f; d.hop = h * (3.2f + rr * 1.6f); d.bounces = 2.5f; d.bounceEnd = 0.62f; d.tumEnd = 0.55f;
                    d.tumble = (float)(Math.PI * 2 * (1.2 + rr)); d.rollK = 0.85f; d.spin = 0f; d.easeP = 2.3f; break;
            }
            d.axisRoll = new float[] { -d.dirY, d.dirX, 0 };
            d.axisTumble = Norm(new float[] { (float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f });

            // числа на гранях і кінцеве положення: грань із результатом - догори
            int top;
            d.poly = Poly.Get(Poly.KindFor(faces));
            d.lift = h;
            if (d.poly != null)
            {
                PolyValues(d, res[i], rnd);
                dice.Add(d);
                continue;
            }
            if (faces == 6)
            {
                for (int k = 0; k < 6; k++) d.vals[k] = D6[k];
                top = Array.IndexOf(D6, res[i]);
            }
            else
            {
                top = 4;
                List<int> used = new List<int>();
                used.Add(res[i]);
                for (int k = 0; k < 6; k++)
                {
                    if (k == top) { d.vals[k] = res[i]; continue; }
                    int v = rnd.Next(1, faces + 1);
                    for (int tries = 0; tries < 20 && used.Contains(v) && faces > 6; tries++) v = rnd.Next(1, faces + 1);
                    used.Add(v);
                    d.vals[k] = v;
                }
            }
            float[] align = AlignUp(top);
            // з числами - верхня грань читається прямо (з невеликим нахилом), з точками - будь-як
            float yaw = faces == 6 ? (float)(rnd.NextDouble() * Math.PI * 2) : (float)((rnd.NextDouble() - 0.5) * 0.6);
            d.target = Mul(RotZ(yaw), align);
            dice.Add(d);
        }
        float end = 0;
        foreach (Die d in dice) end = Math.Max(end, d.delay + d.dur);
        landAt = end;
        timer.Interval = 15;
        timer.Tick += delegate { try { Step(); } catch (Exception ex) { Util.ErrorLog(ex); Finish(); } };
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Stop();
        timer.Dispose();
        if (frame != null) { frame.Dispose(); frame = null; }
        if (!landedFired) { landedFired = true; if (landed != null) landed(); }
        if (cur == this) cur = null;
        base.OnFormClosed(e);
    }

    void Finish() { try { Close(); } catch { } }

    // ---------- кадр ----------
    void Step()
    {
        float t = (float)clock.Elapsed.TotalSeconds;
        if (t >= landAt && !landedFired) { landedFired = true; if (landed != null) landed(); }
        if (t >= landAt + Hold + Fade) { Finish(); return; }
        if (t >= landAt + 0.35f + 0.25f && frame != null)
        {
            if (busy) return;
            if (!still)
            {
                // останній кадр - кубики лежать, підпис проявився повністю
                still = true;
                foreach (Die d in dice) Pose(d, landAt + 1);
                Render(landAt + 1);
            }
            // кубики лежать і підпис уже проявився: далі лише згасання, без перемальовування
            float f = t - landAt - Hold;
            byte a = f <= 0 ? (byte)255 : (byte)Math.Max(0, (int)(255 * (1 - f / Fade)));
            if (a != PushAlpha) { PushAlpha = a; Refade(frame, frameAt); }
            if (f <= 0) timer.Interval = 100;           // нерухомо - рідше прокидатися
            else timer.Interval = 15;
            return;
        }
        // кадр рахується у фоновому потоці, поки інтерфейс не зайнятий: так кубики не «рвуться», коли їх багато
        if (busy) return;
        busy = true;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            Bitmap nb = null; Point at = Point.Empty;
            try
            {
                foreach (Die d in dice) Pose(d, t);
                if (!Compose(t, out nb, out at)) nb = null;
            }
            catch (Exception ex) { Util.ErrorLog(ex); nb = null; }
            try
            {
                BeginInvoke(new Action(delegate
                {
                    busy = false;
                    if (nb == null) return;
                    if (IsDisposed || landedFired && still) { nb.Dispose(); return; }
                    Present(nb, at);
                }));
            }
            catch { if (nb != null) nb.Dispose(); busy = false; }
        });
    }

    // контури чисел на гранях - один раз на значення (малювати їх щокадру надто дорого)
    static readonly Dictionary<string, GraphicsPath> glyphs = new Dictionary<string, GraphicsPath>();
    static FontFamily glyphFont;
    static GraphicsPath Glyph(string s, float em)
    {
        string key = s + "|" + em;
        lock (glyphs)
        {
            GraphicsPath p;
            if (glyphs.TryGetValue(key, out p)) return p;
            if (glyphFont == null) glyphFont = new FontFamily("Segoe UI");
            p = new GraphicsPath();
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                p.AddString(s, glyphFont, (int)FontStyle.Bold, em, new RectangleF(0, 2, 100, 100), sf);
            }
            if (glyphs.Count > 300) glyphs.Clear();
            glyphs[key] = p;
            return p;
        }
    }

    static string Opposite(string side) { return side == "left" ? "right" : side == "right" ? "left" : side == "top" ? "bottom" : "top"; }

    static float Ease(float x, float p) { x = Math.Max(0, Math.Min(1, x)); return 1 - (float)Math.Pow(1 - x, p); }

    void Pose(Die d, float t)
    {
        float k = Math.Max(0, Math.Min(1, (t - d.delay) / d.dur));
        float s = d.len * Ease(k, d.easeP);
        d.x = d.x0 + d.dirX * s;
        d.y = d.y0 + d.dirY * s;
        // підскоки, що згасають (кількість і висота залежать від виду кидка)
        float tb = d.bounceEnd;
        if (k < tb)
        {
            float q = k / tb;
            d.z = d.hop * (float)Math.Pow(1 - q, 1.5) * Math.Abs((float)Math.Cos(Math.PI * d.bounces * q));
        }
        else d.z = 0;
        // котиться без ковзання (кут прямує до нуля - кінцеве положення точне) + перекиди в повітрі + обертання навколо вертикалі
        float roll = -(d.len - s) / h * d.rollK;
        float tum = k < d.tumEnd ? d.tumble * (float)Math.Pow(1 - k / d.tumEnd, 2) : 0;
        float sp = d.spin > 0 ? d.spin * (float)(Math.PI * 2) * (1 - k) * (1 - k) : 0;
        d.rot = Mul(RotZ(sp), Mul(RotAxis(d.axisRoll, roll), Mul(RotAxis(d.axisTumble, tum), d.target)));
    }

    PointF Project(float x, float y, float z)
    {
        float depth = y * cosE - z * sinE;
        float f = persp / (persp + depth);
        return new PointF(cx + x * f, cy - (y * sinE + z * cosE) * f);
    }

    PointF ProjectBody(Die d, float bx, float by, float bz)
    {
        float[] r = d.rot;
        float wx = r[0] * bx + r[1] * by + r[2] * bz;
        float wy = r[3] * bx + r[4] * by + r[5] * bz;
        float wz = r[6] * bx + r[7] * by + r[8] * bz;
        return Project(d.x + wx, d.y + wy, d.z + d.lift + wz);
    }

    float[] World(Die d, float[] v)
    {
        float[] r = d.rot;
        return new float[] { r[0] * v[0] + r[1] * v[1] + r[2] * v[2], r[3] * v[0] + r[4] * v[1] + r[5] * v[2], r[6] * v[0] + r[7] * v[1] + r[8] * v[2] };
    }

    void Render(float t)
    {
        Bitmap nb; Point at;
        if (Compose(t, out nb, out at)) Present(nb, at);
    }

    void Present(Bitmap b, Point at)
    {
        PushAlpha = 255;
        Push(b, at);
        if (frame != null) frame.Dispose();
        frame = b;
        frameAt = at;
    }

    // кадр малюється без звернення до вікна, тому може виконуватись у фоновому потоці
    bool Compose(float t, out Bitmap result, out Point at)
    {
        result = null; at = Point.Empty;
        // межі кадру: кубики, тіні й підпис
        RectangleF bb = RectangleF.Empty;
        foreach (Die d in dice)
        {
            if (d.poly != null)
            {
                float S = h * d.poly.Size;
                foreach (float[] v in d.poly.V) bb = Grow(bb, PolyPt(d, v, S));
            }
            else
                for (int c = 0; c < 8; c++)
                {
                    PointF p = ProjectBody(d, (c & 1) != 0 ? h : -h, (c & 2) != 0 ? h : -h, (c & 4) != 0 ? h : -h);
                    bb = Grow(bb, p);
                }
            PointF sp = Project(d.x, d.y, 0);
            bb = Grow(bb, new PointF(sp.X - h * 2, sp.Y - h * 1.2f));
            bb = Grow(bb, new PointF(sp.X + h * 2, sp.Y + h * 1.2f));
        }
        float capA = Math.Max(0, Math.Min(1, (t - landAt - 0.35f) / 0.25f));
        RectangleF capR = RectangleF.Empty;
        Font capF = null;
        if (capA > 0 && caption.Length > 0)
        {
            capF = new Font("Segoe UI", 15f * sc, FontStyle.Bold, GraphicsUnit.Pixel);
            float top = float.MaxValue, mx = 0;
            foreach (Die d in dice)
            {
                PointF p = ProjectBody(d, 0, 0, h);
                // високі форми (к10, к12, к20) стирчать вище за звичайний кубик - підпис піднімається разом із ними
                float ext = d.poly != null ? Math.Max(0f, d.poly.Size * 1.15f - 1.1f) * h : 0f;
                top = Math.Min(top, p.Y - ext); mx += p.X;
            }
            mx /= dice.Count;
            Size ts = TextRenderer.MeasureText(caption, capF, new Size((int)(560 * sc), 1000), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            capR = new RectangleF(mx - ts.Width / 2f - 14 * sc, top - h * 1.1f - ts.Height - 16 * sc, ts.Width + 28 * sc, ts.Height + 14 * sc);
            if (capR.X < area.X + 8) capR.X = area.X + 8;
            if (capR.Right > area.Right - 8) capR.X = area.Right - 8 - capR.Width;
            if (capR.Y < area.Y + 8) capR.Y = area.Y + 8;
            bb = RectangleF.Union(bb, capR);
        }
        Rectangle box = Rectangle.Intersect(Rectangle.Inflate(Rectangle.Round(bb), 8, 8), area);
        if (box.Width < 2 || box.Height < 2) { if (capF != null) capF.Dispose(); return false; }

        Bitmap b = new Bitmap(box.Width, box.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            g.TranslateTransform(-box.X, -box.Y);
            // тіні - першими (усі під кубиками)
            foreach (Die d in dice) DrawShadow(g, d);
            // дальні кубики першими
            List<Die> order = new List<Die>(dice);
            order.Sort(delegate (Die a, Die c) { return (c.y * cosE).CompareTo(a.y * cosE); });
            foreach (Die d in order) { if (d.poly != null) DrawPoly(g, d); else DrawCube(g, d); }
            if (capF != null)
            {
                g.ResetTransform();
                RectangleF cr = new RectangleF(capR.X - box.X, capR.Y - box.Y, capR.Width, capR.Height);
                int a = (int)(capA * 255);
                using (GraphicsPath gp = Util.Round(cr, 12 * sc))
                {
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(a * 225 / 255, cCapBg))) g.FillPath(bg, gp);
                    using (Pen pn = new Pen(Color.FromArgb(a * 170 / 255, cCapEdge), 1.2f)) g.DrawPath(pn, gp);
                }
                using (StringFormat sf = new StringFormat())
                using (SolidBrush tb = new SolidBrush(Color.FromArgb(a, cCapFg)))
                {
                    sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(caption, capF, tb, cr, sf);
                }
                capF.Dispose();
            }
        }
        result = b; at = box.Location;
        return true;
    }

    static RectangleF Grow(RectangleF r, PointF p)
    {
        if (r.IsEmpty) return new RectangleF(p.X, p.Y, 0.01f, 0.01f);
        float l = Math.Min(r.Left, p.X), t = Math.Min(r.Top, p.Y), rr = Math.Max(r.Right, p.X), b = Math.Max(r.Bottom, p.Y);
        return RectangleF.FromLTRB(l, t, rr, b);
    }

    void DrawShadow(Graphics g, Die d)
    {
        PointF c = Project(d.x, d.y, 0);
        float lift = Math.Min(1, d.z / (h * 5));
        float rx = h * (1.45f + lift * 0.5f), ry = rx * sinE * 0.85f;
        int a = (int)(110 * (1 - lift * 0.7f));
        for (int i = 0; i < 3; i++)
        {
            float k = 1 + i * 0.18f;
            using (SolidBrush sb = new SolidBrush(Color.FromArgb(a / (i + 2), 0, 0, 0)))
                g.FillEllipse(sb, c.X - rx * k + h * 0.15f, c.Y - ry * k + h * 0.1f, rx * 2 * k, ry * 2 * k);
        }
    }

    static readonly float[] Light = Norm(new float[] { -0.45f, -0.35f, 1f });

    void DrawCube(Graphics g, Die d)
    {
        float[] toCam = { 0, -cosE, sinE };
        for (int f = 0; f < 6; f++)
        {
            float[] n = World(d, FN[f]);
            if (Dot(n, toCam) <= 0.02f) continue;
            float[] u = FU[f], v = Cross(FN[f], FU[f]), c = { FN[f][0] * h, FN[f][1] * h, FN[f][2] * h };
            // кути грані в просторі «0..100»: (0,0) - вгорі зліва, якщо дивитися на грань ззовні
            PointF p0 = ProjectBody(d, c[0] - u[0] * h + v[0] * h, c[1] - u[1] * h + v[1] * h, c[2] - u[2] * h + v[2] * h);
            PointF p1 = ProjectBody(d, c[0] + u[0] * h + v[0] * h, c[1] + u[1] * h + v[1] * h, c[2] + u[2] * h + v[2] * h);
            PointF p3 = ProjectBody(d, c[0] - u[0] * h - v[0] * h, c[1] - u[1] * h - v[1] * h, c[2] - u[2] * h - v[2] * h);
            float lum = 0.6f + 0.4f * Math.Max(0, Dot(n, Light));
            Color body = Shade(cBody, lum);
            Color edge = Shade(cEdge, lum);
            Color ink = cInk;
            using (Matrix m = new Matrix(new RectangleF(0, 0, 100, 100), new PointF[] { p0, p1, p3 }))
            {
                GraphicsState st = g.Save();
                g.MultiplyTransform(m);
                using (SolidBrush eb = new SolidBrush(edge)) g.FillRectangle(eb, -0.6f, -0.6f, 101.2f, 101.2f);
                using (GraphicsPath gp = Util.Round(new RectangleF(4, 4, 92, 92), 16))
                using (SolidBrush bb = new SolidBrush(body)) g.FillPath(bb, gp);
                int val = d.vals[f];
                if (faces == 6)
                {
                    float[] pp = Dice.PipsOf(val);
                    Color pc = val == 1 ? cOne : ink;
                    float r = val == 1 ? 11f : 8.5f;
                    using (SolidBrush pb = new SolidBrush(pc))
                        for (int i = 0; i + 1 < pp.Length; i += 2)
                            g.FillEllipse(pb, pp[i] * 100 - r, pp[i + 1] * 100 - r, r * 2, r * 2);
                }
                else
                {
                    string s = val.ToString() + ((val == 6 || val == 9) && faces >= 9 ? "." : "");
                    float em = s.Length >= 3 ? 40 : (s.Length == 2 ? 52 : 62);
                    using (SolidBrush tb = new SolidBrush(ink)) g.FillPath(tb, Glyph(s, em));
                }
                g.Restore(st);
            }
        }
    }

    static Color Shade(Color c, float k)
    {
        return Color.FromArgb(255, Math.Min(255, (int)(c.R * k)), Math.Min(255, (int)(c.G * k)), Math.Min(255, (int)(c.B * k)));
    }

    // ---------- 3×3 матриці (за рядками) ----------
    static float[] Mul(float[] a, float[] b)
    {
        float[] r = new float[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
        return r;
    }

    static float[] RotAxis(float[] a, float ang)
    {
        float c = (float)Math.Cos(ang), s = (float)Math.Sin(ang), t = 1 - c;
        float x = a[0], y = a[1], z = a[2];
        return new float[]
        {
            t * x * x + c,     t * x * y - s * z, t * x * z + s * y,
            t * x * y + s * z, t * y * y + c,     t * y * z - s * x,
            t * x * z - s * y, t * y * z + s * x, t * z * z + c
        };
    }

    static float[] RotZ(float a) { return RotAxis(new float[] { 0, 0, 1 }, a); }

    // поворот, що ставить напрям тіла n догори (+Z світу), а u - від глядача (+Y): число на верхній грані читається прямо
    static float[] Align(float[] n, float[] u)
    {
        float[] uu = Norm(new float[] { u[0] - n[0] * Dot(u, n), u[1] - n[1] * Dot(u, n), u[2] - n[2] * Dot(u, n) });
        float[] r1 = Cross(uu, n);
        return new float[] { r1[0], r1[1], r1[2], uu[0], uu[1], uu[2], n[0], n[1], n[2] };
    }

    // числа на гранях форми і кінцеве положення (результат - нагорі)
    void PolyValues(Die d, int res, Random rnd)
    {
        Poly p = d.poly;
        float S = h * p.Size;
        if (p.Kind == 4)
        {
            // к4: числа на вершинах; результат - вершина вгорі (на всіх видимих гранях біля неї те саме число)
            d.pv = new int[4];
            int topV = rnd.Next(4);
            List<int> rest = new List<int>();
            for (int v = 1; v <= 4; v++) if (v != res) rest.Add(v);
            for (int k = 0; k < 4; k++)
            {
                if (k == topV) { d.pv[k] = res; continue; }
                if (faces == 4) { int pick = rnd.Next(rest.Count); d.pv[k] = rest[pick]; rest.RemoveAt(pick); }
                else d.pv[k] = rnd.Next(1, faces + 1);
            }
            float[] up = p.V[topV], side = p.V[(topV + 1) % 4];
            d.lift = S * p.Rest;
            d.target = Mul(RotZ((float)((rnd.NextDouble() - 0.5) * 0.8)), Align(up, side));
            return;
        }
        int fc = p.F.Length;
        d.pv = new int[fc];
        int top = rnd.Next(fc);
        if (faces == fc)
        {
            // як на справжніх кубиках: протилежні грані в сумі дають кількість граней + 1
            bool[] done = new bool[fc];
            d.pv[top] = res; done[top] = true;
            if (p.Opp[top] >= 0) { d.pv[p.Opp[top]] = fc + 1 - res; done[p.Opp[top]] = true; }
            List<int> pairs = new List<int>();
            for (int v = 1; v <= fc / 2; v++) if (v != res && v != fc + 1 - res) pairs.Add(v);
            for (int f = 0; f < fc; f++)
            {
                if (done[f]) continue;
                int pick = rnd.Next(pairs.Count), v = pairs[pick];
                pairs.RemoveAt(pick);
                if (rnd.Next(2) == 1) v = fc + 1 - v;
                d.pv[f] = v; done[f] = true;
                if (p.Opp[f] >= 0 && !done[p.Opp[f]]) { d.pv[p.Opp[f]] = fc + 1 - v; done[p.Opp[f]] = true; }
            }
        }
        else
        {
            List<int> used = new List<int>();
            used.Add(res);
            for (int f = 0; f < fc; f++)
            {
                if (f == top) { d.pv[f] = res; continue; }
                int v = rnd.Next(1, faces + 1);
                for (int tries = 0; tries < 30 && used.Contains(v) && faces > fc; tries++) v = rnd.Next(1, faces + 1);
                used.Add(v);
                d.pv[f] = v;
            }
        }
        d.lift = S * p.Rest;
        d.target = Mul(RotZ((float)((rnd.NextDouble() - 0.5) * 0.6)), Align(p.N[top], p.Up[top]));
    }

    PointF PolyPt(Die d, float[] v, float s) { return ProjectBody(d, v[0] * s, v[1] * s, v[2] * s); }

    void PolyText(Graphics g, Die d, string s, float[] c, float[] up, float[] n, float half, float S, float em)
    {
        float[] right = Cross(up, n);
        Func<float, float, PointF> at = delegate (float a, float b)
        {
            return ProjectBody(d, (c[0] + right[0] * a + up[0] * b) * S, (c[1] + right[1] * a + up[1] * b) * S, (c[2] + right[2] * a + up[2] * b) * S);
        };
        using (Matrix m = new Matrix(new RectangleF(0, 0, 100, 100), new PointF[] { at(-half, half), at(half, half), at(-half, -half) }))
        {
            GraphicsState st = g.Save();
            g.MultiplyTransform(m);
            using (SolidBrush tb = new SolidBrush(cInk)) g.FillPath(tb, Glyph(s, em));
            g.Restore(st);
        }
    }

    static string Num(int v, int faces) { return v.ToString() + ((v == 6 || v == 9) && faces >= 9 ? "." : ""); }

    void DrawPoly(Graphics g, Die d)
    {
        Poly p = d.poly;
        float S = h * p.Size;
        float[] toCam = { 0, -cosE, sinE };
        for (int f = 0; f < p.F.Length; f++)
        {
            float[] n = World(d, p.N[f]);
            if (Dot(n, toCam) <= 0.02f) continue;
            int[] fv = p.F[f];
            float[] c = p.C[f];
            PointF[] outer = new PointF[fv.Length], inner = new PointF[fv.Length];
            for (int i = 0; i < fv.Length; i++)
            {
                float[] v = p.V[fv[i]];
                outer[i] = PolyPt(d, v, S);
                inner[i] = PolyPt(d, new float[] { c[0] + (v[0] - c[0]) * 0.88f, c[1] + (v[1] - c[1]) * 0.88f, c[2] + (v[2] - c[2]) * 0.88f }, S);
            }
            float lum = 0.6f + 0.4f * Math.Max(0, Dot(n, Light));
            using (SolidBrush eb = new SolidBrush(Shade(cEdge, lum))) g.FillPolygon(eb, outer);
            using (SolidBrush bb = new SolidBrush(Shade(cBody, lum))) g.FillPolygon(bb, inner);
            if (p.Kind == 4)
            {
                // біля кожної вершини грані - її число, «головою» до вершини
                foreach (int k in fv)
                {
                    float[] dv = Norm(new float[] { p.V[k][0] - c[0], p.V[k][1] - c[1], p.V[k][2] - c[2] });
                    float dist = (float)Math.Sqrt(Dot(new float[] { p.V[k][0] - c[0], p.V[k][1] - c[1], p.V[k][2] - c[2] }, new float[] { p.V[k][0] - c[0], p.V[k][1] - c[1], p.V[k][2] - c[2] }));
                    float[] pos = { c[0] + dv[0] * dist * 0.52f, c[1] + dv[1] * dist * 0.52f, c[2] + dv[2] * dist * 0.52f };
                    PolyText(g, d, Num(d.pv[k], faces), pos, dv, p.N[f], p.Inr[f] * 0.5f, S, 60);
                }
            }
            else
            {
                string s = Num(d.pv[f], faces);
                PolyText(g, d, s, c, p.Up[f], p.N[f], p.Inr[f] * (p.Kind == 10 ? 1.35f : (p.Kind == 20 ? 1.0f : 0.9f)), S, s.Length >= 3 ? 40 : (s.Length == 2 ? 50 : 60));
            }
        }
    }

    // поворот, що ставить грань тіла догори (+Z світу)
    static float[] AlignUp(int face)
    {
        float hp = (float)(Math.PI / 2);
        switch (face)
        {
            case 0: return RotAxis(new float[] { 0, 1, 0 }, -hp);     // +X -> +Z
            case 1: return RotAxis(new float[] { 0, 1, 0 }, hp);      // -X -> +Z
            case 2: return RotAxis(new float[] { 1, 0, 0 }, hp);      // +Y -> +Z
            case 3: return RotAxis(new float[] { 1, 0, 0 }, -hp);     // -Y -> +Z
            case 5: return RotAxis(new float[] { 1, 0, 0 }, (float)Math.PI);
            default: return RotAxis(new float[] { 0, 0, 1 }, 0);
        }
    }

    static float Dot(float[] a, float[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
    static float[] Cross(float[] a, float[] b) { return new float[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }
    static float[] Norm(float[] v) { float l = (float)Math.Sqrt(Dot(v, v)); if (l < 1e-6f) return new float[] { 0, 0, 1 }; return new float[] { v[0] / l, v[1] / l, v[2] / l }; }
}

// ----------------------------------------------------------------------
//  Розширена панель кубика: налаштування й кнопка «Кинути»
// ----------------------------------------------------------------------
sealed class DiceWindow : SbWindow
{
    TextBox tbLabels;
    bool labelsDirty;

    public DiceWindow(NeoBar bar) : base(bar, L.T("Кубик"), 440, 760)
    { Glyph = "\uE8B1";
        Dice.Load();
        Dice.Changed += OnData;
        tbLabels = MakeInput();
        tbLabels.Multiline = true;
        tbLabels.AcceptsReturn = true;
        tbLabels.ScrollBars = ScrollBars.None;           // власна смуга прокрутки в стилі теми, лише коли назв багато
        tbLabels.WordWrap = false;
        tbLabels.Text = string.Join("\r\n", Dice.Labels.ToArray());
        tbLabels.TextChanged += delegate { labelsDirty = true; CommitLabels(); Invalidate(); };
        tbLabels.Leave += delegate { CommitLabels(); };
    }

    void OnData() { try { if (!IsDisposed) Invalidate(); } catch { } }

    void CommitLabels()
    {
        if (!labelsDirty) return;
        labelsDirty = false;
        Dice.SetLabels(tbLabels.Text);
    }

    void Throw() { CommitLabels(); Dice.Roll(Bar); }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        CommitLabels();
        Dice.Changed -= OnData;
        base.OnFormClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!(ActiveControl is TextBox) && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)) { Throw(); e.Handled = true; return; }
        base.OnKeyDown(e);
    }

    static string FacesWord(int n)
    {
        int d = n % 10, h = n % 100;
        if (d == 1 && h != 11) return L.T("грань");
        if (d >= 2 && d <= 4 && (h < 12 || h > 14)) return L.T("грані");
        return L.T("граней");
    }

    void Stepper(Graphics g, Rectangle r, string title, string sub, int value, Action minus, Action plus, bool enabled)
    {
        TxtL(g, title, F, new Rectangle(r.X, r.Y, r.Width - Px(130), Px(22)), P.Fg);
        if (sub.Length > 0) TxtL(g, sub, FS, new Rectangle(r.X, r.Y + Px(21), r.Width - Px(130), Px(18)), P.Sub);
        int bw = Px(34), cy = r.Y + (r.Height - bw) / 2;
        Btn(g, new Rectangle(r.Right - bw * 3 - Px(8), cy, bw, bw), "−", minus, 0, enabled);
        TxtC(g, value.ToString(), FB, new Rectangle(r.Right - bw * 2 - Px(4), cy, bw, bw), P.Fg);
        Btn(g, new Rectangle(r.Right - bw, cy, bw, bw), "+", plus, 0, enabled);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        int y = r.Y;
        // останній результат і кидок
        Rectangle rc = new Rectangle(r.X, y, r.Width, Px(86));
        Card(g, rc);
        int[] v = Dice.Shown;
        string res = Dice.Rolling ? L.T("Кубик летить…") : (v.Length == 0 ? L.T("Ще не кидали") : Dice.Text(v));
        TxtL(g, Dice.Rolling || v.Length == 0 ? L.T("Результат") : L.T("Випало"), FS, new Rectangle(rc.X + Px(14), rc.Y + Px(8), rc.Width - Px(170), Px(18)), P.Sub);
        Txt(g, res, FB, new Rectangle(rc.X + Px(14), rc.Y + Px(26), rc.Width - Px(170), rc.Height - Px(32)), P.Fg,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        Btn(g, new Rectangle(rc.Right - Px(146), rc.Y + (rc.Height - Px(40)) / 2, Px(132), Px(40)), L.T("Кинути"), Throw, 1, !Dice.Rolling);
        y = rc.Bottom + Px(14);

        int rh = Px(44);
        Stepper(g, new Rectangle(r.X, y, r.Width, rh), L.T("Кубиків"), Dice.Locked ? L.T("З назвами - завжди один") : L.T("Скільки кидати разом"), Dice.Count,
            delegate { Dice.SetCount(Dice.Count - 1); }, delegate { Dice.SetCount(Dice.Count + 1); }, !Dice.Locked);
        y += rh + Px(4);
        bool lockF = Dice.Locked;
        Stepper(g, new Rectangle(r.X, y, r.Width, rh), L.T("Граней"), lockF ? L.T("Рахується за кількістю назв нижче") : L.T("Кубик на ") + Dice.Faces + " " + FacesWord(Dice.Faces), Dice.Faces,
            delegate { Dice.SetFaces(Dice.Faces - 1); }, delegate { Dice.SetFaces(Dice.Faces + 1); }, !lockF);
        y += rh + Px(4);
        if (!lockF)
        {
            int[] presets = { 4, 6, 8, 10, 12, 20 };
            int cw = (r.Width - Px(6) * (presets.Length - 1)) / presets.Length;
            for (int i = 0; i < presets.Length; i++)
            {
                int f = presets[i];
                Chip(g, new Rectangle(r.X + i * (cw + Px(6)), y, cw, Px(28)), L.T("к") + f, Dice.Faces == f, delegate { Dice.SetFaces(f); });
            }
            y += Px(40);
        }
        if (Dice.Count > 1)
        {
            TxtL(g, L.T("Результат"), F, new Rectangle(r.X, y, Px(120), Px(30)), P.Fg);
            int cw2 = (r.Width - Px(130) - Px(6)) / 2;
            Chip(g, new Rectangle(r.X + Px(130), y + Px(1), cw2, Px(28)), L.T("Сума"), Dice.Total, delegate { Dice.SetTotal(true); });
            Chip(g, new Rectangle(r.X + Px(130) + cw2 + Px(6), y + Px(1), cw2, Px(28)), L.T("Кожен окремо"), !Dice.Total, delegate { Dice.SetTotal(false); });
            y += Px(38);
        }

        using (Pen lp = new Pen(P.Line, 1f)) g.DrawLine(lp, r.X, y, r.Right, y);
        y += Px(10);
        TxtL(g, L.T("Свої назви"), FB, new Rectangle(r.X, y, r.Width, Px(22)), P.Fg);
        y += Px(22);
        TxtL(g, L.T("Один рядок - одна грань (рядок 1 - число 1)"), FS, new Rectangle(r.X, y, r.Width, Px(18)), P.Sub);
        y += Px(24);
        // історія внизу: 10 рядків по Px(18), лише в пам'яті
        int histH = Px(24) + 10 * Px(18) + Px(6);
        Rectangle fr = new Rectangle(r.X, y, r.Width, Math.Max(Px(70), r.Bottom - y - histH - Px(8)));
        MultiField(g, tbLabels, fr);
        int hy = fr.Bottom + Px(8);
        using (Pen lp = new Pen(P.Line, 1f)) g.DrawLine(lp, r.X, hy, r.Right, hy);
        hy += Px(4);
        TxtL(g, L.T("Історія кидків (") + Dice.History.Count + "/10)", FB, new Rectangle(r.X, hy, r.Width - Px(110), Px(22)), P.Fg);
        Btn(g, new Rectangle(r.Right - Px(100), hy, Px(100), Px(22)), L.T("Очистити"), delegate { Dice.History.Clear(); Invalidate(); }, 0, Dice.History.Count > 0);
        hy += Px(24);
        if (Dice.History.Count == 0) TxtL(g, L.T("Поки порожньо (зникає при виході з NeoBar)"), FS, new Rectangle(r.X, hy, r.Width, Px(18)), P.Sub);
        for (int i = 0; i < Dice.History.Count && i < 10; i++)
            TxtL(g, Dice.History[i], FS, new Rectangle(r.X, hy + i * Px(18), r.Width, Px(18)), i == 0 ? P.Fg : P.Sub);
    }
}
