using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// ======================================================================
//  Перевірка одного файла на ознаки шкідливості (без бази сигнатур): тип за вмістом, а не за розширенням,
//  підпис, позначка «з інтернету», для програм - пакувальники й підозрілі функції, для документів -
//  макроси й JavaScript, для скриптів і ярликів - небезпечні команди, для архівів - що всередині.
//  Файл лише читається; нікуди не надсилається (кнопка VirusTotal відкриває сайт з одним лише хешем).
// ======================================================================
sealed class FileReport
{
    public string Path = "", Kind = "", Sha256 = "", Url = "", Signer = "", Error = "";
    public long Size;
    public int SigKind = Sig.Unknown, Score;
    public bool Done;
    public readonly List<string> Why = new List<string>(), Info = new List<string>();
    // VirusTotal: 0 - не перевіряли, 1 - перевірка, 2 - є результат, 3 - помилка
    public volatile int VtState;
    public string VtText = "";
    public bool VtDone;
    public int Level { get { return Score >= 5 ? 2 : (Score >= 3 ? 1 : 0); } }
    public void Add(int s, string why) { Score += s; if (!Why.Contains(why)) Why.Add(why); }
}

static class FileCheck
{
    static bool StartsWith(byte[] b, params byte[] m)
    {
        if (b.Length < m.Length) return false;
        for (int i = 0; i < m.Length; i++) if (b[i] != m[i]) return false;
        return true;
    }

    static int IndexOf(byte[] b, byte[] p, int max)
    {
        int end = Math.Min(b.Length, max) - p.Length;
        for (int i = 0; i <= end; i++)
        {
            int k = 0;
            while (k < p.Length && b[i + k] == p[k]) k++;
            if (k == p.Length) return i;
        }
        return -1;
    }

