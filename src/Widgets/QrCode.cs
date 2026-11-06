using System;
using System.Collections.Generic;
using System.Text;

// ======================================================================
//  QR-код (ISO/IEC 18004): байтовий режим, версії 1-40, рівні корекції L/M/Q/H,
//  код Ріда-Соломона, вибір маски за штрафними балами. Без сторонніх бібліотек.
// ======================================================================
static class QrCode
{
    // рівні корекції: 0 L (~7%), 1 M (~15%), 2 Q (~25%), 3 H (~30%)
    static readonly int[] FormatBits = { 1, 0, 3, 2 };

    static readonly int[,] EccPerBlock = {
        { -1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
        { -1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        { -1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 } };

    static readonly int[,] Blocks = {
        { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
        { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
        { -1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },
        { -1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 } };

    public sealed class Result
    {
        public bool[,] Dark;        // [y, x]
        public int Size, Version, Ecl, Mask, Bytes, Capacity;
    }

    static int RawModules(int ver)
    {
        int r = (16 * ver + 128) * ver + 64;
        if (ver >= 2)
        {
            int na = ver / 7 + 2;
            r -= (25 * na - 10) * na - 55;
            if (ver >= 7) r -= 36;
        }
        return r;
    }

    static int DataCodewords(int ver, int ecl) { return RawModules(ver) / 8 - EccPerBlock[ecl, ver] * Blocks[ecl, ver]; }

    // скільки байтів уміщає версія при цьому рівні (байтовий режим)
    public static int CapacityBytes(int ver, int ecl)
    {
        int bits = DataCodewords(ver, ecl) * 8 - 4 - (ver < 10 ? 8 : 16);
        return Math.Max(0, bits / 8);
    }

    // текст у UTF-8 без BOM (сучасні сканери розпізнають UTF-8; для чистого ASCII це те саме)
    public static Result EncodeText(string text, int ecl, bool boost) { return Encode(Encoding.UTF8.GetBytes(text ?? ""), ecl, boost); }

    // null - не вміщається навіть у версію 40
    public static Result Encode(byte[] data, int ecl, bool boost)
    {
        ecl = Math.Max(0, Math.Min(3, ecl));
        int ver = 0;
        for (int v = 1; v <= 40; v++)
            if (data.Length <= CapacityBytes(v, ecl)) { ver = v; break; }
        if (ver == 0) return null;
        // вищий рівень корекції, якщо дані все одно вміщаються в цю ж версію
        if (boost)
            for (int e = 3; e > ecl; e--)
                if (data.Length <= CapacityBytes(ver, e)) { ecl = e; break; }

        // ---- біти: режим (байти), довжина, дані, кінець, вирівнювання, заповнювачі ----
        List<bool> bits = new List<bool>();
        Action<int, int> put = delegate (int val, int n) { for (int i = n - 1; i >= 0; i--) bits.Add(((val >> i) & 1) != 0); };
        put(4, 4);
        put(data.Length, ver < 10 ? 8 : 16);
        foreach (byte b in data) put(b, 8);
        int cap = DataCodewords(ver, ecl) * 8;
        put(0, Math.Min(4, cap - bits.Count));
        put(0, (8 - bits.Count % 8) % 8);
        for (int pad = 0xEC; bits.Count < cap; pad ^= 0xEC ^ 0x11) put(pad, 8);
        byte[] cw = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++) if (bits[i]) cw[i >> 3] |= (byte)(1 << (7 - (i & 7)));

        byte[] all = AddEcc(cw, ver, ecl);
        int size = ver * 4 + 17;
        bool[,] dark = new bool[size, size], fn = new bool[size, size];
        Functions(dark, fn, ver, size);
        PlaceData(dark, fn, all, size);

        // маска з найменшим штрафом
        int best = 0, bestPen = int.MaxValue;
        for (int m = 0; m < 8; m++)
        {
            ApplyMask(dark, fn, m, size);
            Format(dark, fn, ecl, m, size);
            int pen = Penalty(dark, size);
            if (pen < bestPen) { bestPen = pen; best = m; }
            ApplyMask(dark, fn, m, size);       // XOR - повторне накладання знімає маску
        }
        ApplyMask(dark, fn, best, size);
        Format(dark, fn, ecl, best, size);

        Result r = new Result();
        r.Dark = dark; r.Size = size; r.Version = ver; r.Ecl = ecl; r.Mask = best; r.Bytes = data.Length; r.Capacity = CapacityBytes(ver, ecl);
        return r;
    }

    // ---------- Рід-Соломон над GF(256), поліном 0x11D ----------
    static int Mul(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z & 0xFF;
    }

    static byte[] Divisor(int deg)
    {
        byte[] r = new byte[deg];
        r[deg - 1] = 1;
        int root = 1;
        for (int i = 0; i < deg; i++)
        {
            for (int j = 0; j < deg; j++)
            {
                r[j] = (byte)Mul(r[j], root);
                if (j + 1 < deg) r[j] ^= r[j + 1];
            }
            root = Mul(root, 2);
        }
        return r;
    }

    static byte[] Remainder(byte[] data, int off, int len, byte[] div)
    {
        byte[] r = new byte[div.Length];
        for (int k = 0; k < len; k++)
        {
            int f = data[off + k] ^ r[0];
            Array.Copy(r, 1, r, 0, r.Length - 1);
            r[r.Length - 1] = 0;
            for (int i = 0; i < r.Length; i++) r[i] ^= (byte)Mul(div[i], f);
        }
        return r;
    }

    static byte[] AddEcc(byte[] data, int ver, int ecl)
    {
        int nb = Blocks[ecl, ver], ecc = EccPerBlock[ecl, ver];
        int raw = RawModules(ver) / 8;
        int nShort = nb - raw % nb, shortLen = raw / nb;
        byte[] div = Divisor(ecc);
        byte[][] blocks = new byte[nb][];
        int k = 0;
        for (int i = 0; i < nb; i++)
        {
            int dl = shortLen - ecc + (i < nShort ? 0 : 1);
            byte[] e = Remainder(data, k, dl, div);
            byte[] b = new byte[shortLen + 1];
            // короткі блоки: на місці «зайвого» байта - пропуск (не потрапляє в результат)
            int p = 0;
            for (int j = 0; j < dl; j++) b[p++] = data[k + j];
            if (i < nShort) p++;
            for (int j = 0; j < ecc; j++) b[p++] = e[j];
            blocks[i] = b;
            k += dl;
        }
        List<byte> res = new List<byte>(raw);
        for (int i = 0; i < shortLen + 1; i++)
            for (int j = 0; j < nb; j++)
                if (i != shortLen - ecc || j >= nShort) res.Add(blocks[j][i]);
        return res.ToArray();
    }

    // ---------- службові візерунки ----------
    static void Set(bool[,] d, bool[,] fn, int x, int y, bool dark) { d[y, x] = dark; fn[y, x] = true; }

    static int[] AlignPos(int ver)
    {
        if (ver == 1) return new int[0];
        int na = ver / 7 + 2;
        int step = ver == 32 ? 26 : (ver * 4 + na * 2 + 1) / (na * 2 - 2) * 2;
        int[] r = new int[na];
        r[0] = 6;
        for (int i = na - 1, pos = ver * 4 + 10; i >= 1; i--, pos -= step) r[i] = pos;
        return r;
    }

    static void Functions(bool[,] d, bool[,] fn, int ver, int size)
    {
        for (int i = 0; i < size; i++) { Set(d, fn, 6, i, i % 2 == 0); Set(d, fn, i, 6, i % 2 == 0); }
        Finder(d, fn, 3, 3, size); Finder(d, fn, size - 4, 3, size); Finder(d, fn, 3, size - 4, size);
        int[] ap = AlignPos(ver);
        int n = ap.Length;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == n - 1) || (i == n - 1 && j == 0)) continue;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                        Set(d, fn, ap[i] + dx, ap[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        Format(d, fn, 0, 0, size);        // місце під біти формату (справжні - після вибору маски)
        if (ver >= 7)
        {
            int rem = ver;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = ver << 12 | rem;
            for (int i = 0; i < 18; i++)
            {
                bool b = ((bits >> i) & 1) != 0;
                int a = size - 11 + i % 3, c = i / 3;
                Set(d, fn, a, c, b);
                Set(d, fn, c, a, b);
            }
        }
    }

    static void Finder(bool[,] d, bool[,] fn, int x, int y, int size)
    {
        for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || yy < 0 || xx >= size || yy >= size) continue;
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                Set(d, fn, xx, yy, dist != 2 && dist != 4);
            }
    }

    static void Format(bool[,] d, bool[,] fn, int ecl, int mask, int size)
    {
        int data = FormatBits[ecl] << 3 | mask, rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        int bits = (data << 10 | rem) ^ 0x5412;
        Func<int, bool> bit = delegate (int i) { return ((bits >> i) & 1) != 0; };
        for (int i = 0; i <= 5; i++) Set(d, fn, 8, i, bit(i));
        Set(d, fn, 8, 7, bit(6));
        Set(d, fn, 8, 8, bit(7));
        Set(d, fn, 7, 8, bit(8));
        for (int i = 9; i < 15; i++) Set(d, fn, 14 - i, 8, bit(i));
        for (int i = 0; i < 8; i++) Set(d, fn, size - 1 - i, 8, bit(i));
        for (int i = 8; i < 15; i++) Set(d, fn, 8, size - 15 + i, bit(i));
        Set(d, fn, 8, size - 8, true);
    }

    static void PlaceData(bool[,] d, bool[,] fn, byte[] data, int size)
    {
        int i = 0, total = data.Length * 8;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < size; vert++)
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool up = ((right + 1) & 2) == 0;
                    int y = up ? size - 1 - vert : vert;
                    if (!fn[y, x] && i < total)
                    {
                        d[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                        i++;
                    }
                }
        }
    }

    static void ApplyMask(bool[,] d, bool[,] fn, int m, int size)
    {
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                if (fn[y, x]) continue;
                bool inv;
                switch (m)
                {
                    case 0: inv = (x + y) % 2 == 0; break;
                    case 1: inv = y % 2 == 0; break;
                    case 2: inv = x % 3 == 0; break;
                    case 3: inv = (x + y) % 3 == 0; break;
                    case 4: inv = (x / 3 + y / 2) % 2 == 0; break;
                    case 5: inv = x * y % 2 + x * y % 3 == 0; break;
                    case 6: inv = (x * y % 2 + x * y % 3) % 2 == 0; break;
                    default: inv = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                }
                if (inv) d[y, x] = !d[y, x];
            }
    }

    // штрафні бали за стандартом: довгі смуги, квадрати 2×2, схожість на шукач, баланс темного
    static int Penalty(bool[,] d, int size)
    {
        int pen = 0;
        for (int pass = 0; pass < 2; pass++)
            for (int a = 0; a < size; a++)
            {
                int run = 1;
                for (int b = 1; b < size; b++)
                {
                    bool cur = pass == 0 ? d[a, b] : d[b, a], prev = pass == 0 ? d[a, b - 1] : d[b - 1, a];
                    if (cur == prev) { run++; if (run == 5) pen += 3; else if (run > 5) pen++; }
                    else run = 1;
                }
                // 1:1:3:1:1 з чотирма світлими з одного боку
                for (int b = 0; b + 10 < size; b++)
                {
                    bool m1 = true, m2 = true;
                    for (int k = 0; k < 11; k++)
                    {
                        bool v = pass == 0 ? d[a, b + k] : d[b + k, a];
                        bool p1 = k == 0 || k == 2 || k == 3 || k == 4 || k == 6;        // 1011101 0000
                        bool p2 = k == 4 || k == 6 || k == 7 || k == 8 || k == 10;       // 0000 1011101
                        if (v != p1) m1 = false;
                        if (v != p2) m2 = false;
                    }
                    if (m1) pen += 40;
                    if (m2) pen += 40;
                }
            }
        for (int y = 0; y + 1 < size; y++)
            for (int x = 0; x + 1 < size; x++)
            {
                bool c = d[y, x];
                if (c == d[y, x + 1] && c == d[y + 1, x] && c == d[y + 1, x + 1]) pen += 3;
            }
        int darkN = 0;
        foreach (bool v in d) if (v) darkN++;
        int total = size * size;
        int k2 = (Math.Abs(darkN * 20 - total * 10) + total - 1) / total - 1;
        pen += Math.Max(0, k2) * 10;
        return pen;
    }
}
