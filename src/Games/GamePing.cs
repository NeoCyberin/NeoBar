using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

// ======================================================================
//  Пінг до сервера гри для оверлею. Лише читання системних даних, у процес гри нічого не впроваджується.
//    TCP: Windows сама веде RTT кожного з'єднання (GetPerTcpConnectionEStats) - беремо з'єднання гри з найбільшим трафіком.
//    UDP (більшість шутерів): адресу сервера видно лише з пакетів, тому поки гра на передньому плані й показник ввімкнено,
//    слухаємо вхідні UDP-пакети на портах гри (сирий сокет, потрібні права адміністратора) і пінгуємо ICMP той адрес,
//    з якого йде найбільше даних. Якщо сервер не відповідає на ICMP - пінгу немає (показується «—»).
//  Усе працює в окремому потоці, не частіше за раз на 2 с; коли показник не потрібен, потік і сокет закриваються.
// ======================================================================
static class GamePing
{
    [DllImport("iphlpapi.dll", SetLastError = true)] static extern int GetExtendedTcpTable(IntPtr tbl, ref int size, bool sort, int af, int cls, int reserved);
    [DllImport("iphlpapi.dll", SetLastError = true)] static extern int GetExtendedUdpTable(IntPtr tbl, ref int size, bool sort, int af, int cls, int reserved);
    [DllImport("iphlpapi.dll")] static extern int GetPerTcpConnectionEStats(IntPtr row, int type, IntPtr rw, uint rwVer, uint rwSize, IntPtr ros, uint rosVer, uint rosSize, IntPtr rod, uint rodVer, uint rodSize);
    [DllImport("iphlpapi.dll")] static extern int SetPerTcpConnectionEStats(IntPtr row, int type, IntPtr rw, uint rwVer, uint rwSize, uint offset);

    const int EstatsData = 1, EstatsPath = 3;       // TCP_ESTATS_TYPE
    const int PathRodSize = 160, DataRodSize = 96;  // TCP_ESTATS_PATH_ROD_v0 (40 ULONG), TCP_ESTATS_DATA_ROD_v0

    public static volatile float Current = float.NaN;   // мс, NaN - пінгу немає
    public static volatile string Source = "";          // звідки значення: «TCP» або «UDP»
    static DateTime okAt = DateTime.MinValue;

    static readonly object gate = new object();
    static Thread th;
    static volatile int pid;
    static volatile bool stop;
    static readonly AutoResetEvent wake = new AutoResetEvent(false);

    // pid > 0 - пінг потрібен для цього процесу гри; 0 - не потрібен
    public static void Use(int p)
    {
        if (p == pid && (p <= 0 || th != null)) return;
        pid = p;
        if (p <= 0) { Current = float.NaN; Source = ""; return; }
        lock (gate)
        {
            if (th != null) return;
            stop = false;
            th = new Thread(Loop);
            th.IsBackground = true;
            th.Name = "NeoBar ping";
            th.Priority = ThreadPriority.BelowNormal;
            th.Start();
        }
    }

    public static void Shutdown() { stop = true; pid = 0; try { wake.Set(); } catch { } StopSniffer(); }

    static void Loop()
    {
        int idle = 0;
        try
        {
            while (!stop)
            {
                int p = pid;
                if (p <= 0)
                {
                    StopSniffer();
                    Reset();
                    if (++idle > 8) break;              // показник не потрібен: потік закривається (з'явиться знову за потреби)
                    wake.WaitOne(1000);
                    continue;
                }
                idle = 0;
                try { Poll(p); } catch { Current = float.NaN; }
                if ((DateTime.Now - okAt).TotalSeconds > 8) { Current = float.NaN; Source = ""; }
                wake.WaitOne(2000);
            }
        }
        finally
        {
            StopSniffer();
            lock (gate) { th = null; }
        }
    }

    static void Reset()
    {
        lastBytes.Clear(); lastKey = ""; icmp.Clear(); Current = float.NaN; Source = "";
    }

    // ---------- вибір з'єднання ----------
    sealed class Cand
    {
        public string Key = "";
        public bool Tcp;
        public uint Ip;                 // адреса сервера (байти в мережевому порядку: a.b.c.d = a<<24|...)
        public long Delta;              // скільки байтів пройшло з минулого опитування
        public float Rtt = float.NaN;   // для TCP: згладжений RTT, мс
    }

