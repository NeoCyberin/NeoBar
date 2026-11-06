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

// ======================================================================
//  Годинник: час і пояс, секундомір, таймер, будильники; сповіщення;
//  годинник/таймер/секундомір на робочому столі (як нотатки)
//  Тут - дані й логіка; віджети на столі - DeskClock.cs, вікно - ClockWindow.cs; налаштування - у вікні налаштувань (SettingsClock.cs).
// ======================================================================
sealed class Alarm
{
    public int H = 7, M = 0;
    public int Days;            // біти: 0 Пн ... 6 Нд; 0 - один раз
    public int MonthDay;        // > 0 - щомісяця цього числа (якщо в місяці менше днів - в останній)
    public string Label = "";
    public string Sound = "theme";    // мелодія цього будильника
    public int SnoozeMin = 5;         // на скільки хвилин він відкладається
    public int Volume = 100;          // гучність мелодії, %
    public bool On = true;
    public DateTime Snooze = DateTime.MinValue, Fired = DateTime.MinValue;

    public static readonly string[] DayNames = { L.T("Пн"), L.T("Вт"), L.T("Ср"), L.T("Чт"), L.T("Пт"), L.T("Сб"), L.T("Нд") };

    public static int Bit(DayOfWeek d) { return 1 << (((int)d + 6) % 7); }

    public string DaysText
    {
        get
        {
            if (MonthDay > 0) return L.F("щомісяця {0}-го", MonthDay);
            if (Days == 0) return L.T("один раз");
            if (Days == 0x7F) return L.T("щодня");
            if (Days == 0x1F) return L.T("у будні");
            if (Days == 0x60) return L.T("у вихідні");
            List<string> l = new List<string>();
            for (int i = 0; i < 7; i++) if ((Days & (1 << i)) != 0) l.Add(DayNames[i]);
            return string.Join(", ", l.ToArray());
        }
    }

    public Alarm Clone() { return (Alarm)MemberwiseClone(); }

    public bool Once { get { return Days == 0 && MonthDay == 0; } }

    // чи дзвонить цього дня
    public bool OnDay(DateTime d)
    {
        if (MonthDay > 0) return d.Day == Math.Min(MonthDay, DateTime.DaysInMonth(d.Year, d.Month));
        return Days == 0 || (Days & Bit(d.DayOfWeek)) != 0;
    }

    public DateTime Next(DateTime now)
    {
        for (int i = 0; i < 64; i++)
        {
            DateTime d = now.Date.AddDays(i).AddHours(H).AddMinutes(M);
            if (d <= now) continue;
            if (OnDay(d)) return d;
        }
        return DateTime.MaxValue;
    }
}

// один таймер зі списку (їх може бути кілька)
sealed class TimerItem
{
    public TimeSpan Set = TimeSpan.FromMinutes(5);
    public DateTime End = DateTime.MinValue;       // MinValue - не йде
    public TimeSpan Paused = TimeSpan.Zero;        // > 0 - на паузі
    public string Label = "";
    public string Sound = "theme";                 // мелодія цього таймера
    public int Volume = 100;                       // гучність, %

    public bool Running { get { return End != DateTime.MinValue; } }
    public bool IsPaused { get { return !Running && Paused > TimeSpan.Zero; } }

    public TimeSpan Left
    {
        get
        {
            if (Running) { TimeSpan l = End - DateTime.Now; return l < TimeSpan.Zero ? TimeSpan.Zero : l; }
            if (Paused > TimeSpan.Zero) return Paused;
            return Set;
        }
    }
}

sealed class DeskSpec
{
    public int Tm;                    // для таймера: номер таймера у списку
    public string Kind = "clock";     // clock | analog | timer | stopwatch
    public int X = 200, Y = 200, Size = 1, Opacity = 88;
    public bool Locked;
    public float Scale;               // розмір, що тягнеться за куточок; 0 - за готовим розміром Size
    public string Opt = "";           // особисті параметри: монітор - cpu,gpu,ram; диски - літери (C:,D:); погода - us (американська тема)
    public int Numbers = 1;           // стрілковий: 0 без цифр, 1 - 12·3·6·9, 2 - 1…12, 3 - 1…12 і 13…24
    public DeskClock Form;
}

