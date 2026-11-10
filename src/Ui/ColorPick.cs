using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// ======================================================================
//  Вибір кольору в стилі теми (замість системного ColorDialog): квадрат насиченості й яскравості,
//  смуга тону, HEX, кольори теми й нещодавні, піпетка з екрана.
//  ColorPick.Ask(власник, початковий колір, out вибраний) - true, якщо натиснули «Вибрати».
// ======================================================================
sealed class ColorPick : SbWindow
{
    double hh, ss, vv;
    Color cur, start;
    bool ok;
    readonly TextBox hex;
    bool hexSync;
    static readonly List<Color> recent = new List<Color>();

    ColorPick(NeoBar bar, Color c) : base(bar, L.T("Колір"), 420, 470) { Init(c); hex = MakeHex(); }
    ColorPick(Theme t, float sc, Color c) : base(t, sc, L.T("Колір"), 420, 470) { Init(c); hex = MakeHex(); }

    void Init(Color c)
    {
        Glyph = "";
        ShowInTaskbar = false;
        TopMost = true;
        start = cur = Color.FromArgb(255, c);
        ColorMath.ToHsv(cur, out hh, out ss, out vv);
    }

    TextBox MakeHex()
    {
        TextBox tb = MakeInput();
        tb.MaxLength = 7;
        tb.Text = ColorMath.Hex(cur);
        tb.TextChanged += delegate
        {
            if (hexSync) return;
            Color c;
            if (ColorMath.TryHex(tb.Text, out c)) { cur = Color.FromArgb(255, c); ColorMath.ToHsv(cur, out hh, out ss, out vv); Invalidate(); }
        };
        tb.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Done(); } };
        return tb;
    }

    void Set(double h, double s, double v)
    {
        hh = h; ss = s; vv = v;
        cur = ColorMath.FromHsv(hh, ss, vv);
        hexSync = true;
        hex.Text = ColorMath.Hex(cur);
        hexSync = false;
        Invalidate();
    }

    void SetColor(Color c)
    {
        cur = Color.FromArgb(255, c);
        ColorMath.ToHsv(cur, out hh, out ss, out vv);
        hexSync = true;
        hex.Text = ColorMath.Hex(cur);
        hexSync = false;
        Invalidate();
    }

    void Done()
    {
        ok = true;
        recent.Remove(cur);
        recent.Insert(0, cur);
        if (recent.Count > 8) recent.RemoveAt(recent.Count - 1);
        Close();
    }

    void Eyedrop()
    {
        double op = Opacity;
        Opacity = 0;
        Timer t = new Timer();
        t.Interval = 160;     // вікно встигає зникнути зі знімка екрана
        t.Tick += delegate
        {
            t.Stop(); t.Dispose();
            ScreenPicker.Start(delegate (Color c) { if (!IsDisposed) SetColor(c); },
                delegate { if (!IsDisposed) { Opacity = op; Activate(); } });
        };
        t.Start();
    }

    void Marker(Graphics g, PointF p, Color c)
    {
        float rad = Px(7);
        using (Pen o = new Pen(Color.FromArgb(160, 0, 0, 0), Px(3))) g.DrawEllipse(o, p.X - rad, p.Y - rad, rad * 2, rad * 2);
        using (Pen w = new Pen(Color.White, Px(2))) g.DrawEllipse(w, p.X - rad, p.Y - rad, rad * 2, rad * 2);
        using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, p.X - rad + Px(2), p.Y - rad + Px(2), rad * 2 - Px(4), rad * 2 - Px(4));
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int bar = Px(22), gap = Px(14), sw = Px(30);
        int s = Math.Min(r.Width - bar - gap, r.Height - Px(150));
        Rectangle sq = new Rectangle(r.X + (r.Width - s - gap - bar) / 2, r.Y, s, s);      // квадрат і смуга - посередині
        using (GraphicsPath gp = Util.Round(sq, P.Rad))
        {
            using (SolidBrush b = new SolidBrush(ColorMath.FromHsv(hh, 1, 1))) g.FillPath(b, gp);
            using (LinearGradientBrush w = new LinearGradientBrush(new Rectangle(sq.X - 1, sq.Y, sq.Width + 2, sq.Height), Color.White, Color.FromArgb(0, 255, 255, 255), 0f)) g.FillPath(w, gp);
            using (LinearGradientBrush k = new LinearGradientBrush(new Rectangle(sq.X, sq.Y - 1, sq.Width, sq.Height + 2), Color.FromArgb(0, 0, 0, 0), Color.Black, 90f)) g.FillPath(k, gp);
        }
        Marker(g, new PointF(sq.X + (float)ss * sq.Width, sq.Y + (float)(1 - vv) * sq.Height), cur);
        DragArea(sq, delegate (Point p) { Set(hh, Math.Max(0, Math.Min(1, (p.X - sq.X) / (double)Math.Max(1, sq.Width))), Math.Max(0, Math.Min(1, 1 - (p.Y - sq.Y) / (double)Math.Max(1, sq.Height)))); });

        Rectangle hb = new Rectangle(sq.Right + gap, sq.Y, bar, s);
        using (LinearGradientBrush lb = new LinearGradientBrush(new Rectangle(hb.X, hb.Y - 1, hb.Width, hb.Height + 2), Color.Red, Color.Red, 90f))
        {
            ColorBlend cb = new ColorBlend(7);
            for (int i = 0; i < 7; i++) { cb.Colors[i] = ColorMath.FromHsv(i * 60, 1, 1); cb.Positions[i] = i / 6f; }
            lb.InterpolationColors = cb;
            Util.FillRound(g, lb, hb, Math.Min(P.Rad, hb.Width / 2));
        }
        Marker(g, new PointF(hb.X + hb.Width / 2f, hb.Y + (float)(hh / 360.0) * hb.Height), ColorMath.FromHsv(hh, 1, 1));
        DragArea(hb, delegate (Point p) { Set(Math.Max(0, Math.Min(359.9, (p.Y - hb.Y) * 360.0 / Math.Max(1, hb.Height))), ss, vv); });

        // було / стало, HEX, піпетка
        int y = sq.Bottom + Px(12);
        Rectangle was = new Rectangle(r.X, y, Px(40), Px(34)), now = new Rectangle(was.Right, y, Px(40), Px(34));
        using (SolidBrush b = new SolidBrush(start)) Util.FillRound(g, b, Rectangle.Union(was, now), P.Rad);
        using (SolidBrush b = new SolidBrush(cur)) g.FillRectangle(b, new Rectangle(now.X, now.Y, now.Width - P.Rad, now.Height));
        using (SolidBrush b = new SolidBrush(cur)) Util.FillRound(g, b, now, P.Rad);
        Tip(was, L.T("Було - клацніть, щоб повернути"));
        Hot(was, delegate { SetColor(start); });
        PlaceInput(g, hex, new Rectangle(now.Right + Px(10), y, Px(120), Px(34)));
        Rectangle eb = new Rectangle(now.Right + Px(140), y, r.Right - now.Right - Px(140), Px(34));
        Btn(g, eb, L.T("Піпетка"), Eyedrop, 0, true);
        Tip(eb, L.T("Піпетка: колір з будь-якого місця екрана"));

        // кольори теми й нещодавні
        y += Px(46);
        List<Color> sw0 = new List<Color>();
        sw0.Add(Util.Opaque(T.Accent)); sw0.Add(Util.Opaque(T.Bg)); sw0.Add(P.Fg);
        foreach (Color c in Util.DefPalette) if (sw0.Count < 11) sw0.Add(c);
        foreach (Color c in recent) if (!sw0.Contains(c) && sw0.Count < 11 + 8) sw0.Add(c);
        int x = r.X;
        foreach (Color c in sw0)
        {
            if (x + sw > r.Right) { x = r.X; y += sw + Px(6); }
            Rectangle cr = new Rectangle(x, y, sw, sw);
            using (SolidBrush b = new SolidBrush(c)) Util.FillRound(g, b, cr, Math.Min(P.Rad, sw / 3));
            using (Pen ed = new Pen(Util.Blend(P.Bg, P.Fg, 0.25f), 1f))                        // контур: колір фону теж видно
            using (GraphicsPath gp = Util.Round(cr, Math.Min(P.Rad, sw / 3))) g.DrawPath(ed, gp);
            if (c.ToArgb() == cur.ToArgb()) using (Pen pn = new Pen(P.Fg, Px(2))) g.DrawRectangle(pn, Rectangle.Inflate(cr, -Px(2), -Px(2)));
            Color cc = c;
            Hot(cr, delegate { SetColor(cc); });
            Tip(cr, ColorMath.Hex(c));
            x += sw + Px(6);
        }

        Btn(g, new Rectangle(r.Right - Px(250), r.Bottom - Px(36), Px(120), Px(36)), L.T("Скасувати"), Close, 0, true);
        Btn(g, new Rectangle(r.Right - Px(120), r.Bottom - Px(36), Px(120), Px(36)), L.T("Вибрати"), Done, 1, true);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }

    // вибір кольору з будь-якого вікна; тема - панелі (або профілю, якщо панелі ще немає)
    public static bool Ask(IWin32Window owner, Color initial, out Color result)
    {
        result = initial;
        NeoBar b = ClockData.Bar;
        ColorPick d;
        if (b != null && !b.IsDisposed) d = new ColorPick(b, initial);
        else
        {
            float sc = 1f;
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) sc = g.DpiX / 96f; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            d = new ColorPick(Prof.ThemeOf(Prof.Cur), sc, initial);
        }
        using (d)
        {
            Form of = owner as Form;
            Rectangle wa = of != null ? Screen.FromControl(of).WorkingArea : Screen.PrimaryScreen.WorkingArea;
            Rectangle ob = of != null && of.Visible ? of.Bounds : wa;
            d.StartPosition = FormStartPosition.Manual;
            d.Location = new Point(Math.Max(wa.Left, ob.Left + (ob.Width - d.Width) / 2), Math.Max(wa.Top, ob.Top + (ob.Height - d.Height) / 2));
            if (owner != null) d.ShowDialog(owner); else d.ShowDialog();
            if (!d.ok) return false;
            result = d.cur;
            return true;
        }
    }
}
