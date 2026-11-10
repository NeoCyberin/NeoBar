using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

// ======================================================================
//  Вибір шрифту Windows у стилі теми (замість системного FontDialog): пошук за назвою, кожен
//  шрифт показано ним самим (шрифти створюються лише для видимих рядків), розмір - кнопками.
// ======================================================================
sealed class FontPick : SbWindow
{
    readonly List<string> all = new List<string>();
    List<string> shown = new List<string>();
    readonly Dictionary<string, Font> faces = new Dictionary<string, Font>();
    readonly TextBox search;
    string sel;
    float size;
    int scroll;
    bool ok;

    FontPick(NeoBar bar, string face, float sz) : base(bar, L.T("Шрифт"), 460, 560)
    {
        Glyph = "";
        ShowInTaskbar = false;
        TopMost = true;
        sel = face;
        size = Math.Max(6f, Math.Min(72f, sz));
        using (InstalledFontCollection fc = new InstalledFontCollection())
            foreach (FontFamily f in fc.Families) { if (f.IsStyleAvailable(FontStyle.Regular)) all.Add(f.Name); f.Dispose(); }
        shown = new List<string>(all);
        search = MakeInput();
        search.TextChanged += delegate { Filter(); };
        search.KeyDown += delegate (object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && shown.Count > 0) { e.SuppressKeyPress = true; sel = shown[0]; Done(); }
        };
        int i = shown.IndexOf(sel);
        if (i > 0) scroll = i * Px(34);
    }

    void Filter()
    {
        string q = search.Text.Trim();
        shown = q.Length == 0 ? new List<string>(all) : all.FindAll(delegate (string n) { return n.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0; });
        scroll = 0;
        Invalidate();
    }

    Font FaceOf(string name)
    {
        Font f;
        if (faces.TryGetValue(name, out f)) return f;
        try { f = new Font(name, F.SizeInPoints + 2f, FontStyle.Regular); } catch { f = null; }
        faces[name] = f;
        return f;
    }

    void Done() { ok = true; Close(); }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        PlaceInput(g, search, new Rectangle(r.X, r.Y, r.Width, Px(34)));
        if (search.Text.Length == 0) TxtL(g, L.T("Пошук шрифту"), F, new Rectangle(r.X + Px(12), r.Y, r.Width, Px(34)), P.Sub);
        int rowH = Px(34);
        Rectangle list = new Rectangle(r.X, r.Y + Px(44), r.Width, r.Height - Px(44) - Px(96));
        int total = shown.Count * rowH;
        scroll = Math.Max(0, Math.Min(Math.Max(0, total - list.Height), scroll));
        GraphicsState st = g.Save();
        g.SetClip(list);
        int first = scroll / rowH, last = Math.Min(shown.Count - 1, (scroll + list.Height) / rowH);
        for (int i = first; i <= last; i++)
        {
            string n = shown[i];
            Rectangle rr = new Rectangle(list.X, list.Y + i * rowH - scroll, list.Width - Px(10), rowH - Px(2));
            bool on = string.Equals(n, sel, StringComparison.OrdinalIgnoreCase);
            if (on || Over(rr)) Card(g, rr, on ? Util.Blend(P.Card, P.Acc, 0.35f) : P.Card);
            Font f = FaceOf(n) ?? F;
            TxtL(g, n, f, new Rectangle(rr.X + Px(10), rr.Y, rr.Width - Px(20), rr.Height), P.Fg);
            string nn = n;
            if (Rectangle.Intersect(rr, list) == rr) Hot(rr, delegate { sel = nn; Invalidate(); });
        }
        g.Restore(st);
        Wheel(list, delegate (int d) { scroll -= d > 0 ? rowH * 3 : -rowH * 3; Invalidate(); });
        VBar(g, new Rectangle(list.Right - Px(6), list.Y + Px(2), Px(4), list.Height - Px(4)), total, list.Height, scroll, delegate (int v) { scroll = v; Invalidate(); });

        // розмір і зразок
        int y = list.Bottom + Px(10);
        TxtL(g, L.T("Розмір"), F, new Rectangle(r.X, y, Px(80), Px(30)), P.Sub);
        IconBtn(g, new Rectangle(r.X + Px(80), y, Px(30), Px(30)), "minus", delegate { size = Math.Max(6f, size - 1f); Invalidate(); }, false);
        TxtC(g, size.ToString("0.#"), FB, new Rectangle(r.X + Px(112), y, Px(50), Px(30)), P.Fg);
        IconBtn(g, new Rectangle(r.X + Px(164), y, Px(30), Px(30)), "plus", delegate { size = Math.Min(72f, size + 1f); Invalidate(); }, false);
        Font pf = sel != null ? FaceOf(sel) : null;
        if (pf != null) TxtL(g, L.T("Зразок тексту"), pf, new Rectangle(r.X + Px(210), y, r.Width - Px(210), Px(30)), P.Fg);

        Btn(g, new Rectangle(r.Right - Px(250), r.Bottom - Px(36), Px(120), Px(36)), L.T("Скасувати"), Close, 0, true);
        Btn(g, new Rectangle(r.Right - Px(120), r.Bottom - Px(36), Px(120), Px(36)), L.T("Вибрати"), Done, 1, sel != null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        foreach (Font f in faces.Values) if (f != null) f.Dispose();
        faces.Clear();
        base.OnFormClosed(e);
    }

    // true - вибрали (face, size)
    public static bool Ask(IWin32Window owner, ref string face, ref float size)
    {
        NeoBar b = ClockData.Bar;
        if (b == null || b.IsDisposed) return false;
        using (FontPick d = new FontPick(b, face, size))
        {
            Form of = owner as Form;
            Rectangle wa = of != null ? Screen.FromControl(of).WorkingArea : Screen.PrimaryScreen.WorkingArea;
            Rectangle ob = of != null && of.Visible ? of.Bounds : wa;
            d.StartPosition = FormStartPosition.Manual;
            d.Location = new Point(Math.Max(wa.Left, ob.Left + (ob.Width - d.Width) / 2), Math.Max(wa.Top, ob.Top + (ob.Height - d.Height) / 2));
            d.ShowDialog(owner);
            if (!d.ok || d.sel == null) return false;
            face = d.sel;
            size = d.size;
            return true;
        }
    }
}
