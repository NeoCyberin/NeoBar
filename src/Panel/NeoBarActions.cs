using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

// ======================================================================
//  NeoBar - дії: запуск, меню, групи, налаштування, пошук, перетягування
// ======================================================================
sealed partial class NeoBar
{
    // ---------- запуск ----------

    void Launch(Entry e)
    {
        Launch(e, false);
    }

    // Запуск - в окремому потоці: ShellExecute часто звертається до Провідника, і якщо той зайнятий,
    // панель не повинна зависати разом з ним
    void Launch(Entry e, bool admin)
    {
        string target = e.Full;
        if (!PreLaunchCheck(target)) return;
        SbLog.W(SbLog.Panel, "запуск: " + target + (admin ? " (від адміністратора)" : ""));
        RunShell(delegate
        {
            string file, args;
            Util.SplitCommand(target, out file, out args);
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file);
                if (args.Length > 0) psi.Arguments = args;
                psi.UseShellExecute = true;
                if (admin) psi.Verb = "runas";
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if ((ext == ".exe" || ext == ".bat" || ext == ".cmd") && File.Exists(file))
                    psi.WorkingDirectory = Path.GetDirectoryName(file);
                using (Process.Start(psi)) { }
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode != 1223) LaunchError(target, ex.Message); // 1223 = користувач скасував UAC
            }
            catch (Exception ex) { LaunchError(target, ex.Message); }
        });
    }

    // раніше тут було попередження про античіт; тепер без «Інтеграції в гру» NeoBar нічого в гру не впроваджує, тож запуск не блокується
    bool PreLaunchCheck(string target) { return true; }

    void LaunchError(string target, string msg)
    {
        SbLog.W(SbLog.Panel, "не вдалося запустити " + target + ": " + msg);
        try
        {
            BeginInvoke(new Action(delegate
            {
                MessageBox.Show(L.T("Не вдалося запустити:\n") + target + "\n\n" + msg, "NeoBar", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }));
        }
        catch { }
    }

    static void RunShell(System.Threading.ThreadStart a)
    {
        System.Threading.Thread t = new System.Threading.Thread(delegate () { try { a(); } catch { } });
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
    }

    static void Reveal(string path, bool dir)
    {
        RunShell(delegate
        {
            string args = dir ? "\"" + path + "\"" : "/select,\"" + path + "\"";
            using (Process.Start("explorer.exe", args)) { }
        });
    }

    // доступ для спливних вікон
    public void LaunchEntry(Entry e, bool admin) { Launch(e, admin); }
    public void ShowMenuFor(Entry e, Control host, Point at) { ShowItemMenu(e, host, at); }

    // ---------- перетягування файлів на панель ----------

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text);
        e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
        dragOver = true;
        if (autohide) SetExpanded(true);
    }

    // над панеллю показуємо, куди потрапить елемент: лінія між елементами або рамка навколо групи
    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text);
        e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (!ok || dragging) return;
        Entry grp;
        int s = ExtDropSlot(PointToClient(new Point(e.X, e.Y)), out grp);
        if (s != dropSlot || grp != dropGroup) { dropSlot = s; dropGroup = grp; Invalidate(); }
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        dragOver = false;
        if (!dragging) { dropSlot = -1; dropGroup = null; Invalidate(); }
    }

    // Під час перетягування з Провідника той чекає, поки панель прийме файли, тому тут лише забираємо дані,
    // а запис і перезавантаження панелі - вже після того, як Провідник відпустить перетягування
    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        dragOver = false;
        Entry dg;
        int slot = ExtDropSlot(PointToClient(new Point(e.X, e.Y)), out dg);
        dropSlot = -1; dropGroup = null;
        Invalidate();
        string[] files = null;
        string text = null;
        try { files = e.Data.GetData(DataFormats.FileDrop) as string[]; } catch { }
        if (files == null) try { text = e.Data.GetData(DataFormats.Text) as string; } catch { }
        BeginInvoke(new Action(delegate { AddDropped(files, text, slot); }));
    }

    // слот під курсором у момент скидання (для шляху WM_DROPFILES, де немає подій перетягування)
    int CursorDropSlot()
    {
        try { Entry g; return ExtDropSlot(PointToClient(Cursor.Position), out g); } catch { return -1; }
    }

    // slot - індекс у списку елементів, куди вставити (у групу/між елементами); -1 - у кінець
    void AddDropped(string[] files, string t, int slot)
    {
        StringBuilder sb = new StringBuilder();
        if (files != null)
        {
            foreach (string f in files)
            {
                string n = Directory.Exists(f)
                    ? Path.GetFileName(f.TrimEnd('\\'))
                    : Path.GetFileNameWithoutExtension(f);
                if (string.IsNullOrEmpty(n)) n = f;
                sb.AppendLine(n.Replace('|', ' ') + " | " + Util.Collapse(f));
            }
        }
        else
        {
            Uri u;
            if (t != null && Uri.TryCreate(t.Trim(), UriKind.Absolute, out u) &&
                (u.Scheme == "http" || u.Scheme == "https"))
            {
                string host = u.Host.StartsWith("www.", StringComparison.Ordinal) ? u.Host.Substring(4) : u.Host;
                sb.AppendLine(host + " | " + t.Trim());
            }
        }
        if (sb.Length == 0) return;
        try
        {
            int before = entries.Count;
            File.AppendAllText(cfgPath, Environment.NewLine + sb.ToString(), new UTF8Encoding(true));
            Reload();
            // нові елементи зчитано в кінець - переносимо туди, куди їх кинули (у потрібну групу й місце)
            int added = entries.Count - before;
            if (slot >= 0 && slot < before && added > 0)
            {
                List<Entry> ne = entries.GetRange(before, added);
                bool allItems = true;
                foreach (Entry x in ne) if (x.Kind != Kind.Item) allItems = false;
                if (allItems)
                {
                    entries.RemoveRange(before, added);
                    entries.InsertRange(Math.Min(slot, entries.Count), ne);
                    hover = -1;
                    DoLayout();
                    BuildFolderIcons();
                    RebuildDiskIds();
                    SaveCfg();
                    Invalidate();
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(L.T("Не вдалося записати neobar.ini:\n") + ex.Message, "NeoBar",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------- меню елемента (ПКМ) ----------

    void ShowItemMenu(Entry e, Control host, Point at)
    {
        tip.Hide(this);
        if (e.Kind == Kind.Widget) return;   // у віджетів немає контекстного меню: усе - у налаштуваннях, вкладка «Віджети»
        int i = entries.IndexOf(e);
        bool locked = settings != null && !settings.IsDisposed; // поки відкрите вікно налаштувань, порядок не міняємо
        ContextMenuStrip m = new ContextMenuStrip();
        Color mbg = PopStyle.Bg(T), mtx = PopStyle.Text(T);
        m.ForeColor = mtx;
        RoundMenu.Apply(m, mbg, mtx, Util.Opaque(T.Accent), Util.Blend(mbg, mtx, 0.22f), Px(10));

        m.Items.Add(e.Kind == Kind.Header ? L.T("Змінити групу…") : L.T("Змінити…"), null, delegate { OpenSettings(e, 0); });

        if (e.Kind == Kind.Item)
        {
            string tg = e.Full;
            bool isFile = File.Exists(tg), isDir = Directory.Exists(tg);
            if (isFile)
            {
                string ext = Path.GetExtension(tg).ToLowerInvariant();
                if (ext == ".exe" || ext == ".bat" || ext == ".cmd")
                    m.Items.Add(L.T("Запустити від імені адміністратора"), null, delegate { Launch(e, true); });
            }
            if (isFile || isDir)
                m.Items.Add(isDir ? L.T("Відкрити в Провіднику") : L.T("Показати у Провіднику"), null, delegate { Reveal(tg, isDir); });
        }
        else if (e.Kind == Kind.Header)
        {
            if (e.Fold) m.Items.Add(L.T("Відкрити групу"), null, delegate { ShowFlyout(e); });
            else m.Items.Add(e.Collapsed ? L.T("Розгорнути групу") : L.T("Згорнути групу"), null, delegate { ToggleGroup(e); });
        }

        m.Items.Add(new ToolStripSeparator());
        ToolStripItem up = m.Items.Add(L.T("Перемістити вгору"), null, delegate { MoveEntry(entries.IndexOf(e), -1); });
        ToolStripItem dn = m.Items.Add(L.T("Перемістити вниз"), null, delegate { MoveEntry(entries.IndexOf(e), 1); });
        up.Enabled = !locked && i > 0;
        dn.Enabled = !locked && i >= 0 && i < entries.Count - 1;
        m.Items.Add(new ToolStripSeparator());
        string delText = e.Kind == Kind.Header ? L.T("Видалити групу (елементи залишаться)") : L.T("Видалити");
        ToolStripItem del = m.Items.Add(delText, null, delegate { DeleteEntry(entries.IndexOf(e)); });
        del.Enabled = !locked;

        menuOpen++;
        m.Closed += delegate
        {
            menuOpen--;
            BeginInvoke(new Action(m.Dispose));
        };
        m.Show(host, at);
        MenuGuard.Watch(m);
    }

    // анімація розкриття/згортання групи: 0 - згорнута, 1 - розкрита
    readonly Dictionary<Entry, float> grpT = new Dictionary<Entry, float>();
    System.Windows.Forms.Timer grpTimer;

    float GroupOpen(Entry h)
    {
        float t;
        if (grpT.TryGetValue(h, out t)) return t;
        return h.Collapsed ? 0f : 1f;
    }

    // компактна панель має довжину за вмістом: коли група розгорнулась/згорнулась, вікно одразу підлаштовується
    void FitCompact()
    {
        if (!IsHandleCreated || !Rounded) return;
        PlaceWindow();
        UpdateShape();
    }

    bool GroupAnimating(Entry h) { return h != null && grpT.ContainsKey(h); }

    void ToggleGroup(Entry e)
    {
        float from = GroupOpen(e);
        e.Collapsed = !e.Collapsed;
        try { Store.Save(cfgPath, cfg, entries); } catch { }   // стан згортання запам'ятовується
        if (cfg.Anim)
        {
            grpT[e] = from;
            if (grpTimer == null)
            {
                grpTimer = new System.Windows.Forms.Timer();
                grpTimer.Interval = 15;
                grpTimer.Tick += delegate
                {
                    foreach (Entry h in new List<Entry>(grpT.Keys))
                    {
                        float tg = h.Collapsed ? 0f : 1f, v = grpT[h];
                        v += Math.Sign(tg - v) * 0.09f;
                        if (Math.Abs(tg - v) < 0.09f) grpT.Remove(h); else grpT[h] = v;
                    }
                    DoLayout();
                    FitCompact();
                    ClampScroll();
                    Invalidate();
                    if (grpT.Count == 0) { grpTimer.Stop(); BuildFolderIcons(); if (IsHandleCreated) Trim(); }
                };
            }
            grpTimer.Start();
        }
        DoLayout();
        FitCompact();
        BuildFolderIcons();
        ClampScroll();
        hover = -1;
        Invalidate();
        UpdateHover(PointToClient(Cursor.Position));
    }

    void SaveEntries()
    {
        try
        {
            Store.Save(cfgPath, cfg, entries);
        }
        catch (Exception ex)
        {
            MessageBox.Show(L.T("Не вдалося зберегти neobar.ini:\n") + ex.Message, "NeoBar",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Reload();
    }

    void MoveEntry(int i, int dir)
    {
        int j = i + dir;
        if (i < 0 || j < 0 || j >= entries.Count) return;
        Entry t = entries[i];
        entries[i] = entries[j];
        entries[j] = t;
        SaveEntries();
    }

    void DeleteEntry(int i)
    {
        if (i < 0 || i >= entries.Count) return;
        Entry e = entries[i];
        string what = e.Kind == Kind.Header ? L.T("групу «") + e.Name + L.Q("»") : (e.Kind == Kind.Widget ? L.T("віджет «") + e.Name + L.Q("»") : L.Q("«") + e.Name + L.Q("»"));
        menuOpen++;
        DialogResult r = MessageBox.Show(L.T("Видалити ") + what + L.T(" з панелі?"), "NeoBar",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        menuOpen--;
        if (r != DialogResult.Yes) return;
        entries.RemoveAt(i);
        SaveEntries();
    }

    // ---------- закріпити / автосховок ----------

    public void TogglePinMode() { TogglePin(); }
    public void OpenSettingsTab(int tab) { OpenSettings(null, tab); }

    // вікно профілю біля аватара; повторний клік закриває
    ProfileWindow profWin;
    DateTime profWinClosedAt = DateTime.MinValue;

    void ShowProfileWindow()
    {
        if (profWin != null && !profWin.IsDisposed)
        {
            bool justLeft = (DateTime.Now - profWin.LeftAt).TotalMilliseconds < 450;
            profWin.Close();
            if (justLeft || Form.ActiveForm == profWin) return;
        }
        // вікно щойно закрилось саме через цей клік по аватару (втратило фокус) - не відкривати знову
        if ((DateTime.Now - profWinClosedAt).TotalMilliseconds < 450) return;
        profWin = new ProfileWindow(this);
        profWin.FormClosed += delegate { profWin = null; profWinClosedAt = DateTime.Now; };
        profWin.ShowNear(RectangleToScreen(TbRect(0)), cfg.Side);
    }

    // змінити профіль: запам'ятований профіль скидається, програма перезапускається у вікно вибору
    public void SwitchProfile()
    {
        try { Store.Save(cfgPath, cfg, entries); } catch { }
        Prof.Last = "";
        Prof.Save();
        SbLog.W(SbLog.Panel, "зміна профілю: перезапуск у вікно вибору");
        if (Prof.Restart()) Close();
    }

    // видалити поточний профіль: підтвердження (і PIN), позначка до видалення, перезапуск у вікно вибору
    public void DeleteProfile()
    {
        Profile p = Prof.Cur;
        if (p == null) return;
        if (Prof.All.Count <= 1)
        {
            SbDialog.Show(this, null, L.T("Видалити профіль"), L.Q("«") + p.Name + L.T("» - єдиний профіль, тож видалити його не можна. Спершу створіть інший: «Змінити профіль» → «Новий профіль»."), L.T("Гаразд"));
            return;
        }
        bool ok;
        using (ProfileDelete d = new ProfileDelete(this, p))
        {
            d.ShowDialog();
            ok = d.Ok;
        }
        if (!ok || !Prof.MarkDrop(p)) return;
        SbLog.W(SbLog.Panel, "видалення профілю «" + p.Name + "»: перезапуск у вікно вибору");
        if (Prof.Restart()) Close();
    }

    // назву чи колір профілю змінили
    public void ProfileChanged()
    {
        Invalidate(TbHot(0));
    }

    void TogglePin()
    {
        cfg.Mode = cfg.Mode == "fixed" ? "autohide" : "fixed";
        SbLog.W(SbLog.Panel, "режим панелі: " + cfg.Mode);
        try { Store.Save(cfgPath, cfg, entries); } catch { }
        Reload();
    }

    // ---------- вікна: налаштування, пошук, папка-група ----------

    public bool SettingsOpen { get { return settings != null && !settings.IsDisposed; } }

    // pick != null: відкрити саме цей елемент; tab: номер вкладки, -1 - остання відкрита (першого разу «Елементи»)
    void OpenSettings(Entry pick, int tab)
    {
        if (settings != null && !settings.IsDisposed)
        {
            settings.ShowEntry(pick, tab);
            return;
        }
        settings = new SettingsForm(this, pick, tab);
        settings.FormClosed += delegate { settings = null; Trim(); };
        settings.Show();
    }

    public void OpenSearch()
    {
        if (suspended) return;
        if (search != null && !search.IsDisposed)
        {
            search.Activate();
            return;
        }
        Rectangle wa = Mon().WorkingArea;
        search = new SearchForm(this, wa);
        search.FormClosed += delegate { search = null; };
        search.ShowCentered(wa);
    }

    // додати знайдений файл чи папку на панель (з вікна пошуку)
    public void AddToPanel(string name, string target) { AddToPanel(name, target, ""); }

    public void AddToPanel(string name, string target, string icon)
    {
        try
        {
            string line = (name ?? "").Replace('|', ' ').Trim() + " | " + Util.Collapse(target) + (string.IsNullOrEmpty(icon) ? "" : " | " + icon.Replace("|", ""));
            File.AppendAllText(cfgPath, Environment.NewLine + line + Environment.NewLine, new UTF8Encoding(true));
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(L.T("Не вдалося записати neobar.ini:\n") + ex.Message, "NeoBar",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void ShowFlyout(Entry head)
    {
        if (flyout != null) flyout.Close();
        flyout = new FlyoutForm(this, head);
        flyout.FormClosed += delegate { flyout = null; };
        flyout.ShowAt(RectangleToScreen(RectOf(head)), cfg.Side);
    }
}