    static byte[] Head(string path, int n)
    {
        using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            byte[] b = new byte[(int)Math.Min(n, f.Length)];
            int got = 0, k;
            while (got < b.Length && (k = f.Read(b, got, b.Length - got)) > 0) got += k;
            return b;
        }
    }

    static readonly string[] PeExt = { ".exe", ".dll", ".sys", ".scr", ".com", ".cpl", ".ocx", ".efi", ".mui", ".drv", ".ax", ".node", ".pyd", ".winmd", ".tlb", ".acm", ".tsp", ".pif", ".bin", ".dat", ".tmp" };
    static readonly string[] ScriptExt = { ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".bat", ".cmd", ".sct" };

    public static FileReport Analyze(string path)
    {
        FileReport r = new FileReport();
        r.Path = path;
        try
        {
            FileInfo fi = new FileInfo(path);
            r.Size = fi.Length;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            r.Score += SecHeur.NameRisk(path, r.Why);
            int zone;
            r.Url = SecHeur.Zone(path, out zone);
            bool web = zone >= 3;
            if (web) r.Info.Add(L.T("Завантажено з інтернету") + (r.Url.Length > 0 && r.Url != "about:internet" ? ": " + r.Url : "") + ".");
            byte[] h = Head(path, 8 << 20);
            if (StartsWith(h, (byte)'M', (byte)'Z')) Pe(r, path, ext, web);
            else if (Array.IndexOf(ScriptExt, ext) >= 0) Script(r, h, ext);
            else if (StartsWith(h, 0x4C, 0x00, 0x00, 0x00, 0x01, 0x14, 0x02, 0x00)) Lnk(r, path);
            else if (StartsWith(h, 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1)) Ole(r, h, ext, path);
            else if (StartsWith(h, (byte)'P', (byte)'K', 3, 4) || StartsWith(h, (byte)'P', (byte)'K', 5, 6)) Zip(r, path, ext);
            else if (StartsWith(h, (byte)'%', (byte)'P', (byte)'D', (byte)'F')) Pdf(r, h);
            else if (StartsWith(h, (byte)'R', (byte)'a', (byte)'r', (byte)'!')) { r.Kind = L.T("Архів RAR"); r.Info.Add(L.T("Вміст архіву RAR тут не розбирається - перевірте файли після розпакування.")); }
            else if (StartsWith(h, (byte)'7', (byte)'z', 0xBC, 0xAF)) { r.Kind = L.T("Архів 7z"); r.Info.Add(L.T("Вміст архіву 7z тут не розбирається - перевірте файли після розпакування.")); }
            else if (h.Length > 0x8006 && h[0x8001] == 'C' && h[0x8002] == 'D' && h[0x8003] == '0' && h[0x8004] == '0' && h[0x8005] == '1')
            {
                r.Kind = L.T("Образ диска (ISO)");
                if (web) r.Add(1, L.T("Образ диска з інтернету: файли всередині не отримують позначку «з інтернету», тож Windows менше попереджає - цим користуються шкідливі розсилки."));
            }
            else r.Kind = KindByExt(ext);
            if (Array.IndexOf(new string[] { ".scr", ".pif", ".com" }, ext) >= 0) r.Add(2, L.T("Розширення ") + ext + L.T(" - це теж програма, але звичайні програми ним майже не користуються."));
            if (ext == ".hta") r.Add(3, L.T("HTA - веб-сторінка з повними правами програми; часто використовується в шкідливих розсилках."));
            if (ext == ".vbe" || ext == ".jse") r.Add(3, L.T("Закодований скрипт (") + ext + L.T(") - вміст навмисно приховано."));
            try { r.Sha256 = Hashes.Sha256Of(path); } catch { }
        }
        catch (Exception ex) { r.Error = ex.Message; }
        r.Done = true;
        return r;
    }

    static string KindByExt(string ext)
    {
        switch (ext)
        {
            case ".txt": case ".log": case ".md": return L.T("Текст");
            case ".jpg": case ".jpeg": case ".png": case ".gif": case ".webp": case ".bmp": return L.T("Зображення");
            case ".mp3": case ".flac": case ".wav": case ".ogg": case ".m4a": return L.T("Аудіо");
            case ".mp4": case ".mkv": case ".avi": case ".mov": case ".webm": return L.T("Відео");
            case ".img": case ".vhd": case ".vhdx": return L.T("Образ диска");
            default: return ext.Length > 0 ? L.T("Файл ") + ext : L.T("Файл");
        }
    }

    static void Pe(FileReport r, string path, string ext, bool web)
    {
        PeInfo pe = PeInfo.Read(path);
        r.Kind = pe.Ok ? (pe.Native ? L.T("Драйвер Windows") : (pe.Dll ? L.T("Бібліотека Windows (DLL)") : L.T("Програма Windows"))) + (pe.Is64 ? L.T(" (64-біт") : L.T(" (32-біт")) + (pe.DotNet ? ", .NET)" : ")") : L.T("Програма (пошкоджена будова)");
        if (!pe.Ok) { r.Add(1, L.T("Файл схожий на програму, але її будова незвична чи пошкоджена.")); }
        if (Array.IndexOf(PeExt, ext) < 0) r.Add(4, L.T("Це програма Windows під виглядом «") + (ext.Length > 0 ? ext : L.T("без розширення")) + L.T("» - так маскують шкідливі файли."));
        string sg;
        r.SigKind = Sig.Check(path, out sg);
        r.Signer = sg;
        if (r.SigKind == Sig.Bad) r.Add(4, L.T("Цифровий підпис недійсний: файл змінено після підписання або сертифікат не довірений."));
        else if (r.SigKind == Sig.None) r.Add(web ? 2 : 1, L.T("Без цифрового підпису: автора не перевірити.") + (web ? L.T(" Завантажено з інтернету.") : ""));
        if (!pe.Ok) return;
        if (pe.Packer.Length > 0) r.Add(r.SigKind == Sig.Ok ? 0 : 2, L.T("Запакований чи захищений (") + pe.Packer + L.T("): справжній код приховано. Так роблять і ігри, і шкідливі програми."));
        else if (pe.CodeEntropy > 7.3 && r.SigKind != Sig.Ok) r.Add(2, L.T("Код програми схожий на стиснений чи зашифрований (висока ентропія) - вміст приховано."));
        if (pe.ImportCount > 0 && pe.ImportCount < 6 && !pe.DotNet && r.SigKind != Sig.Ok) r.Add(1, L.T("Дуже мало імпортованих функцій - типово для запакованих програм."));
        int sig = r.SigKind == Sig.Ok ? 0 : 1;           // у підписаних програм ці функції - звичайна справа
        if (pe.Has("WriteProcessMemory") && pe.Has("CreateRemoteThread", "NtCreateThreadEx", "QueueUserAPC", "SetThreadContext"))
            r.Add(2 * sig, L.T("Уміє записувати код в інші процеси й запускати його там (впровадження). Так працюють і відладчики, і шкідливі програми."));
        if (pe.Has("SetWindowsHookEx") && pe.Has("GetAsyncKeyState", "GetKeyboardState", "GetKeyState"))
            r.Add(sig, L.T("Перехоплює натискання клавіш (так працюють і гарячі клавіші, і клавіатурні шпигуни)."));
        if (pe.Has("URLDownloadToFile")) r.Add(sig, L.T("Завантажує файли з інтернету."));
        if (pe.Has("CryptEncrypt", "BCryptEncrypt") && pe.Has("FindFirstFile", "FindFirstFileEx") && pe.Has("DeleteFile", "MoveFileEx"))
            r.Add(sig, L.T("Шифрує дані й обходить файли на диску (так працюють і архіватори, і програми-вимагачі)."));
        if (pe.Has("IsDebuggerPresent", "CheckRemoteDebuggerPresent") && pe.Has("VirtualAllocEx", "VirtualProtect") && sig > 0)
            r.Add(1, L.T("Перевіряє, чи її не аналізують у відладчику, і змінює права на пам'ять."));
        if (pe.Stamp > DateTime.UtcNow.AddDays(2) && pe.Stamp.Year < 2100) r.Add(1, L.T("Дата збирання програми - у майбутньому (") + pe.Stamp.ToString("dd.MM.yyyy") + ").");
        if (pe.Dlls.Count > 0) r.Info.Add(L.T("Використовує: ") + string.Join(", ", pe.Dlls.GetRange(0, Math.Min(10, pe.Dlls.Count)).ToArray()) + (pe.Dlls.Count > 10 ? "…" : ""));
    }

    static void Script(FileReport r, byte[] h, string ext)
    {
        r.Kind = L.T("Скрипт ") + ext;
        string t = Encoding.UTF8.GetString(h, 0, Math.Min(h.Length, 2 << 20));
        if (h.Length > 2 && h[0] == 0xFF && h[1] == 0xFE) t = Encoding.Unicode.GetString(h, 2, Math.Min(h.Length - 2, 2 << 20));
        r.Score += SecHeur.CmdRisk(t, r.Why);
        string lt = t.ToLowerInvariant();
        int run = 0, maxRun = 0;
        foreach (char c in t)
        {
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=') { run++; if (run > maxRun) maxRun = run; }
            else run = 0;
        }
        if (maxRun > 1500) r.Add(2, L.T("Містить довгий закодований блок (Base64, ") + maxRun + L.T(" символів) - так ховають вкладену програму чи код."));
        int chr = Count(lt, "chr(") + Count(lt, "chrw(") + Count(lt, "[char]") + Count(lt, "fromcharcode");
        if (chr > 40) r.Add(2, L.T("Текст складається з кодів символів (") + chr + L.T(" разів) - типове заплутування коду."));
        if ((lt.Contains("eval(") || lt.Contains("execute(") || lt.Contains("executeglobal")) && (lt.Contains("unescape") || lt.Contains("chr(") || lt.Contains("fromcharcode") || lt.Contains("replace(")))
            r.Add(2, L.T("Збирає код із шматків і виконує його (eval/Execute)."));
        if (lt.Contains("wscript.shell") && (lt.Contains(".run") || lt.Contains(".exec"))) r.Add(1, L.T("Запускає інші програми чи команди."));
        if (lt.Contains("activexobject") || lt.Contains("createobject(\"msxml2.xmlhttp") || lt.Contains("winhttp.winhttprequest")) r.Add(1, L.T("Звертається до інтернету."));
        if (lt.Contains("adodb.stream") && lt.Contains("savetofile")) r.Add(2, L.T("Зберігає завантажені дані у файл."));
    }

    static int Count(string s, string p)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(p, i, StringComparison.Ordinal)) >= 0) { n++; i += p.Length; }
        return n;
    }

    static void Lnk(FileReport r, string path)
    {
        r.Kind = L.T("Ярлик");
        try
        {
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic s = sh.CreateShortcut(path);
            string target = s.TargetPath ?? "", args = s.Arguments ?? "";
            r.Info.Add(L.T("Запускає: ") + target + (args.Length > 0 ? " " + (args.Length > 200 ? args.Substring(0, 200) + "…" : args) : ""));
            r.Score += SecHeur.CmdRisk(Path.GetFileName(target) + " " + args, r.Why);
            string tn = Path.GetFileNameWithoutExtension(target).ToLowerInvariant();
            if (Array.IndexOf(new string[] { "powershell", "pwsh", "cmd", "mshta", "wscript", "cscript", "rundll32", "regsvr32", "certutil", "bitsadmin" }, tn) >= 0 && args.Length > 0)
                r.Add(2, L.T("Ярлик запускає системну програму-посередника (") + tn + L.T(") з командою - так виглядають шкідливі ярлики з розсилок."));
            if (args.Length > 260) r.Add(1, L.T("Дуже довгі параметри ярлика (") + args.Length + L.T(" символів)."));
        }
        catch { r.Info.Add(L.T("Ціль ярлика не вдалося прочитати.")); }
    }

    static void Ole(FileReport r, byte[] h, string ext, string path)
    {
        if (ext == ".msi" || ext == ".msp")
        {
            r.Kind = L.T("Інсталятор Windows (MSI)");
            string sg;
            r.SigKind = Sig.Check(path, out sg);
            r.Signer = sg;
            if (r.SigKind == Sig.None) r.Add(1, L.T("Інсталятор без цифрового підпису."));
            else if (r.SigKind == Sig.Bad) r.Add(4, L.T("Підпис інсталятора недійсний."));
            return;
        }
        r.Kind = L.T("Документ Office (старий формат)");
        byte[] vba = Encoding.Unicode.GetBytes("_VBA_PROJECT");
        if (IndexOf(h, vba, h.Length) >= 0 || IndexOf(h, Encoding.Unicode.GetBytes("Macros"), h.Length) >= 0)
        {
            r.Add(3, L.T("Містить макроси - найпоширеніший спосіб зараження через документи. Не вмикайте вміст, якщо не чекали такого файла."));
            string a = Encoding.ASCII.GetString(h).ToLowerInvariant();
            if (a.Contains("autoopen") || a.Contains("document_open") || a.Contains("workbook_open") || a.Contains("auto_open"))
                r.Add(2, L.T("Макрос запускається сам під час відкриття документа."));
            if (a.Contains("shell") && (a.Contains("wscript") || a.Contains("powershell") || a.Contains("cmd.exe")))
                r.Add(2, L.T("Макрос, схоже, запускає команди Windows."));
        }
        else r.Info.Add(L.T("Макросів не знайдено."));
    }

    static void Zip(FileReport r, string path, string ext)
    {
        string err;
        List<ZipAes.Entry> ents = ZipAes.List(path, out err);
        bool office = ext == ".docx" || ext == ".xlsx" || ext == ".pptx" || ext == ".docm" || ext == ".xlsm" || ext == ".pptm";
        r.Kind = office ? L.T("Документ Office") : (ext == ".jar" ? L.T("Програма Java") : (ext == ".apk" ? L.T("Програма Android") : L.T("Архів ZIP")));
        if (ents == null) { r.Info.Add(err); return; }
        bool macro = false, encExec = false;
        int exec = 0;
        foreach (ZipAes.Entry e in ents)
        {
            string n = e.Name.ToLowerInvariant();
            if (n.EndsWith("vbaproject.bin")) macro = true;
            string ee = Path.GetExtension(n);
            if (!e.Dir && SecHeur.IsExecExt(ee) && ee != ".dll")
            {
                exec++;
                if (e.Encrypted) encExec = true;
                List<string> w = new List<string>();
                int s = SecHeur.NameRisk(e.Name.Replace('/', '\\'), w);
                if (w.Count > 0) r.Add(s, L.T("Усередині: ") + w[0]);
            }
            if (!e.Dir && (ee == ".iso" || ee == ".img" || ee == ".vhd")) r.Add(1, L.T("Усередині образ диска «") + Path.GetFileName(e.Name) + L.T("» - типова «матрьошка» шкідливих розсилок."));
        }
        if (macro) r.Add(ext.EndsWith("m") ? 3 : 5, ext.EndsWith("m") ? L.T("Документ із макросами - найпоширеніший спосіб зараження через документи.") :
                                                  L.T("Документ має макроси, хоча формат ") + ext + L.T(" їх не передбачає - підозріло."));
        if (!office && ext != ".jar" && ext != ".apk")
        {
            r.Info.Add(L.T("Файлів в архіві: ") + ents.Count + (exec > 0 ? L.T(", з них програм чи скриптів: ") + exec : ""));
            if (encExec) r.Add(2, L.T("Запаролений архів із програмою чи скриптом: антивіруси не можуть заглянути всередину, тому так часто доставляють шкідливі файли."));
            else if (exec > 0 && ents.Count <= 3) r.Add(1, L.T("Архів містить майже лише програму чи скрипт."));
        }
    }

    static void Pdf(FileReport r, byte[] h)
    {
        r.Kind = L.T("Документ PDF");
        string a = Encoding.ASCII.GetString(h);
        bool js = a.Contains("/JavaScript") || a.Contains("/JS ") || a.Contains("/JS(") || a.Contains("/JS<");
        if (js) r.Add(2, L.T("PDF містить JavaScript - звичайним документам він не потрібен."));
        if (js && a.Contains("/OpenAction")) r.Add(1, L.T("Скрипт запускається одразу під час відкриття."));
        if (a.Contains("/Launch")) r.Add(3, L.T("PDF може запускати інші програми (/Launch)."));
        if (a.Contains("/EmbeddedFile")) r.Info.Add(L.T("Має вкладені файли."));
        if (!js && !a.Contains("/Launch")) r.Info.Add(L.T("Активного вмісту (JavaScript, запуск програм) не знайдено."));
    }
}

