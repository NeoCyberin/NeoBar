# Кольорові емодзі (Noto Color Emoji, OFL) -> emoji.bin для NeoBar
# python gen_emoji.py ../../res/emoji.bin   (потрібні pillow, regex)
import sys, io, struct, regex
from PIL import Image, ImageDraw, ImageFont
SZ = 48
FONT = '/usr/share/fonts/truetype/noto/NotoColorEmoji.ttf'
f = ImageFont.truetype(FONT, 109)

def wanted(c):
    ch = chr(c)
    if not regex.match(r'\p{Emoji}', ch): return False
    if c < 0xA9: return False                         # цифри, #, *
    if 0x1F1E6 <= c <= 0x1F1FF: return False         # регіональні індикатори
    if 0x1F3FB <= c <= 0x1F3FF: return False         # відтінки шкіри
    if 0x2190 <= c <= 0x21FF: return False           # стрілки лишаються текстом
    if 0x25A0 <= c <= 0x25FF and c not in (0x25B6, 0x25C0): return False
    if 0x2660 <= c <= 0x2667: return False
    if c in (0xA9, 0xAE, 0x203C, 0x2049, 0x2122, 0x2139, 0x3030, 0x303D, 0x3297, 0x3299, 0x24C2): return False
    if c >= 0x1F000 or regex.match(r'\p{Emoji_Presentation}', ch): return True
    return 0x2600 <= c <= 0x27BF or 0x2B00 <= c <= 0x2BFF or 0x2300 <= c <= 0x23FF

items = []
for c in range(0x110000):
    if not wanted(c): continue
    im = Image.new('RGBA', (160, 160), (0, 0, 0, 0))
    ImageDraw.Draw(im).text((10, 10), chr(c), font=f, embedded_color=True)
    bb = im.getbbox()
    if not bb: continue
    px = im.get_flattened_data() if hasattr(im, 'get_flattened_data') else im.getdata()
    colored = sum(1 for p in px if p[3] > 200 and (abs(p[0]-p[1]) > 24 or abs(p[1]-p[2]) > 24))
    if colored < 40: continue                          # порожній або однотонний гліф - не емодзі
    cell = im.crop((10, 10, 146, 138))                 # клітинка 136x128
    sq = Image.new('RGBA', (136, 136), (0, 0, 0, 0)); sq.paste(cell, (0, 4))
    sm = sq.resize((SZ, SZ), Image.LANCZOS)
    q = sm.quantize(colors=192, method=Image.Quantize.FASTOCTREE, dither=Image.Dither.NONE); b = io.BytesIO(); q.save(b, 'PNG', optimize=True)
    items.append((c, b.getvalue()))
print(len(items), 'емодзі', sum(len(x[1]) for x in items) // 1024, 'КБ')
out = io.BytesIO()
out.write(struct.pack('<i', len(items)))
off = 0
for c, d in items:
    out.write(struct.pack('<iii', c, off, len(d))); off += len(d)
for c, d in items: out.write(d)
open(sys.argv[1], 'wb').write(out.getvalue())
