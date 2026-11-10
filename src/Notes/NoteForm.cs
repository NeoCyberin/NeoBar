using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// Вікно нотатки: редагування, закріплення на шпалерах, попередній перегляд

// панель із подвійним буфером (без мигтіння при зміні розміру)
sealed class BufPanel : Panel
{
    public BufPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }
}

sealed class NoteForm : Form
{
    public static int MinW { get { return Notes.Px(300); } }
    public static int MinH { get { return Notes.Px(190); } }
    static int HeadH { get { return Notes.Px(32); } }
    static int ToolH { get { return Notes.Px(40); } }
    static int BodyPad { get { return Notes.Px(10); } }
    static float DpiY { get { return 96f * Notes.Scale; } }

    readonly Note n;
    readonly Panel headerPanel, toolbar, bodyPanel;
    readonly NoteBox box;
    readonly Panel boxHost;         // обрізає системну смугу прокрутки поля тексту (замість неї - власна, у кольорах теми)
    readonly NoteScroll vbar;
    readonly Panel titlePanel;
    readonly TextBox titleBox;
    readonly List<EmojiPic> pics = new List<EmojiPic>();
    readonly List<int[]> emo = new List<int[]>();       // емодзі в тексті: {індекс, довжина, код, розмір у твіпах}
    string themeFace = "", titleKey = "";
    float themeBase;                                    // базовий розмір тексту (pt) теми цієї нотатки
    int fmtBusy;                                        // >0: триває службове форматування (події виділення ігноруємо)
    bool emoDirty = true, emoQueued, emoForce, emoBusy;
    IntPtr titleHF = IntPtr.Zero;
    readonly GlyphButton bAdd, bTheme, bLock, bClose, bB, bI, bU, bS, bL, bE, bZ, grip;
    readonly OpacityBar bar;
    readonly ToolTip tip = new ToolTip();
    bool ready;
    int busy;
    string themeFont = "";

    const int GWL_EXSTYLE = -20;
    const int WS_EX_LAYERED = 0x00080000;
    const int WS_EX_TRANSPARENT = 0x00000020;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
    static readonly IntPtr HWND_TOP = IntPtr.Zero;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

    [DllImport("user32.dll", SetLastError = true)] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll", SetLastError = true)] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr dc, int index);

    [StructLayout(LayoutKind.Sequential)] struct RC { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] struct CHR { public int cpMin, cpMax; }
    [StructLayout(LayoutKind.Sequential)]
    struct FMTRANGE
    {
        public IntPtr hdc, hdcTarget;
        public RC rc, rcPage;
        public CHR chrg;
    }

    int Px(float v) { return Notes.Px(v); }

    public NoteForm(Note note)
    {
        n = note;
        FormBorderStyle = FormBorderStyle.None;
        AppIcon.Apply(this);
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        ResizeRedraw = true;
        Bounds = n.R;
        MinimumSize = new Size(MinW, MinH);

        // ----- шапка: [+] [тема] ......... [прозорість] [замок] [✕] -----
        headerPanel = new BufPanel();
        headerPanel.Dock = DockStyle.Top;
        headerPanel.Height = HeadH;
        headerPanel.Cursor = Cursors.SizeAll;
        headerPanel.MouseDown += delegate(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && !n.Locked) DragWindow(2);        // HTCAPTION
            else if (e.Button == MouseButtons.Right) ShowThemeMenu(headerPanel, e.Location);
        };

        bTheme = new GlyphButton(Glyph.Theme); bTheme.Dock = DockStyle.Left; bTheme.Width = Px(34);
        bTheme.Click += delegate { ShowThemeMenu(bTheme, new Point(0, bTheme.Height)); };
        bAdd = new GlyphButton(Glyph.Plus); bAdd.Dock = DockStyle.Left; bAdd.Width = Px(34);
        bAdd.Click += delegate { Notes.New(n.Theme); };

        bar = new OpacityBar(); bar.Dock = DockStyle.Right; bar.Width = Px(150);
        bar.SetValue(Notes.OpacityOf(n));
        bar.Started += delegate { StartPreview(); };                // перегляд: прозорий лише фон
        bar.Changed += delegate(int v)
        {
            Notes.SetNoteOpacity(n, v);
            UpdatePreview();
        };
        bar.Finished += delegate { EndPreview(); };
        bLock = new GlyphButton(Glyph.Lock); bLock.Dock = DockStyle.Right; bLock.Width = Px(36);
        bLock.Click += delegate
        {
            // замок «защіпається», і лише тоді нотатка закріплюється на шпалерах
            if (bLock.LockBusy) return;
            bLock.AnimateLock(0f, 1f, 240, delegate { Notes.SetLocked(n, true); bLock.LockT = 0f; });
        };
        bClose = new GlyphButton(Glyph.Close); bClose.Dock = DockStyle.Right; bClose.Width = Px(34);
        bClose.Click += delegate { AskDelete(); };

        headerPanel.Controls.Add(bar);
        headerPanel.Controls.Add(bLock);
        headerPanel.Controls.Add(bClose);
        headerPanel.Controls.Add(bTheme);
        headerPanel.Controls.Add(bAdd);

        tip.SetToolTip(bAdd, L.T("Нова нотатка"));
        tip.SetToolTip(bTheme, L.T("Тема нотатки (права кнопка миші по шапці - теж)"));
        tip.SetToolTip(bar, L.T("Прозорість фону (текст лишається чітким)"));
        tip.SetToolTip(bLock, L.T("Закріпити текст на робочому столі"));
        tip.SetToolTip(bClose, L.T("Видалити нотатку"));

        // ----- панель інструментів -----
        toolbar = new BufPanel();
        toolbar.Dock = DockStyle.Bottom;
        toolbar.Height = ToolH;
        int bw = Px(38), bh = Px(32), by = (ToolH - bh) / 2, x = Px(6);
        bZ = Tb(Glyph.Size, ref x, bw, bh, by, L.T("Розмір тексту нотатки: малий, стандартний, великий"));
        bB = Tb(Glyph.Bold, ref x, bw, bh, by, L.T("Жирний (Ctrl+B)"));
        bI = Tb(Glyph.Italic, ref x, bw, bh, by, L.T("Курсив (Ctrl+I)"));
        bU = Tb(Glyph.Underline, ref x, bw, bh, by, L.T("Підкреслений (Ctrl+U)"));
        bS = Tb(Glyph.Strike, ref x, bw, bh, by, L.T("Закреслений (Ctrl+T)"));
        bL = Tb(Glyph.Bullets, ref x, bw, bh, by, L.T("Маркований список (Ctrl+Shift+L)"));
        bE = Tb(Glyph.Emoji, ref x, bw, bh, by, L.T("Емодзі та символи Unicode"));
        bB.Click += delegate { ToggleStyle(FontStyle.Bold); };
        bI.Click += delegate { ToggleStyle(FontStyle.Italic); };
        bU.Click += delegate { ToggleStyle(FontStyle.Underline); };
        bS.Click += delegate { ToggleStyle(FontStyle.Strikeout); };
        bL.Click += delegate { ToggleBullets(); };
        bE.Click += delegate { ShowEmoji(); };
        bZ.Click += delegate { ShowSizeMenu(); };

