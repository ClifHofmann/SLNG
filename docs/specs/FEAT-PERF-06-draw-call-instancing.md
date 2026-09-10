# [FEAT-PERF-06] Draw-call reduction via MultiMesh instancing for repeated static prims

- **Feature ID:** `FEAT-PERF-06`
- **Track:** `render`
- **Status:** `🧪 Review` — P1 shipped on `main` (v0.22.29-alpha, commit d54cc0b); mechanically correct and verified (build/test/selftest), but the headline goal (FPS on the villa scene) was not met — that scene is GPU-fill bound (MSAA), see "In-world result". P1 in-world checks (no visible diff, picking/edit/cull intact) still open.
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## The measurement that opened this

Reported live 2026-09-10 on an OpenSim villa/garden region, **fully streamed in and
static** (nothing left loading), Puris `v0.22.28-alpha`. From the on-screen readout and
`logs/slng-perf.log`:

```
fps=36  medMs=27.8  p99≈42  worstMs≈48
draws≈12150 / frame     tris≈33.8M     ~13500 obj (per pass)
processMs(TIME_PROCESS)=43–50          ← larger than the frame time
VRAM 7.26 GB   (GpuCache only 2.25 GB of it)
[PhaseCost] ms per second of wall clock: cull=47 avatar-render=11 cursor-pick=4.2
            avatar-control=4.1 workqueue=1.4 extrapolate=0.9 world-drain=0.4 terrain=0
```

**Not main-thread bound.** The `[PhaseCost]` line sums to ~68 ms *per second of wall
clock* — under 2 ms per frame of C#. `cull.scan` + `cull.texlod` together are ~2 ms/frame.

**Draw-call bound.** `TIME_PROCESS` (45 ms) exceeding the frame time (28 ms) is the
signature of the main thread stalling in the RenderingServer sync because the render
thread cannot retire the submission in time. 12 150 draw calls for roughly 4–4 500
visible objects — the factor of ~3 is the two directional-shadow splits
(`Parallel2Splits` + `DirectionalShadowBlendSplits`, `Boot.cs`). The 33.8 M
"primitives in frame" is the same geometry submitted ~3× plus MSAA 4× and 4096
soft shadows.

The root cause of the ~4 500 individual calls: **every prim is its own
`MeshInstance3D` and there is no instancing or static batching anywhere in the
renderer** (`grep MultiMesh app/` → nothing). A villa garden is hundreds of identical
lavender plants / bushes / fence segments / roof tiles, each paying a full draw call,
each ×3 for the shadow passes.

