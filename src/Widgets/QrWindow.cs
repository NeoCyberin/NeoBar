using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// ======================================================================
//  Віджет «QR-код»: текст, посилання, Wi-Fi, контакт, email, SMS, телефон, геоточка.
//  Логотип (картинка) в центрі, копіювання в буфер, збереження PNG і SVG.
//  Уведене живе лише до виходу з NeoBar (пароль Wi-Fi нікуди не записується).
// ======================================================================
// список підказок адрес: рядки в стилі теми, клік вибирає; не бере фокус, щоб поле пошуку лишалось активним
sealed class SugList : Control
{
    public List<string> Items = new List<string>();
    public Color Back, Edge, Fg, HotBack;
    public int RowH = 30;
    int hover = -1;
    public event Action<int> Picked;

    public SugList()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        SetStyle(ControlStyles.Selectable, false);
        Cursor = Cursors.Hand;
    }

    public int Hover
    {
        get { return hover; }
        set { int v = Math.Max(-1, Math.Min(Items.Count - 1, value)); if (v != hover) { hover = v; Invalidate(); } }
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); Hover = e.Y / Math.Max(1, RowH); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Hover = -1; }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int i = e.Y / Math.Max(1, RowH);
        if (i >= 0 && i < Items.Count && Picked != null) Picked(i);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Back);
        for (int i = 0; i < Items.Count; i++)
        {
            Rectangle rr = new Rectangle(0, i * RowH, Width, RowH);
            if (i == hover) using (SolidBrush b = new SolidBrush(HotBack)) g.FillRectangle(b, rr);
            TextRenderer.DrawText(g, Items[i], Font, new Rectangle(rr.X + 10, rr.Y, rr.Width - 16, rr.Height), Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
        using (Pen p = new Pen(Edge)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
    }
}

static class QrState
{
    public static string Kind = "text";
    public static readonly Dictionary<string, string> Values = new Dictionary<string, string>();
    public static string WifiAuth = "WPA";
    public static bool WifiHidden;
    public static int Ecl = 1;
    public static bool Colored;                 // кольоровий код (інакше чорний)
    public static Color Custom = Color.Empty;   // вибраний колір; порожній - колір теми (затемнений для сканера)
    public static Image Logo;

    public static string Get(string k) { string v; return Values.TryGetValue(k, out v) ? v : ""; }

    static string EscWifi(string s)
    {
        StringBuilder b = new StringBuilder();
        foreach (char c in s ?? "") { if (c == '\\' || c == ';' || c == ',' || c == ':' || c == '"') b.Append('\\'); b.Append(c); }
        return b.ToString();
    }

    // стандартний рядок для QR-коду підключення до Wi-Fi (його розпізнають камери телефонів)
    public static string WifiPayload(string ssid, string auth, string pass, bool hidden)
    {
        string t = auth == "nopass" ? "nopass" : auth;
        string s = "WIFI:T:" + t + ";S:" + EscWifi(ssid) + ";";
        if (auth != "nopass") s += "P:" + EscWifi(pass) + ";";
        if (hidden) s += "H:true;";
        return s + ";";
    }

    static string EscV(string s)
    {
        return (s ?? "").Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");
    }

    static string Num(string s) { return (s ?? "").Trim().Replace(',', '.'); }

    // номер для набору: цифри, + лише на початку, * і #; дужки, дефіси, крапки й пробіли - лише оформлення, відкидаються
    public static string Phone(string s)
    {
        StringBuilder b = new StringBuilder();
        s = (s ?? "").Trim();
        bool digit = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c >= '0' && c <= '9') { b.Append(c); digit = true; }
            else if (c == '+' && b.Length == 0) b.Append(c);
            else if (c == '*' || c == '#') b.Append(c);
        }
        return digit ? b.ToString() : "";
    }

    // у номері є щось, крім цифр, + ( ) - . пробілів, * і #
    public static bool PhoneOdd(string s)
    {
        foreach (char c in s ?? "")
            if (!(c >= '0' && c <= '9') && "+()-. *#\u00A0".IndexOf(c) < 0) return true;
        return false;
    }

    // що саме закодувати; "" - даних ще немає
    public static string Payload()
    {
        switch (Kind)
        {
            case "url":
            {
                string u = Get("url").Trim();
                if (u.Length == 0) return "";
                if (u.IndexOf("://", StringComparison.Ordinal) < 0 && !u.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) u = "https://" + u;
                return u;
            }
            case "wifi":
            {
                string ssid = Get("ssid");
                if (ssid.Length == 0) return "";
                return WifiPayload(ssid, WifiAuth, Get("wpass"), WifiHidden);
            }
            case "contact":
            {
                string n = Get("cname").Trim();
                if (n.Length == 0 && Get("ctel").Trim().Length == 0 && Get("cmail").Trim().Length == 0) return "";
                StringBuilder b = new StringBuilder();
                b.Append("BEGIN:VCARD\r\nVERSION:3.0\r\n");
                b.Append("N:" + EscV(n) + ";;;;\r\nFN:" + EscV(n) + "\r\n");
                if (Get("corg").Trim().Length > 0) b.Append("ORG:" + EscV(Get("corg").Trim()) + "\r\n");
                if (Get("ctel").Trim().Length > 0) b.Append("TEL;TYPE=CELL:" + EscV(Get("ctel").Trim()) + "\r\n");
                if (Get("cmail").Trim().Length > 0) b.Append("EMAIL:" + EscV(Get("cmail").Trim()) + "\r\n");
                if (Get("curl").Trim().Length > 0) b.Append("URL:" + EscV(Get("curl").Trim()) + "\r\n");
                b.Append("END:VCARD");
                return b.ToString();
            }
            case "email":
            {
                string to = Get("mto").Trim();
                if (to.Length == 0) return "";
                List<string> q = new List<string>();
                if (Get("msubj").Length > 0) q.Add("subject=" + Uri.EscapeDataString(Get("msubj")));
                if (Get("mbody").Length > 0) q.Add("body=" + Uri.EscapeDataString(Get("mbody")));
                return "mailto:" + to + (q.Count > 0 ? "?" + string.Join("&", q.ToArray()) : "");
            }
            case "sms":
            {
                string num = Phone(Get("snum"));
                if (num.Length == 0) return "";
                return "SMSTO:" + num + ":" + Get("stext");
            }
            case "tel":
            {
                string num = Phone(Get("tnum"));
                return num.Length == 0 ? "" : "tel:" + num;
            }
            case "geo":
            {
                double la, lo;
                if (!double.TryParse(Num(Get("glat")), NumberStyles.Float, CultureInfo.InvariantCulture, out la) ||
                    !double.TryParse(Num(Get("glon")), NumberStyles.Float, CultureInfo.InvariantCulture, out lo)) return "";
                if (Math.Abs(la) > 90 || Math.Abs(lo) > 180) return "";
                return "geo:" + la.ToString("0.######", CultureInfo.InvariantCulture) + "," + lo.ToString("0.######", CultureInfo.InvariantCulture);
            }
        }
        return Get("text");
    }

    // зображення коду: module - пікселів на модуль, з полем 4 модулі
    public static Bitmap Render(QrCode.Result q, int module, Color dark)
    {
        int border = 4, n = (q.Size + border * 2) * module;
        Bitmap bm = new Bitmap(n, n, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bm))
        {
            g.Clear(Color.White);
            using (SolidBrush b = new SolidBrush(dark))
                for (int y = 0; y < q.Size; y++)
                    for (int x = 0; x < q.Size; x++)
                        if (q.Dark[y, x]) g.FillRectangle(b, (x + border) * module, (y + border) * module, module, module);
            if (Logo != null) DrawLogo(g, n);
        }
        return bm;
    }

    static RectangleF LogoRect(float n)
    {
        float s = n * 0.2f;
        return new RectangleF((n - s) / 2f, (n - s) / 2f, s, s);
    }

    static void DrawLogo(Graphics g, float n)
    {
        RectangleF r = LogoRect(n);
        RectangleF plate = RectangleF.Inflate(r, r.Width * 0.08f, r.Height * 0.08f);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath gp = Util.Round(Rectangle.Round(plate), (int)(plate.Width * 0.18f)))
        using (SolidBrush wb = new SolidBrush(Color.White)) g.FillPath(wb, gp);
        float k = Math.Min(r.Width / Logo.Width, r.Height / Logo.Height);
        float w = Logo.Width * k, h = Logo.Height * k;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(Logo, r.X + (r.Width - w) / 2f, r.Y + (r.Height - h) / 2f, w, h);
    }

    public static string Svg(QrCode.Result q, Color dark)
    {
        int border = 4, n = q.Size + border * 2;
        StringBuilder sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" version=\"1.1\" viewBox=\"0 0 " + n + " " + n + "\" shape-rendering=\"crispEdges\">\n");
        sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"#FFFFFF\"/>\n<path fill=\"#" + dark.R.ToString("X2") + dark.G.ToString("X2") + dark.B.ToString("X2") + "\" d=\"");
        for (int y = 0; y < q.Size; y++)
            for (int x = 0; x < q.Size; x++)
            {
                if (!q.Dark[y, x]) continue;
                int run = 1;
                while (x + run < q.Size && q.Dark[y, x + run]) run++;
                sb.Append("M" + (x + border) + "," + (y + border) + "h" + run + "v1h-" + run + "z");
                x += run - 1;
            }
        sb.Append("\"/>\n");
        if (Logo != null)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (Bitmap lb = new Bitmap(Logo)) lb.Save(ms, ImageFormat.Png);
                RectangleF r = LogoRect(n);
                RectangleF p = RectangleF.Inflate(r, r.Width * 0.08f, r.Height * 0.08f);
                string f = "0.###";
                sb.Append("<rect x=\"" + p.X.ToString(f, CultureInfo.InvariantCulture) + "\" y=\"" + p.Y.ToString(f, CultureInfo.InvariantCulture) + "\" width=\"" + p.Width.ToString(f, CultureInfo.InvariantCulture) +
                          "\" height=\"" + p.Height.ToString(f, CultureInfo.InvariantCulture) + "\" rx=\"" + (p.Width * 0.18f).ToString(f, CultureInfo.InvariantCulture) + "\" fill=\"#FFFFFF\"/>\n");
                sb.Append("<image x=\"" + r.X.ToString(f, CultureInfo.InvariantCulture) + "\" y=\"" + r.Y.ToString(f, CultureInfo.InvariantCulture) + "\" width=\"" + r.Width.ToString(f, CultureInfo.InvariantCulture) +
                          "\" height=\"" + r.Height.ToString(f, CultureInfo.InvariantCulture) + "\" preserveAspectRatio=\"xMidYMid meet\" href=\"data:image/png;base64," + Convert.ToBase64String(ms.ToArray()) + "\"/>\n");
            }
        }
        sb.Append("</svg>\n");
        return sb.ToString();
    }
}

