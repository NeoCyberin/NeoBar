using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

// ======================================================================
//  Вікно «Шифрування»: хеш-сума файла, шифрування тексту, шифрування файлів (.nbenc або ZIP з AES-256).
//  Важка робота (обхід файла, виведення ключа з пароля) - в окремому потоці; вікно лише показує поступ.
// ======================================================================
sealed class CryptWindow : ToolWindow
{
    TextBox tbCmp, tbIn, tbOut, tbPw, tbFpw, tbFpw2;
    Font mono;
    readonly System.Windows.Forms.Timer fast = new System.Windows.Forms.Timer();

    // хеш
    string hFile = "";
    long hSize;
    string[] hVals;
    volatile float hProg;
    volatile bool hBusy, hStop;
    string hErr = "";

    // текст
    volatile bool tBusy;
    bool showPw;

    // файли
    sealed class FItem { public string Path, Result = "", Error = ""; public long Size; public bool Done; }
    readonly List<FItem> files = new List<FItem>();
    int fmt;                    // 0 - .nbenc, 1 - ZIP AES-256
    volatile bool fBusy, fStop;
    volatile float fProg;
    volatile int fCur = -1;
    int fScroll;
    string fMsg = "";
    bool fMsgBad;

    public CryptWindow(NeoBar bar) : base(bar, L.T("Шифрування"), 580, 640) { Setup(); }
    public CryptWindow(Theme t, float sc) : base(t, sc, L.T("Шифрування"), 580, 640) { Setup(); }

    void Setup()
    {
        Glyph = "\uE72E";
        mono = Util.MakeFont("Consolas", 9f, FontStyle.Regular);
        tbCmp = MakeInput(); tbIn = MakeInput(); tbOut = MakeInput(); tbPw = MakeInput(); tbFpw = MakeInput(); tbFpw2 = MakeInput();
        Tabs = new string[] { L.T("Хеш-сума"), L.T("Текст"), L.T("Файли") };
        foreach (TextBox t in new TextBox[] { tbIn, tbOut })
        {
            t.Multiline = true; t.WordWrap = true; t.AcceptsReturn = true; t.ScrollBars = ScrollBars.None;
        }
        tbOut.ReadOnly = true;
        tbOut.BackColor = P.Field;
        tbOut.Font = mono;
        tbCmp.Font = mono;
        foreach (TextBox t in new TextBox[] { tbPw, tbFpw, tbFpw2 }) t.UseSystemPasswordChar = true;
        tbCmp.TextChanged += delegate { Invalidate(); };
        tbFpw.TextChanged += delegate { Invalidate(); };
        tbFpw2.TextChanged += delegate { Invalidate(); };
        tbPw.TextChanged += delegate { Invalidate(); };
        fast.Interval = 120;
        fast.Tick += delegate { if (hBusy || tBusy || fBusy) Invalidate(); };
        fast.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        hStop = true; fStop = true;
        fast.Stop(); fast.Dispose();
        base.OnFormClosed(e);
        mono.Dispose();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (fBusy && Ask(L.T("Шифрування"), L.T("Ще обробляються файли. Зупинити й закрити?\n\nНезавершений файл буде видалено, оригінали не зачіпаються."), L.T("Зупинити"), L.T("Чекати")) != 0) { e.Cancel = true; return; }
        base.OnFormClosing(e);
    }

    protected override void OnTab()
    {
        HideInputs();
    }

    void HideInputs()
    {
        foreach (TextBox t in new TextBox[] { tbCmp, tbIn, tbOut, tbPw, tbFpw, tbFpw2 }) if (t.Visible) t.Visible = false;
    }

    protected override void Dropped(string[] f)
    {
        if (Tab == 0)
        {
            foreach (string p in f) if (File.Exists(p)) { HashFile(p); return; }
            return;
        }
        AddFiles(f);
    }

