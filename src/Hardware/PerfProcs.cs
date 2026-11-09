using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
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
        public string Name = "", Key = "";
        public float Cpu;
        public long Ws;
        public double Io;
        public bool Child, Open;
    }

    const int SecOdd = 0;                // розділи: 0 - аномалії, далі 1 + ProcRow.Kind
    int procScroll, procSort, selPid;    // сортування: 0 назва, 1 PID, 2 ЦП, 3 пам'ять, 4 диск, 5 мережа
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
        l.Cpu = r.Cpu; l.Ws = r.Ws; l.Io = r.Io; l.Net = r.NetEst; l.Level = r.Level;
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
        TxtL(g, sum, F, new Rectangle(r.X + Px(4), r.Y + Px(3), chipB.X - r.X - Px(12), Px(28)), P.Sub);

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
            Rectangle hr = new Rectangle(cx[c], hy, cx[c + 1] - cx[c], hh);
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
            int nameW = cx[1] - nx - Px(4);
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

    int DetailH(ProcRow p) { return Px(40) + Px(19) * (6 + Math.Min(p.Why.Count, 4)) + Px(10); }

    void PaintProcDetail(Graphics g, Rectangle d, ProcRow p)
    {
        Card(g, d);
        int pad = Px(12), lh = Px(19);
        Color kc = p.Level > 0 ? LevelColor(p.Level) : KindColor(p.Kind);
        using (SolidBrush b = new SolidBrush(kc)) Util.FillRound(g, b, new Rectangle(d.X, d.Y + Px(10), Px(4), Px(22)), Px(2));
        // кнопки справа
        int bw = Px(140), bh = Px(28);
        Rectangle bCopy = new Rectangle(d.Right - pad - bw, d.Y + Px(7), bw, bh);
        Rectangle bDir = new Rectangle(bCopy.X - Px(8) - bw, bCopy.Y, bw, bh);
        bool has = p.Path.Length > 0;
        bool okC = procCopied == "p" + p.Pid && (DateTime.Now - procCopiedAt).TotalMilliseconds < 1600;
        string pth = p.Path;
        int ppid = p.Pid;
        Btn(g, bDir, L.T("Показати теку"), delegate
        {
            try { if (File.Exists(pth)) Process.Start("explorer.exe", "/select,\"" + pth + "\""); } catch { }
        }, 0, has && File.Exists(pth));
        Btn(g, bCopy, okC ? L.T("Скопійовано") : L.T("Копіювати шлях"), delegate
        {
            try { Clipboard.SetText(pth); procCopied = "p" + ppid; procCopiedAt = DateTime.Now; Invalidate(); } catch { }
        }, 0, has);
        TxtL(g, p.Name + "  ·  PID " + p.Pid + "  ·  " + KindName(p.Kind), FB, new Rectangle(d.X + pad, d.Y + Px(8), bDir.X - d.X - pad * 2, Px(24)), kc);

        int y = d.Y + Px(40);
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

        // на всю ширину: файл і команда
        string file = has ? (p.Exists ? p.Path : L.F("{0} (видалено)", p.Path)) : "—";
        Field(g, new Rectangle(d.X + pad, y, full, lh), lw, L.T("Файл"), file); y += lh;
        Field(g, new Rectangle(d.X + pad, y, full, lh), lw, L.T("Команда"), p.Cmd.Length > 0 ? p.Cmd : "—"); y += lh;
        // дві колонки: походження ліворуч, ресурси праворуч
        int half = (full - Px(16)) / 2;
        int x2 = d.X + pad + half + Px(16);
        string[][] left =
        {
            new string[] { L.T("Запустив"), par != null ? par.Name + " (PID " + par.Pid + ")" : (p.Parent > 0 ? L.F("PID {0} (завершився)", p.Parent) : "—") },
            new string[] { L.T("Старт"), p.Created > 0 ? p.Started.ToString("dd.MM.yyyy HH:mm:ss") : "—" },
            new string[] { L.T("Підпис"), sig },
            new string[] { L.T("Сеанс"), p.Session == 0 ? L.T("0 (системний)") : p.Session.ToString() }
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
