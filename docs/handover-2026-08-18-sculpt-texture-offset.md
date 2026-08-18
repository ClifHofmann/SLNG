# Handover — sculpt texture placement (FEAT-RENDER-01), 2026-08-18

**Branch:** `feature/FEAT-RENDER-01-planar-texgen` (13 commits over `main`, 12 of them unpushed —
origin still has only `3856ed5`)
**Version:** `v0.3.116-alpha`
**Build/tests:** clean, 153 green, `dotnet format` clean for everything touched
**Working tree:** clean

---

## 1. Where this stands

Two real rendering defects were found and fixed. A third is **still open**: sculpt textures sit at
a small constant offset from where Firestorm puts them, on **both** axes.

The open part is small (roughly 2% of a texture) but it is real, reproducible, and measured — not
an impression.

---

## 2. Fixed and confirmed

### Planar TexGen was never implemented (`3856ed5`)

SL's `TEX_GEN_PLANAR` projects UVs from the prim's local axes instead of using the face's default
mapping. It is what build tools use for tiled walls and floors. We decoded the flag and then threw
it away — the only code reading it was a log line.

Three defects were stacked here:

1. The projection did not exist.
2. The diagnostic could not report it: `FaceTexture.TexGen` holds SL's raw enum where planar is
   **2**, while the doc comment and `ObjectRenderer` both compared against **1**. So the
   "PLANAR (not implemented!)" line printed "default" for every planar face there has ever been.
3. Prims sending no per-face entries lost texgen entirely — `ObjectUpdateEvent` and
   `PrimitiveComponent` had no field for the default face.

`slng_planar_uv` now ports `planarProjection` (llface.cpp:88-120) into the vertex stage.
Live-verified against Firestorm on a known-answer probe: matches on tile count **and** phase.

Spherical (4) and cylindrical (6) need nothing — they appear in the entire viewer source only in
the enum declaration, so the real viewer ignores them too. Our fallback to the mesh UVs is parity.

### Sculpt textures were vertically mirrored (`bf564b8`)

`AssignSharedMesh(..., flipV: true)` for sculpts was wrong. The comment beside it admitted it
"leans on the SL-convention default rather than hard proof".

Settled by A/B against Firestorm, not by argument:

| | result |
|---|---|
| `flipV: true` | red/blue frame edges swapped, letters upside down — a full mirror |
| `flipV: false` | orientation correct, small constant offset remains |

Confirmed independently on the statue in Dangazi Forest and on the sculpt probe.

**Note the arithmetic still argues for `true` and is therefore wrong somewhere.** Our mesh UVs
provably equal the viewer's `tt` exactly, and SL is usually described as sampling bottom-origin
against Godot's top-origin, which would demand `1 - tt`. Measurement says otherwise, twice.
Whatever reconciles that is probably the same thing causing the leftover offset.

### FEAT-NET-03 closed (merged to `main`)

LibreMetaverse 3.0.0 → 3.1.3. The one criterion headless could not reach — texture fetch over
UDP/HTTP CAPS and the appearance path — was exercised by a real OSGrid session during this work.

---

## 3. Proven correct — do not re-investigate

`tests/SLNG.Assets.Tests/ViewerSculptParityTests.cs` compares our sculpt mesher against a C# port
of `LLVolume::sculpt` / `sculpt_calc_mesh_resolution` / `sculptGenerateMapVertices`, run on **real
cached sculpt maps**. Three separate checks, all at **0.00000** deviation across five maps
(sphere, plane and cylinder stitching; square and 16x256):

| check | what it pins |
|---|---|
| `OurSculptMesh_MatchesViewerAlgorithm` | vertex positions (as a set) |
| `OurSculptUVs_MatchViewerAlgorithm` | texture coordinates, index by index |
| `OurSculptVertexOrder_MatchesViewerAlgorithm` | the PAIRING between the two |

The third exists because the first two both pass even if our vertex order differed — every
position would still exist and every UV value still occur, just attached to a different point.
That renders as "geometry right, coordinates right, texture in the wrong place", which is exactly
the open symptom, so it had to be excluded explicitly.

The maps are grid content and cannot be checked in; the tests skip when the local asset cache
(`%APPDATA%/Godot/app_userdata/Puris Viewer/cache/assets`) lacks them. **Re-run locally after a
session in Dangazi Forest to get real coverage.**

---

## 4. The open problem

Sculpt textures sit at a constant offset from Firestorm's placement.

- **V:** approximately **+3 grid rows** (`0.0234` in texture units) dialled in on the probe.
- **U:** unmeasured. The real stone reads as shifted to the right; the nudge control for it was
  added but never used.

Properties established by measurement:

| varied | offset changes? |
|---|---|
| repeat (1x1 vs 1x6) | **no** — same value fits both |
| camera zoom / distance | **no** |
| texture upload resolution | **no** — sharpening reaches full resolution before measuring |

So it is a fixed offset applied at the **end** of the chain, after the repeat multiplication.

The awkward part: in the very case measured (`repeat 1`, `offset 0`) the shader's UV transform
reduces to the identity — `(v - 0.5) * 1 + 0.5 = v` — and the mesh V provably equals the viewer's
`tt`. So on paper there is nothing left to produce an offset, and yet there is one.

---

## 5. Tools built for this (use them)

### `tools/testassets/` — known-answer probes

