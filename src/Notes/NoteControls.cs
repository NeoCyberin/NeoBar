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

// Дрібні елементи керування нотаток: кнопки-піктограми, повзунок непрозорості, заокруглені поле й кнопка

enum Glyph { Plus, Theme, Lock, Close, Bold, Italic, Underline, Strike, Bullets, Emoji, Size, Grip }

sealed class GlyphButton : Control
{
    public Glyph Kind;
    public Color Ink = Color.White;
    public Color Fill = Color.Gray;        // для Glyph.Theme: колір фону теми
    bool active, hover;
    public float LockT;                    // для Glyph.Lock: 0 - замок відкритий (режим редагування), 1 - закритий
    System.Windows.Forms.Timer lockTimer;
    public bool LockBusy { get { return lockTimer != null; } }

    // анімація замка: дужка опускається (0 -> 1) або піднімається (1 -> 0)
    public void AnimateLock(float from, float to, int ms, Action done)
    {
        if (lockTimer != null) return;
        DateTime t0 = DateTime.Now;
        LockT = from;
        lockTimer = new System.Windows.Forms.Timer();
        lockTimer.Interval = 16;
        lockTimer.Tick += delegate
        {
            float f = (float)((DateTime.Now - t0).TotalMilliseconds / ms);
            if (f >= 1f)
            {
                lockTimer.Stop(); lockTimer.Dispose(); lockTimer = null;
                LockT = to; Invalidate();
                if (done != null) done();
                return;
            }
            float e = 1f - (1f - f) * (1f - f);
            LockT = from + (to - from) * e;
            Invalidate();
        };
        lockTimer.Start();
    }

    public bool Active { get { return active; } set { if (active != value) { active = value; Invalidate(); } } }

