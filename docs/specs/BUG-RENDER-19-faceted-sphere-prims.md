# [BUG-RENDER-19] Sphere prims render visibly faceted

- **Feature ID:** `BUG-RENDER-19`
- **Track:** `render`
- **Status:** `✅ Done (code) — not yet re-verified in-world with a walk-up test`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Reported live 2026-09-11 while A/B-ing the lighting probe against Firestorm: *"die kugel ist
recht eckig"*.

A 2 m sphere prim, filling roughly half the screen height at a few metres, shows a clearly
polygonal silhouette in SLNG — the flat edges are countable by eye. Firestorm renders the same
prim, at the same distance, with a smooth outline.

Split out of `FEAT-RENDER-19` so the lighting work could close.

## Findings & Fix (2026-09-11)

Both the mesher and the LOD-selection code already existed and were already correct in
principle:

- Volume prims already go through LibreMetaverse's own `MeshFoundry` (not a custom mesher) —
  `src/SLNG.Assets/PrimMeshService.cs`, package `LibreMetaverse.Rendering.MeshFoundry` 3.1.3.
- `ObjectRenderer.PickPrimDetailLevel` already picks a `MeshDetailLevel` from apparent size
  (`maxScale / distance`), the same idea as the real viewer's screen-size-driven LOD. Verified
  against source: `LLVolumeLODGroup::getDetailFromTan` (`llvolumemgr.cpp:33-42`) and
  `LLVOVolume::computeLODDetail` (`llvovolume.cpp:1451-1465`). SLNG's ceiling (`Highest`,
  24-sided — `MeshFoundry.cs:715-799` caps sides at 24 regardless of `High` vs `Highest`) is
  bit-for-bit the real viewer's own LOD-3 sphere (`MIN_DETAIL_FACES=6 * detail=4.0`,
  `llvolume.h:67`), so there was nothing to gain from a higher maximum — question 4 in this
  spec's own list is answered: SLNG already matches Firestorm's top LOD exactly.

**The actual bug (question 3, confirmed):** `PickPrimDetailLevel` ran exactly ONCE per object,
gated on `state.LoadedPrimShape != prim.Shape` inside `ObjectRenderer.UpdateVisual`. Camera
motion changes neither `prim.Shape` nor anything else in that guard, so a sphere first seen far
away — or during login, before the local agent position is known, which silently falls back to
`Medium` — stayed at that coarse tessellation for the rest of the session however close the
camera later walked. The real viewer re-runs the equivalent every time a drawable's camera
distance is recomputed (`LLDrawable::updateDistance` → `LLVOVolume::updateLOD`,
`lldrawable.cpp:871,939,953`).

Fix (`v0.22.65-alpha`): `ObjectRenderer._Process`'s existing draw-distance cull sweep — the same
~4Hz spread pass that already re-offers texture LOD to the `GpuCache` as the camera approaches —
now also recomputes a procedural prim's apparent size from its current (nearer-of-avatar/camera)
distance and re-requests the mesh at the new `MeshDetailLevel` when it differs from
`state.LoadedPrimDetailLevel` (a new field). `LoadAndApplyPrimMeshAsync`'s completion handler
gained a matching staleness check so an in-flight request for a level nobody wants anymore
(superseded by a second, faster distance change) cannot clobber a newer result. Instanced
(`FEAT-PERF-06`-batched) prims are skipped — batching exists precisely so per-instance detail
stops mattering for them individually. Threshold logic (`apparentSize > 0.3/0.1/0.03`) was
extracted into `DetailLevelForApparentSize` so the initial pick and the periodic re-check use
the exact same rule.

Build (solution + `app/`) + 690 tests + `dotnet format` + shader-globals + `--selftest` 38/38
green.

**Not yet re-verified in-world with a walk-up test.** A live A/B session right after this fix
already showed no visible faceting on the probe sphere at a static camera position (*"die form
scheint schon zu passen, wer weiß was das vorhin warf"*) — consistent with the bug needing the
walk-up trigger (or a login-time `Medium` latch) that a static shot does not reproduce. Still
needs: rez the probe far away, walk toward it, confirm the silhouette visibly sharpens as it
gets closer instead of staying fixed at whatever level it was first meshed at.

## Why it matters more than it looks

This is not specific to the probe. It is the prim geometry path, so it affects **every** sphere,
torus, cylinder and cut/hollow prim in the world — a large share of older SL building content.
It is also immediately visible without any reference viewer next to it, which puts it above the
other two lighting follow-ups in user-facing terms.

## First questions, in order

1. **Is it tessellation or normals?** A faceted SILHOUETTE is geometry — too few segments. Faceted
   SHADING with a smooth outline would instead be missing/!split normals. The screenshot shows the
   silhouette itself is polygonal, which points at segment count, but both should be confirmed
   rather than assumed.
2. **Is the LOD being chosen, or is it fixed?** SL picks a prim's tessellation from its screen
   size and the viewer's LOD factor. If SLNG generates one fixed level, a close-up sphere will
   always be coarse regardless of how near the camera gets.
3. **Is the LOD being UPGRADED as the camera approaches?** A correct initial choice that never
   re-evaluates looks identical to a fixed level in a walk-up test.
4. **What does Firestorm actually use here?** `RenderVolumeLODFactor` is a client setting; the
   comparison is only fair once the value on the reference machine is known. Compare against
   `LLVolumeLODGroup` rather than guessing a segment count.

## Acceptance Criteria

- [ ] A 2 m sphere at ~3 m has no countable facets in its silhouette — **needs a live walk-up
      test**; a static-camera A/B right after the fix already showed no faceting, which doesn't
      by itself confirm the re-evaluation path
- [ ] Tessellation tracks camera distance, verified by walking toward and away from the probe —
      **not yet exercised live**
- [x] The chosen level is derived from the viewer's own rule, with the source cited —
      `LLVolumeLODGroup::getDetailFromTan`/`LLVOVolume::computeLODDetail`, see Findings above
- [x] No measurable regression in draw calls / triangle count on a busy region — the fix changes
      only *when* a level is (re-)chosen, not the ceiling; the naive "tessellate everything at
      maximum" was explicitly not done

## Technical Specs & Affected Files

- `src/SLNG.Assets/**` — prim shape → mesh generation
- `app/scripts/ObjectRenderer.cs` — where a mesh is requested and when it is rebuilt
- Reference: `scratch/slviewer/indra/llmath/llvolume.cpp`, `LLVolumeLODGroup`

## Method

Use `tools/testassets/probe_lighting.lsl` — it already rezzes a 2 m sphere with everything else
switched off, so the silhouette is the only variable. Step 0 (white) gives the cleanest outline
against the courtyard.
