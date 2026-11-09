using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

// ======================================================================
//  Вкладка «Процеси» монітора системи: розширений диспетчер завдань.
//  Розділи як у Диспетчері завдань (програми, фонові, служби, процеси Windows), кожен своїм кольором; однакові процеси
//  згорнуті в групу. Процеси з ознаками аномальної поведінки винесено нагору, причину видно в підказці й у деталях.
//  Лише показ: нічого не завершує й не блокує.
// ======================================================================
sealed partial class PerfWindow
{
    sealed class PLine
    {
        public int Type;                 // 0 - заголовок розділу, 1 - група однакових процесів, 2 - процес
        public int Sec, Count, Level, Net;
        public ProcRow P;
        public string Name = "", Key = "", Ctl = "";
        public float Cpu;
        public long Ws;
        public double Io;
        public bool Child, Open;
    }

    const int SecOdd = 0;                // розділи: 0 - аномалії, далі 1 + ProcRow.Kind
    int procScroll, procSort, selPid;    // сортування: 0 назва, 1 PID, 2 ЦП, 3 пам'ять, 4 диск, 5 мережа, 6 хто керує
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
    bool procDesc, onlyOdd;
    List<PLine> lines = new List<PLine>();
    readonly HashSet<string> openGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<int> shutSecs = new HashSet<int>();
    int procTick;
    string procCopied = "";
    DateTime procCopiedAt = DateTime.MinValue;

