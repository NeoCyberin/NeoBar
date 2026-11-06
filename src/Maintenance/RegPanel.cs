using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

// Вкладка «Реєстр» вікна очищення: категорії з кількістю знахідок, розгортання до окремих записів,
// зняття позначок з окремих записів, виправлення з резервною копією й відновлення з копії.
sealed partial class CleanWindow
{
    int scrollR;
    string rmsg = "";
    bool rbad;

    sealed class RRow
    {
        public int H;
        public RegCat Cat;
        public RegItem Item;
        public string Text;
    }

    void Toggle(Graphics g, Rectangle tr, bool on)
    {
        int sh = tr.Height;
        using (SolidBrush b = new SolidBrush(on ? Util.Blend(P.Bg, P.Acc, 0.7f) : Util.Blend(P.Bg, P.Fg, 0.22f))) Util.FillRound(g, b, tr, sh / 2);
        int k = sh - Px(6);
        using (SolidBrush b = new SolidBrush(on ? Color.White : Util.Blend(P.Bg, P.Fg, 0.7f))) g.FillEllipse(b, new Rectangle(on ? tr.Right - k - Px(3) : tr.X + Px(3), tr.Y + Px(3), k, k));
    }

    void CheckBox(Graphics g, Rectangle r, bool on)
    {
        using (GraphicsPath gp = new GraphicsPath())
        {
            int d = Px(4);
            gp.AddArc(r.X, r.Y, d, d, 180, 90); gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            if (on) using (SolidBrush b = new SolidBrush(Util.Blend(P.Bg, P.Acc, 0.75f))) g.FillPath(b, gp);
            using (Pen p = new Pen(on ? P.Acc : Util.Blend(P.Bg, P.Fg, 0.45f), 1.3f)) g.DrawPath(p, gp);
        }
        if (on)
        {
            SmoothingMode sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen p = new Pen(Color.White, Math.Max(1.6f, Px(2)))) g.DrawLines(p, new PointF[] { new PointF(r.X + r.Width * 0.24f, r.Y + r.Height * 0.52f), new PointF(r.X + r.Width * 0.43f, r.Y + r.Height * 0.72f), new PointF(r.X + r.Width * 0.77f, r.Y + r.Height * 0.3f) });
            g.SmoothingMode = sm;
        }
    }

    void Arrow(Graphics g, Rectangle r, bool open, Color c)
    {
        SmoothingMode sm = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, s = r.Width * 0.3f;
        PointF[] pt = open ? new PointF[] { new PointF(cx - s, cy - s * 0.6f), new PointF(cx + s, cy - s * 0.6f), new PointF(cx, cy + s * 0.8f) }
                           : new PointF[] { new PointF(cx - s * 0.6f, cy - s), new PointF(cx - s * 0.6f, cy + s), new PointF(cx + s * 0.8f, cy) };
        using (SolidBrush b = new SolidBrush(c)) g.FillPolygon(b, pt);
        g.SmoothingMode = sm;
    }

    void RegMsg(string s, bool bad) { rmsg = s; rbad = bad; Invalidate(); }

    void PaintReg(Graphics g, Rectangle r)
    {
        RegClean.Init();
        bool busy = RegClean.Busy, fixing = RegClean.Fixing;
        bool scanned = RegClean.LastAt != DateTime.MinValue;
        int sel = RegClean.Selected();
        Rectangle top = new Rectangle(r.X, r.Y, r.Width, Px(86));
        Card(g, top);
        string cap, big;
        if (busy) { cap = RegClean.Stage; big = "…"; }
        else if (fixing) { cap = L.T("Виправлення (резервна копія вже збережена)"); big = "…"; }
        else if (!scanned) { cap = L.T("Реєстр ще не перевірено"); big = "—"; }
        else { cap = L.T("Знайдено проблем (вибране)"); big = sel.ToString(); }
        TxtL(g, cap, FS, new Rectangle(top.X + Px(14), top.Y + Px(10), top.Width - Px(180), Px(18)), P.Sub);
        TxtL(g, big, FBig, new Rectangle(top.X + Px(14), top.Y + Px(30), top.Width - Px(180), Px(44)), P.Fg);
        if (busy) Btn(g, new Rectangle(top.Right - Px(150), top.Y + (top.Height - Px(36)) / 2, Px(136), Px(36)), L.T("Зупинити"), delegate { RegClean.Stop = true; }, 0, true);
        else
        {
            Btn(g, new Rectangle(top.Right - Px(150), top.Y + Px(10), Px(136), Px(30)), scanned ? L.T("Сканувати знову") : L.T("Сканувати"), delegate { rmsg = ""; RegClean.Scan(); Invalidate(); }, 0, !fixing);
            Btn(g, new Rectangle(top.Right - Px(150), top.Y + Px(46), Px(136), Px(30)), L.T("Виправити"), RegFix, 1, sel > 0 && !fixing);
        }
        int y = top.Bottom + Px(12);

        int footH = Px(104);
        Rectangle list = new Rectangle(r.X, y, r.Width, r.Bottom - y - footH);
        List<RRow> rows = new List<RRow>();
        foreach (RegCat c in RegClean.Cats)
        {
            RRow h = new RRow(); h.H = Px(58); h.Cat = c; rows.Add(h);
            if (!c.Open) continue;
            List<RegItem> its = c.Items;
            for (int i = 0; i < its.Count; i++)
            {
                if (i >= 60)
                {
                    RRow m = new RRow(); m.H = Px(26); m.Cat = c; m.Text = L.T("…ще ") + (its.Count - 60) + L.T(" (вони теж будуть виправлені, якщо категорія ввімкнена)"); rows.Add(m);
                    break;
                }
                RRow ir = new RRow(); ir.H = Px(32); ir.Cat = c; ir.Item = its[i]; rows.Add(ir);
            }
        }
        int total = 0;
        foreach (RRow rw in rows) total += rw.H;
        int maxScroll = Math.Max(0, total - list.Height);
        scrollR = Math.Max(0, Math.Min(maxScroll, scrollR));
        GraphicsState st = g.Save();
        g.SetClip(list);
        int ry = list.Y - scrollR;
        bool lock0 = busy || fixing;
        foreach (RRow rw in rows)
        {
            Rectangle rr = new Rectangle(list.X, ry, list.Width - Px(8), rw.H - Px(6));
            ry += rw.H;
            if (rr.Bottom < list.Y || rr.Top > list.Bottom) continue;
            Rectangle vis = Rectangle.Intersect(rr, list);
            RegCat c = rw.Cat;
            if (rw.Item == null && rw.Text == null)
            {
                int n = c.Items.Count;
                Card(g, rr, Over(rr) ? Util.Blend(P.Card, P.Fg, 0.05f) : P.Card);
                int sw = Px(36), sh = Px(20);
                Rectangle tr = new Rectangle(rr.Right - sw - Px(12), rr.Y + Px(10), sw, sh);
                Toggle(g, tr, c.On);
                string cnt = !scanned ? "" : (c.Note.Length > 0 ? "—" : n.ToString());
                int cw = Px(46);
                TxtR(g, cnt, FB, new Rectangle(tr.X - cw - Px(8), rr.Y + Px(6), cw, Px(26)), n > 0 ? P.Fg : P.Sub);
                if (n > 0) Arrow(g, new Rectangle(rr.X + Px(8), rr.Y + Px(8), Px(22), Px(22)), c.Open, P.Sub);
                TxtL(g, c.Title, F, new Rectangle(rr.X + Px(32), rr.Y + Px(6), tr.X - cw - Px(20) - rr.X - Px(24), Px(26)), P.Fg);
                string d = c.Note.Length > 0 ? c.Note : (c.Caution.Length > 0 ? c.Caution : c.Desc);
                TxtL(g, d, FS, new Rectangle(rr.X + Px(32), rr.Y + Px(30), rr.Width - Px(44), Px(18)), c.Note.Length > 0 || c.Caution.Length > 0 ? P.Warn : P.Sub);
                RegCat cc = c;
                Tip(vis, RegClean.CatTip(c));
                if (!lock0)
                {
                    Rectangle sr = new Rectangle(tr.X - Px(8), rr.Y, rr.Right - tr.X + Px(8), rr.Height);
                    Hot(Rectangle.Intersect(sr, list), delegate { cc.On = !cc.On; Invalidate(); });
                    Rectangle hr = new Rectangle(rr.X, rr.Y, sr.X - rr.X, rr.Height);
                    if (n > 0) Hot(Rectangle.Intersect(hr, list), delegate { cc.Open = !cc.Open; Invalidate(); });
                }
            }
            else if (rw.Item != null)
            {
                RegItem it = rw.Item;
                if (Over(rr)) using (SolidBrush b = new SolidBrush(Util.Blend(P.Bg, P.Fg, 0.05f))) Util.FillRound(g, b, rr, Px(6));
                CheckBox(g, new Rectangle(rr.X + Px(22), rr.Y + (rr.Height - Px(16)) / 2, Px(16), Px(16)), !it.Skip && c.On);
                int tw = (rr.Width - Px(60)) * 45 / 100;
                TxtL(g, it.Title, FS, new Rectangle(rr.X + Px(46), rr.Y, tw, rr.Height), c.On ? P.Fg : P.Sub);
                TxtL(g, it.Detail, FS, new Rectangle(rr.X + Px(46) + tw + Px(8), rr.Y, rr.Width - Px(60) - tw - Px(8), rr.Height), P.Sub);
                RegItem ii = it;
                Tip(vis, RegClean.Describe(it));          // повний текст: що це й що саме буде видалено (рядок обрізається трьома крапками)
                if (!lock0) Hot(vis, delegate { ii.Skip = !ii.Skip; Invalidate(); });
            }
            else
            {
                TxtL(g, rw.Text, FS, new Rectangle(rr.X + Px(46), rr.Y, rr.Width - Px(60), rr.Height), P.Sub);
                Tip(vis, rw.Text);
            }
        }
        g.Restore(st);
        Wheel(list, delegate (int dd) { scrollR -= dd > 0 ? Px(58) : -Px(58); Invalidate(); });
        VBar(g, new Rectangle(list.Right - Px(6), list.Y + Px(2), Px(4), list.Height - Px(10)), total, list.Height, scrollR, delegate (int v) { scrollR = v; Invalidate(); });

        // низ: відновлення з копії, тека копій, пояснення
        string bk = RegClean.LastBackup();
        int fy = r.Bottom - footH + Px(6);
        Btn(g, new Rectangle(r.X, fy, Px(210), Px(32)), L.T("Відновити з копії"), RegRestore, 0, bk != null && !lock0);
        Btn(g, new Rectangle(r.X + Px(220), fy, Px(150), Px(32)), L.T("Тека копій"), delegate
        {
            try { Directory.CreateDirectory(RegClean.BackupDir); Process.Start("explorer.exe", "\"" + RegClean.BackupDir + "\""); } catch { }
        }, 0, true);
        string info = rmsg.Length > 0 ? rmsg : (bk != null ? L.T("Остання копія: ") + Path.GetFileName(bk) : L.T("Резервних копій ще немає."));
        TxtW(g, info, FS, new Rectangle(r.X, fy + Px(38), r.Width, Px(30)), rmsg.Length > 0 ? (rbad ? P.Bad : P.Good) : P.Sub);
        TxtW(g, L.T("Сканування лише читає реєстр. Запис вважається сміттям, лише коли всі його файли точно відсутні; перед змінами все зберігається в копію."),
            FS, new Rectangle(r.X, r.Bottom - Px(36), r.Width, Px(36)), P.Sub);
    }

    void RegFix()
    {
        List<RegItem> sel = RegClean.SelectedItems();
        if (sel.Count == 0 || RegClean.Fixing || RegClean.Busy) return;
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append(L.T("Виправити записів у реєстрі: ")).Append(sel.Count).Append("?\n");
        bool lm = false;
        foreach (RegCat c in RegClean.Cats)
        {
            int n = 0;
            if (c.On) foreach (RegItem it in c.Items) if (!it.Skip) { n++; if (it.Hive == 1) lm = true; }
            if (n > 0) sb.Append("\n· ").Append(c.Title).Append(": ").Append(n);
        }
        sb.Append(L.T("\n\nПеред змінами всі ці записи зберігаються в резервну копію - її можна відновити кнопкою «Відновити з копії»."));
        if (lm) sb.Append(L.T("\nДля записів, спільних для всіх користувачів, Windows попросить дозвіл адміністратора."));
        if (SbDialog.Show(Bar, this, L.T("Реєстр"), sb.ToString(), L.T("Виправити"), L.T("Скасувати")) != 0) return;
        RegClean.Fixing = true;
        RegMsg("", false);
        Thread th = new Thread(delegate ()
        {
            int removed = 0, skipped = 0;
            string file = "", err = "";
            try { err = RegClean.Fix(sel, out removed, out skipped, out file); }
            catch (Exception ex) { Util.ErrorLog(ex); err = ex.Message; }
            try
            {
                BeginInvoke(new Action(delegate
                {
                    RegClean.Fixing = false;
                    if (err.Length > 0 && removed == 0) RegMsg(err, true);
                    else RegMsg(L.T("Прибрано записів: ") + removed + (skipped > 0 ? L.T(", пропущено: ") + skipped + L.T(" (вже змінилися, зайняті або їх не вдалося безпечно зберегти)") : "") + L.T(". Копія: ") + Path.GetFileName(file) + (err.Length > 0 ? "\n" + err : ""), err.Length > 0);
                    RegClean.Scan();
                }));
            }
            catch { RegClean.Fixing = false; }
        });
        th.IsBackground = true;
        th.Start();
    }

    void RegRestore()
    {
        string bk = RegClean.LastBackup();
        if (bk == null || RegClean.Fixing || RegClean.Busy) return;
        int n = RegClean.BackupSize(bk);
        if (SbDialog.Show(Bar, this, L.T("Реєстр"), L.T("Повернути в реєстр записи з копії ") + Path.GetFileName(bk) + L.T(" (розділів: ") + n + L.T(")?\n\nПрава доступу до розділів (ACL) не відновлюються - вони й не змінювалися програмою."), L.T("Відновити"), L.T("Скасувати")) != 0) return;
        RegClean.Fixing = true;
        RegMsg("", false);
        Thread th = new Thread(delegate ()
        {
            int cnt = 0;
            string err = "";
            try { err = RegClean.Restore(bk, out cnt); }
            catch (Exception ex) { Util.ErrorLog(ex); err = ex.Message; }
            try
            {
                BeginInvoke(new Action(delegate
                {
                    RegClean.Fixing = false;
                    RegMsg(err.Length > 0 ? err : L.T("Відновлено розділів: ") + cnt + ".", err.Length > 0);
                    RegClean.Scan();
                }));
            }
            catch { RegClean.Fixing = false; }
        });
        th.IsBackground = true;
        th.Start();
    }
}
