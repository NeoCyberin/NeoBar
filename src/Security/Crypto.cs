using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

// ======================================================================
//  Хеш-суми, шифрування тексту й файлів.
//   - Хеші: MD5, SHA-1, SHA-256, SHA-512, CRC32 - за один прохід файлом.
//   - Власний формат .nbenc і текст «SBX1:…»: AES-256-CBC, ключ з пароля через PBKDF2-HMAC-SHA256
//     (200 000 повторів), цілісність - HMAC-SHA256 (спершу перевіряється підпис, лише потім розшифровується,
//     тож із неправильним паролем чи пошкодженим файлом не з'являється жодного байта «сміття»).
//   - ZIP з AES-256 (стандарт WinZip AE-2): відкривається в 7-Zip і WinRAR без NeoBar.
//  Без пароля нічого не відновити - «чорного ходу» немає.
// ======================================================================
static class Crc32
{
    static readonly uint[] table = Make();

    static uint[] Make()
    {
        uint[] t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    // стан починається з 0xFFFFFFFF, у кінці - інверсія
    public static uint Update(uint crc, byte[] b, int off, int n)
    {
        for (int i = 0; i < n; i++) crc = table[(crc ^ b[off + i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}

static class Hashes
{
    public static readonly string[] Names = { "MD5", "SHA-1", "SHA-256", "SHA-512", "CRC32" };

    static string Hex(byte[] b)
    {
        StringBuilder s = new StringBuilder(b.Length * 2);
        foreach (byte x in b) s.Append(x.ToString("x2"));
        return s.ToString();
    }

    static HashAlgorithm Make(int i)
    {
        try
        {
            switch (i)
            {
                case 0: return MD5.Create();          // у режимі FIPS Windows не дає MD5 - тоді просто пропускаємо
                case 1: return SHA1.Create();
                case 2: return SHA256.Create();
                default: return SHA512.Create();
            }
        }
        catch { return null; }
    }

    // усі хеші одним проходом; null - зупинено. progress(0..1) кличеться з потоку обчислення
    public static string[] OfFile(string path, Action<float> progress, Func<bool> stop)
    {
        HashAlgorithm[] a = new HashAlgorithm[4];
        for (int i = 0; i < 4; i++) a[i] = Make(i);
        try
        {
            uint crc = 0xFFFFFFFFu;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
            {
                long len = fs.Length, done = 0;
                byte[] buf = new byte[1 << 20];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    if (stop != null && stop()) return null;
                    foreach (HashAlgorithm h in a) if (h != null) h.TransformBlock(buf, 0, n, null, 0);
                    crc = Crc32.Update(crc, buf, 0, n);
                    done += n;
                    if (progress != null && len > 0) progress((float)((double)done / len));
                }
            }
            string[] r = new string[5];
            for (int i = 0; i < 4; i++)
            {
                if (a[i] == null) { r[i] = ""; continue; }
                a[i].TransformFinalBlock(new byte[0], 0, 0);
                r[i] = Hex(a[i].Hash);
            }
            r[4] = (crc ^ 0xFFFFFFFFu).ToString("x8");
            return r;
        }
        finally { foreach (HashAlgorithm h in a) if (h != null) h.Dispose(); }
    }

    public static string Sha256Of(string path)
    {
        using (SHA256 h = SHA256.Create())
        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
            return Hex(h.ComputeHash(fs));
    }

    // очікуваний хеш, як його вставили (з пробілами, «SHA256: …», великими літерами) -> лише шістнадцяткові цифри
    public static string Clean(string s)
    {
        StringBuilder b = new StringBuilder();
        foreach (char c in (s ?? "").ToLowerInvariant())
            if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')) b.Append(c);
        string r = b.ToString();
        // «SHA256:» чи «sha-1» спереду дають зайві цифри - беремо хвіст відомої довжини
        foreach (int L in new int[] { 128, 64, 40, 32, 8 })
            if (r.Length > L && r.Length <= L + 4) return r.Substring(r.Length - L);
        return r;
    }
}

static class SbCrypt
{
    public const int Iter = 200000;
    static readonly byte[] Magic = Encoding.ASCII.GetBytes("SBENC1");
    public const string Ext = ".nbenc";

    // PBKDF2 з довільним HMAC (SHA-256 для свого формату, SHA-1 - для ZIP AES за стандартом)
    public static byte[] Pbkdf2(HMAC h, byte[] salt, int iter, int len)
    {
        int hl = h.HashSize / 8;
        byte[] res = new byte[len];
        byte[] s = new byte[salt.Length + 4];
        Buffer.BlockCopy(salt, 0, s, 0, salt.Length);
        for (int blk = 1, pos = 0; pos < len; blk++, pos += hl)
        {
            s[salt.Length] = (byte)(blk >> 24); s[salt.Length + 1] = (byte)(blk >> 16); s[salt.Length + 2] = (byte)(blk >> 8); s[salt.Length + 3] = (byte)blk;
            byte[] u = h.ComputeHash(s);
            byte[] t = (byte[])u.Clone();
            for (int i = 1; i < iter; i++)
            {
                u = h.ComputeHash(u);
                for (int j = 0; j < hl; j++) t[j] ^= u[j];
            }
            Buffer.BlockCopy(t, 0, res, pos, Math.Min(hl, len - pos));
        }
        return res;
    }

    static byte[] Rand(int n)
    {
        byte[] b = new byte[n];
        using (RandomNumberGenerator r = RandomNumberGenerator.Create()) r.GetBytes(b);
        return b;
    }

    // ключ шифрування й ключ підпису - окремі, обидва з одного виведеного
    static void Keys(string pw, byte[] salt, int iter, out byte[] enc, out byte[] mac)
    {
        byte[] dk;
        using (HMACSHA256 h = new HMACSHA256(Encoding.UTF8.GetBytes(pw))) dk = Pbkdf2(h, salt, iter, 32);
        using (HMACSHA256 h = new HMACSHA256(dk))
        {
            enc = h.ComputeHash(Encoding.ASCII.GetBytes("NeoBar enc"));
            mac = h.ComputeHash(Encoding.ASCII.GetBytes("NeoBar mac"));
        }
        Array.Clear(dk, 0, dk.Length);
    }

    static Aes MakeAes(byte[] key, byte[] iv)
    {
        Aes a = Aes.Create();
        a.KeySize = 256; a.Mode = CipherMode.CBC; a.Padding = PaddingMode.PKCS7;
        a.Key = key; a.IV = iv;
        return a;
    }

    static bool Same(byte[] a, int ao, byte[] b, int n)
    {
        int d = 0;
        for (int i = 0; i < n; i++) d |= a[ao + i] ^ b[i];
        return d == 0;
    }

    static void PutInt(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
    static int GetInt(byte[] b, int o) { return b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24); }

    // ---------- текст ----------
    public const string TextTag = "SBX1:";

    public static string EncryptText(string text, string pw)
    {
        byte[] salt = Rand(16), iv = Rand(16), ek, mk;
        Keys(pw, salt, Iter, out ek, out mk);
        byte[] ct;
        using (Aes a = MakeAes(ek, iv))
        using (ICryptoTransform t = a.CreateEncryptor())
        {
            byte[] pt = Encoding.UTF8.GetBytes(text ?? "");
            ct = t.TransformFinalBlock(pt, 0, pt.Length);
        }
        byte[] all = new byte[4 + 16 + 16 + ct.Length + 32];
        PutInt(all, 0, Iter);
        Buffer.BlockCopy(salt, 0, all, 4, 16);
        Buffer.BlockCopy(iv, 0, all, 20, 16);
        Buffer.BlockCopy(ct, 0, all, 36, ct.Length);
        using (HMACSHA256 h = new HMACSHA256(mk))
        {
            byte[] m = h.ComputeHash(all, 0, 36 + ct.Length);
            Buffer.BlockCopy(m, 0, all, 36 + ct.Length, 32);
        }
        return TextTag + Convert.ToBase64String(all);
    }

    // null + err - не вдалося (не той пароль, пошкоджено, не наш формат)
    public static string DecryptText(string s, string pw, out string err)
    {
        err = "";
        s = (s ?? "").Trim();
        int at = s.IndexOf(TextTag, StringComparison.Ordinal);
        if (at < 0) { err = L.T("Це не зашифрований NeoBar текст (має починатися з «") + TextTag + L.Q("»)."); return null; }
        // блок Base64 може бути розбитий на рядки (месенджер, лист), а після нього - звичайний текст:
        // беремо найдовшу послідовність шматків, що разом дає коректний блок
        List<string> parts = new List<string>();
        foreach (string w in s.Substring(at + TextTag.Length).Split(new char[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            bool ok = true;
            foreach (char c in w) if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=')) { ok = false; break; }
            if (!ok) break;
            parts.Add(w);
        }
        byte[] all = null;
        for (int k = parts.Count; k > 0 && all == null; k--)
        {
            string b64 = string.Join("", parts.GetRange(0, k).ToArray());
            if (b64.Length % 4 != 0) continue;
            try
            {
                byte[] x = Convert.FromBase64String(b64);
                if (x.Length >= 4 + 32 + 16 + 32 && (x.Length - 36 - 32) % 16 == 0) all = x;
            }
            catch { }
        }
        if (all == null) { err = L.T("Текст пошкоджено або обрізано: частину символів втрачено під час копіювання."); return null; }
        int iter = GetInt(all, 0);
        if (iter < 1000 || iter > 10000000) { err = L.T("Текст пошкоджено."); return null; }
        byte[] salt = new byte[16], iv = new byte[16], ek, mk;
        Buffer.BlockCopy(all, 4, salt, 0, 16);
        Buffer.BlockCopy(all, 20, iv, 0, 16);
        Keys(pw, salt, iter, out ek, out mk);
        int cl = all.Length - 36 - 32;
        using (HMACSHA256 h = new HMACSHA256(mk))
        {
            byte[] m = h.ComputeHash(all, 0, 36 + cl);
            if (!Same(all, 36 + cl, m, 32)) { err = L.T("Неправильний пароль (або текст змінено)."); return null; }
        }
        using (Aes a = MakeAes(ek, iv))
        using (ICryptoTransform t = a.CreateDecryptor())
            return Encoding.UTF8.GetString(t.TransformFinalBlock(all, 36, cl));
    }

    // ---------- файли .nbenc ----------
    // заголовок: «SBENC1», версія 1, повтори (4), сіль (16), IV (16); далі шифротекст; у кінці HMAC-SHA256 (32) від усього попереднього.
    // Усередині шифротексту: довжина імені (2), ім'я (UTF-8), вміст файла.
    const int HeadLen = 6 + 1 + 4 + 16 + 16;

    static string FreeName(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path), nm = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string p = Path.Combine(dir, nm + " (" + i + ")" + ext);
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }
    public static string Free(string path) { return FreeName(path); }

    static void Copy(Stream from, Stream to, long total, ref long done, Action<long> step, Func<bool> stop)
    {
        byte[] buf = new byte[1 << 20];
        int n;
        while ((n = from.Read(buf, 0, buf.Length)) > 0)
        {
            if (stop != null && stop()) throw new OperationCanceledException();
            to.Write(buf, 0, n);
            done += n;
            if (step != null) step(done);
        }
    }

    // результат - шлях нового файла; помилка - виняток з текстом для людини
    public static string EncryptFile(string src, string pw, Action<float> progress, Func<bool> stop)
    {
        string dst = FreeName(src + Ext), part = dst + ".part";
        byte[] salt = Rand(16), iv = Rand(16), ek, mk;
        Keys(pw, salt, Iter, out ek, out mk);
        byte[] head = new byte[HeadLen];
        Buffer.BlockCopy(Magic, 0, head, 0, 6);
        head[6] = 1;
        PutInt(head, 7, Iter);
        Buffer.BlockCopy(salt, 0, head, 11, 16);
        Buffer.BlockCopy(iv, 0, head, 27, 16);
        try
        {
            using (FileStream inp = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
            using (FileStream outp = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (HMACSHA256 h = new HMACSHA256(mk))
            {
                outp.Write(head, 0, head.Length);
                h.TransformBlock(head, 0, head.Length, null, 0);
                long total = inp.Length, done = 0;
                using (Aes a = MakeAes(ek, iv))
                using (ICryptoTransform t = a.CreateEncryptor())
                using (MacStream ms = new MacStream(outp, h))
                using (CryptoStream cs = new CryptoStream(ms, t, CryptoStreamMode.Write))
                {
                    byte[] nm = Encoding.UTF8.GetBytes(Path.GetFileName(src));
                    cs.WriteByte((byte)nm.Length); cs.WriteByte((byte)(nm.Length >> 8));
                    cs.Write(nm, 0, nm.Length);
                    Copy(inp, cs, total, ref done, delegate (long d) { if (progress != null && total > 0) progress((float)((double)d / total)); }, stop);
                    cs.FlushFinalBlock();
                }
                h.TransformFinalBlock(new byte[0], 0, 0);
                outp.Write(h.Hash, 0, 32);
            }
            File.Move(part, dst);
            return dst;
        }
        catch { try { File.Delete(part); } catch { } throw; }
    }

    public static bool IsSbenc(string path)
    {
        try
        {
            using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] b = new byte[6];
                return f.Read(b, 0, 6) == 6 && Same(b, 0, Magic, 6);
            }
        }
        catch { return false; }
    }

    public static string DecryptFile(string src, string pw, Action<float> progress, Func<bool> stop)
    {
        using (FileStream inp = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
        {
            long len = inp.Length;
            byte[] head = new byte[HeadLen];
            if (len < HeadLen + 16 + 32 || inp.Read(head, 0, HeadLen) != HeadLen || !Same(head, 0, Magic, 6))
                throw new InvalidDataException(L.T("Це не файл, зашифрований NeoBar."));
            if (head[6] != 1) throw new InvalidDataException(L.T("Файл зашифровано новішою версією NeoBar."));
            int iter = GetInt(head, 7);
            if (iter < 1000 || iter > 10000000) throw new InvalidDataException(L.T("Файл пошкоджено."));
            byte[] salt = new byte[16], iv = new byte[16], ek, mk;
            Buffer.BlockCopy(head, 11, salt, 0, 16);
            Buffer.BlockCopy(head, 27, iv, 0, 16);
            Keys(pw, salt, iter, out ek, out mk);
            long body = len - HeadLen - 32;
            if (body <= 0 || body % 16 != 0) throw new InvalidDataException(L.T("Файл пошкоджено або обрізано."));
            // 1) перевірка підпису (пароль і цілісність) - поки не перевірено, нічого не пишемо
            using (HMACSHA256 h = new HMACSHA256(mk))
            {
                h.TransformBlock(head, 0, HeadLen, null, 0);
                byte[] buf = new byte[1 << 20];
                long left = body;
                while (left > 0)
                {
                    if (stop != null && stop()) throw new OperationCanceledException();
                    int n = inp.Read(buf, 0, (int)Math.Min(buf.Length, left));
                    if (n <= 0) throw new InvalidDataException(L.T("Файл обрізано."));
                    h.TransformBlock(buf, 0, n, null, 0);
                    left -= n;
                    if (progress != null) progress((float)(0.5 * (body - left) / body));
                }
                h.TransformFinalBlock(new byte[0], 0, 0);
                byte[] tag = new byte[32];
                if (inp.Read(tag, 0, 32) != 32 || !Same(tag, 0, h.Hash, 32)) throw new InvalidDataException(L.T("Неправильний пароль (або файл змінено чи пошкоджено)."));
            }
            // 2) розшифрування
            inp.Position = HeadLen;
            string dir = Path.GetDirectoryName(src), part = null, dst = null;
            try
            {
                using (Aes a = MakeAes(ek, iv))
                using (ICryptoTransform t = a.CreateDecryptor())
                using (CryptoStream cs = new CryptoStream(new LimitStream(inp, body), t, CryptoStreamMode.Read))
                {
                    int l0 = cs.ReadByte(), l1 = cs.ReadByte();
                    if (l0 < 0 || l1 < 0) throw new InvalidDataException(L.T("Файл пошкоджено."));
                    byte[] nm = new byte[l0 | (l1 << 8)];
                    if (ReadAll(cs, nm) != nm.Length) throw new InvalidDataException(L.T("Файл пошкоджено."));
                    string name = Path.GetFileName(Encoding.UTF8.GetString(nm));
                    if (!SafeName(name)) name = Path.GetFileNameWithoutExtension(src);
                    dst = FreeName(Path.Combine(dir, name));
                    part = dst + ".part";
                    long done = 0;
                    using (FileStream outp = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                        Copy(cs, outp, body, ref done, delegate (long d) { if (progress != null) progress((float)(0.5 + 0.5 * Math.Min(1.0, (double)d / body))); }, stop);
                }
                File.Move(part, dst);
                return dst;
            }
            catch { if (part != null) try { File.Delete(part); } catch { } throw; }
        }
    }

    static int ReadAll(Stream s, byte[] b)
    {
        int got = 0, n;
        while (got < b.Length && (n = s.Read(b, got, b.Length - got)) > 0) got += n;
        return got;
    }

    // ім'я файла без шляху, без заборонених символів і без «..»
    public static bool SafeName(string n)
    {
        if (string.IsNullOrEmpty(n) || n == "." || n == "..") return false;
        return n.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && n.IndexOfAny(new char[] { '/', '\\', ':' }) < 0;
    }
}

// потік-обгортка: усе, що записується, ще й проходить через HMAC
sealed class MacStream : Stream
{
    readonly Stream s;
    readonly HashAlgorithm h;
    public long Written;
    public MacStream(Stream s, HashAlgorithm h) { this.s = s; this.h = h; }
    public override bool CanRead { get { return false; } }
    public override bool CanSeek { get { return false; } }
    public override bool CanWrite { get { return true; } }
    public override long Length { get { throw new NotSupportedException(); } }
    public override long Position { get { return Written; } set { throw new NotSupportedException(); } }
    public override void Flush() { s.Flush(); }
    public override int Read(byte[] b, int o, int c) { throw new NotSupportedException(); }
    public override long Seek(long o, SeekOrigin so) { throw new NotSupportedException(); }
    public override void SetLength(long v) { throw new NotSupportedException(); }
    public override void Write(byte[] b, int o, int c)
    {
        h.TransformBlock(b, o, c, null, 0);
        s.Write(b, o, c);
        Written += c;
    }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); }       // базовий потік не закриваємо
}

// читання не більше n байтів з поточного місця (базовий потік лишається відкритим)
sealed class LimitStream : Stream
{
    readonly Stream s;
    long left;
    public LimitStream(Stream s, long n) { this.s = s; left = n; }
    public override bool CanRead { get { return true; } }
    public override bool CanSeek { get { return false; } }
    public override bool CanWrite { get { return false; } }
    public override long Length { get { throw new NotSupportedException(); } }
    public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
    public override void Flush() { }
    public override int Read(byte[] b, int o, int c)
    {
        if (left <= 0) return 0;
        int n = s.Read(b, o, (int)Math.Min(c, left));
        if (n > 0) left -= n;
        return n;
    }
    public override long Seek(long o, SeekOrigin so) { throw new NotSupportedException(); }
    public override void SetLength(long v) { throw new NotSupportedException(); }
    public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); }
}

// ======================================================================
//  ZIP з AES-256 (WinZip AE-2). Шифр - AES у режимі лічильника (лічильник little-endian з 1),
//  ключі з PBKDF2-HMAC-SHA1 (1000 повторів, за стандартом), підпис - HMAC-SHA1 (10 байтів) від шифротексту.
//  Без Zip64: кожен файл і весь архів - до 4 ГБ.
// ======================================================================
sealed class AesCtr
{
    readonly ICryptoTransform ecb;
    readonly byte[] ctr = new byte[16], ks = new byte[16];
    int used = 16;

    public AesCtr(byte[] key)
    {
        Aes a = Aes.Create();
        a.KeySize = 256; a.Mode = CipherMode.ECB; a.Padding = PaddingMode.None; a.Key = key;
        ecb = a.CreateEncryptor();
    }

    public void Xor(byte[] b, int o, int n)
    {
        for (int i = 0; i < n; i++)
        {
            if (used == 16)
            {
                for (int j = 0; j < 8; j++) if (++ctr[j] != 0) break;
                ecb.TransformBlock(ctr, 0, 16, ks, 0);
                used = 0;
            }
            b[o + i] ^= ks[used++];
        }
    }
}

// запис: дані шифруються, підписуються й ідуть у файл архіву
sealed class ZipAesWrite : Stream
{
    readonly Stream s;
    readonly AesCtr c;
    readonly HMACSHA1 h;
    public long Written;
    byte[] tmp = new byte[1 << 16];
    public ZipAesWrite(Stream s, AesCtr c, HMACSHA1 h) { this.s = s; this.c = c; this.h = h; }
    public override bool CanRead { get { return false; } }
    public override bool CanSeek { get { return false; } }
    public override bool CanWrite { get { return true; } }
    public override long Length { get { throw new NotSupportedException(); } }
    public override long Position { get { return Written; } set { throw new NotSupportedException(); } }
    public override void Flush() { }
    public override int Read(byte[] b, int o, int n) { throw new NotSupportedException(); }
    public override long Seek(long o, SeekOrigin so) { throw new NotSupportedException(); }
    public override void SetLength(long v) { throw new NotSupportedException(); }
    public override void Write(byte[] b, int o, int n)
    {
        while (n > 0)
        {
            int k = Math.Min(n, tmp.Length);
            Buffer.BlockCopy(b, o, tmp, 0, k);
            c.Xor(tmp, 0, k);
            h.TransformBlock(tmp, 0, k, null, 0);
            s.Write(tmp, 0, k);
            Written += k; o += k; n -= k;
        }
    }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); }
}

static class ZipAes
{
    public const long Max = 0xFFFFFFFFL - 1024;

