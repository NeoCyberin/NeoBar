using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

// ======================================================================
//  Захист теки програми, коли ввімкнено «Завжди від імені адміністратора».
//  Завдання Планувальника запускає NeoBar з найвищими правами без запиту UAC. Якби тека лишалась
//  відкритою на запис, будь-яка програма користувача могла б підмінити exe чи покласти поруч DLL -
//  і отримати права адміністратора. Тому:
//   * власник теки - адміністратори, корінь і програмні підтеки для користувача «лише читання»;
//     теки з даними (профілі, журнали, кеш…) лишаються на запис;
//   * завдання запускає не саму теку, а копію-запускач у Program Files (туди пише лише адміністратор);
//     запускач перевіряє, що тека програми досі захищена (її могли перейменувати й підкласти іншу), і
//     лише тоді запускає NeoBar з правами.
//  Вимкнули режим - права знову спадкові, запускач видаляється.
// ======================================================================
static class AppLock
{
    // теки з даними: сюди NeoBar пише навіть тоді, коли працює без прав адміністратора;
    // коду з них не завантажується
    static readonly string[] DataDirs = { "profiles", "logs", "cache", "icons", "links", "sounds", "wallpapers", "lang" };

    static string Root { get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/'); } }

    static SecurityIdentifier UserSid(string user)
    {
        if (!string.IsNullOrEmpty(user))
            try { return (SecurityIdentifier)new NTAccount(user).Translate(typeof(SecurityIdentifier)); } catch { }
        return WindowsIdentity.GetCurrent().User;
    }

    static readonly SecurityIdentifier Admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
    static readonly SecurityIdentifier SystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
    // «Права власника»: без цього власник файлу (користувач, що встановив програму) міг би змінити права
    static readonly SecurityIdentifier OwnerRights = new SecurityIdentifier("S-1-3-4");
    const InheritanceFlags All = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    // увімкнути захист (true - вдалося)
    public static bool Apply(string user, TextWriter w)
    {
        try
        {
            SecurityIdentifier me = UserSid(user);
            DirectorySecurity ds = new DirectorySecurity();
            ds.SetAccessRuleProtection(true, false);
            ds.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, All, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, All, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, All, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(OwnerRights, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, All, PropagationFlags.None, AccessControlType.Allow));
            ds.SetOwner(Admins);
            Directory.SetAccessControl(Root, ds);      // спадкові права дочірніх файлів перераховуються самі
            // файли з явними (не спадковими) правами: скидаємо до спадкових, інакше вони лишились би на запис
            Reset(Root);
            foreach (string d in DataDirs)
            {
                string p = Path.Combine(Root, d);
                try
                {
                    Directory.CreateDirectory(p);
                    if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) continue;
                    DirectorySecurity s = Directory.GetAccessControl(p);
                    // сама тека: створювати файли й підтеки, але не видаляти чи перейменовувати її (підміна посиланням)
                    s.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.ReadAndExecute | FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories |
                        FileSystemRights.Synchronize, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
                    // усе всередині - звичайна зміна
                    s.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.Modify | FileSystemRights.Synchronize, All, PropagationFlags.InheritOnly, AccessControlType.Allow));
                    Directory.SetAccessControl(p, s);
                }
                catch (Exception ex) { if (w != null) w.WriteLine(d + ": " + ex.Message); }
            }
            if (w != null) w.WriteLine(L.T("Тека програми захищена від підміни: змінювати файли NeoBar тепер може лише адміністратор."));
            return true;
        }
        catch (Exception ex)
        {
            Util.ErrorLog(ex);
            if (w != null) w.WriteLine(L.F("Не вдалося захистити теку програми: {0}", ex.Message));
            return false;
        }
    }

    // зняти захист: усе знову успадковує права батьківської теки (як після встановлення)
    public static bool Remove(TextWriter w)
    {
        try
        {
            SecurityIdentifier me = WindowsIdentity.GetCurrent().User;
            DirectorySecurity ds = Directory.GetAccessControl(Root);
            ds.SetAccessRuleProtection(false, false);
            foreach (FileSystemAccessRule r in ds.GetAccessRules(true, false, typeof(SecurityIdentifier))) ds.RemoveAccessRuleSpecific(r);
            try { ds.SetOwner(me); } catch { }
            Directory.SetAccessControl(Root, ds);
            Reset(Root);
            return true;
        }
        catch (Exception ex)
        {
            Util.ErrorLog(ex);
            if (w != null) w.WriteLine(L.F("Не вдалося повернути права теки програми: {0}", ex.Message));
            return false;
        }
    }

    // захист уже стоїть? (корінь не успадковує права)
    public static bool Applied()
    {
        try { return Directory.GetAccessControl(Root).AreAccessRulesProtected; }
        catch { return false; }
    }

    // явні права на файлах і теках усередині - прибрати (лишаються спадкові)
    static void Reset(string dir)
    {
        foreach (string f in Directory.GetFiles(dir))
            try
            {
                FileSecurity s = File.GetAccessControl(f);
                if (!s.AreAccessRulesProtected && s.GetAccessRules(true, false, typeof(SecurityIdentifier)).Count == 0) continue;
                s.SetAccessRuleProtection(false, false);
                foreach (FileSystemAccessRule r in s.GetAccessRules(true, false, typeof(SecurityIdentifier))) s.RemoveAccessRuleSpecific(r);
                File.SetAccessControl(f, s);
            }
            catch { }
        foreach (string d in Directory.GetDirectories(dir))
        {
            if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;   // посилання на інші теки не чіпаємо
            try
            {
                DirectorySecurity s = Directory.GetAccessControl(d);
                if (s.AreAccessRulesProtected || s.GetAccessRules(true, false, typeof(SecurityIdentifier)).Count > 0)
                {
                    s.SetAccessRuleProtection(false, false);
                    foreach (FileSystemAccessRule r in s.GetAccessRules(true, false, typeof(SecurityIdentifier))) s.RemoveAccessRuleSpecific(r);
                    Directory.SetAccessControl(d, s);
                }
            }
            catch { }
            Reset(d);
        }
    }

    // тимчасова тека підвищеного процесу (оновлення): лише адміністратори пишуть, щоб ніхто не підклав DLL
    public static void Protect(string dir)
    {
        try
        {
            DirectorySecurity ds = new DirectorySecurity();
            ds.SetAccessRuleProtection(true, false);
            ds.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, All, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, All, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, All, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(dir, ds);
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    // ---------- запускач у Program Files ----------

    public static string LauncherDir
    {
        get
        {
            string pf = Environment.GetEnvironmentVariable("ProgramW6432");
            if (string.IsNullOrEmpty(pf)) pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return Path.Combine(pf, "NeoBar");
        }
    }
    public static string LauncherExe { get { return Path.Combine(LauncherDir, "NeoBarAdmin.exe"); } }

    // поставити або оновити копію-запускач (вона ж NeoBar.exe, запущена з ключем --launch)
    public static void InstallLauncher()
    {
        string src = System.Windows.Forms.Application.ExecutablePath;
        Directory.CreateDirectory(LauncherDir);
        bool same = false;
        try
        {
            same = File.Exists(LauncherExe) &&
                System.Diagnostics.FileVersionInfo.GetVersionInfo(LauncherExe).FileVersion == System.Diagnostics.FileVersionInfo.GetVersionInfo(src).FileVersion &&
                new FileInfo(LauncherExe).Length == new FileInfo(src).Length;
        }
        catch { }
        if (!same) File.Copy(src, LauncherExe, true);
    }

    public static void RemoveLauncher()
    {
        try { if (File.Exists(LauncherExe)) File.Delete(LauncherExe); } catch (Exception ex) { Util.ErrorLog(ex); }
        try { if (Directory.Exists(LauncherDir) && Directory.GetFileSystemEntries(LauncherDir).Length == 0) Directory.Delete(LauncherDir); } catch { }
    }

    // тека захищена так, як її залишив Apply: власник - адміністратори або SYSTEM, права не успадковуються,
    // ніхто, крім них, не може писати в теку, змінювати права чи видаляти; сама тека й NeoBar.exe - не посилання
    public static bool Trusted(string dir, string exe)
    {
        try
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return false;
            if ((File.GetAttributes(exe) & FileAttributes.ReparsePoint) != 0) return false;
            DirectorySecurity ds = Directory.GetAccessControl(dir, AccessControlSections.Owner | AccessControlSections.Access);
            SecurityIdentifier owner = (SecurityIdentifier)ds.GetOwner(typeof(SecurityIdentifier));
            if (owner != Admins && owner != SystemSid) return false;
            if (!ds.AreAccessRulesProtected) return false;
            if (!SafeRules(ds.GetAccessRules(true, true, typeof(SecurityIdentifier)))) return false;
            FileSecurity fs = File.GetAccessControl(exe, AccessControlSections.Access);
            return SafeRules(fs.GetAccessRules(true, true, typeof(SecurityIdentifier)));
        }
        catch { return false; }
    }

    const FileSystemRights Danger = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.WriteAttributes | FileSystemRights.Delete |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    static bool SafeRules(AuthorizationRuleCollection rules)
    {
        foreach (FileSystemAccessRule r in rules)
        {
            if (r.AccessControlType != AccessControlType.Allow) continue;
            SecurityIdentifier s = r.IdentityReference as SecurityIdentifier;
            if (s == Admins || s == SystemSid) continue;
            if ((r.FileSystemRights & Danger) != 0) return false;
        }
        return true;
    }
}
