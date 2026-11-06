using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;

// ======================================================================
//  Вкладка «Діагностика» вікна «Мережа»: додаткові інструменти - перевірка DNS, порти ПК і хоста,
//  скан підмережі, очищення DNS-кешу, оновлення IP, таблиця маршрутів і ARP. Вивід іде в те саме поле, що й пінг.
// ======================================================================
sealed partial class NetWindow
{
    int DiagTools(Graphics g, Rectangle r, int y)
    {
        int ch = Px(30);
        bool ok = !running;
        // перший ряд - чотири кнопки, другий - три
        int w4 = (r.Width - Px(6) * 3) / 4;
        Rectangle[] a = new Rectangle[4];
        for (int i = 0; i < 4; i++) a[i] = new Rectangle(r.X + i * (w4 + Px(6)), y, w4, ch);
        Btn(g, a[0], L.T("Перевірка DNS"), StartDns, 0, ok);
        Btn(g, a[1], L.T("Порти ПК"), StartLocalPorts, 0, ok);
        Btn(g, a[2], L.T("Порти хоста"), StartHostPorts, 0, ok);
        Btn(g, a[3], L.T("Маршрути"), delegate { StartTool(L.T("route print - таблиця маршрутів"), "route.exe", "print"); }, 0, ok);
        Tip(a[0], L.T("Запитує адресу (поле вище; для IP береться google.com) у DNS ваших адаптерів і в публічних серверах: видно, хто відповідає й як швидко"));
        Tip(a[1], L.T("Які порти слухає цей ПК і яка програма: що доступне з мережі, а що лише локально"));
        Tip(a[2], L.T("Перевіряє популярні порти на адресі з поля (або один порт, якщо дописати :порт, наприклад 192.168.0.1:3389). Перевіряйте лише свої пристрої"));
        Tip(a[3], L.T("Показує, куди система надсилає трафік"));
        y += ch + Px(6);
        int w3 = (r.Width - Px(6) * 2) / 3;
        Rectangle b0 = new Rectangle(r.X, y, w3, ch), b1 = new Rectangle(r.X + w3 + Px(6), y, w3, ch), b2 = new Rectangle(r.X + (w3 + Px(6)) * 2, y, w3, ch);
        Btn(g, b0, L.T("ARP-таблиця"), delegate { StartTool(L.T("arp -a - пристрої, з якими щойно спілкувався ПК"), "arp.exe", "-a"); }, 0, ok);
        Btn(g, b1, L.T("Очистити DNS-кеш"), delegate { StartIp("flushdns"); }, 0, ok);
        Btn(g, b2, L.T("Оновити IP…"), delegate { StartIp("renew"); }, 0, ok);
        Tip(b0, L.T("Показує IP і MAC-адреси сусідніх пристроїв у локальній мережі"));
        Tip(b1, L.T("ipconfig /flushdns: забуває збережені відповіді DNS (потрібен дозвіл адміністратора)"));
        Tip(b2, L.T("ipconfig /release і /renew: нова IP-адреса від роутера; зв'язок на кілька секунд обірветься (потрібен дозвіл адміністратора)"));
        return y + ch + Px(10);
    }

    static string Kind(string s)
    {
        return s.Length > 0 && s[0] != ' ' && s.TrimEnd().EndsWith(":", StringComparison.Ordinal) ? "head" : "";
    }

    void StartTool(string title, string exe, string args)
    {
        Begin(title);
        follow = false;
        outScroll = 0;
        int id = runId;
        Util.Bg(delegate
        {
            int code = NetTools.RunLines(exe, args, delegate (string s) { Add(id, s, Kind(s)); }, delegate { return id != runId; });
            if (id == runId && code != 0) Add(id, L.T("Команда завершилась із помилкою (код ") + code + ")", "bad");
            Finish(id);
        });
    }

