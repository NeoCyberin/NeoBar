using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

// ======================================================================
//  Фоновий нагляд за системою (раз на 5 с, через спільне опитування датчиків):
//  - критичні сповіщення: перегрів процесора чи відеокарти, вичерпана відеопам'ять чи оперативна пам'ять,
//    процес, що з'їв забагато пам'яті. Умова має триматися ~15 с; те саме сповіщення - не частіше ніж раз на 10 хв.
//    Кожне пишеться в logs\alerts.log, в історію за добу і позначкою в запис аналізу процесу, якщо той іде;
//  - історія за добу: щохвилини температури й навантаження (найвища температура й середнє навантаження за
//    хвилину), зберігається у history.csv поруч із програмою - пояснює знахідки перевірки системи;
//  - звіт після гри: середній FPS, 1 % low, температури, навантаження, події - у Документи\NeoBar\Ігри.
// ======================================================================
sealed class HistPoint
{
    public DateTime T;                      // початок хвилини (місцевий час)
    public float CpuT = float.NaN, GpuT = float.NaN, Cpu = float.NaN, Gpu = float.NaN, Ram = float.NaN, Vram = float.NaN;
}

sealed class HistEvent { public DateTime T; public string Text = ""; }

static class Watch
{
    // налаштування (Cfg → Apply)
    public static bool Alerts = true, History = true, GameReport = true;
    public static int CpuT = 95, GpuT = 90, ProcGb, VramPct = 95;      // ProcGb 0 - половина оперативної пам'яті

    static NeoBar bar;
    static bool started;
    static int lastTick, lastProcScan, procBusy;

    public static string AlertLog { get { return Path.Combine(SbLog.Dir, "alerts.log"); } }
    static string HistFile { get { return Path.Combine(Prof.Root, "history.csv"); } }

    public static void Start(NeoBar b)
    {
        bar = b;
        if (!started) { started = true; LoadHistory(); Sensors.Updated += OnSample; }
        Apply();
    }

    public static void FromCfg(Cfg c)
    {
        Alerts = c.WAlerts; History = c.WHistory; GameReport = c.WGameReport;
        CpuT = c.WCpuT; GpuT = c.WGpuT; ProcGb = c.WProcGb; VramPct = c.WVramPct;
        Apply();
    }

    public static void Apply()
    {
        if (!started) return;
        bool need = Alerts || History || GameReport;
        Sensors.SetInterval("watch", 5000);
        Sensors.Use("watch", need ? 2 : 0);
    }

    public static void Stop()
    {
        if (!started) return;
        started = false;
        Sensors.Updated -= OnSample;
        Sensors.Use("watch", 0);
        EndGame();
        CloseMinute();
        SaveHistory();
    }

