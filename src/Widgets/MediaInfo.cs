using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;

// ======================================================================
//  Тривалість відео або GIF: для відео - від оболонки Windows (колонка «Тривалість» Провідника),
//  для GIF - сума затримок кадрів. Нічого не запускає у фоні; 0 - тривалість невідома.
// ======================================================================
static class MediaInfo
{
    public static double Seconds(string file)
    {
        try
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return 0;
            if (string.Equals(Path.GetExtension(file), ".gif", StringComparison.OrdinalIgnoreCase)) return GifSeconds(file);
            return ShellSeconds(file);
        }
        catch { return 0; }
    }

    static double GifSeconds(string file)
    {
        using (Image im = Image.FromFile(file))
        {
            FrameDimension fd = new FrameDimension(im.FrameDimensionsList[0]);
            int n = im.GetFrameCount(fd);
            if (n < 2) return 0;
            PropertyItem pi = im.GetPropertyItem(0x5100);       // PropertyTagFrameDelay, соті частки секунди
            double total = 0;
            for (int i = 0; i < n && (i + 1) * 4 <= pi.Value.Length; i++)
            {
                int d = BitConverter.ToInt32(pi.Value, i * 4);
                total += (d <= 0 ? 10 : d) / 100.0;
            }
            return total;
        }
    }

    static double ShellSeconds(string file)
    {
        Type t = Type.GetTypeFromProgID("Shell.Application");
        if (t == null) return 0;
        object sh = Activator.CreateInstance(t);
        try
        {
            object folder = t.InvokeMember("NameSpace", BindingFlags.InvokeMethod, null, sh, new object[] { Path.GetDirectoryName(file) });
            if (folder == null) return 0;
            object item = folder.GetType().InvokeMember("ParseName", BindingFlags.InvokeMethod, null, folder, new object[] { Path.GetFileName(file) });
            if (item == null) return 0;
            object raw = folder.GetType().InvokeMember("GetDetailsOf", BindingFlags.InvokeMethod, null, folder, new object[] { item, 27 });
            string s = raw as string;
            if (string.IsNullOrEmpty(s)) return 0;
            // оболонка може вставити невидимі знаки напрямку тексту - лишаємо цифри й двокрапки
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (char c in s) if ((c >= '0' && c <= '9') || c == ':') sb.Append(c);
            string[] p = sb.ToString().Split(':');
            double sec = 0;
            foreach (string x in p)
            {
                int v;
                if (!int.TryParse(x, out v)) return 0;
                sec = sec * 60 + v;
            }
            return p.Length >= 2 ? sec : 0;
        }
        finally
        {
            try { System.Runtime.InteropServices.Marshal.ReleaseComObject(sh); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
    }

    public static string Format(double sec)
    {
        int s = (int)Math.Round(sec);
        if (s < 0) s = 0;
        int h = s / 3600, m = s % 3600 / 60, r = s % 60;
        return h > 0 ? h + ":" + m.ToString("00") + ":" + r.ToString("00") : m + ":" + r.ToString("00");
    }
}
