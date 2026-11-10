using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// ======================================================================
//  Допоміжне для нотаток:
//   RichFmt     - формат символів RichEdit напряму (EM_SETCHARFORMAT): жирний/курсив/розмір/шрифт
//                 окремо для виділеного, без перебудови шрифтів GDI+ (власні шрифти мають лише
//                 «звичайне» накреслення, жирний і курсив RichEdit/GDI синтезують самі);
//   ColorEmoji  - кольорові емодзі (набір картинок emoji.bin у ресурсах exe);
//   NoteBox     - RichTextBox, який повідомляє про прокрутку (для накладок із емодзі);
//   EmojiPic    - накладка: кольорова картинка поверх символу-емодзі.
// ======================================================================

static class RichFmt
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CF
    {
        public int cbSize;
        public uint dwMask;
        public uint dwEffects;
        public int yHeight;
        public int yOffset;
        public int crTextColor;
        public byte bCharSet;
        public byte bPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szFaceName;
        public ushort wWeight;
        public short sSpacing;
        public int crBackColor;
        public int lcid;
        public uint dwReserved;
        public short sStyle;
        public ushort wKerning;
        public byte bUnderlineType;
        public byte bAnimation;
        public byte bRevAuthor;
        public byte bUnderlineColor;
    }

    public const uint CFM_BOLD = 1, CFM_ITALIC = 2, CFM_UNDERLINE = 4, CFM_STRIKEOUT = 8;
    public const uint CFM_SIZE = 0x80000000, CFM_FACE = 0x20000000, CFM_CHARSET = 0x08000000;
    public const int SCF_DEFAULT = 0, SCF_SELECTION = 1, SCF_ALL = 4;

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    static extern IntPtr SendCF(IntPtr h, int msg, IntPtr w, ref CF lp);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    static extern IntPtr SendMsg(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMsgS(IntPtr h, int msg, IntPtr w, string s);
    [DllImport("gdi32.dll")] static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] static extern int SetTextColor(IntPtr hdc, int color);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int DrawTextW(IntPtr hdc, string s, int n, ref RECT r, uint fmt);

    public static bool Native { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }

    static CF New()
    {
        CF c = new CF();
        c.cbSize = Marshal.SizeOf(typeof(CF));
        c.szFaceName = "";
        return c;
    }

    // формат виділеного (або точки введення); dwMask показує, які властивості однакові в усьому виділеному
    public static bool Get(IntPtr h, out CF c)
    {
        c = New();
        try { SendCF(h, 0x043A, (IntPtr)1, ref c); return true; }      // EM_GETCHARFORMAT, SCF_SELECTION
        catch { return false; }
    }

    static void Set(IntPtr h, int scf, ref CF c)
    {
        try { SendCF(h, 0x0444, (IntPtr)scf, ref c); } catch (Exception skipEx) { SbLog.Skip(skipEx); }         // EM_SETCHARFORMAT
    }

    public static bool Effect(IntPtr h, uint bit)
    {
        CF c;
        return Get(h, out c) && (c.dwMask & bit) != 0 && (c.dwEffects & bit) != 0;
    }

    public static void SetEffect(IntPtr h, uint bit, bool on)
    {
        CF c = New();
        c.dwMask = bit;
        c.dwEffects = on ? bit : 0;
        Set(h, SCF_SELECTION, ref c);
    }

    public static void SetFace(IntPtr h, int scf, string face)
    {
        CF c = New();
        c.dwMask = CFM_FACE | CFM_CHARSET;
        c.bCharSet = 1;                       // DEFAULT_CHARSET: кирилиця й латиниця з одного шрифту
        c.szFaceName = face;
        Set(h, scf, ref c);
    }

    public static void SetSize(IntPtr h, int scf, float pt)
    {
        SetSizeTw(h, scf, (int)Math.Round(pt * 20f));
    }

    public static void SetSizeTw(IntPtr h, int scf, int twips)
    {
        CF c = New();
        c.dwMask = CFM_SIZE;
        c.yHeight = Math.Max(20, twips);
        Set(h, scf, ref c);
    }

    // розмір (pt) виділеного; -1, якщо він неоднаковий
    public static float SizeOf(IntPtr h)
    {
        CF c;
        if (!Get(h, out c) || (c.dwMask & CFM_SIZE) == 0 || c.yHeight <= 0) return -1f;
        return c.yHeight / 20f;
    }

    public static string FaceOf(IntPtr h)
    {
        CF c;
        if (!Get(h, out c) || (c.dwMask & CFM_FACE) == 0) return "";
        return c.szFaceName ?? "";
    }

    // ---- звичайні елементи керування (поле назви): шрифт GDI за назвою, без GDI+ ----
    public static IntPtr MakeHFont(string face, int px, bool bold)
    {
        return Win.CreateFontW(-Math.Max(6, px), 0, 0, 0, bold ? 700 : 400, 0, 0, 0, 1, 0, 0, 4, 0, face);
    }

    public static void FreeHFont(IntPtr hf) { if (hf != IntPtr.Zero) Win.DeleteObject(hf); }

    public static void SetControlFont(Control c, IntPtr hf)
    {
        if (hf == IntPtr.Zero) return;
        try { SendMsg(c.Handle, 0x0030, hf, (IntPtr)1); } catch (Exception skipEx) { SbLog.Skip(skipEx); }      // WM_SETFONT
    }

    public static void SetCue(Control c, string text)
    {
        try { SendMsgS(c.Handle, 0x1501, (IntPtr)1, text); } catch (Exception skipEx) { SbLog.Skip(skipEx); }   // EM_SETCUEBANNER
    }

    // текст через GDI (так само, як малює RichEdit) у вказаний Graphics
    public static void DrawText(Graphics g, IntPtr hf, string text, Rectangle r, Color col)
    {
        IntPtr hdc = g.GetHdc();
        try
        {
            IntPtr old = Win.SelectObject(hdc, hf);
            SetBkMode(hdc, 1);
            SetTextColor(hdc, col.R | (col.G << 8) | (col.B << 16));
            RECT rc = new RECT(); rc.l = r.Left; rc.t = r.Top; rc.r = r.Right; rc.b = r.Bottom;
            DrawTextW(hdc, text, -1, ref rc, 0x4 | 0x20 | 0x800 | 0x8000);     // VCENTER, SINGLELINE, NOPREFIX, END_ELLIPSIS
            Win.SelectObject(hdc, old);
        }
        finally { g.ReleaseHdc(hdc); }
    }
}

