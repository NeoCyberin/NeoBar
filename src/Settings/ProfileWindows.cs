using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

// Вікна профілів: вибір, редагування, керування, видалення

// ----------------------------------------------------------------------
//  Вікно вибору профілю (до запуску панелі та після «Змінити профіль»)
// ----------------------------------------------------------------------
sealed class ProfilePicker : SbWindow
{
    public Profile Chosen;
    Profile pinFor;
    string err = "";
    bool showPin;
    readonly TextBox pin;

    static float ScreenScale()
    {
        try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return Math.Max(1f, g.DpiX / 96f); } catch { return 1f; }
    }

    const int Cols = 6;     // карток у ряду; профілів не більше 12, тож вікно лишається одного розміру завжди

    public ProfilePicker(Profile last) : base(Prof.ThemeOf(last ?? (Prof.All.Count > 0 ? Prof.All[0] : null)), ScreenScale(), "NeoBar", 868, 578)
    {
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        Text = L.T("NeoBar - вибір профілю");
        pin = MakeInput();
        pin.UseSystemPasswordChar = true;
        pin.MaxLength = 32;
        pin.KeyDown += delegate (object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; TryPin(); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; CancelPin(); }
        };
        pin.TextChanged += delegate { if (err.Length > 0) { err = ""; Invalidate(); } };
        if (last != null && last.HasPin) pinFor = last;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle &= ~0x80;        // не вікно-інструмент: видно на панелі завдань
            cp.ExStyle |= 0x40000;      // WS_EX_APPWINDOW
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        if (pinFor != null) { pin.Visible = true; pin.Focus(); }
    }

    void Pick(Profile p)
    {
        if (p.HasPin) { pinFor = p; err = ""; pin.Text = ""; pin.Visible = true; Invalidate(); BeginInvoke(new Action(delegate { pin.Focus(); })); return; }
        Chosen = p;
        Close();
    }

    void TryPin()
    {
        if (pinFor == null) return;
        if (Prof.Check(pinFor, pin.Text)) { Chosen = pinFor; Close(); return; }
        err = Prof.PinErr(pinFor, L.T("Невірний PIN"));
        pin.SelectAll();
        Invalidate();
    }

    void CancelPin()
    {
        pinFor = null; err = ""; pin.Text = ""; pin.Visible = false; Invalidate();
    }

    void Edit(Profile p)
    {
        if (p == null && Prof.All.Count >= Prof.MaxProfiles) return;
        using (ProfileEdit f = new ProfileEdit(T, Sc, p))
        {
            f.StartPosition = FormStartPosition.CenterParent;
            f.ShowDialog(this);
        }
        if (pinFor != null && Prof.ById(pinFor.Id) == null) CancelPin();
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && pinFor != null) { CancelPin(); return; }
        base.OnKeyDown(e);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        TxtC(g, L.T("Хто працює з NeoBar?"), FBig, new Rectangle(r.X, r.Y + Px(6), r.Width, Px(42)), P.Fg);
        TxtC(g, L.T("Оберіть профіль. Правий клік по профілю - змінити його."), F, new Rectangle(r.X, r.Y + Px(48), r.Width, Px(22)), P.Sub);

        bool canAdd = Prof.All.Count < Prof.MaxProfiles;
        int n = Prof.All.Count + (canAdd ? 1 : 0), rows = (n + Cols - 1) / Cols;
        int cw = Px(118), ch = Px(138), gap = Px(14);
        int y0 = r.Y + Px(88) + (2 - rows) * Px(76);           // порожні ряди не лишають діру: профілі стоять посередині області карток
        for (int i = 0; i < n; i++)
        {
            int row = i / Cols, col = i % Cols;
            int inRow = Math.Min(Cols, n - row * Cols);
            int rowW = inRow * cw + (inRow - 1) * gap;
            Rectangle c = new Rectangle(r.X + (r.Width - rowW) / 2 + col * (cw + gap), y0 + row * (ch + gap), cw, ch);
            if (i < Prof.All.Count) ProfileCard(g, c, Prof.All[i]);
            else AddCard(g, c);
        }

        int by = r.Y + Px(88) + 2 * (ch + gap) + Px(4);
        if (pinFor != null)
        {
            TxtC(g, L.T("PIN профілю «") + pinFor.Name + L.Q("»"), FB, new Rectangle(r.X, by, r.Width, Px(22)), P.Fg);
            int fw = Px(200), bw = Px(96);
            int eye = Px(34);
            Rectangle fr = new Rectangle(r.X + (r.Width - fw - bw - eye - Px(16)) / 2, by + Px(26), fw, Px(34));
            PlaceInput(g, pin, fr);
            IconBtn(g, new Rectangle(fr.Right + Px(8), fr.Y, eye, fr.Height), showPin ? "eyeoff" : "eye", delegate { showPin = !showPin; pin.UseSystemPasswordChar = !showPin; Invalidate(); }, false);
            Btn(g, new Rectangle(fr.Right + eye + Px(16), fr.Y, bw, fr.Height), L.T("Увійти"), TryPin, 1, true);
            if (err.Length > 0) TxtC(g, err, F, new Rectangle(r.X, fr.Bottom + Px(4), r.Width, Px(20)), P.Bad);
        }
        else if (pin.Visible) pin.Visible = false;

        Rectangle quit = new Rectangle(r.X, r.Bottom - Px(32), Px(150), Px(32));
        Btn(g, quit, L.T("Вийти з NeoBar"), delegate { Chosen = null; Close(); }, 2, true);
    }

    void ProfileCard(Graphics g, Rectangle c, Profile p)
    {
        bool h = Over(c), sel = p == pinFor;
        if (h || sel) Card(g, c, sel ? Util.Blend(P.Bg, P.Acc, 0.22f) : P.Card);
        int a = Px(74);
        Rectangle av = new Rectangle(c.X + (c.Width - a) / 2, c.Y + Px(12), a, a);
        ProfileArt.Avatar(g, av, p, h || sel, P.Acc);
        TxtC(g, p.Name, FB, new Rectangle(c.X + Px(4), av.Bottom + Px(8), c.Width - Px(8), Px(22)), P.Fg);
        if (p.HasPin) Ico.Draw(g, "lock", new RectangleF(c.X + (c.Width - Px(12)) / 2f, av.Bottom + Px(32), Px(12), Px(12)), P.Sub);
        Profile k = p;
        HotR(c, delegate { Pick(k); }, delegate { Edit(k); });
    }

    void AddCard(Graphics g, Rectangle c)
    {
        bool h = Over(c);
        if (h) Card(g, c);
        int a = Px(74);
        Rectangle av = new Rectangle(c.X + (c.Width - a) / 2, c.Y + Px(12), a, a);
        using (Pen pn = new Pen(h ? P.Acc : Util.Blend(P.Bg, P.Fg, 0.35f), Math.Max(1.5f, Px(2))) { DashStyle = DashStyle.Dash })
            g.DrawEllipse(pn, av);
        Ico.Draw(g, "plus", new RectangleF(av.X + a * 0.32f, av.Y + a * 0.32f, a * 0.36f, a * 0.36f), h ? P.Acc : P.Sub);
        TxtC(g, L.T("Новий профіль"), F, new Rectangle(c.X + Px(4), av.Bottom + Px(8), c.Width - Px(8), Px(22)), h ? P.Fg : P.Sub);
        Hot(c, delegate { Edit(null); });
    }
}

