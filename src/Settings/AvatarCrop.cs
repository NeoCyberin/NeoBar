using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

// ======================================================================
//  Вибір ділянки фото для значка профілю: коло, всередині якого - пікселі майбутнього значка.
//  Зображення тягнуть мишею, масштабують коліщатком чи повзунком; стрілки зсувають на 1 піксель (Shift - на 10).
// ======================================================================
static class PhotoSrc
{
    public const long MaxFile = 30L * 1024 * 1024;        // файл-джерело, байт
    public const long MaxPixels = 64L * 1000 * 1000;      // ~64 мегапікселі: більше - надто багато пам'яті на розкодування
    public const int WorkSide = 4096;                     // робоча копія для вибору ділянки: довша сторона не більша
    public const int Out = 256;                           // розмір значка, що зберігається

    // Робоча копія зображення (з урахуванням повороту з EXIF). Кидає InvalidOperationException з поясненням для користувача.
    public static Bitmap Load(string file)
    {
        FileInfo fi = new FileInfo(file);
        if (!fi.Exists) throw new InvalidOperationException(L.T("Файл не знайдено"));
        if (fi.Length > MaxFile) throw new InvalidOperationException(L.T("Файл завеликий (понад ") + (MaxFile / 1024 / 1024) + L.T(" МБ)"));
        using (FileStream fs = File.OpenRead(file))
        using (Image src = Image.FromStream(fs, false, false))
        {
            int w = src.Width, h = src.Height;
            if (w < 1 || h < 1) throw new InvalidOperationException(L.T("Не вдалося відкрити зображення"));
            if ((long)w * h > MaxPixels) throw new InvalidOperationException(L.T("Зображення завелике (понад ") + (MaxPixels / 1000000) + L.T(" мегапікселів)"));
            float k = Math.Min(1f, (float)WorkSide / Math.Max(w, h));
            int nw = Math.Max(1, (int)Math.Round(w * k)), nh = Math.Max(1, (int)Math.Round(h * k));
            Bitmap b = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(src, new Rectangle(0, 0, nw, nh), 0, 0, w, h, GraphicsUnit.Pixel);
            }
            try
            {
                if (Array.IndexOf(src.PropertyIdList, 0x0112) >= 0)
                {
                    byte[] v = src.GetPropertyItem(0x0112).Value;
                    int o = v != null && v.Length > 0 ? v[0] : 1;
                    RotateFlipType rf = RotateFlipType.RotateNoneFlipNone;
                    switch (o)
                    {
                        case 2: rf = RotateFlipType.RotateNoneFlipX; break;
                        case 3: rf = RotateFlipType.Rotate180FlipNone; break;
                        case 4: rf = RotateFlipType.RotateNoneFlipY; break;
                        case 5: rf = RotateFlipType.Rotate90FlipX; break;
                        case 6: rf = RotateFlipType.Rotate90FlipNone; break;
                        case 7: rf = RotateFlipType.Rotate270FlipX; break;
                        case 8: rf = RotateFlipType.Rotate270FlipNone; break;
                    }
                    if (rf != RotateFlipType.RotateNoneFlipNone) b.RotateFlip(rf);
                }
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
            return b;
        }
    }

    // квадрат d×d з точки (x, y) робочої копії → значок Out×Out
    public static Bitmap Crop(Bitmap work, int x, int y, int d)
    {
        Bitmap b = new Bitmap(Out, Out, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(work, new Rectangle(0, 0, Out, Out), new Rectangle(x, y, d, d), GraphicsUnit.Pixel);
        }
        return b;
    }
}

sealed class AvatarCrop : SbWindow
{
    readonly Bitmap work;
    readonly int dMax, dMin;
    int d;                          // сторона вибраного квадрата (діаметр кола), пікселі зображення
    double fx, fy;                  // лівий верхній кут квадрата (дробові - під час перетягування)
    bool hasDrag; Point lastDrag;
    public Bitmap Result;           // вибраний значок (null - скасовано)

