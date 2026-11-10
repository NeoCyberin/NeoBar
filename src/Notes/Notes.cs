using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// ======================================================================
//  Нотатки на робочому столі (у стилі «Липких нотаток» Windows)
//
//  * Текст форматується окремо для кожного символа (RichTextBox).
//  * Теми нотаток = теми панелі (темна, світла, скляна, кіберпанк, стімпанк, ...).
//    Кожна тема задає шрифт, колір тексту, шапку, фон і РАМКУ у власному стилі;
//    все це можна налаштувати окремо для кожної теми. Тема «Як на панелі» слідкує
//    за поточною темою панелі.
//  * Прозорість - лише фону (попіксельна альфа): текст і рамка завжди чіткі.
//  * Закріплюється лише область тексту (з рамкою); шапка й панель зникають.
//  * Закріплена нотатка - звичайне вікно: клік крізь неї (WS_EX_TRANSPARENT)
//    і положення внизу (HWND_BOTTOM), без вбудовування в Progman/WorkerW.
//
//  Тут - дані, збереження й рамки нотаток. Вікно нотатки - NoteForm.cs, елементи керування -
//  NoteControls.cs, емодзі й символи - NoteSymbols.cs, тло й вибір теми - NoteBackdrop.cs.
// ======================================================================

sealed class NoteTheme
{
    public string Id = "", Title = "";
    public Color Header, Body, Fg, Border, Accent2;
    public string FontName = "Segoe UI";
    public float FontSize = 11f;
    public int BorderW = 1, Radius = 6;
    public int Opacity = 90;          // типова прозорість ФОНУ для нових нотаток, 0..100
    public string Frame = "plain";    // стиль рамки: plain, double, plate, neon, hazard, heart, leaf, glass, sketch
    public bool Edited;               // користувач змінював тему (тоді вона зберігається в notes.ini)

    public NoteTheme Clone() { return (NoteTheme)MemberwiseClone(); }

    public void CopyFrom(NoteTheme o)
    {
        Header = o.Header; Body = o.Body; Fg = o.Fg; Border = o.Border; Accent2 = o.Accent2;
        FontName = o.FontName; FontSize = o.FontSize; BorderW = o.BorderW; Radius = o.Radius;
        Opacity = o.Opacity; Frame = o.Frame;
    }

    // тема нотатки з теми панелі
    public static NoteTheme From(Theme t)
    {
        NoteTheme n = new NoteTheme();
        n.Id = t.Id; n.Title = t.Title;
        n.Body = Util.Opaque(t.Bg);
        n.Fg = Util.Opaque(t.Text.A < 255 ? Util.Blend(n.Body, Util.Opaque(t.Text), t.Text.A / 255f) : t.Text);
        Color acc = Util.Opaque(t.Accent);
        n.Header = Util.Blend(n.Body, acc, 0.78f);
        n.FontName = t.Font;                                       // той самий шрифт, що й у панелі та вікнах цієї теми
        n.FontSize = t.Sketchy ? 12f : (t.Id == "cyber" ? 10.5f : 11f);
        n.BorderW = Math.Max(1, t.EdgeW);
        n.Radius = t.Radius;
        n.Opacity = t.Glass ? 55 : 92;
        n.Accent2 = t.Palette != null && t.Palette.Length > 1 ? Util.Opaque(t.Palette[1]) : Util.Blend(acc, Color.White, 0.45f);
        n.Border = t.Edge;
        switch (t.Style)
        {
            case IconStyle.Neon: n.Frame = "neon"; n.Border = acc; break;
            case IconStyle.Plate: n.Frame = "plate"; break;
            case IconStyle.Spark: n.Frame = "hazard"; n.Border = acc; break;
            case IconStyle.Heart: n.Frame = "heart"; break;
            case IconStyle.Leaf: n.Frame = "leaf"; break;
            case IconStyle.Glass: n.Frame = "glass"; break;
            case IconStyle.Sketch: n.Frame = "sketch"; break;
            case IconStyle.Tile: n.Frame = "double"; n.Border = Util.Blend(Util.Opaque(t.Edge), acc, 0.55f); break;
            default: n.Frame = "plain"; n.Border = Util.Blend(Util.Opaque(t.Edge), acc, 0.55f); break;
        }
        return n;
    }
}

