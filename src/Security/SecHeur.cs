using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

// ======================================================================
//  Евристики безпеки без бази сигнатур: цифровий підпис, будова файла програми (PE), місце запуску,
//  підробка системних назв, небезпечні команди. Кожна ознака має вагу; підсумок - «бал» підозрілості.
//  Це не антивірус: ознаки бувають і в звичайних програм, тому всюди - пояснення «чому», а не вирок.
//  Пам'ять чужих процесів не читається і нічого в них не впроваджується - лише файли на диску й відкриті дані системи.
// ======================================================================
static class Sig
{
    public const int Unknown = -1, None = 0, Ok = 1, Bad = 2, Expired = 3;

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_FILE_INFO { public uint cbStruct; public IntPtr pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_CATALOG_INFO
    {
        public uint cbStruct; public uint dwCatalogVersion; public IntPtr pcwszCatalogFilePath; public IntPtr pcwszMemberTag; public IntPtr pcwszMemberFilePath;
        public IntPtr hMemberFile; public IntPtr pbCalculatedFileHash; public uint cbCalculatedFileHash; public IntPtr pcCatalogContext; public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData; public uint dwUIChoice; public uint fdwRevocationChecks;
        public uint dwUnionChoice; public IntPtr pInfo; public uint dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference;
        public uint dwProvFlags; public uint dwUIContext; public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CATALOG_INFO { public uint cbStruct; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string wszCatalogFile; }

    [DllImport("wintrust.dll")] static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, IntPtr data);
    [DllImport("wintrust.dll", SetLastError = true)] static extern bool CryptCATAdminAcquireContext2(out IntPtr h, IntPtr subsys, [MarshalAs(UnmanagedType.LPWStr)] string alg, IntPtr policy, uint flags);
    [DllImport("wintrust.dll", SetLastError = true)] static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr h, IntPtr file, ref uint size, byte[] hash, uint flags);
    [DllImport("wintrust.dll")] static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr h, byte[] hash, uint size, uint flags, IntPtr prev);
    [DllImport("wintrust.dll")] static extern bool CryptCATCatalogInfoFromContext(IntPtr ctx, ref CATALOG_INFO info, uint flags);
    [DllImport("wintrust.dll")] static extern bool CryptCATAdminReleaseCatalogContext(IntPtr h, IntPtr ctx, uint flags);
    [DllImport("wintrust.dll")] static extern bool CryptCATAdminReleaseContext(IntPtr h, uint flags);

    static Guid Action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");      // WINTRUST_ACTION_GENERIC_VERIFY_V2

    sealed class Res { public int Kind; public string Signer; }
    static readonly Dictionary<string, Res> cache = new Dictionary<string, Res>(StringComparer.OrdinalIgnoreCase);

    // pInfo - WINTRUST_FILE_INFO або WINTRUST_CATALOG_INFO; choice: 1 - файл, 2 - каталог
    static int Verify(IntPtr info, uint choice)
    {
        WINTRUST_DATA d = new WINTRUST_DATA();
        d.cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA));
        d.dwUIChoice = 2;                 // WTD_UI_NONE
        d.fdwRevocationChecks = 0;        // без перевірки відкликання (вона ходить у мережу)
        d.dwUnionChoice = choice;
        d.pInfo = info;
        d.dwStateAction = 0;
        d.dwProvFlags = 0x1000;           // WTD_CACHE_ONLY_URL_RETRIEVAL - жодних звернень у мережу
        IntPtr pd = Marshal.AllocHGlobal((int)d.cbStruct);
        try
        {
            Marshal.StructureToPtr(d, pd, false);
            return WinVerifyTrust(new IntPtr(-1), ref Action, pd);      // INVALID_HANDLE_VALUE - без вікон
        }
        finally { Marshal.FreeHGlobal(pd); }
    }

    static int Embedded(string file)
    {
        WINTRUST_FILE_INFO fi = new WINTRUST_FILE_INFO();
        fi.cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO));
        fi.pcwszFilePath = Marshal.StringToHGlobalUni(file);
        IntPtr p = Marshal.AllocHGlobal((int)fi.cbStruct);
        try
        {
            Marshal.StructureToPtr(fi, p, false);
            return Verify(p, 1);
        }
        finally { Marshal.FreeHGlobal(p); Marshal.FreeHGlobal(fi.pcwszFilePath); }
    }

    // підпис через каталог Windows (так підписана більшість системних файлів); catFile - файл каталогу
    static int Catalog(string file, string alg, out string catFile)
    {
        catFile = "";
        IntPtr adm;
        if (!CryptCATAdminAcquireContext2(out adm, IntPtr.Zero, alg, IntPtr.Zero, 0)) return -1;
        try
        {
            using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                IntPtr fh = fs.SafeFileHandle.DangerousGetHandle();
                uint sz = 0;
                CryptCATAdminCalcHashFromFileHandle2(adm, fh, ref sz, null, 0);
                if (sz == 0 || sz > 256) return -1;
                byte[] hash = new byte[sz];
                if (!CryptCATAdminCalcHashFromFileHandle2(adm, fh, ref sz, hash, 0)) return -1;
                IntPtr ci = CryptCATAdminEnumCatalogFromHash(adm, hash, sz, 0, IntPtr.Zero);
                if (ci == IntPtr.Zero) return -1;
                try
                {
                    CATALOG_INFO info = new CATALOG_INFO();
                    info.cbStruct = (uint)Marshal.SizeOf(typeof(CATALOG_INFO));
                    if (!CryptCATCatalogInfoFromContext(ci, ref info, 0)) return -1;
                    catFile = info.wszCatalogFile;
                    StringBuilder tag = new StringBuilder();
                    foreach (byte b in hash) tag.Append(b.ToString("X2"));
                    WINTRUST_CATALOG_INFO wc = new WINTRUST_CATALOG_INFO();
                    wc.cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_CATALOG_INFO));
                    wc.pcwszCatalogFilePath = Marshal.StringToHGlobalUni(info.wszCatalogFile);
                    wc.pcwszMemberTag = Marshal.StringToHGlobalUni(tag.ToString());
                    wc.pcwszMemberFilePath = Marshal.StringToHGlobalUni(file);
                    wc.hMemberFile = fh;
                    wc.pbCalculatedFileHash = Marshal.AllocHGlobal(hash.Length);
                    Marshal.Copy(hash, 0, wc.pbCalculatedFileHash, hash.Length);
                    wc.cbCalculatedFileHash = (uint)hash.Length;
                    wc.hCatAdmin = adm;
                    IntPtr p = Marshal.AllocHGlobal((int)wc.cbStruct);
                    try
                    {
                        Marshal.StructureToPtr(wc, p, false);
                        return Verify(p, 2);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(p);
                        Marshal.FreeHGlobal(wc.pcwszCatalogFilePath); Marshal.FreeHGlobal(wc.pcwszMemberTag);
                        Marshal.FreeHGlobal(wc.pcwszMemberFilePath); Marshal.FreeHGlobal(wc.pbCalculatedFileHash);
                    }
                }
                finally { CryptCATAdminReleaseCatalogContext(adm, ci, 0); }
            }
        }
        catch { return -1; }
        finally { CryptCATAdminReleaseContext(adm, 0); }
    }

    static string SubjectName(string subject)
    {
        if (string.IsNullOrEmpty(subject)) return "";
        foreach (string key in new string[] { "O=", "CN=" })
        {
            int i = subject.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) continue;
            i += key.Length;
            string v;
            if (i < subject.Length && subject[i] == '"') { int e = subject.IndexOf('"', i + 1); v = e > i ? subject.Substring(i + 1, e - i - 1) : subject.Substring(i + 1); }
            else { int e = subject.IndexOf(", ", i, StringComparison.Ordinal); v = e > 0 ? subject.Substring(i, e - i) : subject.Substring(i); }
            if (v.Trim().Length > 0) return v.Trim();
        }
        return subject;
    }

    static string SignerOf(string signedFile)
    {
        try
        {
            X509Certificate c = X509Certificate.CreateFromSignedFile(signedFile);
            return SubjectName(c.Subject);
        }
        catch { return ""; }
    }

    // Ok / None (немає підпису) / Bad (підпис є, але недійсний: файл змінено, сертифікат не довірений) / Expired / Unknown
    public static int Check(string file, out string signer)
    {
        signer = "";
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) return Unknown;
        string key;
        try { key = file + "|" + File.GetLastWriteTimeUtc(file).Ticks; } catch { key = file; }
        Res r;
        lock (cache) if (cache.TryGetValue(key, out r)) { signer = r.Signer; return r.Kind; }
        r = new Res(); r.Signer = "";
        try
        {
            int v = Embedded(file);
            if (v == 0) { r.Kind = Ok; r.Signer = SignerOf(file); }
            else if (v == unchecked((int)0x800B0100) || v == unchecked((int)0x800B0003))
            {
                // TRUST_E_NOSIGNATURE / TRUST_E_SUBJECT_FORM_UNKNOWN: вбудованого підпису немає - шукаємо в каталогах Windows
                string cat;
                int c = Catalog(file, "SHA256", out cat);
                if (c != 0) c = Catalog(file, null, out cat);
                if (c == 0) { r.Kind = Ok; r.Signer = SignerOf(cat); if (r.Signer.Length == 0) r.Signer = L.T("Microsoft Windows (каталог)"); }
                else r.Kind = None;
            }
            else if (v == unchecked((int)0x800B0101)) { r.Kind = Expired; r.Signer = SignerOf(file); }        // CERT_E_EXPIRED без позначки часу
            else { r.Kind = Bad; r.Signer = SignerOf(file); }
        }
        catch { r.Kind = Unknown; }
        lock (cache) cache[key] = r;
        signer = r.Signer;
        return r.Kind;
    }

    public static string Text(int kind, string signer)
    {
        switch (kind)
        {
            case Ok: return L.T("Підпис: ") + (signer.Length > 0 ? signer : L.T("дійсний"));
            case None: return L.T("Без цифрового підпису");
            case Bad: return L.T("Підпис НЕДІЙСНИЙ") + (signer.Length > 0 ? " (" + signer + ")" : "");
            case Expired: return L.T("Підпис прострочений") + (signer.Length > 0 ? " (" + signer + ")" : "");
            default: return L.T("Підпис не перевірено");
        }
    }

    public static bool Microsoft(string signer) { return signer.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0; }
}

