using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// ======================================================================
//  Вікно «Мережа», вкладка «Адаптери»: список адаптерів, їхні налаштування (IPv4 автоматично чи вручну, DNS,
//  увімкнути/вимкнути) і, якщо на ПК є Wi-Fi, збережені мережі: пароль, QR-код, нова мережа, зміна пароля,
//  «забути». Вкладка нічого не робить сама: читання без прав, будь-яка зміна - після кнопки й (де треба)
//  запиту адміністратора від Windows. Вигляд вкладки - сторінки: список, адаптер, зміна IP/DNS, Wi-Fi,
//  мережа Wi-Fi, нова мережа.
// ======================================================================
sealed partial class NetWindow
{
    const int PgList = 0, PgAd = 1, PgEdit = 2, PgWifi = 3, PgNet = 4, PgAdd = 5;
    int page;

    // адаптери
    List<AdapterInfo> ads = new List<AdapterInfo>();
    bool adLoaded, adLoading, hasWifi;
    AdapterInfo cur;
    int scrollA;
    bool busy;
    string busyText = "";

    // зміна IP/DNS
    TextBox tbIp, tbMask, tbGw, tbD1, tbD2;
    bool manIp, manDns;
    string editErr = "";
    List<string> undo;
    string undoId = "";

    // Wi-Fi
    List<WifiNet> nets = new List<WifiNet>();
    readonly HashSet<string> shown = new HashSet<string>(StringComparer.Ordinal);
    bool wLoading, wAsk, wLoaded;
    string wError = "", wErr = "";
    WifiNet wsel;
    QrCode.Result qr;
    Bitmap qrBmp;
    int scrollW;
    TextBox tbSsid, tbPass;
    bool changing, showPass, addHidden;
    string addAuth = "WPA";
    System.Windows.Forms.Timer clipTimer;
    string clipText = "";
    TextBox[] adBoxes;

    static readonly string[][] DnsPairs =
    {
        new[] { "Google", "8.8.8.8", "8.8.4.4" }, new[] { "Cloudflare", "1.1.1.1", "1.0.0.1" }, new[] { "Quad9", "9.9.9.9", "149.112.112.112" },
        new[] { "OpenDNS", "208.67.222.222", "208.67.220.220" }, new[] { "AdGuard", "94.140.14.14", "94.140.15.15" }
    };

    void AdInit()
    {
        tbIp = MakeInput(); tbMask = MakeInput(); tbGw = MakeInput(); tbD1 = MakeInput(); tbD2 = MakeInput();
        tbSsid = MakeInput(); tbPass = MakeInput();
        FwInit();
        adBoxes = new TextBox[] { tbIp, tbMask, tbGw, tbD1, tbD2, tbSsid, tbPass, tbSearch, tbFwName, tbFwPort, tbFwApp };
        tbPass.KeyDown += delegate (object s, KeyEventArgs ke)
        {
            if (ke.KeyCode != Keys.Enter) return;
            ke.SuppressKeyPress = true;
            if (page == PgAdd) DoAdd(); else if (page == PgNet && changing) DoSetKey();
        };
        tbSsid.KeyDown += delegate (object s, KeyEventArgs ke)
        {
            if (ke.KeyCode == Keys.Enter) { ke.SuppressKeyPress = true; tbPass.Focus(); }
        };
    }

    readonly HashSet<TextBox> placed = new HashSet<TextBox>();

    // поле вводу цієї сторінки: показується там, де намальоване; не намальовані цього разу ховаються наприкінці малювання
    void Fld(Graphics g, TextBox tb, Rectangle r)
    {
        PlaceInput(g, tb, r);
        placed.Add(tb);
    }

    void HideUnplaced()
    {
        if (adBoxes != null) foreach (TextBox t in adBoxes) if (!placed.Contains(t) && t.Visible) t.Visible = false;
    }

    void AdHide()
    {
        if (adBoxes != null) foreach (TextBox t in adBoxes) if (t.Visible) t.Visible = false;
    }

    // вихід із вкладки або закриття вікна: паролі не лишаються в пам'яті
    void AdLeave()
    {
        AdHide();
        foreach (WifiNet n in nets) n.Pass = "";
        nets = new List<WifiNet>();
        shown.Clear();
        wsel = null; qr = null; wLoaded = false;
        if (qrBmp != null) { qrBmp.Dispose(); qrBmp = null; }
        if (tbPass != null) tbPass.Text = "";
        changing = false; showPass = false;
        if (page >= PgWifi) page = PgList;
        editErr = ""; wErr = "";
    }

    void AdClosed()
    {
        AdLeave();
        if (clipTimer != null) { clipTimer.Stop(); clipTimer.Dispose(); clipTimer = null; }
    }

    void UiPost(Action a)
    {
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { }
    }

    // ---------- завантаження ----------
    void ReloadAds()
    {
        if (adLoading) return;
        adLoading = true;
        Invalidate();
        Util.Bg(delegate
        {
            List<AdapterInfo> l = NetCfg.Load();
            UiPost(delegate
            {
                adLoading = false; adLoaded = true;
                ads = l;
                hasWifi = false;
                foreach (AdapterInfo a in ads) if (a.Wireless) hasWifi = true;
                if (cur != null)
                {
                    AdapterInfo found = null;
                    foreach (AdapterInfo a in ads) if (a.Id == cur.Id) found = a;
                    cur = found;
                    if (found == null && (page == PgAd || page == PgEdit)) page = PgList;
                }
                Invalidate();
            });
        });
    }

    void ReloadWifi()
    {
        if (wLoading) return;
        wLoading = true;
        Invalidate();
        Util.Bg(delegate
        {
            string err;
            List<WifiNet> l = WifiList.Load(out err);
            UiPost(delegate { SetNets(l, err); });
        });
    }

    void SetNets(List<WifiNet> l, string err)
    {
        string keep = wsel != null ? wsel.Name : null;
        foreach (WifiNet n in nets) n.Pass = "";
        nets = l;
        wError = err;
        wLoading = false; wLoaded = true;
        wsel = null; qr = null;
        if (qrBmp != null) { qrBmp.Dispose(); qrBmp = null; }
        if (keep != null) foreach (WifiNet n in nets) if (n.Name == keep) Pick(n, false);
        if (wsel == null && page == PgNet) { page = PgWifi; changing = false; }
        Invalidate();
    }

