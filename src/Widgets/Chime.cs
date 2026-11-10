using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// ----------------------------------------------------------------------
//  Звуки сигналу: кілька вбудованих мелодій (синтезуються, без файлів) і власні файли
//  (wav, mp3, wma - у папці sounds поряд з програмою)
// ----------------------------------------------------------------------
static class Chime
{
    static System.Media.SoundPlayer player;
    static MemoryStream wav;
    static bool mciOpen;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern int mciSendString(string cmd, StringBuilder ret, int len, IntPtr cb);

    // доступні для вибору; тематичні мелодії (кіберпанк, стімпанк тощо) вмикаються лише через «За темою панелі»
    public static readonly string[][] BuiltIn =
    {
        new[] { "theme", L.T("За темою панелі") },
        new[] { "chime", L.T("Дзвіночок") }, new[] { "marimba", L.T("Маримба") }, new[] { "harp", L.T("Арфа") },
        new[] { "bells", L.T("Дзвони") }, new[] { "rise", L.T("Світанок") }, new[] { "8bit", L.T("8-біт") }
    };

    // порожній вибір - «За темою панелі»
    public static string Normalize(string id)
    {
        return string.IsNullOrEmpty(id) ? "theme" : id;
    }

    // мелодія теми панелі
    public static string ForTheme(string themeId)
    {
        switch (themeId)
        {
            case "dark": return "night";
            case "light": return "day";
            case "glass": return "glass";
            case "cyber": return "cyber";
            case "steam": return "steam";
            case "urban": return "electro";
            case "pink": return "musicbox";
            case "nature": return "birds";
            case "art": return "piano";
            case "halloween": return "spooky";
            case "xmas": return "jingle";
            case "easter": return "spring";
            case "birthday": return "bday";
        }
        return "chime";
    }

    public static string ThemeId = "dark";

    public static string Dir
    {
        get
        {
            string d = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sounds");
            try { Directory.CreateDirectory(d); return d; }
            catch
            {
                d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeoBar", "sounds");
                try { Directory.CreateDirectory(d); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                return d;
            }
        }
    }

    public static List<string> Files()
    {
        List<string> l = new List<string>();
        try
        {
            foreach (string f in Directory.GetFiles(Dir))
            {
                string e = Path.GetExtension(f).ToLowerInvariant();
                if (e == ".wav" || e == ".mp3" || e == ".wma" || e == ".m4a" || e == ".aac") l.Add(Path.GetFileName(f));
            }
            l.Sort(StringComparer.CurrentCultureIgnoreCase);
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return l;
    }

    public static string Name(string id)
    {
        id = Normalize(id);
        if (id.StartsWith("file:")) return Path.GetFileNameWithoutExtension(id.Substring(5));
        foreach (string[] b in BuiltIn) if (b[0] == id) return b[1];
        return L.T("За темою панелі");
    }

    // копіює вибраний файл до папки sounds; повертає id звуку
    public static string Import(string src)
    {
        string name = Path.GetFileName(src);
        string dst = Path.Combine(Dir, name);
        if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase)) File.Copy(src, dst, true);
        return "file:" + name;
    }

    static void Tone(double[] buf, int rate, double start, double freq, double decay, double attack, double[] partials, double gain)
    {
        int i0 = (int)(start * rate);
        for (int i = Math.Max(0, i0); i < buf.Length; i++)
        {
            double dt = (double)(i - i0) / rate;
            double env = Math.Exp(-dt * decay) * Math.Min(1.0, dt / attack);
            if (env < 0.0005 && dt > attack) break;
            double v = 0;
            for (int k = 0; k < partials.Length; k += 2) v += partials[k + 1] * Math.Sin(2 * Math.PI * freq * partials[k] * dt);
            buf[i] += env * v * gain;
        }
    }

