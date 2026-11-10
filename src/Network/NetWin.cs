using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Threading;
using System.Windows.Forms;

// ======================================================================
//  Мережа: швидкість і пінг вибраного сервера (раз на заданий інтервал)
// ======================================================================
static class PingMon
{
    public static string Host = "8.8.8.8";
    public static int Interval = 60;            // секунди
    public static double Last = double.NaN;     // мс; NaN - не відповів
    public static DateTime LastAt = DateTime.MinValue;
    public static int Sent, Lost;
    public static readonly List<float> Hist = new List<float>();
    public static event Action Updated;

    static System.Windows.Forms.Timer timer;
    static volatile bool busy;

    public static readonly string[][] Presets =
    {
        new[] { "8.8.8.8", "Google DNS" }, new[] { "1.1.1.1", "Cloudflare" }, new[] { "9.9.9.9", "Quad9" },
        new[] { "208.67.222.222", "OpenDNS" }, new[] { "94.140.14.14", "AdGuard DNS" }, new[] { "8.26.56.26", "Comodo DNS" }
    };

    public static void Configure(string opt, bool active)
    {
        string h = Host; int iv = Interval;
        if (!string.IsNullOrEmpty(opt))
        {
            string[] p = opt.Split(';');
            if (p[0].Trim().Length > 0) h = p[0].Trim();
            int n;
            if (p.Length > 1 && int.TryParse(p[1], out n)) iv = Math.Max(5, Math.Min(3600, n));
        }
        bool changed = h != Host;
        Host = h; Interval = iv;
        if (changed) { Hist.Clear(); Sent = Lost = 0; Last = double.NaN; LastAt = DateTime.MinValue; }
        // без віджета мережі (на панелі чи на столі) нічого не пінгуємо й не опитуємо DNS
        if (!active) { if (timer != null) timer.Stop(); return; }
        ResolveDetail();
        if (timer == null)
        {
            timer = new System.Windows.Forms.Timer();
            timer.Tick += delegate { Now(); };
        }
        timer.Interval = Interval * 1000;
        if (!timer.Enabled) timer.Start();
        if (changed || LastAt == DateTime.MinValue) Now();
    }

    // назва сервера: готовий варіант («Google DNS») або сама адреса
    public static string Title
    {
        get
        {
            foreach (string[] p in Presets) if (string.Equals(p[0], Host, StringComparison.OrdinalIgnoreCase) && p[1] != p[0]) return p[1];
            return Host;
        }
    }

    // IP та ім'я хоста (зворотний пошук DNS), визначається у фоні
    public static string Detail = "";
    static string detailFor = "";

