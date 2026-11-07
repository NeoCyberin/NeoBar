using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

// ======================================================================
//  Перевірка оновлень: при запуску (із затримкою, не частіше за раз на 12 годин) читається
//  остання публічна версія з GitHub. Якщо вона новіша - сповіщення з вибором: відкрити сторінку
//  випуску, пропустити цю версію або нагадати пізніше. Нічого не завантажується й не
//  запускається саме - оновлюєте ви самі зі сторінки випуску.
// ======================================================================
static class Updater
{
    const string Api = "https://api.github.com/repos/NeoCyberin/NeoBar/releases/latest";
    const string TagPage = "https://github.com/NeoCyberin/NeoBar/releases/tag/";
    const string LatestPage = "https://github.com/NeoCyberin/NeoBar/releases/latest";
    public const string SourceUrl = Api;
    const int EveryHours = 12;
    static readonly object gate = new object();
    static bool shown;                        // сповіщення за цей запуск уже було

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
            if (File.Exists(StateFile))
                foreach (string l in File.ReadAllLines(StateFile, Encoding.UTF8))
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
            File.WriteAllText(StateFile, sb.ToString(), new UTF8Encoding(false));
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
        if (bar == null || !bar.Config.UpdCheck) return;
        Timer t = new Timer();
        t.Interval = 25000;                                   // не заважає запуску
        t.Tick += delegate
        {
            t.Stop(); t.Dispose();
            Util.Bg(delegate { Run(bar); });
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
                    L.F("Вийшла версія {0} (у вас {1}). Клікніть, щоб дізнатися більше.", Display(tag), CurrentText), "bell",
                    delegate { Offer(bar, null, tag); });
            }));
        }
        catch { }
    }

    static string Display(string tag) { Version v = ParseTag(tag); return v == null ? tag : v.Major + "." + v.Minor + "." + v.Build; }

    // вибір користувача
    static void Offer(NeoBar bar, Form owner, string tag)
    {
        int r = SbDialog.Show(bar, owner, L.T("Доступне оновлення NeoBar"),
            L.F("Вийшла нова версія NeoBar: {0} (у вас {1}).\n\nПрограма не оновлюється сама: завантажте нову версію зі сторінки випуску.", Display(tag), CurrentText),
            L.T("Відкрити сторінку"), L.T("Пропустити цю версію"), L.T("Пізніше"));
        if (r == 0) { try { System.Diagnostics.Process.Start(PageOf(tag)); } catch (Exception ex) { Util.ErrorLog(ex); } }
        else if (r == 1)
        {
            lock (gate) { Dictionary<string, string> st = Load(); st["skip"] = tag; Save(st); }
        }
    }

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
