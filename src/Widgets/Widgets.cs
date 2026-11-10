using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

using System.Text.RegularExpressions;
// ======================================================================
//  Віджети: годинник, дата, погода, монітор системи
// ======================================================================
static class Widgets
{
    static CultureInfo uk;

    public static CultureInfo UK
    {
        get
        {
            if (uk == null)
            {
                // назви місяців і днів - мовою інтерфейсу
                try { uk = new CultureInfo(L.Code == "uk" ? "uk-UA" : L.Code); }
                catch { try { uk = new CultureInfo("en-US"); } catch { uk = CultureInfo.InvariantCulture; } }
            }
            return uk;
        }
    }

    // ---------- дані ----------
    public static int BatPct = -1;
    public static long NetDown, NetUp;
    public static bool BatCharging, BatPresent, BatOnAc;
    public static bool BatDead { get { return BatPresent && BatOnAc && !BatCharging && BatPct >= 0 && BatPct <= 3; } }  // від мережі, а заряд не росте
    static long lastRx, lastTx;
    static DateTime lastNet = DateTime.MinValue;

    // диски: id тому (GUID або літера) -> останні дані
    public static readonly HashSet<string> DiskIds = new HashSet<string>();
    static readonly Dictionary<string, Vol> disks = new Dictionary<string, Vol>();
    static readonly Dictionary<string, DateTime> diskAt = new Dictionary<string, DateTime>();

    public static double Temp = double.NaN, Wind, FeelsLike = double.NaN, Pressure = double.NaN;
    public static int WCode = -1, Hum;
    public static bool WDay = true, WStale;
    public static DateTime WUpdated = DateTime.MinValue, WSaved = DateTime.MinValue;
    public static string WErr = "", WTz = "", WTzAbbr = "";
    public static int WTzOffset = int.MinValue;     // зміщення від UTC за даними сайту, с

    // прогноз на тиждень і погодинний
    public sealed class Day
    {
        public DateTime Date, Rise, Set;
        public int Code = -1;
        public double Max = double.NaN, Min = double.NaN, Rain, RainProb = double.NaN, Wind, Uv = double.NaN;
    }
    public static List<Day> Days = new List<Day>();
    public static List<DateTime> HourAt = new List<DateTime>();
    public static List<double> HourT = new List<double>(), HourRain = new List<double>();
    public static List<int> HourCode = new List<int>();
    static volatile bool wBusy;
    public static bool GeoChanged;          // панель має зберегти місто/координати в ini

    // які рядки показує віджет монітора (ЦП, ГП, ОЗП); хоч один завжди увімкнений
    public static bool PerfCpu = true, PerfGpu = true, PerfRam = true;
    public static int PerfN { get { return Math.Max(1, (PerfCpu ? 1 : 0) + (PerfGpu ? 1 : 0) + (PerfRam ? 1 : 0)); } }
    public static void PerfSet(Cfg c) { PerfCpu = c.PerfCpu; PerfGpu = c.PerfGpu; PerfRam = c.PerfRam; if (!PerfCpu && !PerfGpu && !PerfRam) PerfCpu = true; }

    public static void Tick(HashSet<string> kinds)
    {
        try
        {
            if (kinds.Contains("disk")) ReadDisks(false);
            if (kinds.Contains("net")) ReadNet();
            if (kinds.Contains("battery")) ReadBattery();
        }
        catch { }
    }

    // id томів віджета (через кому); порожньо - системний диск
    public static List<string> DiskKeys(Entry e)
    {
        List<string> l = new List<string>();
        string opt = e != null ? (e.Opt ?? "").Trim() : "";
        foreach (string p in opt.Split(','))
            if (p.Trim().Length > 0 && !l.Contains(p.Trim())) l.Add(p.Trim());
        if (l.Count == 0) l.Add(Vols.SysRoot());
        return l;
    }

    public static string DiskKey(Entry e) { return DiskKeys(e)[0]; }

    public static List<Vol> DisksOf(Entry e)
    {
        List<Vol> l = new List<Vol>();
        lock (disks) foreach (string k in DiskKeys(e)) { Vol v; disks.TryGetValue(k, out v); l.Add(v); }
        return l;
    }

    public static int Len(Entry e, bool horizontal, int cell, int itemLen, float sc)
    {
        if (e.Target == "disk" && !horizontal)
        {
            int n = DiskKeys(e).Count;
            if (n > 1) return (int)(8 * sc) + n * (int)(30 * sc);
        }
        return Len(e.Target, horizontal, cell, itemLen, sc);
    }

    public static Vol DiskOf(Entry e)
    {
        Vol v;
        lock (disks) disks.TryGetValue(DiskKey(e), out v);
        return v;
    }

    static volatile bool diskBusy;
    static bool diskForce;
    // місце на дисках перечитано (викликається з фонового потоку)
    public static event Action DisksUpdated;

    // Читання дисків - у фоновому потоці: сплячий жорсткий диск або мережевий том можуть відповідати
    // кілька секунд, і раніше на цей час підвисала вся панель
    public static void ReadDisks(bool force)
    {
        if (diskBusy) { if (force) diskForce = true; return; }
        if (diskForce) { diskForce = false; force = true; }
        DateTime now = DateTime.Now;
        List<string> ids = new List<string>();
        foreach (string id in DiskIds)
        {
            DateTime at;
            if (!force && diskAt.TryGetValue(id, out at) && (now - at).TotalSeconds < 15) continue;
            ids.Add(id);
        }
        if (ids.Count == 0) return;
        foreach (string id in ids) diskAt[id] = now;
        Dictionary<string, Vol> snap;
        lock (disks) snap = new Dictionary<string, Vol>(disks);
        diskBusy = true;
        Util.Bg(delegate
        {
            try
            {
                foreach (string id in ids)
                {
                    Vol old;
                    snap.TryGetValue(id, out old);
                    Vol v = null;
                    try { v = Vols.Quick(id, old != null ? old.Root : ""); } catch { }
                    lock (disks) disks[id] = v;
                }
            }
            finally { diskBusy = false; }
            Action a = DisksUpdated;
            if (a != null) try { a(); } catch { }
        });
    }