    // ---------- зовнішні виклики (віджет, контекстне меню) ----------
    public void HashFile(string path)
    {
        if (!File.Exists(path)) return;
        if (Tab != 0) { Tab = 0; OnTab(); }
        if (hBusy) { hStop = true; }
        hFile = path;
        hVals = null; hErr = "";
        try { hSize = new FileInfo(path).Length; } catch { hSize = 0; }
        hProg = 0; hStop = false; hBusy = true;
        string p0 = path;
        Thread t = new Thread(delegate ()
        {
            string[] r = null;
            string err = "";
            try { r = Hashes.OfFile(p0, delegate (float f) { hProg = f; }, delegate { return hStop || p0 != hFile; }); }
            catch (Exception ex) { err = ex.Message; }
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (p0 != hFile) return;          // уже вибрали інший файл
                    hBusy = false;
                    if (r != null) hVals = r;
                    hErr = err;
                    Invalidate();
                }));
            }
            catch { }
        });
        t.IsBackground = true;
        t.Priority = ThreadPriority.BelowNormal;
        t.Start();
        Invalidate();
    }

    public void AddFiles(string[] paths)
    {
        if (Tab != 2) { Tab = 2; OnTab(); }
        foreach (string p in paths)
        {
            if (!File.Exists(p)) continue;
            bool have = false;
            foreach (FItem x in files) if (string.Equals(x.Path, p, StringComparison.OrdinalIgnoreCase)) have = true;
            if (have) continue;
            FItem it = new FItem();
            it.Path = p;
            try { it.Size = new FileInfo(p).Length; } catch { }
            files.Add(it);
        }
        fMsg = "";
        Invalidate();
    }

    // ---------- малювання ----------
    protected override void PaintBody(Graphics g, Rectangle r)
    {
        int bottom = r.Bottom - Px(30);
        if (Tab == 0) PaintHash(g, new Rectangle(r.X, r.Y, r.Width, bottom - r.Y - Px(8)));
        else if (Tab == 1) PaintText(g, new Rectangle(r.X, r.Y, r.Width, bottom - r.Y - Px(8)));
        else PaintFiles(g, new Rectangle(r.X, r.Y, r.Width, bottom - r.Y - Px(8)));
        Rectangle sw = new Rectangle(r.X, bottom, r.Width, Px(28));
        if (NoteLive) TxtL(g, Note, F, sw, NoteBad ? P.Bad : P.Good);
        else Switch(g, sw, L.T("Хеш-сума й шифрування в контекстному меню файлів"), ShellMenu.Has("crypt"), delegate
        {
            bool was = ShellMenu.Has("crypt");
            string err = ShellMenu.Toggle("crypt");
            if (err.Length > 0) Say(L.T("Не вдалося змінити меню: ") + err, true);
            else Say(was ? L.T("Пункти шифрування прибрано з контекстного меню.") : L.T("Додано: ПКМ на файлі → NeoBar (у Windows 11 - «Показати додаткові параметри»)."), false);
        });
    }

    void PaintHash(Graphics g, Rectangle r)
    {
        foreach (TextBox t in new TextBox[] { tbIn, tbOut, tbPw, tbFpw, tbFpw2 }) if (t.Visible) t.Visible = false;
        int y = r.Y;
        Rectangle top = new Rectangle(r.X, y, r.Width, Px(70));
        Card(g, top);
        if (hFile.Length == 0)
        {
            TxtL(g, L.T("Перетягніть файл у вікно або виберіть його"), F, new Rectangle(top.X + Px(14), top.Y, top.Width - Px(190), top.Height), P.Sub);
        }
        else
        {
            TxtL(g, Path.GetFileName(hFile), FB, new Rectangle(top.X + Px(14), top.Y + Px(12), top.Width - Px(190), Px(22)), P.Fg);
            TxtL(g, SizeText(hSize) + " · " + Path.GetDirectoryName(hFile), FS, new Rectangle(top.X + Px(14), top.Y + Px(36), top.Width - Px(190), Px(20)), P.Sub);
        }
        Rectangle pick = new Rectangle(top.Right - Px(164), top.Y + (top.Height - Px(34)) / 2, Px(150), Px(34));
        if (hBusy) Btn(g, pick, L.T("Зупинити"), delegate { hStop = true; hBusy = false; hFile = ""; Invalidate(); }, 0, true);
        else Btn(g, pick, L.T("Вибрати файл…"), PickHash, 1, true);
        y = top.Bottom + Px(12);
        if (hBusy)
        {
            ProgressBar(g, new Rectangle(r.X, y, r.Width, Px(8)), hProg, P.Acc);
            TxtL(g, L.T("Обчислення… ") + (int)(hProg * 100) + "%", FS, new Rectangle(r.X, y + Px(12), r.Width, Px(20)), P.Sub);
            y += Px(40);
        }
        if (hErr.Length > 0)
        {
            TxtW(g, L.T("Не вдалося прочитати файл: ") + hErr, F, new Rectangle(r.X, y, r.Width, Px(44)), P.Bad);
            y += Px(48);
        }
        string want = Hashes.Clean(tbCmp.Text);
        int match = -1;
        if (hVals != null && want.Length > 0)
            for (int i = 0; i < hVals.Length; i++) if (hVals[i] == want) match = i;
        int rh = Px(52);
        for (int i = 0; i < Hashes.Names.Length; i++)
        {
            Rectangle rr = new Rectangle(r.X, y, r.Width, rh - Px(6));
            string v = hVals != null ? (hVals[i].Length > 0 ? hVals[i] : L.T("недоступно (режим FIPS)")) : "";
            bool hot = v.Length > 0 && Over(rr);
            Card(g, rr, i == match ? Util.Blend(P.Card, P.Good, 0.22f) : (hot ? Util.Blend(P.Card, P.Fg, 0.06f) : P.Card));
            TxtL(g, Hashes.Names[i], FB, new Rectangle(rr.X + Px(12), rr.Y + Px(4), Px(90), Px(20)), i == match ? P.Good : P.Sub);
            if (v.Length > 0)
            {
                TxtR(g, hot ? L.T("клік - копіювати") : (i == match ? L.T("збігається ✓") : ""), FS, new Rectangle(rr.Right - Px(170), rr.Y + Px(4), Px(158), Px(20)), i == match ? P.Good : P.Sub);
                Txt(g, v, mono, new Rectangle(rr.X + Px(12), rr.Y + Px(22), rr.Width - Px(24), Px(22)), P.Fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                string cv = v, nm = Hashes.Names[i];
                if (hVals[i].Length > 0) Hot(rr, delegate { Copy(cv); Say(nm + L.T(" скопійовано."), false); });
            }
            y += rh;
        }
        y += Px(6);
        TxtL(g, L.T("Порівняти з очікуваною сумою (вставте з сайту):"), F, new Rectangle(r.X, y, r.Width, Px(22)), P.Fg);
        y += Px(26);
        PlaceInput(g, tbCmp, new Rectangle(r.X, y, r.Width, Px(34)));
        y += Px(42);
        if (want.Length > 0 && hVals != null)
        {
            if (match >= 0) TxtL(g, L.T("✓ Збігається з ") + Hashes.Names[match] + L.T(" - файл той самий, що й на сайті."), FB, new Rectangle(r.X, y, r.Width, Px(24)), P.Good);
            else TxtW(g, L.T("✗ Не збігається з жодною сумою. Файл інший (пошкоджений, змінений або іншої версії) - або вставлено суму іншого файла."), F, new Rectangle(r.X, y, r.Width, Px(44)), P.Bad);
        }
    }

    void PickHash()
    {
        using (OpenFileDialog d = new OpenFileDialog())
        {
            d.Title = L.T("Файл для хеш-суми");
            d.Filter = L.T("Усі файли|*.*");
            if (d.ShowDialog(this) == DialogResult.OK) HashFile(d.FileName);
        }
    }

    // ---------- текст ----------
    void PaintText(Graphics g, Rectangle r)
    {
        foreach (TextBox t in new TextBox[] { tbCmp, tbFpw, tbFpw2 }) if (t.Visible) t.Visible = false;
        int y = r.Y;
        TxtL(g, L.T("Текст (або зашифрований блок «") + SbCrypt.TextTag + L.T("…» для розшифрування):"), F, new Rectangle(r.X, y, r.Width, Px(22)), P.Fg);
        y += Px(26);
        int fh = (r.Height - Px(26) - Px(26) - Px(52) - Px(48) - Px(26)) / 2;
        MultiField(g, tbIn, new Rectangle(r.X, y, r.Width, fh));
        y += fh + Px(10);
        TxtL(g, L.T("Пароль:"), F, new Rectangle(r.X, y, Px(80), Px(34)), P.Fg);
        Rectangle pr = new Rectangle(r.X + Px(80), y, r.Width - Px(80) - Px(120), Px(34));
        PlaceInput(g, tbPw, pr);
        Chip(g, new Rectangle(pr.Right + Px(8), y, Px(112), Px(34)), showPw ? L.T("Сховати") : L.T("Показати"), showPw, delegate { showPw = !showPw; tbPw.UseSystemPasswordChar = !showPw; Invalidate(); });
        y += Px(42);
        int bw = (r.Width - Px(10)) / 2;
        bool ok = !tBusy && tbPw.Text.Length > 0 && tbIn.Text.Trim().Length > 0;
        Btn(g, new Rectangle(r.X, y, bw, Px(36)), tBusy ? L.T("Зачекайте…") : L.T("Зашифрувати"), delegate { RunText(true); }, 1, ok);
        Btn(g, new Rectangle(r.X + bw + Px(10), y, bw, Px(36)), L.T("Розшифрувати"), delegate { RunText(false); }, 0, ok && tbIn.Text.IndexOf(SbCrypt.TextTag, StringComparison.Ordinal) >= 0);
        y += Px(46);
        TxtL(g, L.T("Результат:"), F, new Rectangle(r.X, y, r.Width - Px(130), Px(22)), P.Fg);
        Btn(g, new Rectangle(r.Right - Px(120), y - Px(2), Px(120), Px(26)), L.T("Копіювати"), delegate { Copy(tbOut.Text); Say(L.T("Скопійовано."), false); }, 2, tbOut.Text.Length > 0);
        y += Px(26);
        MultiField(g, tbOut, new Rectangle(r.X, y, r.Width, Math.Max(Px(40), r.Bottom - y)));
    }

    void RunText(bool enc)
    {
        if (tBusy) return;
        string text = tbIn.Text, pw = tbPw.Text;
        if (enc && Strength(pw) == 0 && Ask(L.T("Шифрування"), L.T("Пароль слабкий (коротший за 8 символів) - такий підбирають швидко.\n\nВсе одно зашифрувати?"), L.T("Зашифрувати"), L.T("Скасувати")) != 0) return;
        tBusy = true;
        Invalidate();
        Thread t = new Thread(delegate ()
        {
            string res = null, err = "";
            try { res = enc ? SbCrypt.EncryptText(text, pw) : SbCrypt.DecryptText(text, pw, out err); }
            catch (Exception ex) { err = ex.Message; }
            try
            {
                BeginInvoke(new Action(delegate
                {
                    tBusy = false;
                    if (res != null)
                    {
                        tbOut.Text = res;
                        Say(enc ? L.T("Зашифровано. Передайте текст, а пароль - окремо (не в тому ж повідомленні).") : L.T("Розшифровано."), false);
                    }
                    else { tbOut.Text = ""; Say(err, true); }
                    Invalidate();
                }));
            }
            catch { }
        });
        t.IsBackground = true;
        t.Start();
    }

    // ---------- файли ----------
    static int Strength(string pw)
    {
        int cls = 0;
        bool lo = false, up = false, dg = false, ot = false;
        foreach (char c in pw)
        {
            if (char.IsLower(c)) lo = true; else if (char.IsUpper(c)) up = true; else if (char.IsDigit(c)) dg = true; else ot = true;
        }
        cls = (lo ? 1 : 0) + (up ? 1 : 0) + (dg ? 1 : 0) + (ot ? 1 : 0);
        if (pw.Length >= 14 || (pw.Length >= 10 && cls >= 3)) return 2;
        if (pw.Length >= 8 || (pw.Length >= 6 && cls >= 3)) return 1;          // середній - уже підходить
        return 0;
    }

    bool AllDecryptable()
    {
        if (files.Count == 0) return false;
        foreach (FItem f in files)
        {
            string ext = Path.GetExtension(f.Path).ToLowerInvariant();
            if (ext != ".zip" && !SbCrypt.IsSbenc(f.Path)) return false;
        }
        return true;
    }

    void PaintFiles(Graphics g, Rectangle r)
    {
        foreach (TextBox t in new TextBox[] { tbCmp, tbIn, tbOut, tbPw }) if (t.Visible) t.Visible = false;
        int y = r.Y;
        // кнопки списку
        int bw = Px(150);
        Btn(g, new Rectangle(r.X, y, bw, Px(32)), L.T("Додати файли…"), PickFiles, 0, !fBusy);
        Btn(g, new Rectangle(r.X + bw + Px(8), y, Px(130), Px(32)), L.T("Очистити"), delegate { files.Clear(); fMsg = ""; Invalidate(); }, 2, !fBusy && files.Count > 0);
        TxtR(g, files.Count > 0 ? L.T("Файлів: ") + files.Count + " · " + SizeText(TotalSize()) : "", FS, new Rectangle(r.Right - Px(160), y, Px(160), Px(32)), P.Sub);
        y += Px(40);
        // список
        int bottomBlock = Px(34) + Px(8) + Px(34) + Px(8) + Px(34) + Px(10) + Px(38) + Px(30);
        Rectangle list = new Rectangle(r.X, y, r.Width, Math.Max(Px(80), r.Bottom - y - bottomBlock));
        int rowH = Px(48);
        Card(g, list, Util.Blend(P.Bg, P.Fg, 0.035f));
        if (files.Count == 0)
            TxtC(g, L.T("Перетягніть файли у вікно або натисніть «Додати файли…»"), F, list, P.Sub);
        int total = files.Count * rowH, maxS = Math.Max(0, total - list.Height + Px(8));
        fScroll = Math.Max(0, Math.Min(maxS, fScroll));
        GraphicsState st = g.Save();
        g.SetClip(list);
        for (int i = 0; i < files.Count; i++)
        {
            FItem f = files[i];
            Rectangle rr = new Rectangle(list.X + Px(4), list.Y + Px(4) + i * rowH - fScroll, list.Width - Px(16), rowH - Px(4));
            if (rr.Bottom < list.Y || rr.Top > list.Bottom) continue;
            bool cur = fBusy && fCur == i;
            Card(g, rr, cur ? Util.Blend(P.Card, P.Acc, 0.15f) : P.Card);
            TxtL(g, Path.GetFileName(f.Path), F, new Rectangle(rr.X + Px(10), rr.Y + Px(3), rr.Width - Px(130), Px(22)), P.Fg);
            string sub; Color sc = P.Sub;
            if (cur) sub = L.T("обробка… ") + (int)(fProg * 100) + "%";
            else if (f.Error.Length > 0) { sub = f.Error; sc = P.Bad; }
            else if (f.Done) { sub = "✓ " + Path.GetFileName(f.Result) + L.T(" (клік - показати в теці)"); sc = P.Good; }
            else sub = SizeText(f.Size) + " · " + Path.GetDirectoryName(f.Path);
            TxtL(g, sub, FS, new Rectangle(rr.X + Px(10), rr.Y + Px(23), rr.Width - Px(50), Px(18)), sc);
            if (cur) ProgressBar(g, new Rectangle(rr.Right - Px(120), rr.Y + Px(10), Px(80), Px(6)), fProg, P.Acc);
            FItem ff = f;
            if (f.Done && Rectangle.Intersect(rr, list) == rr) Hot(new Rectangle(rr.X, rr.Y, rr.Width - Px(40), rr.Height), delegate { ShowInFolder(ff.Result); });
            if (!fBusy && Rectangle.Intersect(rr, list) == rr)
                IconBtn(g, new Rectangle(rr.Right - Px(34), rr.Y + (rr.Height - Px(28)) / 2, Px(28), Px(28)), "close", delegate { files.Remove(ff); Invalidate(); }, false);
        }
        g.Restore(st);
        Wheel(list, delegate (int d) { fScroll -= d > 0 ? rowH : -rowH; Invalidate(); });
        VBar(g, new Rectangle(list.Right - Px(7), list.Y + Px(4), Px(4), list.Height - Px(8)), total + Px(8), list.Height, fScroll, delegate (int v) { fScroll = v; Invalidate(); });
        y = list.Bottom + Px(10);

        bool dec = AllDecryptable();
        // формат (для шифрування)
        int cw = (r.Width - Px(8)) / 2;
        Chip(g, new Rectangle(r.X, y, cw, Px(34)), L.T(".nbenc - лише NeoBar"), fmt == 0, delegate { fmt = 0; Invalidate(); });
        Chip(g, new Rectangle(r.X + cw + Px(8), y, cw, Px(34)), "ZIP AES-256 - 7-Zip, WinRAR", fmt == 1, delegate { fmt = 1; Invalidate(); });
        y += Px(42);
        TxtL(g, L.T("Пароль:"), F, new Rectangle(r.X, y, Px(80), Px(34)), P.Fg);
        int pw = (r.Width - Px(80) - Px(8)) / 2;
        PlaceInput(g, tbFpw, new Rectangle(r.X + Px(80), y, pw, Px(34)));
        PlaceInput(g, tbFpw2, new Rectangle(r.X + Px(80) + pw + Px(8), y, pw, Px(34)));
        if (tbFpw2.Text.Length == 0 && !tbFpw2.Focused) TxtL(g, L.T("повтор (для шифрування)"), FS, new Rectangle(r.X + Px(90) + pw + Px(8), y, pw - Px(16), Px(34)), Util.Blend(P.Field, P.Sub, 0.8f));
        y += Px(42);
        string p1 = tbFpw.Text;
        string hint; Color hc;
        if (p1.Length == 0) { hint = dec ? L.T("Введіть пароль, яким файли зашифровано.") : L.T("Без пароля файли не відкрити - його неможливо відновити."); hc = P.Sub; }
        else if (fmt == 1 && !dec && !ZipAes.AsciiOk(p1)) { hint = L.T("Для ZIP пароль лише латиницею, цифрами й знаками - інакше інші архіватори можуть його не прийняти."); hc = P.Bad; }
        else if (tbFpw2.Text.Length > 0 && tbFpw2.Text != p1) { hint = L.T("Паролі не збігаються."); hc = P.Bad; }
        else { int s = Strength(p1); hint = s == 2 ? L.T("Надійний пароль.") : (s == 1 ? L.T("Середній пароль - підійде.") : L.T("Слабкий пароль: краще щонайменше 8 символів.")); hc = s == 2 ? P.Good : (s == 1 ? P.Warn : P.Bad); }
        if (fMsg.Length > 0) { hint = fMsg; hc = fMsgBad ? P.Bad : P.Good; }
        TxtL(g, hint, FS, new Rectangle(r.X, y, r.Width, Px(24)), hc);
        y += Px(30);
        int bw2 = (r.Width - Px(10)) / 2;
        Rectangle eb = new Rectangle(r.X, y, bw2, Px(38)), db = new Rectangle(r.X + bw2 + Px(10), y, bw2, Px(38));
        if (fBusy)
        {
            Btn(g, new Rectangle(r.X, y, r.Width, Px(38)), L.T("Зупинити"), delegate { fStop = true; }, 0, true);
            return;
        }
        bool canEnc = files.Count > 0 && p1.Length > 0 && tbFpw2.Text == p1 && (fmt == 0 || ZipAes.AsciiOk(p1));
        Btn(g, eb, L.T("Зашифрувати"), delegate { RunFiles(true); }, dec ? 0 : 1, canEnc);
        Btn(g, db, L.T("Розшифрувати"), delegate { RunFiles(false); }, dec ? 1 : 0, dec && p1.Length > 0);
    }

    long TotalSize() { long s = 0; foreach (FItem f in files) s += f.Size; return s; }

    void PickFiles()
    {
        using (OpenFileDialog d = new OpenFileDialog())
        {
            d.Title = L.T("Файли для шифрування чи розшифрування");
            d.Filter = L.T("Усі файли|*.*");
            d.Multiselect = true;
            if (d.ShowDialog(this) == DialogResult.OK) AddFiles(d.FileNames);
        }
    }

    // ім'я архіву: один файл - «назва.zip», кілька - за назвою теки першого файла
    static string ZipName(List<FItem> list)
    {
        string first = list[0].Path, dir = Path.GetDirectoryName(first);
        string nm = list.Count == 1 ? Path.GetFileNameWithoutExtension(first) : Path.GetFileName(dir.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(nm)) nm = L.T("Зашифровано");
        return SbCrypt.Free(Path.Combine(dir, nm + ".zip"));
    }

    void RunFiles(bool enc)
    {
        if (fBusy || files.Count == 0) return;
        string pw = tbFpw.Text;
        if (enc && Strength(pw) == 0 && Ask(L.T("Шифрування"), L.T("Пароль слабкий (коротший за 8 символів) - його можуть підібрати.\n\nВсе одно зашифрувати?"), L.T("Зашифрувати"), L.T("Скасувати")) != 0) return;
        List<FItem> work = new List<FItem>(files);
        foreach (FItem f in work) { f.Done = false; f.Error = ""; f.Result = ""; }
        int mode = fmt;
        fBusy = true; fStop = false; fMsg = ""; fProg = 0;
        Invalidate();
        Thread t = new Thread(delegate ()
        {
            int ok = 0, bad = 0;
            string extra = "";
            try
            {
                if (enc && mode == 1)
                {
                    // усі вибрані файли - в один архів
                    List<KeyValuePair<string, string>> items = new List<KeyValuePair<string, string>>();
                    HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (FItem f in work)
                    {
                        string n = Path.GetFileName(f.Path), b = n;
                        for (int k = 2; !names.Add(n); k++) n = Path.GetFileNameWithoutExtension(b) + " (" + k + ")" + Path.GetExtension(b);
                        items.Add(new KeyValuePair<string, string>(f.Path, n));
                    }
                    string zp = ZipName(work);
                    fCur = 0;
                    try
                    {
                        ZipAes.Create(zp, items, pw, delegate (float p) { fProg = p; }, delegate { return fStop; });
                        foreach (FItem f in work) { f.Done = true; f.Result = zp; }
                        ok = work.Count;
                    }
                    catch (OperationCanceledException) { extra = L.T("Зупинено."); }
                    catch (Exception ex) { foreach (FItem f in work) f.Error = ex.Message; bad = work.Count; }
                }
                else
                {
                    for (int i = 0; i < work.Count && !fStop; i++)
                    {
                        FItem f = work[i];
                        fCur = files.IndexOf(f);
                        fProg = 0;
                        try
                        {
                            if (enc) f.Result = SbCrypt.EncryptFile(f.Path, pw, delegate (float p) { fProg = p; }, delegate { return fStop; });
                            else if (SbCrypt.IsSbenc(f.Path)) f.Result = SbCrypt.DecryptFile(f.Path, pw, delegate (float p) { fProg = p; }, delegate { return fStop; });
                            else
                            {
                                string notes;
                                f.Result = ZipAes.Extract(f.Path, pw, delegate (float p) { fProg = p; }, delegate { return fStop; }, out notes);
                                if (notes.Length > 0) extra = notes;
                            }
                            f.Done = true; ok++;
                        }
                        catch (OperationCanceledException) { extra = L.T("Зупинено."); break; }
                        catch (Exception ex) { f.Error = ex.Message; bad++; }
                    }
                }
            }
            catch (Exception ex) { Util.ErrorLog(ex); extra = ex.Message; }
            try
            {
                BeginInvoke(new Action(delegate
                {
                    fBusy = false; fCur = -1;
                    fMsgBad = bad > 0 || ok == 0;
                    fMsg = (ok > 0 ? (enc ? L.T("Зашифровано: ") : L.T("Розшифровано: ")) + ok + ". " : "") + (bad > 0 ? L.T("Помилок: ") + bad + ". " : "") + extra +
                           (enc && ok > 0 ? L.T(" Оригінали лишилися на місці.") : "");
                    Invalidate();
                }));
            }
            catch { }
        });
        t.IsBackground = true;
        t.Priority = ThreadPriority.BelowNormal;
        t.Start();
    }
}
