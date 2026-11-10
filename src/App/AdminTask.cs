using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using System.Windows.Forms;

// ======================================================================
//  «Завжди від імені адміністратора»: завдання Планувальника з найвищими правами.
//  Підтвердження UAC потрібне лише раз - під час створення завдання. Завдання запускає
//  копію-запускач у Program Files, а тека програми захищається від підміни (див. AppLock).
// ======================================================================
static class AdminTask
{
    public const string Name = "NeoBar Admin";

    // виконується в підвищеному помічнику
    public static int Configure(bool on, bool autostart, string user, TextWriter w)
    {
        if (w == null) w = TextWriter.Null;
        if (!on)
        {
            int c = Run("schtasks.exe", "/delete /tn \"" + Name + "\" /f", w);
            w.WriteLine(c == 0 ? L.T("Завдання видалено.") : L.T("Завдання не знайдено."));
            AppLock.RemoveLauncher();
            AppLock.Remove(w);
            return 0;
        }
        if (user.Length == 0) user = Environment.UserDomainName + "\\" + Environment.UserName;
        // спершу захист теки: без нього завдання стало б обхідним шляхом повз UAC
        if (!AppLock.Apply(user, w))
        {
            w.WriteLine(L.T("Режим не ввімкнено: тека програми не підтримує права доступу (наприклад, флешка FAT) або їх не вдалося змінити."));
            return 1;
        }
        try { AppLock.InstallLauncher(); }
        catch (Exception ex)
        {
            w.WriteLine(L.F("Не вдалося поставити запускач у Program Files: {0}", ex.Message));
            AppLock.Remove(w);
            return 1;
        }
        string exe = AppLock.LauncherExe;
        string dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
        string trig = autostart
            ? "<LogonTrigger><Enabled>true</Enabled><UserId>" + X(user) + "</UserId><Delay>PT10S</Delay></LogonTrigger>"
            : "";
        string xml =
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
            L.T("<RegistrationInfo><Description>Запуск панелі NeoBar з правами адміністратора (FPS у іграх, обслуговування дисків)</Description></RegistrationInfo>\r\n") +
            "<Triggers>" + trig + "</Triggers>\r\n" +
            "<Principals><Principal id=\"Author\"><UserId>" + X(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
            "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
            "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>false</StartWhenAvailable>" +
            "<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>4</Priority></Settings>\r\n" +
            "<Actions Context=\"Author\"><Exec><Command>" + X(exe) + "</Command><Arguments>" + X("--launch \"" + dir + "\"") + "</Arguments><WorkingDirectory>" + X(AppLock.LauncherDir) + "</WorkingDirectory></Exec></Actions>\r\n" +
            "</Task>";
        string file = Path.Combine(Path.GetTempPath(), "neobar-task.xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        int code = Run("schtasks.exe", "/create /tn \"" + Name + "\" /xml \"" + file + "\" /f", w);
        try { File.Delete(file); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        w.WriteLine(code == 0 ? L.T("Готово: NeoBar запускатиметься з правами адміністратора без запиту.") : L.T("Не вдалося створити завдання (код ") + code + ").");
        return code;
    }

    static string X(string s) { return SecurityElement.Escape(s ?? ""); }

    // запускач (копія в Program Files, уже з правами адміністратора): тека програми досі захищена - запускаємо панель
    public static int LaunchFrom(string dir)
    {
        try
        {
            dir = Path.GetFullPath(dir).TrimEnd('\\', '/');
            string exe = Path.Combine(dir, "NeoBar.exe");
            if (!File.Exists(exe) || !AppLock.Trusted(dir, exe)) return 2;
            ProcessStartInfo psi = new ProcessStartInfo(exe, "--restarted");
            psi.UseShellExecute = false;
            psi.WorkingDirectory = dir;
            using (Process.Start(psi)) { }
            return 0;
        }
        catch { return 1; }
    }

    // підвищена панель під час запуску: завдання старого зразка (вело прямо на NeoBar.exe) або знятий захист -
    // переробляємо; інакше лише оновлюємо копію-запускач після оновлення програми
    public static void Refresh()
    {
        try
        {
            string xml = Query();
            if (xml == null) return;
            bool auto = xml.IndexOf("<LogonTrigger", StringComparison.OrdinalIgnoreCase) >= 0;
            if (xml.IndexOf(AppLock.LauncherExe, StringComparison.OrdinalIgnoreCase) < 0 || !AppLock.Applied())
                Configure(true, auto, "", null);
            else AppLock.InstallLauncher();
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    public static bool Exists() { return Query() != null; }

    static string Query()
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", "/query /tn \"" + Name + "\" /xml");
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                return p.ExitCode == 0 ? o : null;
            }
        }
        catch { return null; }
    }

    // без прав: попросити підвищеного помічника (op - relock/unlock) і дочекатися (до 60 с)
    public static bool Helper(string op)
    {
        string err;
        string outFile = Elevated.Run(op, "", out err);
        if (outFile == null) return false;
        bool done = false;
        for (int i = 0; i < 600 && !done; i++)
        {
            Elevated.ReadOut(outFile, out done);
            if (!done) System.Threading.Thread.Sleep(100);
        }
        try { File.Delete(outFile); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return done;
    }

    static int Run(string exe, string args, TextWriter w)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                if (p.ExitCode != 0 && err.Trim().Length > 0 && w != null) w.WriteLine(err.Trim());
                return p.ExitCode;
            }
        }
        catch (Exception ex) { if (w != null) w.WriteLine(ex.Message); return -1; }
    }

    // запуск через завдання (без UAC); true - успішно
    public static bool Launch()
    {
        return Run("schtasks.exe", "/run /tn \"" + Name + "\"", null) == 0;
    }

    // у neobar.ini увімкнено «@admin yes»?
    public static bool Wanted()
    {
        try
        {
            string ini = Prof.PeekLastFile("neobar.ini");
            if (!File.Exists(ini)) return false;
            foreach (string l in File.ReadAllLines(ini, Encoding.UTF8))
            {
                string t = l.Trim().ToLowerInvariant();
                if (t.StartsWith("@admin")) return t.EndsWith("yes") || t.EndsWith("true") || t.EndsWith("1");
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return false;
    }
}
