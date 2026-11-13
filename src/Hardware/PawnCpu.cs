using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

// ======================================================================
//  Температура й потужність процесора через PawnIO - підписаний драйвер, який ставлять
//  LibreHardwareMonitor і FanControl. Сам драйвер встановлюється окремо (pawnio.eu);
//  потрібний модуль (AMDFamily17.bin для AMD Ryzen, IntelMSR.bin для Intel) NeoBar завантажує
//  з офіційних релізів PawnIO.Modules (GitHub) у папку pawnio поруч із програмою - лише за згодою користувача.
//   AMD:   температура - SMN 0x00059800 (THM_TCON_CUR_TMP); потужність - MSR 0xC001029B / 0xC0010299.
//   Intel: температура - MSR 0x1B1 / 0x19C (TjMax з 0x1A2); потужність - MSR 0x611 / 0x606.
//  Формули - як у LibreHardwareMonitor.
// ======================================================================
static class CpuTempPawn
{
    public static string Status = "";
    public static float PowerW = float.NaN;


    delegate int FOpen(out IntPtr h);
    delegate int FLoad(IntPtr h, byte[] blob, IntPtr size);
    delegate int FExec(IntPtr h, [MarshalAs(UnmanagedType.LPStr)] string name, long[] inp, IntPtr inSize, long[] outp, IntPtr outSize, out IntPtr ret);
    delegate int FClose(IntPtr h);

    static IntPtr handle;
    static FExec exec;
    static float last = float.NaN;
    static DateTime next = DateTime.MinValue;
    static volatile bool downloading;
    static Mutex pci;
    static int vendor = -1;                  // 0 - AMD, 1 - Intel, 2 - інший
    static double energyUnit;
    static double tjMax = 100;
    static long lastEnergy = -1;
    static DateTime lastEnergyAt;

