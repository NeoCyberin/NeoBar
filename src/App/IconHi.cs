using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// ======================================================================
//  Чіткі іконки програм. Icon.ExtractAssociatedIcon віддає лише 32 px (з стрілкою ярлика для .lnk), і на панелі
//  вони виходять розмитими. Тут іконка береться з самого файлу (exe, dll, ico) у великому розмірі - панель
//  потім стискає її до потрібного. Для ярликів (.lnk) і інтернет-ярликів (.url) спершу читається,
//  де лежить їхня іконка чи ціль, а стрілка ярлика не додається.
//  Нічого не вийшло - повертається null, і панель бере звичайну іконку, як і раніше.
// ======================================================================
static class IconHi
{
    const int Big = 256;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] phicon, uint[] piconid, uint nIcons, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLinkCoClass { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010B-0000-0000-C000-000000000046")]
    interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    // велика іконка файлу або null
    public static Bitmap FromFile(string path)
    {
        try
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".exe": case ".dll": case ".icl": case ".ocx": case ".scr": case ".ico":
                    return Extract(path, 0);
                case ".lnk": return FromLnk(path);
                case ".url": return FromUrl(path);
            }
        }
        catch { }
        return null;
    }

    static Bitmap FromLnk(string path)
    {
        IShellLinkW link = null;
        try
        {
            link = (IShellLinkW)new ShellLinkCoClass();
            ((IPersistFile)link).Load(path, 0);
            StringBuilder sb = new StringBuilder(1024);
            int idx;
            link.GetIconLocation(sb, sb.Capacity, out idx);
            string ic = Environment.ExpandEnvironmentVariables(sb.ToString().Trim());
            Bitmap b = null;
            if (ic.Length > 0 && File.Exists(ic)) b = Extract(ic, idx);
            if (b != null) return b;
            StringBuilder tg = new StringBuilder(1024);
            link.GetPath(tg, tg.Capacity, IntPtr.Zero, 0);
            string t = Environment.ExpandEnvironmentVariables(tg.ToString().Trim());
            if (t.Length > 0 && File.Exists(t)) return Extract(t, 0);
        }
        catch { }
        finally { if (link != null) try { Marshal.ReleaseComObject(link); } catch { } }
        return null;
    }

    // куди веде ярлик (.lnk): повний шлях цілі або null (напр., для додатків Microsoft Store шляху немає)
    public static string LinkTarget(string path)
    {
        IShellLinkW link = null;
        try
        {
            link = (IShellLinkW)new ShellLinkCoClass();
            ((IPersistFile)link).Load(path, 0);
            StringBuilder tg = new StringBuilder(1024);
            link.GetPath(tg, tg.Capacity, IntPtr.Zero, 0);
            string t = tg.ToString().Trim();
            return t.Length > 0 ? t : null;
        }
        catch { return null; }
        finally { if (link != null) try { Marshal.ReleaseComObject(link); } catch { } }
    }

    // інтернет-ярлик: рядки IconFile= та IconIndex=
    static Bitmap FromUrl(string path)
    {
        string file = null;
        int idx = 0;
        foreach (string line in File.ReadAllLines(path))
        {
            string l = line.Trim();
            if (l.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase)) file = l.Substring(9).Trim();
            else if (l.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase)) int.TryParse(l.Substring(10).Trim(), out idx);
        }
        if (string.IsNullOrEmpty(file)) return null;
        file = Environment.ExpandEnvironmentVariables(file);
        return File.Exists(file) ? Extract(file, idx) : null;
    }

    static Bitmap Extract(string file, int index)
    {
        IntPtr[] h = new IntPtr[1];
        uint[] id = new uint[1];
        uint n = PrivateExtractIcons(file, index, Big, Big, h, id, 1, 0);
        if (n == 0 || n == 0xFFFFFFFF || h[0] == IntPtr.Zero) return null;
        try
        {
            using (Icon ic = Icon.FromHandle(h[0]))
            using (Bitmap bm = ic.ToBitmap()) return new Bitmap(bm);
        }
        finally { DestroyIcon(h[0]); }
    }
}
