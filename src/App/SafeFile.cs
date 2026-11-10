using System;
using System.IO;
using System.Text;

// ======================================================================
//  Надійний запис файлів даних: спершу тимчасовий файл (дані скинуто на диск), потім заміна
//  оригіналу. Збій живлення чи помилка посеред запису не залишать порожній або обрізаний файл.
//  Попередня версія лишається поруч як «.bak» - її читаємо, якщо основний файл зник або порожній.
// ======================================================================
static class SafeFile
{
    public static void Write(string path, string text, Encoding enc)
    {
        byte[] pre = enc.GetPreamble(), body = enc.GetBytes(text ?? "");
        byte[] data = new byte[pre.Length + body.Length];
        System.Buffer.BlockCopy(pre, 0, data, 0, pre.Length);
        System.Buffer.BlockCopy(body, 0, data, pre.Length, body.Length);
        Write(path, data);
    }

    public static void Write(string path, byte[] data)
    {
        if (data.Length == 0) data = new byte[] { 13, 10 };        // порожній файл означав би «пошкоджено»
        if (admin) NoLinks(path);
        string tmp = path + ".tmp", bak = path + ".bak";
        using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(data, 0, data.Length);
            fs.Flush(true);
        }
        if (!File.Exists(path)) { File.Move(tmp, path); return; }
        FileAttributes at = File.GetAttributes(path);
        try
        {
            if ((at & (FileAttributes.Hidden | FileAttributes.ReadOnly)) != 0) File.SetAttributes(path, FileAttributes.Normal);
            if (File.Exists(bak)) File.SetAttributes(bak, FileAttributes.Normal);
            File.Replace(tmp, path, bak, true);
        }
        catch (Exception ex)
        {
            if (ex is FileNotFoundException || ex is UnauthorizedAccessException) throw;
            // диск без підтримки заміни (FAT, мережа): копія, потім перезапис
            File.Copy(path, bak, true);
            File.Copy(tmp, path, true);
            File.Delete(tmp);
        }
        if ((at & FileAttributes.Hidden) != 0) try { File.SetAttributes(path, FileAttributes.Hidden); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    // читання з відкатом на .bak, якщо основний файл зник або порожній (обірваний запис старішою версією)
    public static string[] ReadLines(string path)
    {
        string bak = path + ".bak";
        bool main = File.Exists(path) && new FileInfo(path).Length > 0;
        if (main)
        {
            try { return File.ReadAllLines(path, Encoding.UTF8); }
            catch (IOException ex) { Util.ErrorLog(ex); if (!File.Exists(bak)) throw; }
        }
        if (File.Exists(bak) && new FileInfo(bak).Length > 0) return File.ReadAllLines(bak, Encoding.UTF8);
        if (!File.Exists(path)) throw new FileNotFoundException(path);
        return new string[0];
    }

    // процес з правами адміністратора не пише крізь посилання (теку даних могли підмінити на посилання на системну)
    static readonly bool admin = Elevated.IsAdmin;
    static readonly string root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');

    static void NoLinks(string path)
    {
        string d = Path.GetDirectoryName(Path.GetFullPath(path));
        while (d != null && d.Length > root.Length && d.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(d) && (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("reparse point: " + d);
            d = Path.GetDirectoryName(d);
        }
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("reparse point: " + path);
    }

    public static bool Exists(string path)
    {
        return File.Exists(path) || File.Exists(path + ".bak");
    }

    public static void Delete(string path)
    {
        foreach (string f in new string[] { path, path + ".bak", path + ".tmp" })
            if (File.Exists(f)) { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); }
    }
}
