using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// ======================================================================
//  Скляний фон: розмиті шпалери робочого столу
// ======================================================================
static class Backdrop
{
    internal static void DrawWall(Graphics g, Image im, Size mon, int style)
    {
        float iw = im.Width, ih = im.Height;
        float x = 0, y = 0, w = mon.Width, h = mon.Height;
        if (style == 6) // Fit
        {
            float s = Math.Min(mon.Width / iw, mon.Height / ih);
            w = iw * s; h = ih * s;
            x = (mon.Width - w) / 2; y = (mon.Height - h) / 2;
        }
        else if (style == 0) // Center
        {
            w = iw; h = ih;
            x = (mon.Width - w) / 2; y = (mon.Height - h) / 2;
        }
        else if (style != 2) // Fill / Span: заповнити з обрізанням
        {
            float s = Math.Max(mon.Width / iw, mon.Height / ih);
            w = iw * s; h = ih * s;
            x = (mon.Width - w) / 2; y = (mon.Height - h) / 2;
        }
        g.DrawImage(im, x, y, w, h);
    }

    // bar - прямокутник панелі в екранних координатах, mon - межі монітора
    internal static Bitmap RenderWall(string wp, int style, Rectangle bar, Rectangle mon, int w, int h, int k, bool blur)
    {
        int sw = Math.Max(2, w / k), sh = Math.Max(2, h / k);

        Bitmap small = new Bitmap(sw, sh, PixelFormat.Format32bppRgb);
        using (Graphics g = Graphics.FromImage(small))
        {
            g.Clear(SystemColors.Desktop);
            if (wp != null)
            {
                try
                {
                    using (FileStream fs = new FileStream(wp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (Image im = Image.FromStream(fs, false, false))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.ScaleTransform(sw / (float)w, sh / (float)h);
                        g.TranslateTransform(-(bar.X - mon.X), -(bar.Y - mon.Y));
                        DrawWall(g, im, mon.Size, style);
                    }
                }
                catch (Exception skipEx) { SbLog.Skip(skipEx); }
            }
        }
        if (!blur) return small;

        // розмиття = сильне зменшення й плавне бікубічне збільшення (краї притиснуті, без темної рамки)
        Bitmap big = Upscale(small, w, h);
        small.Dispose();
        return big;
    }

    static int Clamp8(float v)
    {
        return v < 0 ? 0 : (v > 255 ? 255 : (int)(v + 0.5f));
    }

    // Ваги Catmull-Rom для кожного пікселя призначення; індекси притискаються до країв
    static void Taps(int srcN, int dstN, int[] idx, float[] wgt)
    {
        float scale = srcN / (float)dstN;
        for (int d = 0; d < dstN; d++)
        {
            float sp = (d + 0.5f) * scale - 0.5f;
            int s0 = (int)Math.Floor(sp);
            float t = sp - s0;
            float t2 = t * t, t3 = t2 * t;
            float[] w4 =
            {
                -0.5f * t3 + t2 - 0.5f * t,
                1.5f * t3 - 2.5f * t2 + 1f,
                -1.5f * t3 + 2f * t2 + 0.5f * t,
                0.5f * t3 - 0.5f * t2
            };
            for (int i = 0; i < 4; i++)
            {
                int ix = s0 - 1 + i;
                idx[d * 4 + i] = ix < 0 ? 0 : (ix >= srcN ? srcN - 1 : ix);
                wgt[d * 4 + i] = w4[i];
            }
        }
    }

    internal static Bitmap Upscale(Bitmap small, int w, int h)
    {
        int sw = small.Width, sh = small.Height;
        int[] src = new int[sw * sh];
        BitmapData sd = small.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        Marshal.Copy(sd.Scan0, src, 0, src.Length);
        small.UnlockBits(sd);

        int[] xi = new int[w * 4]; float[] xw = new float[w * 4];
        int[] yi = new int[h * 4]; float[] yw = new float[h * 4];
        Taps(sw, w, xi, xw);
        Taps(sh, h, yi, yw);

        int[] dst = new int[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float r = 0, g = 0, b = 0;
                for (int j = 0; j < 4; j++)
                {
                    float wy = yw[y * 4 + j];
                    int row = yi[y * 4 + j] * sw;
                    for (int i = 0; i < 4; i++)
                    {
                        float wg = wy * xw[x * 4 + i];
                        int p = src[row + xi[x * 4 + i]];
                        r += wg * ((p >> 16) & 255);
                        g += wg * ((p >> 8) & 255);
                        b += wg * (p & 255);
                    }
                }
                dst[y * w + x] = unchecked((int)0xFF000000) | (Clamp8(r) << 16) | (Clamp8(g) << 8) | Clamp8(b);
            }
        }
        Bitmap big = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        BitmapData bd = big.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        Marshal.Copy(dst, 0, bd.Scan0, dst.Length);
        big.UnlockBits(bd);
        return big;
    }

    public static Bitmap Build(Rectangle bar, Rectangle mon, int w, int h, int k, bool blur)
    {
        return RenderWall(Wall.Path_(), Wall.StyleCode(), bar, mon, w, h, k, blur);
    }
}