// ---------- кольорові емодзі ----------

static class ColorEmoji
{
    // ресурс exe відображено в пам'ять (сторінки файлу, спільні) - копію 2 МБ не робимо, читаємо лише потрібну картинку
    static Stream data;
    static int baseOff;
    static readonly Dictionary<int, int[]> idx = new Dictionary<int, int[]>();
    static readonly Dictionary<int, Bitmap> cache = new Dictionary<int, Bitmap>();
    static bool inited;

    public static bool Ready { get { return idx.Count > 0; } }

    public static void Init()
    {
        if (inited) return;
        inited = true;
        try
        {
            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("emoji.bin");
            if (s == null) return;
            BinaryReader r = new BinaryReader(s);
            int n = r.ReadInt32();
            if (n <= 0 || n > 100000) { s.Dispose(); return; }
            baseOff = 4 + 12 * n;
            for (int i = 0; i < n; i++)
            {
                int cp = r.ReadInt32(), off = r.ReadInt32(), len = r.ReadInt32();
                idx[cp] = new int[] { off, len };
            }
            data = s;
        }
        catch { idx.Clear(); data = null; }
    }

    public static int Cp(string s, int i)
    {
        if (string.IsNullOrEmpty(s) || i < 0 || i >= s.Length) return -1;
        if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) return char.ConvertToUtf32(s[i], s[i + 1]);
        return s[i];
    }

    public static bool Has(int cp) { return idx.ContainsKey(cp); }

    // рядок - рівно один символ, і для нього є кольорова картинка
    public static bool HasOne(string s)
    {
        if (s == null || s.Length == 0 || s.Length > 2) return false;
        int cp = Cp(s, 0);
        return cp >= 0 && (cp > 0xFFFF ? s.Length == 2 : s.Length == 1) && idx.ContainsKey(cp);
    }

    public static Bitmap Get(int cp)
    {
        Bitmap b;
        if (cache.TryGetValue(cp, out b)) return b;
        int[] e;
        if (data == null || !idx.TryGetValue(cp, out e)) return null;
        try
        {
            byte[] png = new byte[e[1]];
            lock (data)
            {
                data.Position = baseOff + e[0];
                int got = 0, k;
                while (got < png.Length && (k = data.Read(png, got, png.Length - got)) > 0) got += k;
            }
            using (MemoryStream ms = new MemoryStream(png))
            using (Bitmap src = new Bitmap(ms))
            {
                b = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(b)) g.DrawImage(src, 0, 0, src.Width, src.Height);
            }
        }
        catch { b = null; }
        cache[cp] = b;
        return b;
    }

    public static Bitmap Get(string s) { return HasOne(s) ? Get(Cp(s, 0)) : null; }

    // малює картинку емодзі у квадраті r (з якісним масштабуванням); false - картинки нема
    public static bool Draw(Graphics g, string s, Rectangle r)
    {
        Bitmap b = Get(s);
        if (b == null) return false;
        InterpolationMode im = g.InterpolationMode;
        PixelOffsetMode po = g.PixelOffsetMode;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(b, r);
        g.InterpolationMode = im;
        g.PixelOffsetMode = po;
        return true;
    }
}

