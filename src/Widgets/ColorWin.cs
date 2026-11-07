using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

// ======================================================================
//  Віджет «Палітра»: піпетка з екрана, різні види вибору кольору,
//  перетворення в колірні моделі (копіювання й редагування)
// ======================================================================

// ----------------------------------------------------------------------
//  Піпетка: знімок усього екрана, лупа біля курсора, клік - колір
// ----------------------------------------------------------------------
sealed class ScreenPicker : Form
{
    readonly Bitmap shot;
    readonly int vsX, vsY;   // початок віртуального екрана (окремі int, щоб не звертатись до полів структури форми)
    Point cur = new Point(-100, -100);
    readonly Action<Color> pick;
    readonly Action end;
    bool done, disposed;
    bool downOk, isDown;     // кнопку натиснуто на піпетці: результат віддаємо лише після відпускання
    Color downColor;
    const int Cells = 11, Zoom = 11;

    ScreenPicker(Action<Color> pick, Action end)
    {
        this.pick = pick;
        this.end = end;
        Rectangle vs = SystemInformation.VirtualScreen;
        vsX = vs.X; vsY = vs.Y;
        shot = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppRgb);
        using (Graphics g = Graphics.FromImage(shot)) g.CopyFromScreen(vs.Location, Point.Empty, vs.Size);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FormBorderStyle = FormBorderStyle.None;
        AppIcon.Apply(this);
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        Bounds = vs;
        Cursor = Cursors.Cross;
        Text = L.T("Піпетка");
    }

    public static void Start(Action<Color> pick, Action end)
    {
        try
        {
            ScreenPicker p = new ScreenPicker(pick, end);
            p.Show();
            try { Win.SetWindowPos(p.Handle, (IntPtr)(-1), 0, 0, 0, 0, 0x0013); } catch { }   // понад усіма, зокрема понад самою панеллю
            p.Activate();
            p.Capture = true;      // жодне клацання не проходить до інших вікон
            Point c = Cursor.Position;
            p.cur = new Point(c.X - p.vsX, c.Y - p.vsY);
            p.Invalidate();
        }
        catch { if (end != null) end(); }
    }

    Rectangle Loupe(Point c)
    {
        int s = Cells * Zoom, gap = 24, lab = 30;
        int x = c.X + gap, y = c.Y + gap;
        if (x + s > ClientSize.Width) x = c.X - gap - s;
        if (y + s + lab > ClientSize.Height) y = c.Y - gap - s - lab;
        return new Rectangle(x - 4, y - 4, s + 8, s + lab + 8);
    }

    Color At(Point p)
    {
        if (disposed) return Color.Black;       // вікно вже закрите: знімок звільнено
        if (p.X < 0 || p.Y < 0 || p.X >= shot.Width || p.Y >= shot.Height) return Color.Black;
        return Color.FromArgb(255, shot.GetPixel(p.X, p.Y));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (disposed) return;
        Graphics g = e.Graphics;
        g.DrawImage(shot, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);
        if (cur.X < -50) return;
        Rectangle lr = Loupe(cur);
        int s = Cells * Zoom;
        Rectangle zr = new Rectangle(lr.X + 4, lr.Y + 4, s, s);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath gp = Util.Round(lr, 12))
        using (SolidBrush b = new SolidBrush(Color.FromArgb(235, 24, 26, 32))) g.FillPath(b, gp);
        Region old = g.Clip;
        using (GraphicsPath cp = Util.Round(zr, 8))
        {
            g.SetClip(cp, CombineMode.Intersect);
            g.DrawImage(shot, zr, new Rectangle(cur.X - Cells / 2, cur.Y - Cells / 2, Cells, Cells), GraphicsUnit.Pixel);
            g.Clip = old;
        }
        g.PixelOffsetMode = PixelOffsetMode.Default;
        Rectangle mid = new Rectangle(zr.X + (Cells / 2) * Zoom, zr.Y + (Cells / 2) * Zoom, Zoom, Zoom);
        using (Pen p1 = new Pen(Color.Black, 3f)) g.DrawRectangle(p1, mid);
        using (Pen p2 = new Pen(Color.White, 1.4f)) g.DrawRectangle(p2, mid);
        Color c = At(cur);
        Rectangle sw = new Rectangle(zr.X, zr.Bottom + 6, 22, 22);
        using (SolidBrush b = new SolidBrush(c)) Util.FillRound(g, b, sw, 5);
        using (Pen p3 = new Pen(Color.FromArgb(120, 255, 255, 255))) using (GraphicsPath gp = Util.Round(sw, 5)) g.DrawPath(p3, gp);
        using (Font f = new Font("Segoe UI", 10f, FontStyle.Bold))
            TextRenderer.DrawText(g, ColorMath.Hex(c), f, new Rectangle(sw.Right + 6, sw.Y, zr.Right - sw.Right - 6, sw.Height), Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    void MoveTo(Point p)
    {
        Rectangle a = Loupe(cur);
        cur = p;
        Invalidate(Rectangle.Inflate(a, 2, 2));
        Invalidate(Rectangle.Inflate(Loupe(cur), 2, 2));
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); MoveTo(e.Location); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (isDown) return;
        isDown = true;
        downOk = e.Button == MouseButtons.Left;
        downColor = At(e.Location);
    }

    // закриваємось лише після відпускання кнопки, інакше «відпускання» потрапить у вікно під піпеткою
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!isDown) return;
        Finish(downOk, downColor);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int st = e.Shift ? 10 : 1;
        Point d = Point.Empty;
        switch (e.KeyCode)
        {
            case Keys.Escape: Finish(false); return;
            case Keys.Enter: case Keys.Space: Finish(true); return;
            case Keys.Left: d = new Point(-st, 0); break;
            case Keys.Right: d = new Point(st, 0); break;
            case Keys.Up: d = new Point(0, -st); break;
            case Keys.Down: d = new Point(0, st); break;
        }
        if (d != Point.Empty)
        {
            Point p = new Point(cur.X + d.X, cur.Y + d.Y);
            Cursor.Position = new Point(p.X + vsX, p.Y + vsY);
            MoveTo(p);
        }
    }

    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Finish(false); }

    void Finish(bool ok) { if (done) return; Finish(ok, At(cur)); }

    void Finish(bool ok, Color c)
    {
        if (done) return;
        done = true;
        Capture = false;
        Close();
        if (ok && pick != null) pick(c);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        disposed = true;
        shot.Dispose();
        if (end != null) end();
    }
}