    public GlyphButton(Glyph k)
    {
        Kind = k;
        DoubleBuffered = true;
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Cursor = k == Glyph.Grip ? Cursors.SizeNWSE : Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using (SolidBrush b = new SolidBrush(Parent != null ? Parent.BackColor : BackColor))
            e.Graphics.FillRectangle(b, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        Rectangle r = ClientRectangle;
        if (Kind != Glyph.Grip && (hover || active))
        {
            Rectangle br = Rectangle.Inflate(r, -Math.Max(1, r.Width / 12), -Math.Max(1, r.Height / 10));
            using (SolidBrush b = new SolidBrush(Color.FromArgb(active ? 70 : 38, Ink)))
                g.FillRectangle(b, br);
        }
        float cx = r.Width / 2f, cy = r.Height / 2f;
        float u = Math.Min(r.Width, r.Height);
        using (Pen pen = new Pen(Ink, Math.Max(1.4f, u * 0.055f)))
        using (SolidBrush ink = new SolidBrush(Ink))
        {
            pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
            switch (Kind)
            {
                case Glyph.Plus:
                    g.DrawLine(pen, cx - u * 0.17f, cy, cx + u * 0.17f, cy);
                    g.DrawLine(pen, cx, cy - u * 0.17f, cx, cy + u * 0.17f);
                    break;
                case Glyph.Close:
                    g.DrawLine(pen, cx - u * 0.14f, cy - u * 0.14f, cx + u * 0.14f, cy + u * 0.14f);
                    g.DrawLine(pen, cx - u * 0.14f, cy + u * 0.14f, cx + u * 0.14f, cy - u * 0.14f);
                    break;
                case Glyph.Theme:
                {
                    float rr = u * 0.23f;
                    using (SolidBrush fb = new SolidBrush(Fill)) g.FillEllipse(fb, cx - rr, cy - rr, rr * 2, rr * 2);
                    g.DrawEllipse(pen, cx - rr, cy - rr, rr * 2, rr * 2);
                    break;
                }
                case Glyph.Lock:
                {
                    float ls = u * 0.58f;
                    Ico.DrawLock(g, new RectangleF(cx - ls / 2, cy - ls / 2 + u * 0.03f, ls, ls), Ink, LockT);
                    break;
                }
                case Glyph.Emoji:
                {
                    float rr = u * 0.27f;
                    g.DrawEllipse(pen, cx - rr, cy - rr, rr * 2, rr * 2);
                    float er = Math.Max(1.3f, u * 0.035f);
                    g.FillEllipse(ink, cx - rr * 0.42f - er, cy - rr * 0.3f - er, er * 2, er * 2);
                    g.FillEllipse(ink, cx + rr * 0.42f - er, cy - rr * 0.3f - er, er * 2, er * 2);
                    g.DrawArc(pen, cx - rr * 0.55f, cy - rr * 0.35f, rr * 1.1f, rr * 1.1f, 20, 140);
                    break;
                }
                case Glyph.Size:
                {
                    // велика й мала літера "A" поруч
                    using (Font f1 = new Font("Segoe UI", u * 0.50f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (Font f2 = new Font("Segoe UI", u * 0.32f, FontStyle.Bold, GraphicsUnit.Pixel))
                    {
                        TextRenderer.DrawText(g, "A", f1, new Point((int)(cx - u * 0.40f), (int)(cy - u * 0.36f)), Ink, TextFormatFlags.NoPadding);
                        TextRenderer.DrawText(g, "A", f2, new Point((int)(cx + u * 0.06f), (int)(cy - u * 0.12f)), Ink, TextFormatFlags.NoPadding);
                    }
                    break;
                }
                case Glyph.Bullets:
                {
                    float rr = Math.Max(1.5f, u * 0.04f);
                    for (int i = -1; i <= 1; i++)
                    {
                        float y = cy + i * u * 0.16f;
                        g.FillEllipse(ink, cx - u * 0.22f - rr, y - rr, rr * 2, rr * 2);
                        g.DrawLine(pen, cx - u * 0.1f, y, cx + u * 0.24f, y);
                    }
                    break;
                }
                case Glyph.Grip:
                {
                    for (int i = 0; i < 3; i++)
                        g.DrawLine(pen, r.Right - 3 - i * u * 0.16f, r.Bottom - 3, r.Right - 3, r.Bottom - 3 - i * u * 0.16f);
                    break;
                }
                case Glyph.Bold:
                case Glyph.Italic:
                case Glyph.Underline:
                case Glyph.Strike:
                {
                    FontStyle fs = Kind == Glyph.Bold ? FontStyle.Bold : Kind == Glyph.Italic ? FontStyle.Italic :
                                   Kind == Glyph.Underline ? FontStyle.Underline : FontStyle.Strikeout;
                    string s = Kind == Glyph.Strike ? "ab" : Kind == Glyph.Bold ? "B" : Kind == Glyph.Italic ? "I" : "U";
                    using (Font f = new Font("Segoe UI", u * 0.38f, fs, GraphicsUnit.Pixel))
                    using (StringFormat sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                        g.DrawString(s, f, ink, new RectangleF(0, 0, r.Width, r.Height), sf);
                    }
                    break;
                }
            }
        }
    }
}

// ---------- повзунок прозорості фону в шапці ----------

sealed class OpacityBar : Control
{
    public int Value = 90;               // 0..100
    public Color Ink = Color.White;
    public event Action<int> Changed;
    public event Action Started, Finished;
    bool drag;

    public OpacityBar()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Cursor = Cursors.Hand;
    }

    int TrackL { get { return Notes.Px(10); } }
    int TrackR { get { return Width - Notes.Px(40); } }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using (SolidBrush b = new SolidBrush(Parent != null ? Parent.BackColor : BackColor))
            e.Graphics.FillRectangle(b, ClientRectangle);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        drag = true;
        if (Started != null) Started();
        From(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (drag) From(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!drag) return;
        drag = false;
        if (Finished != null) Finished();
    }

    void From(int x)
    {
        int l = TrackL, rr = Math.Max(l + 1, TrackR);
        int v = (int)Math.Round((Math.Max(l, Math.Min(rr, x)) - l) * 100.0 / (rr - l));
        if (v == Value) return;
        Value = v;
        Invalidate();
        if (Changed != null) Changed(v);
    }

    public void SetValue(int v) { Value = Math.Max(0, Math.Min(100, v)); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        int l = TrackL, rr = Math.Max(l + 1, TrackR);
        float cy = Height / 2f;
        float th = Math.Max(2f, Notes.Px(3));
        float kx = l + (rr - l) * Value / 100f;
        using (SolidBrush tr = new SolidBrush(Color.FromArgb(70, Ink)))
            g.FillRectangle(tr, l, cy - th / 2, rr - l, th);
        using (SolidBrush fl = new SolidBrush(Color.FromArgb(220, Ink)))
            g.FillRectangle(fl, l, cy - th / 2, kx - l, th);
        float kr = Math.Max(4f, Notes.Px(5.5f));
        using (SolidBrush kb = new SolidBrush(Ink))
            g.FillEllipse(kb, kx - kr, cy - kr, kr * 2, kr * 2);
        using (Font f = new Font("Segoe UI", Math.Max(8f, Height * 0.42f), FontStyle.Regular, GraphicsUnit.Pixel))
        using (SolidBrush tb = new SolidBrush(Ink))
        using (StringFormat sf = new StringFormat())
        {
            sf.Alignment = StringAlignment.Far; sf.LineAlignment = StringAlignment.Center;
            g.DrawString(Value + "%", f, tb, new RectangleF(rr + Notes.Px(4), 0, Width - rr - Notes.Px(6), Height), sf);
        }
    }
}

// ---------- вікно нотатки ----------

// поле введення зі скругленими кутами, внутрішньою тінню і світінням у фокусі
sealed class RoundBox : Control
{
    public readonly TextBox Tb = new TextBox();
    public Color Fill, Edge, Glow;
    bool focus;
    readonly int g;

    public RoundBox()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        g = Notes.Px(3);
        Tb.BorderStyle = BorderStyle.None;
        Tb.Enter += delegate { focus = true; Invalidate(); };
        Tb.Leave += delegate { focus = false; Invalidate(); };
        Controls.Add(Tb);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        Tb.BackColor = Fill; Tb.ForeColor = ForeColor;
        int pad = Notes.Px(12);
        Tb.SetBounds(g + pad, (Height - Tb.Height) / 2, Math.Max(10, Width - g * 2 - pad * 2), Tb.Height);
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Tb.Focus(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics gr = e.Graphics;
        gr.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = new Rectangle(g, g, Width - g * 2 - 1, Height - g * 2 - 1);
        int rad = r.Height / 2;
        if (focus)
            for (int i = 3; i >= 1; i--)
                using (GraphicsPath gp = Util.Round(new Rectangle(r.X - i, r.Y - i, r.Width + i * 2, r.Height + i * 2), rad + i))
                using (Pen p = new Pen(Color.FromArgb(34 + (3 - i) * 14, Glow), 1.6f))
                    gr.DrawPath(p, gp);
        // без заливки: усередині той самий колір, що й у капсули (Fill), лише тонкий контур
        using (GraphicsPath gp = Util.Round(r, rad))
        using (Pen p = new Pen(focus ? Glow : Edge, focus ? 1.6f : 1f))
            gr.DrawPath(p, gp);
    }
}

// скруглена кнопка: градієнт, блік, світіння при наведенні, "натискання"
sealed class RoundButton : Control
{
    public Color C1, C2, Edge, Glow;
    bool hot, down;

    public RoundButton()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; down = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); down = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); down = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics gr = e.Graphics;
        gr.SmoothingMode = SmoothingMode.AntiAlias;
        int m = Notes.Px(3);
        Rectangle r = new Rectangle(m, m, Width - m * 2 - 1, Height - m * 2 - 1);
        if (r.Width < 4 || r.Height < 4) return;
        int rad = r.Height / 2;
        if (hot)
            for (int i = 3; i >= 1; i--)
                using (GraphicsPath gp = Util.Round(new Rectangle(r.X - i, r.Y - i, r.Width + i * 2, r.Height + i * 2), rad + i))
                using (Pen p = new Pen(Color.FromArgb(30 + (3 - i) * 16, Glow), 1.6f))
                    gr.DrawPath(p, gp);
        Color a = hot ? Util.Blend(C1, Color.White, 0.18f) : C1, b = C2;
        if (down) { Color t = a; a = b; b = t; }
        using (GraphicsPath gp = Util.Round(r, rad))
        {
            using (LinearGradientBrush br = new LinearGradientBrush(r, a, b, 90f)) gr.FillPath(br, gp);
            // блік у верхній половині (обрізаний контуром кнопки)
            Rectangle hl = new Rectangle(r.X, r.Y, r.Width + 1, r.Height / 2);
            gr.SetClip(gp);
            using (LinearGradientBrush hb = new LinearGradientBrush(hl, Color.FromArgb(down ? 14 : 50, 255, 255, 255), Color.FromArgb(4, 255, 255, 255), 90f))
                gr.FillRectangle(hb, hl);
            gr.ResetClip();
            using (Pen p = new Pen(hot ? Glow : Edge, hot ? 1.5f : 1f)) gr.DrawPath(p, gp);
        }
        Rectangle tr = new Rectangle(r.X, r.Y + (down ? 1 : 0), r.Width, r.Height);
        TextRenderer.DrawText(gr, Text, Font, tr, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }
}
