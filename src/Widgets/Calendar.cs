using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// ======================================================================
//  Календар: державні свята з інтернету (date.nager.at, за країною), важливі світові дні,
//  день народження користувача і власні свята
// ======================================================================
sealed class Holiday
{
    public const int Public = 0, Mine = 1, World = 2, Birth = 3;
    public DateTime Date;
    public string Name = "";
    public bool Own, Yearly;
    public int Kind;            // Public - державне, Mine - власне (Own), World - світовий день, Birth - день народження

    // колір позначки в календарі
    public Color Mark(Pal p) { return Kind == Public ? p.Bad : (Kind == World ? p.Good : p.Acc); }

    public string Suffix
    {
        get
        {
            if (Kind == Public) return L.T("  · державне");
            if (Kind == World) return L.T("  · світовий день");
            if (Kind == Birth) return L.T("  · щороку");
            return Yearly ? L.T("  · щороку") : "";
        }
    }
}

static class Holidays
{
    public static readonly List<Holiday> Own = new List<Holiday>();
    public static string Country = "";          // "" - автоматично за місцем
    public static bool WeekNumbers = true, Online = true, World = true;
    public static bool NotifyOn = true;
    public static int NotifyAt = 9 * 60, NotifyDelay = 5;     // хвилини від півночі; хвилини після запуску
    public static DateTime LastNotified = DateTime.MinValue;
    public static string Status = "";
    public static event Action Changed;

    static readonly Dictionary<string, List<Holiday>> net = new Dictionary<string, List<Holiday>>();
    static readonly HashSet<string> busy = new HashSet<string>();
    static string path;
    static bool loaded;

    public static readonly string[][] Countries =
    {
        new[] { "UA", L.T("Україна") }, new[] { "PL", L.T("Польща") }, new[] { "DE", L.T("Німеччина") }, new[] { "CZ", L.T("Чехія") },
        new[] { "SK", L.T("Словаччина") }, new[] { "MD", L.T("Молдова") }, new[] { "RO", L.T("Румунія") }, new[] { "HU", L.T("Угорщина") },
        new[] { "LT", L.T("Литва") }, new[] { "LV", L.T("Латвія") }, new[] { "EE", L.T("Естонія") }, new[] { "AT", L.T("Австрія") },
        new[] { "IT", L.T("Італія") }, new[] { "ES", L.T("Іспанія") }, new[] { "FR", L.T("Франція") }, new[] { "NL", L.T("Нідерланди") },
        new[] { "GB", L.T("Велика Британія") }, new[] { "IE", L.T("Ірландія") }, new[] { "PT", L.T("Португалія") }, new[] { "US", L.T("США") },
        new[] { "CA", L.T("Канада") }, new[] { "NO", L.T("Норвегія") }, new[] { "SE", L.T("Швеція") }, new[] { "FI", L.T("Фінляндія") }
    };

    public static string CountryName(string cc)
    {
        foreach (string[] c in Countries) if (c[0] == cc) return c[1];
        return cc;
    }

    // код країни: вибраний вручну, інакше з місця розташування, інакше Україна
    // Країна свят: вибрана вручну; інакше за місцем (лише якщо дозволено визначати розташування),
    // а коли місця ще немає - за IP-адресою; без дозволу - Україна
    static string ipCc = "";
    static bool ipBusy;

    public static string Cc(Cfg c)
    {
        if (Country.Length == 2) return Country;
        if (c != null && !c.LocAllow) return "UA";
        if (c != null && c.Country.Length == 2) return c.Country.ToUpperInvariant();
        if (ipCc.Length == 2) return ipCc;
        if (!ipBusy)
        {
            ipBusy = true;
            Util.Bg(delegate
            {
                double la, lo; string city, cc, tz;
                if (Geo.IpLocation(out la, out lo, out city, out cc, out tz) && cc.Length == 2)
                {
                    ipCc = cc.ToUpperInvariant();
                    if (ipCc != "UA") Reset();
                }
            });
        }
        return "UA";
    }

