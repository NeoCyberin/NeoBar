using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

// ======================================================================
//  Нотатки: вікно зі списком (як в інших віджетів) замість меню
// ======================================================================
sealed class NotesWindow : SbWindow
{
    int scroll;

    public NotesWindow(NeoBar bar) : base(bar, L.T("Нотатки"), 460, 560)
    { Glyph = "\uE70B";
        Notes.Changed += OnData;
    }

    void OnData() { try { if (!IsDisposed) BeginInvoke(new Action(Invalidate)); } catch (Exception skipEx) { SbLog.Skip(skipEx); } }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Notes.Changed -= OnData;
        base.OnFormClosed(e);
    }

    protected override void HeadButtons(Graphics g, ref int right)
    {
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        Btn(g, new Rectangle(r.X, r.Y, Px(170), Px(34)), L.T("+ Нова нотатка"), delegate { Notes.New(); }, 1, true);
        int bw = (r.Width - Px(170) - Px(16)) / 2;
        Btn(g, new Rectangle(r.X + Px(178), r.Y, bw, Px(34)), L.T("Закріпити всі"), delegate { Notes.LockAll(); }, 0, Notes.LockedCount < Notes.Count);
        Btn(g, new Rectangle(r.X + Px(186) + bw, r.Y, bw, Px(34)), L.T("Редагувати всі"), delegate { Notes.UnlockAll(); }, 0, Notes.LockedCount > 0);
        int y = r.Y + Px(46);
        Rectangle lr = new Rectangle(r.X, y, r.Width, r.Bottom - y - Px(24));
        if (Notes.Count == 0)
        {
            TxtC(g, L.T("Нотаток ще немає"), F, new Rectangle(lr.X, lr.Y + Px(40), lr.Width, Px(30)), P.Sub);
        }
        int rowH = Px(56);
        int vis = Math.Max(1, lr.Height / rowH);
        scroll = Math.Max(0, Math.Min(scroll, Notes.Count - vis));
        int idx = 0;
        foreach (Note n0 in Notes.All.ToArray())
        {
            if (idx++ < scroll) continue;
            if (y + rowH > lr.Bottom) break;
            Note n = n0;
            Rectangle cr = new Rectangle(r.X, y, r.Width, rowH - Px(8));
            Card(g, cr, Over(cr) ? Util.Blend(P.Card, P.Fg, 0.05f) : P.Card);
            NoteTheme t = Notes.ThemeOf(n);
            Rectangle sw = new Rectangle(cr.X + Px(12), cr.Y + (cr.Height - Px(22)) / 2, Px(22), Px(22));
            using (SolidBrush b = new SolidBrush(Util.Opaque(t.Body))) Util.FillRound(g, b, sw, Px(5));
            using (Pen pn = new Pen(Util.Opaque(t.Border), 1.5f)) g.DrawRectangle(pn, sw);
            TxtL(g, n.Label, FB, new Rectangle(sw.Right + Px(10), cr.Y + Px(4), cr.Width - Px(190), Px(22)), P.Fg);
            TxtL(g, (n.Locked ? L.T("на шпалерах") : L.T("редагується")) + " · " + t.Title, FS, new Rectangle(sw.Right + Px(10), cr.Y + Px(24), cr.Width - Px(190), Px(18)), P.Sub);
            Switch(g, new Rectangle(cr.Right - Px(140), cr.Y + (cr.Height - Px(26)) / 2, Px(92), Px(26)), L.T("Закріп."), n.Locked, delegate { Notes.SetLocked(n, !n.Locked); });
            IconBtn(g, new Rectangle(cr.Right - Px(40), cr.Y + (cr.Height - Px(30)) / 2, Px(30), Px(30)), "trash", delegate
            {
                if (Ask("NeoBar", L.T("Видалити нотатку «") + n.Label + L.Q("»?"), L.T("Видалити"), L.T("Скасувати")) == 0) Notes.Delete(n);
            }, false);
            Hot(new Rectangle(cr.X, cr.Y, cr.Width - Px(150), cr.Height), delegate { Notes.Reveal(n); });
            y += rowH;
        }
        Wheel(lr, delegate (int d) { scroll -= d; });
        TxtL(g, L.T("Клік по нотатці - показати її; перемикач - закріпити на шпалерах або редагувати."), FS, new Rectangle(r.X, r.Bottom - Px(20), r.Width, Px(20)), P.Sub);
    }
}
