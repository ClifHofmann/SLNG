"""Generate the FEAT-RENDER-01 UV placement probe texture.

A 1024x1024 target where every pixel's position in UV space is readable from a
screenshot, so a misplaced texture can be described exactly ("cell C2 is where
A1 should be") instead of "sieht falsch aus".

    python tools/testassets/gen_uv_probe.py

Writes tools/testassets/out/uvprobe_1024.png (and _512).
Upload to OpenSim with lossless OFF (this is a diffuse map, not a sculpt map).
"""
import os
from PIL import Image, ImageDraw, ImageFont

SIZE = 1024
BORDER = 14
GRID = 4                      # 4x4 lettered cells
COLS = "ABCD"

# edge colours -- a 90 deg rotation is legible from the frame alone, at any distance
EDGE_TOP    = (228,  40,  40)   # red
EDGE_RIGHT  = ( 40, 200,  70)   # green
EDGE_BOTTOM = ( 50, 110, 245)   # blue
EDGE_LEFT   = (245, 205,  40)   # yellow

CELL_A = (238, 238, 238)
CELL_B = (176, 176, 176)
INK    = ( 16,  16,  16)


def font(px):
    for path in (r"C:\Windows\Fonts\arialbd.ttf",
                 r"C:\Windows\Fonts\arial.ttf",
                 "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"):
        if os.path.exists(path):
            return ImageFont.truetype(path, px)
    return ImageFont.load_default(px)


def centred(d, xy, text, f, fill):
    x, y = xy
    l, t, r, b = d.textbbox((0, 0), text, font=f)
    d.text((x - (r - l) / 2 - l, y - (b - t) / 2 - t), text, font=f, fill=fill)


def build(size=SIZE):
    img = Image.new("RGB", (size, size), (255, 255, 255))
    d = ImageDraw.Draw(img)
    s = size / SIZE                        # scale factor for the 512 variant
    border = max(4, int(BORDER * s))
    inner = size - 2 * border
    cell = inner / GRID

    # corner cells get a pastel tint + a TL/TR/BL/BR word, so orientation is
    # readable even when repeat/offset pushes most of the texture off the face
    TINT = {(0, 0): ((252, 214, 214), "TL"), (3, 0): ((208, 246, 216), "TR"),
            (0, 3): ((252, 242, 200), "BL"), (3, 3): ((211, 226, 252), "BR")}

    # --- 4x4 checkered cells, labelled A1..D4 (A=left column, 1=top row).
    #     Labels sit in the cell's top-left so the centre stays free. ---
    f_cell = font(int(96 * s))
    f_sub = font(int(40 * s))
    labels = []
    for cx in range(GRID):
        for cy in range(GRID):
            x0 = border + cx * cell
            y0 = border + cy * cell
            tint = TINT.get((cx, cy))
            bg = tint[0] if tint else (CELL_A if (cx + cy) % 2 == 0 else CELL_B)
            d.rectangle([x0, y0, x0 + cell, y0 + cell], fill=bg)
            labels.append((cx, cy, x0, y0, tint[1] if tint else None))

    # cell gridlines
    for i in range(GRID + 1):
        p = border + i * cell
        w = max(1, int(3 * s))
        d.line([p, border, p, border + inner], fill=(90, 90, 90), width=w)
        d.line([border, p, border + inner, p], fill=(90, 90, 90), width=w)

    # --- diagonal: bottom-left -> top-right. Reveals mirroring even when the
    #     centre arrow has been pushed off the face by an offset. ---
    d.line([border, size - border, size - border, border],
           fill=(255, 130, 0), width=max(2, int(6 * s)))

    # --- asymmetric centre marker: an arrow pointing at the top of the image ---
    c = size / 2
    tip, half, stem = 190 * s, 118 * s, 42 * s
    d.polygon([(c, c - tip),
               (c + half, c - tip + half),
               (c + stem, c - tip + half),
               (c + stem, c + tip),
               (c - stem, c + tip),
               (c - stem, c - tip + half),
               (c - half, c - tip + half)],
              fill=(200, 30, 160), outline=INK, width=max(1, int(4 * s)))
    centred(d, (c, c + tip * 0.6), "UP", font(int(56 * s)), (255, 255, 255))

    # --- sharpness patch: 2px checker. A truncated J2K stream flattens it to
    #     grey while leaving the big shapes intact. ---
    n = int(cell * 0.30)
    px0 = border + 1.62 * cell
    py0 = border + 3.60 * cell
    for y in range(n):
        for x in range(n):
            if ((x // 2) + (y // 2)) % 2 == 0:
                d.point((px0 + x, py0 + y), fill=(0, 0, 0))
    d.rectangle([px0 - 3, py0 - 3, px0 + n + 3, py0 + n + 3],
                outline=(200, 30, 160), width=max(1, int(3 * s)))

    # --- U/V rulers in a translucent gutter over the top and left edges.
    #     V is printed in SL convention (bottom-origin), so the number next to a
    #     row is the v you would type in the build floater. ---
    gut = int(62 * s)
    band = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    bd = ImageDraw.Draw(band)
    bd.rectangle([border, border, size - border, border + gut], fill=(255, 255, 255, 205))
    bd.rectangle([border, border, border + gut, size - border], fill=(255, 255, 255, 205))
    img.paste(Image.alpha_composite(img.convert("RGBA"), band).convert("RGB"), (0, 0))
    d = ImageDraw.Draw(img)

    f_tick = font(int(30 * s))
    for i in range(0, 21):
        t = i / 20.0
        x = border + t * inner
        y = border + t * inner
        major = (i % 5 == 0)
        h = int((30 if major else 14) * s)
        w = max(1, int((3 if major else 2) * s))
        d.line([x, border, x, border + h], fill=INK, width=w)
        d.line([border, y, border + h, y], fill=INK, width=w)
        if major and 0 < i < 20:
            centred(d, (x, border + gut - 16 * s), f"{t:.2f}"[1:], f_tick, INK)
            centred(d, (border + gut - 20 * s, y), f"{1 - t:.2f}"[1:], f_tick, INK)
    centred(d, (border + gut + 42 * s, border + gut * 0.45), "u >", f_tick, (150, 0, 0))
    centred(d, (border + gut * 0.5, border + gut + 40 * s), "v", f_tick, (150, 0, 0))

    # --- cell labels last, so the ruler gutter never washes them out ---
    for cx, cy, x0, y0, sub in labels:
        lx = x0 + (gut + 10 * s if cx == 0 else 12 * s)
        ly = y0 + (gut + 4 * s if cy == 0 else 6 * s)
        d.text((lx, ly), f"{COLS[cx]}{cy + 1}", font=f_cell, fill=INK)
        if sub:
            d.text((lx + 4 * s, ly + 106 * s), sub, font=f_sub, fill=(70, 70, 70))

    # --- coloured frame, drawn last so nothing overlaps it ---
    d.rectangle([0, 0, size - 1, border], fill=EDGE_TOP)
    d.rectangle([0, size - 1 - border, size - 1, size - 1], fill=EDGE_BOTTOM)
    d.rectangle([0, 0, border, size - 1], fill=EDGE_LEFT)
    d.rectangle([size - 1 - border, 0, size - 1, size - 1], fill=EDGE_RIGHT)
    return img


if __name__ == "__main__":
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "out")
    os.makedirs(out, exist_ok=True)
    for sz in (1024, 512):
        p = os.path.join(out, f"uvprobe_{sz}.png")
        build(sz).save(p)
        print(f"wrote {p}")
