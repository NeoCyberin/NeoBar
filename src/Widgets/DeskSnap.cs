using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

// ======================================================================
//  Прив'язка віджетів і нотаток на робочому столі під час перетягування:
//  до країв екрана (з відступом), до країв і сусідства з іншими віджетами, інакше - до сітки.
//  Alt під час перетягування - без прив'язки (точне положення).
// ======================================================================
static class DeskSnap
{
    public static bool On = true;
    static float Sc { get { return ClockData.Bar != null ? ClockData.Bar.Dpi : 1f; } }
    static int Px(float v) { return (int)Math.Round(v * Sc); }

    public static int Grid { get { return Px(16); } }      // крок сітки
    static int Gap { get { return Px(12); } }               // відступ від краю екрана й між віджетами
    static int Pull { get { return Px(14); } }              // наскільки близько, щоб «прилипнути»

    // інші віджети й нотатки (екранні прямокутники вмісту), крім того, що тягнуть
    public static Func<object, List<Rectangle>> Others;

    // r - куди хочуть поставити вміст (екранні координати); повертає скориговане місце лівого верхнього кута
    public static Point Snap(Rectangle r, object self)
    {
        if (!On || (Control.ModifierKeys & Keys.Alt) != 0) return r.Location;
        Rectangle wa = Screen.FromRectangle(r).WorkingArea;
        List<Rectangle> os = Others != null ? Others(self) : new List<Rectangle>();
        int g = Gap;
        List<int> xs = new List<int> { wa.Left + g, wa.Right - g - r.Width };
        List<int> ys = new List<int> { wa.Top + g, wa.Bottom - g - r.Height };
        foreach (Rectangle o in os)
        {
            if (o.IsEmpty) continue;
            // по горизонталі: вирівняти краї або стати поруч (якщо по вертикалі вони поряд)
            bool nearV = r.Bottom + g * 3 > o.Top && r.Top - g * 3 < o.Bottom;
            bool nearH = r.Right + g * 3 > o.Left && r.Left - g * 3 < o.Right;
            xs.Add(o.Left); xs.Add(o.Right - r.Width);
            if (nearV) { xs.Add(o.Right + g); xs.Add(o.Left - g - r.Width); }
            ys.Add(o.Top); ys.Add(o.Bottom - r.Height);
            if (nearH) { ys.Add(o.Bottom + g); ys.Add(o.Top - g - r.Height); }
        }
        int x = Best(r.X, xs, wa.Left + g, Grid);
        int y = Best(r.Y, ys, wa.Top + g, Grid);
        return new Point(x, y);
    }

    static int Best(int v, List<int> cands, int origin, int grid)
    {
        int best = v, bd = int.MaxValue;
        foreach (int c in cands) { int d = Math.Abs(c - v); if (d < bd) { bd = d; best = c; } }
        if (bd <= Pull) return best;
        if (grid <= 1) return v;
        return origin + (int)Math.Round((v - origin) / (double)grid) * grid;
    }
}
