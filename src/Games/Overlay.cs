using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// ======================================================================
//  Ігровий оверлей - окремий модуль (не залежить від віджета «ЦП та ОЗП»):
//  показники поверх гри у власному прозорому вікні, графіки, глобальна гаряча клавіша.
// ======================================================================
sealed class OvMetric
{
    public string Key;
    public bool Show, Graph;
    public OvMetric(string k, bool s, bool g) { Key = k; Show = s; Graph = g; }
}

// Вигляд оверлею (розташування, розмір, тема, показники): загальний і окремий для гри з власними налаштуваннями
sealed class LookProfile
{
    public int Theme, CustomBg, CustomAccent, Corner, OffX, OffY, Scale, Opacity, Interval, GraphW, GraphH, GraphSec;
    public bool Horizontal;
    public List<OvMetric> Metrics = new List<OvMetric>();

    public LookProfile Clone()
    {
        LookProfile p = (LookProfile)MemberwiseClone();
        p.Metrics = new List<OvMetric>();
        foreach (OvMetric m in Metrics) p.Metrics.Add(new OvMetric(m.Key, m.Show, m.Graph));
        return p;
    }

    // «ключ=значення;...»: без символів «|» і переведення рядка (лежить в одному рядку games.ini)
    public string Encode()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("theme=").Append(Theme).Append(";cbg=").Append(CustomBg).Append(";cac=").Append(CustomAccent)
          .Append(";corner=").Append(Corner).Append(";ox=").Append(OffX).Append(";oy=").Append(OffY)
          .Append(";scale=").Append(Scale).Append(";op=").Append(Opacity).Append(";row=").Append(Horizontal ? 1 : 0)
          .Append(";int=").Append(Interval).Append(";gw=").Append(GraphW).Append(";gh=").Append(GraphH).Append(";gs=").Append(GraphSec)
          .Append(";m=");
        for (int i = 0; i < Metrics.Count; i++)
            sb.Append(i > 0 ? "," : "").Append(Metrics[i].Key).Append(':').Append(Metrics[i].Show ? 1 : 0).Append(':').Append(Metrics[i].Graph ? 1 : 0);
        return sb.ToString();
    }

    // зміни поверх основи (якщо в рядку чогось бракує, береться з основи)
    public static LookProfile Decode(string s, LookProfile baseP)
    {
        LookProfile p = baseP != null ? baseP.Clone() : OverlayCfg.Capture();
        if (string.IsNullOrEmpty(s)) return p;
        try
        {
            foreach (string part in s.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string k = part.Substring(0, eq), v = part.Substring(eq + 1);
                int n; int.TryParse(v, out n);
                switch (k)
                {
                    case "theme": p.Theme = Math.Max(0, Math.Min(OvTheme.Names.Length - 1, n)); break;
                    case "cbg": p.CustomBg = n; break;
                    case "cac": p.CustomAccent = n; break;
                    case "corner": p.Corner = Math.Max(0, Math.Min(3, n)); break;
                    case "ox": p.OffX = Math.Max(0, Math.Min(2000, n)); break;
                    case "oy": p.OffY = Math.Max(0, Math.Min(2000, n)); break;
                    case "scale": p.Scale = Math.Max(50, Math.Min(250, n)); break;
                    case "op": p.Opacity = Math.Max(0, Math.Min(100, n)); break;
                    case "row": p.Horizontal = v == "1"; break;
                    case "int": p.Interval = Math.Max(100, Math.Min(2000, n)); break;
                    case "gw": p.GraphW = Math.Max(60, Math.Min(600, n)); break;
                    case "gh": p.GraphH = Math.Max(16, Math.Min(200, n)); break;
                    case "gs": p.GraphSec = Math.Max(5, Math.Min(300, n)); break;
                    case "m":
                    {
                        List<OvMetric> l = new List<OvMetric>();
                        foreach (string mp in v.Split(','))
                        {
                            string[] f = mp.Split(':');
                            if (f.Length < 3 || OverlayCfg.Info(f[0]) == null) continue;
                            l.Add(new OvMetric(f[0], f[1] == "1", f[2] == "1" && OverlayCfg.CanGraph(f[0])));
                        }
                        foreach (string[] c in OverlayCfg.Catalog)
                        {
                            bool have = false;
                            foreach (OvMetric m in l) if (m.Key == c[0]) have = true;
                            if (!have) l.Add(new OvMetric(c[0], false, false));
                        }
                        p.Metrics = l;
                        break;
                    }
                }
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return p;
    }
}

static class OverlayCfg
{
    public static bool On = true;          // завжди: оверлей вмикається для кожної гри окремо
    public static int Mode;               // завжди 0: лише коли на передньому плані гра з увімкненим оверлеєм
    public static bool PawnNo;             // користувач відмовився від завантаження модуля PawnIO
    public static int Theme;               // 0 - за темою панелі, 1..4 - готові, 5 - власна
    public static int CustomBg = Color.FromArgb(14, 16, 22).ToArgb();
    public static int CustomAccent = Color.FromArgb(255, 210, 70).ToArgb();
    public static int Corner;              // 0 ↖, 1 ↗, 2 ↙, 3 ↘
    public static int OffX = 14, OffY = 14;
    public static int Scale = 100;         // %
    public static int Opacity = 66;        // непрозорість фону, %
    public static bool Horizontal;
    public const bool Shadow = true;
    public static int Interval = 500;      // мс
    public static int GraphW = 180, GraphH = 38, GraphSec = 30;
    public static Keys KeyToggle = Keys.Control | Keys.Shift | Keys.F10;
    // обмежувач кадрів
    public static bool LimOn = true;       // гаряча клавіша вимикає до перезапуску NeoBar (не зберігається)
    public static Keys KeyLimit = Keys.Control | Keys.Shift | Keys.F11;
    public static List<OvMetric> Metrics = new List<OvMetric>();

    // ключ, назва в налаштуваннях, підпис в оверлеї, категорія (колір)
    public static readonly string[][] Catalog =
    {
        new[] { "fps", L.T("Частота кадрів (FPS)"), "FPS", "f" },
        new[] { "frame", L.T("Час кадру"), L.T("КАДР"), "f" },
        new[] { "avgft", L.T("Середній час кадру"), L.T("СЕР."), "f" },
        new[] { "low1", "1% low", "1%", "f" },
        new[] { "low01", "0.1% low", "0.1%", "f" },
        new[] { "maxft", L.T("Найдовший кадр (10 с)"), L.T("МАКС"), "f" },
        new[] { "stutter", L.T("Мікрозатримки"), L.T("ЗАТР."), "f" },
        new[] { "api", L.T("Графічний API"), "API", "f" },
        new[] { "gpu", L.T("ГП: завантаження"), L.T("ГП %"), "g" },
        new[] { "gclk", L.T("ГП: частота"), L.T("ГП МГц"), "g" },
        new[] { "gtemp", L.T("ГП: температура"), L.T("ГП °C"), "g" },
        new[] { "ghot", L.T("ГП: hotspot"), "HOT °C", "g" },
        new[] { "gpow", L.T("ГП: потужність"), L.T("ГП Вт"), "g" },
        new[] { "gfan", L.T("ГП: вентилятор"), "FAN", "g" },
        new[] { "gvolt", L.T("ГП: напруга"), L.T("ГП В"), "g" },
        new[] { "vram", L.T("Відеопам'ять: використання"), "VRAM", "g" },
        new[] { "mclk", L.T("Відеопам'ять: частота"), L.T("VRAM МГц"), "g" },
        new[] { "mtemp", L.T("Відеопам'ять: температура"), "VRAM °C", "g" },
        new[] { "cpu", L.T("ЦП: завантаження"), L.T("ЦП %"), "c" },
        new[] { "creal", L.T("ЦП: найзавантаженіший потік"), L.T("ЦП ПОТІК"), "c" },
        new[] { "cbusy", L.T("ЦП: працюючих потоків"), L.T("ПОТОКИ"), "c" },
        new[] { "cmhz", L.T("ЦП: частота"), L.T("ЦП ГГц"), "c" },
        new[] { "ctemp", L.T("ЦП: температура"), L.T("ЦП °C"), "c" },
        new[] { "cpow", L.T("ЦП: потужність"), L.T("ЦП Вт"), "c" },
        new[] { "ram", L.T("ОЗП: використання"), L.T("ОЗП"), "r" },
        new[] { "ping", L.T("Пінг до сервера гри"), L.T("ПІНГ"), "n" },
        new[] { "game", L.T("Назва гри"), L.T("ГРА"), "s" },
        new[] { "time", L.T("Годинник"), L.T("ЧАС"), "s" }
    };

    public static string[] Info(string key)
    {
        foreach (string[] c in Catalog) if (c[0] == key) return c;
        return null;
    }

    public static bool CanGraph(string key)
    {
        return key != "api" && key != "game" && key != "time";
    }

    // пояснення показника в налаштуваннях (порожньо - назва і так зрозуміла)
    public static string Describe(string key)
    {
        switch (key)
        {
            case "cpu": return L.T("Загальне навантаження всього процесора: середнє по всіх потоках. Може бути невисоким, навіть коли гра впирається в один потік.");
            case "creal": return L.T("Навантаження найзавантаженішого потоку процесора (логічного ядра), %. Близько 100% при низькому FPS - гра впирається в один потік ЦП (вузьке місце), і більший ЦП із тими ж потоками не допоможе.");
            case "cbusy": return L.T("Скільки потоків процесора зараз працюють (завантажені більш ніж на 20%), з усіх наявних. Показує, чи гра розподіляє роботу між ядрами.");
            case "low1": return L.T("FPS, нижче якого було 1% найповільніших кадрів: чим ближче до середнього FPS, тим плавніше гра.");
            case "low01": return L.T("FPS, нижче якого було 0.1% найповільніших кадрів: показує найрідкісніші, але помітні підвисання.");
            case "stutter": return L.T("Частка кадрів, що тривали помітно довше за сусідні (різкі підвисання).");
            case "maxft": return L.T("Найдовший кадр за останні 10 секунд.");
            case "ping": return L.T("Затримка до сервера в мережевій грі, мс. Беруться з'єднання самої гри: для TCP - RTT від Windows, для UDP (більшість шутерів) - ICMP-пінг сервера, адресу якого видно з пакетів гри (потрібні права адміністратора). Якщо сервер не відповідає на ICMP, показується «—». Це не внутрішній пінг гри, а час до її сервера. У процес гри нічого не впроваджується.");
            case "ghot": return L.T("Температура найгарячішої точки відеочіпа (hotspot); зазвичай вища за основну.");
        }
        return "";
    }

    static string path;

    static void Defaults()
    {
        Metrics.Clear();
        string[] show = { "fps", "frame", "low1", "gpu", "gtemp", "vram", "cpu", "ram" };
        foreach (string[] c in Catalog)
            Metrics.Add(new OvMetric(c[0], Array.IndexOf(show, c[0]) >= 0, c[0] == "frame"));
    }

    public static void ResetMetrics() { Defaults(); }

    // ---------- загальний вигляд і вигляд поточної гри ----------
    public static LookProfile Base;                 // загальний вигляд (overlay.ini)
    public static bool Editing;                     // відкрито налаштування: вони самі керують поточним виглядом

    public static LookProfile Capture()
    {
        LookProfile p = new LookProfile();
        p.Theme = Theme; p.CustomBg = CustomBg; p.CustomAccent = CustomAccent; p.Corner = Corner; p.OffX = OffX; p.OffY = OffY;
        p.Scale = Scale; p.Opacity = Opacity; p.Horizontal = Horizontal; p.Interval = Interval;
        p.GraphW = GraphW; p.GraphH = GraphH; p.GraphSec = GraphSec;
        foreach (OvMetric m in Metrics) p.Metrics.Add(new OvMetric(m.Key, m.Show, m.Graph));
        return p;
    }

    public static void Apply(LookProfile p)
    {
        if (p == null) return;
        Theme = p.Theme; CustomBg = p.CustomBg; CustomAccent = p.CustomAccent; Corner = p.Corner; OffX = p.OffX; OffY = p.OffY;
        Scale = p.Scale; Opacity = p.Opacity; Horizontal = p.Horizontal; Interval = p.Interval;
        GraphW = p.GraphW; GraphH = p.GraphH; GraphSec = p.GraphSec;
        List<OvMetric> l = new List<OvMetric>();
        foreach (OvMetric m in p.Metrics) l.Add(new OvMetric(m.Key, m.Show, m.Graph));
        Metrics = l;                                    // новий список: хто саме перебирає старий, не ламається
    }

    // вигляд для гри: власний (якщо в неї власні налаштування й вона його змінювала) або загальний
    public static void UseGame(Game g)
    {
        if (Editing) return;
        if (Base == null) Base = Capture();
        LookProfile own = g != null ? g.OwnLook() : null;
        Apply(own ?? Base);
    }

    public static void Load()
    {
        if (Base != null) Apply(Base);
        LoadFile();
        Base = Capture();
    }

    static void LoadFile()
    {
        path = Prof.File("overlay.ini");
        Defaults();
        try
        {
            if (!SafeFile.Exists(path)) return;

            foreach (string raw in SafeFile.ReadLines(path))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0 || raw.StartsWith("#")) continue;
                string k = raw.Substring(0, eq).Trim(), v = raw.Substring(eq + 1).Trim();
                int n; int.TryParse(v, out n);
                switch (k)
                {
                    case "pawnno": PawnNo = v == "1"; break;
                    case "theme": Theme = Math.Max(0, Math.Min(OvTheme.Names.Length - 1, n)); break;
                    case "custombg": CustomBg = n; break;
                    case "customaccent": CustomAccent = n; break;
                    case "corner": Corner = Math.Max(0, Math.Min(3, n)); break;
                    case "offx": OffX = Math.Max(0, Math.Min(2000, n)); break;
                    case "offy": OffY = Math.Max(0, Math.Min(2000, n)); break;
                    case "scale": Scale = Math.Max(50, Math.Min(250, n)); break;
                    case "opacity": Opacity = Math.Max(0, Math.Min(100, n)); break;
                    case "row": Horizontal = v == "1"; break;
                    case "interval": Interval = Math.Max(100, Math.Min(2000, n)); break;
                    case "graphw": GraphW = Math.Max(60, Math.Min(600, n)); break;
                    case "graphh": GraphH = Math.Max(16, Math.Min(200, n)); break;
                    case "graphsec": GraphSec = Math.Max(5, Math.Min(300, n)); break;
                    case "keytoggle": KeyToggle = (Keys)n; break;
                    case "keylimit": KeyLimit = (Keys)n; break;
                    case "metrics":
                    {
                        // key:показувати:графік, у порядку відображення
                        List<OvMetric> l = new List<OvMetric>();
                        foreach (string part in v.Split(','))
                        {
                            string[] f = part.Split(':');
                            if (f.Length < 3 || Info(f[0]) == null) continue;
                            l.Add(new OvMetric(f[0], f[1] == "1", f[2] == "1" && CanGraph(f[0])));
                        }
                        foreach (string[] c in Catalog)
                        {
                            bool have = false;
                            foreach (OvMetric m in l) if (m.Key == c[0]) have = true;
                            if (!have) l.Add(new OvMetric(c[0], false, false));
                        }
                        Metrics.Clear();
                        Metrics.AddRange(l);
                        break;
                    }
                }
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    public static void Save()
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(L.T("# NeoBar - ігровий оверлей"));
            sb.AppendLine("pawnno=" + (PawnNo ? 1 : 0));
            sb.AppendLine("theme=" + Theme);
            sb.AppendLine("custombg=" + CustomBg);
            sb.AppendLine("customaccent=" + CustomAccent);
            sb.AppendLine("corner=" + Corner);
            sb.AppendLine("offx=" + OffX);
            sb.AppendLine("offy=" + OffY);
            sb.AppendLine("scale=" + Scale);
            sb.AppendLine("opacity=" + Opacity);
            sb.AppendLine("row=" + (Horizontal ? 1 : 0));
            sb.AppendLine("interval=" + Interval);
            sb.AppendLine("graphw=" + GraphW);
            sb.AppendLine("graphh=" + GraphH);
            sb.AppendLine("graphsec=" + GraphSec);
            sb.AppendLine("keytoggle=" + (int)KeyToggle);
            sb.AppendLine("keylimit=" + (int)KeyLimit);
            List<string> ms = new List<string>();
            foreach (OvMetric m in Metrics) ms.Add(m.Key + ":" + (m.Show ? 1 : 0) + ":" + (m.Graph ? 1 : 0));
            sb.AppendLine("metrics=" + string.Join(",", ms.ToArray()));
            SafeFile.Write(path, sb.ToString(), new UTF8Encoding(true));
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    public static string KeyText(Keys k)
    {
        if ((k & Keys.KeyCode) == Keys.None) return L.T("Немає");
        List<string> p = new List<string>();
        if ((k & Keys.Control) != 0) p.Add("Ctrl");
        if ((k & Keys.Alt) != 0) p.Add("Alt");
        if ((k & Keys.Shift) != 0) p.Add("Shift");
        Keys kc = k & Keys.KeyCode;
        if (kc >= Keys.D0 && kc <= Keys.D9) p.Add(((int)(kc - Keys.D0)).ToString());
        else if (kc >= Keys.NumPad0 && kc <= Keys.NumPad9) p.Add("Num" + (int)(kc - Keys.NumPad0));
        else if (kc == Keys.Space) p.Add(L.T("Пробіл"));
        else p.Add(kc.ToString());
        return string.Join("+", p.ToArray());
    }
}

// ----------------------------------------------------------------------
//  Значення показників (для власного вікна, OSD RTSS і перегляду в налаштуваннях)
// ----------------------------------------------------------------------
static class OvValues
{
    static readonly CultureInfo ic = CultureInfo.InvariantCulture;

    public static Color Cat(string c)
    {
        OvPalette p = OvTheme.Get();
        switch (c)
        {
            case "f": return p.F;
            case "c": return p.C;
            case "g": return p.G;
            case "r": return p.R;
        }
        return p.S;
    }

    static string D(double v, string fmt, string unit)
    {
        return double.IsNaN(v) || double.IsInfinity(v) ? "—" : v.ToString(fmt, ic) + unit;
    }

    public static string Value(string k, bool demo)
    {
        if (demo) return Demo(k);
        FrameStats f = Sensors.Frame;
        switch (k)
        {
            case "fps": return float.IsNaN(Sensors.FpsNow) ? FpsHint() : Math.Round(Sensors.FpsNow).ToString(ic);
            case "frame": return D(f.FrameMs, "0.0", L.T(" мс"));
            case "avgft": return D(f.AvgMs, "0.0", L.T(" мс"));
            case "low1": return D(f.Low1, "0", "");
            case "low01": return D(f.Low01, "0", "");
            case "maxft": return D(f.MaxMs, "0.0", L.T(" мс"));
            case "stutter": return D(f.StutterPct, "0.0", "%");
            case "api": return f.Api.Length > 0 ? f.Api : "—";
            case "gpu": return Sensors.F0(Sensors.GpuLoad, "%");
            case "gclk": return Sensors.F0(Sensors.GpuClock, L.T(" МГц"));
            case "gtemp": return Sensors.F0(Sensors.GpuTemp, "°C");
            case "ghot": return Sensors.F0(Sensors.Hotspot, "°C");
            case "gpow": return !float.IsNaN(Sensors.GpuPowerW) ? Sensors.F0(Sensors.GpuPowerW, L.T(" Вт")) : Sensors.F0(Sensors.GpuPower, "% TDP");
            case "gfan": return Sensors.GpuFan > 0 ? Sensors.GpuFan + L.T(" об/хв") : Sensors.F0(Sensors.GpuFanPct, "%");
            case "gvolt": return float.IsNaN(Sensors.GpuVolt) ? "—" : (Sensors.GpuVolt / 1000f).ToString("0.000", ic) + L.T(" В");
            case "vram": return Sensors.VramTotalGb <= 0 ? "—" : Sensors.VramUsedGb.ToString("0.0", ic) + L.T(" ГБ");
            case "mclk": return Sensors.F0(Sensors.MemClock, L.T(" МГц"));
            case "mtemp": return Sensors.F0(Sensors.MemTemp, "°C");
            case "cpu": return Sensors.F0(Sensors.Cpu, "%");
            case "creal": return Sensors.F0(Sensors.CpuPeak, "%");
            case "cbusy": return Sensors.CpuBusy < 0 ? "—" : Sensors.CpuBusy + (Sensors.CpuThreads > 0 ? L.T(" з ") + Sensors.CpuThreads : "");
            case "cmhz": return float.IsNaN(Sensors.CpuMhz) ? "—" : (Sensors.CpuMhz / 1000f).ToString("0.00", ic) + L.T(" ГГц");
            case "ctemp": return Sensors.F0(Sensors.CpuTemp, "°C");
            case "cpow": return Sensors.F0(Sensors.CpuPowerW, L.T(" Вт"));
            case "ram": return Sensors.RamTotalGb <= 0 ? "—" : Sensors.RamUsedGb.ToString("0.0", ic) + L.T(" ГБ");
            case "ping": return D(GamePing.Current, "0", L.T(" мс"));
            case "game": return Games.CurrentName.Length > 0 ? Games.CurrentName : "—";
            case "time": return DateTime.Now.ToString("HH:mm");
        }
        return "";
    }

    // чому немає FPS - коротко, просто в оверлеї
    static string FpsHint()
    {
        if (Games.CurrentPid <= 0) return "—";
        if (!Elevated.IsAdmin) return L.T("адмін?");
        return "—";
    }

    static string Demo(string k)
    {
        switch (k)
        {
            case "fps": return "144"; case "frame": return L.T("6.9 мс"); case "avgft": return L.T("7.0 мс"); case "low1": return "112";
            case "low01": return "96"; case "maxft": return L.T("14.2 мс"); case "stutter": return "0.4%"; case "api": return "DirectX 12";
            case "gpu": return "97%"; case "gclk": return L.T("2450 МГц"); case "gtemp": return "63°C"; case "ghot": return "78°C";
            case "gpow": return L.T("212 Вт"); case "gfan": return L.T("1450 об/хв"); case "gvolt": return L.T("1.050 В"); case "vram": return L.T("6.2 ГБ");
            case "mclk": return L.T("2500 МГц"); case "mtemp": return "70°C"; case "cpu": return "34%"; case "creal": return "92%"; case "cbusy": return L.T("3 з 16"); case "cmhz": return L.T("4.65 ГГц");
            case "ctemp": return "61°C"; case "cpow": return L.T("65 Вт"); case "ram": return L.T("11.4 ГБ"); case "ping": return L.T("38 мс"); case "game": return L.T("Гра");
            case "time": return DateTime.Now.ToString("HH:mm");
        }
        return "";
    }

    // дані графіка
    public static float[] Graph(string k, bool demo, int points)
    {
        if (demo)
        {
            float[] d = new float[Math.Max(2, points)];
            Random r = new Random(k.GetHashCode());
            for (int i = 0; i < d.Length; i++) d[i] = k == "frame" ? 6.5f + (float)r.NextDouble() * 1.5f + (i % 37 == 0 ? 6 : 0) : 50 + (float)Math.Sin(i / 6.0) * 20 + (float)r.NextDouble() * 6;
            return d;
        }
        if (k == "frame") return Sensors.Frame.Recent;
        List<float> h = Sensors.History(k);
        if (h == null) return new float[0];
        int n = Math.Min(points, h.Count);
        return h.GetRange(h.Count - n, n).ToArray();
    }

    public static float GraphMax(string k, float[] d)
    {
        float mx = 0;
        foreach (float v in d) if (!float.IsNaN(v)) mx = Math.Max(mx, v);
        switch (k)
        {
            case "gpu": case "cpu": case "ram": case "vram": case "stutter": case "creal": return 100;
            case "cbusy": return Math.Max(Sensors.CpuThreads, 1);
            case "gtemp": case "ghot": case "mtemp": case "ctemp": return Math.Max(100, mx);
            case "frame": case "avgft": case "maxft": return Math.Max(20, mx * 1.15f);
            case "fps": case "low1": case "low01": return Math.Max(60, mx * 1.1f);
        }
        return mx <= 0 ? 1 : mx * 1.15f;
    }
}

// ----------------------------------------------------------------------
//  Вікно оверлея (прозоре для миші, поверх усього)
// ----------------------------------------------------------------------
sealed class OverlayForm : LayeredForm
{
    bool shown;

    public OverlayForm()
    {
        ExtraEx = 0x20 | 0x8 | 0x08000000;   // TRANSPARENT (кліки крізь) | TOPMOST | NOACTIVATE
        TopMost = true;
        Text = "NeoBar overlay";
    }

    public void HideOv()
    {
        ResetWidths();
        if (shown) { Hide(); shown = false; }
    }

    public void ShowOv(float dpi, string note, bool rows)
    {
        Screen scr = Screen.FromHandle(Win.GetForegroundWindow());
        using (Bitmap b = Render(dpi, false, note, rows))
        {
            Rectangle sb = scr.Bounds;
            int ox = (int)(OverlayCfg.OffX * dpi), oy = (int)(OverlayCfg.OffY * dpi);
            int x = OverlayCfg.Corner % 2 == 0 ? sb.Left + ox : sb.Right - b.Width - ox;
            int y = OverlayCfg.Corner < 2 ? sb.Top + oy : sb.Bottom - b.Height - oy;
            Bounds = new Rectangle(x, y, b.Width, b.Height);
            Push(b, new Point(x, y));
        }
        bool first = !shown;
        if (first) { Win.ShowWindow(Handle, 4); shown = true; }
        // «Поверх усіх» підтверджуємо лише коли потрібно: при появі, при зміні активного вікна
        // або коли над нами опинилось вікно гри (деякі ігри піднімають себе вже після запуску).
        // Чужі оверлеї (Discord тощо) не чіпаємо - інакше виходить боротьба за верхню позицію і блимання.
        IntPtr fg = Win.GetForegroundWindow();
        if (first || fg != lastFg || ((DateTime.Now - lastTop).TotalSeconds >= 1 && GameAboveUs()))
        {
            lastFg = fg; lastTop = DateTime.Now;
            try { Win.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
    }


    // чи є над нашим вікном видиме вікно процесу гри
    bool GameAboveUs()
    {
        int gp = Games.CurrentPid;
        if (gp <= 0) return false;
        IntPtr w = Handle;
        for (int i = 0; i < 200; i++)
        {
            w = Win.GetWindow(w, 3);                 // GW_HWNDPREV - вікно вище в порядку накладання
            if (w == IntPtr.Zero) return false;
            if (!Win.IsWindowVisible(w)) continue;
            int pid;
            Win.GetWindowThreadProcessId(w, out pid);
            if (pid == gp) return true;
        }
        return false;
    }

    IntPtr lastFg;
    DateTime lastTop = DateTime.MinValue;

    static bool lightTheme;
    static readonly Dictionary<string, int> valSeen = new Dictionary<string, int>();
    public static void ResetWidths() { valSeen.Clear(); }
    static readonly string[] SensorKeys = { "gclk", "ghot", "gpow", "gfan", "gvolt", "mclk", "mtemp", "cmhz", "ctemp", "cpow" };

    public static float RefScale;     // реальний масштаб (DPI) - щоб перегляд з іншим зумом переносив рядки так само
    public static bool Opaque;        // малюємо для вставки в кадр гри: суцільний фон і прямі кути (прозорості там немає)
    public static Size FrameBox;      // розмір кадру гри, якщо картинка йде в кадр: рядки переносяться по ньому, а не по екрану

    public static Bitmap Render(float sc, bool demo, string note, bool rowsOn)
    {
        float k = OverlayCfg.Scale / 100f * sc;
        OvPalette pal = OvTheme.Get();
        lightTheme = pal.Light;
        // графік будується лише для показаного показника і стоїть одразу під ним
        List<OvMetric> rows = new List<OvMetric>();
        if (rowsOn) foreach (OvMetric m in OverlayCfg.Metrics) if (m.Show) rows.Add(m);
        // показники датчиків, яких немає в цій системі (наприклад, температура ЦП без драйвера), не займають місце
        if (!demo)
            rows.RemoveAll(delegate (OvMetric m) { return Array.IndexOf(SensorKeys, m.Key) >= 0 && OvValues.Value(m.Key, false) == "—"; });
        if (rows.Count == 0 && rowsOn) rows.Add(new OvMetric("fps", true, false));
        int points = Math.Max(10, OverlayCfg.GraphSec * 1000 / Math.Max(100, OverlayCfg.Interval));
        string fam = OvTheme.FontName(pal.Font);
        using (Font fl = new Font(fam, 7.5f * k, FontStyle.Bold, GraphicsUnit.Point))
        using (Font fv = new Font(fam, 9.5f * k, FontStyle.Bold, GraphicsUnit.Point))
        using (Font fs = new Font(fam, 6.5f * k, FontStyle.Regular, GraphicsUnit.Point))
        {
            int pad = (int)(8 * k), rowH = (int)(18 * k), labW = (int)(34 * k), valW = (int)(82 * k);
            int gw = (int)(OverlayCfg.GraphW * k), gh = (int)(OverlayCfg.GraphH * k), gap = (int)(4 * k);
            int capH = 0;                                              // без підпису над графіком
            // ширина підписів - за найдовшим із показаних ("ГП МГц", "VRAM °C"...)
            Dictionary<string, int> lwD = new Dictionary<string, int>(), vwD = new Dictionary<string, int>();
            using (Bitmap mb = new Bitmap(1, 1))
            using (Graphics mg = Graphics.FromImage(mb))
                foreach (OvMetric m in rows)
                {
                    string[] inf0 = OverlayCfg.Info(m.Key);
                    int lw0 = (int)Math.Ceiling(mg.MeasureString(inf0[2], fl).Width) - (int)(2 * k) + (int)(4 * k);
                    lwD[m.Key] = Math.Max((int)(20 * k), lw0) + (int)(4 * k);
                    labW = Math.Max(labW, lw0);
                    // ширина значень тільки зростає, поки оверлей видно, - щоб вікно не смикалось від "9%" до "100%"
                    int vw0 = (int)Math.Ceiling(mg.MeasureString(OvValues.Value(m.Key, demo), fv).Width) - (int)(2 * k) + (int)(6 * k);
                    string wk = m.Key + "|" + (int)(k * 100);
                    int seen;
                    if (!valSeen.TryGetValue(wk, out seen) || vw0 > seen) { seen = vw0; valSeen[wk] = vw0; }
                    vwD[m.Key] = Math.Max((int)(30 * k), seen);
                    valW = Math.Max(valW, seen);
                }
            int noteH = string.IsNullOrEmpty(note) ? 0 : rowH;
            bool horiz = OverlayCfg.Horizontal;
            bool anyGraph = false;
            foreach (OvMetric m in rows) if (m.Graph && OverlayCfg.CanGraph(m.Key)) anyGraph = true;
            int baseW = labW + valW;
            if (anyGraph && !horiz) baseW = Math.Max(baseW, gw);
            // розкладка: вертикально - один стовпчик; горизонтально - рядки з автоматичним переносом по ширині екрана
            int n = rows.Count;
            int[] px = new int[n], py = new int[n], pcw = new int[n], plw = new int[n], pvw = new int[n];
            int w, h;
            // доступна область: монітор мінус однакові відступи з обох боків (як у ShowOv)
            float refSc = RefScale > 0 ? RefScale : sc;
            int maxW = 100000, maxH = 100000;
            Screen scr = null;
            try { scr = Screen.FromHandle(Win.GetForegroundWindow()); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            try
            {
                if (scr == null) scr = Screen.PrimaryScreen;
                if (scr != null)
                {
                    float r = sc / refSc;
                    maxW = (int)((scr.Bounds.Width - 2 * (int)(OverlayCfg.OffX * refSc)) * r);
                    maxH = (int)((scr.Bounds.Height - 2 * (int)(OverlayCfg.OffY * refSc)) * r);
                }
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
            if (FrameBox.Width > 0 && FrameBox.Height > 0)
            {
                maxW = FrameBox.Width - 2 * (int)(OverlayCfg.OffX * sc);
                maxH = FrameBox.Height - 2 * (int)(OverlayCfg.OffY * sc);
            }
            maxW = Math.Max(maxW, (int)(240 * k));
            maxH = Math.Max(maxH - (noteH > 0 ? noteH + gap : 0), (int)(120 * k));
            int hg = (int)(10 * k);
            if (!horiz)
            {
                // один стовпчик; якщо не вміщується по висоті екрана - наступний стовпчик праворуч
                int x0 = pad, y0 = pad, cols = 1, lastBottom = pad;
                for (int i = 0; i < n; i++)
                {
                    bool gr = rows[i].Graph && OverlayCfg.CanGraph(rows[i].Key);
                    int mh = rowH + (gr ? gap + capH + gh : 0);
                    if (y0 > pad && y0 + mh > maxH - pad) { x0 += baseW + hg; y0 = pad; cols++; }
                    px[i] = x0; py[i] = y0; pcw[i] = baseW; plw[i] = labW; pvw[i] = baseW - labW;
                    y0 += mh + gap;
                    lastBottom = Math.Max(lastBottom, y0 - gap);
                }
                w = pad * 2 + baseW * cols + hg * (cols - 1);
                h = cols > 1 ? maxH : lastBottom + pad;       // кілька стовпчиків - на всю висоту, відступи зверху й знизу однакові
                // у правому куті стовпчики йдуть справа наліво: перший - біля краю екрана
                if (cols > 1 && OverlayCfg.Corner % 2 == 1)
                    for (int i = 0; i < n; i++) px[i] = w - px[i] - baseW;
            }
            else
            {
                // рядки з переносом; якщо рядків кілька - на всю ширину, відступи ліворуч і праворуч однакові
                int x0 = pad, y0 = pad, rowMax = 0, maxX = 0, rowNo = 0;
                int[] rowOf = new int[n];
                for (int i = 0; i < n; i++)
                {
                    bool gr = rows[i].Graph && OverlayCfg.CanGraph(rows[i].Key);
                    int lw = lwD[rows[i].Key], vw = vwD[rows[i].Key];
                    int cw = gr ? Math.Max(lw + vw, gw) : lw + vw;
                    int mh = rowH + (gr ? gap + capH + gh : 0);
                    if (x0 > pad && x0 + cw > maxW - pad) { x0 = pad; y0 += rowMax + gap; rowMax = 0; rowNo++; }
                    px[i] = x0; py[i] = y0; pcw[i] = cw; plw[i] = lw; pvw[i] = vw; rowOf[i] = rowNo;
                    x0 += cw + hg;
                    rowMax = Math.Max(rowMax, mh);
                    maxX = Math.Max(maxX, x0 - hg);
                }
                w = maxX + pad; h = y0 + rowMax + pad;
                if (rowNo > 0)
                {
                    // розтягуємо заповнені рядки до країв (крім останнього)
                    for (int r = 0; r < rowNo; r++)
                    {
                        int first = -1, last = -1;
                        for (int i = 0; i < n; i++) if (rowOf[i] == r) { if (first < 0) first = i; last = i; }
                        if (first < 0 || last == first) continue;
                        int slack = (maxW - pad) - (px[last] + pcw[last]);
                        if (slack <= 0) continue;
                        for (int i = first; i <= last; i++) px[i] += (int)((long)slack * (i - first) / (last - first));
                    }
                    w = maxW;
                }
            }
            h += noteH;
            w = Math.Max(w, pad * 2 + labW);
            Bitmap b = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                int a = Opaque ? 255 : (int)(255 * OverlayCfg.Opacity / 100.0);
                if (a > 0)
                {
                    int rad = Opaque ? 0 : (int)(pal.Radius * k);
                    using (Brush bg = Festive.Has(pal.Grad) ? (Brush)Festive.Brush(pal.Grad, new RectangleF(0, 0, w, h), w >= h ? 0f : 90f, 0.15f, a) : new SolidBrush(Color.FromArgb(a, pal.Bg)))
                        Util.FillRound(g, bg, new Rectangle(0, 0, w - 1, h - 1), rad);
                    if (pal.Glass)
                    {
                        // скляний відблиск у верхній половині
                        using (GraphicsPath cp = Util.Round(new RectangleF(0, 0, w - 1, h - 1), rad))
                        {
                            Region old = g.Clip;
                            g.SetClip(cp, CombineMode.Intersect);
                            using (LinearGradientBrush gl = new LinearGradientBrush(new RectangleF(0, 0, w, Math.Max(2, h * 0.55f)), Color.FromArgb(Math.Min(70, 30 + a / 6), 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
                                g.FillRectangle(gl, new RectangleF(0, 0, w, Math.Max(2, h * 0.55f)));
                            g.Clip = old;
                        }
                    }
                    if (pal.EdgeW > 0 && !pal.Edge.IsEmpty)
                    {
                        float ew = Math.Max(1f, pal.EdgeW * k), inset = ew / 2f;
                        using (GraphicsPath ep = Util.Round(new RectangleF(inset, inset, w - 1 - inset * 2, h - 1 - inset * 2), Math.Max(0, rad - inset)))
                        using (Pen pen = new Pen(Color.FromArgb(Math.Min(pal.Edge.A, Math.Max(a, 90)), pal.Edge), ew))
                            g.DrawPath(pen, ep);
                    }
                }
                using (StringFormat sf = new StringFormat())
                {
                    sf.LineAlignment = StringAlignment.Center;
                    sf.FormatFlags = StringFormatFlags.NoWrap;
                    for (int i = 0; i < n; i++)
                    {
                        OvMetric m = rows[i];
                        string[] inf = OverlayCfg.Info(m.Key);
                        bool gr = m.Graph && OverlayCfg.CanGraph(m.Key);
                        int x = px[i], y = py[i];
                        Color lc = OvValues.Cat(inf[3]);
                        sf.Alignment = StringAlignment.Near;
                        DrawStr(g, inf[2], fl, lc, new RectangleF(x, y, plw[i], rowH), sf);
                        sf.Alignment = horiz ? StringAlignment.Near : StringAlignment.Far;
                        DrawStr(g, OvValues.Value(m.Key, demo), fv, pal.Value, new RectangleF(x + plw[i], y, pvw[i], rowH), sf);
                        if (gr)
                            DrawGraph(g, new Rectangle(x, y + rowH + gap, pcw[i], capH + gh), capH, m.Key, inf, demo, points, fs, a);
                    }
                    if (noteH > 0)
                    {
                        sf.Alignment = StringAlignment.Center;
                        DrawStr(g, note, fl, Color.FromArgb(255, 210, 70), new RectangleF(0, h - pad - noteH, w, noteH), sf);
                    }
                }
            }
            return b;
        }
    }

    // r - блок графіка (capH - відступ зверху, зараз 0)
    static void DrawGraph(Graphics g, Rectangle r, int capH, string key, string[] inf, bool demo, int points, Font fs, int alpha)
    {
        Color c = OvValues.Cat(inf[3]);
        Rectangle plot = new Rectangle(r.X, r.Y + capH, r.Width, r.Height - capH);
        using (SolidBrush bg = new SolidBrush(Color.FromArgb(Math.Max(60, alpha / 2), OvTheme.Get().GraphBg))) g.FillRectangle(bg, plot);
        float[] d = OvValues.Graph(key, demo, points);
        float mx = OvValues.GraphMax(key, d);
        if (d.Length >= 2 && mx > 0)
        {
            PointF[] pts = new PointF[d.Length];
            for (int i = 0; i < d.Length; i++)
            {
                float v = float.IsNaN(d[i]) ? 0 : Math.Min(mx, d[i]);
                pts[i] = new PointF(plot.X + (float)i * (plot.Width - 1) / (d.Length - 1), plot.Bottom - 2 - v / mx * (plot.Height - 4));
            }
            using (Pen p = new Pen(c, 1.3f)) { p.LineJoin = LineJoin.Round; g.DrawLines(p, pts); }
        }
    }

    static void DrawStr(Graphics g, string s, Font f, Color c, RectangleF r, StringFormat sf)
    {
        if (OverlayCfg.Shadow && !lightTheme)
            using (SolidBrush sh = new SolidBrush(Color.FromArgb(200, 0, 0, 0))) g.DrawString(s, f, sh, new RectangleF(r.X + 1, r.Y + 1, r.Width, r.Height), sf);
        using (SolidBrush b = new SolidBrush(c)) g.DrawString(s, f, b, r, sf);
    }
}

// ----------------------------------------------------------------------
//  Теми оверлея
// ----------------------------------------------------------------------
sealed class OvPalette
{
    public Color Bg, Value, Label, F, C, G, R, S, GraphBg;
    public bool Light;
    public Color Edge = Color.Empty;     // рамка (як у тем панелі)
    public bool Glass;
    public Theme Grad;                   // тема з градієнтним тлом (тло оверлея - її градієнт)
    public int EdgeW = 0, Radius = 8;
    public string Font = "Segoe UI";
}

static class OvTheme
{
    public static readonly string[] Names = { L.T("Тематична (за темою панелі)"), L.T("М'ятна"), L.T("Неон"), L.T("Монохром"), L.T("Багряна"), L.T("Власна") };
    public const int Custom = 5;

    // поточна тема панелі (виставляє панель при зміні теми)
    public static Theme Panel;

    // назва шрифту зі списку "А|Б|В" - перший встановлений
    static readonly Dictionary<string, string> fontCache = new Dictionary<string, string>();
    public static string FontName(string list)
    {
        string r;
        if (fontCache.TryGetValue(list, out r)) return r;
        r = "Segoe UI";
        try
        {
            foreach (string n in list.Split('|'))
            {
                bool ok = false;
                foreach (FontFamily ff in FontFamily.Families) if (string.Equals(ff.Name, n, StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
                if (ok) { r = n; break; }
            }
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        fontCache[list] = r;
        return r;
    }

    static OvPalette Make(int bg, int val, int lab, int f, int c, int g, int r, int s, int gbg, bool light)
    {
        OvPalette p = new OvPalette();
        p.Bg = Color.FromArgb(bg); p.Value = Color.FromArgb(val); p.Label = Color.FromArgb(lab);
        p.F = Color.FromArgb(f); p.C = Color.FromArgb(c); p.G = Color.FromArgb(g); p.R = Color.FromArgb(r); p.S = Color.FromArgb(s);
        p.GraphBg = Color.FromArgb(gbg); p.Light = light;
        return p;
    }

    static OvPalette Deco(OvPalette p, int edge, int edgeW, int radius, string font)
    {
        p.Edge = Color.FromArgb(edge); p.EdgeW = edgeW; p.Radius = radius; p.Font = font;
        return p;
    }

    static OvPalette Festal(OvPalette p, Theme t) { p.Grad = t; return p; }

    static int Rgb(int r, int g, int b) { return Color.FromArgb(r, g, b).ToArgb(); }

    static OvPalette Dark()
    {
        return Make(Rgb(14, 16, 22), Rgb(255, 255, 255), Rgb(200, 200, 200), Rgb(255, 210, 70), Rgb(70, 200, 255), Rgb(120, 230, 120), Rgb(230, 140, 255), Rgb(220, 220, 220), Rgb(0, 0, 0), false);
    }

    static OvPalette LightP()
    {
        return Make(Rgb(240, 242, 246), Rgb(20, 22, 30), Rgb(70, 74, 86), Rgb(190, 110, 0), Rgb(0, 105, 185), Rgb(15, 135, 55), Rgb(150, 55, 170), Rgb(70, 74, 86), Rgb(206, 212, 222), true);
    }

    // тема оверлея за темою панелі
    static OvPalette FromPanel()
    {
        Theme t = Panel;
        string id = t == null ? "dark" : t.Id;
        switch (id)
        {
            case "light": return LightP();
            case "glass":
            {
                OvPalette g = Dark();
                g.Bg = Color.FromArgb(14, 20, 34); g.Value = Color.FromArgb(244, 247, 255); g.Label = Color.FromArgb(196, 206, 222);
                g.Glass = true; g.Edge = Color.FromArgb(120, 255, 255, 255); g.EdgeW = 1; g.Radius = 10;
                return g;
            }
            case "cyber":
                return Deco(Make(Rgb(9, 7, 22), Rgb(140, 240, 232), Rgb(190, 60, 255), Rgb(255, 46, 140), Rgb(0, 200, 255), Rgb(0, 255, 110), Rgb(190, 100, 255), Rgb(255, 230, 0), Rgb(0, 0, 0), false),
                            Rgb(255, 46, 140), 2, 3, "Consolas");
            case "steam":
                return Deco(Make(Rgb(43, 29, 20), Rgb(242, 220, 178), Rgb(190, 150, 100), Rgb(235, 150, 70), Rgb(218, 186, 84), Rgb(165, 195, 120), Rgb(210, 125, 95), Rgb(196, 166, 116), Rgb(20, 12, 8), false),
                            Rgb(184, 115, 51), 2, 8, "Cambria|Georgia|Segoe UI");
            case "urban":
                return Deco(Make(Rgb(30, 32, 36), Rgb(242, 242, 242), Rgb(255, 196, 0), Rgb(255, 196, 0), Rgb(0, 205, 225), Rgb(255, 130, 20), Rgb(255, 80, 100), Rgb(215, 215, 215), Rgb(0, 0, 0), false),
                            Rgb(255, 196, 0), 2, 0, "Bahnschrift|Segoe UI");
            case "pink":
                return Deco(Make(Rgb(255, 228, 240), Rgb(140, 20, 88), Rgb(190, 70, 135), Rgb(225, 30, 125), Rgb(150, 55, 195), Rgb(200, 40, 100), Rgb(235, 85, 150), Rgb(170, 55, 115), Rgb(255, 196, 222), true),
                            Rgb(255, 105, 180), 2, 14, "Segoe UI");
            case "nature":
                return Deco(Make(Rgb(19, 34, 25), Rgb(234, 246, 222), Rgb(150, 185, 130), Rgb(205, 232, 95), Rgb(110, 205, 170), Rgb(120, 212, 100), Rgb(225, 185, 115), Rgb(190, 215, 170), Rgb(10, 20, 12), false),
                            Rgb(110, 165, 80), 2, 10, "Segoe UI");
            case "art":
                return Deco(Make(Rgb(247, 242, 230), Rgb(54, 50, 58), Rgb(118, 106, 98), Rgb(196, 78, 64), Rgb(36, 128, 138), Rgb(76, 138, 64), Rgb(118, 78, 168), Rgb(118, 106, 98), Rgb(226, 218, 200), true),
                            Rgb(80, 76, 84), 2, 4, "Ink Free|Segoe Print|Segoe UI");
            // святкові теми
            // святкові теми: тло - градієнт теми (Grad), шрифт - шрифт теми
            case "halloween":
                return Festal(Deco(Make(Rgb(24, 15, 32), Rgb(255, 238, 214), Rgb(255, 156, 64), Rgb(255, 140, 30), Rgb(190, 120, 255), Rgb(150, 225, 80), Rgb(255, 96, 72), Rgb(232, 204, 170), Rgb(0, 0, 0), false),
                            Rgb(232, 116, 30), 2, 10, t.Font), t);
            case "xmas":
                return Festal(Deco(Make(Rgb(9, 17, 38), Rgb(240, 246, 255), Rgb(176, 204, 244), Rgb(255, 255, 255), Rgb(110, 175, 255), Rgb(120, 225, 240), Rgb(186, 166, 255), Rgb(200, 214, 236), Rgb(4, 10, 24), false),
                            Rgb(150, 190, 240), 2, 10, t.Font), t);
            case "easter":
                return Festal(Deco(Make(Rgb(252, 248, 238), Rgb(66, 58, 84), Rgb(120, 92, 150), Rgb(200, 76, 110), Rgb(52, 116, 200), Rgb(56, 132, 56), Rgb(170, 116, 16), Rgb(104, 94, 124), Rgb(236, 228, 246), true),
                            Rgb(190, 170, 215), 2, 14, t.Font), t);
            case "birthday":
                return Festal(Deco(Make(Rgb(255, 250, 246), Rgb(62, 42, 74), Rgb(190, 64, 120), Rgb(206, 52, 108), Rgb(40, 116, 206), Rgb(40, 140, 80), Rgb(196, 112, 10), Rgb(110, 90, 120), Rgb(240, 228, 236), true),
                            Rgb(236, 184, 208), 2, 14, t.Font), t);
            case "custom":
            {
                // власна тема панелі: її фон, текст і акцент
                Color bg = Util.Opaque(t.Bg), ac = Util.Opaque(t.Accent), tx = Util.Opaque(t.Text);
                bool light = Util.Lum(bg) > 150;
                int a = Rgb(ac.R, ac.G, ac.B);
                OvPalette p = Make(Rgb(bg.R, bg.G, bg.B), Rgb(tx.R, tx.G, tx.B), a, a, a, a, a, a, light ? Rgb(206, 212, 222) : Rgb(0, 0, 0), light);
                return Deco(p, a, 2, 8, "Segoe UI");
            }
        }
        return Dark();
    }

    public static OvPalette Get()
    {
        switch (Math.Max(0, Math.Min(Names.Length - 1, OverlayCfg.Theme)))
        {
            case 0: return FromPanel();
            case 1: return Make(Rgb(10, 24, 22), Rgb(240, 255, 250), Rgb(190, 215, 210), Rgb(255, 230, 120), Rgb(120, 220, 255), Rgb(120, 255, 190), Rgb(255, 170, 220), Rgb(220, 240, 235), Rgb(0, 0, 0), false);
            case 2: return Make(Rgb(8, 6, 22), Rgb(255, 255, 255), Rgb(200, 200, 235), Rgb(255, 60, 200), Rgb(0, 200, 255), Rgb(0, 255, 170), Rgb(190, 120, 255), Rgb(230, 230, 255), Rgb(0, 0, 0), false);
            // Монохром: усі підписи одного кольору
            case 3: return Make(Rgb(18, 18, 18), Rgb(255, 255, 255), Rgb(180, 180, 180), Rgb(180, 180, 180), Rgb(180, 180, 180), Rgb(180, 180, 180), Rgb(180, 180, 180), Rgb(180, 180, 180), Rgb(0, 0, 0), false);
            case 4: return Make(Rgb(22, 10, 12), Rgb(255, 255, 255), Rgb(210, 190, 190), Rgb(255, 90, 70), Rgb(255, 190, 120), Rgb(255, 140, 90), Rgb(255, 120, 160), Rgb(235, 215, 215), Rgb(0, 0, 0), false);
            default:
            {
                Color bg = Color.FromArgb(OverlayCfg.CustomBg), ac = Color.FromArgb(OverlayCfg.CustomAccent);
                bool light = 0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B > 150;
                int val = light ? Rgb(20, 22, 30) : Rgb(255, 255, 255);
                int a = Rgb(ac.R, ac.G, ac.B);
                return Make(Rgb(bg.R, bg.G, bg.B), val, a, a, a, a, a, a, light ? Rgb(206, 212, 222) : Rgb(0, 0, 0), light);
            }
        }
    }
}

// ----------------------------------------------------------------------
//  Модуль оверлея: коли показувати і гаряча клавіша
// ----------------------------------------------------------------------
static class OverlayModule
{
    public const int HkToggle = 0x5B01, HkLimit = 0x5B02;
    static NeoBar bar;
    static HotkeyWindow hk;
    static OverlayForm form;
    static Timer timer;
    static bool manualHidden;
    public static bool Visible;
    public static string HotkeyError = "";

    public static void Init(NeoBar b)
    {
        bar = b;
        OverlayCfg.Load();
        try
        {
            hk = new HotkeyWindow();
            hk.Pressed += OnHotkey;
        }
        catch { hk = null; }
        timer = new Timer();
        timer.Tick += delegate { try { Tick(); } catch (Exception skipEx) { SbLog.Skip(skipEx); } };
        Apply();
    }

    // після змін у налаштуваннях
    public static void Apply()
    {
        if (bar == null) return;
        HotkeyError = "";
        if (hk != null)
        {
            List<string> bad = new List<string>();
            if (!hk.Register(HkToggle, OverlayCfg.KeyToggle)) bad.Add(OverlayCfg.KeyText(OverlayCfg.KeyToggle));
            if (!hk.Register(HkLimit, OverlayCfg.KeyLimit)) bad.Add(OverlayCfg.KeyText(OverlayCfg.KeyLimit));
            if (bad.Count > 0) HotkeyError = L.T("Зайняті іншою програмою: ") + string.Join(", ", bad.ToArray());
        }
        timer.Interval = Math.Max(100, OverlayCfg.Interval);
        if (!timer.Enabled) timer.Start();
        manualHidden = false;
        Tick();
    }

    public static void Shutdown()
    {
        try { if (timer != null) timer.Stop(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        try { if (hk != null) hk.Dispose(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        try { FpsLimiter.Shutdown(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        try { GamePing.Shutdown(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        try { if (form != null && !form.IsDisposed) form.Close(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    static void OnHotkey(int id)
    {
        if (id == HkLimit)
        {
            string msg = FpsLimiter.Toggle();
            Note(msg);
            return;
        }
        if (id == HkToggle)
        {
            manualHidden = !manualHidden;
            Tick();
        }
    }

    // коротке повідомлення в оверлеї (наприклад, після гарячої клавіші обмежувача)
    static string note = "";
    static DateTime noteUntil = DateTime.MinValue;

    public static void Note(string msg)
    {
        note = msg ?? "";
        noteUntil = DateTime.Now.AddSeconds(2.5);
        Tick();
    }

    // чи на передньому плані (або щойно була) гра
    static bool GameActive
    {
        get { return Games.CurrentPid > 0 && Games.InForeground && GameWantsOverlay; }
    }

    // оверлей увімкнено для поточної гри
    public static bool GameWantsOverlay
    {
        get { return Games.CurrentPid > 0 && (Games.Current == null || (Games.Current.Ov && !FpsLimiter.EmbedDrawing(Games.CurrentPid) && !FpsLimiter.WindowMustWait(Games.CurrentPid) && FpsLimiter.OverlaySupported(Games.CurrentPid))); }
    }

    // оверлей видно лише поки на передньому плані гра, для якої він увімкнений (інакше все на паузі)
    public static bool Want { get { return GameActive && !manualHidden; } }

    // картинка для показу в кадрі гри: той самий малюнок, що й у вікні, з тією ж прозорістю (суцільний фон лише в DirectDraw)
    static bool FrameTick(bool noteOn)
    {
        int pid = Games.CurrentPid, fw, fh;
        if (pid <= 0 || !Games.InForeground || !FpsLimiter.FrameWanted(pid, out fw, out fh)) return false;
        Sensors.Use("overlay", 2);
        Sensors.SetInterval("overlay", OverlayCfg.Interval);
        if (fw <= 0 || fh <= 0) return true;                  // розмір кадру ще невідомий: поки діє запасний текст бібліотеки
        float ratio = 1f;
        try
        {
            Screen scr = Screen.FromHandle(Win.GetForegroundWindow());
            if (scr != null && scr.Bounds.Height > 0) ratio = (float)fh / scr.Bounds.Height;
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        ratio = Math.Max(0.75f, Math.Min(2f, ratio));      // гра в нижчій роздільності, ніж екран: не робимо картинку нечитабельною
        float sc = bar.Dpi * ratio;
        Bitmap b = null;
        OverlayForm.Opaque = FpsLimiter.FrameOpaque(pid);
        OverlayForm.FrameBox = new Size(fw, fh);
        try { if (!manualHidden) b = OverlayForm.Render(sc, false, noteOn ? note : "", true); }
        catch { b = null; }
        finally { OverlayForm.Opaque = false; OverlayForm.FrameBox = Size.Empty; }
        try { FpsLimiter.PushFrame(pid, b, OverlayCfg.Corner, (int)(OverlayCfg.OffX * sc), (int)(OverlayCfg.OffY * sc)); }
        finally { if (b != null) b.Dispose(); }
        return true;
    }

    public static void Tick()
    {
        if (bar == null) return;
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        TickCore();
        // пінг рахується (окремим потоком), лише поки гра на передньому плані, оверлей їй увімкнено й показник «Пінг» видно
        int pp = 0;
        if (Games.CurrentPid > 0 && Games.InForeground && !manualHidden && (Games.Current == null || Games.Current.Ov) && PingShown()) pp = Games.CurrentPid;
        GamePing.Use(pp);
        if (sw.ElapsedMilliseconds >= 25 && SbLog.On(SbLog.Games)) SbLog.W(SbLog.Games, "оверлей: кадр " + sw.ElapsedMilliseconds + " мс у потоці інтерфейсу");
    }

    static bool PingShown()
    {
        foreach (OvMetric m in OverlayCfg.Metrics) if (m.Key == "ping" && m.Show) return true;
        return false;
    }

    static void TickCore()
    {
        bool want = Want;
        Visible = want;
        bool noteOn = note.Length > 0 && DateTime.Now < noteUntil;
        if (!noteOn) note = "";
        bool frameLive = FrameTick(noteOn);
        if (!want && noteOn)
        {
            // оверлей вимкнено - показуємо лише повідомлення
            if (form == null || form.IsDisposed) { form = new OverlayForm(); form.CreateControl(); }
            form.ShowOv(bar.Dpi, note, false);
            return;
        }
        if (!want)
        {
            if (form != null && !form.IsDisposed) form.HideOv();
            if (!frameLive) Sensors.Use("overlay", 0);
            return;
        }
        Sensors.Use("overlay", 2);
        Sensors.SetInterval("overlay", OverlayCfg.Interval);
        if (form == null || form.IsDisposed) { form = new OverlayForm(); form.CreateControl(); }
        form.ShowOv(bar.Dpi, noteOn ? note : "", true);
    }
}
