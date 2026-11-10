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

// Вікно «Годинник і календар»: годинник, таймери, секундомір, будильники (налаштування - вікно налаштувань, «Місце й погода»)

// ----------------------------------------------------------------------
//  Вікно годинника
// ----------------------------------------------------------------------
sealed partial class ClockWindow : SbWindow
{
    TextBox tbTimer, tbAlarm;
    int lapScroll;
    // редагування будильника (копія; оригінал змінюється лише після «Зберегти»)
    Alarm edit, editOrig;
    bool syncingTimer;

    public ClockWindow(NeoBar bar, int tab) : base(bar, L.T("Час і дата"), 690, 868)
    { Glyph = "\uE121";
        Tabs = new string[] { L.T("Годинник і календар"), L.T("Секундомір"), L.T("Таймер"), L.T("Будильник") };
        Tab = Math.Max(0, Math.Min(3, tab));
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        if (Height > wa.Height - 20) Height = wa.Height - 20;
        InitCalendar();
        tbTimer = MakeInput();
        tbTimer.Text = ClockData.CurTimer.Label;
        tbTimer.TextChanged += delegate
        {
            if (syncingTimer) return;
            ClockData.CurTimer.Label = tbTimer.Text.Trim();
            ClockData.Save();
        };
        tbAlarm = MakeInput();
        ClockData.Changed += OnData;
        OnTab();
    }

