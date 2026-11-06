using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

// ======================================================================
//  Прапорці мов (малюються, без картинок) і меню вибору мови.
//  Опис у файлі мови: "h #колір #колір…" - смуги згори вниз, "v …" - зліва направо;
//  окремі малюнки: us, cz, br. Прапорець без опису - сірий з кодом мови.
// ======================================================================
static class Flags
{
    public static void Draw(Graphics g, Rectangle r, string spec, string code)
    {
        SmoothingMode sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int rad = Math.Max(2, r.Height / 5);
        using (GraphicsPath clip = Util.Round(r, rad))
        {
            Region old = g.Clip;
            g.SetClip(clip, CombineMode.Intersect);
            Paint(g, r, (spec ?? "").Trim(), code ?? "");
            g.Clip = old;
            using (Pen p = new Pen(Color.FromArgb(70, 0, 0, 0), 1f)) g.DrawPath(p, clip);
        }
        g.SmoothingMode = sm;
    }

    static void Paint(Graphics g, Rectangle r, string spec, string code)
    {
        string[] p = spec.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        string kind = p.Length > 0 ? p[0].ToLowerInvariant() : "";
        if (kind == "us") { Us(g, r); return; }
        if (kind == "cz") { Cz(g, r); return; }
        if (kind == "br") { Br(g, r); return; }
        List<Color> cs = new List<Color>();
        for (int i = 1; i < p.Length; i++) { Color c = Util.ParseColor(p[i]); if (!c.IsEmpty) cs.Add(Color.FromArgb(255, c)); }
        if ((kind == "h" || kind == "v") && cs.Count > 0)
        {
            int n = cs.Count;
            for (int i = 0; i < n; i++)
            {
                RectangleF s = kind == "h"
                    ? new RectangleF(r.X, r.Y + r.Height * i / (float)n, r.Width, r.Height / (float)n + 1)
                    : new RectangleF(r.X + r.Width * i / (float)n, r.Y, r.Width / (float)n + 1, r.Height);
                using (SolidBrush b = new SolidBrush(cs[i])) g.FillRectangle(b, s);
            }
            return;
        }
        using (SolidBrush b = new SolidBrush(Color.FromArgb(120, 128, 140))) g.FillRectangle(b, r);
        string t = code.Split('-')[0].ToUpperInvariant();
        using (Font f = new Font("Segoe UI", Math.Max(5f, r.Height * 0.42f), FontStyle.Bold, GraphicsUnit.Pixel))
            TextRenderer.DrawText(g, t, f, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    static void Us(Graphics g, Rectangle r)
    {
        Color red = Color.FromArgb(178, 34, 52), blue = Color.FromArgb(60, 59, 110);
        using (SolidBrush w = new SolidBrush(Color.White)) g.FillRectangle(w, r);
        using (SolidBrush b = new SolidBrush(red))
            for (int i = 0; i < 13; i += 2) g.FillRectangle(b, new RectangleF(r.X, r.Y + r.Height * i / 13f, r.Width, r.Height / 13f));
        RectangleF c = new RectangleF(r.X, r.Y, r.Width * 0.42f, r.Height * 7 / 13f);
        using (SolidBrush b = new SolidBrush(blue)) g.FillRectangle(b, c);
        // зірки - крапками (у малому розмірі однаково не видно)
        if (r.Height >= 12)
            using (SolidBrush w = new SolidBrush(Color.FromArgb(230, 255, 255, 255)))
            {
                float d = Math.Max(1f, r.Height / 14f);
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 5; x++)
                        g.FillEllipse(w, c.X + c.Width * (x + 0.5f + (y % 2) * 0.25f) / 5.5f - d / 2, c.Y + c.Height * (y + 0.5f) / 4f - d / 2, d, d);
            }
    }

    static void Cz(Graphics g, Rectangle r)
    {
        using (SolidBrush w = new SolidBrush(Color.White)) g.FillRectangle(w, r.X, r.Y, r.Width, r.Height / 2f);
        using (SolidBrush b = new SolidBrush(Color.FromArgb(215, 20, 26))) g.FillRectangle(b, r.X, r.Y + r.Height / 2f, r.Width, r.Height / 2f + 1);
        using (SolidBrush b = new SolidBrush(Color.FromArgb(17, 69, 126)))
            g.FillPolygon(b, new[] { new PointF(r.X, r.Y), new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f), new PointF(r.X, r.Bottom) });
    }

    static void Br(Graphics g, Rectangle r)
    {
        using (SolidBrush b = new SolidBrush(Color.FromArgb(0, 156, 59))) g.FillRectangle(b, r);
        float mx = r.Width * 0.09f, my = r.Height * 0.12f, cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 223, 0)))
            g.FillPolygon(b, new[] { new PointF(cx, r.Y + my), new PointF(r.Right - mx, cy), new PointF(cx, r.Bottom - my), new PointF(r.X + mx, cy) });
        float rr = r.Height * 0.27f;
        using (SolidBrush b = new SolidBrush(Color.FromArgb(0, 39, 118))) g.FillEllipse(b, cx - rr, cy - rr, rr * 2, rr * 2);
    }

    public static Bitmap Make(LangInfo li, int w, int h)
    {
        Bitmap b = new Bitmap(w, h);
        using (Graphics g = Graphics.FromImage(b)) Draw(g, new Rectangle(0, 0, w - 1, h - 1), li.Flag, li.Code);
        return b;
    }

    // назва мови в меню: для неповних власних перекладів - відсоток
    public static string Title(LangInfo li)
    {
        string s = li.Name;
        if (!li.Builtin && li.Total > 0 && li.Done < li.Total) s += "  (" + (li.Done * 100 / li.Total) + "%)";
        return s;
    }

    // меню вибору мови; вибір зберігається, після чого викликається done(true - мову змінено)
    public static void Menu(Control owner, Point at, Color bg, Color fg, Color acc, int rad, Action<bool> done)
    {
        ContextMenuStrip m = new ContextMenuStrip();
        m.ForeColor = fg;
        m.ImageScalingSize = new Size(SUi.S(24), SUi.S(16));
        RoundMenu.Apply(m, bg, fg, acc, Util.Blend(bg, fg, 0.22f), rad);
        foreach (LangInfo li in L.All())
        {
            LangInfo me = li;
            ToolStripMenuItem it = new ToolStripMenuItem(Title(li), Make(li, SUi.S(24), SUi.S(16)));
            it.ForeColor = fg;
            it.Checked = string.Equals(li.Code, L.Code, StringComparison.OrdinalIgnoreCase);
            it.Click += delegate
            {
                bool changed = !string.Equals(me.Code, L.Code, StringComparison.OrdinalIgnoreCase);
                L.Save(me.Code);
                if (done != null) done(changed);
            };
            m.Items.Add(it);
        }
        m.Closed += delegate { owner.BeginInvoke(new Action(m.Dispose)); };
        m.Show(owner, at);
    }
}
