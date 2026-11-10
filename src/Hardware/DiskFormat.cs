using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

// ======================================================================
//  Форматування тому: швидке (нова файлова система) або повне - «низькорівневе» в сучасному
//  розумінні: кожен сектор тому перезаписується нулями (з прогресом), потім створюється файлова
//  система. Справжнє низькорівневе форматування роблять лише на заводі. Виконує помічник
//  з правами адміністратора; системний диск, диск зі NeoBar і з файлом підкачки - заборонені.
// ======================================================================
static class DiskFormat
{

    // поточний розмір кластера тому в байтах (0 - не вдалося визначити)
    public static int ClusterOf(string root)
    {
        try
        {
            if (string.IsNullOrEmpty(root)) return 0;
            uint a, b, c, d;
            if (Win.GetDiskFreeSpace(root.EndsWith("\\") ? root : root + "\\", out a, out b, out c, out d)) return (int)Math.Min(int.MaxValue, (long)a * b);
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return 0;
    }

    // розміри кластера, які пропонуємо для файлової системи (Windows приймає степені двійки)
    public static List<int> Clusters(string fs, int current)
    {
        List<int> l = new List<int>();
        int lo = fs == "exFAT" ? 4096 : 512, hi = fs == "NTFS" ? 65536 : (fs == "exFAT" ? 262144 : 32768);
        for (int c = lo; c <= hi; c *= 2) l.Add(c);
        if (current > 0 && !l.Contains(current)) { l.Add(current); l.Sort(); }
        return l;
    }

    public static string ClusterText(int c)
    {
        if (c <= 0) return L.T("Авто");
        if (c < 1024) return c + L.T(" Б");
        if (c < 1048576) return c / 1024 + L.T(" КБ");
        return c / 1048576 + L.T(" МБ");
    }

    // чому цей диск форматувати не можна ("" - можна)
    public static string Forbidden(string letter)
    {
        letter = (letter ?? "").TrimEnd('\\', ':').ToUpperInvariant();
        if (letter.Length != 1 || letter[0] < 'A' || letter[0] > 'Z') return L.T("У тому немає літери диска.");
        try
        {
            string sys = Path.GetPathRoot(Environment.SystemDirectory).Substring(0, 1).ToUpperInvariant();
            if (letter == sys) return L.T("Це системний диск (на ньому Windows) - його форматувати не можна.");
            string me = Path.GetPathRoot(Application.ExecutablePath).Substring(0, 1).ToUpperInvariant();
            if (letter == me) return L.T("На цьому диску сам NeoBar - форматувати його звідси не можна.");
            DriveInfo d = new DriveInfo(letter);
            if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) return L.T("Форматуються лише локальні й знімні диски.");
            if (!d.IsReady) return L.T("Диск не готовий: перевірити його не вдалося.");
            foreach (string f in new[] { "pagefile.sys", "swapfile.sys", "hiberfil.sys" })
                if (File.Exists(letter + ":\\" + f)) return L.T("На диску файл підкачки чи сплячого режиму Windows - форматувати не можна.");
        }
        catch { return L.T("Не вдалося перевірити диск - форматування скасовано задля безпеки."); }
        return "";
    }

    public static int MaxLabel(string fs) { return fs == "NTFS" ? 32 : (fs == "exFAT" ? 15 : 11); }

