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

// Вікно налаштувань: тема й кольори, віджети, пошук міста, експорт/імпорт/скидання
sealed partial class SettingsForm
{
    // ---------- вкладка «Вигляд» ----------

    int ThemeIndex(string id)
    {
        for (int i = 0; i < Themes.All.Count; i++) if (Themes.All[i].Id == id) return i;
        return 0;
    }
    void UpdateSwatches()
    {
        bool was = loading;
        loading = true;
        bool custom = c.ThemeId == "custom";
        Theme t = Themes.Get(c.ThemeId, c);
        Color[] cols = { t.Bg, t.Hover, t.Text, t.Accent };
        int bgPct = custom ? (c.Opacity >= 0 ? c.Opacity : 100) : t.Opacity;
        for (int i = 0; i < 4; i++)
        {
            Color op = Util.Opaque(cols[i]);
            bCol[i].UseVisualStyleBackColor = false;
            bCol[i].BackColor = op;
            bCol[i].ForeColor = Util.Lum(op) > 140 ? Color.Black : Color.White;
            bCol[i].FlatStyle = FlatStyle.Flat;   // щоб колір тексту діяв і на «вимкнених» кнопках
            bCol[i].ForeColor = Util.Lum(op) > 140 ? Color.Black : Color.White;
            int pct = i == 0 ? bgPct : (int)Math.Round(cols[i].A * 100 / 255.0);
            pct = Math.Max(0, Math.Min(100, pct));
            tbA[i].Value = pct;
            tbA[i].Enabled = custom;
            bCol[i].Enabled = custom;
            lA[i].Text = pct + "%";
        }
        chBlur.Checked = custom ? c.Blur == 1 : t.Glass;
        chBlur.Enabled = custom;
        bCopy.Enabled = !custom;
        loading = was;
    }

    void OnThemeChanged()
    {
        if (loading) return;
        c.ThemeId = Themes.All[cbTheme.SelectedIndex].Id;
        UpdateSwatches();
    }

    // Копіює кольори готової теми у «Власну», щоб їх можна було змінювати
    void ToCustom()
    {
        if (c.ThemeId == "custom") return;
        Theme t = Themes.Get(c.ThemeId, c);
        c.CBg = Util.Opaque(t.Bg);
        c.CHover = t.Hover;
        c.CText = t.Text;
        c.CAccent = t.Accent;
        c.Opacity = t.Opacity;
        c.Blur = t.Glass ? 1 : 0;
        c.ThemeId = "custom";
        loading = true;
        cbTheme.SelectedIndex = ThemeIndex("custom");
        loading = false;
    }

    void OnBlurChanged()
    {
        if (loading || c.ThemeId != "custom") return;
        c.Blur = chBlur.Checked ? 1 : 0;
    }

    void OnAlphaChanged(int k)
    {
        lA[k].Text = tbA[k].Value + "%";
        if (loading || c.ThemeId != "custom") return;
        if (k == 0)
        {
            c.Opacity = tbA[0].Value;
            return;
        }
        int a = (int)Math.Round(tbA[k].Value * 2.55);
        if (k == 1) c.CHover = Color.FromArgb(a, c.CHover);
        else if (k == 2) c.CText = Color.FromArgb(a, c.CText);
        else c.CAccent = Color.FromArgb(a, c.CAccent);
    }

    void PickColor(int k)
    {
        if (c.ThemeId != "custom") return;
        Color cur = k == 0 ? c.CBg : (k == 1 ? c.CHover : (k == 2 ? c.CText : c.CAccent));
        Color nc;
        if (ColorPick.Ask(this, Util.Opaque(cur), out nc))
        {
            if (k == 0) c.CBg = nc;
            else if (k == 1) c.CHover = Color.FromArgb(cur.A, nc);
            else if (k == 2) c.CText = Color.FromArgb(cur.A, nc);
            else c.CAccent = Color.FromArgb(cur.A, nc);
        }
        UpdateSwatches();
    }

    void UpdateTraySwatches()
    {
        for (int i = 0; i < 3; i++)
        {
            if (bTrayCol[i] == null) continue;
            Color op = Util.Opaque(trayCol[i]);
            bTrayCol[i].UseVisualStyleBackColor = false;
            bTrayCol[i].FlatStyle = FlatStyle.Flat;
            bTrayCol[i].BackColor = op;
            bTrayCol[i].ForeColor = Util.Lum(op) > 140 ? Color.Black : Color.White;
            bTrayCol[i].Invalidate();
        }
    }

