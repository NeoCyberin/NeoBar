using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

// ======================================================================
//  Помічник автооновлення. Це копія NeoBar.exe з тимчасової теки, запущена з ключем --update:
//  основна програма вже закрилася, тож файли можна замінити. Перевіряє контрольну суму,
//  ставить оновлення (встановлювач або розпакований портативний архів) і знову запускає NeoBar.
//  Портативний архів: замінюються лише файли пакета (профілі, кеш і решта ваших даних не
//  чіпаються); якщо щось не вдалося - усе повертається з резервної копії.
// ======================================================================
static class UpdateHelper
{
    // --update <setup|zip> <файл> <sha256> <тека програми> <pid> <1 - підвищений через UAC>
    public static int Run(string[] a)
    {
        if (a.Length < 7) return 2;
        string mode = a[1], file = a[2], sha = a[3], appDir = a[4];
        int pid;
        int.TryParse(a[5], out pid);
        bool elev = a[6] == "1";
        string exe = Path.Combine(appDir, "NeoBar.exe");
        string err = null;
        try
        {
            WaitExit(pid);
            if (!string.Equals(Sha256(file), sha, StringComparison.OrdinalIgnoreCase))
                throw new IOException(L.T("Контрольна сума файлу не збігається."));
            if (mode == "setup") InstallSetup(file);
            else InstallZip(file, appDir);
        }
        catch (Exception ex) { err = ex.Message; }
        if (err != null)
        {
            try { MessageBox.Show(L.F("Оновлення не вдалося: {0}", err), "NeoBar", MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
        }
        try
        {
            if (File.Exists(exe))
            {
                // після підвищення через UAC запускаємо звичайно, від імені користувача (через провідник)
                if (elev) Process.Start("explorer.exe", "\"" + exe + "\"");
                else
                {
                    ProcessStartInfo psi = new ProcessStartInfo(exe);
                    psi.WorkingDirectory = appDir;
                    Process.Start(psi);
                }
            }
        }
        catch { }
        return err == null ? 0 : 1;
    }

    static void WaitExit(int pid)
    {
        if (pid <= 0) return;
        try
        {
            using (Process p = Process.GetProcessById(pid))
                if (!p.WaitForExit(60000)) throw new IOException(L.T("NeoBar не закрився вчасно."));
        }
        catch (ArgumentException) { }          // процесу вже немає
    }

    public static string Sha256(string path)
    {
        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (SHA256 h = SHA256.Create())
        {
            byte[] b = h.ComputeHash(fs);
            StringBuilder sb = new StringBuilder(64);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }

    // рядок «хеш  ім'я файлу» зі SHA256SUMS.txt
    public static string FindHash(string sums, string name)
    {
        foreach (string line in sums.Split('\n'))
        {
            Match m = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
            if (m.Success && string.Equals(m.Groups[2].Value.Trim(), name, StringComparison.OrdinalIgnoreCase)) return m.Groups[1].Value.ToLowerInvariant();
        }
        return null;
    }

    static void InstallSetup(string setup)
    {
        // /SILENT - без запитань, але з вікном прогресу; програма вже закрита, тож нічого закривати не треба
        ProcessStartInfo psi = new ProcessStartInfo(setup, "/SILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL /CLOSEAPPLICATIONS");
        psi.UseShellExecute = false;
        using (Process p = Process.Start(psi))
        {
            if (!p.WaitForExit(10 * 60 * 1000)) throw new IOException(L.T("Встановлювач не завершив роботу вчасно."));
            if (p.ExitCode != 0) throw new IOException(L.F("Встановлювач завершився з кодом {0}.", p.ExitCode));
        }
    }

    sealed class Done { public string Dst, Backup; public bool Existed; }

    // розпаковує архів у тимчасову теку (поруч із ним) і копіює файли пакета в теку програми
    public static void InstallZip(string zip, string appDir)
    {
        string work = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(zip)), "x");
        string backup = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(zip)), "backup");
        try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
        try { if (Directory.Exists(backup)) Directory.Delete(backup, true); } catch { }
        Directory.CreateDirectory(work);
        string workFull = Path.GetFullPath(work).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        List<string> files = new List<string>();
        using (ZipArchive z = ZipFile.OpenRead(zip))
        {
            foreach (ZipArchiveEntry e in z.Entries)
            {
                string n = e.FullName.Replace('\\', '/');
                if (n.EndsWith("/") || e.Name.Length == 0) continue;
                if (!n.StartsWith("NeoBar/", StringComparison.Ordinal)) continue;
                string rel = n.Substring(7);
                string dest = Path.GetFullPath(Path.Combine(work, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (!dest.StartsWith(workFull, StringComparison.OrdinalIgnoreCase)) throw new IOException(L.T("Архів не схожий на пакет NeoBar."));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                e.ExtractToFile(dest, true);
                files.Add(rel.Replace('/', Path.DirectorySeparatorChar));
            }
        }
        if (!files.Contains("NeoBar.exe")) throw new IOException(L.T("Архів не схожий на пакет NeoBar."));

        List<Done> done = new List<Done>();
        try
        {
            // NeoBar.exe - останнім: якщо до нього щось не вдалося, стара програма лишається робочою
            files.Sort(delegate (string x, string y)
            {
                int ax = x == "NeoBar.exe" ? 1 : 0, ay = y == "NeoBar.exe" ? 1 : 0;
                return ax != ay ? ax - ay : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            });
            foreach (string rel in files)
            {
                string src = Path.Combine(work, rel), dst = Path.Combine(appDir, rel);
                Done d = new Done();
                d.Dst = dst;
                d.Existed = File.Exists(dst);
                if (d.Existed)
                {
                    d.Backup = Path.Combine(backup, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(d.Backup));
                    File.Copy(dst, d.Backup, true);
                }
                done.Add(d);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                CopyRetry(src, dst);
            }
        }
        catch
        {
            for (int i = done.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (done[i].Existed) CopyRetry(done[i].Backup, done[i].Dst);
                    else File.Delete(done[i].Dst);
                }
                catch { }
            }
            throw;
        }
    }

    // файл може бути ще зайнятий (антивірус, вікно з контекстного меню) - кілька спроб
    static void CopyRetry(string s, string d)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                if (File.Exists(d)) File.SetAttributes(d, FileAttributes.Normal);
                File.Copy(s, d, true);
                return;
            }
            catch (IOException) { if (i >= 20) throw; }
            catch (UnauthorizedAccessException) { if (i >= 20) throw; }
            Thread.Sleep(500);
        }
    }
}
