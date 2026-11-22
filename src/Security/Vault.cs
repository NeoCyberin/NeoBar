using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

// ======================================================================
//  Сейф: тека, у якій файли лежать лише зашифрованими (AES-256 + HMAC-SHA256), без пароля їх не прочитати.
//
//  Будова теки сейфа:
//    vault.nbv    - заголовок: «NBVAULT1», повтори PBKDF2, сіль, головний ключ (64 байти: шифрування + підпис),
//                   зашифрований ключем з пароля, і підпис. Зміна пароля перешифровує лише цей ключ.
//    index.nbv    - зашифрований перелік: ідентифікатор, ім'я (з підтекою), розмір, час додавання.
//    data\<id>    - кожен файл окремо: IV (16) + шифротекст + HMAC (32) від ідентифікатора, IV і шифротексту.
//  Імена файлів теж зашифровані: у теці сейфа видно лише випадкові назви й розміри.
//
//  Межі (чесно): відкритий файл розшифровується в тимчасову теку й лежить там відкритим, поки сейф не замкнуть;
//  забутий пароль не відновити ніяк - файли втрачено.
// ======================================================================
sealed class VaultItem
{
    public string Id = "", Name = "";
    public long Size;
    public DateTime Added;
}

static class Vault
{
    static readonly byte[] Magic = Encoding.ASCII.GetBytes("NBVAULT1");
    const int HeadLen = 8 + 4 + 16 + 16 + 80 + 32;     // магія, повтори, сіль, IV, зашифрований ключ, підпис

    static byte[] encKey, macKey;                       // поки сейф відкритий
    static string dir = "";
    public static readonly List<VaultItem> Items = new List<VaultItem>();
    public static DateTime LastUse = DateTime.MinValue;

    public static bool IsOpen { get { return encKey != null; } }
    public static string Dir { get { return dir; } }

    // де лежить сейф (шлях запам'ятовується в профілі)
    static string PathFile { get { return Path.Combine(Prof.Dir, "vault.path"); } }
    public static string DefaultDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NeoBar", L.T("Сейф")); } }

    public static string Location
    {
        get
        {
            try { if (File.Exists(PathFile)) { string p = File.ReadAllText(PathFile).Trim(); if (p.Length > 0) return p; } }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
            return DefaultDir;
        }
        set { try { File.WriteAllText(PathFile, value ?? "", new UTF8Encoding(false)); } catch (Exception ex) { SbLog.Skip(ex); } }
    }

    public static bool Exists(string d) { return File.Exists(Path.Combine(d, "vault.nbv")); }

    // тимчасова тека для відкритих файлів (прибирається, коли сейф замикають, і під час наступного запуску)
    public static string TempRoot { get { return Path.Combine(Path.GetTempPath(), "NeoBar-safe"); } }

    public static event Action Changed;
    static void Notify() { Action a = Changed; if (a != null) try { a(); } catch (Exception skipEx) { SbLog.Skip(skipEx); } }

    // ---------- ключі ----------

    static void PwKeys(string pw, byte[] salt, int iter, out byte[] ek, out byte[] mk)
    {
        byte[] pb = Encoding.UTF8.GetBytes(pw);
        byte[] dk = SbCrypt.Pbkdf2("SHA256", pb, salt, iter, 32);
        Array.Clear(pb, 0, pb.Length);
        using (HMACSHA256 h = new HMACSHA256(dk))
        {
            ek = h.ComputeHash(Encoding.ASCII.GetBytes("NeoBar vault kek"));
            mk = h.ComputeHash(Encoding.ASCII.GetBytes("NeoBar vault kmac"));
        }
        Array.Clear(dk, 0, dk.Length);
    }

