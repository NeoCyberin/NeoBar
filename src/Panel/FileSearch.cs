using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// ======================================================================
//  Розширений пошук файлів і папок :
//    1) індекс NTFS через MFT/USN - миттєво, потребує прав адміністратора
//    2) індекс пошуку Windows - швидко, без адміністратора
//    3) звичайний обхід дисків - запасний варіант
// ======================================================================

sealed class FileHit
{
    public string Name = "", Path = "";
    public bool IsDir;
    public long Size = -1;
    public DateTime Modified;
}

// ---------- розбір запиту ----------
sealed class FindSpec
{
    public readonly List<string> Words = new List<string>();   // підрядок у назві
    public readonly List<Regex> Wild = new List<Regex>();      // шаблон * ? на всю назву
    public readonly List<string> PathWords = new List<string>();
    public string[] Exts;            // null = будь-яке розширення
    public int Kind;                 // 0 усе, 1 лише файли, 2 лише папки
    public long MinSize = -1, MaxSize = -1;
    public DateTime After = DateTime.MinValue;

    public bool NeedStat { get { return MinSize >= 0 || MaxSize >= 0 || After > DateTime.MinValue; } }

    public bool Empty
    {
        get { return Words.Count == 0 && Wild.Count == 0 && PathWords.Count == 0 && Exts == null; }
    }