    static void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true;
        path = Prof.File("calendar.ini");
        try
        {
            if (!SafeFile.Exists(path)) return;
            foreach (string raw in SafeFile.ReadLines(path))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0 || raw.StartsWith("#")) continue;
                string k = raw.Substring(0, eq).Trim(), v = raw.Substring(eq + 1);
                if (k == "country") Country = v.Trim().ToUpperInvariant();
                else if (k == "weeks") WeekNumbers = v.Trim() == "1";
                else if (k == "online") Online = v.Trim() != "0";
                else if (k == "world") World = v.Trim() != "0";
                else if (k == "notify") NotifyOn = v.Trim() != "0";
                else if (k == "notifyat") { int n; if (int.TryParse(v.Trim(), out n)) NotifyAt = Math.Max(0, Math.Min(1439, n)); }
                else if (k == "notifydelay") { int n; if (int.TryParse(v.Trim(), out n)) NotifyDelay = Math.Max(0, Math.Min(120, n)); }
                else if (k == "notified") { DateTime d; if (DateTime.TryParseExact(v.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) LastNotified = d; }
                else if (k == "holiday")
                {
                    string[] p = v.Split('|');
                    if (p.Length < 3) continue;
                    Holiday h = new Holiday();
                    if (!DateTime.TryParseExact(p[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out h.Date)) continue;
                    h.Yearly = p[1] == "1";
                    h.Name = p[2];
                    h.Own = true;
                    h.Kind = Holiday.Mine;
                    Own.Add(h);
                }
            }
        }
        catch { }
    }

    public static void Save()
    {
        EnsureLoaded();
        try
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(L.T("# NeoBar - календар: власні свята та налаштування"));
            sb.AppendLine("country=" + Country);
            sb.AppendLine("weeks=" + (WeekNumbers ? 1 : 0));
            sb.AppendLine("online=" + (Online ? 1 : 0));
            sb.AppendLine("world=" + (World ? 1 : 0));
            sb.AppendLine("notify=" + (NotifyOn ? 1 : 0));
            sb.AppendLine("notifyat=" + NotifyAt);
            sb.AppendLine("notifydelay=" + NotifyDelay);
            if (LastNotified != DateTime.MinValue) sb.AppendLine("notified=" + LastNotified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            foreach (Holiday h in Own)
                sb.AppendLine("holiday=" + h.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + (h.Yearly ? 1 : 0) + "|" + h.Name.Replace("|", "/").Replace("\n", " "));
            SafeFile.Write(path, sb.ToString(), new UTF8Encoding(true));
        }
        catch { }
        Fire();
    }

    public static int Ver;          // лічильник змін (віджет-календар на столі перемальовується лише коли щось змінилось)

    static void Fire()
    {
        Ver++;
        Action a = Changed;
        if (a != null) try { a(); } catch { }
    }

    // свята на дату: власні, день народження, державні (з інтернету), світові дні
    public static List<Holiday> On(DateTime d, Cfg c)
    {
        EnsureLoaded();
        if (c == null && ClockData.Bar != null) c = ClockData.Bar.Config;
        List<Holiday> r = new List<Holiday>();
        foreach (Holiday h in Own)
            if (h.Date.Date == d.Date || (h.Yearly && h.Date.Month == d.Month && h.Date.Day == d.Day)) r.Add(h);
        DateTime bd;
        if (c != null && BirthdayIn(c, d.Year, out bd) && bd == d.Date)
        {
            Holiday b = new Holiday();
            b.Date = bd; b.Name = L.T("Мій день народження"); b.Kind = Holiday.Birth; b.Yearly = true;
            r.Add(b);
        }
        bool pub = false;
        if (Online)
        {
            List<Holiday> l = Year(d.Year, c);
            if (l != null) foreach (Holiday h in l) if (h.Date.Date == d.Date) { r.Add(h); pub = true; }
        }
        // світовий день, що збігається з державним святом (Новий рік, Різдво, Великдень…), не дублюється
        if (World && !pub)
            foreach (Holiday h in WorldYear(d.Year, Cc(c))) if (h.Date == d.Date) r.Add(h);
        return r;
    }

    // день народження з налаштувань («MM-dd») у вказаному році; 29 лютого в невисокосний рік - 28-го
    public static bool BirthdayIn(Cfg c, int year, out DateTime d)
    {
        d = DateTime.MinValue;
        if (c == null || c.Birthday.Length != 5) return false;
        int m, day;
        if (!int.TryParse(c.Birthday.Substring(0, 2), out m) || !int.TryParse(c.Birthday.Substring(3, 2), out day)) return false;
        if (m < 1 || m > 12 || day < 1) return false;
        day = Math.Min(day, DateTime.DaysInMonth(year, m));
        d = new DateTime(year, m, day);
        return true;
    }

    // Великдень: православний (юліанська пасхалія + 13 днів, чинне для 1900-2099) або західний (григоріанська)
    public static DateTime Easter(int y, bool orthodox)
    {
        if (orthodox)
        {
            int a = y % 4, b = y % 7, cc = y % 19, dd = (19 * cc + 15) % 30, e = (2 * a + 4 * b - dd + 34) % 7;
            int month = (dd + e + 114) / 31, day = (dd + e + 114) % 31 + 1;
            return new DateTime(y, month, day).AddDays(13);
        }
        int ga = y % 19, gb = y / 100, gc = y % 100, gd = gb / 4, ge = gb % 4, gf = (gb + 8) / 25, gg = (gb - gf + 1) / 3;
        int gh = (19 * ga + gb - gd - gg + 15) % 30, gi = gc / 4, gk = gc % 4, gl = (32 + 2 * ge + 2 * gi - gh - gk) % 7;
        int gm = (ga + 11 * gh + 22 * gl) / 451;
        int mo = (gh + gl - 7 * gm + 114) / 31, da = (gh + gl - 7 * gm + 114) % 31 + 1;
        return new DateTime(y, mo, da);
    }

    // Великдень для святкової теми: за вибором у налаштуваннях, інакше за країною
    public static DateTime EasterFor(int y, Cfg c)
    {
        string r = c != null ? c.EasterRite : "";
        return Easter(y, r == "orthodox" || (r != "catholic" && Orthodox(Cc(c))));
    }

    // Різдво для святкової теми: 25 грудня (григоріанський календар) або 7 січня (юліанський, 25 грудня за старим стилем)
    public static DateTime ChristmasFor(int y, Cfg c)
    {
        string r = c != null ? c.XmasCal : "";
        bool julian = r == "julian" || (r != "gregorian" && JulianXmas(Cc(c)));
        return julian ? new DateTime(y, 1, 7) : new DateTime(y, 12, 25);
    }

    // де Різдво за замовчуванням святкують 7 січня (в Україні державне свято з 2023 року - 25 грудня)
    static bool JulianXmas(string cc) { return cc == "RU" || cc == "BY" || cc == "RS" || cc == "GE" || cc == "MK" || cc == "ME"; }

    // країни, де Великдень святкують за східним обрядом
    public static bool Orthodox(string cc) { return cc == "UA" || cc == "MD" || cc == "RO"; }

    static readonly Dictionary<string, List<Holiday>> world = new Dictionary<string, List<Holiday>>();

    // n-та неділя місяця
    static DateTime NthSunday(int y, int m, int n)
    {
        DateTime d = new DateTime(y, m, 1);
        while (d.DayOfWeek != DayOfWeek.Sunday) d = d.AddDays(1);
        return d.AddDays(7 * (n - 1));
    }

    // важливі світові дні за рік (рахуються на місці, без інтернету)
    static List<Holiday> WorldYear(int y, string cc)
    {
        string key = cc + "-" + y;
        List<Holiday> l;
        lock (world) if (world.TryGetValue(key, out l)) return l;
        l = new List<Holiday>();
        Action<DateTime, string> add = delegate (DateTime d, string n)
        {
            foreach (Holiday x in l) if (x.Date == d && x.Name == n) return;
            Holiday h = new Holiday(); h.Date = d; h.Name = n; h.Kind = Holiday.World; l.Add(h);
        };
        add(new DateTime(y, 1, 1), L.T("Новий рік"));
        add(new DateTime(y, 2, 14), L.T("День святого Валентина"));
        add(new DateTime(y, 3, 8), L.T("Міжнародний жіночий день"));
        add(new DateTime(y, 4, 1), L.T("День сміху"));
        add(new DateTime(y, 4, 7), L.T("Всесвітній день здоров'я"));
        add(new DateTime(y, 4, 12), L.T("Міжнародний день польоту людини в космос"));
        add(new DateTime(y, 4, 22), L.T("День Землі"));
        DateTime eo = Easter(y, true), ew = Easter(y, false);
        if (eo == ew) add(eo, L.T("Великдень"));
        else if (Orthodox(cc)) { add(eo, L.T("Великдень")); add(ew, L.T("Великдень (західний обряд)")); }
        else { add(ew, L.T("Великдень")); add(eo, L.T("Великдень (східний обряд)")); }
        add(new DateTime(y, 5, 1), L.T("День праці"));
        add(NthSunday(y, 5, 2), L.T("День матері"));
        add(new DateTime(y, 6, 1), L.T("Міжнародний день захисту дітей"));
        add(new DateTime(y, 6, 5), L.T("Всесвітній день довкілля"));
        add(NthSunday(y, 6, 3), L.T("День батька"));
        add(new DateTime(y, 1, 1).AddDays(255), L.T("День програміста"));
        add(new DateTime(y, 9, 21), L.T("Міжнародний день миру"));
        add(new DateTime(y, 10, 5), L.T("Всесвітній день учителя"));
        add(new DateTime(y, 10, 31), L.T("Хелловін"));
        add(new DateTime(y, 12, 10), L.T("День прав людини"));
        add(new DateTime(y, 12, 25), L.T("Різдво"));
        add(new DateTime(y, 12, 31), L.T("Новорічна ніч"));
        l.Sort(delegate (Holiday a, Holiday b) { return a.Date.CompareTo(b.Date); });
        lock (world) world[key] = l;
        return l;
    }

    // як On, але null, якщо свята з інтернету ще не завантажені
    public static List<Holiday> TryOn(DateTime d, Cfg c)
    {
        EnsureLoaded();
        if (Online && Year(d.Year, c) == null) return null;
        return On(d, c);
    }

    public static void Load() { EnsureLoaded(); }

    public static string TodayText()
    {
        try
        {
            List<Holiday> l = On(DateTime.Today, ClockData.Bar != null ? ClockData.Bar.Config : null);
            List<string> n = new List<string>();
            foreach (Holiday h in l) n.Add(h.Name);
            return n.Count > 0 ? L.T("Сьогодні: ") + string.Join(", ", n.ToArray()) : "";
        }
        catch { return ""; }
    }

    // свята країни за рік; null - ще завантажуються (по готовності - подія Changed)
    public static List<Holiday> Year(int year, Cfg c)
    {
        string cc = Cc(c);
        string key = cc + "-" + year;
        List<Holiday> l;
        lock (net) if (net.TryGetValue(key, out l)) return l;
        lock (busy)
        {
            if (busy.Contains(key)) return null;
            busy.Add(key);
        }
        Status = L.T("Завантаження свят…");
        Util.Bg(delegate
        {
            List<Holiday> res = new List<Holiday>();
            string st;
            try
            {
                bool stale;
                string url = "https://date.nager.at/api/v3/PublicHolidays/" + year + "/" + cc;
                string js = Encoding.UTF8.GetString(NetCache.Get(url, url, TimeSpan.Zero, out stale, null));
                foreach (string o in Js.Objects(js))
                {
                    Holiday h = new Holiday();
                    if (!DateTime.TryParseExact(Js.Str(o, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out h.Date)) continue;
                    h.Name = Js.Str(o, "localName");
                    if (h.Name.Length == 0) h.Name = Js.Str(o, "name");
                    res.Add(h);
                }
                st = L.T("Свята: ") + CountryName(cc) + " (date.nager.at)" + (stale ? L.T(", збережені") : "");
            }
            catch { st = L.T("Немає зв'язку - свята з інтернету недоступні"); }
            lock (net) net[key] = res;
            lock (busy) busy.Remove(key);
            Status = st;
            Fire();
        });
        return null;
    }

    public static void Reset()
    {
        lock (net) net.Clear();
        lock (world) world.Clear();
        Fire();
    }
}

