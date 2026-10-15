# [FEAT-PERF-20] Batch static prims beyond MultiMesh

- **Feature ID:** `FEAT-PERF-20`
- **Track:** `render`
- **Status:** `🚧 In Progress` — Phase 0 (measurement) built in `v0.27.54-alpha`; the Sirens Beach numbers are open (recipe and table below), and they decide Phase A/B.
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md), plan in [MVP6-1-performance-program.md](file:///E:/Git/SLNG/docs/specs/MVP6-1-performance-program.md), [ADR 0004](file:///E:/Git/SLNG/docs/adr/0004-performance-architecture.md) pillar P2
- **Depends on:** FEAT-PERF-06 (MultiMesh groups). **Composes with:** [FEAT-PERF-24](file:///E:/Git/SLNG/docs/specs/FEAT-PERF-24-ecs-renderingserver-prims.md) (RID instances), FEAT-PERF-14 (size-based visibility ranges, spec on `feature/FEAT-PERF-14-visibility-ranges`). **Feeds:** FEAT-PERF-21 (occluders per cell).

## Overview & Goal

At Sirens Beach (2026-10-15: ~19k entities, 29 avatars, separate render thread) a frame takes ~60 ms. The main
thread spends ~20 ms in scripts while the **render thread is the bottleneck**: `renderCpuMs` 33–35 ms, and
`postFlushMs` ~38 ms is the main thread waiting for it. The frame submits **33k draws**: ~20k in the scene pass
and ~13k in the directional shadow pass. The GPU needs ~20 ms. So the cost is in submitting draws, not in
drawing pixels.

Nothing merges these draws today. Godot 4.7 Forward+ merges two draws only if they use the same mesh **and the
same material object** (rules below), and SLNG builds a new `ShaderMaterial` for every prim face. The only
batching is FEAT-PERF-06's explicit MultiMesh groups (~1.6–3.6k objects). FEAT-PERF-14's size cull removes
about 5–9 % of prim draws.

This task decides, **from numbers measured on a real crowded region**, how SLNG cuts its draw count. It then
specifies how to build it. Phase 0 is measurement only; it changes nothing that is drawn.

## Why nothing merges today (Godot 4.7-stable, checked in source)

`servers/rendering/renderer_rd/forward_clustered/`:

- **The merge rule.** `render_forward_clustered.cpp`, `_fill_instance_data`: an element becomes an extra instance
  of the previous draw only if `sort_key1` and `sort_key2` are both equal, the mirror flag is equal, and neither
  element is a multimesh or a skinned `mesh_instance`. Otherwise a new draw is counted
  (`VIEWPORT_RENDER_INFO_DRAW_CALLS_IN_FRAME++`).
- **What the keys hold** (`render_forward_clustered.h`, `GeometryInstanceSurfaceDataCache::sort`):
  - `lod_index`, the soft-shadow / projector / forward-GI / lightmap flags, `depth_layer`
  - `surface_index` and `geometry_id` (the mesh)
  - `material_id` and `shader_id`
  - `priority`

  `SortByKey` compares `sort_key2` (material, shader, priority) first and `sort_key1` (mesh, surface, depth layer)
  second. In the opaque list, identical elements therefore end up next to each other, so **one draw remains per
  distinct (mesh, surface, material, shader, depth layer)**.
- **Depth layer.** `_fill_render_list` computes `depth_layer = CLAMP(int(depth * 16 / (z_far - z_near)), 0, 15)`,
  where `depth` is the distance from the camera to the AABB centre minus `sorting_offset`. SLNG keeps Godot's
  default far plane of 4000 m, so the bands are 250 m deep. Within the draw distance almost every prim is in
  layer 0, which costs almost nothing.
- **The shadow pass merges by the same keys.** `_render_shadow_append` fills the secondary list and sorts it with
  `sort_by_key_range`. An opaque material that qualifies for the shared shadow material
  (`uses_shared_shadow_material`) is drawn with the default material, but its sort key still holds the main
  material's ids. So sharing materials helps the shadow pass exactly as much as the main pass.
- **Passes per material.** A material that writes `ALPHA` goes into the transparent list only: it casts no shadow
  and is sorted by depth, so it practically never merges. `next_pass` materials are added as their own elements
  (`_geometry_instance_add_surface_with_material_chain`). One consequence: BUG-RENDER-16's DepthCore is one more
  transparent-list draw per blended foliage face.

So merging needs three things:
1. a **shared material object**, not just equal values;
2. a **shared mesh resource**. SLNG already shares mesh resources by shape and LOD for prims, and by asset and LOD
   for mesh assets (`AssignSharedMesh`);
3. the **opaque list**. Only opaque and alpha-tested surfaces benefit.

## Which faces really need their own material

Each per-object case listed in the task, checked against the code:

| Case | What it writes, and where | Needs a private material? |
|---|---|---|
| Texture animation | `TickTextureAnimations` → `ApplyTextureAnimFrame` writes the UV uniforms on the face's material every frame | **Yes**, while it runs. Copy on write when the animation starts. The viewer also breaks its batches here (`can_batch_texture`, and `tex_mat` in `registerFace`). |
| Selection | `HighlightVisual` → `SuppressInstancing` + `SelectionOutline`, a separate hull mesh with its own material | **No.** Selection never writes the prim material. An edit arrives as a new face record → a new key. (The tally counts selected faces as private anyway; it is a handful.) |
| Mirror (FEAT-RENDER-22) | `SetActiveMirror` swaps `Shader` on the chosen object's surface materials (`MirrorSurfaces`) | **Yes**, for the one chosen mirror. Copy on write. |
| Alpha-sort hysteresis (BUG-RENDER-16) | `TickAlphaSortHysteresis` writes `SortingOffset` on the **instance**; `SplitSortedSurfaces` moves a surface onto a child instance | **No.** Neither touches a material. The split child can take the shared material. |
| Per-face tint / alpha | Uniforms, from `FaceTexture.Color` | **No.** It is part of the key: different values give different keys, the same values give one shared material. |
| MOAP media | `ApplyMediaImageAsync` swaps `albedo_texture` on the face's material | **Yes.** |
| Alpha decision | `ApplyAlphaCutout` picks Opaque / Scissor / Blend / DepthCore once the texture is there, by changing `Shader` and `next_pass` in place | **No**, but the decision has to belong to the cache entry. It depends only on the texture and the face record, both part of the key, so every face with that key gets the same answer. |
| Late maps | the normal / specular map callbacks write uniforms when their texture arrives | **No**, same as above: they write into the entry, once. |
| Viewer-specular lab toggle | `ApplyViewerSpecularVariant` swaps the shader twin | **No.** It is global, so it is applied per entry. |
| In-place texture sharpen | `GpuCache` calls `SetImage` on the same `Texture2D` | **No.** Textures are already shared. |

## The material key (`SLNG.Core.PrimMaterialKey`, Phase 0)

The key is built from the **inputs**, not read back from the material. Every uniform on a prim material is a
function of three things, all immutable for a given id:
- the face's `FaceTexture`
- the prim scale
- the per-asset data reached through the ids in that record (texture, legacy material, PBR material)

`FaceSurfaceMerge` already relies on this rule inside one object. The key carries it across objects. Reading
the uniforms back costs one interop call each, plus a string per material (`MaterialFingerprint`); the key costs
one struct.

- **Exact, not a hash.** A collision would hand a face somebody else's material, so equality is value equality.
- **Normalised where the renderer cannot tell inputs apart:**
  - the prim scale counts only for planar mapping (FEAT-PERF-06 measured what keeping it costs);
  - texgen is reduced to planar or not (spherical and cylindrical render as default);
  - shiny outside 1–3 counts as none;
  - −0 equals +0.
- **`Variant`** is the renderer's own choice on top of the inputs: the shader in use (base, double-sided and
  viewer-spec twins, the mirror) plus whether a depth pass is chained. Equal inputs with a different variant are
  a different material.
- **Glow** is not in `FaceTexture`, because SLNG does not render it yet. It joins the key when it is rendered.
- **Checked against the real uniforms.** In-world, the line compares up to 48 equal-key pairs per pass by every
  uniform value (`keyCheck=n/m equal`). If a pair disagrees, it prints the first uniform that differs. The
  selftest plants such a difference (a tint the face record does not carry), and the check names `albedo_color`.
- **The check only uses shaders whose uniform list is already cached.** Reading the list the first time is a
  RenderingServer round trip (BUG-PERF-15). Instancing caches it for the opaque shader as soon as a prim joins
  a group, so in practice the check covers opaque pairs. That is enough: the key's logic does not depend on
  the shader.

Tests: `tests/SLNG.Core.Tests/PrimMaterialKeyTests.cs`. Equal inputs give equal keys and hashes. Each of the 16
inputs that reach a uniform separates two keys. Each normalisation above is covered.

## The reference viewer's batching (comparison, not template)

`scratch/slviewer/indra/newview/llvovolume.cpp` and `llspatialpartition.h`:

- **Static geometry is merged per octree node.** `LLVolumeGeometryManager::rebuildGeom` (`:5644`) copies every
  face of a node's drawables into the node's vertex buffers. Limits from `settings.xml`:
  - `RenderMaxVBOSize` 4096 KB per vertex buffer;
  - `RenderMaxNodeSize` 65536 KB per node;
  - `OctreeMaxNodeCapacity` 128 elements per node;
  - `OctreeStaticObjectSizeFactor` 3.

  A static drawable's model matrix is the region's `mRenderMatrix` (`registerFace`, `:5392-5412`), so its faces
  land in shared buffers in region space.
- **Draw calls are `LLDrawInfo`s.** `genDrawInfo` (`:6319`) sorts a node's faces with `CompareBatchBreaker`.
  `registerFace` (`:5336`, condition at `:5490-5505`) then appends a face to the previous draw while all of these
  hold:
  - same vertex buffer, and the face is contiguous with it;
  - same texture, or a free slot in the draw's indexed texture list (`can_batch_texture`, `:5232`);
  - same material id, fullbright, bump, shiny, texture matrix and model matrix;
  - same shader mask, avatar and skin hash.

  `MAX_FACE_COUNT` is 4096 (`:5262`).
- **Movers leave the batches.** A moved drawable becomes ACTIVE (`LLDrawable::makeActive`, `lldrawable.cpp:498`)
  and gets its own render matrix, so it never batches with static geometry. A texture-entry update "breaks
  batches, isolate in octree" (`shrinkWrap`, `llvovolume.cpp:537`, `:2326`). Texture animation and media break
  batches too (`can_batch_texture`).
- **Alpha keeps per-face order.** The alpha faces get a distance-sorted `genDrawInfo`.

The viewer is therefore **option B**: merged geometry per spatial node, batched by material. On top it uses
indexed texture batching, which Godot's one-material-per-surface model cannot express without a texture-array
shader (out of scope). Two differences: the viewer's node is an octree node sized by its contents, not a fixed
grid; and a whole node rebuilds when any member changes.

## The options

### A — shared-material cache, copy on write

- A main-thread `PrimMaterialCache` maps the **input** key (face, planar scale) to an entry. The entry holds:
  - one `ShaderMaterial`;
  - a ref count;
  - the decisions made once its textures have arrived: shader variant, depth pass, late maps.
- `BuildFaceMaterialAsync` becomes "acquire": a hit returns the entry's material; a miss builds as today and
  registers the result. Entries die with their last user, together with their texture refs. The cache is
  reached through `UsedTextureIds`, which already counts refs per object.
- The **copy-on-write** writers are texture animation, the hero mirror and MOAP. When one starts it takes
  `Duplicate()`; when it stops it returns to the shared entry. **Every in-place writer must go through the
  cache.** The table above is the complete list today, and a grep for `SetShaderParameter` / `.Shader =` /
  `NextPass` on prim materials guards it.
- Godot then auto-instances equal (mesh, surface, material) elements in the opaque and shadow lists. Nothing
  else changes: one node per prim, picking, selection, LOD swaps, FEAT-PERF-06 groups.
- Side wins: far fewer material objects and uniform buffers, and `mat.params`-type main-thread work done once
  per key instead of once per face.
- **Limit:** it merges only draws of the same mesh. A box prim, a cylinder at one LOD, or one mesh asset
  repeated in a build all merge. Unique meshes do not. The line's `shared` column measures exactly this.
- **Risk:** BUG-RENDER-17 found alpha-discard materials rendering wrong under a MultiMesh. Auto-instancing uses
  the same instance-buffer path, so Phase A1 must show the "Blendable Trails" object (or a scissor probe) under
  auto-instancing. If it fails, Masked surfaces keep their own material.

### B — per-cell merged static meshes on a worker

- **Build.** For each (cell, material key, shadow flag), a worker builds one `ArrayMesh` from the members'
  `MeshData` × transform. That CPU data is already held (`LoadedMeshData`), so there is no read-back. The main
  thread commits it as one instance and stops drawing the members' own instances.
- **Eligible:** static (no move or turn for 30 s), not physical, opaque or alpha-tested, no private-material
  reason. Not instanced by FEAT-PERF-06: a group of identical meshes is cheaper as a MultiMesh than as copied
  vertices.
- **Picking and selection.** `StaticBody3D` and collision stay per prim, so raycast picking is unchanged.
  Selecting a member takes it out of its cell. The outline is its own hull mesh.
- **Per-face updates and moves.** The member is taken out at once. Its vertex range in the merged surface is
  collapsed with `RenderingServer.MeshSurfaceUpdateVertexRegion` (a write, no read-back) and its own instance is
  shown again. The compacting rebuild is debounced (2 s) on a worker. The viewer rebuilds the whole node instead;
  collapsing avoids a ghost or a z-fight while the rebuild runs.
- **LOD.** Members are built at the LOD band of the cell's centre. When the camera crosses a band (with
  hysteresis), the cell is re-queued. Option: HLOD, with the merged cell beyond distance D and the members within
  it, via `InstanceSetVisibilityParent`.