// Точність годинника: перевірка відхилення (NTP) і синхронізація. Стан читає вікно налаштувань.
static class TimeTools
{
    public static volatile string NtpText = "", SyncMsg = "";
    public static volatile bool NtpBusy;
    public static DateTime NtpAt = DateTime.MinValue;
    public static string SyncFile;

    public static void CheckNtp()
    {
        if (NtpBusy) return;
        NtpBusy = true;
        NtpAt = DateTime.Now;
        Util.Bg(delegate
        {
            string s;
            try
            {
                double off, rtt; string srv;
                bool ok = Ntp.Query(out off, out rtt, out srv);
                if (!ok) s = L.T("Немає зв'язку із сервером часу (UDP 123).");
                else if (Math.Abs(off) < 0.5) s = L.T("Годинник точний: відхилення ") + Math.Abs(off).ToString("0.00", Widgets.UK) + L.T(" с (") + srv + ")";
                else s = (off > 0 ? L.T("Годинник відстає на ") : L.T("Годинник поспішає на ")) + Math.Abs(off).ToString(Math.Abs(off) < 60 ? "0.0" : "0", Widgets.UK) + L.T(" с (") + srv + ")";
            }
            catch { s = L.T("Не вдалося перевірити час."); }
            NtpText = s;
            NtpBusy = false;
        });
    }

    // викликається періодично: підхоплює результат синхронізації з правами адміністратора
    public static void Poll()
    {
        if (SyncFile == null) return;
        bool done;
        string o = Elevated.ReadOut(SyncFile, out done);
        if (!done) return;
        SyncFile = null;
        SyncMsg = o.Trim().Length > 0 ? LastLine(o) : L.T("Синхронізацію виконано.");
        CheckNtp();
    }

    static string LastLine(string s)
    {
        string[] l = s.Replace("\r", "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return l.Length > 0 ? l[l.Length - 1].Trim() : "";
    }

    public static void SyncNow()
    {
        if (Elevated.IsAdmin)
        {
            SyncMsg = L.T("Синхронізація…");
            Util.Bg(delegate
            {
                string msg;
                TimeSync.Run(out msg);
                SyncMsg = msg;
                CheckNtp();
            });
            return;
        }
        string err;
        SyncFile = Elevated.Run("ntp", "", out err);
        if (SyncFile == null) NtpText = err;
    }
}

static class ClockData
{
    public static readonly List<Alarm> Alarms = new List<Alarm>();
    public static readonly List<TimerItem> Timers = new List<TimerItem>();
    public static int SelTimer;                              // вибраний у вікні таймер
    public const int MaxTimers = 8;
    public static DateTime SwStart = DateTime.MinValue;      // MinValue - стоїть
    public static TimeSpan SwAcc = TimeSpan.Zero;
    public static readonly List<TimeSpan> Laps = new List<TimeSpan>();
    public static readonly List<DeskSpec> Desk = new List<DeskSpec>();
    public static bool AutoTz;
    public static string AlarmSound = "theme", TimerSound = "theme";
    public static int SnoozeMin = 5;      // типове відкладення для нових будильників
    public static bool SyncOnStart;
    public static int AnalogNumbers = 1;     // цифри нових стрілкових годинників на столі
    public static string SyncNote = "";
    static readonly DateTime started = DateTime.Now;

    static NeoBar bar;
    static System.Windows.Forms.Timer tick;
    static string path;
    static int lastMinute = -1;
    public static event Action Changed;

    public static NeoBar Bar { get { return bar; } }