// ----------------------------------------------------------------------
//  Вікно календаря
// ----------------------------------------------------------------------
sealed partial class ClockWindow
{
    DateTime month, sel;
    TextBox tbName;
    bool adding, addYearly = true;
    int listScroll;

    void InitCalendar()
    {
        month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        sel = DateTime.Today;
        tbName = MakeInput();
        tbName.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { SaveNew(); e.SuppressKeyPress = true; } };
        Holidays.Changed += OnHol;
    }

    void OnHol()
    {
        try { if (!IsDisposed) BeginInvoke(new Action(Invalidate)); } catch { }
    }

    static string Cap(string s) { return s.Length > 0 ? char.ToUpper(s[0]) + s.Substring(1) : s; }

    void PickMonth(Rectangle at)
    {
        ContextMenuStrip m = ThemedMenu();
        for (int i = 1; i <= 12; i++)
        {
            int k = i;
            ToolStripMenuItem it = new ToolStripMenuItem(Cap(new DateTime(2000, i, 1).ToString("MMMM", Widgets.UK)));
            it.Checked = month.Month == i;
            it.Click += delegate { month = new DateTime(month.Year, k, 1); listScroll = 0; Invalidate(); };
            m.Items.Add(it);
        }
        m.Show(this, new Point(at.X, at.Bottom));
    }

    void PickYear(Rectangle at)
    {
        ContextMenuStrip m = ThemedMenu();
        int y0 = DateTime.Today.Year;
        for (int y = y0 - 10; y <= y0 + 10; y++)
        {
            int k = y;
            ToolStripMenuItem it = new ToolStripMenuItem(y.ToString());
            it.Checked = month.Year == y;
            if (y == y0) it.Font = new Font(it.Font, FontStyle.Bold);
            it.Click += delegate { month = new DateTime(k, month.Month, 1); listScroll = 0; Invalidate(); };
            m.Items.Add(it);
        }
        m.Show(this, new Point(at.X, at.Bottom));
    }

    void Go(int d)
    {
        month = month.AddMonths(d);
        listScroll = 0;
        Invalidate();
    }

    void PaintCalendar(Graphics g, Rectangle r)
    {
        Cfg c = Bar.Config;
        // заголовок місяця
        // місяць і рік - випадні списки для швидкого переходу
        string mn = Cap(month.ToString("MMMM", Widgets.UK)) + "  ▾";
        string yn = month.Year + "  ▾";
        IconBtn(g, new Rectangle(r.X, r.Y, Px(32), Px(32)), "left", delegate { Go(-1); }, false);
        int mw = TextRenderer.MeasureText(mn, FH).Width + Px(16), yw = TextRenderer.MeasureText(yn, FH).Width + Px(16);
        int tx0 = r.X + Px(36) + (r.Width - Px(180) - mw - yw - Px(4)) / 2;
        Rectangle mr = new Rectangle(tx0, r.Y, mw, Px(32)), yr = new Rectangle(tx0 + mw + Px(4), r.Y, yw, Px(32));
        foreach (Rectangle rr in new[] { mr, yr })
            if (Over(rr)) using (SolidBrush b = new SolidBrush(P.Card)) Util.FillRound(g, b, rr, Math.Min(P.Rad, rr.Height / 2));
        TxtC(g, mn, FH, mr, P.Fg);
        TxtC(g, yn, FH, yr, P.Fg);
        Hot(mr, delegate { PickMonth(mr); });
        Hot(yr, delegate { PickYear(yr); });
        IconBtn(g, new Rectangle(r.Right - Px(140), r.Y, Px(32), Px(32)), "right", delegate { Go(1); }, false);
        Btn(g, new Rectangle(r.Right - Px(100), r.Y + Px(2), Px(100), Px(28)), L.T("Сьогодні"), delegate
        {
            month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); sel = DateTime.Today; Invalidate();
        }, 0, true);
        Wheel(new Rectangle(r.X, r.Y, r.Width, Px(300)), delegate (int d) { Go(-d); });

        // сітка
        bool wk = Holidays.WeekNumbers;
        int wkW = wk ? Px(30) : 0;
        int gx = r.X + wkW, gw = r.Width - wkW;
        int cw = gw / 7, ch = Px(38);
        int y0 = r.Y + Px(42);
        string[] dn = { L.T("Пн"), L.T("Вт"), L.T("Ср"), L.T("Чт"), L.T("Пт"), L.T("Сб"), L.T("Нд") };
        for (int i = 0; i < 7; i++)
            TxtC(g, dn[i], FS, new Rectangle(gx + i * cw, y0, cw, Px(20)), i >= 5 ? P.Bad : P.Sub);
        int lead = ((int)month.DayOfWeek + 6) % 7;
        DateTime start = month.AddDays(-lead);
        int gy = y0 + Px(22);
        for (int row = 0; row < 6; row++)
        {
            DateTime rowStart = start.AddDays(row * 7);
            if (wk) TxtC(g, Widgets.Week(rowStart).ToString(), FS, new Rectangle(r.X, gy + row * ch, wkW, ch), Util.Blend(P.Bg, P.Sub, 0.7f));
            for (int col = 0; col < 7; col++)
            {
                DateTime d = rowStart.AddDays(col);
                Rectangle cr = new Rectangle(gx + col * cw + Px(2), gy + row * ch + Px(2), cw - Px(4), ch - Px(4));
                bool inMonth = d.Month == month.Month;
                bool today = d == DateTime.Today, isSel = d == sel;
                List<Holiday> hs = Holidays.On(d, c);
                bool hol = false;
                List<Color> marks = new List<Color>();
                foreach (Holiday h in hs)
                {
                    if (h.Kind == Holiday.Public) hol = true;
                    Color mc = today ? P.Fg : h.Mark(P);
                    if (!marks.Contains(mc)) marks.Add(mc);
                }
                if (isSel || Over(cr))
                    using (SolidBrush b = new SolidBrush(isSel ? Util.Blend(P.Bg, P.Acc, 0.25f) : P.Card)) Util.FillRound(g, b, cr, Math.Min(P.Rad, cr.Height / 2));
                if (today)
                {
                    int dsz = Math.Min(cr.Width, cr.Height) - Px(4);
                    Rectangle tr = new Rectangle(cr.X + (cr.Width - dsz) / 2, cr.Y + (cr.Height - dsz) / 2, dsz, dsz);
                    using (SolidBrush b = new SolidBrush(P.Acc)) g.FillEllipse(b, tr);
                }
                Color tc = today ? Util.Readable(P.Fg, P.Acc, 120) : (hol ? P.Bad : (col >= 5 ? Util.Blend(P.Fg, P.Bad, 0.45f) : P.Fg));
                if (!inMonth) tc = Util.Blend(P.Bg, tc, 0.4f);
                TxtC(g, d.Day.ToString(), hol || today ? FB : F, cr, tc);
                // крапки під числом: червона - державне, зелена - світовий день, колір теми - своє / день народження
                if (marks.Count > 0)
                {
                    int ds = Px(5), gap = Px(2);
                    int dx = cr.X + (cr.Width - marks.Count * ds - (marks.Count - 1) * gap) / 2;
                    foreach (Color mc in marks)
                    {
                        using (SolidBrush b = new SolidBrush(mc)) g.FillEllipse(b, dx, cr.Bottom - ds - Px(1), ds, ds);
                        dx += ds + gap;
                    }
                }
                DateTime dd = d;
                Hot(cr, delegate
                {
                    sel = dd; adding = false; tbName.Visible = false;
                    if (dd.Month != month.Month) month = new DateTime(dd.Year, dd.Month, 1);
                });
            }
        }
        int y = gy + 6 * ch + Px(10);

        // вибраний день
        Rectangle dc = new Rectangle(r.X, y, r.Width, r.Bottom - y - Px(26));
        Card(g, dc);
        Rectangle di = Rectangle.Inflate(dc, -Px(14), -Px(10));
        string sd = sel.ToString("dddd, d MMMM", Widgets.UK);
        sd = char.ToUpper(sd[0]) + sd.Substring(1);
        TxtL(g, sd, FB, new Rectangle(di.X, di.Y, di.Width - Px(130), Px(24)), P.Fg);
        if (!adding)
            Btn(g, new Rectangle(di.Right - Px(126), di.Y - Px(2), Px(126), Px(28)), L.T("+ Своє свято"), delegate
            {
                adding = true; tbName.Text = ""; tbName.Focus();
            }, 1, true);
        int ly = di.Y + Px(30);
        if (adding)
        {
            PlaceInput(g, tbName, new Rectangle(di.X, ly, di.Width, Px(32)));
            ly += Px(40);
            Switch(g, new Rectangle(di.X, ly, Px(200), Px(28)), L.T("Щороку"), addYearly, delegate { addYearly = !addYearly; });
            Btn(g, new Rectangle(di.Right - Px(110), ly, Px(110), Px(30)), L.T("Додати"), delegate { SaveNew(); }, 1, true);
            Btn(g, new Rectangle(di.Right - Px(230), ly, Px(110), Px(30)), L.T("Скасувати"), delegate { adding = false; tbName.Visible = false; }, 0, true);
            ly += Px(40);
        }
        else tbName.Visible = false;

        List<Holiday> list = Holidays.On(sel, c);
        if (list.Count == 0 && !adding)
        {
            // найближчі свята місяця
            List<KeyValuePair<DateTime, Holiday>> up = new List<KeyValuePair<DateTime, Holiday>>();
            DateTime last = month.AddMonths(1);
            for (DateTime d = month; d < last; d = d.AddDays(1))
                foreach (Holiday h in Holidays.On(d, c)) up.Add(new KeyValuePair<DateTime, Holiday>(d, h));
            TxtL(g, up.Count > 0 ? L.T("Свята цього місяця:") : L.T("Цього місяця свят немає"), FS, new Rectangle(di.X, ly, di.Width, Px(20)), P.Sub);
            ly += Px(22);
            int rowH = Px(24);
            int vis = Math.Max(1, (di.Bottom - ly) / rowH);
            listScroll = Math.Max(0, Math.Min(listScroll, up.Count - vis));
            for (int i = listScroll; i < up.Count && ly + rowH <= di.Bottom; i++)
            {
                KeyValuePair<DateTime, Holiday> kv = up[i];
                DateTime dd = kv.Key;
                Rectangle rr = new Rectangle(di.X, ly, di.Width, rowH);
                TxtL(g, dd.ToString("d MMM", Widgets.UK), FB, new Rectangle(rr.X, rr.Y, Px(64), rowH), kv.Value.Mark(P));
                TxtL(g, kv.Value.Name, F, new Rectangle(rr.X + Px(68), rr.Y, rr.Width - Px(68), rowH), P.Fg);
                Hot(rr, delegate { sel = dd; });
                ly += rowH;
            }
            Wheel(dc, delegate (int d) { listScroll -= d; });
        }
        else
        {
            foreach (Holiday h in list)
            {
                if (ly + Px(28) > di.Bottom) break;
                Holiday hh = h;
                using (SolidBrush b = new SolidBrush(h.Mark(P))) g.FillEllipse(b, di.X, ly + Px(9), Px(8), Px(8));
                TxtL(g, h.Name + h.Suffix, F, new Rectangle(di.X + Px(16), ly, di.Width - Px(52), Px(26)), P.Fg);
                if (h.Own)
                    IconBtn(g, new Rectangle(di.Right - Px(28), ly, Px(26), Px(26)), "trash", delegate { Holidays.Own.Remove(hh); Holidays.Save(); }, false);
                ly += Px(28);
            }
        }
        string st = Holidays.Online ? (Holidays.Status.Length > 0 ? Holidays.Status : L.T("Свята: ") + Holidays.CountryName(Holidays.Cc(c))) : L.T("Свята з інтернету вимкнено");
        TxtL(g, st, FS, new Rectangle(r.X, r.Bottom - Px(20), r.Width - Px(150), Px(20)), P.Sub);
        if (Tab == 0)
            Btn(g, new Rectangle(r.Right - Px(140), r.Bottom - Px(24), Px(140), Px(28)), L.T("Налаштування…"), delegate { Bar.OpenSettingsTab(SettingsForm.PgPlace); }, 0, true);
    }

    void SaveNew()
    {
        string n = tbName.Text.Trim();
        if (n.Length == 0) return;
        Holiday h = new Holiday();
        h.Date = sel; h.Name = n; h.Own = true; h.Kind = Holiday.Mine; h.Yearly = addYearly;
        Holidays.Own.Add(h);
        Holidays.Save();
        adding = false;
        tbName.Visible = false;
        Invalidate();
    }
}
