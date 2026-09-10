# [BUG-RENDER-16] High-frequency foliage: soft edge vs. flicker

- **Feature ID:** `BUG-RENDER-16`
- **Track:** `render`
- **Status:** `🧪 Review` (interim ship: `blend` default; real fix — foliage surface merge — not started)
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Symptom

Reported live 2026-09-10 on SL region *Millenium*, dense ornamental grass (pink
plumes + green blades), Firestorm side-by-side.

- With BUG-RENDER-11's shipped behaviour (undeclared alpha → hard `Scissor` cutout),
  the grass renders as flat, hard-edged, blocky pink shapes. The thin low-alpha
  green blades are cut away entirely, so the field reads far too pink and the
  feathered silhouette is gone. Firestorm shows soft, translucent blade tips and
  keeps the green.
- Routing the same faces to `Blend` restores the Firestorm look exactly — but the
  grass then **flickers under camera motion**: chunks swap draw order as the camera
  orbits. This is BUG-RENDER-11's original complaint, for the subset it deliberately
  traded away.

## Root cause

Two separate limits, and no shader mode escapes both:

1. **Depth-writing / opaque-queue modes never flicker but cannot make a soft edge.**
   `Scissor` is a binary test; `alpha_to_coverage` over the project's 4× MSAA gives
   only ~4 coverage levels, so a wispy tip stair-steps. `Hash` (BUG-RENDER-09's
   stochastic cutout) writes depth like opaque so it never sorts, but resolves the
   gradient as a per-pixel dither that reads as crunchy speckle.
2. **Blended modes make the correct soft edge but flicker.** Godot's transparent
   queue sorts **per surface** by AABB-centre distance — far coarser than the
   viewer's per-draw sort — so dense overlapping foliage at similar distance swaps
   order on tiny camera moves. `BuildArrayMesh` emits **one Godot surface per SL
   face**, so a multi-blade grass mesh hands the sorter N independently-reorderable
   pieces (same shape as BUG-RENDER-12's hair: "one Godot surface per SL face handed
   the sorter 18 reorderable pieces of one authored hair stream").

Firestorm is stable because its sort granularity is finer, which — per BUG-RENDER-11
and BUG-RENDER-12 — **no shader mode fixes**.

## What was tried (all in-world, `--foliage-alpha=<mode>`, v0.22.9–v0.22.16)

| mode | edge | flicker | verdict |
|---|---|---|---|
| `scissor` (BUG-RENDER-11) | hard, blocky, eats the green | none | rejected: wrong look |
| `blend` | **soft, matches Firestorm** | **yes** | correct look, flickers |
| `hash` (`ALPHA_HASH_SCALE` 1→4) | crunchy dither / "zu scharf" | none | rejected: grainy |
| `prepass` (`depth_prepass_alpha`) | soft | yes | Godot's prepass α-threshold is ~0.99 — mid-alpha grass never enters the depth prepass, so it still sorts |
| `edge` (`ALPHA_ANTIALIASING_EDGE`) | still hard | none | still 4 MSAA coverage levels; the built-in doesn't help enough |
| `blenddepth` (`depth_draw_always`) | soft | **strong** | render order = depth-write order, so ties flip harder — worse |
| + **TAA** (`use_taa`) on `hash` | still grainy | none | Godot's alpha hash is stable per surface point, not temporally jittered, so TAA has nothing to average when static |
| + **FXAA** (`screen_space_aa=1`) on `hash` | still grainy | none | softens a little, not enough |

## Interim decision (this branch, `v0.22.16-alpha`)

- **`RenderConfig.HighFrequencyFoliageAlpha` defaults to `Blend`.** It is the only
  mode whose texture the user accepted as matching Firestorm; the flicker is a known
  Godot-sort limitation and shipping the correct look with it beats a permanently
  degraded edge. `Scissor` for the same subset (BUG-RENDER-11) was rejected in-world.
- The other five modes stay wired, reachable via `--foliage-alpha=` /
  `tools/run-client.ps1 -FoliageAlpha`, as the A/B harness the real fix needs.
- `--foliage-hash-scale=N` tunes `ALPHA_HASH_SCALE` for the `hash` mode.
- `project.godot` gains `anti_aliasing/quality/screen_space_aa=1` (FXAA) — kept as a
  reasonable default; it is cheap and has no ghosting, unlike TAA (which was tried
  and reverted).
- Clean cutouts (`maskable == true`: fences, sharp leaf cards) and `fullbright`
  faces (BUG-RENDER-14 flames) are **never** re-routed — unchanged.

## The real fix (not started)

Merge a foliage mesh's faces that resolve to an **identical material** (same
texture, tint, UV transform, shader kind) into **one Godot surface**, so the
transparent sorter sees one sortable piece per plant instead of N. This is the
BUG-RENDER-12 approach ("ten shader experiments could not" fix it; merging same-
material consecutive faces did). Stable under pure rotation; only reorders when the
camera actually crosses a plant.

Design points to settle:

- Material equality is only known **after** `BuildFaceMaterialAsync` (UV
  offset/repeat, tint, legacy/glTF alpha mode). Either resolve materials before
  meshing, or merge in a second pass once materials are known and rebuild the
  `ArrayMesh` / `_meshFaceIndices` mapping.
- `_meshFaceIndices` / `_meshFaceMaterialKeys` currently assume surface ⇔ face 1:1
  (see their doc comments); the merge breaks that and both consumers
  (`ApplyFaceMaterialsAsync`, the per-frame texanim reapply) need the new mapping.
- Interaction with LOD swaps and with `RenderConfig` draw-call batching.
- Scope to foliage / `Blend`-kind faces first; a general merge is larger.

Candidate owner: `graphics-engineer`.

## Acceptance Criteria

- [ ] Dense grass renders with Firestorm-soft edges **and** no flicker under pure
      camera rotation, A/B'd on *Millenium*.
- [ ] No regression on fences / sharp leaf cards (`maskable`) or flames
      (`fullbright`).
- [ ] No regression to per-face texture animation or LOD swaps after the surface
      merge.
- [ ] Build + `dotnet test` + `dotnet format` + shader-globals + selftest green.

## Affected Files

- `app/scripts/RenderConfig.cs` — `FoliageAlpha` enum, `HighFrequencyFoliageAlpha`
  (default `Blend`), `FoliageHashScale`.
- `app/scripts/Diagnostics.cs` — `--foliage-alpha=` / `--foliage-hash-scale=` parse.
- `app/scripts/ObjectRenderer.cs` — `ApplyAlphaCutout` mode switch; `PrimShaderKindName`.
- `app/scripts/PrimShaderFamily.cs` — `BlendPrepass`, `ScissorEdge`, `BlendDepth`
  variants + `AlphaEdge` uniform name.
- `app/materials/prim/prim_blend_prepass.gdshader`, `prim_scissor_edge.gdshader`,
  `prim_blend_depth.gdshader` — new variants.
- `app/project.godot` — `screen_space_aa=1`.
- `tools/run-client.ps1` — `-FoliageAlpha`, `-FoliageHashScale`.
- `app/scripts/Boot.cs` — `AppVersion` → `v0.22.16-alpha`.

## Sub-tasks / Progress

- [x] Diagnose (both limits), build the 6-mode A/B harness, run it in-world.
- [x] Interim: ship `Blend` default + FXAA; document the dead ends.
- [ ] Real fix: same-material foliage face → single surface merge.
- [ ] Re-verify in-world; then this can go `✅ Done`.
