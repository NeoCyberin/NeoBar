using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// Навігація вікна налаштувань: бічні розділи, вкладки-сегменти, заголовок розділу

// ----------------------------------------------------------------------
//  Спільна основа перемикачів розділів (бічна навігація, сегментовані вкладки):
//  вибір, наведення, клавіші й «серфінг» підсвітки. Нащадок задає розміщення пунктів і малювання.
// ----------------------------------------------------------------------
abstract class SurfPicker : Control
{
    protected int sel = -1, hot = -1;
    protected readonly Surf surf = new Surf();
    readonly bool vertical;
    public event EventHandler SelectedChanged;

    protected SurfPicker(bool vertical)
    {
        this.vertical = vertical;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        surf.Tick += delegate { Invalidate(); };
    }

    protected abstract int ItemCount { get; }
    public abstract Rectangle ItemRect(int i);

    // підсвітка тягнеться вздовж осі перемикача
    void Glide(bool animate)
    {
        Rectangle r = ItemRect(sel);
        if (vertical) surf.Go(r.Top, r.Bottom, animate); else surf.Go(r.Left, r.Right, animate);
    }

    public int SelectedIndex
    {
        get { return sel; }
        set
        {
            int v = Math.Max(0, Math.Min(ItemCount - 1, value));
            if (v == sel) return;
            bool first = sel < 0;
            sel = v;
            Glide(!first && IsHandleCreated && Visible);
            Invalidate();
            EventHandler h = SelectedChanged; if (h != null) h(this, EventArgs.Empty);
        }
    }

    int HitTest(Point p)
    {
        for (int i = 0; i < ItemCount; i++) if (ItemRect(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (sel >= 0) Glide(false);
    }

    protected override void Dispose(bool disposing) { if (disposing) surf.Dispose(); base.Dispose(disposing); }

    protected override bool IsInputKey(Keys k)
    {
        return (vertical ? k == Keys.Up || k == Keys.Down : k == Keys.Left || k == Keys.Right) || base.IsInputKey(k);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == (vertical ? Keys.Up : Keys.Left)) SelectedIndex = sel - 1;
        else if (e.KeyCode == (vertical ? Keys.Down : Keys.Right)) SelectedIndex = sel + 1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = HitTest(e.Location);
        if (h != hot) { hot = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hot != -1) { hot = -1; Invalidate(); } }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        int i = HitTest(e.Location);
        if (i >= 0) SelectedIndex = i;
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}

// ----------------------------------------------------------------------
//  Бічна навігація: профіль угорі, розділи з піктограмами; підсвітка «серфить» між розділами
// ----------------------------------------------------------------------
sealed class NavRail : SurfPicker
{
    public sealed class Item { public string Key, Text; }
    public readonly List<Item> Items = new List<Item>();
    public Profile Who;

    public NavRail() : base(true) { }

    public void Add(string key, string text) { Item it = new Item(); it.Key = key; it.Text = text; Items.Add(it); Invalidate(); }

    int Top0 { get { return SUi.S(88); } }
    int IH { get { return SUi.S(40); } }

