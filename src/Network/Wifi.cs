using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// ======================================================================
//  Wi-Fi у вкладці «Адаптери» вікна «Мережа»: мережі, збережені на цьому ПК, з паролями; додати мережу,
//  змінити пароль, забути мережу; QR-код для підключення телефона; список для передачі іншій людині.
//  Пароль у відкритому вигляді Windows віддає лише адміністратору: без прав видно назви й тип захисту,
//  а паролі розблоковуються одним запитом UAC. Паролі живуть лише в пам'яті відкритого вікна й нікуди
//  не записуються, поки людина сама не збереже список. Усе змінене - лише за її командою.
// ======================================================================
sealed class WifiNet
{
    public string Name = "", Ssid = "", Auth = "WPA", Sec = "", Pass = "";      // Auth: nopass, WEP, WPA, SAE, ent (корпоративна - QR не підходить)
    public bool Hidden, Auto, Enterprise, Locked, Connected;                    // Locked - пароль є, але Windows його не віддала
    public bool Open { get { return Auth == "nopass"; } }
    public bool CanShare { get { return Auth != "ent" && (Open || Pass.Length > 0); } }
}

static class WifiList
{
    [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(int ver, IntPtr res, out int neg, out IntPtr h);
    [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr h, IntPtr res);
    [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr h, IntPtr res, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern int WlanQueryInterface(IntPtr h, ref Guid g, int op, IntPtr res, out int size, out IntPtr data, IntPtr type);
    [DllImport("wlanapi.dll")] static extern int WlanGetProfileList(IntPtr h, ref Guid g, IntPtr res, out IntPtr list);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] static extern int WlanGetProfile(IntPtr h, ref Guid g, string name, IntPtr res, out IntPtr xml, ref int flags, out int access);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] static extern int WlanSetProfile(IntPtr h, ref Guid g, int flags, string xml, string sec, bool overwrite, IntPtr res, out int reason);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] static extern int WlanDeleteProfile(IntPtr h, ref Guid g, string name, IntPtr res);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);

    static string Tag(string xml, string tag)
    {
        int a = xml.IndexOf("<" + tag + ">", StringComparison.Ordinal);
        if (a < 0) return "";
        a += tag.Length + 2;
        int b = xml.IndexOf("</" + tag + ">", a, StringComparison.Ordinal);
        return b > a ? System.Net.WebUtility.HtmlDecode(xml.Substring(a, b - a)) : "";
    }

    // з XML-профілю Windows. Пароль береться лише тоді, коли Windows віддала його відкритим (protected=false);
    // зашифрований вміст keyMaterial паролем не вважається ніколи.
    public static WifiNet Parse(string name, string xml)
    {
        WifiNet w = new WifiNet();
        w.Name = name;
        int ss = xml.IndexOf("<SSID>", StringComparison.Ordinal);
        w.Ssid = ss >= 0 ? Tag(xml.Substring(ss), "name") : "";
        if (w.Ssid.Length == 0) w.Ssid = name;
        string au = Tag(xml, "authentication").ToUpperInvariant(), en = Tag(xml, "encryption").ToUpperInvariant();
        bool onex = Tag(xml, "useOneX").ToLowerInvariant() == "true";
        w.Hidden = Tag(xml, "nonBroadcast").ToLowerInvariant() == "true";
        w.Auto = Tag(xml, "connectionMode").ToLowerInvariant() == "auto";
        bool ent = onex || au == "WPA" || au == "WPA2" || au == "WPA3" || au == "WPA3ENT" || au == "WPA3ENT192";
        w.Enterprise = ent;
        if (ent)
        {
            w.Auth = "ent";
            w.Sec = au == "WPA3" || au.StartsWith("WPA3ENT") ? "WPA3-Enterprise" : (au == "WPA" ? "WPA-Enterprise" : "WPA2-Enterprise");
        }
        else if (au == "OPEN" || au == "OWE")
        {
            if (en == "WEP") { w.Auth = "WEP"; w.Sec = "WEP"; }
            else { w.Auth = "nopass"; w.Sec = au == "OWE" ? "Enhanced Open" : L.T("Відкрита (без пароля)"); }
        }
        else if (au == "SHARED") { w.Auth = "WEP"; w.Sec = "WEP"; }
        else if (au == "WPA3SAE") { w.Auth = "SAE"; w.Sec = "WPA3-Personal"; }
        else { w.Auth = "WPA"; w.Sec = au == "WPAPSK" ? "WPA-Personal" : (au == "WPA2PSK" ? "WPA2-Personal" : (au.Length > 0 ? au : "WPA")); }
        string km = Tag(xml, "keyMaterial");
        if (km.Length > 0 && Tag(xml, "protected").ToLowerInvariant() == "false") w.Pass = km;
        w.Locked = !w.Open && !w.Enterprise && w.Pass.Length == 0;
        return w;
    }

    static WifiNet Read(IntPtr h, ref Guid g, string name)
    {
        IntPtr xp; int flags = 4, access;        // WLAN_PROFILE_GET_PLAINTEXT_KEY
        int r = WlanGetProfile(h, ref g, name, IntPtr.Zero, out xp, ref flags, out access);
        if (r != 0)
        {
            flags = 0;
            r = WlanGetProfile(h, ref g, name, IntPtr.Zero, out xp, ref flags, out access);
            if (r != 0) return null;
        }
        string xml;
        try { xml = Marshal.PtrToStringUni(xp); }
        finally { WlanFreeMemory(xp); }
        return string.IsNullOrEmpty(xml) ? null : Parse(name, xml);
    }

    // усі збережені мережі; error - пояснення, якщо Wi-Fi недоступний
    public static List<WifiNet> Load(out string error)
    {
        error = "";
        List<WifiNet> res = new List<WifiNet>();
        Dictionary<string, WifiNet> seen = new Dictionary<string, WifiNet>(StringComparer.Ordinal);
        IntPtr h = IntPtr.Zero, list = IntPtr.Zero;
        try
        {
            int neg;
            if (WlanOpenHandle(2, IntPtr.Zero, out neg, out h) != 0) { error = L.T("Wi-Fi недоступний на цьому ПК."); return res; }
            if (WlanEnumInterfaces(h, IntPtr.Zero, out list) != 0) { error = L.T("Не вдалося прочитати адаптери Wi-Fi."); return res; }
            int n = Marshal.ReadInt32(list, 0);
            if (n == 0) { error = L.T("На цьому ПК немає адаптера Wi-Fi."); return res; }
            for (int i = 0; i < n; i++)
            {
                IntPtr item = new IntPtr(list.ToInt64() + 8 + i * 532);
                byte[] gb = new byte[16];
                Marshal.Copy(item, gb, 0, 16);
                Guid g = new Guid(gb);
                string cur = "";
                if (Marshal.ReadInt32(item, 16 + 512) == 1)          // wlan_interface_state_connected
                {
                    int sz; IntPtr data;
                    if (WlanQueryInterface(h, ref g, 7, IntPtr.Zero, out sz, out data, IntPtr.Zero) == 0)
                    {
                        try { cur = Marshal.PtrToStringUni(new IntPtr(data.ToInt64() + 8)) ?? ""; }
                        finally { WlanFreeMemory(data); }
                    }
                }
                IntPtr pl;
                if (WlanGetProfileList(h, ref g, IntPtr.Zero, out pl) != 0) continue;
                try
                {
                    int pc = Marshal.ReadInt32(pl, 0);
                    for (int j = 0; j < pc; j++)
                    {
                        string name = Marshal.PtrToStringUni(new IntPtr(pl.ToInt64() + 8 + j * 516));
                        if (string.IsNullOrEmpty(name)) continue;
                        WifiNet w;
                        if (seen.TryGetValue(name, out w)) { if (name == cur) w.Connected = true; continue; }
                        w = Read(h, ref g, name);
                        if (w == null) continue;
                        w.Connected = name == cur;
                        seen[name] = w;
                        res.Add(w);
                    }
                }
                finally { WlanFreeMemory(pl); }
            }
            res.Sort(delegate (WifiNet a, WifiNet b)
            {
                if (a.Connected != b.Connected) return a.Connected ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
        }
        catch (DllNotFoundException) { error = L.T("Wi-Fi недоступний на цьому ПК."); }
        catch (Exception ex) { error = ex.Message; }
        finally
        {
            if (list != IntPtr.Zero) WlanFreeMemory(list);
            if (h != IntPtr.Zero) WlanCloseHandle(h, IntPtr.Zero);
        }
        return res;
    }

    // ---------- зміни: пароль, нова мережа, забути ----------
    static string XmlEsc(string s) { return System.Security.SecurityElement.Escape(s ?? ""); }

    // Wi-Fi-пароль: 8-63 символи ASCII або рівно 64 шістнадцяткові цифри
    public static string CheckPass(string p)
    {
        if (p == null || p.Length == 0) return L.T("Уведіть пароль.");
        if (p.Length == 64)
        {
            foreach (char c in p) if (!Uri.IsHexDigit(c)) return L.T("Пароль із 64 символів має складатися лише з цифр 0-9 і букв A-F.");
            return "";
        }
        if (p.Length < 8 || p.Length > 63) return L.T("Пароль має бути від 8 до 63 символів.");
        foreach (char c in p) if (c < 32 || c > 126) return L.T("Пароль Wi-Fi може містити лише латинські літери, цифри та знаки (ASCII).");
        return "";
    }

    public static string CheckSsid(string s)
    {
        if (string.IsNullOrEmpty(s)) return L.T("Уведіть назву мережі.");
        if (Encoding.UTF8.GetByteCount(s) > 32) return L.T("Назва мережі - не більше 32 байтів.");
        foreach (char c in s) if (c < 32) return L.T("У назві не можна керівні символи.");
        return "";
    }

    // XML профілю нової мережі; auth: WPA (WPA2-Personal), SAE (WPA3-Personal), nopass (відкрита)
    public static string BuildXml(string ssid, string auth, string pass, bool hidden)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><name>").Append(XmlEsc(ssid)).Append("</name>");
        sb.Append("<SSIDConfig><SSID><name>").Append(XmlEsc(ssid)).Append("</name></SSID><nonBroadcast>").Append(hidden ? "true" : "false").Append("</nonBroadcast></SSIDConfig>");
        sb.Append("<connectionType>ESS</connectionType><connectionMode>auto</connectionMode><MSM><security><authEncryption><authentication>");
        sb.Append(auth == "nopass" ? "open" : (auth == "SAE" ? "WPA3SAE" : "WPA2PSK")).Append("</authentication><encryption>").Append(auth == "nopass" ? "none" : "AES").Append("</encryption><useOneX>false</useOneX></authEncryption>");
        if (auth != "nopass") sb.Append("<sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>").Append(XmlEsc(pass)).Append("</keyMaterial></sharedKey>");
        sb.Append("</security></MSM></WLANProfile>");
        return sb.ToString();
    }

    // у XML наявного профілю підставляє новий пароль (null - немає де його міняти)
    public static string ReplaceKey(string xml, string pass)
    {
        const string open = "<keyMaterial>", close = "</keyMaterial>";
        int a = xml.IndexOf(open, StringComparison.Ordinal);
        if (a < 0) return null;
        int b = xml.IndexOf(close, a, StringComparison.Ordinal);
        if (b < 0) return null;
        string r = xml.Substring(0, a + open.Length) + XmlEsc(pass) + xml.Substring(b);
        return r.Replace("<protected>true</protected>", "<protected>false</protected>");
    }

    static List<Guid> Ifaces(IntPtr h)
    {
        List<Guid> l = new List<Guid>();
        IntPtr list;
        if (WlanEnumInterfaces(h, IntPtr.Zero, out list) != 0) return l;
        try
        {
            int n = Marshal.ReadInt32(list, 0);
            for (int i = 0; i < n; i++)
            {
                byte[] gb = new byte[16];
                Marshal.Copy(new IntPtr(list.ToInt64() + 8 + i * 532), gb, 0, 16);
                l.Add(new Guid(gb));
            }
        }
        finally { WlanFreeMemory(list); }
        return l;
    }

    // 0 - готово; 5 - потрібні права адміністратора; інше - помилка (пояснення в err)
    public static int SetKey(string profile, string pass, out string err)
    {
        err = CheckPass(pass);
        if (err.Length > 0) return 1;
        IntPtr h = IntPtr.Zero;
        try
        {
            int neg;
            if (WlanOpenHandle(2, IntPtr.Zero, out neg, out h) != 0) { err = L.T("Wi-Fi недоступний на цьому ПК."); return 1; }
            int worst = 1168;
            foreach (Guid g0 in Ifaces(h))
            {
                Guid g = g0;
                IntPtr xp; int flags = 4, access;
                int r = WlanGetProfile(h, ref g, profile, IntPtr.Zero, out xp, ref flags, out access);
                if (r != 0) { if (r == 5) worst = 5; continue; }
                string xml;
                try { xml = Marshal.PtrToStringUni(xp); }
                finally { WlanFreeMemory(xp); }
                if ((flags & 1) != 0) { err = L.T("Цю мережу задано груповою політикою - змінити її тут не можна."); return 1; }
                string nx = string.IsNullOrEmpty(xml) ? null : ReplaceKey(xml, pass);
                if (nx == null) { err = L.T("У цій мережі немає пароля, який можна змінити (відкрита чи корпоративна)."); return 1; }
                int reason;
                r = WlanSetProfile(h, ref g, (flags & 2) != 0 ? 2 : 0, nx, null, true, IntPtr.Zero, out reason);
                if (r == 5) return 5;
                if (r != 0) { err = L.T("Windows не прийняла зміну (код ") + r + ")."; return 1; }
                return 0;
            }
            if (worst == 5) return 5;
            err = L.T("Мережу не знайдено.");
            return 1;
        }
        catch (Exception ex) { err = ex.Message; return 1; }
        finally { if (h != IntPtr.Zero) WlanCloseHandle(h, IntPtr.Zero); }
    }

    public static int Forget(string profile, out string err)
    {
        err = "";
        IntPtr h = IntPtr.Zero;
        try
        {
            int neg;
            if (WlanOpenHandle(2, IntPtr.Zero, out neg, out h) != 0) { err = L.T("Wi-Fi недоступний на цьому ПК."); return 1; }
            bool done = false, denied = false;
            foreach (Guid g0 in Ifaces(h))
            {
                Guid g = g0;
                int r = WlanDeleteProfile(h, ref g, profile, IntPtr.Zero);
                if (r == 0) done = true;
                else if (r == 5) denied = true;
            }
            if (done) return 0;
            if (denied) return 5;
            err = L.T("Мережу не знайдено.");
            return 1;
        }
        catch (Exception ex) { err = ex.Message; return 1; }
        finally { if (h != IntPtr.Zero) WlanCloseHandle(h, IntPtr.Zero); }
    }

    // нова мережа - профіль користувача (прав адміністратора не треба)
    public static int AddNet(string ssid, string auth, string pass, bool hidden, out string err)
    {
        err = CheckSsid(ssid);
        if (err.Length == 0 && auth != "nopass") err = CheckPass(pass);
        if (err.Length > 0) return 1;
        IntPtr h = IntPtr.Zero;
        try
        {
            int neg;
            if (WlanOpenHandle(2, IntPtr.Zero, out neg, out h) != 0) { err = L.T("Wi-Fi недоступний на цьому ПК."); return 1; }
            string xml = BuildXml(ssid, auth, pass, hidden);
            bool done = false;
            int last = 0;
            foreach (Guid g0 in Ifaces(h))
            {
                Guid g = g0;
                int reason;
                last = WlanSetProfile(h, ref g, 2, xml, null, true, IntPtr.Zero, out reason);
                if (last == 0) done = true;
            }
            if (done) return 0;
            err = L.T("Не вдалося додати мережу (код ") + last + ").";
            return last == 5 ? 5 : 1;
        }
        catch (Exception ex) { err = ex.Message; return 1; }
        finally { if (h != IntPtr.Zero) WlanCloseHandle(h, IntPtr.Zero); }
    }

    // з помічника з підвищенням: рядки P|профіль|пароль і F|профіль (base64)
    public static void ExecPlan(string[] lines, TextWriter w)
    {
        foreach (string raw in lines)
        {
            string[] p = raw.Trim().Split('|');
            if (p.Length < 2) continue;
            try
            {
                string name = U(p[1]), err;
                if (name.Length == 0 || name.Length > 64) { w.WriteLine(L.T("ERR|Некоректна назва мережі.")); continue; }
                int r;
                if (p[0] == "P" && p.Length >= 3) r = SetKey(name, U(p[2]), out err);
                else if (p[0] == "F") r = Forget(name, out err);
                else continue;
                w.WriteLine(r == 0 ? "OK|" + name : "ERR|" + (err.Length > 0 ? err : L.T("код ") + r));
            }
            catch (Exception ex) { w.WriteLine("ERR|" + ex.Message.Replace('\r', ' ').Replace('\n', ' ')); }
        }
    }

    // помічник: файл завдання + контрольна сума
    public static int RunFile(string file, string hash, TextWriter w)
    {
        byte[] raw = File.ReadAllBytes(file);
        try { File.Delete(file); } catch { }
        if (!ElevHash.Same(raw, hash)) { w.WriteLine(L.T("ERR|Файл завдання змінено після створення - нічого не виконано.")); return 1; }
        ExecPlan(Encoding.UTF8.GetString(raw).Split('\n'), w);
        return 0;
    }

    public static string PlanKey(string profile, string pass) { return "P|" + B(profile) + "|" + B(pass); }
    public static string PlanForget(string profile) { return "F|" + B(profile); }

    // ---------- обмін із помічником (рядки у base64, щоб жодні символи в назвах чи паролях не ламали розбір) ----------
    static string B(string s) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? "")); }
    static string U(string s) { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }

    public static string Pack(WifiNet w)
    {
        return "N|" + B(w.Name) + "|" + B(w.Ssid) + "|" + w.Auth + "|" + B(w.Sec) + "|" +
               (w.Hidden ? "1" : "0") + (w.Auto ? "1" : "0") + (w.Enterprise ? "1" : "0") + (w.Locked ? "1" : "0") + (w.Connected ? "1" : "0") + "|" + B(w.Pass);
    }

    public static WifiNet Unpack(string line)
    {
        try
        {
            string[] p = line.Trim().Split('|');
            if (p.Length < 7 || p[0] != "N" || p[5].Length < 5) return null;
            WifiNet w = new WifiNet();
            w.Name = U(p[1]); w.Ssid = U(p[2]); w.Auth = p[3]; w.Sec = U(p[4]);
            w.Hidden = p[5][0] == '1'; w.Auto = p[5][1] == '1'; w.Enterprise = p[5][2] == '1'; w.Locked = p[5][3] == '1'; w.Connected = p[5][4] == '1';
            w.Pass = U(p[6]);
            if (w.Auth != "nopass" && w.Auth != "WEP" && w.Auth != "WPA" && w.Auth != "SAE" && w.Auth != "ent") return null;
            return w.Name.Length > 0 ? w : null;
        }
        catch { return null; }
    }

    // викликається помічником з підвищенням: лише читає й виводить
    public static int Run(TextWriter w)
    {
        string err;
        List<WifiNet> l = Load(out err);
        foreach (WifiNet n in l) w.WriteLine(Pack(n));
        if (err.Length > 0) w.WriteLine("E|" + B(err));
        return 0;
    }

    public static List<WifiNet> ParseOut(string text, out string error)
    {
        error = "";
        List<WifiNet> l = new List<WifiNet>();
        foreach (string line in (text ?? "").Split('\n'))
        {
            if (line.StartsWith("E|", StringComparison.Ordinal)) { try { error = U(line.Substring(2).Trim()); } catch { } continue; }
            WifiNet w = Unpack(line);
            if (w != null) l.Add(w);
        }
        return l;
    }

    // текстовий список для передачі
    public static string Report(List<WifiNet> nets)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append(L.T("Мережі Wi-Fi, збережені на цьому ПК\r\n"));
        sb.Append(L.T("Файл містить паролі відкритим текстом - передавайте його лише тим, кому довіряєте.\r\n"));
        foreach (WifiNet n in nets)
        {
            sb.Append(L.T("\r\nМережа: ")).Append(n.Ssid);
            sb.Append(L.T("\r\nЗахист: ")).Append(n.Sec);
            if (n.Hidden) sb.Append(L.T(" (прихована мережа)"));
            if (n.Open) sb.Append(L.T("\r\nПароль: не потрібен"));
            else if (n.Enterprise) sb.Append(L.T("\r\nПароль: корпоративна мережа - вхід за обліковим записом"));
            else if (n.Pass.Length > 0) sb.Append(L.T("\r\nПароль: ")).Append(n.Pass);
            else sb.Append(L.T("\r\nПароль: недоступний (потрібні права адміністратора)"));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }
}
