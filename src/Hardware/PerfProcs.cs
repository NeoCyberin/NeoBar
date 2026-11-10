using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

// ======================================================================
//  Вкладка «Процеси» монітора системи: розширений диспетчер завдань і служб.
//  Процеси - розділами, як у Диспетчері завдань (програми, фонові, служби, процеси Windows), кожен своїм кольором;
//  однакові процеси згорнуті в групу. Служби - окремим списком (працюють / зупинені) із запуском, зупинкою й типом
//  запуску. Завершення, перезапуск і зміни служб - лише за підтвердженням. Аналіз підозрілого - у «Безпеці».
// ======================================================================
sealed partial class PerfWindow
{
    sealed class PLine
    {
        public int Type;                 // 0 - заголовок розділу, 1 - група однакових процесів, 2 - процес
        public int Sec, Count, Net;
        public ProcRow P;
        public string Name = "", Key = "", Ctl = "";
        public float Cpu;
        public long Ws;
        public double Io;
        public bool Child, Open;
    }

    int pview;                           // 0 - процеси, 1 - служби
    TextBox search;                      // пошук за іменем або PID (і процесів, і служб)
    string query = "";
    int scrollToPid;                     // «До процесу»: прокрутити список до цього процесу
    volatile bool sampling;
    int procScroll, procSort, selPid;    // сортування: 0 назва, 1 PID, 2 ЦП, 3 пам'ять, 4 диск, 5 мережа, 6 хто керує
    bool procDesc;
    List<PLine> lines = new List<PLine>();
    readonly HashSet<string> openGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<int> shutSecs = new HashSet<int>();
    int procTick;
    string procCopied = "";
    DateTime procCopiedAt = DateTime.MinValue;
    volatile bool actBusy;               // виконується дія (завершення, перезапуск, служба)
    string actNote = "";
    bool actBad;
    DateTime actAt = DateTime.MinValue;
    // кеш для малювання (щоб не відкривати процес і не читати реєстр на кожну перемальовку)
    int blockPid = -1;
    long blockCreated;
    string blockText = "";
    readonly Dictionary<string, int> modeCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    DateTime modeAt = DateTime.MinValue;

    static string SecName(int k)
    {
        switch (k)
        {
            case ProcRow.KApp: return L.T("Програми");
            case ProcRow.KBg: return L.T("Фонові процеси");
            case ProcRow.KSvc: return L.T("Служби");
            default: return L.T("Процеси Windows");
        }
    }

    static string KindName(int k)
    {
        switch (k)
        {
            case ProcRow.KApp: return L.T("програма з вікном");
            case ProcRow.KSvc: return L.T("служба");
            case ProcRow.KWin: return L.T("процес Windows");
            default: return L.T("фоновий процес");
        }
    }

    // кольори розділів - окремо для світлої й темної теми
    Color KindColor(int k)
    {
        bool light = Util.Lum(P.Bg) > 140;
        switch (k)
        {
            case ProcRow.KApp: return light ? Color.FromArgb(20, 110, 210) : Color.FromArgb(80, 170, 255);
            case ProcRow.KBg: return light ? Color.FromArgb(20, 135, 105) : Color.FromArgb(80, 205, 165);
            case ProcRow.KSvc: return light ? Color.FromArgb(185, 50, 135) : Color.FromArgb(235, 125, 195);
            default: return light ? Color.FromArgb(95, 85, 185) : Color.FromArgb(160, 150, 240);
        }
    }

    string BlockedCached(ProcRow p)
    {
        if (p.Pid != blockPid || p.Created != blockCreated) { blockPid = p.Pid; blockCreated = p.Created; blockText = ProcAct.Blocked(p); }
        return blockText;
    }

    int ModeCached(string svc)
    {
        if ((DateTime.Now - modeAt).TotalSeconds > 5) { modeCache.Clear(); modeAt = DateTime.Now; }
        int m;
        if (!modeCache.TryGetValue(svc, out m)) { m = ProcAct.StartMode(svc); modeCache[svc] = m; }
        return m;
    }

