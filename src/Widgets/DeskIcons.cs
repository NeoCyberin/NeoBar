using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// ======================================================================
//  Значки робочого столу не під віджетами: значок, що опинився під віджетом NeoBar чи нотаткою,
//  переставляється на найближче вільне місце сітки того самого монітора.
//
//  Як: документований шлях Провідника (як у статті Реймонда Чена «Manipulating the positions of desktop icons») -
//  IShellWindows → вікно робочого столу → IShellBrowser → IShellView → IFolderView:
//  GetItemPosition / SelectAndPositionItems. Жодного читання чи запису чужої пам'яті.
//  Якщо в Провіднику ввімкнено «Упорядковувати значки автоматично», Windows сама розставляє значки -
//  тоді нічого не робимо (Explorer однаково поверне їх назад).
//  Запуск: після переміщення віджета чи нотатки, коли на робочому столі з'являється файл, коли змінились екрани.
//  Без опитування: усе - за подіями з відкладенням на 1,5 с.
// ======================================================================
static class DeskIcons
{
    public static bool Enabled;
    public static string State = "";        // для «Діагностики»: "" - ще не запускалось, "ok", "auto" (автоупорядкування), "fail"
    public static int LastMoved;

    static Timer timer;
    static readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
    static Func<List<Rectangle>> source;

    [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int QIFn(IntPtr self, ref Guid iid, out IntPtr ppv);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint RelFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int FindSWFn(IntPtr self, IntPtr loc, IntPtr root, int cls, out int hwnd, int opts, out IntPtr disp);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int QSFn(IntPtr self, ref Guid sid, ref Guid iid, out IntPtr ppv);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PtrOutFn(IntPtr self, out IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CountFn(IntPtr self, uint flags, out int n);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ItemFn(IntPtr self, int i, out IntPtr pidl);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PosFn(IntPtr self, IntPtr pidl, out PT pt);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SpacingFn(IntPtr self, ref PT pt);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int NoArgFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PlaceFn(IntPtr self, uint cidl, IntPtr[] apidl, PT[] apt, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] static extern int MapWindowPoints(IntPtr from, IntPtr to, ref RECT r, int n);
    [DllImport("ole32.dll")] static extern void CoTaskMemFree(IntPtr p);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    static readonly Guid CLSID_ShellWindows = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    static Guid IID_IShellWindows = new Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85");
    static Guid IID_IServiceProvider = new Guid("6D5140C1-7436-11CE-8034-00AA006009FA");
    static Guid SID_STopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
    static Guid IID_IShellBrowser = new Guid("000214E2-0000-0000-C000-000000000046");
    static Guid IID_IFolderView = new Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE");

    static T Fn<T>(IntPtr obj, int slot) where T : class
    {
        IntPtr vt = Marshal.ReadIntPtr(obj);
        return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vt, slot * IntPtr.Size), typeof(T)) as T;
    }

    static void Rel(ref IntPtr p) { if (p != IntPtr.Zero) { try { Fn<RelFn>(p, 2)(p); } catch (Exception skipEx) { SbLog.Skip(skipEx); } p = IntPtr.Zero; } }

    // ---------- увімкнення, події ----------