// будова файла програми Windows (PE): розділи, ентропія, імпортовані функції
sealed class PeInfo
{
    public bool Ok, Is64, Dll, Native, DotNet;
    public string Packer = "";
    public double MaxEntropy, CodeEntropy;
    public int ImportCount;
    public readonly HashSet<string> Imports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Dlls = new List<string>();
    public readonly List<string> Sections = new List<string>();
    public DateTime Stamp;

    static int U16(byte[] b, long o) { return o + 2 <= b.Length ? b[o] | (b[o + 1] << 8) : 0; }
    static uint U32(byte[] b, long o) { return o + 4 <= b.Length ? (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)) : 0; }

    static double Entropy(byte[] b, long off, long len)
    {
        if (len <= 0 || off < 0 || off >= b.Length) return 0;
        len = Math.Min(len, b.Length - off);
        long[] cnt = new long[256];
        for (long i = off; i < off + len; i++) cnt[b[i]]++;
        double e = 0;
        foreach (long c in cnt) if (c > 0) { double p = (double)c / len; e -= p * Math.Log(p, 2); }
        return e;
    }

    static readonly string[][] Packers =
    {
        new[] { "upx", "UPX" }, new[] { ".aspack", "ASPack" }, new[] { ".adata", "ASPack" }, new[] { "mpress", "MPRESS" }, new[] { ".themida", "Themida" },
        new[] { ".winlice", "Themida" }, new[] { ".vmp", "VMProtect" }, new[] { ".enigma", "Enigma" }, new[] { "pec2", "PECompact" }, new[] { "pecompact", "PECompact" },
        new[] { ".petite", "Petite" }, new[] { ".nsp", "NsPack" }, new[] { ".packed", L.T("пакувальник") }, new[] { ".mew", "MEW" }, new[] { ".fsg", "FSG" }
    };

    public static PeInfo Read(string path)
    {
        PeInfo pi = new PeInfo();
        byte[] b;
        try
        {
            using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                int n = (int)Math.Min(f.Length, 48L << 20);
                b = new byte[n];
                int got = 0, k;
                while (got < n && (k = f.Read(b, got, n - got)) > 0) got += k;
            }
        }
        catch { return pi; }
        if (b.Length < 0x40 || b[0] != 'M' || b[1] != 'Z') return pi;
        long pe = U32(b, 0x3C);
        if (pe <= 0 || pe + 24 > b.Length || U32(b, pe) != 0x00004550) return pi;
        int nsec = U16(b, pe + 6);
        uint stamp = U32(b, pe + 8);
        int optSize = U16(b, pe + 20), chars = U16(b, pe + 22);
        long opt = pe + 24;
        int magic = U16(b, opt);
        if (magic != 0x10b && magic != 0x20b) return pi;
        pi.Ok = true;
        pi.Is64 = magic == 0x20b;
        pi.Dll = (chars & 0x2000) != 0;
        pi.Native = U16(b, opt + 68) == 1;
        try { pi.Stamp = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(stamp); } catch { }
        long dd = opt + (pi.Is64 ? 112 : 96);
        uint entry = U32(b, opt + 16);
        uint impRva = U32(b, dd + 8), clrRva = U32(b, dd + 14 * 8);
        pi.DotNet = clrRva != 0;
        long sec = opt + optSize;
        uint[] va = new uint[nsec], vs = new uint[nsec], raw = new uint[nsec], rs = new uint[nsec];
        for (int i = 0; i < nsec && i < 96; i++)
        {
            long s = sec + i * 40;
            if (s + 40 > b.Length) break;
            string name = Encoding.ASCII.GetString(b, (int)s, 8).TrimEnd('\0', ' ');
            vs[i] = U32(b, s + 8); va[i] = U32(b, s + 12); rs[i] = U32(b, s + 16); raw[i] = U32(b, s + 20);
            pi.Sections.Add(name);
            double e = Entropy(b, raw[i], Math.Min(rs[i], 8u << 20));
            if (rs[i] > 1024) pi.MaxEntropy = Math.Max(pi.MaxEntropy, e);
            if (entry >= va[i] && entry < va[i] + Math.Max(vs[i], rs[i])) pi.CodeEntropy = e;
            string ln = name.ToLowerInvariant();
            foreach (string[] pk in Packers) if (pi.Packer.Length == 0 && ln.StartsWith(pk[0])) pi.Packer = pk[1];
        }
        // імпорт: RVA -> зсув у файлі
        Func<uint, long> off = delegate (uint rva)
        {
            for (int i = 0; i < nsec; i++)
                if (rva >= va[i] && rva < va[i] + Math.Max(vs[i], rs[i])) return raw[i] + (rva - va[i]);
            return -1;
        };
        long imp = impRva != 0 ? off(impRva) : -1;
        for (int d = 0; imp >= 0 && d < 512; d++)
        {
            long de = imp + d * 20;
            if (de + 20 > b.Length) break;
            uint oft = U32(b, de), nameRva = U32(b, de + 12), ft = U32(b, de + 16);
            if (oft == 0 && nameRva == 0 && ft == 0) break;
            long no = off(nameRva);
            if (no >= 0 && no < b.Length) pi.Dlls.Add(CStr(b, no).ToLowerInvariant());
            long th = off(oft != 0 ? oft : ft);
            int step = pi.Is64 ? 8 : 4;
            for (int t = 0; th >= 0 && t < 4096; t++)
            {
                long te = th + t * step;
                if (te + step > b.Length) break;
                ulong v = pi.Is64 ? (U32(b, te) | ((ulong)U32(b, te + 4) << 32)) : U32(b, te);
                if (v == 0) break;
                pi.ImportCount++;
                bool ord = pi.Is64 ? (v & 0x8000000000000000UL) != 0 : (v & 0x80000000UL) != 0;
                if (ord) continue;
                long fn = off((uint)(v & 0x7FFFFFFF));
                if (fn >= 0 && fn + 2 < b.Length) pi.Imports.Add(CStr(b, fn + 2));
            }
        }
        return pi;
    }

    static string CStr(byte[] b, long o)
    {
        int e = (int)o;
        while (e < b.Length && b[e] != 0 && e - o < 256) e++;
        return Encoding.ASCII.GetString(b, (int)o, e - (int)o);
    }

    public bool Has(params string[] names)
    {
        foreach (string n in names) if (Imports.Contains(n) || Imports.Contains(n + "A") || Imports.Contains(n + "W")) return true;
        return false;
    }
}

