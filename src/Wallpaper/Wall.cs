using System;
using System.IO;
using System.Text;
using Microsoft.Win32;

// Поточні шпалери - потрібні для скляного фону панелі (тема «Скло»): шпалери NeoBar (зображення,
// поточний кадр слайд-шоу), інакше системні
static class Wall
{
    public static string Path_()
    {
        try
        {
            if ((WallCfg.Mode == 1 || WallCfg.Mode == 2) && WallMgr.CurrentFile.Length > 0 && File.Exists(WallMgr.CurrentFile)) return WallMgr.CurrentFile;
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return SystemPath();
    }

    public static string SystemPath()
    {
        try
        {
            StringBuilder sb = new StringBuilder(520);
            if (Win.SystemParametersInfo(0x73, 520, sb, 0) && sb.Length > 0 && File.Exists(sb.ToString()))
                return sb.ToString();
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        string t = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                @"Microsoft\Windows\Themes\TranscodedWallpaper");
        return File.Exists(t) ? t : null;
    }

    public static int StyleCode()
    {
        if ((WallCfg.Mode == 1 || WallCfg.Mode == 2) && WallMgr.CurrentFile.Length > 0) return WallCfg.Fit == 1 ? 6 : (WallCfg.Fit == 2 ? 2 : (WallCfg.Fit == 3 ? 0 : 10));
        try
        {
            string key = @"HKEY_CURRENT_USER\Control Panel\Desktop";
            int st;
            if (!int.TryParse(Convert.ToString(Registry.GetValue(key, "WallpaperStyle", "10")), out st)) st = 10;
            if (Convert.ToString(Registry.GetValue(key, "TileWallpaper", "0")) == "1") return 10;
            return st;
        }
        catch { return 10; }
    }
}
