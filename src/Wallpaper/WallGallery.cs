using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// ======================================================================
//  Бібліотека шпалер: окрема тека для кожного виду (зображення, слайд-шоу, живі).
//  Додані файли КОПІЮЮТЬСЯ сюди - оригінал можна переміщати чи видаляти, шпалери лишаються.
//  Тека спільна для всіх профілів; який файл обрано - окремо для кожного профілю (wallpaper.ini).
// ======================================================================
static class WallLib
{
    public const int Image = 0, Slideshow = 1, Live = 2;
    public static readonly string[] ImgExt = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".jxr" };
    public static readonly string[] VidExt = { ".mp4", ".m4v", ".wmv", ".avi", ".mov", ".mkv", ".gif" };
    static readonly string[] Sub = { "images", "slideshow", "live" };

    public static string Root { get { return Path.Combine(Prof.Root, "wallpapers"); } }

    public static string Dir(int kind)
    {
        string d = Path.Combine(Root, Sub[Math.Max(0, Math.Min(2, kind))]);
        try { Directory.CreateDirectory(d); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return d;
    }

    public static bool Accepts(int kind, string file)
    {
        string x = Path.GetExtension(file ?? "").ToLowerInvariant();
        return Array.IndexOf(kind == Live ? VidExt : ImgExt, x) >= 0;
    }

    public static string Filter(int kind)
    {
        string[] ex = kind == Live ? VidExt : ImgExt;
        string mask = "";
        foreach (string x in ex) mask += (mask.Length > 0 ? ";" : "") + "*" + x;
        return (kind == Live ? L.T("Відео й анімації") : L.T("Зображення")) + "|" + mask + L.T("|Усі файли|*.*");
    }

    public static List<string> List(int kind)
    {
        List<string> r = new List<string>();
        try
        {
            foreach (string f in Directory.GetFiles(Dir(kind)))
                if (Accepts(kind, f)) r.Add(f);
        }
        catch (Exception skipEx) { SbLog.Skip(skipEx); }
        r.Sort(StringComparer.CurrentCultureIgnoreCase);
        return r;
    }

    // скопіювати файли (і для слайд-шоу - файли з тек) у теку галереї; повертає шляхи копій
    public static List<string> Add(int kind, IEnumerable<string> paths)
    {
        List<string> res = new List<string>();
        string dir = Dir(kind);
        foreach (string p in paths)
        {
            try
            {
                if (Directory.Exists(p))
                {
                    if (kind == Slideshow) foreach (string f in Directory.GetFiles(p)) CopyOne(kind, dir, f, res);
                }
                else CopyOne(kind, dir, p, res);
            }
            catch (Exception ex) { Util.ErrorLog(ex); }
        }
        return res;
    }

    static void CopyOne(int kind, string dir, string file, List<string> res)
    {
        if (!File.Exists(file) || !Accepts(kind, file)) return;
        string full = Path.GetFullPath(file);
        string where = Path.GetDirectoryName(full) ?? "";
        if (string.Equals(where.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) { res.Add(full); return; }
        string name = Path.GetFileName(full), dst = Path.Combine(dir, name);
        int n = 1;
        while (File.Exists(dst))
        {
            FileInfo a = new FileInfo(full), b = new FileInfo(dst);
            if (a.Length == b.Length && a.LastWriteTimeUtc == b.LastWriteTimeUtc) { res.Add(dst); return; }     // цей самий файл уже в галереї
            n++;
            dst = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + " (" + n + ")" + Path.GetExtension(name));
        }
        File.Copy(full, dst);
        res.Add(dst);
    }

    // у кошик (можна відновити); якщо кошик недоступний - видалити
    public static bool Remove(string file)
    {
        try
        {
            if (!File.Exists(file)) return true;
            if (ToRecycleBin(file)) return !File.Exists(file);
            File.Delete(file);
            return true;
        }
        catch (Exception ex) { Util.ErrorLog(ex); return false; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FileOp64
    {
        public IntPtr hwnd; public uint func; public string from; public string to; public ushort flags;
        public int aborted; public IntPtr maps; public string title;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    struct FileOp32
    {
        public IntPtr hwnd; public uint func; public string from; public string to; public ushort flags;
        public int aborted; public IntPtr maps; public string title;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    static extern int ShFileOp64(ref FileOp64 op);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    static extern int ShFileOp32(ref FileOp32 op);

    static bool ToRecycleBin(string file)
    {
        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;
        ushort fl = (ushort)(FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOERRORUI);
        try
        {
            if (IntPtr.Size == 8)
            {
                FileOp64 o = new FileOp64(); o.func = FO_DELETE; o.from = file + "\0"; o.flags = fl;
                return ShFileOp64(ref o) == 0 && o.aborted == 0;
            }
            FileOp32 q = new FileOp32(); q.func = FO_DELETE; q.from = file + "\0"; q.flags = fl;
            return ShFileOp32(ref q) == 0 && q.aborted == 0;
        }
        catch { return false; }
    }
}

// ----------------------------------------------------------------------
//  Мініатюри: у фоновому потоці (STA), по одній; зображення - засобами GDI+, решта (відео) - мініатюра оболонки
// ----------------------------------------------------------------------
static class WallThumbs
{
    static readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>();
    static readonly HashSet<string> asked = new HashSet<string>();
    static readonly Queue<string[]> queue = new Queue<string[]>();       // { ключ, шлях, ширина, висота }
    static readonly object gate = new object();
    static Thread worker;
    public static event Action Ready;

    static string Key(string file, int w, int h)
    {
        long len = 0, ticks = 0;
        try { FileInfo fi = new FileInfo(file); len = fi.Length; ticks = fi.LastWriteTimeUtc.Ticks; } catch (Exception skipEx) { SbLog.Skip(skipEx); }
        return file + "|" + w + "x" + h + "|" + len + "|" + ticks;
    }

    // готова мініатюра або null (тоді її вже поставлено в чергу)
    public static Bitmap Get(string file, int w, int h)
    {
        string k = Key(file, w, h);
        lock (gate)
        {
            Bitmap b;
            if (cache.TryGetValue(k, out b)) return b;                 // null у кеші - мініатюру зробити не вдалося
            if (cache.ContainsKey(k) || asked.Contains(k)) return null;
            asked.Add(k);
            queue.Enqueue(new string[] { k, file, w.ToString(), h.ToString() });
            if (worker == null)
            {
                worker = new Thread(Work);
                worker.SetApartmentState(ApartmentState.STA);
                worker.IsBackground = true;
                worker.Priority = ThreadPriority.BelowNormal;
                worker.Start();
            }
        }
        return null;
    }

    // викликати з потоку інтерфейсу перед малюванням: забагато мініатюр у пам'яті - почати спочатку
    public static void Trim()
    {
        lock (gate)
        {
            if (cache.Count <= 400) return;
            foreach (Bitmap b in cache.Values) if (b != null) b.Dispose();
            cache.Clear();
            asked.Clear();
        }
    }

    static void Work()
    {
        while (true)
        {
            string[] job;
            lock (gate)
            {
                if (queue.Count == 0) { worker = null; return; }
                job = queue.Dequeue();
            }
            Bitmap bmp = null;
            try { bmp = Make(job[1], int.Parse(job[2]), int.Parse(job[3])); }
            catch (Exception ex) { Util.ErrorLog(ex); }
            lock (gate) { cache[job[0]] = bmp; asked.Remove(job[0]); }
            Action r = Ready;
            if (r != null) { try { r(); } catch (Exception skipEx) { SbLog.Skip(skipEx); } }
        }
    }

    static Bitmap Make(string file, int w, int h)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        bool gdi = ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp" || ext == ".gif" || ext == ".tif" || ext == ".tiff";
        if (gdi)
        {
            try
            {
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (Image im = Image.FromStream(fs, false, false))
                    return Cover(im, w, h);
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
        }
        return ShellThumb(file, w, h);
    }

    // вписати зображення в плитку w x h із заповненням (обрізаючи краї)
    static Bitmap Cover(Image im, int w, int h)
    {
        Bitmap b = new Bitmap(w, h);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Black);
            float k = Math.Max((float)w / im.Width, (float)h / im.Height);
            float sw = w / k, sh = h / k;
            g.DrawImage(im, new Rectangle(0, 0, w, h), (im.Width - sw) / 2f, (im.Height - sh) / 2f, sw, sh, GraphicsUnit.Pixel);
        }
        return b;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);


    static Bitmap ShellThumb(string file, int w, int h)
    {
        IShellItemImageFactory f = null;
        try
        {
            Guid iid = new Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B");
            if (SHCreateItemFromParsingName(file, IntPtr.Zero, ref iid, out f) != 0 || f == null) return null;
            SIZE sz = new SIZE(); sz.cx = w; sz.cy = h;
            IntPtr hb;
            if (f.GetImage(sz, 8 /* лише мініатюра, без піктограми */, out hb) != 0 || hb == IntPtr.Zero) return null;
            try
            {
                using (Bitmap src = Image.FromHbitmap(hb)) return Cover(src, w, h);
            }
            finally { Win.DeleteObject(hb); }
        }
        catch { return null; }
        finally { if (f != null) { try { Marshal.ReleaseComObject(f); } catch (Exception skipEx) { SbLog.Skip(skipEx); } } }
    }
}

// ----------------------------------------------------------------------
//  Галерея: сітка мініатюр файлів теки; клік обирає, Delete прибирає в кошик, файли можна кидати мишею
// ----------------------------------------------------------------------
sealed class WallGallery : Control
{
    int kind = -1, sel = -1, hot = -1;
    List<string> files = new List<string>();
    readonly ThinBar bar = new ThinBar();
    bool busy;

    public string Info = "";
    public event EventHandler SelectionChanged;      // обраний файл змінився (в тому числі після видалення)
    public event EventHandler InfoChanged;

    public int Kind { get { return kind; } }
    public string Selected { get { return sel >= 0 && sel < files.Count ? files[sel] : ""; } }

    public WallGallery()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.Opaque, true);
        TabStop = true;
        DropFiles.Prepare(this);
        Font = SUi.Small;
        WallThumbs.Ready += OnThumb;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WallThumbs.Ready -= OnThumb;
        base.Dispose(disposing);
    }

    void OnThumb()
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(delegate { Invalidate(); })); } catch (Exception skipEx) { SbLog.Skip(skipEx); }
    }

    void SetInfo(string s)
    {
        Info = s ?? "";
        EventHandler h = InfoChanged;
        if (h != null) h(this, EventArgs.Empty);
    }

    // показати файли виду kind; current - файл, який зараз обрано (повний або відносний шлях)
    public void SetKind(int k, string current)
    {
        kind = k;
        SetInfo("");
        Reload(string.IsNullOrEmpty(current) ? null : Util.ResolvePath(current), false);
    }

    void Reload(string select, bool notify)
    {
        string prev = Selected;
        files = kind < 0 ? new List<string>() : WallLib.List(kind);
        sel = -1;
        if (select != null)
            for (int i = 0; i < files.Count; i++)
                if (string.Equals(files[i], select, StringComparison.OrdinalIgnoreCase)) { sel = i; break; }
        hot = -1;
        Metrics();
        if (sel >= 0) EnsureVisible(sel);
        Invalidate();
        if (notify && !string.Equals(prev, Selected, StringComparison.OrdinalIgnoreCase)) FireSel();
    }

    void FireSel() { EventHandler h = SelectionChanged; if (h != null) h(this, EventArgs.Empty); }

    // ---------- розміри ----------
    int TileW { get { return SUi.S(172); } }
    int ThumbH { get { return SUi.S(92); } }
    int TileH { get { return ThumbH + SUi.S(28); } }
    int Gap { get { return SUi.S(10); } }
    int Pad { get { return SUi.S(10); } }

    int Cols()
    {
        int usable = Width - Pad * 2 - ThinBar.W;
        return Math.Max(1, (usable + Gap) / (TileW + Gap));
    }

    void Metrics()
    {
        int rows = (files.Count + Cols() - 1) / Cols();
        bar.View = Height;
        bar.Content = rows == 0 ? 0 : Pad * 2 + rows * TileH + (rows - 1) * Gap;
        bar.Pos = bar.Clamp(bar.Pos);
    }

    Rectangle TileRect(int i)
    {
        int c = Cols();
        int cols = i % c, row = i / c;
        int total = c * TileW + (c - 1) * Gap;
        int x0 = Pad + Math.Max(0, (Width - Pad * 2 - ThinBar.W - total) / 2);
        return new Rectangle(x0 + cols * (TileW + Gap), Pad + row * (TileH + Gap) - bar.Pos, TileW, TileH);
    }

    int HitTest(Point p)
    {
        if (bar.Need && p.X >= Width - ThinBar.W) return -1;
        for (int i = 0; i < files.Count; i++)
        {
            Rectangle r = TileRect(i);
            if (r.Bottom < 0) continue;
            if (r.Top > Height) break;
            if (r.Contains(p)) return i;
        }
        return -1;
    }

    void EnsureVisible(int i)
    {
        Rectangle r = TileRect(i);
        if (r.Top < 0) bar.Pos = bar.Clamp(bar.Pos + r.Top - Pad);
        else if (r.Bottom > Height) bar.Pos = bar.Clamp(bar.Pos + r.Bottom - Height + Pad);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Metrics(); Invalidate(); }

    // ---------- малювання ----------
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(FormTheme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle all = new Rectangle(0, 0, Width - 1, Height - 1);
        int rad = SUi.R(2);
        using (SolidBrush b = new SolidBrush(FormTheme.Input)) Util.FillRound(g, b, all, rad);
        using (Pen pn = new Pen(Focused ? FormTheme.Acc : FormTheme.Line))
        using (GraphicsPath gp = Util.Round(all, rad)) g.DrawPath(pn, gp);

        WallThumbs.Trim();
        g.SetClip(new Rectangle(1, 1, Width - 2, Height - 2));
        if (files.Count == 0)
        {
            string t = busy ? L.T("Копіюю файли…") : (kind < 0 ? "" : L.T("Тут поки порожньо.\nДодайте файли кнопкою нижче або перетягніть їх сюди."));
            TextRenderer.DrawText(g, t, SUi.Base, ClientRectangle, FormTheme.Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
        else
        {
            for (int i = 0; i < files.Count; i++)
            {
                Rectangle r = TileRect(i);
                if (r.Bottom < 0) continue;
                if (r.Top > Height) break;
                DrawTile(g, i, r);
            }
        }
        g.ResetClip();
        if (busy && files.Count > 0)
        {
            Rectangle st = new Rectangle(Pad, Height - SUi.S(30), Width - Pad * 2, SUi.S(22));
            using (SolidBrush b = new SolidBrush(Color.FromArgb(220, FormTheme.Card))) Util.FillRound(g, b, st, SUi.S(6));
            TextRenderer.DrawText(g, L.T("Копіюю файли…"), SUi.Small, st, FormTheme.Fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
        bar.Paint(g, new Rectangle(0, 0, Width, Height));
    }

    void DrawTile(Graphics g, int i, Rectangle r)
    {
        string f = files[i];
        bool isSel = i == sel, isHot = i == hot;
        Rectangle th = new Rectangle(r.X, r.Y, r.Width, ThumbH);
        int rad = SUi.S(6);
        Bitmap bmp = WallThumbs.Get(f, th.Width, th.Height);
        if (bmp != null)
        {
            GraphicsPath clip = Util.Round(th, rad);
            try
            {
                using (Region old = g.Clip)
                {
                    g.SetClip(clip, CombineMode.Intersect);
                    g.DrawImageUnscaled(bmp, th.Location);
                    g.Clip = old;
                }
            }
            finally { clip.Dispose(); }
        }
        else
        {
            using (SolidBrush b = new SolidBrush(Util.Blend(FormTheme.Input, FormTheme.Fg, 0.08f))) Util.FillRound(g, b, th, rad);
            string ext = Path.GetExtension(f).TrimStart('.').ToUpperInvariant();
            TextRenderer.DrawText(g, ext, SUi.Base, th, FormTheme.Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
        if (isSel || isHot)
        {
            using (Pen pn = new Pen(isSel ? FormTheme.Acc : Util.Blend(FormTheme.Input, FormTheme.Fg, 0.35f), isSel ? SUi.S(2) : 1))
            using (GraphicsPath gp = Util.Round(Rectangle.Inflate(th, -1, -1), rad)) g.DrawPath(pn, gp);
        }
        Rectangle cap = new Rectangle(r.X, th.Bottom + SUi.S(3), r.Width, r.Bottom - th.Bottom - SUi.S(3));
        TextRenderer.DrawText(g, Path.GetFileNameWithoutExtension(f), SUi.Small, cap, isSel ? FormTheme.Fg : FormTheme.Sub,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }

    // ---------- миша й клавіатура ----------
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left) return;
        if (bar.MouseDown(this, e, new Rectangle(0, 0, Width, Height), delegate (int v) { bar.Pos = v; })) return;
        int i = HitTest(e.Location);
        if (i >= 0 && i != sel) { sel = i; FireSel(); Invalidate(); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int np;
        bool ch = bar.Move(e.Location, new Rectangle(0, 0, Width, Height), out np);
        if (bar.Dragging) bar.Pos = np;
        int h = bar.Dragging ? -1 : HitTest(e.Location);
        if (h != hot) { hot = h; ch = true; }
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        if (ch) Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        bar.MouseUp(this);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hot != -1 || bar.Hot) { hot = -1; bar.Hot = false; Invalidate(); }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!bar.Need) return;
        bar.Pos = bar.Clamp(bar.Pos - Math.Sign(e.Delta) * (TileH + Gap) / 2);
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override bool IsInputKey(Keys k)
    {
        return k == Keys.Delete || k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (files.Count == 0) return;
        int c = Cols(), n = sel;
        switch (e.KeyCode)
        {
            case Keys.Delete: RemoveSelected(); e.Handled = true; return;
            case Keys.Left: n = sel < 0 ? 0 : sel - 1; break;
            case Keys.Right: n = sel < 0 ? 0 : sel + 1; break;
            case Keys.Up: n = sel < 0 ? 0 : sel - c; break;
            case Keys.Down: n = sel < 0 ? 0 : sel + c; break;
            default: return;
        }
        e.Handled = true;
        n = Math.Max(0, Math.Min(files.Count - 1, n));
        if (n != sel) { sel = n; EnsureVisible(sel); FireSel(); Invalidate(); }
    }

    // ---------- додавання, видалення, перетягування ----------
    public void AddFiles(string[] paths)
    {
        if (busy || kind < 0 || paths == null || paths.Length == 0) return;
        busy = true;
        SetInfo("");
        Invalidate();
        int k = kind;
        ThreadPool.QueueUserWorkItem(delegate
        {
            List<string> added = new List<string>();
            try { added = WallLib.Add(k, paths); } catch (Exception ex) { Util.ErrorLog(ex); }
            try
            {
                BeginInvoke(new Action(delegate
                {
                    busy = false;
                    if (kind != k) { Invalidate(); return; }
                    string pick = added.Count > 0 && k != WallLib.Slideshow ? added[added.Count - 1] : Selected;
                    Reload(pick, true);
                    SetInfo(added.Count > 0 ? L.T("Додано файлів: ") + added.Count : L.T("Нічого не додано: формат не підтримується або файл недоступний"));
                }));
            }
            catch (Exception skipEx) { SbLog.Skip(skipEx); }
        });
    }

    public void RemoveSelected()
    {
        string f = Selected;
        if (f.Length == 0 || busy) return;
        int at = sel;
        if (!WallLib.Remove(f)) { SetInfo(L.T("Не вдалося видалити файл")); return; }
        Reload(null, false);
        if (files.Count > 0 && kind != WallLib.Slideshow) sel = Math.Min(at, files.Count - 1);
        FireSel();
        SetInfo(L.T("Видалено в кошик: ") + Path.GetFileName(f));
        Invalidate();
    }

    public void OpenFolder()
    {
        if (kind < 0) return;
        try { System.Diagnostics.Process.Start("explorer.exe", "\"" + WallLib.Dir(kind) + "\""); } catch (Exception ex) { Util.ErrorLog(ex); }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DropFiles.Register(this);
    }

    protected override void WndProc(ref Message m)
    {
        string[] f = DropFiles.Take(ref m);
        if (f != null) { AddFiles(f); return; }
        base.WndProc(ref m);
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        e.Effect = !busy && kind >= 0 && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effect = !busy && kind >= 0 && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        string[] p = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (p != null) AddFiles(p);
    }
}