sealed class Note
{
    public string Id = "";
    public Rectangle R = new Rectangle(200, 180, 320, 300);   // вікно редагування (з шапкою й панеллю)
    public string Theme = "auto";                              // id теми або auto (як на панелі)
    public int Opacity = -1;                                   // прозорість фону 0..100 (-1: типова для теми)
    public bool Locked;
    public int Size = 1;                                       // розмір тексту: 0 малий, 1 стандартний, 2 великий
    public string Title = "";                                  // назва нотатки (показується в списку нотаток)
    public string Text = "";
    public string Rtf = "";

    // підпис у списку: назва, а якщо її нема - початок тексту
    public string Label
    {
        get
        {
            string t = (Title ?? "").Replace("\r", "").Replace("\n", " ").Trim();
            if (t.Length == 0) return Preview;
            return Cut(t, 32);
        }
    }

    public string Preview
    {
        get
        {
            string s = (Text ?? "").Replace("\r", "").Replace("\n", " ").Trim();
            if (s.Length == 0) return L.T("порожня нотатка");
            return Cut(s, 24);
        }
    }

    // прибирає непарні сурогати й обрізає без розриву сурогатної пари (емодзі)
    static string Cut(string s, int max)
    {
        StringBuilder cl = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (char.IsHighSurrogate(ch) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { cl.Append(ch).Append(s[i + 1]); i++; }
            else if (!char.IsSurrogate(ch)) cl.Append(ch);
        }
        s = cl.ToString();
        if (s.Length <= max) return s;
        int k = max;
        if (char.IsHighSurrogate(s[k - 1])) k--;
        return s.Substring(0, k).TrimEnd() + "…";
    }
}

static class Notes
{
    public static readonly List<Note> All = new List<Note>();
    public static readonly List<NoteTheme> Themes = BuildThemes();
    static readonly Dictionary<Note, NoteForm> forms = new Dictionary<Note, NoteForm>();

    // закріплені нотатки (для замочка розблокування під мишею)
    public static List<KeyValuePair<Note, NoteForm>> LockedForms()
    {
        List<KeyValuePair<Note, NoteForm>> l = new List<KeyValuePair<Note, NoteForm>>();
        foreach (KeyValuePair<Note, NoteForm> kv in forms) if (kv.Key.Locked && kv.Value != null && !kv.Value.IsDisposed && kv.Value.Visible) l.Add(kv);
        return l;
    }
    static readonly System.Windows.Forms.Timer zTimer = new System.Windows.Forms.Timer();
    static readonly System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer();
    static string path = "";
    static bool started, dirty;
    public static float Scale = 1f;
    public static readonly float[] SizeK = { 1.0f, 1.3f, 1.6f };       // малий (як раніше), стандартний, великий
    public static readonly string[] SizeNames = { L.T("Малий"), L.T("Стандартний"), L.T("Великий") };
    public static float FontPt(Note n, NoteTheme t) { return t.FontSize * SizeK[Math.Max(0, Math.Min(2, n.Size))]; }
    public static string PanelId = "dark";

    public static int Px(float v) { return (int)Math.Round(v * Scale); }
    public static int Count { get { return All.Count; } }

    public static int LockedCount
    {
        get { int n = 0; foreach (Note x in All) if (x.Locked) n++; return n; }
    }

    public static event Action Changed;

    static void Notify()
    {
        Action a = Changed;
        if (a != null) try { a(); } catch { }
    }

    static List<NoteTheme> BuildThemes()
    {
        List<NoteTheme> l = new List<NoteTheme>();
        foreach (Theme t in global::Themes.All) l.Add(NoteTheme.From(t));
        return l;
    }

