using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;

// ======================================================================
//  Характеристики комплектуючих (як у CPU-Z / AIDA64 / HWiNFO): збираються з WMI один раз у фоні
// ======================================================================
sealed class InfoGroup
{
    public string Title;
    public readonly List<string[]> Rows = new List<string[]>();     // {назва, значення}; назва порожня - підзаголовок
    public InfoGroup(string t) { Title = t; }
    public void Add(string k, string v) { if (!string.IsNullOrEmpty(v)) Rows.Add(new[] { k, v }); }
    public void Sub(string t) { Rows.Add(new[] { "", t }); }
}

static class Wmi
{
    // запит до WMI (пізнє зв'язування COM, без System.Management); властивості читаються через Properties_
    public static List<Dictionary<string, object>> Query(string ns, string wql, params string[] props)
    {
        List<Dictionary<string, object>> res = new List<Dictionary<string, object>>();
        try
        {
            Type lt = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (lt == null) return res;
            dynamic loc = Activator.CreateInstance(lt);
            dynamic svc = loc.ConnectServer(".", ns);
            foreach (dynamic o in svc.ExecQuery(wql))
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                foreach (string p in props)
                {
                    object v = null;
                    try { v = o.Properties_.Item(p).Value; } catch { }
                    d[p] = v;
                }
                res.Add(d);
            }
        }
        catch { }
        return res;
    }

    public static string S(Dictionary<string, object> d, string k)
    {
        object v;
        if (!d.TryGetValue(k, out v) || v == null) return "";
        string s = v.ToString().Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        string l = s.ToLowerInvariant();
        // заглушки виробників плат
        if (l == "to be filled by o.e.m." || l == "default string" || l == "none" || l == "n/a" || l == "not specified" ||
            l == "system serial number" || l == "system product name" || l == "0" || l == "unknown" || l == "o.e.m." ||
            l.StartsWith("to be filled") || l == "123456789" || l == "0000000000") return "";
        return s;
    }

    public static long L(Dictionary<string, object> d, string k)
    {
        object v;
        if (!d.TryGetValue(k, out v) || v == null) return 0;
        try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return 0; }
    }

    // 20240131000000.000000+120 -> 31.01.2024
    public static string Date(Dictionary<string, object> d, string k, bool time)
    {
        string s = S(d, k);
        if (s.Length < 8) return "";
        try
        {
            int y = int.Parse(s.Substring(0, 4)), m = int.Parse(s.Substring(4, 2)), dd = int.Parse(s.Substring(6, 2));
            if (time && s.Length >= 12) return new DateTime(y, m, dd, int.Parse(s.Substring(8, 2)), int.Parse(s.Substring(10, 2)), 0).ToString("dd.MM.yyyy HH:mm");
            return new DateTime(y, m, dd).ToString("dd.MM.yyyy");
        }
        catch { return ""; }
    }
}

static class HwInfo
{
    public static List<InfoGroup> Groups = new List<InfoGroup>();
    public static volatile bool Loading, Done;
    static readonly CultureInfo ic = CultureInfo.InvariantCulture;

    public static void Load(bool force)
    {
        if (Loading || (Done && !force)) return;
        Loading = true;
        Util.Bg(delegate
        {
            List<InfoGroup> l = new List<InfoGroup>();
            try { Collect(l); } catch { }
            Groups = l;
            Done = true;
            Loading = false;
        });
    }

    static string Gb(long bytes) { return (bytes / 1073741824.0).ToString(bytes % 1073741824L == 0 ? "0" : "0.0", ic) + L.T(" ГБ"); }

    static string DdrType(long t, long speedClock)
    {
        switch ((int)t)
        {
            case 20: return "DDR";
            case 21: return "DDR2";
            case 24: return "DDR3";
            case 26: return "DDR4";
            case 34: return "DDR5";
            case 35: return "LPDDR5";
            case 30: return "LPDDR4";
        }
        return "";
    }

