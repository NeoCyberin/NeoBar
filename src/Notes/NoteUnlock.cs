using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// ======================================================================
//  Закріплена нотатка пропускає кліки крізь себе, тому розблокування - окремим замочком:
//  коли миша над нотаткою (і там видно робочий стіл), у її куті з'являється кружечок із замком.
// ======================================================================
static class NoteUnlock
{
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);

    static Timer timer;
    static Badge badge;

    public static void Ensure()
    {
        if (timer != null) return;
        timer = new Timer();
        timer.Interval = 150;
        timer.Tick += delegate { try { Tick(); } catch (Exception skipEx) { SbLog.Skip(skipEx); } };
        timer.Start();
    }

    // під курсором - робочий стіл (а не вікно програми, що перекриває нотатку)
    static bool DesktopAt(Point p)
    {
        IntPtr h = WindowFromPoint(p);
        if (h == IntPtr.Zero) return false;
        IntPtr root = Win.GetAncestor(h, 2);
        StringBuilder sb = new StringBuilder(64);
        Win.GetClassName(root != IntPtr.Zero ? root : h, sb, 64);
        string c = sb.ToString();
        return c == "Progman" || c == "WorkerW";
    }

    // частота - за обставинами: немає закріплених нотаток (чи гра) - раз на секунду, миша далеко - 4 рази, над нотаткою - частіше
    static void Rate(int ms) { if (timer.Interval != ms) timer.Interval = ms; }

    static void Tick()
    {
        List<KeyValuePair<Note, NoteForm>> locked = NeoBar.Paused ? null : Notes.LockedForms();
        if (locked == null || locked.Count == 0)
        {
            if (badge != null && !badge.IsDisposed && badge.Visible) badge.Hide();
            Rate(1000);
            return;
        }
        Point p = Cursor.Position;
        if (badge != null && !badge.IsDisposed && badge.Visible && badge.Bounds.Contains(p)) { Rate(150); return; }
        bool near = false;
        foreach (KeyValuePair<Note, NoteForm> kv in locked)
        {
            Rectangle r = kv.Value.Bounds;
            if (Rectangle.Inflate(r, 40, 40).Contains(p)) near = true;
            if (!r.Contains(p) || !DesktopAt(p)) continue;
            if (badge == null || badge.IsDisposed) badge = new Badge();
            badge.ShowFor(kv.Key, r);
            Rate(150);
            return;
        }
        if (badge != null && !badge.IsDisposed && badge.Visible) badge.Hide();
        Rate(near ? 150 : 250);
    }

    sealed class Badge : LayeredForm
    {
        Note note;
        bool hover;
        float lockT = 1f;                 // 1 - замок закритий; під час кліку піднімається до 0
        System.Windows.Forms.Timer opening;
        int S { get { return (int)Math.Round(28 * Notes.Scale); } }

        public void ShowFor(Note n, Rectangle r)
        {
            bool changed = note != n || !Visible;
            note = n;
            Rectangle b = new Rectangle(r.Right - S - (int)(6 * Notes.Scale), r.Top + (int)(6 * Notes.Scale), S, S);
            if (Bounds != b) Bounds = b;
            if (!Visible) { Show(); try { Win.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x1 | 0x2 | 0x10); } catch (Exception skipEx) { SbLog.Skip(skipEx); } }
            if (changed) Redraw();
        }

        void Redraw()
        {
            if (note == null) return;
            NoteTheme t = Notes.ThemeOf(note);
            Color bg = hover ? t.Header : Color.FromArgb(225, Util.Blend(t.Body, t.Fg, 0.2f));
            Color ink = hover ? Notes.Contrast(t.Header) : t.Fg;
            using (Bitmap bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (SolidBrush b = new SolidBrush(bg)) g.FillEllipse(b, 0, 0, S - 1, S - 1);
                    float isz = S * 0.5f;
                    Ico.DrawLock(g, new RectangleF((S - isz) / 2f, (S - isz) / 2f, isz, isz), ink, lockT);
                }
                Push(bmp, Location);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Cursor = Cursors.Hand; Redraw(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Redraw(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || note == null) return;
            if (opening != null) return;
            Note n = note;
            // спершу замок відчиняється, потім нотатка розблоковується
            DateTime t0 = DateTime.Now;
            opening = new System.Windows.Forms.Timer();
            opening.Interval = 16;
            opening.Tick += delegate
            {
                float f = (float)((DateTime.Now - t0).TotalMilliseconds / 240.0);
                if (f >= 1f)
                {
                    opening.Stop(); opening.Dispose(); opening = null;
                    lockT = 1f;
                    Hide();
                    Notes.SetLocked(n, false);
                    return;
                }
                lockT = 1f - (1f - (1f - f) * (1f - f));
                Redraw();
            };
            opening.Start();
        }
    }
}
