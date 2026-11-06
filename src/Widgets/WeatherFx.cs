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

using System.Text.RegularExpressions;

// ----------------------------------------------------------------------
//  Анімація погоди: дощ, сніг, туман, спека, гроза - у розширеній панелі й на віджеті на робочому столі
// ----------------------------------------------------------------------
static class WeatherFx
{
    static readonly DateTime t0 = DateTime.Now;

    // "" - без анімації
    public static string Kind
    {
        get
        {
            int c = Widgets.WCode;
            if (c < 0) return "";
            if (c >= 95) return "storm";
            if ((c >= 71 && c <= 77) || c == 85 || c == 86) return "snow";
            if ((c >= 51 && c <= 67) || (c >= 80 && c <= 82)) return "rain";
            if (c == 45 || c == 48) return "fog";
            if (!double.IsNaN(Widgets.Temp) && Widgets.Temp >= 28 && c <= 3 && Widgets.WDay) return "heat";
            return "";
        }
    }

    public static bool Active(Cfg c) { return (c == null || c.Anim) && Kind.Length > 0; }

    static float Frac(double v) { return (float)(v - Math.Floor(v)); }
    static float Hash(int i, int k) { uint x = (uint)(i * 374761393 + k * 668265263); x = (x ^ (x >> 13)) * 1274126177; return (x & 0xFFFF) / 65535f; }

    // малює поверх фону картки (до тексту); r - область, rad - заокруглення для обрізання
    public static void Draw(Graphics g, Rectangle r, int rad, float sc, bool lightBg)
    {
        string kind = Kind;
        if (kind.Length == 0 || r.Width < 10 || r.Height < 10) return;
        double t = (DateTime.Now - t0).TotalSeconds;
        GraphicsState st = g.Save();
        SmoothingMode sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath clip = Util.Round(r, Math.Max(1, rad))) g.SetClip(clip, CombineMode.Intersect);
        float W = r.Width, H = r.Height;
        if (kind == "rain" || kind == "storm")
        {
            int n = (int)(W * H / (900 * sc * sc)) + 12;
            Color rc = lightBg ? Color.FromArgb(110, 40, 110, 210) : Color.FromArgb(95, 140, 190, 255);
            using (Pen p = new Pen(rc, Math.Max(1f, 1.3f * sc)))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                for (int i = 0; i < n; i++)
                {
                    float sp = 0.9f + Hash(i, 1) * 0.8f;                  // скорість падіння (висот картки за секунду)
                    float len = (9 + Hash(i, 2) * 9) * sc;
                    float y = Frac(Hash(i, 3) + t * sp) * (H + len) - len;
                    float x = Hash(i, 4) * (W + H * 0.3f) - y * 0.3f;
                    g.DrawLine(p, r.X + x, r.Y + y, r.X + x - len * 0.3f, r.Y + y + len);
                }
            }
            if (kind == "storm")
            {
                // спалах блискавки раз на кілька секунд
                double ph = t % 5.3;
                if (ph < 0.18) using (SolidBrush fl = new SolidBrush(Color.FromArgb((int)(70 * (1 - ph / 0.18)), 255, 255, 255))) g.FillRectangle(fl, r);
            }
        }
        else if (kind == "snow")
        {
            int n = (int)(W * H / (1400 * sc * sc)) + 10;
            using (SolidBrush b = new SolidBrush(lightBg ? Color.FromArgb(150, 150, 170, 200) : Color.FromArgb(150, 255, 255, 255)))
                for (int i = 0; i < n; i++)
                {
                    float sp = 0.12f + Hash(i, 1) * 0.12f;
                    float d = (2 + Hash(i, 2) * 3) * sc;
                    float y = Frac(Hash(i, 3) + t * sp) * (H + d) - d;
                    float x = Hash(i, 4) * W + (float)Math.Sin(t * (0.8 + Hash(i, 5)) + i) * 7 * sc;
                    g.FillEllipse(b, r.X + x, r.Y + y, d, d);
                }
        }
        else if (kind == "fog")
        {
            Color fc = lightBg ? Color.FromArgb(34, 90, 100, 120) : Color.FromArgb(30, 220, 225, 235);
            using (SolidBrush b = new SolidBrush(fc))
                for (int i = 0; i < 7; i++)
                {
                    float bw = W * (0.55f + Hash(i, 1) * 0.5f), bh = H * (0.16f + Hash(i, 2) * 0.14f);
                    float sp = (6 + Hash(i, 3) * 10) * sc;
                    float x = Frac(Hash(i, 4) + t * sp / (W + bw)) * (W + bw) - bw;
                    float y = H * (0.1f + Hash(i, 5) * 0.8f) - bh / 2 + (float)Math.Sin(t * 0.4 + i) * 3 * sc;
                    g.FillEllipse(b, r.X + x, r.Y + y, bw, bh);
                }
        }
        else if (kind == "heat")
        {
            // марево: хвилясті лінії, що повільно піднімаються, і теплий відблиск
            using (LinearGradientBrush lg = new LinearGradientBrush(new PointF(r.X, r.Bottom), new PointF(r.X, r.Y), Color.FromArgb(46, 255, 140, 40), Color.FromArgb(0, 255, 140, 40)))
                g.FillRectangle(lg, r);
            using (Pen p = new Pen(Color.FromArgb(60, 255, 170, 70), Math.Max(1f, 1.4f * sc)))
                for (int i = 0; i < 5; i++)
                {
                    float y0 = H - Frac(Hash(i, 1) + t * 0.12) * (H + 20 * sc);
                    PointF[] pts = new PointF[24];
                    for (int k = 0; k < pts.Length; k++)
                    {
                        float x = W * k / (pts.Length - 1);
                        pts[k] = new PointF(r.X + x, r.Y + y0 + (float)Math.Sin(x / (14 * sc) + t * 2.2 + i) * 3 * sc);
                    }
                    g.DrawLines(p, pts);
                }
        }
        g.SmoothingMode = sm;
        g.Restore(st);
    }
}
