# [FEAT-PERF-23] Outfit merge: one skinned character per avatar

- **Feature ID:** `FEAT-PERF-23`
- **Track:** `render`
- **Status:** `⏸️ Pending`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md), [ADR 0004](../adr/0004-performance-architecture.md)
  pillar P3, [MVP6-1](MVP6-1-performance-program.md)
- **Depends on:** BUG-PERF-15 (no read-backs, done), BUG-PERF-16 (park instead of unload, in progress)

## Overview & Goal

Today every worn rigged prim is its own `MeshInstance3D` with its own `Skin`. A mesh body is dozens
of skinned instances, and one avatar is about 150 of them.

This feature merges an avatar's worn rigged meshes on a worker whenever the outfit changes:

- one `Skin` per avatar, holding the union of all binds;
- opaque and alpha-scissor surfaces merged per material;
- sorted-alpha surfaces kept as separate instances (BUG-RENDER-16/38), sharing that one `Skin`.

The main thread only commits the finished result (ADR 0004 P1). The feature lands behind a setting,
off by default. It replaces the old path only after an in-world A/B, the way FEAT-PERF-13 did.

**Before any of it is built, a measurement probe (Phase 0) has to show that instances are what
costs.** Two facts make that a real question:

- Cutting skin binds from 306k to 47k did not move `postFlushMs` (MVP6-1 baseline).
- Avatar skinned instances are about 3 % of the scene's ~56k nodes.

The merge is expected to cut draws, render-thread CPU, GPU skinning dispatches and the cost of
parking an avatar. It is not expected to fix the 57k-node flush; that is P2.

## The current pipeline (what the merge has to keep working)

Line numbers refer to commit `405fa3e5` (v0.27.31). BUG-PERF-16 is editing `AvatarRenderer.cs`
right now, so they will drift; the method names are the stable reference.