// ----------------------------------------------------------------------
//  Розширене вікно «Палітра»
// ----------------------------------------------------------------------
sealed class ColorWindow : SbWindow
{
    public static readonly string[] ModeNames = { L.T("Квадрат"), L.T("Коло"), L.T("Соти"), L.T("Спектр"), L.T("Повзунки"), L.T("Зразки") };

    double hh, ss, vv;                 // HSV поточного кольору (тон не губиться на сірих)
    int editIdx = -1;                  // яка модель редагується
    TextBox tbEdit;
    int copiedIdx = -1;
    readonly Timer flash = new Timer();
    bool editBad;

    // кеш колеса (дороге малювання по пікселях)
    Bitmap wheel;
    int wheelD = -1;
    double wheelV = -1;

    public ColorWindow(NeoBar bar, Entry e) : base(bar, L.T("Палітра"), 560, 690)
    { Glyph = "\uE790";
        ColorState.Load(e != null ? e.Opt : "");
        Tabs = new string[] { L.T("Вибір"), L.T("Колірні моделі") };
        SyncHsv();
        tbEdit = MakeInput();
        tbEdit.KeyDown += delegate (object s, KeyEventArgs ke)
        {
            if (ke.KeyCode == Keys.Enter) { ke.SuppressKeyPress = true; ApplyEdit(); }
            else if (ke.KeyCode == Keys.Escape) { ke.SuppressKeyPress = true; CancelEdit(); }
        };
        tbEdit.TextChanged += delegate
        {
            if (editIdx < 0) return;
            Color c;
            editBad = tbEdit.Text.Trim().Length > 0 && !ColorMath.Parse(editIdx, tbEdit.Text, out c);
            Invalidate();
        };
        flash.Interval = 1300;
        flash.Tick += delegate { flash.Stop(); copiedIdx = -1; Invalidate(); };
    }

    void SyncHsv()
    {
        double h, s, v;
        ColorMath.ToHsv(ColorState.Cur, out h, out s, out v);
        if (s > 0.0001 && v > 0.0001) hh = h;
        if (v > 0.0001) ss = s;
        vv = v;
    }

    // колір змінено мишею (без запису в історію - до кінця перетягування)
    void SetHsv(double h, double s, double v)
    {
        hh = ((h % 360) + 360) % 360; ss = Math.Max(0, Math.Min(1, s)); vv = Math.Max(0, Math.Min(1, v));
        ColorState.Cur = ColorMath.FromHsv(hh, ss, vv);
        Bar.ColorChanged(false);
        Invalidate();
    }

    // колір вибрано повністю (клік, піпетка, введення, історія) - запам'ятати й зберегти
    void Commit(Color c)
    {
        ColorState.Cur = Color.FromArgb(255, c);
        SyncHsv();
        ColorState.Remember(ColorState.Cur);
        Bar.ColorChanged(true);
        Invalidate();
    }

    protected override void OnDragEnd()
    {
        ColorState.Remember(ColorState.Cur);
        Bar.ColorChanged(true);
        Invalidate();
    }

