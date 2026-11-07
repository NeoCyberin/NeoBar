using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

using System.Drawing.Text;
// ======================================================================
//  Вікно налаштувань (створюється лише на вимогу).
//  Ліворуч - навігація за розділами, праворуч - сторінка розділу з картками налаштувань.
//  Тут - каркас і збереження; сторінки - SettingsPages.cs, тема/віджети/дані - SettingsView.cs,
//  елементи панелі - SettingsItems.cs, шпалери - SettingsWall.cs, віджети й оверлей - SettingsWidgets.cs.
// ======================================================================
sealed partial class SettingsForm : Form, IGradHost
{
    readonly NeoBar owner;
    Cfg c = new Cfg();
    string themeWas = "";      // тема на момент відкриття (щоб відрізнити ручний вибір від святкової теми)
    List<Entry> list = new List<Entry>();
    readonly string[] sideIds = { "left", "right", "top", "bottom" };
    readonly List<string> monIds = new List<string>();
    readonly string[] styleIds = { "regular", "bold", "italic", "bolditalic" };
    bool loading, adminWas, autoWas;

    // каркас вікна
    NavRail nav;
    HeadBar head;
    SHost foot;
    readonly List<Control> pages = new List<Control>();
    int page = -1;
    // порядок вкладок: вміст і вигляд панелі, її поведінка й пошук, потім шпалери та ігровий оверлей,
    // наприкінці рідкісне (місце, дані, діагностика)
    internal const int PgItems = 0, PgView = 1, PgPanel = 2, PgSearch = 3, PgWall = 4, PgOverlay = 5, PgPlace = 6, PgData = 7, PgDiag = 8;
    static int lastPage = PgItems;      // остання відкрита вкладка (до закриття NeoBar) - так відкриваються налаштування з меню профілю
    static readonly string[][] PageInfo =
    {
        new[] { "items",  L.T("Елементи"),          L.T("Програми, папки, сайти, групи й віджети на панелі та їхній порядок") },
        new[] { "view",   L.T("Вигляд"),            L.T("Тема, кольори, іконки, підписи й шрифт") },
        new[] { "panel",  L.T("Панель"),            L.T("Де стоїть панель, як поводиться і як запускається") },
        new[] { "search", L.T("Пошук"),             L.T("Поле пошуку, гаряча клавіша й пошук файлів") },
        new[] { "wall",   L.T("Шпалери"),           L.T("Зображення, слайд-шоу з переходами й живі шпалери робочого столу") },
        new[] { "game",   L.T("Ігровий оверлей"),   L.T("Показники поверх гри, ліміт кадрів і налаштування окремих ігор") },
        new[] { "place",  L.T("Місце й погода"),    L.T("Розташування, часовий пояс і точність годинника, свята, джерела даних") },
        new[] { "data",   L.T("Дані"),              L.T("Експорт, імпорт і скидання налаштувань профілю, кеш з інтернету") },
        new[] { "diag",   L.T("Діагностика"),       L.T("Стан системи, вікно привітання й журнали для пошуку проблем") },
    };

    // «Елементи»
    SList lb;
    Button bUp, bDown, bDel, bGone, bBrowseFile, bBrowseFolder, bBrowseIcon, bFavicon, bColor, bColorReset, bCopy;
    Label lStatus;
    TextBox tbName, tbTarget, tbIconFile, tbTags;
    SCombo cbIconMode, cbThemeIcon, cbGMode;
    PictureBox pic;
    SCard propCard;
    int pk;                         // що вибрано: 0 нічого, 1 елемент, 2 група, 3 віджет
    string wTitle = "", wDesc = "";

    // «Панель» і «Пошук»
    SCombo cbSide, cbMonitor, cbMode;
    SNum nW, nGap;
    CheckBox chCompact, chFs, chAuto, chSearch, chHot, chFiles, chAdmin;
    HotkeyBox hkSearch;
    CheckBox chSuggest, chWelcome, chUpd;
    Label lHkSearch;
    Keys searchKeyOk;

    // «Вигляд»
    SCombo cbTheme, cbFx, cbGroups, cbFont, cbFontStyle, cbLabelPos;
    SNum nIcon, nFont;
    CheckBox chBlur, chAnim;
    readonly SSlider[] tbA = new SSlider[4];
    readonly Label[] lA = new Label[4];
    readonly Button[] bCol = new Button[4];

    // «Місце й погода»
    TextBox tbCity;
    Label lCity;
    SList lbCitySug;
    bool citySugOpen;
    SCard cityCard;
    List<string[]> citySug = new List<string[]>();
    System.Windows.Forms.Timer cityTimer;
    string cityLast = "";
    SRadio rbAuto, rbManual;
    CheckBox chLocAllow;
    Button bDetect;
    MapView map;
    Label lMap, lCache;

