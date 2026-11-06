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

// Тло нотаток (візерунки за темою) і діалог вибору теми нотатки

static class NoteBackdrop
{
    static int P(float v) { return Notes.Px(v); }

    public static void Draw(Graphics g, Rectangle r, NoteTheme t, int seed)
    {
        SmoothingMode old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Random rnd = new Random(seed);
        GraphicsState st = g.Save();
        g.SetClip(r);
        try
        {
            switch (t.Id)
            {
                case "dark": Stars(g, r, t, rnd); break;
                case "light": Ruled(g, r, t); break;
                case "glass": Glass(g, r); break;
                case "cyber": Glitch(g, r, t, rnd); break;
                case "steam": Gears(g, r, t); break;
                case "urban": Alarm(g, r, t); break;
                case "pink": Hearts(g, r, t, rnd); break;
                case "nature": Leaves(g, r, t, rnd); break;
                case "art": Hatch(g, r, t); break;
                case "halloween": case "xmas": case "easter": case "birthday":
                    Festive.Decor(g, Themes.Get(t.Id, null), r, Notes.Px(100) / 100f, true); break;
                default: Dots(g, r, t); break;
            }
        }
        finally { g.Restore(st); g.SmoothingMode = old; }
    }

    static void Stars(Graphics g, Rectangle r, NoteTheme t, Random rnd)
    {
        for (int i = 0; i < 30; i++)
        {
            float x = r.X + (float)rnd.NextDouble() * r.Width, y = r.Y + (float)rnd.NextDouble() * r.Height;
            float s = rnd.Next(10) < 8 ? 1.2f : 2.2f;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(24 + rnd.Next(30), t.Fg)))
                g.FillEllipse(b, x, y, s, s);
        }
    }

    static void Ruled(Graphics g, Rectangle r, NoteTheme t)
    {
        using (Pen p = new Pen(Color.FromArgb(18, t.Fg), 1f))
            for (int y = r.Y + P(36); y < r.Bottom; y += P(24)) g.DrawLine(p, r.X + P(6), y, r.Right - P(6), y);
        using (Pen p = new Pen(Color.FromArgb(26, t.Header), 1f))
            g.DrawLine(p, r.X + P(30), r.Y, r.X + P(30), r.Bottom);
    }

    static void Glass(Graphics g, Rectangle r)
    {
        using (LinearGradientBrush lb = new LinearGradientBrush(new Rectangle(r.X, r.Y, r.Width, Math.Max(2, r.Height / 2)),
            Color.FromArgb(30, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
            g.FillRectangle(lb, r.X, r.Y, r.Width, Math.Max(2, r.Height / 2));
        PointF[] a = { new PointF(r.X + r.Width * 0.55f, r.Y), new PointF(r.X + r.Width * 0.75f, r.Y),
                       new PointF(r.X + r.Width * 0.35f, r.Bottom), new PointF(r.X + r.Width * 0.15f, r.Bottom) };
        using (SolidBrush b = new SolidBrush(Color.FromArgb(12, 255, 255, 255))) g.FillPolygon(b, a);
        PointF[] c = { new PointF(r.X + r.Width * 0.82f, r.Y), new PointF(r.X + r.Width * 0.9f, r.Y),
                       new PointF(r.X + r.Width * 0.5f, r.Bottom), new PointF(r.X + r.Width * 0.42f, r.Bottom) };
        using (SolidBrush b = new SolidBrush(Color.FromArgb(8, 255, 255, 255))) g.FillPolygon(b, c);
    }

    // глітч: рядки розгортки і кілька зміщених кольорових смуг
    static void Glitch(Graphics g, Rectangle r, NoteTheme t, Random rnd)
    {
        SmoothingMode sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        using (Pen p = new Pen(Color.FromArgb(9, t.Accent2), 1f))
            for (int y = r.Y; y < r.Bottom; y += P(3)) g.DrawLine(p, r.X, y, r.Right, y);
        Color m = t.Border, c = t.Accent2;
        for (int i = 0; i < 5; i++)
        {
            int y = r.Y + (int)(rnd.NextDouble() * r.Height * 0.9);
            int h = P(2) + rnd.Next(P(4));
            int w = (int)(r.Width * (0.2 + rnd.NextDouble() * 0.45));
            int x = r.X + rnd.Next(Math.Max(1, r.Width - w));
            using (SolidBrush b1 = new SolidBrush(Color.FromArgb(22, m))) g.FillRectangle(b1, x, y, w, h);
            using (SolidBrush b2 = new SolidBrush(Color.FromArgb(20, c))) g.FillRectangle(b2, x + P(4), y + P(1), w, h);
            using (SolidBrush b3 = new SolidBrush(Color.FromArgb(10, 255, 255, 255))) g.FillRectangle(b3, x + w / 3, y, w / 4, Math.Max(1, h / 2));
        }
        for (int i = 0; i < 6; i++)
        {
            int s = P(3) + rnd.Next(P(5));
            int x = r.Right - rnd.Next((int)(r.Width * 0.3)) - s, y = r.Y + rnd.Next((int)(r.Height * 0.9));
            using (SolidBrush b = new SolidBrush(Color.FromArgb(16, i % 2 == 0 ? m : c))) g.FillRectangle(b, x, y, s, s);
        }
        g.SmoothingMode = sm;
    }

    static void Gear(Graphics g, float cx, float cy, float rad, int teeth, Color col)
    {
        List<PointF> pts = new List<PointF>();
        int n = teeth * 4;
        for (int i = 0; i < n; i++)
        {
            double a = i * Math.PI * 2 / n;
            float rr = (i % 4 == 0 || i % 4 == 1) ? rad : rad * 0.8f;
            pts.Add(new PointF(cx + (float)Math.Cos(a) * rr, cy + (float)Math.Sin(a) * rr));
        }
        using (GraphicsPath gp = new GraphicsPath())
        {
            gp.AddPolygon(pts.ToArray());
            gp.AddEllipse(cx - rad * 0.3f, cy - rad * 0.3f, rad * 0.6f, rad * 0.6f);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(14, col))) g.FillPath(b, gp);
            using (Pen p = new Pen(Color.FromArgb(34, col), 1.2f)) g.DrawPath(p, gp);
        }
    }

    static void Gears(Graphics g, Rectangle r, NoteTheme t)
    {
        Gear(g, r.Right - P(26), r.Bottom - P(24), P(64), 12, t.Border);
        Gear(g, r.X + P(18), r.Y + P(14), P(30), 8, t.Border);
    }

    // урбаністична: тьмяний сигнальний вогник «тривоги» у куті + смуга обережності знизу
    static void Alarm(Graphics g, Rectangle r, NoteTheme t)
    {
        float rad = Math.Min(r.Width, r.Height) * 0.75f;
        using (GraphicsPath gp = new GraphicsPath())
        {
            gp.AddEllipse(r.Right - rad, r.Y - rad, rad * 2, rad * 2);
            using (PathGradientBrush pg = new PathGradientBrush(gp))
            {
                pg.CenterPoint = new PointF(r.Right, r.Y);
                pg.CenterColor = Color.FromArgb(60, 255, 70, 55);
                pg.SurroundColors = new Color[] { Color.FromArgb(0, 255, 70, 55) };
                g.FillPath(pg, gp);
            }
        }
        for (int i = 1; i <= 3; i++)
            using (Pen p = new Pen(Color.FromArgb(26 - i * 5, 255, 90, 70), 1.2f))
            {
                float d = P(28) * i;
                g.DrawArc(p, r.Right - d, r.Y - d, d * 2, d * 2, 90, 90);
            }
        int bh = P(9);
        Rectangle band = new Rectangle(r.X, r.Bottom - bh, r.Width, bh);
        GraphicsState st = g.Save();
        g.SetClip(band, CombineMode.Intersect);
        using (SolidBrush b = new SolidBrush(Color.FromArgb(24, t.Border)))
            for (int x = r.X - bh; x < r.Right + bh; x += bh * 2)
            {
                PointF[] q = { new PointF(x, r.Bottom), new PointF(x + bh, r.Bottom), new PointF(x + bh * 2, r.Bottom - bh), new PointF(x + bh, r.Bottom - bh) };
                g.FillPolygon(b, q);
            }
        g.Restore(st);
    }

    static GraphicsPath HeartPath(float cx, float cy, float s)
    {
        GraphicsPath p = new GraphicsPath();
        p.AddBezier(cx, cy + 0.6f * s, cx - 1.0f * s, cy - 0.1f * s, cx - 0.6f * s, cy - 0.8f * s, cx, cy - 0.3f * s);
        p.AddBezier(cx, cy - 0.3f * s, cx + 0.6f * s, cy - 0.8f * s, cx + 1.0f * s, cy - 0.1f * s, cx, cy + 0.6f * s);
        p.CloseFigure();
        return p;
    }

    static void Hearts(Graphics g, Rectangle r, NoteTheme t, Random rnd)
    {
        for (int i = 0; i < 9; i++)
        {
            float s = P(6) + rnd.Next(P(10));
            float x = r.X + (float)rnd.NextDouble() * r.Width, y = r.Y + (float)rnd.NextDouble() * r.Height;
            Color c = i % 3 == 0 ? Color.White : t.Border;
            using (GraphicsPath hp = HeartPath(x, y, s))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(i % 3 == 0 ? 60 : 26, c)))
                g.FillPath(b, hp);
        }
    }

    static void Leaf(Graphics g, float x, float y, float len, float ang, Color col)
    {
        GraphicsState st = g.Save();
        g.TranslateTransform(x, y);
        g.RotateTransform(ang);
        float w = len * 0.38f;
        using (GraphicsPath p = new GraphicsPath())
        {
            p.AddBezier(0, -len, w, -len * 0.3f, w, len * 0.3f, 0, len);
            p.AddBezier(0, len, -w, len * 0.3f, -w, -len * 0.3f, 0, -len);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(24, col))) g.FillPath(b, p);
            using (Pen pen = new Pen(Color.FromArgb(40, col), 1f)) g.DrawLine(pen, 0, -len, 0, len);
        }
        g.Restore(st);
    }

    static void Leaves(Graphics g, Rectangle r, NoteTheme t, Random rnd)
    {
        Color[] cols = { t.Border, t.Accent2, Util.Blend(t.Border, t.Fg, 0.3f) };
        for (int i = 0; i < 7; i++)
        {
            bool right = i % 2 == 0;
            float x = right ? r.Right - P(10) - rnd.Next(P(70)) : r.X + P(10) + rnd.Next(P(60));
            float y = r.Y + (float)rnd.NextDouble() * r.Height;
            Leaf(g, x, y, P(10) + rnd.Next(P(12)), rnd.Next(360), cols[i % cols.Length]);
        }
    }

    static void Hatch(Graphics g, Rectangle r, NoteTheme t)
    {
        // два трикутники штрихування; контури звільняються одразу (раніше лишалися до збирання сміття на кожне малювання)
        GraphicsState st = g.Save();
        using (GraphicsPath tri = new GraphicsPath())
        using (Pen p = new Pen(Color.FromArgb(26, t.Fg), 1f))
        {
            tri.AddPolygon(new Point[] { new Point(r.X, r.Bottom), new Point(r.X + (int)(r.Width * 0.42f), r.Bottom), new Point(r.X, r.Bottom - (int)(r.Height * 0.55f)) });
            g.SetClip(tri, CombineMode.Intersect);
            for (int d = -r.Height; d < r.Width + r.Height; d += P(6))
                g.DrawLine(p, r.X + d, r.Bottom, r.X + d + r.Height, r.Bottom - r.Height);
        }
        g.Restore(st);
        st = g.Save();
        using (GraphicsPath tri2 = new GraphicsPath())
        using (Pen p = new Pen(Color.FromArgb(22, t.Header), 1f))
        {
            tri2.AddPolygon(new Point[] { new Point(r.Right, r.Y), new Point(r.Right - (int)(r.Width * 0.25f), r.Y), new Point(r.Right, r.Y + (int)(r.Height * 0.3f)) });
            g.SetClip(tri2, CombineMode.Intersect);
            for (int d = -r.Height; d < r.Width + r.Height; d += P(5))
                g.DrawLine(p, r.X + d, r.Bottom, r.X + d + r.Height, r.Bottom - r.Height);
        }
        g.Restore(st);
    }

    static void Dots(Graphics g, Rectangle r, NoteTheme t)
    {
        using (SolidBrush b = new SolidBrush(Color.FromArgb(18, t.Fg)))
            for (int y = r.Y + P(10); y < r.Bottom; y += P(18))
                for (int x = r.X + P(10); x < r.Right; x += P(18))
                    g.FillEllipse(b, x, y, 1.8f, 1.8f);
    }
}

