using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// ======================================================================
//  Оформлення звичайних вікон WinForms (налаштування) за темою панелі
// ======================================================================
static class FormTheme
{
    public static Color Bg = SystemColors.Control, Fg = SystemColors.ControlText, Sub = SystemColors.GrayText, Acc = SystemColors.Highlight,
                        Card = SystemColors.Window, Input = SystemColors.Window, Line = SystemColors.ControlDark, Hover = SystemColors.ControlLight,
                        Good = Color.SeaGreen, Warn = Color.DarkOrange, Bad = Color.Firebrick;
    public static bool Dark, Neon, Plate, Sketchy, Square;
    public static int Rad = 6;
    public static string Face = "Segoe UI";


    public static Theme T;          // тема панелі (градієнт навігації тощо)

    public static void Init(Pal p)
    {
        T = p.T;
        Bg = Util.Opaque(p.Bg);
        Fg = Util.Opaque(p.Fg);
        Sub = Util.Blend(Bg, Fg, 0.62f);
        Acc = Util.Opaque(p.Acc);
        bool dark0 = Util.Lum(Bg) < 128;
        // картки світлих тем - світліші за тло (як у Windows 11), темних - трохи світліші за тло; поля вводу - ще світліші
        Card = dark0 ? Util.Blend(Bg, Fg, 0.05f) : Util.Blend(Bg, Color.White, 0.65f);
        Input = dark0 ? Util.Blend(Bg, Fg, 0.105f) : Util.Blend(Bg, Color.White, 0.92f);
        Line = Util.Blend(Bg, Fg, 0.24f);
        Hover = Util.Blend(Bg, Fg, 0.15f);
        Good = Util.Opaque(p.Good); Warn = Util.Opaque(p.Warn); Bad = Util.Opaque(p.Bad);
        Dark = Util.Lum(Bg) < 128;
        Rad = Math.Max(3, Math.Min(12, p.T != null ? p.T.Radius : 6));
        Neon = p.T != null && p.T.Style == IconStyle.Neon;
        Plate = p.T != null && p.T.Style == IconStyle.Plate;
        Sketchy = p.T != null && p.T.Sketchy;
        Square = p.T != null && p.T.Radius <= 1;
        Face = p.T != null && !string.IsNullOrEmpty(p.T.Font) ? p.T.Font : "Segoe UI";
    }

    // шрифт заголовків і вкладок - шрифт теми (решта тексту лишається Segoe UI: під нього розкладено вікна)
    public static Font HeadFont(float pt)
    {
        return Util.MakeFont(Face + "|Segoe UI", pt, FontStyle.Bold);
    }

    // заголовок вікна за темою: темний режим (Windows 10 1809+), колір заголовка й рамки (Windows 11)
    public static void Title(Form f)
    {
        try
        {
            int on = Dark ? 1 : 0;
            if (Win.DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) Win.DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
            int cap = Bg.R | (Bg.G << 8) | (Bg.B << 16);
            int txt = Fg.R | (Fg.G << 8) | (Fg.B << 16);
            Color e = Neon ? Acc : Util.Blend(Bg, Fg, 0.14f);
            int edge = e.R | (e.G << 8) | (e.B << 16);
            Win.DwmSetWindowAttribute(f.Handle, 35, ref cap, 4);
            Win.DwmSetWindowAttribute(f.Handle, 36, ref txt, 4);
            Win.DwmSetWindowAttribute(f.Handle, 34, ref edge, 4);
        }
        catch { }
    }

    public static void Apply(Control root)
    {
        One(root);
        Skin.Attach(root);
        if (root is UpDownBase || root is ComboBox || root is ListView || root is MapView || root is OvPreviewBox) return;
        foreach (Control c in root.Controls) Apply(c);
        // елементи, додані пізніше (рядки списків, сторінки), отримують той самий вигляд
        if (!(root is ButtonBase) && !(root is TextBoxBase) && !(root is Label))
            root.ControlAdded += delegate (object s, ControlEventArgs e) { try { Apply(e.Control); } catch { } };
    }

