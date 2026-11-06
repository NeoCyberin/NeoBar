using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

// ======================================================================
//  Вікно «Безпека»: результат сканування за вкладками, пояснення кожної знахідки й дії з підтвердженням.
//  Нічого не видаляється: автозапуск вимикається так само, як у Диспетчері завдань (і так само вмикається назад),
//  процес завершується лише після «Так».
// ======================================================================
sealed class SecWindow : VtWindow
{
    readonly int[] scroll = new int[5];
    bool all;
    volatile bool fixing;

    public SecWindow(NeoBar bar) : base(bar, L.T("Безпека"), 620, 780)
    { Glyph = "\uEA18";
        SecScan.Changed += OnData;
        SecFix.Load();
        SetTabs();
        SecTray.Alert(false);          // вікно відкрито - червона крапка на значку в треї гасне
    }

    // файли, перетягнуті у вікно, - на перевірку
    protected override void Dropped(string[] files) { foreach (string f in files) if (File.Exists(f)) Details(f); }

    void PickCheck()
    {
        using (OpenFileDialog d = new OpenFileDialog())
        {
            d.Title = L.T("Файли для перевірки на підозрілість");
            d.Filter = L.T("Усі файли|*.*");
            d.Multiselect = true;
            if (d.ShowDialog(this) == DialogResult.OK) foreach (string f in d.FileNames) Details(f);
        }
    }

