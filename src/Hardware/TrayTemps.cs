using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

// ======================================================================
//  Температури в треї: окремий значок із числом (градуси) обраного кольору для кожного вибраного датчика -
//  процесор, відеокарта (край, hotspot, пам'ять), друга відеокарта, диски. Додаються в налаштуваннях («Система»).
//  Значки малюються лише коли число змінилося; у грі й повноекранній програмі зникають разом з іншими датчиками панелі.
//  Кожен значок має постійний ідентифікатор (номер і GUID, прив'язаний до шляху NeoBar.exe), тож Windows запам'ятовує
//  його як той самий значок: показ на панелі завдань і місце, куди його перетягнули, зберігаються між запусками.
//  Програма сама не може поставити значок на конкретне місце - це вирішує Windows; уперше вони додаються в порядку
//  списку в налаштуваннях, далі порядок задає користувач, перетягуючи їх біля годинника.
// ======================================================================
static class TrayTemps
{
    sealed class Slot
    {
        public string Key = "", Name = "";
        public bool Added, UseGuid = true;
        public Color Col;
        public Icon Ico;
        public string Shown = "", Tip = "";
        public int ColArgb;
        public uint Id;
        public Guid G;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NID
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_GUID = 0x20;
    const int CallbackMsg = 0x8000 + 0x51;
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint msg, ref NID d);

    static readonly List<Slot> slots = new List<Slot>();

    // ---------- датчики, які можна вивести в трей ----------
    public sealed class Src { public string Key = "", Name = "", Dev = ""; public Color Def; }

    static readonly Color CCpu = Color.FromArgb(70, 170, 255), CGpu = Color.FromArgb(90, 200, 110), CHot = Color.FromArgb(255, 150, 60),
                          CMem = Color.FromArgb(190, 120, 255), CGpu2 = Color.FromArgb(70, 200, 200), CDisk = Color.FromArgb(240, 200, 70);

    // що є зараз: процесор і відеокарта - завжди (значок покаже риску, якщо драйвер не віддає датчик), друга відеокарта
    // й диски - якщо є
    public static List<Src> Catalog()
    {
        List<Src> l = new List<Src>();
        l.Add(Mk("cpu", L.T("Процесор"), Sensors.CpuName, CCpu));
        l.Add(Mk("gpu", L.T("Відеокарта"), Sensors.GpuName, CGpu));
        l.Add(Mk("hot", L.T("Відеокарта: hotspot"), Sensors.GpuName, CHot));
        l.Add(Mk("gmem", L.T("Відеокарта: пам'ять"), Sensors.GpuName, CMem));
        if (Sensors.Gpu2Name.Length > 0) l.Add(Mk("gpu2", Sensors.Gpu2Integrated ? L.T("Вбудована відеокарта") : L.T("Друга відеокарта"), Sensors.Gpu2Name, CGpu2));
        foreach (DiskInfo d in DiskTemps.Scan()) l.Add(Mk(DiskTemps.Key(d), L.T("Диск"), d.Model, CDisk));
        return l;
    }

    static Src Mk(string k, string n, string dev, Color c) { Src s = new Src(); s.Key = k; s.Name = n; s.Dev = (dev ?? "").Trim(); s.Def = c; return s; }

    public static float ValueOf(string key)
    {
        switch (key)
        {
            case "cpu": return Sensors.CpuTemp;
            case "gpu": return Sensors.GpuTemp;
            case "hot": return Sensors.Hotspot;
            case "gmem": return Sensors.MemTemp;
            case "gpu2": return Sensors.Gpu2Temp;
        }
        return key.StartsWith("disk:") ? DiskTemps.Value(key) : float.NaN;
    }

    // колір за замовчуванням для датчика (поки користувач не вибрав свій)
    public static Color DefColor(string key)
    {
        switch (key)
        {
            case "cpu": return CCpu;
            case "gpu": return CGpu;
            case "hot": return CHot;
            case "gmem": return CMem;
            case "gpu2": return CGpu2;
        }
        return CDisk;
    }

    // назва датчика для підказки значка (без опитування дисків)
    static string NameOf(string key)
    {
        switch (key)
        {
            case "cpu": return L.T("Процесор");
            case "gpu": return L.T("Відеокарта");
            case "hot": return L.T("Відеокарта: hotspot");
            case "gmem": return L.T("Відеокарта: пам'ять");
            case "gpu2": return Sensors.Gpu2Integrated ? L.T("Вбудована відеокарта") : L.T("Друга відеокарта");
        }
        string m = DiskTemps.Model(key);
        return L.T("Диск") + (m.Length > 0 ? " " + m : "");
    }

