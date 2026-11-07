using System;
using System.Collections.Generic;
using System.IO;

// ======================================================================
//  «Зниклі» елементи: програму чи гру видалили (файл або папки вже немає), а елемент на панелі лишився.
//  Такі елементи приглушуються на панелі, позначаються в налаштуваннях, і їх можна прибрати однією кнопкою.
//  Нічого не видаляється саме. Обережно, щоб не помилитись: сайти, команди без шляху, системні місця, мережеві
//  шляхи й диски, яких зараз немає (флешка, мережа), зниклими не вважаються.
// ======================================================================
static class Gone
{
    static readonly Dictionary<string, KeyValuePair<DateTime, bool>> cache = new Dictionary<string, KeyValuePair<DateTime, bool>>();

    public static bool Is(Entry e)
    {
        if (e == null || e.Kind != Kind.Item) return false;
        string key = e.Full;
        if (string.IsNullOrEmpty(key)) return false;
        lock (cache)
        {
            KeyValuePair<DateTime, bool> c;
            if (cache.TryGetValue(key, out c) && (DateTime.Now - c.Key).TotalSeconds < 5) return c.Value;
        }
        bool r = false;
        try { r = Check(e); } catch { r = false; }
        lock (cache) cache[key] = new KeyValuePair<DateTime, bool>(DateTime.Now, r);
        return r;
    }

    public static int Count(List<Entry> list)
    {
        int n = 0;
        foreach (Entry e in list) if (Is(e)) n++;
        return n;
    }

    // прибирає зниклі елементи зі списку; повертає, скільки прибрано
    public static int Remove(List<Entry> list)
    {
        return list.RemoveAll(delegate (Entry e) { return Is(e); });
    }

    static bool Check(Entry e)
    {
        string t = e.Full;
        if (Util.IsUrl(t) || SysCatalog.Csidl(e.Target) >= 0) return false;
        string file, args;
        Util.SplitCommand(t, out file, out args);
        file = (file ?? "").Trim().Trim('"');
        if (!Reachable(file)) return false;
        if (!File.Exists(file) && !Directory.Exists(file)) return true;
        // ярлик: дивимось, куди він веде
        if (string.Equals(Path.GetExtension(file), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            string tg = IconHi.LinkTarget(file);
            tg = Environment.ExpandEnvironmentVariables((tg ?? "").Trim());
            if (Reachable(tg) && !File.Exists(tg) && !Directory.Exists(tg)) return true;
        }
        return false;
    }

    // повний шлях на локальному диску, який зараз підключений: лише про такі можна впевнено сказати «немає»
    public static bool Reachable(string p)
    {
        if (string.IsNullOrEmpty(p) || !Path.IsPathRooted(p) || p.StartsWith(@"\\")) return false;
        if (Util.SlowPath(p)) return false;
        string root = Path.GetPathRoot(p);
        return !string.IsNullOrEmpty(root) && Directory.Exists(root);
    }
}
