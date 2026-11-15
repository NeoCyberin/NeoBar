using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

// ======================================================================
//  Вікно «Пристрої»: пристрої за групами (без повторів - однакові зведено в один рядок «×N», службові приховано),
//  проблемні - окремим фільтром із поясненням коду; дії - вимкнути/увімкнути, видалити пристрій, видалити драйвер,
//  властивості Windows. «Сховище драйверів» - пакети сторонніх драйверів із розміром і застарілими версіями.
//  «Оновлення» - драйвери, які пропонує Windows Update.
// ======================================================================
sealed partial class DeviceWindow : ToolWindow
{
    const int TabDev = 0, TabStore = 1, TabUpd = 2;
    // фільтри списку пристроїв
    const int FMain = 0, FBad = 1, FOff = 2, FGhost = 3, FAll = 4;

    List<DevItem> devs;
    List<DrvPackage> pkgs;
    List<Devices.Update> ups;
    bool loading, pkgLoading, updBusy, acting;
    string updErr = "";
    int filter = FMain;
    readonly int[] scroll = new int[3];
    string open = "";                       // розгорнутий рядок (ключ групи)
    readonly TextBox search;
    public static int LastProblems = -1;    // для підпису віджета на панелі

    sealed class Grp { public string Key, Cls, ClassName; public List<DevItem> Items = new List<DevItem>(); public DevItem First { get { return Items[0]; } } }

    public DeviceWindow(NeoBar bar) : base(bar, L.T("Пристрої"), 700, 760)
    {
        Glyph = "";
        Tabs = new string[] { L.T("Пристрої"), L.T("Сховище драйверів"), L.T("Оновлення") };
        search = MakeInput();
        search.TextChanged += delegate { scroll[TabDev] = 0; Invalidate(); };
        Reload();
    }

    void Reload()
    {
        if (loading) return;
        loading = true;
        Invalidate();
        Util.Bg(delegate
        {
            List<DevItem> l = null;
            try { l = Devices.List(); } catch (Exception ex) { Util.ErrorLog(ex); }
            Ui(delegate
            {
                loading = false;
                devs = l ?? new List<DevItem>();
                int bad = 0;
                foreach (DevItem d in devs) if (d.Bad) bad++;
                LastProblems = bad;
                if (Bar != null) Bar.WidgetRedraw("devices");
                if (pkgs != null) LoadStore();
                Invalidate();
            });
        });
    }

    void LoadStore()
    {
        if (pkgLoading || devs == null) return;
        pkgLoading = true;
        List<DevItem> d0 = devs;
        Util.Bg(delegate
        {
            List<DrvPackage> l = null;
            try { l = Devices.Store(d0); } catch (Exception ex) { Util.ErrorLog(ex); }
            Ui(delegate { pkgs = l ?? new List<DrvPackage>(); pkgLoading = false; Invalidate(); });
            // розміри пакетів - окремо (великі драйвери відеокарт - тисячі файлів)
            if (l != null)
                foreach (DrvPackage p in l)
                {
                    if (IsDisposed) return;
                    p.Size = p.StoreDir.Length > 0 ? Devices.DirSize(p.StoreDir) : 0;
                }
            Ui(Invalidate);
        });
    }

