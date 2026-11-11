using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

// ======================================================================
//  Монітор системи → «Аналіз»: навантаження вибраного процесу протягом часу.
//  Процес вибирається у «Завданнях» (кнопка «Аналіз» у картці процесу) - посередині з'являється вікно запису:
//  інтервал, куди писати журнал, «Почати». Під час запису - живий графік; потім програвач: повзунок часу,
//  відтворення з прискоренням, значення в будь-який момент і підсумки (мін, макс, середнє, 95 % часу).
// ======================================================================
sealed partial class PerfWindow
{
    const int TabAnalysis = 3;
    int anMetric;
    double anHead = -1;             // позиція програвача (номер заміру); -1 - кінець (наживо)
    bool anPlay, anSetup;
    float anSpeed = 10f;
    System.Windows.Forms.Timer anTimer;
    DateTime anLast;
    int anPid, anInterval = 1000;
    string anName = "", anPath = "", anLog = "", anMsg = "";
    bool anMsgBad;
    List<ProcSample> anData = new List<ProcSample>();
    ProcRecorder.Stat[] anStats;
    int anStatsN = -1;
    bool anHooked;

    static readonly int[] AnIntervals = { 500, 1000, 2000, 5000, 10000 };

    // Esc закриває вікно запису, а не весь монітор
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (anSetup && (keyData & Keys.KeyCode) == Keys.Escape) { anSetup = false; anMsg = ""; Invalidate(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void EnterAnalysis()
    {
        if (!anHooked) { ProcRecorder.Changed += OnRecData; anHooked = true; }
        AnReload();
    }

    void OnRecData()
    {
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(delegate { if (Tab == TabAnalysis) { AnReload(); Invalidate(); } })); }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    void AnReload()
    {
        anData = ProcRecorder.Snapshot();
        if (anData.Count != anStatsN) { anStats = ProcRecorder.Stats(anData); anStatsN = anData.Count; }
    }

    void LeaveAnalysis()
    {
        if (anHooked) { ProcRecorder.Changed -= OnRecData; anHooked = false; }
        StopPlay();
    }

    // з картки процесу в «Завданнях»
    void AnalyzeProcess(ProcRow p)
    {
        if (p == null) return;
        anPid = p.Pid; anName = p.Name; anPath = p.Path;
        anLog = ProcRecorder.DefaultLog(p.Name);
        anMsg = "";
        anSetup = true;
        Tab = TabAnalysis;
        OnTab();
        Invalidate();
    }

    void StartRec()
    {
        string e = ProcRecorder.Start(anPid, anName, anPath, anInterval, anLog);
        if (e.Length > 0) { anMsg = e; anMsgBad = true; Invalidate(); return; }
        anSetup = false;
        anHead = -1;
        anMsg = ""; anMsgBad = false;
        StopPlay();
        AnReload();
        Invalidate();
    }

    void PickLog()
    {
        using (SaveFileDialog d = new SaveFileDialog())
        {
            d.Filter = L.T("Журнал CSV (*.csv)|*.csv");
            d.FileName = Path.GetFileName(anLog);
            try { d.InitialDirectory = Path.GetDirectoryName(anLog); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            d.OverwritePrompt = true;
            if (d.ShowDialog(this) == DialogResult.OK) anLog = d.FileName;
        }
        Invalidate();
    }

    void OpenRec()
    {
        using (OpenFileDialog d = new OpenFileDialog())
        {
            d.Filter = L.T("Журнал CSV (*.csv)|*.csv");
            try { d.InitialDirectory = Path.GetDirectoryName(ProcRecorder.DefaultLog("x")); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            if (d.ShowDialog(this) != DialogResult.OK) return;
            string e = ProcRecorder.Load(d.FileName);
            anMsg = e; anMsgBad = e.Length > 0;
            anHead = 0;
            AnReload();
            Invalidate();
        }
    }

    // ---------- програвач ----------
    void TogglePlay()
    {
        if (anPlay) { StopPlay(); Invalidate(); return; }
        if (anData.Count < 2) return;
        if (anHead < 0 || anHead >= anData.Count - 1) anHead = 0;
        anPlay = true;
        anLast = DateTime.UtcNow;
        if (anTimer == null)
        {
            anTimer = new System.Windows.Forms.Timer();
            anTimer.Interval = 40;
            anTimer.Tick += delegate { PlayStep(); };
        }
        anTimer.Start();
        Invalidate();
    }

    void StopPlay()
    {
        anPlay = false;
        if (anTimer != null) anTimer.Stop();
    }

    void PlayStep()
    {
        if (!anPlay || anData.Count < 2) { StopPlay(); return; }
        DateTime now = DateTime.UtcNow;
        double dt = (now - anLast).TotalSeconds;
        anLast = now;
        double step = Math.Max(0.05, (anData[anData.Count - 1].Sec - anData[0].Sec) / (anData.Count - 1));
        anHead += dt * anSpeed / step;
        if (anHead >= anData.Count - 1) { anHead = anData.Count - 1; StopPlay(); }
        Invalidate();
    }

    int HeadIndex { get { return anData.Count == 0 ? -1 : (anHead < 0 ? anData.Count - 1 : Math.Max(0, Math.Min(anData.Count - 1, (int)Math.Round(anHead)))); } }

    static string Dur(double sec)
    {
        TimeSpan t = TimeSpan.FromSeconds(Math.Max(0, sec));
        return t.TotalHours >= 1 ? ((int)t.TotalHours).ToString("0") + ":" + t.ToString("mm\\:ss") : t.ToString("mm\\:ss");
    }

    // ---------- малювання ----------
    void PaintAnalysis(Graphics g, Rectangle r)
    {
        if (anData.Count != ProcRecorder.Samples) AnReload();
        bool rec = ProcRecorder.Recording;
        int y = r.Y;

        // заголовок: процес, стан, дії
        Rectangle top = new Rectangle(r.X, y, r.Width, Px(64));
        Card(g, top);
        int bw = Px(150), bh = Px(32);
        Rectangle b1 = new Rectangle(top.Right - Px(12) - bw, top.Y + (top.Height - bh) / 2, bw, bh);
        Rectangle b2 = new Rectangle(b1.X - Px(8) - bw, b1.Y, bw, bh);
        Rectangle b3 = new Rectangle(b2.X - Px(8) - Px(110), b1.Y, Px(110), bh);
        string title, sub;
        if (anData.Count == 0 && !rec)
        {
            title = L.T("Аналіз навантаження процесу");
            sub = L.T("Виберіть процес у «Завданнях» і натисніть «Аналіз» - або відкрийте збережений журнал.");
        }
        else
        {
            title = ProcRecorder.FromFile ? ProcRecorder.ProcName : L.F("{0}  ·  PID {1}", ProcRecorder.ProcName, ProcRecorder.Pid);
            double len = anData.Count > 0 ? anData[anData.Count - 1].Sec : 0;
            sub = (rec ? L.T("Запис") : (ProcRecorder.FromFile ? L.T("З файлу") : L.T("Зупинено"))) + "  ·  " + Dur(len) + "  ·  " +
                  L.F("замірів: {0}", anData.Count) + (ProcRecorder.Note.Length > 0 && !rec ? "  ·  " + ProcRecorder.Note : "");
        }
        if (rec)
        {
            using (SolidBrush b = new SolidBrush(P.Bad)) g.FillEllipse(b, new Rectangle(top.X + Px(14), top.Y + Px(14), Px(10), Px(10)));
            TxtL(g, title, FB, new Rectangle(top.X + Px(30), top.Y + Px(6), b3.X - top.X - Px(40), Px(26)), P.Fg);
        }
        else TxtL(g, title, FB, new Rectangle(top.X + Px(14), top.Y + Px(6), b3.X - top.X - Px(24), Px(26)), P.Fg);
        TxtL(g, sub, FS, new Rectangle(top.X + Px(14), top.Y + Px(32), b3.X - top.X - Px(24), Px(22)), P.Sub);
        if (rec) DangerBtn(g, b1, L.T("Зупинити запис"), delegate { ProcRecorder.Stop(null); AnReload(); Invalidate(); }, true);
        else Btn(g, b1, L.T("Новий запис…"), delegate
        {
            if (anPid == 0) { Tab = 2; OnTab(); Invalidate(); return; }      // процес ще не вибрано - до «Завдань»
            anLog = ProcRecorder.DefaultLog(anName); anSetup = true; Invalidate();
        }, 1, true);
        Btn(g, b2, L.T("Відкрити журнал…"), OpenRec, 0, !rec);
        string lp = ProcRecorder.LogPath;
        Btn(g, b3, L.T("Тека"), delegate { try { if (File.Exists(lp)) Process.Start("explorer.exe", "/select,\"" + lp + "\""); } catch (Exception skipEx) { SbLog.Skip(skipEx); } }, 0, lp.Length > 0 && File.Exists(lp));
        Tip(b3, lp);
        y = top.Bottom + Px(10);

        // показники
        int cx = r.X;
        for (int i = 0; i < ProcRecorder.Count; i++)
        {
            string t = ProcRecorder.Short(i);
            int w = TextRenderer.MeasureText(t, FS).Width + Px(22);
            int k = i;
            Chip(g, new Rectangle(cx, y, w, Px(28)), t, anMetric == i, delegate { anMetric = k; Invalidate(); });
            cx += w + Px(6);
        }
        y += Px(38);

        // графік
        int statsH = Px(26) + ProcRecorder.Count * Px(22) + Px(10);
        int playH = Px(40);
        Rectangle gr = new Rectangle(r.X, y, r.Width, Math.Max(Px(120), r.Bottom - y - statsH - playH - Px(16) - (anMsg.Length > 0 ? Px(22) : 0)));
        PaintAnGraph(g, gr);
        y = gr.Bottom + Px(8);

        // програвач
        PaintPlayer(g, new Rectangle(r.X, y, r.Width, playH), rec);
        y += playH + Px(8);
        if (anMsg.Length > 0) { TxtL(g, anMsg, FS, new Rectangle(r.X, y, r.Width, Px(20)), anMsgBad ? P.Bad : P.Sub); y += Px(22); }

        PaintAnStats(g, new Rectangle(r.X, y, r.Width, r.Bottom - y));

        if (anSetup) PaintSetup(g, r);
    }

    void PaintAnGraph(Graphics g, Rectangle gr)
    {
        Card(g, gr);
        Rectangle pl = new Rectangle(gr.X + Px(64), gr.Y + Px(12), gr.Width - Px(76), gr.Height - Px(34));
        int n = anData.Count;
        if (n < 2)
        {
            TxtC(g, ProcRecorder.Recording ? L.T("Збираю перші заміри…") : L.T("Тут буде графік навантаження"), F, pl, P.Sub);
            return;
        }
        int m = anMetric;
        double mx = 0;
        foreach (ProcSample s in anData) if (s.V[m] > mx) mx = s.V[m];
        if (m == 0) mx = Math.Max(mx, 5);
        if (mx <= 0) mx = 1;
        mx *= 1.1;
        using (Pen gl = new Pen(Util.Blend(P.Card, P.Fg, 0.1f), 1f))
            for (int i = 0; i <= 4; i++)
            {
                int yy = pl.Bottom - pl.Height * i / 4;
                g.DrawLine(gl, pl.X, yy, pl.Right, yy);
                TxtR(g, ProcRecorder.Fmt(m, mx * i / 4), FS, new Rectangle(gr.X + Px(2), yy - Px(9), Px(58), Px(18)), P.Sub);
            }
        double t0 = anData[0].Sec, t1 = anData[n - 1].Sec, span = Math.Max(0.001, t1 - t0);
        // по стовпчику пікселів: смуга мін-макс і лінія середнього (довгий запис не малює тисячі точок)
        int cols = Math.Max(2, pl.Width);
        PointF[] avg = new PointF[cols];
        int ai = 0;
        Color ac = P.Acc;
        using (Pen band = new Pen(Color.FromArgb(70, ac), 1f))
        {
            int j = 0;
            for (int c = 0; c < cols; c++)
            {
                double tEnd = t0 + span * (c + 1) / cols;
                double lo = double.MaxValue, hi = double.MinValue, sum = 0;
                int cnt = 0;
                while (j < n && (anData[j].Sec <= tEnd || c == cols - 1))
                {
                    double v = anData[j].V[m];
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                    sum += v; cnt++; j++;
                }
                if (cnt == 0) continue;
                float x = pl.X + c;
                float yLo = (float)(pl.Bottom - pl.Height * lo / mx), yHi = (float)(pl.Bottom - pl.Height * hi / mx);
                if (yLo - yHi >= 1) g.DrawLine(band, x, yLo, x, yHi);
                avg[ai++] = new PointF(x, (float)(pl.Bottom - pl.Height * (sum / cnt) / mx));
            }
        }
        if (ai >= 2)
        {
            PointF[] pts = new PointF[ai];
            Array.Copy(avg, pts, ai);
            using (Pen ln = new Pen(ac, Math.Max(1.5f, Px(2)))) { ln.LineJoin = LineJoin.Round; g.DrawLines(ln, pts); }
        }
        // середнє за весь час - пунктиром
        if (anStats != null)
        {
            float ya = (float)(pl.Bottom - pl.Height * anStats[m].Avg / mx);
            using (Pen dp = new Pen(Util.Blend(P.Card, P.Fg, 0.4f), 1f)) { dp.DashStyle = DashStyle.Dash; g.DrawLine(dp, pl.X, ya, pl.Right, ya); }
        }
        // програвач: позиція
        int hi0 = HeadIndex;
        if (hi0 >= 0)
        {
            float hx = pl.X + (float)((anData[hi0].Sec - t0) / span * pl.Width);
            using (Pen hp = new Pen(P.Fg, Math.Max(1f, Px(1)))) g.DrawLine(hp, hx, pl.Y, hx, pl.Bottom);
            float hy = (float)(pl.Bottom - pl.Height * anData[hi0].V[m] / mx);
            using (SolidBrush hb = new SolidBrush(ac)) g.FillEllipse(hb, hx - Px(4), hy - Px(4), Px(8), Px(8));
        }
        TxtL(g, anData[0].T.ToString("HH:mm:ss"), FS, new Rectangle(pl.X, pl.Bottom + Px(2), Px(80), Px(18)), P.Sub);
        TxtR(g, anData[n - 1].T.ToString("HH:mm:ss"), FS, new Rectangle(pl.Right - Px(80), pl.Bottom + Px(2), Px(80), Px(18)), P.Sub);
        // клік чи перетягування по графіку - перемотка
        DragArea(pl, delegate (Point p)
        {
            double t = t0 + span * Math.Max(0, Math.Min(1, (p.X - pl.X) / (double)Math.Max(1, pl.Width)));
            int lo = 0, hi = n - 1;
            while (lo < hi) { int mid = (lo + hi) / 2; if (anData[mid].Sec < t) lo = mid + 1; else hi = mid; }
            anHead = lo;
            StopPlay();
        });
    }

    void PaintPlayer(Graphics g, Rectangle pr, bool rec)
    {
        int n = anData.Count;
        IconBtn(g, new Rectangle(pr.X, pr.Y + Px(2), Px(36), Px(36)), anPlay ? "pause" : "play", TogglePlay, true);
        int x = pr.X + Px(46);
        foreach (float sp in new float[] { 1f, 10f, 60f, 300f })
        {
            float s0 = sp;
            string t = "×" + sp.ToString("0");
            int w = TextRenderer.MeasureText(t, FS).Width + Px(20);
            Chip(g, new Rectangle(x, pr.Y + Px(6), w, Px(28)), t, Math.Abs(anSpeed - sp) < 0.01f, delegate { anSpeed = s0; Invalidate(); });
            x += w + Px(6);
        }
        Tip(new Rectangle(pr.X + Px(46), pr.Y, x - pr.X - Px(46), pr.Height), L.T("Швидкість відтворення: у скільки разів швидше за реальний час"));
        if (rec)
        {
            Chip(g, new Rectangle(x + Px(6), pr.Y + Px(6), Px(90), Px(28)), L.T("Наживо"), anHead < 0, delegate { anHead = -1; StopPlay(); Invalidate(); });
            x += Px(102);
        }
        int hi0 = HeadIndex;
        string pos = hi0 >= 0 ? Dur(anData[hi0].Sec) + " / " + Dur(anData[n - 1].Sec) + "   ·   " + anData[hi0].T.ToString("HH:mm:ss") : "—";
        TxtR(g, pos, F, new Rectangle(x, pr.Y, pr.Right - x, pr.Height), P.Fg);
    }

    void PaintAnStats(Graphics g, Rectangle sr)
    {
        string[] head = { L.T("Показник"), L.T("У цей момент"), L.T("Мін"), L.T("Макс"), L.T("Середнє"), L.T("95 % часу"), L.T("Пік о") };
        int[] w = { Px(190), 0, 0, 0, 0, 0, Px(80) };
        int rest = (sr.Width - w[0] - w[6]) / 5;
        for (int i = 1; i <= 5; i++) w[i] = rest;
        int rh = Px(22);
        int x = sr.X;
        for (int c = 0; c < head.Length; c++)
        {
            Rectangle hr = new Rectangle(x, sr.Y, w[c] - Px(6), rh);
            if (c == 0) TxtL(g, head[c], FS, hr, P.Sub); else TxtR(g, head[c], FS, hr, P.Sub);
            x += w[c];
        }
        Tip(new Rectangle(sr.X + w[0] + w[1] + w[2] + w[3] + w[4], sr.Y, w[5], rh), L.T("95-й процентиль: 95 % замірів були не більші за це значення (короткі піки не враховано)"));
        int hi0 = HeadIndex;
        for (int i = 0; i < ProcRecorder.Count; i++)
        {
            int yy = sr.Y + rh + Px(2) + i * rh;
            Rectangle row = new Rectangle(sr.X, yy, sr.Width, rh);
            if (i == anMetric) using (SolidBrush b = new SolidBrush(Util.Blend(P.Card, P.Acc, 0.25f))) Util.FillRound(g, b, row, Px(4));
            else if (i % 2 == 0) using (SolidBrush b = new SolidBrush(P.Card)) Util.FillRound(g, b, row, Px(4));
            ProcRecorder.Stat s = anStats != null && anStats.Length > i ? anStats[i] : new ProcRecorder.Stat();
            string[] v =
            {
                ProcRecorder.Name(i),
                hi0 >= 0 ? ProcRecorder.Fmt(i, anData[hi0].V[i]) : "—",
                ProcRecorder.Fmt(i, s.Min), ProcRecorder.Fmt(i, s.Max), ProcRecorder.Fmt(i, s.Avg), ProcRecorder.Fmt(i, s.P95),
                double.IsNaN(s.Max) ? "—" : s.MaxAt.ToString("HH:mm:ss")
            };
            x = sr.X;
            for (int c = 0; c < v.Length; c++)
            {
                Rectangle cr = new Rectangle(x + (c == 0 ? Px(8) : 0), yy, w[c] - Px(6) - (c == 0 ? Px(8) : 0), rh);
                if (c == 0) TxtL(g, v[c], F, cr, P.Fg); else TxtR(g, v[c], F, cr, c == 3 ? P.Warn : P.Fg);
                x += w[c];
            }
            int k = i;
            Hot(row, delegate { anMetric = k; Invalidate(); });
        }
    }

    // вікно посередині: що записувати, як часто й куди
    void PaintSetup(Graphics g, Rectangle r)
    {
        using (SolidBrush dim = new SolidBrush(Color.FromArgb(150, P.Bg))) g.FillRectangle(dim, r);
        Hot(r, delegate { });                       // під вікном нічого не натискається
        int w = Px(500), h = Px(316);
        Rectangle c = new Rectangle(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
        Card(g, c, Util.Blend(P.Bg, P.Card, 0.6f));
        using (Pen ep = new Pen(Util.Blend(P.Card, P.Acc, 0.6f), Math.Max(1f, Px(1))))
        using (GraphicsPath gp = Util.Round(c, P.Rad)) g.DrawPath(ep, gp);
        int pad = Px(18), y = c.Y + Px(14);
        TxtL(g, L.T("Новий запис навантаження"), FH, new Rectangle(c.X + pad, y, c.Width - pad * 2, Px(28)), P.Fg);
        y += Px(34);
        TxtL(g, L.F("{0}  ·  PID {1}", anName, anPid), FB, new Rectangle(c.X + pad, y, c.Width - pad * 2, Px(22)), P.Acc);
        y += Px(22);
        TxtL(g, anPath.Length > 0 ? anPath : L.T("шлях невідомий"), FS, new Rectangle(c.X + pad, y, c.Width - pad * 2, Px(18)), P.Sub);
        y += Px(30);
        TxtL(g, L.T("Заміряти кожні"), F, new Rectangle(c.X + pad, y, Px(150), Px(28)), P.Fg);
        int x = c.X + pad + Px(150);
        foreach (int iv in AnIntervals)
        {
            int v0 = iv;
            string t = iv < 1000 ? (iv / 1000.0).ToString("0.#") + L.T(" с") : (iv / 1000) + L.T(" с");
            int cw = TextRenderer.MeasureText(t, FS).Width + Px(18);
            Chip(g, new Rectangle(x, y, cw, Px(28)), t, anInterval == iv, delegate { anInterval = v0; Invalidate(); });
            x += cw + Px(6);
        }
        y += Px(40);
        TxtL(g, L.T("Журнал (усі заміри, CSV) і підсумок (.txt) поруч:"), F, new Rectangle(c.X + pad, y, c.Width - pad * 2, Px(22)), P.Fg);
        y += Px(24);
        Rectangle pathR = new Rectangle(c.X + pad, y, c.Width - pad * 2 - Px(120), Px(30));
        using (SolidBrush b = new SolidBrush(P.Field)) Util.FillRound(g, b, pathR, Math.Min(P.Rad, pathR.Height / 2));
        TxtL(g, anLog, FS, new Rectangle(pathR.X + Px(10), pathR.Y, pathR.Width - Px(20), pathR.Height), P.Fg);
        Tip(pathR, anLog);
        Btn(g, new Rectangle(pathR.Right + Px(8), y, Px(112), Px(30)), L.T("Змінити…"), PickLog, 0, true);
        y += Px(40);
        if (anMsg.Length > 0 && anMsgBad) TxtW(g, anMsg, FS, new Rectangle(c.X + pad, y, c.Width - pad * 2, Px(34)), P.Bad);
        else TxtW(g, L.T("Запис триває, навіть якщо закрити вікно монітора; зупиняється сам, коли процес завершиться."), FS, new Rectangle(c.X + pad, y, c.Width - pad * 2, Px(34)), P.Sub);
        Btn(g, new Rectangle(c.Right - pad - Px(150), c.Bottom - Px(52), Px(150), Px(36)), L.T("Почати запис"), StartRec, 1, true);
        Btn(g, new Rectangle(c.Right - pad - Px(150) - Px(8) - Px(120), c.Bottom - Px(52), Px(120), Px(36)), L.T("Скасувати"), delegate { anSetup = false; anMsg = ""; Invalidate(); }, 0, true);
    }
}