    protected override int ItemCount { get { return Items.Count; } }
    public override Rectangle ItemRect(int i) { return new Rectangle(SUi.S(12), Top0 + i * (IH + SUi.S(3)), Width - SUi.S(24), IH); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color bg = BackColor;
        if (!Festive.Backdrop(g, this, ClientRectangle)) Ctl.Floor(g, bg, ClientRectangle);
        bool grad = Festive.Has(FormTheme.T) && !FormTheme.Sketchy;

        // профіль
        int av = SUi.S(40);
        Rectangle ar = new Rectangle(SUi.S(20), SUi.S(22), av, av);
        ProfileArt.Avatar(g, ar, Who, false, FormTheme.Acc);
        int tx = ar.Right + SUi.S(12), tw = Width - tx - SUi.S(12);
        SUi.Line1(g, Who != null ? Who.Name : "NeoBar", SUi.Head, new Rectangle(tx, ar.Y, tw, av / 2 + SUi.S(2)), FormTheme.Fg, TextFormatFlags.Left | TextFormatFlags.Bottom);
        SUi.Line1(g, L.T("Налаштування"), SUi.Small, new Rectangle(tx, ar.Y + av / 2 + SUi.S(2), tw, av / 2), FormTheme.Sub, TextFormatFlags.Left | TextFormatFlags.Top);

        int rad = Math.Min(SUi.R(2), IH / 2);
        Color hov = Util.Blend(bg, FormTheme.Fg, 0.05f);
        Color pill = Util.Blend(bg, FormTheme.Fg, FormTheme.Dark ? 0.09f : 0.07f);
        if (FormTheme.Neon) pill = Util.Blend(bg, FormTheme.Acc, 0.16f);
        if (FormTheme.Plate) pill = Util.Blend(bg, FormTheme.Acc, 0.26f);
        if (grad)
        {
            // напівпрозора підсвітка поверх градієнта: світла на темних темах, темна на світлих
            Color k = FormTheme.Dark ? Color.White : FormTheme.Fg;
            hov = Color.FromArgb(FormTheme.Dark ? 34 : 16, k);
            pill = Color.FromArgb(FormTheme.Dark ? 64 : 30, k);
        }

        if (hot >= 0 && hot != sel && !surf.Running) Skin.Fill(g, ItemRect(hot), rad, hov);
        if (sel >= 0)
        {
            Rectangle r0 = ItemRect(sel);
            Rectangle pr = new Rectangle(r0.X, (int)Math.Round(surf.A), r0.Width, Math.Max(SUi.S(8), (int)Math.Round(surf.B - surf.A)));
            if (FormTheme.Plate)
            {
                using (GraphicsPath gp = Skin.Shape(pr, rad))
                using (LinearGradientBrush lg = new LinearGradientBrush(new Rectangle(pr.X, pr.Y - 1, pr.Width, pr.Height + 2), Util.Blend(pill, Color.White, 0.1f), Util.Blend(pill, Color.Black, 0.14f), 90f))
                    g.FillPath(lg, gp);
                using (SolidBrush rv = new SolidBrush(Util.Blend(FormTheme.Acc, Color.White, 0.3f)))
                {
                    int d = SUi.S(4);
                    g.FillEllipse(rv, pr.Right - SUi.S(10), pr.Y + SUi.S(6), d, d);
                    g.FillEllipse(rv, pr.Right - SUi.S(10), pr.Bottom - SUi.S(10), d, d);
                }
            }
            else Skin.Fill(g, pr, rad, pill);
            if (FormTheme.Neon) Skin.Edge(g, pr, rad, Color.FromArgb(160, FormTheme.Acc), 1f);
            // мітка акценту ліворуч (тягнеться разом із підсвіткою)
            int ih = Math.Max(SUi.S(6), pr.Height - SUi.S(24));
            Rectangle ind = new Rectangle(pr.X + SUi.S(1), pr.Y + (pr.Height - ih) / 2, SUi.S(3), ih);
            using (SolidBrush b = new SolidBrush(FormTheme.Acc)) Util.FillRound(g, b, ind, ind.Width / 2);
        }
        for (int i = 0; i < Items.Count; i++)
        {
            Rectangle r = ItemRect(i);
            bool on = i == sel;
            Color c = on ? FormTheme.Fg : Util.Blend(FormTheme.Sub, FormTheme.Fg, i == hot ? 0.7f : 0.35f);
            int gs = SUi.S(18);
            G.Draw(g, Items[i].Key, new RectangleF(r.X + SUi.S(16), r.Y + (r.Height - gs) / 2f, gs, gs), on ? Util.Readable(FormTheme.Acc, pill, 60) : c);
            SUi.Line1(g, Items[i].Text, SUi.Nav, new Rectangle(r.X + SUi.S(46), r.Y, r.Width - SUi.S(52), r.Height), c, TextFormatFlags.Left);
        }
        // святкові теми: прикраси на вільному місці під розділами
        if (Items.Count > 0 && FormTheme.T != null && Festive.HasDecor(FormTheme.T.Id) && !FormTheme.Sketchy)
        {
            int fy = ItemRect(Items.Count - 1).Bottom + SUi.S(14);
            if (Height - fy > SUi.S(70))
            {
                GraphicsState ds = g.Save();
                Festive.Decor(g, FormTheme.T, new Rectangle(0, fy, Width, Height - fy), SUi.S(100) / 100f, true);
                g.Restore(ds);
            }
        }
        if (Focused && sel >= 0 && ShowFocusCues)
            using (Pen p = new Pen(Color.FromArgb(120, FormTheme.Acc), 1f))
            using (GraphicsPath fp = Skin.Shape(Rectangle.Inflate(ItemRect(sel), -1, -1), rad)) g.DrawPath(p, fp);
    }
}

// ----------------------------------------------------------------------
//  Сегментовані вкладки (Загальне / Показники / Ігри): підсвітка теж «серфить»
// ----------------------------------------------------------------------
sealed class SegTabs : SurfPicker
{
    public readonly List<string> Items = new List<string>();