| Step | Where | What it does |
|---|---|---|
| Request | `AvatarRenderer.cs:3368-3391` (`ApplyAttachmentMeshDataAsync`) | A rigged mesh goes to `RequestRig`. Anything else becomes a static `AttachMesh` under a `BoneAttachment3D`. |
| Newest request wins | `AvatarRenderer.cs:317`, `RigWorker.cs:76`, `RigWorker.cs:151-207` | One prepare in flight per entity. Claim order: self first, then distance, hidden avatars last. |
| Worker prepare | `RigWorker.cs:209-250`, `:346-410` | Joints are resolved against the skeleton definition. `RiggedMeshBuilder.Build`, MikkTSpace tangents (`:420-437`) and pick chunks are all made here. |
| Vertex loop | `src/SLNG.Assets/RiggedMeshBuilder.cs:46` | The bind-shape matrix is baked into positions and normals (`:58-70`, `:142`). Invisible faces are dropped (`:123`). Consecutive equal faces become one surface (`:72`, BUG-RENDER-12). Weights are normalised to 4 influences (`:154-160`). Bad joints are clamped or remapped to slot 0 (`:252`, `:262`). |
| Weights | `src/SLNG.Assets/MeshSkinWeightDecoder.cs:46-49` | SLNG's own 4-weight reader. LibreMetaverse mis-parses the 4-influence case. |
| Main commit | `AvatarRenderer.cs:3404-3513` (`CommitPreparedRig`) | Steps in order: route an animesh away (`:3428`); discard the old instance (`:3436`); apply joint overrides (`:3456`); bind a new `Skin` (`:3459`, `BindRiggedSkin` `:5608`); `AddSurfaceFromArrays` (`:5699-5781`); `SortingOffset` tie-break (`:3484`); add to the skeleton; pick colliders (`:3507`); BoM registration (`:3508`); face materials (`:3511`). |
| Materials | `AvatarRenderer.cs:3566-3630` | One material per surface, built off the main thread (`BuildFaceMaterialAsync`, `:3820`). Its Kind (Opaque, Scissor or Blend) is only known once the texture's alpha stats exist (`ClassifyAlpha`, `:4277`). |
| Sort split | `AvatarRenderer.cs:3747-3804` | Once all materials are in (`:3623-3629`), every Blend surface of a mesh with two or more of them moves onto its own child instance. It shares the parent's `Skin`, sits at `SortingOffset + (s+1)·1e-5`, and the parent draws that surface with `Hidden`. |
| All-hidden mesh | `AvatarRenderer.cs:3660-3709` | A mesh with no drawn surface is hidden, and its skeleton path is cleared so Godot stops writing its binds. |
| Joint overrides | `:5037-5241`, `:5264-5284`, `:5333-5349` | Positions, `lock_scale_if_joint_position` (`:5077`, `:5137`) and pelvis fixups (`:5229-5240`) are kept per mesh in `JointOverrideSet`. `RebuildSkeletonAfterJointChange` (`:5366-5379`) re-binds every `Skin`. |
| Shape change | `:1793-1804` → `RebuildRiggedAttachmentSkins` `:6087-6101` | Builds a new `Skin` per worn mesh, because own-scale is baked into each bind (`InjectOwnScale`, `:2176`). |
| BoM | `:3921-3928`, `:4330-4440`, bake arrival `:1747-1752` | A magic id is resolved to the wearer's bake. When a new bake arrives, the materials of the meshes in `BomAttachments` (keyed by `MeshInstance3D`) are rebuilt. |
| LOD | `:2682-2725`, `:2643-2660`, dedupe `:2459-2471` | The LOD is picked at load and only ever raised. A raise re-fetches and re-rigs the item. |
| Cap | `SetAvatarReduced` `:6661-6737`, `SetAvatarFull` `:6739-6768`, `EvaluateAvatarLimit` `:6779` | A reduced avatar discards every rig and texture pin. Promotion re-requests every item. |
| Picking | `:2933-3012`, `:3033-3084`, chunks `:3098-3132` | Self and control avatars only (`:3036`). Up to 8 per-bone colliders per item, one per queue item. Validity check: `_riggedAttachments[entity] == Mi` (`:3067`). |
| Outline | `:2814-2871`, `SelectionOutline.cs:225-305` | A rigged item's outline uses **the instance's whole mesh** (`SelectionOutline.cs:244`), skinned with the same `Skin`. |
| Animesh | `AvatarRenderer.WornAnimesh.cs:104-136` | A worn animated object is skinned to its own control avatar, never to the wearer. |
| Cost line | `ReportAvatarCost` `:6875-6939` | `[AvatarCost]` counts skeleton children and grandchildren. |

`_riggedAttachments` (`:254`, one instance per entity) is read by every cleanup path:

- `:527` `OnEntityRemoved`
- `:876` `RemoveVisual`
- `:2851` highlight
- `:3067` pick validity
- `:3191` discard
- `:4650` move to a HUD point
- `:6682` reduce

The merge changes what this map means. That is the riskiest part of the work.

## Design

### Terms

| Term | Meaning |
|---|---|
| Item | One worn rigged prim (entity) skinned to the wearer. |
| Linkset | A worn object: its root prim plus children (`WornLinksetRoot`, `:2896`). SL attaches and detaches whole linksets. |
| Ready item | An item with prepared geometry and every face material built and classified. |
| Bucket | One merged `MeshInstance3D` holding the Opaque and Scissor surfaces of one linkset (Phase 1), one surface per material key. |
| Sorted instance | One `MeshInstance3D` per Blend surface. |
| Outfit skin | The avatar's single `Skin`, shared by every bucket and sorted instance. |
| Staging | Today's per-item path (own instance, own `Skin`), unchanged. Used for items that are not, or not yet, mergeable. |

### Flow

1. **Unchanged up to the item commit.** `UpdateAttachment` → mesh fetch → `RequestRig` → worker
   prepare → `CommitPreparedRig`. Joint overrides go on at this commit, at the same moment as today.
2. **The item becomes ready** once its materials are all in: the `avatar.split_sorted` item at
   `:3623`, which already runs after the last face material. At that point:
   - its material keys and Kinds are recorded (see *Material key*);
   - its linkset's bucket is marked dirty.