        grip = new GlyphButton(Glyph.Grip);
        grip.Dock = DockStyle.Right;
        grip.Width = Px(24);
        grip.MouseDown += delegate(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && !n.Locked) { gripDrag = true; try { DragWindow(17); } finally { gripDrag = false; } }       // HTBOTTOMRIGHT
        };
        toolbar.Controls.Add(grip);

        // ----- текст -----
        bodyPanel = new BufPanel();
        bodyPanel.Dock = DockStyle.Fill;
        bodyPanel.Padding = new Padding(BodyPad);

        // назва нотатки над текстом
        titlePanel = new BufPanel();
        titlePanel.Dock = DockStyle.Top;
        titlePanel.Height = Notes.Px(38);
        titleBox = new TextBox();
        titleBox.BorderStyle = BorderStyle.None;
        titleBox.MaxLength = 80;
        titleBox.Text = n.Title ?? "";
        titlePanel.Controls.Add(titleBox);
        titlePanel.Resize += delegate { LayoutTitle(); };
        titlePanel.Paint += delegate(object so, PaintEventArgs pe)
        {
            NoteTheme tt = Notes.ThemeOf(n);
            using (Pen pn = new Pen(Util.Blend(tt.Body, tt.Fg, 0.28f)))
                pe.Graphics.DrawLine(pn, 0, titlePanel.Height - 1, titlePanel.Width, titlePanel.Height - 1);
        };
        titleBox.HandleCreated += delegate
        {
            RichFmt.SetControlFont(titleBox, titleHF);
            RichFmt.SetCue(titleBox, L.T("Назва нотатки"));
        };
        titleBox.TextChanged += delegate
        {
            if (!ready) return;
            n.Title = titleBox.Text;
            Notes.Touch();
        };
        titleBox.KeyDown += delegate(object so, KeyEventArgs ke)
        {
            if (ke.KeyCode == Keys.Enter || ke.KeyCode == Keys.Down) { ke.SuppressKeyPress = true; ke.Handled = true; box.Focus(); }
        };

        box = new NoteBox();
        box.BorderStyle = BorderStyle.None;
        box.Multiline = true;
        box.WordWrap = true;
        box.DetectUrls = false;
        box.AcceptsTab = true;
        box.HideSelection = false;
        box.ScrollBars = RichTextBoxScrollBars.ForcedVertical;        // завжди є - ширина тексту не стрибає; сама смуга - за краєм boxHost
        box.BulletIndent = Px(16);
        box.HandleCreated += delegate
        {
            try { Win.SendMessage(box.Handle, 0xD3, 3, 0); } catch { }   // EM_SETMARGINS: без внутрішніх полів
        };
        box.TextChanged += delegate
        {
            if (!ready) return;
            n.Text = box.Text;
            Notes.Touch();
            if (fmtBusy == 0) QueueEmoji(true);
        };
        box.SelectionChanged += delegate
        {
            if (fmtBusy != 0) return;
            SyncToolbar();
            // рух курсора може довантажити прокрутку/перемалювати рядок: картинки переставляємо й перемальовуємо
            emoForce = true; QueueEmoji(false);
        };
        box.KeyDown += OnBoxKey;
        box.ViewChanged += delegate { if (ready && fmtBusy == 0) { if (emoDirty) QueueEmoji(false); else RefreshEmoji(); } };   // перебудову (вона міняє виділення) не робимо всередині перемальовки
        box.Resize += delegate { if (ready) QueueEmoji(false); };
        box.HandleCreated += delegate { QueueEmoji(true); };
        boxHost = new Panel();
        boxHost.Dock = DockStyle.Fill;
        boxHost.Controls.Add(box);
        boxHost.Resize += delegate { LayoutBox(); };
        vbar = new NoteScroll(box, delegate { return Notes.ThemeOf(n); });
        vbar.Dock = DockStyle.Right;
        vbar.Width = Px(10);
        box.ViewChanged += delegate { vbar.Invalidate(); };
        box.TextChanged += delegate { vbar.Invalidate(); };
        bodyPanel.Controls.Add(boxHost);
        bodyPanel.Controls.Add(vbar);
        bodyPanel.Controls.Add(titlePanel);

        Controls.Add(bodyPanel);
        Controls.Add(toolbar);
        Controls.Add(headerPanel);

        try { NoteTheme t0 = Notes.ThemeOf(n); box.Font = Util.MakeFont(t0.FontName, Notes.FontPt(n, t0), FontStyle.Regular); } catch { }
        try
        {
            if (!string.IsNullOrEmpty(n.Rtf)) box.Rtf = n.Rtf; else box.Text = n.Text;
        }
        catch { box.Text = n.Text; }
        ApplyTheme(true);
        n.Text = box.Text;
        ready = true;
        SyncToolbar();

        VisibleChanged += delegate { if (Visible) QueueEmoji(true); };
        Move += delegate { if (ready && busy == 0 && !n.Locked && Visible) { PullRect(); Notes.Touch(); } };
        Resize += delegate { if (ready && busy == 0 && !n.Locked && Visible) { PullRect(); Notes.Touch(); if (!sizing) UpdateRegion(); } };
    }

    // поле тексту ширше за контейнер на ширину системної смуги прокрутки - вона опиняється за краєм і не видна
    void LayoutBox()
    {
        Size cs = boxHost.ClientSize;
        Rectangle want = new Rectangle(0, 0, cs.Width + SystemInformation.VerticalScrollBarWidth, cs.Height);
        if (box.Bounds != want) box.Bounds = want;
        vbar.Invalidate();
    }

    // ---- без мигтіння при зміні розміру ----
    bool sizing, gripDrag;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x02000000;      // WS_EX_COMPOSITED: усі дочірні елементи малюються через подвійний буфер
            return cp;
        }
    }

    protected override void OnResizeBegin(EventArgs e)
    {
        base.OnResizeBegin(e);
        // заокруглений Region міняти на кожен піксель дорого й мигає - на час перетягування прибираємо
        if (!gripDrag) return;          // це переміщення, а не зміна розміру
        sizing = true;
        try { if (Region != null) { Region.Dispose(); Region = null; } } catch { }
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        if (!sizing) return;
        sizing = false;
        UpdateRegion();
        Invalidate(true);
    }

    GlyphButton Tb(Glyph g, ref int x, int w, int h, int y, string tt)
    {
        GlyphButton b = new GlyphButton(g);
        b.Location = new Point(x, y);
        b.Size = new Size(w, h);
        toolbar.Controls.Add(b);
        tip.SetToolTip(b, tt);
        x += w + Px(2);
        return b;
    }

    void DragWindow(int hit)
    {
        try
        {
            ReleaseCapture();
            Win.SendMessage(Handle, 0xA1, hit, 0);       // WM_NCLBUTTONDOWN
        }
        catch { }
    }

    protected override bool ShowWithoutActivation { get { return n != null && n.Locked; } }

    // вибір розміру тексту всієї нотатки: малий / стандартний / великий
    void ShowSizeMenu()
    {
        NoteTheme t = Notes.ThemeOf(n);
        Color bg = Util.Opaque(t.Body), tx = Util.Opaque(t.Fg);
        ContextMenuStrip m = new ContextMenuStrip();
        m.ForeColor = tx;
        RoundMenu.Apply(m, bg, tx, Util.Opaque(t.Border), Util.Blend(bg, tx, 0.25f), Px(10));
        for (int i = 0; i < 3; i++)
        {
            int k = i;
            ToolStripMenuItem it = new ToolStripMenuItem(Notes.SizeNames[i]);
            it.ForeColor = tx;
            it.Checked = n.Size == i;
            try { it.Font = new Font("Segoe UI", 9f + i * 2.5f, FontStyle.Regular); } catch { }
            it.Click += delegate { ApplyNoteSize(k); };
            m.Items.Add(it);
        }
        m.Closed += delegate { BeginInvoke(new Action(m.Dispose)); };
        m.Show(bZ, new Point(0, -m.PreferredSize.Height - Px(2)));
    }

    // розмір усієї нотатки: усі символи стають такого розміру
    void ApplyNoteSize(int i)
    {
        n.Size = i;
        Notes.Touch();
        NoteTheme t = Notes.ThemeOf(n);
        if (RichFmt.Native)
        {
            float pt = Notes.FontPt(n, t);
            fmtBusy++;
            try
            {
                RichFmt.SetSize(box.Handle, RichFmt.SCF_ALL, pt);
                RichFmt.SetSize(box.Handle, RichFmt.SCF_DEFAULT, pt);
                themeBase = pt;
            }
            catch { }
            finally { fmtBusy--; }
            ApplyTheme(false);
        }
        else ApplyTheme(true);
        QueueEmoji(true);
        box.Focus();
    }

    // ---------- тема ----------
    public void ThemeUpdated()
    {
        ApplyTheme(false);
        if (n.Locked) ReapplyLock();
    }

    void ApplyTheme(bool force)
    {
        NoteTheme t = Notes.ThemeOf(n);
        Color head = t.Header, body = t.Body, fg = t.Fg;
        Color tool = Util.Blend(body, fg, 0.07f);
        BackColor = body;
        if (!n.Locked) Padding = new Padding(NoteFrame.Inset(t));
        headerPanel.BackColor = head;
        bodyPanel.BackColor = body;
        boxHost.BackColor = body;
        toolbar.BackColor = tool;
        box.BackColor = body;
        vbar.Invalidate();
        Color hi = Notes.Contrast(head);
        bAdd.Ink = hi; bTheme.Ink = hi; bLock.Ink = hi; bClose.Ink = hi; bar.Ink = hi;
        bTheme.Fill = body;
        bTheme.Invalidate();
        bar.SetValue(Notes.OpacityOf(n));
        foreach (GlyphButton b in new GlyphButton[] { bB, bI, bU, bS, bL, bE, bZ, grip }) b.Ink = fg;
        headerPanel.Invalidate(true);
        toolbar.Invalidate(true);

        float pt = Notes.FontPt(n, t);
        bool native = RichFmt.Native;
        string face = native ? Util.ResolveFace(t.FontName) : "";
        string key = native ? face : t.FontName + "|" + pt.ToString(CultureInfo.InvariantCulture);
        bool fontChanged = force || key != themeFont;
        themeFont = key;
        int ss = 0, sl = 0;
        try { ss = box.SelectionStart; sl = box.SelectionLength; } catch { }
        Redraw(false);
        fmtBusy++;
        try
        {
            if (native) ApplyFontNative(face, pt, fontChanged);
            else ApplyFontOld(t, pt, fontChanged, fg);
            box.ForeColor = fg;
            box.SelectAll();
            box.SelectionColor = fg;
            box.BulletIndent = Px(16);
            box.Select(Math.Min(ss, box.TextLength), sl);
        }
        catch { }
        finally { fmtBusy--; }
        ApplyTitle(t, pt, face);
        Redraw(true);
        UpdateRegion();
        Invalidate(true);
        QueueEmoji(true);
    }

    // RichEdit: шрифт за назвою (GDI), розміри окремих символів зберігаються; жирний/курсив лишаються
    void ApplyFontNative(string face, float pt, bool faceChanged)
    {
        IntPtr h = box.Handle;
        if (faceChanged)
        {
            List<int[]> sym = SymRuns(box.Text);                 // ділянки шрифту символів лишаємо як є
            RichFmt.SetFace(h, RichFmt.SCF_ALL, face);
            foreach (int[] r in sym) { Sel(r[0], r[1]); RichFmt.SetFace(h, RichFmt.SCF_SELECTION, EmojiSets.Font); }
            RichFmt.SetFace(h, RichFmt.SCF_DEFAULT, face);
            themeFace = face;
        }
        if (themeBase <= 0f)
        {
            RichFmt.SetSize(h, RichFmt.SCF_DEFAULT, pt);         // перше застосування: розміри з RTF не чіпаємо
            themeBase = pt;
        }
        else if (Math.Abs(pt - themeBase) > 0.01f)
        {
            RemapSizes((double)pt / themeBase);                  // змінився базовий розмір теми: масштабуємо всі розміри
            RichFmt.SetSize(h, RichFmt.SCF_DEFAULT, pt);
            themeBase = pt;
        }
    }

    // не Windows (тести): старий шлях через шрифти WinForms
    void ApplyFontOld(NoteTheme t, float pt, bool fontChanged, Color fg)
    {
        if (!fontChanged) return;
        List<int[]> runs = CollectRuns();
        Font nf = Util.MakeFont(t.FontName, pt, FontStyle.Regular);
        box.Font = nf;
        box.ForeColor = fg;
        box.SelectAll();
        box.SelectionColor = fg;
        foreach (int[] r in runs)
        {
            if (r[2] == 0 && r[3] == 0) continue;
            box.Select(r[0], r[1]);
            // RichTextBox копіює шрифт у формат тексту - сам об'єкт одразу звільняємо (інакше GDI-дескриптор висить до збирача сміття)
            using (Font f = r[3] != 0 ? new Font(EmojiSets.Font, nf.Size, (FontStyle)r[2], nf.Unit)
                                      : new Font(nf.FontFamily, nf.Size, (FontStyle)r[2], nf.Unit))
                box.SelectionFont = f;
        }
    }

    // виділення від індексу a довжиною len (без подій WinForms)
    void Sel(int a, int len)
    {
        if (RichFmt.Native) Win.SendMessage(box.Handle, 0x00B1, a, a + len);         // EM_SETSEL
        else box.Select(a, len);
    }

    // ділянки тексту, набрані шрифтом символів
    List<int[]> SymRuns(string all)
    {
        List<int[]> runs = new List<int[]>();
        IntPtr h = box.Handle;
        for (int i = 0; i < all.Length; )
        {
            int w = char.IsHighSurrogate(all[i]) && i + 1 < all.Length ? 2 : 1;
            if (EmojiSets.NeedsSym(all[i]))
            {
                Sel(i, w);
                string f = RichFmt.FaceOf(h);
                if (f == EmojiSets.Font || f == "Segoe UI Emoji")
                {
                    int[] last = runs.Count > 0 ? runs[runs.Count - 1] : null;
                    if (last != null && last[0] + last[1] == i) last[1] += w; else runs.Add(new int[] { i, w });
                }
            }
            i += w;
        }
        return runs;
    }

    // усі розміри символів множаться на ratio (змінилася тема або базовий розмір)
    void RemapSizes(double ratio)
    {
        IntPtr h = box.Handle;
        int len = box.TextLength;
        int i = 0;
        while (i < len)
        {
            Sel(i, 1);
            RichFmt.CF c;
            if (!RichFmt.Get(h, out c)) return;
            int y = c.yHeight, j = i + 1;
            while (j < len)
            {
                Sel(j, 1);
                RichFmt.CF d;
                if (!RichFmt.Get(h, out d) || d.yHeight != y) break;
                j++;
            }
            if (y > 0)
            {
                Sel(i, j - i);
                RichFmt.SetSizeTw(h, RichFmt.SCF_SELECTION, (int)Math.Round(y * ratio));
            }
            i = j;
        }
    }

    // шрифт і розмір поля назви
    void ApplyTitle(NoteTheme t, float pt, string face)
    {
        titlePanel.BackColor = t.Body;
        titleBox.BackColor = t.Body;
        titleBox.ForeColor = t.Fg;
        float tpt = pt * 1.3f;
        string key = face + "|" + tpt.ToString(CultureInfo.InvariantCulture) + "|" + t.FontName;
        if (key != titleKey)
        {
            titleKey = key;
            try
            {
                if (RichFmt.Native)
                {
                    titleBox.Font = new Font("Segoe UI", tpt, FontStyle.Bold, GraphicsUnit.Point);
                    IntPtr old = titleHF;
                    titleHF = RichFmt.MakeHFont(face, (int)Math.Round(tpt * DpiY / 72f), true);
                    if (titleBox.IsHandleCreated) RichFmt.SetControlFont(titleBox, titleHF);
                    RichFmt.FreeHFont(old);
                }
                else titleBox.Font = Util.MakeFont(t.FontName, tpt, FontStyle.Regular);
            }
            catch { }
        }
        // панель назви підлаштовується під висоту поля (лінія знизу не має ховатися під ним)
        int need = Math.Max(Notes.Px(30), titleBox.PreferredHeight + Px(10));
        if (titlePanel.Height != need) titlePanel.Height = need;
        LayoutTitle();
        titlePanel.Invalidate();
    }

    void LayoutTitle()
    {
        int h = titleBox.PreferredHeight;
        titleBox.SetBounds(Px(2), Math.Max(0, (titlePanel.Height - 1 - h) / 2), Math.Max(10, titlePanel.Width - Px(4)), h);
    }

    // заокруглені кути вікна редагування
    void UpdateRegion()
    {
        if (sizing) return;
        try
        {
            NoteTheme t = Notes.ThemeOf(n);
            int rad = Px(t.Radius);
            if (n.Locked || rad <= 1 || t.Frame == "sketch")
            {
                Util.SetRegion(this, (Region)null);
                return;
            }
            using (GraphicsPath p = Util.Round(new Rectangle(0, 0, Width, Height), rad)) Util.SetRegion(this, p);
        }
        catch { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (n == null || n.Locked) return;
        NoteTheme t = Notes.ThemeOf(n);
        NoteFrame.Draw(e.Graphics, new Rectangle(0, 0, Width, Height), t, Color.Transparent, n.Id.GetHashCode());
    }

    // послідовні ділянки з однаковим стилем: {початок, довжина, стиль, емодзі(0/1)}
    List<int[]> CollectRuns()
    {
        List<int[]> runs = new List<int[]>();
        int len = box.TextLength;
        string all = box.Text;
        for (int i = 0; i < len; i++)
        {
            box.Select(i, 1);
            Font f = box.SelectionFont;
            int st = f != null ? ((int)f.Style & 15) : 0;
            int em = f != null && f.Name == EmojiSets.Font && i < all.Length && EmojiSets.NeedsSym(all[i]) ? 1 : 0;
            if (runs.Count > 0)
            {
                int[] last = runs[runs.Count - 1];
                if (last[2] == st && last[3] == em && last[0] + last[1] == i) { last[1]++; continue; }
            }
            runs.Add(new int[] { i, 1, st, em });
        }
        return runs;
    }

    void Redraw(bool on)
    {
        try
        {
            if (box.IsHandleCreated) Win.SendMessage(box.Handle, 0x000B, on ? 1 : 0, 0);   // WM_SETREDRAW
            if (on) box.Invalidate();
        }
        catch { }
    }

    // ---------- форматування по символах ----------

    void ToggleStyle(FontStyle fs)
    {
        if (RichFmt.Native)
        {
            // напряму через формат символів: шрифт і розмір виділеного не змінюються
            uint bit = fs == FontStyle.Bold ? RichFmt.CFM_BOLD : fs == FontStyle.Italic ? RichFmt.CFM_ITALIC
                     : fs == FontStyle.Underline ? RichFmt.CFM_UNDERLINE : RichFmt.CFM_STRIKEOUT;
            try { RichFmt.SetEffect(box.Handle, bit, !RichFmt.Effect(box.Handle, bit)); } catch { }
            Notes.Touch();
            SyncToolbar();
            QueueEmoji(true);
            box.Focus();
            return;
        }
        try
        {
            Font f = box.SelectionFont;
            if (box.SelectionLength == 0)
            {
                Font cur = f ?? box.Font;
                box.SelectionFont = new Font(cur, cur.Style ^ fs);
            }
            else if (f != null)
            {
                box.SelectionFont = new Font(f, f.Style ^ fs);
            }
            else
            {
                int s0 = box.SelectionStart, l0 = box.SelectionLength;
                bool allOn = true;
                Redraw(false);
                for (int i = s0; i < s0 + l0; i++)
                {
                    box.Select(i, 1);
                    Font c = box.SelectionFont;
                    if (c != null && (c.Style & fs) == 0) { allOn = false; break; }
                }
                bool on = !allOn;
                for (int i = s0; i < s0 + l0; i++)
                {
                    box.Select(i, 1);
                    Font c = box.SelectionFont;
                    if (c == null) continue;
                    FontStyle ns = on ? (c.Style | fs) : (c.Style & ~fs);
                    if (ns != c.Style) box.SelectionFont = new Font(c, ns);
                }
                box.Select(s0, l0);
                Redraw(true);
            }
        }
        catch { Redraw(true); }
        Notes.Touch();
        SyncToolbar();
        box.Focus();
    }

    void ToggleBullets()
    {
        try
        {
            box.BulletIndent = Px(16);
            box.SelectionBullet = !box.SelectionBullet;
        }
        catch { }
        Notes.Touch();
        SyncToolbar();
        QueueEmoji(true);     // відступ списку зсуває текст - картинки емодзі мають піти за ним
        box.Focus();
    }

    void SyncToolbar()
    {
        if (RichFmt.Native)
        {
            try
            {
                RichFmt.CF c;
                if (RichFmt.Get(box.Handle, out c))
                {
                    bB.Active = (c.dwMask & RichFmt.CFM_BOLD) != 0 && (c.dwEffects & RichFmt.CFM_BOLD) != 0;
                    bI.Active = (c.dwMask & RichFmt.CFM_ITALIC) != 0 && (c.dwEffects & RichFmt.CFM_ITALIC) != 0;
                    bU.Active = (c.dwMask & RichFmt.CFM_UNDERLINE) != 0 && (c.dwEffects & RichFmt.CFM_UNDERLINE) != 0;
                    bS.Active = (c.dwMask & RichFmt.CFM_STRIKEOUT) != 0 && (c.dwEffects & RichFmt.CFM_STRIKEOUT) != 0;
                }
                bL.Active = box.SelectionBullet;
            }
            catch { }
            return;
        }
        try
        {
            Font f = box.SelectionFont;
            bB.Active = f != null && f.Bold;
            bI.Active = f != null && f.Italic;
            bU.Active = f != null && f.Underline;
            bS.Active = f != null && f.Strikeout;
            bL.Active = box.SelectionBullet;
        }
        catch { }
    }

    void OnBoxKey(object s, KeyEventArgs e)
    {
        if (!e.Control) return;
        if (e.KeyCode == Keys.B) { ToggleStyle(FontStyle.Bold); e.SuppressKeyPress = true; e.Handled = true; }
        else if (e.KeyCode == Keys.I) { ToggleStyle(FontStyle.Italic); e.SuppressKeyPress = true; e.Handled = true; }
        else if (e.KeyCode == Keys.U) { ToggleStyle(FontStyle.Underline); e.SuppressKeyPress = true; e.Handled = true; }
        else if (e.KeyCode == Keys.T) { ToggleStyle(FontStyle.Strikeout); e.SuppressKeyPress = true; e.Handled = true; }
        else if (e.Shift && e.KeyCode == Keys.L) { ToggleBullets(); e.SuppressKeyPress = true; e.Handled = true; }
        else if (e.KeyCode == Keys.A) { box.SelectAll(); e.SuppressKeyPress = true; e.Handled = true; }
    }


    // ---------- вставка символів: емодзі та весь Unicode (SymbolPicker) ----------

    SymbolPicker picker;

    void ShowEmoji()
    {
        if (Environment.TickCount - SymbolPicker.LastClosed < 250) return;   // клік по кнопці закрив панель - не відкриваємо знову
        if (picker != null && !picker.IsDisposed) { picker.Close(); picker = null; return; }
        NoteTheme t = Notes.ThemeOf(n);
        SymbolPicker pk = new SymbolPicker(t, delegate(string s) { InsertSymbol(s); });
        Point pt = bE.PointToScreen(new Point(bE.Width / 2, 0));
        Rectangle wa = Screen.FromPoint(pt).WorkingArea;
        int x = Math.Max(wa.Left, Math.Min(pt.X - pk.Width / 2, wa.Right - pk.Width));
        int y = pt.Y - pk.Height - Px(6);
        if (y < wa.Top) y = Math.Min(wa.Bottom - pk.Height, pt.Y + bE.Height + Px(6));
        pk.StartPosition = FormStartPosition.Manual;
        pk.Location = new Point(x, y);
        pk.FormClosed += delegate { try { box.Focus(); } catch { } };
        picker = pk;
        pk.Show(this);
    }

    // Символи вставляються по одному. Емодзі та інші символи йдуть через RTF з явним шрифтом: так RichEdit
    // надійно склеює сурогатні пари (інакше замість емодзі іноді лишається "▯▯"). Кольорове емодзі потім
    // показується картинкою поверх цього символу (див. RefreshEmoji).
    void InsertSymbol(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        EmojiSets.AddRecent(s);
        try
        {
            for (int i = 0; i < s.Length; )
            {
                int w = char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? 2 : 1;
                InsertOne(s.Substring(i, w));
                i += w;
            }
        }
        catch { }
        Notes.Touch();
        QueueEmoji(true);
    }

    // розмір (pt) у місці вставки
    float CurPt()
    {
        float pt = -1f;
        try
        {
            if (RichFmt.Native) pt = RichFmt.SizeOf(box.Handle);
            else { Font f = box.SelectionFont; if (f != null) pt = f.SizeInPoints; }
        }
        catch { }
        if (pt <= 0f) pt = themeBase > 0f ? themeBase : box.Font.SizeInPoints;
        return pt;
    }

    void InsertOne(string c)
    {
        fmtBusy++;
        try
        {
            NoteTheme t = Notes.ThemeOf(n);
            if (!EmojiSets.NeedsSym(c[0]))
            {
                box.SelectedText = c;
                return;
            }
            float pt = CurPt();
            Color fg = t.Fg;
            int start = box.SelectionStart;
            StringBuilder sb = new StringBuilder();
            sb.Append("{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0\\fnil\\fcharset0 " + EmojiSets.Font + ";}}");
            sb.Append("{\\colortbl ;\\red" + fg.R + "\\green" + fg.G + "\\blue" + fg.B + ";}");
            sb.Append("\\cf1\\f0\\fs" + (int)Math.Round(pt * 2) + " ");
            foreach (char ch in c) sb.Append("\\u" + (short)ch + "?");
            sb.Append("}");
            box.SelectedRtf = sb.ToString();
            int end = box.SelectionStart;
            if (end <= start) end = Math.Min(box.TextLength, start + c.Length);
            // страховка: якщо RTF не дав потрібного тексту, вставляємо напряму
            string got = start >= 0 && end <= box.TextLength && end > start ? box.Text.Substring(start, end - start) : "";
            if (got != c)
            {
                box.Select(start, Math.Max(0, end - start));
                box.SelectedText = c;
                end = start + c.Length;
            }
            // сам символ: шрифт символів і розмір тексту навколо
            box.Select(start, end - start);
            if (RichFmt.Native)
            {
                RichFmt.SetFace(box.Handle, RichFmt.SCF_SELECTION, EmojiSets.Font);
                RichFmt.SetSize(box.Handle, RichFmt.SCF_SELECTION, pt);
            }
            else
            {
                Font cur = box.SelectionFont;
                if (cur == null || cur.Name != EmojiSets.Font)
                    box.SelectionFont = new Font(EmojiSets.Font, pt, FontStyle.Regular, GraphicsUnit.Point);
            }
            box.SelectionColor = fg;
            // далі набираємо шрифтом теми
            box.Select(end, 0);
            if (RichFmt.Native)
            {
                if (themeFace.Length > 0) RichFmt.SetFace(box.Handle, RichFmt.SCF_SELECTION, themeFace);
                RichFmt.SetSize(box.Handle, RichFmt.SCF_SELECTION, pt);
            }
            else box.SelectionFont = box.Font;
            box.SelectionColor = fg;
        }
        catch { }
        finally { fmtBusy--; }
    }

    // ---------- кольорові емодзі: картинки поверх символів ----------
    // RichEdit малює емодзі лише однотонно, тому справжній символ лишається в тексті (копіюється, шукається),
    // а над ним показується кольорова картинка. У закріпленій нотатці картинки малюються в піксельний знімок.

    void QueueEmoji(bool rebuild)
    {
        if (rebuild) emoDirty = true;
        if (emoQueued || !IsHandleCreated) return;
        emoQueued = true;
        try { BeginInvoke(new Action(RefreshEmoji)); } catch { emoQueued = false; }
    }

    void RebuildEmoji()
    {
        emoDirty = false;
        emo.Clear();
        if (!ColorEmoji.Ready) return;
        string all = box.Text;
        List<int[]> found = new List<int[]>();
        for (int i = 0; i < all.Length; )
        {
            int cp = ColorEmoji.Cp(all, i);
            int w = cp > 0xFFFF ? 2 : 1;
            if (cp >= 0x2190 && ColorEmoji.Has(cp)) found.Add(new int[] { i, w, cp, 0 });
            i += w;
        }
        if (found.Count == 0) return;
        int ss = 0, sl = 0;
        try { ss = box.SelectionStart; sl = box.SelectionLength; } catch { }
        fmtBusy++;
        try
        {
            foreach (int[] f in found)
            {
                float pt = -1f;
                try
                {
                    Sel(f[0], f[1]);
                    if (RichFmt.Native) pt = RichFmt.SizeOf(box.Handle);
                    else { Font sf = box.SelectionFont; if (sf != null) pt = sf.SizeInPoints; }
                }
                catch { }
                if (pt <= 0f) pt = themeBase > 0f ? themeBase : box.Font.SizeInPoints;
                f[3] = (int)Math.Round(pt * 20f);
                emo.Add(f);
            }
        }
        finally
        {
            try { Sel(ss, sl); } catch { }
            fmtBusy--;
        }
    }

    // місце символу в клієнтських координатах RichEdit
    bool EmojiRect(int[] e, out Rectangle r)
    {
        r = Rectangle.Empty;
        try
        {
            Point p = box.GetPositionFromCharIndex(e[0]);
            Point q = box.GetPositionFromCharIndex(e[0] + e[1]);
            float em = e[3] / 20f * DpiY / 72f;
            int lineH = (int)Math.Round(em * 1.36f);
            int adv = (q.Y == p.Y && q.X > p.X) ? q.X - p.X : (int)Math.Round(em * 1.15f);
            adv = Math.Max((int)Math.Round(em * 0.9f), Math.Min(adv, (int)Math.Round(em * 1.5f)));
            r = new Rectangle(p.X, p.Y, adv, lineH);
            return true;
        }
        catch { return false; }
    }

    void HidePics(int from)
    {
        for (int i = from; i < pics.Count; i++) if (pics[i].Visible) pics[i].Visible = false;
    }

    void RefreshEmoji()
    {
        emoQueued = false;
        if (emoBusy) return;
        emoBusy = true;
        bool force = emoForce; emoForce = false;
        try
        {
            if (!ColorEmoji.Ready || !IsHandleCreated || !box.IsHandleCreated || !box.Visible || !Visible) { HidePics(0); return; }
            if (emoDirty) RebuildEmoji();
            Rectangle cr = box.ClientRectangle;
            Color bgc = box.BackColor;
            int used = 0;
            foreach (int[] e in emo)
            {
                Rectangle r;
                if (!EmojiRect(e, out r)) continue;
                if (r.Bottom < 0 || r.Top > cr.Height || r.Right < 0 || r.Left > cr.Width) continue;
                EmojiPic pic;
                if (used < pics.Count) pic = pics[used];
                else { pic = new EmojiPic(); pics.Add(pic); box.Controls.Add(pic); }
                pic.Place(ColorEmoji.Get(e[2]), e[2].ToString(), bgc, r);
                if (force) pic.Refresh();     // RichEdit міг домалювати однотонний символ поверх картинки
                used++;
            }
            HidePics(used);
        }
        catch { }
        finally { emoBusy = false; }
    }

    // колір самих символів-емодзі (на час знімка вони «зникають»: чорні на чорному, білі на білому)
    void TintEmoji(Color c)
    {
        try
        {
            if (emoDirty) RebuildEmoji();
            if (emo.Count == 0) return;
            int ss = 0, sl = 0;
            try { ss = box.SelectionStart; sl = box.SelectionLength; } catch { }
            fmtBusy++;
            try
            {
                foreach (int[] e in emo) { Sel(e[0], e[1]); box.SelectionColor = c; }
            }
            finally
            {
                try { Sel(ss, sl); } catch { }
                fmtBusy--;
            }
        }
        catch { }
    }

    // кольорові картинки емодзі в піксельний знімок закріпленої нотатки
    void OverlayEmoji(byte[] pm, int w, int h)
    {
        try
        {
            if (!ColorEmoji.Ready) return;
            if (emoDirty) RebuildEmoji();
            if (emo.Count == 0) return;
            IntPtr hb = box.Handle;
            int first = 0;
            if (RichFmt.Native)
            {
                first = (int)Win.SendMessage(hb, 0x00CE, 0, 0);                 // EM_GETFIRSTVISIBLELINE
                if (first > 0) Win.SendMessage(hb, 0x00B6, 0, -first);           // EM_LINESCROLL: знімок рахується з початку тексту
            }
            try
            {
                int ox = BodyPad, oy = BodyPad + PinTitleH();
                using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                {
                    Rectangle all = new Rectangle(0, 0, w, h);
                    BitmapData bd = bmp.LockBits(all, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                    Marshal.Copy(pm, 0, bd.Scan0, pm.Length);
                    bmp.UnlockBits(bd);
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SetClip(new Rectangle(ox, oy, Math.Max(1, w - 2 * BodyPad), Math.Max(1, h - oy - BodyPad)));
                        foreach (int[] e in emo)
                        {
                            Rectangle r;
                            if (!EmojiRect(e, out r)) continue;
                            int sz = Math.Min(r.Width, r.Height);
                            Rectangle d = new Rectangle(ox + r.X + (r.Width - sz) / 2, oy + r.Y + (r.Height - sz) / 2, sz, sz);
                            ColorEmoji.Draw(g, char.ConvertFromUtf32(e[2]), d);
                        }
                    }
                    bd = bmp.LockBits(all, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                    Marshal.Copy(bd.Scan0, pm, 0, pm.Length);
                    bmp.UnlockBits(bd);
                }
            }
            finally
            {
                if (first > 0) Win.SendMessage(hb, 0x00B6, 0, first);
            }
        }
        catch { }
    }

    // висота назви в закріпленій нотатці (порожня назва місця не займає)
    int PinTitleH() { return titleBox.Text.Trim().Length > 0 ? titlePanel.Height : 0; }

    // ---------- меню теми ----------

    void ShowThemeMenu(Control host, Point at)
    {
        ContextMenuStrip m = new ContextMenuStrip();
        NoteTheme panel = Notes.ById(Notes.PanelId) ?? Notes.Themes[0];
        ToolStripMenuItem auto = new ToolStripMenuItem(L.T("Як на панелі (") + panel.Title + ")");
        auto.Checked = n.Theme == "auto";
        auto.Image = NoteFrame.Swatch(panel);
        auto.Click += delegate { Notes.SetNoteTheme(n, "auto"); };
        m.Items.Add(auto);
        m.Items.Add(new ToolStripSeparator());
        foreach (NoteTheme t0 in Notes.Themes)
        {
            NoteTheme t = t0;
            ToolStripMenuItem it = new ToolStripMenuItem(t.Title);
            it.Checked = n.Theme == t.Id;
            it.Image = NoteFrame.Swatch(t);
            it.Click += delegate { Notes.SetNoteTheme(n, t.Id); };
            m.Items.Add(it);
        }
        m.Items.Add(new ToolStripSeparator());
        NoteTheme cur = Notes.ThemeOf(n);
        ToolStripMenuItem cust = new ToolStripMenuItem(L.T("Налаштувати тему «") + cur.Title + L.Q("»…"));
        cust.Image = NoteFrame.Swatch(cur);
        cust.Click += delegate { Notes.EditTheme(Notes.ThemeOf(n)); };
        m.Items.Add(cust);
        m.Closed += delegate { BeginInvoke(new Action(m.Dispose)); };
        m.Show(host, at);
    }

    void AskDelete()
    {
        if (MessageBox.Show(L.T("Видалити цю нотатку?"), "NeoBar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            Notes.Delete(n);
    }

    // ---------- стани: редагування / закріплено ----------

    public void Prepare()
    {
        IntPtr h = Handle;                   // створити вікно до показу
        busy++;
        try { if (n.Locked) ApplyLocked(); }
        finally { busy--; }
    }

    public void Edit()
    {
        SetLocked(false);
        try { box.Focus(); box.Select(box.TextLength, 0); } catch { }
    }

    // показати нотатку (зі списку): редагована - наперед і з фокусом, закріплена лишається закріпленою
    public void Reveal()
    {
        try
        {
            if (!Visible) Show();
            if (n.Locked) return;
            Win.SetWindowPos(Handle, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            BringToFront();
            Activate();
            Repaint();
        }
        catch { }
    }

    // повне перемалювання з усіма дочірніми елементами (після зміни стилю вікна шапка й панель самі не домальовуються)
    [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr h, IntPtr rc, IntPtr rgn, uint flags);

    void Repaint()
    {
        try
        {
            RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0100 | 0x0400);      // INVALIDATE | ERASE | ALLCHILDREN | UPDATENOW | FRAME
            BeginInvoke(new Action(delegate { try { if (!IsDisposed) { Invalidate(true); Update(); } } catch { } }));
        }
        catch { }
    }

    public void SetLocked(bool locked)
    {
        busy++;
        try
        {
            if (locked && !n.Locked && IsHandleCreated && Visible) PullRect();
            n.Locked = locked;
            if (locked) ApplyLocked(); else ApplyUnlocked();
        }
        finally { busy--; }
        Notes.Modified();
    }

    public void ReapplyLock()
    {
        if (!n.Locked) return;
        busy++;
        try { ApplyLocked(); }
        finally { busy--; }
    }

    // область тексту (екранні координати) - саме вона закріплюється (разом із рамкою)
    Rectangle TextRect()
    {
        int ins = NoteFrame.Inset(Notes.ThemeOf(n));
        return new Rectangle(n.R.X + ins, n.R.Y + ins + HeadH, Math.Max(Px(60), n.R.Width - 2 * ins),
                             Math.Max(Px(40), n.R.Height - 2 * ins - HeadH - ToolH));
    }

    void ApplyLocked()
    {
        try { box.Select(box.TextLength, 0); } catch { }
        Rectangle tr = TextRect();
        NoteTheme t = Notes.ThemeOf(n);
        bool ok = false;
        try { ok = LockPixels(tr, t); }
        catch { ok = false; }
        if (!ok) LockOpaque(tr);
        SendToBottom();
    }

    // піксельне зображення тексту на тематичному фоні (прозорий лише фон); null - не вдалося
    byte[] BuildPixels(int w, int h, NoteTheme t)
    {
        if (w < 20 || h < 20) return null;
        IntPtr hb = box.Handle;                       // гарантуємо створення вікна RichEdit
        byte[] k, wh;
        try
        {
            // RichEdit сам малює підкладку під рядками кольором BackColor - тому на час
            // знімка вона має збігатися з тлом (чорним/білим), інакше «внутрішній фон» стане непрозорим
            box.BackColor = Color.Black;
            TintEmoji(Color.Black);
            k = CaptureText(Color.Black, w, h, t);
            box.BackColor = Color.White;
            TintEmoji(Color.White);
            wh = CaptureText(Color.White, w, h, t);
        }
        finally { box.BackColor = t.Body; TintEmoji(t.Fg); }
        if (k == null || wh == null) return null;
        if (k[0] > 12 || k[1] > 12 || k[2] > 12 || wh[0] < 243 || wh[1] < 243 || wh[2] < 243) return null;
        if (n.Text.Trim().Length > 0)
        {
            bool any = false;
            for (int i = 0; i < k.Length && !any; i += 4)
                if (k[i] > 40 || k[i + 1] > 40 || k[i + 2] > 40 || wh[i] < 215 || wh[i + 1] < 215 || wh[i + 2] < 215) any = true;
            if (!any) return null;
        }
        byte[] layer = Notes.BackLayer(w, h, t, n.Id.GetHashCode());
        byte[] pm = Notes.Composite(k, wh, w, h, layer, Notes.OpacityOf(n));
        OverlayEmoji(pm, w, h);
        return pm;
    }

    // закріплення: картинка з попіксельною альфою - прозорий лише фон, текст і рамка чіткі
    bool LockPixels(Rectangle tr, NoteTheme t)
    {
        int w = tr.Width, h = tr.Height;
        byte[] pm = BuildPixels(w, h, t);
        if (pm == null) return false;
        Notes.ApplyFrame(pm, w, h, t, n.Id.GetHashCode());

        SetChildrenVisible(false);
        Padding = Padding.Empty;
        if (Region != null) { Region.Dispose(); Region = null; }
        MinimumSize = Size.Empty;
        SetBounds(tr.X, tr.Y, w, h);
        ResetLayered(true);
        return PresentTo(Handle, pm, w, h, tr.X, tr.Y);
    }

    // ---------- перегляд прозорості під час перетягування повзунка ----------
    // Область тексту вирізається з вікна редагування, а над нею показується той самий
    // піксельний малюнок, що й у закріпленої нотатки: прозорий лише фон, текст не змінюється.
    PreviewWin pw;
    Point pwPos;

    void StartPreview()
    {
        EndPreview();
        try
        {
            Rectangle br = bodyPanel.Bounds;
            pwPos = PointToScreen(br.Location);
            pw = new PreviewWin();
            pw.SetBounds(pwPos.X, pwPos.Y, br.Width, br.Height);
            pw.Show(this);
            if (!UpdatePreview()) { EndPreview(); return; }
            Region hole = Region != null ? Region.Clone() : new Region(new Rectangle(0, 0, Width, Height));
            hole.Exclude(br);
            Util.SetRegion(this, hole);
            bodyPanel.Visible = false;
        }
        catch { EndPreview(); }
    }

    bool UpdatePreview()
    {
        if (pw == null) return false;
        try
        {
            Rectangle br = bodyPanel.Bounds;
            byte[] pm = BuildPixels(br.Width, br.Height, Notes.ThemeOf(n));
            if (pm == null) return false;
            return PresentTo(pw.Handle, pm, br.Width, br.Height, pwPos.X, pwPos.Y);
        }
        catch { return false; }
    }

    void EndPreview()
    {
        if (pw != null)
        {
            try { pw.Close(); pw.Dispose(); } catch { }
            pw = null;
            try { bodyPanel.Visible = true; UpdateRegion(); Invalidate(true); } catch { }
        }
    }

    // аварійний варіант, якщо картинку не вдалося створити: звичайне непрозоре вікно з текстом
    void LockOpaque(Rectangle tr)
    {
        box.ReadOnly = true;
        titleBox.ReadOnly = true;
        headerPanel.Visible = false;
        toolbar.Visible = false;
        bodyPanel.Visible = true;
        box.Visible = true;
        Padding = Padding.Empty;
        if (Region != null) { Region.Dispose(); Region = null; }
        MinimumSize = Size.Empty;
        SetBounds(tr.X, tr.Y, tr.Width, tr.Height);
        ResetLayered(false);
    }

    void ApplyUnlocked()
    {
        ResetLayered(false);
        EndPreview();
        MinimumSize = new Size(MinW, MinH);
        Padding = new Padding(NoteFrame.Inset(Notes.ThemeOf(n)));
        SetBounds(n.R.X, n.R.Y, n.R.Width, n.R.Height);
        SetChildrenVisible(true);
        box.ReadOnly = false;
        titleBox.ReadOnly = false;
        QueueEmoji(true);
        UpdateRegion();
        try { Win.SetWindowPos(Handle, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW); } catch { }
        BringToFront();
        Activate();
        Invalidate(true);
        Repaint();
    }

    void SetChildrenVisible(bool on)
    {
        headerPanel.Visible = on;
        toolbar.Visible = on;
        bodyPanel.Visible = on;
        box.Visible = on;
    }

    // WS_EX_LAYERED треба скинути й виставити наново перед UpdateLayeredWindow
    void ResetLayered(bool locked)
    {
        try
        {
            int ex = GetWindowLong(Handle, GWL_EXSTYLE);
            ex &= ~(WS_EX_LAYERED | WS_EX_TRANSPARENT | 0x02000000);
            SetWindowLong(Handle, GWL_EXSTYLE, ex);
            ex |= WS_EX_TOOLWINDOW;
            if (locked) ex |= WS_EX_LAYERED | WS_EX_TRANSPARENT; else ex |= 0x02000000;
            SetWindowLong(Handle, GWL_EXSTYLE, ex);
        }
        catch { }
    }

    public void SendToBottom()
    {
        if (!IsHandleCreated || !n.Locked) return;
        try { Win.SetWindowPos(Handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); }
        catch { }
    }

    // ---------- рендер тексту RichEdit у картинку (EM_FORMATRANGE) ----------

    byte[] CaptureText(Color bg, int w, int h, NoteTheme t)
    {
        using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(bg);
                int th = PinTitleH();
                if (th > 0) DrawPinnedTitle(g, w, th, t);
                RenderBox(g, new Rectangle(BodyPad, BodyPad + th, w - 2 * BodyPad, h - 2 * BodyPad - th));
            }
            return Notes.ReadBits(bmp);
        }
    }

    // назва нотатки у знімку: той самий шрифт і підкреслення, що й у вікні редагування
    void DrawPinnedTitle(Graphics g, int w, int th, NoteTheme t)
    {
        Rectangle r = new Rectangle(BodyPad + Px(2) + 1, BodyPad, Math.Max(10, w - 2 * BodyPad - Px(4)), th - 1);
        string text = titleBox.Text.Trim();
        if (RichFmt.Native && titleHF != IntPtr.Zero) RichFmt.DrawText(g, titleHF, text, r, t.Fg);
        else TextRenderer.DrawText(g, text, titleBox.Font, r, t.Fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        using (Pen p = new Pen(Util.Blend(t.Body, t.Fg, 0.28f)))
            g.DrawLine(p, BodyPad, BodyPad + th - 1, w - BodyPad, BodyPad + th - 1);
    }

    void RenderBox(Graphics g, Rectangle r)
    {
        IntPtr hdc = g.GetHdc();
        IntPtr p = IntPtr.Zero;
        try
        {
            int dpi = GetDeviceCaps(hdc, 88);            // LOGPIXELSX
            if (dpi <= 0) dpi = 96;
            FMTRANGE fr = new FMTRANGE();
            fr.hdc = hdc; fr.hdcTarget = hdc;
            fr.rc.left = r.Left * 1440 / dpi;  fr.rc.top = r.Top * 1440 / dpi;
            fr.rc.right = r.Right * 1440 / dpi; fr.rc.bottom = r.Bottom * 1440 / dpi;
            fr.rcPage = fr.rc;
            fr.chrg.cpMin = 0; fr.chrg.cpMax = -1;
            p = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(FMTRANGE)));
            Marshal.StructureToPtr(fr, p, false);
            Win.SendMessage(box.Handle, 0x0439, (IntPtr)1, p);               // EM_FORMATRANGE (малювати)
            Win.SendMessage(box.Handle, 0x0439, IntPtr.Zero, IntPtr.Zero);   // звільнити кеш
        }
        finally
        {
            if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
            g.ReleaseHdc(hdc);
        }
    }

    internal static bool PresentTo(IntPtr hwnd, byte[] pm, int w, int h, int x, int y)
    {
        IntPtr screen = Win.GetDC(IntPtr.Zero);
        IntPtr mem = Win.CreateCompatibleDC(screen);
        IntPtr dib = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            IntPtr bits;
            dib = Win.CreateDib32(w, h, out bits);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return false;
            Marshal.Copy(pm, 0, bits, pm.Length);
            old = Win.SelectObject(mem, dib);
            Win.POINT pos = new Win.POINT(); pos.x = x; pos.y = y;
            Win.SIZE sz = new Win.SIZE(); sz.cx = w; sz.cy = h;
            Win.POINT src = new Win.POINT();
            Win.BLENDFUNCTION bl = new Win.BLENDFUNCTION(); bl.BlendOp = 0; bl.SourceConstantAlpha = 255; bl.AlphaFormat = 1;   // AC_SRC_OVER, AC_SRC_ALPHA
            return Win.UpdateLayeredWindow(hwnd, screen, ref pos, ref sz, mem, ref src, 0, ref bl, 2);   // ULW_ALPHA
        }
        finally
        {
            if (old != IntPtr.Zero) Win.SelectObject(mem, old);
            if (dib != IntPtr.Zero) Win.DeleteObject(dib);
            Win.DeleteDC(mem);
            Win.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // ---------- дані ----------

    void PullRect()
    {
        if (WindowState == FormWindowState.Normal && !n.Locked) n.R = Bounds;
    }

    public void Pull()
    {
        if (!n.Locked && IsHandleCreated && Visible) PullRect();
        try { n.Title = titleBox.Text; n.Text = box.Text; n.Rtf = box.Rtf; } catch { }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEACTIVATE = 0x0021;
        const int MA_NOACTIVATE = 3;
        const int WM_SYSCOMMAND = 0x0112;
        const int SC_MINIMIZE = 0xF020;

        if (m.Msg == WM_SYSCOMMAND && ((int)m.WParam & 0xFFF0) == SC_MINIMIZE) return;   // нотатку не згортаємо
        if (n != null && n.Locked && m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { tip.Dispose(); } catch { } }
        base.Dispose(disposing);
        try { RichFmt.FreeHFont(titleHF); titleHF = IntPtr.Zero; } catch { }
    }
}

// ---------- вікно перегляду прозорості (над вирізаною областю тексту) ----------

sealed class PreviewWin : Form
{
    public PreviewWin()
    {
        FormBorderStyle = FormBorderStyle.None;
        AppIcon.Apply(this);
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x00080000 | 0x00000020 | 0x00000080 | 0x08000000;   // LAYERED, TRANSPARENT, TOOLWINDOW, NOACTIVATE
            return cp;
        }
    }
}

// ---------- емодзі ----------