    static void ReadNet()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (System.Net.NetworkInformation.NetworkInterface ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                System.Net.NetworkInformation.IPv4InterfaceStatistics st = ni.GetIPv4Statistics();
                rx += st.BytesReceived;
                tx += st.BytesSent;
            }
        }
        catch { }
        DateTime now = DateTime.Now;
        if (lastNet != DateTime.MinValue)
        {
            double dt = (now - lastNet).TotalSeconds;
            if (dt > 0.2)
            {
                NetDown = Math.Max(0, (long)((rx - lastRx) / dt));
                NetUp = Math.Max(0, (long)((tx - lastTx) / dt));
            }
        }
        lastRx = rx; lastTx = tx; lastNet = now;
    }

    static void ReadBattery()
    {
        try
        {
            PowerStatus ps = SystemInformation.PowerStatus;
            BatPresent = ps.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery;
            BatPct = BatPresent ? (int)Math.Round(ps.BatteryLifePercent * 100) : -1;
            BatOnAc = ps.PowerLineStatus == PowerLineStatus.Online;
            BatCharging = (ps.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;     // справді заряджається, а не просто від мережі
        }
        catch { BatPresent = false; }
    }

    // ---------- підписи для виявлення змін (щоб не перемальовувати зайве) ----------
    public static string Sig(string kind, Cfg c)
    {
        switch (kind)
        {
            case "clock": return DateTime.Now.ToString("HHmm");
            case "date": return DateTime.Now.ToString("yyyyMMdd");
            case "weather": return Temp + "|" + WCode + "|" + WUpdated.Ticks + "|" + WErr + "|" + wBusy;
            case "perf": return (PerfCpu ? Sensors.Cpu.ToString() : "") + "|" + (PerfGpu ? Sensors.GpuLoad.ToString() : "") + "|" + (PerfRam ? Sensors.Ram.ToString() : "");
            case "net": return (NetDown >> 7) + "|" + (NetUp >> 7) + "|" + PingMon.LastAt.Ticks;
            case "battery": return BatPct + "|" + BatCharging + "|" + BatOnAc;
            case "notes": return Notes.Count + "|" + Notes.LockedCount;
            case "color": return ColorMath.Hex(ColorState.Cur);
            case "dice": return Dice.Sig;
            case "clean": return Cleaner.LastFound + "|" + Cleaner.Busy;
            case "security": return SecScan.LastAt.Ticks + "|" + SecScan.Busy;
        }
        return "";
    }

    public static string Sig(Entry e, Cfg c)
    {
        if (e.Target == "disk")
        {
            StringBuilder sb = new StringBuilder();
            foreach (Vol v in DisksOf(e)) sb.Append(v == null ? "none" : v.Root + "|" + (v.Free >> 24) + "|" + v.Label).Append(";");
            return sb.ToString();
        }
        return Sig(e.Target, c);
    }

    public static int Len(string kind, bool horizontal, int cell, int itemLen, float sc)
    {
        if (horizontal) return cell;
        switch (kind)
        {
            case "perf": return (int)(itemLen * (0.3 + 0.35 * PerfN)) + (int)(4 * sc);
            case "date":
            case "weather": return itemLen;
            case "net": return itemLen + (int)(4 * sc);
            default: return itemLen - (int)(10 * sc);
        }
    }

    public static string Tip(Entry e, Cfg c)
    {
        string kind = e.Target;
        switch (kind)
        {
            case "clock":
            {
                string s = DateTime.Now.ToString("dddd, d MMMM yyyy", UK);
                foreach (TimerItem ti in ClockData.Timers) if (ti.Running) s += "\n" + ClockData.TimerName(ti) + ": " + ClockData.Fmt(ti.Left, false);
                if (ClockData.SwRunning) s += L.T("\nСекундомір: ") + ClockData.Fmt(ClockData.SwElapsed, false);
                return s + L.T("\nКлік - годинник, таймер, будильник");
            }
            case "date":
            {
                string s = DateTime.Now.ToString("dddd, d MMMM yyyy", UK) + L.T("\nТиждень ") + Week(DateTime.Now);
                string h = Holidays.TodayText();
                if (h.Length > 0) s += "\n" + h;
                return s + L.T("\nКлік - календар");
            }
            case "disk":
            {
                StringBuilder sb = new StringBuilder();
                List<Vol> vl = DisksOf(e);
                for (int i = 0; i < vl.Count; i++)
                {
                    Vol v = vl[i];
                    if (v == null) sb.Append(L.T("Диск не підключено\n"));
                    else sb.Append(v.Title + L.T(": вільно ") + Vols.Size(v.Free) + L.T(" з ") + Vols.Size(v.Total) + "\n");
                }
                return sb + L.T("Клік - стан і обслуговування");
            }
            case "battery":
                return BatPresent ? L.T("Батарея: ") + BatPct + "%" + (BatCharging ? L.T(" (заряджається)") : (BatDead ? L.T(" - від мережі, але не заряджається") : (BatOnAc ? L.T(" (від мережі)") : ""))) + L.T("\nКлік - стан батареї") : L.T("Батареї немає");
            case "perf":
            {
                List<string> pt = new List<string>();
                if (PerfCpu) pt.Add(L.T("ЦП ") + Sensors.F0(Sensors.Cpu, "%"));
                if (PerfGpu) pt.Add(L.T("ГП ") + Sensors.F0(Sensors.GpuLoad, "%") + (float.IsNaN(Sensors.GpuTemp) ? "" : ", " + Sensors.F0(Sensors.GpuTemp, "°") + (float.IsNaN(Sensors.Hotspot) ? "" : " (hotspot " + Sensors.F0(Sensors.Hotspot, "°") + ")")));
                if (PerfRam) pt.Add(L.T("ОЗП ") + Sensors.F0(Sensors.Ram, "%"));
                return string.Join(", ", pt.ToArray()) + L.T("\nКлік - монітор системи");
            }
            case "weather":
            {
                if (double.IsNaN(Temp)) return WErr.Length > 0 ? L.T("Погода: ") + WErr : L.T("Погода: завантаження…");
                string s = CityLabel(c) + ": " + FmtTemp(c) + ", " + WeatherText(WCode) + L.T("\nВітер ") + WindText(c.Fahrenheit) + L.T(", вологість ") + Hum + "%";
                if (!double.IsNaN(FeelsLike)) s += L.T("\nВідчувається як ") + Deg(FeelsLike, c.Fahrenheit);
                return s + L.T("\nКлік - прогноз на тиждень");
            }
            case "net": return L.T("Мережа: ↓ ") + Rate(NetDown) + "  ↑ " + Rate(NetUp) + L.T("\nПінг ") + PingMon.Title + ": " + PingMon.Text + (PingMon.Detail.Length > 0 ? " (" + PingMon.Detail + ")" : "") + L.T("\nКлік - мережа, пінг, адаптери та Wi-Fi");
            case "color": return L.T("Палітра: ") + ColorMath.Hex(ColorState.Cur) + " (" + ColorMath.NearestName(ColorState.Cur) + L.T(")\nКлік - палітра, піпетка й колірні моделі");
            case "dice":
            {
                int[] dv = Dice.Shown;
                return (dv.Length == 0 ? L.T("Кубик: ще не кидали") : L.T("Кубик: ") + Dice.Text(dv)) + L.T("\nКлік - налаштування кубика");
            }
            case "qr": return L.T("QR-код: текст, посилання, Wi-Fi, контакт та інше\nКлік - створити QR-код");
            case "recover": return L.T("Відновлення видалених файлів і папок з диска чи флешки\nКлік - вибрати диск і знайти файли");
            case "security":
            {
                if (SecScan.Busy) return L.T("Безпека: перевірка…");
                if (SecScan.LastAt == DateTime.MinValue) return L.T("Безпека: ще не перевіряли\nКлік - перевірити процеси, автозапуск, служби й налаштування");
                int sb, sw;
                SecScan.Count(out sb, out sw);
                return L.T("Безпека (о ") + SecScan.LastAt.ToString("HH:mm") + "): " + (sb + sw == 0 ? L.T("підозрілого не знайдено") : (sb > 0 ? L.T("підозріло - ") + sb : "") + (sb > 0 && sw > 0 ? ", " : "") + (sw > 0 ? L.T("варто перевірити - ") + sw : "")) + L.T("\nКлік - подробиці");
            }
            case "crypt": return L.T("Шифрування тексту й файлів, хеш-суми\nКлік - відкрити (можна й перетягнути файл у вікно)");
            case "clean": return (Cleaner.LastFound >= 0 ? L.T("Сміття: ") + Cleaner.Size(Cleaner.LastFound) + L.T(" (перевірено о ") + Cleaner.LastAt.ToString("HH:mm") + ")" : L.T("Очищення Windows від сміття")) + L.T("\nКлік - аналіз і очищення");
            case "notes": return L.T("Нотатки на робочому столі: ") + Notes.Count + (Notes.LockedCount > 0 ? L.T(" (на шпалерах: ") + Notes.LockedCount + ")" : "") + L.T("\nКлік - створити, заблокувати або розблокувати");
        }
        return "";
    }

    public static int Week(DateTime d)
    {
        return UK.Calendar.GetWeekOfYear(d, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
    }

    public static string RateText(long bps) { return Rate(bps); }

    static string Rate(long bps)
    {
        if (bps < 1024) return bps + L.T(" Б/с");
        if (bps < 1048576) return (bps / 1024.0).ToString("0.0") + L.T(" КБ/с");
        return (bps / 1048576.0).ToString("0.0") + L.T(" МБ/с");
    }

    public static string CityLabel(Cfg c)
    {
        return c.CityName.Length > 0 ? c.CityName : c.City;
    }

    static string FmtTemp(Cfg c)
    {
        return TempText(Temp, c.Fahrenheit);
    }

    // дані зберігаються в метричних одиницях; тема віджета (європейська / американська) лише перераховує для показу
    public static double Conv(double celsius, bool us) { return us ? celsius * 9.0 / 5.0 + 32.0 : celsius; }

    public static string Deg(double celsius, bool us)
    {
        return double.IsNaN(celsius) ? "—" : Math.Round(Conv(celsius, us)).ToString(CultureInfo.InvariantCulture) + "°";
    }

    public static string TempText(double celsius, bool us)
    {
        return double.IsNaN(celsius) ? "—" : Deg(celsius, us) + (us ? "F" : "C");
    }

    public static string WindText(bool us)
    {
        return us ? Math.Round(Wind * 0.621371) + L.T(" миль/год") : Math.Round(Wind) + L.T(" км/год");
    }

    public static string PressText(bool us)
    {
        if (double.IsNaN(Pressure)) return "—";
        return us ? (Pressure * 0.02953).ToString("0.00", CultureInfo.InvariantCulture) + " inHg" : Math.Round(Pressure * 0.750062) + L.T(" мм рт. ст.");
    }

    public static string WeatherText(int code)
    {
        switch (code)
        {
            case 0: return L.T("ясно");
            case 1: return L.T("переважно ясно");
            case 2: return L.T("мінлива хмарність");
            case 3: return L.T("похмуро");
            case 45: case 48: return L.T("туман");
            case 51: case 53: case 55: return L.T("мряка");
            case 56: case 57: return L.T("паморозна мряка");
            case 61: return L.T("невеликий дощ");
            case 63: return L.T("дощ");
            case 65: return L.T("сильний дощ");
            case 66: case 67: return L.T("крижаний дощ");
            case 71: return L.T("невеликий сніг");
            case 73: return L.T("сніг");
            case 75: return L.T("сильний сніг");
            case 77: return L.T("снігова крупа");
            case 80: case 81: case 82: return L.T("зливи");
            case 85: case 86: return L.T("снігопад");
            case 95: return L.T("гроза");
            case 96: case 99: return L.T("гроза з градом");
        }
        return "—";
    }

    // ---------- мережа: геокодування та прогноз (Open-Meteo, без ключа) ----------

    static string Http(string url)
    {
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(url);
        rq.Timeout = 9000;
        rq.ReadWriteTimeout = 9000;
        rq.UserAgent = "NeoBar/1.0";
        using (WebResponse rs = rq.GetResponse())
        using (StreamReader sr = new StreamReader(rs.GetResponseStream(), Encoding.UTF8))
            return sr.ReadToEnd();
    }

    static string Num(double d) { return d.ToString("0.####", CultureInfo.InvariantCulture); }

    static double Field(string json, string name)
    {
        Match m = Regex.Match(json, "\"" + name + "\"\\s*:\\s*(-?[0-9.]+(?:[eE][-+]?[0-9]+)?)");
        double d;
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return double.NaN;
    }

    public static bool WeatherLookup(string city, out double lat, out double lon, out string name)
    {
        lat = lon = double.NaN;
        name = city;
        try
        {
            string js = Http("https://geocoding-api.open-meteo.com/v1/search?count=1&language=uk&format=json&name=" +
                             Uri.EscapeDataString(city));
            Match r = Regex.Match(js, "\"results\"\\s*:\\s*\\[\\s*\\{(.*?)\\}", RegexOptions.Singleline);
            if (!r.Success) return false;
            string o = r.Groups[1].Value;
            lat = Field(o, "latitude");
            lon = Field(o, "longitude");
            Match n = Regex.Match(o, "\"name\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (n.Success) name = Regex.Unescape(n.Groups[1].Value);
            return !double.IsNaN(lat) && !double.IsNaN(lon);
        }
        catch { return false; }
    }

    // Підказки міст: до 8 кандидатів. Кожен — {опис, name, lat, lon}
    public static List<string[]> WeatherSuggest(string q)
    {
        List<string[]> res = new List<string[]>();
        if (q == null || q.Trim().Length < 3) return res;
        try
        {
            string js = Http("https://geocoding-api.open-meteo.com/v1/search?count=8&language=uk&format=json&name=" +
                             Uri.EscapeDataString(q.Trim()));
            foreach (Match m in Regex.Matches(js, "\\{(?<o>[^{}]*)\\}"))
            {
                string o = m.Groups["o"].Value;
                if (o.IndexOf("\"latitude\"", StringComparison.Ordinal) < 0) continue;
                double lat = Field(o, "latitude"), lon = Field(o, "longitude");
                if (double.IsNaN(lat) || double.IsNaN(lon)) continue;
                string name = Str(o, "name"), adm = Str(o, "admin1"), ctry = Str(o, "country");
                if (name.Length == 0) continue;
                string disp = name;
                if (adm.Length > 0 && adm != name) disp += ", " + adm;
                if (ctry.Length > 0) disp += ", " + ctry;
                res.Add(new string[] { disp, name, Num(lat), Num(lon), Str(o, "country_code") });
                if (res.Count >= 8) break;
            }
        }
        catch { }
        return res;
    }

    static string Str(string json, string name)
    {
        Match m = Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? Regex.Unescape(m.Groups[1].Value) : "";
    }

    public static bool WeatherBusy { get { return wBusy; } }

    // Анімація оновлення погоди: дані плавно згасають, поки вантажаться, і з'являються вже оновленими
    static DateTime wfStart = DateTime.MinValue, wfEnd = DateTime.MinValue;
    const double WfOut = 320, WfIn = 520;
    const float WfLow = 0.18f;

    static float Smooth(double t) { t = Math.Max(0, Math.Min(1, t)); return (float)(t * t * (3 - 2 * t)); }

    // 1 - повна яскравість; менше - під час згасання / появи
    public static float WeatherFade
    {
        get
        {
            if (wfStart == DateTime.MinValue) return 1f;
            DateTime now = DateTime.Now;
            double since = (now - wfStart).TotalMilliseconds;
            float lvl = 1f - (1f - WfLow) * Smooth(since / WfOut);
            if (wfEnd != DateTime.MinValue && since >= WfOut)
            {
                double from = Math.Max(0, (now - wfEnd).TotalMilliseconds);
                if (from >= WfIn) { wfStart = DateTime.MinValue; return 1f; }
                lvl = WfLow + (1f - WfLow) * Smooth(from / WfIn);
            }
            return lvl;
        }
    }

    public static bool WeatherFadeActive { get { float f = WeatherFade; return wfStart != DateTime.MinValue; } }

    public static bool WeatherDue()
    {
        return !wBusy && (DateTime.Now - WUpdated).TotalMinutes > 30;
    }

    // Оновлює погоду й прогноз у фоні; done викликається з фонового потоку (панель сама переходить в UI-потік).
    // Відповідь кешується: без інтернету показується останній збережений прогноз.
    public static void WeatherRefresh(Cfg c, Action done)
    {
        if (wBusy) return;
        wBusy = true;
        wfStart = DateTime.Now; wfEnd = DateTime.MinValue;
        string city = c.City;
        double lat = c.Lat, lon = c.Lon;
        Util.Bg(delegate
        {
            try
            {
                if (double.IsNaN(lat) || double.IsNaN(lon))
                {
                    string nm;
                    if (!WeatherLookup(city, out lat, out lon, out nm)) throw new Exception(L.T("місто не знайдено"));
                    c.Lat = lat; c.Lon = lon; c.CityName = nm;
                    GeoChanged = true;
                }
                string url = "https://api.open-meteo.com/v1/forecast?latitude=" + Num(lat) + "&longitude=" + Num(lon) +
                    "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,is_day,surface_pressure" +
                    "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,precipitation_sum,wind_speed_10m_max,sunrise,sunset,uv_index_max" +
                    "&hourly=temperature_2m,weather_code,precipitation_probability&forecast_days=8&timezone=auto";
                bool stale;
                string js = Encoding.UTF8.GetString(NetCache.Get(url, url, TimeSpan.FromMinutes(25), out stale, null));
                Parse(js);
                WStale = stale;
                WSaved = NetCache.SavedAt(url);
                WUpdated = DateTime.Now;
                WErr = "";
                if (stale) WUpdated = DateTime.Now.AddMinutes(-25);   // без зв'язку - повторимо за ~5 хв
            }
            catch (Exception ex)
            {
                SbLog.W(SbLog.Widgets, "погода: помилка оновлення: " + ex.Message);
                WErr = ex.Message.Length > 60 ? L.T("немає зв'язку") : ex.Message;
                if (WErr.IndexOf("remote", StringComparison.OrdinalIgnoreCase) >= 0 || WErr.IndexOf("name", StringComparison.OrdinalIgnoreCase) >= 0)
                    WErr = L.T("немає зв'язку");
                WUpdated = DateTime.Now.AddMinutes(-25);   // повторимо за ~5 хв
            }
            wBusy = false;
            wfEnd = DateTime.Now;
            try { if (done != null) done(); } catch { }
        });
    }

    static void Parse(string js)
    {
        string cur = Js.Obj(js, "current");
        if (cur.Length == 0) throw new Exception(L.T("порожня відповідь"));
        Temp = Field(cur, "temperature_2m");
        FeelsLike = Field(cur, "apparent_temperature");
        double v = Field(cur, "weather_code");
        WCode = double.IsNaN(v) ? -1 : (int)v;
        v = Field(cur, "relative_humidity_2m");
        Hum = double.IsNaN(v) ? 0 : (int)v;
        Wind = Field(cur, "wind_speed_10m");
        Pressure = Field(cur, "surface_pressure");
        v = Field(cur, "is_day");
        WDay = double.IsNaN(v) || v > 0;
        string tz = Js.Str(js, "timezone");
        if (tz.Length > 0 && tz != "GMT") WTz = tz;
        WTzAbbr = Js.Str(js, "timezone_abbreviation");
        if (WTzAbbr.StartsWith("GMT")) WTzAbbr = "";
        double uo = Js.Num(js, "utc_offset_seconds");
        if (!double.IsNaN(uo)) WTzOffset = (int)uo;

        List<Day> days = new List<Day>();
        string d = Js.Obj(js, "daily");
        string[] dt = Js.Strs(d, "time"), rise = Js.Strs(d, "sunrise"), set = Js.Strs(d, "sunset");
        double[] code = Js.Nums(d, "weather_code"), mx = Js.Nums(d, "temperature_2m_max"), mn = Js.Nums(d, "temperature_2m_min"),
                 pp = Js.Nums(d, "precipitation_probability_max"), ps = Js.Nums(d, "precipitation_sum"), wm = Js.Nums(d, "wind_speed_10m_max"),
                 uv = Js.Nums(d, "uv_index_max");
        for (int i = 0; i < dt.Length; i++)
        {
            Day x = new Day();
            if (!DateTime.TryParseExact(dt[i], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out x.Date)) continue;
            x.Code = i < code.Length && !double.IsNaN(code[i]) ? (int)code[i] : -1;
            x.Max = At(mx, i); x.Min = At(mn, i); x.RainProb = At(pp, i); x.Rain = At(ps, i); x.Wind = At(wm, i); x.Uv = At(uv, i);
            if (i < rise.Length) DateTime.TryParse(rise[i], CultureInfo.InvariantCulture, DateTimeStyles.None, out x.Rise);
            if (i < set.Length) DateTime.TryParse(set[i], CultureInfo.InvariantCulture, DateTimeStyles.None, out x.Set);
            days.Add(x);
        }
        string h = Js.Obj(js, "hourly");
        string[] ht = Js.Strs(h, "time");
        double[] htemp = Js.Nums(h, "temperature_2m"), hcode = Js.Nums(h, "weather_code"), hp = Js.Nums(h, "precipitation_probability");
        List<DateTime> at = new List<DateTime>();
        List<double> tl = new List<double>(), rl = new List<double>();
        List<int> cl = new List<int>();
        for (int i = 0; i < ht.Length; i++)
        {
            DateTime t;
            if (!DateTime.TryParse(ht[i], CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) continue;
            at.Add(t); tl.Add(At(htemp, i)); rl.Add(At(hp, i));
            cl.Add(i < hcode.Length && !double.IsNaN(hcode[i]) ? (int)hcode[i] : -1);
        }
        Days = days; HourAt = at; HourT = tl; HourRain = rl; HourCode = cl;
    }

    static double At(double[] a, int i) { return i < a.Length ? a[i] : double.NaN; }

    // ---------- малювання ----------

    static Font PxFont(Font basis, float px, FontStyle st)
    {
        using (FontFamily ff = new FontFamily(basis.Name))
        {
            FontStyle s = ff.IsStyleAvailable(st) ? st : FontStyle.Regular;
            return new Font(ff, Math.Max(6f, px), s, GraphicsUnit.Pixel);
        }
    }

    static readonly TextFormatFlags TF = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
        TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;

    // найбільший розмір шрифту (px), за якого текст влазить у ширину
    static Font FitFont(Font basis, string text, float maxPx, int maxW, FontStyle st)
    {
        float s = maxPx;
        Font f = PxFont(basis, s, st);
        while (s > 7f && TextRenderer.MeasureText(text, f, new Size(10000, 1000), TextFormatFlags.NoPadding).Width > maxW)
        {
            f.Dispose();
            s -= 1f;
            f = PxFont(basis, s, st);
        }
        return f;
    }

    static void Txt(Graphics g, string text, Font f, Rectangle r, Color col)
    {
        TextRenderer.DrawText(g, text, f, r, col, TF);
    }

    static Color LoadColor(int pct, Color ok, Color mid)
    {
        if (pct >= 90) return Color.FromArgb(230, 70, 70);
        if (pct >= 70) return mid;
        return ok;
    }

    static void Bar(Graphics g, Rectangle r, int pct, Color fill, Color back)
    {
        pct = Math.Max(0, Math.Min(100, pct));
        using (GraphicsPath pb = Util.Round(r, r.Height / 2))
        using (SolidBrush bb = new SolidBrush(back))
            g.FillPath(bb, pb);
        int w = Math.Max(pct > 0 ? r.Height : 0, r.Width * pct / 100);
        if (w <= 0) return;
        Rectangle fr = new Rectangle(r.X, r.Y, w, r.Height);
        using (GraphicsPath pf = Util.Round(fr, r.Height / 2))
        using (SolidBrush fb = new SolidBrush(fill))
            g.FillPath(fb, pf);
    }

    public static void Draw(Graphics g, Rectangle r0, Entry e, Theme t, Cfg c, Font font, Color text, bool hover, float sc)
    {
        string kind = e.Target;
        int pad = (int)Math.Round(3 * sc);
        Rectangle r = Rectangle.Inflate(r0, -pad, -(int)Math.Round(1 * sc));
        Color plate = t.Hover.A < 255 ? Color.FromArgb(Math.Max(28, t.Hover.A / 2), t.Hover) : Color.FromArgb(hover ? 255 : 150, t.Hover);
        if (hover) plate = Color.FromArgb(Math.Min(255, plate.A + 60), plate);
        using (SolidBrush pb = new SolidBrush(plate))
            Util.FillRound(g, pb, r, t.Radius);
        if (t.Sketchy)
            using (Pen pn = new Pen(Color.FromArgb(150, t.Edge), 1.2f))
                Sk.Line(g, pn, r.X + 2, r.Bottom - 1, r.Right - 2, r.Bottom - 1, new Random(r.Y), 1.2f);

        Color sec = Util.Blend(Util.Opaque(t.Bg), text, 0.62f);
        Color acc = Util.Opaque(t.Accent);
        Color back = Color.FromArgb(70, Util.Blend(Util.Opaque(t.Bg), text, 0.4f));
        Rectangle inner = Rectangle.Inflate(r, -(int)(4 * sc), -(int)(3 * sc));
        int w = inner.Width, h = inner.Height;

        switch (kind)
        {
            case "clock":
            {
                DateTime now = DateTime.Now;
                // на вузькій панелі - без секунд (секунди - на столі й у розширеній панелі)
                string tm = c.Clock24 ? now.ToString("HH:mm", CultureInfo.InvariantCulture)
                                      : now.ToString("h:mm", CultureInfo.InvariantCulture);
                using (Font big = FitFont(font, tm, h * 0.5f, w, FontStyle.Bold))
                    Txt(g, tm, big, new Rectangle(inner.X, inner.Y + (int)(h * 0.04f), w, (int)(h * 0.58f)), text);
                string ds = c.Clock24 ? now.ToString("ddd, d MMM", UK) : now.ToString("tt", CultureInfo.InvariantCulture) + " " + now.ToString("d MMM", UK);
                using (Font sm = FitFont(font, ds, h * 0.2f, w, FontStyle.Regular))
                    Txt(g, ds, sm, new Rectangle(inner.X, inner.Y + (int)(h * 0.62f), w, (int)(h * 0.3f)), sec);
                break;
            }
            case "date":
            {
                DateTime now = DateTime.Now;
                string mon = now.ToString("MMM", UK).ToUpper(UK);
                using (Font sm = FitFont(font, mon, h * 0.22f, w, FontStyle.Bold))
                    Txt(g, mon, sm, new Rectangle(inner.X, inner.Y, w, (int)(h * 0.26f)), acc);
                string dn = now.Day.ToString();
                using (Font big = FitFont(font, "88", h * 0.5f, w, FontStyle.Bold))
                    Txt(g, dn, big, new Rectangle(inner.X, inner.Y + (int)(h * 0.22f), w, (int)(h * 0.48f)), text);
                string wd = now.ToString("dddd", UK);
                using (Font sm2 = FitFont(font, wd, h * 0.2f, w, FontStyle.Regular))
                    Txt(g, wd, sm2, new Rectangle(inner.X, inner.Y + (int)(h * 0.7f), w, (int)(h * 0.26f)), sec);
                break;
            }
            case "weather":
            {
                float wf = WeatherFade;
                Color bgc = Util.Opaque(t.Bg);
                if (wf < 0.999f) { text = Util.Blend(bgc, text, wf); sec = Util.Blend(bgc, sec, wf); }
                bool wErrShown = WErr.Length > 0 && double.IsNaN(Temp);
                int isz = (int)Math.Min(w * 0.55f, h * (wErrShown ? 0.44f : 0.5f));
                int th0 = (int)(h * 0.34f);
                int y0 = wErrShown ? inner.Y : inner.Y + Math.Max(0, (h - isz - th0) / 2);   // без міста - вміст по центру плитки
                Rectangle ir = new Rectangle(inner.X + (w - isz) / 2, y0, isz, isz);
                if (WCode >= 0) DrawWeather(g, ir, WCode, WDay, t, text, wf);
                else Txt(g, wBusy ? "…" : "?", font, ir, sec);
                string tt = FmtTemp(c);
                using (Font big = FitFont(font, tt, h * 0.32f, w, FontStyle.Bold))
                    Txt(g, tt, big, new Rectangle(inner.X, wErrShown ? inner.Y + (int)(h * 0.44f) : y0 + isz, w, th0), text);
                // місто на панелі не пишемо (воно - у розширеній панелі); лише помилку, якщо даних немає
                if (WErr.Length > 0 && double.IsNaN(Temp))
                    using (Font sm = FitFont(font, WErr, h * 0.18f, w, FontStyle.Regular))
                        Txt(g, WErr, sm, new Rectangle(inner.X, inner.Y + (int)(h * 0.76f), w, (int)(h * 0.22f)), sec);
                break;
            }
            case "disk":
                if (DiskKeys(e).Count > 1)
                {
                    List<Vol> vl = DisksOf(e);
                    int rh = h / vl.Count;
                    using (Font sm = FitFont(font, L.T("C: 888 ГБ"), Math.Min(rh * 0.42f, 13 * sc), w, FontStyle.Regular))
                        for (int i = 0; i < vl.Count; i++)
                        {
                            Vol v = vl[i];
                            int y = inner.Y + i * rh;
                            string lab = v == null ? "—" : (v.Letter.Length > 0 ? v.Letter : "?") + " " + FreeShort(v.Free);
                            Txt(g, lab, sm, new Rectangle(inner.X, y, w, (int)(rh * 0.58f)), v == null ? sec : text);
                            Rectangle br = new Rectangle(inner.X + (int)(3 * sc), y + (int)(rh * 0.62f), w - (int)(6 * sc), Math.Max(3, (int)(rh * 0.2f)));
                            int up = v != null ? v.UsedPct : 0;
                            Bar(g, br, Math.Max(0, up), LoadColor(up, acc, Color.FromArgb(240, 170, 40)), back);
                        }
                    break;
                }
                goto case "battery";
            case "battery":
            {
                int pct; string label, val;
                if (kind == "disk")
                {
                    Vol v = DiskOf(e);
                    pct = v != null ? v.UsedPct : -1;
                    label = v != null ? (v.Letter.Length > 0 ? L.T("Диск ") + v.Letter : L.T("Диск")) : L.T("Диск");
                    val = v != null ? FreeShort(v.Free) : "—";
                }
                else
                {
                    pct = BatPresent ? BatPct : -1;
                    label = BatCharging ? L.T("Заряд ⚡") : (BatDead ? L.T("Батарея ⚠") : L.T("Батарея")); val = BatPresent ? BatPct + "%" : "—";
                    if (BatDead) { text = Color.FromArgb(235, 80, 70); sec = text; }
                }
                using (Font sm = FitFont(font, label, h * 0.2f, w, FontStyle.Regular))
                    Txt(g, label, sm, new Rectangle(inner.X, inner.Y, w, (int)(h * 0.26f)), sec);
                using (Font big = FitFont(font, val, h * 0.36f, w, FontStyle.Bold))
                    Txt(g, val, big, new Rectangle(inner.X, inner.Y + (int)(h * 0.26f), w, (int)(h * 0.4f)), text);
                Rectangle br = new Rectangle(inner.X + (int)(2 * sc), inner.Y + (int)(h * 0.74f), w - (int)(4 * sc), Math.Max(4, (int)(h * 0.14f)));
                Bar(g, br, Math.Max(0, pct), kind == "battery" ? (pct < 20 && !BatCharging ? Color.FromArgb(230, 70, 70) : acc) : LoadColor(pct, acc, Color.FromArgb(240, 170, 40)), back);
                break;
            }
            case "net":
            {
                string d = "↓ " + Rate(NetDown), u = "↑ " + Rate(NetUp);
                float nf = PingMon.Fade.Level;
                Color nbg = Util.Opaque(t.Bg);
                if (nf < 0.999f) { acc = Util.Blend(nbg, acc, nf); text = Util.Blend(nbg, text, nf); }
                using (Font sm = FitFont(font, L.T("↓ 88.8 КБ/с"), h * 0.22f, w, FontStyle.Bold))
                {
                    Txt(g, d, sm, new Rectangle(inner.X, inner.Y, w, (int)(h * 0.32f)), acc);
                    Txt(g, u, sm, new Rectangle(inner.X, inner.Y + (int)(h * 0.32f), w, (int)(h * 0.32f)), text);
                }
                Color pq = PingMon.Quality(Color.FromArgb(80, 200, 110), Color.FromArgb(240, 170, 40), Color.FromArgb(230, 70, 70));
                if (nf < 0.999f) pq = Util.Blend(nbg, pq, nf);
                string pt = PingMon.LastAt == DateTime.MinValue ? L.T("пінг…") : (double.IsNaN(PingMon.Last) ? L.T("немає") : Math.Round(PingMon.Last) + L.T(" мс"));
                using (Font sm2 = FitFont(font, L.T("● 888 мс"), h * 0.22f, w, FontStyle.Regular))
                    Txt(g, "● " + pt, sm2, new Rectangle(inner.X, inner.Y + (int)(h * 0.66f), w, (int)(h * 0.32f)), pq);
                break;
            }
            case "color":
            {
                ColorState.Load(e.Opt);
                Color cc = ColorState.Cur;
                float s = Math.Min(w * 0.62f, h * 0.5f);
                RectangleF cr = new RectangleF(inner.X + (w - s) / 2f, inner.Y + h * 0.04f, s, s);
                SmoothingMode smc = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // маленьке колесо тонів навколо зразка поточного кольору
                for (int k = 0; k < 12; k++)
                    using (SolidBrush kb = new SolidBrush(ColorMath.FromHsv(k * 30, 0.75, 1)))
                        g.FillPie(kb, cr.X, cr.Y, s, s, -90 + k * 30, 30);
                float ins = s * 0.2f;
                RectangleF mid = new RectangleF(cr.X + ins, cr.Y + ins, s - ins * 2, s - ins * 2);
                using (SolidBrush mb = new SolidBrush(Util.Opaque(t.Bg))) g.FillEllipse(mb, RectangleF.Inflate(mid, s * 0.04f, s * 0.04f));
                using (SolidBrush mb = new SolidBrush(cc)) g.FillEllipse(mb, mid);
                g.SmoothingMode = smc;
                string hx = ColorMath.Hex(cc);
                using (Font sm = FitFont(font, hx, h * 0.22f, w, FontStyle.Regular))
                    Txt(g, hx, sm, new Rectangle(inner.X, inner.Y + (int)(h * 0.6f), w, (int)(h * 0.34f)), sec);
                break;
            }
            case "qr":
            {
                // значок: три «шукачі» й кілька модулів
                float s = Math.Max(6 * sc, Math.Min(h * 0.56f, w * 0.8f));
                float x0 = inner.X + (w - s) / 2f, y0 = inner.Y + h * 0.03f, m = s / 7f;
                SmoothingMode smq = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.None;
                using (SolidBrush qb = new SolidBrush(text))
                using (SolidBrush pb2 = new SolidBrush(plate.A > 200 ? plate : Util.Opaque(t.Bg)))
                {
                    PointF[] corners = { new PointF(x0, y0), new PointF(x0 + s - 3 * m, y0), new PointF(x0, y0 + s - 3 * m) };
                    foreach (PointF c0 in corners)
                    {
                        g.FillRectangle(qb, c0.X, c0.Y, 3 * m, 3 * m);
                        g.FillRectangle(pb2, c0.X + m * 0.6f, c0.Y + m * 0.6f, m * 1.8f, m * 1.8f);
                        g.FillRectangle(qb, c0.X + m, c0.Y + m, m, m);
                    }
                    int[] mods = { 4, 0, 3, 3, 5, 3, 4, 4, 6, 4, 3, 5, 5, 6, 6, 6, 4, 6, 6, 5 };
                    for (int k = 0; k + 1 < mods.Length; k += 2) g.FillRectangle(qb, x0 + mods[k] * m, y0 + mods[k + 1] * m, m, m);
                }
                g.SmoothingMode = smq;
                using (Font sm = FitFont(font, L.T("QR-код"), h * 0.22f, w, FontStyle.Regular))
                    Txt(g, L.T("QR-код"), sm, new Rectangle(inner.X, inner.Y + (int)(h * 0.62f), w, (int)(h * 0.34f)), sec);
                break;
            }
            case "recover":
            {
                // значок: диск і кругла стрілка «повернути» у плашці
                float s = Math.Max(6 * sc, Math.Min(h * 0.52f, w * 0.74f));
                float x0 = inner.X + (w - s) / 2f, y0 = inner.Y + h * 0.04f;
                Ico.Draw(g, "drive", new RectangleF(x0, y0, s * 0.86f, s * 0.86f), text);
                SmoothingMode smr = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float bd = s * 0.54f, bx = x0 + s - bd, by = y0 + s - bd, rr = bd * 0.27f, bcx = bx + bd / 2f, bcy = by + bd / 2f;
                using (SolidBrush pb = new SolidBrush(plate.A > 200 ? plate : Util.Opaque(t.Bg))) g.FillEllipse(pb, bx - s * 0.03f, by - s * 0.03f, bd + s * 0.06f, bd + s * 0.06f);
                using (SolidBrush ab = new SolidBrush(acc)) g.FillEllipse(ab, bx, by, bd, bd);
                Color ink = Util.Opaque(t.Bg);
                using (Pen ap = new Pen(ink, Math.Max(1.2f, bd * 0.11f)))
                {
                    g.DrawArc(ap, bcx - rr, bcy - rr, rr * 2, rr * 2, -50, -280);
                    double ea = 30 * Math.PI / 180;          // кінець дуги й напрямок руху проти годинникової
                    float ex = bcx + rr * (float)Math.Cos(ea), ey = bcy + rr * (float)Math.Sin(ea);
                    float tx = (float)Math.Sin(ea), ty = -(float)Math.Cos(ea), nx = (float)Math.Cos(ea), ny = (float)Math.Sin(ea), hl = rr * 0.9f, hw = rr * 0.55f;
                    using (SolidBrush hb = new SolidBrush(ink))
                        g.FillPolygon(hb, new PointF[] { new PointF(ex + tx * hl, ey + ty * hl), new PointF(ex + nx * hw, ey + ny * hw), new PointF(ex - nx * hw, ey - ny * hw) });
                }
                g.SmoothingMode = smr;
                using (Font sm = FitFont(font, L.T("Відновлення"), h * 0.22f, w, FontStyle.Regular))
                    Txt(g, L.T("Відновлення"), sm, new Rectangle(inner.X, inner.Y + (int)(h * 0.66f), w, (int)(h * 0.32f)), sec);
                break;
            }
            case "security":
            {
                int sb, sw;
                SecScan.Count(out sb, out sw);
                bool done = SecScan.LastAt != DateTime.MinValue && !SecScan.Busy;
                Color shc = SecScan.Busy ? acc : (!done ? text : (sb > 0 ? Color.FromArgb(235, 85, 85) : (sw > 0 ? Color.FromArgb(240, 175, 50) : Color.FromArgb(70, 200, 110))));
                float s = Math.Max(6 * sc, Math.Min(h * 0.5f, w * 0.7f));
                Ico.Draw(g, "shield", new RectangleF(inner.X + (w - s) / 2f, inner.Y + h * 0.06f, s, s), shc);
                string cap3 = SecScan.Busy ? "…" : (!done ? L.T("Безпека") : (sb + sw == 0 ? L.T("Гаразд") : (sb + sw).ToString()));
                using (Font sm = FitFont(font, cap3, h * 0.22f, w, FontStyle.Regular))
                    Txt(g, cap3, sm, new Rectangle(inner.X, inner.Y + (int)(h * 0.62f), w, (int)(h * 0.34f)), done && sb + sw > 0 ? shc : sec);
                break;
            }
            case "crypt":
            {
                float s = Math.Max(6 * sc, Math.Min(h * 0.5f, w * 0.7f));
                Ico.Draw(g, "key", new RectangleF(inner.X + (w - s) / 2f, inner.Y + h * 0.06f, s, s), text);
                using (Font sm = FitFont(font, L.T("Шифрування"), h * 0.22f, w, FontStyle.Regular))
                    Txt(g, L.T("Шифрування"), sm, new Rectangle(inner.X, inner.Y + (int)(h * 0.62f), w, (int)(h * 0.34f)), sec);
                break;
            }
            case "clean":
            {
                float s = Math.Max(6 * sc, Math.Min(h * 0.5f, w * 0.7f));
                Ico.Draw(g, "trash", new RectangleF(inner.X + (w - s) / 2f, inner.Y + h * 0.06f, s, s), Cleaner.Busy ? acc : text);
                string cap2 = Cleaner.Busy ? "…" : (Cleaner.LastFound >= 0 ? Cleaner.Size(Cleaner.LastFound) : L.T("Очищення"));
                using (Font sm = FitFont(font, cap2, h * 0.22f, w, FontStyle.Regular))
                    Txt(g, cap2, sm, new Rectangle(inner.X, inner.Y + (int)(h * 0.62f), w, (int)(h * 0.34f)), Cleaner.LastFound > (1L << 30) ? acc : sec);
                break;
            }
            case "dice":
            {
                int[] dv = Dice.Shown;
                int n = Math.Max(1, Dice.Count);
                bool lab = n == 1 && dv.Length == 1 && !Dice.Rolling && Dice.LabelOf(dv[0]).Length > 0;
                // на панелі - один кубик: результат (для кількох кубиків - сума або їхня кількість);
                // розмір і місце - як у значків інших віджетів (над підписом, по центру)
                float s = Math.Max(6 * sc, Math.Min(h * 0.52f, w * 0.7f));
                Color face = Util.Blend(Util.Opaque(t.Bg), text, Util.Lum(Util.Opaque(t.Bg)) > 140 ? 0.06f : 0.2f);
                Color ink = text;
                bool ready = dv.Length > 0 && !Dice.Rolling;
                int tv = 0; string tl = null;
                if (ready)
                {
                    if (n == 1) tv = dv[0];
                    else if (Dice.Total) tl = Dice.Sum(dv).ToString();
                    else tl = "x" + n;
                }
                using (Font nf = FitFont(font, "88", s * 0.55f, (int)(s * 0.86f), FontStyle.Bold))
                    Dice.DrawDie(g, new RectangleF(inner.X + (w - s) / 2f, inner.Y + h * 0.05f, s, s), tv, face, ink, nf, tl);
                // підпис: тип кубика (к4, к6, к20…) - однаково для всіх; число - на самому кубику
                string dk = Dice.Locked ? L.T("Кубик") : (n > 1 ? n + L.T("к") + Dice.Faces : L.T("к") + Dice.Faces);
                string cap = Dice.Rolling ? "…" : (lab ? Dice.LabelOf(dv[0])
                    : (n > 1 && dv.Length > 0 && Dice.Total ? L.T("сума з ") + n : dk));
                using (Font sm = FitFont(font, cap, h * 0.22f, w, FontStyle.Regular))
                    Txt(g, cap, sm, new Rectangle(inner.X, inner.Y + (int)(h * 0.62f), w, (int)(h * 0.34f)), lab ? text : sec);
                break;
            }
            case "notes":
            {
                // піктограма: папірець із загнутим кутиком і рядками
                float s = Math.Min(w * 0.62f, h * 0.5f);
                RectangleF nr = new RectangleF(inner.X + (w - s) / 2f, inner.Y + h * 0.04f, s, s);
                float fold = s * 0.28f;
                SmoothingMode smn = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath np = new GraphicsPath())
                {
                    np.AddLine(nr.X, nr.Y, nr.Right, nr.Y);
                    np.AddLine(nr.Right, nr.Y, nr.Right, nr.Bottom - fold);
                    np.AddLine(nr.Right, nr.Bottom - fold, nr.Right - fold, nr.Bottom);
                    np.AddLine(nr.Right - fold, nr.Bottom, nr.X, nr.Bottom);
                    np.CloseFigure();
                    using (SolidBrush nb = new SolidBrush(Color.FromArgb(255, 222, 110))) g.FillPath(nb, np);
                }
                using (SolidBrush fb = new SolidBrush(Color.FromArgb(214, 176, 60)))
                    g.FillPolygon(fb, new PointF[] { new PointF(nr.Right, nr.Bottom - fold), new PointF(nr.Right - fold, nr.Bottom - fold), new PointF(nr.Right - fold, nr.Bottom) });
                using (Pen lp = new Pen(Color.FromArgb(150, 120, 90, 30), Math.Max(1f, s * 0.07f)))
                    for (int li = 0; li < 3; li++)
                    {
                        float ly = nr.Y + s * (0.28f + li * 0.2f);
                        g.DrawLine(lp, nr.X + s * 0.18f, ly, nr.Right - s * (li == 2 ? 0.45f : 0.18f), ly);
                    }
                g.SmoothingMode = smn;
                string nt = Notes.Count == 0 ? L.T("Нотатки") : L.T("Нотатки: ") + Notes.Count;
                using (Font sm = FitFont(font, nt, h * 0.22f, w, FontStyle.Regular))
                    Txt(g, nt, sm, new Rectangle(inner.X, inner.Y + (int)(h * 0.6f), w, (int)(h * 0.34f)), sec);
                break;
            }
            case "perf":
            {
                List<string> lb = new List<string>();
                List<float> pv = new List<float>();
                if (PerfCpu) { lb.Add(L.T("ЦП")); pv.Add(Sensors.Cpu); }
                if (PerfGpu) { lb.Add(L.T("ГП")); pv.Add(Sensors.GpuLoad); }
                if (PerfRam) { lb.Add(L.T("ОЗП")); pv.Add(Sensors.Ram); }
                if (lb.Count == 0) { lb.Add(L.T("ЦП")); pv.Add(Sensors.Cpu); }
                int rowH = h / lb.Count;
                using (Font sm = FitFont(font, L.T("ОЗП 88%"), rowH * 0.42f, w, FontStyle.Regular))
                {
                    for (int i = 0; i < lb.Count; i++)
                    {
                        int pc = float.IsNaN(pv[i]) ? -1 : (int)pv[i];
                        int y = inner.Y + i * rowH;
                        Txt(g, lb[i] + " " + (pc < 0 ? "…" : pc + "%"), sm, new Rectangle(inner.X, y + (int)(rowH * 0.04f), w, (int)(rowH * 0.56f)), text);
                        Rectangle br = new Rectangle(inner.X + (int)(3 * sc), y + (int)(rowH * 0.64f), w - (int)(6 * sc), Math.Max(3, (int)(rowH * 0.18f)));
                        Bar(g, br, Math.Max(0, pc), LoadColor(pc, acc, Color.FromArgb(240, 170, 40)), back);
                    }
                }
                break;
            }
        }
    }

    static string FreeShort(long bytes)
    {
        double g = bytes / 1073741824.0;
        if (g >= 1000) return (g / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + L.T(" ТБ");
        if (g >= 10) return g.ToString("0") + L.T(" ГБ");
        return g.ToString("0.0", CultureInfo.InvariantCulture) + L.T(" ГБ");
    }

    // ---------- векторні піктограми погоди ----------

    // хмара з окремих фігур (без «дірок» на перетинах за будь-якого режиму заливки)
    static void FillCloud(Graphics g, Brush b, RectangleF r)
    {
        float w = r.Width, h = r.Height;
        g.FillEllipse(b, r.X + w * 0.04f, r.Y + h * 0.42f, w * 0.38f, h * 0.5f);
        g.FillEllipse(b, r.X + w * 0.22f, r.Y + h * 0.1f, w * 0.44f, h * 0.7f);
        g.FillEllipse(b, r.X + w * 0.5f, r.Y + h * 0.28f, w * 0.46f, h * 0.64f);
        g.FillRectangle(b, r.X + w * 0.2f, r.Y + h * 0.5f, w * 0.55f, h * 0.42f);
    }

    static void Sun(Graphics g, float cx, float cy, float rad, Color col)
    {
        using (SolidBrush b = new SolidBrush(col))
            g.FillEllipse(b, cx - rad, cy - rad, rad * 2, rad * 2);
        using (Pen p = new Pen(col, Math.Max(1.2f, rad * 0.22f)))
        {
            p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                g.DrawLine(p, cx + (float)Math.Cos(a) * rad * 1.45f, cy + (float)Math.Sin(a) * rad * 1.45f,
                              cx + (float)Math.Cos(a) * rad * 1.9f, cy + (float)Math.Sin(a) * rad * 1.9f);
            }
        }
    }

    static void Moon(Graphics g, float cx, float cy, float rad, Color col)
    {
        using (GraphicsPath cut = new GraphicsPath())
        {
            cut.AddEllipse(cx - rad * 0.35f, cy - rad * 1.1f, rad * 1.8f, rad * 1.8f);
            GraphicsState st = g.Save();
            g.SetClip(cut, CombineMode.Exclude);
            using (SolidBrush b = new SolidBrush(col))
                g.FillEllipse(b, cx - rad, cy - rad, rad * 2, rad * 2);
            g.Restore(st);
        }
    }

    public static void DrawWeather(Graphics g, Rectangle r, int code, bool day, Theme t, Color text)
    {
        DrawWeather(g, r, code, day, t, text, 1f);
    }

    // fade < 1 - піктограма напівпрозора (анімація оновлення погоди)
    public static void DrawWeather(Graphics g, Rectangle r, int code, bool day, Theme t, Color text, float fade)
    {
        SmoothingMode sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int al = (int)Math.Round(255 * Math.Max(0f, Math.Min(1f, fade)));
        Color sun = Color.FromArgb(al, 255, 196, 40);
        Color cloud = Util.Lum(Util.Opaque(t.Bg)) > 140 ? Color.FromArgb(al, 120, 140, 170) : Color.FromArgb(al, 214, 224, 238);
        Color dark = Color.FromArgb(al, Util.Blend(cloud, Color.Black, 0.35f));
        Color rain = Color.FromArgb(al, 70, 160, 255);
        RectangleF q = new RectangleF(r.X, r.Y, r.Width, r.Height);
        float cx = q.X + q.Width / 2, cy = q.Y + q.Height / 2;

        bool clear = code == 0, partly = code == 1 || code == 2;
        bool fog = code == 45 || code == 48;
        bool drizzle = (code >= 51 && code <= 57), rainy = (code >= 61 && code <= 67) || (code >= 80 && code <= 82);
        bool snow = (code >= 71 && code <= 77) || code == 85 || code == 86;
        bool storm = code >= 95;

        if (clear)
        {
            if (day) Sun(g, cx, cy, q.Width * 0.22f, sun);
            else Moon(g, cx, cy, q.Width * 0.3f, Color.FromArgb(al, 240, 232, 190));
            g.SmoothingMode = sm;
            return;
        }
        if (partly)
        {
            if (day) Sun(g, q.X + q.Width * 0.34f, q.Y + q.Height * 0.34f, q.Width * 0.15f, sun);
            else Moon(g, q.X + q.Width * 0.34f, q.Y + q.Height * 0.34f, q.Width * 0.2f, Color.FromArgb(al, 240, 232, 190));
            using (SolidBrush b = new SolidBrush(cloud))
                FillCloud(g, b, new RectangleF(q.X + q.Width * 0.18f, q.Y + q.Height * 0.38f, q.Width * 0.8f, q.Height * 0.55f));
            g.SmoothingMode = sm;
            return;
        }
        RectangleF cr = new RectangleF(q.X + q.Width * 0.04f, q.Y + q.Height * 0.08f, q.Width * 0.92f, q.Height * 0.6f);
        using (SolidBrush b = new SolidBrush(storm || rainy ? dark : cloud))
            FillCloud(g, b, cr);
        float lw = Math.Max(1.4f, q.Width * 0.06f);
        float y0 = cr.Bottom + q.Height * 0.04f;
        if (fog)
        {
            using (Pen p = new Pen(cloud, lw)) { p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                for (int i = 0; i < 3; i++) g.DrawLine(p, q.X + q.Width * 0.14f, y0 + i * q.Height * 0.11f, q.Right - q.Width * (i == 1 ? 0.1f : 0.2f), y0 + i * q.Height * 0.11f); }
        }
        else if (drizzle || rainy || storm)
        {
            using (Pen p = new Pen(rain, lw)) { p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                int n = drizzle ? 2 : 3;
                for (int i = 0; i < n; i++)
                {
                    float x = q.X + q.Width * (0.28f + i * 0.22f);
                    g.DrawLine(p, x, y0, x - q.Width * 0.07f, y0 + q.Height * 0.2f);
                } }
            if (storm) DrawBoltSmall(g, q.X + q.Width * 0.42f, cr.Bottom - q.Height * 0.12f, q.Height * 0.38f, al);
        }
        else if (snow)
        {
            using (SolidBrush b = new SolidBrush(Color.FromArgb(al, Color.White)))
            {
                float d = Math.Max(2f, q.Width * 0.07f);
                for (int i = 0; i < 3; i++)
                    g.FillEllipse(b, q.X + q.Width * (0.26f + i * 0.22f), y0 + (i % 2) * q.Height * 0.08f, d, d);
            }
        }
        g.SmoothingMode = sm;
    }

    static void DrawBoltSmall(Graphics g, float x, float y, float s, int al)
    {
        PointF[] p =
        {
            new PointF(x + s * 0.5f, y), new PointF(x, y + s * 0.55f), new PointF(x + s * 0.32f, y + s * 0.55f),
            new PointF(x + s * 0.2f, y + s), new PointF(x + s * 0.75f, y + s * 0.4f), new PointF(x + s * 0.42f, y + s * 0.4f)
        };
        using (SolidBrush b = new SolidBrush(Color.FromArgb(al, 255, 214, 40)))
            g.FillPolygon(b, p);
    }
}