    void StartIp(string op)
    {
        if (op == "renew" && Ask(L.T("Мережа"), L.T("Оновити IP-адресу?\n\nБуде виконано ipconfig /release і /renew для адаптерів з автоматичним IP. Зв'язок на кілька секунд обірветься, потім відновиться (роутер може видати іншу адресу)."),
                                 L.T("Оновити"), L.T("Скасувати")) != 0) return;
        Begin(op == "flushdns" ? L.T("ipconfig /flushdns - очищення DNS-кешу") : L.T("ipconfig /release і /renew - оновлення IP"));
        follow = false;
        outScroll = 0;
        int id = runId;
        Util.Bg(delegate
        {
            string text = null, err = "";
            if (Elevated.IsAdmin)
            {
                StringWriter sw = new StringWriter();
                NetTools.IpCfg(op, sw);
                text = sw.ToString();
            }
            else
            {
                Add(id, L.T("Очікую дозвіл адміністратора…"), "");
                string f = Elevated.Run("ipcfg", op, out err);
                if (f != null)
                {
                    DateTime until = DateTime.Now.AddMinutes(3);
                    while (DateTime.Now < until && id == runId)
                    {
                        Thread.Sleep(250);
                        bool done;
                        string t = Elevated.ReadOut(f, out done);
                        if (done) { text = t; break; }
                    }
                    try { File.Delete(f); } catch { }
                    if (text == null && id == runId) err = L.T("Помічник не відповів.");
                }
            }
            if (id != runId) return;
            if (text == null) { Add(id, err.Length > 0 ? err : L.T("Не виконано."), "bad"); Finish(id); return; }
            foreach (string l in text.Split('\n')) { string s = l.TrimEnd('\r'); if (s.Trim().Length > 0) Add(id, s, Kind(s)); }
            Add(id, L.T("Готово"), "ok");
            Finish(id);
        });
    }

    // ---------- порти цього ПК ----------
    void StartLocalPorts()
    {
        Begin(L.T("Порти, які слухає цей ПК (IPv4)"));
        follow = false;
        outScroll = 0;
        int id = runId;
        Util.Bg(delegate
        {
            List<NetTools.PortRow> l = NetTools.ListeningPorts();
            if (id != runId) return;
            if (l.Count == 0) { Add(id, L.T("Не вдалося прочитати таблицю портів."), "bad"); Finish(id); return; }
            int open = 0, loc = 0;
            foreach (NetTools.PortRow r in l)
            {
                if (r.Local) loc++; else open++;
                string nm = NetTools.PortName(r.Port);
                Add(id, (r.Proto + " " + r.Addr + ":" + r.Port).PadRight(26) + r.Proc + (nm.Length > 0 ? " · " + nm : ""), r.Local ? "ok" : "");
            }
            Add(id, L.T("Доступні з мережі: ") + open + L.T(" · лише на цьому ПК (зелені): ") + loc, "head");
            Add(id, L.T("Порт, відкритий для мережі, у Windows ще не означає доступ ззовні: його може закривати брандмауер (вкладка «Брандмауер»)."), "");
            Finish(id);
        });
    }

    // ---------- порти іншого пристрою ----------
    void StartHostPorts()
    {
        string host; int one;
        if (!NetTools.SplitHostPort(tbHost.Text, out host, out one) || host.Length == 0)
        {
            Begin(L.T("Порти хоста"));
            Add(runId, L.T("Введіть адресу або адресу:порт, наприклад 192.168.0.1:3389."), "bad");
            running = false;
            return;
        }
        int[] ports = one > 0 ? new int[] { one } : NetTools.CommonPorts;
        Begin(L.T("Порти на ") + host + (one > 0 ? ":" + one : " - " + ports.Length + L.T(" популярних")) + L.T(" · перевіряйте лише свої пристрої"));
        int id = runId;
        Util.Bg(delegate
        {
            IPAddress ip = null;
            try { ip = Resolve(host); } catch { }
            if (ip == null) { Add(id, L.T("Не вдалося знайти адресу «") + host + L.Q("»"), "bad"); Finish(id); return; }
            Add(id, L.T("Адреса: ") + ip, "");
            int n = ports.Length;
            int[] res = new int[n];
            double[] tm = new double[n];
            List<Thread> th = new List<Thread>();
            for (int i = 0; i < n; i++)
            {
                int k = i;
                Thread t = new Thread(delegate () { double ms; res[k] = NetTools.TcpProbe(ip, ports[k], 1200, out ms); tm[k] = ms; });
                t.IsBackground = true;
                t.Start();
                th.Add(t);
            }
            foreach (Thread t in th) t.Join();
            if (id != runId) return;
            int open = 0;
            for (int i = 0; i < n; i++)
            {
                string nm = NetTools.PortName(ports[i]);
                string label = ports[i] + (nm.Length > 0 ? " · " + nm : "");
                if (res[i] == 0) { open++; Add(id, label + L.T(": відкритий (") + Math.Max(1, (int)Math.Round(tm[i])) + L.T(" мс)"), "ok"); }
                else if (n == 1) Add(id, label + (res[i] == 1 ? L.T(": закритий (пристрій відмовив у з'єднанні)") : L.T(": не відповідає (порт закрито брандмауером або пристрій вимкнено)")), "bad");
            }
            if (n > 1) Add(id, L.T("Відкрито: ") + open + L.T(" з ") + n + L.T(". Решта закрита або не відповідає."), open > 0 ? "head" : "");
            Finish(id);
        });
    }

