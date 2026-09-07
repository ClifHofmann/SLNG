# [BUG-RENDER-09] A BoM alpha-layer mask renders with a hard, ragged edge instead of a smooth fade

- **Feature ID:** `BUG-RENDER-09`
- **Track:** `render`
- **Status:** `✅ Done` — the `depth_prepass_alpha` fix (`v0.20.54`) is confirmed in-world 2026-09-07: soft edge with 1 and 2 alpha layers, no venetian-blind banding, heads and hair unaffected. (Trail: 1a `v0.20.41` reverted `v0.20.46`; 1c `v0.20.52` rejected live; 1b shipped `v0.20.53`; real cause — a Blend depth-sort failure — found `v0.20.54`.)
- **Owner:** `claude`
- **Depends on:** `FEAT-RENDER-01` (shader family), `FEAT-AVATAR-01` / `BUG-AVATAR-02` (BoM bake resolve)
- **Reported:** live, Agni, 2026-09-03, with two screenshots of a mesh-body foot: the skin ends
  partway down the ankle in a **sawtooth / torn** edge. *"die Haut über dem Alpha sieht komisch
  zerrissen aus."*

## What's happening

A Bakes-on-Mesh channel's baked texture carries the user's worn **alpha-layer wearable** as a
soft gradient in its alpha channel (composited server-side to hide the system/mesh body where a
shoe or a piece of clothing sits). SLNG resolves that bake for the mesh face
(`AvatarRenderer.BuildFaceMaterialAsync`) and then `ClassifyAlpha(kind, built)` decides the alpha
mode from the pixels. A mostly-opaque skin texture with a thin soft mask band measures
`fracMid ≤ GradedAlphaThreshold` and `fracClear ≤ MostlyClearThreshold`, so it falls through to
**`Kind.Scissor` at `HardCutoutScissorThreshold` (0.25)** — a hard per-pixel alpha test. On a
smooth 0→1 gradient that produces a binary sawtooth edge (only `alpha_to_coverage` + MSAA 4x
soften it, which is ~4 steps — not enough for a wide fade).

The real viewer blends this (`LLDrawPoolAvatar` / `LLDrawPoolAlpha`), so the edge is a clean fade.