// поточна мережа Wi-Fi і її пароль (через WLAN API; пароль у відкритому вигляді Windows дає лише адміністратору)
static class WifiInfo
{

    static string Tag(string xml, string tag)
    {
        int a = xml.IndexOf("<" + tag + ">", StringComparison.Ordinal);
        if (a < 0) return "";
        a += tag.Length + 2;
        int b = xml.IndexOf("</" + tag + ">", a, StringComparison.Ordinal);
        return b > a ? System.Net.WebUtility.HtmlDecode(xml.Substring(a, b - a)) : "";
    }

    // false - Wi-Fi не підключено або служба WLAN недоступна; error - пояснення
    public static bool Current(out string ssid, out string pass, out string auth, out string error)
    {
        ssid = pass = error = ""; auth = "WPA";
        IntPtr h = IntPtr.Zero, list = IntPtr.Zero;
        try
        {
            int neg;
            if (WifiList.WlanOpenHandle(2, IntPtr.Zero, out neg, out h) != 0) { error = L.T("Wi-Fi недоступний на цьому ПК."); return false; }
            if (WifiList.WlanEnumInterfaces(h, IntPtr.Zero, out list) != 0) { error = L.T("Не вдалося прочитати адаптери Wi-Fi."); return false; }
            int n = Marshal.ReadInt32(list, 0);
            for (int i = 0; i < n; i++)
            {
                IntPtr item = new IntPtr(list.ToInt64() + 8 + i * 532);
                byte[] gb = new byte[16];
                Marshal.Copy(item, gb, 0, 16);
                Guid g = new Guid(gb);
                int state = Marshal.ReadInt32(item, 16 + 512);
                if (state != 1) continue;           // wlan_interface_state_connected
                int sz; IntPtr data;
                if (WifiList.WlanQueryInterface(h, ref g, 7, IntPtr.Zero, out sz, out data, IntPtr.Zero) != 0) continue;   // current_connection
                string prof;
                try { prof = Marshal.PtrToStringUni(new IntPtr(data.ToInt64() + 8)); }
                finally { WifiList.WlanFreeMemory(data); }
                if (string.IsNullOrEmpty(prof)) continue;
                IntPtr xp; int flags = 4, access;       // WLAN_PROFILE_GET_PLAINTEXT_KEY
                if (WifiList.WlanGetProfile(h, ref g, prof, IntPtr.Zero, out xp, ref flags, out access) != 0) { ssid = prof; return true; }
                string xml;
                try { xml = Marshal.PtrToStringUni(xp); }
                finally { WifiList.WlanFreeMemory(xp); }
                int ss = xml.IndexOf("<SSID>", StringComparison.Ordinal);
                ssid = ss >= 0 ? Tag(xml.Substring(ss), "name") : "";
                if (ssid.Length == 0) ssid = prof;
                string a = Tag(xml, "authentication").ToUpperInvariant();
                auth = a == "OPEN" ? (Tag(xml, "encryption").ToUpperInvariant() == "WEP" ? "WEP" : "nopass") : (a.StartsWith("SHARED") ? "WEP" : "WPA");
                pass = Tag(xml, "keyMaterial");
                if (pass.Length == 0 && auth != "nopass") error = L.T("Пароль Windows показує лише адміністратору - уведіть його вручну.");
                return true;
            }
            error = L.T("Wi-Fi не підключено.");
            return false;
        }
        catch (DllNotFoundException) { error = L.T("Wi-Fi недоступний на цьому ПК."); return false; }
        catch (Exception ex) { error = ex.Message; return false; }
        finally
        {
            if (list != IntPtr.Zero) WifiList.WlanFreeMemory(list);
            if (h != IntPtr.Zero) WifiList.WlanCloseHandle(h, IntPtr.Zero);
        }
    }
}