    static double[] Pattern(string id, int rate)
    {
        double[] warm = { 1, 1, 2, 0.3, 3, 0.1 };
        double[] pure = { 1, 1, 2, 0.12 };
        double[] bell = { 1, 1, 2, 0.6, 2.76, 0.45, 5.4, 0.25 };
        double[] sq = { 1, 1, 3, 0.33, 5, 0.2, 7, 0.14 };
        double[] saw = { 1, 1, 2, 0.5, 3, 0.33, 4, 0.25, 5, 0.2 };
        double[] piano = { 1, 1, 2, 0.45, 3, 0.22, 4, 0.12, 5, 0.06 };
        double[] buf;
        switch (id)
        {
            case "marimba":
            {
                buf = new double[(int)(rate * 1.4)];
                double[] f = { 523.25, 659.25, 783.99, 1046.5 };
                for (int i = 0; i < 4; i++) Tone(buf, rate, i * 0.15, f[i], 8, 0.004, new double[] { 1, 1, 4, 0.25 }, 0.9);
                break;
            }
            case "harp":
            {
                buf = new double[(int)(rate * 1.6)];
                double[] f = { 440, 554.37, 659.25, 880, 1108.7, 1318.5 };
                for (int i = 0; i < 6; i++) Tone(buf, rate, i * 0.07, f[i], 3, 0.003, warm, 0.55);
                break;
            }
            case "bells":
                buf = new double[(int)(rate * 2.4)];
                Tone(buf, rate, 0, 523.25, 2.2, 0.002, bell, 0.6);
                Tone(buf, rate, 0.6, 392, 2.2, 0.002, bell, 0.6);
                break;
            case "rise":
            {
                buf = new double[(int)(rate * 1.7)];
                double[] f = { 392, 493.88, 587.33, 783.99, 587.33, 493.88 };
                for (int i = 0; i < 6; i++) Tone(buf, rate, i * 0.22, f[i], 3.5, 0.03, pure, 0.8);
                break;
            }
            case "night":
            {
                // темна тема: низькі м'які тони з довгим згасанням
                buf = new double[(int)(rate * 2.8)];
                double[] f = { 220, 261.63, 329.63, 440, 329.63 };
                for (int i = 0; i < f.Length; i++) Tone(buf, rate, i * 0.34, f[i], 1.5, 0.02, new double[] { 1, 1, 2, 0.22, 3, 0.06 }, 0.6);
                Tone(buf, rate, 0, 110, 0.9, 0.05, pure, 0.5);
                break;
            }
            case "day":
            {
                // світла тема: яскравий глокеншпіль
                buf = new double[(int)(rate * 1.8)];
                double[] f = { 1046.5, 1318.5, 1568, 2093, 1568, 1318.5, 2093 };
                for (int i = 0; i < f.Length; i++) Tone(buf, rate, i * 0.11, f[i], 5, 0.002, new double[] { 1, 1, 2.76, 0.3, 5.4, 0.12 }, 0.5);
                break;
            }
            case "8bit":
            {
                // чіптюн: прямокутна хвиля з короткими нотами
                buf = new double[(int)(rate * 1.4)];
                double[] f = { 523.25, 659.25, 783.99, 1046.5, 783.99, 659.25, 880, 1046.5, 1318.5, 1046.5 };
                for (int i = 0; i < f.Length; i++) Tone(buf, rate, i * 0.1, f[i], 0.5, 0.002, new double[] { 1, 1, 3, 0.33, 5, 0.2, 7, 0.14 }, 0.4, 0.085);
                break;
            }
            case "digital":
                buf = new double[(int)(rate * 1.0)];
                for (int i = 0; i < 4; i++) Tone(buf, rate, i * 0.15, 2048, 0.5, 0.002, sq, 0.45, 0.08);
                break;
            case "cyber":
            {
                buf = new double[(int)(rate * 1.6)];
                double[] f = { 440, 523.25, 659.25, 880, 659.25, 523.25, 440, 329.63 };
                for (int i = 0; i < 8; i++) Tone(buf, rate, i * 0.12, f[i], 9, 0.003, saw, 0.4);
                Tone(buf, rate, 0, 110, 1.5, 0.01, saw, 0.25);
                break;
            }
            case "steam":
                // молоточок б'є по дзвонику старого будильника
                buf = new double[(int)(rate * 1.6)];
                for (int i = 0; i < 16; i++) Tone(buf, rate, i * 0.055, 1250, 14, 0.001, bell, 0.35);
                break;
            case "birds":
                buf = new double[(int)(rate * 2.0)];
                Chirp(buf, rate, 0.00, 3200, 4600, 0.09, 0.5);
                Chirp(buf, rate, 0.14, 3400, 4900, 0.08, 0.5);
                Chirp(buf, rate, 0.26, 4800, 3600, 0.12, 0.45);
                Chirp(buf, rate, 0.80, 2800, 4200, 0.06, 0.4);
                Chirp(buf, rate, 0.90, 2900, 4300, 0.06, 0.4);
                Chirp(buf, rate, 1.00, 3000, 4400, 0.06, 0.4);
                Chirp(buf, rate, 1.18, 4200, 3000, 0.15, 0.45);
                break;
            case "musicbox":
            {
                buf = new double[(int)(rate * 2.2)];
                double[] f = { 1046.5, 1318.5, 1568, 1318.5, 1760, 1568, 1318.5, 1174.7 };
                for (int i = 0; i < 8; i++) Tone(buf, rate, i * 0.22, f[i], 4.5, 0.002, new double[] { 1, 1, 3.01, 0.15, 5.2, 0.05 }, 0.55);
                break;
            }
            case "electro":
                buf = new double[(int)(rate * 1.2)];
                for (int i = 0; i < 6; i++) Tone(buf, rate, i * 0.1, i % 2 == 0 ? 880 : 1108.7, 0.5, 0.004, sq, 0.35, 0.07);
                break;
            case "piano":
            {
                buf = new double[(int)(rate * 2.4)];
                double[] f = { 523.25, 587.33, 659.25, 783.99, 659.25, 587.33, 523.25 };
                double[] t = { 0, 0.25, 0.5, 0.75, 1.15, 1.4, 1.65 };
                for (int i = 0; i < f.Length; i++) Tone(buf, rate, t[i], f[i], 2.5, 0.004, piano, 0.6);
                Tone(buf, rate, 0, 261.63, 1.2, 0.006, piano, 0.3);
                Tone(buf, rate, 1.15, 196, 1.2, 0.006, piano, 0.3);
                break;
            }
            case "glass":
                buf = new double[(int)(rate * 2.6)];
                Tone(buf, rate, 0, 1567.98, 1.6, 0.01, new double[] { 1, 1, 2.7, 0.35, 5.1, 0.12 }, 0.5);
                Tone(buf, rate, 0.45, 2093, 1.6, 0.01, new double[] { 1, 1, 2.7, 0.35, 5.1, 0.12 }, 0.4);
                Tone(buf, rate, 0.9, 1318.5, 1.6, 0.01, new double[] { 1, 1, 2.7, 0.35, 5.1, 0.12 }, 0.45);
                break;
            case "spooky":
            {
                // Хелловін: «терменвокс» з вібрато над низьким органним тритоном
                buf = new double[(int)(rate * 3.2)];
                double[] organ = { 1, 1, 2, 0.5, 3, 0.25, 4, 0.18, 6, 0.08 };
                Tone(buf, rate, 0, 146.83, 0.9, 0.12, organ, 0.32);       // ре
                Tone(buf, rate, 0, 207.65, 0.9, 0.12, organ, 0.26);       // соль-дієз - тритон
                Glide(buf, rate, 0.15, 0.9, 392, 587.33, 0.5);
                Glide(buf, rate, 1.05, 0.8, 587.33, 554.37, 0.5);
                Glide(buf, rate, 1.85, 1.2, 554.37, 293.66, 0.5);
                Tone(buf, rate, 1.85, 138.59, 1.1, 0.1, organ, 0.28);
                break;
            }
            case "jingle":
            {
                // Різдво: «Jingle Bells» (народна мелодія, 1857) дзвіночками і бубонці на кожну долю
                buf = new double[(int)(rate * 3.4)];
                double q = 0.2;
                double[] f = { 659.25, 659.25, 659.25, 659.25, 659.25, 659.25, 659.25, 783.99, 523.25, 587.33, 659.25 };
                double[] at = { 0, 1, 2, 4, 5, 6, 8, 9, 10, 11.5, 12 };
                for (int i = 0; i < f.Length; i++) Tone(buf, rate, at[i] * q, f[i], i == f.Length - 1 ? 1.8 : 4, 0.003, bell, 0.5);
                double[] bass = { 261.63, 196, 261.63, 196, 261.63, 196, 261.63 };
                for (int i = 0; i < bass.Length; i++) Tone(buf, rate, i * 2 * q, bass[i], 4, 0.004, warm, 0.22);
                for (int i = 0; i < 14; i++) Jingle(buf, rate, i * q, 0.16);
                break;
            }
            case "spring":
            {
                // Великдень: легка флейта по пентатоніці й пташка у відповідь
                buf = new double[(int)(rate * 2.8)];
                double[] flute = { 1, 1, 2, 0.18, 3, 0.05 };
                double[] f = { 783.99, 880, 1046.5, 1174.7, 1046.5, 880, 1046.5 };
                double[] at = { 0, 0.18, 0.36, 0.54, 0.9, 1.08, 1.26 };
                for (int i = 0; i < f.Length; i++) Tone(buf, rate, at[i], f[i], 2.6, 0.035, flute, 0.5);
                Chirp(buf, rate, 1.8, 3400, 4700, 0.08, 0.35);
                Chirp(buf, rate, 1.93, 3500, 4800, 0.08, 0.35);
                Chirp(buf, rate, 2.08, 4700, 3500, 0.12, 0.3);
                break;
            }
            // ---- звуки святкових ефектів на столі (по три варіанти на тему, обирається випадково) ----
            case "fx_hw1":
            {
                // Хелловін: грім, мінорний акорд органа, писк кажанів і дзвін
                buf = new double[(int)(rate * 4.2)];
                double[] organ = { 1, 1, 2, 0.55, 3, 0.35, 4, 0.2 };
                Rumble(buf, rate, 0, 1.8, 1.0);
                foreach (double f in new double[] { 110, 130.81, 164.81 }) Tone(buf, rate, 0.7, f, 1.1, 0.05, organ, 0.4);
                foreach (double st in new double[] { 1.9, 2.1, 2.4, 2.55 }) Chirp(buf, rate, st, 4200, 6400, 0.07, 0.25);
                Tone(buf, rate, 2.7, 196, 1.1, 0.002, bell, 0.5);
                Tone(buf, rate, 3.3, 185, 1.3, 0.002, bell, 0.4);
                break;
            }
            case "fx_hw2":
            {
                // Хелловін: привид - вібрато терменвокса, скрип дверей і шепіт
                buf = new double[(int)(rate * 4.0)];
                Glide(buf, rate, 0.1, 1.5, 330, 880, 0.35);
                Glide(buf, rate, 1.5, 1.6, 880, 415, 0.32);
                Glide(buf, rate, 0.6, 1.8, 200, 95, 0.2);
                Rumble(buf, rate, 2.9, 1.0, 0.5);
                Tone(buf, rate, 3.1, 98, 1.6, 0.01, new double[] { 1, 1, 2, 0.6, 3, 0.4 }, 0.4);
                break;
            }
            case "fx_hw3":
            {
                // Хелловін: регіт відьми й бульбашки в казані
                buf = new double[(int)(rate * 3.8)];
                double[] sq2 = { 1, 1, 3, 0.4, 5, 0.25, 7, 0.15 };
                double f0 = 760;
                for (int i = 0; i < 9; i++) { Tone(buf, rate, 0.15 + i * 0.105, f0 * (1 - i * 0.045), 3, 0.004, sq2, 0.4, 0.08); Tone(buf, rate, 0.2 + i * 0.105, f0 * 1.5 * (1 - i * 0.045), 3, 0.004, sq2, 0.12, 0.05); }
                Random r = new Random(5);
                for (int i = 0; i < 12; i++) Chirp(buf, rate, 1.4 + i * 0.17 + r.NextDouble() * 0.08, 180 + r.NextDouble() * 120, 420 + r.NextDouble() * 200, 0.08, 0.35);
                Tone(buf, rate, 3.3, 233.08, 1.4, 0.002, bell, 0.35);
                break;
            }
            case "fx_xm1":
            {
                // Різдво: біг саней - дзвіночки зі швидкою, потім повільнішою долею й арпеджіо
                buf = new double[(int)(rate * 3.6)];
                double at = 0;
                for (int i = 0; i < 16; i++) { Jingle(buf, rate, at, 1.3); at += i < 8 ? 0.11 : 0.15; }
                double[] f = { 523.25, 659.25, 783.99, 1046.5 };
                for (int i = 0; i < 4; i++) Tone(buf, rate, 1.9 + i * 0.12, f[i], 3, 0.003, bell, 0.45);
                Tone(buf, rate, 2.5, 1318.5, 1.8, 0.003, bell, 0.5);
                break;
            }
            case "fx_xm2":
            {
                // Різдво: куранти - падаюча пентатоніка й іскри
                buf = new double[(int)(rate * 4.2)];
                double[] f = { 1318.5, 1174.7, 987.77, 783.99, 659.25, 587.33, 523.25 };
                for (int i = 0; i < f.Length; i++) Tone(buf, rate, i * 0.28, f[i], 2.2, 0.002, bell, 0.45);
                Random r = new Random(9);
                for (int i = 0; i < 14; i++) Chirp(buf, rate, 0.3 + i * 0.2 + r.NextDouble() * 0.1, 6200, 8400, 0.05, 0.12);
                Tone(buf, rate, 2.2, 261.63, 1.0, 0.01, warm, 0.35);
                Tone(buf, rate, 2.2, 329.63, 1.0, 0.01, warm, 0.3);
                Tone(buf, rate, 2.2, 392, 1.0, 0.01, warm, 0.3);
                break;
            }
            case "fx_xm3":
            {
                // Різдво: вечірній передзвін і м'який хор
                buf = new double[(int)(rate * 4.4)];
                for (int i = 0; i < 4; i++) Tone(buf, rate, i * 0.9, i % 2 == 0 ? 392 : 293.66, 0.9, 0.002, bell, 0.55);
                double[] choir = { 1, 1, 2, 0.25, 3, 0.1 };
                foreach (double f in new double[] { 349.23, 440, 523.25 }) Tone(buf, rate, 0.4, f, 0.7, 0.7, choir, 0.28, 3.4);
                break;
            }
            case "fx_ea1":
            {
                // Великдень: ранок - щебет пташок і флейта
                buf = new double[(int)(rate * 3.8)];
                Random r = new Random(3);
                for (int i = 0; i < 16; i++)
                {
                    double st = i * 0.17 + r.NextDouble() * 0.12, a = 3000 + r.NextDouble() * 1800;
                    Chirp(buf, rate, st, a, a + (r.NextDouble() - 0.4) * 1800, 0.06 + r.NextDouble() * 0.07, 0.9);
                }
                double[] flute = { 1, 1, 2, 0.18, 3, 0.05 };
                double[] n = { 880, 1046.5, 1318.5, 1174.7, 1046.5 };
                for (int i = 0; i < n.Length; i++) Tone(buf, rate, 1.5 + i * 0.3, n[i], 3, 0.03, flute, 0.45, 0.28);
                break;
            }
            case "fx_ea2":
            {
                // Великдень: зозуля й трелі
                buf = new double[(int)(rate * 3.8)];
                double[] cu = { 1, 1, 2, 0.15 };
                for (int i = 0; i < 3; i++) { Tone(buf, rate, i * 0.7, 784, 3, 0.02, cu, 0.3, 0.22); Tone(buf, rate, i * 0.7 + 0.3, 622.25, 3, 0.02, cu, 0.3, 0.3); }
                for (int i = 0; i < 14; i++) Chirp(buf, rate, 2.3 + i * 0.065, 4200 + (i % 2) * 700, 4600 + (i % 2) * 300, 0.045, 0.8);
                Chirp(buf, rate, 3.3, 3500, 5000, 0.14, 0.8);
                break;
            }
            case "fx_ea3":
            {
                // Великдень: арфа вгору, дзвіночки й пташка
                buf = new double[(int)(rate * 3.8)];
                double[] f = { 392, 440, 523.25, 587.33, 659.25, 783.99, 880, 1046.5, 1174.7, 1318.5 };
                for (int i = 0; i < f.Length; i++) Tone(buf, rate, i * 0.09, f[i], 2.4, 0.003, warm, 0.45);
                foreach (double s0 in new double[] { 1.1, 1.4, 1.7 }) Tone(buf, rate, s0, 2093, 4, 0.002, bell, 0.25);
                Chirp(buf, rate, 2.1, 3400, 4900, 0.09, 0.7); Chirp(buf, rate, 2.25, 3500, 5000, 0.09, 0.7); Chirp(buf, rate, 2.42, 4900, 3600, 0.12, 0.7);
                break;
            }
            case "fx_bd2":
            {
                // День народження: свистульки-«язики», хлопавки й марімба
                buf = new double[(int)(rate * 3.6)];
                double[] honk = { 1, 1, 3, 0.5, 5, 0.3, 7, 0.2 };
                foreach (double st in new double[] { 0.1, 0.55, 1.0 }) { Glide(buf, rate, st, 0.32, 380, 900, 0.3); Tone(buf, rate, st + 0.3, 900, 8, 0.003, honk, 0.3, 0.1); }
                foreach (double pt in new double[] { 0.5, 0.95, 1.2, 1.35 }) Crash(buf, rate, pt, 0.14, 0.45);
                double[] mf = { 523.25, 659.25, 783.99, 1046.5, 783.99, 1046.5 };
                double[] ma = { 1.5, 1.68, 1.86, 2.04, 2.3, 2.5 };
                for (int i = 0; i < mf.Length; i++) Tone(buf, rate, ma[i], mf[i], 5, 0.004, new double[] { 1, 1, 4, 0.25 }, 0.5);
                Crash(buf, rate, 2.5, 1.0, 0.3);
                break;
            }
            case "fx_bd3":
            {
                // День народження: барабанний дріб, «та-дам!» тарілка
                buf = new double[(int)(rate * 3.4)];
                double[] brass = { 1, 1, 2, 0.8, 3, 0.55, 4, 0.35, 5, 0.22 };
                for (int i = 0; i < 26; i++) Crash(buf, rate, i * 0.048, 0.05, 2.0 + i * 0.1);
                foreach (double f in new double[] { 392, 523.25, 659.25, 783.99 }) Tone(buf, rate, 1.35, f, 1.3, 0.02, brass, 0.38, 1.2);
                Crash(buf, rate, 1.35, 1.4, 0.4);
                foreach (double pt in new double[] { 1.9, 2.2, 2.45 }) Crash(buf, rate, pt, 0.15, 0.45);
                break;
            }
            case "fx_bd1":
            {
                // привітання: тріумфальна фанфара труб, тарілка й хлопки салюту
                buf = new double[(int)(rate * 3.4)];
                double[] brass = { 1, 1, 2, 0.8, 3, 0.55, 4, 0.35, 5, 0.22, 6, 0.12 };
                double[] up = { 392, 523.25, 659.25 };
                for (int i = 0; i < up.Length; i++) Tone(buf, rate, i * 0.13, up[i], 6, 0.02, brass, 0.5, 0.12);
                double[] ch = { 523.25, 659.25, 783.99, 1046.5 };
                foreach (double f in ch) Tone(buf, rate, 0.45, f, 1.3, 0.03, brass, 0.36, 1.0);
                Crash(buf, rate, 0.45, 1.3, 0.4);
                double[] up2 = { 523.25, 659.25, 783.99 };
                for (int i = 0; i < up2.Length; i++) Tone(buf, rate, 1.7 + i * 0.13, up2[i], 6, 0.02, brass, 0.5, 0.12);
                foreach (double f in new double[] { 659.25, 783.99, 1046.5, 1318.5 }) Tone(buf, rate, 2.1, f, 1.6, 0.03, brass, 0.36, 1.2);
                Crash(buf, rate, 2.1, 1.3, 0.45);
                foreach (double pt in new double[] { 1.05, 1.4, 1.6 }) Crash(buf, rate, pt, 0.16, 0.5);
                break;
            }
            case "bday":
            {
                // День народження: «Happy Birthday to You» (мелодія 1893 р., суспільне надбання) музичною скринькою
                buf = new double[(int)(rate * 4.4)];
                double q = 0.3;
                double[] f = { 392, 392, 440, 392, 523.25, 493.88, 392, 392, 440, 392, 587.33, 523.25 };
                double[] at = { 0, 0.75, 1, 2, 3, 4, 6, 6.75, 7, 8, 9, 10 };
                double[] box = { 1, 1, 3.01, 0.15, 5.2, 0.05 };
                for (int i = 0; i < f.Length; i++) Tone(buf, rate, at[i] * q, f[i] * 2, i == 5 || i == 11 ? 1.6 : 3.2, 0.002, box, 0.55);
                double[] bass = { 261.63, 196, 196, 261.63 };
                for (int i = 0; i < bass.Length; i++) Tone(buf, rate, (1 + i * 3) * q, bass[i], 2.2, 0.004, warm, 0.25);
                break;
            }
            default:
                buf = new double[(int)(rate * 1.6)];
                Tone(buf, rate, 0, 988, 5, 0.005, warm, 1);
                Tone(buf, rate, 0.18, 1319, 5, 0.005, warm, 1);
                Tone(buf, rate, 0.36, 988, 5, 0.005, warm, 1);
                Tone(buf, rate, 0.54, 1319, 5, 0.005, warm, 1);
                break;
        }
        return buf;
    }

