using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

// ======================================================================
//  Перевірка оновлень: при запуску (із затримкою, не частіше за раз на 12 годин) читається
//  остання публічна версія з GitHub. Якщо вона новіша - сповіщення з вибором: відкрити сторінку
//  випуску, пропустити цю версію або нагадати пізніше. Без вашої згоди нічого не завантажується.
//  Після згоди: файли випуску беруться лише з github.com/NeoCyberin/NeoBar, перевіряється
//  SHA-256 зі SHA256SUMS.txt, а ставить оновлення помічник (UpdateHelper), коли NeoBar закрито.
// ======================================================================
static class Updater
{
    const string Api = "https://api.github.com/repos/NeoCyberin/NeoBar/releases/latest";
    const string TagPage = "https://github.com/NeoCyberin/NeoBar/releases/tag/";
    const string LatestPage = "https://github.com/NeoCyberin/NeoBar/releases/latest";
    public const string SourceUrl = Api;
    const string DlBase = "https://github.com/NeoCyberin/NeoBar/releases/download/";
    const long MaxPackage = 300L * 1024 * 1024;
    const int EveryHours = 12;
    static readonly object gate = new object();
    static bool shown;                        // сповіщення за цей запуск уже було
    static bool busy;                         // оновлення вже виконується

    // встановлено встановлювачем (є unins000.exe) - оновлюємо встановлювачем; інакше це портативна копія
    static bool IsInstalled { get { return File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "unins000.exe")); } }
    static string TempRoot { get { return Path.Combine(Path.GetTempPath(), "NeoBarUpdate"); } }

    static string StateFile { get { return Path.Combine(NetCache.Root, "update.ini"); } }

    public static Version Current
    {
        get
        {
            Version v = Assembly.GetExecutingAssembly().GetName().Version;
            return new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
        }
    }

    public static string CurrentText { get { Version v = Current; return v.Major + "." + v.Minor + "." + v.Build; } }

    // «v1.2.3», «1.2», «1.2.0-beta» -> 1.2.3 / 1.2.0 / 1.2.0; null, якщо це не версія
    public static Version ParseTag(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        Match m = Regex.Match(tag.Trim(), @"^[vV]?(\d+)(?:\.(\d+))?(?:\.(\d+))?");
        if (!m.Success) return null;
        try
        {
            return new Version(int.Parse(m.Groups[1].Value),
                m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0,
                m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        }
        catch { return null; }
    }

    static Dictionary<string, string> Load()
    {
        Dictionary<string, string> d = new Dictionary<string, string>();
        try
        {
            if (SafeFile.Exists(StateFile))
                foreach (string l in SafeFile.ReadLines(StateFile))
                {
                    int i = l.IndexOf('=');
                    if (i > 0) d[l.Substring(0, i).Trim()] = l.Substring(i + 1).Trim();
                }
        }
        catch { }
        return d;
    }

    static void Save(Dictionary<string, string> d)
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in d) sb.AppendLine(kv.Key + "=" + kv.Value);
            SafeFile.Write(StateFile, sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    // запит до GitHub: тег останнього випуску (без чернеток і пре-релізів)
    static bool Query(out string tag)
    {
        tag = null;
        try
        {
            byte[] b = NetCache.Download(Api, 10000, "NeoBar/" + CurrentText);
            string json = Encoding.UTF8.GetString(b);
            Match m = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            if (!m.Success) return false;
            string t = m.Groups[1].Value;
            if (ParseTag(t) == null || !Regex.IsMatch(t, @"^[A-Za-z0-9._\-]{1,40}$")) return false;
            tag = t;
            return true;
        }
        catch { return false; }
    }

    static string PageOf(string tag)
    {
        return !string.IsNullOrEmpty(tag) && Regex.IsMatch(tag, @"^[A-Za-z0-9._\-]{1,40}$") ? TagPage + tag : LatestPage;
    }

    static bool Newer(string tag)
    {
        Version v = ParseTag(tag);
        return v != null && v > Current;
    }

    // ---------- при запуску ----------

    public static void StartupCheck(NeoBar bar)
    {
        if (bar == null) return;
        Timer t = new Timer();
        t.Interval = 25000;                                   // не заважає запуску
        t.Tick += delegate
        {
            t.Stop(); t.Dispose();
            Util.Bg(delegate
            {
                try { if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, true); } catch { }   // залишки минулого оновлення
                if (bar.Config.UpdCheck) Run(bar);
            });
        };
        t.Start();
    }

    static void Run(NeoBar bar)
    {
        string tag;
        lock (gate)
        {
            Dictionary<string, string> st = Load();
            long lt;
            DateTime lastAt = DateTime.MinValue;
            try { if (long.TryParse(st.ContainsKey("last") ? st["last"] : "0", out lt) && lt > 0) lastAt = new DateTime(lt); } catch { }
            bool due = (DateTime.Now - lastAt).TotalHours >= EveryHours || lastAt > DateTime.Now;
            if (due)
            {
                string q;
                if (Query(out q)) { st["found"] = q; st["last"] = DateTime.Now.Ticks.ToString(); Save(st); }
                else return;                                   // немає мережі - спробуємо наступного запуску
            }
            tag = st.ContainsKey("found") ? st["found"] : null;
            if (!Newer(tag)) return;
            if (st.ContainsKey("skip") && st["skip"] == tag) return;
        }
        try
        {
            bar.BeginInvoke(new Action(delegate
            {
                if (bar.IsDisposed || shown || NeoBar.Paused) return;
                shown = true;
                ToastForm.ShowClick(bar, L.T("Доступне оновлення NeoBar"),
                    L.F("Вийшла версія {0} (у вас {1}). Клікніть, щоб оновити.", Display(tag), CurrentText), "bell",
                    delegate { Offer(bar, null, tag); });
            }));
        }
        catch { }
    }

    static string Display(string tag) { Version v = ParseTag(tag); return v == null ? tag : v.Major + "." + v.Minor + "." + v.Build; }

    // вибір користувача
    static void Offer(NeoBar bar, Form owner, string tag)
    {
        if (busy) return;
        int r = SbDialog.Show(bar, owner, L.T("Доступне оновлення NeoBar"),
            L.F("Вийшла нова версія NeoBar: {0} (у вас {1}).\n\nОновити зараз? Оновлення завантажиться з GitHub, його цілісність буде перевірено, після чого NeoBar сам закриється, встановить нову версію й запуститься знову. Ваші налаштування й дані збережуться.", Display(tag), CurrentText),
            L.T("Оновити зараз"), L.T("Відкрити сторінку"), L.T("Пропустити цю версію"), L.T("Пізніше"));
        if (r == 0) Install(bar, owner, tag);
        else if (r == 1) { try { Process.Start(PageOf(tag)); } catch (Exception ex) { Util.ErrorLog(ex); } }
        else if (r == 2)
        {
            lock (gate) { Dictionary<string, string> st = Load(); st["skip"] = tag; Save(st); }
        }
    }

    // ---------- завантаження й установка (лише після згоди) ----------

    sealed class Job
    {
        public volatile bool Cancel;
        public string Err, File, Sha;
    }

    static void Install(NeoBar bar, Form owner, string tag)
    {
        if (busy) return;
        if (!Regex.IsMatch(tag, @"^[A-Za-z0-9._\-]{1,40}$")) return;
        busy = true;
        try
        {
            bool setup = IsInstalled;
            string appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            string ver = Display(tag);
            string name = "NeoBar-" + ver + (setup ? "-setup.exe" : "-portable.zip");
            // тека пакета може бути захищена від запису - тоді помічникові потрібне підтвердження прав (UAC)
            bool needElev = !Elevated.IsAdmin && !Writable(appDir);

            string work = Path.Combine(TempRoot, DateTime.Now.Ticks.ToString("x"));
            Job job = new Job();
            UpdateWindow w = new UpdateWindow(bar);
            w.OnCancel = delegate { job.Cancel = true; };
            w.Shown += delegate
            {
                Util.Bg(delegate
                {
                    try { Fetch(tag, name, work, job, w); }
                    catch (OperationCanceledException) { job.Cancel = true; }
                    catch (Exception ex) { job.Err = ex.Message; }
                    try { w.BeginInvoke(new Action(delegate { w.End(); })); } catch { }
                });
            };
            using (w)
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                Form o = owner != null && owner.Visible ? owner : null;
                Rectangle ob = o != null ? o.Bounds : wa;
                w.Location = new Point(Math.Max(wa.Left, ob.Left + (ob.Width - w.Width) / 2), Math.Max(wa.Top, ob.Top + (ob.Height - w.Height) / 2));
                if (o != null) w.ShowDialog(o); else w.ShowDialog(new NoOwner());
            }
            if (job.Cancel) { try { Directory.Delete(work, true); } catch { } return; }
            if (job.Err != null)
            {
                try { Directory.Delete(work, true); } catch { }
                int r = SbDialog.Show(bar, owner, L.T("Оновлення NeoBar"),
                    L.F("Не вдалося оновити автоматично: {0}\n\nНову версію можна завантажити вручну зі сторінки випуску.", job.Err),
                    L.T("Відкрити сторінку"), L.T("Закрити"));
                if (r == 0) { try { Process.Start(PageOf(tag)); } catch (Exception ex) { Util.ErrorLog(ex); } }
                return;
            }

            // помічник - копія програми з тимчасової теки: основна закриється, а файли в її теці треба замінити
            string helper = Path.Combine(work, "NeoBarUpdater.exe");
            File.Copy(Application.ExecutablePath, helper, true);
            ProcessStartInfo psi = new ProcessStartInfo(helper,
                "--update " + (setup ? "setup" : "zip") + " \"" + job.File + "\" " + job.Sha + " \"" + appDir + "\" " +
                Process.GetCurrentProcess().Id + " " + (needElev ? "1" : "0"));
            psi.WorkingDirectory = work;
            psi.UseShellExecute = true;
            if (needElev) psi.Verb = "runas";
            try { Process.Start(psi); }
            catch (System.ComponentModel.Win32Exception)
            {
                SbDialog.Show(bar, owner, L.T("Оновлення NeoBar"), L.T("Оновлення скасовано: без підтвердження прав адміністратора не можна змінити теку програми."), L.T("Гаразд"));
                try { Directory.Delete(work, true); } catch { }
                return;
            }
            SbLog.W(SbLog.Panel, "оновлення до " + ver + ": запущено помічник (" + (setup ? "встановлювач" : "портативна копія") + ")");
            bar.Quit();
        }
        catch (Exception ex)
        {
            Util.ErrorLog(ex);
            SbDialog.Show(bar, owner, L.T("Оновлення NeoBar"), L.F("Не вдалося оновити автоматично: {0}\n\nНову версію можна завантажити вручну зі сторінки випуску.", ex.Message), L.T("Гаразд"));
        }
        finally { busy = false; }
    }

    static bool Writable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".neobar-w" + Environment.TickCount);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    // з фонового потоку: контрольні суми -> пакет -> перевірка SHA-256
    static void Fetch(string tag, string name, string work, Job job, UpdateWindow w)
    {
        Directory.CreateDirectory(work);
        // підвищена панель запустить звідси помічника з правами - тека лише для адміністраторів (ніхто не підкладе DLL)
        if (Elevated.IsAdmin) AppLock.Protect(work);
        string baseUrl = DlBase + tag + "/";
        w.Set(L.T("Отримання контрольних сум…"), 0, 0);
        string sums;
        try { sums = Encoding.UTF8.GetString(NetCache.Download(baseUrl + "SHA256SUMS.txt", 15000, "NeoBar/" + CurrentText)); }
        catch (WebException) { throw new IOException(L.T("у випуску немає файлу SHA256SUMS.txt")); }
        string want = UpdateHelper.FindHash(sums, name);
        if (want == null) throw new IOException(L.F("у випуску немає файлу {0}", name));
        if (job.Cancel) throw new OperationCanceledException();

        string file = Path.Combine(work, name);
        Download(baseUrl + name, file, job, w);
        w.Set(L.T("Перевірка файлу…"), 0, 0);
        string got = UpdateHelper.Sha256(file);
        if (!string.Equals(got, want, StringComparison.OrdinalIgnoreCase))
            throw new IOException(L.T("Контрольна сума файлу не збігається."));
        job.File = file;
        job.Sha = want;
    }

    static bool GitHubHost(Uri u)
    {
        string h = u.Host.ToLowerInvariant();
        return u.Scheme == "https" && (h == "github.com" || h == "githubusercontent.com" || h.EndsWith(".githubusercontent.com"));
    }

    static void Download(string url, string dest, Job job, UpdateWindow w)
    {
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;   // TLS 1.2
        HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(url);
        rq.UserAgent = "NeoBar/" + CurrentText;
        rq.Timeout = 20000;
        rq.ReadWriteTimeout = 20000;
        using (WebResponse rs = rq.GetResponse())
        {
            if (!GitHubHost(rs.ResponseUri)) throw new IOException(L.T("адресу завантаження перенаправлено поза GitHub"));
            long total = rs.ContentLength;
            if (total > MaxPackage) throw new IOException(L.T("файл завеликий"));
            using (Stream s = rs.GetResponseStream())
            using (FileStream fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buf = new byte[81920];
                long got = 0;
                int n;
                DateTime last = DateTime.MinValue;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    if (job.Cancel) throw new OperationCanceledException();
                    fs.Write(buf, 0, n);
                    got += n;
                    if (got > MaxPackage) throw new IOException(L.T("файл завеликий"));
                    if ((DateTime.Now - last).TotalMilliseconds > 100)
                    {
                        last = DateTime.Now;
                        w.Set(total > 0 ? L.F("Завантаження… {0} з {1} МБ", Mb(got), Mb(total)) : L.F("Завантаження… {0} МБ", Mb(got)), got, total);
                    }
                }
                if (total > 0 && got != total) throw new IOException(L.T("файл завантажено не повністю"));
            }
        }
    }

    static string Mb(long b) { return (b / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture); }

    // ---------- вручну (налаштування) ----------

    // done(повідомлення, тег новішої версії або null) - з фонового потоку
    public static void CheckNow(Action<string, string> done)
    {
        Util.Bg(delegate
        {
            string tag;
            if (!Query(out tag)) { done(L.T("Не вдалося перевірити оновлення. Перевірте інтернет і спробуйте пізніше."), null); return; }
            lock (gate)
            {
                Dictionary<string, string> st = Load();
                st["found"] = tag; st["last"] = DateTime.Now.Ticks.ToString();
                Save(st);
            }
            if (Newer(tag)) done(null, tag);
            else done(L.F("У вас остання версія ({0}).", CurrentText), null);
        });
    }

    public static void OfferFromSettings(NeoBar bar, Form owner, string tag) { Offer(bar, owner, tag); }
}

