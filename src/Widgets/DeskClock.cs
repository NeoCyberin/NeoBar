using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Віджети на робочому столі: годинник, календар, погода, мережа, монітор, диски

// ----------------------------------------------------------------------
//  Годинник / таймер / секундомір на робочому столі
// ----------------------------------------------------------------------
sealed class DeskClock : LayeredForm
{
    readonly DeskSpec spec;
    bool dragging;
    Point dragFrom;

    System.Windows.Forms.Timer fxTimer;

    public DeskClock(DeskSpec s)
    {
        spec = s;
        Text = "NeoBar - " + ClockData.KindTitle(s.Kind);
        if (s.Kind == "trash") { cleanHook = delegate { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(TrashChanged)); } catch (Exception skipEx) { SbLog.Skip(skipEx); } }; Cleaner.Changed += cleanHook; }
        if (s.Kind == "weather")
        {
            // анімація погоди на віджеті: ~15 кадрів/с, лише коли є що показувати
            fxTimer = new System.Windows.Forms.Timer();
            fxTimer.Interval = 66;
            fxTimer.Tick += delegate { if (Visible && ClockData.Bar != null && WeatherFx.Active(ClockData.Bar.Config)) Redraw(); };
            fxTimer.Start();
        }
    }

    Action cleanHook;
    bool trashArm, trashRun;        // trashRun: очищення запущене саме цим віджетом
    Point trashAt;

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (cleanHook != null) { Cleaner.Changed -= cleanHook; cleanHook = null; }
        if (fxTimer != null) { fxTimer.Stop(); fxTimer.Dispose(); fxTimer = null; }
        base.OnHandleDestroyed(e);
    }

    float Sc { get { return ClockData.Bar != null ? ClockData.Bar.Dpi : 1f; } }
    int Px(float v) { return (int)Math.Round(v * Sc); }

    float K { get { return Math.Max(MinK, Math.Min(MaxK, spec.Scale > 0 ? spec.Scale : (spec.Size == 0 ? 0.75f : (spec.Size == 2 ? 1.35f : 1f)))); } }

    // межі масштабу: менше - дрібні підписи вже не прочитати (у брандмауера й кошика вони найдрібніші), більше - віджет
    // закриває пів робочого столу
    const float MaxK = 2.5f;
    float MinK { get { return spec.Kind == "trash" || spec.Kind == "fw" ? 1f : (spec.Kind == "calendar" || spec.Kind == "clockcal" ? 0.95f : 0.85f); } }

    // великі написи (час, температура, підсумок): шрифтом нотаток - тим, що в теми (за налаштуванням), і так,
    // щоб напис вмістився по ширині (власні шрифти тем бувають ширшими за системний)
    Font Big(Graphics g, Pal p, string text, float pt, FontStyle st, float maxW)
    {
        NeoBar b = ClockData.Bar;
        string face = p.Face;
        if (b != null && b.Config.DeskNoteFont) face = SBFonts.ForTheme(b.CurrentTheme.Id) + "|" + p.Face;
        Font f = Util.MakeFont(face, pt, st);
        try
        {
            float w = g.MeasureString(text ?? "", f, PointF.Empty, StringFormat.GenericTypographic).Width;
            if (maxW > 0 && w > maxW * 0.96f)
            {
                float np = Math.Max(pt * 0.55f, pt * maxW * 0.94f / w);
                f.Dispose();
                f = Util.MakeFont(face, np, st);
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return f;
    }

    Size Want() { return Want(K); }

    Size Want(float k)
    {
        if (spec.Kind == "analog") return new Size(Px(170 * k), Px(170 * k));
        if (spec.Kind == "calendar") return new Size(Px(240 * k), Px(220 * k));
        if (spec.Kind == "clockcal") return new Size(Px(250 * k), Px(300 * k));
        if (spec.Kind == "weather") return new Size(Px(270 * k), Px(124 * k));
        if (spec.Kind == "perf") { bool[] ps = PerfOf(spec); return new Size(Px(250 * k), Px((34 + 38 * ((ps[0] ? 1 : 0) + (ps[1] ? 1 : 0) + (ps[2] ? 1 : 0))) * k)); }
        if (spec.Kind == "disk") return new Size(Px(260 * k), Px((40 + Math.Max(1, Math.Min(8, ShownDisks().Count)) * 38) * k));
        if (spec.Kind == "trash" || spec.Kind == "fw") return new Size(Px(100 * k), Px(100 * k));
        return new Size(Px(250 * k), Px(110 * k));
    }

    // мініатюра для вибору у вікні годинника (без показу вікна)
    public static Bitmap Preview(string kind)
    {
        DeskSpec s = new DeskSpec();
        s.Kind = kind;
        s.Opacity = 100;
        return Preview(s);
    }

    public static Bitmap Preview(DeskSpec s)
    {
        using (DeskClock dc = new DeskClock(s))
        {
            Size sz = dc.Want();
            Bitmap b = new Bitmap(sz.Width, sz.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b)) dc.Render(g, sz);
            return b;
        }
    }

    // ---------- шапка керування (як у нотатки): налаштування, непрозорість, замок, видалення; куточок розміру ----------
    int HeadH { get { return Px(32); } }
    int T0 { get { return spec.Locked ? 0 : HeadH; } }       // зсув вмісту вниз; замкнений віджет без шапки
    Rectangle rSlider, rLock, rClose, rGrip, rUnlock;
    int hover;                    // 1 повзунок, 2 замок, 3 видалення, 4 куточок, 5 відімкнути
    bool sliding, resizing, hot, framed;
    int dx0;                      // вузький віджет у ширшій рамці: зсув вмісту праворуч

    // шапка не вужча за це, щоб уміщалися кнопки й повзунок
    int FrameW(Size cs) { return spec.Locked ? cs.Width : Math.Max(cs.Width, Px(spec.Kind == "trash" || spec.Kind == "fw" ? 150 : 200)); }

    // повзунок: межі доріжки (у вузькій шапці - без підпису відсотків)
    void Track(out int l, out int rr)
    {
        bool lab = rSlider.Width >= Px(110);
        l = rSlider.X + Px(6);
        rr = Math.Max(l + 1, rSlider.Right - (lab ? Px(38) : Px(8)));
    }

    public void Place()
    {
        Size sz = Want();
        Rectangle r = new Rectangle(spec.X, spec.Y, sz.Width, sz.Height);
        bool vis = false;
        foreach (Screen s in Screen.AllScreens) if (s.WorkingArea.IntersectsWith(r)) vis = true;
        if (!vis) { Rectangle wa = Screen.PrimaryScreen.WorkingArea; spec.X = wa.Left + 100; spec.Y = wa.Top + 100; }
        int fw0 = FrameW(sz);
        dx0 = (fw0 - sz.Width) / 2;
        Bounds = new Rectangle(spec.X - dx0, spec.Y - T0, fw0, sz.Height + T0);
        Show();
        Redraw();
        KeepBottom();
    }

    static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

    void KeepBottom()
    {
        try { Win.SetWindowPos(Handle, HWND_BOTTOM, 0, 0, 0, 0, 0x1 | 0x2 | 0x10); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    protected override void WndProc(ref Message m)
    {
        // завжди під іншими вікнами (як нотатка на робочому столі)
        if (m.Msg == 0x46 && !dragging)   // WM_WINDOWPOSCHANGING
        {
            try
            {
                int flagsOff = IntPtr.Size * 2 + 16;
                Marshal.WriteIntPtr(m.LParam, IntPtr.Size, HWND_BOTTOM);
                int fl = Marshal.ReadInt32(m.LParam, flagsOff);
                Marshal.WriteInt32(m.LParam, flagsOff, fl & ~0x4);   // без SWP_NOZORDER
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
        base.WndProc(ref m);
    }

    // Щосекундне оновлення. Погода й календар міняються рідко: їх перемальовуємо лише коли змінився вміст
    // (або раз на хвилину - на випадок того, що підпис не врахував), а не щосекунди, як годинник.
    // Решта перемальовувань (тема, наведення, анімації) іде напряму через Redraw.
    string tickSig;

    public void TickRedraw()
    {
        string s = TickSig();
        if (s != null && s == tickSig) return;
        tickSig = s;
        Redraw();
    }

    string TickSig()
    {
        DateTime now = DateTime.Now;
        switch (spec.Kind)
        {
            case "weather": return Widgets.Sig("weather", ClockData.Bar != null ? ClockData.Bar.Config : null) + "|" + spec.Opt + "|" + now.ToString("HHmm");
            case "calendar": return now.ToString("yyyyMMddHHmm") + "|" + Holidays.Ver;
            case "fw": return "fw|" + Fw.Overall(false) + "|" + fwBusy + "|" + (fwMsg.Length > 0 && (now - fwMsgAt).TotalSeconds < 8);
            case "trash":
                TrashPoll();
                return Cleaner.Busy + "|" + Cleaner.Stage + "|" + Cleaner.LastFound + "|" + Cleaner.LastFreed + "|" + ((now - Cleaner.LastCleanAt).TotalSeconds < 90);
            default: return null;           // годинники, секундомір, мережа, монітор, диски - щосекунди, як і раніше
        }
    }

    public void Redraw()
    {
        if (IsDisposed || ClockData.Bar == null) return;
        Size cs = Want();
        int t0 = T0;
        int fw = FrameW(cs);
        Size full = new Size(fw, cs.Height + t0);
        if (!dragging) dx0 = (fw - cs.Width) / 2;
        if (!dragging)
        {
            Rectangle wb = new Rectangle(spec.X - dx0, spec.Y - t0, full.Width, full.Height);
            if (Bounds != wb) Bounds = wb;
        }
        else if (Size != full) Size = full;
        // малюємо прямо в поверхню вікна (без проміжного бітмапа на кожне оновлення)
        Graphics fg = BeginFrame(full.Width, full.Height);
        if (fg != null)
        {
            using (fg) Compose(fg, cs, full, t0);
            PushFrame(Location, full);
        }
        else
            using (Bitmap bm = new Bitmap(full.Width, full.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bm)) Compose(g, cs, full, t0);
                Push(bm, Location);
            }
    }

    void Compose(Graphics g, Size cs, Size full, int t0)
    {
        Pal p = Pal.Of(ClockData.Bar.CurrentTheme, ClockData.Bar.LabelFont, Sc);
        framed = !spec.Locked;
        rSlider = rLock = rClose = rGrip = rUnlock = Rectangle.Empty;
        if (spec.Locked)
        {
            if (hot) rUnlock = new Rectangle(full.Width - Px(32), Px(5), Px(26), Px(26));
            else hover = 0;
        }
        else
        {
            int hh = HeadH;
            rClose = new Rectangle(full.Width - Px(36), 0, Px(34), hh);
            rLock = new Rectangle(rClose.X - Px(36), 0, Px(36), hh);
            int sw = Math.Max(Px(60), Math.Min(Px(170), rLock.X - Px(10)));
            rSlider = new Rectangle(rLock.X - sw, 0, sw, hh);
            rGrip = new Rectangle(full.Width - Px(20), full.Height - Px(20), Px(20), Px(20));
        }
        if (spec.Locked)
        {
            // майже невидима, але «тверда» зона навколо замочка: кругла рамка годинника має прозорі кути,
            // і без неї курсор, що їде від циферблата до замочка, виходить із вікна й замочок зникає
            using (SolidBrush zb = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
                g.FillRectangle(zb, full.Width - Px(46), 0, Px(46), Px(42));
        }
        if (framed)
        {
            // фон тіла рамки (вміст сам фон не малює)
            int rad = p.Rad + Px(4);
            int a = (int)Math.Round(255 * spec.Opacity / 100.0);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath bp = Util.Round(new Rectangle(0, t0, full.Width - 1, cs.Height - 1), 0, 0, rad, rad))
            using (Brush bb = Festive.BgBrush(p.T, p.Bg, Math.Max(1, a), new RectangleF(0, 0, full.Width, full.Height))) g.FillPath(bb, bp);
        }
        using (Bitmap cb = new Bitmap(cs.Width, cs.Height, PixelFormat.Format32bppArgb))
        {
            using (Graphics cg = Graphics.FromImage(cb)) Render(cg, cs);
            float wf = spec.Kind == "weather" ? Widgets.WeatherFade : (spec.Kind == "net" ? PingMon.Fade.Level : 1f);
            if (wf < 0.999f)
            {
                // оновлення погоди: вміст плавно згасає й з'являється
                using (ImageAttributes ia = new ImageAttributes())
                {
                    ColorMatrix cmx = new ColorMatrix();
                    cmx.Matrix33 = wf;
                    ia.SetColorMatrix(cmx);
                    g.DrawImage(cb, new Rectangle(dx0, t0, cb.Width, cb.Height), 0, 0, cb.Width, cb.Height, GraphicsUnit.Pixel, ia);
                }
            }
            else g.DrawImage(cb, dx0, t0);
        }
        if (!spec.Locked) DrawControls(g, p, full);
        else if (hot) DrawUnlock(g, p);
    }

    void DrawControls(Graphics g, Pal p, Size full)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        int rad = p.Rad + Px(4), hh = HeadH;
        Color head = p.Acc, ink = Notes.Contrast(head);
        using (GraphicsPath hp = Util.Round(new Rectangle(0, 0, full.Width - 1, hh), rad, rad, 0, 0))
        using (SolidBrush hb = new SolidBrush(head)) g.FillPath(hb, hp);
        using (GraphicsPath fp = Util.Round(new Rectangle(0, 0, full.Width - 1, full.Height - 1), rad))
        using (Pen fpn = new Pen(head, Math.Max(1.5f, Px(1.5f)))) g.DrawPath(fpn, fp);
        // підсвітка кнопки під мишею
        Rectangle hr = hover == 2 ? rLock : (hover == 3 ? rClose : Rectangle.Empty);
        if (!hr.IsEmpty)
            using (GraphicsPath bp = Util.Round(Rectangle.Inflate(hr, -Px(3), -Px(4)), Px(5)))
            using (SolidBrush bb = new SolidBrush(Color.FromArgb(50, ink))) g.FillPath(bb, bp);
        int isz = Px(16);
        Func<Rectangle, RectangleF> ic = delegate (Rectangle rr) { return new RectangleF(rr.X + (rr.Width - isz) / 2f, rr.Y + (rr.Height - isz) / 2f, isz, isz); };
        Ico.DrawLock(g, ic(rLock), ink, lkBusy ? lkT : 0f);
        Ico.Draw(g, "close", ic(rClose), ink);
        // повзунок непрозорості фону: 10..100 %
        int l, rr2;
        Track(out l, out rr2);
        float cy = hh / 2f, th = Math.Max(2f, Px(3));
        int v = Math.Max(10, Math.Min(100, spec.Opacity));
        float kx = l + (rr2 - l) * (v - 10) / 90f;
        using (SolidBrush tr = new SolidBrush(Color.FromArgb(70, ink))) g.FillRectangle(tr, l, cy - th / 2, rr2 - l, th);
        using (SolidBrush fl = new SolidBrush(Color.FromArgb(220, ink))) g.FillRectangle(fl, l, cy - th / 2, kx - l, th);
        float kr = Math.Max(4f, Px(hover == 1 ? 6.5f : 5.5f));
        using (SolidBrush kb = new SolidBrush(ink)) g.FillEllipse(kb, kx - kr, cy - kr, kr * 2, kr * 2);
        using (Font f = p.Font(8f * Sc, FontStyle.Regular))
        using (StringFormat sf = new StringFormat())
        using (SolidBrush tb = new SolidBrush(ink))
        {
            sf.Alignment = StringAlignment.Far; sf.LineAlignment = StringAlignment.Center;
            if (rSlider.Width >= Px(110)) g.DrawString(v + "%", f, tb, new RectangleF(rr2 + Px(2), 0, rSlider.Right - rr2 - Px(2), hh), sf);
        }
        // куточок зміни розміру
        using (Pen gp2 = new Pen(Color.FromArgb(hover == 4 ? 255 : 160, hover == 4 ? p.Acc : p.Fg), Math.Max(1.2f, Px(1.4f))))
        {
            gp2.StartCap = LineCap.Round; gp2.EndCap = LineCap.Round;
            int gx = rGrip.Right - Px(5), gy = rGrip.Bottom - Px(5);
            for (int i = 1; i <= 3; i++) g.DrawLine(gp2, gx - Px(3.5f) * i, gy, gx, gy - Px(3.5f) * i);
        }
    }

    // замкнений віджет: під мишею з'являється замочок, клік - відімкнути
    void DrawUnlock(Graphics g, Pal p)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color c = hover == 5 ? p.Acc : Util.Blend(p.Bg, p.Fg, 0.25f);
        using (SolidBrush b = new SolidBrush(Color.FromArgb(230, c))) g.FillEllipse(b, rUnlock);
        int isz = Px(14);
        Ico.DrawLock(g, new RectangleF(rUnlock.X + (rUnlock.Width - isz) / 2f, rUnlock.Y + (rUnlock.Height - isz) / 2f, isz, isz), hover == 5 ? Notes.Contrast(p.Acc) : p.Fg, lkBusy ? lkT : 1f);
    }

    void Render(Graphics g, Size sz)
    {
        NeoBar bar = ClockData.Bar;
        Theme t = bar.CurrentTheme;
        Pal p = Pal.Of(t, bar.LabelFont, Sc);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        int a = (int)Math.Round(255 * spec.Opacity / 100.0);
        Rectangle r = new Rectangle(0, 0, sz.Width - 1, sz.Height - 1);
        bool analog = spec.Kind == "analog";
        int rad0 = p.Rad + Px(4);
        // розблокований віджет - у рамці з шапкою (як нотатка): вміст без верхніх заокруглень, рамку малює шапка
        using (GraphicsPath gp = analog ? Circle(r) : Util.Round(r, rad0))
        {
            if (!framed) using (Brush bb = Festive.BgBrush(t, p.Bg, Math.Max(1, a), r)) g.FillPath(bb, gp);
            Color edge = t.Style == IconStyle.Neon ? p.Acc : Util.Blend(p.Bg, Util.Opaque(t.Edge), 0.85f);
            if (!framed) using (Pen pn = new Pen(Color.FromArgb(Math.Max(120, a), edge), Math.Max(1f, Px(t.EdgeW)))) g.DrawPath(pn, gp);
            // святкова тема: невеликі прикраси в куті (під вмістом, у межах форми віджета)
            if (Festive.HasDecor(t.Id) && a > 40)
            {
                GraphicsState ds = g.Save();
                g.SetClip(gp, CombineMode.Intersect);
                Festive.Decor(g, t, Rectangle.Inflate(r, -Px(2), -Px(2)), Px(100) / 100f, false);
                g.Restore(ds);
            }
        }
        float k = sz.Height / (float)Px(110);
        if (analog) { Analog(g, r, p); return; }
        if (spec.Kind == "calendar" || spec.Kind == "clockcal")
        {
            float kk = sz.Width / (float)Px(spec.Kind == "calendar" ? 240 : 250);
            int top = 0;
            if (spec.Kind == "clockcal")
            {
                DateTime now0 = DateTime.Now;
                string tv = now0.ToString((bar.Config.Clock24 ? "HH:mm" : "h:mm") + (bar.Config.ClockSec ? ":ss" : ""), CultureInfo.InvariantCulture);
                using (Font fv = Big(g, p, tv, 30f * kk * Sc, FontStyle.Bold, sz.Width - Px(16)))
                using (Font fs = p.Font(8.5f * kk * Sc, FontStyle.Regular))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                    using (SolidBrush b = new SolidBrush(p.Fg)) g.DrawString(tv, fv, b, new RectangleF(0, Px(4) * kk, sz.Width, Px(54) * kk), sf);
                    string dl = now0.ToString("dddd, d MMMM", Widgets.UK);
                    using (SolidBrush b = new SolidBrush(p.Sub)) g.DrawString(dl, fs, b, new RectangleF(0, Px(56) * kk, sz.Width, Px(20) * kk), sf);
                }
                top = (int)(Px(80) * kk);
                using (Pen lp = new Pen(Color.FromArgb(90, p.Sub), 1f)) g.DrawLine(lp, Px(16), top, sz.Width - Px(16), top);
            }
            DrawMonth(g, new Rectangle(Px(10), top + Px(6), sz.Width - Px(20), sz.Height - top - Px(14)), p, kk, bar.Config);
            return;
        }

        if (spec.Kind == "disk")
        {
            DrawDiskBox(g, sz, p, sz.Width / (float)Px(260));
            return;
        }
        if (spec.Kind == "trash")
        {
            DrawTrashBox(g, sz, p, sz.Width / (float)Px(100));
            return;
        }
        if (spec.Kind == "fw")
        {
            DrawFwBox(g, sz, p, sz.Width / (float)Px(100));
            return;
        }
        if (spec.Kind == "weather" || spec.Kind == "net" || spec.Kind == "perf")
        {
            float kk = sz.Width / (float)Px(spec.Kind == "weather" ? 270 : 250);
            if (spec.Kind == "weather") DrawWeatherBox(g, sz, p, t, kk, bar.Config);
            else if (spec.Kind == "net") DrawNetBox(g, sz, p, kk);
            else DrawPerfBox(g, sz, p, kk);
            return;
        }

        string title, value, sub = "";
        Color vc = p.Fg;
        DateTime now = DateTime.Now;
        Cfg c = bar.Config;
        switch (spec.Kind)
        {
            case "timer":
            {
                TimerItem ti = ClockData.TimerOf(spec);
                title = ClockData.TimerName(ti).ToUpper(Widgets.UK);
                value = ClockData.Fmt(ti.Left, false);
                sub = ti.Running ? L.T("до ") + ti.End.ToLocalTime().ToString("HH:mm") : (ti.IsPaused ? L.T("пауза") : L.T("двічі клацніть, щоб запустити"));
                if (ti.Running) vc = p.Acc;
                break;
            }
            case "alarm":
            {
                title = L.T("БУДИЛЬНИК");
                Alarm best = null;
                DateTime bt = DateTime.MaxValue;
                foreach (Alarm al in ClockData.Alarms)
                {
                    if (!al.On && al.Snooze == DateTime.MinValue) continue;
                    DateTime nx = al.Snooze != DateTime.MinValue ? al.Snooze.ToLocalTime() : al.Next(now);
                    if (nx < bt) { bt = nx; best = al; }
                }
                if (best == null) { value = "--:--"; sub = L.T("немає увімкнених будильників"); }
                else
                {
                    value = bt.ToString("HH:mm");
                    bool snz = best.Snooze != DateTime.MinValue;
                    TimeSpan d = bt - now;
                    string day = bt.Date == now.Date ? L.T("сьогодні") : (bt.Date == now.Date.AddDays(1) ? L.T("завтра") : bt.ToString("ddd", Widgets.UK));
                    sub = (snz ? L.T("відкладено · ") : day + " · ") + L.T("через ") + (d.TotalHours >= 1 ? (int)d.TotalHours + L.T(" год ") : "") + d.Minutes + L.T(" хв") + (best.Label.Length > 0 ? " · " + best.Label : "");
                    title = snz ? L.T("БУДИЛЬНИК · ВІДКЛАДЕНО") : L.T("БУДИЛЬНИК · ") + best.DaysText.ToUpper(Widgets.UK);
                    vc = p.Acc;
                }
                break;
            }
            case "stopwatch":
                title = L.T("СЕКУНДОМІР");
                value = ClockData.Fmt(ClockData.SwElapsed, false);
                sub = ClockData.SwRunning ? L.T("іде") + (ClockData.Laps.Count > 0 ? L.T(" · кіл: ") + ClockData.Laps.Count : "") : (ClockData.SwElapsed > TimeSpan.Zero ? L.T("пауза") : L.T("двічі клацніть, щоб запустити"));
                if (ClockData.SwRunning) vc = p.Acc;
                break;
            default:
                title = now.ToString("dddd", Widgets.UK).ToUpper(Widgets.UK);
                value = now.ToString((c.Clock24 ? "HH:mm" : "h:mm") + (c.ClockSec ? ":ss" : ""), CultureInfo.InvariantCulture);
                sub = now.ToString("d MMMM yyyy", Widgets.UK);
                break;
        }
        float fk = k * Sc;    // растр 96 dpi: масштаб екрана додаємо вручну
        using (Font ft = p.Font(8.5f * fk, FontStyle.Bold))
        using (Font fv = Big(g, p, value, 30f * fk, FontStyle.Bold, sz.Width - Px(16)))
        using (Font fs = p.Font(8.5f * fk, FontStyle.Regular))
        using (StringFormat sf = new StringFormat())
        {
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            sf.Trimming = StringTrimming.EllipsisCharacter;
            sf.FormatFlags = StringFormatFlags.NoWrap;
            using (SolidBrush b = new SolidBrush(p.Sub)) g.DrawString(title, ft, b, new RectangleF(Px(8), Px(6) * k, sz.Width - Px(16), Px(20) * k), sf);
            using (SolidBrush b = new SolidBrush(vc)) g.DrawString(value, fv, b, new RectangleF(0, Px(24) * k, sz.Width, Px(56) * k), sf);
            using (SolidBrush b = new SolidBrush(p.Sub)) g.DrawString(sub, fs, b, new RectangleF(Px(8), Px(80) * k, sz.Width - Px(16), Px(22) * k), sf);
        }
        TimerItem pt = spec.Kind == "timer" ? ClockData.TimerOf(spec) : null;
        if (pt != null && (pt.Running || pt.IsPaused) && pt.Set.TotalSeconds > 0)
        {
            float frac = (float)(1 - pt.Left.TotalSeconds / pt.Set.TotalSeconds);
            Rectangle br = new Rectangle(Px(18), sz.Height - Px(9), sz.Width - Px(36), Math.Max(3, Px(4)));
            using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Max(60, a / 2), Util.Blend(p.Bg, p.Fg, 0.3f)))) Util.FillRound(g, b, br, br.Height / 2);
            using (SolidBrush b = new SolidBrush(p.Acc)) Util.FillRound(g, b, new Rectangle(br.X, br.Y, Math.Max(br.Height, (int)(br.Width * frac)), br.Height), br.Height / 2);
        }
    }

    // що показує цей монітор: ЦП, ГП, ОЗП (порожньо - все; хоч одне завжди є)
    public static bool[] PerfOf(DeskSpec s)
    {
        string o = (s.Opt ?? "").ToLowerInvariant();
        bool[] r = { true, true, true };
        if (o.Length > 0)
        {
            r[0] = o.Contains("cpu"); r[1] = o.Contains("gpu"); r[2] = o.Contains("ram");
            if (!r[0] && !r[1] && !r[2]) r[0] = true;
        }
        return r;
    }

    // диски цього віджета: вибрані літери або всі
    List<DeskDisk> ShownDisks()
    {
        List<DeskDisk> all = DeskDisks;
        string o = (spec.Opt ?? "").Trim();
        if (o.Length == 0) return all;
        List<string> want = new List<string>(o.ToUpperInvariant().Split(','));
        List<DeskDisk> r = new List<DeskDisk>();
        foreach (DeskDisk d in all) if (want.Contains(d.V.Letter.ToUpperInvariant())) r.Add(d);
        return r;
    }

    // ---------- диски на столі: усі томи з літерою; список оновлюється у фоні ----------
    sealed class DeskDisk { public Vol V; public bool Flash; }
    static List<DeskDisk> deskDisks = new List<DeskDisk>();
    static DateTime deskDisksAt = DateTime.MinValue;
    static volatile bool deskDisksBusy;
    static bool deskHooked;

    static List<DeskDisk> DeskDisks
    {
        get
        {
            if (!deskHooked) { deskHooked = true; NeoBar.DisksChanged += delegate { deskDisksAt = DateTime.MinValue; }; }
            if (!deskDisksBusy && (DateTime.Now - deskDisksAt).TotalSeconds > 20)
            {
                deskDisksBusy = true;
                deskDisksAt = DateTime.Now;
                Util.Bg(delegate
                {
                    List<DeskDisk> l = new List<DeskDisk>();
                    try
                    {
                        Dictionary<int, DiskInfo> dis = new Dictionary<int, DiskInfo>();
                        foreach (Vol v in Vols.List())
                        {
                            if (v.DriveType != 2 && v.DriveType != 3) continue;
                            DiskInfo d = null;
                            if (v.DiskNo >= 0 && !dis.TryGetValue(v.DiskNo, out d)) { d = Vols.Disk(v.DiskNo); dis[v.DiskNo] = d; }
                            DeskDisk dd = new DeskDisk(); dd.V = v; dd.Flash = Vols.IsFlash(v, d);
                            l.Add(dd);
                        }
                    }
                    catch (Exception skipEx) { SbLog.Skip(skipEx); }
                    deskDisks = l;
                    deskDisksBusy = false;
                });
            }
            return deskDisks;
        }
    }

    void DrawDiskBox(Graphics g, Size sz, Pal p, float kk)
    {
        Func<float, int> U = delegate (float v) { return (int)Math.Round(Px(v) * kk); };
        List<DeskDisk> l = ShownDisks();
        using (Font ft = p.Font(8.5f * kk * Sc, FontStyle.Bold))
        using (Font fv = p.Font(9.5f * kk * Sc, FontStyle.Bold))
        using (Font fs = p.Font(8.5f * kk * Sc, FontStyle.Regular))
        using (StringFormat lf = LeftFmt())
        using (StringFormat rf = RightFmt())
        using (StringFormat cf = LeftFmt())
        using (SolidBrush bs = new SolidBrush(p.Sub))
        using (SolidBrush bf = new SolidBrush(p.Fg))
        {
            cf.Alignment = StringAlignment.Center;
            string dh = L.T("Диск");
            if (l.Count > 0)
            {
                List<string> ls = new List<string>();
                for (int i = 0; i < l.Count && i < 8; i++) ls.Add(l[i].V.Letter.Length > 0 ? l[i].V.Letter : "?");
                dh += " (" + string.Join(", ", ls.ToArray()) + ")";
            }
            g.DrawString(dh, ft, bs, new RectangleF(U(8), U(6), sz.Width - U(16), U(20)), cf);
            if (l.Count == 0) { g.DrawString(DeskDisks.Count > 0 ? L.T("Вибраних дисків немає") : L.T("Пошук дисків…"), fs, bs, new RectangleF(U(14), U(30), sz.Width - U(28), U(24)), cf); return; }
            int x = U(14), rw = sz.Width - 2 * x, y = U(30);
            for (int i = 0; i < l.Count && i < 8; i++)
            {
                Vol v = l[i].V;
                int isz = U(14);
                Ico.Draw(g, l[i].Flash ? "usb" : "drive", new RectangleF(x, y + U(3), isz, isz), l[i].Flash ? p.Acc : p.Sub);
                string name = (v.Letter.Length > 0 ? v.Letter + " " : "") + (v.Label.Length > 0 ? v.Label : (l[i].Flash ? L.T("Флешка") : L.T("Диск")));
                g.DrawString(name, fv, bf, new RectangleF(x + isz + U(6), y, rw - isz - U(6) - U(90), U(20)), lf);
                g.DrawString(L.T("вільно ") + Vols.Size(v.Free), fs, bs, new RectangleF(x, y, rw, U(20)), rf);
                int up = Math.Max(0, v.UsedPct);
                Rectangle br = new Rectangle(x, y + U(22), rw, Math.Max(4, U(6)));
                using (SolidBrush b = new SolidBrush(Util.Blend(p.Bg, p.Fg, 0.18f))) Util.FillRound(g, b, br, br.Height / 2);
                Color bc = up >= 90 ? p.Bad : (up >= 75 ? p.Warn : p.Acc);
                using (SolidBrush b = new SolidBrush(bc)) Util.FillRound(g, b, new Rectangle(br.X, br.Y, Math.Max(br.Height, br.Width * up / 100), br.Height), br.Height / 2);
                y += U(38);
            }
        }
    }

    static StringFormat LeftFmt()
    {
        StringFormat sf = new StringFormat();
        sf.Alignment = StringAlignment.Near; sf.LineAlignment = StringAlignment.Center;
        sf.Trimming = StringTrimming.EllipsisCharacter; sf.FormatFlags = StringFormatFlags.NoWrap;
        return sf;
    }

    static StringFormat RightFmt()
    {
        StringFormat sf = LeftFmt();
        sf.Alignment = StringAlignment.Far;
        return sf;
    }

    void DrawWeatherBox(Graphics g, Size sz, Pal p, Theme t, float kk, Cfg c)
    {
        Func<float, int> U = delegate (float v) { return (int)Math.Round(Px(v) * kk); };
        using (Font fc = p.Font(8.5f * kk * Sc, FontStyle.Bold))
        using (Font fv = Big(g, p, double.IsNaN(Widgets.Temp) ? "" : Widgets.TempText(Widgets.Temp, spec.Opt == "us"), 28f * kk * Sc, FontStyle.Bold, sz.Width - (int)Math.Round(Px(100) * kk)))
        using (Font fm = p.Font(9.5f * kk * Sc, FontStyle.Regular))
        using (Font fs = p.Font(8f * kk * Sc, FontStyle.Regular))
        using (StringFormat lf = LeftFmt())
        using (SolidBrush bs = new SolidBrush(p.Sub))
        using (SolidBrush bf = new SolidBrush(p.Fg))
        {
            if (!double.IsNaN(Widgets.Temp) && WeatherFx.Active(c))
                WeatherFx.Draw(g, new Rectangle(0, 0, sz.Width - 1, sz.Height - 1), p.Rad + Px(4), Sc * kk, Util.Lum(p.Bg) > 140);
            string city = Widgets.CityLabel(c);
            g.DrawString((city.Length > 0 ? city : L.T("Погода")).ToUpper(Widgets.UK), fc, bs, new RectangleF(U(14), U(6), sz.Width - U(28), U(20)), lf);
            if (double.IsNaN(Widgets.Temp))
            {
                string msg = Widgets.WeatherBusy ? L.T("Завантаження…") : (Widgets.WErr.Length > 0 ? L.T("Погода: ") + Widgets.WErr : L.T("Немає даних"));
                g.DrawString(msg, fm, bs, new RectangleF(U(14), U(34), sz.Width - U(28), U(60)), lf);
                return;
            }
            Widgets.DrawWeather(g, new Rectangle(U(12), U(30), U(66), U(66)), Widgets.WCode, Widgets.WDay, t, p.Fg);
            bool us = spec.Opt == "us";
            string tv = Widgets.TempText(Widgets.Temp, us);
            g.DrawString(tv, fv, bf, new RectangleF(U(90), U(26), sz.Width - U(100), U(46)), lf);
            g.DrawString(Widgets.WeatherText(Widgets.WCode), fm, bs, new RectangleF(U(90), U(72), sz.Width - U(100), U(20)), lf);
            string ex = L.T("Вітер ") + Widgets.WindText(us) + " · " + Widgets.Hum + "%";
            g.DrawString(ex, fs, bs, new RectangleF(U(14), U(98), sz.Width - U(28), U(20)), lf);
        }
    }

    // ---------- «Брандмауер»: стан і вмикання/вимикання одним кліком по щиту ----------
    bool fwBusy;
    string fwMsg = "";
    DateTime fwMsgAt;

    void FwClick()
    {
        NeoBar bar = ClockData.Bar;
        if (bar == null || fwBusy) return;
        bool turnOn = Fw.Overall(true) != 2;
        if (!turnOn && SbDialog.Show(bar, null, L.T("Брандмауер"), L.T("Вимкнути брандмауер Windows (усі профілі)?\n\nБез нього ПК не перевіряє мережевий трафік. Вимикайте лише ненадовго й увімкніть знову."), L.T("Вимкнути"), L.T("Скасувати")) != 0) return;
        fwBusy = true;
        Redraw();
        List<string> l = new List<string>();
        l.Add(Fw.PlState(3, turnOn));
        Util.Bg(delegate
        {
            string err;
            string t = Fw.Apply(l, out err);
            string fe;
            int bad;
            NetCfg.Summary(t, out fe, out bad);
            string msg = t == null ? err : (bad > 0 ? fe : "");
            try
            {
                BeginInvoke(new Action(delegate
                {
                    fwBusy = false;
                    fwMsg = msg; fwMsgAt = DateTime.Now;
                    Fw.Overall(true);
                    Redraw();
                    ClockData.RedrawKind("fw");
                    Fw.RaiseChanged();      // відкрите вікно «Мережа» теж одразу покаже новий стан
                }));
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
        });
    }

    void DrawFwBox(Graphics g, Size sz, Pal p, float kk)
    {
        Func<float, int> U = delegate (float v) { return (int)Math.Round(Px(v) * kk); };
        int st = Fw.Overall(false);
        Color c = fwBusy ? p.Acc : (st == 2 ? p.Good : (st == 0 ? p.Bad : (st == 1 ? p.Warn : p.Sub)));
        bool msg = fwMsg.Length > 0 && (DateTime.Now - fwMsgAt).TotalSeconds < 8;
        string big = fwBusy ? "…" : (st == 2 ? L.T("УВІМК.") : (st == 0 ? L.T("ВИМК.") : (st == 1 ? L.T("ЧАСТКОВО") : "—")));
        string small = fwBusy ? L.T("зачекайте") : (msg ? L.T("не вдалося") : (st == 2 ? L.T("клік - вимкнути") : (st >= 0 ? L.T("клік - увімкнути") : "")));
        int isz = U(36);
        Rectangle ir = new Rectangle((sz.Width - isz) / 2, U(6), isz, isz);
        GraphicsState gst = g.Save();
        if (small.Length == 0) g.TranslateTransform(0, U(7));
        using (SolidBrush zb = new SolidBrush(Color.FromArgb(1, 0, 0, 0))) g.FillRectangle(zb, Rectangle.Inflate(ir, U(6), U(6)));
        Ico.Draw(g, "shield", ir, c);
        using (Font ft = p.Font(8f * kk * Sc, FontStyle.Bold))
        using (Font fv = p.Font((big.Length > 6 ? 9.5f : 12f) * kk * Sc, FontStyle.Bold))
        using (Font fm = p.Font(7f * kk * Sc, FontStyle.Regular))
        using (StringFormat cf = new StringFormat(StringFormatFlags.NoWrap))
        using (SolidBrush bs = new SolidBrush(p.Sub))
        using (SolidBrush bf = new SolidBrush(c))
        {
            cf.Alignment = StringAlignment.Center; cf.LineAlignment = StringAlignment.Center; cf.Trimming = StringTrimming.EllipsisCharacter;
            g.DrawString(L.T("БРАНДМАУЕР"), ft, bs, new RectangleF(U(2), U(43), sz.Width - U(4), U(14)), cf);
            g.DrawString(big, fv, bf, new RectangleF(U(2), U(56), sz.Width - U(4), U(24)), cf);
            if (small.Length > 0) g.DrawString(small, fm, bs, new RectangleF(U(2), U(80), sz.Width - U(4), U(14)), cf);
        }
        g.Restore(gst);
    }

    // раз на годину (і при першому показі) оновлюємо оцінку сміття, щоб на віджеті було актуальне число
    void TrashPoll()
    {
        NeoBar bar = ClockData.Bar;
        if (bar == null || Cleaner.Busy) return;
        if (Cleaner.LastAt == DateTime.MinValue || (DateTime.Now - Cleaner.LastAt).TotalMinutes > 60) { Cleaner.Init(); Cleaner.Run(bar.Config, false); }
    }

    // клік по віджету: очистити вибране у вікні «Очищення» (без вибору - відкрити вікно)
    void TrashClick()
    {
        NeoBar bar = ClockData.Bar;
        if (bar == null || Cleaner.Busy) return;
        Cleaner.Init();
        bool any = false, rec = false;
        foreach (JunkCat c in Cleaner.Cats)
            if (Cleaner.On(bar.Config, c)) { any = true; if (c.Recycle && c.Files > 0) rec = true; }
        if (!any) { bar.OpenClean(); return; }
        // кошик, якщо вибраний, очищається безповоротно - лише тоді питаємо
        if (rec && SbDialog.Show(bar, null, L.T("Очищення"), L.T("Серед вибраного є кошик - звідти файли вже не повернути.\n\nОчистити вибране?"), L.T("Очистити"), L.T("Скасувати")) != 0) return;
        trashRun = true;
        Cleaner.Run(bar.Config, true);
        if (!Cleaner.Busy) trashRun = false;
    }

    // стан очищення змінився; коли очищення, запущене цим віджетом, завершилось - запам'ятовуємо, скільки звільнено
    void TrashChanged()
    {
        if (trashRun && !Cleaner.Busy)
        {
            trashRun = false;
            if (Cleaner.LastFreed >= 0) { spec.Opt = "f" + Cleaner.LastFreed; ClockData.Save(); }
        }
        Redraw();
    }

    // скільки віджет очистив востаннє (зберігається в налаштуваннях); -1 - ще не чистив
    long TrashFreed()
    {
        long v;
        string o = spec.Opt ?? "";
        return o.StartsWith("f") && long.TryParse(o.Substring(1), out v) ? v : -1;
    }

    // чи курсор над самою іконкою корзини (координати вікна)
    bool OnTrashIcon(Point pt)
    {
        if (spec.Kind != "trash" && spec.Kind != "fw") return false;
        Size cs = Want();
        float kk = cs.Width / (float)Px(100);
        int isz = (int)Math.Round(Px(36) * kk), pad = (int)Math.Round(Px(6) * kk);
        Rectangle ir = new Rectangle(dx0 + (cs.Width - isz) / 2, T0 + (int)Math.Round(Px(6) * kk), isz, isz);
        ir.Inflate(pad, pad);
        return ir.Contains(pt);
    }

    void DrawTrashBox(Graphics g, Size sz, Pal p, float kk)
    {
        Func<float, int> U = delegate (float v) { return (int)Math.Round(Px(v) * kk); };
        bool busy = Cleaner.Busy;
        bool done = !busy && Cleaner.LastFreed >= 0 && (DateTime.Now - Cleaner.LastCleanAt).TotalSeconds < 90;
        // після очищення: велике - що ЗАЛИШИЛОСЬ (зазвичай 0 Б), нижче дрібно - скільки звільнено
        string cap = busy ? (Cleaner.Stage.Length > 0 ? Cleaner.Stage : L.T("Очищення")) : (done ? L.T("ЗАЛИШИЛОСЬ") : L.T("СМІТТЯ"));
        string big = busy ? "…" : (Cleaner.LastFound >= 0 ? Cleaner.Size(Cleaner.LastFound) : (done ? L.T("0 Б") : "—"));
        int isz = U(36);
        Rectangle ir = new Rectangle((sz.Width - isz) / 2, U(6), isz, isz);
        long was = TrashFreed();
        string small = busy ? "" : (done ? (Cleaner.LastFreed > 0 ? L.T("Звільнено: ") + Cleaner.Size(Cleaner.LastFreed) : L.T("Вже було чисто")) : (was >= 0 ? L.T("Очищено: ") + Cleaner.Size(was) : ""));
        // без нижнього рядка вміст нижчий - зсуваємо його, щоб він стояв по центру плитки
        GraphicsState gst = g.Save();
        if (small.Length == 0) g.TranslateTransform(0, U(7));
        // майже невидима тверда зона під іконкою: у прозорих місцях значка клік інакше проходив би «крізь» вікно
        using (SolidBrush zb = new SolidBrush(Color.FromArgb(1, 0, 0, 0))) g.FillRectangle(zb, Rectangle.Inflate(ir, U(6), U(6)));
        Ico.Draw(g, "trash", ir, busy ? p.Acc : (done ? p.Good : p.Fg));
        using (Font ft = p.Font(8f * kk * Sc, FontStyle.Bold))
        using (Font fv = Big(g, p, big, 13f * kk * Sc, FontStyle.Bold, sz.Width - U(8)))
        using (Font fm = p.Font(7f * kk * Sc, FontStyle.Regular))
        using (StringFormat cf = new StringFormat(StringFormatFlags.NoWrap))
        using (SolidBrush bs = new SolidBrush(p.Sub))
        using (SolidBrush bf = new SolidBrush(busy ? p.Acc : p.Fg))
        {
            cf.Alignment = StringAlignment.Center; cf.LineAlignment = StringAlignment.Center; cf.Trimming = StringTrimming.EllipsisCharacter;
            g.DrawString(cap, ft, bs, new RectangleF(U(2), U(43), sz.Width - U(4), U(14)), cf);
            g.DrawString(big, fv, bf, new RectangleF(U(2), U(56), sz.Width - U(4), U(24)), cf);
            if (small.Length > 0) g.DrawString(small, fm, bs, new RectangleF(U(2), U(80), sz.Width - U(4), U(14)), cf);
        }
        g.Restore(gst);
    }

    void DrawNetBox(Graphics g, Size sz, Pal p, float kk)
    {
        Func<float, int> U = delegate (float v) { return (int)Math.Round(Px(v) * kk); };
        using (Font ft = p.Font(8.5f * kk * Sc, FontStyle.Bold))
        using (Font fv = p.Font(11.5f * kk * Sc, FontStyle.Bold))
        using (Font fs = p.Font(8.5f * kk * Sc, FontStyle.Regular))
        using (StringFormat lf = LeftFmt())
        using (StringFormat cf = LeftFmt())
        using (SolidBrush bs = new SolidBrush(p.Sub))
        using (SolidBrush ba = new SolidBrush(p.Acc))
        using (SolidBrush bf = new SolidBrush(p.Fg))
        {
            cf.Alignment = StringAlignment.Center;
            g.DrawString(L.T("МЕРЕЖА"), ft, bs, new RectangleF(U(8), U(6), sz.Width - U(16), U(20)), cf);
            int hw = (sz.Width - U(28)) / 2;
            int w1 = hw + U(8), w2 = hw - U(8);
            g.DrawString("↓ " + Widgets.RateText(Widgets.NetDown), fv, ba, new RectangleF(U(14), U(30), w1, U(32)), lf);
            g.DrawString("↑ " + Widgets.RateText(Widgets.NetUp), fv, bf, new RectangleF(U(14) + w1, U(30), w2, U(32)), lf);
            Color qc = PingMon.Quality(p.Good, p.Warn, p.Bad);
            using (SolidBrush bq = new SolidBrush(qc))
                g.DrawString(L.T("Пінг ") + PingMon.Title + ": " + PingMon.Text, fs, bq, new RectangleF(U(14), U(70), sz.Width - U(28), U(22)), lf);
            g.DrawString(PingMon.Detail.Length > 0 ? PingMon.Detail : L.T("завантаження · віддача"), fs, bs, new RectangleF(U(14), U(88), sz.Width - U(28), U(18)), lf);
        }
    }

    void DrawPerfBox(Graphics g, Size sz, Pal p, float kk)
    {
        Func<float, int> U = delegate (float v) { return (int)Math.Round(Px(v) * kk); };
        using (Font ft = p.Font(8.5f * kk * Sc, FontStyle.Bold))
        using (Font fv = p.Font(10.5f * kk * Sc, FontStyle.Bold))
        using (Font fs = p.Font(8.5f * kk * Sc, FontStyle.Regular))
        using (StringFormat lf = LeftFmt())
        using (StringFormat rf = RightFmt())
        using (StringFormat cf = LeftFmt())
        using (SolidBrush bs = new SolidBrush(p.Sub))
        using (SolidBrush bf = new SolidBrush(p.Fg))
        {
            cf.Alignment = StringAlignment.Center;
            bool[] ps = PerfOf(spec);
            List<string> hn = new List<string>();
            if (ps[0]) hn.Add(L.T("ЦП"));
            if (ps[1]) hn.Add(L.T("ГП"));
            if (ps[2]) hn.Add(L.T("ОЗП"));
            g.DrawString(L.T("Монітор системи (") + string.Join(", ", hn.ToArray()) + ")", ft, bs, new RectangleF(U(8), U(6), sz.Width - U(16), U(20)), cf);
            string cpuSub = float.IsNaN(Sensors.CpuTemp) ? "" : Sensors.F0(Sensors.CpuTemp, "°");
            string ramSub = Sensors.RamTotalGb > 0 ? Sensors.RamUsedGb.ToString("0.0", Widgets.UK) + " / " + Sensors.RamTotalGb.ToString("0.0", Widgets.UK) + L.T(" ГБ") : "";
            string gpuSub = !float.IsNaN(Sensors.GpuTemp) ? Sensors.F0(Sensors.GpuTemp, "°") + (float.IsNaN(Sensors.Hotspot) ? "" : " (hotspot " + Sensors.F0(Sensors.Hotspot, "°") + ")") :
                            (Sensors.VramTotalGb > 0 ? Sensors.VramUsedGb.ToString("0.0", Widgets.UK) + " / " + Sensors.VramTotalGb.ToString("0", Widgets.UK) + L.T(" ГБ") : "");
            int ry = U(28), step = U(38);
            if (ps[0]) { PerfRow(g, p, kk, ry, L.T("ЦП"), Sensors.Cpu, cpuSub, sz.Width, fv, fs, lf, rf, bf, bs); ry += step; }
            if (ps[1]) { PerfRow(g, p, kk, ry, L.T("ГП"), Sensors.GpuLoad, gpuSub, sz.Width, fv, fs, lf, rf, bf, bs); ry += step; }
            if (ps[2]) { PerfRow(g, p, kk, ry, L.T("ОЗП"), Sensors.Ram, ramSub, sz.Width, fv, fs, lf, rf, bf, bs); ry += step; }
        }
    }

    void PerfRow(Graphics g, Pal p, float kk, int y, string name, float v, string extra, int w, Font fv, Font fs, StringFormat lf, StringFormat rf, Brush bf, Brush bs)
    {
        int x = (int)Math.Round(Px(14) * kk), rw = w - 2 * x, lh = (int)Math.Round(Px(20) * kk);
        g.DrawString(name, fv, bf, new RectangleF(x, y, rw, lh), lf);
        string pc = float.IsNaN(v) ? "—" : Math.Round(v) + "%";
        g.DrawString(extra.Length > 0 ? extra + "   " + pc : pc, fv, bf, new RectangleF(x, y, rw, lh), rf);
        Rectangle br = new Rectangle(x, y + lh + (int)Math.Round(Px(3) * kk), rw, Math.Max(4, (int)Math.Round(Px(6) * kk)));
        using (SolidBrush b = new SolidBrush(Util.Blend(p.Bg, p.Fg, 0.18f))) Util.FillRound(g, b, br, br.Height / 2);
        if (!float.IsNaN(v))
        {
            int fw = Math.Max(br.Height, (int)(br.Width * Math.Max(0, Math.Min(100, v)) / 100f));
            using (SolidBrush b = new SolidBrush(v >= 90 ? p.Bad : p.Acc)) Util.FillRound(g, b, new Rectangle(br.X, br.Y, fw, br.Height), br.Height / 2);
        }
    }

    // місяць сіткою: сьогодні - коло акценту, свята - червоним
    void DrawMonth(Graphics g, Rectangle r, Pal p, float kk, Cfg c)
    {
        DateTime today = DateTime.Today, m0 = new DateTime(today.Year, today.Month, 1);
        string mt = m0.ToString("MMMM yyyy", Widgets.UK);
        mt = char.ToUpper(mt[0]) + mt.Substring(1);
        int hh = (int)(Px(22) * kk);
        using (Font fh = p.Font(9.5f * kk * Sc, FontStyle.Bold))
        using (Font fd = p.Font(8f * kk * Sc, FontStyle.Regular))
        using (Font fb = p.Font(8f * kk * Sc, FontStyle.Bold))
        using (Font fw = p.Font(6.5f * kk * Sc, FontStyle.Regular))
        using (StringFormat sf = new StringFormat())
        {
            sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
            using (SolidBrush b = new SolidBrush(p.Fg)) g.DrawString(mt, fh, b, new RectangleF(r.X, r.Y, r.Width, hh), sf);
            float cw = r.Width / 7f, ch = (r.Height - hh - Px(16) * kk) / 6f;
            float y0 = r.Y + hh;
            for (int i = 0; i < 7; i++)
                using (SolidBrush b = new SolidBrush(i >= 5 ? p.Bad : p.Sub))
                    g.DrawString(Alarm.DayNames[i], fw, b, new RectangleF(r.X + i * cw, y0, cw, Px(16) * kk), sf);
            y0 += Px(16) * kk;
            int lead = ((int)m0.DayOfWeek + 6) % 7;
            DateTime d = m0.AddDays(-lead);
            for (int row = 0; row < 6; row++)
                for (int col = 0; col < 7; col++, d = d.AddDays(1))
                {
                    RectangleF cr = new RectangleF(r.X + col * cw, y0 + row * ch, cw, ch);
                    bool inM = d.Month == m0.Month;
                    bool hol = false;
                    try { foreach (Holiday h in Holidays.On(d, c)) if (h.Kind == Holiday.Public) hol = true; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                    if (d == today)
                    {
                        float ds = Math.Min(cw, ch) - 2;
                        using (SolidBrush b = new SolidBrush(p.Acc)) g.FillEllipse(b, cr.X + (cw - ds) / 2, cr.Y + (ch - ds) / 2, ds, ds);
                    }
                    Color tc = d == today ? Util.Readable(p.Fg, p.Acc, 120) : (hol ? p.Bad : (col >= 5 ? Util.Blend(p.Fg, p.Bad, 0.45f) : p.Fg));
                    if (!inM) tc = Color.FromArgb(90, tc);
                    using (SolidBrush b = new SolidBrush(tc)) g.DrawString(d.Day.ToString(), d == today || hol ? fb : fd, b, cr, sf);
                }
        }
    }

    static GraphicsPath Circle(Rectangle r)
    {
        GraphicsPath gp = new GraphicsPath();
        gp.AddEllipse(r);
        return gp;
    }

    void Analog(Graphics g, Rectangle r, Pal p)
    {
        DateTime now = DateTime.Now;
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, rad = r.Width / 2f - Px(6);
        using (Pen tick = new Pen(p.Sub, Math.Max(1.2f, rad * 0.02f)))
        using (Pen tick5 = new Pen(p.Fg, Math.Max(2f, rad * 0.045f)))
            for (int i = 0; i < 60; i++)
            {
                double ang = i * Math.PI / 30;
                bool big = i % 5 == 0;
                float r1 = rad * (big ? 0.8f : 0.88f), r2 = rad * 0.94f;
                g.DrawLine(big ? tick5 : tick, cx + (float)Math.Sin(ang) * r1, cy - (float)Math.Cos(ang) * r1, cx + (float)Math.Sin(ang) * r2, cy - (float)Math.Cos(ang) * r2);
            }
        // цифри
        if (spec.Numbers > 0)
        {
            float fk = rad / Px(80f) * Sc;
            using (Font fo = p.Font(Math.Max(6f, 11f * fk), FontStyle.Bold))
            using (Font fi = p.Font(Math.Max(5f, 6.5f * fk), FontStyle.Regular))
            using (SolidBrush bo = new SolidBrush(p.Fg))
            using (SolidBrush bi = new SolidBrush(p.Sub))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                for (int h = 1; h <= 12; h++)
                {
                    bool quarter = h % 3 == 0;
                    if (spec.Numbers == 1 && !quarter) continue;
                    double ang = h * Math.PI / 6;
                    float ro = rad * 0.64f;
                    g.DrawString(h.ToString(), fo, bo, cx + (float)Math.Sin(ang) * ro, cy - (float)Math.Cos(ang) * ro, sf);
                    if (spec.Numbers == 3)
                    {
                        float ri = rad * 0.44f;
                        g.DrawString((h + 12).ToString(), fi, bi, cx + (float)Math.Sin(ang) * ri, cy - (float)Math.Cos(ang) * ri, sf);
                    }
                }
            }
        }
        double sec = now.Second, min = now.Minute + sec / 60.0, hr = now.Hour % 12 + min / 60.0;
        Hand(g, cx, cy, hr * Math.PI / 6, rad * 0.5f, Math.Max(3f, rad * 0.07f), p.Fg);
        Hand(g, cx, cy, min * Math.PI / 30, rad * 0.74f, Math.Max(2f, rad * 0.045f), p.Fg);
        Hand(g, cx, cy, sec * Math.PI / 30, rad * 0.82f, Math.Max(1.2f, rad * 0.02f), p.Acc);
        using (SolidBrush b = new SolidBrush(p.Acc)) g.FillEllipse(b, cx - rad * 0.05f, cy - rad * 0.05f, rad * 0.1f, rad * 0.1f);
    }

    static void Hand(Graphics g, float cx, float cy, double ang, float len, float w, Color c)
    {
        using (Pen pn = new Pen(c, w))
        {
            pn.StartCap = LineCap.Round; pn.EndCap = LineCap.Round;
            g.DrawLine(pn, cx - (float)Math.Sin(ang) * len * 0.15f, cy + (float)Math.Cos(ang) * len * 0.15f, cx + (float)Math.Sin(ang) * len, cy - (float)Math.Cos(ang) * len);
        }
    }

    // ---------- миша ----------

    void SetOpacityFrom(int x)
    {
        int l, rr;
        Track(out l, out rr);
        int v = 10 + (int)Math.Round((Math.Max(l, Math.Min(rr, x)) - l) * 90.0 / Math.Max(1, rr - l));
        if (v == spec.Opacity) return;
        spec.Opacity = v;
        Redraw();
    }

    void SetScaleFrom(Point pt)
    {
        Size b = Want(1f);
        float ky = (pt.Y - T0) / (float)Math.Max(1, b.Height);
        float k = dx0 > 0 ? ky : Math.Max(pt.X / (float)Math.Max(1, b.Width), ky);   // у ширшій рамці - за висотою
        k = Math.Max(MinK, Math.Min(MaxK, k));
        if (Math.Abs(k - K) < 0.004f) return;
        spec.Scale = k;
        Redraw();
    }

    int HitAt(Point pt)
    {
        if (spec.Locked) return rUnlock.Contains(pt) ? 5 : 0;
        if (rGrip.Contains(pt)) return 4;
        if (rLock.Contains(pt)) return 2;
        if (rClose.Contains(pt)) return 3;
        if (rSlider.Contains(pt)) return 1;
        return 0;
    }

    // анімація замка: 0 - відкритий, 1 - закритий
    float lkT;
    bool lkBusy;
    System.Windows.Forms.Timer lkTimer;

    void AnimLock(float from, float to, Action done)
    {
        if (lkBusy) return;
        lkBusy = true; lkT = from;
        DateTime t0 = DateTime.Now;
        lkTimer = new System.Windows.Forms.Timer();
        lkTimer.Interval = 16;
        lkTimer.Tick += delegate
        {
            float f = (float)((DateTime.Now - t0).TotalMilliseconds / 240.0);
            if (f >= 1f)
            {
                lkTimer.Stop(); lkTimer.Dispose(); lkTimer = null;
                lkBusy = false;
                done();
                return;
            }
            lkT = from + (to - from) * (1f - (1f - f) * (1f - f));
            Redraw();
        };
        lkTimer.Start();
        Redraw();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || lkBusy) return;
        int h = HitAt(e.Location);
        if (spec.Locked)
        {
            if (h == 5) AnimLock(1f, 0f, delegate { spec.Locked = false; hot = false; hover = 0; ClockData.Save(); Redraw(); });
            else if (h == 0 && OnTrashIcon(e.Location)) { trashArm = true; trashAt = Cursor.Position; }
            return;
        }

        if (h == 4) { resizing = true; Capture = true; return; }
        if (h == 1) { sliding = true; Capture = true; SetOpacityFrom(e.X); return; }
        if (h == 2) { AnimLock(0f, 1f, delegate { spec.Locked = true; hot = true; hover = 0; ClockData.Save(); Redraw(); }); return; }       // замкнений віджет лишається без панелі
        if (h == 3) { ClockData.RemoveDesk(spec); return; }
        dragging = true; dragFrom = e.Location; Capture = true;
        if (OnTrashIcon(e.Location)) { trashArm = true; trashAt = Cursor.Position; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (resizing) { SetScaleFrom(e.Location); return; }
        if (sliding) { SetOpacityFrom(e.X); return; }
        if (!dragging)
        {
            bool wasHot = hot;
            hot = true;
            if (spec.Locked && !wasHot) Redraw();          // показати замочок
            int h = HitAt(e.Location);
            // розблокований віджет тягнуть за будь-яке місце - курсор перетягування
            Cursor = h == 4 ? Cursors.SizeNWSE : (h > 0 || (h == 0 && OnTrashIcon(e.Location)) ? Cursors.Hand : (spec.Locked ? Cursors.Default : Cursors.SizeAll));
            if (h != hover) { hover = h; Redraw(); }
            return;
        }
        Location = new Point(Location.X + e.X - dragFrom.X, Location.Y + e.Y - dragFrom.Y);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (dragging || resizing || sliding) return;
        bool need = hover != 0 || (spec.Locked && hot);
        hover = 0; hot = false;
        if (need) Redraw();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        // «Кошик»: клік без пересування - очистити
        bool tclick = false;
        if (trashArm)
        {
            trashArm = false;
            Point cp = Cursor.Position;
            tclick = e.Button == MouseButtons.Left && Math.Abs(cp.X - trashAt.X) + Math.Abs(cp.Y - trashAt.Y) < 5;
        }
        if (resizing || sliding)
        {
            resizing = sliding = false; Capture = false;
            ClockData.Save();
            KeepBottom();
            return;
        }
        if (dragging)
        {
            dragging = false; Capture = false;
            spec.X = Left + dx0; spec.Y = Top + T0;
            ClockData.Save();
            KeepBottom();
        }
        if (tclick) { if (spec.Kind == "fw") FwClick(); else TrashClick(); }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (e.Y < T0 || HitAt(e.Location) > 0) return;          // на панелі керування подвійний клік нічого не відкриває
        if (spec.Kind == "disk") { if (ClockData.Bar != null) ClockData.Bar.OpenDisk(); }
        else if (spec.Kind == "weather") { if (ClockData.Bar != null) ClockData.Bar.OpenWeather(); }
        else if (spec.Kind == "net") { if (ClockData.Bar != null) ClockData.Bar.OpenNet(); }
        else if (spec.Kind == "fw") { if (ClockData.Bar != null) ClockData.Bar.OpenNet(3); }
        else if (spec.Kind == "perf") { if (ClockData.Bar != null) ClockData.Bar.OpenPerf(0); }
        else if (spec.Kind == "timer") ClockData.TimerStartPause(ClockData.TimerOf(spec));
        else if (spec.Kind == "stopwatch") ClockData.SwStartPause();
        else if (ClockData.Bar != null) ClockData.Bar.OpenClock(spec.Kind == "alarm" ? 3 : 0);
    }
}