// Перевірка файла на VirusTotal (за хешем, за потреби - з надсиланням): спільна для вікна перевірки файла й вікна «Безпека»
abstract class VtWindow : ToolWindow
{
    protected VtWindow(NeoBar bar, string caption, int w, int h) : base(bar, caption, w, h) { }
    protected VtWindow(Theme t, float sc, string caption, int w, int h) : base(t, sc, caption, w, h) { }

    // ---------- VirusTotal ----------
    // ключ API: одне вікно введення і для «Списків», і для перевірки файла (порожньо - прибрати ключ)
    protected void AskVtKey()
    {
        string k = InputDialog.Ask(Bar, T, Sc, this, L.T("Ключ VirusTotal"), L.T("Вставте ключ API зі сторінки свого профілю на virustotal.com (64 символи; порожньо - прибрати ключ). Він зберігається у файлі virustotal.key поруч із NeoBar."), Vt.Key);
        if (k != null) Vt.Key = k;
    }

    protected void StartVt(FileReport r)
    {
        if (r.VtState == 1 || r.Sha256.Length == 0) return;
        if (Vt.Key.Length == 0)
        {
            int a = Ask("VirusTotal", L.T("Щоб перевіряти прямо тут (і надсилати файли, яких немає в базі), потрібен безкоштовний ключ API VirusTotal: зареєструйтеся на virustotal.com, відкрийте свій профіль → «API key» і скопіюйте ключ.\n\n") +
                        L.T("Без ключа можна відкрити сторінку файла на сайті: якщо його там немає, натисніть «Choose file» і виберіть файл (шлях буде скопійовано)."),
                        L.T("Ввести ключ"), L.T("Відкрити сайт"), L.T("Скасувати"));
            if (a == 1) { Copy(r.Path); OpenVt(r.Sha256); Say(L.T("Шлях до файла скопійовано - знадобиться, якщо його немає в базі."), false); return; }
            if (a != 0) return;
            AskVtKey();
            if (Vt.Key.Length == 0) return;
        }
        r.VtState = 1; r.VtText = L.T("VirusTotal: перевірка за хешем…");
        Invalidate();
        Bg(delegate
        {
            Vt.Res v = Vt.Lookup(r.Sha256);
            Ui(delegate
            {
                if (v.Code == 200) VtResult(r, v);
                else if (v.Code == 404) VtUpload(r);
                else { r.VtState = 3; r.VtText = "VirusTotal: " + v.Err; }
            });
        });
    }

