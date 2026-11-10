using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// ======================================================================
//  Відновлення файлів. Лише читання тому (нічого на ньому не змінюється); результат пишеться
//  на ІНШИЙ диск. Методи:
//   * швидкий - записи файлової системи про видалені файли: NTFS (записи $MFT без позначки
//     «використовується»), FAT16/FAT32 (записи каталогів 0xE5), exFAT (записи без біта «в ужитку»);
//     шанс оцінюється за картою зайнятих кластерів ($Bitmap / FAT / бітмапа exFAT);
//   * глибокий - пошук за вмістом по всьому тому: сигнатури файлів (фото, відео, документи,
//     архіви, аудіо) і загублені записи $MFT (після форматування NTFS - з іменами й шляхами).
// ======================================================================
interface IBlockDev : IDisposable
{
    long Length { get; }
    int Sector { get; }
    int Read(long off, byte[] buf, int idx, int len);      // скільки прочитано (0 - кінець або помилка)
}

sealed class FileDev : IBlockDev
{
    readonly FileStream fs;
    public FileDev(string path) { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
    public long Length { get { return fs.Length; } }
    public int Sector { get { return 512; } }
    public int Read(long off, byte[] buf, int idx, int len)
    {
        if (off < 0 || off >= fs.Length) return 0;
        fs.Position = off;
        int got = 0;
        while (got < len) { int n = fs.Read(buf, idx + got, len - got); if (n <= 0) break; got += n; }
        return got;
    }
    public void Dispose() { fs.Dispose(); }
}

// том Windows (\\.\X:) - читання лише кратними сектору шматками, тому через вирівняний кеш
sealed class VolumeDev : IBlockDev
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle h, byte[] buf, int n, out int done, IntPtr ovl);

    readonly SafeFileHandle h;
    readonly long len;
    readonly int sector = 512;
    const int Chunk = 1 << 20;
    readonly byte[] cache = new byte[Chunk];
    long cacheAt = -1;
    int cacheLen;

    public VolumeDev(string letter) : this("\\\\.\\" + letter.TrimEnd('\\', ':') + ":", true) { }

    // фізичний диск цілком (\\.\PhysicalDriveN)
    public VolumeDev(int physicalNo) : this("\\\\.\\PhysicalDrive" + physicalNo, true) { }

    VolumeDev(string path, bool full)
    {
        h = Io.Open(path, Io.GENERIC_READ);
        if (h == null) throw new IOException(L.T("Немає доступу до тому (потрібні права адміністратора)."));
        byte[] li = Io.Ioctl(h, 0x7405C, null, 8);                         // IOCTL_DISK_GET_LENGTH_INFO
        len = li != null && li.Length >= 8 ? BitConverter.ToInt64(li, 0) : 0;
        byte[] geo = Io.Ioctl(h, 0x700A0, null, 64);                       // IOCTL_DISK_GET_DRIVE_GEOMETRY_EX
        if (geo != null && geo.Length >= 24) { int s = BitConverter.ToInt32(geo, 20); if (s >= 512 && s <= 65536) sector = s; }
        if (len <= 0) { h.Dispose(); throw new IOException(L.T("Не вдалося визначити розмір.")); }
    }

    public long Length { get { return len; } }
    public int Sector { get { return sector; } }

    bool Fill(long at)
    {
        long a = at / Chunk * Chunk;
        if (a == cacheAt) return cacheLen > 0;
        cacheAt = a; cacheLen = 0;
        long np;
        if (!Win.SetFilePointerEx(h, a, out np, 0)) return false;
        int want = (int)Math.Min(Chunk, len - a), done;
        want = (want + sector - 1) / sector * sector;
        if (ReadFile(h, cache, want, out done, IntPtr.Zero)) { cacheLen = done; return done > 0; }
        // погані сектори: по сектору, нечитані - нулями
        Array.Clear(cache, 0, Chunk);
        for (int o = 0; o < want; o += sector)
        {
            if (!Win.SetFilePointerEx(h, a + o, out np, 0)) break;
            byte[] one = new byte[sector];
            if (ReadFile(h, one, sector, out done, IntPtr.Zero) && done > 0) Buffer.BlockCopy(one, 0, cache, o, done);
        }
        cacheLen = want;
        return true;
    }

    public int Read(long off, byte[] buf, int idx, int n)
    {
        int got = 0;
        while (got < n && off + got < len)
        {
            long at = off + got;
            if (!Fill(at)) break;
            int inChunk = (int)(at - cacheAt);
            int take = Math.Min(n - got, cacheLen - inChunk);
            if (take <= 0) break;
            Buffer.BlockCopy(cache, inChunk, buf, idx + got, take);
            got += take;
        }
        return got;
    }

    public void Dispose() { h.Dispose(); }
}

// ділянка іншого пристрою (розділ диска): зсув і довжина; батьківський пристрій закриває його власник
sealed class SubDev : IBlockDev
{
    readonly IBlockDev d;
    readonly long off, len;
    public SubDev(IBlockDev dev, long offset, long length) { d = dev; off = offset; len = length; }
    public long Length { get { return len; } }
    public int Sector { get { return d.Sector; } }
    public int Read(long o, byte[] buf, int idx, int n)
    {
        if (o < 0 || o >= len) return 0;
        return d.Read(off + o, buf, idx, (int)Math.Min(n, len - o));
    }
    public void Dispose() { }
}

sealed class Part
{
    public long Start, Len;
    public string Fs = "", Name = "";
}

// розділи диска: MBR (з розширеними) і GPT; лише читання таблиць
static class PartTable
{
    static uint U32(byte[] b, int o) { return BitConverter.ToUInt32(b, o); }
    static ulong U64(byte[] b, int o) { return BitConverter.ToUInt64(b, o); }

    static byte[] At(IBlockDev d, long off, int n)
    {
        byte[] b = new byte[n];
        int got = d.Read(off, b, 0, n);
        if (got < n) Array.Clear(b, Math.Max(0, got), n - Math.Max(0, got));
        return b;
    }