sealed class QrWindow : SbWindow
{
    static readonly string[][] Kinds = {
        new[] { "text", L.T("Текст") }, new[] { "url", L.T("Посилання") }, new[] { "wifi", "Wi-Fi" }, new[] { "contact", L.T("Контакт") },
        new[] { "email", "Email" }, new[] { "sms", "SMS" }, new[] { "tel", L.T("Телефон") }, new[] { "geo", L.T("Геоточка") } };

    // поля кожного виду: ключ, підпис, багаторядкове
    static readonly Dictionary<string, string[][]> Fields = new Dictionary<string, string[][]>
    {
        { "text", new[] { new[] { "text", L.T("Текст"), "m" } } },
        { "url", new[] { new[] { "url", L.T("Адреса сайту"), "" } } },
        { "wifi", new[] { new[] { "ssid", L.T("Назва мережі (SSID)"), "" }, new[] { "wpass", L.T("Пароль"), "" } } },
        { "contact", new[] { new[] { "cname", L.T("Ім'я"), "" }, new[] { "ctel", L.T("Телефон"), "" }, new[] { "cmail", "Email", "" }, new[] { "corg", L.T("Організація"), "" }, new[] { "curl", L.T("Сайт"), "" } } },
        { "email", new[] { new[] { "mto", L.T("Кому"), "" }, new[] { "msubj", L.T("Тема"), "" }, new[] { "mbody", L.T("Текст листа"), "m" } } },
        { "sms", new[] { new[] { "snum", L.T("Номер"), "" }, new[] { "stext", L.T("Повідомлення"), "m" } } },
        { "tel", new[] { new[] { "tnum", L.T("Номер телефону"), "" } } },
        { "geo", new[] { new[] { "glat", L.T("Широта"), "" }, new[] { "glon", L.T("Довгота"), "" } } },       // малюються окремо (GeoUi): карта, пошук, дві колонки
    };