// ---------- діалог кастомізації теми ----------

sealed class ThemeDialog : Form
{
    readonly NoteTheme t;
    readonly NoteTheme backup;
    readonly bool wasEdited;
    readonly Button bHead, bBody, bFg, bBorder, bFont;
    readonly NumericUpDown nW, nR;
    readonly TrackBar tb;
    readonly Label lOp;
    bool syncing;

    static int Px(float v) { return Notes.Px(v); }

    public ThemeDialog(NoteTheme theme)
    {
        t = theme;
        backup = theme.Clone();
        wasEdited = theme.Edited;
        Text = L.T("Тема нотаток: ") + t.Title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        AppIcon.Apply(this);
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ClientSize = new Size(Px(410), Px(420));

        int y = Px(16), lx = Px(16), cx = Px(180), cw = Px(210), rh = Px(40);
        AddLabel(L.T("Колір шапки"), lx, y); bHead = ColorBtn(cx, y, cw); bHead.Click += delegate { Pick(0); }; y += rh;
        AddLabel(L.T("Колір фону"), lx, y); bBody = ColorBtn(cx, y, cw); bBody.Click += delegate { Pick(1); }; y += rh;
        AddLabel(L.T("Колір тексту"), lx, y); bFg = ColorBtn(cx, y, cw); bFg.Click += delegate { Pick(2); }; y += rh;
        AddLabel(L.T("Колір рамки"), lx, y); bBorder = ColorBtn(cx, y, cw); bBorder.Click += delegate { Pick(3); }; y += rh;

        AddLabel(L.T("Товщина рамки"), lx, y);
        nW = new NumericUpDown(); nW.Minimum = 1; nW.Maximum = 6;
        nW.SetBounds(cx, y - Px(3), Px(70), Px(26));
        nW.ValueChanged += delegate { if (syncing) return; t.BorderW = (int)nW.Value; Notes.ThemeChanged(t); };
        Controls.Add(nW); y += rh;

        AddLabel(L.T("Заокруглення кутів"), lx, y);
        nR = new NumericUpDown(); nR.Minimum = 0; nR.Maximum = 24;
        nR.SetBounds(cx, y - Px(3), Px(70), Px(26));
        nR.ValueChanged += delegate { if (syncing) return; t.Radius = (int)nR.Value; Notes.ThemeChanged(t); };
        Controls.Add(nR); y += rh;

        AddLabel(L.T("Шрифт"), lx, y);
        bFont = new Button();
        bFont.SetBounds(cx, y - Px(3), cw, Px(28));
        bFont.Click += delegate { PickFont(); };
        Controls.Add(bFont); y += rh;

        AddLabel(L.T("Прозорість фону (для нових)"), lx, y);
        tb = new TrackBar();
        tb.Minimum = 0; tb.Maximum = 100; tb.TickFrequency = 10; tb.SmallChange = 1; tb.LargeChange = 10;
        tb.SetBounds(cx - Px(6), y - Px(8), cw - Px(40), Px(36));
        tb.ValueChanged += delegate
        {
            lOp.Text = tb.Value + "%";
            if (syncing) return;
            t.Opacity = tb.Value;
            Notes.ThemeChanged(t);
        };
        Controls.Add(tb);
        lOp = new Label();
        lOp.SetBounds(cx + cw - Px(38), y, Px(40), Px(20));
        Controls.Add(lOp);
        y += rh;

        Label hint = new Label();
        hint.SetBounds(lx, y - Px(4), Px(380), Px(40));
        hint.ForeColor = SystemColors.GrayText;
        hint.Text = L.T("Зміни діють на всі нотатки цієї теми. Прозорість окремої нотатки задається повзунком у її шапці.");
        Controls.Add(hint);

        Button reset = new Button(); reset.Text = L.T("Скинути");
        reset.SetBounds(lx, ClientSize.Height - Px(44), Px(90), Px(30));
        reset.Click += delegate { Notes.ResetTheme(t); Sync(); };
        Button ok = new Button(); ok.Text = "OK"; ok.DialogResult = DialogResult.OK;
        ok.SetBounds(ClientSize.Width - Px(190), ClientSize.Height - Px(44), Px(80), Px(30));
        Button cancel = new Button(); cancel.Text = L.T("Скасувати"); cancel.DialogResult = DialogResult.Cancel;
        cancel.SetBounds(ClientSize.Width - Px(100), ClientSize.Height - Px(44), Px(84), Px(30));
        Controls.Add(reset); Controls.Add(ok); Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;
        FormClosing += delegate
        {
            if (DialogResult == DialogResult.Cancel)
            {
                t.CopyFrom(backup);
                Notes.ThemeChanged(t);
                t.Edited = wasEdited;
            }
        };
        Sync();
    }

