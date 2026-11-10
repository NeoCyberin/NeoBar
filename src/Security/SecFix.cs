using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

// ======================================================================
//  Виправлення знахідок «Безпеки». Кожне виправлення - набір простих дій (значення реєстру, права доступу
//  до файла, вміст файла, запуск системного засобу). Перед виконанням запам'ятовується попередній стан,
//  і в журналі з'являється запис, який можна скасувати - так нічого не міняється безповоротно.
//  Дії для всієї системи виконує помічник NeoBar з правами адміністратора (один запит UAC на виправлення).
// ======================================================================
sealed class FixOp
{
    // REG: корінь (LM/CU), ключ, назва, тип (dword/qword/sz/expand/bin/multi/del), значення
    // ACL: шлях, SDDL (лише список доступу)
    // FILE: шлях, вміст (Base64)
    // RUN: програма, параметри (RUNQ - те саме, але код завершення не важливий: «зупинити службу», яка вже стоїть)
    // MOVEX: звідки, куди - перенесення файла з перетворенням XOR (ізольований файл не запуститься випадково)
    // DEL: файл, що видаляється (копія в ізоляції)
    public string Kind;
    public string[] A;
    public FixOp(string kind, params string[] a) { Kind = kind; A = a; }

    public string Line()
    {
        StringBuilder b = new StringBuilder(Kind);
        foreach (string s in A) b.Append('\t').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? "")));
        return b.ToString();
    }

    public static FixOp Parse(string line)
    {
        string[] p = line.Split('\t');
        if (p.Length < 1 || p[0].Length == 0) return null;
        string[] a = new string[p.Length - 1];
        for (int i = 1; i < p.Length; i++) a[i - 1] = Encoding.UTF8.GetString(Convert.FromBase64String(p[i]));
        return new FixOp(p[0], a);
    }
}

sealed class Fix
{
    public string Button = L.T("Виправити"), Confirm = "", Done = "";
    public bool Admin;                                      // потрібні права адміністратора
    public readonly List<FixOp> Ops = new List<FixOp>();
    public readonly List<FixOp> UndoRun = new List<FixOp>();  // зворотні команди для RUN (решту відкату NeoBar складає сам)

    public Fix Reg(bool lm, string key, string name, string type, string value) { Ops.Add(new FixOp("REG", lm ? "LM" : "CU", key, name, type, value)); if (lm) Admin = true; return this; }
}

sealed class FixEntry
{
    public string Title = "", When = "";
    public bool Admin;
    public List<FixOp> Undo = new List<FixOp>();
}

static class SecFix
{
    static string JournalPath { get { return Path.Combine(Prof.Root, "security-fixes.txt"); } }
    public static readonly List<FixEntry> Journal = new List<FixEntry>();
    static bool loaded;

