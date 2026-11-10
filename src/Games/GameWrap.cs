using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

// ======================================================================
//  Обгортки старих API, що кладуться в теку гри за вибором користувача (лише для ігор без античіта):
//   - DirectX 8: d3d8to9 (Patrick Mours, BSD 2-clause) - d3d8.dll змушує гру працювати через DirectX 9;
//   - DirectDraw: cnc-ddraw (FunkyFr3sh, MIT) - ddraw.dll + мінімальний ddraw.ini (вікно на весь екран без рамки).
//  Тоді NeoBar бачить гру як звичайну DirectX 9 / OpenGL: працюють ліміт FPS і показники в кадрі.
//  Чужий файл не перезаписується; видалення - лише нашого файлу (за хешем; ddraw.ini - за міткою).
// ======================================================================
static class GameWrap
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct OSVI { public int Size, Major, Minor, Build, Platform; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Csd; }
    [DllImport("ntdll.dll")] static extern int RtlGetVersion(ref OSVI v);

    // чужа обгортка вже лежить у теці гри (DDrawCompat, dgVoodoo, cnc-ddraw, d3d8to9, dxwrapper, DXVK - під будь-яким іменем файлу, напр. xdd.dll): своєї не ставимо
    static readonly System.Collections.Generic.Dictionary<string, KeyValuePair<DateTime, string>> foreignCache = new System.Collections.Generic.Dictionary<string, KeyValuePair<DateTime, string>>();
    static readonly string[] wrapKeys = { "ddrawcompat", "dgvoodoo", "cnc-ddraw", "d3d8to9", "dxwrapper", "dxvk" };
    // імена, які Windows підхоплює з теки exe сама (системні бібліотеки, що їх підміняють обгортки)
    static readonly string[] stdNames = { "ddraw.dll", "d3d8.dll", "d3d9.dll", "d3d10.dll", "d3d10_1.dll", "d3d11.dll", "dxgi.dll", "opengl32.dll", "dinput.dll", "dinput8.dll", "winmm.dll", "version.dll", "dsound.dll" };
    static bool StdDll(string lowerName) { return Array.IndexOf(stdNames, lowerName) >= 0; }

    // чи містить exe (або, якщо запущений exe невідомий, будь-який exe теки) ім'я бібліотеки - так перевіряємо, що гра справді підвантажує обгортку з нестандартним іменем
    static byte Low(byte c) { return (c >= (byte)'A' && c <= (byte)'Z') ? (byte)(c + 32) : c; }
    static readonly Dictionary<string, bool> refCache = new Dictionary<string, bool>();
    static bool ExeRefers(string exe, string dir, string lowerName)
    {
        string rk = exe + "|" + (exe.Length == 0 ? dir : "") + "|" + lowerName;
        bool known;
        if (refCache.TryGetValue(rk, out known)) return known;
        bool res = ExeRefersRaw(exe, dir, lowerName);
        refCache[rk] = res;
        return res;
    }
    static bool ExeRefersRaw(string exe, string dir, string lowerName)
    {
        try
        {
            List<string> list = new List<string>();
            if (exe.Length > 0) list.Add(exe); else list.AddRange(Directory.GetFiles(dir, "*.exe"));
            byte[] pat = System.Text.Encoding.ASCII.GetBytes(lowerName);
            foreach (string e in list)
            {
                FileInfo fi = new FileInfo(e);
                if (!fi.Exists || fi.Length > 96L * 1024 * 1024) continue;
                byte[] b = File.ReadAllBytes(e);
                for (int i = 0; i + pat.Length <= b.Length; i++)
                {
                    int j = 0;
                    while (j < pat.Length && Low(b[i + j]) == pat[j]) j++;
                    if (j == pat.Length) return true;
                }
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return false;
    }

    public static string Foreign(Game g)
    {
        string dir = TargetDir(g);
        if (dir.Length == 0 || !Directory.Exists(dir)) return "";
        KeyValuePair<DateTime, string> c;
        string exe = FpsLimiter.ExePathOf(g);
        if (exe.Length == 0) exe = g.ExeFile ?? "";
        string ckey = dir + "|" + exe;
        if (foreignCache.TryGetValue(ckey, out c) && (DateTime.Now - c.Key).TotalSeconds < 30) return c.Value;
        string r = "";
        try
        {
            foreach (string f in Directory.GetFiles(dir, "*.dll"))
            {
                string name = Path.GetFileName(f).ToLowerInvariant();
                string kind = name == "d3d8.dll" ? "d3d8" : name == "ddraw.dll" ? "ddraw" : "";
                if (kind.Length > 0 && File.Exists(SrcPath(kind)) && new FileInfo(f).Length == new FileInfo(SrcPath(kind)).Length && HashOf(f) == HashOf(SrcPath(kind))) continue;      // наш файл
                string t = "";
                try { System.Diagnostics.FileVersionInfo v = System.Diagnostics.FileVersionInfo.GetVersionInfo(f); t = (v.ProductName + " " + v.FileDescription + " " + v.CompanyName + " " + v.InternalName + " " + v.Comments).ToLowerInvariant(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                foreach (string k in wrapKeys)
                    if (t.Contains(k))
                    {
                        // обгортка з нестандартним ім'ям (xdd.dll) працює, лише якщо exe гри її підвантажує (виправлений імпорт); інакше вона в теці просто лежить
                        if (!StdDll(name) && !ExeRefers(exe, dir, name)) break;
                        r = k + " (" + Path.GetFileName(f) + ")"; break;
                    }
                if (r.Length == 0 && kind.Length > 0) r = L.T("чужий ") + Path.GetFileName(f);
                if (r.Length > 0) break;
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        foreignCache[ckey] = new KeyValuePair<DateTime, string>(DateTime.Now, r);
        return r;
    }

    // сучасна система (Windows 10 і новіша): на ній вбудовані DirectX 8 і DirectDraw лише емуляція, і обгортка дає більше
    static int modern = -1;
    public static bool ModernOs
    {
        get
        {
            if (modern < 0)
            {
                try { OSVI v = new OSVI(); v.Size = Marshal.SizeOf(typeof(OSVI)); modern = RtlGetVersion(ref v) == 0 && v.Major >= 10 ? 1 : 0; }
                catch { modern = 0; }
            }
            return modern == 1;
        }
    }

    const string IniMark = "; NeoBar: файл створено автоматично";

    public static string KindOf(string api) { return api == "dx8" ? "d3d8" : api == "ddraw" ? "ddraw" : ""; }
    static string Dll(string kind) { return kind == "d3d8" ? "d3d8.dll" : "ddraw.dll"; }
    static string SrcPath(string kind)
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Path.Combine(kind == "d3d8" ? "d3d8to9" : "cnc-ddraw", Dll(kind)));
    }

    public static bool Available(string api) { string k = KindOf(api); return k.Length > 0 && File.Exists(SrcPath(k)); }

    static string HashOf(string path)
    {
        try
        {
            using (SHA1 h = SHA1.Create())
            using (FileStream f = File.OpenRead(path))
                return BitConverter.ToString(h.ComputeHash(f));
        }
        catch { return ""; }
    }

    // тека, куди кладеться файл: тека запущеного exe (якщо гра зараз запущена), інакше тека гри
    public static string TargetDir(Game g)
    {
        if (g == null) return "";
        string d = FpsLimiter.ExeDirOf(g);
        if (d.Length == 0) d = g.ExeDir ?? "";
        if (d.Length == 0) d = g.Dir ?? "";
        return d;
    }

    static string Target(Game g, string kind) { string d = TargetDir(g); return d.Length > 0 ? Path.Combine(d, Dll(kind)) : ""; }

    // у теці гри лежить саме наша обгортка
    public static bool Installed(Game g, string api)
    {
        string k = KindOf(api); if (k.Length == 0) return false;
        string t = Target(g, k);
        if (t.Length == 0 || !File.Exists(t) || !File.Exists(SrcPath(k))) return false;
        try { if (new FileInfo(t).Length != new FileInfo(SrcPath(k)).Length) return false; } catch { return false; }
        return HashOf(t) == HashOf(SrcPath(k));
    }

    // порожній рядок - успіх, інакше пояснення
    public static string Install(Game g, string api)
    {
        string k = KindOf(api);
        if (k.Length == 0) return L.T("Для цього API обгортки немає.");
        if (!File.Exists(SrcPath(k))) return L.T("Немає файлу ") + (k == "d3d8" ? "d3d8to9\\d3d8.dll" : "cnc-ddraw\\ddraw.dll") + L.T(" поруч із NeoBar.");
        string t = Target(g, k);
        if (t.Length == 0 || !Directory.Exists(Path.GetDirectoryName(t))) return L.T("Не знайдено теку гри.");
        try
        {
            if (File.Exists(t) && !Installed(g, api)) return L.T("У теці гри вже є інший ") + Dll(k) + L.T(" - NeoBar його не чіпає.");
            File.Copy(SrcPath(k), t, true);
            if (k == "ddraw")
            {
                string ini = Path.Combine(Path.GetDirectoryName(t), "ddraw.ini");
                if (!File.Exists(ini))
                    File.WriteAllText(ini, IniMark + "\r\n[ddraw]\r\nfullscreen=true\r\nwindowed=true\r\nrenderer=auto\r\nmaxfps=-1\r\n");
            }
            return "";
        }
        catch (Exception ex) { return L.T("Не вдалося скопіювати файл у теку гри: ") + ex.Message; }
    }

    // видалення програми: прибрати свою обгортку з теки (лише якщо файл точно наш - за хешем)
    public static void RemoveAt(string dir, string kind)
    {
        try
        {
            string t = Path.Combine(dir, Dll(kind)), src = SrcPath(kind);
            if (!File.Exists(t) || !File.Exists(src)) return;
            string ht = HashOf(t);
            if (ht.Length == 0 || ht != HashOf(src)) return;
            File.Delete(t);
            if (kind == "ddraw")
            {
                string ini = Path.Combine(dir, "ddraw.ini");
                if (File.Exists(ini) && File.ReadAllText(ini).StartsWith(IniMark)) File.Delete(ini);
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    public static string Remove(Game g, string api)
    {
        string k = KindOf(api); if (k.Length == 0) return "";
        string t = Target(g, k);
        if (t.Length == 0 || !File.Exists(t)) return "";
        if (!Installed(g, api)) return Dll(k) + L.T(" у теці гри не наш - NeoBar його не видаляє.");
        try
        {
            File.Delete(t);
            if (k == "ddraw")
            {
                string ini = Path.Combine(Path.GetDirectoryName(t), "ddraw.ini");
                try { if (File.Exists(ini) && File.ReadAllText(ini).StartsWith(IniMark)) File.Delete(ini); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            }
            return "";
        }
        catch (Exception ex) { return L.T("Не вдалося видалити файл (гра запущена?): ") + ex.Message; }
    }

    // API гри, для якого потрібна обгортка: вже покладена нами або визначена
    public static string ApiFor(Game g, string api)
    {
        if (g != null && g.Wrap == "d3d8") return "dx8";
        if (g != null && g.Wrap == "ddraw") return "ddraw";
        return api;
    }

    // обгортка можлива: сучасна система, 32-бітна гра без античіта, інтеграція дозволена й щось із неї потрібне
    public static bool Eligible(Game g)
    {
        return g != null && ModernOs && g.Bits == 32 && g.Integr && g.Ac.Length == 0 && (g.Ov || g.Limit) && Foreign(g).Length == 0;
    }

    static string key(Game g) { return g.Api + "|" + g.Wrap + "|" + g.Integr + "|" + Foreign(g) + "|" + g.Ac.Length + "|" + g.Ov + "|" + g.Limit + "|" + g.ExeDir + "|" + g.Bits; }

    // автоматично: після того, як відомо, що гра на DirectX 8 або DirectDraw, кладе обгортку в теку гри; зі зніманням інтеграції - прибирає. Викликається часто, працює лише при зміні стану.
    public static void Sync(Game g)
    {
        if (g == null) return;
        // дешева перевірка до будь-якої роботи з диском: обгортки потрібні лише іграм на DirectX 8 / DirectDraw
        // (і тим, куди NeoBar уже поклав свою); решта бібліотеки ігор теку не сканує
        if (g.Wrap.Length == 0 && KindOf(g.Api).Length == 0) return;
        string k = key(g);
        if (g.WrapKey == k || DateTime.Now < g.WrapRetry) return;
        string dir = TargetDir(g);
        if (dir.Length == 0 || !Directory.Exists(dir)) return;
        g.WrapKey = k;
        try
        {
            if (g.Wrap.Length > 0)
            {
                string a = ApiFor(g, "");
                if (Eligible(g))
                {
                    if (!Installed(g, a))
                    {
                        string r = Install(g, a);
                        FpsLimiter.LogWrap(g, "обгортка " + g.Wrap + " відновлена: " + (r.Length == 0 ? "ок" : r));
                        if (r.Length > 0) g.WrapRetry = DateTime.Now.AddMinutes(5);
                        else if (FpsLimiter.IsRunning(g)) g.WrapFresh = true;
                    }
                    else if (g.Api == "dx9" || g.Api == "opengl") g.WrapFresh = false;
                }
                else
                {
                    string r = Remove(g, a);
                    if (r.Length == 0) { g.Wrap = ""; g.WrapFresh = false; Games.Save(); FpsLimiter.LogWrap(g, "обгортку прибрано"); }
                    else { g.WrapRetry = DateTime.Now.AddSeconds(30); }         // гра ще працює - файл зайнятий
                }
            }
            else if (Foreign(g).Length > 0)
            {
                // у теці чужа обгортка (DDrawCompat тощо): залишки наших файлів (збіг за вмістом) прибираємо, навіть якщо стан обгортки втрачено
                foreach (string ap in new string[] { "ddraw", "dx8" })
                {
                    string t = Target(g, KindOf(ap));
                    if (t.Length > 0 && File.Exists(t) && Installed(g, ap))
                    {
                        string r = Remove(g, ap);
                        FpsLimiter.LogWrap(g, "залишок нашої обгортки " + KindOf(ap) + (r.Length == 0 ? " прибрано (у теці чужа обгортка)" : ": " + r));
                        if (r.Length > 0) g.WrapRetry = DateTime.Now.AddSeconds(30);
                    }
                }
            }
            else if (Eligible(g))
            {
                string kind = KindOf(g.Api);
                if (kind.Length == 0 || !Available(g.Api)) return;
                string r = Install(g, g.Api);
                FpsLimiter.LogWrap(g, "обгортка " + kind + ": " + (r.Length == 0 ? "встановлено в " + dir : r));
                if (r.Length == 0) { g.Wrap = kind; g.WrapFresh = FpsLimiter.IsRunning(g); Games.Save(); }
                else g.WrapRetry = DateTime.Now.AddMinutes(10);
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    public static void SyncAll() { foreach (Game g in Games.List) Sync(g); }

    // підпис для налаштувань гри
    public static string Info(Game g, string api)
    {
        string a = ApiFor(g, api), core = InfoCore(g, api);
        string note = a == "ddraw" ? L.T("Старі ігри (DirectDraw, API ddraw) можуть працювати некоректно або не працювати взагалі: оверлей у кадрі та ліміт FPS для них не гарантовані.")
                    : a == "dx8" ? L.T("Старі ігри (DirectX 8) працюють через обгортку DirectX 9; на окремих іграх можливі збої.")
                    : "";
        if (note.Length == 0) return core;
        return core.Length > 0 ? core + " " + note : note;
    }

    static string InfoCore(Game g, string api)
    {
        string a = ApiFor(g, api);
        if (KindOf(a).Length == 0) return "";
        string name = KindOf(a) == "d3d8" ? "DirectX 8 → 9 (d3d8to9)" : "DirectDraw (cnc-ddraw)";
        if (g.Ac.Length > 0) return "";
        string fw = Foreign(g);
        if (fw.Length > 0) return L.T("У теці гри вже є обгортка ") + fw + L.T(": NeoBar свою не ставить") + (g.Wrap.Length > 0 ? L.T(" і прибирає раніше поставлену.") : ".");
        if (!ModernOs) return L.T("Обгортка ") + name + L.T(" потрібна лише в Windows 10 і новіших.");
        if (g.Bits != 32 && g.Bits != 0) return "";
        if (g.Wrap.Length > 0 && g.WrapMiss) return L.T("Гра не підхопила обгортку ") + name + L.T(": вона завантажила системну бібліотеку. Перевірте властивості exe гри (вкладка «Сумісність»): режими сумісності й «зменшений колір» заважають.");
        if (g.Wrap.Length > 0) return L.T("Обгортка ") + name + L.T(" поставлена автоматично (файли в теці гри); гра йде через DirectX 9 / OpenGL, тож працюють показники й ліміт. Якщо вимкнути інтеграцію, її буде прибрано.") + (g.WrapFresh ? L.T(" Перезапустіть гру.") : "");
        if (!g.Integr) return L.T("З інтеграцією NeoBar сам би поклав у теку гри обгортку ") + name + L.T(", щоб працювали показники й ліміт.");
        return L.T("NeoBar сам покладе в теку гри обгортку ") + name + L.T(", щойно гру буде запущено; діє з наступного запуску.");
    }
}
