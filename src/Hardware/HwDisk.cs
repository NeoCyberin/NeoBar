using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

// ======================================================================
//  Залізо без WMI: батарея (IOCTL), томи й диски, безпечне вилучення,
//  підвищений помічник (SMART, літера диска, перевірка, оптимізація)
// ======================================================================
static class Io
{
    public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    public const uint SHARE_RW = 3, OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);


    public static SafeFileHandle Open(string path, uint access)
    {
        SafeFileHandle h = CreateFile(path, access, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return null; }
        return h;
    }

    public static byte[] Ioctl(SafeFileHandle h, uint code, byte[] input, int outSize)
    {
        byte[] o = new byte[outSize];
        int ret;
        if (!Win.DeviceIoControl(h, code, input, input == null ? 0 : input.Length, o, outSize, out ret, IntPtr.Zero)) return null;
        if (ret < outSize) Array.Resize(ref o, ret);
        return o;
    }

    public static uint U32(byte[] b, int o) { return o + 4 <= b.Length ? BitConverter.ToUInt32(b, o) : 0; }
    public static int I32(byte[] b, int o) { return o + 4 <= b.Length ? BitConverter.ToInt32(b, o) : 0; }
    public static ulong U64(byte[] b, int o) { return o + 8 <= b.Length ? BitConverter.ToUInt64(b, o) : 0; }

    public static string Ansi(byte[] b, int o)
    {
        if (o <= 0 || o >= b.Length) return "";
        int e = o;
        while (e < b.Length && b[e] != 0) e++;
        return Encoding.ASCII.GetString(b, o, e - o).Trim();
    }

    public static string Wide(byte[] b)
    {
        if (b == null) return "";
        string s = Encoding.Unicode.GetString(b);
        int z = s.IndexOf('\0');
        return (z >= 0 ? s.Substring(0, z) : s).Trim();
    }

    // ---------- SetupAPI: шляхи пристроїв за класом інтерфейсу ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid Guid; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, uint idx, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, ref SP_DEVINFO_DATA info);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    // повертає пари (шлях пристрою, DevInst)
    public static List<KeyValuePair<string, uint>> Interfaces(Guid g)
    {
        List<KeyValuePair<string, uint>> res = new List<KeyValuePair<string, uint>>();
        IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x2 | 0x10);   // PRESENT | DEVICEINTERFACE
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return res;
        try
        {
            for (uint i = 0; i < 64; i++)
            {
                SP_DEVICE_INTERFACE_DATA d = new SP_DEVICE_INTERFACE_DATA();
                d.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) break;
                SP_DEVINFO_DATA info = new SP_DEVINFO_DATA();
                info.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                int req;
                SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out req, ref info);
                if (req <= 0) continue;
                IntPtr buf = Marshal.AllocHGlobal(req);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);   // cbSize SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out req, ref info))
                        res.Add(new KeyValuePair<string, uint>(Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4)), info.DevInst));
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return res;
    }
}

// ----------------------------------------------------------------------
//  Батарея
// ----------------------------------------------------------------------
sealed class BatInfo
{
    public bool Present;
    public int Pct = -1;
    public bool OnAc, Charging, Discharging, Critical, Relative;
    public string Chem = "", Name = "", Maker = "", Serial = "", Made = "";
    public uint Design, Full, Remain, VoltMv;
    public int RateMw = int.MinValue, Cycles = -1, TimeLeftMin = -1;
    public double TempC = double.NaN;

    public double Wear { get { return Design > 0 && Full > 0 ? Math.Max(0, 100.0 - Full * 100.0 / Design) : double.NaN; } }

    // Висновок про стан: 0 - гаразд, 1 - зношена, 2 - кінець ресурсу / не працює; Title порожній - сказати нічого
    public int HealthLevel;
    public string HealthTitle = "", HealthText = "";

    public void Judge()
    {
        HealthLevel = 0; HealthTitle = HealthText = "";
        if (!Present) return;
        string repl = Name.Length > 0 ? L.T(" Модель для заміни: ") + Name + "." : "";
        string v = (VoltMv / 1000.0).ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');
        // будь-яка літієва батарея ноутбука - це 1-4 елементи по 3,0-4,35 В; менше ніж 3 В на всю батарею - глибокий розряд
        if (VoltMv > 0 && VoltMv < 3000)
        {
            HealthLevel = 2; HealthTitle = L.T("Батарея непридатна");
            HealthText = L.T("Напруга всієї батареї ") + v + L.T(" В - нижче за мінімум навіть одного елемента (3 В). Батарея глибоко розряджена, ") +
                         L.T("і її контролер, найімовірніше, заблокував заряджання. Ноутбук працює лише від мережі; потрібна заміна.") + repl;
            return;
        }
        if (OnAc && !Charging && Pct >= 0 && Pct <= 3)
        {
            HealthLevel = 2; HealthTitle = L.T("Батарея не заряджається");
            HealthText = L.T("Від мережі, але заряд ") + Pct + L.T("% і не зростає") + (Full == 0 ? L.T(", а контролер не повідомляє ємність") : "") +
                         L.T(". Найімовірніше, батарея вичерпала ресурс або її відключив контролер. Якщо не допоможе вимкнути ноутбук і ") +
                         L.T("заряджати його вимкненим годину-дві - потрібна заміна.") + repl;
            return;
        }
        if (Design > 0 && Full == 0 && !Relative)
        {
            HealthLevel = 2; HealthTitle = L.T("Батарея не тримає заряд");
            HealthText = L.T("Контролер повідомляє нульову повну ємність - батарея вичерпала ресурс.") + repl;
            return;
        }
        double w = Wear;
        if (double.IsNaN(w)) return;
        double health = 100 - w;
        if (health <= 40)
        {
            HealthLevel = 2; HealthTitle = L.T("Кінець ресурсу");
            HealthText = L.T("Лишилося ") + Math.Round(health) + L.T("% від заводської ємності: батарея помітно сідає швидше й може раптово вимикати ноутбук. Варто замінити.") + repl;
        }
        else if (health <= 70)
        {
            HealthLevel = 1; HealthTitle = L.T("Помітно зношена");
            HealthText = L.T("Лишилося ") + Math.Round(health) + L.T("% від заводської ємності.");
        }
    }

