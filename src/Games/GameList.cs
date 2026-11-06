using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// ======================================================================
//  Список ігор у налаштуваннях оверлею: власний «select box».
//  Рядок - назва гри й короткі позначки. Клік по рядку розгортає під ним
//  параметри цієї гри: два перемикачі («Оверлей», «Обмеження»), повзунок
//  ліміту FPS і вибір «за частотою монітора». Одночасно розгорнуто одну гру.
// ======================================================================
sealed class GameRow
{
    public Game G;
    public bool Ov, Lim;               // оверлей у цій грі; обмеження частоти кадрів (за замовчуванням вимкнено)
    public int IntRaw = -1;            // «Інтеграція в гру»: -1 за замовчуванням (є, якщо немає античіта), 0 вимкнено, 1 увімкнено явно
    public string Api = "";            // що використовує гра (відомо після першого запуску): dx8..dx12, opengl, vulkan, ddraw
    public string Look = "";           // власний вигляд оверлею (порожньо - загальний)
    public int Mode = 3;               // показ у справжньому повноекрані: 3 автоматично, 0 накладене вікно, 1 у кадрі
    public double Fps;                 // 0 - за частотою монітора
    public int MonHz;                  // частота монітора, на якому гра працювала востаннє (0 - невідомо)
    public string Ac = "";             // що знайдено: античіт, VAC тощо
    public bool Integr { get { return IntRaw == 1 || (IntRaw < 0 && Ac.Length == 0); } }
}

// підпис, який у вимкненому стані лишається читабельним на будь-якій темі
// (стандартний Label малює вимкнений текст сірим системним кольором - на темному тлі його майже не видно)
sealed class DimLabel : Label
{
    protected override void OnPaint(PaintEventArgs e)
    {
        Color c = Enabled ? ForeColor : Util.Blend(BackColor, ForeColor, 0.55f);
        e.Graphics.Clear(BackColor);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, c, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }
}

sealed class GameList : Panel
{
    public readonly List<GameRow> Rows = new List<GameRow>();
    public int Sel = -1;
    public event EventHandler Changed;
    // користувач вмикає інтеграцію в грі з античітом: true - підтвердив ризик
    public Func<GameRow, bool> ConfirmAc;
    public Action<GameRow> OpenLook;                    // «Налаштувати вигляд…»: перейти до вигляду цієї гри
    // частоти моніторів для цієї гри: першою - та, за якою гра обмежується «за монітором» (монітор гри або основний)
    public Func<GameRow, int[]> MonitorRates = delegate { return new int[] { 60 }; };
    public Func<GameRow, bool> MonitorFromGame = delegate { return false; };      // true: перша частота - монітора, на якому гра вже запускалась
    // за номером режиму (Game.Mode): 0 вікно, 1 у кадрі, 3 автоматично
    public static readonly string[] ModeNames = { L.T("Накладене вікно"), L.T("Вбудований у кадр"), "", L.T("Автоматично") };
    static readonly int[] ModeItems = { 3, 0, 1 };

    const int HeadH = 34, Gap = 3, Pad = 12;
    readonly CheckBox cbOv = new SToggle(), cbLim = new SToggle(), cbInt = new SToggle(), cbLook = new SToggle();
    readonly Button btLook = new SBtn();
    readonly Label lMode = new DimLabel(), lInt = new DimLabel();
    readonly SCombo cbMode = new SCombo();
    readonly Label lModeSel = new DimLabel();
    readonly Label lD8 = new DimLabel();             // стан обгортки старих API (DirectX 8 → 9, DirectDraw): NeoBar ставить її сам
    readonly SSlider tb = new SSlider();
    readonly SNum nud = new SNum();
    readonly Label lFps = new DimLabel(), lUnit = new DimLabel(), lHz = new DimLabel();
    bool loading;
    int hot = -1;
    bool tbDrag;                        // повзунок тягнуть мишею: лише тоді він прилипає до частот моніторів
    const int SnapR = 4;                // на скільки FPS від частоти монітора повзунок прилипає
    int[] tops = new int[0];

    // власна тонка прокрутка замість системної смуги
    int sy, contentH;
    readonly ThinBar vbar = new ThinBar();
    new Point AutoScrollPosition { get { return new Point(0, -sy); } set { SetScroll(Math.Abs(value.Y)); } }
    new Size AutoScrollMinSize { get { return new Size(0, contentH); } set { contentH = value.Height; SetScroll(sy); } }
    int CW { get { vbar.View = ClientSize.Height; vbar.Content = contentH; return ClientSize.Width - (vbar.Need ? ThinBar.W : 0); } }

