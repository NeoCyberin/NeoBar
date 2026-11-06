using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

// ======================================================================
//  Списки «Безпеки»:
//   - дозволені (білий список): знахідка більше не вважається підозрілою. Для файла пам'ятається ще й SHA-256 -
//     якщо файл зміниться, дозвіл перестає діяти;
//   - ізольовані (чорний список): файл перенесено в теку ізоляції (у зміненому вигляді, щоб він не запустився),
//     його процеси завершено, автозапуск і служби, що на нього посилаються, вимкнено. Усе повертається кнопкою «Відновити».
//     Якщо такий файл з'явиться знову (той самий шлях чи вміст) - сканер позначить його.
// ======================================================================
sealed class ListEntry
{
    public char Kind;                       // 'A' - дозволено, 'Q' - ізольовано
    public string Key = "", Path = "", Sha = "", Title = "", When = "", QFile = "";
    public long Size = -1;
    public bool Admin;
    public List<FixOp> Undo = new List<FixOp>();
}

static class SecLists
{
    static string FilePath { get { return System.IO.Path.Combine(Prof.Root, "security-lists.txt"); } }
    public static string QDir { get { return System.IO.Path.Combine(Prof.Root, "quarantine"); } }
    public static readonly List<ListEntry> All = new List<ListEntry>();
    static bool loaded;

