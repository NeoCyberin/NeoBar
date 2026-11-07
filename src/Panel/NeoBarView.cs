using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

// ======================================================================
//  NeoBar - розкладка, іконки, малювання, миша
// ======================================================================
sealed partial class NeoBar
{
    static readonly TextFormatFlags TF = TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis |
        TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

    // ---------- розкладка ----------

    string HeadText(Entry e)
    {
        string arrow = e.Collapsed ? "▲ " : "▼ ";
        return arrow + e.Name;
    }

    bool IsFold(Entry head)
    {
        return head.GMode.Length > 0 ? head.GMode == "fold" : cfg.GroupMode == "fold";
    }

    void DoLayout()
    {
        H = cfg.Side == "top" || cfg.Side == "bottom";
        int fh = font.Height, hfh = headFont.Height;
        int px = Px(cfg.IconSize);
        slot = (int)Math.Round(px * T.IconScale);
        itemLen = Px(8) + slot + (cfg.Labels ? 1 + fh + Px(2) : 0) + Px(5);
        cell = Math.Max(Px(cfg.Width), slot + Px(10));
        if (H)
        {
            capH = hfh + Px(6);
            thick = Math.Max(itemLen + Px(8), Px(49));
            thick += capH;
            profS = Math.Max(Px(20), Math.Min(Px(34), thick - capH - Px(14)));
            // аватар, тематичний роздільник, пошук (роздільник - лише коли є пошук; інакше його роль виконує лінія після пошуку)
            profLen = Px(10) + profS + (cfg.ShowSearch ? Px(19) : Px(12));
            tbBlock = Px(6);
            searchLen = profLen + (cfg.ShowSearch ? Px(34) + Px(14) : Px(6));     // горизонтальна панель: пошук - лише значок (поле все одно відкриває вікно)
        }
        else
        {
            capH = 0;
            thick = cell;
            profS = Math.Max(Px(20), Math.Min(Px(34), cell - Px(14)));
            profLen = Px(10) + profS + (cfg.ShowSearch ? Px(19) : Px(12));
            tbBlock = Px(6);
            searchLen = profLen + (cfg.ShowSearch ? Px(28) + Px(14) : Px(6));
        }

        // Віджети (і автороздільник за ними) стоять у нерухомій зоні під пошуком і не прокручуються - як і сам пошук.
        // Прокручуються лише іконки.
        int nfix = 0;
        while (nfix < entries.Count && entries[nfix].Kind == Kind.Widget) nfix++;
        if (nfix > 0 && nfix < entries.Count && entries[nfix].Kind == Kind.Separator && entries[nfix].Auto) nfix++;
        int fpos = nfix > 0 ? Px(2) : 0;
        int pos = Px(2);
        spans.Clear();
        GroupSpan cur = null;
        Entry curHdr = null;
        int shownW = 0;                 // віджетів, видимих на панелі (усі сховані - автороздільник теж ховається)

        for (int ei = 0; ei < entries.Count; ei++)
        {
            Entry e = entries[ei];
            e.Head = null;
            e.Gs = null;
            e.Fold = false;
            e.Fix = ei < nfix;
            e.Kids.Clear();
            if (e.Kind == Kind.Header)
            {
                e.Col = e.GroupColor.IsEmpty ? T.Accent : e.GroupColor;
                e.Hidden = false;
                e.Pos = pos;
                curHdr = e;
                e.Fold = IsFold(e);
                if (e.Fold)
                {
                    cur = null;
                    e.Len = H ? cell : itemLen;
                    pos += e.Len;
                }
                else
                {
                    if (spans.Count > 0 && cur != null) pos += Px(4);
                    e.Pos = pos;
                    int tw = TextRenderer.MeasureText(HeadText(e), headFont).Width;
                    int len;
                    if (H)
                    {
                        // підпис живе в смузі під/над групою й місця вздовж осі не займає,
                        // крім порожньої або згорнутої групи (тоді потрібна мінімальна ширина)
                        int kids = 0;
                        for (int j = ei + 1; j < entries.Count && entries[j].Kind == Kind.Item; j++) kids++;
                        len = (e.Collapsed || kids == 0) ? Math.Max(Px(34), Math.Min(Px(150), tw + Px(18))) : 0;
                    }
                    else len = hfh + Px(12);
                    e.Len = len;
                    pos += len;
                    cur = new GroupSpan();
                    cur.Start = e.Pos;
                    cur.End = pos;
                    cur.Color = e.Col;
                    cur.Pill = H && e.Len > 0;
                    e.Gs = cur;
                    spans.Add(cur);
                }
            }
            else if ((e.Kind == Kind.Widget && (e.Off || e.Target == "battery" && !Battery.Present())) ||
                     (e.Kind == Kind.Separator && e.Auto && shownW == 0))
            {
                // схований з панелі віджет (лишився в списку), віджет батареї без батареї, автороздільник без видимих віджетів
                e.Hidden = true;
                e.Pos = e.Fix ? fpos : pos;
                e.Len = 0;
            }
            else if (e.Kind == Kind.Separator || e.Kind == Kind.Widget)
            {
                cur = null;
                curHdr = null;
                e.Hidden = false;
                e.Len = e.Kind == Kind.Separator ? (e.Auto ? Px(24) : Px(14)) : Widgets.Len(e, H, cell, itemLen, scale);
                if (e.Kind == Kind.Widget) shownW++;
                if (e.Fix) { e.Pos = fpos; fpos += e.Len; }
                else { e.Pos = pos; pos += e.Len; }
            }
            else
            {
                if (curHdr != null)
                {
                    e.Head = curHdr;
                    curHdr.Kids.Add(e);
                }
                float open = curHdr == null ? 1f : (curHdr.Fold ? 0f : GroupOpen(curHdr));
                if (open <= 0.001f)
                {
                    e.Hidden = true;
                    e.Pos = pos;
                    e.Len = 0;
                }
                else
                {
                    e.Hidden = false;
                    e.Pos = pos;
                    int full = H ? cell : itemLen;
                    // під час анімації групи елементи плавно «виїжджають» / ховаються
                    e.Len = open >= 0.999f ? full : Math.Max(1, (int)Math.Round(full * (1 - (1 - open) * (1 - open))));
                    pos += e.Len;
                    if (cur != null) cur.End = pos;
                }
            }
        }
        fixLen = fpos;
        topLen = searchLen + fixLen;
        contentLen = pos + Px(12);
        desiredLen = topLen + contentLen + tbBlock;
        Recenter();
    }

