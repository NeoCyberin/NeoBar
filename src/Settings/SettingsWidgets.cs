using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

// ======================================================================
//  Налаштування: віджети (два стовпчики з перетягуванням + параметри вибраного), ігри
// ======================================================================
sealed partial class SettingsForm
{
    readonly ToolTip wTip = new ToolTip();
    // ігри
    GameList gl;
    int acVersion;
    DateTime limMsgAt = DateTime.MinValue;

    void LimMsg(string t) { if (lLimStatus != null) { lLimStatus.Text = t; limMsgAt = DateTime.Now; } }
    // ігровий оверлей
    CheckBox chLive;
    SCombo cbOvLayout;
    Button btOvBg, btOvAc;
    SCombo cbOvCorner, cbOvTheme;
    MetricList mlist;
    SSlider tbScale, tbOpacity;
    SNum nOffX, nOffY, nOvInterval, nGraphW, nGraphH, nGraphSec;
    Label lScaleV, lOpacityV, lHkStatus, lMetricInfo;
    // головний перемикач: оверлей у іграх
    CheckBox cbGenOv;
    SegTabs ovSeg;
    SPage ovGen, ovLook;
    SHost ovMet, ovGames, ovHost;
    SCard prevCard;
    // для якої гри зараз налаштовується вигляд оверлею (null-рядок = загальний)
    sealed class LookTarget
    {
        public GameRow Row; public string Text;
        public override string ToString() { return Text; }
    }
    readonly List<SCombo> lookCombos = new List<SCombo>();
    readonly List<Button> lookResets = new List<Button>();
    LookTarget lookBase = new LookTarget(), lookCur;
    HotkeyBox hkToggle;
    OvPreviewBox preview;
    // обмежувач кадрів
    HotkeyBox hkLimit;
    Label lLimStatus;
    System.Windows.Forms.Timer ovPreview;


    // віджет можна додати на панель (батарея - лише якщо вона є)
    static bool CanUse(string kind) { return kind != "battery" || Battery.Present(); }

    // ---------- віджети в списку «Елементи» ----------
    // Віджети завжди вгорі списку (і на початку панелі): спершу ті, що на панелі (кнопка «−», порядок можна
    // міняти між собою), одразу за ними - ті, що не на панелі (кнопка «+»). Після віджетів - автоматичний
    // роздільник (лише коли є хоч один віджет), далі решта елементів. Недоступний віджет (батарея без батареї) -
    // кружок з перекресленням.

    readonly List<string> inactive = new List<string>();
    readonly List<string> dropDesk = new List<string>();     // віджети, прибрані з панелі: їхні копії на столі видаляються при «Застосувати»
    readonly List<object> rows = new List<object>();     // рядок списку: Entry або string (вид віджета не на панелі)
    int lastReal = -1;
    Point wDownAt = Point.Empty;
    string wDownKind;
    Entry eDown;                     // звичайний елемент (програма, група, роздільник), який почали тягнути
    int dropRow = -1;                // рядок, перед яким з'явиться перетягуваний елемент (для лінії-підказки)

    Entry WidgetOf(string kind)
    {
        foreach (Entry e in list) if (e.Kind == Kind.Widget && e.Target == kind) return e;
        return null;
    }

    void ComputeInactive()
    {
        inactive.Clear();
        foreach (string[] it in WidgetCatalog.Items)
            if (WidgetOf(it[0]) == null && CanUse(it[0])) inactive.Add(it[0]);
        foreach (string[] it in WidgetCatalog.Items)           // недоступні (напр. батарея на ПК) - в самому кінці
            if (WidgetOf(it[0]) == null && !CanUse(it[0])) inactive.Add(it[0]);
    }

    // рядки: активні віджети, неактивні, автороздільник, решта
    void BuildRows()
    {
        Store.Normalize(list);
        ComputeInactive();
        rows.Clear();
        int wn = Store.WidgetCount(list);
        for (int i = 0; i < wn; i++) rows.Add(list[i]);
        foreach (string k in inactive) rows.Add(k);
        for (int i = wn; i < list.Count; i++) rows.Add(list[i]);
    }

    Entry EntryAt(int row) { return row >= 0 && row < rows.Count ? rows[row] as Entry : null; }
    int ListIndexOfRow(int row) { Entry e = EntryAt(row); return e != null ? list.IndexOf(e) : -1; }
    int RowOf(object o) { return o == null ? -1 : rows.IndexOf(o); }

    // вид віджета в рядку списку ("" - рядок не віджет)
    string KindOfRow(int i)
    {
        if (i < 0 || i >= rows.Count) return "";
        string k = rows[i] as string;
        if (k != null) return k;
        Entry e = (Entry)rows[i];
        return e.Kind == Kind.Widget ? e.Target : "";
    }

    bool IsInactiveRow(int i) { return i >= 0 && i < rows.Count && rows[i] is string; }

    int FirstRestIndex() { int wn = Store.WidgetCount(list); return wn > 0 ? wn + 1 : 0; }

    // позиція вставки нового елемента: під вибраним звичайним елементом, інакше в кінці
    int InsertPos()
    {
        int li = ListIndexOfRow(lb.SelectedIndex);
        int first = FirstRestIndex();
        if (li >= first) return li + 1;
        if (lastReal >= first && lastReal < list.Count) return lastReal + 1;
        return list.Count;
    }

    static Rectangle ChipRect(Rectangle b)
    {
        int s = Math.Min(18, b.Height - 6);
        return new Rectangle(b.Right - s - 8, b.Y + (b.Height - s) / 2, s, s);
    }

    // «око» ліворуч від «−»: показувати віджет на панелі чи сховати (лише для віджетів, що є в списку)
    static Rectangle EyeRect(Rectangle b)
    {
        Rectangle c = ChipRect(b);
        int w = c.Width + 6;
        return new Rectangle(c.X - w - 6, c.Y, w, c.Height);
    }

    bool HasEye(int row) { Entry e = EntryAt(row); return e != null && e.Kind == Kind.Widget && CanUse(e.Target); }

    void ToggleOff(int row)
    {
        Entry e = EntryAt(row);
        if (e == null || e.Kind != Kind.Widget) return;
        e.Off = !e.Off;
        RefreshSel(e);
    }