    static void Collect(List<InfoGroup> l)
    {
        const string cim = @"root\cimv2";

        // ---- процесор ----
        InfoGroup g = new InfoGroup(L.T("Процесор"));
        foreach (Dictionary<string, object> d in Wmi.Query(cim, "SELECT * FROM Win32_Processor", "Name", "Manufacturer", "NumberOfCores", "NumberOfLogicalProcessors", "MaxClockSpeed", "CurrentClockSpeed",
            "SocketDesignation", "L2CacheSize", "L3CacheSize", "Description", "ProcessorId", "VirtualizationFirmwareEnabled", "ExtClock", "Revision", "Family"))
        {
            g.Add(L.T("Назва"), Wmi.S(d, "Name"));
            g.Add(L.T("Виробник"), Wmi.S(d, "Manufacturer"));
            g.Add(L.T("Ядра / потоки"), Wmi.L(d, "NumberOfCores") + " / " + Wmi.L(d, "NumberOfLogicalProcessors"));
            g.Add(L.T("Базова частота"), Wmi.L(d, "MaxClockSpeed") > 0 ? (Wmi.L(d, "MaxClockSpeed") / 1000.0).ToString("0.00", ic) + L.T(" ГГц") : "");
            g.Add(L.T("Роз'єм (сокет)"), Wmi.S(d, "SocketDesignation"));
            long l2 = Wmi.L(d, "L2CacheSize"), l3 = Wmi.L(d, "L3CacheSize");
            g.Add(L.T("Кеш L2"), l2 > 0 ? (l2 >= 1024 ? (l2 / 1024.0).ToString("0.#", ic) + L.T(" МБ") : l2 + L.T(" КБ")) : "");
            g.Add(L.T("Кеш L3"), l3 > 0 ? (l3 >= 1024 ? (l3 / 1024.0).ToString("0.#", ic) + L.T(" МБ") : l3 + L.T(" КБ")) : "");
            g.Add(L.T("Сімейство / модель"), Wmi.S(d, "Description"));
            g.Add(L.T("Ідентифікатор (CPUID)"), Wmi.S(d, "ProcessorId"));
            if (d["VirtualizationFirmwareEnabled"] != null) g.Add(L.T("Віртуалізація"), Convert.ToBoolean(d["VirtualizationFirmwareEnabled"]) ? L.T("увімкнена") : L.T("вимкнена"));
            break;
        }
        if (g.Rows.Count > 0) l.Add(g);

        // ---- материнська плата, BIOS, виробник ПК ----
        g = new InfoGroup(L.T("Материнська плата і BIOS"));
        foreach (Dictionary<string, object> d in Wmi.Query(cim, "SELECT * FROM Win32_BaseBoard", "Manufacturer", "Product", "Version", "SerialNumber"))
        {
            g.Add(L.T("Плата"), (Wmi.S(d, "Manufacturer") + " " + Wmi.S(d, "Product")).Trim());
            g.Add(L.T("Версія плати"), Wmi.S(d, "Version"));
            g.Add(L.T("Серійний номер плати"), Wmi.S(d, "SerialNumber"));
            break;
        }
        foreach (Dictionary<string, object> d in Wmi.Query(cim, "SELECT * FROM Win32_ComputerSystem", "Manufacturer", "Model", "SystemSKUNumber"))
        {
            string mm = (Wmi.S(d, "Manufacturer") + " " + Wmi.S(d, "Model")).Trim();
            g.Add(L.T("Система (ПК)"), mm);
            g.Add("SKU", Wmi.S(d, "SystemSKUNumber"));
            break;
        }
        foreach (Dictionary<string, object> d in Wmi.Query(cim, "SELECT * FROM Win32_BIOS", "Manufacturer", "SMBIOSBIOSVersion", "ReleaseDate", "SerialNumber", "SMBIOSMajorVersion", "SMBIOSMinorVersion"))
        {
            g.Add("BIOS", (Wmi.S(d, "Manufacturer") + " " + Wmi.S(d, "SMBIOSBIOSVersion")).Trim());
            g.Add(L.T("Дата BIOS"), Wmi.Date(d, "ReleaseDate", false));
            long smM = Wmi.L(d, "SMBIOSMajorVersion");
            if (smM > 0) g.Add("SMBIOS", smM + "." + Wmi.L(d, "SMBIOSMinorVersion"));
            break;
        }
        foreach (Dictionary<string, object> d in Wmi.Query(cim, "SELECT * FROM Win32_ComputerSystemProduct", "UUID"))
        {
            g.Add(L.T("UUID системи"), Wmi.S(d, "UUID"));
            break;
        }
        if (g.Rows.Count > 0) l.Add(g);

        // ---- пам'ять ----
        g = new InfoGroup(L.T("Оперативна пам'ять"));
        long slots = 0;
        foreach (Dictionary<string, object> d in Wmi.Query(cim, "SELECT * FROM Win32_PhysicalMemoryArray", "MemoryDevices", "MaxCapacity"))
        {
            slots += Wmi.L(d, "MemoryDevices");
        }
        long total = 0;
        List<Dictionary<string, object>> mods = Wmi.Query(cim, "SELECT * FROM Win32_PhysicalMemory", "BankLabel", "DeviceLocator", "Capacity", "Speed", "ConfiguredClockSpeed",
            "SMBIOSMemoryType", "Manufacturer", "PartNumber", "SerialNumber", "FormFactor", "ConfiguredVoltage");
        foreach (Dictionary<string, object> d in mods) total += Wmi.L(d, "Capacity");
        if (mods.Count > 0)
        {
            g.Add(L.T("Усього"), Gb(total) + L.T(" · модулів: ") + mods.Count + (slots > 0 ? L.T(" з ") + slots + L.T(" слотів") : ""));
            foreach (Dictionary<string, object> d in mods)
            {
                string loc = Wmi.S(d, "DeviceLocator");
                if (loc.Length == 0) loc = Wmi.S(d, "BankLabel");
                g.Sub(loc.Length > 0 ? loc : L.T("Модуль"));
                long speed = Wmi.L(d, "ConfiguredClockSpeed") > 0 ? Wmi.L(d, "ConfiguredClockSpeed") : Wmi.L(d, "Speed");
                long ff = Wmi.L(d, "FormFactor");
                string type = DdrType(Wmi.L(d, "SMBIOSMemoryType"), speed);
                g.Add(L.T("Обсяг і тип"), (Gb(Wmi.L(d, "Capacity")) + " " + type + (ff == 12 ? " SO-DIMM" : (ff == 8 ? " DIMM" : ""))).Trim());
                g.Add(L.T("Швидкість"), speed > 0 ? speed + L.T(" МТ/с") : "");
                g.Add(L.T("Виробник"), Wmi.S(d, "Manufacturer"));
                g.Add(L.T("Артикул (Part number)"), Wmi.S(d, "PartNumber"));
                g.Add(L.T("Серійний номер"), Wmi.S(d, "SerialNumber"));
                long mv = Wmi.L(d, "ConfiguredVoltage");
                g.Add(L.T("Напруга"), mv > 0 ? (mv / 1000.0).ToString("0.00", ic) + L.T(" В") : "");
            }
            l.Add(g);
        }

        // ---- відеокарти ----
        g = new InfoGroup(L.T("Відеоадаптери"));
        int gi = 0;
        foreach (Dictionary<string, object> d in Wmi.Query(cim, "SELECT * FROM Win32_VideoController", "Name", "AdapterRAM", "DriverVersion", "DriverDate", "PNPDeviceID",
            "VideoProcessor", "CurrentHorizontalResolution", "CurrentVerticalResolution", "CurrentRefreshRate", "VideoModeDescription"))
        {
            gi++;
            g.Sub(Wmi.S(d, "Name"));
            long ram = Wmi.L(d, "AdapterRAM");
            // WMI віддає лише 32 біти: для великих карт беремо реальний обсяг із датчиків
            if (Sensors.VramTotalGb > 0 && Wmi.S(d, "Name").Length > 0 && Sensors.GpuName.Length > 0 && Wmi.S(d, "Name").IndexOf(Sensors.GpuName, StringComparison.OrdinalIgnoreCase) >= 0)
                g.Add(L.T("Відеопам'ять"), Sensors.VramTotalGb.ToString("0", ic) + L.T(" ГБ"));
            else if (ram > 0 && ram < 4294967000L) g.Add(L.T("Відеопам'ять"), Gb(ram));
            g.Add(L.T("Процесор GPU"), Wmi.S(d, "VideoProcessor"));
            g.Add(L.T("Драйвер"), Wmi.S(d, "DriverVersion") + (Wmi.Date(d, "DriverDate", false).Length > 0 ? L.T(" від ") + Wmi.Date(d, "DriverDate", false) : ""));
            string pnp = Wmi.S(d, "PNPDeviceID");
            g.Add(L.T("Ідентифікатор"), PciIds(pnp));
            long hr = Wmi.L(d, "CurrentHorizontalResolution"), vr = Wmi.L(d, "CurrentVerticalResolution");
            if (hr > 0) g.Add(L.T("Поточний режим"), hr + "×" + vr + (Wmi.L(d, "CurrentRefreshRate") > 0 ? " · " + Wmi.L(d, "CurrentRefreshRate") + L.T(" Гц") : ""));
        }
        if (gi > 0) l.Add(g);

        // ---- накопичувачі ----
        g = new InfoGroup(L.T("Накопичувачі"));
        Dictionary<string, string> kinds = new Dictionary<string, string>();
        foreach (Dictionary<string, object> d in Wmi.Query(@"root\Microsoft\Windows\Storage", "SELECT FriendlyName, MediaType, BusType, SpindleSpeed, HealthStatus FROM MSFT_PhysicalDisk",
            "FriendlyName", "MediaType", "BusType", "SpindleSpeed", "HealthStatus"))
        {
            string bus = "";
            switch ((int)Wmi.L(d, "BusType"))
            {
                case 17: bus = "NVMe"; break;
                case 11: bus = "SATA"; break;
                case 7: bus = "USB"; break;
                case 8: bus = "RAID"; break;
                case 3: bus = "ATA"; break;
                case 10: bus = "SAS"; break;
            }
            string media = "";
            switch ((int)Wmi.L(d, "MediaType")) { case 3: media = "HDD"; break; case 4: media = "SSD"; break; case 5: media = "SCM"; break; }
            string hs = "";
            switch ((int)Wmi.L(d, "HealthStatus")) { case 0: hs = L.T("справний"); break; case 1: hs = L.T("є попередження"); break; case 2: hs = L.T("несправний"); break; }
            string kd = (media + " " + bus).Trim();
            if (Wmi.L(d, "SpindleSpeed") > 1 && Wmi.L(d, "SpindleSpeed") < 100000) kd += " · " + Wmi.L(d, "SpindleSpeed") + L.T(" об/хв");
            if (hs.Length > 0) kd += " · " + hs;
            kinds[Wmi.S(d, "FriendlyName")] = kd;
        }
        foreach (Dictionary<string, object> d in Wmi.Query(cim, "SELECT * FROM Win32_DiskDrive", "Model", "Size", "InterfaceType", "SerialNumber", "FirmwareRevision", "Partitions", "MediaType"))
        {
            string model = Wmi.S(d, "Model");
            g.Sub(model);
            string kd;
            if (kinds.TryGetValue(model, out kd) && kd.Length > 0) g.Add(L.T("Тип"), kd);
            else g.Add(L.T("Інтерфейс"), Wmi.S(d, "InterfaceType"));
            long size = Wmi.L(d, "Size");
            g.Add(L.T("Ємність"), size > 0 ? (size >= 1000000000000L ? (size / 1e12).ToString("0.##", ic) + L.T(" ТБ") : Math.Round(size / 1e9) + L.T(" ГБ")) + " (" + Gb(size) + L.T(" у двійкових)") : "");
            g.Add(L.T("Прошивка"), Wmi.S(d, "FirmwareRevision"));
            g.Add(L.T("Серійний номер"), Wmi.S(d, "SerialNumber"));
            if (Wmi.L(d, "Partitions") > 0) g.Add(L.T("Розділів"), Wmi.L(d, "Partitions").ToString());
        }
        if (g.Rows.Count > 0) l.Add(g);

        // ---- мережеві адаптери (без WMI) ----
        g = new InfoGroup(L.T("Мережеві адаптери"));
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet && ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.GigabitEthernet) continue;
                string desc = ni.Description;
                if (desc.IndexOf("virtual", StringComparison.OrdinalIgnoreCase) >= 0 || desc.IndexOf("vethernet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    desc.IndexOf("vmware", StringComparison.OrdinalIgnoreCase) >= 0 || desc.IndexOf("virtualbox", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    desc.IndexOf("loopback", StringComparison.OrdinalIgnoreCase) >= 0 || desc.IndexOf("tap-", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                g.Sub(desc);
                string mac = ni.GetPhysicalAddress().ToString();
                if (mac.Length == 12) { StringBuilder sb = new StringBuilder(); for (int i = 0; i < 12; i += 2) { if (i > 0) sb.Append(':'); sb.Append(mac, i, 2); } mac = sb.ToString(); }
                g.Add(L.T("MAC-адреса"), mac);
                g.Add(L.T("Тип"), ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet");
                g.Add(L.T("Стан"), ni.OperationalStatus == OperationalStatus.Up ? L.T("підключено") + (ni.Speed > 0 ? " · " + (ni.Speed >= 1000000000L ? (ni.Speed / 1e9).ToString("0.##", ic) + L.T(" Гбіт/с") : Math.Round(ni.Speed / 1e6) + L.T(" Мбіт/с")) : "") : L.T("не підключено"));
            }
        }
        catch { }
        if (g.Rows.Count > 0) l.Add(g);

        // ---- Windows ----
        g = new InfoGroup("Windows");
        foreach (Dictionary<string, object> d in Wmi.Query(cim, "SELECT * FROM Win32_OperatingSystem", "Caption", "Version", "BuildNumber", "OSArchitecture", "InstallDate", "LastBootUpTime", "RegisteredUser"))
        {
            g.Add(L.T("Система"), Wmi.S(d, "Caption") + " " + Wmi.S(d, "OSArchitecture"));
            g.Add(L.T("Версія"), Wmi.S(d, "Version") + (Wmi.S(d, "BuildNumber").Length > 0 ? L.T(" (збірка ") + Wmi.S(d, "BuildNumber") + ")" : ""));
            g.Add(L.T("Встановлено"), Wmi.Date(d, "InstallDate", false));
            g.Add(L.T("Останнє завантаження"), Wmi.Date(d, "LastBootUpTime", true));
            break;
        }
        g.Add(L.T("Ім'я комп'ютера"), Environment.MachineName);
        if (g.Rows.Count > 0) l.Add(g);
    }