    public static string SafeLabel(string s, string fs)
    {
        StringBuilder b = new StringBuilder();
        foreach (char c in s ?? "")
            if (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_') b.Append(c);
        string r = b.ToString().Trim();
        int max = MaxLabel(fs);
        return r.Length > max ? r.Substring(0, max) : r;
    }

    // помічник: --helper format out X fs quick|full кластер(0 - авто) base64(мітка)
    public static int Run(string letter, string fs, string mode, string cluster, string labelB64, StreamWriter w, string stopFile)
    {
        letter = letter.TrimEnd('\\', ':').ToUpperInvariant();
        if (fs != "NTFS" && fs != "exFAT" && fs != "FAT32") { w.WriteLine(L.T("Невідома файлова система.")); return 1; }
        string why = Forbidden(letter);
        if (why.Length > 0) { w.WriteLine(why); return 1; }
        string label = "";
        try { label = SafeLabel(Encoding.UTF8.GetString(Convert.FromBase64String(labelB64)), fs); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        int cl;
        if (!int.TryParse(cluster, out cl) || cl < 512 || cl > (32 << 20) || (cl & (cl - 1)) != 0) cl = 0;       // лише степені двійки; 0 - вибір Windows
        if (mode == "full")
        {
            int z = Zero(letter, w, stopFile);
            if (z != 0) w.WriteLine(L.T("Перезапис нулями не завершено - створюємо файлову систему, щоб диск лишився придатним."));
        }
        if (fs == "FAT32" && VolLen(letter) > (32L << 30)) { w.WriteLine(L.T("Створення FAT32 (власний форматер: Windows не робить FAT32 понад 32 ГБ)…")); return Fat32Raw(letter, cl, label, w); }
        w.WriteLine(L.T("Створення файлової системи ") + fs + "…");
        string ps = "$ErrorActionPreference='Stop'; try { Format-Volume -DriveLetter " + letter + " -FileSystem " + fs +
                    " -NewFileSystemLabel '" + label.Replace("'", "''") + "'" + (cl > 0 ? " -AllocationUnitSize " + cl : "") + " -Force -Confirm:$false | Out-Null; Write-Output 'OK'; exit 0 } catch { Write-Output $_.Exception.Message; exit 1 }";
        string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
        ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + enc);
        psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        int code;
        using (Process p = Process.Start(psi))
        {
            string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
            p.WaitForExit();
            code = p.ExitCode;
            string msg = (o + "\n" + e).Trim();
            if (code == 0) w.WriteLine(L.T("Готово: диск ") + letter + L.T(": відформатовано (") + fs + (label.Length > 0 ? L.Q(", «") + label + L.Q("»") : "") + ").");
            else w.WriteLine(L.T("Помилка форматування: ") + (msg.Length > 0 ? msg : L.T("код ") + code));
        }
        return code;
    }

    // закрити всі відкриті на томі файли: блокування, потім відключення файлової системи; false - том зайнятий
    static bool Grab(SafeFileHandle h, StreamWriter w)
    {
        bool locked = false;
        for (int i = 0; i < 10 && !locked; i++) { locked = Io.Ioctl(h, 0x90018, null, 0) != null; if (!locked) Thread.Sleep(500); }      // FSCTL_LOCK_VOLUME
        if (!locked) { w.WriteLine(L.T("Том зайнятий іншою програмою (відкриті файли) - закрийте їх і повторіть.")); return false; }
        Io.Ioctl(h, 0x90020, null, 0);                                   // FSCTL_DISMOUNT_VOLUME
        Io.Ioctl(h, 0x90083, null, 0);                                   // FSCTL_ALLOW_EXTENDED_DASD_IO
        return true;
    }

    // точний розмір тому в байтах (0 - не вдалося)
    static long VolLen(string letter)
    {
        try
        {
            using (SafeFileHandle h = Io.Open("\\\\.\\" + letter + ":", Io.GENERIC_READ))
            {
                if (h == null) return 0;
                byte[] li = Io.Ioctl(h, 0x7405C, null, 8);
                return li != null && li.Length >= 8 ? BitConverter.ToInt64(li, 0) : 0;
            }
        }
        catch { return 0; }
    }

    // FAT32 на томі понад 32 ГБ: розмітка пишеться напряму (Fat32Fmt), том має бути вже заблокований цим кодом
    static int Fat32Raw(string letter, int cluster, string label, StreamWriter w)
    {
        IntPtr buf = IntPtr.Zero;
        const int Block = 1 << 20;
        try
        {
            using (SafeFileHandle h = Io.CreateFile("\\\\.\\" + letter + ":", Io.GENERIC_READ | Io.GENERIC_WRITE, Io.SHARE_RW, IntPtr.Zero, Io.OPEN_EXISTING, 0x20000000 | 0x80000000, IntPtr.Zero))
            {
                if (h.IsInvalid) { w.WriteLine(L.T("Немає доступу до тому (код ") + Marshal.GetLastWin32Error() + ")."); return 1; }
                if (!Grab(h, w)) return 1;
                byte[] li = Io.Ioctl(h, 0x7405C, null, 8);                       // IOCTL_DISK_GET_LENGTH_INFO
                long len = li != null && li.Length >= 8 ? BitConverter.ToInt64(li, 0) : 0;
                byte[] ge = Io.Ioctl(h, 0x700A0, null, 128);                     // IOCTL_DISK_GET_DRIVE_GEOMETRY_EX: розмір сектора - зсув 20
                int bps = ge != null && ge.Length >= 24 ? BitConverter.ToInt32(ge, 20) : 512;
                if (bps < 512 || bps > 4096 || (bps & (bps - 1)) != 0) bps = 512;
                byte[] pi = Io.Ioctl(h, 0x70048, null, 144);                     // IOCTL_DISK_GET_PARTITION_INFO_EX: початок розділу - зсув 8
                long hidden = pi != null && pi.Length >= 16 ? BitConverter.ToInt64(pi, 8) / bps : 0;
                if (len <= 0) { w.WriteLine(L.T("Не вдалося визначити розмір тому.")); return 1; }
                string err;
                Fat32Fmt.Plan plan = Fat32Fmt.MakePlan(len, bps, cluster, out err);
                if (plan == null) { w.WriteLine(err); return 1; }
                buf = Win.VirtualAlloc(IntPtr.Zero, (UIntPtr)(Block + 4096), 0x3000, 0x04);
                if (buf == IntPtr.Zero) { w.WriteLine(L.T("Немає пам'яті.")); return 1; }
                string fail = null;
                Fat32Fmt.WriteAt wr = delegate (long off, byte[] b, int n)
                {
                    if (fail != null) return;
                    Marshal.Copy(b, 0, buf, n);
                    long np; int done;
                    if (!Win.SetFilePointerEx(h, off, out np, 0) || !Win.WriteFile(h, buf, n, out done, IntPtr.Zero) || done != n) fail = L.T("Помилка запису на диск (код ") + Marshal.GetLastWin32Error() + L.T(") за зсувом ") + off + ".";
                };
                Fat32Fmt.Format(wr, plan, hidden, Fat32Fmt.LabelOf(label), delegate (string s) { w.WriteLine(s); });
                if (fail != null) { w.WriteLine(fail); return 1; }
                Io.Ioctl(h, 0x9001C, null, 0);                                   // FSCTL_UNLOCK_VOLUME
                Io.Ioctl(h, 0x90020, null, 0);                                   // ще раз відключити: Windows заново прочитає нову розмітку
            }
            w.WriteLine(L.T("Готово: диск ") + letter + L.T(": відформатовано (FAT32") + (label.Length > 0 ? L.Q(", «") + label + L.Q("»") : "") + ").");
            return 0;
        }
        catch (Exception ex) { w.WriteLine(ex.Message); return 1; }
        finally { if (buf != IntPtr.Zero) Win.VirtualFree(buf, UIntPtr.Zero, 0x8000); }
    }

    // запис нулями по всьому тому; 0 - готово, 1 - помилка, 2 - зупинено
    static int Zero(string letter, StreamWriter w, string stopFile)
    {
        IntPtr buf = IntPtr.Zero;
        const int Block = 4 << 20;
        try
        {
            using (SafeFileHandle h = Io.CreateFile("\\\\.\\" + letter + ":", Io.GENERIC_READ | Io.GENERIC_WRITE, Io.SHARE_RW, IntPtr.Zero, Io.OPEN_EXISTING, 0x20000000 | 0x80000000, IntPtr.Zero))
            {
                if (h.IsInvalid) { w.WriteLine(L.T("Немає доступу до тому (код ") + Marshal.GetLastWin32Error() + ")."); return 1; }
                if (!Grab(h, w)) return 1;
                byte[] li = Io.Ioctl(h, 0x7405C, null, 8);                       // IOCTL_DISK_GET_LENGTH_INFO
                long len = li != null && li.Length >= 8 ? BitConverter.ToInt64(li, 0) : 0;
                if (len <= 0) { w.WriteLine(L.T("Не вдалося визначити розмір тому.")); return 1; }
                buf = Win.VirtualAlloc(IntPtr.Zero, (UIntPtr)Block, 0x3000, 0x04);   // обнулена й вирівняна пам'ять
                if (buf == IntPtr.Zero) { w.WriteLine(L.T("Немає пам'яті.")); return 1; }
                Stopwatch sw = Stopwatch.StartNew();
                long pos = 0, bad = 0;
                int lastPct = -1;
                while (pos < len)
                {
                    if (stopFile != null && File.Exists(stopFile)) { w.WriteLine(L.T("Зупинено на ") + Vols.Size(pos) + "."); return 2; }
                    int n = (int)Math.Min(Block, len - pos);
                    long np; int done;
                    if (!Win.SetFilePointerEx(h, pos, out np, 0) || !Win.WriteFile(h, buf, n, out done, IntPtr.Zero) || done != n) { bad += n; }
                    pos += n;
                    int pct = (int)(pos * 100 / len);
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        double mbs = pos / 1048576.0 / Math.Max(0.1, sw.Elapsed.TotalSeconds);
                        double left = (len - pos) / 1048576.0 / Math.Max(1, mbs);
                        w.WriteLine(L.T("↻Перезапис нулями: ") + pct + "% (" + Vols.Size(pos) + L.T(" з ") + Vols.Size(len) + "), " + Math.Round(mbs) + L.T(" МБ/с, лишилося ~") + Eta(left));
                    }
                }
                w.WriteLine(L.T("Перезапис нулями завершено за ") + Eta(sw.Elapsed.TotalSeconds) + "." + (bad > 0 ? L.T(" Не вдалося записати: ") + Vols.Size(bad) + L.T(" (можливі погані сектори).") : ""));
                return 0;
            }
        }
        catch (Exception ex) { w.WriteLine(ex.Message); return 1; }
        finally { if (buf != IntPtr.Zero) Win.VirtualFree(buf, UIntPtr.Zero, 0x8000); }
    }

    public static string Eta(double sec)
    {
        if (sec < 60) return Math.Max(1, (int)sec) + L.T(" с");
        if (sec < 3600) return (int)(sec / 60) + L.T(" хв");
        return (int)(sec / 3600) + L.T(" год ") + (int)(sec % 3600 / 60) + L.T(" хв");
    }
}

sealed class FormatWindow : SbWindow
{
    readonly Vol vol;
    readonly DiskInfo di;
    string fs, mode = "quick", outFile, outText = "", msg = "";
    bool done;
    readonly TextBox tbLabel;
    readonly string curFs;                 // файлова система диска зараз (NTFS/exFAT/FAT32; інакше порожньо)
    readonly int curCluster;               // розмір кластера зараз, байт
    int cluster;                           // вибраний розмір кластера (0 - вирішить Windows)
    string autoNote = "";

    public FormatWindow(NeoBar bar, Vol v, DiskInfo d) : base(bar, L.T("Форматування ") + v.Letter, 480, 640)
    { Glyph = "\uEDA2";
        vol = v; di = d;
        // налаштування за замовчуванням - ті, що диск має зараз: файлова система, мітка, розмір кластера
        string cf = (v.Fs ?? "").Trim().ToUpperInvariant();
        curFs = cf == "NTFS" ? "NTFS" : (cf == "EXFAT" ? "exFAT" : (cf == "FAT32" ? "FAT32" : ""));
        curCluster = curFs.Length > 0 ? DiskFormat.ClusterOf(v.Root) : 0;
        if (curFs.Length > 0 && !(curFs == "FAT32" && v.Total > Fat32Fmt.MaxBytes(512))) fs = curFs;
        else
        {
            fs = v.Total <= (32L << 30) && v.DriveType == 2 ? "FAT32" : (v.DriveType == 2 ? "exFAT" : "NTFS");
            if (curFs == "FAT32") autoNote = L.T("Зараз FAT32, але том завеликий для неї (найбільше ~2 ТБ) - запропоновано ") + fs + ".";
            else if (cf.Length > 0 && curFs.Length == 0) autoNote = L.T("Зараз ") + v.Fs + L.T(" - запропоновано ") + fs + ".";
        }
        cluster = ClusterFor(fs);
        tbLabel = MakeInput();
        tbLabel.Text = v.Label;
        tbLabel.MaxLength = 32;
        Ticker.Interval = 500;
        Ticker.Start();
        StartPosition = FormStartPosition.CenterScreen;
    }

