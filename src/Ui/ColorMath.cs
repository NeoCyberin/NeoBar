using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

// Віджет «Палітра»: стан і перетворення між колірними моделями (вікно - ColorWin.cs)

// поточний колір, вигляд палітри та історія (зберігаються в рядку віджета: "RRGGBB;вид;h1,h2,...")
static class ColorState
{
    public static Color Cur = Color.FromArgb(66, 135, 245);
    public static int Mode;
    public static readonly int[] Recent = { 0, 1, 2 };   // три останні скопійовані моделі (для вкладки «Вибір»)
    public static readonly List<Color> Hist = new List<Color>();
    public const int HistMax = 14;
    static string loaded;

    public static void Load(string opt)
    {
        if (opt == null) opt = "";
        if (opt == loaded) return;
        loaded = opt;
        string[] p = opt.Split(';');
        Color c;
        if (p.Length > 0 && ColorMath.TryHex(p[0], out c)) Cur = c;
        int m;
        if (p.Length > 1 && int.TryParse(p[1], out m)) Mode = Math.Max(0, Math.Min(ColorWindow.ModeNames.Length - 1, m));
        if (p.Length > 3)
        {
            string[] rs = p[3].Split(',');
            if (rs.Length == 3)
            {
                int[] tmp = new int[3]; bool ok = true;
                for (int i = 0; i < 3; i++) { int v; if (!int.TryParse(rs[i], out v) || v < 0 || v >= ColorMath.Models.Length) ok = false; else tmp[i] = v; }
                if (ok) { Recent[0] = tmp[0]; Recent[1] = tmp[1]; Recent[2] = tmp[2]; }
            }
        }
        Hist.Clear();
        if (p.Length > 2)
            foreach (string h in p[2].Split(','))
                if (ColorMath.TryHex(h, out c) && Hist.Count < HistMax) Hist.Add(c);
    }

    public static string Opt
    {
        get
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(ColorMath.Hex(Cur).TrimStart('#')).Append(';').Append(Mode).Append(';');
            for (int i = 0; i < Hist.Count; i++) { if (i > 0) sb.Append(','); sb.Append(ColorMath.Hex(Hist[i]).TrimStart('#')); }
            sb.Append(';').Append(Recent[0]).Append(',').Append(Recent[1]).Append(',').Append(Recent[2]);
            loaded = sb.ToString();
            return loaded;
        }
    }

    public static void UsedModel(int m)
    {
        int[] n = new int[3]; n[0] = m; int k = 1;
        for (int i = 0; i < 3 && k < 3; i++) if (Recent[i] != m) n[k++] = Recent[i];
        for (int i = 0; i < 3; i++) Recent[i] = n[i];
    }

    public static void Remember(Color c)
    {
        c = Color.FromArgb(255, c);
        for (int i = Hist.Count - 1; i >= 0; i--) if (Hist[i].ToArgb() == c.ToArgb()) Hist.RemoveAt(i);
        Hist.Insert(0, c);
        while (Hist.Count > HistMax) Hist.RemoveAt(Hist.Count - 1);
    }
}

// ----------------------------------------------------------------------
//  Перетворення між колірними моделями
// ----------------------------------------------------------------------
static class ColorMath
{
    static readonly CultureInfo IC = CultureInfo.InvariantCulture;

    public static readonly string[] Models =
    {
        "HEX", "RGB", "HSL", "HSV / HSB", "HWB", "CMYK", "CIE XYZ", "CIE Lab", "CIE LCh", "OKLab", "OKLCH",
        L.T("Десяткове"), "Win32 COLORREF", L.T("Назва (CSS)")
    };

    static int Cl(double v) { return (int)Math.Max(0, Math.Min(255, Math.Round(v))); }
    static double Clamp01(double v) { return Math.Max(0, Math.Min(1, v)); }
    static string N(double v, string fmt) { return v.ToString(fmt, IC); }

