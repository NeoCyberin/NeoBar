using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Win32;

// ======================================================================
//  «NeoBar.exe --uninstall» - запускає встановлювач перед видаленням файлів.
//  Прибирає все, що NeoBar сам додав у систему: автозапуск, пункти контекстного меню,
//  шар Vulkan, обгортки в теках ігор (лише свої, за хешем), завдання «NeoBar Admin».
//  Профілі й налаштування тут не чіпаються - про них питає сам встановлювач.
// ======================================================================
static class Uninstall
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static int Run()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                if (k != null) k.DeleteValue("NeoBar", false);
        }
        catch { }
        try { ShellMenu.RemoveAll(); } catch { }
        VkValues(RegistryHive.CurrentUser);
        try
        {
            foreach (string dir in ProfileDirs()) Wrappers(Path.Combine(dir, "games.ini"));
        }
        catch { }
        // завдання з найвищими правами й шар Vulkan у HKLM прибирає лише адміністратор
        if (TaskExists() || AppLock.Applied() || VkValues(RegistryHive.LocalMachine, true))
        {
            if (Elevated.IsAdmin) Elevate(null);
            else
            {
                string err;
                string outFile = Elevated.Run("uninstall", "", out err);
                if (outFile != null)
                    for (int i = 0; i < 300; i++)           // до 30 с
                    {
                        bool done;
                        Elevated.ReadOut(outFile, out done);
                        if (done) break;
                        Thread.Sleep(100);
                    }
                try { if (outFile != null) File.Delete(outFile); } catch { }
            }
        }
        return 0;
    }

    // підвищена частина (помічник «--helper uninstall»)
    public static int Elevate(StreamWriter w)
    {
        if (TaskExists()) AdminTask.Configure(false, false, "", w);
        else if (AppLock.Applied()) { AppLock.RemoveLauncher(); AppLock.Remove(w); }
        VkValues(RegistryHive.LocalMachine);
        return 0;
    }

    static bool TaskExists()
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", "/query /tn \"" + AdminTask.Name + "\"");
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                return p.ExitCode == 0;
            }
        }
        catch { return false; }
    }

    // записи шару Vulkan, що ведуть у теку NeoBar; onlyCheck - лише сказати, чи такі є
    static bool VkValues(RegistryHive hive, bool onlyCheck = false)
    {
        bool found = false;
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using (RegistryKey b = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey k = b.OpenSubKey(@"SOFTWARE\Khronos\Vulkan\ImplicitLayers", !onlyCheck))
                {
                    if (k == null) continue;
                    foreach (string n in k.GetValueNames())
                    {
                        string f = Path.GetFileName(n);
                        if (!f.StartsWith("NeoBarVkLayer", StringComparison.OrdinalIgnoreCase) || !n.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
                        found = true;
                        if (!onlyCheck) try { k.DeleteValue(n, false); } catch { }
                    }
                }
            }
            catch { }
        }
        return found;
    }

    static List<string> ProfileDirs()
    {
        List<string> l = new List<string>();
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles");
        if (Directory.Exists(root)) l.AddRange(Directory.GetDirectories(root));
        return l;
    }

    // обгортки, які NeoBar поклав у теки ігор (games.ini: game2=…|Dir(3)|…|Wrap(13)|ExeDir(14))
    static void Wrappers(string ini)
    {
        if (!File.Exists(ini)) return;
        foreach (string raw in File.ReadAllLines(ini, Encoding.UTF8))
        {
            if (!raw.StartsWith("game2=")) continue;
            string[] p = raw.Substring(6).Split('|');
            if (p.Length < 14 || (p[13] != "d3d8" && p[13] != "ddraw")) continue;
            string d = p.Length > 14 && p[14].Length > 0 ? p[14] : p[3];
            if (d.Length > 0) GameWrap.RemoveAt(d, p[13]);
        }
    }
}