This is the same objection the system-bake path already carries as an unsettled `MIGRATION NOTE`
(`AvatarRenderer.cs` ~981: *"a hard 0.5 cutoff blotches the soft gradients SL's alpha-layer
wearables paint into the bake … Settle it with a live A/B"*), and the
`[[avatar-alpha-hash-vs-scissor]]` memory (*"hard AlphaScissor(0.5) blotches soft SL alpha-layer
gradients"*).

## Why it isn't a one-line change

`ClassifyAlpha`'s Scissor fallthrough exists for a reason with scars:
- `Kind.Blend` for an avatar body face = **no depth write** in Godot, so the body's own
  front/back faces and overlapping mesh-body pieces can sort wrong and show through (the exact
  reason the *system*-bake path at ~989 uses Scissor, not Blend — a Blend attempt there made "the
  system body/head reappear fully opaque / see-through").
- `Kind.Hash` isn't a shader-family variant here (no `prim_hash.gdshader`), and past AlphaHash
  attempts turned hair into a grainy stipple (`ClassifyAlpha` comment, 2026-07-25, *"do not
  re-attempt without new evidence"*). This screenshot **is** new evidence, but only for **skin**,
  not hair.

## Options (needs a live A/B)

1. **Scope a fix to `wasBom` skin faces only** — leave hair / clothing / non-BoM through
   `ClassifyAlpha` untouched. For a BoM bake face:
   - a. `Kind.Blend` — matches the viewer, smooth; risk = body depth-sort artifacts. Test a
     mesh body from several angles, arms crossing torso, sitting.
   - b. Keep `Kind.Scissor` but drop the threshold to ~`0.04` — keeps depth-write, discards only
     near-fully-transparent pixels, so the ragged edge sits where alpha ≈ 4 % (nothing visible to
     be ragged). Lowest risk; may still show a faint step.
   - c. A dedicated `prim_hash_avatar` variant (`ALPHA_HASH_SCALE` in `render_mode`) — dithered
     but depth-correct and no hard edge; the stipple that ruined hair is far less visible on a
     large low-contrast skin surface. New shader + `check_shader_globals` + selftest.
2. Do the same for the **system-bake path** (`LoadAndApplyTextureAsync` ~989, hardcoded
   `Scissor @ 0.5`) — same content, same defect, and its own comment already asks for this A/B.

## Not this bug

The immediate live report also had *"das Alpha wurde nicht entfernt obwohl ich es abgenommen
habe"* — the mask is still in the bake at all. That is `BUG-AVATAR-03` (the wearable-remove /
rebake path is unreliable on rate-limited SSB); the alpha should not be there, and once it's
genuinely gone the ragged edge goes with it. `BUG-RENDER-09` is only about how a *legitimately
present* alpha-layer mask should render — smoothly, for when the user actually wants one.

## Fix (`v0.20.41-alpha`)

`BuildFaceMaterialAsync`, right after `ClassifyAlpha`:

```csharp
if (wasBom && kind == PrimShaderFamily.Kind.Scissor)
    kind = PrimShaderFamily.Kind.Blend;
```

Option **1a**, scoped to `wasBom && ClassifyAlpha == Scissor`.

**REVERTED `v0.20.46-alpha`.** The scope was not tight enough: a BoM **head/body** bake carries a
soft neck-blend alpha, so `ClassifyAlpha` returns `Scissor` for those faces too → they flipped to
`Blend` → the head mesh rendered in the transparent queue (`cull_disabled`, no depth write) and
its own overlapping faces sorted against each other = **blocky see-through chunks across the
face** (live, *"sieht richtig kaputt aus, das sah schon besser aus"*). Exactly the regression
class the `ClassifyAlpha` history warns about. `Blend` is off the table for any BoM face.

**Next attempt should be option 1c** — a dedicated `prim_hash_avatar` variant (`ALPHA_HASH_SCALE`
in `render_mode`): dithered but depth-correct and no hard edge, so it can't cause the sort
failure, and the stipple that ruined hair is far less visible on a large low-contrast skin
surface. New shader + `check_shader_globals` + selftest. Or option 1b (drop the Scissor threshold
to ~0.04) as a cheaper interim — keeps depth-write, pushes the banded edge to where alpha ≈ 4 %.

## Fix (`v0.20.52-alpha`) — option 1c, the `prim_hash_avatar` variant

A fourth `PrimShaderFamily.Kind`, `Hash`, with two new shaders:

- `app/materials/prim/prim_hash.gdshader` (base, `cull_back`)
- `app/materials/prim/prim_hash_avatar.gdshader` (`cull_disabled`)

Both are `prim_scissor`'s body with `ALPHA_SCISSOR_THRESHOLD` replaced by `ALPHA_HASH_SCALE`.
Writing `ALPHA_HASH_SCALE` is what puts Godot's fragment stage into hashed-alpha mode at all; the
uniform value only scales the noise. `alpha_to_coverage` is deliberately **not** carried over from
`prim_scissor` — that is its silhouette antialiasing and it would dither an already-dithered
result. `render_mode` is otherwise identical to the scissor twin, `depth_draw_opaque` included, so
the depth behaviour is bit-for-bit the one that already works and the v0.20.41 sort failure is
structurally out of reach.

Routing, in `BuildFaceMaterialAsync` right after `ClassifyAlpha`:

```csharp
if (wasBom && kind == PrimShaderFamily.Kind.Scissor)
    kind = PrimShaderFamily.Kind.Hash;
```

Same scope as the reverted v0.20.41 — `wasBom` faces that `ClassifyAlpha` sent to `Scissor` —
but the destination is depth-writing this time, so a BoM head/body face landing here (which is
what broke v0.20.41) cannot sort against itself. Hair, clothing and every non-BoM face keep their
existing verdict, so the historic hair-speckle regression is unreachable from this change. The
system-bake path (`LoadAndApplyTextureAsync`'s hardcoded `Scissor @ 0.5`, ~line 989) is left alone
until this has been A/B'd in-world.

`Surface.Hud` and the `_doublesided` axis have no hash twin: nothing routes to `Kind.Hash` there
yet, so `Select` falls them back to `Scissor` — the behaviour they had before this `Kind` existed —
rather than silently picking up a variant nobody has looked at. `FinishFaceMaterial` still sets
the scissor threshold on that fallback path.

Verified: `check_shader_globals` clean, selftest **32/32** (was 29/29 — the two new shaders plus
the `prim_hash_avatar` ↔ `prim_hash` uniform-pair check), both hash shaders report 35 uniforms so
they genuinely compiled.

**Tried in-world and REJECTED (`v0.20.52`).** Screenshot of the self avatar's legs: large
axis-aligned **rectangular patches** of skin dropping out — not a dither and not the old sawtooth.

Godot's hashed alpha is Wyman & McGuire's *Hashed Alpha Testing*: the per-pixel threshold is
`hash(floor(pix_scale * position))` with `pix_scale = 1 / (alpha_hash_scale * max screen-space
derivative of position)`. Close up on a large surface that derivative is big, `pix_scale` is
small, and whole **blocks** of object space share one threshold — a coarse patchwork rather than a
fine dither. Tuning `alpha_hash_scale` only trades block size for per-pixel speckle on skin, which
is the regression the `ClassifyAlpha` history already warns about for hair.

`Kind.Hash` and both shaders are **kept** (they are correct, selftested, and a dither is the right
tool for some content) but **nothing routes to them**. Do not re-point BoM faces at `Hash`.

## Fix (`v0.20.53-alpha`) — option 1b, and why the measurement says it is the right one

The premise behind 1a and 1c was "there is a wide soft gradient here that needs fading". The
classifier says otherwise: a face only reaches `Scissor` from `ClassifyAlpha` when
`fracMid <= 0.06` **and** `fracClear <= 0.5` — predominantly opaque with a **thin** anti-aliased
border. Anything genuinely graded already goes to `Blend`. So there is no wide gradient to dither,
and the defect is purely **where the binary cut falls**: at `HardCutoutScissorThreshold` (0.25) the
contour lands in the middle of that thin border, i.e. in visibly near-opaque skin, which is what
reads as a torn edge (and as venetian-blind banding once `alpha_to_coverage` steps it).

```csharp
if (wasBom && kind == PrimShaderFamily.Kind.Scissor)
    scissorThreshold = BomAlphaScissorThreshold;   // 0.04f
```

Same `Kind`, same shader, same depth write, same sort behaviour — only the cut point moves, to
where the mask is already ~96 % transparent. Not 0: a genuinely clear texel must still be
discarded or the face renders as its full uncut card. `alpha_to_coverage` still antialiases what
is left. This is the lowest-risk option in the list and the only one whose assumption matches the
measured pixel content.

**Not yet verified in-world.** The check to run:

## The real cause (`v0.20.54`) — these faces were never on the Scissor path

`v0.20.53` moved the cut from 0.25 to 0.04 and the live screenshot was **unchanged**. That is the
finding: the faces in the screenshot do not reach the `Scissor` branch at all.

`ClassifyAlpha` returns `Blend` when `fracMid > 0.06` **OR `fracClear > 0.5`**. The second clause
exists for hair cards ("mostly hole, appearance lives in the edges"). A lower-body BoM bake with
**two** alpha-layer wearables worn — the user's session had *"Super High Cutoffs Alpha Layer"* and
*"Camden Boots Alpha Layer"* — has most of its texture cleared, so it trips that clause and goes to
`Blend`. Every `prim_blend_*` variant is `depth_draw_opaque`, which on a transparent material means
**no depth write**; avatar geometry is `cull_disabled`; so the front and back of the same shin, and
two alpha layers over the same skin, have no defined draw order. That is the hard-edged translucent
patchwork in both screenshots.

It also explains the user's own reading of it — *"das Haar und das Skin problem sind die gleiche
Baustelle → Probleme mit mehreren alpha layern"*. Correct: **one** alpha layer stays under 50 %
clear and classifies as `Scissor`, which does write depth and looks fine; **two** push it over the
line into the broken path. Hair lands on the same path for the same reason.

### Fix

`depth_prepass_alpha` on `prim_blend_avatar.gdshader`. Godot runs a depth prepass for the
near-opaque part of the surface (cut at alpha 0.99), so solid skin and hair-strand cores establish
depth exactly like an opaque face while the soft edge still blends — the standard remedy for hair
and foliage, and what StandardMaterial3D's opaque-prepass depth mode does. It cannot reintroduce
the `v0.20.41` regression: that was `Scissor` faces *losing* depth write; this only gives depth
write back to faces that had none. Scoped to the Avatar variant; `prim_blend`, `prim_blend_hud` and
`prim_blend_doublesided` are untouched.

`BomAlphaScissorThreshold = 0.04` from `v0.20.53` is **kept** — still correct for BoM faces that do
classify as `Scissor`, it just was not the faces in the screenshot.

### New diagnostic

`[AvatarAlpha] <id> bom=<ch> minA=.. fracMid=.. fracClear=.. -> <Kind>` — one deduplicated line per
texture per verdict, unconditional. Two rounds of this bug were spent guessing which branch a face
took; now it says so, and the next log confirms or refutes the diagnosis above directly.

## Acceptance

- A BoM skin face whose bake carries a soft alpha-layer mask renders a smooth edge, not a
  sawtooth, from every viewing angle.
- No new see-through / depth-sort artifact on a mesh body (front/back, crossing limbs, seated).
- Hair and non-BoM clothing alpha unchanged (regression-checked against the `ClassifyAlpha`
  history: clothing-invisible, hair-speckle, "scarf" artifact).