    void AddLabel(string s, int x, int y)
    {
        Label l = new Label();
        l.Text = s;
        l.SetBounds(x, y, Px(160), Px(36));
        Controls.Add(l);
    }

    Button ColorBtn(int x, int y, int w)
    {
        Button b = new Button();
        b.FlatStyle = FlatStyle.Flat;
        b.SetBounds(x, y - Px(3), w, Px(28));
        Controls.Add(b);
        return b;
    }

    void Sync()
    {
        syncing = true;
        bHead.BackColor = t.Header; bHead.FlatAppearance.BorderColor = Color.Gray;
        bBody.BackColor = t.Body; bBody.FlatAppearance.BorderColor = Color.Gray;
        bFg.BackColor = t.Fg; bFg.FlatAppearance.BorderColor = Color.Gray;
        bBorder.BackColor = Util.Opaque(t.Border); bBorder.FlatAppearance.BorderColor = Color.Gray;
        nW.Value = Math.Max(1, Math.Min(6, t.BorderW));
        nR.Value = Math.Max(0, Math.Min(24, t.Radius));
        string fn = t.FontName.Split('|')[0].Trim();
        bFont.Text = fn + ", " + t.FontSize.ToString("0.#", CultureInfo.InvariantCulture) + L.T(" пт");
        tb.Value = Math.Max(0, Math.Min(100, t.Opacity));
        lOp.Text = tb.Value + "%";
        syncing = false;
    }