    void SetScroll(int v)
    {
        vbar.View = ClientSize.Height;
        vbar.Content = contentH;
        int nv = vbar.Clamp(v);
        vbar.Pos = nv;
        if (nv == sy) return;
        sy = nv;
        if (Sel >= 0 && tops != null && tops.Length == Rows.Count) PlaceDetail();
        Invalidate();
    }

    public GameList()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = FormTheme.Bg;
        ForeColor = FormTheme.Fg;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        cbOv.Text = L.T("Показувати оверлей у цій грі");
        cbInt.Text = L.T("Інтеграція в гру");
        cbLook.Text = L.T("Власний вигляд оверлею");
        btLook.Text = L.T("Налаштувати вигляд…"); btLook.FlatStyle = FlatStyle.Flat; btLook.Visible = false; Controls.Add(btLook);
        btLook.Click += delegate { GameRow r = Cur(); if (r != null && OpenLook != null) OpenLook(r); };
        cbLook.CheckedChanged += delegate
        {
            GameRow r = Cur(); if (r == null || loading) return;
            if (cbLook.Checked)
            {
                if (r.Look.Length == 0) { try { r.Look = (OverlayCfg.Base ?? OverlayCfg.Capture()).Encode(); } catch { r.Look = ""; } }
            }
            else r.Look = "";
            btLook.Enabled = cbLook.Checked;
            Fire();
        };
        cbLim.Text = L.T("Обмежувати частоту кадрів");
        lMode.AutoSize = false; lMode.Height = 40; lMode.Visible = false; Controls.Add(lMode);
        lInt.AutoSize = false; lInt.Visible = false; Controls.Add(lInt);
        foreach (CheckBox c in new CheckBox[] { cbOv, cbLook, cbInt, cbLim }) { c.AutoSize = false; c.Height = 24; c.Visible = false; Controls.Add(c); }
        tb.AutoSize = false; tb.Height = 30; tb.TickStyle = TickStyle.BottomRight; tb.TickFrequency = 10000; tb.Minimum = 10; tb.Maximum = 360; tb.SmallChange = 1; tb.LargeChange = 10;
        tb.Visible = false; Controls.Add(tb);
        nud.DecimalPlaces = 2; nud.Minimum = 10; nud.Maximum = 1000; nud.Increment = 1; nud.Width = 84; nud.Visible = false; Controls.Add(nud);
        lFps.Text = L.T("Ліміт"); lFps.AutoSize = false; lFps.Height = 22; lFps.Visible = false; Controls.Add(lFps);
        lUnit.Text = "FPS"; lUnit.AutoSize = false; lUnit.Height = 22; lUnit.Visible = false; Controls.Add(lUnit);
        lHz.AutoSize = false; lHz.Height = 38; lHz.Visible = false; Controls.Add(lHz);

