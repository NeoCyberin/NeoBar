using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

// ======================================================================
//  Мови інтерфейсу. Тексти в коді - українською (вона ж і ключ перекладу): L.T("…").
//  Переклади - файли *.lang: вбудовані в exe (res\lang) і власні в теці lang поруч із NeoBar.
//  Формат файла (UTF-8):
//     @name Deutsch            назва мови в списку
//     @code de                 код (як у Windows: de, en-US, pt-BR)
//     @flag h #000000 #DD0000 #FFCE00     прапорець: h - смуги згори вниз, v - зліва направо
//     @quotes „ “              лапки мови (у текстах пишуться « », програма замінить)
//     < Текст українською      (ключ)
//     > Переклад               (порожньо - текст лишається з запасної мови)
//  \n - новий рядок, \t - табуляція, \\ - зворотна риска.
//  Мова міняється після перезапуску програми.
// ======================================================================
sealed class LangInfo
{
    public string Code = "", Name = "", Flag = "", File = "";     // File: "" - українська, "res:…" - вбудована
    public string Quotes = "";                                    // лапки мови замість « »: два символи через пробіл
    public bool Builtin;
    public int Total, Done;                                       // для власних: скільки рядків перекладено
}

static class L
{
    public static string Code = "uk";
    static Dictionary<string, string> map, fallback;
    static string qo = "«", qc = "»";

    public static string T(string uk)
    {
        if (uk == null || map == null) return uk;
        string r;
        if (map.TryGetValue(uk, out r)) return r;
        if (fallback != null && fallback.TryGetValue(uk, out r)) return r;
        return uk;
    }

    public static bool Uk { get { return map == null; } }

    // лапки в текстах, що не перекладаються (назви файлів, служб тощо в « »)
    public static string Q(string s)
    {
        if (s == null || qo == "«") return s;
        return s.Replace("«", qo).Replace("»", qc);
    }

    // переклад із підстановкою {0}, {1}…; зіпсований переклад - тоді українською
    public static string F(string uk, params object[] a)
    {
        try { return string.Format(T(uk), a); }
        catch (FormatException) { return string.Format(uk, a); }
    }

    static string Root { get { return AppDomain.CurrentDomain.BaseDirectory; } }
    public static string Dir { get { return Path.Combine(Root, "lang"); } }
    static string CfgFile { get { return Path.Combine(Root, "language.ini"); } }

    // вбудовані мови: порядок у списку
    static readonly string[] Builtins = { "en-US", "de", "fr", "es", "it", "pl", "cs", "pt-BR" };

    // вибрана мова (порожньо - ще не вибирали)
    public static string Saved()
    {
        try
        {
            if (System.IO.File.Exists(CfgFile))
                foreach (string l in System.IO.File.ReadAllLines(CfgFile, Encoding.UTF8))
                    if (l.StartsWith("lang=")) return l.Substring(5).Trim();
        }
        catch { }
        return "";
    }

    public static bool Save(string code)
    {
        try { System.IO.File.WriteAllText(CfgFile, "lang=" + code + "\r\n", new UTF8Encoding(false)); return true; }
        catch { return false; }
    }

    // під час запуску, до появи будь-якого вікна
    public static void Init()
    {
        string want = Saved();
        if (want.Length == 0) want = Guess();
        Load(want);
    }

    // перший запуск: мова Windows, якщо є такий переклад; інакше англійська (для України - українська)
    static string Guess()
    {
        try
        {
            CultureInfo ui = CultureInfo.InstalledUICulture;
            string two = ui.TwoLetterISOLanguageName;
            if (two == "uk" || two == "ru" || two == "be") return "uk";
            foreach (LangInfo li in All())
                if (string.Equals(li.Code, ui.Name, StringComparison.OrdinalIgnoreCase)) return li.Code;
            foreach (LangInfo li in All())
                if (li.Code.Split('-')[0].Equals(two, StringComparison.OrdinalIgnoreCase)) return li.Code;
            return "en-US";
        }
        catch { return "uk"; }
    }

    static void Load(string code)
    {
        map = null; fallback = null; Code = "uk";
        if (code == "uk") return;
        LangInfo li = Find(code);
        if (li == null) return;
        Dictionary<string, string> m = Parse(ReadText(li), null);
        if (m == null) return;
        map = m;
        Code = li.Code;
        string[] q = li.Quotes.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (q.Length == 2) { qo = q[0]; qc = q[1]; }
        // неперекладене - англійською (зрозуміліше за українську), для самої англійської - українською
        if (li.Code != "en-US")
        {
            LangInfo en = Find("en-US");
            if (en != null) fallback = Parse(ReadText(en), null);
        }
        if (qo != "«")
            foreach (Dictionary<string, string> d in new[] { map, fallback })
            {
                if (d == null) continue;
                List<string> ks = new List<string>(d.Keys);
                foreach (string k in ks) d[k] = Q(d[k]);
            }
    }

    public static LangInfo Find(string code)
    {
        foreach (LangInfo li in All()) if (string.Equals(li.Code, code, StringComparison.OrdinalIgnoreCase)) return li;
        return null;
    }

