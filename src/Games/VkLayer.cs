using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

// Шар Vulkan: NeoBar реєструє себе як неявний шар Vulkan (запис у реєстрі + маніфест), і завантажувач Vulkan сам підключає
// NeoBarFps64.dll / NeoBarFps32.dll до кожної Vulkan-гри. Так API гри, кадри й ліміт у Vulkan відомі точно.
// Шар підключається до ВСІХ Vulkan-програм, тож для ігор з античітом, яким NeoBar заборонено щось впроваджувати,
// реєстрація знімається, поки така гра запущена.
static class VkLayer
{
    const string LayerName = "VK_LAYER_NEOBAR_fps";
    const string RegPath = @"SOFTWARE\Khronos\Vulkan\ImplicitLayers";

    public static string Note = "";               // стан для налаштувань
    static bool on;                               // зараз зареєстровано
    static bool tried;
    static DateTime nextCheck = DateTime.MinValue;
    static readonly List<string> manifests = new List<string>();

    static string Dir { get { return AppDomain.CurrentDomain.BaseDirectory; } }

    // раз на секунду (з таймера ігор)
    public static void Tick()
    {
        try
        {
            if (!tried)
            {
                tried = true;
                Remove(false);                      // залишки від минулого запуску (якщо NeoBar закрився аварійно)
            }
            bool want = Games.VkLayer;
            if (!want) { if (on) Remove(true); Note = L.T("Вимкнено."); return; }
            if (DateTime.Now < nextCheck) return;
            nextCheck = DateTime.Now.AddSeconds(3);
            string risky = RiskyRunning();
            if (risky.Length > 0)
            {
                if (on) Remove(true);
                Note = L.T("Тимчасово знято: запущено гру з античітом (") + risky + L.T("). Шар повернеться після її закриття.");
                return;
            }
            if (!on)
            {
                string err = Install();
                if (err.Length > 0) { Note = err; nextCheck = DateTime.Now.AddSeconds(60); }
                else Note = L.T("Зареєстровано: Vulkan-ігри, запущені відтепер, підключаються автоматично.");
            }
        }
        catch (Exception ex) { Note = L.T("Помилка: ") + ex.Message; nextCheck = DateTime.Now.AddSeconds(60); }
    }

    public static void Shutdown()
    {
        try { if (on) Remove(true); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    // гра з античітом, якій NeoBar не дозволено торкатися, зараз працює
    // служби античіта, що запускаються разом із захищеною грою (постійні служби на кшталт Vanguard чи FACEIT сюди не входять:
    // вони працюють і без гри). Поки така служба працює, шар знімаємо - навіть якщо самої гри немає в списку NeoBar.
    static readonly string[] GameAcProcesses =
    {
        "easyanticheat", "easyanticheat_eos", "beservice", "beservice_x64", "eaanticheat.gameservice", "eaanticheat.gameservicelauncher",
        "start_protected_game", "xigncode", "gameguard", "pnkbstra", "pnkbstrb", "sguard64"
    };

    static string RiskyRunning()
    {
        List<Game> risky = new List<Game>();
        foreach (Game g in Games.List)
            if (g.Ac.Length > 0 && g.Mode == 2 && g.Dir.Length > 0) risky.Add(g);
        Process[] all = Process.GetProcesses();
        try
        {
            foreach (Process p in all)
            {
                string n = "";
                try { n = p.ProcessName.ToLowerInvariant(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                if (n.Length > 0 && Array.IndexOf(GameAcProcesses, n) >= 0) return L.T("служба ") + n;
                if (risky.Count == 0) continue;
                string exe;
                try { exe = Games.ExeOf(p.Id); } catch { continue; }
                if (exe.Length == 0) continue;
                foreach (Game g in risky)
                    if (exe.StartsWith(g.Dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return g.Name;
            }
        }
        finally { foreach (Process p in all) p.Dispose(); }
        return "";
    }

    static string Json(string dll)
    {
        return "{\n  \"file_format_version\": \"1.1.2\",\n  \"layer\": {\n    \"name\": \"" + LayerName + "\",\n    \"type\": \"GLOBAL\",\n" +
               "    \"library_path\": \"" + dll.Replace("\\", "\\\\") + "\",\n    \"api_version\": \"1.3.0\",\n    \"implementation_version\": \"1\",\n" +
               "    \"description\": \"NeoBar frame counter and limiter\",\n" +
               "    \"functions\": { \"vkNegotiateLoaderLayerInterfaceVersion\": \"vkNegotiateLoaderLayerInterfaceVersion\" },\n" +
               "    \"disable_environment\": { \"DISABLE_VK_LAYER_NEOBAR\": \"1\" }\n  }\n}\n";
    }

    // пише маніфест і запис у реєстрі; повертає текст помилки
    static string Install()
    {
        bool os64 = Environment.Is64BitOperatingSystem;
        List<string[]> items = new List<string[]>();          // { json, dll, view }
        if (os64) items.Add(new[] { Path.Combine(Dir, "NeoBarVkLayer64.json"), Path.Combine(Dir, "NeoBarFps64.dll"), "64" });
        items.Add(new[] { Path.Combine(Dir, "NeoBarVkLayer32.json"), Path.Combine(Dir, "NeoBarFps32.dll"), "32" });
        bool admin = Elevated.IsAdmin;
        manifests.Clear();
        bool any = false;
        foreach (string[] it in items)
        {
            if (!File.Exists(it[1])) continue;
            File.WriteAllText(it[0], Json(it[1]), new UTF8Encoding(false));
            RegistryView view = it[2] == "64" ? RegistryView.Registry64 : RegistryView.Registry32;
            bool ok = false;
            // HKLM (потрібні права адміністратора) - так читають усі версії завантажувача; HKCU - новіші
            try
            {
                using (RegistryKey b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (RegistryKey k = b.CreateSubKey(RegPath))
                { k.SetValue(it[0], 0, RegistryValueKind.DWord); ok = true; }
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
            if (!ok)
                try
                {
                    using (RegistryKey b = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
                    using (RegistryKey k = b.CreateSubKey(RegPath))
                    { k.SetValue(it[0], 0, RegistryValueKind.DWord); ok = true; }
                }
                catch (Exception skipEx) { SbLog.Skip(skipEx); }
            if (ok) { manifests.Add(it[0] + "|" + it[2]); any = true; }
        }
        if (!any) return admin ? L.T("Не вдалося записати шар у реєстр.") : L.T("Потрібні права адміністратора (запустіть NeoBar від імені адміністратора).");
        on = true;
        return "";
    }

    // знімає запис (і з HKLM, і з HKCU) та видаляє маніфести
    static void Remove(bool keepNote)
    {
        foreach (string suffix in new[] { "64", "32" })
        {
            string json = Path.Combine(Dir, "NeoBarVkLayer" + suffix + ".json");
            RegistryView view = suffix == "64" ? RegistryView.Registry64 : RegistryView.Registry32;
            if (suffix == "64" && !Environment.Is64BitOperatingSystem) continue;
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                try
                {
                    using (RegistryKey b = RegistryKey.OpenBaseKey(hive, view))
                    using (RegistryKey k = b.OpenSubKey(RegPath, true))
                        if (k != null && k.GetValue(json) != null) k.DeleteValue(json, false);
                }
                catch (Exception skipEx) { SbLog.Skip(skipEx); }
            try { if (File.Exists(json)) File.Delete(json); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
        manifests.Clear();
        on = false;
    }
}