    public static List<Part> Find(IBlockDev d)
    {
        List<Part> l = new List<Part>();
        try
        {
            int ss = Math.Max(512, d.Sector);
            byte[] mbr = At(d, 0, 512);
            if (mbr[510] != 0x55 || mbr[511] != 0xAA) return l;
            bool gpt = false;
            for (int i = 0; i < 4; i++) if (mbr[446 + i * 16 + 4] == 0xEE) gpt = true;
            if (gpt) Gpt(d, ss, l);
            else Mbr(d, ss, mbr, l);
            for (int i = l.Count - 1; i >= 0; i--)
                if (l[i].Len < ss * 64L || l[i].Start < 0 || l[i].Start + l[i].Len > d.Length + ss) l.RemoveAt(i);
            foreach (Part p in l) p.Fs = Detect(d, p.Start);
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return l;
    }

    static string Detect(IBlockDev d, long start)
    {
        byte[] b = At(d, start, 512);
        string oem = Encoding.ASCII.GetString(b, 3, 8);
        if (oem == "NTFS    ") return "NTFS";
        if (oem == "EXFAT   ") return "exFAT";
        string f32 = Encoding.ASCII.GetString(b, 82, 5), f16 = Encoding.ASCII.GetString(b, 54, 5);
        if (f32 == "FAT32") return "FAT32";
        if (f16 == "FAT16" || f16 == "FAT12") return f16;
        return "";
    }

    static void Mbr(IBlockDev d, int ss, byte[] mbr, List<Part> l)
    {
        long ext = -1;
        for (int i = 0; i < 4; i++)
        {
            int o = 446 + i * 16;
            byte t = mbr[o + 4];
            long start = U32(mbr, o + 8) * (long)ss, len = U32(mbr, o + 12) * (long)ss;
            if (t == 0 || len == 0) continue;
            if (t == 0x05 || t == 0x0F || t == 0x85) { if (ext < 0) ext = start; continue; }
            Part p = new Part(); p.Start = start; p.Len = len; l.Add(p);
        }
        // розширений розділ: ланцюжок EBR
        long ebr = ext;
        for (int n = 0; n < 64 && ebr >= 0; n++)
        {
            byte[] b = At(d, ebr, 512);
            if (b[510] != 0x55 || b[511] != 0xAA) break;
            long start = ebr + U32(b, 446 + 8) * (long)ss, len = U32(b, 446 + 12) * (long)ss;
            if (b[446 + 4] != 0 && len > 0) { Part p = new Part(); p.Start = start; p.Len = len; l.Add(p); }
            long next = U32(b, 462 + 8) * (long)ss;
            if (b[462 + 4] == 0 || next == 0) break;
            ebr = ext + next;
        }
    }

    static void Gpt(IBlockDev d, int ss, List<Part> l)
    {
        byte[] h = At(d, ss, 512);
        if (Encoding.ASCII.GetString(h, 0, 8) != "EFI PART") return;
        long lba = (long)U64(h, 72);
        uint cnt = U32(h, 80), esz = U32(h, 84);
        if (cnt == 0 || cnt > 256 || esz < 128 || esz > 1024) return;
        byte[] e = At(d, lba * ss, (int)(cnt * esz));
        for (int i = 0; i < cnt; i++)
        {
            int o = (int)(i * esz);
            bool used = false;
            for (int k = 0; k < 16; k++) if (e[o + k] != 0) used = true;
            if (!used) continue;
            long first = (long)U64(e, o + 32), last = (long)U64(e, o + 40);
            if (last < first) continue;
            Part p = new Part(); p.Start = first * ss; p.Len = (last - first + 1) * ss;
            p.Name = Encoding.Unicode.GetString(e, o + 56, 72).TrimEnd('\0');
            l.Add(p);
        }
    }
}

sealed class Found
{
    public string Name = "", Dir = "", Ext = "", Method = "";
    public long ParentRec = -1;            // загублений запис NTFS: номер батьківської папки (шлях - після пошуку)
    public long Size;
    public DateTime When = DateTime.MinValue;
    public int Chance = 2;                 // 2 - добрий, 1 - частково перезаписаний / не перевірено, 0 - перезаписаний
    public List<long[]> Runs = new List<long[]>();     // {зсув у томі (байти) або -1 для «дірки», довжина}
    public byte[] Resident;
    public bool Sel;
    public string Group { get { return RecoverScan.GroupOf(Ext); } }
}

sealed class RecoverScan
{
    readonly IBlockDev dev;
    public readonly List<Found> Items = new List<Found>();
    public volatile bool Stop;
    public string Stage = "", Fs = "", Error = "";
    public double Progress;                 // 0..1
    public bool CarveOnly;                  // увесь диск: лише пошук за сигнатурами (зсуви записів NTFS тут відносні до розділу, а не диска)
    public long ClusterSize = 4096;
    readonly object gate = new object();

    public RecoverScan(IBlockDev d) { dev = d; }

    void Add(Found f) { lock (gate) Items.Add(f); }
    public List<Found> Snapshot() { lock (gate) return new List<Found>(Items); }
    public int Count { get { lock (gate) return Items.Count; } }

