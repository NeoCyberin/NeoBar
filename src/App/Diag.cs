using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

// ======================================================================
//  Діагностика: що встановлено, чого бракує і що зробити (розділ «Діагностика» в налаштуваннях і вікно привітання)
// ======================================================================
sealed class CheckItem
{
    public string Title = "", Detail = "";
    public int State;            // 0 гаразд, 1 увага, 2 проблема, 3 до відома
    public readonly List<KeyValuePair<string, Action>> Acts = new List<KeyValuePair<string, Action>>();
    public CheckItem(string t, int st, string d) { Title = t; State = st; Detail = d; }
    public CheckItem Add(string text, Action a) { Acts.Add(new KeyValuePair<string, Action>(text, a)); return this; }
    public string Sig { get { string x = Title + "|" + State + "|" + Detail; foreach (KeyValuePair<string, Action> kv in Acts) x += "|" + kv.Key; return x; } }
}

static class SysCheck
{
    static string wmiOs;                    // назва системи з WMI (запит повільний - один раз за запуск)

    // Перевірки побудовані за можливостями програми: що їм потрібно і чи воно є на цьому комп'ютері.
    // full - разом із повільними (WMI, мережа, сервер часу; мережа - з кешу NetSources, перевіряється у фоні);
    // ui - вікно для повідомлень; refresh - перебудувати список (напр. коли перевірка мережі завершилась)
    public static List<CheckItem> Collect(NeoBar bar, Control ui, bool full, Action refresh)
    {
        List<CheckItem> l = new List<CheckItem>();
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        Cfg cfg = bar != null ? bar.Config : null;
        // перезапуск від адміністратора - лише коли панель уже працює (у першому знайомстві для цього окрема кнопка)
        bool running = false;
        try { running = bar != null && bar.IsHandleCreated && bar.Visible; } catch { }
        Action restart = delegate { if (Elevated.RestartAsAdmin()) bar.Quit(); };

        // ---------- права адміністратора ----------
        bool admin = Elevated.IsAdmin;
        if (admin)
            l.Add(new CheckItem(L.T("Права адміністратора"), 0, L.T("Є - працюють FPS в іграх, температура процесора, синхронізація часу, швидкий пошук файлів (індекс NTFS) і обслуговування дисків.")));
        else
        {
            CheckItem ad = new CheckItem(L.T("Права адміністратора"), 1, L.T("Немає - не працюють FPS в іграх, температура процесора (PawnIO), синхронізація часу, швидкий пошук файлів (індекс NTFS), перевірка й оптимізація дисків. Щоб так було завжди: Налаштування → Панель → «Завжди від імені адміністратора» (Windows спитає лише один раз)."));
            if (running) ad.Add(L.T("Перезапустити від адміністратора"), restart);
            l.Add(ad);
        }

        // ---------- тека програми: налаштування, профілі, кеш і журнали лежать поруч із NeoBar.exe ----------
        string wErr = WriteTest(appDir);
        if (wErr.Length > 0)
            l.Add(new CheckItem(L.T("Тека програми"), 2, L.T("NeoBar не може записувати у свою теку (") + wErr + L.T("). Налаштування, профілі й кеш не збережуться. Перенесіть NeoBar у теку користувача (напр. Документи) або в іншу теку з правом запису."))
                .Add(L.T("Відкрити теку"), delegate { OpenDir(appDir); }));
        else
            l.Add(new CheckItem(L.T("Тека програми"), 0, L.T("Запис дозволено - налаштування, профілі й кеш зберігаються поруч із програмою.")));

        // ---------- файли, що постачаються разом із NeoBar ----------
        {
            List<string> miss = new List<string>();
            List<string> what = new List<string>();
            if (!File.Exists(Path.Combine(appDir, "NeoBarFps64.dll")) || !File.Exists(Path.Combine(appDir, "NeoBarFps32.dll")))
            { miss.Add("NeoBarFps64.dll / NeoBarFps32.dll"); what.Add(L.T("оверлей у кадрі гри, обмеження кадрів, шар Vulkan")); }
            if (!File.Exists(Path.Combine(appDir, WallEngine.DllName)))
            { miss.Add(WallEngine.DllName); what.Add(L.T("шпалери NeoBar")); }
            if (!File.Exists(Path.Combine(appDir, @"d3d8to9\d3d8.dll"))) { miss.Add(@"d3d8to9\d3d8.dll"); what.Add(L.T("оверлей у старих іграх DirectX 8")); }
            if (!File.Exists(Path.Combine(appDir, @"cnc-ddraw\ddraw.dll"))) { miss.Add(@"cnc-ddraw\ddraw.dll"); what.Add(L.T("старі 2D-ігри DirectDraw у вікні без рамки")); }
            if (miss.Count > 0)
                l.Add(new CheckItem(L.T("Файли програми"), 2, L.T("Бракує: ") + string.Join(", ", miss.ToArray()) + L.T(". Не працюватиме: ") + string.Join("; ", what.ToArray()) + L.T(". Розпакуйте архів повністю (з усіма теками).")));
            else
                l.Add(new CheckItem(L.T("Файли програми"), 0, L.T("Усі на місці: оверлей і обмеження кадрів, рушій шпалер, обгортки для старих ігор.")));
        }

        // ---------- автозапуск: запис має вказувати на цю копію програми (після перенесення теки - на стару) ----------
        if (cfg != null && !cfg.AlwaysAdmin)
        {
            string run = AutostartPath();
            if (run.Length > 0)
            {
                bool same = true;
                try { same = string.Equals(Path.GetFullPath(run), Path.GetFullPath(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase); } catch { }
                if (same) l.Add(new CheckItem(L.T("Автозапуск"), 0, L.T("NeoBar запускається разом з Windows.")));
                else l.Add(new CheckItem(L.T("Автозапуск"), 1, L.T("Автозапуск вказує на іншу копію програми: ") + run + L.T(". Схоже, теку NeoBar перенесли."))
                    .Add(L.T("Виправити"), delegate { NeoBar.SetAutostart(true); if (refresh != null) refresh(); }));
            }
        }

        // ---------- PawnIO: драйвер і модуль для процесора ----------
        int ph = CpuTempPawn.Phase;
        if (ph == 3)
            l.Add(new CheckItem(L.T("Температура процесора"), 3, L.T("Процесор не AMD і не Intel - температуру через PawnIO не прочитати.")));
        else
        {
            if (ph == 1)
                l.Add(new CheckItem(L.T("Драйвер PawnIO"), 2, L.T("Не встановлено - немає температури й потужності процесора в моніторі та оверлеї. PawnIO - безкоштовний підписаний драйвер (ним користуються LibreHardwareMonitor і FanControl). Перезапускати панель після встановлення не треба."))
                    .Add(L.T("Відкрити pawnio.eu"), delegate { Open("https://pawnio.eu"); })
                    .Add(L.T("Завантажити встановлювач"), delegate { Open("https://github.com/namazso/PawnIO.Setup/releases/latest"); }));
            else l.Add(new CheckItem(L.T("Драйвер PawnIO"), 0, L.T("Встановлено.")));

            string mod = CpuTempPawn.ModuleName;
            string modDir = Path.GetDirectoryName(CpuTempPawn.ModulePath);
            if (ph == 2 || ph == 1)
            {
                CheckItem mi = new CheckItem(L.T("Модуль PawnIO для процесора"), ph == 1 ? 3 : 1,
                    (ph == 1 ? L.T("Після драйвера потрібен ще модуль для вашого процесора - ") : L.T("Бракує модуля для вашого процесора - ")) + mod +
                    L.T(" (у новіших випусках назва може трохи відрізнятися). Його можна завантажити автоматично або покласти вручну в теку pawnio поруч з програмою."));
                if (ph == 2) mi.Add(L.T("Завантажити модуль"), delegate { DownloadModule(bar, ui, refresh); });
                mi.Add(L.T("Сторінка модулів"), delegate { Open("https://github.com/namazso/PawnIO.Modules/releases"); });
                mi.Add(L.T("Відкрити теку pawnio"), delegate { try { Directory.CreateDirectory(modDir); } catch { } OpenDir(modDir); });
                l.Add(mi);
            }
            else
            {
                string st = CpuTempPawn.Status;
                l.Add(new CheckItem(L.T("Модуль PawnIO для процесора"), admin ? 0 : 1, L.T("Знайдено: ") + mod + "." + (admin ? "" : L.T(" Читати температуру можна лише з правами адміністратора.")) +
                    (st.Length > 0 && admin ? " " + st : "")));
            }
        }

        // ---------- датчики відеокарти від драйвера виробника ----------
        l.Add(GpuSensors());

        // ---------- ігри: FPS і шар Vulkan (без прав адміністратора - див. пункт про права) ----------
        if (admin)
            l.Add(new CheckItem(L.T("FPS в іграх"), Fps.Running ? 0 : 3, Fps.Running ? L.T("Трасування кадрів: ") + Fps.Status + "." : L.T("Вмикається само, коли гра зі списку на передньому плані.")));
        if (Games.VkLayer)
        {
            string vn = VkLayer.Note;
            bool vbad = vn.StartsWith(L.T("Помилка: ")) || vn == L.T("Не вдалося записати шар у реєстр.");
            if (vn.Length > 0) l.Add(new CheckItem(L.T("Шар Vulkan (оверлей у Vulkan-іграх)"), vbad ? 1 : 0, vn));
        }

        // ---------- гарячі клавіші: комбінацію могла зайняти інша програма ----------
        {
            List<string> hk = new List<string>();
            if (bar != null && bar.Config.Hotkeys && !bar.SearchKeyOk) hk.Add(L.T("пошук"));
            if (OverlayModule.HotkeyError.Length > 0) hk.Add(L.T("оверлей (") + OverlayModule.HotkeyError.Replace(L.T("Зайняті іншою програмою: "), "") + ")");
            if (hk.Count > 0)
                l.Add(new CheckItem(L.T("Гарячі клавіші"), 1, L.T("Не вдалося зареєструвати: ") + string.Join("; ", hk.ToArray()) + L.T(". Комбінацію вже зайняла інша програма - виберіть іншу в налаштуваннях (Пошук / Оверлей).")));
        }

        // ---------- місце: служба розташування Windows ----------
        {
            bool allow = cfg == null || cfg.LocAllow;
            bool auto = cfg == null || cfg.LocMode == "auto";
            string why = LocationOff();
            if (!allow)
                l.Add(new CheckItem(L.T("Служба розташування Windows"), 3, L.T("Визначення розташування вимкнено (Налаштування → Місце й погода): місто задається вручну, автоматичний часовий пояс недоступний.")));
            else if (why.Length == 0 && auto && Geo.Source == "ip")
                l.Add(new CheckItem(L.T("Служба розташування Windows"), 1,
                    L.T("Служба увімкнена, але місця не дала - місто визначено за IP-адресою, тобто це місто провайдера (в Україні часто Київ), а не ваше. Панель ще спробує пізніше; можна вибрати місто вручну (Налаштування → Місце й погода)."))
                    .Add(L.T("Параметри розташування"), delegate { Open("ms-settings:privacy-location"); }));
            else if (why.Length == 0)
                l.Add(new CheckItem(L.T("Служба розташування Windows"), 0, L.T("Увімкнена - місто для погоди, свят і часового поясу визначається точно.")));
            else
                l.Add(new CheckItem(L.T("Служба розташування Windows"), auto ? 1 : 3,
                    why + " " + (auto ? L.T("Місто й часовий пояс визначаються лише за IP-адресою - менш точно (за IP видно місто провайдера, в Україні часто Київ, а не ваше). ") : L.T("Місто задано вручну, тому це не заважає. ")) +
                    L.T("Увімкнути: Параметри Windows → Конфіденційність і безпека → Розташування («Служби визначення розташування» та «Дозволити класичним застосункам доступ до вашого розташування»)."))
                    .Add(L.T("Параметри розташування"), delegate { Open("ms-settings:privacy-location"); })
                    .Add(L.T("Служби Windows"), delegate { Run("services.msc"); }));
        }

        // ---------- пошук файлів на вкладці «Комп'ютер» ----------
        if (admin)
            l.Add(new CheckItem(L.T("Пошук файлів"), 0, L.T("Швидкий пошук за індексом NTFS (усі файли й теки).")));
        else if (ServiceDisabled("WSearch"))
            l.Add(new CheckItem(L.T("Пошук файлів"), 1, L.T("Без прав адміністратора пошук іде через службу «Пошук Windows», а її вимкнено - вкладка «Комп'ютер» нічого не знайде. Увімкніть службу (WSearch, тип запуску «Автоматично») або запускайте NeoBar від адміністратора."))
                .Add(L.T("Служби Windows"), delegate { Run("services.msc"); }));
        else
            l.Add(new CheckItem(L.T("Пошук файлів"), 3, L.T("Без прав адміністратора - через службу «Пошук Windows»: лише проіндексовані теки. З правами адміністратора - усі файли за індексом NTFS.")));

        // ---------- шпалери ----------
        if (WallCfg.Mode >= 1 && File.Exists(Path.Combine(appDir, WallEngine.DllName)))
        {
            string st = WallMgr.Status.Replace("\n", " · ");
            l.Add(new CheckItem(L.T("Шпалери"), WallEngine.Failed ? 2 : 0, st.Length > 0 ? st : L.T("Працюють.")));
            // живі шпалери (відео) декодує Media Foundation; у випусках Windows N її немає
            if (WallCfg.Mode == 3 && !File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mfplat.dll")))
                l.Add(new CheckItem(L.T("Відео для живих шпалер"), 2, L.T("У Windows немає Media Foundation (буває у випусках «N») - відео не відтворюватиметься, лише GIF. Встановіть «Пакет функцій мультимедіа» (Media Feature Pack)."))
                    .Add(L.T("Додаткові компоненти"), delegate { Open("ms-settings:optionalfeatures"); }));
        }

        // ---------- власні шрифти тем (нотатки) - лише коли їх уже підключено (вікно привітання з'являється раніше) ----------
        if (SBFonts.Ready)
        {
            int seen = 0;
            foreach (string n in SBFonts.Names) if (SBFonts.GdiVisible(n)) seen++;
            if (seen < SBFonts.Names.Length)
                l.Add(new CheckItem(L.T("Шрифти тем"), 1, L.T("Windows підхопила ") + seen + L.T(" з ") + SBFonts.Names.Length + L.T(" власних шрифтів NeoBar - у нотатках частина тем покаже стандартний шрифт. Подробиці - у журналі «Віджети й датчики».")));
        }

        if (full)
        {
            // ---------- WMI: характеристики ПК і стан дисків ----------
            if (wmiOs == null)
            {
                try
                {
                    List<Dictionary<string, object>> q = Wmi.Query(@"root\cimv2", "SELECT Caption FROM Win32_OperatingSystem", "Caption");
                    if (q.Count > 0) wmiOs = Wmi.S(q[0], "Caption");
                }
                catch { }
            }
            if (!string.IsNullOrEmpty(wmiOs)) l.Add(new CheckItem(L.T("Служба WMI"), 0, L.T("Працює (") + wmiOs + L.T(") - характеристики ПК і стан дисків доступні.")));
            else l.Add(new CheckItem(L.T("Служба WMI"), 2, L.T("Недоступна - не буде характеристик ПК і стану дисків. Перевірте службу «Інструментарій керування Windows» (winmgmt): тип запуску «Автоматично», стан «Виконується»."))
                .Add(L.T("Служби Windows"), delegate { Run("services.msc"); }));

            // ---------- інтернет і точний час (результат - із кешу; застарів - перевіряємо у фоні) ----------
            if (!NetSources.Fresh && !NetSources.Busy) NetSources.CheckAll(refresh);
            l.Add(InternetItem(refresh));
            CheckItem ti = ClockItem(admin, running ? restart : null, refresh);
            if (ti != null) l.Add(ti);
        }

        // ---------- журнали ----------
        {
            List<string> on = new List<string>();
            for (int g = 0; g < SbLog.GroupTitles.Length; g++) if (SbLog.GroupOn(g)) on.Add(L.Q("«") + SbLog.GroupTitles[g] + L.Q("»"));
            if (on.Count > 0)
                l.Add(new CheckItem(L.T("Журнали пошуку проблем"), 3, L.T("Увімкнено ") + string.Join(", ", on.ToArray()) + L.T(" - пишуться на диск (до 2 МБ кожен). Коли проблему знайдено, їх варто вимкнути."))
                    .Add(L.T("Вимкнути всі"), delegate { for (int g = 0; g < SbLog.GroupTitles.Length; g++) SbLog.SetGroup(g, false); if (refresh != null) refresh(); }));
        }
        string log = Path.Combine(appDir, "neobar-error.log");
        long logLen = 0;
        try { if (File.Exists(log)) logLen = new FileInfo(log).Length; } catch { }
        if (logLen > 0)
            l.Add(new CheckItem(L.T("Журнал помилок"), 1, L.T("Програма записала помилки (") + (logLen / 1024.0).ToString("0.#") + L.T(" КБ). Якщо щось працює не так - відкрийте журнал і надішліть його разом з описом проблеми."))
                .Add(L.T("Відкрити журнал"), delegate { Run("notepad.exe", "\"" + log + "\""); })
                .Add(L.T("Очистити"), delegate { try { File.Delete(log); } catch { } if (refresh != null) refresh(); }));

        // від важливого до менш важливого: проблеми, увага, до відома, гаразд (усередині - як було)
        List<CheckItem> sorted = new List<CheckItem>();
        foreach (int st in new[] { 2, 1, 3, 0 })
            foreach (CheckItem it in l) if (it.State == st) sorted.Add(it);
        return sorted;
    }

    // датчики відеокарти: бібліотека драйвера виробника
    static CheckItem GpuSensors()
    {
        string gpu = "";
        try { List<GpuAdapter> al = Gpu.Adapters(); if (al.Count > 0) gpu = al[0].Name; } catch { }
        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string low = gpu.ToLowerInvariant();
        if (low.Contains("nvidia") || low.Contains("geforce") || low.Contains("rtx") || low.Contains("gtx"))
        {
            bool nv = VendorGpu.NvmlPath() != null;
            if (nv) return new CheckItem(L.T("Датчики відеокарти"), 0, gpu + L.T(": бібліотека драйвера NVIDIA (NVML) є - температура, частоти, вентилятор."));
            return new CheckItem(L.T("Датчики відеокарти"), 1, gpu + L.T(": не знайдено бібліотеку драйвера NVIDIA (nvml.dll) - лише завантаження й пам'ять. Встановіть або оновіть драйвер NVIDIA."))
                .Add(L.T("Драйвери NVIDIA"), delegate { Open("https://www.nvidia.com/Download/index.aspx"); });
        }
        if (low.Contains("amd") || low.Contains("radeon"))
        {
            bool adl = File.Exists(Path.Combine(sys, IntPtr.Size == 8 ? "atiadlxx.dll" : "atiadlxy.dll"));
            if (adl) return new CheckItem(L.T("Датчики відеокарти"), 0, gpu + L.T(": бібліотека драйвера AMD (ADL) є - температура, hotspot, частоти, вентилятор."));
            return new CheckItem(L.T("Датчики відеокарти"), 1, gpu + L.T(": не знайдено бібліотеку драйвера AMD (atiadlxx.dll) - лише завантаження й пам'ять. Встановіть або оновіть AMD Software: Adrenalin Edition."))
                .Add(L.T("Драйвери AMD"), delegate { Open("https://www.amd.com/en/support/download/drivers.html"); });
        }
        if (low.Contains("intel") || low.Contains("arc") || low.Contains("iris") || low.Contains("uhd"))
        {
            bool igcl = File.Exists(Path.Combine(sys, IntPtr.Size == 8 ? "ControlLib.dll" : "ControlLib32.dll"));
            if (igcl) return new CheckItem(L.T("Датчики відеокарти"), 0, gpu + L.T(": бібліотека драйвера Intel (IGCL) є - частоти, потужність, температура (на дискретних Arc; вбудоване ядро температуру зазвичай не віддає).") +
                (VendorGpu.IntelStatus.Length > 0 ? L.T(" Стан: ") + VendorGpu.IntelStatus + "." : ""));
            return new CheckItem(L.T("Датчики відеокарти"), 1, gpu + L.T(": не знайдено бібліотеку драйвера Intel (ControlLib.dll) - лише завантаження й пам'ять. Оновіть графічний драйвер Intel (2022 року або новіший)."))
                .Add(L.T("Драйвери Intel"), delegate { Open("https://www.intel.com/content/www/us/en/support/detect.html"); });
        }
        return new CheckItem(L.T("Датчики відеокарти"), 3, (gpu.Length > 0 ? gpu + ". " : "") + L.T("Датчики виробника підтримуються для AMD, NVIDIA та Intel; завантаження й пам'ять беруться з Windows."));
    }

    // інтернет-сервіси (погода, свята, карта, місце) - підсумок перевірки NetSources
    static CheckItem InternetItem(Action refresh)
    {
        if (NetSources.Busy || NetSources.LastAt == DateTime.MinValue)
            return new CheckItem(L.T("Інтернет-сервіси"), 3, L.T("Перевіряю доступність погоди, свят, карти, місця й сервера часу…"));
        List<string> bad = new List<string>();
        string firstState = "";
        int used = 0;
        foreach (NetSource s in NetSources.All)
        {
            if (s.Ntp || !s.IsUsed) continue;
            used++;
            if (s.Ok == false) { bad.Add(s.Title + " (" + s.Name + "): " + s.State); if (firstState.Length == 0) firstState = s.State; }
        }
        if (bad.Count == 0)
            return new CheckItem(L.T("Інтернет-сервіси"), 0, L.T("Усі ") + used + L.T(" доступні (перевірено о ") + NetSources.LastAt.ToString("HH:mm") + L.T("). Подробиці - Налаштування → Місце й погода → «Джерела даних»."));
        bool none = bad.Count == used;
        CheckItem it = new CheckItem(L.T("Інтернет-сервіси"), none ? 2 : 1,
            (none ? L.T("Жоден сервіс не відповідає (") + firstState + L.T(") - схоже, немає інтернету або його блокує брандмауер чи проксі.")
                  : L.T("Не відповідають: ") + string.Join("; ", bad.ToArray()) + ".") +
            L.T(" Погода, свята й карта показуються зі збереженого в кеші."));
        it.Add(L.T("Перевірити ще раз"), delegate { if (NetSources.CheckAll(refresh) && refresh != null) refresh(); });
        it.Add(L.T("Мережа й Інтернет"), delegate { Open("ms-settings:network-status"); });
        return it;
    }

    // точність годинника за сервером часу
    static CheckItem ClockItem(bool admin, Action restart, Action refresh)
    {
        NetSource t = NetSources.Time;
        if (t == null || t.Ok == null) return null;
        if (t.Ok == false) return new CheckItem(L.T("Точний час"), 1, L.T("Сервери часу не відповідають (") + t.Name + L.T(") - точність годинника не перевірити, синхронізація не спрацює. Часто це закритий порт UDP 123."));
        double a = Math.Abs(t.Offset);
        string txt = NetSources.ClockText(t.Offset) + ".";
        if (a < 2) return new CheckItem(L.T("Точний час"), 0, txt.Substring(0, 1).ToUpperInvariant() + txt.Substring(1));
        CheckItem it = new CheckItem(L.T("Точний час"), a < 30 ? 1 : 2, txt.Substring(0, 1).ToUpperInvariant() + txt.Substring(1) +
            (admin ? L.T(" Синхронізуйте час.") : L.T(" Синхронізація потребує прав адміністратора.")));
        if (admin)
            it.Add(L.T("Синхронізувати"), delegate
            {
                Util.Bg(delegate { string msg; TimeSync.Run(out msg); NetSources.LastAt = DateTime.MinValue; if (refresh != null) refresh(); });
            });
        else if (restart != null) it.Add(L.T("Перезапустити від адміністратора"), restart);
        it.Add(L.T("Дата й час Windows"), delegate { Open("ms-settings:dateandtime"); });
        return it;
    }

    // порожньо - запис дозволено; інакше коротка причина
    static string WriteTest(string dir)
    {
        try
        {
            string f = Path.Combine(dir, ".neobar-write-test");
            File.WriteAllText(f, "1");
            File.Delete(f);
            return "";
        }
        catch (UnauthorizedAccessException) { return L.T("немає дозволу - напр. теку в Program Files без прав адміністратора"); }
        catch (Exception ex) { return ex.Message; }
    }

    // шлях з автозапуску (HKCU\...\Run "NeoBar"); порожньо - автозапуску немає
    static string AutostartPath()
    {
        try
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                string v = k != null ? k.GetValue("NeoBar") as string : null;
                return string.IsNullOrEmpty(v) ? "" : v.Trim().Trim('"');
            }
        }
        catch { return ""; }
    }