    public string ChemText
    {
        get
        {
            switch ((Chem ?? "").ToUpperInvariant())
            {
                case "LION": case "LI-I": case "LIP": return Chem.ToUpperInvariant() == "LIP" ? L.T("літій-полімерна (Li-Po)") : L.T("літій-іонна (Li-ion)");
                case "PBAC": return L.T("свинцево-кислотна");
                case "NIMH": return L.T("нікель-металогідридна (NiMH)");
                case "NICD": return L.T("нікель-кадмієва (NiCd)");
                case "NIZN": return L.T("нікель-цинкова");
                case "RAM": return L.T("лужна акумуляторна");
                case "": return L.T("невідомо");
            }
            return Chem;
        }
    }
}

static class Battery
{
    static readonly Guid BatGuid = new Guid("72631E54-78A4-11D0-BCF7-00AA00B7B32A");
    const uint QUERY_TAG = 0x294040, QUERY_INFO = 0x294044, QUERY_STATUS = 0x29404C;

    public static bool Present()
    {
        try
        {
            System.Windows.Forms.PowerStatus ps = System.Windows.Forms.SystemInformation.PowerStatus;
            return ps.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.NoSystemBattery &&
                   ps.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.Unknown;
        }
        catch { return false; }
    }

    public static BatInfo Read()
    {
        BatInfo b = new BatInfo();
        try
        {
            System.Windows.Forms.PowerStatus ps = System.Windows.Forms.SystemInformation.PowerStatus;
            b.Present = Present();
            b.Pct = b.Present ? (int)Math.Round(ps.BatteryLifePercent * 100) : -1;
            b.OnAc = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
            if (ps.BatteryLifeRemaining > 0) b.TimeLeftMin = ps.BatteryLifeRemaining / 60;
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        if (!b.Present) return b;
        try
        {
            foreach (KeyValuePair<string, uint> dev in Io.Interfaces(BatGuid))
            {
                using (Microsoft.Win32.SafeHandles.SafeFileHandle h = Io.Open(dev.Key, Io.GENERIC_READ | Io.GENERIC_WRITE))
                {
                    if (h == null) continue;
                    byte[] tagB = Io.Ioctl(h, QUERY_TAG, BitConverter.GetBytes(0u), 4);
                    if (tagB == null || tagB.Length < 4) continue;
                    uint tag = BitConverter.ToUInt32(tagB, 0);
                    if (tag == 0) continue;

                    byte[] info = Query(h, tag, 0, 32);
                    if (info != null && info.Length >= 32)
                    {
                        uint caps = Io.U32(info, 0);
                        b.Relative = (caps & 0x40000000) != 0;
                        b.Chem = Encoding.ASCII.GetString(info, 8, 4).Trim('\0', ' ');
                        b.Design = Io.U32(info, 12);
                        b.Full = Io.U32(info, 16);
                        uint cyc = Io.U32(info, 28);
                        b.Cycles = cyc > 0 && cyc < 100000 ? (int)cyc : -1;
                    }
                    byte[] t = Query(h, tag, 2, 4);
                    if (t != null && t.Length >= 4)
                    {
                        uint dk = BitConverter.ToUInt32(t, 0);
                        if (dk > 2000 && dk < 4000) b.TempC = dk / 10.0 - 273.15;
                    }
                    b.Name = Io.Wide(Query(h, tag, 4, 256));
                    b.Maker = Io.Wide(Query(h, tag, 6, 256));
                    b.Serial = Io.Wide(Query(h, tag, 8, 256));
                    byte[] md = Query(h, tag, 5, 4);
                    if (md != null && md.Length >= 4)
                    {
                        int day = md[0], mon = md[1], yr = BitConverter.ToUInt16(md, 2);
                        if (yr > 1990 && mon >= 1 && mon <= 12 && day >= 1 && day <= 31) b.Made = new DateTime(yr, mon, day).ToString("d MMMM yyyy", Widgets.UK);
                    }

                    byte[] ws = new byte[20];
                    Array.Copy(BitConverter.GetBytes(tag), 0, ws, 0, 4);
                    byte[] st = Io.Ioctl(h, QUERY_STATUS, ws, 16);
                    if (st != null && st.Length >= 16)
                    {
                        uint pw = Io.U32(st, 0);
                        b.OnAc = (pw & 1) != 0;
                        b.Discharging = (pw & 2) != 0;
                        b.Charging = (pw & 4) != 0;
                        b.Critical = (pw & 8) != 0;
                        uint cap = Io.U32(st, 4), volt = Io.U32(st, 8);
                        int rate = Io.I32(st, 12);
                        if (cap != 0xFFFFFFFF) b.Remain = cap;
                        if (volt != 0xFFFFFFFF) b.VoltMv = volt;
                        if (rate != unchecked((int)0x80000000)) b.RateMw = rate;
                    }
                    break;    // перша батарея
                }
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        b.Judge();
        return b;
    }

    static byte[] Query(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint tag, int level, int outSize)
    {
        byte[] q = new byte[12];
        Array.Copy(BitConverter.GetBytes(tag), 0, q, 0, 4);
        Array.Copy(BitConverter.GetBytes(level), 0, q, 4, 4);
        return Io.Ioctl(h, QUERY_INFO, q, outSize);
    }
}

// ----------------------------------------------------------------------
//  Томи (розділи з літерами) і фізичні диски
// ----------------------------------------------------------------------
sealed class Vol
{
    public string Guid = "";     // \\?\Volume{...}\  - незмінний ідентифікатор тому
    public string Root = "";     // D:\  (порожньо - без літери)
    public string Label = "", Fs = "";
    public long Total, Free;
    public int DriveType;        // 2 знімний, 3 фіксований, 4 мережа, 5 CD
    public int DiskNo = -1;

    public string Letter { get { return Root.Length >= 2 ? Root.Substring(0, 2).ToUpperInvariant() : ""; } }
    public string Id { get { return Guid.Length > 0 ? Guid : Root; } }

    public string Title
    {
        get
        {
            string l = Label.Length > 0 ? Label : (DriveType == 2 ? L.T("Знімний диск") : (DriveType == 5 ? L.T("Оптичний привід") : L.T("Локальний диск")));
            return (Letter.Length > 0 ? Letter + " " : "") + l;
        }
    }

    public int UsedPct { get { return Total > 0 ? (int)Math.Round(100.0 - Free * 100.0 / Total) : -1; } }
}

sealed class DiskInfo
{
    public int No = -1;
    public string Model = "", Vendor = "", Serial = "", Rev = "", Bus = "";
    public int BusType;
    public bool Removable, Ssd, SsdKnown;
    public bool Optical;                 // оптичний привід (CD/DVD/Blu-ray)
    public long Size;
    public double TempC = double.NaN;

    public bool Usb { get { return BusType == 7; } }

    public string Kind
    {
        get
        {
            if (Optical) return L.T("Оптичний привід");
            if (BusType == 17) return "NVMe SSD";
            if (BusType == 7) return Removable ? L.T("USB-флешка") : L.T("USB-диск");
            if (BusType == 12 || BusType == 13) return L.T("Карта пам'яті");
            if (BusType == 14 || BusType == 15) return L.T("Віртуальний диск");
            if (SsdKnown) return Ssd ? "SSD" : "HDD";
            return L.T("Диск");
        }
    }
}

static class Vols
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr FindFirstVolume(StringBuilder name, int len);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool FindNextVolume(IntPtr h, StringBuilder name, int len);
    [DllImport("kernel32.dll")]
    static extern bool FindVolumeClose(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumePathNamesForVolumeName(string vol, char[] buf, int len, out int ret);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetDiskFreeSpaceEx(string root, out long freeAvail, out long total, out long totalFree);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetDriveType(string root);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeNameForVolumeMountPoint(string mount, StringBuilder name, int len);
    [DllImport("kernel32.dll")]
    static extern uint SetErrorMode(uint mode);

    const uint GET_DEVICE_NUMBER = 0x2D1080, QUERY_PROPERTY = 0x2D1400, GEOMETRY_EX = 0x700A0;

    // корінь системного диска (C:\)
    public static string SysRoot()
    {
        try
        {
            string r = Path.GetPathRoot(Environment.SystemDirectory);
            if (!string.IsNullOrEmpty(r)) return r;
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return "C:\\";
    }

    public static List<Vol> List()
    {
        List<Vol> res = new List<Vol>();
        uint oldMode = 0;
        try { oldMode = SetErrorMode(1); } catch (Exception skipEx) { SbLog.Skip(skipEx); }   // без вікон «Вставте диск»
        try
        {
            StringBuilder sb = new StringBuilder(260);
            IntPtr h = FindFirstVolume(sb, sb.Capacity);
            if (h == new IntPtr(-1) || h == IntPtr.Zero) return Fallback();
            try
            {
                do
                {
                    Vol v = Make(sb.ToString());
                    if (v != null) res.Add(v);
                    sb.Length = 0;
                } while (FindNextVolume(h, sb, sb.Capacity));
            }
            finally { FindVolumeClose(h); }
        }
        catch { return Fallback(); }
        finally { try { SetErrorMode(oldMode); } catch (Exception skipEx) { SbLog.Skip(skipEx); } }
        res.Sort(delegate (Vol a, Vol b)
        {
            if (a.Letter.Length == 0 != (b.Letter.Length == 0)) return a.Letter.Length == 0 ? 1 : -1;
            return string.Compare(a.Letter, b.Letter, StringComparison.Ordinal);
        });
        return res;
    }

    static Vol Make(string guid)
    {
        char[] buf = new char[1024];
        int ret;
        string root = "";
        if (GetVolumePathNamesForVolumeName(guid, buf, buf.Length, out ret))
        {
            string all = new string(buf, 0, Math.Max(0, ret));
            foreach (string p in all.Split('\0'))
                if (p.Length == 3 && p[1] == ':') { root = p; break; }
        }
        if (root.Length == 0) return null;     // томи без літери (відновлення, EFI) не показуємо
        Vol v = new Vol();
        v.Guid = guid;
        v.Root = root;
        Fill(v);
        return v;
    }

    static void Fill(Vol v)
    {
        string r = v.Root.Length > 0 ? v.Root : v.Guid;
        try { v.DriveType = GetDriveType(r); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        try
        {
            StringBuilder lab = new StringBuilder(261), fs = new StringBuilder(261);
            uint ser, mc, fl;
            if (Win.GetVolumeInformation(r, lab, lab.Capacity, out ser, out mc, out fl, fs, fs.Capacity))
            {
                v.Label = lab.ToString();
                v.Fs = fs.ToString();
            }
            long fa, tot, tf;
            if (GetDiskFreeSpaceEx(r, out fa, out tot, out tf)) { v.Total = tot; v.Free = fa; }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        // оптичний привід і мережевий диск - не фізичні диски: номер пристрою CD-ROM збігся б із PhysicalDrive (показувало б чужий SSD)
        v.DiskNo = v.DriveType == 5 || v.DriveType == 4 ? -1 : DiskNumber(v);
    }

    static List<Vol> Fallback()
    {
        List<Vol> res = new List<Vol>();
        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            try
            {
                Vol v = new Vol();
                v.Root = d.Name;
                v.DriveType = (int)d.DriveType;
                if (d.IsReady) { v.Label = d.VolumeLabel; v.Fs = d.DriveFormat; v.Total = d.TotalSize; v.Free = d.AvailableFreeSpace; }
                res.Add(v);
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
        return res;
    }

    public static string GuidOf(string root)
    {
        try
        {
            StringBuilder sb = new StringBuilder(64);
            if (GetVolumeNameForVolumeMountPoint(root.EndsWith("\\") ? root : root + "\\", sb, sb.Capacity)) return sb.ToString();
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return "";
    }

    // Пошук тому за збереженим id (GUID або літера); null - не підключений
    public static Vol Find(string id)
    {
        if (string.IsNullOrEmpty(id)) id = Vols.SysRoot();
        foreach (Vol v in List())
            if (string.Equals(v.Guid, id, StringComparison.OrdinalIgnoreCase) || string.Equals(v.Root, id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v.Letter, id.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return v;
        return null;
    }

    // Швидке оновлення місця для віджета (без перебору всіх томів)
    public static Vol Quick(string id, string lastRoot)
    {
        try
        {
            if (string.IsNullOrEmpty(id)) id = Vols.SysRoot();
            // літера (C:\) - одразу цей том, без перебору всіх
            if (id.Length >= 2 && id.Length <= 3 && id[1] == ':')
            {
                string root = id.Substring(0, 2).ToUpperInvariant() + "\\";
                if (GetDriveType(root) <= 1) return null;
                Vol lv = new Vol(); lv.Root = root; lv.Guid = GuidOf(root);
                Fill(lv);
                return lv;
            }
            if (!string.IsNullOrEmpty(lastRoot) && id.StartsWith("\\\\?\\", StringComparison.Ordinal) &&
                string.Equals(GuidOf(lastRoot), id, StringComparison.OrdinalIgnoreCase))
            {
                Vol v = new Vol(); v.Guid = id; v.Root = lastRoot;
                long fa, tot, tf;
                if (GetDiskFreeSpaceEx(lastRoot, out fa, out tot, out tf)) { v.Total = tot; v.Free = fa; }
                StringBuilder lab = new StringBuilder(261), fs = new StringBuilder(261);
                uint ser, mc, fl;
                if (Win.GetVolumeInformation(lastRoot, lab, lab.Capacity, out ser, out mc, out fl, fs, fs.Capacity)) { v.Label = lab.ToString(); v.Fs = fs.ToString(); }
                v.DriveType = GetDriveType(lastRoot);
                return v;
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        // GUID без відомої літери - лише цей том
        try { if (id.StartsWith("\\\\?\\", StringComparison.Ordinal)) return Make(id); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return Find(id);
    }

    static int DiskNumber(Vol v)
    {
        try
        {
            string path = v.Letter.Length > 0 ? "\\\\.\\" + v.Letter : v.Guid.TrimEnd('\\');
            using (SafeFileHandle h = Io.Open(path, 0))
            {
                if (h == null) return -1;
                byte[] o = Io.Ioctl(h, GET_DEVICE_NUMBER, null, 12);
                if (o == null || o.Length < 8) return -1;
                return BitConverter.ToInt32(o, 4);
            }
        }
        catch { return -1; }
    }

    static byte[] Prop(SafeFileHandle h, int id, int outSize)
    {
        byte[] q = new byte[12];
        Array.Copy(BitConverter.GetBytes(id), 0, q, 0, 4);
        return Io.Ioctl(h, QUERY_PROPERTY, q, outSize);
    }

    // оптичний привід за літерою: модель і інтерфейс (стан, швидкість і обслуговування для нього не мають сенсу)
    public static DiskInfo Optical(string root)
    {
        DiskInfo d = new DiskInfo();
        d.Optical = true;
        try
        {
            using (SafeFileHandle h = Io.Open("\\\\.\\" + root.TrimEnd('\\'), 0))
            {
                if (h == null) return d;
                byte[] o = Prop(h, 0, 1024);
                if (o != null && o.Length >= 32)
                {
                    d.Removable = true;
                    d.Vendor = Io.Ansi(o, (int)Io.U32(o, 12));
                    d.Model = Io.Ansi(o, (int)Io.U32(o, 16));
                    d.Rev = Io.Ansi(o, (int)Io.U32(o, 20));
                    d.BusType = (int)Io.U32(o, 28);
                    d.Bus = BusName(d.BusType);
                    if (d.Vendor.Length > 0 && !d.Model.StartsWith(d.Vendor, StringComparison.OrdinalIgnoreCase)) d.Model = d.Vendor + " " + d.Model;
                }
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return d;
    }

    public static DiskInfo Disk(int no)
    {
        DiskInfo d = new DiskInfo();
        d.No = no;
        if (no < 0) return d;
        try
        {
            using (SafeFileHandle h = Io.Open("\\\\.\\PhysicalDrive" + no, 0))
            {
                if (h == null) return d;
                byte[] o = Prop(h, 0, 1024);
                if (o != null && o.Length >= 32)
                {
                    d.Removable = o[10] != 0;
                    d.Vendor = Io.Ansi(o, (int)Io.U32(o, 12));
                    d.Model = Io.Ansi(o, (int)Io.U32(o, 16));
                    d.Rev = Io.Ansi(o, (int)Io.U32(o, 20));
                    d.Serial = Io.Ansi(o, (int)Io.U32(o, 24));
                    d.BusType = (int)Io.U32(o, 28);
                    d.Bus = BusName(d.BusType);
                    if (d.Vendor.Length > 0 && !d.Model.StartsWith(d.Vendor, StringComparison.OrdinalIgnoreCase)) d.Model = d.Vendor + " " + d.Model;
                }
                byte[] sp = Prop(h, 7, 12);
                if (sp != null && sp.Length >= 9) { d.SsdKnown = true; d.Ssd = sp[8] == 0; }
                byte[] tp = Prop(h, 52, 64);     // StorageDeviceTemperatureProperty
                if (tp != null && tp.Length >= 28 && BitConverter.ToUInt16(tp, 12) > 0)
                {
                    short tc = BitConverter.ToInt16(tp, 26);
                    if (tc > -40 && tc < 150) d.TempC = tc;
                }
                byte[] ge = Io.Ioctl(h, GEOMETRY_EX, null, 256);
                if (ge != null && ge.Length >= 32) d.Size = BitConverter.ToInt64(ge, 24);
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return d;
    }

    static string BusName(int b)
    {
        switch (b)
        {
            case 1: return "SCSI"; case 2: return "ATAPI"; case 3: return "ATA"; case 4: return "IEEE 1394";
            case 7: return "USB"; case 8: return "RAID"; case 9: return "iSCSI"; case 10: return "SAS"; case 11: return "SATA";
            case 12: return "SD"; case 13: return "MMC"; case 14: case 15: return L.T("віртуальна"); case 16: return "Storage Spaces";
            case 17: return "NVMe"; case 18: return "SCM"; case 19: return "UFS";
        }
        return "—";
    }

    public static string Size(long bytes)
    {
        double g = bytes / 1073741824.0;
        if (g >= 1000) return (g / 1024.0).ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',') + L.T(" ТБ");
        if (g >= 10) return g.ToString("0", CultureInfo.InvariantCulture) + L.T(" ГБ");
        if (g >= 1) return g.ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',') + L.T(" ГБ");
        return (bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + L.T(" МБ");
    }

    // ---------- безпечне вилучення ----------
    static readonly Guid DiskIface = new Guid("53F56307-B6BF-11D0-94F2-00A0C91EFB8B");

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Parent(out uint parent, uint devInst, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Request_Device_Eject(uint devInst, out int vetoType, StringBuilder vetoName, int len, int flags);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_DevNode_Registry_PropertyW(uint devInst, uint prop, out uint type, byte[] buf, ref uint len, int flags);

    const uint DN_REMOVABLE = 0x00004000, CM_DRP_CAPABILITIES = 0x10, CM_DEVCAP_REMOVABLE = 0x4;

    static uint DevInstOf(int diskNo)
    {
        foreach (KeyValuePair<string, uint> d in Io.Interfaces(DiskIface))
        {
            using (SafeFileHandle h = Io.Open(d.Key, 0))
            {
                if (h == null) continue;
                byte[] o = Io.Ioctl(h, GET_DEVICE_NUMBER, null, 12);
                if (o != null && o.Length >= 8 && BitConverter.ToInt32(o, 4) == diskNo) return d.Value;
            }
        }
        return 0;
    }

    static bool IsRemovableNode(uint inst)
    {
        try
        {
            uint st, pr;
            if (CM_Get_DevNode_Status(out st, out pr, inst, 0) == 0 && (st & DN_REMOVABLE) != 0) return true;
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        try
        {
            uint type, len = 4;
            byte[] b = new byte[4];
            if (CM_Get_DevNode_Registry_PropertyW(inst, CM_DRP_CAPABILITIES, out type, b, ref len, 0) == 0 && (BitConverter.ToUInt32(b, 0) & CM_DEVCAP_REMOVABLE) != 0) return true;
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return false;
    }

    // найближчий пристрій, який Windows дозволяє вилучити (сам диск, USB-накопичувач, NVMe у порту з гарячим підключенням)
    static uint EjectTarget(int diskNo)
    {
        uint inst = DevInstOf(diskNo);
        if (inst == 0) return 0;
        // спершу батьківські пристрої (USB-накопичувач, контролер NVMe), щоб не зачепити зовнішній USB-хаб вище
        uint node;
        if (CM_Get_Parent(out node, inst, 0) == 0)
            for (int i = 0; i < 6 && node != 0; i++)
            {
                if (IsRemovableNode(node)) return node;
                uint parent;
                if (CM_Get_Parent(out parent, node, 0) != 0) break;
                node = parent;
            }
        return IsRemovableNode(inst) ? inst : 0;
    }

    static readonly Dictionary<int, bool> ejectable = new Dictionary<int, bool>();

    // чи можна безпечно вилучити диск (як у значку «Безпечне вилучення» Windows); системний - ні
    public static bool CanEject(int diskNo)
    {
        if (diskNo < 0) return false;
        lock (ejectable)
        {
            bool c;
            if (ejectable.TryGetValue(diskNo, out c)) return c;
        }
        bool res = false;
        try
        {
            Vol sys = null;
            foreach (Vol v in List()) if (string.Equals(v.Root, SysRoot(), StringComparison.OrdinalIgnoreCase)) sys = v;
            if (sys == null || sys.DiskNo != diskNo) res = EjectTarget(diskNo) != 0;
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        lock (ejectable) ejectable[diskNo] = res;
        return res;
    }

    public static void ForgetEjectable() { lock (ejectable) ejectable.Clear(); }

    // повертає "" при успіху, інакше пояснення
    public static string Eject(int diskNo)
    {
        try
        {
            uint target = EjectTarget(diskNo);
            if (target == 0)
            {
                uint inst = DevInstOf(diskNo);
                if (inst == 0) return L.T("Пристрій не знайдено.");
                uint parent;
                target = CM_Get_Parent(out parent, inst, 0) == 0 ? parent : inst;
            }
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int veto;
                StringBuilder vn = new StringBuilder(260);
                int r = CM_Request_Device_Eject(target, out veto, vn, vn.Capacity, 0);
                if (r == 0 && veto == 0) { ForgetEjectable(); return ""; }
                if (attempt == 2)
                    return veto != 0 ? L.T("Windows не дозволяє вилучити пристрій: його використовує програма або відкритий файл.") : L.T("Помилка вилучення (код ") + r + ").";
                Thread.Sleep(400);
            }
        }
        catch (Exception ex) { return ex.Message; }
        return L.T("Не вдалося вилучити.");
    }

    // флешка чи карта пам'яті (а не диск): знімний том, USB-накопичувач або кардридер
    public static bool IsFlash(Vol v, DiskInfo di)
    {
        if (v != null && v.DriveType == 2) return true;
        if (di == null) return false;
        return (di.BusType == 7 && di.Removable) || di.BusType == 12 || di.BusType == 13;
    }
}

// ----------------------------------------------------------------------
//  Права адміністратора та підвищений помічник (той самий NeoBar.exe з ключем --helper)
// ----------------------------------------------------------------------
static class Elevated
{
    public static bool IsAdmin
    {
        get
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }
    }

    // Запускає помічника з правами адміністратора. Вивід пишеться у файл (повертається шлях).
    public static string Run(string op, string args, out string error)
    {
        error = "";
        string outFile = Path.Combine(Path.GetTempPath(), "neobar-" + op + "-" + DateTime.Now.Ticks + ".txt");
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(System.Windows.Forms.Application.ExecutablePath,
                "--helper " + op + " \"" + outFile + "\" " + args);
            psi.UseShellExecute = true;
            if (!IsAdmin) psi.Verb = "runas";
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            using (Process.Start(psi)) { }
            return outFile;
        }
        catch (Win32Exception ex)
        {
            error = ex.NativeErrorCode == 1223 ? L.T("Скасовано (немає дозволу адміністратора).") : ex.Message;
            return null;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    // Читає вивід помічника (файл може ще дописуватися)
    public static string ReadOut(string file, out bool done)
    {
        done = false;
        try
        {
            if (file == null || !File.Exists(file)) return "";
            using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
            {
                string s = sr.ReadToEnd();
                int k = s.IndexOf("<<END", StringComparison.Ordinal);
                if (k >= 0) { done = true; s = s.Substring(0, k); }
                return s;
            }
        }
        catch { return ""; }
    }

    // Перезапуск усієї панелі від імені адміністратора (для FPS у іграх)
    public static bool RestartAsAdmin()
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(System.Windows.Forms.Application.ExecutablePath, "--restarted");
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            using (Process.Start(psi)) { }
            return true;
        }
        catch { return false; }
    }
}

// контрольна сума файлу завдання для підвищеного помічника: файл у %TEMP% не можна тихо змінити між створенням і запуском
static class ElevHash
{
    public static string Of(byte[] b)
    {
        using (System.Security.Cryptography.SHA256 h = new System.Security.Cryptography.SHA256Cng())
        {
            StringBuilder sb = new StringBuilder();
            foreach (byte x in h.ComputeHash(b)) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }

    public static bool Same(byte[] b, string hex)
    {
        return hex != null && hex.Length == 64 && string.Equals(Of(b), hex, StringComparison.OrdinalIgnoreCase);
    }
}

static class Helper
{
    // Точка входу помічника: --helper op outFile args...
    public static int Run(string[] a)
    {
        if (a.Length < 3) return 1;
        string op = a[1], outFile = a[2];
        StreamWriter w = null;
        try
        {
            FileStream fs = new FileStream(outFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            w = new StreamWriter(fs, new UTF8Encoding(false));
            w.AutoFlush = true;
            int code = 0;
            switch (op)
            {
                case "smart": code = Smart.Report(int.Parse(a[3]), w); break;
                case "letter": code = SetLetter(a[3], a[4], w); break;
                case "chkdsk": code = Tool("chkdsk.exe", a[3] + (a.Length > 4 && a[4] == "ntfs" ? " /scan" : ""), w); break;
                // HDD - дефрагментація, SSD/флешки - лише TRIM (/L)
                case "defrag": code = Tool("defrag.exe", a[3] + (a.Length > 4 && a[4] == "trim" ? " /L /U /V" : " /O /U /V"), w); break;
                case "ntp": code = SyncTime(w); break;
                case "format": code = DiskFormat.Run(a[3], a[4], a[5], a.Length > 6 ? a[6] : "0", a.Length > 7 ? a[7] : "", w, outFile + ".stop"); break;
                case "secfix": code = a.Length > 4 ? SecFix.RunFile(a[3], a[4], w) : 1; break;
                case "wifi": code = WifiList.Run(w); break;
                case "wifiset": code = a.Length > 4 ? WifiList.RunFile(a[3], a[4], w) : 1; break;
                case "fw": code = a.Length > 4 ? Fw.RunFile(a[3], a[4], w) : 1; break;
                case "fwlist": code = Fw.Dump(w); break;
                case "ipcfg": code = a.Length > 3 ? NetTools.IpCfg(a[3], w) : 1; break;
                case "netcfg": code = a.Length > 4 ? NetCfg.RunFile(a[3], a[4], w) : 1; break;
                case "uninstall": code = Uninstall.Elevate(w); break;
                case "relock": code = AppLock.Apply("", w) ? 0 : 1; if (code == 0) AppLock.InstallLauncher(); break;
                case "unlock": code = AppLock.Remove(w) ? 0 : 1; break;
                case "task": code = AdminTask.Configure(a[3] == "on", a.Length > 4 && a[4] == "auto", a.Length > 5 ? a[5] : "", w); break;
                default: w.WriteLine(L.T("Невідома дія.")); code = 1; break;
            }
            w.WriteLine();
            w.Write("<<END " + code + ">>");
            return code;
        }
        catch (Exception ex)
        {
            try { if (w != null) { w.WriteLine(ex.Message); w.Write("<<END 1>>"); } } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            return 1;
        }
        finally { if (w != null) w.Dispose(); }
    }

    static int SyncTime(StreamWriter w)
    {
        string msg;
        bool ok = TimeSync.Run(out msg);
        w.WriteLine(char.ToUpper(msg[0]) + msg.Substring(1));
        return ok ? 0 : 1;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool DeleteVolumeMountPoint(string mount);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetVolumeMountPoint(string mount, string vol);

    static int SetLetter(string guid, string letter, StreamWriter w)
    {
        letter = letter.TrimEnd(':', '\\').ToUpperInvariant();
        if (!guid.EndsWith("\\")) guid += "\\";
        if (letter.Length != 1) { w.WriteLine(L.T("Неправильна літера.")); return 1; }
        string target = letter + ":\\";
        if (Directory.Exists(target)) { w.WriteLine(L.T("Літера ") + letter + L.T(": уже зайнята.")); return 1; }
        foreach (Vol v in Vols.List())
            if (string.Equals(v.Guid, guid, StringComparison.OrdinalIgnoreCase) && v.Root.Length > 0)
            {
                if (!DeleteVolumeMountPoint(v.Root)) { w.WriteLine(L.T("Не вдалося зняти стару літеру (код ") + Marshal.GetLastWin32Error() + ")."); return 1; }
                break;
            }
        if (!SetVolumeMountPoint(target, guid)) { w.WriteLine(L.T("Не вдалося призначити літеру (код ") + Marshal.GetLastWin32Error() + ")."); return 1; }
        w.WriteLine(L.T("Готово: тепер це диск ") + letter + L.T(":. Windows запам'ятає літеру для цього тому."));
        return 0;
    }

    // Запуск консольної утиліти з перехопленням виводу (рядок за рядком, з оновленням прогресу)
    static int Tool(string exe, string args, StreamWriter w)
    {
        ProcessStartInfo psi = new ProcessStartInfo(exe, args);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        try { psi.StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        using (Process p = Process.Start(psi))
        {
            p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) lock (w) w.WriteLine(e.Data); };
            p.BeginErrorReadLine();
            StringBuilder line = new StringBuilder();
            int ch;
            while ((ch = p.StandardOutput.Read()) >= 0)
            {
                if (ch == '\r' || ch == '\n')
                {
                    if (line.Length > 0)
                    {
                        string l = line.ToString();
                        lock (w) w.WriteLine(ch == '\r' ? "\u21BB" + l : l);   // ↻ - рядок прогресу, що перезаписується
                        line.Length = 0;
                    }
                    continue;
                }
                line.Append((char)ch);
            }
            if (line.Length > 0) lock (w) w.WriteLine(line.ToString());
            p.WaitForExit();
            return p.ExitCode;
        }
    }
}

// ----------------------------------------------------------------------
//  SMART / стан накопичувача (працює з правами адміністратора)
// ----------------------------------------------------------------------
static class Smart
{
    const uint QUERY_PROPERTY = 0x2D1400, SMART_RCV = 0x7C088, PREDICT = 0x2D1100;

    public static int Report(int disk, StreamWriter w)
    {
        DiskInfo di = Vols.Disk(disk);
        w.WriteLine("model=" + di.Model);
        using (SafeFileHandle h = Io.Open("\\\\.\\PhysicalDrive" + disk, Io.GENERIC_READ | Io.GENERIC_WRITE))
        {
            if (h == null) { w.WriteLine(L.T("error=Не вдалося відкрити диск (код ") + Marshal.GetLastWin32Error() + ")."); return 1; }
            if (di.BusType == 17 && Nvme(h, w)) return 0;
            if (Ata(h, disk, w)) return 0;
            if (Nvme(h, w)) return 0;
            byte[] pf = Io.Ioctl(h, PREDICT, null, 516);
            if (pf != null && pf.Length >= 4)
            {
                w.WriteLine("kind=basic");
                w.WriteLine("health=" + (Io.U32(pf, 0) != 0 ? "fail" : "ok"));
                return 0;
            }
            w.WriteLine("kind=none");
            w.WriteLine(L.T("error=Пристрій не віддає SMART (часто так у USB-флешок і деяких USB-боксів)."));
            return 0;
        }
    }

    static bool Nvme(SafeFileHandle h, StreamWriter w)
    {
        // STORAGE_PROPERTY_QUERY(StorageDeviceProtocolSpecificProperty) + STORAGE_PROTOCOL_SPECIFIC_DATA + 512 байт журналу
        byte[] q = new byte[8 + 40 + 512];
        BitConverter.GetBytes(50).CopyTo(q, 0);      // PropertyId
        BitConverter.GetBytes(0).CopyTo(q, 4);       // PropertyStandardQuery
        BitConverter.GetBytes(3).CopyTo(q, 8);       // ProtocolTypeNvme
        BitConverter.GetBytes(2).CopyTo(q, 12);      // NVMeDataTypeLogPage
        BitConverter.GetBytes(2).CopyTo(q, 16);      // журнал SMART/Health
        BitConverter.GetBytes(0).CopyTo(q, 20);
        BitConverter.GetBytes(40).CopyTo(q, 24);     // ProtocolDataOffset
        BitConverter.GetBytes(512).CopyTo(q, 28);    // ProtocolDataLength
        byte[] o = Io.Ioctl(h, QUERY_PROPERTY, q, q.Length);
        if (o == null || o.Length < 48 + 200) return false;
        int off = 8 + (int)Io.U32(o, 8 + 16);
        if (off <= 8 || off + 192 > o.Length) off = 48;
        byte cw = o[off];
        int tempK = BitConverter.ToUInt16(o, off + 1);
        w.WriteLine("kind=nvme");
        w.WriteLine("health=" + (cw != 0 ? "fail" : (o[off + 5] >= 90 ? "warn" : "ok")));
        if (cw != 0) w.WriteLine(L.T("warning=Критичне попередження контролера (біти 0x") + cw.ToString("X2") + ")");
        if (tempK > 200) w.WriteLine("temp=" + (tempK - 273));
        w.WriteLine("spare=" + o[off + 3]);
        w.WriteLine("used=" + o[off + 5]);
        w.WriteLine("read=" + (Io.U64(o, off + 32) * 512000UL));
        w.WriteLine("written=" + (Io.U64(o, off + 48) * 512000UL));
        w.WriteLine("cycles=" + Io.U64(o, off + 112));
        w.WriteLine("poh=" + Io.U64(o, off + 128));
        w.WriteLine("unsafe=" + Io.U64(o, off + 144));
        w.WriteLine("mediaerr=" + Io.U64(o, off + 160));
        return true;
    }

    static readonly Dictionary<int, string> Names = new Dictionary<int, string>
    {
        { 1, L.T("Помилки читання") }, { 3, L.T("Час розкрутки") }, { 4, L.T("Запуски/зупинки") }, { 5, L.T("Перепризначені сектори") },
        { 7, L.T("Помилки позиціонування") }, { 9, L.T("Відпрацьовано годин") }, { 10, L.T("Повтори розкрутки") }, { 12, L.T("Цикли живлення") },
        { 170, L.T("Резервні блоки") }, { 171, L.T("Помилки запису") }, { 172, L.T("Помилки стирання") }, { 173, L.T("Знос (стирання)") },
        { 174, L.T("Раптові вимкнення") }, { 177, L.T("Знос") }, { 179, L.T("Використано резерву") }, { 181, L.T("Помилки програмування") },
        { 182, L.T("Помилки стирання") }, { 183, L.T("Помилки SATA") }, { 184, L.T("Наскрізні помилки") }, { 187, L.T("Невиправні помилки") },
        { 188, L.T("Тайм-аути команд") }, { 190, L.T("Температура повітря") }, { 192, L.T("Аварійні вимкнення") }, { 193, L.T("Цикли головок") },
        { 194, L.T("Температура") }, { 195, L.T("Виправлено ECC") }, { 196, L.T("Події перепризначення") }, { 197, L.T("Нестабільні сектори") },
        { 198, L.T("Невиправні сектори") }, { 199, L.T("Помилки CRC інтерфейсу") }, { 202, L.T("Залишок ресурсу") }, { 231, L.T("Залишок ресурсу SSD") },
        { 232, L.T("Резерв") }, { 233, L.T("Знос NAND") }, { 241, L.T("Записано (LBA)") }, { 242, L.T("Прочитано (LBA)") }
    };

    static byte[] AtaCmd(SafeFileHandle h, int drive, byte feature)
    {
        byte[] q = new byte[33];
        BitConverter.GetBytes(512).CopyTo(q, 0);
        q[4] = feature;      // READ DATA / READ THRESHOLDS
        q[5] = 1; q[6] = 1;
        q[7] = 0x4F; q[8] = 0xC2;
        q[9] = (byte)(0xA0 | ((drive & 1) << 4));
        q[10] = 0xB0;        // SMART
        q[12] = (byte)drive;
        return Io.Ioctl(h, SMART_RCV, q, 16 + 512);
    }

    static bool Ata(SafeFileHandle h, int disk, StreamWriter w)
    {
        byte[] d = AtaCmd(h, disk, 0xD0), t = AtaCmd(h, disk, 0xD1);
        if (d == null || d.Length < 16 + 362) return false;
        Dictionary<int, int> thr = new Dictionary<int, int>();
        if (t != null && t.Length >= 16 + 362)
            for (int i = 0; i < 30; i++)
            {
                int o = 16 + 2 + i * 12;
                if (t[o] != 0) thr[t[o]] = t[o + 1];
            }
        bool fail = false, warn = false;
        w.WriteLine("kind=ata");
        for (int i = 0; i < 30; i++)
        {
            int o = 16 + 2 + i * 12;
            int id = d[o];
            if (id == 0) continue;
            int cur = d[o + 3], worst = d[o + 4];
            long raw = 0;
            for (int k = 5; k >= 0; k--) raw = (raw << 8) | d[o + 5 + k];
            int th = thr.ContainsKey(id) ? thr[id] : 0;
            if (th > 0 && cur <= th) fail = true;
            string nm;
            if (!Names.TryGetValue(id, out nm)) nm = L.T("Атрибут ") + id;
            long shown = raw;
            if (id == 194 || id == 190) shown = raw & 0xFF;
            if (id == 9) shown = raw & 0xFFFFFFFF;
            w.WriteLine("attr=" + id + "|" + nm + "|" + cur + "|" + worst + "|" + th + "|" + shown);
            if (id == 194 || (id == 190 && !thr.ContainsKey(194))) w.WriteLine("temp=" + (raw & 0xFF));
            if (id == 9) w.WriteLine("poh=" + (raw & 0xFFFFFFFF));
            if (id == 12) w.WriteLine("cycles=" + raw);
            if ((id == 5 || id == 197 || id == 198) && raw > 0) warn = true;
            if (id == 5) w.WriteLine("realloc=" + raw);
            if (id == 197) w.WriteLine("pending=" + raw);
            if (id == 198) w.WriteLine("uncorrect=" + raw);
            if (id == 177 || id == 231 || id == 233 || id == 202) w.WriteLine("life=" + cur);
        }
        w.WriteLine("health=" + (fail ? "fail" : (warn ? "warn" : "ok")));
        return true;
    }
}