- **Shadows.** One shadow flag per merged mesh, so casters and non-casters are separate meshes (the tally counts
  them that way). The distance shadow cull (`ShadowCasterDistance`) becomes per cell.
- **Draw distance and VRAM.** The cell is shown or hidden as a whole. It holds the texture refs of its members'
  materials. Release and reload stay per object for textures.
- **Culling gets coarser.** A 64 m cell draws all its triangles when any part of it is on screen: less CPU,
  more GPU. 32 m against 64 m is measured, not guessed.
- **Memory.** Merged vertices are copies. A build that repeats one 2k-vertex mesh 500 times costs 500 copies.
  This is why repeats stay in MultiMesh groups. Phase B0 measures the vertex bytes per cell.
- **Alpha-blended surfaces never go into a cell** (per-object sort, BUG-RENDER-16).

### C — both

A first, then B for the static opaque part. Under C, a cell only has to catch what A leaves: different meshes
with the same material. Shared materials also make the cell key cheap, because it becomes the entry identity.

### How each composes with FEAT-PERF-24 and FEAT-PERF-14

| | A (shared materials) | B (merged cells) |
|---|---|---|
| **FEAT-PERF-24** (static prims as RID instances) | Works unchanged: an instance takes the shared material's RID (`InstanceSetSurfaceOverrideMaterial`). Independent of the RID backend, can ship first. | Much simpler with RIDs. "Drawn by" is one enum (`Own / Group / Cell / Hidden`) on the ECS side instead of nulling and restoring `node.Mesh`. FEAT-PERF-24's go/no-go rule already counts "the maintainer schedules FEAT-PERF-20" as a go. |
| **FEAT-PERF-14** (engine visibility ranges by size) | Orthogonal. Ranges stay per instance; the engine culls first and the survivors are merged. | A merged cell has one range, so a small prim inside it loses its own cull. **Prims that FEAT-PERF-14 gives a range** (radius < 1.5 m, about 21–23 % of prims under 0.5 m in the cached regions) **stay out of cells** and keep their instance and range. Alternative: a second, short-range "detail" mesh per cell. |
| **FEAT-PERF-06** (MultiMesh groups) | A shared material makes the group fingerprint (`MaterialFingerprint.Of`, a string of every uniform) obsolete: the entry *is* the fingerprint. | Exclusive: a prim is drawn by its own instance, a group, or a cell, never two. Groups also need a cell key for culling (FEAT-PERF-24 spec). |