    // служба вимкнена (тип запуску «Вимкнено»)
    static bool ServiceDisabled(string name)
    {
        try
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name))
                return k != null && k.GetValue("Start") is int && (int)k.GetValue("Start") == 4;
        }
        catch { return false; }
    }

    static void Run(string exe) { Run(exe, ""); }
    static void Run(string exe, string args) { try { using (Process.Start(exe, args)) { } } catch { } }
    static void OpenDir(string dir) { try { using (Process.Start("explorer.exe", "\"" + dir + "\"")) { } } catch { } }

    // порожньо - усе гаразд; інакше пояснення, чому розташування Windows недоступне
    public static string LocationOff()
    {
        const string cm = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location";
        try
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\lfsvc"))
                if (k != null && k.GetValue("Start") is int && (int)k.GetValue("Start") == 4) return L.T("Службу визначення розташування (lfsvc) вимкнено.");
        }
        catch { }
        try
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors"))
                if (k != null && k.GetValue("DisableLocation") is int && (int)k.GetValue("DisableLocation") == 1) return L.T("Розташування вимкнено політикою системи.");
        }
        catch { }
        try
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(cm))
                if (k != null && string.Equals(k.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase)) return L.T("Доступ до розташування вимкнено для всього комп'ютера.");
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(cm))
                if (k != null && string.Equals(k.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase)) return L.T("Служби розташування вимкнено в параметрах конфіденційності.");
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(cm + @"\NonPackaged"))
                if (k != null && string.Equals(k.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase)) return L.T("Класичним застосункам (до яких належить NeoBar) заборонено доступ до розташування.");
        }
        catch { }
        return "";
    }

    static void Open(string url) { try { using (Process.Start(url)) { } } catch { } }

    public static void DownloadModule(NeoBar bar, Control ui, Action refresh)
    {
        CpuTempPawn.DownloadAsync(delegate (string err)
        {
            try
            {
                ui.BeginInvoke(new MethodInvoker(delegate
                {
                    string mod = CpuTempPawn.ModuleName;
                    if (err == null)
                        SbDialog.Show(bar, ui.FindForm(), L.T("Модуль завантажено"), L.T("Модуль ") + mod + L.T(" на місці. Температура процесора з'явиться за кілька секунд (потрібні права адміністратора)."), L.T("Гаразд"));
                    else
                    {
                        string dir = Path.GetDirectoryName(CpuTempPawn.ModulePath);
                        int r = SbDialog.Show(bar, ui.FindForm(), L.T("Не вдалося завантажити модуль"),
                            L.T("Автоматично не вийшло (") + err + L.T(").\n\nВручну:\n1. Відкрийте сторінку релізів PawnIO.Modules на GitHub.\n2. Візьміть з останнього релізу файл ") + mod +
                            L.T(" (назва може трохи відрізнятися).\n3. Покладіть його в папку:\n") + dir + L.T("\n\nNeoBar підхопить файл сам, перезапуск не потрібен."),
                            L.T("Відкрити сторінку й папку"), L.T("Закрити"));
                        if (r == 0)
                        {
                            try { Directory.CreateDirectory(dir); } catch { }
                            Open("https://github.com/namazso/PawnIO.Modules/releases");
                            try { using (Process.Start("explorer.exe", "\"" + dir + "\"")) { } } catch { }
                        }
                    }
                    if (refresh != null) refresh();
                }));
            }
            catch { }
        });
    }
}