    void EnterProcs()
    {
        procScroll = 0;
        procTick = 0;
        if (search == null)
        {
            search = MakeInput();
            try { SendMessage(search.Handle, 0x1501, new IntPtr(1), L.T("Пошук: ім'я або PID")); } catch (Exception skipEx) { SbLog.Skip(skipEx); }     // EM_SETCUEBANNER
            search.TextChanged += delegate { query = search.Text.Trim(); procScroll = 0; svcScroll = 0; RebuildView(); Invalidate(); };
            search.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; search.Text = ""; } };
        }
        Sample();
        if (pview == 1) LoadSvcs();
    }

    void TickProcs()
    {
        if ((procTick++ & 1) == 0) Sample();
        if (pview == 1 && (DateTime.Now - svcAt).TotalSeconds >= 10 && !actBusy) LoadSvcs();
        Invalidate();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

    // знімок процесів - у фоні, щоб інтерфейс не чекав (служби, шляхи, мережа)
    void Sample()
    {
        if (sampling) return;
        sampling = true;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            try { ProcMon.Sample(); }
            catch (Exception ex) { Util.ErrorLog(ex); }
            try { BeginInvoke(new Action(delegate { sampling = false; RebuildView(); Invalidate(); })); }
            catch { sampling = false; }
        });
    }

    // пошук: число - PID (точно чи з початку), інакше частина імені
    bool Match(string name, int pid, string extra)
    {
        if (query.Length == 0) return true;
        int n;
        if (int.TryParse(query, out n)) return pid > 0 && (pid == n || pid.ToString().StartsWith(query));
        return name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 || (extra != null && extra.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0);
    }

    static PLine Of(ProcRow r, int sec, bool child)
    {
        PLine l = new PLine();
        l.Type = 2; l.Sec = sec; l.P = r; l.Name = r.Name; l.Child = child; l.Count = 1;
        l.Cpu = r.Cpu; l.Ws = r.Ws; l.Io = r.Io; l.Net = r.NetEst; l.Ctl = CtlShort(r);
        return l;
    }

    int Cmp(PLine a, PLine b)
    {
        int c = 0;
        switch (procSort)
        {
            case 0: c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); break;
            case 1: c = (a.P != null ? a.P.Pid : 0).CompareTo(b.P != null ? b.P.Pid : 0); break;
            case 2: c = a.Cpu.CompareTo(b.Cpu); break;
            case 3: c = a.Ws.CompareTo(b.Ws); break;
            case 4: c = a.Io.CompareTo(b.Io); break;
            case 5: c = a.Net.CompareTo(b.Net); break;
            case 6: c = string.Compare(a.Ctl, b.Ctl, StringComparison.CurrentCultureIgnoreCase); break;
        }
        if (procDesc) c = -c;
        if (c == 0) c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);       // рівні значення - за назвою
        if (c == 0 && a.P != null && b.P != null) c = a.P.Pid.CompareTo(b.P.Pid);
        return c;
    }

    void RebuildView()
    {
        List<ProcRow>[] secs = new List<ProcRow>[4];
        for (int s = 0; s < 4; s++) secs[s] = new List<ProcRow>();
        foreach (ProcRow r in ProcMon.Rows) if (Match(r.Name, r.Pid, null)) secs[Math.Max(0, Math.Min(3, r.Kind))].Add(r);
        bool found = query.Length > 0;           // під час пошуку все розгорнуто
        List<PLine> outl = new List<PLine>();
        for (int s = 0; s < 4; s++)
        {
            if (secs[s].Count == 0) continue;
            PLine h = new PLine();
            h.Type = 0; h.Sec = s; h.Name = SecName(s); h.Count = secs[s].Count;
            foreach (ProcRow r in secs[s]) { h.Cpu += r.Cpu; h.Ws += r.Ws; h.Io += r.Io; h.Net += r.NetEst; }
            h.Open = found || !shutSecs.Contains(s);
            outl.Add(h);
            if (!h.Open) continue;
            List<PLine> items = new List<PLine>();
            Dictionary<string, List<ProcRow>> byName = new Dictionary<string, List<ProcRow>>(StringComparer.OrdinalIgnoreCase);
            foreach (ProcRow r in secs[s])
            {
                List<ProcRow> l;
                if (!byName.TryGetValue(r.Name, out l)) { l = new List<ProcRow>(); byName[r.Name] = l; }
                l.Add(r);
            }
            foreach (KeyValuePair<string, List<ProcRow>> kv in byName)
            {
                if (kv.Value.Count == 1) { items.Add(Of(kv.Value[0], s, false)); continue; }
                PLine g = new PLine();
                g.Type = 1; g.Sec = s; g.Name = kv.Value[0].Name; g.Count = kv.Value.Count; g.Key = s + "|" + kv.Key;
                foreach (ProcRow r in kv.Value) { g.Cpu += r.Cpu; g.Ws += r.Ws; g.Io += r.Io; g.Net += r.NetEst; }
                g.Ctl = CtlShort(kv.Value[0]);
                foreach (ProcRow r in kv.Value) if (CtlShort(r) != g.Ctl) { g.Ctl = L.T("різні"); break; }
                g.Open = found || openGroups.Contains(g.Key);
                items.Add(g);
            }
            items.Sort(Cmp);
            foreach (PLine it in items)
            {
                outl.Add(it);
                if (it.Type != 1 || !it.Open) continue;
                List<PLine> kids = new List<PLine>();
                foreach (ProcRow r in byName[it.Name]) kids.Add(Of(r, s, true));
                kids.Sort(Cmp);
                outl.AddRange(kids);
            }
        }
        lines = outl;
    }

    void SortBy(int col)
    {
        if (procSort == col) procDesc = !procDesc;
        else { procSort = col; procDesc = col >= 2 && col <= 5; }
        RebuildView();
        Invalidate();
    }

    // коротко, хто керує процесом (колонка «Керує»)
    static string CtlShort(ProcRow r)
    {
        switch (r.Ctl)
        {
            case ProcRow.CSvc: return r.Svcs.Count > 0 ? L.T("Служба") : (r.CtlText.Length > 0 && r.CtlText != L.T("диспетчер служб") ? r.CtlText : L.T("Служба"));
            case ProcRow.CAuto: return L.T("Автозапуск");
            case ProcRow.CTask: return L.T("Планувальник");
            case ProcRow.CUser: return L.T("Користувач");
            case ProcRow.CWin: return "Windows";
            case ProcRow.CProc: return r.CtlText;
            default: return "—";
        }
    }

    static ProcRow Find(int pid)
    {
        foreach (ProcRow r in ProcMon.Rows) if (r.Pid == pid) return r;
        return null;
    }

    static string Rate(double b)
    {
        if (b < 1) return "—";
        if (b < 1024) return Math.Round(b) + L.T(" Б/с");
        if (b < 1048576) return (b / 1024.0).ToString("0") + L.T(" КБ/с");
        return (b / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture) + L.T(" МБ/с");
    }

    static string CpuText(float c) { return c < 0.05f ? "—" : c.ToString(c < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + "%"; }

    void Tri(Graphics g, int cx, int cy, bool open, Color c)
    {
        int s = Px(4);
        Point[] p = open ? new Point[] { new Point(cx - s, cy - s / 2), new Point(cx + s, cy - s / 2), new Point(cx, cy + s) }
                         : new Point[] { new Point(cx - s / 2, cy - s), new Point(cx - s / 2, cy + s), new Point(cx + s, cy) };
        using (SolidBrush b = new SolidBrush(c)) g.FillPolygon(b, p);
    }

    // заголовок колонки з сортуванням
    void Head(Graphics g, Rectangle hr, string text, bool right, bool act, bool desc, Action click)
    {
        Color hc = act ? P.Acc : (Over(hr) ? P.Fg : P.Sub);
        Rectangle tr = right ? new Rectangle(hr.X + Px(4), hr.Y, hr.Width - Px(12), hr.Height) : new Rectangle(hr.X + Px(16), hr.Y, hr.Width - Px(18), hr.Height);
        if (right) TxtR(g, text, act ? FB : FS, tr, hc); else TxtL(g, text, act ? FB : FS, tr, hc);
        if (act)
        {
            int ax = hr.X + Px(7), ay = hr.Y + hr.Height / 2, s = Px(3);
            Point[] tri = desc ? new Point[] { new Point(ax - s, ay - s / 2), new Point(ax + s, ay - s / 2), new Point(ax, ay + s) }
                               : new Point[] { new Point(ax - s, ay + s / 2), new Point(ax + s, ay + s / 2), new Point(ax, ay - s) };
            using (SolidBrush ab = new SolidBrush(P.Acc)) g.FillPolygon(ab, tri);
        }
        Hot(hr, click);
    }

    // червона кнопка небезпечної дії
    void DangerBtn(Graphics g, Rectangle r, string text, Action click, bool on)
    {
        bool h = on && Over(r);
        using (SolidBrush b = new SolidBrush(Util.Blend(P.Card, P.Bad, !on ? 0.08f : (h ? 0.32f : 0.18f)))) Util.FillRound(g, b, r, Math.Min(P.Rad, r.Height / 2));
        TxtC(g, text, FB, r, on ? P.Bad : Util.Blend(P.Card, P.Bad, 0.5f));
        if (on) Hot(r, click);
    }

    void PaintProcs(Graphics g, Rectangle r)
    {
        if (ProcMon.Error.Length > 0) { TxtC(g, ProcMon.Error, F, r, P.Sub); return; }

        // верхній рядок: процеси / служби, підсумок, нове завдання
        Rectangle cP = new Rectangle(r.X, r.Y + Px(3), Px(110), Px(28));
        Rectangle cS = new Rectangle(cP.Right + Px(6), cP.Y, Px(110), Px(28));
        Chip(g, cP, L.T("Процеси"), pview == 0, delegate { pview = 0; Invalidate(); });
        Chip(g, cS, L.T("Служби"), pview == 1, delegate { if (pview != 1) { pview = 1; svcScroll = 0; LoadSvcs(); } Invalidate(); });
        Rectangle bNew = new Rectangle(r.Right - Px(170), cP.Y, Px(170), Px(28));
        Btn(g, bNew, L.T("Нове завдання…"), NewTask, 0, true);
        Tip(bNew, L.T("Запустити програму, документ, теку чи команду - як «Виконати» або «Нове завдання» в Диспетчері завдань"));
        Rectangle sR = new Rectangle(bNew.X - Px(10) - Px(200), cP.Y, Px(200), Px(28));
        if (search != null) PlaceInput(g, search, sR);
        Rectangle sumR = new Rectangle(cS.Right + Px(16), cP.Y, sR.X - cS.Right - Px(24), Px(28));
        bool noteOn = actBusy || (actNote.Length > 0 && (DateTime.Now - actAt).TotalSeconds < 10);
        if (noteOn)
        {
            string nt = actBusy ? L.T("Виконується…") : actNote;
            TxtL(g, nt, actBusy ? F : FB, sumR, actBusy ? P.Sub : (actBad ? P.Bad : P.Good));
            if (!actBusy && TextRenderer.MeasureText(nt, FB).Width > sumR.Width) Tip(sumR, nt);
        }
        else if (pview == 0)
        {
            int total = ProcMon.Rows.Count, thr = 0;
            foreach (ProcRow p in ProcMon.Rows) thr += p.Threads;
            TxtL(g, L.F("Процесів: {0} · потоків: {1}", total, thr), F, sumR, P.Sub);
        }
        else
        {
            int run = 0;
            foreach (SvcInfo s in svcs) if (s.State == 4) run++;
            TxtL(g, L.F("Служб: {0} · працюють: {1}", svcs.Count, run), F, sumR, P.Sub);
        }
        Rectangle body = new Rectangle(r.X, r.Y + Px(38), r.Width, r.Height - Px(38));
        if (pview == 1) { PaintSvcList(g, body); return; }

        // колонки
        int wPid = Px(62), wCpu = Px(66), wMem = Px(96), wIo = Px(92), wNet = Px(70), sb = Px(10), wCtl = Px(132);
        int wName = r.Width - wPid - wCpu - wMem - wIo - wNet - sb;
        int[] cx = new int[7];
        cx[0] = r.X;
        cx[1] = cx[0] + wName; cx[2] = cx[1] + wPid; cx[3] = cx[2] + wCpu; cx[4] = cx[3] + wMem; cx[5] = cx[4] + wIo; cx[6] = cx[5] + wNet;
        int xCtl = cx[1] - wCtl;
        string[] hd = { L.T("Назва"), "PID", L.T("ЦП"), L.T("Пам'ять"), L.T("Диск"), L.T("Мережа") };
        int hy = body.Y, hh = Px(24);
        for (int c = 0; c < 6; c++)
        {
            int col = c;
            Rectangle hr = new Rectangle(cx[c], hy, (c == 0 ? xCtl : cx[c + 1]) - cx[c], hh);
            if (c == 0) hr = new Rectangle(hr.X + Px(12), hr.Y, hr.Width - Px(12), hr.Height);
            Head(g, hr, hd[c], c > 0, procSort == c, procDesc, delegate { SortBy(col); });
        }
        Rectangle hCtl = new Rectangle(xCtl, hy, wCtl, hh);
        Head(g, hCtl, L.T("Керує"), false, procSort == 6, procDesc, delegate { SortBy(6); });
        Tip(hCtl, L.T("Хто запускає процес і стежить за ним: служба, автозапуск, планувальник завдань, користувач, інша програма чи сама Windows"));

        // деталі вибраного процесу
        ProcRow sel = selPid != 0 ? Find(selPid) : null;
        int detH = sel != null ? DetailH(sel) : 0;
        int ly = hy + hh + Px(2);
        Rectangle list = new Rectangle(r.X, ly, r.Width, r.Bottom - ly - (detH > 0 ? detH + Px(8) : 0));

        if (ProcMon.Quiet)
        {
            // античіт: шляхів і підписів немає - це не помилка
            int nh = Px(20);
            TxtL(g, L.F("Працює античіт ({0}) - шляхи та підписи тимчасово не збираються.", ProcMon.QuietName), FS, new Rectangle(r.X + Px(4), list.Y, r.Width - Px(8), nh), P.Warn);
            list = new Rectangle(list.X, list.Y + nh, list.Width, list.Height - nh);
        }

        int rowH = Px(25);
        int content = lines.Count * rowH;
        if (scrollToPid != 0)
        {
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].Type == 2 && lines[i].P != null && lines[i].P.Pid == scrollToPid) { procScroll = i * rowH - (list.Height - rowH) / 2; break; }
            scrollToPid = 0;
        }
        // щойно вибраний рядок унизу ховала картка деталей - підтягуємо його, щоб лишився видно
        if (ensureSel && selPid != 0)
        {
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].Type == 2 && lines[i].P != null && lines[i].P.Pid == selPid)
                {
                    if (i * rowH + rowH > procScroll + list.Height) procScroll = i * rowH + rowH - list.Height;
                    if (i * rowH < procScroll) procScroll = i * rowH;
                    break;
                }
        }
        ensureSel = false;
        procScroll = Math.Max(0, Math.Min(procScroll, Math.Max(0, content - list.Height)));
        Region old = g.Clip;
        g.SetClip(list);
        int i0 = Math.Max(0, procScroll / rowH);
        int y = list.Y - procScroll + i0 * rowH;
        for (int i = i0; i < lines.Count && y < list.Bottom; i++, y += rowH)
        {
            PLine ln = lines[i];
            Rectangle rr = new Rectangle(r.X, y, r.Width - sb, rowH);
            Rectangle vis = Rectangle.Intersect(rr, list);
            bool hv = !vis.IsEmpty && Over(vis);
            Color sc = KindColor(ln.Sec);
            if (ln.Type == 0)
            {
                // заголовок розділу: колір, назва, кількість, підсумки
                if (hv) using (SolidBrush b = new SolidBrush(Util.Blend(P.Bg, P.Fg, 0.06f))) Util.FillRound(g, b, rr, Px(4));
                Tri(g, rr.X + Px(10), rr.Y + rowH / 2, ln.Open, sc);
                using (SolidBrush b = new SolidBrush(sc)) g.FillEllipse(b, new Rectangle(rr.X + Px(20), rr.Y + rowH / 2 - Px(4), Px(8), Px(8)));
                TxtL(g, ln.Name + "  " + ln.Count, FB, new Rectangle(cx[0] + Px(34), rr.Y, wName - Px(36), rowH), sc);
                TxtR(g, CpuText(ln.Cpu), FS, new Rectangle(cx[2], rr.Y, wCpu - Px(8), rowH), P.Sub);
                TxtR(g, Cleaner.Size(ln.Ws), FS, new Rectangle(cx[3], rr.Y, wMem - Px(8), rowH), P.Sub);
                TxtR(g, Rate(ln.Io), FS, new Rectangle(cx[4], rr.Y, wIo - Px(8), rowH), P.Sub);
                TxtR(g, ln.Net > 0 ? ln.Net.ToString() : "—", FS, new Rectangle(cx[5], rr.Y, wNet - Px(8), rowH), P.Sub);
                using (Pen pn = new Pen(Util.Blend(P.Bg, sc, 0.45f), 1f)) g.DrawLine(pn, rr.X, rr.Bottom - 1, rr.Right, rr.Bottom - 1);
                if (!vis.IsEmpty)
                {
                    int sec = ln.Sec;
                    Hot(vis, delegate { if (!shutSecs.Remove(sec)) shutSecs.Add(sec); RebuildView(); Invalidate(); });
                }
                continue;
            }
            ProcRow p = ln.P;
            bool isSel = p != null && p.Pid == selPid;
            Color bg = Util.Blend(i % 2 == 0 ? P.Card : Util.Blend(P.Card, P.Bg, 0.5f), sc, 0.06f);
            if (isSel) bg = Util.Blend(P.Card, P.Acc, 0.30f);
            else if (hv) bg = Util.Blend(bg, P.Fg, 0.08f);
            Rectangle rb = Rectangle.Inflate(rr, 0, -1);
            using (SolidBrush b = new SolidBrush(bg)) Util.FillRound(g, b, rb, Px(4));
            using (SolidBrush b = new SolidBrush(sc)) Util.FillRound(g, b, new Rectangle(rb.X, rb.Y + Px(3), Px(3), rb.Height - Px(6)), Px(1));
            int nx = cx[0] + Px(14) + (ln.Child ? Px(22) : 0);
            if (ln.Type == 1) Tri(g, nx + Px(4), rr.Y + rowH / 2, ln.Open, P.Sub);
            nx += Px(14);
            string nm = ln.Name.Length > 0 ? ln.Name : "—";
            int nameW = xCtl - nx - Px(4);
            TxtL(g, ln.Ctl, FS, new Rectangle(xCtl + Px(16), rr.Y, wCtl - Px(18), rowH), ln.Ctl == L.T("Автозапуск") ? KindColor(ProcRow.KApp) : P.Sub);
            if (ln.Type == 1)
            {
                int tw = Math.Min(nameW - Px(40), TextRenderer.MeasureText(nm, F).Width);
                TxtL(g, nm, F, new Rectangle(nx, rr.Y, tw + Px(4), rowH), P.Fg);
                TxtL(g, "×" + ln.Count, FS, new Rectangle(nx + tw + Px(6), rr.Y, Px(40), rowH), P.Sub);
            }
            else TxtL(g, nm, F, new Rectangle(nx, rr.Y, nameW, rowH), ln.Child ? P.Sub : P.Fg);
            if (p != null) TxtR(g, p.Pid.ToString(), F, new Rectangle(cx[1], rr.Y, wPid - Px(8), rowH), P.Sub);
            TxtR(g, CpuText(ln.Cpu), F, new Rectangle(cx[2], rr.Y, wCpu - Px(8), rowH), ln.Cpu >= 60 ? P.Warn : P.Fg);
            TxtR(g, Cleaner.Size(ln.Ws), F, new Rectangle(cx[3], rr.Y, wMem - Px(8), rowH), P.Fg);
            TxtR(g, Rate(ln.Io), F, new Rectangle(cx[4], rr.Y, wIo - Px(8), rowH), P.Fg);
            TxtR(g, ln.Net > 0 ? ln.Net.ToString() : "—", F, new Rectangle(cx[5], rr.Y, wNet - Px(8), rowH), P.Fg);
            if (vis.IsEmpty) continue;
            if (ln.Type == 1)
            {
                string key = ln.Key;
                Hot(vis, delegate { if (!openGroups.Remove(key)) openGroups.Add(key); RebuildView(); Invalidate(); });
            }
            else
            {
                int pid = p.Pid;
                Hot(vis, delegate { selPid = selPid == pid ? 0 : pid; ensureSel = true; Invalidate(); });
            }
        }
        g.Clip = old;
        if (lines.Count == 0) TxtC(g, L.T("Збираю дані…"), F, list, P.Sub);
        Wheel(list, delegate (int d) { procScroll -= d * rowH * 3; });
        VBar(g, new Rectangle(list.Right - Px(6), list.Y, Px(4), list.Height), content, list.Height, procScroll, delegate (int v) { procScroll = v; Invalidate(); });

        if (sel != null) PaintProcDetail(g, new Rectangle(r.X, r.Bottom - detH, r.Width, detH), sel);
        else if (selPid != 0) selPid = 0;
    }

    bool ensureSel;
    const int SvcMax = 3;

    int DetailH(ProcRow p)
    {
        int n = Math.Min(p.Svcs.Count, SvcMax);
        return Px(42) + Px(19) * 6 + (n > 0 ? Px(8) + Px(20) + n * Px(32) + (p.Svcs.Count > SvcMax ? Px(18) : 0) : 0) + Px(10);
    }

    void PaintProcDetail(Graphics g, Rectangle d, ProcRow p)
    {
        Card(g, d);
        int pad = Px(12), lh = Px(19);
        Color kc = KindColor(p.Kind);
        using (SolidBrush b = new SolidBrush(kc)) Util.FillRound(g, b, new Rectangle(d.X, d.Y + Px(10), Px(4), Px(22)), Px(2));
        // дії: завершити, перезапустити, тека, шлях
        int bw = Px(122), bh = Px(28), gap = Px(6);
        Rectangle bCopy = new Rectangle(d.Right - pad - bw, d.Y + Px(8), bw, bh);
        Rectangle bDir = new Rectangle(bCopy.X - gap - bw, bCopy.Y, bw, bh);
        Rectangle bRe = new Rectangle(bDir.X - gap - bw, bCopy.Y, bw, bh);
        Rectangle bEnd = new Rectangle(bRe.X - gap - bw, bCopy.Y, bw, bh);
        bool has = p.Path.Length > 0;
        bool okC = procCopied == "p" + p.Pid && (DateTime.Now - procCopiedAt).TotalMilliseconds < 1600;
        string pth = p.Path;
        int ppid = p.Pid;
        bool can = !actBusy && BlockedCached(p).Length == 0;
        DangerBtn(g, bEnd, L.T("Завершити"), delegate { AskEnd(ppid, false); }, !actBusy);
        Btn(g, bRe, L.T("Перезапустити"), delegate { AskEnd(ppid, true); }, 0, can && (has || p.Svcs.Count > 0));
        Btn(g, bDir, L.T("Показати теку"), delegate
        {
            try { if (File.Exists(pth)) Process.Start("explorer.exe", "/select,\"" + pth + "\""); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }, 0, has && File.Exists(pth));
        Btn(g, bCopy, okC ? L.T("Скопійовано") : L.T("Копіювати шлях"), delegate
        {
            try { Clipboard.SetText(pth); procCopied = "p" + ppid; procCopiedAt = DateTime.Now; Invalidate(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }, 0, has);
        Tip(bEnd, p.Svcs.Count > 0 ? L.T("Зупинити служби процесу (разом із залежними) і завершити його") : L.T("Завершити разом із пов'язаними процесами - перед цим буде список і підтвердження"));
        Tip(bRe, p.Svcs.Count > 0 ? L.T("Перезапустити служби процесу") : L.T("Завершити й запустити знову з тими самими параметрами"));
        TxtL(g, p.Name + "  ·  PID " + p.Pid + "  ·  " + KindName(p.Kind), FB, new Rectangle(d.X + pad, d.Y + Px(9), bEnd.X - d.X - pad - Px(8), Px(24)), kc);

        int y = d.Y + Px(42);
        int lw = Px(92);
        int full = d.Width - pad * 2;
        ProcRow par = Find(p.Parent);
        // підпис - лише для вибраного, у фоні (з каталогами Windows, як у «Безпеці»)
        string sig;
        ProcMon.SigInfo si = has ? ProcMon.SigOf(p.Path) : null;
        if (!has) sig = ProcMon.Quiet ? L.T("не збирається (працює античіт)") : L.T("немає доступу до шляху");
        else if (si == null || !si.Done) sig = L.T("перевіряється…");
        else if (si.Kind == Sig.Ok) sig = si.Signer.Length > 0 ? L.F("підписаний: {0}", si.Signer) : L.T("підписаний");
        else if (si.Kind == Sig.Bad) sig = L.T("підпис недійсний");
        else if (si.Kind == Sig.Expired) sig = si.Signer.Length > 0 ? L.F("підпис прострочений: {0}", si.Signer) : L.T("підпис прострочений");
        else if (si.Kind == Sig.Unknown) sig = "—";
        else sig = L.T("без підпису");

        string file = has ? (File.Exists(p.Path) ? p.Path : L.F("{0} (видалено)", p.Path)) : "—";
        Field(g, new Rectangle(d.X + pad, y, full, lh), lw, L.T("Файл"), file); y += lh;
        Field(g, new Rectangle(d.X + pad, y, full, lh), lw, L.T("Команда"), p.Cmd.Length > 0 ? p.Cmd : "—"); y += lh;
        int half = (full - Px(16)) / 2;
        int x2 = d.X + pad + half + Px(16);
        string[][] left =
        {
            new string[] { L.T("Керує"), p.CtlText.Length > 0 ? p.CtlText : "—" },
            new string[] { L.T("Запустив"), par != null ? par.Name + " (PID " + par.Pid + ")" : (p.Parent > 0 ? L.F("PID {0} (завершився)", p.Parent) : "—") },
            new string[] { L.T("Старт"), p.Created > 0 ? p.Started.ToString("dd.MM.yyyy HH:mm:ss") : "—" },
            new string[] { L.T("Підпис"), sig }
        };
        string[][] right =
        {
            new string[] { L.T("Пам'ять"), L.F("{0} · виділено {1}", Cleaner.Size(p.Ws), Cleaner.Size(p.Commit)) },
            new string[] { L.T("Дескриптори"), L.F("{0} · потоків {1}", p.Handles, p.Threads) },
            new string[] { L.T("Мережа"), L.F("з'єднань {0} · портів {1}", p.NetEst, p.NetListen) },
            new string[] { L.T("Диск"), Rate(p.Io) }
        };
        for (int i = 0; i < 4; i++)
        {
            Field(g, new Rectangle(d.X + pad, y, half, lh), lw, left[i][0], left[i][1]);
            Field(g, new Rectangle(x2, y, half, lh), lw, right[i][0], right[i][1]);
            y += lh;
        }
        if (p.Svcs.Count > 0) PaintSvcs(g, new Rectangle(d.X + pad, y + Px(8), full, d.Bottom - y - Px(8)), p);
    }

    static string StateText(int st)
    {
        switch (st)
        {
            case 4: return L.T("працює");
            case 2: return L.T("запускається");
            case 3: return L.T("зупиняється");
            case 5: case 6: case 7: return L.T("призупинена");
            default: return L.T("зупинена");
        }
    }

    Color StateColor(int st) { return st == 4 ? P.Good : (st == 1 ? P.Sub : P.Warn); }

    static string ModeText(int m)
    {
        switch (m)
        {
            case 0: return L.T("Вручну");
            case 1: return L.T("Автоматично");
            case 2: return L.T("Із затримкою");
            case 3: return L.T("Вимкнено");
            default: return L.T("Системна");
        }
    }

    // тип запуску: чотири перемикачі
    void ModeChips(Graphics g, int x, int y, int cw, string sn, string sd, int mode)
    {
        int cg = Px(4);
        for (int k = 0; k < 4; k++)
        {
            int kk = k;
            Rectangle cr = new Rectangle(x + k * (cw + cg), y, cw, Px(28));
            Chip(g, cr, ModeText(k), mode == k, delegate { if (!actBusy) AskMode(sn, sd, kk); });
        }
        if (mode < 0) Tip(new Rectangle(x, y, cw * 4 + cg * 3, Px(28)), L.T("Особливий тип запуску (завантажувальна чи системна служба)"));
    }

    // служби в процесі: стан, тип запуску, зупинка / запуск
    void PaintSvcs(Graphics g, Rectangle r, ProcRow p)
    {
        using (Pen pn = new Pen(P.Line, 1f)) g.DrawLine(pn, r.X, r.Y - Px(4), r.Right, r.Y - Px(4));
        TxtL(g, p.Svcs.Count == 1 ? L.T("Служба в цьому процесі") : L.F("Служби в цьому процесі: {0}", p.Svcs.Count), FS, new Rectangle(r.X, r.Y, r.Width, Px(20)), P.Sub);
        int y = r.Y + Px(20);
        int cw = Px(98), bw = Px(104), sw = Px(84), cg = Px(4);
        for (int i = 0; i < p.Svcs.Count && i < SvcMax; i++)
        {
            SvcInfo s = p.Svcs[i];
            int rowY = y + Px(2);
            Rectangle btn = new Rectangle(r.Right - bw, rowY, bw, Px(28));
            int chipsX = btn.X - Px(10) - (cw * 4 + cg * 3);
            Rectangle st = new Rectangle(chipsX - Px(8) - sw, rowY, sw, Px(28));
            Rectangle nm = new Rectangle(r.X, rowY, st.X - r.X - Px(8), Px(28));
            TxtL(g, s.Display, F, nm, P.Fg);
            Tip(nm, s.Display + " (" + s.Name + ")");
            bool run = s.State == 4;
            TxtL(g, StateText(s.State), FS, st, StateColor(s.State));
            string sn = s.Name, sd = s.Display;
            if (ProcAct.IsCriticalSvc(s.Name))
                TxtL(g, L.T("ключова служба Windows - не змінюється"), FS, new Rectangle(chipsX, rowY, cw * 4 + cg * 3 + Px(10) + bw, Px(28)), P.Sub);
            else
            {
                ModeChips(g, chipsX, rowY, cw, sn, sd, ModeCached(s.Name));
                Btn(g, btn, run ? L.T("Зупинити") : L.T("Запустити"), delegate { SvcToggle(sn, sd, run); }, 0, !actBusy);
            }
            y += Px(32);
        }
        if (p.Svcs.Count > SvcMax) TxtL(g, L.F("і ще {0} - див. «Служби» вгорі", p.Svcs.Count - SvcMax), FS, new Rectangle(r.X, y, r.Width, Px(18)), P.Sub);
    }

    void SvcToggle(string name, string display, bool running)
    {
        if (running) AskSvcStop(name, display);
        else RunAct(delegate { string e = ProcAct.SvcStart(name); return e.Length > 0 ? "!" + e : L.F("Службу «{0}» запущено.", display); });
    }

    // дія у фоні; результат - рядок ("!" на початку - помилка)
    void RunAct(Func<string> act)
    {
        if (actBusy) return;
        actBusy = true;
        Invalidate();
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            string res;
            try { res = act(); }
            catch (Exception ex) { Util.ErrorLog(ex); res = "!" + ex.Message; }
            try
            {
                BeginInvoke(new Action(delegate
                {
                    actBusy = false;
                    modeCache.Clear();
                    blockPid = -1;
                    actBad = res.StartsWith("!");
                    actNote = actBad ? res.Substring(1) : res;
                    actAt = DateTime.Now;
                    Sample();
                    if (pview == 1) { svcLoading = false; LoadSvcs(); }
                    Invalidate();
                }));
            }
            catch { actBusy = false; }
        });
    }

    static string Names(List<ProcRow> l, int max)
    {
        List<string> n = new List<string>();
        for (int i = 0; i < l.Count && i < max; i++) n.Add(l[i].Name + " (" + l[i].Pid + ")");
        string s = string.Join(", ", n.ToArray());
        return l.Count > max ? s + L.F(" і ще {0}", l.Count - max) : s;
    }

    // завершення / перезапуск: аналіз зв'язків, список, підтвердження
    void AskEnd(int pid, bool restart)
    {
        if (actBusy) return;
        ProcRow p = Find(pid);
        KillPlan k = ProcAct.Plan(p);
        string title = restart ? L.T("Перезапуск процесу") : L.T("Завершення процесу");
        if (k.Block.Length > 0) { SbDialog.Show(Bar, this, title, k.Block, L.T("Гаразд")); return; }
        StringBuilder t = new StringBuilder();
        t.Append(restart ? L.F("Перезапустити «{0}» (PID {1})?", k.Name, k.Pid) : L.F("Завершити «{0}» (PID {1})?", k.Name, k.Pid)).Append("\n\n");
        if (k.Svcs.Count > 0)
        {
            t.Append(L.F("Служби процесу буде {0} через диспетчер служб: {1}.", restart ? L.T("перезапущено") : L.T("зупинено"), string.Join(", ", k.SvcNames.ToArray()))).Append("\n");
            if (k.Dependents.Count > 0) t.Append(L.F("Разом з ними зупиняться залежні служби: {0}.", string.Join(", ", k.Dependents.ToArray()))).Append("\n");
        }
        if (k.Kids.Count > 0 && !(restart && k.Svcs.Count > 0))
            t.Append(L.F("Пов'язані процеси, запущені ним ({0}), буде завершено першими: {1}.", k.Kids.Count, Names(k.Kids, 8))).Append("\n");
        if (k.Svcs.Count == 0)
            t.Append(k.HasWin ? L.T("Програмі спершу буде запропоновано закритися самій (до 3 с), далі - примусово. Незбережені дані можуть втратитися.")
                              : L.T("Процес буде завершено примусово. Незбережені дані можуть втратитися.")).Append("\n");
        if (restart && k.Svcs.Count == 0)
            t.Append(Elevated.IsAdmin && !k.WasElevated ? L.T("Потім програму буде запущено знову з тими самими параметрами (зі звичайними правами, як і була).") : L.T("Потім програму буде запущено знову з тими самими параметрами.")).Append("\n");
        if (ProcMon.Quiet) t.Append("\n").Append(L.F("Працює античіт ({0}): завершення процесів гри він може розцінити як втручання.", ProcMon.QuietName));
        string[] btns = restart ? new string[] { L.T("Перезапустити"), L.T("Скасувати") }
                      : (k.Kids.Count > 0 && k.Svcs.Count == 0 ? new string[] { L.T("Завершити все"), L.T("Лише цей процес"), L.T("Скасувати") } : new string[] { L.T("Завершити"), L.T("Скасувати") });
        int a = SbDialog.Show(Bar, this, title, t.ToString().TrimEnd(), btns);
        if (a < 0 || a >= btns.Length - 1) return;
        bool links = a == 0;
        if (restart) RunAct(delegate { return ProcAct.Restart(k); });
        else RunAct(delegate { return ProcAct.End(k, links); });
        if (!restart && selPid == pid) selPid = 0;
    }

    void AskSvcStop(string name, string display)
    {
        List<string> dep = ProcAct.DependentsOf(name);
        string t = L.F("Зупинити службу «{0}»?", display) + (dep.Count > 0 ? "\n\n" + L.F("Разом з нею зупиняться залежні служби: {0}.", string.Join(", ", dep.ToArray())) : "") +
                   "\n\n" + L.T("Тип запуску не зміниться: служба може запуститися знову сама або після перезавантаження ПК.");
        if (SbDialog.Show(Bar, this, L.T("Служба"), t, L.T("Зупинити"), L.T("Скасувати")) != 0) return;
        RunAct(delegate { string e = ProcAct.SvcStop(name); return e.Length > 0 ? "!" + e : L.F("Службу «{0}» зупинено.", display); });
    }

    void AskSvcRestart(string name, string display)
    {
        if (SbDialog.Show(Bar, this, L.T("Служба"), L.F("Перезапустити службу «{0}»?", display), L.T("Перезапустити"), L.T("Скасувати")) != 0) return;
        RunAct(delegate
        {
            List<string> dep = ProcAct.DependentsOf(name);
            string e = ProcAct.SvcStop(name);
            if (e.Length == 0) e = ProcAct.SvcStart(name);
            for (int i = dep.Count - 1; i >= 0 && e.Length == 0; i--) ProcAct.SvcStart(dep[i]);
            return e.Length > 0 ? "!" + e : L.F("Службу перезапущено: {0}.", display);
        });
    }

    void AskMode(string name, string display, int mode)
    {
        string[] names = { L.T("вручну"), L.T("автоматично"), L.T("автоматично із затримкою"), L.T("вимкнено") };
        string path = SvcImage(name);
        bool win = path.Length == 0 || path.IndexOf(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("%systemroot%", StringComparison.OrdinalIgnoreCase) >= 0 || path.StartsWith(@"\SystemRoot", StringComparison.OrdinalIgnoreCase);
        string t = L.F("Тип запуску служби «{0}»: {1}?", display, names[mode]) + "\n\n" +
                   (mode == 3 ? L.T("Вимкнена служба не запуститься, доки її не ввімкнуть знову. ") : "") +
                   (win ? L.T("Це служба Windows: невдала зміна може порушити роботу системи. ") : "") +
                   L.T("Зміну записано в журнал - відкотити можна в «Безпеці», вкладка «Система».");
        if (SbDialog.Show(Bar, this, L.T("Служба"), t, L.T("Змінити"), L.T("Скасувати")) != 0) return;
        RunAct(delegate { string e = ProcAct.SetStart(name, display, mode); return e.Length > 0 ? "!" + e : L.F("Тип запуску «{0}»: {1}.", display, names[mode]); });
    }

    // підпис і значення; довге значення - повністю в підказці
    void Field(Graphics g, Rectangle r, int lw, string label, string value)
    {
        TxtL(g, label, FS, new Rectangle(r.X, r.Y, lw, r.Height), P.Sub);
        Rectangle vr = new Rectangle(r.X + lw, r.Y, r.Width - lw, r.Height);
        TxtL(g, value, F, vr, P.Fg);
        if (TextRenderer.MeasureText(value, F).Width > vr.Width) Tip(vr, value);
    }

    // ================= служби =================
    List<SvcInfo> svcs = new List<SvcInfo>();
    DateTime svcAt = DateTime.MinValue;
    int svcScroll, svcSort;                  // 0 назва, 1 ім'я, 2 стан, 3 тип запуску, 4 PID
    bool svcDesc;
    string selSvc = "";
    readonly HashSet<int> shutSvc = new HashSet<int>();
    readonly Dictionary<string, string[]> svcInfo = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);   // шлях, опис

    volatile bool svcLoading;

    // список служб - у фоні (сотні записів реєстру)
    void LoadSvcs()
    {
        svcAt = DateTime.Now;
        if (svcLoading) return;
        svcLoading = true;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            List<SvcInfo> l = null;
            try { l = ProcMon.AllServices(); }
            catch (Exception ex) { Util.ErrorLog(ex); }
            try { BeginInvoke(new Action(delegate { svcLoading = false; if (l != null) svcs = l; Invalidate(); })); }
            catch { svcLoading = false; }
        });
    }

    int SvcCmp(SvcInfo a, SvcInfo b)
    {
        int c = 0;
        switch (svcSort)
        {
            case 1: c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
            case 2: c = a.State.CompareTo(b.State); break;
            case 3: c = a.Mode.CompareTo(b.Mode); break;
            case 4: c = a.Pid.CompareTo(b.Pid); break;
            default: c = string.Compare(a.Display, b.Display, StringComparison.CurrentCultureIgnoreCase); break;
        }
        if (svcDesc) c = -c;
        if (c == 0) c = string.Compare(a.Display, b.Display, StringComparison.CurrentCultureIgnoreCase);
        return c;
    }

    void SvcSortBy(int col)
    {
        if (svcSort == col) svcDesc = !svcDesc;
        else { svcSort = col; svcDesc = false; }
        Invalidate();
    }


    // шлях до файла служби й опис (з реєстру; опис буває посиланням на ресурс DLL)
    string[] SvcMeta(string name)
    {
        string[] v;
        if (svcInfo.TryGetValue(name, out v)) return v;
        v = new string[] { "", "" };
        try
        {
            using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey k = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name))
            {
                if (k != null)
                {
                    v[0] = Convert.ToString(k.GetValue("ImagePath", "", RegistryValueOptions.DoNotExpandEnvironmentNames));
                    string d = Convert.ToString(k.GetValue("Description", ""));
                    if (d.StartsWith("@"))
                    {
                        StringBuilder sb = new StringBuilder(1024);
                        if (Win.SHLoadIndirectString(d, sb, sb.Capacity, IntPtr.Zero) == 0) d = sb.ToString(); else d = "";
                    }
                    v[1] = d;
                }
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        svcInfo[name] = v;
        return v;
    }

    string SvcImage(string name) { return SvcMeta(name)[0]; }

    SvcInfo FindSvc(string name)
    {
        foreach (SvcInfo s in svcs) if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    void PaintSvcList(Graphics g, Rectangle r)
    {
        int sb = Px(10);
        int wName = Px(210), wState = Px(110), wMode = Px(130), wPid = Px(70);
        int wDisp = r.Width - wName - wState - wMode - wPid - sb;
        int[] cx = { r.X, r.X + wDisp, r.X + wDisp + wName, r.X + wDisp + wName + wState, r.X + wDisp + wName + wState + wMode, r.Right - sb };
        string[] hd = { L.T("Назва"), L.T("Ім'я служби"), L.T("Стан"), L.T("Запуск"), "PID" };
        int hy = r.Y, hh = Px(24);
        for (int c = 0; c < 5; c++)
        {
            int col = c;
            Rectangle hr = new Rectangle(cx[c] + (c == 0 ? Px(12) : 0), hy, cx[c + 1] - cx[c] - (c == 0 ? Px(12) : 0), hh);
            Head(g, hr, hd[c], c == 4, svcSort == c, svcDesc, delegate { SvcSortBy(col); });
        }
        SvcInfo sel = selSvc.Length > 0 ? FindSvc(selSvc) : null;
        int detH = sel != null ? Px(42) + Px(19) * 4 + Px(36) + Px(10) : 0;
        int ly = hy + hh + Px(2);
        Rectangle list = new Rectangle(r.X, ly, r.Width, r.Bottom - ly - (detH > 0 ? detH + Px(8) : 0));

        // розділи: працюють / зупинені
        List<SvcInfo> run = new List<SvcInfo>(), stop = new List<SvcInfo>();
        foreach (SvcInfo s in svcs) if (Match(s.Display, s.Pid, s.Name)) (s.State == 1 ? stop : run).Add(s);
        run.Sort(SvcCmp);
        stop.Sort(SvcCmp);
        List<object> rows = new List<object>();
        rows.Add(0);
        bool found = query.Length > 0;
        if (found || !shutSvc.Contains(0)) foreach (SvcInfo s in run) rows.Add(s);
        rows.Add(1);
        if (found || !shutSvc.Contains(1)) foreach (SvcInfo s in stop) rows.Add(s);

        int rowH = Px(25);
        int content = rows.Count * rowH;
        if (ensureSel && sel != null)
        {
            int si = rows.IndexOf(sel);
            if (si >= 0 && si * rowH + rowH > svcScroll + list.Height) svcScroll = si * rowH + rowH - list.Height;
            if (si >= 0 && si * rowH < svcScroll) svcScroll = si * rowH;
        }
        ensureSel = false;
        svcScroll = Math.Max(0, Math.Min(svcScroll, Math.Max(0, content - list.Height)));
        Region old = g.Clip;
        g.SetClip(list);
        int i0 = Math.Max(0, svcScroll / rowH);
        int y = list.Y - svcScroll + i0 * rowH;
        for (int i = i0; i < rows.Count && y < list.Bottom; i++, y += rowH)
        {
            Rectangle rr = new Rectangle(r.X, y, r.Width - sb, rowH);
            Rectangle vis = Rectangle.Intersect(rr, list);
            bool hv = !vis.IsEmpty && Over(vis);
            if (rows[i] is int)
            {
                int sec = (int)rows[i];
                Color sc = sec == 0 ? P.Good : P.Sub;
                if (hv) using (SolidBrush b = new SolidBrush(Util.Blend(P.Bg, P.Fg, 0.06f))) Util.FillRound(g, b, rr, Px(4));
                Tri(g, rr.X + Px(10), rr.Y + rowH / 2, !shutSvc.Contains(sec), sc);
                TxtL(g, (sec == 0 ? L.T("Працюють") : L.T("Зупинені")) + "  " + (sec == 0 ? run.Count : stop.Count), FB, new Rectangle(rr.X + Px(24), rr.Y, rr.Width / 2, rowH), sc);
                using (Pen pn = new Pen(Util.Blend(P.Bg, sc, 0.45f), 1f)) g.DrawLine(pn, rr.X, rr.Bottom - 1, rr.Right, rr.Bottom - 1);
                if (!vis.IsEmpty) Hot(vis, delegate { if (!shutSvc.Remove(sec)) shutSvc.Add(sec); Invalidate(); });
                continue;
            }
            SvcInfo s = (SvcInfo)rows[i];
            bool isSel = string.Equals(s.Name, selSvc, StringComparison.OrdinalIgnoreCase);
            Color stc = StateColor(s.State);
            Color bg = i % 2 == 0 ? P.Card : Util.Blend(P.Card, P.Bg, 0.5f);
            if (isSel) bg = Util.Blend(P.Card, P.Acc, 0.30f);
            else if (hv) bg = Util.Blend(bg, P.Fg, 0.08f);
            Rectangle rb = Rectangle.Inflate(rr, 0, -1);
            using (SolidBrush b = new SolidBrush(bg)) Util.FillRound(g, b, rb, Px(4));
            using (SolidBrush b = new SolidBrush(stc)) Util.FillRound(g, b, new Rectangle(rb.X, rb.Y + Px(3), Px(3), rb.Height - Px(6)), Px(1));
            TxtL(g, s.Display, F, new Rectangle(cx[0] + Px(16), rr.Y, cx[1] - cx[0] - Px(20), rowH), P.Fg);
            TxtL(g, s.Name, FS, new Rectangle(cx[1] + Px(16), rr.Y, cx[2] - cx[1] - Px(20), rowH), P.Sub);
            TxtL(g, StateText(s.State), FS, new Rectangle(cx[2] + Px(16), rr.Y, cx[3] - cx[2] - Px(20), rowH), stc);
            TxtL(g, ModeText(s.Mode), FS, new Rectangle(cx[3] + Px(16), rr.Y, cx[4] - cx[3] - Px(20), rowH), s.Mode == 3 ? P.Warn : P.Sub);
            TxtR(g, s.Pid > 0 ? s.Pid.ToString() : "—", F, new Rectangle(cx[4], rr.Y, cx[5] - cx[4] - Px(8), rowH), P.Sub);
            if (!vis.IsEmpty)
            {
                string nm = s.Name;
                Hot(vis, delegate { selSvc = string.Equals(selSvc, nm, StringComparison.OrdinalIgnoreCase) ? "" : nm; ensureSel = true; Invalidate(); });
            }
        }
        g.Clip = old;
        if (svcs.Count == 0) TxtC(g, L.T("Збираю дані…"), F, list, P.Sub);
        Wheel(list, delegate (int d) { svcScroll -= d * rowH * 3; });
        VBar(g, new Rectangle(list.Right - Px(6), list.Y, Px(4), list.Height), content, list.Height, svcScroll, delegate (int v) { svcScroll = v; Invalidate(); });

        if (sel != null) PaintSvcDetail(g, new Rectangle(r.X, r.Bottom - detH, r.Width, detH), sel);
        else selSvc = "";
    }

    void PaintSvcDetail(Graphics g, Rectangle d, SvcInfo s)
    {
        Card(g, d);
        int pad = Px(12), lh = Px(19), lw = Px(110);
        Color stc = StateColor(s.State);
        using (SolidBrush b = new SolidBrush(stc)) Util.FillRound(g, b, new Rectangle(d.X, d.Y + Px(10), Px(4), Px(22)), Px(2));
        bool crit = ProcAct.IsCriticalSvc(s.Name);
        bool run = s.State == 4;
        string sn = s.Name, sd = s.Display;
        int pid = s.Pid;
        int bw = Px(130), bh = Px(28), gap = Px(6);
        Rectangle b3 = new Rectangle(d.Right - pad - bw, d.Y + Px(8), bw, bh);
        Rectangle b2 = new Rectangle(b3.X - gap - bw, b3.Y, bw, bh);
        Rectangle b1 = new Rectangle(b2.X - gap - bw, b3.Y, bw, bh);
        if (run) DangerBtn(g, b1, L.T("Зупинити"), delegate { AskSvcStop(sn, sd); }, !actBusy && !crit);
        else Btn(g, b1, L.T("Запустити"), delegate { SvcToggle(sn, sd, false); }, 1, !actBusy && s.State == 1 && s.Mode != 3);
        if (!run && s.Mode == 3) Tip(b1, L.T("Служба вимкнена - спершу виберіть інший тип запуску"));
        Btn(g, b2, L.T("Перезапустити"), delegate { AskSvcRestart(sn, sd); }, 0, !actBusy && run && !crit);
        Btn(g, b3, L.T("До процесу"), delegate { GoToProcess(pid); }, 0, pid > 0);
        TxtL(g, s.Display, FB, new Rectangle(d.X + pad, d.Y + Px(9), b1.X - d.X - pad - Px(8), Px(24)), P.Fg);

        int y = d.Y + Px(42);
        int full = d.Width - pad * 2;
        string[] meta = SvcMeta(s.Name);
        ProcRow host = pid > 0 ? Find(pid) : null;
        Field(g, new Rectangle(d.X + pad, y, full, lh), lw, L.T("Ім'я служби"), s.Name + "  ·  " + StateText(s.State) + (pid > 0 ? "  ·  " + (host != null ? host.Name + " " : "") + "PID " + pid : "")); y += lh;
        Field(g, new Rectangle(d.X + pad, y, full, lh), lw, L.T("Файл"), meta[0].Length > 0 ? meta[0] : "—"); y += lh;
        TxtL(g, L.T("Опис"), FS, new Rectangle(d.X + pad, y, lw, lh), P.Sub);
        Rectangle dr = new Rectangle(d.X + pad + lw, y + Px(1), full - lw, lh * 2);
        TxtW(g, meta[1].Length > 0 ? meta[1] : "—", FS, dr, P.Fg);
        if (meta[1].Length > 0) Tip(dr, meta[1]);
        y += lh * 2;
        TxtL(g, L.T("Запуск"), FS, new Rectangle(d.X + pad, y + Px(4), lw, Px(28)), P.Sub);
        if (crit) TxtL(g, L.T("ключова служба Windows - не змінюється"), FS, new Rectangle(d.X + pad + lw, y + Px(4), full - lw, Px(28)), P.Sub);
        else ModeChips(g, d.X + pad + lw, y + Px(4), Px(110), sn, sd, s.Mode);
    }

    // «До процесу»: перейти до списку процесів, розгорнути розділ і групу, виділити й прокрутити до рядка
    void GoToProcess(int pid)
    {
        ProcRow p = Find(pid);
        pview = 0;
        selPid = pid;
        if (p != null)
        {
            if (!Match(p.Name, p.Pid, null) && search != null) search.Text = "";
            int k = Math.Max(0, Math.Min(3, p.Kind));
            shutSecs.Remove(k);
            openGroups.Add(k + "|" + p.Name);
        }
        RebuildView();
        scrollToPid = pid;
        Invalidate();
    }

    // ================= нове завдання =================
    static string lastTask = "";

    void NewTask()
    {
        bool admin;
        string cmd = RunTaskDialog.Ask(Bar, this, lastTask, out admin);
        if (cmd == null || cmd.Trim().Length == 0) return;
        lastTask = cmd.Trim();
        string err = TaskLauncher.Run(lastTask, admin);
        actBad = err.Length > 0;
        actNote = actBad ? err : L.F("Запущено: {0}", lastTask);
        actAt = DateTime.Now;
        Invalidate();
    }
}

