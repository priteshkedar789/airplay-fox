"""Builds docs/social-preview.png (1280x640) from the fox icon and a real screenshot.
Run from anywhere: python tools/make_social_preview.py   (needs Pillow)."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parent.parent
W, H = 1280, 640
FONTS = Path("C:/Windows/Fonts")


def font(name, size):
    try:
        return ImageFont.truetype(str(FONTS / name), size)
    except OSError:
        return ImageFont.load_default()


# background: vertical gradient
bg = Image.new("RGB", (W, H))
px = bg.load()
top, bottom = (22, 25, 31), (36, 42, 56)
for y in range(H):
    t = y / (H - 1)
    row = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
    for x in range(W):
        px[x, y] = row
canvas = bg.convert("RGBA")
d = ImageDraw.Draw(canvas)

# fox icon (largest frame of the .ico)
ico = Image.open(ROOT / "src" / "AirplayFox" / "fox.ico")
ico.size = max(ico.info.get("sizes", {(256, 256)}))
fox = ico.convert("RGBA").resize((200, 200), Image.LANCZOS)
canvas.alpha_composite(fox, (80, 90))

# text
d.text((300, 100), "AirplayFox", font=font("segoeuib.ttf", 96), fill=(255, 255, 255))
d.text((304, 215), "Windows audio to HomePod", font=font("segoeui.ttf", 38), fill=(255, 168, 82))
d.text((80, 360), "AirPlay 2 sender for Windows", font=font("segoeui.ttf", 38), fill=(220, 224, 232))
d.text((80, 415), "Tray app  |  0 to 4 s adjustable latency", font=font("segoeui.ttf", 34), fill=(170, 178, 192))
d.text((80, 520), "Free and open source  |  MIT  |  Tested on Windows 10", font=font("segoeui.ttf", 28), fill=(140, 148, 164))

# real screenshot on the right, rounded corners + soft shadow
shot = Image.open(ROOT / "docs" / "tray-menu-streaming.png").convert("RGBA")
scale = 0.80
shot = shot.resize((int(shot.width * scale), int(shot.height * scale)), Image.LANCZOS)
mask = Image.new("L", shot.size, 0)
ImageDraw.Draw(mask).rounded_rectangle((0, 0, *shot.size), radius=18, fill=255)
pos = (W - shot.width - 60, (H - shot.height) // 2 + 20)
shadow = Image.new("RGBA", (shot.width + 80, shot.height + 80), (0, 0, 0, 0))
ImageDraw.Draw(shadow).rounded_rectangle((40, 50, shot.width + 40, shot.height + 50), radius=18, fill=(0, 0, 0, 150))
shadow = shadow.filter(ImageFilter.GaussianBlur(18))
canvas.alpha_composite(shadow, (pos[0] - 40, pos[1] - 40))
canvas.paste(shot, pos, mask)

out = ROOT / "docs" / "social-preview.png"
canvas.convert("RGB").save(out, optimize=True)
print("wrote", out, out.stat().st_size // 1024, "KB")
