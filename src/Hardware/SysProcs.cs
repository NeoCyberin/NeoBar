using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

// ======================================================================
//  Легкий знімок усіх процесів одним системним викликом (як у Диспетчері завдань): PID, батьківський PID,
//  час запуску, назва, оперативна й виділена пам'ять. Без дескрипторів процесів - працює й поруч з античітами.
//  Для групового аналізу (процес і всі його дочірні) і для сповіщень про процес, що з'їв забагато пам'яті.
// ======================================================================
sealed class SysProc
{
    public int Pid, Parent;
    public long Created;            // FILETIME (UTC)
    public string Name = "";
    public long Ws, Commit;         // приватний робочий набір і виділена пам'ять, байти
}

static class SysProcs
{
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);

    static readonly object gate = new object();
    static IntPtr buf;
    static int bufLen;

    public static List<SysProc> Snapshot()
    {
        List<SysProc> l = new List<SysProc>();
        if (IntPtr.Size != 8) return l;
        lock (gate)
        {
            try
            {
                int ret;
                if (buf == IntPtr.Zero) { bufLen = 1 << 20; buf = Marshal.AllocHGlobal(bufLen); }
                int st = NtQuerySystemInformation(5, buf, bufLen, out ret);
                for (int tries = 0; st == unchecked((int)0xC0000004) && tries < 6; tries++)       // STATUS_INFO_LENGTH_MISMATCH
                {
                    Marshal.FreeHGlobal(buf);
                    bufLen = Math.Max(bufLen * 2, ret + (1 << 16));
                    buf = Marshal.AllocHGlobal(bufLen);
                    st = NtQuerySystemInformation(5, buf, bufLen, out ret);
                }
                if (st != 0) return l;
                int off = 0;
                while (true)
                {
                    IntPtr p = new IntPtr(buf.ToInt64() + off);
                    int next = Marshal.ReadInt32(p, 0);
                    SysProc s = new SysProc();
                    s.Pid = (int)Marshal.ReadInt64(p, 80);
                    if (s.Pid != 0)
                    {
                        int nl = (ushort)Marshal.ReadInt16(p, 56);
                        IntPtr nb = Marshal.ReadIntPtr(p, 64);
                        s.Name = nl > 0 && nb != IntPtr.Zero ? Marshal.PtrToStringUni(nb, nl / 2) : (s.Pid == 4 ? "System" : "");
                        s.Parent = (int)Marshal.ReadInt64(p, 88);
                        s.Created = Marshal.ReadInt64(p, 32);
                        s.Ws = Marshal.ReadInt64(p, 8);
                        s.Commit = Marshal.ReadInt64(p, 200);
                        l.Add(s);
                    }
                    if (next == 0) break;
                    off += next;
                }
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
        return l;
    }

    // процес і всі його нащадки (дочірній має бути запущений пізніше за батька - інакше це чужий процес зі старим PID)
    public static Dictionary<int, SysProc> Tree(List<SysProc> all, int root)
    {
        Dictionary<int, SysProc> byPid = new Dictionary<int, SysProc>();
        foreach (SysProc s in all) byPid[s.Pid] = s;
        Dictionary<int, SysProc> res = new Dictionary<int, SysProc>();
        SysProc r;
        if (byPid.TryGetValue(root, out r)) res[root] = r;
        else return res;
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (SysProc s in all)
            {
                if (res.ContainsKey(s.Pid)) continue;
                SysProc par;
                if (res.TryGetValue(s.Parent, out par) && s.Created >= par.Created) { res[s.Pid] = s; grew = true; }
            }
        }
        return res;
    }
}
