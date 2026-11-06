using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// ======================================================================
//  Перегляд оверлея в налаштуваннях: масштаб (коліщатко, кнопки), перетягування мишею,
//  реальні дані або приклад. Оверлей малюється одразу в потрібному масштабі - без розмиття.
// ======================================================================
sealed class OvPreviewBox : Control
{
    public float Zoom = 1f;                 // 1 = реальний розмір оверлея
    public float BaseScale = 1f;            // масштаб Windows (DPI панелі)
    public bool Live = true;
    public event Action ViewChanged;
    Bitmap bmp;
    Point pan;
    bool centered = true, drag;
    Point dragFrom, panFrom;

    public OvPreviewBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.SizeAll;
        TabStop = false;
    }

    public void Rebuild()
    {
        OverlayForm.RefScale = BaseScale;
        Bitmap nb;
        try { nb = OverlayForm.Render(Zoom * BaseScale, !Live, "", true); } finally { OverlayForm.RefScale = 0; }
        Bitmap old = bmp;
        bmp = nb;
        if (old != null) old.Dispose();
        if (centered) pan = new Point((Width - bmp.Width) / 2, (Height - bmp.Height) / 2);
        Clamp();
        Invalidate();
    }

    void Clamp()
    {
        if (bmp == null) return;
        int keep = 40;
        pan.X = Math.Max(keep - bmp.Width, Math.Min(Width - keep, pan.X));
        pan.Y = Math.Max(keep - bmp.Height, Math.Min(Height - keep, pan.Y));
    }

    // масштаб навколо точки (px, py) у координатах елемента
    void ZoomAround(float factor, int px, int py)
    {
        float nz = Math.Max(0.5f, Math.Min(5f, Zoom * factor));
        if (Math.Abs(nz - Zoom) < 0.001f) return;
        float r = nz / Zoom;
        pan = new Point((int)(px - (px - pan.X) * r), (int)(py - (py - pan.Y) * r));
        Zoom = nz;
        Rebuild();
        if (ViewChanged != null) ViewChanged();
    }

    public void ZoomBy(float factor)
    {
        if (bmp == null) { Zoom = Math.Max(0.5f, Math.Min(5f, Zoom * factor)); return; }
        if (centered)
        {
            float nz = Math.Max(0.5f, Math.Min(5f, Zoom * factor));
            Zoom = nz;
            Rebuild();
            if (ViewChanged != null) ViewChanged();
        }
        else ZoomAround(factor, Width / 2, Height / 2);
    }

    public void ResetView()
    {
        Zoom = 1f;
        centered = true;
        Rebuild();
        if (ViewChanged != null) ViewChanged();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (bmp != null && centered) { pan = new Point((Width - bmp.Width) / 2, (Height - bmp.Height) / 2); Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        drag = true; dragFrom = e.Location; panFrom = pan; centered = false;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!drag) return;
        pan = new Point(panFrom.X + e.X - dragFrom.X, panFrom.Y + e.Y - dragFrom.Y);
        Clamp();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        drag = false;
        Capture = false;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        ResetView();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        centered = false;
        ZoomAround(e.Delta > 0 ? 1.15f : 1f / 1.15f, e.X, e.Y);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        Focus();          // щоб коліщатко працювало без попереднього клацання
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        // тло, схоже на картинку гри: темний градієнт із легкою сіткою
        using (LinearGradientBrush br = new LinearGradientBrush(ClientRectangle, Color.FromArgb(52, 64, 82), Color.FromArgb(22, 26, 34), 70f))
            g.FillRectangle(br, ClientRectangle);
        using (Pen gp = new Pen(Color.FromArgb(14, 255, 255, 255)))
        {
            for (int x = 0; x < Width; x += 24) g.DrawLine(gp, x, 0, x, Height);
            for (int y = 0; y < Height; y += 24) g.DrawLine(gp, 0, y, Width, y);
        }
        if (bmp != null) g.DrawImageUnscaled(bmp, pan);
        using (Pen bp = new Pen(Color.FromArgb(110, 110, 120))) g.DrawRectangle(bp, 0, 0, Width - 1, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && bmp != null) { bmp.Dispose(); bmp = null; }
        base.Dispose(disposing);
    }
}