    readonly Dictionary<string, TextBox> boxes = new Dictionary<string, TextBox>();
    TextBox tbSearch;
    SugList sug;
    System.Windows.Forms.Timer sugTimer;
    string sugLast = "";
    List<string[]> sugItems = new List<string[]>();
    MapView map;
    readonly int baseH;
    QrCode.Result code;
    Bitmap preview;
    int previewModule;
    string note = "", sig = "";

    public QrWindow(NeoBar bar) : base(bar, L.T("QR-код"), 480, 780)
    { Glyph = "\uED14";
        foreach (KeyValuePair<string, string[][]> kv in Fields)
            foreach (string[] f in kv.Value)
            {
                TextBox tb = MakeInput();
                string key = f[0];
                if (f[2] == "m") { tb.Multiline = true; tb.AcceptsReturn = true; tb.ScrollBars = ScrollBars.None; }      // смуга - власна (MultiField)
                if (key == "wpass") tb.UseSystemPasswordChar = true;
                tb.Text = QrState.Get(key);
                tb.TextChanged += delegate { QrState.Values[key] = tb.Text; Rebuild(); SyncMarker(); Invalidate(); };
                boxes[key] = tb;
            }
        tbSearch = MakeInput();
        sug = new SugList();
        sug.Visible = false;
        sug.Picked += PickSug;
        Controls.Add(sug);
        sugTimer = new System.Windows.Forms.Timer();
        sugTimer.Interval = 350;
        sugTimer.Tick += delegate { sugTimer.Stop(); Suggest(false); };
        tbSearch.TextChanged += delegate
        {
            string q = tbSearch.Text.Trim();
            if (q == sugLast) return;
            sugTimer.Stop();
            if (q.Length >= 3) sugTimer.Start(); else HideSug();
        };
        tbSearch.LostFocus += delegate { if (!sug.Bounds.Contains(PointToClient(Cursor.Position))) HideSug(); };
        tbSearch.KeyDown += delegate (object o, KeyEventArgs ke)
        {
            bool open = sug.Visible && sugItems.Count > 0;
            if (ke.KeyCode == Keys.Down && open) { sug.Hover = sug.Hover + 1; ke.Handled = true; ke.SuppressKeyPress = true; }
            else if (ke.KeyCode == Keys.Up && open) { sug.Hover = Math.Max(0, sug.Hover - 1); ke.Handled = true; ke.SuppressKeyPress = true; }
            else if (ke.KeyCode == Keys.Escape && open) { HideSug(); ke.Handled = true; ke.SuppressKeyPress = true; }
            else if (ke.KeyCode == Keys.Enter)
            {
                ke.Handled = true; ke.SuppressKeyPress = true;
                if (open) PickSug(sug.Hover >= 0 ? sug.Hover : 0); else Suggest(true);
            }
        };
        baseH = Height;
        Rebuild();
        FitHeight();
    }

    // геоточка: вікно вище, щоб вмістилася карта
    void FitHeight()
    {
        Rectangle wa = Screen.FromControl(this).WorkingArea;
        int want = Math.Min(QrState.Kind == "geo" ? baseH + Px(130) + (ClientSize.Width - Px(32) - Px(200)) : baseH, wa.Height - Px(8));
        if (Height != want) Height = want;
        if (Bottom > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - Height);
    }

