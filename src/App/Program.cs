using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // мова інтерфейсу - найперше (і в помічниках: їхній вивід показується у вікнах)
        try { L.Init(); } catch { }
        // папка програми для шляхів у налаштуваннях (%NEOBAR_DIR%\links\… - власні копії ярликів)
        try { Environment.SetEnvironmentVariable("NEOBAR_DIR", AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/')); } catch { }
        // підвищений помічник (SMART, літера диска, перевірка, оптимізація) - окремий короткий процес
        if (args.Length > 0 && args[0] == "--helper") return Helper.Run(args);
        // встановлювач перед видаленням програми: прибрати її сліди в системі
        if (args.Length > 0 && args[0] == "--uninstall") return Uninstall.Run();
        // автооновлення: копія програми з тимчасової теки ставить нову версію, поки основна закрита
        if (args.Length > 0 && args[0] == "--update") return UpdateHelper.Run(args);
        // пункт контекстного меню файлів (хеш, шифрування, перевірка) - окреме легке вікно без панелі
        if (args.Length >= 3 && args[0] == "--tool")
        {
            Win.SetProcessDPIAware();
            return ToolHost.Run(args[1], args[2]);
        }

        Win.SetProcessDPIAware();
        bool restarted = args.Length > 0 && args[0] == "--restarted";
        // «Завжди від імені адміністратора»: звичайний запуск передає роботу завданню з найвищими правами
        if (!restarted && !Elevated.IsAdmin && AdminTask.Wanted())
        {
            if (AdminTask.Launch()) return 0;
        }
        bool created = false;
        Mutex mtx = null;
        // після перезапуску від адміністратора чекаємо, поки стара копія закриється
        for (int i = 0; i < (restarted ? 50 : 1); i++)
        {
            // копію з правами адміністратора звичайний процес відкрити не може (відмова в доступі) - це теж «уже запущено»
            try { mtx = new Mutex(true, "Local\\NeoBarLauncher", out created); }
            catch (UnauthorizedAccessException) { created = false; mtx = null; }
            if (created) break;
            if (mtx != null) mtx.Dispose();
            mtx = null;
            Thread.Sleep(200);
        }
        if (!created)
        {
            // друга копія не запускається: показуємо вже запущену панель і підказку на ній
            if (!restarted) try { Win.PostMessage(new IntPtr(0xFFFF), Win.RegisterWindowMessage(NeoBar.RunningMsg), IntPtr.Zero, IntPtr.Zero); } catch { }
            return 0;
        }
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) { Util.ErrorLog(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e) { Exception ex = e.ExceptionObject as Exception; if (ex != null) Util.ErrorLog(ex); };
            // профіль: останній (без PIN) - одразу, інакше вікно вибору; закрили вікно - вихід
            try { Prof.Init(); } catch (Exception ex) { Util.ErrorLog(ex); }
            if (!Prof.Choose()) return 0;
            SbLog.W(SbLog.Panel, "запуск NeoBar: профіль «" + Prof.Cur.Name + "» (" + Prof.Dir + ")" + (Elevated.IsAdmin ? ", від адміністратора" : ""));
            // перший запуск профілю: швидке налаштування ще до появи панелі (записується прямо у файл профілю)
            try { FirstRun(); } catch (Exception ex) { Util.ErrorLog(ex); }
            ShellMenu.Refresh();          // NeoBar перенесли - пункти контекстного меню ведуть на новий шлях
            NeoBar bar = new NeoBar();
            // перше знайомство - один раз, до появи панелі
            if (!bar.Config.Welcomed)
            {
                bool go = true, dontShow = true;
                try
                {
                    using (WelcomeWindow w = new WelcomeWindow(bar))
                    {
                        w.ShowDialog();
                        go = w.Start;
                        dontShow = w.DontShow;
                    }
                }
                catch { }
                // хрестик - вихід; перезапуск від адміністратора - нова копія покаже це вікно ще раз
                if (!go)
                {
                    try { Fps.Stop(); } catch { }
                    try { mtx.Dispose(); } catch { }
                    Environment.Exit(0);
                }
                if (dontShow) bar.MarkWelcomed();
            }
            try { SecWatch.Init(bar); } catch (Exception ex) { Util.ErrorLog(ex); }
            Application.Run(bar);
        }
        finally
        {
            try { Fps.Stop(); } catch { }
            mtx.Dispose();
        }
        return 0;
    }

    static void FirstRun()
    {
        string ini = Prof.File("neobar.ini");
        Cfg c = new Cfg();
        List<Entry> l = new List<Entry>();
        if (System.IO.File.Exists(ini)) Store.Load(ini, c, l);
        else { l = NeoBar.DefaultEntries(); Store.Normalize(l); }
        if (c.Welcomed || c.QuickDone) return;
        bool auto = false, ov = true;
        try { auto = NeoBar.IsAutostart(); } catch { }
        try { Games.Load(); ov = Games.GenOv; } catch { }
        using (QuickSetup q = new QuickSetup(c, l, auto, ov, true))
        {
            q.ShowDialog();
            c.QuickDone = true;
            if (!q.Done) { Store.Save(ini, c, l); return; }
            q.ApplyTo(c, l);
            Store.Save(ini, c, l);
            if (q.AutoKnown && q.Auto != auto) try { NeoBar.SetAutostart(q.Auto); } catch { }
            if (q.Overlay != ov) try { Games.Load(); Games.GenOv = q.Overlay; Games.Save(); } catch { }
        }
    }
}