    public AvatarCrop(Theme t, float sc, Bitmap src) : base(t, sc, L.T("Ділянка для значка"), 440, 520)
    { Glyph = "\uE7A8";
        work = src;
        dMax = Math.Min(work.Width, work.Height);
        dMin = Math.Min(dMax, 32);
        Reset();
    }

    void Reset()
    {
        d = dMax;
        fx = (work.Width - d) / 2.0;
        fy = (work.Height - d) / 2.0;
    }

    int Ox { get { return (int)Math.Round(fx); } }
    int Oy { get { return (int)Math.Round(fy); } }

    void Clamp()
    {
        d = Math.Max(dMin, Math.Min(dMax, d));
        fx = Math.Max(0, Math.Min(work.Width - d, fx));
        fy = Math.Max(0, Math.Min(work.Height - d, fy));
    }

    void SetD(int nd)
    {
        double cx = Ox + d / 2.0, cy = Oy + d / 2.0;
        d = Math.Max(dMin, Math.Min(dMax, nd));
        fx = cx - d / 2.0; fy = cy - d / 2.0;
        fx = Math.Round(fx); fy = Math.Round(fy);
        Clamp();
    }

    void Nudge(int dx, int dy)
    {
        fx = Ox + dx; fy = Oy + dy;
        Clamp();
        Invalidate();
    }

    void Zoom(int dir)
    {
        int nd = dir > 0 ? Math.Min(d - 1, (int)Math.Round(d * 0.93)) : Math.Max(d + 1, (int)Math.Round(d / 0.93));
        SetD(nd);
        Invalidate();
    }

    int ZoomV()
    {
        if (dMin >= dMax) return 0;
        double v = Math.Log((double)d / dMax) / Math.Log((double)dMin / dMax) * 100.0;
        return (int)Math.Round(Math.Max(0, Math.Min(100, v)));
    }

    void SetV(int v)
    {
        if (dMin >= dMax) return;
        SetD((int)Math.Round(dMax * Math.Pow((double)dMin / dMax, v / 100.0)));
    }

