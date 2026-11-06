Шрифти NeoBar (SB Dark, SB Light, SB Glass, SB Cyber, SB Steam, SB Urban, SB Pink, SB Nature, SB Art).
Намальовані як скелети літер (skeleton.py) + стилі (gen.py: товщина, кінці, засічки, глітч, трафарет, перо, "рука").
Перегенерація: pip install shapely fonttools pillow
  python gen.py out            (створює 36 файлів .ttf; у ресурс йдуть лише 9 звичайних)
  python pack.py out ../../res/sbfonts.bin   (пакує в ресурс, який вбудовує build.bat)
  python preview.py p.png dark cyber ...   (картинка-перегляд)

Кольорові емодзі: emoji.bin (картинки 48x48 із Noto Color Emoji, ліцензія OFL).
  pip install pillow regex
  python gen_emoji.py ../../res/emoji.bin   (потрібен шрифт NotoColorEmoji.ttf; шлях у gen_emoji.py)
