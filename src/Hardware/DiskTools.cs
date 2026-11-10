using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

// ======================================================================
//  Диски: тест швидкості (без кешу Windows), стан за даними Windows, подробиці
// ======================================================================
sealed class BenchResult
{
    public double WriteMBs = double.NaN, ReadMBs = double.NaN, Rand4kMBs = double.NaN, Rand4kIops = double.NaN;
    public string Error = "", Stage = "";
    public int Progress;          // 0..100
    public bool Done;
}

static class DiskBench
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int n, out int done, IntPtr ovl);

    const uint NO_BUFFERING = 0x20000000, WRITE_THROUGH = 0x80000000, DELETE_ON_CLOSE = 0x04000000;

    // Папка для тестового файлу на цьому томі (корінь системного диска закритий - беремо TEMP)
    static string TestDir(Vol v)
    {
        string temp = Path.GetTempPath();
        if (string.Equals(Path.GetPathRoot(temp), v.Root, StringComparison.OrdinalIgnoreCase)) return temp;
        string d = Path.Combine(v.Root, "NeoBar-test");
        Directory.CreateDirectory(d);
        return d;
    }

    public static void Run(Vol v, BenchResult res)
    {
        IntPtr buf = IntPtr.Zero;
        string dir = null, file = null;
        const int block = 4 << 20;
        try
        {
            long free = v.Free;
            bool small = v.DriveType == 2;
            long size = (small ? 128L : 512L) << 20;
            if (free < size * 2) size = Math.Max(32L << 20, (free / 4) / block * block);
            if (size < (32L << 20)) { res.Error = L.T("Замало вільного місця для тесту."); return; }
            dir = TestDir(v);
            file = Path.Combine(dir, "neobar-speed-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            buf = Win.VirtualAlloc(IntPtr.Zero, (UIntPtr)block, 0x3000, 0x04);   // вирівняна пам'ять для запису без кешу
            if (buf == IntPtr.Zero) { res.Error = L.T("Немає пам'яті для тесту."); return; }
            byte[] rnd = new byte[block];
            new Random(7).NextBytes(rnd);
            Marshal.Copy(rnd, 0, buf, block);

            // послідовний запис
            res.Stage = L.T("Запис");
            using (SafeFileHandle h = Io.CreateFile(file, Io.GENERIC_WRITE, 0, IntPtr.Zero, 2, NO_BUFFERING | WRITE_THROUGH, IntPtr.Zero))
            {
                if (h.IsInvalid) { res.Error = L.T("Не вдалося створити тестовий файл (код ") + Marshal.GetLastWin32Error() + ")."; return; }
                Stopwatch sw = Stopwatch.StartNew();
                long done = 0;
                while (done < size)
                {
                    int n;
                    if (!Win.WriteFile(h, buf, block, out n, IntPtr.Zero) || n == 0) { res.Error = L.T("Помилка запису (код ") + Marshal.GetLastWin32Error() + ")."; return; }
                    done += n;
                    res.Progress = (int)(done * 40 / size);
                }
                sw.Stop();
                res.WriteMBs = size / 1048576.0 / sw.Elapsed.TotalSeconds;
            }
            // послідовне читання
            res.Stage = L.T("Читання");
            using (SafeFileHandle h = Io.CreateFile(file, Io.GENERIC_READ, 1, IntPtr.Zero, 3, NO_BUFFERING, IntPtr.Zero))
            {
                if (h.IsInvalid) { res.Error = L.T("Не вдалося відкрити тестовий файл."); return; }
                Stopwatch sw = Stopwatch.StartNew();
                long done = 0;
                while (done < size)
                {
                    int n;
                    if (!ReadFile(h, buf, block, out n, IntPtr.Zero) || n == 0) break;
                    done += n;
                    res.Progress = 40 + (int)(done * 40 / size);
                }
                sw.Stop();
                res.ReadMBs = done / 1048576.0 / sw.Elapsed.TotalSeconds;

                // випадкове читання блоками по 4 КБ (5 секунд)
                res.Stage = L.T("Випадкове читання 4 КБ");
                Random r = new Random(11);
                long blocks = size / 4096;
                int ops = 0;
                Stopwatch rs = Stopwatch.StartNew();
                while (rs.ElapsedMilliseconds < 5000)
                {
                    long pos = (long)(r.NextDouble() * (blocks - 1)) * 4096;
                    long np;
                    int n;
                    if (!Win.SetFilePointerEx(h, pos, out np, 0) || !ReadFile(h, buf, 4096, out n, IntPtr.Zero)) break;
                    ops++;
                    res.Progress = 80 + (int)(rs.ElapsedMilliseconds * 20 / 5000);
                }
                rs.Stop();
                if (ops > 0)
                {
                    res.Rand4kIops = ops / rs.Elapsed.TotalSeconds;
                    res.Rand4kMBs = res.Rand4kIops * 4096 / 1048576.0;
                }
            }
            res.Progress = 100;
        }
        catch (Exception ex) { res.Error = ex.Message; }
        finally
        {
            if (buf != IntPtr.Zero) Win.VirtualFree(buf, UIntPtr.Zero, 0x8000);
            try { if (file != null && File.Exists(file)) File.Delete(file); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            try { if (dir != null && dir.EndsWith("NeoBar-test") && Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            res.Stage = "";
            res.Done = true;
        }
    }
}

// Стан фізичних дисків за даними Windows (служба сховища; права адміністратора не потрібні)
sealed class WinDiskHealth
{
    public string Name = "", Health = "", Operational = "", Media = "", Bus = "", Usage = "";
    public long Size;
}

static class DiskHealth
{
    static readonly Dictionary<int, WinDiskHealth> cache = new Dictionary<int, WinDiskHealth>();
    static DateTime at = DateTime.MinValue;

    // через PowerShell (Get-PhysicalDisk): ~1-2 с, тому лише у фоні
    public static WinDiskHealth Get(int diskNo)
    {
        lock (cache)
        {
            if ((DateTime.Now - at).TotalSeconds > 60) Refresh();
            WinDiskHealth h;
            return cache.TryGetValue(diskNo, out h) ? h : null;
        }
    }

    static void Refresh()
    {
        at = DateTime.Now;
        cache.Clear();
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
                "Get-PhysicalDisk | ForEach-Object { '{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}' -f $_.DeviceId,$_.FriendlyName,$_.HealthStatus,($_.OperationalStatus -join ','),$_.MediaType,$_.BusType,$_.Size,$_.Usage }\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            using (Process p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(15000);
                foreach (string line in o.Replace("\r", "").Split('\n'))
                {
                    string[] f = line.Split('|');
                    int no;
                    if (f.Length < 8 || !int.TryParse(f[0].Trim(), out no)) continue;
                    WinDiskHealth h = new WinDiskHealth();
                    h.Name = f[1].Trim(); h.Health = f[2].Trim(); h.Operational = f[3].Trim();
                    h.Media = f[4].Trim(); h.Bus = f[5].Trim(); h.Usage = f[7].Trim();
                    long.TryParse(f[6].Trim(), out h.Size);
                    cache[no] = h;
                }
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    public static string HealthText(string h)
    {
        switch ((h ?? "").ToLowerInvariant())
        {
            case "healthy": return L.T("Справний");
            case "warning": return L.T("Попередження");
            case "unhealthy": return L.T("Несправний");
        }
        return h == null || h.Length == 0 ? L.T("невідомо") : h;
    }

    public static string OpText(string o)
    {
        List<string> l = new List<string>();
        foreach (string p in (o ?? "").Split(','))
        {
            switch (p.Trim().ToLowerInvariant())
            {
                case "ok": l.Add(L.T("працює нормально")); break;
                case "": break;
                case "predictive failure": l.Add(L.T("прогнозується збій")); break;
                case "lost communication": l.Add(L.T("втрачено зв'язок")); break;
                case "degraded": l.Add(L.T("погіршений стан")); break;
                case "in maintenance": l.Add(L.T("обслуговування")); break;
                default: l.Add(p.Trim()); break;
            }
        }
        return string.Join(", ", l.ToArray());
    }
}

// Подробиці про том і накопичувач (IOCTL, без прав адміністратора)
static class DiskDetails
{

    const uint QUERY_PROPERTY = 0x2D1400;

    static byte[] Prop(SafeFileHandle h, int id, int outSize)
    {
        byte[] q = new byte[12];
        Array.Copy(BitConverter.GetBytes(id), 0, q, 0, 4);
        return Io.Ioctl(h, QUERY_PROPERTY, q, outSize);
    }

    public static List<string[]> Get(Vol v, DiskInfo d)
    {
        List<string[]> l = new List<string[]>();
        try
        {
            if (v.Root.Length > 0)
            {
                uint spc, bps, fc, tc;
                if (Win.GetDiskFreeSpace(v.Root, out spc, out bps, out fc, out tc)) l.Add(new[] { L.T("Розмір кластера"), (spc * bps / 1024) + L.T(" КБ") });
                StringBuilder lab = new StringBuilder(261), fs = new StringBuilder(261);
                uint ser, mc, fl;
                if (Win.GetVolumeInformation(v.Root, lab, lab.Capacity, out ser, out mc, out fl, fs, fs.Capacity))
                {
                    l.Add(new[] { L.T("Серійний номер тому"), ser.ToString("X8").Insert(4, "-") });
                    List<string> f = new List<string>();
                    if ((fl & 0x10) != 0) f.Add(L.T("стиснення"));
                    if ((fl & 0x20000) != 0) f.Add(L.T("шифрування EFS"));
                    if ((fl & 0x80000) != 0) f.Add(L.T("лише читання"));
                    if ((fl & 0x20) != 0) f.Add(L.T("квоти"));
                    if (f.Count > 0) l.Add(new[] { L.T("Можливості ФС"), string.Join(", ", f.ToArray()) });
                }
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        if (d == null || d.No < 0) return l;
        try
        {
            using (SafeFileHandle h = Io.Open("\\\\.\\PhysicalDrive" + d.No, 0))
            {
                if (h == null) return l;
                if (d.Rev.Length > 0) l.Add(new[] { L.T("Прошивка"), d.Rev });
                byte[] trim = Prop(h, 8, 12);
                if (trim != null && trim.Length >= 9) l.Add(new[] { "TRIM", trim[8] != 0 ? L.T("підтримується") : L.T("ні") });
                byte[] al = Prop(h, 6, 28);
                if (al != null && al.Length >= 24)
                    l.Add(new[] { L.T("Сектор (логічний / фізичний)"), Io.U32(al, 16) + " / " + Io.U32(al, 20) + L.T(" байт") });
                byte[] wc = Prop(h, 4, 32);
                if (wc != null && wc.Length >= 16)
                {
                    uint en = Io.U32(wc, 12);
                    l.Add(new[] { L.T("Кеш запису"), en == 2 ? L.T("увімкнено") : (en == 1 ? L.T("вимкнено") : L.T("невідомо")) });
                }
                byte[] ad = Prop(h, 1, 64);
                if (ad != null && ad.Length >= 12)
                {
                    uint mx = Io.U32(ad, 8);
                    if (mx > 0 && mx < 0x40000000) l.Add(new[] { L.T("Макс. передача за запит"), (mx / 1024) + L.T(" КБ") });
                }
                l.Add(new[] { L.T("Номер диска в системі"), d.No.ToString() });
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return l;
    }
}