    bool Ssd { get { return di != null && (di.Ssd && di.SsdKnown || di.BusType == 17); } }
    bool Busy { get { return outFile != null && !done; } }

    // кластер, з яким диск працює зараз, лишається, поки обрано ту саму файлову систему; інакше - на вибір Windows
    int ClusterFor(string f) { return f == curFs && curCluster > 0 ? curCluster : 0; }

    void StepCluster(int d)
    {
        List<int> l = new List<int>();
        l.Add(0);
        l.AddRange(DiskFormat.Clusters(fs, fs == curFs ? curCluster : 0));
        int i = Math.Max(0, l.IndexOf(cluster));
        cluster = l[Math.Max(0, Math.Min(l.Count - 1, i + d))];
        Invalidate();
    }

    protected override void OnTick()
    {
        if (outFile == null) return;
        bool d;
        string t = Elevated.ReadOut(outFile, out d);
        if (t != outText || d != done)
        {
            outText = t;
            if (d && !done) { done = true; try { Bar.OnDisksChanged(); } catch (Exception skipEx) { SbLog.Skip(skipEx); } try { File.Delete(outFile + ".stop"); } catch (Exception skipEx) { SbLog.Skip(skipEx); } }
            Invalidate();
        }
    }

    void Start()
    {
        string why = DiskFormat.Forbidden(vol.Letter);
        if (why.Length > 0) { msg = why; Invalidate(); return; }
        string label = DiskFormat.SafeLabel(tbLabel.Text, fs);
        string what = vol.Title + " (" + Vols.Size(vol.Total) + ")";
        string text = L.T("Усі файли на диску ") + what + L.T(" буде знищено.\n\nСпосіб: ") + (mode == "full" ? L.T("повне - кожен сектор перезаписується нулями, відновити дані вже не вийде") : L.T("швидке")) +
                      L.T("\nФайлова система: ") + fs + (label.Length > 0 ? L.T(", мітка «") + label + L.Q("»") : "") + L.T("\nРозмір кластера: ") + (cluster > 0 ? DiskFormat.ClusterText(cluster) : L.T("вибере Windows"));
        if (SbDialog.Show(Bar, this, L.T("Форматування"), text, L.T("Форматувати"), L.T("Скасувати")) != 0) return;
        if (SbDialog.Show(Bar, this, L.T("Останнє підтвердження"), L.T("Точно форматувати ") + vol.Letter + " " + (vol.Label.Length > 0 ? L.Q("«") + vol.Label + L.Q("» ") : "") + L.T("? Цю дію не скасувати."), L.T("Так, форматувати"), L.T("Ні")) != 0) return;
        string e;
        done = false; outText = ""; msg = "";
        outFile = Elevated.Run("format", vol.Letter + " " + fs + " " + mode + " " + cluster + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes(label)), out e);
        if (outFile == null) msg = e;
        Invalidate();
    }

    void StopIt()
    {
        if (outFile == null) return;
        try { File.WriteAllText(outFile + ".stop", "stop"); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        msg = L.T("Зупиняємо…");
        Invalidate();
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        int y = r.Y;
        Rectangle ic = new Rectangle(r.X, y, r.Width, Px(64));
        Card(g, ic);
        TxtL(g, vol.Title, FB, new Rectangle(ic.X + Px(14), ic.Y + Px(8), ic.Width - Px(28), Px(24)), P.Fg);
        string kind = di != null ? di.Kind : "";
        TxtL(g, Vols.Size(vol.Total) + L.T(" · зараз ") + (vol.Fs.Length > 0 ? vol.Fs : L.T("без файлової системи")) + (kind.Length > 0 ? " · " + kind : ""), FS,
            new Rectangle(ic.X + Px(14), ic.Y + Px(34), ic.Width - Px(28), Px(20)), P.Sub);
        y = ic.Bottom + Px(14);
        bool lockUi = Busy;

        TxtL(g, L.T("Файлова система"), F, new Rectangle(r.X, y, r.Width, Px(20)), P.Fg);
        y += Px(24);
        string[] fss = { "NTFS", "exFAT", "FAT32" };
        int cw = (r.Width - Px(12)) / 3;
        for (int i = 0; i < 3; i++)
        {
            string f = fss[i];
            bool ok = f != "FAT32" || vol.Total <= Fat32Fmt.MaxBytes(512);
            Chip(g, new Rectangle(r.X + i * (cw + Px(6)), y, cw, Px(30)), f, fs == f, ok && !lockUi ? (Action)delegate { fs = f; cluster = ClusterFor(f); Invalidate(); } : null);
        }
        y += Px(34);
        TxtL(g, fs == "NTFS" ? L.T("Для дисків Windows; файли понад 4 ГБ, права доступу") : fs == "exFAT" ? L.T("Для флешок і карт: Windows, macOS, телевізори; файли понад 4 ГБ") : L.T("Найсумісніша (старі пристрої), але файли лише до 4 ГБ"),
            FS, new Rectangle(r.X, y, r.Width, Px(18)), P.Sub);
        y += Px(24);
        if (fs == "FAT32" && vol.Total > (32L << 30))
        {
            TxtW(g, L.T("Понад 32 ГБ Windows сама FAT32 не створює (штучне обмеження її засобів; формат підтримує до 2 ТБ). NeoBar пише розмітку напряму; файли - лише до 4 ГБ."), FS, new Rectangle(r.X, y - Px(2), r.Width, Px(34)), P.Sub);
            y += Px(34);
        }
        if (autoNote.Length > 0) { TxtW(g, autoNote, FS, new Rectangle(r.X, y - Px(4), r.Width, Px(34)), P.Acc); y += Px(30); }
        // розмір кластера: [−] значення [+]
        TxtL(g, L.T("Розмір кластера"), F, new Rectangle(r.X, y, Px(150), Px(32)), P.Fg);
        int bw2 = Px(34);
        Btn(g, new Rectangle(r.Right - bw2 * 2 - Px(96), y, bw2, Px(32)), "−", delegate { StepCluster(-1); }, 0, !lockUi);
        TxtC(g, DiskFormat.ClusterText(cluster), FB, new Rectangle(r.Right - bw2 - Px(92), y, Px(88), Px(32)), P.Fg);
        Btn(g, new Rectangle(r.Right - bw2, y, bw2, Px(32)), "+", delegate { StepCluster(1); }, 0, !lockUi);
        y += Px(34);
        TxtL(g, fs == curFs && curCluster > 0 ? L.T("Зараз на диску: ") + DiskFormat.ClusterText(curCluster) + L.T(" (залишено). Авто - вибере Windows за розміром тому.") : (fs == "FAT32" && vol.Total > (32L << 30) ? L.T("Авто: ") + DiskFormat.ClusterText(Fat32Fmt.DefaultCluster(vol.Total)) + L.T(" - типовий розмір Microsoft для великого FAT32.") : L.T("Авто - Windows вибере розмір за розміром тому.")),
            FS, new Rectangle(r.X, y, r.Width, Px(18)), P.Sub);
        y += Px(28);
        TxtL(g, L.T("Мітка тому (до ") + DiskFormat.MaxLabel(fs) + L.T(" символів)"), F, new Rectangle(r.X, y, r.Width, Px(20)), P.Fg);
        y += Px(24);
        PlaceInput(g, tbLabel, new Rectangle(r.X, y, r.Width, Px(34)));
        tbLabel.Enabled = !lockUi;
        y += Px(46);

        TxtL(g, L.T("Спосіб"), F, new Rectangle(r.X, y, r.Width, Px(20)), P.Fg);
        y += Px(24);
        int mw = (r.Width - Px(6)) / 2;
        Chip(g, new Rectangle(r.X, y, mw, Px(30)), L.T("Швидке"), mode == "quick", lockUi ? null : (Action)delegate { mode = "quick"; Invalidate(); });
        Chip(g, new Rectangle(r.X + mw + Px(6), y, mw, Px(30)), L.T("Повне (низькорівневе)"), mode == "full", lockUi ? null : (Action)delegate { mode = "full"; Invalidate(); });
        y += Px(36);
        string desc;
        if (mode == "quick")
            desc = L.T("Лише нова файлова система, за кілька секунд. Старі файли фізично лишаються, і їх ще можна відновити") + (Ssd ? L.T(" - крім SSD: там Windows одразу стирає звільнене (TRIM).") : ".");
        else
        {
            double sec = vol.Total / (1048576.0 * (Ssd ? 400 : (vol.DriveType == 2 ? 25 : 120)));
            desc = L.T("Кожен сектор тому перезаписується нулями, потім створюється файлова система. Даних уже не відновити; збійні місця буде показано. Орієнтовно ") + DiskFormat.Eta(sec) + "." +
                   (Ssd ? L.T(" Для SSD це зайве: лише витрачає ресурс запису - вистачить швидкого.") : L.T(" Справжнє «низькорівневе» форматування роблять лише на заводі; це - найближчий аналог."));
        }
        TxtW(g, desc, FS, new Rectangle(r.X, y, r.Width, Px(52)), Ssd && mode == "full" ? P.Acc : P.Sub);
        y += Px(60);

        if (Busy) Btn(g, new Rectangle(r.X, y, r.Width, Px(38)), mode == "full" ? L.T("Зупинити перезапис") : L.T("Виконується…"), mode == "full" ? (Action)StopIt : null, 0, mode == "full");
        else Btn(g, new Rectangle(r.X, y, r.Width, Px(38)), L.T("Форматувати"), Start, 1, true);
        y += Px(48);
        string show = msg.Length > 0 ? msg : (outText.Length > 0 ? LastLines(outText, 4) : "");
        if (show.Length > 0)
        {
            Rectangle oc = new Rectangle(r.X, y, r.Width, r.Bottom - y);
            Card(g, oc);
            TxtW(g, show, FS, Rectangle.Inflate(oc, -Px(12), -Px(8)), P.Sub);
        }
    }

    static string LastLines(string s, int n)
    {
        List<string> l = new List<string>();
        foreach (string raw in s.Replace("\r", "").Split('\n'))
        {
            if (raw.Trim().Length == 0) continue;
            if (raw.StartsWith("↻") && l.Count > 0 && l[l.Count - 1].StartsWith("↻")) l[l.Count - 1] = raw; else l.Add(raw);
        }
        List<string> r = new List<string>();
        for (int i = Math.Max(0, l.Count - n); i < l.Count; i++) r.Add(l[i].TrimStart('↻'));
        return string.Join("\n", r.ToArray());
    }
}