    void OnData()
    {
        try
        {
            if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(delegate
            {
                SetTabs();
                Invalidate();
                if (!SecScan.Busy && Bar != null) Bar.WidgetRedraw("security");
            }));
        }
        catch { }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SecScan.Changed -= OnData;
        base.OnFormClosed(e);
    }

    static int Flagged(List<Finding> l)
    {
        int n = 0;
        foreach (Finding f in l) if (!f.Off && !f.Ok && !f.Allowed && f.Level >= 1) n++;
        return n;
    }

    void SetTabs()
    {
        List<Finding>[] ls = { SecScan.Proc, SecScan.Auto, SecScan.Svc, SecScan.Sys };
        string[] nm = { L.T("Процеси"), L.T("Автозапуск"), L.T("Служби"), L.T("Система") };
        string[] t = new string[5];
        for (int i = 0; i < 4; i++)
        {
            int n = SecScan.LastAt == DateTime.MinValue ? 0 : Flagged(ls[i]);
            t[i] = nm[i] + (n > 0 ? " · " + n : "");
        }
        t[4] = L.T("Списки");
        Tabs = t;
    }

    void Start()
    {
        if (SecScan.Busy) return;
        bool procs = true;
        string ac;
        if (SecScan.AntiCheatRunning(out ac))
        {
            int a = Ask(L.T("Безпека"), L.T("Працює служба античіта (") + ac + L.T(").\n\nПеревірка процесів лише читає їхні назви, шляхи й параметри запуску - так само, як Диспетчер завдань; у процеси нічого не впроваджується і їхня пам'ять не читається. Але щоб не ризикувати, поки гра запущена, процеси можна пропустити."),
                        L.T("Пропустити процеси"), L.T("Перевірити все"), L.T("Скасувати"));
            if (a == 2 || a < 0) return;
            procs = a == 1;
        }
        SecScan.Run(procs);
        Invalidate();
    }

    int fitTotal;

    List<Finding> ShownItems()
    {
        List<Finding> src = Tab == 0 ? SecScan.Proc : (Tab == 1 ? SecScan.Auto : (Tab == 2 ? SecScan.Svc : SecScan.Sys));
        if (Tab == 3 || all)
        {
            if (Tab != 2 || !all) return src;
            // «усе» для служб - лише сторонні (системних кілька сотень)
            List<Finding> r = new List<Finding>();
            foreach (Finding f in src) if (f.Score > 0 || f.Place != SecHeur.PSys) r.Add(f);
            return r;
        }
        List<Finding> l = new List<Finding>();
        foreach (Finding f in src) if (f.Level >= 1 && !f.Allowed) l.Add(f);
        return l;
    }

    // рядки картки: причини, потім довідкові
    List<string> Lines(Finding f)
    {
        List<string> l = new List<string>();
        foreach (string w in f.Why) l.Add((f.Ok ? "" : "• ") + w);
        return l;
    }

    int CardH(Finding f, int w)
    {
        int h = Px(34);
        if (f.Path.Length > 0 || f.Cmd.Length > 0) h += Px(18);
        if (f.Detail.Length > 0 || f.Area != "sys") h += Px(18);
        foreach (string s in Lines(f)) h += TextRenderer.MeasureText(s, FS, new Size(w - Px(28), 0), TextFormatFlags.WordBreak).Height + Px(2);
        if (f.Open) h += Px(8) + ExtraH(f, w);
        int rows = ActRows(Actions(f), w);
        if (rows > 0) h += rows * Px(38) + Px(2);
        return h + Px(10);
    }

    // розгорнута картка («Детальніше»): докладний аналіз файла - те, чого немає в короткому описі
    List<string> Extra(Finding f)
    {
        List<string> l = new List<string>();
        FileReport r = f.Rep;
        if (r == null || !r.Done) { l.Add(L.T("Аналіз файла…")); return l; }
        if (r.Error.Length > 0) { l.Add(L.T("Не вдалося прочитати файл: ") + r.Error); return l; }
        l.Add(L.T("Тип: ") + (r.Kind.Length > 0 ? r.Kind : L.T("файл")) + " · " + SizeText(r.Size));
        foreach (string s in r.Why)
        {
            // те саме вже сказано у короткому описі (однакове чи з тим самим початком - наприклад, про цифровий підпис)
            bool dup = false;
            foreach (string x in f.Why) if (x == s || (x.Length >= 20 && s.Length >= 20 && string.CompareOrdinal(x, 0, s, 0, 20) == 0)) dup = true;
            if (!dup) l.Add("• " + s);
        }
        foreach (string s in r.Info) l.Add(s);
        if (r.VtText.Length > 0) l.Add(r.VtText);
        if (r.Sha256.Length > 0) l.Add("SHA-256: " + r.Sha256);
        return l;
    }

    int ExtraH(Finding f, int w)
    {
        int h = 0;
        foreach (string s in Extra(f)) h += TextRenderer.MeasureText(s, FS, new Size(w - Px(28), 0), TextFormatFlags.WordBreak).Height + Px(2);
        return h;
    }

    void EnsureReport(Finding f, Action then)
    {
        if (f.Rep != null && f.Rep.Done) { if (then != null) then(); return; }
        if (f.Busy) return;
        f.Busy = true;
        string path = f.Path;
        Bg(delegate
        {
            FileReport res = FileCheck.Analyze(path);
            Ui(delegate { f.Rep = res; f.Busy = false; if (then != null) then(); });
        });
    }

    void CardOpen(Finding f)
    {
        f.Open = !f.Open;
        if (f.Open) EnsureReport(f, null);
    }

    void CardVt(Finding f)
    {
        if (f.Rep != null && f.Rep.VtDone && f.Rep.Sha256.Length > 0) { OpenVt(f.Rep.Sha256); return; }
        f.Open = true;
        EnsureReport(f, delegate { if (f.Rep.Sha256.Length > 0) StartVt(f.Rep); });
    }

    string VtLabel(Finding f)
    {
        if (f.Rep != null && f.Rep.VtDone) return L.T("Звіт VirusTotal");
        if (f.Rep != null && f.Rep.VtState == 1) return L.T("Перевірка…");
        return L.T("Перевірити на VirusTotal");
    }

    int ActW(Act a) { return Math.Max(Px(84), TextRenderer.MeasureText(a.Text, F).Width + Px(24)); }

    // кнопки переносяться на новий рядок, якщо не вміщаються
    int ActRows(List<Act> acts, int w)
    {
        if (acts.Count == 0) return 0;
        int rows = 1, x = Px(14);
        foreach (Act a in acts)
        {
            int bw = ActW(a);
            if (x + bw > w - Px(10) && x > Px(14)) { rows++; x = Px(14); }
            x += bw + Px(8);
        }
        return rows;
    }

    sealed class Act { public string Text; public Action Do; public int Style; }

    List<Act> Actions(Finding f)
    {
        List<Act> a = new List<Act>();
        Finding ff = f;
        foreach (Fix fx in f.Fixes) { Fix x = fx; a.Add(new Act { Text = x.Button, Do = delegate { RunFix(ff, x); }, Style = 1 }); }
        bool file = f.Path.Length > 0 && File.Exists(f.Path);
        if (f.Area == "proc" && f.Level >= 1 && !f.Allowed && f.Pids.Count > 0) a.Add(new Act { Text = f.Pids.Count > 1 ? L.T("Завершити (") + f.Pids.Count + ")" : L.T("Завершити"), Do = delegate { KillIt(ff); }, Style = 0 });
        if (f.AutoKind.Length > 0) a.Add(new Act { Text = f.Off ? L.T("Увімкнути") : L.T("Вимкнути"), Do = delegate { Toggle(ff); }, Style = f.Off ? 0 : (f.Level >= 1 ? 1 : 0) });
        if (file) a.Add(new Act { Text = L.T("Відкрити теку"), Do = delegate { ShowInFolder(ff.Path); }, Style = 0 });
        if (SecLists.CanIsolate(f) && !f.Ok) a.Add(new Act { Text = L.T("Ізолювати"), Do = delegate { IsolateIt(ff); }, Style = 0 });
        if (f.Allowed) a.Add(new Act { Text = L.T("Прибрати з дозволених"), Do = delegate { Unallow(ff); }, Style = 2 });
        else if (!f.Ok && (f.Level >= 1 || (f.Area == "sys" && f.Score > 0))) a.Add(new Act { Text = L.T("Дозволити"), Do = delegate { AllowIt(ff); }, Style = 2 });
        if (file && f.Level >= 1) a.Add(new Act { Text = VtLabel(f), Do = delegate { CardVt(ff); }, Style = 0 });
        if (file) a.Add(new Act { Text = f.Open ? L.T("Згорнути") : L.T("Детальніше"), Do = delegate { CardOpen(ff); }, Style = 2 });
        if (f.Area == "svc") a.Add(new Act { Text = L.T("Служби Windows"), Do = delegate { try { Process.Start("services.msc"); } catch { } }, Style = 2 });
        foreach (string[] l in f.Links) { string tg = l[1]; a.Add(new Act { Text = l[0], Do = delegate { Open(tg); }, Style = 2 }); }
        return a;
    }

    void KillIt(Finding f)
    {
        string text = L.T("Завершити «") + f.Title + L.Q("»") + (f.Pids.Count > 1 ? L.T(" (процесів: ") + f.Pids.Count + ")" : "") + "?\n\n" +
                      L.T("Незбережені дані в цій програмі пропадуть. Якщо вона є в автозапуску, після перезавантаження запуститься знову - вимкніть і її автозапуск.");
        if (Ask(L.T("Безпека"), text, L.T("Завершити"), L.T("Скасувати")) != 0) return;
        string err = SecScan.Kill(f);
        if (err.Length > 0) Say(L.T("Не вдалося: ") + err, true);
        else { f.Pids.Clear(); f.Detail = L.T("завершено"); Say(L.T("Процес завершено."), false); }
        Invalidate();
    }

    void Toggle(Finding f)
    {
        bool on = f.Off;
        string err = f.AutoKind == "task" ? SecScan.SetTask(f, on) : SecScan.SetApproved(f, on);
        if (err.Length > 0) Say(err, true);
        else Say(on ? L.T("Увімкнено «") + f.Title + L.Q("».") : L.T("Вимкнено «") + f.Title + L.T("». Запис лишився - повернути можна тут або в Диспетчері завдань."), false);
        SetTabs();
        Invalidate();
    }

    // ---------- білий список і ізоляція ----------
    void AllowIt(Finding f)
    {
        string what = f.Area == "sys" ? L.T("цю перевірку") : (f.Path.Length > 0 ? L.Q("«") + Path.GetFileName(f.Path) + L.T("» (поки файл не зміниться)") : L.Q("«") + f.Title + L.Q("»"));
        if (Ask(L.T("Безпека"), L.T("Більше не позначати ") + what + L.T(" як підозріле?\n\nПовернути можна на вкладці «Списки»."), L.T("Дозволити"), L.T("Скасувати")) != 0) return;
        try { SecLists.Allow(f); } catch (Exception ex) { Say(ex.Message, true); return; }
        Say(L.T("Додано до дозволених."), false);
        SetTabs();
        if (Bar != null) Bar.WidgetRedraw("security");
        Invalidate();
    }

    void Unallow(Finding f)
    {
        string key = SecLists.KeyOf(f);
        foreach (ListEntry e in SecLists.Of('A')) if (e.Key == key) SecLists.Remove(e);
        f.Allowed = false;
        SetTabs();
        Invalidate();
    }

    static bool SamePath(Finding a, Finding b) { return a.Path.Length > 0 && string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase); }

    void IsolateIt(Finding f)
    {
        if (fixing) return;
        string what;
        Fix fx;
        try { fx = SecLists.PlanIsolate(f, out what); }
        catch (Exception ex) { Say(ex.Message, true); return; }
        List<int> pids = new List<int>();
        foreach (Finding p in SecScan.Proc) if (SamePath(p, f)) pids.AddRange(p.Pids);
        string text = L.T("Ізолювати «") + f.Title + L.T("»?\n\nБуде зроблено:\n• ") + (pids.Count > 0 ? L.T("завершити процеси (") + pids.Count + ");\n• " : "") + what.Replace(";\n", ";\n• ") +
                      L.T(".\n\nУсе можна повернути: вкладка «Списки» → «Відновити».") + (fx.Admin && !Elevated.IsAdmin ? L.T("\n\nWindows попросить дозвіл адміністратора.") : "");
        if (Ask(L.T("Безпека"), text, L.T("Ізолювати"), L.T("Скасувати")) != 0) return;
        if (pids.Count > 0)
        {
            Finding k = new Finding();
            k.Pids.AddRange(pids);
            string kerr = SecScan.Kill(k);
            if (kerr.Length > 0) { Say(L.T("Процес не вдалося завершити: ") + kerr + L.T(" Нічого не змінено."), true); return; }
        }
        fixing = true;
        Say(L.T("Ізоляція…"), false);
        Thread t = new Thread(delegate ()
        {
            if (pids.Count > 0) Thread.Sleep(700);          // процесу треба мить, щоб звільнити файл
            string err;
            try { err = SecLists.Isolate(f, fx); }
            catch (Exception ex) { err = ex.Message; }
            try
            {
                BeginInvoke(new Action(delegate
                {
                    fixing = false;
                    if (err.Length == 0)
                    {
                        foreach (List<Finding> l in new List<Finding>[] { SecScan.Proc, SecScan.Auto, SecScan.Svc })
                            foreach (Finding x in l)
                                if (SamePath(x, f)) { x.Ok = true; x.Score = 0; x.Fixes.Clear(); x.Pids.Clear(); if (x.AutoKind.Length > 0) x.Off = true; x.Why.Insert(0, L.T("✓ Ізольовано.")); }
                        Say(L.T("Ізольовано. Повернути можна на вкладці «Списки»."), false);
                        SetTabs();
                        if (Bar != null) Bar.WidgetRedraw("security");
                    }
                    else Say(L.T("Не вдалося ізолювати: ") + err, true);
                    Invalidate();
                }));
            }
            catch { }
        });
        t.IsBackground = true;
        t.Start();
        Invalidate();
    }

    void ListAction(ListEntry e, int act)
    {
        if (fixing) return;
        if (act == 0)
        {
            if (Ask(L.T("Безпека"), L.T("Повернути «") + e.Title + L.T("» з ізоляції?\n\nФайл повернеться на місце (") + e.Path + L.T("), а вимкнені автозапуск і служби - увімкнуться знову.") +
                    (e.Admin && !Elevated.IsAdmin ? L.T("\n\nWindows попросить дозвіл адміністратора.") : ""), L.T("Відновити"), L.T("Скасувати")) != 0) return;
        }
        else if (act == 1)
        {
            if (Ask(L.T("Безпека"), L.T("Видалити копію «") + e.Title + L.T("» з ізоляції назавжди?\n\nПовернути файл уже не вийде. Запис лишиться в списку: якщо такий файл з'явиться знову, сканер його позначить."), L.T("Видалити копію"), L.T("Скасувати")) != 0) return;
        }
        fixing = true;
        Thread t = new Thread(delegate ()
        {
            string err;
            try { err = act == 0 ? SecLists.Restore(e) : SecLists.Purge(e); }
            catch (Exception ex) { err = ex.Message; }
            try
            {
                BeginInvoke(new Action(delegate
                {
                    fixing = false;
                    if (err.Length > 0) Say(L.T("Не вдалося: ") + err, true);
                    else Say(act == 0 ? L.T("Відновлено. Щоб побачити актуальний стан, проскануйте ще раз.") : L.T("Копію видалено."), false);
                    Invalidate();
                }));
            }
            catch { }
        });
        t.IsBackground = true;
        t.Start();
        Invalidate();
    }

    int ListCardH(ListEntry e) { return e.Kind == 'Q' ? Px(96) : Px(66); }

    void PaintLists(Graphics g, Rectangle list)
    {
        List<ListEntry> q = SecLists.Of('Q'), a = SecLists.Of('A');
        int w = list.Width - Px(10);
        int total = Px(70) + Px(34) + (q.Count == 0 ? Px(30) : 0) + Px(34) + (a.Count == 0 ? Px(30) : 0);
        foreach (ListEntry e in q) total += ListCardH(e) + Px(8);
        foreach (ListEntry e in a) total += ListCardH(e) + Px(8);
        int sc = scroll[4] = Math.Max(0, Math.Min(Math.Max(0, total - list.Height), scroll[4]));
        GraphicsState st = g.Save();
        g.SetClip(list);
        int cy = list.Y - sc;
        // ключ VirusTotal
        Rectangle vc = new Rectangle(list.X, cy, w, Px(60));
        Card(g, vc);
        bool has = Vt.Key.Length > 0;
        TxtL(g, L.T("Ключ VirusTotal API"), FB, new Rectangle(vc.X + Px(14), vc.Y + Px(6), vc.Width - Px(200), Px(24)), P.Fg);
        TxtL(g, has ? L.T("Задано: перевірка й надсилання файлів прямо з NeoBar.") : L.T("Не задано: для перевірки відкривається сайт."), FS, new Rectangle(vc.X + Px(14), vc.Y + Px(30), vc.Width - Px(200), Px(20)), P.Sub);
        Rectangle kb = new Rectangle(vc.Right - Px(174), vc.Y + Px(14), Px(160), Px(32));
        if (Rectangle.Intersect(kb, list) == kb)
            Btn(g, kb, has ? L.T("Змінити ключ") : L.T("Задати ключ"), delegate { AskVtKey(); Invalidate(); }, 0, true);
        cy += Px(70);
        TxtL(g, L.T("Ізольовані (чорний список)") + (q.Count > 0 ? " · " + q.Count : ""), FB, new Rectangle(list.X, cy + Px(4), w, Px(24)), P.Fg);
        cy += Px(34);
        if (q.Count == 0) { TxtL(g, L.T("Порожньо. Кнопка «Ізолювати» є на картках підозрілих файлів."), FS, new Rectangle(list.X, cy, w, Px(22)), P.Sub); cy += Px(30); }
        foreach (ListEntry e in q)
        {
            Rectangle c = new Rectangle(list.X, cy, w, ListCardH(e));
            cy += c.Height + Px(8);
            if (c.Bottom < list.Y || c.Y > list.Bottom) continue;
            bool copy = e.QFile.Length > 0 && File.Exists(e.QFile);
            Card(g, c, Util.Blend(P.Card, P.Bad, 0.06f));
            TxtL(g, e.Title, FB, new Rectangle(c.X + Px(14), c.Y + Px(6), c.Width - Px(28), Px(22)), P.Fg);
            Txt(g, e.Path, FS, new Rectangle(c.X + Px(14), c.Y + Px(28), c.Width - Px(28), Px(18)), P.Sub, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine);
            TxtL(g, e.When + " · " + (copy ? L.T("копія в ізоляції") : L.T("копію видалено - лишився запис для впізнавання")), FS, new Rectangle(c.X + Px(14), c.Y + Px(46), c.Width - Px(28), Px(18)), P.Sub);
            int bx = c.X + Px(14), by = c.Y + Px(64);
            ListEntry ee = e;
            List<Act> acts = new List<Act>();
            if (copy && e.Undo.Count > 0) acts.Add(new Act { Text = L.T("Відновити"), Do = delegate { ListAction(ee, 0); }, Style = 0 });
            if (copy) acts.Add(new Act { Text = L.T("Видалити копію"), Do = delegate { ListAction(ee, 1); }, Style = 2 });
            else acts.Add(new Act { Text = L.T("Прибрати зі списку"), Do = delegate { SecLists.Remove(ee); Invalidate(); }, Style = 2 });
            foreach (Act ac in acts)
            {
                Rectangle br = new Rectangle(bx, by, ActW(ac), Px(26));
                Btn(g, br, ac.Text, Rectangle.Intersect(br, list) == br ? ac.Do : null, ac.Style, !fixing);
                bx += br.Width + Px(8);
            }
        }
        cy += Px(4);
        TxtL(g, L.T("Дозволені (білий список)") + (a.Count > 0 ? " · " + a.Count : ""), FB, new Rectangle(list.X, cy + Px(4), w, Px(24)), P.Fg);
        cy += Px(34);
        if (a.Count == 0) { TxtL(g, L.T("Порожньо. Кнопка «Дозволити» прибирає знахідку з підозрілих."), FS, new Rectangle(list.X, cy, w, Px(22)), P.Sub); cy += Px(30); }
        foreach (ListEntry e in a)
        {
            Rectangle c = new Rectangle(list.X, cy, w, ListCardH(e));
            cy += c.Height + Px(8);
            if (c.Bottom < list.Y || c.Y > list.Bottom) continue;
            Card(g, c);
            TxtL(g, e.Title, FB, new Rectangle(c.X + Px(14), c.Y + Px(6), c.Width - Px(190), Px(22)), P.Fg);
            string sub = e.Path.Length > 0 ? e.Path : (e.Key.StartsWith("sys:") ? L.T("перевірка налаштувань") : e.Key);
            Txt(g, sub, FS, new Rectangle(c.X + Px(14), c.Y + Px(28), c.Width - Px(190), Px(18)), P.Sub, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine);
            TxtL(g, e.When + (e.Sha.Length > 0 ? L.T(" · діє, поки файл не зміниться") : ""), FS, new Rectangle(c.X + Px(14), c.Y + Px(44), c.Width - Px(190), Px(18)), P.Sub);
            Rectangle br = new Rectangle(c.Right - Px(164), c.Y + (c.Height - Px(30)) / 2, Px(150), Px(30));
            ListEntry ee = e;
            Btn(g, br, L.T("Прибрати"), Rectangle.Intersect(br, list) == br ? (Action)delegate
            {
                SecLists.Remove(ee);
                foreach (List<Finding> l in new List<Finding>[] { SecScan.Proc, SecScan.Auto, SecScan.Svc, SecScan.Sys })
                    foreach (Finding f in l) if (SecLists.KeyOf(f) == ee.Key) f.Allowed = false;
                SetTabs();
                Invalidate();
            } : null, 0, true);
        }
        g.Restore(st);
        Wheel(list, delegate (int d) { scroll[4] -= d > 0 ? Px(70) : -Px(70); Invalidate(); });
        VBar(g, new Rectangle(list.Right - Px(5), list.Y, Px(4), list.Height), total, list.Height, sc, delegate (int v) { scroll[4] = v; Invalidate(); });
    }

    FileCheckWindow check;

    // довільний файл (кнопка «Перевірити файл…», перетягування) - у вікні перевірки файлів
    void Details(string path)
    {
        if (check == null || check.IsDisposed)
        {
            check = new FileCheckWindow(Bar);
            check.Location = new Point(Math.Max(0, Left - Px(40)), Top + Px(40));
        }
        check.Add(path);
        check.Show();
        check.Activate();
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        int y = r.Y;
        Rectangle top = new Rectangle(r.X, y, r.Width, Px(90));
        Card(g, top);
        bool busy = SecScan.Busy;
        int bad, warn;
        SecScan.Count(out bad, out warn);
        string big, small;
        Color bc = P.Fg;
        if (busy) { big = L.T("Перевірка…"); small = SecScan.Stage; }
        else if (SecScan.LastAt == DateTime.MinValue) { big = L.T("Ще не перевіряли"); small = L.T("Процеси, автозапуск, служби й налаштування Windows - за хвилину."); }
        else if (bad + warn == 0) { big = L.T("Підозрілого не знайдено"); small = L.T("Перевірено о ") + SecScan.LastAt.ToString("HH:mm") + (SecScan.ProcSkipped ? L.T(" (без процесів)") : ""); bc = P.Good; }
        else
        {
            big = (bad > 0 ? L.T("Підозріло: ") + bad : "") + (bad > 0 && warn > 0 ? " · " : "") + (warn > 0 ? L.T("Варто перевірити: ") + warn : "");
            small = L.T("Перевірено о ") + SecScan.LastAt.ToString("HH:mm") + (SecScan.ProcSkipped ? L.T(" (без процесів)") : "");
            bc = bad > 0 ? P.Bad : P.Warn;
        }
        TxtL(g, big, FH, new Rectangle(top.X + Px(14), top.Y + Px(12), top.Width - Px(190), Px(28)), bc);
        TxtL(g, small, FS, new Rectangle(top.X + Px(14), top.Y + Px(44), top.Width - Px(190), Px(20)), P.Sub);
        Rectangle br = new Rectangle(top.Right - Px(174), top.Y + Px(9), Px(160), Px(34));
        if (busy) Btn(g, br, L.T("Зупинити"), delegate { SecScan.Stop = true; }, 0, true);
        else Btn(g, br, SecScan.LastAt == DateTime.MinValue ? L.T("Сканувати") : L.T("Сканувати знову"), Start, 1, true);
        Btn(g, new Rectangle(br.X, br.Bottom + Px(5), br.Width, Px(32)), L.T("Перевірити файл…"), PickCheck, 0, true);
        y = top.Bottom + Px(10);
        if (Tab < 3) { Switch(g, new Rectangle(r.X, y, r.Width, Px(26)), L.T("Показувати все, а не лише підозріле"), all, delegate { all = !all; Invalidate(); }); y += Px(32); }

        Rectangle list = new Rectangle(r.X, y, r.Width, r.Bottom - y - Px(44) - Px(34) - Px(32));
        if (Tab == 4)
        {
            PaintLists(g, list);
            FitTo(r, list.Y + Px(470) + Px(44) + Px(34) + Px(32), Px(470));         // вкладка «Списки» - стала висота
        }
        else
        {
            PaintFindings(g, list);
            // вікно за висотою - за вмістом (без порожнього місця при кількох знахідках), але не нижче за мінімум і не вище за екран
            FitTo(r, list.Y + Math.Min(Math.Max(fitTotal, Px(150)), Px(620)) + Px(44) + Px(34) + Px(32), Px(470));
        }
        bool watch = Bar != null && Bar.Config.SecWatch;
        Switch(g, new Rectangle(r.X, r.Bottom - Px(38) - Px(64), r.Width, Px(28)), L.T("Стежити у фоні (значок у треї) і сповіщати про нове; не під час гри"), watch && Bar.Config.SecUsed, delegate
        {
            if (Bar == null) return;
            bool on = !(Bar.Config.SecWatch && Bar.Config.SecUsed);
            SecWatch.SetWatch(on);
            Say(on ? L.T("Стеження ввімкнено: значок у треї, працює й без віджета на панелі. Процеси - щохвилини, автозапуск і служби - раз на 10-20 хв; про кожну знахідку - одне сповіщення до перезавантаження ПК.") : L.T("Фонове стеження вимкнено, значок із трея прибрано."), false);
        });
        Switch(g, new Rectangle(r.X, r.Bottom - Px(38) - Px(32), r.Width, Px(28)), L.T("«Перевірити на підозрілість» у контекстному меню файлів"), ShellMenu.Has("sec"), delegate
        {
            bool was = ShellMenu.Has("sec");
            string err = ShellMenu.Toggle("sec");
            if (err.Length > 0) Say(L.T("Не вдалося змінити меню: ") + err, true);
            else Say(was ? L.T("Пункт прибрано з контекстного меню.") : L.T("Додано: ПКМ на файлі → NeoBar → «Перевірити на підозрілість» (у Windows 11 - «Показати додаткові параметри»)."), false);
        });
        string foot = NoteLive ? Note : (fixing ? L.T("Виправлення…") : L.T("Евристики без бази сигнатур - підказка, а не вирок: ознаки бувають і в звичайних програм. Нічого не змінюється без вашого підтвердження."));
        TxtW(g, foot, FS, new Rectangle(r.X, r.Bottom - Px(38), r.Width, Px(38)), NoteLive ? (NoteBad ? P.Bad : P.Good) : P.Sub);
    }

    void PaintFindings(Graphics g, Rectangle list)
    {
        List<Finding> items = ShownItems();
        List<FixEntry> journal = Tab == 3 ? new List<FixEntry>(SecFix.Journal) : new List<FixEntry>();
        if (items.Count == 0 && journal.Count == 0)
        {
            string empty = SecScan.LastAt == DateTime.MinValue ? L.T("Натисніть «Сканувати».") :
                           (Tab == 0 && SecScan.ProcSkipped ? L.T("Процеси пропущено (працював античіт).") : L.T("Нічого підозрілого."));
            if (Tab == 0 && SecScan.LastAt != DateTime.MinValue && !SecScan.ProcSkipped)
                empty += L.T("\nПроцесів: ") + SecScan.ProcCount + L.T("; захищених системних (Windows не показує навіть шлях): ") + SecScan.NoAccess + ".";
            TxtC(g, empty.Split('\n')[0], F, new Rectangle(list.X, list.Y + Px(30), list.Width, Px(24)), P.Sub);
            if (empty.Contains("\n")) TxtC(g, empty.Split('\n')[1], FS, new Rectangle(list.X, list.Y + Px(56), list.Width, Px(20)), P.Sub);
        }
        int w = list.Width - Px(10), total = 0;
        foreach (Finding f in items) total += CardH(f, w) + Px(8);
        if (journal.Count > 0) total += Px(34) + journal.Count * Px(64);
        fitTotal = total;
        int sc = scroll[Tab] = Math.Max(0, Math.Min(Math.Max(0, total - list.Height), scroll[Tab]));
        GraphicsState st = g.Save();
        g.SetClip(list);
        int cy = list.Y - sc;
        foreach (Finding f in items)
        {
            int h = CardH(f, w);
            Rectangle c = new Rectangle(list.X, cy, w, h);
            cy += h + Px(8);
            if (c.Bottom < list.Y || c.Y > list.Bottom) continue;
            PaintCard(g, c, f, list);
        }
        if (journal.Count > 0)
        {
            // журнал виправлень: що зроблено й кнопка відкату
            TxtL(g, L.T("Зроблені виправлення (можна скасувати)"), FB, new Rectangle(list.X, cy + Px(4), w, Px(24)), P.Fg);
            cy += Px(34);
            foreach (FixEntry e in journal)
            {
                Rectangle c = new Rectangle(list.X, cy, w, Px(56));
                cy += Px(64);
                if (c.Bottom < list.Y || c.Y > list.Bottom) continue;
                Card(g, c);
                TxtL(g, e.Title, F, new Rectangle(c.X + Px(14), c.Y + Px(6), c.Width - Px(190), Px(24)), P.Fg);
                TxtL(g, e.When + (e.Admin ? L.T(" · з правами адміністратора") : ""), FS, new Rectangle(c.X + Px(14), c.Y + Px(30), c.Width - Px(190), Px(18)), P.Sub);
                Rectangle ub = new Rectangle(c.Right - Px(164), c.Y + Px(12), Px(150), Px(32));
                FixEntry ee = e;
                Btn(g, ub, L.T("Скасувати"), Rectangle.Intersect(ub, list) == ub ? (Action)delegate { UndoFix(ee); } : null, 0, !fixing);
            }
        }
        g.Restore(st);
        int tab = Tab;
        Wheel(list, delegate (int d) { scroll[tab] -= d > 0 ? Px(70) : -Px(70); Invalidate(); });
        VBar(g, new Rectangle(list.Right - Px(5), list.Y, Px(4), list.Height), total, list.Height, sc, delegate (int v) { scroll[tab] = v; Invalidate(); });
    }

    void PaintCard(Graphics g, Rectangle c, Finding f, Rectangle clip)
    {
        Color lc = f.Off || f.Allowed ? P.Sub : (f.Ok ? P.Good : (f.Level == 2 ? P.Bad : (f.Level == 1 ? P.Warn : P.Sub)));
        Card(g, c, f.Level >= 1 && !f.Off ? Util.Blend(P.Card, lc, 0.07f) : P.Card);
        using (SolidBrush b = new SolidBrush(lc)) g.FillEllipse(b, c.X + Px(12), c.Y + Px(13), Px(9), Px(9));
        string lev = f.Off ? L.T("Вимкнено") : f.Allowed ? L.T("Дозволено") : (f.Ok ? L.T("✓ Гаразд") : (f.Level == 2 ? L.T("Підозріло") : (f.Level == 1 ? L.T("Варто перевірити") : (f.Area == "sys" ? L.T("Довідково") : ""))));
        int lw = TextRenderer.MeasureText(lev, FS).Width + Px(6);
        TxtL(g, f.Title, FB, new Rectangle(c.X + Px(28), c.Y + Px(6), c.Width - Px(40) - lw, Px(24)), f.Off ? P.Sub : P.Fg);
        TxtR(g, lev, FS, new Rectangle(c.Right - lw - Px(12), c.Y + Px(6), lw, Px(24)), lc);
        int y = c.Y + Px(32);
        if (f.Path.Length > 0 || f.Cmd.Length > 0)
        {
            string p = f.Cmd.Length > 0 && (f.Area == "auto" || f.Cmd != f.Path) ? f.Cmd : f.Path;
            Txt(g, p, FS, new Rectangle(c.X + Px(14), y, c.Width - Px(28), Px(18)), P.Sub, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine);
            y += Px(18);
        }
        if (f.Detail.Length > 0 || f.Area != "sys")
        {
            string sig = f.Area == "sys" ? "" : (f.SigKind == Sig.Unknown && f.Place == SecHeur.PSys ? L.T("системна тека Windows") : Sig.Text(f.SigKind, f.Signer));
            string d = f.Detail + (f.Detail.Length > 0 && sig.Length > 0 ? " · " : "") + sig;
            TxtL(g, d, FS, new Rectangle(c.X + Px(14), y, c.Width - Px(28), Px(18)), f.SigKind == Sig.Bad ? P.Bad : P.Sub);
            y += Px(18);
        }
        foreach (string s in Lines(f))
        {
            int lh = TextRenderer.MeasureText(s, FS, new Size(c.Width - Px(28), 0), TextFormatFlags.WordBreak).Height;
            TxtW(g, s, FS, new Rectangle(c.X + Px(14), y, c.Width - Px(28), lh + Px(2)), f.Ok || f.Area == "sys" && f.Score == 0 ? P.Sub : P.Fg);
            y += lh + Px(2);
        }
        if (f.Open)
        {
            y += Px(2);
            using (Pen sp = new Pen(Util.Blend(P.Card, P.Fg, 0.12f))) g.DrawLine(sp, c.X + Px(14), y, c.Right - Px(14), y);
            y += Px(6);
            foreach (string s in Extra(f))
            {
                int lh = TextRenderer.MeasureText(s, FS, new Size(c.Width - Px(28), 0), TextFormatFlags.WordBreak).Height;
                TxtW(g, s, FS, new Rectangle(c.X + Px(14), y, c.Width - Px(28), lh + Px(2)), s.StartsWith("• ") ? P.Fg : P.Sub);
                y += lh + Px(2);
            }
        }
        List<Act> acts = Actions(f);
        if (acts.Count == 0) return;
        int rows = ActRows(acts, c.Width);
        int bx = c.X + Px(14), by = c.Bottom - Px(10) - rows * Px(38) + Px(6);
        foreach (Act a in acts)
        {
            int bw = ActW(a);
            if (bx + bw > c.Right - Px(10) && bx > c.X + Px(14)) { bx = c.X + Px(14); by += Px(38); }
            Rectangle br = new Rectangle(bx, by, bw, Px(30));
            bool inside = Rectangle.Intersect(br, clip) == br;
            Btn(g, br, a.Text, inside ? a.Do : null, a.Style, !fixing);
            bx += bw + Px(8);
        }
    }

    // ---------- виправлення ----------
    void RunFix(Finding f, Fix x)
    {
        if (fixing) return;
        string text = x.Confirm + "\n\n" + (x.Admin && !Elevated.IsAdmin ? L.T("Windows попросить дозвіл адміністратора. ") : "") +
                      L.T("Попередній стан буде збережено - скасувати можна на вкладці «Система» в розділі «Зроблені виправлення».");
        if (Ask(L.T("Безпека"), text, x.Button, L.T("Скасувати")) != 0) return;
        fixing = true;
        Say(L.T("Виправлення…"), false);
        string title = f.Title + " - " + char.ToLower(x.Button[0]) + x.Button.Substring(1);
        Thread t = new Thread(delegate ()
        {
            string err;
            try { err = SecFix.Apply(x, title); }
            catch (Exception ex) { err = ex.Message; }
            try
            {
                BeginInvoke(new Action(delegate
                {
                    fixing = false;
                    if (err.Length == 0)
                    {
                        f.Fixes.Clear();
                        f.Score = 0;
                        f.Ok = true;
                        f.Why.Insert(0, "✓ " + x.Done);
                        Say(x.Done, false);
                        SetTabs();
                        if (Bar != null) Bar.WidgetRedraw("security");
                    }
                    else Say(L.T("Не вдалося: ") + err, true);
                    Invalidate();
                }));
            }
            catch { }
        });
        t.IsBackground = true;
        t.Start();
        Invalidate();
    }

    void UndoFix(FixEntry e)
    {
        if (fixing) return;
        if (Ask(L.T("Безпека"), L.T("Скасувати виправлення «") + e.Title + L.Q("» (") + e.When + L.T(")?\n\nБуде повернуто стан, який був до нього.") +
                (e.Admin && !Elevated.IsAdmin ? L.T("\n\nWindows попросить дозвіл адміністратора.") : ""), L.T("Скасувати виправлення"), L.T("Ні")) != 0) return;
        fixing = true;
        Say(L.T("Скасування…"), false);
        Thread t = new Thread(delegate ()
        {
            string err;
            try { err = SecFix.Undo(e); }
            catch (Exception ex) { err = ex.Message; }
            try
            {
                BeginInvoke(new Action(delegate
                {
                    fixing = false;
                    if (err.Length == 0) Say(L.T("Скасовано. Щоб побачити актуальний стан, проскануйте ще раз."), false);
                    else Say(L.T("Не вдалося скасувати: ") + err, true);
                    Invalidate();
                }));
            }
            catch { }
        });
        t.IsBackground = true;
        t.Start();
        Invalidate();
    }

    static void Open(string target)
    {
        try { Process.Start(target); } catch (Exception ex) { Util.ErrorLog(ex); }
    }
}
