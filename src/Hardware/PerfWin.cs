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

// ======================================================================
//  Монітор системи (у стилі Afterburner); оверлей у іграх - окремий модуль (Overlay.cs)
// ======================================================================
// ----------------------------------------------------------------------
//  Вікно монітора
// ----------------------------------------------------------------------
sealed partial class PerfWindow : SbWindow
{
    int scroll;
    // показники, позначені повзунком «На столі»: ними заповнюється віджет, коли натиснути кнопку з дисплеєм
    static readonly HashSet<string> deskSel = new HashSet<string>(new string[] { "cpu" });     // хоч один показник завжди вибраний, за замовчуванням ЦП
    string copiedId = "";
    DateTime copiedAt = DateTime.MinValue;

    void CopyText(string text, string id)
    {
        try { Clipboard.SetText(text); } catch { return; }
        copiedId = id;
        copiedAt = DateTime.Now;
        Invalidate();
    }

    public PerfWindow(NeoBar bar, int tab) : base(bar, L.T("Монітор системи"), 560, 740)
    { Glyph = "\uE9D9";
        Tabs = new string[] { L.T("Продуктивність"), L.T("Характеристики"), L.T("Процеси") };
        Tab = Math.Max(0, Math.Min(2, tab));
        Sensors.Use("perfwin", 2);
        Sensors.Updated += OnData;
        OnTab();
    }

    void OnData() { if (!IsDisposed) Invalidate(); }

    public void SetTab(int t) { Tab = Math.Max(0, Math.Min(2, t)); OnTab(); Invalidate(); }

    protected override void HeadButtons(Graphics g, ref int right)
    {
        if (Tab != 0) return;
        Rectangle gr = new Rectangle(right - Px(32), Px(7), Px(32), Px(32));
        IconBtn(g, gr, "desk", delegate
        {
            List<string> l = new List<string>();
            foreach (string k in new[] { "cpu", "gpu", "ram" }) if (deskSel.Contains(k)) l.Add(k);
            ClockData.AddDesk("perf", l.Count == 0 || l.Count == 3 ? "" : string.Join(",", l.ToArray()));     // нічого не позначено - усі три
        }, false);
        right = gr.Left - Px(4);
    }

    protected override void OnTab()
    {
        scroll = 0;
        ResizeFor(Tab == 2 ? 880 : 560);
        if (Tab == 1) HwInfo.Load(false);
        if (Tab == 2) EnterProcs();
    }