static class SecHeur
{
    // місце файла: 0 - система, 1 - Program Files, 2 - теки користувача (AppData, Temp, Завантаження…), 3 - інше (інші диски)
    public const int PSys = 0, PProg = 1, PUser = 2, POther = 3;

    static string Low(string s) { return (s ?? "").ToLowerInvariant(); }
    static readonly string win = Low(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).TrimEnd('\\');
    static readonly string users = Low(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\')) ?? "c:\\users");
    static readonly string pd = Low(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)).TrimEnd('\\');
    static readonly string pf = Low(Environment.GetEnvironmentVariable("ProgramFiles") ?? "c:\\program files").TrimEnd('\\');
    static readonly string pf86 = Low(Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "c:\\program files (x86)").TrimEnd('\\');

    public static int Place(string path, out string where)
    {
        where = "";
        string p = Low(path);
        if (p.Length == 0) return POther;
        if (p.Contains("\\$recycle.bin\\")) { where = L.T("Кошик"); return PUser; }
        if (p.Contains("\\temp\\") || p.Contains("\\tmp\\")) { where = "Temp"; return PUser; }
        if (p.StartsWith(win + "\\")) return PSys;
        if (p.StartsWith(pf + "\\") || p.StartsWith(pf86 + "\\")) return PProg;
        if (p.StartsWith(users + "\\"))
        {
            if (p.Contains("\\downloads\\") || p.Contains("\\завантаження\\")) where = L.T("Завантаження");
            else if (p.Contains("\\desktop\\") || p.Contains("\\робочий стіл\\")) where = L.T("Робочий стіл");
            else if (p.Contains("\\appdata\\local\\programs\\")) where = "AppData\\Local\\Programs";
            else if (p.Contains("\\appdata\\roaming\\")) where = "AppData\\Roaming";
            else if (p.Contains("\\appdata\\local\\")) where = "AppData\\Local";
            else if (p.Contains("\\users\\public\\")) where = "Users\\Public";
            else where = L.T("тека користувача");
            return PUser;
        }
        if (p.StartsWith(pd + "\\"))
        {
            where = "ProgramData";
            return PUser;
        }
        return POther;
    }

