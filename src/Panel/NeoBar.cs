using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

// ======================================================================
//  NeoBar - головна панель (AppBar / автосховок): вікно, геометрія, таймери
// ======================================================================
sealed partial class NeoBar : Form
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    List<Entry> entries = new List<Entry>();
    readonly List<GroupSpan> spans = new List<GroupSpan>();
    Cfg cfg = new Cfg();
    Theme T;
    Font font, headFont;
    SolidBrush hoverBrush, edgeBrush;
    Pen sepPen;
    readonly string cfgPath;
    readonly float scale;
    readonly ToolTip tip = new ToolTip();
    readonly uint cbMsg, tbCreated, runMsg;
    public const string RunningMsg = "NeoBarAlreadyRunning";      // друга копія просить показати першу
    SettingsForm settings;
    FlyoutForm flyout;
    SearchForm search;

    // розкладка
    bool H;
    int capH, thick, cell, slot, itemLen, contentLen, scroll, topLen, searchLen, fixLen, tbBlock, centerOff, desiredLen, profS, profLen;

    // стан
    int hover = -1, tbHover = -1, menuOpen;
    bool srHover, registered, wantBlur, useWall, suspended, expanded = true, fsAbn, autohide, firstLoad = true;
    float slideT = 1f;
    int opPct = 100, dwell, leave, pollN;
    string cityKey = "";
    Color textCol;
    Bitmap backdrop;
    Rectangle backdropAt;
    readonly Dictionary<string, Bitmap> tbIcons = new Dictionary<string, Bitmap>();
    readonly HashSet<string> wkinds = new HashSet<string>();
    readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer settle = new System.Windows.Forms.Timer();
    int settleN;
    readonly System.Windows.Forms.Timer wtick = new System.Windows.Forms.Timer();


    public NeoBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FormBorderStyle = FormBorderStyle.None;
        AppIcon.Apply(this);
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DropFiles.Prepare(this);
        Text = "NeoBar";

        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;

        cfgPath = Prof.File("neobar.ini");
        cbMsg = Win.RegisterWindowMessage("NeoBarAppBarMsg");
        tbCreated = Win.RegisterWindowMessage("TaskbarCreated");
        runMsg = Win.RegisterWindowMessage(RunningMsg);

        anim.Interval = 16;
        anim.Tick += delegate { AnimTick(); };
        settle.Interval = 250;
        settle.Tick += delegate
        {
            // після зняття резервування місця Провідник змінює робочу область із затримкою - уточнюємо позицію
            PlaceWindow();
            if (++settleN >= 8) settle.Stop();
        };
        poll.Interval = 150;
        poll.Tick += delegate { PollTick(); };
        wtick.Interval = 1000;
        wtick.Tick += delegate { WidgetTick(); };

        EnsureConfig();
        Reload();
        Bounds = autohide ? PanelRect(Ease(slideT)) : FixedStart();

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnPrefChanged;
    }

    int Px(float v) { return (int)Math.Round(v * scale); }

    static float Ease(float t)
    {
        float u = 1f - Math.Max(0f, Math.Min(1f, t));
        return 1f - u * u * u;
    }

    // ---------- доступ для вікна налаштувань та спливних вікон ----------

    public string CfgPath { get { return cfgPath; } }
    public Color ThemeBg { get { return T.Glass ? Color.FromArgb(30, 32, 40) : T.Bg; } }
    public Color ThemeAccent { get { return T.Accent; } }
    public Theme CurrentTheme { get { return T; } }
    public Cfg Config { get { return cfg; } }
    public Font LabelFont { get { return font; } }
    public float Dpi { get { return scale; } }

    public Bitmap PreviewIcon(Entry e, int px)
    {
        Bitmap b = LoadIcon(e, Math.Max(px, 8));
        return b;
    }

    public List<Entry> Entries { get { return entries; } }
    public bool MenuIsOpen { get { return menuOpen > 0; } }

    // ---------- монітор і геометрія ----------

    Screen Mon()
    {
        if (cfg.Monitor.Length > 0)
            foreach (Screen s in Screen.AllScreens)
                if (s.DeviceName == cfg.Monitor) return s;
        return Screen.PrimaryScreen;
    }

    uint Edge()
    {
        switch (cfg.Side)
        {
            case "left": return 0;
            case "top": return 1;
            case "bottom": return 3;
            default: return 2;
        }
    }

    Rectangle FixedStart()
    {
        Rectangle sb = Mon().Bounds;
        switch (cfg.Side)
        {
            case "left": return new Rectangle(sb.Left, sb.Top, thick, sb.Height);
            case "top": return new Rectangle(sb.Left, sb.Top, sb.Width, thick);
            case "bottom": return new Rectangle(sb.Left, sb.Bottom - thick, sb.Width, thick);
            default: return new Rectangle(sb.Right - thick, sb.Top, thick, sb.Height);
        }
    }

    // Прямокутник панелі в режимі автосховку (t = 1 - висунута, 0 - лише смужка).
    // Висунута панель займає всю довжину краю екрана; схована смужка (і зона виклику)
    // стоїть лише посередині, з відступом CornerGap від кутів, щоб кути не реагували.
    Rectangle PanelRect(float t)
    {
        Screen s = Mon();
        Rectangle wa = s.WorkingArea;
        int gap = Px(cfg.CornerGap);
        int strip = Math.Max(2, Px(3));
        int fullLen = H ? wa.Width : wa.Height;
        int midLen = Math.Max(Px(160), fullLen - 2 * gap);
        if (H && cfg.Compact)
        {
            fullLen = midLen = Math.Min(midLen, Math.Max(Px(260), desiredLen));
        }
        int len = (int)Math.Round(midLen + (fullLen - midLen) * t);
        int x, y, w, h;
        if (!H)
        {
            h = len;
            y = wa.Top + (wa.Height - h) / 2;
            w = thick;
            bool right = cfg.Side != "left";
            int xe = right ? wa.Right - w : wa.Left;
            int xh = right ? wa.Right - strip : wa.Left - w + strip;
            x = (int)Math.Round(xh + (xe - xh) * t);
        }
        else
        {
            w = len;
            x = wa.Left + (wa.Width - w) / 2;
            h = thick;
            bool top = cfg.Side == "top";
            int ye = top ? wa.Top : wa.Bottom - h;
            int yh = top ? wa.Top - h + strip : wa.Bottom - strip;
            y = (int)Math.Round(yh + (ye - yh) * t);
        }
        return new Rectangle(x, y, w, h);
    }

    // смужка біля краю екрана, на яку наводять мишу, щоб висунути панель (без кутів)
    Rectangle HotRect()
    {
        Rectangle mid = PanelRect(0f);
        Rectangle wa = Mon().WorkingArea;
        int z = Math.Max(3, Px(3));
        if (!H)
            return cfg.Side == "left"
                ? new Rectangle(wa.Left, mid.Y, z, mid.Height)
                : new Rectangle(wa.Right - z, mid.Y, z, mid.Height);
        return cfg.Side == "top"
            ? new Rectangle(mid.X, wa.Top, mid.Width, z)
            : new Rectangle(mid.X, wa.Bottom - z, mid.Width, z);
    }

    // Компактна панель угорі/внизу: заокруглені внутрішні кути
    // компактна панель (заокруглена, за вмістом) можлива лише в автосховку вгорі/внизу
    bool Rounded { get { return H && cfg.Compact && autohide; } }

    int CornerRadius { get { return Px(16); } }

    string shapeKey;

    // Форма вікна: заокруглення компактної панелі та обрізання частини, що заходить на сусідній монітор
    // (схована панель на внутрішньому краї між двома екранами інакше видна на іншому моніторі смугою)
    void UpdateShape()
    {
        if (!IsHandleCreated) return;
        Rectangle cs = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
        Rectangle vis = cs;
        if (autohide)
        {
            Rectangle wb = Bounds;
            bool other = false;
            Screen mon = Mon();
            foreach (Screen s in Screen.AllScreens)
                if (s.DeviceName != mon.DeviceName && s.Bounds.IntersectsWith(wb)) other = true;
            if (other)
            {
                Rectangle iv = Rectangle.Intersect(wb, mon.Bounds);
                vis = iv.Width <= 0 || iv.Height <= 0 ? Rectangle.Empty : new Rectangle(iv.X - wb.X, iv.Y - wb.Y, iv.Width, iv.Height);
            }
        }
        bool round = Rounded && cs.Width > 40 && cs.Height > 20;
        string key = round + "|" + cs + "|" + vis + "|" + cfg.Side;
        if (key == shapeKey) return;
        shapeKey = key;
        Region nr = null;
        if (round)
        {
            int r = CornerRadius;
            bool top = cfg.Side == "top";
            using (GraphicsPath gp = Util.Round(cs, top ? 0 : r, top ? 0 : r, top ? r : 0, top ? r : 0))
                nr = new Region(gp);
            if (vis != cs) nr.Intersect(vis);
        }
        else if (vis != cs) nr = new Region(vis);
        Util.SetRegion(this, nr);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        UpdateShape();
    }

    void PlaceWindow()
    {
        if (!autohide || !IsHandleCreated) return;
        Rectangle r = PanelRect(Ease(slideT));
        if (Bounds != r) Bounds = r;
    }

    // ---------- вікно ----------

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x08000000; // TOOLWINDOW | NOACTIVATE
            return cp;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyMode(false);
        ApplyHotkeys();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Trim();
        AfterShown();
        Seasons.Start(this);
        Dice.Changed += DiceRedraw;
        // шпалери профілю - трохи згодом: під час автозапуску Провідник може ще не створити робочий стіл
        System.Windows.Forms.Timer wt = new System.Windows.Forms.Timer();
        wt.Interval = 1500;
        wt.Tick += delegate { wt.Stop(); wt.Dispose(); try { WallEngine.Owner = Handle; WallCfg.Load(); WallMgr.Apply(); } catch (Exception ex) { Util.ErrorLog(ex); } };
        wt.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnPrefChanged;
        Dice.Changed -= DiceRedraw;
        anim.Stop(); poll.Stop(); wtick.Stop();
        try { Win.UnregisterHotKey(Handle, 1); } catch { }
        try { if (searchHk != null) searchHk.Dispose(); } catch { }
        try { Notes.Shutdown(); } catch { }
        try { OverlayModule.Shutdown(); Fps.Stop(); } catch { }
        try { WallMgr.Shutdown(); } catch { }
        if (backdrop != null) backdrop.Dispose();
        RemoveBar();
        AppBarQ.Flush(1500);
        base.OnFormClosed(e);
    }

    void OnDisplayChanged(object sender, EventArgs e)
    {
        if (IsHandleCreated) BeginInvoke(new Action(delegate { DropBackdrop(); ApplyMode(true); }));
    }

    void OnPrefChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.Desktop && IsHandleCreated && useWall)
            BeginInvoke(new Action(DropBackdrop));
    }

    // ---------- AppBar ----------

    Win.APPBARDATA Abd()
    {
        Win.APPBARDATA d = new Win.APPBARDATA();
        d.cbSize = Marshal.SizeOf(typeof(Win.APPBARDATA));
        d.hWnd = Handle;
        return d;
    }

    // Усі звернення до Провідника (SHAppBarMessage) йдуть в окремому потоці: якщо Провідник зайнятий
    // і сам чекає на панель, вони не блокують одне одного (раніше так іноді зависали обидва)
    void RegisterBar()
    {
        if (registered || !IsHandleCreated) return;
        Win.APPBARDATA d = Abd();
        d.uCallbackMessage = cbMsg;
        registered = true;
        AppBarQ.Run(delegate { Win.SHAppBarMessage(0, ref d); }); // ABM_NEW
    }

    void RemoveBar()
    {
        if (!registered || !IsHandleCreated) { registered = false; return; }
        Win.APPBARDATA d = Abd();
        registered = false;
        AppBarQ.Run(delegate { Win.SHAppBarMessage(1, ref d); }); // ABM_REMOVE
    }

    bool posQueued, posAgain;
    DateTime posWin = DateTime.MinValue;
    int posCount;
    System.Windows.Forms.Timer posRetry;

    void SetPos()
    {
        if (!registered || !IsHandleCreated) return;
        if (posQueued) { posAgain = true; return; }
        // захист від «пінг-понгу» з Провідником або іншою панеллю: не частіше 20 разів за 3 секунди
        DateTime now = DateTime.Now;
        if ((now - posWin).TotalSeconds > 3) { posWin = now; posCount = 0; }
        if (++posCount > 20)
        {
            if (posRetry == null)
            {
                posRetry = new System.Windows.Forms.Timer();
                posRetry.Interval = 3000;
                posRetry.Tick += delegate { posRetry.Stop(); posWin = DateTime.MinValue; SetPos(); };
            }
            posRetry.Stop(); posRetry.Start();
            return;
        }
        posQueued = true;
        Rectangle mb = Mon().Bounds;
        Win.APPBARDATA d = Abd();
        d.uEdge = Edge();
        d.rc.left = mb.Left; d.rc.top = mb.Top; d.rc.right = mb.Right; d.rc.bottom = mb.Bottom;
        int th = thick;
        AppBarQ.Run(delegate
        {
            Win.SHAppBarMessage(2, ref d); // ABM_QUERYPOS
            switch (d.uEdge)
            {
                case 0: d.rc.right = d.rc.left + th; break;
                case 1: d.rc.bottom = d.rc.top + th; break;
                case 3: d.rc.top = d.rc.bottom - th; break;
                default: d.rc.left = d.rc.right - th; break;
            }
            Win.SHAppBarMessage(3, ref d); // ABM_SETPOS
            Rectangle wr = Rectangle.FromLTRB(d.rc.left, d.rc.top, d.rc.right, d.rc.bottom);
            try
            {
                BeginInvoke(new Action(delegate
                {
                    posQueued = false;
                    // закріплена панель завжди на всю довжину краю (компактна - лише в автосховку)
                    if (registered && !autohide && Bounds != wr) Bounds = wr;
                    if (posAgain) { posAgain = false; SetPos(); }
                }));
            }
            catch { posQueued = false; }
        });
    }

    // Фіксована панель резервує місце (вікна підлаштовуються), автосховок - накладається поверх
    void ApplyMode(bool keepState)
    {
        autohide = cfg.Mode == "autohide";
        if (autohide && !keepState) { expanded = false; slideT = 0f; }
        if (!autohide) { expanded = true; slideT = 1f; }
        if (!IsHandleCreated) return;
        if (autohide)
        {
            RemoveBar();
            PlaceWindow();
            settleN = 0;
            settle.Start();
        }
        else
        {
            RegisterBar();
            SetPos();
        }
        Win.SetWindowPos(Handle, (IntPtr)(-1), 0, 0, 0, 0, 0x0013); // HWND_TOPMOST, без активації
        UpdateShape();
        UpdatePoll();
        Invalidate();
    }

    void UpdatePoll()
    {
        if (autohide || cfg.FsHide) poll.Start(); else poll.Stop();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // панель від адміністратора має отримати повідомлення й від звичайної копії (інакше UIPI його відкине)
        try { if (runMsg != 0) Win.ChangeWindowMessageFilterEx(Handle, runMsg, 1, IntPtr.Zero); } catch { }
        DropFiles.Register(this);
    }

    protected override void WndProc(ref Message m)
    {
        string[] dropped = DropFiles.Take(ref m);
        if (dropped != null)
        {
            if (dropped.Length > 0) BeginInvoke(new Action(delegate { AddDropped(dropped, null); }));
            return;
        }
        if (runMsg != 0 && m.Msg == (int)runMsg)
        {
            BeginInvoke(new Action(delegate
            {
                try
                {
                    if (autohide) SetExpanded(true);
                    ShowTipText(L.T("NeoBar уже запущено - друга копія не відкривається"), TbRect(0));
                }
                catch { }
            }));
            return;
        }
        // WM_DEVICECHANGE: підключено/від'єднано диск чи флешку (DBT_DEVICEARRIVAL, DBT_DEVICEREMOVECOMPLETE)
        if (m.Msg == 0x219 && (m.WParam.ToInt32() == 0x8000 || m.WParam.ToInt32() == 0x8004 || m.WParam.ToInt32() == 0x7)) OnDisksChanged();
        // Провідник перезапустився: резервування місця зникло разом з ним - реєструємося знову
        if (tbCreated != 0 && m.Msg == (int)tbCreated)
        {
            registered = false;
            shapeKey = null;
            BeginInvoke(new Action(delegate { DropBackdrop(); ApplyMode(true); try { WallMgr.Restart(); } catch { } }));
        }
        if (m.Msg == (int)cbMsg)
        {
            int n = m.WParam.ToInt32();
            if (n == 1) // ABN_POSCHANGED
            {
                SetPos();
            }
            else if (n == 2) // ABN_FULLSCREENAPP
            {
                fsAbn = m.LParam != IntPtr.Zero;
                CheckFullscreen();
            }
            return;
        }
        if ((m.Msg == 0x1A || m.Msg == 0x7E) && autohide) // WM_SETTINGCHANGE / WM_DISPLAYCHANGE: робоча область змінилась
        {
            base.WndProc(ref m);
            PlaceWindow();
            return;
        }
        if (m.Msg == 0x21) // WM_MOUSEACTIVATE
        {
            m.Result = (IntPtr)3; // MA_NOACTIVATE
            return;
        }
        if (m.Msg == 0x312 && m.WParam.ToInt32() == 1) // WM_HOTKEY: пошук
        {
            if (autohide) SetExpanded(true);
            OpenSearch();
            return;
        }
        if (registered)
        {
            if (m.Msg == 0x06) { Win.APPBARDATA d = Abd(); d.lParam = m.WParam; AppBarQ.Run(delegate { Win.SHAppBarMessage(6, ref d); }); }
            else if (m.Msg == 0x47) { Win.APPBARDATA d2 = Abd(); AppBarQ.Run(delegate { Win.SHAppBarMessage(9, ref d2); }); }
        }
        base.WndProc(ref m);
    }

    // Гаряча клавіша пошуку - на окремому невидимому вікні: панель інколи перестворює своє вікно
    // (зміна стилю, монітора), і тоді реєстрація на ньому губилася - клавіша «переставала працювати»
    HotkeyWindow searchHk;
    public const int HkSearch = 0x5B10;
    public static bool Paused;   // гра або повноекранна програма на передньому плані
    public bool SearchKeyOk = true;
    public Keys SearchKeyNow { get { return cfg.Hotkeys ? (Keys)cfg.SearchKey : Keys.None; } }

    void ApplyHotkeys()
    {
        if (!IsHandleCreated) return;
        try
        {
            Win.UnregisterHotKey(Handle, 1);
            if (searchHk == null)
            {
                searchHk = new HotkeyWindow();
                searchHk.Pressed += delegate (int id)
                {
                    if (id != HkSearch || suspended) return;   // під час гри/повноекранного режиму пошук не викликається
                    if (autohide) SetExpanded(true);
                    OpenSearch();
                };
            }
            SearchKeyOk = searchHk.Register(HkSearch, cfg.Hotkeys ? (Keys)cfg.SearchKey : Keys.None);
        }
        catch { SearchKeyOk = false; }
    }

    // ---------- автосховок: висунути / сховати ----------

    public void SetExpanded(bool e)
    {
        if (!autohide) return;
        if (expanded == e && ((e && slideT >= 1f) || (!e && slideT <= 0f))) return;
        expanded = e;
        if (!e) tip.Hide(this);
        if (!cfg.Anim)
        {
            slideT = e ? 1f : 0f;
            PlaceWindow();
            Invalidate();
            return;
        }
        anim.Start();
    }

    bool HoldOpen
    {
        get { return menuOpen > 0 || flyout != null || search != null || dragOver || dragging; }
    }

    bool dragOver;

    void PollTick()
    {
        pollN++;
        if (autohide && !suspended)
        {
            Point cp = Cursor.Position;
            if (!expanded)
            {
                if (HotRect().Contains(cp)) { if (++dwell >= 1) { dwell = 0; SetExpanded(true); } }
                else dwell = 0;
            }
            else
            {
                Rectangle cur = Rectangle.Inflate(PanelRect(Ease(slideT)), Px(4), Px(4));
                if (cur.Contains(cp) || HoldOpen) leave = 0;
                else if (++leave >= 3) { leave = 0; SetExpanded(false); }
            }
        }
        if (pollN % 5 == 0 && (cfg.FsHide || suspended)) CheckFullscreen();
    }

    int fsHits;

    void CheckFullscreen()
    {
        // гра й повноекранні програми, що повідомили про себе (AppBar), ховають панель одразу;
        // решту (повноекранне вікно, стан «не турбувати») підтверджує друга перевірка поспіль - короткі накладки
        // на кшталт знімка екрана панель не ховають
        // опцію вимкнули, поки панель була схована - повернути її
        if (!cfg.FsHide) { fsHits = 0; SetSuspended(false); return; }
        bool sure = fsAbn || Games.InForeground;
        bool maybe = !sure && (Nat.FullscreenBusy() || Nat.ForegroundCovers(true, IsHandleCreated ? Handle : IntPtr.Zero));
        fsHits = maybe ? fsHits + 1 : 0;
        SetSuspended(sure || (suspended ? maybe : fsHits >= 2));
    }

    // віджети на робочому столі (годинник, нотатки тощо) на час гри зникають і не оновлюються
    readonly List<Form> pausedForms = new List<Form>();

    void PauseDesk(bool s)
    {
        try
        {
            if (s)
            {
                List<Form> all = new List<Form>();
                foreach (Form f0 in Application.OpenForms) all.Add(f0);
                foreach (Form f in all)
                    if ((f is DeskClock || f is NoteForm) && f.Visible && f.IsHandleCreated) { Win.ShowWindow(f.Handle, 0); pausedForms.Add(f); }
            }
            else
            {
                foreach (Form f in pausedForms)
                    if (!f.IsDisposed && f.IsHandleCreated) Win.ShowWindow(f.Handle, 4);
                pausedForms.Clear();
            }
        }
        catch { }
    }

    // Під час повноекранного режиму панель ховається, а таймери й анімації зупиняються
    void SetSuspended(bool s)
    {
        if (s == suspended || !IsHandleCreated) return;
        suspended = s;
        Paused = s;
        PauseDesk(s);
        AfterReloadWidgets();      // датчики й пінг на паузу / знову працювати
        if (s)
        {
            // гра чи повноекранна програма: працює лише ігровий оверлей, решта на паузі
            if (flyout != null) flyout.Close();
            if (search != null && !search.IsDisposed) search.Close();
            Win.ShowWindow(Handle, 0);
            wtick.Stop();
            anim.Stop();
        }
        else
        {
            Win.ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
            Win.SetWindowPos(Handle, (IntPtr)(-1), 0, 0, 0, 0, 0x0013);
            if (wkinds.Count > 0) wtick.Start();
            Invalidate();
        }
    }

    void AnimTick()
    {
        bool busy = false;

        // анімація висування
        if (autohide)
        {
            float target = expanded ? 1f : 0f;
            if (Math.Abs(slideT - target) > 0.001f)
            {
                float step = 0.14f;
                slideT = slideT < target ? Math.Min(target, slideT + step) : Math.Max(target, slideT - step);
                PlaceWindow();
                if (slideT <= 0.001f || slideT >= 0.999f) Invalidate();
                busy = true;
            }
        }

        // анімація наведення
        float spd = cfg.Anim ? 0.2f : 1f;
        for (int i = 0; i < entries.Count; i++)
        {
            Entry e = entries[i];
            if (e.Hidden) continue;
            float tg = i == hover ? 1f : 0f;
            if (Math.Abs(e.Hv - tg) > 0.001f)
            {
                e.Hv = e.Hv < tg ? Math.Min(tg, e.Hv + spd) : Math.Max(tg, e.Hv - spd);
                Invalidate(Rectangle.Inflate(RectOf(e), 1, 1));
                if (e.Hv != tg) busy = true;
            }
        }
        if (!busy) anim.Stop();
    }

    static void Trim()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        try { Win.SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1)); } catch { }
    }

    // види віджетів, чиї дані потрібні: на панелі і на робочому столі
    void RebuildKinds()
    {
        wkinds.Clear();
        foreach (Entry e in entries) if (e.Kind == Kind.Widget && !e.Off) wkinds.Add(e.Target);
        foreach (string k in new[] { "weather", "net", "perf" }) if (ClockData.DeskHas(k)) wkinds.Add(k);
    }

    // додали або прибрали погоду / мережу / монітор на столі
    public void RefreshDataNeeds()
    {
        RebuildKinds();
        AfterReloadWidgets();
        if (wkinds.Count > 0 && !suspended)
        {
            Widgets.Tick(wkinds);
            wtick.Start();
            if (wkinds.Contains("weather") && Widgets.WeatherDue()) RefreshWeather();
        }
        else wtick.Stop();
    }

    // ---------- віджети: таймер ----------

    void WidgetTick()
    {
        if (suspended || wkinds.Count == 0) return;
        Widgets.Tick(wkinds);
        bool visible = !autohide || slideT > 0.05f;
        if (wkinds.Contains("weather") && Widgets.WeatherDue()) RefreshWeather();
        LocateAuto(false);
        for (int i = 0; i < entries.Count; i++)
        {
            Entry e = entries[i];
            if (e.Kind != Kind.Widget || e.Off) continue;
            string sig = Widgets.Sig(e, cfg);
            if (sig == e.Sig) continue;
            e.Sig = sig;
            if (visible) Invalidate(RectOf(e));
        }
    }

    System.Windows.Forms.Timer wfTimer;
    public static event Action WeatherFadeTick;

    // поки йде анімація оновлення погоди, перемальовуємо всі її відображення
    public void StartFade() { StartWeatherFade(); }

    void StartWeatherFade()
    {
        if (wfTimer == null)
        {
            wfTimer = new System.Windows.Forms.Timer();
            wfTimer.Interval = 33;
            wfTimer.Tick += delegate
            {
                for (int i = 0; i < entries.Count; i++)
                    if (entries[i].Kind == Kind.Widget && entries[i].Target == "weather") Invalidate(RectOf(entries[i]));
                ClockData.RedrawKind("weather");
                bool net = PingMon.Fade.Active;
                if (net)
                {
                    for (int i = 0; i < entries.Count; i++)
                        if (entries[i].Kind == Kind.Widget && entries[i].Target == "net") Invalidate(RectOf(entries[i]));
                    ClockData.RedrawKind("net");
                }
                Action a = WeatherFadeTick;
                if (a != null) try { a(); } catch { }
                if (!Widgets.WeatherFadeActive && !net)
                {
                    wfTimer.Stop();
                    // останній кадр - повна яскравість
                    for (int i = 0; i < entries.Count; i++)
                        if (entries[i].Kind == Kind.Widget && (entries[i].Target == "net" || entries[i].Target == "weather")) Invalidate(RectOf(entries[i]));
                    ClockData.RedrawKind("net"); ClockData.RedrawKind("weather");
                }
            };
        }
        if (!wfTimer.Enabled) wfTimer.Start();
    }

    void RefreshWeather()
    {
        Widgets.WeatherRefresh(cfg, delegate
        {
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (Widgets.GeoChanged)
                    {
                        Widgets.GeoChanged = false;
                        try { Store.Save(cfgPath, cfg, entries); } catch { }
                    }
                    for (int i = 0; i < entries.Count; i++)
                        if (entries[i].Kind == Kind.Widget && entries[i].Target == "weather") Invalidate(RectOf(entries[i]));
                    ClockData.CheckTz();
                }));
            }
            catch { }
        });
        StartWeatherFade();
        Invalidate();
    }

    // ---------- конфіг ----------

    static Entry MkEntry(Kind k, string name, string target, string icon)
    {
        Entry e = new Entry();
        e.Kind = k; e.Name = name; e.Target = target; e.IconSpec = icon;
        return e;
    }

    public static List<Entry> DefaultEntries()
    {
        List<Entry> l = new List<Entry>();
        l.Add(MkEntry(Kind.Widget, L.T("Годинник"), "clock", ""));
        l.Add(MkEntry(Kind.Widget, L.T("Погода"), "weather", ""));
        l.Add(MkEntry(Kind.Separator, "", "", ""));
        l.Add(MkEntry(Kind.Header, L.T("Ігри"), "", ""));
        l.Add(MkEntry(Kind.Item, "Steam", "steam://open/main", "theme:game"));
        l.Add(MkEntry(Kind.Separator, "", "", ""));
        l.Add(MkEntry(Kind.Header, L.T("Застосунки"), "", ""));
        l.Add(MkEntry(Kind.Item, L.T("Калькулятор"), "calc.exe", "theme:calc"));
        l.Add(MkEntry(Kind.Item, L.T("Блокнот"), "notepad.exe", "theme:edit"));
        l.Add(MkEntry(Kind.Item, "Paint", "mspaint.exe", "theme:photo"));
        l.Add(MkEntry(Kind.Item, L.T("Диспетчер завдань"), "taskmgr.exe", "theme:taskmgr"));
        l.Add(MkEntry(Kind.Item, L.T("Параметри"), "ms-settings:", "theme:settings"));
        l.Add(MkEntry(Kind.Separator, "", "", ""));
        l.Add(MkEntry(Kind.Header, L.T("Система"), "", ""));
        l.Add(MkEntry(Kind.Item, L.T("Цей ПК"), "shell:MyComputerFolder", "theme:pc"));
        l.Add(MkEntry(Kind.Item, L.T("Кошик"), "shell:RecycleBinFolder", "theme:bin"));
        l.Add(MkEntry(Kind.Item, L.T("Особиста папка"), @"%USERPROFILE%", "theme:home"));
        l.Add(MkEntry(Kind.Separator, "", "", ""));
        l.Add(MkEntry(Kind.Header, L.T("Папки"), "", ""));
        l.Add(MkEntry(Kind.Item, L.T("Завантаження"), @"%USERPROFILE%\Downloads", ""));
        l.Add(MkEntry(Kind.Item, L.T("Документи"), @"%USERPROFILE%\Documents", ""));
        l.Add(MkEntry(Kind.Separator, "", "", ""));
        l.Add(MkEntry(Kind.Header, L.T("Сайти"), "", ""));
        l.Add(MkEntry(Kind.Item, "YouTube", "https://www.youtube.com", ""));
        l.Add(MkEntry(Kind.Item, "GitHub", "https://github.com", ""));
        return l;
    }

    void EnsureConfig()
    {
        try
        {
            if (File.Exists(cfgPath)) return;
            Store.Save(cfgPath, new Cfg(), DefaultEntries());
        }
        catch { }
    }

    void ApplyTheme()
    {
        T = Themes.Get(cfg.ThemeId, cfg);
        try { ProfileArt.SyncStyle(T); } catch { }      // значок профілю - у стилі теми
        OvTheme.Panel = T;
        Notes.SetPanelTheme(T);
        Chime.ThemeId = T.Id;
        if (font != null) font.Dispose();
        if (headFont != null) headFont.Dispose();
        if (hoverBrush != null) hoverBrush.Dispose();
        if (edgeBrush != null) edgeBrush.Dispose();
        if (sepPen != null) sepPen.Dispose();

        string fname = cfg.FontName.Length > 0 ? cfg.FontName : T.Font;
        FontStyle st = FontStyle.Regular;
        if (cfg.FontStyle == "bold" || cfg.FontStyle == "bolditalic") st |= FontStyle.Bold;
        if (cfg.FontStyle == "italic" || cfg.FontStyle == "bolditalic") st |= FontStyle.Italic;
        font = Util.MakeFont(fname, cfg.FontSize, st);
        headFont = Util.MakeFont(fname, Math.Max(6f, cfg.FontSize - 0.5f), st | FontStyle.Bold);
        hoverBrush = new SolidBrush(T.Hover);
        edgeBrush = new SolidBrush(T.Edge);
        sepPen = new Pen(T.Sep);

        // готові теми незмінні; прозорість і скло налаштовуються лише у «Власній»
        bool custom = cfg.ThemeId == "custom";
        opPct = Math.Max(0, Math.Min(100, custom ? (cfg.Opacity >= 0 ? cfg.Opacity : 100) : T.Opacity));
        wantBlur = custom ? cfg.Blur == 1 : T.Glass;
        useWall = wantBlur || opPct < 100;
        textCol = T.Text.A < 255
            ? Util.Blend(Util.Opaque(T.Bg), Util.Opaque(T.Text), T.Text.A / 255f)
            : T.Text;
        // на дуже темному/світлому тлі підписи не повинні зливатися
        textCol = Util.Readable(textCol, Util.Opaque(T.Bg), 70);
        if (backdrop != null) { backdrop.Dispose(); backdrop = null; }
        BackColor = T.Bg;
    }

    public void Reload()
    {
        SbLog.W(SbLog.Panel, "перезавантаження панелі (" + cfgPath + ")");
        if (flyout != null) flyout.Close();
        foreach (Entry e in entries) if (e.Icon != null) e.Icon.Dispose();
        cfg = new Cfg();
        entries = new List<Entry>();
        Store.Load(cfgPath, cfg, entries);
        if (firstLoad && Seasons.Expire(cfg)) try { Store.Save(cfgPath, cfg, entries); } catch { }   // святкова тема відслужила
        ApplyTheme();
        themeDirty = true;

        slot = (int)Math.Round(Px(cfg.IconSize) * T.IconScale);
        foreach (Entry e in entries)
            if (e.Kind == Kind.Item) e.Icon = LoadIcon(e, Px(cfg.IconSize));

        RebuildKinds();

        DoLayout();
        BuildFolderIcons();
        MakeToolbarIcons();
        hover = -1;
        tbHover = -1;
        scroll = 0;
        if (IsHandleCreated) tip.Hide(this);

        ApplyMode(!firstLoad);
        firstLoad = false;
        ApplyHotkeys();
        UpdatePoll();

        // віджети
        string ck = cfg.City;
        if (ck != cityKey)
        {
            cityKey = ck;
            Widgets.Temp = double.NaN;
            Widgets.WCode = -1;
            Widgets.WUpdated = DateTime.MinValue;
        }
        foreach (Entry e in entries) e.Sig = "";
        AfterReloadWidgets();
        if (wkinds.Count > 0 && !suspended)
        {
            Widgets.Tick(wkinds);
            wtick.Start();
            if (wkinds.Contains("weather") && Widgets.WeatherDue()) RefreshWeather();
        }
        else wtick.Stop();

        Invalidate();
        if (IsHandleCreated) Trim();
        // відкриті вікна віджетів переодягаються в нову тему
        if (themeDirty)
        {
            themeDirty = false;
            Action a = ThemeChanged;
            if (a != null) try { a(); } catch { }
        }
    }

    bool themeDirty;
    public static event Action ThemeChanged;

    void AfterShown()
    {
        CheckFullscreen();
        InitWidgetSystems();
        LocateAtStart();
        try
        {
            Notes.Changed += delegate { foreach (Entry e in entries) e.Sig = ""; Invalidate(); };
            Notes.Start(Path.GetDirectoryName(Path.GetFullPath(cfgPath)), scale);
        }
        catch { }
    }

    // ---------- автозапуск ----------

    public static bool IsAutostart()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue("NeoBar") != null;
        }
        catch { return false; }
    }

    public static void SetAutostart(bool on)
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (k == null) return;
                if (on) k.SetValue("NeoBar", "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue("NeoBar", false);
            }
        }
        catch { }
    }
}

// Черга викликів SHAppBarMessage в окремому фоновому потоці
static class AppBarQ
{
    static readonly Queue<Action> q = new Queue<Action>();
    static System.Threading.Thread th;

    public static void Run(Action a)
    {
        lock (q)
        {
            q.Enqueue(a);
            if (th == null)
            {
                th = new System.Threading.Thread(Loop);
                th.IsBackground = true;
                th.Name = "AppBar";
                th.Start();
            }
            System.Threading.Monitor.Pulse(q);
        }
    }

    static void Loop()
    {
        while (true)
        {
            Action a;
            lock (q)
            {
                while (q.Count == 0) System.Threading.Monitor.Wait(q);
                a = q.Dequeue();
            }
            try { a(); } catch { }
        }
    }

    // дочекатися, поки черга виконається (не довше ms)
    public static bool Flush(int ms)
    {
        if (th == null) return true;
        System.Threading.ManualResetEvent ev = new System.Threading.ManualResetEvent(false);
        Run(delegate { ev.Set(); });
        return ev.WaitOne(ms);
    }
}