    void Ui(Action a) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch (Exception skipEx) { SbLog.Skip(skipEx); } }

    protected override void OnTab()
    {
        search.Visible = Tab == TabDev;
        if (Tab == TabStore && pkgs == null) LoadStore();
        Invalidate();
    }

    // ---------- групи ----------
    bool Pass(DevItem d)
    {
        switch (filter)
        {
            case FBad: return d.Bad;
            case FOff: return d.Disabled;
            case FGhost: return !d.Present;
            case FAll: return true;
            default: return d.Present && (d.Problem != 0 || !Devices.Noise(d));
        }
    }

    List<Grp> Groups()
    {
        List<Grp> res = new List<Grp>();
        if (devs == null) return res;
        string q = search.Text.Trim();
        Dictionary<string, Grp> by = new Dictionary<string, Grp>();
        foreach (DevItem d in devs)
        {
            if (!Pass(d)) continue;
            if (q.Length > 0 && d.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0 && d.ClassName.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0 &&
                d.DrvProvider.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0 && d.HwId.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            // однакові пристрої (ядра процесора, порти) - один рядок; різний стан чи драйвер - окремо
            string key = d.Class + "|" + d.Name + "|" + d.DrvVersion + "|" + d.Present + "|" + d.Problem;
            Grp g;
            if (!by.TryGetValue(key, out g)) { g = new Grp(); g.Key = key; g.Cls = d.Class; g.ClassName = d.ClassName; by[key] = g; res.Add(g); }
            g.Items.Add(d);
        }
        res.Sort(delegate (Grp a, Grp b)
        {
            int c = Devices.Rank(a.Cls).CompareTo(Devices.Rank(b.Cls));
            if (c == 0) c = string.Compare(a.ClassName, b.ClassName, StringComparison.CurrentCultureIgnoreCase);
            if (c == 0) c = b.First.Bad.CompareTo(a.First.Bad);
            return c != 0 ? c : string.Compare(a.First.Name, b.First.Name, StringComparison.CurrentCultureIgnoreCase);
        });
        return res;
    }

    int Count(int f) { int n = 0; if (devs != null) { int keep = filter; filter = f; foreach (DevItem d in devs) if (Pass(d)) n++; filter = keep; } return n; }

    // ---------- малювання ----------
    protected override void PaintBody(Graphics g, Rectangle r)
    {
        int foot = Px(40);
        Rectangle body = new Rectangle(r.X, r.Y, r.Width, r.Height - foot);
        if (Tab == TabDev) PaintDevices(g, body);
        else if (Tab == TabStore) PaintStore(g, body);
        else PaintUpdates(g, body);
        string ft = NoteLive ? Note : (acting ? L.T("Виконується…") : (Elevated.IsAdmin ? L.T("Зміни робить вбудований засіб Windows (pnputil). Критичні для запуску Windows пристрої не вимикаються й не видаляються.") : L.T("Для змін потрібні права адміністратора - Windows запитає дозвіл.")));
        TxtW(g, ft, FS, new Rectangle(r.X, r.Bottom - Px(36), r.Width, Px(36)), NoteLive ? (NoteBad ? P.Bad : P.Good) : P.Sub);
    }

    void PaintDevices(Graphics g, Rectangle r)
    {
        int y = r.Y;
        Rectangle sR = new Rectangle(r.X, y, r.Width - Px(42), Px(32));
        PlaceInput(g, search, sR);
        if (search.Text.Length == 0) TxtL(g, L.T("Пошук пристрою"), FS, new Rectangle(sR.X + Px(10), sR.Y, sR.Width, sR.Height), P.Sub);
        Rectangle rs = new Rectangle(r.Right - Px(32), y, Px(32), Px(32));
        IconBtn(g, rs, "sync", loading || acting ? null : (Action)delegate { Act(L.T("Пошук змін обладнання…"), delegate { return Devices.Rescan(); }); }, false);
        Tip(rs, L.T("Шукати зміни обладнання (як у Диспетчері пристроїв) і перечитати список"));
        y += Px(40);
        int cx = r.X;
        int[] fs = { FMain, FBad, FOff, FGhost, FAll };
        string[] fn = { L.T("Основні"), L.T("Потребують уваги"), L.T("Вимкнені"), L.T("Від'єднані"), L.T("Усі") };
        string[] ft = {
            L.T("Підключені пристрої без службових (програмні, томи, системні шини, концентратори USB)"),
            L.T("Пристрої з помилкою: немає драйвера, не запускається, Windows зупинила"),
            L.T("Пристрої, які вимкнено вручну"),
            L.T("Колись підключені, зараз їх немає: старі флешки, монітори, адаптери VPN. Їх можна прибрати - при підключенні Windows додасть знову"),
            L.T("Усі пристрої, зокрема службові й від'єднані") };
        for (int i = 0; i < fs.Length; i++)
        {
            int n = devs == null || fs[i] == FMain || fs[i] == FAll ? -1 : Count(fs[i]);
            string t = fn[i] + (n > 0 ? " · " + n : "");
            int w = TextRenderer.MeasureText(t, FS).Width + Px(18);
            if (cx + w > r.Right) break;
            Rectangle c = new Rectangle(cx, y, w, Px(28));
            int fi = fs[i];
            Chip(g, c, t, filter == fi, delegate { filter = fi; scroll[TabDev] = 0; open = ""; Invalidate(); });
            Tip(c, ft[i]);
            cx += w + Px(6);
        }
        y += Px(38);
        Rectangle list = new Rectangle(r.X, y, r.Width, r.Bottom - y);
        if (devs == null) { TxtC(g, L.T("Читаю перелік пристроїв…"), F, new Rectangle(list.X, list.Y + Px(30), list.Width, Px(24)), P.Sub); return; }
        List<Grp> gs = Groups();
        if (filter == FGhost && gs.Count > 0)
        {
            Rectangle bb = new Rectangle(list.Right - Px(230), list.Y, Px(230), Px(30));
            Btn(g, bb, L.T("Прибрати всі від'єднані"), acting ? null : (Action)RemoveGhosts, 0, !acting);
            TxtL(g, L.T("Пристрої, яких зараз немає, лише засмічують список"), FS, new Rectangle(list.X, list.Y, bb.X - list.X - Px(8), Px(30)), P.Sub);
            list = new Rectangle(list.X, list.Y + Px(40), list.Width, list.Height - Px(40));
        }
        if (gs.Count == 0)
        {
            string e = filter == FBad ? L.T("Усі пристрої працюють - проблем не знайдено.") : (search.Text.Length > 0 ? L.T("Нічого не знайдено.") : L.T("Тут порожньо."));
            TxtC(g, e, F, new Rectangle(list.X, list.Y + Px(30), list.Width, Px(24)), filter == FBad ? P.Good : P.Sub);
            return;
        }
        int w0 = list.Width - Px(12);
        int total = 0;
        string lastCls = null;
        foreach (Grp gp in gs) { if (gp.ClassName != lastCls) { total += Px(32); lastCls = gp.ClassName; } total += RowH(gp, w0) + Px(4); }
        int sc = scroll[TabDev] = Math.Max(0, Math.Min(Math.Max(0, total - list.Height), scroll[TabDev]));
        GraphicsState st = g.Save();
        g.SetClip(list);
        int cy = list.Y - sc;
        lastCls = null;
        foreach (Grp gp in gs)
        {
            if (gp.ClassName != lastCls)
            {
                lastCls = gp.ClassName;
                int n = 0;
                foreach (Grp x in gs) if (x.ClassName == lastCls) n += x.Items.Count;
                if (cy + Px(32) > list.Y && cy < list.Bottom)
                    TxtL(g, lastCls + "  ·  " + n, FB, new Rectangle(list.X + Px(4), cy + Px(6), w0, Px(24)), P.Sub);
                cy += Px(32);
            }
            int h = RowH(gp, w0);
            Rectangle rr = new Rectangle(list.X, cy, w0, h);
            cy += h + Px(4);
            if (rr.Bottom < list.Y || rr.Y > list.Bottom) continue;
            PaintRow(g, rr, gp, list);
        }
        g.Restore(st);
        Wheel(list, delegate (int d) { scroll[TabDev] -= d > 0 ? Px(120) : -Px(120); Invalidate(); });
        VBar(g, new Rectangle(list.Right - Px(6), list.Y + Px(2), Px(4), list.Height - Px(4)), total, list.Height, sc, delegate (int v) { scroll[TabDev] = v; Invalidate(); });
    }

    List<string> Details(Grp gp)
    {
        DevItem d = gp.First;
        List<string> l = new List<string>();
        l.Add(L.T("Стан: ") + Devices.ProblemText(d) + (d.Present && d.Problem != 0 && d.Problem != 22 ? " (" + L.F("код {0}", d.Problem) + ")" : ""));
        if (d.Inf.Length > 0 || d.DrvVersion.Length > 0)
            l.Add(L.T("Драйвер: ") + Join(d.DrvProvider, d.DrvVersion, d.DrvDate, d.Inf));
        else l.Add(L.T("Драйвер: не встановлено"));
        if (d.Mfg.Length > 0) l.Add(L.T("Виробник: ") + d.Mfg);
        if (d.Service.Length > 0) l.Add(L.T("Служба: ") + d.Service);
        if (d.HwId.Length > 0) l.Add(L.T("ID обладнання: ") + d.HwId);
        if (gp.Items.Count > 1) l.Add(L.F("Однакових пристроїв: {0} (дії - для всіх)", gp.Items.Count));
        if (d.Problem == 43 || d.Problem == 10) l.Add(L.T("Що робити: перепідключіть пристрій, оновіть або перевстановіть драйвер («Видалити пристрій», потім «Шукати зміни обладнання»)."));
        else if (d.NoDriver) l.Add(L.T("Що робити: пошукайте драйвер у вкладці «Оновлення» або на сайті виробника за ID обладнання."));
        return l;
    }

    static string Join(params string[] parts)
    {
        List<string> l = new List<string>();
        foreach (string s in parts) if (!string.IsNullOrEmpty(s)) l.Add(s);
        return string.Join("  ·  ", l.ToArray());
    }

    int RowH(Grp gp, int w)
    {
        int h = Px(48);
        if (open == gp.Key) h += Details(gp).Count * Px(20) + Px(12) + ActRows(gp, w) * Px(40);
        return h;
    }

    List<KeyValuePair<string, Action>> Acts(Grp gp)
    {
        DevItem d = gp.First;
        List<KeyValuePair<string, Action>> acts = new List<KeyValuePair<string, Action>>();
        if (d.Present) acts.Add(new KeyValuePair<string, Action>(L.T("Властивості"), delegate { Devices.Properties(d); }));
        if (Devices.CanDisable(d)) acts.Add(new KeyValuePair<string, Action>(d.Disabled ? L.T("Увімкнути") : L.T("Вимкнути"), delegate { DoToggle(gp); }));
        if (Devices.CanRemove(d)) acts.Add(new KeyValuePair<string, Action>(L.T("Видалити пристрій"), delegate { DoRemove(gp); }));
        if (Devices.CanDeleteDriver(d)) acts.Add(new KeyValuePair<string, Action>(L.T("Видалити драйвер"), delegate { DoDeleteDriver(gp); }));
        if (d.HwId.Length > 0) acts.Add(new KeyValuePair<string, Action>(L.T("Копіювати ID"), delegate { Copy(d.HwId); Say(L.T("ID обладнання скопійовано."), false); }));
        return acts;
    }

    // кнопки дій - рядками, скільки влізе в ширину
    int ActRows(Grp gp, int w)
    {
        int rows = 1, x = Px(30);
        foreach (KeyValuePair<string, Action> a in Acts(gp))
        {
            int bw = TextRenderer.MeasureText(a.Key, F).Width + Px(28);
            if (x + bw > w - Px(8) && x > Px(30)) { rows++; x = Px(30); }
            x += bw + Px(8);
        }
        if (Devices.Critical(gp.First) && gp.First.Present) rows++;
        return rows;
    }

    void PaintRow(Graphics g, Rectangle rr, Grp gp, Rectangle list)
    {
        DevItem d = gp.First;
        bool isOpen = open == gp.Key;
        Card(g, rr, isOpen || Over(rr) && rr.Y >= list.Y ? Util.Blend(P.Card, P.Fg, 0.04f) : P.Card);
        Color sc = !d.Present ? P.Sub : d.Bad ? P.Bad : d.Disabled ? P.Warn : P.Good;
        using (SolidBrush b = new SolidBrush(sc)) g.FillEllipse(b, rr.X + Px(12), rr.Y + Px(19), Px(9), Px(9));
        string name = d.Name + (gp.Items.Count > 1 ? "  ×" + gp.Items.Count : "");
        TxtL(g, name, FB, new Rectangle(rr.X + Px(30), rr.Y + Px(4), rr.Width - Px(40), Px(22)), d.Present ? P.Fg : P.Sub);
        string sub = d.Bad || d.Disabled || !d.Present ? Devices.ProblemText(d) : (d.Inf.Length > 0 || d.DrvVersion.Length > 0 ? Join(d.DrvProvider, d.DrvVersion, d.DrvDate) : L.T("Драйвер не потрібен або вбудований"));
        TxtL(g, sub, FS, new Rectangle(rr.X + Px(30), rr.Y + Px(25), rr.Width - Px(40), Px(18)), d.Bad ? P.Bad : P.Sub);
        Rectangle head = new Rectangle(rr.X, rr.Y, rr.Width, Px(48));
        if (head.Y >= list.Y && head.Bottom <= list.Bottom + Px(48)) Hot(Rectangle.Intersect(head, list), delegate { open = isOpen ? "" : gp.Key; Invalidate(); });
        if (!isOpen) return;
        int y = rr.Y + Px(50);
        foreach (string s in Details(gp))
        {
            TxtL(g, s, FS, new Rectangle(rr.X + Px(30), y, rr.Width - Px(40), Px(20)), P.Fg);
            y += Px(20);
        }
        y += Px(8);
        int x = rr.X + Px(30);
        foreach (KeyValuePair<string, Action> a in Acts(gp))
        {
            int bw = TextRenderer.MeasureText(a.Key, F).Width + Px(28);
            if (x + bw > rr.Right - Px(8) && x > rr.X + Px(30)) { x = rr.X + Px(30); y += Px(40); }
            Rectangle b = new Rectangle(x, y, bw, Px(32));
            if (b.Y >= list.Y && b.Bottom <= list.Bottom) Btn(g, b, a.Key, acting ? null : a.Value, 0, !acting);
            else Btn(g, b, a.Key, null, 0, false);
            x += bw + Px(8);
        }
        if (Devices.Critical(d) && d.Present)
            TxtL(g, L.T("Критичний для запуску Windows - вимкнення й видалення недоступні"), FS, new Rectangle(rr.X + Px(30), y + Px(40), rr.Width - Px(40), Px(28)), P.Sub);
    }

    // ---------- дії ----------
    void Act(string busy, Func<string> work)
    {
        if (acting) return;
        acting = true;
        Say(busy, false);
        Util.Bg(delegate
        {
            string e;
            try { e = work(); } catch (Exception ex) { e = ex.Message; }
            Ui(delegate
            {
                acting = false;
                if (e == null || e.Length == 0) Say(L.T("Готово."), false);
                else if (e == "!") Say(L.T("Готово. Щоб зміни набули чинності, перезавантажте комп'ютер."), false);
                else Say(L.T("Не вдалося: ") + e, true);
                Reload();
                if (Tab == TabStore) { pkgs = null; LoadStore(); }
            });
        });
    }

    // для групи однакових - по черзі; помилка першого зупиняє решту
    static string Each(List<DevItem> l, Func<DevItem, string> f)
    {
        bool reboot = false;
        foreach (DevItem d in l)
        {
            string e = f(d);
            if (e == "!") reboot = true;
            else if (e.Length > 0) return e;
        }
        return reboot ? "!" : "";
    }

    string Many(Grp gp) { return gp.Items.Count > 1 ? " " + L.F("(однакових: {0})", gp.Items.Count) : ""; }

    void DoToggle(Grp gp)
    {
        DevItem d = gp.First;
        if (d.Disabled) { Act(L.T("Вмикаю…"), delegate { return Each(gp.Items, Devices.Enable); }); return; }
        string warn = string.Equals(d.Class, "Display", StringComparison.OrdinalIgnoreCase) ? L.T("\n\nЕкран на мить згасне, далі Windows покаже зображення без драйвера відеокарти (низька роздільність, без ігор).") :
                      string.Equals(d.Class, "Keyboard", StringComparison.OrdinalIgnoreCase) || string.Equals(d.Class, "Mouse", StringComparison.OrdinalIgnoreCase) ? L.T("\n\nЯкщо це ваша єдина клавіатура чи миша, увімкнути її назад буде важко.") :
                      string.Equals(d.Class, "Net", StringComparison.OrdinalIgnoreCase) ? L.T("\n\nЯкщо через цей адаптер ви в інтернеті, з'єднання зникне.") : "";
        if (Ask(L.T("Вимкнути пристрій"), L.F("Вимкнути «{0}»{1}? Увімкнути назад можна тут же, у фільтрі «Вимкнені».", d.Name, Many(gp)) + warn, L.T("Вимкнути"), L.T("Скасувати")) != 0) return;
        Act(L.T("Вимикаю…"), delegate { return Each(gp.Items, Devices.Disable); });
    }

    void DoRemove(Grp gp)
    {
        DevItem d = gp.First;
        string text = d.Present
            ? L.F("Видалити «{0}»{1} з системи? Драйвер лишиться в сховищі: після «Шукати зміни обладнання» або перезавантаження Windows знайде пристрій і встановить його знову. Так лікують «зависле» обладнання.", d.Name, Many(gp))
            : L.F("Прибрати від'єднаний «{0}»{1}? Якщо підключити його знову, Windows додасть його сама.", d.Name, Many(gp));
        if (Ask(L.T("Видалити пристрій"), text, L.T("Видалити"), L.T("Скасувати")) != 0) return;
        Act(L.T("Видаляю…"), delegate { return Each(gp.Items, Devices.Remove); });
    }

    void DoDeleteDriver(Grp gp)
    {
        DevItem d = gp.First;
        string warn = string.Equals(d.Class, "Display", StringComparison.OrdinalIgnoreCase) ? L.T("\n\nДля відеокарти Windows перейде на базовий драйвер (низька роздільність) до встановлення нового.") : "";
        if (Ask(L.T("Видалити драйвер"), L.F("Видалити драйвер {0} ({1}) для «{2}»: прибрати його з усіх пристроїв, що ним користуються, і зі сховища драйверів? Windows підбере інший драйвер, якщо він є.", d.Inf, Join(d.DrvProvider, d.DrvVersion), d.Name) + warn,
                L.T("Видалити"), L.T("Скасувати")) != 0) return;
        string inf = d.Inf;
        Act(L.T("Видаляю драйвер…"), delegate { return Devices.DeleteDriver(inf, true); });
    }

    void RemoveGhosts()
    {
        List<DevItem> l = new List<DevItem>();
        foreach (DevItem d in devs) if (!d.Present && Devices.CanRemove(d) && !Devices.Critical(d)) l.Add(d);
        if (l.Count == 0) return;
        if (Ask(L.T("Прибрати від'єднані"), L.F("Прибрати {0} від'єднаних пристроїв (старі флешки, монітори, адаптери)? Пристрої, критичні для Windows, не зачіпаються. Якщо котрийсь підключити знову, Windows додасть його сама.", l.Count), L.T("Прибрати"), L.T("Скасувати")) != 0) return;
        Act(L.T("Прибираю від'єднані пристрої…"), delegate
        {
            int bad = 0;
            foreach (DevItem d in l) { string e = Devices.Remove(d); if (e.Length > 0 && e != "!") bad++; }
            return bad == 0 ? "" : L.F("не вдалося прибрати {0} з {1}", bad, l.Count);
        });
    }

    // ---------- сховище драйверів ----------
    void PaintStore(Graphics g, Rectangle r)
    {
        if (pkgs == null) { TxtC(g, L.T("Читаю сховище драйверів…"), F, new Rectangle(r.X, r.Y + Px(30), r.Width, Px(24)), P.Sub); return; }
        long all = 0, oldSz = 0; int old = 0; bool sizing = false;
        foreach (DrvPackage p in pkgs) { if (p.Size < 0) sizing = true; else all += p.Size; if (p.Old) { old++; if (p.Size > 0) oldSz += p.Size; } }
        string sum = L.F("Сторонніх пакетів драйверів: {0}", pkgs.Count) + (sizing ? L.T(", рахую розмір…") : ", " + SizeText(all)) + (old > 0 ? L.F("; застарілих: {0}", old) + (sizing ? "" : " (" + SizeText(oldSz) + ")") : "");
        Rectangle bb = new Rectangle(r.Right - Px(210), r.Y, Px(210), Px(32));
        Btn(g, bb, L.T("Видалити застарілі"), acting || old == 0 ? null : (Action)DeleteOld, old > 0 ? 1 : 0, !acting && old > 0);
        Tip(bb, L.T("Старі версії драйверів, які лишились після оновлень і якими не користується жоден підключений пристрій"));
        TxtW(g, sum, FS, new Rectangle(r.X, r.Y, bb.X - r.X - Px(10), Px(32)), P.Sub);
        Rectangle list = new Rectangle(r.X, r.Y + Px(42), r.Width, r.Height - Px(42));
        List<DrvPackage> l = new List<DrvPackage>(pkgs);
        l.Sort(delegate (DrvPackage a, DrvPackage b)
        {
            int c = b.Old.CompareTo(a.Old);
            if (c == 0) c = string.Compare(a.Class, b.Class, StringComparison.CurrentCultureIgnoreCase);
            return c != 0 ? c : string.Compare(a.Original, b.Original, StringComparison.OrdinalIgnoreCase);
        });
        int rowH = Px(52), w0 = list.Width - Px(12);
        int total = l.Count * (rowH + Px(4));
        int sc = scroll[TabStore] = Math.Max(0, Math.Min(Math.Max(0, total - list.Height), scroll[TabStore]));
        GraphicsState st = g.Save();
        g.SetClip(list);
        int cy = list.Y - sc;
        foreach (DrvPackage p in l)
        {
            Rectangle rr = new Rectangle(list.X, cy, w0, rowH);
            cy += rowH + Px(4);
            if (rr.Bottom < list.Y || rr.Y > list.Bottom) continue;
            Card(g, rr);
            string status = p.Old ? L.T("застарілий") : p.UsedPresent > 0 ? L.F("використовується ({0})", p.UsedPresent) : p.UsedGhost > 0 ? L.T("лише від'єднаними") : L.T("не використовується");
            Color stc = p.Old ? P.Warn : p.UsedPresent > 0 ? P.Good : P.Sub;
            Rectangle del = new Rectangle(rr.Right - Px(110), rr.Y + Px(10), Px(100), Px(32));
            bool can = p.UsedPresent == 0 && !acting;
            DrvPackage pp = p;
            Btn(g, del, L.T("Видалити"), can && del.Y >= list.Y && del.Bottom <= list.Bottom ? (Action)delegate { DeletePkg(pp); } : null, 0, can);
            if (p.UsedPresent > 0) Tip(del, L.T("Ним користуються підключені пристрої - видаліть його з вкладки «Пристрої» («Видалити драйвер»)"));
            TxtL(g, p.Original + "  ·  " + Join(p.Provider, p.Class), FB, new Rectangle(rr.X + Px(12), rr.Y + Px(5), del.X - rr.X - Px(140), Px(22)), P.Fg);
            TxtR(g, status, FS, new Rectangle(del.X - Px(136), rr.Y + Px(5), Px(128), Px(22)), stc);
            TxtL(g, Join(p.Version, p.Date, p.Inf, p.Size >= 0 ? SizeText(p.Size) : ""), FS, new Rectangle(rr.X + Px(12), rr.Y + Px(28), del.X - rr.X - Px(20), Px(18)), P.Sub);
        }
        g.Restore(st);
        Wheel(list, delegate (int d) { scroll[TabStore] -= d > 0 ? Px(120) : -Px(120); Invalidate(); });
        VBar(g, new Rectangle(list.Right - Px(6), list.Y + Px(2), Px(4), list.Height - Px(4)), total, list.Height, sc, delegate (int v) { scroll[TabStore] = v; Invalidate(); });
    }

    void DeletePkg(DrvPackage p)
    {
        if (Ask(L.T("Видалити драйвер"), L.F("Видалити пакет {0} ({1}, {2}) зі сховища драйверів? Пристрої зараз ним не користуються.", p.Original, p.Inf, Join(p.Provider, p.Version)), L.T("Видалити"), L.T("Скасувати")) != 0) return;
        string inf = p.Inf;
        Act(L.T("Видаляю…"), delegate { return Devices.DeleteDriver(inf, false); });
    }

    void DeleteOld()
    {
        List<DrvPackage> l = new List<DrvPackage>();
        long sz = 0;
        foreach (DrvPackage p in pkgs) if (p.Old) { l.Add(p); if (p.Size > 0) sz += p.Size; }
        if (Ask(L.T("Видалити застарілі"), L.F("Видалити {0} застарілих пакетів драйверів ({1})? Для кожного є новіша версія, і жоден підключений пристрій ними не користується.", l.Count, SizeText(sz)), L.T("Видалити"), L.T("Скасувати")) != 0) return;
        Act(L.T("Видаляю застарілі драйвери…"), delegate
        {
            int bad = 0;
            foreach (DrvPackage p in l) { string e = Devices.DeleteDriver(p.Inf, false); if (e.Length > 0 && e != "!") bad++; }
            return bad == 0 ? "" : L.F("не вдалося видалити {0} з {1}", bad, l.Count);
        });
    }

    // ---------- оновлення ----------
    void PaintUpdates(Graphics g, Rectangle r)
    {
        TxtW(g, L.T("Драйвери, які Microsoft пропонує через Windows Update для вашого обладнання. Для відеокарт новіші драйвери зазвичай на сайті виробника (AMD, NVIDIA, Intel)."), FS, new Rectangle(r.X, r.Y, r.Width - Px(220), Px(40)), P.Sub);
        Rectangle bb = new Rectangle(r.Right - Px(210), r.Y + Px(4), Px(210), Px(32));
        Btn(g, bb, updBusy ? L.T("Шукаю…") : L.T("Шукати оновлення"), updBusy || acting ? null : (Action)FindUpdates, 1, !updBusy && !acting);
        int y = r.Y + Px(52);
        if (updBusy) { TxtC(g, L.T("Windows Update шукає драйвери - це може тривати до хвилини."), F, new Rectangle(r.X, y + Px(20), r.Width, Px(24)), P.Sub); return; }
        if (updErr.Length > 0) { TxtW(g, L.T("Не вдалося: ") + updErr, F, new Rectangle(r.X, y, r.Width, Px(48)), P.Bad); return; }
        if (ups == null) return;
        if (ups.Count == 0) { TxtC(g, L.T("Нових драйверів у Windows Update немає."), F, new Rectangle(r.X, y + Px(20), r.Width, Px(24)), P.Good); return; }
        Rectangle ia = new Rectangle(r.Right - Px(210), y, Px(210), Px(32));
        Btn(g, ia, L.F("Встановити всі ({0})", ups.Count), acting ? null : (Action)delegate { InstallUpd(new List<Devices.Update>(ups)); }, 1, !acting);
        TxtL(g, L.F("Знайдено: {0}", ups.Count), FB, new Rectangle(r.X, y, ia.X - r.X, Px(32)), P.Fg);
        y += Px(42);
        foreach (Devices.Update u in ups)
        {
            if (y + Px(52) > r.Bottom) break;
            Rectangle rr = new Rectangle(r.X, y, r.Width - Px(12), Px(52));
            Card(g, rr);
            Rectangle ib = new Rectangle(rr.Right - Px(120), rr.Y + Px(10), Px(110), Px(32));
            Devices.Update uu = u;
            Btn(g, ib, L.T("Встановити"), acting ? null : (Action)delegate { InstallUpd(new List<Devices.Update> { uu }); }, 0, !acting);
            TxtL(g, u.Title, FB, new Rectangle(rr.X + Px(12), rr.Y + Px(5), ib.X - rr.X - Px(20), Px(22)), P.Fg);
            TxtL(g, Join(u.Provider, u.Class, u.Date, u.HwId), FS, new Rectangle(rr.X + Px(12), rr.Y + Px(28), ib.X - rr.X - Px(20), Px(18)), P.Sub);
            y += Px(56);
        }
    }

    void FindUpdates()
    {
        updBusy = true; updErr = "";
        Invalidate();
        Thread t = new Thread(delegate ()
        {
            string err;
            List<Devices.Update> l = Devices.FindUpdates(out err);
            Ui(delegate { updBusy = false; updErr = err; ups = err.Length > 0 ? null : l; Invalidate(); });
        });
        t.IsBackground = true;
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
    }

    void InstallUpd(List<Devices.Update> l)
    {
        if (Ask(L.T("Встановити драйвери"), L.F("Завантажити й встановити драйверів: {0}? Windows Update зробить це сам; може знадобитись перезавантаження.", l.Count), L.T("Встановити"), L.T("Скасувати")) != 0) return;
        Act(L.T("Завантажую й встановлюю…"), delegate
        {
            string e = Devices.Install(l);
            if (e.Length == 0 || e == "!") Ui(delegate { ups = null; FindUpdates(); });
            return e;
        });
    }
}