    static byte[] Header(string pw, byte[] master)
    {
        byte[] salt = SbCrypt.Rand(16), iv = SbCrypt.Rand(16), ek, mk;
        PwKeys(pw, salt, SbCrypt.Iter, out ek, out mk);
        byte[] h = new byte[HeadLen];
        Buffer.BlockCopy(Magic, 0, h, 0, 8);
        BitConverter.GetBytes(SbCrypt.Iter).CopyTo(h, 8);
        Buffer.BlockCopy(salt, 0, h, 12, 16);
        Buffer.BlockCopy(iv, 0, h, 28, 16);
        using (Aes a = SbCrypt.MakeAes(ek, iv))
        using (ICryptoTransform t = a.CreateEncryptor())
        {
            byte[] ct = t.TransformFinalBlock(master, 0, master.Length);     // 64 -> 80 (з доповненням)
            Buffer.BlockCopy(ct, 0, h, 44, 80);
        }
        using (HMACSHA256 m = new HMACSHA256(mk)) Buffer.BlockCopy(m.ComputeHash(h, 0, HeadLen - 32), 0, h, HeadLen - 32, 32);
        Array.Clear(ek, 0, ek.Length); Array.Clear(mk, 0, mk.Length);
        return h;
    }

    // null - пароль не підходить; виняток - файл пошкоджено
    static byte[] OpenHeader(byte[] h, string pw)
    {
        if (h.Length != HeadLen || !SbCrypt.Same(h, 0, Magic, 8)) throw new InvalidDataException(L.T("Це не сейф NeoBar або його пошкоджено."));
        int iter = BitConverter.ToInt32(h, 8);
        if (iter < 1000 || iter > 10000000) throw new InvalidDataException(L.T("Сейф пошкоджено."));
        byte[] salt = new byte[16], iv = new byte[16], ek, mk;
        Buffer.BlockCopy(h, 12, salt, 0, 16);
        Buffer.BlockCopy(h, 28, iv, 0, 16);
        PwKeys(pw, salt, iter, out ek, out mk);
        try
        {
            using (HMACSHA256 m = new HMACSHA256(mk))
                if (!SbCrypt.Same(h, HeadLen - 32, m.ComputeHash(h, 0, HeadLen - 32), 32)) return null;
            using (Aes a = SbCrypt.MakeAes(ek, iv))
            using (ICryptoTransform t = a.CreateDecryptor())
                return t.TransformFinalBlock(h, 44, 80);
        }
        finally { Array.Clear(ek, 0, ek.Length); Array.Clear(mk, 0, mk.Length); }
    }

    static void SetMaster(byte[] master)
    {
        byte[] e = new byte[32], m = new byte[32];
        Buffer.BlockCopy(master, 0, e, 0, 32);
        Buffer.BlockCopy(master, 32, m, 0, 32);
        Array.Clear(master, 0, master.Length);
        macKey = m;
        encKey = e;
    }

    // ---------- створення, відкриття, замикання ----------

    public static void Create(string d, string pw)
    {
        if (Exists(d)) throw new IOException(L.T("У цій теці вже є сейф."));
        Directory.CreateDirectory(Path.Combine(d, "data"));
        byte[] master = SbCrypt.Rand(64);
        AtomicWrite(Path.Combine(d, "vault.nbv"), Header(pw, master));
        dir = d;
        SetMaster(master);
        lock (Items) Items.Clear();
        SaveIndex();
        Location = d;
        LastUse = DateTime.Now;
        SbLog.W(SbLog.Panel, "сейф створено");
        Notify();
    }

    // false - неправильний пароль
    public static bool Open(string d, string pw)
    {
        byte[] master = OpenHeader(File.ReadAllBytes(Path.Combine(d, "vault.nbv")), pw);
        if (master == null) return false;
        dir = d;
        SetMaster(master);
        try { LoadIndex(); }
        catch { Lock(); throw; }
        Location = d;
        LastUse = DateTime.Now;
        Notify();
        return true;
    }

    public static void ChangePassword(string oldPw, string newPw)
    {
        string hp = Path.Combine(dir, "vault.nbv");
        byte[] master = OpenHeader(File.ReadAllBytes(hp), oldPw);
        if (master == null) throw new UnauthorizedAccessException(L.T("Неправильний поточний пароль."));
        try { AtomicWrite(hp, Header(newPw, master)); }
        finally { Array.Clear(master, 0, master.Length); }
    }

    // замкнути: ключі стерти, відкриті копії прибрати
    public static void Lock()
    {
        bool was = encKey != null;
        if (encKey != null) Array.Clear(encKey, 0, encKey.Length);
        if (macKey != null) Array.Clear(macKey, 0, macKey.Length);
        encKey = macKey = null;
        lock (Items) Items.Clear();
        CleanTemp();
        if (was) { SbLog.W(SbLog.Panel, "сейф замкнено"); Notify(); }
    }