    static bool NeedsGpu(string key) { return key == "gpu" || key == "hot" || key == "gmem" || key == "gpu2"; }
    static NeoBar bar;
    static bool hooked, updHooked, running;
    static Wnd wnd;
    static ContextMenuStrip menu;
    static int taskbarCreated;

    // приховане вікно для повідомлень від значків (клік, меню) і перезапуску Провідника
    sealed class Wnd : NativeWindow
    {
        public Wnd()
        {
            CreateHandle(new CreateParams());
            taskbarCreated = (int)Win.RegisterWindowMessage("TaskbarCreated");
            // NeoBar від адміністратора: дозволити повідомлення від Провідника (UIPI)
            try { Win.ChangeWindowMessageFilterEx(Handle, (uint)taskbarCreated, 1, IntPtr.Zero); Win.ChangeWindowMessageFilterEx(Handle, CallbackMsg, 1, IntPtr.Zero); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == CallbackMsg)
            {
                int ev = (int)(m.LParam.ToInt64() & 0xFFFF);
                if (ev == 0x0202) Open();                                   // WM_LBUTTONUP
                else if (ev == 0x0205) ShowMenu();                          // WM_RBUTTONUP
                return;
            }
            if (taskbarCreated != 0 && m.Msg == taskbarCreated)
            {
                // Провідник перезапустився - значки треба додати знову
                foreach (Slot s in slots.ToArray()) if (s.Added) { s.Added = false; Add(s); }
            }
            base.WndProc(ref m);
        }
    }