    // нота фіксованої тривалості (для «цифрових» сигналів)
    static void Tone(double[] buf, int rate, double start, double freq, double decay, double attack, double[] partials, double gain, double dur)
    {
        int i0 = (int)(start * rate), i1 = i0 + (int)(dur * rate);
        for (int i = Math.Max(0, i0); i < Math.Min(buf.Length, i1); i++)
        {
            double dt = (double)(i - i0) / rate, v = 0;
            for (int k = 0; k < partials.Length; k += 2) v += partials[k + 1] * Math.Sin(2 * Math.PI * freq * partials[k] * dt);
            double env = Math.Min(1.0, dt / attack) * Math.Min(1.0, (i1 - i) / (rate * 0.004));
            buf[i] += v * env * gain;
        }
    }

    // плавний перехід висоти з вібрато (як терменвокс): наростання, рівна нота, згасання
    static void Glide(double[] buf, int rate, double start, double dur, double f0, double f1, double gain)
    {
        int i0 = (int)(start * rate), n = (int)(dur * rate);
        double ph = 0;
        for (int i = 0; i < n && i0 + i < buf.Length; i++)
        {
            double x = (double)i / n, tt = (double)i / rate;
            double sm = x * x * (3 - 2 * x);
            double f = (f0 + (f1 - f0) * sm) * (1 + 0.012 * Math.Sin(2 * Math.PI * 5.5 * tt));
            ph += 2 * Math.PI * f / rate;
            double env = Math.Min(1.0, x / 0.15) * Math.Min(1.0, (1 - x) / 0.2);
            buf[i0 + i] += (Math.Sin(ph) + 0.15 * Math.Sin(2 * ph)) * env * gain;
        }
    }