    // ---------- раз на ~5 с ----------
    static void OnSample()
    {
        if (unchecked(Environment.TickCount - lastTick) < 4500) return;
        lastTick = Environment.TickCount;
        try
        {
            if (History) AddToMinute();
            if (Alerts) CheckAlerts();
            if (GameReport) GameTick(); else if (game != null) game = null;
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    static float VramPctNow { get { return Sensors.VramTotalGb > 0 ? (float)(Sensors.VramUsedGb / Sensors.VramTotalGb * 100) : float.NaN; } }

    // ---------- сповіщення ----------
    sealed class Cond { public int Hits; public DateTime Last = DateTime.MinValue; }
    static readonly Dictionary<string, Cond> conds = new Dictionary<string, Cond>();

    // умова справджується 3 заміри поспіль (~15 с) - сповістити, якщо цього ж не було останні cooldownMin хвилин
    static bool Hold(string key, bool now, int cooldownMin)
    {
        Cond c;
        if (!conds.TryGetValue(key, out c)) { c = new Cond(); conds[key] = c; }
        if (!now) { c.Hits = 0; return false; }
        if (++c.Hits < 3) return false;
        if ((DateTime.Now - c.Last).TotalMinutes < cooldownMin) return false;
        c.Last = DateTime.Now;
        return true;
    }

    static void CheckAlerts()
    {
        float ct = Sensors.CpuTemp, gt = Sensors.GpuTemp, vp = VramPctNow, ram = Sensors.Ram;
        if (Hold("cpuT", ct >= CpuT, 10))
            Alert(L.T("Процесор перегрівається"), L.F("Температура процесора {0} °C (межа {1} °C).", ct.ToString("0"), CpuT));
        if (Hold("gpuT", gt >= GpuT, 10))
            Alert(L.T("Відеокарта перегрівається"), L.F("Температура відеокарти {0} °C (межа {1} °C).", gt.ToString("0"), GpuT) + GameNote());
        if (Hold("vram", vp >= VramPct, 10))
            Alert(L.T("Відеопам'ять майже вичерпана"), L.F("Зайнято {0} % відеопам'яті ({1} з {2} ГБ). Гра чи програма може почати гальмувати або впасти.",
                vp.ToString("0"), Sensors.VramUsedGb.ToString("0.0"), Sensors.VramTotalGb.ToString("0.0")) + GameNote());
        if (Hold("ram", ram >= 95, 10))
            ScanProcs(true);
        // процеси - раз на 30 с, у фоні (знімок усіх процесів - один системний виклик)
        if (unchecked(Environment.TickCount - lastProcScan) >= 30000) ScanProcs(false);
    }

    static string GameNote() { return Games.CurrentName.Length > 0 ? " " + L.F("Гра: {0}.", Games.CurrentName) : ""; }

    // ramFull - оперативна пам'ять вичерпана: назвати, хто найбільше займає
    static void ScanProcs(bool ramFull)
    {
        lastProcScan = Environment.TickCount;
        if (Interlocked.Exchange(ref procBusy, 1) == 1) return;
        double total = Sensors.RamTotalGb * 1024.0 * 1048576.0;
        long limit = ProcGb > 0 ? (long)ProcGb << 30 : (long)(total / 2);
        Util.Bg(delegate
        {
            try
            {
                List<SysProc> all = SysProcs.Snapshot();
                all.Sort(delegate (SysProc a, SysProc b) { return b.Ws.CompareTo(a.Ws); });
                List<SysProc> fat = new List<SysProc>();
                if (limit > 0) foreach (SysProc s in all) { if (s.Ws < limit) break; fat.Add(s); }
                string top = "";
                for (int i = 0; i < Math.Min(3, all.Count); i++) top += (i > 0 ? ", " : "") + all[i].Name + " " + Cleaner.Size(all[i].Ws);
                Post(delegate
                {
                    if (ramFull)
                        Alert(L.T("Оперативна пам'ять майже вичерпана"), L.F("Зайнято {0} % пам'яті. Найбільше займають: {1}.", Sensors.Ram.ToString("0"), top));
                    // процес понад межу на кількох перевірках поспіль (~1 хв) - сповістити; той самий - не частіше ніж раз на 30 хв
                    HashSet<string> over = new HashSet<string>();
                    foreach (SysProc s in fat) over.Add("proc:" + s.Name.ToLowerInvariant());
                    foreach (string k in new List<string>(conds.Keys)) if (k.StartsWith("proc:") && !over.Contains(k)) Hold(k, false, 30);
                    HashSet<string> told = new HashSet<string>();
                    foreach (SysProc s in fat)
                        if (told.Add("proc:" + s.Name.ToLowerInvariant()) && Hold("proc:" + s.Name.ToLowerInvariant(), true, 30))
                            Alert(L.T("Процес займає забагато пам'яті"), L.F("{0} (PID {1}) займає {2} оперативної пам'яті (межа {3}). Можливо, у програмі витік пам'яті.",
                                s.Name, s.Pid, Cleaner.Size(s.Ws), Cleaner.Size(limit)));
                });
            }
            catch (Exception ex) { SbLog.Skip(ex); }
            finally { Interlocked.Exchange(ref procBusy, 0); }
        });
    }

    static void Post(Action a)
    {
        try { NeoBar b = bar; if (b != null && !b.IsDisposed && b.IsHandleCreated) b.BeginInvoke(a); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    static readonly object logGate = new object();
    static bool logMarked;

    public static void Alert(string title, string text)
    {
        // у журнал сповіщень (завжди), в історію за добу, у запис аналізу процесу й у звіт гри
        try
        {
            lock (logGate)
            {
                Directory.CreateDirectory(SbLog.Dir);
                string head = "";
                if (!logMarked) { logMarked = true; head = LogKeep.SessionLine() + Environment.NewLine; }
                File.AppendAllText(AlertLog, head + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + title + ". " + text + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        AddEvent(title + ". " + text);
        try { ProcRecorder.AddMark(title); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        if (game != null) game.Events.Add(DateTime.Now.ToString("HH:mm") + "  " + title);
        try { NeoBar b = bar; if (b != null && !b.IsDisposed) ToastForm.ShowClick(b, title, text, "bell", delegate { b.OpenPerf(4); }); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    public static void TrimLog(char mode, int n) { lock (logGate) LogKeep.Trim(AlertLog, mode, n, null); }

    // ---------- історія за добу ----------
    static readonly List<HistPoint> hist = new List<HistPoint>();
    static readonly List<HistEvent> events = new List<HistEvent>();
    static DateTime minute = DateTime.MinValue;
    static float mCpuT, mGpuT, mVram, sCpu, sGpu, sRam;
    static int nCpu, nGpu, nRam;
    static DateTime savedAt = DateTime.Now;

    public static List<HistPoint> Points() { lock (hist) return new List<HistPoint>(hist); }
    public static List<HistEvent> Events() { lock (hist) return new List<HistEvent>(events); }

    static float Max(float a, float b) { return float.IsNaN(a) ? b : float.IsNaN(b) ? a : Math.Max(a, b); }

    static void AddToMinute()
    {
        DateTime m = DateTime.Now;
        m = new DateTime(m.Year, m.Month, m.Day, m.Hour, m.Minute, 0);
        if (m != minute) { CloseMinute(); minute = m; mCpuT = mGpuT = mVram = float.NaN; sCpu = sGpu = sRam = 0; nCpu = nGpu = nRam = 0; }
        mCpuT = Max(mCpuT, Sensors.CpuTemp);
        mGpuT = Max(mGpuT, Sensors.GpuTemp);
        mVram = Max(mVram, VramPctNow);
        if (!float.IsNaN(Sensors.Cpu)) { sCpu += Sensors.Cpu; nCpu++; }
        if (!float.IsNaN(Sensors.GpuLoad)) { sGpu += Sensors.GpuLoad; nGpu++; }
        if (!float.IsNaN(Sensors.Ram)) { sRam += Sensors.Ram; nRam++; }
        if ((DateTime.Now - savedAt).TotalMinutes >= 10) SaveHistory();
    }

    static void CloseMinute()
    {
        if (minute == DateTime.MinValue) return;
        HistPoint p = new HistPoint();
        p.T = minute;
        p.CpuT = mCpuT; p.GpuT = mGpuT; p.Vram = mVram;
        p.Cpu = nCpu > 0 ? sCpu / nCpu : float.NaN;
        p.Gpu = nGpu > 0 ? sGpu / nGpu : float.NaN;
        p.Ram = nRam > 0 ? sRam / nRam : float.NaN;
        lock (hist)
        {
            hist.Add(p);
            DateTime lim = DateTime.Now.AddHours(-24);
            hist.RemoveAll(delegate (HistPoint x) { return x.T < lim; });
        }
        minute = DateTime.MinValue;
    }

    static void AddEvent(string text)
    {
        HistEvent e = new HistEvent();
        e.T = DateTime.Now; e.Text = text;
        lock (hist)
        {
            events.Add(e);
            DateTime lim = DateTime.Now.AddHours(-24);
            events.RemoveAll(delegate (HistEvent x) { return x.T < lim; });
        }
    }

    static string F(float v) { return float.IsNaN(v) ? "" : v.ToString("0.#", CultureInfo.InvariantCulture); }
    static float P(string s) { float v; return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : float.NaN; }

    static void SaveHistory()
    {
        savedAt = DateTime.Now;
        if (!History) return;
        StringBuilder b = new StringBuilder();
        lock (hist)
        {
            foreach (HistPoint p in hist)
                b.Append(p.T.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(';').Append(F(p.CpuT)).Append(';').Append(F(p.GpuT)).Append(';')
                 .Append(F(p.Cpu)).Append(';').Append(F(p.Gpu)).Append(';').Append(F(p.Ram)).Append(';').Append(F(p.Vram)).Append("\r\n");
            foreach (HistEvent e in events)
                b.Append("E;").Append(e.T.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(';').Append(e.Text.Replace("\r", " ").Replace("\n", " ")).Append("\r\n");
        }
        string text = b.ToString();
        Util.Bg(delegate { try { File.WriteAllText(HistFile, text, new UTF8Encoding(false)); } catch (Exception skipEx) { SbLog.Skip(skipEx); } });
    }

    static void LoadHistory()
    {
        try
        {
            if (!File.Exists(HistFile)) return;
            DateTime lim = DateTime.Now.AddHours(-24);
            foreach (string line in File.ReadAllLines(HistFile, Encoding.UTF8))
            {
                string[] p = line.Split(';');
                if (p.Length >= 3 && p[0] == "E")
                {
                    HistEvent e = new HistEvent();
                    if (!DateTime.TryParseExact(p[1], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out e.T) || e.T < lim) continue;
                    e.Text = string.Join(";", p, 2, p.Length - 2);
                    events.Add(e);
                    continue;
                }
                if (p.Length < 7) continue;
                HistPoint h = new HistPoint();
                if (!DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out h.T) || h.T < lim) continue;
                h.CpuT = P(p[1]); h.GpuT = P(p[2]); h.Cpu = P(p[3]); h.Gpu = P(p[4]); h.Ram = P(p[5]); h.Vram = P(p[6]);
                hist.Add(h);
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    // стан системи в ту хвилину (для пояснень у перевірці системи); "" - даних немає
    public static HistPoint PointAt(DateTime t)
    {
        HistPoint best = null;
        lock (hist)
            foreach (HistPoint p in hist)
                if (Math.Abs((p.T - t).TotalMinutes) <= 2 && (best == null || Math.Abs((p.T - t).TotalSeconds) < Math.Abs((best.T - t).TotalSeconds))) best = p;
        return best;
    }

    public static string At(DateTime t)
    {
        HistPoint best = PointAt(t);
        if (best == null) return "";
        List<string> l = new List<string>();
        if (!float.IsNaN(best.GpuT) || !float.IsNaN(best.Gpu))
            l.Add(L.T("відеокарта") + (float.IsNaN(best.GpuT) ? "" : " " + best.GpuT.ToString("0") + " °C") + (float.IsNaN(best.Gpu) ? "" : L.F(", навантаження {0} %", best.Gpu.ToString("0"))));
        if (!float.IsNaN(best.CpuT) || !float.IsNaN(best.Cpu))
            l.Add(L.T("процесор") + (float.IsNaN(best.CpuT) ? "" : " " + best.CpuT.ToString("0") + " °C") + (float.IsNaN(best.Cpu) ? "" : L.F(", навантаження {0} %", best.Cpu.ToString("0"))));
        if (!float.IsNaN(best.Ram)) l.Add(L.F("пам'ять {0} %", best.Ram.ToString("0")));
        if (!float.IsNaN(best.Vram)) l.Add(L.F("відеопам'ять {0} %", best.Vram.ToString("0")));
        return l.Count == 0 ? "" : L.F("о {0}: ", best.T.ToString("HH:mm")) + string.Join("; ", l.ToArray());
    }

    // ---------- звіт після гри ----------
    sealed class Session
    {
        public int Pid;
        public string Name = "";
        public DateTime Start = DateTime.Now;
        public readonly List<float> Fps = new List<float>(), Low = new List<float>();
        public float MaxCpuT = float.NaN, MaxGpuT = float.NaN, MaxVram = float.NaN, MaxRam = float.NaN, MinFps = float.NaN;
        public DateTime MinFpsAt;
        public double SumCpu, SumGpu, SumCpuT, SumGpuT;
        public int NCpu, NGpu, NCpuT, NGpuT;
        public readonly List<string> Events = new List<string>();
    }
    static Session game;

    static void GameTick()
    {
        int pid = Games.CurrentPid;
        if (game != null && game.Pid != pid) EndGame();
        if (pid <= 0) return;
        if (game == null) { game = new Session(); game.Pid = pid; game.Name = Games.CurrentName; }
        Session s = game;
        if (Sensors.Frame != null && Sensors.Frame.Valid)
        {
            float f = (float)Sensors.Frame.Fps;
            s.Fps.Add(f);
            if (!double.IsNaN(Sensors.Frame.Low1)) s.Low.Add((float)Sensors.Frame.Low1);
            if (float.IsNaN(s.MinFps) || f < s.MinFps) { s.MinFps = f; s.MinFpsAt = DateTime.Now; }
        }
        s.MaxCpuT = Max(s.MaxCpuT, Sensors.CpuTemp);
        s.MaxGpuT = Max(s.MaxGpuT, Sensors.GpuTemp);
        s.MaxVram = Max(s.MaxVram, VramPctNow);
        s.MaxRam = Max(s.MaxRam, Sensors.Ram);
        if (!float.IsNaN(Sensors.Cpu)) { s.SumCpu += Sensors.Cpu; s.NCpu++; }
        if (!float.IsNaN(Sensors.GpuLoad)) { s.SumGpu += Sensors.GpuLoad; s.NGpu++; }
        if (!float.IsNaN(Sensors.CpuTemp)) { s.SumCpuT += Sensors.CpuTemp; s.NCpuT++; }
        if (!float.IsNaN(Sensors.GpuTemp)) { s.SumGpuT += Sensors.GpuTemp; s.NGpuT++; }
    }

    static string Avg(double sum, int n, string unit) { return n > 0 ? (sum / n).ToString("0") + unit : "—"; }
    static string Mx(float v, string unit) { return float.IsNaN(v) ? "—" : v.ToString("0") + unit; }

    static void EndGame()
    {
        Session s = game;
        game = null;
        if (s == null || !GameReport) return;
        TimeSpan len = DateTime.Now - s.Start;
        if (len.TotalMinutes < 2) return;               // коротке - не гра, а запуск і вихід
        StringBuilder b = new StringBuilder();
        b.AppendLine(L.F("Звіт після гри: {0}", s.Name));
        b.AppendLine(L.F("{0} - {1}, тривалість {2}", s.Start.ToString("yyyy-MM-dd HH:mm"), DateTime.Now.ToString("HH:mm"),
            ((int)len.TotalHours > 0 ? (int)len.TotalHours + L.T(" год ") : "") + len.Minutes + L.T(" хв")));
        b.AppendLine();
        string fpsLine;
        if (s.Fps.Count > 0)
        {
            double sum = 0; foreach (float f in s.Fps) sum += f;
            double low = 0; foreach (float f in s.Low) low += f;
            fpsLine = L.F("FPS: середній {0}, 1 % low {1}, найнижчий {2} (о {3})", (sum / s.Fps.Count).ToString("0"),
                s.Low.Count > 0 ? (low / s.Low.Count).ToString("0") : "—", s.MinFps.ToString("0"), s.MinFpsAt.ToString("HH:mm"));
        }
        else fpsLine = L.T("FPS: не рахувався (увімкніть оверлей для цієї гри)");
        b.AppendLine(fpsLine);
        b.AppendLine(L.F("Процесор: навантаження в середньому {0}, температура в середньому {1}, найвища {2}", Avg(s.SumCpu, s.NCpu, " %"), Avg(s.SumCpuT, s.NCpuT, " °C"), Mx(s.MaxCpuT, " °C")));
        b.AppendLine(L.F("Відеокарта: навантаження в середньому {0}, температура в середньому {1}, найвища {2}", Avg(s.SumGpu, s.NGpu, " %"), Avg(s.SumGpuT, s.NGpuT, " °C"), Mx(s.MaxGpuT, " °C")));
        b.AppendLine(L.F("Пам'ять: найбільше {0}; відеопам'ять: найбільше {1}", Mx(s.MaxRam, " %"), Mx(s.MaxVram, " %")));
        if (s.Events.Count > 0)
        {
            b.AppendLine();
            b.AppendLine(L.T("Події під час гри:"));
            foreach (string e in s.Events) b.AppendLine(e);
        }
        string file = "";
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NeoBar", L.T("Ігри"));
            Directory.CreateDirectory(dir);
            string n = s.Name;
            foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            file = Path.Combine(dir, n + "_" + s.Start.ToString("yyyy-MM-dd_HH-mm", CultureInfo.InvariantCulture) + ".txt");
            File.WriteAllText(file, b.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex) { Util.ErrorLog(ex); file = ""; }
        string body = fpsLine + "\n" + L.F("Найвища температура: процесор {0}, відеокарта {1}.", Mx(s.MaxCpuT, " °C"), Mx(s.MaxGpuT, " °C")) +
            (s.Events.Count > 0 ? " " + L.F("Сповіщень під час гри: {0}.", s.Events.Count) : "") + (file.Length > 0 ? "\n" + L.T("Клік - повний звіт.") : "");
        string f0 = file;
        try
        {
            NeoBar bb = bar;
            if (bb != null && !bb.IsDisposed)
                ToastForm.ShowClick(bb, L.F("Звіт після гри: {0}", s.Name), body, "flag", delegate { if (f0.Length > 0) try { using (Process.Start("notepad.exe", "\"" + f0 + "\"")) { } } catch (Exception skipEx) { SbLog.Skip(skipEx); } });
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }
}