The graphics settings that could paper over this (`ShadowDistance`, `ShadowSplits`,
`SmallObjectShadows`, `Msaa` in `GraphicsSettings.cs`) already exist and the user can
turn them down. The missing capability is draw-call reduction itself. This is the
concrete first slice of the long-standing `MVP6-1` ("draw-call reduction on busy
sims").

## Overview & Goal

Render groups of **identical, repeated, static** prims through a single
`MultiMeshInstance3D` per group instead of one `MeshInstance3D` each, collapsing the
foliage / fence / prop fields from thousands of draw calls to dozens. Everything that
needs to address a prim individually — collision, picking, selection, editing, texture
animation, per-object texture LOD — is preserved by keeping the per-object
`StaticBody3D`/`CollisionShape3D` untouched and by *evicting* a prim from its group the
moment it stops being "identical, repeated and static".

Target: a large reduction in `draws=` on the reported scene (expect the instanced
buckets to remove the bulk of the ~4 500 unique objects and their shadow copies), a
corresponding drop in `TIME_PROCESS`, and a visible FPS gain, with **no visible
change** to the scene, picking, selection or editing.

## Constraints that shape the design (Godot `MultiMesh`)

- A `MultiMesh` has **one `Mesh` and one material** for every instance. There is no
  per-instance surface-override material. → a group can only contain prims that resolve
  to the **exact same mesh resource and the exact same single material**.
- Per-instance variation is limited to the instance **transform** and, if
  `use_colors` / `use_custom_data` are enabled, one `Color` + one `Color` of custom
  data readable in the shader as `INSTANCE_CUSTOM`. → per-instance tint is possible but
  is deferred to Phase 2; Phase 1 groups only prims whose material is byte-for-byte the
  same (white/opaque default tint included).
- One `cast_shadow` for the whole `MultiMeshInstance3D`. → the per-object
  small/distant-caster rule (`ObjectRenderer.cs:3207`) becomes a per-group decision,
  evaluated once from the shared mesh's bounding radius.
- `MultiMesh` instances are **not individually frustum-culled** by Godot; the whole
  buffer is drawn if the `MultiMeshInstance3D`'s AABB is visible. This is acceptable
  because a group is by construction many small copies of one asset within one region;
  the win is draw-call count, and per-instance culling of a 200-instance lavender field
  would cost more CPU than it saves. Visibility range / draw-distance is still enforced
  by removing out-of-range instances from the buffer (see below).

## Eligibility — a prim may join a group iff all hold

1. It is a **procedural prim or a mesh/sculpt** that has finished loading and been
   assigned a shared `LoadedMeshKey` (`Guid.Empty` = not ready → not eligible).
2. Its Godot mesh has **exactly one surface** after BUG-RENDER-16's merge
   (`_meshFaceIndices[key].Length == 1`), and that surface's material is a
   `ShaderMaterial` — i.e. no `SetSurfaceOverrideMaterial` fan-out to reproduce.
3. Its material is **shareable**: shader is one of the depth-writing opaque kinds
   (`PrimShaderFamily.Opaque`, `Scissor`, `ScissorEdge`, `Hash`) — never a
   sorted-transparent kind (`IsSortedTransparent` true), because those depend on
   per-object sort depth and the BUG-RENDER-16 split. Its full uniform set (albedo tex
   RID, normal/spec RIDs, tint, alpha cutoff, planar-UV params, fullbright flag, …)
   hashes equal to other members'.
4. It has **no running texture animation** (`UpdateTextureAnimRegistration` inactive
   for this entity) and `SplitChildren == null`.
5. It is **not selected / not hover-highlighted** and **not pinned**
   (`ObjectSelectionController`).
6. It is **within draw distance** and not `ResourcesReleased`.
7. It is **not an attachment** (already excluded from `ObjectRenderer`) and has
   **no `OmniLight3D` / `ObjectParticles` child** (those need the live node; keep them
   standalone — they are rare and not the draw-call problem).

The group key is `(LoadedMeshKey, materialHash, castShadow)`. A group with **≥ 2**
members is realised as a `MultiMeshInstance3D`; a lone member stays a normal
`MeshInstance3D` (no point instancing one).

## Lifecycle

- **Join:** when `UpdateVisual` finishes assigning a mesh+material and the prim passes
  eligibility, register it in `InstanceGroups` under its key. If the group reaches ≥ 2,
  build/extend the `MultiMesh`, append this instance's transform, and set the prim's own
  `state.MeshInstance.Mesh = null` (node stays in the tree for the highlight box and as
  the transform source of truth).
- **Move:** `UpdateVisual` fires on every `ObjectUpdate`. For an instanced prim, skip
  the mesh/material work (unchanged) and just rewrite `multiMesh.SetInstanceTransform(i,
  …)`. Cheap, no allocation.
- **Evict** (any of: selected, hover, texture/material/color edit, texture anim starts,
  surface count changes, scale-driven LOD change, goes out of draw distance,
  `ReleaseResources`): swap-remove the instance from the `MultiMesh` buffer
  (move the last instance into slot `i`, shrink `InstanceCount`, fix the moved prim's
  stored index), and restore `state.MeshInstance.Mesh` to the shared mesh. If the group
  drops to 1, dissolve it (restore the last member too).
- **Destroy:** `RemoveVisual` evicts first, then frees as today.

## Interaction with existing systems

| System | Handling |
|---|---|
| **Collision / picking** | Untouched. `StaticBody3D` + `CollisionShape3D` stay per-object; the selection raycast (`ObjectSelectionController`) reads `LocalId`/`EntityId` metas off the body, never the `MeshInstance3D`. |
| **Selection highlight** | `HighlightVisual` adds a `HighlightBox` child to `state.MeshInstance`, which still exists. Selecting also **evicts** (rule 5) so the real geometry is a normal node again while it is being edited. |
| **Shadows** | Per-group `castShadow` from `BoundingRadius(sharedMesh) >= 0.5f || RenderConfig.SmallObjectShadows`, mirroring `ObjectRenderer.cs:3207`. Small-prop fields therefore stop casting the two shadow-split copies — a large part of the win. |
| **Texture LOD re-offer** (`cull.texlod`) | Still iterates `state.UsedTextureIds` per prim; instanced members re-offer the same id and `GpuCache` dedupes. No change needed, cost unchanged. |
| **BUG-RENDER-16 alpha split** | Sorted-transparent prims are excluded by rule 3; `SplitChildren` by rule 4. The flicker fix is untouched. |
| **Texture animation** | Excluded by rule 4; a script that *starts* an anim on an instanced prim triggers an evict via the existing `UpdateTextureAnimRegistration` path. |
| **`--diag` line** | New `[Instancing]` line on the perf-log timer: `groups=N instances=M biggest=K evicted/s=…`, so the effect is observable while standing still. |

## Affected files

- `app/scripts/ObjectRenderer.cs` — eligibility check, `InstanceGroups` registry, join/
  move/evict/dissolve, hooks in `UpdateVisual`, the cull sweep (`ReleaseResources`,
  visibility), `HighlightVisual`, `RemoveVisual`.
- `app/scripts/ObjectInstanceGroups.cs` *(new)* — the registry + `MultiMesh` buffer
  management (append, swap-remove, index bookkeeping), kept out of the already-3.7k-line
  `ObjectRenderer.cs`.
- `app/scripts/UI/StatsOverlay.cs` — emit the `[Instancing]` perf line.
- `app/scripts/RenderConfig.cs` — `EnableInstancing` (default true).
- `app/scripts/Diagnostics.cs` — parse `--no-instancing`; print the state.
- `app/scripts/Boot.cs` — `AppVersion` bump.
- `tools/run-client.ps1` — `-NoInstancing` switch → `--no-instancing` (the launcher
  only forwards flags it has a typed param for).
- `tests/` — see below.

## Acceptance Criteria

- [ ] On the reported villa scene, `draws=` drops substantially versus `--no-instancing`
      in the same spot (target: at least a third fewer), `TIME_PROCESS` drops with it,
      and median FPS rises. Both numbers recorded in the roadmap entry from
      `logs/slng-perf.log` (`[Instancing]` line shows `drawCallsSaved≈`).
- [ ] No visible difference in the scene with instancing on vs. off (screenshot A/B):
      same geometry, same shadows-where-expected, same textures.
- [ ] Clicking, selecting, box-highlighting, moving and editing an instanced prim all
      still work; a selected prim is visibly a normal node again (evicted) and re-joins
      its group on deselect.
- [ ] Walking away from an instanced field culls it at draw distance exactly as before
      (no instances left drawing past the edge); walking back rebuilds the group.
- [ ] A prim whose script starts an `llSetTextureAnim` leaves its group and animates.
- [x] `dotnet build SLNG.sln` + `dotnet build app/SLNG.App.csproj` + `dotnet test` (683) +
      `dotnet format SLNG.sln` + `check_shader_globals.py` + `--selftest` (38/38) all green.
- [x] `InstanceSlotMap` swap-remove verified through `SelfTest.CheckInstanceSlotMap`
      (app/ has no xUnit project; `--selftest` is the harness app/ code runs). Covers:
      dense indices after a middle removal, the moved-id report, last-entry removal,
      absent-id no-op.

## In-world result (2026-09-10, villa scene, v0.22.29)

P1 works — `[Instancing] groups=218 instances=1330 biggest=110 drawCallsSaved≈1112`
after the `prim_scale` fingerprint fix (a uniform written on every prim from its size but
read only by the planar projection; folding it into the fingerprint split every
slightly-differently-scaled copy into its own group — `biggest` 35 → 110 once it is
neutralised for non-planar faces). **But it did not move the frame rate**, because this
scene is not draw-call bound: `draws` 12135 → ~10030, FPS 36 → 36, `TIME_PROCESS ≈
frame time` with C# phases summing to ~2 ms/frame — GPU-fill bound. The distance
shadow-caster cull (56 m) and CSM 150 → 90 m also gave nothing here (the expensive
casters are the near villa, which fills the frame).

A settings sweep found the actual cost on this GPU:

| change (cumulative) | FPS |
|---|---|
| baseline | 36 |
| SSIL off | 37 |
| + SSAO off | 40 |
| + Glow off | 40 |
| + **MSAA off** (FXAA stays) | **50** |
| + shadow atlas 2048 | 51 |

**MSAA 4× was ~10 FPS by itself.** `project.godot` `msaa_3d` 2 → 1 (4× → 2×);
`screen_space_aa=1` (FXAA) was already on, so edge AA survives at a fraction of the cost.
A saved `settings.cfg` overrides the project default, so an existing user still has to
move the MSAA slider once. SSAO is the second lever (~3 FPS) — a quality selector for it
is a reasonable follow-up.

The instancing + shadow-caster work stays: both are correct, cut draw calls ~15 %, lower
CPU-side submission, and matter more on a busier sim (avatars, denser builds) than on
this GPU-fill-bound one.

## Testing note

`app/` is outside `SLNG.sln` and has no unit-test project, so the pure slot bookkeeping
is checked via `--selftest` (`CheckInstanceSlotMap`) instead of xUnit. The rendering
behaviour (eviction, dissolve, fingerprint stability across a texture upgrade, no visible
diff) is only verifiable in-world — `--no-instancing` is the A/B switch and the
`[Instancing]` perf-sidecar line is the instrumentation.

## Sub-tasks / Progress

- [x] **P1** `ObjectInstanceGroups` registry + `MultiMesh` buffer ops (`InstanceSlotMap`,
      `InstanceGroup` with grow-in-chunks + `VisibleInstanceCount`, swap-remove).
- [x] **P1** `MaterialFingerprint` over the `ShaderMaterial` (shader instance id + every
      uniform; textures by resource RID so a GpuCache in-place re-upload does not evict).
- [x] **P1** Eligibility predicate (`EvaluateInstancing` / `TryBuildInstanceKey`);
      lazy join from the cull sweep, realise at the 2nd member, dissolve at 1; eager
      evict from `ReleaseMeshRef`, `ApplyFaceMaterialsAsync`, `ReleaseResources`,
      `UpdateTextureAnimRegistration`, `HighlightVisual` (+ `_instanceSuppressed`);
      transform push from `UpdateVisual`.
- [x] **P1** `[Instancing]` perf line (5 s cadence); `--no-instancing`; `AppVersion`
      → `v0.22.29-alpha`; local verify green.
- [ ] **P1** In-world A/B on the villa scene: record `draws` / `TIME_PROCESS` / FPS with
      and without `--no-instancing`; confirm no visible diff, picking/selection/edit
      intact, draw-distance cull intact.
- [ ] **P2** *(follow-up, separate commit)* per-instance tint via
      `use_custom_data` / `INSTANCE_CUSTOM` so near-identical prims that differ only in
      face colour also group.
- [ ] **P2** *(follow-up)* `OccluderInstance3D` bake for the solid villa shells
      (occlusion culling) — the other half of `MVP6-1`.