// ----------------------------------------------------------------------
//  Створення й зміна профілю: назва, колір, необов'язковий PIN
// ----------------------------------------------------------------------
sealed class ProfileEdit : SbWindow
{
    Profile p;                    // null - новий профіль
    readonly TextBox name, curPin, newPin, newPin2;
    bool showPin;
    int shade;                    // колір - місце в палітрі теми
    string style;
    Bitmap photoNew;              // вибране фото (збережеться після «Зберегти»)
    bool photoDrop;               // прибрати наявне фото
    bool dropPin;
    string err = "";
    public bool Changed;

    public ProfileEdit(Theme t, float sc, Profile edit) : base(t, sc, edit == null ? L.T("Новий профіль") : L.T("Профіль «") + edit.Name + L.Q("»"), 440, (edit != null && edit.HasPin ? 470 : 410) + 118)
    {
        Glyph = "\uE77B";
        p = edit;
        style = p != null ? ProfileArt.StyleOf(p) : "flat";      // новий профіль починає з темної теми
        shade = p != null ? (p.Shade >= 0 ? p.Shade : ProfileArt.Nearest(style, p.Color)) : Prof.All.Count % 8;
        name = MakeInput();
        name.MaxLength = 32;
        name.Text = p != null ? p.Name : "";
        if (p != null && p.HasPin) { curPin = MakeInput(); curPin.UseSystemPasswordChar = true; curPin.MaxLength = 32; }
        newPin = MakeInput();
        newPin.UseSystemPasswordChar = true;
        newPin.MaxLength = 32;
        newPin2 = MakeInput();
        newPin2.UseSystemPasswordChar = true;
        newPin2.MaxLength = 32;
        foreach (TextBox tb in new TextBox[] { name, curPin, newPin, newPin2 })
        {
            if (tb == null) continue;
            tb.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Ok(); } };
            tb.TextChanged += delegate { err = ""; Invalidate(); };       // піктограма (літера) в перегляді оновлюється під час набору назви
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (curPin != null) curPin.Focus(); else name.Focus();
    }

    void Ok()
    {
        string nm = name.Text.Trim();
        if (nm.Length == 0) { err = L.T("Вкажіть назву профілю"); Invalidate(); return; }
        if (Prof.NameTaken(nm, p)) { err = L.T("Профіль з такою назвою вже є"); Invalidate(); return; }
        if (p != null && p.HasPin && !Prof.Check(p, curPin.Text)) { err = Prof.PinErr(p, L.T("Поточний PIN невірний")); Invalidate(); return; }
        if (p == null && Prof.All.Count >= Prof.MaxProfiles) { Close(); return; }        // плюсик у цьому разі не малюється; запобіжник без повідомлення
        string np = dropPin ? "" : newPin.Text;
        if (np.Length > 0 && np.Length < 4) { err = L.T("PIN - щонайменше 4 символи"); Invalidate(); return; }
        if (np.Length > 0 && np != newPin2.Text) { err = L.T("PIN-коди не збігаються: повторіть його ще раз"); Invalidate(); return; }
        Profile target = p;
        Color color = ProfileArt.Palette(style)[shade];
        bool fresh = target == null;
        // спершу фото, потім зміни профілю: якщо фото не збереглося, нічого не змінюється (а новий профіль не лишається недоробленим)
        if (fresh) { target = Prof.Create(nm, np, color); target.Shade = shade; }
        bool photoSet = target.Photo;
        try
        {
            if (photoNew != null) { Prof.SavePhoto(target, photoNew); photoSet = true; }
            else if (photoDrop) { Prof.DropPhoto(target); photoSet = false; }
        }
        catch (Exception ex)
        {
            Util.ErrorLog(ex);
            // новий профіль скасовуємо повністю, щоб повторне натискання «Зберегти» не впиралось у «така назва вже є»
            if (fresh && !Prof.Delete(target)) p = target;
            err = L.T("Не вдалося зберегти фото");
            Invalidate();
            return;
        }
        if (!fresh)
        {
            target.Name = nm;
            target.Color = color;
            target.Shade = shade;
            if (dropPin) Prof.SetPin(target, "");
            else if (np.Length > 0) Prof.SetPin(target, np);
        }
        target.Photo = photoSet;
        Prof.Save();
        Changed = true;
        Close();
    }

    // показати / сховати все введене в полях PIN
    void ToggleShow()
    {
        showPin = !showPin;
        foreach (TextBox tb in new TextBox[] { curPin, newPin, newPin2 }) if (tb != null) tb.UseSystemPasswordChar = !showPin;
        Invalidate();
    }

    bool HasPhoto { get { return photoNew != null || (p != null && p.Photo && !photoDrop); } }

    void PickPhoto()
    {
        using (OpenFileDialog d = new OpenFileDialog())
        {
            d.Title = L.T("Фото профілю");
            d.Filter = L.T("Зображення|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Усі файли|*.*");
            if (d.ShowDialog(this) != DialogResult.OK) return;
            UsePhoto(d.FileName);
        }
    }

    // фото можна й перетягнути у вікно (з Провідника, браузера тощо)
    protected override bool AcceptsFiles { get { return true; } }

    protected override void FilesDropped(string[] files)
    {
        foreach (string f in files)
        {
            if (!File.Exists(f)) continue;
            string ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif" || ext == ".webp" || ext == ".tif" || ext == ".tiff" || ext == ".ico")
            {
                Activate();
                UsePhoto(f);
                return;
            }
        }
        err = L.T("Перетягніть зображення (PNG, JPG, BMP, GIF)");
        Invalidate();
    }

    void UsePhoto(string file)
    {
        try
        {
            err = "";
            using (Bitmap work = PhotoSrc.Load(file))
            using (AvatarCrop dlg = new AvatarCrop(T, Sc, work))
            {
                dlg.StartPosition = FormStartPosition.CenterParent;
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
                {
                    if (photoNew != null) photoNew.Dispose();
                    photoNew = dlg.Result;
                    photoDrop = false;
                }
            }
        }
        catch (InvalidOperationException ex) { err = ex.Message; }
        catch { err = L.T("Не вдалося відкрити зображення"); }
        Invalidate();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (photoNew != null) { photoNew.Dispose(); photoNew = null; }
        base.OnFormClosed(e);
    }

    void Remove()
    {
        if (p == null) return;
        if (p.HasPin && !Prof.Check(p, curPin.Text)) { err = Prof.PinErr(p, L.T("Щоб видалити, введіть поточний PIN")); Invalidate(); return; }
        if (MessageBox.Show(this, L.T("Видалити профіль «") + p.Name + L.T("» разом з усіма його налаштуваннями, нотатками й списком ігор?"),
                            "NeoBar", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        if (!Prof.Delete(p)) { err = L.T("Не вдалося видалити профіль"); Invalidate(); return; }
        Changed = true;
        Close();
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        int y = r.Y;
        int av = Px(72);
        Profile shown = new Profile();
        shown.Name = name.Text.Trim().Length > 0 ? name.Text : "?";
        shown.Shade = shade;
        shown.Style = style;
        bool photo = HasPhoto;
        Image ph = photoNew ?? (photo ? ProfileArt.PhotoImage(p) : null);
        ProfileArt.Avatar(g, new Rectangle(r.X, y, av, av), shown, false, P.Acc, ph, !photo);
        // кольори - палітра теми профілю (з фото - не діють)
        Color[] pal = ProfileArt.Palette(style);
        string[] names = ProfileArt.ShadeNames(style);
        int sw = Px(22), sg = Px(7), sx = r.X + av + Px(18);
        int over = -1;
        for (int i = 0; i < pal.Length; i++) if (Over(new Rectangle(sx + i * (sw + sg), y + Px(22), sw, sw))) over = i;
        string cap = photo ? L.T("Колір (з фото не діє)") : L.T("Колір: ") + names[over >= 0 ? over : shade];
        TxtL(g, cap, FS, new Rectangle(sx, y, r.Right - sx, Px(18)), over >= 0 && !photo ? P.Fg : P.Sub);
        for (int i = 0; i < pal.Length; i++)
        {
            Rectangle cr = new Rectangle(sx + i * (sw + sg), y + Px(22), sw, sw);
            Color c = photo ? Util.Blend(P.Bg, pal[i], 0.35f) : pal[i];
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(cr);
                using (PathGradientBrush pb = new PathGradientBrush(gp))
                {
                    pb.CenterPoint = new PointF(cr.X + cr.Width * 0.38f, cr.Y + cr.Height * 0.32f);
                    pb.CenterColor = Util.Blend(c, Color.White, 0.28f);
                    pb.SurroundColors = new Color[] { Util.Blend(c, Color.Black, 0.12f) };
                    g.FillPath(pb, gp);
                }
            }
            if (!photo && (i == shade || i == over))
                using (Pen pn = new Pen(i == shade ? P.Fg : Util.Blend(P.Bg, P.Fg, 0.5f), Math.Max(1.5f, Px(2)))) g.DrawEllipse(pn, Rectangle.Inflate(cr, Px(3), Px(3)));
            int k = i;
            if (!photo) Hot(cr, delegate { shade = k; });
        }
        // фото
        int by = y + Px(22) + sw + Px(12), bw0 = Px(118);
        Btn(g, new Rectangle(sx, by, bw0, Px(30)), photo ? L.T("Інше фото…") : L.T("Своє фото…"), PickPhoto, 0, true);
        Tip(new Rectangle(sx, by, bw0, Px(30)), L.T("Фото можна й перетягнути у це вікно"));
        Tip(new Rectangle(r.X, y, av, av), L.T("Фото можна й перетягнути у це вікно"));
        if (photo) Btn(g, new Rectangle(sx + bw0 + Px(8), by, Px(130), Px(30)), L.T("Прибрати фото"), delegate { if (photoNew != null) { photoNew.Dispose(); photoNew = null; } photoDrop = p != null && p.Photo; }, 2, true);
        y += av + Px(16);

        // стиль значка - за темою профілю (змінюється разом із темою в налаштуваннях)
        TxtW(g, photo ? L.T("Значок - ваше фото (без стилю теми й кольору).") : L.T("Стиль і кольори - теми «") + ProfileArt.StyleTitles[Math.Max(0, ProfileArt.Styles.IndexOf(style))] + L.T("». Зі зміною теми значок перейде в її стиль, а колір - у відповідний із її палітри."),
            FS, new Rectangle(r.X, y, r.Width, Px(34)), P.Sub);
        y += Px(38);
        // підказка: фото можна перетягнути
        Rectangle hint = new Rectangle(r.X, y, r.Width, Px(20));
        Ico.Draw(g, "plus", new RectangleF(hint.X, hint.Y + Px(2), Px(16), Px(16)), P.Acc);
        TxtL(g, L.T("Фото можна просто перетягнути у це вікно з Провідника."), FS, new Rectangle(hint.X + Px(22), hint.Y, hint.Width - Px(22), hint.Height), P.Acc);
        y += Px(30);


        TxtL(g, L.T("Назва"), FS, new Rectangle(r.X, y, r.Width, Px(18)), P.Sub);
        PlaceInput(g, name, new Rectangle(r.X, y + Px(20), r.Width, Px(34)));
        y += Px(64);
        if (curPin != null)
        {
            TxtL(g, L.T("Поточний PIN"), FS, new Rectangle(r.X, y, r.Width, Px(18)), P.Sub);
            PlaceInput(g, curPin, new Rectangle(r.X, y + Px(20), r.Width, Px(34)));
            y += Px(64);
        }
        int eyeW = Px(34), gp2 = Px(8), fw2 = (r.Width - eyeW - gp2 * 2) / 2;
        if (!dropPin)
        {
            TxtL(g, p != null && p.HasPin ? L.T("Новий PIN (порожньо - лишити)") : L.T("PIN (від 4 символів)"), FS, new Rectangle(r.X, y, fw2, Px(18)), P.Sub);
            PlaceInput(g, newPin, new Rectangle(r.X, y + Px(20), fw2, Px(34)));
            TxtL(g, L.T("Повторіть PIN"), FS, new Rectangle(r.X + fw2 + gp2, y, fw2, Px(18)), P.Sub);
            PlaceInput(g, newPin2, new Rectangle(r.X + fw2 + gp2, y + Px(20), fw2, Px(34)));
            IconBtn(g, new Rectangle(r.Right - eyeW, y + Px(20), eyeW, Px(34)), showPin ? "eyeoff" : "eye", ToggleShow, false);
        }
        else
        {
            if (newPin.Visible) newPin.Visible = false;
            if (newPin2.Visible) newPin2.Visible = false;
            TxtL(g, L.T("PIN буде прибрано"), F, new Rectangle(r.X, y + Px(20), r.Width, Px(34)), P.Warn);
        }
        y += Px(64);
        if (p != null && p.HasPin)
        {
            Switch(g, new Rectangle(r.X, y, r.Width, Px(28)), L.T("Прибрати PIN"), dropPin, delegate { dropPin = !dropPin; if (!dropPin) { newPin.Visible = true; newPin2.Visible = true; } });
            y += Px(34);
        }
        if (err.Length > 0) TxtL(g, err, F, new Rectangle(r.X, y, r.Width, Px(22)), P.Bad);

        int bh = Px(34), bw = Px(110);
        Rectangle ok = new Rectangle(r.Right - bw, r.Bottom - bh, bw, bh);
        Btn(g, ok, p == null ? L.T("Створити") : L.T("Зберегти"), Ok, 1, true);
        Btn(g, new Rectangle(ok.X - bw - Px(8), ok.Y, bw, bh), L.T("Скасувати"), delegate { Close(); }, 0, true);
        if (p != null && Prof.All.Count > 1 && p != Prof.Cur)
        {
            Rectangle del = new Rectangle(r.X, ok.Y, Px(120), bh);
            bool h = Over(del);
            using (SolidBrush b = new SolidBrush(h ? Util.Blend(P.Bg, P.Bad, 0.3f) : Util.Blend(P.Bg, P.Bad, 0.14f))) Util.FillRound(g, b, del, Math.Min(P.Rad, bh / 2));
            TxtC(g, L.T("Видалити"), FB, del, P.Bad);
            Hot(del, Remove);
        }
    }
}

