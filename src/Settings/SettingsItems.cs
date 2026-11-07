using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

using System.Drawing.Text;

// Вікно налаштувань: елементи панелі - пакетне додавання, список, властивості, іконки сайтів
sealed partial class SettingsForm
{
    // ---------- пакетне додавання ----------

    // Діалог із прапорцями; повертає індекси обраних
    // have[i] == true - такий елемент уже є: він відмічений; повторно не додається, а якщо галку знято - потрапляє в removed (його треба прибрати)
    static List<int> Pick(IWin32Window parent, string title, string hint, List<string> items, bool preCheck, List<bool> have = null, List<int> removed = null)
    {
        List<int> res = new List<int>();
        using (Form f = new Form())
        {
            f.Text = title;
            f.Font = SUi.Base;
            f.AutoScaleMode = AutoScaleMode.None;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            AppIcon.Apply(f);
            f.StartPosition = FormStartPosition.CenterParent;
            f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false;
            f.BackColor = FormTheme.Bg; f.ForeColor = FormTheme.Fg;
            f.ClientSize = new Size(SUi.S(460), SUi.S(540));
            f.HandleCreated += delegate { FormTheme.Title(f); };
            int p = SUi.S(18), W = f.ClientSize.Width, H = f.ClientSize.Height;
            Label lh = Lbl(f, hint, p, p, W - p * 2, SUi.S(40));
            lh.ForeColor = FormTheme.Sub;
            SCard card = new SCard();
            f.Controls.Add(card);
            SList l = new SList();
            l.CheckBoxes = true;
            l.ItemHeight = 32;
            card.Controls.Add(l);
            for (int hi = 0; hi < items.Count; hi++)
            {
                bool dup = have != null && hi < have.Count && have[hi];
                l.Items.Add(items[hi], dup || preCheck);
            }
            int bh = SUi.S(36), by = H - p - bh;
            card.SetBounds(p, p + SUi.S(46), W - p * 2, by - SUi.S(14) - (p + SUi.S(46)));
            l.SetBounds(SUi.S(6), SUi.S(6), card.Width - SUi.S(12), card.Height - SUi.S(12));
            Button all = Bt(L.T("Усі"), "ghost g:check", 84, delegate { for (int i = 0; i < l.Items.Count; i++) l.SetChecked(i, true); });
            Button none = Bt(L.T("Жодного"), "ghost g:close", 104, delegate { for (int i = 0; i < l.Items.Count; i++) l.SetChecked(i, have != null && i < have.Count && have[i]); });
            Button ok = have != null ? Bt(L.T("Застосувати"), "primary g:check", 130, null) : Bt(L.T("Додати"), "primary g:plus", 116, null);
            ok.DialogResult = DialogResult.OK;
            Button cancel = Bt(L.T("Скасувати"), "", 116, null);
            cancel.DialogResult = DialogResult.Cancel;
            foreach (Button b in new Button[] { all, none, ok, cancel }) f.Controls.Add(b);
            all.SetBounds(p, by, all.Width, bh);
            none.SetBounds(all.Right + SUi.S(6), by, none.Width, bh);
            cancel.SetBounds(W - p - cancel.Width, by, cancel.Width, bh);
            ok.SetBounds(cancel.Left - SUi.S(8) - ok.Width, by, ok.Width, bh);
            f.AcceptButton = ok;
            f.CancelButton = cancel;
            FormTheme.Apply(f);
            if (f.ShowDialog(parent) != DialogResult.OK) return res;
            res = l.CheckedIndices;
            if (have != null)
            {
                res.RemoveAll(delegate (int i) { return i < have.Count && have[i]; });
                if (removed != null) for (int i = 0; i < l.Items.Count && i < have.Count; i++) if (have[i] && !l.IsChecked(i)) removed.Add(i);
            }
        }
        return res;
    }

    static string TargetKey(string t)
    {
        string s = (t ?? "").Trim().Trim('"');
        try { s = Environment.ExpandEnvironmentVariables(s); } catch { }
        return s.TrimEnd('\\', '/').ToLowerInvariant();
    }

