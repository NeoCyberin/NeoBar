using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// ======================================================================
//  Монітор системи → «Історія»: температури й навантаження за добу (Watch, щохвилини) з подіями -
//  критичні сповіщення NeoBar і знахідки журналу подій Windows (зависання відеодрайвера, нестача пам'яті,
//  раптове вимкнення). Наведіть мишу - значення в ту хвилину.
// ======================================================================
sealed partial class PerfWindow
{
    const int TabHistory = 4;
    readonly List<Rectangle> hsPlots = new List<Rectangle>();

    sealed class HsLine { public string Name; public Color C; public Func<HistPoint, float> V; public string Unit; }

    void PaintHistory(Graphics g, Rectangle r)
    {
        hsPlots.Clear();
        List<HistPoint> pts = Watch.Points();
        DateTime t1 = DateTime.Now, t0 = t1.AddHours(-24);
        string sub;
        if (!Watch.History) sub = L.T("Історію вимкнено: Налаштування → Панель → «Нагляд за системою».");
        else if (pts.Count == 0) sub = L.T("Збираю дані: перша точка з'явиться за хвилину. Історія зберігається, поки NeoBar працює.");
        else sub = L.F("Щохвилини, від {0}. Найвища температура й середнє навантаження за хвилину; розриви - NeoBar не працював.", pts[0].T.ToString("dd.MM HH:mm"));
        TxtL(g, L.T("Температури й навантаження за добу"), FB, new Rectangle(r.X, r.Y, r.Width, Px(24)), P.Fg);
        TxtL(g, sub, FS, new Rectangle(r.X, r.Y + Px(24), r.Width, Px(20)), P.Sub);

        // події: сповіщення NeoBar і журнал подій Windows
        List<HistEvent> ev = Watch.Events();
        SysEvents.Sum se = SysEvents.Cached;
        if (se == null) SysEvents.Refresh(delegate { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(Invalidate)); } catch (Exception skipEx) { SbLog.Skip(skipEx); } });
        else
        {
            if (se.Gpu > 0 && se.GpuAt > t0) ev.Add(new HistEvent { T = se.GpuAt, Text = L.T("Windows: відеодрайвер завис і перезапустився") });
            if (se.Mem > 0 && se.MemAt > t0) ev.Add(new HistEvent { T = se.MemAt, Text = L.T("Windows: бракувало пам'яті") });
            if (se.Power > 0 && se.PowerAt > t0) ev.Add(new HistEvent { T = se.PowerAt, Text = L.T("Windows: раптове вимкнення або перезавантаження") });
        }

        int top = r.Y + Px(52), gap = Px(12);
        int h = (r.Height - (top - r.Y) - gap - Px(28)) / 2;
        HsLine[] temps =
        {
            new HsLine { Name = L.T("Процесор"), C = P.Warn, V = delegate (HistPoint p) { return p.CpuT; }, Unit = " °C" },
            new HsLine { Name = L.T("Відеокарта"), C = P.Acc, V = delegate (HistPoint p) { return p.GpuT; }, Unit = " °C" },
        };
        HsLine[] loads =
        {
            new HsLine { Name = L.T("Процесор"), C = P.Warn, V = delegate (HistPoint p) { return p.Cpu; }, Unit = " %" },
            new HsLine { Name = L.T("Відеокарта"), C = P.Acc, V = delegate (HistPoint p) { return p.Gpu; }, Unit = " %" },
            new HsLine { Name = L.T("Пам'ять"), C = P.Good, V = delegate (HistPoint p) { return p.Ram; }, Unit = " %" },
            new HsLine { Name = L.T("Відеопам'ять"), C = Util.Blend(P.Fg, P.Acc, 0.4f), V = delegate (HistPoint p) { return p.Vram; }, Unit = " %" },
        };
        HsChart(g, new Rectangle(r.X, top, r.Width, h), L.T("Температури"), temps, pts, ev, t0, t1, false);
        HsChart(g, new Rectangle(r.X, top + h + gap, r.Width, h), L.T("Навантаження"), loads, pts, ev, t0, t1, true);
    }

    void HsChart(Graphics g, Rectangle box, string title, HsLine[] lines, List<HistPoint> pts, List<HistEvent> ev, DateTime t0, DateTime t1, bool pct)
    {
        Card(g, box, P.Card);
        TxtL(g, title, FB, new Rectangle(box.X + Px(12), box.Y + Px(6), Px(200), Px(22)), P.Fg);
        // легенда
        int lx = box.Right - Px(12);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            int tw = TextRenderer.MeasureText(lines[i].Name, FS).Width;
            lx -= tw + Px(4);
            TxtL(g, lines[i].Name, FS, new Rectangle(lx, box.Y + Px(6), tw + Px(4), Px(22)), P.Sub);
            lx -= Px(14);
            using (SolidBrush b = new SolidBrush(lines[i].C)) g.FillRectangle(b, lx, box.Y + Px(15), Px(10), Px(3));
            lx -= Px(10);
        }
        Rectangle pl = new Rectangle(box.X + Px(52), box.Y + Px(34), box.Width - Px(64), box.Height - Px(58));
        hsPlots.Add(pl);
        double span = (t1 - t0).TotalMinutes;
        // шкала
        double lo = 0, hi = pct ? 100 : 0;
        if (!pct)
        {
            lo = double.MaxValue;
            foreach (HistPoint p in pts) foreach (HsLine ln in lines) { float v = ln.V(p); if (!float.IsNaN(v)) { hi = Math.Max(hi, v); lo = Math.Min(lo, v); } }
            if (lo == double.MaxValue) { lo = 30; hi = 90; }
            lo = Math.Max(0, Math.Floor((lo - 5) / 10) * 10);
            hi = Math.Max(lo + 20, Math.Ceiling((hi + 5) / 10) * 10);
        }
        using (Pen gl = new Pen(Util.Blend(P.Card, P.Fg, 0.08f), 1f))
            for (int i = 0; i <= 4; i++)
            {
                int y = pl.Bottom - pl.Height * i / 4;
                g.DrawLine(gl, pl.X, y, pl.Right, y);
                double v = lo + (hi - lo) * i / 4;
                TxtR(g, v.ToString("0") + (pct ? " %" : "°"), FS, new Rectangle(box.X, y - Px(9), Px(46), Px(18)), P.Sub);
            }
        // години
        DateTime hh = new DateTime(t0.Year, t0.Month, t0.Day, t0.Hour, 0, 0).AddHours(1);
        for (; hh < t1; hh = hh.AddHours(1))
        {
            if (hh.Hour % 3 != 0) continue;
            float x = pl.X + (float)((hh - t0).TotalMinutes / span * pl.Width);
            using (Pen gl = new Pen(Util.Blend(P.Card, P.Fg, 0.06f), 1f)) g.DrawLine(gl, x, pl.Y, x, pl.Bottom);
            TxtC(g, hh.ToString("HH:mm"), FS, new Rectangle((int)x - Px(30), pl.Bottom + Px(2), Px(60), Px(18)), P.Sub);
        }
        // лінії з розривами, де даних немає довше 3 хв
        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (HsLine ln in lines)
            using (Pen pen = new Pen(ln.C, Math.Max(1.5f, Px(1.6f))))
            {
                List<PointF> seg = new List<PointF>();
                DateTime prev = DateTime.MinValue;
                foreach (HistPoint p in pts)
                {
                    float v = ln.V(p);
                    if (float.IsNaN(v) || (prev != DateTime.MinValue && (p.T - prev).TotalMinutes > 3)) { if (seg.Count > 1) g.DrawLines(pen, seg.ToArray()); seg.Clear(); }
                    prev = p.T;
                    if (float.IsNaN(v)) continue;
                    seg.Add(new PointF(pl.X + (float)((p.T - t0).TotalMinutes / span * pl.Width), (float)(pl.Bottom - pl.Height * (Math.Max(lo, Math.Min(hi, v)) - lo) / (hi - lo))));
                }
                if (seg.Count > 1) g.DrawLines(pen, seg.ToArray());
            }
        // події
        foreach (HistEvent e in ev)
        {
            if (e.T < t0) continue;
            float x = pl.X + (float)((e.T - t0).TotalMinutes / span * pl.Width);
            bool win = e.Text.StartsWith(L.T("Windows:"));
            Color c = win ? P.Warn : P.Bad;
            using (Pen p = new Pen(Color.FromArgb(180, c), 1f)) { p.DashStyle = DashStyle.Dash; g.DrawLine(p, x, pl.Y, x, pl.Bottom); }
            PointF[] tri = { new PointF(x - Px(5), pl.Y), new PointF(x + Px(5), pl.Y), new PointF(x, pl.Y + Px(7)) };
            using (SolidBrush b = new SolidBrush(c)) g.FillPolygon(b, tri);
            Tip(new Rectangle((int)x - Px(7), pl.Y - Px(2), Px(14), Px(14)), e.T.ToString("HH:mm") + "  " + e.Text);
        }
        // під курсором
        if (pl.Contains(anMouse) && pts.Count > 0)
        {
            DateTime at = t0.AddMinutes((anMouse.X - pl.X) / (double)pl.Width * span);
            HistPoint best = null;
            foreach (HistPoint p in pts) if (best == null || Math.Abs((p.T - at).TotalMinutes) < Math.Abs((best.T - at).TotalMinutes)) best = p;
            if (best != null && Math.Abs((best.T - at).TotalMinutes) <= 3)
            {
                float x = pl.X + (float)((best.T - t0).TotalMinutes / span * pl.Width);
                using (Pen dp = new Pen(Util.Blend(P.Card, P.Fg, 0.35f), 1f)) { dp.DashStyle = DashStyle.Dot; g.DrawLine(dp, x, pl.Y, x, pl.Bottom); }
                List<string> parts = new List<string>();
                foreach (HsLine ln in lines) { float v = ln.V(best); if (!float.IsNaN(v)) parts.Add(ln.Name + " " + v.ToString("0") + ln.Unit); }
                Label(g, best.T.ToString("HH:mm") + "   " + string.Join("  ·  ", parts.ToArray()), box, x, pl.Y + Px(10), false, Util.Blend(P.Card, P.Fg, 0.3f));
            }
        }
        g.SmoothingMode = SmoothingMode.None;
    }
}
