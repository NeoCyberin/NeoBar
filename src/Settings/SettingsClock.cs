using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

// Вікно налаштувань: годинник і календар у розділі «Місце й погода» - часовий пояс, формат часу,
// точність годинника (сервер часу, синхронізація), свята й сповіщення про них.
// Застосовується разом з рештою налаштувань («Застосувати» / «Готово»); «Перевірити», «Синхронізувати»
// і «Встановити пояс за місцем» - дії одразу.
sealed partial class SettingsForm
{
    SCombo cbTz, cbHolCountry, cbHolAt, cbBDay, cbBMonth, cbEaster, cbXmas;
    CheckBox chClock24, chClockSec, chSyncStart, chHolOnline, chHolWorld, chWeekNum, chHolNotify;
    SNum nHolDelay;
    SCard tzCard, ntpCard;
    readonly List<string> tzIds = new List<string>();     // id поясу для кожного рядка cbTz (рядок 0 - автоматично)
    Timer ntpTimer;
    string bdWas = "";

    void BuildClock(SPage left)
    {
        // ---------- часовий пояс і формат ----------
        tzCard = left.Card(L.T("Час"));
        cbTz = Cb(330);
        cbTz.Items.Add(L.T("Автоматично за місцем"));
        tzIds.Add("");
        try
        {
            List<TimeZoneInfo> zs = new List<TimeZoneInfo>(TimeZoneInfo.GetSystemTimeZones());
            zs.Sort(delegate (TimeZoneInfo a, TimeZoneInfo b) { int k = a.BaseUtcOffset.CompareTo(b.BaseUtcOffset); return k != 0 ? k : string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCulture); });
            foreach (TimeZoneInfo z in zs) { cbTz.Items.Add(z.DisplayName); tzIds.Add(z.Id); }
        }
        catch { }
        cbTz.SelectedIndexChanged += delegate { if (!loading) tzCard.Queue(); };
        SRow tr = tzCard.Row(L.T("Часовий пояс"), "", cbTz);
        tr.DescFn = delegate { return TzText(); };
        Button bFix = Bt(L.T("Встановити за місцем"), "primary g:locate", 220, delegate { ClockData.ApplyTz(true); tzCard.Queue(); tzCard.Invalidate(); });
        SRow fr = tzCard.Row(L.T("Пояс системи не збігається з місцем"), L.T("Windows показує інший пояс, ніж у місті з налаштувань"), bFix);
        fr.Indent = true;
        fr.When = delegate
        {
            string s;
            return cbTz.SelectedIndex == 0 && c.LocAllow && Widgets.WTz.Length > 0 && !ClockData.TzMatches(out s);
        };
        chClock24 = Tg();
        tzCard.Row(L.T("24-годинний формат"), "", chClock24);
        chClockSec = Tg();
        tzCard.Row(L.T("Секунди на годиннику на столі"), L.T("На панелі й у розширеній панелі годинника секунди видно завжди"), chClockSec);

        // ---------- точність годинника ----------
        ntpCard = left.Card(L.T("Точність годинника"));
        SRow nr = ntpCard.Row(L.T("Сервер часу"), "",
            Bt(L.T("Перевірити"), "g:refresh", 140, delegate { TimeTools.CheckNtp(); ntpCard.Invalidate(); }),
            Bt(L.T("Синхронізувати"), "primary", 160, delegate { TimeTools.SyncNow(); ntpCard.Invalidate(); }));
        nr.DescFn = delegate
        {
            string ntp = TimeTools.SyncFile != null ? L.T("Синхронізація…") : (TimeTools.NtpBusy ? L.T("Перевірка через сервер часу…") : (TimeTools.NtpText.Length > 0 ? TimeTools.NtpText : L.T("Ще не перевірялось")));
            string sync = TimeTools.SyncMsg.Length > 0 ? TimeTools.SyncMsg : (ClockData.SyncNote.Length > 0 ? ClockData.SyncNote : (Elevated.IsAdmin ? "" : L.T("Синхронізація системного часу потребує прав адміністратора.")));
            return ntp + (sync.Length > 0 ? "\n" + sync : "");
        };
        chSyncStart = Tg();
        ntpCard.Row(L.T("Синхронізувати під час запуску панелі"), L.T("Через 20 секунд після запуску, коли вже є мережа (потрібні права адміністратора)"), chSyncStart);