        lModeSel.Text = L.T("Показ у повноекрані"); lModeSel.AutoSize = false; lModeSel.Height = 22; lModeSel.Visible = false; Controls.Add(lModeSel);
        cbMode.DropDownStyle = ComboBoxStyle.DropDownList; cbMode.FlatStyle = FlatStyle.Flat;
        foreach (int mm in ModeItems) cbMode.Items.Add(ModeNames[mm]);
        cbMode.Visible = false; Controls.Add(cbMode);
        cbMode.SelectedIndexChanged += delegate
        {
            GameRow r = Cur(); if (r == null || loading) return;
            if (cbMode.SelectedIndex < 0 || cbMode.SelectedIndex >= ModeItems.Length) return;
            r.Mode = ModeItems[cbMode.SelectedIndex];
            lMode.Text = FpsLimiter.ModeText(r.Api, EffMode(r));
            Fire();
        };
        lD8.Visible = false; lD8.AutoSize = false; Controls.Add(lD8);
        cbOv.CheckedChanged += delegate
        {
            GameRow r = Cur(); if (r == null || loading) return;
            r.Ov = cbOv.Checked;
            Fire();
        };
        // інтеграція: у грі з античітом за замовчуванням вимкнена, увімкнення - лише після попередження
        cbInt.CheckedChanged += delegate
        {
            GameRow r = Cur(); if (r == null || loading) return;
            bool ac = r.Ac.Length > 0;
            if (cbInt.Checked)
            {
                if (ac && r.IntRaw != 1)
                {
                    bool ok = ConfirmAc != null && ConfirmAc(r);
                    if (!ok) { loading = true; cbInt.Checked = false; loading = false; return; }
                }
                r.IntRaw = ac ? 1 : -1;
            }
            else r.IntRaw = ac ? -1 : 0;
            Relayout();
            Fire();
        };
        cbLim.CheckedChanged += delegate
        {
            GameRow r = Cur(); if (r == null || loading) return;
            r.Lim = cbLim.Checked;
            SyncFps(r);
            Fire();
        };
        tb.MouseDown += delegate { tbDrag = true; };
        tb.MouseUp += delegate { tbDrag = false; };
        tb.ValueChanged += delegate
        {
            GameRow r = Cur(); if (r == null || loading) return;
            int v = tb.Value;
            int[] rates = MonitorRates(r);
            // прив'язка: поруч із частотою монітора повзунок стає точно на неї
            if (tbDrag)
                foreach (int m in rates)
                    if (m != v && m >= tb.Minimum && m <= tb.Maximum && Math.Abs(v - m) <= SnapR) { v = m; loading = true; tb.Value = m; loading = false; break; }
            r.Fps = v == rates[0] ? 0 : v;             // на частоті «свого» монітора: ліміт іде за монітором, а не фіксується числом
            loading = true; nud.Value = v; loading = false;
            HzText(r);
            Fire();
        };
        nud.ValueChanged += delegate
        {
            GameRow r = Cur(); if (r == null || loading) return;
            double nv = (double)nud.Value;
            r.Fps = Math.Abs(nv - MonitorRates(r)[0]) < 0.005 ? 0 : nv;
            loading = true; tb.Value = (int)Math.Max(tb.Minimum, Math.Min(tb.Maximum, Math.Round(nv))); loading = false;
            HzText(r);
            Fire();
        };
    }


    // позначки частот моніторів на шкалі повзунка
    void SetTicks(int[] rates)
    {
        tb.Ticks = rates ?? new int[0];
        tb.Invalidate();
    }

    // підпис під повзунком: до чого він прилипає і чи стоїть зараз на частоті монітора
    void HzText(GameRow r)
    {
        int[] rates = MonitorRates(r);
        bool fromGame = MonitorFromGame(r);
        string def = rates[0] + L.T(" Гц");
        string src = fromGame ? L.T("монітор, на якому гра запускалась востаннє") : L.T("основний монітор");
        string list = "";
        for (int i = 0; i < rates.Length; i++) list += (i > 0 ? ", " : "") + rates[i] + L.T(" Гц") + (i == 0 && rates.Length > 1 ? (fromGame ? L.T(" (монітор гри)") : L.T(" (основний)")) : "");
        if (r.Fps <= 0)
            lHz.Text = L.T("Ліміт іде за монітором, на якому гра: у вікні налаштувань це ") + def + " (" + src + L.T("); під час гри береться той монітор, де її вікно.") + (rates.Length > 1 ? L.T(" Позначки на шкалі: ") + list : "");
        else
        {
            int hit = Array.IndexOf(rates, (int)Math.Round(r.Fps));
            lHz.Text = hit > 0 && Math.Abs(r.Fps - Math.Round(r.Fps)) < 0.005
                ? L.T("Дорівнює частоті іншого монітора (") + rates[hit] + L.T(" Гц): фіксований ліміт. Щоб іти за монітором гри - став повзунок на ") + def
                : L.T("Фіксований ліміт. Повзунок прилипає до частот моніторів: ") + list + L.T(". На ") + def + L.T(" ліміт іде за монітором гри.");
        }
    }

    // оновити розкладку розгорнутої гри після зміни загальних налаштувань
    public void Relayout2() { if (tops != null && tops.Length == Rows.Count) Relayout(); }

    GameRow Cur() { return Sel >= 0 && Sel < Rows.Count ? Rows[Sel] : null; }

    void Fire() { Invalidate(); if (Changed != null) Changed(this, EventArgs.Empty); }

    // стан повзунка й поля залежить від «обмеження»; позначки - частоти моніторів
    void SyncFps(GameRow r)
    {
        bool old = loading; loading = true;
        int[] rates = MonitorRates(r);
        SetTicks(rates);
        double v = r.Fps > 0 ? r.Fps : rates[0];
        nud.Value = (decimal)Math.Max((double)nud.Minimum, Math.Min((double)nud.Maximum, v));
        tb.Value = (int)Math.Max(tb.Minimum, Math.Min(tb.Maximum, Math.Round(v)));
        bool on = r.Lim && r.Integr;
        tb.Enabled = on; nud.Enabled = on;
        lFps.Enabled = on; lUnit.Enabled = on; lHz.Enabled = on;
        HzText(r);
        loading = old;
    }

    // ---------- наповнення ----------
    public void SetRows(List<GameRow> rows)
    {
        Rows.Clear();
        Rows.AddRange(rows);
        Sel = -1;
        HideDetail();
        Relayout();
        Invalidate();
    }

    // кольори списку й дочірніх елементів беруться з поточної теми
    void ApplyColors()
    {
        BackColor = FormTheme.Bg;
        ForeColor = FormTheme.Fg;
        Color dc = Util.Blend(FormTheme.Bg, FormTheme.Fg, 0.035f);
        foreach (Control c in new Control[] { cbOv, cbLook, cbInt, cbLim, tb, lFps, lUnit, lMode, lHz, lModeSel })
        {
            c.BackColor = dc;
            c.ForeColor = FormTheme.Fg;
        }
        cbMode.BackColor = dc; cbMode.ForeColor = FormTheme.Fg;
        SBtn lb = btLook as SBtn; if (lb != null) lb.Backdrop = dc;
        lD8.BackColor = dc; lD8.ForeColor = FormTheme.Sub;
        lInt.BackColor = dc; lInt.ForeColor = FormTheme.Sub;
        nud.BackColor = dc;
        nud.ForeColor = FormTheme.Fg;
    }


    void HideDetail()
    {
        foreach (Control c in new Control[] { cbOv, cbLook, btLook, cbInt, lInt, lMode, cbLim, tb, nud, lFps, lUnit, lHz, lModeSel, cbMode, lD8 }) c.Visible = false;
    }

    // показ у повноекрані (як Game.Mode): без інтеграції - лише окреме вікно (2)
    public int EffMode(GameRow r) { return !r.Integr ? 2 : (r.Mode == 0 || r.Mode == 1 ? r.Mode : 3); }

    // API гри змінився (гру запустили): оновити підпис і пояснення без перевідкриття вікна
    public void SyncApi()
    {
        GameRow r = Cur();
        if (r != null) { lMode.Text = FpsLimiter.ModeText(r.Api, EffMode(r)); Relayout(); }
        Invalidate();
    }

    // підпис про обгортку старого API (DirectX 8 → 9, DirectDraw): NeoBar ставить її автоматично, у іграх без античіта
    static string WrapInfo(GameRow r) { return r == null ? "" : GameWrap.Info(r.G, r.Api); }

    int DetailH(GameRow r) { return Px((r.Ac.Length > 0 ? 394 : 356) + (WrapInfo(r).Length > 0 ? 70 + WrapExtra(WrapInfo(r)) : 0)); }
    static int WrapExtra(string s) { return s.Length > 230 ? 28 : 0; }

    void PlaceWrap(GameRow r, int x, int y, int w)
    {
        string t = WrapInfo(r);
        lD8.Visible = t.Length > 0;
        if (t.Length == 0) return;
        lD8.Text = t;
        lD8.SetBounds(x, y, w, Px(62 + WrapExtra(t)));
    }
    static float dpi;
    int Px(int v)
    {
        if (dpi <= 0) { try { using (Graphics g = CreateGraphics()) dpi = g.DpiX / 96f; } catch { dpi = 1f; } }
        return (int)Math.Round(v * dpi);
    }

    // ---------- розкладка ----------
    // висоти рядків, розміри області прокручування й позиції дочірніх контролів
    void Relayout()
    {
        tops = new int[Rows.Count];
        int y = Px(2);
        for (int i = 0; i < Rows.Count; i++)
        {
            tops[i] = y;
            y += Px(HeadH) + Px(Gap);
            if (i == Sel) y += DetailH(Rows[i]);
        }
        AutoScrollMinSize = new Size(0, y + Px(2));
        PlaceDetail();
        Invalidate();
    }

    void PlaceDetail()
    {
        GameRow r = Cur();
        if (r == null) { HideDetail(); return; }
        ApplyColors();
        int sy = AutoScrollPosition.Y;                       // від'ємне зміщення прокрутки
        int w = CW - Px(Pad) * 2 - Px(8);
        int x = Px(Pad) + Px(8);
        int y = tops[Sel] + Px(HeadH) + Px(8) + sy;
        bool ac = r.Ac.Length > 0;
        bool integr = r.Integr;
        bool old = loading; loading = true;
        cbOv.Checked = r.Ov;
        cbInt.Checked = integr;
        cbMode.SelectedIndex = Math.Max(0, Array.IndexOf(ModeItems, EffMode(r) == 2 ? r.Mode : EffMode(r)));
        cbLim.Checked = r.Lim && integr;
        cbLook.Checked = r.Look.Length > 0;
        btLook.Enabled = r.Look.Length > 0;
        loading = old;

        cbOv.SetBounds(x, y, w, Px(24)); y += Px(28);
        cbLook.SetBounds(x, y, w - Px(190), Px(24));
        btLook.SetBounds(x + w - Px(180), y - Px(3), Px(180), Px(30)); y += Px(32);
        cbInt.SetBounds(x, y, w, Px(24)); y += Px(28);
        lInt.SetBounds(x + Px(24), y, w - Px(24), Px(ac ? 96 : 60));
        lInt.Text = ac
            ? L.T("⚠ Знайдено: ") + r.Ac + L.T(". Вимкнено за замовчуванням. Інтеграція вставляє бібліотеку NeoBar у процес гри (хуки): ліміт кадрів, підміна повноекрану, показ у кадрі. У мережевій грі це може призвести до блокування акаунта. Без неї оверлей - звичайне вікно поверх гри (лише в режимі «Вікно без рамки»).")
            : L.T("Дозволяє NeoBar впроваджувати бібліотеку в гру: точний API, ліміт кадрів, підміна повноекрану вікном без рамки, показ у кадрі. Без неї оверлей - звичайне вікно поверх гри (у вікні або вікні без рамки).");
        lInt.ForeColor = ac ? FormTheme.Warn : FormTheme.Sub;
        y += Px(ac ? 100 : 64);
        lModeSel.SetBounds(x + Px(24), y + Px(3), Px(150), Px(22));
        cbMode.SetBounds(x + Px(178), y - Px(3), Px(220), Px(30)); y += Px(30);
        lMode.SetBounds(x + Px(24), y, w - Px(24), Px(52)); y += Px(54);
        lMode.Text = FpsLimiter.ModeText(r.Api, EffMode(r));
        cbLim.SetBounds(x, y, w, Px(24)); y += Px(28);
        lFps.SetBounds(x + Px(4), y + Px(4), Px(44), Px(22));
        int tbx = x + Px(50);
        int nw = Px(84), unitW = Px(32);
        int tbw = Math.Max(Px(80), w - Px(50) - nw - unitW - Px(12));
        tb.SetBounds(tbx, y, tbw, Px(30));
        nud.SetBounds(tbx + tbw + Px(6), y, nw, Px(30));
        lUnit.SetBounds(tbx + tbw + Px(6) + nw + Px(4), y + Px(5), unitW, Px(22));
        y += Px(36);
        lHz.SetBounds(x + Px(4), y, w - Px(4), Px(38));
        y += Px(42);
        cbMode.Enabled = integr; lModeSel.Enabled = integr;
        cbLim.Enabled = integr;
        PlaceWrap(r, x, y, w);
        foreach (Control c in new Control[] { cbOv, cbLook, btLook, cbInt, lInt, lMode, cbLim, tb, nud, lFps, lUnit, lHz, lModeSel, cbMode }) c.Visible = true;
        SyncFps(r);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        SetScroll(sy);
        if (Rows != null && tops != null && tops.Length == Rows.Count) PlaceDetail();
    }

    // ---------- малювання ----------
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(FormTheme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // зсув прокрутки додається до координат напряму (TextRenderer не завжди враховує перетворення Graphics, і текст відставав би від плашок)
        int dy = AutoScrollPosition.Y;
        if (Rows.Count == 0)
        {
            TextRenderer.DrawText(g, L.T("Ігор не знайдено. Натисніть «Знайти ігри» або «Додати гру»."), Font, new Rectangle(Px(Pad), Px(10), CW - Px(Pad) * 2, Px(40)),
                FormTheme.Sub, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return;
        }
        int cw = CW;
        for (int i = 0; i < Rows.Count && i < tops.Length; i++)
        {
            int top = tops[i];
            int hh = Px(HeadH);
            Rectangle hr = new Rectangle(Px(Pad) / 2, top + dy, cw - Px(Pad), hh);
            bool sel = i == Sel;
            if (hr.Bottom < 0 || hr.Top > ClientSize.Height) { if (!sel) continue; }
            GameRow r = Rows[i];
            Color plate = sel ? Util.Blend(FormTheme.Card, FormTheme.Acc, 0.18f) : (i == hot ? FormTheme.Hover : FormTheme.Card);
            using (GraphicsPath gp = Util.Round(hr, Px(7)))
            {
                using (SolidBrush b = new SolidBrush(plate)) g.FillPath(b, gp);
                using (Pen p = new Pen(sel ? FormTheme.Acc : FormTheme.Line, 1f)) g.DrawPath(p, gp);
            }
            // шеврон
            int cx = hr.X + Px(14), cy = hr.Y + hh / 2, cs = Px(4);
            PointF[] ch = sel
                ? new PointF[] { new PointF(cx - cs, cy - cs / 2f), new PointF(cx + cs, cy - cs / 2f), new PointF(cx, cy + cs - 1) }
                : new PointF[] { new PointF(cx - cs / 2f, cy - cs), new PointF(cx - cs / 2f, cy + cs), new PointF(cx + cs - 1, cy) };
            using (SolidBrush b = new SolidBrush(sel ? FormTheme.Acc : FormTheme.Sub)) g.FillPolygon(b, ch);

            // позначки праворуч
            int rx = hr.Right - Px(10);
            if (r.Ac.Length > 0) rx = Chip(g, rx, hr, "⚠", FormTheme.Warn);
            Rectangle nr = new Rectangle(hr.X + Px(28), hr.Y, Math.Max(10, rx - hr.X - Px(34)), hh);
            TextRenderer.DrawText(g, r.G.Name + "   ", Font, nr, FormTheme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (sel)
            {
                // фон під параметрами: вертикальна смуга акценту зліва
                Rectangle dr = new Rectangle(hr.X, hr.Bottom + Px(2), hr.Width, DetailH(r) - Px(4));
                using (SolidBrush b = new SolidBrush(Util.Blend(FormTheme.Bg, FormTheme.Fg, 0.035f))) g.FillRectangle(b, dr);
                using (SolidBrush b = new SolidBrush(FormTheme.Acc)) g.FillRectangle(b, dr.X, dr.Y, Math.Max(2, Px(3)), dr.Height);
            }
        }
        vbar.Paint(g, ClientRectangle);
    }

    // позначка-«пілюля»; повертає новий правий край
    int Chip(Graphics g, int right, Rectangle hr, string text, Color col)
    {
        Size ts = TextRenderer.MeasureText(text, Font, new Size(1000, 100), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        int cw = ts.Width + Px(14), ch = Px(20);
        Rectangle cr = new Rectangle(right - cw, hr.Y + (hr.Height - ch) / 2, cw, ch);
        Color bg = Util.Blend(FormTheme.Card, col, 0.28f);
        using (GraphicsPath gp = Util.Round(cr, ch / 2))
        {
            using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, gp);
            using (Pen p = new Pen(Util.Blend(FormTheme.Card, col, 0.6f), 1f)) g.DrawPath(p, gp);
        }
        TextRenderer.DrawText(g, text, Font, cr, FormTheme.Fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        return cr.X - Px(6);
    }

    // ---------- миша ----------
    int RowAt(Point p)
    {
        int y = p.Y - AutoScrollPosition.Y;
        for (int i = 0; i < Rows.Count && i < tops.Length; i++)
            if (y >= tops[i] && y < tops[i] + Px(HeadH)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (vbar.MouseMove(this, e, ClientRectangle, SetScroll)) return;
        if (vbar.Hot) { if (hot != -1) { hot = -1; Invalidate(); } Cursor = Cursors.Default; return; }
        int h = RowAt(e.Location);
        if (h != hot) { hot = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hot != -1 || vbar.Hot) { hot = -1; vbar.Hot = false; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        vbar.MouseUp(this);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (vbar.MouseDown(this, e, ClientRectangle, SetScroll)) return;
        int i = RowAt(e.Location);
        if (i < 0) return;
        Sel = i == Sel ? -1 : i;
        Relayout();
        // розгорнутий рядок має бути повністю видимий
        if (Sel >= 0)
        {
            int bottom = tops[Sel] + Px(HeadH) + DetailH(Rows[Sel]) + Px(4);
            int viewBottom = -AutoScrollPosition.Y + ClientSize.Height;
            if (bottom > viewBottom) AutoScrollPosition = new Point(0, -AutoScrollPosition.Y + (bottom - viewBottom));
            if (tops[Sel] < -AutoScrollPosition.Y) AutoScrollPosition = new Point(0, tops[Sel]);
            PlaceDetail();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        SetScroll(sy - e.Delta * Px(90) / 120);
        HandledMouseEventArgs he = e as HandledMouseEventArgs;
        if (he != null) he.Handled = true;
    }
}