    void VtUpload(FileReport r)
    {
        if (Ask("VirusTotal", L.T("Файла «") + System.IO.Path.GetFileName(r.Path) + L.T("» немає в базі VirusTotal.\n\nНадіслати його на перевірку? Надіслані файли доступні дослідникам безпеки VirusTotal, тож не надсилайте особисті документи, фото чи файли з паролями."),
                L.T("Надіслати"), L.T("Не надсилати")) != 0)
        { r.VtState = 3; r.VtText = L.T("VirusTotal: файла немає в базі (не надсилали)."); Invalidate(); return; }
        r.VtText = L.T("VirusTotal: надсилання…");
        Invalidate();
        Bg(delegate
        {
            Vt.Res u = Vt.Upload(r.Path);
            if (u.Err.Length > 0) { Ui(delegate { r.VtState = 3; r.VtText = "VirusTotal: " + u.Err; }); return; }
            Ui(delegate { r.VtText = L.T("VirusTotal: файл надіслано, триває аналіз (зазвичай 1-3 хвилини)…"); });
            DateTime until = DateTime.Now.AddMinutes(8);
            while (DateTime.Now < until && !IsDisposed)
            {
                Thread.Sleep(20000);               // безкоштовний ключ - не частіше 4 запитів на хвилину
                Vt.Res a = Vt.Analysis(u.Id);
                if (a.Err.Length > 0) { Ui(delegate { r.VtState = 3; r.VtText = "VirusTotal: " + a.Err; }); return; }
                if (a.Status == "completed") { Ui(delegate { VtResult(r, a); }); return; }
            }
            Ui(delegate { r.VtState = 3; r.VtDone = true; r.VtText = L.T("VirusTotal: аналіз ще триває - результат буде в звіті на сайті."); });
        });
    }