    static bool Coords(string a, string b, out double la, out double lo)
    {
        la = lo = 0;
        return double.TryParse(Num2(a), NumberStyles.Float, CultureInfo.InvariantCulture, out la) &&
               double.TryParse(Num2(b), NumberStyles.Float, CultureInfo.InvariantCulture, out lo) && Math.Abs(la) <= 90 && Math.Abs(lo) <= 180;
    }
    static string Num2(string s) { return (s ?? "").Trim().Replace(',', '.'); }

    // поля широти й довготи змінено вручну - маркер на карті слідом
    void SyncMarker()
    {
        double la, lo;
        if (map != null && Coords(QrState.Get("glat"), QrState.Get("glon"), out la, out lo)) { map.MarkLat = la; map.MarkLon = lo; map.Invalidate(); }
    }

    void EnsureMap()
    {
        if (map != null) return;
        map = new MapView();
        Controls.Add(map);
        map.Picked += delegate
        {
            double pla = map.MarkLat, plo = map.MarkLon;
            boxes["glat"].Text = pla.ToString("0.####", CultureInfo.InvariantCulture);
            boxes["glon"].Text = plo.ToString("0.####", CultureInfo.InvariantCulture);
            sig = ""; Rebuild(); Invalidate();
        };
        double la, lo;
        Cfg c = Bar.Config;
        if (Coords(QrState.Get("glat"), QrState.Get("glon"), out la, out lo)) map.SetMark(la, lo, true);
        else if (!double.IsNaN(c.Lat) && !double.IsNaN(c.Lon)) map.CenterOn(c.Lat, c.Lon, 9);
    }

    // пошук населеного пункту за назвою: після 3 літер - список адрес, як у налаштуваннях «Місце й погода»
    void Search() { Suggest(true); }