// рядок режиму «Папки»: тека знайдених файлів (разом із вкладеними)
sealed class FolderRow
{
    public string Path = "", Name = "";
    public int Depth, Sel, Good;
    public long Size;
    public List<Found> Items = new List<Found>();
}

sealed class RecoverWindow : SbWindow
{
    readonly Vol vol;
    readonly DiskInfo di;
    readonly bool physical;                // увесь фізичний диск (з вибором розділу)
    readonly string prefix = "";           // режим «вибрана папка»: шлях усередині тому
    List<Part> parts = new List<Part>();
    int part = -1;                         // -1 - весь диск, інакше номер розділу в parts
    bool partsDone;
    RecoverScan scan;
    IBlockDev dev, baseDev;
    Thread worker;
    List<Found> view = new List<Found>();
    string group = "all", note = "", outDir = "";
    bool goodOnly, saving, folders;
    int scroll, saved, failed, lastCount = -1;
    List<FolderRow> frows = new List<FolderRow>();
    bool wasBusy;
    double saveProg;
    TextBox tbFind;

    static readonly string[][] Groups = { new[] { "all", L.T("Усі") }, new[] { "photo", L.T("Фото") }, new[] { "video", L.T("Відео") }, new[] { "doc", L.T("Документи") },
                                          new[] { "arch", L.T("Архіви") }, new[] { "audio", L.T("Аудіо") }, new[] { "other", L.T("Інше") } };

    public RecoverWindow(NeoBar bar, Vol v, DiskInfo d) : this(bar, v, d, "") { }

    public RecoverWindow(NeoBar bar, Vol v, DiskInfo d, string folder) : base(bar, folder.Length > 0 ? L.T("Відновлення у папці") : L.T("Відновлення файлів ") + v.Letter, 700, 760)
    {
        Glyph = "\uE777";
        vol = v; di = d; prefix = folder;
        Init();
    }

    public RecoverWindow(NeoBar bar, DiskInfo d) : base(bar, L.T("Відновлення: диск ") + d.No, 700, 760)
    {
        Glyph = "\uE777";
        physical = true; di = d;
        vol = new Vol(); vol.Total = d.Size;
        Init();
    }

    string SrcName { get { return physical ? L.T("диск ") + di.No : vol.Letter; } }

    void Post(Action a)
    {
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (physical && Elevated.IsAdmin)
            Util.Bg(delegate
            {
                List<Part> l = new List<Part>();
                try { using (VolumeDev vd = new VolumeDev(di.No)) l = PartTable.Find(vd); } catch (Exception ex) { Util.ErrorLog(ex); }
                Post(delegate { parts = l; partsDone = true; Invalidate(); });
            });
        else partsDone = true;
        if (prefix.Length > 0) Post(delegate { Begin(false); });
    }

    void Init()
    {
        tbFind = MakeInput();
        tbFind.TextChanged += delegate { Refilter(); Invalidate(); };
        Ticker.Interval = 400;
        Ticker.Start();
        StartPosition = FormStartPosition.CenterScreen;
    }

    bool Ssd { get { return di != null && (di.Ssd && di.SsdKnown || di.BusType == 17); } }
    bool Running { get { return worker != null && worker.IsAlive; } }

    protected override void OnTick()
    {
        if (scan == null) return;
        int c = scan.Count;
        bool busy = Running || saving;
        if (c != lastCount || busy || wasBusy) { if (c != lastCount) { lastCount = c; Refilter(); } Invalidate(); }
        wasBusy = busy;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (scan != null) scan.Stop = true;
        try { if (worker != null) worker.Join(2000); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        if (baseDev != null) try { baseDev.Dispose(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        base.OnFormClosed(e);
    }

    void Begin(bool deep)
    {
        if (Running) return;
        if (!Elevated.IsAdmin) { note = L.T("Для читання диска напряму потрібні права адміністратора - запустіть NeoBar від імені адміністратора."); Invalidate(); return; }
        try
        {
            if (baseDev != null) baseDev.Dispose();
            baseDev = null;
            if (physical)
            {
                baseDev = new VolumeDev(di.No);
                dev = part >= 0 && part < parts.Count ? (IBlockDev)new SubDev(baseDev, parts[part].Start, parts[part].Len) : baseDev;
            }
            else { baseDev = new VolumeDev(vol.Letter); dev = baseDev; }
        }
        catch (Exception ex) { note = ex.Message; Invalidate(); return; }
        scan = new RecoverScan(dev);
        scan.CarveOnly = physical && part < 0;
        note = ""; lastCount = -1; scroll = 0;
        RecoverScan s = scan;
        worker = new Thread(delegate () { try { if (deep) s.Deep(); else s.Quick(); } catch (Exception ex) { s.Error = ex.Message; } });
        worker.IsBackground = true;
        worker.Priority = ThreadPriority.BelowNormal;
        worker.Start();
        Invalidate();
    }

    void Refilter()
    {
        if (scan == null) { view = new List<Found>(); return; }
        string q = tbFind.Text.Trim().ToLowerInvariant();
        List<Found> l = new List<Found>();
        foreach (Found f in scan.Snapshot())
        {
            if (prefix.Length > 0 && !InPrefix(f)) continue;
            if (group != "all" && f.Group != group) continue;
            if (goodOnly && f.Chance < 2) continue;
            if (q.Length > 0 && f.Name.ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) < 0 && f.Dir.ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) < 0) continue;
            l.Add(f);
        }
        view = l;
        if (folders) BuildFolders(l); else frows = new List<FolderRow>();
    }

    bool InPrefix(Found f)
    {
        return f.Method != L.T("вміст") && (string.Equals(f.Dir, prefix, StringComparison.OrdinalIgnoreCase) || f.Dir.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase));
    }

