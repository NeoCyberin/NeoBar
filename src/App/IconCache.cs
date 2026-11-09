using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Text;

// ======================================================================
//  Кеш іконок програм: коли NeoBar з усіма налаштуваннями переносять на інший ПК, а самої програми там
//  ще немає (або її видалили), елемент панелі не втрачає свою справжню іконку, а показує збережену копію (приглушену).
//  Лежить у теці cache\icons поруч із програмою, тому їде разом із нею. Ключ - шлях, що не залежить від ПК
//  (відносний до теки NeoBar або з %змінними%).
// ======================================================================
static class IconCache
{
    static string Dir { get { return NetCache.Dir("icons"); } }

    static string FileOf(string path)
    {
        string k;
        try
        {
            string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            k = Util.ToRelative(full);
            if (k == full) k = Util.Collapse(full);
        }
        catch { k = path; }
        using (SHA1 h = SHA1.Create())
        {
            byte[] b = h.ComputeHash(Encoding.UTF8.GetBytes(k.ToLowerInvariant()));
            StringBuilder sb = new StringBuilder(40);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return Path.Combine(Dir, sb.ToString() + ".png");
        }
    }

    // зберегти іконку, видобуту з файла path (якщо копії ще немає або файл новіший за неї)
    public static void Save(string path, Bitmap bmp)
    {
        try
        {
            if (bmp == null || string.IsNullOrEmpty(path)) return;
            string f = FileOf(path);
            if (File.Exists(f) && File.GetLastWriteTimeUtc(f) >= File.GetLastWriteTimeUtc(path)) return;
            string tmp = f + ".tmp";
            using (Bitmap c = new Bitmap(bmp)) c.Save(tmp, ImageFormat.Png);
            if (File.Exists(f)) File.Delete(f);
            File.Move(tmp, f);
        }
        catch { }
    }

    // збережена копія іконки для файла, якого вже (ще) немає; null - копії немає
    public static Bitmap Load(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return null;
            string f = FileOf(path);
            if (!File.Exists(f)) return null;
            using (FileStream fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (Image im = Image.FromStream(fs)) return new Bitmap(im);
        }
        catch { return null; }
    }
}