    // бубонець: короткий металевий дзвін з кількох високих неспівмірних тонів
    static void Jingle(double[] buf, int rate, double start, double gain)
    {
        double[] fs = { 5200, 6350, 7100, 8300 };
        foreach (double f in fs) Tone(buf, rate, start, f, 28, 0.001, new double[] { 1, 1 }, gain / fs.Length);
        foreach (double f in fs) Tone(buf, rate, start + 0.03, f * 1.03, 32, 0.001, new double[] { 1, 1 }, gain / fs.Length * 0.6);
    }

    // низький гуркіт (грім): шум, двічі пропущений через низькочастотний фільтр; наростає й довго згасає
    static void Rumble(double[] buf, int rate, double start, double dur, double gain)
    {
        Random r = new Random((int)(start * 1000) + 11);
        int i0 = (int)(start * rate), n = (int)(dur * rate);
        double y1 = 0, y2 = 0;
        for (int i = 0; i < n && i0 + i < buf.Length; i++)
        {
            double x = i / (double)n, env = Math.Min(1.0, x / 0.08) * Math.Exp(-3.2 * x) * (0.75 + 0.25 * Math.Sin(x * 40));
            y1 += 0.012 * ((r.NextDouble() * 2 - 1) - y1);
            y2 += 0.04 * (y1 - y2);
            buf[i0 + i] += y2 * env * gain * 9.0;
        }
    }

