import sys
from PIL import Image, ImageDraw, ImageFont
keys=sys.argv[2:]
out=sys.argv[1]
lines=["Привіт, світе! Київ ґанок їжак Єва","ABCDEFGHIJKLMNOPQRSTUVWXYZ","abcdefghijklmnopqrstuvwxyz 0123456789","АБВГҐДЕЄЖЗИІЇЙКЛМНОПРСТУФХЦЧШЩЬЮЯ","абвгґдеєжзиіїйклмнопрстуфхцчшщьюя ЪЫЭЁ","?!.,:;-—«»()№ %$₴€ \"'’ ъыэё"]
H=len(lines)*62+20
im=Image.new('RGB',(1500,(H+30)*len(keys)),(20,24,32))
d=ImageDraw.Draw(im)
y=0
for k in keys:
    f=ImageFont.truetype('out/%s.ttf'%k,46)
    d.text((10,y+2),k,fill=(120,160,200),font=ImageFont.load_default())
    yy=y+18
    for l in lines:
        d.text((10,yy),l,fill=(230,235,245),font=f); yy+=62
    y+=H+30
im.save(out)
