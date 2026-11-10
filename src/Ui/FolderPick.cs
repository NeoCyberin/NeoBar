using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// ======================================================================
//  Вибір теки сучасним вікном Провідника (IFileOpenDialog з FOS_PICKFOLDERS) замість старого
//  дерева FolderBrowserDialog: адресний рядок, пошук, швидкий доступ. Не вдалося - старе вікно.
// ======================================================================
static class FolderPick
{
    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")] class FileOpenDialogRCW { }

    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint n, IntPtr spec);
        void SetFileTypeIndex(uint i);
        void GetFileTypeIndex(out uint i);
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint fos);
        void GetOptions(out uint fos);
        void SetDefaultFolder(IShellItem si);
        void SetFolder(IShellItem si);
        void GetFolder(out IShellItem si);
        void GetCurrentSelection(out IShellItem si);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem si);
        void AddPlace(IShellItem si, int where);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string ext);
        void Close(int hr);
        void SetClientGuid(ref Guid g);
        void ClearClientData();
        void SetFilter(IntPtr filter);
        void GetResults(out IntPtr items);
        void GetSelectedItems(out IntPtr items);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem si);
        void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attrs);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

    const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_NOCHANGEDIR = 0x8, FOS_PATHMUSTEXIST = 0x800;
    const uint SIGDN_FILESYSPATH = 0x80058000;

    // true - вибрали теку (path), false - скасували
    public static bool Ask(IWin32Window owner, string title, string initial, out string path)
    {
        path = null;
        IFileOpenDialog d = null;
        try
        {
            d = (IFileOpenDialog)new FileOpenDialogRCW();
            d.SetOptions(FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_NOCHANGEDIR | FOS_PATHMUSTEXIST);
            if (!string.IsNullOrEmpty(title)) d.SetTitle(title);
            if (!string.IsNullOrEmpty(initial) && System.IO.Directory.Exists(initial))
            {
                IShellItem start;
                SHCreateItemFromParsingName(initial, IntPtr.Zero, typeof(IShellItem).GUID, out start);
                if (start != null) d.SetFolder(start);
            }
            int hr = d.Show(owner != null ? owner.Handle : IntPtr.Zero);
            if (hr != 0) return false;                 // скасовано (0x800704C7) або помилка
            IShellItem res;
            d.GetResult(out res);
            res.GetDisplayName(SIGDN_FILESYSPATH, out path);
            return !string.IsNullOrEmpty(path);
        }
        catch (Exception ex)
        {
            SbLog.Skip(ex);
            // запасний шлях - старе вікно
            using (FolderBrowserDialog f = new FolderBrowserDialog())
            {
                f.Description = title ?? "";
                if (!string.IsNullOrEmpty(initial)) f.SelectedPath = initial;
                if (f.ShowDialog(owner) != DialogResult.OK) return false;
                path = f.SelectedPath;
                return true;
            }
        }
        finally { if (d != null) Marshal.ReleaseComObject(d); }
    }
}