3. **A merge job runs on a worker,** at most one per avatar at a time (the `_rigsPreparing` pattern).
   It takes immutable snapshots of the dirty buckets' ready items and the outfit skin table, and
   returns:
   - per bucket, the concatenated arrays for each material key, with bones remapped to outfit slots
     and indices offset;
   - one array set per Blend surface, with its `SortingOffset`;
   - the new slot keys;
   - per-item vertex ranges and stats.
4. **The main thread commits,** within the work-queue budget:
   - new binds onto the outfit skin;
   - `AddSurfaceFromArrays` into a new `ArrayMesh`, which stays off the tree until complete;
   - one swap item: the bucket and sorted instances go in, the absorbed staging instances come out.
     The two never show together and never are both missing.
5. **Generation check.** Like `CommitPreparedRig` (`:3406-3408`), the result is dropped when any of
   its input items changed after the snapshot. The newer change already queued its own job.

### Where the code goes

The merge core is pure array work, engine-agnostic: `src/SLNG.Assets/OutfitMerger.cs`, with xUnit
tests and `System.Numerics` types, like `RiggedMeshBuilder`. It knows no Godot type. The app passes
in material keys as opaque values, plus a "sorted" flag.

The app side lives in `app/scripts/AvatarRenderer.OutfitMerge.cs`.

### What merges

- **Opaque and Scissor surfaces of ready items, one surface per material key.** Order inside a
  surface does not matter for depth-tested passes. It is kept stable (linkset root LocalId, item
  LocalId, authored surface order) so the same outfit always merges to the same bytes. That matters
  for the P4 cache later.
- **Material key** = `(FaceTexture, resolved texture id, Kind, scissor threshold)`. FaceTexture
  equality already means "identical material inputs": that is the BUG-RENDER-12 argument at
  `:3519-3522`. The resolved id (the bake, for BoM) and the Kind cover what is derived from the
  texture.
  - A merged surface wears the `ShaderMaterial` the item path built for the first face with that
    key. The others are identical and are dropped. Texture refs are counted per avatar and per
    texture id (`PinnedTextureIds` / `UsedTextureIds`), not per material, so nothing leaks. A self
    test checks this.
- **Faces whose texture failed** (`:4022-4027`, drawn as an alpha-0 Blend card) are left out of the
  geometry. Same pixels as today. If the texture arrives later, the item path rebuilds the item,
  which re-merges its bucket.

### What stays separate, and how it coexists

| Kept apart | Why | How |
|---|---|---|
| Blend surfaces | Godot sorts transparent draws per instance (`:3726-3728`). The order must stay fixed and authored (BUG-RENDER-16/38). | One sorted instance per Blend surface, using the outfit skin, so no extra binds. `SortingOffset` is **exactly today's**: the item's `RiggedAlphaSortTieBreak` (`:3542-3556`) for an item with one Blend surface, plus `(s+1)·1e-5` for an item with two or more (`:3790`). `CastShadow` is off, as on today's split children. Because they are not also in a parent mesh, the double skinning of today's split (`:3743-3745`) goes away. |
| Items not ready yet | The Kind is unknown until the texture's alpha stats exist. | Staging, as today. Absorbed when ready. |
| Items with a BoM face whose bake is not resolved | The bake decides the Kind. A mostly-clear hair bake classifies as Blend (`:4320`). | Staging until `LoadedTextures` has the channel. Afterwards, bake changes are applied in place (see *Changes*). |
| Animated objects | Their skeleton is the control avatar's (`WornAnimesh.cs:104`). | Never merged. If the animated flag arrives after the item was merged (`:2398-2403`), the linkset is re-merged without it and the route takes over. |
| Rigid attachments | They hang under a `BoneAttachment3D`, not on the skin. | Unchanged. Merging them as single-bone skinned geometry is a Phase 3 option. |
| HUDs | Separate viewport. | Unchanged. |
| Texture-animated items | Worn texture animation is not drawn today: no `TextureAnim` reference in `AvatarRenderer*.cs`. Once FEAT-PERF-16 puts it into the shader, it is just material parameters. | Phase 1: `PrimitiveComponent.TextureAnim != null` means staging. Revisit after FEAT-PERF-16. |
| Volatile items (per-face dynamic changes) | Scripted colour cyclers, blinking lights and churning alpha toggles would re-merge a linkset every few hundred ms. | An item whose face records change 3 times within 10 s goes to staging, and stays there until 30 s after its last change. |
| Selected items (Phase 1) | `SelectionOutline` outlines a whole mesh (`SelectionOutline.cs:244`). On a bucket that would be the whole linkset. | Phase 1 ejects a selected item to staging and re-merges it on deselect. Phase 2 draws the outline from the item's own retained arrays instead (see *Interactions*). |
| The self avatar (Phase 1) | It has rigged pick colliders and is edited most. | Not merged in Phase 1. Phase 2. |

