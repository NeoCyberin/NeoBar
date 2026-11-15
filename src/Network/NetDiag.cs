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
    // дії згруповано за змістом: що робимо з адресою з поля, DNS, сам ПК і локальна мережа
    int DiagTools(Graphics g, Rectangle r, int y)
    {
        int ch = Px(32), gap = Px(6);
        bool ok = !running;
        y = Group(g, r, y, L.T("Адреса з поля"));
        Rectangle[] a = Row(r, y, 3, ch, gap);
        Btn(g, a[0], running ? L.T("Зупинити") : L.T("Пінг (10 пакетів)"), delegate { if (running) Stop(); else StartPing(); }, 1, true);
        Btn(g, a[1], L.T("Трасування"), delegate { StartTrace(); }, 0, ok);
        Btn(g, a[2], L.T("Порти хоста"), StartHostPorts, 0, ok);
        Tip(a[1], L.T("Через які вузли йде трафік до адреси і де з'являється затримка"));
        Tip(a[2], L.T("Перевіряє популярні порти на адресі з поля (або один порт, якщо дописати :порт, наприклад 192.168.0.1:3389). Перевіряйте лише свої пристрої"));
        y += ch + Px(10);

        y = Group(g, r, y, L.T("DNS та IP"));
        a = Row(r, y, 3, ch, gap);
        Btn(g, a[0], L.T("Перевірка DNS"), StartDns, 0, ok);
        Btn(g, a[1], L.T("Очистити DNS-кеш"), delegate { StartIp("flushdns"); }, 0, ok);
        Btn(g, a[2], L.T("Оновити IP…"), delegate { StartIp("renew"); }, 0, ok);
        Tip(a[0], L.T("У полі IP DNS-сервера (наприклад, 1.1.1.1) - перевіряє, чи він працює і чи швидший за ваш поточний.\nУ полі ім'я сайту - як його бачать ваші DNS і публічні сервери.\nПоле порожнє - чи працюють ваші DNS-сервери."));
        Tip(a[1], L.T("ipconfig /flushdns: забуває збережені відповіді DNS (потрібен дозвіл адміністратора)"));
        Tip(a[2], L.T("ipconfig /release і /renew: нова IP-адреса від роутера; зв'язок на кілька секунд обірветься (потрібен дозвіл адміністратора)"));
        y += ch + Px(10);

        y = Group(g, r, y, L.T("Цей ПК і локальна мережа"));
        a = Row(r, y, 4, ch, gap);
        Btn(g, a[0], L.T("Скан підмережі"), StartSweep, 0, ok);
        Btn(g, a[1], L.T("Порти ПК"), StartLocalPorts, 0, ok);
        Btn(g, a[2], L.T("Маршрути"), delegate { StartTool(L.T("route print - таблиця маршрутів"), "route.exe", "print"); }, 0, ok);
        Btn(g, a[3], L.T("ARP-таблиця"), delegate { StartTool(L.T("arp -a - пристрої, з якими щойно спілкувався ПК"), "arp.exe", "-a"); }, 0, ok);
        Tip(a[0], L.T("Пінгує всі адреси вашої локальної мережі й показує, які пристрої живі: ім'я, MAC, шлюз"));
        Tip(a[1], L.T("Які порти слухає цей ПК і яка програма: що доступне з мережі, а що лише локально"));
        Tip(a[2], L.T("Показує, куди система надсилає трафік"));
        Tip(a[3], L.T("Показує IP і MAC-адреси сусідніх пристроїв у локальній мережі"));
        return y + ch + Px(12);
    }

    int Group(Graphics g, Rectangle r, int y, string title)
    {
        TxtL(g, title, FS, new Rectangle(r.X, y, r.Width, Px(18)), P.Sub);
        return y + Px(20);
    }

    static Rectangle[] Row(Rectangle r, int y, int n, int h, int gap)
    {
        Rectangle[] a = new Rectangle[n];
        int w = (r.Width - gap * (n - 1)) / n;
        for (int i = 0; i < n; i++) a[i] = new Rectangle(r.X + i * (w + gap), y, i == n - 1 ? r.Right - (r.X + i * (w + gap)) : w, h);
        return a;
    }

    // «пігулка»: лише контур, вибрана - контур і текст кольору акценту
    void Pill(Graphics g, Rectangle r, string text, bool sel, Action click)
    {
        bool h = Over(r);
        System.Drawing.Drawing2D.SmoothingMode sm = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if (h) using (SolidBrush b = new SolidBrush(Util.Blend(P.Bg, P.Fg, 0.06f))) Util.FillRound(g, b, r, r.Height / 2);
        using (Pen p = new Pen(sel ? P.Acc : Util.Blend(P.Bg, P.Fg, 0.22f), sel ? 1.6f : 1f))
        using (System.Drawing.Drawing2D.GraphicsPath gp = Util.Round(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), r.Height / 2))
            g.DrawPath(p, gp);
        g.SmoothingMode = sm;
        TextRenderer.DrawText(g, text, FS, Rectangle.Inflate(r, -Px(4), 0), sel ? P.Acc : P.Sub,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        Hot(r, click);
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
                    try { File.Delete(f); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
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
            try { ip = Resolve(host); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            if (ip == null) { Add(id, L.T("Не вдалося знайти адресу «") + host + L.Q("»"), "bad"); Finish(id); return; }
            Add(id, L.T("Адреса: ") + ip, "");
            int n = ports.Length;
            int[] res = new int[n];
            double[] tm = new double[n];
            List<Thread> th = new List<Thread>();
            for (int i = 0; i < n; i++)
            {
                int k = i;
                Thread t = new Thread(delegate () { try { double ms; res[k] = NetTools.TcpProbe(ip, ports[k], 1200, out ms); tm[k] = ms; } catch { res[k] = 2; } });
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
                                    catch (Exception skipEx) { SbLog.Skip(skipEx); }
                                    lock (lk) { found[a] = rp.RoundtripTime; names[a] = nm; }
                                }
                            }
                            catch (Exception skipEx) { SbLog.Skip(skipEx); }
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
    // що в полі, те й перевіряємо: IP - це DNS-сервер (чи працює й чи швидший за ваш), ім'я - як його бачать різні DNS,
    // порожньо - чи працюють DNS-сервери ваших адаптерів
    static readonly string[] DnsProbe = { "google.com", "wikipedia.org", "microsoft.com" };

    static string PresetName(string ip)
    {
        foreach (string[] p in PingMon.Presets) if (p[0] == ip) return p[1];
        return "";
    }

    // DNS-сервери активних адаптерів: {адреса, підпис}
    static List<string[]> MyDns()
    {
        List<string[]> l = new List<string[]>();
        HashSet<string> seen = new HashSet<string>();
        foreach (AdapterInfo a in NetCfg.Load())
        {
            if (!a.Up) continue;
            foreach (string d in a.Dns) if (seen.Add(d) && l.Count < 4) l.Add(new[] { d, L.T("адаптер «") + a.Name + L.Q("»") });
        }
        return l;
    }

    // середній час відповіді сервера на кілька імен; NaN - не відповів жодного разу
    static double DnsAvg(IPAddress ip, int id, Func<bool> stop, Action<string, double, string> each)
    {
        double sum = 0; int n = 0;
        foreach (string d in DnsProbe)
        {
            if (stop()) break;
            List<string> addrs = new List<string>();
            double ms; string txt;
            bool ok = NetTools.Query(ip, d, 2000, addrs, out ms, out txt);
            if (ok) { sum += ms; n++; }
            if (each != null) each(d, ok ? ms : double.NaN, txt);
        }
        return n > 0 ? sum / n : double.NaN;
    }

    static string Ms(double ms) { return Math.Max(1, (int)Math.Round(ms)) + L.T(" мс"); }

    void StartDns()
    {
        string h = tbHost.Text.Trim();
        IPAddress ip;
        if (h.Length > 0 && IPAddress.TryParse(h, out ip)) DnsServer(ip);
        else DnsName(h);
    }

    void DnsServer(IPAddress ip)
    {
        string nm = PresetName(ip.ToString());
        string who = ip + (nm.Length > 0 ? " (" + nm + ")" : "");
        Begin(L.T("Перевірка DNS-сервера ") + who);
        int id = runId;
        Func<bool> stop = delegate { return id != runId; };
        Util.Bg(delegate
        {
            Add(id, L.T("Чи відповідає сервер (запити трьох відомих сайтів):"), "head");
            double avg = DnsAvg(ip, id, stop, delegate (string d, double ms, string txt)
            {
                if (double.IsNaN(ms)) Add(id, "  " + d + ": " + txt, "bad");
                else Add(id, "  " + d + ": " + Ms(ms), ms < 80 ? "ok" : "");
            });
            if (stop()) return;
            if (double.IsNaN(avg))
            {
                Add(id, L.T("Сервер не відповідає: за цією адресою немає DNS-сервера, або порт 53 заблоковано."), "bad");
                Finish(id);
                return;
            }
            // ваші поточні DNS - для порівняння тими самими запитами
            List<string[]> mine = MyDns();
            double myBest = double.NaN; string myName = "";
            bool same = false;
            if (mine.Count > 0) Add(id, L.T("Ваші поточні DNS-сервери - для порівняння:"), "head");
            foreach (string[] s in mine)
            {
                if (stop()) return;
                IPAddress mip;
                if (!IPAddress.TryParse(s[0], out mip)) continue;
                if (mip.Equals(ip)) { same = true; Add(id, "  " + s[0] + " · " + s[1] + L.T(": це він і є"), ""); continue; }
                double a2 = DnsAvg(mip, id, stop, null);
                if (double.IsNaN(a2)) Add(id, "  " + s[0] + " · " + s[1] + L.T(": не відповідає"), "bad");
                else
                {
                    Add(id, "  " + s[0] + " · " + s[1] + ": " + Ms(a2), a2 < 80 ? "ok" : "");
                    if (double.IsNaN(myBest) || a2 < myBest) { myBest = a2; myName = s[0]; }
                }
            }
            if (stop()) return;
            string res = L.T("Сервер працює, у середньому ") + Ms(avg) + ". ";
            if (same) Add(id, res + L.T("Ви вже ним користуєтесь."), "ok");
            else if (mine.Count == 0 || double.IsNaN(myBest))
                Add(id, res + L.T("Ваші поточні DNS не відповідають - можна перейти на цей сервер на вкладці «Адаптери»."), "ok");
            else if (avg + 10 < myBest * 0.8)
                Add(id, res + string.Format(L.T("Він швидший за ваш поточний ({0}, {1}) - перейти можна на вкладці «Адаптери»."), myName, Ms(myBest)), "ok");
            else
                Add(id, res + string.Format(L.T("Ваш поточний DNS ({0}, {1}) не повільніший - змінювати не обов'язково."), myName, Ms(myBest)), "ok");
            Finish(id);
        });
    }

    void DnsName(string h)
    {
        bool mineOnly = h.Length == 0;
        if (mineOnly) h = DnsProbe[0];
        Begin(mineOnly ? L.T("Перевірка DNS: чи працюють ваші DNS-сервери (на прикладі ") + h + ")"
                       : L.T("Перевірка DNS для імені ") + h);
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
                if (v4.Count > 4) v4.RemoveRange(4, v4.Count - 4);
                Add(id, L.T("Windows знаходить адресу: ") + (v4.Count > 0 ? string.Join(", ", v4.ToArray()) : L.T("лише IPv6")) + " · " + Ms(sw.ElapsedMilliseconds), "ok");
            }
            catch (Exception) { Add(id, L.T("Windows не знаходить цю адресу"), "bad"); }

            // 2) кожен сервер окремо: ваші, потім публічні
            List<string[]> srv = new List<string[]>();      // {адреса, підпис, "cfg"/"pub"}
            HashSet<string> seen = new HashSet<string>();
            foreach (string[] s in MyDns()) { seen.Add(s[0]); srv.Add(new[] { s[0], s[1], "cfg" }); }
            string[][] pub = { new[] { "8.8.8.8", "Google" }, new[] { "1.1.1.1", "Cloudflare" }, new[] { "9.9.9.9", "Quad9" } };
            foreach (string[] p in pub) if (seen.Add(p[0])) srv.Add(new[] { p[0], p[1], "pub" });
            int cfgOk = 0, cfgAll = 0, pubOk = 0;
            string best = ""; double bestMs = double.MaxValue;
            bool headPub = false;
            if (srv.Count > 0 && srv[0][2] == "cfg") Add(id, L.T("Ваші DNS-сервери:"), "head");
            foreach (string[] s in srv)
            {
                if (id != runId) return;
                IPAddress ip;
                if (!IPAddress.TryParse(s[0], out ip)) continue;
                bool cfg = s[2] == "cfg";
                if (!cfg && !headPub) { headPub = true; Add(id, L.T("Публічні DNS-сервери - для порівняння:"), "head"); }
                List<string> addrs = new List<string>();
                double ms; string txt;
                bool ok = NetTools.Query(ip, h, 2000, addrs, out ms, out txt);
                if (cfg) cfgAll++;
                if (ok) { if (cfg) cfgOk++; else pubOk++; if (ms < bestMs) { bestMs = ms; best = s[0] + " · " + s[1]; } }
                string head = "  " + s[0] + " · " + s[1] + ": ";
                if (ok) Add(id, head + Ms(ms), ms < 80 ? "ok" : "");
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
            if (best.Length > 0) Add(id, L.T("Найшвидший: ") + best + ", " + Ms(bestMs), "");
            Finish(id);
        });
    }
}
