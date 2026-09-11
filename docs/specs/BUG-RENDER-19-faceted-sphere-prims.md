# [BUG-RENDER-19] Sphere prims render visibly faceted

- **Feature ID:** `BUG-RENDER-19`
- **Track:** `render`
- **Status:** `⏸️ Pending`
- **Owner:** *(unassigned)*
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Reported live 2026-09-11 while A/B-ing the lighting probe against Firestorm: *"die kugel ist
recht eckig"*.

A 2 m sphere prim, filling roughly half the screen height at a few metres, shows a clearly
polygonal silhouette in SLNG — the flat edges are countable by eye. Firestorm renders the same
prim, at the same distance, with a smooth outline.

Not investigated at all yet; split out of `FEAT-RENDER-19` so the lighting work could close.

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

- [ ] A 2 m sphere at ~3 m has no countable facets in its silhouette
- [ ] Tessellation tracks camera distance, verified by walking toward and away from the probe
- [ ] The chosen level is derived from the viewer's own rule, with the source cited
- [ ] No measurable regression in draw calls / triangle count on a busy region (a naive fix here
      is "tessellate everything at maximum", which trades this bug for `FEAT-PERF-06`'s)

## Technical Specs & Affected Files

- `src/SLNG.Assets/**` — prim shape → mesh generation
- `app/scripts/ObjectRenderer.cs` — where a mesh is requested and when it is rebuilt
- Reference: `scratch/slviewer/indra/llmath/llvolume.cpp`, `LLVolumeLODGroup`

## Method

Use `tools/testassets/probe_lighting.lsl` — it already rezzes a 2 m sphere with everything else
switched off, so the silhouette is the only variable. Step 0 (white) gives the cleanest outline
against the courtyard.
