using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;

// ======================================================================
//  Мережеві адаптери: читання стану (без прав адміністратора) і зміна налаштувань IPv4/DNS, увімкнення/вимкнення.
//
//  Безпека:
//   - Змінює мережу лише системний netsh.exe із каталогу Windows, а аргументи складаються тільки з перевірених
//     частин: адаптер знаходиться за ідентифікатором у списку системи (назва береться звідти ж і відхиляється,
//     якщо містить лапки, зворотну скісну риску чи керівні символи), адреси проходять сувору перевірку й
//     записуються у власному записі (a.b.c.d), а не так, як їх ввів користувач.
//   - Помічник із підвищенням отримує файл завдання з контрольною сумою SHA-256, яку передано в командному рядку:
//     підміна файла між створенням і запуском не пройде. Кожен рядок перевіряється заново вже в помічнику.
//   - Без прав адміністратора нічого не змінюється й не обходиться: Windows сама запитає дозвіл (UAC).
// ======================================================================
sealed class AdapterInfo
{
    public string Id = "", Name = "", Descr = "", Mac = "";
    public bool Up, Wireless, Virtual, Dhcp, DnsAuto = true, Disabled;     // Disabled - вимкнений у Windows (системний список мережевих інтерфейсів його не віддає)
    public long Speed;                                              // біт/с, -1 - невідомо
    public readonly List<string> Ips = new List<string>(), Masks = new List<string>(), Gateways = new List<string>(), Dns = new List<string>();

    public string Status { get { return Disabled ? L.T("Вимкнено") : (Up ? L.T("Підключено") : L.T("Немає зв'язку (кабель чи мережа)")); } }
    public string Kind { get { return Wireless ? "Wi-Fi" : (Virtual ? L.T("Віртуальний") : L.T("Дротовий")); } }
}