### The skin

- **One outfit skin per avatar,** shared by every bucket and sorted instance. Godot keeps one skin
  binding per `Skin` resource, which is why today's split children cost no extra binds (`:3655-3657`).
  Check this against 4.7-stable `scene/3d/skeleton_3d.cpp` (`register_skin`) in Phase 0.
- **Slot key = (bone index, IBM source).** The source is the asset's raw 4×4 inverse-bind matrix,
  compared bit for bit. An identity IBM gets its own "rest" marker, because `BindRiggedSkin` binds it
  to the live rest inverse (`:5661-5663`), which moves with shape and overrides.
- **The bind for a slot is computed exactly as `BindRiggedSkin` does:** `C⁻¹·IBM·C`, then
  `InjectOwnScale` (`:5673-5674`), then the non-finite fallback (`:5680-5685`). The skinned result is
  therefore bit-identical to the per-item skins. Exact dedupe ships in Phase 1. Tolerance dedupe or
  canonicalisation is a Phase 3 option, only if the probe shows exact dedupe leaves too many binds.
  - Canonicalisation: an item whose binds all differ from the canonical ones by one common transform
    X gets X folded into its vertices.
- **Bind-shape matrix:** already in the vertices (`RiggedMeshBuilder.cs:58-70`, `:142`). The merge
  does not touch it.
- **4-weight handling:** unchanged. The merge only renumbers slots, item-local to outfit, through a
  per-item table on the worker.
  - Weights stay bit-identical: decoded by `MeshSkinWeightDecoder`, normalised and clamped by
    `RiggedMeshBuilder`.
  - An item's slot-0 fallback for unresolved joints (`RiggedMeshBuilder.cs:262`) keeps pointing at
    that item's first bound joint.
- **The table is append-only per avatar.** Merge jobs are serialised per avatar, so a job's new keys
  never collide.
  - New binds are added in the commit (`Skin.AddBind`, batched, once per job).
  - Keys that no ready item uses any more are compacted only on a full re-merge: unpark from cold,
    a setting toggle, or garbage over 50 %.
- **Joint overrides, `lock_scale_if_joint_position`, pelvis fixups:** still applied to the skeleton
  per item, at the item's commit (`:3446`, `:3456`). Still released per item (`:5333`).
  - The merged geometry does not depend on them. Only the binds do, through the rest (identity IBMs)
    and own-scale.
  - `RebuildSkeletonAfterJointChange` and the shape path then re-bind the outfit skin in place: one
    `SetBindPose` pass over its slots, instead of a new `Skin` per item (`:6087-6101`).
  - Order rule, the same as today (`:3452-3455`): overrides go on before the binds are computed. In
    Phase 1 every item passes the staging commit first, so this holds by construction. Phase 3's
    direct merge must apply overrides in the merge commit, before the binds.
  - The root-joint skip (`:5177`) and the animesh exclusion (`:5276-5279`) are unchanged.

### Changes: incremental per bucket, debounced