    public static string Hex(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

    public static bool TryHex(string s, out Color c)
    {
        c = Color.Empty;
        if (s == null) return false;
        s = s.Trim().TrimStart('#');
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        if (s.Length == 3) s = new string(new char[] { s[0], s[0], s[1], s[1], s[2], s[2] });
        if (s.Length == 8) s = s.Substring(2);                     // AARRGGBB - прозорість відкидаємо
        if (s.Length != 6) return false;
        int v;
        if (!int.TryParse(s, NumberStyles.HexNumber, IC, out v)) return false;
        c = Color.FromArgb(255, (v >> 16) & 255, (v >> 8) & 255, v & 255);
        return true;
    }

    // ---------- HSV / HSL / HWB ----------
    public static void ToHsv(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), d = mx - mn;
        h = Hue(r, g, b, mx, d);
        s = mx <= 0 ? 0 : d / mx;
        v = mx;
    }

    static double Hue(double r, double g, double b, double mx, double d)
    {
        if (d <= 1e-9) return 0;
        double h;
        if (mx == r) h = ((g - b) / d) % 6;
        else if (mx == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h *= 60;
        if (h < 0) h += 360;
        return h;
    }

    public static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360; s = Clamp01(s); v = Clamp01(v);
        double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return Color.FromArgb(255, Cl((r + m) * 255), Cl((g + m) * 255), Cl((b + m) * 255));
    }