    public static void Init(NeoBar b)
    {
        bar = b;
        path = Prof.File("clock.ini");
        Load();
        EnsureTimers();
        tick = new System.Windows.Forms.Timer();
        tick.Interval = 500;
        tick.Tick += delegate { Check(); };
        tick.Start();
        ShowDesk();
        if (SyncOnStart)
        {
            // синхронізація після запуску (коли вже є мережа)
            System.Windows.Forms.Timer st = new System.Windows.Forms.Timer();
            st.Interval = 20000;
            st.Tick += delegate
            {
                st.Stop(); st.Dispose();
                if (!Elevated.IsAdmin) { SyncNote = L.T("Автосинхронізація пропущена: панель запущена без прав адміністратора."); return; }
                Util.Bg(delegate
                {
                    string msg;
                    TimeSync.Run(out msg);
                    try { bar.BeginInvoke(new Action(delegate { SyncNote = L.T("Під час запуску: ") + msg; Fire(); })); } catch { }
                });
            };
            st.Start();
        }
    }

    // ---------- стан ----------

    // завжди є щонайменше один таймер
    public static void EnsureTimers()
    {
        if (Timers.Count == 0) { TimerItem t = new TimerItem(); t.Label = L.T("Таймер 1"); Timers.Add(t); }
        SelTimer = Math.Max(0, Math.Min(SelTimer, Timers.Count - 1));
    }

    public static TimerItem CurTimer { get { EnsureTimers(); return Timers[SelTimer]; } }

    public static TimerItem TimerOf(DeskSpec d)
    {
        EnsureTimers();
        return Timers[Math.Max(0, Math.Min(d.Tm, Timers.Count - 1))];
    }

    // типова назва: найменший вільний номер ("Таймер 2", ...)
    public static string NextName(string prefix, IEnumerable<string> used)
    {
        List<string> u = new List<string>(used);
        for (int n = 1; n < 1000; n++)
            if (!u.Contains(prefix + " " + n)) return prefix + " " + n;
        return prefix;
    }

    public static string TimerName(TimerItem t)
    {
        if (t.Label.Length > 0) return t.Label;
        EnsureTimers();
        return L.T("Таймер ") + (Timers.IndexOf(t) + 1);
    }

    public static TimerItem AddTimer()
    {
        EnsureTimers();
        if (Timers.Count >= MaxTimers) return null;
        TimerItem t = new TimerItem();
        List<string> names = new List<string>();
        foreach (TimerItem x in Timers) names.Add(x.Label);
        t.Label = NextName(L.T("Таймер"), names);
        Timers.Add(t);
        SelTimer = Timers.Count - 1;
        Save(); Fire();
        return t;
    }

    public static void RemoveTimer(TimerItem t)
    {
        if (Timers.Count <= 1) return;
        int at = Timers.IndexOf(t);
        if (at < 0) return;
        Timers.RemoveAt(at);
        foreach (DeskSpec d in Desk) { if (d.Tm == at) d.Tm = 0; else if (d.Tm > at) d.Tm--; }
        SelTimer = Math.Max(0, Math.Min(SelTimer, Timers.Count - 1));
        Save(); Fire();
    }

    public static void TimerStartPause(TimerItem t)
    {
        if (t.Running) { t.Paused = t.Left; t.End = DateTime.MinValue; }
        else
        {
            TimeSpan left = t.Paused > TimeSpan.Zero ? t.Paused : t.Set;
            if (left <= TimeSpan.Zero) return;
            t.End = DateTime.Now + left;
            t.Paused = TimeSpan.Zero;
        }
        Save(); Fire();
    }

    public static void TimerReset(TimerItem t)
    {
        t.End = DateTime.MinValue; t.Paused = TimeSpan.Zero;
        Save(); Fire();
    }

    public static bool SwRunning { get { return SwStart != DateTime.MinValue; } }
    public static TimeSpan SwElapsed { get { return SwAcc + (SwRunning ? DateTime.Now - SwStart : TimeSpan.Zero); } }

    public static void SwStartPause()
    {
        if (SwRunning) { SwAcc += DateTime.Now - SwStart; SwStart = DateTime.MinValue; }
        else SwStart = DateTime.Now;
        Save(); Fire();
    }

