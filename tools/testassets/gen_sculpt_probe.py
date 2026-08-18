"""Generate a known-answer sculpt map for the FEAT-RENDER-01 placement hunt.

Deliberately 16 x 256 -- the same shape as the OSGrid stone (map 7ff4b74d) whose texture sits
differently in SLNG than in Firestorm. A sculpt map's aspect ratio drives the vertex grid both
viewers build, so reproducing it is the point; a square map would not exercise the same
arithmetic.

    python tools/testassets/gen_sculpt_probe.py

Writes tools/testassets/out/sculptprobe_16x256.png.

UPLOAD THIS ONE LOSSLESS. A sculpt map is geometry, not a picture: every pixel IS a vertex
position, so JPEG2000 ringing moves the surface. (The opposite of uvprobe, which is a diffuse
map and should NOT be lossless.)

The shape is chosen so the two failure modes are separable by eye:

  * 8 stacked bands of alternating radius. Vertical drift in the sampling moves the band edges,
    and they are countable.
  * A strongly asymmetric ("egg") cross-section, fattest a quarter turn away from the seam.
    If the sampling rotates row by row, the fat side winds into a helix instead of running
    straight up. It is deliberately a smooth bulge rather than a one-column marker: both viewers
    sample only about 7 of the map's 16 columns, and a narrow feature would simply fall between
    samples. Keeping it off the seam separates a twist from a seam-stitching artifact.
"""
import math
import os
from PIL import Image

W, H = 16, 256
BANDS = 8
R_THICK, R_THIN = 0.50, 0.34
EGG_AXIS = 1.6                       # radians: where the cross-section is fattest
EGG_DEPTH = 0.45                     # 0 = round, ->0.65 = increasingly teardrop


def build():
    img = Image.new("RGB", (W, H))
    px = img.load()

    for y in range(H):
        z = y / (H - 1) - 0.5                       # -0.5 .. +0.5 along the axis
        band = int(y / H * BANDS)
        r_base = R_THICK if band % 2 == 0 else R_THIN

        for x in range(W):
            theta = x / W * 2.0 * math.pi           # wraps: column W is column 0
            r = r_base * (0.65 + EGG_DEPTH * math.cos(theta - EGG_AXIS))

            X = r * math.cos(theta)
            Y = r * math.sin(theta)

            # sculpt map encoding: each channel 0..255 maps linearly to -0.5 .. +0.5
            px[x, y] = (int(round((X + 0.5) * 255)),
                        int(round((Y + 0.5) * 255)),
                        int(round((z + 0.5) * 255)))
    return img


if __name__ == "__main__":
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "out")
    os.makedirs(out, exist_ok=True)
    p = os.path.join(out, f"sculptprobe_{W}x{H}.png")
    build().save(p)
    print(f"wrote {p}  ({W}x{H}, {BANDS} bands, egg cross-section)")