    // усі Wi-Fi-адаптери вимкнено: Windows тоді не віддає ні мереж, ні паролів
    bool WifiOff()
    {
        bool any = false;
        foreach (AdapterInfo a in ads) if (a.Wireless) { if (!a.Disabled) return false; any = true; }
        return any;
    }

    int LockedCount()
    {
        int c = 0;
        foreach (WifiNet n in nets) if (n.Locked) c++;
        return c;
    }

    // один запит UAC: помічник читає ті самі мережі вже з правами адміністратора
    void Unlock()
    {
        if (wAsk || wLoading || busy) return;
        wAsk = true;
        Say(L.T("Очікую дозвіл адміністратора…"), false);
        Util.Bg(delegate
        {
            string err = "", text = null;
            string f = Elevated.Run("wifi", "", out err);
            if (f != null)
            {
                DateTime until = DateTime.Now.AddMinutes(2);
                while (DateTime.Now < until)
                {
                    Thread.Sleep(250);
                    bool done;
                    string t = Elevated.ReadOut(f, out done);
                    if (done) { text = t; break; }
                }
                try { File.Delete(f); } catch { }
                if (text == null) err = L.T("Помічник не відповів.");
            }
            string perr = "";
            List<WifiNet> l = text != null ? WifiList.ParseOut(text, out perr) : null;
            UiPost(delegate
            {
                wAsk = false;
                if (l == null) { Say(err, true); return; }
                SetNets(l, perr);
                Say(LockedCount() > 0 ? L.T("Частину паролів Windows так і не віддала.") : L.T("Паролі прочитано."), LockedCount() > 0);
            });
        });
    }

    // ---------- навігація ----------
    void Go(int p)
    {
        page = p;
        editErr = ""; wErr = "";
        AdHide();
        Invalidate();
    }

    void OpenAd(AdapterInfo a) { cur = a; Go(PgAd); }

    void OpenEdit()
    {
        AdapterInfo a = cur;
        if (a == null) return;
        manIp = !a.Dhcp;
        manDns = !a.DnsAuto;
        tbIp.Text = a.Ips.Count > 0 ? a.Ips[0] : "";
        tbMask.Text = a.Masks.Count > 0 ? a.Masks[0] : "255.255.255.0";
        tbGw.Text = a.Gateways.Count > 0 ? a.Gateways[0] : "";
        tbD1.Text = a.Dns.Count > 0 ? a.Dns[0] : "";
        tbD2.Text = a.Dns.Count > 1 ? a.Dns[1] : "";
        Go(PgEdit);
    }

    void OpenWifi()
    {
        Go(PgWifi);
        if (!wLoaded) ReloadWifi();
    }

    void OpenAdd()
    {
        tbSsid.Text = ""; tbPass.Text = "";
        showPass = false; addHidden = false; addAuth = "WPA";
        Go(PgAdd);
    }

    void Pick(WifiNet n, bool open)
    {
        wsel = n;
        qr = null;
        if (qrBmp != null) { qrBmp.Dispose(); qrBmp = null; }
        changing = false; showPass = false;
        tbPass.Text = "";
        if (n != null && n.CanShare)
        {
            qr = QrCode.EncodeText(QrState.WifiPayload(n.Ssid, n.Auth, n.Pass, n.Hidden), 1, true);
            if (qr != null) qrBmp = RenderQr(qr, 6);
        }
        if (open) Go(PgNet);
    }