    protected override void OnTab() { CancelEdit(); }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        flash.Dispose();
        if (wheel != null) wheel.Dispose();
        Bar.ColorChanged(true);
        base.OnFormClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && editIdx >= 0) { CancelEdit(); e.Handled = true; return; }
        if (e.Control && e.KeyCode == Keys.C && editIdx < 0) { Copy(0); return; }
        base.OnKeyDown(e);
    }

    // ---------- піпетка ----------
    void Eyedrop()
    {
        CancelEdit();
        double op = Opacity;
        Opacity = 0;
        Timer t = new Timer();
        t.Interval = 160;     // вікно встигає зникнути зі знімка
        t.Tick += delegate
        {
            t.Stop(); t.Dispose();
            ScreenPicker.Start(delegate (Color c) { Commit(c); },
                delegate { if (!IsDisposed) { Opacity = 1; Activate(); } });
        };
        t.Start();
    }

    // ---------- копіювання / редагування ----------
    void Copy(int m)
    {
        try { Clipboard.SetText(ColorMath.Format(m, ColorState.Cur)); } catch { return; }
        if (Tab == 1) ColorState.UsedModel(m);   // «останні» запам'ятовуємо лише з вкладки «Колірні моделі»
        ColorState.Remember(ColorState.Cur);
        Bar.ColorChanged(true);
        copiedIdx = m;
        flash.Stop(); flash.Start();
        Invalidate();
    }

    void StartEdit(int m)
    {
        editIdx = m;
        editBad = false;
        tbEdit.Text = ColorMath.Format(m, ColorState.Cur).TrimStart('≈', ' ');
        Invalidate();
        Update();
        tbEdit.Focus();
        tbEdit.SelectAll();
    }

    void ApplyEdit()
    {
        if (editIdx < 0) return;
        Color c;
        if (!ColorMath.Parse(editIdx, tbEdit.Text, out c)) { editBad = true; Invalidate(); return; }
        CancelEdit();
        Commit(c);
    }

    void CancelEdit()
    {
        if (editIdx < 0) return;
        editIdx = -1;
        tbEdit.Visible = false;
        Focus();
        Invalidate();
    }

    // ---------- малювання ----------
    protected override void PaintBody(Graphics g, Rectangle r)
    {
        bool editShown = false;
        Color cur = ColorState.Cur;

        // зразок поточного кольору + піпетка
        Rectangle sw = new Rectangle(r.X, r.Y, Px(116), Px(62));
        using (SolidBrush b = new SolidBrush(cur)) Util.FillRound(g, b, sw, P.Rad);
        using (Pen pn = new Pen(Util.Blend(P.Bg, P.Fg, 0.25f), 1f)) using (GraphicsPath gp = Util.Round(sw, P.Rad)) g.DrawPath(pn, gp);
        TxtC(g, ColorMath.Hex(cur), FB, sw, ColorMath.Ink(cur));
        Hot(sw, delegate { Copy(0); });
        int bx = sw.Right + Px(12);
        TxtL(g, ColorMath.NearestName(cur), F, new Rectangle(bx, r.Y, r.Right - bx - Px(140), Px(22)), P.Fg);
        TxtL(g, ColorMath.Format(1, cur), FS, new Rectangle(bx, r.Y + Px(22), r.Right - bx - Px(140), Px(18)), P.Sub);
        TxtL(g, copiedIdx == 0 ? L.T("HEX скопійовано") : L.T("Клік по зразку - скопіювати HEX"), FS, new Rectangle(bx, r.Y + Px(40), r.Right - bx - Px(140), Px(18)), copiedIdx == 0 ? P.Good : P.Sub);
        Rectangle eb = new Rectangle(r.Right - Px(130), r.Y + Px(8), Px(130), Px(44));
        DropperBtn(g, eb);

        // історія
        int y = sw.Bottom + Px(10);
        int cs = Px(24), cg = Px(6);
        if (ColorState.Hist.Count == 0)
            TxtL(g, L.T("Тут з'являться вибрані кольори"), FS, new Rectangle(r.X, y, r.Width, cs), P.Sub);
        for (int i = 0; i < ColorState.Hist.Count; i++)
        {
            Rectangle hr = new Rectangle(r.X + i * (cs + cg), y, cs, cs);
            if (hr.Right > r.Right) break;
            Color hc = ColorState.Hist[i];
            bool hov = Over(hr);
            using (SolidBrush b = new SolidBrush(hc)) g.FillEllipse(b, hov ? Rectangle.Inflate(hr, Px(2), Px(2)) : hr);
            using (Pen pn = new Pen(hc.ToArgb() == cur.ToArgb() ? P.Fg : Util.Blend(P.Bg, P.Fg, 0.25f), hc.ToArgb() == cur.ToArgb() ? 2f : 1f)) g.DrawEllipse(pn, hr);
            Color cc = hc;
            Hot(hr, delegate { Commit(cc); });
        }
        y += cs + Px(10);

        if (Tab == 0)
        {
            // вид палітри
            int n = ModeNames.Length, chW = (r.Width - Px(6) * (n - 1)) / n, chH = Px(28);
            for (int i = 0; i < n; i++)
            {
                int k = i;
                Chip(g, new Rectangle(r.X + i * (chW + Px(6)), y, chW, chH), ModeNames[i], ColorState.Mode == i,
                    delegate { ColorState.Mode = k; Bar.ColorChanged(true); Invalidate(); });
            }
            y += chH + Px(10);
            int quickH = 3 * Px(34) + 2 * Px(6);
            Rectangle pa = new Rectangle(r.X, y, r.Width, r.Bottom - y - quickH - Px(12));
            Card(g, pa);
            Rectangle pin = Rectangle.Inflate(pa, -Px(12), -Px(12));
            switch (ColorState.Mode)
            {
                case 0: PaintSquare(g, pin); break;
                case 1: PaintWheel(g, pin); break;
                case 2: PaintHoney(g, pin); break;
                case 3: PaintSpectrum(g, pin); break;
                case 4: PaintSliders(g, pin); break;
                default: PaintSwatches(g, pin); break;
            }
            int ry = pa.Bottom + Px(12);
            int[] quick = (int[])ColorState.Recent.Clone();
            foreach (int m in quick)
            {
                if (ModelRow(g, new Rectangle(r.X, ry, r.Width, Px(34)), m)) editShown = true;
                ry += Px(40);
            }
        }
        else
        {
            int rh = Px(32), gap = Px(5);
            for (int m = 0; m < ColorMath.Models.Length; m++)
            {
                Rectangle rr = new Rectangle(r.X, y, r.Width, rh);
                if (rr.Bottom > r.Bottom) break;
                if (ModelRow(g, rr, m)) editShown = true;
                y += rh + gap;
            }
        }
        if (!editShown && tbEdit.Visible) tbEdit.Visible = false;
    }

    void DropperBtn(Graphics g, Rectangle eb)
    {
        bool hov = Over(eb);
        using (SolidBrush b = new SolidBrush(Util.Blend(P.Bg, P.Acc, hov ? 0.55f : 0.38f))) Util.FillRound(g, b, eb, Math.Min(P.Rad, eb.Height / 2));
        // піктограма піпетки
        float s = eb.Height * 0.5f;
        RectangleF ir = new RectangleF(eb.X + Px(12), eb.Y + (eb.Height - s) / 2f, s, s);
        using (Pen pn = new Pen(P.Fg, Math.Max(1.6f, Px(2))))
        {
            pn.StartCap = LineCap.Round; pn.EndCap = LineCap.Round;
            g.DrawLine(pn, ir.X + s * 0.12f, ir.Bottom - s * 0.12f, ir.X + s * 0.62f, ir.Y + s * 0.38f);
            g.DrawLine(pn, ir.X + s * 0.45f, ir.Y + s * 0.3f, ir.X + s * 0.7f, ir.Y + s * 0.55f);
        }
        using (SolidBrush b = new SolidBrush(P.Fg)) g.FillEllipse(b, ir.X + s * 0.55f, ir.Y, s * 0.45f, s * 0.45f);
        TxtL(g, L.T("Піпетка"), FB, new Rectangle((int)ir.Right + Px(8), eb.Y, eb.Right - (int)ir.Right - Px(8), eb.Height), P.Fg);
        Hot(eb, Eyedrop);
    }

    // рядок моделі: назва, значення (клік - редагувати), «редагувати», «копіювати»
    bool ModelRow(Graphics g, Rectangle rr, int m)
    {
        Card(g, rr);
        int lw = Px(118), bw = Px(30);
        TxtL(g, ColorMath.Models[m], FS, new Rectangle(rr.X + Px(10), rr.Y, lw - Px(10), rr.Height), P.Sub);
        Rectangle copy = new Rectangle(rr.Right - bw - Px(4), rr.Y + (rr.Height - bw) / 2, bw, bw);
        Rectangle edit = new Rectangle(copy.X - bw - Px(2), copy.Y, bw, bw);
        Rectangle val = new Rectangle(rr.X + lw, rr.Y + Px(3), edit.X - rr.X - lw - Px(6), rr.Height - Px(6));
        bool editing = editIdx == m;
        if (editing)
        {
            PlaceInput(g, tbEdit, val);
            if (editBad)
                using (GraphicsPath gp = Util.Round(val, Math.Min(P.Rad, val.Height / 2)))
                using (Pen pn = new Pen(P.Bad, 1.4f)) g.DrawPath(pn, gp);
            SmallIcon(g, edit, "ok", delegate { ApplyEdit(); }, L.T("Застосувати (Enter)"));
            SmallIcon(g, copy, "x", delegate { CancelEdit(); }, L.T("Скасувати (Esc)"));
            return true;
        }
        string v = ColorMath.Format(m, ColorState.Cur);
        bool cp = copiedIdx == m;
        TxtL(g, cp ? L.T("Скопійовано") : v, F, val, cp ? P.Good : P.Fg);
        Hot(val, delegate { StartEdit(m); });
        SmallIcon(g, edit, "edit", delegate { StartEdit(m); }, L.T("Редагувати"));
        SmallIcon(g, copy, cp ? "ok" : "copy", delegate { Copy(m); }, L.T("Копіювати"));
        return false;
    }

    void SmallIcon(Graphics g, Rectangle r, string kind, Action click, string tip)
    {
        bool hov = Over(r);
        if (hov) using (SolidBrush b = new SolidBrush(Util.Blend(P.Card, P.Fg, 0.12f))) Util.FillRound(g, b, r, Math.Min(P.Rad, r.Height / 2));
        Color c = hov ? P.Acc : P.Sub;
        float s = r.Width * 0.42f, x = r.X + (r.Width - s) / 2f, y = r.Y + (r.Height - s) / 2f;
        using (Pen pn = new Pen(c, Math.Max(1.3f, Px(1.5f))))
        {
            pn.StartCap = LineCap.Round; pn.EndCap = LineCap.Round; pn.LineJoin = LineJoin.Round;
            switch (kind)
            {
                case "copy":
                    g.DrawRectangle(pn, x + s * 0.3f, y, s * 0.7f, s * 0.7f);
                    g.DrawLines(pn, new PointF[] { new PointF(x + s * 0.18f, y + s * 0.3f), new PointF(x, y + s * 0.3f), new PointF(x, y + s), new PointF(x + s * 0.7f, y + s), new PointF(x + s * 0.7f, y + s * 0.82f) });
                    break;
                case "edit":
                    g.DrawLine(pn, x + s * 0.1f, y + s * 0.9f, x + s * 0.85f, y + s * 0.15f);
                    g.DrawLine(pn, x + s * 0.1f, y + s * 0.9f, x + s * 0.05f, y + s);
                    g.DrawLine(pn, x + s * 0.68f, y + s * 0.05f, x + s * 0.95f, y + s * 0.32f);
                    break;
                case "ok":
                    g.DrawLines(pn, new PointF[] { new PointF(x, y + s * 0.55f), new PointF(x + s * 0.38f, y + s * 0.9f), new PointF(x + s, y + s * 0.12f) });
                    break;
                default:
                    g.DrawLine(pn, x, y, x + s, y + s);
                    g.DrawLine(pn, x + s, y, x, y + s);
                    break;
            }
        }
        Hot(r, click);
    }

    void Marker(Graphics g, PointF p, Color c)
    {
        float rad = Px(7);
        using (Pen o = new Pen(Color.FromArgb(160, 0, 0, 0), Px(3))) g.DrawEllipse(o, p.X - rad, p.Y - rad, rad * 2, rad * 2);
        using (Pen w = new Pen(Color.White, Px(2))) g.DrawEllipse(w, p.X - rad, p.Y - rad, rad * 2, rad * 2);
        using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, p.X - rad + Px(2), p.Y - rad + Px(2), rad * 2 - Px(4), rad * 2 - Px(4));
    }

    static ColorBlend HueBlend(bool vertical)
    {
        ColorBlend cb = new ColorBlend(7);
        for (int i = 0; i < 7; i++)
        {
            cb.Colors[i] = ColorMath.FromHsv(i * 60, 1, 1);
            cb.Positions[i] = i / 6f;
        }
        return cb;
    }

    // вертикальна смуга тону
    void HueBar(Graphics g, Rectangle hb)
    {
        using (LinearGradientBrush lb = new LinearGradientBrush(new Rectangle(hb.X, hb.Y - 1, hb.Width, hb.Height + 2), Color.Red, Color.Red, 90f))
        {
            lb.InterpolationColors = HueBlend(true);
            Util.FillRound(g, lb, hb, Math.Min(P.Rad, hb.Width / 2));
        }
        float hy = hb.Y + (float)(hh / 360.0) * hb.Height;
        Marker(g, new PointF(hb.X + hb.Width / 2f, hy), ColorMath.FromHsv(hh, 1, 1));
        DragArea(hb, delegate (Point p) { SetHsv(Math.Max(0, Math.Min(359.9, (p.Y - hb.Y) * 360.0 / Math.Max(1, hb.Height))), ss, vv); });
    }

    // квадрат: насиченість × яскравість + смуга тону
    void PaintSquare(Graphics g, Rectangle a)
    {
        int bar = Px(22), gap = Px(14);
        int s = Math.Min(a.Height, a.Width - bar - gap);
        Rectangle sq = new Rectangle(a.X + (a.Width - s - bar - gap) / 2, a.Y + (a.Height - s) / 2, s, s);
        using (GraphicsPath gp = Util.Round(sq, P.Rad))
        {
            using (SolidBrush b = new SolidBrush(ColorMath.FromHsv(hh, 1, 1))) g.FillPath(b, gp);
            using (LinearGradientBrush w = new LinearGradientBrush(new Rectangle(sq.X - 1, sq.Y, sq.Width + 2, sq.Height), Color.White, Color.FromArgb(0, 255, 255, 255), 0f)) g.FillPath(w, gp);
            using (LinearGradientBrush k = new LinearGradientBrush(new Rectangle(sq.X, sq.Y - 1, sq.Width, sq.Height + 2), Color.FromArgb(0, 0, 0, 0), Color.Black, 90f)) g.FillPath(k, gp);
        }
        Marker(g, new PointF(sq.X + (float)ss * sq.Width, sq.Y + (float)(1 - vv) * sq.Height), ColorState.Cur);
        DragArea(sq, delegate (Point p) { SetHsv(hh, (p.X - sq.X) / (double)Math.Max(1, sq.Width), 1 - (p.Y - sq.Y) / (double)Math.Max(1, sq.Height)); });
        HueBar(g, new Rectangle(sq.Right + gap, sq.Y, bar, s));
    }

    // коло: тон по колу, насиченість від центру, яскравість - смуга поруч
    void PaintWheel(Graphics g, Rectangle a)
    {
        int bar = Px(22), gap = Px(14);
        int d = Math.Min(a.Height, a.Width - bar - gap);
        Rectangle wr = new Rectangle(a.X + (a.Width - d - bar - gap) / 2, a.Y + (a.Height - d) / 2, d, d);
        if (wheel == null || wheelD != d || Math.Abs(wheelV - vv) > 0.002)
        {
            if (wheel != null) wheel.Dispose();
            wheel = MakeWheel(d, vv);
            wheelD = d; wheelV = vv;
        }
        g.DrawImage(wheel, wr.X, wr.Y, d, d);
        double ang = hh * Math.PI / 180, rr = ss * d / 2.0;
        Marker(g, new PointF((float)(wr.X + d / 2.0 + Math.Cos(ang) * rr), (float)(wr.Y + d / 2.0 - Math.Sin(ang) * rr)), ColorState.Cur);
        DragArea(wr, delegate (Point p)
        {
            double dx = p.X - (wr.X + d / 2.0), dy = (wr.Y + d / 2.0) - p.Y;
            double h = Math.Atan2(dy, dx) * 180 / Math.PI;
            SetHsv(h < 0 ? h + 360 : h, Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / (d / 2.0)), vv);
        });
        Rectangle vb = new Rectangle(wr.Right + gap, wr.Y, bar, d);
        using (LinearGradientBrush lb = new LinearGradientBrush(new Rectangle(vb.X, vb.Y - 1, vb.Width, vb.Height + 2), ColorMath.FromHsv(hh, ss, 1), Color.Black, 90f))
            Util.FillRound(g, lb, vb, Math.Min(P.Rad, vb.Width / 2));
        Marker(g, new PointF(vb.X + vb.Width / 2f, vb.Y + (float)(1 - vv) * vb.Height), ColorState.Cur);
        DragArea(vb, delegate (Point p) { SetHsv(hh, ss, 1 - (p.Y - vb.Y) / (double)Math.Max(1, vb.Height)); });
    }

    static Bitmap MakeWheel(int d, double v)
    {
        Bitmap b = new Bitmap(d, d, PixelFormat.Format32bppArgb);
        BitmapData bd = b.LockBits(new Rectangle(0, 0, d, d), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        int[] px = new int[d * d];
        double rad = d / 2.0;
        for (int y = 0; y < d; y++)
            for (int x = 0; x < d; x++)
            {
                double dx = x + 0.5 - rad, dy = rad - (y + 0.5);
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > rad) { px[y * d + x] = 0; continue; }
                double h = Math.Atan2(dy, dx) * 180 / Math.PI;
                if (h < 0) h += 360;
                Color c = ColorMath.FromHsv(h, dist / rad, v);
                int alpha = dist > rad - 1 ? (int)(255 * (rad - dist)) : 255;      // згладжений край
                px[y * d + x] = (Math.Max(0, Math.Min(255, alpha)) << 24) | (c.R << 16) | (c.G << 8) | c.B;
            }
        System.Runtime.InteropServices.Marshal.Copy(px, 0, bd.Scan0, px.Length);
        b.UnlockBits(bd);
        return b;
    }

    // соти: шестикутники за тоном і насиченістю + ряд відтінків сірого
    void PaintHoney(Graphics g, Rectangle a)
    {
        const int R = 6;
        double sq3 = Math.Sqrt(3);
        int grayH = Px(34);
        double size = Math.Min(a.Width / (sq3 * (2 * R + 1)), (a.Height - grayH - Px(8)) / (3.0 * R + 2));
        float cx = a.X + a.Width / 2f, cy = a.Y + (float)(size * (1.5 * R + 1));
        int cur = ColorState.Cur.ToArgb();
        PointF[] hit = null;
        for (int q = -R; q <= R; q++)
            for (int rr = -R; rr <= R; rr++)
            {
                int ring = Math.Max(Math.Abs(q), Math.Max(Math.Abs(rr), Math.Abs(q + rr)));
                if (ring > R) continue;
                float x = cx + (float)(size * sq3 * (q + rr / 2.0)), y = cy + (float)(size * 1.5 * rr);
                double h = Math.Atan2(-(y - cy), x - cx) * 180 / Math.PI;
                if (h < 0) h += 360;
                Color c = ring == 0 ? Color.White : ColorMath.FromHsv(h, ring / (double)R, 1);
                PointF[] pts = Hex(x, y, (float)size * 0.96f);
                using (SolidBrush b = new SolidBrush(c)) g.FillPolygon(b, pts);
                if (c.ToArgb() == cur) { hit = pts; }
                Color cc = c;
                Rectangle hr = new Rectangle((int)(x - size * 0.7), (int)(y - size * 0.7), (int)(size * 1.4), (int)(size * 1.4));
                Hot(hr, delegate { Commit(cc); });
                if (Over(hr)) using (Pen hp = new Pen(ColorMath.Ink(c), Px(1.5f))) g.DrawPolygon(hp, pts);
            }
        // сірі
        int gn = 13;
        float gs = (float)Math.Min(size, (a.Width / (double)gn) / sq3);
        float gy = a.Bottom - grayH / 2f;
        float gx0 = a.X + (a.Width - (float)(gs * sq3 * gn)) / 2f + (float)(gs * sq3 / 2);
        for (int i = 0; i < gn; i++)
        {
            int v = (int)Math.Round(255 * (1 - i / (double)(gn - 1)));
            Color c = Color.FromArgb(255, v, v, v);
            float x = gx0 + (float)(gs * sq3 * i);
            PointF[] pts = Hex(x, gy, gs * 0.96f);
            using (SolidBrush b = new SolidBrush(c)) g.FillPolygon(b, pts);
            if (c.ToArgb() == cur) { hit = pts; }
            Color cc = c;
            Rectangle hr = new Rectangle((int)(x - gs * 0.8f), (int)(gy - gs * 0.8f), (int)(gs * 1.6f), (int)(gs * 1.6f));
            Hot(hr, delegate { Commit(cc); });
            if (Over(hr)) using (Pen hp = new Pen(ColorMath.Ink(c), Px(1.5f))) g.DrawPolygon(hp, pts);
        }
        if (hit != null)
        {
            using (Pen o = new Pen(Color.FromArgb(180, 0, 0, 0), Px(4))) g.DrawPolygon(o, hit);
            using (Pen w = new Pen(Color.White, Px(2))) g.DrawPolygon(w, hit);
        }
    }

    static PointF[] Hex(float x, float y, float s)
    {
        PointF[] p = new PointF[6];
        for (int i = 0; i < 6; i++)
        {
            double an = (60 * i - 30) * Math.PI / 180;
            p[i] = new PointF(x + (float)(s * Math.Cos(an)), y + (float)(s * Math.Sin(an)));
        }
        return p;
    }

    // спектр: по горизонталі тон, по вертикалі світлота (зверху біле, внизу чорне)
    void PaintSpectrum(Graphics g, Rectangle a)
    {
        using (GraphicsPath gp = Util.Round(a, P.Rad))
        {
            using (LinearGradientBrush lb = new LinearGradientBrush(new Rectangle(a.X - 1, a.Y, a.Width + 2, a.Height), Color.Red, Color.Red, 0f))
            {
                lb.InterpolationColors = HueBlend(false);
                g.FillPath(lb, gp);
            }
            ColorBlend cb = new ColorBlend(3);
            cb.Colors = new Color[] { Color.White, Color.FromArgb(0, 128, 128, 128), Color.Black };
            cb.Positions = new float[] { 0f, 0.5f, 1f };
            using (LinearGradientBrush lv = new LinearGradientBrush(new Rectangle(a.X, a.Y - 1, a.Width, a.Height + 2), Color.White, Color.Black, 90f))
            {
                lv.InterpolationColors = cb;
                g.FillPath(lv, gp);
            }
        }
        double h, s, l;
        ColorMath.ToHsl(ColorState.Cur, out h, out s, out l);
        if (s < 0.001) h = hh;
        Marker(g, new PointF(a.X + (float)(h / 360) * a.Width, a.Y + (float)(1 - l) * a.Height), ColorState.Cur);
        DragArea(a, delegate (Point p)
        {
            double hx = Math.Max(0, Math.Min(359.9, (p.X - a.X) * 360.0 / Math.Max(1, a.Width)));
            double ly = 1 - Math.Max(0, Math.Min(1, (p.Y - a.Y) / (double)Math.Max(1, a.Height)));
            Color c = ColorMath.FromHsl(hx, 1, ly);
            ColorState.Cur = c;
            double h2, s2, v2;
            ColorMath.ToHsv(c, out h2, out s2, out v2);
            hh = hx; ss = s2; vv = v2;
            Bar.ColorChanged(false);
            Invalidate();
        });
    }

    // повзунки: R, G, B і тон, насиченість, світлота (HSL)
    void PaintSliders(Graphics g, Rectangle a)
    {
        Color c = ColorState.Cur;
        double h, s, l;
        ColorMath.ToHsl(c, out h, out s, out l);
        if (s < 0.001) h = hh;
        string[] nm = { "R", "G", "B", L.T("Тон"), L.T("Насичен."), L.T("Світлота") };
        double[] val = { c.R / 255.0, c.G / 255.0, c.B / 255.0, h / 360, s, l };
        string[] txt = { c.R.ToString(), c.G.ToString(), c.B.ToString(), Math.Round(h) + "°", Math.Round(s * 100) + "%", Math.Round(l * 100) + "%" };
        int rowH = a.Height / 6, lw = Px(78), vw = Px(52);
        for (int i = 0; i < 6; i++)
        {
            Rectangle row = new Rectangle(a.X, a.Y + i * rowH, a.Width, rowH);
            TxtL(g, nm[i], FS, new Rectangle(row.X, row.Y, lw, row.Height), P.Sub);
            TxtR(g, txt[i], F, new Rectangle(row.Right - vw, row.Y, vw, row.Height), P.Fg);
            int th = Px(14);
            Rectangle tr = new Rectangle(row.X + lw, row.Y + (row.Height - th) / 2, row.Width - lw - vw - Px(10), th);
            ColorBlend cb = new ColorBlend(7);
            for (int k = 0; k < 7; k++)
            {
                double t = k / 6.0;
                Color kc;
                switch (i)
                {
                    case 0: kc = Color.FromArgb(255, (int)(t * 255), c.G, c.B); break;
                    case 1: kc = Color.FromArgb(255, c.R, (int)(t * 255), c.B); break;
                    case 2: kc = Color.FromArgb(255, c.R, c.G, (int)(t * 255)); break;
                    case 3: kc = ColorMath.FromHsl(t * 360, Math.Max(0.6, s), Math.Max(0.35, Math.Min(0.65, l))); break;
                    case 4: kc = ColorMath.FromHsl(h, t, l); break;
                    default: kc = ColorMath.FromHsl(h, s, t); break;
                }
                cb.Colors[k] = kc; cb.Positions[k] = (float)t;
            }
            using (LinearGradientBrush lb = new LinearGradientBrush(new Rectangle(tr.X - 1, tr.Y, tr.Width + 2, tr.Height), Color.Black, Color.White, 0f))
            {
                lb.InterpolationColors = cb;
                Util.FillRound(g, lb, tr, th / 2);
            }
            Marker(g, new PointF(tr.X + (float)val[i] * tr.Width, tr.Y + th / 2f), c);
            int idx = i;
            Rectangle hit = new Rectangle(tr.X - Px(8), row.Y, tr.Width + Px(16), row.Height);
            DragArea(hit, delegate (Point p)
            {
                double t = Math.Max(0, Math.Min(1, (p.X - tr.X) / (double)Math.Max(1, tr.Width)));
                Color b = ColorState.Cur;
                double h1, s1, l1;
                ColorMath.ToHsl(b, out h1, out s1, out l1);
                if (s1 < 0.001) h1 = hh;
                Color n;
                switch (idx)
                {
                    case 0: n = Color.FromArgb(255, (int)Math.Round(t * 255), b.G, b.B); break;
                    case 1: n = Color.FromArgb(255, b.R, (int)Math.Round(t * 255), b.B); break;
                    case 2: n = Color.FromArgb(255, b.R, b.G, (int)Math.Round(t * 255)); break;
                    case 3: n = ColorMath.FromHsl(Math.Min(359.9, t * 360), s1, l1); h1 = Math.Min(359.9, t * 360); break;
                    case 4: n = ColorMath.FromHsl(h1, t, l1); break;
                    default: n = ColorMath.FromHsl(h1, s1, t); break;
                }
                ColorState.Cur = n;
                double h2, s2, v2;
                ColorMath.ToHsv(n, out h2, out s2, out v2);
                hh = (s2 > 0.0001 && v2 > 0.0001) ? h2 : h1; ss = s2; vv = v2;
                Bar.ColorChanged(false);
                Invalidate();
            });
        }
    }

    // зразки: тони × світлота + сірі, і 16 базових кольорів CSS
    static readonly string[] Basic = { "000000", "808080", "C0C0C0", "FFFFFF", "800000", "FF0000", "808000", "FFFF00", "008000", "00FF00", "008080", "00FFFF", "000080", "0000FF", "800080", "FF00FF" };

    void PaintSwatches(Graphics g, Rectangle a)
    {
        int cols = 13, rows = 8;
        int basicH = Px(30);
        int gap = Px(3);
        int cw = (a.Width - gap * (cols - 1)) / cols;
        int ch = Math.Min(cw, (a.Height - basicH - Px(10) - gap * (rows - 1)) / rows);
        int x0 = a.X + (a.Width - (cw * cols + gap * (cols - 1))) / 2;
        int cur = ColorState.Cur.ToArgb();
        for (int rr = 0; rr < rows; rr++)
            for (int cc = 0; cc < cols; cc++)
            {
                double l = 0.9 - rr * (0.75 / (rows - 1));
                Color c = cc == cols - 1 ? ColorMath.FromHsl(0, 0, l) : ColorMath.FromHsl(cc * 30, 0.85, l);
                Rectangle sr = new Rectangle(x0 + cc * (cw + gap), a.Y + rr * (ch + gap), cw, ch);
                SwatchCell(g, sr, c, cur);
            }
        int by = a.Y + rows * (ch + gap) + Px(8);
        int bwid = (a.Width - gap * (Basic.Length - 1)) / Basic.Length;
        for (int i = 0; i < Basic.Length; i++)
        {
            Color c;
            ColorMath.TryHex(Basic[i], out c);
            SwatchCell(g, new Rectangle(a.X + i * (bwid + gap), by, bwid, Math.Min(basicH, a.Bottom - by)), c, cur);
        }
    }

    void SwatchCell(Graphics g, Rectangle sr, Color c, int cur)
    {
        int rad = Math.Max(2, Math.Min(P.Rad / 2, sr.Height / 3));
        using (SolidBrush b = new SolidBrush(c)) Util.FillRound(g, b, sr, rad);
        bool sel = c.ToArgb() == cur, hov = Over(sr);
        if (sel || hov)
            using (GraphicsPath gp = Util.Round(Rectangle.Inflate(sr, -1, -1), rad))
            using (Pen pn = new Pen(ColorMath.Ink(c), sel ? Px(2.5f) : Px(1.2f))) g.DrawPath(pn, gp);
        Color cc = c;
        Hot(sr, delegate { Commit(cc); });
    }
}
