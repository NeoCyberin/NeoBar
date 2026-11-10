using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

// ======================================================================
//  Погода: зараз, погодинно на добу, прогноз на тиждень
// ======================================================================
sealed class WeatherWindow : SbWindow
{
    int Pick { get { return Bar.Config.Fahrenheit ? 1 : 0; } }   // тема: 0 європейська, 1 американська (та сама, що в налаштуваннях)
    Bitmap[] previews;
    string previewKey = "";

    readonly Action fadeTick;
    Rectangle fxRect;
    readonly System.Windows.Forms.Timer fx = new System.Windows.Forms.Timer();

    public WeatherWindow(NeoBar bar) : base(bar, L.T("Погода"), 500, 830)
    { Glyph = "\uE706";
        string cl0 = Widgets.CityLabel(bar.Config);
        Caption = cl0.Length > 0 ? L.T("Погода · ") + cl0 : L.T("Погода");   // місто видно одразу, до першого малювання
        fadeTick = delegate { if (!IsDisposed && IsHandleCreated) Invalidate(); };
        NeoBar.WeatherFadeTick += fadeTick;
        // анімація погоди: ~25 кадрів/с, перемальовується лише картка «зараз»
        fx.Interval = 40;
        fx.Tick += delegate { if (Tab == 0 && WeatherFx.Active(Bar.Config) && !fxRect.IsEmpty) Invalidate(fxRect); };
        fx.Start();
    }

