# FEAT-RENDER-01 — texture placement probe

A known-answer rig for the open half of FEAT-RENDER-01 ("texture misplaced").
Everything internal has been ruled out by measurement; what was missing is a test
object whose *correct* appearance is known in advance.

## Why this rig and not another screenshot of Dangazi Forest

`ObjectRenderer.LogFaceTextureParams` already prints what the sim told us to draw,
and its own doc comment names the reason that was never conclusive:

> the Firestorm half of that comparison is unavailable for no-modify content,
> which is most of what a real grid contains

A prim **we** rez is full-perm. So for the first time all three readings of the same
number are available at once:

| reading | source |
|---|---|
| what the script asked for | `llOwnerSay` from `uv_probe.lsl` |
| what SLNG received | `[FaceParams]` in `client-output.log` (logged on every object click) |
| what Firestorm received | the object's Texture tab |

That splits the search space in one click:

- LSL and `[FaceParams]` **disagree** → the TextureEntry decode in `SLNG.Net` is wrong.
- They **agree** but SLNG renders differently from Firestorm → the UV transform in
  `ObjectRenderer` / the material is wrong.
- All three agree and both viewers look the same → this object is not the bug.

## Files

- `gen_uv_probe.py` → `out/uvprobe_1024.png` (and `_512`). Run `python tools/testassets/gen_uv_probe.py`.
- `uv_probe.lsl` — the in-world driver.

The texture is built so a screenshot alone identifies the transform:

- **4x4 lettered cells** `A1`–`D4` (A = left column, 1 = top row) — say "C2 is where B2 should be".
- **Coloured frame** top=red, right=green, bottom=blue, left=yellow — a 90° rotation is
  legible from across the room.
- **Centre arrow** points at the top of the image — asymmetric, so it separates rotation
  from mirroring.
- **Orange diagonal** BL→TR — still visible when an offset pushes the arrow off the face.
- **u/v rulers** on the top and left edges. `v` is printed in SL's bottom-origin
  convention, i.e. the number you would type in the build floater.
- **2px checker patch** in `B4` — a truncated J2K stream flattens it to grey while the
  big shapes survive, so sharpness and placement stay separable.

## Setup

1. Upload `out/uvprobe_1024.png`. **Lossless off** — this is a diffuse map, not a sculpt map.
2. Rez a box, drop in the texture **and** `uv_probe.lsl`.
3. The script sizes the prim to 2×2×2, makes it full-bright untinted white, and applies case 0.

Touch = next case. On channel 42: `next`, `prev`, `<n>`, `list`, `reset`, `size 4`.

## Cases

| # | case | what it discriminates |
|---|---|---|
| 0 | identity | baseline — must be pixel-identical in both viewers |
| 1–3 | rot 90 / 180 / 270 | the documented π/2 finding (49 faces in Dangazi Forest) |
| 4 | rot 45 | separates *rotation ignored* from *rotation snapped to a right angle* |
| 5–6 | offset u / v +0.25 | sign and axis of the offset |
| 7–8 | repeat 2×1 / 1×2 | which axis repeat maps to |
| 9–10 | flip u / flip v | negative repeat handling |
| 11 | rot 90 + repeat 2×2 | **order of operations** — rotate-then-scale vs scale-then-rotate |
| 12 | rot 90 + offset u .25 | **rotation pivot** — about (0.5,0.5) or about the origin |
| 13 | rot 45 + repeat 2×2 | same two, without axis-aligned coincidences masking the error |
| 14–16 | planar texgen | see below |
| 17 | SPREAD | six rotations on six faces in one screenshot |

Cases 11–13 matter because an identity case and a pure-rotation case both look right
under several *wrong* transforms; only a combination separates them.

## Standing suspicion: PLANAR texgen is decoded but never applied

`f4d6643` carried SL's per-face TexGen through to the renderer, and it reaches
`FaceTexture.TexGen` — but the only code in the repo that reads it is the log line in
`ObjectRenderer.cs:325`, which prints `PLANAR (not implemented!)`. Nothing in the
material path branches on it.

Planar texgen projects UVs from the prim's local axes instead of using the per-face
default mapping, and it is what build tools reach for on tiled walls and floors. A face
using it would render with plainly wrong texture placement while the object's geometry,
repeat, offset and rotation are all correct — which is the reported symptom.

Cases 14–16 are the direct test. If they diverge from Firestorm while 0–13 match, the
"misplaced" half of FEAT-RENDER-01 is planar texgen, and it is an implementation gap
rather than a bug.
