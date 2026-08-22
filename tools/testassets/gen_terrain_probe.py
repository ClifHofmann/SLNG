#!/usr/bin/env python3
"""
Generate four known-answer terrain detail textures for FEAT-RENDER-02.

Why these and not another screenshot of grass
---------------------------------------------
Comparing SLNG's terrain against Firestorm on a region's real textures mixes four
things into one image: where the band boundaries fall, what the textures look like,
how they are filtered, and what colour space they are blended in. Every one of those
has already been wrong at some point in this task, and a grass-on-grass comparison
cannot separate them.

These textures remove three of the four. Each band gets one unmistakable hue, so the
only thing left to compare is *where the boundaries are*.

They also carry a checkerboard and a tile border, which gives two more readings for
free:
  * count the checks across a known distance -> the detail UV scale (should be one
    full texture repeat per 12 m, the viewer's RenderTerrainScale default)
  * compare how crisp the checks look between the two viewers -> texture filtering,
    which is how the missing anisotropic filtering was caught

Colours are chosen so that no *blend* of two neighbouring bands can be mistaken for a
third band: red/green mix to olive, green/blue to teal, blue/magenta to violet, and
none of those collide with a pure band colour. Bands only ever blend with their
immediate neighbour, so that is sufficient.

Usage
-----
    python gen_terrain_probe.py

Writes out/terrain_probe_{1..4}.png. Upload all four and set them as the region's
terrain textures (Estate tools -> Region/Estate -> Terrain), in order:

    texture 1 (LOW)  = terrain_probe_1.png   red
    texture 2        = terrain_probe_2.png   green
    texture 3        = terrain_probe_3.png   blue
    texture 4 (HIGH) = terrain_probe_4.png   magenta

Then take the same A/B shot in both viewers.

What to expect on Howletts
--------------------------
With the region's current settings (start height 10, height range 60, terrain topping
out at 25 m) the composition value only spans roughly 0.7 to 1.6, so you will see the
red/green transition and the start of green/blue. **Magenta will not appear at all** --
that is correct, not a bug.

To exercise all four bands, temporarily narrow the elevation range in the estate
terrain tab (e.g. low 18 / high 28). Note that the Perlin term swings the effective
height by roughly +/-9 m, so with a narrow range it dominates and the bands become
mottled rather than layered -- still a valid comparison, just a noisier-looking one.
"""

import os

try:
    from PIL import Image, ImageDraw
except ImportError:
    raise SystemExit("Pillow is required:  pip install pillow")

SIZE = 256          # power of two, and a normal SL texture size
CHECK = 32          # checker cell in pixels -> 8 across the tile, ~1.5 m at a 12 m repeat
BORDER = 3          # tile-edge frame, so repeats can be counted
MARKER = 22         # corner orientation marker

# (name, base colour, ink colour for the digit and frame)
BANDS = [
    ("1", (220, 30, 30), (255, 255, 255)),      # low
    ("2", (30, 200, 60), (0, 0, 0)),
    ("3", (40, 90, 230), (255, 255, 255)),
    ("4", (230, 40, 220), (0, 0, 0)),           # high
]

# Seven-segment digit shapes, drawn from rectangles so this needs no font file and
# renders identically on any machine.
SEGMENTS = {
    "1": "bc",
    "2": "abged",
    "3": "abgcd",
    "4": "fgbc",
}


def darken(rgb, factor=0.72):
    return tuple(int(c * factor) for c in rgb)


def draw_digit(draw, char, box, colour):
    """Draw a seven-segment digit inside box = (x0, y0, x1, y1)."""
    x0, y0, x1, y1 = box
    w = x1 - x0
    h = y1 - y0
    t = max(4, w // 6)          # segment thickness
    mid = y0 + h // 2

    rects = {
        "a": (x0, y0, x1, y0 + t),
        "b": (x1 - t, y0, x1, mid),
        "c": (x1 - t, mid, x1, y1),
        "d": (x0, y1 - t, x1, y1),
        "e": (x0, mid, x0 + t, y1),
        "f": (x0, y0, x0 + t, mid),
        "g": (x0, mid - t // 2, x1, mid + t // 2),
    }
    for seg in SEGMENTS[char]:
        draw.rectangle(rects[seg], fill=colour)


def build(char, base, ink):
    img = Image.new("RGB", (SIZE, SIZE), base)
    draw = ImageDraw.Draw(img)

    # Checkerboard. Low contrast against the base so the band's hue stays obvious from
    # a distance, while the checks are still resolvable close up for the filtering read.
    shade = darken(base)
    for cy in range(0, SIZE, CHECK):
        for cx in range(0, SIZE, CHECK):
            if ((cx // CHECK) + (cy // CHECK)) % 2 == 0:
                draw.rectangle((cx, cy, cx + CHECK - 1, cy + CHECK - 1), fill=shade)

    # Tile frame — makes one repeat countable on the ground.
    for i in range(BORDER):
        draw.rectangle((i, i, SIZE - 1 - i, SIZE - 1 - i), outline=ink)

    # Orientation marker: a filled corner triangle in the TOP-LEFT of the image. If the
    # terrain UVs are flipped or transposed relative to the viewer, this lands somewhere
    # else and says so immediately.
    draw.polygon(
        [(BORDER, BORDER), (BORDER + MARKER, BORDER), (BORDER, BORDER + MARKER)],
        fill=ink,
    )

    # Band number, large and centred.
    m = SIZE // 4
    draw_digit(draw, char, (m, m, SIZE - m, SIZE - m), ink)

    return img


def main():
    out_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "out")
    os.makedirs(out_dir, exist_ok=True)

    for char, base, ink in BANDS:
        img = build(char, base, ink)
        path = os.path.join(out_dir, f"terrain_probe_{char}.png")
        img.save(path, format="PNG", optimize=True)
        print(f"wrote {path}  base={base}")

    print()
    print("Upload all four and set them as the region's terrain textures, low to high:")
    for char, base, _ in BANDS:
        print(f"  texture {char} = terrain_probe_{char}.png")


if __name__ == "__main__":
    main()