    void AddMany(List<Entry> es)
    {
        if (es.Count == 0) return;
        int at = InsertPos();
        Entry last = null;
        foreach (Entry e in es) { if (e.Kind == Kind.Widget) { if (WidgetOf(e.Target) == null) list.Insert(Store.WidgetCount(list), e); last = e; continue; } list.Insert(at++, e); last = e; }
        RefreshSel(last);
    }

    void AddSystem()
    {
        List<string> names = new List<string>();
        List<bool> have = new List<bool>();
        List<string> present = new List<string>();
        foreach (Entry pe in list) if (pe.Kind == Kind.Item) present.Add(TargetKey(pe.Target));
        foreach (string[] s in SysCatalog.Items) { names.Add(s[0]); have.Add(present.Contains(TargetKey(s[1]))); }
        List<int> gone = new List<int>();
        List<int> sel = Pick(this, L.T("Системні елементи"), L.T("Місця й утиліти Windows. Позначені вже є на панелі - зніміть галку, щоб їх прибрати."), names, false, have, gone);
        if (gone.Count > 0)
        {
            List<string> keys = new List<string>();
            foreach (int gi in gone) keys.Add(TargetKey(SysCatalog.Items[gi][1]));
            list.RemoveAll(delegate (Entry pe) { return pe.Kind == Kind.Item && !pe.Auto && keys.Contains(TargetKey(pe.Target)); });
            RefreshSel(null);
        }
        List<Entry> es = new List<Entry>();
        foreach (int i in sel)
        {
            string[] s = SysCatalog.Items[i];
            Entry e = MakeEntry(Kind.Item, s[0], s[1]);
            e.IconSpec = "theme:" + s[2];
            es.Add(e);
        }
        AddMany(es);
    }