    void Pick(int which)
    {
        using (ColorDialog cd = new ColorDialog())
        {
            cd.FullOpen = true;
            cd.Color = which == 0 ? t.Header : which == 1 ? t.Body : which == 2 ? t.Fg : Util.Opaque(t.Border);
            if (cd.ShowDialog(this) != DialogResult.OK) return;
            if (which == 0) t.Header = cd.Color;
            else if (which == 1) t.Body = cd.Color;
            else if (which == 2) t.Fg = cd.Color;
            else t.Border = cd.Color;
            Sync();
            Notes.ThemeChanged(t);
        }
    }

    // власні шрифти NeoBar (з прикладом написання) + "інший шрифт" зі списку Windows
    void PickFont()
    {
        ContextMenuStrip m = new ContextMenuStrip();
        string cur = t.FontName.Split('|')[0].Trim();
        foreach (string nm in SBFonts.Names)
        {
            string name = nm;
            ToolStripMenuItem it = new ToolStripMenuItem(name + L.T("   Привіт, Київ! Abc 123"));
            it.Checked = string.Equals(cur, name, StringComparison.OrdinalIgnoreCase);
            try { FontFamily ff = SBFonts.Family(name); if (ff != null) it.Font = new Font(ff, 12f, FontStyle.Regular, GraphicsUnit.Point); } catch { }
            it.Click += delegate
            {
                t.FontName = name + "|Segoe UI";
                Sync();
                Notes.ThemeChanged(t);
            };
            m.Items.Add(it);
        }
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(L.T("Інший шрифт Windows…"), null, delegate
        {
            using (FontDialog fd = new FontDialog())
            {
                try { fd.Font = new Font(cur, t.FontSize); } catch { }
                fd.ShowEffects = false;
                if (fd.ShowDialog(this) != DialogResult.OK) return;
                t.FontName = fd.Font.Name;
                t.FontSize = fd.Font.SizeInPoints;
                Sync();
                Notes.ThemeChanged(t);
            }
        });
        m.Closed += delegate { BeginInvoke(new Action(m.Dispose)); };
        m.Show(bFont, new Point(0, bFont.Height));
    }
}
