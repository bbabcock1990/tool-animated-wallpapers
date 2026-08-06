"""Generate a 1280x640 GitHub social-preview card in the aurora theme.
Run from the repo root: python tools/make_social.py
Output: .github/social-preview.png
"""
import os
import numpy as np
from PIL import Image, ImageFilter, ImageDraw, ImageFont

W, H = 1280, 640


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0.0, 1.0)
    return t * t * (3 - 2 * t)


def vgrad(yn):
    stops = [
        (0.00, (170, 92, 248)),
        (0.34, (72, 130, 246)),
        (0.62, (24, 186, 194)),
        (1.00, (46, 208, 138)),
    ]
    r = np.zeros_like(yn); g = np.zeros_like(yn); b = np.zeros_like(yn)
    for i in range(len(stops) - 1):
        y0, c0 = stops[i]; y1, c1 = stops[i + 1]
        m = (yn >= y0) & (yn <= y1)
        t = (yn[m] - y0) / (y1 - y0)
        r[m] = c0[0] + (c1[0] - c0[0]) * t
        g[m] = c0[1] + (c1[1] - c0[1]) * t
        b[m] = c0[2] + (c1[2] - c0[2]) * t
    return r, g, b


def aurora():
    ys, xs = np.mgrid[0:H, 0:W].astype(np.float32)
    xn = xs / W; yn = ys / H
    curtains = [
        (0.60, 0.05, 3.0, 0.4, 0.045, 1.0),
        (0.70, 0.07, 2.3, 2.1, 0.060, 1.15),
        (0.80, 0.05, 3.4, 4.0, 0.040, 0.95),
        (0.90, 0.04, 2.7, 1.2, 0.035, 0.8),
        (0.52, 0.06, 2.0, 3.3, 0.05, 0.7),
    ]
    field = np.zeros((H, W), np.float32)
    for xc, amp, freq, phase, w, s in curtains:
        center = xc + amp * np.sin(yn * np.pi * freq + phase)
        dx = (xn - center) / w
        band = np.exp(-dx * dx)
        env = smoothstep(0.02, 0.35, yn) * (1 - smoothstep(0.80, 1.02, yn))
        streak = 0.78 + 0.22 * np.sin(yn * np.pi * 20 + xc * 30)
        field += band * env * streak * s
    field = np.clip(field, 0, 1.6)
    r, g, b = vgrad(yn)
    rgb = np.clip(np.stack([r, g, b], -1) * field[..., None], 0, 255).astype(np.uint8)
    au = Image.fromarray(rgb, "RGB")
    au = Image.blend(au, au.filter(ImageFilter.GaussianBlur(10)), 0.55)
    au = Image.fromarray(
        np.clip(np.asarray(au, np.int16) +
                (np.asarray(au.filter(ImageFilter.GaussianBlur(26)), np.int16) * 0.5).astype(np.int16),
                0, 255).astype(np.uint8), "RGB")
    return au


def background():
    ys, xs = np.mgrid[0:H, 0:W].astype(np.float32)
    xn = xs / W; yn = ys / H
    base = np.array([8, 12, 22], np.float32)
    tint = np.array([13, 24, 40], np.float32)
    d = np.sqrt((xn - 0.72) ** 2 + (yn - 0.6) ** 2)
    m = (1 - np.clip(d / 0.9, 0, 1))[..., None]
    bg = base + (tint - base) * (m * 0.9)
    return Image.fromarray(np.clip(bg, 0, 255).astype(np.uint8), "RGB")


def add_stars(img):
    rng = np.random.default_rng(11)
    d = ImageDraw.Draw(img, "RGBA")
    for _ in range(70):
        x = rng.uniform(0.02, 0.98) * W
        y = rng.uniform(0.02, 0.85) * H
        r = rng.uniform(0.5, 2.0)
        a = int(rng.uniform(60, 200))
        d.ellipse([x - r, y - r, x + r, y + r], fill=(230, 240, 255, a))
    return img


def font(name, size):
    for p in (rf"C:\Windows\Fonts\{name}", name):
        try:
            return ImageFont.truetype(p, size)
        except OSError:
            continue
    return ImageFont.load_default()


def main():
    bg = background()
    au = aurora()
    img = Image.fromarray(
        np.clip(np.asarray(bg, np.float32) + np.asarray(au, np.float32), 0, 255).astype(np.uint8), "RGB")
    img = add_stars(img).convert("RGBA")

    # Left-side scrim so text stays legible over the aurora.
    scrim = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    sd = ImageDraw.Draw(scrim)
    for x in range(W):
        a = int(210 * max(0.0, 1.0 - x / (W * 0.72)))
        sd.line([(x, 0), (x, H)], fill=(6, 9, 18, a))
    img = Image.alpha_composite(img, scrim)

    d = ImageDraw.Draw(img)
    x0 = 72

    # App icon (top-left accent).
    try:
        ico = Image.open("appicon.ico")
        ico = ico.ico.getimage((256, 256)) if hasattr(ico, "ico") else ico
        ico = ico.resize((96, 96), Image.LANCZOS).convert("RGBA")
        img.alpha_composite(ico, (x0, 70))
    except Exception:
        pass

    f_kicker = font("seguisb.ttf", 30)
    f_title = font("segoeuib.ttf", 82)
    f_tag = font("segoeui.ttf", 36)
    f_small = font("seguisb.ttf", 27)

    def shadow_text(pos, text, fnt, fill, sh=(0, 0, 0, 170)):
        d.text((pos[0] + 2, pos[1] + 2), text, font=fnt, fill=sh)
        d.text(pos, text, font=fnt, fill=fill)

    shadow_text((x0 + 116, 92), "WINDOWS 11 LIVE WALLPAPER", f_kicker, (150, 214, 255, 255))

    shadow_text((x0, 210), "Animated Desktop", f_title, (255, 255, 255, 255))
    shadow_text((x0, 300), "Wallpapers Helper", f_title, (255, 255, 255, 255))

    shadow_text((x0, 410), "Any HTML / CSS / JS / canvas page as a native", f_tag, (206, 216, 232, 255))
    shadow_text((x0, 452), "live wallpaper - behind your desktop icons.", f_tag, (206, 216, 232, 255))

    # Feature pills.
    pills = ["One-command install", "Multi-monitor", "Calendar module", "System-tray toggle"]
    px = x0; py = 528
    for text in pills:
        tw = d.textlength(text, font=f_small)
        pad = 18
        w = tw + pad * 2; h = 46
        d.rounded_rectangle([px, py, px + w, py + h], radius=23,
                            fill=(255, 255, 255, 26), outline=(150, 214, 255, 120), width=2)
        d.text((px + pad, py + 9), text, font=f_small, fill=(224, 234, 248, 255))
        px += w + 16

    os.makedirs(".github", exist_ok=True)
    img.convert("RGB").save(".github/social-preview.png", "PNG")
    print("wrote .github/social-preview.png")


if __name__ == "__main__":
    main()
