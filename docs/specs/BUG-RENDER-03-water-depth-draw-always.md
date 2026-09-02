# [BUG-RENDER-03] Water's `depth_draw_always` cut holes in alpha-blended tree canopies

- **Feature ID:** `BUG-RENDER-03`
- **Track:** `render`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **Branch:** `feature/FEAT-SL-01-second-life-readiness` (found and fixed mid-session, same branch)
- **Version:** `v0.20.0-alpha`
- **Supersedes:** `BUG-RENDER-02`'s diagnosis of the same user report (that spec's own fix stays —
  see its correction note — it was just answering the wrong question)

## Overview & Goal

Reported live on Aditi, with a screenshot: a hazy grey band cutting horizontally across a tree's
canopy, at roughly the water's height. First read as "the water plane overwrites textures... at
every tree, sim-wide." The user corrected the first fix attempt directly: *"das hat nix mit den SL
Bäumen zu tun das sind mesh bäume und die Blätter sind Texturen mit Transparenz"* — these are mesh
trees with alpha-blended (transparent) leaf textures, nothing to do with SL's procedural system
trees.

## Root cause

`grep -rn "depth_draw_" app/materials/` shows every prim shader in this project —
`prim_opaque`, `prim_blend`, `prim_scissor`, and every `_avatar`/`_hud` variant of each — uses
`render_mode ... depth_draw_opaque ...`. `water.gdshader` was the **one exception**:
`depth_draw_always`, with no comment anywhere explaining why, and nothing else in the codebase
depending on it.

`depth_draw_opaque` writes to the depth buffer only where a fragment is opaque-looking;
`depth_draw_always` forces a depth write **unconditionally**, regardless of how transparent the
surface actually is. Godot's transparent-pass rendering order between two *separate* transparent
objects (the water plane and a tree's alpha-blended leaf mesh) is only an approximate,
object-level sort (by AABB distance to camera) — not a guaranteed correct per-pixel order the way
opaque geometry gets from the depth buffer alone. Wherever the water plane happened to render
before a tree's canopy in that approximate order, its forced depth write could fail the depth test
for leaf fragments that were geometrically *behind* the water's depth value at that pixel — even
though both surfaces are translucent and were meant to blend together, not occlude one another.
The leaf pixel was discarded outright rather than composited, which reads exactly as "a hole cut
where the water is" — a horizontal band at the water's height, on every tree whose canopy happens
to cross it, sim-wide.

## Acceptance Criteria

- [x] `water.gdshader` uses `depth_draw_opaque`, matching every other shader in the family.
- [x] Confirmed via `--selftest` that the shader still compiles and its uniform count is
      unchanged (11 — this is a `render_mode` flag change, not a uniform change).
- [x] No other code path was found to depend on water's depth-buffer presence (the screen-space
      refraction uniforms in the same file are commented "declared but unused until the
      refraction pass exists").

## Technical Specs & Affected Files

| File | Change |
|---|---|
| `app/materials/water.gdshader` | `render_mode`: `depth_draw_always` → `depth_draw_opaque` |

### Design notes

- **One-line fix, but only after finding the right one-line.** The first hypothesis (SL system
  trees, `BUG-RENDER-02`) was wrong for this report; the actual cause needed comparing water's
  `render_mode` against every sibling shader's, which is what surfaced the sole outlier.
- **`depth_draw_opaque` is the established convention, not a new choice.** Every alpha-blended
  material in this project already uses it and already coexists correctly with every other
  transparent surface; water just never followed its own family's own pattern.
- **No unit test.** This is a GPU rasterization ordering bug — nothing SLNG.*.Tests exercises
  touches render-mode flags or depth-buffer interaction; `--selftest`'s shader compile check is
  the only mechanical guard available, and it passed (uniform count unchanged, shader still
  compiles).

## What the tests guarantee

Nothing beyond `--selftest`'s shader-compiles-and-uniforms-match check, which this passes cleanly
(11 uniforms, same as before). The actual visual fix can only be confirmed in-world.

## Still open

- **If a screen-space refraction or underwater-fog pass is ever built**, it may need water
  reliably present in the depth buffer again — revisit this render_mode choice at that point
  rather than assuming `depth_draw_opaque` is permanently correct.

## ⚠️ Correction (same session, verified live)

**This fix was real and necessary, but not sufficient.** Re-tested in-world (user screenshots: a
tree canopy correct before its texture loaded, cleanly cut off at the water/horizon height once
the real texture arrived) — the band was still there after this shipped. `depth_draw_opaque` fixed
a real, separate depth-TEST failure mode; it did nothing for Godot's independent transparent
draw-order SORT, which turned out to be the dominant cause: two transparent objects at the same
default `RenderPriority` (water and every ordinary foliage/prim material) fall back to an
approximate, per-object camera-distance heuristic that a plane the size of water (up to 16384m
across) cannot give a meaningful single answer to. See `BUG-RENDER-05` for the actual complete fix
(`RenderPriority = -1` on the shared water material) and the full mechanism — kept as a real,
still-necessary piece of the fix, not reverted.
