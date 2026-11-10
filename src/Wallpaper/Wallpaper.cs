using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

// ======================================================================
//  Шпалери робочого столу (окремо для кожного профілю, wallpaper.ini):
//   0 - системні (NeoBar нічого не змінює),
//   1 - зображення, 2 - слайд-шоу з переходами, 3 - живі шпалери (відео або GIF).
//  Режими 1-3 показує рушій NeoBarWall64.dll / NeoBarWall32.dll (через rundll32) поверх шпалер Windows,
//  тому самі шпалери Windows лишаються незмінними: після виходу з NeoBar видно їх. Лише якщо рушій
//  не зміг показати кадр, зображення ставиться засобами Windows - і після виходу повертаються попередні.
//  Рушій - окремий маленький процес (C++, Direct3D 11 + Direct2D + Media Foundation), який NeoBar
//  запускає зі звичайними правами навіть тоді, коли сам працює від адміністратора: вікно з процесу
//  адміністратора Провідник під іконками може й не показати. Керування - WM_COPYDATA, стан рушій
//  пише у файл (TEMP\NeoBarWall.status), журнал - у журнал панелі.
//  Пауза (гра, розгорнуте вікно, батарея) - за рішенням NeoBar; рушій у паузі нічого не малює.
// ======================================================================
static class WallCfg
{
    public static int Mode;                         // 0 системні, 1 зображення, 2 слайд-шоу, 3 живі
    public static string Image = "", Video = "";
    public static int Fit;                          // 0 заповнити, 1 вписати, 2 розтягнути, 3 по центру
    public static int Interval = 10;                // хвилин між зображеннями слайд-шоу
    public static bool Shuffle = true, Mute = true, AllMonitors = true, PauseCovered = true, PauseBattery = true;
    public static int Fx = 1;                       // 0 без анімації, 1 згасання, 2 зсув, 3 наближення, 4 шторки, 5 випадково
    public static int TransMs = 1200;

    public static readonly string[] Modes = { L.T("Системні (NeoBar нічого не змінює)"), L.T("Зображення"), L.T("Слайд-шоу"), L.T("Живі шпалери (відео або GIF)") };
    public static readonly string[] Fits = { L.T("Заповнити (обрізати краї)"), L.T("Вписати (з полями)"), L.T("Розтягнути"), L.T("По центру") };
    public static readonly string[] Transitions = { L.T("Без анімації (миттєва заміна)"), L.T("Плавне згасання"), L.T("Зсув"), L.T("Наближення"), L.T("Шторки"), L.T("Випадковий") };
    public static readonly int[] Intervals = { 1, 5, 10, 15, 30, 60, 180, 1440 };
    public static readonly string[] IntervalNames = { L.T("Щохвилини"), L.T("Кожні 5 хвилин"), L.T("Кожні 10 хвилин"), L.T("Кожні 15 хвилин"), L.T("Кожні 30 хвилин"), L.T("Щогодини"), L.T("Кожні 3 години"), L.T("Раз на добу") };

    static string Path_ { get { return Prof.File("wallpaper.ini"); } }

