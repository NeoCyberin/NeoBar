using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

// ======================================================================
//  NeoBar - вікна віджетів (ЛКМ) і їхні налаштування в контекстному меню (ПКМ)
// ======================================================================
sealed partial class NeoBar
{
    readonly Dictionary<string, SbWindow> wins = new Dictionary<string, SbWindow>();
    DateTime lastLocate = DateTime.MinValue;
    bool locating;
    int locRetry;
    System.Windows.Forms.Timer locTimer;

    // одноразово після старту панелі: визначити розташування (як кнопка "Визначити зараз"),
    // а якщо служба Windows ще не готова й вийшов лише IP - ще кілька спроб із паузами
    void LocateAtStart()
    {
        if (cfg.LocMode != "auto" || !cfg.LocAllow) return;
        locRetry = 0;
        ScheduleLocate(4000);
    }

    void ScheduleLocate(int ms)
    {
        if (locTimer == null)
        {
            locTimer = new System.Windows.Forms.Timer();
            locTimer.Tick += delegate
            {
                locTimer.Stop();
                if (locating) { locTimer.Interval = 5000; locTimer.Start(); return; }
                LocateAuto(true);
            };
        }
        locTimer.Stop();
        locTimer.Interval = ms;
        locTimer.Start();
    }

    Entry FirstWidget(string kind)
    {
        foreach (Entry e in entries) if (e.Kind == Kind.Widget && e.Target == kind) return e;
        return null;
    }

    // Вікно віджета: повторний клік по віджету закриває його
    SbWindow OpenWin(string key, Entry anchor, Func<SbWindow> make, bool toggle)
    {
        SbWindow w;
        if (wins.TryGetValue(key, out w) && !w.IsDisposed)
        {
            bool justLeft = (DateTime.Now - w.LeftAt).TotalMilliseconds < 450;
            if (toggle && (Form.ActiveForm == w || justLeft)) { w.Close(); return null; }
            if (w.WindowState == FormWindowState.Minimized) w.WindowState = FormWindowState.Normal;
            w.Activate();
            return w;
        }
        // розкрите вікно лише одне: інші розкриті віджети закриваємо
        foreach (KeyValuePair<string, SbWindow> kv in new List<KeyValuePair<string, SbWindow>>(wins))
            if (kv.Key != key && kv.Value != null && !kv.Value.IsDisposed) kv.Value.Close();
        SbWindow nw = make();
        nw.AutoClose = true;                 // клік повз вікно (стільниця, інша програма) закриває його
        wins[key] = nw;
        nw.FormClosed += delegate { SbWindow cur; if (wins.TryGetValue(key, out cur) && cur == nw) wins.Remove(key); };
        Rectangle tile = anchor != null && entries.Contains(anchor) ? RectangleToScreen(RectOf(anchor)) : RectangleToScreen(ClientRectangle);
        nw.ShowNear(tile, cfg.Side);
        return nw;
    }

    public void OpenClock(int tab)
    {
        SbWindow w = OpenWin("clock", FirstWidget("clock"), delegate { return new ClockWindow(this, tab); }, false);
        ClockWindow cw = w as ClockWindow;
        if (cw != null) cw.SelectTab(tab);
        if (Widgets.WTz.Length == 0 && Widgets.WeatherDue()) RefreshWeather();
    }

    public void OpenWeather() { OpenWin("weather", FirstWidget("weather"), delegate { return new WeatherWindow(this); }, false); }
    public void OpenBattery() { OpenWin("battery", FirstWidget("battery"), delegate { return new BatteryWindow(this); }, false); }

    public void OpenDisk(Entry e)
    {
        SbWindow old;
        if (wins.TryGetValue("disk", out old) && !old.IsDisposed) old.Close();
        OpenWin("disk", e, delegate { return new DiskWindow(this, e); }, false);
    }

    public void OpenPerf(int tab)
    {
        SbWindow old;
        if (wins.TryGetValue("perf", out old) && !old.IsDisposed && old is PerfWindow) { ((PerfWindow)old).SetTab(tab); old.Activate(); return; }
        OpenWin("perf", FirstWidget("perf"), delegate { return new PerfWindow(this, tab); }, false);
    }