    // rects - звідки брати прямокутники віджетів і нотаток (екранні координати)
    public static void Configure(bool on, Func<List<Rectangle>> rects)
    {
        source = rects;
        if (on == Enabled) return;
        Enabled = on;
        if (!on)
        {
            foreach (FileSystemWatcher w in watchers) try { w.EnableRaisingEvents = false; w.Dispose(); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            watchers.Clear();
            if (timer != null) timer.Stop();
            return;
        }
        // новий файл на робочому столі стає на перше вільне місце - може й під віджет
        foreach (Environment.SpecialFolder sf in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
        {
            try
            {
                string d = Environment.GetFolderPath(sf);
                if (d.Length == 0 || !Directory.Exists(d)) continue;
                FileSystemWatcher w = new FileSystemWatcher(d);
                w.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName;
                w.InternalBufferSize = 4096;
                FileSystemEventHandler h = delegate { Later(2000); };
                w.Created += h;
                w.Renamed += delegate { Later(2000); };
                w.EnableRaisingEvents = true;
                watchers.Add(w);
            }
            catch (Exception ex) { SbLog.Skip(ex); }
        }
        Later(3000);
    }

    static Control ui;
    public static void Bind(Control c) { ui = c; }

    // відкласти перевірку (кілька подій поспіль - одна перевірка); можна кликати з будь-якого потоку
    public static void Later(int ms)
    {
        if (!Enabled) return;
        Control c = ui;
        if (c == null || c.IsDisposed || !c.IsHandleCreated) return;
        try
        {
            c.BeginInvoke(new Action(delegate
            {
                if (timer == null) { timer = new Timer(); timer.Tick += delegate { timer.Stop(); Run(); }; }
                timer.Stop();
                timer.Interval = Math.Max(200, ms);
                timer.Start();
            }));
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    // ---------- головне ----------

    static void Run()
    {
        if (!Enabled || NeoBar.Paused || ScreenState.Away) return;
        List<Rectangle> blocks = source != null ? source() : null;
        if (blocks == null || blocks.Count == 0) return;
        try { LastMoved = Arrange(blocks); }
        catch (Exception ex) { State = "fail"; SbLog.W(SbLog.Widgets, "значки робочого столу: " + ex.Message); }
    }

    static int Arrange(List<Rectangle> screenBlocks)
    {
        IntPtr sw = IntPtr.Zero, disp = IntPtr.Zero, sp = IntPtr.Zero, sb = IntPtr.Zero, view = IntPtr.Zero, fv = IntPtr.Zero;
        object swObj = null;
        IntPtr var = Marshal.AllocCoTaskMem(32), var2 = Marshal.AllocCoTaskMem(32);
        List<IntPtr> pidls = new List<IntPtr>();
        try
        {
            for (int i = 0; i < 32; i++) { Marshal.WriteByte(var, i, 0); Marshal.WriteByte(var2, i, 0); }     // VT_EMPTY
            swObj = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellWindows));
            IntPtr unk = Marshal.GetIUnknownForObject(swObj);
            int hr = Fn<QIFn>(unk, 0)(unk, ref IID_IShellWindows, out sw);
            Rel(ref unk);
            if (hr != 0 || sw == IntPtr.Zero) throw new Exception("IShellWindows " + hr.ToString("X"));
            int hwnd;
            hr = Fn<FindSWFn>(sw, 15)(sw, var, var2, 8 /* SWC_DESKTOP */, out hwnd, 1 /* SWFO_NEEDDISPATCH */, out disp);
            if (hr != 0 || disp == IntPtr.Zero) throw new Exception("FindWindowSW " + hr.ToString("X"));
            if (Fn<QIFn>(disp, 0)(disp, ref IID_IServiceProvider, out sp) != 0) throw new Exception("IServiceProvider");
            if (Fn<QSFn>(sp, 3)(sp, ref SID_STopLevelBrowser, ref IID_IShellBrowser, out sb) != 0) throw new Exception("IShellBrowser");
            if (Fn<PtrOutFn>(sb, 15)(sb, out view) != 0 || view == IntPtr.Zero) throw new Exception("QueryActiveShellView");
            if (Fn<QIFn>(view, 0)(view, ref IID_IFolderView, out fv) != 0) throw new Exception("IFolderView");

            // автоупорядкування - Windows сама розставляє значки
            if (Fn<NoArgFn>(fv, 14)(fv) == 0) { State = "auto"; return 0; }
            State = "ok";

            IntPtr defView;
            if (Fn<PtrOutFn>(view, 3)(view, out defView) != 0) defView = IntPtr.Zero;
            IntPtr lv = defView != IntPtr.Zero ? FindWindowEx(defView, IntPtr.Zero, "SysListView32", null) : IntPtr.Zero;
            if (lv == IntPtr.Zero) lv = defView;
            if (lv == IntPtr.Zero) return 0;

            PT spc = new PT();
            Fn<SpacingFn>(fv, 12)(fv, ref spc);
            if (spc.X < 16 || spc.Y < 16) return 0;

            int n;
            if (Fn<CountFn>(fv, 7)(fv, 2 /* SVGIO_ALLVIEW */, out n) != 0 || n <= 0) return 0;
            n = Math.Min(n, 2000);
            List<PT> pos = new List<PT>(n);
            for (int i = 0; i < n; i++)
            {
                IntPtr p;
                if (Fn<ItemFn>(fv, 6)(fv, i, out p) != 0 || p == IntPtr.Zero) continue;
                PT pt;
                if (Fn<PosFn>(fv, 11)(fv, p, out pt) != 0) { CoTaskMemFree(p); continue; }
                pidls.Add(p);
                pos.Add(pt);
            }
            if (pidls.Count == 0) return 0;

            // екран -> координати робочого столу (вікна значків)
            Func<Rectangle, Rectangle> toLv = delegate (Rectangle s)
            {
                RECT rc = new RECT(); rc.L = s.Left; rc.T = s.Top; rc.R = s.Right; rc.B = s.Bottom;
                MapWindowPoints(IntPtr.Zero, lv, ref rc, 2);
                return Rectangle.FromLTRB(rc.L, rc.T, rc.R, rc.B);
            };
            List<Rectangle> blocks = new List<Rectangle>();
            foreach (Rectangle b in screenBlocks) { Rectangle r = toLv(b); r.Inflate(4, 4); blocks.Add(r); }
            List<Rectangle> areas = new List<Rectangle>();
            foreach (Screen s in Screen.AllScreens) areas.Add(toLv(s.WorkingArea));

            // сітка: від найлівішого й найвищого значка з кроком spacing
            int ox = int.MaxValue, oy = int.MaxValue;
            foreach (PT p in pos) { ox = Math.Min(ox, p.X); oy = Math.Min(oy, p.Y); }
            Func<PT, Rectangle> cell = delegate (PT p) { return new Rectangle(p.X, p.Y, spc.X, spc.Y); };
            Func<Rectangle, bool> hitsBlock = delegate (Rectangle c)
            {
                Rectangle inner = Rectangle.Inflate(c, -spc.X / 6, -spc.Y / 8);       // краєчок клітинки під рамкою - не страшно
                foreach (Rectangle b in blocks) if (b.IntersectsWith(inner)) return true;
                return false;
            };
            List<Rectangle> taken = new List<Rectangle>();
            List<int> bad = new List<int>();
            for (int i = 0; i < pos.Count; i++)
            {
                if (hitsBlock(cell(pos[i]))) bad.Add(i);
                else taken.Add(cell(pos[i]));
            }
            if (bad.Count == 0) return 0;

            List<IntPtr> movP = new List<IntPtr>();
            List<PT> movT = new List<PT>();
            foreach (int i in bad)
            {
                PT from = pos[i];
                Rectangle area = Rectangle.Empty;
                foreach (Rectangle a in areas) if (a.Contains(from.X + spc.X / 2, from.Y + spc.Y / 2)) { area = a; break; }
                if (area.IsEmpty && areas.Count > 0) area = areas[0];
                // кандидати - клітинки сітки в робочій області монітора; найближча вільна
                int x0 = ox + (int)Math.Ceiling((area.Left - ox) / (double)spc.X) * spc.X;
                int y0 = oy + (int)Math.Ceiling((area.Top - oy) / (double)spc.Y) * spc.Y;
                long best = long.MaxValue;
                PT bestP = new PT();
                for (int y = y0; y + spc.Y <= area.Bottom; y += spc.Y)
                    for (int x = x0; x + spc.X <= area.Right; x += spc.X)
                    {
                        Rectangle c = new Rectangle(x, y, spc.X, spc.Y);
                        if (hitsBlock(c)) continue;
                        bool busy = false;
                        foreach (Rectangle t in taken) if (Rectangle.Inflate(t, -spc.X / 4, -spc.Y / 4).IntersectsWith(c)) { busy = true; break; }
                        if (busy) continue;
                        long dx = x - from.X, dy = y - from.Y, d = dx * dx + dy * dy;
                        if (d < best) { best = d; bestP.X = x; bestP.Y = y; }
                    }
                if (best == long.MaxValue) continue;          // місця немає - лишаємо як є
                taken.Add(new Rectangle(bestP.X, bestP.Y, spc.X, spc.Y));
                movP.Add(pidls[i]);
                movT.Add(bestP);
            }
            if (movP.Count == 0) return 0;
            int r2 = Fn<PlaceFn>(fv, 16)(fv, (uint)movP.Count, movP.ToArray(), movT.ToArray(), 0x80 /* SVSI_POSITIONITEM */);
            SbLog.W(SbLog.Widgets, "значки робочого столу: переставлено з-під віджетів " + movP.Count + (r2 != 0 ? " (код " + r2.ToString("X") + ")" : ""));
            return r2 == 0 ? movP.Count : 0;
        }
        finally
        {
            foreach (IntPtr p in pidls) CoTaskMemFree(p);
            Rel(ref fv); Rel(ref view); Rel(ref sb); Rel(ref sp); Rel(ref disp); Rel(ref sw);
            if (swObj != null) try { Marshal.ReleaseComObject(swObj); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
            Marshal.FreeCoTaskMem(var); Marshal.FreeCoTaskMem(var2);
        }
    }
}