    static string B(string s) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? "")); }
    static string U(string s) { try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); } catch { return ""; } }

    public static void Load()
    {
        if (loaded) return;
        loaded = true;
        lock (All)
        {
            All.Clear();
            try
            {
                if (!File.Exists(FilePath)) return;
                ListEntry cur = null;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    if (line.StartsWith("#"))
                    {
                        string[] p = line.Split('\t');
                        if (p.Length < 10) { cur = null; continue; }
                        cur = new ListEntry();
                        cur.Kind = p[0].Length > 1 ? p[0][1] : 'A';
                        cur.Key = U(p[1]); cur.Path = U(p[2]); cur.Sha = p[3]; cur.Title = U(p[4]); cur.When = p[5]; cur.QFile = U(p[6]);
                        long.TryParse(p[7], out cur.Size); cur.Admin = p[8] == "1";
                        All.Add(cur);
                    }
                    else if (cur != null && line.Length > 0) { FixOp o = FixOp.Parse(line); if (o != null) cur.Undo.Add(o); }
                }
            }
            catch (Exception ex) { Util.ErrorLog(ex); }
        }
    }

    static void Save()
    {
        try
        {
            StringBuilder b = new StringBuilder();
            lock (All)
                foreach (ListEntry e in All)
                {
                    b.Append('#').Append(e.Kind).Append('\t').Append(B(e.Key)).Append('\t').Append(B(e.Path)).Append('\t').Append(e.Sha).Append('\t')
                     .Append(B(e.Title)).Append('\t').Append(e.When).Append('\t').Append(B(e.QFile)).Append('\t').Append(e.Size).Append('\t').Append(e.Admin ? "1" : "0").Append('\t').Append("1\r\n");
                    foreach (FixOp o in e.Undo) b.Append(o.Line()).Append("\r\n");
                }
            File.WriteAllText(FilePath, b.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    public static List<ListEntry> Of(char kind)
    {
        Load();
        List<ListEntry> r = new List<ListEntry>();
        lock (All) foreach (ListEntry e in All) if (e.Kind == kind) r.Add(e);
        return r;
    }

    // ключ знахідки: файл (за шляхом) або сама перевірка/запис
    public static string KeyOf(Finding f)
    {
        if (f.Area == "sys") return "sys:" + f.Title;
        if (f.Path.Length > 0) return "file:" + f.Path.ToLowerInvariant();
        return f.Area + ":" + f.Title + "|" + f.Cmd;
    }

    static readonly Dictionary<string, string> shaCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static string ShaOf(string path)
    {
        try
        {
            string k = path + "|" + File.GetLastWriteTimeUtc(path).Ticks + "|" + new FileInfo(path).Length;
            string s;
            lock (shaCache) if (shaCache.TryGetValue(k, out s)) return s;
            s = Hashes.Sha256Of(path);
            lock (shaCache) shaCache[k] = s;
            return s;
        }
        catch { return ""; }
    }

    // після сканування: позначити дозволене й ізольоване раніше (виклик - з потоку сканування)
    public static void Mark(List<Finding> list)
    {
        Load();
        List<ListEntry> a = Of('A'), q = Of('Q');
        if (a.Count == 0 && q.Count == 0) return;
        Dictionary<long, bool> qSizes = new Dictionary<long, bool>();
        foreach (ListEntry e in q) if (e.Size >= 0) qSizes[e.Size] = true;
        foreach (Finding f in list)
        {
            string key = KeyOf(f);
            foreach (ListEntry e in a)
                if (e.Key == key && (e.Sha.Length == 0 || (File.Exists(f.Path) && ShaOf(f.Path) == e.Sha))) { f.Allowed = true; break; }
            if (f.Path.Length == 0 || !File.Exists(f.Path)) continue;
            foreach (ListEntry e in q)
            {
                bool same = string.Equals(e.Path, f.Path, StringComparison.OrdinalIgnoreCase);
                if (!same && e.Sha.Length > 0 && qSizes.ContainsKey(new FileInfo(f.Path).Length)) same = ShaOf(f.Path) == e.Sha;
                if (same) { f.Allowed = false; f.Add(8, L.T("Цей файл ви раніше ізолювали («") + e.Title + L.Q("», ") + e.When + L.T("), а він знову з'явився.")); break; }
            }
        }
    }

    public static void Allow(Finding f)
    {
        Load();
        ListEntry e = new ListEntry();
        e.Kind = 'A';
        e.Key = KeyOf(f);
        e.Path = f.Path;
        e.Title = f.Title;
        e.When = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
        if (f.Path.Length > 0 && File.Exists(f.Path)) e.Sha = ShaOf(f.Path);
        lock (All) { All.RemoveAll(delegate (ListEntry x) { return x.Kind == 'A' && x.Key == e.Key; }); All.Insert(0, e); }
        Save();
        f.Allowed = true;
    }

    public static void Remove(ListEntry e)
    {
        lock (All) All.Remove(e);
        Save();
    }

    // можна ізолювати: файл існує, не системний і не підписаний Microsoft
    public static bool CanIsolate(Finding f)
    {
        if (f.Path.Length == 0 || !File.Exists(f.Path) || f.Place == SecHeur.PSys) return false;
        if (f.SigKind == Sig.Ok && Sig.Microsoft(f.Signer)) return false;
        return true;
    }

    // що буде зроблено, для тексту підтвердження
    public static Fix PlanIsolate(Finding f, out string what)
    {
        Fix fx = new Fix();
        List<string> w = new List<string>();
        string sc = System.IO.Path.Combine(Environment.SystemDirectory, "sc.exe"), st = System.IO.Path.Combine(Environment.SystemDirectory, "schtasks.exe");
        foreach (Finding a in SecScan.Auto)
        {
            if (!string.Equals(a.Path, f.Path, StringComparison.OrdinalIgnoreCase) || a.Off) continue;
            if (a.AutoKind == "run" || a.AutoKind == "folder")
            {
                byte[] b = new byte[12];
                b[0] = 3;
                long ft = DateTime.Now.ToFileTimeUtc();
                for (int i = 0; i < 8; i++) b[4 + i] = (byte)(ft >> (8 * i));
                fx.Reg(a.Hklm, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + a.ApprovedSub, a.ApprovedName, "bin", Convert.ToBase64String(b));
                w.Add(L.T("вимкнути автозапуск «") + a.Title + L.Q("»"));
            }
            else if (a.AutoKind == "task")
            {
                fx.Admin = true;
                fx.Ops.Add(new FixOp("RUNQ", st, "/Change /TN \"" + a.TaskPath + "\" /DISABLE"));
                fx.UndoRun.Add(new FixOp("RUNQ", st, "/Change /TN \"" + a.TaskPath + "\" /ENABLE"));
                w.Add(L.T("вимкнути заплановане завдання «") + a.Title + L.Q("»"));
            }
        }
        foreach (Finding s in SecScan.Svc)
        {
            if (!string.Equals(s.Path, f.Path, StringComparison.OrdinalIgnoreCase) || s.SvcName.Length == 0) continue;
            fx.Reg(true, @"SYSTEM\CurrentControlSet\Services\" + s.SvcName, "Start", "dword", "4");
            fx.Ops.Add(new FixOp("RUNQ", sc, "stop \"" + s.SvcName + "\""));
            if (s.SvcStart == 2) fx.UndoRun.Add(new FixOp("RUNQ", sc, "start \"" + s.SvcName + "\""));
            w.Add(L.T("зупинити й вимкнути службу «") + s.Title + L.Q("»"));
        }
        string q = System.IO.Path.Combine(QDir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + System.IO.Path.GetFileName(f.Path) + ".quarantine");
        fx.Ops.Add(new FixOp("MOVEX", f.Path, q));
        w.Add(L.T("перенести файл в ізоляцію (теку quarantine поруч із NeoBar, у зміненому вигляді)"));
        if (f.Place != SecHeur.PUser) fx.Admin = true;
        what = string.Join(";\n", w.ToArray());
        return fx;
    }

    // виклик - не з потоку інтерфейсу. Процеси вже завершено.
    public static string Isolate(Finding f, Fix fx)
    {
        Load();
        long size = -1;
        string sha = "";
        try { size = new FileInfo(f.Path).Length; sha = ShaOf(f.Path); } catch { }
        List<FixOp> undo;
        string err = SecFix.ApplyRaw(fx, out undo);
        if (err.Length > 0) return err;
        ListEntry e = new ListEntry();
        e.Kind = 'Q';
        e.Key = KeyOf(f); e.Path = f.Path; e.Sha = sha; e.Size = size; e.Title = f.Title;
        e.When = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
        e.Admin = fx.Admin;
        e.Undo = undo;
        foreach (FixOp o in fx.Ops) if (o.Kind == "MOVEX") e.QFile = o.A[1];
        lock (All) All.Insert(0, e);
        Save();
        return "";
    }

    public static string Restore(ListEntry e)
    {
        string err = SecFix.Run(e.Undo, e.Admin);
        if (err.Length > 0) return err;
        Remove(e);
        return "";
    }

    // прибрати копію з ізоляції назавжди (запис лишається в чорному списку: якщо файл з'явиться знову - буде позначено)
    public static string Purge(ListEntry e)
    {
        if (e.QFile.Length > 0 && File.Exists(e.QFile))
        {
            List<FixOp> ops = new List<FixOp>();
            ops.Add(new FixOp("DEL", e.QFile));
            string err = SecFix.Run(ops, false);
            if (err.Length > 0) err = SecFix.Run(ops, true);
            if (err.Length > 0) return err;
        }
        e.QFile = "";
        e.Undo.Clear();
        Save();
        return "";
    }
}

// ======================================================================
//  VirusTotal: перевірка за хешем, а якщо файла немає в базі - надсилання на перевірку (лише після згоди).
//  Потрібен безкоштовний ключ API (обмеження: 4 запити на хвилину, 500 на день). Без ключа - відкривається сайт.
// ======================================================================
static class Vt
{
    static string KeyPath { get { return Path.Combine(Prof.Root, "virustotal.key"); } }

    public static string Key
    {
        get { try { return File.Exists(KeyPath) ? File.ReadAllText(KeyPath).Trim() : ""; } catch { return ""; } }
        set
        {
            try
            {
                if (string.IsNullOrEmpty(value)) { if (File.Exists(KeyPath)) File.Delete(KeyPath); }
                else File.WriteAllText(KeyPath, value.Trim());
            }
            catch (Exception ex) { Util.ErrorLog(ex); }
        }
    }

    public sealed class Res
    {
        public int Code;                 // HTTP-код; 0 - немає зв'язку
        public int Mal, Sus, Total;
        public string Status = "", Id = "", Err = "";
    }

    static string Req(string method, string url, byte[] body, string ctype, out int code)
    {
        code = 0;
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(url);
        rq.Method = method;
        rq.Headers["x-apikey"] = Key;
        rq.Accept = "application/json";
        rq.UserAgent = "NeoBar";
        rq.Timeout = 30000;
        rq.ReadWriteTimeout = 10 * 60000;
        if (body != null)
        {
            rq.ContentType = ctype;
            rq.ContentLength = body.Length;
            rq.AllowWriteStreamBuffering = false;
            using (Stream s = rq.GetRequestStream())
                for (int o = 0; o < body.Length; o += 1 << 16) s.Write(body, o, Math.Min(1 << 16, body.Length - o));
        }
        try
        {
            using (HttpWebResponse rs = (HttpWebResponse)rq.GetResponse())
            using (StreamReader r = new StreamReader(rs.GetResponseStream(), Encoding.UTF8))
            {
                code = (int)rs.StatusCode;
                return r.ReadToEnd();
            }
        }
        catch (WebException ex)
        {
            HttpWebResponse rs = ex.Response as HttpWebResponse;
            if (rs == null) throw;
            code = (int)rs.StatusCode;
            using (StreamReader r = new StreamReader(rs.GetResponseStream(), Encoding.UTF8)) return r.ReadToEnd();
        }
    }

    static int Num(string json, string block, string name)
    {
        int b = json.IndexOf("\"" + block + "\"", StringComparison.Ordinal);
        if (b < 0) return 0;
        Match m = Regex.Match(json.Substring(b, Math.Min(600, json.Length - b)), "\"" + Regex.Escape(name) + "\"\\s*:\\s*(\\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    static void Stats(string json, string block, Res r)
    {
        r.Mal = Num(json, block, "malicious");
        r.Sus = Num(json, block, "suspicious");
        r.Total = r.Mal + r.Sus + Num(json, block, "undetected") + Num(json, block, "harmless");
    }

    static string ErrText(int code)
    {
        if (code == 401) return L.T("Ключ VirusTotal не прийнято - перевірте його.");
        if (code == 429) return L.T("Вичерпано ліміт безкоштовного ключа (4 запити на хвилину, 500 на день). Спробуйте пізніше.");
        if (code == 0) return L.T("Немає зв'язку з VirusTotal.");
        return L.T("VirusTotal відповів кодом ") + code + ".";
    }

    // звіт за хешем: Code 200 - є в базі, 404 - немає
    public static Res Lookup(string sha)
    {
        Res r = new Res();
        try
        {
            string j = Req("GET", "https://www.virustotal.com/api/v3/files/" + sha, null, null, out r.Code);
            if (r.Code == 200) Stats(j, "last_analysis_stats", r);
            else if (r.Code != 404) r.Err = ErrText(r.Code);
        }
        catch (Exception ex) { r.Err = L.T("Немає зв'язку з VirusTotal: ") + ex.Message; }
        return r;
    }

    // надіслати файл; результат - Id аналізу
    public static Res Upload(string path)
    {
        Res r = new Res();
        try
        {
            long len = new FileInfo(path).Length;
            if (len > 650L << 20) { r.Err = L.T("Файл більший за 650 МБ - VirusTotal такі не приймає."); return r; }
            string url = "https://www.virustotal.com/api/v3/files";
            if (len > 32L << 20)
            {
                int c;
                string j0 = Req("GET", "https://www.virustotal.com/api/v3/files/upload_url", null, null, out c);
                Match m0 = Regex.Match(j0, "\"data\"\\s*:\\s*\"([^\"]+)\"");
                if (c != 200 || !m0.Success) { r.Err = ErrText(c); return r; }
                url = m0.Groups[1].Value;
            }
            string bnd = "----NeoBar" + DateTime.Now.Ticks.ToString("x");
            byte[] head = Encoding.UTF8.GetBytes("--" + bnd + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"" + Path.GetFileName(path).Replace("\"", "") + "\"\r\nContent-Type: application/octet-stream\r\n\r\n");
            byte[] tail = Encoding.ASCII.GetBytes("\r\n--" + bnd + "--\r\n");
            byte[] data = File.ReadAllBytes(path);
            byte[] body = new byte[head.Length + data.Length + tail.Length];
            Buffer.BlockCopy(head, 0, body, 0, head.Length);
            Buffer.BlockCopy(data, 0, body, head.Length, data.Length);
            Buffer.BlockCopy(tail, 0, body, head.Length + data.Length, tail.Length);
            string j = Req("POST", url, body, "multipart/form-data; boundary=" + bnd, out r.Code);
            Match m = Regex.Match(j, "\"id\"\\s*:\\s*\"([^\"]+)\"");
            if (r.Code == 200 && m.Success) r.Id = m.Groups[1].Value;
            else r.Err = ErrText(r.Code);
        }
        catch (Exception ex) { r.Err = L.T("Не вдалося надіслати: ") + ex.Message; }
        return r;
    }

    // стан аналізу: Status "completed" - готово
    public static Res Analysis(string id)
    {
        Res r = new Res();
        try
        {
            string j = Req("GET", "https://www.virustotal.com/api/v3/analyses/" + id, null, null, out r.Code);
            if (r.Code != 200) { r.Err = ErrText(r.Code); return r; }
            Match m = Regex.Match(j, "\"status\"\\s*:\\s*\"([^\"]+)\"");
            r.Status = m.Success ? m.Groups[1].Value : "";
            if (r.Status == "completed") Stats(j, "stats", r);
        }
        catch (Exception ex) { r.Err = L.T("Немає зв'язку з VirusTotal: ") + ex.Message; }
        return r;
    }
}

// невелике вікно з одним полем введення (ключ VirusTotal тощо)
sealed class InputDialog : SbWindow
{
    readonly string text;
    readonly TextBox tb;
    public string Value = "";
    bool ok;

    InputDialog(NeoBar bar, string title, string text, string value) : base(bar, title, 520, 230) { this.text = text; tb = MakeInput(); tb.Text = value ?? ""; Init(); }
    InputDialog(Theme t, float sc, string title, string text, string value) : base(t, sc, title, 520, 230) { this.text = text; tb = MakeInput(); tb.Text = value ?? ""; Init(); }

    void Init()
    {
        Glyph = "\uE70F";
        TopMost = true;
        tb.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Ok(); } };
    }

    void Ok() { ok = true; Value = tb.Text.Trim(); Close(); }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        TxtW(g, text, F, new Rectangle(r.X, r.Y, r.Width, Px(64)), P.Fg);
        PlaceInput(g, tb, new Rectangle(r.X, r.Y + Px(70), r.Width, Px(34)));
        Btn(g, new Rectangle(r.Right - Px(130), r.Bottom - Px(36), Px(130), Px(36)), L.T("Гаразд"), Ok, 1, true);
        Btn(g, new Rectangle(r.Right - Px(270), r.Bottom - Px(36), Px(130), Px(36)), L.T("Скасувати"), delegate { Close(); }, 0, true);
    }

    // null - скасовано
    public static string Ask(NeoBar bar, Theme t, float sc, Form owner, string title, string text, string value)
    {
        using (InputDialog d = bar != null ? new InputDialog(bar, title, text, value) : new InputDialog(t, sc, title, text, value))
        {
            Rectangle ob = owner != null ? owner.Bounds : Screen.PrimaryScreen.WorkingArea;
            d.Location = new Point(ob.Left + (ob.Width - d.Width) / 2, ob.Top + Math.Max(0, (ob.Height - d.Height) / 2));
            if (owner != null) d.ShowDialog(owner); else d.ShowDialog(new NoOwner());
            return d.ok ? d.Value : null;
        }
    }
}