    void AddDesktop()
    {
        List<string> files = new List<string>();
        foreach (Environment.SpecialFolder sf in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
        {
            try
            {
                string dir = Environment.GetFolderPath(sf);
                if (!Directory.Exists(dir)) continue;
                foreach (string f in Directory.GetFiles(dir))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".lnk" && ext != ".url" && ext != ".exe") continue;
                    files.Add(f);
                }
            }
            catch { }
        }
        if (files.Count == 0)
        {
            SbDialog.Show(owner, this, L.T("Ярлики з робочого столу"), L.T("На робочому столі не знайдено ярликів."), L.T("Гаразд"));
            return;
        }
        List<string> names = new List<string>();
        foreach (string f in files) names.Add(Path.GetFileNameWithoutExtension(f));
        List<int> sel = Pick(this, L.T("Ярлики з робочого столу"), L.T("Оберіть ярлики, які потрібно додати на панель."), names, true);
        List<Entry> es = new List<Entry>();
        foreach (int i in sel) es.Add(MakeEntry(Kind.Item, names[i], Util.Collapse(files[i])));
        AddMany(es);
    }

    void OnTagsChanged()
    {
        if (loading) return;
        Entry e = Sel();
        if (e == null || e.Kind != Kind.Item) return;
        e.Tags = tbTags.Text;
    }

    void OnGModeChanged()
    {
        if (loading) return;
        Entry e = Sel();
        if (e == null || e.Kind != Kind.Header) return;
        e.GMode = cbGMode.SelectedIndex == 1 ? "open" : (cbGMode.SelectedIndex == 2 ? "fold" : "");
    }

    // ---------- список елементів ----------

    static string LabelOf(Entry e)
    {
        if (e.Kind == Kind.Header) return "▼ " + e.Name + " " + L.T("   (група)").Trim();
        if (e.Kind == Kind.Separator) return e.Auto ? L.T("роздільник віджетів (автоматичний)") : L.T("-------  роздільник  -------");
        if (e.Kind == Kind.Widget) return "◆ " + WidgetCatalog.Title(e.Target) + " " + L.T("   (віджет)").Trim();
        return "      " + e.Name;
    }

    Entry Sel()
    {
        return EntryAt(lb.SelectedIndex);
    }

    static Entry MakeEntry(Kind k, string name, string target)
    {
        Entry e = new Entry();
        e.Kind = k; e.Name = name; e.Target = target;
        return e;
    }

    // select - індекс у list (вибрати його рядок)
    void RefreshList(int select)
    {
        Entry e = select >= 0 && select < list.Count ? list[select] : null;
        if (e == null && list.Count > 0 && select >= list.Count) e = list[list.Count - 1];
        RefreshSel(e);
    }

    // перебудувати список і вибрати рядок з цим елементом (Entry або вид віджета)
    void RefreshSel(object sel)
    {
        loading = true;
        BuildRows();
        lb.BeginUpdate();
        lb.Items.Clear();
        foreach (object o in rows)
        {
            Entry e = o as Entry;
            lb.Items.Add(e != null ? LabelOf(e) : "◆ " + WidgetCatalog.Title((string)o) + " " + L.T("   (віджет)").Trim());
        }
        lb.EndUpdate();
        int r = RowOf(sel);
        if (r < 0 && sel is Entry && ((Entry)sel).Kind == Kind.Widget) r = RowOf(((Entry)sel).Target);
        if (r < 0 && rows.Count > 0) r = Math.Min(Math.Max(0, lb.SelectedIndex), rows.Count - 1);
        if (r >= 0 && r < rows.Count) lb.SelectedIndex = r;
        loading = false;
        LoadSel();
    }

    void AddEntry(Entry e)
    {
        if (e.Kind == Kind.Widget) { if (WidgetOf(e.Target) == null) list.Insert(Store.WidgetCount(list), e); RefreshSel(e); return; }
        int at = InsertPos();
        list.Insert(at, e);
        RefreshSel(e);
    }

    void AddFile()
    {
        using (OpenFileDialog d = new OpenFileDialog())
        {
            d.Title = L.T("Оберіть програму, гру або ярлик");
            d.Filter = L.T("Програми та ярлики|*.exe;*.lnk;*.bat;*.cmd;*.url|Усі файли|*.*");
            d.DereferenceLinks = false;
            if (d.ShowDialog(this) != DialogResult.OK) return;
            AddEntry(MakeEntry(Kind.Item, Path.GetFileNameWithoutExtension(d.FileName), Util.Collapse(d.FileName)));
        }
    }

    void AddFolder()
    {
        using (FolderBrowserDialog d = new FolderBrowserDialog())
        {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            string n = Path.GetFileName(d.SelectedPath.TrimEnd('\\'));
            AddEntry(MakeEntry(Kind.Item, n.Length > 0 ? n : d.SelectedPath, Util.Collapse(d.SelectedPath)));
        }
    }

    void AddSite()
    {
        AddEntry(MakeEntry(Kind.Item, L.T("Сайт"), "https://"));
        tbTarget.Focus();
        tbTarget.SelectionStart = tbTarget.Text.Length;
    }

    void DeleteSel()
    {
        Entry e = Sel();
        if (e == null || e.Auto) return;       // автоматичний роздільник не видаляється
        int i = list.IndexOf(e);
        list.RemoveAt(i);
        RefreshList(Math.Min(i, list.Count - 1));
    }

    void MoveSel(int dir)
    {
        int j;
        if (!CanMove(dir, out j)) return;
        Entry t = Sel();
        int i = list.IndexOf(t);
        list[i] = list[j];
        list[j] = t;
        RefreshSel(t);
    }

    // віджети рухаються лише між віджетами, решта - лише серед решти (не через автороздільник)
    bool CanMove(int dir, out int j)
    {
        j = -1;
        Entry e = Sel();
        if (e == null || e.Auto) return false;
        int i = list.IndexOf(e), wn = Store.WidgetCount(list);
        j = i + dir;
        if (e.Kind == Kind.Widget) return j >= 0 && j < wn;
        int first = wn > 0 ? wn + 1 : 0;
        return j >= first && j < list.Count;
    }

    // ---------- властивості вибраного елемента ----------

    void LoadSel()
    {
        loading = true;
        Entry e = Sel();
        int sidx = lb.SelectedIndex;
        if (e != null) lastReal = list.IndexOf(e);
        string inKind = IsInactiveRow(sidx) ? KindOfRow(sidx) : "";
        bool item = e != null && e.Kind == Kind.Item;
        bool grp = e != null && e.Kind == Kind.Header;
        bool wid = (e != null && e.Kind == Kind.Widget) || inKind.Length > 0;

        pk = item ? 1 : (grp ? 2 : (wid ? 3 : 0));

        if (item)
        {
            tbName.Text = e.Name;
            tbTarget.Text = e.Target;
            tbTags.Text = e.Tags ?? "";
            string s = e.IconSpec ?? "";
            tbIconFile.Text = "";
            cbThemeIcon.SelectedIndex = 0;
            if (s.StartsWith("theme:", StringComparison.OrdinalIgnoreCase))
            {
                cbIconMode.SelectedIndex = 1;
                string key = s.Substring(6);
                for (int i = 0; i < IconFactory.Catalog.Length; i++)
                    if (IconFactory.Catalog[i][0] == key) { cbThemeIcon.SelectedIndex = i; break; }
            }
            else if (s.Length > 0)
            {
                cbIconMode.SelectedIndex = 2;
                tbIconFile.Text = s;
            }
            else
            {
                cbIconMode.SelectedIndex = 0;
            }
        }
        else if (grp)
        {
            tbName.Text = e.Name;
            bColor.BackColor = e.GroupColor.IsEmpty ? owner.ThemeAccent : e.GroupColor;
            cbGMode.SelectedIndex = e.GMode == "open" ? 1 : (e.GMode == "fold" ? 2 : 0);
        }
        else if (wid)
        {
            string k = e != null ? e.Target : inKind;
            wTitle = L.T("Віджет «") + WidgetCatalog.Title(k) + L.Q("»");
            if (e != null) wDesc = (e.Off ? L.T("Схований з панелі (око в списку закрите): лишається в списку - зручно, коли його показано на робочому столі. ") : L.T("Стоїть на панелі. ")) +
                L.T("Віджети завжди на початку панелі; порядок між ними - кнопками ▲ ▼ або перетягуванням. Налаштування віджета - у його розширеній панелі (клік по віджету на панелі). Око в списку ховає чи показує його на панелі, «−» прибирає зі списку.");
            else wDesc = CanUse(inKind) ? L.T("Не на панелі. Натисніть «+» у списку - віджет стане останнім серед віджетів.") : L.T("Недоступний: у цьому комп'ютері немає батареї.");
        }

        int jj;
        bUp.Enabled = CanMove(-1, out jj);
        bDown.Enabled = CanMove(1, out jj);
        bDel.Enabled = e != null && !e.Auto;
        lStatus.Text = "";
        loading = false;
        UpdateIconControls();
        UpdatePreview();
    }

    void UpdateIconControls()
    {
        // видимість рядків вирішує сама картка (за видом вибраного й способом іконки)
        if (propCard != null) propCard.Queue();
    }

    void UpdatePreview()
    {
        Entry e = Sel();
        Image old = pic.Image;
        if (e != null && e.Kind == Kind.Item)
        {
            pic.BackColor = owner.ThemeBg;
            pic.Image = owner.PreviewIcon(e, 40);
        }
        else
        {
            pic.Image = null;
        }
        if (old != null) old.Dispose();
    }

    void OnNameChanged()
    {
        if (loading) return;
        Entry e = Sel();
        if (e == null) return;
        e.Name = tbName.Text;
        loading = true;
        lb.Items[lb.SelectedIndex] = LabelOf(e);
        loading = false;
    }

    void OnTargetChanged()
    {
        if (loading) return;
        Entry e = Sel();
        if (e == null || e.Kind != Kind.Item) return;
        e.Target = tbTarget.Text;
        UpdatePreview();
    }

    void OnIconModeChanged()
    {
        if (loading) return;
        Entry e = Sel();
        if (e == null || e.Kind != Kind.Item) return;
        int mode = cbIconMode.SelectedIndex;
        if (mode == 0)
        {
            e.IconSpec = "";
        }
        else if (mode == 1)
        {
            if (cbThemeIcon.SelectedIndex < 0) cbThemeIcon.SelectedIndex = 0;
            e.IconSpec = "theme:" + IconFactory.Catalog[cbThemeIcon.SelectedIndex][0];
        }
        else
        {
            e.IconSpec = tbIconFile.Text;
        }
        UpdateIconControls();
        UpdatePreview();
    }

    void OnThemeIconChanged()
    {
        if (loading) return;
        Entry e = Sel();
        if (e == null || e.Kind != Kind.Item || cbIconMode.SelectedIndex != 1) return;
        int i = cbThemeIcon.SelectedIndex;
        if (i < 0) return;
        e.IconSpec = "theme:" + IconFactory.Catalog[i][0];
        UpdatePreview();
    }

    void OnIconFileChanged()
    {
        if (loading) return;
        Entry e = Sel();
        if (e == null || e.Kind != Kind.Item || cbIconMode.SelectedIndex != 2) return;
        e.IconSpec = tbIconFile.Text;
        UpdatePreview();
    }

    void BrowseTargetFile()
    {
        using (OpenFileDialog d = new OpenFileDialog())
        {
            d.Filter = L.T("Програми та ярлики|*.exe;*.lnk;*.bat;*.cmd;*.url|Усі файли|*.*");
            d.DereferenceLinks = false;
            if (d.ShowDialog(this) == DialogResult.OK) tbTarget.Text = Util.Collapse(d.FileName);
        }
    }

    void BrowseTargetFolder()
    {
        using (FolderBrowserDialog d = new FolderBrowserDialog())
        {
            if (d.ShowDialog(this) == DialogResult.OK) tbTarget.Text = Util.Collapse(d.SelectedPath);
        }
    }

    void BrowseIcon()
    {
        using (OpenFileDialog d = new OpenFileDialog())
        {
            d.Filter = L.T("Іконки та зображення|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.exe;*.dll|Усі файли|*.*");
            if (d.ShowDialog(this) == DialogResult.OK) tbIconFile.Text = NormIcon(d.FileName);
        }
    }

    void PickGroupColor()
    {
        Entry e = Sel();
        if (e == null || e.Kind != Kind.Header) return;
        using (ColorDialog d = new ColorDialog())
        {
            d.Color = e.GroupColor.IsEmpty ? owner.ThemeAccent : e.GroupColor;
            d.FullOpen = true;
            if (d.ShowDialog(this) != DialogResult.OK) return;
            e.GroupColor = d.Color;
            bColor.BackColor = d.Color;
        }
    }

    void ResetGroupColor()
    {
        Entry e = Sel();
        if (e == null || e.Kind != Kind.Header) return;
        e.GroupColor = Color.Empty;
        bColor.BackColor = owner.ThemeAccent;
    }

    // ---------- іконка сайту ----------

    void DoFavicon()
    {
        Entry e = Sel();
        if (e == null || e.Kind != Kind.Item) return;
        Uri u;
        if (!Uri.TryCreate(tbTarget.Text.Trim(), UriKind.Absolute, out u) ||
            (u.Scheme != "http" && u.Scheme != "https"))
        {
            lStatus.Text = L.T("Вкажіть адресу сайту (https://…) у полі «Ціль».");
            return;
        }
        bFavicon.Enabled = false;
        lStatus.Text = L.T("Завантаження іконки…");
        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icons");
        Util.Bg(delegate
        {
            string file = null;
            try { file = Util.DownloadFavicon(u, dir); }
            catch { }
            try { BeginInvoke(new Action(delegate { OnFavicon(e, file); })); }
            catch { }
        });
    }

    void OnFavicon(Entry e, string file)
    {
        bFavicon.Enabled = true;
        if (file == null)
        {
            lStatus.Text = L.T("Не вдалося завантажити іконку. Перевірте адресу та інтернет.");
            return;
        }
        lStatus.Text = L.T("Готово: іконку збережено в папці icons.");
        e.IconSpec = Util.ToRelative(file);
        if (Sel() == e)
        {
            loading = true;
            cbIconMode.SelectedIndex = 2;
            tbIconFile.Text = e.IconSpec;
            loading = false;
            UpdateIconControls();
            UpdatePreview();
        }
    }
}