    // PCI\VEN_1002&DEV_744C&SUBSYS_E4231002&REV_CC -> "VEN 1002 · DEV 744C · SUBSYS E4231002 · REV CC"
    static string PciIds(string pnp)
    {
        if (string.IsNullOrEmpty(pnp) || !pnp.StartsWith("PCI", StringComparison.OrdinalIgnoreCase)) return pnp ?? "";
        List<string> r = new List<string>();
        foreach (string part in pnp.Split('\\', '&'))
        {
            int us = part.IndexOf('_');
            if (us < 0) continue;
            string k = part.Substring(0, us).ToUpperInvariant();
            if (k == "VEN" || k == "DEV" || k == "SUBSYS" || k == "REV") r.Add(k + " " + part.Substring(us + 1));
        }
        return string.Join(" · ", r.ToArray());
    }

    // один блок (Процесор, Материнська плата…) для копіювання
    public static string GroupText(InfoGroup g)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(g.Title);
        foreach (string[] r in g.Rows) sb.AppendLine(r[0].Length == 0 ? "-- " + r[1] : r[0] + ": " + r[1]);
        return sb.ToString().TrimEnd();
    }

    public static string AsText()
    {
        StringBuilder sb = new StringBuilder();
        foreach (InfoGroup g in Groups)
        {
            sb.AppendLine("== " + g.Title + " ==");
            foreach (string[] r in g.Rows) sb.AppendLine(r[0].Length == 0 ? "-- " + r[1] : r[0] + ": " + r[1]);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