    public static void Load()
    {
        Mode = 0; Image = Video = ""; Fit = 0; Interval = 10; Shuffle = true; Mute = true; AllMonitors = true; PauseCovered = true; PauseBattery = true;
        Fx = 1; TransMs = 1200;
        try
        {
            if (!SafeFile.Exists(Path_)) return;
            foreach (string raw in SafeFile.ReadLines(Path_))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                string k = raw.Substring(0, eq).Trim().ToLowerInvariant(), v = raw.Substring(eq + 1).Trim();
                int n; int.TryParse(v, out n);
                switch (k)
                {
                    case "mode": Mode = Math.Max(0, Math.Min(5, n)); break;
                    case "image": Image = v; break;
                    case "video": Video = v; break;
                    case "fit": Fit = Math.Max(0, Math.Min(3, n)); break;
                    case "interval": Interval = Math.Max(1, n); break;
                    case "shuffle": Shuffle = v == "1"; break;
                    case "mute": Mute = v == "1"; break;
                    case "all": AllMonitors = v == "1"; break;
                    case "pausecovered": PauseCovered = v == "1"; break;
                    case "pausebattery": PauseBattery = v == "1"; break;
                    case "fx": Fx = Math.Max(0, Math.Min(5, n)); break;
                    case "transms": TransMs = Math.Max(200, Math.Min(5000, n)); break;
                }
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    public static void Save()
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(L.T("# NeoBar - шпалери"));
            sb.AppendLine("mode=" + Mode);
            sb.AppendLine("image=" + Image);
            sb.AppendLine("video=" + Video);
            sb.AppendLine("fit=" + Fit);
            sb.AppendLine("interval=" + Interval);
            sb.AppendLine("shuffle=" + (Shuffle ? 1 : 0));
            sb.AppendLine("mute=" + (Mute ? 1 : 0));
            sb.AppendLine("all=" + (AllMonitors ? 1 : 0));
            sb.AppendLine("pausecovered=" + (PauseCovered ? 1 : 0));
            sb.AppendLine("pausebattery=" + (PauseBattery ? 1 : 0));
            sb.AppendLine("fx=" + Fx);
            sb.AppendLine("transms=" + TransMs);
            SafeFile.Write(Path_, sb.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }
}

// ----------------------------------------------------------------------
//  Шпалери Windows, які були до того, як NeoBar поставив свої засобами Windows (лише запасний шлях,
//  коли рушій не зміг показати кадр). Один файл на всі профілі: wallpaper-original.ini поруч із програмою.
//  Set - що поставив NeoBar: якщо користувач потім сам змінив шпалери, нічого не повертаємо.
// ----------------------------------------------------------------------
static class WallOrig
{
    public static string Image = "", Style = "", Tile = "", Set = "";
    public static string Copy = "";       // копія вихідних шпалер поруч із програмою (ім'я файла): вони повертаються, навіть якщо оригінал зник (перенесення на інший ПК)
    static bool loaded;

    public static string CopyPath { get { return Copy.Length > 0 ? Path.Combine(Prof.Root, Copy) : ""; } }

    // зберегти копію вихідних шпалер (до 80 МБ)
    public static void MakeCopy()
    {
        try
        {
            if (Image.Length == 0 || !File.Exists(Image)) return;
            FileInfo fi = new FileInfo(Image);
            if (fi.Length > 80L * 1024 * 1024) return;
            string name = "wallpaper-original" + Path.GetExtension(Image).ToLowerInvariant();
            File.Copy(Image, Path.Combine(Prof.Root, name), true);
            Copy = name;
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    static string File_ { get { return Path.Combine(Prof.Root, "wallpaper-original.ini"); } }

    public static bool Has { get { Load(); return Image.Length > 0 || Set.Length > 0; } }

    public static void Load()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            if (!SafeFile.Exists(File_)) return;
            foreach (string raw in SafeFile.ReadLines(File_))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                string k = raw.Substring(0, eq).Trim().ToLowerInvariant(), v = raw.Substring(eq + 1).Trim();
                if (k == "image") Image = v;
                else if (k == "style") Style = v;
                else if (k == "tile") Tile = v;
                else if (k == "set") Set = v;
                else if (k == "copy") Copy = v;
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    public static void Save()
    {
        try
        {
            if (Image.Length == 0 && Set.Length == 0) { SafeFile.Delete(File_); return; }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(L.T("# NeoBar - шпалери Windows до NeoBar (щоб повернути)"));
            sb.AppendLine("image=" + Image);
            sb.AppendLine("style=" + Style);
            sb.AppendLine("tile=" + Tile);
            sb.AppendLine("set=" + Set);
            sb.AppendLine("copy=" + Copy);
            SafeFile.Write(File_, sb.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    public static void Clear()
    {
        try { if (Copy.Length > 0 && File.Exists(CopyPath)) File.Delete(CopyPath); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        Image = Style = Tile = Set = Copy = "";
        Save();
    }
}

// ----------------------------------------------------------------------
//  Рушій шпалер NeoBarWall: запуск зі звичайними правами, команди, стан
// ----------------------------------------------------------------------
static class WallEngine
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)] struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, ref COPYDATASTRUCT l, uint flags, uint timeout, out IntPtr result);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr ret);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);

    static Process proc;
    static string args = "";
    static readonly List<string> queue = new List<string>();
    static readonly Timer flush = new Timer();
    static bool flushHooked;
    static DateTime waitUntil;
    public static string Error = "";
    public static IntPtr Owner;

    // одне значення з файлу стану рушія ("" - немає файлу чи ключа)
    public static string StatusValue(string key)
    {
        try
        {
            if (!File.Exists(StatusFile)) return "";
            foreach (string l in File.ReadAllLines(StatusFile, Encoding.UTF8))
                if (l.StartsWith(key + "=", StringComparison.Ordinal)) return l.Substring(key.Length + 1);
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return "";
    }

    // рушій закінчив вбудування і приймає вміст
    public static bool Ready
    {
        get { string st = StatusValue("state"); return st == "ok" || st == "unverified"; }
    }

    // рушій - DLL, яку окремим процесом запускає системний rundll32 (окремого .exe немає)
    public static string DllName { get { return Environment.Is64BitOperatingSystem ? "NeoBarWall64.dll" : "NeoBarWall32.dll"; } }
    static string DllPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DllName); } }

