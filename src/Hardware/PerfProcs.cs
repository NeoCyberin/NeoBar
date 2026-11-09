using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

// ======================================================================
//  Вкладка «Процеси» монітора системи: розширений диспетчер завдань.
//  Лише показ (нічого не завершує й не блокує); аномалії виділені кольором, причину видно в підказці й у деталях.
// ======================================================================
sealed partial class PerfWindow
{
    int procScroll, procSort = -1, selPid;           // -1: спершу аномалії, потім за ЦП
    bool procDesc = true, onlyOdd;
    List<ProcRow> view = new List<ProcRow>();
    int procTick;
    string procCopied = "";
    DateTime procCopiedAt = DateTime.MinValue;

    void ResizeFor(int w)
    {
        int nw = Px(w);
        if (ClientSize.Width == nw) return;
        int d = nw - ClientSize.Width;
        ClientSize = new Size(nw, ClientSize.Height);
        try
        {
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            Left = Math.Max(wa.Left, Math.Min(wa.Right - Width, Left - d / 2));
        }
        catch { }
    }

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

    void RebuildView()
    {
        List<ProcRow> src = ProcMon.Rows;
        List<ProcRow> l = new List<ProcRow>(src.Count);
        foreach (ProcRow r in src) if (!onlyOdd || r.Level > 0) l.Add(r);
        int sort = procSort, dir = procDesc ? -1 : 1;
        l.Sort(delegate (ProcRow a, ProcRow b)
        {
            int c = 0;
            switch (sort)
            {
                case 0: c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); break;
                case 1: c = a.Pid.CompareTo(b.Pid); break;
                case 2: c = a.Cpu.CompareTo(b.Cpu); break;
                case 3: c = a.Ws.CompareTo(b.Ws); break;
                case 4: c = a.Io.CompareTo(b.Io); break;
                case 5: c = (a.NetEst + a.NetListen).CompareTo(b.NetEst + b.NetListen); break;
                default:
                    c = a.Level.CompareTo(b.Level);
                    if (c == 0) c = a.Cpu.CompareTo(b.Cpu);
                    dir = -1;
                    break;
            }
            if (c == 0) c = a.Pid.CompareTo(b.Pid);
            return c * dir;
        });
        view = l;
    }

    void SortBy(int col)
    {
        if (procSort == col) procDesc = !procDesc;
        else { procSort = col; procDesc = col != 0 && col != 1; }
        procScroll = 0;
        RebuildView();
        Invalidate();
    }

    static ProcRow Find(int pid)
    {
        foreach (ProcRow r in ProcMon.Rows) if (r.Pid == pid) return r;
        return null;
    }

    Color LevelColor(int lev) { return lev >= 2 ? P.Bad : (lev == 1 ? P.Warn : P.Sub); }

    static string Rate(double b)
    {
        if (b < 1024) return b < 1 ? "—" : Math.Round(b) + L.T(" Б/с");
        if (b < 1048576) return (b / 1024.0).ToString("0") + L.T(" КБ/с");
        return (b / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture) + L.T(" МБ/с");
    }

    void PaintProcs(Graphics g, Rectangle r)
    {
        if (ProcMon.Error.Length > 0) { TxtC(g, ProcMon.Error, F, r, P.Sub); return; }
        int odd = 0, total = ProcMon.Rows.Count, thr = 0;
        foreach (ProcRow p in ProcMon.Rows) { if (p.Level > 0) odd++; thr += p.Threads; }

        // верхній рядок: підсумок і фільтр
        int topH = Px(34);
        Rectangle chipA = new Rectangle(r.Right - Px(120), r.Y + Px(3), Px(120), Px(28));
        Rectangle chipB = new Rectangle(chipA.X - Px(8) - Px(110), chipA.Y, Px(110), Px(28));
        Chip(g, chipB, L.T("Усі"), !onlyOdd, delegate { onlyOdd = false; procScroll = 0; RebuildView(); Invalidate(); });
        Chip(g, chipA, L.T("Аномалії") + (odd > 0 ? " · " + odd : ""), onlyOdd, delegate { onlyOdd = true; procScroll = 0; RebuildView(); Invalidate(); });
        string sum = float.IsNaN(Sensors.Cpu) ? L.F("Процесів: {0} · потоків: {1}", total, thr) : L.F("Процесів: {0} · потоків: {1} · ЦП: {2}%", total, thr, Math.Round(Sensors.Cpu));
        TxtL(g, sum, F, new Rectangle(r.X + Px(4), r.Y + Px(3), chipB.X - r.X - Px(12), Px(28)), P.Sub);

        // колонки
        int wPid = Px(62), wCpu = Px(62), wMem = Px(92), wIo = Px(92), wNet = Px(66), sb = Px(10);
        int wName = r.Width - wPid - wCpu - wMem - wIo - wNet - sb;
        int[] cx = new int[7];
        cx[0] = r.X;
        cx[1] = cx[0] + wName; cx[2] = cx[1] + wPid; cx[3] = cx[2] + wCpu; cx[4] = cx[3] + wMem; cx[5] = cx[4] + wIo; cx[6] = cx[5] + wNet;
        string[] hd = { L.T("Назва"), "PID", L.T("ЦП"), L.T("Пам'ять"), L.T("Диск"), L.T("Мережа") };
        int hy = r.Y + topH, hh = Px(24);
        for (int c = 0; c < 6; c++)
        {
            Rectangle hr = new Rectangle(cx[c], hy, cx[c + 1] - cx[c], hh);
            bool act = procSort == c;
            Rectangle tr = new Rectangle(hr.X + Px(c == 0 ? 24 : 4), hr.Y, hr.Width - Px(c == 0 ? 28 : 12), hr.Height);
            if (c == 0) TxtL(g, hd[c], act ? FB : FS, tr, act ? P.Acc : P.Sub); else TxtR(g, hd[c], act ? FB : FS, tr, act ? P.Acc : P.Sub);
            if (act)
            {
                int ax = c == 0 ? hr.X + Px(12) : hr.X + Px(2) + Px(2), ay = hr.Y + hh / 2, s = Px(3);
                Point[] tri = procDesc ? new Point[] { new Point(ax - s, ay - s / 2), new Point(ax + s, ay - s / 2), new Point(ax, ay + s) }
                                       : new Point[] { new Point(ax - s, ay + s / 2), new Point(ax + s, ay + s / 2), new Point(ax, ay - s) };
                using (SolidBrush ab = new SolidBrush(P.Acc)) g.FillPolygon(ab, tri);
            }
            int col = c;
            Hot(hr, delegate { SortBy(col); });
            Tip(hr, L.T("Натисніть, щоб відсортувати"));
        }

        // деталі вибраного процесу
        ProcRow sel = selPid != 0 ? Find(selPid) : null;
        int detH = 0;
        if (sel != null) detH = Px(132) + Math.Min(sel.Why.Count, 5) * Px(18);
        int ly = hy + hh + Px(2);
        Rectangle list = new Rectangle(r.X, ly, r.Width, r.Bottom - ly - (detH > 0 ? detH + Px(6) : 0));

        if (ProcMon.Quiet)
        {
            // античіт: шляхів і підписів немає - це не помилка
            int nh = Px(20);
            TxtL(g, L.F("Працює античіт ({0}) - шляхи та підписи тимчасово не збираються.", ProcMon.QuietName), FS, new Rectangle(r.X + Px(4), list.Y, r.Width - Px(8), nh), P.Warn);
            list = new Rectangle(list.X, list.Y + nh, list.Width, list.Height - nh);
        }

        int rowH = Px(25);
        int content = view.Count * rowH;
        procScroll = Math.Max(0, Math.Min(procScroll, Math.Max(0, content - list.Height)));
        Region old = g.Clip;
        g.SetClip(list);
        int y = list.Y - procScroll;
        int i0 = Math.Max(0, procScroll / rowH);
        y += i0 * rowH;
        for (int i = i0; i < view.Count && y < list.Bottom; i++, y += rowH)
        {
            ProcRow p = view[i];
            Rectangle rr = new Rectangle(r.X, y, r.Width - sb, rowH);
            bool isSel = p.Pid == selPid, hv = Over(Rectangle.Intersect(rr, list));
            Color lc = LevelColor(p.Level);
            Color bg = p.Level > 0 ? Util.Blend(P.Card, lc, isSel ? 0.30f : (hv ? 0.24f : 0.16f)) : (isSel ? Util.Blend(P.Card, P.Acc, 0.28f) : (hv ? Util.Blend(P.Card, P.Fg, 0.10f) : (i % 2 == 0 ? P.Card : Util.Blend(P.Card, P.Bg, 0.5f))));
            using (SolidBrush b = new SolidBrush(bg)) Util.FillRound(g, b, Rectangle.Inflate(rr, 0, -1), Px(4));
            if (p.Level > 0)
                using (SolidBrush b = new SolidBrush(lc)) g.FillEllipse(b, new Rectangle(rr.X + Px(8), rr.Y + rowH / 2 - Px(4), Px(8), Px(8)));
            string nm = p.Name.Length > 0 ? p.Name : "—";
            TxtL(g, nm, p.Level > 0 ? FB : F, new Rectangle(cx[0] + Px(24), rr.Y, wName - Px(28), rowH), P.Fg);
            TxtR(g, p.Pid.ToString(), F, new Rectangle(cx[1], rr.Y, wPid - Px(8), rowH), P.Sub);
            TxtR(g, p.Cpu < 0.05f ? "—" : p.Cpu.ToString(p.Cpu < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + "%", F, new Rectangle(cx[2], rr.Y, wCpu - Px(8), rowH), p.Cpu >= 60 ? P.Warn : P.Fg);
            TxtR(g, Cleaner.Size(p.Ws), F, new Rectangle(cx[3], rr.Y, wMem - Px(8), rowH), P.Fg);
            TxtR(g, Rate(p.Io), F, new Rectangle(cx[4], rr.Y, wIo - Px(8), rowH), p.Io >= 30 * 1048576.0 ? P.Warn : P.Fg);
            TxtR(g, p.NetEst + p.NetListen == 0 ? "—" : p.NetEst.ToString(), F, new Rectangle(cx[5], rr.Y, wNet - Px(8), rowH), P.Fg);
            Rectangle vis = Rectangle.Intersect(rr, list);
            if (!vis.IsEmpty)
            {
                int pid = p.Pid;
                Hot(vis, delegate { selPid = selPid == pid ? 0 : pid; Invalidate(); });
                if (p.Why.Count > 0) Tip(vis, string.Join("\n", p.Why.ToArray()));
            }
        }
        g.Clip = old;
        if (view.Count == 0)
            TxtC(g, onlyOdd ? L.T("Аномальної поведінки не помічено.") : L.T("Збираю дані…"), F, list, P.Sub);
        Wheel(list, delegate (int d) { procScroll -= d * rowH * 3; });
        VBar(g, new Rectangle(list.Right - Px(6), list.Y, Px(4), list.Height), content, list.Height, procScroll, delegate (int v) { procScroll = v; Invalidate(); });

        if (sel != null) PaintProcDetail(g, new Rectangle(r.X, r.Bottom - detH, r.Width, detH), sel);
        else if (selPid != 0) selPid = 0;
    }

    void PaintProcDetail(Graphics g, Rectangle d, ProcRow p)
    {
        Card(g, d);
        int pad = Px(12), lh = Px(18);
        Rectangle ri = new Rectangle(d.X + pad, d.Y + Px(8), d.Width - pad * 2, lh);
        TxtL(g, p.Name + "  ·  PID " + p.Pid, FB, ri, p.Level > 0 ? LevelColor(p.Level) : P.Fg);
        // кнопки справа
        int bw = Px(130), bh = Px(26);
        Rectangle bCopy = new Rectangle(d.Right - pad - bw, d.Y + Px(6), bw, bh);
        Rectangle bDir = new Rectangle(bCopy.X - Px(8) - bw, bCopy.Y, bw, bh);
        bool has = p.Path.Length > 0;
        bool okC = procCopied == "p" + p.Pid && (DateTime.Now - procCopiedAt).TotalMilliseconds < 1600;
        string pth = p.Path;
        Btn(g, bDir, L.T("Показати теку"), delegate
        {
            try { if (File.Exists(pth)) Process.Start("explorer.exe", "/select,\"" + pth + "\""); } catch { }
        }, 0, has && File.Exists(pth));
        Btn(g, bCopy, okC ? L.T("Скопійовано") : L.T("Копіювати шлях"), delegate
        {
            try { Clipboard.SetText(pth); procCopied = "p" + p.Pid; procCopiedAt = DateTime.Now; Invalidate(); } catch { }
        }, 0, has);
        ri.Y = d.Y + Px(38);
        ri.Height = lh;
        int lw = Px(110);
        ProcRow par = Find(p.Parent);
        string sig;
        if (!has) sig = ProcMon.Quiet ? L.T("не збирається (працює античіт)") : L.T("немає доступу до шляху");
        else if (!p.SigDone) sig = L.T("перевіряється…");
        else if (p.SigKind == Sig.Ok) sig = p.Signer.Length > 0 ? L.F("підписаний: {0}", p.Signer) : L.T("підписаний");
        else if (p.SigKind == Sig.Bad) sig = L.T("підпис недійсний");
        else if (p.SigKind == Sig.Expired) sig = p.Signer.Length > 0 ? L.F("підпис прострочений: {0}", p.Signer) : L.T("підпис прострочений");
        else sig = L.T("без підпису");
        string[][] rows = new string[][]
        {
            new string[] { L.T("Файл"), has ? (p.Exists ? p.Path : L.F("{0} (видалено)", p.Path)) : "—" },
            new string[] { L.T("Команда"), p.Cmd.Length > 0 ? p.Cmd : "—" },
            new string[] { L.T("Запустив"), par != null ? par.Name + " (PID " + par.Pid + ")" : (p.Parent > 0 ? L.F("PID {0} (завершився)", p.Parent) : "—") },
            new string[] { L.T("Старт"), p.Created > 0 ? p.Started.ToString("dd.MM.yyyy HH:mm:ss") : "—" },
            new string[] { L.T("Підпис"), sig },
            new string[] { L.T("Ресурси"), L.F("дескрипторів {0}, потоків {1}, виділено {2}, з'єднань {3}, портів {4}", p.Handles, p.Threads, Cleaner.Size(p.Commit), p.NetEst, p.NetListen) }
        };
        foreach (string[] row in rows)
        {
            TxtL(g, row[0], FS, new Rectangle(ri.X, ri.Y, lw, lh), P.Sub);
            Rectangle vr = new Rectangle(ri.X + lw, ri.Y, ri.Width - lw, lh);
            TxtL(g, row[1], F, vr, P.Fg);
            if (row[1].Length > 60) Tip(vr, row[1]);
            ri.Y += lh;
        }
        int n = 0;
        foreach (string w in p.Why)
        {
            if (n++ >= 5) break;
            TxtL(g, "• " + w, FS, new Rectangle(ri.X, ri.Y, ri.Width, lh), LevelColor(p.Level));
            Tip(new Rectangle(ri.X, ri.Y, ri.Width, lh), w);
            ri.Y += lh;
        }
    }
}
