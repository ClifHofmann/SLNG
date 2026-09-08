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

## Fix — an undeclared-alpha worn face is a cutout unless it is a uniform film

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

## Follow-up — sharpen the coverage edge (v0.21.16)

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

## Deliberately NOT changed

`Surface.Avatar` stays `cull_disabled`. The winding is corrected in both
`BuildRiggedMeshInstance` and the static-attachment builder, so `cull_back` *would* be
viewer parity (`LLGLSPipelineAlpha` touches only `GL_BLEND`; the pipeline's own
`GL_CULL_FACE` stays on) and would halve the fragment work — but it is a second variable
and the earlier rounds show what bundling two of them costs. Separate task if wanted.

## Files

- `app/scripts/AvatarRenderer.cs` — `ClassifyAlpha` cutout-vs-uniform-film branch,
  `UniformFilmTolerance`, `CleanSheetScissorThreshold` / `WispySheetScissorThreshold`, `solidCount`,
  `LogAlphaVerdict` maskable.
- `app/materials/prim/prim_scissor_avatar.gdshader` — `ALPHA_ANTIALIASING_EDGE` +
  `ALPHA_TEXTURE_COORDINATE`; `prim_scissor{,_doublesided,_hud}.gdshader` — uniform declaration
  only, for SelfTest parity.
- `app/scripts/PrimShaderFamily.cs` — `AlphaAntialiasingEdge`.
- `app/scripts/Boot.cs` — `AppVersion` → `v0.21.18-alpha`.

## Acceptance

- ✅ Mesh hair no longer switches which strands are in front as the camera orbits an avatar
  (`v0.21.21`, in-world).
- ✅ Strand tips read as soft, not hard-cut spikes, and the hair stays opaque — the final approach
  keeps `Kind.Blend`, so this is the shading that was already correct.
- ✅ BoM skin under one *and* two alpha-layer wearables is unchanged: `ClassifyAlpha` is
  byte-identical to its pre-BUG-RENDER-12 form, and `prim_blend_avatar` keeps
  `depth_prepass_alpha` (BUG-RENDER-09).
- ✅ Veils / tinted translucent panes still blend — same reason.
- ✅ Merge is material-safe: only consecutive submeshes whose whole `FaceTexture` record compares
  equal are joined, verified in-session on a multi-material mesh (`6 submeshes -> 5 surface(s)`).

Landed as `64dc8d8`, merged to `main` as `cd0a854`.
