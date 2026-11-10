using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

// ======================================================================
//  Дії над процесами й службами для вкладки «Процеси»: завершення з урахуванням зв'язків, перезапуск,
//  зупинка й запуск служб, тип запуску служби. Усе - лише за підтвердженням людини.
//  Критичні процеси Windows (без яких система падає) і сама NeoBar не завершуються ніколи.
//  Тип запуску служби змінюється через журнал «Безпеки», тож його можна відкотити (Безпека → Система).
// ======================================================================
sealed class KillPlan
{
    public ProcRow Target;
    public string Path = "", Cmd = "", Name = "";
    public int Pid;
    public long Created;
    public bool HasWin, WasElevated;
    public readonly List<ProcRow> Kids = new List<ProcRow>();          // від найглибших до найближчих
    public readonly List<string> Svcs = new List<string>();            // служби цього процесу (зупинити через диспетчер служб)
    public readonly List<string> SvcNames = new List<string>();        // їхні назви для людини
    public readonly List<string> Dependents = new List<string>();      // залежні служби (зупиняться разом)
    public string Block = "";                                          // чому не можна
}

static class ProcAct
{
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr tok, int cls, out int val, int len, out int ret);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenService(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool EnumDependentServices(IntPtr svc, uint state, IntPtr buf, int size, out int need, out int count);

    // без цих процесів Windows зупиняється (синій екран) або втрачає вхід у систему
    static readonly string[] Critical = { "system", "registry", "secure system", "memory compression", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "lsaiso.exe", "lsm.exe" };
    // служби, від яких залежить решта системи
    static readonly string[] CriticalSvc = { "rpcss", "dcomlaunch", "rpceptmapper", "lsm", "brokerinfrastructure", "power", "plugplay", "samss", "coremessagingregistrar", "systemeventsbroker", "eventlog", "profsvc", "usermanager", "gpsvc", "statereposervice" };

    public static bool IsCriticalSvc(string name) { return Array.IndexOf(CriticalSvc, (name ?? "").ToLowerInvariant()) >= 0; }

    static bool CriticalFlag(int pid)
    {
        try
        {
            IntPtr h = Win.OpenProcess(0x1000, false, (uint)pid);
            if (h == IntPtr.Zero) return false;
            try { int v, r; return Win.NtQueryInformationProcess(h, 29, out v, 4, out r) == 0 && v != 0; }     // ProcessBreakOnTermination
            finally { Win.CloseHandle(h); }
        }
        catch { return false; }
    }

    public static bool IsElevated(int pid)
    {
        IntPtr h = Win.OpenProcess(0x1000, false, (uint)pid);
        if (h == IntPtr.Zero) return true;              // не відкривається - вважаємо, що з підвищеними правами
        IntPtr tok;
        try
        {
            if (!Win.OpenProcessToken(h, 0x0008, out tok)) return true;
            try { int v, r; return GetTokenInformation(tok, 20, out v, 4, out r) && v != 0; }     // TokenElevation
            finally { Win.CloseHandle(tok); }
        }
        finally { Win.CloseHandle(h); }
    }

    // чому процес не можна завершувати (або "")
    public static string Blocked(ProcRow p)
    {
        if (p == null) return L.T("Процес уже завершився.");
        if (p.Pid == Process.GetCurrentProcess().Id || (p.Name.Equals("NeoBar.exe", StringComparison.OrdinalIgnoreCase) && p.Path.Length > 0 && string.Equals(p.Path, System.Windows.Forms.Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
            return L.T("Це сама NeoBar - закривайте її через меню профілю («Вийти з NeoBar»).");
        if (p.Pid <= 4 || Array.IndexOf(Critical, p.Name.ToLowerInvariant()) >= 0 || CriticalFlag(p.Pid))
            return L.T("Критичний процес Windows: після його завершення система зупиниться (синій екран). Його не можна завершувати.");
        foreach (SvcInfo s in p.Svcs)
            if (IsCriticalSvc(s.Name)) return L.F("У цьому процесі працює ключова служба Windows «{0}» - без неї система перестане працювати. Його не можна завершувати.", s.Display);
        return "";
    }

    // аналіз: дочірні процеси (усе дерево), служби процесу і залежні від них служби
    public static KillPlan Plan(ProcRow p)
    {
        KillPlan k = new KillPlan();
        k.Target = p;
        if (p == null) { k.Block = Blocked(p); return k; }
        k.Pid = p.Pid; k.Name = p.Name; k.Path = p.Path; k.Cmd = p.Cmd; k.Created = p.Created; k.HasWin = p.HasWin;
        k.Block = Blocked(p);
        if (k.Block.Length > 0) return k;
        k.WasElevated = IsElevated(p.Pid);
        Dictionary<int, List<ProcRow>> kids = new Dictionary<int, List<ProcRow>>();
        foreach (ProcRow r in ProcMon.Rows)
        {
            if (r.Pid == p.Pid) continue;
            List<ProcRow> l;
            if (!kids.TryGetValue(r.Parent, out l)) { l = new List<ProcRow>(); kids[r.Parent] = l; }
            l.Add(r);
        }
        Walk(p, kids, k.Kids, new HashSet<int>(), 0);
        // дочірні критичні процеси не чіпаємо (буває, що їх запустив звичайний процес - напр., conhost)
        k.Kids.RemoveAll(delegate (ProcRow r) { return Blocked(r).Length > 0; });
        foreach (SvcInfo s in p.Svcs)
        {
            if (s.State != 4 && s.State != 2) continue;
            k.Svcs.Add(s.Name);
            k.SvcNames.Add(s.Display);
            foreach (string d in DependentsOf(s.Name)) if (!k.Dependents.Contains(d)) k.Dependents.Add(d);
        }
        return k;
    }

    static void Walk(ProcRow p, Dictionary<int, List<ProcRow>> kids, List<ProcRow> outl, HashSet<int> seen, int depth)
    {
        List<ProcRow> l;
        if (depth > 32 || !kids.TryGetValue(p.Pid, out l)) return;
        foreach (ProcRow c in l)
        {
            // PID міг перейти до нового процесу: дочірній не може бути старшим за батька
            if (c.Created < p.Created || !seen.Add(c.Pid)) continue;
            Walk(c, kids, outl, seen, depth + 1);
            outl.Add(c);            // спершу найглибші
        }
    }

    // активні служби, що залежать від цієї (у порядку зупинки)
    public static List<string> DependentsOf(string name)
    {
        List<string> r = new List<string>();
        IntPtr scm = Win.OpenSCManager(null, null, 0x0001);
        if (scm == IntPtr.Zero) return r;
        IntPtr s = OpenService(scm, name, 0x0008);              // SERVICE_ENUMERATE_DEPENDENTS
        IntPtr b = IntPtr.Zero;
        try
        {
            if (s == IntPtr.Zero) return r;
            int need, count;
            EnumDependentServices(s, 1, IntPtr.Zero, 0, out need, out count);
            if (need <= 0) return r;
            b = Marshal.AllocHGlobal(need);
            if (!EnumDependentServices(s, 1, b, need, out need, out count)) return r;
            int sz = IntPtr.Size * 2 + 28;
            sz = (sz + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size;
            for (int i = 0; i < count; i++)
            {
                IntPtr np = Marshal.ReadIntPtr(new IntPtr(b.ToInt64() + (long)i * sz));
                string n = Marshal.PtrToStringUni(np);
                if (!string.IsNullOrEmpty(n)) r.Add(n);
            }
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
        finally
        {
            if (b != IntPtr.Zero) Marshal.FreeHGlobal(b);
            if (s != IntPtr.Zero) Win.CloseServiceHandle(s);
            Win.CloseServiceHandle(scm);
        }
        return r;
    }

    // ---------- виконання (не з потоку інтерфейсу) ----------
    static bool Alive(int pid, long created)
    {
        foreach (ProcRow r in ProcMon.Rows) if (r.Pid == pid && r.Created == created) return true;
        return false;
    }

    // примусово; true - завершено
    static bool Kill(int pid, int waitMs, out string err)
    {
        err = "";
        IntPtr h = Win.OpenProcess(0x0001 | 0x00100000, false, (uint)pid);       // TERMINATE | SYNCHRONIZE
        if (h == IntPtr.Zero) { err = Marshal.GetLastWin32Error() == 5 ? L.T("немає доступу") : L.T("не відкривається"); return false; }
        try
        {
            if (!TerminateProcess(h, 1)) { err = L.T("немає доступу (захищений процес)"); return false; }
            return Win.WaitForSingleObject(h, (uint)waitMs) == 0;
        }
        finally { Win.CloseHandle(h); }
    }

    // спершу ввічливо: закрити вікна програми, як хрестиком
    static bool CloseNicely(int pid, int waitMs)
    {
        List<IntPtr> wins = new List<IntPtr>();
        Win.EnumWindows(delegate (IntPtr w, IntPtr l)
        {
            uint p;
            Win.GetWindowThreadProcessId(w, out p);
            if (p == pid && Win.IsWindowVisible(w) && Win.GetWindow(w, 4) == IntPtr.Zero) wins.Add(w);
            return true;
        }, IntPtr.Zero);
        if (wins.Count == 0) return false;
        foreach (IntPtr w in wins) Win.PostMessage(w, 0x0010, IntPtr.Zero, IntPtr.Zero);         // WM_CLOSE
        IntPtr h = Win.OpenProcess(0x00100000, false, (uint)pid);
        if (h == IntPtr.Zero) return false;
        try { return Win.WaitForSingleObject(h, (uint)waitMs) == 0; }
        finally { Win.CloseHandle(h); }
    }

    static string Net { get { return Path.Combine(Environment.SystemDirectory, "net.exe"); } }

    // завершити за планом: зв'язки (залежні служби, дочірні процеси), служби процесу, потім сам процес. Повертає звіт.
    public static string End(KillPlan k, bool withLinks)
    {
        int ok = 0;
        List<string> bad = new List<string>();
        string err;
        if (withLinks)
            foreach (ProcRow c in k.Kids)
            {
                if (Kill(c.Pid, 2000, out err)) ok++;
                else if (err.Length > 0) bad.Add(c.Name + " (" + err + ")");
            }
        if (k.Svcs.Count > 0)
        {
            // служби - через диспетчер служб: «net stop /y» зупиняє й залежні; права адміністратора за потреби запитає Windows
            List<FixOp> ops = new List<FixOp>();
            foreach (string s in k.Svcs) ops.Add(new FixOp("RUNQ", Net, "stop \"" + s + "\" /y"));
            string e = SecFix.Run(ops, true);
            if (e.Length > 0) bad.Add(e);
            ProcMon.RefreshServicesSoon();
            for (int i = 0; i < 30 && ProcessExists(k.Pid, k.Created); i++) Thread.Sleep(200);
        }
        if (ProcessExists(k.Pid, k.Created))
        {
            if (k.HasWin && k.Svcs.Count == 0 && CloseNicely(k.Pid, 3000)) ok++;
            else if (Kill(k.Pid, 3000, out err)) ok++;
            else bad.Add(k.Name + (err.Length > 0 ? " (" + err + ")" : ""));
        }
        else ok++;
        if (bad.Count > 0) return L.F("Завершено: {0}. Не вдалося: {1}", ok, string.Join(", ", bad.ToArray()));
        return k.Svcs.Count > 0 ? L.F("Служби зупинено, процес «{0}» завершено.", k.Name) : (ok > 1 ? L.F("Завершено «{0}» і пов'язані процеси ({1}).", k.Name, ok - 1) : L.F("«{0}» завершено.", k.Name));
    }

    // перезапуск: служби - зупинити й запустити знову; програма - завершити й запустити тією ж командою
    public static string Restart(KillPlan k)
    {
        if (k.Svcs.Count > 0)
        {
            List<FixOp> ops = new List<FixOp>();
            foreach (string s in k.Svcs) ops.Add(new FixOp("RUNQ", Net, "stop \"" + s + "\" /y"));
            foreach (string s in k.Svcs) ops.Add(new FixOp("RUNQ", Net, "start \"" + s + "\""));
            // залежні служби, що працювали, теж повертаємо
            for (int i = k.Dependents.Count - 1; i >= 0; i--) ops.Add(new FixOp("RUNQ", Net, "start \"" + k.Dependents[i] + "\""));
            string e = SecFix.Run(ops, true);
            ProcMon.RefreshServicesSoon();
            return e.Length > 0 ? e : L.F("Службу перезапущено: {0}.", string.Join(", ", k.SvcNames.ToArray()));
        }
        if (k.Path.Length == 0 || !File.Exists(k.Path)) return L.T("Невідомий шлях до програми - перезапустити не вийде.");
        string rep = End(k, true);
        if (ProcessExists(k.Pid, k.Created)) return rep;
        // програма могла перезапуститися сама (як Провідник) - тоді другу копію не запускаємо
        DateTime t0 = DateTime.UtcNow;
        for (int i = 0; i < 12; i++)
        {
            Thread.Sleep(250);
            if (Running(k.Path, t0)) return L.F("«{0}» перезапущено.", k.Name);
        }
        string args = ProcMon.ArgsOf(k.Cmd, k.Path);
        try
        {
            if (Elevated.IsAdmin && !k.WasElevated)
            {
                // працювала зі звичайними правами - так і запускаємо (а не з правами NeoBar)
                if (UserLaunch.Run(k.Path, args).Length == 0) return L.T("Процес завершено, але запустити знову не вдалося.");
            }
            else
            {
                ProcessStartInfo psi = new ProcessStartInfo(k.Path, args);
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Path.GetDirectoryName(k.Path);
                Process.Start(psi).Dispose();
            }
        }
        catch (Exception ex) { return L.T("Процес завершено, але запустити знову не вдалося: ") + ex.Message; }
        return L.F("«{0}» перезапущено.", k.Name);
    }

    static bool ProcessExists(int pid, long created)
    {
        try
        {
            using (Process p = Process.GetProcessById(pid))
                return !p.HasExited && (created == 0 || p.StartTime.ToUniversalTime().ToFileTimeUtc() / 10000000 == created / 10000000);
        }
        catch (ArgumentException) { return false; }
        catch { return Alive(pid, created); }
    }

    static bool Running(string path, DateTime after)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        foreach (Process p in Process.GetProcessesByName(name))
        {
            try { if (p.StartTime.ToUniversalTime() >= after.AddSeconds(-1)) return true; }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
            finally { p.Dispose(); }
        }
        return false;
    }

    // ---------- служби: запуск, зупинка, тип запуску ----------
    public static string SvcStop(string name) { string e = SecFix.Run(new List<FixOp> { new FixOp("RUNQ", Net, "stop \"" + name + "\" /y") }, true); ProcMon.RefreshServicesSoon(); return e; }
    public static string SvcStart(string name) { string e = SecFix.Run(new List<FixOp> { new FixOp("RUNQ", Net, "start \"" + name + "\"") }, true); ProcMon.RefreshServicesSoon(); return e; }

    // 0 - вручну, 1 - автоматично, 2 - автоматично із затримкою, 3 - вимкнено; -1 - інше (завантажувальна, системна)
    public static int StartMode(string name)
    {
        try
        {
            using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey k = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name))
            {
                if (k == null) return -1;
                int st = Convert.ToInt32(k.GetValue("Start", 3));
                int dl = Convert.ToInt32(k.GetValue("DelayedAutostart", 0));
                if (st == 2) return dl != 0 ? 2 : 1;
                if (st == 3) return 0;
                if (st == 4) return 3;
                return -1;
            }
        }
        catch { return -1; }
    }

    static readonly string[] ScMode = { "demand", "auto", "delayed-auto", "disabled" };
    static readonly int[] RegStart = { 3, 2, 2, 4 };

    // змінити тип запуску (із записом у журнал «Безпеки» для відкату)
    public static string SetStart(string name, string display, int mode)
    {
        int old = StartMode(name);
        if (old == mode) return "";
        if (old < 0) return L.T("Для цієї служби тип запуску тут не змінюється.");
        string key = @"SYSTEM\CurrentControlSet\Services\" + name;
        string sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
        Fix fx = new Fix().Reg(true, key, "Start", "dword", RegStart[mode].ToString());
        fx.Reg(true, key, "DelayedAutostart", "dword", mode == 2 ? "1" : "0");
        fx.Ops.Add(new FixOp("RUNQ", sc, "config \"" + name + "\" start= " + ScMode[mode]));
        fx.UndoRun.Add(new FixOp("RUNQ", sc, "config \"" + name + "\" start= " + ScMode[old]));
        string[] names = { L.T("вручну"), L.T("автоматично"), L.T("автоматично із затримкою"), L.T("вимкнено") };
        return SecFix.Apply(fx, L.F("Тип запуску служби «{0}»: {1}", display, names[mode]));
    }
}