## Recommendation

**Start with A (Phase A1), and decide B from the Sirens numbers.**

- A is the only option that is local (one cache in `BuildFaceMaterialAsync`, plus copy on write in three
  writers). It keeps every per-prim behaviour (picking, LOD, culling, alpha sort). It composes with
  FEAT-PERF-24 and FEAT-PERF-14 without design work, and it helps the shadow pass as much as the scene pass.
  The Phase 0 line predicts its result before it is built (`shared`).
- B is the reference viewer's answer and the only one that merges *different* meshes. It costs:
  - a builder and a commit budget;
  - a rebuild policy and vertex memory;
  - per-member LOD;
  - coarser culling;
  - a clash with FEAT-PERF-14's per-object ranges.

  It is worth that only if the line shows a large gap between `shared` and `both32`/`both64`. It should land on
  FEAT-PERF-24's RID instances, not on nodes.
- **Decision rule**, applied to the Sirens line, main and shadow added (with the cascade factor below):
  - **A is worth building** if `shared ≤ 0.8 × now`, i.e. at least a 20 % cut in prim draws.
  - **B (as C) is worth building** if `min(both32, both64) ≤ 0.75 × shared`, i.e. at least another 25 % on top
    of A. **And** Phase B0's vertex-memory estimate stays under ~300 MB at the chosen cell size.
  - **Cell size:** 32 m if `both32 − both64` is under 10 % of `both32` (finer culling, cheaper rebuilds);
    otherwise 64 m.
  - If neither holds, the draw count is dominated by unique meshes with unique materials. The levers are then
    culling (FEAT-PERF-14, FEAT-PERF-21 occluders) and LOD, not batching. Record that here and close this task.