    // відкрите око - видно на панелі; закрите (з віями донизу) - сховано
    static void DrawEye(Graphics g, Rectangle r, Color ink, bool open)
    {
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, hw = r.Width / 2f - 1, hh = r.Height * 0.3f;
        using (Pen p = new Pen(ink, 1.6f))
        {
            p.StartCap = System.Drawing.Drawing2D.LineCap.Round; p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            p.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
            if (open)
            {
                using (System.Drawing.Drawing2D.GraphicsPath gp = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    gp.AddBezier(cx - hw, cy, cx - hw * 0.45f, cy - hh * 1.6f, cx + hw * 0.45f, cy - hh * 1.6f, cx + hw, cy);
                    gp.AddBezier(cx + hw, cy, cx + hw * 0.45f, cy + hh * 1.6f, cx - hw * 0.45f, cy + hh * 1.6f, cx - hw, cy);
                    gp.CloseFigure();
                    g.DrawPath(p, gp);
                }
                float pr = hh * 0.75f;
                using (SolidBrush b = new SolidBrush(ink)) g.FillEllipse(b, cx - pr, cy - pr, pr * 2, pr * 2);
            }
            else
            {
                // закрита повіка - дуга донизу і три вії
                g.DrawBezier(p, cx - hw, cy - hh * 0.3f, cx - hw * 0.45f, cy + hh * 1.2f, cx + hw * 0.45f, cy + hh * 1.2f, cx + hw, cy - hh * 0.3f);
                float ly = cy + hh * 0.6f;
                g.DrawLine(p, cx, ly + 1, cx, ly + hh * 0.9f);
                g.DrawLine(p, cx - hw * 0.55f, ly - 1, cx - hw * 0.8f, ly + hh * 0.6f);
                g.DrawLine(p, cx + hw * 0.55f, ly - 1, cx + hw * 0.8f, ly + hh * 0.6f);
            }
        }
    }