    public static void SwLap()
    {
        if (!SwRunning) return;
        Laps.Insert(0, SwElapsed);
        if (Laps.Count > 99) Laps.RemoveAt(Laps.Count - 1);
        Save(); Fire();
    }

    public static void SwReset()
    {
        SwStart = DateTime.MinValue; SwAcc = TimeSpan.Zero; Laps.Clear();
        Save(); Fire();
    }

    static void Fire()
    {
        Action a = Changed;
        if (a != null) try { a(); } catch { }
        foreach (DeskSpec d in Desk) if (d.Form != null && !d.Form.IsDisposed) d.Form.Redraw();
    }

    // секундомір: години, хвилини, секунди й мілісекунди
    public static string FmtMs(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return ((int)t.TotalHours).ToString("00") + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00") + "." + t.Milliseconds.ToString("000");
    }

    public static string Fmt(TimeSpan t, bool tenths)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        string s = t.TotalHours >= 1 ? ((int)t.TotalHours).ToString("00") + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00")
                                     : t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
        if (tenths) s += "," + (t.Milliseconds / 100);
        return s;
    }

    // ---------- перевірка будильників і таймера ----------

    static void Check()
    {
        DateTime now = DateTime.Now;
        foreach (TimerItem t in Timers.ToArray())
            if (t.Running && now >= t.End)
            {
                t.End = DateTime.MinValue;
                t.Paused = TimeSpan.Zero;
                Save();
                Toast(TimerName(t), L.T("Минуло ") + Fmt(t.Set, false), true, null, t.Sound, SnoozeMin, t.Volume);
                Fire();
            }
        foreach (Alarm a in Alarms.ToArray())
        {
            if (a.Snooze != DateTime.MinValue && now >= a.Snooze)
            {
                a.Snooze = DateTime.MinValue;
                RingAlarm(a);
                continue;
            }
            if (!a.On || now.Hour != a.H || now.Minute != a.M) continue;
            if (!a.Once && !a.OnDay(now)) continue;
            if (a.Fired.Date == now.Date && a.Fired.Hour == now.Hour && a.Fired.Minute == now.Minute) continue;
            a.Fired = now;
            if (a.Once) a.On = false;
            Save();
            RingAlarm(a);
        }
        HolidayNotice(now);
        Seasons.Tick(bar);
        // віджети на столі: раз на секунду (секундомір - частіше)
        int m = now.Second;
        foreach (DeskSpec d in Desk)
            if (d.Form != null && !d.Form.IsDisposed && m != lastMinute && !NeoBar.Paused) d.Form.TickRedraw();
        lastMinute = m;
    }

    // Сповіщення про свята: о заданій годині або через кілька хвилин після запуску, якщо ПК увімкнули пізніше
    static void HolidayNotice(DateTime now)
    {
        if (!Holidays.NotifyOn || Holidays.LastNotified.Date == now.Date) return;
        if (now.TimeOfDay < TimeSpan.FromMinutes(Holidays.NotifyAt)) return;
        if ((now - started).TotalMinutes < Holidays.NotifyDelay && started.Date == now.Date && started.TimeOfDay >= TimeSpan.FromMinutes(Holidays.NotifyAt)) return;
        List<Holiday> l;
        try { l = Holidays.TryOn(now.Date, bar != null ? bar.Config : null); } catch { return; }
        if (l == null) return;    // свята з інтернету ще вантажаться
        Holidays.LastNotified = now.Date;
        Holidays.Save();
        if (l.Count == 0) return;
        List<string> n = new List<string>();
        foreach (Holiday h in l) n.Add(h.Name);
        // коли панель саме вітає святковою темою (ефект і звук), сповіщення мовчить: інакше звуки перебивають один одного
        if (bar != null) ToastForm.Show(bar, L.T("Сьогодні свято"), string.Join(" · ", n.ToArray()), "bell", false, null, Seasons.Greeting(bar) ? "none" : null);
    }

