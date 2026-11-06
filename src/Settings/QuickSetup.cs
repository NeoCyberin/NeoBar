using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// ======================================================================
//  Швидке налаштування: перед першим запуском панелі (і для нового профілю) - край екрана, режим,
//  тема, віджети, автозапуск, ігровий оверлей, розташування. Поле пошуку - завжди (на горизонтальній панелі - значок). Вікно одразу показується в обраній темі.
//  Решта - у налаштуваннях; звідти це вікно можна відкрити ще раз (розділ «Діагностика»).
// ======================================================================
sealed class QuickSetup : SbWindow
{
    static readonly string[] SideIds = { "left", "right", "top", "bottom" };
    static readonly string[] SideNames = { L.T("Ліворуч"), L.T("Праворуч"), L.T("Угорі"), L.T("Внизу") };

    public string Side, Mode, ThemeId;
    public bool Auto, Overlay, AutoKnown = true, LocAuto;
    int scroll, contentH;               // на невеликому екрані вміст прокручується, кнопки лишаються внизу
    public readonly List<string> Widgets = new List<string>();
    public bool Done;                   // «Готово» / «Далі» (а не «Пропустити»)
    readonly bool startup;
    readonly List<Theme> themes = new List<Theme>();

    static float ScreenScale()
    {
        try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return Math.Max(1f, g.DpiX / 96f); } catch { return 1f; }
    }

    // c, list - поточні налаштування профілю (зміни повертаються через поля, нічого не записується тут)
    // перший запуск: мова - прямо в шапці (зміна перезапускає програму, швидке налаштування покажеться знову)
    protected override void HeadButtons(Graphics g, ref int right)
    {
        if (!startup) return;
        LangInfo cur = L.Find(L.Code) ?? L.All()[0];
        Rectangle r = new Rectangle(right - Px(48), Px(9), Px(44), Px(28));
        bool h = Over(r);
        using (SolidBrush b = new SolidBrush(h ? Util.Blend(P.Bg, P.Fg, 0.14f) : Util.Blend(P.Bg, P.Fg, 0.07f))) Util.FillRound(g, b, r, Px(8));
        Flags.Draw(g, new Rectangle(r.X + Px(7), r.Y + Px(5), Px(30), Px(18)), cur.Flag, cur.Code);
        Hot(r, delegate
        {
            Flags.Menu(this, new Point(r.X, r.Bottom + Px(2)), P.Bg, P.Fg, P.Acc, P.Rad, delegate (bool changed)
            {
                if (!changed) return;
                if (Prof.Restart()) Environment.Exit(0);
            });
        });
        Tip(r, cur.Name + " · Language");
        right = r.Left - Px(6);
    }

    public QuickSetup(Cfg c, List<Entry> list, bool auto, bool overlay, bool startup)
        : base(Themes.Get(c.ThemeId == "custom" ? "dark" : c.ThemeId, c), ScreenScale(), L.T("Швидке налаштування"), 760, 880)
    {
        Glyph = "\uE713";
        this.startup = startup;
        Side = Array.IndexOf(SideIds, c.Side) >= 0 ? c.Side : "right";
        Mode = c.Mode == "autohide" ? "autohide" : "fixed";
        ThemeId = c.ThemeId == "custom" ? "dark" : c.ThemeId;
        LocAuto = c.LocAllow && c.LocMode == "auto";
        Auto = auto;
        AutoKnown = !c.AlwaysAdmin;          // автозапуск через «завжди від адміністратора» тут не міняємо
        Overlay = overlay;
        foreach (Entry e in list) if (e.Kind == Kind.Widget && !Widgets.Contains(e.Target)) Widgets.Add(e.Target);
        foreach (Theme t in Themes.All) if (t.Id != "custom" && !Themes.Seasonal(t.Id)) themes.Add(t);
        ShowInTaskbar = startup;
        StartPosition = FormStartPosition.CenterScreen;
        // не вище робочої області екрана (старі ноутбуки 1366×768): решту видно прокруткою
        try
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int maxH = wa.Height - Px(12), maxW = wa.Width - Px(12);
            if (ClientSize.Height > maxH || ClientSize.Width > maxW) ClientSize = new Size(Math.Min(ClientSize.Width, maxW), Math.Min(ClientSize.Height, maxH));
        }
        catch { }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            if (startup) { cp.ExStyle &= ~0x80; cp.ExStyle |= 0x40000; }   // видно на панелі завдань
            return cp;
        }
    }

    protected override void OnShown(EventArgs e) { base.OnShown(e); Activate(); }

    // застосувати вибране до налаштувань профілю (віджети - у тому ж порядку, нові - в кінець)
    public void ApplyTo(Cfg c, List<Entry> list)
    {
        c.Side = Side;
        c.Mode = Mode;
        c.ThemeId = ThemeId;
        // розташування (погода, свята, часовий пояс): увімкнено - служба розташування Windows, інакше за IP;
        // вимкнено - нічого не визначається, місто задається вручну в налаштуваннях (лишається те, що було)
        if (LocAuto) { c.LocAllow = true; c.LocMode = "auto"; }
        else { c.LocAllow = false; c.LocMode = "manual"; }
        List<Entry> keep = new List<Entry>(), rest = new List<Entry>();
        foreach (Entry e in list)
        {
            if (e.Auto) continue;
            if (e.Kind == Kind.Widget) { if (Widgets.Contains(e.Target)) keep.Add(e); }
            else rest.Add(e);
        }
        foreach (string k in Widgets)
        {
            bool have = false;
            foreach (Entry e in keep) if (e.Target == k) have = true;
            if (have) continue;
            Entry n = new Entry();
            n.Kind = Kind.Widget; n.Target = k; n.Name = WidgetCatalog.Title(k);
            keep.Add(n);
        }
        list.Clear();
        list.AddRange(keep);
        list.AddRange(rest);
        Store.Normalize(list);
    }

    Rectangle vp;                       // видима частина вмісту (без кнопок унизу)

    // клікабельна зона лише в межах видимої частини (інакше прихований під прокруткою елемент перехопив би клік шапки)
    void HotV(Rectangle t, Action a)
    {
        Rectangle i = Rectangle.Intersect(t, vp);
        if (i.Width > 0 && i.Height >= Px(6)) Hot(i, a);
    }

    void SwitchV(Graphics g, ref int y, Rectangle r, string label, bool on, Action click)
    {
        Rectangle sr = new Rectangle(r.X, y, r.Width - Px(10), Px(28));
        if (sr.Y >= vp.Y && sr.Bottom <= vp.Bottom + Px(2)) Switch(g, sr, label, on, click);
        y += Px(32);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int bh = Px(40), bw = Px(170);
        vp = new Rectangle(r.X, r.Y, r.Width, r.Bottom - bh - Px(12) - r.Y);
        int view = vp.Height;
        scroll = Math.Max(0, Math.Min(Math.Max(0, contentH - view), scroll));
        GraphicsState st = g.Save();
        g.SetClip(vp);
        r = new Rectangle(r.X, r.Y, r.Width - (contentH > view ? Px(10) : 0), r.Height);
        int y = r.Y - scroll;
        TxtW(g, L.T("Кілька кроків - і панель готова. Усе це (і багато іншого) можна змінити пізніше в налаштуваннях."), F, new Rectangle(r.X, y, r.Width, Px(22)), P.Sub);
        y += Px(34);

        // край екрана: маленькі «екрани» з панеллю на потрібному боці
        TxtL(g, L.T("Де стоїть панель"), FB, new Rectangle(r.X, y, r.Width, Px(22)), P.Fg);
        y += Px(28);
        int tw = (r.Width - Px(12) * 3) / 4, th = Px(84);
        for (int i = 0; i < 4; i++)
        {
            Rectangle t = new Rectangle(r.X + i * (tw + Px(12)), y, tw, th);
            bool on = SideIds[i] == Side, h = Over(t);
            Card(g, t, on ? Util.Blend(P.Bg, P.Acc, 0.22f) : (h ? Util.Blend(P.Card, P.Fg, 0.05f) : P.Card));
            Rectangle scr = new Rectangle(t.X + (t.Width - Px(64)) / 2, t.Y + Px(10), Px(64), Px(40));
            using (Pen pn = new Pen(on ? P.Acc : Util.Blend(P.Card, P.Fg, 0.45f), Math.Max(1.2f, Px(1.5f)))) g.DrawRectangle(pn, scr);
            int b = Px(8);
            Rectangle bar = i == 0 ? new Rectangle(scr.X + 2, scr.Y + 2, b, scr.Height - 3) : (i == 1 ? new Rectangle(scr.Right - b - 1, scr.Y + 2, b, scr.Height - 3)
                          : (i == 2 ? new Rectangle(scr.X + 2, scr.Y + 2, scr.Width - 3, b) : new Rectangle(scr.X + 2, scr.Bottom - b - 1, scr.Width - 3, b)));
            using (SolidBrush bb = new SolidBrush(on ? P.Acc : Util.Blend(P.Card, P.Fg, 0.35f))) g.FillRectangle(bb, bar);
            TxtC(g, SideNames[i], on ? FB : F, new Rectangle(t.X, scr.Bottom + Px(6), t.Width, Px(22)), on ? P.Fg : P.Sub);
            string k = SideIds[i];
            HotV(t, delegate { Side = k; });
        }
        y += th + Px(18);

        // режим
        TxtL(g, L.T("Як поводиться"), FB, new Rectangle(r.X, y, r.Width, Px(22)), P.Fg);
        y += Px(28);
        int mw = (r.Width - Px(12)) / 2, mh = Px(58);
        string[][] modes = { new[] { "fixed", L.T("Закріплена"), L.T("Вікна підлаштовуються під панель") }, new[] { "autohide", L.T("Автосховок"), L.T("З'являється, коли підвести мишу до краю") } };
        for (int i = 0; i < 2; i++)
        {
            Rectangle t = new Rectangle(r.X + i * (mw + Px(12)), y, mw, mh);
            bool on = modes[i][0] == Mode, h = Over(t);
            Card(g, t, on ? Util.Blend(P.Bg, P.Acc, 0.22f) : (h ? Util.Blend(P.Card, P.Fg, 0.05f) : P.Card));
            TxtL(g, modes[i][1], FB, new Rectangle(t.X + Px(14), t.Y + Px(8), t.Width - Px(28), Px(22)), P.Fg);
            TxtL(g, modes[i][2], FS, new Rectangle(t.X + Px(14), t.Y + Px(30), t.Width - Px(28), Px(20)), P.Sub);
            string k = modes[i][0];
            HotV(t, delegate { Mode = k; });
        }
        y += mh + Px(18);

        // тема - вікно одразу переодягається
        TxtL(g, L.T("Тема"), FB, new Rectangle(r.X, y, r.Width, Px(22)), P.Fg);
        y += Px(28);
        int cols = 5, gw = (r.Width - Px(10) * (cols - 1)) / cols, gh = Px(56);
        for (int i = 0; i < themes.Count; i++)
        {
            Theme t = themes[i];
            Rectangle tr = new Rectangle(r.X + (i % cols) * (gw + Px(10)), y + (i / cols) * (gh + Px(10)), gw, gh);
            bool on = t.Id == ThemeId, h = Over(tr);
            Color bg = Util.Opaque(t.Glass ? Color.FromArgb(40, 52, 76) : t.Bg);
            using (GraphicsPath gp = Util.Round(tr, Math.Max(Px(4), Math.Min(Px(10), Px(t.Radius)))))
            {
                using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, gp);
                using (Pen pn = new Pen(on ? P.Acc : (h ? Util.Blend(bg, Util.Opaque(t.Text), 0.5f) : Util.Blend(bg, Util.Opaque(t.Text), 0.18f)), on ? Math.Max(2f, Px(2)) : 1f)) g.DrawPath(pn, gp);
            }
            // мініатюра: смужка акценту й три «іконки»
            using (SolidBrush ab = new SolidBrush(Util.Opaque(t.Accent))) g.FillRectangle(ab, tr.X + Px(8), tr.Y + Px(9), Px(4), gh - Px(18));
            for (int k = 0; k < 3; k++)
                using (SolidBrush ib = new SolidBrush(Util.Opaque(t.Palette != null && t.Palette.Length > k ? t.Palette[k] : Util.Blend(Util.Opaque(t.Hover), Util.Opaque(t.Accent), 0.3f * k))))
                    g.FillEllipse(ib, tr.X + Px(18) + k * Px(14), tr.Y + Px(10), Px(10), Px(10));
            TextRenderer.DrawText(g, t.Title, FS, new Rectangle(tr.X + Px(18), tr.Y + Px(26), tr.Width - Px(22), Px(22)), Util.Opaque(t.Text),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            Theme tt = t;
            HotV(tr, delegate { ThemeId = tt.Id; UseTheme(Themes.Get(tt.Id, new Cfg())); });
        }
        y += ((themes.Count + cols - 1) / cols) * (gh + Px(10)) + Px(8);

        // віджети
        TxtL(g, L.T("Віджети вгорі панелі"), FB, new Rectangle(r.X, y, r.Width, Px(22)), P.Fg);
        y += Px(28);
        int cx = r.X, ch = Px(32);
        foreach (string[] w in WidgetCatalog.Items)
        {
            if (w[0] == "battery" && !Battery.Present()) continue;
            bool on = Widgets.Contains(w[0]);
            int cw = TextRenderer.MeasureText(w[1], F).Width + Px(44);
            if (cx + cw > r.Right) { cx = r.X; y += ch + Px(8); }
            Rectangle cr = new Rectangle(cx, y, cw, ch);
            bool h = Over(cr);
            Color f = on ? Util.Blend(P.Bg, P.Acc, h ? 0.62f : 0.5f) : (h ? Util.Blend(P.Card, P.Fg, 0.08f) : P.Card);
            using (SolidBrush b = new SolidBrush(f)) Util.FillRound(g, b, cr, ch / 2);
            if (!on) using (GraphicsPath gp = Util.Round(cr, ch / 2)) using (Pen pn = new Pen(Util.Blend(P.Card, P.Fg, 0.2f))) g.DrawPath(pn, gp);
            Color tc = on ? Util.Readable(Color.White, f, 110) : P.Fg;
            G.Draw(g, on ? "check" : "plus", new RectangleF(cr.X + Px(12), cr.Y + (ch - Px(14)) / 2f, Px(14), Px(14)), tc);
            TxtL(g, w[1], F, new Rectangle(cr.X + Px(32), cr.Y, cr.Width - Px(36), ch), tc);
            string k = w[0];
            HotV(cr, delegate { if (Widgets.Contains(k)) Widgets.Remove(k); else Widgets.Add(k); });
            cx += cw + Px(8);
        }
        y += ch + Px(20);

        // інше
        if (AutoKnown) SwitchV(g, ref y, r, L.T("Запускати NeoBar разом з Windows"), Auto, delegate { Auto = !Auto; });
        SwitchV(g, ref y, r, L.T("Ігровий оверлей у іграх (FPS, температури, завантаження)"), Overlay, delegate { Overlay = !Overlay; });
        SwitchV(g, ref y, r, L.T("Визначати моє розташування (для погоди й часу)"), LocAuto, delegate { LocAuto = !LocAuto; });
        TxtW(g, LocAuto ? L.T("Служба розташування Windows, а якщо вона вимкнена - за IP-адресою (точність до міста).")
                        : L.T("Вимкнено: місто для погоди задасте вручну в налаштуваннях (розділ «Місце й погода»)."),
            FS, new Rectangle(r.X, y - Px(4), r.Width - Px(10), Px(36)), P.Sub);
        y += Px(34);

        int total = y + scroll - vp.Y;
        g.Restore(st);
        if (total != contentH) { contentH = total; Invalidate(); }
        Wheel(vp, delegate (int d) { scroll -= d > 0 ? Px(80) : -Px(80); Invalidate(); });
        VBar(g, new Rectangle(vp.Right - Px(5), vp.Y, Px(4), vp.Height), contentH, view, scroll, delegate (int v) { scroll = v; Invalidate(); });
        if (contentH > view && scroll < contentH - view)
            TxtL(g, L.T("Прокрутіть коліщатком - нижче ще налаштування ↓"), FS, new Rectangle(r.X, vp.Bottom + Px(12), r.Width - Px(350), Px(40)), P.Sub);

        Rectangle ok = new Rectangle(vp.Right - bw, vp.Bottom + Px(12), bw, bh);
        Btn(g, ok, startup ? L.T("Далі") : L.T("Готово"), delegate { Done = true; Close(); }, 1, true);
        Btn(g, new Rectangle(ok.X - Px(150) - Px(10), ok.Y, Px(150), bh), startup ? L.T("Пропустити") : L.T("Скасувати"), delegate { Done = false; Close(); }, 0, true);
    }
}
