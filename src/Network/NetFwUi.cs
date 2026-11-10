using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

// ======================================================================
//  Вікно «Мережа», вкладка «Брандмауер»: стан профілів (домен, приватна, загальна), список правил із
//  перемикачами, додавання й видалення власних правил. Системні правила показуються, але не змінюються.
//  Читання не потребує прав; кожна зміна - після кнопки й запиту адміністратора від Windows.
// ======================================================================
sealed partial class NetWindow
{
    readonly bool[] fwOn = new bool[3], fwMan = new bool[3];
    bool fwRead, fwLoading, fwDenied, fwAsk;
    int fwRev = Fw.Rev;                    // версія стану, яку показує вікно (змінюється, коли хтось інший перемкнув брандмауер)
    List<FwRule> fwRules = new List<FwRule>();
    readonly List<FwRule> fwView = new List<FwRule>();
    string fwViewKey = "";
    int fwPage, fwDir, fwScroll;                // fwDir: 0 будь-які, 1 вхідні, 2 вихідні
    bool fwMine = true;
    TextBox tbSearch, tbFwName, tbFwPort, tbFwApp;
    bool addIn = true, addAllow = true;
    string addProto = "tcp", fwErr = "";

    void FwInit()
    {
        tbSearch = MakeInput(); tbFwName = MakeInput(); tbFwPort = MakeInput(); tbFwApp = MakeInput();
        tbSearch.TextChanged += delegate { fwViewKey = ""; fwScroll = 0; Invalidate(); };
        tbFwName.MaxLength = 60;
        tbFwPort.MaxLength = 60;
        tbFwApp.MaxLength = 259;
    }

    void FwLeave()
    {
        fwPage = 0; fwErr = "";
        if (tbSearch != null) tbSearch.Text = "";
    }

    // ---------- читання ----------
    // раз на секунду: стан профілів міг змінитися поза вікном - віджетом на столі, Захисником Windows тощо
    void FwSync()
    {
        if (!fwRead || fwLoading || busy) return;
        if (fwRev != Fw.Rev) { fwRev = Fw.Rev; FwReload(); return; }
        bool[] on = new bool[3], man = new bool[3];
        if (!Fw.Read(on, man)) return;
        bool diff = false;
        for (int i = 0; i < 3; i++) if (on[i] != fwOn[i] || man[i] != fwMan[i]) diff = true;
        if (!diff) return;
        for (int i = 0; i < 3; i++) { fwOn[i] = on[i]; fwMan[i] = man[i]; }
        Invalidate();
    }

    void FwReload()
    {
        if (fwLoading) return;
        fwLoading = true;
        Invalidate();
        Util.Bg(delegate
        {
            bool[] on = new bool[3], man = new bool[3];
            bool ok = Fw.Read(on, man);
            List<FwRule> l = Fw.Load();
            UiPost(delegate
            {
                fwLoading = false; fwRead = true;
                if (ok) for (int i = 0; i < 3; i++) { fwOn[i] = on[i]; fwMan[i] = man[i]; }
                fwDenied = l == null;
                if (l != null) fwRules = l;
                fwViewKey = "";
                Invalidate();
            });
        });
    }