    static int Vendor
    {
        get
        {
            if (vendor >= 0) return vendor;
            vendor = 2;
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    string v = k != null ? (k.GetValue("VendorIdentifier") as string ?? "") : "";
                    if (v.IndexOf("AuthenticAMD", StringComparison.OrdinalIgnoreCase) >= 0) vendor = 0;
                    else if (v.IndexOf("GenuineIntel", StringComparison.OrdinalIgnoreCase) >= 0) vendor = 1;
                }
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
            return vendor;
        }
    }

    static int family = -1;

    // родина процесора AMD (з рядка «AMD64 Family 26 Model 68 …»)
    static int Family
    {
        get
        {
            if (family >= 0) return family;
            family = 0;
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    string id = k != null ? (k.GetValue("Identifier") as string ?? "") : "";
                    Match m = Regex.Match(id, @"Family\s+(\d+)");
                    if (m.Success) family = int.Parse(m.Groups[1].Value);
                }
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
            return family;
        }
    }

    // початок назви модуля для цього процесора; у нових випусках PawnIO.Modules назва може мати доповнення
    static string Prefix
    {
        get
        {
            if (Vendor == 1) return "IntelMSR";
            int f = Family;
            if (f == 0 || f >= 0x17) return "AMDFamily17";
            if (f >= 0x10) return "AMDFamily10";
            return "AMDFamily0F";
        }
    }

    // знайдений файл модуля (у папці pawnio поруч з програмою або в самій папці програми); null - немає
    public static string ModuleFile
    {
        get
        {
            string pre = Prefix;
            string[] dirs = { Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pawnio"), AppDomain.CurrentDomain.BaseDirectory };
            foreach (string d in dirs)
            {
                string exact = Path.Combine(d, pre + ".bin");
                if (File.Exists(exact)) return exact;
            }
            foreach (string d in dirs)
            {
                try
                {
                    if (!Directory.Exists(d)) continue;
                    foreach (string f in Directory.GetFiles(d, pre + "*.bin")) return f;
                }
                catch (Exception skipEx) { SbLog.Skip(skipEx); }
            }
            return null;
        }
    }

    public static string ModuleName
    {
        get { string f = ModuleFile; return f != null ? Path.GetFileName(f) : Prefix + ".bin"; }
    }

    // куди класти завантажений модуль
    public static string ModulePath
    {
        get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"pawnio\" + Prefix + ".bin"); }
    }

    // 0 - усе є (драйвер і модуль), 1 - PawnIO не встановлено, 2 - драйвер є, але нема модуля, 3 - процесор не підтримується
    public static int Phase
    {
        get
        {
            if (Vendor > 1) return 3;
            if (FindLib() == null) return 1;
            return ModuleFile != null ? 0 : 2;
        }
    }

    static string FindLib()
    {
        List<string> dirs = new List<string>();
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO"));
        string w6432 = Environment.GetEnvironmentVariable("ProgramW6432");
        if (!string.IsNullOrEmpty(w6432)) dirs.Add(Path.Combine(w6432, "PawnIO"));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PawnIO"));
        dirs.Add(AppDomain.CurrentDomain.BaseDirectory);
        foreach (string d in dirs)
        {
            string f = Path.Combine(d, "PawnIOLib.dll");
            if (File.Exists(f)) return f;
        }
        return null;
    }

    static bool ReadMsr(uint msr, out long v)
    {
        long[] inp = { msr }, outp = { 0 };
        IntPtr ret;
        int hr = exec(handle, "ioctl_read_msr", inp, new IntPtr(1), outp, new IntPtr(1), out ret);
        v = outp[0];
        return hr == 0;
    }

    // повертає температуру ЦП або NaN; заодно оновлює PowerW
    static readonly object gate = new object();

    // викликається з різних потоків (віджет, оверлей): драйвер і лічильник енергії - під одним замком
    public static float Read()
    {
        lock (gate) return ReadLocked();
    }

    static float ReadLocked()
    {
        if (DateTime.Now < next) return last;
        next = DateTime.Now.AddSeconds(1);
        if (IntPtr.Size != 8) { Status = L.T("PawnIO: потрібна 64-бітна програма"); next = DateTime.Now.AddMinutes(10); return float.NaN; }
        if (Vendor > 1) { Status = L.T("PawnIO: підтримуються лише процесори AMD і Intel"); next = DateTime.Now.AddMinutes(10); return float.NaN; }
        if (!Elevated.IsAdmin) { Status = L.T("PawnIO: потрібні права адміністратора"); next = DateTime.Now.AddSeconds(30); return float.NaN; }
        if (handle == IntPtr.Zero && !Init()) { last = float.NaN; PowerW = float.NaN; return last; }
        try
        {
            float t = Vendor == 0 ? ReadAmdTemp() : ReadIntelTemp();
            ReadPower();
            if (float.IsNaN(t)) { last = float.NaN; return last; }
            Status = L.T("PawnIO: працює");
            last = t;
            return t;
        }
        catch (Exception ex) { Status = "PawnIO: " + ex.Message; last = float.NaN; next = DateTime.Now.AddMinutes(1); return last; }
    }

    static float ReadAmdTemp()
    {
        bool locked = false, busy = false;
        try
        {
            if (pci == null) pci = new Mutex(false, @"Global\Access_PCI");
            locked = pci.WaitOne(150);
            busy = !locked;
        }
        catch (AbandonedMutexException) { locked = true; }     // попередній власник закрився, не звільнивши: м'ютекс тепер наш - обов'язково звільнити
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        // шину PCI/SMN зараз читає інша програма (HWiNFO, утиліта виробника): без черги її й наші запити перемішаються -
        // і обидві отримають чуже значення. Цього разу пропускаємо, лишається попереднє
        if (busy) return last;
        long[] inp = { 0x00059800 }, outp = { 0 };
        IntPtr ret;
        int hr;
        try { hr = exec(handle, "ioctl_read_smn", inp, new IntPtr(1), outp, new IntPtr(1), out ret); }
        finally { if (locked) { try { pci.ReleaseMutex(); } catch (Exception skipEx) { SbLog.Skip(skipEx); } } }
        if (hr != 0) { Status = L.T("PawnIO: помилка читання (0x") + hr.ToString("X8") + ")"; return float.NaN; }
        uint v = (uint)(outp[0] & 0xFFFFFFFF);
        float t = ((v >> 21) & 0x7FF) / 8.0f;
        if ((v & 0x80000) != 0) t -= 49;          // діапазон -49...206 °C
        if (t < 5 || t > 125) { Status = L.T("PawnIO: нереальне значення ") + t.ToString("0.#") + " °C"; return float.NaN; }
        return t;
    }

    static float ReadIntelTemp()
    {
        long v;
        // пакетний датчик, якщо чинний, інакше датчик ядра
        foreach (uint msr in new uint[] { 0x1B1, 0x19C })
        {
            if (!ReadMsr(msr, out v)) continue;
            if (((v >> 31) & 1) == 0) continue;                 // біт чинності
            int readout = (int)((v >> 16) & 0x7F);
            float t = (float)(tjMax - readout);
            if (t >= 5 && t <= 125) return t;
        }
        Status = L.T("PawnIO: Intel не віддав чинного значення");
        return float.NaN;
    }

    // потужність пакета з лічильника енергії
    static void ReadPower()
    {
        if (energyUnit <= 0) { PowerW = float.NaN; return; }
        long e;
        if (!ReadMsr(Vendor == 0 ? 0xC001029Bu : 0x611u, out e)) { PowerW = float.NaN; return; }
        e &= 0xFFFFFFFFL;
        DateTime now = DateTime.Now;
        if (lastEnergy >= 0)
        {
            double dt = (now - lastEnergyAt).TotalSeconds;
            if (dt >= 0.5)
            {
                long d = (e - lastEnergy) & 0xFFFFFFFFL;
                double w = d * energyUnit / dt;
                PowerW = w >= 0 && w < 800 ? (float)w : float.NaN;
                lastEnergy = e; lastEnergyAt = now;
            }
        }
        else { lastEnergy = e; lastEnergyAt = now; }
    }

    static bool Init()
    {
        string lib = FindLib();
        if (lib == null)
        {
            Status = L.T("PawnIO не встановлено - встановіть його з pawnio.eu, і температура ЦП з'явиться");
            next = DateTime.Now.AddSeconds(10);
            return false;
        }
        string modFile = ModuleFile;
        if (modFile == null)
        {
            Status = downloading ? L.T("PawnIO: завантажую модуль ") + ModuleName + "…" : L.T("PawnIO: потрібен модуль ") + ModuleName;
            next = DateTime.Now.AddSeconds(5);
            return false;
        }
        try
        {
            IntPtr m = Win.LoadLibrary(lib);
            if (m == IntPtr.Zero) { Status = L.T("PawnIO: не вдалося завантажити PawnIOLib.dll"); next = DateTime.Now.AddMinutes(5); return false; }
            FOpen open = Fn<FOpen>(m, "pawnio_open");
            FLoad load = Fn<FLoad>(m, "pawnio_load");
            exec = Fn<FExec>(m, "pawnio_execute");
            if (open == null || load == null || exec == null) { Status = L.T("PawnIO: несумісна версія PawnIOLib.dll"); next = DateTime.Now.AddMinutes(10); return false; }
            IntPtr h;
            int hr = open(out h);
            if (hr != 0 || h == IntPtr.Zero) { Status = L.T("PawnIO: драйвер не відкрився (0x") + hr.ToString("X8") + L.T("); можливо, потрібен перезапуск Windows"); next = DateTime.Now.AddMinutes(2); return false; }
            byte[] blob = File.ReadAllBytes(modFile);
            hr = load(h, blob, new IntPtr(blob.Length));
            if (hr != 0)
            {
                FClose close = Fn<FClose>(m, "pawnio_close");
                if (close != null) close(h);
                Status = L.T("PawnIO: модуль не завантажився (0x") + hr.ToString("X8") + ")";
                SbLog.W(SbLog.Sensors, Status);
                next = DateTime.Now.AddMinutes(10);
                return false;
            }
            handle = h;
            // одиниця енергії та TjMax - один раз
            long v;
            if (ReadMsr(Vendor == 0 ? 0xC0010299u : 0x606u, out v)) energyUnit = 1.0 / (1L << (int)((v >> 8) & 0x1F));
            if (Vendor == 1 && ReadMsr(0x1A2, out v)) { int tj = (int)((v >> 16) & 0xFF); if (tj > 50 && tj < 130) tjMax = tj; }
            lastEnergy = -1;
            SbLog.W(SbLog.Sensors, "PawnIO: модуль " + ModuleName + " завантажено");
            return true;
        }
        catch (Exception ex) { Status = "PawnIO: " + ex.Message; next = DateTime.Now.AddMinutes(5); return false; }
    }

    static T Fn<T>(IntPtr lib, string name) where T : class
    {
        IntPtr f = Win.GetProcAddress(lib, name);
        return f == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(f, typeof(T)) as T;
    }

    // Завантажити модуль (за згодою користувача). done(null) - успіх, інакше текст помилки; викликається з фонового потоку
    public static void DownloadAsync(Action<string> done)
    {
        if (downloading) return;
        downloading = true;
        Thread t = new Thread(delegate ()
        {
            string err = null;
            try { err = Download(); }
            catch (Exception ex) { err = ex.Message; }
            if (err != null) SbLog.W(SbLog.Sensors, "PawnIO: завантаження модуля: " + err);
            next = DateTime.MinValue;
            downloading = false;
            try { done(err); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        });
        t.IsBackground = true;
        t.Start();
    }

    // офіційний підписаний модуль з останнього релізу PawnIO.Modules; null - успіх
    static string Download()
    {
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;     // TLS 1.2
        string name = Prefix + ".bin", pre = Prefix.ToLowerInvariant();
        using (WebClient wc = new WebClient())
        {
            wc.Headers[HttpRequestHeader.UserAgent] = "NeoBar";
            string json = wc.DownloadString("https://api.github.com/repos/namazso/PawnIO.Modules/releases/latest");
            string direct = null, zip = null;
            foreach (Match m in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
            {
                string u = m.Groups[1].Value;
                string low = u.ToLowerInvariant();
                string leaf = low.Substring(low.LastIndexOf('/') + 1);
                if (leaf == name.ToLowerInvariant() || (direct == null && leaf.StartsWith(pre) && leaf.EndsWith(".bin"))) direct = u;
                else if (low.EndsWith(".zip") && zip == null) zip = u;
            }
            byte[] data = null;
            if (direct != null) data = wc.DownloadData(direct);
            else if (zip != null) data = UnzipEntry(wc.DownloadData(zip), name, Prefix);
            if (data == null || data.Length < 64) return L.T("файл ") + name + L.T(" не знайдено в останньому релізі");
            Directory.CreateDirectory(Path.GetDirectoryName(ModulePath));
            File.WriteAllBytes(ModulePath, data);
            SbLog.W(SbLog.Sensors, "PawnIO: модуль " + name + " завантажено (" + data.Length + " байт)");
            return null;
        }
    }

    // мінімальний читач zip (через центральний каталог), щоб не залежати від зайвих збірок
    static byte[] UnzipEntry(byte[] z, string fileName, string prefix)
    {
        int eocd = -1;
        for (int i = z.Length - 22; i >= 0 && i > z.Length - 70000; i--)
            if (z[i] == 0x50 && z[i + 1] == 0x4B && z[i + 2] == 5 && z[i + 3] == 6) { eocd = i; break; }
        if (eocd < 0) return null;
        int count = BitConverter.ToUInt16(z, eocd + 10);
        int p0 = (int)BitConverter.ToUInt32(z, eocd + 16);
        // спершу точна назва, потім - будь-який файл, що починається так само (інша версія модуля)
        for (int pass = 0; pass < 2; pass++)
        {
        int p = p0;
        for (int n = 0; n < count && p + 46 <= z.Length; n++)
        {
            if (BitConverter.ToUInt32(z, p) != 0x02014B50) break;
            int method = BitConverter.ToUInt16(z, p + 10);
            int csize = (int)BitConverter.ToUInt32(z, p + 20), usize = (int)BitConverter.ToUInt32(z, p + 24);
            int nlen = BitConverter.ToUInt16(z, p + 28), elen = BitConverter.ToUInt16(z, p + 30), clen = BitConverter.ToUInt16(z, p + 32);
            int lho = (int)BitConverter.ToUInt32(z, p + 42);
            string name = Encoding.UTF8.GetString(z, p + 46, nlen);
            p += 46 + nlen + elen + clen;
            string leaf = name.Substring(name.LastIndexOfAny(new[] { '/', '\\' }) + 1);
            bool hit = pass == 0 ? leaf.Equals(fileName, StringComparison.OrdinalIgnoreCase)
                                 : leaf.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && leaf.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);
            if (!hit) continue;
            int ds = lho + 30 + BitConverter.ToUInt16(z, lho + 26) + BitConverter.ToUInt16(z, lho + 28);
            if (method == 0) { byte[] r = new byte[usize]; Array.Copy(z, ds, r, 0, usize); return r; }
            if (method != 8) return null;
            using (MemoryStream src = new MemoryStream(z, ds, csize))
            using (DeflateStream df = new DeflateStream(src, CompressionMode.Decompress))
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buf = new byte[8192];
                int k;
                while ((k = df.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, k);
                return ms.ToArray();
            }
        }
        }
        return null;
    }
}