// ---------- RichTextBox з сповіщенням про прокрутку ----------

sealed class NoteBox : RichTextBox
{
    public event EventHandler ViewChanged;

    public void RaiseView() { if (ViewChanged != null) ViewChanged(this, EventArgs.Empty); }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.Style |= 0x02000000;           // WS_CLIPCHILDREN: накладки з емодзі не затираються перемальовкою тексту
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // 0x115 WM_VSCROLL, 0x20A WM_MOUSEWHEEL, 0x0B6 EM_LINESCROLL, 0x0B7 EM_SCROLLCARET (автопрокрутка до курсора),
        // 0x05 WM_SIZE, 0x0F WM_PAINT (будь-яке перемалювання тексту: позиції символів могли змінитися)
        if ((m.Msg == 0x115 || m.Msg == 0x20A || m.Msg == 0x0B6 || m.Msg == 0x0B7 || m.Msg == 0x05 || m.Msg == 0x0F) && ViewChanged != null)
            ViewChanged(this, EventArgs.Empty);
    }
}

// ---------- накладка: кольорове емодзі поверх символу ----------

sealed class EmojiPic : Control
{
    Bitmap img;
    Color bg = Color.Black;
    string key = "";

    public EmojiPic()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
    }

    public void Place(Bitmap b, string k, Color back, Rectangle r)
    {
        bool same = b == img && key == k && back == bg && Bounds == r;
        img = b; key = k; bg = back;
        if (same) { if (!Visible) Visible = true; return; }
        BackColor = back;
        Bounds = r;
        if (!Visible) Visible = true;
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(bg);
        if (img == null) return;
        int s = Math.Min(Width, Height);
        Rectangle r = new Rectangle((Width - s) / 2, (Height - s) / 2, s, s);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(img, r);
    }

    // клік проходить крізь накладку до тексту
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0084) { m.Result = (IntPtr)(-1); return; }      // WM_NCHITTEST -> HTTRANSPARENT
        base.WndProc(ref m);
    }
}

// ---------- власна смуга прокрутки нотатки: тонка, у кольорах теми (системна - за краєм поля тексту) ----------
sealed class NoteScroll : Control
{
    readonly NoteBox box;
    readonly Func<NoteTheme> theme;
    bool drag, hover;
    int dragOff;

