using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// Перетягування файлів з Провідника в NeoBar, запущений від адміністратора.
// Windows (UIPI) не пускає звичайне OLE-перетягування зі звичайного Провідника у вікно з підвищеними правами,
// тому в такому режимі вікно приймає файли старим способом (WM_DROPFILES), дозволивши це повідомлення через UIPI.
// Без прав адміністратора нічого не змінюється: працює звичайний AllowDrop.
static class DropFiles
{
    public const int WM_DROPFILES = 0x233;
    static readonly bool elevated = Elevated.IsAdmin;

    [DllImport("shell32.dll")] static extern void DragAcceptFiles(IntPtr h, bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFile(IntPtr hDrop, uint i, StringBuilder buf, int cch);
    [DllImport("shell32.dll")] static extern void DragFinish(IntPtr hDrop);
    [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilter(uint msg, uint flag);

    // чи вікно приймає файли через WM_DROPFILES (замість OLE)
    public static bool Legacy { get { return elevated; } }

    // у конструкторі вікна: звичайний режим - AllowDrop, режим адміністратора - WM_DROPFILES
    public static void Prepare(Control c)
    {
        c.AllowDrop = !elevated;
    }

    // в OnHandleCreated вікна
    public static void Register(Control c)
    {
        if (!elevated) return;
        try
        {
            foreach (uint msg in new uint[] { WM_DROPFILES, 0x4A, 0x49 })            // WM_DROPFILES, WM_COPYDATA, WM_COPYGLOBALDATA
            {
                try { ChangeWindowMessageFilter(msg, 1); } catch { }
                try { Win.ChangeWindowMessageFilterEx(c.Handle, msg, 1, IntPtr.Zero); } catch { }
            }
            DragAcceptFiles(c.Handle, true);
        }
        catch { }
    }

    // у WndProc: повертає список файлів, якщо це WM_DROPFILES, інакше null
    public static string[] Take(ref Message m)
    {
        if (m.Msg != WM_DROPFILES) return null;
        List<string> r = new List<string>();
        try
        {
            uint n = DragQueryFile(m.WParam, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < n; i++)
            {
                StringBuilder sb = new StringBuilder(1024);
                if (DragQueryFile(m.WParam, i, sb, sb.Capacity) > 0) r.Add(sb.ToString());
            }
        }
        catch { }
        try { DragFinish(m.WParam); } catch { }
        return r.ToArray();
    }
}