    // теки зі знайденими файлами: кожен префікс шляху - окремий рядок, у якому всі файли всередині (із вкладеними теками)
    void BuildFolders(List<Found> files)
    {
        Dictionary<string, FolderRow> map = new Dictionary<string, FolderRow>(StringComparer.OrdinalIgnoreCase);
        foreach (Found f in files)
        {
            if (f.Method == L.T("вміст") || f.Dir.Length == 0) continue;
            string[] parts = f.Dir.Split('\\');
            string p = "";
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                p = p.Length == 0 ? parts[i] : p + "\\" + parts[i];
                FolderRow fr;
                if (!map.TryGetValue(p, out fr)) { fr = new FolderRow(); fr.Path = p; fr.Name = parts[i]; fr.Depth = i; map[p] = fr; }
                fr.Items.Add(f);
                fr.Size += f.Size;
                if (f.Chance >= 2) fr.Good++;
            }
        }
        List<FolderRow> l = new List<FolderRow>(map.Values);
        // ключ із символом, що менший за будь-яку літеру, щоб вкладені теки йшли одразу під батьківською
        l.Sort(delegate (FolderRow a, FolderRow b) { return string.Compare(a.Path.Replace('\\', '\u0001'), b.Path.Replace('\\', '\u0001'), StringComparison.OrdinalIgnoreCase); });
        frows = l;
        RecountFolders();
    }

    void RecountFolders()
    {
        foreach (FolderRow fr in frows)
        {
            int n = 0;
            foreach (Found f in fr.Items) if (f.Sel) n++;
            fr.Sel = n;
        }
    }

    long SelSize(out int n)
    {
        long s = 0; n = 0;
        if (scan == null) return 0;
        foreach (Found f in scan.Snapshot()) if (f.Sel) { s += f.Size; n++; }
        return s;
    }

    // місце призначення - на тому самому диску, який відновлюємо
    bool SameDisk(string root)
    {
        if (root.Length < 2) return false;
        string l = root.Substring(0, 2).ToUpperInvariant();
        if (!physical) return l == vol.Letter.ToUpperInvariant();
        try { foreach (Vol x in Vols.List()) if (x.Letter == l && x.DiskNo == di.No) return true; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return false;
    }

    void SaveSel()
    {
        int n;
        long size = SelSize(out n);
        if (n == 0 || saving) return;
        string dir;
        using (FolderBrowserDialog d = new FolderBrowserDialog())
        {
            d.Description = L.T("Куди зберегти відновлені файли (лише на ІНШИЙ диск - не на ") + SrcName + ")";
            if (d.ShowDialog(this) != DialogResult.OK) return;
            dir = d.SelectedPath;
        }
        string root = Path.GetPathRoot(dir) ?? "";
        if (SameDisk(root))
        {
            note = L.T("Не можна зберігати на той самий диск: запис затре ще не відновлені файли. Виберіть інший диск.");
            Invalidate();
            return;
        }
        try
        {
            DriveInfo dr = new DriveInfo(root);
            if (dr.AvailableFreeSpace < size) { note = L.T("На вибраному диску замало місця: потрібно ") + Cleaner.Size(size) + "."; Invalidate(); return; }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        outDir = Path.Combine(dir, L.T("Відновлено ") + (physical ? L.T("диск ") + di.No : vol.Letter.TrimEnd(':')) + " " + DateTime.Now.ToString("yyyy-MM-dd HH-mm"));
        List<Found> todo = new List<Found>();
        foreach (Found f in scan.Snapshot()) if (f.Sel) todo.Add(f);
        saving = true; saved = failed = 0; saveProg = 0; note = "";
        RecoverScan s = scan;
        Thread t = new Thread(delegate ()
        {
            byte[] buf = new byte[1 << 20];
            long doneB = 0;
            foreach (Found f in todo)
            {
                if (s.Stop) break;
                try { s.Save(f, outDir, buf); saved++; } catch (Exception ex) { failed++; Util.ErrorLog(ex); }
                doneB += f.Size;
                saveProg = size > 0 ? (double)doneB / size : 1;
            }
            saving = false;
        });
        t.IsBackground = true;
        t.Start();
        Invalidate();
    }

    // розділи диска: «весь диск» або один розділ (для нього працює швидкий пошук з іменами й папками)
    int PartsUi(Graphics g, Rectangle r, int y)
    {
        if (!partsDone) { TxtL(g, L.T("Читаю таблицю розділів…"), FS, new Rectangle(r.X, y, r.Width, Px(22)), P.Sub); return y + Px(28); }
        if (!Elevated.IsAdmin) { TxtW(g, L.T("Для читання диска напряму потрібні права адміністратора - запустіть NeoBar від імені адміністратора."), FS, new Rectangle(r.X, y, r.Width, Px(34)), P.Acc); return y + Px(38); }
        List<string> names = new List<string>();
        names.Add(L.T("Весь диск (за вмістом)"));
        for (int i = 0; i < parts.Count && i < 12; i++)
            names.Add(L.T("Розділ ") + (i + 1) + " · " + (parts[i].Fs.Length > 0 ? parts[i].Fs : L.T("невідомо")) + " · " + Vols.Size(parts[i].Len));
        int x = r.X, rowY = y, ch = Px(28);
        for (int i = 0; i < names.Count; i++)
        {
            int w = TextRenderer.MeasureText(names[i], F).Width + Px(24);
            if (x > r.X && x + w > r.Right) { x = r.X; rowY += ch + Px(6); }
            int idx = i - 1;
            Chip(g, new Rectangle(x, rowY, w, ch), names[i], part == idx, Running || saving ? null : (Action)delegate
            {
                if (part == idx) return;
                part = idx; scan = null; view = new List<Found>(); frows = new List<FolderRow>(); lastCount = -1; scroll = 0; outDir = ""; note = "";
                Invalidate();
            });
            x += w + Px(6);
        }
        return rowY + ch + Px(10);
    }

    static string ChanceText(int c) { return c == 2 ? L.T("ймовірно ціла") : (c == 1 ? L.T("може бути пошкоджена") : L.T("перезаписана")); }

    static string Files(int n)
    {
        int m10 = n % 10, m100 = n % 100;
        return n + " " + (m10 == 1 && m100 != 11 ? L.T("файл") : (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? L.T("файли") : L.T("файлів")));
    }

    // що означають позначки під назвами файлів
    void ChanceHelp()
    {
        SbDialog.Show(Bar, this, L.T("Що означають позначки"),
            L.T("Під кожним файлом написано, чи вдасться відновити його цілим. Це оцінка: NeoBar дивиться в карту зайнятих кластерів диска, чи не віддала система місце, де лежав файл, іншим даним.\n\n") +
            L.T("● Ймовірно ціла - місце файлу ще вільне, ніхто його не затер. Файл, скоріш за все, відновиться повністю.\n\n") +
            L.T("● Може бути пошкоджена - частина місця вже зайнята або перевірити неможливо: файл знайдено за вмістом, він міг лежати шматками, або його ланцюжок кластерів стерто (FAT). Відновиться, але перевірте його.\n\n") +
            L.T("● Перезаписана - місце вже віддали новим даним. Збережений файл міститиме чужі дані або сміття.\n\n") +
            L.T("Перемикач «Лише ймовірно цілі» ховає все, крім першої групи. Гарантій це не дає: SSD стирає звільнені блоки сам (TRIM), а після повного форматування даних уже немає."), L.T("Зрозуміло"));
    }

    void DrawFileRow(Graphics g, Found f, Rectangle rr, int rowH, Rectangle list)
    {
        Rectangle cb = new Rectangle(rr.X + Px(10), rr.Y + (rowH - Px(18)) / 2, Px(18), Px(18));
        using (SolidBrush b = new SolidBrush(f.Sel ? P.Acc : Util.Blend(P.Card, P.Fg, 0.15f))) Util.FillRound(g, b, cb, Px(4));
        if (f.Sel) Ico.Draw(g, "check", new RectangleF(cb.X + Px(3), cb.Y + Px(3), cb.Width - Px(6), cb.Height - Px(6)), Color.White);
        Color cc = f.Chance == 2 ? Color.FromArgb(80, 200, 110) : (f.Chance == 1 ? Color.FromArgb(240, 170, 40) : Color.FromArgb(230, 70, 70));
        using (SolidBrush b = new SolidBrush(cc)) g.FillEllipse(b, rr.X + Px(36), rr.Y + rowH / 2 - Px(4), Px(8), Px(8));
        int tx = rr.X + Px(52), sizeW = Px(90), dateW = Px(118);
        TxtL(g, f.Name, F, new Rectangle(tx, rr.Y + Px(2), rr.Width - tx + rr.X - sizeW - dateW - Px(16), Px(20)), P.Fg);
        TxtL(g, (f.Dir.Length > 0 ? f.Dir + " · " : "") + f.Method + " · " + ChanceText(f.Chance), FS, new Rectangle(tx, rr.Y + Px(20), rr.Width - tx + rr.X - sizeW - dateW - Px(16), Px(18)), P.Sub);
        TxtR(g, Cleaner.Size(f.Size), F, new Rectangle(rr.Right - sizeW - dateW - Px(12), rr.Y, sizeW, rowH), P.Fg);
        TxtR(g, f.When != DateTime.MinValue ? f.When.ToString("dd.MM.yyyy HH:mm") : "", FS, new Rectangle(rr.Right - dateW - Px(10), rr.Y, dateW, rowH), P.Sub);
        Found ff = f;
        if (Rectangle.Intersect(rr, list) == rr && !saving) Hot(rr, delegate { ff.Sel = !ff.Sel; Invalidate(); });
    }

    void DrawFolderRow(Graphics g, FolderRow fr, Rectangle rr, int rowH, Rectangle list)
    {
        Rectangle cb = new Rectangle(rr.X + Px(10), rr.Y + (rowH - Px(18)) / 2, Px(18), Px(18));
        bool all = fr.Items.Count > 0 && fr.Sel == fr.Items.Count, some = fr.Sel > 0 && !all;
        using (SolidBrush b = new SolidBrush(all || some ? P.Acc : Util.Blend(P.Card, P.Fg, 0.15f))) Util.FillRound(g, b, cb, Px(4));
        if (all) Ico.Draw(g, "check", new RectangleF(cb.X + Px(3), cb.Y + Px(3), cb.Width - Px(6), cb.Height - Px(6)), Color.White);
        else if (some) using (SolidBrush b = new SolidBrush(Color.White)) g.FillRectangle(b, cb.X + Px(5), cb.Y + cb.Height / 2 - Px(1), cb.Width - Px(10), Px(2));
        int ind = Math.Min(fr.Depth, 6) * Px(14);
        int tx = rr.X + Px(36) + ind, sizeW = Px(90);
        Ico.Draw(g, "folder", new RectangleF(tx, rr.Y + (rowH - Px(18)) / 2f, Px(18), Px(18)), P.Sub);
        int nx = tx + Px(24), w = rr.Right - nx - sizeW - Px(16);
        TxtL(g, fr.Name, F, new Rectangle(nx, rr.Y + Px(2), w, Px(20)), P.Fg);
        TxtL(g, Files(fr.Items.Count) + L.T(", ймовірно цілих: ") + fr.Good + " · " + fr.Path, FS, new Rectangle(nx, rr.Y + Px(20), w, Px(18)), P.Sub);
        TxtR(g, Cleaner.Size(fr.Size), F, new Rectangle(rr.Right - sizeW - Px(10), rr.Y, sizeW, rowH), P.Fg);
        FolderRow ff = fr;
        if (Rectangle.Intersect(rr, list) == rr && !saving) Hot(rr, delegate
        {
            bool allSel = ff.Items.Count > 0 && ff.Sel == ff.Items.Count;
            foreach (Found x in ff.Items) x.Sel = !allSel;
            RecountFolders();
            Invalidate();
        });
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        int y = r.Y;
        bool run = Running;
        // заголовок: попередження про SSD
        string head = physical ? L.T("Диск ") + di.No + " · " + (di.Model.Length > 0 ? di.Model : di.Kind) + " · " + Vols.Size(di.Size)
                    : (prefix.Length > 0 ? L.T("Папка ") + vol.Letter + "\\" + prefix + L.T(" (з вкладеними)") : vol.Title + " · " + (vol.Fs.Length > 0 ? vol.Fs : L.T("без файлової системи")) + " · " + Vols.Size(vol.Total));
        TxtL(g, head, FB, new Rectangle(r.X, y, r.Width, Px(22)), P.Fg);
        y += Px(24);
        string warn = Ssd ? L.T("SSD: після видалення Windows повідомляє накопичувачу про звільнені блоки (TRIM), і він їх стирає - шанс відновлення малий.")
                          : L.T("Нічого не записуйте на цей диск, доки не відновите потрібне: нові файли затирають видалені.");
        TxtW(g, warn, FS, new Rectangle(r.X, y, r.Width, Px(34)), Ssd ? P.Acc : P.Sub);
        y += Px(38);
        if (physical) y = PartsUi(g, r, y);
        int bw = (r.Width - Px(16)) / 3;
        bool canQuick = !physical || part >= 0, canDeep = prefix.Length == 0;
        Btn(g, new Rectangle(r.X, y, bw, Px(36)), L.T("Швидкий пошук"), delegate { Begin(false); }, canDeep && canQuick ? 0 : 1, !run && !saving && canQuick);
        Btn(g, new Rectangle(r.X + bw + Px(8), y, bw, Px(36)), L.T("Глибокий пошук"), delegate { Begin(true); }, canQuick ? 1 : 1, !run && !saving && canDeep);
        Btn(g, new Rectangle(r.X + (bw + Px(8)) * 2, y, bw, Px(36)), L.T("Зупинити"), delegate { if (scan != null) scan.Stop = true; }, 0, run || saving);
        y += Px(44);
        // стан
        string st;
        if (scan == null && physical && part < 0) st = L.T("Весь диск: пошук файлів за вмістом по всьому диску, зокрема на видалених розділах (імен і папок не буде). Щоб побачити імена й папки, виберіть розділ вище.");
        else if (scan == null) st = L.T("Швидкий - записи файлової системи про видалені файли (з іменами й папками), секунди. Глибокий - ще й пошук за вмістом по всьому вільному місцю (після форматування теж), довго.");
        else if (run) st = (scan.Stage.Length > 0 ? scan.Stage : L.T("Пошук…")) + L.T(" · знайдено ") + scan.Count;
        else st = scan.Error.Length > 0 ? scan.Error : (L.T("Знайдено файлів: ") + scan.Count + (scan.Fs.Length > 0 ? " (" + scan.Fs + ")" : ""));
        if (saving) st = L.T("Збереження: ") + saved + L.T(" готово") + (failed > 0 ? L.T(", помилок ") + failed : "");
        else if (outDir.Length > 0 && !run) st = L.T("Збережено: ") + saved + (failed > 0 ? L.T(", не вдалося: ") + failed : "") + " → " + outDir;
        if (note.Length > 0) st = note;
        TxtW(g, st, FS, new Rectangle(r.X, y, r.Width, Px(34)), note.Length > 0 ? P.Acc : P.Sub);
        y += Px(36);
        if (run || saving) ProgressBar(g, new Rectangle(r.X, y, r.Width, Px(6)), (float)(saving ? saveProg : scan.Progress), P.Acc);
        y += Px(14);

        // фільтри
        int gw = (r.Width - Px(6) * (Groups.Length - 1)) / Groups.Length;
        for (int i = 0; i < Groups.Length; i++)
        {
            string k = Groups[i][0];
            Chip(g, new Rectangle(r.X + i * (gw + Px(6)), y, gw, Px(28)), Groups[i][1], group == k, delegate { group = k; scroll = 0; Refilter(); Invalidate(); });
        }
        y += Px(36);
        int mw = Px(72);
        Chip(g, new Rectangle(r.X, y + Px(2), mw, Px(28)), L.T("Файли"), !folders, delegate { if (folders) { folders = false; scroll = 0; Refilter(); Invalidate(); } });
        Chip(g, new Rectangle(r.X + mw + Px(4), y + Px(2), mw, Px(28)), L.T("Папки"), folders, delegate { if (!folders) { folders = true; scroll = 0; Refilter(); Invalidate(); } });
        int fx = r.X + mw * 2 + Px(14);
        PlaceInput(g, tbFind, new Rectangle(fx, y, r.Right - Px(26) - Px(8) - (TextRenderer.MeasureText(L.T("Лише ймовірно цілі"), F).Width + Px(52)) - Px(12) - fx, Px(32)));
        string goodLabel = L.T("Лише ймовірно цілі");
        int qb = Px(26), swW = TextRenderer.MeasureText(goodLabel, F).Width + Px(36) + Px(16);
        Btn(g, new Rectangle(r.Right - qb, y + (Px(32) - qb) / 2, qb, qb), "?", ChanceHelp, 0, true);
        Switch(g, new Rectangle(r.Right - qb - Px(8) - swW, y, swW, Px(32)), goodLabel, goodOnly, delegate { goodOnly = !goodOnly; scroll = 0; Refilter(); Invalidate(); });
        y += Px(40);

        // список
        int rowH = Px(40);
        Rectangle list = new Rectangle(r.X, y, r.Width, r.Bottom - y - Px(50));
        Card(g, list);
        int count = folders ? frows.Count : view.Count;
        int maxScroll = Math.Max(0, count * rowH - list.Height);
        scroll = Math.Max(0, Math.Min(maxScroll, scroll));
        GraphicsState gs = g.Save();
        g.SetClip(list);
        int first = scroll / rowH, last = Math.Min(count - 1, (scroll + list.Height) / rowH);
        for (int i = first; i <= last; i++)
        {
            Rectangle rr = new Rectangle(list.X, list.Y + i * rowH - scroll, list.Width, rowH);
            if (Over(rr) && Rectangle.Intersect(rr, list) == rr) using (SolidBrush hb = new SolidBrush(Util.Blend(P.Card, P.Fg, 0.06f))) g.FillRectangle(hb, rr);
            if (folders) DrawFolderRow(g, frows[i], rr, rowH, list);
            else DrawFileRow(g, view[i], rr, rowH, list);
        }
        if (count == 0) TxtC(g, scan == null ? L.T("Почніть пошук") : (run ? L.T("Шукаємо…") : (folders && view.Count > 0 ? L.T("Тек немає: пошук за вмістом не зберігає ні імен, ні тек") : L.T("Нічого не знайдено"))), F, list, P.Sub);
        g.Restore(gs);
        Wheel(list, delegate (int d) { scroll -= d > 0 ? rowH * 3 : -rowH * 3; Invalidate(); });
        VBar(g, new Rectangle(list.Right - Px(7), list.Y + Px(6), Px(4), list.Height - Px(12)), count * rowH, list.Height, scroll, delegate (int v) { scroll = v; Invalidate(); });

        // низ: вибір і збереження
        int n;
        long sel = SelSize(out n);
        int by = r.Bottom - Px(40);
        Btn(g, new Rectangle(r.X, by, Px(150), Px(36)), L.T("Вибрати показані"), delegate { bool all = true; foreach (Found f in view) if (!f.Sel) all = false; foreach (Found f in view) f.Sel = !all; RecountFolders(); Invalidate(); }, 0, view.Count > 0 && !saving);
        TxtL(g, n > 0 ? L.T("Вибрано ") + n + " (" + Cleaner.Size(sel) + ")" : "", FS, new Rectangle(r.X + Px(160), by, r.Width - Px(360), Px(36)), P.Sub);
        Btn(g, new Rectangle(r.Right - Px(190), by, Px(190), Px(36)), L.T("Відновити в…"), SaveSel, 1, n > 0 && !saving && !run);
    }
}

// ----------------------------------------------------------------------
//  Віджет «Відновлення»: вибір диска, потім вікно відновлення цього диска
// ----------------------------------------------------------------------
sealed class RecoverPickWindow : SbWindow
{
    List<Vol> vols = new List<Vol>();
    List<DiskInfo> disks = new List<DiskInfo>();
    readonly Dictionary<string, DiskInfo> infos = new Dictionary<string, DiskInfo>();
    readonly Dictionary<int, string> letters = new Dictionary<int, string>();      // номер диска -> літери його томів
    bool loading = true;
    int scroll, tab = 1;                    // 0 - фізичні диски, 1 - логічні, 2 - вибрана папка
    string note = "";

    public RecoverPickWindow(NeoBar bar) : base(bar, L.T("Відновлення файлів"), 520, 620)
    { Glyph = "\uE777";
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Reload();
    }

    void Post(Action a)
    {
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    // томи й накопичувачі - у фоні: сплячий диск може відповідати секундами
    void Reload()
    {
        loading = true;
        Invalidate();
        Util.Bg(delegate
        {
            List<Vol> nv = new List<Vol>();
            List<DiskInfo> nd = new List<DiskInfo>();
            Dictionary<string, DiskInfo> inf = new Dictionary<string, DiskInfo>();
            Dictionary<int, DiskInfo> byNo = new Dictionary<int, DiskInfo>();
            Dictionary<int, string> lt = new Dictionary<int, string>();
            try
            {
                foreach (Vol v in Vols.List())
                {
                    if (v.Letter.Length == 0 || v.DriveType == 4 || v.DriveType == 5 || v.Total <= 0) continue;     // без мережевих і оптичних
                    nv.Add(v);
                    DiskInfo d = null;
                    if (v.DiskNo >= 0 && !byNo.TryGetValue(v.DiskNo, out d)) { d = Vols.Disk(v.DiskNo); byNo[v.DiskNo] = d; }
                    if (d != null) inf[v.Id] = d;
                    if (v.DiskNo >= 0) { string cur; lt.TryGetValue(v.DiskNo, out cur); lt[v.DiskNo] = (cur != null && cur.Length > 0 ? cur + ", " : "") + v.Letter; }
                }
                for (int n = 0; n < 16; n++)
                {
                    DiskInfo d;
                    if (!byNo.TryGetValue(n, out d)) d = Vols.Disk(n);
                    if (d != null && d.Size > 0 && !d.Optical) nd.Add(d);
                }
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
            Post(delegate
            {
                vols = nv; disks = nd;
                foreach (KeyValuePair<string, DiskInfo> kv in inf) infos[kv.Key] = kv.Value;
                letters.Clear();
                foreach (KeyValuePair<int, string> kv in lt) letters[kv.Key] = kv.Value;
                loading = false; Invalidate();
            });
        });
    }

    protected override void HeadButtons(Graphics g, ref int right)
    {
        Rectangle rr = new Rectangle(right - Px(32), Px(7), Px(32), Px(32));
        IconBtn(g, rr, "sync", delegate { Reload(); }, false);
        right = rr.Left - Px(4);
    }

    protected override bool AcceptsFiles { get { return true; } }

    // папку (або файл - тоді його теку) кинули у вікно
    protected override void FilesDropped(string[] files)
    {
        string p = files[0];
        try { if (File.Exists(p)) p = Path.GetDirectoryName(p); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        tab = 2;
        if (loading) { note = L.T("Диски ще читаються, повторіть за мить."); Invalidate(); return; }
        OpenFolder(p);
    }

    void PickFolder()
    {
        string path;
        using (FolderBrowserDialog d = new FolderBrowserDialog())
        {
            d.Description = L.T("Папка, з якої зникли файли (для видаленої теки - та, де вона лежала)");
            d.ShowNewFolderButton = false;
            if (d.ShowDialog(this) != DialogResult.OK) return;
            path = d.SelectedPath;
        }
        OpenFolder(path);
    }

    void OpenFolder(string path)
    {
        string root = Path.GetPathRoot(path) ?? "";
        if (root.Length < 3 || root[1] != ':') { note = L.T("Підтримуються лише локальні диски з літерою."); Invalidate(); return; }
        Vol v = null;
        foreach (Vol x in vols) if (string.Equals(x.Letter, root.Substring(0, 2), StringComparison.OrdinalIgnoreCase)) v = x;
        if (v == null) { note = L.T("Диск ") + root + L.T(" не знайдено серед локальних і знімних дисків."); Invalidate(); return; }
        DiskInfo di; infos.TryGetValue(v.Id, out di);
        string rel = path.Substring(root.Length).Trim('\\');
        note = "";
        new RecoverWindow(Bar, v, di, rel).Show();
        Close();
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        int y = r.Y;
        string[] tn = { L.T("Фізичні диски"), L.T("Логічні диски"), L.T("Вибрана папка") };
        int tw = (r.Width - Px(12)) / 3;
        for (int i = 0; i < 3; i++)
        {
            int k = i;
            Chip(g, new Rectangle(r.X + i * (tw + Px(6)), y, tw, Px(32)), tn[i], tab == i, delegate { tab = k; scroll = 0; note = ""; Invalidate(); });
        }
        y += Px(42);
        string intro = tab == 0 ? L.T("Диск цілком: пошук файлів по всьому накопичувачу, зокрема на видалених розділах. Виберіть розділ, щоб побачити імена й папки.")
                     : tab == 1 ? L.T("Виберіть диск чи флешку, з яких зникли файли або папки.")
                     : L.T("Виберіть папку, з якої зникли файли. Для видаленої теки виберіть ту, де вона лежала. Буде знайдено все видалене в ній і у вкладених.");
        TxtW(g, intro + L.T(" Нічого не пишіть на цей диск, поки не відновите потрібне: нові файли затирають видалені."), F, new Rectangle(r.X, y, r.Width, Px(70)), P.Fg);
        y += Px(74);
        if (!Elevated.IsAdmin)
        {
            TxtW(g, L.T("Для читання диска напряму потрібні права адміністратора - запустіть NeoBar від імені адміністратора."), FS, new Rectangle(r.X, y, r.Width, Px(34)), P.Acc);
            y += Px(38);
        }
        if (tab == 2)
        {
            DropZone(g, new Rectangle(r.X, y, r.Width, Px(112)), L.T("Перетягніть папку сюди"), L.T("Вибрати папку…"), PickFolder, !loading);
            y += Px(122);
            if (note.Length > 0) TxtW(g, note, FS, new Rectangle(r.X, y, r.Width, Px(40)), P.Acc);
            else TxtW(g, L.T("Підійде папка на будь-якому локальному чи знімному диску з NTFS, exFAT або FAT. Мережеві папки не підтримуються."), FS, new Rectangle(r.X, y, r.Width, Px(60)), P.Sub);
            FitTo(r, y + Px(68), Px(300));
            return;
        }
        int rowH = Px(68);
        Rectangle list = new Rectangle(r.X, y, r.Width, r.Bottom - y);
        int count = tab == 0 ? disks.Count : vols.Count;
        int total = count * rowH;
        // вікно за висотою - за вмістом (не порожнє при одному диску), але не вище за екран
        FitTo(r, list.Y + Math.Max(total, Px(70)), Px(300));
        scroll = Math.Max(0, Math.Min(Math.Max(0, total - list.Height), scroll));
        if (loading && count == 0) TxtC(g, L.T("Читаю диски…"), F, list, P.Sub);
        else if (count == 0) TxtC(g, L.T("Дисків не знайдено"), F, list, P.Sub);
        GraphicsState st = g.Save();
        g.SetClip(list);
        string sys = Vols.SysRoot();
        for (int i = 0; i < count; i++)
        {
            Rectangle rr = new Rectangle(list.X, list.Y + i * rowH - scroll, list.Width, rowH - Px(8));
            if (rr.Bottom < list.Y || rr.Top > list.Bottom) continue;
            Card(g, rr, Over(rr) ? Util.Blend(P.Card, P.Fg, 0.06f) : P.Card);
            int tx = rr.X + Px(62), tw2 = rr.Width - tx + rr.X - Px(110);
            string title, sub, hint = "";
            bool flash, isSys, ssd;
            DiskInfo d;
            Vol v = null;
            if (tab == 0)
            {
                d = disks[i];
                string lt; letters.TryGetValue(d.No, out lt);
                lt = lt ?? "";
                title = L.T("Диск ") + d.No + " · " + (d.Model.Length > 0 ? d.Model : d.Kind);
                sub = d.Kind + " · " + Vols.Size(d.Size) + (lt.Length > 0 ? L.T(" · томи: ") + lt : L.T(" · без томів"));
                flash = d.Usb || d.BusType == 12 || d.BusType == 13;
                isSys = lt.IndexOf(sys.Length >= 2 ? sys.Substring(0, 2) : "?", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            else
            {
                v = vols[i];
                infos.TryGetValue(v.Id, out d);
                title = v.Title;
                sub = (v.Fs.Length > 0 ? v.Fs : L.T("без файлової системи")) + " · " + Vols.Size(v.Total) + (d != null ? " · " + d.Kind : "");
                flash = v.DriveType == 2 || (d != null && (d.Usb || d.BusType == 12 || d.BusType == 13));
                isSys = string.Equals(v.Root, sys, StringComparison.OrdinalIgnoreCase);
            }
            ssd = d != null && (d.Ssd && d.SsdKnown || d.BusType == 17);
            int isz = Px(34);
            Ico.Draw(g, flash ? "usb" : "drive", new RectangleF(rr.X + Px(14), rr.Y + (rr.Height - isz) / 2f, isz, isz), P.Fg);
            TxtL(g, title, FB, new Rectangle(tx, rr.Y + Px(6), tw2, Px(24)), P.Fg);
            TxtL(g, sub, FS, new Rectangle(tx, rr.Y + Px(28), tw2, Px(18)), P.Sub);
            hint = isSys ? L.T("системний: Windows постійно пише - шанс менший") : (ssd ? L.T("SSD: TRIM стирає видалене - шанс малий") : "");
            if (hint.Length > 0) TxtL(g, hint, FS, new Rectangle(tx, rr.Y + Px(44), tw2, Px(16)), P.Acc);
            TxtR(g, L.T("Відновити ›"), F, new Rectangle(rr.Right - Px(110), rr.Y, Px(98), rr.Height), P.Acc);
            if (Rectangle.Intersect(rr, list) == rr)
            {
                Vol vv = v; DiskInfo dd = d;
                Hot(rr, delegate
                {
                    if (vv != null) new RecoverWindow(Bar, vv, dd).Show();
                    else new RecoverWindow(Bar, dd).Show();
                    Close();
                });
            }
        }
        g.Restore(st);
        Wheel(list, delegate (int dd) { scroll -= dd > 0 ? rowH : -rowH; Invalidate(); });
        VBar(g, new Rectangle(list.Right - Px(6), list.Y + Px(2), Px(4), list.Height - Px(8)), total, list.Height, scroll, delegate (int v) { scroll = v; Invalidate(); });
    }
}
