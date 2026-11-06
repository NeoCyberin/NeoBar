using System;
using System.Text;

// ======================================================================
//  Власний створювач FAT32. Windows (format.exe, Format-Volume) відмовляється робити FAT32 на томах
//  понад 32 ГБ - це штучне обмеження засобів, а не формату: FAT32 підтримує томи до 2 ТБ. Тут
//  розмітка пишеться напряму за специфікацією Microsoft: завантажувальний сектор і його копія,
//  FSInfo, дві таблиці FAT, порожній кореневий каталог. Дані тому не чіпаються (швидке форматування),
//  а таблиці й початок тому обнуляються, щоб не лишилося решток старої файлової системи.
// ======================================================================
static class Fat32Fmt
{
    public delegate void WriteAt(long off, byte[] buf, int count);       // зсув і кількість кратні розміру сектора

    // найбільший том: кількість секторів має влізти в 32 біти
    public static long MaxBytes(int bps) { return (long)bps * 0xFFFFFFFFL; }

    // кластер за таблицею Microsoft для FAT32
    public static int DefaultCluster(long len)
    {
        long mb = len >> 20;
        if (mb <= 260) return 512;
        if (mb <= 8192) return 4096;
        if (mb <= 16384) return 8192;
        if (mb <= 32768) return 16384;
        return 32768;
    }

    public sealed class Plan
    {
        public int Bps, Spc, Rsv;
        public long Tot, FatSz, Clusters;
        public int ClusterBytes { get { return Bps * Spc; } }
    }

    // розмітка тому; null і опис помилки - том для FAT32 не підходить
    public static Plan MakePlan(long len, int bps, int clusterBytes, out string err)
    {
        err = "";
        if (bps < 512 || bps > 4096 || (bps & (bps - 1)) != 0) { err = L.T("Нестандартний розмір сектора: ") + bps + L.T(" байт."); return null; }
        long tot = len / bps;
        if (tot > 0xFFFFFFFFL) { err = L.T("Том завеликий для FAT32 (найбільше ~2 ТБ). Для великого тому підходить exFAT чи NTFS."); return null; }
        int cb = clusterBytes > 0 ? clusterBytes : DefaultCluster(len);
        if (cb < bps) cb = bps;
        for (;;)
        {
            int spc = cb / bps;
            if (spc < 1 || spc > 128) { err = L.T("Недопустимий розмір кластера."); return null; }
            long cl = (tot - 32) / spc;
            if (cl > 0x0FFFFFF0L)
            {
                if (cb >= 65536) { err = L.T("Том завеликий для FAT32."); return null; }
                cb *= 2; continue;                                             // забагато кластерів - беремо більший
            }
            if (cl < 65525)
            {
                if (cb <= bps) { err = L.T("Том замалий для FAT32 (потрібно щонайменше ~33 МБ)."); return null; }
                cb /= 2; continue;                                             // замало кластерів (це вже FAT16) - беремо менший
            }
            Plan p = new Plan();
            p.Bps = bps; p.Spc = spc;
            long fat = ((cl + 2) * 4 + bps - 1) / bps;
            int rsv = 32;
            long start = rsv + 2 * fat;
            rsv += (int)((spc - start % spc) % spc);                           // початок даних - на межі кластера (вирівнювання під флеш)
            p.Rsv = rsv; p.Tot = tot; p.FatSz = fat;
            p.Clusters = (tot - rsv - 2 * fat) / spc;
            if (p.Clusters < 65525) { if (cb <= bps) { err = L.T("Том замалий для FAT32."); return null; } cb /= 2; continue; }
            return p;
        }
    }

