using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;

// ======================================================================
//  Моделі даних, налаштування, збереження neobar.ini, імпорт/експорт
// ======================================================================

enum Kind { Item, Separator, Header, Widget }
enum IconStyle { Tile, Flat, Glass, Neon, Plate, Spark, Heart, Leaf, Sketch, Pumpkin, Ornament, Egg, Balloon }

sealed class Entry
{
    public Kind Kind;
    public string Name = "", Target = "", IconSpec = "", Tags = "", GMode = "";
    public string Opt = "";           // параметр віджета (напр. id тому для «Диска»)
    public Color GroupColor = Color.Empty;
    public bool Collapsed, Hidden;
    public bool Off;                  // віджет схований з панелі: лишається в списку (напр. показаний лише на робочому столі)
    public bool Fold;                 // (розрахунок) група показана як папка
    public bool Fix;                  // (розрахунок) віджет або автороздільник у нерухомій зоні (не прокручується)
    public bool Auto;                 // автоматичний роздільник після віджетів (не зберігається й не видаляється)
    public Color Col;
    public Bitmap Icon;
    public int Pos, Len;
    public float Hv;                  // стан анімації наведення 0..1
    public string Sig = "";           // підпис віджета (для виявлення змін)
    public GroupSpan Gs;              // (розрахунок) смуга розгорнутої групи
    public Entry Head;                // (розрахунок) заголовок групи, до якої належить елемент
    public bool Gone;                 // (розрахунок) програму чи файл видалено - елемент приглушується
    public List<Entry> Kids = new List<Entry>(); // (розрахунок) елементи групи

    public string Full
    {
        get { return Environment.ExpandEnvironmentVariables((Target ?? "").Trim()); }
    }

    // пошук за назвою, тегами та ціллю
    public bool Matches(string q)
    {
        if (string.IsNullOrEmpty(q)) return true;
        foreach (string w in q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = w.TrimStart('#');
            if (t.Length == 0) continue;
            bool tagOnly = w[0] == '#';
            bool ok = Has(Tags, t);
            if (!tagOnly) ok = ok || Has(Name, t) || Has(Target, t);
            if (!ok) return false;
        }
        return true;
    }