// ----------------------------------------------------------------------
//  Вікно профілю біля аватара на панелі: налаштування, закріплення | зміна й видалення профілю | перезавантаження, вихід
// ----------------------------------------------------------------------
sealed class ProfileWindow : SbWindow
{
    bool busy;          // відкрито вікно зміни профілю - не закриватися

    public ProfileWindow(NeoBar bar) : base(bar, L.T("Профіль"), 330, 460) { Glyph = "\uE77B"; }

    protected override void OnShown(EventArgs e) { base.OnShown(e); Activate(); }

    // клік поза вікном (робочий стіл, інше вікно) - закриваємось
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (busy || IsDisposed) return;
        try { BeginInvoke(new Action(delegate { if (!busy && !IsDisposed) Close(); })); } catch { }
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        Profile p = Prof.Cur;
        Rectangle top = new Rectangle(r.X, r.Y, r.Width, Px(76));
        Card(g, top);
        int av = Px(52);
        ProfileArt.Avatar(g, new Rectangle(top.X + Px(12), top.Y + (top.Height - av) / 2, av, av), p, false, P.Acc);
        int tx = top.X + Px(12) + av + Px(12);
        Rectangle eb = new Rectangle(top.Right - Px(46), top.Y + (top.Height - Px(38)) / 2, Px(38), Px(38));
        TxtL(g, p != null ? p.Name : "—", FH, new Rectangle(tx, top.Y + Px(14), eb.X - tx - Px(4), Px(26)), P.Fg);
        TxtL(g, p != null && p.HasPin ? L.T("Захищено PIN-кодом") : L.T("Без PIN-коду"), FS, new Rectangle(tx, top.Y + Px(42), eb.X - tx - Px(4), Px(20)), P.Sub);
        bool eh = Over(eb);
        if (eh) using (SolidBrush b = new SolidBrush(Util.Blend(P.Card, P.Fg, 0.1f))) g.FillEllipse(b, eb);
        ProfileArt.Glyph(g, "edit", new RectangleF(eb.X + Px(7), eb.Y + Px(7), Px(24), Px(24)), eh ? P.Fg : P.Sub);
        Hot(eb, EditMe);

