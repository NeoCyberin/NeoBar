using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

// ======================================================================
//  Профілі NeoBar: у кожного профілю свої дані (панель, вигляд, віджети, нотатки, ігри, оверлей).
//  Дані профілю лежать у profiles\<id>\ поруч із програмою; список - у profiles\profiles.ini.
//  Останній профіль запам'ятовується (і при автозапуску з Windows вхід іде в нього);
//  для профілю з PIN-кодом під час запуску з'являється вікно вибору з полем PIN.
// ======================================================================
sealed class Profile
{
    public string Id = "", Name = "", Salt = "", Hash = "";
    public Color Color = Color.FromArgb(76, 139, 245);
    public string Style = "";           // стиль значка - за темою профілю (зберігається, щоб у вікні вибору кожен профіль був у своїй темі)
    public bool Photo;                  // власне фото замість літери (тоді без стилю й кольору)
    public int Shade = -1;              // колір із палітри теми (0..7); -1 - старий профіль із довільним кольором
    public bool HasPin { get { return Hash.Length > 0; } }
    public string Initial
    {
        get
        {
            string n = (Name ?? "").Trim();
            return n.Length > 0 ? n.Substring(0, 1).ToUpperInvariant() : "?";
        }
    }
}

static class Prof
{
    public static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
    static string PDir { get { return Path.Combine(Root, "profiles"); } }
    static string ListPath { get { return Path.Combine(PDir, "profiles.ini"); } }

    public static readonly List<Profile> All = new List<Profile>();
    public static Profile Cur;          // профіль, у якому працює NeoBar
    public static string Last = "";     // у який профіль заходити під час наступного запуску
    static string Drop = "";            // профіль, який видалити під час наступного запуску (поточний профіль не видалити, поки він відкритий)

    public static readonly Color[] Colors =
    {
        Color.FromArgb(76, 139, 245), Color.FromArgb(46, 170, 120), Color.FromArgb(235, 120, 45), Color.FromArgb(210, 70, 90),
        Color.FromArgb(150, 95, 225), Color.FromArgb(30, 165, 190), Color.FromArgb(200, 160, 40), Color.FromArgb(110, 120, 140)
    };

    public static string DirOf(Profile p) { return Path.Combine(PDir, p.Id); }
    public const int MaxProfiles = 12;
    public static string PhotoOf(Profile p) { return Path.Combine(DirOf(p), "avatar.nbp"); }

    // ---- фото профілю: не звичайний PNG на видноті, а власний файл (avatar.nbp): позначка, випадкове число, дані, закриті маскою
    // з ключем від ідентифікатора профілю, і контрольна сума. Це не шифрування (ключ відомий програмі), а захист від випадкового
    // перегляду: файл не відкривається переглядачами зображень, а скопований в інший профіль не покаже фото.
    static byte[] PhotoMask(string id, byte[] nonce, int len)
    {
        byte[] k = new byte[len];
        byte[] idb = Encoding.UTF8.GetBytes((id ?? "") + "|sbphoto");
        using (SHA256 sha = SHA256.Create())
        {
            int pos = 0; uint ctr = 0;
            while (pos < len)
            {
                byte[] inb = new byte[nonce.Length + idb.Length + 4];
                Buffer.BlockCopy(nonce, 0, inb, 0, nonce.Length);
                Buffer.BlockCopy(idb, 0, inb, nonce.Length, idb.Length);
                Buffer.BlockCopy(BitConverter.GetBytes(ctr++), 0, inb, nonce.Length + idb.Length, 4);
                byte[] h = sha.ComputeHash(inb);
                int n = Math.Min(h.Length, len - pos);
                Buffer.BlockCopy(h, 0, k, pos, n);
                pos += n;
            }
        }
        return k;
    }

    static byte[] PhotoPack(Profile p, byte[] png)
    {
        byte[] nonce = new byte[16];
        using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(nonce);
        byte[] mask = PhotoMask(p.Id, nonce, png.Length);
        byte[] chk;
        using (SHA256 sha = SHA256.Create()) chk = sha.ComputeHash(png);
        byte[] o = new byte[4 + 1 + 16 + 4 + png.Length + 4];
        o[0] = (byte)'S'; o[1] = (byte)'B'; o[2] = (byte)'P'; o[3] = (byte)'F'; o[4] = 1;
        Buffer.BlockCopy(nonce, 0, o, 5, 16);
        Buffer.BlockCopy(BitConverter.GetBytes(png.Length), 0, o, 21, 4);
        for (int i = 0; i < png.Length; i++) o[25 + i] = (byte)(png[i] ^ mask[i]);
        Buffer.BlockCopy(chk, 0, o, 25 + png.Length, 4);
        return o;
    }

    const int MaxPhotoFile = 2 * 1024 * 1024;       // значок 256×256 - це сотні кілобайт; більше - не наш файл

    // null - файл не наш, пошкоджений або від іншого профілю
    static byte[] PhotoUnpack(Profile p, byte[] d)
    {
        if (d == null || d.Length < 29 || d[0] != 'S' || d[1] != 'B' || d[2] != 'P' || d[3] != 'F' || d[4] != 1) return null;
        int len = BitConverter.ToInt32(d, 21);
        if (len <= 0 || len > MaxPhotoFile || d.Length != 29 + len) return null;
        byte[] nonce = new byte[16];
        Buffer.BlockCopy(d, 5, nonce, 0, 16);
        byte[] mask = PhotoMask(p.Id, nonce, len);
        byte[] png = new byte[len];
        for (int i = 0; i < len; i++) png[i] = (byte)(d[25 + i] ^ mask[i]);
        using (SHA256 sha = SHA256.Create())
        {
            byte[] chk = sha.ComputeHash(png);
            for (int i = 0; i < 4; i++) if (chk[i] != d[25 + len + i]) return null;
        }
        return png;
    }

    static void Unhide(string f) { try { if (System.IO.File.Exists(f)) System.IO.File.SetAttributes(f, FileAttributes.Normal); } catch { } }