    // групи розширень для списку фільтрів
    public static readonly string[][] Groups =
    {
        null, null, null,
        new string[] { "png", "jpg", "jpeg", "gif", "bmp", "webp", "tif", "tiff", "ico", "svg", "heic", "raw", "cr2", "nef" },
        new string[] { "mp4", "mkv", "avi", "mov", "wmv", "webm", "flv", "m4v", "mpg", "mpeg", "ts", "m2ts" },
        new string[] { "mp3", "flac", "wav", "ogg", "m4a", "aac", "wma", "opus", "aiff" },
        new string[] { "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "rtf", "odt", "ods", "odp", "md", "csv", "djvu", "epub", "fb2" },
        new string[] { "exe", "msi", "bat", "cmd", "lnk", "url", "ps1" },
        new string[] { "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "cab" },
        new string[] { "blend", "fbx", "obj", "stl", "3ds", "dae", "gltf", "glb", "max", "c4d", "ztl", "spp", "sbs" }
    };

    public static FindSpec Parse(string q, int group)
    {
        FindSpec s = new FindSpec();
        if (group == 1) s.Kind = 1;
        else if (group == 2) s.Kind = 2;
        else if (group >= 3 && group < Groups.Length && Groups[group] != null)
        {
            s.Exts = Groups[group];
            s.Kind = 1;
        }

        foreach (string tok in Tokens(q ?? ""))
        {
            string t = tok;
            if (t.Length == 0) continue;
            int c = t.IndexOf(':');
            if (c > 0)
            {
                string k = t.Substring(0, c).ToLowerInvariant();
                string v = t.Substring(c + 1);
                if (k == "ext" || k == "роз")
                {
                    List<string> ex = new List<string>();
                    foreach (string p in v.Split(',', ';'))
                    {
                        string e = p.Trim().TrimStart('.').ToLowerInvariant();
                        if (e.Length > 0) ex.Add(e);
                    }
                    if (ex.Count > 0) { s.Exts = ex.ToArray(); s.Kind = 1; }
                    continue;
                }
                if (k == "path" || k == "шлях") { if (v.Length > 0) s.PathWords.Add(v); continue; }
                if (k == "file" || k == "файл") { s.Kind = 1; if (v.Length > 0) s.Words.Add(v); continue; }
                if (k == "folder" || k == "dir" || k == "папка") { s.Kind = 2; if (v.Length > 0) s.Words.Add(v); continue; }
                if (k == "size" || k == "розмір") { ParseSize(s, v); continue; }
                if (k == "dm" || k == "дата") { ParseDate(s, v); continue; }
            }
            if (t.IndexOf('*') >= 0 || t.IndexOf('?') >= 0)
            {
                try { s.Wild.Add(new Regex("^" + Regex.Escape(t).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)); }
                catch { s.Words.Add(t); }
                continue;
            }
            s.Words.Add(t);
        }
        return s;
    }

    // слова з урахуванням лапок
    static List<string> Tokens(string q)
    {
        List<string> res = new List<string>();
        StringBuilder sb = new StringBuilder();
        bool quote = false;
        foreach (char ch in q)
        {
            if (ch == '"') { quote = !quote; continue; }
            if (ch == ' ' && !quote)
            {
                if (sb.Length > 0) { res.Add(sb.ToString()); sb.Length = 0; }
                continue;
            }
            sb.Append(ch);
        }
        if (sb.Length > 0) res.Add(sb.ToString());
        return res;
    }

    static void ParseSize(FindSpec s, string v)
    {
        v = v.Trim().ToLowerInvariant();
        bool less = v.StartsWith("<"), more = v.StartsWith(">");
        v = v.TrimStart('<', '>', '=');
        long mul = 1;
        if (v.EndsWith("kb")) { mul = 1024; v = v.Substring(0, v.Length - 2); }
        else if (v.EndsWith("mb")) { mul = 1024 * 1024; v = v.Substring(0, v.Length - 2); }
        else if (v.EndsWith("gb")) { mul = 1024L * 1024 * 1024; v = v.Substring(0, v.Length - 2); }
        double d;
        if (!double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return;
        long bytes = (long)(d * mul);
        if (less) s.MaxSize = bytes;
        else if (more) s.MinSize = bytes;
        else { s.MinSize = bytes; s.MaxSize = bytes; }
        s.Kind = 1;
    }

    static void ParseDate(FindSpec s, string v)
    {
        DateTime now = DateTime.Now;
        switch (v.Trim().ToLowerInvariant())
        {
            case "today": case "сьогодні": s.After = now.Date; break;
            case "yesterday": case "вчора": s.After = now.Date.AddDays(-1); break;
            case "week": case "тиждень": s.After = now.Date.AddDays(-7); break;
            case "month": case "місяць": s.After = now.Date.AddMonths(-1); break;
            case "year": case "рік": s.After = now.Date.AddYears(-1); break;
        }
    }

    // швидка перевірка лише за назвою (без побудови шляху)
    public bool NameOk(string name, bool isDir)
    {
        if (Kind == 1 && isDir) return false;
        if (Kind == 2 && !isDir) return false;
        if (Exts != null)
        {
            if (isDir) return false;
            int d = name.LastIndexOf('.');
            if (d < 0) return false;
            string e = name.Substring(d + 1).ToLowerInvariant();
            bool ok = false;
            for (int i = 0; i < Exts.Length; i++) if (Exts[i] == e) { ok = true; break; }
            if (!ok) return false;
        }
        for (int i = 0; i < Words.Count; i++)
            if (name.IndexOf(Words[i], StringComparison.OrdinalIgnoreCase) < 0) return false;
        for (int i = 0; i < Wild.Count; i++)
            if (!Wild[i].IsMatch(name)) return false;
        return true;
    }

    public bool PathOk(string path)
    {
        for (int i = 0; i < PathWords.Count; i++)
            if (path.IndexOf(PathWords[i], StringComparison.OrdinalIgnoreCase) < 0) return false;
        return true;
    }

    public bool StatOk(long size, DateTime mod)
    {
        if (MinSize >= 0 && size < MinSize) return false;
        if (MaxSize >= 0 && size > MaxSize) return false;
        if (After > DateTime.MinValue && mod < After) return false;
        return true;
    }
}

// ---------- Win32 для читання MFT ----------
static class WinFs
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);


}

// ---------- індекс NTFS  ----------
// Список імен з MFT (FSCTL_ENUM_USN_DATA) у компактних масивах: номер запису, батько, ім'я (однакові
// імена - один рядок), тека чи файл. Між пошуками індекс доповнюється з журналу змін NTFS (USN), тож
// нові, видалені й перейменовані файли видно без перезапуску. Без пошуку 10 хвилин - пам'ять звільняється.
static class MftIndex
{
    sealed class Rec
    {
        public long Parent;
        public string Name;
        public bool Dir;
    }

    sealed class Vol
    {
        public string Root;                 // "C:\"
        public int N;
        public long[] Frn, Par;             // відсортовано за Frn
        public string[] Name;
        public bool[] Dir, Dead;            // Dead - запис видалено або змінено (нова версія - в Added)
        public readonly Dictionary<long, Rec> Added = new Dictionary<long, Rec>();
        public long Journal, NextUsn;       // журнал змін тому і звідки читати далі
        public bool Stale;                  // журнал скинуто чи переповнено - потрібна повна перебудова