        int y = top.Bottom + Px(10), rh = Px(42);
        bool fixedMode = Bar.Config.Mode == "fixed";
        Row(g, new Rectangle(r.X, y, r.Width, rh), "gear", L.T("Налаштування"), false, delegate { Close(); Bar.OpenSettingsTab(-1); }); y += rh + Px(2);
        Row(g, new Rectangle(r.X, y, r.Width, rh), "pin", fixedMode ? L.T("Увімкнути автосховок") : L.T("Закріпити панель"), false, delegate { Close(); Bar.TogglePinMode(); }); y += rh + Px(10);
        using (Pen lp = new Pen(P.Line, 1f)) g.DrawLine(lp, r.X + Px(6), y - Px(5), r.Right - Px(6), y - Px(5));
        Row(g, new Rectangle(r.X, y, r.Width, rh), "#switch", L.T("Змінити профіль"), false, delegate { Close(); Bar.SwitchProfile(); }); y += rh + Px(2);
        Row(g, new Rectangle(r.X, y, r.Width, rh), "trash", L.T("Видалити профіль"), true, delegate { Close(); Bar.DeleteProfile(); }); y += rh + Px(10);
        using (Pen lp = new Pen(P.Line, 1f)) g.DrawLine(lp, r.X + Px(6), y - Px(5), r.Right - Px(6), y - Px(5));
        Row(g, new Rectangle(r.X, y, r.Width, rh), "sync", L.T("Перезавантажити панель"), false, delegate { Close(); Bar.Reload(); }); y += rh + Px(2);
        Row(g, new Rectangle(r.X, y, r.Width, rh), "#power", L.T("Вийти з NeoBar"), true, delegate { Close(); Bar.Close(); });
    }

    void Row(Graphics g, Rectangle r, string icon, string text, bool danger, Action click)
    {
        bool h = Over(r);
        if (h) using (SolidBrush b = new SolidBrush(danger ? Util.Blend(P.Bg, P.Bad, 0.16f) : P.Card)) Util.FillRound(g, b, r, Math.Min(P.Rad, r.Height / 2));
        Color c = danger ? P.Bad : (h ? P.Fg : Util.Blend(P.Sub, P.Fg, 0.4f));
        RectangleF ir = new RectangleF(r.X + Px(12), r.Y + (r.Height - Px(18)) / 2f, Px(18), Px(18));
        if (icon.StartsWith("#")) ProfileArt.Glyph(g, icon.Substring(1), ir, c);
        else Ico.Draw(g, icon, ir, c);
        TxtL(g, text, F, new Rectangle(r.X + Px(42), r.Y, r.Width - Px(48), r.Height), danger ? P.Bad : P.Fg);
        Hot(r, click);
    }

    void EditMe()
    {
        if (Prof.Cur == null) return;
        bool changed;
        busy = true;
        try
        {
            using (ProfileEdit f = new ProfileEdit(T, Sc, Prof.Cur))
            {
                f.StartPosition = FormStartPosition.CenterScreen;
                f.ShowDialog(this);
                changed = f.Changed;
            }
        }
        finally { busy = false; }
        if (!IsDisposed) Activate();
        if (changed) { Bar.ProfileChanged(); Invalidate(); }
    }
}