    static void ResolveDetail()
    {
        string host = Host;
        if (detailFor == host) return;
        detailFor = host;
        Detail = "";
        SynchronizationContext ui = SynchronizationContext.Current;
        Util.Bg(delegate
        {
            string d = "";
            try
            {
                System.Net.IPAddress ip;
                if (System.Net.IPAddress.TryParse(host, out ip))
                {
                    string name = "";
                    try { name = System.Net.Dns.GetHostEntry(ip).HostName; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                    d = ip + (name.Length > 0 && name != ip.ToString() ? " · " + name : "");
                }
                else
                {
                    string addr = "";
                    foreach (System.Net.IPAddress a in System.Net.Dns.GetHostAddresses(host))
                        if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) { addr = a.ToString(); break; }
                    d = host + (addr.Length > 0 ? " · " + addr : "");
                }
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
            Action done = delegate
            {
                if (host != Host) return;
                Detail = d;
                Action u = Updated;
                if (u != null) try { u(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            };
            if (ui != null) ui.Post(delegate { done(); }, null); else done();
        });
    }

    // оновлення вручну (кнопка в шапці): дані плавно згасають і з'являються, як у погоди
    public static readonly FadeAnim Fade = new FadeAnim();

    public static void Refresh()
    {
        if (busy) return;
        Fade.Start();
        Now();
    }

    public static void Now()
    {
        if (busy) return;
        busy = true;
        string host = Host;
        SynchronizationContext ui = SynchronizationContext.Current;
        Util.Bg(delegate
        {
            double ms = double.NaN;
            try
            {
                using (Ping p = new Ping())
                {
                    PingReply r = p.Send(host, 2000);
                    if (r != null && r.Status == IPStatus.Success) ms = r.RoundtripTime;
                }
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
            Action done = delegate
            {
                busy = false;
                Fade.End();
                if (host != Host) return;
                Sent++;
                if (double.IsNaN(ms)) Lost++;
                Last = ms; LastAt = DateTime.Now;
                Hist.Add(double.IsNaN(ms) ? float.NaN : (float)ms);
                if (Hist.Count > 120) Hist.RemoveAt(0);
                Action u = Updated;
                if (u != null) try { u(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            };
            if (ui != null) ui.Post(delegate { done(); }, null); else done();
        });
    }

    public static string Text
    {
        get
        {
            if (LastAt == DateTime.MinValue) return L.T("пінг…");
            return double.IsNaN(Last) ? L.T("немає відповіді") : Math.Round(Last) + L.T(" мс");
        }
    }

    public static Color Quality(Color good, Color warn, Color bad)
    {
        if (LastAt == DateTime.MinValue) return warn;
        if (double.IsNaN(Last)) return bad;
        return Last < 60 ? good : (Last < 150 ? warn : bad);
    }
}

sealed partial class NetWindow : ToolWindow
{
    readonly Entry entry;
    TextBox tbHost;
    readonly List<string[]> outLines = new List<string[]>();   // {текст, тип: "" звичайний, "ok", "bad", "head"}
    volatile int runId;
    volatile bool running;
    int outScroll;
    bool follow = true;

    readonly Action fadeTick;

    public NetWindow(NeoBar bar, Entry e) : base(bar, L.T("Мережа"), 520, 640)
    { Glyph = "\uE968";
        entry = e;
        fadeTick = delegate { if (!IsDisposed && IsHandleCreated) Invalidate(); };
        NeoBar.WeatherFadeTick += fadeTick;
        Tabs = new string[] { L.T("Огляд"), L.T("Адаптери"), L.T("Діагностика"), L.T("Брандмауер") };
        PingMon.Updated += OnData;
        tbHost = MakeInput();
        AdInit();
        tbHost.Text = PingMon.Host;
        tbHost.KeyDown += delegate (object s, KeyEventArgs ke)
        {
            if (ke.KeyCode == Keys.Enter) { ke.SuppressKeyPress = true; StartPing(); }
        };
    }

    void OnData() { if (!IsDisposed) Invalidate(); }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        runId++;
        AdClosed();
        PingMon.Updated -= OnData;
        NeoBar.WeatherFadeTick -= fadeTick;
        base.OnFormClosed(e);
    }

    protected override void OnTab()
    {
        tbHost.Visible = false;
        AdHide();
        if (Tab != 1) AdLeave();
        if (Tab != 3) FwLeave();
    }

    // відкрити вікно одразу на потрібній вкладці (з віджета на столі)
    public void SelectTab(int t)
    {
        if (t < 0 || t >= Tabs.Length || t == Tab) return;
        Tab = t;
        OnTab();
        Invalidate();
    }

    protected override void OnTick() { base.OnTick(); if (Tab == 3) FwSync(); Invalidate(); }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        if (Tab == 2) { AdHide(); PaintDiag(g, r); return; }
        if (Tab == 1) { PaintAdapters(g, r); return; }
        if (Tab == 3) { PaintFw(g, r); return; }
        AdHide();
        PaintOverview(g, r);
        float f = PingMon.Fade.Level;
        if (f < 0.999f)
            using (SolidBrush ov = new SolidBrush(Color.FromArgb((int)Math.Round(255 * (1f - f)), P.Bg))) g.FillRectangle(ov, r);
    }

    void PaintOverview(Graphics g, Rectangle r)
    {
        tbHost.Visible = false;
        // швидкість
        Rectangle sc = new Rectangle(r.X, r.Y, r.Width, Px(70));
        Card(g, sc);
        int hw = (sc.Width - Px(28)) / 2;
        TxtL(g, L.T("Завантаження"), FS, new Rectangle(sc.X + Px(14), sc.Y + Px(8), hw, Px(18)), P.Sub);
        TxtL(g, "↓ " + Widgets.RateText(Widgets.NetDown), FBig, new Rectangle(sc.X + Px(14), sc.Y + Px(26), hw, Px(36)), P.Acc);
        TxtL(g, L.T("Віддача"), FS, new Rectangle(sc.X + Px(14) + hw, sc.Y + Px(8), hw, Px(18)), P.Sub);
        TxtL(g, "↑ " + Widgets.RateText(Widgets.NetUp), FBig, new Rectangle(sc.X + Px(14) + hw, sc.Y + Px(26), hw, Px(36)), P.Fg);

        // пінг вибраного сервера
        int y = sc.Bottom + Px(10);
        Rectangle pc = new Rectangle(r.X, y, r.Width, Px(220));
        Card(g, pc);
        Rectangle pi = Rectangle.Inflate(pc, -Px(14), -Px(10));
        TxtL(g, L.T("Пінг до ") + PingMon.Title, FB, new Rectangle(pi.X, pi.Y, pi.Width - Px(130), Px(22)), P.Fg);
        if (PingMon.Detail.Length > 0) TxtL(g, PingMon.Detail, FS, new Rectangle(pi.X, pi.Y + Px(24), pi.Width - Px(130), Px(18)), P.Sub);
        TxtR(g, PingMon.Text, FH, new Rectangle(pi.X, pi.Y - Px(2), pi.Width, Px(26)), PingMon.Quality(P.Good, P.Warn, P.Bad));
        float mx = 100, mn = float.MaxValue, sum = 0; int n = 0;
        foreach (float v in PingMon.Hist) if (!float.IsNaN(v)) { mx = Math.Max(mx, v * 1.2f); mn = Math.Min(mn, v); sum += v; n++; }
        Graph(g, new Rectangle(pi.X, pi.Y + Px(46), pi.Width, Px(114)), PingMon.Hist, mx, PingMon.Quality(P.Good, P.Warn, P.Bad));
        string st = n > 0 ? L.T("мін ") + Math.Round(mn) + L.T(" · сер ") + Math.Round(sum / n) + L.T(" · макс ") + Math.Round(mx / 1.2f) + L.T(" мс") : L.T("ще немає вимірів");
        if (PingMon.Sent > 0) st += L.T(" · втрати ") + Math.Round(PingMon.Lost * 100.0 / PingMon.Sent) + "%";
        TxtL(g, st, FS, new Rectangle(pi.X, pi.Bottom - Px(20), pi.Width - Px(120), Px(20)), P.Sub);
        string at = PingMon.LastAt == DateTime.MinValue ? "" : L.T("о ") + PingMon.LastAt.ToString("HH:mm:ss");
        TxtR(g, at, FS, new Rectangle(pi.X, pi.Bottom - Px(20), pi.Width, Px(20)), P.Sub);

        // готові сервери для пінгу віджета
        y = pc.Bottom + Px(12);
        TxtL(g, L.T("Сервер для пінгу на панелі"), FS, new Rectangle(r.X, y, r.Width, Px(18)), P.Sub);
        y += Px(22);
        int cw = (r.Width - Px(6) * 2) / 3, ch = Px(30);
        for (int i = 0; i < PingMon.Presets.Length; i++)
        {
            string h = PingMon.Presets[i][0];
            Rectangle cr = new Rectangle(r.X + (i % 3) * (cw + Px(6)), y + (i / 3) * (ch + Px(6)), cw, ch);
            Chip(g, cr, PingMon.Presets[i][1], PingMon.Host == h, delegate { tbHost.Text = h; Bar.SetNetOpt(entry, h + ";" + PingMon.Interval); });
        }
        // як часто пінгувати
        y += 2 * (ch + Px(6)) + Px(8);
        TxtL(g, L.T("Як часто пінгувати"), FS, new Rectangle(r.X, y, r.Width, Px(18)), P.Sub);
        y += Px(22);
        int[] iv = { 15, 30, 60, 900 };
        string[] ivn = { L.T("15 с"), L.T("30 с"), L.T("1 хв"), L.T("15 хв") };
        int iw = (r.Width - Px(6) * 3) / 4;
        for (int i = 0; i < iv.Length; i++)
        {
            int v = iv[i];
            Chip(g, new Rectangle(r.X + i * (iw + Px(6)), y, iw, ch), ivn[i], PingMon.Interval == v, delegate { Bar.SetNetOpt(entry, PingMon.Host + ";" + v); });
        }
    }

    // ---------- діагностика: пінг 10 пакетів і трасування до будь-якої адреси ----------
    void PaintDiag(Graphics g, Rectangle r)
    {
        int y = r.Y;
        TxtL(g, L.T("Адреса або IP"), FS, new Rectangle(r.X, y, r.Width, Px(18)), P.Sub);
        y += Px(20);
        int bwFollow = Px(150);
        PlaceInput(g, tbHost, new Rectangle(r.X, y, r.Width - bwFollow - Px(8), Px(32)));
        string hnow = tbHost.Text.Trim();
        Btn(g, new Rectangle(r.Right - bwFollow, y, bwFollow, Px(32)), L.T("Стежити на панелі"), delegate
        {
            string h = tbHost.Text.Trim();
            if (h.Length > 0) Bar.SetNetOpt(entry, h + ";" + PingMon.Interval);
        }, 0, hnow.Length > 0 && hnow != PingMon.Host);
        y += Px(40);
        int cw = (r.Width - Px(6) * 2) / 3, ch = Px(28);
        for (int i = 0; i < PingMon.Presets.Length; i++)
        {
            string h = PingMon.Presets[i][0];
            Chip(g, new Rectangle(r.X + (i % 3) * (cw + Px(6)), y + (i / 3) * (ch + Px(6)), cw, ch), PingMon.Presets[i][1], tbHost.Text.Trim() == h, delegate { tbHost.Text = h; });
        }
        y += ((PingMon.Presets.Length + 2) / 3) * (ch + Px(6)) + Px(4);
        int bw = (r.Width - Px(16)) / 3;
        Btn(g, new Rectangle(r.X, y, bw, Px(34)), running ? L.T("Зупинити") : L.T("Пінг (10 пакетів)"), delegate { if (running) Stop(); else StartPing(); }, 1, true);
        Btn(g, new Rectangle(r.X + bw + Px(8), y, bw, Px(34)), L.T("Трасування"), delegate { StartTrace(); }, 0, !running);
        Rectangle sw = new Rectangle(r.X + (bw + Px(8)) * 2, y, bw, Px(34));
        Btn(g, sw, L.T("Скан підмережі"), StartSweep, 0, !running);
        Tip(sw, L.T("Пінгує всі адреси вашої локальної мережі й показує, які пристрої живі: ім'я, MAC, шлюз"));
        y += Px(44);
        y = DiagTools(g, r, y);

        Rectangle oc = new Rectangle(r.X, y, r.Width, r.Bottom - y);
        Card(g, oc);
        Rectangle oi = Rectangle.Inflate(oc, -Px(12), -Px(8));
        if (outLines.Count == 0)
        {
            TxtC(g, L.T("Введіть адресу й натисніть «Пінг» (або Enter) чи «Трасування», або виберіть інструмент вище"), FS, oi, P.Sub);
            return;
        }
        int rowH = Math.Max(Px(20), FB.Height + Px(3));
        oi.Width -= Px(10);                                   // місце під смугу прокрутки
        List<string[]> rows = Wrapped(oi.Width);              // довгі рядки (ipconfig, netstat) переносяться, а не обрізаються
        int vis = Math.Max(1, oi.Height / rowH);
        int maxScroll = Math.Max(0, rows.Count - vis);
        if (follow) outScroll = maxScroll;
        outScroll = Math.Max(0, Math.Min(outScroll, maxScroll));
        int ly = oi.Y;
        for (int i = outScroll; i < rows.Count && ly + rowH <= oi.Bottom; i++)
        {
            string[] l = rows[i];
            Color c = l[1] == "ok" ? P.Good : (l[1] == "bad" ? P.Bad : (l[1] == "head" ? P.Acc : P.Fg));
            TxtL(g, l[0], l[1] == "head" ? FB : F, new Rectangle(oi.X, ly, oi.Width, rowH), c);
            ly += rowH;
        }
        int ms = maxScroll;
        Wheel(oc, delegate (int d) { outScroll -= d * 3; follow = outScroll >= ms; });
        VBar(g, new Rectangle(oc.Right - Px(9), oi.Y, Px(4), vis * rowH), rows.Count, vis, outScroll,
            delegate (int v) { outScroll = v; follow = outScroll >= ms; Invalidate(); });
        if (!running && outLines.Count > 0)
            HotR(oc, null, delegate
            {
                StringBuilderCopy();
            });
    }

    // рядки виводу з переносом під ширину (кеш: перераховується, лише коли додалися рядки чи змінилась ширина)
    List<string[]> wrapCache = new List<string[]>();
    int wrapCount = -1, wrapW = -1, wrapFirst;
    int wrapRun = -1;

    List<string[]> Wrapped(int w)
    {
        lock (outLines)
        {
            if (wrapCount == outLines.Count && wrapW == w && wrapRun == runId) return wrapCache;
            if (wrapW != w || wrapRun != runId || outLines.Count < wrapCount) { wrapCache = new List<string[]>(); wrapFirst = 0; }
            for (int i = wrapFirst; i < outLines.Count; i++)
            {
                string[] l = outLines[i];
                Font f = l[1] == "head" ? FB : F;
                string s = l[0] ?? "";
                // відступ на початку рядка (ipconfig) зберігаємо й для перенесених частин
                int ind = 0; while (ind < s.Length && s[ind] == ' ') ind++;
                string pad = s.Substring(0, Math.Min(ind, 8));
                bool first = true;
                while (s.Length > 0)
                {
                    int fit = s.Length;
                    if (TextRenderer.MeasureText(s, f, new Size(int.MaxValue, 100), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width > w)
                    {
                        int lo = 1, hi = s.Length;           // найдовший префікс, що вміщується
                        while (lo < hi)
                        {
                            int mid = (lo + hi + 1) / 2;
                            if (TextRenderer.MeasureText(s.Substring(0, mid), f, new Size(int.MaxValue, 100), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width <= w) lo = mid; else hi = mid - 1;
                        }
                        fit = lo;
                        int sp = s.LastIndexOf(' ', Math.Max(0, fit - 1));
                        if (sp > ind + 4 && sp < fit) fit = sp + 1;     // переносимо по пробілу, якщо він не надто близько до початку
                    }
                    wrapCache.Add(new[] { (first ? "" : pad + "  ") + s.Substring(0, fit).TrimEnd(), l[1] });
                    s = s.Substring(fit).TrimStart();
                    first = false;
                }
                if ((l[0] ?? "").Length == 0) wrapCache.Add(new[] { "", l[1] });
            }
            wrapFirst = outLines.Count;
            wrapCount = outLines.Count; wrapW = w; wrapRun = runId;
            return wrapCache;
        }
    }

    void StringBuilderCopy()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        lock (outLines) foreach (string[] l in outLines) sb.AppendLine(l[0]);
        try { Clipboard.SetText(sb.ToString()); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    void Add(int id, string text, string kind)
    {
        if (id != runId) return;
        try
        {
            BeginInvoke(new Action(delegate
            {
                if (id != runId) return;
                lock (outLines) outLines.Add(new[] { text, kind });
                Invalidate();
            }));
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    void Finish(int id)
    {
        try { BeginInvoke(new Action(delegate { if (id == runId) { running = false; Invalidate(); } })); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    void Stop() { runId++; running = false; lock (outLines) outLines.Add(new[] { L.T("Зупинено"), "" }); Invalidate(); }

    void Begin(string title)
    {
        runId++;
        running = true;
        follow = true;
        lock (outLines) { outLines.Clear(); outLines.Add(new[] { title, "head" }); }
        Invalidate();
    }

    static System.Net.IPAddress Resolve(string host)
    {
        System.Net.IPAddress ip;
        if (System.Net.IPAddress.TryParse(host, out ip)) return ip;
        System.Net.IPAddress[] all = System.Net.Dns.GetHostAddresses(host);
        foreach (System.Net.IPAddress a in all) if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) return a;
        return all.Length > 0 ? all[0] : null;
    }

    void StartPing()
    {
        string host = tbHost.Text.Trim();
        if (host.Length == 0) return;
        Begin(L.T("Пінг ") + host + L.T(" - 10 пакетів по 32 байти"));
        int id = runId;
        Util.Bg(delegate
        {
            int ok = 0; long mn = long.MaxValue, mx = 0, sum = 0;
            System.Net.IPAddress ip = null;
            try { ip = Resolve(host); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            if (ip == null) { Add(id, L.T("Не вдалося знайти адресу «") + host + L.Q("»"), "bad"); Finish(id); return; }
            if (ip.ToString() != host) Add(id, L.T("Адреса: ") + ip, "");
            byte[] buf = new byte[32];
            for (int i = 0; i < buf.Length; i++) buf[i] = (byte)('a' + i % 23);
            int sent = 0;
            using (Ping p = new Ping())
                for (int i = 0; i < 10 && id == runId; i++)
                {
                    DateTime t0 = DateTime.Now;
                    sent++;
                    try
                    {
                        PingReply rp = p.Send(ip, 2000, buf, new PingOptions(128, true));
                        if (rp.Status == IPStatus.Success)
                        {
                            ok++; long ms = rp.RoundtripTime; mn = Math.Min(mn, ms); mx = Math.Max(mx, ms); sum += ms;
                            Add(id, (i + 1) + L.T(".  Відповідь від ") + rp.Address + L.T(": час ") + (ms < 1 ? "<1" : ms.ToString()) + L.T(" мс") + (rp.Options != null ? ", TTL " + rp.Options.Ttl : ""), ms < 60 ? "ok" : "");
                        }
                        else Add(id, (i + 1) + ".  " + StatusText(rp.Status), "bad");
                    }
                    catch (Exception ex) { Add(id, (i + 1) + L.T(".  Помилка: ") + (ex.InnerException != null ? ex.InnerException.Message : ex.Message), "bad"); }
                    int wait = 1000 - (int)(DateTime.Now - t0).TotalMilliseconds;
                    if (i < 9 && wait > 0) Thread.Sleep(wait);
                }
            if (id != runId) return;
            int lost = sent - ok;
            Add(id, L.T("Надіслано ") + sent + L.T(", отримано ") + ok + L.T(", втрачено ") + lost + " (" + (sent > 0 ? lost * 100 / sent : 0) + "%)", lost == 0 ? "ok" : "bad");
            if (ok > 0) Add(id, L.T("Мін ") + mn + L.T(" мс · сер ") + (sum / ok) + L.T(" мс · макс ") + mx + L.T(" мс"), "");
            Finish(id);
        });
    }

    void StartTrace()
    {
        string host = tbHost.Text.Trim();
        if (host.Length == 0) return;
        Begin(L.T("Трасування до ") + host + L.T(" (до 30 вузлів)"));
        int id = runId;
        Util.Bg(delegate
        {
            System.Net.IPAddress ip = null;
            try { ip = Resolve(host); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            if (ip == null) { Add(id, L.T("Не вдалося знайти адресу «") + host + L.Q("»"), "bad"); Finish(id); return; }
            Add(id, L.T("Ціль: ") + ip, "");
            byte[] buf = new byte[32];
            using (Ping p = new Ping())
                for (int ttl = 1; ttl <= 30 && id == runId; ttl++)
                {
                    string addr = null;
                    bool reached = false;
                    List<string> times = new List<string>();
                    for (int k = 0; k < 3 && id == runId; k++)
                    {
                        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                        try
                        {
                            PingReply rp = p.Send(ip, 1500, buf, new PingOptions(ttl, true));
                            sw.Stop();
                            if (rp.Status == IPStatus.TtlExpired || rp.Status == IPStatus.Success)
                            {
                                addr = rp.Address.ToString();
                                long ms = rp.Status == IPStatus.Success && rp.RoundtripTime > 0 ? rp.RoundtripTime : sw.ElapsedMilliseconds;
                                times.Add(ms < 1 ? "<1" : ms.ToString());
                                if (rp.Status == IPStatus.Success) reached = true;
                            }
                            else times.Add("*");
                        }
                        catch { times.Add("*"); }
                    }
                    if (id != runId) return;
                    string tl = string.Join(" / ", times.ToArray()) + (times.Exists(delegate (string x) { return x != "*"; }) ? L.T(" мс") : "");
                    Add(id, ttl.ToString().PadLeft(2) + ".  " + (addr ?? L.T("немає відповіді")) + "   " + tl, addr == null ? "bad" : (reached ? "ok" : ""));
                    if (reached) { Add(id, L.T("Трасування завершено"), "ok"); break; }
                    if (ttl == 30) Add(id, L.T("Ціль не досягнута за 30 вузлів"), "bad");
                }
            Finish(id);
        });
    }

    static string StatusText(IPStatus s)
    {
        switch (s)
        {
            case IPStatus.TimedOut: return L.T("Час очікування вичерпано");
            case IPStatus.DestinationHostUnreachable: return L.T("Вузол недоступний");
            case IPStatus.DestinationNetworkUnreachable: return L.T("Мережа недоступна");
            case IPStatus.TtlExpired: return L.T("Час життя пакета (TTL) вичерпано");
            case IPStatus.PacketTooBig: return L.T("Завеликий пакет");
        }
        return L.T("Немає відповіді (") + s + ")";
    }

    protected override void HeadButtons(Graphics g, ref int right)
    {
        if (Tab == 3)
        {
            Rectangle fr = new Rectangle(right - Px(32), Px(7), Px(32), Px(32));
            IconBtn(g, fr, "desk", delegate { ClockData.AddDesk("fw"); }, false);
            Tip(fr, L.T("Віджет брандмауера на робочий стіл: стан і вмикання одним кліком"));
            right = fr.Left - Px(4);
            fr = new Rectangle(right - Px(32), Px(7), Px(32), Px(32));
            IconBtn(g, fr, "sync", delegate { FwReload(); }, false);
            right = fr.Left - Px(4);
            return;
        }
        if (Tab != 0) return;
        // зліва направо: оновити, на робочий стіл, закрити
        Rectangle rr = new Rectangle(right - Px(32), Px(7), Px(32), Px(32));
        IconBtn(g, rr, "desk", delegate { ClockData.AddDesk("net"); }, false);
        right = rr.Left - Px(4);
        rr = new Rectangle(right - Px(32), Px(7), Px(32), Px(32));
        IconBtn(g, rr, "sync", delegate { PingMon.Refresh(); Bar.StartFade(); }, false);
        right = rr.Left - Px(4);
    }
}