    // ---------- підмережа ----------
    void StartSweep()
    {
        Begin(L.T("Сканування підмережі"));
        int id = runId;
        Util.Bg(delegate
        {
            AdapterInfo me = null;
            foreach (AdapterInfo a in NetCfg.Load())
            {
                if (!a.Up || a.Ips.Count == 0 || a.Masks[0].Length == 0) continue;
                if (me == null || (me.Gateways.Count == 0 && a.Gateways.Count > 0) || (me.Virtual && !a.Virtual)) me = a;
            }
            uint first, last, self; bool trimmed;
            if (me == null || !NetTools.SubnetRange(me.Ips[0], me.Masks[0], out first, out last, out self, out trimmed))
            { Add(id, L.T("Немає активного підключення з IP-адресою, підмережу визначити не вдалося."), "bad"); Finish(id); return; }
            int total = (int)(last - first + 1);
            Add(id, L.T("Адаптер «") + me.Name + L.Q("», ") + NetCfg.V4Text(first) + " - " + NetCfg.V4Text(last) + " (" + total + L.T(" адрес)") + (trimmed ? L.T(" - мережа велика, скануємо лише ваш /24") : "") + "…", "");
            string gw = me.Gateways.Count > 0 ? me.Gateways[0] : "";
            Dictionary<uint, double> found = new Dictionary<uint, double>();
            Dictionary<uint, string> names = new Dictionary<uint, string>();
            object lk = new object();
            int next = -1, doneN = 0;
            List<Thread> th = new List<Thread>();
            for (int w = 0; w < 24; w++)
            {
                Thread t = new Thread(delegate ()
                {
                    byte[] buf = new byte[16];
                    using (System.Net.NetworkInformation.Ping p = new System.Net.NetworkInformation.Ping())
                        while (id == runId)
                        {
                            int k = Interlocked.Increment(ref next);
                            if (k >= total) break;
                            uint a = first + (uint)k;
                            IPAddress ip = IPAddress.Parse(NetCfg.V4Text(a));
                            try
                            {
                                System.Net.NetworkInformation.PingReply rp = p.Send(ip, 700, buf);
                                if (rp.Status == System.Net.NetworkInformation.IPStatus.Success)
                                {
                                    string nm = "";
                                    try
                                    {
                                        System.Threading.Tasks.Task<string> tk = System.Threading.Tasks.Task<string>.Factory.StartNew(delegate { try { return Dns.GetHostEntry(ip).HostName; } catch { return ""; } });
                                        if (tk.Wait(1200)) nm = tk.Result;
                                    }
                                    catch { }
                                    lock (lk) { found[a] = rp.RoundtripTime; names[a] = nm; }
                                }
                            }
                            catch { }
                            Interlocked.Increment(ref doneN);
                        }
                });
                t.IsBackground = true;
                t.Start();
                th.Add(t);
            }
            foreach (Thread t in th) t.Join();
            if (id != runId) return;
            // MAC і пристрої, які не відповіли на пінг, але відповіли на ARP
            Dictionary<string, string> arp = NetTools.ArpTable();
            List<uint> ips = new List<uint>(found.Keys);
            foreach (KeyValuePair<string, string> kv in arp)
            {
                uint u;
                if (NetCfg.ParseV4(kv.Key, out u) && u >= first && u <= last && !found.ContainsKey(u)) { found[u] = -1; names[u] = ""; ips.Add(u); }
            }
            ips.Sort();
            int pinged = 0;
            foreach (uint u in ips)
            {
                string s = NetCfg.V4Text(u);
                string mac; arp.TryGetValue(s, out mac);
                string line = s.PadRight(16) + (found[u] >= 0 ? Math.Max(1, (int)found[u]) + L.T(" мс") : L.T("без пінга"));
                if (found[u] >= 0) pinged++;
                if (names[u].Length > 0) line += " · " + names[u];
                if (mac != null) line += " · " + mac;
                if (u == self) line += L.T(" · цей ПК");
                else if (s == gw) line += L.T(" · шлюз (роутер)");
                Add(id, line, u == self || s == gw ? "ok" : "");
            }
            Add(id, L.T("Знайдено пристроїв: ") + ips.Count + L.T(" (з відповіддю на пінг: ") + pinged + ")", "head");
            Add(id, L.T("Пристрої, які не відповідають на пінг і ARP, тут не видно; «без пінга» - пристрій є в ARP, але пінг блокує."), "");
            Finish(id);
        });
    }