static class NetCfg
{
    // ---------- читання ----------
    static bool Skip(NetworkInterface ni)
    {
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) return true;
        string d = (ni.Description ?? "");
        return d.IndexOf("WAN Miniport", StringComparison.OrdinalIgnoreCase) >= 0 || d.IndexOf("Pseudo-Interface", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static bool IsVirtual(string d, string n)
    {
        string s = (d + " " + n).ToLowerInvariant();
        string[] k = { "virtual", "vmware", "hyper-v", "vethernet", "tap-", "tap windows", "wintun", "wireguard", "vpn", "bluetooth", "vmnet", "docker", "loopback", "tailscale", "zerotier", "tunnel", "nordlynx", "openvpn" };
        foreach (string x in k) if (s.IndexOf(x, StringComparison.Ordinal) >= 0) return true;
        return false;
    }

    public static List<AdapterInfo> Load()
    {
        List<AdapterInfo> res = new List<AdapterInfo>();
        NetworkInterface[] all;
        try { all = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return res; }
        foreach (NetworkInterface ni in all)
        {
            try
            {
                if (Skip(ni)) continue;
                AdapterInfo a = new AdapterInfo();
                a.Id = ni.Id ?? ""; a.Name = ni.Name ?? ""; a.Descr = ni.Description ?? "";
                a.Up = ni.OperationalStatus == OperationalStatus.Up;
                a.Wireless = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                a.Virtual = !a.Wireless && IsVirtual(a.Descr, a.Name);
                try { a.Speed = ni.Speed; } catch { a.Speed = -1; }
                try
                {
                    byte[] mb = ni.GetPhysicalAddress().GetAddressBytes();
                    StringBuilder sb = new StringBuilder();
                    foreach (byte b in mb) { if (sb.Length > 0) sb.Append('-'); sb.Append(b.ToString("X2")); }
                    a.Mac = sb.ToString();
                }
                catch (Exception skipEx) { SbLog.Skip(skipEx); }
                IPInterfaceProperties ip = ni.GetIPProperties();
                try { IPv4InterfaceProperties v4 = ip.GetIPv4Properties(); a.Dhcp = v4 != null && v4.IsDhcpEnabled; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                foreach (UnicastIPAddressInformation u in ip.UnicastAddresses)
                {
                    if (u.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    a.Ips.Add(u.Address.ToString());
                    string m = "";
                    try { m = u.IPv4Mask != null ? u.IPv4Mask.ToString() : ""; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                    a.Masks.Add(m);
                }
                foreach (GatewayIPAddressInformation g in ip.GatewayAddresses)
                    if (g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && g.Address.ToString() != "0.0.0.0") a.Gateways.Add(g.Address.ToString());
                foreach (System.Net.IPAddress d in ip.DnsAddresses)
                    if (d.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) a.Dns.Add(d.ToString());
                a.DnsAuto = !StaticDns(a.Id);
                res.Add(a);
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
        HashSet<string> present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AdapterInfo a in res) present.Add(a.Id);
        foreach (AdapterInfo d in DisabledList(present)) res.Add(d);
        res.Sort(delegate (AdapterInfo x, AdapterInfo y)
        {
            int kx = (x.Disabled ? 4 : (x.Up ? 0 : 2)) + (x.Virtual ? 1 : 0), ky = (y.Disabled ? 4 : (y.Up ? 0 : 2)) + (y.Virtual ? 1 : 0);
            if (kx != ky) return kx.CompareTo(ky);
            return string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase);
        });
        return res;
    }

    // Вимкнені адаптери Windows не віддає через NetworkInterface. Їх видно в реєстрі: підключення з мережевого класу,
    // чий пристрій позначено як вимкнений (ConfigFlags, біт 1). Лише читання; видалені й просто відключені пристрої не підходять.
    const string NetClass = @"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}";

    static List<AdapterInfo> DisabledList(HashSet<string> present)
    {
        List<AdapterInfo> res = new List<AdapterInfo>();
        try
        {
            using (Microsoft.Win32.RegistryKey cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(NetClass))
            {
                if (cls == null) return res;
                foreach (string sub in cls.GetSubKeyNames())
                {
                    try
                    {
                        if (!IsGuid(sub) || present.Contains(sub)) continue;
                        string name, pnp;
                        int media = 0;
                        using (Microsoft.Win32.RegistryKey c = cls.OpenSubKey(sub + "\\Connection"))
                        {
                            if (c == null) continue;
                            name = c.GetValue("Name") as string;
                            pnp = c.GetValue("PnpInstanceID") as string;
                            object mo = c.GetValue("MediaSubType");
                            if (mo is int) media = (int)mo;
                        }
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(pnp) || pnp.Length > 200 || pnp.IndexOf("..", StringComparison.Ordinal) >= 0) continue;
                        using (Microsoft.Win32.RegistryKey dev = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + pnp))
                        {
                            if (dev == null) continue;
                            object fo = dev.GetValue("ConfigFlags");
                            if (!(fo is int) || ((int)fo & 1) == 0) continue;
                            string desc = (dev.GetValue("FriendlyName") as string) ?? (dev.GetValue("DeviceDesc") as string) ?? "";
                            int sc = desc.LastIndexOf(';');
                            if (sc >= 0) desc = desc.Substring(sc + 1);
                            if (desc.IndexOf("WAN Miniport", StringComparison.OrdinalIgnoreCase) >= 0 || desc.IndexOf("Pseudo-Interface", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            AdapterInfo a = new AdapterInfo();
                            a.Id = sub; a.Name = name; a.Descr = desc.Length > 0 ? desc : name;
                            a.Disabled = true; a.Up = false; a.Speed = -1;
                            a.Wireless = media == 2 || desc.IndexOf("Wi-Fi", StringComparison.OrdinalIgnoreCase) >= 0 || desc.IndexOf("Wireless", StringComparison.OrdinalIgnoreCase) >= 0 || desc.IndexOf("802.11", StringComparison.Ordinal) >= 0;
                            a.Virtual = !a.Wireless && IsVirtual(a.Descr, a.Name);
                            res.Add(a);
                        }
                    }
                    catch (Exception skipEx) { SbLog.Skip(skipEx); }
                }
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return res;
    }

    // DNS задано вручну, якщо в параметрах інтерфейсу є NameServer (лише читання реєстру)
    static bool StaticDns(string id)
    {
        try
        {
            if (!IsGuid(id)) return false;
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + id))
            {
                if (k == null) return false;
                string s = k.GetValue("NameServer") as string;
                return !string.IsNullOrEmpty(s) && s.Trim().Length > 0;
            }
        }
        catch { return false; }
    }

    public static string SpeedText(long bps)
    {
        if (bps <= 0 || bps > 400000000000L) return L.T("невідомо");
        if (bps >= 1000000000L) return Math.Round(bps / 1e9, 1).ToString(CultureInfo.InvariantCulture) + L.T(" Гбіт/с");
        if (bps >= 1000000L) return Math.Round(bps / 1e6).ToString(CultureInfo.InvariantCulture) + L.T(" Мбіт/с");
        return Math.Round(bps / 1e3).ToString(CultureInfo.InvariantCulture) + L.T(" Кбіт/с");
    }

    // ---------- перевірка введеного ----------
    public static bool ParseV4(string s, out uint v)
    {
        v = 0;
        if (s == null) return false;
        string[] p = s.Trim().Split('.');
        if (p.Length != 4) return false;
        uint r = 0;
        foreach (string x in p)
        {
            if (x.Length < 1 || x.Length > 3) return false;
            if (x.Length > 1 && x[0] == '0') return false;               // 010 - двозначно (вісімкове чи десяткове), не приймаємо
            foreach (char c in x) if (c < '0' || c > '9') return false;
            int n = int.Parse(x, CultureInfo.InvariantCulture);
            if (n > 255) return false;
            r = (r << 8) | (uint)n;
        }
        v = r;
        return true;
    }

    public static string V4Text(uint v)
    {
        return ((v >> 24) & 255) + "." + ((v >> 16) & 255) + "." + ((v >> 8) & 255) + "." + (v & 255);
    }

    // маска: 255.255.255.0, «24» чи «/24»; довжина префікса 8-30, одиниці йдуть підряд
    public static bool ParseMask(string s, out uint m)
    {
        m = 0;
        if (s == null) return false;
        s = s.Trim();
        if (s.StartsWith("/", StringComparison.Ordinal)) s = s.Substring(1);
        int n;
        if (s.IndexOf('.') < 0)
        {
            if (s.Length < 1 || s.Length > 2) return false;
            foreach (char c in s) if (c < '0' || c > '9') return false;
            n = int.Parse(s, CultureInfo.InvariantCulture);
            if (n < 8 || n > 30) return false;
            m = 0xFFFFFFFFu << (32 - n);
            return true;
        }
        uint v;
        if (!ParseV4(s, out v)) return false;
        uint inv = ~v;
        if ((inv & (inv + 1)) != 0) return false;                         // не суцільна маска
        n = 0;
        for (uint t = v; t != 0; t <<= 1) n++;
        if (n < 8 || n > 30) return false;
        m = v;
        return true;
    }

    static bool Unicast(uint ip)
    {
        uint a = ip >> 24;
        return a >= 1 && a <= 223 && a != 127;
    }

    // "" - усе гаразд, інакше пояснення українською
    public static string CheckStatic(string ip, string mask, string gw)
    {
        uint i, m, g;
        if (!ParseV4(ip, out i)) return L.T("IP-адреса має вигляд 192.168.1.10 (чотири числа 0-255).");
        if (!Unicast(i)) return L.T("Ця IP-адреса не підходить для комп'ютера (не можна 0.x, 127.x та 224 і вище).");
        if (!ParseMask(mask, out m)) return L.T("Маска підмережі має вигляд 255.255.255.0 (або довжина префікса від 8 до 30).");
        if ((i & ~m) == 0) return L.T("Це адреса самої мережі - виберіть іншу.");
        if ((i & ~m) == ~m) return L.T("Це широкомовна адреса мережі - виберіть іншу.");
        if (gw != null && gw.Trim().Length > 0)
        {
            if (!ParseV4(gw, out g)) return L.T("Шлюз має вигляд 192.168.1.1 (чотири числа 0-255).");
            if (!Unicast(g)) return L.T("Ця адреса шлюзу не підходить.");
            if ((g & m) != (i & m)) return L.T("Шлюз має бути в тій самій підмережі, що й IP-адреса.");
            if (g == i) return L.T("Шлюз не може збігатися з IP-адресою.");
            if ((g & ~m) == 0 || (g & ~m) == ~m) return L.T("Шлюз не може бути адресою мережі чи широкомовною адресою.");
        }
        return "";
    }

    public static string CheckDns(string d1, string d2)
    {
        uint a, b;
        if (d1 == null || d1.Trim().Length == 0) return L.T("Уведіть хоча б один DNS-сервер.");
        if (!ParseV4(d1, out a) || !Unicast(a)) return L.T("Основний DNS має вигляд 8.8.8.8 (чотири числа 0-255).");
        if (d2 != null && d2.Trim().Length > 0)
        {
            if (!ParseV4(d2, out b) || !Unicast(b)) return L.T("Додатковий DNS має вигляд 8.8.4.4 (чотири числа 0-255).");
            if (a == b) return L.T("Основний і додатковий DNS однакові.");
        }
        return "";
    }

    // ---------- завдання (рядки через |) ----------
    public static string PlAddrDhcp(string id) { return "AD|" + id; }

    public static string PlAddrStatic(string id, string ip, string mask, string gw)
    {
        uint i, m, g;
        ParseV4(ip, out i); ParseMask(mask, out m);
        string gs = gw != null && gw.Trim().Length > 0 && ParseV4(gw, out g) ? V4Text(g) : "";
        return "AS|" + id + "|" + V4Text(i) + "|" + V4Text(m) + "|" + gs;
    }

    public static string PlDnsDhcp(string id) { return "DD|" + id; }

    public static string PlDnsStatic(string id, string d1, string d2)
    {
        uint a, b;
        ParseV4(d1, out a);
        string s2 = d2 != null && d2.Trim().Length > 0 && ParseV4(d2, out b) ? V4Text(b) : "";
        return "DS|" + id + "|" + V4Text(a) + "|" + s2;
    }

    public static string PlState(string id, bool on) { return "ST|" + id + "|" + (on ? "on" : "off"); }

    // ---------- виконання (процес із правами адміністратора) ----------
    public static bool IsGuid(string id)
    {
        if (id == null || id.Length < 30 || id.Length > 40) return false;
        foreach (char c in id) if (!(Uri.IsHexDigit(c) || c == '{' || c == '}' || c == '-')) return false;
        return true;
    }

    // назва адаптера за ідентифікатором: лише з системного списку й лише безпечна для командного рядка
    public static string NameOf(string id)
    {
        if (!IsGuid(id)) return null;
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!string.Equals(ni.Id, id, StringComparison.OrdinalIgnoreCase) || Skip(ni)) continue;
                string n = ni.Name;
                if (string.IsNullOrEmpty(n) || n.Length > 100) return null;
                foreach (char c in n) if (c == '"' || c == '\\' || c < 32) return null;
                return n;
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        // вимкнений адаптер: його можна лише знову ввімкнути (інші дії netsh сам відхилить)
        foreach (AdapterInfo d in DisabledList(new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
        {
            if (!string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
            string n = d.Name;
            if (string.IsNullOrEmpty(n) || n.Length > 100) return null;
            foreach (char c in n) if (c == '"' || c == '\\' || c < 32) return null;
            return n;
        }
        return null;
    }

    static string FirstLine(string s)
    {
        foreach (string l in (s ?? "").Split('\n'))
        {
            string t = l.Trim();
            if (t.Length == 0) continue;
            StringBuilder sb = new StringBuilder();
            foreach (char c in t) if (c >= 32) sb.Append(c);
            return sb.Length > 160 ? sb.ToString(0, 160) : sb.ToString();
        }
        return "";
    }

    static int Netsh(string args, out string output)
    {
        output = "";
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            try { psi.StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            StringBuilder sb = new StringBuilder();
            using (Process pr = Process.Start(psi))
            {
                pr.OutputDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                pr.BeginOutputReadLine();
                if (!pr.WaitForExit(30000)) { try { pr.Kill(); } catch (Exception skipEx) { SbLog.Skip(skipEx); } output = L.T("час очікування вичерпано"); return 1; }
                pr.WaitForExit();
                lock (sb) output = FirstLine(sb.ToString());
                return pr.ExitCode;
            }
        }
        catch (Exception ex) { output = ex.Message; return 1; }
    }

    // викликається з помічника (або напряму, якщо вікно вже має права): кожен рядок перевіряється наново
    public static void Exec(string[] lines, TextWriter w)
    {
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            string[] p = line.Split('|');
            try
            {
                string name = NameOf(p.Length > 1 ? p[1] : "");
                if (name == null) { w.WriteLine(L.T("ERR|Адаптер не знайдено або його не можна змінювати.")); continue; }
                string err = "", a1 = null, a2 = null;
                string q = "name=\"" + name + "\"";
                switch (p[0])
                {
                    case "AD":
                        a1 = "interface ipv4 set address " + q + " source=dhcp";
                        break;
                    case "AS":
                        if (p.Length < 5) { err = L.T("Неповне завдання."); break; }
                        err = CheckStatic(p[2], p[3], p[4]);
                        if (err.Length > 0) break;
                        {
                            uint i, m, g;
                            ParseV4(p[2], out i); ParseMask(p[3], out m);
                            a1 = "interface ipv4 set address " + q + " source=static address=" + V4Text(i) + " mask=" + V4Text(m);
                            if (p[4].Trim().Length > 0 && ParseV4(p[4], out g)) a1 += " gateway=" + V4Text(g);
                        }
                        break;
                    case "DD":
                        a1 = "interface ipv4 set dnsservers " + q + " source=dhcp";
                        break;
                    case "DS":
                        if (p.Length < 4) { err = L.T("Неповне завдання."); break; }
                        err = CheckDns(p[2], p[3]);
                        if (err.Length > 0) break;
                        {
                            uint a, b;
                            ParseV4(p[2], out a);
                            a1 = "interface ipv4 set dnsservers " + q + " source=static address=" + V4Text(a) + " validate=no";
                            if (p[3].Trim().Length > 0 && ParseV4(p[3], out b)) a2 = "interface ipv4 add dnsservers " + q + " address=" + V4Text(b) + " index=2 validate=no";
                        }
                        break;
                    case "ST":
                        if (p.Length < 3 || (p[2] != "on" && p[2] != "off")) { err = L.T("Неповне завдання."); break; }
                        a1 = "interface set interface " + q + " admin=" + (p[2] == "on" ? "enabled" : "disabled");
                        break;
                    default: err = L.T("Невідома дія."); break;
                }
                if (err.Length > 0) { w.WriteLine("ERR|" + err); continue; }
                string o;
                int code = Netsh(a1, out o);
                if (code == 0 && a2 != null) code = Netsh(a2, out o);
                w.WriteLine(code == 0 ? "OK|" + name : "ERR|" + name + ": " + (o.Length > 0 ? o : L.T("помилка netsh (код ") + code + ")"));
            }
            catch (Exception ex) { w.WriteLine("ERR|" + ex.Message.Replace('\r', ' ').Replace('\n', ' ')); }
        }
    }

    // помічник: файл завдання + контрольна сума
    public static int RunFile(string file, string hash, TextWriter w)
    {
        byte[] raw = File.ReadAllBytes(file);
        try { File.Delete(file); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        if (!ElevHash.Same(raw, hash)) { w.WriteLine(L.T("ERR|Файл завдання змінено після створення - нічого не виконано.")); return 1; }
        Exec(Encoding.UTF8.GetString(raw).Split('\n'), w);
        return 0;
    }

    // запуск помічника з підвищенням (op: netcfg чи wifiset); повертає вивід або null з поясненням у err
    public static string RunElevated(string op, List<string> lines, out string err)
    {
        err = "";
        string planFile = Path.Combine(Path.GetTempPath(), "neobar-" + op + "-plan-" + DateTime.Now.Ticks + ".txt");
        byte[] raw = new UTF8Encoding(false).GetBytes(string.Join("\n", lines.ToArray()) + "\n");
        try { File.WriteAllBytes(planFile, raw); }
        catch (Exception ex) { err = ex.Message; return null; }
        string outFile = Elevated.Run(op, "\"" + planFile + "\" " + ElevHash.Of(raw), out err);
        if (outFile == null) { try { File.Delete(planFile); } catch (Exception skipEx) { SbLog.Skip(skipEx); } return null; }
        DateTime until = DateTime.Now.AddMinutes(5);
        while (DateTime.Now < until)
        {
            Thread.Sleep(250);
            bool done;
            string t = Elevated.ReadOut(outFile, out done);
            if (done) { try { File.Delete(outFile); } catch (Exception skipEx) { SbLog.Skip(skipEx); } return t; }
        }
        err = L.T("Помічник не відповів.");
        return null;
    }

    // зміна адаптерів: у цьому процесі, якщо вже є права адміністратора, інакше через помічника (запит UAC)
    public static string Apply(List<string> lines, out string err)
    {
        err = "";
        if (Elevated.IsAdmin)
        {
            StringWriter sw = new StringWriter();
            Exec(lines.ToArray(), sw);
            return sw.ToString();
        }
        return RunElevated("netcfg", lines, out err);
    }

    // підсумок виводу: скільки OK, перша помилка
    public static int Summary(string text, out string firstErr, out int bad)
    {
        int ok = 0;
        bad = 0;
        firstErr = "";
        foreach (string l in (text ?? "").Split('\n'))
        {
            string t = l.Trim();
            if (t.StartsWith("OK|", StringComparison.Ordinal)) ok++;
            else if (t.StartsWith("ERR|", StringComparison.Ordinal)) { bad++; if (firstErr.Length == 0) firstErr = t.Substring(4); }
        }
        return ok;
    }
}