    // «ігри, steam» -> «#ігри #steam»
    public string TagsText()
    {
        List<string> l = new List<string>();
        foreach (string t in (Tags ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            l.Add("#" + t.TrimStart('#'));
        return string.Join(" ", l.ToArray());
    }

    static bool Has(string hay, string needle)
    {
        return (hay ?? "").IndexOf(needle, StringComparison.CurrentCultureIgnoreCase) >= 0;
    }
}

sealed class GroupSpan
{
    public int Start, End;
    public Color Color;
    public bool Pill;   // згорнута/порожня група на H-панелі: компактна «пігулка»
}

sealed class Cfg
{
    // вигляд
    public int Width = 76, IconSize = 36, Opacity = -1, Blur = -1;
    public bool Labels = true;
    public string LabelPos = "below"; // below | above  — підпис під чи над іконкою
    public string Side = "right", ThemeId = "dark";
    public Color CBg = Color.FromArgb(22, 24, 30);
    public Color CHover = Color.FromArgb(52, 57, 70);
    public Color CText = Color.FromArgb(205, 210, 220);
    public Color CAccent = Color.FromArgb(59, 130, 246);
    // шрифт
    public string FontName = "", FontStyle = "regular";
    public float FontSize = 8f;
    // поведінка
    public string Mode = "fixed";      // fixed | autohide
    public string Monitor = "";        // "" = основний, інакше DeviceName
    public bool Compact;               // угорі/внизу: вікно по вмісту й по центру
    public int CornerGap = 48;         // автосховок: відступ від кутів екрана, px
    public bool ShowSearch = true;
    public bool FsHide = true;         // ховатися під час повноекранних програм
    public bool UpdCheck = true;       // при запуску дивитися на GitHub, чи вийшла нова версія
    public bool Hotkeys = true;
    public bool SearchSuggest = true;       // пошук пропонує додати знайдені програми на панель
    public int SearchKey = (int)(System.Windows.Forms.Keys.S | System.Windows.Forms.Keys.Alt | System.Windows.Forms.Keys.Shift);   // гаряча клавіша пошуку
    public bool AlwaysAdmin, AdminAuto;           // завжди з правами адміністратора (через Планувальник завдань, без запиту UAC)
    public bool FileSearch = true;     // вкладка «Комп'ютер» у вікні пошуку
    public int FsEngine;               // 0 авто, 1 індекс NTFS, 2 пошук Windows, 3 обхід дисків
    public string GroupMode = "open";  // open | fold
    public int HoverFx = 1;            // 0 нічого, 1 збільшення, 2 підйом, 3 свічення, 4 збільшення+свічення
    public bool Anim = true;
    public bool SecWatch = true;      // «Безпека»: стежити у фоні (значок у треї; не під час гри)
    public bool SecUsed;              // віджет «Безпека» хоч раз був на панелі: тоді стеження й значок у треї працюють і після його прибирання
    // віджети
    public string City = L.T("Київ"), CityName = "";
    public double Lat = double.NaN, Lon = double.NaN;
    public bool LocAllow = false;      // користувач дозволив визначати розташування (інакше - лише вручну)
    public string LocMode = "manual";    // manual | auto (служба розташування Windows, інакше за IP)
    public string Country = "";        // код країни місця (для свят), визначається автоматично
    public bool Fahrenheit, Clock24 = true, ClockSec;
    public bool PerfCpu = true, PerfGpu = true, PerfRam = true;
    // температури в треї: окремий значок на кожну, зі своїм кольором
    public bool TrayCpu, TrayGpu, TrayHot;
    public Color TrayCpuCol = Color.FromArgb(70, 170, 255), TrayGpuCol = Color.FromArgb(90, 200, 110), TrayHotCol = Color.FromArgb(255, 150, 60);
    public bool Welcomed, QuickDone;      // QuickDone - швидке налаштування вже показано
    // свята
    public string EasterRite = "";        // Великдень для святкової теми: "" - за країною, "orthodox" - православний, "catholic" - католицький
    public string XmasCal = "";           // Різдво для святкової теми: "" - за країною, "gregorian" - 25 грудня, "julian" - 7 січня
    public string Birthday = "";          // день народження «MM-dd» (необов'язково) - у календарі й для святкової теми
    public string SeasonTheme = "", SeasonPrev = "";   // увімкнена святкова тема і тема, до якої повернутися
    public DateTime SeasonUntil = DateTime.MinValue;   // до якого дня (не включно) діє святкова тема
    public string SeasonSkip = "";
    public string CleanFlip = "";         // очищення: категорії, увімкнені/вимкнені не так, як за замовчуванням        // «більше не пропонувати»: «id-рік» через кому
    // шпалери
}

sealed class Theme
{
    public string Id, Title, Font = "Segoe UI";
    public Color Bg, Hover, Text, Head, Sep, Edge, Accent;
    public int Radius = 6, EdgeW = 1, Opacity = 100;
    public bool Glass, Sketchy;
    public float IconScale = 1f;
    public IconStyle Style = IconStyle.Tile;
    public Color[] Palette;
    public Color[] Grad;           // багатоколірне градієнтне тло (Festive.Fill); Bg - суцільний колір для вікон і полів
}

// Системні елементи, які можна додати одним кліком
static class SysCatalog
{
    // назва, ціль, ключ тематичної іконки
    public static readonly string[][] Items = new string[][]
    {
        new string[] { L.T("Цей ПК"), "shell:MyComputerFolder", "pc" },
        new string[] { L.T("Кошик"), "shell:RecycleBinFolder", "bin" },
        new string[] { L.T("Особиста папка"), @"%USERPROFILE%", "home" },
        new string[] { L.T("Робочий стіл"), "shell:Desktop", "desktop" },
        new string[] { L.T("Документи"), @"%USERPROFILE%\Documents", "doc" },
        new string[] { L.T("Завантаження"), @"%USERPROFILE%\Downloads", "download" },
        new string[] { L.T("Зображення"), @"%USERPROFILE%\Pictures", "pictures" },
        new string[] { L.T("Музика"), @"%USERPROFILE%\Music", "music" },
        new string[] { L.T("Відео"), @"%USERPROFILE%\Videos", "video" },
        new string[] { L.T("Мережа"), "shell:NetworkPlacesFolder", "web" },
        new string[] { L.T("Панель керування"), "control.exe", "settings" },
        new string[] { L.T("Параметри Windows"), "ms-settings:", "settings" },
        new string[] { L.T("Диспетчер завдань"), "taskmgr.exe", "taskmgr" },
        new string[] { L.T("Командний рядок"), "cmd.exe", "terminal" },
        new string[] { L.T("Калькулятор"), "calc.exe", "calc" },
        new string[] { L.T("Блокнот"), "notepad.exe", "edit" },
        new string[] { L.T("Програми та компоненти"), "appwiz.cpl", "settings" },
        new string[] { L.T("Заблокувати ПК"), "rundll32.exe user32.dll,LockWorkStation", "lock" },
        // системні утиліти (є у Windows 11 і Windows 10; більшість - ще з Windows XP)
        new string[] { L.T("Диспетчер пристроїв"), "devmgmt.msc", "pc" },
        new string[] { L.T("Керування дисками"), "diskmgmt.msc", "save" },
        new string[] { L.T("Редактор реєстру"), "regedit.exe", "code" },
        new string[] { L.T("Конфігурація системи"), "msconfig.exe", "settings" },
        new string[] { L.T("Служби"), "services.msc", "settings" },
        new string[] { L.T("Перегляд подій"), "eventvwr.msc", "history" },
        new string[] { L.T("Планувальник завдань"), "taskschd.msc", "calendar" },
        new string[] { L.T("Керування комп'ютером"), "compmgmt.msc", "admin" },
        new string[] { L.T("Монітор ресурсів"), "resmon.exe", "taskmgr" },
        new string[] { L.T("Системний монітор"), "perfmon.exe", "taskmgr" },
        new string[] { L.T("Відомості про систему"), "msinfo32.exe", "pc" },
        new string[] { L.T("Діагностика DirectX"), "dxdiag.exe", "game" },
        new string[] { L.T("Очищення диска"), "cleanmgr.exe", "bin" },
        new string[] { L.T("Оптимізація дисків"), "dfrgui.exe", "save" },
        new string[] { L.T("Брандмауер Windows"), "wf.msc", "lock" },
        new string[] { L.T("Мережеві підключення"), "ncpa.cpl", "wifi" },
        new string[] { L.T("Звук"), "mmsys.cpl", "volume" },
        new string[] { L.T("Електроживлення"), "powercfg.cpl", "power" },
        new string[] { L.T("Дата й час"), "timedate.cpl", "calendar" },
        new string[] { L.T("Властивості системи"), "sysdm.cpl", "pc" },
        new string[] { L.T("Облікові записи користувачів"), "netplwiz.exe", "user" },
        new string[] { L.T("Віддалений робочий стіл"), "mstsc.exe", "desktop" },
        new string[] { "PowerShell", "powershell.exe", "terminal" },
        new string[] { L.T("Таблиця символів"), "charmap.exe", "edit" },
        new string[] { L.T("Екранна клавіатура"), "osk.exe", "keyboard" },
        new string[] { L.T("Екранна лупа"), "magnify.exe", "search" },
        new string[] { "Paint", "mspaint.exe", "photo" },
        new string[] { "Windows Update", "ms-settings:windowsupdate", "download" },
        new string[] { L.T("Безпека Windows"), "windowsdefender:", "lock" }
    };

    // CSIDL для системних папок (щоб показувати справжні іконки Windows)
    public static int Csidl(string target)
    {
        string t = (target ?? "").Trim().ToLowerInvariant();
        switch (t)
        {
            case "shell:mycomputerfolder": return 0x11;
            case "shell:recyclebinfolder": return 0x0A;
            case "shell:desktop": return 0x00;
            case "shell:networkplacesfolder": return 0x12;
        }
        return -1;
    }
}

// Віджети, доступні для додавання: ключ, назва
static class WidgetCatalog
{
    // ключ, коротка назва (для списків), повна назва (підказка), група (за ними впорядковані списки й чипи):
    // 0 - час і довкілля, 1 - система, 2 - інструменти, 3 - обслуговування дисків
    public static readonly string[][] Items = new string[][]
    {
        new string[] { "clock", L.T("Час і дата"), L.T("Час і дата: годинник та календар, секундомір, таймери, будильники"), "0" },
        new string[] { "weather", L.T("Погода"), L.T("Погода та прогноз"), "0" },
        new string[] { "battery", L.T("Батарея"), L.T("Батарея: заряд, знос, цикли"), "0" },
        new string[] { "perf", L.T("Монітор системи"), L.T("Монітор системи: ЦП, відеокарта, пам'ять і характеристики комплектуючих"), "1" },
        new string[] { "net", L.T("Мережа"), L.T("Мережа: швидкість, пінг, адаптери та Wi-Fi"), "1" },
        new string[] { "disk", L.T("Простір"), L.T("Простір: диски й флешки, вільне місце та стан"), "1" },
        new string[] { "security", L.T("Безпека"), L.T("Безпека: підозрілі процеси, автозапуск, служби й небезпечні налаштування Windows (евристики без бази сигнатур)"), "1" },
        new string[] { "notes", L.T("Нотатки"), L.T("Нотатки на робочому столі"), "2" },
        new string[] { "color", L.T("Палітра"), L.T("Палітра: піпетка з екрана, вибір кольору й перетворення в колірні моделі"), "2" },
        new string[] { "dice", L.T("Кубик"), L.T("Кубик: випадкове число або вибір зі своїх назв (що подивитися, у що пограти)"), "2" },
        new string[] { "qr", L.T("QR-код"), L.T("QR-код: текст, посилання, Wi-Fi, контакт, email, SMS, телефон, геоточка; логотип у центрі"), "2" },
        new string[] { "crypt", L.T("Шифрування"), L.T("Шифрування тексту й файлів (AES-256), хеш-суми файлів; пункти в контекстному меню файлів"), "2" },
        new string[] { "clean", L.T("Очищення"), L.T("Очищення Windows від сміття: тимчасові файли, кеші браузерів і програм, звіти про помилки"), "3" },
        new string[] { "recover", L.T("Відновлення"), L.T("Відновлення видалених файлів і папок з диска чи флешки (також після форматування)"), "3" }
    };

    public static string Full(string kind)
    {
        foreach (string[] w in Items) if (w[0] == kind) return w[2];
        return kind;
    }

    public static string Title(string kind)
    {
        foreach (string[] w in Items) if (w[0] == kind) return w[1];
        return kind;
    }
}

// ----------------------------------------------------------------------
//  Збереження / читання neobar.ini
// ----------------------------------------------------------------------
static class Store
{
    static bool B(string v)
    {
        v = v.ToLowerInvariant();
        return !(v == "no" || v == "false" || v == "0" || v == "off");
    }

    static string Yn(bool b) { return b ? "yes" : "no"; }

    static string Num(double d) { return d.ToString("0.#####", CultureInfo.InvariantCulture); }

    static void ParseOpt(Cfg cfg, string s)
    {
        string[] kv = s.Split(new[] { ' ', '=', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
        if (kv.Length < 2) return;
        string key = kv[0].ToLowerInvariant();
        string val = kv[1].Trim();
        string lv = val.ToLowerInvariant();
        int n;
        double d;
        Color col;
        switch (key)
        {
            case "width":
                if (int.TryParse(val, out n)) cfg.Width = Math.Max(56, Math.Min(240, n));
                break;
            case "iconsize":
                if (int.TryParse(val, out n)) cfg.IconSize = Math.Max(24, Math.Min(96, n));
                break;
            case "side":
                cfg.Side = (lv == "left" || lv == "top" || lv == "bottom") ? lv : "right";
                break;
            case "labels": cfg.Labels = B(val); break;
            case "labelpos": cfg.LabelPos = lv == "above" ? "above" : "below"; break;
            case "theme": cfg.ThemeId = lv; break;
            case "easterrite": cfg.EasterRite = lv == "orthodox" || lv == "catholic" ? lv : ""; break;
            case "xmascal": cfg.XmasCal = lv == "gregorian" || lv == "julian" ? lv : ""; break;
            case "birthday":
            {
                DateTime bd;
                cfg.Birthday = DateTime.TryParseExact(val.Trim(), "MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out bd) ? val.Trim() : "";
                break;
            }
            case "season":
            {
                // id|до (yyyy-MM-dd)|попередня тема
                string[] sp = val.Split('|');
                DateTime su;
                if (sp.Length >= 3 && DateTime.TryParseExact(sp[1].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out su))
                { cfg.SeasonTheme = sp[0].Trim().ToLowerInvariant(); cfg.SeasonUntil = su; cfg.SeasonPrev = sp[2].Trim().ToLowerInvariant(); }
                break;
            }
            case "seasonskip": cfg.SeasonSkip = lv; break;
            case "cleanflip": cfg.CleanFlip = lv; break;
            case "opacity":
                if (int.TryParse(val, out n)) cfg.Opacity = Math.Max(0, Math.Min(100, n));
                break;
            case "blur":
                cfg.Blur = lv == "auto" ? -1 : (B(val) ? 1 : 0);
                break;
            case "cbg": col = Util.ParseColor(val); if (!col.IsEmpty) cfg.CBg = col; break;
            case "chover": col = Util.ParseColor(val); if (!col.IsEmpty) cfg.CHover = col; break;
            case "ctext": col = Util.ParseColor(val); if (!col.IsEmpty) cfg.CText = col; break;
            case "caccent": col = Util.ParseColor(val); if (!col.IsEmpty) cfg.CAccent = col; break;

            case "font": cfg.FontName = val; break;
            case "fontstyle":
                cfg.FontStyle = (lv == "bold" || lv == "italic" || lv == "bolditalic") ? lv : "regular";
                break;
            case "fontsize":
                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    cfg.FontSize = (float)Math.Max(6, Math.Min(20, d));
                break;

            case "mode": cfg.Mode = lv == "autohide" ? "autohide" : "fixed"; break;
            case "monitor": cfg.Monitor = val; break;
            case "compact": cfg.Compact = B(val); break;
            case "cornergap":
                if (int.TryParse(val, out n)) cfg.CornerGap = Math.Max(0, Math.Min(300, n));
                break;
            case "search": cfg.ShowSearch = B(val); break;
            case "fshide": cfg.FsHide = B(val); break;
            case "updcheck": cfg.UpdCheck = B(val); break;
            case "hotkeys": cfg.Hotkeys = B(val); break;
            case "searchsuggest": cfg.SearchSuggest = B(val); break;
            case "searchkey": { int sk; if (int.TryParse(val.Trim(), out sk) && sk != 0) cfg.SearchKey = sk; } break;
            case "admin": cfg.AlwaysAdmin = B(val); break;
            case "adminauto": cfg.AdminAuto = B(val); break;
            case "filesearch": cfg.FileSearch = B(val); break;
            case "fsengine":
                if (int.TryParse(val, out n)) cfg.FsEngine = Math.Max(0, Math.Min(3, n));
                break;
            case "groupmode": cfg.GroupMode = lv == "fold" ? "fold" : "open"; break;
            case "hoverfx":
                if (int.TryParse(val, out n)) cfg.HoverFx = Math.Max(0, Math.Min(4, n));
                break;
            case "anim": cfg.Anim = B(val); break;
            case "secwatch": cfg.SecWatch = B(val); break;
            case "secused": cfg.SecUsed = B(val); break;

            case "city": cfg.City = val; break;
            case "cityname": cfg.CityName = val; break;
            case "lat":
                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) cfg.Lat = d;
                break;
            case "lon":
                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) cfg.Lon = d;
                break;
            case "fahrenheit": cfg.Fahrenheit = B(val); break;
            case "clock24": cfg.Clock24 = B(val); break;
            case "clocksec": cfg.ClockSec = B(val); break;
            case "traycpu": cfg.TrayCpu = B(val); break;
            case "traygpu": cfg.TrayGpu = B(val); break;
            case "trayhot": cfg.TrayHot = B(val); break;
            case "traycpucol": col = Util.ParseColor(val); if (!col.IsEmpty) cfg.TrayCpuCol = col; break;
            case "traygpucol": col = Util.ParseColor(val); if (!col.IsEmpty) cfg.TrayGpuCol = col; break;
            case "trayhotcol": col = Util.ParseColor(val); if (!col.IsEmpty) cfg.TrayHotCol = col; break;
            case "perfcpu": cfg.PerfCpu = B(val); break;
            case "perfgpu": cfg.PerfGpu = B(val); break;
            case "perfram": cfg.PerfRam = B(val); break;
            case "welcomed": cfg.Welcomed = B(val); break;
            case "quickdone": cfg.QuickDone = B(val); break;
            case "locmode": cfg.LocMode = lv == "auto" ? "auto" : "manual"; break;
            case "locallow": cfg.LocAllow = B(val); break;
            case "country": cfg.Country = val.Trim().ToUpperInvariant(); break;
        }
    }

    public static void Load(string path, Cfg cfg, List<Entry> list)
    {
        string[] lines;
        try { lines = SafeFile.ReadLines(path); }
        catch { lines = new string[0]; }
        LoadLines(lines, cfg, list);
        Normalize(list);
    }

    // Віджети завжди на початку панелі (вгорі або ліворуч), між собою - у своєму порядку;
    // якщо є хоч один віджет, після них стоїть автоматичний роздільник
    public static void Normalize(List<Entry> list)
    {
        List<Entry> w = new List<Entry>(), rest = new List<Entry>();
        foreach (Entry e in list)
        {
            if (e.Auto) continue;
            if (e.Kind == Kind.Widget) w.Add(e); else rest.Add(e);
        }
        if (w.Count > 0) while (rest.Count > 0 && rest[0].Kind == Kind.Separator) rest.RemoveAt(0);   // роздільник уже є автоматичний
        // спершу показані на панелі, потім приховані (порядок усередині кожної групи зберігається)
        List<Entry> shown = new List<Entry>(), hid = new List<Entry>();
        foreach (Entry e in w) (e.Off ? hid : shown).Add(e);
        w.Clear(); w.AddRange(shown); w.AddRange(hid);
        list.Clear();
        list.AddRange(w);
        if (w.Count > 0) { Entry s = new Entry(); s.Kind = Kind.Separator; s.Auto = true; list.Add(s); }
        list.AddRange(rest);
    }

    public static int WidgetCount(List<Entry> list)
    {
        int n = 0;
        foreach (Entry e in list) if (e.Kind == Kind.Widget) n++;
        return n;
    }

    public static void LoadLines(string[] lines, Cfg cfg, List<Entry> list)
    {
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '@') { ParseOpt(cfg, line.Substring(1)); continue; }
            if (line == "-" || line.StartsWith("---", StringComparison.Ordinal))
            {
                Entry s = new Entry();
                s.Kind = Kind.Separator;
                list.Add(s);
                continue;
            }
            if (line[0] == '=')
            {
                string[] hp = line.Substring(1).Split('|');
                Entry h = new Entry();
                h.Kind = Kind.Header;
                h.Name = hp[0].Trim().TrimStart('⁠');
                if (hp.Length > 1) h.GroupColor = Util.ParseColor(hp[1].Trim());
                if (hp.Length > 2)
                {
                    string m = hp[2].Trim().ToLowerInvariant();
                    h.GMode = (m == "open" || m == "fold") ? m : "";
                }
                if (hp.Length > 3 && hp[3].Trim().ToLowerInvariant() == "closed") h.Collapsed = true;
                list.Add(h);
                continue;
            }
            if (line[0] == '~')
            {
                string[] wp = line.Substring(1).Split('|');
                Entry w = new Entry();
                w.Kind = Kind.Widget;
                w.Target = wp[0].Trim().ToLowerInvariant();
                if (wp.Length > 1) w.Opt = wp[1].Trim();
                if (wp.Length > 2) w.Off = wp[2].Trim().ToLowerInvariant() == "off";
                bool known = false;
                foreach (string[] wi in WidgetCatalog.Items) if (wi[0] == w.Target) known = true;
                if (!known) continue;                           // невідомий віджет відкидається
                w.Name = WidgetCatalog.Title(w.Target);
                // кожен віджет - лише один раз (кілька «Дисків» зливаються в один зі списком томів)
                Entry same = null;
                foreach (Entry x in list) if (x.Kind == Kind.Widget && x.Target == w.Target) same = x;
                if (same != null)
                {
                    if (!w.Off) same.Off = false;
                    if (w.Target == "disk" && w.Opt.Length > 0)
                        same.Opt = (same.Opt.Length > 0 ? same.Opt + "," : Vols.SysRoot() + ",") + w.Opt;
                    continue;
                }
                if (w.Target.Length > 0) list.Add(w);
                continue;
            }
            string[] p = line.Split('|');
            if (p.Length < 2) continue;
            Entry e = new Entry();
            e.Kind = Kind.Item;
            e.Name = p[0].Trim().TrimStart('⁠');
            e.Target = p[1].Trim();
            if (p.Length > 2) e.IconSpec = p[2].Trim();
            if (p.Length > 3) e.Tags = p[3].Trim();
            if (e.Target.Length == 0) continue;
            list.Add(e);
        }
    }

    static string CleanName(string n)
    {
        n = (n ?? "").Replace("|", "/").Replace("\r", " ").Replace("\n", " ").Trim();
        if (n.Length > 0 && "#@=-~".IndexOf(n[0]) >= 0) n = "⁠" + n;
        return n;
    }

    static string CleanTags(string t)
    {
        return (t ?? "").Replace("|", " ").Replace("\r", " ").Replace("\n", " ").Trim();
    }

    public static string Serialize(Cfg c, List<Entry> list)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(L.T("# NeoBar - файл налаштувань. Зазвичай його змінює вікно «Налаштування» (кнопка з шестернею на панелі)."));
        sb.AppendLine("@side " + c.Side);
        sb.AppendLine("@mode " + c.Mode);
        if (c.Monitor.Length > 0) sb.AppendLine("@monitor " + c.Monitor);
        sb.AppendLine("@compact " + Yn(c.Compact));
        sb.AppendLine("@cornergap " + c.CornerGap);
        sb.AppendLine("@width " + c.Width);
        sb.AppendLine("@iconsize " + c.IconSize);
        sb.AppendLine("@labels " + Yn(c.Labels));
        sb.AppendLine("@labelpos " + c.LabelPos);
        sb.AppendLine("@search " + Yn(c.ShowSearch));
        sb.AppendLine("@groupmode " + c.GroupMode);
        sb.AppendLine("@hoverfx " + c.HoverFx);
        sb.AppendLine("@anim " + Yn(c.Anim));
        sb.AppendLine("@secwatch " + Yn(c.SecWatch));
        sb.AppendLine("@secused " + Yn(c.SecUsed));
        sb.AppendLine("@fshide " + Yn(c.FsHide));
        sb.AppendLine("@updcheck " + Yn(c.UpdCheck));
        sb.AppendLine("@hotkeys " + Yn(c.Hotkeys));
        sb.AppendLine("@searchsuggest " + Yn(c.SearchSuggest));
        sb.AppendLine("@searchkey " + c.SearchKey);
        sb.AppendLine("@admin " + Yn(c.AlwaysAdmin));
        sb.AppendLine("@adminauto " + Yn(c.AdminAuto));
        sb.AppendLine("@filesearch " + Yn(c.FileSearch));
        sb.AppendLine("@fsengine " + c.FsEngine);
        sb.AppendLine("@theme " + c.ThemeId);
        if (c.EasterRite.Length > 0) sb.AppendLine("@easterrite " + c.EasterRite);
        if (c.XmasCal.Length > 0) sb.AppendLine("@xmascal " + c.XmasCal);
        if (c.Birthday.Length > 0) sb.AppendLine("@birthday " + c.Birthday);
        if (c.SeasonTheme.Length > 0) sb.AppendLine("@season " + c.SeasonTheme + "|" + c.SeasonUntil.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + c.SeasonPrev);
        if (c.SeasonSkip.Length > 0) sb.AppendLine("@seasonskip " + c.SeasonSkip);
        if (c.CleanFlip.Length > 0) sb.AppendLine("@cleanflip " + c.CleanFlip);
        if (c.Opacity >= 0) sb.AppendLine("@opacity " + c.Opacity);
        if (c.Blur >= 0) sb.AppendLine("@blur " + Yn(c.Blur == 1));
        sb.AppendLine("@cbg " + Util.Hex(c.CBg));
        sb.AppendLine("@chover " + Util.Hex(c.CHover));
        sb.AppendLine("@ctext " + Util.Hex(c.CText));
        sb.AppendLine("@caccent " + Util.Hex(c.CAccent));
        if (c.FontName.Length > 0) sb.AppendLine("@font " + c.FontName);
        sb.AppendLine("@fontstyle " + c.FontStyle);
        sb.AppendLine("@fontsize " + Num(c.FontSize));
        sb.AppendLine("@city " + c.City);
        if (c.CityName.Length > 0) sb.AppendLine("@cityname " + c.CityName);
        if (!double.IsNaN(c.Lat) && !double.IsNaN(c.Lon))
        {
            sb.AppendLine("@lat " + Num(c.Lat));
            sb.AppendLine("@lon " + Num(c.Lon));
        }
        sb.AppendLine("@fahrenheit " + Yn(c.Fahrenheit));
        sb.AppendLine("@clock24 " + Yn(c.Clock24));
        sb.AppendLine("@clocksec " + Yn(c.ClockSec));
        sb.AppendLine("@traycpu " + Yn(c.TrayCpu));
        sb.AppendLine("@traygpu " + Yn(c.TrayGpu));
        sb.AppendLine("@trayhot " + Yn(c.TrayHot));
        sb.AppendLine("@traycpucol " + Util.Hex(c.TrayCpuCol));
        sb.AppendLine("@traygpucol " + Util.Hex(c.TrayGpuCol));
        sb.AppendLine("@trayhotcol " + Util.Hex(c.TrayHotCol));
        sb.AppendLine("@perfcpu " + Yn(c.PerfCpu));
        sb.AppendLine("@perfgpu " + Yn(c.PerfGpu));
        sb.AppendLine("@perfram " + Yn(c.PerfRam));
        if (c.Welcomed) sb.AppendLine("@welcomed yes");
        if (c.QuickDone) sb.AppendLine("@quickdone yes");
        sb.AppendLine("@locmode " + c.LocMode);
        sb.AppendLine("@locallow " + Yn(c.LocAllow));
        if (c.Country.Length > 0) sb.AppendLine("@country " + c.Country);
        sb.AppendLine();
        foreach (Entry e in list)
        {
            if (e.Auto) continue;
            if (e.Kind == Kind.Separator)
            {
                sb.AppendLine("---");
            }
            else if (e.Kind == Kind.Widget)
            {
                string opt = (e.Opt ?? "").Replace("|", "");
                sb.AppendLine("~ " + e.Target + (opt.Length > 0 || e.Off ? " | " + opt : "") + (e.Off ? " | off" : ""));
            }
            else if (e.Kind == Kind.Header)
            {
                string line = "= " + CleanName(e.Name);
                bool hasCol = !e.GroupColor.IsEmpty;
                if (hasCol || e.GMode.Length > 0 || e.Collapsed) line += " | " + (hasCol ? Util.Hex(e.GroupColor) : "");
                if (e.GMode.Length > 0 || e.Collapsed) line += " | " + e.GMode;
                if (e.Collapsed) line += " | closed";
                sb.AppendLine(line);
            }
            else
            {
                string line = CleanName(e.Name) + " | " + (e.Target ?? "").Replace("|", "").Trim();
                string ic = (e.IconSpec ?? "").Replace("|", "").Trim();
                string tg = CleanTags(e.Tags);
                if (ic.Length > 0 || tg.Length > 0) line += " | " + ic;
                if (tg.Length > 0) line += " | " + tg;
                sb.AppendLine(line);
            }
        }
        return sb.ToString();
    }

