using System;
using System.Collections.Generic;
using System.Globalization;

// ======================================================================
//  Святкові теми: Хелловін, Різдво, Великдень (запрошення за 3 доби до свята, тема діє 10 діб)
//  і день народження (без попередження, з самого дня, 7 діб).
//  Тема вмикається лише після підтвердження; після закінчення строку панель повертається до попередньої
//  теми під час наступного запуску. Вручну вибрану святкову тему (у «Вигляді») ніхто не вимикає.
// ======================================================================
static class Seasons
{
    sealed class Occasion
    {
        public string Theme, Name;
        public DateTime Day, From, Until;      // Until - не включно
    }

    static readonly HashSet<string> later = new HashSet<string>();     // «Не зараз» - до наступного запуску
    static DateTime day = DateTime.MinValue;
    static bool started, asking;
    static DateTime waitFrom = DateTime.MinValue;
    static string ended = "";        // повідомлення про вимкнену тему - показується, коли панель уже на екрані

    // під час запуску панелі (трохи згодом, коли панель уже на екрані)
    public static void Start(NeoBar bar)
    {
        System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
        t.Interval = 2000;
        t.Tick += delegate
        {
            t.Stop(); t.Dispose();
            if (ended.Length > 0) { try { ToastForm.Show(bar, L.T("Свято минуло"), ended, "check", false, null); } catch (Exception skipEx) { SbLog.Skip(skipEx); } ended = ""; }
            started = true;
        };
        t.Start();
    }

    // з годинникового таймера (двічі на секунду): нова доба або відкладене запрошення.
    // Тему пропонують і міняють лише тоді, коли користувач саме працює з панеллю: курсор на панелі,
    // фокус не в налаштуваннях чи іншому вікні. Доти запрошення чекає (це дешева перевірка)
    public static void Tick(NeoBar bar)
    {
        if (!started || asking || bar == null || NeoBar.Paused) return;
        DateTime today = DateTime.Today;
        if (today == day) return;
        if (!Free(bar)) return;
        // краще, коли користувач на панелі, але не чекаємо вічно: через кілька секунд показуємо й так
        if (!OnPanel(bar))
        {
            if (waitFrom == DateTime.MinValue) waitFrom = DateTime.Now;
            if ((DateTime.Now - waitFrom).TotalSeconds < 6) return;
        }
        waitFrom = DateTime.MinValue;
        day = today;
        try { Offer(bar); } catch (Exception ex) { Util.ErrorLog(ex); }
    }

    // зараз іде (або от-от піде) вітання святковою темою зі звуком: інші сповіщення мають помовчати
    public static bool Greeting(NeoBar bar)
    {
        try
        {
            if (asking || SeasonFx.Playing) return true;
            return started && bar != null && day != DateTime.Today && Today(bar.Config) != null;
        }
        catch { return false; }
    }

    // тему не змінюємо й не пропонуємо, поки відкриті налаштування чи інше вікно панелі
    static bool Free(NeoBar bar)
    {
        try
        {
            if (!bar.Visible || bar.SettingsOpen) return false;
            System.Windows.Forms.Form af = System.Windows.Forms.Form.ActiveForm;
            return af == null || af == bar;
        }
        catch { return false; }
    }

    static bool OnPanel(NeoBar bar)
    {
        try { return bar.Bounds.Contains(System.Windows.Forms.Cursor.Position); }
        catch { return false; }
    }

    // тема дня народження має найвищий пріоритет: поки вона діє, інші свята її не змінюють
    static bool BirthdayActive(Cfg c)
    {
        return c.SeasonTheme == "birthday" && c.ThemeId == "birthday" && DateTime.Today < c.SeasonUntil;
    }

