"""Draws the tray fox at 4x and downsamples (clean edges), writes fox.ico next to where it is run (16-256px)."""
from PIL import Image, ImageDraw
S = 1024
im = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(im)
ORANGE, DARK, WHITE, INNER = (240, 120, 30, 255), (60, 35, 25, 255), (255, 248, 238, 255), (90, 50, 40, 255)
# ears (outer orange, inner dark)
d.polygon([(120, 80), (400, 330), (150, 520)], fill=ORANGE)
d.polygon([(904, 80), (624, 330), (874, 520)], fill=ORANGE)
d.polygon([(190, 220), (330, 340), (210, 440)], fill=INNER)
d.polygon([(834, 220), (694, 340), (814, 440)], fill=INNER)
# head
d.polygon([(130, 470), (512, 300), (894, 470), (780, 760), (512, 960), (244, 760)], fill=ORANGE)
# white cheeks / muzzle
d.polygon([(130, 470), (330, 600), (512, 960), (244, 760)], fill=WHITE)
d.polygon([(894, 470), (694, 600), (512, 960), (780, 760)], fill=WHITE)
# eyes + nose
for x in (370, 654):
    d.ellipse((x - 38, 540 - 46, x + 38, 540 + 46), fill=DARK)
d.polygon([(440, 800), (584, 800), (512, 890)], fill=DARK)
im.save("fox.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
im.resize((256, 256), Image.LANCZOS).save("fox_preview.png")
