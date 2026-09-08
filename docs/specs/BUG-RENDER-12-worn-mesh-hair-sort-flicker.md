# [BUG-RENDER-12] Worn-mesh hair flips/pops as the camera turns

- **Feature ID:** `BUG-RENDER-12`
- **Track:** `render`
- **Status:** `✅ Done` — confirmed in-world at `v0.21.21`: *"kein Flackern und die Haare sehen gut aus"*
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Symptom

Mesh **hair** on worn attachments switches back and forth — strands/cards pop in front of
and behind each other — **as the camera rotates**. Stable while the camera is still.
Reported live on SL/Agni for **every avatar in the scene**, not one item. Firestorm is
stable on the same content.

Split out of [BUG-RENDER-11](BUG-RENDER-11-alpha-depth-write-parity.md), which fixed the
identical failure for world prims (grass/foliage) and left the avatar-side half open.

## Root cause

**The hair asset, decoded straight out of the client's own cache** (`b9af3b5f` dark /
`fc9c89c1` light — the same 256×256 texture in two tints, identified visually, not
guessed):

| | fully clear (α≤16) | graded | fully solid (α≥239) | α ≥ 253 |
|---|---|---|---|---|
| hair | 0.204 | 0.347 | 0.449 | 0.397 |

**Hair is not "mostly hole"** — it is a largely solid card with a wide soft strand halo.

That hair sits on **three rigged meshes of six faces each** (`7bda45d9`, `b60ba130`,
`94fb8260`; plus `6d07686e` for the dark variant) — **18 overlapping alpha draw calls
inside one head-sized volume**. All of them classified `Kind.Blend`, i.e. Godot's
**sorted transparent queue**, which orders by **AABB-centre distance**. Three hair pieces
sharing a head have near-identical centre distances, so that order is unstable and
reshuffles on the smallest camera move — strands jump back and forth while turning, stable
while still, on every avatar.

### Why the reference viewer does not pop, although it also blends hair

`LLFace::canRenderAsMask()` → `LLImageGL::analyzeAlphaData()` deliberately keeps
high-frequency alpha *out* of the mask pass, so the viewer really does blend hair. It
gets away with it because of two things Godot does not have:

`scratch/slviewer/indra/newview/lldrawpoolalpha.cpp:203-248`

```cpp
// first pass, render rigged objects only and render to depth buffer
forwardRender(true);
// second pass, regular forward alpha rendering
forwardRender();
...
bool write_depth = rigged || ...;
LLGLDepthTest depth(GL_TRUE, write_depth ? GL_TRUE : GL_FALSE);
```

1. **Rigged alpha gets its own, earlier pass with depth WRITE enabled** — unlike unrigged
   alpha, which writes no depth.
2. That is only safe because the pool draws its alpha **back-to-front at draw-info
   granularity**, so nothing is ever depth-rejected; the write exists to occlude what
   comes later.

### Why the existing mitigations do not close the gap

- **`depth_prepass_alpha`** (on `prim_blend_avatar`, added for BUG-RENDER-09) writes depth
  only where `ALPHA >= 0.99` — measured, that is **39.7 %** of this hair card. So the solid
  cores are already stable and the whole reshuffle lives in the **34.7 % soft halo**, which
  no prepass cutoff can reach.
- **`depth_draw_always`** was tried in 2026-07 and collapses the layered card stack into a
  flat silhouette ("looks like it was coloured in with a marker"). The viewer can pair a
  depth write with blending *only* because it draws strictly back-to-front first.

## Fix — batch worn faces in authored order

**This is what shipped.** Everything before it — five attempts recorded in BUG-RENDER-11 and five
more inside this bug — is history, kept at the bottom of this file. Reading the viewer's actual
sorting code settled what the fix should be, and it is not a shading change at all.

### What the viewer does

**It does not sort worn mesh alpha.** `llvovolume.cpp:6332-6335`, with Linden's own comment:

```cpp
if (rigged)
{
    if (!distance_sort) // <--- alpha "sort" rigged faces by maintaining original draw order
        std::sort(faces, faces + face_count, CompareBatchBreakerRigged());
}
else if (!distance_sort) { std::sort(faces, ..., CompareBatchBreaker()); }
else                     { std::sort(faces, ..., LLFace::CompareDistanceGreater()); }
```

`alpha_sort` is unconditionally `true` (`:6146`), so for **rigged** geometry that branch sorts
nothing: worn faces are batched in the order the creator authored them. Only **unrigged** alpha is
distance-sorted — and even that re-sorts only once the view angle has moved more than `0.64`
(`llspatialpartition.cpp:667-674`, the `ALPHA_DIRTY` hysteresis).

