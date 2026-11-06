using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// ----------------------------------------------------------------------
//  Вікно з попіксельною прозорістю (для віджетів на робочому столі й оверлея)
// ----------------------------------------------------------------------
class LayeredForm : Form
{
    protected int ExtraEx;     // додаткові розширені стилі (напр. прозорість для кліків)
    protected byte PushAlpha = 255;   // загальна непрозорість вікна (плавне згасання без перемальовування)

    // Поверхня вікна: одна DIB (BGRA, премультиплікована), що лише росте. Раніше кожен кадр робив
    // GetHbitmap - новий GDI-бітмап і повну копію зображення; для кубиків (60 кадрів/с) і оверлея в грі це зайве.
    IntPtr memDC, dib, dibOld, dibBits;
    Bitmap surf;
    int surfW, surfH;
    Size shown;                // розмір останнього показаного кадру

    public LayeredForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        AppIcon.Apply(this);
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x80 | ExtraEx;   // LAYERED | TOOLWINDOW
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    bool EnsureSurface(int w, int h)
    {
        if (w <= 0 || h <= 0) return false;
        if (surf != null && w <= surfW && h <= surfH) return true;
        int nw = Math.Max(w, surfW), nh = Math.Max(h, surfH);
        FreeSurface();
        try
        {
            memDC = Win.CreateCompatibleDC(IntPtr.Zero);
            dib = Win.CreateDib32(nw, nh, out dibBits);
            if (memDC == IntPtr.Zero || dib == IntPtr.Zero || dibBits == IntPtr.Zero) { FreeSurface(); return false; }
            dibOld = Win.SelectObject(memDC, dib);
            surf = new Bitmap(nw, nh, nw * 4, PixelFormat.Format32bppPArgb, dibBits);
            surfW = nw; surfH = nh;
            return true;
        }
        catch { FreeSurface(); return false; }
    }

    void FreeSurface()
    {
        if (surf != null) { surf.Dispose(); surf = null; }
        if (memDC != IntPtr.Zero && dibOld != IntPtr.Zero) Win.SelectObject(memDC, dibOld);
        if (dib != IntPtr.Zero) Win.DeleteObject(dib);
        if (memDC != IntPtr.Zero) Win.DeleteDC(memDC);
        memDC = dib = dibOld = dibBits = IntPtr.Zero;
        surfW = surfH = 0;
        shown = Size.Empty;
    }

    void Ulw(Point at, Size sz)
    {
        if (!IsHandleCreated) CreateHandle();
        Win.POINT pos = new Win.POINT(); pos.x = at.X; pos.y = at.Y;
        Win.SIZE size = new Win.SIZE(); size.cx = sz.Width; size.cy = sz.Height;
        Win.POINT src = new Win.POINT();
        Win.BLENDFUNCTION bl = new Win.BLENDFUNCTION(); bl.BlendOp = 0; bl.SourceConstantAlpha = PushAlpha; bl.AlphaFormat = 1;   // AC_SRC_ALPHA
        Win.UpdateLayeredWindow(Handle, IntPtr.Zero, ref pos, ref size, memDC, ref src, 0, ref bl, 2);                          // ULW_ALPHA
        shown = sz;
    }

    // Малювати прямо в поверхню: прозоре полотно w×h. Після малювання - PushFrame.
    // null - поверхню створити не вдалося (тоді малюють у звичайний Bitmap і викликають Push)
    protected Graphics BeginFrame(int w, int h)
    {
        if (!EnsureSurface(w, h)) return null;
        Graphics g = Graphics.FromImage(surf);
        g.SetClip(new Rectangle(0, 0, w, h));
        g.CompositingMode = CompositingMode.SourceCopy;
        using (SolidBrush clear = new SolidBrush(Color.Transparent)) g.FillRectangle(clear, 0, 0, w, h);
        g.CompositingMode = CompositingMode.SourceOver;
        return g;
    }

