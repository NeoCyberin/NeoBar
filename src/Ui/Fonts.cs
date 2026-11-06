using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

// ======================================================================
//  Власні шрифти NeoBar (кирилиця + латиниця): по одному на тему.
//  Намальовані для проєкту; лежать в exe одним стисненим ресурсом sbfonts.bin
//  (лише звичайне накреслення: Windows надійно підхоплює не більше десятка приватних
//  файлів шрифтів, а жирний і курсив RichEdit/GDI синтезують самі).
//
//  RichEdit малює текст через GDI, тому шрифт має бути видимий саме GDI:
//   1) AddFontMemResourceEx (з пам'яті, лише для цього процесу);
//   2) якщо GDI його так і не бачить - пишемо файли в %TEMP%\NeoBarFonts
//      і підключаємо AddFontResourceEx з FR_PRIVATE (теж лише для процесу).
//  Для GDI+ (Font/FontFamily) додатково тримаємо PrivateFontCollection.
//  Результат перевірки пишеться в neobar-fonts.log поряд з notes.ini.
//  Шрифти в систему НЕ встановлюються.
// ======================================================================

static class SBFonts
{
    public static readonly string[] Names =
    {
        "SB Dark", "SB Light", "SB Glass", "SB Cyber", "SB Steam", "SB Urban", "SB Pink", "SB Nature", "SB Art"
    };

    // id теми -> шрифт
    public static string ForTheme(string id)
    {
        switch (id)
        {
            case "light": return "SB Light";
            case "glass": return "SB Glass";
            case "cyber": return "SB Cyber";
            case "steam": return "SB Steam";
            case "urban": return "SB Urban";
            case "pink": return "SB Pink";
            case "nature": return "SB Nature";
            case "art": return "SB Art";
            // святкові теми - найближчі за характером з наявних (більше файлів шрифтів Windows підхоплює ненадійно)
            case "halloween": return "SB Art";
            case "xmas": return "SB Nature";
            case "easter": return "SB Light";
            case "birthday": return "SB Pink";
            default: return "SB Dark";
        }
    }

