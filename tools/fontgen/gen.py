# -*- coding: utf-8 -*-
# Генератор шрифтів NeoBar: з ескізів літер (skeleton.py) робить TTF у кількох стилях.
import math, random, os, sys, struct, gzip, io
from shapely.geometry import LineString, Polygon, MultiPolygon, MultiPoint, box, Point
from shapely.ops import unary_union
from shapely.affinity import translate, scale, skew, rotate
from shapely.geometry.polygon import orient
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen

import skeleton as SK

UPM = 1000


class Style(object):
    def __init__(self, **kw):
        self.family = 'SB Dark'
        self.w = 80            # товщина штриха (Regular)
        self.bold = 1.5        # множник для Bold
        self.cap = 'flat'      # flat | round | square
        self.join = 'mitre'    # mitre | round | bevel
        self.xs = 1.0          # масштаб по ширині
        self.slant = 0.0       # власний нахил, градуси
        self.sb = 48           # бічний зазор
        self.step = 10         # крок дуг (градуси)
        self.pen = None        # (кут, великий діаметр, малий) - каліграфічне перо
        self.serif = False     # плитові засічки
        self.glitch = False
        self.stencil = False
        self.wobble = 0.0      # "рука": амплітуда дрижання
        self.bounce = 0.0      # стрибки базової лінії
        self.tilt = 0.0        # випадковий нахил літер
        self.space = 280
        self.italic = 12.0
        for k, v in kw.items():
            setattr(self, k, v)


STYLES = {
    # темна: щільний геометричний гротеск
    'dark': Style(family='SB Dark', w=98, bold=1.4, cap='flat', join='mitre', xs=0.97, sb=56),
    # світла: тонша, з округлими кінцями, доброзичлива
    'light': Style(family='SB Light', w=54, cap='round', join='round', xs=1.04, sb=60, italic=10),
    # скляна: надтонка, широка, елегантна
    'glass': Style(family='SB Glass', w=30, bold=2.0, cap='flat', join='mitre', xs=1.16, sb=74, space=320),
    # кіберпанк: кутаста, зі зрізаними кутами й "глітчем"
    'cyber': Style(family='SB Cyber', w=84, cap='flat', join='bevel', xs=0.96, sb=56, step=45, glitch=True, italic=9, bold=1.35),
    # стімпанк: плитові засічки
    'steam': Style(family='SB Steam', w=76, cap='flat', join='mitre', xs=1.06, sb=62, serif=True, bold=1.6),
    # урбаністична: важка, вузька, трафаретна
    'urban': Style(family='SB Urban', w=136, bold=1.22, cap='flat', join='mitre', xs=0.8, sb=46, stencil=True, space=240, italic=8),
    # гламурна: кругла "бульбашкова"
    'pink': Style(family='SB Pink', w=132, bold=1.26, cap='round', join='round', xs=1.12, sb=46, bounce=20, tilt=3.2, italic=10),
    # природна: каліграфічне перо
    'nature': Style(family='SB Nature', w=84, bold=1.5, cap='round', join='round', xs=1.0, sb=56, pen=(38, 150, 30), slant=5.0, italic=9),
    # артистична: ескіз від руки
    'art': Style(family='SB Art', w=58, bold=1.6, cap='round', join='round', xs=1.02, sb=56, wobble=16.0, bounce=11, tilt=3.8, slant=3.0, italic=9),
}


