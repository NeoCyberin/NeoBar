using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// ======================================================================
//  Теми оформлення та тематичні іконки
// ======================================================================
static class Themes
{
    public static readonly List<Theme> All = Build();

    // святкові теми (Хелловін, Різдво, Великдень, день народження) - у швидкому налаштуванні не показуються
    public static bool Seasonal(string id) { return id == "halloween" || id == "xmas" || id == "easter" || id == "birthday"; }

    static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }

    static Theme Mk(string id, string title, Color bg, Color hover, Color text, Color head,
                    Color sep, Color edge, Color accent, string font, int radius, int edgeW, IconStyle st)
    {
        Theme t = new Theme();
        t.Id = id; t.Title = title;
        t.Bg = bg; t.Hover = hover; t.Text = text; t.Head = head;
        t.Sep = sep; t.Edge = edge; t.Accent = accent;
        t.Font = font; t.Radius = radius; t.EdgeW = edgeW; t.Style = st;
        return t;
    }

    static List<Theme> Build()
    {
        List<Theme> l = new List<Theme>();

        l.Add(Mk("dark", L.T("Темна"), C(22, 24, 30), C(52, 57, 70), C(205, 210, 220), C(120, 128, 145),
                 C(55, 60, 72), C(45, 50, 62), C(59, 130, 246), "Segoe UI", 6, 1, IconStyle.Tile));

        l.Add(Mk("light", L.T("Світла"), C(243, 245, 248), C(222, 227, 236), C(32, 36, 46), C(110, 118, 135),
                 C(210, 215, 224), C(205, 210, 220), C(37, 99, 235), "Segoe UI", 6, 1, IconStyle.Flat));

        // Скляна: розмиті шпалери робочого столу + напівпрозоре тонування
        Theme glass = Mk("glass", L.T("Скляна"), C(14, 20, 34), Color.FromArgb(64, 255, 255, 255), C(244, 247, 255), C(196, 206, 222),
                 Color.FromArgb(80, 255, 255, 255), Color.FromArgb(120, 255, 255, 255), C(125, 190, 255), "Trebuchet MS|Segoe UI", 10, 1, IconStyle.Glass);
        glass.Glass = true;
        glass.Opacity = 42;
        l.Add(glass);

        Theme cyber = Mk("cyber", L.T("Кіберпанк"), C(9, 7, 22), C(46, 18, 78), C(140, 240, 232), C(190, 60, 255),
                 C(120, 0, 200), C(255, 46, 140), C(255, 46, 140), "Consolas", 2, 2, IconStyle.Neon);
        cyber.Palette = new Color[] { C(255, 0, 128), C(0, 255, 234), C(255, 230, 0), C(157, 0, 255), C(0, 255, 102) };
        l.Add(cyber);

        // Стімпанк: мідні пластини - заокруглені прямокутники із заклепками
        Theme steam = Mk("steam", L.T("Стімпанк"), C(27, 21, 17), C(98, 70, 42), C(238, 216, 172), C(206, 166, 98),
                 C(150, 106, 60), C(205, 128, 54), C(228, 180, 62), "Georgia", 8, 3, IconStyle.Plate);
        // закопчене тло, світліші мідні розділювачі й рамка, яскрава латунь як акцент, патина - для статусів
        steam.Palette = new Color[] { C(228, 180, 62), C(205, 128, 54), C(96, 165, 142), C(176, 98, 58), C(214, 196, 140) };
        l.Add(steam);

        // Урбаністична: нічне місто, неонові вивіски, блискавка
        Theme urban = Mk("urban", L.T("Урбаністична"), C(30, 32, 36), C(60, 64, 72), C(232, 232, 232), C(140, 143, 148),
                 C(78, 82, 88), C(255, 196, 0), C(255, 196, 0), "Bahnschrift|Segoe UI", 0, 3, IconStyle.Spark);
        urban.Palette = new Color[] { C(255, 196, 0), C(255, 122, 0), C(240, 240, 240), C(0, 200, 220), C(255, 70, 90) };
        l.Add(urban);

        // Гламурна: глянцеві сердечка (більші плитки при тому ж розмірі піктограм), білі символи
        Theme pink = Mk("pink", L.T("Гламурна"), C(255, 243, 248), C(255, 211, 231), C(98, 24, 72), C(178, 72, 132),
                 C(246, 190, 216), C(255, 116, 184), C(216, 36, 116), "Palatino Linotype|Georgia|Segoe UI", 14, 2, IconStyle.Heart);
        // рожевий + ніжно-блакитний і бузковий як другі кольори, персиковий і м'ятний - для різноманіття серій
        pink.Palette = new Color[] { C(240, 98, 170), C(104, 190, 238), C(184, 138, 240), C(255, 166, 128), C(106, 212, 174), C(216, 36, 116) };
        l.Add(pink);

        Theme nature = Mk("nature", L.T("Природна"), C(19, 34, 25), C(46, 78, 52), C(222, 238, 212), C(140, 175, 125),
                 C(60, 94, 60), C(92, 140, 68), C(140, 198, 63), "Cambria|Georgia|Segoe UI", 10, 2, IconStyle.Leaf);
        nature.Palette = new Color[] { C(140, 198, 63), C(228, 180, 76), C(76, 175, 80), C(46, 139, 87), C(192, 160, 96), C(127, 176, 105) };
        l.Add(nature);

        // Артистична: папір, графіт і кольорові олівці, штрихування
        Theme art = Mk("art", L.T("Артистична"), C(247, 242, 230), Color.FromArgb(205, 255, 214, 70), C(54, 50, 58), C(118, 106, 98),
                 C(150, 140, 130), C(80, 76, 84), C(196, 74, 64), "Segoe Print|Ink Free|Comic Sans MS|Segoe UI", 3, 2, IconStyle.Sketch);
        art.Sketchy = true;
        art.Palette = new Color[] { C(224, 96, 84), C(222, 164, 52), C(64, 156, 158), C(138, 98, 178), C(96, 156, 86), C(76, 118, 190) };
        l.Add(art);

        // ---------- святкові (вмикаються вручну або пропозицією біля свята - див. Seasons.cs) ----------
        // У кожної - свій градієнт (уздовж панелі, у шапці й тлі вікон, на віджетах), свій шрифт
        // і прикраси у вільних місцях (Festive.Decor). Bg - суцільний колір для полів, меню й нотаток.

        // Хелловін: ніч від фіолетової темряви вгорі до жару гарбуза внизу
        Theme hw = Mk("halloween", L.T("Хелловін"), C(24, 15, 32), C(64, 40, 74), C(246, 232, 212), C(255, 156, 64),
                 C(70, 48, 84), C(232, 116, 30), C(255, 128, 24), "Franklin Gothic Medium|Segoe UI", 10, 2, IconStyle.Pumpkin);
        hw.Grad = new Color[] { C(28, 14, 38), C(42, 18, 52), C(58, 22, 48), C(86, 34, 22) };
        hw.Palette = new Color[] { C(255, 128, 24), C(240, 96, 16), C(255, 160, 40), C(150, 70, 200), C(110, 190, 60) };
        l.Add(hw);
        // Різдвяна: глибока зимова ніч - майже чорне небо зверху, внизу світлішає до синього, як над снігом
        Theme xm = Mk("xmas", L.T("Різдвяна"), C(9, 17, 38), C(28, 48, 92), C(238, 244, 255), C(176, 204, 244),
                 C(34, 56, 104), C(150, 190, 240), C(120, 180, 255), "Constantia|Georgia|Segoe UI", 10, 2, IconStyle.Ornament);
        xm.Grad = new Color[] { C(2, 5, 14), C(6, 13, 32), C(12, 28, 62), C(24, 54, 104), C(44, 86, 144) };
        xm.Palette = new Color[] { C(56, 112, 224), C(150, 196, 246), C(226, 234, 246), C(66, 104, 204), C(96, 196, 236), C(120, 140, 220) };
        l.Add(xm);
        // Великодня: світла, пастельні писанки; кремовий → бузковий → м'ятний → персиковий
        Theme ea = Mk("easter", L.T("Великодня"), C(252, 248, 238), C(236, 228, 250), C(66, 58, 84), C(120, 92, 150),
                 C(226, 218, 236), C(190, 170, 215), C(62, 128, 58), "Candara|Segoe UI", 14, 1, IconStyle.Egg);
        ea.Grad = new Color[] { C(253, 246, 236), C(246, 238, 252), C(234, 247, 238), C(253, 241, 230) };
        ea.Palette = new Color[] { C(240, 150, 166), C(146, 200, 134), C(240, 196, 92), C(168, 152, 230), C(124, 184, 230) };
        l.Add(ea);
        // День народження: світла пастельна веселка (рожевий → персиковий → лимонний → м'ятний → небесний → бузковий),
        // яскраві кульки й рожево-коралевий акцент. Без великих насичених плям - очі не втомлюються
        Theme bd = Mk("birthday", L.T("День народження"), C(255, 250, 246), Color.FromArgb(64, 236, 72, 140), C(62, 42, 74), C(190, 64, 120),
                 Color.FromArgb(80, 214, 120, 170), C(236, 184, 208), C(206, 52, 108), "Comic Sans MS|Segoe UI", 14, 1, IconStyle.Balloon);
        bd.Grad = new Color[] { C(255, 224, 236), C(255, 238, 214), C(255, 248, 208), C(224, 246, 230), C(220, 238, 255), C(240, 230, 255) };
        bd.Palette = new Color[] { C(236, 72, 153), C(250, 176, 20), C(56, 156, 236), C(76, 184, 96), C(255, 112, 67), C(150, 100, 220) };
        l.Add(bd);

        l.Add(Mk("custom", L.T("Власна"), C(22, 24, 30), C(52, 57, 70), C(205, 210, 220), C(120, 128, 145),
                 C(55, 60, 72), C(59, 130, 246), C(59, 130, 246), "Segoe UI", 8, 2, IconStyle.Tile));
        return l;
    }

    public static Theme Get(string id, Cfg c)
    {
        if (id == "custom")
        {
            Theme ct = Mk("custom", L.T("Власна"), Util.Opaque(c.CBg), c.CHover, c.CText,
                          Util.Blend(c.CText, c.CBg, 0.45f), Util.Blend(c.CBg, c.CText, 0.15f),
                          c.CAccent, c.CAccent, "Segoe UI", 8, 2, IconStyle.Tile);
            ct.Glass = c.Blur == 1;
            return ct;
        }
        foreach (Theme th in All) if (th.Id == id) return th;
        return All[0];
    }
}

