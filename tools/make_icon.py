"""Generate appicon.ico — an aurora motif matching the wallpaper theme
(green -> teal -> blue -> purple glow on near-black), multi-resolution.
Run from the repo root: python tools/make_icon.py
"""
import numpy as np
from PIL import Image, ImageFilter, ImageDraw

S = 512  # master render size


def smoothstep(edge0, edge1, x):
    t = np.clip((x - edge0) / (edge1 - edge0), 0.0, 1.0)
    return t * t * (3 - 2 * t)


def vertical_gradient(yn):
    """Map normalized y (0 top .. 1 bottom) to an aurora RGB color."""
    stops = [
        (0.00, (170, 92, 248)),   # purple (top)
        (0.34, (72, 130, 246)),   # blue
        (0.62, (24, 186, 194)),   # teal
        (1.00, (46, 208, 138)),   # green (bottom)
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


def build_aurora():
    ys, xs = np.mgrid[0:S, 0:S].astype(np.float32)
    xn = xs / S
    yn = ys / S

    # Curtains: wavy vertical light sheets.
    curtains = [
        # (x-center frac, wave amp, wave freq, phase, width frac, strength)
        (0.30, 0.055, 3.1, 0.4, 0.055, 1.00),
        (0.48, 0.075, 2.4, 2.1, 0.070, 1.15),
        (0.66, 0.050, 3.6, 4.0, 0.050, 0.95),
        (0.80, 0.045, 2.8, 1.2, 0.042, 0.70),
    ]
    field = np.zeros((S, S), np.float32)
    for xc, amp, freq, phase, w, strength in curtains:
        center = xc + amp * np.sin(yn * np.pi * freq + phase)
        dx = (xn - center) / w
        band = np.exp(-dx * dx)
        env = smoothstep(0.08, 0.30, yn) * (1.0 - smoothstep(0.78, 0.98, yn))
        streak = 0.75 + 0.25 * np.sin(yn * np.pi * 22 + xc * 30)
        field += band * env * streak * strength
    field = np.clip(field, 0.0, 1.6)

    r, g, b = vertical_gradient(yn)
    a = field
    rgb = np.clip(np.stack([r * a, g * a, b * a], axis=-1), 0, 255).astype(np.uint8)
    aurora = Image.fromarray(rgb, "RGB")

    glow = aurora.filter(ImageFilter.GaussianBlur(S * 0.02))
    aurora = Image.blend(aurora, glow, 0.55)
    glow2 = aurora.filter(ImageFilter.GaussianBlur(S * 0.05))
    aurora = Image.fromarray(
        np.clip(np.asarray(aurora, np.int16) + (np.asarray(glow2, np.int16) * 0.5).astype(np.int16),
                0, 255).astype(np.uint8),
        "RGB",
    )
    return aurora


def background():
    ys, xs = np.mgrid[0:S, 0:S].astype(np.float32)
    xn = xs / S; yn = ys / S
    base = np.array([9, 13, 24], np.float32)
    tint = np.array([14, 26, 40], np.float32)
    d = np.sqrt((xn - 0.45) ** 2 + (yn - 0.62) ** 2)
    m = (1.0 - np.clip(d / 0.8, 0, 1))[..., None]
    bg = base + (tint - base) * (m * 0.9)
    return Image.fromarray(np.clip(bg, 0, 255).astype(np.uint8), "RGB")


def add_stars(img):
    rng = np.random.default_rng(7)
    d = ImageDraw.Draw(img, "RGBA")
    for _ in range(26):
        x = rng.uniform(0.06, 0.94) * S
        y = rng.uniform(0.06, 0.42) * S
        r = rng.uniform(0.6, 2.2) * (S / 256)
        a = int(rng.uniform(90, 220))
        d.ellipse([x - r, y - r, x + r, y + r], fill=(230, 240, 255, a))
    return img


def rounded_mask(size, radius):
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return m


def main():
    bg = background().convert("RGB")
    aurora = build_aurora()
    composed = np.clip(np.asarray(bg, np.float32) + np.asarray(aurora, np.float32), 0, 255).astype(np.uint8)
    img = Image.fromarray(composed, "RGB")
    img = add_stars(img)

    img = img.convert("RGBA")
    mask = rounded_mask(S, radius=int(S * 0.22))
    img.putalpha(mask)
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([2, 2, S - 3, S - 3], radius=int(S * 0.22),
                        outline=(255, 255, 255, 40), width=max(2, S // 180))

    sizes = [16, 24, 32, 48, 64, 128, 256]
    # Pillow's ICO writer downsamples the provided image to each requested size,
    # so hand it the full-res master (not a pre-shrunk frame).
    master = img.resize((256, 256), Image.LANCZOS)
    master.save("appicon.ico", format="ICO", sizes=[(s, s) for s in sizes])
    master.save("tools/appicon-preview.png")
    print("wrote appicon.ico and tools/appicon-preview.png")


if __name__ == "__main__":
    main()
