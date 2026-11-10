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
    [DllImport("user32.dll", SetLastError = true)] static extern bool ChangeWindowMessageFilter(uint msg, uint flag);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetExStyleLong(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetExStyleLong(IntPtr h, int i, int v);

    // чи вікно приймає файли через WM_DROPFILES (замість OLE)
    public static bool Legacy { get { return elevated; } }

    // у конструкторі вікна: звичайний режим - AllowDrop, режим адміністратора - WM_DROPFILES
    public static void Prepare(Control c)
    {
        c.AllowDrop = !elevated;
    }

    // в OnHandleCreated вікна (і повторно, коли вікно показали: ця дія безпечна й кількаразова)
    public static void Register(Control c)
    {
        if (!elevated) return;
        try
        {
            IntPtr h = c.Handle;
            string log = c.GetType().Name + ": ";
            foreach (uint msg in new uint[] { WM_DROPFILES, 0x4A, 0x49 })            // WM_DROPFILES, WM_COPYDATA, WM_COPYGLOBALDATA
            {
                bool a = false, b = false;
                try { a = ChangeWindowMessageFilter(msg, 1); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                try { b = Win.ChangeWindowMessageFilterEx(h, msg, 1, IntPtr.Zero); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                log += "0x" + msg.ToString("X") + (a ? " +" : " -") + (b ? "+" : "-") + "; ";
            }
            DragAcceptFiles(h, true);
            // прапорець WS_EX_ACCEPTFILES (0x10) має стояти на вікні: якщо щось його зняло - ставимо самі
            int ex = GetExStyleLong(h, -20);
            if ((ex & 0x10) == 0) { SetExStyleLong(h, -20, ex | 0x10); ex = GetExStyleLong(h, -20); }
            log += "exstyle 0x" + ex.ToString("X");
            SbLog.W(SbLog.Panel, "перетягування файлів (від адміністратора), фільтр UIPI: " + log);
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        DropBridge.Add(c);          // основний шлях: помічник зі звичайними правами (WM_DROPFILES від Провідника UIPI зазвичай не пропускає)
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
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        try { DragFinish(m.WParam); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return r.ToArray();
    }
}
