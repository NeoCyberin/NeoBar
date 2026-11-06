using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

// ======================================================================
//  Диск: заповненість, накопичувач, SMART, перевірка, оптимізація, вилучення, літера
// ======================================================================
sealed class DiskWindow : SbWindow
{
    readonly Entry entry;
    List<Vol> vols = new List<Vol>();
    Vol cur;
    DiskInfo di;
    List<string[]> details = new List<string[]>();
    WinDiskHealth wh;
    bool whBusy;
    readonly Dictionary<string, string> smart = new Dictionary<string, string>();
    readonly List<string[]> attrs = new List<string[]>();
    string smartFile, toolFile, toolTitle = "", toolOut = "", msg = "";
    bool toolDone;
    int attrScroll;
    BenchResult bench;
    DateTime letterAt = DateTime.MinValue;
    static uint qcap;

    public DiskWindow(NeoBar bar, Entry e) : base(bar, L.T("Простір"), 560, 660)
    { Glyph = "\uEDA2";
        entry = e;
        Tabs = new string[] { L.T("Огляд"), L.T("Стан"), L.T("Швидкість"), L.T("Обслуговування") };
        Ticker.Interval = 500;
        try { if (qcap == 0) qcap = Win.RegisterWindowMessage("QueryCancelAutoPlay"); } catch { }
        NeoBar.DisksChanged += OnDevices;
    }