    public static void Load()
    {
        if (loaded) return;
        loaded = true;
        Journal.Clear();
        try
        {
            if (!File.Exists(JournalPath)) return;
            FixEntry cur = null;
            foreach (string line in File.ReadAllLines(JournalPath, Encoding.UTF8))
            {
                if (line.StartsWith("#FIX\t"))
                {
                    string[] p = line.Split('\t');
                    cur = new FixEntry();
                    cur.When = p.Length > 1 ? p[1] : "";
                    cur.Admin = p.Length > 2 && p[2] == "1";
                    cur.Title = p.Length > 3 ? Encoding.UTF8.GetString(Convert.FromBase64String(p[3])) : "";
                    Journal.Add(cur);
                }
                else if (cur != null && line.Length > 0) { FixOp o = FixOp.Parse(line); if (o != null) cur.Undo.Add(o); }
            }
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    static void Save()
    {
        try
        {
            StringBuilder b = new StringBuilder();
            foreach (FixEntry e in Journal)
            {
                b.Append("#FIX\t").Append(e.When).Append('\t').Append(e.Admin ? "1" : "0").Append('\t').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(e.Title))).Append("\r\n");
                foreach (FixOp o in e.Undo) b.Append(o.Line()).Append("\r\n");
            }
            File.WriteAllText(JournalPath, b.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    // ---------- стан до виправлення (для відкату) ----------
    static RegistryKey Root(string r) { return RegistryKey.OpenBaseKey(r == "LM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64); }

    static FixOp RegNow(FixOp o)
    {
        string[] a = o.A;
        try
        {
            using (RegistryKey root = Root(a[0]))
            using (RegistryKey k = root.OpenSubKey(a[1]))
            {
                object v = k == null ? null : k.GetValue(a[2], null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (v == null) return new FixOp("REG", a[0], a[1], a[2], "del", "");
                RegistryValueKind kind = k.GetValueKind(a[2]);
                switch (kind)
                {
                    case RegistryValueKind.DWord: return new FixOp("REG", a[0], a[1], a[2], "dword", ((int)v).ToString());
                    case RegistryValueKind.QWord: return new FixOp("REG", a[0], a[1], a[2], "qword", ((long)v).ToString());
                    case RegistryValueKind.ExpandString: return new FixOp("REG", a[0], a[1], a[2], "expand", (string)v);
                    case RegistryValueKind.MultiString: return new FixOp("REG", a[0], a[1], a[2], "multi", string.Join("\0", (string[])v));
                    case RegistryValueKind.Binary: return new FixOp("REG", a[0], a[1], a[2], "bin", Convert.ToBase64String((byte[])v));
                    default: return new FixOp("REG", a[0], a[1], a[2], "sz", v.ToString());
                }
            }
        }
        catch { return new FixOp("REG", a[0], a[1], a[2], "del", ""); }
    }

    public static string SddlOf(string path)
    {
        FileSystemSecurity s = Directory.Exists(path) ? (FileSystemSecurity)Directory.GetAccessControl(path, AccessControlSections.Access) : File.GetAccessControl(path, AccessControlSections.Access);
        return s.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    }

    static List<FixOp> MakeUndo(Fix f)
    {
        List<FixOp> u = new List<FixOp>();
        foreach (FixOp o in f.Ops)
        {
            if (o.Kind == "REG") u.Insert(0, RegNow(o));
            else if (o.Kind == "ACL") u.Insert(0, new FixOp("ACL", o.A[0], SddlOf(o.A[0])));
            else if (o.Kind == "FILE") u.Insert(0, new FixOp("FILE", o.A[0], File.Exists(o.A[0]) ? Convert.ToBase64String(File.ReadAllBytes(o.A[0])) : ""));
            else if (o.Kind == "MOVEX") u.Insert(0, new FixOp("MOVEX", o.A[1], o.A[0]));
        }
        foreach (FixOp o in f.UndoRun) u.Insert(0, o);
        return u;
    }

    // ---------- виконання ----------
    // ops у цьому процесі; w - звіт для людини
    public static int Exec(List<FixOp> ops, TextWriter w)
    {
        int bad = 0;
        foreach (FixOp o in ops)
        {
            try
            {
                switch (o.Kind)
                {
                    case "REG": SetReg(o.A); break;
                    case "ACL":
                    {
                        bool dir = Directory.Exists(o.A[0]);
                        if (dir) { DirectorySecurity s = new DirectorySecurity(); s.SetSecurityDescriptorSddlForm(o.A[1], AccessControlSections.Access); Directory.SetAccessControl(o.A[0], s); }
                        else { FileSecurity s = new FileSecurity(); s.SetSecurityDescriptorSddlForm(o.A[1], AccessControlSections.Access); File.SetAccessControl(o.A[0], s); }
                        break;
                    }
                    case "FILE":
                    {
                        string tmp = o.A[0] + ".neobar-new";
                        File.WriteAllBytes(tmp, Convert.FromBase64String(o.A[1]));
                        if (File.Exists(o.A[0])) File.Replace(tmp, o.A[0], null); else File.Move(tmp, o.A[0]);
                        break;
                    }
                    case "MOVEX": MoveX(o.A[0], o.A[1]); break;
                    case "DEL": if (File.Exists(o.A[0])) { File.SetAttributes(o.A[0], FileAttributes.Normal); File.Delete(o.A[0]); } break;
                    case "RUNQ":
                    {
                        ProcessStartInfo psi = new ProcessStartInfo(o.A[0], o.A[1]);
                        psi.UseShellExecute = false; psi.CreateNoWindow = true;
                        using (Process p = Process.Start(psi)) p.WaitForExit(60 * 1000);
                        break;
                    }
                    case "RUN":
                    {
                        ProcessStartInfo psi = new ProcessStartInfo(o.A[0], o.A[1]);
                        psi.UseShellExecute = false; psi.CreateNoWindow = true;
                        using (Process p = Process.Start(psi))
                        {
                            if (!p.WaitForExit(15 * 60 * 1000)) { w.WriteLine(Path.GetFileName(o.A[0]) + L.T(": не завершилось вчасно.")); bad++; break; }
                            if (p.ExitCode != 0 && p.ExitCode != 3010) { w.WriteLine(Path.GetFileName(o.A[0]) + L.T(": код ") + p.ExitCode + "."); bad++; }
                        }
                        break;
                    }
                }
            }
            catch (UnauthorizedAccessException) { w.WriteLine(L.T("Недостатньо прав: ") + (o.A.Length > 1 ? o.A[1] : o.Kind)); bad++; }
            catch (Exception ex) { w.WriteLine(ex.Message); bad++; }
        }
        return bad == 0 ? 0 : 1;
    }

    // перенесення з XOR 0xA5: копія в ізоляції - не програма, її не запустить ні подвійний клік, ні автозапуск
    static void MoveX(string src, string dst)
    {
        if (!File.Exists(src)) throw new FileNotFoundException(L.T("Файл не знайдено: ") + src);
        if (File.Exists(dst)) throw new IOException(L.T("На місці вже є файл: ") + dst);
        string dir = Path.GetDirectoryName(dst);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        string part = dst + ".part";
        using (FileStream i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (FileStream o = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            byte[] b = new byte[1 << 20];
            int n;
            while ((n = i.Read(b, 0, b.Length)) > 0)
            {
                for (int k = 0; k < n; k++) b[k] ^= 0xA5;
                o.Write(b, 0, n);
            }
        }
        try
        {
            File.SetAttributes(src, FileAttributes.Normal);
            File.Delete(src);              // зайнятий (програма ще працює) - копію прибираємо, оригінал лишається
        }
        catch { try { File.Delete(part); } catch (Exception skipEx) { SbLog.Skip(skipEx); } throw; }
        File.Move(part, dst);
    }

    static void SetReg(string[] a)
    {
        using (RegistryKey root = Root(a[0]))
        {
            if (a[3] == "del")
            {
                using (RegistryKey k = root.OpenSubKey(a[1], true)) if (k != null) k.DeleteValue(a[2], false);
                return;
            }
            using (RegistryKey k = root.CreateSubKey(a[1]))
                switch (a[3])
                {
                    case "dword": k.SetValue(a[2], int.Parse(a[4]), RegistryValueKind.DWord); break;
                    case "qword": k.SetValue(a[2], long.Parse(a[4]), RegistryValueKind.QWord); break;
                    case "expand": k.SetValue(a[2], a[4], RegistryValueKind.ExpandString); break;
                    case "multi": k.SetValue(a[2], a[4].Split('\0'), RegistryValueKind.MultiString); break;
                    case "bin": k.SetValue(a[2], Convert.FromBase64String(a[4]), RegistryValueKind.Binary); break;
                    default: k.SetValue(a[2], a[4], RegistryValueKind.String); break;
                }
        }
    }

    // точка входу помічника з правами адміністратора: --helper secfix out файл-з-діями
    public static int RunFile(string file, string hash, TextWriter w)
    {
        List<FixOp> ops = new List<FixOp>();
        byte[] raw = File.ReadAllBytes(file);
        try { File.Delete(file); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        if (!ElevHash.Same(raw, hash)) { w.WriteLine(L.T("Файл завдання змінено після створення - нічого не виконано.")); return 1; }
        foreach (string l in Encoding.UTF8.GetString(raw).Split('\n')) { FixOp o = FixOp.Parse(l.TrimEnd('\r')); if (o != null) ops.Add(o); }
        return Exec(ops, w);
    }

    // виконати (з підвищенням, якщо треба) і дочекатися; "" - успіх
    static string Do(List<FixOp> ops, bool admin)
    {
        bool started;
        return Do(ops, admin, out started);
    }

    // started - дії справді почали виконуватися (не скасовано запит UAC)
    static string Do(List<FixOp> ops, bool admin, out bool started)
    {
        started = true;
        if (!admin || Elevated.IsAdmin)
        {
            StringWriter sw = new StringWriter();
            return Exec(ops, sw) == 0 ? "" : sw.ToString().Trim();
        }
        string opsFile = Path.Combine(Path.GetTempPath(), "neobar-secfix-" + DateTime.Now.Ticks + ".txt");
        StringBuilder b = new StringBuilder();
        foreach (FixOp o in ops) b.Append(o.Line()).Append("\r\n");
        byte[] raw = new UTF8Encoding(false).GetBytes(b.ToString());
        File.WriteAllBytes(opsFile, raw);
        string err;
        string outFile = Elevated.Run("secfix", "\"" + opsFile + "\" " + ElevHash.Of(raw), out err);
        if (outFile == null) { started = false; try { File.Delete(opsFile); } catch (Exception skipEx) { SbLog.Skip(skipEx); } return err; }
        DateTime until = DateTime.Now.AddMinutes(16);
        while (DateTime.Now < until)
        {
            Thread.Sleep(300);
            string t = "";
            try
            {
                if (File.Exists(outFile))
                    using (FileStream fs = new FileStream(outFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (StreamReader r = new StreamReader(fs, Encoding.UTF8)) t = r.ReadToEnd();
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
            int k = t.IndexOf("<<END ", StringComparison.Ordinal);
            if (k < 0) continue;
            try { File.Delete(outFile); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            string code = t.Substring(k + 6).TrimEnd('>', ' ', '\r', '\n');
            return code == "0" ? "" : (t.Substring(0, k).Trim().Length > 0 ? t.Substring(0, k).Trim() : L.T("Не вдалося."));
        }
        return L.T("Помічник не відповів.");
    }

    // виконати й повернути відкат; на невдачі вже зроблене відкочується, щоб не лишилося половини змін
    public static string ApplyRaw(Fix f, out List<FixOp> undo)
    {
        try { undo = MakeUndo(f); }
        catch (Exception ex) { undo = null; return L.T("Не вдалося запам'ятати поточний стан, тож нічого не змінено: ") + ex.Message; }
        bool started;
        string err = Do(f.Ops, f.Admin, out started);
        if (err.Length > 0)
        {
            if (!started) return err;
            try { Do(undo, f.Admin); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            return err + L.T(" Зміни відкочено.");
        }
        return "";
    }

    public static string Run(List<FixOp> ops, bool admin) { return Do(ops, admin); }

    // застосувати виправлення й записати відкат у журнал (виклик - не з потоку інтерфейсу: UAC і засоби Windows чекають)
    public static string Apply(Fix f, string title)
    {
        Load();
        List<FixOp> undo;
        string err = ApplyRaw(f, out undo);
        if (err.Length > 0) return err;
        FixEntry e = new FixEntry();
        e.Title = title; e.When = DateTime.Now.ToString("dd.MM.yyyy HH:mm"); e.Admin = f.Admin; e.Undo = undo;
        lock (Journal) { Journal.Insert(0, e); Save(); }
        return "";
    }

    public static string Undo(FixEntry e)
    {
        string err = Do(e.Undo, e.Admin);
        if (err.Length > 0) return err;
        lock (Journal) { Journal.Remove(e); Save(); }
        return "";
    }

    // ---------- готові виправлення ----------
    // права «запис» для звичайних користувачів прибираються, «читання й запуск» лишаються
    public static Fix StripWrite(string path, bool dir)
    {
        FileSystemSecurity s = dir ? (FileSystemSecurity)Directory.GetAccessControl(path, AccessControlSections.Access) : File.GetAccessControl(path, AccessControlSections.Access);
        s.SetAccessRuleProtection(true, true);              // успадковані правила стають власними, щоб їх можна було змінити
        string sddl0 = s.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        s = dir ? (FileSystemSecurity)new DirectorySecurity() : new FileSecurity();
        s.SetSecurityDescriptorSddlForm(sddl0, AccessControlSections.Access);
        string[] weak = { "S-1-5-32-545", "S-1-5-11", "S-1-1-0", "S-1-5-4" };
        FileSystemRights wr = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes |
                              FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        bool changed = false;
        foreach (FileSystemAccessRule r in s.GetAccessRules(true, false, typeof(SecurityIdentifier)))
        {
            if (r.AccessControlType != AccessControlType.Allow || Array.IndexOf(weak, r.IdentityReference.Value) < 0 || (r.FileSystemRights & (wr | (FileSystemRights)0x50000000)) == 0) continue;
            s.RemoveAccessRuleSpecific(r);
            FileSystemRights keep = r.FileSystemRights & ~wr & ~(FileSystemRights)0x10000000 & ~(FileSystemRights)0x40000000;   // GENERIC_ALL / GENERIC_WRITE теж прибираємо
            if ((r.FileSystemRights & (FileSystemRights)0x10000000) != 0) keep |= FileSystemRights.ReadAndExecute;
            if (keep != 0) s.AddAccessRule(new FileSystemAccessRule(r.IdentityReference, keep | FileSystemRights.Synchronize, r.InheritanceFlags, r.PropagationFlags, AccessControlType.Allow));
            changed = true;
        }
        if (!changed) return null;
        Fix f = new Fix();
        f.Admin = true;
        f.Ops.Add(new FixOp("ACL", path, s.GetSecurityDescriptorSddlForm(AccessControlSections.Access)));
        return f;
    }

    public static Fix Run(string exe, string args, string undoExe, string undoArgs)
    {
        Fix f = new Fix();
        f.Admin = true;
        f.Ops.Add(new FixOp("RUN", exe, args));
        if (undoExe != null) f.UndoRun.Add(new FixOp("RUN", undoExe, undoArgs));
        return f;
    }
}