    // прибрати розшифровані копії; зайняті програмою файли лишаться до наступного разу (повертає, скільки лишилось)
    public static int CleanTemp()
    {
        int left = 0;
        try
        {
            if (!Directory.Exists(TempRoot)) return 0;
            foreach (string f in Directory.GetFiles(TempRoot, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); Wipe(f); File.Delete(f); }
                catch { left++; }
            }
            if (left == 0) try { Directory.Delete(TempRoot, true); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return left;
    }

    // перезаписати вміст нулями перед видаленням (на SSD це не гарантує повного знищення, але прибирає очевидне)
    static void Wipe(string f)
    {
        try
        {
            long n = new FileInfo(f).Length;
            if (n <= 0 || n > 512L * 1024 * 1024) return;
            byte[] z = new byte[1 << 16];
            using (FileStream s = new FileStream(f, FileMode.Open, FileAccess.Write, FileShare.None))
                for (long i = 0; i < n; i += z.Length) s.Write(z, 0, (int)Math.Min(z.Length, n - i));
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    // ---------- перелік ----------

    static void SaveIndex()
    {
        StringBuilder sb = new StringBuilder();
        lock (Items)
            foreach (VaultItem it in Items)
                sb.Append(it.Id).Append('\t').Append(it.Size).Append('\t').Append(it.Added.Ticks).Append('\t')
                  .Append(it.Name.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
        byte[] pt = Encoding.UTF8.GetBytes(sb.ToString());
        AtomicWrite(Path.Combine(dir, "index.nbv"), Seal("index", pt));
        Array.Clear(pt, 0, pt.Length);
    }

    static void LoadIndex()
    {
        lock (Items) Items.Clear();
        string p = Path.Combine(dir, "index.nbv");
        if (!File.Exists(p)) return;
        byte[] pt = Unseal("index", File.ReadAllBytes(p));
        if (pt == null) throw new InvalidDataException(L.T("Перелік файлів сейфа пошкоджено."));
        foreach (string line in Encoding.UTF8.GetString(pt).Split('\n'))
        {
            string[] f = line.Split(new char[] { '\t' }, 4);
            if (f.Length < 4) continue;
            VaultItem it = new VaultItem();
            it.Id = f[0];
            long.TryParse(f[1], out it.Size);
            long tk;
            if (long.TryParse(f[2], out tk)) try { it.Added = new DateTime(tk); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            it.Name = f[3];
            // ідентифікатор - лише шістнадцяткові цифри (ним складається шлях до файла)
            bool ok = it.Id.Length > 0;
            foreach (char c in it.Id) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) ok = false;
            if (ok && it.Name.Length > 0) lock (Items) Items.Add(it);
        }
    }

    // невеликі дані: IV + шифротекст + HMAC (тег прив'язує дані до призначення)
    static byte[] Seal(string tag, byte[] pt)
    {
        byte[] iv = SbCrypt.Rand(16), ct;
        using (Aes a = SbCrypt.MakeAes(encKey, iv))
        using (ICryptoTransform t = a.CreateEncryptor()) ct = t.TransformFinalBlock(pt, 0, pt.Length);
        byte[] all = new byte[16 + ct.Length + 32];
        Buffer.BlockCopy(iv, 0, all, 0, 16);
        Buffer.BlockCopy(ct, 0, all, 16, ct.Length);
        using (HMACSHA256 h = new HMACSHA256(macKey))
        {
            byte[] tb = Encoding.UTF8.GetBytes(tag);
            h.TransformBlock(tb, 0, tb.Length, null, 0);
            h.TransformFinalBlock(all, 0, 16 + ct.Length);
            Buffer.BlockCopy(h.Hash, 0, all, 16 + ct.Length, 32);
        }
        return all;
    }

    static byte[] Unseal(string tag, byte[] all)
    {
        if (all.Length < 16 + 16 + 32 || (all.Length - 48) % 16 != 0) return null;
        int cl = all.Length - 48;
        using (HMACSHA256 h = new HMACSHA256(macKey))
        {
            byte[] tb = Encoding.UTF8.GetBytes(tag);
            h.TransformBlock(tb, 0, tb.Length, null, 0);
            h.TransformFinalBlock(all, 0, 16 + cl);
            if (!SbCrypt.Same(all, 16 + cl, h.Hash, 32)) return null;
        }
        byte[] iv = new byte[16];
        Buffer.BlockCopy(all, 0, iv, 0, 16);
        using (Aes a = SbCrypt.MakeAes(encKey, iv))
        using (ICryptoTransform t = a.CreateDecryptor()) return t.TransformFinalBlock(all, 16, cl);
    }

    static void AtomicWrite(string path, byte[] data)
    {
        string part = path + ".part";
        File.WriteAllBytes(part, data);
        if (File.Exists(path)) File.Replace(part, path, null);
        else File.Move(part, path);
    }

    // ---------- файли ----------

    static string DataPath(string id) { return Path.Combine(dir, "data", id); }

    // додати файл (name - ім'я в сейфі, з підтекою через «\»); повертає новий елемент
    public static VaultItem AddFile(string src, string name, Action<float> progress, Func<bool> stop)
    {
        byte[] ek = encKey, mk = macKey;
        if (ek == null) throw new InvalidOperationException(L.T("Сейф замкнено."));
        string id = BitConverter.ToString(SbCrypt.Rand(12)).Replace("-", "").ToLowerInvariant();
        string dst = DataPath(id), part = dst + ".part";
        byte[] iv = SbCrypt.Rand(16);
        long total;
        try
        {
            using (FileStream inp = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
            using (FileStream outp = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (HMACSHA256 h = new HMACSHA256(mk))
            {
                total = inp.Length;
                byte[] ib = Encoding.ASCII.GetBytes(id);
                h.TransformBlock(ib, 0, ib.Length, null, 0);
                outp.Write(iv, 0, 16);
                h.TransformBlock(iv, 0, 16, null, 0);
                using (Aes a = SbCrypt.MakeAes(ek, iv))
                using (ICryptoTransform t = a.CreateEncryptor())
                using (MacStream ms = new MacStream(outp, h))
                using (CryptoStream cs = new CryptoStream(ms, t, CryptoStreamMode.Write))
                {
                    byte[] buf = new byte[1 << 20];
                    long done = 0;
                    int n;
                    while ((n = inp.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (stop != null && stop()) throw new OperationCanceledException();
                        cs.Write(buf, 0, n);
                        done += n;
                        if (progress != null && total > 0) progress((float)((double)done / total));
                    }
                    cs.FlushFinalBlock();
                }
                h.TransformFinalBlock(new byte[0], 0, 0);
                outp.Write(h.Hash, 0, 32);
            }
            File.Move(part, dst);
        }
        catch { try { File.Delete(part); } catch (Exception skipEx) { SbLog.Skip(skipEx); } throw; }
        VaultItem it = new VaultItem();
        it.Id = id; it.Name = name; it.Size = total; it.Added = DateTime.Now;
        lock (Items) Items.Add(it);
        SaveIndex();
        LastUse = DateTime.Now;
        return it;
    }

    // розшифрувати в теку; спершу перевіряється підпис (нічого не пишеться, поки дані не підтверджено)
    public static string Extract(VaultItem it, string toDir, Action<float> progress, Func<bool> stop)
    {
        byte[] ek = encKey, mk = macKey;
        if (ek == null) throw new InvalidOperationException(L.T("Сейф замкнено."));
        using (FileStream inp = new FileStream(DataPath(it.Id), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
        {
            long body = inp.Length - 16 - 32;
            if (body < 16 || body % 16 != 0) throw new InvalidDataException(L.F("Файл «{0}» у сейфі пошкоджено.", it.Name));
            byte[] iv = new byte[16];
            if (inp.Read(iv, 0, 16) != 16) throw new InvalidDataException(L.F("Файл «{0}» у сейфі пошкоджено.", it.Name));
            using (HMACSHA256 h = new HMACSHA256(mk))
            {
                byte[] ib = Encoding.ASCII.GetBytes(it.Id);
                h.TransformBlock(ib, 0, ib.Length, null, 0);
                h.TransformBlock(iv, 0, 16, null, 0);
                byte[] buf = new byte[1 << 20];
                long left = body;
                while (left > 0)
                {
                    if (stop != null && stop()) throw new OperationCanceledException();
                    int n = inp.Read(buf, 0, (int)Math.Min(buf.Length, left));
                    if (n <= 0) throw new InvalidDataException(L.F("Файл «{0}» у сейфі обрізано.", it.Name));
                    h.TransformBlock(buf, 0, n, null, 0);
                    left -= n;
                    if (progress != null) progress((float)(0.4 * (body - left) / body));
                }
                h.TransformFinalBlock(new byte[0], 0, 0);
                byte[] tag = new byte[32];
                if (inp.Read(tag, 0, 32) != 32 || !SbCrypt.Same(tag, 0, h.Hash, 32)) throw new InvalidDataException(L.F("Файл «{0}» у сейфі змінено чи пошкоджено.", it.Name));
            }
            // ім'я з підтеками: лише безпечні частини, без «..»
            string rel = "";
            foreach (string p in it.Name.Split('\\', '/'))
                if (SbCrypt.SafeName(p)) rel = rel.Length == 0 ? p : Path.Combine(rel, p);
            if (rel.Length == 0) rel = it.Id;
            string dst = SbCrypt.Free(Path.Combine(toDir, rel));
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            string tmp = dst + ".part";
            inp.Position = 16;
            try
            {
                using (Aes a = SbCrypt.MakeAes(ek, iv))
                using (ICryptoTransform t = a.CreateDecryptor())
                using (CryptoStream cs = new CryptoStream(new LimitStream(inp, body), t, CryptoStreamMode.Read))
                using (FileStream outp = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    byte[] buf = new byte[1 << 20];
                    long done = 0;
                    int n;
                    while ((n = cs.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (stop != null && stop()) throw new OperationCanceledException();
                        outp.Write(buf, 0, n);
                        done += n;
                        if (progress != null) progress((float)(0.4 + 0.6 * Math.Min(1.0, (double)done / Math.Max(1, it.Size))));
                    }
                }
                File.Move(tmp, dst);
            }
            catch { try { File.Delete(tmp); } catch (Exception skipEx) { SbLog.Skip(skipEx); } throw; }
            LastUse = DateTime.Now;
            return dst;
        }
    }

    // відкрити файл програмою: розшифровується в тимчасову теку (прибирається, коли сейф замикають)
    public static string OpenCopy(VaultItem it, Action<float> progress, Func<bool> stop)
    {
        string d = Path.Combine(TempRoot, BitConverter.ToString(SbCrypt.Rand(6)).Replace("-", ""));
        Directory.CreateDirectory(d);
        return Extract(it, d, progress, stop);
    }

    public static void Remove(VaultItem it)
    {
        if (!IsOpen) return;
        try { File.Delete(DataPath(it.Id)); } catch (Exception ex) { Util.ErrorLog(ex); }
        lock (Items) Items.Remove(it);
        SaveIndex();
        LastUse = DateTime.Now;
    }

    // перевірка доданого: копія в сейфі розшифровується повністю (перед тим як видалити оригінал)
    public static bool Verify(VaultItem it)
    {
        string d = Path.Combine(TempRoot, "verify-" + BitConverter.ToString(SbCrypt.Rand(4)).Replace("-", ""));
        try
        {
            Directory.CreateDirectory(d);
            string f = Extract(it, d, null, null);
            return new FileInfo(f).Length == it.Size;
        }
        catch (Exception ex) { SbLog.Skip(ex); return false; }
        finally
        {
            try
            {
                if (Directory.Exists(d))
                {
                    foreach (string f in Directory.GetFiles(d, "*", SearchOption.AllDirectories)) { Wipe(f); File.Delete(f); }
                    Directory.Delete(d, true);
                }
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
    }

    public static List<VaultItem> Snapshot() { lock (Items) return new List<VaultItem>(Items); }

    public static long TotalSize { get { long n = 0; lock (Items) foreach (VaultItem it in Items) n += it.Size; return n; } }
}
