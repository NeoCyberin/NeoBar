using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// ======================================================================
//  Мережна діагностика: перевірка DNS (власний запит A-запису до кожного сервера - видно, хто відповідає
//  і як швидко) та запуск системних утиліт (ipconfig, route, arp) із фіксованого переліку.
//  Користувацьких аргументів утиліти не отримують: лише готові команди. Очищення DNS-кешу й оновлення IP
//  потребують прав адміністратора - Windows запитає їх сама (помічник приймає лише ці два слова).
// ======================================================================
static class NetTools
{
    // ---------- DNS-запит ----------
    // пакет запиту A-запису; null, якщо назва не підходить для DNS
    public static byte[] BuildQuery(string host, ushort id)
    {
        if (string.IsNullOrEmpty(host)) return null;
        host = host.Trim().TrimEnd('.');
        string ascii;
        try { ascii = new IdnMapping().GetAscii(host); }
        catch { return null; }
        if (ascii.Length == 0 || ascii.Length > 253) return null;
        List<byte> b = new List<byte>();
        b.Add((byte)(id >> 8)); b.Add((byte)id);
        b.Add(0x01); b.Add(0x00);                       // RD
        b.Add(0); b.Add(1);                             // QDCOUNT
        for (int i = 0; i < 6; i++) b.Add(0);           // AN, NS, AR
        foreach (string lab in ascii.Split('.'))
        {
            if (lab.Length < 1 || lab.Length > 63) return null;
            foreach (char c in lab)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_')) return null;
            b.Add((byte)lab.Length);
            foreach (char c in lab) b.Add((byte)c);
        }
        b.Add(0);
        b.Add(0); b.Add(1);                             // QTYPE A
        b.Add(0); b.Add(1);                             // QCLASS IN
        return b.ToArray();
    }

    static int SkipName(byte[] d, int p, int len)
    {
        int guard = 0;
        while (p < len && guard++ < 130)
        {
            int l = d[p];
            if (l == 0) return p + 1;
            if ((l & 0xC0) == 0xC0) return p + 2 <= len ? p + 2 : -1;
            if ((l & 0xC0) != 0) return -1;
            p += 1 + l;
        }
        return -1;
    }

    // розбір відповіді: кількість знайдених IPv4 (адреси - в addrs), -1 - пакет пошкоджений чи не наш; rcode - код відповіді DNS
    public static int ParseResponse(byte[] d, int len, ushort id, List<string> addrs, out int rcode)
    {
        rcode = -1;
        if (d == null || len < 12 || len > d.Length) return -1;
        if (d[0] != (byte)(id >> 8) || d[1] != (byte)id) return -1;
        if ((d[2] & 0x80) == 0) return -1;                                  // це не відповідь
        rcode = d[3] & 0x0F;
        int qd = (d[4] << 8) | d[5], an = (d[6] << 8) | d[7];
        int p = 12;
        for (int i = 0; i < qd; i++)
        {
            p = SkipName(d, p, len);
            if (p < 0 || p + 4 > len) return -1;
            p += 4;
        }
        int n = 0;
        for (int i = 0; i < an && i < 64; i++)
        {
            p = SkipName(d, p, len);
            if (p < 0 || p + 10 > len) return n > 0 ? n : -1;
            int type = (d[p] << 8) | d[p + 1], rdl = (d[p + 8] << 8) | d[p + 9];
            p += 10;
            if (p + rdl > len) return n > 0 ? n : -1;
            if (type == 1 && rdl == 4) { addrs.Add(d[p] + "." + d[p + 1] + "." + d[p + 2] + "." + d[p + 3]); n++; }
            p += rdl;
        }
        return n;
    }

    static readonly Random rnd = new Random();