| Change | What happens |
|---|---|
| Attach | The item stages as today. When ready, its linkset's bucket is dirty. |
| Detach of a whole linkset | Free its bucket and sorted instances. No job. This is the common case. |
| Detach of one prim of a merged linkset | Re-merge the bucket at once (no debounce). It draws the old geometry for a few frames until the swap. |
| Face change on a merged item (applier, tint, alpha toggle, texture swap) | The item re-prepares as today, but it is **not re-staged**: it stays merged with its old look until the bucket re-merge swaps in the new one. No double draw. New textures are in by then, so there is no untextured interim. |
| LOD raise (`:2643`) | Same as a face change. One avatar's sweep raises all its items, and the debounce folds them into one re-merge per bucket. |
| Bake change on a merged BoM surface | The material is rebuilt once per key and applied in place with `SetSurfaceOverrideMaterial`. A re-merge happens only if the Kind crosses the Blend line. Replaces the `BomAttachments` loop (`:1747-1752`) for merged items. |
| Shape change, joint-override change | Re-bind the outfit skin. No geometry work. |
| Debounce | A bucket starts its job after 0.3 s without a change, and never later than 1.5 s after its first change. Jobs are claimed in the rig workers' order (self, distance, hidden last). |

Full re-merges happen only for skin compaction, unpark from cold, and a setting toggle.

## Interactions

- **FEAT-PERF-08 cap and BUG-PERF-16 park.** BUG-PERF-16 (in progress, gemini, see
  [its spec](BUG-PERF-16-park-over-cap-avatars.md)) turns `SetAvatarReduced` into a park and
  `SetAvatarFull` into an unpark. It tracks `IsParked` and `HasLoadedOutfit`, and frees the outfit
  only in `UnloadAvatarOutfit` (`GpuCache.IsOverBudget`, or 60 s hidden). The merge plugs into those
  three:
  - **Park** of a merged avatar hides and `DetachSkeleton`s its buckets and sorted instances: one
    skin reference instead of ~150. **Unpark** is the reverse. This is the ADR's "camera swing costs
    nothing".
  - **`UnloadAvatarOutfit`** also drops buckets, sorted instances, the outfit skin and the retained
    item data.
  - Phase 2 adds a **cold** step between the two: GPU meshes freed, item CPU arrays kept, so
    re-merging needs no fetch or rig.
  - An avatar that never loaded stays a jelly doll, as BUG-PERF-16 has it.
- **`[AvatarCost]`.** Buckets and sorted instances are skeleton children, so they are counted.
  - `boundSkinBinds` must count each distinct `Skin` once. Otherwise the shared outfit skin is
    counted per instance.
  - New fields: `mergedAvatars`, `buckets`, `sortedInstances`, `stagedItems`, `mergedItems`,
    `outfitSlots`, `mergeCpuMB`.
- **Picking** (Phase 2, self).
  - Chunks are still cut per item on the worker (`RigWorker.cs:389-397`), with slots remapped to
    the outfit skin. `BuildPickChunk` takes its bind pose from the outfit skin (`:2994`).
  - The validity check at `:3067` becomes "item generation unchanged".
  - Colliders are rebuilt when the item's own geometry changes, not when its bucket re-merges.
  - Phase 1 needs no pick change: remote avatars have no rigged colliders (`:3036`).
- **Selection outline** (Phase 2). An outline instance built from the selected item's retained
  arrays, already remapped, sharing the outfit skin, through a `SelectionOutline.Apply` overload that
  takes an explicit mesh. One `AddSurfaceFromArrays` per selection. `RestoreWornHighlight` (`:2883`)
  re-applies it after a swap.
- **Hidden faces.**
  - Invisible faces never reach merged geometry (`RiggedMeshBuilder.cs:123`), so a bucket draws no
    `Hidden` surfaces and `UpdateWornMeshVisibility` is needed only for staging.
  - `RecomputeMeshVisibility` (`:4388`) reads only face records. `BomAttachments` is changed to
    entity-keyed data, so merged items, which have no own instance, still hide the system parts.
- **GpuCache.** Texture LOD and shrink write into the same `ImageTexture` (`GpuCache.cs:468`, `:1513`),
  so merged materials follow without a re-merge. Pins, refs and the draw-distance unpin
  (`:6988-7015`) are unchanged.
- **LOD.** See *Changes*. The merge never picks a LOD itself.
- **Culling.** A bucket gets the same 8 m `CustomAabb` every rigged item has today (`:3483`). All of
  an avatar's items already culled as one box, so nothing changes.
- **Shadows.** Merged Opaque and Scissor surfaces cast shadows as their faces did. Blend surfaces
  never did.

## Threading (ADR 0004 P1)

