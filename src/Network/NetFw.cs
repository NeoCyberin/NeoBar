using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

// ======================================================================
//  Брандмауер Windows: стан профілів і правила (читання - з реєстру, незалежно від мови системи),
//  зміни - лише системним netsh.exe.
//
//  Безпека (принцип «краще не змінити, ніж зламати»):
//   - Змінювати й видаляти можна лише власні правила із звичайною унікальною назвою; системні (@-назви, групи,
//     повторні назви) показуються, але не чіпаються. Назва «all» заборонена: для netsh це «усі правила».
//   - Нове правило мусить мати порт або програму: «дозволити/заблокувати все» не створюється.
//   - Помічник із підвищенням отримує файл завдання з контрольною сумою SHA-256 і перевіряє кожен рядок наново,
//     включно з тим, що правило з такою назвою рівно одне.
// ======================================================================
sealed class FwRule
{
    public string Id = "", Name = "", Desc = "", Dir = "In", Action = "Allow", Proto = "", LPort = "", RPort = "", App = "", Svc = "", Group = "", Profile = "", RAddr = "";
    public bool Active = true, Plain;                           // Plain - власне правило, яке можна змінювати

    public bool Allow { get { return string.Equals(Action, "Allow", StringComparison.OrdinalIgnoreCase); } }
    public bool In { get { return !string.Equals(Dir, "Out", StringComparison.OrdinalIgnoreCase); } }

    public string ProtoText
    {
        get
        {
            switch (Proto)
            {
                case "6": return "TCP";
                case "17": return "UDP";
                case "1": return "ICMP";
                case "58": return "ICMPv6";
                case "": case "256": return L.T("будь-який");
            }
            return L.T("протокол ") + Proto;
        }
    }

    public string Summary
    {
        get
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(In ? L.T("Вхідне") : L.T("Вихідне")).Append(" · ").Append(ProtoText);
            if (LPort.Length > 0) sb.Append(L.T(" · порт ")).Append(LPort);
            if (RPort.Length > 0) sb.Append(" → ").Append(RPort);
            if (App.Length > 0) sb.Append(" · ").Append(Path.GetFileName(App));
            else if (Svc.Length > 0) sb.Append(L.T(" · служба ")).Append(Svc);
            return sb.ToString();
        }
    }
}

static class Fw
{
    const string Base = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";
    static readonly string[] ProfKeys = { "DomainProfile", "StandardProfile", "PublicProfile" };
    public static readonly string[] ProfNames = { L.T("Домен"), L.T("Приватна"), L.T("Загальна") };
    static readonly string[] ProfNetsh = { "domainprofile", "privateprofile", "publicprofile" };


    // ---------- стан профілів ----------
    // on[i]: увімкнено; managed[i]: керується груповою політикою (змінити тут не вийде)
    public static bool Read(bool[] on, bool[] managed)
    {
        try
        {
            using (Microsoft.Win32.RegistryKey bk = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Base))
            {
                if (bk == null) return false;
                for (int i = 0; i < 3; i++)
                {
                    on[i] = true; managed[i] = false;
                    using (Microsoft.Win32.RegistryKey k = bk.OpenSubKey(ProfKeys[i]))
                    {
                        object v = k != null ? k.GetValue("EnableFirewall") : null;
                        on[i] = !(v is int) || (int)v != 0;
                    }
                    try
                    {
                        using (Microsoft.Win32.RegistryKey pk = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\WindowsFirewall\" + ProfKeys[i]))
                        {
                            object pv = pk != null ? pk.GetValue("EnableFirewall") : null;
                            if (pv is int) { managed[i] = true; on[i] = (int)pv != 0; }
                        }
                    }
                    catch (Exception skipEx) { SbLog.Skip(skipEx); }
                }
                return true;
            }
        }
        catch { return false; }
    }

    // загальний стан для віджета на столі: -1 невідомо, 0 усе вимкнено, 1 частково, 2 усе ввімкнено (кеш на 3 с)
    static DateTime ovAt = DateTime.MinValue;
    static int ovVal = -1;

    // стан змінили (віджетом на столі або вікном «Мережа») - решта показів має перечитати його
    public static int Rev;
    public static void RaiseChanged() { Rev++; ovAt = DateTime.MinValue; }