// список перевірок: кольорова позначка, назва, пояснення і кнопка дії
sealed class DiagList : Control
{
    List<CheckItem> items = new List<CheckItem>();
    readonly List<KeyValuePair<Rectangle, Action>> hits = new List<KeyValuePair<Rectangle, Action>>();
    int scroll, total, hot = -1;
    public string Empty = L.T("Перевіряю…");

    public DiagList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void SetItems(List<CheckItem> l) { items = l ?? new List<CheckItem>(); scroll = 0; Invalidate(); }

    static Color Info { get { return FormTheme.Dark ? Color.FromArgb(110, 160, 235) : Color.FromArgb(50, 110, 200); } }

    static Color StateColor(int s)
    {
        // «до відома» - спокійний синій (акцент теми буває червоним і виглядав би як проблема)
        return s == 0 ? FormTheme.Good : (s == 1 ? FormTheme.Warn : (s == 2 ? FormTheme.Bad : Info));
    }

    static string StateText(int s) { return s == 0 ? L.T("гаразд") : (s == 1 ? L.T("увага") : (s == 2 ? L.T("проблема") : L.T("до відома"))); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (SolidBrush b = new SolidBrush(BackColor.A == 255 && BackColor != SystemColors.Control ? BackColor : FormTheme.Card)) g.FillRectangle(b, ClientRectangle);
        hits.Clear();
        if (items.Count == 0)
        {
            TextRenderer.DrawText(g, Empty, Font, ClientRectangle, FormTheme.Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        using (Font fb = new Font(Font, FontStyle.Bold))
        using (Font fs = new Font(Font.FontFamily, Math.Max(7f, Font.Size - 0.5f)))
        {
            int pad = 12, y = pad - scroll, w = Width - pad * 2 - 18;
            int idx = 0;
            foreach (CheckItem it in items)
            {
                Size ds = TextRenderer.MeasureText(it.Detail, fs, new Size(w, 0), TextFormatFlags.WordBreak);
                // кнопки дій - рядком під поясненням (переносяться, якщо не вміщаються)
                List<Rectangle> brs = new List<Rectangle>();
                int bx = 0, by = 0, bh = 24;
                foreach (KeyValuePair<string, Action> kv in it.Acts)
                {
                    int bw = TextRenderer.MeasureText(kv.Key, fs).Width + 22;
                    if (bx > 0 && bx + bw > w) { bx = 0; by += bh + 6; }
                    brs.Add(new Rectangle(bx, by, bw, bh));
                    bx += bw + 8;
                }
                int actH = it.Acts.Count > 0 ? by + bh + 8 : 0;
                int h = 22 + ds.Height + 12 + actH;
                Rectangle row = new Rectangle(pad, y, w + 18, h);
                if (row.Bottom > 0 && row.Top < Height)
                {
                    Color sc = StateColor(it.State);
                    using (SolidBrush b = new SolidBrush(sc)) g.FillEllipse(b, row.X, row.Y + 5, 11, 11);
                    TextRenderer.DrawText(g, it.Title, fb, new Rectangle(row.X + 18, row.Y, w - 80, 22), FormTheme.Fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(g, StateText(it.State), fs, new Rectangle(row.X + 18 + Math.Min(w - 80, TextRenderer.MeasureText(it.Title, fb).Width) + 8, row.Y, 80, 22), sc, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                    TextRenderer.DrawText(g, it.Detail, fs, new Rectangle(row.X + 18, row.Y + 22, w, ds.Height + 4), FormTheme.Sub, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
                }
                for (int k = 0; k < brs.Count; k++)
                {
                    Rectangle br = new Rectangle(row.X + 18 + brs[k].X, row.Y + 22 + ds.Height + 8 + brs[k].Y, brs[k].Width, brs[k].Height);
                    int hid = hits.Count;
                    if (br.Bottom > 0 && br.Top < Height)
                    {
                        bool hv = hot == hid;
                        using (SolidBrush b = new SolidBrush(hv ? FormTheme.Acc : Util.Blend(FormTheme.Card, FormTheme.Acc, k == 0 ? 0.34f : 0.18f))) Util.FillRound(g, b, br, 6);
                        TextRenderer.DrawText(g, it.Acts[k].Key, fs, br, hv ? Notes.Contrast(FormTheme.Acc) : FormTheme.Fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    }
                    hits.Add(new KeyValuePair<Rectangle, Action>(br, it.Acts[k].Value));
                }
                y += h;
                idx++;
                if (idx < items.Count) using (Pen p = new Pen(FormTheme.Line)) g.DrawLine(p, pad, y - 6, Width - pad, y - 6);
            }
            total = y + scroll + pad;
        }
        bar.View = Height; bar.Content = total; bar.Pos = scroll;
        bar.Paint(g, ClientRectangle);
    }

    readonly ThinBar bar = new ThinBar();

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        bar.MouseDown(this, e, ClientRectangle, ScrollTo);
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); bar.MouseUp(this); }
    void ScrollTo(int v) { scroll = v; }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (bar.MouseMove(this, e, ClientRectangle, ScrollTo)) return;
        int h = -1;
        for (int i = 0; i < hits.Count; i++) if (hits[i].Key.Contains(e.Location)) h = i;
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        if (h != hot) { hot = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hot != -1) { hot = -1; Invalidate(); } }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        foreach (KeyValuePair<Rectangle, Action> kv in hits)
            if (kv.Key.Contains(e.Location) && kv.Value != null) { try { kv.Value(); } catch { } return; }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        scroll = Math.Max(0, Math.Min(Math.Max(0, total - Height), scroll - Math.Sign(e.Delta) * 60));
        HandledMouseEventArgs he = e as HandledMouseEventArgs;
        if (he != null) he.Handled = true;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); try { Focus(); } catch { } }
}

// повідомлення в стилі панелі (замість системного MessageBox); повертає номер натиснутої кнопки
sealed class NoOwner : IWin32Window { public IntPtr Handle { get { return IntPtr.Zero; } } }

sealed class SbDialog : SbWindow
{
    readonly string text;
    readonly string[] buttons;
    int result = -1;