    static readonly string[] ThemeIds = { "", "us" };
    static readonly string[] ThemeNames = { L.T("Європейський"), L.T("Американський") };

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        NeoBar.WeatherFadeTick -= fadeTick;
        fx.Stop(); fx.Dispose();
        if (previews != null) foreach (Bitmap b in previews) if (b != null) b.Dispose();
        previews = null;
        base.OnFormClosed(e);
    }

    // галерея тем погоди (як у годинника): вибір змінює одиниці на панелі й у вікні, кнопка додає віджет на стіл
    protected override void ThemeUpdated() { previewKey = ""; }

    void PaintGallery(Graphics g, Rectangle r)
    {
        TxtL(g, L.T("Теми погоди"), FB, new Rectangle(r.X, r.Y, r.Width, Px(28)), P.Fg);
        string key = Widgets.WUpdated.Ticks + "|" + Widgets.WCode + "|" + T.Id + "|" + Bar.LabelFont.Name + Bar.LabelFont.Size;
        if (previews == null || key != previewKey)
        {
            previewKey = key;
            if (previews != null) foreach (Bitmap ob in previews) if (ob != null) ob.Dispose();
            previews = new Bitmap[2];
            for (int i = 0; i < 2; i++)
            {
                DeskSpec s = new DeskSpec(); s.Kind = "weather"; s.Opacity = 100; s.Opt = ThemeIds[i];
                try { previews[i] = DeskClock.Preview(s); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            }
        }
        int y = r.Y + Px(34);
        int gap = Px(8), tw = (r.Width - gap) / 2, th = r.Bottom - y;
        for (int i = 0; i < 2; i++)
        {
            Rectangle tr = new Rectangle(r.X + i * (tw + gap), y, tw, th);
            bool sel = Pick == i;
            Card(g, tr, sel ? Util.Blend(P.Bg, P.Acc, 0.28f) : (Over(tr) ? Util.Blend(P.Card, P.Fg, 0.07f) : P.Card));
            if (sel)
                using (GraphicsPath gp = Util.Round(tr, P.Rad))
                using (Pen pn = new Pen(P.Acc, Math.Max(1.5f, Px(2)))) g.DrawPath(pn, gp);
            Rectangle ir = new Rectangle(tr.X + Px(6), tr.Y + Px(6), tr.Width - Px(12), tr.Height - Px(30));
            if (previews[i] != null)
            {
                float k = Math.Min(ir.Width / (float)previews[i].Width, ir.Height / (float)previews[i].Height);
                int w = (int)(previews[i].Width * k), h = (int)(previews[i].Height * k);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(previews[i], new Rectangle(ir.X + (ir.Width - w) / 2, ir.Y + (ir.Height - h) / 2, w, h));
            }
            TxtC(g, ThemeNames[i], FS, new Rectangle(tr.X, tr.Bottom - Px(22), tr.Width, Px(18)), sel ? P.Fg : P.Sub);
            bool us = i == 1;
            Hot(tr, delegate { Bar.SetUnits(us); Invalidate(); });
        }
    }

    protected override void HeadButtons(Graphics g, ref int right)
    {
        Rectangle r3 = new Rectangle(right - Px(32), Px(7), Px(32), Px(32));
        IconBtn(g, r3, "desk", delegate { ClockData.AddDesk("weather", ThemeIds[Pick]); }, false);
        right = r3.Left - Px(4);
        Rectangle r4 = new Rectangle(right - Px(32), Px(7), Px(32), Px(32));
        IconBtn(g, r4, "sync", Widgets.WeatherBusy ? null : (Action)delegate { Bar.ForceWeather(); Invalidate(); }, false);
        right = r4.Left - Px(4);
    }

    string Deg(double v) { return Widgets.Deg(v, Bar.Config.Fahrenheit); }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        PaintBody0(g, r);
        float f = Widgets.WeatherFade;
        if (f < 0.999f)
            using (SolidBrush ov = new SolidBrush(Color.FromArgb((int)Math.Round(255 * (1f - f)), P.Bg))) g.FillRectangle(ov, r);
    }

    void PaintBody0(Graphics g, Rectangle r)
    {
        Cfg c = Bar.Config;
        bool fahr = c.Fahrenheit;
        string pl = Widgets.CityLabel(c);
        Caption = pl.Length > 0 ? L.T("Погода · ") + pl : L.T("Погода");   // місто - у заголовку розширеної панелі
        if (double.IsNaN(Widgets.Temp))
        {
            TxtC(g, Widgets.WeatherBusy ? L.T("Завантаження…") : (Widgets.WErr.Length > 0 ? L.T("Погода: ") + Widgets.WErr : L.T("Немає даних")), F, r, P.Sub);
            return;
        }
        // зараз
        Rectangle now = new Rectangle(r.X, r.Y, r.Width, Px(124));
        Card(g, now);
        fxRect = now;
        if (WeatherFx.Active(c)) WeatherFx.Draw(g, now, P.Rad, Sc, Util.Lum(P.Bg) > 140);
        int dx = Math.Max(0, (now.Width - Px(372)) / 2 - Px(14));      // вміст картки по центру
        int isz = Px(84);
        Widgets.DrawWeather(g, new Rectangle(now.X + Px(14) + dx, now.Y + (now.Height - isz) / 2, isz, isz), Widgets.WCode, Widgets.WDay, T, P.Fg);
        int tx = now.X + Px(110) + dx;
        TxtL(g, Deg(Widgets.Temp) + (fahr ? "F" : "C"), FHuge, new Rectangle(tx, now.Y + Px(8), Px(150), Px(60)), P.Fg);
        string desc = Widgets.WeatherText(Widgets.WCode);
        desc = char.ToUpper(desc[0]) + desc.Substring(1);
        TxtL(g, desc, FB, new Rectangle(tx, now.Y + Px(66), now.Right - tx - Px(10), Px(22)), P.Fg);
        string fl = L.T("Відчувається ") + Deg(Widgets.FeelsLike);
        TxtL(g, fl, FS, new Rectangle(tx, now.Y + Px(88), now.Right - tx - Px(10), Px(20)), P.Sub);
        int cx = now.X + Px(270) + dx;
        string[] k = { L.T("Вітер"), L.T("Вологість"), L.T("Тиск") };
        string[] v = {
            Widgets.WindText(fahr),
            Widgets.Hum + "%",
            Widgets.PressText(fahr)
        };
        for (int i = 0; i < 3; i++)
        {
            TxtL(g, k[i], FS, new Rectangle(cx, now.Y + Px(14) + i * Px(34), now.Right - cx - Px(10), Px(16)), P.Sub);
            TxtL(g, v[i], FB, new Rectangle(cx, now.Y + Px(29) + i * Px(34), now.Right - cx - Px(10), Px(18)), P.Fg);
        }

        // погодинно (24 год)
        int y = now.Bottom + Px(12);
        Rectangle hr = new Rectangle(r.X, y, r.Width, Px(156));
        Card(g, hr);
        PaintHours(g, Rectangle.Inflate(hr, -Px(12), -Px(8)));

        // тиждень
        y = hr.Bottom + Px(12);
        int gh = Px(168);
        Rectangle wr = new Rectangle(r.X, y, r.Width, r.Bottom - y - Px(22) - gh - Px(8));
        Card(g, wr);
        PaintWeek(g, Rectangle.Inflate(wr, -Px(12), -Px(6)));
        PaintGallery(g, new Rectangle(r.X, r.Bottom - gh, r.Width, gh));

        string place = Widgets.CityLabel(c);
        string upd = Widgets.WSaved != DateTime.MinValue ? L.T("оновлено ") + Widgets.WSaved.ToString("HH:mm") : "";
        if (Widgets.WStale) upd = L.T("немає зв'язку, показано збережене (") + Widgets.WSaved.ToString("d MMM HH:mm", Widgets.UK) + ")";
        TxtL(g, place + (upd.Length > 0 ? " · " + upd : "") + " · Open-Meteo", FS, new Rectangle(r.X, r.Bottom - gh - Px(24), r.Width, Px(18)), Widgets.WStale ? P.Warn : P.Sub);
    }

    void PaintHours(Graphics g, Rectangle r)
    {
        List<DateTime> at = Widgets.HourAt;
        if (at.Count == 0) { TxtC(g, L.T("Немає погодинного прогнозу"), FS, r, P.Sub); return; }
        DateTime now = DateTime.Now;
        int first = 0;
        while (first < at.Count && at[first].AddHours(1) <= now) first++;
        int n = Math.Min(24, at.Count - first);
        if (n < 2) return;
        double mn = double.MaxValue, mx = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            double t = Widgets.HourT[first + i];
            if (double.IsNaN(t)) continue;
            mn = Math.Min(mn, t); mx = Math.Max(mx, t);
        }
        if (mx - mn < 4) { double m = (mx + mn) / 2; mn = m - 2; mx = m + 2; }
        Rectangle plot = new Rectangle(r.X + Px(10), r.Y + Px(26), r.Width - Px(20), Px(46));
        PointF[] pts = new PointF[n];
        for (int i = 0; i < n; i++)
        {
            double t = Widgets.HourT[first + i];
            float x = plot.X + (float)i * plot.Width / (n - 1);
            float yv = double.IsNaN(t) ? plot.Bottom : plot.Bottom - (float)((t - mn) / (mx - mn)) * plot.Height;
            pts[i] = new PointF(x, yv);
        }
        using (GraphicsPath fp = new GraphicsPath())
        {
            fp.AddLines(pts);
            fp.AddLine(pts[n - 1].X, pts[n - 1].Y, pts[n - 1].X, plot.Bottom + Px(4));
            fp.AddLine(pts[n - 1].X, plot.Bottom + Px(4), pts[0].X, plot.Bottom + Px(4));
            fp.CloseFigure();
            using (LinearGradientBrush lb = new LinearGradientBrush(new Rectangle(plot.X, plot.Y - 2, plot.Width, plot.Height + 8), Color.FromArgb(90, P.Acc), Color.FromArgb(5, P.Acc), 90f))
                g.FillPath(lb, fp);
        }
        using (Pen pn = new Pen(P.Acc, Math.Max(1.5f, Px(2)))) { pn.LineJoin = LineJoin.Round; g.DrawLines(pn, pts); }
        for (int i = 0; i < n; i += 3)
        {
            int idx = first + i;
            float x = pts[i].X;
            Rectangle lab = new Rectangle((int)x - Px(24), (int)pts[i].Y - Px(22), Px(48), Px(18));
            TxtC(g, Deg(Widgets.HourT[idx]), FS, lab, P.Fg);
            int is2 = Px(22);
            Widgets.DrawWeather(g, new Rectangle((int)x - is2 / 2, plot.Bottom + Px(8), is2, is2), Widgets.HourCode[idx],
                at[idx].Hour >= 7 && at[idx].Hour < 20, T, P.Fg);
            double rp = idx < Widgets.HourRain.Count ? Widgets.HourRain[idx] : double.NaN;
            TxtC(g, i == 0 ? L.T("зараз") : at[idx].ToString("HH:mm"), FS, new Rectangle((int)x - Px(30), plot.Bottom + Px(32), Px(60), Px(16)), P.Sub);
            if (!double.IsNaN(rp) && rp >= 20)
                TxtC(g, Math.Round(rp) + "%", FS, new Rectangle((int)x - Px(30), plot.Bottom + Px(47), Px(60), Px(16)), P.Acc);
        }
    }

    void PaintWeek(Graphics g, Rectangle r)
    {
        List<Widgets.Day> days = Widgets.Days;
        if (days.Count == 0) { TxtC(g, L.T("Немає прогнозу на тиждень"), FS, r, P.Sub); return; }
        double mn = double.MaxValue, mx = double.MinValue;
        foreach (Widgets.Day d in days)
        {
            if (!double.IsNaN(d.Min)) mn = Math.Min(mn, d.Min);
            if (!double.IsNaN(d.Max)) mx = Math.Max(mx, d.Max);
        }
        if (mx <= mn) mx = mn + 1;
        int n = Math.Min(8, days.Count);
        int rowH = Math.Max(Px(30), r.Height / n);
        for (int i = 0; i < n; i++)
        {
            Widgets.Day d = days[i];
            int y = r.Y + i * rowH;
            if (y + rowH > r.Bottom + Px(4)) break;
            if (i > 0) using (Pen lp = new Pen(P.Line, 1f)) g.DrawLine(lp, r.X, y, r.Right, y);
            string name = d.Date == DateTime.Today ? L.T("Сьогодні") : (d.Date == DateTime.Today.AddDays(1) ? L.T("Завтра") : d.Date.ToString("ddd, d", Widgets.UK));
            if (name.Length > 0) name = char.ToUpper(name[0]) + name.Substring(1);
            bool we = d.Date.DayOfWeek == DayOfWeek.Saturday || d.Date.DayOfWeek == DayOfWeek.Sunday;
            TxtL(g, name, d.Date == DateTime.Today ? FB : F, new Rectangle(r.X, y, Px(92), rowH), we ? Util.Blend(P.Fg, P.Bad, 0.4f) : P.Fg);
            int is2 = Math.Min(rowH - Px(6), Px(28));
            Widgets.DrawWeather(g, new Rectangle(r.X + Px(96), y + (rowH - is2) / 2, is2, is2), d.Code, true, T, P.Fg);
            string rp = !double.IsNaN(d.RainProb) && d.RainProb >= 10 ? Math.Round(d.RainProb) + "%" : "";
            TxtL(g, rp, FS, new Rectangle(r.X + Px(130), y, Px(40), rowH), P.Acc);
            // смуга мін..макс у межах тижня
            int bx = r.X + Px(214), bw = r.Width - Px(214) - Px(56);
            TxtR(g, Deg(d.Min), F, new Rectangle(r.X + Px(166), y, Px(44), rowH), P.Sub);
            Rectangle bar = new Rectangle(bx, y + rowH / 2 - Px(3), bw, Px(6));
            using (SolidBrush b = new SolidBrush(Util.Blend(P.Bg, P.Fg, 0.12f))) Util.FillRound(g, b, bar, bar.Height / 2);
            if (!double.IsNaN(d.Min) && !double.IsNaN(d.Max))
            {
                int x1 = bx + (int)((d.Min - mn) / (mx - mn) * bw), x2 = bx + (int)((d.Max - mn) / (mx - mn) * bw);
                Rectangle fr = new Rectangle(x1, bar.Y, Math.Max(bar.Height, x2 - x1), bar.Height);
                using (LinearGradientBrush lb = new LinearGradientBrush(new Rectangle(bx - 1, bar.Y, bw + 2, bar.Height), Color.FromArgb(90, 170, 255), Color.FromArgb(255, 150, 60), 0f))
                    Util.FillRound(g, lb, fr, bar.Height / 2);
            }
            TxtR(g, Deg(d.Max), FB, new Rectangle(r.Right - Px(48), y, Px(48), rowH), P.Fg);
        }
    }
}