    static readonly Dictionary<string, long> lastBytes = new Dictionary<string, long>();
    static string lastKey = "";
    static readonly List<float> icmp = new List<float>();
    static int icmpFails;

    static void Poll(int p)
    {
        List<Cand> list = new List<Cand>();
        ReadTcp(p, list);
        ReadUdp(p, list);

        Cand best = null, cur = null;
        foreach (Cand c in list)
        {
            if (c.Key == lastKey) cur = c;
            if (c.Delta > 0 && (best == null || c.Delta > best.Delta)) best = c;
        }
        // не стрибати між з'єднаннями: поточне лишається, поки воно живе й не набагато тихіше за найактивніше
        if (cur != null && cur.Delta > 0 && best != null && cur.Delta * 4 >= best.Delta) best = cur;
        if (best == null) return;
        if (best.Key != lastKey) { lastKey = best.Key; icmp.Clear(); icmpFails = 0; }

        float v = float.NaN;
        if (best.Tcp) v = best.Rtt;
        else v = IcmpRtt(best.Ip);
        if (!float.IsNaN(v)) { Current = v; Source = best.Tcp ? "TCP" : "UDP"; okAt = DateTime.Now; }
        else if (!best.Tcp && ++icmpFails >= 3) { Current = float.NaN; Source = ""; }
    }

    // ---------- TCP ----------
    static uint Flip(uint a) { return ((a & 255) << 24) | (((a >> 8) & 255) << 16) | (((a >> 16) & 255) << 8) | ((a >> 24) & 255); }

    // сервер, а не сам ПК: без loopback, link-local, multicast і broadcast
    static bool GoodRemote(uint ip)
    {
        uint a = ip >> 24, b = (ip >> 16) & 255;
        if (ip == 0 || a == 127 || a >= 224) return false;
        if (a == 169 && b == 254) return false;
        return true;
    }