## Phase plan

| Phase | Work | Measurable acceptance |
|---|---|---|
| **0** (this task, v0.27.54) | `[BatchPotential]` line; `PrimMaterialKey` and `BatchPotentialTally` in Core with tests; selftests (hand-counted scene + cost bench). No render change. | Line printed at Sirens (table below). Cost ≤ 0.5 ms/frame: the line's own `avgMs`/`worstMs` and `diag.batch` in [WorkCost] (headless: worst slice 0.421 ms). `keyCheck` ≥ 95 % equal; otherwise fix the key before Phase A. |
| **A1** | `PrimMaterialCache` with copy on write for texture animation, mirror and MOAP. Behind `RenderConfig.ShareMaterials` / `--share-materials`, default off. | Same spot, A/B: `drawsScene + drawsShadow` within ±10 % of what Phase 0 predicted; `renderCpuMs` down. No visual difference on the known-answer set (texanim board, mirror, MOAP face, Blendable Trails path, a selected and edited prim, blended foliage). New selftests: equal faces share one material; a texanim face detaches and returns. |
| **A2** | Default on; FEAT-PERF-06 groups key on the entry instead of `MaterialFingerprint`. | A week of `[Perf]` logs without a regression report; instancing join cost down in [WorkCost]. |
| **B0** (gate for B) | Extend the line: vertex bytes per cell configuration, prims excluded by FEAT-PERF-14 ranges, member churn (moves and retextures per minute per cell). Measure at Sirens plus one prim-heavy older build. | The decision rule above, written into this spec with the numbers. |
| **B1** | Cell builder on a worker; time-sliced commit; collapse-on-leave; debounced rebuild. On FEAT-PERF-24's RID instances. Behind a setting. | Draws within ±10 % of the `both` prediction. Commit ≤ 1 ms main-thread slice. Worker rebuild p95 reported. Picking, selection and edit selftests unchanged. VRAM delta reported. |
| **B2** | LOD bands or HLOD per cell; shadow distance per cell; FEAT-PERF-21 occluders from the same cells. | Same-spot A/B against B1; no LOD pop beyond today's. |