        // ---------- календар і свята ----------
        SCard hc = left.Card(L.T("Календар і свята"));
        cbHolCountry = Cb(300);
        cbHolCountry.Items.Add(L.T("Автоматично за місцем"));
        foreach (string[] cc in Holidays.Countries) cbHolCountry.Items.Add(cc[1]);
        SRow cr = hc.Row(L.T("Країна свят"), "", cbHolCountry);
        cr.DescFn = delegate
        {
            if (cbHolCountry.SelectedIndex > 0) return L.T("Державні свята вибраної країни");
            return c.LocAllow ? L.T("За місцем: ") + Holidays.CountryName(Holidays.Cc(c)) : L.T("Розташування не визначається - ") + Holidays.CountryName("UA") + L.T(" (за замовчуванням)");
        };
        chHolOnline = Tg();
        hc.Row(L.T("Брати свята з інтернету"), L.T("date.nager.at; оновлюються під час кожного запуску панелі"), chHolOnline);
        chHolWorld = Tg();
        hc.Row(L.T("Світові дні"), L.T("День святого Валентина, День матері, Хелловін, Різдво й інші важливі дати (зелена позначка)"), chHolWorld);
        // день народження - необов'язково: з'являється в календарі й вмикає запрошення на святкову тему
        cbBDay = Cb(84);
        cbBDay.Items.Add("-");
        for (int d = 1; d <= 31; d++) cbBDay.Items.Add(d.ToString());
        cbBMonth = Cb(150);
        cbBMonth.Items.Add(L.T("не вказано"));
        for (int m = 1; m <= 12; m++) cbBMonth.Items.Add(new DateTime(2000, m, 1).ToString("MMMM", Widgets.UK));
        hc.Row(L.T("Мій день народження"), L.T("Необов'язково. Додається в календар; у цей день панель запропонує святкову тему на тиждень"), cbBDay, cbBMonth);
        // дати Великодня й Різдва для святкових тем (запрошення за 3 доби до свята)
        cbEaster = Cb(360, L.T("Автоматично (за країною)"), L.T("Православний (юліанська пасхалія)"), L.T("Католицький (григоріанський календар)"));
        SRow er = hc.Row(L.T("Великдень для святкової теми"), "", cbEaster);
        er.DescFn = delegate
        {
            string cur = cbEaster.SelectedIndex == 1 ? "orthodox" : cbEaster.SelectedIndex == 2 ? "catholic" : "";
            Cfg t = new Cfg(); t.EasterRite = cur; t.Country = c.Country; t.LocAllow = c.LocAllow;
            int y = DateTime.Today.Year;
            DateTime d = Holidays.EasterFor(y, t);
            if (d.AddDays(7) < DateTime.Today) d = Holidays.EasterFor(y + 1, t);
            return L.T("Найближчий: ") + d.ToString("d MMMM yyyy", Widgets.UK) + L.T(". Запрошення на тему - за 3 доби до свята");
        };
        cbXmas = Cb(360, L.T("Автоматично (за країною)"), L.T("25 грудня (григоріанський календар)"), L.T("7 січня (юліанський календар)"));
        SRow xr = hc.Row(L.T("Різдво для святкової теми"), "", cbXmas);
        xr.DescFn = delegate
        {
            string cur = cbXmas.SelectedIndex == 1 ? "gregorian" : cbXmas.SelectedIndex == 2 ? "julian" : "";
            Cfg t = new Cfg(); t.XmasCal = cur; t.Country = c.Country; t.LocAllow = c.LocAllow;
            int y = DateTime.Today.Year;
            DateTime d = Holidays.ChristmasFor(y, t);
            if (d.AddDays(7) < DateTime.Today) d = Holidays.ChristmasFor(y + 1, t);
            return L.T("Найближче: ") + d.ToString("d MMMM yyyy", Widgets.UK) + L.T(". Запрошення на тему - за 3 доби до свята");
        };
        cbEaster.SelectedIndexChanged += delegate { hc.Invalidate(true); };
        cbXmas.SelectedIndexChanged += delegate { hc.Invalidate(true); };
        chWeekNum = Tg();
        hc.Row(L.T("Номери тижнів у календарі"), "", chWeekNum);
        chHolNotify = Tg();
        chHolNotify.CheckedChanged += delegate { cbHolAt.Enabled = nHolDelay.Enabled = chHolNotify.Checked; };
        hc.Row(L.T("Сповіщати про свята"), L.T("Власні дати додаються в календарі (клік по дню)"), chHolNotify);
        cbHolAt = Cb(110);
        for (int m = 0; m < 1440; m += 15) cbHolAt.Items.Add((m / 60).ToString("00") + ":" + (m % 60).ToString("00"));
        SRow ar = hc.Row(L.T("Час сповіщення"), "", cbHolAt);
        ar.Indent = true;
        nHolDelay = Nm(96, 0, 120);
        nHolDelay.Increment = 5;
        SRow dr = hc.Row(L.T("Пропущене сповіщення, хв"), L.T("ПК був вимкнений у час сповіщення - показати через стільки хвилин після запуску"), nHolDelay);
        dr.Indent = true;