    void VtResult(FileReport r, Vt.Res v)
    {
        r.VtState = 2; r.VtDone = true;
        if (v.Total == 0) { r.VtText = L.T("VirusTotal: результату ще немає - відкрийте звіт пізніше."); return; }
        if (v.Mal == 0 && v.Sus == 0) { r.VtText = L.T("VirusTotal: жоден із ") + v.Total + L.T(" антивірусів не знайшов загрози."); return; }
        r.VtText = "";
        int n = v.Mal + v.Sus;
        r.Add(v.Mal >= 3 ? 6 : 2, "VirusTotal: " + n + L.T(" з ") + v.Total + L.T(" антивірусів вважають файл ") + (v.Mal > 0 ? L.T("шкідливим") : L.T("підозрілим")) +
              (v.Mal < 3 ? L.T(" (1-2 спрацювання бувають і хибними).") : "."));
    }

    // ---------- звіти про файли (спільні для вікна «Перевірка файла» і вкладки «Файл» у «Безпеці») ----------
    protected readonly List<FileReport> Reps = new List<FileReport>();
    protected int RepScroll;

    // vt - одразу після аналізу перевірити й на VirusTotal
    public void AddReport(string path, bool vt)
    {
        if (!File.Exists(path)) return;
        foreach (FileReport x in Reps) if (string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)) { Reps.Remove(x); break; }
        FileReport r = new FileReport();
        r.Path = path;
        Reps.Insert(0, r);
        RepScroll = 0;
        Thread t = new Thread(delegate ()
        {
            FileReport res = FileCheck.Analyze(path);
            try
            {
                BeginInvoke(new Action(delegate
                {
                    int i = Reps.IndexOf(r);
                    if (i >= 0) Reps[i] = res;
                    Invalidate();
                    if (vt && i >= 0 && res.Sha256.Length > 0) StartVt(res);
                }));
            }
            catch { }
        });
        t.IsBackground = true;
        t.Priority = ThreadPriority.BelowNormal;
        t.Start();
        Invalidate();
    }

    int CardH(FileReport r, int w)
    {
        int h = Px(64);
        if (!r.Done) return h + Px(10);
        foreach (string s in Lines(r)) h += TextRenderer.MeasureText(s, FS, new Size(w - Px(28), 0), TextFormatFlags.WordBreak).Height + Px(2);
        return h + Px(48);
    }

    static List<string> Lines(FileReport r)
    {
        List<string> l = new List<string>();
        if (r.Error.Length > 0) { l.Add(L.T("Не вдалося прочитати: ") + r.Error); return l; }
        foreach (string s in r.Why) l.Add("• " + s);
        foreach (string s in r.Info) l.Add(s);
        if (r.VtText.Length > 0) l.Add(r.VtText);
        if (r.SigKind != Sig.Unknown) l.Add(Sig.Text(r.SigKind, r.Signer));
        if (r.Sha256.Length > 0) l.Add("SHA-256: " + r.Sha256);
        return l;
    }

    // картки зі звітами у прямокутнику list; повертає повну висоту вмісту
    protected int PaintReports(Graphics g, Rectangle list)
    {
        if (Reps.Count == 0) TxtC(g, L.T("Тут з'явиться висновок щодо файла."), F, list, P.Sub);
        int total = 0;
        foreach (FileReport x in Reps) total += CardH(x, list.Width - Px(10)) + Px(10);
        RepScroll = Math.Max(0, Math.Min(Math.Max(0, total - list.Height), RepScroll));
        GraphicsState st = g.Save();
        g.SetClip(list);
        int cy = list.Y - RepScroll;
        foreach (FileReport x in Reps)
        {
            int h = CardH(x, list.Width - Px(10));
            Rectangle c = new Rectangle(list.X, cy, list.Width - Px(10), h);
            cy += h + Px(10);
            if (c.Bottom < list.Y || c.Y > list.Bottom) continue;
            Color lc = !x.Done ? P.Sub : (x.Error.Length > 0 ? P.Sub : (x.Level == 2 ? P.Bad : (x.Level == 1 ? P.Warn : P.Good)));
            Card(g, c, Util.Blend(P.Card, lc, 0.07f));
            using (SolidBrush b = new SolidBrush(lc)) Util.FillRound(g, b, new Rectangle(c.X, c.Y + Px(10), Px(4), Px(44)), Px(2));
            TxtL(g, System.IO.Path.GetFileName(x.Path), FB, new Rectangle(c.X + Px(14), c.Y + Px(8), c.Width - Px(28), Px(22)), P.Fg);
            string verdict = !x.Done ? L.T("Перевірка…") : (x.Error.Length > 0 ? L.T("Не перевірено") :
                             (x.Level == 2 ? L.T("Підозріло") : (x.Level == 1 ? L.T("Варто перевірити") : (x.Why.Count > 0 ? L.T("Серйозних ознак немає") : L.T("Ознак шкідливості не знайдено")))));
            TxtL(g, verdict + (x.Done && x.Kind.Length > 0 ? " · " + x.Kind : "") + " · " + SizeText(x.Size), FS, new Rectangle(c.X + Px(14), c.Y + Px(32), c.Width - Px(28), Px(20)), lc);
            if (!x.Done) continue;
            int ly = c.Y + Px(60);
            foreach (string s in Lines(x))
            {
                int lh = TextRenderer.MeasureText(s, FS, new Size(c.Width - Px(28), 0), TextFormatFlags.WordBreak).Height;
                bool reason = s.StartsWith("• ");
                TxtW(g, s, FS, new Rectangle(c.X + Px(14), ly, c.Width - Px(28), lh + Px(2)), reason ? P.Fg : P.Sub);
                ly += lh + Px(2);
            }
            int bx = c.X + Px(14), by = c.Bottom - Px(38);
            FileReport xx = x;
            Btn(g, new Rectangle(bx, by, Px(130), Px(28)), L.T("Відкрити теку"), delegate { ShowInFolder(xx.Path); }, 0, true);
            bx += Px(138);
            if (x.Sha256.Length > 0)
            {
                if (x.VtDone) Btn(g, new Rectangle(bx, by, Px(170), Px(28)), L.T("Звіт VirusTotal"), delegate { OpenVt(xx.Sha256); }, 0, true);
                else Btn(g, new Rectangle(bx, by, Px(190), Px(28)), x.VtState == 1 ? L.T("Перевірка…") : L.T("Перевірити на VirusTotal"), delegate { StartVt(xx); }, 0, x.VtState != 1);
            }
        }
        g.Restore(st);
        Wheel(list, delegate (int d) { RepScroll -= d > 0 ? Px(60) : -Px(60); Invalidate(); });
        VBar(g, new Rectangle(list.Right - Px(5), list.Y, Px(4), list.Height), total, list.Height, RepScroll, delegate (int v) { RepScroll = v; Invalidate(); });
        return total;
    }

    protected void Bg(ThreadStart a)
    {
        Thread t = new Thread(delegate () { try { a(); } catch (Exception ex) { Util.ErrorLog(ex); } });
        t.IsBackground = true;
        t.Start();
    }

    protected void Ui(Action a)
    {
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(delegate { a(); Invalidate(); })); } catch { }
    }

    protected static void OpenVt(string sha)
    {
        // відкривається лише сторінка за хешем: сам файл не завантажується
        try { System.Diagnostics.Process.Start("https://www.virustotal.com/gui/file/" + sha); } catch { }
    }
}

