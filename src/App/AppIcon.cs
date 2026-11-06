using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// ======================================================================
//  Іконка програми (ресурс NeoBar.ico в exe) - для вікон, панелі задач і Alt+Tab,
//  та символи вікон (Segoe Fluent Icons / Segoe MDL2 Assets) для шапок.
// ======================================================================
static class AppIcon
{
    static Icon ico;
    static Bitmap small;
    static Font glyphFont;
    static float glyphSize;
    static int glyphOk;         // 0 - не перевіряли, 1 - є шрифт символів, 2 - немає

    public static Icon Get()
    {
        if (ico == null)
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("NeoBar.ico"))
                    if (s != null) ico = new Icon(s);
            }
            catch { }
        }
        return ico;
    }

    // поставити іконку вікну (тихо, якщо ресурсу немає)
    public static void Apply(Form f)
    {
        Icon i = Get();
        if (i != null) try { f.Icon = i; } catch { }
    }

    // мала картинка для шапки вікон без власного символу
    public static Bitmap Small()
    {
        if (small == null)
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("NeoBar.ico"))
                    if (s != null) using (Icon i = new Icon(s, 64, 64)) small = i.ToBitmap();
            }
            catch { }
        }
        return small;
    }

    // символ вікна в шапці: колір теми, розмір під висоту рядка
    public static bool DrawGlyph(Graphics g, string glyph, Rectangle r, Color c)
    {
        if (string.IsNullOrEmpty(glyph)) return false;
        if (glyphOk == 0)
        {
            glyphOk = 2;
            foreach (string n in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            {
                try
                {
                    using (Font t = new Font(n, 10f))
                        if (t.Name == n) { glyphOk = 1; glyphName = n; break; }
                }
                catch { }
            }
        }
        if (glyphOk != 1) return false;
        float px = r.Height * 0.8f;
        if (glyphFont == null || Math.Abs(glyphSize - px) > 0.5f)
        {
            if (glyphFont != null) glyphFont.Dispose();
            glyphFont = new Font(glyphName, px, FontStyle.Regular, GraphicsUnit.Pixel);
            glyphSize = px;
        }
        TextRenderer.DrawText(g, glyph, glyphFont, r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        return true;
    }
    static string glyphName = "Segoe MDL2 Assets";
}