    static bool Table(bool tcp, out IntPtr buf)
    {
        buf = IntPtr.Zero;
        int size = 0;
        int cls = tcp ? 5 : 1;                      // TCP_TABLE_OWNER_PID_ALL / UDP_TABLE_OWNER_PID
        IntPtr b = IntPtr.Zero;
        try
        {
            int rc = tcp ? GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, cls, 0) : GetExtendedUdpTable(IntPtr.Zero, ref size, false, 2, cls, 0);
            for (int attempt = 0; attempt < 4 && size > 0 && size < 16000000; attempt++)
            {
                if (b != IntPtr.Zero) Marshal.FreeHGlobal(b);
                b = Marshal.AllocHGlobal(size + 256);
                rc = tcp ? GetExtendedTcpTable(b, ref size, false, 2, cls, 0) : GetExtendedUdpTable(b, ref size, false, 2, cls, 0);
                if (rc == 0) { buf = b; return true; }
                if (rc != 122) break;
            }
        }
        catch { }
        if (b != IntPtr.Zero) Marshal.FreeHGlobal(b);
        return false;
    }

    static readonly HashSet<string> estatsTried = new HashSet<string>();

    static void ReadTcp(int p, List<Cand> list)
    {
        IntPtr t;
        if (!Table(true, out t)) return;
        IntPtr row = Marshal.AllocHGlobal(32), rod = Marshal.AllocHGlobal(256), rw = Marshal.AllocHGlobal(8);
        HashSet<string> alive = new HashSet<string>();
        try
        {
            int n = Marshal.ReadInt32(t, 0);
            int used = 0;
            for (int i = 0; i < n && i < 8000 && used < 24; i++)
            {
                long r = t.ToInt64() + 4 + (long)i * 24;
                if (Marshal.ReadInt32(new IntPtr(r), 20) != p) continue;
                if (Marshal.ReadInt32(new IntPtr(r), 0) != 5) continue;              // лише ESTABLISHED
                uint rip = Flip((uint)Marshal.ReadInt32(new IntPtr(r), 12));
                if (!GoodRemote(rip)) continue;
                used++;
                string key = "t" + Marshal.ReadInt32(new IntPtr(r), 4) + ":" + Marshal.ReadInt32(new IntPtr(r), 8) + ">" + rip + ":" + Marshal.ReadInt32(new IntPtr(r), 16);
                alive.Add(key);
                for (int k = 0; k < 20; k += 4) Marshal.WriteInt32(row, k, Marshal.ReadInt32(new IntPtr(r), k));   // MIB_TCPROW: стан, адреси й порти
                Cand c = new Cand();
                c.Key = key; c.Tcp = true; c.Ip = rip;

                for (int z = 0; z < DataRodSize; z += 4) Marshal.WriteInt32(rod, z, 0);
                if (GetPerTcpConnectionEStats(row, EstatsData, IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0, rod, 0, (uint)DataRodSize) != 0)
                {
                    // статистика для цього з'єднання ще не ввімкнена (потрібні права адміністратора): вмикаємо, дані з'являться з наступного опитування
                    if (estatsTried.Add(key))
                    {
                        Marshal.WriteByte(rw, 0, 1);
                        SetPerTcpConnectionEStats(row, EstatsData, rw, 0, 1, 0);
                        SetPerTcpConnectionEStats(row, EstatsPath, rw, 0, 1, 0);
                    }
                    continue;
                }
                long bytes = Marshal.ReadInt64(rod, 0) + Marshal.ReadInt64(rod, 16);      // DataBytesOut + DataBytesIn
                long prev;
                c.Delta = lastBytes.TryGetValue(key, out prev) ? Math.Max(0, bytes - prev) : 0;
                lastBytes[key] = bytes;

                for (int z = 0; z < PathRodSize; z += 4) Marshal.WriteInt32(rod, z, 0);
                if (GetPerTcpConnectionEStats(row, EstatsPath, IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0, rod, 0, (uint)PathRodSize) == 0)
                {
                    uint srtt = (uint)Marshal.ReadInt32(rod, 27 * 4), cnt = (uint)Marshal.ReadInt32(rod, 32 * 4);   // SmoothedRtt, CountRtt
                    if (cnt > 0 && srtt > 0 && srtt < 10000) c.Rtt = srtt;
                }
                list.Add(c);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(row); Marshal.FreeHGlobal(rod); Marshal.FreeHGlobal(rw);
            Marshal.FreeHGlobal(t);
        }
        // забути завершені з'єднання
        List<string> dead = new List<string>();
        foreach (string k in lastBytes.Keys) if (k[0] == 't' && !alive.Contains(k)) dead.Add(k);
        foreach (string k in dead) { lastBytes.Remove(k); estatsTried.Remove(k); }
    }

    // ---------- UDP ----------
    static readonly object udpGate = new object();
    static Dictionary<uint, long> udpBytes = new Dictionary<uint, long>();      // адреса сервера -> байтів, що прийшли з минулого опитування
    static volatile HashSet<int> ports = new HashSet<int>();                    // локальні UDP-порти гри
    static DateTime sniffFailAt = DateTime.MinValue;

    static void ReadUdp(int p, List<Cand> list)
    {
        IntPtr t;
        if (Table(false, out t))
        {
            try
            {
                HashSet<int> ps = new HashSet<int>();
                int n = Marshal.ReadInt32(t, 0);
                for (int i = 0; i < n && i < 8000; i++)
                {
                    long r = t.ToInt64() + 4 + (long)i * 12;
                    if (Marshal.ReadInt32(new IntPtr(r), 8) != p) continue;
                    uint port = (uint)Marshal.ReadInt32(new IntPtr(r), 4);
                    ps.Add((int)(((port & 255) << 8) | ((port >> 8) & 255)));
                }
                ports = ps;
            }
            finally { Marshal.FreeHGlobal(t); }
        }
        if (ports.Count == 0) { StopSniffer(); return; }
        StartSniffer();
        Dictionary<uint, long> snap;
        lock (udpGate) { snap = udpBytes; udpBytes = new Dictionary<uint, long>(); }
        foreach (KeyValuePair<uint, long> kv in snap)
        {
            if (kv.Value < 400) continue;           // поодинокі пакети (службові) не рахуємо
            Cand c = new Cand();
            c.Key = "u" + kv.Key; c.Ip = kv.Key; c.Delta = kv.Value;
            list.Add(c);
        }
    }

    static Socket sock;
    static Thread sniffTh;
    static volatile bool sniffStop;
    static uint localIp;

    static void StartSniffer()
    {
        if (sock != null || (DateTime.Now - sniffFailAt).TotalSeconds < 60) return;
        try
        {
            IPAddress me;
            using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                s.Connect(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9));      // пакет не відправляється: лише вибір інтерфейсу за маршрутом
                me = ((IPEndPoint)s.LocalEndPoint).Address;
            }
            byte[] mb = me.GetAddressBytes();
            localIp = ((uint)mb[0] << 24) | ((uint)mb[1] << 16) | ((uint)mb[2] << 8) | mb[3];
            Socket sk = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
            try
            {
                sk.Bind(new IPEndPoint(me, 0));
                sk.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.HeaderIncluded, true);
                sk.IOControl(IOControlCode.ReceiveAll, BitConverter.GetBytes(1), null);     // SIO_RCVALL: усі IPv4-пакети інтерфейсу
                sk.ReceiveTimeout = 1000;
            }
            catch { sk.Close(); throw; }
            sock = sk;
            sniffStop = false;
            sniffTh = new Thread(Sniff);
            sniffTh.IsBackground = true;
            sniffTh.Name = "NeoBar udp";
            sniffTh.Priority = ThreadPriority.BelowNormal;
            sniffTh.Start(sk);
        }
        catch { sniffFailAt = DateTime.Now; sock = null; }       // немає прав або сокет недоступний: лишається лише TCP
    }

    static void StopSniffer()
    {
        sniffStop = true;
        Socket s = sock;
        sock = null;
        if (s != null) try { s.Close(); } catch { }
        lock (udpGate) { udpBytes.Clear(); }
    }

    static void Sniff(object o)
    {
        Socket s = (Socket)o;
        byte[] b = new byte[65536];
        while (!sniffStop)
        {
            int n;
            try { n = s.Receive(b); }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.TimedOut) continue;
                break;
            }
            catch { break; }
            try
            {
                if (n < 28 || (b[0] >> 4) != 4 || b[9] != 17) continue;                  // IPv4 і UDP
                int ihl = (b[0] & 15) * 4;
                if (ihl < 20 || n < ihl + 8) continue;
                if ((((b[6] & 0x1F) << 8) | b[7]) != 0) continue;                        // не перший фрагмент
                uint src = ((uint)b[12] << 24) | ((uint)b[13] << 16) | ((uint)b[14] << 8) | b[15];
                uint dst = ((uint)b[16] << 24) | ((uint)b[17] << 16) | ((uint)b[18] << 8) | b[19];
                int sport = (b[ihl] << 8) | b[ihl + 1], dport = (b[ihl + 2] << 8) | b[ihl + 3];
                HashSet<int> ps = ports;
                uint remote;
                if (dst == localIp && ps.Contains(dport)) remote = src;                  // пакет гравцю
                else if (src == localIp && ps.Contains(sport)) remote = dst;             // пакет від гравця (якщо система його показує)
                else continue;
                if (!GoodRemote(remote) || remote == localIp) continue;
                int len = (b[2] << 8) | b[3];
                lock (udpGate)
                {
                    long cur;
                    udpBytes.TryGetValue(remote, out cur);
                    if (udpBytes.Count < 512) udpBytes[remote] = cur + len;
                }
            }
            catch { }
        }
    }

    // ---------- ICMP для UDP-серверів ----------
    static float IcmpRtt(uint ip)
    {
        try
        {
            IPAddress a = new IPAddress(new byte[] { (byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip });
            using (Ping pg = new Ping())
            {
                PingReply r = pg.Send(a, 900);
                if (r == null || r.Status != IPStatus.Success) return float.NaN;
                icmp.Add(r.RoundtripTime);
                while (icmp.Count > 3) icmp.RemoveAt(0);
                icmpFails = 0;
                List<float> s = new List<float>(icmp);
                s.Sort();
                return s[s.Count / 2];          // медіана останніх трьох: поодинокий сплеск не шарпає показник
            }
        }
        catch { return float.NaN; }
    }
}