    // дані дисків збираються у фоні вже після появи вікна - воно відкривається одразу
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Reload(Widgets.DiskKey(entry));
    }

    void Post(Action a)
    {
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { }
    }

    // зліва направо: оновити, на робочий стіл (лише «Огляд»), закрити
    protected override void HeadButtons(Graphics g, ref int right)
    {
        Rectangle gr;
        if (Tab == 0)
        {
            gr = new Rectangle(right - Px(32), Px(7), Px(32), Px(32));
            IconBtn(g, gr, "desk", delegate { AddDiskDesk(); }, false);
            right = gr.Left - Px(4);
        }
        gr = new Rectangle(right - Px(32), Px(7), Px(32), Px(32));
        IconBtn(g, gr, "sync", loading ? null : (Action)delegate { Refresh2(); }, false);
        right = gr.Left - Px(4);
    }

    // оновлення вручну: дані плавно згасають і з'являються (як у погоди й мережі)
    readonly FadeAnim fade = new FadeAnim();
    System.Windows.Forms.Timer fadeTimer;

    void Refresh2()
    {
        fade.Start();
        if (fadeTimer == null)
        {
            fadeTimer = new System.Windows.Forms.Timer();
            fadeTimer.Interval = 33;
            fadeTimer.Tick += delegate { if (IsDisposed) { fadeTimer.Stop(); return; } Invalidate(); if (!fade.Active) fadeTimer.Stop(); };
        }
        fadeTimer.Start();
        Reload(cur != null ? cur.Id : "");
    }

    // диски, позначені повзунком «На столі»: ними заповнюється віджет, коли натиснути кнопку з дисплеєм
    static readonly HashSet<string> deskSel = new HashSet<string>();

    // літери наявних дисків, позначені «На столі»
    int DeskSelCount()
    {
        int n = 0;
        foreach (Vol v in vols) if (v.Letter.Length > 0 && deskSel.Contains(v.Letter.ToUpperInvariant())) n++;
        return n;
    }

    // хоч один диск завжди позначений: якщо нічого немає - диск C (або перший з літерою)
    void EnsureDeskSel()
    {
        if (DeskSelCount() > 0) return;
        string first = "";
        foreach (Vol v in vols)
        {
            if (v.Letter.Length == 0) continue;
            string l = v.Letter.ToUpperInvariant();
            if (l.StartsWith("C")) { first = l; break; }
            if (first.Length == 0) first = l;
        }
        if (first.Length > 0) deskSel.Add(first);
    }

    void AddDiskDesk()
    {
        EnsureDeskSel();
        List<string> pick = new List<string>();
        int lettered = 0;
        foreach (Vol v in vols)
        {
            if (v.Letter.Length == 0) continue;
            lettered++;
            if (deskSel.Contains(v.Letter.ToUpperInvariant())) pick.Add(v.Letter.ToUpperInvariant());
        }
        if (pick.Count == 0 && cur != null && cur.Letter.Length > 0) pick.Add(cur.Letter.ToUpperInvariant());   // нічого не позначено - поточний диск
        ClockData.AddDesk("disk", pick.Count >= lettered ? "" : string.Join(",", pick.ToArray()));
    }

    // флешка чи диск - для піктограм і кнопки «Показати на панелі»
    Dictionary<string, bool> flash = new Dictionary<string, bool>();
    bool ejectOk;

    bool IsFlashVol(Vol v)
    {
        bool f;
        return v != null && flash.TryGetValue(v.Id, out f) && f;
    }

    void OnDevices() { if (!IsDisposed) Reload(cur != null ? cur.Id : ""); }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        NeoBar.DisksChanged -= OnDevices;
        base.OnFormClosed(e);
    }

    // після зміни літери Windows не відкриває вікно диска (автозапуск скасовується, поки це вікно активне)
    protected override void WndProc(ref Message m)
    {
        if (qcap != 0 && m.Msg == (int)qcap && (DateTime.Now - letterAt).TotalSeconds < 60) { m.Result = (IntPtr)1; return; }
        base.WndProc(ref m);
    }

    int loadGen, pickGen;
    bool loading;

    // Перелік томів, вибраний том і його накопичувач - у фоновому потоці (сплячий HDD чи мережевий диск
    // можуть відповідати секундами, а пошук безпечного вилучення перебирає всі пристрої)
    void Reload(string id)
    {
        int gen = ++loadGen, pg = pickGen;
        loading = true;
        Util.Bg(delegate
        {
            List<Vol> nv = new List<Vol>();
            Dictionary<string, bool> nf = new Dictionary<string, bool>();
            Dictionary<int, DiskInfo> dis = new Dictionary<int, DiskInfo>();
            Vol found = null;
            DiskInfo nd = null;
            bool ej = false;
            List<string[]> det = new List<string[]>();
            try
            {
                nv = Vols.List();
                foreach (Vol v in nv)
                {
                    DiskInfo d = null;
                    if (v.DiskNo >= 0 && !dis.TryGetValue(v.DiskNo, out d)) { d = Vols.Disk(v.DiskNo); dis[v.DiskNo] = d; }
                    nf[v.Id] = Vols.IsFlash(v, d);
                }
                foreach (Vol v in nv)
                    if (string.Equals(v.Guid, id, StringComparison.OrdinalIgnoreCase) || string.Equals(v.Root, id, StringComparison.OrdinalIgnoreCase)) found = v;
                if (found == null && nv.Count > 0)
                {
                    string sys = Vols.SysRoot();
                    foreach (Vol v in nv) if (string.Equals(v.Root, sys, StringComparison.OrdinalIgnoreCase)) found = v;
                    if (found == null) found = nv[0];
                }
                if (found != null && IsOptical(found)) { nd = Vols.Optical(found.Root); det = OpticalDetails(found, nd); }
                else if (found != null)
                {
                    if (!dis.TryGetValue(found.DiskNo, out nd) || nd == null) nd = Vols.Disk(found.DiskNo);
                    ej = Vols.CanEject(found.DiskNo);
                    det = DiskDetails.Get(found, nd);
                }
            }
            catch { }
            Post(delegate
            {
                if (gen != loadGen) return;
                loading = false;
                fade.End();
                vols = nv;
                flash = nf;
                if (pg != pickGen && cur != null)
                {
                    // поки читали, користувач вибрав інший диск - лишаємо його
                    foreach (Vol v in nv) if (v.Id == cur.Id) cur = v;
                    Invalidate();
                    return;
                }
                bool same = cur != null && found != null && cur.Id == found.Id;
                cur = found;
                di = nd;
                ejectOk = ej;
                details = det;
                if (!same) { ResetVol(); StartHealth(); }
                Invalidate();
            });
        });
        Invalidate();
    }

    static bool IsOptical(Vol v) { return v != null && v.DriveType == 5; }

    // оптичний привід: що в ньому зараз (інше - стан, швидкість, обслуговування - для нього не має сенсу)
    static List<string[]> OpticalDetails(Vol v, DiskInfo d)
    {
        List<string[]> l = new List<string[]>();
        if (d != null && d.Rev.Length > 0) l.Add(new[] { L.T("Прошивка"), d.Rev });
        l.Add(new[] { L.T("Диск у приводі"), v.Total > 0 ? L.T("є") : L.T("немає") });
        if (v.Total > 0)
        {
            if (v.Label.Length > 0) l.Add(new[] { L.T("Назва диска"), v.Label });
            if (v.Fs.Length > 0) l.Add(new[] { L.T("Файлова система"), v.Fs });
            l.Add(new[] { L.T("Обсяг даних"), Vols.Size(v.Total - v.Free) });
        }
        return l;
    }

    void ResetVol()
    {
        smart.Clear(); attrs.Clear(); msg = "";
        bench = null; wh = null;
    }

    void Pick(Vol v)
    {
        cur = v;
        ResetVol();
        di = null; ejectOk = false; details = new List<string[]>();
        int pg = ++pickGen;
        if (v != null)
        {
            Util.Bg(delegate
            {
                DiskInfo nd = null;
                bool ej = false;
                List<string[]> det = new List<string[]>();
                try
                {
                    if (IsOptical(v)) { nd = Vols.Optical(v.Root); det = OpticalDetails(v, nd); }
                    else
                    {
                        nd = Vols.Disk(v.DiskNo);
                        ej = Vols.CanEject(v.DiskNo);
                        det = DiskDetails.Get(v, nd);
                    }
                }
                catch { }
                Post(delegate
                {
                    if (pg != pickGen || cur != v) return;
                    di = nd; ejectOk = ej; details = det;
                    Invalidate();
                });
            });
        }
        StartHealth();
        Invalidate();
    }

    void StartHealth()
    {
        Vol v = cur;
        if (v == null || v.DiskNo < 0 || whBusy) return;
        whBusy = true;
        int no = v.DiskNo;
        Util.Bg(delegate
        {
            WinDiskHealth h = null;
            try { h = DiskHealth.Get(no); } catch { }
            Post(delegate
            {
                whBusy = false;
                if (cur != null && cur.DiskNo == no) wh = h;
                else StartHealth();       // поки читали, вибрали інший накопичувач
                Invalidate();
            });
        });
    }

    protected override void OnTick()
    {
        if (smartFile != null)
        {
            bool done;
            string o = Elevated.ReadOut(smartFile, out done);
            if (done)
            {
                smartFile = null;
                smart.Clear(); attrs.Clear();
                foreach (string line in o.Replace("\r", "").Split('\n'))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq), val = line.Substring(eq + 1);
                    if (k == "attr") attrs.Add(val.Split('|'));
                    else smart[k] = val;
                }
                if (smart.Count == 0) msg = L.T("Перевірку не виконано.");
            }
        }
        if (toolFile != null)
        {
            bool done;
            toolOut = Elevated.ReadOut(toolFile, out done);
            if (done) { toolFile = null; toolDone = true; if (cur != null) Reload(cur.Id); }
        }
        Invalidate();
    }

    // HDD - дефрагментація; SSD, флешки й карти пам'яті - оптимізація (TRIM)
    bool IsHdd { get { return di != null && di.SsdKnown && !di.Ssd && !di.Usb && !di.Removable && di.BusType != 17 && cur != null && cur.DriveType != 2; } }
    bool CanEject { get { return cur != null && cur.DiskNo >= 0 && (cur.DriveType == 2 || ejectOk); } }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        PaintBody0(g, r);
        float f = fade.Level;
        if (f < 0.999f)
            using (SolidBrush ov = new SolidBrush(Color.FromArgb((int)Math.Round(255 * (1f - f)), P.Bg))) g.FillRectangle(ov, r);
    }

    void PaintBody0(Graphics g, Rectangle r)
    {
        // вибір тому
        int x = r.X, y = r.Y;
        foreach (Vol v in vols)
        {
            string t = v.Letter.Length > 0 ? v.Letter : "?";
            int w = TextRenderer.MeasureText(t, FB).Width + Px(44);
            if (x + w > r.Right) break;
            Vol vv = v;
            IconChip(g, new Rectangle(x, y, w, Px(30)), IsFlashVol(v) ? "usb" : "drive", t, cur != null && v.Id == cur.Id, delegate { Pick(vv); });
            x += w + Px(6);
        }
        y += Px(40);
        if (cur == null) { TxtC(g, loading ? L.T("Читаю диски…") : L.T("Дисків не знайдено"), F, new Rectangle(r.X, y, r.Width, Px(40)), P.Sub); return; }
        Rectangle body = new Rectangle(r.X, y, r.Width, r.Bottom - y);
        if (Tab != 0 && IsOptical(cur))
        {
            Card(g, new Rectangle(body.X, body.Y, body.Width, Px(80)));
            TxtW(g, L.T("Це оптичний привід - стан, швидкість і обслуговування для нього недоступні. Що є в приводі, видно на вкладці «Огляд»."),
                F, new Rectangle(body.X + Px(14), body.Y + Px(12), body.Width - Px(28), Px(60)), P.Sub);
            return;
        }
        switch (Tab)
        {
            case 0: Overview(g, body); break;
            case 1: Health(g, body); break;
            case 2: Speed(g, body); break;
            default: Service(g, body); break;
        }
    }

    // ---------- Огляд ----------
    void Overview(Graphics g, Rectangle r)
    {
        int y = r.Y;
        Rectangle uc = new Rectangle(r.X, y, r.Width, Px(102));
        Card(g, uc);
        Rectangle ui = Rectangle.Inflate(uc, -Px(14), -Px(10));
        // повзунки: «На панелі» - лише для дисків (флешки показуються тільки тут), «На столі» - для відбору дисків віджета на столі
        bool optical = IsOptical(cur);
        bool canPanel = !optical && !IsFlashVol(cur) && Bar.HasWidget("disk");
        int swW = Px(112);
        int sx = ui.Right - swW;
        TxtL(g, cur.Title, FH, new Rectangle(ui.X, ui.Y, ui.Width - (canPanel ? swW * 2 + Px(8) : swW + Px(6)), Px(26)), P.Fg);
        if (cur.Letter.Length > 0 && !optical)
        {
            string lk = cur.Letter.ToUpperInvariant();
            EnsureDeskSel();
            bool onDesk = deskSel.Contains(lk);
            Switch(g, new Rectangle(sx, ui.Y, swW, Px(26)), L.T("На столі"), onDesk, delegate { if (!onDesk) deskSel.Add(lk); else if (DeskSelCount() > 1) deskSel.Remove(lk); Invalidate(); });
            sx -= swW + Px(4);
        }
        if (canPanel)
        {
            bool shown = Bar.DiskShown(entry, cur);
            bool last = shown && Bar.PanelDiskCount() <= 1;      // останній диск на панелі прибрати не можна
            Switch(g, new Rectangle(sx, ui.Y, swW, Px(26)), L.T("На панелі"), shown, last ? null : (Action)delegate { Bar.ToggleDisk(entry, cur); });
        }
        if (optical)
        {
            // привід: заповненість не має сенсу - лише що в ньому
            TxtL(g, cur.Total > 0 ? L.T("У приводі диск") + (cur.Label.Length > 0 ? L.Q(" «") + cur.Label + L.Q("»") : "") + " - " + Vols.Size(cur.Total - cur.Free) + L.T(" даних") : L.T("Диска в приводі немає"),
                F, new Rectangle(ui.X, ui.Y + Px(40), ui.Width, Px(22)), P.Fg);
            TxtL(g, (cur.Fs.Length > 0 ? cur.Fs + " · " : "") + L.T("оптичний привід (CD / DVD / Blu-ray) - на панель і робочий стіл не додається"), FS, new Rectangle(ui.X, ui.Y + Px(66), ui.Width, Px(18)), P.Sub);
        }
        else
        {
            int used = Math.Max(0, cur.UsedPct);
            Color bc = used >= 90 ? P.Bad : (used >= 75 ? P.Warn : P.Acc);
            ProgressBar(g, new Rectangle(ui.X, ui.Y + Px(36), ui.Width, Px(12)), used / 100f, bc);
            TxtL(g, L.T("Зайнято ") + Vols.Size(cur.Total - cur.Free) + L.T(" з ") + Vols.Size(cur.Total) + " (" + used + "%)", F, new Rectangle(ui.X, ui.Y + Px(54), ui.Width, Px(22)), P.Fg);
            TxtR(g, L.T("вільно ") + Vols.Size(cur.Free), FB, new Rectangle(ui.X, ui.Y + Px(54), ui.Width, Px(22)), P.Fg);
            TxtL(g, (cur.Fs.Length > 0 ? cur.Fs : "—") + " · " + (IsFlashVol(cur) ? L.T("флешка / карта пам'яті") : (ejectOk ? L.T("диск, можна безпечно вилучити") : L.T("диск"))) + L.T(" · ID тому ") + ShortGuid(cur.Guid), FS, new Rectangle(ui.X, ui.Y + Px(74), ui.Width, Px(18)), P.Sub);
        }
        y = uc.Bottom + Px(10);

        Rectangle dc = new Rectangle(r.X, y, r.Width, r.Bottom - y);
        Card(g, dc);
        Rectangle dI = Rectangle.Inflate(dc, -Px(14), -Px(10));
        string model = di != null && di.Model.Length > 0 ? di.Model : (optical ? L.T("Оптичний привід") : L.T("Накопичувач"));
        TxtL(g, model, FB, new Rectangle(dI.X, dI.Y, dI.Width - Px(80), Px(22)), P.Fg);
        double tc = Temp();
        if (!double.IsNaN(tc)) TxtR(g, Math.Round(tc) + " °C", FB, new Rectangle(dI.X, dI.Y, dI.Width, Px(22)), tc >= 60 ? P.Bad : (tc >= 50 ? P.Warn : P.Good));
        List<string[]> kv = new List<string[]>();
        if (di != null)
        {
            kv.Add(new[] { L.T("Тип"), di.Kind });
            if (di.Bus.Length > 0 && di.Bus != "—") kv.Add(new[] { L.T("Інтерфейс"), di.Bus });
            if (di.Size > 0) kv.Add(new[] { L.T("Ємність накопичувача"), Vols.Size(di.Size) });
            if (di.Serial.Length > 0) kv.Add(new[] { L.T("Серійний номер"), di.Serial });
        }
        kv.AddRange(details);
        int rowH = Px(21), yy = dI.Y + Px(28);
        foreach (string[] p in kv)
        {
            if (yy + rowH > dI.Bottom) break;
            TxtL(g, p[0], FS, new Rectangle(dI.X, yy, dI.Width / 2, rowH), P.Sub);
            TxtR(g, p[1], F, new Rectangle(dI.X + dI.Width / 3, yy, dI.Width * 2 / 3, rowH), P.Fg);
            yy += rowH;
        }
    }

    double Temp()
    {
        double tc = di != null ? di.TempC : double.NaN;
        string ts;
        if (smart.TryGetValue("temp", out ts)) { double t2; if (double.TryParse(ts, NumberStyles.Float, CultureInfo.InvariantCulture, out t2)) tc = t2; }
        return tc;
    }

    // ---------- Стан ----------
    void Health(Graphics g, Rectangle r)
    {
        // за даними Windows
        Rectangle wc = new Rectangle(r.X, r.Y, r.Width, Px(92));
        Card(g, wc);
        Rectangle wi = Rectangle.Inflate(wc, -Px(14), -Px(10));
        TxtL(g, L.T("Стан за даними Windows"), FB, new Rectangle(wi.X, wi.Y, wi.Width, Px(22)), P.Fg);
        if (wh == null)
            TxtL(g, whBusy ? L.T("Запит до служби сховища Windows…") : L.T("Windows не повідомила стан цього накопичувача."), F, new Rectangle(wi.X, wi.Y + Px(28), wi.Width, Px(22)), P.Sub);
        else
        {
            string hl = wh.Health.ToLowerInvariant();
            Color hc = hl == "healthy" ? P.Good : (hl == "warning" ? P.Warn : (hl == "unhealthy" ? P.Bad : P.Sub));
            Rectangle badge = new Rectangle(wi.Right - Px(150), wi.Y - Px(2), Px(150), Px(26));
            using (SolidBrush b = new SolidBrush(Util.Blend(P.Card, hc, 0.25f))) Util.FillRound(g, b, badge, badge.Height / 2);
            TxtC(g, DiskHealth.HealthText(wh.Health), FB, badge, hc);
            TxtL(g, L.T("Робота: ") + DiskHealth.OpText(wh.Operational) + (wh.Media.Length > 0 && wh.Media != "Unspecified" ? " · " + wh.Media : ""), F, new Rectangle(wi.X, wi.Y + Px(28), wi.Width, Px(22)), P.Fg);
            TxtL(g, L.T("Служба сховища Windows відстежує помилки й прогноз збою диска."), FS, new Rectangle(wi.X, wi.Y + Px(50), wi.Width, Px(20)), P.Sub);
        }

        // SMART
        int y = wc.Bottom + Px(10);
        Rectangle sc = new Rectangle(r.X, y, r.Width, r.Bottom - y);
        Card(g, sc);
        Rectangle si = Rectangle.Inflate(sc, -Px(14), -Px(10));
        TxtL(g, L.T("SMART накопичувача"), FB, new Rectangle(si.X, si.Y, si.Width, Px(22)), P.Fg);
        string health;
        if (smart.TryGetValue("health", out health))
        {
            string ht = health == "ok" ? L.T("Добрий") : (health == "warn" ? L.T("Увага") : L.T("Поганий - зробіть копію!"));
            Color hc = health == "ok" ? P.Good : (health == "warn" ? P.Warn : P.Bad);
            Rectangle badge = new Rectangle(si.Right - Px(170), si.Y - Px(2), Px(170), Px(26));
            using (SolidBrush b = new SolidBrush(Util.Blend(P.Card, hc, 0.25f))) Util.FillRound(g, b, badge, badge.Height / 2);
            TxtC(g, ht, FB, badge, hc);
        }
        int gy = si.Y + Px(30);
        int bottom = si.Bottom - Px(36);
        if (smartFile != null) TxtL(g, L.T("Читання SMART… (підтвердіть запит адміністратора)"), F, new Rectangle(si.X, gy, si.Width, Px(22)), P.Sub);
        else if (smart.Count == 0)
            TxtW(g, msg.Length > 0 ? msg : L.T("Температура, знос, години роботи, перепризначені сектори тощо. Windows віддає ці дані лише адміністратору."), FS, new Rectangle(si.X, gy, si.Width, Px(44)), P.Sub);
        else
        {
            List<string[]> kv = new List<string[]>();
            Add(kv, L.T("Температура"), "temp", " °C");
            Add(kv, L.T("Використано ресурсу"), "used", "%");
            Add(kv, L.T("Залишок ресурсу"), "life", "%");
            Add(kv, L.T("Резерв"), "spare", "%");
            Add(kv, L.T("Відпрацьовано"), "poh", L.T(" год"));
            Add(kv, L.T("Увімкнень"), "cycles", "");
            AddBytes(kv, L.T("Записано"), "written");
            AddBytes(kv, L.T("Прочитано"), "read");
            Add(kv, L.T("Перепризначені сектори"), "realloc", "");
            Add(kv, L.T("Нестабільні сектори"), "pending", "");
            Add(kv, L.T("Невиправні сектори"), "uncorrect", "");
            Add(kv, L.T("Аварійні вимкнення"), "unsafe", "");
            Add(kv, L.T("Помилки носія"), "mediaerr", "");
            string err; if (smart.TryGetValue("error", out err)) kv.Add(new[] { "", err });
            int colW = si.Width / 2, rowH = Px(20), yy = gy;
            for (int i = 0; i < kv.Count; i++)
            {
                int cx = si.X + (i % 2) * colW;
                yy = gy + (i / 2) * rowH;
                if (kv[i][0].Length == 0) { TxtW(g, kv[i][1], FS, new Rectangle(si.X, yy, si.Width, rowH * 2), P.Warn); break; }
                TxtL(g, kv[i][0], FS, new Rectangle(cx, yy, colW - Px(80), rowH), P.Sub);
                TxtR(g, kv[i][1], FB, new Rectangle(cx, yy, colW - Px(12), rowH), P.Fg);
            }
            // таблиця атрибутів ATA
            int ty = gy + ((kv.Count + 1) / 2) * rowH + Px(8);
            if (attrs.Count > 0 && ty + rowH * 2 < bottom)
            {
                Rectangle ar = new Rectangle(si.X, ty, si.Width, bottom - ty);
                TxtL(g, L.T("ID   Атрибут"), FS, new Rectangle(ar.X, ar.Y, ar.Width, rowH), P.Sub);
                TxtR(g, L.T("значення / поріг     сире"), FS, new Rectangle(ar.X, ar.Y, ar.Width, rowH), P.Sub);
                int vis = Math.Max(1, ar.Height / rowH - 1);
                attrScroll = Math.Max(0, Math.Min(attrScroll, attrs.Count - vis));
                for (int i = attrScroll; i < attrs.Count && i - attrScroll < vis; i++)
                {
                    string[] a = attrs[i];
                    if (a.Length < 6) continue;
                    int y2 = ar.Y + (i - attrScroll + 1) * rowH;
                    int cv, th;
                    int.TryParse(a[2], out cv); int.TryParse(a[4], out th);
                    Color c = th > 0 && cv <= th ? P.Bad : P.Fg;
                    TxtL(g, a[0].PadLeft(3) + "  " + a[1], FS, new Rectangle(ar.X, y2, ar.Width - Px(160), rowH), c);
                    TxtR(g, a[2] + " / " + a[4] + "     " + a[5], FS, new Rectangle(ar.X, y2, ar.Width, rowH), c);
                }
                Wheel(ar, delegate (int d) { attrScroll -= d; });
            }
        }
        Btn(g, new Rectangle(si.X, si.Bottom - Px(30), Px(200), Px(30)), smart.Count > 0 ? L.T("Перевірити ще раз") : L.T("Прочитати SMART"), delegate
        {
            string e;
            smartFile = Elevated.Run("smart", cur.DiskNo.ToString(), out e);
            if (smartFile == null) msg = e;
        }, 1, smartFile == null && cur.DiskNo >= 0);
    }

    // ---------- Швидкість ----------
    void Speed(Graphics g, Rectangle r)
    {
        Rectangle c1 = new Rectangle(r.X, r.Y, r.Width, Px(250));
        Card(g, c1);
        Rectangle ci = Rectangle.Inflate(c1, -Px(14), -Px(10));
        TxtL(g, L.T("Тест швидкості ") + cur.Letter, FB, new Rectangle(ci.X, ci.Y, ci.Width, Px(22)), P.Fg);
        TxtW(g, L.T("Записує й читає тимчасовий файл в обхід кешу Windows (") + (cur.DriveType == 2 ? "128" : "512") + L.T(" МБ), потім 5 секунд випадкових читань по 4 КБ. Файл видаляється одразу після тесту."),
            FS, new Rectangle(ci.X, ci.Y + Px(24), ci.Width, Px(34)), P.Sub);
        string[] names = { L.T("Послідовний запис"), L.T("Послідовне читання"), L.T("Випадкове читання 4 КБ") };
        double[] vals = { bench != null ? bench.WriteMBs : double.NaN, bench != null ? bench.ReadMBs : double.NaN, bench != null ? bench.Rand4kMBs : double.NaN };
        double mx = 100;
        foreach (double v in vals) if (!double.IsNaN(v)) mx = Math.Max(mx, v);
        for (int i = 0; i < 3; i++)
        {
            int yy = ci.Y + Px(66) + i * Px(44);
            TxtL(g, names[i], F, new Rectangle(ci.X, yy, ci.Width, Px(20)), P.Fg);
            string vt = double.IsNaN(vals[i]) ? "—" : vals[i].ToString(vals[i] >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture).Replace('.', ',') + L.T(" МБ/с") +
                (i == 2 && bench != null && !double.IsNaN(bench.Rand4kIops) ? " · " + Math.Round(bench.Rand4kIops) + " IOPS" : "");
            TxtR(g, vt, FB, new Rectangle(ci.X, yy, ci.Width, Px(20)), P.Fg);
            ProgressBar(g, new Rectangle(ci.X, yy + Px(22), ci.Width, Px(8)), double.IsNaN(vals[i]) ? 0f : (float)(Math.Log10(1 + vals[i]) / Math.Log10(1 + mx)), P.Acc);
        }
        bool run = bench != null && !bench.Done;
        if (run)
        {
            TxtL(g, bench.Stage + "… " + bench.Progress + "%", F, new Rectangle(ci.X, ci.Bottom - Px(28), ci.Width - Px(180), Px(28)), P.Acc);
        }
        else if (bench != null && bench.Error.Length > 0) TxtL(g, bench.Error, FS, new Rectangle(ci.X, ci.Bottom - Px(28), ci.Width - Px(180), Px(28)), P.Bad);
        Btn(g, new Rectangle(ci.Right - Px(170), ci.Bottom - Px(30), Px(170), Px(30)), bench == null ? L.T("Почати тест") : L.T("Тест ще раз"), delegate
        {
            BenchResult b = new BenchResult();
            bench = b;
            Vol v = cur;
            Util.Bg(delegate { DiskBench.Run(v, b); });
        }, 1, !run && cur.Root.Length > 0);
        if (bench != null && bench.Done && bench.Error.Length == 0)
        {
            string verdict = Verdict(bench);
            TxtW(g, verdict, FS, new Rectangle(r.X, c1.Bottom + Px(10), r.Width, Px(60)), P.Sub);
        }
    }

    string Verdict(BenchResult b)
    {
        string kind = di != null ? di.Kind : L.T("диск");
        double rd = b.ReadMBs;
        if (double.IsNaN(rd)) return "";
        if (di != null && di.BusType == 17) return rd > 1500 ? L.T("Швидкість типова для NVMe SSD.") : L.T("Для NVMe SSD читання повільне - можливо, диск у слоті з меншою кількістю ліній PCIe, перегрівається або майже заповнений.");
        if (di != null && di.Ssd && !di.Usb) return rd > 350 ? L.T("Швидкість типова для SATA SSD.") : L.T("Для SATA SSD читання повільне - перевірте кабель, режим AHCI і заповненість диска.");
        if (IsHdd) return rd > 90 ? L.T("Швидкість типова для жорсткого диска.") : L.T("Для жорсткого диска читання повільне - можлива сильна фрагментація або проблеми з поверхнею (перевірте SMART).");
        return kind + L.T(": запис ") + Math.Round(b.WriteMBs) + L.T(" МБ/с, читання ") + Math.Round(rd) + L.T(" МБ/с.");
    }

    // ---------- Обслуговування ----------
    void Service(Graphics g, Rectangle r)
    {
        int bw = (r.Width - Px(8)) / 2, bh = Px(34);
        bool busy = toolFile != null;
        int y = r.Y;
        Btn(g, new Rectangle(r.X, y, bw, bh), L.T("Перевірити файлову систему"), delegate { RunTool("chkdsk", L.T("Перевірка файлової системи"), ""); }, 0, !busy && cur.Letter.Length > 0);
        Btn(g, new Rectangle(r.X + bw + Px(8), y, bw, bh), IsHdd ? L.T("Дефрагментувати") : L.T("Оптимізувати (TRIM)"), delegate
        {
            RunTool("defrag", IsHdd ? L.T("Дефрагментація") : L.T("Оптимізація (TRIM)"), IsHdd ? "defrag" : "trim");
        }, 0, !busy && cur.Fs.Length > 0);
        y += bh + Px(8);
        Btn(g, new Rectangle(r.X, y, bw, bh), L.T("Безпечно вилучити"), delegate
        {
            string res = Vols.Eject(cur.DiskNo);
            if (res.Length == 0)
            {
                ToastForm.Show(Bar, L.T("Пристрій можна від'єднати"), cur.Title + L.T(" безпечно вилучено."), "eject", false, null);
                Bar.OnDisksChanged();
            }
            else msg = res;
        }, 0, CanEject && !busy);
        Btn(g, new Rectangle(r.X + bw + Px(8), y, bw, bh), L.T("Призначити літеру…"), delegate { LetterMenu(); }, 0, !busy && cur.Guid.Length > 0);
        y += bh + Px(8);
        bool fixedOk = cur.Letter.Length > 0 && cur.DriveType != 4 && cur.DriveType != 5;
        Btn(g, new Rectangle(r.X, y, bw, bh), L.T("Форматувати…"), delegate
        {
            string why = DiskFormat.Forbidden(cur.Letter);
            if (why.Length > 0) { msg = why; Invalidate(); return; }
            new FormatWindow(Bar, cur, di).Show();
        }, 0, !busy && fixedOk);
        y += bh + Px(10);
        TxtW(g, (IsHdd ? L.T("Жорсткий диск: оптимізація = дефрагментація.") : L.T("SSD, флешки й карти пам'яті не дефрагментуються - для них виконується TRIM (повідомлення накопичувачу про звільнені блоки).")) +
            L.T(" Перевірка, оптимізація й зміна літери потребують прав адміністратора."), FS, new Rectangle(r.X, y, r.Width, Px(36)), P.Sub);
        y += Px(44);
        if (toolOut.Length > 0 || busy || msg.Length > 0)
        {
            Rectangle oc = new Rectangle(r.X, y, r.Width, r.Bottom - y);
            Card(g, oc);
            Rectangle oi = Rectangle.Inflate(oc, -Px(12), -Px(8));
            if (toolTitle.Length > 0 && (busy || toolOut.Length > 0))
                TxtL(g, toolTitle + (busy ? L.T(" - виконується…") : (toolDone ? L.T(" - завершено") : "")), FB, new Rectangle(oi.X, oi.Y, oi.Width, Px(20)), busy ? P.Acc : P.Fg);
            string text = msg.Length > 0 && !busy ? msg : Tail(toolOut, Math.Max(1, (oi.Height - Px(22)) / Px(16)));
            TxtW(g, text, FS, new Rectangle(oi.X, oi.Y + Px(22), oi.Width, oi.Height - Px(22)), P.Sub);
        }
    }

    static string ShortGuid(string g)
    {
        int a = g.IndexOf('{'), b = g.IndexOf('}');
        return a >= 0 && b > a ? g.Substring(a + 1, Math.Min(8, b - a - 1)) + "…" : "—";
    }

    void Add(List<string[]> kv, string name, string key, string unit)
    {
        string v;
        if (smart.TryGetValue(key, out v) && v.Length > 0) kv.Add(new[] { name, v + unit });
    }

    void AddBytes(List<string[]> kv, string name, string key)
    {
        string v; ulong b;
        if (smart.TryGetValue(key, out v) && ulong.TryParse(v, out b) && b > 0) kv.Add(new[] { name, Vols.Size((long)Math.Min(b, (ulong)long.MaxValue)) });
    }

    // останні рядки виводу; рядок прогресу (↻) замінює попередній
    static string Tail(string s, int lines)
    {
        List<string> l = new List<string>();
        foreach (string raw in s.Replace("\r", "").Split('\n'))
        {
            if (raw.Trim().Length == 0) continue;
            if (raw.StartsWith("↻"))
            {
                if (l.Count > 0 && l[l.Count - 1].StartsWith("↻")) l[l.Count - 1] = raw; else l.Add(raw);
            }
            else l.Add(raw);
        }
        int from = Math.Max(0, l.Count - lines);
        List<string> r = new List<string>();
        for (int i = from; i < l.Count; i++) r.Add(l[i].TrimStart('↻'));
        return string.Join("\n", r.ToArray());
    }

    void RunTool(string op, string title, string mode)
    {
        if (cur == null || cur.Letter.Length == 0) return;
        string e;
        msg = "";
        toolTitle = title;
        toolOut = "";
        toolDone = false;
        string args = cur.Letter;
        if (op == "chkdsk" && cur.Fs.ToUpperInvariant() == "NTFS") args += " ntfs";
        if (op == "defrag") args += " " + mode;
        toolFile = Elevated.Run(op, args, out e);
        if (toolFile == null) { msg = e; toolTitle = ""; }
    }

    void LetterMenu()
    {
        ContextMenuStrip m = ThemedMenu();
        HashSet<char> used = new HashSet<char>();
        foreach (DriveInfo d in DriveInfo.GetDrives()) used.Add(char.ToUpperInvariant(d.Name[0]));
        for (char c = 'D'; c <= 'Z'; c++)
        {
            if (used.Contains(c)) continue;
            char cc = c;
            m.Items.Add(c + ":", null, delegate
            {
                string e;
                msg = "";
                toolTitle = L.T("Зміна літери на ") + cc + ":";
                toolOut = ""; toolDone = false;
                letterAt = DateTime.Now;
                // без кінцевої «\» перед лапками - інакше Windows прочитає її як екранування лапки
                toolFile = Elevated.Run("letter", "\"" + cur.Guid.TrimEnd('\\') + "\" " + cc, out e);
                if (toolFile == null) { msg = e; toolTitle = ""; }
            });
        }
        if (m.Items.Count == 0) m.Items.Add(L.T("Вільних літер немає")).Enabled = false;
        m.Show(Cursor.Position);
    }
}