    void PickTrayColor(int i)
    {
        Color nc;
        if (ColorPick.Ask(this, Util.Opaque(trayCol[i]), out nc)) trayCol[i] = nc;
        UpdateTraySwatches();
    }

    void FillView()
    {
        loading = true;
        int si = Array.IndexOf(sideIds, c.Side);
        cbSide.SelectedIndex = si >= 0 ? si : 1;
        int mi = monIds.IndexOf(c.Monitor);
        if (mi < 0) mi = monIds.IndexOf("");
        cbMonitor.SelectedIndex = mi >= 0 ? mi : 0;
        nW.Value = Math.Max(nW.Minimum, Math.Min(nW.Maximum, c.Width));
        nIcon.Value = Math.Max(nIcon.Minimum, Math.Min(nIcon.Maximum, c.IconSize));
        cbLabelPos.SelectedIndex = !c.Labels ? 2 : (c.LabelPos == "above" ? 1 : 0);
        chCompact.Checked = c.Compact;
        cbGroups.SelectedIndex = c.GroupMode == "fold" ? 1 : 0;
        cbFx.SelectedIndex = Math.Max(0, Math.Min(4, c.HoverFx));
        chAnim.Checked = c.Anim;
        chDeskFont.Checked = c.DeskNoteFont;
        int ak = Array.IndexOf(logKeepIds, c.LogKeep);
        cbLogKeep.SelectedIndex = ak >= 0 ? ak : 2;
        cbTheme.SelectedIndex = ThemeIndex(c.ThemeId);
        themeWas = c.ThemeId;
        int fi = c.FontName.Length == 0 ? 0 : cbFont.FindStringExact(c.FontName);
        cbFont.SelectedIndex = fi >= 0 ? fi : 0;
        int fs = Array.IndexOf(styleIds, c.FontStyle);
        cbFontStyle.SelectedIndex = fs >= 0 ? fs : 0;
        nFont.Value = Math.Max(nFont.Minimum, Math.Min(nFont.Maximum, (decimal)c.FontSize));
        UpdateSwatches();

        cbMode.SelectedIndex = c.Mode == "autohide" ? 1 : 0;
        nGap.Value = Math.Max(nGap.Minimum, Math.Min(nGap.Maximum, c.CornerGap));
        chFs.Checked = c.FsHide;
        chTray[0].Checked = c.TrayCpu; chTray[1].Checked = c.TrayGpu; chTray[2].Checked = c.TrayHot;
        trayCol[0] = c.TrayCpuCol; trayCol[1] = c.TrayGpuCol; trayCol[2] = c.TrayHotCol;
        UpdateTraySwatches();
        chHot.Checked = c.Hotkeys;
        chSuggest.Checked = c.SearchSuggest;
        chWelcome.Checked = !c.Welcomed;
        chUpd.Checked = c.UpdCheck;
        searchKeyOk = (Keys)c.SearchKey;
        hkSearch.SetValue(searchKeyOk);
        hkSearch.Enabled = c.Hotkeys;
        lHkSearch.Text = owner.SearchKeyOk || !c.Hotkeys ? L.T("Клацніть у поле й натисніть комбінацію (Esc - очистити)") : L.T("Комбінацію не вдалося зареєструвати - її вже зайняла інша програма. Виберіть іншу.");
        chSearch.Checked = c.ShowSearch;
        chFiles.Checked = c.FileSearch;
        chAdmin.Checked = c.AlwaysAdmin;
        chAuto.Checked = c.AlwaysAdmin ? c.AdminAuto : NeoBar.IsAutostart();
        adminWas = c.AlwaysAdmin; autoWas = chAuto.Checked;
        FillStatics();
        SyncCompact();

        cityLast = c.City.Trim();
        tbCity.Text = c.City;
        chLocAllow.Checked = c.LocAllow;
        rbAuto.Enabled = c.LocAllow;
        bDetect.Enabled = c.LocAllow;
        rbAuto.Checked = c.LocMode == "auto" && c.LocAllow;
        rbManual.Checked = !rbAuto.Checked;
        tbCity.Enabled = c.LocMode != "auto";
        if (!double.IsNaN(c.Lat)) map.SetMark(c.Lat, c.Lon, true);
        ShowMapText();
        ShowCache();
        lCity.Text = c.CityName.Length > 0 ? L.T("Знайдено: ") + c.CityName : "";
        FillClock();

        loading = false;
    }

