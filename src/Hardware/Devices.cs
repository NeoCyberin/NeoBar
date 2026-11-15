using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

// ======================================================================
//  Пристрої й драйвери: перелік через SetupAPI (як у Диспетчері пристроїв, але згрупований, без повторів і службових
//  пристроїв), стан і код проблеми (CfgMgr32), драйвер із реєстру класу. Дії - через pnputil (вбудований засіб
//  Windows, з 10 2004): вимкнути/увімкнути, видалити пристрій, видалити пакет драйвера зі сховища, шукати зміни
//  обладнання. Оновлення - через Windows Update (лише драйвери, які пропонує Microsoft).
//  Безпека: критичні для запуску Windows пристрої (процесор, системні, контролери дисків) не вимикаються й не
//  видаляються; пакети драйверів, вбудовані у Windows, не видаляються - лише сторонні (oem*.inf).
// ======================================================================
sealed class DevItem
{
    public string Id = "", Name = "", Class = "", ClassName = "", Mfg = "", Service = "", HwId = "";
    public string DrvProvider = "", DrvVersion = "", DrvDate = "", Inf = "";
    public bool Present, Hidden, Disableable;
    public int Problem;                 // код проблеми Windows (0 - працює), -1 - пристрій від'єднано
    public bool Disabled { get { return Problem == 22; } }
    public bool Bad { get { return Present && Problem != 0 && Problem != 22; } }
    public bool NoDriver { get { return Present && (Problem == 28 || Problem == 1 || (Inf.Length == 0 && Service.Length == 0 && Problem != 0)); } }
}

sealed class DrvPackage
{
    public string Inf = "", Original = "", Provider = "", Class = "", Version = "", Date = "", StoreDir = "";
    public long Size = -1;
    public int UsedPresent, UsedGhost;
    public bool Old;                    // є новіша версія того самого пакета, а цей не використовує жоден підключений пристрій
}