    SbDialog(NeoBar bar, string title, string text, string[] buttons) : base(bar, title, 480, 200)
    {
        this.text = text;
        this.buttons = buttons;
        Glyph = "\uE946";
        Fit();
    }

    // без панелі (окреме вікно з контекстного меню файлів): тема й масштаб явно
    SbDialog(Theme t, float sc, string title, string text, string[] buttons) : base(t, sc, title, 480, 200)
    {
        this.text = text;
        this.buttons = buttons;
        Glyph = "\uE946";
        Fit();
    }

    void Fit()
    {
        // кнопки не вміщаються в рядок - вікно ширшає
        int bws = 0;
        foreach (string bt in buttons) bws += ButtonW(bt) + Px(8);
        if (bws + Px(20) > ClientSize.Width - Px(28)) ClientSize = new Size(bws + Px(48), ClientSize.Height);
        int w = ClientSize.Width - Px(28);
        int th = TextRenderer.MeasureText(text, F, new Size(w, 0), TextFormatFlags.WordBreak).Height;
        ClientSize = new Size(ClientSize.Width, HeadH + Px(10) + th + Px(20) + Px(36) + Px(18));
        ShowInTaskbar = false;
        TopMost = true;         // запрошення (святкова тема тощо) - поверх усіх вікон
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        TxtW(g, text, F, new Rectangle(r.X, r.Y, r.Width, r.Height - Px(46)), P.Fg);
        int x = r.Right;
        for (int i = buttons.Length - 1; i >= 0; i--)
        {
            int bw = ButtonW(buttons[i]);
            x -= bw;
            int k = i;
            Btn(g, new Rectangle(x, r.Bottom - Px(36), bw, Px(36)), buttons[i], delegate { result = k; Close(); }, i == 0 ? 1 : 0, true);
            x -= Px(8);
        }
    }