The layering is then resolved by **depth**, because the rigged alpha pass writes it
(`lldrawpoolalpha.cpp:240-248`, `write_depth = rigged || ...`).

Both mechanisms are **view-independent**. That is the whole reason the reference viewer never pops,
and why it can afford real alpha blending — soft hair — without a cutout.

### The port

1. **Authored order survives as one draw call.** `BuildRiggedMeshInstance` (and the non-rigged
   attachment builder) now commit **consecutive submeshes whose resolved `FaceTexture` compares
   equal** as a single surface, shifting the appended submesh's indices past the vertices already
   in the `SurfaceTool`. Godot re-sorts every transparent *surface* by AABB-centre distance each
   frame, but draws triangles **within** a surface in index order and sorts nothing — so one
   surface per authored run is Godot's equivalent of the viewer's rigged batching. On the measured
   hair (three meshes × six faces, all `b9af3b5f`) this is **18 co-located transparent draws → 3**.
   - Only *consecutive* runs merge. Merging scattered matches would interleave triangles the
     creator ordered deliberately — the very thing being preserved.
   - `FaceTexture` is a `readonly record struct`, so equality covers every field a material is
     built from. Merged faces cannot render differently from the surfaces they replace.
2. **`prim_blend_avatar` → `depth_draw_always`**, replacing BUG-RENDER-09's `depth_prepass_alpha`.
   That prepass was the same idea at a fraction of the strength: Godot cuts it at `ALPHA >= 0.99`,
   which measured only 39.7 % of the hair card, leaving the whole soft halo order-dependent.
3. **Hair goes back to `Kind.Blend`** — `ClassifyAlpha` is reverted to its pre-v0.21.14 form. Real
   translucency, the Firestorm look, now safe because neither of its inputs is view-dependent any
   more.