    public static NoteTheme ById(string id)
    {
        foreach (NoteTheme t in Themes) if (t.Id == id) return t;
        return null;
    }

    public static NoteTheme ThemeOf(Note n)
    {
        NoteTheme t = n.Theme == "auto" ? null : ById(n.Theme);
        if (t == null) t = ById(PanelId);
        return t ?? Themes[0];
    }

    public static NoteTheme DefaultTheme(string id)
    {
        foreach (Theme t in global::Themes.All) if (t.Id == id) return NoteTheme.From(t);
        return null;
    }

    public static int OpacityOf(Note n)
    {
        return n.Opacity >= 0 ? n.Opacity : ThemeOf(n).Opacity;
    }

    // панель змінила тему: нотатки «як на панелі» перемальовуються
    public static void SetPanelTheme(Theme t)
    {
        PanelId = t.Id;
        if (t.Id == "custom")
        {
            NoteTheme c = ById("custom");
            if (c != null && !c.Edited) c.CopyFrom(NoteTheme.From(t));
        }
        if (!started) return;
        foreach (KeyValuePair<Note, NoteForm> kv in new List<KeyValuePair<Note, NoteForm>>(forms))
            if (kv.Key.Theme == "auto" || ById(kv.Key.Theme) == null) kv.Value.ThemeUpdated();
    }

    public static void Start(string dir, float scale)
    {
        if (started) return;
        started = true;
        Scale = scale;
        SBFonts.Init(dir);
        ColorEmoji.Init();
        path = Path.Combine(dir, "notes.ini");
        Load();
        foreach (Note n in All) Open(n);

        // закріплені нотатки періодично повертаємо на дно, щоб клік по робочому столу їх не підняв
        zTimer.Interval = 1000;
        zTimer.Tick += delegate
        {
            foreach (NoteForm f in forms.Values) f.SendToBottom();
        };
        zTimer.Start();

        saveTimer.Interval = 700;
        saveTimer.Tick += delegate { saveTimer.Stop(); if (dirty) Save(); };
    }

    public static void Shutdown()
    {
        if (!started) return;
        zTimer.Stop();
        saveTimer.Stop();
        Save();
        foreach (NoteForm f in new List<NoteForm>(forms.Values)) { try { f.Dispose(); } catch { } }
        forms.Clear();
        started = false;
    }

    public static void Touch()
    {
        dirty = true;
        saveTimer.Stop();
        saveTimer.Start();
    }

    // ---------- файл ----------

    static string Esc(string s)
    {
        return (s ?? "").Replace("\\", "\\\\").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\n");
    }