// ----------------------------------------------------------------------
//  «Від руки»: нерівні лінії, штрихування, паперова текстура
// ----------------------------------------------------------------------
static class Sk
{
    public static PointF[] RoundPoints(Rectangle r, int rad, Random rnd, float amp)
    {
        List<PointF> p = new List<PointF>();
        int m = Math.Max(1, Math.Min(rad, Math.Min(r.Width, r.Height) / 2));
        float[][] corners =
        {
            new float[] { r.Left + m, r.Top + m, 180 },
            new float[] { r.Right - m, r.Top + m, 270 },
            new float[] { r.Right - m, r.Bottom - m, 0 },
            new float[] { r.Left + m, r.Bottom - m, 90 }
        };
        for (int c = 0; c < 4; c++)
        {
            for (int s = 0; s <= 2; s++)
            {
                double a = (corners[c][2] + s * 45) * Math.PI / 180.0;
                p.Add(new PointF(corners[c][0] + (float)Math.Cos(a) * m, corners[c][1] + (float)Math.Sin(a) * m));
            }
            // середина ребра до наступного кута
            int n = (c + 1) % 4;
            double a2 = (corners[n][2]) * Math.PI / 180.0;
            PointF next = new PointF(corners[n][0] + (float)Math.Cos(a2) * m, corners[n][1] + (float)Math.Sin(a2) * m);
            PointF cur = p[p.Count - 1];
            p.Add(new PointF((cur.X + next.X) / 2, (cur.Y + next.Y) / 2));
        }
        PointF[] res = p.ToArray();
        for (int i = 0; i < res.Length; i++)
        {
            res[i].X += (float)((rnd.NextDouble() - 0.5) * 2 * amp);
            res[i].Y += (float)((rnd.NextDouble() - 0.5) * 2 * amp);
        }
        return res;
    }

    public static GraphicsPath WobblyRound(Rectangle r, int rad, Random rnd, float amp)
    {
        GraphicsPath gp = new GraphicsPath();
        gp.AddClosedCurve(RoundPoints(r, rad, rnd, amp), 0.35f);
        return gp;
    }