    void Suggest(bool explicitAsk)
    {
        string q = tbSearch.Text.Trim();
        if (q.Length < 3) { note = L.T("Введіть назву міста чи села (від 3 літер)."); Invalidate(); return; }
        sugLast = q;
        if (explicitAsk) { note = L.T("Шукаю…"); Invalidate(); }
        Util.Bg(delegate
        {
            List<string[]> l = Widgets.WeatherSuggest(q);
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (IsDisposed || tbSearch.Text.Trim() != q) return;
                    if (note == L.T("Шукаю…")) note = "";
                    sugItems = l;
                    sug.Items.Clear();
                    foreach (string[] it in l) sug.Items.Add(it[0]);
                    sug.Hover = -1;
                    if (l.Count == 0 && explicitAsk) note = L.T("Нічого не знайдено - перевірте назву або клацніть на карті.");
                    if (l.Count == 1 && explicitAsk) { PickSug(0); return; }
                    sug.Visible = l.Count > 0 && QrState.Kind == "geo";
                    if (sug.Visible) sug.BringToFront();
                    Invalidate();
                }));
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
        });
    }

    void HideSug()
    {
        sugTimer.Stop();
        if (sugItems.Count > 0) sugItems = new List<string[]>();
        if (sug.Visible) { sug.Visible = false; Invalidate(); }
    }

    void PickSug(int i)
    {
        if (i < 0 || i >= sugItems.Count) return;
        string[] it = sugItems[i];
        double la, lo;
        if (!Coords(it[2], it[3], out la, out lo)) return;
        sugLast = it[1];
        tbSearch.Text = it[1];
        boxes["glat"].Text = la.ToString("0.####", CultureInfo.InvariantCulture);
        boxes["glon"].Text = lo.ToString("0.####", CultureInfo.InvariantCulture);
        EnsureMap();
        map.SetMark(la, lo, true);
        note = L.T("Обрано: ") + it[0];
        HideSug();
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (map != null && map.Visible && map.Bounds.Contains(e.Location)) { map.WheelAt(e.Delta, new Point(e.X - map.Left, e.Y - map.Top)); return; }
        base.OnMouseWheel(e);
    }

    protected override void ThemeUpdated()
    {
        base.ThemeUpdated();
        Rebuild();
    }

    // колір теми, але досить темний для сканера (контраст з білим) - перше значення для вибору кольору
    Color ThemeDark()
    {
        Color c = Util.Opaque(P.Acc);
        for (int i = 0; i < 12 && Util.Lum(c) > 80; i++) c = Util.Blend(c, Color.Black, 0.18f);
        return c;
    }

    Color DarkColor()
    {
        if (!QrState.Colored) return Color.Black;
        return QrState.Custom.IsEmpty ? ThemeDark() : Color.FromArgb(255, QrState.Custom);
    }

    // свій колір коду: вибір кольору в стилі теми, спершу - колір теми
    void PickColor()
    {
        Color nc;
        if (!ColorPick.Ask(this, QrState.Custom.IsEmpty ? ThemeDark() : QrState.Custom, out nc)) return;
        QrState.Custom = nc;
        QrState.Colored = true;
        Rebuild();
        Invalidate();
    }

    void Rebuild()
    {
        string pl = QrState.Payload();
        // колір теми - теж у підписі: змінилася тема - код перемальовується в новому кольорі
        string s = pl + "|" + QrState.Ecl + "|" + QrState.Colored + "|" + DarkColor().ToArgb() + "|" + (QrState.Logo != null ? QrState.Logo.GetHashCode() : 0);
        if (s == sig) return;
        sig = s;
        if (preview != null) { preview.Dispose(); preview = null; }
        code = null;
        note = "";
        if (pl.Length == 0) return;
        // з логотипом - найвищий рівень корекції: центр коду закрито картинкою
        code = QrCode.EncodeText(pl, QrState.Logo != null ? 3 : QrState.Ecl, QrState.Logo == null);
        if (code == null) { note = L.T("Забагато даних для одного QR-коду (до ~2900 байтів при рівні L)."); return; }
        previewModule = 0;              // зображення для перегляду - під розмір вікна, у PaintBody
        if (Util.Lum(DarkColor()) > 150) note = L.T("Світлий колір на білому може погано скануватися - краще темніший.");
        if ((QrState.Kind == "tel" && QrState.PhoneOdd(QrState.Get("tnum"))) || (QrState.Kind == "sms" && QrState.PhoneOdd(QrState.Get("snum"))))
            note = L.T("У номері враховуються цифри, + на початку, * і #; дужки, дефіси, крапки й пробіли відкидаються, решта символів - теж.");
    }

    void SetKind(string k)
    {
        QrState.Kind = k;
        foreach (KeyValuePair<string, TextBox> kv in boxes) kv.Value.Visible = false;
        tbSearch.Visible = false;
        if (map != null) map.Visible = false;
        Rebuild();
        FitHeight();
        Invalidate();
    }

    void PickLogo()
    {
        using (OpenFileDialog d = new OpenFileDialog())
        {
            d.Title = L.T("Логотип для QR-коду");
            d.Filter = L.T("Зображення|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico|Усі файли|*.*");
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                Image img;
                using (FileStream fs = new FileStream(d.FileName, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (Image src = Image.FromStream(fs)) img = new Bitmap(src);
                if (QrState.Logo != null) QrState.Logo.Dispose();
                QrState.Logo = img;
            }
            catch (Exception ex) { note = L.T("Не вдалося відкрити картинку: ") + ex.Message; }
        }
        sig = ""; Rebuild(); Invalidate();
    }

    void Copy()
    {
        if (code == null) return;
        try
        {
            using (Bitmap b = QrState.Render(code, Math.Max(8, 600 / (code.Size + 8)), DarkColor())) Clipboard.SetImage(b);
            note = L.T("Скопійовано в буфер обміну.");
        }
        catch (Exception ex) { note = ex.Message; }
        Invalidate();
    }

    void Save()
    {
        if (code == null) return;
        using (SaveFileDialog d = new SaveFileDialog())
        {
            d.Title = L.T("Зберегти QR-код");
            d.Filter = L.T("Зображення PNG|*.png|Векторне SVG|*.svg");
            d.FileName = "qr-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                if (d.FilterIndex == 2 || d.FileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                    File.WriteAllText(d.FileName, QrState.Svg(code, DarkColor()), new UTF8Encoding(false));
                else
                    using (Bitmap b = QrState.Render(code, Math.Max(10, 1000 / (code.Size + 8)), DarkColor())) b.Save(d.FileName, ImageFormat.Png);
                note = L.T("Збережено: ") + Path.GetFileName(d.FileName);
            }
            catch (Exception ex) { note = L.T("Не вдалося зберегти: ") + ex.Message; }
        }
        Invalidate();
    }

    void FromWifi()
    {
        string ssid, pass, auth, err;
        if (WifiInfo.Current(out ssid, out pass, out auth, out err))
        {
            boxes["ssid"].Text = ssid;
            if (pass.Length > 0) boxes["wpass"].Text = pass;
            QrState.WifiAuth = auth;
        }
        note = err;
        sig = ""; Rebuild(); Invalidate();
    }

    void FromHere()
    {
        Cfg c = Bar.Config;
        if (double.IsNaN(c.Lat) || double.IsNaN(c.Lon)) { note = L.T("Місце ще не визначено (задається в налаштуваннях, на мапі)."); Invalidate(); return; }
        boxes["glat"].Text = c.Lat.ToString("0.####", CultureInfo.InvariantCulture);
        boxes["glon"].Text = c.Lon.ToString("0.####", CultureInfo.InvariantCulture);
        EnsureMap();
        map.SetMark(c.Lat, c.Lon, true);
    }

    // геоточка: пошук, карта, дві колонки координат, «Моє місце»
    int GeoUi(Graphics g, Rectangle r, int y)
    {
        TxtL(g, L.T("Знайти місце за назвою"), FS, new Rectangle(r.X, y, r.Width, Px(18)), P.Sub);
        y += Px(19);
        PlaceInput(g, tbSearch, new Rectangle(r.X, y, r.Width - Px(190), Px(34)));
        Btn(g, new Rectangle(r.Right - Px(182), y, Px(84), Px(34)), L.T("Знайти"), Search, 0, true);
        Btn(g, new Rectangle(r.Right - Px(92), y, Px(92), Px(34)), L.T("Моє місце"), FromHere, 0, true);
        y += Px(42);
        EnsureMap();
        // підказки адрес - поверх карти під полем пошуку
        if (sug.Visible && sugItems.Count > 0)
        {
            sug.Font = F; sug.RowH = Px(30);
            sug.Back = P.Field; sug.Fg = P.Fg; sug.Edge = Util.Blend(P.Field, P.Fg, 0.3f); sug.HotBack = Util.Blend(P.Field, P.Acc, 0.5f);
            Rectangle sb = new Rectangle(r.X, y - Px(6), r.Width - Px(190), sug.RowH * sug.Items.Count + 2);
            if (sug.Bounds != sb) sug.Bounds = sb;
        }
        int mh = r.Width;                                       // карта квадратна, 1:1
        Rectangle mr = new Rectangle(r.X, y, r.Width, mh);
        if (map.Bounds != mr) map.Bounds = mr;
        if (!map.Visible) map.Visible = true;
        y += mh + Px(8);
        int hw = (r.Width - Px(8)) / 2;
        TxtL(g, L.T("Широта"), FS, new Rectangle(r.X, y, hw, Px(18)), P.Sub);
        TxtL(g, L.T("Довгота"), FS, new Rectangle(r.X + hw + Px(8), y, hw, Px(18)), P.Sub);
        y += Px(19);
        PlaceInput(g, boxes["glat"], new Rectangle(r.X, y, hw, Px(34)));
        PlaceInput(g, boxes["glon"], new Rectangle(r.X + hw + Px(8), y, hw, Px(34)));
        return y + Px(42);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (preview != null) { preview.Dispose(); preview = null; }
        sugTimer.Stop(); sugTimer.Dispose();
        base.OnFormClosed(e);
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        int y = r.Y;
        // вид даних: два рядки по чотири
        int cw = (r.Width - Px(6) * 3) / 4, ch = Px(30);
        for (int i = 0; i < Kinds.Length; i++)
        {
            string k = Kinds[i][0];
            Chip(g, new Rectangle(r.X + (i % 4) * (cw + Px(6)), y + (i / 4) * (ch + Px(6)), cw, ch), Kinds[i][1], QrState.Kind == k, delegate { SetKind(k); });
        }
        y += ch * 2 + Px(16);

        // поля вибраного виду
        string[][] fl = Fields[QrState.Kind];
        foreach (KeyValuePair<string, TextBox> kv in boxes)
        {
            bool mine = false;
            foreach (string[] f in fl) if (f[0] == kv.Key) mine = true;
            if (!mine && kv.Value.Visible) kv.Value.Visible = false;
        }
        int fh = Px(34);
        if (QrState.Kind != "geo")
        {
            if (tbSearch.Visible) tbSearch.Visible = false;
            if (map != null && map.Visible) map.Visible = false;
            if (sug.Visible) sug.Visible = false;
        }
        if (QrState.Kind == "geo") y = GeoUi(g, r, y);
        else
        foreach (string[] f in fl)
        {
            TxtL(g, f[1], FS, new Rectangle(r.X, y, r.Width, Px(18)), P.Sub);
            y += Px(19);
            bool multi = f[2] == "m";
            int h = multi ? (QrState.Kind == "text" ? Px(96) : Px(64)) : fh;
            if (multi) MultiField(g, boxes[f[0]], new Rectangle(r.X, y, r.Width, h));
            else PlaceInput(g, boxes[f[0]], new Rectangle(r.X, y, r.Width, h));
            y += h + Px(8);
        }
        if (QrState.Kind == "wifi")
        {
            string[][] au = { new[] { "WPA", "WPA/WPA2/WPA3" }, new[] { "WEP", "WEP" }, new[] { "nopass", L.T("Без пароля") } };
            int aw = (r.Width - Px(12)) / 3;
            for (int i = 0; i < au.Length; i++)
            {
                string a = au[i][0];
                Chip(g, new Rectangle(r.X + i * (aw + Px(6)), y, aw, Px(28)), au[i][1], QrState.WifiAuth == a, delegate { QrState.WifiAuth = a; Rebuild(); Invalidate(); });
            }
            y += Px(36);
            Switch(g, new Rectangle(r.X, y, r.Width / 2 - Px(8), Px(28)), L.T("Прихована мережа"), QrState.WifiHidden, delegate { QrState.WifiHidden = !QrState.WifiHidden; Rebuild(); Invalidate(); });
            Btn(g, new Rectangle(r.X + r.Width / 2 + Px(4), y, r.Width / 2 - Px(4), Px(28)), L.T("Поточна мережа"), FromWifi, 0, true);
            y += Px(36);
        }

        // попередній перегляд: решта висоти мінус рядки параметрів і кнопок
        int bottomH = Px(28) + Px(8) + Px(28) + Px(10) + Px(34) + Px(8) + Px(36);
        int side = Math.Max(Px(110), Math.Min(r.Width, r.Bottom - y - bottomH));
        Rectangle pc = new Rectangle(r.X + (r.Width - side) / 2, y, side, side);
        if (code != null)
        {
            // перегляд малюється 1:1 у розмірі вікна (цілий розмір модуля): і модулі чіткі, і логотип не розмитий
            int mod = Math.Max(1, side / (code.Size + 8));
            if (preview == null || previewModule != mod)
            {
                if (preview != null) preview.Dispose();
                preview = QrState.Render(code, mod, DarkColor());
                previewModule = mod;
            }
            int ps = preview.Width;
            pc = new Rectangle(r.X + (r.Width - ps) / 2, y + (side - ps) / 2, ps, ps);
        }
        if (preview != null)
        {
            InterpolationMode im = g.InterpolationMode;
            PixelOffsetMode pm = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using (GraphicsPath gp = Util.Round(pc, Math.Min(P.Rad, Px(10))))
            {
                GraphicsState st = g.Save();
                g.SetClip(gp, CombineMode.Intersect);
                g.DrawImage(preview, pc);
                g.Restore(st);
            }
            g.InterpolationMode = im;
            g.PixelOffsetMode = pm;
        }
        else
        {
            Card(g, pc);
            TxtC(g, note.Length > 0 && code == null && QrState.Payload().Length > 0 ? L.T("Забагато даних") : L.T("Уведіть дані вище"), F, pc, P.Sub);
        }
        y = pc.Bottom + Px(8) + (code != null ? (side - pc.Height) / 2 : 0);
        string info = code != null ? L.T("Версія ") + code.Version + " · " + code.Size + "×" + code.Size + L.T(" · рівень ") + "LMQH"[code.Ecl] + (QrState.Logo == null && code.Ecl > QrState.Ecl ? L.T(" (авто: вміщається без збільшення)") : "") + " · " + code.Bytes + L.T(" з ") + code.Capacity + L.T(" байт") : "";
        TxtC(g, note.Length > 0 ? note : info, FS, new Rectangle(r.X, y, r.Width, Px(28)), note.Length > 0 ? P.Acc : P.Sub);
        y += Px(36);

        // корекція помилок, колір, логотип
        string[] lv = { "L", "M", "Q", "H" };
        int lw = Px(40);
        TxtL(g, L.T("Корекція"), F, new Rectangle(r.X, y, Px(80), Px(28)), P.Fg);
        for (int i = 0; i < 4; i++)
        {
            int e = i;
            bool sel = QrState.Logo != null ? i == 3 : QrState.Ecl == i;
            Chip(g, new Rectangle(r.X + Px(80) + i * (lw + Px(4)), y, lw, Px(28)), lv[i], sel, QrState.Logo != null ? null : (Action)delegate { QrState.Ecl = e; Rebuild(); Invalidate(); });
        }
        int cx = r.X + Px(80) + 4 * (lw + Px(4)) + Px(10), cw2 = (r.Right - cx - Px(6)) / 2;
        Chip(g, new Rectangle(cx, y, cw2, Px(28)), L.T("Чорний"), !QrState.Colored, delegate { QrState.Colored = false; Rebuild(); Invalidate(); });
        Rectangle cc = new Rectangle(cx + cw2 + Px(6), y, cw2, Px(28));
        Chip(g, cc, L.T("Колір…"), QrState.Colored, PickColor);
        // зразок кольору на кнопці
        Color sw = QrState.Custom.IsEmpty ? ThemeDark() : QrState.Custom;
        using (SolidBrush b = new SolidBrush(sw)) g.FillEllipse(b, new Rectangle(cc.X + Px(8), cc.Y + (cc.Height - Px(12)) / 2, Px(12), Px(12)));
        Tip(cc, L.T("Вибрати колір коду (спершу запропоновано колір теми)"));
        y += Px(38);
        int bw = (r.Width - Px(8)) / 2;
        Btn(g, new Rectangle(r.X, y, bw, Px(34)), QrState.Logo == null ? L.T("Логотип у центрі…") : L.T("Інший логотип…"), PickLogo, 0, true);
        Btn(g, new Rectangle(r.X + bw + Px(8), y, bw, Px(34)), L.T("Прибрати логотип"), delegate { if (QrState.Logo != null) { QrState.Logo.Dispose(); QrState.Logo = null; } sig = ""; Rebuild(); Invalidate(); }, 0, QrState.Logo != null);
        y += Px(42);
        Btn(g, new Rectangle(r.X, y, bw, Px(36)), L.T("Копіювати"), Copy, 0, code != null);
        Btn(g, new Rectangle(r.X + bw + Px(8), y, bw, Px(36)), L.T("Зберегти…"), Save, 1, code != null);
    }
}