    // зберегти фото профілю (атомарно: спершу тимчасовий файл)
    public static void SavePhoto(Profile p, Image img)
    {
        byte[] png;
        using (MemoryStream ms = new MemoryStream()) { img.Save(ms, System.Drawing.Imaging.ImageFormat.Png); png = ms.ToArray(); }
        byte[] data = PhotoPack(p, png);
        Directory.CreateDirectory(DirOf(p));
        string f = PhotoOf(p), tmp = f + ".tmp";
        System.IO.File.WriteAllBytes(tmp, data);
        Unhide(f);
        if (System.IO.File.Exists(f)) System.IO.File.Delete(f);
        System.IO.File.Move(tmp, f);
        try { System.IO.File.SetAttributes(f, FileAttributes.Hidden); } catch { }
    }

    public static void DropPhoto(Profile p)
    {
        string f = PhotoOf(p);
        Unhide(f);
        if (System.IO.File.Exists(f)) System.IO.File.Delete(f);
    }

    // розкодовані байти PNG (null - фото немає або воно пошкоджене)
    public static byte[] PhotoBytes(Profile p)
    {
        try
        {
            string f = PhotoOf(p);
            System.IO.FileInfo fi = new System.IO.FileInfo(f);
            if (!fi.Exists || fi.Length > MaxPhotoFile + 64) return null;
            return PhotoUnpack(p, System.IO.File.ReadAllBytes(f));
        }
        catch { return null; }
    }

    // тека поточного профілю (поки профіль не обрано - тека програми)
    public static string Dir { get { return Cur != null ? DirOf(Cur) : Root; } }