def resample(pts, maxseg=18.0):
    out = [pts[0]]
    for i in range(1, len(pts)):
        x0, y0 = out[-1]
        x1, y1 = pts[i]
        d = math.hypot(x1 - x0, y1 - y0)
        n = int(d // maxseg)
        for k in range(1, n + 1):
            t = k / float(n + 1)
            out.append((x0 + (x1 - x0) * t, y0 + (y1 - y0) * t))
        out.append((x1, y1))
    return out


def pen_polygon(angle, major, minor):
    pts = []
    a = math.radians(angle)
    for i in range(24):
        t = 2 * math.pi * i / 24
        x = major / 2.0 * math.cos(t)
        y = minor / 2.0 * math.sin(t)
        pts.append((x * math.cos(a) - y * math.sin(a), x * math.sin(a) + y * math.cos(a)))
    return pts


def stroke_geom(pts, w, st):
    if len(pts) < 2:
        return None
    if st.pen:
        ang, major, minor = st.pen
        k = w / 84.0
        pp = pen_polygon(ang, major * k, minor * k)
        pts = resample(pts, 14.0)
        parts = []
        for i in range(len(pts) - 1):
            a = pts[i]
            b = pts[i + 1]
            parts.append(MultiPoint([(x + a[0], y + a[1]) for (x, y) in pp] + [(x + b[0], y + b[1]) for (x, y) in pp]).convex_hull)
        return unary_union(parts)
    ls = LineString(pts)
    if ls.length < 0.5:
        return None
    if ls.length < 30:      # крапка
        cx = (pts[0][0] + pts[-1][0]) / 2.0
        cy = (pts[0][1] + pts[-1][1]) / 2.0
        r = w * 0.56
        if st.cap == 'round':
            return Point(cx, cy).buffer(r, resolution=10)
        if st.join == 'bevel':
            return Polygon([(cx - r, cy), (cx, cy + r), (cx + r, cy), (cx, cy - r)])
        return box(cx - r * 0.9, cy - r * 0.9, cx + r * 0.9, cy + r * 0.9)
    return ls.buffer(w / 2.0, cap_style=st.cap, join_style=st.join, resolution=8, mitre_limit=2.6)


def add_serifs(strokes, w, st):
    out = []
    for s in strokes:
        if len(s) < 2:
            continue
        for (p, q) in ((s[0], s[1]), (s[-1], s[-2])):
            dx, dy = p[0] - q[0], p[1] - q[1]
            L = math.hypot(dx, dy)
            if L >= 200 and abs(dy) > 0.9 * L and min(abs(p[1] - yy) for yy in (0, SK.X, SK.C, SK.ASC)) < 16:
                sw, sh = w * 2.7, w * 0.62
                cy = p[1] - (sh / 2.0 if dy > 0 else -sh / 2.0)
                out.append(box(p[0] - sw / 2.0, cy - sh / 2.0, p[0] + sw / 2.0, cy + sh / 2.0))
    return out


def wobble_pts(pts, amp, rnd):
    pts = resample(pts, 16.0)
    ph1, ph2 = rnd.uniform(0, 6.28), rnd.uniform(0, 6.28)
    f1, f2 = rnd.uniform(0.05, 0.09), rnd.uniform(0.11, 0.19)
    out = []
    for i, (x, y) in enumerate(pts):
        out.append((x + amp * (0.7 * math.sin(i * f1 * 3 + ph1) + 0.3 * math.sin(i * f2 * 3 + ph2)),
                    y + amp * (0.7 * math.cos(i * f1 * 3 + ph2) + 0.3 * math.sin(i * f2 * 3 + ph1))))
    return out


def polys(geom):
    if geom.is_empty:
        return []
    if isinstance(geom, Polygon):
        return [geom]
    if isinstance(geom, MultiPolygon):
        return list(geom.geoms)
    res = []
    for g in getattr(geom, 'geoms', []):
        res.extend(polys(g))
    return res


def make_glyph(ch, strokes, st, bold, italic, seed):
    rnd = random.Random(seed * 7919 + ord(ch))
    w = st.w * (st.bold if bold else 1.0)
    geoms = []
    ss = []
    for s in strokes:
        pts = [(x * st.xs, y) for (x, y) in s]
        if st.wobble:
            pts = wobble_pts(pts, st.wobble, rnd)
        ss.append(pts)
    for pts in ss:
        g = stroke_geom(pts, w, st)
        if g is not None:
            geoms.append(g)
    if st.serif:
        geoms.extend(add_serifs(ss, w, st))
    if not geoms:
        return None
    G = unary_union(geoms)
    minx, miny, maxx, maxy = G.bounds
    # ефекти
    if st.stencil and (maxy - miny) > 380:
        ym = (miny + maxy) / 2.0 if miny > -60 else 350
        G = G.difference(box(minx - 5, ym - 12, maxx + 5, ym + 12))
    if st.glitch and (maxy - miny) > 300:
        # один зсунутий "рядок" посередині + тонкий розріз біля низу - помітно, але літери читаються
        mid = G.intersection(box(minx - 80, 330, maxx + 80, 376))
        rest = G.difference(box(minx - 80, 330, maxx + 80, 376)).difference(box(minx - 80, 96, maxx + 80, 110))
        G = unary_union([rest, translate(mid, 40, 0)])
    if st.tilt:
        a = rnd.uniform(-st.tilt, st.tilt)
        G = rotate(G, a, origin=(G.centroid.x, 0))
    if st.bounce:
        G = translate(G, 0, rnd.uniform(-st.bounce, st.bounce))
    sl = st.slant + (st.italic if italic else 0.0)
    if sl:
        G = skew(G, xs=sl, origin=(0, 0))
    minx, miny, maxx, maxy = G.bounds
    sb = st.sb + (w - st.w) * 0.35
    G = translate(G, sb - minx, 0)
    adv = (maxx - minx) + 2 * sb
    return G, adv


def to_contours(G):
    G = G.simplify(1.4, preserve_topology=True)
    res = []
    for p in polys(G):
        if p.area < 40:
            continue
        p = orient(p, sign=-1.0)  # зовнішній контур за годинниковою
        res.append(list(p.exterior.coords)[:-1])
        for h in p.interiors:
            res.append(list(h.coords)[:-1])
    return res


def build_font(key, st, bold, italic, out_path):
    sk = SK.build_for(st.step) if hasattr(SK, 'build_for') else None
    SK.STEP[0] = st.step
    skel = SK.build()
    glyph_order = ['.notdef', 'space', 'nbspace']
    glyphs = {}
    metrics = {}
    cmap = {}
    pen = TTGlyphPen(None)
    # .notdef - рамка
    pen.moveTo((60, 0)); pen.lineTo((60, 700)); pen.lineTo((460, 700)); pen.lineTo((460, 0)); pen.closePath()
    pen.moveTo((110, 50)); pen.lineTo((410, 50)); pen.lineTo((410, 650)); pen.lineTo((110, 650)); pen.closePath()
    glyphs['.notdef'] = pen.glyph()
    metrics['.notdef'] = (520, 60)
    sp = int(st.space)
    for name in ('space', 'nbspace'):
        glyphs[name] = TTGlyphPen(None).glyph()
        metrics[name] = (sp, 0)
    cmap[0x20] = 'space'
    cmap[0xA0] = 'nbspace'
    seed = abs(hash(key)) % 1000 if False else sum(ord(c) for c in key)
    for ch in SK.ALL:
        if ch not in skel:
            continue
        r = make_glyph(ch, skel[ch], st, bold, italic, seed)
        if r is None:
            continue
        G, adv = r
        cont = to_contours(G)
        if not cont:
            continue
        name = 'uni%04X' % ord(ch)
        pen = TTGlyphPen(None)
        for c in cont:
            pts = [(int(round(x)), int(round(y))) for (x, y) in c]
            # прибрати дублікати поспіль
            clean = [pts[0]]
            for p in pts[1:]:
                if p != clean[-1]:
                    clean.append(p)
            if len(clean) < 3:
                continue
            pen.moveTo(clean[0])
            for p in clean[1:]:
                pen.lineTo(p)
            pen.closePath()
        glyphs[name] = pen.glyph()
        minx = min(int(round(p[0])) for c in cont for p in c)
        metrics[name] = (int(round(adv)), minx)
        glyph_order.append(name)
        cmap[ord(ch)] = name
    fb = FontBuilder(UPM, isTTF=True)
    fb.setupGlyphOrder(glyph_order)
    fb.setupCharacterMap(cmap)
    fb.setupGlyf(glyphs)
    fb.setupHorizontalMetrics(metrics)
    fb.setupHorizontalHeader(ascent=950, descent=-280)
    style_name = ('Bold Italic' if bold and italic else 'Bold' if bold else 'Italic' if italic else 'Regular')
    fam = st.family
    fb.setupNameTable({
        'familyName': fam,
        'styleName': style_name,
        'uniqueFontIdentifier': '%s %s NeoBar' % (fam, style_name),
        'fullName': '%s %s' % (fam, style_name) if style_name != 'Regular' else fam,
        'psName': fam.replace(' ', '') + '-' + style_name.replace(' ', ''),
        'version': 'Version 1.000',
    })
    sel = 0x40 if not bold and not italic else 0
    if bold:
        sel |= 0x20
    if italic:
        sel |= 0x01
    fb.setupOS2(sTypoAscender=950, sTypoDescender=-280, sTypoLineGap=0, usWinAscent=950, usWinDescent=280,
                sxHeight=500, sCapHeight=700, usWeightClass=700 if bold else 400, fsSelection=sel,
                fsType=0, achVendID='SBFN', xAvgCharWidth=520,
                ulCodePageRange1=0x00000007,      # 1252 (Latin 1), 1250 (Latin 2), 1251 (Cyrillic)
                ulCodePageRange2=0xC0000000,      # 437 (US), 866 (Cyrillic OEM)
                yStrikeoutSize=58, yStrikeoutPosition=270)    # лінія закреслення посередині x-висоти, а не на базовій лінії
    fb.setupPost(underlinePosition=-110, underlineThickness=58)
    fb.font['head'].macStyle = (1 if bold else 0) | (2 if italic else 0)
    fb.save(out_path)


def main(outdir, only=None):
    os.makedirs(outdir, exist_ok=True)
    made = []
    for key, st in STYLES.items():
        if only and key not in only:
            continue
        for bold in (False, True):
            for italic in (False, True):
                tag = key + ('B' if bold else '') + ('I' if italic else '')
                path = os.path.join(outdir, '%s.ttf' % tag)
                build_font(key, st, bold, italic, path)
                made.append(path)
                print(path, os.path.getsize(path))
    return made


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2:] or None)