    // rundll32 тієї самої розрядності, що й DLL (32-бітний NeoBar на 64-бітній системі бачить System32 як SysWOW64 - там потрібен Sysnative)
    static string Rundll()
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess) return Path.Combine(Path.Combine(win, "Sysnative"), "rundll32.exe");
        return Path.Combine(Environment.SystemDirectory, "rundll32.exe");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetShortPathName(string path, StringBuilder buf, int len);

    // рядок запуску для rundll32: "<dll>",RunW <аргументи>. Кома в шляху ламає розбір rundll32 - тоді беремо коротке ім'я 8.3
    static string RundllArgs(string a)
    {
        string dll = DllPath;
        if (dll.IndexOf(',') >= 0)
        {
            StringBuilder sb = new StringBuilder(520);
            if (GetShortPathName(dll, sb, 520) > 0) dll = sb.ToString();
        }
        return "\"" + dll + "\",RunW " + a;
    }
    public static string StatusFile { get { return Path.Combine(Path.GetTempPath(), "NeoBarWall.status"); } }

    public static bool Alive
    {
        get
        {
            try { return proc != null && !proc.HasExited; }
            catch { return false; }
        }
    }

    static IntPtr Window { get { return Win.FindWindow("NeoBarWallIpc", null); } }

    // запустити (або лишити запущений, якщо налаштування ті самі)
    public static bool Start()
    {
        Error = "";
        // спосіб вбудування і виведення рушій обирає сам: перевіряє кадр за пікселями екрана, перший варіант - Progman + DirectX flip
        string a = "--owner " + Owner.ToInt64() + " --status \"" + StatusFile + "\" --all " + (WallCfg.AllMonitors ? 1 : 0);
        if (SbLog.GroupOn(0)) a += " --log \"" + SbLog.FileOf(0) + "\"";
        if (Alive && a == args) return true;
        Stop();
        KillStray();
        if (!File.Exists(DllPath))
        {
            Error = L.T("Немає ") + DllName + L.T(" поруч із NeoBar.exe - розпакуйте архів повністю");
            return false;
        }
        try { File.Delete(StatusFile); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        args = a;
        queue.Clear();
        string how;
        try { proc = Launch(a, out how); }
        catch (Exception ex)
        {
            proc = null;
            Error = L.T("Рушій шпалер не запустився: ") + ex.Message;
            SbLog.W(SbLog.Panel, "шпалери: " + Error);
            return false;
        }
        SbLog.W(SbLog.Panel, "шпалери: рушій " + DllName + " запущено " + how + " - " + a);
        waitUntil = DateTime.Now.AddSeconds(6);
        return proc != null;
    }

    // запуск рушія: зі звичайними правами, навіть коли NeoBar від адміністратора
    static Process Launch(string a, out string how)
    {
        Process p = null;
        how = "";
        string host = Rundll(), ra = RundllArgs(a);
        if (Elevated.IsAdmin)
        {
            p = StartAsShellChild(host, ra);
            how = p != null ? L.T("зі звичайними правами (через Провідник)") : "";
        }
        if (p == null)
        {
            ProcessStartInfo psi = new ProcessStartInfo(host, ra);
            psi.UseShellExecute = false;
            psi.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
            p = Process.Start(psi);
            if (how.Length == 0) how = Elevated.IsAdmin ? L.T("від адміністратора (звичайні права отримати не вдалося)") : L.T("зі звичайними правами");
        }
        return p;
    }

    // Процес, батьком якого вважається Провідник: отримує його маркер доступу (звичайні права, не адміністратор)
    static Process StartAsShellChild(string exe, string a)
    {
        IntPtr hp = IntPtr.Zero, list = IntPtr.Zero, val = IntPtr.Zero;
        try
        {
            uint pid;
            IntPtr shell = Win.GetShellWindow();
            if (shell == IntPtr.Zero || Win.GetWindowThreadProcessId(shell, out pid) == 0 || pid == 0) return null;
            hp = Win.OpenProcess(0x0080, false, pid);                      // PROCESS_CREATE_PROCESS
            if (hp == IntPtr.Zero) return null;
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            list = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) return null;
            val = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(val, hp);
            if (!UpdateProcThreadAttribute(list, 0, (IntPtr)0x00020000, val, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) return null;   // PROC_THREAD_ATTRIBUTE_PARENT_PROCESS
            STARTUPINFOEX si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
            si.lpAttributeList = list;
            PROCESS_INFORMATION pi;
            StringBuilder cmd = new StringBuilder("\"" + exe + "\" " + a);
            if (!CreateProcess(exe, cmd, IntPtr.Zero, IntPtr.Zero, false, 0x00080000, IntPtr.Zero, AppDomain.CurrentDomain.BaseDirectory, ref si, out pi))   // EXTENDED_STARTUPINFO_PRESENT
            {
                SbLog.W(SbLog.Panel, "шпалери: запуск через Провідник не вдався, код " + Marshal.GetLastWin32Error());
                return null;
            }
            Win.CloseHandle(pi.hThread);
            Win.CloseHandle(pi.hProcess);
            try { return Process.GetProcessById(pi.dwProcessId); } catch { return null; }
        }
        catch (Exception ex) { SbLog.W(SbLog.Panel, "шпалери: запуск через Провідник: " + ex.Message); return null; }
        finally
        {
            if (list != IntPtr.Zero) { DeleteProcThreadAttributeList(list); Marshal.FreeHGlobal(list); }
            if (val != IntPtr.Zero) Marshal.FreeHGlobal(val);
            if (hp != IntPtr.Zero) Win.CloseHandle(hp);
        }
    }

    public static void Send(string cmd)
    {
        IntPtr w = Window;
        if (w != IntPtr.Zero && queue.Count == 0 && Post(w, cmd)) return;
        queue.Add(cmd);
        // у Timer інтервал за замовчуванням уже 100, тому обробник вішаємо за окремим прапорцем, а не за інтервалом
        if (!flushHooked)
        {
            flushHooked = true;
            flush.Interval = 100;
            flush.Tick += delegate { Flush(); };
        }
        flush.Start();
    }

    // рушій щойно запущено - чекаємо його вікна для команд
    static void Flush()
    {
        IntPtr w = Window;
        if (w != IntPtr.Zero)
        {
            int sent = 0;
            for (int i = 0; i < queue.Count; i++)
            {
                if (!Post(w, queue[i])) break;      // рушій не відповів - команду лишаємо й пробуємо знову
                sent++;
            }
            queue.RemoveRange(0, sent);
            if (queue.Count == 0) { flush.Stop(); return; }
        }
        if (!Alive || DateTime.Now > waitUntil)
        {
            flush.Stop();
            queue.Clear();
            Error = Alive ? L.T("Рушій шпалер не відповідає") : L.T("Рушій шпалер завершився одразу після запуску");
            SbLog.W(SbLog.Panel, "шпалери: " + Error);
        }
    }

    static bool Post(IntPtr w, string cmd)
    {
        IntPtr mem = Marshal.StringToHGlobalUni(cmd);
        try
        {
            COPYDATASTRUCT cd = new COPYDATASTRUCT();
            cd.dwData = (IntPtr)0x5342;
            cd.cbData = cmd.Length * 2;
            cd.lpData = mem;
            IntPtr r;
            IntPtr ok = SendMessageTimeout(w, 0x004A, Owner, ref cd, 0x0002, 3000, out r);   // WM_COPYDATA, SMTO_ABORTIFHUNG
            return ok != IntPtr.Zero;
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    // забутий екземпляр рушія (після збою чи старої версії) тримає своє чорне вікно на робочому столі - закриваємо чужі
    static void KillStray()
    {
        try
        {
            int mine = -1;
            try { if (proc != null && !proc.HasExited) mine = proc.Id; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            // чужий екземпляр знаходимо за його вікном команд; зупиняємо лише процес rundll32 або старий NeoBarWall*.exe
            IntPtr w = Window;
            if (w != IntPtr.Zero)
            {
                uint pid;
                Win.GetWindowThreadProcessId(w, out pid);
                if (pid != 0 && pid != (uint)mine)
                    try
                    {
                        using (Process p = Process.GetProcessById((int)pid))
                        {
                            string n = p.ProcessName.ToLowerInvariant();
                            if (n == "rundll32" || n.StartsWith("neobarwall")) { p.Kill(); p.WaitForExit(1500); }
                        }
                    }
                    catch (Exception skipEx) { SbLog.Skip(skipEx); }
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    public static void Stop()
    {
        flush.Stop();
        queue.Clear();
        IntPtr w = Window;
        if (w != IntPtr.Zero) Post(w, "quit");
        if (proc != null)
        {
            try { if (!proc.WaitForExit(1500)) proc.Kill(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            try { proc.Dispose(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            proc = null;
        }
        args = "";
    }

    // що не гаразд у рушія (порожньо, якщо все добре): помилка або пауза
    public static string Problem()
    {
        try
        {
            if (!File.Exists(StatusFile)) return "";
            string err = StatusValue("error");
            string state = StatusValue("state");
            if (!Alive && state != "error") return "";
            string s = "";
            if (err.Length > 0) s = L.T("Помилка: ") + err;
            else if (state == "unverified") s = L.T("Кадр на екрані не підтверджено");
            if (StatusValue("paused") == "1")
                s += (s.Length > 0 ? "\n" : "") + L.T("Пауза: поверх робочого столу розгорнуте вікно, або комп'ютер працює від батареї");
            return s;
        }
        catch { return ""; }
    }

    public static bool Failed
    {
        get
        {
            try
            {
                if (!File.Exists(StatusFile)) return false;
                foreach (string l in File.ReadAllLines(StatusFile, Encoding.UTF8)) if (l == "state=error") return true;
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
            return false;
        }
    }
}

// ----------------------------------------------------------------------
//  Керування шпалерами: режим, слайд-шоу, пауза, перезапуск рушія
// ----------------------------------------------------------------------
static class WallMgr
{
    static readonly Timer tick = new Timer();
    static List<string> files = new List<string>();
    static int idx = -1;
    static DateTime nextAt = DateTime.MaxValue;
    static readonly Random rnd = new Random();
    static bool paused, inited, engine;
    static string msg = "", liveCmd;
    static bool fadeIn;                     // перше зображення після Apply(true) з'являється з анімацією
    static int restarts, failChecks, ackTries;
    static DateTime restartWindow = DateTime.MinValue;
    public static string CurrentFile = "";

    // у стаціонарного комп'ютера батареї немає - «пауза від батареї» йому не потрібна
    public static bool HasBattery
    {
        get
        {
            try { return (SystemInformation.PowerStatus.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0; }
            catch { return false; }
        }
    }

    // стан для налаштувань: що зараз показується + що каже рушій
    public static string Status
    {
        get
        {
            string s = msg;
            if (engine)
            {
                string e = WallEngine.Error.Length > 0 ? L.T("Помилка: ") + WallEngine.Error : WallEngine.Problem();
                if (e.Length > 0) s += (s.Length > 0 ? "\n" : "") + e;
            }
            return s;
        }
    }

    static void Init()
    {
        if (inited) return;
        inited = true;
        tick.Interval = 1500;
        tick.Tick += delegate { try { Tick(); } catch (Exception ex) { Util.ErrorLog(ex); } };
        SystemEvents.DisplaySettingsChanged += delegate { try { if (engine) WallEngine.Send("refresh"); } catch (Exception skipEx) { SbLog.Skip(skipEx); } };
    }

    // застосувати поточні WallCfg (після запуску й після «Застосувати» в налаштуваннях)
    public static void Apply() { Apply(false); }

    // animate: нові шпалери змінюють поточні обраним переходом (якщо рушій уже показує зображення), а не різко
    public static void Apply(bool animate)
    {
        Init();
        fadeIn = animate && engine && WallEngine.Alive && CurrentFile.Length > 0 && liveCmd != null && !liveCmd.StartsWith("video");
        Stop(false);
        msg = "";
        CurrentFile = "";
        ReturnOriginal();           // якщо раніше NeoBar ставив шпалери засобами Windows - повернути ті, що були
        try
        {
            switch (WallCfg.Mode)
            {
                case 1: StartImage(); break;
                case 2: StartSlides(); break;
                case 3: StartLive(); break;
                default: break;         // «Системні»: NeoBar нічого не показує поверх шпалер Windows
            }
        }
        catch (Exception ex) { Util.ErrorLog(ex); msg = "Помилка: " + ex.Message; }
        if (!engine) WallEngine.Stop();
        SbLog.W(SbLog.Panel, "шпалери: режим " + WallCfg.Mode + (msg.Length > 0 ? " - " + msg.Replace("\n", "; ") : ""));
    }

    // запам'ятати шпалери Windows перед тим, як поставити свої засобами Windows
    static void RememberOriginal()
    {
        WallOrig.Load();
        if (WallOrig.Image.Length > 0) return;
        try
        {
            WallOrig.Image = CurrentSystem();
            WallOrig.Style = Convert.ToString(Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallpaperStyle", "10"));
            WallOrig.Tile = Convert.ToString(Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "TileWallpaper", "0"));
            WallOrig.MakeCopy();
            WallOrig.Save();
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    // шлях, який зараз записано як шпалери Windows (навіть якщо файлу вже немає)
    static string CurrentSystem()
    {
        try
        {
            StringBuilder sb = new StringBuilder(520);
            if (Win.SystemParametersInfo(0x73, 520, sb, 0)) return sb.ToString();
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return "";
    }

    static bool SamePath(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    static bool Under(string file, string dir)
    {
        if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(dir)) return false;
        try
        {
            string d = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            return Path.GetFullPath(file).StartsWith(d, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // шпалери Windows зараз ті, що поставив NeoBar (а не змінені потім користувачем)?
    static bool SystemIsOurs()
    {
        string cur = CurrentSystem();
        if (cur.Length == 0) return false;
        if (SamePath(cur, WallOrig.Set)) return true;
        if (Under(cur, WallLib.Root)) return true;
        if (SamePath(cur, Util.ResolvePath(WallCfg.Image ?? ""))) return true;
        return false;
    }

    // повернути шпалери Windows, якщо їх змінював NeoBar; якщо користувач змінив їх сам - лише забути запис
    static void ReturnOriginal()
    {
        if (!WallOrig.Has) return;
        if (!SystemIsOurs()) { WallOrig.Clear(); return; }
        if (WallOrig.Image.Length == 0) { WallOrig.Clear(); return; }
        try
        {
            Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallpaperStyle", WallOrig.Style.Length > 0 ? WallOrig.Style : "10");
            Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "TileWallpaper", WallOrig.Tile.Length > 0 ? WallOrig.Tile : "0");
            Win.SystemParametersInfoSet(0x14, 0, File.Exists(WallOrig.Image) ? WallOrig.Image : (WallOrig.Copy.Length > 0 && File.Exists(WallOrig.CopyPath) ? WallOrig.CopyPath : ""), 3);
            SbLog.W(SbLog.Panel, "шпалери: повернуто шпалери Windows " + Path.GetFileName(WallOrig.Image));
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        WallOrig.Clear();
    }

    static bool SetSystem(string file)
    {
        string f = Util.ResolvePath(file ?? "");
        if (f.Length == 0 || !File.Exists(f)) { msg = f.Length == 0 ? L.T("Оберіть зображення в галереї") : L.T("Зображення не знайдено"); return false; }
        RememberOriginal();
        WallOrig.Set = f;
        WallOrig.Save();
        string[] st = { "10", "6", "2", "0" };
        Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallpaperStyle", st[Math.Max(0, Math.Min(3, WallCfg.Fit))]);
        Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "TileWallpaper", "0");
        bool ok = Win.SystemParametersInfoSet(0x14, 0, f, 3);
        if (!ok) msg = L.T("Windows не прийняла зображення");
        CurrentFile = f;
        return ok;
    }

    static bool StartEngine()
    {
        engine = true;
        restarts = 0;
        ackTries = 0;
        failChecks = 40;          // кілька десятків секунд після запуску дивимось, чи рушій не повідомив про помилку
        if (WallEngine.Start()) return true;
        msg = WallEngine.Error;
        return false;
    }

    static void StartImage()
    {
        string f = Util.ResolvePath(WallCfg.Image ?? "");
        if (f.Length == 0 || !File.Exists(f)) { msg = f.Length == 0 ? L.T("Оберіть зображення в галереї") : L.T("Зображення не знайдено"); return; }
        if (!StartEngine())
        {
            engine = false;
            SetSystem(f);
            msg += L.T(" - зображення поставлено засобами Windows; після виходу з NeoBar повернуться попередні шпалери");
            return;
        }
        liveCmd = "image\t" + f + "\t" + WallCfg.Fit + "\t" + (fadeIn && WallCfg.Fx > 0 ? WallCfg.Fx - 1 : -1) + "\t" + WallCfg.TransMs;
        fadeIn = false;
        WallEngine.Send(liveCmd);
        CurrentFile = f;
        tick.Start();
    }

    static void StartSlides()
    {
        string dir = WallLib.Dir(WallLib.Slideshow);
        files = new List<string>();
        try
        {
            if (Directory.Exists(dir))
                foreach (string f in Directory.GetFiles(dir))
                    if (WallLib.Accepts(WallLib.Slideshow, f)) files.Add(f);
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        if (files.Count == 0) { msg = L.T("У галереї слайд-шоу немає зображень - додайте їх"); return; }
        files.Sort(StringComparer.CurrentCultureIgnoreCase);
        if (!StartEngine())
        {
            engine = false;
            msg += L.T(" - слайд-шоу йде засобами Windows, без анімацій");
        }
        idx = -1;
        Next(fadeIn);
        fadeIn = false;
        tick.Start();
        string head = L.T("Слайдів: ") + files.Count;
        msg = msg.Length > 0 ? head + "\n" + msg : head;
    }

    static void StartLive()
    {
        string f = Util.ResolvePath(WallCfg.Video ?? "");
        if (f.Length == 0 || !File.Exists(f)) { msg = f.Length == 0 ? L.T("Оберіть відео в галереї") : L.T("Файл не знайдено"); return; }
        if (!StartEngine()) return;
        liveCmd = "video\t" + f + "\t" + WallCfg.Fit + "\t" + (WallCfg.Mute ? 1 : 0);
        WallEngine.Send(liveCmd);
        CurrentFile = f;
        double sec = MediaInfo.Seconds(f);
        msg = sec > 0 ? L.T("Тривалість: ") + MediaInfo.Format(sec) : "";
        tick.Start();
    }

    // наступне зображення слайд-шоу
    public static void Next(bool animate)
    {
        if (WallCfg.Mode != 2 || files.Count == 0) return;
        for (int tries = 0; tries < Math.Min(files.Count, 8); tries++)
        {
            idx = WallCfg.Shuffle && files.Count > 1 ? Pick() : (idx + 1) % files.Count;
            string f = files[idx];
            if (!File.Exists(f)) continue;
            if (engine)
            {
                liveCmd = "image\t" + f + "\t" + WallCfg.Fit + "\t" + (animate && WallCfg.Fx > 0 ? WallCfg.Fx - 1 : -1) + "\t" + WallCfg.TransMs;
                WallEngine.Send(liveCmd);
                CurrentFile = f;
                break;
            }
            if (SetSystem(f)) break;
        }
        nextAt = DateTime.Now.AddMinutes(Math.Max(1, WallCfg.Interval));
        SbLog.W(SbLog.Panel, "шпалери: слайд " + Path.GetFileName(CurrentFile) + (engine ? "" : " (засобами Windows)"));
    }

    static int Pick()
    {
        int n;
        do n = rnd.Next(files.Count); while (n == idx);
        return n;
    }

    static void Tick()
    {
        if (WallCfg.Mode < 1) { tick.Stop(); return; }
        // рушій не зміг показати кадр жодним способом - зображення й слайд-шоу показуємо засобами Windows
        if (engine && WallCfg.Mode <= 2 && failChecks > 0 && WallEngine.Alive && (--failChecks >= 0) && WallEngine.Failed)
        {
            SbLog.W(SbLog.Panel, "шпалери: рушій не зміг вбудуватися - далі засобами Windows");
            WallEngine.Stop();
            engine = false;
            msg = L.T("Рушій шпалер не зміг показати кадр на екрані - шпалери поставлено засобами Windows") + (WallCfg.Mode == 2 ? L.T(", без анімацій") : "") +
                  L.T(". Після виходу з NeoBar повернуться попередні. Подробиці - у журналі панелі (розділ «Діагностика», рядки [шпалери]).");
            if (WallCfg.Mode == 2) Next(false);
            else if (CurrentFile.Length > 0) SetSystem(CurrentFile);
            if (WallCfg.Mode == 1) tick.Stop();
            return;
        }
        // рушій упав або відеокарту скинуто - перезапустити (не більше 3 разів за 5 хвилин)
        if (engine && !WallEngine.Alive)
        {
            if (DateTime.Now > restartWindow) { restartWindow = DateTime.Now.AddMinutes(5); restarts = 0; }
            if (restarts < 3)
            {
                restarts++;
                SbLog.W(SbLog.Panel, "шпалери: рушій зупинився - перезапуск " + restarts);
                if (WallEngine.Start())
                {
                    if (liveCmd != null) WallEngine.Send(liveCmd.Replace("\t" + (WallCfg.Fx - 1) + "\t" + WallCfg.TransMs, "\t-1\t" + WallCfg.TransMs));
                    if (paused) WallEngine.Send("pause\t1");
                }
            }
        }
        // рушій живий і готовий, але вмісту не підтвердив (команда загубилась або прийшла надто рано) - повторюємо
        if (engine && liveCmd != null && WallEngine.Alive && ackTries < 6 && WallEngine.Ready && WallEngine.StatusValue("content").Length == 0 && WallEngine.StatusValue("error").Length == 0)
        {
            ackTries++;
            SbLog.W(SbLog.Panel, "шпалери: рушій не підтвердив вміст - повторюю команду (" + ackTries + ")");
            WallEngine.Send(liveCmd);
        }
        if (WallCfg.Mode == 1) return;          // нерухоме зображення: рушій і так нічого не малює, пауза не потрібна
        bool cover = WallCfg.PauseCovered && (NeoBar.Paused || Nat.FullscreenBusy() || Nat.ForegroundCovers(false, IntPtr.Zero));
        bool battery = WallCfg.PauseBattery && WallMgr.HasBattery && SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
        bool p = cover || battery;
        if (p != paused)
        {
            paused = p;
            if (engine) WallEngine.Send("pause\t" + (p ? 1 : 0));
        }
        if (!paused && WallCfg.Mode == 2 && DateTime.Now >= nextAt) Next(true);
    }

    // Провідник перезапустився: рушій сам вбудовується заново; якщо його немає - застосувати все спочатку
    public static void Restart()
    {
        if (WallCfg.Mode < 1) return;
        if (engine && WallEngine.Alive) WallEngine.Send("refresh");
        else Apply();
    }

    static void Stop(bool quitEngine)
    {
        tick.Stop();
        if (quitEngine) WallEngine.Stop();
        files.Clear();
        paused = false;
        engine = false;
        liveCmd = null;
    }


    // вихід із NeoBar: рушій зупинити - під ним і так шпалери Windows; якщо NeoBar ставив їх сам - повернути попередні
    public static void Shutdown()
    {
        bool had = engine;
        Stop(true);
        try
        {
            ReturnOriginal();
            if (had)
            {
                IntPtr pm = Win.FindWindow("Progman", null);
                if (pm != IntPtr.Zero) Win.RedrawWindow(pm, IntPtr.Zero, IntPtr.Zero, 0x1 | 0x4 | 0x80 | 0x100);     // INVALIDATE | ERASE | ALLCHILDREN | UPDATENOW
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }
}
