#!/usr/bin/env python3
"""Renders the FlowSwitch logo at every icon size (hand-tuned per size, 8x supersampled).

Concept: a small glowing core (the selected window) with two tilted concentric orbits and two
planets — the Solar System switcher reduced to a mark. Output:
  assets/icons/FlowSwitch.ico          app / tray icon (16…256)
  assets/icons/FlowSwitch-paused.ico   desaturated tray icon while paused
  assets/logo/flowswitch-*.png         previews / store art
"""
import math
import os
from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
SS = 8
INDIGO = (122, 132, 255)
CYAN = (86, 214, 255)
CORE = (236, 242, 255)


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(len(a)))


def draw_orbit(size, cx, cy, rx, ry, rot, width, alpha_front, alpha_back):
    """A smooth elliptical ring, brighter at the front (bottom) like the orbits in the overlay."""
    ring = Image.new('L', (size, size), 0)
    ImageDraw.Draw(ring).ellipse([cx - rx, cy - ry, cx + rx, cy + ry], outline=255, width=max(1, int(round(width))))
    # Front/back falloff and indigo→cyan colour sweep, both in the orbit's own (unrotated) frame.
    shade = Image.new('L', (size, size), 0)
    sd = ImageDraw.Draw(shade)
    colour = Image.new('RGB', (size, size))
    cd = ImageDraw.Draw(colour)
    for y in range(size):
        front = min(1.0, max(0.0, (y - (cy - ry)) / (2 * ry)))
        sd.line([(0, y), (size, y)], fill=int(255 * (alpha_back + (alpha_front - alpha_back) * front ** 1.2)))
    for x in range(size):
        t = min(1.0, max(0.0, (x - (cx - rx)) / (2 * rx)))
        cd.line([(x, 0), (x, size)], fill=lerp(INDIGO, CYAN, t))
    from PIL import ImageChops
    alpha = ImageChops.multiply(ring, shade)
    layer = Image.merge('RGBA', (*colour.split(), alpha))
    return layer.rotate(-math.degrees(rot), resample=Image.BICUBIC, center=(cx, cy))


def orbit_point(cx, cy, rx, ry, rot, angle):
    c, s = math.cos(rot), math.sin(rot)  # image y points down, so this matches Image.rotate(-deg)
    x, y = rx * math.sin(angle), ry * math.cos(angle)
    return cx + x * c - y * s, cy + x * s + y * c


def glow(size, cx, cy, radius, color, strength):
    g = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(g)
    d.ellipse([cx - radius, cy - radius, cx + radius, cy + radius], fill=color + (int(255 * strength),))
    return g.filter(ImageFilter.GaussianBlur(radius * 0.6))


def render(px, background=True, desaturate=False):
    S = px * SS
    img = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    small = px <= 24
    if background:
        bg = Image.new('RGBA', (S, S), (0, 0, 0, 0))
        mask = Image.new('L', (S, S), 0)
        inset = S * (0.02 if small else 0.04)
        ImageDraw.Draw(mask).rounded_rectangle([inset, inset, S - inset, S - inset], radius=S * 0.24, fill=255)
        grad = Image.new('RGBA', (S, S))
        gd = ImageDraw.Draw(grad)
        for y in range(S):
            t = y / S
            gd.line([(0, y), (S, y)], fill=lerp((24, 31, 56), (7, 9, 18), t) + (255,))
        bg.paste(grad, (0, 0), mask)
        img = Image.alpha_composite(img, bg)
        if not small:
            hl = Image.new('RGBA', (S, S), (0, 0, 0, 0))
            ImageDraw.Draw(hl).rounded_rectangle([inset, inset, S - inset, S - inset], radius=S * 0.24,
                                                 outline=(255, 255, 255, 34), width=max(1, S // 128))
            img = Image.alpha_composite(img, hl)

    cx, cy = S * 0.5, S * 0.52
    rot = math.radians(-18)
    outer = (S * 0.36, S * 0.16)
    inner = (S * 0.235, S * 0.1)
    stroke = S * (0.085 if px <= 16 else 0.06 if px <= 24 else 0.036 if px <= 48 else 0.02)

    img = Image.alpha_composite(img, glow(S, cx, cy, S * 0.2, (110, 130, 255), 0.55))

    img = Image.alpha_composite(img, draw_orbit(S, cx, cy, outer[0], outer[1], rot, stroke, 1.0, 0.3 if not small else 0.6))
    if px > 16:
        img = Image.alpha_composite(img, draw_orbit(S, cx, cy, inner[0], inner[1], rot, stroke * 0.85, 0.85, 0.25 if not small else 0.5))

    # Core: a soft sun with a crisp centre.
    core_r = S * (0.13 if px <= 16 else 0.09 if px <= 32 else 0.072)
    img = Image.alpha_composite(img, glow(S, cx, cy, core_r * 2.2, (140, 170, 255), 0.9))
    d = ImageDraw.Draw(img)
    d.ellipse([cx - core_r, cy - core_r, cx + core_r, cy + core_r], fill=CORE + (255,))

    # Planets: one bright on the outer orbit (front right), one on the inner orbit (back left).
    p1 = orbit_point(cx, cy, outer[0], outer[1], rot, math.radians(58))
    r1 = S * (0.085 if px <= 16 else 0.065 if px <= 32 else 0.055)
    img = Image.alpha_composite(img, glow(S, p1[0], p1[1], r1 * 2.4, CYAN, 0.8))
    d = ImageDraw.Draw(img)
    d.ellipse([p1[0] - r1, p1[1] - r1, p1[0] + r1, p1[1] + r1], fill=(150, 232, 255, 255))
    if px > 16:
        p2 = orbit_point(cx, cy, inner[0], inner[1], rot, math.radians(-128))
        r2 = S * (0.05 if px <= 32 else 0.04)
        img = Image.alpha_composite(img, glow(S, p2[0], p2[1], r2 * 2.2, INDIGO, 0.7))
        d = ImageDraw.Draw(img)
        d.ellipse([p2[0] - r2, p2[1] - r2, p2[0] + r2, p2[1] + r2], fill=(170, 176, 255, 255))

    out = img.resize((px, px), Image.LANCZOS)
    if desaturate:
        r, g, b, a = out.split()
        grey = Image.merge('RGB', (r, g, b)).convert('L')
        grey = grey.point(lambda v: int(v * 0.8))
        out = Image.merge('RGBA', (grey, grey, grey, a))
    return out


def main():
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    for name, desat in (('FlowSwitch', False), ('FlowSwitch-paused', True)):
        images = [render(s, desaturate=desat) for s in sizes]
        path = os.path.join(ROOT, 'assets', 'icons', f'{name}.ico')
        images[-1].save(path, format='ICO', sizes=[(s, s) for s in sizes], append_images=images[:-1])
        print('wrote', path)
    for s in (16, 32, 64, 256, 1024):
        p = os.path.join(ROOT, 'assets', 'logo', f'flowswitch-{s}.png')
        render(s).save(p)
    render(1024, background=False).save(os.path.join(ROOT, 'assets', 'logo', 'flowswitch-mark.png'))
    sheet = Image.new('RGBA', (16 + 32 + 64 + 256 + 5 * 24, 256 + 48), (18, 20, 26, 255))
    x = 24
    for s in (16, 32, 64, 256):
        sheet.alpha_composite(render(s), (x, 24 + (256 - s) // 2))
        x += s + 24
    sheet.save(os.path.join(ROOT, 'assets', 'logo', 'flowswitch-sizes.png'))
    print('wrote logo previews')


if __name__ == '__main__':
    main()