    readonly ToolTip boundTip = new ToolTip();

    // допустимі межі числового поля: при виході за них - підказка й повернення до межі
    void Bound(SNum n, string what)
    {
        n.TextChanged += delegate
        {
            decimal v;
            if (!decimal.TryParse(n.Text, out v)) return;
            string m = null;
            if (v < n.Minimum) m = what + L.T(": менше ") + n.Minimum + L.T(" px не можна.");
            else if (v > n.Maximum) m = what + L.T(": більше ") + n.Maximum + L.T(" px не можна.");
            if (m == null) return;
            boundTip.Show(m + L.T(" Допустимо від ") + n.Minimum + L.T(" до ") + n.Maximum + " px.", n, 0, n.Height + 4, 3500);
            if (v > n.Maximum) n.Value = n.Maximum;   // завелике одразу обмежуємо; замале - коли користувач закінчить вводити
        };
        n.Leave += delegate { decimal v; if (decimal.TryParse(n.Text, out v) && v < n.Minimum) n.Value = n.Minimum; };
    }

    // Іконки й ширина залежать одна від одної; підлаштування - лише після «Застосувати», з підтвердженням.
    // false - користувач скасував (налаштування не зберігаються).
    bool CheckSizes()
    {
        double sc = owner.CurrentTheme.IconScale;
        int need = (int)Math.Ceiling(c.IconSize * sc) + 10;
        if (c.Width >= need) return true;
        Cfg cur = owner.Config;
        string msg;
        int newW = c.Width, newI = c.IconSize;
        if (c.IconSize != cur.IconSize || c.Width == cur.Width)
        {
            newW = Math.Min((int)nW.Maximum, need);
            msg = L.T("Іконки ") + c.IconSize + L.T(" px не вміщуються у ширину панелі ") + c.Width + L.T(" px.\nЗбільшити ширину панелі до ") + newW + " px?";
        }
        else
        {
            newI = Math.Max((int)nIcon.Minimum, (int)Math.Floor((c.Width - 10) / sc));
            msg = L.T("Ширина панелі ") + c.Width + L.T(" px замала для іконок ") + c.IconSize + L.T(" px.\nЗменшити іконки до ") + newI + " px?";
        }
        int r = SbDialog.Show(owner, this, L.T("Розмір панелі й іконок"), msg, L.T("Підтвердити"), L.T("Скасувати"));
        if (r != 0) return false;
        c.Width = newW; c.IconSize = newI;
        nW.Value = Math.Max(nW.Minimum, Math.Min(nW.Maximum, newW));
        nIcon.Value = Math.Max(nIcon.Minimum, Math.Min(nIcon.Maximum, newI));
        return true;
    }