    public static int Overall(bool fresh)
    {
        if (!fresh && (DateTime.Now - ovAt).TotalSeconds < 3) return ovVal;
        bool[] on = new bool[3], man = new bool[3];
        if (Read(on, man))
        {
            int n = 0;
            foreach (bool b in on) if (b) n++;
            ovVal = n == 3 ? 2 : (n == 0 ? 0 : 1);
        }
        else ovVal = -1;
        ovAt = DateTime.Now;
        return ovVal;
    }

    // ---------- правила ----------
    static string Resolve(string s)
    {
        if (s == null || !s.StartsWith("@", StringComparison.Ordinal)) return s ?? "";
        try
        {
            StringBuilder sb = new StringBuilder(260);
            if (Win.SHLoadIndirectString(s, sb, sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0) return sb.ToString();
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return s;
    }

    // рядок правила з реєстру: v2.31|Action=Allow|Active=TRUE|Dir=In|Protocol=6|LPort=80|App=...|Name=...|
    public static FwRule ParseRule(string id, string v)
    {
        if (string.IsNullOrEmpty(v) || v.Length > 8000) return null;
        FwRule r = new FwRule();
        r.Id = id ?? "";
        foreach (string part in v.Split('|'))
        {
            int e = part.IndexOf('=');
            if (e <= 0) continue;
            string k = part.Substring(0, e), val = part.Substring(e + 1);
            switch (k)
            {
                case "Action": r.Action = val; break;
                case "Active": r.Active = string.Equals(val, "TRUE", StringComparison.OrdinalIgnoreCase); break;
                case "Dir": r.Dir = val; break;
                case "Protocol": r.Proto = val; break;
                case "LPort": r.LPort = r.LPort.Length > 0 ? r.LPort + "," + val : val; break;
                case "RPort": r.RPort = r.RPort.Length > 0 ? r.RPort + "," + val : val; break;
                case "App": r.App = Environment.ExpandEnvironmentVariables(val); break;
                case "Svc": r.Svc = val; break;
                case "Name": r.Name = val; break;
                case "Desc": r.Desc = val; break;
                case "EmbedCtxt": r.Group = val; break;
                case "Profile": r.Profile = r.Profile.Length > 0 ? r.Profile + "," + val : val; break;
                case "RA4": case "RA6": r.RAddr = r.RAddr.Length > 0 ? r.RAddr + "," + val : val; break;
            }
        }
        if (r.Name.Length == 0) return null;
        return r;
    }

    // зводить розібрані правила: розв'язує @-назви, позначає ті, що можна змінювати
    public static List<FwRule> Finish(List<FwRule> raw)
    {
        Dictionary<string, int> cnt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (FwRule r in raw) { int c; cnt.TryGetValue(r.Name, out c); cnt[r.Name] = c + 1; }
        foreach (FwRule r in raw)
        {
            bool literal = !r.Name.StartsWith("@", StringComparison.Ordinal);
            r.Plain = literal && r.Group.Length == 0 && cnt[r.Name] == 1 && CheckName(r.Name, false).Length == 0;
            if (!literal) r.Name = Resolve(r.Name);
            if (r.Group.StartsWith("@", StringComparison.Ordinal)) r.Group = Resolve(r.Group);
        }
        raw.Sort(delegate (FwRule a, FwRule b)
        {
            if (a.Plain != b.Plain) return a.Plain ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });
        return raw;
    }

    // null - реєстр недоступний (потрібні права адміністратора)
    public static List<FwRule> Load()
    {
        try
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Base + @"\FirewallRules"))
            {
                if (k == null) return null;
                List<FwRule> l = new List<FwRule>();
                foreach (string n in k.GetValueNames())
                {
                    FwRule r = ParseRule(n, k.GetValue(n) as string);
                    if (r != null) l.Add(r);
                    if (l.Count >= 6000) break;
                }
                return Finish(l);
            }
        }
        catch { return null; }
    }

    // з помічника з підвищенням: сирі рядки правил у base64 (id|значення)
    public static int Dump(TextWriter w)
    {
        try
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Base + @"\FirewallRules"))
            {
                if (k == null) { w.WriteLine("E|" + B(L.T("Правил не знайдено."))); return 0; }
                foreach (string n in k.GetValueNames())
                {
                    string v = k.GetValue(n) as string;
                    if (v != null) w.WriteLine("R|" + B(n) + "|" + B(v));
                }
            }
        }
        catch (Exception ex) { w.WriteLine("E|" + B(ex.Message)); }
        return 0;
    }

    public static List<FwRule> ParseDump(string text)
    {
        List<FwRule> l = new List<FwRule>();
        foreach (string line in (text ?? "").Split('\n'))
        {
            string[] p = line.Trim().Split('|');
            if (p.Length != 3 || p[0] != "R") continue;
            try
            {
                FwRule r = ParseRule(U(p[1]), U(p[2]));
                if (r != null) l.Add(r);
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
            if (l.Count >= 6000) break;
        }
        return Finish(l);
    }

    static string B(string s) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? "")); }
    static string U(string s) { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }

    // ---------- перевірка введеного ----------
    public static string CheckName(string n, bool forNew)
    {
        if (string.IsNullOrWhiteSpace(n)) return L.T("Уведіть назву правила.");
        if (n != n.Trim()) return L.T("Назва не має починатися чи закінчуватися пробілом.");
        if (n.Length > 60) return L.T("Назва - не довше 60 символів.");
        if (n.StartsWith("@", StringComparison.Ordinal)) return L.T("Назва не може починатися з @.");
        if (string.Equals(n, "all", StringComparison.OrdinalIgnoreCase)) return L.T("Назва «all» зарезервована системою.");
        foreach (char c in n) if (c == '"' || c == '\\' || c == '|' || c == '%' || c < 32) return L.T("У назві не можна лапки, \\, |, % і керівні символи.");
        return "";
    }

    static readonly Regex PortRe = new Regex(@"^\d{1,5}(-\d{1,5})?(,\d{1,5}(-\d{1,5})?)*$", RegexOptions.Compiled);

    public static string CheckPorts(string p)
    {
        if (p == null || p.Length == 0) return "";
        if (p.Length > 60 || !PortRe.IsMatch(p)) return L.T("Порт: число, діапазон (5000-5010) або список через кому (80,443).");
        foreach (string part in p.Split(','))
        {
            string[] ab = part.Split('-');
            int a = int.Parse(ab[0], CultureInfo.InvariantCulture), b = ab.Length > 1 ? int.Parse(ab[1], CultureInfo.InvariantCulture) : a;
            if (a < 1 || b > 65535 || a > b) return L.T("Порти - від 1 до 65535, початок діапазону не більший за кінець.");
        }
        return "";
    }

    static readonly Regex ExeRe = new Regex(@"^[A-Za-z]:\\[^""<>|*?%\x00-\x1f]+\.exe$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string CheckProgram(string p)
    {
        if (p == null || p.Length == 0) return "";
        if (p.Length > 259 || !ExeRe.IsMatch(p) || p.Contains("..\\")) return L.T("Програма: повний шлях до .exe, наприклад C:\\Games\\game.exe.");
        return "";
    }

    // повна перевірка нового правила: "" - гаразд
    public static string CheckNew(string name, string proto, string port, string app)
    {
        string e = CheckName(name, true);
        if (e.Length > 0) return e;
        if (proto != "tcp" && proto != "udp" && proto != "any") return L.T("Невідомий протокол.");
        e = CheckPorts(port);
        if (e.Length > 0) return e;
        e = CheckProgram(app);
        if (e.Length > 0) return e;
        if (port.Length == 0 && app.Length == 0) return L.T("Укажіть порт або програму: правило «для всього» не створюється - воно вимкнуло б мережу чи відкрило ПК.");
        if (port.Length > 0 && proto == "any") return L.T("Для порту виберіть TCP або UDP.");
        return "";
    }

    // ---------- завдання ----------
    public static string PlState(int prof, bool on) { return "S|" + prof + "|" + (on ? "on" : "off"); }       // prof 0-2, 3 - усі

    public static string PlAdd(string name, bool inbound, bool allow, string proto, string port, string app)
    {
        return "A|" + B(name) + "|" + (inbound ? "in" : "out") + "|" + (allow ? "allow" : "block") + "|" + proto + "|" + port + "|" + B(app);
    }

    public static string PlDel(string name) { return "D|" + B(name); }
    public static string PlEnable(string name, bool on) { return "E|" + B(name) + "|" + (on ? "on" : "off"); }

    static int Netsh(string args, out string output)
    {
        output = "";
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), args);
            psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true;
            try { psi.StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            StringBuilder sb = new StringBuilder();
            using (Process pr = Process.Start(psi))
            {
                pr.OutputDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null && e.Data.Trim().Length > 0) lock (sb) if (sb.Length < 400) sb.Append(e.Data.Trim()).Append(' '); };
                pr.BeginOutputReadLine();
                if (!pr.WaitForExit(30000)) { try { pr.Kill(); } catch (Exception skipEx) { SbLog.Skip(skipEx); } output = L.T("час очікування вичерпано"); return 1; }
                pr.WaitForExit();
                lock (sb) output = sb.ToString().Trim();
                return pr.ExitCode;
            }
        }
        catch (Exception ex) { output = ex.Message; return 1; }
    }

    // скільки правил має таку назву (за реєстром, у процесі з підвищенням) і чи воно власне
    static string Existing(string name, out int count, out bool plain)
    {
        count = 0; plain = false;
        List<FwRule> l = Load();
        if (l == null) return L.T("Не вдалося прочитати правила.");
        foreach (FwRule r in l) if (string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) { count++; plain = r.Plain; }
        return "";
    }

    public static void Exec(string[] lines, TextWriter w)
    {
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            string[] p = line.Split('|');
            try
            {
                string err = "", args = null;
                switch (p[0])
                {
                    case "S":
                    {
                        int pf;
                        if (p.Length < 3 || !int.TryParse(p[1], out pf) || pf < 0 || pf > 3 || (p[2] != "on" && p[2] != "off")) { err = L.T("Некоректне завдання."); break; }
                        args = "advfirewall set " + (pf == 3 ? "allprofiles" : ProfNetsh[pf]) + " state " + p[2];
                        break;
                    }
                    case "A":
                    {
                        if (p.Length < 7) { err = L.T("Некоректне завдання."); break; }
                        string name = U(p[1]), app = U(p[6]), proto = p[4], port = p[5];
                        err = CheckNew(name, proto, port, app);
                        if (err.Length == 0 && ((p[2] != "in" && p[2] != "out") || (p[3] != "allow" && p[3] != "block"))) err = L.T("Некоректне завдання.");
                        if (err.Length > 0) break;
                        int cnt; bool pl;
                        err = Existing(name, out cnt, out pl);
                        if (err.Length == 0 && cnt > 0) { err = L.T("Правило з такою назвою вже є."); break; }
                        if (err.Length > 0) break;
                        args = "advfirewall firewall add rule name=\"" + name + "\" dir=" + p[2] + " action=" + p[3] + " enable=yes profile=any protocol=" + proto +
                               (port.Length > 0 ? " localport=" + port : "") + (app.Length > 0 ? " program=\"" + app + "\"" : "");
                        break;
                    }
                    case "D":
                    case "E":
                    {
                        if (p.Length < (p[0] == "E" ? 3 : 2)) { err = L.T("Некоректне завдання."); break; }
                        string name = U(p[1]);
                        if (p[0] == "E" && p[2] != "on" && p[2] != "off") { err = L.T("Некоректне завдання."); break; }
                        err = CheckName(name, false);
                        if (err.Length > 0) break;
                        int cnt; bool pl;
                        err = Existing(name, out cnt, out pl);
                        if (err.Length > 0) break;
                        if (cnt != 1 || !pl) { err = L.T("Це правило системне або його назва не унікальна - його не чіпаю."); break; }
                        args = p[0] == "D" ? "advfirewall firewall delete rule name=\"" + name + "\""
                                           : "advfirewall firewall set rule name=\"" + name + "\" new enable=" + (p[2] == "on" ? "yes" : "no");
                        break;
                    }
                    default: err = L.T("Невідома дія."); break;
                }
                if (err.Length > 0) { w.WriteLine("ERR|" + err); continue; }
                string o;
                int code = Netsh(args, out o);
                w.WriteLine(code == 0 ? "OK|" + p[0] : "ERR|" + (o.Length > 0 ? o : L.T("помилка netsh (код ") + code + ")"));
            }
            catch (Exception ex) { w.WriteLine("ERR|" + ex.Message.Replace('\r', ' ').Replace('\n', ' ')); }
        }
    }

    public static int RunFile(string file, string hash, TextWriter w)
    {
        byte[] raw = File.ReadAllBytes(file);
        try { File.Delete(file); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        if (!ElevHash.Same(raw, hash)) { w.WriteLine(L.T("ERR|Файл завдання змінено після створення - нічого не виконано.")); return 1; }
        Exec(Encoding.UTF8.GetString(raw).Split('\n'), w);
        return 0;
    }

    public static string Apply(List<string> lines, out string err)
    {
        err = "";
        if (Elevated.IsAdmin)
        {
            StringWriter sw = new StringWriter();
            Exec(lines.ToArray(), sw);
            return sw.ToString();
        }
        return NetCfg.RunElevated("fw", lines, out err);
    }
}
