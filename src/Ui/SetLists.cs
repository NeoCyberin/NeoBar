using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// Списки без системної смуги прокрутки: SList (заміна ListBox) і список показників оверлея

// ----------------------------------------------------------------------
//  Список рядків без системної смуги прокрутки (заміна ListBox / CheckedListBox):
//  той самий набір властивостей і подій, власне малювання й тонка смуга
// ----------------------------------------------------------------------
sealed class SList : Control
{
    public sealed class ItemList
    {
        readonly SList o;
        readonly List<object> l = new List<object>();
        internal readonly List<bool> chk = new List<bool>();
        internal ItemList(SList owner) { o = owner; }
        public int Count { get { return l.Count; } }
        public object this[int i] { get { return l[i]; } set { l[i] = value; o.Changed(); } }
        public void Add(object x) { l.Add(x); chk.Add(false); o.Changed(); }
        public void Add(object x, bool check) { l.Add(x); chk.Add(check); o.Changed(); }
        public void Clear() { l.Clear(); chk.Clear(); o.Changed(); }
    }

    public readonly ItemList Items;
    public bool CheckBoxes;
    public string Empty = "";
    int ih, sel = -1, hot = -1, top, updating;
    readonly ThinBar bar = new ThinBar();
    public event EventHandler SelectedIndexChanged;
    public event DrawItemEventHandler DrawItem;
    public event EventHandler CheckChanged;

    public SList()
    {
        Items = new ItemList(this);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        ih = SUi.S(28);
        ForeColor = FormTheme.Fg;
    }

    public override Color BackColor { get { return FormTheme.Card; } set { base.BackColor = value; } }

    public int ItemHeight { get { return ih; } set { ih = Math.Max(SUi.S(16), SUi.S(value)); Changed(); } }
    int PadY { get { return SUi.S(4); } }
    int Total { get { return Items.Count * ih + PadY * 2; } }

    internal void Changed()
    {
        if (sel >= Items.Count) sel = Items.Count - 1;
        if (updating > 0) return;
        Sync();
        Invalidate();
    }

    void Sync()
    {
        bar.View = ClientSize.Height;
        bar.Content = Total;
        top = bar.Clamp(top);
        bar.Pos = top;
    }

    public void BeginUpdate() { updating++; }
    public void EndUpdate() { if (updating > 0) updating--; if (updating == 0) Changed(); }

    public int SelectedIndex
    {
        get { return sel; }
        set
        {
            int v = Math.Max(-1, Math.Min(Items.Count - 1, value));
            if (v == sel) return;
            sel = v;
            EnsureVisible(v);
            Invalidate();
            EventHandler h = SelectedIndexChanged; if (h != null) h(this, EventArgs.Empty);
        }
    }

    public object SelectedItem { get { return sel >= 0 && sel < Items.Count ? Items[sel] : null; } }

    public bool IsChecked(int i) { return i >= 0 && i < Items.chk.Count && Items.chk[i]; }
    public void SetChecked(int i, bool v)
    {
        if (i < 0 || i >= Items.chk.Count || Items.chk[i] == v) return;
        Items.chk[i] = v;
        Invalidate();
        EventHandler h = CheckChanged; if (h != null) h(this, EventArgs.Empty);
    }
    public List<int> CheckedIndices { get { List<int> r = new List<int>(); for (int i = 0; i < Items.chk.Count; i++) if (Items.chk[i]) r.Add(i); return r; } }

    public void EnsureVisible(int i)
    {
        if (i < 0) return;
        Sync();
        int y = PadY + i * ih;
        if (y < top) top = y - PadY;
        else if (y + ih > top + ClientSize.Height) top = y + ih - ClientSize.Height + PadY;
        top = bar.Clamp(top);
        bar.Pos = top;
    }

    int BarSpace { get { return bar.Need ? ThinBar.W : 0; } }
    void ScrollTo(int v) { top = v; bar.Pos = top; }

