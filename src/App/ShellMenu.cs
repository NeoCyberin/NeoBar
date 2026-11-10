using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// ======================================================================
//  Пункти NeoBar у контекстному меню файлів Провідника. Пишеться лише в реєстр поточного користувача
//  (HKCU\Software\Classes) - права адміністратора не потрібні, і нічого системного не змінюється.
//  У новому меню Windows 11 класичні пункти показуються під «Показати додаткові параметри» (Shift+F10):
//  у перше меню потрапляють лише програми з підписаним пакетом MSIX.
// ======================================================================
static class ShellMenu
{
    const string Root = @"Software\Classes\*\shell\NeoBar";
    const string ExtKey = @"Software\Classes\.nbenc";
    const string ProgId = "NeoBar.nbenc";
    const string ProgKey = @"Software\Classes\" + ProgId;

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int ev, int flags, IntPtr a, IntPtr b);

    static string Exe { get { return Application.ExecutablePath; } }
    static string Cmd(string verb) { return "\"" + Exe + "\" --tool " + verb + " \"%1\""; }

    // дві незалежні групи пунктів: "crypt" - хеш-сума, шифрування, розшифрування (вікно «Шифрування»);
    // "sec" - перевірка файла на підозрілість (вікно «Безпека»). Спільне підменю «NeoBar» є, поки ввімкнена хоч одна
    static readonly string[] CryptVerbs = { "1hash", "2enc", "3dec" };
    static readonly string[] SecVerbs = { "4scan" };

    static bool HasKey(string sub)
    {
        try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(sub)) return k != null; }
        catch { return false; }
    }

    public static bool Has(string group)
    {
        return HasKey(Root + @"\shell\" + (group == "sec" ? SecVerbs[0] : CryptVerbs[0]));
    }

    static void Verb(RegistryKey parent, string id, string title, string verb, string appliesTo)
    {
        using (RegistryKey k = parent.CreateSubKey(id))
        {
            k.SetValue("MUIVerb", title);
            k.SetValue("Icon", "\"" + Exe + "\",0");
            if (appliesTo != null) k.SetValue("AppliesTo", appliesTo);
            using (RegistryKey c = k.CreateSubKey("command")) c.SetValue("", Cmd(verb));
        }
    }

    static void Install(bool crypt, bool sec)
    {
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Root))
        {
            k.SetValue("MUIVerb", "NeoBar");
            k.SetValue("Icon", "\"" + Exe + "\",0");
            k.SetValue("SubCommands", "");
            using (RegistryKey sh = k.CreateSubKey("shell"))
            {
                if (crypt)
                {
                    Verb(sh, "1hash", L.T("Хеш-сума"), "hash", null);
                    Verb(sh, "2enc", L.T("Зашифрувати…"), "enc", null);
                    Verb(sh, "3dec", L.T("Розшифрувати…"), "dec", "System.FileExtension:=.nbenc OR System.FileExtension:=.zip");
                }
                if (sec) Verb(sh, "4scan", L.T("Перевірити на підозрілість"), "scan", null);
            }
        }
        if (!crypt) return;
        // .nbenc - подвійний клік відкриває розшифрування; чуже прив'язування розширення не перебиваємо
        string cur = null;
        using (RegistryKey e = Registry.CurrentUser.OpenSubKey(ExtKey)) if (e != null) cur = e.GetValue("") as string;
        if (string.IsNullOrEmpty(cur) || cur == ProgId)
        {
            using (RegistryKey e = Registry.CurrentUser.CreateSubKey(ExtKey)) e.SetValue("", ProgId);
            using (RegistryKey p = Registry.CurrentUser.CreateSubKey(ProgKey))
            {
                p.SetValue("", L.T("Файл, зашифрований NeoBar"));
                using (RegistryKey i = p.CreateSubKey("DefaultIcon")) i.SetValue("", "\"" + Exe + "\",0");
                using (RegistryKey o = p.CreateSubKey(@"shell\open"))
                {
                    o.SetValue("", L.T("Розшифрувати"));
                    using (RegistryKey c = o.CreateSubKey("command")) c.SetValue("", Cmd("dec"));
                }
            }
        }
    }

    static void RemoveGroup(string group)
    {
        foreach (string v in group == "sec" ? SecVerbs : CryptVerbs)
            try { Registry.CurrentUser.DeleteSubKeyTree(Root + @"\shell\" + v, false); } catch (Exception ex) { Util.ErrorLog(ex); }
        if (group == "crypt")
            try
            {
                string cur = null;
                using (RegistryKey e = Registry.CurrentUser.OpenSubKey(ExtKey)) if (e != null) cur = e.GetValue("") as string;
                if (cur == ProgId) Registry.CurrentUser.DeleteSubKeyTree(ExtKey, false);
                Registry.CurrentUser.DeleteSubKeyTree(ProgKey, false);
            }
            catch (Exception ex) { Util.ErrorLog(ex); }
        // жодного пункту не лишилося - прибираємо й підменю
        if (!Has("crypt") && !Has("sec"))
            try { Registry.CurrentUser.DeleteSubKeyTree(Root, false); } catch (Exception ex) { Util.ErrorLog(ex); }
    }

    // усі пункти й асоціація .nbenc (видалення програми)
    public static void RemoveAll()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(Root, false); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        try
        {
            string cur = null;
            using (RegistryKey e = Registry.CurrentUser.OpenSubKey(ExtKey)) if (e != null) cur = e.GetValue("") as string;
            if (cur == ProgId) Registry.CurrentUser.DeleteSubKeyTree(ExtKey, false);
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        try { Registry.CurrentUser.DeleteSubKeyTree(ProgKey, false); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        Notify();
    }

    static void Notify() { try { SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); } catch (Exception skipEx) { SbLog.Skip(skipEx); } }      // SHCNE_ASSOCCHANGED

    // NeoBar перенесли в іншу теку - пункти меню вели б на старий шлях: оновлюємо (під час запуску панелі)
    public static void Refresh()
    {
        try
        {
            bool c = Has("crypt"), s = Has("sec");
            if (!c && !s) return;
            string cmd = null;
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Root + @"\shell\" + (c ? "1hash" : "4scan") + @"\command")) if (k != null) cmd = k.GetValue("") as string;
            if (cmd != Cmd(c ? "hash" : "scan")) { Install(c, s); Notify(); }
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    // увімкнути/вимкнути групу пунктів; "" - вдалося, інакше текст помилки
    public static string Toggle(string group)
    {
        try
        {
            if (Has(group)) RemoveGroup(group);
            else Install(group == "crypt", group == "sec");
            Notify();
            return "";
        }
        catch (Exception ex) { return ex.Message; }
    }
}

// ======================================================================
//  Окремий процес для пунктів контекстного меню («NeoBar.exe --tool hash|enc|dec|scan файл»).
//  Панель для цього не потрібна. Коли вибрано кілька файлів, Провідник запускає по процесу на файл -
//  перший стає «господарем» вікна, решта передають йому шлях через іменований канал і закриваються.
// ======================================================================
static class ToolHost
{

    static string PipeName { get { return "NeoBarTool-" + Process.GetCurrentProcess().SessionId + "-" + Environment.UserName; } }

    static Theme theme;
    static float scale = 1f;
    static Control marshal;
    static ApplicationContext ctx;
    static CryptWindow crypt;
    static FileCheckWindow check;

    public static int Run(string verb, string path)
    {
        // працює панель - файл відкривається в її розкритому вікні (одне розкрите вікно замінює інше)
        if (DropBridge.SendTool(verb, path)) return 0;
        bool created;
        Mutex m = new Mutex(true, "Local\\NeoBarTool", out created);
        if (!created)
        {
            if (Send(verb, path)) return 0;
            m.Dispose();
            m = null;                      // господар не відповідає - відкриваємо своє вікно
        }
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) { Util.ErrorLog(e.Exception); };
            theme = Prof.PeekTheme();
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = Math.Max(1f, g.DpiX / 96f); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            marshal = new Control();
            IntPtr h = marshal.Handle;     // вікно для передачі викликів у потік інтерфейсу
            ctx = new ApplicationContext();
            Handle(verb, path);
            if (created) Serve();
            Application.Run(ctx);
        }
        finally { if (m != null) { try { m.ReleaseMutex(); } catch (Exception skipEx) { SbLog.Skip(skipEx); } m.Dispose(); } }
        return 0;
    }

    static bool Send(string verb, string path)
    {
        try { Win.AllowSetForegroundWindow(-1); } catch (Exception skipEx) { SbLog.Skip(skipEx); }       // щоб господар зміг вивести своє вікно наперед
        DateTime until = DateTime.Now.AddSeconds(4);
        while (DateTime.Now < until)
        {
            try
            {
                using (NamedPipeClientStream c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    c.Connect(500);
                    byte[] b = Encoding.UTF8.GetBytes(verb + "\t" + path + "\n");
                    c.Write(b, 0, b.Length);
                    c.Flush();
                    return true;
                }
            }
            catch { Thread.Sleep(150); }
        }
        return false;
    }

    static void Serve()
    {
        Thread t = new Thread(delegate ()
        {
            for (;;)
            {
                try
                {
                    using (NamedPipeServerStream ps = new NamedPipeServerStream(PipeName, PipeDirection.In, 1))
                    {
                        ps.WaitForConnection();
                        using (StreamReader r = new StreamReader(ps, Encoding.UTF8))
                        {
                            string l;
                            while ((l = r.ReadLine()) != null)
                            {
                                int k = l.IndexOf('\t');
                                if (k <= 0) continue;
                                string v = l.Substring(0, k), p = l.Substring(k + 1);
                                try { marshal.BeginInvoke(new Action(delegate { Handle(v, p); })); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
                            }
                        }
                    }
                }
                catch { Thread.Sleep(300); }
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    static void Place(Form f)
    {
        Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        f.Location = new Point(wa.Left + (wa.Width - f.Width) / 2, wa.Top + Math.Max(0, (wa.Height - f.Height) / 2));
    }

    static void Front(Form f)
    {
        if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal;
        f.Show();
        f.Activate();
        f.BringToFront();
    }

    static void Closed()
    {
        // вікно під час FormClosed ще не звільнене (IsDisposed = false) - тому поле обнуляється в обробнику закриття
        bool any = (crypt != null && !crypt.IsDisposed) || (check != null && !check.IsDisposed);
        if (!any) ctx.ExitThread();
    }

    static void Handle(string verb, string path)
    {
        try
        {
            if (verb == "scan")
            {
                if (check == null || check.IsDisposed)
                {
                    check = new FileCheckWindow(theme, scale);
                    check.FormClosed += delegate { check = null; Closed(); };
                    Place(check);
                }
                check.Add(path);
                Front(check);
                return;
            }
            if (crypt == null || crypt.IsDisposed)
            {
                crypt = new CryptWindow(theme, scale);
                crypt.FormClosed += delegate { crypt = null; Closed(); };
                Place(crypt);
            }
            if (verb == "hash") crypt.HashFile(path);
            else crypt.AddFiles(new string[] { path });
            Front(crypt);
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }
}

// ======================================================================
//  Основа вікон, які працюють і з панеллю, і окремо (з контекстного меню файлів)
// ======================================================================
class ToolWindow : SbWindow
{
    protected readonly bool Standalone;

    public ToolWindow(NeoBar bar, string caption, int w, int h) : base(bar, caption, w, h) { Init(); }

    public ToolWindow(Theme t, float sc, string caption, int w, int h) : base(t, sc, caption, w, h)
    {
        Standalone = true;
        ShowInTaskbar = true;
        Init();
    }

    void Init()
    {
        DropFiles.Prepare(this);
        DragEnter += delegate (object s, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
        DragDrop += delegate (object s, DragEventArgs e)
        {
            string[] f = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (f != null && f.Length > 0) Dropped(f);
        };
    }

    protected virtual void Dropped(string[] files) { }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DropFiles.Register(this);
    }

    protected override void WndProc(ref Message m)
    {
        string[] f = DropFiles.Take(ref m);
        if (f != null) { if (f.Length > 0) Dropped(f); return; }
        base.WndProc(ref m);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            if (Standalone) { cp.ExStyle &= ~0x80; cp.ExStyle |= 0x40000; }        // окреме вікно - з кнопкою на панелі завдань
            return cp;
        }
    }

    protected int Ask(string title, string text, params string[] buttons)
    {
        if (Bar != null) return SbDialog.Show(Bar, this, title, text, buttons);
        return SbDialog.Show(T, Sc, this, title, text, buttons);
    }

    protected static void ShowInFolder(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
            else
            {
                string d = Path.GetDirectoryName(path);
                if (Directory.Exists(d)) Process.Start("explorer.exe", "\"" + d + "\"");
            }
        }
        catch (Exception ex) { Util.ErrorLog(ex); }
    }

    protected static void Copy(string s)
    {
        try { if (!string.IsNullOrEmpty(s)) Clipboard.SetText(s); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    protected static string SizeText(long b) { return Cleaner.Size(b); }

    // повідомлення внизу вікна, що саме зникає
    protected string Note = "";
    protected bool NoteBad;
    DateTime noteAt;
    protected void Say(string s, bool bad) { Note = s; NoteBad = bad; noteAt = DateTime.Now; Invalidate(); }
    protected bool NoteLive { get { return Note.Length > 0 && (DateTime.Now - noteAt).TotalSeconds < (NoteBad ? 12 : 5); } }

    protected override void OnTick()
    {
        if (Note.Length > 0 && !NoteLive) { Note = ""; Invalidate(); }
    }
}