    // ---------- перевірка DNS ----------
    void StartDns()
    {
        string h = tbHost.Text.Trim();
        IPAddress tmp;
        bool def = h.Length == 0 || IPAddress.TryParse(h, out tmp);
        if (def) h = "google.com";
        Begin(L.T("Перевірка DNS: ") + h + (def ? L.T(" (у полі IP чи порожньо - беру типовий домен)") : ""));
        int id = runId;
        Util.Bg(delegate
        {
            // 1) як бачить система (разом із кешем Windows)
            try
            {
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                IPAddress[] all = Dns.GetHostAddresses(h);
                sw.Stop();
                List<string> v4 = new List<string>();
                foreach (IPAddress a in all) if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) v4.Add(a.ToString());
                Add(id, L.T("Система (з кешем Windows): ") + (v4.Count > 0 ? string.Join(", ", v4.ToArray()) : L.T("лише IPv6")) + " · " + Math.Max(1, (int)sw.ElapsedMilliseconds) + L.T(" мс"), "ok");
            }
            catch (Exception) { Add(id, L.T("Система (з кешем Windows): адресу не знайдено"), "bad"); }

            // 2) сервери: DNS активних адаптерів, потім публічні
            List<string[]> srv = new List<string[]>();      // {адреса, підпис, "cfg"/"pub"}
            HashSet<string> seen = new HashSet<string>();
            foreach (AdapterInfo a in NetCfg.Load())
            {
                if (!a.Up) continue;
                foreach (string d in a.Dns) if (seen.Add(d) && srv.Count < 6) srv.Add(new[] { d, L.T("DNS адаптера «") + a.Name + L.Q("»"), "cfg" });
            }
            string[][] pub = { new[] { "8.8.8.8", "Google" }, new[] { "1.1.1.1", "Cloudflare" }, new[] { "9.9.9.9", "Quad9" } };
            foreach (string[] p in pub) if (seen.Add(p[0])) srv.Add(new[] { p[0], p[1], "pub" });
            int cfgOk = 0, cfgAll = 0, pubOk = 0;
            string best = ""; double bestMs = double.MaxValue;
            foreach (string[] s in srv)
            {
                if (id != runId) return;
                IPAddress ip;
                if (!IPAddress.TryParse(s[0], out ip)) continue;
                List<string> addrs = new List<string>();
                double ms; string txt;
                bool ok = NetTools.Query(ip, h, 2000, addrs, out ms, out txt);
                bool cfg = s[2] == "cfg";
                if (cfg) cfgAll++;
                if (ok) { if (cfg) cfgOk++; else pubOk++; if (ms < bestMs) { bestMs = ms; best = s[0] + " (" + s[1] + ")"; } }
                string head = s[0] + "  " + s[1] + ": ";
                if (ok)
                {
                    if (addrs.Count > 3) addrs.RemoveRange(3, addrs.Count - 3);
                    Add(id, head + Math.Max(1, (int)Math.Round(ms)) + L.T(" мс → ") + string.Join(", ", addrs.ToArray()), ms < 80 ? "ok" : "");
                }
                else Add(id, head + txt, "bad");
            }
            if (id != runId) return;
            string sum; string kind;
            if (cfgAll == 0) { sum = L.T("У активних адаптерів немає DNS-серверів (або вони не ввімкнені)."); kind = "bad"; }
            else if (cfgOk == cfgAll) { sum = L.T("DNS працює: усі сервери ваших адаптерів відповідають."); kind = "ok"; }
            else if (cfgOk == 0 && pubOk > 0) { sum = L.T("Ваші DNS-сервери не відповідають, а публічні працюють. Спробуйте інший DNS на вкладці «Адаптери»."); kind = "bad"; }
            else if (cfgOk == 0) { sum = L.T("Жоден DNS не відповідає: немає інтернету або порт 53 заблоковано."); kind = "bad"; }
            else { sum = L.T("Частина DNS-серверів ваших адаптерів не відповідає."); kind = "bad"; }
            Add(id, sum, kind);
            if (best.Length > 0) Add(id, L.T("Найшвидший: ") + best + ", " + Math.Max(1, (int)Math.Round(bestMs)) + L.T(" мс"), "");
            Finish(id);
        });
    }
}