    static void RingAlarm(Alarm a)
    {
        Alarm al = a;
        Toast(L.T("Будильник ") + al.H.ToString("00") + ":" + al.M.ToString("00"), al.Label.Length > 0 ? al.Label : L.T("Час прокидатися!"), true,
            delegate { al.Snooze = DateTime.Now.AddMinutes(al.SnoozeMin); Save(); }, al.Sound, al.SnoozeMin, al.Volume);
        Fire();
    }

    static void Toast(string title, string body, bool alarm, Action snooze)
    {
        Toast(title, body, alarm, snooze, null);
    }

    static void Toast(string title, string body, bool alarm, Action snooze, string sound)
    {
        Toast(title, body, alarm, snooze, sound, SnoozeMin);
    }

    static void Toast(string title, string body, bool alarm, Action snooze, string sound, int snoozeMin)
    {
        Toast(title, body, alarm, snooze, sound, snoozeMin, 100);
    }

    static void Toast(string title, string body, bool alarm, Action snooze, string sound, int snoozeMin, int volume)
    {
        if (bar == null) return;
        ToastForm.Show(bar, title, body, alarm ? "bell" : "check", alarm, snooze, sound, snoozeMin, volume);
    }

    // ---------- часовий пояс за місцем ----------

    // Порівняння з даними сайту (пояс IANA і поточне зміщення з урахуванням літнього часу)
    public static bool TzMatches(out string text)
    {
        text = "";
        string loc = Widgets.WTz;
        if (loc.Length == 0) { text = L.T("Дані сайту про пояс ще не отримано."); return true; }
        TimeZoneInfo local = TimeZoneInfo.Local;
        TimeSpan sysOff = TimeSpan.Zero;
        try { sysOff = local.GetUtcOffset(DateTime.Now); } catch { }
        string win = TzMap.ToWindows(loc);
        string dst = "";
        TimeSpan siteOff = TimeSpan.FromSeconds(Widgets.WTzOffset);
        bool haveOff = Widgets.WTzOffset != int.MinValue;
        if (win != null)
        {
            try
            {
                TimeZoneInfo z = TimeZoneInfo.FindSystemTimeZoneById(win);
                if (haveOff && z.SupportsDaylightSavingTime) dst = siteOff > z.BaseUtcOffset ? L.T("літній час") : L.T("зимовий час");
            }
            catch { }
        }
        string off = haveOff ? (siteOff < TimeSpan.Zero ? "UTC−" : "UTC+") + siteOff.Duration().Hours.ToString("00") + ":" + siteOff.Duration().Minutes.ToString("00") : "";
        text = L.T("Сайт (") + Widgets.CityLabel(bar != null ? bar.Config : new Cfg()) + "): " + loc +
            (Widgets.WTzAbbr.Length > 0 ? " · " + Widgets.WTzAbbr : "") + (off.Length > 0 ? " · " + off : "") + (dst.Length > 0 ? " · " + dst : "");
        bool idOk = win == null || string.Equals(win, local.Id, StringComparison.OrdinalIgnoreCase);
        bool offOk = !haveOff || Math.Abs((sysOff - siteOff).TotalMinutes) < 1;
        if (!offOk) text += L.T(" - система показує інше зміщення");
        return idOk && offOk;
    }

    static bool tzIpBusy;

    // автоматичний пояс: лише якщо користувач дозволив визначати розташування; якщо поясу від погоди ще немає
    // (немає віджета погоди чи служба розташування вимкнена) - беремо пояс за IP-адресою
    public static bool TzAllowed { get { return bar != null && bar.Config.LocAllow; } }

    public static void CheckTz()
    {
        if (!AutoTz || !TzAllowed) return;
        if (Widgets.WTz.Length > 0) { ApplyTz(false); return; }
        if (tzIpBusy) return;
        tzIpBusy = true;
        Util.Bg(delegate
        {
            double la, lo; string city, cc, tz;
            bool ok = Geo.IpLocation(out la, out lo, out city, out cc, out tz);
            try
            {
                bar.BeginInvoke(new Action(delegate
                {
                    tzIpBusy = false;
                    if (ok && tz.Length > 0 && Widgets.WTz.Length == 0) { Widgets.WTz = tz; if (Geo.Source.Length == 0) Geo.Source = "ip"; }
                    if (Widgets.WTz.Length > 0) ApplyTz(false);
                }));
            }
            catch { tzIpBusy = false; }
        });
    }

