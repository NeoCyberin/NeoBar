import sys, os, gzip, struct, io
d=sys.argv[1]; outp=sys.argv[2]
REG=['dark','light','glass','cyber','steam','urban','pink','nature','art']
files=[k+'.ttf' for k in REG]     # лише звичайні накреслення: Windows надійно підхоплює ~9-12 файлів, жирний/курсив робить RichEdit
def w7(n):
    b=bytearray()
    while True:
        x=n&0x7f; n>>=7
        if n: b.append(x|0x80)
        else: b.append(x); break
    return bytes(b)
buf=io.BytesIO()
buf.write(struct.pack('<i',len(files)))
for f in files:
    name=f[:-4].encode('utf-8')
    data=open(os.path.join(d,f),'rb').read()
    buf.write(w7(len(name))); buf.write(name)
    buf.write(struct.pack('<i',len(data))); buf.write(data)
with gzip.GzipFile(outp,'wb',compresslevel=9,mtime=0) as g: g.write(buf.getvalue())
print(len(files), os.path.getsize(outp))