// запуск команди, програми, документа чи теки - як «Виконати»
static class TaskLauncher
{
    // поділ рядка на файл і параметри: «"C:\x y\a.exe" -p», «C:\x y\a.exe» (наявний файл), «notepad a.txt»
    public static void Split(string line, out string file, out string args)
    {
        string l = line.Trim();
        file = l; args = "";
        if (l.StartsWith("\""))
        {
            int q = l.IndexOf('"', 1);
            if (q > 0) { file = l.Substring(1, q - 1); args = l.Substring(q + 1).Trim(); }
            return;
        }
        string ex = Environment.ExpandEnvironmentVariables(l);
        try { if (File.Exists(ex) || Directory.Exists(ex)) { file = l; return; } } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        int sp = l.IndexOf(' ');
        if (sp > 0) { file = l.Substring(0, sp); args = l.Substring(sp + 1).Trim(); }
    }

    // "" - запущено, інакше текст помилки
    public static string Run(string line, bool admin)
    {
        string file, args;
        Split(line, out file, out args);
        file = Environment.ExpandEnvironmentVariables(file);
        try
        {
            if (!admin && Elevated.IsAdmin)
            {
                // NeoBar працює від адміністратора, а завдання - зі звичайними правами (як із Провідника)
                if (UserLaunch.Shell(file, args).Length > 0) return "";
                return L.T("Не вдалося запустити зі звичайними правами.");
            }
            ProcessStartInfo psi = new ProcessStartInfo(file, args);
            psi.UseShellExecute = true;
            if (admin && !Elevated.IsAdmin) psi.Verb = "runas";
            try { string dir = Path.GetDirectoryName(file); if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            using (Process p = Process.Start(psi)) { }
            return "";
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            if (ex.NativeErrorCode == 1223) return L.T("Скасовано.");
            if (ex.NativeErrorCode == 2 || ex.NativeErrorCode == 3) return L.F("Не знайдено: {0}", file);
            return ex.Message;
        }
        catch (Exception ex) { return ex.Message; }
    }
}

// вікно «Нове завдання»: рядок команди, огляд, права адміністратора; файл можна перетягнути
sealed class RunTaskDialog : SbWindow
{
    readonly TextBox tb;
    bool admin, ok;

