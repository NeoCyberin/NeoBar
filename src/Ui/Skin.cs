using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// ======================================================================
//  Спільні помічники малювання за темою (форма з заокругленням, заливка, контур, тло під елементом)
//  і тонка рамка звичайних полів тексту. Самі елементи налаштувань малюють себе власним кодом (SetCtl.cs).
// ======================================================================
static class Skin
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] internal static extern IntPtr GetWindowDC(IntPtr h);

    public static bool NoNative = false;    // лише для знімків вигляду поза Windows: малювання не перехоплюється
    internal static readonly Dictionary<Control, NativeWindow> hooked = new Dictionary<Control, NativeWindow>();

    public static void Attach(Control c)
    {
        if (c == null || hooked.ContainsKey(c)) return;
        NativeWindow h = null;
        if (c is ISelfPaint) return;
        try
        {
            if ((c is TextBox && ((TextBox)c).BorderStyle != BorderStyle.None) || c is ListBox || c is ListView) h = new FrameSkin(c);
        }
        catch (Exception ex) { Util.ErrorLog(ex); h = null; }
        if (h == null) return;
        hooked[c] = h;
        c.Disposed += delegate { hooked.Remove(c); };
    }

    // форма з заокругленням теми (для «Артистичної» - з нерівним контуром)
    public static GraphicsPath Shape(Rectangle r, int rad)
    {
        rad = Math.Max(0, Math.Min(rad, Math.Min(r.Width, r.Height) / 2));
        if (FormTheme.Sketchy) return Sk.WobblyRound(r, Math.Max(2, rad), new Random(r.X * 7 + r.Y * 13 + r.Width), 0.9f);
        return Util.Round(r, rad);
    }

    public static void Fill(Graphics g, Rectangle r, int rad, Color c)
    {
        using (GraphicsPath gp = Shape(r, rad))
        using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, gp);
    }

    public static void Edge(Graphics g, Rectangle r, int rad, Color c, float w)
    {
        using (GraphicsPath gp = Shape(r, rad))
        using (Pen p = new Pen(c, w)) g.DrawPath(p, gp);
        if (FormTheme.Neon)
            using (GraphicsPath gp = Shape(Rectangle.Inflate(r, 1, 1), rad + 1))
            using (Pen p = new Pen(Color.FromArgb(70, c), w + 2f)) g.DrawPath(p, gp);
    }

    // радіус «пігулки» висоти h (урбаністична тема - прямокутна)
    public static int Pill(int h) { return FormTheme.Square ? 2 : h / 2; }

}

// рамка поля тексту та списків: тонка лінія теми (підсвічується при фокусі) замість системної
sealed class FrameSkin : NativeWindow
{
    readonly Control c;
    const int WM_NCPAINT = 0x0085;

    public FrameSkin(Control ctl)
    {
        c = ctl;
        if (c is TextBox && ((TextBox)c).BorderStyle == BorderStyle.Fixed3D) ((TextBox)c).BorderStyle = BorderStyle.FixedSingle;
        if (c is ListBox && ((ListBox)c).BorderStyle == BorderStyle.Fixed3D) ((ListBox)c).BorderStyle = BorderStyle.FixedSingle;
        if (Skin.NoNative) return;
        c.HandleCreated += delegate { if (Handle == IntPtr.Zero) AssignHandle(c.Handle); };
        c.HandleDestroyed += delegate { if (Handle != IntPtr.Zero) ReleaseHandle(); };
        c.GotFocus += delegate { Frame(); };
        c.LostFocus += delegate { Frame(); };
        c.MouseEnter += delegate { Frame(); };
        c.MouseLeave += delegate { Frame(); };
        if (c.IsHandleCreated) AssignHandle(c.Handle);
    }

    void Frame()
    {
        if (c.IsHandleCreated) Win.RedrawWindow(c.Handle, IntPtr.Zero, IntPtr.Zero, 0x0400 | 0x0001);   // RDW_FRAME | RDW_INVALIDATE
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != WM_NCPAINT) return;
        IntPtr dc = Skin.GetWindowDC(m.HWnd);
        if (dc == IntPtr.Zero) return;
        try
        {
            using (Graphics g = Graphics.FromHdc(dc))
            {
                bool hot = c.Focused || c.ClientRectangle.Contains(c.PointToClient(Cursor.Position));
                Color col = c.Focused ? Util.Blend(FormTheme.Line, FormTheme.Acc, 0.7f) : (hot ? Util.Blend(FormTheme.Line, FormTheme.Fg, 0.2f) : FormTheme.Line);
                using (Pen p = new Pen(col)) g.DrawRectangle(p, 0, 0, c.Width - 1, c.Height - 1);
            }
        }
        catch { }
        finally { Win.ReleaseDC(m.HWnd, dc); }
    }
}