## Measuring at Sirens Beach (the open part of Phase 0)

1. `tools/run-client.ps1 -Diag` (or start the client with `--batch-potential` for the line without the rest of
   `--diag`).
2. Stand still, and do not turn the camera, for about a minute at the usual Sirens spot. A pass measures
   against the camera as it was when the pass started. A pass costs ~12 µs per visual, in slices of 0.4 ms
   per frame: about 10k visuals at 16 fps is ~120 ms of work, spread over ~20 s. The line prints its own
   `pass=` duration, and a new pass starts at most every 10 s.
3. Copy from `user://logs/slng-perf.log`:
   - one `[Perf]` line (for `drawsScene`, `drawsShadow`, `renderCpuMs`);
   - the `[BatchPotential]` lines next to it;
   - `[Instancing]`;
   - `[WorkCost]` (for `diag.batch`).
4. Repeat at a second spot facing a different direction.

### The line

`[BatchPotential] objs=… (instanced=… animesh=… unloaded=… other=…) | main: surf now shared cell32 cell64 both32 both64 | keys private (anim sel mirror media) blend depthPass hidden static | shadow(1 view): surf now shared cell32 cell64 both32 both64 | keyCheck=n/m equal | pass slices avgMs worstMs`

- **Which visuals count:** shown, non-instanced prims. MultiMesh members are counted in `instanced`; their draws
  are in [Instancing].
