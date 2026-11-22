using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

// ======================================================================
//  Нотатки: вікно зі списком (як в інших віджетів) замість меню.
//  Вкладки: на столі, приховані (не показуються на робочому столі), кошик (відновлення протягом 30 днів)
// ======================================================================
sealed class NotesWindow : SbWindow
{
    int scroll;
    const int TabDesk = 0, TabHidden = 1, TabTrash = 2;

    public NotesWindow(NeoBar bar) : base(bar, L.T("Нотатки"), 460, 560)
    { Glyph = "";
        Notes.Changed += OnData;
        SetTabs();
    }

    void SetTabs()
    {
        Tabs = new string[]
        {
            L.T("На столі") + (Notes.Count > 0 ? " · " + Notes.Count : ""),
            L.T("Приховані") + (Notes.HiddenCount > 0 ? " · " + Notes.HiddenCount : ""),
            L.T("Кошик") + (Notes.TrashCount > 0 ? " · " + Notes.TrashCount : "")
        };
    }

    void OnData() { try { if (!IsDisposed) BeginInvoke(new Action(delegate { SetTabs(); Invalidate(); })); } catch (Exception skipEx) { SbLog.Skip(skipEx); } }

    protected override void OnTab() { scroll = 0; }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Notes.Changed -= OnData;
        base.OnFormClosed(e);
    }

    protected override void HeadButtons(Graphics g, ref int right)
    {
    }

    List<Note> ShownList()
    {
        List<Note> l = new List<Note>();
        foreach (Note n in Notes.All.ToArray())
            if (Tab == TabDesk ? n.OnDesk : (Tab == TabHidden ? n.Hidden && !n.InTrash : n.InTrash)) l.Add(n);
        if (Tab == TabTrash) l.Sort(delegate (Note a, Note b) { return b.Trashed.CompareTo(a.Trashed); });
        return l;
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        List<Note> list = ShownList();
        // верхній ряд кнопок - свій для кожної вкладки
        if (Tab == TabDesk)
        {
            Btn(g, new Rectangle(r.X, r.Y, Px(170), Px(34)), L.T("+ Нова нотатка"), delegate { Notes.New(); }, 1, true);
            int bw = (r.Width - Px(170) - Px(16)) / 2;
            Btn(g, new Rectangle(r.X + Px(178), r.Y, bw, Px(34)), L.T("Закріпити всі"), delegate { Notes.LockAll(); }, 0, Notes.LockedCount < Notes.Count);
            Btn(g, new Rectangle(r.X + Px(186) + bw, r.Y, bw, Px(34)), L.T("Редагувати всі"), delegate { Notes.UnlockAll(); }, 0, Notes.LockedCount > 0);
        }
        else if (Tab == TabHidden)
            TxtW(g, L.T("Приховані нотатки не показуються на робочому столі, але зберігаються тут. «Показати» повертає нотатку на стіл."), FS, new Rectangle(r.X, r.Y, r.Width, Px(36)), P.Sub);
        else
        {
            TxtW(g, L.F("Нотатки в кошику зникають назовсім через {0} днів.", Notes.TrashDays), FS, new Rectangle(r.X, r.Y, r.Width - Px(180), Px(36)), P.Sub);
            Btn(g, new Rectangle(r.Right - Px(170), r.Y, Px(170), Px(34)), L.T("Очистити кошик"), delegate
            {
                if (Ask("NeoBar", L.F("Видалити назовсім нотатки з кошика ({0})?", Notes.TrashCount), L.T("Видалити"), L.T("Скасувати")) == 0) Notes.EmptyTrash();
            }, 0, Notes.TrashCount > 0);
        }
        int y = r.Y + Px(46);
        Rectangle lr = new Rectangle(r.X, y, r.Width, r.Bottom - y - Px(24));
        if (list.Count == 0)
        {
            string empty = Tab == TabDesk ? L.T("Нотаток ще немає") : (Tab == TabHidden ? L.T("Прихованих нотаток немає") : L.T("Кошик порожній"));
            TxtC(g, empty, F, new Rectangle(lr.X, lr.Y + Px(40), lr.Width, Px(30)), P.Sub);
        }
        int rowH = Px(56);
        int vis = Math.Max(1, lr.Height / rowH);
        scroll = Math.Max(0, Math.Min(scroll, list.Count - vis));
        for (int idx = scroll; idx < list.Count; idx++)
        {
            if (y + rowH > lr.Bottom) break;
            Note n = list[idx];
            Rectangle cr = new Rectangle(r.X, y, r.Width, rowH - Px(8));
            Card(g, cr, Over(cr) ? Util.Blend(P.Card, P.Fg, 0.05f) : P.Card);
            NoteTheme t = Notes.ThemeOf(n);
            Rectangle sw = new Rectangle(cr.X + Px(12), cr.Y + (cr.Height - Px(22)) / 2, Px(22), Px(22));
            using (SolidBrush b = new SolidBrush(Util.Opaque(t.Body))) Util.FillRound(g, b, sw, Px(5));
            using (Pen pn = new Pen(Util.Opaque(t.Border), 1.5f)) g.DrawRectangle(pn, sw);
            int textW = cr.Width - Px(200);
            TxtL(g, n.Label, FB, new Rectangle(sw.Right + Px(10), cr.Y + Px(4), textW, Px(22)), P.Fg);
            string sub;
            if (Tab == TabDesk) sub = (n.Locked ? L.T("на шпалерах") : L.T("редагується")) + " · " + t.Title;
            else if (Tab == TabHidden) sub = L.T("прихована") + " · " + t.Title;
            else
            {
                int left = Math.Max(0, Notes.TrashDays - (int)(DateTime.Now - n.Trashed).TotalDays);
                sub = L.F("у кошику з {0} · зникне через {1} дн.", n.Trashed.ToString("d"), left);
            }
            TxtL(g, sub, FS, new Rectangle(sw.Right + Px(10), cr.Y + Px(24), textW, Px(18)), P.Sub);
            int bx = cr.Right - Px(40), by = cr.Y + (cr.Height - Px(30)) / 2;
            if (Tab == TabDesk)
            {
                IconBtn(g, new Rectangle(bx, by, Px(30), Px(30)), "trash", delegate { Notes.Trash(n); }, false);
                IconBtn(g, new Rectangle(bx - Px(36), by, Px(30), Px(30)), "eyeoff", delegate { Notes.SetHidden(n, true); }, false);
                Switch(g, new Rectangle(bx - Px(136), cr.Y + (cr.Height - Px(26)) / 2, Px(92), Px(26)), L.T("Закріп."), n.Locked, delegate { Notes.SetLocked(n, !n.Locked); });
                Hot(new Rectangle(cr.X, cr.Y, cr.Width - Px(180), cr.Height), delegate { Notes.Reveal(n); });
            }
            else if (Tab == TabHidden)
            {
                IconBtn(g, new Rectangle(bx, by, Px(30), Px(30)), "trash", delegate { Notes.Trash(n); }, false);
                Btn(g, new Rectangle(bx - Px(116), by, Px(108), Px(30)), L.T("Показати"), delegate { Notes.SetHidden(n, false); }, 0, true);
            }
            else
            {
                IconBtn(g, new Rectangle(bx, by, Px(30), Px(30)), "close", delegate
                {
                    if (Ask("NeoBar", L.T("Видалити нотатку «") + n.Label + L.T("» назовсім? Відновити її вже не вийде."), L.T("Видалити"), L.T("Скасувати")) == 0) Notes.Delete(n);
                }, false);
                Btn(g, new Rectangle(bx - Px(116), by, Px(108), Px(30)), L.T("Відновити"), delegate { Notes.Restore(n); }, 0, true);
            }
            y += rowH;
        }
        Wheel(lr, delegate (int d) { scroll -= d; });
        string hint = Tab == TabDesk ? L.T("Клік по нотатці - показати її; перемикач - закріпити на шпалерах; око - приховати; кошик - прибрати.")
            : (Tab == TabHidden ? L.T("Прихована нотатка нікуди не зникає - вона просто не займає місце на робочому столі.") : L.T("«Відновити» повертає нотатку туди, де вона була."));
        TxtL(g, hint, FS, new Rectangle(r.X, r.Bottom - Px(20), r.Width, Px(20)), P.Sub);
    }
}