        public int Find(long frn)
        {
            int i = Array.BinarySearch(Frn, 0, N, frn);
            return i >= 0 && !Dead[i] ? i : -1;
        }

        public bool Get(long frn, out long parent, out string name)
        {
            Rec r;
            if (Added.TryGetValue(frn, out r)) { parent = r.Parent; name = r.Name; return r.Name != null; }
            int i = Find(frn);
            if (i < 0) { parent = 0; name = null; return false; }
            parent = Par[i]; name = Name[i];
            return true;
        }

        public int Live { get { int c = 0; foreach (Rec r in Added.Values) if (r.Name != null) c++; for (int i = 0; i < N; i++) if (!Dead[i]) c++; return c; } }
    }

    const uint FSCTL_ENUM_USN_DATA = 0x000900B3, FSCTL_READ_USN_JOURNAL = 0x000900BB, FSCTL_QUERY_USN_JOURNAL = 0x000900F4;
    const int MaxPerVolume = 6000000;      // запобіжник проти величезних томів
    const int IdleMinutes = 10;

    static readonly object gate = new object(), qgate = new object();
    static List<Vol> vols = new List<Vol>();
    static Thread worker;
    static volatile bool ready, building, failed;
    static volatile int count;
    static long lastUse, lastSync;
    static System.Threading.Timer idle;

    public static bool Ready { get { return ready; } }
    public static bool Building { get { return building; } }
    public static bool Failed { get { return failed; } }
    public static int Count { get { return count; } }

    public static void Begin()
    {
        lock (gate)
        {
            Interlocked.Exchange(ref lastUse, Environment.TickCount);
            if (ready || building || failed || worker != null) return;
            building = true;
            worker = new Thread(delegate () { try { Build(); } catch (Exception ex) { Util.ErrorLog(ex); } finally { lock (gate) worker = null; } });
            worker.IsBackground = true;
            worker.Priority = ThreadPriority.BelowNormal;
            worker.Start();
        }
    }

    // звільнити пам'ять, якщо пошуком довго не користувались (наступний пошук збудує індекс знову)
    static void IdleCheck(object o)
    {
        lock (gate)
        {
            if (!ready || building) return;
            if (unchecked(Environment.TickCount - (int)Interlocked.Read(ref lastUse)) < IdleMinutes * 60000) return;
            vols = new List<Vol>();
            ready = false; count = 0;
            if (idle != null) { idle.Dispose(); idle = null; }
        }
        SbLog.W(SbLog.Panel, "індекс NTFS звільнено (пошуком не користувались " + IdleMinutes + " хв)");
        GC.Collect();
    }

    static void Build()
    {
        List<Vol> built = new List<Vol>();
        int total = 0;
        try
        {
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    if (!string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) continue;
                    Vol v = ReadVolume(d.RootDirectory.FullName);
                    if (v != null)
                    {
                        built.Add(v);
                        total += v.N;
                        count = total;
                    }
                }
                catch (Exception ex) { Util.ErrorLog(ex); }
            }
        }
        catch { }