    void OnData() { if (!IsDisposed) Invalidate(); }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        ClockData.Changed -= OnData;
        Holidays.Changed -= OnHol;
        Chime.Stop();
        base.OnFormClosed(e);
    }

    public int CurrentTab { get { return Tab; } }

    public void SelectTab(int t) { Tab = Math.Max(0, Math.Min(3, t)); OnTab(); Invalidate(); }

    protected override void OnDragEnd()
    {
        // після зміни гучності - коротка перевірка звуку
        if (Tab == 3 && edit != null) { Chime.Play(edit.Sound, false, edit.Volume); preview = edit.Sound; }
        else if (Tab == 2) { TimerItem t = ClockData.CurTimer; ClockData.Save(); Chime.Play(t.Sound, false, t.Volume); preview = t.Sound; }
    }

    protected override void OnTab()
    {
        Chime.Stop();
        preview = null;
        tbTimer.Visible = false;
        tbAlarm.Visible = false;
        tbName.Visible = false;
        adding = false;
        edit = null;
        Ticker.Interval = Tab == 1 ? 33 : (Tab == 2 ? 250 : 1000);
    }

    protected override void OnTick()
    {
        Invalidate();
    }

    protected override void HeadButtons(Graphics g, ref int right)
    {
        Rectangle r = new Rectangle(right - Px(32), Px(7), Px(32), Px(32));
        string kind = Tab == 0 ? DeskKinds[deskPick] : (Tab == 1 ? "stopwatch" : (Tab == 2 ? "timer" : "alarm"));
        IconBtn(g, r, "desk", delegate { ClockData.AddDesk(Tab == 0 ? DeskKinds[deskPick] : kind); }, false);
        right = r.Left - Px(4);
    }

    static readonly string[] DeskKinds = { "clock", "analog", "calendar", "clockcal" };
    static readonly string[] DeskNames = { L.T("Цифровий"), L.T("Стрілковий"), L.T("Календар"), L.T("Час + календар") };

    Bitmap[] previews;
    protected override void ThemeUpdated() { previews = null; }
    int deskPick;

    // галерея тем годинника для робочого столу
    void PaintDeskGallery(Graphics g, Rectangle r)
    {
        TxtL(g, L.T("Годинники на робочий стіл"), FB, new Rectangle(r.X, r.Y, r.Width, Px(28)), P.Fg);
        if (previews == null)
        {
            previews = new Bitmap[DeskKinds.Length];
            for (int i = 0; i < DeskKinds.Length; i++) try { previews[i] = DeskClock.Preview(DeskKinds[i]); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
        int y = r.Y + Px(34);
        int numH = Px(38);              // рядок «Цифри» під плитками (активний для стрілкового)
        int gap = Px(8), tw = (r.Width - gap * 3) / 4, th = r.Bottom - y - numH;
        for (int i = 0; i < DeskKinds.Length; i++)
        {
            Rectangle tr = new Rectangle(r.X + i * (tw + gap), y, tw, th);
            bool sel = deskPick == i;
            Card(g, tr, sel ? Util.Blend(P.Bg, P.Acc, 0.28f) : (Over(tr) ? Util.Blend(P.Card, P.Fg, 0.07f) : P.Card));
            if (sel)
                using (GraphicsPath gp = Util.Round(tr, P.Rad))
                using (Pen pn = new Pen(P.Acc, Math.Max(1.5f, Px(2)))) g.DrawPath(pn, gp);
            Rectangle ir = new Rectangle(tr.X + Px(6), tr.Y + Px(6), tr.Width - Px(12), tr.Height - Px(26));
            if (previews[i] != null)
            {
                float k = Math.Min(ir.Width / (float)previews[i].Width, ir.Height / (float)previews[i].Height);
                int w = (int)(previews[i].Width * k), h = (int)(previews[i].Height * k);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(previews[i], new Rectangle(ir.X + (ir.Width - w) / 2, ir.Y + (ir.Height - h) / 2, w, h));
            }
            TxtC(g, DeskNames[i], FS, new Rectangle(tr.X, tr.Bottom - Px(20), tr.Width, Px(18)), sel ? P.Fg : P.Sub);
            int k2 = i;
            Hot(tr, delegate { deskPick = k2; });
        }

        // цифри стрілкового годинника: вибираються, коли вибрано «Стрілковий»
        bool analog = DeskKinds[deskPick] == "analog";
        int ny = r.Bottom - numH + Px(8);
        TxtL(g, L.T("Цифри"), FS, new Rectangle(r.X, ny, Px(60), Px(28)), analog ? P.Fg : Util.Blend(P.Bg, P.Sub, 0.6f));
        string[] nn = { L.T("Без цифр"), "12 · 3 · 6 · 9", "1–12", L.T("1–12 і 13–24") };
        int nx = r.X + Px(60), nw = (r.Right - nx - gap * 3) / 4;
        for (int i = 0; i < nn.Length; i++)
        {
            int k = i;
            Rectangle cr = new Rectangle(nx + i * (nw + gap), ny, nw, Px(28));
            if (analog)
                Chip(g, cr, nn[i], ClockData.AnalogNumbers == i, delegate { ClockData.SetAnalogNumbers(k); previews = null; Invalidate(); });
            else
            {
                // неактивні: лише показують вибране, натиснути не можна
                bool on = ClockData.AnalogNumbers == i;
                using (SolidBrush b = new SolidBrush(on ? Util.Blend(P.Bg, P.Acc, 0.16f) : Util.Blend(P.Bg, P.Card, 0.5f)))
                    Util.FillRound(g, b, cr, Math.Min(P.Rad, cr.Height / 2));
                TxtC(g, nn[i], FS, cr, Util.Blend(P.Bg, P.Sub, 0.6f));
            }
        }
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        switch (Tab)
        {
            case 0: PaintTimeCal(g, r); break;
            case 1: PaintStopwatch(g, r); break;
            case 2: PaintTimer(g, r); break;
            default: PaintAlarms(g, r); break;
        }
    }

    // ---------- Час і календар ----------
    void PaintTimeCal(Graphics g, Rectangle r)
    {
        DateTime now = DateTime.Now;
        Cfg c = Bar.Config;
        string tm = c.Clock24 ? now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : now.ToString("h:mm:ss tt", CultureInfo.InvariantCulture);   // у розширеній панелі секунди завжди
        TxtC(g, tm, FHuge, new Rectangle(r.X, r.Y - Px(4), r.Width, Px(62)), P.Fg);
        string dl = now.ToString("dddd, d MMMM yyyy", Widgets.UK);
        dl = char.ToUpper(dl[0]) + dl.Substring(1) + L.T(" · тиждень ") + Widgets.Week(now);
        TxtC(g, dl, F, new Rectangle(r.X, r.Y + Px(56), r.Width, Px(22)), P.Sub);
        int off = Px(88), gh = Px(178);
        PaintCalendar(g, new Rectangle(r.X, r.Y + off, r.Width, r.Height - off - gh - Px(10)));
        PaintDeskGallery(g, new Rectangle(r.X, r.Bottom - gh, r.Width, gh));
    }

    // ---------- Секундомір ----------
    void PaintStopwatch(Graphics g, Rectangle r)
    {
        TimeSpan el = ClockData.SwElapsed;
        TxtC(g, ClockData.FmtMs(el), FHuge, new Rectangle(r.X, r.Y + Px(6), r.Width, Px(70)), ClockData.SwRunning ? P.Acc : P.Fg);
        int bs = Px(52), gap = Px(22);
        int bx = r.X + (r.Width - bs * 3 - gap * 2) / 2, by = r.Y + Px(88);
        IconBtn(g, new Rectangle(bx, by, bs, bs), "reset", delegate { ClockData.SwReset(); }, false);
        IconBtn(g, new Rectangle(bx + bs + gap, by, bs, bs), ClockData.SwRunning ? "pause" : "play", delegate { ClockData.SwStartPause(); }, true);
        // коло - лише коли секундомір іде
        IconBtn(g, new Rectangle(bx + (bs + gap) * 2, by, bs, bs), "flag", ClockData.SwRunning ? (Action)delegate { ClockData.SwLap(); } : null, false);

        Rectangle lr = new Rectangle(r.X, by + bs + Px(18), r.Width, r.Bottom - (by + bs + Px(18)));
        Card(g, lr);
        Rectangle li = Rectangle.Inflate(lr, -Px(14), -Px(8));
        if (ClockData.Laps.Count == 0) { TxtC(g, L.T("Кола з'являться тут (кнопка з прапорцем)"), F, li, P.Sub); return; }
        int rowH = Px(26);
        int vis = li.Height / rowH;
        lapScroll = Math.Max(0, Math.Min(lapScroll, ClockData.Laps.Count - vis));
        TimeSpan best = TimeSpan.MaxValue, worst = TimeSpan.Zero;
        int n = ClockData.Laps.Count;
        for (int i = 0; i < n; i++)
        {
            TimeSpan d = ClockData.Laps[i] - (i + 1 < n ? ClockData.Laps[i + 1] : TimeSpan.Zero);
            if (d < best) best = d;
            if (d > worst) worst = d;
        }
        for (int i = lapScroll; i < n && (i - lapScroll) < vis; i++)
        {
            int yy = li.Y + (i - lapScroll) * rowH;
            TimeSpan tot = ClockData.Laps[i], d = tot - (i + 1 < n ? ClockData.Laps[i + 1] : TimeSpan.Zero);
            Color dc = n > 2 && d == best ? P.Good : (n > 2 && d == worst ? P.Bad : P.Fg);
            TxtL(g, L.T("Коло ") + (n - i), F, new Rectangle(li.X, yy, Px(100), rowH), P.Sub);
            TxtL(g, ClockData.FmtMs(d), FB, new Rectangle(li.X + Px(100), yy, Px(150), rowH), dc);
            TxtR(g, ClockData.FmtMs(tot), F, new Rectangle(li.X, yy, li.Width, rowH), P.Sub);
        }
        Wheel(lr, delegate (int d) { lapScroll -= d; });
    }

    // ---------- Таймер ----------
    void SelectTimer(int i)
    {
        ClockData.SelTimer = Math.Max(0, Math.Min(i, ClockData.Timers.Count - 1));
        syncingTimer = true;
        tbTimer.Text = ClockData.CurTimer.Label;
        syncingTimer = false;
        Invalidate();
    }

    void PaintTimer(Graphics g, Rectangle r0)
    {
        ClockData.EnsureTimers();
        TimerItem t = ClockData.CurTimer;
        // список таймерів: по два в рядку, останній - «+»
        int n = ClockData.Timers.Count, cols = 2, chH = Px(34), chG = Px(6);
        int slots = n + (n < ClockData.MaxTimers ? 1 : 0);
        int rowsN = (slots + cols - 1) / cols;
        int chW = (r0.Width - chG * (cols - 1)) / cols;
        for (int i = 0; i < slots; i++)
        {
            Rectangle cr = new Rectangle(r0.X + (i % cols) * (chW + chG), r0.Y + (i / cols) * (chH + chG), chW, chH);
            if (i == n)
            {
                Chip(g, cr, L.T("+ Новий таймер"), false, delegate { AddTimerUi(); });
                continue;
            }
            TimerItem ti = ClockData.Timers[i];
            int k = i;
            bool sel = i == ClockData.SelTimer;
            Chip(g, cr, "", sel, delegate { SelectTimer(k); });
            string tt = ClockData.Fmt(ti.Left, false);
            Color tc = ti.Running ? P.Acc : (sel ? P.Fg : P.Sub);
            TxtR(g, tt, FB, new Rectangle(cr.X, cr.Y, cr.Width - Px(10), cr.Height), tc);
            TxtL(g, ClockData.TimerName(ti), F, new Rectangle(cr.X + Px(10), cr.Y, cr.Width - Px(20) - TextRenderer.MeasureText(tt, FB).Width, cr.Height), sel ? P.Fg : P.Sub);
        }
        int lh = rowsN * (chH + chG) + Px(6);
        Rectangle r = new Rectangle(r0.X, r0.Y + lh, r0.Width, r0.Height - lh);

        bool run = t.Running, paused = t.IsPaused;
        TimeSpan left = t.Left;
        Rectangle tr = new Rectangle(r.X, r.Y + Px(4), r.Width, Px(70));
        if (!run && !paused)
        {
            // редагування годин/хвилин/секунд коліщатком або кнопками
            TimeSpan s = t.Set;
            int[] v = { (int)s.TotalHours, s.Minutes, s.Seconds };
            string[] cap = { L.T("год"), L.T("хв"), L.T("с") };
            int cw = Px(96), gap = Px(18);
            int x0 = r.X + (r.Width - cw * 3 - gap * 2) / 2;
            for (int i = 0; i < 3; i++)
            {
                Rectangle cr = new Rectangle(x0 + i * (cw + gap), tr.Y, cw, Px(70));
                Card(g, cr, Over(cr) ? Util.Blend(P.Card, P.Fg, 0.08f) : P.Card);
                TxtC(g, v[i].ToString("00"), FBig, new Rectangle(cr.X, cr.Y + Px(4), cr.Width, Px(44)), P.Fg);
                TxtC(g, cap[i], FS, new Rectangle(cr.X, cr.Y + Px(46), cr.Width, Px(18)), P.Sub);
                int k = i;
                Wheel(cr, delegate (int d) { Adjust(k, d); });
                IconBtn(g, new Rectangle(cr.X + Px(6), cr.Bottom + Px(4), Px(28), Px(24)), "minus", delegate { Adjust(k, -1); }, false);
                IconBtn(g, new Rectangle(cr.Right - Px(34), cr.Bottom + Px(4), Px(28), Px(24)), "plus", delegate { Adjust(k, 1); }, false);
            }
            TxtC(g, L.T("Коліщатко миші над числом змінює значення"), FS, new Rectangle(r.X, tr.Bottom + Px(30), r.Width, Px(18)), P.Sub);
        }
        else
        {
            TxtC(g, ClockData.Fmt(left, false), FHuge, tr, run ? P.Acc : P.Fg);
            float frac = t.Set.TotalSeconds > 0 ? (float)(1 - left.TotalSeconds / t.Set.TotalSeconds) : 0;
            ProgressBar(g, new Rectangle(r.X + Px(30), tr.Bottom + Px(8), r.Width - Px(60), Px(8)), frac, P.Acc);
            TxtC(g, run ? L.T("Сигнал о ") + t.End.ToLocalTime().ToString("HH:mm:ss") : L.T("Пауза"), FS, new Rectangle(r.X, tr.Bottom + Px(24), r.Width, Px(20)), P.Sub);
        }
        int y = tr.Bottom + Px(54);
        // швидкий вибір
        int[] mins = { 1, 3, 5, 10, 15, 25, 30, 60 };
        int chw = (r.Width - Px(6) * 3) / 4, chh = Px(30);
        for (int i = 0; i < mins.Length; i++)
        {
            Rectangle cr = new Rectangle(r.X + (i % 4) * (chw + Px(6)), y + (i / 4) * (chh + Px(6)), chw, chh);
            int m = mins[i];
            Chip(g, cr, m == 60 ? L.T("1 год") : m + L.T(" хв"), !run && !paused && t.Set == TimeSpan.FromMinutes(m), delegate
            {
                ClockData.TimerReset(t);
                t.Set = TimeSpan.FromMinutes(m);
                ClockData.Save();
            });
        }
        y += (chh + Px(6)) * 2 + Px(10);
        TxtL(g, L.T("Назва"), FS, new Rectangle(r.X, y, r.Width, Px(18)), P.Sub);
        PlaceInput(g, tbTimer, new Rectangle(r.X, y + Px(20), r.Width, Px(32)));
        SoundRow(g, new Rectangle(r.X, y + Px(60), r.Width, Px(32)), t.Sound, delegate (string v) { t.Sound = v; }, t.Volume);
        TxtL(g, L.T("Гучність"), F, new Rectangle(r.X + Px(2), y + Px(98), Px(80), Px(30)), P.Fg);
        Slider(g, new Rectangle(r.X + Px(84), y + Px(98), r.Width - Px(84) - Px(56), Px(30)), t.Volume, delegate (int vv) { t.Volume = vv; });
        TxtR(g, t.Volume + "%", FB, new Rectangle(r.Right - Px(50), y + Px(98), Px(50), Px(30)), P.Fg);
        int bs = Px(56), gap2 = Px(26);
        int by = Math.Min(r.Bottom - bs, y + Px(140));
        int bx = r.X + (r.Width - bs * 2 - gap2) / 2;
        IconBtn(g, new Rectangle(bx, by, bs, bs), "reset", delegate { ClockData.TimerReset(t); }, false);
        IconBtn(g, new Rectangle(bx + bs + gap2, by, bs, bs), run ? "pause" : "play", delegate { ClockData.TimerStartPause(t); }, true);
        if (ClockData.Timers.Count > 1)
            Btn(g, new Rectangle(r.X, by + (bs - Px(32)) / 2, Px(120), Px(32)), L.T("Видалити"), delegate
            {
                ClockData.RemoveTimer(t);
                SelectTimer(ClockData.SelTimer);
            }, 0, true);
    }

    void AddTimerUi()
    {
        if (ClockData.AddTimer() != null) SelectTimer(ClockData.SelTimer);
    }

    // рядок вибору мелодії: назва (меню зі списком) і кнопка прослуховування
    int soundVol = 100;      // гучність для пробного програвання

    void SoundRow(Graphics g, Rectangle r, string cur, Action<string> set, int vol)
    {
        soundVol = vol;
        TxtL(g, L.T("Мелодія"), F, new Rectangle(r.X, r.Y, Px(80), r.Height), P.Fg);
        Rectangle nr = new Rectangle(r.X + Px(84), r.Y, r.Width - Px(84) - Px(44), r.Height);
        Btn(g, nr, Chime.Name(cur) + "  ▾", delegate { SoundMenu(nr, cur, set); }, 0, true);
        Rectangle pr = new Rectangle(r.Right - Px(36), r.Y, Px(36), r.Height);
        bool playing = Chime.Playing && preview == cur;
        IconBtn(g, pr, playing ? "stop" : "play", delegate
        {
            if (Chime.Playing && preview == cur) { Chime.Stop(); preview = null; }
            else { Chime.Play(cur, false, vol); preview = cur; }
        }, false);
    }

    string preview;

    void SoundMenu(Rectangle at, string cur, Action<string> set)
    {
        ContextMenuStrip m = ThemedMenu();
        foreach (string[] b in Chime.BuiltIn)
        {
            string id = b[0];
            ToolStripMenuItem it = new ToolStripMenuItem(b[1]);
            it.Checked = cur == id;
            it.Click += delegate { set(id); ClockData.Save(); Chime.Play(id, false, soundVol); preview = id; Invalidate(); };
            m.Items.Add(it);
        }
        List<string> files = Chime.Files();
        if (files.Count > 0) m.Items.Add(new ToolStripSeparator());
        foreach (string f in files)
        {
            string id = "file:" + f;
            ToolStripMenuItem it = new ToolStripMenuItem(Path.GetFileNameWithoutExtension(f));
            it.Checked = cur == id;
            it.Click += delegate { set(id); ClockData.Save(); Chime.Play(id, false, soundVol); preview = id; Invalidate(); };
            m.Items.Add(it);
        }
        if (files.Count > 0)
        {
            ToolStripMenuItem del = new ToolStripMenuItem(L.T("Видалити свою мелодію"));
            foreach (string f in files)
            {
                string fn = f;
                del.DropDownItems.Add(Path.GetFileNameWithoutExtension(f), null, delegate
                {
                    if (Ask("NeoBar", L.T("Видалити мелодію «") + Path.GetFileNameWithoutExtension(fn) + L.Q("»?"), L.T("Видалити"), L.T("Скасувати")) != 0) return;
                    Chime.Stop();
                    try { File.Delete(Path.Combine(Chime.Dir, fn)); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                    foreach (Alarm al in ClockData.Alarms) if (al.Sound == "file:" + fn) al.Sound = "theme";
                    foreach (TimerItem ti in ClockData.Timers) if (ti.Sound == "file:" + fn) ti.Sound = "theme";
                    if (edit != null && edit.Sound == "file:" + fn) edit.Sound = "theme";
                    ClockData.Save(); Invalidate();
                });
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(del);
        }
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(L.T("Завантажити свою мелодію…"), null, delegate
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = L.T("Мелодія для сигналу");
                d.Filter = L.T("Звук (*.mp3;*.wav;*.wma)|*.mp3;*.wav;*.wma|Усі файли|*.*");
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string id = Chime.Import(d.FileName);
                    set(id); ClockData.Save(); Invalidate();
                }
                catch (Exception ex) { Ask("NeoBar", L.T("Не вдалося додати мелодію:\n") + ex.Message); }
            }
        });
        m.Items.Add(L.T("Відкрити папку мелодій"), null, delegate
        {
            try { using (System.Diagnostics.Process.Start("explorer.exe", "\"" + Chime.Dir + "\"")) { } } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        });
        m.Show(this, new Point(at.X, at.Bottom));
    }

    void Adjust(int part, int d)
    {
        TimerItem ti = ClockData.CurTimer;
        TimeSpan s = ti.Set;
        int h = (int)s.TotalHours, m = s.Minutes, sec = s.Seconds;
        if (part == 0) h = (h + d + 24) % 24;
        else if (part == 1) m = (m + d + 60) % 60;
        else sec = (sec + (d * 5) + 60) % 60;
        ti.Set = new TimeSpan(h, m, sec);
        ClockData.Save();
        Invalidate();
    }

    // ---------- Будильники ----------
    void PaintAlarms(Graphics g, Rectangle r)
    {
        if (edit != null) { PaintEditor(g, r); return; }
        tbAlarm.Visible = false;
        int y = r.Y;
        int rowH = Px(64);
        DateTime now = DateTime.Now;
        if (ClockData.Alarms.Count == 0)
            TxtC(g, L.T("Будильників ще немає"), F, new Rectangle(r.X, y + Px(30), r.Width, Px(30)), P.Sub);
        foreach (Alarm a in ClockData.Alarms)
        {
            if (y + rowH > r.Bottom - Px(48)) break;
            Alarm al = a;
            Rectangle cr = new Rectangle(r.X, y, r.Width, rowH - Px(8));
            Card(g, cr, Over(cr) ? Util.Blend(P.Card, P.Fg, 0.05f) : P.Card);
            Color tc = a.On || a.Snooze != DateTime.MinValue ? P.Fg : P.Sub;
            TxtL(g, a.H.ToString("00") + ":" + a.M.ToString("00"), FBig, new Rectangle(cr.X + Px(14), cr.Y, Px(110), cr.Height), tc);
            bool snoozed = a.Snooze != DateTime.MinValue;
            string sub = a.DaysText + (a.Label.Length > 0 ? " · " + a.Label : "");
            string sub2 = "";
            if (snoozed)
            {
                TimeSpan sd = a.Snooze - DateTime.UtcNow;
                int sm = Math.Max(0, (int)Math.Ceiling(sd.TotalMinutes));
                sub2 = L.T("Відкладено до ") + a.Snooze.ToLocalTime().ToString("HH:mm") + L.T(" · ще ") + sm + L.T(" хв");
            }
            else if (a.On)
            {
                DateTime nx = a.Next(now);
                if (nx != DateTime.MaxValue)
                {
                    TimeSpan d = nx - now;
                    sub += L.T(" · через ") + (d.TotalHours >= 1 ? (int)d.TotalHours + L.T(" год ") : "") + d.Minutes + L.T(" хв");
                }
            }
            Rectangle sr = new Rectangle(cr.X + Px(124), cr.Y, cr.Width - Px(124) - Px(110), cr.Height);
            if (snoozed)
            {
                TxtL(g, sub, FS, new Rectangle(sr.X, sr.Y + Px(6), sr.Width, sr.Height / 2 - Px(6)), P.Sub);
                TxtL(g, sub2, FS, new Rectangle(sr.X, sr.Y + sr.Height / 2, sr.Width, sr.Height / 2 - Px(6)), P.Acc);
            }
            else TxtL(g, sub, FS, sr, P.Sub);

            Hot(new Rectangle(cr.X, cr.Y, cr.Width - Px(100), cr.Height), delegate { Edit(al, false); });
            Switch(g, new Rectangle(cr.Right - Px(96), cr.Y + (cr.Height - Px(28)) / 2, Px(44), Px(28)), "", a.On || snoozed, delegate
            {
                al.On = !(al.On || al.Snooze != DateTime.MinValue); al.Snooze = DateTime.MinValue; ClockData.Save();
            });
            IconBtn(g, new Rectangle(cr.Right - Px(42), cr.Y + (cr.Height - Px(30)) / 2, Px(30), Px(30)), "trash", delegate
            {
                ClockData.Alarms.Remove(al); ClockData.Save();
            }, false);
            y += rowH;
        }
        Btn(g, new Rectangle(r.X, r.Bottom - Px(36), Px(200), Px(34)), L.T("+ Додати будильник"), delegate
        {
            Alarm a = new Alarm();
            a.SnoozeMin = ClockData.SnoozeMin;
            DateTime n = DateTime.Now.AddHours(1);
            a.H = n.Hour; a.M = 0;
            List<string> names = new List<string>();
            foreach (Alarm x in ClockData.Alarms) names.Add(x.Label);
            a.Label = ClockData.NextName(L.T("Будильник"), names);
            Edit(a, true);
        }, 1, true);
        TxtR(g, L.T("Працює, поки запущено NeoBar"), FS, new Rectangle(r.X + Px(210), r.Bottom - Px(36), r.Width - Px(210), Px(34)), P.Sub);
    }

    void Edit(Alarm a, bool isNew)
    {
        editOrig = isNew ? null : a;
        edit = a.Clone();
        tbAlarm.Text = a.Label;
        Invalidate();
    }

    void PaintEditor(Graphics g, Rectangle r)
    {
        Alarm a = edit;
        int cw = Px(110), gap = Px(20);
        int x0 = r.X + (r.Width - cw * 2 - gap) / 2;
        int[] v = { a.H, a.M };
        string[] cap = { L.T("години"), L.T("хвилини") };
        for (int i = 0; i < 2; i++)
        {
            Rectangle cr = new Rectangle(x0 + i * (cw + gap), r.Y, cw, Px(84));
            Card(g, cr, Over(cr) ? Util.Blend(P.Card, P.Fg, 0.08f) : P.Card);
            TxtC(g, v[i].ToString("00"), FHuge, new Rectangle(cr.X, cr.Y + Px(2), cr.Width, Px(60)), P.Fg);
            TxtC(g, cap[i], FS, new Rectangle(cr.X, cr.Y + Px(60), cr.Width, Px(18)), P.Sub);
            int k = i;
            Wheel(cr, delegate (int d)
            {
                if (k == 0) a.H = (a.H + d + 24) % 24;
                else a.M = (a.M + d + 60) % 60;
            });
            IconBtn(g, new Rectangle(cr.X - Px(2), cr.Bottom + Px(4), Px(28), Px(26)), "minus", delegate
            {
                if (k == 0) a.H = (a.H + 23) % 24; else a.M = (a.M + 59) % 60;
            }, false);
            IconBtn(g, new Rectangle(cr.Right - Px(26), cr.Bottom + Px(4), Px(28), Px(26)), "plus", delegate
            {
                if (k == 0) a.H = (a.H + 1) % 24; else a.M = (a.M + 1) % 60;
            }, false);
        }
        int y = r.Y + Px(124);
        TxtL(g, L.T("Повторювати"), FS, new Rectangle(r.X, y, r.Width, Px(18)), P.Sub);
        string[] presets = { L.T("Один раз"), L.T("Щодня"), L.T("Будні"), L.T("Вихідні"), L.T("Щомісяця") };
        int[] pv = { 0, 0x7F, 0x1F, 0x60, -1 };
        int pw = (r.Width - Px(6) * 4) / 5;
        for (int i = 0; i < 5; i++)
        {
            int k = pv[i];
            bool on = k < 0 ? a.MonthDay > 0 : (a.MonthDay == 0 && a.Days == k);
            Chip(g, new Rectangle(r.X + i * (pw + Px(6)), y + Px(20), pw, Px(28)), presets[i], on, delegate
            {
                if (k < 0) { if (a.MonthDay == 0) a.MonthDay = DateTime.Today.Day; a.Days = 0; }
                else { a.MonthDay = 0; a.Days = k; }
            });
        }
        y += Px(56);
        if (a.MonthDay > 0)
        {
            // число місяця
            TxtL(g, L.T("Число місяця"), F, new Rectangle(r.X, y, Px(130), Px(30)), P.Fg);
            Rectangle dr = new Rectangle(r.X + Px(170), y, Px(80), Px(30));
            Card(g, dr);
            TxtC(g, a.MonthDay.ToString(), FB, dr, P.Fg);
            Wheel(dr, delegate (int d) { a.MonthDay = (a.MonthDay - 1 + d + 31) % 31 + 1; });
            IconBtn(g, new Rectangle(r.X + Px(134), y, Px(30), Px(30)), "minus", delegate { a.MonthDay = (a.MonthDay + 29) % 31 + 1; }, false);
            IconBtn(g, new Rectangle(dr.Right + Px(4), y, Px(30), Px(30)), "plus", delegate { a.MonthDay = a.MonthDay % 31 + 1; }, false);
            if (a.MonthDay > 28) TxtL(g, L.T("у коротших місяцях - в останній день"), FS, new Rectangle(dr.Right + Px(42), y, r.Right - dr.Right - Px(42), Px(30)), P.Sub);
        }
        else
        {
            int dw = (r.Width - Px(6) * 6) / 7;
            for (int i = 0; i < 7; i++)
            {
                int bit = 1 << i;
                Chip(g, new Rectangle(r.X + i * (dw + Px(6)), y, dw, Px(30)), Alarm.DayNames[i], (a.Days & bit) != 0, delegate { a.Days ^= bit; });
            }
        }
        y += Px(44);
        TxtL(g, L.T("Назва"), FS, new Rectangle(r.X, y, r.Width, Px(18)), P.Sub);
        PlaceInput(g, tbAlarm, new Rectangle(r.X, y + Px(20), r.Width, Px(32)));
        SoundRow(g, new Rectangle(r.X, y + Px(62), r.Width, Px(32)), a.Sound, delegate (string sv) { a.Sound = sv; }, a.Volume);
        TxtL(g, L.T("Гучність"), F, new Rectangle(r.X + Px(2), y + Px(102), Px(80), Px(30)), P.Fg);
        Slider(g, new Rectangle(r.X + Px(84), y + Px(102), r.Width - Px(84) - Px(56), Px(30)), a.Volume, delegate (int vv) { a.Volume = vv; });
        TxtR(g, a.Volume + "%", FB, new Rectangle(r.Right - Px(50), y + Px(102), Px(50), Px(30)), P.Fg);
        int sy = y + Px(142);
        TxtL(g, L.T("Відкладати на"), F, new Rectangle(r.X + Px(2), sy, Px(120), Px(30)), P.Fg);
        IconBtn(g, new Rectangle(r.X + Px(126), sy, Px(30), Px(30)), "minus", delegate { a.SnoozeMin = Math.Max(1, a.SnoozeMin - (a.SnoozeMin > 10 ? 5 : 1)); }, false);
        TxtC(g, a.SnoozeMin + L.T(" хв"), FB, new Rectangle(r.X + Px(158), sy, Px(64), Px(30)), P.Fg);
        IconBtn(g, new Rectangle(r.X + Px(224), sy, Px(30), Px(30)), "plus", delegate { a.SnoozeMin = Math.Min(60, a.SnoozeMin + (a.SnoozeMin >= 10 ? 5 : 1)); }, false);
        int by = r.Bottom - Px(36);
        Btn(g, new Rectangle(r.Right - Px(150), by, Px(150), Px(34)), L.T("Зберегти"), delegate
        {
            a.Label = tbAlarm.Text.Trim();
            a.On = true; a.Snooze = DateTime.MinValue; a.Fired = DateTime.MinValue;
            int at = editOrig != null ? ClockData.Alarms.IndexOf(editOrig) : -1;
            if (at >= 0) ClockData.Alarms[at] = a; else ClockData.Alarms.Add(a);
            ClockData.Alarms.Sort(delegate (Alarm x, Alarm z) { return (x.H * 60 + x.M).CompareTo(z.H * 60 + z.M); });
            ClockData.Save();
            Chime.Stop(); preview = null;
            edit = null; tbAlarm.Visible = false;
        }, 1, true);
        Btn(g, new Rectangle(r.Right - Px(300), by, Px(140), Px(34)), L.T("Скасувати"), delegate
        {
            Chime.Stop(); preview = null;
            edit = null; tbAlarm.Visible = false;
        }, 0, true);
    }
}
