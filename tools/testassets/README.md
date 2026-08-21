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
- `uv_probe.lsl` — the in-world driver for texture placement on a box.
- `gen_sculpt_probe.py` → `out/sculptprobe_16x256.png`, plus `sculpt_probe.lsl` — the same
  known-answer idea for SCULPT geometry. See "Sculpt probe" below.

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


---

## Sculpt probe

The texture-transform half of FEAT-RENDER-01 is closed. What is not closed is a stone on OSGrid
(object 38399801) whose texture sits differently in SLNG than in Firestorm: a cylinder-stitched
sculpt, **16 x 256** map, `repeat <2.25, 6>`, default texgen — so the planar fix does not apply.

That object is not ours, so there is no known answer to check against. This probe supplies one.

`gen_sculpt_probe.py` builds a sculpt map with the **same 16 x 256 shape** as the problem case —
deliberately, because a sculpt map's aspect ratio drives the vertex grid both viewers build, and
a square map would not exercise the same arithmetic. The encoded shape makes the two candidate
failures separable by eye:

- **8 stacked bands** of alternating radius → vertical drift in the sampling moves the band
  edges, and they can be counted.
- **A strongly asymmetric ("egg") cross-section**, fattest a quarter turn from the seam → if the
  sampling rotates row by row, the fat side winds into a helix instead of running straight up.
  It is a smooth bulge rather than a one-column marker on purpose: both viewers sample only
  about 7 of the map's 16 columns, so a narrow feature would fall between samples. Keeping it
  off the seam separates a twist from a seam-stitching artifact.

### Upload

| file | lossless? |
|---|---|
| `sculptprobe_16x256.png` | **YES** — it is geometry; every pixel is a vertex position and JPEG2000 ringing moves the surface |
| `uvprobe_1024.png` | no — ordinary diffuse map |

Rez any prim, drop in both textures and `sculpt_probe.lsl`. The script turns the prim into the
sculpt itself. Touch to step; `/43 list`, `/43 <n>`, `/43 size 2 2 4`.

Case 1 reproduces the real object exactly (`repeat <2.25, 6>`); the others bracket it so a
disagreement can be pinned to one axis. Case 5 (`1 x 24`) exists to amplify vertical error.

### What is known so far

SLNG builds a **9 x 129** vertex grid for this map where the viewer builds **8 x 128**, and the
two therefore read different columns of the map (ours 8/10/12/14, the viewer's 9/11/13). That
difference is measured and real. Whether it is what makes the stone look wrong is **not**
established — two earlier attempts to name a cause from reading viewer source were both wrong,
which is why this probe exists instead of a third guess.

## EEP parity probes (FEAT-ENV-01)

`eep-parity/` holds 18 sky and 7 water presets for the procedure in
[`docs/eep-parity-probe-protocol.md`](../../docs/eep-parity-probe-protocol.md).
Generated by `gen_eep_parity_presets.py` — edit the script, not the XML.

Each file is the viewer's own `app_settings/windlight/skies/Default.xml`
(respectively `water/Default.xml`) with **exactly one value changed**, at or near
that parameter's validator limit. The neutral baseline is therefore Linden's
default rather than anything we chose, which is the whole point: a difference in
one probe has one candidate cause.

They are **legacy Windlight XML**, not EEP LLSD, because that is what the
environment editor's `Import…` accepts —
`LLFloaterFixedEnvironment::doImportFromDisk` is commented "Load a legacy
Windlight XML from disk". Import, save under the file's own name, apply to the
region, screenshot both viewers from the same spot.

Three format traps, documented at length in the generator's docstring: scalars are
mostly 4-element arrays with the value in `[0]`; `cloud_scroll_rate` is stored
**offset by +10**; and `sun_angle` is the sun's altitude in radians, with the moon
placed diametrically opposite on import.

Probes 13, 17 and W7 have no file on purpose — `cloud_variance` and
`moon_brightness` have no legacy key at all, and W7 is W0 with the camera under the
surface.