    static void One(Control c)
    {
        if (c is ISelfPaint) return;
        if (c is Form) { c.BackColor = Bg; c.ForeColor = Fg; return; }
        if (c is TabPage) { ((TabPage)c).UseVisualStyleBackColor = false; c.BackColor = Bg; c.ForeColor = Fg; return; }
        if (c.GetType() == typeof(Panel)) { c.BackColor = Bg; return; }
        if (c is Button)
        {
            Button b = (Button)c;
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.BackColor = Card;
            b.ForeColor = Fg;
            b.FlatAppearance.BorderColor = Line;
            b.FlatAppearance.MouseOverBackColor = Hover;
            b.FlatAppearance.MouseDownBackColor = Util.Blend(Hover, Acc, 0.35f);
            if ("danger".Equals(b.Tag))
            {
                Color red = Color.FromArgb(214, 64, 64);
                b.BackColor = Util.Blend(Card, red, 0.22f);
                b.ForeColor = Dark ? Color.FromArgb(255, 170, 170) : Color.FromArgb(170, 30, 30);
                b.FlatAppearance.BorderColor = Util.Blend(Line, red, 0.6f);
                b.FlatAppearance.MouseOverBackColor = Util.Blend(Card, red, 0.38f);
                b.FlatAppearance.MouseDownBackColor = Util.Blend(Card, red, 0.55f);
            }
            return;
        }
        if (c is CheckBox)
        {
            CheckBox x = (CheckBox)c;
            x.FlatStyle = FlatStyle.Flat;
            x.FlatAppearance.BorderColor = Line;
            x.FlatAppearance.CheckedBackColor = Input;
            x.ForeColor = Fg;
            return;
        }
        if (c is RadioButton) { ((RadioButton)c).FlatStyle = FlatStyle.Flat; c.ForeColor = Fg; return; }
        if (c is TextBoxBase)
        {
            c.BackColor = Input; c.ForeColor = Fg;
            // у картках поле малює сама картка (заокруглена «пігулка»), тож у TextBox рамки немає
            if (c is TextBox && ((TextBox)c).BorderStyle == BorderStyle.Fixed3D) ((TextBox)c).BorderStyle = BorderStyle.FixedSingle;
            return;
        }
        if (c is ComboBox) { ComboBox cb = (ComboBox)c; cb.FlatStyle = FlatStyle.Flat; cb.BackColor = Input; cb.ForeColor = Fg; return; }
        if (c is NumericUpDown) { NumericUpDown n = (NumericUpDown)c; n.BackColor = Input; n.ForeColor = Fg; n.BorderStyle = BorderStyle.FixedSingle; return; }
        if (c is ListBox) { ListBox l = (ListBox)c; l.BackColor = Input; l.ForeColor = Fg; l.BorderStyle = BorderStyle.FixedSingle; return; }
        if (c is ListView)
        {
            ListView lv = (ListView)c;
            lv.BackColor = Input; lv.ForeColor = Fg; lv.BorderStyle = BorderStyle.FixedSingle;
            if (lv.View == View.Details && !lv.OwnerDraw)
            {
                lv.OwnerDraw = true;
                lv.DrawColumnHeader += delegate (object s, DrawListViewColumnHeaderEventArgs e)
                {
                    using (SolidBrush b = new SolidBrush(Card)) e.Graphics.FillRectangle(b, e.Bounds);
                    using (Pen p = new Pen(Line)) e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                    TextRenderer.DrawText(e.Graphics, e.Header.Text, lv.Font, new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height), Sub,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                };
                lv.DrawItem += delegate (object s, DrawListViewItemEventArgs e) { e.DrawDefault = true; };
                lv.DrawSubItem += delegate (object s, DrawListViewSubItemEventArgs e) { e.DrawDefault = true; };
            }
            return;
        }
        if (c is TrackBar) { c.BackColor = SUi.BgOf(c); return; }
        if (c is PictureBox) { if (c.BackColor == SystemColors.Control || c.BackColor.A < 255) c.BackColor = Input; return; }
        if (c is LinkLabel) { LinkLabel ll = (LinkLabel)c; ll.LinkColor = Acc; ll.ActiveLinkColor = Fg; ll.VisitedLinkColor = Acc; return; }
    }
}