    void ToggleWin(string key, Action open)
    {
        SbWindow w;
        if (wins.TryGetValue(key, out w) && !w.IsDisposed)
        {
            bool justLeft = (DateTime.Now - w.LeftAt).TotalMilliseconds < 450;
            if (Form.ActiveForm == w || justLeft) { w.Close(); return; }
        }
        open();
    }

    // годинник і календар - одне вікно: клік по іншому віджету перемикає вкладку, по тому самому - закриває
    void ClockClick(int tab)
    {
        SbWindow w;
        if (wins.TryGetValue("clock", out w) && !w.IsDisposed)
        {
            ClockWindow cw = w as ClockWindow;
            bool justLeft = (DateTime.Now - w.LeftAt).TotalMilliseconds < 450;
            if ((Form.ActiveForm == w || justLeft) && cw != null && cw.CurrentTab == tab) { w.Close(); return; }
        }
        OpenClock(tab);
    }

    void WidgetClick(Entry en)
    {
        switch (en.Target)
        {
            case "notes": ToggleWin("notes", OpenNotes); return;
            case "clock": ClockClick(0); return;
            case "weather": ToggleWin("weather", OpenWeather); return;
            case "battery": if (Battery.Present()) ToggleWin("battery", OpenBattery); return;
            case "disk": ToggleWin("disk", delegate { OpenDisk(en); }); return;
            case "perf": ToggleWin("perf", delegate { OpenPerf(0); }); return;
            case "net": ToggleWin("net", delegate { OpenNet(en); }); return;
            case "color": ToggleWin("color", delegate { OpenColor(en); }); return;
            case "dice": ToggleWin("dice", delegate { OpenWin("dice", en, delegate { return new DiceWindow(this); }, false); }); return;
            case "qr": ToggleWin("qr", delegate { OpenWin("qr", en, delegate { return new QrWindow(this); }, false); }); return;
            case "clean": ToggleWin("clean", delegate { OpenWin("clean", en, delegate { return new CleanWindow(this); }, false); }); return;
            case "recover": ToggleWin("recover", delegate { OpenWin("recover", en, delegate { return new RecoverPickWindow(this); }, false); }); return;
            case "security": ToggleWin("security", delegate { OpenWin("security", en, delegate { return new SecWindow(this); }, false); }); return;
            case "crypt": ToggleWin("crypt", delegate { OpenWin("crypt", en, delegate { return new CryptWindow(this); }, false); }); return;
        }
    }

    public void OpenSecurity() { OpenWin("security", FirstWidget("security"), delegate { return new SecWindow(this); }, false); }

    public void OpenClean() { OpenWin("clean", FirstWidget("clean"), delegate { return new CleanWindow(this); }, false); }

    // аналіз чи очищення завершено - оновити розмір сміття на віджеті
    public void CleanRedraw()
    {
        try
        {
            ClockData.RedrawKind("trash");
            if (IsDisposed || !IsHandleCreated) return;
            foreach (Entry x in entries)
                if (x.Kind == Kind.Widget && x.Target == "clean") { x.Sig = ""; Invalidate(RectOf(x)); }
        }
        catch { }
    }

