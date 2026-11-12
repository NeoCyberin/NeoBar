using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// ======================================================================
//  Монітор системи → «Завдання»: підказки під полем пошуку. Після трьох літер - до восьми процесів
//  (за назвою, з кількістю копій) і служб (за назвою чи ім'ям служби, зі станом). Стрілки ↑ ↓ і Enter або клік -
//  перейти до процесу чи служби; Esc чи клік деінде ховає підказки.
// ======================================================================
sealed partial class PerfWindow
{
    sealed class Sugg
    {
        public bool Svc;
        public string Name = "", Sub = "", Key = "";
        public int Pid, Count;
        public Color SubColor;
    }

    const int SugMax = 8;
    List<Sugg> sug = new List<Sugg>();
    int sugSel = -1;
    bool sugOpen, sugMute;
    Rectangle sugAnchor, sugRect;

    void SearchChanged()
    {
        query = search.Text.Trim();
        procScroll = 0; svcScroll = 0;
        RebuildView();
        if (sugMute) sugOpen = false;
        else BuildSug(true);
        Invalidate();
    }

    // reopen: показати знову (після введення); false - лише оновити вже відкриті
    void BuildSug(bool reopen)
    {
        if (!reopen && !sugOpen) return;
        sug.Clear();
        int num;
        if (query.Length < 3 || int.TryParse(query, out num)) { sugOpen = false; sugSel = -1; return; }
        if (svcs.Count == 0 && !svcLoading) LoadSvcs();          // служби читаються у фоні; підказки оновляться, коли прочитаються

        // процеси - одна підказка на назву, з найбільшим за пам'яттю (зазвичай головний)
        Dictionary<string, Sugg> byName = new Dictionary<string, Sugg>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, long> best = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (ProcRow r in ProcMon.Rows)
        {
            if (r.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
            Sugg s;
            if (!byName.TryGetValue(r.Name, out s)) { s = new Sugg(); s.Name = r.Name; s.Key = r.Name; byName[r.Name] = s; best[r.Name] = -1; }
            s.Count++;
            if (r.Ws > best[r.Name]) { best[r.Name] = r.Ws; s.Pid = r.Pid; }
        }
        List<Sugg> procs = new List<Sugg>(byName.Values);
        foreach (Sugg s in procs) { s.Sub = s.Count > 1 ? L.F("процесів: {0}", s.Count) : "PID " + s.Pid; s.SubColor = P.Sub; }
        List<Sugg> svl = new List<Sugg>();
        foreach (SvcInfo v in svcs)
        {
            if (v.Display.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) < 0 && v.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
            Sugg s = new Sugg();
            s.Svc = true; s.Name = v.Display.Length > 0 ? v.Display : v.Name; s.Key = v.Name; s.Pid = v.Pid;
            s.Sub = v.Name + "  ·  " + StateText(v.State);
            s.SubColor = StateColor(v.State);
            svl.Add(s);
        }
        Comparison<Sugg> rank = delegate (Sugg a, Sugg b)
        {
            int c = Starts(b).CompareTo(Starts(a));             // ті, що починаються з введеного, - вище
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        };
        procs.Sort(rank);
        svl.Sort(rank);
        int np = Math.Min(procs.Count, SugMax - Math.Min(svl.Count, 3));
        for (int i = 0; i < np; i++) sug.Add(procs[i]);
        for (int i = 0; i < svl.Count && sug.Count < SugMax; i++) sug.Add(svl[i]);
        // єдина підказка, яка вже точно введена, нічого не додає
        if (sug.Count == 1 && (string.Equals(sug[0].Name, query, StringComparison.OrdinalIgnoreCase) || string.Equals(sug[0].Key, query, StringComparison.OrdinalIgnoreCase))) sug.Clear();
        sugOpen = sug.Count > 0 && search != null && search.Focused;
        if (sugSel >= sug.Count) sugSel = sug.Count - 1;
        if (reopen) sugSel = -1;
    }

    bool Starts(Sugg s)
    {
        return s.Name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) || s.Key.StartsWith(query, StringComparison.OrdinalIgnoreCase);
    }

    // клавіші в полі пошуку; true - оброблено
    bool SugKey(Keys k)
    {
        if (!sugOpen || sug.Count == 0) return false;
        if (k == Keys.Down) { sugSel = (sugSel + 1) % sug.Count; Invalidate(); return true; }
        if (k == Keys.Up) { sugSel = sugSel <= 0 ? sug.Count - 1 : sugSel - 1; Invalidate(); return true; }
        if (k == Keys.Enter) { PickSug(sug[Math.Max(0, sugSel)]); return true; }
        if (k == Keys.Escape) { sugOpen = false; Invalidate(); return true; }
        return false;
    }

    void PickSug(Sugg s)
    {
        sugOpen = false;
        sugMute = true;
        try { search.Text = s.Svc ? s.Key : s.Name; search.SelectionStart = search.TextLength; }
        finally { sugMute = false; }
        if (s.Svc)
        {
            if (pview != 1) { pview = 1; LoadSvcs(); }
            selSvc = s.Key;
            ensureSel = true;
            Invalidate();
        }
        else GoToProcess(s.Pid);
    }

    // клік поза підказками ховає їх (сам клік далі спрацьовує як звичайно)
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (sugOpen && !sugRect.Contains(e.Location) && !sugAnchor.Contains(e.Location)) { sugOpen = false; Invalidate(); }
        base.OnMouseDown(e);
    }

    void PaintSug(Graphics g)
    {
        sugRect = Rectangle.Empty;
        if (!sugOpen || sug.Count == 0 || search == null || !search.Visible) return;
        int rowH = Px(34), pad = Px(4);
        int w = Math.Max(sugAnchor.Width, Px(380));
        int x = Math.Min(sugAnchor.X, ClientSize.Width - Px(8) - w);
        sugRect = new Rectangle(x, sugAnchor.Bottom + Px(4), w, sug.Count * rowH + pad * 2);
        Card(g, sugRect, Util.Blend(P.Bg, P.Card, 0.6f));
        using (Pen ep = new Pen(Util.Blend(P.Card, P.Acc, 0.6f), Math.Max(1f, Px(1))))
        using (GraphicsPath gp = Util.Round(sugRect, P.Rad)) g.DrawPath(ep, gp);
        Hot(sugRect, delegate { });                         // під підказками нічого не натискається
        int y = sugRect.Y + pad;
        for (int i = 0; i < sug.Count; i++, y += rowH)
        {
            Sugg s = sug[i];
            Rectangle rr = new Rectangle(sugRect.X + pad, y, sugRect.Width - pad * 2, rowH);
            if (i == sugSel || Over(rr)) Card(g, rr, i == sugSel ? Util.Blend(P.Card, P.Acc, 0.3f) : P.Card);
            int kw = Px(70);
            TxtL(g, s.Name, FB, new Rectangle(rr.X + Px(10), rr.Y + Px(1), rr.Width - kw - Px(20), Px(18)), P.Fg);
            TxtL(g, s.Sub, FS, new Rectangle(rr.X + Px(10), rr.Y + Px(17), rr.Width - kw - Px(20), Px(15)), s.SubColor);
            TextRenderer.DrawText(g, s.Svc ? L.T("служба") : L.T("процес"), FS, new Rectangle(rr.Right - kw - Px(8), rr.Y, kw, rr.Height), s.Svc ? P.Acc : P.Sub,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            Sugg ss = s;
            Hot(rr, delegate { PickSug(ss); });
        }
    }
}