    static string Unesc(string s)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                char d = s[++i];
                if (d == 'n') sb.Append("\r\n");
                else sb.Append(d);
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static void Load()
    {
        All.Clear();
        if (!File.Exists(path)) return;
        Note cur = null;
        NoteTheme th = null;
        try
        {
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (line == "[note]") { cur = new Note(); th = null; All.Add(cur); continue; }
                if (line == "[theme]") { th = new NoteTheme(); th.Id = ""; cur = null; pendingTheme = null; continue; }
                if (line.StartsWith("[")) { cur = null; th = null; continue; }
                int eq = raw.IndexOf('=');
                if (eq < 0) continue;
                string k = raw.Substring(0, eq).Trim().ToLowerInvariant();
                string v = raw.Substring(eq + 1);
                if (th != null) { LoadThemeLine(ref th, k, v); continue; }
                if (cur == null) continue;
                switch (k)
                {
                    case "id": cur.Id = v.Trim(); break;
                    case "rect":
                    {
                        string[] p = v.Split(',');
                        int x, y, w, h;
                        if (p.Length == 4 && int.TryParse(p[0], out x) && int.TryParse(p[1], out y) &&
                            int.TryParse(p[2], out w) && int.TryParse(p[3], out h))
                            cur.R = new Rectangle(x, y, Math.Max(NoteForm.MinW, w), Math.Max(NoteForm.MinH, h));
                        break;
                    }
                    case "theme": cur.Theme = v.Trim(); break;
                    case "opacity":
                    {
                        int o;
                        if (int.TryParse(v.Trim(), out o)) cur.Opacity = Math.Max(0, Math.Min(100, o));
                        break;
                    }
                    case "locked": cur.Locked = v.Trim() == "1"; break;
                    case "size": { int o; if (int.TryParse(v.Trim(), out o)) cur.Size = Math.Max(0, Math.Min(2, o)); break; }
                    case "title": cur.Title = Unesc(v); break;
                    case "text": cur.Text = Unesc(v); break;
                    case "rtf": cur.Rtf = Unesc(v); break;
                }
            }
        }
        catch { }
        foreach (Note n in All)
        {
            if (n.Id.Length == 0) n.Id = NewId();
            if (n.Theme != "auto" && ById(n.Theme) == null) n.Theme = "auto";
            n.R = Visible(n.R);
        }
    }

    static Theme Themes_Get(string id) { foreach (Theme t in global::Themes.All) if (t.Id == id) return t; return null; }

    // рядок секції [theme]: застосовується до вже наявної теми з таким id
    static NoteTheme pendingTheme;
    static void LoadThemeLine(ref NoteTheme th, string k, string v)
    {
        if (k == "id")
        {
            NoteTheme t = ById(v.Trim());
            pendingTheme = t;
            if (t != null) t.Edited = true;
            return;
        }
        NoteTheme d = pendingTheme;
        if (d == null) return;
        switch (k)
        {
            case "header": { Color c = Util.ParseColor(v); if (!c.IsEmpty) d.Header = Util.Opaque(c); break; }
            case "body": { Color c = Util.ParseColor(v); if (!c.IsEmpty) d.Body = Util.Opaque(c); break; }
            case "fg": { Color c = Util.ParseColor(v); if (!c.IsEmpty) d.Fg = Util.Opaque(c); break; }
            case "border": { Color c = Util.ParseColor(v); if (!c.IsEmpty) d.Border = c; break; }
            case "font":
            {
                string fv = v.Trim();
                Theme old = Themes_Get(d.Id);
                // збережений "старий" типовий шрифт теми замінюємо на новий власний
                if (fv.Length > 0 && !(old != null && string.Equals(fv, old.Font, StringComparison.OrdinalIgnoreCase)))
                    d.FontName = fv;
                break;
            }
            case "size":
            {
                float f;
                if (float.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                    d.FontSize = Math.Max(6f, Math.Min(72f, f));
                break;
            }
            case "borderw": { int o; if (int.TryParse(v.Trim(), out o)) d.BorderW = Math.Max(1, Math.Min(6, o)); break; }
            case "radius": { int o; if (int.TryParse(v.Trim(), out o)) d.Radius = Math.Max(0, Math.Min(24, o)); break; }
            case "opacity": { int o; if (int.TryParse(v.Trim(), out o)) d.Opacity = Math.Max(0, Math.Min(100, o)); break; }
        }
    }

    public static void Save()
    {
        if (path.Length == 0) return;
        dirty = false;
        try
        {
            foreach (NoteForm f in forms.Values) f.Pull();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(L.T("# NeoBar - нотатки на робочому столі"));
            sb.AppendLine();
            foreach (NoteTheme t in Themes)
            {
                if (!t.Edited) continue;     // змінені користувачем теми
                sb.AppendLine("[theme]");
                sb.AppendLine("id=" + t.Id);
                sb.AppendLine("header=" + Util.Hex(t.Header));
                sb.AppendLine("body=" + Util.Hex(t.Body));
                sb.AppendLine("fg=" + Util.Hex(t.Fg));
                sb.AppendLine("border=" + Util.Hex(t.Border));
                sb.AppendLine("font=" + t.FontName);
                sb.AppendLine("size=" + t.FontSize.ToString("0.#", CultureInfo.InvariantCulture));
                sb.AppendLine("borderw=" + t.BorderW);
                sb.AppendLine("radius=" + t.Radius);
                sb.AppendLine("opacity=" + t.Opacity);
                sb.AppendLine();
            }
            foreach (Note n in All)
            {
                sb.AppendLine("[note]");
                sb.AppendLine("id=" + n.Id);
                sb.AppendLine("rect=" + n.R.X + "," + n.R.Y + "," + n.R.Width + "," + n.R.Height);
                sb.AppendLine("theme=" + n.Theme);
                if (n.Opacity >= 0) sb.AppendLine("opacity=" + n.Opacity);
                sb.AppendLine("locked=" + (n.Locked ? "1" : "0"));
                sb.AppendLine("size=" + n.Size);
                sb.AppendLine("title=" + Esc(n.Title));
                sb.AppendLine("text=" + Esc(n.Text));
                sb.AppendLine("rtf=" + Esc(n.Rtf));
                sb.AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }
        catch { }
    }

    static string NewId()
    {
        return DateTime.Now.Ticks.ToString("x") + new Random().Next(4096).ToString("x");
    }

    static Rectangle Visible(Rectangle r)
    {
        foreach (Screen s in Screen.AllScreens)
        {
            Rectangle i = Rectangle.Intersect(s.WorkingArea, r);
            if (i.Width >= 60 && i.Height >= 40) return r;
        }
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        return new Rectangle(wa.Left + Px(80), wa.Top + Px(80), r.Width, r.Height);
    }

    static Rectangle DefaultRect()
    {
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        int w = Px(320), h = Px(300);
        int off = (All.Count % 8) * Px(32);
        return new Rectangle(wa.Left + Px(200) + off, wa.Top + Px(160) + off, w, h);
    }

    // ---------- керування ----------

    static void Open(Note n)
    {
        NoteForm f = new NoteForm(n);
        forms[n] = f;
        NoteUnlock.Ensure();
        f.Prepare();
        f.Show();
    }

    public static void New() { New("auto"); }

    public static void New(string theme)
    {
        Note n = new Note();
        n.Id = NewId();
        n.R = DefaultRect();
        n.Theme = theme;
        All.Add(n);
        Open(n);
        forms[n].Edit();
        Save();
        Notify();
    }

    public static void Delete(Note n)
    {
        NoteForm f;
        if (forms.TryGetValue(n, out f)) { forms.Remove(n); try { f.Dispose(); } catch { } }
        All.Remove(n);
        Save();
        Notify();
    }

    public static void SetLocked(Note n, bool locked)
    {
        NoteForm f;
        if (forms.TryGetValue(n, out f)) f.SetLocked(locked);
        else n.Locked = locked;
        Save();
        Notify();
    }

    // клік у списку нотаток: показати нотатку, не змінюючи закріплення (закріплюють перемикач і замочок)
    public static void Reveal(Note n)
    {
        NoteForm f;
        if (forms.TryGetValue(n, out f) && f != null && !f.IsDisposed) f.Reveal();
    }

    public static void LockAll() { foreach (Note n in All) SetLocked(n, true); }
    public static void UnlockAll() { foreach (Note n in All) SetLocked(n, false); }
    public static void Modified() { Notify(); }

    public static void SetNoteTheme(Note n, string id)
    {
        n.Theme = id;
        NoteForm f;
        if (forms.TryGetValue(n, out f)) f.ThemeUpdated();
        Touch();
    }

    public static void SetNoteOpacity(Note n, int v)
    {
        n.Opacity = Math.Max(0, Math.Min(100, v));
        Touch();
    }

    // тему змінили (кольори, шрифт, рамка) - оновлюємо всі нотатки цієї теми
    public static void ThemeChanged(NoteTheme t)
    {
        t.Edited = true;
        foreach (KeyValuePair<Note, NoteForm> kv in new List<KeyValuePair<Note, NoteForm>>(forms))
            if (ThemeOf(kv.Key) == t) kv.Value.ThemeUpdated();
        Touch();
    }

    public static void ResetTheme(NoteTheme t)
    {
        NoteTheme d = DefaultTheme(t.Id);
        if (d == null) return;
        t.CopyFrom(d);
        t.Edited = false;
        foreach (KeyValuePair<Note, NoteForm> kv in new List<KeyValuePair<Note, NoteForm>>(forms))
            if (ThemeOf(kv.Key) == t) kv.Value.ThemeUpdated();
        Touch();
    }

    public static void EditTheme(NoteTheme t)
    {
        using (ThemeDialog d = new ThemeDialog(t)) d.ShowDialog();
        Save();
    }

    // ---------- чиста математика: два рендери тексту (на чорному і на білому) -> піксельна альфа ----------
    // onBlack/onWhite: BGRA. layer: BGRA непрозорий шар фону (колір теми + візерунок).
    // Повертає преміультиплікований BGRA: текст непрозорий, фон - шар із прозорістю opacity.
    public static byte[] Composite(byte[] onBlack, byte[] onWhite, int w, int h, byte[] layer, int opacity)
    {
        byte[] pm = new byte[w * h * 4];
        int a0 = Math.Max(0, Math.Min(255, opacity * 255 / 100));
        int n = w * h;
        for (int p = 0; p < n; p++)
        {
            int i = p * 4;
            int kb = onBlack[i], kg = onBlack[i + 1], kr = onBlack[i + 2];
            int d = ((onWhite[i + 2] - kr) + (onWhite[i + 1] - kg) + (onWhite[i] - kb)) / 3;
            int cov = 255 - d;                       // покриття символом 0..255
            if (cov < 0) cov = 0; else if (cov > 255) cov = 255;
            int bgA = (255 - cov) * a0 / 255;        // внесок фону
            int alpha = cov + bgA;
            if (alpha > 255) alpha = 255;
            int pb = kb + bgA * layer[i] / 255;
            int pg = kg + bgA * layer[i + 1] / 255;
            int pr = kr + bgA * layer[i + 2] / 255;
            if (pb > alpha) pb = alpha;
            if (pg > alpha) pg = alpha;
            if (pr > alpha) pr = alpha;
            pm[i] = (byte)pb; pm[i + 1] = (byte)pg; pm[i + 2] = (byte)pr; pm[i + 3] = (byte)alpha;
        }
        return pm;
    }

    // шар фону теми: суцільний колір + ледь помітний тематичний візерунок
    public static byte[] BackLayer(int w, int h, NoteTheme t, int seed)
    {
        using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(t.Body);
                try { NoteBackdrop.Draw(g, new Rectangle(0, 0, w, h), t, seed); } catch { }
            }
            byte[] l = ReadBits(bmp);
            for (int i = 3; i < l.Length; i += 4) l[i] = 255;
            return l;
        }
    }

    // накладає рамку (непрозору) і заокруглення на преміультиплікований буфер
    public static void ApplyFrame(byte[] pm, int w, int h, NoteTheme t, int seed)
    {
        int rad = Px(t.Radius);
        bool sketch = t.Frame == "sketch";
        using (Bitmap fb = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        using (Bitmap mb = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(fb))
            {
                g.Clear(Color.Transparent);
                NoteFrame.Draw(g, new Rectangle(0, 0, w, h), t, Color.Transparent, seed);
            }
            using (Graphics g = Graphics.FromImage(mb))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (!sketch && rad > 0)
                    using (GraphicsPath p = Util.Round(new Rectangle(0, 0, w, h), rad))
                        g.FillPath(Brushes.White, p);
                else g.Clear(Color.White);
            }
            byte[] f = ReadBits(fb), m = ReadBits(mb);
            int n = w * h;
            for (int p = 0; p < n; p++)
            {
                int i = p * 4;
                int ma = m[i + 3], fa = f[i + 3];
                for (int c = 0; c < 3; c++)
                {
                    int under = pm[i + c] * ma / 255;
                    int over = f[i + c] * fa / 255;
                    int v = over + under * (255 - fa) / 255;
                    pm[i + c] = (byte)(v > 255 ? 255 : v);
                }
                int a = fa + (pm[i + 3] * ma / 255) * (255 - fa) / 255;
                pm[i + 3] = (byte)(a > 255 ? 255 : a);
                for (int c = 0; c < 3; c++) if (pm[i + c] > pm[i + 3]) pm[i + c] = pm[i + 3];
            }
        }
    }

    public static byte[] ReadBits(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        BitmapData d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] buf = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), buf, y * w * 4, w * 4);
            return buf;
        }
        finally { bmp.UnlockBits(d); }
    }

    public static Color Contrast(Color c)
    {
        double l = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        return l > 150 ? Color.FromArgb(32, 32, 32) : Color.FromArgb(250, 250, 250);
    }
}