    [StructLayout(LayoutKind.Sequential)] struct SCROLLINFO { public int cbSize; public uint fMask; public int nMin, nMax; public uint nPage; public int nPos, nTrackPos; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetScrollInfo(IntPtr h, int bar, ref SCROLLINFO si);
    [StructLayout(LayoutKind.Sequential)]
    struct SCROLLBARINFO
    {
        public int cbSize; public int l, t, r, b; public int dxyLineButton, xyThumbTop, xyThumbBottom, reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public int[] rgstate;
    }
    [DllImport("user32.dll")] static extern bool GetScrollBarInfo(IntPtr h, int obj, ref SCROLLBARINFO sbi);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern IntPtr SendPoint(IntPtr h, int msg, IntPtr w, ref POINT l);

    public NoteScroll(NoteBox b, Func<NoteTheme> t)
    {
        box = b;
        theme = t;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
    }

    // false - прокручувати нічого (текст вміщається)
    bool Info(out int min, out int max, out int page, out int pos)
    {
        min = max = page = pos = 0;
        if (!box.IsHandleCreated) return false;
        SCROLLINFO si = new SCROLLINFO();
        si.cbSize = Marshal.SizeOf(typeof(SCROLLINFO));
        si.fMask = 0x17;                    // SIF_ALL
        if (!GetScrollInfo(box.Handle, 1, ref si)) return false;
        min = si.nMin; max = si.nMax; page = (int)si.nPage; pos = si.nPos;
        if (page <= 0 || max - min + 1 <= page) return false;
        // текст вміщається: поле з «завжди видимою» смугою вимикає її, але діапазон лишає трохи більшим за сторінку
        try
        {
            SCROLLBARINFO sb = new SCROLLBARINFO();
            sb.cbSize = Marshal.SizeOf(typeof(SCROLLBARINFO));
            if (GetScrollBarInfo(box.Handle, unchecked((int)0xFFFFFFFB), ref sb) && sb.rgstate != null && (sb.rgstate[0] & 0x1) != 0) return false;   // OBJID_VSCROLL, STATE_SYSTEM_UNAVAILABLE
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return max - min + 1 - page > Notes.Px(6);
    }

    Rectangle Track() { return new Rectangle(0, Notes.Px(2), Width, Math.Max(1, Height - Notes.Px(8))); }       // знизу - відступ від панелі форматування

    Rectangle Thumb(out int min, out int span, out int trackFree)
    {
        int max, page, pos;
        span = 0; trackFree = 0;
        if (!Info(out min, out max, out page, out pos)) return Rectangle.Empty;
        Rectangle tr = Track();
        int range = max - min + 1;
        int th = Math.Min(tr.Height, Math.Max(Notes.Px(24), (int)((long)tr.Height * page / range)));
        span = range - page;
        trackFree = tr.Height - th;
        int y = tr.Y + (span > 0 ? (int)((long)trackFree * Math.Max(0, Math.Min(span, pos - min)) / span) : 0);
        return new Rectangle(0, y, Width, th);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        NoteTheme t = theme();
        e.Graphics.Clear(t.Body);
        int min, span, free;
        Rectangle th = Thumb(out min, out span, out free);
        if (th.IsEmpty) return;
        int w = hover || drag ? Notes.Px(6) : Notes.Px(4);
        Rectangle r = new Rectangle((Width - w) / 2, th.Y, w, th.Height);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (SolidBrush br = new SolidBrush(Util.Blend(t.Body, t.Fg, drag ? 0.6f : (hover ? 0.45f : 0.3f)))) Util.FillRound(e.Graphics, br, r, Math.Max(1, w / 2));
    }

    void ScrollTo(int pos)
    {
        POINT p = new POINT();
        SendPoint(box.Handle, 0x04DD, IntPtr.Zero, ref p);        // EM_GETSCROLLPOS
        p.Y = Math.Max(0, pos);
        SendPoint(box.Handle, 0x04DE, IntPtr.Zero, ref p);        // EM_SETSCROLLPOS
        box.RaiseView();
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        int min, span, free;
        Rectangle th = Thumb(out min, out span, out free);
        if (th.IsEmpty) return;
        if (th.Contains(e.Location)) { drag = true; dragOff = e.Y - th.Y; Capture = true; }
        else { SendMessage(box.Handle, 0x115, new IntPtr(e.Y < th.Y ? 2 : 3), IntPtr.Zero); box.RaiseView(); }      // WM_VSCROLL: SB_PAGEUP / SB_PAGEDOWN
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!drag) return;
        int min, span, free;
        Rectangle th = Thumb(out min, out span, out free);
        if (th.IsEmpty || free <= 0) return;
        int y = e.Y - dragOff - Track().Y;
        ScrollTo(min + (int)((long)Math.Max(0, Math.Min(free, y)) * span / free));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        drag = false;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

    // коліщатко над смугою - як над текстом
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x20A && box.IsHandleCreated) { SendMessage(box.Handle, 0x20A, m.WParam, m.LParam); return; }
        base.WndProc(ref m);
    }
}