`depth_draw_always` alone was tried in 2026-07 and flattened the card stack ("coloured in with a
marker"). It is only safe **with** step 1 — depth write plus an arbitrary order means "whoever
draws first wins". The shader comment says so, so the pair is not separated later.

### Diagnostic

`[RiggedMesh] mesh=… merged N submeshes -> M surface(s)` prints whenever a merge fires. If the hair
does not report `6 submeshes -> 1 surface`, the merge did not happen and the instability remains.

### Deliberately unchanged

`Surface.Avatar` stays `cull_disabled`. The viewer does back-face cull rigged alpha
(`LLGLSPipelineAlpha` touches only `GL_BLEND`; the pipeline's own `GL_CULL_FACE` stays on) and
SLNG's winding is corrected in both mesh builders, so `cull_back` would be parity — but it is a
second variable in a change that already has two.

### The port needed a third piece — the minimum-alpha discard (v0.21.20)

v0.21.19 shipped the merge and `depth_draw_always` and came back **"ganz kaputt"**: the hair was
reduced to scattered shards over the shirt.

`depth_draw_always` writes depth for **every** fragment, including fully transparent ones. A hair
card is ~20 % fully clear, so the front-most card stamped a solid depth rectangle across its whole
quad and every strand behind it was rejected — leaving only whatever survived through the front
card's opaque texels.

The viewer discards first, and says why — `class2/deferred/alphaF.glsl:208-213`:

```glsl
// Insure we don't pollute depth with invis pixels in impostor rendering
if (final_alpha < minimum_alpha) { discard; }
```

`minimum_alpha` is `MINIMUM_ALPHA = 0.004f`, *"~ 1/255"* (`lldrawpoolalpha.cpp:63`), set on every
shader the alpha pool prepares (`:129`). Below one 8-bit step of alpha, contribute nothing at all —
neither colour nor depth.

`prim_blend_avatar` now does the same. A bare `discard` does not move the material to the opaque
queue the way `ALPHA_SCISSOR_THRESHOLD` would; it stays a blended surface, which is the point.

So the ported design is **three** pieces, not two, and all three are load-bearing:
authored-order batching, depth write, and the minimum-alpha discard.

### depth_draw_always reverted; the merge is kept (v0.21.21)

v0.21.20 (merge + `depth_draw_always` + minimum-alpha discard) came back still wrong: the hair
rendered as a **single translucent layer** with the shirt showing through.

The mechanism, now precisely named: with an unconditional depth write, only the *nearest so far*
chain survives at each pixel. A hair card's faint halo — anything above the minimum-alpha discard,
i.e. one 8-bit step — claims depth and rejects every strand behind it, so the card stack stops
accumulating. Identical to the 2026-07 "coloured in with a marker" result.

Two reasons the viewer's `write_depth = rigged` does not port cleanly, both recorded in the shader
so it is not attempted a fourth time:

- The viewer **back-face culls** rigged alpha (`LLGLSPipelineAlpha` touches only `GL_BLEND`; the
  pipeline's own `GL_CULL_FACE` stays on), while `prim_blend_avatar` is `cull_disabled` — roughly
  twice as many fragments here can claim depth as there.
- Depth write is only *half* of the viewer's design, and it is the half that does not stand alone.

**What is kept, and is verified working:** the authored-order surface merge. The log proves it
fires and is material-correct —

```
[RiggedMesh] mesh=6d07686e merged 6 submeshes -> 1 surface(s)
[RiggedMesh] mesh=7b773afa merged 6 submeshes -> 5 surface(s)   <- multi-material, correctly only partly merged
```

so the hair went from 18 co-located transparent draws to 3, and each hair mesh is now a single
draw whose triangle order never changes. `depth_prepass_alpha` (BUG-RENDER-09's in-world-validated
behaviour) is restored alongside it, as is the minimum-alpha discard, which is correct on its own
terms regardless of the depth mode.

**CONFIRMED IN-WORLD (v0.21.21):** *"kein Flackern und die Haare sehen gut aus."*

**The merge alone was the fix.** Every shader experiment in this bug — five before it was filed,
and five more inside it (`Scissor` at four different thresholds, `alpha_to_coverage_and_one`,
`ALPHA_ANTIALIASING_EDGE`, `depth_draw_always`) — was aimed at the wrong layer. The instability was
never in how a fragment was shaded; it was that SLNG handed Godot's transparent sorter **six
independently reorderable surfaces** where the creator had authored one ordered stream. Give the
sorter one surface and there is nothing left to reorder, and the shading that was already correct
stays correct.

`cull_back` on `Surface.Avatar` remains the one open parity gap (the viewer back-face culls rigged
alpha; SLNG's winding is corrected in both mesh builders), but nothing depends on it now.

### And that was the whole fix

**CONFIRMED IN-WORLD (v0.21.21):** *"kein Flackern und die Haare sehen gut aus."*

The merge alone did it. `depth_draw_always` was reverted, the shading was never touched, and the
hair renders with exactly the `Kind.Blend` treatment it always had. The instability was never in
how a fragment shades; it was that SLNG handed Godot's transparent sorter **six independently
reorderable surfaces** where the creator had authored one ordered stream.

## Deliberately NOT changed

`Surface.Avatar` stays `cull_disabled`. The winding is corrected in both
`BuildRiggedMeshInstance` and the static-attachment builder, so `cull_back` *would* be
viewer parity (`LLGLSPipelineAlpha` touches only `GL_BLEND`; the pipeline's own
`GL_CULL_FACE` stays on) and would halve the fragment work — but it is a second variable
and the earlier rounds show what bundling two of them costs. Separate task if wanted.

## Files

- `app/scripts/AvatarRenderer.cs` — `ResolveFaceTexture`, authored-order surface merging in both
  `BuildRiggedMeshInstance` and the static-attachment builder, `[RiggedMesh] merged` diagnostic
  (behind `--diag`).
- `app/materials/prim/prim_blend_avatar.gdshader` — `MINIMUM_ALPHA` discard; the
  `depth_draw_always` experiment and why it does not port, recorded in place.
- `app/scripts/Boot.cs` — `AppVersion` → `v0.21.21-alpha`.

## Acceptance

- ✅ Mesh hair no longer switches which strands are in front as the camera orbits an avatar.
- ✅ Strand tips read as soft, not hard-cut spikes, and the hair stays opaque — the shipped fix
  keeps `Kind.Blend`, so this is the shading that was already correct.
- ✅ BoM skin under one *and* two alpha-layer wearables is unchanged: `ClassifyAlpha` is
  byte-identical to its pre-BUG-RENDER-12 form, and `prim_blend_avatar` keeps
  `depth_prepass_alpha` (BUG-RENDER-09).
- ✅ Veils / tinted translucent panes still blend — same reason.
- ✅ Merge is material-safe: only consecutive submeshes whose whole `FaceTexture` record compares
  equal are joined, verified in-session on a multi-material mesh (`6 submeshes -> 5 surface(s)`).

Landed as `64dc8d8`, merged to `main` as `cd0a854`.

---

# History — ten attempts that aimed at the wrong layer

Kept in full, because the cost of this bug was not the fix but the ten rounds before it, and
because two of these (`depth_draw_always`, `Kind.Scissor` for hair) look reasonable enough to be
tried again by someone who has not read this.

## Why the earlier attempts failed

The five reverted attempts recorded in BUG-RENDER-11 all changed the *mechanism* while
leaving the cutoff at a value tuned for something else:

| attempt | outcome |
|---|---|
| `Kind.Scissor` at a **low** threshold (0.06) | keeps nearly the whole card → solid sheets ("dunkelbraune Flächen beim Zoomen") |
| `depth_draw_always` | flattens the card stack (no back-to-front sort to make it safe) |
| `+ cull_back` | ditto, cull was not the variable |
| a double-sided twin | a hand-rolled prepass, same 0.99-equivalent problem |
| `hairSheet → Surface.WorldPrim` (cull_back) | still @0.06 → the brown-sheet result again |

A sixth, `v0.21.14`, gated the same mechanism on `fracClear > 0.5` and missed the hair
outright — the log proved it: nine faces took the route, none of them hair. What was
missing every time was not a better mechanism but **knowing which texture the hair is**.
Decoding the candidates out of the client's own cache and looking at them settled it in
one step.

## Abandoned: an undeclared-alpha worn face as a cutout (v0.21.14–15)

The first attempt at this bug gated on `fracClear > 0.5` ("mostly hole"). That is the
wrong feature: it missed the hair (20 % clear) entirely and instead caught skin and lace.
Confirmed from the `v0.21.14` log — nine faces took the new route, none of them the hair.

The feature that actually separates a cutout from genuine translucency is **whether both
ends of the alpha histogram are populated**. A cutout has fully-clear texels *and*
fully-opaque ones; a real translucent film has neither — measured on the only two such
assets in the same session, `a0566c3c` (minA=101) and `c9569503` (minA=16), both at
exactly `clear=0.000 solid=0.000`, every texel partial. The gap to the hair's
`0.204 / 0.449` is the whole histogram wide, so `UniformFilmTolerance` only has to be
non-zero; 2 % absorbs a stray texel or a resized LOD.

So, for a **non-BoM** face in `ClassifyAlpha`:

- `fracClear >= 2 %` **or** `fracSolid >= 2 %` → `Kind.Scissor`
- otherwise → `Kind.Blend` (a uniform partial film)

`prim_scissor_avatar` is depth-writing and renders in the **opaque queue** →
**order-independent**, so there is no order left to reshuffle. Its `alpha_to_coverage`
does **not** force the halo opaque: `ALPHA` becomes the MSAA coverage mask, so the soft
strand edge survives as ~4 coverage levels rather than collapsing to a hard cut. That
pairing is what carried BUG-RENDER-11's grass, which is thinner, higher-frequency alpha
than hair. `GpuCache.TryGetIsAlphaMaskable` — the `analyzeAlphaData` port BUG-RENDER-11
already shipped — only picks the **cutoff**: `0.5` for a clean 1-bit mask, `0.33` for a
high-frequency one (the viewer's own DoF-pass minimum alpha).

**Bakes-on-Mesh faces are excluded** (`bomChannel < 0`). A bake is skin, not a worn card;
BUG-RENDER-09's measured behaviour — including a mostly-cleared bake staying on `Blend` +
`depth_prepass_alpha`, and `BomAlphaScissorThreshold` — is untouched.

The blast radius is small: of the non-BoM faces in the reference session only five were
`Blend` at all. Two stay (the films), three become `Scissor`. The direction is also the
safe one — the historical regressions in this area came from faces *losing* depth write by
moving to `Blend`; moving to `Scissor` keeps it.

`[AvatarAlpha]` now prints `SHEET maskable=<bool>` on this branch.

**In-world result (v0.21.15):** *"flackert nicht mehr ist aber etwas hart"* — the pop is gone,
confirming the diagnosis. Side by side with Firestorm the strand tips came out hard and spiky
where Firestorm's are soft.

## Abandoned: sharpening the coverage edge (v0.21.16)

The hair classified `Scissor @0.33`, `maskable=False`, with the project's `msaa_3d=2` (4x). Four
coverage levels spread across a **35 %-wide** alpha gradient is a stepped ramp, not an edge —
that is the hardness, and it is inherent to `alpha_to_coverage` alone.

Godot's remedy is `ALPHA_ANTIALIASING_EDGE` (+ the required `ALPHA_TEXTURE_COORDINATE`): alpha is
rescaled by its own screen-space derivative so the transition narrows to roughly one pixel, and
four coverage levels across one pixel is real edge antialiasing. This is what
`StandardMaterial3D`'s *Alpha Antialiasing = Alpha To Coverage + Edge* drives.

- The edge value is set to the **scissor threshold itself** — Godot's own default for the control
  is 0.3 and the cut points here are 0.33 / 0.5, so there is no second constant to keep in sync.
- Writing those built-ins is **compile-time significant** in Godot (it defines
  `ALPHA_ANTIALIASING_EDGE_USED` regardless of the uniform's value), so only
  `prim_scissor_avatar.gdshader` writes them. BUG-RENDER-11's in-world-confirmed world-prim
  cutout stays bit-identical.
- The uniform is nevertheless *declared* in all four scissor variants, because `SelfTest` requires
  a variant to expose exactly its base shader's uniforms (it now reports 36 for each).

## Abandoned: the edge band was set wrong (v0.21.16 → v0.21.17)

v0.21.16 set `ALPHA_ANTIALIASING_EDGE` **equal to** the scissor threshold, on the reading that it
is a sharpening amount centred on the cut. It came back from in-world as **"echt harte Kanten"** —
strictly worse than the plain cutout.

It is not a sharpening amount. Godot's docs call it *"the threshold **below** which alpha to
coverage antialiasing should be used"*, and the PR that added it (godotengine/godot#40364) says it
*"controls the width of the transparency band around the object"*. So the antialiased band is
**[scissor_threshold, edge]** — setting the two equal puts the entire band inside the region the
scissor already discards, leaving a purely binary cut with no band at all.

Two changes follow from that same source:

1. **`AlphaAaBandWidth = 0.42`**, added *on top of* the threshold rather than replacing it — the
   hair's two cut points become bands `0.33–0.75` and `0.5–0.92`, covering the bulk of its
   measured 35 % graded halo without reaching into the 45 % solid core.
2. **`prim_scissor_avatar` switches to `alpha_to_coverage_and_one`.** PR #40364: plain
   `alpha_to_coverage` marks the samples but *"leaves the alpha value unchanged"*, so a surviving
   fragment is attenuated twice — once by coverage, once by the blend — which the PR describes as
   bleed/halo. `_and_one` marks the same samples and then forces alpha to 1, so *"no alpha blending
   occurs anymore"*, giving clean antialiased edges; the PR names hair/foliage as its case. Every
   other user of this variant is near-binary alpha (`fracMid <= 0.06`), where forcing a surviving
   texel to full alpha changes nothing.

`prim_scissor.gdshader` deliberately stays on plain `alpha_to_coverage` and never writes the
built-ins — writing them is compile-time significant in Godot, and BUG-RENDER-11's world-prim
cutout is confirmed in-world as it stands.

**The scissor threshold is deliberately unchanged from the round that removed the flicker**, so if
alpha-to-coverage turns out not to resolve in this pipeline at all, this is bit-identical to
v0.21.15 ("etwas hart") rather than a regression. Widening `AlphaAaBandWidth`, or lowering the
threshold so the band starts earlier, is the next lever.

## Abandoned: calibrating the band width (v0.21.18)

v0.21.17 came back **too transparent**: the shirt showed through the hair. That is a more useful
result than it looks, because it **settles the open question** from the round before it —
alpha-to-coverage demonstrably *does* resolve in this pipeline. The hardness was never a dead A2C,
it was a missing band.

`AlphaAaBandWidth` is now the only dial left on worn cutout content, and both of its ends have been
seen in-world on the same asset:

| width | band | result |
|---|---|---|
| `0.00` (v0.21.15) | none — binary cut | *"etwas hart"* — the whole 35 % graded halo renders opaque, tips are spikes |
| `0.42` (v0.21.17) | 0.33–0.75, ~15 % of texels | far too transparent — a band that wide feathers the strand **body**, not the silhouette |
| **`0.12`** (v0.21.18) | 0.33–0.45, ~4 % of texels | silhouette only; opacity preserved |

Deliberately near the low end: overshooting costs opacity, undershooting only returns to a state
already judged acceptable.

## The note that ended the loop

Written at the end of the v0.21.18 round, before the viewer's sorting code had been read:

> The remaining structural option, should tuning this dial not converge, is to attack the measured
> cause directly rather than the shading: the hair's **six faces per mesh all carry the same
> texture and tint** (`face ids: [b9af3b5f × 6]`, `default=b9af3b5f`). Merging same-material faces
> into one surface would take the scene from **18 sorted transparent draws to 3**, which would make
> `Kind.Blend` — real translucency, the Firestorm look — viable again. That is a mesh-build change,
> not a number.

That is what shipped. The measurement pointing at it (`18 co-located transparent draws`) had been
in this file since the first round; five more shading experiments were run past it before anyone
acted on it.