    // перевірка одного сервера; text - пояснення результату, ms - час відповіді
    public static bool Query(IPAddress server, string host, int timeoutMs, List<string> addrs, out double ms, out string text)
    {
        ms = 0; text = "";
        ushort id = (ushort)rnd.Next(1, 65535);
        byte[] q = BuildQuery(host, id);
        if (q == null) { text = L.T("назва не підходить для DNS"); return false; }
        try
        {
            using (UdpClient u = new UdpClient(server.AddressFamily))
            {
                u.Client.ReceiveTimeout = timeoutMs;
                u.Client.SendTimeout = timeoutMs;
                u.Connect(server, 53);
                Stopwatch sw = Stopwatch.StartNew();
                u.Send(q, q.Length);
                IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                byte[] r = u.Receive(ref ep);
                sw.Stop();
                ms = sw.Elapsed.TotalMilliseconds;
                int rc;
                int n = ParseResponse(r, r.Length, id, addrs, out rc);
                if (n < 0) { text = L.T("некоректна відповідь"); return false; }
                if (rc == 3) { text = L.T("такого імені немає (NXDOMAIN)"); return false; }
                if (rc != 0) { text = L.T("сервер повернув помилку (код ") + rc + ")"; return false; }
                if (n == 0) { text = L.T("відповідь без IPv4-адрес"); return false; }
                return true;
            }
        }
        catch (SocketException ex)
        {
            text = ex.SocketErrorCode == SocketError.TimedOut ? L.T("немає відповіді за ") + (timeoutMs / 1000) + L.T(" с") : L.T("помилка мережі (") + ex.SocketErrorCode + ")";
            return false;
        }
        catch (Exception ex) { text = ex.Message; return false; }
    }

    // ---------- порти ----------
    [DllImport("iphlpapi.dll", SetLastError = true)] static extern int GetExtendedTcpTable(IntPtr tbl, ref int size, bool sort, int af, int cls, int reserved);
    [DllImport("iphlpapi.dll", SetLastError = true)] static extern int GetExtendedUdpTable(IntPtr tbl, ref int size, bool sort, int af, int cls, int reserved);

    public sealed class PortRow
    {
        public string Proto = "", Addr = "", Proc = "";
        public int Port, Pid;
        public bool Local;                          // слухає лише на цьому ПК (127.x)
    }