    static readonly string[] Packed = { ".zip", ".7z", ".rar", ".gz", ".xz", ".bz2", ".zst", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".avif",
        ".mp3", ".aac", ".ogg", ".opus", ".flac", ".m4a", ".mp4", ".mkv", ".avi", ".mov", ".webm", ".docx", ".xlsx", ".pptx", ".pdf", ".apk", ".jar", ".cab", ".msi" };

    static void U16(Stream s, int v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); }
    static void U32(Stream s, long v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 24)); }

    static void DosTime(DateTime t, out int time, out int date)
    {
        if (t.Year < 1980) t = new DateTime(1980, 1, 1);
        time = (t.Hour << 11) | (t.Minute << 5) | (t.Second / 2);
        date = ((t.Year - 1980) << 9) | (t.Month << 5) | t.Day;
    }

    static void Derive(string pw, byte[] salt, out byte[] key, out byte[] mac, out byte[] pv)
    {
        byte[] dk;
        using (HMACSHA1 h = new HMACSHA1(Encoding.UTF8.GetBytes(pw))) dk = SbCrypt.Pbkdf2(h, salt, 1000, 66);
        key = new byte[32]; mac = new byte[32]; pv = new byte[2];
        Buffer.BlockCopy(dk, 0, key, 0, 32);
        Buffer.BlockCopy(dk, 32, mac, 0, 32);
        Buffer.BlockCopy(dk, 64, pv, 0, 2);
    }

    sealed class Ent { public string Name; public int Method, Time, Date; public long Comp, Size, Off; public bool Dir; }

    // для ZIP пароль - лише ASCII: інші архіватори по-різному кодують кирилицю, і такий архів міг би не відкритися
    public static bool AsciiOk(string pw)
    {
        foreach (char c in pw) if (c < 32 || c > 126) return false;
        return true;
    }

    // items: (шлях на диску, ім'я в архіві; тека - ім'я з «/» в кінці і шлях null)
    public static void Create(string zipPath, List<KeyValuePair<string, string>> items, string pw, Action<float> progress, Func<bool> stop)
    {
        long total = 0, done = 0;
        foreach (KeyValuePair<string, string> it in items)
            if (it.Key != null)
            {
                long l = new FileInfo(it.Key).Length;
                if (l > Max) throw new InvalidDataException(L.Q("«") + Path.GetFileName(it.Key) + L.T("» більший за 4 ГБ - для такого ZIP без Zip64 завеликий. Оберіть формат .nbenc."));
                total += l;
            }
        string part = zipPath + ".part";
        List<Ent> ents = new List<Ent>();
        RandomNumberGenerator rng = RandomNumberGenerator.Create();
        try
        {
            using (FileStream z = new FileStream(part, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16))
            {
                foreach (KeyValuePair<string, string> it in items)
                {
                    Ent e = new Ent();
                    e.Name = it.Value.Replace('\\', '/');
                    e.Dir = it.Key == null;
                    DateTime mt = e.Dir ? DateTime.Now : File.GetLastWriteTime(it.Key);
                    DosTime(mt, out e.Time, out e.Date);
                    e.Off = z.Position;
                    if (e.Off > Max) throw new InvalidDataException(L.T("Архів виходить більшим за 4 ГБ. Оберіть формат .nbenc."));
                    byte[] nb = Encoding.UTF8.GetBytes(e.Name);
                    // вже стиснуте й порожнє - без стиснення (порожній потік Deflate деякі архіватори вважають помилкою)
                    e.Method = e.Dir || new FileInfo(it.Key).Length == 0 || Array.IndexOf(Packed, Path.GetExtension(it.Key).ToLowerInvariant()) >= 0 ? 0 : 8;
                    // локальний заголовок (розміри допишемо після даних)
                    U32(z, 0x04034b50); U16(z, e.Dir ? 20 : 51); U16(z, (e.Dir ? 0 : 1) | 0x800); U16(z, e.Dir ? 0 : 99);
                    U16(z, e.Time); U16(z, e.Date); U32(z, 0);
                    long sizesAt = z.Position;
                    U32(z, 0); U32(z, 0);
                    U16(z, nb.Length); U16(z, e.Dir ? 0 : 11);
                    z.Write(nb, 0, nb.Length);
                    if (!e.Dir) { U16(z, 0x9901); U16(z, 7); U16(z, 2); z.WriteByte((byte)'A'); z.WriteByte((byte)'E'); z.WriteByte(3); U16(z, e.Method); }
                    if (!e.Dir)
                    {
                        byte[] salt = new byte[16], key, mac, pv;
                        rng.GetBytes(salt);
                        Derive(pw, salt, out key, out mac, out pv);
                        long start = z.Position;
                        z.Write(salt, 0, 16); z.Write(pv, 0, 2);
                        using (HMACSHA1 hm = new HMACSHA1(mac))
                        {
                            ZipAesWrite w = new ZipAesWrite(z, new AesCtr(key), hm);
                            using (FileStream inp = new FileStream(it.Key, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
                            {
                                e.Size = inp.Length;
                                Stream sink = e.Method == 8 ? (Stream)new DeflateStream(w, CompressionMode.Compress, true) : w;
                                byte[] buf = new byte[1 << 20];
                                int n;
                                while ((n = inp.Read(buf, 0, buf.Length)) > 0)
                                {
                                    if (stop != null && stop()) throw new OperationCanceledException();
                                    sink.Write(buf, 0, n);
                                    done += n;
                                    if (progress != null && total > 0) progress((float)((double)done / total));
                                }
                                if (e.Method == 8) sink.Dispose();
                            }
                            hm.TransformFinalBlock(new byte[0], 0, 0);
                            z.Write(hm.Hash, 0, 10);
                        }
                        e.Comp = z.Position - start;
                        if (e.Comp > Max) throw new InvalidDataException(L.T("Архів виходить більшим за 4 ГБ. Оберіть формат .nbenc."));
                        long end = z.Position;
                        z.Position = sizesAt; U32(z, e.Comp); U32(z, e.Size);
                        z.Position = end;
                    }
                    ents.Add(e);
                }
                long cd = z.Position;
                foreach (Ent e in ents)
                {
                    byte[] nb = Encoding.UTF8.GetBytes(e.Name);
                    U32(z, 0x02014b50); U16(z, 63); U16(z, e.Dir ? 20 : 51); U16(z, (e.Dir ? 0 : 1) | 0x800); U16(z, e.Dir ? 0 : 99);
                    U16(z, e.Time); U16(z, e.Date); U32(z, 0); U32(z, e.Comp); U32(z, e.Size);
                    U16(z, nb.Length); U16(z, e.Dir ? 0 : 11); U16(z, 0); U16(z, 0); U16(z, 0);
                    U32(z, e.Dir ? 0x10 : 0x20); U32(z, e.Off);
                    z.Write(nb, 0, nb.Length);
                    if (!e.Dir) { U16(z, 0x9901); U16(z, 7); U16(z, 2); z.WriteByte((byte)'A'); z.WriteByte((byte)'E'); z.WriteByte(3); U16(z, e.Method); }
                }
                long cdLen = z.Position - cd;
                if (z.Position > Max || ents.Count > 65000) throw new InvalidDataException(L.T("Архів завеликий для ZIP без Zip64. Оберіть формат .nbenc."));
                U32(z, 0x06054b50); U16(z, 0); U16(z, 0); U16(z, ents.Count); U16(z, ents.Count); U32(z, cdLen); U32(z, cd); U16(z, 0);
            }
            File.Move(part, zipPath);
        }
        catch { try { File.Delete(part); } catch { } throw; }
        finally { rng.Dispose(); }
    }

    // ---------- читання ----------
    public sealed class Entry
    {
        public string Name;
        public int Flags, Method, AesMethod, Strength, AeVer;
        public long Comp, Size, LocalOff;
        public uint Crc;
        public bool Aes { get { return Method == 99; } }
        public bool Encrypted { get { return (Flags & 1) != 0; } }
        public bool Dir { get { return Name.EndsWith("/"); } }
    }

    static int R16(byte[] b, int o) { return b[o] | (b[o + 1] << 8); }
    static long R32(byte[] b, int o) { return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)); }

    // зміст архіву за центральним каталогом; null - не ZIP (або Zip64, якого тут немає)
    public static List<Entry> List(string path, out string err)
    {
        err = "";
        try
        {
            using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long len = f.Length;
                int tail = (int)Math.Min(len, 65536 + 22);
                byte[] t = new byte[tail];
                f.Position = len - tail;
                if (f.Read(t, 0, tail) != tail) { err = L.T("Не вдалося прочитати архів."); return null; }
                int eocd = -1;
                for (int i = tail - 22; i >= 0; i--) if (t[i] == 0x50 && t[i + 1] == 0x4b && t[i + 2] == 5 && t[i + 3] == 6) { eocd = i; break; }
                if (eocd < 0) { err = L.T("Це не ZIP-архів."); return null; }
                int count = R16(t, eocd + 10);
                long cdLen = R32(t, eocd + 12), cdOff = R32(t, eocd + 16);
                if (cdOff == 0xFFFFFFFFL || count == 0xFFFF) { err = L.T("Архів у форматі Zip64 - його відкриє 7-Zip."); return null; }
                if (cdOff + cdLen > len || cdLen > 64L << 20) { err = L.T("Архів пошкоджено."); return null; }
                byte[] cd = new byte[cdLen];
                f.Position = cdOff;
                if (f.Read(cd, 0, cd.Length) != cd.Length) { err = L.T("Архів пошкоджено."); return null; }
                List<Entry> r = new List<Entry>();
                int p = 0;
                for (int i = 0; i < count && p + 46 <= cd.Length; i++)
                {
                    if (R32(cd, p) != 0x02014b50) { err = L.T("Архів пошкоджено."); return null; }
                    Entry e = new Entry();
                    e.Flags = R16(cd, p + 8); e.Method = R16(cd, p + 10); e.Crc = (uint)R32(cd, p + 16);
                    e.Comp = R32(cd, p + 20); e.Size = R32(cd, p + 24);
                    int nl = R16(cd, p + 28), xl = R16(cd, p + 30), cl = R16(cd, p + 32);
                    e.LocalOff = R32(cd, p + 42);
                    if (p + 46 + nl + xl + cl > cd.Length) { err = L.T("Архів пошкоджено."); return null; }
                    Encoding enc = (e.Flags & 0x800) != 0 ? Encoding.UTF8 : Encoding.GetEncoding(437);
                    e.Name = enc.GetString(cd, p + 46, nl).Replace('\\', '/');
                    for (int x = p + 46 + nl; x + 4 <= p + 46 + nl + xl; )
                    {
                        int id = R16(cd, x), sz = R16(cd, x + 2);
                        if (id == 0x9901 && sz >= 7) { e.AeVer = R16(cd, x + 4); e.Strength = cd[x + 8]; e.AesMethod = R16(cd, x + 9); }
                        x += 4 + sz;
                    }
                    r.Add(e);
                    p += 46 + nl + xl + cl;
                }
                return r;
            }
        }
        catch (Exception ex) { err = ex.Message; return null; }
    }

    // шлях усередині теки призначення: відкидаємо «..», диски й абсолютні шляхи (захист від виходу за межі теки)
    public static string SafeRel(string name)
    {
        List<string> parts = new List<string>();
        foreach (string s0 in name.Split('/'))
        {
            string s = s0.Trim();
            if (s.Length == 0 || s == "." || s == "..") continue;
            StringBuilder b = new StringBuilder();
            foreach (char c in s) b.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || c == ':' ? '_' : c);
            string q = b.ToString().TrimEnd('.', ' ');
            if (q.Length > 0) parts.Add(q);
        }
        return string.Join(Path.DirectorySeparatorChar.ToString(), parts.ToArray());
    }

    // розпакування в нову теку поруч з архівом; повертає теку. Шифровані записи - лише AES (ZipCrypto застарілий і тут не підтримується)
    public static string Extract(string zip, string pw, Action<float> progress, Func<bool> stop, out string notes)
    {
        notes = "";
        string err;
        List<Entry> ents = List(zip, out err);
        if (ents == null) throw new InvalidDataException(err);
        bool anyAes = false;
        foreach (Entry e in ents) if (e.Aes) anyAes = true;
        string root = SbCrypt.Free(Path.Combine(Path.GetDirectoryName(zip), Path.GetFileNameWithoutExtension(zip)));
        long total = 0, done = 0;
        foreach (Entry e in ents) total += e.Comp;
        int skipped = 0;
        bool made = false;
        try
        {
        using (FileStream f = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
        {
            foreach (Entry e in ents)
            {
                string rel = SafeRel(e.Name);
                if (rel.Length == 0) continue;
                string dst = Path.Combine(root, rel);
                if (e.Dir) { Directory.CreateDirectory(dst); made = true; continue; }
                if (e.Encrypted && !e.Aes) { skipped++; continue; }
                int method = e.Aes ? e.AesMethod : e.Method;
                if (method != 0 && method != 8) { skipped++; continue; }
                byte[] lh = new byte[30];
                f.Position = e.LocalOff;
                if (f.Read(lh, 0, 30) != 30 || R32(lh, 0) != 0x04034b50) throw new InvalidDataException(L.T("Архів пошкоджено."));
                long dataAt = e.LocalOff + 30 + R16(lh, 26) + R16(lh, 28);
                long dataLen = e.Comp;
                AesCtr ctr = null;
                if (e.Aes)
                {
                    int sl = e.Strength == 1 ? 8 : (e.Strength == 2 ? 12 : 16);
                    if (e.Strength != 3) { skipped++; continue; }
                    byte[] salt = new byte[sl], pv = new byte[2], key, mac, pvx;
                    f.Position = dataAt;
                    f.Read(salt, 0, sl); f.Read(pv, 0, 2);
                    Derive(pw, salt, out key, out mac, out pvx);
                    if (pv[0] != pvx[0] || pv[1] != pvx[1]) throw new InvalidDataException(L.T("Неправильний пароль."));
                    dataAt += sl + 2;
                    dataLen = e.Comp - sl - 2 - 10;
                    if (dataLen < 0) throw new InvalidDataException(L.T("Архів пошкоджено."));
                    // підпис перевіряємо до розшифрування
                    using (HMACSHA1 hm = new HMACSHA1(mac))
                    {
                        byte[] buf = new byte[1 << 20];
                        long left = dataLen;
                        while (left > 0)
                        {
                            if (stop != null && stop()) throw new OperationCanceledException();
                            int n = f.Read(buf, 0, (int)Math.Min(buf.Length, left));
                            if (n <= 0) throw new InvalidDataException(L.T("Архів обрізано."));
                            hm.TransformBlock(buf, 0, n, null, 0);
                            left -= n;
                        }
                        hm.TransformFinalBlock(new byte[0], 0, 0);
                        byte[] tag = new byte[10];
                        f.Read(tag, 0, 10);
                        for (int i = 0; i < 10; i++) if (tag[i] != hm.Hash[i]) throw new InvalidDataException(L.T("Файл «") + rel + L.T("» в архіві пошкоджено (або змінено)."));
                    }
                    ctr = new AesCtr(key);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                made = true;
                string part = dst + ".part";
                try
                {
                    f.Position = dataAt;
                    Stream src = new LimitStream(f, dataLen);
                    if (ctr != null) src = new CtrRead(src, ctr);
                    if (method == 8) src = new DeflateStream(src, CompressionMode.Decompress);
                    uint crc = 0xFFFFFFFFu;
                    long written = 0;
                    long cap = e.Size == 0xFFFFFFFFL ? long.MaxValue : e.Size;
                    if (e.Size > 0 && new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))).AvailableFreeSpace < e.Size + (64L << 20))
                        throw new IOException(L.T("Недостатньо місця на диску для розпакування «") + rel + L.Q("»."));
                    using (FileStream o = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        byte[] buf = new byte[1 << 20];
                        int n;
                        long before = done;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (stop != null && stop()) throw new OperationCanceledException();
                            written += n;
                            if (written > cap) throw new InvalidDataException(L.T("Файл «") + rel + L.T("» більший, ніж оголошено в архіві (схоже на «бомбу») - розпаковку зупинено."));
                            o.Write(buf, 0, n);
                            crc = Crc32.Update(crc, buf, 0, n);
                            if (progress != null && total > 0) progress((float)Math.Min(1.0, (double)(before + Math.Min(e.Comp, o.Length)) / total));
                        }
                    }
                    // AE-2 (і невеликі файли) CRC не мають; для решти перевіряємо
                    if (!(e.Aes && e.AeVer == 2) && (crc ^ 0xFFFFFFFFu) != e.Crc) throw new InvalidDataException(L.T("Файл «") + rel + L.T("» в архіві пошкоджено."));
                    string fin = SbCrypt.Free(dst);
                    File.Move(part, fin);
                }
                catch { try { File.Delete(part); } catch { } throw; }
                done += e.Comp;
            }
        }
        }
        catch
        {
            // невдача (пароль, пошкодження, зупинка): тека без жодного файла не лишається
            try { if (Directory.Exists(root) && Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == 0) Directory.Delete(root, true); } catch { }
            throw;
        }
        if (!anyAes) notes = L.T("В архіві немає шифрованих файлів - його просто розпаковано.");
        if (skipped > 0) notes = (notes.Length > 0 ? notes + " " : "") + L.T("Пропущено записів у непідтримуваному форматі: ") + skipped + L.T(" (їх відкриє 7-Zip).");
        if (!made) Directory.CreateDirectory(root);
        return root;
    }
}

// розшифрування AES-CTR під час читання
sealed class CtrRead : Stream
{
    readonly Stream s;
    readonly AesCtr c;
    public CtrRead(Stream s, AesCtr c) { this.s = s; this.c = c; }
    public override bool CanRead { get { return true; } }
    public override bool CanSeek { get { return false; } }
    public override bool CanWrite { get { return false; } }
    public override long Length { get { throw new NotSupportedException(); } }
    public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
    public override void Flush() { }
    public override int Read(byte[] b, int o, int n)
    {
        int k = s.Read(b, o, n);
        if (k > 0) c.Xor(b, o, k);
        return k;
    }
    public override long Seek(long o, SeekOrigin so) { throw new NotSupportedException(); }
    public override void SetLength(long v) { throw new NotSupportedException(); }
    public override void Write(byte[] b, int o, int n) { throw new NotSupportedException(); }
}