    [DllImport("gdi32.dll")]
    static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, ref uint pcFonts);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    static extern int AddFontResourceExW(string file, uint fl, IntPtr res);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    static extern int GetTextFaceW(IntPtr hdc, int count, StringBuilder name);

    static PrivateFontCollection pfc;
    static readonly Dictionary<string, FontFamily> fam = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, bool> gdiOk = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    static readonly List<string[]> files = new List<string[]>();      // {ім'я, шлях у temp}
    static readonly List<byte[]> blobs = new List<byte[]>();
    static readonly StringBuilder log = new StringBuilder();
    static bool inited;
    public static bool Ready { get { return inited; } }       // підключення шрифтів уже виконано (з будь-яким результатом)
    public static int Loaded;       // скільки файлів шрифтів зареєстровано

    static bool IsWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }

    // чи бачить GDI шрифт із такою назвою для латиниці (0) і кирилиці (204)
    static bool GdiSees(string face)
    {
        try
        {
            IntPtr dc = Win.CreateCompatibleDC(IntPtr.Zero);
            bool ok = true;
            foreach (uint cs in new uint[] { 0, 204 })
            {
                IntPtr hf = Win.CreateFontW(-16, 0, 0, 0, 400, 0, 0, 0, cs, 0, 0, 4, 0, face);
                IntPtr old = Win.SelectObject(dc, hf);
                StringBuilder sb = new StringBuilder(128);
                GetTextFaceW(dc, 128, sb);
                Win.SelectObject(dc, old);
                Win.DeleteObject(hf);
                if (!string.Equals(sb.ToString(), face, StringComparison.OrdinalIgnoreCase)) { ok = false; log.AppendLine("  GDI: '" + face + "' charset " + cs + " -> '" + sb + "'"); }
            }
            Win.DeleteDC(dc);
            return ok;
        }
        catch (Exception e) { log.AppendLine("  GDI-перевірка не вдалась: " + e.Message); return false; }
    }

    static int CountOk()
    {
        int ok = 0;
        foreach (string n in Names) { bool g = GdiSees(n); gdiOk[n] = g; if (g) ok++; }
        return ok;
    }

    public static void Init(string logDir)
    {
        if (inited) return;
        inited = true;
        try
        {
            Stream rs = Assembly.GetExecutingAssembly().GetManifestResourceStream("sbfonts.bin");
            if (rs == null) { log.AppendLine("Ресурс sbfonts.bin не знайдено в exe (збирайте через build.bat)."); return; }
            pfc = new PrivateFontCollection();
            List<IntPtr> ptrs = new List<IntPtr>();
            using (GZipStream gz = new GZipStream(rs, CompressionMode.Decompress))
            using (BinaryReader br = new BinaryReader(gz))
            {
                int count = br.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    string name = br.ReadString();
                    int len = br.ReadInt32();
                    byte[] data = br.ReadBytes(len);
                    blobs.Add(data);
                    files.Add(new string[] { name, "" });
                    IntPtr p = Marshal.AllocCoTaskMem(len);        // пам'ять має жити до кінця процесу
                    Marshal.Copy(data, 0, p, len);
                    ptrs.Add(p);
                    try { pfc.AddMemoryFont(p, len); Loaded++; } catch (Exception e) { log.AppendLine("PFC " + name + ": " + e.Message); }
                }
            }
            log.AppendLine("Шрифтів у ресурсі: " + blobs.Count + "; у PrivateFontCollection: " + Loaded);

            if (IsWindows)
            {
                // 1) з пам'яті
                for (int i = 0; i < blobs.Count; i++)
                {
                    try
                    {
                        uint n = 0;
                        IntPtr h = AddFontMemResourceEx(ptrs[i], (uint)blobs[i].Length, IntPtr.Zero, ref n);
                        if (h == IntPtr.Zero) log.AppendLine("AddFontMemResourceEx " + files[i][0] + ": помилка " + Marshal.GetLastWin32Error());
                    }
                    catch (Exception e) { log.AppendLine("AddFontMemResourceEx: " + e.Message); break; }
                }
                int ok = CountOk();
                log.AppendLine("GDI бачить після AddFontMemResourceEx: " + ok + " з " + Names.Length);

                // 2) запасний шлях: файли в temp + AddFontResourceEx(FR_PRIVATE)
                if (ok < Names.Length)
                {
                    try
                    {
                        string dir = Path.Combine(Path.GetTempPath(), "NeoBarFonts");
                        Directory.CreateDirectory(dir);
                        for (int i = 0; i < blobs.Count; i++)
                        {
                            string fp = Path.Combine(dir, files[i][0] + ".ttf");
                            try { File.WriteAllBytes(fp, blobs[i]); } catch { }
                            int r = AddFontResourceExW(fp, 0x10, IntPtr.Zero);
                            if (r == 0) log.AppendLine("AddFontResourceEx " + files[i][0] + ": 0 шрифтів");
                        }
                        ok = CountOk();
                        log.AppendLine("GDI бачить після AddFontResourceEx: " + ok + " з " + Names.Length);
                    }
                    catch (Exception e) { log.AppendLine("Запасний шлях: " + e.Message); }
                }
            }
            else
            {
                foreach (string n in Names) gdiOk[n] = true;       // не Windows: шрифти бере fontconfig за назвою
            }

            foreach (FontFamily f in pfc.Families)
            {
                // перевірка: шрифт із колекції справді придатний (на деяких системах GDI+ не вміє приватні шрифти)
                try { using (Font probe = new Font(f, 12f, FontStyle.Regular, GraphicsUnit.Point)) { int h = probe.Height; } fam[f.Name] = f; }
                catch (Exception e) { log.AppendLine("GDI+ шрифт " + f.Name + " непридатний: " + e.Message); }
            }
            log.AppendLine("Придатних для GDI+: " + fam.Count);
            foreach (string nm in Names)
                log.AppendLine("  " + nm + ": GDI=" + (gdiOk.ContainsKey(nm) && gdiOk[nm] ? "так" : "НІ") + ", GDI+=" + (fam.ContainsKey(nm) ? "так" : "НІ"));
        }
        catch (Exception e) { log.AppendLine("Помилка: " + e); }
        finally
        {
            // перевірка шрифтів - у журнал «Шрифти» (якщо ввімкнено)
            SbLog.W(SbLog.Fonts, log.ToString());
        }
    }

    // Чи можна користуватися власним шрифтом: GDI (RichEdit) має його бачити, інакше текст лишиться стандартним
    public static bool GdiVisible(string name)
    {
        bool ok;
        return name != null && gdiOk.TryGetValue(name.Trim(), out ok) && ok;
    }

    public static FontFamily Family(string name)
    {
        FontFamily f;
        if (name == null || !fam.TryGetValue(name.Trim(), out f)) return null;
        return GdiVisible(name) ? f : null;
    }

    public static bool IsOwn(string name)
    {
        if (name == null) return false;
        foreach (string n in Names) if (string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