| Where | Work |
|---|---|
| Worker | Item prepare (existing). Copying tangents out of the `CommitToArrays` result into managed `float[]` (new, in `PrepareRiggedMesh`). The merge job: dedupe, remap, concatenation, ranges, stats, and packing into `Godot.Collections.Array` (already done on workers since BUG-PERF-06). The `Kind` and key of each face are recorded where `BuildFaceMaterialAsync` finishes, which is already a pool thread (`ConfigureAwait(false)`). |
| Main | New `Skin.AddBind` / `SetBindPose`: these read the skeleton node's rest and own-scale, about 0.1 ms per 100 binds. `AddSurfaceFromArrays`, at most one large surface per queue item, into an `ArrayMesh` that is not in the tree yet. One swap item that adds and frees nodes and sets materials. |
| Never | `SurfaceGetArrays`, `GetImage`, `CreateTrimeshShape`, synchronous RS getters. The merge reads only its retained managed arrays. Buckets are not put into `MeshSurfaceCache`, because nothing reads them back. |

`MainThreadWorkQueue.Pump` always runs at least one item per lane per frame, so one oversized
`AddSurfaceFromArrays` would be a hitch. A material group larger than the probe's per-item limit is
cut into several surfaces: a few more draws, no hitch. If the probe shows that packing dominates,
Phase 3 packs the RS surface dictionary on the worker and commits with `RenderingServer.MeshAddSurface`.

## Expected numbers

The source is the crowded run in `slng-perf.log` at 08:29:53–08:30:19 on 2026-10-10: 47 avatars,
35 reduced, so 12 full; render thread separate.

| Measure | Value |
|---|---|
| `skinnedMeshes` / drawn | 1,810 / 1,759 |
| `surfaces` | 1,975 |
| `skinBinds` | 100,580 |
| `boundSkinBinds` | 96,585 |
| `maxSkinned` | 302 |
| `reqHiddenSubmeshes` | 31,690 / 43,837 (already dropped at build) |
| draws | 14.9–15.4k (scene 7.9k, shadow 6.6–7.2k) |
| `nodes` | 56k |
| `renderCpuMs` | 24–31 |

Per full avatar: **~151 skinned instances, ~165 surfaces, ~8,400 binds.**

Main-thread arrival cost today, from the same window:

- `avatar.rig` n=182, 67 ms in total: 0.37 ms per item, 6.8 ms max.
- With the material, BoM and split items that is ~0.45 ms per item, so **~65 ms per avatar**,
  spread over frames.
- Of that, `avatar.rig.commit` (the upload) is 0.15 ms per item.

Estimates per full avatar, to be replaced by the probe:

| Measure | Today | Phase 1 (per-linkset buckets) | Phase 2 (packed buckets, Blend runs) |
|---|---|---|---|
| Skinned instances | ~151 | ~25–45 (≈20 linksets + 5–20 sorted) | ~8–20 |
| `Skin` objects bound to the skeleton | ~151 | 1 | 1 |
| Binds written per re-pose | ~8,400 | ~300–1,500 (exact dedupe; the skeleton has 159 joints) | ≤ ~300 with canonicalisation |
| Surfaces = draws per pass | ~165 | ~50–100 (distinct materials per linkset) | ~40–80 |
| Main-thread ms at arrival | ~65, spread | ~65 + ~20 for the merged commits (staging kept) | Phase 3 direct merge: ~25 |
| Main-thread ms for park / unpark | n/a (unload, then re-rig ~65) | ~0.1 (a few node writes) | same |

For 12 full avatars that is:

- instances 1,810 → ~300–540 → ~100–240;
- binds 100k → ≤ 18k;
- avatar surfaces per pass 1,975 → ~600–1,200.

**What is not known:** how much those cuts are worth in ms. Phase 0 answers that.

## Phase 0: measurement probe (the gate)

1. **`[OutfitProbe]`** (Develop menu action, on demand, main-thread walk). Per full avatar, from data
   already held: `visual.RiggedAttachments` meshes, `_attachmentMeshIds` faces, the instances'
   materials. It reports:
   - items, linksets and vertices;
   - Opaque, Scissor and Blend surfaces;
   - distinct material keys per linkset and per avatar;
   - Σ binds against distinct (bone, IBM) keys, exact and at 1e-5;
   - how many items could be canonicalised.

   This fills the table above with real numbers.