    protected void PushFrame(Point at, Size sz)
    {
        try { Ulw(at, sz); } catch { }
    }

    // bmp - 32bpp ARGB розміром з вікно
    public void Push(Bitmap bmp, Point at)
    {
        try
        {
            int w = bmp.Width, h = bmp.Height;
            if (!EnsureSurface(w, h)) { PushSlow(bmp, at); return; }
            using (Graphics g = Graphics.FromImage(surf))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(bmp, new Rectangle(0, 0, w, h), 0, 0, w, h, GraphicsUnit.Pixel);
            }
            Ulw(at, new Size(w, h));
        }
        catch { }
    }

    // Лише нова загальна непрозорість (PushAlpha) для щойно показаного кадру - без копіювання пікселів
    protected void Refade(Bitmap frame, Point at)
    {
        if (surf != null && shown == frame.Size) { try { Ulw(at, shown); } catch { } }
        else Push(frame, at);
    }

    // запасний шлях (DIB не створилась): тимчасовий GDI-бітмап на кадр
    void PushSlow(Bitmap bmp, Point at)
    {
        if (!IsHandleCreated) CreateHandle();
        IntPtr scr = IntPtr.Zero, mem = IntPtr.Zero, hb = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            scr = Win.GetDC(IntPtr.Zero);
            mem = Win.CreateCompatibleDC(scr);
            hb = bmp.GetHbitmap(Color.FromArgb(0));
            old = Win.SelectObject(mem, hb);
            Win.POINT pos = new Win.POINT(); pos.x = at.X; pos.y = at.Y;
            Win.SIZE sz = new Win.SIZE(); sz.cx = bmp.Width; sz.cy = bmp.Height;
            Win.POINT src = new Win.POINT();
            Win.BLENDFUNCTION bl = new Win.BLENDFUNCTION(); bl.BlendOp = 0; bl.SourceConstantAlpha = PushAlpha; bl.AlphaFormat = 1;
            Win.UpdateLayeredWindow(Handle, scr, ref pos, ref sz, mem, ref src, 0, ref bl, 2);
        }
        catch { }
        finally
        {
            try
            {
                if (old != IntPtr.Zero) Win.SelectObject(mem, old);
                if (hb != IntPtr.Zero) Win.DeleteObject(hb);
                if (mem != IntPtr.Zero) Win.DeleteDC(mem);
                if (scr != IntPtr.Zero) Win.ReleaseDC(IntPtr.Zero, scr);
            }
            catch { }
        }
    }

    protected override void Dispose(bool disposing)
    {
        try { FreeSurface(); } catch { }
        base.Dispose(disposing);
    }
}

// Плавне згасання й поява даних під час оновлення (погода, мережа)
sealed class FadeAnim
{
    DateTime start = DateTime.MinValue, end = DateTime.MinValue;
    const double Out = 320, In = 520;
    const float Low = 0.18f;

    public void Start() { start = DateTime.Now; end = DateTime.MinValue; }
    public void End() { if (start != DateTime.MinValue) end = DateTime.Now; }

    static float Smooth(double t) { t = Math.Max(0, Math.Min(1, t)); return (float)(t * t * (3 - 2 * t)); }

    public float Level
    {
        get
        {
            if (start == DateTime.MinValue) return 1f;
            DateTime now = DateTime.Now;
            double since = (now - start).TotalMilliseconds;
            if (since > 15000) { start = DateTime.MinValue; return 1f; }     // запобіжник: відповідь так і не прийшла
            float lvl = 1f - (1f - Low) * Smooth(since / Out);
            if (end != DateTime.MinValue && since >= Out)
            {
                double from = Math.Max(0, (now - end).TotalMilliseconds);
                if (from >= In) { start = DateTime.MinValue; return 1f; }
                lvl = Low + (1f - Low) * Smooth(from / In);
            }
            return lvl;
        }
    }

    public bool Active { get { float f = Level; return start != DateTime.MinValue; } }
}