    int TotalLen { get { return H ? ClientSize.Width : ClientSize.Height; } }

    int ViewLen { get { return Math.Max(1, TotalLen - topLen - tbBlock); } }

    // угорі/внизу вміст завжди по центру, якщо він вміщується
    void Recenter()
    {
        centerOff = (H && contentLen < ViewLen) ? (ViewLen - contentLen) / 2 : 0;
    }

    void ClampScroll()
    {
        int max = Math.Max(0, contentLen - ViewLen);
        scroll = Math.Max(0, Math.Min(max, scroll));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (T == null) return;
        UpdateShape();
        Recenter();
        ClampScroll();
        Invalidate();
    }

    void BuildFolderIcons()
    {
        foreach (Entry e in entries)
        {
            if (e.Kind != Kind.Header || !e.Fold) continue;
            if (e.Icon != null) e.Icon.Dispose();
            List<Bitmap> kids = new List<Bitmap>();
            foreach (Entry k in e.Kids) if (k.Icon != null && kids.Count < 4) kids.Add(k.Icon);
            e.Icon = IconFactory.FolderTile(T, e.Col, kids, slot);
        }
    }

    void MakeToolbarIcons()
    {
        foreach (Bitmap b in tbIcons.Values) b.Dispose();
        tbIcons.Clear();
    }

    // ---------- іконки ----------

    static string UrlKey(string t)
    {
        if (t.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)) return "game";
        if (t.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return "mail";
        return "web";
    }