    static string SecName(int s)
    {
        switch (s)
        {
            case SecOdd: return L.T("Аномалії");
            case 1 + ProcRow.KApp: return L.T("Програми");
            case 1 + ProcRow.KBg: return L.T("Фонові процеси");
            case 1 + ProcRow.KSvc: return L.T("Служби");
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

    Color SecColor(int s) { return s == SecOdd ? P.Bad : KindColor(s - 1); }

    Color LevelColor(int lev) { return lev >= 2 ? P.Bad : (lev == 1 ? P.Warn : P.Sub); }

    void EnterProcs()
    {
        procScroll = 0;
        procTick = 0;
        ProcMon.Sample();
        RebuildView();
    }

    void TickProcs()
    {
        if ((procTick++ & 1) == 0) { ProcMon.Sample(); RebuildView(); }
        Invalidate();
    }

    static PLine Of(ProcRow r, int sec, bool child)
    {
        PLine l = new PLine();
        l.Type = 2; l.Sec = sec; l.P = r; l.Name = r.Name; l.Child = child; l.Count = 1;
        l.Cpu = r.Cpu; l.Ws = r.Ws; l.Io = r.Io; l.Net = r.NetEst; l.Level = r.Level; l.Ctl = CtlShort(r);
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
        List<ProcRow>[] secs = new List<ProcRow>[5];
        for (int s = 0; s < 5; s++) secs[s] = new List<ProcRow>();
        foreach (ProcRow r in ProcMon.Rows)
        {
            int s = r.Level > 0 ? SecOdd : 1 + r.Kind;
            if (onlyOdd && s != SecOdd) continue;
            secs[s].Add(r);
        }
        List<PLine> outl = new List<PLine>();
        for (int s = 0; s < 5; s++)
        {
            if (secs[s].Count == 0) continue;
            PLine h = new PLine();
            h.Type = 0; h.Sec = s; h.Name = SecName(s); h.Count = secs[s].Count;
            foreach (ProcRow r in secs[s]) { h.Cpu += r.Cpu; h.Ws += r.Ws; h.Io += r.Io; h.Net += r.NetEst; h.Level = Math.Max(h.Level, r.Level); }
            h.Open = !shutSecs.Contains(s);
            outl.Add(h);
            if (!h.Open) continue;
            List<PLine> items = new List<PLine>();
            Dictionary<string, List<ProcRow>> byName = new Dictionary<string, List<ProcRow>>(StringComparer.OrdinalIgnoreCase);
            foreach (ProcRow r in secs[s])
            {
                if (s == SecOdd) { items.Add(Of(r, s, false)); continue; }         // аномалії - кожна окремо
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
                g.Open = openGroups.Contains(g.Key);
                g.P = null;
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
        else { procSort = col; procDesc = col >= 2; }
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

    void PaintProcs(Graphics g, Rectangle r)
    {
        if (ProcMon.Error.Length > 0) { TxtC(g, ProcMon.Error, F, r, P.Sub); return; }
        int odd = 0, total = ProcMon.Rows.Count, thr = 0;
        foreach (ProcRow p in ProcMon.Rows) { if (p.Level > 0) odd++; thr += p.Threads; }

        // верхній рядок: підсумок і фільтр
        Rectangle chipA = new Rectangle(r.Right - Px(130), r.Y + Px(3), Px(130), Px(28));
        Rectangle chipB = new Rectangle(chipA.X - Px(8) - Px(110), chipA.Y, Px(110), Px(28));
        Chip(g, chipB, L.T("Усі"), !onlyOdd, delegate { onlyOdd = false; procScroll = 0; RebuildView(); Invalidate(); });
        Chip(g, chipA, L.T("Аномалії") + (odd > 0 ? " · " + odd : ""), onlyOdd, delegate { onlyOdd = true; procScroll = 0; RebuildView(); Invalidate(); });
        string sum = float.IsNaN(Sensors.Cpu) ? L.F("Процесів: {0} · потоків: {1}", total, thr) : L.F("Процесів: {0} · потоків: {1} · ЦП: {2}%", total, thr, Math.Round(Sensors.Cpu));
        bool noteOn = actBusy || (actNote.Length > 0 && (DateTime.Now - actAt).TotalSeconds < 10);
        Rectangle sumR = new Rectangle(r.X + Px(4), r.Y + Px(3), chipB.X - r.X - Px(12), Px(28));
        if (noteOn)
        {
            string nt = actBusy ? L.T("Виконується…") : actNote;
            TxtL(g, nt, actBusy ? F : FB, sumR, actBusy ? P.Sub : (actBad ? P.Bad : P.Good));
            if (!actBusy && TextRenderer.MeasureText(nt, FB).Width > sumR.Width) Tip(sumR, nt);
        }
        else TxtL(g, sum, F, sumR, P.Sub);

        // колонки
        int wPid = Px(62), wCpu = Px(66), wMem = Px(96), wIo = Px(92), wNet = Px(70), sb = Px(10);
        int wName = r.Width - wPid - wCpu - wMem - wIo - wNet - sb;
        int[] cx = new int[7];
        cx[0] = r.X;
        cx[1] = cx[0] + wName; cx[2] = cx[1] + wPid; cx[3] = cx[2] + wCpu; cx[4] = cx[3] + wMem; cx[5] = cx[4] + wIo; cx[6] = cx[5] + wNet;
        string[] hd = { L.T("Назва"), "PID", L.T("ЦП"), L.T("Пам'ять"), L.T("Диск"), L.T("Мережа") };
        int hy = r.Y + Px(38), hh = Px(24);
        for (int c = 0; c < 6; c++)
        {
            Rectangle hr = new Rectangle(cx[c], hy, (c == 0 ? cx[1] - Px(132) : cx[c + 1]) - cx[c], hh);
            bool act = procSort == c;
            Rectangle tr = new Rectangle(hr.X + Px(c == 0 ? 28 : 4), hr.Y, hr.Width - Px(c == 0 ? 32 : 12), hr.Height);
            Color hc = act ? P.Acc : (Over(hr) ? P.Fg : P.Sub);
            if (c == 0) TxtL(g, hd[c], act ? FB : FS, tr, hc); else TxtR(g, hd[c], act ? FB : FS, tr, hc);
            if (act)
            {
                int ax = c == 0 ? hr.X + Px(14) : hr.X + Px(6), ay = hr.Y + hh / 2, s = Px(3);
                Point[] tri = procDesc ? new Point[] { new Point(ax - s, ay - s / 2), new Point(ax + s, ay - s / 2), new Point(ax, ay + s) }
                                       : new Point[] { new Point(ax - s, ay + s / 2), new Point(ax + s, ay + s / 2), new Point(ax, ay - s) };
                using (SolidBrush ab = new SolidBrush(P.Acc)) g.FillPolygon(ab, tri);
            }
            int col = c;
            Hot(hr, delegate { SortBy(col); });
        }

        // «Керує» - права частина колонки назви
        int wCtl = Px(132), xCtl = cx[1] - wCtl;
        {
            Rectangle hr = new Rectangle(xCtl, hy, wCtl, hh);
            bool act = procSort == 6;
            TxtL(g, L.T("Керує"), act ? FB : FS, new Rectangle(xCtl + Px(14), hy, wCtl - Px(14), hh), act ? P.Acc : (Over(hr) ? P.Fg : P.Sub));
            if (act)
            {
                int ax = xCtl + Px(6), ay = hy + hh / 2, s = Px(3);
                Point[] tri = procDesc ? new Point[] { new Point(ax - s, ay - s / 2), new Point(ax + s, ay - s / 2), new Point(ax, ay + s) }
                                       : new Point[] { new Point(ax - s, ay + s / 2), new Point(ax + s, ay + s / 2), new Point(ax, ay - s) };
                using (SolidBrush ab = new SolidBrush(P.Acc)) g.FillPolygon(ab, tri);
            }
            Hot(hr, delegate { SortBy(6); });
            Tip(hr, L.T("Хто запускає процес і стежить за ним: служба, автозапуск, планувальник завдань, користувач, інша програма чи сама Windows"));
        }

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
            Color sc = SecColor(ln.Sec);
            if (ln.Type == 0)
            {
                // заголовок розділу: колір, назва, кількість, підсумки
                if (hv) using (SolidBrush b = new SolidBrush(Util.Blend(P.Bg, P.Fg, 0.06f))) Util.FillRound(g, b, rr, Px(4));
                Tri(g, rr.X + Px(10), rr.Y + rowH / 2, ln.Open, sc);
                using (SolidBrush b = new SolidBrush(sc)) g.FillEllipse(b, new Rectangle(rr.X + Px(20), rr.Y + rowH / 2 - Px(4), Px(8), Px(8)));
                string ht = ln.Name + "  " + ln.Count;
                TxtL(g, ht, FB, new Rectangle(cx[0] + Px(34), rr.Y, wName - Px(36), rowH), sc);
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
            Color lc = LevelColor(ln.Level);
            Color stripe = ln.Level > 0 ? lc : sc;
            Color bg = Util.Blend(i % 2 == 0 ? P.Card : Util.Blend(P.Card, P.Bg, 0.5f), stripe, ln.Level > 0 ? 0.16f : 0.06f);
            if (isSel) bg = Util.Blend(P.Card, P.Acc, 0.30f);
            else if (hv) bg = Util.Blend(bg, P.Fg, 0.08f);
            Rectangle rb = Rectangle.Inflate(rr, 0, -1);
            using (SolidBrush b = new SolidBrush(bg)) Util.FillRound(g, b, rb, Px(4));
            using (SolidBrush b = new SolidBrush(stripe)) Util.FillRound(g, b, new Rectangle(rb.X, rb.Y + Px(3), Px(3), rb.Height - Px(6)), Px(1));
            int nx = cx[0] + Px(14) + (ln.Child ? Px(22) : 0);
            if (ln.Type == 1) Tri(g, nx + Px(4), rr.Y + rowH / 2, ln.Open, P.Sub);
            if (ln.Level > 0 && ln.Type == 2) using (SolidBrush b = new SolidBrush(lc)) g.FillEllipse(b, new Rectangle(nx, rr.Y + rowH / 2 - Px(4), Px(8), Px(8)));
            nx += Px(14);
            string nm = ln.Name.Length > 0 ? ln.Name : "—";
            int nameW = xCtl - nx - Px(4);
            TxtL(g, ln.Ctl, FS, new Rectangle(xCtl + Px(14), rr.Y, wCtl - Px(16), rowH), ln.Ctl == L.T("Автозапуск") ? KindColor(ProcRow.KApp) : P.Sub);
            if (ln.Type == 1)
            {
                int tw = Math.Min(nameW - Px(40), TextRenderer.MeasureText(nm, F).Width);
                TxtL(g, nm, F, new Rectangle(nx, rr.Y, tw + Px(4), rowH), P.Fg);
                TxtL(g, "×" + ln.Count, FS, new Rectangle(nx + tw + Px(6), rr.Y, Px(40), rowH), P.Sub);
            }
            else TxtL(g, nm, ln.Level > 0 ? FB : F, new Rectangle(nx, rr.Y, nameW, rowH), ln.Child ? P.Sub : P.Fg);
            if (p != null) TxtR(g, p.Pid.ToString(), F, new Rectangle(cx[1], rr.Y, wPid - Px(8), rowH), P.Sub);
            TxtR(g, CpuText(ln.Cpu), F, new Rectangle(cx[2], rr.Y, wCpu - Px(8), rowH), ln.Cpu >= 60 ? P.Warn : P.Fg);
            TxtR(g, Cleaner.Size(ln.Ws), F, new Rectangle(cx[3], rr.Y, wMem - Px(8), rowH), P.Fg);
            TxtR(g, Rate(ln.Io), F, new Rectangle(cx[4], rr.Y, wIo - Px(8), rowH), ln.Io >= 30 * 1048576.0 ? P.Warn : P.Fg);
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
                Hot(vis, delegate { selPid = selPid == pid ? 0 : pid; Invalidate(); });
                if (p.Why.Count > 0) Tip(vis, string.Join("\n", p.Why.ToArray()));
            }
        }
        g.Clip = old;
        if (lines.Count == 0)
            TxtC(g, onlyOdd ? L.T("Аномальної поведінки не помічено.") : L.T("Збираю дані…"), F, list, P.Sub);
        Wheel(list, delegate (int d) { procScroll -= d * rowH * 3; });
        VBar(g, new Rectangle(list.Right - Px(6), list.Y, Px(4), list.Height), content, list.Height, procScroll, delegate (int v) { procScroll = v; Invalidate(); });

        if (sel != null) PaintProcDetail(g, new Rectangle(r.X, r.Bottom - detH, r.Width, detH), sel);
        else if (selPid != 0) selPid = 0;
    }

    const int SvcMax = 3;

    int DetailH(ProcRow p)
    {
        int n = Math.Min(p.Svcs.Count, SvcMax);
        return Px(42) + Px(19) * (6 + Math.Min(p.Why.Count, 4)) + (n > 0 ? Px(8) + Px(20) + n * Px(32) + (p.Svcs.Count > SvcMax ? Px(18) : 0) : 0) + Px(10);
    }

    void PaintProcDetail(Graphics g, Rectangle d, ProcRow p)
    {
        Card(g, d);
        int pad = Px(12), lh = Px(19);
        Color kc = p.Level > 0 ? LevelColor(p.Level) : KindColor(p.Kind);
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
        // «Завершити» - червона, як інші небезпечні дії
        {
            bool h = !actBusy && Over(bEnd);
            using (SolidBrush b = new SolidBrush(Util.Blend(P.Card, P.Bad, actBusy ? 0.08f : (h ? 0.32f : 0.18f)))) Util.FillRound(g, b, bEnd, Math.Min(P.Rad, bh / 2));
            TxtC(g, L.T("Завершити"), FB, bEnd, actBusy ? Util.Blend(P.Card, P.Bad, 0.5f) : P.Bad);
            if (!actBusy) Hot(bEnd, delegate { AskEnd(ppid, false); });
        }
        Btn(g, bRe, L.T("Перезапустити"), delegate { AskEnd(ppid, true); }, 0, can && (has || p.Svcs.Count > 0));
        Btn(g, bDir, L.T("Показати теку"), delegate
        {
            try { if (File.Exists(pth)) Process.Start("explorer.exe", "/select,\"" + pth + "\""); } catch { }
        }, 0, has && File.Exists(pth));
        Btn(g, bCopy, okC ? L.T("Скопійовано") : L.T("Копіювати шлях"), delegate
        {
            try { Clipboard.SetText(pth); procCopied = "p" + ppid; procCopiedAt = DateTime.Now; Invalidate(); } catch { }
        }, 0, has);
        Tip(bEnd, p.Svcs.Count > 0 ? L.T("Зупинити служби процесу (разом із залежними) і завершити його") : L.T("Завершити разом із пов'язаними процесами - перед цим буде список і підтвердження"));
        Tip(bRe, p.Svcs.Count > 0 ? L.T("Перезапустити служби процесу") : L.T("Завершити й запустити знову з тими самими параметрами"));
        TxtL(g, p.Name + "  ·  PID " + p.Pid + "  ·  " + KindName(p.Kind), FB, new Rectangle(d.X + pad, d.Y + Px(9), bEnd.X - d.X - pad - Px(8), Px(24)), kc);

        int y = d.Y + Px(42);
        int lw = Px(92);
        int full = d.Width - pad * 2;
        ProcRow par = Find(p.Parent);
        string sig;
        if (!has) sig = ProcMon.Quiet ? L.T("не збирається (працює античіт)") : L.T("немає доступу до шляху");
        else if (!p.SigDone) sig = L.T("перевіряється…");
        else if (p.SigKind == Sig.Ok) sig = p.Signer.Length > 0 ? L.F("підписаний: {0}", p.Signer) : L.T("підписаний");
        else if (p.SigKind == Sig.Bad) sig = L.T("підпис недійсний");
        else if (p.SigKind == Sig.Expired) sig = p.Signer.Length > 0 ? L.F("підпис прострочений: {0}", p.Signer) : L.T("підпис прострочений");
        else sig = L.T("без підпису");

        string file = has ? (p.Exists ? p.Path : L.F("{0} (видалено)", p.Path)) : "—";
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
        int n = 0;
        foreach (string w in p.Why)
        {
            if (n++ >= 4) break;
            Rectangle wr = new Rectangle(d.X + pad, y, full, lh);
            TxtL(g, "• " + w, FS, wr, LevelColor(p.Level));
            Tip(wr, w);
            y += lh;
        }
        if (p.Svcs.Count > 0) PaintSvcs(g, new Rectangle(d.X + pad, y + Px(8), full, d.Bottom - y - Px(8)), p);
    }

    // служби в процесі: стан, тип запуску, зупинка / запуск
    void PaintSvcs(Graphics g, Rectangle r, ProcRow p)
    {
        using (Pen pn = new Pen(P.Line, 1f)) g.DrawLine(pn, r.X, r.Y - Px(4), r.Right, r.Y - Px(4));
        TxtL(g, p.Svcs.Count == 1 ? L.T("Служба в цьому процесі") : L.F("Служби в цьому процесі: {0}", p.Svcs.Count), FS, new Rectangle(r.X, r.Y, r.Width, Px(20)), P.Sub);
        int y = r.Y + Px(20);
        string[] modes = { L.T("Вручну"), L.T("Автоматично"), L.T("Із затримкою"), L.T("Вимкнено") };
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
            TxtL(g, run ? L.T("працює") : (s.State == 2 ? L.T("запускається") : (s.State == 3 ? L.T("зупиняється") : L.T("зупинена"))), FS, st, run ? P.Good : P.Warn);
            bool crit = ProcAct.IsCriticalSvc(s.Name);
            string sn = s.Name, sd = s.Display;
            if (crit)
            {
                TxtL(g, L.T("ключова служба Windows - не змінюється"), FS, new Rectangle(chipsX, rowY, cw * 4 + cg * 3 + Px(10) + bw, Px(28)), P.Sub);
            }
            else
            {
                int mode = ModeCached(s.Name);
                for (int k = 0; k < 4; k++)
                {
                    int kk = k;
                    Rectangle cr = new Rectangle(chipsX + k * (cw + cg), rowY, cw, Px(28));
                    Chip(g, cr, modes[k], mode == k, delegate { if (!actBusy) AskMode(sn, sd, kk); });
                }
                if (mode < 0) Tip(new Rectangle(chipsX, rowY, cw * 4 + cg * 3, Px(28)), L.T("Особливий тип запуску (завантажувальна чи системна служба)"));
                Btn(g, btn, run ? L.T("Зупинити") : L.T("Запустити"), delegate { if (run) AskSvcStop(sn, sd); else RunAct(delegate { string e = ProcAct.SvcStart(sn); return e.Length > 0 ? "!" + e : L.F("Службу «{0}» запущено.", sd); }); }, 0, !actBusy);
            }
            y += Px(32);
        }
        if (p.Svcs.Count > SvcMax) TxtL(g, L.F("і ще {0} - див. «Служби» Windows", p.Svcs.Count - SvcMax), FS, new Rectangle(r.X, y, r.Width, Px(18)), P.Sub);
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
                    ProcMon.Sample();
                    RebuildView();
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
        if (restart) RunAct(delegate { string res = ProcAct.Restart(k); return res; });
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

    void AskMode(string name, string display, int mode)
    {
        string[] names = { L.T("вручну"), L.T("автоматично"), L.T("автоматично із затримкою"), L.T("вимкнено") };
        string path = "";
        foreach (ProcRow r in ProcMon.Rows) foreach (SvcInfo s in r.Svcs) if (s.Name == name) path = r.Path;
        bool win = path.Length == 0 || path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase);
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
}