    public SegTabs() : base(false)
    {
        Height = SUi.S(38);
    }

    public int PreferredW
    {
        get
        {
            int w = SUi.S(8);
            foreach (string s in Items) w += TextRenderer.MeasureText(s, SUi.Base).Width + SUi.S(36);
            return w;
        }
    }

    protected override int ItemCount { get { return Items.Count; } }

    public override Rectangle ItemRect(int i)
    {
        int x = SUi.S(4);
        for (int k = 0; k < Items.Count; k++)
        {
            int w = TextRenderer.MeasureText(Items[k], SUi.Base).Width + SUi.S(36);
            if (k == i) return new Rectangle(x, SUi.S(4), w, Height - SUi.S(8));
            x += w;
        }
        return Rectangle.Empty;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Ctl.FloorOn(g, this, SUi.BgOf(this), ClientRectangle);
        Rectangle track = new Rectangle(0, 0, Width - 1, Height - 1);
        int rad = Math.Min(SUi.R(3), track.Height / 2);
        Skin.Fill(g, track, rad, FormTheme.Card);
        Skin.Edge(g, track, rad, Util.Blend(FormTheme.Card, FormTheme.Fg, 0.08f), 1f);
        Color fill = Util.Blend(FormTheme.Bg, FormTheme.Acc, FormTheme.Dark ? 0.55f : 0.75f);
        if (sel >= 0)
        {
            Rectangle r0 = ItemRect(sel);
            Rectangle pr = new Rectangle((int)Math.Round(surf.A), r0.Y, Math.Max(SUi.S(8), (int)Math.Round(surf.B - surf.A)), r0.Height);
            int pr2 = Math.Min(SUi.R(1), pr.Height / 2);
            if (FormTheme.Plate)
            {
                using (GraphicsPath gp = Skin.Shape(pr, pr2))
                using (LinearGradientBrush lg = new LinearGradientBrush(new Rectangle(pr.X, pr.Y - 1, pr.Width, pr.Height + 2), Util.Blend(fill, Color.White, 0.14f), Util.Blend(fill, Color.Black, 0.16f), 90f))
                    g.FillPath(lg, gp);
            }
            else Skin.Fill(g, pr, pr2, fill);
            if (FormTheme.Neon) Skin.Edge(g, pr, pr2, FormTheme.Acc, 1f);
        }
        for (int i = 0; i < Items.Count; i++)
        {
            Rectangle r = ItemRect(i);
            bool on = i == sel;
            if (i == hot && !on && !surf.Running) Skin.Fill(g, r, Math.Min(SUi.R(1), r.Height / 2), Util.Blend(FormTheme.Card, FormTheme.Fg, 0.06f));
            // текст вибраної вкладки - контрастний до підсвітки; поки підсвітка пливе, колір перетікає
            float k = 0f;
            if (sel >= 0) { float ov = Math.Max(0f, Math.Min(r.Right, surf.B) - Math.Max(r.Left, surf.A)); k = Math.Max(0f, Math.Min(1f, ov / Math.Max(1f, r.Width))); }
            Color c = Util.Blend(i == hot ? FormTheme.Fg : FormTheme.Sub, Util.Readable(FormTheme.Fg, fill, 110), k);
            SUi.Line1(g, Items[i], on ? SUi.Semi : SUi.Base, r, c, TextFormatFlags.HorizontalCenter);
        }
    }
}

// ----------------------------------------------------------------------
//  Заголовок розділу над сторінкою: велика назва й пояснення
// ----------------------------------------------------------------------
sealed class HeadBar : Control, IGradHost
{
    string title = "", sub = "";
    public string Title { get { return title; } set { title = value ?? ""; Invalidate(); } }
    public string Sub { get { return sub; } set { sub = value ?? ""; Invalidate(); } }

    public HeadBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        if (!Festive.Backdrop(g, this, ClientRectangle)) Ctl.Floor(g, FormTheme.Bg, ClientRectangle);
        int th = SUi.Page.Height + SUi.S(6);
        SUi.Line1(g, title, SUi.Page, new Rectangle(0, SUi.S(4), Width, th), FormTheme.Fg, TextFormatFlags.Left);
        SUi.Line1(g, sub, SUi.Base, new Rectangle(0, SUi.S(4) + th, Width, SUi.S(22)), FormTheme.Sub, TextFormatFlags.Left);
    }
}