        lock (gate)
        {
            building = false;
            if (built.Count == 0) { failed = true; ready = false; }
            else
            {
                vols = built; count = total; ready = true;
                lastSync = Environment.TickCount;
                if (idle == null) idle = new System.Threading.Timer(IdleCheck, null, 60000, 60000);
            }
        }
    }

    static IntPtr OpenVol(string root)
    {
        IntPtr h = WinFs.CreateFileW("\\\\.\\" + root.Substring(0, 2), 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        return h == new IntPtr(-1) ? IntPtr.Zero : h;
    }

    // журнал змін: ідентифікатор і наступний номер запису
    static bool QueryJournal(IntPtr h, out long id, out long next)
    {
        id = next = 0;
        IntPtr o = Marshal.AllocHGlobal(80);
        try
        {
            int ret;
            if (!Win.DeviceIoControl(h, FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0, o, 80, out ret, IntPtr.Zero) || ret < 24) return false;
            id = Marshal.ReadInt64(o, 0);
            next = Marshal.ReadInt64(o, 16);
            return true;
        }
        finally { Marshal.FreeHGlobal(o); }
    }

    static Vol ReadVolume(string root)
    {
        IntPtr h = OpenVol(root);
        if (h == IntPtr.Zero) return null;    // немає прав адміністратора
        Vol v = new Vol();
        v.Root = root;
        int cap = 1 << 16, n = 0;
        long[] frn = new long[cap], par = new long[cap];
        string[] name = new string[cap];
        bool[] dir = new bool[cap];
        Dictionary<string, string> pool = new Dictionary<string, string>(StringComparer.Ordinal);   // однакові імена - один рядок
        int bufSize = 1 << 20;
        IntPtr buf = Marshal.AllocHGlobal(bufSize);
        IntPtr inBuf = Marshal.AllocHGlobal(24);
        try
        {
            // журнал - до читання MFT: зміни під час читання потраплять у перше оновлення
            if (!QueryJournal(h, out v.Journal, out v.NextUsn)) v.Stale = true;
            long start = 0;
            while (n < MaxPerVolume)
            {
                Marshal.WriteInt64(inBuf, 0, start);
                Marshal.WriteInt64(inBuf, 8, 0);
                Marshal.WriteInt64(inBuf, 16, long.MaxValue);
                int ret;
                if (!Win.DeviceIoControl(h, FSCTL_ENUM_USN_DATA, inBuf, 24, buf, bufSize, out ret, IntPtr.Zero)) break;
                if (ret <= 8) break;
                start = Marshal.ReadInt64(buf, 0);
                long baseAddr = buf.ToInt64();
                int off = 8;
                while (off + 60 <= ret)
                {
                    int recLen = Marshal.ReadInt32(buf, off);
                    if (recLen <= 0 || off + recLen > ret) break;
                    if (Marshal.ReadInt16(buf, off + 4) == 2)
                    {
                        int nameLen = (ushort)Marshal.ReadInt16(buf, off + 56);
                        int nameOff = (ushort)Marshal.ReadInt16(buf, off + 58);
                        if (nameLen > 0 && nameOff >= 60 && off + nameOff + nameLen <= ret)
                        {
                            if (n == cap)
                            {
                                cap *= 2;
                                Array.Resize(ref frn, cap); Array.Resize(ref par, cap);
                                Array.Resize(ref name, cap); Array.Resize(ref dir, cap);
                            }
                            frn[n] = Marshal.ReadInt64(buf, off + 8);
                            par[n] = Marshal.ReadInt64(buf, off + 16);
                            dir[n] = (Marshal.ReadInt32(buf, off + 52) & 0x10) != 0;
                            string s = Marshal.PtrToStringUni(new IntPtr(baseAddr + off + nameOff), nameLen / 2), p;
                            if (pool.TryGetValue(s, out p)) s = p; else pool[s] = s;
                            name[n] = s;
                            n++;
                        }
                    }
                    off += recLen;
                }
            }
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
        finally
        {
            Marshal.FreeHGlobal(buf);
            Marshal.FreeHGlobal(inBuf);
            Win.CloseHandle(h);
        }
        if (n == 0) return null;
        pool = null;
        // впорядкувати за номером запису (пошук батька - двійковий)
        int[] idx = new int[n];
        for (int i = 0; i < n; i++) idx[i] = i;
        long[] keys = new long[n];
        Array.Copy(frn, keys, n);
        Array.Sort(keys, idx);
        v.N = n;
        v.Frn = keys;
        v.Par = new long[n]; v.Name = new string[n]; v.Dir = new bool[n]; v.Dead = new bool[n];
        for (int i = 0; i < n; i++) { int k = idx[i]; v.Par[i] = par[k]; v.Name[i] = name[k]; v.Dir[i] = dir[k]; }
        return v;
    }

    // ---------- оновлення з журналу змін ----------
    const uint ReasonCreate = 0x100, ReasonDelete = 0x200, ReasonRenameNew = 0x2000;

    static void Sync()
    {
        if (unchecked(Environment.TickCount - (int)Interlocked.Read(ref lastSync)) < 1500) return;
        Interlocked.Exchange(ref lastSync, Environment.TickCount);
        bool rebuild = false;
        foreach (Vol v in vols)
        {
            if (!v.Stale) SyncVol(v);
            if (v.Stale || v.Added.Count > 200000) rebuild = true;
        }
        if (!rebuild) return;
        // журнал скинуто чи змін забагато - тихо перебудовуємо, старий індекс працює до кінця
        lock (gate)
        {
            if (building || worker != null) return;
            building = true;
            worker = new Thread(delegate () { try { Build(); } catch (Exception ex) { Util.ErrorLog(ex); } finally { lock (gate) worker = null; } });
            worker.IsBackground = true;
            worker.Priority = ThreadPriority.BelowNormal;
            worker.Start();
        }
    }

    static void SyncVol(Vol v)
    {
        IntPtr h = OpenVol(v.Root);
        if (h == IntPtr.Zero) return;
        const int bufSize = 1 << 16;
        IntPtr buf = Marshal.AllocHGlobal(bufSize);
        IntPtr inBuf = Marshal.AllocHGlobal(40);
        try
        {
            long id, next;
            if (!QueryJournal(h, out id, out next) || id != v.Journal) { v.Stale = true; return; }
            for (int guard = 0; guard < 4096 && v.NextUsn < next; guard++)
            {
                Marshal.WriteInt64(inBuf, 0, v.NextUsn);
                Marshal.WriteInt32(inBuf, 8, unchecked((int)(ReasonCreate | ReasonDelete | ReasonRenameNew)));
                Marshal.WriteInt32(inBuf, 12, 0);       // не чекати закриття файлу
                Marshal.WriteInt64(inBuf, 16, 0);       // не чекати нових записів
                Marshal.WriteInt64(inBuf, 24, 0);
                Marshal.WriteInt64(inBuf, 32, v.Journal);
                int ret;
                if (!Win.DeviceIoControl(h, FSCTL_READ_USN_JOURNAL, inBuf, 40, buf, bufSize, out ret, IntPtr.Zero)) { v.Stale = true; return; }
                if (ret <= 8) break;
                long nu = Marshal.ReadInt64(buf, 0);
                long baseAddr = buf.ToInt64();
                int off = 8;
                while (off + 60 <= ret)
                {
                    int recLen = Marshal.ReadInt32(buf, off);
                    if (recLen <= 0 || off + recLen > ret) break;
                    if (Marshal.ReadInt16(buf, off + 4) == 2)
                    {
                        long frn = Marshal.ReadInt64(buf, off + 8);
                        uint reason = (uint)Marshal.ReadInt32(buf, off + 40);
                        if ((reason & ReasonDelete) != 0) Remove(v, frn);
                        else
                        {
                            int nameLen = (ushort)Marshal.ReadInt16(buf, off + 56), nameOff = (ushort)Marshal.ReadInt16(buf, off + 58);
                            if (nameLen > 0 && off + nameOff + nameLen <= ret)
                            {
                                Rec r = new Rec();
                                r.Parent = Marshal.ReadInt64(buf, off + 16);
                                r.Dir = (Marshal.ReadInt32(buf, off + 52) & 0x10) != 0;
                                r.Name = Marshal.PtrToStringUni(new IntPtr(baseAddr + off + nameOff), nameLen / 2);
                                int i = Array.BinarySearch(v.Frn, 0, v.N, frn);
                                if (i >= 0) v.Dead[i] = true;
                                v.Added[frn] = r;
                            }
                        }
                    }
                    off += recLen;
                }
                if (nu <= v.NextUsn) break;
                v.NextUsn = nu;
            }
        }
        catch (Exception ex) { Util.ErrorLog(ex); v.Stale = true; }
        finally
        {
            Marshal.FreeHGlobal(buf);
            Marshal.FreeHGlobal(inBuf);
            Win.CloseHandle(h);
        }
    }

    static void Remove(Vol v, long frn)
    {
        int i = Array.BinarySearch(v.Frn, 0, v.N, frn);
        if (i >= 0) v.Dead[i] = true;
        if (v.Added.ContainsKey(frn)) v.Added.Remove(frn);
    }

    static string PathOf(Vol v, string name, long parent)
    {
        string[] parts = new string[64];
        int n = 0;
        parts[n++] = name;
        long p = parent;
        for (int i = 0; i < 60; i++)
        {
            if ((p & 0xFFFFFFFFFFFFL) == 5 || p == 0) break;      // 5 = корінь тому
            long up; string nm;
            if (!v.Get(p, out up, out nm)) break;
            if (n >= parts.Length) break;
            parts[n++] = nm;
            p = up;
        }
        StringBuilder sb = new StringBuilder(v.Root);
        for (int i = n - 1; i >= 0; i--)
        {
            sb.Append(parts[i]);
            if (i > 0) sb.Append('\\');
        }
        return sb.ToString();
    }

    public static List<FileHit> Query(FindSpec s, int max)
    {
        List<FileHit> res = new List<FileHit>();
        Interlocked.Exchange(ref lastUse, Environment.TickCount);
        lock (qgate)
        {
            List<Vol> snap;
            lock (gate) { snap = vols; }
            try { Sync(); } catch (Exception ex) { Util.ErrorLog(ex); }
            int total = 0;
            foreach (Vol v in snap) total += v.Live;
            count = total;
            foreach (Vol v in snap)
            {
                for (int i = 0; i < v.N; i++)
                {
                    if (v.Dead[i] || !s.NameOk(v.Name[i], v.Dir[i])) continue;
                    if (Add(res, s, v, v.Name[i], v.Par[i], v.Dir[i]) && res.Count >= max) return res;
                }
                foreach (Rec r in v.Added.Values)
                {
                    if (r.Name == null || !s.NameOk(r.Name, r.Dir)) continue;
                    if (Add(res, s, v, r.Name, r.Parent, r.Dir) && res.Count >= max) return res;
                }
            }
        }
        return res;
    }

    static bool Add(List<FileHit> res, FindSpec s, Vol v, string name, long parent, bool dir)
    {
        string path = PathOf(v, name, parent);
        if (!s.PathOk(path)) return false;
        FileHit h = new FileHit();
        h.Name = name; h.Path = path; h.IsDir = dir;
        if (s.NeedStat)
        {
            try
            {
                if (dir) { h.Modified = Directory.GetLastWriteTime(path); h.Size = 0; }
                else { FileInfo fi = new FileInfo(path); h.Size = fi.Length; h.Modified = fi.LastWriteTime; }
            }
            catch { return false; }
            if (!s.StatOk(h.Size, h.Modified)) return false;
        }
        res.Add(h);
        return true;
    }
}

// ---------- пошук: вибір рушія та виконання у фоні ----------
static class FileFind
{
    public const int EngineAuto = 0, EngineMft = 1, EngineWin = 2, EngineScan = 3;

    static int gen;
    public static volatile string LastStatus = "";
    static volatile bool winSearchBroken;

    public static bool IsWindows
    {
        get
        {
            PlatformID p = Environment.OSVersion.Platform;
            return p == PlatformID.Win32NT || p == PlatformID.Win32Windows;
        }
    }

    public static int NextGen() { return Interlocked.Increment(ref gen); }

    // Готує рушій заздалегідь (щоб індекс будувався, поки користувач друкує)
    public static void Prepare(int engine)
    {
        if (!IsWindows) return;
        if (engine == EngineAuto || engine == EngineMft) MftIndex.Begin();
    }

    public static bool Indexing { get { return MftIndex.Building; } }
    public static bool IndexReady { get { return MftIndex.Ready; } }
    public static int IndexCount { get { return MftIndex.Count; } }

    public static string EngineStatus(int engine)
    {
        if (MftIndex.Building) return L.T("Індексую диски (NTFS)…");
        if (MftIndex.Ready) return L.T("Індекс NTFS: ") + MftIndex.Count.ToString("N0", CultureInfo.CurrentCulture) + L.T(" об'єктів");
        if (!IsWindows) return L.T("Перегляд дисків");
        if (engine == EngineMft && MftIndex.Failed) return L.T("Індекс NTFS: потрібен адміністратор");
        if (engine == EngineScan) return L.T("Перегляд дисків");
        if (winSearchBroken) return L.T("Перегляд дисків (індекс Windows недоступний)");
        return L.T("Пошук Windows (системний індекс)");
    }

    // Запускає пошук у фоні. report викликається з робочого потоку.
    public static void Begin(string query, int group, int engine, int max, int myGen,
                             Action<List<FileHit>, string, int> report)
    {
        FindSpec s = FindSpec.Parse(query, group);
        if (s.Empty)
        {
            report(new List<FileHit>(), "", myGen);
            return;
        }
        // індекс звільнили через бездіяльність - збудувати знову (поки будується, шукає Windows)
        if (IsWindows && (engine == EngineAuto || engine == EngineMft)) MftIndex.Begin();
        Util.Bg(delegate
        {
            List<FileHit> res = null;
            string note = "";
            try
            {
                bool win = IsWindows;
                bool useMft = win && (engine == EngineAuto || engine == EngineMft) && MftIndex.Ready;
                if (useMft)
                {
                    res = MftIndex.Query(s, max);
                    note = L.T("Індекс NTFS");
                }
                else if (!win || engine == EngineScan || winSearchBroken)
                {
                    res = Walk(s, max, myGen);
                    note = L.T("Перегляд дисків");
                }
                else if (engine == EngineAuto || engine == EngineWin)
                {
                    res = WinSearch(s, query, max);
                    if (res == null)
                    {
                        winSearchBroken = true;
                        res = Walk(s, max, myGen);
                        note = L.T("Перегляд дисків");
                    }
                    else note = L.T("Пошук Windows");
                }
                else
                {
                    res = Walk(s, max, myGen);
                    note = L.T("Перегляд дисків");
                }
            }
            catch { }
            if (res == null) res = new List<FileHit>();
            Sort(res, s);
            LastStatus = note;
            try { report(res, note, myGen); } catch { }
        });
    }

    static void Sort(List<FileHit> res, FindSpec s)
    {
        string first = s.Words.Count > 0 ? s.Words[0] : "";
        res.Sort(delegate (FileHit a, FileHit b)
        {
            int ra = Rank(a, first), rb = Rank(b, first);
            if (ra != rb) return ra - rb;
            if (a.Name.Length != b.Name.Length) return a.Name.Length - b.Name.Length;
            return string.Compare(a.Path, b.Path, StringComparison.CurrentCultureIgnoreCase);
        });
    }

    static int Rank(FileHit h, string q)
    {
        if (q.Length == 0) return h.IsDir ? 0 : 1;
        bool starts = h.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase);
        bool exact = string.Equals(Path.GetFileNameWithoutExtension(h.Name), q, StringComparison.OrdinalIgnoreCase);
        if (exact) return 0;
        if (starts) return 1;
        return 2;
    }

    // ---------- індекс пошуку Windows ----------
    static List<FileHit> WinSearch(FindSpec s, string raw, int max)
    {
        List<string> likes = new List<string>();
        foreach (string w in s.Words) likes.Add(Esc(w));
        foreach (Regex r in s.Wild) { }
        if (likes.Count == 0 && s.Exts == null) return new List<FileHit>();

        StringBuilder sb = new StringBuilder();
        sb.Append("SELECT TOP ").Append(max)
          .Append(" System.ItemNameDisplay, System.ItemPathDisplay, System.ItemType, System.Size, System.DateModified")
          .Append(" FROM SystemIndex WHERE ");
        bool first = true;
        foreach (string l in likes)
        {
            if (!first) sb.Append(" AND ");
            sb.Append("System.ItemNameDisplay LIKE '%").Append(l).Append("%'");
            first = false;
        }
        if (first)   // лише фільтр за розширенням
        {
            sb.Append("System.ItemNameDisplay IS NOT NULL");
        }
        if (s.Exts != null && s.Exts.Length > 0)
        {
            sb.Append(" AND (");
            for (int i = 0; i < s.Exts.Length; i++)
            {
                if (i > 0) sb.Append(" OR ");
                sb.Append("System.FileExtension = '.").Append(Esc(s.Exts[i])).Append("'");
            }
            sb.Append(")");
        }
        foreach (string p in s.PathWords)
            sb.Append(" AND System.ItemPathDisplay LIKE '%").Append(Esc(p)).Append("%'");

        List<FileHit> res = new List<FileHit>();
        OleDbConnection cn = null;
        OleDbCommand cmd = null;
        OleDbDataReader rd = null;
        try
        {
            cn = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties=\"Application=Windows\"");
            cn.Open();
            cmd = new OleDbCommand(sb.ToString(), cn);
            cmd.CommandTimeout = 10;
            rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                string name = rd.IsDBNull(0) ? "" : Convert.ToString(rd.GetValue(0));
                string path = rd.IsDBNull(1) ? "" : Convert.ToString(rd.GetValue(1));
                string type = rd.IsDBNull(2) ? "" : Convert.ToString(rd.GetValue(2));
                if (name.Length == 0 || path.Length == 0) continue;
                FileHit h = new FileHit();
                h.Name = name;
                h.Path = path;
                h.IsDir = string.Equals(type, "Directory", StringComparison.OrdinalIgnoreCase);
                try { h.Size = rd.IsDBNull(3) ? -1 : Convert.ToInt64(rd.GetValue(3)); } catch { }
                try { h.Modified = rd.IsDBNull(4) ? DateTime.MinValue : Convert.ToDateTime(rd.GetValue(4)); } catch { }
                if (!s.NameOk(h.Name, h.IsDir)) continue;
                if (!s.PathOk(h.Path)) continue;
                if (s.NeedStat && !s.StatOk(h.Size, h.Modified)) continue;
                res.Add(h);
            }
        }
        catch { res = null; }
        finally
        {
            // Dispose цих об'єктів може кидати виняток; фіналізатор тоді вбиває процес
            Kill(rd); Kill(cmd); Kill(cn);
        }
        return res;
    }

    static void Kill(IDisposable c)
    {
        if (c == null) return;
        try { GC.SuppressFinalize(c); } catch { }
        try { c.Dispose(); } catch { }
    }

    static string Esc(string v)
    {
        return (v ?? "").Replace("'", "''").Replace("%", "").Replace("[", "").Replace("]", "").Replace("_", "");
    }

    // ---------- запасний варіант: обхід дисків із обмеженням часу ----------
    static List<FileHit> Walk(FindSpec s, int max, int myGen)
    {
        List<FileHit> res = new List<FileHit>();
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(2500);
        Queue<string> q = new Queue<string>();
        try { q.Enqueue(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)); } catch { }
        try
        {
            foreach (DriveInfo d in DriveInfo.GetDrives())
                if (d.DriveType == DriveType.Fixed && d.IsReady) q.Enqueue(d.RootDirectory.FullName);
        }
        catch { }

        while (q.Count > 0)
        {
            if (res.Count >= max || DateTime.UtcNow > deadline || myGen != gen) break;
            string dir = q.Dequeue();
            string[] subs = null, files = null;
            try { subs = Directory.GetDirectories(dir); } catch { }
            try { if (s.Kind != 2) files = Directory.GetFiles(dir); } catch { }
            if (subs != null)
            {
                foreach (string sd in subs)
                {
                    string nm = Path.GetFileName(sd);
                    if (nm.Length == 0) continue;
                    try
                    {
                        FileAttributes fa = File.GetAttributes(sd);
                        if ((fa & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }
                    q.Enqueue(sd);
                    if (s.NameOk(nm, true) && s.PathOk(sd))
                    {
                        FileHit h = new FileHit();
                        h.Name = nm; h.Path = sd; h.IsDir = true;
                        if (!s.NeedStat) res.Add(h);
                        else
                        {
                            try { h.Modified = Directory.GetLastWriteTime(sd); h.Size = 0; } catch { continue; }
                            if (s.StatOk(h.Size, h.Modified)) res.Add(h);
                        }
                        if (res.Count >= max) break;
                    }
                }
            }
            if (files != null)
            {
                foreach (string ff in files)
                {
                    string nm = Path.GetFileName(ff);
                    if (!s.NameOk(nm, false) || !s.PathOk(ff)) continue;
                    FileHit h = new FileHit();
                    h.Name = nm; h.Path = ff; h.IsDir = false;
                    if (s.NeedStat)
                    {
                        try { FileInfo fi = new FileInfo(ff); h.Size = fi.Length; h.Modified = fi.LastWriteTime; }
                        catch { continue; }
                        if (!s.StatOk(h.Size, h.Modified)) continue;
                    }
                    res.Add(h);
                    if (res.Count >= max) break;
                }
            }
        }
        return res;
    }

    public static string SizeText(long bytes)
    {
        if (bytes < 0) return "";
        if (bytes < 1024) return bytes + L.T(" Б");
        double k = bytes / 1024.0;
        if (k < 1024) return k.ToString("0.#", CultureInfo.CurrentCulture) + L.T(" КБ");
        double m = k / 1024.0;
        if (m < 1024) return m.ToString("0.#", CultureInfo.CurrentCulture) + L.T(" МБ");
        return (m / 1024.0).ToString("0.##", CultureInfo.CurrentCulture) + L.T(" ГБ");
    }
}