// ---------- рамка нотатки: кожна тема має власний стиль ----------

static class NoteFrame
{
    // товщина рамки (кільця) в пікселях
    public static int Inset(NoteTheme t)
    {
        int w = Notes.Px(t.BorderW);
        int extra = 0;
        switch (t.Frame)
        {
            case "double": case "plate": extra = Notes.Px(3); break;
            case "neon": case "hazard": case "heart": case "sketch": extra = Notes.Px(2); break;
        }
        return Math.Max(1, Math.Min(Notes.Px(8), w + extra));
    }

    static GraphicsPath RR(Rectangle r, int rad)
    {
        if (rad <= 0 || r.Width <= 2 * rad || r.Height <= 2 * rad) { GraphicsPath p = new GraphicsPath(); p.AddRectangle(r); return p; }
        return Util.Round(r, rad);
    }

    public static void Draw(Graphics g, Rectangle r, NoteTheme t, Color ringFill, int seed)
    {
        SmoothingMode old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int ins = Inset(t);
        int rad = Notes.Px(t.Radius);
        Color bc = t.Border;
        Rectangle inner = Rectangle.Inflate(r, -ins, -ins);
        int irad = Math.Max(0, rad - ins);
        float one = Math.Max(1f, Notes.Px(1));
        int half = Math.Max(1, ins / 2);
        Rectangle mid = Rectangle.Inflate(r, -half, -half);
        int mrad = Math.Max(0, rad - half);

        if (t.Frame == "sketch")
        {
            if (ringFill.A > 0)
                using (GraphicsPath ring = new GraphicsPath(FillMode.Alternate))
                {
                    ring.AddPath(RR(r, rad), false);
                    ring.AddPath(RR(inner, irad), false);
                    using (SolidBrush b = new SolidBrush(ringFill)) g.FillPath(b, ring);
                }
            float amp = Math.Max(0.6f, ins * 0.25f);
            using (GraphicsPath p1 = Sk.WobblyRound(mid, Math.Max(3, rad), new Random(seed), amp))
            using (Pen pen = new Pen(bc, Math.Max(1.4f, ins * 0.55f)))
                g.DrawPath(pen, p1);
            using (GraphicsPath p2 = Sk.WobblyRound(Rectangle.Inflate(mid, -1, -1), Math.Max(3, rad), new Random(seed + 7), amp))
            using (Pen pen = new Pen(Color.FromArgb(Math.Min(255, bc.A * 140 / 255), bc), Math.Max(1f, ins * 0.3f)))
                g.DrawPath(pen, p2);
            g.SmoothingMode = old;
            return;
        }

        // суцільне кільце кольору рамки
        Color ringCol = t.Frame == "hazard" ? Color.FromArgb(30, 30, 34) : bc;
        using (GraphicsPath ring = new GraphicsPath(FillMode.Alternate))
        {
            ring.AddPath(RR(r, rad), false);
            ring.AddPath(RR(inner, irad), false);
            using (SolidBrush b = new SolidBrush(ringCol)) g.FillPath(b, ring);
        }

        switch (t.Frame)
        {
            case "double":
            case "plate":
                using (GraphicsPath mp = RR(mid, mrad))
                using (Pen pen = new Pen(Color.FromArgb(110, 0, 0, 0), one))
                    g.DrawPath(pen, mp);
                if (t.Frame == "plate")
                {
                    float o = ins * 0.5f + rad * 0.18f;
                    float rv = Math.Max(1.5f, ins * 0.26f);
                    Color rc = Util.Blend(Util.Opaque(bc), Color.White, 0.55f);
                    PointF[] pts = { new PointF(r.X + o, r.Y + o), new PointF(r.Right - o, r.Y + o),
                                     new PointF(r.X + o, r.Bottom - o), new PointF(r.Right - o, r.Bottom - o) };
                    using (SolidBrush rb = new SolidBrush(rc))
                    using (Pen rp = new Pen(Color.FromArgb(140, 0, 0, 0), 1f))
                        foreach (PointF pt in pts)
                        {
                            g.FillEllipse(rb, pt.X - rv, pt.Y - rv, rv * 2, rv * 2);
                            g.DrawEllipse(rp, pt.X - rv, pt.Y - rv, rv * 2, rv * 2);
                        }
                }
                break;
            case "neon":
                // неонове світіння всередину + світла лінія по внутрішньому краю
                for (int k = 3; k >= 1; k--)
                {
                    Rectangle gr = Rectangle.Inflate(inner, -(k - 1) * Math.Max(1, Notes.Px(1)), -(k - 1) * Math.Max(1, Notes.Px(1)));
                    using (GraphicsPath gp = RR(gr, Math.Max(0, irad - k)))
                    using (Pen pen = new Pen(Color.FromArgb(70 / k, bc), one * (4 - k) + 1))
                        g.DrawPath(pen, gp);
                }
                using (GraphicsPath ip = RR(Rectangle.Inflate(inner, 0, 0), irad))
                using (Pen pen = new Pen(t.Accent2, one))
                    g.DrawPath(pen, ip);
                break;
            case "hazard":
                using (GraphicsPath mp = RR(mid, mrad))
                using (Pen pen = new Pen(bc, Math.Max(1f, ins - one)))
                {
                    pen.DashStyle = DashStyle.Custom;
                    pen.DashPattern = new float[] { 1.6f, 1.6f };
                    g.DrawPath(pen, mp);
                }
                break;
            case "heart":
                using (GraphicsPath mp = RR(mid, mrad))
                using (Pen pen = new Pen(Color.FromArgb(230, 255, 255, 255), one))
                {
                    pen.DashStyle = DashStyle.Dot;
                    g.DrawPath(pen, mp);
                }
                break;
            case "leaf":
                using (GraphicsPath ip = RR(inner, irad))
                using (Pen pen = new Pen(t.Accent2, one))
                    g.DrawPath(pen, ip);
                break;
            case "glass":
                using (GraphicsPath ip = RR(inner, irad))
                using (Pen pen = new Pen(Color.FromArgb(70, 255, 255, 255), one))
                    g.DrawPath(pen, ip);
                break;
        }
        g.SmoothingMode = old;
    }

    // мітка кольору теми для меню: шапка + фон + рамка
    public static Image Swatch(NoteTheme t)
    {
        Bitmap b = new Bitmap(16, 16);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Util.Round(new Rectangle(0, 0, 15, 15), 4))
            {
                g.SetClip(p);
                using (SolidBrush bd = new SolidBrush(t.Body)) g.FillRectangle(bd, 0, 0, 16, 16);
                using (SolidBrush h = new SolidBrush(t.Header)) g.FillRectangle(h, 0, 0, 16, 6);
                g.ResetClip();
                using (Pen pen = new Pen(Color.FromArgb(255, Util.Opaque(t.Border)), 1.6f)) g.DrawPath(pen, p);
            }
        }
        return b;
    }
}

// ---------- кнопки-піктограми (не забирають фокус у тексту) ----------