    protected override void OnTick() { if (Tab == 2) TickProcs(); else if (Tab == 1 && (HwInfo.Loading || (DateTime.Now - copiedAt).TotalMilliseconds < 1800)) Invalidate(); }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Sensors.Updated -= OnData;
        Sensors.Use("perfwin", 0);
        ProcMon.Reset();
        base.OnFormClosed(e);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        if (Tab == 2) PaintProcs(g, r); else if (Tab == 1) PaintSpecs(g, r); else PaintMonitor(g, r);
    }

    // ---------- характеристики ----------
    void PaintSpecs(Graphics g, Rectangle r)
    {
        int barH = Px(40);
        Rectangle area = new Rectangle(r.X, r.Y, r.Width, r.Height - barH);
        if (!HwInfo.Done)
        {
            TxtC(g, L.T("Збираю дані про комплектуючі…"), F, area, P.Sub);
        }
        else
        {
            List<InfoGroup> gr = HwInfo.Groups;
            int rowH = Px(22), pad = Px(12), head = Px(30), gap = Px(8);
            int total = 0;
            foreach (InfoGroup ig in gr) total += head + ig.Rows.Count * rowH + pad + gap;
            scroll = Math.Max(0, Math.Min(scroll, Math.Max(0, total - area.Height)));
            Region old = g.Clip;
            g.SetClip(area);
            int y = area.Y - scroll;
            int lw = Px(190);
            int gi = -1;
            foreach (InfoGroup ig in gr)
            {
                gi++;
                int h = head + ig.Rows.Count * rowH + pad;
                if (y + h > area.Y && y < area.Bottom)
                {
                    Rectangle cr = new Rectangle(area.X, y, area.Width, h);
                    Card(g, cr);
                    TxtL(g, ig.Title, FB, new Rectangle(cr.X + pad, cr.Y + Px(4), cr.Width - pad * 2 - Px(110), head - Px(6)), P.Acc);
                    // скопіювати весь блок
                    Rectangle cb = new Rectangle(cr.Right - pad - Px(104), cr.Y + Px(4), Px(104), head - Px(8));
                    bool okG = copiedId == "g" + gi && (DateTime.Now - copiedAt).TotalMilliseconds < 1600;
                    Rectangle cbv = Rectangle.Intersect(cb, area);
                    if (!cbv.IsEmpty)
                    {
                        bool hv = Over(cbv);
                        if (hv || okG) using (SolidBrush hb = new SolidBrush(Util.Blend(P.Card, P.Fg, 0.1f))) Util.FillRound(g, hb, cb, Math.Min(P.Rad, cb.Height / 2));
                        TxtC(g, okG ? L.T("Скопійовано") : L.T("Копіювати"), FS, cb, okG ? P.Good : (hv ? P.Fg : P.Sub));
                        string gid = "g" + gi; InfoGroup gg = ig;
                        Hot(cbv, delegate { CopyText(HwInfo.GroupText(gg), gid); });
                    }
                    int ry = cr.Y + head;
                    int ri = 0;
                    foreach (string[] row in ig.Rows)
                    {
                        if (row[0].Length == 0)
                            TxtL(g, row[1], FB, new Rectangle(cr.X + pad, ry, cr.Width - pad * 2, rowH), P.Fg);
                        else
                        {
                            Rectangle rr = new Rectangle(cr.X + Px(4), ry, cr.Width - Px(8), rowH);
                            Rectangle rv = Rectangle.Intersect(rr, area);
                            string rid = "r" + gi + "_" + ri;
                            bool okR = copiedId == rid && (DateTime.Now - copiedAt).TotalMilliseconds < 1600;
                            if (!rv.IsEmpty && (Over(rv) || okR))
                                using (SolidBrush hb = new SolidBrush(Util.Blend(P.Card, P.Fg, 0.08f))) Util.FillRound(g, hb, rr, Px(4));
                            TxtL(g, row[0], FS, new Rectangle(cr.X + pad + Px(8), ry, lw - Px(8), rowH), P.Sub);
                            TxtL(g, row[1], F, new Rectangle(cr.X + pad + lw, ry, cr.Width - pad * 2 - lw - (okR ? Px(96) : 0), rowH), okR ? P.Good : P.Fg);
                            if (okR) TxtR(g, L.T("Скопійовано"), FS, new Rectangle(cr.X + pad, ry, cr.Width - pad * 2, rowH), P.Good);
                            if (!rv.IsEmpty)
                            {
                                string val = row[1];
                                Hot(rv, delegate { CopyText(val, rid); });
                            }
                        }
                        ry += rowH;
                        ri++;
                    }
                }
                y += h + gap;
            }
            g.Clip = old;
            Wheel(area, delegate (int d) { scroll -= d * Px(60); });
        }
        Btn(g, new Rectangle(r.X, r.Bottom - Px(34), Px(180), Px(32)), L.T("Копіювати все"), delegate
        {
            try { Clipboard.SetText(HwInfo.AsText()); } catch { }
        }, 1, HwInfo.Done);
        Btn(g, new Rectangle(r.X + Px(190), r.Bottom - Px(34), Px(130), Px(32)), L.T("Оновити"), delegate { scroll = 0; HwInfo.Load(true); }, 0, !HwInfo.Loading);
        TxtR(g, L.T("Дані зчитуються з Windows (WMI)"), FS, new Rectangle(r.X + Px(330), r.Bottom - Px(34), r.Width - Px(330), Px(32)), P.Sub);
    }

    static List<float> H(string k)
    {
        List<float> l;
        return Sensors.Hist.TryGetValue(k, out l) ? l : null;
    }

    void Row(Graphics g, Rectangle r, string title, string sub, string[] vals, string hist, float max, Color c, string key)
    {
        Card(g, r);
        Rectangle ri = Rectangle.Inflate(r, -Px(12), -Px(8));
        int lw = Px(170);
        TxtL(g, title, FB, new Rectangle(ri.X, ri.Y, lw, Px(20)), P.Fg);
        TxtL(g, sub, FS, new Rectangle(ri.X, ri.Y + Px(20), lw, Px(18)), P.Sub);
        for (int i = 0; i < vals.Length; i++)
            TxtL(g, vals[i], i == 0 ? FBig : F, new Rectangle(ri.X, ri.Y + Px(40) + (i == 0 ? 0 : Px(14) + i * Px(18)), lw, i == 0 ? Px(34) : Px(18)), i == 0 ? c : P.Fg);
        Graph(g, new Rectangle(ri.X + lw, ri.Y + Px(2), ri.Width - lw, ri.Height - Px(4)), H(hist), max, c);
        // показувати цей рядок у віджетах на панелі та на столі
        bool on = key == "cpu" ? Widgets.PerfCpu : (key == "gpu" ? Widgets.PerfGpu : Widgets.PerfRam);
        string k2 = key;
        Switch(g, new Rectangle(ri.X, ri.Bottom - Px(22), lw - Px(10), Px(22)), L.T("На панелі"), on, delegate { Bar.SetPerfShow(k2, !on); });
        bool onDesk = deskSel.Contains(key);
        Switch(g, new Rectangle(ri.X, ri.Bottom - Px(50), lw - Px(10), Px(22)), L.T("На столі"), onDesk, delegate { if (!onDesk) deskSel.Add(k2); else if (deskSel.Count > 1) deskSel.Remove(k2); Invalidate(); });
    }

    void PaintMonitor(Graphics g, Rectangle r)
    {
        CultureInfo ic = CultureInfo.InvariantCulture;
        int gap = Px(8);
        int rh = (r.Height - gap * 2) / 3;
        Color cc = Color.FromArgb(70, 170, 255), gc = Color.FromArgb(90, 200, 110), rc = Color.FromArgb(200, 120, 240);
        if (Util.Lum(P.Bg) > 140) { cc = Color.FromArgb(20, 110, 210); gc = Color.FromArgb(30, 140, 60); rc = Color.FromArgb(140, 60, 190); }

        string cpuSub = Sensors.CpuName.Length > 0 ? Sensors.CpuName : L.T("Процесор");
        string mhz = float.IsNaN(Sensors.CpuMhz) ? "" : (Sensors.CpuMhz / 1000f).ToString("0.00", ic) + L.T(" ГГц");
        string ct = float.IsNaN(Sensors.CpuTemp) ? L.T("темп. —") + (float.IsNaN(Sensors.CpuPowerW) ? "" : " · " + Math.Round(Sensors.CpuPowerW) + L.T(" Вт")) : Math.Round(Sensors.CpuTemp) + " °C" + (Sensors.TempIsAcpi ? " (ACPI)" : "") +
                    (float.IsNaN(Sensors.CpuPowerW) ? "" : " · " + Math.Round(Sensors.CpuPowerW) + L.T(" Вт"));
        Rectangle cpuR = new Rectangle(r.X, r.Y, r.Width, rh);
        Row(g, cpuR, L.T("ЦП"), cpuSub, new[] { Sensors.F0(Sensors.Cpu, "%"), mhz + (Sensors.Cores > 0 ? " · " + Sensors.Cores + L.T(" потоків") : ""), ct }, "cpu", 100, cc, "cpu");

        string gs = Sensors.GpuName.Length > 0 ? Sensors.GpuName : L.T("Відеоадаптер");
        string gt = (float.IsNaN(Sensors.GpuTemp) ? L.T("темп. —") : Math.Round(Sensors.GpuTemp) + " °C") +
                    (float.IsNaN(Sensors.Hotspot) ? "" : " (hotspot " + Math.Round(Sensors.Hotspot) + ")") +
                    (!float.IsNaN(Sensors.GpuPowerW) ? " · " + Math.Round(Sensors.GpuPowerW) + L.T(" Вт") : (float.IsNaN(Sensors.GpuPower) ? "" : " · " + Math.Round(Sensors.GpuPower) + "% TDP")) +
                    (float.IsNaN(Sensors.GpuClock) ? "" : " · " + Math.Round(Sensors.GpuClock) + L.T(" МГц"));
        string vr = (Sensors.VramTotalGb > 0 ? "VRAM " + Sensors.VramUsedGb.ToString("0.0", ic) + L.T(" з ") + Sensors.VramTotalGb.ToString("0", ic) + L.T(" ГБ") : "") +
                    (Sensors.GpuFan > 0 ? L.T(" · вент. ") + Sensors.GpuFan + L.T(" об/хв") : (float.IsNaN(Sensors.GpuFanPct) ? "" : L.T(" · вент. ") + Math.Round(Sensors.GpuFanPct) + "%"));
        if (Sensors.VendorSource.Length > 0) gs += " · " + Sensors.VendorSource;
        Row(g, new Rectangle(r.X, r.Y + (rh + gap), r.Width, rh), L.T("ГП"), gs, new[] { Sensors.F0(Sensors.GpuLoad, "%"), gt, vr }, "gpu", 100, gc, "gpu");

        string ru = Sensors.RamTotalGb > 0 ? Sensors.RamUsedGb.ToString("0.0", ic) + L.T(" з ") + Sensors.RamTotalGb.ToString("0.0", ic) + L.T(" ГБ") : "";
        Row(g, new Rectangle(r.X, r.Y + (rh + gap) * 2, r.Width, rh), L.T("Оперативна пам'ять"), L.T("ОЗП"), new[] { Sensors.F0(Sensors.Ram, "%"), ru }, "ram", 100, rc, "ram");
    }
}
