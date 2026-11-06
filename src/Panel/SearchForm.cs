using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// Пошук: вікно пошуку програм, файлів і налаштувань

// Пошук за назвою, ціллю та тегами (#тег - лише за тегами)
//  Дві вкладки: «Панель» (елементи) і «Комп'ютер» (усі файли та папки, як у файловому менеджері)
sealed class SearchForm : Form
{
    readonly NeoBar owner;
    readonly Cfg cfg;
    readonly Theme t;
    readonly Font f, small, tabFont;
    readonly float sc;
    readonly TextBox tb = new TextBox();
    readonly FastList lb = new FastList();
    readonly Label hint = new Label();
    readonly List<Entry> all = new List<Entry>();
    readonly Dictionary<string, Bitmap> icons = new Dictionary<string, Bitmap>();
    readonly System.Windows.Forms.Timer delay = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer();
    bool fileMode, wasIndexing;
    int rowH, myGen, radius, hoverRow = -1, baseTop;
    string note = "";

    // геометрія (координати клієнтської області)
    Rectangle rTrack, rTabP, rTabF, rChip, rField, rList;
    // стан анімацій
    float openT, tabT, focusT;
    readonly float[] hv = new float[3];
    int hot = -1;
    // динамічний фільтр за розширеннями
    List<FileHit> raw = new List<FileHit>();
    string curKey;   // null - усі типи
    List<string[]> recOrder;   // випадковий порядок рекомендацій на це відкриття
    bool shuffleHot;

    int Px(float v) { return (int)Math.Round(v * sc); }

    public SearchForm(NeoBar owner, Rectangle wa)
    {
        this.owner = owner;
        cfg = owner.Config;
        t = owner.CurrentTheme;
        f = (Font)owner.LabelFont.Clone();   // власна копія: панель після перезавантаження знищує свій шрифт
        sc = owner.Dpi;
        small = new Font(f.Name, Math.Max(6f, f.Size - 0.5f), FontStyle.Regular);
        tabFont = new Font(f.Name, f.Size + 1f, FontStyle.Regular);

        FormBorderStyle = FormBorderStyle.None;
        AppIcon.Apply(this);
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = PopStyle.Bg(t);
        Opacity = 0;

        foreach (Entry e in owner.Entries) if (e.Kind == Kind.Item) all.Add(e);

        Color bg = PopStyle.Bg(t), txt = PopStyle.Text(t);
        int w = Math.Max(Px(520), Math.Min(Px(940), (int)(wa.Width * 0.42f)));
        int h = Math.Max(Px(400), Math.Min(Px(860), (int)(wa.Height * 0.62f)));
        radius = Px(14);
        int m = Px(14);

        // вкладки (малюються на формі, піґулька-підсвітка ковзає між ними)
        int tw = Px(116), th = Px(34), pad = Px(4);
        int nt = cfg.FileSearch ? 2 : 1;
        rTrack = new Rectangle(m, m, tw * nt + pad * 2, th);
        rTabP = new Rectangle(m + pad, m, tw, th);
        rTabF = new Rectangle(m + pad + tw, m, tw, th);
        rChip = new Rectangle(w - m - Px(190), m, Px(190), th);

        // поле вводу в заокругленій рамці
        tb.Font = new Font(f.Name, f.Size + 3.5f, FontStyle.Regular);
        tb.BorderStyle = BorderStyle.None;
        tb.BackColor = FieldCol();
        tb.ForeColor = txt;
        int fh = tb.PreferredHeight + Px(18);
        rField = new Rectangle(m, m + th + Px(12), w - m * 2, fh);
        tb.SetBounds(rField.X + Px(44), rField.Y + (fh - tb.PreferredHeight) / 2, rField.Width - Px(44) - Px(16), tb.PreferredHeight);
        tb.TextChanged += delegate { Kick(); };
        tb.KeyDown += OnTbKey;
        tb.GotFocus += delegate { StartAnim(); };
        tb.LostFocus += delegate { StartAnim(); };
        Controls.Add(tb);

        hint.Font = small;
        hint.ForeColor = Util.Blend(bg, txt, 0.55f);
        hint.BackColor = bg;
        hint.AutoSize = false;
        hint.UseMnemonic = false;
        hint.SetBounds(m + Px(4), rField.Bottom + Px(8), w - m * 2 - Px(8), small.Height * 2 + Px(4));
        Controls.Add(hint);

        rowH = Math.Max(Px(40), f.Height + small.Height + Px(12));
        rList = new Rectangle(m, hint.Bottom + Px(6), w - m * 2, Math.Max(rowH + Px(8), h - hint.Bottom - Px(6) - m));
        lb.DrawMode = DrawMode.OwnerDrawFixed;
        lb.ItemHeight = rowH;
        lb.IntegralHeight = false;
        lb.BorderStyle = BorderStyle.None;
        lb.BackColor = bg;
        lb.ForeColor = txt;
        lb.DrawItem += DrawRow;
        lb.MouseUp += OnListMouse;
        lb.MouseMove += delegate (object s, MouseEventArgs e)
        {
            int i = lb.IndexFromPoint(e.Location);
            // рука - лише над кнопками («+ На панель», «перемішати»)
            bool onBtn = false, sh = false;
            if (i >= 0 && i < lb.Items.Count)
            {
                Rectangle ir = lb.GetItemRectangle(i);
                if (lb.Items[i] is SearchRec) onBtn = RecButton(ir).Contains(e.Location);
                else if (lb.Items[i] is RecHeader) onBtn = sh = ShuffleButton(ir).Contains(e.Location);
            }
            lb.Cursor = onBtn ? Cursors.Hand : Cursors.Default;
            if (sh != shuffleHot) { shuffleHot = sh; InvRow(i); }
            if (i != hoverRow) { int old = hoverRow; hoverRow = i; InvRow(old); InvRow(i); }
        };
        lb.MouseLeave += delegate { if (hoverRow != -1) { int old = hoverRow; hoverRow = -1; InvRow(old); } };
        lb.SetBounds(rList.X + Px(5), rList.Y + Px(5), rList.Width - Px(10), rList.Height - Px(10));
        Controls.Add(lb);

        ClientSize = new Size(w, h);
        delay.Interval = 220;
        delay.Tick += delegate { delay.Stop(); Refill(); };
        tick.Interval = 600;
        tick.Tick += delegate
        {
            if (!fileMode) return;
            bool now = FileFind.Indexing;
            if (wasIndexing && !now) { wasIndexing = false; Refill(); return; }   // індекс готовий - повторюємо пошук
            wasIndexing = now;
            UpdateHint();
        };
        anim.Interval = 15;
        anim.Tick += delegate { Step(); };

        SetMode(false);
        Refill();
        if (cfg.SearchSuggest)
        {
            Action onReady = null;
            onReady = delegate { try { if (!IsDisposed) BeginInvoke(new Action(delegate { if (!IsDisposed && !fileMode) Refill(); })); } catch { } };
            StartApps.Ready += onReady;
            FormClosed += delegate { StartApps.Ready -= onReady; };
        }
    }