    void Done()
    {
        Clamp();
        Result = PhotoSrc.Crop(work, Ox, Oy, d);
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        int step = (keyData & Keys.Shift) != 0 ? 10 : 1;
        switch (keyData & Keys.KeyCode)
        {
            case Keys.Left: Nudge(-step, 0); return true;
            case Keys.Right: Nudge(step, 0); return true;
            case Keys.Up: Nudge(0, -step); return true;
            case Keys.Down: Nudge(0, step); return true;
            case Keys.Add: case Keys.Oemplus: Zoom(1); return true;
            case Keys.Subtract: case Keys.OemMinus: Zoom(-1); return true;
            case Keys.Enter: Done(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnDragEnd() { hasDrag = false; base.OnDragEnd(); }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        int vh = Px(300);
        Rectangle vp = new Rectangle(r.X, r.Y, r.Width, vh);
        int circ = Px(268);
        double s = (double)circ / d;               // екранних пікселів на піксель зображення
        double cx = Ox + d / 2.0, cy = Oy + d / 2.0;
        PointF vc = new PointF(vp.X + vp.Width / 2f, vp.Y + vp.Height / 2f);

        GraphicsState st0 = g.Save();
        using (GraphicsPath clip = Util.Round(vp, Math.Max(2, P.Rad)))
        {
            g.SetClip(clip, CombineMode.Intersect);
            using (SolidBrush b = new SolidBrush(Util.Blend(P.Bg, P.Fg, 0.08f))) g.FillRectangle(b, vp);
            // видима частина зображення (цілі пікселі), щоб не малювати все
            int sx0 = Math.Max(0, (int)Math.Floor(cx - vp.Width / 2.0 / s)), sy0 = Math.Max(0, (int)Math.Floor(cy - vp.Height / 2.0 / s));
            int sx1 = Math.Min(work.Width, (int)Math.Ceiling(cx + vp.Width / 2.0 / s)), sy1 = Math.Min(work.Height, (int)Math.Ceiling(cy + vp.Height / 2.0 / s));
            if (sx1 > sx0 && sy1 > sy0)
            {
                RectangleF dst = new RectangleF((float)(vc.X + (sx0 - cx) * s), (float)(vc.Y + (sy0 - cy) * s), (float)((sx1 - sx0) * s), (float)((sy1 - sy0) * s));
                bool px = s >= 2.5;                 // сильне збільшення: показуємо справжні пікселі
                g.InterpolationMode = px ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = px ? PixelOffsetMode.Half : PixelOffsetMode.HighQuality;
                g.DrawImage(work, dst, new RectangleF(sx0, sy0, sx1 - sx0, sy1 - sy0), GraphicsUnit.Pixel);
                g.PixelOffsetMode = PixelOffsetMode.Default;
            }
            // затемнення поза колом
            RectangleF cr = new RectangleF(vc.X - circ / 2f, vc.Y - circ / 2f, circ, circ);
            using (GraphicsPath dim = new GraphicsPath(FillMode.Alternate))
            using (SolidBrush db = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
            {
                dim.AddRectangle(vp);
                dim.AddEllipse(cr);
                g.FillPath(db, dim);
            }
            using (Pen o = new Pen(Color.FromArgb(120, 0, 0, 0), 3f)) g.DrawEllipse(o, cr);
            using (Pen w = new Pen(Color.FromArgb(235, 255, 255, 255), 1.5f)) g.DrawEllipse(w, cr);
        }
        g.Restore(st0);
        using (Pen lp = new Pen(P.Line, 1f)) using (GraphicsPath bp = Util.Round(vp, Math.Max(2, P.Rad))) g.DrawPath(lp, bp);
        Wheel(vp, delegate (int dir) { Zoom(dir); });
        DragArea(vp, delegate (Point pt)
        {
            if (!hasDrag) { hasDrag = true; lastDrag = pt; return; }
            fx -= (pt.X - lastDrag.X) / s; fy -= (pt.Y - lastDrag.Y) / s;
            lastDrag = pt;
            Clamp();
        }, Cursors.SizeAll);

        int y = vp.Bottom + Px(12);
        TxtL(g, L.T("Масштаб"), FS, new Rectangle(r.X, y, Px(80), Px(18)), P.Sub);
        int rb = Px(90);
        Slider(g, new Rectangle(r.X + Px(70), y - Px(5), r.Width - Px(70) - rb - Px(10), Px(28)), ZoomV(), SetV);
        Btn(g, new Rectangle(r.Right - rb, y - Px(6), rb, Px(28)), L.T("Скинути"), delegate { Reset(); }, 2, true);
        y += Px(32);
        TxtL(g, L.T("Ділянка: x ") + Ox + ", y " + Oy + " · " + d + "×" + d + L.T(" пікс. з ") + work.Width + "×" + work.Height, FS, new Rectangle(r.X, y, r.Width, Px(18)), P.Sub);
        y += Px(22);
        TxtW(g, L.T("Тягніть зображення мишею, коліщатко - масштаб. Стрілки зсувають на 1 піксель (Shift - на 10), +/− змінюють масштаб, Enter - готово."),
            FS, new Rectangle(r.X, y, r.Width, Px(34)), P.Sub);

        int bh = Px(34), bw = Px(110);
        Rectangle ok = new Rectangle(r.Right - bw, r.Bottom - bh, bw, bh);
        Btn(g, ok, L.T("Готово"), Done, 1, true);
        Btn(g, new Rectangle(ok.X - bw - Px(8), ok.Y, bw, bh), L.T("Скасувати"), delegate { Close(); }, 0, true);
    }
}