    public static void ToHsl(Color c, out double h, out double s, out double l)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), d = mx - mn;
        h = Hue(r, g, b, mx, d);
        l = (mx + mn) / 2;
        s = d <= 1e-9 ? 0 : d / (1 - Math.Abs(2 * l - 1));
    }

    public static Color FromHsl(double h, double s, double l)
    {
        s = Clamp01(s); l = Clamp01(l);
        double v = l + s * Math.Min(l, 1 - l);
        double sv = v <= 0 ? 0 : 2 * (1 - l / v);
        return FromHsv(h, sv, v);
    }

    public static Color FromHwb(double h, double w, double bl)
    {
        w = Clamp01(w); bl = Clamp01(bl);
        if (w + bl >= 1) { int gr = Cl(w / (w + bl) * 255); return Color.FromArgb(255, gr, gr, gr); }
        double v = 1 - bl;
        return FromHsv(h, 1 - w / v, v);
    }

    // ---------- CIE (sRGB, D65) ----------
    static double Lin(double u) { u /= 255.0; return u <= 0.04045 ? u / 12.92 : Math.Pow((u + 0.055) / 1.055, 2.4); }
    static int Gam(double v) { v = v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(Math.Max(0, v), 1 / 2.4) - 0.055; return Cl(v * 255); }

    public static void ToXyz(Color c, out double x, out double y, out double z)
    {
        double r = Lin(c.R), g = Lin(c.G), b = Lin(c.B);
        x = (0.4124564 * r + 0.3575761 * g + 0.1804375 * b) * 100;
        y = (0.2126729 * r + 0.7151522 * g + 0.0721750 * b) * 100;
        z = (0.0193339 * r + 0.1191920 * g + 0.9503041 * b) * 100;
    }

    public static Color FromXyz(double x, double y, double z)
    {
        x /= 100; y /= 100; z /= 100;
        double r = 3.2404542 * x - 1.5371385 * y - 0.4985314 * z;
        double g = -0.9692660 * x + 1.8760108 * y + 0.0415560 * z;
        double b = 0.0556434 * x - 0.2040259 * y + 1.0572252 * z;
        return Color.FromArgb(255, Gam(r), Gam(g), Gam(b));
    }

    const double Xn = 95.047, Yn = 100.0, Zn = 108.883;
    static double F(double t) { return t > 0.008856 ? Math.Pow(t, 1.0 / 3) : 7.787 * t + 16.0 / 116; }
    static double Fi(double f) { double f3 = f * f * f; return f3 > 0.008856 ? f3 : (f - 16.0 / 116) / 7.787; }

    public static void ToLab(Color c, out double L, out double a, out double b)
    {
        double x, y, z;
        ToXyz(c, out x, out y, out z);
        double fx = F(x / Xn), fy = F(y / Yn), fz = F(z / Zn);
        L = 116 * fy - 16; a = 500 * (fx - fy); b = 200 * (fy - fz);
    }

    public static Color FromLab(double L, double a, double b)
    {
        double fy = (L + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
        return FromXyz(Fi(fx) * Xn, Fi(fy) * Yn, Fi(fz) * Zn);
    }

    // ---------- OKLab ----------
    public static void ToOklab(Color c, out double L, out double a, out double b)
    {
        double r = Lin(c.R), g = Lin(c.G), bb = Lin(c.B);
        double l = Math.Pow(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * bb, 1.0 / 3);
        double m = Math.Pow(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * bb, 1.0 / 3);
        double s = Math.Pow(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * bb, 1.0 / 3);
        L = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
        a = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
        b = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
    }

    public static Color FromOklab(double L, double a, double b)
    {
        double l = L + 0.3963377774 * a + 0.2158037573 * b;
        double m = L - 0.1055613458 * a - 0.0638541728 * b;
        double s = L - 0.0894841775 * a - 1.2914855480 * b;
        l = l * l * l; m = m * m * m; s = s * s * s;
        double r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
        double g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
        double bb = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;
        return Color.FromArgb(255, Gam(r), Gam(g), Gam(bb));
    }

    static void Polar(double a, double b, out double c, out double h)
    {
        c = Math.Sqrt(a * a + b * b);
        h = Math.Atan2(b, a) * 180 / Math.PI;
        if (h < 0) h += 360;
        if (c < 1e-4) h = 0;
    }

    // ---------- назви ----------
    static List<KeyValuePair<string, Color>> names;

    static List<KeyValuePair<string, Color>> Names
    {
        get
        {
            if (names == null)
            {
                names = new List<KeyValuePair<string, Color>>();
                foreach (KnownColor k in Enum.GetValues(typeof(KnownColor)))
                {
                    Color c = Color.FromKnownColor(k);
                    if (c.IsSystemColor || c.A < 255) continue;
                    names.Add(new KeyValuePair<string, Color>(c.Name, c));
                }
            }
            return names;
        }
    }

    public static string NearestName(Color c)
    {
        double best = double.MaxValue; string bn = "";
        double L, a, b;
        ToLab(c, out L, out a, out b);
        foreach (KeyValuePair<string, Color> kv in Names)
        {
            double L2, a2, b2;
            ToLab(kv.Value, out L2, out a2, out b2);
            double d = (L - L2) * (L - L2) + (a - a2) * (a - a2) + (b - b2) * (b - b2);
            if (d < best) { best = d; bn = kv.Key; }
            if (d == 0) break;
        }
        return best < 0.01 ? bn : "≈ " + bn;
    }

    // ---------- текстове подання ----------
    public static string Format(int m, Color c)
    {
        double h, s, v, l, x, y, z, L, a, b, ch;
        switch (m)
        {
            case 0: return Hex(c);
            case 1: return "rgb(" + c.R + ", " + c.G + ", " + c.B + ")";
            case 2: ToHsl(c, out h, out s, out l); return "hsl(" + N(h, "0") + ", " + N(s * 100, "0") + "%, " + N(l * 100, "0") + "%)";
            case 3: ToHsv(c, out h, out s, out v); return "hsv(" + N(h, "0") + ", " + N(s * 100, "0") + "%, " + N(v * 100, "0") + "%)";
            case 4: ToHsv(c, out h, out s, out v); return "hwb(" + N(h, "0") + " " + N((1 - s) * v * 100, "0") + "% " + N((1 - v) * 100, "0") + "%)";
            case 5:
            {
                double r = c.R / 255.0, g = c.G / 255.0, bb = c.B / 255.0, k = 1 - Math.Max(r, Math.Max(g, bb));
                double cc = k >= 1 ? 0 : (1 - r - k) / (1 - k), mm = k >= 1 ? 0 : (1 - g - k) / (1 - k), yy = k >= 1 ? 0 : (1 - bb - k) / (1 - k);
                return "cmyk(" + N(cc * 100, "0") + "%, " + N(mm * 100, "0") + "%, " + N(yy * 100, "0") + "%, " + N(k * 100, "0") + "%)";
            }
            case 6: ToXyz(c, out x, out y, out z); return "xyz(" + N(x, "0.00") + ", " + N(y, "0.00") + ", " + N(z, "0.00") + ")";
            case 7: ToLab(c, out L, out a, out b); return "lab(" + N(L, "0.00") + " " + N(a, "0.00") + " " + N(b, "0.00") + ")";
            case 8: ToLab(c, out L, out a, out b); Polar(a, b, out ch, out h); return "lch(" + N(L, "0.00") + " " + N(ch, "0.00") + " " + N(h, "0.0") + ")";
            case 9: ToOklab(c, out L, out a, out b); return "oklab(" + N(L, "0.000") + " " + N(a, "0.000") + " " + N(b, "0.000") + ")";
            case 10: ToOklab(c, out L, out a, out b); Polar(a, b, out ch, out h); return "oklch(" + N(L, "0.000") + " " + N(ch, "0.000") + " " + N(h, "0.0") + ")";
            case 11: return ((c.R << 16) | (c.G << 8) | c.B).ToString(IC);
            case 12: return "0x00" + c.B.ToString("X2") + c.G.ToString("X2") + c.R.ToString("X2");
            case 13: return NearestName(c);
        }
        return "";
    }

    static List<double> Nums(string s)
    {
        List<double> r = new List<double>();
        foreach (Match mt in Regex.Matches(s, @"-?\d+(?:\.\d+)?"))
        {
            double d;
            if (double.TryParse(mt.Value, NumberStyles.Float, IC, out d)) r.Add(d);
        }
        return r;
    }

    // розбір відредагованого значення; false - не вдалося
    public static bool Parse(int m, string s, out Color c)
    {
        c = Color.Empty;
        if (s == null) return false;
        s = s.Trim();
        if (s.Length == 0) return false;
        if (m == 0) return TryHex(s, out c);
        if (m == 13)
        {
            string nm = s.TrimStart('≈', ' ');
            Color k = Color.FromName(nm);
            if (!k.IsKnownColor || k.IsSystemColor) return TryHex(s, out c);
            c = Color.FromArgb(255, k);
            return true;
        }
        if (m == 11)
        {
            long dv;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.StartsWith("#")) return TryHex(s, out c);
            if (!long.TryParse(s, NumberStyles.Integer, IC, out dv) || dv < 0 || dv > 0xFFFFFF) return false;
            c = Color.FromArgb(255, (int)(dv >> 16) & 255, (int)(dv >> 8) & 255, (int)dv & 255);
            return true;
        }
        if (m == 12)
        {
            string hs = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s.Substring(2) : s.TrimStart('#');
            int v;
            if (!int.TryParse(hs, NumberStyles.HexNumber, IC, out v)) return false;
            c = Color.FromArgb(255, v & 255, (v >> 8) & 255, (v >> 16) & 255);
            return true;
        }
        List<double> n = Nums(s);
        int need = m == 5 ? 4 : 3;
        if (n.Count < need) return false;
        double p0 = n[0], p1 = n[1], p2 = n[2];
        switch (m)
        {
            case 1: c = Color.FromArgb(255, Cl(p0), Cl(p1), Cl(p2)); return true;
            case 2: c = FromHsl(p0, p1 / 100, p2 / 100); return true;
            case 3: c = FromHsv(p0, p1 / 100, p2 / 100); return true;
            case 4: c = FromHwb(p0, p1 / 100, p2 / 100); return true;
            case 5:
            {
                double k = Clamp01(n[3] / 100);
                c = Color.FromArgb(255, Cl(255 * (1 - Clamp01(p0 / 100)) * (1 - k)), Cl(255 * (1 - Clamp01(p1 / 100)) * (1 - k)), Cl(255 * (1 - Clamp01(p2 / 100)) * (1 - k)));
                return true;
            }
            case 6: c = FromXyz(p0, p1, p2); return true;
            case 7: c = FromLab(p0, p1, p2); return true;
            case 8: c = FromLab(p0, p1 * Math.Cos(p2 * Math.PI / 180), p1 * Math.Sin(p2 * Math.PI / 180)); return true;
            case 9: if (s.Contains("%") || p0 > 1.5) p0 /= 100; c = FromOklab(p0, p1, p2); return true;
            case 10: if (s.Contains("%") || p0 > 1.5) p0 /= 100; c = FromOklab(p0, p1 * Math.Cos(p2 * Math.PI / 180), p1 * Math.Sin(p2 * Math.PI / 180)); return true;
        }
        return false;
    }

    public static Color Ink(Color bg) { return Util.Lum(bg) > 150 ? Color.FromArgb(20, 20, 24) : Color.White; }
}