2. **Upload microbenchmark** in `--selftest` (headless). Main-thread `AddSurfaceFromArrays` time for
   skinned arrays of 10k, 50k and 65k vertices. `mesh_create_surface_data_from_arrays` runs on the
   calling thread even with the dummy renderer. This sets the per-item surface limit.
3. **In-world naive merge** (Develop menu toggle "Outfit merge (probe)").
   - For every full non-self avatar, on the main thread: concatenate the instances' arrays from
     `MeshSurfaceCache` per material key. Keep Blend surfaces as they are. Build one `Skin` with
     exact dedupe. Hide the originals with `DetachSkeleton`, show the merge. The toggle undoes it.
   - It ignores pending BoM and similar details: it measures engine cost, not looks.
   - Procedure: at the MVP6-1 baseline spot with the render thread on, 3 × 30 s per state. Read
     `[Perf]` (`draws`, `drawsScene`, `drawsShadow`, `renderCpuMs`, `postFlushMs`, `processMs`,
     `renderGpuMs`) and `[AvatarCost]`.

**Decision rule.** Build Phase 1 only if probe 3 cuts avatar draws by ≥ 40 % **and** `renderCpuMs`
or `postFlushMs` by ≥ 15 %. Otherwise record the numbers here, stop, and move P2/P4 ahead.

## Risks

1. **Materials dedupe less than hoped.** Each garment has its own textures, so draws barely drop.
   Probe 1 tells. A per-avatar texture array or atlas is the follow-up lever, and outside this spec.