    // «Дані»
    Label lData;

    int S(float v) { return SUi.S(v); }

    // тема змінилася після «Застосувати» - вікно відкривається наново в новому оформленні, на тому самому місці
    static Rectangle reopenAt = Rectangle.Empty;
    string themeAtOpen = "";
    string ThemeSig() { return c.ThemeId + "|" + c.CBg.ToArgb() + "|" + c.CHover.ToArgb() + "|" + c.CText.ToArgb() + "|" + c.CAccent.ToArgb() + "|" + c.Blur; }

    void Retheme()
    {
        if (WindowState == FormWindowState.Normal) reopenAt = Bounds;
        int pg = page;
        NeoBar o = owner;
        try { o.BeginInvoke(new Action(delegate { o.OpenSettingsTab(pg); })); } catch { return; }
        Close();
    }

    static T Add<T>(Control parent, T ctl, int x, int y, int w, int h) where T : Control
    {
        ctl.SetBounds(x, y, w, h);
        parent.Controls.Add(ctl);
        return ctl;
    }

    static Label Lbl(Control parent, string text, int x, int y, int w, int h)
    {
        Label l = Add(parent, new Label(), x, y, w, h);
        l.AutoSize = false;
        l.Text = text;
        return l;
    }

    // ---- елементи для рядків карток ----
    // tag: "primary", "danger", "ghost", "g:<піктограма>" (через пробіл)
    static Button Bt(string text, string tag, int w, EventHandler click)
    {
        Button b = new SBtn();
        b.Text = text;
        b.Tag = tag ?? "";
        b.Size = new Size(SUi.S(w), SUi.S(32));
        b.UseVisualStyleBackColor = true;
        if (click != null) b.Click += click;
        return b;
    }

    static SCombo Cb(int w, params object[] items)
    {
        SCombo cb = new SCombo();
        cb.DropDownStyle = ComboBoxStyle.DropDownList;
        cb.Width = SUi.S(w);
        if (items != null && items.Length > 0) cb.Items.AddRange(items);
        return cb;
    }

    static CheckBox Tg()
    {
        CheckBox x = new SToggle();
        x.Text = "";
        x.AutoSize = false;
        x.Size = new Size(SUi.S(128), SUi.S(28));
        return x;
    }

    static SNum Nm(int w, decimal min, decimal max)
    {
        SNum n = new SNum();
        n.Width = SUi.S(w);
        n.Minimum = min; n.Maximum = max;
        n.TextAlign = HorizontalAlignment.Center;
        return n;
    }

    static TextBox Tb()
    {
        TextBox t = new TextBox();
        t.BorderStyle = BorderStyle.None;
        return t;
    }

    static Label Note()
    {
        Label l = new Label();
        l.AutoSize = false;
        l.Text = "";
        l.ForeColor = FormTheme.Sub;
        l.Font = SUi.Small;
        return l;
    }

    static Label Val(int w)
    {
        Label l = new Label();
        l.AutoSize = false;
        l.Size = new Size(SUi.S(w), SUi.S(24));
        l.TextAlign = ContentAlignment.MiddleLeft;
        l.Font = SUi.Base;
        return l;
    }

    static SSlider Tk(int w, int min, int max)
    {
        SSlider t = new SSlider();
        t.AutoSize = false;
        t.Size = new Size(SUi.S(w), SUi.S(30));
        t.TickStyle = TickStyle.None;
        t.Minimum = min; t.Maximum = max;
        return t;
    }

    public SettingsForm(NeoBar owner, Entry pick, int tab)
    {
        this.owner = owner;
        AutoScaleMode = AutoScaleMode.None;
        Store.Load(owner.CfgPath, c, list);
        NormalizePaths();
        FormTheme.Init(Pal.Of(owner.CurrentTheme, owner.LabelFont, 1f));
        SUi.Init(owner.Dpi);
        Font = SUi.Base;
        Text = L.T("NeoBar - Налаштування");
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        AppIcon.Apply(this);
        MaximizeBox = true;
        BackColor = FormTheme.Bg;
        ForeColor = FormTheme.Fg;
        Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        ClientSize = new Size(Math.Min(S(1220), wa.Width - S(40)), Math.Min(S(800), wa.Height - S(40)));
        MinimumSize = SizeFromClientSize(new Size(Math.Min(S(1000), wa.Width - S(40)), Math.Min(S(640), wa.Height - S(40))));
        if (!reopenAt.IsEmpty)
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = reopenAt;
            reopenAt = Rectangle.Empty;
        }
        themeAtOpen = ThemeSig();