    static void U16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    static void U32(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
    static void Put(byte[] b, int o, string s) { for (int i = 0; i < s.Length; i++) b[o + i] = (byte)s[i]; }

    // мітка FAT: до 11 символів, верхній регістр, у кодовій сторінці ОЕМ; те, чого в ній немає, замінюємо на «_»
    public static string LabelOf(string s)
    {
        StringBuilder b = new StringBuilder();
        foreach (char c in (s ?? "").ToUpperInvariant())
        {
            if (b.Length >= 11) break;
            if (c >= ' ' && c < 127 && "\"*+,./:;<=>?[\\]|".IndexOf(c) < 0) b.Append(c);
            else if (c > 127) b.Append('_');
        }
        return b.ToString().TrimEnd();
    }

    static byte[] BootSector(Plan p, long hidden, uint volId, string label)
    {
        byte[] b = new byte[p.Bps];
        b[0] = 0xEB; b[1] = 0x58; b[2] = 0x90;
        Put(b, 3, "MSDOS5.0");
        U16(b, 11, p.Bps); b[13] = (byte)p.Spc; U16(b, 14, p.Rsv); b[16] = 2;
        b[21] = 0xF8;
        U16(b, 24, 63); U16(b, 26, 255);
        U32(b, 28, (uint)Math.Min(hidden, 0xFFFFFFFFL)); U32(b, 32, (uint)p.Tot); U32(b, 36, (uint)p.FatSz);
        U32(b, 44, 2); U16(b, 48, 1); U16(b, 50, 6);
        b[64] = 0x80; b[66] = 0x29; U32(b, 67, volId);
        Put(b, 71, (label.Length > 0 ? label : "NO NAME").PadRight(11));
        Put(b, 82, "FAT32   ");
        // код: вивести «Not a bootable disk» і зависнути - том даних, а не системний
        byte[] code = { 0xFA, 0x31, 0xC0, 0x8E, 0xD0, 0xBC, 0x00, 0x7C, 0xFB, 0x8E, 0xD8,        // cli; xor ax,ax; mov ss,ax; mov sp,7C00h; sti; mov ds,ax
                        0xBE, 0x78, 0x7C,                                                         // mov si, повідомлення (7C00h+90+30)
                        0xAC, 0x08, 0xC0, 0x74, 0x09, 0xB4, 0x0E, 0xBB, 0x07, 0x00, 0xCD, 0x10, 0xEB, 0xF2,      // lodsb; or al,al; jz кінець; mov ah,0Eh; mov bx,7; int 10h; jmp назад
                        0xEB, 0xFE };                                                             // кінець: jmp $
        Array.Copy(code, 0, b, 90, code.Length);
        Put(b, 90 + code.Length, "Not a bootable disk");
        b[510] = 0x55; b[511] = 0xAA;
        return b;
    }

    static byte[] FsInfo(Plan p)
    {
        byte[] b = new byte[p.Bps];
        U32(b, 0, 0x41615252); U32(b, 484, 0x61417272);
        U32(b, 488, (uint)(p.Clusters - 1)); U32(b, 492, 3);              // вільних: усі, крім кореневого каталогу; шукати з кластера 3
        U32(b, 508, 0xAA550000);
        return b;
    }

    static void Zero(WriteAt wr, long from, long to, byte[] zero)
    {
        for (long pos = from; pos < to; pos += zero.Length)
            wr(pos, zero, (int)Math.Min(zero.Length, to - pos));
    }

    // записує нову файлову систему; label - вже очищена мітка (LabelOf). hidden - початок розділу в секторах
    public static void Format(WriteAt wr, Plan p, long hidden, string label, Action<string> log)
    {
        byte[] zero = new byte[1 << 20];
        long bps = p.Bps, fat0 = p.Rsv * bps, fat1 = fat0 + p.FatSz * bps, dataAt = fat1 + p.FatSz * bps, clBytes = p.ClusterBytes;
        log(L.T("Обнулення службових областей…"));
        Zero(wr, 0, fat0, zero);                                          // зарезервована область (старі завантажувальні сектори)
        Zero(wr, fat0, dataAt, zero);                                     // обидві FAT
        Zero(wr, dataAt, dataAt + clBytes, zero);                         // кореневий каталог (кластер 2)

        byte[] f = new byte[p.Bps];
        U32(f, 0, 0x0FFFFFF8); U32(f, 4, 0x0FFFFFFF); U32(f, 8, 0x0FFFFFFF);          // медіа, кінець, кореневий каталог - один кластер
        wr(fat0, f, p.Bps); wr(fat1, f, p.Bps);

        if (label.Length > 0)                                             // запис мітки тому в корені
        {
            byte[] d = new byte[p.Bps];
            Put(d, 0, label.PadRight(11));
            d[11] = 0x08;
            DateTime n = DateTime.Now;
            int t = (n.Hour << 11) | (n.Minute << 5) | (n.Second / 2), dt = ((Math.Max(1980, n.Year) - 1980) << 9) | (n.Month << 5) | n.Day;
            U16(d, 22, t); U16(d, 24, dt);
            wr(dataAt, d, p.Bps);
        }

        byte[] info = FsInfo(p);
        byte[] ext = new byte[p.Bps]; ext[510] = 0x55; ext[511] = 0xAA;
        wr(1 * bps, info, p.Bps); wr(2 * bps, ext, p.Bps);
        wr(7 * bps, info, p.Bps); wr(8 * bps, ext, p.Bps);
        uint id = (uint)(DateTime.Now.Ticks ^ (DateTime.Now.Ticks >> 32) ^ Environment.TickCount);
        byte[] boot = BootSector(p, hidden, id, label);
        wr(6 * bps, boot, p.Bps);                                         // копія
        wr(0, boot, p.Bps);                                               // останнім: до цього моменту том ще не виглядає чинним
        log(L.T("Записано FAT32: кластер ") + (p.ClusterBytes >= 1024 ? p.ClusterBytes / 1024 + L.T(" КБ") : p.ClusterBytes + L.T(" Б")) + ", " + p.Clusters + L.T(" кластерів."));
    }
}