    static Bitmap RenderQr(QrCode.Result q, int module)
    {
        int border = 4, size = (q.Size + border * 2) * module;
        Bitmap bm = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bm))
        {
            g.Clear(Color.White);
            using (SolidBrush b = new SolidBrush(Color.Black))
                for (int y = 0; y < q.Size; y++)
                    for (int x = 0; x < q.Size; x++)
                        if (q.Dark[y, x]) g.FillRectangle(b, (x + border) * module, (y + border) * module, module, module);
        }
        return bm;
    }

    // ---------- дії над адаптером ----------
    List<string> BuildUndo(AdapterInfo a)
    {
        List<string> u = new List<string>();
        if (a.Dhcp) u.Add(NetCfg.PlAddrDhcp(a.Id));
        else
        {
            if (a.Ips.Count != 1 || a.Masks[0].Length == 0) return null;       // кілька адрес - повернути точно не можемо
            u.Add(NetCfg.PlAddrStatic(a.Id, a.Ips[0], a.Masks[0], a.Gateways.Count > 0 ? a.Gateways[0] : ""));
        }
        if (a.DnsAuto) u.Add(NetCfg.PlDnsDhcp(a.Id));
        else if (a.Dns.Count > 0) u.Add(NetCfg.PlDnsStatic(a.Id, a.Dns[0], a.Dns.Count > 1 ? a.Dns[1] : ""));
        return u;
    }

    void ApplyEdit()
    {
        AdapterInfo a = cur;
        if (a == null || busy) return;
        string err = "";
        List<string> lines = new List<string>();
        string ipText, dnsText;
        if (manIp)
        {
            err = NetCfg.CheckStatic(tbIp.Text, tbMask.Text, tbGw.Text);
            if (err.Length == 0) lines.Add(NetCfg.PlAddrStatic(a.Id, tbIp.Text, tbMask.Text, tbGw.Text));
            uint m;
            NetCfg.ParseMask(tbMask.Text, out m);
            ipText = tbIp.Text.Trim() + " / " + NetCfg.V4Text(m) + (tbGw.Text.Trim().Length > 0 ? L.T(", шлюз ") + tbGw.Text.Trim() : L.T(", без шлюзу"));
        }
        else
        {
            if (!a.Dhcp) lines.Add(NetCfg.PlAddrDhcp(a.Id));
            ipText = L.T("автоматично (DHCP)");
        }
        if (err.Length == 0)
        {
            if (manDns)
            {
                err = NetCfg.CheckDns(tbD1.Text, tbD2.Text);
                if (err.Length == 0) lines.Add(NetCfg.PlDnsStatic(a.Id, tbD1.Text, tbD2.Text));
                dnsText = tbD1.Text.Trim() + (tbD2.Text.Trim().Length > 0 ? ", " + tbD2.Text.Trim() : "");
            }
            else
            {
                if (!a.DnsAuto) lines.Add(NetCfg.PlDnsDhcp(a.Id));
                dnsText = L.T("автоматично");
            }
        }
        else dnsText = "";
        if (err.Length > 0) { editErr = err; Invalidate(); return; }
        editErr = "";
        if (lines.Count == 0) { Say(L.T("Нічого міняти: налаштування вже такі."), false); return; }
        List<string> back = BuildUndo(a);
        if (Ask(L.T("Мережа"), L.T("Змінити налаштування адаптера «") + a.Name + L.Q("»?\n\nIP: ") + ipText + "\nDNS: " + dnsText +
                L.T("\n\nЗ'єднання може ненадовго обірватися.") + (back != null ? L.T(" Попередні налаштування можна повернути кнопкою «Повернути попередні».") : ""),
                L.T("Застосувати"), L.T("Скасувати")) != 0) return;
        RunNet(lines, L.T("Налаштування застосовано."), back, a.Id);
    }

    void DoState(bool on)
    {
        AdapterInfo a = cur;
        if (a == null || busy) return;
        if (!on)
        {
            int others = 0;
            foreach (AdapterInfo x in ads) if (x.Up && x.Id != a.Id && !x.Virtual) others++;
            string t = L.T("Вимкнути адаптер «") + a.Name + L.Q("»?\n\n") + (a.Up && others == 0 ? L.T("Це єдине активне підключення: мережа й інтернет зникнуть, доки адаптер не увімкнути знову. ") : "") +
                       L.T("Увімкнути його можна тут само.");
            if (Ask(L.T("Мережа"), t, L.T("Вимкнути"), L.T("Скасувати")) != 0) return;
        }
        List<string> l = new List<string>();
        l.Add(NetCfg.PlState(a.Id, on));
        RunNet(l, on ? L.T("Адаптер увімкнено.") : L.T("Адаптер вимкнено."), null, a.Id);
    }

    void DoUndo()
    {
        if (undo == null || busy) return;
        List<string> l = undo;
        undo = null;
        RunNet(l, L.T("Попередні налаштування повернуто."), null, "");
    }

    void RunNet(List<string> lines, string okMsg, List<string> back, string forId)
    {
        busy = true;
        busyText = Elevated.IsAdmin ? L.T("Застосовую…") : L.T("Очікую дозвіл адміністратора…");
        Invalidate();
        Util.Bg(delegate
        {
            string err;
            string t = NetCfg.Apply(lines, out err);
            string fe;
            int bad;
            int ok = NetCfg.Summary(t, out fe, out bad);
            Thread.Sleep(lines.Count > 0 && lines[0].StartsWith("ST|", StringComparison.Ordinal) ? 3500 : 1500);     // Windows оновлює стан адаптера не миттєво
            UiPost(delegate
            {
                busy = false;
                if (t == null) Say(err, true);
                else if (bad > 0) Say((ok > 0 ? L.T("Застосовано частково: ") : L.T("Не застосовано: ")) + fe, true);
                else Say(okMsg, false);
                if (ok > 0 && back != null) { undo = back; undoId = forId; }
                ReloadAds();
            });
        });
    }

    // ---------- дії над Wi-Fi ----------
    void CopySecret(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        Copy(s);
        clipText = s;
        if (clipTimer == null)
        {
            clipTimer = new System.Windows.Forms.Timer();
            clipTimer.Interval = 60000;
            clipTimer.Tick += delegate
            {
                clipTimer.Stop();
                try { if (clipText.Length > 0 && Clipboard.ContainsText() && Clipboard.GetText() == clipText) Clipboard.Clear(); } catch { }
                clipText = "";
            };
        }
        clipTimer.Stop();
        clipTimer.Start();
    }

    void CopyList()
    {
        CopySecret(WifiList.Report(nets));
        Say(L.T("Список скопійовано (з паролями). Буфер обміну очиститься за хвилину."), false);
    }

    void SaveList()
    {
        using (SaveFileDialog d = new SaveFileDialog())
        {
            d.Title = L.T("Зберегти список Wi-Fi");
            d.Filter = L.T("Текстовий файл|*.txt");
            d.FileName = "wifi-" + DateTime.Now.ToString("yyyyMMdd");
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                File.WriteAllText(d.FileName, WifiList.Report(nets), new UTF8Encoding(true));
                Say(L.T("Збережено: ") + Path.GetFileName(d.FileName) + L.T(". У файлі паролі відкритим текстом."), false);
            }
            catch (Exception ex) { Say(L.T("Не вдалося зберегти: ") + ex.Message, true); }
        }
    }

    void CopyQr()
    {
        if (qr == null) return;
        try
        {
            using (Bitmap b = RenderQr(qr, Math.Max(8, 600 / (qr.Size + 8)))) Clipboard.SetImage(b);
            Say(L.T("QR-код скопійовано."), false);
        }
        catch (Exception ex) { Say(ex.Message, true); }
    }

    void SaveQr()
    {
        if (qr == null || wsel == null) return;
        using (SaveFileDialog d = new SaveFileDialog())
        {
            d.Title = L.T("Зберегти QR-код мережі");
            d.Filter = L.T("Зображення PNG|*.png");
            string nm = wsel.Ssid;
            foreach (char c in Path.GetInvalidFileNameChars()) nm = nm.Replace(c, '_');
            d.FileName = "wifi-" + nm;
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                using (Bitmap b = RenderQr(qr, Math.Max(10, 1000 / (qr.Size + 8)))) b.Save(d.FileName, ImageFormat.Png);
                Say(L.T("Збережено: ") + Path.GetFileName(d.FileName) + L.T(". Код містить пароль мережі."), false);
            }
            catch (Exception ex) { Say(L.T("Не вдалося зберегти: ") + ex.Message, true); }
        }
    }

    string PassText(WifiNet n)
    {
        if (n.Open) return L.T("без пароля");
        if (n.Enterprise) return L.T("вхід за обліковим записом");
        if (n.Locked) return L.T("потрібні права адміністратора");
        return shown.Contains(n.Name) ? n.Pass : "••••••••";
    }

    static bool SafeName(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (char c in s) if (c == '"' || c == '\\' || c < 32) return false;
        return true;
    }

    void DoConnect()
    {
        WifiNet n = wsel;
        if (n == null || busy) return;
        if (!SafeName(n.Name)) { Say(L.T("Назва містить символи, з якими підключитися звідси не вийде - зробіть це через Windows."), true); return; }
        string name = n.Name;
        Util.Bg(delegate
        {
            int code = 1;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), "wlan connect name=\"" + name + "\"");
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                using (Process pr = Process.Start(psi))
                    if (pr.WaitForExit(15000)) code = pr.ExitCode; else { try { pr.Kill(); } catch { } }
            }
            catch { }
            UiPost(delegate { Say(code == 0 ? L.T("Запит на підключення до «") + name + L.T("» надіслано.") : L.T("Не вдалося надіслати запит на підключення."), code != 0); });
        });
    }

    void DoSetKey()
    {
        WifiNet n = wsel;
        if (n == null || busy) return;
        string p = tbPass.Text;
        string err = WifiList.CheckPass(p);
        if (err.Length > 0) { wErr = err; Invalidate(); return; }
        wErr = "";
        RunWifiOp("P", n.Name, p, L.T("Пароль мережі «") + n.Name + L.T("» змінено."));
    }

    void DoForget()
    {
        WifiNet n = wsel;
        if (n == null || busy) return;
        if (Ask("Wi-Fi", L.T("Забути мережу «") + n.Name + L.T("»?\n\nЇї налаштування й пароль буде видалено з цього ПК. Щоб підключитися знову, пароль доведеться ввести."),
                L.T("Забути"), L.T("Скасувати")) != 0) return;
        RunWifiOp("F", n.Name, "", L.T("Мережу «") + n.Name + L.T("» забуто."));
    }

    // спершу пробуємо від імені користувача; якщо Windows відмовила через права - один запит UAC
    void RunWifiOp(string kind, string name, string pass, string okMsg)
    {
        busy = true;
        busyText = L.T("Виконую…");
        Invalidate();
        Util.Bg(delegate
        {
            string err = "";
            int r = kind == "P" ? WifiList.SetKey(name, pass, out err) : WifiList.Forget(name, out err);
            bool done = r == 0;
            if (r == 5 && !Elevated.IsAdmin)
            {
                UiPost(delegate { busyText = L.T("Очікую дозвіл адміністратора…"); Invalidate(); });
                List<string> l = new List<string>();
                l.Add(kind == "P" ? WifiList.PlanKey(name, pass) : WifiList.PlanForget(name));
                string t = NetCfg.RunElevated("wifiset", l, out err);
                if (t != null)
                {
                    string fe;
                    int bad;
                    done = NetCfg.Summary(t, out fe, out bad) > 0 && bad == 0;
                    err = fe;
                }
            }
            else if (r == 5) err = L.T("Windows відмовила в доступі.");
            bool forget = kind == "F";
            UiPost(delegate
            {
                busy = false;
                if (!done) { Say(err.Length > 0 ? err : L.T("Не вдалося виконати."), true); return; }
                Say(okMsg, false);
                changing = false; tbPass.Text = "";
                if (forget) { wsel = null; page = PgWifi; }
                ReloadWifi();
            });
        });
    }

    void DoAdd()
    {
        if (busy) return;
        string ssid = tbSsid.Text.Trim(), pass = tbPass.Text;
        string err = WifiList.CheckSsid(ssid);
        if (err.Length == 0 && addAuth != "nopass") err = WifiList.CheckPass(pass);
        if (err.Length > 0) { wErr = err; Invalidate(); return; }
        wErr = "";
        string auth = addAuth;
        bool hid = addHidden;
        busy = true;
        busyText = L.T("Додаю мережу…");
        Invalidate();
        Util.Bg(delegate
        {
            string e2;
            int r = WifiList.AddNet(ssid, auth, pass, hid, out e2);
            UiPost(delegate
            {
                busy = false;
                if (r != 0) { wErr = e2; Invalidate(); return; }
                tbSsid.Text = ""; tbPass.Text = "";
                Say(L.T("Мережу «") + ssid + L.T("» додано. Windows підключиться до неї, щойно вона з'явиться в зоні дії."), false);
                page = PgWifi;
                AdHide();
                ReloadWifi();
            });
        });
    }

    // ---------- малювання ----------
    int Back(Graphics g, Rectangle r, string title, Action back)
    {
        IconBtn(g, new Rectangle(r.X, r.Y, Px(32), Px(32)), "left", back, false);
        TxtL(g, title, FB, new Rectangle(r.X + Px(40), r.Y, r.Width - Px(40), Px(32)), P.Fg);
        return r.Y + Px(40);
    }

    void PaintAdapters(Graphics g, Rectangle r)
    {
        if (tbHost.Visible) tbHost.Visible = false;
        placed.Clear();
        if (!adLoaded && !adLoading) ReloadAds();
        int bot = r.Bottom - Px(38);
        switch (page)
        {
            case PgAd: PageAd(g, r, bot); break;
            case PgEdit: PageEdit(g, r, bot); break;
            case PgWifi: PageWifi(g, r, bot); break;
            case PgNet: PageNet(g, r, bot); break;
            case PgAdd: PageAdd(g, r, bot); break;
            default: PageList(g, r, bot); break;
        }
        HideUnplaced();
        Rectangle nr = new Rectangle(r.X, r.Bottom - Px(36), r.Width, Px(36));
        if (busy) TxtW(g, busyText, FS, nr, P.Sub);
        else if (NoteLive) TxtW(g, Note, FS, nr, NoteBad ? P.Bad : P.Good);
    }

    void Dot(Graphics g, Rectangle rr, Color c)
    {
        int d = Px(10);
        using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, rr.X + Px(14), rr.Y + (rr.Height - d) / 2, d, d);
    }

    void PageList(Graphics g, Rectangle r, int bot)
    {
        int y = r.Y;
        TxtL(g, adLoading ? L.T("Читаю адаптери…") : L.T("Мережеві адаптери: ") + ads.Count, FB, new Rectangle(r.X, y, r.Width - Px(120), Px(30)), P.Fg);
        Btn(g, new Rectangle(r.Right - Px(110), y, Px(110), Px(30)), L.T("Оновити"), ReloadAds, 0, !adLoading && !busy);
        y += Px(38);
        int wifiH = hasWifi ? Px(70) : 0;
        Rectangle list = new Rectangle(r.X, y, r.Width, Math.Max(Px(80), bot - y - wifiH));
        int rowH = Px(64), total = ads.Count * rowH;
        scrollA = Math.Max(0, Math.Min(Math.Max(0, total - list.Height), scrollA));
        if (ads.Count == 0) TxtC(g, adLoading ? L.T("Читаю адаптери…") : L.T("Мережевих адаптерів не знайдено"), F, list, P.Sub);
        GraphicsState st = g.Save();
        g.SetClip(list);
        for (int i = 0; i < ads.Count; i++)
        {
            AdapterInfo a = ads[i];
            Rectangle rr = new Rectangle(list.X, list.Y + i * rowH - scrollA, list.Width - (total > list.Height ? Px(10) : 0), rowH - Px(8));
            if (rr.Bottom < list.Y || rr.Top > list.Bottom) continue;
            Card(g, rr, Over(rr) ? Util.Blend(P.Card, P.Fg, 0.06f) : P.Card);
            AdapterInfo aa = a;
            Hot(Rectangle.Intersect(rr, list), delegate { OpenAd(aa); });
            Dot(g, rr, a.Up ? P.Good : (a.Disabled ? P.Warn : Util.Blend(P.Bg, P.Sub, 0.6f)));
            int rw = Px(130);
            TxtL(g, a.Name, FB, new Rectangle(rr.X + Px(34), rr.Y + Px(6), rr.Width - Px(34) - rw - Px(12), Px(24)), P.Fg);
            TxtL(g, a.Descr, FS, new Rectangle(rr.X + Px(34), rr.Y + Px(30), rr.Width - Px(34) - rw - Px(12), Px(20)), P.Sub);
            TxtR(g, a.Disabled ? L.T("вимкнено") : (a.Ips.Count > 0 ? a.Ips[0] : L.T("немає IP")), FS, new Rectangle(rr.Right - rw - Px(12), rr.Y + Px(8), rw, Px(20)), a.Disabled ? P.Warn : (a.Ips.Count > 0 ? P.Fg : P.Sub));
            TxtR(g, a.Kind, FS, new Rectangle(rr.Right - rw - Px(12), rr.Y + Px(30), rw, Px(20)), P.Sub);
        }
        g.Restore(st);
        if (total > list.Height)
        {
            Wheel(list, delegate (int dd) { scrollA -= dd > 0 ? rowH : -rowH; Invalidate(); });
            VBar(g, new Rectangle(list.Right - Px(6), list.Y + Px(2), Px(4), list.Height - Px(4)), total, list.Height, scrollA, delegate (int v) { scrollA = v; Invalidate(); });
        }
        if (hasWifi)
        {
            Rectangle wc = new Rectangle(r.X, bot - Px(62), r.Width, Px(62));
            Card(g, wc);
            TxtL(g, L.T("Збережені мережі Wi-Fi"), FB, new Rectangle(wc.X + Px(14), wc.Y + Px(8), wc.Width - Px(150), Px(22)), P.Fg);
            TxtL(g, L.T("Паролі, QR-код, нова мережа, зміна пароля"), FS, new Rectangle(wc.X + Px(14), wc.Y + Px(32), wc.Width - Px(150), Px(20)), P.Sub);
            Btn(g, new Rectangle(wc.Right - Px(122), wc.Y + Px(15), Px(110), Px(32)), L.T("Відкрити"), OpenWifi, 1, !busy);
        }
    }

    void InfoRow(Graphics g, Rectangle card, ref int y, string label, string val, Color c)
    {
        TxtL(g, label, FS, new Rectangle(card.X + Px(14), y, Px(110), Px(22)), P.Sub);
        TxtL(g, val, F, new Rectangle(card.X + Px(128), y, card.Width - Px(142), Px(22)), c);
        y += Px(22);
    }

    static string JoinList(List<string> l)
    {
        return l.Count == 0 ? L.T("немає") : string.Join(", ", l.ToArray());
    }

    void PageAd(Graphics g, Rectangle r, int bot)
    {
        AdapterInfo a = cur;
        if (a == null) { page = PgList; return; }
        int y = Back(g, r, a.Name, delegate { Go(PgList); });
        int rows = 9;
        Rectangle card = new Rectangle(r.X, y, r.Width, rows * Px(22) + Px(20));
        Card(g, card);
        int ty = card.Y + Px(10);
        InfoRow(g, card, ref ty, L.T("Адаптер"), a.Descr, P.Fg);
        InfoRow(g, card, ref ty, L.T("Стан"), a.Status, a.Up ? P.Good : P.Warn);
        InfoRow(g, card, ref ty, "MAC", a.Mac.Length > 0 ? a.Mac : "—", P.Fg);
        InfoRow(g, card, ref ty, L.T("Швидкість"), NetCfg.SpeedText(a.Speed), P.Fg);
        InfoRow(g, card, ref ty, "IPv4", JoinList(a.Ips), P.Fg);
        string masks = "";
        foreach (string m in a.Masks) if (m.Length > 0) masks += (masks.Length > 0 ? ", " : "") + m;
        InfoRow(g, card, ref ty, L.T("Маска"), masks.Length > 0 ? masks : "—", P.Fg);
        InfoRow(g, card, ref ty, L.T("Шлюз"), JoinList(a.Gateways), P.Fg);
        InfoRow(g, card, ref ty, "DNS", JoinList(a.Dns), P.Fg);
        InfoRow(g, card, ref ty, L.T("Режим"), a.Disabled ? "—" : "IP " + (a.Dhcp ? L.T("автоматично") : L.T("вручну")) + " · DNS " + (a.DnsAuto ? L.T("автоматично") : L.T("вручну")), P.Sub);
        y = card.Bottom + Px(10);

        Btn(g, new Rectangle(r.X, y, r.Width, Px(36)), L.T("Змінити IP та DNS"), OpenEdit, 1, !busy && !a.Disabled);
        y += Px(44);
        int bw = (r.Width - Px(8)) / 2;
        Rectangle be = new Rectangle(r.X, y, bw, Px(34)), bd = new Rectangle(r.X + bw + Px(8), y, bw, Px(34));
        Btn(g, be, L.T("Увімкнути адаптер"), delegate { DoState(true); }, a.Disabled ? 1 : 0, !busy && a.Disabled);
        Btn(g, bd, L.T("Вимкнути адаптер"), delegate { DoState(false); }, 0, !busy && !a.Disabled);
        Tip(be, a.Disabled ? L.T("Вмикає адаптер (потрібен дозвіл адміністратора)") : L.T("Адаптер уже ввімкнено"));
        Tip(bd, a.Disabled ? L.T("Адаптер уже вимкнено") : L.T("Вимикає адаптер до наступного вмикання"));
        y += Px(42);
        if (a.Wireless)
        {
            Btn(g, new Rectangle(r.X, y, r.Width, Px(34)), L.T("Wi-Fi: збережені мережі, паролі"), OpenWifi, 0, !busy);
            y += Px(42);
        }
        if (!Elevated.IsAdmin && y + Px(36) <= bot)
            TxtW(g, L.T("Зміни потребують дозволу адміністратора: Windows запитає його, коли ви підтвердите зміну."), FS, new Rectangle(r.X, y, r.Width, Px(36)), P.Sub);
    }

    void Lbl(Graphics g, Rectangle r, string s)
    {
        TxtL(g, s, FS, r, P.Sub);
    }

    void PageEdit(Graphics g, Rectangle r, int bot)
    {
        AdapterInfo a = cur;
        if (a == null) { page = PgList; return; }
        int y = Back(g, r, L.T("Налаштування: ") + a.Name, delegate { Go(PgAd); });
        int cw = (r.Width - Px(6)) / 2;
        Lbl(g, new Rectangle(r.X, y, r.Width, Px(18)), L.T("IP-адреса"));
        y += Px(20);
        Chip(g, new Rectangle(r.X, y, cw, Px(30)), L.T("Автоматично (DHCP)"), !manIp, delegate { manIp = false; editErr = ""; });
        Chip(g, new Rectangle(r.X + cw + Px(6), y, cw, Px(30)), L.T("Вручну"), manIp, delegate { manIp = true; editErr = ""; });
        y += Px(36);
        if (manIp)
        {
            int fw = (r.Width - Px(16)) / 3;
            string[] lb = { L.T("IP-адреса"), L.T("Маска підмережі"), L.T("Шлюз (можна пусто)") };
            TextBox[] tb = { tbIp, tbMask, tbGw };
            for (int i = 0; i < 3; i++)
            {
                int fx = r.X + i * (fw + Px(8));
                Lbl(g, new Rectangle(fx, y, fw, Px(16)), lb[i]);
                Fld(g, tb[i], new Rectangle(fx, y + Px(18), fw, Px(30)));
            }
            y += Px(54);
        }
        Lbl(g, new Rectangle(r.X, y, r.Width, Px(18)), L.T("DNS-сервери"));
        y += Px(20);
        Chip(g, new Rectangle(r.X, y, cw, Px(30)), L.T("Автоматично"), !manDns, delegate { manDns = false; editErr = ""; });
        Chip(g, new Rectangle(r.X + cw + Px(6), y, cw, Px(30)), L.T("Вручну"), manDns, delegate { manDns = true; editErr = ""; });
        y += Px(36);
        if (manDns)
        {
            int fw = (r.Width - Px(8)) / 2;
            Lbl(g, new Rectangle(r.X, y, fw, Px(16)), L.T("Основний"));
            Lbl(g, new Rectangle(r.X + fw + Px(8), y, fw, Px(16)), L.T("Додатковий (можна пусто)"));
            Fld(g, tbD1, new Rectangle(r.X, y + Px(18), fw, Px(30)));
            Fld(g, tbD2, new Rectangle(r.X + fw + Px(8), y + Px(18), fw, Px(30)));
            y += Px(54);
            int pw = (r.Width - Px(4) * (DnsPairs.Length - 1)) / DnsPairs.Length;
            for (int i = 0; i < DnsPairs.Length; i++)
            {
                string[] pr = DnsPairs[i];
                bool on = tbD1.Text.Trim() == pr[1] && tbD2.Text.Trim() == pr[2];
                Chip(g, new Rectangle(r.X + i * (pw + Px(4)), y, pw, Px(26)), pr[0], on, delegate { tbD1.Text = pr[1]; tbD2.Text = pr[2]; editErr = ""; });
            }
            y += Px(34);
        }
        if (editErr.Length > 0)
        {
            TxtW(g, editErr, FS, new Rectangle(r.X, y, r.Width, Px(34)), P.Bad);
            y += Px(36);
        }
        bool canUndo = undo != null && undoId == a.Id;
        int bw = canUndo ? (r.Width - Px(8)) / 2 : r.Width;
        Btn(g, new Rectangle(r.X, y, bw, Px(36)), L.T("Застосувати"), ApplyEdit, 1, !busy);
        if (canUndo)
        {
            Rectangle ub = new Rectangle(r.X + bw + Px(8), y, bw, Px(36));
            Btn(g, ub, L.T("Повернути попередні"), DoUndo, 0, !busy);
            Tip(ub, L.T("Повертає IP і DNS, які були до останньої зміни"));
        }
        y += Px(44);
        if (!Elevated.IsAdmin && y + Px(34) <= bot)
            TxtW(g, L.T("Зміна мережевих налаштувань потребує дозволу адміністратора - Windows запитає його після підтвердження."), FS, new Rectangle(r.X, y, r.Width, Px(34)), P.Sub);
    }

    void PageWifi(Graphics g, Rectangle r, int bot)
    {
        int y = Back(g, r, L.T("Збережені мережі Wi-Fi"), delegate { Go(PgList); });
        int locked = LockedCount();
        bool needAdmin = locked > 0 && !Elevated.IsAdmin;
        int n3 = needAdmin ? 3 : 2;
        int bw = (r.Width - Px(8) * (n3 - 1)) / n3;
        Btn(g, new Rectangle(r.X, y, bw, Px(32)), L.T("Додати мережу"), OpenAdd, 1, !busy && !WifiOff());
        Btn(g, new Rectangle(r.X + bw + Px(8), y, bw, Px(32)), L.T("Оновити"), ReloadWifi, 0, !wLoading && !wAsk && !busy);
        if (needAdmin) Btn(g, new Rectangle(r.X + (bw + Px(8)) * 2, y, bw, Px(32)), L.T("Показати паролі"), Unlock, 0, !wLoading && !wAsk && !busy);
        y += Px(40);

        int foot = Px(42);
        Rectangle list = new Rectangle(r.X, y, r.Width, Math.Max(Px(80), bot - y - foot));
        int rowH = Px(58), total = nets.Count * rowH;
        scrollW = Math.Max(0, Math.Min(Math.Max(0, total - list.Height), scrollW));
        if (nets.Count == 0)
            TxtW(g, wLoading ? L.T("Читаю мережі…") : (WifiOff() ? L.T("Wi-Fi-адаптер вимкнено, тому Windows не віддає збережені мережі. Увімкніть його на сторінці адаптера.") : (wError.Length > 0 ? wError : L.T("Збережених мереж немає"))), F,
                 Rectangle.Inflate(list, -Px(20), -Px(20)), wLoading ? P.Sub : (wError.Length > 0 || WifiOff() ? P.Warn : P.Sub));
        GraphicsState st = g.Save();
        g.SetClip(list);
        for (int i = 0; i < nets.Count; i++)
        {
            WifiNet n = nets[i];
            Rectangle rr = new Rectangle(list.X, list.Y + i * rowH - scrollW, list.Width - (total > list.Height ? Px(10) : 0), rowH - Px(8));
            if (rr.Bottom < list.Y || rr.Top > list.Bottom) continue;
            Card(g, rr, Over(rr) ? Util.Blend(P.Card, P.Fg, 0.06f) : P.Card);
            WifiNet nn = n;
            Hot(Rectangle.Intersect(rr, list), delegate { Pick(nn, true); });
            Dot(g, rr, n.Connected ? P.Good : Util.Blend(P.Bg, P.Sub, 0.6f));
            TxtL(g, n.Name, FB, new Rectangle(rr.X + Px(34), rr.Y + Px(5), rr.Width - Px(34) - Px(150), Px(22)), P.Fg);
            string sub = n.Sec + (n.Connected ? L.T(" · підключено") : "") + (n.Hidden ? L.T(" · прихована") : "");
            TxtL(g, sub, FS, new Rectangle(rr.X + Px(34), rr.Y + Px(27), rr.Width - Px(34) - Px(150), Px(18)), n.Connected ? P.Good : P.Sub);
            bool canShow = !n.Open && !n.Enterprise && !n.Locked;
            string pt = n.Open ? L.T("без пароля") : (n.Enterprise ? L.T("корпоративна") : (n.Locked ? L.T("пароль приховано") : (shown.Contains(n.Name) ? n.Pass : "••••••••")));
            TxtR(g, pt, FS, new Rectangle(rr.Right - Px(146), rr.Y + Px(14), Px(134), Px(22)), canShow && shown.Contains(n.Name) ? P.Fg : P.Sub);
        }
        g.Restore(st);
        if (total > list.Height)
        {
            Wheel(list, delegate (int dd) { scrollW -= dd > 0 ? rowH : -rowH; Invalidate(); });
            VBar(g, new Rectangle(list.Right - Px(6), list.Y + Px(2), Px(4), list.Height - Px(4)), total, list.Height, scrollW, delegate (int v) { scrollW = v; Invalidate(); });
        }
        y = list.Bottom + Px(8);
        int hw = (r.Width - Px(8)) / 2;
        Btn(g, new Rectangle(r.X, y, hw, Px(32)), L.T("Копіювати список"), CopyList, 0, nets.Count > 0);
        Btn(g, new Rectangle(r.X + hw + Px(8), y, hw, Px(32)), L.T("Зберегти список…"), SaveList, 0, nets.Count > 0);
    }

    void PageNet(Graphics g, Rectangle r, int bot)
    {
        WifiNet n = wsel;
        if (n == null) { page = PgWifi; return; }
        int y = Back(g, r, n.Name, delegate { changing = false; tbPass.Text = ""; Go(PgWifi); });
        TxtL(g, n.Sec + (n.Connected ? L.T(" · підключено") : "") + (n.Hidden ? L.T(" · прихована") : "") + (n.Auto ? "" : L.T(" · без автопідключення")), FS,
             new Rectangle(r.X + Px(40), y - Px(12), r.Width - Px(40), Px(18)), n.Connected ? P.Good : P.Sub);
        y += Px(10);

        // пароль
        Rectangle pc = new Rectangle(r.X, y, r.Width, Px(60));
        Card(g, pc);
        TxtL(g, L.T("Пароль"), FS, new Rectangle(pc.X + Px(14), pc.Y + Px(8), Px(120), Px(18)), P.Sub);
        bool canShow = !n.Open && !n.Enterprise && !n.Locked;
        if (n.Locked)
        {
            TxtL(g, L.T("Потрібні права адміністратора"), F, new Rectangle(pc.X + Px(14), pc.Y + Px(28), pc.Width - Px(190), Px(24)), P.Warn);
            if (!Elevated.IsAdmin) Btn(g, new Rectangle(pc.Right - Px(166), pc.Y + Px(14), Px(154), Px(32)), L.T("Показати паролі"), Unlock, 1, !wAsk && !busy);
        }
        else
        {
            TxtL(g, PassText(n), FB, new Rectangle(pc.X + Px(14), pc.Y + Px(28), pc.Width - Px(250), Px(24)), canShow && shown.Contains(n.Name) ? P.Fg : P.Sub);
            if (canShow)
            {
                bool sh = shown.Contains(n.Name);
                Btn(g, new Rectangle(pc.Right - Px(226), pc.Y + Px(14), Px(104), Px(32)), sh ? L.T("Сховати") : L.T("Показати"), delegate { if (!shown.Remove(n.Name)) shown.Add(n.Name); Invalidate(); }, 0, true);
                Btn(g, new Rectangle(pc.Right - Px(116), pc.Y + Px(14), Px(104), Px(32)), L.T("Копіювати"), delegate { CopySecret(n.Pass); Say(L.T("Пароль скопійовано. Буфер обміну очиститься за хвилину."), false); }, 0, n.Pass.Length > 0);
            }
        }
        y = pc.Bottom + Px(8);

        if (changing)
        {
            Rectangle cc = new Rectangle(r.X, y, r.Width, Px(124));
            Card(g, cc);
            TxtL(g, L.T("Новий пароль мережі"), FS, new Rectangle(cc.X + Px(14), cc.Y + Px(8), cc.Width - Px(28), Px(18)), P.Sub);
            tbPass.UseSystemPasswordChar = !showPass;
            Fld(g, tbPass, new Rectangle(cc.X + Px(14), cc.Y + Px(30), cc.Width - Px(28), Px(32)));
            Switch(g, new Rectangle(cc.X + Px(14), cc.Y + Px(66), cc.Width - Px(28), Px(24)), L.T("Показувати пароль"), showPass, delegate { showPass = !showPass; Invalidate(); });
            int hw = (cc.Width - Px(36)) / 2;
            Btn(g, new Rectangle(cc.X + Px(14), cc.Bottom - Px(36), hw, Px(30)), L.T("Зберегти пароль"), DoSetKey, 1, !busy);
            Btn(g, new Rectangle(cc.X + Px(22) + hw, cc.Bottom - Px(36), hw, Px(30)), L.T("Скасувати"), delegate { changing = false; tbPass.Text = ""; wErr = ""; Invalidate(); }, 0, true);
            y = cc.Bottom + Px(6);
            if (wErr.Length > 0) { TxtW(g, wErr, FS, new Rectangle(r.X, y, r.Width, Px(34)), P.Bad); y += Px(36); }
            else { TxtW(g, L.T("Пароль міняється лише на цьому ПК - сам роутер не зміниться. Новий пароль має збігатися з тим, що встановлено на роутері."), FS, new Rectangle(r.X, y, r.Width, Px(34)), P.Sub); y += Px(36); }
        }
        else if (qrBmp != null)
        {
            int qh = Px(188);
            Rectangle card = new Rectangle(r.X, y, r.Width, qh);
            Card(g, card);
            int qs = qh - Px(20);
            Rectangle qrR = new Rectangle(card.X + Px(10), card.Y + Px(10), qs, qs);
            InterpolationMode im = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            PixelOffsetMode po = g.PixelOffsetMode;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(qrBmp, qrR);
            g.InterpolationMode = im; g.PixelOffsetMode = po;
            int tx = qrR.Right + Px(16), tw = card.Right - tx - Px(14);
            TxtL(g, L.T("QR-код для телефона"), FB, new Rectangle(tx, card.Y + Px(10), tw, Px(24)), P.Fg);
            TxtW(g, L.T("Наведіть камеру: телефон запропонує підключитися до цієї мережі. Код містить пароль - показуйте його лише тому, кому довіряєте."), FS, new Rectangle(tx, card.Y + Px(38), tw, Px(76)), P.Sub);
            Btn(g, new Rectangle(tx, card.Bottom - Px(78), tw, Px(30)), L.T("Копіювати код"), CopyQr, 0, true);
            Btn(g, new Rectangle(tx, card.Bottom - Px(42), tw, Px(30)), L.T("Зберегти PNG…"), SaveQr, 0, true);
            y = card.Bottom + Px(8);
        }
        else
        {
            string why = n.Enterprise ? L.T("Корпоративна мережа (вхід за логіном і паролем облікового запису) - QR-код і пароль для неї не передбачені; налаштовується в Windows.")
                       : (n.Locked ? L.T("Пароль цієї мережі ще не прочитано - натисніть «Показати паролі».") : L.T("QR-код для цієї мережі недоступний."));
            TxtW(g, why, FS, new Rectangle(r.X, y, r.Width, Px(50)), P.Warn);
            y += Px(54);
        }

        int bw = (r.Width - Px(16)) / 3;
        bool canChange = !n.Open && !n.Enterprise && !changing;
        Btn(g, new Rectangle(r.X, y, bw, Px(34)), L.T("Змінити пароль"), delegate { changing = true; showPass = false; tbPass.Text = ""; wErr = ""; Invalidate(); }, 0, canChange && !busy);
        Btn(g, new Rectangle(r.X + bw + Px(8), y, bw, Px(34)), L.T("Підключитися"), DoConnect, 0, !busy);
        Btn(g, new Rectangle(r.X + (bw + Px(8)) * 2, y, bw, Px(34)), L.T("Забути"), DoForget, 0, !busy);
    }

    void PageAdd(Graphics g, Rectangle r, int bot)
    {
        int y = Back(g, r, L.T("Нова мережа Wi-Fi"), delegate { Go(PgWifi); });
        Lbl(g, new Rectangle(r.X, y, r.Width, Px(18)), L.T("Назва мережі (SSID)"));
        y += Px(20);
        Fld(g, tbSsid, new Rectangle(r.X, y, r.Width, Px(32)));
        y += Px(40);
        Lbl(g, new Rectangle(r.X, y, r.Width, Px(18)), L.T("Захист"));
        y += Px(20);
        int cw = (r.Width - Px(12)) / 3;
        Chip(g, new Rectangle(r.X, y, cw, Px(30)), "WPA2", addAuth == "WPA", delegate { addAuth = "WPA"; wErr = ""; });
        Chip(g, new Rectangle(r.X + cw + Px(6), y, cw, Px(30)), "WPA3", addAuth == "SAE", delegate { addAuth = "SAE"; wErr = ""; });
        Chip(g, new Rectangle(r.X + (cw + Px(6)) * 2, y, cw, Px(30)), L.T("Відкрита"), addAuth == "nopass", delegate { addAuth = "nopass"; wErr = ""; });
        y += Px(38);
        if (addAuth != "nopass")
        {
            Lbl(g, new Rectangle(r.X, y, r.Width, Px(18)), L.T("Пароль мережі"));
            y += Px(20);
            tbPass.UseSystemPasswordChar = !showPass;
            Fld(g, tbPass, new Rectangle(r.X, y, r.Width, Px(32)));
            y += Px(38);
            Switch(g, new Rectangle(r.X, y, r.Width, Px(26)), L.T("Показувати пароль"), showPass, delegate { showPass = !showPass; Invalidate(); });
            y += Px(30);
        }
        Switch(g, new Rectangle(r.X, y, r.Width, Px(26)), L.T("Прихована мережа (не віддає назву)"), addHidden, delegate { addHidden = !addHidden; Invalidate(); });
        y += Px(34);
        if (wErr.Length > 0) { TxtW(g, wErr, FS, new Rectangle(r.X, y, r.Width, Px(34)), P.Bad); y += Px(36); }
        Btn(g, new Rectangle(r.X, y, r.Width, Px(36)), L.T("Додати мережу"), DoAdd, 1, !busy);
        y += Px(44);
        if (y + Px(50) <= bot)
            TxtW(g, L.T("Мережа додається для вашого облікового запису й підключається автоматично, коли з'явиться в зоні дії. Корпоративні мережі (вхід за логіном і паролем облікового запису) тут не налаштовуються - їх додає Windows."), FS, new Rectangle(r.X, y, r.Width, Math.Max(Px(50), bot - y)), P.Sub);
    }
}
