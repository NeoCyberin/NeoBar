using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using System.Windows.Forms;

// ======================================================================
//  «Завжди від імені адміністратора»: завдання Планувальника з найвищими правами.
//  Підтвердження UAC потрібне лише раз - під час створення завдання.
// ======================================================================
static class AdminTask
{
    public const string Name = "NeoBar Admin";

    // виконується в підвищеному помічнику
    public static int Configure(bool on, bool autostart, string user, StreamWriter w)
    {
        if (!on)
        {
            int c = Run("schtasks.exe", "/delete /tn \"" + Name + "\" /f", w);
            w.WriteLine(c == 0 ? L.T("Завдання видалено.") : L.T("Завдання не знайдено."));
            return 0;
        }
        if (user.Length == 0) user = Environment.UserDomainName + "\\" + Environment.UserName;
        string exe = Application.ExecutablePath;
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
            "<Actions Context=\"Author\"><Exec><Command>" + X(exe) + "</Command><Arguments>--restarted</Arguments><WorkingDirectory>" + X(Path.GetDirectoryName(exe)) + "</WorkingDirectory></Exec></Actions>\r\n" +
            "</Task>";
        string file = Path.Combine(Path.GetTempPath(), "neobar-task.xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        int code = Run("schtasks.exe", "/create /tn \"" + Name + "\" /xml \"" + file + "\" /f", w);
        try { File.Delete(file); } catch { }
        w.WriteLine(code == 0 ? L.T("Готово: NeoBar запускатиметься з правами адміністратора без запиту.") : L.T("Не вдалося створити завдання (код ") + code + ").");
        return code;
    }

    static string X(string s) { return SecurityElement.Escape(s ?? ""); }

    static int Run(string exe, string args, StreamWriter w)
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
        catch { }
        return false;
    }
}
