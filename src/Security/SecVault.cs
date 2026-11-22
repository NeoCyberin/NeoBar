using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

// ======================================================================
//  «Безпека» → «Шифрування» → «Сейф»: тека з файлами, які без пароля не прочитати (Vault.cs).
//  Сейф замикається сам: коли закривають вікно, коли екран вимкнено чи ПК заблоковано, і після 10 хв без дій.
//  Повільне (ключ з пароля, шифрування великих файлів) - у фоновому потоці.
// ======================================================================
sealed partial class SecWindow
{
    TextBox tbV1, tbV2, tbV3;               // пароль / повтор; при зміні пароля - поточний, новий, повтор
    string vLoc = "";                       // тека сейфа
    volatile bool vBusy, vStop;
    volatile float vProg;
    volatile string vWhat = "";
    string vMsg = "";
    bool vMsgBad, vChange, vDelOrig;
    int vScroll;
    readonly List<string> vPending = new List<string>();     // додати, щойно сейф відкриють
    System.Windows.Forms.Timer vTimer;
    public const int VaultIdleMin = 10;

    void VaultSetup()
    {
        tbV1 = MakeInput(); tbV2 = MakeInput(); tbV3 = MakeInput();
        foreach (TextBox t in new TextBox[] { tbV1, tbV2, tbV3 })
        {
            t.UseSystemPasswordChar = true;
            t.TextChanged += delegate { Invalidate(); };
            t.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; VaultEnter(); } };
        }
        vLoc = Vault.Location;
        Vault.Changed += OnVault;
        ScreenState.Changed += OnVaultAway;
        // автозамикання після бездіяльності; таймер працює, лише поки сейф відкритий
        vTimer = new System.Windows.Forms.Timer();
        vTimer.Interval = 30000;
        vTimer.Tick += delegate
        {
            if (!Vault.IsOpen) { vTimer.Stop(); return; }
            if (!vBusy && (DateTime.Now - Vault.LastUse).TotalMinutes >= VaultIdleMin) LockVault(L.F("Сейф замкнено: {0} хв без дій.", VaultIdleMin));
        };
        if (Vault.IsOpen) vTimer.Start();
        // відкриті минулого разу копії, які тоді були зайняті, - прибрати
        if (!Vault.IsOpen) ThreadPool.QueueUserWorkItem(delegate { Vault.CleanTemp(); });
    }

    void VaultClosed()
    {
        vStop = true;
        Vault.Changed -= OnVault;
        ScreenState.Changed -= OnVaultAway;
        if (vTimer != null) { vTimer.Stop(); vTimer.Dispose(); }
        // закрили вікно - сейф замикається (ключі стираються, відкриті копії прибираються)
        if (Vault.IsOpen) Vault.Lock();
    }

    void OnVault() { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(Invalidate)); } catch (Exception skipEx) { SbLog.Skip(skipEx); } }

    void OnVaultAway() { if (ScreenState.Away && Vault.IsOpen && !vBusy) LockVault(L.T("Сейф замкнено: екран вимкнено чи комп'ютер заблоковано.")); }

    void LockVault(string why)
    {
        Vault.Lock();
        vTimer.Stop();
        vChange = false;
        foreach (TextBox t in new TextBox[] { tbV1, tbV2, tbV3 }) t.Text = "";
        int left = Vault.CleanTemp();
        vMsg = why + (left > 0 ? " " + L.F("Відкритих копій, ще зайнятих програмами: {0} - їх приберу пізніше; закрийте ці програми.", left) : "");
        vMsgBad = false;
        Invalidate();
    }

    // файли з контекстного меню чи перетягнуті у вікно
    public void AddToVault(string[] paths)
    {
        CrShow(3);
        if (!Vault.IsOpen)
        {
            foreach (string p in paths) if (!vPending.Contains(p)) vPending.Add(p);
            vMsg = L.F("Файлів чекає на додавання: {0}. Відкрийте сейф паролем.", vPending.Count);
            vMsgBad = false;
            Invalidate();
            return;
        }
        StartAdd(paths);
    }

    void VaultEnter()
    {
        if (vBusy) return;
        if (Vault.IsOpen) { if (vChange) DoChangePw(); return; }
        if (Vault.Exists(vLoc)) DoOpen(); else DoCreate();
    }

    // ---------- дії ----------

    void Work(string what, ThreadStart job)
    {
        vBusy = true; vStop = false; vProg = 0; vWhat = what; vMsg = "";
        Busy();
        Invalidate();
        Thread th = new Thread(delegate ()
        {
            try { job(); }
            catch (OperationCanceledException) { Post(L.T("Зупинено."), false); }
            catch (Exception ex) { Post(ex.Message, true); }
            finally
            {
                vBusy = false;
                try { BeginInvoke(new Action(Invalidate)); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            }
        });
        th.IsBackground = true;
        th.Start();
    }

    void Post(string msg, bool bad)
    {
        try { BeginInvoke(new Action(delegate { vMsg = msg; vMsgBad = bad; Invalidate(); })); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    void DoCreate()
    {
        string p1 = tbV1.Text, p2 = tbV2.Text, d = vLoc;
        if (p1.Length == 0 || p1 != p2) return;
        Work(L.T("створюю сейф…"), delegate
        {
            Vault.Create(d, p1);
            BeginInvoke(new Action(delegate { tbV1.Text = tbV2.Text = ""; Opened(); }));
            Post(L.T("Сейф створено. Перетягніть сюди файли або натисніть «Додати файли…»."), false);
        });
    }

    void DoOpen()
    {
        string pw = tbV1.Text, d = vLoc;
        if (pw.Length == 0) return;
        Work(L.T("відкриваю…"), delegate
        {
            if (!Vault.Open(d, pw)) { Post(L.T("Неправильний пароль."), true); return; }
            BeginInvoke(new Action(delegate { tbV1.Text = ""; Opened(); }));
        });
    }

    void Opened()
    {
        vTimer.Start();
        HideInputs();
        if (vPending.Count > 0)
        {
            string[] l = vPending.ToArray();
            vPending.Clear();
            StartAdd(l);
        }
        Invalidate();
    }

    void DoChangePw()
    {
        string cur = tbV1.Text, nw = tbV2.Text;
        if (cur.Length == 0 || nw.Length == 0 || nw != tbV3.Text) return;
        Work(L.T("змінюю пароль…"), delegate
        {
            Vault.ChangePassword(cur, nw);
            BeginInvoke(new Action(delegate { vChange = false; tbV1.Text = tbV2.Text = tbV3.Text = ""; HideInputs(); }));
            Post(L.T("Пароль змінено. Файли не перешифровувались - змінився лише ключ до них."), false);
        });
    }

    // розгорнути теки: (повний шлях, ім'я в сейфі з підтеками)
    static List<KeyValuePair<string, string>> Expand(string[] paths)
    {
        List<KeyValuePair<string, string>> l = new List<KeyValuePair<string, string>>();
        foreach (string p in paths)
        {
            try
            {
                if (File.Exists(p)) l.Add(new KeyValuePair<string, string>(p, Path.GetFileName(p)));
                else if (Directory.Exists(p))
                {
                    string root = Path.GetDirectoryName(p.TrimEnd('\\')) ?? "";
                    foreach (string f in Directory.GetFiles(p, "*", SearchOption.AllDirectories))
                        l.Add(new KeyValuePair<string, string>(f, f.Substring(root.Length).TrimStart('\\')));
                }
            }
            catch (Exception ex) { SbLog.Skip(ex); }
        }
        return l;
    }

    void StartAdd(string[] paths)
    {
        if (vBusy) { foreach (string p in paths) if (!vPending.Contains(p)) vPending.Add(p); return; }
        // видаляти оригінали - лише після явного «так» (назовсім, не в кошик)
        int choice = 1;
        if (vDelOrig)
            choice = Ask(L.T("Сейф"), L.T("Після додавання оригінали буде видалено назовсім (не в кошик), але лише ті, чию копію в сейфі перевірено.\n\nНа SSD залишки видаленого файла інколи можна відновити спеціальними програмами.") + "\n\n" + L.T("Продовжити?"),
                L.T("Додати й видалити"), L.T("Лише додати"), L.T("Скасувати"));
        if (choice < 0 || choice == 2) return;
        bool del = choice == 0;
        Work(L.T("додаю…"), delegate
        {
            List<KeyValuePair<string, string>> l = Expand(paths);
            long total = 0, done = 0;
            foreach (KeyValuePair<string, string> kv in l) try { total += new FileInfo(kv.Key).Length; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            int ok = 0, removed = 0;
            List<string> errors = new List<string>();
            foreach (KeyValuePair<string, string> kv in l)
            {
                if (vStop) throw new OperationCanceledException();
                vWhat = Path.GetFileName(kv.Key);
                long size = 0;
                try { size = new FileInfo(kv.Key).Length; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                long d0 = done;
                try
                {
                    VaultItem it = Vault.AddFile(kv.Key, kv.Value, delegate (float f) { vProg = total > 0 ? (float)((d0 + f * size) / total) : f; }, delegate { return vStop; });
                    ok++;
                    if (del && Vault.Verify(it)) { try { File.SetAttributes(kv.Key, FileAttributes.Normal); File.Delete(kv.Key); removed++; } catch (Exception ex) { errors.Add(Path.GetFileName(kv.Key) + ": " + ex.Message); } }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add(Path.GetFileName(kv.Key) + ": " + ex.Message); }
                done += size;
            }
            // порожні теки, з яких усе перенесено
            if (del) foreach (string p in paths) if (Directory.Exists(p)) try { if (Directory.GetFiles(p, "*", SearchOption.AllDirectories).Length == 0) Directory.Delete(p, true); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            string msg = L.F("Додано файлів: {0}.", ok) + (del ? " " + L.F("Оригіналів видалено: {0}.", removed) : "");
            if (errors.Count > 0) msg += " " + L.F("Не вдалося: {0}", string.Join("; ", errors.Count > 3 ? errors.GetRange(0, 3).ToArray() : errors.ToArray()));
            Post(msg, errors.Count > 0);
        });
    }

    void OpenItem(VaultItem it)
    {
        Work(L.T("розшифровую…"), delegate
        {
            string f = Vault.OpenCopy(it, delegate (float p) { vProg = p; }, delegate { return vStop; });
            BeginInvoke(new Action(delegate
            {
                try { using (Process.Start(new ProcessStartInfo(f) { UseShellExecute = true })) { } }
                catch (Exception ex) { vMsg = ex.Message; vMsgBad = true; Invalidate(); }
            }));
            Post(L.T("Відкрито тимчасову копію - її буде стерто, коли сейф замкнеться. Зміни в ній у сейф не потрапляють: щоб зберегти, додайте файл знову."), false);
        });
    }

    void SaveItems(List<VaultItem> items)
    {
        string to;
        using (FolderBrowserDialog d = new FolderBrowserDialog())
        {
            d.Description = L.T("Куди зберегти розшифровані файли");
            if (d.ShowDialog(this) != DialogResult.OK) return;
            to = d.SelectedPath;
        }
        Work(L.T("розшифровую…"), delegate
        {
            int n = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (vStop) throw new OperationCanceledException();
                vWhat = items[i].Name;
                int k = i;
                Vault.Extract(items[i], to, delegate (float p) { vProg = (k + p) / items.Count; }, delegate { return vStop; });
                n++;
            }
            Post(L.F("Збережено файлів: {0} - у «{1}».", n, to), false);
        });
    }

    void PickVaultFiles(bool folder)
    {
        if (folder)
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = L.T("Тека, яку додати в сейф");
                if (d.ShowDialog(this) == DialogResult.OK) StartAdd(new string[] { d.SelectedPath });
            }
            return;
        }
        using (OpenFileDialog d = new OpenFileDialog())
        {
            d.Title = L.T("Файли, які додати в сейф");
            d.Filter = L.T("Усі файли|*.*");
            d.Multiselect = true;
            if (d.ShowDialog(this) == DialogResult.OK) StartAdd(d.FileNames);
        }
    }

    void PickVaultDir()
    {
        using (FolderBrowserDialog d = new FolderBrowserDialog())
        {
            d.Description = L.T("Тека сейфа: наявний сейф відкриється, у порожній теці буде створено новий");
            try { if (Directory.Exists(vLoc)) d.SelectedPath = vLoc; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            if (d.ShowDialog(this) != DialogResult.OK) return;
            vLoc = d.SelectedPath;
            if (Vault.Exists(vLoc)) Vault.Location = vLoc;
            vMsg = "";
            Invalidate();
        }
    }

    // ---------- малювання ----------

    void PaintVault(Graphics g, Rectangle r)
    {
        foreach (TextBox t in new TextBox[] { tbCmp, tbIn, tbOut, tbPw, tbFpw, tbFpw2 }) if (t.Visible) t.Visible = false;
        int y = r.Y;
        if (!Vault.IsOpen) { PaintVaultLocked(g, r); return; }
        Vault.LastUse = Over(r) ? DateTime.Now : Vault.LastUse;      // вікно в роботі - не замикати
        if (vChange) { PaintVaultPw(g, r); return; }
        foreach (TextBox t in new TextBox[] { tbV1, tbV2, tbV3 }) if (t.Visible) t.Visible = false;

        // верхній ряд
        int bw = (r.Width - Px(16)) / 3;
        Btn(g, new Rectangle(r.X, y, bw, Px(32)), L.T("Додати файли…"), delegate { PickVaultFiles(false); }, 1, !vBusy);
        Btn(g, new Rectangle(r.X + bw + Px(8), y, bw, Px(32)), L.T("Додати теку…"), delegate { PickVaultFiles(true); }, 0, !vBusy);
        Btn(g, new Rectangle(r.X + (bw + Px(8)) * 2, y, bw, Px(32)), L.T("Замкнути"), delegate { LockVault(L.T("Сейф замкнено.")); }, 0, !vBusy);
        y += Px(40);
        List<VaultItem> items = Vault.Snapshot();
        items.Sort(delegate (VaultItem a, VaultItem b) { return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); });
        TxtL(g, L.F("Файлів: {0} · {1}", items.Count, SizeText(Vault.TotalSize)) + " · " + vLoc, FS, new Rectangle(r.X, y, r.Width, Px(20)), P.Sub);
        y += Px(24);

        int bottomBlock = Px(28) + Px(6) + Px(34) + Px(8) + Px(26);
        Rectangle list = new Rectangle(r.X, y, r.Width, Math.Max(Px(80), r.Bottom - y - bottomBlock));
        int rowH = Px(50);
        if (items.Count == 0) DropZone(g, list, L.T("Перетягніть сюди файли чи теки"), L.T("Додати файли…"), delegate { PickVaultFiles(false); }, !vBusy);
        else Card(g, list, Util.Blend(P.Bg, P.Fg, 0.035f));
        int total = items.Count * rowH, maxS = Math.Max(0, total - list.Height + Px(8));
        vScroll = Math.Max(0, Math.Min(maxS, vScroll));
        GraphicsState st = g.Save();
        g.SetClip(list);
        for (int i = 0; i < items.Count; i++)
        {
            VaultItem it = items[i];
            Rectangle rr = new Rectangle(list.X + Px(4), list.Y + Px(4) + i * rowH - vScroll, list.Width - Px(16), rowH - Px(4));
            if (rr.Bottom < list.Y || rr.Top > list.Bottom) continue;
            bool full = Rectangle.Intersect(rr, list) == rr;
            Card(g, rr, Over(rr) ? Util.Blend(P.Card, P.Fg, 0.05f) : P.Card);
            int bx = rr.Right - Px(34);
            int tw = rr.Width - Px(250);
            string nm = it.Name;
            int slash = nm.LastIndexOf('\\');
            TxtL(g, slash >= 0 ? nm.Substring(slash + 1) : nm, F, new Rectangle(rr.X + Px(10), rr.Y + Px(4), tw, Px(22)), P.Fg);
            TxtL(g, SizeText(it.Size) + " · " + it.Added.ToString("d") + (slash >= 0 ? " · " + nm.Substring(0, slash) : ""), FS, new Rectangle(rr.X + Px(10), rr.Y + Px(25), tw, Px(18)), P.Sub);
            if (!full || vBusy) continue;
            VaultItem itc = it;
            IconBtn(g, new Rectangle(bx, rr.Y + (rr.Height - Px(28)) / 2, Px(28), Px(28)), "trash", delegate
            {
                if (Ask(L.T("Сейф"), L.F("Видалити «{0}» із сейфа назовсім?", itc.Name), L.T("Видалити"), L.T("Скасувати")) == 0) { Vault.Remove(itc); Invalidate(); }
            }, false);
            Btn(g, new Rectangle(bx - Px(108), rr.Y + (rr.Height - Px(28)) / 2, Px(100), Px(28)), L.T("Зберегти…"), delegate { SaveItems(new List<VaultItem> { itc }); }, 0, true);
            Btn(g, new Rectangle(bx - Px(208), rr.Y + (rr.Height - Px(28)) / 2, Px(94), Px(28)), L.T("Відкрити"), delegate { OpenItem(itc); }, 0, true);
        }
        g.Restore(st);
        Wheel(list, delegate (int d) { vScroll -= d > 0 ? rowH : -rowH; Invalidate(); });
        VBar(g, new Rectangle(list.Right - Px(7), list.Y + Px(4), Px(4), list.Height - Px(8)), total + Px(8), list.Height, vScroll, delegate (int v) { vScroll = v; Invalidate(); });
        y = list.Bottom + Px(8);

        if (vBusy)
        {
            TxtL(g, vWhat + " · " + (int)(vProg * 100) + "%", FS, new Rectangle(r.X, y, r.Width - Px(130), Px(20)), P.Sub);
            ProgressBar(g, new Rectangle(r.X, y + Px(22), r.Width - Px(130), Px(6)), vProg, P.Acc);
            Btn(g, new Rectangle(r.Right - Px(120), y, Px(120), Px(30)), L.T("Зупинити"), delegate { vStop = true; }, 0, true);
            return;
        }
        Switch(g, new Rectangle(r.X, y, r.Width, Px(28)), L.T("Видаляти оригінали після додавання (лише перевірені копії)"), vDelOrig, delegate { vDelOrig = !vDelOrig; Invalidate(); });
        y += Px(34);
        int hw = (r.Width - Px(8)) / 2;
        Btn(g, new Rectangle(r.X, y, hw, Px(32)), L.T("Змінити пароль…"), delegate { vChange = true; vMsg = ""; Invalidate(); }, 0, true);
        Btn(g, new Rectangle(r.X + hw + Px(8), y, hw, Px(32)), L.T("Зберегти все…"), delegate { SaveItems(Vault.Snapshot()); }, 0, items.Count > 0);
        y += Px(40);
        string hint = vMsg.Length > 0 ? vMsg : L.F("Сейф замкнеться сам: коли закриєте вікно, вимкнеться екран чи після {0} хв без дій.", VaultIdleMin);
        TxtL(g, hint, FS, new Rectangle(r.X, y, r.Width, Px(22)), vMsg.Length > 0 ? (vMsgBad ? P.Bad : P.Good) : P.Sub);
    }

    void PaintVaultLocked(Graphics g, Rectangle r)
    {
        int y = r.Y;
        bool exists = Vault.Exists(vLoc);
        tbV3.Visible = false;
        TxtW(g, exists
            ? L.T("Сейф замкнено. Введіть пароль, щоб побачити й відкрити файли.")
            : L.T("Сейф - тека, де файли лежать лише зашифрованими (AES-256): без пароля їх не прочитати, навіть назви. Перетягніть файли сюди чи виберіть «Додати в сейф» у контекстному меню файла."),
            F, new Rectangle(r.X, y, r.Width, Px(60)), P.Fg);
        y += Px(66);
        TxtL(g, L.T("Тека сейфа:"), FS, new Rectangle(r.X, y, Px(110), Px(30)), P.Sub);
        TxtL(g, vLoc, FS, new Rectangle(r.X + Px(110), y, r.Width - Px(240), Px(30)), P.Fg);
        Btn(g, new Rectangle(r.Right - Px(122), y, Px(122), Px(30)), exists ? L.T("Інший…") : L.T("Змінити…"), PickVaultDir, 0, !vBusy);
        y += Px(42);
        string p1 = tbV1.Text;
        TxtL(g, L.T("Пароль:"), F, new Rectangle(r.X, y, Px(110), Px(34)), P.Fg);
        PlaceInput(g, tbV1, new Rectangle(r.X + Px(110), y, r.Width - Px(110), Px(34)));
        y += Px(42);
        if (!exists)
        {
            TxtL(g, L.T("Ще раз:"), F, new Rectangle(r.X, y, Px(110), Px(34)), P.Fg);
            PlaceInput(g, tbV2, new Rectangle(r.X + Px(110), y, r.Width - Px(110), Px(34)));
            y += Px(42);
        }
        else tbV2.Visible = false;
        string hint; Color hc;
        if (vMsg.Length > 0) { hint = vMsg; hc = vMsgBad ? P.Bad : P.Good; }
        else if (vPending.Count > 0) { hint = L.F("Файлів чекає на додавання: {0}. Відкрийте сейф паролем.", vPending.Count); hc = P.Warn; }
        else if (exists) { hint = ""; hc = P.Sub; }
        else if (p1.Length == 0) { hint = L.T("Пароль неможливо відновити: забудете - файли в сейфі втрачено назавжди."); hc = P.Warn; }
        else if (tbV2.Text.Length > 0 && tbV2.Text != p1) { hint = L.T("Паролі не збігаються."); hc = P.Bad; }
        else { int s = Strength(p1); hint = s == 2 ? L.T("Надійний пароль.") : (s == 1 ? L.T("Середній пароль - підійде.") : L.T("Слабкий пароль: краще щонайменше 8 символів.")); hc = s == 2 ? P.Good : (s == 1 ? P.Warn : P.Bad); }
        TxtW(g, hint, FS, new Rectangle(r.X, y, r.Width, Px(40)), hc);
        y += Px(46);
        if (vBusy)
        {
            TxtL(g, vWhat, FS, new Rectangle(r.X, y, r.Width, Px(20)), P.Sub);
            ProgressBar(g, new Rectangle(r.X, y + Px(24), r.Width, Px(6)), (float)((Environment.TickCount % 1500) / 1500.0), P.Acc);
            return;
        }
        if (exists) Btn(g, new Rectangle(r.X, y, r.Width, Px(38)), L.T("Відкрити сейф"), DoOpen, 1, p1.Length > 0);
        else Btn(g, new Rectangle(r.X, y, r.Width, Px(38)), L.T("Створити сейф"), DoCreate, 1, p1.Length > 0 && tbV2.Text == p1);
    }

    void PaintVaultPw(Graphics g, Rectangle r)
    {
        int y = r.Y;
        TxtW(g, L.T("Новий пароль замінить старий; файли перешифровувати не треба. Забутий пароль не відновити."), F, new Rectangle(r.X, y, r.Width, Px(44)), P.Fg);
        y += Px(52);
        string[] labels = { L.T("Поточний:"), L.T("Новий:"), L.T("Ще раз:") };
        TextBox[] tbs = { tbV1, tbV2, tbV3 };
        for (int i = 0; i < 3; i++)
        {
            TxtL(g, labels[i], F, new Rectangle(r.X, y, Px(110), Px(34)), P.Fg);
            PlaceInput(g, tbs[i], new Rectangle(r.X + Px(110), y, r.Width - Px(110), Px(34)));
            y += Px(42);
        }
        string hint = vMsg; Color hc = vMsgBad ? P.Bad : P.Good;
        if (hint.Length == 0 && tbV3.Text.Length > 0 && tbV3.Text != tbV2.Text) { hint = L.T("Паролі не збігаються."); hc = P.Bad; }
        TxtW(g, hint, FS, new Rectangle(r.X, y, r.Width, Px(40)), hc);
        y += Px(46);
        if (vBusy) { TxtL(g, vWhat, FS, new Rectangle(r.X, y, r.Width, Px(20)), P.Sub); return; }
        int hw = (r.Width - Px(8)) / 2;
        Btn(g, new Rectangle(r.X, y, hw, Px(38)), L.T("Змінити пароль"), DoChangePw, 1, tbV1.Text.Length > 0 && tbV2.Text.Length > 0 && tbV2.Text == tbV3.Text);
        Btn(g, new Rectangle(r.X + hw + Px(8), y, hw, Px(38)), L.T("Скасувати"), delegate { vChange = false; vMsg = ""; tbV1.Text = tbV2.Text = tbV3.Text = ""; HideInputs(); Invalidate(); }, 0, true);
    }
}
