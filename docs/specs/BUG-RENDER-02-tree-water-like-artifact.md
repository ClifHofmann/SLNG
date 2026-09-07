# [BUG-RENDER-02] Every tree rendered as a hazy, water-like plane

- **Feature ID:** `BUG-RENDER-02`
- **Track:** `render`
- **Status:** `✅ Done` — committed `v0.20.0-alpha` with 7 tests; inert-but-correct for real foliage pcodes. Closed 2026-09-07 (doc triage). The user-reported "water" symptom was a separate bug (`BUG-RENDER-03` / `BUG-RENDER-05`).
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **Branch:** `feature/FEAT-SL-01-second-life-readiness` (found and fixed mid-session, same branch)
- **Version:** `v0.20.0-alpha`

## ⚠️ Correction — this is NOT the bug the "water overwrites textures" report turned out to be

First reported as "the water plane overwrites textures" (with a screenshot), then clarified:
*"das mit dem wasser tritt auf der ganzen sim bei den bäumen auf"* — at trees, sim-wide. This spec
was written on the assumption that meant SL **system** trees (Tree/NewTree/Grass pcodes), and
fixed the very real bug described below. The user then corrected that directly: *"das hat nix mit
den SL Bäumen zu tun das sind mesh bäume und die Blätter sind Texturen mit Transparenz"* — these
are **mesh** trees with alpha-blended leaf textures, not system trees at all. The actual water/tree
report is fixed in **`BUG-RENDER-03`** instead (`water.gdshader`'s `depth_draw_always`).

This fix is harmless and inert for a mesh tree (mesh objects never carry a foliage pcode, so
`PrimPCode.IsFoliage` never matches one) — it did not make anything worse, it just did not explain
what was reported. It is kept because it is a real, separate, still-valid bug for genuine SL system
trees, verified independently of the water report below.

## Overview & Goal

## Root cause

`PrimMeshService.Generate` already special-cases the three procedural-foliage pcodes
(`Tree`, `NewTree`, `Grass`) with a crossed-planes placeholder mesh — real branch/blade geometry
needs a species table (`trees.xml`, bundled but not parsed anywhere in this codebase) the way the
reference viewer generates `LLVOTree` geometry, and that hasn't been built yet:

```csharp
if (prim.PrimData.PCode == PCode.Tree || prim.PrimData.PCode == PCode.NewTree || prim.PrimData.PCode == PCode.Grass)
    return GenerateCrossedPlanes();
```

Nothing downstream knew that. `ObjectRenderer.ApplyFaceMaterialsAsync` treated the resulting mesh
exactly like a normal prim face: it built a `FaceTexture` from `prim.TextureId` and tried to fetch
that as a real texture asset. For these three pcodes, it is not one — a tree's visible appearance
comes from its **species** (a small server-assigned index the real viewer maps through
`trees.xml` to a fixed, bundled texture), not from the object's own `TextureEntry`. The id SLNG
was fetching resolved to nothing meaningful, and the resulting unresolved, untextured face —
composited through the same atmospheric-fog blending every prim shader applies — rendered as a
flat, fog-tinted, translucent-looking plane. Visually indistinguishable from the water shader's
own hazy, fog-blended look at a glance, and identical on every tree because every tree hits this
exact same unresolved path — which is why it read as "water" and why it was sim-wide rather than
tied to actual water proximity.

## Acceptance Criteria

- [x] Trees and grass no longer render as a hazy, semi-transparent, water-like plane.
- [x] Foliage pcodes render **opaque** with a plain placeholder tint instead of chasing a
      meaningless texture id.
- [x] Unit tests written and passing (7 new, pinning the pcode values the fix keys off).
- [ ] **Real species textures (bark/leaves from `trees.xml`) — explicitly out of scope here.**
      This is a visibility/correctness fix (stop the wrong artifact), not the tree-rendering
      feature. See "Still open."

## Technical Specs & Affected Files

| File | Change |
|---|---|
| `src/SLNG.Core/PrimPCode.cs` | new — named pcode constants (`Tree`, `NewTree`, `Grass`) and `IsFoliage(byte)`, verified by reflection against the pinned LibreMetaverse 3.1.3 assembly |
| `app/scripts/ObjectRenderer.cs` | `ApplyFaceMaterialsAsync` short-circuits on `PrimPCode.IsFoliage`, applying an opaque placeholder material and skipping the texture-fetch path entirely |
| `tests/SLNG.Core.Tests/PrimPCodeTests.cs` | new — 7 tests |

### Design notes

- **Named constants in `SLNG.Core`, not a raw byte comparison in `app/`.** `app/` must never
  reference `LibreMetaverse.PCode` directly (AGENTS.md's layering rule — no LibreMetaverse type
  crosses `SLNG.Net`/`SLNG.Assets`'s public boundary), and `PrimMeshService` (which already
  special-cases these three pcodes for geometry) lives on the other side of exactly that boundary.
  `PrimPCode` is the neutral name both sides can use without either duplicating magic numbers or
  crossing the boundary.
- **Opaque, not alpha-blended.** The crossed-plane geometry is two intersecting quads, not a
  leaf-shaped cutout — rendering it alpha-blended with no real leaf texture would still look wrong
  (an untextured translucent shape), and opaque makes the placeholder nature obvious (a plain green
  cross) rather than accidentally looking like an intentional, if ugly, effect.
- **Values confirmed by reflection**, not the vendored source or memory: `Tree = 255`,
  `NewTree = 111`, `Grass = 95`, matching the project's own standing rule (`sl-viewer-source-verification`)
  that `scratch/libremetaverse_src` is not the pinned package.

## What the tests guarantee

`PrimPCodeTests` pins the three pcode values against the pinned assembly directly (a reflection
probe, not trust in the vendored source), confirms `IsFoliage` recognises all three and rejects a
sample of ordinary pcodes (prim, avatar, legacy sculpt), so a future refactor that touches this
constant fails a test instead of silently reintroducing the water-like artifact.

The material-selection branch in `ObjectRenderer` itself is not unit-tested — it needs a live
`PrimitiveComponent`/mesh-instance scene graph, out of scope for `SLNG.Core.Tests`. Confidence
comes from the same reasoning that found the bug: the fallback path this bypasses
(`BuildFaceMaterialAsync` fetching `prim.TextureId`) is unchanged and still exercised by every
non-foliage object.

## Still open

- **Not yet re-verified in-world.** Reported once, fixed once; not yet confirmed the artifact is
  actually gone on Aditi.
- **Real tree/grass species rendering is a separate, larger task**, not covered here: parsing
  `trees.xml`'s species table, mapping the object's species/state byte to the right bundled
  texture, generating (or at minimum texturing) geometry that reads as an actual tree rather than
  a plain green cross. This fix only removes the wrong, actively-misleading artifact; it does not
  make trees look right.