    static List<Occasion> For(int y, Cfg c)
    {
        List<Occasion> l = new List<Occasion>();
        DateTime bd;
        if (Holidays.BirthdayIn(c, y, out bd)) l.Add(Make("birthday", L.T("день народження"), bd, 0, 7));
        l.Add(Make("halloween", L.T("Хелловін"), new DateTime(y, 10, 31), 3, 10));
        DateTime xd = new DateTime(y, 12, 25), ed = Holidays.Easter(y, true);
        try { xd = Holidays.ChristmasFor(y, c); ed = Holidays.EasterFor(y, c); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        l.Add(Make("xmas", L.T("Різдво"), xd, 3, 10));
        l.Add(Make("easter", L.T("Великдень"), ed, 3, 10));
        return l;
    }

    static Occasion Make(string theme, string name, DateTime d, int before, int days)
    {
        Occasion o = new Occasion();
        o.Theme = theme; o.Name = name; o.Day = d;
        o.From = d.AddDays(-before);
        o.Until = o.From.AddDays(days);
        return o;
    }

    // свято, чиє вікно припадає на сьогодні (день народження - першим)
    static Occasion Today(Cfg c)
    {
        DateTime t = DateTime.Today;
        bool bd = BirthdayActive(c);
        // день народження - першим (у будь-якому з двох років), навіть якщо одночасно діє інше свято
        for (int pass = 0; pass < 2; pass++)
            for (int y = t.Year; y >= t.Year - 1; y--)
                foreach (Occasion o in For(y, c))
                {
                    if ((o.Theme == "birthday") != (pass == 0)) continue;
                    if (bd && o.Theme != "birthday") continue;
                    if (t >= o.From && t < o.Until && !Skipped(c, o) && !later.Contains(Key(o)) && c.ThemeId != o.Theme) return o;
                }
        return null;
    }

    static string Key(Occasion o) { return o.Theme + "-" + o.Day.Year; }

    static bool Skipped(Cfg c, Occasion o)
    {
        foreach (string s in c.SeasonSkip.Split(','))
            if (s.Trim() == Key(o)) return true;
        return false;
    }

    // Строк святкової теми минув - повернути попередню. Лише під час запуску панелі, ще до першого
    // малювання (щоб не блимала святкова тема). true - налаштування змінено, їх треба зберегти
    public static bool Expire(Cfg c)
    {
        if (c.SeasonTheme.Length == 0 || DateTime.Today < c.SeasonUntil) return false;
        if (c.ThemeId == c.SeasonTheme)
        {
            string title = Themes.Get(c.SeasonTheme, c).Title;
            c.ThemeId = Known(c.SeasonPrev) ? c.SeasonPrev : "dark";
            ended = L.T("Тему «") + title + L.T("» вимкнено, повернулась «") + Themes.Get(c.ThemeId, c).Title + L.Q("»");
        }
        c.SeasonTheme = ""; c.SeasonPrev = ""; c.SeasonUntil = DateTime.MinValue;
        return true;
    }

    static bool Known(string id)
    {
        foreach (Theme t in Themes.All) if (t.Id == id) return id.Length > 0;
        return false;
    }

    // Пропонує свята по черзі: день народження першим; якщо від нього відмовились («Не зараз»,
    // «Більше не пропонувати» чи закрили вікно), одразу пропонується наступне свято, що діє сьогодні
    static void Offer(NeoBar bar)
    {
        for (int i = 0; i < 4; i++)
        {
            if (NeoBar.Paused) return;          // гра на передньому плані - не турбуємо
            if (OfferOne(bar)) return;
        }
    }

    // true - більше нічого не пропонувати (тему ввімкнено або свят немає)
    static bool OfferOne(NeoBar bar)
    {
        Cfg c = bar.Config;
        Occasion o = Today(c);
        if (o == null) return true;
        string title = Themes.Get(o.Theme, c).Title;
        string prev = c.SeasonTheme.Length > 0 && Known(c.SeasonPrev) ? c.SeasonPrev : c.ThemeId;
        string until = o.Until.AddDays(-1).ToString("d MMMM", Widgets.UK);
        string text;
        if (o.Theme == "birthday")
            text = L.T("З днем народження! Увімкнути святкову тему «") + title + L.T("»?\n\nВона буде до ") + until +
                   L.T(" включно, а потім, під час наступного запуску, панель повернеться до теми «") + Themes.Get(prev, c).Title + L.Q("».");
        else
            text = (DateTime.Today < o.Day ? L.T("Наближається ") + o.Name + " (" + o.Day.ToString("d MMMM", Widgets.UK) + ")" : o.Name + L.T(" - святкові дні")) +
                   L.T(". Увімкнути святкову тему «") + title + L.T("»?\n\nВона буде до ") + until +
                   L.T(" включно, а потім, під час наступного запуску, панель повернеться до теми «") + Themes.Get(prev, c).Title + L.Q("».");
        int r;
        SeasonFx.Start(bar, o.Theme);      // ефект на столі (салюти, кажани, сніг, писанки) і мелодія теми
        asking = true;
        try { r = SbDialog.Show(bar, null, L.T("Святкова тема"), text, L.T("Увімкнути"), L.T("Не зараз"), L.T("Більше не пропонувати")); }
        finally { asking = false; }
        if (r == 0)
        {
            c.SeasonPrev = prev;
            c.SeasonTheme = o.Theme;
            c.SeasonUntil = o.Until;
            c.ThemeId = o.Theme;
            Save(bar, true);
            return true;
        }
        else if (r == 2)
        {
            List<string> keep = new List<string>();
            foreach (string s in c.SeasonSkip.Split(','))
            {
                // старі роки забуваються
                string k = s.Trim();
                int dash = k.LastIndexOf('-'), y;
                if (k.Length > 0 && dash > 0 && int.TryParse(k.Substring(dash + 1), out y) && y >= DateTime.Today.Year - 1) keep.Add(k);
            }
            keep.Add(Key(o));
            c.SeasonSkip = string.Join(",", keep.ToArray());
            Save(bar, false);
        }
        else later.Add(Key(o));
        return false;
    }

    static void Save(NeoBar bar, bool reload)
    {
        try { Store.Save(bar.CfgPath, bar.Config, bar.Entries); } catch (Exception ex) { Util.ErrorLog(ex); return; }
        if (reload) bar.Reload();
    }
}