    // нерівна лінія: кілька точок з поперечним шумом
    public static void Line(Graphics g, Pen pen, float x1, float y1, float x2, float y2, Random rnd, float amp)
    {
        float dx = x2 - x1, dy = y2 - y1;
        float len = (float)Math.Sqrt(dx * dx + dy * dy);
        if (len < 2) { g.DrawLine(pen, x1, y1, x2, y2); return; }
        float nx = -dy / len, ny = dx / len;
        int n = Math.Max(2, (int)(len / 14));
        PointF[] pts = new PointF[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n;
            float j = (i == 0 || i == n) ? 0 : (float)((rnd.NextDouble() - 0.5) * 2 * amp);
            pts[i] = new PointF(x1 + dx * t + nx * j, y1 + dy * t + ny * j);
        }
        g.DrawCurve(pen, pts, 0.4f);
    }

    // діагональне штрихування олівцем усередині clip-області
    public static void Hatch(Graphics g, Rectangle r, Color col, float spacing, float lw, bool back, Random rnd)
    {
        using (Pen pn = new Pen(col, lw))
        {
            pn.StartCap = LineCap.Round;
            pn.EndCap = LineCap.Round;
            int h = r.Height, w = r.Width;
            for (float x = -h; x < w + h; x += spacing)
            {
                float j1 = (float)((rnd.NextDouble() - 0.5) * spacing * 0.35), j2 = (float)((rnd.NextDouble() - 0.5) * spacing * 0.35);
                if (back) g.DrawLine(pn, r.X + x + j1, r.Y + h, r.X + x + h + j2, r.Y);
                else g.DrawLine(pn, r.X + x + j1, r.Y, r.X + x + h + j2, r.Y + h);
            }
        }
    }

    static Bitmap paper;

    // тайл паперу з ледь помітними штрихами графіту (для фону панелі)
    public static Bitmap Paper(Color baseCol)
    {
        if (paper != null) return paper;
        int n = 112;
        Bitmap b = new Bitmap(n, n, PixelFormat.Format32bppArgb);
        Random rnd = new Random(11);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.Clear(baseCol);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < 70; i++)
            {
                int x = rnd.Next(n), y = rnd.Next(n), l = rnd.Next(6, 20);
                using (Pen p = new Pen(Color.FromArgb(rnd.Next(10, 26), 90, 80, 70), 1f))
                {
                    // малюємо з перенесенням через межі, щоб тайл стикувався
                    for (int ox = -n; ox <= 0; ox += n)
                        for (int oy = -n; oy <= 0; oy += n)
                            g.DrawLine(p, x + ox, y + oy, x + ox + l, y + oy + l);
                }
            }
        }
        paper = b;
        return b;
    }
}

// ----------------------------------------------------------------------
//  Тематичні іконки (малюються кодом, гліфи Segoe MDL2 Assets)
// ----------------------------------------------------------------------
static class IconFactory
{
    // ключ, назва, код гліфа
    public static readonly string[][] Catalog = new string[][]
    {
        new string[] { "game", L.T("Гра"), "E7FC" },
        new string[] { "web", L.T("Сайт"), "E774" },
        new string[] { "folder", L.T("Папка"), "E8B7" },
        new string[] { "home", L.T("Дім"), "E80F" },
        new string[] { "pc", L.T("Комп'ютер"), "E7F8" },
        new string[] { "desktop", L.T("Робочий стіл"), "E7F4" },
        new string[] { "bin", L.T("Кошик"), "E74D" },
        new string[] { "music", L.T("Музика"), "E8D6" },
        new string[] { "video", L.T("Відео"), "E714" },
        new string[] { "photo", L.T("Фото"), "E722" },
        new string[] { "pictures", L.T("Галерея"), "E8B9" },
        new string[] { "chat", L.T("Чат"), "E8BD" },
        new string[] { "mail", L.T("Пошта"), "E715" },
        new string[] { "code", L.T("Код"), "E943" },
        new string[] { "terminal", L.T("Термінал"), "E756" },
        new string[] { "settings", L.T("Налаштування"), "E713" },
        new string[] { "taskmgr", L.T("Диспетчер"), "E9D9" },
        new string[] { "star", L.T("Обране"), "E734" },
        new string[] { "download", L.T("Завантаження"), "E896" },
        new string[] { "upload", L.T("Вивантаження"), "E898" },
        new string[] { "doc", L.T("Документ"), "E8A5" },
        new string[] { "link", L.T("Посилання"), "E71B" },
        new string[] { "cloud", L.T("Хмара"), "E753" },
        new string[] { "phone", L.T("Телефон"), "E717" },
        new string[] { "headphones", L.T("Навушники"), "E7F6" },
        new string[] { "calendar", L.T("Календар"), "E787" },
        new string[] { "search", L.T("Пошук"), "E721" },
        new string[] { "shop", L.T("Магазин"), "E719" },
        new string[] { "edit", L.T("Редактор"), "E70F" },
        new string[] { "lock", L.T("Безпека"), "E72E" },
        new string[] { "play", L.T("Плеєр"), "E768" },
        new string[] { "calc", L.T("Калькулятор"), "E8EF" },
        new string[] { "book", L.T("Бібліотека"), "E8F1" },
        new string[] { "user", L.T("Профіль"), "E77B" },
        new string[] { "learn", L.T("Навчання"), "E7BE" },
        new string[] { "wifi", "Wi-Fi", "E701" },
        new string[] { "bluetooth", "Bluetooth", "E702" },
        new string[] { "pin", L.T("Мапа"), "E707" },
        new string[] { "people", L.T("Люди"), "E716" },
        new string[] { "send", L.T("Надіслати"), "E724" },
        new string[] { "share", L.T("Поділитися"), "E72D" },
        new string[] { "save", L.T("Зберегти"), "E74E" },
        new string[] { "volume", L.T("Звук"), "E767" },
        new string[] { "color", L.T("Колір"), "E790" },
        new string[] { "cart", L.T("Кошик покупок"), "E7BF" },
        new string[] { "power", L.T("Живлення"), "E7E8" },
        new string[] { "admin", L.T("Адміністратор"), "E7EF" },
        new string[] { "history", L.T("Історія"), "E81C" },
        new string[] { "bank", L.T("Банк"), "E825" },
        new string[] { "copy", L.T("Копіювати"), "E8C8" },
        new string[] { "tag", L.T("Мітка"), "E8EC" },
        new string[] { "bolt", L.T("Блискавка"), "E945" },
        new string[] { "puzzle", L.T("Пазл"), "EA86" },
        new string[] { "heart", L.T("Улюблене"), "EB51" },
        new string[] { "keyboard", L.T("Клавіатура"), "E765" },
        new string[] { "mic", L.T("Мікрофон"), "E720" },
        new string[] { "print", L.T("Друк"), "E749" },
    };

