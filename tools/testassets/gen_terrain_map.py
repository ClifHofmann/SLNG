#!/usr/bin/env python3
"""Generate a known-answer TERRAIN heightmap for the FEAT-RENDER-02 band comparison.

The texture probes (gen_terrain_probe.py) made the four detail slots tellable apart.
They could not make the BANDS tellable apart, because Howletts' natural terrain sits
in a two-metre spread where the Perlin term alone decides every pixel -- so every
comparison was an argument about mottling.

This inverts that. Five large FLAT terraces, each placed so its composition value
lands dead on one band, with a height range wide enough that the noise becomes a thin
fringe instead of the whole signal:

    terrace   height   composition   expected
    south      25 m      0.08 +-0.15  solid RED
               50 m      0.50 +-0.15  mottled RED/GREEN  <- the noise field, exposed
               80 m      1.00 +-0.15  solid GREEN
              140 m      2.00 +-0.15  solid BLUE
    north     200 m      3.00 +-0.15  solid MAGENTA

The 50 m terrace is the instrument. Sitting exactly on the red/green crossfade
midpoint, every pixel of it is decided by the Perlin term and nothing else -- the
height is the same everywhere, so it contributes a constant. Two viewers must paint
that terrace with the SAME mottling, and phase, scale and amplitude each fail that
test in their own recognisable way.

The other four are the control, one per band. They sit far enough from any boundary
that the noise cannot flip them, so each must be SOLID, and a wrong band there is
unmissable rather than arguable -- which is the whole thing Howletts' natural terrain
could not give us.

Height range 240 is what buys that separation. The viewer's noise swings the effective
height by roughly +-9 m regardless of settings, so a band has to be much wider than 18 m
before "solid" means anything -- at Howletts' default range of 60 a band is 15 m wide
and everything is mottled by construction.

    python tools/testassets/gen_terrain_map.py

Writes out/terrain_bands.raw: Linden RAW, the format the viewer's estate tools upload.
13 bytes per cell, height = red * green / 128, file row 0 is NORTH -- read out of
OpenSim's own loader (LLRAW.cs LoadStream/SaveStream), not guessed. The other 11 bytes
are the constants OpenSim itself writes on export.
"""

import os
import struct

REGION = 256          # cells per side; LLRAW infers the dimension from the file length
WATER = 20.0          # Howletts' waterline, for the "is it visible" check below

# (name, height in metres). South to north, in the order they are laid out.
TERRACES = [
    ("band 1",    25.0),
    ("crossfade", 50.0),
    ("band 2",    80.0),
    ("band 3",   140.0),
    ("band 4",   200.0),
]

# The estate settings this map is designed against. Both halves have to match or the
# terraces land nowhere in particular -- the map alone is not the known answer, the map
# WITH these settings is.
# start = the waterline, so terrace 1 sits just above composition 0 and band 1 gets a
# solid control of its own -- with start = 0 the lowest band is only reachable below
# water, where nothing can be compared.
START_HEIGHT = 20.0
HEIGHT_RANGE = 240.0


def encode(height):
    """Closest (red, green) with red * green / 128 == height.

    OpenSim builds the same table and binary-searches it (LLRAW.BuildLookupHeightTable);
    exhaustive search is instant at this size and avoids reproducing its sort order.
    Exact for any integer height via green = 128, and for anything else it reports the
    error so a silently-rounded terrace cannot masquerade as a placed one.
    """
    best = (0, 0)
    best_err = float("inf")
    for green in range(256):
        if green == 0:
            continue
        # red that best hits the target for this green
        red = round(height * 128.0 / green)
        if not 0 <= red <= 255:
            continue
        err = abs(red * green / 128.0 - height)
        if err < best_err:
            best_err, best = err, (red, green)
            if err == 0.0:
                return best, 0.0
    return best, best_err


def build_heights():
    """heights[y][x] in metres, y = 0 at the SOUTH edge (SL's own convention)."""
    heights = []
    for y in range(REGION):
        # Proportional split rather than a fixed depth, so five terraces divide 256
        # evenly-ish instead of leaving a remainder strip at the north edge.
        terrace = min(y * len(TERRACES) // REGION, len(TERRACES) - 1)
        heights.append([TERRACES[terrace][1]] * REGION)
    return heights


def main():
    out_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "out")
    os.makedirs(out_dir, exist_ok=True)
    path = os.path.join(out_dir, "terrain_bands.raw")

    heights = build_heights()

    # Encode once per distinct height rather than per cell, and report the error so a
    # terrace that could not be represented exactly says so instead of drifting quietly.
    print("terrace heights:")
    encoded = {}
    for name, h in TERRACES:
        (red, green), err = encode(h)
        encoded[h] = (red, green)
        comp = (h - START_HEIGHT) * 4.0 / HEIGHT_RANGE
        note = "" if err == 0.0 else f"  !! not exactly representable, off by {err:.4f} m"
        print(f"  {name:>10}  {h:6.1f} m  -> red={red:3d} green={green:3d}"
              f"  composition {comp:.2f}{note}")

    with open(path, "wb") as fh:
        # File row 0 is NORTH: LLRAW.LoadStream writes into [x, (Height-1) - y] as it
        # reads rows in order, so the first row read becomes the highest y index.
        for row in range(REGION):
            y = REGION - 1 - row
            out = bytearray()
            for x in range(REGION):
                red, green = encoded[heights[y][x]]
                # The remaining 11 bytes are exactly what OpenSim's SaveStream emits:
                # blue = 20, four zero alphas, four 255 alphas, then red and green again.
                out += struct.pack(
                    "13B", red, green, 20, 0, 0, 0, 0, 255, 255, 255, 255, red, green
                )
            fh.write(out)

    size = os.path.getsize(path)
    print()
    print(f"wrote {path}  ({size:,} bytes = {REGION}x{REGION} x 13)")
    print()
    print("Estate tools -> Region/Estate -> Terrain:")
    print(f"  1. Upload RAW  ->  {os.path.basename(path)}")
    print(f"  2. Set ALL FOUR corners: low = {START_HEIGHT:g}, high = {HEIGHT_RANGE:g}")
    print("  3. Keep the four terrain_probe_N textures in slots 1-4, low to high")
    print()
    print("Then, south to north:")
    for (name, h), _ in zip(TERRACES, TERRACES):
        comp = (h - START_HEIGHT) * 4.0 / HEIGHT_RANGE
        if abs(comp - round(comp)) > 0.25:
            expect = "mottled between the two neighbouring textures"
        else:
            expect = f"SOLID texture {int(round(comp)) + 1}"
        under = "  (below the waterline!)" if h < WATER else ""
        print(f"  {h:6.1f} m  composition {comp:.2f}  {expect}{under}")
    print()
    print("The crossfade terrace is the measurement: at composition 0.50 every pixel is")
    print("decided by the Perlin term alone, so both viewers must show the same mottling.")
    print("The other four are the control -- one per band, and each must be solid in both.")


if __name__ == "__main__":
    main()