static class Devices
{
    // ---------- SetupAPI ----------
    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA d);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA d, StringBuilder id, int size, out int req);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA d, int prop, out int type, byte[] buf, int size, out int req);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetClassDescriptionW(ref Guid g, StringBuilder s, int size, out int req);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupGetInfDriverStoreLocationW(string inf, IntPtr alt, string locale, StringBuilder ret, int size, out int req);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_DevNode_Status(out int status, out int problem, int devInst, int flags);
    [DllImport("kernel32.dll")] static extern int GetOEMCP();

    const int DIGCF_PRESENT = 2, DIGCF_ALLCLASSES = 4;
    const int SPDRP_DEVICEDESC = 0, SPDRP_HARDWAREID = 1, SPDRP_SERVICE = 4, SPDRP_CLASS = 7, SPDRP_DRIVER = 9, SPDRP_MFG = 0xB, SPDRP_FRIENDLYNAME = 0xC;
    const int DN_HAS_PROBLEM = 0x400, DN_DISABLEABLE = 0x2000, DN_NO_SHOW_IN_DM = 0x40000000;

    static string Prop(IntPtr set, ref SP_DEVINFO_DATA d, int prop)
    {
        int type, req;
        byte[] buf = new byte[1024];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref d, prop, out type, buf, buf.Length, out req))
        {
            if (req <= buf.Length || req > 65536) return "";
            buf = new byte[req];
            if (!SetupDiGetDeviceRegistryPropertyW(set, ref d, prop, out type, buf, buf.Length, out req)) return "";
        }
        string s = Encoding.Unicode.GetString(buf, 0, Math.Max(0, Math.Min(req, buf.Length)));
        int z = s.IndexOf('\0');
        return (z >= 0 ? s.Substring(0, z) : s).Trim();         // для багаторядкових (ID обладнання) - перший рядок
    }

    static readonly Dictionary<Guid, string> classNames = new Dictionary<Guid, string>();

    static string ClassDesc(Guid g, string fallback)
    {
        string s;
        lock (classNames) if (classNames.TryGetValue(g, out s)) return s;
        StringBuilder b = new StringBuilder(256);
        int req;
        s = SetupDiGetClassDescriptionW(ref g, b, b.Capacity, out req) && b.Length > 0 ? b.ToString() : (fallback.Length > 0 ? fallback : L.T("Інші пристрої"));
        lock (classNames) classNames[g] = s;
        return s;
    }

    // усі пристрої, зокрема від'єднані («привиди» - колись підключені флешки, старі монітори, мережеві адаптери VPN)
    public static List<DevItem> List()
    {
        List<DevItem> l = new List<DevItem>();
        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_ALLCLASSES);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return l;
        try
        {
            SP_DEVINFO_DATA d = new SP_DEVINFO_DATA();
            d.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
            StringBuilder id = new StringBuilder(512);
            using (RegistryKey cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class"))
                for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
                {
                    DevItem x = new DevItem();
                    int req;
                    if (SetupDiGetDeviceInstanceIdW(set, ref d, id, id.Capacity, out req)) x.Id = id.ToString();
                    x.Name = Prop(set, ref d, SPDRP_FRIENDLYNAME);
                    if (x.Name.Length == 0) x.Name = Prop(set, ref d, SPDRP_DEVICEDESC);
                    if (x.Name.Length == 0) x.Name = L.T("Невідомий пристрій");
                    x.Class = Prop(set, ref d, SPDRP_CLASS);
                    x.ClassName = ClassDesc(d.ClassGuid, x.Class);
                    x.Mfg = Prop(set, ref d, SPDRP_MFG);
                    x.Service = Prop(set, ref d, SPDRP_SERVICE);
                    x.HwId = Prop(set, ref d, SPDRP_HARDWAREID);
                    int st, pr;
                    if (CM_Get_DevNode_Status(out st, out pr, d.DevInst, 0) == 0)
                    {
                        x.Present = true;
                        x.Problem = (st & DN_HAS_PROBLEM) != 0 ? pr : 0;
                        x.Hidden = (st & DN_NO_SHOW_IN_DM) != 0;
                        x.Disableable = (st & DN_DISABLEABLE) != 0;
                    }
                    else x.Problem = -1;
                    string drv = Prop(set, ref d, SPDRP_DRIVER);
                    if (drv.Length > 0 && cls != null)
                        try
                        {
                            using (RegistryKey k = cls.OpenSubKey(drv))
                                if (k != null)
                                {
                                    x.DrvProvider = (k.GetValue("ProviderName") as string ?? "").Trim();
                                    x.DrvVersion = (k.GetValue("DriverVersion") as string ?? "").Trim();
                                    x.DrvDate = (k.GetValue("DriverDate") as string ?? "").Trim();
                                    x.Inf = (k.GetValue("InfPath") as string ?? "").Trim();
                                }
                        }
                        catch (Exception skipEx) { SbLog.Skip(skipEx); }
                    l.Add(x);
                }
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return l;
    }

    // ---------- класи ----------
    // що варто бачити першим (решта - за назвою)
    static readonly string[] order = { "Display", "MEDIA", "AudioEndpoint", "Net", "Bluetooth", "Monitor", "DiskDrive", "CDROM", "SCSIAdapter", "HDC", "USB", "Keyboard", "Mouse", "HIDClass", "Camera", "Image", "Printer", "Biometric", "Battery", "Sensor", "SmartCardReader", "Ports", "Processor", "System" };

    public static int Rank(string cls)
    {
        for (int i = 0; i < order.Length; i++) if (string.Equals(order[i], cls, StringComparison.OrdinalIgnoreCase)) return i;
        return order.Length;
    }

    // службові класи (програмні пристрої, томи, системні шини, контролери USB, HID без назви) - показуються лише
    // з «Усі» або коли з ними щось не так
    static readonly HashSet<string> noise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SoftwareDevice", "SoftwareComponent", "Volume", "VolumeSnapshot", "LegacyDriver", "System", "Computer", "UCM", "Firmware",
        "PrintQueue", "Extension", "SecurityDevices", "WSDPrintDevice", "PnpPrinters", "USBFunctionController", "Infrared"
    };

    public static bool Noise(DevItem d)
    {
        if (d.Hidden || noise.Contains(d.Class)) return true;
        string n = d.Name.ToLowerInvariant();
        string id = d.Id.ToUpperInvariant();
        if (id.StartsWith("SWD\\") || id.StartsWith("ROOT\\") && !string.Equals(d.Class, "Net", StringComparison.OrdinalIgnoreCase) && !string.Equals(d.Class, "Display", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(d.Class, "HIDClass", StringComparison.OrdinalIgnoreCase) && (n.Contains("hid-compliant") || n.Contains("hid-сумісн") || n.Contains("vendor-defined") || n.Contains("визначений постачальником"))) return true;
        if (string.Equals(d.Class, "USB", StringComparison.OrdinalIgnoreCase) && (n.Contains("hub") || n.Contains("концентратор") || n.Contains("composite") || n.Contains("складений"))) return true;
        if (string.Equals(d.Class, "Net", StringComparison.OrdinalIgnoreCase) && (n.Contains("wan miniport") || n.Contains("мініпорт wan") || n.Contains("kernel debug"))) return true;
        return false;
    }

    // без цих Windows не запуститься або не побачить диски - не вимикаються й не видаляються (крім від'єднаних)
    static readonly HashSet<string> critical = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "Computer", "Processor", "System", "HDC", "SCSIAdapter", "DiskDrive", "Volume", "VolumeSnapshot", "SoftwareComponent", "LegacyDriver", "Firmware" };

    public static bool Critical(DevItem d) { return critical.Contains(d.Class); }

    public static bool CanDisable(DevItem d) { return d.Present && !Critical(d) && (d.Disableable || d.Disabled); }
    public static bool CanRemove(DevItem d) { return !d.Present || !Critical(d); }
    public static bool CanDeleteDriver(DevItem d) { return !Critical(d) && d.Inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase); }

    // ---------- пояснення коду проблеми ----------
    public static string ProblemText(DevItem d)
    {
        if (!d.Present) return L.T("Від'єднано: колись був підключений, зараз його немає");
        switch (d.Problem)
        {
            case 0: return L.T("Працює");
            case 22: return L.T("Вимкнено");
            case 1: return L.T("Не налаштовано: драйвер не встановлено");
            case 28: return L.T("Драйвер не встановлено");
            case 10: return L.T("Не вдається запустити: драйвер несумісний чи пошкоджений, або пристрій несправний");
            case 18: return L.T("Драйвер треба перевстановити");
            case 19: return L.T("Пошкоджено записи в реєстрі - перевстановіть драйвер");
            case 24: return L.T("Пристрій відсутній або працює неправильно");
            case 31: return L.T("Windows не змогла завантажити драйвер");
            case 37: return L.T("Драйвер повернув помилку під час запуску");
            case 39: return L.T("Драйвер пошкоджено або його бракує");
            case 41: return L.T("Драйвер завантажено, але пристрою не знайдено");
            case 43: return L.T("Windows зупинила пристрій: він повідомив про збій");
            case 45: return L.T("Зараз не підключено");
            case 47: return L.T("Підготовлено до безпечного від'єднання");
            case 48: return L.T("Драйвер заблоковано: він відомий проблемами");
            case 52: return L.T("Підпис драйвера не перевірено");
            default: return L.F("Проблема, код {0}", d.Problem);
        }
    }

    // ---------- дії (pnputil) ----------
    static string PnpUtil { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe"); } }

    // "" - успіх; "!..." - успіх, але потрібне перезавантаження; інакше - текст помилки
    public static string Pnp(string args)
    {
        string exe = PnpUtil;
        if (!File.Exists(exe)) return L.T("Не знайдено pnputil.exe");
        if (!Elevated.IsAdmin)
        {
            string e = SecFix.Run(new List<FixOp> { new FixOp("RUN", exe, args) }, true);
            return e;
        }
        try
        {
            ProcessStartInfo si = new ProcessStartInfo(exe, args);
            si.UseShellExecute = false; si.CreateNoWindow = true;
            si.RedirectStandardOutput = true; si.RedirectStandardError = true;
            try { si.StandardOutputEncoding = Encoding.GetEncoding(GetOEMCP()); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            using (Process p = Process.Start(si))
            {
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                if (!p.WaitForExit(120000)) { try { p.Kill(); } catch (Exception skipEx) { SbLog.Skip(skipEx); } return L.T("pnputil не відповідає"); }
                SbLog.W(SbLog.Panel, "пристрої: pnputil " + args + " -> " + p.ExitCode);
                if (p.ExitCode == 0) return "";
                if (p.ExitCode == 3010) return "!";
                // остання змістовна фраза з відповіді pnputil (мова - як у Windows)
                string last = "";
                foreach (string line in o.Split('\n')) { string s = line.Trim(); if (s.Length > 0) last = s; }
                if (p.ExitCode == 87 && last.Length == 0) return L.T("Цей засіб потребує Windows 10 версії 2004 або новішої");
                return (last.Length > 0 ? last : L.F("код {0}", p.ExitCode));
            }
        }
        catch (Exception ex) { return ex.Message; }
    }

    static string Q(string s) { return "\"" + s.Replace("\"", "") + "\""; }

    public static string Disable(DevItem d) { return Pnp("/disable-device " + Q(d.Id)); }
    public static string Enable(DevItem d) { return Pnp("/enable-device " + Q(d.Id)); }
    public static string Remove(DevItem d) { return Pnp("/remove-device " + Q(d.Id)); }
    public static string Rescan() { return Pnp("/scan-devices"); }

    // пакет драйвера зі сховища; uninstall - спершу прибрати з пристроїв, які ним користуються
    public static string DeleteDriver(string oemInf, bool uninstall)
    {
        if (!oemInf.StartsWith("oem", StringComparison.OrdinalIgnoreCase) || !oemInf.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)) return L.T("Вбудовані у Windows драйвери не видаляються");
        return Pnp("/delete-driver " + Q(oemInf) + (uninstall ? " /uninstall" : ""));
    }

    // вікно властивостей пристрою Windows (вкладки «Драйвер», «Відомості»)
    public static void Properties(DevItem d)
    {
        try { using (Process.Start("rundll32.exe", "devmgr.dll,DeviceProperties_RunDLL /DeviceID " + Q(d.Id))) { } } catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    // ---------- сховище драйверів ----------
    public static List<DrvPackage> Store(List<DevItem> devs)
    {
        List<DrvPackage> l = new List<DrvPackage>();
        string infDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        Dictionary<string, DrvPackage> byInf = new Dictionary<string, DrvPackage>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string f in Directory.GetFiles(infDir, "oem*.inf"))
            {
                DrvPackage p = new DrvPackage();
                p.Inf = Path.GetFileName(f);
                ReadInf(f, p);
                StringBuilder b = new StringBuilder(520);
                int req;
                if (SetupGetInfDriverStoreLocationW(f, IntPtr.Zero, null, b, b.Capacity, out req) && b.Length > 0)
                {
                    p.Original = Path.GetFileName(b.ToString());
                    p.StoreDir = Path.GetDirectoryName(b.ToString());
                }
                if (p.Original.Length == 0) p.Original = p.Inf;
                l.Add(p);
                byInf[p.Inf] = p;
            }
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
        foreach (DevItem d in devs)
        {
            DrvPackage p;
            if (d.Inf.Length > 0 && byInf.TryGetValue(d.Inf, out p)) { if (d.Present) p.UsedPresent++; else p.UsedGhost++; }
        }
        // застарілі: та сама назва пакета, є новіша версія, і цим не користується жоден підключений пристрій
        Dictionary<string, DrvPackage> newest = new Dictionary<string, DrvPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (DrvPackage p in l)
        {
            DrvPackage n;
            if (!newest.TryGetValue(p.Original, out n) || Newer(p, n)) newest[p.Original] = p;
        }
        foreach (DrvPackage p in l) p.Old = p.UsedPresent == 0 && newest[p.Original] != p;
        return l;
    }

    static Version Ver(string s) { Version v; return Version.TryParse(s, out v) ? v : new Version(0, 0); }

    static DateTime Date(string s)
    {
        DateTime t;
        return DateTime.TryParseExact(s, new string[] { "M/d/yyyy", "MM/dd/yyyy", "M-d-yyyy", "MM-dd-yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out t) ? t : DateTime.MinValue;
    }

    static bool Newer(DrvPackage a, DrvPackage b)
    {
        int c = Ver(a.Version).CompareTo(Ver(b.Version));
        if (c != 0) return c > 0;
        return Date(a.Date) > Date(b.Date);
    }

    // [Version]: Provider, Class, DriverVer (значення %...% - з [Strings])
    static void ReadInf(string f, DrvPackage p)
    {
        try
        {
            string[] lines = File.ReadAllLines(f, Encoding.Default);
            Dictionary<string, string> strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> ver = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string sec = "";
            foreach (string raw in lines)
            {
                string s = raw;
                int sc = s.IndexOf(';');
                if (sc >= 0 && s.IndexOf('"') < 0) s = s.Substring(0, sc);
                s = s.Trim();
                if (s.Length == 0) continue;
                if (s.StartsWith("[")) { sec = s.Trim('[', ']', ' ').ToLowerInvariant(); continue; }
                int eq = s.IndexOf('=');
                if (eq <= 0) continue;
                string k = s.Substring(0, eq).Trim(), v = s.Substring(eq + 1).Trim().Trim('"');
                if (sec == "version") ver[k] = v;
                else if (sec == "strings" && !strings.ContainsKey(k)) strings[k] = v;
            }
            string tmp;
            p.Provider = ver.TryGetValue("Provider", out tmp) ? Expand(tmp, strings) : "";
            p.Class = ver.TryGetValue("Class", out tmp) ? Expand(tmp, strings) : "";
            if (ver.TryGetValue("DriverVer", out tmp))
            {
                string[] dv = tmp.Split(',');
                p.Date = dv[0].Trim();
                if (dv.Length > 1) p.Version = dv[1].Trim();
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    static string Expand(string v, Dictionary<string, string> strings)
    {
        if (v.Length > 2 && v.StartsWith("%") && v.EndsWith("%"))
        {
            string r;
            if (strings.TryGetValue(v.Trim('%'), out r)) return r;
        }
        return v;
    }

    public static long DirSize(string dir)
    {
        long n = 0;
        try { foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) try { n += new FileInfo(f).Length; } catch (Exception skipEx) { SbLog.Skip(skipEx); } }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return n;
    }

    // ---------- оновлення з Windows Update ----------
    public sealed class Update
    {
        public string Title = "", Provider = "", Class = "", Date = "", HwId = "", Msg = "";
        public object Raw;
        public long Size;
        public volatile int State, Pct;            // UWait / UDown / UInst / UOk / UFail; відсоток поточного етапу
    }
    public const int UWait = 0, UDown = 1, UInst = 2, UOk = 3, UFail = 4;

    // пошук драйверів, які Windows Update пропонує й ще не встановлено (довго: до хвилини)
    public static List<Update> FindUpdates(out string error)
    {
        error = "";
        List<Update> l = new List<Update>();
        try
        {
            dynamic s = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session"));
            dynamic searcher = s.CreateUpdateSearcher();
            dynamic res = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
            int n = res.Updates.Count;
            for (int i = 0; i < n; i++)
            {
                dynamic u = res.Updates.Item(i);
                Update x = new Update();
                x.Raw = u;
                try { x.Title = (string)u.Title; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                try { x.Provider = (string)u.DriverProvider; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                try { x.Class = (string)u.DriverClass; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                try { x.HwId = (string)u.DriverHardwareID; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                try { x.Date = ((DateTime)u.DriverVerDate).ToString("d"); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                try { x.Size = Convert.ToInt64(u.MaxDownloadSize); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                l.Add(x);
            }
        }
        catch (Exception ex) { error = WuText(ex); SbLog.W(SbLog.Panel, "пристрої: пошук у Windows Update - " + ex.Message); }
        return l;
    }

    // "" - встановлено; "!" - встановлено, потрібне перезавантаження; інакше помилка.
    // Стан кожного драйвера - у його State/Pct/Msg; progress(загальний відсоток) - з фонового потоку.
    // Спершу асинхронно (BeginDownload/BeginInstall - так видно відсотки), якщо Windows не прийме - звичайним викликом.
    public static string Install(List<Update> ups, Action<int> progress)
    {
        if (!Elevated.IsAdmin) return L.T("Встановлення драйверів потребує прав адміністратора: запустіть NeoBar від імені адміністратора");
        foreach (Update u in ups) { u.State = UWait; u.Pct = 0; u.Msg = ""; }
        try
        {
            dynamic s = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session"));
            dynamic coll = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl"));
            foreach (Update u in ups)
            {
                dynamic r = u.Raw;
                try { if (!(bool)r.EulaAccepted) r.AcceptEula(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                coll.Add(r);
            }
            WuCallback cb = new WuCallback();

            // 1) завантаження: 0-50% загального
            dynamic dl = s.CreateUpdateDownloader();
            dl.Updates = coll;
            dynamic dres = null, job = null;
            try { job = dl.BeginDownload(cb, cb, null); } catch (Exception skipEx) { SbLog.Skip(skipEx); job = null; }
            if (job != null)
            {
                while (true)
                {
                    bool done = false;
                    try { done = (bool)job.IsCompleted; } catch (Exception skipEx) { SbLog.Skip(skipEx); done = true; }
                    try
                    {
                        dynamic pr = job.GetProgress();
                        Track(ups, (int)pr.CurrentUpdateIndex, (int)pr.CurrentUpdatePercentComplete, UDown);
                        if (progress != null) progress((int)pr.PercentComplete / 2);
                    }
                    catch (Exception skipEx) { SbLog.Skip(skipEx); }
                    if (done) break;
                    System.Threading.Thread.Sleep(300);
                }
                dres = dl.EndDownload(job);
            }
            else
            {
                foreach (Update u in ups) u.State = UDown;
                if (progress != null) progress(0);
                dres = dl.Download();
            }
            for (int i = 0; i < ups.Count; i++)
            {
                int rc = 4;
                try { rc = (int)dres.GetUpdateResult(i).ResultCode; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                if (rc == 2 || rc == 3) { ups[i].State = UWait; ups[i].Pct = 0; }
                else { ups[i].State = UFail; ups[i].Msg = L.T("не вдалося завантажити"); }
            }
            if (progress != null) progress(50);

            // 2) встановлення: 50-100%
            dynamic inst = s.CreateUpdateInstaller();
            inst.Updates = coll;
            dynamic res = null; job = null;
            try { job = inst.BeginInstall(cb, cb, null); } catch (Exception skipEx) { SbLog.Skip(skipEx); job = null; }
            if (job != null)
            {
                while (true)
                {
                    bool done = false;
                    try { done = (bool)job.IsCompleted; } catch (Exception skipEx) { SbLog.Skip(skipEx); done = true; }
                    try
                    {
                        dynamic pr = job.GetProgress();
                        Track(ups, (int)pr.CurrentUpdateIndex, (int)pr.CurrentUpdatePercentComplete, UInst);
                        if (progress != null) progress(50 + (int)pr.PercentComplete / 2);
                    }
                    catch (Exception skipEx) { SbLog.Skip(skipEx); }
                    if (done) break;
                    System.Threading.Thread.Sleep(300);
                }
                res = inst.EndInstall(job);
            }
            else
            {
                foreach (Update u in ups) if (u.State != UFail) u.State = UInst;
                res = inst.Install();
            }
            int code = (int)res.ResultCode;          // 2 - успішно, 3 - з помилками, 4 - не вдалося
            bool reboot = false;
            try { reboot = (bool)res.RebootRequired; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            int bad = 0;
            for (int i = 0; i < ups.Count; i++)
            {
                Update u = ups[i];
                if (u.State == UFail) { bad++; continue; }
                int rc = 4, hr = 0;
                try { dynamic ur = res.GetUpdateResult(i); rc = (int)ur.ResultCode; hr = (int)ur.HResult; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                u.Pct = 100;
                if (rc == 2 || rc == 3) u.State = UOk;
                else { u.State = UFail; u.Msg = hr != 0 ? WuShort(hr) : L.F("код {0}", rc); bad++; }
            }
            if (progress != null) progress(100);
            SbLog.W(SbLog.Panel, "пристрої: встановлення драйверів з Windows Update - " + code + ", невдалих " + bad + (reboot ? ", треба перезавантажити" : ""));
            if (bad == 0) return reboot ? "!" : "";
            if (bad < ups.Count) return L.T("Частину драйверів не вдалося встановити") + (reboot ? " (" + L.T("потрібне перезавантаження") + ")" : "");
            return L.F("Windows Update повернув код {0}", code);
        }
        catch (Exception ex)
        {
            int hr = Marshal.GetHRForException(ex);
            foreach (Update u in ups) if (u.State != UOk) { u.State = UFail; if (u.Msg.Length == 0) u.Msg = WuShort(hr); }
            SbLog.W(SbLog.Panel, "пристрої: встановлення з Windows Update - " + ex.Message);
            return WuText(ex);
        }
    }

    // Зрозумілий опис помилок Windows Update: {що сталося, що робити}; null - невідомий код
    static string[] WuExplain(int hr)
    {
        switch ((uint)hr)
        {
            case 0x80240438: case 0x80244022: case 0x8024401C: case 0x80072EE2: case 0x80072EFD: case 0x80072EFE: case 0x80072EE7: case 0x8024402C: case 0x8024402F:
                return new[] { L.T("Немає зв'язку із сервером Windows Update"),
                    L.T("Перевірте інтернет. Якщо інтернет є - доступ до серверів Microsoft блокує брандмауер, VPN, проксі, файл hosts або програма, що вимикає оновлення й телеметрію. Спробуйте пізніше або вимкніть таке блокування.") };
            case 0x80072F8F:
                return new[] { L.T("Не вдалося встановити захищене з'єднання з Windows Update"),
                    L.T("Найчастіше на комп'ютері неправильні дата чи час - перевірте їх і повторіть.") };
            case 0x8024002E: case 0x8024500C:
                return new[] { L.T("Windows Update вимкнено політикою"),
                    L.T("Оновлення заблоковано груповою політикою чи реєстром (так роблять програми для вимкнення оновлень). Поверніть доступ до Windows Update і повторіть.") };
            case 0x80070422: case 0x8024001E:
                return new[] { L.T("Служба Windows Update вимкнена"),
                    L.T("Увімкніть службу «Windows Update» (wuauserv): Монітор системи → Завдання → Служби, тип запуску «Вручну».") };
            case 0x80240016:
                return new[] { L.T("Windows Update зараз зайнятий іншим встановленням"),
                    L.T("Дочекайтеся, поки воно закінчиться, або перезавантажте комп'ютер і повторіть.") };
            case 0x80070005:
                return new[] { L.T("Немає доступу"), L.T("Запустіть NeoBar від імені адміністратора.") };
            case 0x80240022:
                return new[] { L.T("Жодне оновлення не встановилося"), L.T("Перезавантажте комп'ютер і повторіть.") };
            case 0x8024000B:
                return new[] { L.T("Операцію скасовано"), "" };
            case 0x80070070: case 0x80240037:
                return new[] { L.T("Бракує місця на диску"), L.T("Звільніть місце на системному диску (віджет «Очищення») і повторіть.") };
        }
        return null;
    }

    static string Hex(int hr) { return "0x" + hr.ToString("X8"); }

    // для одного драйвера в списку: коротко
    static string WuShort(int hr)
    {
        string[] e = WuExplain(hr);
        return e != null ? e[0] : Hex(hr);
    }

    // для повідомлення у вікні: «заголовок \n порада \n код»
    public static string WuText(Exception ex)
    {
        int hr = Marshal.GetHRForException(ex);
        string[] e = WuExplain(hr);
        if (e == null) return ex.Message + "\n\n" + L.T("Код помилки: ") + Hex(hr);
        return e[0] + "\n" + e[1] + "\n" + L.T("Код помилки: ") + Hex(hr);
    }

    // хто вже пройшов етап, хто зараз, хто чекає
    static void Track(List<Update> ups, int cur, int pct, int phase)
    {
        for (int i = 0; i < ups.Count; i++)
        {
            Update u = ups[i];
            if (u.State == UFail || u.State == UOk) continue;
            if (i < cur) { u.State = phase; u.Pct = 100; }
            else if (i == cur) { u.State = phase; u.Pct = Math.Max(0, Math.Min(100, pct)); }
        }
    }
}

// Зворотні виклики Windows Update для BeginDownload/BeginInstall: самі нічого не роблять (стан опитуємо через GetProgress),
// але Windows вимагає об'єкти саме з цими інтерфейсами
[ComImport, Guid("8C3F1CDD-6173-4591-AEBD-A56A53CA77C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IWuDownloadProgressCb { void Invoke(IntPtr job, IntPtr args); }
[ComImport, Guid("77254866-9F5B-4C8E-B9E2-C77A8530D64B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IWuDownloadDoneCb { void Invoke(IntPtr job, IntPtr args); }
[ComImport, Guid("E01402D5-F8DA-43BA-A012-38894BD048F1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IWuInstallProgressCb { void Invoke(IntPtr job, IntPtr args); }
[ComImport, Guid("45F4F6F3-D602-4F98-9A8A-3EFA152AD2D3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IWuInstallDoneCb { void Invoke(IntPtr job, IntPtr args); }

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
sealed class WuCallback : IWuDownloadProgressCb, IWuDownloadDoneCb, IWuInstallProgressCb, IWuInstallDoneCb
{
    void IWuDownloadProgressCb.Invoke(IntPtr job, IntPtr args) { }
    void IWuDownloadDoneCb.Invoke(IntPtr job, IntPtr args) { }
    void IWuInstallProgressCb.Invoke(IntPtr job, IntPtr args) { }
    void IWuInstallDoneCb.Invoke(IntPtr job, IntPtr args) { }
}