// ======================================================================
//  Батарея: реальний стан, хімія, знос
// ======================================================================
sealed class BatteryWindow : SbWindow
{
    BatInfo b;

    public BatteryWindow(NeoBar bar) : base(bar, L.T("Батарея"), 440, 560)
    { Glyph = "\uE83F";
        Ticker.Interval = 3000;
        b = Battery.Read();
    }

    protected override void HeadButtons(Graphics g, ref int right)
    {
    }

    protected override void OnTick()
    {
        b = Battery.Read();
        Invalidate();
    }

    static string MWh(uint v, bool rel)
    {
        if (v == 0 || v == 0xFFFFFFFF) return "—";
        if (rel) return v.ToString("N0", CultureInfo.InvariantCulture).Replace(",", " ") + L.T(" од.");
        return (v / 1000.0).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + L.T(" Вт·год");
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        if (b == null || !b.Present) { TxtC(g, L.T("Батареї немає"), F, r, P.Sub); return; }
        // піктограма батареї
        Rectangle top = new Rectangle(r.X, r.Y, r.Width, Px(118));
        Card(g, top);
        Rectangle ic = new Rectangle(top.X + Px(18), top.Y + Px(30), Px(110), Px(56));
        using (Pen pn = new Pen(P.Fg, Math.Max(2f, Px(2.5f))))
        using (GraphicsPath gp = Util.Round(ic, Px(8)))
            g.DrawPath(pn, gp);
        using (SolidBrush tb = new SolidBrush(P.Fg)) g.FillRectangle(tb, ic.Right + Px(2), ic.Y + ic.Height / 2 - Px(10), Px(6), Px(20));
        int pct = Math.Max(0, Math.Min(100, b.Pct));
        Color fc = b.HealthLevel >= 2 || (pct <= 15 && !b.OnAc) ? P.Bad : (pct <= 30 && !b.OnAc ? P.Warn : P.Good);
        Rectangle fill = new Rectangle(ic.X + Px(5), ic.Y + Px(5), Math.Max(Px(4), (ic.Width - Px(10)) * pct / 100), ic.Height - Px(10));
        using (SolidBrush fb = new SolidBrush(fc)) Util.FillRound(g, fb, fill, Px(4));
        if (b.Charging)
        {
            float cx = ic.X + ic.Width / 2f, cy = ic.Y + ic.Height / 2f, s = ic.Height * 0.7f;
            PointF[] bolt = { new PointF(cx + s * 0.1f, cy - s * 0.5f), new PointF(cx - s * 0.28f, cy + s * 0.06f), new PointF(cx - s * 0.02f, cy + s * 0.06f),
                              new PointF(cx - s * 0.12f, cy + s * 0.5f), new PointF(cx + s * 0.28f, cy - s * 0.08f), new PointF(cx + s * 0.02f, cy - s * 0.08f) };
            using (SolidBrush bb = new SolidBrush(Color.FromArgb(255, 214, 40))) g.FillPolygon(bb, bolt);
            using (Pen bp = new Pen(Color.FromArgb(120, 0, 0, 0), 1f)) g.DrawPolygon(bp, bolt);
        }
        int tx = ic.Right + Px(28);
        TxtL(g, pct + "%", FHuge, new Rectangle(tx, top.Y + Px(10), top.Right - tx, Px(56)), P.Fg);
        string st = b.Charging ? L.T("Заряджається") : (b.OnAc ? (pct >= 95 ? L.T("Заряджено, від мережі") : L.T("Від мережі, не заряджається")) : L.T("Розряджається"));
        TxtL(g, st, FB, new Rectangle(tx, top.Y + Px(64), top.Right - tx, Px(22)), P.Fg);
        string tl = "";
        if (!b.OnAc && b.TimeLeftMin > 0) tl = L.T("Залишилось ≈ ") + (b.TimeLeftMin / 60) + L.T(" год ") + (b.TimeLeftMin % 60) + L.T(" хв");
        else if (b.Charging && b.RateMw > 0 && b.Full > b.Remain && !b.Relative)
        {
            double h = (b.Full - b.Remain) / (double)b.RateMw;
            tl = L.T("До повного ≈ ") + (int)h + L.T(" год ") + (int)((h - (int)h) * 60) + L.T(" хв");
        }
        TxtL(g, tl, FS, new Rectangle(tx, top.Y + Px(86), top.Right - tx, Px(20)), P.Sub);

        // знос
        int y = top.Bottom + Px(10);
        bool verdict = b.HealthLevel > 0;
        int vh = Px(104);
        if (verdict)
            vh = Math.Max(Px(104), TextRenderer.MeasureText(b.HealthText, FS, new Size(r.Width - Px(28), 2000), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + Px(52));
        Rectangle wc = new Rectangle(r.X, y, r.Width, vh);
        Card(g, wc, verdict ? Util.Blend(P.Card, b.HealthLevel >= 2 ? P.Bad : P.Warn, 0.12f) : P.Card);
        Rectangle wi = Rectangle.Inflate(wc, -Px(14), -Px(10));
        double wear = b.Wear;
        if (verdict)
        {
            // висновок про стан - замість голих цифр (0% від мережі не пояснює, що батарея мертва)
            Color vc = b.HealthLevel >= 2 ? P.Bad : P.Warn;
            TxtL(g, b.HealthTitle, FB, new Rectangle(wi.X, wi.Y, wi.Width, Px(24)), vc);
            if (!double.IsNaN(wear))
                TxtR(g, Math.Round(100 - wear) + L.T("% від нової"), FB, new Rectangle(wi.X, wi.Y, wi.Width, Px(24)), vc);
            TxtW(g, b.HealthText, FS, new Rectangle(wi.X, wi.Y + Px(28), wi.Width, wi.Height - Px(28)), P.Fg);
        }
        else TxtL(g, L.T("Знос (деградація)"), FB, new Rectangle(wi.X, wi.Y, wi.Width, Px(22)), P.Fg);
        if (verdict) { }
        else if (!double.IsNaN(wear))
        {
            Color wcol = wear >= 40 ? P.Bad : (wear >= 20 ? P.Warn : P.Good);
            TxtR(g, wear.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + "%", FH, new Rectangle(wi.X, wi.Y - Px(2), wi.Width, Px(26)), wcol);
            ProgressBar(g, new Rectangle(wi.X, wi.Y + Px(30), wi.Width, Px(10)), (float)(100 - wear) / 100f, wcol);
            TxtL(g, L.T("Повна ємність зараз ") + MWh(b.Full, b.Relative) + L.T(" із заводських ") + MWh(b.Design, b.Relative) +
                " (" + Math.Round(100 - wear) + L.T("% від нової)"), FS, new Rectangle(wi.X, wi.Y + Px(46), wi.Width, Px(36)), P.Sub);
        }
        else TxtL(g, L.T("Батарея не повідомляє заводську ємність."), FS, new Rectangle(wi.X, wi.Y + Px(30), wi.Width, Px(20)), P.Sub);

        // подробиці
        y = wc.Bottom + Px(10);
        Rectangle dc = new Rectangle(r.X, y, r.Width, r.Bottom - y);
        Card(g, dc);
        Rectangle dI = Rectangle.Inflate(dc, -Px(14), -Px(10));
        List<string[]> kv = new List<string[]>();
        kv.Add(new[] { L.T("Тип"), b.ChemText });
        if (b.Remain > 0) kv.Add(new[] { L.T("Поточний заряд"), MWh(b.Remain, b.Relative) });
        if (b.RateMw != int.MinValue && b.RateMw != 0 && !b.Relative)
            kv.Add(new[] { b.RateMw > 0 ? L.T("Потужність заряду") : L.T("Споживання"), (Math.Abs(b.RateMw) / 1000.0).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + L.T(" Вт") });
        if (b.VoltMv > 0) kv.Add(new[] { L.T("Напруга"), (b.VoltMv / 1000.0).ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',') + L.T(" В") });
        if (b.Cycles >= 0) kv.Add(new[] { L.T("Цикли заряду"), b.Cycles.ToString() });
        if (!double.IsNaN(b.TempC)) kv.Add(new[] { L.T("Температура"), Math.Round(b.TempC) + " °C" });
        if (b.Maker.Length > 0) kv.Add(new[] { L.T("Виробник"), b.Maker });
        if (b.Name.Length > 0) kv.Add(new[] { L.T("Модель"), b.Name });
        if (b.Serial.Length > 0) kv.Add(new[] { L.T("Серійний №"), b.Serial });
        if (b.Made.Length > 0) kv.Add(new[] { L.T("Виготовлено"), b.Made });
        int rowH = Math.Max(Px(22), Math.Max(F.Height, FB.Height) + Px(4));     // крупний системний шрифт не зсуває значення відносно назв
        for (int i = 0; i < kv.Count; i++)
        {
            int yy = dI.Y + i * rowH;
            if (yy + rowH > dI.Bottom) break;
            TxtL(g, kv[i][0], F, new Rectangle(dI.X, yy, dI.Width / 2, rowH), P.Sub);
            TxtR(g, kv[i][1], FB, new Rectangle(dI.X + dI.Width / 3, yy, dI.Width * 2 / 3, rowH), P.Fg);
        }
    }
}