    static Bitmap LoadFileIcon(string path, int px)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".ico")
        {
            using (Icon ic = new Icon(path, new Size(px, px))) return ic.ToBitmap();
        }
        if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif")
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (Image im = Image.FromStream(fs)) return new Bitmap(im);
        }
        using (Icon ic2 = Icon.ExtractAssociatedIcon(path))
        {
            return ic2 != null ? ic2.ToBitmap() : null;
        }
    }

    // px - розмір символу; готова іконка має розмір px * IconScale теми
    Bitmap LoadIcon(Entry e, int px)
    {
        int sl = (int)Math.Round(px * T.IconScale);
        Bitmap raw = null;
        bool themed = false;
        try
        {
            string spec = (e.IconSpec ?? "").Trim();
            string tgt = e.Full;
            if (spec.StartsWith("theme:", StringComparison.OrdinalIgnoreCase))
            {
                raw = IconFactory.Make(T, spec.Substring(6), e.Name, sl);
                themed = true;
            }
            else if (spec.Length > 0)
            {
                string ip = Util.ResolvePath(spec);
                if (File.Exists(ip)) raw = LoadFileIcon(ip, px);
            }

            if (raw == null)
            {
                int cs = SysCatalog.Csidl(e.Target);
                if (Util.IsUrl(tgt))
                {
                    // справжня іконка сайту з інтернету (кеш); поки вона вантажиться - тематична
                    string fav = Favicons.Get(tgt, FaviconReady);
                    if (fav != null) raw = LoadFileIcon(fav, px);
                    if (raw == null)
                    {
                        raw = IconFactory.Make(T, UrlKey(tgt), e.Name, sl);
                        themed = true;
                    }
                }
                else if (cs >= 0) raw = Util.ShellIconPidl(cs);
                else if (Util.SlowPath(tgt)) { }      // мережевий шлях: перевірка недоступного сервера тримала б панель до півхвилини - тематична іконка
                else if (Directory.Exists(tgt)) raw = Util.ShellIcon(tgt, true);
                else if (File.Exists(tgt)) raw = LoadFileIcon(tgt, px);
            }
        }
        catch { }
        if (raw == null)
        {
            raw = IconFactory.Make(T, null, e.Name, sl);
            themed = true;
        }
        if (themed) return raw;
        return CenterIn(Fit(raw, px), sl);
    }

    static Bitmap Fit(Bitmap src, int px)
    {
        if (src.Width == px && src.Height == px) return src;
        Bitmap b = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, new Rectangle(0, 0, px, px));
        }
        src.Dispose();
        return b;
    }

    static Bitmap CenterIn(Bitmap src, int size)
    {
        if (src.Width >= size) return src;
        Bitmap b = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
            g.DrawImage(src, (size - src.Width) / 2, (size - src.Height) / 2, src.Width, src.Height);
        src.Dispose();
        return b;
    }

    // ---------- геометрія ----------

    // висота смуги підписів: якщо підписів немає, вміст центрується по всій висоті панелі
    int CapNow { get { return capH; } }

    int Origin { get { return topLen + centerOff - scroll; } }

    // нерухома зона віджетів: від кінця пошуку до початку області прокрутки
    Rectangle FixRect()
    {
        if (H) return new Rectangle(searchLen, 0, fixLen, ClientSize.Height);
        return new Rectangle(0, searchLen, ClientSize.Width, fixLen);
    }

    Rectangle RectOf(Entry e)
    {
        int a = (e.Fix ? searchLen : Origin) + e.Pos;
        if (H)
        {
            if (e.Kind == Kind.Header && !e.Fold && e.Gs != null) return HeadRect(e);
            int hh = ClientSize.Height - Px(8) - capH;
            if (e.Kind == Kind.Widget || e.Kind == Kind.Separator)
                return new Rectangle(a, (ClientSize.Height - hh) / 2, e.Len, hh);   // віджети й роздільники - завжди по центру панелі
            // плитки згорнутих груп і елементи поза розгорнутою групою (без смуги підпису) - по центру панелі
            bool banded = e.Kind == Kind.Item && e.Head != null && !e.Head.Fold;
            if (!banded) return new Rectangle(a, (ClientSize.Height - itemLen) / 2, e.Len, itemLen);
            return new Rectangle(a, Px(4) + ItemTop, e.Len, hh);
        }
        return new Rectangle(0, a, ClientSize.Width, e.Len);
    }

    // верхня панель: підпис під групою; нижня: над групою
    int ItemTop { get { return cfg.Side == "bottom" ? CapNow : 0; } }

    Rectangle HeadRect(Entry e)
    {
        if (e.Gs.Pill) return SpanRect(e.Gs);
        int a = Origin + e.Gs.Start, l = e.Gs.End - e.Gs.Start;
        int y = cfg.Side == "bottom" ? Px(3) : ClientSize.Height - capH - Px(3);
        return new Rectangle(a, y, l, capH);
    }

    bool InHeadBand(int y)
    {
        if (!H || capH <= 0) return false;
        return cfg.Side == "bottom" ? y < capH + Px(3) : y >= ClientSize.Height - capH - Px(3);
    }

    Rectangle SpanRect(GroupSpan s)
    {
        int a = Origin + s.Start, l = s.End - s.Start;
        if (H && s.Pill)
        {
            int ph = capH + Px(10);
            return new Rectangle(a, (ClientSize.Height - ph) / 2, l, ph);
        }
        if (H) return new Rectangle(a, Px(2), l, ClientSize.Height - Px(4));
        return new Rectangle(Px(2), a, ClientSize.Width - Px(4), l);
    }

    Rectangle ViewRect()
    {
        if (H) return new Rectangle(topLen, 0, ViewLen, ClientSize.Height);
        return new Rectangle(0, topLen, ClientSize.Width, ViewLen);
    }

    // кнопка профілю (аватар) - перед пошуком; налаштування, перезавантаження, закріплення й вихід - у вікні профілю
    Rectangle TbRect(int i)
    {
        int w = ClientSize.Width, h = ClientSize.Height;
        if (!H) return new Rectangle((w - profS) / 2, Px(10), profS, profS);
        return new Rectangle(Px(10), (h - profS) / 2, profS, profS);
    }

    Rectangle TbHot(int i)
    {
        // з запасом під кільце наведення: інакше перемальовується лише середина і кільце виглядає «обкусаним»
        return Rectangle.Inflate(TbRect(i), Px(7), Px(7));
    }

    Rectangle SearchRect()
    {
        if (!H) return new Rectangle(Px(6), profLen, ClientSize.Width - Px(12), Px(28));
        return new Rectangle(profLen, (ClientSize.Height - Px(34)) / 2, Px(34), Px(34));
    }

    string TbName(int i)
    {
        return L.T("Профіль «") + (Prof.Cur != null ? Prof.Cur.Name : "—") + L.T("»: налаштування, закріплення, вихід");
    }

    // ---------- малювання ----------

    void DropBackdrop()
    {
        if (backdrop != null) { backdrop.Dispose(); backdrop = null; }
        Invalidate();
    }

    void PaintBackdrop(Graphics g, Rectangle clip, int w, int h)
    {
        if (T.Sketchy)
        {
            using (TextureBrush tb = new TextureBrush(Sk.Paper(T.Bg)))
                g.FillRectangle(tb, clip);
            return;
        }
        if (Festive.Has(T))
        {
            // багатоколірний градієнт уздовж панелі (тема «День народження»)
            Festive.Fill(g, T, new RectangleF(0, 0, w, h), clip, h >= w ? 90f : 0f, 0);
            return;
        }
        if (!useWall)
        {
            g.Clear(T.Bg);
            return;
        }
        // розмиті шпалери беремо для повністю висунутого положення панелі
        Rectangle bar = autohide ? PanelRect(1f) : RectangleToScreen(new Rectangle(0, 0, w, h));
        if (backdrop == null || backdrop.Width != w || backdrop.Height != h || backdropAt != bar)
        {
            if (backdrop != null) backdrop.Dispose();
            Rectangle mon = Screen.FromRectangle(bar).Bounds;
            int k = wantBlur ? Math.Max(4, Px(10)) : 1;
            backdrop = Backdrop.Build(bar, mon, w, h, k, wantBlur);
            backdropAt = bar;
        }
        g.DrawImage(backdrop, clip, clip, GraphicsUnit.Pixel);
        int a = Math.Max(0, Math.Min(255, (int)Math.Round(opPct * 2.55)));
        if (a > 0)
        {
            using (SolidBrush tb = new SolidBrush(Color.FromArgb(a, Util.Opaque(T.Bg))))
                g.FillRectangle(tb, clip);
        }
    }

    void DrawIcon(Graphics g, Bitmap bmp, Rectangle ir, float hv)
    {
        int fx = cfg.HoverFx;
        float zoom = (fx == 1 || fx == 4) ? 0.18f * hv : 0f;
        float lift = fx == 2 ? -Px(3) * hv : 0f;
        if (hv <= 0.001f || (zoom == 0f && lift == 0f))
        {
            g.DrawImage(bmp, ir);
            return;
        }
        float dw = ir.Width * zoom, dh = ir.Height * zoom;
        RectangleF dst = new RectangleF(ir.X - dw / 2f, ir.Y - dh / 2f + lift, ir.Width + dw, ir.Height + dh);
        InterpolationMode old = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(bmp, dst);
        g.InterpolationMode = old;
    }

    void Line(Graphics g, float x1, float y1, float x2, float y2)
    {
        if (T.Sketchy) Sk.Line(g, sepPen, x1, y1, x2, y2, new Random((int)(x1 * 7 + y1 * 13) & 0xffff), 1.3f);
        else g.DrawLine(sepPen, x1, y1, x2, y2);
    }

    void DrawSearch(Graphics g)
    {
        Rectangle r = SearchRect();
        Color bg = Util.Opaque(T.Glass ? Color.FromArgb(30, 34, 46) : T.Bg);
        Color fg = Util.Opaque(textCol);
        Color plate = Util.Blend(bg, fg, srHover ? 0.2f : 0.1f);
        using (SolidBrush sb = new SolidBrush(Color.FromArgb(T.Glass ? 110 : 255, plate)))
            Util.FillRound(g, sb, r, Math.Max(3, T.Radius));
        using (GraphicsPath gp = Util.Round(r, Math.Max(3, T.Radius)))
        using (Pen pn = new Pen(Color.FromArgb(140, Util.Blend(bg, fg, 0.4f)), 1f))
            g.DrawPath(pn, gp);
        int isz = r.Height - Px(10);
        int tw = TextRenderer.MeasureText(L.T("Пошук"), font, new Size(1000, 100), TextFormatFlags.NoPadding).Width;
        bool withText = r.Width >= isz + tw + Px(18);
        float ix = withText ? r.X + Px(8) : r.X + (r.Width - isz) / 2f;
        IconFactory.DrawTool(g, "search", new RectangleF(ix, r.Y + (r.Height - isz) / 2f, isz, isz), Util.Blend(bg, fg, 0.78f), Math.Max(1.5f, isz * 0.12f), false);
        if (withText)
        {
            Rectangle tr = new Rectangle(r.X + Px(8) + isz + Px(4), r.Y, r.Width - isz - Px(16), r.Height);
            TextRenderer.DrawText(g, L.T("Пошук"), font, tr, Util.Blend(bg, fg, 0.62f),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }

    protected override void OnPaint(PaintEventArgs pe)
    {
        if (T == null) return;
        Graphics g = pe.Graphics;
        int w = ClientSize.Width, h = ClientSize.Height;
        Rectangle clip = pe.ClipRectangle;
        PaintBackdrop(g, clip, w, h);

        // автосховок: поки панель сховано, видно лише кольорову смужку-«ручку»
        if (autohide && slideT < 0.04f)
        {
            using (SolidBrush hb = new SolidBrush(Color.FromArgb(200, Util.Opaque(T.Accent))))
                g.FillRectangle(hb, 0, 0, w, h);
            return;
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        int px = slot;

        Rectangle view = ViewRect();
        g.SetClip(view, CombineMode.Intersect);
        Rectangle fixRect = FixRect();
        Rectangle vclip = view;
        bool fixPass = false;

        foreach (GroupSpan s in spans)
        {
            Rectangle sr = SpanRect(s);
            if (!sr.IntersectsWith(clip)) continue;
            using (SolidBrush tb = new SolidBrush(Color.FromArgb(useWall ? 56 : 42, Util.Opaque(s.Color))))
                Util.FillRound(g, tb, sr, T.Radius + 2);
        }

        // два проходи: спершу прокручувана область, потім (після тла під пошуком) нерухомі віджети
        Action paintEntries = delegate ()
        {
        for (int i = 0; i < entries.Count; i++)
        {
            Entry e = entries[i];
            if (e.Hidden || e.Fix != fixPass) continue;
            Rectangle r = RectOf(e);
            if (!r.IntersectsWith(Rectangle.Inflate(clip, Px(10), Px(10)))) continue;

            bool clipAnim = e.Kind == Kind.Item && GroupAnimating(e.Head);
            if (clipAnim) g.SetClip(Rectangle.Intersect(vclip, r));
            if (e.Kind == Kind.Item || (e.Kind == Kind.Header && e.Fold))
            {
                Rectangle cr = H ? Rectangle.Inflate(r, -Px(1), -Px(1)) : Rectangle.Inflate(r, -Px(3), -Px(1));
                if (e.Hv > 0.001f || i == hover)
                {
                    float hv = Math.Max(e.Hv, i == hover ? 0.001f : 0f);
                    int fx = cfg.HoverFx;
                    using (SolidBrush hb = new SolidBrush(Color.FromArgb((int)(T.Hover.A * Math.Min(1f, hv * 1.5f)), T.Hover)))
                        Util.FillRound(g, hb, cr, T.Radius);
                    if (fx == 3 || fx == 4)
                    {
                        using (GraphicsPath gp = Util.Round(cr, T.Radius))
                        using (SolidBrush gl = new SolidBrush(Color.FromArgb((int)(40 * hv), Util.Opaque(T.Accent))))
                        using (Pen gpn = new Pen(Color.FromArgb((int)(170 * hv), Util.Opaque(T.Accent)), Math.Max(1.5f, Px(2))))
                        {
                            g.FillPath(gl, gp);
                            g.DrawPath(gpn, gp);
                        }
                    }
                }
                bool above = cfg.Labels && cfg.LabelPos == "above";
                int labH = cfg.Labels ? font.Height + Px(2) : 0;
                int iconY = r.Y + Px(8) + (above ? labH : 0);
                if (e.Icon != null)
                {
                    Rectangle ir = new Rectangle(r.X + (r.Width - px) / 2, iconY, px, px);
                    DrawIcon(g, e.Icon, ir, e.Hv);
                }
                if (cfg.Labels)
                {
                    int labelY = above ? r.Y + Px(4) : iconY + px + 1;
                    TextRenderer.DrawText(g, e.Name, font,
                        new Rectangle(cr.X + Px(3), labelY, cr.Width - Px(6), font.Height + 2), textCol, TF);   // підпис не виходить за плитку
                }
            }
            else if (e.Kind == Kind.Widget)
            {
                Widgets.Draw(g, r, e, T, cfg, font, textCol, i == hover, scale);
            }
            else if (e.Kind == Kind.Separator)
            {
                if (H)
                {
                    int cx = r.X + r.Width / 2;
                    Line(g, cx, Px(14), cx, h - Px(14));
                }
                else
                {
                    int cy = r.Y + r.Height / 2;
                    Line(g, Px(12), cy, w - Px(12), cy);
                }
            }
            else // заголовок розгорнутої групи
            {
                if (i == hover)
                    Util.FillRound(g, hoverBrush, Rectangle.Inflate(r, -Px(2), -Px(1)), T.Radius);
                Color hc = Util.Readable(Util.Blend(e.Col, T.Text, 0.35f), Util.Opaque(T.Bg), 55);
                if (H)
                {
                    TextRenderer.DrawText(g, HeadText(e), headFont, new Rectangle(r.X + 2, r.Y, r.Width - 4, r.Height),
                        hc, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                    int ly = cfg.Side == "bottom" ? r.Bottom : r.Y;
                    if (!e.Gs.Pill)
                    using (Pen up = new Pen(e.Col, Math.Max(1, Px(2))))
                    {
                        if (T.Sketchy) Sk.Line(g, up, r.X + Px(8), ly, r.Right - Px(8), ly, new Random(e.Pos & 0xffff), 1.4f);
                        else g.DrawLine(up, r.X + Px(8), ly, r.Right - Px(8), ly);
                    }
                }
                else
                {
                    TextRenderer.DrawText(g, HeadText(e), headFont, new Rectangle(r.X + 2, r.Y, r.Width - 4, r.Height),
                        hc, TF | TextFormatFlags.VerticalCenter);
                    int uy = r.Y + (r.Height + headFont.Height) / 2 + 1;
                    using (Pen up = new Pen(e.Col, Math.Max(1, Px(2))))
                    {
                        if (T.Sketchy) Sk.Line(g, up, r.X + Px(8), uy, r.Right - Px(8), uy, new Random(e.Pos & 0xffff), 1.4f);
                        else g.DrawLine(up, r.X + Px(8), uy, r.Right - Px(8), uy);
                    }
                }
            }
            if (clipAnim) g.SetClip(vclip);
        }
        };
        paintEntries();
        DrawDropMark(g, false);
        g.ResetClip();

        // Пошук, роздільники й кнопки малюються поверх вмісту на чистому тлі: прокручені або заширокі
        // елементи (напр. останній сайт) ніколи не налазять на кнопки
        Rectangle topArea = H ? new Rectangle(0, 0, topLen, h) : new Rectangle(0, 0, w, topLen);
        Rectangle tbArea = H ? new Rectangle(w - tbBlock, 0, tbBlock, h) : new Rectangle(0, h - tbBlock, w, tbBlock);
        foreach (Rectangle ar in new Rectangle[] { topArea, tbArea })
        {
            g.SetClip(ar);
            PaintBackdrop(g, ar, w, h);
        }
        g.ResetClip();
        ProfileArt.Avatar(g, TbRect(0), Prof.Cur, tbHover == 0, Util.Opaque(T.Accent));
        if (cfg.ShowSearch)
        {
            // тематичний роздільник між профілем і пошуком (посередині проміжку)
            Rectangle av = TbRect(0);
            int mid = (H ? av.Right : av.Bottom) + (profLen - (H ? av.Right : av.Bottom)) / 2;
            if (H) Line(g, mid, Px(14), mid, h - Px(14));
            else Line(g, Px(12), mid, w - Px(12), mid);
            DrawSearch(g);
        }
        if (H) Line(g, searchLen - Px(5), Px(14), searchLen - Px(5), h - Px(14));
        else Line(g, Px(12), searchLen - Px(5), w - Px(12), searchLen - Px(5));
        // нерухомі віджети: поверх чистого тла, не прокручуються
        if (fixLen > 0)
        {
            fixPass = true;
            vclip = fixRect;
            g.SetClip(fixRect);
            paintEntries();
            DrawDropMark(g, true);
            g.ResetClip();
        }


        g.ResetClip();

        if (Rounded)
        {
            SmoothingMode osm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool tp = cfg.Side == "top";
            int cr = CornerRadius, ow = Math.Max(1, Px(T.EdgeW));
            Rectangle rr = new Rectangle(0, 0, w - 1, h - 1);
            using (GraphicsPath gp = Util.Round(rr, tp ? 0 : cr, tp ? 0 : cr, tp ? cr : 0, tp ? cr : 0))
            using (Pen op = new Pen(T.Edge, ow))
                g.DrawPath(op, gp);
            g.SmoothingMode = osm;
        }

        // край панелі
        int ew = Math.Max(1, Px(T.EdgeW));
        if (T.Sketchy)
        {
            using (Pen ep = new Pen(T.Edge, ew))
            {
                Random rnd = new Random(5);
                switch (cfg.Side)
                {
                    case "left": Sk.Line(g, ep, w - ew, 0, w - ew, h, rnd, 1.5f); break;
                    case "top": Sk.Line(g, ep, 0, h - ew, w, h - ew, rnd, 1.5f); break;
                    case "bottom": Sk.Line(g, ep, 0, ew, w, ew, rnd, 1.5f); break;
                    default: Sk.Line(g, ep, ew, 0, ew, h, rnd, 1.5f); break;
                }
            }
        }
        else
        {
            switch (cfg.Side)
            {
                case "left": g.FillRectangle(edgeBrush, w - ew, 0, ew, h); break;
                case "top": g.FillRectangle(edgeBrush, 0, h - ew, w, ew); break;
                case "bottom": g.FillRectangle(edgeBrush, 0, 0, w, ew); break;
                default: g.FillRectangle(edgeBrush, 0, 0, ew, h); break;
            }
        }
        // в автосховку праворуч/ліворуч/угорі/внизу додаємо тонку «ручку»
    }

    // ---------- миша ----------

    int HitTb(Point p)
    {
        return TbHot(0).Contains(p) ? 0 : -1;
    }

    int Hit(Point p)
    {
        int c0 = H ? p.X : p.Y;
        if (c0 < searchLen || c0 >= TotalLen - tbBlock) return -1;
        if (c0 < topLen)
        {
            // нерухома зона віджетів
            int cf = c0 - searchLen;
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                if (!e.Fix || e.Hidden) continue;
                if (cf >= e.Pos && cf < e.Pos + e.Len) return e.Kind == Kind.Separator ? -1 : i;
            }
            return -1;
        }
        int c = c0 - Origin;
        bool band = InHeadBand(p.Y);
        if (H)
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                if (e.Kind == Kind.Header && !e.Fold && e.Gs != null && e.Gs.Pill && HeadRect(e).Contains(p)) return i;
            }
        if (band)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                if (e.Kind == Kind.Header && !e.Fold && e.Gs != null && HeadRect(e).Contains(p)) return i;
            }
        }
        for (int i = 0; i < entries.Count; i++)
        {
            Entry e = entries[i];
            if (e.Hidden || e.Fix) continue;
            if (band && e.Kind == Kind.Item && e.Head != null && !e.Head.Fold) continue;
            if (c >= e.Pos && c < e.Pos + e.Len) return e.Kind == Kind.Separator ? -1 : i;
        }
        return -1;
    }

    void InvalidateEntry(int i)
    {
        Invalidate(Rectangle.Inflate(RectOf(entries[i]), Px(8), Px(8)));
    }

    void UpdateHover(Point p)
    {
        int tb = HitTb(p);
        bool sr = cfg.ShowSearch && tb < 0 && SearchRect().Contains(p);
        int i = (tb >= 0 || sr) ? -1 : Hit(p);
        if (tb == tbHover && i == hover && sr == srHover) return;

        if (tbHover >= 0 && tbHover != tb) Invalidate(TbHot(tbHover));
        if (srHover != sr) Invalidate(SearchRect());
        int old = hover;
        tbHover = tb;
        hover = i;
        srHover = sr;
        if (old >= 0 && old < entries.Count && old != i) InvalidateEntry(old);

        if (tb >= 0)
        {
            Invalidate(TbHot(tb));
            ShowTipText(TbName(tb), TbRect(tb));
            Cursor = Cursors.Hand;
        }
        else if (sr)
        {
            Invalidate(SearchRect());
            ShowTipText(cfg.Hotkeys ? L.T("Пошук (") + OverlayCfg.KeyText((Keys)cfg.SearchKey) + ")" : L.T("Пошук"), SearchRect());
            Cursor = Cursors.Hand;
        }
        else if (i >= 0)
        {
            InvalidateEntry(i);
            if (entries[i].Kind != Kind.Header || entries[i].Fold) ShowTip(i); else tip.Hide(this);
            Cursor = Cursors.Hand;
        }
        else
        {
            tip.Hide(this);
            Cursor = Cursors.Default;
        }
        anim.Start();
    }

    // підказка з внутрішніми відступами (малюється вручну)
    bool tipInit;
    string tipText = "";

    Size TipTextSize(string text)
    {
        return TextRenderer.MeasureText(text, SystemFonts.MessageBoxFont, new Size(Px(380), 0),
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
    }

    Size TipSize(string text)
    {
        Size m = TipTextSize(text);
        return new Size(m.Width + Px(24), m.Height + Px(16));
    }

    void InitTip()
    {
        if (tipInit) return;
        tipInit = true;
        tip.OwnerDraw = true;
        tip.Popup += delegate (object s, PopupEventArgs e) { e.ToolTipSize = TipSize(tipText); };
        tip.Draw += delegate (object s, DrawToolTipEventArgs e)
        {
            Graphics g = e.Graphics;
            Color bg = PopStyle.Bg(T), tx = PopStyle.Text(T);
            Color fill = Util.Blend(bg, tx, 0.10f);
            using (SolidBrush b = new SolidBrush(fill)) g.FillRectangle(b, e.Bounds);
            using (Pen p = new Pen(Util.Blend(bg, tx, 0.30f)))
                g.DrawRectangle(p, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
            TextRenderer.DrawText(g, tipText, SystemFonts.MessageBoxFont,
                new Rectangle(Px(12), Px(8), e.Bounds.Width - Px(24), e.Bounds.Height - Px(16)), tx,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
        };
    }

    void ShowTipText(string text, Rectangle r)
    {
        InitTip();
        tipText = text;
        Size sz = TipSize(text);
        int x, y;
        switch (cfg.Side)
        {
            case "left":
                x = ClientSize.Width + Px(10); y = r.Y + (r.Height - sz.Height) / 2; break;
            case "top":
                x = r.X + (r.Width - sz.Width) / 2; y = ClientSize.Height + Px(10); break;
            case "bottom":
                x = r.X + (r.Width - sz.Width) / 2; y = -(sz.Height + Px(10)); break;
            default:
                x = -(sz.Width + Px(10)); y = r.Y + (r.Height - sz.Height) / 2; break;
        }
        try
        {
            Rectangle sb = Screen.FromControl(this).Bounds;
            Point sp = PointToScreen(new Point(x, y));
            if (sp.X < sb.Left + 4) x += sb.Left + 4 - sp.X;
            else if (sp.X + sz.Width > sb.Right - 4) x -= sp.X + sz.Width - (sb.Right - 4);
            if (sp.Y < sb.Top + 4) y += sb.Top + 4 - sp.Y;
            else if (sp.Y + sz.Height > sb.Bottom - 4) y -= sp.Y + sz.Height - (sb.Bottom - 4);
        }
        catch { }
        tip.Show(text, this, x, y, 5000);
    }

    void ShowTip(int i)
    {
        Entry e = entries[i];
        if (e.Kind == Kind.Widget)
        {
            ShowTipText(Widgets.Tip(e, cfg), RectOf(e));
            return;
        }
        if (e.Kind == Kind.Header)
        {
            string names = "";
            int n = 0;
            foreach (Entry k in e.Kids)
            {
                if (n++ >= 8) { names += "\n…"; break; }
                names += "\n• " + k.Name;
            }
            ShowTipText(e.Name + " (" + e.Kids.Count + ")" + names, RectOf(e));
            return;
        }
        string t = e.Name + "\n" + e.Full;
        if (e.TagsText().Length > 0) t += "\n" + e.TagsText();
        ShowTipText(t, RectOf(e));
    }

    // ---------- перетягування елементів по панелі (ярлики, папки, віджети; роздільники й групи - ні) ----------
    int dragIdx = -1, dropSlot = -1;
    bool dragging;
    Point dragAt;
    Entry dropGroup;                 // група, на яку кинуть елемент (підсвічується рамкою)

    // Елемент витягли з вікна групи (flyout): далі перетягування веде сама панель
    public void BeginDragFromFlyout(Entry k)
    {
        int idx = entries.IndexOf(k);
        if (idx < 0 || !CanDrag(k)) return;
        dragIdx = idx;
        dragging = true;
        dropSlot = -1;
        dropGroup = null;
        Capture = true;
        Cursor = Cursors.SizeAll;
        tip.Hide(this);
        Invalidate();
    }

    // група під курсором (заголовок або плитка згорнутої групи) і слот у ній: до кінця групи або одразу за заголовком
    int GroupSlotAt(Point p, out Entry grp)
    {
        grp = null;
        if (dragIdx < 0 || dragIdx >= entries.Count || entries[dragIdx].Kind != Kind.Item) return -1;
        int hi = Hit(p);
        if (hi < 0 || entries[hi].Kind != Kind.Header) return -1;
        grp = entries[hi];
        int s = hi + 1;
        if (grp.Fold || grp.Collapsed)
            while (s < entries.Count && entries[s].Kind == Kind.Item) s++;     // згорнута - у кінець групи
        return s;
    }

    bool CanDrag(Entry e) { return e.Kind == Kind.Item || e.Kind == Kind.Widget; }

    // слот вставки: індекс елемента, перед яким стане перетягуваний (entries.Count - у кінець)
    int SlotAt(Point p)
    {
        // віджети міняються місцями в нерухомій зоні, решта - в області прокрутки
        bool wdrag = dragIdx >= 0 && dragIdx < entries.Count && entries[dragIdx].Kind == Kind.Widget;
        int c = (H ? p.X : p.Y) - (wdrag ? searchLen : Origin);
        int last = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            Entry e = entries[i];
            if (e.Fix != wdrag) { if (!wdrag && e.Fix) last = i; continue; }
            if (wdrag && e.Kind != Kind.Widget) continue;
            if (e.Hidden || e.Len <= 0) { if (wdrag) last = i; continue; }
            if (c < e.Pos + e.Len / 2) return i;
            last = i;
        }
        return last + 1;
    }

    int SlotPos(int slot)
    {
        if (slot < entries.Count && slot >= 0) return entries[slot].Pos;
        for (int i = entries.Count - 1; i >= 0; i--) if (!entries[i].Hidden && entries[i].Len > 0) return entries[i].Pos + entries[i].Len;
        return 0;
    }

    void DrawDropMark(Graphics g, bool fixedZone)
    {
        if (!dragging || dropSlot < 0 || dragIdx < 0 || dragIdx >= entries.Count) return;
        bool wdrag = entries[dragIdx].Kind == Kind.Widget;
        if (wdrag != fixedZone) return;
        if (dropGroup != null)
        {
            // кидають на групу - підсвічуємо її рамкою
            using (Pen gp = new Pen(Util.Opaque(T.Accent), Math.Max(2, Px(3))))
                g.DrawRectangle(gp, Rectangle.Inflate(RectOf(dropGroup), -1, -1));
            return;
        }
        int a = (wdrag ? searchLen : Origin) + SlotPos(dropSlot);
        Color c = Util.Opaque(T.Accent);
        int th = Math.Max(2, Px(3));
        using (SolidBrush b = new SolidBrush(c))
        {
            if (H) { g.FillRectangle(b, a - th / 2, Px(4), th, ClientSize.Height - Px(8)); g.FillEllipse(b, a - th * 1.5f, Px(2), th * 3, th * 3); }
            else { g.FillRectangle(b, Px(4), a - th / 2, ClientSize.Width - Px(8), th); g.FillEllipse(b, Px(2), a - th * 1.5f, th * 3, th * 3); }
        }
    }

    void FinishDrag()
    {
        int src = dragIdx, slot = dropSlot;
        dragging = false; dragIdx = -1; dropSlot = -1; dropGroup = null;
        Capture = false;
        Cursor = Cursors.Default;
        if (src < 0 || src >= entries.Count || slot < 0) { Invalidate(); return; }
        if (slot == src || slot == src + 1) { Invalidate(); return; }     // на те саме місце
        Entry e = entries[src];
        entries.RemoveAt(src);
        if (slot > src) slot--;
        entries.Insert(Math.Min(slot, entries.Count), e);
        hover = -1;
        DoLayout();
        BuildFolderIcons();
        RebuildDiskIds();
        SaveCfg();
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragIdx >= 0 && (e.Button & MouseButtons.Left) != 0)
        {
            if (!dragging)
            {
                Size ds = SystemInformation.DragSize;
                if (Math.Abs(e.X - dragAt.X) < ds.Width && Math.Abs(e.Y - dragAt.Y) < ds.Height) return;
                dragging = true;
                Capture = true;
                tip.Hide(this);
            }
            Cursor = Cursors.SizeAll;
            int s2 = SlotAt(e.Location);
            // віджети міняються місцями лише між собою; елементи не заходять у зону віджетів
            int wn = Store.WidgetCount(entries);
            if (entries[dragIdx].Kind == Kind.Widget) s2 = Math.Min(s2, wn);
            else s2 = Math.Max(s2, wn > 0 ? wn + 1 : 0);
            // елемент над групою (плиткою чи заголовком) - перенести в неї
            Entry grp;
            int gs = GroupSlotAt(e.Location, out grp);
            if (grp != null && gs >= 0) s2 = Math.Max(gs, wn > 0 ? wn + 1 : 0); else grp = null;
            if (s2 != dropSlot || grp != dropGroup) { dropSlot = s2; dropGroup = grp; Invalidate(); }
            return;
        }
        UpdateHover(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hover >= 0 && hover < entries.Count) InvalidateEntry(hover);
        if (tbHover >= 0) Invalidate(TbHot(tbHover));
        if (srHover) Invalidate(SearchRect());
        hover = -1;
        tbHover = -1;
        srHover = false;
        tip.Hide(this);
        Cursor = Cursors.Default;
        anim.Start();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        dragIdx = -1; dragging = false;
        if (e.Button == MouseButtons.Left && (settings == null || settings.IsDisposed))
        {
            int hi = Hit(e.Location);
            if (hi >= 0 && CanDrag(entries[hi])) { dragIdx = hi; dragAt = e.Location; }
        }
        if (flyout != null) { Entry hd = flyout.Head; flyout.Close(); flyoutJustClosed = hd; }
        else flyoutJustClosed = null;
    }

    Entry flyoutJustClosed;

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (dragging && e.Button == MouseButtons.Left) { FinishDrag(); return; }     // після перетягування - не запускати
        dragIdx = -1;
        int tb = HitTb(e.Location);
        if (e.Button == MouseButtons.Left && tb >= 0)
        {
            tip.Hide(this);
            ShowProfileWindow();
            return;
        }
        if (e.Button == MouseButtons.Left && cfg.ShowSearch && SearchRect().Contains(e.Location))
        {
            OpenSearch();
            return;
        }
        int i = Hit(e.Location);
        if (i < 0) return;
        Entry en = entries[i];
        if (e.Button == MouseButtons.Right)
        {
            ShowItemMenu(en, this, e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (en.Kind == Kind.Header)
        {
            if (en.Fold)
            {
                if (flyoutJustClosed != en) ShowFlyout(en);   // повторний клік по тій самій папці закриває її
            }
            else ToggleGroup(en);
        }
        else if (en.Kind == Kind.Widget) WidgetClick(en);
        else Launch(en);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int max = Math.Max(0, contentLen - ViewLen);
        if (max == 0) return;
        int ns = Math.Max(0, Math.Min(max, scroll - e.Delta / 120 * itemLen));
        if (ns == scroll) return;
        scroll = ns;
        tip.Hide(this);
        hover = -1;
        Invalidate();
        UpdateHover(PointToClient(Cursor.Position));
    }
}
