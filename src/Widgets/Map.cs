using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

// ======================================================================
//  Карта OpenStreetMap для вибору місця (тайли кешуються на диску на 30 днів)
// ======================================================================
sealed class MapView : Control
{
    public double MarkLat = double.NaN, MarkLon = double.NaN;
    double cx, cy;                  // центр у світових пікселях поточного масштабу
    int zoom = 5;
    public event Action Picked;

    readonly Dictionary<string, Bitmap> mem = new Dictionary<string, Bitmap>();
    readonly LinkedList<string> order = new LinkedList<string>();
    readonly HashSet<string> loading = new HashSet<string>();
    readonly Queue<string> queue = new Queue<string>();
    int workers;
    bool drag, moved;
    Point downAt, last;

    public MapView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(170, 211, 223);
        Cursor = Cursors.Hand;
        CenterOn(50.45, 30.52, 5);
    }

    const double TS = 256.0;

    static double WX(double lon, int z) { return (lon + 180.0) / 360.0 * TS * (1 << z); }
    static double WY(double lat, int z)
    {
        lat = Math.Max(-85.0511, Math.Min(85.0511, lat));
        double r = lat * Math.PI / 180.0;
        return (1.0 - Math.Log(Math.Tan(r) + 1.0 / Math.Cos(r)) / Math.PI) / 2.0 * TS * (1 << z);
    }
    static double Lon(double x, int z) { return x / (TS * (1 << z)) * 360.0 - 180.0; }
    static double Lat(double y, int z)
    {
        double n = Math.PI - 2.0 * Math.PI * y / (TS * (1 << z));
        return 180.0 / Math.PI * Math.Atan(0.5 * (Math.Exp(n) - Math.Exp(-n)));
    }

    public void CenterOn(double lat, double lon, int z)
    {
        if (double.IsNaN(lat) || double.IsNaN(lon)) return;
        zoom = Math.Max(2, Math.Min(17, z));
        cx = WX(lon, zoom); cy = WY(lat, zoom);
        Invalidate();
    }

    public void SetMark(double lat, double lon, bool center)
    {
        MarkLat = lat; MarkLon = lon;
        if (center) CenterOn(lat, lon, Math.Max(zoom, 9));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.Clear(BackColor);
        int n = 1 << zoom;
        double left = cx - Width / 2.0, top = cy - Height / 2.0;
        int tx0 = (int)Math.Floor(left / TS), ty0 = (int)Math.Floor(top / TS);
        int tx1 = (int)Math.Floor((left + Width) / TS), ty1 = (int)Math.Floor((top + Height) / TS);
        for (int ty = ty0; ty <= ty1; ty++)
        {
            if (ty < 0 || ty >= n) continue;
            for (int tx = tx0; tx <= tx1; tx++)
            {
                int wx = ((tx % n) + n) % n;
                int px = (int)Math.Round(tx * TS - left), py = (int)Math.Round(ty * TS - top);
                Bitmap b = Tile(zoom, wx, ty);
                if (b != null) g.DrawImage(b, px, py, 256, 256);
                else
                {
                    // поки тайл вантажиться - збільшений шматок з меншого масштабу
                    Bitmap up = zoom > 0 ? Cached(zoom - 1, wx / 2, ty / 2) : null;
                    if (up != null)
                        g.DrawImage(up, new Rectangle(px, py, 256, 256), new Rectangle((wx % 2) * 128, (ty % 2) * 128, 128, 128), GraphicsUnit.Pixel);
                }
            }
        }
        if (!double.IsNaN(MarkLat))
        {
            float mx = (float)(WX(MarkLon, zoom) - left), my = (float)(WY(MarkLat, zoom) - top);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddArc(mx - 9, my - 30, 18, 18, 150, 240);
                p.AddLine(mx + 7.8f, my - 16.5f, mx, my);
                p.CloseFigure();
                using (SolidBrush b = new SolidBrush(Color.FromArgb(225, 45, 45))) g.FillPath(b, p);
                using (Pen pn = new Pen(Color.FromArgb(120, 0, 0, 0), 1.2f)) g.DrawPath(pn, p);
            }
            g.FillEllipse(Brushes.White, mx - 3.5f, my - 24.5f, 7, 7);
        }
        string attr = L.T("© учасники OpenStreetMap");
        Size s = TextRenderer.MeasureText(attr, Font);
        Rectangle ar = new Rectangle(Width - s.Width - 6, Height - s.Height - 2, s.Width + 6, s.Height + 2);
        using (SolidBrush b = new SolidBrush(Color.FromArgb(200, 255, 255, 255))) g.FillRectangle(b, ar);
        TextRenderer.DrawText(g, attr, Font, ar, Color.FromArgb(60, 60, 60), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        string zh = L.T("Коліщатко - масштаб, перетягування - рух, клік - вибрати місце");
        Size zs = TextRenderer.MeasureText(zh, Font);
        Rectangle zr = new Rectangle(0, 0, Math.Min(Width, zs.Width + 8), zs.Height + 4);
        using (SolidBrush b = new SolidBrush(Color.FromArgb(190, 255, 255, 255))) g.FillRectangle(b, zr);
        TextRenderer.DrawText(g, zh, Font, zr, Color.FromArgb(50, 50, 50), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        using (Pen bp = new Pen(Color.FromArgb(120, 120, 120))) g.DrawRectangle(bp, 0, 0, Width - 1, Height - 1);
    }

    Bitmap Cached(int z, int x, int y)
    {
        Bitmap b;
        lock (mem) mem.TryGetValue(z + "/" + x + "/" + y, out b);
        return b;
    }

    Bitmap Tile(int z, int x, int y)
    {
        string key = z + "/" + x + "/" + y;
        Bitmap b;
        lock (mem)
        {
            if (mem.TryGetValue(key, out b)) return b;
            if (loading.Contains(key)) return null;
            loading.Add(key);
            queue.Enqueue(key);
        }
        Pump();
        return null;
    }

    void Pump()
    {
        lock (mem)
        {
            while (workers < 4 && queue.Count > 0)
            {
                string key = queue.Dequeue();
                workers++;
                Util.Bg(delegate { Load(key); });
            }
        }
    }

    void Load(string key)
    {
        Bitmap bmp = null;
        try
        {
            string file = Path.Combine(NetCache.Dir("tiles"), key.Replace('/', Path.DirectorySeparatorChar) + ".png");
            byte[] data = null;
            if (File.Exists(file) && (DateTime.Now - File.GetLastWriteTime(file)).TotalDays < 30) data = File.ReadAllBytes(file);
            else
            {
                try
                {
                    data = NetCache.Download("https://tile.openstreetmap.org/" + key + ".png", 10000, null);
                    NetCache.Save(file, data);
                }
                catch { if (File.Exists(file)) data = File.ReadAllBytes(file); }
            }
            if (data != null)
                using (MemoryStream ms = new MemoryStream(data))
                using (Image im = Image.FromStream(ms))
                    bmp = new Bitmap(im);
        }
        catch { }
        lock (mem)
        {
            workers--;
            loading.Remove(key);
            if (bmp != null)
            {
                mem[key] = bmp;
                order.AddLast(key);
                while (order.Count > 160)
                {
                    string old = order.First.Value;
                    order.RemoveFirst();
                    Bitmap ob;
                    if (mem.TryGetValue(old, out ob)) { mem.Remove(old); ob.Dispose(); }
                }
            }
        }
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(delegate { Invalidate(); Pump(); })); } catch { }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left) return;
        drag = true; moved = false;
        downAt = last = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!drag) return;
        if (Math.Abs(e.X - downAt.X) + Math.Abs(e.Y - downAt.Y) > 4) moved = true;
        if (!moved) return;
        cx -= e.X - last.X; cy -= e.Y - last.Y;
        double max = TS * (1 << zoom);
        cy = Math.Max(0, Math.Min(max, cy));
        last = e.Location;
        Cursor = Cursors.SizeAll;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!drag) return;
        drag = false;
        Cursor = Cursors.Hand;
        if (moved) return;
        double left = cx - Width / 2.0, top = cy - Height / 2.0;
        double lon = Lon(left + e.X, zoom), lat = Lat(top + e.Y, zoom);
        while (lon < -180) lon += 360;
        while (lon > 180) lon -= 360;
        MarkLat = Math.Round(lat, 4); MarkLon = Math.Round(lon, 4);
        Invalidate();
        Action p = Picked;
        if (p != null) p();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        WheelAt(e.Delta, e.Location);
    }

    // масштаб коліщатком у точці p (координати карти); викликає й вікно-хазяїн, коли фокус не на карті
    public void WheelAt(int delta, Point p)
    {
        int nz = Math.Max(2, Math.Min(17, zoom + (delta > 0 ? 1 : -1)));
        if (nz == zoom) return;
        double left = cx - Width / 2.0, top = cy - Height / 2.0;
        double lon = Lon(left + p.X, zoom), lat = Lat(top + p.Y, zoom);
        zoom = nz;
        cx = WX(lon, zoom) - p.X + Width / 2.0;
        cy = WY(lat, zoom) - p.Y + Height / 2.0;
        lock (mem) queue.Clear();
        lock (mem) loading.Clear();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) lock (mem) { foreach (Bitmap b in mem.Values) b.Dispose(); mem.Clear(); queue.Clear(); }
        base.Dispose(disposing);
    }
}