// вікно завантаження оновлення (хрестик, Esc або «Скасувати» - переривають)
sealed class UpdateWindow : SbWindow
{
    string status = "";
    long got, total;
    bool finished;

    public UpdateWindow(NeoBar bar) : base(bar, L.T("Оновлення NeoBar"), 460, 190)
    {
        Glyph = "\uE896";
        ShowInTaskbar = false;
        TopMost = true;
        status = L.T("Отримання контрольних сум…");
    }

    public void Set(string s, long g, long t)
    {
        try { BeginInvoke(new Action(delegate { status = s; got = g; total = t; Invalidate(); })); } catch { }
    }

    public void End() { finished = true; Close(); }

    public Action OnCancel;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!finished && OnCancel != null) OnCancel();
        base.OnFormClosing(e);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        TxtW(g, status, F, new Rectangle(r.X, r.Y, r.Width, Px(24)), P.Fg);
        Rectangle bar = new Rectangle(r.X, r.Y + Px(34), r.Width, Px(10));
        using (SolidBrush b = new SolidBrush(P.Field)) g.FillRectangle(b, bar);
        if (total > 0)
        {
            int fw = (int)(bar.Width * Math.Min(1.0, (double)got / total));
            using (SolidBrush b = new SolidBrush(P.Acc)) g.FillRectangle(b, bar.X, bar.Y, fw, bar.Height);
        }
        else
        {
            // розмір невідомий - рухомий відрізок
            int seg = bar.Width / 4, x = bar.X + (int)((Environment.TickCount / 8) % (bar.Width + seg)) - seg;
            int x0 = Math.Max(bar.X, x), x1 = Math.Min(bar.Right, x + seg);
            if (x1 > x0) using (SolidBrush b = new SolidBrush(P.Acc)) g.FillRectangle(b, x0, bar.Y, x1 - x0, bar.Height);
        }
        string cancel = L.T("Скасувати");
        int bw = Math.Max(Px(110), TextRenderer.MeasureText(cancel, F).Width + Px(30));
        Btn(g, new Rectangle(r.Right - bw, r.Bottom - Px(36), bw, Px(36)), cancel, delegate { Close(); }, 0, true);
    }
}