    // прокрутка на dy пікселів (наприклад, коли тягнуть елемент до краю списку)
    public void ScrollBy(int dy)
    {
        if (!bar.Need) return;
        top = bar.Clamp(top + dy);
        bar.Pos = top;
        Invalidate();
    }

    public Rectangle GetItemRectangle(int i) { return new Rectangle(SUi.S(4), PadY + i * ih - top, Math.Max(1, ClientSize.Width - SUi.S(8) - BarSpace), ih); }

    public int IndexFromPoint(Point p)
    {
        if (p.X < 0 || p.Y < 0 || p.X >= ClientSize.Width - BarSpace || p.Y >= ClientSize.Height) return -1;
        int i = (p.Y + top - PadY) / ih;
        if (p.Y + top - PadY < 0) return -1;
        return i >= 0 && i < Items.Count ? i : -1;
    }

    public string GetItemText(object o) { return o == null ? "" : o.ToString(); }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Sync(); }
    protected override bool IsInputKey(Keys k)
    {
        Keys c = k & Keys.KeyCode;
        return c == Keys.Up || c == Keys.Down || c == Keys.Home || c == Keys.End || c == Keys.PageUp || c == Keys.PageDown || base.IsInputKey(k);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int page = Math.Max(1, ClientSize.Height / ih - 1);
        switch (e.KeyCode)
        {
            case Keys.Up: SelectedIndex = Math.Max(0, sel - 1); e.Handled = true; break;
            case Keys.Down: SelectedIndex = Math.Min(Items.Count - 1, sel + 1); e.Handled = true; break;
            case Keys.Home: SelectedIndex = 0; e.Handled = true; break;
            case Keys.End: SelectedIndex = Items.Count - 1; e.Handled = true; break;
            case Keys.PageUp: SelectedIndex = Math.Max(0, sel - page); e.Handled = true; break;
            case Keys.PageDown: SelectedIndex = Math.Min(Items.Count - 1, sel + page); e.Handled = true; break;
            case Keys.Space: if (CheckBoxes && sel >= 0) { SetChecked(sel, !IsChecked(sel)); e.Handled = true; } break;
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (CanFocus) Focus();
        if (bar.MouseDown(this, e, ClientRectangle, ScrollTo)) return;
        int i = IndexFromPoint(e.Location);
        if (i >= 0 && e.Button == MouseButtons.Left)
        {
            SelectedIndex = i;
            if (CheckBoxes) SetChecked(i, !IsChecked(i));
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (bar.MouseMove(this, e, ClientRectangle, ScrollTo)) return;
        int h = IndexFromPoint(e.Location);
        if (h != hot) { hot = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { bar.MouseUp(this); base.OnMouseUp(e); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); bool ch = hot != -1 || bar.Hot; hot = -1; bar.Hot = false; if (ch) Invalidate(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!bar.Need) return;
        top = bar.Clamp(top - e.Delta * ih * 3 / 120);
        bar.Pos = top;
        HandledMouseEventArgs he = e as HandledMouseEventArgs;
        if (he != null) he.Handled = true;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Sync();
        using (SolidBrush b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Items.Count == 0 && Empty.Length > 0)
            TextRenderer.DrawText(g, Empty, Font, ClientRectangle, FormTheme.Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        int first = Math.Max(0, (top - PadY) / ih), last = Math.Min(Items.Count - 1, (top + ClientSize.Height) / ih + 1);
        for (int i = first; i <= last; i++)
        {
            Rectangle r = GetItemRectangle(i);
            DrawItemState st = i == sel ? DrawItemState.Selected : DrawItemState.None;
            if (Focused && i == sel) st |= DrawItemState.Focus;
            DrawItemEventHandler h = DrawItem;
            if (h != null) { try { h(this, new DrawItemEventArgs(g, Font, r, i, st, ForeColor, BackColor)); } catch (Exception ex) { Util.ErrorLog(ex); } continue; }
            int rad = Math.Min(SUi.R(0), r.Height / 2);
            Color fill = i == sel ? Util.Blend(BackColor, FormTheme.Acc, 0.3f) : (i == hot ? Util.Blend(BackColor, FormTheme.Fg, 0.06f) : BackColor);
            if (fill != BackColor) Skin.Fill(g, Rectangle.Inflate(r, 0, -1), rad, fill);
            int tx = r.X + SUi.S(10);
            if (CheckBoxes)
            {
                int d = SUi.S(16);
                Rectangle cr = new Rectangle(r.X + SUi.S(8), r.Y + (r.Height - d) / 2, d, d);
                bool on = IsChecked(i);
                Skin.Fill(g, cr, SUi.S(4), on ? FormTheme.Acc : FormTheme.Input);
                Skin.Edge(g, cr, SUi.S(4), on ? FormTheme.Acc : FormTheme.Line, 1f);
                if (on) G.Draw(g, "check", new RectangleF(cr.X + 1, cr.Y + 1, cr.Width - 2, cr.Height - 2), Util.Readable(Color.White, FormTheme.Acc, 90));
                tx = cr.Right + SUi.S(10);
            }
            SUi.Line1(g, GetItemText(Items[i]), Font, new Rectangle(tx, r.Y, r.Right - tx - SUi.S(6), r.Height), i == sel ? Util.Readable(ForeColor, fill, 100) : ForeColor, TextFormatFlags.Left);
        }
        bar.Paint(g, ClientRectangle);
    }
}

// ----------------------------------------------------------------------
//  Показники ігрового оверлею: перемикач «показувати», назва, позначка «графік»; порядок - перетягуванням
// ----------------------------------------------------------------------
sealed class MetricList : Control
{
    public sealed class Row { public string Key, Text; public bool Show, Graph, CanGraph; }
    public readonly List<Row> Rows = new List<Row>();
    int sel = -1, hot = -1, hotPart, top, dragFrom = -1, dropAt = -1;
    Point downAt;
    bool downOnRow;
    readonly ThinBar bar = new ThinBar();
    public event EventHandler Changed, SelChanged;

    public MetricList()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        ForeColor = FormTheme.Fg;
    }

    public override Color BackColor { get { return FormTheme.Card; } set { base.BackColor = value; } }

    int RH { get { return SUi.S(36); } }

    public void SetRows(List<Row> l)
    {
        Rows.Clear();
        Rows.AddRange(l);
        if (sel >= Rows.Count) sel = -1;
        Sync();
        Invalidate();
    }

    public int Selected
    {
        get { return sel; }
        set { int v = Math.Max(-1, Math.Min(Rows.Count - 1, value)); if (v == sel) return; sel = v; Ensure(v); Invalidate(); EventHandler h = SelChanged; if (h != null) h(this, EventArgs.Empty); }
    }

    void Fire() { Invalidate(); EventHandler h = Changed; if (h != null) h(this, EventArgs.Empty); }

    public void MoveSel(int dir)
    {
        int i = sel, j = sel + dir;
        if (i < 0 || j < 0 || j >= Rows.Count) return;
        Row t = Rows[i]; Rows[i] = Rows[j]; Rows[j] = t;
        sel = j;
        Ensure(j);
        Fire();
    }

    public void SetAll(bool on) { foreach (Row r in Rows) r.Show = on; Fire(); }

    void Sync() { bar.View = ClientSize.Height; bar.Content = Rows.Count * RH + SUi.S(8); top = bar.Clamp(top); bar.Pos = top; }

    void Ensure(int i)
    {
        if (i < 0) return;
        Sync();
        int y = SUi.S(4) + i * RH;
        if (y < top) top = y - SUi.S(4);
        else if (y + RH > top + ClientSize.Height) top = y + RH - ClientSize.Height + SUi.S(4);
        top = bar.Clamp(top); bar.Pos = top;
    }

    int BarSpace { get { return bar.Need ? ThinBar.W : 0; } }
    void ScrollTo(int v) { top = v; bar.Pos = top; }
    Rectangle RowRect(int i) { return new Rectangle(SUi.S(4), SUi.S(4) + i * RH - top, ClientSize.Width - SUi.S(8) - BarSpace, RH); }
    Rectangle ToggleRect(Rectangle r) { int h = SUi.S(18), w = SUi.S(34); return new Rectangle(r.X + SUi.S(34), r.Y + (r.Height - h) / 2, w, h); }
    Rectangle GraphRect(Rectangle r) { int h = SUi.S(24), w = SUi.S(92); return new Rectangle(r.Right - w - SUi.S(6), r.Y + (r.Height - h) / 2, w, h); }

    int RowAt(Point p) { if (p.X >= ClientSize.Width - BarSpace) return -1; int y = p.Y + top - SUi.S(4); if (y < 0) return -1; int i = y / RH; return i < Rows.Count ? i : -1; }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Sync(); }
    protected override bool IsInputKey(Keys k) { Keys c = k & Keys.KeyCode; return c == Keys.Up || c == Keys.Down || base.IsInputKey(k); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool alt = e.Alt || e.Control;
        if (e.KeyCode == Keys.Up) { if (alt) MoveSel(-1); else Selected = Math.Max(0, sel - 1); e.Handled = true; }
        else if (e.KeyCode == Keys.Down) { if (alt) MoveSel(1); else Selected = Math.Min(Rows.Count - 1, sel + 1); e.Handled = true; }
        else if (e.KeyCode == Keys.Space && sel >= 0) { Rows[sel].Show = !Rows[sel].Show; Fire(); e.Handled = true; }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (CanFocus) Focus();
        if (bar.MouseDown(this, e, ClientRectangle, ScrollTo)) return;
        int i = RowAt(e.Location);
        downOnRow = false;
        if (i < 0 || e.Button != MouseButtons.Left) return;
        Rectangle r = RowRect(i);
        Selected = i;
        if (ToggleRect(r).Contains(e.Location)) { Rows[i].Show = !Rows[i].Show; Fire(); return; }
        if (Rows[i].CanGraph && GraphRect(r).Contains(e.Location)) { Rows[i].Graph = !Rows[i].Graph; if (Rows[i].Graph) Rows[i].Show = true; Fire(); return; }
        downOnRow = true; downAt = e.Location; dragFrom = -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (bar.MouseMove(this, e, ClientRectangle, ScrollTo)) return;
        if (downOnRow && e.Button == MouseButtons.Left)
        {
            if (dragFrom < 0 && Math.Abs(e.Y - downAt.Y) > SystemInformation.DragSize.Height) { dragFrom = sel; Capture = true; }
            if (dragFrom >= 0)
            {
                int y = e.Y + top - SUi.S(4);
                int at = Math.Max(0, Math.Min(Rows.Count, (y + RH / 2) / RH));
                if (at != dropAt) { dropAt = at; Invalidate(); }
                Cursor = Cursors.SizeNS;
                return;
            }
        }
        int h = RowAt(e.Location), part = 0;
        if (h >= 0) { Rectangle r = RowRect(h); part = ToggleRect(r).Contains(e.Location) ? 1 : (Rows[h].CanGraph && GraphRect(r).Contains(e.Location) ? 2 : 0); }
        if (h != hot || part != hotPart) { hot = h; hotPart = part; Invalidate(); }
        Cursor = part > 0 ? Cursors.Hand : (h >= 0 ? Cursors.SizeNS : Cursors.Default);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        bar.MouseUp(this);
        if (dragFrom >= 0 && dropAt >= 0)
        {
            Row r = Rows[dragFrom];
            int at = dropAt > dragFrom ? dropAt - 1 : dropAt;
            Rows.RemoveAt(dragFrom);
            Rows.Insert(Math.Max(0, Math.Min(Rows.Count, at)), r);
            sel = Rows.IndexOf(r);
            Fire();
        }
        dragFrom = -1; dropAt = -1; downOnRow = false; Capture = false;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; bar.Hot = false; Invalidate(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!bar.Need) return;
        top = bar.Clamp(top - e.Delta * RH * 2 / 120); bar.Pos = top;
        HandledMouseEventArgs he = e as HandledMouseEventArgs; if (he != null) he.Handled = true;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Sync();
        using (SolidBrush b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color bg = BackColor;
        for (int i = 0; i < Rows.Count; i++)
        {
            Rectangle r = RowRect(i);
            if (r.Bottom < 0 || r.Top > ClientSize.Height) continue;
            Row m = Rows[i];
            int rad = Math.Min(SUi.R(1), r.Height / 2);
            bool on = i == sel;
            if (on || i == hot) Skin.Fill(g, Rectangle.Inflate(r, 0, -2), rad, on ? Util.Blend(bg, FormTheme.Acc, 0.2f) : Util.Blend(bg, FormTheme.Fg, 0.05f));
            G.Draw(g, "grip", new RectangleF(r.X + SUi.S(8), r.Y + (r.Height - SUi.S(16)) / 2f, SUi.S(16), SUi.S(16)), Util.Blend(bg, FormTheme.Fg, 0.35f));
            // перемикач «показувати»
            Rectangle tr = ToggleRect(r);
            Color track = m.Show ? FormTheme.Acc : Util.Blend(bg, FormTheme.Fg, i == hot && hotPart == 1 ? 0.32f : 0.22f);
            Skin.Fill(g, tr, Skin.Pill(tr.Height), track);
            int k = tr.Height - SUi.S(6);
            using (SolidBrush b = new SolidBrush(m.Show ? Util.Readable(Color.White, track, 60) : Util.Blend(bg, FormTheme.Fg, 0.75f)))
                g.FillEllipse(b, m.Show ? tr.Right - k - SUi.S(3) : tr.X + SUi.S(3), tr.Y + SUi.S(3), k, k);
            int tx = tr.Right + SUi.S(12);
            Rectangle gr = GraphRect(r);
            SUi.Line1(g, m.Text, Font, new Rectangle(tx, r.Y, gr.X - tx - SUi.S(6), r.Height), m.Show ? FormTheme.Fg : FormTheme.Sub, TextFormatFlags.Left);
            if (m.CanGraph)
            {
                bool gh = i == hot && hotPart == 2;
                Color gf = m.Graph ? Util.Blend(bg, FormTheme.Acc, 0.35f) : (gh ? Util.Blend(bg, FormTheme.Fg, 0.08f) : bg);
                int grd = Skin.Pill(gr.Height);
                Skin.Fill(g, gr, grd, gf);
                Skin.Edge(g, gr, grd, m.Graph ? FormTheme.Acc : Util.Blend(bg, FormTheme.Fg, gh ? 0.3f : 0.16f), 1f);
                Color gc = m.Graph ? Util.Readable(FormTheme.Fg, gf, 100) : FormTheme.Sub;
                int gs = SUi.S(14);
                G.Draw(g, "chart", new RectangleF(gr.X + SUi.S(10), gr.Y + (gr.Height - gs) / 2f, gs, gs), gc);
                SUi.Line1(g, L.T("Графік"), SUi.Small, new Rectangle(gr.X + SUi.S(28), gr.Y, gr.Width - SUi.S(32), gr.Height), gc, TextFormatFlags.Left);
            }
        }
        if (dragFrom >= 0 && dropAt >= 0)
        {
            int y = SUi.S(4) + dropAt * RH - top;
            using (Pen p = new Pen(FormTheme.Acc, Math.Max(2f, SUi.S(2)))) g.DrawLine(p, SUi.S(10), y, ClientSize.Width - SUi.S(10) - BarSpace, y);
        }
        bar.Paint(g, ClientRectangle);
    }
}