    // шум із відсіченням низьких частот: тарілка й хлопок салюту
    static void Crash(double[] buf, int rate, double start, double dur, double gain)
    {
        Random r = new Random((int)(start * 1000) + 7);
        int i0 = (int)(start * rate), n = (int)(dur * rate);
        double prev = 0;
        for (int i = 0; i < n && i0 + i < buf.Length; i++)
        {
            double x = r.NextDouble() * 2 - 1, hp = x - prev * 0.9;
            prev = x;
            buf[i0 + i] += hp * Math.Exp(-5.0 * i / n * (dur > 0.5 ? 1.0 : 3.0)) * gain * 0.5;
        }
    }

    // пташиний «цвірінь»: швидка зміна частоти
    static void Chirp(double[] buf, int rate, double start, double f0, double f1, double dur, double gain)
    {
        int i0 = (int)(start * rate), n = (int)(dur * rate);
        double ph = 0;
        for (int i = 0; i < n && i0 + i < buf.Length; i++)
        {
            double x = (double)i / n;
            double f = f0 + (f1 - f0) * x;
            ph += 2 * Math.PI * f / rate;
            double env = Math.Sin(Math.PI * x);
            buf[i0 + i] += Math.Sin(ph) * env * env * gain;
        }
    }

    // loop: кілька повторів мелодії з паузами для безперервного програвання (без згасання в кінці);
    // інакше - прослуховування: мелодія звучить двічі
    public const int PreviewPlays = 2;
    const double MaxPreviewSec = 30;     // стеля для дуже довгих файлів