    public static List<LangInfo> All()
    {
        List<LangInfo> l = new List<LangInfo>();
        LangInfo uk = new LangInfo();
        uk.Code = "uk"; uk.Name = "Українська"; uk.Flag = "h #0057B7 #FFD700"; uk.Builtin = true;
        l.Add(uk);
        Assembly a = Assembly.GetExecutingAssembly();
        foreach (string c in Builtins)
        {
            string res = "lang." + c + ".lang";
            using (Stream s = a.GetManifestResourceStream(res))
            {
                if (s == null) continue;
                LangInfo li = Head(new StreamReader(s, Encoding.UTF8).ReadToEnd());
                if (li == null) continue;
                li.File = "res:" + res; li.Builtin = true;
                l.Add(li);
            }
        }
        // власні файли: нова мова або заміна вбудованої
        try
        {
            if (Directory.Exists(Dir))
                foreach (string f in Directory.GetFiles(Dir, "*.lang"))
                {
                    if (Path.GetFileName(f).StartsWith("template", StringComparison.OrdinalIgnoreCase)) continue;
                    string txt;
                    try { txt = System.IO.File.ReadAllText(f, Encoding.UTF8); } catch { continue; }
                    LangInfo li = Head(txt);
                    if (li == null || li.Code == "uk") continue;
                    li.File = f;
                    int total, done;
                    Parse(txt, null, out total, out done);
                    li.Total = total; li.Done = done;
                    int at = l.FindIndex(delegate (LangInfo x) { return string.Equals(x.Code, li.Code, StringComparison.OrdinalIgnoreCase); });
                    if (at >= 0) l[at] = li; else l.Add(li);
                }
        }
        catch { }
        return l;
    }

    static string ReadText(LangInfo li)
    {
        try
        {
            if (li.File.StartsWith("res:"))
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(li.File.Substring(4)))
                    return s == null ? "" : new StreamReader(s, Encoding.UTF8).ReadToEnd();
            return System.IO.File.ReadAllText(li.File, Encoding.UTF8);
        }
        catch { return ""; }
    }

    static LangInfo Head(string txt)
    {
        LangInfo li = new LangInfo();
        using (StringReader r = new StringReader(txt))
        {
            string l;
            int n = 0;
            while ((l = r.ReadLine()) != null && n++ < 40)
            {
                if (l.StartsWith("@name ")) li.Name = l.Substring(6).Trim();
                else if (l.StartsWith("@code ")) li.Code = l.Substring(6).Trim();
                else if (l.StartsWith("@flag ")) li.Flag = l.Substring(6).Trim();
                else if (l.StartsWith("@quotes ")) li.Quotes = l.Substring(8).Trim();
                else if (l.StartsWith("<")) break;
            }
        }
        if (li.Code.Length == 0 || li.Code.Length > 16) return null;
        if (li.Name.Length == 0) li.Name = li.Code;
        return li;
    }

    static Dictionary<string, string> Parse(string txt, Dictionary<string, string> into)
    {
        int t, d;
        return Parse(txt, into, out t, out d);
    }

    static Dictionary<string, string> Parse(string txt, Dictionary<string, string> into, out int total, out int done)
    {
        total = 0; done = 0;
        Dictionary<string, string> m = into ?? new Dictionary<string, string>(StringComparer.Ordinal);
        string key = null;
        using (StringReader r = new StringReader(txt ?? ""))
        {
            string l;
            while ((l = r.ReadLine()) != null)
            {
                if (l.StartsWith("<")) { key = Unesc(l.StartsWith("< ") ? l.Substring(2) : l.Substring(1)); total++; continue; }
                if (l.StartsWith(">") && key != null)
                {
                    string v = Unesc(l.Length > 1 && l[1] == ' ' ? l.Substring(2) : l.Substring(1));
                    if (v.Length > 0) { m[key] = v; done++; }
                    key = null;
                }
            }
        }
        return m;
    }

    public static string Esc(string s) { return s.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t"); }

    static string Unesc(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        StringBuilder b = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                char n = s[++i];
                b.Append(n == 'n' ? '\n' : n == 't' ? '\t' : n == 'r' ? '\r' : n);
            }
            else b.Append(c);
        }
        return b.ToString();
    }

    // шаблон для власної мови: усі тексти, поруч - англійський переклад як підказка
    public static string WriteTemplate()
    {
        Directory.CreateDirectory(Dir);
        string path = Path.Combine(Dir, "template.lang");
        LangInfo en = Find("en-US");
        string txt = en != null ? ReadText(en) : "";
        Dictionary<string, string> hint = Parse(txt, null);
        StringBuilder b = new StringBuilder();
        b.Append("# NeoBar - language template / шаблон мови\r\n");
        b.Append("# 1. Copy this file to e.g. nl.lang (same folder) and fill in @name, @code, @flag.\r\n");
        b.Append("# 2. Under each '<' line (Ukrainian original) write the translation after '> '.\r\n");
        b.Append("#    The '#' line above it is the English translation, for reference.\r\n");
        b.Append("#    Empty '>' lines fall back to English. Keep \\n (new line) and symbols like «», %, •.\r\n");
        b.Append("# 3. Restart NeoBar and pick the language in Settings.\r\n");
        b.Append("# @flag: h = horizontal stripes (top to bottom), v = vertical (left to right), colors #RRGGBB.\r\n\r\n");
        b.Append("@name My language\r\n@code xx\r\n@flag h #FFFFFF #888888\r\n@quotes « »\r\n\r\n");
        using (StringReader r = new StringReader(txt))
        {
            string l;
            while ((l = r.ReadLine()) != null)
            {
                if (!l.StartsWith("<")) continue;
                string key = Unesc(l.StartsWith("< ") ? l.Substring(2) : l.Substring(1));
                string h;
                if (hint.TryGetValue(key, out h)) b.Append("# ").Append(Esc(h)).Append("\r\n");
                b.Append("< ").Append(Esc(key)).Append("\r\n> \r\n\r\n");
            }
        }
        System.IO.File.WriteAllText(path, b.ToString(), new UTF8Encoding(true));
        return path;
    }
}