    int ButtonW(string t) { return Math.Max(Px(110), TextRenderer.MeasureText(t, F).Width + Px(30)); }

    public static int Show(NeoBar bar, Form owner, string title, string text, params string[] buttons)
    {
        if (bar == null) return -1;
        if (buttons == null || buttons.Length == 0) buttons = new[] { L.T("Гаразд") };
        using (SbDialog d = new SbDialog(bar, title, text, buttons)) return Run(d, owner);
    }

    public static int Show(Theme t, float sc, Form owner, string title, string text, params string[] buttons)
    {
        if (buttons == null || buttons.Length == 0) buttons = new[] { L.T("Гаразд") };
        using (SbDialog d = new SbDialog(t, sc, title, text, buttons)) return Run(d, owner);
    }

    static int Run(SbDialog d, Form owner)
    {
        {
            Rectangle wa = owner != null ? Screen.FromControl(owner).WorkingArea : Screen.PrimaryScreen.WorkingArea;
            Rectangle ob = owner != null && owner.Visible ? owner.Bounds : wa;
            d.Location = new Point(Math.Max(wa.Left, ob.Left + (ob.Width - d.Width) / 2), Math.Max(wa.Top, ob.Top + (ob.Height - d.Height) / 2));
            if (owner != null && owner.Visible) d.ShowDialog(owner);
            else d.ShowDialog(new NoOwner());      // без власника: інакше ним міг стати вікно ефекту, і запрошення зникало б разом із ним
            return d.result;
        }
    }
}

// перше знайомство: один раз перед першим запуском панелі
sealed class WelcomeWindow : SbWindow
{
    readonly DiagList list;
    readonly System.Windows.Forms.Timer live = new System.Windows.Forms.Timer();
    string sig = "";