    // ручний вибір поясу Windows
    public static void SetTzManual(string id, string name)
    {
        Util.Bg(delegate
        {
            bool ok = SysTz.Set(id);
            try
            {
                if (bar != null) bar.BeginInvoke(new Action(delegate
                {
                    Toast(L.T("Часовий пояс"), ok ? L.T("Встановлено: ") + name : L.T("Не вдалося змінити часовий пояс."), false, null);
                    Fire();
                }));
            }
            catch { }
        });
    }

    // Встановлює пояс за місцем (tzutil також вмикає автоматичний перехід на літній/зимовий час)
    public static void ApplyTz(bool force)
    {
        if (bar == null) return;
        string txt;
        if (TzMatches(out txt) && !force) return;
        string win = TzMap.ToWindows(Widgets.WTz);
        if (string.IsNullOrEmpty(win)) return;
        Util.Bg(delegate
        {
            bool ok = SysTz.Set(win);
            try
            {
                bar.BeginInvoke(new Action(delegate
                {
                    Toast(L.T("Часовий пояс"), ok ? L.T("Встановлено пояс за місцем: ") + Widgets.WTz + L.T(" (з переходом на літній/зимовий час)") : L.T("Не вдалося змінити часовий пояс."), false, null);
                    Fire();
                }));
            }
            catch { }
        });
    }

    // ---------- збереження ----------

    static string Ts(TimeSpan t) { return t.Ticks.ToString(CultureInfo.InvariantCulture); }
    static string Dt(DateTime d) { return d.Ticks.ToString(CultureInfo.InvariantCulture); }