    // повний шлях до файлу даних поточного профілю
    public static string File(string name)
    {
        string d = Dir;
        try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); } catch { }
        return Path.Combine(d, name);
    }

    public static Profile ById(string id)
    {
        foreach (Profile p in All) if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    public static void Init()
    {
        Load();
        if (All.Count == 0) CreateFirst();
        DropPending();
    }

    // видалити профіль, позначений минулим запуском (той екземпляр уже закрився і файли вільні)
    static void DropPending()
    {
        if (Drop.Length == 0) return;
        Profile d = ById(Drop);
        if (d == null || All.Count <= 1) { Drop = ""; Save(); return; }
        string dir = DirOf(d);
        bool gone = false;
        for (int i = 0; i < 10 && !gone; i++)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); gone = true; }
            catch (Exception ex) { if (i == 9) Util.ErrorLog(ex); else System.Threading.Thread.Sleep(200); }
        }
        if (gone)
        {
            // запис зі списку прибирається, лише коли файли справді видалено
            Drop = "";
            All.Remove(d);
            if (string.Equals(Last, d.Id, StringComparison.OrdinalIgnoreCase)) Last = "";
        }
        // не вдалося видалити (файли зайняті): профіль лишається в списку, а видалення повториться під час наступного запуску
        Save();
    }

    // поточний профіль: позначити до видалення; видалиться після перезапуску, коли його файли звільняться
    public static bool MarkDrop(Profile p)
    {
        if (p == null || All.Count <= 1) return false;
        Drop = p.Id;
        Last = "";
        Save();
        return true;
    }

    static void Load()
    {
        All.Clear();
        Last = "";
        Drop = "";
        if (!SafeFile.Exists(ListPath)) return;
        Profile cur = null;
        foreach (string raw in SafeFile.ReadLines(ListPath))
        {
            string l = raw.Trim();
            if (l.Length == 0 || l.StartsWith(";")) continue;
            if (l.StartsWith("[") && l.EndsWith("]"))
            {
                string id = l.Substring(1, l.Length - 2).Trim();
                if (!SafeId(id) || ById(id) != null) { cur = null; continue; }
                cur = new Profile();
                cur.Id = id;
                cur.Name = id;
                All.Add(cur);
                continue;
            }
            int eq = l.IndexOf('=');
            if (eq <= 0) continue;
            string k = l.Substring(0, eq).Trim().ToLowerInvariant(), v = l.Substring(eq + 1).Trim();
            if (cur == null) { if (k == "last") Last = v; else if (k == "drop") Drop = v; continue; }
            if (k == "name" && v.Length > 0) cur.Name = v;
            else if (k == "color") { int c; if (int.TryParse(v, System.Globalization.NumberStyles.HexNumber, null, out c)) cur.Color = Color.FromArgb(255, Color.FromArgb(c)); }
            else if (k == "style" && ProfileArt.Styles.Contains(v)) cur.Style = v;
            else if (k == "photo") cur.Photo = v == "1";
            else if (k == "shade") { int sh; if (int.TryParse(v, out sh) && sh >= 0 && sh < 8) cur.Shade = sh; }
            else if (k == "pin")
            {
                int c = v.IndexOf(':');
                if (c > 0) { cur.Salt = v.Substring(0, c); cur.Hash = v.Substring(c + 1); }
            }
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(PDir);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(L.T("; профілі NeoBar"));
            sb.AppendLine("last=" + Last);
            if (Drop.Length > 0) sb.AppendLine("drop=" + Drop);
            foreach (Profile p in All)
            {
                sb.AppendLine();
                sb.AppendLine("[" + p.Id + "]");
                sb.AppendLine("name=" + p.Name.Replace("\r", " ").Replace("\n", " "));
                sb.AppendLine("color=" + (p.Color.ToArgb() & 0xFFFFFF).ToString("X6"));
                if (p.Style.Length > 0) sb.AppendLine("style=" + p.Style);
                if (p.Shade >= 0) sb.AppendLine("shade=" + p.Shade);
                if (p.Photo) sb.AppendLine("photo=1");
                if (p.HasPin) sb.AppendLine("pin=" + p.Salt + ":" + p.Hash);
            }
            // запис через тимчасовий файл: обірваний запис не зіпсує список профілів
            SafeFile.Write(ListPath, sb.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    // перший запуск: створюється профіль «Основний»
    static void CreateFirst()
    {
        Profile p = new Profile();
        p.Id = "main";
        p.Name = L.T("Основний");
        try { Directory.CreateDirectory(DirOf(p)); }
        catch (Exception ex) { Util.ErrorLog(ex); }
        All.Add(p);
        Last = p.Id;
        Save();
    }

    static bool SafeId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 40) return false;
        foreach (char c in id) if (!(char.IsLetterOrDigit(c) && c < 128) && c != '_' && c != '-') return false;
        return true;
    }

    public static bool NameTaken(string name, Profile except)
    {
        foreach (Profile p in All) if (p != except && string.Equals(p.Name.Trim(), name.Trim(), StringComparison.CurrentCultureIgnoreCase)) return true;
        return false;
    }

    public static Profile Create(string name, string pin, Color color)
    {
        Profile p = new Profile();
        string id;
        do { id = "p" + DateTime.UtcNow.Ticks.ToString("x"); } while (ById(id) != null);
        p.Id = id;
        p.Name = name.Trim();
        p.Color = color;
        SetPin(p, pin);
        try { Directory.CreateDirectory(DirOf(p)); } catch (Exception ex) { Util.ErrorLog(ex); }
        All.Add(p);
        Save();
        return p;
    }

    public static void SetPin(Profile p, string pin)
    {
        if (string.IsNullOrEmpty(pin)) { p.Salt = ""; p.Hash = ""; return; }
        byte[] s = new byte[16];
        using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(s);
        p.Salt = Convert.ToBase64String(s);
        p.Hash = HashOf(p.Salt, pin);
    }

    static string HashOf(string salt, string pin)
    {
        // кілька тисяч проходів SHA-256: PIN короткий, тож прямий перебір за копією profiles.ini має бути повільним
        using (SHA256 sha = SHA256.Create())
        {
            byte[] h = Encoding.UTF8.GetBytes(salt + "|" + pin);
            for (int i = 0; i < 20000; i++) h = sha.ComputeHash(h);
            StringBuilder sb = new StringBuilder(64);
            foreach (byte b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    // невдалі спроби PIN: після третьої підряд - пауза, що зростає (5, 10, 20 ... до 300 с); лічильник скидається після вдалого входу
    static readonly Dictionary<string, int> pinFails = new Dictionary<string, int>();
    static readonly Dictionary<string, DateTime> pinUntil = new Dictionary<string, DateTime>();

    // скільки секунд ще чекати до наступної спроби (0 - можна пробувати)
    public static int WaitSec(Profile p)
    {
        DateTime u;
        if (p == null || !pinUntil.TryGetValue(p.Id, out u)) return 0;
        double s = (u - DateTime.Now).TotalSeconds;
        return s > 0 ? (int)Math.Ceiling(s) : 0;
    }

    // текст помилки після невдалої перевірки PIN
    public static string PinErr(Profile p, string basic)
    {
        int w = WaitSec(p);
        return w > 0 ? L.T("Забагато спроб. Зачекайте ") + w + L.T(" с") : basic;
    }

    public static bool Check(Profile p, string pin)
    {
        if (p == null) return false;
        if (!p.HasPin) return true;
        if (WaitSec(p) > 0) return false;
        string h = HashOf(p.Salt, pin ?? "");
        bool ok = h.Length == p.Hash.Length;
        if (ok)
        {
            int diff = 0;
            for (int i = 0; i < h.Length; i++) diff |= h[i] ^ p.Hash[i];
            ok = diff == 0;
        }
        if (ok) { pinFails.Remove(p.Id); pinUntil.Remove(p.Id); return true; }
        int n;
        pinFails.TryGetValue(p.Id, out n);
        pinFails[p.Id] = ++n;
        if (n >= 3) pinUntil[p.Id] = DateTime.Now.AddSeconds(Math.Min(300, 5 << Math.Min(n - 3, 6)));
        return false;
    }

    // видалити профіль разом з даними (не поточний і не останній залишений)
    public static bool Delete(Profile p)
    {
        if (p == null || p == Cur || All.Count <= 1) return false;
        try { if (Directory.Exists(DirOf(p))) Directory.Delete(DirOf(p), true); }
        catch (Exception ex) { Util.ErrorLog(ex); return false; }
        All.Remove(p);
        if (string.Equals(Last, p.Id, StringComparison.OrdinalIgnoreCase)) Last = "";
        Save();
        return true;
    }

    // шлях до файлу останнього профілю ще до вибору (потрібен до запуску панелі: «завжди від адміністратора»)
    public static string PeekLastFile(string name)
    {
        try
        {
            if (All.Count == 0) Load();
            Profile p = ById(Last);
            if (p != null) return Path.Combine(DirOf(p), name);
        }
        catch { }
        return Path.Combine(Root, name);
    }

    // тема останнього профілю без жодних змін у профілях (окреме вікно з контекстного меню файлів, поки панель може працювати)
    public static Theme PeekTheme()
    {
        try { if (All.Count == 0) Load(); return ThemeOf(ById(Last)); }
        catch { return Themes.Get("dark", new Cfg()); }
    }

    // тема профілю (для вікна вибору, коли панелі ще немає)
    public static Theme ThemeOf(Profile p)
    {
        try
        {
            if (p != null)
            {
                string ini = Path.Combine(DirOf(p), "neobar.ini");
                if (System.IO.File.Exists(ini))
                {
                    Cfg c = new Cfg();
                    Store.Load(ini, c, new List<Entry>());
                    return Themes.Get(c.ThemeId, c);
                }
            }
        }
        catch { }
        return Themes.Get("dark", new Cfg());
    }

    // вибір профілю під час запуску: false - користувач закрив вікно (вихід із програми)
    public static bool Choose()
    {
        Profile last = ById(Last);
        if (last != null && !last.HasPin) { Cur = last; return true; }
        Profile chosen = null;
        try
        {
            using (ProfilePicker f = new ProfilePicker(last))
            {
                f.ShowDialog();
                chosen = f.Chosen;
            }
        }
        catch (Exception ex) { Util.ErrorLog(ex); if (last == null && All.Count > 0) chosen = null; }
        if (chosen == null) return false;
        Cur = chosen;
        Last = chosen.Id;
        Save();
        return true;
    }

    // перезапуск програми (новий профіль або вікно вибору): нова копія чекає, поки ця закриється
    public static bool Restart()
    {
        try { using (Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--restarted") { UseShellExecute = false })) { } return true; }
        catch (Exception ex) { Util.ErrorLog(ex); return false; }
    }
}

// ----------------------------------------------------------------------
//  Малювання аватара профілю й піктограм, яких немає серед загальних
// ----------------------------------------------------------------------
static class ProfileArt
{
    public static readonly List<string> Styles = new List<string> { "flat", "light", "glass", "neon", "plate", "spark", "heart", "leaf", "sketch", "pumpkin", "ornament", "egg", "balloon" };
    public static readonly string[] StyleTitles = { L.T("Темна"), L.T("Світла"), L.T("Скляна"), L.T("Кіберпанк"), L.T("Стімпанк"), L.T("Урбаністична"), L.T("Гламурна"), L.T("Природна"), L.T("Артистична"),
                                                    L.T("Хелловін"), L.T("Різдвяна"), L.T("Великодня"), L.T("День народження") };

    // стиль профілю: збережений, а якщо його ще немає - за темою з налаштувань профілю (читається один раз)
    public static string StyleOf(Profile p)
    {
        if (p == null) return "flat";
        if (p.Style.Length == 0 || !Styles.Contains(p.Style))
        {
            try { p.Style = StyleOfTheme(Prof.ThemeOf(p)); } catch { p.Style = "flat"; }
        }
        return p.Style;
    }

    // тема профілю змінилась (запуск панелі, «Застосувати») - значок переходить у її стиль і це запам'ятовується;
    // колір лишається «тим самим за місцем» у палітрі нової теми (латунь стімпанку -> корал світлої тощо)
    public static void SyncStyle(Theme t)
    {
        Profile p = Prof.Cur;
        if (p == null || t == null) return;
        string st = StyleOfTheme(t);
        if (p.Style == st) return;
        p.Style = st;
        if (p.Shade >= 0) p.Color = Palette(st)[p.Shade];
        Prof.Save();
    }

    // Палітри значків: для кожного стилю теми - вісім кольорів, що з нею асоціюються
    static readonly Dictionary<string, Color[]> palettes = new Dictionary<string, Color[]>();
    static readonly Dictionary<string, string[]> shadeNames = new Dictionary<string, string[]>();

    static void Pal(string style, params object[] a)
    {
        Color[] c = new Color[a.Length / 2];
        string[] n = new string[a.Length / 2];
        for (int i = 0; i < c.Length; i++) { n[i] = (string)a[i * 2]; int v = (int)a[i * 2 + 1]; c[i] = Color.FromArgb(255, (v >> 16) & 255, (v >> 8) & 255, v & 255); }
        palettes[style] = c;
        shadeNames[style] = n;
    }

    static ProfileArt()
    {
        Pal("flat", L.T("Сапфір"), 0x4678E6, L.T("Смарагд"), 0x28A073, L.T("Бурштин"), 0xE18728, L.T("Гранат"), 0xC83C55, L.T("Аметист"), 0x8C5AD7, L.T("Бірюза"), 0x1EA0B4, L.T("Мідь"), 0xBE693C, L.T("Графіт"), 0x646E82);
        Pal("light", L.T("Небо"), 0x5096F0, L.T("М'ята"), 0x3CB98C, L.T("Корал"), 0xF5785F, L.T("Лаванда"), 0x967DE6, L.T("Соняшник"), 0xEBB428, L.T("Троянда"), 0xE65F8C, L.T("Аква"), 0x28AFC8, L.T("Шавлія"), 0x78966E);
        Pal("glass", L.T("Лід"), 0x78B4EB, L.T("Аква"), 0x46C8D7, L.T("Морська піна"), 0x5FCDAA, L.T("Бузок"), 0xAA91EB, L.T("Барвінок"), 0x6E82E6, L.T("Перлина"), 0xDCAAC8, L.T("Срібло"), 0x96A5B9, L.T("Синє скло"), 0x3C78D2);
        Pal("neon", L.T("Маджента"), 0xFF28C8, L.T("Кібер-ціан"), 0x00E6FF, L.T("Кислотний лайм"), 0xA0FF28, L.T("Електрик"), 0x8C46FF, L.T("Лазерний помаранч"), 0xFF781E, L.T("Гаряча рожева"), 0xFF3C78, L.T("Ультрамарин"), 0x285AFF, L.T("Токсичний жовтий"), 0xF0E61E);
        Pal("plate", L.T("Латунь"), 0xC4A050, L.T("Мідь"), 0xB86E40, L.T("Бронза"), 0x966937, L.T("Патина"), 0x469682, L.T("Іржа"), 0xA54B2D, L.T("Червоне дерево"), 0x783228, L.T("Вороно-сталь"), 0x555F69, L.T("Старе золото"), 0xCDAA3C);
        Pal("spark", L.T("Сигнальний помаранч"), 0xF5781E, L.T("Дорожній жовтий"), 0xF0C31E, L.T("Синій метро"), 0x286EC8, L.T("Графіті"), 0xDC323C, L.T("Бетон"), 0x8C9196, L.T("Асфальт"), 0x464B55, L.T("Цегла"), 0xAA503C, L.T("Неонова вивіска"), 0x3CC8AA);
        Pal("heart", L.T("Троянда"), 0xEB5A96, L.T("Фуксія"), 0xDC288C, L.T("Персик"), 0xFA9678, L.T("Бузок"), 0xBE82DC, L.T("Шампань"), 0xE1B98C, L.T("Рубін"), 0xC81E50, L.T("Рум'янець"), 0xF5A0B4, L.T("Ягідний"), 0x96286E);
        Pal("leaf", L.T("Мох"), 0x6E9146, L.T("Папороть"), 0x3C965A, L.T("Небо"), 0x50A0DC, L.T("Земля"), 0x8C6441, L.T("Пшениця"), 0xD7B45A, L.T("Сосна"), 0x236950, L.T("Ягода"), 0xAA325A, L.T("Річка"), 0x288CA0);
        Pal("pumpkin", L.T("Гарбуз"), 0xF0821E, L.T("Полум'я"), 0xE65A14, L.T("Бурштин"), 0xF5AA28, L.T("Відьмин фіолет"), 0x8C46C8, L.T("Отрута"), 0x78BE3C, L.T("Кров"), 0xB4283C, L.T("Кістка"), 0xC8BEA0, L.T("Ніч"), 0x463C5A);
        Pal("ornament", L.T("Сапфір"), 0x3870E0, L.T("Іній"), 0x96C4F6, L.T("Сніг"), 0xE2EAF6, L.T("Ультрамарин"), 0x244AAA, L.T("Лід"), 0x60C4EC, L.T("Барвінок"), 0x788CDC, L.T("Срібло"), 0xA0AABE, L.T("Зимова ніч"), 0x1E3264);
        Pal("egg", L.T("Рожева"), 0xF6A4B2, L.T("Фісташкова"), 0xA0D296, L.T("Жовтенька"), 0xF6D06E, L.T("Бузкова"), 0xB6A8EC, L.T("Блакитна"), 0x8CC4EC, L.T("Персикова"), 0xF8B48C, L.T("М'ятна"), 0x8CD8C0, L.T("Писанкова червона"), 0xD2504B);
        Pal("balloon", L.T("Рожева"), 0xFF78BE, L.T("Золота"), 0xFFD648, L.T("Блакитна"), 0x6ED6FF, L.T("Лаймова"), 0xB0F06E, L.T("Коралова"), 0xFF966E, L.T("Фіалкова"), 0xE296FF, L.T("Малинова"), 0xE6326E, L.T("Синя"), 0x3C78F0);
        Pal("sketch", L.T("Чорнило"), 0x2D4696, L.T("Сепія"), 0x8C5F3C, L.T("Вугілля"), 0x3C3C41, L.T("Кармін"), 0xB42832, L.T("Охра"), 0xC89132, L.T("Зелень"), 0x50823C, L.T("Берлінська лазур"), 0x1E5078, L.T("Умбра"), 0x6E462D);
    }

    public static Color[] Palette(string style) { Color[] c; return palettes.TryGetValue(style ?? "", out c) ? c : palettes["flat"]; }
    public static string[] ShadeNames(string style) { string[] n; return shadeNames.TryGetValue(style ?? "", out n) ? n : shadeNames["flat"]; }

    // найближчий колір палітри (для старих профілів із довільним кольором)
    public static int Nearest(string style, Color c)
    {
        Color[] pal = Palette(style);
        int best = 0, bd = int.MaxValue;
        for (int i = 0; i < pal.Length; i++)
        {
            int dr = pal[i].R - c.R, dg = pal[i].G - c.G, db = pal[i].B - c.B;
            int d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    // колір значка: з палітри теми профілю, а для старих профілів - збережений
    public static Color ColorOf(Profile p)
    {
        if (p == null) return Color.Gray;
        if (p.Shade >= 0 && p.Shade < 8) return Palette(StyleOf(p))[p.Shade];
        return p.Color;
    }

    // стиль значка, що відповідає темі панелі (для нових профілів)
    public static string StyleOfTheme(Theme t)
    {
        if (t == null) return "flat";
        if (t.Sketchy) return "sketch";
        if (t.Glass) return "glass";
        switch (t.Style)
        {
            case IconStyle.Flat: return "light";
            case IconStyle.Glass: return "glass";
            case IconStyle.Neon: return "neon";
            case IconStyle.Plate: return "plate";
            case IconStyle.Spark: return "spark";
            case IconStyle.Heart: return "heart";
            case IconStyle.Leaf: return "leaf";
            case IconStyle.Pumpkin: return "pumpkin";
            case IconStyle.Ornament: return "ornament";
            case IconStyle.Egg: return "egg";
            case IconStyle.Balloon: return "balloon";
        }
        return Util.Lum(Util.Opaque(t.Bg)) > 140 ? "light" : "flat";
    }

    // фото профілів: кешуються, доки файл не зміниться
    static readonly Dictionary<string, KeyValuePair<DateTime, Image>> photos = new Dictionary<string, KeyValuePair<DateTime, Image>>();

    public static Image PhotoImage(Profile p)
    {
        if (p == null || !p.Photo) return null;
        try
        {
            string f = Prof.PhotoOf(p);
            if (!System.IO.File.Exists(f)) return null;
            DateTime t = System.IO.File.GetLastWriteTimeUtc(f);
            KeyValuePair<DateTime, Image> kv;
            if (photos.TryGetValue(f, out kv) && kv.Key == t) return kv.Value;
            if (kv.Value != null) kv.Value.Dispose();
            Image img;
            byte[] png = Prof.PhotoBytes(p);
            if (png == null) return null;
            using (System.IO.MemoryStream ms = new System.IO.MemoryStream(png)) using (Image src = Image.FromStream(ms)) img = new Bitmap(src);
            photos[f] = new KeyValuePair<DateTime, Image>(t, img);
            return img;
        }
        catch { return null; }
    }

    // контур значка за стилем
    public static GraphicsPath ShapeOf(string style, RectangleF r)
    {
        GraphicsPath gp = new GraphicsPath();
        float w = r.Width, h = r.Height;
        switch (style)
        {
            case "spark":
            {
                float d = w * 0.36f;
                gp.AddArc(r.X, r.Y, d, d, 180, 90); gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                gp.CloseFigure();
                break;
            }
            case "heart":
            {
                float x = r.X, y = r.Y;
                gp.AddBezier(x + w * 0.5f, y + h * 0.98f, x + w * 0.12f, y + h * 0.72f, x - w * 0.02f, y + h * 0.42f, x + w * 0.06f, y + h * 0.22f);
                // верхня «западина» піднята (0.12 замість 0.24), щоб не залізати на літеру
                gp.AddBezier(x + w * 0.06f, y + h * 0.22f, x + w * 0.16f, y + h * 0.0f, x + w * 0.44f, y + h * 0.0f, x + w * 0.5f, y + h * 0.12f);
                gp.AddBezier(x + w * 0.5f, y + h * 0.12f, x + w * 0.56f, y + h * 0.0f, x + w * 0.84f, y + h * 0.0f, x + w * 0.94f, y + h * 0.22f);
                gp.AddBezier(x + w * 0.94f, y + h * 0.22f, x + w * 1.02f, y + h * 0.42f, x + w * 0.88f, y + h * 0.72f, x + w * 0.5f, y + h * 0.98f);
                gp.CloseFigure();
                break;
            }
            case "leaf":
            {
                // два протилежні кути гострі, два - дуже заокруглені
                float d = w * 0.9f, k = w * 0.12f;
                gp.AddArc(r.X, r.Y, d, d, 180, 90);
                gp.AddLine(r.X + d / 2f, r.Y, r.Right - k, r.Y);
                gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                gp.AddLine(r.Right - d / 2f, r.Bottom, r.X + k, r.Bottom);
                gp.CloseFigure();
                break;
            }
            case "pumpkin": case "ornament": case "egg": case "balloon":
                gp.Dispose();
                return Festive.Outline(FestiveStyle(style), r);
            default:
                gp.AddEllipse(r);
                break;
        }
        return gp;
    }

    static IconStyle FestiveStyle(string style)
    {
        return style == "pumpkin" ? IconStyle.Pumpkin : style == "ornament" ? IconStyle.Ornament : style == "egg" ? IconStyle.Egg : IconStyle.Balloon;
    }

    static FontFamily FamilyOf(string style)
    {
        string list = style == "sketch" ? "Segoe Print|Ink Free|Comic Sans MS|Segoe UI" : (style == "plate" ? "Georgia|Segoe UI Semibold|Segoe UI" :
                      (style == "spark" ? "Bahnschrift SemiBold|Bahnschrift|Segoe UI Semibold|Segoe UI" : "Segoe UI Semibold|Segoe UI|Arial"));
        foreach (string n in list.Split('|'))
            try { FontFamily f = new FontFamily(n); return f; } catch { }
        return new FontFamily(System.Drawing.Text.GenericFontFamilies.SansSerif);
    }

    // літера - контуром за її справжніми межами, тож стоїть точно в центрі (текст шрифтом «пливе» вгору чи вниз)
    static GraphicsPath Letter(string s, string style, RectangleF r, float scale)
    {
        GraphicsPath tp = new GraphicsPath();
        using (FontFamily ff = FamilyOf(style))
        {
            FontStyle fs = style == "plate" || style == "spark" ? FontStyle.Bold : FontStyle.Regular;
            if (!ff.IsStyleAvailable(fs)) fs = FontStyle.Regular;
            tp.AddString(s, ff, (int)fs, r.Height * scale, PointF.Empty, StringFormat.GenericTypographic);
        }
        RectangleF b = tp.GetBounds();
        if (b.Width > 0 && b.Height > 0)
            using (Matrix m = new Matrix())
            {
                m.Translate(r.X + r.Width / 2f - (b.X + b.Width / 2f), r.Y + r.Height / 2f - (b.Y + b.Height / 2f));
                tp.Transform(m);
            }
        return tp;
    }

    public static void Avatar(Graphics g, Rectangle r, Profile p, bool hot, Color ring) { Avatar(g, r, p, hot, ring, null, false); }

    // photo - фото для показу (вікно зміни профілю, ще не збережене); noPhoto - показати стиль, навіть якщо фото є
    public static void Avatar(Graphics g, Rectangle r, Profile p, bool hot, Color ring, Image photo, bool noPhoto)
    {
        SmoothingMode sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        PixelOffsetMode pm = g.PixelOffsetMode;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        try
        {
            Image img = noPhoto ? null : (photo ?? PhotoImage(p));
            string style = img != null ? "photo" : StyleOf(p);
            Color c = ColorOf(p);
            RectangleF rf = new RectangleF(r.X, r.Y, r.Width, r.Height);
            if (img != null)
            {
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddEllipse(rf);
                    GraphicsState st = g.Save();
                    g.SetClip(gp, CombineMode.Intersect);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img, rf);
                    g.Restore(st);
                    using (Pen pn = new Pen(Color.FromArgb(60, 0, 0, 0), 1f)) g.DrawPath(pn, gp);
                }
            }
            else DrawStyled(g, rf, style, c, p != null ? p.Initial : "?");
            if (hot)
            {
                // кільце наведення з невеликим проміжком, тієї самої форми, що й значок
                float pw = Math.Max(1.5f, r.Width * 0.065f), off = pw / 2f + Math.Max(1.5f, r.Width * 0.05f);
                bool fest = img == null && (style == "pumpkin" || style == "ornament" || style == "egg" || style == "balloon");
                using (GraphicsPath ring2 = fest ? Festive.Ring(FestiveStyle(style), rf, off) : ShapeOf(img != null ? "flat" : style, RectangleF.Inflate(rf, off, off)))
                using (Pen pn = new Pen(ring, pw)) g.DrawPath(pn, ring2);
            }
        }
        catch { }
        g.PixelOffsetMode = pm;
        g.SmoothingMode = sm;
    }

    static void DrawStyled(Graphics g, RectangleF r, string style, Color c, string s)
    {
        float w = r.Width;
        using (GraphicsPath shape = ShapeOf(style, r))
        {
            switch (style)
            {
                case "glass":
                {
                    using (LinearGradientBrush b = new LinearGradientBrush(RectangleF.Inflate(r, 0, 1), Color.FromArgb(210, Util.Blend(c, Color.White, 0.3f)), Color.FromArgb(190, Util.Blend(c, Color.Black, 0.2f)), 90f))
                        g.FillPath(b, shape);
                    GraphicsState st = g.Save();
                    g.SetClip(shape, CombineMode.Intersect);
                    using (SolidBrush gl = new SolidBrush(Color.FromArgb(70, 255, 255, 255))) g.FillEllipse(gl, r.X - w * 0.1f, r.Y - w * 0.45f, w * 1.2f, w * 0.85f);
                    g.Restore(st);
                    using (Pen pn = new Pen(Color.FromArgb(150, 255, 255, 255), Math.Max(1f, w * 0.03f))) g.DrawPath(pn, shape);
                    using (GraphicsPath tp = Letter(s, style, r, 0.48f)) using (SolidBrush wb = new SolidBrush(Color.White)) g.FillPath(wb, tp);
                    break;
                }
                case "light":
                {
                    using (LinearGradientBrush b = new LinearGradientBrush(RectangleF.Inflate(r, 0, 1), Color.White, Util.Blend(c, Color.White, 0.8f), 90f))
                        g.FillPath(b, shape);
                    using (Pen pn = new Pen(Util.Blend(c, Color.White, 0.15f), Math.Max(1.3f, w * 0.05f))) g.DrawEllipse(pn, RectangleF.Inflate(r, -w * 0.025f, -w * 0.025f));
                    using (GraphicsPath tp = Letter(s, style, r, 0.48f)) using (SolidBrush wb = new SolidBrush(Util.Blend(c, Color.Black, 0.18f))) g.FillPath(wb, tp);
                    break;
                }
                case "neon":
                {
                    Color glow = Util.Blend(c, Color.White, 0.25f);
                    using (SolidBrush b = new SolidBrush(Util.Blend(c, Color.Black, 0.84f))) g.FillPath(b, shape);
                    foreach (float[] k in new float[][] { new float[] { 50, 0.16f }, new float[] { 110, 0.09f }, new float[] { 255, 0.035f } })
                        using (Pen pn = new Pen(Color.FromArgb((int)k[0], glow), Math.Max(1f, w * k[1]))) g.DrawPath(pn, shape);
                    using (GraphicsPath tp = Letter(s, style, r, 0.46f))
                    {
                        using (Pen gp2 = new Pen(Color.FromArgb(90, glow), Math.Max(1.5f, w * 0.07f)) { LineJoin = LineJoin.Round }) g.DrawPath(gp2, tp);
                        using (SolidBrush wb = new SolidBrush(Util.Blend(glow, Color.White, 0.35f))) g.FillPath(wb, tp);
                    }
                    break;
                }
                case "plate":
                {
                    Color brass = Util.Blend(c, Color.FromArgb(205, 140, 60), 0.55f);
                    using (LinearGradientBrush b = new LinearGradientBrush(RectangleF.Inflate(r, 0, 1), Util.Blend(brass, Color.White, 0.35f), Util.Blend(brass, Color.Black, 0.35f), 70f))
                        g.FillPath(b, shape);
                    using (Pen pn = new Pen(Util.Blend(brass, Color.Black, 0.45f), Math.Max(1f, w * 0.035f))) g.DrawEllipse(pn, RectangleF.Inflate(r, -w * 0.11f, -w * 0.11f));
                    using (SolidBrush rv = new SolidBrush(Util.Blend(brass, Color.White, 0.5f)))
                    {
                        float d = Math.Max(2f, w * 0.08f), rr = w * 0.39f, cx = r.X + w / 2f, cy = r.Y + w / 2f;
                        foreach (double a in new double[] { 45, 135, 225, 315 })
                        {
                            double rad = a * Math.PI / 180;
                            g.FillEllipse(rv, cx + (float)Math.Cos(rad) * rr - d / 2f, cy + (float)Math.Sin(rad) * rr - d / 2f, d, d);
                        }
                    }
                    using (GraphicsPath tp = Letter(s, style, r, 0.44f))
                    {
                        using (Matrix m = new Matrix()) { m.Translate(0, Math.Max(1f, w * 0.025f)); tp.Transform(m); }
                        using (SolidBrush hl = new SolidBrush(Color.FromArgb(110, 255, 240, 210))) g.FillPath(hl, tp);
                        using (Matrix m = new Matrix()) { m.Translate(0, -Math.Max(1f, w * 0.025f)); tp.Transform(m); }
                        using (SolidBrush db = new SolidBrush(Util.Blend(brass, Color.Black, 0.62f))) g.FillPath(db, tp);
                    }
                    break;
                }
                case "spark":
                {
                    using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, shape);
                    GraphicsState st = g.Save();
                    g.SetClip(shape, CombineMode.Intersect);
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(60, 255, 255, 255)))
                        g.FillPolygon(sb, new PointF[] { new PointF(r.X + w * 0.55f, r.Y), new PointF(r.Right, r.Y), new PointF(r.Right, r.Y + w * 0.2f), new PointF(r.X + w * 0.2f, r.Bottom), new PointF(r.X, r.Bottom), new PointF(r.X, r.Y + w * 0.8f) });
                    g.Restore(st);
                    using (GraphicsPath tp = Letter(s, style, r, 0.5f)) using (SolidBrush wb = new SolidBrush(Util.Readable(Color.White, c, 90))) g.FillPath(wb, tp);
                    break;
                }
                case "heart":
                {
                    using (LinearGradientBrush b = new LinearGradientBrush(RectangleF.Inflate(r, 0, 1), Util.Blend(c, Color.White, 0.25f), Util.Blend(c, Color.Black, 0.15f), 90f))
                        g.FillPath(b, shape);
                    using (SolidBrush gl = new SolidBrush(Color.FromArgb(110, 255, 255, 255))) g.FillEllipse(gl, r.X + w * 0.16f, r.Y + w * 0.14f, w * 0.2f, w * 0.13f);
                    RectangleF lr = new RectangleF(r.X, r.Y + w * 0.08f, w, w * 0.8f);
                    using (GraphicsPath tp = Letter(s, style, lr, 0.5f)) using (SolidBrush wb = new SolidBrush(Color.White)) g.FillPath(wb, tp);
                    break;
                }
                case "leaf":
                {
                    using (LinearGradientBrush b = new LinearGradientBrush(RectangleF.Inflate(r, 1, 1), Util.Blend(c, Color.White, 0.22f), Util.Blend(c, Color.Black, 0.25f), 45f))
                        g.FillPath(b, shape);
                    using (Pen vein = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1f, w * 0.035f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLine(vein, r.Right - w * 0.1f, r.Y + w * 0.1f, r.X + w * 0.12f, r.Bottom - w * 0.12f);
                    using (GraphicsPath tp = Letter(s, style, r, 0.46f)) using (SolidBrush wb = new SolidBrush(Color.White)) g.FillPath(wb, tp);
                    break;
                }
                case "pumpkin": case "ornament": case "egg": case "balloon":
                {
                    Color ink, sh; int glow;
                    RectangleF fc = Festive.Draw(g, FestiveStyle(style), r, c, out ink, out sh, out glow);
                    float k = style == "egg" ? 0.4f : 0.42f;
                    RectangleF lr = new RectangleF(r.X, fc.Y + fc.Height / 2f - w / 2f, w, w);
                    using (GraphicsPath tp = Letter(s, style, lr, k))
                    {
                        if (glow > 0 && w >= 28)
                            using (Pen gp2 = new Pen(Color.FromArgb(glow, ink), Math.Max(1.5f, w * 0.06f)) { LineJoin = LineJoin.Round }) g.DrawPath(gp2, tp);
                        if (!sh.IsEmpty)
                            using (Matrix m = new Matrix())
                            {
                                float o = Math.Max(0.6f, w * 0.015f);
                                m.Translate(o, o); tp.Transform(m);
                                using (SolidBrush sb = new SolidBrush(sh)) g.FillPath(sb, tp);
                                m.Reset(); m.Translate(-o, -o); tp.Transform(m);
                            }
                        using (SolidBrush wb = new SolidBrush(ink)) g.FillPath(wb, tp);
                    }
                    break;
                }
                case "sketch":
                {
                    Rectangle ri = Rectangle.Round(r);
                    using (SolidBrush b = new SolidBrush(Util.Blend(c, Color.FromArgb(250, 246, 236), 0.78f))) g.FillEllipse(b, r);
                    GraphicsState st = g.Save();
                    g.SetClip(shape, CombineMode.Intersect);
                    using (Pen hp = new Pen(Color.FromArgb(55, c), Math.Max(1f, w * 0.025f)))
                        for (float x = -w; x < w * 2; x += w * 0.14f) g.DrawLine(hp, r.X + x, r.Bottom, r.X + x + w * 0.7f, r.Y);
                    g.Restore(st);
                    using (GraphicsPath wob = Sk.WobblyRound(ri, ri.Width / 2, new Random(ri.Width * 7 + c.R), 1.1f))
                    using (Pen pn = new Pen(Util.Blend(c, Color.Black, 0.2f), Math.Max(1.4f, w * 0.05f))) g.DrawPath(pn, wob);
                    using (GraphicsPath tp = Letter(s, style, r, 0.5f)) using (SolidBrush wb = new SolidBrush(Util.Blend(c, Color.Black, 0.35f))) g.FillPath(wb, tp);
                    break;
                }
                default:
                {
                    using (LinearGradientBrush b = new LinearGradientBrush(RectangleF.Inflate(r, 0, 1), Util.Blend(c, Color.White, 0.18f), Util.Blend(c, Color.Black, 0.18f), 90f))
                        g.FillPath(b, shape);
                    using (GraphicsPath tp = Letter(s, style, r, 0.48f)) using (SolidBrush wb = new SolidBrush(Color.White)) g.FillPath(wb, tp);
                    break;
                }
            }
        }
    }

    // user - людина, power - вимкнення, switch - зміна профілю (дві людини зі стрілкою), edit - олівець
    public static void Glyph(Graphics g, string key, RectangleF r, Color c)
    {
        SmoothingMode sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Math.Min(r.Width, r.Height), cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        using (Pen p = new Pen(c, Math.Max(1.4f, s * 0.1f)))
        {
            p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
            switch (key)
            {
                case "user":
                    g.DrawEllipse(p, cx - s * 0.18f, cy - s * 0.42f, s * 0.36f, s * 0.36f);
                    g.DrawArc(p, cx - s * 0.36f, cy + s * 0.02f, s * 0.72f, s * 0.7f, 200, 140);
                    break;
                case "switch":
                    g.DrawEllipse(p, cx - s * 0.38f, cy - s * 0.38f, s * 0.3f, s * 0.3f);
                    g.DrawArc(p, cx - s * 0.5f, cy - s * 0.02f, s * 0.54f, s * 0.55f, 200, 140);
                    g.DrawLine(p, cx + s * 0.08f, cy + s * 0.12f, cx + s * 0.46f, cy + s * 0.12f);
                    g.DrawLine(p, cx + s * 0.32f, cy - s * 0.02f, cx + s * 0.46f, cy + s * 0.12f);
                    g.DrawLine(p, cx + s * 0.32f, cy + s * 0.26f, cx + s * 0.46f, cy + s * 0.12f);
                    break;
                case "power":
                    g.DrawArc(p, cx - s * 0.36f, cy - s * 0.32f, s * 0.72f, s * 0.72f, -55, 290);
                    g.DrawLine(p, cx, cy - s * 0.44f, cx, cy - s * 0.04f);
                    break;
                case "edit":
                {
                    // чорнильниця й перо в ній
                    float lw = Math.Max(1.1f, s * 0.065f);
                    using (Pen fp = new Pen(c, lw) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        // чорнильниця: широке тіло й шийка
                        RectangleF body = new RectangleF(cx - s * 0.46f, cy + s * 0.14f, s * 0.46f, s * 0.3f);
                        using (GraphicsPath jar = new GraphicsPath())
                        {
                            float rr = s * 0.1f;
                            jar.AddArc(body.X, body.Y, rr, rr, 180, 90);
                            jar.AddArc(body.Right - rr, body.Y, rr, rr, 270, 90);
                            jar.AddArc(body.Right - rr, body.Bottom - rr, rr, rr, 0, 90);
                            jar.AddArc(body.X, body.Bottom - rr, rr, rr, 90, 90);
                            jar.CloseFigure();
                            g.DrawPath(fp, jar);
                        }
                        g.DrawRectangle(fp, cx - s * 0.33f, cy + s * 0.04f, s * 0.2f, s * 0.1f);
                        // перо: стрижень із горлечка, опахало вздовж нього
                        PointF bse = new PointF(cx - s * 0.12f, cy - s * 0.06f), tip = new PointF(cx + s * 0.44f, cy - s * 0.46f);
                        float dx = tip.X - bse.X, dy = tip.Y - bse.Y, len = (float)Math.Sqrt(dx * dx + dy * dy);
                        float ux = dx / len, uy = dy / len, nx = -uy, ny = ux;
                        Func<float, float, PointF> at = delegate (float along, float side) { return new PointF(bse.X + ux * len * along + nx * len * side, bse.Y + uy * len * along + ny * len * side); };
                        using (GraphicsPath vane = new GraphicsPath())
                        {
                            vane.AddBezier(bse, at(0.25f, 0.3f), at(0.75f, 0.24f), tip);
                            vane.AddBezier(tip, at(0.8f, -0.1f), at(0.4f, -0.14f), bse);
                            vane.CloseFigure();
                            g.DrawPath(fp, vane);
                        }
                        g.DrawLine(fp, cx - s * 0.23f, cy + s * 0.06f, at(0.7f, 0f).X, at(0.7f, 0f).Y);
                    }
                    break;
                }
            }
        }
        g.SmoothingMode = sm;
    }
}