    // ярлики елементів копіюються в links (див. Util.ImportLink); true - щось змінилося
    public static bool ImportLinks(List<Entry> list)
    {
        bool ch = false;
        foreach (Entry e in list)
        {
            if (e.Kind != Kind.Item) continue;
            string nt = Util.ImportLink(e.Target);
            if (nt != e.Target) { e.Target = nt; ch = true; }
        }
        return ch;
    }

    static List<string> LinkRefs(string path)
    {
        List<string> r = new List<string>();
        try
        {
            if (!File.Exists(path)) return r;
            Cfg c0 = new Cfg();
            List<Entry> l0 = new List<Entry>();
            Load(path, c0, l0);
            foreach (Entry e in l0)
            {
                string s = (e.Target ?? "").Trim();
                if (e.Kind == Kind.Item && s.StartsWith(Util.LinksRef, StringComparison.OrdinalIgnoreCase) && !r.Contains(s)) r.Add(s);
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return r;
    }

    public static void Save(string path, Cfg c, List<Entry> list)
    {
        ImportLinks(list);
        List<string> oldLinks = LinkRefs(path);            // ярлики, що були в попередньому файлі
        List<string> old = IconRefs(path);                 // іконки, що були в попередньому файлі
        SafeFile.Write(path, Serialize(c, list), new UTF8Encoding(true));
        try
        {
            // елемент видалили або змінили іконку: власну копію в icons, якої більше ніхто не використовує, прибираємо
            List<string> now = new List<string>();
            foreach (Entry e in list) if (e.IconSpec != null) now.Add(e.IconSpec.Trim());
            foreach (string s in old)
                if (!now.Exists(delegate (string x) { return string.Equals(x, s, StringComparison.OrdinalIgnoreCase); }))
                    Util.DeleteIconIfUnused(s);
            // так само для копій ярликів
            foreach (string s in oldLinks)
                if (!list.Exists(delegate (Entry x) { return x.Kind == Kind.Item && string.Equals((x.Target ?? "").Trim(), s, StringComparison.OrdinalIgnoreCase); }))
                    Util.DeleteLinkIfUnused(s);
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    static List<string> IconRefs(string path)
    {
        List<string> r = new List<string>();
        try
        {
            if (!File.Exists(path)) return r;
            Cfg c0 = new Cfg();
            List<Entry> l0 = new List<Entry>();
            Load(path, c0, l0);
            foreach (Entry e in l0)
            {
                string s = (e.IconSpec ?? "").Trim();
                if (s.Length > 0 && !r.Contains(s)) r.Add(s);
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return r;
    }

    // ---------- імпорт / експорт (один текстовий файл .nbpack: ini + використані іконки) ----------

    const string PackHead = "NEOBAR-PACK 1";

    static string NormSep(string p)
    {
        return p.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
    }

    public static int Export(string packPath, string baseDir, string iniPath)
    {
        List<string> files = new List<string>();
        files.Add(Path.GetFileName(iniPath));

        Cfg cfg = new Cfg();
        List<Entry> l = new List<Entry>();
        Load(iniPath, cfg, l);
        foreach (Entry e in l)
        {
            string ic = (e.IconSpec ?? "").Trim();
            if (ic.Length == 0 || ic.StartsWith("theme:", StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.IsPathRooted(ic) || ic.IndexOf('%') >= 0) continue;
            string icn = ic.Replace('\\', '/');
            if (File.Exists(Path.Combine(baseDir, NormSep(icn))) && !files.Contains(icn)) files.Add(icn);
        }
        if (Directory.Exists(Path.Combine(baseDir, "icons")))
        {
            foreach (string f in Directory.GetFiles(Path.Combine(baseDir, "icons")))
            {
                string rel = "icons/" + Path.GetFileName(f);
                if (!files.Contains(rel)) files.Add(rel);
            }
        }
        // копії ярликів, на які посилаються елементи панелі (links\), - інакше на іншому ПК ці елементи порожні
        foreach (Entry e in l)
        {
            string tg = (e.Target ?? "").Trim().Trim('"');
            if (!tg.StartsWith(Util.LinksRef, StringComparison.OrdinalIgnoreCase)) continue;
            string nm = tg.Substring(Util.LinksRef.Length);
            if (nm.Length == 0 || nm.IndexOfAny(new char[] { '\\', '/' }) >= 0) continue;
            string rel = "links/" + nm;
            if (!files.Contains(rel)) files.Add(rel);
        }
        // збережені іконки програм (щоб елементи мали справжню іконку навіть там, де програми ще немає)
        string icd = Path.Combine(baseDir, "cache", "icons");
        if (Directory.Exists(icd))
            foreach (string f in Directory.GetFiles(icd, "*.png")) files.Add("cache/icons/" + Path.GetFileName(f));
        // власні мелодії сигналів (не більше 25 МБ разом)
        string snd = Path.Combine(baseDir, "sounds");
        if (Directory.Exists(snd))
        {
            long tot = 0;
            foreach (string f in Directory.GetFiles(snd))
            {
                string ex = Path.GetExtension(f).ToLowerInvariant();
                if (ex != ".mp3" && ex != ".wav" && ex != ".wma") continue;
                long len = new FileInfo(f).Length;
                if (tot + len > 25L * 1024 * 1024) continue;
                tot += len;
                files.Add("sounds/" + Path.GetFileName(f));
            }
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine(PackHead);
        int n = 0;
        foreach (string rel in files)
        {
            string full = Path.Combine(baseDir, NormSep(rel));
            if (!File.Exists(full)) continue;
            sb.AppendLine("FILE " + rel.Replace('\\', '/'));
            sb.AppendLine(Convert.ToBase64String(File.ReadAllBytes(full)));
            n++;
        }
        File.WriteAllText(packPath, sb.ToString(), new UTF8Encoding(false));
        return n;
    }

    // повертає кількість розпакованих файлів; кидає виняток, якщо файл не схожий на пакет
    public static int Import(string packPath, string baseDir, string iniPath)
    {
        string[] lines = File.ReadAllLines(packPath, Encoding.UTF8);
        if (lines.Length == 0 || lines[0].Trim() != PackHead) throw new InvalidDataException(L.T("Це не файл налаштувань NeoBar."));

        string baseFull = Path.GetFullPath(baseDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string iniName = Path.GetFileName(iniPath);
        int n = 0;
        bool iniDone = false;
        for (int i = 1; i + 1 < lines.Length; i++)
        {
            if (!lines[i].StartsWith("FILE ", StringComparison.Ordinal)) continue;
            string rel = NormSep(lines[i].Substring(5).Trim());
            byte[] data = Convert.FromBase64String(lines[i + 1].Trim());
            i++;
            if (rel.Length == 0 || Path.IsPathRooted(rel) || rel.IndexOf("..", StringComparison.Ordinal) >= 0) continue;
            string full = Path.GetFullPath(Path.Combine(baseDir, rel));
            if (!full.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase)) continue;
            bool isIni = string.Equals(rel, iniName, StringComparison.OrdinalIgnoreCase);
            if (!isIni)
            {
                // із пакета беремо зображення, копії ярликів (лише в links\) і мелодії (лише в sounds\); ніколи не програми
                string ext = Path.GetExtension(rel).ToLowerInvariant();
                string top = rel.Replace('\\', '/').Split('/')[0].ToLowerInvariant();
                bool img = ext == ".png" || ext == ".ico" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif";
                bool lnk = top == "links" && (ext == ".lnk" || ext == ".url");
                bool snd = top == "sounds" && (ext == ".mp3" || ext == ".wav" || ext == ".wma");
                if (!img && !lnk && !snd) continue;
            }
            string dir = Path.GetDirectoryName(full);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (isIni)
            {
                // .old: «.bak» - попередня версія, її веде надійний запис
                try { if (File.Exists(full)) File.Copy(full, full + ".old", true); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                iniDone = true;
            }
            File.WriteAllBytes(full, data);
            n++;
        }
        if (!iniDone) throw new InvalidDataException(L.T("У файлі немає налаштувань панелі."));
        return n;
    }
}