    void InvRow(int i)
    {
        if (i >= 0 && i < lb.Items.Count) lb.Invalidate(lb.GetItemRectangle(i));
    }

    Color FieldCol() { return Util.Blend(PopStyle.Bg(t), PopStyle.Text(t), 0.10f); }

    // ---------- анімації ----------

    void StartAnim() { if (!anim.Enabled) anim.Start(); }

    static float Ease(float v, float target, float k)
    {
        float d = target - v;
        return Math.Abs(d) < 0.004f ? target : v + d * k;
    }

    void Step()
    {
        bool busy = false;
        if (openT < 1f)
        {
            openT = Math.Min(1f, openT + 0.1f);
            float e = 1f - (float)Math.Pow(1f - openT, 3);
            try { Opacity = Math.Min(0.995, e); } catch { }
            Top = baseTop + (int)((1f - e) * Px(16));
            busy = true;
        }
        float tt = fileMode ? 1f : 0f;
        float n = Ease(tabT, tt, 0.28f);
        if (n != tabT) { tabT = n; busy = true; }
        n = Ease(focusT, tb.Focused ? 1f : 0f, 0.25f);
        if (n != focusT) { focusT = n; busy = true; }
        for (int i = 0; i < 3; i++)
        {
            n = Ease(hv[i], hot == i ? 1f : 0f, 0.3f);
            if (n != hv[i]) { hv[i] = n; busy = true; }
        }
        Invalidate(new Rectangle(0, 0, Width, rField.Bottom + Px(6)));
        if (!busy) anim.Stop();
    }