    void InitListWidgets()
    {
        lb.AllowDrop = true;
        lb.DrawItem += delegate (object s, DrawItemEventArgs e) { DrawListRow(e); };
        lb.MouseDown += delegate (object s, MouseEventArgs e)
        {
            wDownKind = null;
            eDown = null;
            if (e.Button != MouseButtons.Left) return;
            int i = lb.IndexFromPoint(e.Location);
            string k = KindOfRow(i);
            Entry de = EntryAt(i);
            if (i >= 0 && k.Length == 0 && de != null && !de.Auto && de.Kind != Kind.Widget) { eDown = de; wDownAt = e.Location; return; }
            if (i < 0 || k.Length == 0) return;
            Rectangle ib = lb.GetItemRectangle(i);
            if (ChipRect(ib).Contains(e.Location)) { ToggleWidget(k); return; }
            if (HasEye(i) && EyeRect(ib).Contains(e.Location)) { ToggleOff(i); return; }
            if (CanUse(k) && !IsInactiveRow(i)) { wDownKind = k; wDownAt = e.Location; }
        };
        lb.MouseMove += delegate (object s, MouseEventArgs e)
        {
            int i = lb.IndexFromPoint(e.Location);
            string k = KindOfRow(i), tip = "";
            if (i >= 0 && k.Length > 0)
            {
                bool onChip = ChipRect(lb.GetItemRectangle(i)).Contains(e.Location);
                bool onEye = HasEye(i) && EyeRect(lb.GetItemRectangle(i)).Contains(e.Location);
                if (!CanUse(k)) tip = L.T("У цьому комп'ютері немає батареї - віджет недоступний.");
                else if (onChip) tip = IsInactiveRow(i) ? L.T("Додати на панель") : L.T("Прибрати зі списку");
                else if (onEye) tip = EntryAt(i).Off ? L.T("Показати на панелі") : L.T("Сховати з панелі (віджет лишається - напр. для робочого столу)");
                else tip = WidgetCatalog.Full(k) + (IsInactiveRow(i) ? "" : L.T("\nПеретягніть або ▲ ▼, щоб змінити порядок віджетів."));
            }
            if (wTip.GetToolTip(lb) != tip) wTip.SetToolTip(lb, tip);
            bool chipHot = i >= 0 && k.Length > 0 && CanUse(k) && (ChipRect(lb.GetItemRectangle(i)).Contains(e.Location) ||
                           HasEye(i) && EyeRect(lb.GetItemRectangle(i)).Contains(e.Location));
            lb.Cursor = chipHot ? Cursors.Hand : (i >= 0 && k.Length > 0 && CanUse(k) && !IsInactiveRow(i) ? Cursors.SizeAll : Cursors.Default);
            if (i >= 0 && k.Length == 0) { Entry me = EntryAt(i); if (me != null && !me.Auto && me.Kind != Kind.Widget) lb.Cursor = Cursors.SizeAll; }
            if ((wDownKind == null && eDown == null) || e.Button != MouseButtons.Left) return;
            Size ds = SystemInformation.DragSize;
            if (Math.Abs(e.X - wDownAt.X) < ds.Width && Math.Abs(e.Y - wDownAt.Y) < ds.Height) return;
            string dk = wDownKind; wDownKind = null;
            Entry dEnt = eDown; eDown = null;
            if (dk != null) lb.DoDragDrop("sbwidget:" + dk, DragDropEffects.Move);
            else if (dEnt != null) lb.DoDragDrop("sbentry:" + list.IndexOf(dEnt), DragDropEffects.Move);
            SetDropRow(-1);
        };
        lb.MouseUp += delegate { wDownKind = null; eDown = null; };
        lb.DragLeave += delegate { SetDropRow(-1); };
        lb.DragOver += delegate (object s, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // файли з Провідника: лінія показує, куди потрапить програма (у групу чи між елементами)
                Point fp = lb.PointToClient(new Point(e.X, e.Y));
                if (fp.Y < 18) lb.ScrollBy(-SUi.S(10));
                else if (fp.Y > lb.Height - 18) lb.ScrollBy(SUi.S(10));
                int frow;
                EntryDropIndex(fp, out frow);
                SetDropRow(frow);
                e.Effect = DragDropEffects.Copy;
                return;
            }
            string d = e.Data.GetData(DataFormats.Text) as string;
            if (d != null && d.StartsWith("sbentry:"))
            {
                Point pt = lb.PointToClient(new Point(e.X, e.Y));
                if (pt.Y < 18) lb.ScrollBy(-SUi.S(10));                                // край списку - прокрутка
                else if (pt.Y > lb.Height - 18) lb.ScrollBy(SUi.S(10));
                int row;
                EntryDropIndex(pt, out row);
                SetDropRow(row);
                e.Effect = DragDropEffects.Move;
                return;
            }
            SetDropRow(-1);
            e.Effect = d != null && d.StartsWith("sbwidget:") ? DragDropEffects.Move : DragDropEffects.None;
        };
        lb.DragDrop += delegate (object s, DragEventArgs e)
        {
            string[] fs = null;
            try { fs = e.Data.GetData(DataFormats.FileDrop) as string[]; } catch { }
            if (fs != null)
            {
                SetDropRow(-1);
                Point dp = lb.PointToClient(new Point(e.X, e.Y));
                BeginInvoke(new Action(delegate { AddFilesAt(fs, dp); }));     // Провідник чекає, поки ми приймемо файли
                return;
            }
            string d = e.Data.GetData(DataFormats.Text) as string;
            SetDropRow(-1);
            if (d != null && d.StartsWith("sbentry:")) { DropEntry(d, lb.PointToClient(new Point(e.X, e.Y))); return; }
            if (d == null || !d.StartsWith("sbwidget:")) return;
            string k = d.Substring(9);
            if (!CanUse(k)) return;
            int i = lb.IndexFromPoint(lb.PointToClient(new Point(e.X, e.Y)));
            int wn = Store.WidgetCount(list);
            // віджет стає лише серед віджетів (вгорі)
            int at = ListIndexOfRow(i);
            if (at < 0 || at > wn) at = wn;
            Entry cur = WidgetOf(k);
            if (cur != null)
            {
                int from = list.IndexOf(cur);
                list.RemoveAt(from);
                if (from < at) at--;
            }
            else cur = WidgetEntry(k);
            list.Insert(Math.Max(0, Math.Min(at, Store.WidgetCount(list))), cur);
            RefreshSel(cur);
        };
    }

    // програми, папки й ярлики з Провідника: додаються туди, куди їх кинули
    void AddFilesAt(string[] files, Point pt)
    {
        int row;
        int at = EntryDropIndex(pt, out row);
        Entry last = null;
        foreach (string f in files)
        {
            string n = Directory.Exists(f) ? Path.GetFileName(f.TrimEnd('\\')) : Path.GetFileNameWithoutExtension(f);
            if (string.IsNullOrEmpty(n)) n = f;
            Entry e = MakeEntry(Kind.Item, n, Util.Collapse(f));
            list.Insert(Math.Max(0, Math.Min(at++, list.Count)), e);
            last = e;
        }
        if (last != null) RefreshSel(last);
    }

    void SetDropRow(int r)
    {
        if (dropRow == r) return;
        dropRow = r;
        lb.Invalidate();
    }

    // куди вставити перетягуваний елемент: індекс у list і рядок, перед яким малюється лінія (rows.Count - у кінець)
    int EntryDropIndex(Point pt, out int lineRow)
    {
        int i = lb.IndexFromPoint(pt);
        if (i < 0) lineRow = rows.Count > 0 && pt.Y < lb.GetItemRectangle(0).Y ? 0 : rows.Count;     // над першим рядком або під останнім
        else
        {
            Rectangle r = lb.GetItemRectangle(i);
            lineRow = pt.Y > r.Y + r.Height / 2 ? i + 1 : i;
        }
        int first = FirstRestIndex();
        int at;
        if (lineRow >= rows.Count) at = list.Count;
        else
        {
            Entry t = rows[lineRow] as Entry;
            at = t != null ? list.IndexOf(t) : Store.WidgetCount(list);     // рядок неактивного віджета - це межа з основним списком
        }
        if (at < first) { at = first; lineRow = Math.Min(rows.Count, RowOfListIndex(first)); }
        return at;
    }

    int RowOfListIndex(int li)
    {
        if (li >= list.Count) return rows.Count;
        int r = RowOf(list[li]);
        return r < 0 ? rows.Count : r;
    }

    // перенесення програми, групи (разом з її елементами) чи роздільника
    void DropEntry(string data, Point pt)
    {
        int from;
        if (!int.TryParse(data.Substring(8), out from) || from < 0 || from >= list.Count) return;
        Entry cur = list[from];
        if (cur.Auto || cur.Kind == Kind.Widget) return;
        int row;
        int at = EntryDropIndex(pt, out row);
        // група їде разом з елементами, що йдуть за нею, до наступної групи, роздільника чи віджета
        int to = from + 1;
        if (cur.Kind == Kind.Header)
            while (to < list.Count && list[to].Kind == Kind.Item) to++;
        if (at >= from && at <= to) return;                       // кидають на саму себе
        List<Entry> block = list.GetRange(from, to - from);
        list.RemoveRange(from, to - from);
        if (at > from) at -= block.Count;
        list.InsertRange(Math.Max(0, Math.Min(at, list.Count)), block);
        RefreshSel(cur);
    }

    void DrawListRow(DrawItemEventArgs e)
    {
        DrawListRow0(e);
        if (dropRow < 0 || e.Index < 0) return;
        int y = -1;
        if (e.Index == dropRow) y = e.Bounds.Top + 1;
        else if (dropRow >= rows.Count && e.Index == rows.Count - 1) y = e.Bounds.Bottom - 2;
        if (y < 0) return;
        using (Pen p = new Pen(FormTheme.Acc, 2f)) e.Graphics.DrawLine(p, e.Bounds.X + 4, y, e.Bounds.Right - 4, y);
    }

    void DrawListRow0(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        Graphics g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Color bg = lb.BackColor, fg = lb.ForeColor;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (SolidBrush b = new SolidBrush(bg)) g.FillRectangle(b, e.Bounds);
        string text = lb.Items[e.Index].ToString();
        TextFormatFlags tf = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        Entry ae = EntryAt(e.Index);
        if (ae != null && ae.Auto)
        {
            // автоматичний роздільник після віджетів
            int cy = e.Bounds.Y + e.Bounds.Height / 2;
            using (Pen p = new Pen(FormTheme.Line)) g.DrawLine(p, e.Bounds.X + 6, cy, e.Bounds.Right - 6, cy);
            Size ts = TextRenderer.MeasureText(text, lb.Font);
            Rectangle tr = new Rectangle(e.Bounds.X + (e.Bounds.Width - ts.Width) / 2 - 6, e.Bounds.Y, ts.Width + 12, e.Bounds.Height);
            using (SolidBrush b = new SolidBrush(bg)) g.FillRectangle(b, tr);
            TextRenderer.DrawText(g, text, lb.Font, tr, FormTheme.Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return;
        }
        if (sel)
            using (SolidBrush b = new SolidBrush(Util.Blend(bg, FormTheme.Acc, 0.35f)))
                Util.FillRound(g, b, Rectangle.Inflate(e.Bounds, -3, -2), 5);
        string kind = KindOfRow(e.Index);
        bool inact = IsInactiveRow(e.Index);
        bool widget = kind.Length > 0;
        bool use = !widget || CanUse(kind);
        bool eye = HasEye(e.Index);
        bool off = eye && ae.Off;
        Color tc = (inact || !use || off) ? Util.Blend(bg, fg, 0.5f) : fg;
        int right = widget ? (eye ? 66 : 36) : 6;
        if (off) text += L.T("  · схований");
        TextRenderer.DrawText(g, inact ? "◇ " + (text.StartsWith("◆ ") ? text.Substring(2) : text) : text, lb.Font, new Rectangle(e.Bounds.X + (inact ? 8 : 2), e.Bounds.Y, e.Bounds.Width - right - (inact ? 8 : 2), e.Bounds.Height), tc, tf);
        if (!widget) return;
        if (eye) DrawEye(g, EyeRect(e.Bounds), off ? Util.Blend(bg, fg, 0.45f) : FormTheme.Sub, !off);
        Rectangle c = ChipRect(e.Bounds);
        Color ink = !use ? Util.Blend(bg, fg, 0.4f) : (inact ? FormTheme.Good : FormTheme.Sub);
        using (Pen p = new Pen(ink, 1.6f))
        {
            p.StartCap = System.Drawing.Drawing2D.LineCap.Round; p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            g.DrawEllipse(p, c.X, c.Y, c.Width - 1, c.Height - 1);
            float cx = c.X + c.Width / 2f, cy = c.Y + c.Height / 2f, q = c.Width * 0.24f;
            if (!use) g.DrawLine(p, c.X + c.Width * 0.22f, c.Bottom - c.Height * 0.24f, c.Right - c.Width * 0.22f, c.Y + c.Height * 0.24f);   // кружок з перекресленням
            else
            {
                g.DrawLine(p, cx - q, cy, cx + q, cy);
                if (inact) g.DrawLine(p, cx, cy - q, cx, cy + q);
            }
        }
    }

    // «+» - увімкнути (стає останнім серед віджетів на панелі), «−» - вимкнути
    void ToggleWidget(string kind)
    {
        if (kind.Length == 0) return;
        Entry cur = WidgetOf(kind);
        if (cur != null)
        {
            list.Remove(cur);
            if (!dropDesk.Contains(kind)) dropDesk.Add(kind);
            RefreshSel(kind);
            return;
        }
        if (!CanUse(kind)) return;
        dropDesk.Remove(kind);
        Entry ne = WidgetEntry(kind);
        list.Insert(Store.WidgetCount(list), ne);
        RefreshSel(ne);
    }

    // значення, що зберігаються поза neobar.ini (оверлей, ігри)
    void FillStatics()
    {
        FillOverlay();
        FillWall();
    }

    // all - «Готово»: застосувати все; інакше («Застосувати») - лише оверлей і шпалери з їхніх вкладок
    void ApplyStatics(bool all)
    {
        if (all || OnPage(PgOverlay))
        {
            ApplyOverlay();
            SaveGames();
            // вигляд для гри, що зараз на екрані (загальний або власний)
            bool ed = OverlayCfg.Editing; OverlayCfg.Editing = false;
            try { OverlayCfg.UseGame(Games.Current); } catch { }
            OverlayCfg.Editing = ed;
        }
        if (all || OnPage(PgWall)) ApplyWall(all);
        // повторне збереження з урахуванням ігор і оверлея
        try { Store.Save(owner.CfgPath, c, list); } catch { }
    }

    // ---------- Ігри ----------

    // ================= Ігровий оверлей (окремий модуль) =================
    // Вкладки «Загальне» / «Показники» / «Ігри»; праворуч від перших двох - живий перегляд оверлею
    Control BuildOverlayTab()
    {
        SHost h = new SHost();
        ovHost = h;
        ovSeg = new SegTabs();
        ovSeg.Items.Add(L.T("Загальне"));
        ovSeg.Items.Add(L.T("Вигляд"));
        ovSeg.Items.Add(L.T("Показники"));
        ovSeg.Items.Add(L.T("Ігри"));
        h.Controls.Add(ovSeg);
        ovGen = new SPage();
        BuildOvGeneral(ovGen);
        ovLook = new SPage();
        LookRow(ovLook.Card(L.T("Для якої гри")));
        BuildOvLook(ovLook);
        h.Controls.Add(ovLook);
        ovMet = new SHost();
        BuildOvMetrics(ovMet);
        ovGames = new SHost();
        BuildGames(ovGames);
        h.Controls.Add(ovGen); h.Controls.Add(ovMet); h.Controls.Add(ovGames);

        // великий перегляд праворуч: масштаб, перетягування, реальні дані
        prevCard = new SCard();
        h.Controls.Add(prevCard);
        Label pt = new Label(); pt.AutoSize = false; pt.Text = L.T("Перегляд"); pt.Font = SUi.Head; pt.BackColor = Color.Transparent; pt.ForeColor = FormTheme.Fg;
        Label ph = Note(); ph.Text = L.T("Коліщатко - масштаб, перетягування мишею - положення"); ph.BackColor = Color.Transparent;
        preview = new OvPreviewBox();
        preview.BaseScale = owner.Dpi;
        preview.ViewChanged += UpdateZoomLabel;
        Button zMinus = Bt("", "ghost g:minus", 36, delegate { preview.ZoomBy(1f / 1.25f); UpdateZoomLabel(); });
        Button zPlus = Bt("", "ghost g:plus", 36, delegate { preview.ZoomBy(1.25f); UpdateZoomLabel(); });
        Button zReset = Bt("", "ghost g:reset", 36, delegate { preview.ResetView(); UpdateZoomLabel(); });
        ToolTip ztt = new ToolTip();
        ztt.SetToolTip(zMinus, L.T("Зменшити")); ztt.SetToolTip(zPlus, L.T("Збільшити")); ztt.SetToolTip(zReset, L.T("Скинути масштаб і положення"));
        chLive = new SToggle();
        chLive.AutoSize = false;
        chLive.Text = L.T("Реальні дані");
        chLive.Size = new Size(S(160), S(28));
        chLive.Checked = true;
        chLive.CheckedChanged += delegate { preview.Live = chLive.Checked; RefreshPreview(); };
        Label pn = Note(); pn.Text = L.T("Без запущеної гри показники FPS порожні - вимкніть «Реальні дані», щоб побачити приклад."); pn.BackColor = Color.Transparent;
        foreach (Control x in new Control[] { pt, ph, preview, zMinus, zPlus, zReset, chLive, pn }) prevCard.Controls.Add(x);
        prevCard.Arrange = delegate (Rectangle r)
        {
            int p = S(16), w = r.Width - p * 2;
            pt.SetBounds(p, p - S(2), w, S(24));
            ph.SetBounds(p, p + S(22), w, S(18));
            int by = r.Height - p - S(36) - S(40);
            preview.SetBounds(p, p + S(48), w, Math.Max(S(80), by - S(10) - (p + S(48))));
            zMinus.SetBounds(p, by, zMinus.Width, S(32));
            zPlus.SetBounds(zMinus.Right + S(4), by, zPlus.Width, S(32));
            zReset.SetBounds(zPlus.Right + S(4), by, zReset.Width, S(32));
            chLive.SetBounds(r.Width - p - chLive.Width, by + S(2), chLive.Width, chLive.Height);
            pn.SetBounds(p, by + S(40), w, S(36));
        };

        h.Arrange = delegate (Rectangle r)
        {
            ovSeg.SetBounds(0, 0, ovSeg.PreferredW, S(38));
            int top = S(52), hh = Math.Max(S(60), r.Height - top);
            bool games = ovSeg.SelectedIndex == 3;
            prevCard.Visible = !games;
            if (games) ovGames.SetBounds(0, top, r.Width, hh);
            else
            {
                int pw = Math.Max(S(330), Math.Min(S(420), r.Width * 38 / 100));
                prevCard.SetBounds(r.Width - pw, top, pw, hh);
                prevCard.Arrange(new Rectangle(0, 0, pw, hh));
                int si = ovSeg.SelectedIndex;
                Control cur = si == 0 ? (Control)ovGen : (si == 1 ? (Control)ovLook : ovMet);
                cur.SetBounds(0, top, r.Width - pw - S(14), hh);
            }
        };
        ovSeg.SelectedIndex = 0;
        ovSeg.SelectedChanged += delegate { ShowOvPage(); };
        ShowOvPage();
        return h;
    }

    void ShowOvPage()
    {
        int i = ovSeg.SelectedIndex;
        ovGen.Visible = i == 0;
        ovLook.Visible = i == 1;
        ovMet.Visible = i == 2;
        ovGames.Visible = i == 3;
        RefreshLookTargets();
        ovHost.Relayout();
    }

    void UpdateZoomLabel() { }

    // «Загальне»: головний перемикач, гарячі клавіші, вигляд
    void BuildOvGeneral(SPage p)
    {
        SCard k = p.Card(L.T("Оверлей"));
        cbGenOv = Tg();
        k.Row(L.T("Показувати оверлей у іграх"), L.T("Головний перемикач для всіх ігор зі списку. Оверлей, інтеграцію в гру й ліміт кадрів для кожної гри окремо - у вкладці «Ігри»."), cbGenOv);
        cbGenOv.CheckedChanged += delegate
        {
            if (loading) return;
            if (gl != null) { gl.Invalidate(); gl.Relayout2(); }
        };

        k = p.Card(L.T("Гарячі клавіші"));
        hkToggle = new HotkeyBox();
        hkToggle.BorderStyle = BorderStyle.None;
        k.Want(hkToggle, S(190));
        Button c1 = Bt("", "ghost g:close", 34, delegate { hkToggle.SetValue(Keys.None); });
        k.Row(L.T("Показати / сховати оверлей"), L.T("Працює навіть коли гра у фокусі. Клацніть у поле й натисніть комбінацію"), hkToggle, c1);
        hkLimit = new HotkeyBox();
        hkLimit.BorderStyle = BorderStyle.None;
        k.Want(hkLimit, S(190));
        Button c2 = Bt("", "ghost g:close", 34, delegate { hkLimit.SetValue(Keys.None); });
        k.Row(L.T("Ліміт кадрів увімк. / вимк."), L.T("Для ігор, де ліміт кадрів увімкнено у вкладці «Ігри»"), hkLimit, c2);
        lHkStatus = Note();
        lHkStatus.ForeColor = FormTheme.Warn;
        k.NoteRow(lHkStatus);
        ToolTip tt = new ToolTip();
        tt.SetToolTip(c1, L.T("Очистити")); tt.SetToolTip(c2, L.T("Очистити"));
    }

    void FillGeneral()
    {
        bool old = loading; loading = true;
        Games.Load();
        cbGenOv.Checked = Games.GenOv;
        loading = old;
    }


    // вибір, для чого налаштовується вигляд (усі ігри / окрема гра з власним виглядом)
    void LookRow(SCard k)
    {
        SCombo cb = Cb(260);
        cb.SelectedIndexChanged += LookTargetChanged;
        lookCombos.Add(cb);
        Button rs = Bt(L.T("Як загальний"), "g:reset", 150, delegate { ResetOwnLook(); });
        lookResets.Add(rs);
        k.Row(L.T("Налаштовується вигляд для"), L.T("Загальний вигляд для всіх ігор або власний вигляд окремої гри (вмикається у вкладці «Ігри»)"), cb, rs);
    }

    void BuildOvMetrics(SHost h)
    {
        SCard look = new SCard();
        h.Controls.Add(look);
        LookRow(look);
        SCard lc = new SCard();
        h.Controls.Add(lc);
        Label lt = new Label(); lt.AutoSize = false; lt.Text = L.T("Показники й графіки"); lt.Font = SUi.Head; lt.BackColor = Color.Transparent; lt.ForeColor = FormTheme.Fg;
        Label ls = Note(); ls.BackColor = Color.Transparent;
        ls.Text = L.T("Перемикач - показувати в оверлеї, «Графік» - графік просто під показником. Порядок - перетягуванням рядка або кнопками ▲ ▼ (також Ctrl+стрілки).");
        mlist = new MetricList();
        mlist.Changed += delegate { if (!loading) RefreshPreview(); };
        mlist.SelChanged += delegate { lMetricInfo.Text = mlist.Selected < 0 ? "" : OverlayCfg.Describe(mlist.Rows[mlist.Selected].Key); };
        Button up = Bt("", "ghost g:up", 34, delegate { mlist.MoveSel(-1); });
        Button down = Bt("", "ghost g:down", 34, delegate { mlist.MoveSel(1); });
        Button all = Bt(L.T("Усі"), "ghost g:check", 84, delegate { mlist.SetAll(true); });
        Button none = Bt(L.T("Жодного"), "ghost g:close", 104, delegate { mlist.SetAll(false); });
        Button def = Bt(L.T("За замовчуванням"), "ghost g:reset", 170, delegate { OverlayCfg.ResetMetrics(); FillMetrics(); RefreshPreview(); });
        lMetricInfo = Note(); lMetricInfo.BackColor = Color.Transparent;
        foreach (Control x in new Control[] { lt, ls, mlist, up, down, all, none, def, lMetricInfo }) lc.Controls.Add(x);
        lc.Arrange = delegate (Rectangle r)
        {
            int p = S(16), w = r.Width - p * 2;
            lt.SetBounds(p, p - S(2), w, S(24));
            ls.SetBounds(p, p + S(22), w, SUi.TextH(ls.Text, ls.Font, w) + S(2));
            int ty = ls.Bottom + S(10), bh = S(32);
            up.SetBounds(p, ty, up.Width, bh);
            down.SetBounds(up.Right + S(4), ty, down.Width, bh);
            def.SetBounds(r.Width - p - def.Width, ty, def.Width, bh);
            none.SetBounds(def.Left - S(4) - none.Width, ty, none.Width, bh);
            all.SetBounds(none.Left - S(4) - all.Width, ty, all.Width, bh);
            int ly = ty + bh + S(8), ih = S(44);
            mlist.SetBounds(S(6), ly, r.Width - S(12), Math.Max(S(80), r.Height - ly - ih - p));
            lMetricInfo.SetBounds(p, mlist.Bottom + S(6), w, ih);
        };
        h.Arrange = delegate (Rectangle r)
        {
            int lh = look.LayoutRows(r.Width);
            look.SetBounds(0, 0, r.Width, lh);
            lc.SetBounds(0, lh + S(12), r.Width, Math.Max(S(120), r.Height - lh - S(12)));
            lc.Arrange(new Rectangle(0, 0, lc.Width, lc.Height));
        };
        look.NeedLayout += delegate { h.Relayout(); };
    }

    void BuildOvLook(SPage p)
    {
        SCard k = p.Card(L.T("Розташування"));
        cbOvCorner = Cb(220, L.T("↖ Ліворуч угорі"), L.T("↗ Праворуч угорі"), L.T("↙ Ліворуч унизу"), L.T("↘ Праворуч унизу"));
        cbOvCorner.SelectedIndexChanged += delegate { if (!loading) RefreshPreview(); };
        k.Row(L.T("Кут екрана"), "", cbOvCorner);
        cbOvLayout = Cb(270, L.T("Вертикально (стовпчик)"), L.T("Горизонтально (рядки по ширині екрана)"));
        cbOvLayout.SelectedIndexChanged += delegate { if (!loading) RefreshPreview(); };
        k.Row(L.T("Розкладка"), "", cbOvLayout);
        nOffX = Nm(86, 0, 2000);
        nOffY = Nm(86, 0, 2000);
        Label lx = Val(16); lx.Text = "X"; lx.TextAlign = ContentAlignment.MiddleRight;
        Label ly = Val(22); ly.Text = "Y"; ly.TextAlign = ContentAlignment.MiddleRight;
        k.Row(L.T("Відступ від краю"), L.T("Пікселі від вибраного кута екрана"), lx, nOffX, ly, nOffY);

        k = p.Card(L.T("Розмір і прозорість"));
        tbScale = Tk(220, 50, 250); tbScale.TickFrequency = 25; tbScale.SmallChange = 5; tbScale.LargeChange = 25;
        lScaleV = Val(56);
        tbScale.ValueChanged += delegate { lScaleV.Text = tbScale.Value + " %"; if (!loading) RefreshPreview(); };
        k.Row(L.T("Масштаб"), "", tbScale, lScaleV);
        tbOpacity = Tk(220, 0, 100); tbOpacity.TickFrequency = 10; tbOpacity.SmallChange = 5; tbOpacity.LargeChange = 10;
        lOpacityV = Val(56);
        tbOpacity.ValueChanged += delegate { lOpacityV.Text = tbOpacity.Value + " %"; if (!loading) RefreshPreview(); };
        k.Row(L.T("Прозорість фону"), L.T("0% - лише текст, без підкладки"), tbOpacity, lOpacityV);

        k = p.Card(L.T("Оновлення й графіки"));
        nOvInterval = Nm(96, 100, 2000); nOvInterval.Increment = 50;
        k.Row(L.T("Оновлювати кожні"), L.T("Мілісекунд; рідше оновлення - менше навантаження"), nOvInterval);
        nGraphW = Nm(86, 60, 600);
        nGraphH = Nm(86, 16, 200);
        Label lxx = Val(18); lxx.Text = "×"; lxx.TextAlign = ContentAlignment.MiddleCenter;
        k.Row(L.T("Розмір графіка"), L.T("Ширина × висота, px"), nGraphW, lxx, nGraphH);
        nGraphSec = Nm(86, 5, 300);
        k.Row(L.T("Період графіка"), L.T("Скільки секунд історії показує графік"), nGraphSec);
        foreach (SNum n in new[] { nOffX, nOffY, nOvInterval, nGraphW, nGraphH, nGraphSec }) n.ValueChanged += delegate { if (!loading) RefreshPreview(); };

        k = p.Card(L.T("Тема оверлею"));
        cbOvTheme = Cb(220);
        cbOvTheme.Items.AddRange(OvTheme.Names);
        cbOvTheme.SelectedIndexChanged += delegate { UpdateOvThemeUi(); if (!loading) RefreshPreview(); };
        k.Row(L.T("Оформлення"), "", cbOvTheme);
        btOvBg = Bt(L.T("Фон…"), "g:palette", 110, delegate { PickThemeColor(true); });
        btOvAc = Bt(L.T("Акцент…"), "g:palette", 124, delegate { PickThemeColor(false); });
        k.Row(L.T("Власні кольори"), L.T("Лише для теми «Власна»"), btOvBg, btOvAc);
        UpdateOvThemeUi();
    }

    // кольори фону й акценту доступні лише для власної теми
    void UpdateOvThemeUi()
    {
        bool custom = cbOvTheme != null && cbOvTheme.SelectedIndex == OvTheme.Custom;
        if (btOvBg != null) btOvBg.Enabled = custom;
        if (btOvAc != null) btOvAc.Enabled = custom;
    }

    void PickThemeColor(bool bg)
    {
        using (ColorDialog d = new ColorDialog())
        {
            d.FullOpen = true;
            d.Color = Color.FromArgb(bg ? OverlayCfg.CustomBg : OverlayCfg.CustomAccent);
            if (d.ShowDialog(this) != DialogResult.OK) return;
            if (bg) OverlayCfg.CustomBg = d.Color.ToArgb(); else OverlayCfg.CustomAccent = d.Color.ToArgb();
            cbOvTheme.SelectedIndex = OvTheme.Names.Length - 1;
            RefreshPreview();
        }
    }

    // ---------- показники ----------
    void FillMetrics()
    {
        bool old = loading; loading = true;
        List<MetricList.Row> l = new List<MetricList.Row>();
        foreach (OvMetric m in OverlayCfg.Metrics)
        {
            string[] inf = OverlayCfg.Info(m.Key);
            if (inf == null) continue;
            MetricList.Row r = new MetricList.Row();
            r.Key = m.Key; r.Text = inf[1]; r.Show = m.Show;
            r.CanGraph = OverlayCfg.CanGraph(m.Key);
            r.Graph = m.Graph && r.CanGraph;
            l.Add(r);
        }
        mlist.SetRows(l);
        lMetricInfo.Text = mlist.Selected < 0 ? L.T("Виберіть показник, щоб побачити, що він показує.") : OverlayCfg.Describe(mlist.Rows[mlist.Selected].Key);
        loading = old;
    }

    // показники зі списку - у OverlayCfg.Metrics (порядок, показ, графік)
    void MetricsToCfg()
    {
        if (mlist == null || mlist.Rows.Count == 0) return;
        List<OvMetric> l = new List<OvMetric>();
        foreach (MetricList.Row r in mlist.Rows) l.Add(new OvMetric(r.Key, r.Show, r.Graph && r.CanGraph));
        OverlayCfg.Metrics.Clear();
        OverlayCfg.Metrics.AddRange(l);
    }

    void RefreshPreview()
    {
        if (preview == null || cbOvCorner == null) return;
        MetricsToCfg();
        PushLookToCfg();
        try { OvTheme.Panel = Themes.Get(c.ThemeId, c); } catch { }
        try { preview.Live = chLive == null || chLive.Checked; preview.Rebuild(); UpdateZoomLabel(); } catch { }
    }

    // перенести контроли вигляду в OverlayCfg (для перегляду; фінально - в ApplyOverlay)
    void PushLookToCfg()
    {
        if (cbOvCorner == null) return;
        OverlayCfg.Corner = Math.Max(0, cbOvCorner.SelectedIndex);
        OverlayCfg.Scale = tbScale.Value;
        OverlayCfg.Opacity = tbOpacity.Value;
        OverlayCfg.Horizontal = cbOvLayout.SelectedIndex == 1;
        OverlayCfg.GraphW = (int)nGraphW.Value;
        OverlayCfg.GraphH = (int)nGraphH.Value;
        OverlayCfg.GraphSec = (int)nGraphSec.Value;
        OverlayCfg.Theme = Math.Max(0, cbOvTheme.SelectedIndex);
        OverlayCfg.OffX = (int)nOffX.Value;
        OverlayCfg.OffY = (int)nOffY.Value;
        OverlayCfg.Interval = (int)nOvInterval.Value;
    }

    // ---------- вигляд: загальний або окремої гри ----------
    void RefreshLookTargets()
    {
        if (lookCombos.Count == 0 || gl == null) return;
        bool old = loading; loading = true;
        List<LookTarget> items = new List<LookTarget>();
        lookBase.Row = null; lookBase.Text = L.T("Усі ігри (загальний вигляд)");
        items.Add(lookBase);
        LookTarget keep = lookBase;
        foreach (GameRow r in gl.Rows)
        {
            if (r.Look.Length == 0) continue;
            LookTarget lt = lookCur != null && lookCur.Row == r ? lookCur : new LookTarget();
            lt.Row = r; lt.Text = r.G.Name;
            items.Add(lt);
            if (lookCur != null && lookCur.Row == r) keep = lt;
        }
        bool lost = lookCur != null && lookCur.Row != null && keep == lookBase;     // гра більше не має власного вигляду
        foreach (SCombo cb in lookCombos)
        {
            cb.BeginUpdate();
            cb.Items.Clear();
            foreach (LookTarget lt in items) cb.Items.Add(lt);
            cb.SelectedItem = keep;
            cb.EndUpdate();
        }
        loading = old;
        if (lost) SwitchLook(lookBase, false);
        else { lookCur = keep; UpdateLookButtons(); }
    }

    void UpdateLookButtons()
    {
        bool own = lookCur != null && lookCur.Row != null;
        foreach (Button b in lookResets) b.Enabled = own;
    }

    void LookTargetChanged(object sender, EventArgs e)
    {
        if (loading) return;
        SCombo cb = (SCombo)sender;
        LookTarget to = cb.SelectedItem as LookTarget;
        if (to == null || to == lookCur) return;
        SwitchLook(to, true);
    }

    void SwitchLook(LookTarget to, bool store)
    {
        if (store) StoreLook();
        lookCur = to;
        LookProfile p = to.Row == null ? OverlayCfg.Base : (to.Row.Look.Length > 0 ? LookProfile.Decode(to.Row.Look, OverlayCfg.Base) : OverlayCfg.Base.Clone());
        OverlayCfg.Apply(p);
        FillLookControls();
        bool old = loading; loading = true;
        foreach (SCombo cb in lookCombos) cb.SelectedItem = to;
        loading = old;
        UpdateLookButtons();
        RefreshPreview();
    }

    // контроли вигляду -> загальний вигляд або вигляд вибраної гри
    void StoreLook()
    {
        if (cbOvCorner == null || mlist == null) return;
        MetricsToCfg();
        PushLookToCfg();
        LookProfile cur = OverlayCfg.Capture();
        if (lookCur == null || lookCur.Row == null) { OverlayCfg.Base = cur; return; }
        lookCur.Row.Look = cur.Encode();
    }

    void ResetOwnLook()
    {
        if (lookCur == null || lookCur.Row == null) return;
        lookCur.Row.Look = "";
        RefreshLookTargets();          // гра більше не має власного вигляду: повертаємось до загального
    }

    // з кнопки в списку ігор: перейти до вигляду цієї гри
    void OpenLookFor(GameRow r)
    {
        RefreshLookTargets();
        foreach (object o in lookCombos[0].Items)
        {
            LookTarget lt = (LookTarget)o;
            if (lt.Row == r) { if (lt != lookCur) SwitchLook(lt, true); break; }
        }
        if (ovSeg != null) ovSeg.SelectedIndex = 1;
    }

    // контроли вигляду й показників беруть значення з OverlayCfg (загальних або вибраної гри)
    void FillLookControls()
    {
        bool old = loading; loading = true;
        cbOvTheme.SelectedIndex = Math.Max(0, Math.Min(OvTheme.Names.Length - 1, OverlayCfg.Theme));
        cbOvCorner.SelectedIndex = OverlayCfg.Corner;
        nOffX.Value = Math.Max(nOffX.Minimum, Math.Min(nOffX.Maximum, OverlayCfg.OffX));
        nOffY.Value = Math.Max(nOffY.Minimum, Math.Min(nOffY.Maximum, OverlayCfg.OffY));
        tbScale.Value = Math.Max(tbScale.Minimum, Math.Min(tbScale.Maximum, OverlayCfg.Scale));
        tbOpacity.Value = Math.Max(0, Math.Min(100, OverlayCfg.Opacity));
        lScaleV.Text = tbScale.Value + " %"; lOpacityV.Text = tbOpacity.Value + " %";
        cbOvLayout.SelectedIndex = OverlayCfg.Horizontal ? 1 : 0;
        nOvInterval.Value = Math.Max(100, Math.Min(2000, OverlayCfg.Interval));
        nGraphW.Value = Math.Max(nGraphW.Minimum, Math.Min(nGraphW.Maximum, OverlayCfg.GraphW));
        nGraphH.Value = Math.Max(nGraphH.Minimum, Math.Min(nGraphH.Maximum, OverlayCfg.GraphH));
        nGraphSec.Value = Math.Max(nGraphSec.Minimum, Math.Min(nGraphSec.Maximum, OverlayCfg.GraphSec));
        UpdateOvThemeUi();
        FillMetrics();
        loading = old;
    }

    void FillOverlay()
    {
        bool old = loading; loading = true;
        OverlayCfg.Editing = true;
        OverlayCfg.Load();
        lookCur = lookBase;
        FillLookControls();
        hkToggle.SetValue(OverlayCfg.KeyToggle);
        hkLimit.SetValue(OverlayCfg.KeyLimit);
        FillGames();
        FillGeneral();
        RefreshLookTargets();
        loading = old;
        RefreshPreview();
        if (ovPreview == null)
        {
            ovPreview = new System.Windows.Forms.Timer();
            ovPreview.Interval = 500;
            ovPreview.Tick += delegate
            {
                bool on = OnPage(PgOverlay);
                bool live = on && chLive != null && chLive.Checked;
                // датчики працюють на повну, поки відкрито живий перегляд
                Sensors.Use("ovpreview", live ? 2 : 0);
                if (live) Sensors.SetInterval("ovpreview", 500);
                if (on)
                {
                    if (lHkStatus != null) lHkStatus.Text = OverlayModule.HotkeyError.Length > 0 ? L.T("Увага: ") + OverlayModule.HotkeyError : "";
                    if (lLimStatus != null && (DateTime.Now - limMsgAt).TotalSeconds > 6) lLimStatus.Text = FpsLimiter.Status();
                    RefreshAntiCheat();
                    RefreshGameInfo();
                    if (live) RefreshPreview();
                }
            };
            ovPreview.Start();
        }
    }

    void ApplyOverlay()
    {
        if (cbOvCorner == null) return;
        OverlayCfg.On = true;
        OverlayCfg.Mode = 0;
        StoreLook();                                  // вигляд вибраної цілі (загальний або гри) - у Base / у рядок гри
        OverlayCfg.Apply(OverlayCfg.Base);            // в overlay.ini йде тільки загальний вигляд
        OverlayCfg.KeyToggle = hkToggle.Value;
        OverlayCfg.KeyLimit = hkLimit.Value;
        OverlayCfg.Save();
        OverlayModule.Apply();
    }

    void OverlayClosed()
    {
        try { if (ovPreview != null) { ovPreview.Stop(); ovPreview.Dispose(); ovPreview = null; } } catch { }
        // скасовані правки не лишаються: загальний вигляд - знову з файлу, у грі - вигляд поточної гри
        try { OverlayCfg.Editing = false; OverlayCfg.Load(); OverlayCfg.UseGame(Games.Current); } catch { }
        try { Sensors.Use("ovpreview", 0); } catch { }
    }

    // ---------- ігри: оверлей і обмеження кадрів для кожної ----------
    // Стан рядків тримається в GameRow до «Застосувати» (скасування не змінює ігри)
    void BuildGames(SHost h)
    {
        SCard top = new SCard();
        h.Controls.Add(top);
        Button bFind = Bt(L.T("Знайти ігри"), "g:search", 150, delegate
        {
            SaveGames();
            int n = Games.Scan();
            FillGames();
            LimMsg(n > 0 ? L.T("Додано нових ігор: ") + n : L.T("Нових ігор не знайдено."));
        });
        Button bAdd = Bt(L.T("Додати гру…"), "g:plus", 150, delegate
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = L.T("Файл гри (.exe)");
                d.Filter = L.T("Програми|*.exe");
                if (d.ShowDialog(this) != DialogResult.OK) return;
                SaveGames();
                Games.AddManual(d.FileName);
                FillGames();
            }
        });
        Button bRem = Bt(L.T("Прибрати зі списку"), "danger g:trash", 190, delegate
        {
            if (gl.Sel < 0 || gl.Sel >= gl.Rows.Count) { LimMsg(L.T("Спершу клацніть гру в списку.")); return; }
            GameRow r = gl.Rows[gl.Sel];
            SaveGames();
            Games.List.Remove(r.G);
            Games.Save();
            FillGames();
        });
        Button bGoneG = Bt(L.T("Прибрати зниклі"), "ghost g:trash", 190, delegate { RemoveGoneGames(); });
        SRow tr = top.Stacked(L.T("Ігри"), L.T("Клацніть гру, щоб розгорнути її налаштування. Оверлей увімкнено в усіх іграх. «Інтеграція в гру» (хуки, ліміт кадрів, показ у повноекрані) за замовчуванням увімкнена в іграх без античіта й вимкнена в іграх з ним. Ліміт кадрів вимкнено в усіх. «Власний вигляд оверлею» дає грі свій вигляд і набір показників."), bFind, bAdd, bRem, bGoneG);
        tr.Flex = -1;
        lLimStatus = Note();
        lLimStatus.ForeColor = FormTheme.Acc;
        top.WithNote(tr, lLimStatus);

        gl = new GameList();
        h.Controls.Add(gl);
        gl.OpenLook = OpenLookFor;
        gl.Changed += delegate { RefreshLookTargets(); };
        gl.MonitorRates = delegate (GameRow r) { return FpsLimiter.MonitorRates(r.MonHz); };
        gl.MonitorFromGame = delegate (GameRow r) { return FpsLimiter.GameMonitorKnown(r.MonHz); };
        gl.ConfirmAc = ConfirmAntiCheat;
        h.Arrange = delegate (Rectangle r)
        {
            int th = top.LayoutRows(r.Width);
            top.SetBounds(0, 0, r.Width, th);
            gl.SetBounds(0, th + S(12), r.Width, Math.Max(S(80), r.Height - th - S(12)));
        };
        top.NeedLayout += delegate { h.Relayout(); };
    }

    // видалені ігри (теки вже немає) лишаються в списку, поки користувач сам їх не прибере
    void RemoveGoneGames()
    {
        SaveGames();
        List<Game> gone = new List<Game>();
        foreach (Game g in Games.List) if (Games.IsGone(g)) gone.Add(g);
        if (gone.Count == 0) { LimMsg(L.T("Зниклих ігор немає.")); return; }
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < gone.Count && i < 8; i++) sb.Append("\n• ").Append(gone[i].Name);
        if (gone.Count > 8) sb.Append("\n…");
        int k = SbDialog.Show(owner, this, L.T("Прибрати зниклі"),
            L.F("Буде прибрано ігор зі списку: {0}. Це ті, чиїх тек уже немає на диску. Самі файли не чіпаються, а налаштування цих ігор буде втрачено.", gone.Count) + "\n" + sb.ToString(),
            L.T("Прибрати"), L.T("Скасувати"));
        if (k != 0) return;
        foreach (Game g in gone) Games.List.Remove(g);
        Games.Save();
        FillGames();
    }

    // «Інтеграція в гру» для гри з античітом: попередження про ризик; вибір за користувачем
    bool ConfirmAntiCheat(GameRow r)
    {
        string msg = L.T("У «") + r.G.Name + L.T("» знайдено: ") + r.Ac + ".\n\n" +
                     L.T("Інтеграція вставляє бібліотеку NeoBar у процес гри (хуки): ліміт кадрів, підміна повноекрану, показ у кадрі. Античіт може сприйняти це як втручання, і в мережевій грі це здатне призвести до блокування акаунта.\n\n") +
                     L.T("Без інтеграції оверлей працює як звичайне вікно поверх гри (режим «Вікно без рамки») і нічого в гру не впроваджується. Якщо ви граєте лише в одиночному режимі або на локальному сервері, ризику немає.");
        int k = SbDialog.Show(owner, this, L.T("Увімкнути інтеграцію в цій грі?"), msg, L.T("Усе одно увімкнути"), L.T("Скасувати"));
        return k == 0;
    }

    void FillGames()
    {
        Games.Load();
        List<GameRow> rows = new List<GameRow>();
        foreach (Game g in Games.List)
        {
            GameRow r = new GameRow();
            r.G = g;
            r.Ov = g.OvOwn;
            r.IntRaw = g.IntRaw;
            r.Lim = g.LimitOn;
            r.Api = g.Api;
            r.Fps = g.LimFps;
            r.Look = g.LookEnc ?? "";
            r.MonHz = g.MonHz;
            r.Mode = g.ModeSel == 0 || g.ModeSel == 1 ? g.ModeSel : 3;
            r.Ac = AntiCheat.ForGame(g);
            r.Gone = Games.IsGone(g);
            rows.Add(r);
        }
        gl.SetRows(rows);
        acVersion = AntiCheat.Version;
        RefreshLookTargets();
    }

    // API й монітор гри відомі лише після запуску: оновлюємо рядки на льоту, щоб не перевідкривати вікно
    void RefreshGameInfo()
    {
        if (gl == null) return;
        bool ch = false;
        foreach (GameRow r in gl.Rows)
        {
            string a = r.G.Api ?? "";
            if (r.Api != a) { r.Api = a; ch = true; }
            if (r.MonHz != r.G.MonHz) { r.MonHz = r.G.MonHz; ch = true; }
        }
        if (ch) gl.SyncApi();
    }

    // нові дані про античіт (Steam) надійшли - оновити позначки, не чіпаючи правок користувача
    void RefreshAntiCheat()
    {
        if (gl == null || acVersion == AntiCheat.Version) return;
        acVersion = AntiCheat.Version;
        foreach (GameRow r in gl.Rows) r.Ac = AntiCheat.ForGame(r.G);
        gl.Invalidate();
    }

    // стан рядків - в ігри й на диск
    void SaveGames()
    {
        if (gl == null) return;
        StoreLook();
        foreach (GameRow r in gl.Rows)
        {
            r.G.OvOwn = r.Ov;
            r.G.IntRaw = r.IntRaw;
            r.G.LimitOn = r.Lim;
            r.G.LimFps = r.Fps;
            r.G.ModeSel = r.Mode;
            r.G.LookEnc = r.Look ?? "";
        }
        if (cbGenOv != null) Games.GenOv = cbGenOv.Checked;
        Games.Save();
        try { GameWrap.SyncAll(); } catch { }
    }
}