// ----------------------------------------------------------------------
//  Підтвердження видалення поточного профілю (з PIN, якщо він є)
// ----------------------------------------------------------------------
sealed class ProfileDelete : SbWindow
{
    readonly Profile p;
    readonly TextBox pin;
    string err = "";
    public bool Ok;

    public ProfileDelete(NeoBar bar, Profile prof) : base(bar, L.T("Видалити профіль"), 460, prof.HasPin ? 330 : 250)
    {
        Glyph = "\uE74D";
        p = prof;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        if (p.HasPin)
        {
            pin = MakeInput();
            pin.UseSystemPasswordChar = true;
            pin.MaxLength = 32;
            pin.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Go(); } };
            pin.TextChanged += delegate { if (err.Length > 0) { err = ""; Invalidate(); } };
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        if (pin != null) pin.Focus();
    }

    void Go()
    {
        if (p.HasPin && !Prof.Check(p, pin.Text)) { err = Prof.PinErr(p, L.T("Невірний PIN")); pin.SelectAll(); Invalidate(); return; }
        Ok = true;
        Close();
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        string t = L.T("Профіль «") + p.Name + L.T("» буде видалено разом з усіма його налаштуваннями, елементами панелі, нотатками, списком ігор і виглядом оверлею. Повернути його не вийде.\n\nNeoBar перезапуститься й покаже вибір профілю.");
        int th = TextRenderer.MeasureText(t, F, new Size(r.Width, 0), TextFormatFlags.WordBreak).Height;
        TxtW(g, t, F, new Rectangle(r.X, r.Y, r.Width, th + Px(4)), P.Fg);
        int y = r.Y + th + Px(14);
        if (pin != null)
        {
            TxtL(g, L.T("Щоб підтвердити, введіть PIN профілю"), FS, new Rectangle(r.X, y, r.Width, Px(18)), P.Sub);
            PlaceInput(g, pin, new Rectangle(r.X, y + Px(20), r.Width, Px(34)));
            y += Px(62);
        }
        if (err.Length > 0) TxtL(g, err, F, new Rectangle(r.X, y, r.Width, Px(22)), P.Bad);
        int bh = Px(36), bw = Px(150);
        Rectangle del = new Rectangle(r.Right - bw, r.Bottom - bh, bw, bh);
        bool h = Over(del);
        using (SolidBrush b = new SolidBrush(Util.Blend(P.Bg, P.Bad, h ? 0.34f : 0.2f))) Util.FillRound(g, b, del, Math.Min(P.Rad, bh / 2));
        TxtC(g, L.T("Видалити профіль"), FB, del, P.Bad);
        Hot(del, Go);
        Btn(g, new Rectangle(del.X - Px(118), del.Y, Px(110), bh), L.T("Скасувати"), delegate { Close(); }, 0, true);
    }
}