        // стан сервера часу оновлюється сам, поки розділ відкритий
        ntpTimer = new Timer();
        ntpTimer.Interval = 700;
        ntpTimer.Tick += delegate { TimeTools.Poll(); if (ntpCard != null && !ntpCard.IsDisposed) ntpCard.Invalidate(); };
        left.VisibleChanged += delegate
        {
            if (left.Visible)
            {
                if (TimeTools.NtpAt == DateTime.MinValue || (DateTime.Now - TimeTools.NtpAt).TotalMinutes > 10) TimeTools.CheckNtp();
                ntpTimer.Start();
            }
            else ntpTimer.Stop();
        };
        Disposed += delegate { ntpTimer.Stop(); ntpTimer.Dispose(); };
    }

    // стан поясу під вибором: що зараз у Windows і що каже сайт погоди
    string TzText()
    {
        TimeZoneInfo local = TimeZoneInfo.Local;
        bool dst = false;
        try { dst = local.IsDaylightSavingTime(DateTime.Now); } catch { }
        string s = L.T("Зараз у Windows: ") + SysTz.Describe(local) + (dst ? L.T(" (літній час)") : "") + " · " + local.DisplayName;
        if (cbTz.SelectedIndex == 0)
        {
            if (!c.LocAllow) return s + L.T("\nАвтоматично - лише коли дозволено визначати розташування (вище).");
            string site;
            ClockData.TzMatches(out site);
            string src = Geo.Source == "windows" ? L.T("місце - служба розташування Windows") :
                (Geo.Source == "ip" || SysCheck.LocationOff().Length > 0 ? L.T("місце й пояс - за IP-адресою") : L.T("місце - з налаштувань"));
            return s + "\n" + site + " (" + src + ")";
        }
        return s;
    }

    void FillClock()
    {
        Holidays.Load();
        int ti = 0;
        if (!ClockData.AutoTz)
        {
            ti = tzIds.IndexOf(TimeZoneInfo.Local.Id);
            if (ti < 0) ti = 0;
        }
        cbTz.SelectedIndex = ti;
        chClock24.Checked = c.Clock24;
        chClockSec.Checked = c.ClockSec;
        chSyncStart.Checked = ClockData.SyncOnStart;
        int ci = 0;
        for (int i = 0; i < Holidays.Countries.Length; i++) if (Holidays.Countries[i][0] == Holidays.Country) ci = i + 1;
        cbHolCountry.SelectedIndex = ci;
        chHolOnline.Checked = Holidays.Online;
        chHolWorld.Checked = Holidays.World;
        DateTime bd;
        bool hasBd = Holidays.BirthdayIn(c, 2000, out bd);      // 2000 - високосний, 29 лютого не губиться
        cbBDay.SelectedIndex = hasBd ? bd.Day : 0;
        cbBMonth.SelectedIndex = hasBd ? bd.Month : 0;
        cbEaster.SelectedIndex = c.EasterRite == "orthodox" ? 1 : c.EasterRite == "catholic" ? 2 : 0;
        cbXmas.SelectedIndex = c.XmasCal == "gregorian" ? 1 : c.XmasCal == "julian" ? 2 : 0;
        chWeekNum.Checked = Holidays.WeekNumbers;
        chHolNotify.Checked = Holidays.NotifyOn;
        cbHolAt.SelectedIndex = Math.Max(0, Math.Min(cbHolAt.Items.Count - 1, Holidays.NotifyAt / 15));
        nHolDelay.Value = Math.Max(0, Math.Min(120, Holidays.NotifyDelay));
        cbHolAt.Enabled = nHolDelay.Enabled = chHolNotify.Checked;
        bdWas = c.Birthday;
    }

    // у копію налаштувань панелі (зберігається в neobar.ini разом з рештою)
    void ReadClock()
    {
        c.Clock24 = chClock24.Checked;
        c.ClockSec = chClockSec.Checked;
        int bdDay = cbBDay.SelectedIndex, bdMon = cbBMonth.SelectedIndex;
        c.EasterRite = cbEaster.SelectedIndex == 1 ? "orthodox" : cbEaster.SelectedIndex == 2 ? "catholic" : "";
        c.XmasCal = cbXmas.SelectedIndex == 1 ? "gregorian" : cbXmas.SelectedIndex == 2 ? "julian" : "";
        c.Birthday = bdDay > 0 && bdMon > 0 ? bdMon.ToString("00") + "-" + Math.Min(bdDay, DateTime.DaysInMonth(2000, bdMon)).ToString("00") : "";
    }

    // годинник і свята мають власні файли (clock.ini, holidays.ini) - пишемо після збереження neobar.ini
    void ApplyClock()
    {
        bool auto = cbTz.SelectedIndex <= 0;
        ClockData.AutoTz = auto;
        ClockData.SyncOnStart = chSyncStart.Checked;
        ClockData.Save();
        if (!auto)
        {
            int i = cbTz.SelectedIndex;
            if (i < tzIds.Count && !string.Equals(tzIds[i], TimeZoneInfo.Local.Id, StringComparison.OrdinalIgnoreCase))
                ClockData.SetTzManual(tzIds[i], (string)cbTz.Items[i]);
        }
        else ClockData.CheckTz();

        string country = cbHolCountry.SelectedIndex > 0 ? Holidays.Countries[cbHolCountry.SelectedIndex - 1][0] : "";
        bool reload = country != Holidays.Country || chHolOnline.Checked != Holidays.Online || chHolWorld.Checked != Holidays.World;
        Holidays.Country = country;
        Holidays.Online = chHolOnline.Checked;
        Holidays.World = chHolWorld.Checked;
        Holidays.WeekNumbers = chWeekNum.Checked;
        Holidays.NotifyOn = chHolNotify.Checked;
        Holidays.NotifyAt = Math.Max(0, cbHolAt.SelectedIndex) * 15;
        Holidays.NotifyDelay = (int)nHolDelay.Value;
        Holidays.Save();
        if (reload || c.Birthday != bdWas) Holidays.Reset();       // календарі перемалюються
        bdWas = c.Birthday;
        ClockData.Restyle();            // формат часу на годинниках на столі
    }
}
