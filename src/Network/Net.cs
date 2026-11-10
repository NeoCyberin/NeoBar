using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// ======================================================================
//  Мережа з кешем: усе, що береться з інтернету (погода, свята, карта, іконки сайтів,
//  місце за IP), зберігається у папці cache поряд з програмою. Без інтернету
//  показується останнє збережене.
// ======================================================================
static class NetCache
{
    static string root;
    static readonly object fileLock = new object();

    public const string UserAgent = "NeoBar/1.0 (Windows desktop panel)";

    public static string Root
    {
        get
        {
            if (root != null) return root;
            string r = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
            try
            {
                Directory.CreateDirectory(r);
                string probe = Path.Combine(r, ".w");
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch
            {
                // папка програми лише для читання (Program Files) - кеш у профілі користувача
                r = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeoBar", "cache");
                try { Directory.CreateDirectory(r); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            }
            root = r;
            return root;
        }
    }

    public static string Dir(string sub)
    {
        string d = Path.Combine(Root, sub);
        try { Directory.CreateDirectory(d); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return d;
    }

    static string Hash(string s)
    {
        using (SHA1 h = SHA1.Create())
        {
            byte[] b = h.ComputeHash(Encoding.UTF8.GetBytes(s));
            StringBuilder sb = new StringBuilder(40);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }

    public static string FileFor(string url)
    {
        return Path.Combine(Dir("http"), Hash(url) + ".bin");
    }

    // Завантаження без кешу
    public static byte[] Download(string url, int timeoutMs, string ua)
    {
        try { return Fetch(url, timeoutMs, ua, true); }
        catch (Exception ex)
        {
            // деякі сервери віддають «deflate» у несумісному вигляді ("Block length does not match with its complement") -
            // повторюємо без стиснення
            if (ex is WebException && ((WebException)ex).Response != null) throw;
            return Fetch(url, timeoutMs, ua, false);
        }
    }

    static byte[] Fetch(string url, int timeoutMs, string ua, bool gzip)
    {
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;   // TLS 1.2
        HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(url);
        rq.Timeout = timeoutMs;
        rq.ReadWriteTimeout = timeoutMs;
        rq.UserAgent = ua ?? UserAgent;
        // лише gzip: .NET Framework не вміє частину варіантів «deflate»
        rq.AutomaticDecompression = gzip ? DecompressionMethods.GZip : DecompressionMethods.None;
        if (!gzip) rq.Headers[HttpRequestHeader.AcceptEncoding] = "identity";
        using (WebResponse rs = rq.GetResponse())
        using (Stream s = rs.GetResponseStream())
        using (MemoryStream ms = new MemoryStream())
        {
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    // Перевірка доступності сервісу: будь-яка відповідь HTTP (навіть помилка 4xx) означає, що сервер досяжний.
    // ms - час відповіді; err - причина, якщо відповіді немає (порожньо, коли все добре)
    public static bool Probe(string url, int timeoutMs, out int ms, out string err)
    {
        ms = 0; err = "";
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;   // TLS 1.2
            HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(url);
            rq.Timeout = timeoutMs;
            rq.ReadWriteTimeout = timeoutMs;
            rq.UserAgent = UserAgent;
            rq.AllowAutoRedirect = true;
            using (WebResponse rs = rq.GetResponse()) { }
            ms = (int)sw.ElapsedMilliseconds;
            return true;
        }
        catch (WebException ex)
        {
            ms = (int)sw.ElapsedMilliseconds;
            HttpWebResponse hr = ex.Response as HttpWebResponse;
            if (hr != null) { hr.Close(); return true; }            // сервер відповів (хай і помилкою) - зв'язок є
            switch (ex.Status)
            {
                case WebExceptionStatus.Timeout: err = L.T("немає відповіді за ") + (timeoutMs / 1000) + L.T(" с"); break;
                case WebExceptionStatus.NameResolutionFailure: err = L.T("адреса не знайдена (DNS або немає інтернету)"); break;
                case WebExceptionStatus.ConnectFailure: err = L.T("не вдалося з'єднатися"); break;
                case WebExceptionStatus.TrustFailure:
                case WebExceptionStatus.SecureChannelFailure: err = L.T("помилка захищеного з'єднання"); break;
                default: err = ex.Message; break;
            }
            return false;
        }
        catch (Exception ex) { ms = (int)sw.ElapsedMilliseconds; err = ex.Message; return false; }
    }

    // Байти з кешу, якщо свіжі; інакше з мережі (і в кеш). Без мережі - старий кеш (stale), якщо є.
    // адреса для журналу без параметрів (у них бувають координати)
    static string Short(string url)
    {
        int q = url.IndexOf('?');
        return q > 0 ? url.Substring(0, q) : url;
    }

    public static byte[] Get(string url, TimeSpan ttl, out bool stale)
    {
        return Get(url, url, ttl, out stale, null);
    }

    public static byte[] Get(string url, string key, TimeSpan ttl, out bool stale, string ua)
    {
        stale = false;
        string f = FileFor(key);
        try
        {
            if (File.Exists(f) && DateTime.Now - File.GetLastWriteTime(f) < ttl) return File.ReadAllBytes(f);
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        try
        {
            DateTime t0 = DateTime.Now;
            byte[] data = Download(url, 10000, ua);
            Save(f, data);
            if (SbLog.On(SbLog.Widgets)) SbLog.W(SbLog.Widgets, "мережа: " + Short(url) + " - " + data.Length + " байт за " + (int)(DateTime.Now - t0).TotalMilliseconds + " мс");
            return data;
        }
        catch (Exception ex)
        {
            if (SbLog.On(SbLog.Widgets)) SbLog.W(SbLog.Widgets, "мережа: " + Short(url) + " - помилка: " + ex.Message + (File.Exists(f) ? " (беру збережену копію)" : ""));
            try
            {
                if (File.Exists(f)) { stale = true; return File.ReadAllBytes(f); }
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
            throw;
        }
    }

    public static string GetText(string url, TimeSpan ttl, out bool stale)
    {
        return Encoding.UTF8.GetString(Get(url, ttl, out stale));
    }

    public static string GetText(string url, TimeSpan ttl)
    {
        bool st;
        return GetText(url, ttl, out st);
    }

    // коли востаннє оновлено (для підпису «станом на ...»)
    public static DateTime SavedAt(string key)
    {
        try
        {
            string f = FileFor(key);
            return File.Exists(f) ? File.GetLastWriteTime(f) : DateTime.MinValue;
        }
        catch { return DateTime.MinValue; }
    }

    public static void Save(string file, byte[] data)
    {
        lock (fileLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                string tmp = file + ".tmp";
                File.WriteAllBytes(tmp, data);
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
    }

    // розмір кешу та очищення (для налаштувань)
    public static long Size()
    {
        long n = 0;
        try { foreach (string f in Directory.GetFiles(Root, "*", SearchOption.AllDirectories)) n += new FileInfo(f).Length; }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return n;
    }

    public static void Clear()
    {
        try
        {
            foreach (string d in Directory.GetDirectories(Root))
                try { Directory.Delete(d, true); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }
}

// ----------------------------------------------------------------------
//  Простий розбір JSON (лише те, що потрібно відповідям Open-Meteo, Nager, геосервісів)
// ----------------------------------------------------------------------
static class Js
{
    public static double Num(string json, string name)
    {
        Match m = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"?(-?[0-9.]+(?:[eE][-+]?[0-9]+)?)");
        double d;
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return double.NaN;
    }

    public static string Str(string json, string name)
    {
        Match m = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        if (!m.Success) return "";
        try { return Regex.Unescape(m.Groups[1].Value); }
        catch { return m.Groups[1].Value; }
    }

    // вміст об'єкта "name": { ... } (без вкладених об'єктів)
    public static string Obj(string json, string name)
    {
        Match m = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*\\{([^{}]*)\\}");
        return m.Success ? m.Groups[1].Value : "";
    }

    static string Arr(string json, string name)
    {
        Match m = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*\\[([^\\]]*)\\]");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static double[] Nums(string json, string name)
    {
        string a = Arr(json, name);
        if (a == null) return new double[0];
        string[] p = a.Split(',');
        double[] r = new double[p.Length];
        for (int i = 0; i < p.Length; i++)
        {
            double d;
            r[i] = double.TryParse(p[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : double.NaN;
        }
        if (a.Trim().Length == 0) return new double[0];
        return r;
    }

    public static string[] Strs(string json, string name)
    {
        string a = Arr(json, name);
        if (a == null) return new string[0];
        List<string> l = new List<string>();
        foreach (Match m in Regex.Matches(a, "\"((?:[^\"\\\\]|\\\\.)*)\""))
        {
            try { l.Add(Regex.Unescape(m.Groups[1].Value)); } catch { l.Add(m.Groups[1].Value); }
        }
        return l.ToArray();
    }

    // об'єкти верхнього рівня без вкладених об'єктів: [{...},{...}]
    public static List<string> Objects(string json)
    {
        List<string> l = new List<string>();
        foreach (Match m in Regex.Matches(json, "\\{([^{}]*)\\}")) l.Add(m.Groups[1].Value);
        return l;
    }

    public static string Esc(string s)
    {
        return Uri.EscapeDataString(s ?? "");
    }

}

// ----------------------------------------------------------------------
//  Місце розташування: Windows (служба розташування) або за IP; назва за координатами
// ----------------------------------------------------------------------
static class Geo
{
    public static string Source = "";     // звідки останнє автоматичне місце: "windows" або "ip"

    // Служба розташування Windows через System.Device (завантажується динамічно; працює,
    // якщо в Параметрах дозволено доступ класичних програм до розташування)
    public static bool WindowsLocation(out double lat, out double lon)
    {
        lat = lon = double.NaN;
        object w = null;
        try
        {
            Type t = Type.GetType("System.Device.Location.GeoCoordinateWatcher, System.Device, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089", false);
            if (t == null) return false;
            w = Activator.CreateInstance(t);
            object ok = t.GetMethod("TryStart", new Type[] { typeof(bool), typeof(TimeSpan) }).Invoke(w, new object[] { false, TimeSpan.FromSeconds(6) });
            if (!(ok is bool) || !(bool)ok) return false;
            for (int i = 0; i < 40; i++)
            {
                object pos = t.GetProperty("Position").GetValue(w, null);
                object loc = pos.GetType().GetProperty("Location").GetValue(pos, null);
                bool unknown = (bool)loc.GetType().GetProperty("IsUnknown").GetValue(loc, null);
                if (!unknown)
                {
                    lat = (double)loc.GetType().GetProperty("Latitude").GetValue(loc, null);
                    lon = (double)loc.GetType().GetProperty("Longitude").GetValue(loc, null);
                    return !double.IsNaN(lat) && !double.IsNaN(lon);
                }
                Thread.Sleep(250);
            }
            return false;
        }
        catch { return false; }
        finally
        {
            try
            {
                if (w != null)
                {
                    w.GetType().GetMethod("Stop").Invoke(w, null);
                    IDisposable d = w as IDisposable;
                    if (d != null) d.Dispose();
                }
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
    }

    // Місце за IP-адресою (точність - місто). tz - пояс IANA, якщо сервіс його дав.
    public static bool IpLocation(out double lat, out double lon, out string city, out string cc, out string tz)
    {
        lat = lon = double.NaN; city = cc = tz = "";
        string[] urls =
        {
            "https://ipwho.is/?lang=uk",
            "https://get.geojs.io/v1/ip/geo.json",
            "http://ip-api.com/json/?fields=status,lat,lon,city,countryCode,timezone&lang=ru"
        };
        foreach (string u in urls)
        {
            try
            {
                bool stale;
                string js = Encoding.UTF8.GetString(NetCache.Get(u, u, TimeSpan.FromHours(3), out stale, null));
                double la = Js.Num(js, "latitude"), lo = Js.Num(js, "longitude");
                if (double.IsNaN(la)) { la = Js.Num(js, "lat"); lo = Js.Num(js, "lon"); }
                if (double.IsNaN(la) || double.IsNaN(lo)) continue;
                lat = la; lon = lo;
                city = Js.Str(js, "city");
                cc = Js.Str(js, "country_code");
                if (cc.Length == 0) cc = Js.Str(js, "countryCode");
                string tzo = Js.Obj(js, "timezone");
                tz = tzo.Length > 0 ? Js.Str(tzo, "id") : Js.Str(js, "timezone");
                return true;
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
        return false;
    }

    // Назва місця та код країни за координатами (кеш на 90 днів)
    public static bool Reverse(double lat, double lon, out string name, out string cc)
    {
        name = ""; cc = "";
        try
        {
            string url = "https://api.bigdatacloud.net/data/reverse-geocode-client?latitude=" +
                lat.ToString("0.###", CultureInfo.InvariantCulture) + "&longitude=" + lon.ToString("0.###", CultureInfo.InvariantCulture) + "&localityLanguage=uk";
            string js = NetCache.GetText(url, TimeSpan.FromDays(90));
            name = Js.Str(js, "city");
            if (name.Length == 0) name = Js.Str(js, "locality");
            if (name.Length == 0) name = Js.Str(js, "principalSubdivision");
            cc = Js.Str(js, "countryCode");
            return name.Length > 0 || cc.Length > 0;
        }
        catch { return false; }
    }

    // Код країни для назви з геокодера Open-Meteo
    public static string CountryOfCity(string city)
    {
        try
        {
            string js = NetCache.GetText("https://geocoding-api.open-meteo.com/v1/search?count=1&language=uk&format=json&name=" + Js.Esc(city), TimeSpan.FromDays(60));
            return Js.Str(js, "country_code");
        }
        catch { return ""; }
    }
}

// ----------------------------------------------------------------------
//  Точність годинника: запит до сервера часу (SNTP)
// ----------------------------------------------------------------------
static class Ntp
{
    public static readonly string[] Servers = { "time.windows.com", "pool.ntp.org", "time.google.com" };

    // offset: на скільки секунд системний годинник відстає (+) чи поспішає (-)
    // Кілька запитів, береться відповідь з найменшою затримкою мережі - вона найточніша
    public static bool Query(out double offset, out double rtt, out string server)
    {
        offset = rtt = 0; server = "";
        foreach (string s in Servers)
        {
            double bo = 0, br = double.MaxValue;
            int ok = 0;
            for (int i = 0; i < 5; i++)
            {
                double o, r;
                if (!Query(s, out o, out r)) { if (ok == 0 && i >= 1) break; continue; }
                ok++;
                if (r < br) { br = r; bo = o; }
                Thread.Sleep(120);
            }
            if (ok > 0) { offset = bo; rtt = br; server = s; return true; }
        }
        return false;
    }

    static bool Query(string host, out double offset, out double rtt)
    {
        offset = rtt = 0;
        try
        {
            IPAddress[] addrs = Dns.GetHostAddresses(host);
            IPAddress ip = null;
            foreach (IPAddress a in addrs) if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
            if (ip == null) return false;
            using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                s.ReceiveTimeout = 3000;
                s.SendTimeout = 3000;
                s.Connect(new IPEndPoint(ip, 123));
                byte[] pkt = new byte[48];
                pkt[0] = 0x1B;   // LI=0, VN=3, Mode=3 (клієнт)
                Stopwatch sw = new Stopwatch();
                DateTime t1 = DateTime.UtcNow;
                sw.Start();
                s.Send(pkt);
                byte[] rx = new byte[48];
                int n = s.Receive(rx);
                sw.Stop();
                if (n < 48) return false;
                DateTime t4 = t1.AddTicks((long)(sw.Elapsed.TotalMilliseconds * 10000));
                DateTime t2 = Stamp(rx, 32), t3 = Stamp(rx, 40);
                if (t3.Year < 2000) return false;
                offset = ((t2 - t1).TotalSeconds + (t3 - t4).TotalSeconds) / 2.0;
                rtt = (t4 - t1).TotalSeconds - (t3 - t2).TotalSeconds;
                return true;
            }
        }
        catch { return false; }
    }

    static DateTime Stamp(byte[] b, int o)
    {
        ulong sec = ((ulong)b[o] << 24) | ((ulong)b[o + 1] << 16) | ((ulong)b[o + 2] << 8) | b[o + 3];
        ulong frac = ((ulong)b[o + 4] << 24) | ((ulong)b[o + 5] << 16) | ((ulong)b[o + 6] << 8) | b[o + 7];
        double ms = sec * 1000.0 + frac * 1000.0 / 4294967296.0;
        return new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);
    }
}

// ----------------------------------------------------------------------
//  Точне встановлення системного часу (потрібні права адміністратора)
// ----------------------------------------------------------------------
static class TimeSync
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct SYSTEMTIME { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Ms; }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetSystemTime(ref SYSTEMTIME st);

    public static bool Run(out string msg)
    {
        double off, rtt; string srv;
        if (!Ntp.Query(out off, out rtt, out srv)) { msg = L.T("немає зв'язку із сервером часу."); return false; }
        if (Math.Abs(off) < 0.015) { msg = L.T("годинник уже точний (") + Math.Abs(off).ToString("0.000") + L.T(" с)."); return true; }
        DateTime t = DateTime.UtcNow.AddSeconds(off);
        SYSTEMTIME st = new SYSTEMTIME();
        st.Year = (ushort)t.Year; st.Month = (ushort)t.Month; st.Day = (ushort)t.Day;
        st.Hour = (ushort)t.Hour; st.Minute = (ushort)t.Minute; st.Second = (ushort)t.Second; st.Ms = (ushort)t.Millisecond;
        if (!SetSystemTime(ref st)) { msg = L.T("Windows не дозволила змінити час (код ") + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ")."; return false; }
        double after, r2; string s2;
        msg = L.T("час виправлено на ") + Math.Abs(off).ToString("0.00") + L.T(" с (") + srv + ")";
        if (Ntp.Query(out after, out r2, out s2)) msg += L.T(", тепер відхилення ") + Math.Abs(after).ToString("0.000") + L.T(" с");
        msg += ".";
        return true;
    }
}

// ----------------------------------------------------------------------
//  Часовий пояс системи
// ----------------------------------------------------------------------
static class SysTz
{
    // Встановлює пояс Windows (право на зміну поясу мають звичайні користувачі - адміністратор не потрібен)
    public static bool Set(string windowsId)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("tzutil.exe", "/s \"" + windowsId + "\"");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            using (Process p = Process.Start(psi))
            {
                p.WaitForExit(8000);
                TimeZoneInfo.ClearCachedData();
                return p.ExitCode == 0;
            }
        }
        catch { return false; }
    }

    public static string Describe(TimeZoneInfo z)
    {
        try
        {
            TimeSpan o = z.GetUtcOffset(DateTime.UtcNow);
            string sign = o < TimeSpan.Zero ? "−" : "+";
            o = o.Duration();
            return "UTC" + sign + o.Hours.ToString("00") + ":" + o.Minutes.ToString("00");
        }
        catch { return ""; }
    }
}

// ----------------------------------------------------------------------
//  Іконки сайтів: завантажуються у фоні й зберігаються в cache\favicons
// ----------------------------------------------------------------------
static class Favicons
{
    static readonly HashSet<string> busy = new HashSet<string>();
    static readonly HashSet<string> failed = new HashSet<string>();

    public static string Host(string url)
    {
        try
        {
            Uri u = new Uri(url);
            string h = u.Host.ToLowerInvariant();
            return h.StartsWith("www.") ? h.Substring(4) : h;
        }
        catch { return ""; }
    }

    static string FileOf(string host)
    {
        return Path.Combine(NetCache.Dir("favicons"), Util.SafeName(host) + ".png");
    }

    // шлях до збереженої іконки або null (тоді завантаження запускається у фоні, done - коли готово)
    public static string Get(string url, Action done)
    {
        string host = Host(url);
        if (host.Length == 0) return null;
        string f = FileOf(host);
        bool have = File.Exists(f);
        bool old = have && (DateTime.Now - File.GetLastWriteTime(f)).TotalDays > 30;
        if (!have || old)
        {
            lock (busy)
            {
                if (!busy.Contains(host) && !failed.Contains(host))
                {
                    busy.Add(host);
                    Util.Bg(delegate
                    {
                        bool ok = Fetch(host, f);
                        lock (busy) { busy.Remove(host); if (!ok) failed.Add(host); }
                        if (ok && done != null) try { done(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                    });
                }
            }
        }
        return have ? f : null;
    }

    static bool Fetch(string host, string file)
    {
        string[] urls =
        {
            "https://" + host + "/apple-touch-icon.png",
            "https://www.google.com/s2/favicons?sz=128&domain=" + host,
            "https://icons.duckduckgo.com/ip3/" + host + ".ico",
            "https://" + host + "/favicon.ico"
        };
        System.Drawing.Bitmap best = null;
        foreach (string u in urls)
        {
            try
            {
                byte[] data = NetCache.Download(u, 8000, "Mozilla/5.0 (Windows NT 10.0; Win64; x64) NeoBar/1.0");
                if (data.Length < 64) continue;
                System.Drawing.Bitmap bmp = Decode(data);
                if (bmp == null) continue;
                if (best == null || bmp.Width > best.Width) { if (best != null) best.Dispose(); best = bmp; }
                else bmp.Dispose();
                if (best.Width >= 96) break;
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
        if (best == null) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            using (best) best.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            return true;
        }
        catch { return false; }
    }

    static System.Drawing.Bitmap Decode(byte[] data)
    {
        using (MemoryStream ms = new MemoryStream(data))
        {
            // .ico - беремо найбільший розмір
            if (data.Length > 6 && data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0)
            {
                try { using (System.Drawing.Icon ic = new System.Drawing.Icon(ms, new System.Drawing.Size(256, 256))) return ic.ToBitmap(); }
                catch { ms.Position = 0; }
            }
            try { using (System.Drawing.Image im = System.Drawing.Image.FromStream(ms)) return new System.Drawing.Bitmap(im); }
            catch { return null; }
        }
    }
}