    // за абеткою (як у списку налаштувань)
    static IconFactory()
    {
        System.Globalization.CompareInfo ci = Widgets.UK.CompareInfo;
        Array.Sort(Catalog, delegate (string[] x, string[] y) { return ci.Compare(x[1], y[1], System.Globalization.CompareOptions.IgnoreCase); });
    }

    static int fontOk; // 0 - невідомо, 1 - є, 2 - немає

    public static bool GlyphFont()
    {
        if (fontOk == 0)
        {
            try
            {
                using (Font f = new Font("Segoe MDL2 Assets", 10f))
                    fontOk = f.Name == "Segoe MDL2 Assets" ? 1 : 2;
            }
            catch { fontOk = 2; }
        }
        return fontOk == 1;
    }

    static readonly Dictionary<string, string> fontCache = new Dictionary<string, string>();

    static string ResolvedFont(Theme t)
    {
        string r;
        if (fontCache.TryGetValue(t.Font, out r)) return r;
        using (Font f = Util.MakeFont(t.Font, 10f, FontStyle.Bold)) r = f.Name;
        fontCache[t.Font] = r;
        return r;
    }

    static Color TileColor(Theme t, string seed)
    {
        if (seed == "close") return Color.FromArgb(214, 64, 64);
        if (seed == "settings") return Util.Opaque(t.Accent);
        int h = Util.Hash(seed);
        if (t.Palette != null && t.Palette.Length > 0) return t.Palette[h % t.Palette.Length];
        return Util.Blend(t.Accent, Util.DefPalette[h % Util.DefPalette.Length], 0.3f);
    }