    // порт у таблицях Windows зберігається в мережевому порядку байтів
    public static int PortOf(uint raw) { return (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF)); }

    public static string AddrOf(uint a) { return (a & 255) + "." + ((a >> 8) & 255) + "." + ((a >> 16) & 255) + "." + ((a >> 24) & 255); }

    static void ReadTable(List<PortRow> l, bool tcp)
    {
        int size = 0;
        int cls = tcp ? 3 : 1;                      // TCP_TABLE_OWNER_PID_LISTENER / UDP_TABLE_OWNER_PID
        IntPtr p = IntPtr.Zero;
        try
        {
            int rc = tcp ? GetExtendedTcpTable(IntPtr.Zero, ref size, true, 2, cls, 0) : GetExtendedUdpTable(IntPtr.Zero, ref size, true, 2, cls, 0);
            for (int attempt = 0; attempt < 4 && size > 0 && size < 16000000; attempt++)
            {
                if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
                p = Marshal.AllocHGlobal(size);
                rc = tcp ? GetExtendedTcpTable(p, ref size, true, 2, cls, 0) : GetExtendedUdpTable(p, ref size, true, 2, cls, 0);
                if (rc == 0) break;
                if (rc != 122) return;              // 122 - буфер замалий, пробуємо ще
            }
            if (rc != 0 || p == IntPtr.Zero) return;
            int n = Marshal.ReadInt32(p, 0), rs = tcp ? 24 : 12;
            for (int i = 0; i < n && i < 5000; i++)
            {
                long row = p.ToInt64() + 4 + (long)i * rs;
                uint addr = (uint)Marshal.ReadInt32(new IntPtr(row), tcp ? 4 : 0);
                uint port = (uint)Marshal.ReadInt32(new IntPtr(row), tcp ? 8 : 4);
                PortRow r = new PortRow();
                r.Proto = tcp ? "TCP" : "UDP";
                r.Addr = AddrOf(addr);
                r.Port = PortOf(port);
                r.Pid = Marshal.ReadInt32(new IntPtr(row), tcp ? 20 : 8);
                r.Local = (addr & 255) == 127;
                l.Add(r);
            }
        }
        finally { if (p != IntPtr.Zero) Marshal.FreeHGlobal(p); }
    }

    // що слухає цей ПК (IPv4): порт, програма. Лише читання системних таблиць.
    public static List<PortRow> ListeningPorts()
    {
        List<PortRow> l = new List<PortRow>();
        try { ReadTable(l, true); ReadTable(l, false); } catch { }
        Dictionary<int, string> names = new Dictionary<int, string>();
        foreach (PortRow r in l)
        {
            string nm;
            if (!names.TryGetValue(r.Pid, out nm))
            {
                nm = r.Pid == 0 ? L.T("система") : (r.Pid == 4 ? "System" : "?");
                if (r.Pid > 4) try { using (Process pr = Process.GetProcessById(r.Pid)) nm = pr.ProcessName; } catch { }
                names[r.Pid] = nm;
            }
            r.Proc = nm;
        }
        l.Sort(delegate (PortRow a, PortRow b)
        {
            if (a.Port != b.Port) return a.Port.CompareTo(b.Port);
            int c = string.CompareOrdinal(a.Proto, b.Proto);
            return c != 0 ? c : string.CompareOrdinal(a.Addr, b.Addr);
        });
        return l;
    }

    public static string PortName(int p)
    {
        switch (p)
        {
            case 20: case 21: return "FTP";
            case 22: return "SSH";
            case 23: return "Telnet";
            case 25: return "SMTP";
            case 53: return "DNS";
            case 67: case 68: return "DHCP";
            case 80: return "HTTP";
            case 110: return "POP3";
            case 123: return "NTP";
            case 135: return "RPC";
            case 137: case 138: case 139: return "NetBIOS";
            case 143: return "IMAP";
            case 161: return "SNMP";
            case 389: return "LDAP";
            case 443: return "HTTPS";
            case 445: return L.T("SMB (спільні файли)");
            case 993: return "IMAPS";
            case 995: return "POP3S";
            case 1433: return "SQL Server";
            case 1900: return "UPnP";
            case 3306: return "MySQL";
            case 3389: return L.T("Віддалений робочий стіл (RDP)");
            case 5353: return "mDNS";
            case 5432: return "PostgreSQL";
            case 5900: return "VNC";
            case 8080: return L.T("HTTP (альтернативний)");
            case 8443: return L.T("HTTPS (альтернативний)");
        }
        return "";
    }

    public static readonly int[] CommonPorts = { 21, 22, 23, 25, 53, 80, 110, 135, 139, 143, 443, 445, 993, 995, 1433, 3306, 3389, 5432, 5900, 8080, 8443 };

    // «адреса» чи «адреса:порт»; порт 0 - не вказано; false - порт некоректний
    public static bool SplitHostPort(string s, out string host, out int port)
    {
        host = (s ?? "").Trim(); port = 0;
        int c = host.LastIndexOf(':');
        if (c < 0 || host.IndexOf(':') != c) return true;                  // без порту (або IPv6 - не розбираємо)
        string ps = host.Substring(c + 1);
        host = host.Substring(0, c).Trim();
        int n;
        if (ps.Length == 0 || ps.Length > 5 || !int.TryParse(ps, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < 1 || n > 65535) return false;
        port = n;
        return host.Length > 0;
    }

    // 0 - відкритий, 1 - закритий (відмова), 2 - немає відповіді (фільтр чи вимкнено)
    public static int TcpProbe(IPAddress ip, int port, int timeoutMs, out double ms)
    {
        ms = 0;
        Socket s = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            Stopwatch sw = Stopwatch.StartNew();
            IAsyncResult ar = s.BeginConnect(ip, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return 2;
            try { s.EndConnect(ar); ms = sw.Elapsed.TotalMilliseconds; return 0; }
            catch (SocketException ex) { return ex.SocketErrorCode == SocketError.ConnectionRefused ? 1 : 2; }
        }
        catch { return 2; }
        finally { try { s.Close(); } catch { } }
    }

    // ---------- підмережа ----------
    // межі сканування: у великій мережі (префікс менше 24) беремо лише /24, у якій стоїть ПК
    public static bool SubnetRange(string ip, string mask, out uint first, out uint last, out uint self, out bool trimmed)
    {
        first = last = self = 0; trimmed = false;
        uint i, m;
        if (!ParseV4Loose(ip, out i) || !ParseV4Loose(mask, out m)) return false;
        if (((~m) & ((~m) + 1)) != 0 || m == 0) return false;
        int bits = 0;
        for (uint t = m; t != 0; t <<= 1) bits++;
        if (bits < 24) { m = 0xFFFFFF00u; trimmed = true; }
        if (bits > 30) return false;
        uint net = i & m, bc = net | ~m;
        first = net + 1; last = bc - 1; self = i;
        return last >= first;
    }

    static bool ParseV4Loose(string s, out uint v) { return NetCfg.ParseV4(s, out v); }

    static readonly Regex ArpLine = new Regex(@"^\s*(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\s+([0-9a-fA-F]{2}(?:-[0-9a-fA-F]{2}){5})\s", RegexOptions.Compiled);

    // IP -> MAC з таблиці ARP (без службових широкомовних записів)
    public static Dictionary<string, string> ArpTable()
    {
        Dictionary<string, string> d = new Dictionary<string, string>();
        RunLines("arp.exe", "-a", delegate (string s)
        {
            Match m = ArpLine.Match(s);
            if (!m.Success) return;
            string mac = m.Groups[2].Value.ToUpperInvariant();
            if (mac == "FF-FF-FF-FF-FF-FF" || mac.StartsWith("01-00-5E", StringComparison.Ordinal)) return;
            d[m.Groups[1].Value] = mac;
        }, null);
        return d;
    }

    // ---------- системні утиліти ----------
    static string SysExe(string name) { return Path.Combine(Environment.SystemDirectory, name); }

    // зупинка разом із дочірньою утилітою (сам cmd.exe лише обгортка)
    static void KillTree(Process pr)
    {
        try
        {
            ProcessStartInfo k = new ProcessStartInfo(SysExe("taskkill.exe"), "/PID " + pr.Id + " /T /F");
            k.UseShellExecute = false; k.CreateNoWindow = true;
            using (Process kp = Process.Start(k)) kp.WaitForExit(3000);
        }
        catch { }
        try { if (!pr.HasExited) pr.Kill(); } catch { }
    }

    // запускає утиліту й віддає рядки виводу; stop() - чи треба перервати
    public static int RunLines(string exe, string args, Action<string> line, Func<bool> stop)
    {
        try
        {
            // Вивід утиліт - у кодовій сторінці консолі. Українська OEM-сторінка 866 не має «і», «ї», «є» - утиліта сама
            // заміняє їх на «?» («П?дключення»). Тому запускаємо через cmd із UTF-8 (chcp 65001) і читаємо як UTF-8.
            ProcessStartInfo psi = new ProcessStartInfo(SysExe("cmd.exe"), "/d /c chcp 65001 >nul & \"" + SysExe(exe) + "\" " + args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            using (Process pr = Process.Start(psi))
            {
                pr.OutputDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) line(e.Data); };
                pr.BeginOutputReadLine();
                DateTime until = DateTime.Now.AddSeconds(60);
                while (!pr.WaitForExit(200))
                    if ((stop != null && stop()) || DateTime.Now > until) { KillTree(pr); return 1; }
                pr.WaitForExit();
                return pr.ExitCode;
            }
        }
        catch (Exception ex) { line(L.T("Не вдалося запустити: ") + ex.Message); return 1; }
    }

    // помічник із підвищенням (і прямий виклик, якщо права вже є): лише flushdns чи renew
    public static int IpCfg(string what, TextWriter w)
    {
        if (what == "flushdns") return RunLines("ipconfig.exe", "/flushdns", delegate (string s) { w.WriteLine(s); }, null);
        if (what == "renew")
        {
            int a = RunLines("ipconfig.exe", "/release", delegate (string s) { w.WriteLine(s); }, null);
            int b = RunLines("ipconfig.exe", "/renew", delegate (string s) { w.WriteLine(s); }, null);
            return a == 0 && b == 0 ? 0 : 1;
        }
        w.WriteLine(L.T("Невідома дія."));
        return 1;
    }
}