2. **Bind dedupe is poor** (every creator's IBMs differ). Probe 1. Canonicalisation is in Phase 3.
3. **Commit hitches.** Probe 2. Mitigated by the per-item surface limit, and by worker-side packing
   in Phase 3.
4. **Per-item granularity is lost.** Every face change re-merges a linkset. The volatile rule and
   per-linkset buckets bound it.
5. **Double work while loading** (staging plus merge, ~+30 % at arrival). Measured in Phase 1,
   removed by Phase 3's direct merge.
6. **Memory.** Retained CPU arrays per full or parked avatar, roughly 10–30 MB. Shown in
   `mergeCpuMB`, dropped at "cold".
7. **Sort regressions** (BUG-RENDER-16/38). The offsets are exactly today's. A self test asserts it.
   The WINGS-ES1105 hair and a BoM body with alpha layers are the in-world checks.
8. **Bookkeeping drift.** Every `_riggedAttachments` reader listed above has to learn "merged". A
   missed one leaves a stale bucket or a missing outline.
9. **Kind flips.** A BoM bake or a late texture can move a face across the Blend line. Handled by
   re-merging; a flip-flop would trip the volatile rule.
10. **Shared-skin churn.** `AddBind` re-registers the skin with Godot. It is batched to once per job.

## Phased plan

| Phase | Scope | Lands as |
|---|---|---|
| 0 | Probes 1–3, decision recorded here. | Develop-menu only, no behaviour change. |
| 1 | Remote avatars. Per-linkset buckets, one outfit skin, Blend surfaces as sorted instances, staging kept, exact dedupe, eject on select, volatile rule, `[AvatarCost]` fields. `OutfitMerger` in `SLNG.Assets` with xUnit tests. Setting `RenderConfig.OutfitMerge` (Graphics > advanced, plus `--outfit-merge=on\|off`), off by default, switchable at runtime. | Behind the setting. In-world A/B at the baseline spot. |
| 2 | Self avatar (remapped pick colliders, outline from item arrays). Small linksets packed into buckets (≤ 65,535 vertices, 16-bit indices). Consecutive same-material Blend surfaces merged in sort order. Park integration with the cold level. | Behind the setting. A/B. Default on if both A/Bs hold. |
| 3 | Direct merge for a remote avatar's first outfit (no staging). Bind canonicalisation. Worker-side RS surface packing if probe 2 says so. P4: merged outfits in the GPU-ready disk cache, keyed by outfit hash. Option: rigid attachments as single-bone skinned items. | Each item measured separately. |

## Acceptance Criteria

- [ ] Phase 0 numbers and the decision are recorded in this spec.
- [ ] Setting off: nothing changes (selftest, and an in-world spot check).
- [ ] Setting on, remote avatars at the baseline spot, no visual change in A/B screenshots of:
  - mesh bodies with BoM and alpha layers;
  - the WINGS-ES1105 hair (sort unchanged);
  - an outfit change (attach, detach, replace), with no stale or doubled geometry;
  - a LOD raise while approaching an avatar;
  - an applier texture change.
- [ ] `[AvatarCost]`: one `Skin` per merged avatar, and skinned instances per full avatar within the
      Phase 1 estimate, or the estimate corrected here.
- [ ] No queue item labelled `avatar.merge.*` above 4 ms max; average ≤ 1 ms.
- [ ] No RS read-back in the merge path. Separate render thread on: no `[RiggedSort]` or
      `MeshSurfaceCache` miss warnings from merged avatars.
- [ ] Self test `outfit merge skins like separate items`:
  - rest-pose CPU-skinned positions are equal within 1e-5 m (merged against per-item skins, through
    `ComputeGlobalRestTransform · bind`);
  - sorted offsets are equal to today's;
  - texture refs are unchanged after merge and detach.
- [ ] xUnit `OutfitMergerTests`: slot dedupe and remap, slot-0 fallback kept, material grouping,
      stable order, Blend kept apart, per-item ranges, an empty or all-hidden item.
- [ ] In-world A/B result (draws, `renderCpuMs`, `postFlushMs`, fps) recorded here and in `docs/perf/`.

## Technical Specs & Affected Files

- `src/SLNG.Assets/OutfitMerger.cs` (new): input and result records, dedupe, remap, concatenation,
  ranges.
- `tests/SLNG.Assets.Tests/OutfitMergerTests.cs` (new).
- `app/scripts/AvatarRenderer.OutfitMerge.cs` (new): readiness, buckets, jobs, commit, swap, outfit
  skin, re-bind, park hooks.
- `app/scripts/AvatarRenderer.OutfitProbe.cs` (new, Phase 0).
- `app/scripts/AvatarRenderer.RigWorker.cs`: `PreparedRiggedMesh` keeps managed tangents; the merge
  job claims in the same order.
- `app/scripts/AvatarRenderer.cs`, at these points:
  - `CommitPreparedRig` and `avatar.split_sorted`: the readiness hook;
  - `RemoveVisual`;
  - `HighlightAttachment`;
  - `BomAttachments`, keyed by entity;
  - bake arrival;
  - `RebuildRiggedAttachmentSkins`: plus the outfit skin re-bind;
  - `SetAvatarReduced` / `SetAvatarFull`;
  - `ReportAvatarCost`;
  - every `_riggedAttachments` reader.
- `app/scripts/SelectionOutline.cs`: explicit-mesh overload (Phase 2).
- `app/scripts/RenderConfig.cs`, `UI/GraphicsSettings.cs`, `UI/GraphicsPreferencesPage.cs`,
  `app/i18n/*.json`: the setting.
- `app/scripts/Boot.cs`: `AppVersion`.
- `docs/BENUTZERHANDBUCH.md`: the setting, in German.

## Sub-tasks / Progress

- [ ] Phase 0: `[OutfitProbe]` counts
- [ ] Phase 0: upload microbenchmark in `--selftest`
- [ ] Phase 0: in-world naive-merge A/B and the decision
- [ ] Phase 1: `OutfitMerger` + xUnit tests (test-first)
- [ ] Phase 1: readiness, buckets, merge job, commit and swap, outfit skin
- [ ] Phase 1: `_riggedAttachments` readers, BoM by entity, bake arrival in place, re-bind
- [ ] Phase 1: setting, `[AvatarCost]` fields, self test
- [ ] Phase 1: in-world A/B
- [ ] Phase 2: self avatar, packed buckets, Blend runs, park cold level
- [ ] Phase 3: direct merge, canonicalisation, worker packing, P4 cache
