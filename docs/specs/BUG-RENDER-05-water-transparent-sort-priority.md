# [BUG-RENDER-05] Water still ate alpha-blended foliage after BUG-RENDER-03 — the depth fix wasn't the whole story

- **Feature ID:** `BUG-RENDER-05`
- **Track:** `render`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **Branch:** `feature/FEAT-SL-01-second-life-readiness`
- **Version:** `v0.20.6-alpha`

## Overview & Goal

User sent two screenshots of the same tree, before and after its leaf textures finished loading:
before, the untextured alpha placeholder canopy rendered fully and correctly; after the real
texture arrived, the canopy was cleanly cut off in a flat horizontal band exactly at the
water/horizon height — leaves above it, nothing but water colour below it. Confirmed with a
Firestorm Build-tool screenshot of the tree's actual texture settings: `Alpha-Modus:
Alpha-Blending` (real alpha blending, not a mask) — ruling out a cutoff/scissor misclassification
and confirming this is the SAME water/foliage transparency interaction `BUG-RENDER-03` already
investigated once, still happening after that fix shipped.

## Root cause

`BUG-RENDER-03` was real and necessary, but not sufficient. It fixed water's `depth_draw_always` →
`depth_draw_opaque`, which stopped water from forcing an incorrect DEPTH-TEST failure on
overlapping alpha fragments. It did not — because depth writes were never the only mechanism in
play — fix Godot's separate **transparent draw-order sort**, which is what was still producing the
cut.

Two transparent objects at the same `RenderPriority` (the default, 0, which is what both water and
every ordinary alpha-blended prim/foliage material use — nothing currently overrides it for
foliage) fall back to Godot's automatic camera-distance sort. That sort operates per *object*
(effectively per AABB), not per pixel. Water's plane is enormous — the per-region `WaterPlane`
covers the whole 256m region, and the horizon-filling `VoidWaterPlane`
(`TerrainRenderer.VoidWaterSize`) is **16384m** across — so its single computed "distance to
camera" bears no relationship to which of its fragments a given tree-canopy pixel is actually in
front of or behind. Wherever that heuristic ranked water as nearer, water's fragment simply painted
over the canopy's, regardless of true depth — visible exactly as a horizon-height cutoff, because
that's where the water plane's own screen-space footprint sits.

This exact class of bug was already hit and fixed once in this codebase — just for one consumer,
not systemically. `ObjectParticles.cs` gives every particle emitter `RenderPriority = 1` ("draw
after the water") with a comment explaining precisely this mechanism and citing the real viewer's
own answer to it: a dedicated three-pass split around water
(`POOL_ALPHA_PRE_WATER`/`POOL_WATER`/`POOL_ALPHA_POST_WATER`, `lldrawpool.h:74-78`) rather than
trusting a sort. Nothing had given foliage — or any other ordinary transparent object — the same
protection; particles happened to be the first thing that got visibly broken by it and got a
point fix.

## Acceptance Criteria

- [x] Water (`TerrainRenderer._waterMaterial`, shared by both the per-region `WaterPlane` and the
      `VoidWaterPlane`) gets `RenderPriority = -1` — strictly below the default (0) every ordinary
      transparent material uses.
- [x] `RenderPriority` buckets are compared before any distance-based tiebreak (a hard guarantee
      per Godot's rendering order, not another heuristic) — so this protects EVERY default-priority
      transparent object against water, not just the ones explicitly taught to fight it, unlike
      `ObjectParticles.cs`'s point fix.
- [x] `BUG-RENDER-03`'s `depth_draw_opaque` fix is kept — it was correct for what it addressed
      (a real, separate depth-test failure mode) and remains necessary; this fix addresses the
      sort-order failure mode alongside it, not instead of it.
- [x] `dotnet build SLNG.sln` / `app/SLNG.App.csproj`: 0 warnings. `dotnet test`: 563/563.
      `dotnet format` clean, shader-globals clean, `--selftest` 26/26 (no shader touched — this is
      a `Material.RenderPriority` assignment, not a `render_mode` change).

## Technical Specs & Affected Files

| File | Change |
|---|---|
| `app/scripts/TerrainRenderer.cs` | `Initialize()`: `_waterMaterial.RenderPriority = -1;` right after the water shader is assigned, with a doc comment cross-referencing `ObjectParticles.cs`'s existing point fix for the same mechanism. |

### Design notes

- **Fix the shared resource, not every consumer.** `ObjectParticles.cs`'s `RenderPriority = 1` on
  the particle material is a per-consumer opt-in — every NEW transparent object type (a translucent
  prim, a different attachment shader, a future effect) would need the same treatment discovered
  and applied independently, exactly how this bug survived undetected for foliage while already
  being fixed for particles. Setting water's own priority lower is the general fix: everything at
  the default priority now reliably wins against water, with nothing new to remember per object
  type.
- **Why `-1` and not something more extreme.** `RenderPriority` is a small signed range (-128..127)
  used sparingly elsewhere in the engine/project; one step below the default is already a hard
  guarantee against anything at the default, and leaves room below water (unlikely to ever be
  needed, but cheap to reserve) for something that should render even further back.
- **`ObjectParticles.cs`'s `RenderPriority = 1` was not removed.** It is now redundant against
  water specifically (water is `-1`, particles at `1` would already win against water at `0`
  regardless) but still correctly separates particles from OTHER default-priority transparent
  objects they may need to draw after — left alone rather than touched speculatively.

## What the tests guarantee

Nothing new is meaningfully unit-testable — `RenderPriority`'s effect on the transparent
draw-order sort is an engine-internal rendering behaviour with no C#-visible assertion surface, and
`app/scripts` sits outside `tests-rules`' `src/`-only scope regardless. The full 563-test suite
passing unmodified confirms no `SLNG.Core`/`SLNG.Net`/`SLNG.Assets` behaviour changed, and
`--selftest`'s unchanged uniform counts confirm no shader was touched (this fix is entirely a
`Material` property assignment in C#, not a `.gdshader` change).

## Still open

- **Not yet re-verified in-world.** Needs the exact scenario from the reporting screenshots — a
  tree canopy crossing the water/horizon height in view — checked again after this fix, ideally
  from a couple of camera angles/distances since the AABB-sort failure was distance-dependent
  before (see `ObjectParticles.cs`'s own note: "the order FLIPS as the camera moves").
- **Not audited: every OTHER transparent object type** (translucent prims, other attachment
  materials) for whether any of them relies, even accidentally, on losing to water at the default
  priority. None currently override `RenderPriority` away from 0 except particles, so none should
  regress — but this wasn't exhaustively checked against every material in the shader family.