    static byte[] Make(string id, int vol, bool loop)
    {
        int rate = 22050;
        double[] pat = Pattern(id, rate);
        double pk = 0;
        foreach (double x in pat) pk = Math.Max(pk, Math.Abs(x));
        double norm = (pk > 0 ? 22000.0 / pk : 7000) * Math.Max(0, Math.Min(100, vol)) / 100.0;     // усі мелодії однаково гучні
        int gap = (int)(rate * 0.7);
        int cycle = pat.Length + gap;
        int plays = id.StartsWith("fx_") ? 1 : PreviewPlays;       // звуки ефектів звучать один раз
        int n = loop ? Math.Max(1, (int)Math.Ceiling(rate * 20.0 / cycle)) * cycle : pat.Length * plays + gap * (plays - 1);
        double[] buf = new double[n];
        for (int at = 0; at < n; at += pat.Length + gap)
            for (int i = 0; i < pat.Length && at + i < n; i++) buf[at + i] = pat[i];
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter w = new BinaryWriter(ms))
        {
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 2); w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(n * 2);
            for (int i = 0; i < n; i++)
            {
                double ramp = 1.0;
                double fade = loop ? 1.0 : Math.Min(1.0, (n - i) / (rate * 0.05));
                w.Write((short)Math.Max(-32000, Math.Min(32000, buf[i] * norm * ramp * fade)));
            }
            return ms.ToArray();
        }
    }

    static DateTime endsAt = DateTime.MinValue;
    static System.Windows.Forms.Timer stopper;

    public static bool Playing { get { return (player != null || mciOpen) && DateTime.Now < endsAt; } }

    // id - вбудована мелодія або "file:назва"; loop - повторювати до вимкнення; vol - гучність 0..100
    public static void Play(string id, bool loop, int vol)
    {
        id = Normalize(id);
        if (id == "theme") id = ForTheme(ThemeId);
        PlayId(id, loop, vol);
    }

    // мелодія саме цієї теми (а не поточної теми панелі): для ефектів святкових запрошень
    static readonly Random fxRnd = new Random();
    static readonly Dictionary<string, int> fxLast = new Dictionary<string, int>();

    // звук ефекту свята (Хелловін, Різдво, Великдень, день народження): один із трьох варіантів, не той самий двічі поспіль
    public static void PlayFx(string themeId, int vol)
    {
        string pre = themeId == "halloween" ? "fx_hw" : themeId == "xmas" ? "fx_xm" : themeId == "easter" ? "fx_ea" : "fx_bd";
        int last, k;
        if (!fxLast.TryGetValue(pre, out last)) last = 0;
        do k = fxRnd.Next(1, 4); while (k == last);
        fxLast[pre] = k;
        PlayId(pre + k, false, vol);
    }

    static void PlayId(string id, bool loop, int vol)
    {
        Stop();
        endsAt = loop ? DateTime.MaxValue : DateTime.Now.AddSeconds(MaxPreviewSec);
        if (stopper != null) stopper.Stop();
        if (id.StartsWith("file:"))
        {
            string f = Path.Combine(Dir, id.Substring(5));
            try
            {
                if (File.Exists(f))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (mciSendString("open \"" + f + "\" type " + (ext == ".wav" ? "waveaudio" : "mpegvideo") + " alias sbsnd", null, 0, IntPtr.Zero) == 0)
                    {
                        mciOpen = true;
                        try { mciSendString("setaudio sbsnd volume to " + Math.Max(0, Math.Min(100, vol)) * 10, null, 0, IntPtr.Zero); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                        // прослуховування файлу: двічі (довжина - з MCI), з повторенням і зупинкою за таймером
                        double sec = MaxPreviewSec;
                        if (!loop)
                        {
                            StringBuilder len = new StringBuilder(32);
                            int ms;
                            if (mciSendString("status sbsnd length", len, len.Capacity, IntPtr.Zero) == 0 && int.TryParse(len.ToString().Trim(), out ms) && ms > 0)
                                sec = Math.Min(MaxPreviewSec, ms / 1000.0 * PreviewPlays + 0.1);
                            StartStopper(sec);
                        }
                        if (mciSendString("play sbsnd" + (loop || sec < MaxPreviewSec ? " repeat" : ""), null, 0, IntPtr.Zero) != 0)
                            mciSendString("play sbsnd", null, 0, IntPtr.Zero);   // не всі формати вміють repeat
                        return;
                    }
                }
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
            id = "chime";    // файл зник або не відтворюється - вбудований звук
        }
        try
        {
            byte[] data = Make(id, vol, loop);
            wav = new MemoryStream(data);
            player = new System.Media.SoundPlayer(wav);
            if (loop) player.PlayLooping();
            else
            {
                double sec = (data.Length - 44) / (22050.0 * 2) + 0.15;
                endsAt = DateTime.Now.AddSeconds(sec);
                StartStopper(sec);
                player.Play();
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    // зупинити прослуховування через sec секунд
    static void StartStopper(double sec)
    {
        endsAt = DateTime.Now.AddSeconds(sec);
        if (stopper == null)
        {
            stopper = new System.Windows.Forms.Timer();
            stopper.Tick += delegate { stopper.Stop(); if (endsAt != DateTime.MaxValue) Stop(); };
        }
        stopper.Stop();
        stopper.Interval = Math.Max(100, (int)(sec * 1000));
        stopper.Start();
    }

    public static void Stop()
    {
        try { if (mciOpen) { mciSendString("stop sbsnd", null, 0, IntPtr.Zero); mciSendString("close sbsnd", null, 0, IntPtr.Zero); } } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        mciOpen = false;
        try { if (player != null) { player.Stop(); player.Dispose(); } } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        player = null;
        try { if (wav != null) wav.Dispose(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        wav = null;
    }
}
