using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

// ======================================================================
//  Скільки зберігати журнали (Налаштування → Діагностика): журнали пошуку проблем (панель, оверлей, віджети,
//  бібліотека в грі), журнал помилок і журнали аналізу процесів. "all" - усі; "d<N>" - за останні N днів;
//  "n<N>" - останні N запусків NeoBar (для бібліотеки - запусків гри) і N останніх записів аналізу.
//  Прибирання - у фоні: під час запуску, після зміни налаштування і раз на добу, поки щось пишеться.
// ======================================================================
static class LogKeep
{
    public static volatile string Spec = "d30";
    public const string Mark = "════";            // рядок-початок запуску в журналі
    static int busy, lastRun;
    static bool ran;

    public static bool Parse(string k, out char mode, out int n)
    {
        mode = '\0'; n = 0;
        if (k == null || k.Length < 2) return false;
        mode = k[0];
        return (mode == 'd' || mode == 'n') && int.TryParse(k.Substring(1), out n) && n > 0;
    }

    // рядок-позначка нового запуску (пишеться першим у кожен журнал за запуск)
    public static string SessionLine()
    {
        Version v = typeof(LogKeep).Assembly.GetName().Version;
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + Mark + " NeoBar " + v.Major + "." + v.Minor + "." + v.Build +
            ", PID " + System.Diagnostics.Process.GetCurrentProcess().Id + " " + Mark;
    }

    // дешево: прибирає не частіше разу на добу (перший раз - таймер через хвилину після запуску)
    public static void Daily()
    {
        if (!ran || unchecked(Environment.TickCount - lastRun) < 86400000) return;
        Soon();
    }

    public static void Soon()
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        ran = true;
        lastRun = Environment.TickCount;
        ThreadPool.QueueUserWorkItem(delegate
        {
            try { Run(); }
            catch (Exception ex) { SbLog.Skip(ex); }
            finally { Interlocked.Exchange(ref busy, 0); }
        });
    }

    static void Run()
    {
        char m; int n;
        if (!Parse(Spec, out m, out n)) return;
        SbLog.TrimAll(m, n);
        lock (Util.ErrGate) Trim(Util.ErrorPath, m, n, null);
        ProcRecorder.PruneSoon(ProcRecorder.LogDir);
    }

    // лишити записи за останні n днів (mode 'd') або від n-го з кінця запуску (mode 'n')
    public static void Trim(string path, char mode, int n, string sessionWord)
    {
        if (!File.Exists(path)) return;
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        int cut;
        if (mode == 'd')
        {
            DateTime lim = DateTime.Now.AddDays(-n);
            bool dated = false;
            cut = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                DateTime t;
                if (!Stamp(lines[i], out t)) continue;          // продовження запису (стек помилки тощо) - разом із попереднім
                dated = true;
                if (t >= lim) { cut = i; break; }
            }
            if (!dated)
            {
                // рядки без дати (журнал бібліотеки пише лише час) - за часом останньої зміни файлу
                if (File.GetLastWriteTime(path) < lim) Delete(path);
                return;
            }
            if (cut < 0) cut = lines.Length;
        }
        else
        {
            List<int> starts = new List<int>();
            for (int i = 0; i < lines.Length; i++)
                if (sessionWord != null ? lines[i].IndexOf(sessionWord, StringComparison.Ordinal) >= 0 : lines[i].IndexOf("  " + Mark + " ", StringComparison.Ordinal) >= 0) starts.Add(i);
            if (starts.Count <= n) return;
            cut = starts[starts.Count - n];
        }
        if (cut <= 0) return;
        if (cut >= lines.Length) { Delete(path); return; }
        string[] rest = new string[lines.Length - cut];
        Array.Copy(lines, cut, rest, 0, rest.Length);
        File.WriteAllLines(path, rest, new UTF8Encoding(true));
    }

    static bool Stamp(string l, out DateTime t)
    {
        t = DateTime.MinValue;
        return l.Length >= 19 && char.IsDigit(l[0]) && DateTime.TryParseExact(l.Substring(0, 19), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t);
    }

    static void Delete(string path) { try { File.Delete(path); } catch (Exception skipEx) { SbLog.Skip(skipEx); } }
}