    static void DrawGlyph(Graphics g, string glyph, bool letter, string fontName, float fs, int px,
                          float dx, float dy, Color col, Color shadow, int glowA)
    {
        if (!letter)
        {
            // піктограма системного шрифту: центруємо за реальними межами символу, а не за метрикою шрифту
            try
            {
                using (FontFamily ff = new FontFamily("Segoe MDL2 Assets"))
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddString(glyph, ff, (int)FontStyle.Regular, fs, new PointF(0, 0), StringFormat.GenericTypographic);
                    RectangleF bb = gp.GetBounds();
                    if (bb.Width > 0.5f && bb.Height > 0.5f)
                    {
                        float ox = dx + px / 2f - (bb.X + bb.Width / 2f), oy = dy + px / 2f - (bb.Y + bb.Height / 2f);
                        float o2 = Math.Max(1f, px * 0.045f);
                        Action<float, float, Color> fill = delegate (float ax, float ay, Color c)
                        {
                            using (Matrix m = new Matrix())
                            using (GraphicsPath p2 = (GraphicsPath)gp.Clone())
                            using (SolidBrush br = new SolidBrush(c))
                            {
                                m.Translate(ox + ax, oy + ay);
                                p2.Transform(m);
                                g.FillPath(br, p2);
                            }
                        };
                        if (glowA > 0)
                        {
                            Color gc = Color.FromArgb(glowA, col);
                            fill(-o2, 0, gc); fill(o2, 0, gc); fill(0, -o2, gc); fill(0, o2, gc);
                        }
                        if (!shadow.IsEmpty) fill(1, 1, shadow);
                        fill(0, 0, col);
                        return;
                    }
                }
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
        using (Font f = letter
            ? new Font(fontName, fs, FontStyle.Bold, GraphicsUnit.Pixel)
            : new Font("Segoe MDL2 Assets", fs, FontStyle.Regular, GraphicsUnit.Pixel))
        using (StringFormat sf = new StringFormat())
        {
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            float o = Math.Max(1f, px * 0.045f);
            if (glowA > 0)
            {
                using (SolidBrush gb = new SolidBrush(Color.FromArgb(glowA, col)))
                {
                    g.DrawString(glyph, f, gb, new RectangleF(dx - o, dy, px, px), sf);
                    g.DrawString(glyph, f, gb, new RectangleF(dx + o, dy, px, px), sf);
                    g.DrawString(glyph, f, gb, new RectangleF(dx, dy - o, px, px), sf);
                    g.DrawString(glyph, f, gb, new RectangleF(dx, dy + o, px, px), sf);
                }
            }
            if (!shadow.IsEmpty)
            {
                using (SolidBrush sb = new SolidBrush(shadow))
                    g.DrawString(glyph, f, sb, new RectangleF(dx + 1, dy + 1, px, px), sf);
            }
            using (SolidBrush cb = new SolidBrush(col))
                g.DrawString(glyph, f, cb, new RectangleF(dx, dy, px, px), sf);
        }
    }

    // стягнути вершини багатокутника до центроїда (для внутрішнього вушка)
    static PointF[] Inset(PointF[] p, float k)
    {
        float cx = 0, cy = 0;
        foreach (PointF q in p) { cx += q.X; cy += q.Y; }
        cx /= p.Length; cy /= p.Length;
        PointF[] r = new PointF[p.Length];
        for (int i = 0; i < p.Length; i++)
            r[i] = new PointF(cx + (p[i].X - cx) * k, cy + (p[i].Y - cy) * k);
        return r;
    }

    public static void DrawSparkle(Graphics g, float cx, float cy, float s, Color col)
    {
        PointF[] p =
        {
            new PointF(cx, cy - s), new PointF(cx + s * 0.28f, cy - s * 0.28f),
            new PointF(cx + s, cy), new PointF(cx + s * 0.28f, cy + s * 0.28f),
            new PointF(cx, cy + s), new PointF(cx - s * 0.28f, cy + s * 0.28f),
            new PointF(cx - s, cy), new PointF(cx - s * 0.28f, cy - s * 0.28f)
        };
        using (SolidBrush b = new SolidBrush(col)) g.FillPolygon(b, p);
    }

    static void DrawBolt(Graphics g, float x, float y, float s, Color fill, Color outline)
    {
        float[][] u =
        {
            new float[] { 0.58f, 0f }, new float[] { 0.12f, 0.56f }, new float[] { 0.44f, 0.56f },
            new float[] { 0.28f, 1f }, new float[] { 0.88f, 0.38f }, new float[] { 0.54f, 0.38f }, new float[] { 0.78f, 0f }
        };
        PointF[] p = new PointF[u.Length];
        for (int i = 0; i < u.Length; i++) p[i] = new PointF(x + u[i][0] * s, y + u[i][1] * s);
        using (SolidBrush b = new SolidBrush(fill)) g.FillPolygon(b, p);
        using (Pen pn = new Pen(outline, Math.Max(1f, s * 0.07f)))
        {
            pn.LineJoin = LineJoin.Round;
            g.DrawPolygon(pn, p);
        }
    }

    // size - розмір готової іконки; символ завжди малюється від size / IconScale
    public static Bitmap Make(Theme t, string key, string label, int size)
    {
        string k = key ?? "";
        string glyph = null;
        foreach (string[] it in Catalog)
        {
            if (it[0] == k)
            {
                glyph = char.ConvertFromUtf32(int.Parse(it[2], NumberStyles.HexNumber));
                break;
            }
        }
        bool letter = glyph == null || !GlyphFont();
        if (letter)
        {
            string lb = (label ?? "").Trim();
            glyph = lb.Length == 0 ? "?" : lb.Substring(0, 1).ToUpperInvariant();
        }

        int px = size;
        float gs = size / t.IconScale;           // «базовий» розмір для символу
        Color tile = TileColor(t, k.Length > 0 ? k : (label ?? ""));
        Color ink = Color.FromArgb(28, 28, 28);

        Bitmap b = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            Rectangle r = new Rectangle(1, 1, px - 3, px - 3);
            float pw = Math.Max(2f, px * 0.07f);
            float fs = gs * (letter ? 0.46f : 0.5f);
            float dx = 0f, dy = 0f;
            Color gcol = Color.White;
            Color gsh = Color.Empty;
            int glow = 0;
            bool doodle = false;

            switch (t.Style)
            {
                // Світла: пастельна плитка, символ насиченого кольору
                case IconStyle.Flat:
                {
                    Color pastel = Util.Blend(tile, Color.White, 0.82f);
                    Color edge = Util.Blend(tile, Color.White, 0.42f);
                    using (SolidBrush sb = new SolidBrush(pastel))
                    using (Pen pn = new Pen(edge, Math.Max(1f, px * 0.04f)))
                    using (GraphicsPath pf = Util.Round(r, px * 24 / 100))
                    {
                        g.FillPath(sb, pf);
                        g.DrawPath(pn, pf);
                    }
                    gcol = Util.Blend(tile, Color.Black, 0.28f);
                    break;
                }

                // Скляна: матове скляне коло з відблиском
                case IconStyle.Glass:
                {
                    using (SolidBrush tb = new SolidBrush(Color.FromArgb(95, tile)))
                        g.FillEllipse(tb, r);
                    using (LinearGradientBrush sheen = new LinearGradientBrush(new Rectangle(0, 0, px, px),
                        Color.FromArgb(120, 255, 255, 255), Color.FromArgb(8, 255, 255, 255), 90f))
                        g.FillEllipse(sheen, r);
                    using (Pen rim = new Pen(Color.FromArgb(185, 255, 255, 255), Math.Max(1f, px * 0.04f)))
                        g.DrawEllipse(rim, r);
                    Rectangle gl = new Rectangle((int)(px * 0.2f), (int)(px * 0.07f), (int)(px * 0.6f), Math.Max(2, (int)(px * 0.3f)));
                    using (LinearGradientBrush gloss = new LinearGradientBrush(gl,
                        Color.FromArgb(130, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                        g.FillEllipse(gloss, gl);
                    gcol = Color.FromArgb(250, 255, 255, 255);
                    gsh = Color.FromArgb(110, 0, 0, 0);
                    break;
                }

                // Кіберпанк: темний щиток зі зрізаними кутами, неонова рамка й світіння символу
                case IconStyle.Neon:
                {
                    int cut = Math.Max(2, px * 22 / 100);
                    int ins = (int)Math.Ceiling(pw / 2f);
                    Rectangle rn = Rectangle.Inflate(r, -ins, -ins);
                    using (GraphicsPath pn = new GraphicsPath())
                    {
                        pn.AddPolygon(new Point[]
                        {
                            new Point(rn.X + cut, rn.Y), new Point(rn.Right, rn.Y),
                            new Point(rn.Right, rn.Bottom - cut), new Point(rn.Right - cut, rn.Bottom),
                            new Point(rn.X, rn.Bottom), new Point(rn.X, rn.Y + cut)
                        });
                        using (SolidBrush fb = new SolidBrush(Util.Blend(Color.Black, tile, 0.14f)))
                        using (Pen glowp = new Pen(Color.FromArgb(70, tile), pw * 2f))
                        using (Pen line = new Pen(tile, pw))
                        {
                            glowp.LineJoin = LineJoin.Round;
                            g.FillPath(fb, pn);
                            g.DrawPath(glowp, pn);
                            g.DrawPath(line, pn);
                        }
                    }
                    gcol = Util.Blend(tile, Color.White, 0.3f);
                    glow = 55;
                    break;
                }

                // Стімпанк: мідна пластина (заокруглений прямокутник) із заклепками; символ менший, щоб не виходив за межі
                case IconStyle.Plate:
                {
                    int rad = Math.Max(3, px * 24 / 100);
                    float ow = Math.Max(2f, px * 0.1f);
                    int ins = (int)Math.Ceiling(ow / 2f);
                    Rectangle ro = Rectangle.Inflate(r, -ins, -ins);
                    using (GraphicsPath pb = Util.Round(r, rad))
                    using (SolidBrush fb = new SolidBrush(Util.Blend(Color.Black, tile, 0.3f)))
                        g.FillPath(fb, pb);
                    using (GraphicsPath pr = Util.Round(ro, Math.Max(2, rad - ins)))
                    using (LinearGradientBrush rg = new LinearGradientBrush(new Rectangle(0, 0, px, px),
                        Util.Blend(tile, Color.White, 0.4f), Util.Blend(tile, Color.Black, 0.35f), 45f))
                    using (Pen po = new Pen(rg, ow))
                        g.DrawPath(po, pr);
                    int gap = (int)Math.Ceiling(ow) + Math.Max(2, px * 5 / 100);
                    Rectangle rin = Rectangle.Inflate(r, -gap, -gap);
                    using (GraphicsPath pi = Util.Round(rin, Math.Max(2, rad - gap)))
                    using (Pen pn = new Pen(Color.FromArgb(120, tile), Math.Max(1f, px * 0.025f)))
                        g.DrawPath(pn, pi);
                    float rv = Math.Max(1.2f, px * 0.05f);
                    float rc = ins + rv + Math.Max(1f, px * 0.05f);
                    using (SolidBrush rivet = new SolidBrush(Util.Blend(tile, Color.White, 0.55f)))
                    using (SolidBrush rdark = new SolidBrush(Color.FromArgb(120, 20, 10, 0)))
                    {
                        float[] xs = { r.Left + rc, r.Right - rc };
                        float[] ys = { r.Top + rc, r.Bottom - rc };
                        foreach (float rx in xs)
                            foreach (float ry in ys)
                            {
                                g.FillEllipse(rdark, rx - rv + 0.7f, ry - rv + 0.7f, rv * 2, rv * 2);
                                g.FillEllipse(rivet, rx - rv, ry - rv, rv * 2, rv * 2);
                            }
                    }
                    gcol = Color.FromArgb(240, 208, 140);
                    gsh = Color.FromArgb(170, 20, 10, 0);
                    fs = gs * (letter ? 0.36f : 0.4f);
                    break;
                }

                // Урбаністична: нічне місто (силует будинків), неонова рамка, блискавка, символ світиться
                case IconStyle.Spark:
                {
                    using (LinearGradientBrush bg = new LinearGradientBrush(new Rectangle(0, 0, px, px),
                        Color.FromArgb(46, 50, 56), Color.FromArgb(22, 24, 28), 90f))
                        g.FillRectangle(bg, r);
                    int cnt = 5;
                    float bw = r.Width / (float)cnt;
                    int hs = Util.Hash(k + label);
                    using (SolidBrush sky = new SolidBrush(Color.FromArgb(78, tile)))
                    using (SolidBrush win = new SolidBrush(Color.FromArgb(190, tile)))
                    {
                        for (int i = 0; i < cnt; i++)
                        {
                            float hf = 0.16f + 0.17f * (((hs >> (i * 2)) & 3) / 3f);
                            float bh = r.Height * hf;
                            RectangleF br = new RectangleF(r.X + i * bw + 0.5f, r.Bottom - bh, bw - 1f, bh);
                            g.FillRectangle(sky, br);
                            if (px >= 36)
                            {
                                float ws = Math.Max(1f, px * 0.03f);
                                for (float wy = br.Top + ws * 2; wy < br.Bottom - ws * 2; wy += ws * 3.2f)
                                    if (((hs >> ((int)(wy) & 15)) & 1) == 1)
                                        g.FillRectangle(win, br.X + bw * 0.3f, wy, ws, ws);
                            }
                        }
                    }
                    using (Pen bp = new Pen(tile, Math.Max(2f, px * 0.07f)))
                    {
                        bp.Alignment = PenAlignment.Inset;
                        g.DrawRectangle(bp, r);
                    }
                    DrawBolt(g, px * 0.64f, px * 0.05f, px * 0.3f, tile, Color.FromArgb(24, 24, 28));
                    gcol = Util.Blend(tile, Color.White, 0.35f);
                    glow = 70;
                    fs = gs * (letter ? 0.42f : 0.46f);
                    dx = -px * 0.03f;
                    dy = px * 0.03f;
                    break;
                }

                // Гламурна: миле глянцеве рожеве кошеня — символ по центру мордочки, біла зірочка
                case IconStyle.Heart:
                {
                    Color pink = Util.Blend(tile, Color.White, 0.08f);
                    Color pinkDk = Util.Blend(tile, Color.Black, 0.12f);
                    // голова (заокруглений квадрат майже на всю плитку, трохи нижче — зверху місце для вушок)
                    float hx = px * 0.06f, hy = px * 0.25f, hw = px * 0.88f, hh = px * 0.70f;
                    RectangleF head = new RectangleF(hx, hy, hw, hh);
                    int hr = Math.Max(4, (int)(hw * 0.46f));
                    // вушка
                    PointF[] earL = { new PointF(px * 0.2f, hy + px * 0.08f), new PointF(px * 0.3f, hy - px * 0.14f), new PointF(px * 0.46f, hy + px * 0.04f) };
                    PointF[] earR = { new PointF(px * 0.8f, hy + px * 0.08f), new PointF(px * 0.7f, hy - px * 0.14f), new PointF(px * 0.54f, hy + px * 0.04f) };
                    using (SolidBrush eb = new SolidBrush(pinkDk))
                    {
                        g.FillPolygon(eb, earL);
                        g.FillPolygon(eb, earR);
                    }
                    using (SolidBrush ib = new SolidBrush(Util.Blend(tile, Color.White, 0.55f)))
                    {
                        g.FillPolygon(ib, Inset(earL, 0.5f));
                        g.FillPolygon(ib, Inset(earR, 0.5f));
                    }
                    using (GraphicsPath ph = Util.Round(Rectangle.Round(head), hr))
                    {
                        using (LinearGradientBrush hg = new LinearGradientBrush(head,
                            Util.Blend(pink, Color.White, 0.3f), pinkDk, 90f))
                            g.FillPath(hg, ph);
                        using (Pen rim = new Pen(Color.FromArgb(235, 255, 255, 255), Math.Max(1f, px * 0.04f)))
                            g.DrawPath(rim, ph);
                    }
                    if (px >= 28)
                    {
                        // вуса: по три з кожного боку, перетинають контур мордочки
                        using (Pen wp = new Pen(Color.FromArgb(235, 255, 255, 255), Math.Max(1f, px * 0.028f)))
                        {
                            wp.StartCap = LineCap.Round; wp.EndCap = LineCap.Round;
                            float wy = hy + hh * 0.6f;
                            float[] dyv = { -0.07f, 0.02f, 0.11f };
                            foreach (float kv in dyv)
                            {
                                float yy = wy + hh * kv;
                                float spread = kv * 0.5f * hh;
                                g.DrawLine(wp, hx - px * 0.035f, yy + spread, hx + hw * 0.2f, yy);
                                g.DrawLine(wp, hx + hw + px * 0.035f, yy + spread, hx + hw * 0.8f, yy);
                            }
                        }
                        // відблиск
                        using (SolidBrush shine = new SolidBrush(Color.FromArgb(120, 255, 255, 255)))
                            g.FillEllipse(shine, hx + hw * 0.16f, hy + hh * 0.1f, hw * 0.28f, hh * 0.14f);
                        // рум'янець
                        using (SolidBrush blush = new SolidBrush(Color.FromArgb(120, 255, 150, 190)))
                        {
                            g.FillEllipse(blush, hx + hw * 0.1f, hy + hh * 0.52f, hw * 0.2f, hh * 0.13f);
                            g.FillEllipse(blush, hx + hw * 0.7f, hy + hh * 0.52f, hw * 0.2f, hh * 0.13f);
                        }
                        DrawSparkle(g, px * 0.84f, hy + px * 0.02f, px * 0.1f, Color.White);
                    }
                    gcol = Color.White;
                    gsh = px >= 28 ? Color.FromArgb(130, 190, 30, 105) : Color.Empty;
                    fs = gs * (letter ? 0.34f : 0.38f);
                    dy = (hy + hh * 0.5f) - px / 2f;   // символ точно по центру мордочки
                    break;
                }

                // Природна: листок із прожилкою, символ кольору пергаменту
                case IconStyle.Leaf:
                {
                    using (GraphicsPath pl = Util.Round(r, px * 45 / 100, px * 10 / 100, px * 45 / 100, px * 10 / 100))
                    {
                        using (LinearGradientBrush lg = new LinearGradientBrush(new Rectangle(0, 0, px, px),
                            Util.Blend(tile, Color.White, 0.18f), Util.Blend(tile, Color.Black, 0.25f), 90f))
                            g.FillPath(lg, pl);
                        using (Pen vein = new Pen(Color.FromArgb(80, 235, 255, 200), Math.Max(1f, px * 0.035f)))
                            g.DrawLine(vein, px * 0.2f, px * 0.8f, px * 0.8f, px * 0.2f);
                    }
                    gcol = Color.FromArgb(247, 242, 208);
                    gsh = Color.FromArgb(120, 8, 36, 8);
                    break;
                }

                // Артистична: аркуш паперу, нерівний контур графітом, штрихування кольоровим олівцем
                case IconStyle.Sketch:
                {
                    Random rnd = new Random(Util.Hash(k + label));
                    Rectangle rs = new Rectangle(3, 3, px - 7, px - 7);
                    float amp = Math.Max(0.6f, px * 0.022f);
                    int rad = Math.Max(3, px * 18 / 100);
                    using (GraphicsPath pf = Sk.WobblyRound(rs, rad, rnd, amp))
                    {
                        using (SolidBrush paper = new SolidBrush(Color.FromArgb(252, 249, 240)))
                            g.FillPath(paper, pf);
                        GraphicsState st = g.Save();
                        g.SetClip(pf, CombineMode.Intersect);
                        float sp = Math.Max(3f, px * 0.085f);
                        Sk.Hatch(g, new Rectangle(0, 0, px, px), Color.FromArgb(120, tile), sp, Math.Max(1f, px * 0.03f), false, rnd);
                        Rectangle low = new Rectangle(0, (int)(px * 0.55f), px, px);
                        Sk.Hatch(g, low, Color.FromArgb(70, Util.Blend(tile, Color.Black, 0.35f)), sp * 1.1f, Math.Max(1f, px * 0.025f), true, rnd);
                        g.Restore(st);
                    }
                    Color graphite = Color.FromArgb(58, 54, 60);
                    for (int pass = 0; pass < 2; pass++)
                    {
                        using (GraphicsPath po = Sk.WobblyRound(rs, rad, rnd, amp * (pass == 0 ? 1f : 1.4f)))
                        using (Pen pn = new Pen(Color.FromArgb(pass == 0 ? 235 : 130, graphite), Math.Max(1.1f, px * (pass == 0 ? 0.04f : 0.025f))))
                        {
                            pn.LineJoin = LineJoin.Round;
                            g.DrawPath(pn, po);
                        }
                    }
                    // білий «ластик»-підкладка під символ, щоб він читався на штрихуванні
                    using (SolidBrush plate = new SolidBrush(Color.FromArgb(235, 252, 249, 240)))
                        g.FillEllipse(plate, px * 0.22f, px * 0.22f, px * 0.56f, px * 0.56f);
                    gcol = Color.FromArgb(44, 40, 48);
                    fs = gs * (letter ? 0.4f : 0.44f);
                    doodle = true;
                    break;
                }

                // святкові: гарбуз, ялинкова кулька, писанка, повітряна кулька (Festive.cs)
                case IconStyle.Pumpkin:
                case IconStyle.Ornament:
                case IconStyle.Egg:
                case IconStyle.Balloon:
                {
                    RectangleF fc = Festive.Draw(g, t.Style, new RectangleF(0, 0, px, px), tile, out gcol, out gsh, out glow);
                    if (px < 28) glow = 0;
                    fs = t.Style == IconStyle.Egg ? gs * (letter ? 0.38f : 0.42f) : gs * (letter ? 0.4f : 0.44f);
                    dy = (fc.Y + fc.Height * 0.5f) - px / 2f;
                    break;
                }

                // Темна і «Власна»: кольорова плитка, контрастний символ
                default:
                {
                    using (GraphicsPath pt = Util.Round(r, px * 22 / 100))
                    using (LinearGradientBrush tg = new LinearGradientBrush(new Rectangle(0, 0, px, px),
                        Util.Blend(tile, Color.White, 0.16f), Util.Blend(tile, Color.Black, 0.16f), 90f))
                        g.FillPath(tg, pt);
                    gcol = Util.Lum(tile) > 175 ? ink : Color.White;
                    break;
                }
            }

            DrawGlyph(g, glyph, letter, ResolvedFont(t), fs, px, dx, dy, gcol, gsh, glow);
            if (doodle) // другий, ледь зсунутий штрих - ефект «обведено від руки»
                DrawGlyph(g, glyph, letter, ResolvedFont(t), fs, px, dx + 0.8f, dy + 0.5f, Color.FromArgb(110, gcol), Color.Empty, 0);
        }
        return b;
    }

    // ---------- векторні піктограми для кнопок (читаються за будь-якого розміру) ----------

    static void ToolPath(string tool, RectangleF r, bool on, Graphics g, Color col, float lw)
    {
        float w = r.Width, cx = r.X + w / 2f, cy = r.Y + r.Height / 2f;
        using (Pen pen = new Pen(col, lw))
        using (SolidBrush br = new SolidBrush(col))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            switch (tool)
            {
                case "close":
                {
                    float m = w * 0.14f;
                    g.DrawLine(pen, r.X + m, r.Y + m, r.Right - m, r.Bottom - m);
                    g.DrawLine(pen, r.Right - m, r.Y + m, r.X + m, r.Bottom - m);
                    break;
                }
                case "settings":
                {
                    float R = w * 0.5f, ri = R * 0.76f;
                    List<PointF> pts = new List<PointF>();
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4;
                        double[] da = { -0.30, -0.19, 0.19, 0.30 };
                        double[] dr = { ri, R, R, ri };
                        for (int j = 0; j < 4; j++)
                            pts.Add(new PointF(cx + (float)(Math.Cos(a + da[j]) * dr[j]), cy + (float)(Math.Sin(a + da[j]) * dr[j])));
                    }
                    using (GraphicsPath gp = new GraphicsPath(FillMode.Alternate))
                    {
                        gp.AddPolygon(pts.ToArray());
                        float h = R * 0.36f;
                        gp.AddEllipse(cx - h, cy - h, h * 2, h * 2);
                        g.FillPath(br, gp);
                    }
                    break;
                }
                case "refresh":
                {
                    float rr = w * 0.36f;
                    RectangleF ar = new RectangleF(cx - rr, cy - rr, rr * 2, rr * 2);
                    float start = -40f, sweep = 285f;
                    g.DrawArc(pen, ar, start, sweep);
                    double ea = (start + sweep) * Math.PI / 180.0;
                    float px = cx + (float)Math.Cos(ea) * rr, py = cy + (float)Math.Sin(ea) * rr;
                    float tx = -(float)Math.Sin(ea), ty = (float)Math.Cos(ea);   // напрямок руху
                    float nx = (float)Math.Cos(ea), ny = (float)Math.Sin(ea);
                    float L = w * 0.26f, Wd = w * 0.2f;
                    PointF[] head =
                    {
                        new PointF(px + tx * L, py + ty * L),
                        new PointF(px + nx * Wd, py + ny * Wd),
                        new PointF(px - nx * Wd, py - ny * Wd)
                    };
                    g.FillPolygon(br, head);
                    break;
                }
                case "pin":
                {
                    GraphicsState st = g.Save();
                    g.TranslateTransform(cx, cy);
                    if (!on) g.RotateTransform(40f);
                    float u = w;
                    g.FillRectangle(br, -0.17f * u, -0.46f * u, 0.34f * u, 0.12f * u);
                    g.FillRectangle(br, -0.095f * u, -0.34f * u, 0.19f * u, 0.34f * u);
                    g.FillRectangle(br, -0.25f * u, -0.01f * u, 0.5f * u, 0.11f * u);
                    using (Pen np = new Pen(col, Math.Max(1.2f, lw * 0.75f)))
                    {
                        np.StartCap = LineCap.Round; np.EndCap = LineCap.Round;
                        g.DrawLine(np, 0, 0.1f * u, 0, 0.46f * u);
                    }
                    g.Restore(st);
                    break;
                }
                case "search":
                {
                    float rr = w * 0.27f;
                    float ox = r.X + w * 0.1f + rr, oy = r.Y + w * 0.1f + rr;
                    g.DrawEllipse(pen, ox - rr, oy - rr, rr * 2, rr * 2);
                    g.DrawLine(pen, ox + rr * 0.72f, oy + rr * 0.72f, r.Right - w * 0.06f, r.Bottom - w * 0.06f);
                    break;
                }
            }
        }
    }

    public static void DrawTool(Graphics g, string tool, RectangleF r, Color col, float lw, bool on)
    {
        SmoothingMode sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        ToolPath(tool, r, on, g, col, lw);
        g.SmoothingMode = sm;
    }

    // Група у вигляді папки (плитка): плитка кольору групи з мініатюрами перших іконок
    public static Bitmap FolderTile(Theme t, Color col, List<Bitmap> kids, int size)
    {
        Bitmap b = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            Rectangle r = new Rectangle(1, 2, size - 3, size - 4);
            int rad = Math.Max(3, size * 20 / 100);
            Color bg = Util.Opaque(t.Glass ? Color.FromArgb(30, 34, 46) : t.Bg);
            Color baseCol = Util.Blend(bg, Util.Opaque(col), 0.34f);
            // «язичок» папки
            Rectangle tab = new Rectangle(r.X + size / 12, r.Y - 1, size * 36 / 100, Math.Max(3, size / 9));
            using (GraphicsPath pt = Util.Round(tab, 2, 2, 0, 0))
            using (SolidBrush sb = new SolidBrush(Util.Blend(baseCol, Util.Opaque(col), 0.35f)))
                g.FillPath(sb, pt);
            using (GraphicsPath pf = Util.Round(r, rad))
            {
                using (LinearGradientBrush lg = new LinearGradientBrush(r,
                    Util.Blend(baseCol, Color.White, 0.14f), baseCol, 90f))
                    g.FillPath(lg, pf);
                using (Pen pn = new Pen(Color.FromArgb(200, Util.Opaque(col)), Math.Max(1f, size * 0.04f)))
                    g.DrawPath(pn, pf);
            }
            int cells = 2;
            int pad = Math.Max(2, size * 9 / 100);
            int gap = Math.Max(1, size * 5 / 100);
            int cw = (Math.Min(r.Width, r.Height) - pad * 2 - gap) / cells;
            int tot = cw * 2 + gap;
            int gx = r.X + (r.Width - tot) / 2, gy = r.Y + (r.Height - tot) / 2;
            for (int i = 0; i < 4 && i < kids.Count; i++)
            {
                int cx = gx + (i % 2) * (cw + gap);
                int cy = gy + (i / 2) * (cw + gap);
                if (kids[i] != null) g.DrawImage(kids[i], new Rectangle(cx, cy, cw, cw));
            }
        }
        return b;
    }
}