    // повертає значення з елементів керування в c
    void ReadView()
    {
        c.Side = sideIds[Math.Max(0, cbSide.SelectedIndex)];
        c.Monitor = monIds[Math.Max(0, cbMonitor.SelectedIndex)];
        c.Width = (int)nW.Value;
        c.IconSize = (int)nIcon.Value;
        c.Labels = cbLabelPos.SelectedIndex != 2;
        c.LabelPos = cbLabelPos.SelectedIndex == 1 ? "above" : "below";
        c.Compact = chCompact.Checked;
        c.GroupMode = cbGroups.SelectedIndex == 1 ? "fold" : "open";
        c.HoverFx = Math.Max(0, cbFx.SelectedIndex);
        c.Anim = chAnim.Checked;
        c.DeskNoteFont = chDeskFont.Checked;
        c.LogKeep = logKeepIds[Math.Max(0, cbLogKeep.SelectedIndex)];
        bool keepChanged = LogKeep.Spec != c.LogKeep;
        LogKeep.Spec = c.LogKeep;
        if (keepChanged) LogKeep.Soon();
        c.FontName = cbFont.SelectedIndex > 0 ? (string)cbFont.SelectedItem : "";
        c.FontStyle = styleIds[Math.Max(0, cbFontStyle.SelectedIndex)];
        c.FontSize = (float)nFont.Value;

        c.Mode = cbMode.SelectedIndex == 1 ? "autohide" : "fixed";
        c.CornerGap = (int)nGap.Value;
        c.FsHide = chFs.Checked;
        c.TrayCpu = chTray[0].Checked; c.TrayGpu = chTray[1].Checked; c.TrayHot = chTray[2].Checked;
        c.TrayCpuCol = trayCol[0]; c.TrayGpuCol = trayCol[1]; c.TrayHotCol = trayCol[2];
        c.SearchSuggest = chSuggest.Checked;
        c.UpdCheck = chUpd.Checked;
        c.Hotkeys = chHot.Checked && (searchKeyOk & Keys.KeyCode) != Keys.None;
        if ((searchKeyOk & Keys.KeyCode) != Keys.None) c.SearchKey = (int)searchKeyOk;
        c.ShowSearch = chSearch.Checked;
        c.FileSearch = chFiles.Checked;
        c.AlwaysAdmin = chAdmin.Checked;
        c.AdminAuto = chAuto.Checked;
        c.FsEngine = 0;   // рушій завжди автоматичний: індекс NTFS, інакше пошук Windows

        string city = tbCity.Text.Trim();
        if (city.Length == 0) city = L.T("Київ");
        if (!string.Equals(city, c.City, StringComparison.CurrentCultureIgnoreCase) && c.LocMode != "auto")
        {
            c.Lat = double.NaN; c.Lon = double.NaN; c.CityName = ""; c.Country = "";
        }
        if (c.LocMode != "auto") c.City = city;
        c.LocAllow = chLocAllow.Checked;
        c.LocMode = rbAuto.Checked && c.LocAllow ? "auto" : "manual";
        ReadClock();

    }



    // ---------- віджети, шпалери, дані ----------

    static Entry WidgetEntry(string kind)
    {
        Entry e = new Entry();
        e.Kind = Kind.Widget;
        e.Target = kind;
        e.Name = WidgetCatalog.Title(kind);
        return e;
    }

    // «Компактна панель» можлива лише для верхньої/нижньої панелі в режимі автосховку
    void SyncCompact()
    {
        if (chCompact == null || cbMode == null) return;
        bool hor = cbSide.SelectedIndex == 2 || cbSide.SelectedIndex == 3;
        bool autohide = cbMode.SelectedIndex == 1;
        chCompact.Enabled = hor && autohide;
        if (nGap != null) nGap.Enabled = autohide;
    }