    // змінився стан, який показує віджет (наприклад, завершилося сканування безпеки) - перемалювати його на панелі
    public void WidgetRedraw(string kind)
    {
        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            foreach (Entry x in entries)
                if (x.Kind == Kind.Widget && x.Target == kind) { x.Sig = ""; Invalidate(RectOf(x)); }
        }
        catch { }
    }

    // кубик кинули чи змінили - перемалювати віджети кубика на панелі
    void DiceRedraw()
    {
        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            foreach (Entry x in entries)
                if (x.Kind == Kind.Widget && x.Target == "dice") { x.Sig = ""; Invalidate(RectOf(x)); }
        }
        catch { }
    }

    public void OpenColor(Entry e) { OpenWin("color", e, delegate { return new ColorWindow(this, e); }, false); }

    // палітра: колір змінився (save - зберегти вибір у neobar.ini)
    public void ColorChanged(bool save)
    {
        foreach (Entry x in entries)
            if (x.Kind == Kind.Widget && x.Target == "color") { x.Sig = ""; Invalidate(RectOf(x)); }
        if (!save) return;
        Entry e = FirstWidget("color");
        string o = ColorState.Opt;
        if (e != null && e.Opt != o) { e.Opt = o; SaveCfg(); }
    }

    public void OpenNet() { OpenNet(FirstWidget("net")); }

    // відкрити «Мережу» одразу на вкладці (3 - брандмауер)
    public void OpenNet(int tab)
    {
        NetWindow w = OpenWin("net", FirstWidget("net"), delegate { return new NetWindow(this, FirstWidget("net")); }, false) as NetWindow;
        if (w != null) w.SelectTab(tab);
    }
    public void OpenNet(Entry e) { OpenWin("net", e, delegate { return new NetWindow(this, e); }, false); }

    // сервер і інтервал пінгу зберігаються в рядку віджета: "host;секунди"
    public void SetNetOpt(Entry e, string opt)
    {
        if (e == null || !entries.Contains(e)) e = FirstWidget("net");
        if (e != null) e.Opt = opt;
        PingMon.Configure(opt, e != null || ClockData.DeskHas("net"));
        SaveCfg();
    }

    // монітор системи: що показувати у віджеті на панелі (на столі у кожного віджета свій вибір) (хоч один рядок лишається)
    public void SetPerfShow(string what, bool on)
    {
        bool c = cfg.PerfCpu, g = cfg.PerfGpu, r = cfg.PerfRam;
        if (what == "cpu") c = on; else if (what == "gpu") g = on; else r = on;
        if (!c && !g && !r) return;
        cfg.PerfCpu = c; cfg.PerfGpu = g; cfg.PerfRam = r;
        Widgets.PerfSet(cfg);
        SaveCfg();
        RefreshDataNeeds();
    }

    // тема погоди: європейська (°C) або американська (°F) - для панелі й вікна погоди
    public void SetUnits(bool us)
    {
        cfg.Fahrenheit = us;
        SaveCfg();
        ClockData.SetWeatherTheme(us ? "us" : "");     // віджети погоди на столі - за тим самим вибором
    }

    // поточний параметр віджета на панелі (вікно налаштувань не має його перезаписувати старим)
    public string WidgetOpt(string kind)
    {
        Entry e = FirstWidget(kind);
        return e != null ? e.Opt : null;
    }

    // перше знайомство показано - більше не показувати
    public void MarkWelcomed()
    {
        if (cfg.Welcomed) return;
        cfg.Welcomed = true;
        SaveCfg();
    }

    public void OpenNotes() { OpenWin("notes", FirstWidget("notes"), delegate { return new NotesWindow(this); }, false); }

    // «Завжди від адміністратора»: створення/видалення завдання (один запит UAC), далі перезапуск з правами
    System.Windows.Forms.Timer adminPoll;

    public void ConfigureAdmin(bool on, bool autostart)
    {
        string err;
        string user = Environment.UserDomainName + "\\" + Environment.UserName;
        string file = Elevated.Run("task", (on ? "on" : "off") + " " + (autostart ? "auto" : "noauto") + " \"" + user + "\"", out err);
        if (file == null)
        {
            ToastForm.Show(this, L.T("Права адміністратора"), err.Length > 0 ? err : L.T("Не вдалося змінити налаштування."), "gear", false, null);
            return;
        }
        if (adminPoll != null) adminPoll.Dispose();
        adminPoll = new System.Windows.Forms.Timer();
        adminPoll.Interval = 500;
        int ticks = 0;
        adminPoll.Tick += delegate
        {
            bool done;
            string o = Elevated.ReadOut(file, out done);
            if (!done && ++ticks < 120) return;
            adminPoll.Stop();
            string msg = o.Trim();
            bool ok = msg.StartsWith(L.T("Готово: NeoBar запускатиметься з правами адміністратора без запиту.")) || msg.StartsWith(L.T("Завдання видалено.")) || msg.StartsWith(L.T("Завдання не знайдено."));
            if (!ok) ToastForm.Show(this, L.T("Права адміністратора"), msg.Length > 0 ? msg : L.T("Немає відповіді."), "gear", false, null);   // успіх без оповіщення
            if (on && ok && !Elevated.IsAdmin && AdminTask.Launch()) Quit();
        };
        adminPoll.Start();
    }

    public void Quit() { Close(); }

    public void ForceWeather()
    {
        Widgets.WUpdated = DateTime.MinValue;
        RefreshWeather();
    }

    void SaveCfg()
    {
        try { Store.Save(cfgPath, cfg, entries); } catch { }
        foreach (Entry e in entries) e.Sig = "";
        Invalidate();
    }


    void RebuildDiskIds()
    {
        Widgets.DiskIds.Clear();
        foreach (Entry e in entries) if (e.Kind == Kind.Widget && e.Target == "disk") foreach (string k in Widgets.DiskKeys(e)) Widgets.DiskIds.Add(k);
    }

    // ---------- меню віджетів ----------

    public void OpenDisk() { OpenDisk(FirstWidget("disk")); }

    public bool HasWidget(string kind) { return FirstWidget(kind) != null; }

    // скільки дисків показує віджет на панелі (останній прибрати не можна)
    public int PanelDiskCount()
    {
        Entry e = FirstWidget("disk");
        return e == null ? 0 : Widgets.DiskKeys(e).Count;
    }

    static bool DiskSelected(List<string> keys, Vol v)
    {
        foreach (string k in keys)
            if (string.Equals(v.Guid, k, StringComparison.OrdinalIgnoreCase) || string.Equals(v.Root, k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // додає або прибирає диск із віджета (хоча б один лишається)
    public void ToggleDisk(Entry e, Vol v)
    {
        if (e == null || !entries.Contains(e)) e = FirstWidget("disk");
        if (e == null) return;
        string id = v.Guid.Length > 0 ? v.Guid : v.Root;
        List<string> keys = new List<string>();
        foreach (string k in (e.Opt ?? "").Split(',')) if (k.Trim().Length > 0) keys.Add(k.Trim());
        if (keys.Count == 0) keys.Add(Vols.SysRoot());
        int at = -1;
        for (int i = 0; i < keys.Count; i++)
            if (string.Equals(keys[i], v.Guid, StringComparison.OrdinalIgnoreCase) || string.Equals(keys[i], v.Root, StringComparison.OrdinalIgnoreCase)) at = i;
        if (at >= 0) { if (keys.Count > 1) keys.RemoveAt(at); }
        else keys.Add(id);
        // системний диск зберігаємо за GUID, як і решту
        for (int i = 0; i < keys.Count; i++)
            if (keys[i].Length <= 3) { string g = Vols.GuidOf(keys[i]); if (g.Length > 0) keys[i] = g; }
        e.Opt = string.Join(",", keys.ToArray());
        RebuildDiskIds();
        Widgets.ReadDisks(true);
        DoLayout();
        SaveCfg();
    }

    public bool DiskShown(Entry e, Vol v)
    {
        if (e == null || !entries.Contains(e)) e = FirstWidget("disk");
        return e != null && DiskSelected(Widgets.DiskKeys(e), v);
    }

    // підключили/від'єднали диск
    bool disksHooked;
    public static event Action DisksChanged;
    System.Windows.Forms.Timer devTimer;

    public void OnDisksChanged()
    {
        if (devTimer == null)
        {
            devTimer = new System.Windows.Forms.Timer();
            devTimer.Interval = 1500;
            devTimer.Tick += delegate
            {
                devTimer.Stop();
                Widgets.ReadDisks(true);
                foreach (Entry x in entries) if (x.Kind == Kind.Widget && x.Target == "disk") x.Sig = "";
                Invalidate();
                Action a = DisksChanged;
                if (a != null) try { a(); } catch { }
            };
        }
        devTimer.Stop();
        devTimer.Start();
    }

    // ---------- місце розташування ----------

    void LocateAuto(bool force)
    {
        if (cfg.LocMode != "auto" || !cfg.LocAllow || locating) return;
        if (!force && (DateTime.Now - lastLocate).TotalHours < 6) return;
        lastLocate = DateTime.Now;
        locating = true;
        Util.Bg(delegate
        {
            double lat = double.NaN, lon = double.NaN;
            string city = "", cc = "", tz = "";
            // служба розташування Windows вимкнена - одразу за IP (і місце, і часовий пояс)
            bool winOn = SysCheck.LocationOff().Length == 0;
            bool ok = winOn && Geo.WindowsLocation(out lat, out lon);
            string src = ok ? "windows" : "ip";
            if (!ok) ok = Geo.IpLocation(out lat, out lon, out city, out cc, out tz);
            if (ok)
            {
                string nm, c2;
                if (Geo.Reverse(lat, lon, out nm, out c2)) { if (nm.Length > 0) city = nm; if (c2.Length > 0) cc = c2; }
            }
            try
            {
                BeginInvoke(new Action(delegate
                {
                    locating = false;
                    if (cfg.LocMode != "auto") return;
                    // Windows-розташування увімкнене, але не відповіло (служба ще стартує) - повтор
                    int[] pause = { 45000, 180000, 600000 };
                    if (!(ok && src == "windows") && winOn && locRetry < pause.Length) ScheduleLocate(pause[locRetry++]);
                    SbLog.W(SbLog.Panel, "розташування: " + (ok ? src : "не вдалося") + (ok ? ", " + city : ""));
                    if (!ok) return;
                    Geo.Source = src;
                    bool moved = double.IsNaN(cfg.Lat) || Math.Abs(cfg.Lat - lat) > 0.02 || Math.Abs(cfg.Lon - lon) > 0.02;
                    cfg.Lat = Math.Round(lat, 4); cfg.Lon = Math.Round(lon, 4);
                    if (city.Length > 0) { cfg.City = city; cfg.CityName = city; }
                    if (cc.Length == 2) cfg.Country = cc.ToUpperInvariant();
                    if (tz.Length > 0 && Widgets.WTz.Length == 0) Widgets.WTz = tz;
                    cityKey = cfg.City;
                    SaveCfg();
                    if (moved) { Holidays.Reset(); ForceWeather(); }
                }));
            }
            catch { locating = false; }
        });
    }

    // ---------- іконки сайтів ----------

    bool iconReloadQueued;

    void FaviconReady()
    {
        try
        {
            BeginInvoke(new Action(delegate
            {
                if (iconReloadQueued) return;
                iconReloadQueued = true;
                // кілька іконок приходять поспіль - оновлюємо разом
                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = 400;
                t.Tick += delegate
                {
                    t.Stop(); t.Dispose();
                    iconReloadQueued = false;
                    foreach (Entry e in entries)
                        if (e.Kind == Kind.Item && Util.IsUrl(e.Full) && (e.IconSpec ?? "").Trim().Length == 0)
                        {
                            Bitmap old = e.Icon;
                            e.Icon = LoadIcon(e, Px(cfg.IconSize));
                            if (old != null) old.Dispose();
                        }
                    BuildFolderIcons();
                    Invalidate();
                };
                t.Start();
            }));
        }
        catch { }
    }

    // ---------- запуск підсистем віджетів ----------

    bool widgetsInit, pingHooked;

    // ---------- гра на передньому плані: FPS і оверлей вмикаються самі ----------
    System.Windows.Forms.Timer gameTimer;
    DateTime lastGame = DateTime.MinValue, lastOvGame = DateTime.MinValue;

    Game lookGame;
    void GameWatch()
    {
        int pid; string name;
        bool game = Games.Foreground(out pid, out name);
        if (game)
        {
            if (pid != Games.CurrentPid) Fps.Log("Гра: " + name + " (" + Games.LastExe + ", pid " + pid + ")");
            Games.CurrentName = name;
            Games.CurrentPid = pid;
            Games.CurrentExe = Games.LastExe;
            Games.Current = Games.LastGame;
            Games.InForeground = true;
            lastGame = DateTime.Now;
            // датчики й підрахунок кадрів працюють лише для гри з увімкненим оверлеєм; для решти все на паузі
            bool wantOv = Games.Current != null && Games.Current.Ov;
            Sensors.Use("game", wantOv ? 2 : 0);
            if (wantOv) { lastOvGame = DateTime.Now; if (Elevated.IsAdmin && !Fps.Running) Fps.Start(); }
        }
        else
        {
            Games.InForeground = false;
            // гра закрилась - одразу прибрати оверлей; згорнута - через 10 с
            bool gone = Games.CurrentPid > 0 && !Games.Alive(Games.CurrentPid);
            if (Games.CurrentPid > 0 && (gone || (DateTime.Now - lastGame).TotalSeconds > 10))
            {
                Games.CurrentName = ""; Games.CurrentPid = 0; Games.CurrentExe = ""; Games.Current = null;
                Sensors.Use("game", 0);
            }
        }
        // підрахунок кадрів (потрібен лише оверлею) зупиняється, коли гри з оверлеєм давно немає
        if (Fps.Running && (DateTime.Now - lastOvGame).TotalSeconds > 30 && Sensors.Level < 2) Fps.Stop();
        // вигляд оверлею залежить від гри (загальний або власний)
        if (!object.ReferenceEquals(Games.Current, lookGame)) { lookGame = Games.Current; try { OverlayCfg.UseGame(lookGame); } catch { } }
        try { FpsLimiter.Tick(); } catch { }
        if (cfg.FsHide) CheckFullscreen();
        if (OverlayModule.Visible != OverlayModule.Want) OverlayModule.Tick();
    }

    void InitWidgetSystems()
    {
        if (widgetsInit) return;
        widgetsInit = true;
        try
        {
            Games.Load();
            gameTimer = new System.Windows.Forms.Timer();
            gameTimer.Interval = 1000;
            gameTimer.Tick += delegate { try { GameWatch(); } catch { } };
            gameTimer.Start();
        }
        catch { }
        try { ClockData.Init(this); } catch { }
        try { RefreshDataNeeds(); } catch { }
        try { OverlayModule.Init(this); } catch { }
    }

    void AfterReloadWidgets()
    {
        RebuildDiskIds();
        Widgets.PerfSet(cfg);
        bool panelPerf = false;
        foreach (Entry pe in entries) if (pe.Kind == Kind.Widget && pe.Target == "perf") panelPerf = true;
        // у грі/повноекранній програмі датчики панелі й столу та пінг на паузі (датчики гри - окремо, «game»)
        bool pz = suspended;
        Sensors.Use("panel", panelPerf && !pz ? (Widgets.PerfGpu ? 2 : 1) : 0);          // відеокарті потрібен повний збір даних
        Sensors.Use("desk", ClockData.DeskHas("perf") && !pz ? (ClockData.DeskPerfGpu() ? 2 : 1) : 0);
        Entry ne = FirstWidget("net");
        PingMon.Configure(ne != null ? ne.Opt : "", !pz && (ne != null || ClockData.DeskHas("net")));
        if (!pingHooked) { pingHooked = true; PingMon.Updated += delegate { foreach (Entry x in entries) if (x.Kind == Kind.Widget && x.Target == "net") { x.Sig = ""; Invalidate(RectOf(x)); } }; }
        if (!disksHooked)
        {
            disksHooked = true;
            Widgets.DisksUpdated += delegate
            {
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        foreach (Entry x in entries) if (x.Kind == Kind.Widget && x.Target == "disk") { x.Sig = ""; Invalidate(RectOf(x)); }
                    }));
                }
                catch { }
            };
        }
        if (wkinds.Contains("disk")) Widgets.ReadDisks(true);
        LocateAuto(false);
        ClockData.Restyle();
    }
}