// ======================================================================
//  Вікно «Перевірка файла»: картки з висновком і причинами; файли - перетягуванням чи з контекстного меню
// ======================================================================
sealed class FileCheckWindow : VtWindow
{
    public FileCheckWindow(NeoBar bar) : base(bar, L.T("Перевірка файла"), 600, 640) { Glyph = "\uE8A5"; }
    public FileCheckWindow(Theme t, float sc) : base(t, sc, L.T("Перевірка файла"), 600, 640) { Glyph = "\uE8A5"; }

    protected override void Dropped(string[] files) { foreach (string f in files) Add(f); }

    public void Add(string path) { AddReport(path, false); }
    public void Add(string path, bool vt) { AddReport(path, vt); }

    void Pick()
    {
        using (OpenFileDialog d = new OpenFileDialog())
        {
            d.Title = L.T("Файл для перевірки");
            d.Filter = L.T("Усі файли|*.*");
            d.Multiselect = true;
            if (d.ShowDialog(this) == DialogResult.OK) foreach (string f in d.FileNames) Add(f);
        }
    }

    protected override void PaintBody(Graphics g, Rectangle r)
    {
        int y = r.Y;
        Btn(g, new Rectangle(r.X, y, Px(170), Px(32)), L.T("Перевірити файл…"), Pick, 1, true);
        TxtL(g, L.T("або перетягніть файли у вікно"), FS, new Rectangle(r.X + Px(180), y, r.Width - Px(180), Px(32)), P.Sub);
        y += Px(42);
        Rectangle list = new Rectangle(r.X, y, r.Width, r.Bottom - y - Px(44));
        int total = PaintReports(g, list);
        string foot = NoteLive ? Note : L.T("Евристики без бази сигнатур: це підказка, а не вирок антивіруса. Файл нікуди не надсилається без вашої згоди.");
        TxtW(g, foot, FS, new Rectangle(r.X, r.Bottom - Px(38), r.Width, Px(38)), NoteLive ? (NoteBad ? P.Bad : P.Good) : P.Sub);
        FitTo(r, list.Y + Math.Max(total, Px(90)) + Px(44), Px(300));
    }
}
