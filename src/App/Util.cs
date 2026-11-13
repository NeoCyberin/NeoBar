using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// Допоміжні функції
static partial class Util
{
    // мережевий шлях (\\сервер\... або підключений мережевий диск): звернення до нього може зависнути, коли сервер недоступний
    public static bool SlowPath(string p)
    {
        if (string.IsNullOrEmpty(p)) return false;
        if (p.StartsWith(@"\\")) return !p.StartsWith(@"\\?\") && !p.StartsWith(@"\\.\");
        if (p.Length >= 3 && p[1] == ':' && (p[2] == '\\' || p[2] == '/'))
        {
            try { return new DriveInfo(p.Substring(0, 1)).DriveType == DriveType.Network; } catch { return false; }
        }
        return false;
    }

    // фонова робота: виняток у потоці пулу інакше закрив би всю програму (і панель лишила б зарезервоване місце на екрані) - ловимо й пишемо в журнал помилок
    public static void Bg(Action a)
    {
        System.Threading.ThreadPool.QueueUserWorkItem(delegate { try { a(); } catch (Exception ex) { ErrorLog(ex); } });
    }

    // журнал помилок програми (neobar-error.log; його показує «Перевірка системи»), не більше 256 КБ
    public static string ErrorPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "neobar-error.log"); } }
    static bool errMarked;

    public static void ErrorLog(Exception ex) { ErrorWrite(ex.ToString()); }

    // подія без винятку (зависання, підозра на витік) - у той самий журнал помилок
    public static void ErrorNote(string text) { ErrorWrite("[увага] " + text); }

    public static readonly object ErrGate = new object();

    static void ErrorWrite(string text)
    {
        try
        {
            lock (ErrGate)
            {
                string p = ErrorPath;
                FileInfo fi = new FileInfo(p);
                if (fi.Exists && fi.Length > 256 * 1024) return;
                string head = "";
                if (!errMarked) { errMarked = true; head = LogKeep.SessionLine() + Environment.NewLine; }
                File.AppendAllText(p, head + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "  " + text + Environment.NewLine);
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        LogKeep.Daily();
    }

    // подвійна буферизація для контролу й усіх вкладених (менше мерехтіння під час анімацій)
    public static void Buffer(System.Windows.Forms.Control c)
    {
        try
        {
            System.Reflection.PropertyInfo p = typeof(System.Windows.Forms.Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (p != null) p.SetValue(c, true, null);
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        foreach (System.Windows.Forms.Control k in c.Controls) Buffer(k);
    }

    public static readonly Color[] DefPalette =
    {
        Color.FromArgb(0x3b, 0x82, 0xf6), Color.FromArgb(0x10, 0xb9, 0x81),
        Color.FromArgb(0xf5, 0x9e, 0x0b), Color.FromArgb(0xef, 0x44, 0x44),
        Color.FromArgb(0x8b, 0x5c, 0xf6), Color.FromArgb(0x06, 0xb6, 0xd4),
        Color.FromArgb(0xec, 0x48, 0x99), Color.FromArgb(0x84, 0xcc, 0x16)
    };

    public static Color Blend(Color a, Color b, float t)
    {
        return Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    public static double Lum(Color c)
    {
        return 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
    }

    // гарантувати читабельність fg на тлі bg: якщо різниця яскравості мала,
    // освітлити (на темному тлі) або затемнити (на світлому) символ/текст
    public static Color Readable(Color fg, Color bg, int minDiff)
    {
        Color f = Opaque(fg), b = Opaque(bg);
        double lb = Lum(b);
        if (Math.Abs(Lum(f) - lb) >= minDiff) return fg;
        Color target = lb < 128 ? Color.White : Color.FromArgb(24, 24, 28);
        for (float t = 0.2f; t <= 1f; t += 0.2f)
        {
            Color c = Blend(f, target, t);
            if (Math.Abs(Lum(c) - lb) >= minDiff) return c;
        }
        return target;
    }

    public static int Hash(string s)
    {
        int h = 7;
        foreach (char c in s ?? "") h = h * 31 + c;
        return h & 0x7fffffff;
    }

    public static string Hex(Color c)
    {
        string rgb = c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        return c.A == 255 ? "#" + rgb : "#" + c.A.ToString("X2") + rgb;
    }

    public static Color Opaque(Color c)
    {
        return Color.FromArgb(255, c.R, c.G, c.B);
    }

    // #RRGGBB або #AARRGGBB (AA - альфа-канал: 00 прозорий, FF непрозорий)
    public static Color ParseColor(string s)
    {
        if (string.IsNullOrEmpty(s)) return Color.Empty;
        s = s.Trim().TrimStart('#');
        uint v;
        if ((s.Length != 6 && s.Length != 8) ||
            !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v))
            return Color.Empty;
        int a = s.Length == 8 ? (int)((v >> 24) & 255) : 255;
        return Color.FromArgb(a, (int)((v >> 16) & 255), (int)((v >> 8) & 255), (int)(v & 255));
    }

    public static bool IsUrl(string t)
    {
        t = t ?? "";
        return t.IndexOf("://", StringComparison.Ordinal) > 0 ||
               t.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
    }

    static List<KeyValuePair<string, string>> envTable;

    static List<KeyValuePair<string, string>> EnvTable()
    {
        if (envTable != null) return envTable;
        string[] names = { "USERPROFILE", "OneDrive", "APPDATA", "LOCALAPPDATA", "ProgramData",
                           "ProgramFiles(x86)", "ProgramFiles", "PUBLIC", "SystemRoot" };
        List<KeyValuePair<string, string>> l = new List<KeyValuePair<string, string>>();
        foreach (string n in names)
        {
            string v = Environment.GetEnvironmentVariable(n);
            if (string.IsNullOrEmpty(v)) continue;
            v = v.TrimEnd('\\');
            if (v.Length < 4) continue;
            l.Add(new KeyValuePair<string, string>(n, v));
        }
        // найдовший збіг першим: AppData\Roaming -> %APPDATA%, а не %USERPROFILE%\AppData\Roaming
        l.Sort(delegate (KeyValuePair<string, string> a, KeyValuePair<string, string> b)
        {
            return b.Value.Length.CompareTo(a.Value.Length);
        });
        envTable = l;
        return l;
    }

    // C:\Users\Ім'я\Documents -> %USERPROFILE%\Documents
    public static string Collapse(string p)
    {
        if (p == null) return p;
        string s = p.Trim().Trim('"');
        if (s.Length == 0 || s.IndexOf('%') >= 0 || IsUrl(s)) return p;
        foreach (KeyValuePair<string, string> kv in EnvTable())
        {
            if (s.StartsWith(kv.Value, StringComparison.OrdinalIgnoreCase) &&
                (s.Length == kv.Value.Length || s[kv.Value.Length] == '\\'))
                return "%" + kv.Key + "%" + s.Substring(kv.Value.Length);
        }
        return s;
    }

    // назва шрифту для GDI/RichEdit: перший зі списку «|», який справді доступний (власний - якщо його бачить GDI)
    public static string ResolveFace(string list)
    {
        foreach (string n0 in (list ?? "").Split('|'))
        {
            string n = n0.Trim();
            if (n.Length == 0) continue;
            if (SBFonts.IsOwn(n)) { if (SBFonts.GdiVisible(n)) return n; continue; }
            try
            {
                using (Font f = new Font(n, 10f))
                    if (string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)) return n;
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
        return "Segoe UI";
    }

    // name може бути списком через «|»: перший встановлений шрифт виграє
    public static Font MakeFont(string name, float pt, FontStyle st)
    {
        foreach (string n0 in (name ?? "").Split('|'))
        {
            string n = n0.Trim();
            if (n.Length == 0) continue;
            FontFamily own = SBFonts.Family(n);         // власні шрифти NeoBar
            if (own != null)
            {
                try
                {
                    FontStyle bi = st & (FontStyle.Bold | FontStyle.Italic);
                    if (own.IsStyleAvailable(bi)) return new Font(own, pt, st, GraphicsUnit.Point);
                    if (own.IsStyleAvailable(FontStyle.Regular)) return new Font(own, pt, st & ~(FontStyle.Bold | FontStyle.Italic), GraphicsUnit.Point);
                }
                catch (Exception skipEx) { SbLog.Skip(skipEx); }
                continue;
            }
            try
            {
                // рукописні шрифти: жирне накреслення на малому розмірі «злипається» - беремо звичайне й трохи більше
                bool hand = IsHand(n);
                bool soft = hand && pt < 16f;
                // вирівнювання візуального розміру: у Cambria й Palatino малі літери дрібніші, ніж у Segoe UI
                float k = FontScale(n);
                Font f = soft ? new Font(n, pt * 1.08f, st & ~FontStyle.Bold) : new Font(n, pt * k, st);
                if (string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)) return f;
                f.Dispose();
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
        return new Font("Segoe UI", pt, st);
    }

    // рукописний шрифт («Артистична»)
    public static bool IsHand(string n)
    {
        n = (n ?? "").Trim();
        return n.Equals("Ink Free", StringComparison.OrdinalIgnoreCase) || n.Equals("Segoe Print", StringComparison.OrdinalIgnoreCase)
            || n.Equals("Comic Sans MS", StringComparison.OrdinalIgnoreCase);
    }

    // перший шрифт зі списку «А|Б|В» - рукописний
    public static bool IsHandFace(string names)
    {
        string n = names ?? "";
        int bar = n.IndexOf('|');
        return IsHand(bar >= 0 ? n.Substring(0, bar) : n);
    }

    static float FontScale(string n)
    {
        if (n.Equals("Cambria", StringComparison.OrdinalIgnoreCase)) return 1.06f;
        if (n.Equals("Palatino Linotype", StringComparison.OrdinalIgnoreCase)) return 1.07f;
        if (n.Equals("Georgia", StringComparison.OrdinalIgnoreCase)) return 1.0f;
        if (n.Equals("Calibri", StringComparison.OrdinalIgnoreCase)) return 1.1f;
        return 1f;
    }

    public static GraphicsPath Round(Rectangle r, int tl, int tr, int br, int bl)
    {
        int m = Math.Max(0, Math.Min(r.Width, r.Height) / 2);
        tl = Math.Min(tl, m); tr = Math.Min(tr, m); br = Math.Min(br, m); bl = Math.Min(bl, m);
        GraphicsPath p = new GraphicsPath();
        int x = r.X, y = r.Y, w = r.Width, h = r.Height;
        if (tl > 0) p.AddArc(x, y, tl * 2, tl * 2, 180, 90);
        p.AddLine(x + tl, y, x + w - tr, y);
        if (tr > 0) p.AddArc(x + w - tr * 2, y, tr * 2, tr * 2, 270, 90);
        p.AddLine(x + w, y + tr, x + w, y + h - br);
        if (br > 0) p.AddArc(x + w - br * 2, y + h - br * 2, br * 2, br * 2, 0, 90);
        p.AddLine(x + w - br, y + h, x + bl, y + h);
        if (bl > 0) p.AddArc(x, y + h - bl * 2, bl * 2, bl * 2, 90, 90);
        p.AddLine(x, y + h - bl, x, y + tl);
        p.CloseFigure();
        return p;
    }

    // Форма вікна. Властивість Region не звільняє попередню область, тож без цього кожна зміна розміру
    // чи відкриття меню лишала по об'єкту Region до збирання сміття. Область r переходить у власність елемента.
    public static void SetRegion(Control c, Region r)
    {
        Region old = c.Region;
        c.Region = r;
        if (old != null && !ReferenceEquals(old, r)) old.Dispose();
    }

    public static void SetRegion(Control c, GraphicsPath p) { SetRegion(c, p == null ? null : new Region(p)); }

    // дробовий варіант (для малювання з субпіксельною точністю); радіус обрізається до половини меншої сторони
    public static GraphicsPath Round(RectangleF r, float rad)
    {
        GraphicsPath p = new GraphicsPath();
        float d = Math.Min(rad * 2f, Math.Min(r.Width, r.Height));
        if (d < 1f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static GraphicsPath Round(Rectangle r, int rad)
    {
        return Round(r, rad, rad, rad, rad);
    }

    public static void FillRound(Graphics g, Brush b, Rectangle r, int rad)
    {
        if (rad <= 0 || r.Width <= 1 || r.Height <= 1) { g.FillRectangle(b, r); return; }
        using (GraphicsPath p = Round(r, rad)) g.FillPath(b, p);
    }

    public static string ResolvePath(string p)
    {
        p = Environment.ExpandEnvironmentVariables((p ?? "").Trim());
        if (p.Length > 0 && !Path.IsPathRooted(p))
            p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, p);
        return p;
    }

    // Власна картинка-іконка зберігається в папці icons поруч із програмою, щоб її не втратити, якщо
    // оригінал (з Робочого столу, Завантажень…) видалять. Повертає шлях до копії або вихідне значення.
    // .exe/.dll не копіюються: з них іконка береться «наживо».
    public static string ImportIcon(string spec)
    {
        try
        {
            string s = (spec ?? "").Trim().Trim('"');
            if (s.Length == 0 || s.StartsWith("theme:", StringComparison.OrdinalIgnoreCase) || IsUrl(s)) return spec;
            if (!Path.IsPathRooted(s) && s.IndexOf('%') < 0) return spec;             // уже відносний шлях (у папці програми)
            string ext = Path.GetExtension(s).ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".bmp" && ext != ".ico" && ext != ".gif") return spec;
            string src = Path.GetFullPath(Environment.ExpandEnvironmentVariables(s));
            string bd = AppDomain.CurrentDomain.BaseDirectory;
            if (src.StartsWith(bd, StringComparison.OrdinalIgnoreCase)) return spec;
            FileInfo fi = new FileInfo(src);
            if (!fi.Exists || fi.Length > 20 * 1024 * 1024) return spec;
            string dir = Path.Combine(bd, "icons");
            Directory.CreateDirectory(dir);
            string name = Path.GetFileNameWithoutExtension(src), dest = null;
            byte[] data = File.ReadAllBytes(src);
            for (int i = 1; i < 100; i++)
            {
                string cand = Path.Combine(dir, name + (i == 1 ? "" : "-" + i) + ext);
                if (!File.Exists(cand)) { File.WriteAllBytes(cand, data); dest = cand; break; }
                FileInfo ci = new FileInfo(cand);
                if (ci.Length == data.Length && SameBytes(File.ReadAllBytes(cand), data)) { dest = cand; break; }     // така сама копія вже є
            }
            return dest != null ? ToRelative(dest) : spec;
        }
        catch { return spec; }
    }

    // Видаляє власну копію іконки з папки icons, якщо на неї більше не посилається жоден профіль.
    // Чужі файли (повні шляхи, %змінні%, theme:) і все поза папкою icons не чіпаються.
    public static void DeleteIconIfUnused(string spec)
    {
        try
        {
            string s = (spec ?? "").Trim().Trim('"');
            if (s.Length == 0 || s.StartsWith("theme:", StringComparison.OrdinalIgnoreCase) || IsUrl(s)) return;
            if (Path.IsPathRooted(s) || s.IndexOf('%') >= 0) return;
            string bd = AppDomain.CurrentDomain.BaseDirectory;
            string dir = Path.GetFullPath(Path.Combine(bd, "icons")).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(bd, s.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(dir, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return;
            if (full.IndexOf(Path.DirectorySeparatorChar, dir.Length) >= 0) return;            // лише файли просто в icons
            string needle = "icons/" + Path.GetFileName(full);
            List<string> inis = new List<string>(Directory.GetFiles(bd, "*.ini"));
            string pd = Path.Combine(bd, "profiles");
            if (Directory.Exists(pd)) inis.AddRange(Directory.GetFiles(pd, "*.ini", SearchOption.AllDirectories));
            foreach (string f in inis)
            {
                string text;
                try { text = File.ReadAllText(f, Encoding.UTF8); } catch { return; }          // не змогли перевірити - краще лишити
                if (text.Replace('\\', '/').IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return;
            }
            File.Delete(full);
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    // Ярлик (.lnk, .url) з Робочого столу чи іншого місця копіюється в папку links поруч із програмою, щоб панель
    // не зламалась, коли оригінал видалять. У елементі зберігається %NEOBAR_DIR%\\links\\ім'я (змінну задає Program.Main).
    // Повертає нове значення цілі або вихідне, якщо копіювати не треба чи не вдалося.
    public const string LinksRef = "%NEOBAR_DIR%\\links\\";

    public static string ImportLink(string target)
    {
        try
        {
            string s = (target ?? "").Trim().Trim('"');
            if (s.Length == 0 || IsUrl(s)) return target;
            string full = Environment.ExpandEnvironmentVariables(s);
            if (!Path.IsPathRooted(full)) return target;
            string ext = Path.GetExtension(full).ToLowerInvariant();
            if (ext != ".lnk" && ext != ".url") return target;
            string src = Path.GetFullPath(full);
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "links");
            string dirSep = Path.GetFullPath(dir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (src.StartsWith(dirSep, StringComparison.OrdinalIgnoreCase)) return target;       // це вже наша копія
            FileInfo fi = new FileInfo(src);
            if (!fi.Exists || fi.Length > 1024 * 1024) return target;
            byte[] data = File.ReadAllBytes(src);
            Directory.CreateDirectory(dir);
            string name = Path.GetFileNameWithoutExtension(src);
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            for (int i = 1; i < 100; i++)
            {
                string cand = Path.Combine(dir, name + (i == 1 ? "" : "-" + i) + ext);
                if (!File.Exists(cand)) { File.WriteAllBytes(cand, data); return LinksRef + Path.GetFileName(cand); }
                if (SameBytes(File.ReadAllBytes(cand), data)) return LinksRef + Path.GetFileName(cand);      // така сама копія вже є
            }
            return target;
        }
        catch { return target; }
    }

    // Видаляє нашу копію ярлика, якщо на неї більше не посилається жоден профіль (чужі файли не чіпає).
    public static void DeleteLinkIfUnused(string target)
    {
        try
        {
            string s = (target ?? "").Trim().Trim('"');
            if (!s.StartsWith(LinksRef, StringComparison.OrdinalIgnoreCase)) return;
            string file = s.Substring(LinksRef.Length);
            if (file.Length == 0 || file.IndexOfAny(new char[] { '\\', '/' }) >= 0) return;
            string bd = AppDomain.CurrentDomain.BaseDirectory;
            string full = Path.Combine(bd, "links", file);
            if (!File.Exists(full)) return;
            string needle = ("%NEOBAR_DIR%/links/" + file).ToLowerInvariant();
            List<string> inis = new List<string>(Directory.GetFiles(bd, "*.ini"));
            string pd = Path.Combine(bd, "profiles");
            if (Directory.Exists(pd)) inis.AddRange(Directory.GetFiles(pd, "*.ini", SearchOption.AllDirectories));
            foreach (string f in inis)
            {
                string text;
                try { text = File.ReadAllText(f, Encoding.UTF8); } catch { return; }          // не змогли перевірити - краще лишити
                if (text.Replace('\\', '/').ToLowerInvariant().IndexOf(needle, StringComparison.Ordinal) >= 0) return;
            }
            File.Delete(full);
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    static bool SameBytes(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    public static string ToRelative(string file)
    {
        string b = AppDomain.CurrentDomain.BaseDirectory;
        if (file.StartsWith(b, StringComparison.OrdinalIgnoreCase)) return file.Substring(b.Length);
        return file;
    }

    public static Bitmap ShellIcon(string path, bool dir)
    {
        Win.SHFILEINFO fi = new Win.SHFILEINFO();
        Win.SHGetFileInfo(path, dir ? 0x10u : 0x80u, ref fi,
            (uint)Marshal.SizeOf(typeof(Win.SHFILEINFO)), 0x100u | 0x10u);
        if (fi.hIcon == IntPtr.Zero) return null;
        try
        {
            using (Icon ic = Icon.FromHandle(fi.hIcon)) return ic.ToBitmap();
        }
        finally { Win.DestroyIcon(fi.hIcon); }
    }

    public static string SafeName(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    // Завантажує іконку сайту (через сервіс favicon Google, запасний варіант - /favicon.ico)
    public static string DownloadFavicon(Uri u, string dir)
    {
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        string host = u.Host;
        string[] urls =
        {
            "https://www.google.com/s2/favicons?sz=128&domain=" + host,
            "https://" + host + "/favicon.ico"
        };
        foreach (string a in urls)
        {
            try
            {
                HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(a);
                rq.Timeout = 8000;
                rq.ReadWriteTimeout = 8000;
                rq.UserAgent = "Mozilla/5.0";
                byte[] data;
                using (WebResponse rs = rq.GetResponse())
                using (Stream s = rs.GetResponseStream())
                using (MemoryStream ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    data = ms.ToArray();
                }
                if (data.Length < 16) continue;

                Bitmap bmp = null;
                using (MemoryStream ms2 = new MemoryStream(data))
                {
                    try
                    {
                        using (Image im = Image.FromStream(ms2)) bmp = new Bitmap(im);
                    }
                    catch
                    {
                        ms2.Position = 0;
                        using (Icon ic = new Icon(ms2, new Size(64, 64))) bmp = ic.ToBitmap();
                    }
                }
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, SafeName(host) + ".png");
                using (bmp) bmp.Save(file, ImageFormat.Png);
                return file;
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
        return null;
    }
}


static partial class Util
{
    // Справжня іконка системної папки (Цей ПК, Кошик...) за CSIDL
    public static Bitmap ShellIconPidl(int csidl)
    {
        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (Win.SHGetSpecialFolderLocation(IntPtr.Zero, csidl, out pidl) != 0 || pidl == IntPtr.Zero) return null;
            Win.SHFILEINFO fi = new Win.SHFILEINFO();
            Win.SHGetFileInfo(pidl, 0, ref fi, (uint)Marshal.SizeOf(typeof(Win.SHFILEINFO)), 0x100u | 0x8u);
            if (fi.hIcon == IntPtr.Zero) return null;
            try
            {
                using (Icon ic = Icon.FromHandle(fi.hIcon)) return ic.ToBitmap();
            }
            finally { Win.DestroyIcon(fi.hIcon); }
        }
        catch { return null; }
        finally { if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl); }
    }

    // розбір «програма аргументи» для цілей на кшталт rundll32.exe user32.dll,LockWorkStation
    public static void SplitCommand(string t, out string file, out string args)
    {
        file = t; args = "";
        t = (t ?? "").Trim();
        if (t.Length == 0 || IsUrl(t) || File.Exists(t) || Directory.Exists(t)) return;
        if (t[0] == '"')
        {
            int q = t.IndexOf('"', 1);
            if (q > 0) { file = t.Substring(1, q - 1); args = t.Substring(q + 1).Trim(); }
            return;
        }
        int sp = t.IndexOf(' ');
        if (sp <= 0) return;
        string first = t.Substring(0, sp);
        string ext = Path.GetExtension(first).ToLowerInvariant();
        if (ext == ".exe" || ext == ".cpl" || ext == ".msc" || ext == ".bat" || ext == ".cmd")
        {
            file = first;
            args = t.Substring(sp + 1).Trim();
        }
    }
}