    void SetHot(int h)
    {
        if (h == 1 && !cfg.FileSearch) h = -1;
        if (h == 2 && !fileMode) h = -1;
        if (h == hot) return;
        hot = h;
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        StartAnim();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHot(rTabP.Contains(e.Location) ? 0 : rTabF.Contains(e.Location) ? 1 : rChip.Contains(e.Location) ? 2 : -1);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHot(-1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (rTabP.Contains(e.Location)) SetMode(false);
        else if (cfg.FileSearch && rTabF.Contains(e.Location)) SetMode(true);
        else if (fileMode && rChip.Contains(e.Location)) ShowFilterMenu();
    }

    void SetMode(bool files)
    {
        if (files && !cfg.FileSearch) files = false;
        fileMode = files;
        raw.Clear();
        curKey = null;
        if (files)
        {
            FileFind.Prepare(cfg.FsEngine);
            tick.Start();
        }
        else tick.Stop();
        StartAnim();
        tb.Focus();
        Invalidate();
        Kick();
    }

    // ---------- динамічний фільтр ----------

    static string KeyOf(FileHit h)
    {
        if (h.IsDir) return "\\dir";
        string x = Path.GetExtension(h.Name).ToLowerInvariant();
        return x.Length == 0 ? "\\none" : x;
    }

    static string KeyName(string k)
    {
        if (k == null) return L.T("Усі типи");
        if (k == "\\dir") return L.T("Папки");
        if (k == "\\none") return L.T("Без розширення");
        return k;
    }

    void ShowFilterMenu()
    {
        Dictionary<string, int> cnt = new Dictionary<string, int>();
        foreach (FileHit h in raw)
        {
            string k = KeyOf(h);
            int c;
            cnt.TryGetValue(k, out c);
            cnt[k] = c + 1;
        }
        List<KeyValuePair<string, int>> lst = new List<KeyValuePair<string, int>>(cnt);
        lst.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b)
        {
            int c = b.Value.CompareTo(a.Value);
            return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
        });
        Color bg = PopStyle.Bg(t), txt = PopStyle.Text(t);
        ContextMenuStrip m = new ContextMenuStrip();
        m.Font = f;
        m.BackColor = Util.Blend(bg, txt, 0.08f);
        m.ForeColor = txt;
        AddFilterItem(m, null, L.T("Усі типи  (") + raw.Count + ")");
        int shown = 0;
        foreach (KeyValuePair<string, int> kv in lst)
        {
            if (shown++ >= 30) break;
            AddFilterItem(m, kv.Key, KeyName(kv.Key) + "  (" + kv.Value + ")");
        }
        if (lst.Count == 0)
        {
            ToolStripItem none = m.Items.Add(L.T("Спершу введіть запит"));
            none.Enabled = false;
        }
        RoundMenu.Apply(m, bg, txt, Util.Opaque(t.Accent), Util.Blend(bg, txt, 0.22f), Px(10));
        m.Closed += delegate { BeginInvoke(new Action(delegate { m.Dispose(); if (!IsDisposed) tb.Focus(); })); };
        m.Show(this, new Point(rChip.X, rChip.Bottom + Px(4)));
    }

    void AddFilterItem(ContextMenuStrip m, string key, string text)
    {
        ToolStripMenuItem it = new ToolStripMenuItem(text);
        it.Checked = key == curKey;
        it.BackColor = m.BackColor;
        it.ForeColor = m.ForeColor;
        string k = key;
        it.Click += delegate { curKey = k; ApplyFilter(); Invalidate(rChip); };
        m.Items.Add(it);
    }

    void ApplyFilter()
    {
        List<FileHit> v = new List<FileHit>();
        foreach (FileHit h in raw) if (curKey == null || KeyOf(h) == curKey) v.Add(h);
        Fill(v.ToArray());
        UpdateHint();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x80;
            cp.ClassStyle |= 0x20000;
            return cp;
        }
    }

    void ApplyRegion()
    {
        try
        {
            using (GraphicsPath gp = Util.Round(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), radius)) Util.SetRegion(this, gp);
        }
        catch { }
    }

    public void ShowCentered(Rectangle wa)
    {
        baseTop = wa.Top + Math.Max(Px(40), (wa.Height - Height) / 4);
        Location = new Point(wa.Left + (wa.Width - Width) / 2, baseTop + Px(16));
        ApplyRegion();
        Show();
        try { Win.SetForegroundWindow(Handle); } catch { }
        Activate();
        tb.Focus();
        StartAnim();
    }

    // ---------- пошук ----------

    void Kick()
    {
        delay.Stop();
        if (fileMode) delay.Start();
        else Refill();
    }

    int Score(Entry e, string q)
    {
        if (q.Length == 0) return 5;
        string ql = q.TrimStart('#');
        if (e.Name.StartsWith(ql, StringComparison.CurrentCultureIgnoreCase) && !q.StartsWith("#")) return 0;
        if (e.Name.IndexOf(ql, StringComparison.CurrentCultureIgnoreCase) >= 0 && !q.StartsWith("#")) return 1;
        if ((e.Tags ?? "").IndexOf(ql, StringComparison.CurrentCultureIgnoreCase) >= 0) return 2;
        return 3;
    }

    static bool LooksLikeTarget(string q)
    {
        if (q.Length < 3 || q.StartsWith("#")) return false;
        if (Util.IsUrl(q) || File.Exists(Environment.ExpandEnvironmentVariables(q)) || Directory.Exists(Environment.ExpandEnvironmentVariables(q))) return true;
        return q.IndexOf(' ') < 0 && q.IndexOf('.') > 0 && !q.EndsWith(".");
    }

    void Refill()
    {
        string q = tb.Text.Trim();
        if (fileMode) { RefillFiles(q); return; }

        List<Entry> res = new List<Entry>();
        foreach (Entry e in all) if (e.Matches(q)) res.Add(e);
        res.Sort(delegate (Entry a, Entry b)
        {
            int c = Score(a, q).CompareTo(Score(b, q));
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });
        if (res.Count > 200) res.RemoveRange(200, res.Count - 200);
        if (LooksLikeTarget(q))
        {
            Entry x = new Entry();
            x.Kind = Kind.Item;
            x.Name = L.T("Відкрити: ") + q;
            string u = q;
            if (!Util.IsUrl(u) && !File.Exists(Environment.ExpandEnvironmentVariables(u)) && !Directory.Exists(Environment.ExpandEnvironmentVariables(u)))
                u = "https://" + u;
            x.Target = u;
            res.Add(x);
        }
        List<object> rows = new List<object>(res.ToArray());
        // рекомендації: програми й ігри, яких ще немає на панелі. Порожній запит - 5 випадкових (щоразу інших),
        // із запитом - ті, що підходять. Додали одну - на її місце стає наступна.
        if (cfg.SearchSuggest && !q.StartsWith("#"))
        {
            HashSet<string> have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Entry e in all) { have.Add(e.Name.Trim()); have.Add(Util.Collapse(e.Target ?? "")); }
            List<string[]> src = StartApps.List;
            if (recOrder == null && src.Count > 0)
            {
                recOrder = new List<string[]>(src);
                Random rnd = new Random();
                for (int i = recOrder.Count - 1; i > 0; i--) { int j = rnd.Next(i + 1); string[] tmp = recOrder[i]; recOrder[i] = recOrder[j]; recOrder[j] = tmp; }
            }
            int n = 0, max = q.Length == 0 ? 5 : 6;
            if (recOrder != null && (q.Length == 0 || q.Length >= 2))
                foreach (string[] a2 in recOrder)
                {
                    if (q.Length > 0 && a2[0].IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                    if (have.Contains(a2[0]) || have.Contains(Util.Collapse(a2[1]))) continue;
                    if (n == 0) rows.Add(new RecHeader());
                    SearchRec r = new SearchRec(); r.Name = a2[0]; r.Path = a2[1]; r.IconFile = a2.Length > 2 ? a2[2] : "";
                    rows.Add(r);
                    if (++n >= max) break;
                }
        }
        // той самий запит - будь-яке оновлення списку (в т.ч. фонове) лишає прокрутку й виділення на місці
        bool keep = q == lastQ && lb.Items.Count > 0;
        int kTop = lb.TopIndex, kSel = lb.SelectedIndex;
        lb.BeginUpdate();
        Fill(rows.ToArray());
        if (keep && lb.Items.Count > 0)
        {
            lb.SelectedIndex = Selectable(Math.Max(0, Math.Min(lb.Items.Count - 1, kSel)), 1);
            lb.TopIndex = Math.Max(0, Math.Min(lb.Items.Count - 1, kTop));
        }
        lb.EndUpdate();
        lastQ = q;
        UpdateHint();
    }

    string lastQ;

    void RefillFiles(string q)
    {
        lastQ = null;
        myGen = FileFind.NextGen();
        if (q.Length == 0)
        {
            raw.Clear();
            curKey = null;
            Fill(new object[0]);
            UpdateHint();
            return;
        }
        int g = myGen;
        note = "…";
        UpdateHint();
        FileFind.Begin(q, 0, cfg.FsEngine, 800, g, delegate (List<FileHit> res, string eng, int rg)
        {
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (IsDisposed || rg != myGen || !fileMode) return;
                    note = eng;
                    raw = res;
                    if (curKey != null)
                    {
                        bool has = false;
                        foreach (FileHit fh in raw) if (KeyOf(fh) == curKey) { has = true; break; }
                        if (!has) curKey = null;
                    }
                    ApplyFilter();
                    Invalidate(rChip);
                }));
            }
            catch { }
        });
    }

    void Fill(object[] items)
    {
        lb.BeginUpdate();
        lb.Items.Clear();
        lb.Items.AddRange(items);
        lb.EndUpdate();
        if (lb.Items.Count > 0) lb.SelectedIndex = lb.Items[0] is RecHeader && lb.Items.Count > 1 ? 1 : 0;
    }

    // заголовок рекомендацій не вибирається: перескакуємо його
    int Selectable(int i, int dir)
    {
        if (i >= 0 && i < lb.Items.Count && lb.Items[i] is RecHeader)
        {
            int j = i + (dir >= 0 ? 1 : -1);
            if (j < 0 || j >= lb.Items.Count) j = i - (dir >= 0 ? 1 : -1);
            if (j >= 0 && j < lb.Items.Count) return j;
        }
        return i;
    }

    Rectangle ShuffleButton(Rectangle row)
    {
        int s = Px(24);
        return new Rectangle(row.Right - s - Px(10), row.Y + (row.Height - s) / 2, s, s);
    }

    // оновлення рекомендацій: рядки згасають, міняються на нові й з'являються; позиція списку не зміняється
    readonly FadeAnim recFade = new FadeAnim();
    System.Windows.Forms.Timer recTm;
    DateTime recStart;
    bool recSwapped;

    void Reshuffle()
    {
        if (recTm != null && recTm.Enabled) return;
        if (!cfg.Anim) { SwapRecs(); return; }
        recFade.Start();
        recStart = DateTime.Now;
        recSwapped = false;
        if (recTm == null)
        {
            recTm = new System.Windows.Forms.Timer();
            recTm.Interval = 15;
            recTm.Tick += delegate
            {
                if (!recSwapped && (DateTime.Now - recStart).TotalMilliseconds >= 340) { recSwapped = true; SwapRecs(); recFade.End(); }
                lb.Invalidate();
                if (recSwapped && !recFade.Active) recTm.Stop();
            };
        }
        recTm.Start();
        tb.Focus();
    }

    SearchRec pendingAdd;

    void SwapRecs()
    {
        int top = lb.TopIndex, sel = lb.SelectedIndex, oldCount = lb.Items.Count;
        lb.BeginUpdate();
        if (pendingAdd != null)
        {
            SearchRec rc = pendingAdd; pendingAdd = null;
            owner.AddToPanel(rc.Name, rc.Path, rc.IconFile);
            all.Clear();
            foreach (Entry e in owner.Entries) if (e.Kind == Kind.Item) all.Add(e);
        }
        else recOrder = null;
        Refill();
        if (lb.Items.Count > 0)
        {
            // доданий елемент з'явився у списку вище - зсуваємо позицію на цю різницю, щоб усе лишилось на тому ж місці екрана
            int d = lb.Items.Count - oldCount;
            lb.SelectedIndex = Selectable(Math.Max(0, Math.Min(lb.Items.Count - 1, sel + d)), 1);
            lb.TopIndex = Math.Max(0, Math.Min(lb.Items.Count - 1, top + d));
        }
        lb.EndUpdate();
        tb.Focus();
    }

    // обрізає рядок так, щоб він вмістився в ширину
    string Fit(string text, int width)
    {
        TextFormatFlags fl = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        if (TextRenderer.MeasureText(text, small, new Size(int.MaxValue, 100), fl).Width <= width) return text;
        while (text.Length > 1)
        {
            text = text.Substring(0, text.Length - 1);
            if (TextRenderer.MeasureText(text + "…", small, new Size(int.MaxValue, 100), fl).Width <= width)
                return text.TrimEnd() + "…";
        }
        return text;
    }

    void UpdateHint()
    {
        int w = hint.Width - Px(4);
        if (!fileMode)
        {
            hint.Text = lb.Items.Count == 0
                ? Fit(L.T("Нічого не знайдено на панелі."), w) + "\n" + Fit(L.T("Tab - пошук файлів на комп'ютері"), w)
                : Fit(L.T("Назва, адреса або #тег"), w) + "\n" + Fit(HasRec() ? L.T("Enter - відкрити   Insert - додати рекомендовану на панель   Tab - комп'ютер") : L.T("Enter - відкрити   Tab - комп'ютер   Esc - закрити"), w);
            return;
        }
        string eng;
        if (FileFind.Indexing) eng = L.T("Індексую диски (NTFS)…");
        else if (note.Length > 1)
            eng = note + (FileFind.IndexReady ? ": " + FileFind.IndexCount.ToString("N0") + L.T(" об'єктів") : "");
        else eng = FileFind.EngineStatus(cfg.FsEngine);
        string cnt = lb.Items.Count > 0 ? lb.Items.Count + L.T(" знайдено. ") : (tb.Text.Trim().Length == 0 ? "" : L.T("Нічого не знайдено. "));
        hint.Text = Fit(cnt + eng, w) + "\n" + Fit(L.T("ext:png   path:ігри   size:>100mb   dm:today   *.psd"), w);
    }

    // ---------- малювання ----------

    Bitmap IconFor(FileHit h)
    {
        string key = h.IsDir ? "\\dir" : Path.GetExtension(h.Name).ToLowerInvariant();
        if (key.Length == 0) key = "\\file";
        Bitmap b;
        if (icons.TryGetValue(key, out b)) return b;
        try { b = Util.ShellIcon(h.IsDir ? h.Path : "x" + key, h.IsDir); }
        catch { b = null; }
        icons[key] = b;
        return b;
    }

    void DrawRow(object sender, DrawItemEventArgs e)
    {
        // помилка малювання одного рядка не повинна ламати весь список (інакше WinForms малює червоний хрест)
        try { DrawRow0(e); } catch { }
    }

    void DrawRow0(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= lb.Items.Count) return;
        if (lb.Items[e.Index] is RecHeader)
        {
            Graphics hg = e.Graphics;
            Color hbg = PopStyle.Bg(t), htx = PopStyle.Text(t);
            using (SolidBrush b = new SolidBrush(hbg)) hg.FillRectangle(b, e.Bounds);
            hg.SmoothingMode = SmoothingMode.AntiAlias;
            int cy = e.Bounds.Y + e.Bounds.Height / 2;
            Size ts = TextRenderer.MeasureText(L.T("Рекомендації"), f);
            int tx0 = e.Bounds.X + Px(10);
            TextRenderer.DrawText(hg, L.T("Рекомендації"), f, new Rectangle(tx0, e.Bounds.Y, ts.Width + Px(4), e.Bounds.Height), Util.Blend(hbg, htx, 0.7f),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            Rectangle sb = ShuffleButton(e.Bounds);
            using (Pen p = new Pen(Util.Blend(hbg, htx, 0.16f)))
                hg.DrawLine(p, tx0 + ts.Width + Px(10), cy, sb.X - Px(8), cy);
            bool hot = e.Index == hoverRow && shuffleHot;
            using (SolidBrush b = new SolidBrush(Util.Blend(hbg, htx, hot ? 0.18f : 0.08f))) hg.FillEllipse(b, sb);
            float m = sb.Width * 0.24f;
            Ico.Draw(hg, "sync", new RectangleF(sb.X + m, sb.Y + m, sb.Width - m * 2, sb.Height - m * 2), hot ? Util.Opaque(t.Accent) : Util.Blend(hbg, htx, 0.7f));
            return;
        }
        object it = lb.Items[e.Index];
        Graphics g = e.Graphics;
        Color bg = PopStyle.Bg(t), txt = PopStyle.Text(t);
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (SolidBrush b = new SolidBrush(bg)) g.FillRectangle(b, e.Bounds);
        if (!sel && e.Index == hoverRow)
        {
            Rectangle hr = Rectangle.Inflate(e.Bounds, -Px(2), -Px(1));
            using (SolidBrush hb = new SolidBrush(Util.Blend(bg, txt, 0.08f)))
                Util.FillRound(g, hb, hr, t.Radius);
        }
        if (sel)
        {
            Rectangle sr = Rectangle.Inflate(e.Bounds, -Px(2), -Px(1));
            using (SolidBrush hb = new SolidBrush(Util.Blend(bg, Util.Opaque(t.Accent), 0.38f)))
                Util.FillRound(g, hb, sr, t.Radius);
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int isz = rowH - Px(12);
        Bitmap icon = null;
        string title, sub;
        float recLv = 1f;
        FileHit fh = it as FileHit;
        if (fh != null)
        {
            icon = IconFor(fh);
            title = fh.Name;
            sub = fh.Path;
            if (!fh.IsDir && fh.Size >= 0) sub += "   " + FileFind.SizeText(fh.Size);
        }
        else if (it is SearchRec)
        {
            SearchRec rc = (SearchRec)it;
            if (rc.Icon == null)
            {
                try
                {
                    if (rc.IconFile.Length > 0 && File.Exists(rc.IconFile))
                        using (FileStream fs = new FileStream(rc.IconFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (Image im = Image.FromStream(fs)) rc.Icon = new Bitmap(im, isz, isz);
                    else if (rc.Path.StartsWith("steam://", StringComparison.OrdinalIgnoreCase)) rc.Icon = IconFactory.Make(t, "game", rc.Name, isz);
                    else rc.Icon = Util.ShellIcon(rc.Path, false);
                }
                catch { }
            }
            icon = rc.Icon;
            recLv = recFade.Level;
            title = rc.Name;
            sub = rc.Path.StartsWith("steam://", StringComparison.OrdinalIgnoreCase) ? L.T("Рекомендація · гра Steam, ще не на панелі") : L.T("Рекомендація · ще не на панелі");
        }
        else
        {
            Entry en = (Entry)it;
            icon = en.Icon;
            title = en.Name;
            sub = en.Full;
            if (en.TagsText().Length > 0) sub += "   " + en.TagsText();
        }
        if (icon != null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            Rectangle icr = new Rectangle(e.Bounds.X + Px(9), e.Bounds.Y + (rowH - isz) / 2, isz, isz);
            if (recLv < 0.999f)
                using (ImageAttributes ia = new ImageAttributes())
                {
                    ColorMatrix cm = new ColorMatrix(); cm.Matrix33 = recLv;
                    ia.SetColorMatrix(cm);
                    g.DrawImage(icon, icr, 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, ia);
                }
            else g.DrawImage(icon, icr);
        }
        int tx = e.Bounds.X + Px(9) + isz + Px(9);
        int tw = e.Bounds.Right - tx - Px(9);
        if (it is SearchRec)
        {
            // кнопка «+ На панель» праворуч
            Rectangle pr = RecButton(e.Bounds);
            Color acc2 = Util.Opaque(t.Accent);
            using (SolidBrush pb = new SolidBrush(Util.Blend(bg, acc2, (e.Index == hoverRow || sel ? 0.55f : 0.32f) * recLv)))
                Util.FillRound(g, pb, pr, pr.Height / 2);
            TextRenderer.DrawText(g, L.T("+ На панель"), small, pr, Util.Blend(bg, txt, recLv), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            tw = pr.X - tx - Px(8);
        }
        TextFormatFlags fl = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(g, title, f, new Rectangle(tx, e.Bounds.Y + Px(5), tw, f.Height + 2), Util.Blend(bg, txt, recLv), fl);
        TextRenderer.DrawText(g, sub, small, new Rectangle(tx, e.Bounds.Y + Px(5) + f.Height + 1, tw, small.Height + 2),
            Util.Blend(bg, txt, 0.55f * recLv), fl);
    }

    static void DrawTri(Graphics g, Color c, int cx, int cy, int r)
    {
        using (SolidBrush b = new SolidBrush(c))
            g.FillPolygon(b, new Point[] { new Point(cx - r, cy - r / 2), new Point(cx + r, cy - r / 2), new Point(cx, cy + r / 2 + 1) });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color bg = PopStyle.Bg(t), txt = PopStyle.Text(t), acc = Util.Opaque(t.Accent);
        Color onAcc = Util.Lum(acc) > 150 ? Color.FromArgb(24, 24, 28) : Color.White;
        Color dim = Util.Blend(bg, txt, 0.72f);
        TextFormatFlags cf = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;

        // вкладки: доріжка + ковзна піґулька
        using (SolidBrush b = new SolidBrush(Util.Blend(bg, txt, 0.09f)))
            Util.FillRound(g, b, rTrack, rTrack.Height / 2);
        Rectangle pill = new Rectangle(rTabP.X + (int)Math.Round(tabT * rTabP.Width), rTabP.Y + Px(3), rTabP.Width, rTabP.Height - Px(6));
        using (SolidBrush gb = new SolidBrush(Color.FromArgb(46, acc)))
            Util.FillRound(g, gb, Rectangle.Inflate(pill, Px(2), Px(2)), pill.Height / 2 + Px(2));
        using (SolidBrush b = new SolidBrush(acc))
            Util.FillRound(g, b, pill, pill.Height / 2);
        string[] names = { L.T("Панель"), L.T("Комп'ютер") };
        for (int i = 0; i < (cfg.FileSearch ? 2 : 1); i++)
        {
            float on = i == 0 ? 1f - tabT : tabT;
            Color idle = Util.Blend(dim, txt, hv[i] * 0.6f);
            Color c = Util.Blend(idle, onAcc, on);
            TextRenderer.DrawText(g, names[i], tabFont, i == 0 ? rTabP : rTabF, c, cf);
        }

        // фільтр за розширенням (лише на вкладці «Комп'ютер»)
        if (fileMode)
        {
            using (SolidBrush b = new SolidBrush(Util.Blend(bg, txt, 0.10f + 0.07f * hv[2])))
                Util.FillRound(g, b, rChip, rChip.Height / 2);
            using (GraphicsPath gp = Util.Round(new Rectangle(rChip.X, rChip.Y, rChip.Width - 1, rChip.Height - 1), rChip.Height / 2))
            using (Pen p = new Pen(Util.Blend(Util.Blend(bg, txt, 0.22f), acc, Math.Min(1f, hv[2] * 0.8f + (curKey != null ? 0.5f : 0f))), 1.2f))
                g.DrawPath(p, gp);
            Rectangle tr = new Rectangle(rChip.X + Px(14), rChip.Y, rChip.Width - Px(14) - Px(32), rChip.Height);
            TextRenderer.DrawText(g, KeyName(curKey), f, tr, curKey != null ? acc : Util.Blend(dim, txt, 0.4f),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            DrawTri(g, Util.Blend(dim, acc, hv[2]), rChip.Right - Px(18), rChip.Y + rChip.Height / 2, Px(4));
        }

        // поле вводу
        int fr = Math.Min(Px(20), rField.Height / 2);
        using (SolidBrush b = new SolidBrush(FieldCol()))
            Util.FillRound(g, b, rField, fr);
        if (focusT > 0.01f)
            using (GraphicsPath gp = Util.Round(Rectangle.Inflate(new Rectangle(rField.X, rField.Y, rField.Width - 1, rField.Height - 1), Px(2), Px(2)), fr + Px(2)))
            using (Pen p = new Pen(Color.FromArgb((int)(70 * focusT), acc), Px(4)))
                g.DrawPath(p, gp);
        using (GraphicsPath gp = Util.Round(new Rectangle(rField.X, rField.Y, rField.Width - 1, rField.Height - 1), fr))
        using (Pen p = new Pen(Util.Blend(Util.Blend(bg, txt, 0.22f), acc, focusT), 1.4f))
            g.DrawPath(p, gp);
        // лупа
        Color lc = Util.Blend(Util.Blend(bg, txt, 0.5f), acc, focusT);
        int cx = rField.X + Px(26), cy = rField.Y + rField.Height / 2 - Px(1), r = Px(7);
        using (Pen p = new Pen(lc, Math.Max(1.6f, Px(2))))
        {
            p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
            g.DrawEllipse(p, cx - r, cy - r, r * 2, r * 2);
            g.DrawLine(p, cx + (int)(r * 0.72f) + 1, cy + (int)(r * 0.72f) + 1, cx + r + Px(4), cy + r + Px(4));
        }

        // рамка списку
        using (GraphicsPath gp = Util.Round(new Rectangle(rList.X, rList.Y, rList.Width - 1, rList.Height - 1), Px(10)))
        using (Pen p = new Pen(Util.Blend(bg, txt, 0.14f), 1f))
            g.DrawPath(p, gp);

        using (GraphicsPath gp = Util.Round(new Rectangle(0, 0, Width - 1, Height - 1), radius))
        using (Pen ep = new Pen(Color.FromArgb(210, t.Edge), Math.Max(1, Px(t.EdgeW))))
            g.DrawPath(ep, gp);
    }

    // ---------- дії ----------

    bool HasRec()
    {
        foreach (object o in lb.Items) if (o is SearchRec) return true;
        return false;
    }

    Rectangle RecButton(Rectangle row)
    {
        int bw = Px(104), bh = Px(26);
        return new Rectangle(row.Right - bw - Px(10), row.Y + (row.Height - bh) / 2, bw, bh);
    }

    // додати рекомендовану на панель: пошук лишається відкритим, на місце доданої стає наступна
    void AddRec(SearchRec rc)
    {
        if (recTm != null && recTm.Enabled) return;
        pendingAdd = rc;
        Reshuffle();   // так само, як оновлення: згасання, заміна на місці, поява
    }

    void OnListMouse(object s, MouseEventArgs e)
    {
        int i = lb.IndexFromPoint(e.Location);
        if (i < 0) return;
        if (lb.Items[i] is RecHeader)
        {
            if (e.Button == MouseButtons.Left && ShuffleButton(lb.GetItemRectangle(i)).Contains(e.Location)) Reshuffle();
            return;
        }
        lb.SelectedIndex = i;
        SearchRec rc = lb.Items[i] as SearchRec;
        if (rc != null && e.Button == MouseButtons.Left && RecButton(lb.GetItemRectangle(i)).Contains(e.Location)) { AddRec(rc); return; }
        if (e.Button == MouseButtons.Left) Go();
        else if (e.Button == MouseButtons.Right) ItemMenu(e.Location);
    }

    void ItemMenu(Point at)
    {
        FileHit h = lb.SelectedItem as FileHit;
        if (h == null) return;
        ContextMenuStrip m = new ContextMenuStrip();
        m.Items.Add(L.T("Відкрити"), null, delegate { Go(); });
        m.Items.Add(L.T("Показати у Провіднику"), null, delegate
        {
            try
            {
                string args = h.IsDir ? "\"" + h.Path + "\"" : "/select,\"" + h.Path + "\"";
                using (Process.Start("explorer.exe", args)) { }
            }
            catch { }
        });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(L.T("Додати на панель"), null, delegate
        {
            string nm = h.IsDir ? Path.GetFileName(h.Path.TrimEnd('\\')) : Path.GetFileNameWithoutExtension(h.Path);
            if (string.IsNullOrEmpty(nm)) nm = h.Name;
            owner.AddToPanel(nm, h.Path);
            Close();
        });
        m.Closed += delegate { BeginInvoke(new Action(m.Dispose)); };
        m.Show(lb, at);
    }

    void OnTbKey(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
        {
            if (lb.Items.Count > 0)
            {
                int i = lb.SelectedIndex + (e.KeyCode == Keys.Down ? 1 : -1);
                lb.SelectedIndex = Selectable(Math.Max(0, Math.Min(lb.Items.Count - 1, i)), e.KeyCode == Keys.Down ? 1 : -1);
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.PageDown || e.KeyCode == Keys.PageUp)
        {
            if (lb.Items.Count > 0)
            {
                int step = Math.Max(1, lb.Height / rowH - 1);
                int i = lb.SelectedIndex + (e.KeyCode == Keys.PageDown ? step : -step);
                lb.SelectedIndex = Selectable(Math.Max(0, Math.Min(lb.Items.Count - 1, i)), e.KeyCode == Keys.PageDown ? 1 : -1);
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Insert && lb.SelectedItem is SearchRec)
        {
            AddRec((SearchRec)lb.SelectedItem);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Enter)
        {
            Go();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Tab)
        {
            if (cfg.FileSearch) SetMode(!fileMode);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    void Go()
    {
        FileHit h = lb.SelectedItem as FileHit;
        if (h != null)
        {
            Entry x = new Entry();
            x.Kind = Kind.Item;
            x.Name = h.Name;
            x.Target = h.Path;
            Close();
            owner.LaunchEntry(x, false);
            return;
        }
        SearchRec rc = lb.SelectedItem as SearchRec;
        if (rc != null)
        {
            Entry rx = new Entry();
            rx.Kind = Kind.Item; rx.Name = rc.Name; rx.Target = rc.Path;
            Close();
            owner.LaunchEntry(rx, false);
            return;
        }
        Entry en = lb.SelectedItem as Entry;
        if (en == null) return;
        Close();
        owner.LaunchEntry(en, false);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        BeginInvoke(new Action(delegate { if (!IsDisposed) Close(); }));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            myGen = FileFind.NextGen();
            delay.Dispose();
            tick.Dispose();
            anim.Dispose();
            small.Dispose();
            tabFont.Dispose();
            f.Dispose();
            if (recTm != null) recTm.Dispose();
            foreach (Bitmap b in icons.Values) if (b != null) b.Dispose();
            icons.Clear();
        }
        base.Dispose(disposing);
    }
}

// Програми з меню «Пуск» (ярлики) - для рекомендацій у пошуку; список збирається один раз у фоні
static class StartApps
{
    static List<string[]> apps;          // {назва, ціль (ярлик або steam://), іконка або ""}
    static bool busy;
    public static event Action Ready;

    static bool Junk(string n)
    {
        string low = n.ToLowerInvariant();
        foreach (string w in new[] { "uninstall", "видалити", "удалить", "readme", "help", "довідка", "справка", "documentation", "website", "manual", "license", "release notes", "changelog" })
            if (low.Contains(w)) return true;
        return false;
    }

    public static List<string[]> List
    {
        get
        {
            if (apps != null) return apps;
            if (!busy)
            {
                busy = true;
                Util.Bg(delegate
                {
                    List<string[]> l = new List<string[]>();
                    HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    // ігри Steam (інтернет-ярлики Steam у «Пуску» є не завжди)
                    foreach (string[] st in Games.SteamApps()) if (seen.Add(st[0])) l.Add(st);
                    foreach (Environment.SpecialFolder sf in new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
                    {
                        try
                        {
                            string dir = Environment.GetFolderPath(sf);
                            if (dir.Length == 0 || !Directory.Exists(dir)) continue;
                            bool desk = sf == Environment.SpecialFolder.DesktopDirectory || sf == Environment.SpecialFolder.CommonDesktopDirectory;
                            foreach (string pat in new[] { "*.lnk", "*.url" })
                                foreach (string f in Directory.GetFiles(dir, pat, desk ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories))
                                {
                                    string n = Path.GetFileNameWithoutExtension(f);
                                    if (Junk(n) || !seen.Add(n)) continue;
                                    // .url - лише ярлики ігор (Steam, Epic тощо), не звичайні сайти
                                    if (pat == "*.url")
                                    {
                                        string tx = "";
                                        try { tx = File.ReadAllText(f); } catch { }
                                        if (tx.IndexOf("steam://", StringComparison.OrdinalIgnoreCase) < 0 && tx.IndexOf("com.epicgames", StringComparison.OrdinalIgnoreCase) < 0 &&
                                            tx.IndexOf("uplay://", StringComparison.OrdinalIgnoreCase) < 0 && tx.IndexOf("goggalaxy://", StringComparison.OrdinalIgnoreCase) < 0) { seen.Remove(n); continue; }
                                    }
                                    l.Add(new string[] { n, f, "" });
                                }
                        }
                        catch { }
                    }
                    l.Sort(delegate (string[] x, string[] y) { return string.Compare(x[0], y[0], StringComparison.CurrentCultureIgnoreCase); });
                    apps = l;
                    Action r = Ready;
                    if (r != null) try { r(); } catch { }
                });
            }
            return new List<string[]>();
        }
    }

    // після додавання на панель - перечитати при наступному відкритті нічого не треба: фільтр за панеллю
}

// рядок-рекомендація в пошуку: програма з «Пуску», якої ще немає на панелі
// заголовок розділу рекомендацій (роздільник + назва + кнопка «перемішати»)
sealed class RecHeader { }

sealed class SearchRec
{
    public string Name, Path, IconFile = "";
    public Bitmap Icon;
}
