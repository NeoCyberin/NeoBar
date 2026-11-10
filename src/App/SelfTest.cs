using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// ======================================================================
//  «NeoBar.exe --selftest <файл звіту>» - самоперевірка без вікон для збірки на GitHub (Windows):
//  відомі тестові вектори PBKDF2 і хешів (системна CNG і запасний шлях на C# мусять збігатися),
//  шифрування тексту туди й назад, надійний запис файлів, розбір запиту пошуку.
//  Код виходу 0 - усе гаразд, 1 - щось не так (подробиці у звіті).
// ======================================================================
static class SelfTest
{
    static StringBuilder log;
    static int failed;

    static void Check(string name, bool ok, string detail)
    {
        if (!ok) failed++;
        log.AppendLine((ok ? "ok    " : "FAIL  ") + name + (ok || string.IsNullOrEmpty(detail) ? "" : "  -> " + detail));
    }

    static void Eq(string name, string want, string got) { Check(name, string.Equals(want, got, StringComparison.OrdinalIgnoreCase), "очікувалось " + want + ", отримано " + got); }

    static string Hex(byte[] b)
    {
        StringBuilder s = new StringBuilder();
        foreach (byte x in b) s.Append(x.ToString("x2"));
        return s.ToString();
    }

    static void Run(string name, Action a)
    {
        try { a(); }
        catch (Exception ex) { Check(name, false, ex.GetType().Name + ": " + ex.Message); }
    }

    public static int Main(string report)
    {
        log = new StringBuilder();
        failed = 0;
        byte[] pw = Encoding.ASCII.GetBytes("password"), salt = Encoding.ASCII.GetBytes("salt");

        // RFC 7914 / RFC 6070
        Run("pbkdf2-sha256", delegate
        {
            Eq("PBKDF2-HMAC-SHA256 c=1 (CNG)", "120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b", Hex(SbCrypt.Pbkdf2("SHA256", pw, salt, 1, 32)));
            Eq("PBKDF2-HMAC-SHA256 c=2 (CNG)", "ae4d0c95af6b46d32d0adff928f06dd02a303f8ef3c251dfd6e2d85a95474c43", Hex(SbCrypt.Pbkdf2("SHA256", pw, salt, 2, 32)));
            using (System.Security.Cryptography.HMACSHA256 h = new System.Security.Cryptography.HMACSHA256(pw))
                Eq("PBKDF2-HMAC-SHA256 c=2 (C#)", "ae4d0c95af6b46d32d0adff928f06dd02a303f8ef3c251dfd6e2d85a95474c43", Hex(SbCrypt.Pbkdf2(h, salt, 2, 32)));
        });
        Run("pbkdf2-sha1", delegate
        {
            Eq("PBKDF2-HMAC-SHA1 c=1 (CNG)", "0c60c80f961f0e71f3a9b524af6012062fe037a6", Hex(SbCrypt.Pbkdf2("SHA1", pw, salt, 1, 20)));
            Eq("PBKDF2-HMAC-SHA1 c=4096 (CNG)", "4b007901b765489abead49d926f721d065a429c1", Hex(SbCrypt.Pbkdf2("SHA1", pw, salt, 4096, 20)));
        });

        string dir = Path.Combine(Path.GetTempPath(), "neobar-selftest-" + Environment.TickCount);
        Directory.CreateDirectory(dir);
        try
        {
            Run("hashes", delegate
            {
                string f = Path.Combine(dir, "abc.txt");
                File.WriteAllBytes(f, Encoding.ASCII.GetBytes("abc"));
                string[] h = Hashes.OfFile(f, null, null);
                Eq("MD5(abc)", "900150983cd24fb0d6963f7d28e17f72", h[0]);
                Eq("SHA-1(abc)", "a9993e364706816aba3e25717850c26c9cd0d89d", h[1]);
                Eq("SHA-256(abc)", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", h[2]);
                Eq("SHA-512(abc)", "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f", h[3]);
                Eq("CRC32(abc)", "352441c2", h[4]);
                // великий файл: кілька шматків по 1 МБ, паралельне читання й обчислення
                string big = Path.Combine(dir, "big.bin");
                byte[] data = new byte[(3 << 20) + 12345];
                for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 31 + 7);
                File.WriteAllBytes(big, data);
                using (System.Security.Cryptography.SHA256 s = new System.Security.Cryptography.SHA256Managed())
                    Eq("SHA-256 (3 МБ, шматками)", Hex(s.ComputeHash(data)), Hashes.OfFile(big, null, null)[2]);
            });
            Run("crypt", delegate
            {
                string enc = SbCrypt.EncryptText("Привіт, NeoBar! ✓", "пароль-123");
                string err;
                Eq("шифрування тексту туди й назад", "Привіт, NeoBar! ✓", SbCrypt.DecryptText(enc, "пароль-123", out err));
                string bad = SbCrypt.DecryptText(enc, "інший", out err);
                Check("неправильний пароль не розшифровує", bad == null, bad);
            });
            Run("safefile", delegate
            {
                string f = Path.Combine(dir, "data.ini");
                SafeFile.Write(f, "a=1\r\n", new UTF8Encoding(true));
                SafeFile.Write(f, "a=2\r\n", new UTF8Encoding(true));
                Eq("надійний запис: нова версія", "a=2", SafeFile.ReadLines(f)[0]);
                Eq("надійний запис: попередня в .bak", "a=1", File.ReadAllLines(f + ".bak", Encoding.UTF8)[0]);
                File.WriteAllBytes(f, new byte[0]);            // обірваний запис старою версією
                Eq("порожній файл - читається .bak", "a=1", SafeFile.ReadLines(f)[0]);
                Check("тимчасового файлу не лишилось", !File.Exists(f + ".tmp"), "");
            });
            Run("search", delegate
            {
                FindSpec s = FindSpec.Parse("звіт ext:pdf", 0);
                Check("пошук: збіг назви й розширення", s.NameOk("Річний звіт.pdf", false), "");
                Check("пошук: інше розширення відкидається", !s.NameOk("звіт.docx", false), "");
            });
            Run("calc", delegate
            {
                string r;
                Check("калькулятор: 2+2*3", Calc.TryEval("2+2*3", out r) && r == "8", r);
                Check("калькулятор: (1+2)^2/3", Calc.TryEval("(1+2)^2/3", out r) && r == "3", r);
                Check("перетворення: 10 км у милі", Calc.TryEval("10 km in mi", out r) && (r.StartsWith("6.21") || r.StartsWith("6,21")), r);
                Check("не вираз - не рахуємо", !Calc.TryEval("notepad", out r), r);
            });
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception skipEx) { SbLog.Skip(skipEx); } }

        log.AppendLine(failed == 0 ? "УСЕ ГАРАЗД" : "ПОМИЛОК: " + failed);
        try { File.WriteAllText(report, log.ToString(), new UTF8Encoding(true)); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return failed == 0 ? 0 : 1;
    }
}