- **main:** surfaces whose bounding sphere was in the camera frustum at the start of the pass.
  - `surf`: draw elements, i.e. surfaces plus chained depth passes;
  - `now`: today's draws after Godot's own merge;
  - `shared` (A), `cell32`/`cell64` (B), `both32`/`both64` (C).
- **shadow(1 view):** every caster, on screen or not, minus blended surfaces. A directional light draws a caster
  once per cascade it falls into. So compare `drawsShadow / shadow.now` (the cascade factor), and scale the
  shadow columns by it.
- **Summary columns:**
  - `keys`: distinct shareable materials on screen;
  - `private`: faces that must keep their own material, split by reason (anim, sel, mirror, media);
  - `blend`, `depthPass`: never batched;
  - `hidden`: BUG-RENDER-16 placeholders. Each is still a draw in both passes — and a shadow draw, because the
    parent casts. Cheap to remove separately.
  - `static`: surfaces a cell would take.

### Results (to be filled in from the user's run)

| Measure | Sirens spot 1 | Sirens spot 2 | Notes |
|---|---|---|---|
| `[Perf]` drawsScene / drawsShadow | | | |
| `[Perf]` renderCpuMs / postFlushMs / fps | | | |
| `[Instancing]` grouped objects / groups | | | |
| objs / instanced / animesh | | | |
| main surf / now | | | `now` vs `drawsScene`: how much of the scene is prims |
| main shared (A) | | | |
| main cell32 / cell64 (B) | | | |
| main both32 / both64 (C) | | | |
| keys / private (anim, sel, mirror, media) | | | |
| blend / depthPass / hidden / static | | | |
| shadow surf / now | | | cascade factor = drawsShadow ÷ now |
| shadow shared / cell32 / cell64 / both32 / both64 | | | |
| keyCheck | | | must be ≥ 95 % equal |
| pass s / avgMs / worstMs; `diag.batch` | | | cost check, ≤ 0.5 ms/frame |
| **Decision** (rule above) | | | |

### Headless numbers (selftest, this machine, not a region)

- **The hand-counted scene** (6 prims × 2 surfaces; P4 selected; P5 half blended; P6 out of view in the next
  32 m cell) prints:

  `main: surf=10 now=10 shared=5 cell32=4 cell64=4 both32=4 both64=4 | keys=2 private=2 (sel=2) blend=1 static=7 | shadow(1 view): surf=11 now=11 shared=4 cell32=4 cell64=3 both32=4 both64=3 | keyCheck=6/8 equal`

  The two disagreeing pairs are the planted tint, named `albedo_color`.