    public static bool InSys32(string path)
    {
        path = path ?? "";
        int cut = path.LastIndexOfAny(new char[] { '\\', '/' });
        string d = cut > 0 ? Low(path.Substring(0, cut)) : "";
        return d == win + "\\system32" || d == win + "\\syswow64";
    }

    // системні процеси й де вони мають бути
    static readonly string[] SysNames =
    {
        "svchost", "lsass", "csrss", "winlogon", "services", "smss", "wininit", "spoolsv", "taskhostw", "dwm", "conhost", "rundll32", "dllhost", "sihost",
        "ctfmon", "runtimebroker", "fontdrvhost", "lsaiso", "wuauclt", "taskmgr", "regsvr32", "cmd", "powershell", "mshta", "wscript", "cscript", "msiexec",
        "searchindexer", "audiodg", "smartscreen", "userinit", "logonui", "lsm", "wmiprvse", "explorer"
    };

    static int Lev(string a, string b)
    {
        int[,] d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                int c = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + c);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }

    // справжні програми з назвами, схожими на системні (Internet Explorer, старий taskhost тощо)
    static readonly string[] NearOk = { "iexplore", "taskhost", "taskhostex", "service", "services", "dllhst3g", "searchui", "conhost64" };

    // повний шлях для записів служб і драйверів: "system32\drivers\x.sys", "\SystemRoot\...", "\??\C:\..."
    static string FullWinPath(string p)
    {
        string l = Low(p);
        if (l.StartsWith("\\??\\")) return p.Substring(4);
        if (l.StartsWith("\\systemroot\\")) return Environment.GetFolderPath(Environment.SpecialFolder.Windows) + p.Substring(11);
        if (l.StartsWith("system32\\") || l.StartsWith("syswow64\\")) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), p);
        return p;
    }

    // файл у теці Windows і підписаний Microsoft (перевірка підпису кешується; викликається рідко - лише для схожих назв)
    static bool MsSignedInWindows(string path)
    {
        try
        {
            string full = FullWinPath(path);
            if (!Low(full).StartsWith(win + "\\") || !File.Exists(full)) return false;
            string signer;
            return Sig.Check(full, out signer) == Sig.Ok && signer.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    // перевірки назви й місця файла програми; повертає бал, причини додає в why
    public static int NameRisk(string path, List<string> why)
    {
        int s = 0;
        path = path ?? "";
        int cut = path.LastIndexOfAny(new char[] { '\\', '/' });
        string file = path.Substring(cut + 1), n = Low(Path.GetFileNameWithoutExtension(file)), ext = Low(Path.GetExtension(file));
        if (n.Length == 0) return 0;
        if (Array.IndexOf(SysNames, n) >= 0)
        {
            string d = cut > 0 ? Low(path.Substring(0, cut)) : "";
            bool okDir = n == "explorer" ? (d == win || d == win + "\\syswow64") :
                         (n == "powershell" ? (d.StartsWith(win + "\\system32\\windowspowershell") || d.StartsWith(win + "\\syswow64\\windowspowershell")) :
                         (n == "wmiprvse" ? (d == win + "\\system32\\wbem" || d == win + "\\syswow64\\wbem") :      // постачальник WMI живе в теці wbem
                         (InSys32(path) || (n == "taskmgr" && d == win + "\\system32"))));
            if (!okDir) { s += 6; why.Add(L.T("Назва системного процесу «") + file + L.T("», але файл не в системній теці Windows - так маскуються шкідливі програми.")); }
        }
        else if (Array.IndexOf(NearOk, n) < 0)
        {
            // схожа на системну: scvhost, svch0st, lsas…
            string norm = n.Replace('0', 'o').Replace('1', 'l').Replace('3', 'e').Replace('5', 's');
            foreach (string sn in SysNames)
            {
                if (sn.Length < 5) continue;
                int dist = Lev(norm, sn);
                if (norm == sn || (dist > 0 && dist <= (sn.Length >= 7 ? 2 : 1)))
                {
                    // у теці Windows багато справжніх компонентів зі схожими назвами (smphost.dll, winnat.sys, moshost.dll…):
                    // якщо файл підписаний Microsoft (вбудовано чи через каталог Windows) - це не маскування
                    if (MsSignedInWindows(path)) break;
                    s += 4; why.Add(L.T("Назва «") + file + L.T("» дуже схожа на системну «") + sn + L.T(".exe» - типовий прийом маскування."));
                    break;
                }
            }
        }
        if (file.IndexOf('\u202E') >= 0) { s += 6; why.Add(L.T("У назві прихований символ зміни напрямку тексту (RLO) - назва показується не такою, як є насправді.")); }
        string[] docs = { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".jpg", ".jpeg", ".png", ".txt", ".mp3", ".mp4", ".zip", ".rar", ".rtf" };
        string inner = Low(Path.GetExtension(Path.GetFileNameWithoutExtension(file)));
        if (Array.IndexOf(docs, inner) >= 0 && IsExecExt(ext)) { s += 5; why.Add(L.T("Подвійне розширення «") + inner + ext + L.T("»: виглядає як документ, а насправді це програма чи скрипт.")); }
        if (file.Contains("      ") && IsExecExt(ext)) { s += 3; why.Add(L.T("Багато пробілів перед розширенням - так ховають «.exe» за межею видимої назви.")); }
        return s;
    }

    public static bool IsExecExt(string ext)
    {
        return Array.IndexOf(new string[] { ".exe", ".scr", ".com", ".pif", ".bat", ".cmd", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".ps1", ".msi", ".cpl", ".lnk", ".dll" }, Low(ext)) >= 0;
    }

    static bool Has(string s, params string[] parts)
    {
        foreach (string p in parts) if (s.Contains(p)) return true;
        return false;
    }

    // небезпечні прийоми в командному рядку чи скрипті
    public static int CmdRisk(string cmd, List<string> why)
    {
        if (string.IsNullOrEmpty(cmd)) return 0;
        string c = Low(cmd);
        int s = 0;
        bool ps = Has(c, "powershell", "pwsh");
        if (ps && System.Text.RegularExpressions.Regex.IsMatch(c, @"\s-(e|ec|enc|enco|encodedcommand)\s+[a-z0-9+/=]{16,}")) { s += 4; why.Add(L.T("PowerShell із закодованою командою (-EncodedCommand) - що саме виконується, не видно.")); }
        if (Has(c, "downloadstring", "downloadfile", "downloaddata", "net.webclient", "invoke-webrequest", "iwr ", "start-bitstransfer", "invoke-restmethod"))
            if (Has(c, "iex", "invoke-expression", "start-process", ".exe", "| powershell", "-outfile")) { s += 4; why.Add(L.T("Завантажує щось з інтернету і одразу запускає.")); }
            else { s += 2; why.Add(L.T("Завантажує дані з інтернету.")); }
        if (ps && Has(c, "-w hidden", "-windowstyle hidden", "-win hidden", "-w 1 ")) { s += 2; why.Add(L.T("PowerShell із прихованим вікном.")); }
        if (Has(c, "frombase64string") && Has(c, "iex", "invoke-expression", "[reflection.assembly]::load")) { s += 3; why.Add(L.T("Розкодовує і виконує прихований код (Base64).")); }
        if (Has(c, "mshta") && Has(c, "http:", "https:", "javascript:", "vbscript:")) { s += 5; why.Add(L.T("mshta запускає скрипт із мережі чи вбудований код.")); }
        if (Has(c, "rundll32") && Has(c, "javascript:", "http:", "https:", "\\\\")) { s += 4; why.Add(L.T("rundll32 виконує скрипт або бібліотеку з мережі.")); }
        if (Has(c, "regsvr32") && Has(c, "/i:http", "-i:http", "scrobj.dll")) { s += 5; why.Add(L.T("regsvr32 запускає скрипт із мережі (прийом «Squiblydoo»).")); }
        if (Has(c, "certutil") && Has(c, "-urlcache", "/urlcache", "-decode", "/decode")) { s += 4; why.Add(L.T("certutil використано для завантаження чи розкодування файла - типовий прийом шкідливих програм.")); }
        if (Has(c, "bitsadmin") && Has(c, "/transfer", "/addfile")) { s += 3; why.Add(L.T("bitsadmin завантажує файл у фоні.")); }
        if ((Has(c, "vssadmin") && Has(c, "delete shadows")) || Has(c, "shadowcopy delete") || (Has(c, "wbadmin") && Has(c, "delete catalog")) ||
            (Has(c, "bcdedit") && Has(c, "recoveryenabled no", "bootstatuspolicy ignoreallfailures")))
        { s += 6; why.Add(L.T("Видаляє тіньові копії чи вимикає відновлення системи - так поводяться програми-вимагачі.")); }
        if (Has(c, "wevtutil") && Has(c, " cl ", "clear-log")) { s += 3; why.Add(L.T("Очищає журнали подій Windows (замітання слідів).")); }
        if (Has(c, "set-mppreference", "add-mppreference") && Has(c, "exclusion", "disablerealtimemonitoring")) { s += 5; why.Add(L.T("Вимикає захист Microsoft Defender або додає винятки.")); }
        if (Has(c, "schtasks") && Has(c, "/create")) { s += 1; why.Add(L.T("Створює заплановане завдання.")); }
        if (Has(c, "reg add") && Has(c, "\\currentversion\\run")) { s += 2; why.Add(L.T("Додає себе в автозапуск через реєстр.")); }
        return s;
    }

    // запис дозволяє писати звичайним користувачам (Users, Authenticated Users, Everyone, Interactive)
    public static bool UserWritable(string path, bool dir)
    {
        try
        {
            FileSystemSecurity sec = dir ? (FileSystemSecurity)Directory.GetAccessControl(path) : File.GetAccessControl(path);
            string[] weak = { "S-1-5-32-545", "S-1-5-11", "S-1-1-0", "S-1-5-4" };
            FileSystemRights w = dir ? (FileSystemRights.CreateFiles | FileSystemRights.WriteData | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)
                                     : (FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership);
            foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (r.AccessControlType != AccessControlType.Allow) continue;
                if ((r.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
                if (Array.IndexOf(weak, r.IdentityReference.Value) < 0) continue;
                if ((r.FileSystemRights & w) != 0) return true;
            }
        }
        catch { }
        return false;
    }

    // «"C:\path\x.exe" -args», «C:\path\x.exe -args», «%SystemRoot%\…», «\SystemRoot\…», «\??\C:\…», «rundll32 x.dll,Entry» -> файл
    public static string ExeOf(string cmd, out string args)
    {
        args = "";
        if (string.IsNullOrEmpty(cmd)) return "";
        string c = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (c.StartsWith("\\??\\")) c = c.Substring(4);
        if (c.StartsWith("\\SystemRoot\\", StringComparison.OrdinalIgnoreCase)) c = win + c.Substring(11);
        else if (c.StartsWith("system32\\", StringComparison.OrdinalIgnoreCase)) c = win + "\\" + c;
        string exe;
        if (c.StartsWith("\""))
        {
            int e = c.IndexOf('"', 1);
            exe = e > 0 ? c.Substring(1, e - 1) : c.Substring(1);
            args = e > 0 ? c.Substring(e + 1).Trim() : "";
        }
        else
        {
            // без лапок: найдовший префікс до пробілу, що є файлом
            exe = c; args = "";
            int sp = -1;
            for (;;)
            {
                sp = c.IndexOf(' ', sp + 1);
                string cand = sp < 0 ? c : c.Substring(0, sp);
                if (File.Exists(cand) || File.Exists(cand + ".exe")) { exe = File.Exists(cand) ? cand : cand + ".exe"; args = sp < 0 ? "" : c.Substring(sp + 1).Trim(); break; }
                if (sp < 0) { int f = c.IndexOf(' '); exe = f > 0 ? c.Substring(0, f) : c; args = f > 0 ? c.Substring(f + 1) : ""; break; }
            }
        }
        if (!exe.Contains("\\") && exe.Length > 0)
        {
            // лише назва (rundll32, powershell): шукаємо в System32
            string s32 = Path.Combine(win + "\\system32", exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : exe + ".exe");
            if (File.Exists(s32)) exe = s32;
        }
        // rundll32 «бібліотека,Функція» - цікава саме бібліотека
        if (Low(Path.GetFileName(exe)) == "rundll32.exe" && args.Length > 0)
        {
            string a = args.Trim().Trim('"');
            int comma = a.IndexOf(',');
            string dll = comma > 0 ? a.Substring(0, comma).Trim().Trim('"') : a;
            if (dll.Length > 0 && File.Exists(dll)) return dll;
        }
        return exe;
    }

    // позначка «завантажено з інтернету» (альтернативний потік Zone.Identifier)
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    public static string Zone(string path, out int zoneId)
    {
        zoneId = -1;
        try
        {
            using (SafeFileHandle h = CreateFileW(path + ":Zone.Identifier", 0x80000000, 1 | 2, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return "";
                using (FileStream fs = new FileStream(h, FileAccess.Read))
                using (StreamReader r = new StreamReader(fs))
                {
                    string t = r.ReadToEnd(), url = "";
                    foreach (string ln in t.Split('\n'))
                    {
                        string l = ln.Trim();
                        if (l.StartsWith("ZoneId=", StringComparison.OrdinalIgnoreCase)) int.TryParse(l.Substring(7), out zoneId);
                        else if (l.StartsWith("HostUrl=", StringComparison.OrdinalIgnoreCase)) url = l.Substring(8);
                        else if (url.Length == 0 && l.StartsWith("ReferrerUrl=", StringComparison.OrdinalIgnoreCase)) url = l.Substring(12);
                    }
                    return url;
                }
            }
        }
        catch { return ""; }
    }
}