    // правила не віддаються без прав: помічник читає їх (лише читання) - один запит UAC
    void FwUnlock()
    {
        if (fwAsk || fwLoading || busy) return;
        fwAsk = true;
        Say(L.T("Очікую дозвіл адміністратора…"), false);
        Util.Bg(delegate
        {
            string err = "", text = null;
            string f = Elevated.Run("fwlist", "", out err);
            if (f != null)
            {
                DateTime until = DateTime.Now.AddMinutes(2);
                while (DateTime.Now < until)
                {
                    Thread.Sleep(250);
                    bool done;
                    string t = Elevated.ReadOut(f, out done);
                    if (done) { text = t; break; }
                }
                try { File.Delete(f); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                if (text == null) err = L.T("Помічник не відповів.");
            }
            List<FwRule> l = text != null ? Fw.ParseDump(text) : null;
            UiPost(delegate
            {
                fwAsk = false;
                if (l == null) { Say(err, true); return; }
                fwRules = l; fwDenied = false; fwViewKey = "";
                Say(L.T("Правила прочитано. Щоб змінювати їх, дозвіл знадобиться ще раз."), false);
                Invalidate();
            });
        });
    }

    // ---------- дії ----------
    void RunFw(List<string> lines, string okMsg, bool goBack)
    {
        busy = true;
        busyText = Elevated.IsAdmin ? L.T("Застосовую…") : L.T("Очікую дозвіл адміністратора…");
        Invalidate();
        Util.Bg(delegate
        {
            string err;
            string t = Fw.Apply(lines, out err);
            string fe;
            int bad;
            int ok = NetCfg.Summary(t, out fe, out bad);
            UiPost(delegate
            {
                busy = false;
                if (t == null) Say(err, true);
                else if (bad > 0) Say((ok > 0 ? L.T("Застосовано частково: ") : L.T("Не застосовано: ")) + fe, true);
                else { Say(okMsg, false); if (goBack) { fwPage = 0; AdHide(); } }
                Fw.RaiseChanged();
                fwRev = Fw.Rev;
                FwReload();
                ClockData.RedrawKind("fw");
            });
        });
    }

    void FwSetState(int prof, bool on)
    {
        if (busy) return;
        if (!on)
        {
            string who = prof == 3 ? L.T("усі профілі") : L.T("профіль «") + Fw.ProfNames[prof] + L.Q("»");
            if (Ask(L.T("Брандмауер"), L.T("Вимкнути брандмауер (") + who + L.T(")?\n\nБез нього ПК не перевіряє мережевий трафік: програми й пристрої з мережі зможуть підключатися без обмежень. Вимикайте лише ненадовго й увімкніть знову."),
                    L.T("Вимкнути"), L.T("Скасувати")) != 0) return;
        }
        List<string> l = new List<string>();
        l.Add(Fw.PlState(prof, on));
        RunFw(l, on ? L.T("Брандмауер увімкнено.") : L.T("Брандмауер вимкнено."), false);
    }

    void FwToggle(FwRule r)
    {
        if (busy || !r.Plain) return;
        List<string> l = new List<string>();
        l.Add(Fw.PlEnable(r.Name, !r.Active));
        RunFw(l, r.Active ? L.T("Правило вимкнено.") : L.T("Правило увімкнено."), false);
    }

    void FwDelete(FwRule r)
    {
        if (busy || !r.Plain) return;
        if (Ask(L.T("Брандмауер"), L.T("Видалити правило «") + r.Name + L.Q("»?\n\n") + r.Summary + L.T("\n\nПовернути його можна лише створивши знову."), L.T("Видалити"), L.T("Скасувати")) != 0) return;
        List<string> l = new List<string>();
        l.Add(Fw.PlDel(r.Name));
        RunFw(l, L.T("Правило видалено."), false);
    }

    void FwOpenAdd()
    {
        tbFwName.Text = ""; tbFwPort.Text = ""; tbFwApp.Text = "";
        addIn = true; addAllow = true; addProto = "tcp"; fwErr = "";
        fwPage = 1;
        AdHide();
        Invalidate();
    }

    void FwBrowse()
    {
        using (OpenFileDialog d = new OpenFileDialog())
        {
            d.Title = L.T("Програма для правила");
            d.Filter = L.T("Програми (*.exe)|*.exe");
            if (d.ShowDialog(this) == DialogResult.OK) { tbFwApp.Text = d.FileName; fwErr = ""; Invalidate(); }
        }
    }

    void FwAdd()
    {
        if (busy) return;
        string name = tbFwName.Text.Trim(), port = tbFwPort.Text.Trim().Replace(" ", ""), app = tbFwApp.Text.Trim().Trim('"');
        string err = Fw.CheckNew(name, addProto, port, app);
        if (err.Length == 0)
            foreach (FwRule r in fwRules) if (string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) { err = L.T("Правило з такою назвою вже є."); break; }
        if (err.Length > 0) { fwErr = err; Invalidate(); return; }
        fwErr = "";
        string what = (addAllow ? L.T("Дозволити") : L.T("Заблокувати")) + (addIn ? L.T(" вхідне") : L.T(" вихідне")) + " · " + (addProto == "any" ? L.T("будь-який протокол") : addProto.ToUpperInvariant()) +
                      (port.Length > 0 ? L.T(" · порт ") + port : "") + (app.Length > 0 ? L.T("\nПрограма: ") + app : "");
        string warn = addAllow ? (addIn ? L.T("\n\nВхідний дозвіл відкриває цей порт чи програму для мережі: робіть так лише для того, чим користуєтесь.") : "")
                               : L.T("\n\nБлокування може завадити програмам працювати. Якщо щось зламається - вимкніть чи видаліть це правило.");
        if (Ask(L.T("Брандмауер"), L.T("Додати правило «") + name + L.Q("»?\n\n") + what + warn, L.T("Додати"), L.T("Скасувати")) != 0) return;
        List<string> l = new List<string>();
        l.Add(Fw.PlAdd(name, addIn, addAllow, addProto, port, app));
        RunFw(l, L.T("Правило «") + name + L.T("» додано."), true);
    }

    // ---------- малювання ----------
    void FwFilter()
    {
        string key = fwDir + "|" + fwMine + "|" + tbSearch.Text + "|" + fwRules.Count + "|" + fwRules.GetHashCode();
        if (key == fwViewKey) return;
        fwViewKey = key;
        fwView.Clear();
        string q = tbSearch.Text.Trim();
        foreach (FwRule r in fwRules)
        {
            if (fwMine && !r.Plain) continue;
            if (fwDir == 1 && !r.In) continue;
            if (fwDir == 2 && r.In) continue;
            if (q.Length > 0 && r.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0 && r.Summary.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0 &&
                r.App.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0 && r.Group.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
            fwView.Add(r);
        }
    }

    void PaintFw(Graphics g, Rectangle r)
    {
        if (tbHost.Visible) tbHost.Visible = false;
        placed.Clear();
        if (!fwRead && !fwLoading) FwReload();
        int bot = r.Bottom - Px(38);
        if (fwPage == 1) FwAddPage(g, r, bot); else FwMain(g, r, bot);
        HideUnplaced();
        Rectangle nr = new Rectangle(r.X, r.Bottom - Px(36), r.Width, Px(36));
        if (busy) TxtW(g, busyText, FS, nr, P.Sub);
        else if (NoteLive) TxtW(g, Note, FS, nr, NoteBad ? P.Bad : P.Good);
    }

    void FwMain(Graphics g, Rectangle r, int bot)
    {
        int y = r.Y;
        int onN = 0;
        for (int i = 0; i < 3; i++) if (fwOn[i]) onN++;
        bool anyMan = fwMan[0] || fwMan[1] || fwMan[2];
        Color sc = !fwRead ? P.Sub : (onN == 3 ? P.Good : (onN == 0 ? P.Bad : P.Warn));
        string st = !fwRead ? "…" : (onN == 3 ? L.T("Увімкнено") : (onN == 0 ? L.T("Вимкнено") : L.T("Частково")));

        Rectangle top = new Rectangle(r.X, y, r.Width, Px(78));
        Card(g, top);
        int isz = Px(40);
        using (SolidBrush b = new SolidBrush(Color.FromArgb(40, sc))) g.FillEllipse(b, top.X + Px(12), top.Y + (top.Height - Px(54)) / 2, Px(54), Px(54));
        Ico.Draw(g, "shield", new RectangleF(top.X + Px(19), top.Y + (top.Height - isz) / 2f, isz, isz), sc);
        TxtL(g, L.T("Брандмауер Windows"), FS, new Rectangle(top.X + Px(80), top.Y + Px(10), top.Width - Px(240), Px(18)), P.Sub);
        TxtL(g, st, FBig, new Rectangle(top.X + Px(80), top.Y + Px(28), top.Width - Px(240), Px(40)), sc);
        bool allOn = onN == 3;
        Btn(g, new Rectangle(top.Right - Px(150), top.Y + Px(22), Px(138), Px(34)), allOn ? L.T("Вимкнути все") : L.T("Увімкнути все"), delegate { FwSetState(3, !allOn); }, allOn ? 0 : 1, fwRead && !busy && !anyMan);
        y = top.Bottom + Px(8);

        int pw = (r.Width - Px(12)) / 3;
        for (int i = 0; i < 3; i++)
        {
            Rectangle pr = new Rectangle(r.X + i * (pw + Px(6)), y, pw, Px(40));
            Card(g, pr);
            int k = i;
            Rectangle inner = Rectangle.Inflate(pr, -Px(12), -Px(6));
            if (fwMan[i]) { TxtL(g, Fw.ProfNames[i], F, new Rectangle(inner.X, inner.Y, inner.Width - Px(20), inner.Height), P.Sub); Ico.Draw(g, "lock", new RectangleF(inner.Right - Px(14), inner.Y + (inner.Height - Px(14)) / 2f, Px(14), Px(14)), P.Sub); Tip(pr, L.T("Цей профіль задано груповою політикою - змінити тут не можна")); }
            else Switch(g, inner, Fw.ProfNames[i], fwOn[i], fwRead && !busy ? (Action)delegate { FwSetState(k, !fwOn[k]); } : null);
        }
        y += Px(48);

        // фільтри
        int x = r.X;
        Chip(g, new Rectangle(x, y, Px(60), Px(30)), L.T("Мої"), fwMine, delegate { fwMine = true; fwScroll = 0; });
        x += Px(64);
        Chip(g, new Rectangle(x, y, Px(60), Px(30)), L.T("Усі"), !fwMine, delegate { fwMine = false; fwScroll = 0; });
        x += Px(72);
        Chip(g, new Rectangle(x, y, Px(76), Px(30)), L.T("Вхідні"), fwDir == 1, delegate { fwDir = fwDir == 1 ? 0 : 1; fwScroll = 0; });
        x += Px(80);
        Chip(g, new Rectangle(x, y, Px(80), Px(30)), L.T("Вихідні"), fwDir == 2, delegate { fwDir = fwDir == 2 ? 0 : 2; fwScroll = 0; });
        Btn(g, new Rectangle(r.Right - Px(138), y, Px(138), Px(30)), L.T("Додати правило"), FwOpenAdd, 1, !busy && !fwDenied);
        y += Px(36);
        Rectangle sr = new Rectangle(r.X, y, r.Width, Px(30));
        Fld(g, tbSearch, sr);
        if (tbSearch.Text.Length == 0 && !tbSearch.Focused) TxtL(g, L.T("Пошук за назвою, портом чи програмою"), FS, new Rectangle(sr.X + Px(12), sr.Y, sr.Width - Px(24), sr.Height), Util.Blend(P.Field, P.Sub, 0.8f));
        y += Px(38);

        Rectangle foot = new Rectangle(r.X, bot - Px(18), r.Width, Px(18));
        Rectangle list = new Rectangle(r.X, y, r.Width, Math.Max(Px(80), bot - y - Px(22)));
        if (fwDenied)
        {
            TxtW(g, L.T("Windows не віддає список правил без прав адміністратора. Читання лише показує їх, нічого не змінює."), F, new Rectangle(list.X + Px(20), list.Y + Px(20), list.Width - Px(40), Px(60)), P.Sub);
            Btn(g, new Rectangle(list.X + Px(20), list.Y + Px(86), list.Width - Px(40), Px(34)), L.T("Показати правила"), FwUnlock, 1, !fwAsk && !busy);
            return;
        }
        FwFilter();
        int rowH = Px(56), total = fwView.Count * rowH;
        fwScroll = Math.Max(0, Math.Min(Math.Max(0, total - list.Height), fwScroll));
        if (fwView.Count == 0)
        {
            string msg = fwLoading ? L.T("Читаю правила…") : (fwMine && tbSearch.Text.Length == 0 && fwRules.Count > 0 ? L.T("Власних правил поки немає. Натисніть «Додати правило» або «Усі», щоб побачити системні.") : L.T("Нічого не знайдено"));
            TxtC(g, msg, F, Rectangle.Inflate(list, -Px(20), -Px(10)), P.Sub);
        }
        GraphicsState gs = g.Save();
        g.SetClip(list);
        for (int i = 0; i < fwView.Count; i++)
        {
            FwRule fr = fwView[i];
            Rectangle rr = new Rectangle(list.X, list.Y + i * rowH - fwScroll, list.Width - (total > list.Height ? Px(10) : 0), rowH - Px(6));
            if (rr.Bottom < list.Y || rr.Top > list.Bottom) continue;
            Card(g, rr);
            Color bc = fr.Allow ? P.Good : P.Bad;
            Rectangle bd = new Rectangle(rr.X + Px(10), rr.Y + (rr.Height - Px(30)) / 2, Px(30), Px(30));
            using (SolidBrush b = new SolidBrush(Color.FromArgb(fr.Active ? 60 : 28, bc))) Util.FillRound(g, b, bd, Px(8));
            Ico.Draw(g, fr.Allow ? "check" : "close", new RectangleF(bd.X + Px(7), bd.Y + Px(7), Px(16), Px(16)), fr.Active ? bc : Util.Blend(P.Card, bc, 0.5f));
            int rw = fr.Plain ? Px(92) : Px(92);
            Color nc = fr.Active ? P.Fg : P.Sub;
            TxtL(g, fr.Name, FB, new Rectangle(bd.Right + Px(10), rr.Y + Px(6), rr.Right - bd.Right - rw - Px(20), Px(22)), nc);
            TxtL(g, fr.Summary + (fr.Active ? "" : L.T(" · вимкнено")), FS, new Rectangle(bd.Right + Px(10), rr.Y + Px(28), rr.Right - bd.Right - rw - Px(20), Px(18)), P.Sub);
            Rectangle vis = Rectangle.Intersect(rr, list);
            if (fr.Plain)
            {
                FwRule f2 = fr;
                Rectangle tr = new Rectangle(rr.Right - Px(92), rr.Y + (rr.Height - Px(24)) / 2, Px(46), Px(24));
                Rectangle dr = new Rectangle(rr.Right - Px(38), rr.Y + (rr.Height - Px(28)) / 2, Px(28), Px(28));
                if (list.Contains(tr)) { Switch(g, tr, "", fr.Active, delegate { FwToggle(f2); }); Tip(tr, fr.Active ? L.T("Вимкнути правило") : L.T("Увімкнути правило")); }
                if (list.Contains(dr)) { IconBtn(g, dr, "trash", delegate { FwDelete(f2); }, false); Tip(dr, L.T("Видалити правило")); }
            }
            else
            {
                Ico.Draw(g, "lock", new RectangleF(rr.Right - Px(30), rr.Y + (rr.Height - Px(14)) / 2f, Px(14), Px(14)), P.Sub);
                Rectangle lr = new Rectangle(rr.Right - Px(92), rr.Y, Px(92), rr.Height);
                if (!vis.IsEmpty) Tip(Rectangle.Intersect(lr, list), L.T("Системне правило Windows чи програми: показано, але тут не змінюється"));
            }
        }
        g.Restore(gs);
        if (total > list.Height)
        {
            Wheel(list, delegate (int dd) { fwScroll -= dd > 0 ? rowH : -rowH; Invalidate(); });
            VBar(g, new Rectangle(list.Right - Px(6), list.Y + Px(2), Px(4), list.Height - Px(4)), total, list.Height, fwScroll, delegate (int v) { fwScroll = v; Invalidate(); });
        }
        int mine = 0;
        foreach (FwRule fr in fwRules) if (fr.Plain) mine++;
        TxtL(g, L.T("Показано ") + fwView.Count + L.T(" з ") + fwRules.Count + L.T(" · власних: ") + mine, FS, foot, P.Sub);
    }

    void FwAddPage(Graphics g, Rectangle r, int bot)
    {
        int y = Back(g, r, L.T("Нове правило"), delegate { fwPage = 0; fwErr = ""; AdHide(); });
        Lbl(g, new Rectangle(r.X, y, r.Width, Px(18)), L.T("Назва правила"));
        y += Px(20);
        Fld(g, tbFwName, new Rectangle(r.X, y, r.Width, Px(32)));
        y += Px(40);
        int hw = (r.Width - Px(12)) / 2;
        Lbl(g, new Rectangle(r.X, y, hw, Px(18)), L.T("Напрямок"));
        Lbl(g, new Rectangle(r.X + hw + Px(12), y, hw, Px(18)), L.T("Дія"));
        y += Px(20);
        int cw = (hw - Px(4)) / 2;
        Chip(g, new Rectangle(r.X, y, cw, Px(30)), L.T("Вхідне"), addIn, delegate { addIn = true; });
        Chip(g, new Rectangle(r.X + cw + Px(4), y, cw, Px(30)), L.T("Вихідне"), !addIn, delegate { addIn = false; });
        Chip(g, new Rectangle(r.X + hw + Px(12), y, cw, Px(30)), L.T("Дозволити"), addAllow, delegate { addAllow = true; });
        Chip(g, new Rectangle(r.X + hw + Px(12) + cw + Px(4), y, cw, Px(30)), L.T("Заблокувати"), !addAllow, delegate { addAllow = false; });
        y += Px(38);
        Lbl(g, new Rectangle(r.X, y, r.Width, Px(18)), L.T("Протокол"));
        y += Px(20);
        int pw = (r.Width - Px(12)) / 3;
        Chip(g, new Rectangle(r.X, y, pw, Px(30)), "TCP", addProto == "tcp", delegate { addProto = "tcp"; });
        Chip(g, new Rectangle(r.X + pw + Px(6), y, pw, Px(30)), "UDP", addProto == "udp", delegate { addProto = "udp"; });
        Chip(g, new Rectangle(r.X + (pw + Px(6)) * 2, y, pw, Px(30)), L.T("Будь-який"), addProto == "any", delegate { addProto = "any"; });
        y += Px(38);
        Lbl(g, new Rectangle(r.X, y, r.Width, Px(18)), L.T("Порт або діапазон (80, 443, 5000-5010) - необов'язково, якщо є програма"));
        y += Px(20);
        Fld(g, tbFwPort, new Rectangle(r.X, y, r.Width, Px(32)));
        y += Px(40);
        Lbl(g, new Rectangle(r.X, y, r.Width, Px(18)), L.T("Програма (.exe) - необов'язково, якщо є порт"));
        y += Px(20);
        Fld(g, tbFwApp, new Rectangle(r.X, y, r.Width - Px(100), Px(32)));
        Btn(g, new Rectangle(r.Right - Px(92), y, Px(92), Px(32)), L.T("Огляд…"), FwBrowse, 0, true);
        y += Px(40);
        if (fwErr.Length > 0) { TxtW(g, fwErr, FS, new Rectangle(r.X, y, r.Width, Px(34)), P.Bad); y += Px(36); }
        Btn(g, new Rectangle(r.X, y, r.Width, Px(36)), L.T("Додати правило"), FwAdd, 1, !busy);
        y += Px(44);
        if (y + Px(34) <= bot)
            TxtW(g, L.T("Потрібен порт або програма: правило «для всього» не створюється. Власне правило можна вимкнути чи видалити у списку."), FS, new Rectangle(r.X, y, r.Width, Math.Max(Px(34), bot - y)), P.Sub);
    }
}