    RunTaskDialog(NeoBar bar, string value) : base(bar, L.T("Нове завдання"), 560, 268)
    {
        Glyph = "\uE7C4";
        TopMost = true;
        tb = MakeInput();
        tb.Text = value ?? "";
        tb.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Ok(); } };
    }

    protected override bool AcceptsFiles { get { return true; } }

    protected override void FilesDropped(string[] files)
    {
        if (files.Length == 0) return;
        string f = files[0];
        tb.Text = f.IndexOf(' ') >= 0 ? "\"" + f + "\"" : f;
        tb.SelectionStart = tb.Text.Length;
        Activate();
        Invalidate();
    }

    void Ok() { if (tb.Text.Trim().Length == 0) return; ok = true; Close(); }

    void Browse()
    {
        using (OpenFileDialog d = new OpenFileDialog())
        {
            d.Title = L.T("Нове завдання");
            d.Filter = L.T("Програми|*.exe;*.bat;*.cmd;*.com;*.msc;*.lnk|Усі файли|*.*");
            if (d.ShowDialog(this) != DialogResult.OK) return;
            tb.Text = d.FileName.IndexOf(' ') >= 0 ? "\"" + d.FileName + "\"" : d.FileName;
        }
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        TxtW(g, L.T("Введіть ім'я програми, теки, документа чи адресу - Windows відкриє їх. Файл можна перетягнути в це вікно."), F, new Rectangle(r.X, r.Y, r.Width, Px(44)), P.Fg);
        Rectangle br = new Rectangle(r.Right - Px(110), r.Y + Px(52), Px(110), Px(34));
        PlaceInput(g, tb, new Rectangle(r.X, br.Y, br.X - r.X - Px(8), Px(34)));
        Btn(g, br, L.T("Огляд…"), Browse, 0, true);
        Switch(g, new Rectangle(r.X, br.Bottom + Px(12), r.Width, Px(28)), L.T("Створити з правами адміністратора"), admin, delegate { admin = !admin; Invalidate(); });
        Btn(g, new Rectangle(r.Right - Px(130), r.Bottom - Px(36), Px(130), Px(36)), L.T("Запустити"), Ok, 1, true);
        Btn(g, new Rectangle(r.Right - Px(270), r.Bottom - Px(36), Px(130), Px(36)), L.T("Скасувати"), delegate { Close(); }, 0, true);
    }

    // null - скасовано
    public static string Ask(NeoBar bar, Form owner, string value, out bool admin)
    {
        admin = false;
        using (RunTaskDialog d = new RunTaskDialog(bar, value))
        {
            Rectangle ob = owner != null ? owner.Bounds : Screen.PrimaryScreen.WorkingArea;
            d.Location = new Point(ob.Left + (ob.Width - d.Width) / 2, ob.Top + Math.Max(0, (ob.Height - d.Height) / 2));
            d.ShowDialog(owner);
            admin = d.admin;
            return d.ok ? d.tb.Text.Trim() : null;
        }
    }
}