    void CityTick(object s, EventArgs e)
    {
        cityTimer.Stop();
        string q = tbCity.Text.Trim();
        if (q.Length < 3 || q == cityLast) return;
        cityLast = q;
        Util.Bg(delegate
        {
            List<string[]> list = Widgets.WeatherSuggest(q);
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (tbCity.Text.Trim() != q) return;
                    citySug = list;
                    lbCitySug.BeginUpdate();
                    lbCitySug.Items.Clear();
                    foreach (string[] it in list) lbCitySug.Items.Add(it[0]);
                    lbCitySug.EndUpdate();
                    SetCitySug(list.Count > 0);
                }));
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
        });
    }

    void CityKey(object s, KeyEventArgs e)
    {
        if (!citySugOpen) return;
        if (e.KeyCode == Keys.Down && s == tbCity)
        {
            lbCitySug.Focus();
            if (lbCitySug.Items.Count > 0 && lbCitySug.SelectedIndex < 0) lbCitySug.SelectedIndex = 0;
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Enter) { PickCitySug(); e.Handled = true; e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Escape) { SetCitySug(false); e.Handled = true; }
    }

    // підказки міст - рядок картки «Місто», що з'являється під полем пошуку
    void SetCitySug(bool open)
    {
        if (open)
        {
            int n = Math.Min(lbCitySug.Items.Count, 6);
            cityCard.Rows[cityCard.Rows.Count - 1].FullH = n * lbCitySug.ItemHeight + S(10);
        }
        if (open == citySugOpen && !open) return;
        citySugOpen = open;
        cityCard.Queue();
    }

    void PickCitySug()
    {
        int i = lbCitySug.SelectedIndex;
        if (i < 0 || i >= citySug.Count) return;
        string[] it = citySug[i];
        double lat, lon;
        double.TryParse(it[2], NumberStyles.Float, CultureInfo.InvariantCulture, out lat);
        double.TryParse(it[3], NumberStyles.Float, CultureInfo.InvariantCulture, out lon);
        cityLast = it[1];
        tbCity.Text = it[1];
        c.City = it[1]; c.CityName = it[1]; c.Lat = lat; c.Lon = lon;
        if (it.Length > 4 && it[4].Length == 2) c.Country = it[4].ToUpperInvariant();
        map.SetMark(lat, lon, true);
        ShowMapText();
        lCity.Text = L.T("Обрано: ") + it[0] + " (" + lat.ToString("0.00", CultureInfo.InvariantCulture) + ", " + lon.ToString("0.00", CultureInfo.InvariantCulture) + ")";
        SetCitySug(false);
        tbCity.Focus();
        tbCity.SelectionStart = tbCity.Text.Length;
    }

    void LookupCity()
    {
        SetCitySug(false);
        string city = tbCity.Text.Trim();
        if (city.Length == 0) return;
        lCity.Text = L.T("Пошук…");
        Util.Bg(delegate
        {
            double lat, lon; string name;
            bool ok = Widgets.WeatherLookup(city, out lat, out lon, out name);
            string ccc = ok ? Geo.CountryOfCity(city) : "";
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (ok)
                    {
                        c.City = city; c.Lat = lat; c.Lon = lon; c.CityName = name;
                        if (ccc.Length == 2) c.Country = ccc.ToUpperInvariant();
                        map.SetMark(lat, lon, true);
                        ShowMapText();
                        lCity.Text = L.T("Знайдено: ") + name + " (" + lat.ToString("0.00", CultureInfo.InvariantCulture) + ", " + lon.ToString("0.00", CultureInfo.InvariantCulture) + ")";
                    }
                    else lCity.Text = L.T("Місто не знайдено або немає інтернету.");
                }));
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
        });
    }



    void DoExport()
    {
        if (!Apply(true)) return;
        using (SaveFileDialog d = new SaveFileDialog())
        {
            d.Filter = L.T("Налаштування NeoBar (*.nbpack)|*.nbpack");
            d.FileName = "neobar-settings.nbpack";
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                int n = Store.Export(d.FileName, AppDomain.CurrentDomain.BaseDirectory, owner.CfgPath);
                lData.Text = L.T("Експортовано файлів: ") + n + ". " + d.FileName;
            }
            catch (Exception ex) { lData.Text = L.T("Помилка експорту: ") + ex.Message; }
        }
    }

    void DoImport()
    {
        using (OpenFileDialog d = new OpenFileDialog())
        {
            d.Filter = L.T("Налаштування NeoBar (*.nbpack)|*.nbpack|Усі файли|*.*");
            if (d.ShowDialog(this) != DialogResult.OK) return;
            if (SbDialog.Show(owner, this, L.T("Імпорт налаштувань"), L.T("Замінити налаштування профілю імпортованими?\nСтара копія збережеться як neobar.ini.old."), L.T("Замінити"), L.T("Скасувати")) != 0) return;
            try
            {
                int n = Store.Import(d.FileName, AppDomain.CurrentDomain.BaseDirectory, owner.CfgPath);
                ReloadFromDisk();
                owner.Reload();
                lData.Text = L.T("Імпортовано файлів: ") + n + ".";
            }
            catch (Exception ex) { lData.Text = L.T("Помилка імпорту: ") + ex.Message; }
        }
    }

    void DoReset()
    {
        if (SbDialog.Show(owner, this, L.T("Скинути налаштування"), L.T("Скинути всі налаштування й елементи профілю до початкових?\nСтара копія збережеться як neobar.ini.old."), L.T("Скинути"), L.T("Скасувати")) != 0) return;
        try
        {
            if (File.Exists(owner.CfgPath)) File.Copy(owner.CfgPath, owner.CfgPath + ".old", true);
            Store.Save(owner.CfgPath, new Cfg(), NeoBar.DefaultEntries());
            ReloadFromDisk();
            owner.Reload();
            lData.Text = L.T("Налаштування скинуто.");
        }
        catch (Exception ex) { lData.Text = L.T("Помилка: ") + ex.Message; }
    }

    void ReloadFromDisk()
    {
        c = new Cfg();
        list = new List<Entry>();
        Store.Load(owner.CfgPath, c, list);
        NormalizePaths();
        FillView();
        RefreshList(0);
    }
}