    static ushort U16(byte[] b, int o) { return BitConverter.ToUInt16(b, o); }
    static uint U32(byte[] b, int o) { return BitConverter.ToUInt32(b, o); }
    static long I64(byte[] b, int o) { return BitConverter.ToInt64(b, o); }
    static uint BE32(byte[] b, int o) { return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]); }

    byte[] ReadAt(long off, int n)
    {
        byte[] b = new byte[n];
        int got = dev.Read(off, b, 0, n);
        if (got < n) Array.Clear(b, Math.Max(0, got), n - Math.Max(0, got));
        return b;
    }

    public static string GroupOf(string ext)
    {
        switch (ext)
        {
            case "jpg": case "png": case "gif": case "bmp": case "heic": case "webp": case "tif": case "cr2": case "nef": case "arw": case "dng": return "photo";
            case "mp4": case "mov": case "avi": case "mkv": case "3gp": case "wmv": case "m4v": return "video";
            case "pdf": case "docx": case "xlsx": case "pptx": case "doc": case "xls": case "ppt": case "odt": case "ods": case "odp": case "txt": case "rtf": case "epub": return "doc";
            case "zip": case "7z": case "rar": case "gz": case "jar": case "apk": return "arch";
            case "mp3": case "wav": case "flac": case "m4a": case "ogg": return "audio";
        }
        return "other";
    }

    static string ExtOf(string name)
    {
        int k = name.LastIndexOf('.');
        return k > 0 && k < name.Length - 1 ? name.Substring(k + 1).ToLowerInvariant() : "";
    }

    // ================================================================= швидкий пошук
    public void Quick()
    {
        try
        {
            byte[] bs = ReadAt(0, 512);
            string oem = Encoding.ASCII.GetString(bs, 3, 8);
            if (oem == "NTFS    ") { Fs = "NTFS"; Ntfs(bs); }
            else if (oem == "EXFAT   ") { Fs = "exFAT"; ExFat(bs); }
            else if (Encoding.ASCII.GetString(bs, 82, 5) == "FAT32" || Encoding.ASCII.GetString(bs, 54, 5) == "FAT16" || Encoding.ASCII.GetString(bs, 54, 5) == "FAT12") { Fs = "FAT"; Fat(bs); }
            else Error = L.T("Файлова система не розпізнана (том відформатовано чи пошкоджено) - скористайтеся глибоким пошуком.");
        }
        catch (Exception ex) { Error = ex.Message; }
        Progress = 1;
    }

    // ----------------------------------------------------------------- NTFS
    long ntfsCluster, mftRecSize;
    List<long[]> mftRuns = new List<long[]>();      // {зсув, довжина} у байтах
    byte[] ntfsBitmap;

    bool Fixup(byte[] r, int o, int size)
    {
        if (r[o] != 'F' || r[o + 1] != 'I' || r[o + 2] != 'L' || r[o + 3] != 'E') return false;
        int usa = U16(r, o + 4), cnt = U16(r, o + 6);
        if (usa < 40 || cnt < 2 || usa + cnt * 2 > size || (cnt - 1) * 512 > size) return false;
        ushort usn = U16(r, o + usa);
        for (int i = 1; i < cnt; i++)
        {
            int end = o + i * 512 - 2;
            if (U16(r, end) != usn) return false;
            r[end] = r[o + usa + i * 2]; r[end + 1] = r[o + usa + i * 2 + 1];
        }
        return true;
    }

    static List<long[]> Runs(byte[] r, int p, int end, long cluster)
    {
        List<long[]> l = new List<long[]>();
        long lcn = 0;
        while (p < end && r[p] != 0)
        {
            int hdr = r[p], ln = hdr & 15, on = hdr >> 4;
            p++;
            if (ln == 0 || ln > 8 || on > 8 || p + ln + on > end) break;
            long count = 0;
            for (int i = 0; i < ln; i++) count |= (long)r[p + i] << (8 * i);
            p += ln;
            if (on == 0) { l.Add(new long[] { -1, count * cluster }); continue; }       // розріджена ділянка
            long delta = 0;
            for (int i = 0; i < on; i++) delta |= (long)r[p + i] << (8 * i);
            if ((r[p + on - 1] & 0x80) != 0) delta |= -1L << (8 * on);
            p += on;
            lcn += delta;
            if (lcn < 0) break;
            l.Add(new long[] { lcn * cluster, count * cluster });
        }
        return l;
    }

    sealed class MftRec
    {
        public bool InUse, IsDir, Ok;
        public string Name = "";
        public long Parent = -1, Size;
        public DateTime When = DateTime.MinValue;
        public List<long[]> Runs;
        public byte[] Resident;
        public bool Unsupported;
    }

    MftRec ParseRec(byte[] r, int o, int size, long cluster)
    {
        MftRec m = new MftRec();
        int flags = U16(r, o + 22);
        m.InUse = (flags & 1) != 0; m.IsDir = (flags & 2) != 0;
        if (I64(r, o + 32) != 0) return m;          // запис-продовження іншого запису
        int a = o + U16(r, o + 20), end = o + Math.Min(size, (int)U32(r, o + 24) > 0 ? (int)U32(r, o + 24) : size);
        int bestNs = -1;
        while (a + 16 <= end)
        {
            uint type = U32(r, a);
            if (type == 0xFFFFFFFF) break;
            int len = (int)U32(r, a + 4);
            if (len < 16 || a + len > end) break;
            bool nonRes = r[a + 8] != 0;
            int nameLen = r[a + 9];
            if (type == 0x10 && !nonRes)
            {
                int c = a + U16(r, a + 20);
                if (c + 16 <= a + len) try { m.When = DateTime.FromFileTimeUtc(I64(r, c + 8)).ToLocalTime(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            }
            else if (type == 0x30 && !nonRes)
            {
                int c = a + U16(r, a + 20);
                if (c + 66 <= a + len)
                {
                    int nl = r[c + 64], ns = r[c + 65];
                    int rank = ns == 2 ? 0 : (ns == 0 ? 1 : 2);        // DOS-ім'я (8.3) - найгірше
                    if (rank > bestNs && c + 66 + nl * 2 <= a + len)
                    {
                        bestNs = rank;
                        m.Name = Encoding.Unicode.GetString(r, c + 66, nl * 2);
                        m.Parent = I64(r, c) & 0xFFFFFFFFFFFFL;
                        if (m.Size == 0) m.Size = I64(r, c + 48);
                    }
                }
            }
            else if (type == 0x80 && nameLen == 0)
            {
                if (!nonRes)
                {
                    int cl = (int)U32(r, a + 16), c = a + U16(r, a + 20);
                    if (c + cl <= a + len) { m.Resident = new byte[cl]; Buffer.BlockCopy(r, c, m.Resident, 0, cl); m.Size = cl; }
                }
                else if (I64(r, a + 16) == 0)        // перший шматок атрибута (VCN 0)
                {
                    int flagsA = U16(r, a + 12);
                    if ((flagsA & 0x0001) != 0 || (flagsA & 0x4000) != 0) m.Unsupported = true;     // стиснений / зашифрований
                    m.Runs = Runs(r, a + U16(r, a + 32), a + len, cluster);
                    m.Size = I64(r, a + 48);
                }
            }
            a += len;
        }
        m.Ok = m.Name.Length > 0;
        return m;
    }

    bool NtfsInit(byte[] bs)
    {
        int bps = U16(bs, 11);
        int spcRaw = bs[13];
        long spc = spcRaw > 0x80 ? 1L << (256 - spcRaw) : spcRaw;
        ntfsCluster = bps * spc;
        ClusterSize = ntfsCluster;
        sbyte cpr = (sbyte)bs[64];
        mftRecSize = cpr < 0 ? 1L << -cpr : cpr * ntfsCluster;
        if (ntfsCluster <= 0 || mftRecSize < 512 || mftRecSize > 65536) { Error = L.T("Пошкоджений завантажувальний сектор NTFS."); return false; }
        long mftOff = I64(bs, 48) * ntfsCluster;
        int rs = (int)mftRecSize;
        byte[] r0 = ReadAt(mftOff, rs);
        if (!Fixup(r0, 0, rs)) { Error = L.T("Не вдалося прочитати $MFT."); return false; }
        MftRec m0 = ParseRec(r0, 0, rs, ntfsCluster);
        mftRuns = m0.Runs ?? new List<long[]> { new long[] { mftOff, rs * 16L } };
        return true;
    }

    void Ntfs(byte[] bs)
    {
        if (!NtfsInit(bs)) return;
        int rs = (int)mftRecSize;
        long total = 0;
        foreach (long[] run in mftRuns) total += run[1];
        long recCount = total / rs;
        // карта зайнятих кластерів - для оцінки шансу
        try
        {
            byte[] r6 = ReadAt(OffsetOfRecord(6), rs);
            if (Fixup(r6, 0, rs))
            {
                MftRec b6 = ParseRec(r6, 0, rs, ntfsCluster);
                if (b6.Runs != null && b6.Size > 0 && b6.Size < (512L << 20)) ntfsBitmap = ReadRuns(b6.Runs, b6.Size);
                else if (b6.Resident != null) ntfsBitmap = b6.Resident;
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }

        // перший прохід: імена й батьки всіх записів (для шляхів), видалені - в окремий список
        Dictionary<long, long> parent = new Dictionary<long, long>();
        Dictionary<long, string> names = new Dictionary<long, string>();
        List<KeyValuePair<long, MftRec>> deleted = new List<KeyValuePair<long, MftRec>>();
        const int Batch = 256;
        byte[] buf = new byte[rs * Batch];
        long rec = 0;
        foreach (long[] run in mftRuns)
        {
            if (run[0] < 0) { rec += run[1] / rs; continue; }
            long inRun = run[1] / rs;
            for (long k = 0; k < inRun && !Stop; k += Batch)
            {
                int n = (int)Math.Min(Batch, inRun - k);
                int got = dev.Read(run[0] + k * rs, buf, 0, n * rs);
                for (int i = 0; i < n; i++, rec++)
                {
                    if ((i + 1) * rs > got) continue;
                    int o = i * rs;
                    if (!Fixup(buf, o, rs)) continue;
                    MftRec m = ParseRec(buf, o, rs, ntfsCluster);
                    if (!m.Ok) continue;
                    if (m.IsDir) { names[rec] = m.Name; parent[rec] = m.Parent; }      // для шляхів потрібні лише папки
                    else if (!m.InUse && rec >= 24) deleted.Add(new KeyValuePair<long, MftRec>(rec, m));
                }
                Progress = recCount > 0 ? (double)rec / recCount : 0;
                Stage = L.T("Читання $MFT: ") + rec.ToString("N0") + L.T(" з ") + recCount.ToString("N0") + L.T(" записів");
            }
            if (Stop) break;
        }
        foreach (KeyValuePair<long, MftRec> kv in deleted)
        {
            MftRec m = kv.Value;
            Found f = new Found();
            f.Name = m.Name; f.Ext = ExtOf(m.Name); f.Size = m.Size; f.When = m.When; f.Method = "NTFS";
            f.Dir = PathOf(m.Parent, parent, names);
            if (m.Resident != null) { f.Resident = m.Resident; f.Chance = 2; }
            else if (m.Runs != null)
            {
                f.Runs = m.Runs;
                f.Chance = m.Unsupported ? 0 : ChanceNtfs(m.Runs, m.Size);
            }
            else if (m.Size == 0) f.Chance = 2;
            else continue;           // даних не знайдено
            Add(f);
        }
    }

    long OffsetOfRecord(long n)
    {
        long off = n * mftRecSize;
        foreach (long[] run in mftRuns)
        {
            if (off < run[1]) return run[0] < 0 ? -1 : run[0] + off;
            off -= run[1];
        }
        return -1;
    }

    static string PathOf(long p, Dictionary<long, long> parent, Dictionary<long, string> names)
    {
        List<string> parts = new List<string>();
        for (int depth = 0; depth < 64 && p != 5 && p >= 0; depth++)      // 5 - корінь тому
        {
            string n;
            if (!names.TryGetValue(p, out n)) { parts.Add("?"); break; }
            parts.Add(n);
            long q;
            if (!parent.TryGetValue(p, out q) || q == p) break;
            p = q;
        }
        parts.Reverse();
        return string.Join("\\", parts.ToArray());
    }

    int ChanceNtfs(List<long[]> runs, long size)
    {
        if (ntfsBitmap == null) return 1;
        long used = 0, all = 0, left = size;
        foreach (long[] run in runs)
        {
            if (left <= 0) break;
            long n = Math.Min(run[1], left);
            left -= n;
            if (run[0] < 0) continue;
            long c0 = run[0] / ntfsCluster, c1 = (run[0] + n + ntfsCluster - 1) / ntfsCluster;
            for (long c = c0; c < c1; c++)
            {
                all++;
                long byteI = c >> 3;
                if (byteI < ntfsBitmap.Length && (ntfsBitmap[byteI] & (1 << (int)(c & 7))) != 0) used++;
                if (all > 4096 && used == 0) return 2;          // досить вибірки
            }
        }
        if (all == 0) return 2;
        return used == 0 ? 2 : (used < all ? 1 : 0);
    }

    byte[] ReadRuns(List<long[]> runs, long size)
    {
        byte[] d = new byte[size];
        long pos = 0;
        foreach (long[] run in runs)
        {
            if (pos >= size) break;
            int n = (int)Math.Min(run[1], size - pos);
            if (run[0] >= 0) dev.Read(run[0], d, (int)pos, n);
            pos += n;
        }
        return d;
    }

    // ----------------------------------------------------------------- FAT16 / FAT32
    long fatDataStart, fatRootStart, fatCluster;
    int fatBits, fatRootEntries;
    long fatClusters, fatOffset;
    byte[] fat;
    readonly HashSet<long> seenDirs = new HashSet<long>();

    uint FatNext(long c)
    {
        if (fat == null) return 0x0FFFFFFF;
        if (fatBits == 32) { long o = c * 4; return o + 4 <= fat.Length ? U32(fat, (int)o) & 0x0FFFFFFF : 0x0FFFFFFF; }
        if (fatBits == 16) { long o = c * 2; return o + 2 <= fat.Length ? (uint)U16(fat, (int)o) : 0xFFFFu; }
        long o12 = c * 3 / 2;
        if (o12 + 2 > fat.Length) return 0xFFF;
        int v = U16(fat, (int)o12);
        return (uint)((c & 1) != 0 ? v >> 4 : v & 0xFFF);
    }

    bool FatEnd(uint v) { return fatBits == 32 ? v >= 0x0FFFFFF8 : (fatBits == 16 ? v >= 0xFFF8 : v >= 0xFF8); }
    long ClusterOff(long c) { return fatDataStart + (c - 2) * fatCluster; }

    void Fat(byte[] bs)
    {
        int bps = U16(bs, 11), spc = bs[13], reserved = U16(bs, 14), nfats = bs[16];
        fatRootEntries = U16(bs, 17);
        long total = U16(bs, 19) != 0 ? U16(bs, 19) : U32(bs, 32);
        long fatSize = U16(bs, 22) != 0 ? U16(bs, 22) : U32(bs, 36);
        if (bps < 512 || spc == 0 || nfats == 0 || fatSize == 0) { Error = L.T("Пошкоджений завантажувальний сектор FAT."); return; }
        fatCluster = (long)bps * spc;
        ClusterSize = fatCluster;
        long rootSectors = (fatRootEntries * 32 + bps - 1) / bps;
        fatOffset = (long)reserved * bps;
        fatRootStart = fatOffset + nfats * fatSize * bps;
        fatDataStart = fatRootStart + rootSectors * bps;
        fatClusters = (total * bps - fatDataStart) / fatCluster;
        fatBits = fatClusters < 4085 ? 12 : (fatClusters < 65525 ? 16 : 32);
        if (fatSize * bps < (256L << 20)) fat = ReadAt(fatOffset, (int)(fatSize * bps));
        Stage = L.T("Читання каталогів FAT");
        if (fatBits == 32) FatDir(ClusterChain(U32(bs, 44), true), "", 0);
        else
        {
            List<long[]> root = new List<long[]> { new long[] { fatRootStart, rootSectors * bps } };
            FatDir(root, "", 0);
        }
    }

    List<long[]> ClusterChain(long first, bool live)
    {
        List<long[]> l = new List<long[]>();
        long c = first;
        for (int i = 0; i < 65536 && c >= 2 && c < fatClusters + 2; i++)
        {
            l.Add(new long[] { ClusterOff(c), fatCluster });
            if (!live) break;
            uint nx = FatNext(c);
            if (FatEnd(nx) || nx < 2) break;
            c = nx;
        }
        return l;
    }

    static DateTime DosTime(int date, int time)
    {
        try { return new DateTime(1980 + (date >> 9), Math.Max(1, (date >> 5) & 15), Math.Max(1, date & 31), (time >> 11) & 31, (time >> 5) & 63, Math.Min(58, (time & 31) * 2)); }
        catch { return DateTime.MinValue; }
    }

    void FatDir(List<long[]> runs, string path, int depth)
    {
        if (depth > 48 || Stop) return;
        long key = runs.Count > 0 ? runs[0][0] : -1;
        if (key < 0 || !seenDirs.Add(key)) return;
        long totalLen = 0;
        foreach (long[] r in runs) totalLen += r[1];
        if (totalLen > (64L << 20)) return;
        byte[] d = ReadRuns(runs, totalLen);
        List<string> lfn = new List<string>();
        for (int o = 0; o + 32 <= d.Length; o += 32)
        {
            byte b0 = d[o];
            if (b0 == 0) break;
            int attr = d[o + 11];
            if (attr == 0x0F)
            {
                StringBuilder sb = new StringBuilder();
                foreach (int[] seg in new[] { new[] { 1, 10 }, new[] { 14, 12 }, new[] { 28, 4 } })
                    for (int k = 0; k < seg[1]; k += 2)
                    {
                        char ch = (char)U16(d, o + seg[0] + k);
                        if (ch == 0 || ch == 0xFFFF) { k = 99; break; }
                        sb.Append(ch);
                    }
                lfn.Insert(0, sb.ToString());
                continue;
            }
            bool del = b0 == 0xE5;
            string shortName = Encoding.ASCII.GetString(d, o + (del ? 1 : 0), del ? 7 : 8).TrimEnd() ;
            string extS = Encoding.ASCII.GetString(d, o + 8, 3).TrimEnd();
            int nt = d[o + 12];                        // Windows: ім'я / розширення 8.3 малими літерами
            if ((nt & 0x08) != 0) shortName = shortName.ToLowerInvariant();
            if ((nt & 0x10) != 0) extS = extS.ToLowerInvariant();
            string name = lfn.Count > 0 ? string.Join("", lfn.ToArray()) : (del ? "_" : "") + shortName + (extS.Length > 0 ? "." + extS : "");
            lfn.Clear();
            if ((attr & 0x08) != 0) continue;         // мітка тому
            if (!del && (shortName == "." || shortName == ".." || b0 == '.')) continue;
            if (b0 == '.') continue;
            long first = (long)U16(d, o + 20) << 16 | U16(d, o + 26);
            if (fatBits != 32) first &= 0xFFFF;
            long size = U32(d, o + 28);
            DateTime when = DosTime(U16(d, o + 24), U16(d, o + 22));
            bool isDir = (attr & 0x10) != 0;
            if (isDir)
            {
                if (first >= 2) FatDir(ClusterChain(first, !del), path.Length > 0 ? path + "\\" + name : name, depth + 1);
                continue;
            }
            if (!del) continue;
            Found f = new Found();
            f.Name = name; f.Ext = ExtOf(name); f.Size = size; f.When = when; f.Dir = path; f.Method = "FAT";
            if (size > 0 && first >= 2)
            {
                f.Runs.Add(new long[] { ClusterOff(first), size });      // ланцюжок стерто - вважаємо, що файл лежав суцільно
                long nc = (size + fatCluster - 1) / fatCluster, busy = 0;
                for (long c = first; c < first + nc && c < first + 4096; c++) if (FatNext(c) != 0) busy++;
                f.Chance = busy == 0 ? (nc > 1 ? 1 : 2) : 0;
                if (f.Chance == 1 && nc <= 1) f.Chance = 2;
            }
            Add(f);
        }
    }

    // ----------------------------------------------------------------- exFAT
    long exHeap, exCluster, exCount, exFatOff;
    byte[] exBitmap;

    long ExOff(long c) { return exHeap + (c - 2) * exCluster; }

    bool ExBusy(long c)
    {
        if (exBitmap == null) return false;
        long i = c - 2;
        return i >= 0 && (i >> 3) < exBitmap.Length && (exBitmap[i >> 3] & (1 << (int)(i & 7))) != 0;
    }

    List<long[]> ExChain(long first, long len, bool noChain)
    {
        List<long[]> l = new List<long[]>();
        if (first < 2) return l;
        if (noChain || len > 0 && exFatOff == 0) { l.Add(new long[] { ExOff(first), len }); return l; }
        long c = first, left = len > 0 ? len : long.MaxValue;
        for (int i = 0; i < 1 << 20 && c >= 2 && c < exCount + 2 && left > 0; i++)
        {
            long n = Math.Min(left, exCluster);
            l.Add(new long[] { ExOff(c), n });
            left -= n;
            byte[] e = ReadAt(exFatOff + c * 4, 4);
            uint nx = U32(e, 0);
            if (nx >= 0xFFFFFFF7 || nx < 2) break;
            c = nx;
        }
        return l;
    }

    static DateTime ExTime(uint v)
    {
        try { return new DateTime(1980 + (int)(v >> 25), Math.Max(1, (int)((v >> 21) & 15)), Math.Max(1, (int)((v >> 16) & 31)), (int)((v >> 11) & 31), (int)((v >> 5) & 63), (int)Math.Min(58, (v & 31) * 2)); }
        catch { return DateTime.MinValue; }
    }

    void ExFat(byte[] bs)
    {
        int bpsShift = bs[108], spcShift = bs[109];
        if (bpsShift < 9 || bpsShift > 12 || spcShift > 25) { Error = L.T("Пошкоджений завантажувальний сектор exFAT."); return; }
        long bps = 1L << bpsShift;
        exCluster = bps << spcShift;
        ClusterSize = exCluster;
        exFatOff = U32(bs, 80) * bps;
        exHeap = U32(bs, 88) * bps;
        exCount = U32(bs, 92);
        long root = U32(bs, 96);
        Stage = L.T("Читання каталогів exFAT");
        ExDir(ExChain(root, 0, false), "", 0, true);
    }

    void ExDir(List<long[]> runs, string path, int depth, bool isRoot)
    {
        if (depth > 48 || Stop || runs.Count == 0) return;
        if (!seenDirs.Add(runs[0][0])) return;
        long totalLen = 0;
        foreach (long[] r in runs) totalLen += r[1];
        if (totalLen > (64L << 20)) return;
        byte[] d = ReadRuns(runs, totalLen);
        for (int o = 0; o + 32 <= d.Length; o += 32)
        {
            int t = d[o];
            if (t == 0) break;
            if (isRoot && t == 0x81 && exBitmap == null)
            {
                long bc = U32(d, o + 20), bl = I64(d, o + 24);
                if (bl > 0 && bl < (256L << 20)) exBitmap = ReadRuns(ExChain(bc, bl, false), bl);
                continue;
            }
            if ((t & 0x7F) != 0x05) continue;            // запис файлу (0x85 - в ужитку, 0x05 - видалений)
            bool del = (t & 0x80) == 0;
            int sec = d[o + 1];
            if (sec < 2 || o + (sec + 1) * 32 > d.Length) continue;
            int attr = U16(d, o + 4);
            DateTime when = ExTime(U32(d, o + 12));
            int so = o + 32;
            if ((d[so] & 0x7F) != 0x40) continue;        // розширення потоку
            bool noChain = (d[so + 1] & 2) != 0;
            int nameLen = d[so + 3];
            long first = U32(d, so + 20), len = I64(d, so + 24);
            StringBuilder nm = new StringBuilder();
            for (int k = 2; k <= sec && nm.Length < nameLen; k++)
            {
                int no = o + k * 32;
                if ((d[no] & 0x7F) != 0x41) break;
                for (int c = 0; c < 15 && nm.Length < nameLen; c++) nm.Append((char)U16(d, no + 2 + c * 2));
            }
            string name = nm.ToString();
            o += sec * 32;
            if (name.Length == 0) continue;
            if ((attr & 0x10) != 0)
            {
                if (first >= 2 && len > 0) ExDir(ExChain(first, len, noChain || del), path.Length > 0 ? path + "\\" + name : name, depth + 1, false);
                continue;
            }
            if (!del) continue;
            Found f = new Found();
            f.Name = name; f.Ext = ExtOf(name); f.Size = len; f.When = when; f.Dir = path; f.Method = "exFAT";
            if (len > 0 && first >= 2)
            {
                f.Runs.Add(new long[] { ExOff(first), len });
                long nc = (len + exCluster - 1) / exCluster, busy = 0;
                for (long c = first; c < first + nc && c < first + 4096; c++) if (ExBusy(c)) busy++;
                f.Chance = busy == 0 ? (noChain ? 2 : 1) : 0;
            }
            Add(f);
        }
    }

    // ================================================================= глибокий пошук
    // step - крок перевірки (сектор); файли зазвичай починаються на межі кластера
    public void Deep()
    {
        try
        {
            // спершу - швидкий пошук (записи файлової системи) і карта зайнятих кластерів;
            // далі за вмістом перевіряється лише вільне місце (зайняте - це наявні файли)
            if (Fs.Length == 0 && Items.Count == 0) { Quick(); Error = ""; Progress = 0; }
            if (ntfsCluster <= 0) { ntfsCluster = ClusterSize > 0 ? ClusterSize : 4096; }
            if (mftRecSize <= 0) mftRecSize = 1024;
            long len = dev.Length;
            int step = Math.Max(512, dev.Sector);
            const int Block = 4 << 20;
            byte[] buf = new byte[Block + 64];
            long skipTo = 0;
            long lastUi = 0;
            for (long pos = 0; pos < len && !Stop; pos += Block)
            {
                int got = dev.Read(pos, buf, 0, (int)Math.Min(Block + 64, len - pos));
                if (got <= 0) continue;
                int lim = Math.Min(Block, got);
                for (int o = 0; o < lim; o += step)
                {
                    long at = pos + o;
                    if (at < skipTo) continue;
                    if (Allocated(at) && !(buf[o] == 'F' && buf[o + 1] == 'I' && buf[o + 2] == 'L' && buf[o + 3] == 'E')) continue;
                    long end = Probe(buf, o, got, at);
                    if (end > at) skipTo = (end + step - 1) / step * step;
                }
                Progress = (double)(pos + lim) / len;
                if (pos - lastUi >= (64L << 20) || pos + lim >= len)
                {
                    lastUi = pos;
                    Stage = L.T("Пошук за вмістом: ") + Vols.Size(pos + lim) + L.T(" з ") + Vols.Size(len);
                }
            }
        }
        catch (Exception ex) { Error = ex.Message; }
        // шляхи загублених записів NTFS - за знайденими загубленими папками
        foreach (Found f in Snapshot())
            if (f.ParentRec >= 0)
            {
                string pth = PathOf(f.ParentRec, orphanParent, orphanNames);
                f.Dir = L.T("(після форматування)") + (pth.Length > 0 && !pth.StartsWith("?") ? "\\" + pth : "");
            }
        Progress = 1;
    }

    // кластер зайнятий наявним файлом (за картою поточної файлової системи)
    bool Allocated(long at)
    {
        if (Fs == "NTFS" && ntfsBitmap != null && ntfsCluster > 0)
        {
            long c = at / ntfsCluster, bi = c >> 3;
            return bi < ntfsBitmap.Length && (ntfsBitmap[bi] & (1 << (int)(c & 7))) != 0;
        }
        if (Fs == "FAT" && fat != null && at >= fatDataStart)
        {
            long c = (at - fatDataStart) / fatCluster + 2;
            return c < fatClusters + 2 && FatNext(c) != 0;
        }
        if (Fs == "exFAT" && exBitmap != null && at >= exHeap) return ExBusy((at - exHeap) / exCluster + 2);
        return false;
    }

    readonly Dictionary<long, long> orphanParent = new Dictionary<long, long>();
    readonly Dictionary<long, string> orphanNames = new Dictionary<long, string>();

    readonly Dictionary<string, int> carveNo = new Dictionary<string, int>();

    void Carved(string ext, long at, long size, string name)
    {
        Found f = new Found();
        int n;
        carveNo.TryGetValue(ext, out n);
        carveNo[ext] = ++n;
        f.Ext = ext; f.Size = size; f.Method = L.T("вміст"); f.Chance = 1;
        f.Name = name ?? (ext + "_" + n.ToString("00000") + "." + ext);
        f.Dir = "";
        f.Runs.Add(new long[] { at, size });
        Add(f);
    }

    bool InMft(long at)
    {
        foreach (long[] r in mftRuns) if (r[0] >= 0 && at >= r[0] && at < r[0] + r[1]) return true;
        return false;
    }

    // перевірка сигнатури на позиції; повертає кінець знайденого файлу (або -1)
    long Probe(byte[] b, int o, int got, long at)
    {
        if (o + 16 > got) return -1;
        byte c = b[o];
        try
        {
            switch (c)
            {
                case 0xFF:
                    if (b[o + 1] == 0xD8 && b[o + 2] == 0xFF) { long n = JpegLen(at); if (n > 0) { Carved("jpg", at, n, null); return at + n; } }
                    break;
                case 0x89:
                    if (b[o + 1] == 'P' && b[o + 2] == 'N' && b[o + 3] == 'G' && b[o + 4] == 0x0D && b[o + 5] == 0x0A) { long n = PngLen(at); if (n > 0) { Carved("png", at, n, null); return at + n; } }
                    break;
                case (byte)'G':
                    if (b[o + 1] == 'I' && b[o + 2] == 'F' && b[o + 3] == '8' && (b[o + 4] == '7' || b[o + 4] == '9') && b[o + 5] == 'a') { long n = GifLen(at); if (n > 0) { Carved("gif", at, n, null); return at + n; } }
                    break;
                case (byte)'B':
                    if (b[o + 1] == 'M')
                    {
                        long sz = U32(b, o + 2), dataOff = U32(b, o + 10), dib = U32(b, o + 14);
                        if (U32(b, o + 6) == 0 && (dib == 12 || dib == 40 || dib == 52 || dib == 56 || dib == 108 || dib == 124) && sz > 54 && sz < (256L << 20) && dataOff < sz && dataOff >= 26)
                        { Carved("bmp", at, sz, null); return at + sz; }
                    }
                    break;
                case (byte)'%':
                    if (b[o + 1] == 'P' && b[o + 2] == 'D' && b[o + 3] == 'F' && b[o + 4] == '-') { long n = PdfLen(at); if (n > 0) { Carved("pdf", at, n, null); return at + n; } }
                    break;
                case (byte)'P':
                    if (b[o + 1] == 'K' && b[o + 2] == 3 && b[o + 3] == 4) { string ext; long n = ZipLen(at, out ext); if (n > 0) { Carved(ext, at, n, null); return at + n; } }
                    break;
                case (byte)'7':
                    if (b[o + 1] == 'z' && b[o + 2] == 0xBC && b[o + 3] == 0xAF && b[o + 4] == 0x27 && b[o + 5] == 0x1C)
                    {
                        long n = 32 + I64(b, o + 12) + I64(b, o + 20);
                        if (n > 32 && n < (64L << 30)) { Carved("7z", at, n, null); return at + n; }
                    }
                    break;
                case (byte)'R':
                    if (b[o + 1] == 'I' && b[o + 2] == 'F' && b[o + 3] == 'F')
                    {
                        long n = (long)U32(b, o + 4) + 8;
                        string form = Encoding.ASCII.GetString(b, o + 8, 4);
                        string ext = form == "AVI " ? "avi" : form == "WAVE" ? "wav" : form == "WEBP" ? "webp" : null;
                        if (ext != null && n > 12) { Carved(ext, at, n, null); return at + n; }
                    }
                    break;
                case 0xD0:
                    if (b[o + 1] == 0xCF && b[o + 2] == 0x11 && b[o + 3] == 0xE0 && b[o + 4] == 0xA1 && b[o + 5] == 0xB1 && b[o + 6] == 0x1A && b[o + 7] == 0xE1)
                    { string ext; long n = OleLen(at, out ext); if (n > 0) { Carved(ext, at, n, null); return at + n; } }
                    break;
                case (byte)'I':
                    if (b[o + 1] == 'D' && b[o + 2] == '3' && b[o + 3] < 5) { long n = Mp3Len(at); if (n > 0) { Carved("mp3", at, n, null); return at + n; } }
                    break;
                case (byte)'F':
                    if (b[o + 1] == 'I' && b[o + 2] == 'L' && b[o + 3] == 'E' && ntfsCluster > 0 && !CarveOnly && !InMft(at)) { MftOrphan(at); return -1; }
                    break;
                case (byte)'f':
                    if (b[o + 1] == 'L' && b[o + 2] == 'a' && b[o + 3] == 'C') { }
                    break;
            }
            // MP4 / MOV / HEIC: «ftyp» на зсуві 4
            if (b[o + 4] == 'f' && b[o + 5] == 't' && b[o + 6] == 'y' && b[o + 7] == 'p')
            {
                string ext; long n = IsoLen(at, out ext);
                if (n > 0) { Carved(ext, at, n, null); return at + n; }
            }
            // Matroska / WebM
            if (c == 0x1A && b[o + 1] == 0x45 && b[o + 2] == 0xDF && b[o + 3] == 0xA3) { long n = MkvLen(at); if (n > 0) { Carved("mkv", at, n, null); return at + n; } }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return -1;
    }

    // загублений запис $MFT (поза поточною $MFT): ім'я і ділянки даних
    readonly HashSet<string> orphanSeen = new HashSet<string>();
    void MftOrphan(long at)
    {
        int rs = mftRecSize > 0 ? (int)mftRecSize : 1024;
        byte[] r = ReadAt(at, rs);
        if (!Fixup(r, 0, rs)) return;
        MftRec m = ParseRec(r, 0, rs, ntfsCluster);
        if (!m.Ok) return;
        long recNo = U32(r, 44);
        if (m.IsDir) { if (recNo > 0 && !orphanNames.ContainsKey(recNo)) { orphanNames[recNo] = m.Name; orphanParent[recNo] = m.Parent; } return; }
        if (m.Runs == null && m.Resident == null) return;
        if (m.Name.StartsWith("$", StringComparison.Ordinal)) return;
        string key = m.Name + "|" + m.Size + "|" + (m.Runs != null && m.Runs.Count > 0 ? m.Runs[0][0] : 0);
        if (!orphanSeen.Add(key)) return;
        Found f = new Found();
        f.Name = m.Name; f.Ext = ExtOf(m.Name); f.Size = m.Size; f.When = m.When; f.Method = L.T("запис NTFS"); f.Dir = L.T("(після форматування)"); f.ParentRec = m.Parent;
        if (m.Resident != null) f.Resident = m.Resident; else f.Runs = m.Runs;
        f.Chance = m.Unsupported ? 0 : 1;
        Add(f);
    }

    // ---------- довжини файлів за їхньою структурою ----------
    long JpegLen(long at)
    {
        const long Max = 64L << 20;
        long p = at + 2;
        byte[] h = new byte[4];
        // сегменти до початку зображення (SOS)
        for (int i = 0; i < 256; i++)
        {
            if (dev.Read(p, h, 0, 4) < 4 || h[0] != 0xFF) return -1;
            int m = h[1];
            if (m == 0xD9) return p + 2 - at;
            if (m >= 0xD0 && m <= 0xD7 || m == 0x01) { p += 2; continue; }
            int sl = h[2] << 8 | h[3];
            if (sl < 2) return -1;
            p += 2 + sl;
            if (m == 0xDA) break;
            if (p - at > Max) return -1;
        }
        // дані зображення: до FFD9 (FF00 і рестарти FFD0-D7 - частина даних); інші маркери (прогресивний JPEG) - пропускаємо
        byte[] buf = new byte[65536];
        while (p - at < Max)
        {
            int got = dev.Read(p, buf, 0, buf.Length);
            if (got < 2) return -1;
            for (int i = 0; i < got - 1; i++)
            {
                if (buf[i] != 0xFF) continue;
                int m = buf[i + 1];
                if (m == 0x00 || m == 0xFF || (m >= 0xD0 && m <= 0xD7)) continue;
                if (m == 0xD9) return p + i + 2 - at;
                if (i + 3 < got)
                {
                    int sl = buf[i + 2] << 8 | buf[i + 3];
                    if (sl < 2) return -1;
                    p += i + 2 + sl;
                    goto next;
                }
                p += i; goto next;
            }
            p += got - 1;
        next: ;
        }
        return -1;
    }

    long PngLen(long at)
    {
        long p = at + 8;
        byte[] h = new byte[8];
        for (int i = 0; i < 100000; i++)
        {
            if (dev.Read(p, h, 0, 8) < 8) return -1;
            long ln = BE32(h, 0);
            string type = Encoding.ASCII.GetString(h, 4, 4);
            for (int k = 4; k < 8; k++) if (!(h[k] >= 'A' && h[k] <= 'Z' || h[k] >= 'a' && h[k] <= 'z')) return -1;
            p += 12 + ln;
            if (type == "IEND") return p - at;
            if (p - at > (256L << 20)) return -1;
        }
        return -1;
    }

    long GifLen(long at)
    {
        byte[] h = ReadAt(at, 13);
        long p = at + 13;
        if ((h[10] & 0x80) != 0) p += 3L << ((h[10] & 7) + 1);
        byte[] one = new byte[1];
        Func<long, long> skipSub = delegate (long q)
        {
            for (int i = 0; i < 1 << 20; i++)
            {
                if (dev.Read(q, one, 0, 1) < 1) return -1;
                if (one[0] == 0) return q + 1;
                q += 1 + one[0];
            }
            return -1;
        };
        for (int i = 0; i < 100000; i++)
        {
            if (dev.Read(p, one, 0, 1) < 1) return -1;
            if (one[0] == 0x3B) return p + 1 - at;
            if (one[0] == 0x21) { p = skipSub(p + 2); if (p < 0) return -1; }
            else if (one[0] == 0x2C)
            {
                byte[] d = ReadAt(p, 10);
                p += 10;
                if ((d[9] & 0x80) != 0) p += 3L << ((d[9] & 7) + 1);
                p = skipSub(p + 1);
                if (p < 0) return -1;
            }
            else return -1;
            if (p - at > (128L << 20)) return -1;
        }
        return -1;
    }

    long PdfLen(long at)
    {
        const long Max = 256L << 20;
        byte[] buf = new byte[1 << 20];
        long p = at, lastEof = -1;
        while (p - at < Max)
        {
            int got = dev.Read(p, buf, 0, buf.Length);
            if (got < 6) break;
            for (int i = 0; i + 5 <= got; i++)
                if (buf[i] == '%' && buf[i + 1] == '%' && buf[i + 2] == 'E' && buf[i + 3] == 'O' && buf[i + 4] == 'F')
                {
                    long e = p + i + 5;
                    if (i + 5 < got && buf[i + 5] == '\r') e++;
                    if (i + 6 < got && buf[i + 6] == '\n' || i + 5 < got && buf[i + 5] == '\n') e++;
                    lastEof = e;
                }
            // після останнього %%EOF уже є дані іншого файлу або порожнеча - кінець
            if (lastEof > 0 && lastEof < p + got - 1024)
            {
                int li = (int)(lastEof - p);
                bool more = false;
                for (int i = li; i + 9 <= got && i < li + 4096; i++)
                    if (buf[i] == 's' && Encoding.ASCII.GetString(buf, i, 9) == "startxref") { more = true; break; }
                    else if (buf[i] == 'x' && i + 4 <= got && Encoding.ASCII.GetString(buf, i, 4) == "xref") { more = true; break; }
                if (!more) return lastEof - at;
            }
            p += got - 8;
        }
        return lastEof > 0 ? lastEof - at : -1;
    }

    long ZipLen(long at, out string ext)
    {
        ext = "zip";
        // перший запис: тип за іменами всередині (docx, xlsx, pptx, odt, epub, apk, jar)
        byte[] h = ReadAt(at, 30 + 256);
        int nl = U16(h, 26);
        string first = nl > 0 && nl <= 256 ? Encoding.ASCII.GetString(h, 30, Math.Min(nl, 256)) : "";
        if (first == "mimetype")
        {
            int el = U16(h, 28);
            string mt = Encoding.ASCII.GetString(ReadAt(at + 30 + nl + el, 64)).TrimEnd('\0');
            if (mt.StartsWith("application/epub")) ext = "epub";
            else if (mt.Contains("opendocument.text")) ext = "odt";
            else if (mt.Contains("opendocument.spreadsheet")) ext = "ods";
            else if (mt.Contains("opendocument.presentation")) ext = "odp";
        }
        const long Max = 2L << 30;
        byte[] buf = new byte[1 << 20];
        long p = at;
        bool word = false, xl = false, ppt = false, apk = false, jar = false;
        while (p - at < Max)
        {
            int got = dev.Read(p, buf, 0, buf.Length);
            if (got < 22) return -1;
            for (int i = 0; i + 22 <= got; i++)
            {
                if (buf[i] != 'P' || buf[i + 1] != 'K') continue;
                if (buf[i + 2] == 1 && buf[i + 3] == 2 && i + 46 <= got)
                {
                    int cn = U16(buf, i + 28);
                    if (i + 46 + cn <= got)
                    {
                        string n = Encoding.ASCII.GetString(buf, i + 46, cn);
                        if (n.StartsWith("word/")) word = true; else if (n.StartsWith("xl/")) xl = true; else if (n.StartsWith("ppt/")) ppt = true;
                        else if (n == "AndroidManifest.xml" || n == "classes.dex") apk = true; else if (n == "META-INF/MANIFEST.MF") jar = true;
                    }
                }
                else if (buf[i + 2] == 5 && buf[i + 3] == 6)
                {
                    int cl = U16(buf, i + 20);
                    if (ext == "zip") ext = word ? "docx" : xl ? "xlsx" : ppt ? "pptx" : apk ? "apk" : jar ? "jar" : "zip";
                    return p + i + 22 + cl - at;
                }
            }
            p += got - 21;
        }
        return -1;
    }

    long OleLen(long at, out string ext)
    {
        ext = "doc";
        byte[] h = ReadAt(at, 512);
        int shift = U16(h, 30);
        if (shift != 9 && shift != 12) return -1;
        long ss = 1L << shift;
        int nFat = (int)U32(h, 44);
        if (nFat <= 0 || nFat > 109) nFat = Math.Min(Math.Max(nFat, 0), 109);
        long maxSec = 0;
        for (int i = 0; i < nFat; i++)
        {
            long fs = U32(h, 76 + i * 4);
            if (fs >= 0xFFFFFFFA) continue;
            byte[] f = ReadAt(at + (fs + 1) * ss, (int)ss);
            for (int k = 0; k + 4 <= f.Length; k += 4)
            {
                uint v = U32(f, k);
                if (v != 0xFFFFFFFF) maxSec = Math.Max(maxSec, i * (ss / 4) + k / 4);
            }
            maxSec = Math.Max(maxSec, fs);
        }
        // тип за коренем каталогу: імена потоків
        long dirSec = U32(h, 48);
        byte[] dir = ReadAt(at + (dirSec + 1) * ss, (int)ss);
        string names = Encoding.Unicode.GetString(dir);
        if (names.Contains("Workbook") || names.Contains("Book")) ext = "xls";
        else if (names.Contains("PowerPoint Document")) ext = "ppt";
        else if (names.Contains("WordDocument")) ext = "doc";
        else ext = "ole";
        long n = (maxSec + 2) * ss;
        return n > 512 && n < (1L << 31) ? n : -1;
    }

    long Mp3Len(long at)
    {
        byte[] h = ReadAt(at, 10);
        long tag = (h[6] & 0x7F) << 21 | (h[7] & 0x7F) << 14 | (h[8] & 0x7F) << 7 | (h[9] & 0x7F);
        long p = at + 10 + tag + ((h[5] & 0x10) != 0 ? 10 : 0);
        int[] br1 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
        int[] br2 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };
        int[] sr = { 44100, 48000, 32000, 0 };
        byte[] f = new byte[4];
        int frames = 0;
        for (int i = 0; i < 2000000; i++)
        {
            if (dev.Read(p, f, 0, 4) < 4) break;
            if (f[0] != 0xFF || (f[1] & 0xE0) != 0xE0) break;
            int ver = (f[1] >> 3) & 3, layer = (f[1] >> 1) & 3, bri = f[2] >> 4, sri = (f[2] >> 2) & 3, pad = (f[2] >> 1) & 1;
            if (layer != 1 || bri == 0 || bri == 15 || sri == 3 || ver == 1) break;          // лише Layer III
            int rate = sr[sri] >> (ver == 3 ? 0 : (ver == 2 ? 1 : 2));
            int bitrate = (ver == 3 ? br1[bri] : br2[bri]) * 1000;
            int len = (ver == 3 ? 144 : 72) * bitrate / rate + pad;
            if (len < 24) break;
            p += len;
            frames++;
        }
        if (frames < 10) return -1;
        byte[] t = ReadAt(p, 3);
        if (t[0] == 'T' && t[1] == 'A' && t[2] == 'G') p += 128;
        return p - at;
    }

    long IsoLen(long at, out string ext)
    {
        ext = "mp4";
        byte[] h = ReadAt(at, 16);
        string brand = Encoding.ASCII.GetString(h, 8, 4);
        if (brand == "qt  ") ext = "mov";
        else if (brand == "heic" || brand == "heix" || brand == "mif1" || brand == "msf1") ext = "heic";
        else if (brand.StartsWith("3g")) ext = "3gp";
        else if (brand == "M4A " || brand == "M4B ") ext = "m4a";
        else if (brand == "M4V ") ext = "m4v";
        string[] known = { "ftyp", "moov", "mdat", "free", "skip", "wide", "uuid", "meta", "pdin", "moof", "mfra", "sidx", "styp", "prft", "emsg", "pnot", "PICT" };
        long p = at;
        bool mdat = false, moov = false;
        byte[] b = new byte[16];
        for (int i = 0; i < 10000; i++)
        {
            if (dev.Read(p, b, 0, 16) < 8) break;
            long sz = BE32(b, 0);
            string type = Encoding.ASCII.GetString(b, 4, 4);
            if (Array.IndexOf(known, type) < 0) break;
            if (sz == 1) sz = (long)((ulong)BE32(b, 8) << 32 | BE32(b, 12));
            else if (sz == 0) return -1;
            if (sz < 8) break;
            if (type == "mdat") mdat = true;
            if (type == "moov" || type == "meta") moov = true;
            p += sz;
            if (p - at > (64L << 30)) return -1;
        }
        if (!moov && !mdat || p - at <= 16) return -1;
        return p - at;
    }

    long MkvLen(long at)
    {
        // EBML-заголовок, потім Segment (0x18538067) із розміром
        byte[] b = ReadAt(at, 64);
        int p = 4;
        int l1; long hsz = Vint(b, p, out l1);
        if (hsz < 0) return -1;
        p += l1;
        long seg = 4 + l1 + hsz;
        byte[] s = ReadAt(at + seg, 16);
        if (!(s[0] == 0x18 && s[1] == 0x53 && s[2] == 0x80 && s[3] == 0x67)) return -1;
        int l2; long ssz = Vint(s, 4, out l2);
        if (ssz <= 0 || ssz > (64L << 30)) return -1;
        return seg + 4 + l2 + ssz;
    }

    static long Vint(byte[] b, int p, out int len)
    {
        len = 0;
        int f = b[p];
        for (int i = 0; i < 8; i++) if ((f & (0x80 >> i)) != 0) { len = i + 1; break; }
        if (len == 0) return -1;
        long v = f & (0xFF >> len);
        for (int i = 1; i < len; i++) v = v << 8 | b[p + i];
        return v;
    }

    // ================================================================= запис знайденого
    static string Clean(string s)
    {
        StringBuilder b = new StringBuilder();
        foreach (char c in s) b.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        string r = b.ToString().Trim().TrimEnd('.');
        return r.Length == 0 ? "_" : r;
    }

    public void Save(Found f, string outDir, byte[] buf)
    {
        string dir = outDir;
        if (f.Method == L.T("вміст")) dir = Path.Combine(outDir, L.T("Знайдено за вмістом"), f.Ext);
        else if (f.Dir.Length > 0)
            foreach (string part in f.Dir.Split('\\')) if (part.Length > 0) dir = Path.Combine(dir, Clean(part));
        Directory.CreateDirectory(dir);
        string name = Clean(f.Name), path = Path.Combine(dir, name);
        for (int i = 2; File.Exists(path) && i < 10000; i++)
            path = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + " (" + i + ")" + Path.GetExtension(name));
        using (FileStream fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            if (f.Resident != null) { fs.Write(f.Resident, 0, (int)Math.Min(f.Resident.Length, Math.Max(0, f.Size))); }
            else
            {
                long left = f.Size;
                foreach (long[] run in f.Runs)
                {
                    long n = Math.Min(run[1], left);
                    long done = 0;
                    while (done < n && !Stop)
                    {
                        int k = (int)Math.Min(buf.Length, n - done);
                        if (run[0] < 0) Array.Clear(buf, 0, k);
                        else { int got = dev.Read(run[0] + done, buf, 0, k); if (got < k) Array.Clear(buf, Math.Max(0, got), k - Math.Max(0, got)); }
                        fs.Write(buf, 0, k);
                        done += k;
                    }
                    left -= n;
                    if (left <= 0 || Stop) break;
                }
            }
        }
        try { if (f.When != DateTime.MinValue) File.SetLastWriteTime(path, f.When); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }
}