        Build();
        FormTheme.Apply(this);
        FillView();
        Util.Buffer(this);
        RefreshList(0);
        ShowEntry(pick, tab);
    }

    // Відкриває потрібний розділ або елемент (шукаємо за видом, назвою й ціллю)
    public void ShowEntry(Entry pick, int tab)
    {
        if (pick != null)
        {
            string tg = Util.Collapse(pick.Target);
            for (int i = 0; i < list.Count; i++)
            {
                Entry e = list[i];
                if (e.Kind != pick.Kind) continue;
                if (e.Kind == Kind.Header && e.Name != pick.Name) continue;
                if (e.Kind == Kind.Item && (e.Name != pick.Name || Util.Collapse(e.Target) != tg)) continue;
                if (e.Kind == Kind.Widget && e.Target != pick.Target) continue;
                nav.SelectedIndex = PgItems;
                RefreshList(i);
                break;
            }
            if (nav.SelectedIndex < 0) nav.SelectedIndex = PgItems;
        }
        else nav.SelectedIndex = Math.Max(0, Math.Min(pages.Count - 1, tab < 0 ? lastPage : tab));
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        FormTheme.Title(this);
        DropFiles.Register(this);          // від адміністратора файли з Провідника приходять старим способом (WM_DROPFILES)
    }

    protected override void WndProc(ref Message m)
    {
        string[] dropped = DropFiles.Take(ref m);
        if (dropped != null)
        {
            if (dropped.Length > 0 && lb != null && lb.Visible && lb.IsHandleCreated)
            {
                Point dp = lb.PointToClient(Cursor.Position);
                BeginInvoke(new Action(delegate { AddFilesAt(dropped, dp); }));
            }
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (pic != null && pic.Image != null) pic.Image.Dispose();
        OverlayClosed();
        if (cityTimer != null) { cityTimer.Stop(); cityTimer.Dispose(); }
        base.OnFormClosed(e);
    }

    // ---------- каркас: навігація, заголовок розділу, кнопки внизу ----------

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (!Festive.Backdrop(e.Graphics, this, e.ClipRectangle)) base.OnPaintBackground(e);
    }

    void Build()
    {
        SuspendLayout();
        nav = new NavRail();
        nav.Who = Prof.Cur;
        nav.BackColor = FormTheme.Bg;
        foreach (string[] pi in PageInfo) nav.Add(pi[0], pi[1]);
        Controls.Add(nav);

        head = new HeadBar();
        Controls.Add(head);

        pages.Add(BuildItems());
        pages.Add(BuildView());
        pages.Add(BuildPanel());
        pages.Add(BuildSearch());
        pages.Add(BuildWall());
        pages.Add(BuildOverlayTab());
        pages.Add(BuildPlace());
        pages.Add(BuildData());
        pages.Add(BuildDiag());
        foreach (Control p in pages) { p.Visible = false; Controls.Add(p); }

        foot = new SHost();
        Controls.Add(foot);
        Label hint = new Label();
        hint.AutoSize = false;
        hint.Text = L.T("«Застосувати» зберігає панель і поточну вкладку, «Готово» - усе.");
        hint.ForeColor = FormTheme.Sub;
        hint.Font = SUi.Small;
        hint.TextAlign = ContentAlignment.MiddleLeft;
        foot.Controls.Add(hint);
        Festive.Clear(hint);
        Button bCancel = Bt(L.T("Скасувати"), "", 120, delegate { Close(); });
        Button bApply = Bt(L.T("Застосувати"), "", 130, delegate { if (Apply(false) && ThemeSig() != themeAtOpen) { Apply(true); Retheme(); } });
        Button bOk = Bt(L.T("Готово"), "primary g:check", 130, delegate { if (Apply(true)) Close(); });
        foot.Controls.Add(bCancel); foot.Controls.Add(bApply); foot.Controls.Add(bOk);
        foot.Paint += delegate (object s, PaintEventArgs e)
        {
            using (Pen p = new Pen(Util.Blend(FormTheme.Bg, FormTheme.Fg, 0.08f))) e.Graphics.DrawLine(p, S(24), 0, foot.Width - S(14), 0);       // з тими ж відступами, що й вміст сторінок
        };
        foot.Arrange = delegate (Rectangle r)
        {
            int bh = S(36), y = (r.Height - bh) / 2, x = r.Right - S(24);
            foreach (Button b in new Button[] { bOk, bApply, bCancel }) { x -= b.Width; b.SetBounds(x, y, b.Width, bh); x -= S(8); }
            hint.SetBounds(S(24), S(2), Math.Max(10, x - S(24)), r.Height - S(4));
        };

        nav.SelectedChanged += delegate { ShowPage(nav.SelectedIndex); };
        ResumeLayout(false);
        Arrange();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (nav != null) Arrange();
    }

    void Arrange()
    {
        Size cs = ClientSize;
        if (cs.Width <= 0 || cs.Height <= 0) return;
        int nw = S(252), fh = S(66);
        nav.SetBounds(0, 0, nw, cs.Height);
        foot.SetBounds(nw, cs.Height - fh, cs.Width - nw, fh);
        int x = nw + S(24), w = cs.Width - x - S(14);
        head.SetBounds(x, S(14), w, S(70));
        int py = head.Bottom + S(6), ph = Math.Max(S(100), foot.Top - py - S(8));
        foreach (Control p in pages) p.SetBounds(x, py, w, ph);
    }

    void ShowPage(int i)
    {
        if (i < 0 || i >= pages.Count || i == page) return;
        pages[i].Visible = true;
        for (int k = 0; k < pages.Count; k++) if (k != i) pages[k].Visible = false;
        page = i;
        lastPage = i;
        head.Title = PageInfo[i][1];
        head.Sub = PageInfo[i][2];
        if (i == PgDiag) RunDiag();
        if (i == PgOverlay) RefreshLookTargets();
    }

    bool OnPage(int i) { return page == i; }

    // ---------- збереження ----------

    static string NormIcon(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0 || s.StartsWith("theme:", StringComparison.OrdinalIgnoreCase)) return s;
        s = Util.ImportIcon(s);          // картинку з Робочого столу тощо копіюємо в icons - щоб не загубити
        string rel = Util.ToRelative(s);
        return rel != s ? rel : Util.Collapse(s);
    }

    void NormalizePaths()
    {
        foreach (Entry e in list)
        {
            if (e.Kind != Kind.Item) continue;
            e.Target = Util.Collapse(e.Target);
            e.IconSpec = NormIcon(e.IconSpec);
        }
    }

    // all: «Готово» (усе); інакше «Застосувати»: панель (елементи, вигляд, пошук тощо) і поточна вкладка
    bool Apply(bool all)
    {
        NormalizePaths();
        RefreshSel(lb.SelectedIndex >= 0 && lb.SelectedIndex < rows.Count ? rows[lb.SelectedIndex] : null);
        ReadView();
        if (!CheckSizes()) return false;
        // те, що змінюється наживо з розширених панелей, беремо з панелі, а не зі старої копії
        Cfg live = owner.Config;
        c.Fahrenheit = live.Fahrenheit;
        c.PerfCpu = live.PerfCpu; c.PerfGpu = live.PerfGpu; c.PerfRam = live.PerfRam;
        c.Welcomed = !chWelcome.Checked; live.Welcomed = c.Welcomed;
        // святкова тема: вибрали тему вручну - строк більше не діє; не чіпали - лишається те, що на панелі зараз
        c.SeasonSkip = live.SeasonSkip;
        if (c.ThemeId != themeWas) { c.SeasonTheme = ""; c.SeasonPrev = ""; c.SeasonUntil = DateTime.MinValue; }
        else
        {
            c.ThemeId = live.ThemeId;
            c.SeasonTheme = live.SeasonTheme; c.SeasonPrev = live.SeasonPrev; c.SeasonUntil = live.SeasonUntil;
        }
        themeWas = c.ThemeId;
        foreach (Entry we in list)
            if (we.Kind == Kind.Widget) { string o = owner.WidgetOpt(we.Target); if (o != null) we.Opt = o; }
        try
        {
            Store.Save(owner.CfgPath, c, list);
        }
        catch (Exception ex)
        {
            SbDialog.Show(owner, this, L.T("Не вдалося зберегти"), L.T("Не вдалося зберегти налаштування:\n") + ex.Message, L.T("Гаразд"));
            return false;
        }
        NeoBar.SetAutostart(chAuto.Checked && !c.AlwaysAdmin);
        ApplyStatics(all);
        if (all || OnPage(PgPlace)) ApplyClock();
        foreach (string wk in dropDesk) ClockData.RemoveDeskOf(wk);
        dropDesk.Clear();
        // «завжди від адміністратора»: створити/змінити/видалити завдання (один запит UAC)
        if (c.AlwaysAdmin != adminWas || (c.AlwaysAdmin && chAuto.Checked != autoWas))
            owner.ConfigureAdmin(c.AlwaysAdmin, chAuto.Checked);
        adminWas = c.AlwaysAdmin; autoWas = chAuto.Checked;
        owner.Reload();
        UpdatePreview();
        return true;
    }
}