- **The cost bench** (1,500 prims × 6 surfaces, 48 materials, real 0.4 ms slices, after a warm-up pass):
  - 12.0 µs per visual (2.0 µs per surface);
  - the pass took 18.0 ms of main thread in 45 slices, avg 0.401 ms, worst 0.421 ms;
  - all 48 key checks equal;
  - `now=1782 shared=48 both32=708` for the ~1/5 of the field in view.

## Limits of the estimate

- **Frustum:** a bounding sphere at the object's position, against the frustum taken once at the start of the
  pass. Godot tests the instance AABB, so `main` is a slight overestimate, and a moving camera mixes views.
- **Depth layer:** from the object's position, not its AABB centre, and without `sorting_offset`. Within 250 m
  bands this does not change the count.
- **Ignored per-instance sort-key flags:** soft-shadow and projector lights affecting an instance. Instances lit
  by different such lights would not merge.
- **Cell membership** is counted from on-screen members only. A merged cell's AABB is larger than its on-screen
  members, so B's `main` counts are a lower bound.
- **Static** means "no move or turn for 30 s, not physical", from the managed position copy. SLNG does not render
  `llTargetOmega` spin, so spinning objects count as static.
- **Shadow** counts one view, with no depth layer.
- **Excluded:** avatars, attachments, terrain, water, sky and particles. Those are the rest of `drawsScene`.
- **The first slice of a session** pays the JIT once (5.7 ms in the headless scene). Every slice after that
  keeps to the budget.

## Technical Specs & Affected Files (Phase 0)

- `src/SLNG.Core/PrimMaterialKey.cs`: the key.
- `src/SLNG.Core/BatchPotentialTally.cs`: the incremental tally (Godot's merge rule as arithmetic).
- `tests/SLNG.Core.Tests/PrimMaterialKeyTests.cs`, `tests/SLNG.Core.Tests/BatchPotentialTallyTests.cs`.
- `app/scripts/ObjectRenderer.BatchPotential.cs`: the sampler. It has its own walk, separate from the cull sweep:
  - a time budget of 0.4 ms per frame (the clock is read before every visit, so the worst slice is the budget
    plus one visit), one pass per 10 s at most;
  - scene-side reads only: no RenderingServer getters, no read-backs (BUG-PERF-15);
  - a sampled key check.
- `app/scripts/ObjectRenderer.BatchPotential.SelfTest.cs`, `app/scripts/SelfTest.BatchPotential.cs`: hand-counted
  scene + cost bench.
- `app/scripts/ObjectRenderer.cs`:
  - `VisualState.LastMovedAt` / `PlacedOnce` / `PlacedRotation`, written in `UpdateVisual`;
  - the `TickBatchPotential` call in `_Process`.
- `app/scripts/RenderConfig.cs` (`BatchPotential`), `app/scripts/Diagnostics.cs` (`--batch-potential`, implied by
  `--diag`).

## Acceptance Criteria

- [x] Phase 0: `[BatchPotential]` line, off by default, behind `--diag` / `--batch-potential`, ≤ 0.5 ms/frame by
  construction (time-sliced, reports its own cost)
- [x] Material key in `SLNG.Core`, tested (equal inputs → equal key; every uniform input separates keys)
- [x] Headless selftests: hand-counted scene, cost bench
- [ ] Sirens Beach numbers in the table above, and the decision written down
- [ ] Phase A1 / B per the plan, each with its acceptance

## Sub-tasks / Progress

- [x] Phase 0 measurement (v0.27.54-alpha)
- [ ] Sirens measurement (user, needs a login)
- [ ] Phase A1 shared-material cache
- [ ] Phase B0 gate measurements
- [ ] Phase B1/B2 merged cells (after FEAT-PERF-24 Phase 2, if the gate passes)
