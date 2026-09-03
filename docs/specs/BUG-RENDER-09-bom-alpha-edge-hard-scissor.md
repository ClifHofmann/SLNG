# [BUG-RENDER-09] A BoM alpha-layer mask renders with a hard, ragged edge instead of a smooth fade

- **Feature ID:** `BUG-RENDER-09`
- **Track:** `render`
- **Status:** `⏸️ Pending` — option 1a (`v0.20.41`) **reverted `v0.20.46`**, it broke BoM heads.
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

## Acceptance

- A BoM skin face whose bake carries a soft alpha-layer mask renders a smooth edge, not a
  sawtooth, from every viewing angle.
- No new see-through / depth-sort artifact on a mesh body (front/back, crossing limbs, seated).
- Hair and non-BoM clothing alpha unchanged (regression-checked against the `ClassifyAlpha`
  history: clothing-invisible, hair-speckle, "scarf" artifact).