    // Start - натиснули «Почати»; Restart - перезапуск від адміністратора; інакше (хрестик, Esc) - вихід з програми
    public bool Start, Restart;
    public bool DontShow = true;     // «Більше не показувати» - наступного запуску вікна не буде

    // показується лише під час запуску, до появи панелі (хрестик закриває програму)
    public WelcomeWindow(NeoBar bar) : base(bar, L.T("Ласкаво просимо до NeoBar"), 660, 720)
    {
        ShowInTaskbar = true;
        FormTheme.Init(P);
        list = new DiagList();
        list.Font = new Font("Segoe UI", 9f);
        Controls.Add(list);
        PlaceList();
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
        // стан оновлюється сам: встановили PawnIO чи поклали модуль - список змінюється, поки вікно відкрите
        live.Interval = 2500;
        live.Tick += delegate { Refill(); };
        live.Start();
    }

    static readonly string Intro = L.T("Панель уже готова до роботи. Кілька речей, які варто знати наперед: частину можливостей (температура процесора, FPS в іграх, датчики відеокарти) Windows сама не віддає - для них потрібні права адміністратора, драйвер чи бібліотека. Нижче - що вже є на цьому комп'ютері і що можна доставити.");
    int introH;

    // вступ займає стільки рядків, скільки треба шрифту теми (рукописний ширший) - список стає одразу під ним
    void PlaceList()
    {
        int pad = Px(14), top = HeadH + Px(10), w = ClientSize.Width - pad * 2;
        introH = TextRenderer.MeasureText(Intro, F, new Size(w, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + Px(4);
        int ly = top + introH + Px(8);
        list.Bounds = new Rectangle(pad, ly, w, Math.Max(Px(120), ClientSize.Height - ly - pad - Px(112)));
    }

    protected override void ThemeUpdated() { PlaceList(); }

    // ті самі перевірки, що й у діагностиці (повні - у фоні, бо WMI може відповідати кілька секунд)
    volatile bool refBusy;

    void Refill()
    {
        if (refBusy) return;
        refBusy = true;
        Util.Bg(delegate
        {
            List<CheckItem> l = null;
            try { l = SysCheck.Collect(Bar, this, true, delegate { try { BeginInvoke(new Action(Refill)); } catch { } }); } catch { }
            try { BeginInvoke(new Action(delegate { refBusy = false; if (!IsDisposed && l != null) Apply(l); })); } catch { refBusy = false; }
        });
    }

    void Apply(List<CheckItem> l)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        foreach (CheckItem it in l) sb.Append(it.Sig).Append('\n');
        string ns = sb.ToString();
        if (ns == sig) return;
        sig = ns;
        list.SetItems(l);
    }

    protected override void OnShown(EventArgs e) { base.OnShown(e); Activate(); Refill(); }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        live.Stop();
        live.Dispose();
        base.OnFormClosed(e);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        TxtW(g, Intro, F, new Rectangle(r.X, r.Y, r.Width, introH), P.Fg);
        Rectangle lr = list.Bounds;
        int sy = lr.Bottom + Px(10);
        Switch(g, new Rectangle(r.X, sy, r.Width, Px(28)), L.T("Більше не показувати це вікно під час запуску"), DontShow, delegate { DontShow = !DontShow; });
        TxtW(g, L.T("Повернути це вікно й перевірити все ще раз: Налаштування → Діагностика. Хрестик закриває програму."),
            FS, new Rectangle(r.X, sy + Px(30), r.Width, Px(20)), P.Sub);
        Btn(g, new Rectangle(r.Right - Px(180), r.Bottom - Px(40), Px(180), Px(40)), L.T("Почати"), delegate { Start = true; Close(); }, 1, true);
        if (!Elevated.IsAdmin)
            Btn(g, new Rectangle(r.Right - Px(180) - Px(10) - Px(300), r.Bottom - Px(40), Px(300), Px(40)), L.T("Перезапустити від адміністратора"), delegate
            {
                // Windows спитає дозвіл; якщо скасували - лишаємось у цьому вікні
                if (Elevated.RestartAsAdmin()) { Restart = true; Close(); }
            }, 0, true);
    }
}