    static void ShowMenu()
    {
        try
        {
            if (menu == null)
            {
                menu = new ContextMenuStrip();
                ToolStripMenuItem open = new ToolStripMenuItem(L.T("Монітор системи"));
                open.Font = new Font(open.Font, FontStyle.Bold);
                open.Click += delegate { Open(); };
                menu.Items.Add(open);
            }
            Win.SetForegroundWindow(wnd.Handle);
            menu.Show(Cursor.Position);
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    // постійний GUID значка: свій для кожного значка й кожного розташування NeoBar.exe (Windows прив'язує GUID до шляху)
    static Guid GuidOf(string key)
    {
        try
        {
            using (MD5 md = MD5.Create())
            {
                byte[] h = md.ComputeHash(Encoding.UTF8.GetBytes("NeoBar.TrayTemp." + key + "|" + Application.ExecutablePath.ToLowerInvariant()));
                h[7] = (byte)((h[7] & 0x0F) | 0x30);
                h[8] = (byte)((h[8] & 0x3F) | 0x80);
                return new Guid(h);
            }
        }
        catch { return Guid.Empty; }            // MD5 заборонено політикою FIPS - тоді лише постійний номер
    }

    // викликається при кожному перезавантаженні панелі й зміні паузи (гра на весь екран)
    public static void Configure(Cfg c, NeoBar b, bool run)
    {
        bar = b;
        // бажаний набір: ключ і колір у порядку налаштувань
        List<string> keys = new List<string>();
        Dictionary<string, Color> cols = new Dictionary<string, Color>();
        foreach (string it in c.Tray)
        {
            int p = it.IndexOf('|');
            if (p <= 0) continue;
            string k = it.Substring(0, p);
            if (keys.Contains(k)) continue;
            Color col = Util.ParseColor(it.Substring(p + 1));
            keys.Add(k);
            cols[k] = col.IsEmpty ? DefColor(k) : col;
        }
        running = keys.Count > 0 && run;
        bool gpu = false, disk = false;
        foreach (string k in keys) { if (NeedsGpu(k)) gpu = true; if (k.StartsWith("disk:")) disk = true; }
        Sensors.Use("tray", running ? (gpu ? 2 : 1) : 0);
        DiskTemps.Watch(running && disk);
        if (!updHooked) { updHooked = true; Sensors.Updated += OnData; }
        if (!hooked) { hooked = true; Application.ApplicationExit += delegate { RemoveAll(); }; }
        if (running && wnd == null) { try { wnd = new Wnd(); } catch (Exception ex) { Util.ErrorLog(ex); return; } }

        bool same = running && slots.Count == keys.Count;
        if (same) for (int i = 0; i < keys.Count; i++) if (slots[i].Key != keys[i]) same = false;
        if (!same)
        {
            // набір змінився - додаємо всі заново, з кінця: новий значок Windows ставить лівіше, тож зліва направо - як у списку
            RemoveAll();
            slots.Clear();
            if (!running) return;
            HashSet<uint> ids = new HashSet<uint>();
            foreach (string k in keys)
            {
                Slot s = new Slot();
                s.Key = k;
                // постійний номер за ключем (Windows пам'ятає значок і за ним); збіг - наступний вільний
                uint id = 101 + (uint)((k.GetHashCode() & 0x7FFFFFFF) % 4000);
                while (!ids.Add(id)) id++;
                s.Id = id;
                s.G = GuidOf(k);
                s.UseGuid = s.G != Guid.Empty;
                slots.Add(s);
            }
        }
        foreach (Slot s in slots) { s.Col = cols[s.Key]; s.Name = NameOf(s.Key); }
        if (!same) { for (int i = slots.Count - 1; i >= 0; i--) Show(slots[i]); }
        else foreach (Slot s in slots) Show(s);
    }

    static void OnData()
    {
        try
        {
            if (!running || bar == null || bar.IsDisposed) return;
            if (bar.InvokeRequired) bar.BeginInvoke(new Action(Refresh));
            else Refresh();
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    static void Refresh()
    {
        if (!running) return;
        foreach (Slot s in slots.ToArray()) Show(s);
    }

    static NID Data(Slot s, uint flags)
    {
        NID d = new NID();
        d.cbSize = Marshal.SizeOf(typeof(NID));
        d.hWnd = wnd.Handle;
        d.uID = s.Id;
        d.uFlags = flags | (s.UseGuid ? NIF_GUID : 0);
        d.uCallbackMessage = CallbackMsg;
        d.hIcon = s.Ico != null ? s.Ico.Handle : IntPtr.Zero;
        d.szTip = s.Tip.Length > 127 ? s.Tip.Substring(0, 127) : s.Tip;
        d.szInfo = ""; d.szInfoTitle = "";
        d.guidItem = s.UseGuid ? s.G : Guid.Empty;
        return d;
    }

    static void Add(Slot s)
    {
        if (wnd == null || s.Added) return;
        NID d = Data(s, 0);
        Shell_NotifyIcon(NIM_DELETE, ref d);              // залишок після аварійного виходу
        d = Data(s, NIF_MESSAGE | NIF_ICON | NIF_TIP);
        if (!Shell_NotifyIcon(NIM_ADD, ref d) && s.UseGuid)
        {
            // GUID зайнятий іншою копією NeoBar.exe - без нього (значок однаково з постійним номером)
            s.UseGuid = false;
            d = Data(s, NIF_MESSAGE | NIF_ICON | NIF_TIP);
            if (!Shell_NotifyIcon(NIM_ADD, ref d)) return;
        }
        s.Added = true;
    }

    static void Show(Slot s)
    {
        float v = ValueOf(s.Key);
        bool none = float.IsNaN(v);
        string t = none ? "–" : Math.Round(v).ToString();
        int col = s.Col.ToArgb();
        string why = s.Key.StartsWith("disk:") ? L.T("диск не віддає температуру") : NeedsGpu(s.Key) ? VendorGpu.Why() : L.T("драйвер не віддає цей датчик");
        string tip = s.Name + ": " + (none ? (Sensors.Ready ? why : L.T("читаю датчик…")) : t + " °C");
        if (s.Added && s.Shown == t && s.ColArgb == col && s.Tip == tip) return;
        try
        {
            Icon old = s.Ico;
            // немає даних - сіра риска й пояснення в підказці, а не риска кольору датчика
            s.Ico = Make(t, none ? Color.FromArgb(150, 150, 150) : s.Col);
            s.Tip = tip;
            if (!s.Added) Add(s);
            else
            {
                NID d = Data(s, NIF_ICON | NIF_TIP);
                if (!Shell_NotifyIcon(NIM_MODIFY, ref d)) { s.Added = false; Add(s); }
            }
            if (old != null) old.Dispose();
            s.Shown = t;
            s.ColArgb = col;
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    static void Open() { try { if (bar != null) bar.OpenPerf(0); } catch (Exception ex) { Util.ErrorLog(ex); } }

    static void Remove(Slot s)
    {
        try
        {
            if (s.Added && wnd != null)
            {
                NID d = Data(s, 0);
                Shell_NotifyIcon(NIM_DELETE, ref d);
            }
            s.Added = false;
            if (s.Ico != null) { s.Ico.Dispose(); s.Ico = null; }
            s.Shown = "";
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    static void RemoveAll()
    {
        foreach (Slot s in slots.ToArray()) Remove(s);
    }

    // число свого кольору на прозорому тлі, з тонкою темною обводкою, щоб читалося і на світлій, і на темній панелі завдань
    static Icon Make(string tx, Color col)
    {
        int s = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using (Bitmap bmp = new Bitmap(s, s, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                Font f = new Font("Segoe UI", tx.Length >= 3 ? s * 0.58f : s * 0.80f, FontStyle.Bold, GraphicsUnit.Pixel);
                try
                {
                    // не ширше за значок
                    while (f.Size > 5 && g.MeasureString(tx, f, 1000, sf).Width > s)
                    {
                        Font nf = new Font("Segoe UI", f.Size - 1, FontStyle.Bold, GraphicsUnit.Pixel);
                        f.Dispose(); f = nf;
                    }
                    RectangleF r = new RectangleF(0, -s * 0.04f, s, s);
                    float o = s >= 24 ? 1.5f : 1f;
                    using (SolidBrush sh = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
                    {
                        g.DrawString(tx, f, sh, new RectangleF(r.X + o, r.Y, r.Width, r.Height), sf);
                        g.DrawString(tx, f, sh, new RectangleF(r.X - o, r.Y, r.Width, r.Height), sf);
                        g.DrawString(tx, f, sh, new RectangleF(r.X, r.Y + o, r.Width, r.Height), sf);
                        g.DrawString(tx, f, sh, new RectangleF(r.X, r.Y - o, r.Width, r.Height), sf);
                    }
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(255, col.R, col.G, col.B)))
                        g.DrawString(tx, f, b, r, sf);
                }
                finally { f.Dispose(); }
            }
            IntPtr h = bmp.GetHicon();
            try { using (Icon tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone(); }
            finally { Win.DestroyIcon(h); }
        }
    }
}

// Температури дисків для значків у треї: опитуються не щосекунди, а раз на 15 с у фоні (запит до диска - не до пам'яті),
// і лише поки в треї є значок диска
static class DiskTemps
{
    static readonly Dictionary<string, float> vals = new Dictionary<string, float>();
    static readonly Dictionary<string, string> models = new Dictionary<string, string>();
    static System.Threading.Timer timer;
    static int busy;

    public static string Key(DiskInfo d) { return "disk:" + (d.Serial.Trim().Length > 0 ? d.Serial.Trim() : d.Model.Trim()); }

    // фізичні диски з температурою (для вибору в налаштуваннях)
    public static List<DiskInfo> Scan()
    {
        List<DiskInfo> l = new List<DiskInfo>();
        for (int no = 0; no < 16; no++)
        {
            DiskInfo d = Vols.Disk(no);
            if (d.Model.Length == 0 || d.Optical || double.IsNaN(d.TempC)) continue;
            l.Add(d);
            lock (vals) { vals[Key(d)] = (float)d.TempC; models[Key(d)] = d.Model; }
        }
        return l;
    }

    public static float Value(string key) { float v; lock (vals) return vals.TryGetValue(key, out v) ? v : float.NaN; }

    public static string Model(string key) { string m; lock (vals) return models.TryGetValue(key, out m) ? m : ""; }

    public static void Watch(bool on)
    {
        if (on && timer == null) timer = new System.Threading.Timer(delegate { Tick(); }, null, 0, 15000);
        else if (!on && timer != null) { timer.Dispose(); timer = null; }
    }

    static void Tick()
    {
        if (ScreenState.Away) return;      // нікого немає - диски не будимо запитами SMART (жорсткі диски можуть зупинитись)
        if (System.Threading.Interlocked.Exchange(ref busy, 1) == 1) return;
        try
        {
            HashSet<string> seen = new HashSet<string>();
            foreach (DiskInfo d in Scan()) seen.Add(Key(d));
            lock (vals) foreach (string k in new List<string>(vals.Keys)) if (!seen.Contains(k)) vals[k] = float.NaN;    // диск від'єднано
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        finally { System.Threading.Interlocked.Exchange(ref busy, 0); }
    }
}
