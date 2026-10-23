# [BUG-RENDER-50] Worn alpha drawn in the wrong order: an add-on behind a bikini paints over it

- **Feature ID:** `BUG-RENDER-50`
- **Track:** `render`
- **Status:** `🚧 In Progress` — built through `v0.27.88-alpha`, **not yet seen in-world**
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **Branch:** `fix/BUG-RENDER-50-worn-alpha-order` (stacked on `fix/BUG-RENDER-49-pbr-material-ids`)
- **Depends on:** `BUG-RENDER-38` (per-surface sort instances), `FEAT-PERF-23` (outfit merge)

## Overview & Goal

A wearer's rear add-on (attachment point 28, four meshes in one linkset) has blended detail strips 19-39 mm
BEHIND her bikini bottom (point 23, one mesh, all faces blended). SLNG drew the strips after the bikini, so they
painted over the fabric; Firestorm hides them.

Two causes:

1. **Order.** BUG-RENDER-38's tie-break ordered an avatar's worn rigged meshes by `LocalId % 256` (bikini 0.0328,
   strips 0.0394-0.0449) and called any fixed order parity. It is parity only while nothing writes depth. The viewer
   draws rigged alpha by attachment point: `CompareRenderOrder` (llspatialpartition.h:240-250) sorts by
   `mRenderOrder` descending, handed out walking `mAttachmentPoints` (a `std::map`, llvoavatar.h:1046) at
   llvoavatar.cpp:3046-3112 - the highest point first, the newest attachment first within a point.
2. **Depth.** The viewer draws rigged alpha in a first pass that writes depth (lldrawpoolalpha.cpp:203-205, :240;
   discard only below 0.004, :63), so a later item behind an earlier one is hidden. SLNG's blended avatar faces write
   depth only above alpha 0.99 (`depth_prepass_alpha`), and the bikini texture is 0.35-0.99 over 60 % of its area.

Also: a legacy material's diffuse alpha mode overrides the texture's alpha for worn faces too
(pipeline.cpp:1781-1820); `BuildFaceMaterialAsync` never read it.

## What was built

- `SLNG.Core.WornAlphaOrder` (+ tests with this avatar's numbers): rank = bake wearers first, then attachment
  point descending, then linkset root LocalId descending (stand-in for attach order), then LocalId descending.
  Rank r owns `[r·1e-4, (r+1)·1e-4)`: blended surface s at `+ (s+1)·1e-5`, the depth pass at `+ 9e-5`; ranks
  clamp at 559, so the avatar stays inside the old 5.6 cm budget.
- `AvatarRenderer.WornAlphaOrder.cs`: a per-avatar registry (`AvatarVisual.WornAlpha`), re-ranked on rig commit,
  detach and attachment-point change (written once per frame), worn items and animated-object parts alike.
- Depth pass `prim_depth_core_avatar.gdshader`: depth only, `cull_disabled`, threshold `RenderConfig.WornAlphaDepth`
  (`--worn-alpha-depth=N`, 0 = off). Not a `next_pass` (Godot ties it with its own colour pass in an unstable
  sort) and not a render priority (that runs it before every transparent draw): on its own instance at the item's
  depth slot. On an item's own instance that is the parent's copy of each surface moved off for sorting, which drew
  nothing before - so a rigged mesh now moves every blended surface, also a single one, and pays no extra
  instance for the pass. A merged sorted instance (FEAT-PERF-23) gets a child that shares its mesh and skin.
  Rigid attachments keep the old split rule and no pass (the viewer depth-sorts them with the scene).
- Legacy alpha mode on worn faces (`SLNG.Core.LegacyMaterialAlpha`): NONE / EMISSIVE opaque, MASK at cutoff/255,
  BLEND blended, a translucent face colour blends, DEFAULT leaves it to the texture. Not when a glTF material is
  present.

## Threshold: 0.33, not the viewer's 0.004

BUG-RENDER-12 (v0.21.19) wrote depth for every avatar blend fragment above the minimum alpha and was reverted
in-world: a hair card's faint halo claimed depth and rejected every strand behind it. SLNG's avatar variants are
`cull_disabled` (the viewer culls rigged alpha), so twice the fragments would claim depth. The pass here never
occludes an item's own layers (it runs after all of them), which removes BUG-RENDER-12's case, but between items
the halo argument holds: an earlier hair item's 1 % halo would cut a later one's strands. 0.33 is the viewer's own
"opaque enough to count" for its depth-only alpha pass (lldrawpoolalpha.cpp:212-227) and the BUG-RENDER-16 core's
default. The bikini texture's minimum alpha is 0.353, so all of it occludes, as at 0.004.

## Acceptance Criteria

- [x] xUnit: point 23 draws after every point-28 part, bake wearer first, offsets distinct and inside 5.6 cm
- [x] Selftest "worn alpha: viewer order and depth pass": staged and merged order, depth passes between items,
      re-rank on detach, legacy NONE/MASK/BLEND
- [ ] In-world: the wearer's bikini hides the add-on strips like Firestorm
- [ ] In-world: hair with several alpha layers / several hair items does not regress (no holes, no thinning)

## Not done

Pixel-level occlusion cannot be asserted headless (no rasterizer); the selftest asserts the order and depth state
that produce it.