The reason these exist: real grid content is mostly no-modify, so Firestorm's Texture tab is
unreadable and "what the sim sent" can never be checked against "what the other viewer drew". A
prim we rez makes all three readings available at once — what the script asked for
(`llOwnerSay`), what SLNG received (`[FaceParams]`, logged on every object click), and what
Firestorm received (its build floater).

- `gen_uv_probe.py` + `uv_probe.lsl` — texture placement on a box. 18 cases covering rotation,
  offset, repeat, flips, combinations and planar texgen. The texture is a labelled 4x4 grid with a
  coloured frame, rulers and an asymmetric centre marker, so a screenshot alone identifies the
  transform.
- `gen_sculpt_probe.py` + `sculpt_probe.lsl` — the same idea for sculpt geometry. The map is
  deliberately **16x256**, the same shape as the problem stone. 8 stacked bands make vertical drift
  countable; an asymmetric cross-section a quarter turn off the seam turns any twist into a visible
  helix. `/43 plane` and `/43 cylinder` switch stitching in place; case 6 reproduces the floor's
  `repeat 20x20`.

**Upload note:** the sculpt map must be uploaded **lossless** (it is geometry). The UV probe must
**not** be.

### Developer menu — live UV nudge

`Entwickler -> Sculpt-U/V verschieben`, plus/minus 1 and 16 grid steps, plus reset. Writes one
shader uniform per surface — no rebuild, no re-decode. Applied to **sculpt faces only**, so prims
stay a valid reference. Reports the value in textures, grid rows and map rows, because which unit
it lands on is itself the finding.

### `[FaceParams]` logging

Every object click dumps the per-face texture placement in the same terms Firestorm's build
floater shows. This is how the two Dangazi objects were finally identified correctly — see the
trap in section 6.

---

## 6. Ruled out — and how the search went wrong

**Six hypotheses were advanced and all six were wrong.** Every one came from reading viewer source
or comparing screenshots; every conclusion that held came from a measurement. Read this section
before forming a seventh.

| hypothesis | why it was wrong |
|---|---|
| Sculpt grid is a fixed 32x32 square while the viewer distributes by map aspect ratio | `mesherLod` is a pixel budget, not an edge length; `SculptMap` already preserves the ratio |
| `ToRows` walks the buffer at the wrong stride | `width++/height++` sits directly below the fill loop; the file was read up to the line before it |
| Our grid is 9x129 where the viewer builds 8x128 | `sculpt_calc_mesh_resolution` returns the REQUESTED size; `genNGon` emits sides+1. `ViewerSculptParityTests` already encoded this correctly |
| LOD mismatch (we always build Highest, Firestorm varies by distance) | measured: the required nudge does not change with zoom |
| Textures never refined past their first upload | `[GpuUpload]` covers first uploads only; the sharpening path was silent. It works — one texture went 1353 -> 2,250,318 px2 and ended at full resolution |
| Coarse upload resolution making a half-texel look like ~2 grid rows | same measurement — full resolution is reached before the comparison |

One more trap worth naming: for several rounds the numbers being reasoned from came from
**whatever object had last been clicked**, not from the object that looked wrong. The stone floor
turned out to be a *plane*-stitched sculpt at `repeat 20x20`; the object supplying the numbers was
a *cylinder* at `repeat 2.25x6`. Always confirm the `[FaceParams]` block belongs to the object in
question.

---

## 7. Next steps

1. **Measure U.** Reset, then dial U and V separately on the probe at `/43 0` and record both.
   - U ~ V ~ 3 -> something displaces the texture as a whole; a suspect class not yet examined.
   - U ~ 0, V ~ 3 -> genuinely vertical only.
   - U and V different but both non-zero -> likely tied to the grid's shape (129 rows vs 9 columns
     on this map), which would be very informative since the axes are so unevenly resolved.
2. **Check whether the same two values straighten the real stone.** If yes, the probe is a full
   stand-in from then on and nobody needs to walk the region again.
3. Only then look for the cause. Note the offset is applied after the repeat, so it belongs in the
   fragment/vertex output or in texture sampling, not in the source coordinate.

### Separate, independent of the above

- **Sculpts ignore LOD entirely.** `GetSculptMeshAsync(sculptId, sculptType)` takes no detail
  level, so every sculpt is built at Highest regardless of distance. Not the cause of this bug
  (measured), but wrong and a performance issue. Belongs with FEAT-PERF-02.
- **`isVolumeGlobal` is a Phase 3 precondition.** `slng_planar_uv` multiplies by `prim_scale`
  unconditionally; the viewer uses `(1,1,1)` for rigged volumes and flexi prims. Neither reaches
  this code today (no flexi at all, rigged goes through `AvatarRenderer`), but it breaks the moment
  `AvatarRenderer` joins the shader family.
- **`AvatarRenderer` still uses `StandardMaterial3D`** with the old rotation approximation. It gets
  neither planar texgen nor the corrected rotation. Phase 3.
- **Texture `6d9be86d`** (one of the Dangazi objects) is truncated to 8% of its declared size on
  OSGrid, confirmed byte-for-byte against Firestorm's own cache index. Both viewers receive the
  same damaged bytes. Nothing to fix client-side — do not chase it again.

---

## 8. Git

```
main                                     37efa77   in sync with origin/main
feature/FEAT-RENDER-01-planar-texgen     4e205d6   13 commits over main
origin/feature/FEAT-RENDER-01-planar-*   3856ed5   12 commits behind local
```

To publish the rest:

```
git push origin feature/FEAT-RENDER-01-planar-texgen
```