    public static void Save()
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(L.T("# NeoBar - годинник: будильники, таймер, секундомір, віджети на робочому столі"));
            sb.AppendLine("autotz=" + (AutoTz ? 1 : 0));
            sb.AppendLine("syncstart=" + (SyncOnStart ? 1 : 0));
            sb.AppendLine("numbers=" + AnalogNumbers);
            foreach (TimerItem t in Timers)
                sb.AppendLine("timer=" + Ts(t.Set) + "|" + Dt(t.End) + "|" + Ts(t.Paused) + "|" + t.Label.Replace("|", "/").Replace("\n", " ").Replace("\r", "") + "|" + t.Sound + "|" + t.Volume);
            sb.AppendLine("swstart=" + Dt(SwStart));
            sb.AppendLine("swacc=" + Ts(SwAcc));
            foreach (TimeSpan l in Laps) sb.AppendLine("lap=" + Ts(l));
            foreach (Alarm a in Alarms)
                sb.AppendLine("alarm=" + a.H + "|" + a.M + "|" + a.Days + "|" + (a.On ? 1 : 0) + "|" + Dt(a.Snooze) + "|" + a.Label.Replace("|", "/").Replace("\n", " ") + "|" + a.MonthDay + "|" + a.Sound + "|" + a.SnoozeMin + "|" + a.Volume);
            foreach (DeskSpec d in Desk)
                sb.AppendLine("desk=" + d.Kind + "|" + d.X + "|" + d.Y + "|" + d.Size + "|" + d.Opacity + "|" + (d.Locked ? 1 : 0) + "|" + d.Numbers + "|" + d.Tm + "|" + (d.Opt ?? "").Replace("|", "").Replace("\n", "") + "|" + d.Scale.ToString("0.###", CultureInfo.InvariantCulture));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }
        catch { }
    }

    static long LongOf(string s) { long v; return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0; }
    static int I(string s) { int v; return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0; }

    static void Load()
    {
        string[] lines;
        try { if (!File.Exists(path)) return; lines = File.ReadAllLines(path, Encoding.UTF8); }
        catch { return; }
        foreach (string raw in lines)
        {
            int eq = raw.IndexOf('=');
            if (eq <= 0 || raw.StartsWith("#")) continue;
            string k = raw.Substring(0, eq).Trim(), v = raw.Substring(eq + 1);
            try
            {
                switch (k)
                {
                    case "autotz": AutoTz = v.Trim() == "1"; break;
                    case "alarmsound": AlarmSound = Chime.Normalize(v.Trim()); break;
                    case "syncstart": SyncOnStart = v.Trim() == "1"; break;
                    case "numbers": AnalogNumbers = Math.Max(0, Math.Min(3, I(v))); break;
                    case "snoozemin": SnoozeMin = Math.Max(1, Math.Min(60, I(v))); break;
                    case "timersound": TimerSound = Chime.Normalize(v.Trim()); break;
                    case "timer":
                    {
                        string[] tp = v.Split('|');
                        if (tp.Length < 3) break;
                        TimerItem ti = new TimerItem();
                        ti.Set = new TimeSpan(LongOf(tp[0])); ti.End = new DateTime(LongOf(tp[1])); ti.Paused = new TimeSpan(LongOf(tp[2]));
                        if (tp.Length > 3) ti.Label = tp[3].Trim();
                        ti.Sound = tp.Length > 4 ? Chime.Normalize(tp[4].Trim()) : "theme";
                        if (tp.Length > 5) ti.Volume = Math.Max(0, Math.Min(100, I(tp[5])));
                        Timers.Add(ti);
                        break;
                    }
                    case "swstart": SwStart = new DateTime(LongOf(v)); break;
                    case "swacc": SwAcc = new TimeSpan(LongOf(v)); break;
                    case "lap": Laps.Add(new TimeSpan(LongOf(v))); break;
                    case "alarm":
                    {
                        string[] p = v.Split('|');
                        if (p.Length < 4) break;
                        Alarm a = new Alarm();
                        a.H = Math.Max(0, Math.Min(23, I(p[0]))); a.M = Math.Max(0, Math.Min(59, I(p[1])));
                        a.Days = I(p[2]) & 0x7F; a.On = p[3] == "1";
                        if (p.Length > 4) a.Snooze = new DateTime(LongOf(p[4]));
                        if (p.Length > 5) a.Label = p[5];
                        if (p.Length > 6) a.MonthDay = Math.Max(0, Math.Min(31, I(p[6])));
                        a.Sound = p.Length > 7 ? Chime.Normalize(p[7].Trim()) : "theme";
                        if (p.Length > 9) a.Volume = Math.Max(0, Math.Min(100, I(p[9])));
                        a.SnoozeMin = p.Length > 8 ? Math.Max(1, Math.Min(60, I(p[8]))) : Math.Max(1, Math.Min(60, SnoozeMin));
                        Alarms.Add(a);
                        break;
                    }
                    case "desk":
                    {
                        string[] p = v.Split('|');
                        if (p.Length < 3) break;
                        DeskSpec d = new DeskSpec();
                        d.Kind = p[0]; d.X = I(p[1]); d.Y = I(p[2]);
                        if (p.Length > 3) d.Size = Math.Max(0, Math.Min(2, I(p[3])));
                        if (p.Length > 4) d.Opacity = Math.Max(20, Math.Min(100, I(p[4])));
                        if (p.Length > 5) d.Locked = p[5] == "1";
                        if (p.Length > 6) d.Numbers = Math.Max(0, Math.Min(3, I(p[6])));
                        if (p.Length > 7) d.Tm = Math.Max(0, I(p[7]));
                        if (p.Length > 8) d.Opt = p[8].Trim();
                        float sc9;
                        if (p.Length > 9 && float.TryParse(p[9], NumberStyles.Float, CultureInfo.InvariantCulture, out sc9)) d.Scale = sc9 > 0 ? Math.Max(0.5f, Math.Min(3.5f, sc9)) : 0;
                        Desk.Add(d);
                        break;
                    }
                }
            }
            catch { }
        }
    }

    // ---------- віджети на робочому столі ----------

    public static void ShowDesk()
    {
        foreach (DeskSpec d in Desk)
            if (d.Form == null || d.Form.IsDisposed)
            {
                d.Form = new DeskClock(d);
                d.Form.Place();
            }
    }

    public static void AddDesk(string kind) { AddDesk(kind, null); }

    public static void AddDesk(string kind, string opt)
    {
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        DeskSpec d = new DeskSpec();
        d.Kind = kind;
        if (kind == "analog") d.Numbers = AnalogNumbers;
        if (opt != null) d.Opt = opt;
        else if (kind == "perf")
        {
            // новий монітор на столі починає з того, що вибрано у вікні монітора
            List<string> pl = new List<string>();
            if (Widgets.PerfCpu) pl.Add("cpu");
            if (Widgets.PerfGpu) pl.Add("gpu");
            if (Widgets.PerfRam) pl.Add("ram");
            d.Opt = pl.Count == 3 ? "" : string.Join(",", pl.ToArray());
        }
        if (kind == "timer") d.Tm = SelTimer;
        d.X = wa.Left + wa.Width / 2 - 120 + Desk.Count * 24;
        d.Y = wa.Top + 120 + Desk.Count * 24;
        Desk.Add(d);
        Save();
        ShowDesk();
        if (bar != null) bar.RefreshDataNeeds();
    }

    public static void RemoveDesk(DeskSpec d)
    {
        Desk.Remove(d);
        if (d.Form != null && !d.Form.IsDisposed) d.Form.Close();
        d.Form = null;
        Save();
        if (bar != null) bar.RefreshDataNeeds();
    }

    // нова тема панелі - перемалювати віджети
    public static void RedrawKind(string kind)
    {
        foreach (DeskSpec d in Desk) if (d.Kind == kind && d.Form != null && !d.Form.IsDisposed) d.Form.Redraw();
    }

    public static void Restyle()
    {
        foreach (DeskSpec d in Desk) if (d.Form != null && !d.Form.IsDisposed) d.Form.Redraw();
    }

    // цифри стрілкових годинників (усіх на столі й нових)
    public static void SetAnalogNumbers(int n)
    {
        AnalogNumbers = Math.Max(0, Math.Min(3, n));
        foreach (DeskSpec d in Desk) if (d.Kind == "analog") { d.Numbers = AnalogNumbers; if (d.Form != null && !d.Form.IsDisposed) d.Form.Redraw(); }
        Save();
    }

    public static void SetWeatherTheme(string opt)
    {
        bool any = false;
        foreach (DeskSpec d in Desk) if (d.Kind == "weather" && d.Opt != opt) { d.Opt = opt; any = true; if (d.Form != null && !d.Form.IsDisposed) d.Form.Redraw(); }
        if (any) Save();
    }

    // чи показує якийсь монітор на столі відеокарту (тоді потрібен повний збір даних)
    public static bool DeskPerfGpu()
    {
        foreach (DeskSpec d in Desk) if (d.Kind == "perf" && DeskClock.PerfOf(d)[1]) return true;
        return false;
    }

    // які джерела даних потрібні віджетам на столі (погода, мережа, монітор)
    public static bool DeskHas(string kind)
    {
        foreach (DeskSpec d in Desk) if (d.Kind == kind) return true;
        return false;
    }

    public static string KindTitle(string k)
    {
        switch (k)
        {
            case "analog": return L.T("Годинник зі стрілками");
            case "timer": return L.T("Таймер");
            case "stopwatch": return L.T("Секундомір");
            case "alarm": return L.T("Найближчий будильник");
            case "calendar": return L.T("Календар");
            case "clockcal": return L.T("Годинник з календарем");
            case "weather": return L.T("Погода");
            case "net": return L.T("Мережа");
            case "perf": return L.T("Монітор системи");
            case "disk": return L.T("Простір");
            case "trash": return L.T("Кошик (очищення)");
            case "fw": return L.T("Брандмауер");
        }
        return L.T("Годинник");
    }
}
