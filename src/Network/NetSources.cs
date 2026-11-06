using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

// ======================================================================
//  Інтернет-джерела NeoBar і перевірка їхньої доступності.
//  Показується в налаштуваннях («Місце й погода» → «Джерела даних») і в «Перевірці системи».
//  Результат зберігається кілька хвилин, щоб повторні перевірки (вікно привітання оновлюється
//  кожні кілька секунд) не смикали мережу.
// ======================================================================
sealed class NetSource
{
    public string Title, Name;
    public string[] Urls;                   // перша адреса - основна, решта - запасні
    public bool Ntp;                        // сервер часу (SNTP, не HTTP)
    public Func<bool> Used;                 // null - використовується завжди
    public string State = "";               // результат останньої перевірки
    public bool? Ok;
    public double Offset = double.NaN;      // для сервера часу: на скільки годинник ПК відстає (+) чи поспішає (-), с

    public bool IsUsed
    {
        get { try { return Used == null || Used(); } catch { return true; } }
    }

    public string Line
    {
        get { return Name + " · " + (!IsUsed ? L.T("не використовується") : (State.Length > 0 ? State : L.T("ще не перевірено"))); }
    }
}

static class NetSources
{
    public static readonly List<NetSource> All = new List<NetSource>();
    public static DateTime LastAt = DateTime.MinValue;
    static int busy;

    public static bool Busy { get { return busy > 0; } }
    public static bool Fresh { get { return (DateTime.Now - LastAt).TotalMinutes < 3; } }

    static Cfg C { get { return ClockData.Bar != null ? ClockData.Bar.Config : null; } }

    static NetSources()
    {
        Add(L.T("Погода"), "Open-Meteo", null, "https://api.open-meteo.com/v1/forecast?latitude=50.45&longitude=30.52&current=temperature_2m");
        Add(L.T("Пошук міста"), "Open-Meteo Geocoding", null, "https://geocoding-api.open-meteo.com/v1/search?count=1&name=Kyiv");
        Add(L.T("Назва місця за координатами"), "BigDataCloud", delegate { Cfg c = C; return c == null || c.LocAllow; },
            "https://api.bigdatacloud.net/data/reverse-geocode-client?latitude=50.45&longitude=30.52&localityLanguage=uk");
        Add(L.T("Свята"), "date.nager.at", delegate { return Holidays.Online; }, "https://date.nager.at/api/v3/AvailableCountries");
        Add(L.T("Карта"), "OpenStreetMap", null, "https://tile.openstreetmap.org/0/0/0.png");
        // місце за IP - лише в автоматичному режимі (коли служба розташування Windows не дала відповіді)
        Add(L.T("Місце за IP"), "ipwho.is, get.geojs.io, ip-api.com", delegate { Cfg c = C; return c == null || (c.LocAllow && c.LocMode == "auto"); },
            "https://ipwho.is/", "https://get.geojs.io/v1/ip/geo.json", "http://ip-api.com/json/?fields=status");
        NetSource t = Add(L.T("Точний час"), string.Join(", ", global::Ntp.Servers), null);
        t.Ntp = true;
    }

    static NetSource Add(string title, string name, Func<bool> used, params string[] urls)
    {
        NetSource s = new NetSource();
        s.Title = title; s.Name = name; s.Used = used; s.Urls = urls;
        All.Add(s);
        return s;
    }

    public static NetSource Time { get { foreach (NetSource s in All) if (s.Ntp) return s; return null; } }

    // Перевірити всі джерела, що використовуються (паралельно, у фоні). done - з фонового потоку, коли все завершено.
    // false - перевірка вже йде (done тоді не викликається)
    public static bool CheckAll(Action done)
    {
        List<NetSource> todo = new List<NetSource>();
        foreach (NetSource s in All) if (s.IsUsed) todo.Add(s);
        if (todo.Count == 0) return false;
        if (Interlocked.CompareExchange(ref busy, todo.Count, 0) != 0) return false;
        foreach (NetSource s0 in todo)
        {
            NetSource s = s0;
            s.State = L.T("перевіряю…");
            s.Ok = null;
            Util.Bg(delegate
            {
                try { Check(s); }
                catch (Exception ex) { s.Ok = false; s.State = L.T("помилка перевірки: ") + ex.Message; }
                if (Interlocked.Decrement(ref busy) == 0)
                {
                    LastAt = DateTime.Now;
                    if (done != null) try { done(); } catch { }
                }
            });
        }
        return true;
    }

    static void Check(NetSource s)
    {
        if (s.Ntp)
        {
            double off, rtt; string srv;
            if (global::Ntp.Query(out off, out rtt, out srv))
            {
                s.Ok = true;
                s.Offset = off;
                s.State = L.T("доступно (") + srv + "), " + (int)Math.Round(rtt * 1000) + L.T(" мс; ") + ClockText(off);
            }
            else { s.Ok = false; s.Offset = double.NaN; s.State = L.T("недоступно: сервери часу не відповідають (порт UDP 123 може бути закритий)"); }
            return;
        }
        string firstErr = "";
        for (int i = 0; i < s.Urls.Length; i++)
        {
            int ms; string err;
            if (NetCache.Probe(s.Urls[i], 6000, out ms, out err))
            {
                s.Ok = true;
                s.State = i == 0 ? L.T("доступно, ") + ms + L.T(" мс")
                                 : L.T("основний недоступний (") + firstErr + L.T("), працює запасний ") + Host(s.Urls[i]) + ", " + ms + L.T(" мс");
                return;
            }
            if (i == 0) firstErr = err;
        }
        s.Ok = false;
        s.State = L.T("недоступно: ") + firstErr;
    }

    // «годинник точний» / «поспішає на 1,2 с» / «відстає на 3 хв»
    public static string ClockText(double off)
    {
        double a = Math.Abs(off);
        if (a < 0.5) return L.T("годинник ПК точний (") + a.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',') + L.T(" с)");
        string amount = a < 90 ? a.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + L.T(" с") : Math.Round(a / 60) + L.T(" хв");
        return L.T("годинник ПК ") + (off > 0 ? L.T("відстає") : L.T("поспішає")) + L.T(" на ") + amount;
    }

    static string Host(string url)
    {
        try { return new Uri(url).Host; } catch { return url; }
    }
}
