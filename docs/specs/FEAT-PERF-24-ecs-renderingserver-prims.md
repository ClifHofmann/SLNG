# [FEAT-PERF-24] Static prims as RenderingServer instances owned by the ECS

- **Feature ID:** `FEAT-PERF-24`
- **Track:** `render` | `perf`
- **Status:** `🚧 In Progress` (Phase 0 split built; skin sharing confirmed in-world 2026-10-16: 21 → 26.5 fps at Sirens; P0-b/P0-c and the gate still open)
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md), [ADR 0004](../adr/0004-performance-architecture.md) pillar P2, plan in [MVP6-1](MVP6-1-performance-program.md)
- **Depends on:** FEAT-PERF-13 (render thread), BUG-PERF-15 (no read-backs). **Feeds:** FEAT-PERF-14, FEAT-PERF-20, FEAT-PERF-21.

## Overview & Goal

Today every prim in the world is three Godot nodes: a `MeshInstance3D` with a `StaticBody3D` and a
`CollisionShape3D` under it, plus more children for split alpha surfaces, a light, particles or a
selection outline. At the crowded spot that is ~56,000 scene nodes, ~90 % of them prims. ADR 0004 (P2)
says: draw static prims as `RenderingServer` instances (RIDs) keyed by the world's entities, and give
an object a node only while someone interacts with it.

**Read this first: the "flush" is mostly waiting, not node work.** At v0.27.31 with the render thread
on, `postFlushMs` goes up and down with the GPU time while the node count stays flat (table below).
Godot's source agrees: a node that does not change costs nothing per frame. So swapping nodes for RIDs
will **not** by itself win back the 22–45 ms. What it does win:

- no Godot calls per prim in our own sweeps (the alpha census costs ~14 ms in one burst every second);
- cheaper region load, region leave and teleport (no 3 nodes created and `QueueFree`d per prim);
- ~50k fewer nodes and C# wrapper objects;
- one place that decides whether a prim is drawn by its own instance, a MultiMesh slot, a merged cell,
  or not at all. FEAT-PERF-20 needs that place.

The frame-rate gains come from what is built on top: culling by size in the engine (FEAT-PERF-14),
merged cells (FEAT-PERF-20) and occluders (FEAT-PERF-21). Those cut what the render thread and the GPU
have to draw.

So this spec does three things. It says what the flush is. It specifies a probe that measures the real
gain before the large change is built (Phase 0). It designs the change so that it lands behind a setting
with an in-world A/B. **Phase 2 (the RID backend) starts only if the Phase 0 gate passes.** Phase 1 pays
for itself either way.

## What the flush actually is

### Measured (slng-perf.log, 2026-10-10, v0.27.31, `rt=separate`, region with ~22.5k entities)

| Time | fps | scriptsMs | postFlushMs | renderCpuMs | renderGpuMs | draws | nodes |
|---|---|---|---|---|---|---|---|
| 08:27:47 | 15 | 25.7 | 20.5 | 18.3 | 49.8 | 14,021 | 55,061 |
| 08:27:52 | 16 | 23.0 | **50.9** | 18.8 | **68.1** | 14,007 | 55,043 |
| 08:27:57 | 15 | 25.1 | 37.9 | 19.1 | 55.0 | 14,007 | 55,043 |
| 08:28:18 | 14 | 26.6 | 20.8 | 25.8 | 28.1 | 15,140 | 55,992 |
| 08:28:23 | 14 | 34.0 | **63.3** | 26.6 | **103.1** | 15,144 | 55,985 |
| 08:30:18 | 7 | 35.5 | **204.0** | 25.6 | **234.2** | 15,446 | 56,012 |

- The node count holds at 55–56k while `postFlushMs` swings from 20 to 204 ms, in step with
  `renderGpuMs`. That is the main thread waiting in `RenderingServer::sync()` for the render thread,
  which itself waits for the GPU (FrameTimeline.cs:38-40 documents this for Separate mode).
- In Safe mode, where there is no wait, the v0.27.28 baseline measured `postFlushMs` at 12–17 ms
  (MVP6-1). That is the most a node change could ever touch, and avatars share it: skeleton updates are
  deferred calls, and bone attachments move every frame.
- The 08:30 run is GPU-bound in a way that is out of scope here (`renderGpuMs` 137–450 ms at
  7.6 GB VRAM). FEAT-PERF-12 has to explain it.
- "Objects 37,969" in ADR 0004 is Godot's `RenderTotalObjectsInFrame`, summed over all passes. It is
  not the prim count. The prim count is roughly nodes ÷ 3.

### What Godot 4.7 does between our last `_Process` and the draw (4.7-stable source)

| Step | What scales with what | Source |
|---|---|---|
| `Main::iteration`: `process()` → message-queue flush → navigation → `RenderingServer::sync()` → `draw()` | `sync()` waits for the render thread in Separate mode | `main/main.cpp:5058-5089`; `servers/rendering/rendering_server_default.cpp:435-451` |
| `SceneTree::process`: `_process(false)`, then `_flush_ugc`, message-queue flush, `flush_transform_notifications`, timers, tweens, `_flush_delete_queue`, idle callbacks, FTI | per frame, in this order | `scene/main/scene_tree.cpp:688-739` |
| `flush_transform_notifications` walks only `xform_change_list`, the nodes that moved since the last flush. It does not walk the tree. | **changed** nodes, not all nodes | `scene_tree.cpp:200-210` |
| A node joins that list only when its transform changes (`_propagate_transform_changed` walks its own children). | children of a moved node | `scene/3d/node_3d.cpp:112-135` |
| Each moved prim then does `instance_set_transform` (VisualInstance3D) and `body_set_state(TRANSFORM)` (StaticBody3D). | per moved prim | `scene/3d/visual_instance_3d.cpp:97-101`; `scene/3d/physics/collision_object_3d.cpp:96-104` |
| `_flush_delete_queue` frees every `QueueFree`d node, with exit-tree notifications and physics/RS teardown. | per removed node. Our removal budget (ObjectRenderer.cs:686-705) does **not** cover this half. | `scene_tree.cpp:735` |
| FTI (physics interpolation) returns at once when disabled. SLNG does not enable it. | — | `scene/main/scene_tree_fti.cpp:604-607` |
| Prims have no `_process`. MeshInstance3D, StaticBody3D and CollisionShape3D do not enable processing. | — | `visual_instance_3d.cpp:208-210` (ctor: RS instance, notify-transform only) |

On the render thread, a static prim costs the same whether a node or a RID created its instance. A node
is only a wrapper around the same `RenderingServer` instance (`visual_instance_3d.cpp:208`).
`_scene_cull` walks every instance in `scenario->instance_data` once per pass. That walk is threaded
above `threaded_cull_minimum_instances` (`renderer_scene_cull.cpp:2892-2930`, `3416-3445`, `4516`). A
hidden instance leaves that list (`instance_set_visible` → `_unpair_instance`, `:1046-1062`). Draws,
the render CPU and the GPU therefore go down only when **fewer instances or surfaces are drawn**.
Changing who owns them does not.

**Conclusion.** For a mostly static scene, the per-frame main-thread cost of prim nodes is small:
moved prims and removed nodes only. The visible "flush" is the render side. Phase 0 checks this
claim with numbers before anything big is built.

### 2026-10-16 finding: at Sirens the flush is skin-bind commands, not prims

Measured at Sirens Beach on v0.27.63 (`rt=separate`, `-ShareMaterials`, vsync off), 25 `[Perf]` lines
09:02–09:04: fps 19–23, `scriptsMs` 16–32 (median ~20), `postFlushMs` 22–30 (median ~26),
`renderCpuMs` 22–30 (median ~24), `renderGpuMs` 13–16, `setupMs` 0–5. The frame (~47 ms) is scripts
plus postFlush, so the two threads do not overlap. Cutting draws from 24k to 21k did not move it.

**What Godot 4.7 does (source read, 4.7-stable).**

- The main thread blocks only in `RenderingServer::sync()` (`main.cpp:5073`) and in synchronous
  calls. `CommandQueueMT::_flush` drops its lock while a command runs (`command_queue_mt.h:191-193`),
  so setters never wait for a draw.
- Each setter from the main thread costs a lock, a copy into the queue and
  `WorkerThreadPool::notify_yield_over` (a second, global lock plus a condition-variable wake,
  `command_queue_mt.h:137-149`, `worker_thread_pool.cpp:634-652`). In Safe mode the same call is
  a direct function call.
- `Skeleton3D` handles `NOTIFICATION_UPDATE_SKELETON` (deferred, so it runs in the message-queue
  flush **after** our scripts, inside `postFlushMs`). For every skin bound to it, it sends
  **one `skeleton_bone_set_transform` per bind** (`skeleton_3d.cpp:349-406`). Meshes share that
  work only when they hold the **same `Skin` object** (`register_skin`, `skeleton_3d.cpp`:
  `if (E->skin == p_skin) return E`).
- SLNG built a new `Skin` for every rigged mesh (`AvatarRenderer.BindRiggedSkin`). The same spot's
  `[AvatarCost]` line: 17 full avatars, `skinnedMeshes=2825`, `skinBinds=158649`, all animating.
  That is ~158,000 RenderingServer commands per frame from skeletons alone.

**Bench (headless, Godot 4.7, a scratch GDScript project, 300 frames each).** 17 skeletons × 166
skinned meshes × 56 binds, one bone re-posed per skeleton per frame. `flushMs` = scripts end to a
zero-length timer, the same cut as P0-a.

| Mode | Distinct skins / skeleton | Writes / frame | flushMs | ns / write |
|---|---|---|---|---|
| safe | 166 (own skin per mesh) | 158,032 | 3.9 | 25 |
| **separate** | 166 | 158,032 | **14.6–17.5** | 93–111 |
| separate | 40 (shared) | 38,080 | 4.0 | 105 |
| separate | 10 (shared) | 9,520 | 1.5 | 161 |
| separate, render thread held for 40 ms | 166 | 158,032 | 9.8 | 62 (no lock contention) |

The render thread works through the same commands at ~22 ns each with the dummy renderer, ~3.4 ms
per frame, plus the real skeleton-buffer writes. So the cost lands mostly on the main thread, in
the flush, and grows linearly with the number of distinct skins.

**Can sharing help? Offline over the asset cache** (60,362 `.mesh` files, 35,575 rigged): only
**8,898 distinct skin contents** (same joints and bit-identical inverse-bind matrices), and 46,005
distinct (joint, matrix) pairs out of 1,273,912. Most meshes come in groups that share one skin (a
mesh body's pieces), so whole-skin sharing removes about three quarters of the writes.

**Cause, in one line.** By the bench, about 15 ms of the 26 ms `postFlushMs` at Sirens is our own flush work
(the in-world `flushMs` / `tailMs` split below confirms or corrects this):
`Skeleton3D` writing ~158k skin binds as separate RenderingServer commands, ~100 ns each with the
separate render thread. That cost exists only because every rigged mesh had its own `Skin`. The rest
is the real wait for the render thread, which also has to execute those commands
(`renderCpuMs` does not count them).

**Fix (v0.27.64-alpha, branch `fix/FEAT-PERF-24-render-overlap`).** `BindRiggedSkin` hands out one
`Skin` per distinct content per avatar (`SLNG.Core.Avatars.SkinBindKey`: bone and the bind matrix's
exact bits, per slot). The palette is bit-identical, so nothing on screen changes. The cache is
cleared when the binds are rebuilt after a shape change. It is on by default. `--no-share-skins`
(`run-client.ps1 -NoShareSkins`) is the A/B switch.

**New readings.**

- `[Perf]`: `flushMs`, `tailMs` (always), `rtBusyMs`, `rtLagMs` (with `--diag` in separate mode), `frees`.
- `[AvatarCost]`: `skinWrites` (binds written per re-pose, distinct skins only),
  `skelUpdatesPerFrame`, `skinWritesPerFrame`, `sharedSkins=shared/bound`, `shareSkins=on|off`.

**Measurement plan for the in-world A/B** (same spot, same settings, `-Diag`, wait for `queue=0`, stand
2 minutes each):

1. `run-client.ps1 -Diag -ShareMaterials -NoShareSkins` (the old behaviour);
2. `run-client.ps1 -Diag -ShareMaterials` (shared skins).

Compare the medians of `[Perf]` `fps`, `postFlushMs`, `flushMs`, `tailMs`, `rtLagMs`, `scriptsMs`,
`renderCpuMs`, and `[AvatarCost]` `skinWritesPerFrame` and `skinWrites` against `boundSkinBinds`.
Expected: `skinWritesPerFrame` falls about 3–4×, and `flushMs` falls by roughly
0.1 µs × the writes saved. If `tailMs ≈ rtLagMs` afterwards, the frame is then render-thread-bound,
and the lever moves to the render side.

**In-world A/B result (2026-10-16, Sirens Beach, v0.27.65, `-Diag -ShareMaterials`, ~50 avatars, 17
full and animating).** Steady windows only (after loading, `queue` < 1,000). Off = `-NoShareSkins`,
09:49:17–09:50:07, 11 windows. On = 09:51:17–09:51:52, 8 windows.

| Median | Off | On | Change |
|---|---|---|---|
| fps | 21 | **26.5** | +26 % |
| p99Ms | ~91 | ~50 | |
| scriptsMs | 18.7 | 17.5 | |
| postFlushMs | 27.2 | **19.2** | −8 |
| flushMs | 14.8 | **6.7** | −8.1 |
| tailMs | 12.5 | 12.6 | |
| rtLagMs | 11.5 | 12.0 | |
| renderCpuMs | 24.8 | 22.0 | −2.8 |
| `[AvatarCost]` skinWritesPerFrame | 140,400 | **12,400** | ÷ 11 |
| skinWrites (per re-pose) vs boundSkinBinds | 140,800 / 140,130 | 13,000 / 133,991 | |

Whole-skin sharing removed 91 % of the writes in-world, more than the cache-wide estimate (75 %).
Avatars at one spot wear the same mesh bodies and heads. `flushMs` fell by 8.1 ms for ~128k fewer
writes, which is 63 ns per write, the bench's uncontended figure.

**What is left.** `tailMs ≈ rtLagMs` (~12 ms) in both runs, so the tail is now an honest wait: the
render thread is the slower side. Per frame it needs ~37 ms (scripts end + `rtLagMs`, about the
frame). Only ~24 ms of that is `renderCpuMs + setupMs`. The remaining ~13 ms is outside the viewport
timers: command execution, skeleton and other buffer uploads, `end_frame`/present and probes. The
next levers are therefore on the render side: fewer draws (FEAT-PERF-14/20/21), and splitting that
unmeasured ~13 ms. The per-avatar palette (below) would take `flushMs` down by at most ~0.8 ms more.
It is no longer the bottleneck.

**`queue` never reaches 0 at Sirens, in either run, and did not on v0.27.63 either** (floor ~50–60).
This is not a backlog. At this spot ~2,900 `visual.update` items per second (moving objects) plus
the cull slices flow through `MainThreadWorkQueue`, so some are always waiting when the overlay
samples. For an A/B, wait until `queue` stays in the hundreds, not until it is 0.

**Next step if the gain is too small.** Use one skin palette per avatar instead of whole-skin
sharing: one slot per distinct (bone, bind) pair, with each mesh's vertex bone indices remapped to
it. Offline, that leaves ~1 in 28 pairs, so a full avatar would write a few hundred binds per
re-pose instead of ~9,000. It touches the rig worker (the slots are cut there), the pick chunks and
the split surfaces. It needs its own task.

**For the gate below.** None of this is prim-node cost. It strengthens the no-go reading for
Phase 2: the ~26 ms was never the node tree.

### 2026-10-16 finding: the render thread's unmeasured ~13 ms is `end_frame` recording every draw

**Reading (v0.27.66, branch `fix/FEAT-PERF-24-rt-split`, `--diag`, separate render thread).** Two more
render-thread markers bracket the queued `_draw`. One is queued in `frame_pre_draw`, which
`RenderingServerDefault::draw` emits right before it queues `_draw` (`rendering_server_default.cpp:443-449`).
The other is queued from the frame() hook after `draw()` returns (C# runs posts to
`Dispatcher.SynchronizationContext` in `ScriptServer::frame()`, `main.cpp:5085-5105`). A frame whose end
marker went out more than 1 ms late is left out.

- `drawCallMs`: inside `_draw`.
- `commandMs`: executing queued commands outside `_draw`, split as `cmdSplit=scripts,flush`.
- `rtIdleMaxMs`: at most this much of the cycle was idle.

The three add up to the render thread's cycle, about `frameMs`. `drawSplit=pre,vp,post` cuts `_draw` at
Godot's own `vp_begin`/`vp_end` timestamps, whose CPU times `RenderSections` reads on the render thread:

- `pre`: scene/canvas update, particles, `render_probes`.
- `vp`: the viewports, which is `renderCpuMs`.
- `post`: the blit to the screen, `canvas_render->update` and `end_frame`.

With Godot's `--gpu-profile` (`run-client.ps1 -GpuProfile`), an `[RtSections]` line names the CPU time
between every capture inside `_draw`, and godot.log gets Godot's per-pass GPU profile.

**Sirens Beach, same spot as above** (`-Diag -ShareMaterials -GpuProfile`, 10:20:26–10:21:01, 8 windows,
`queue` 50–214), against a 15 s look out to sea (10:20:09–10:20:19, 3 windows):

| Median | Beach | Sea |
|---|---|---|
| fps | 23 | 46 |
| draws (scene / shadow) | 23,830 (12,650 / 10,880) | 1,389 (360 / 720) |
| `drawCallMs` | **41.1** | 5.4 |
| `drawSplit` pre / vp / post | 2.7 / 25.8 / **12.6** | 2.5 / 1.7 / 1.2 |
| `renderCpuMs` / `setupMs` | 25.8 / 1.8 | 1.7 / 2.0 |
| `commandMs` (scripts + flush) | **1.2** (0.15 + 1.0) | 0.0 |
| `rtIdleMaxMs` | 0.55 | 16.5 |
| `tailMs` / `rtLagMs` | 16.1 / 15.5 | 0.1 / 0.0 |
| `renderGpuMs` / Godot GPU-profile total | 10.6 / 10–14 | 4.8 / – |

At the beach the render thread is never idle. Its cycle (41.1 + 1.2 + 0.55 = 42.9 ms) is the frame period
at 23 fps, and pre + vp + post = 41.1 = `drawCallMs`. The gap between the viewport timers and the frame
is therefore `post`, 12.6 ms, and nothing else of size:

- **Not the queued commands.** `commandMs` is 1.2 ms: the 12.4k skin binds and the ~2,900 `visual.update`
  setters per second execute in about 1 ms per frame.
- **Not reflection probes.** `render_probes` sits in `pre`, which is 2.7 ms in total. Of that, 1.8 ms is
  `Prepare Render Frame` (scene and canvas update) and 0.7 ms is the stretch before it. The follow probe
  re-bakes every ~2–3 s (`[ReflProbe] bake #100`), but averaged over the frames that adds at most ~0.2 ms.
  There is no mirror and no hero probe here.
- **Not a GPU or present wait.** The GPU works 10–14 ms of the 43 ms cycle, so the fence for the frame
  slot (`_begin_frame` → `_stall_for_frame`) has long been signalled. V-Sync is off, so present does not
  block. The fixed part of `post` (blit, canvas, submit, present) is at most the 1.2 ms the sea view shows.
- **It is per draw.** `post` goes from 1.2 to 12.6 ms as draws go from 1.4k to 23.8k: 0.51 µs per draw.
  `[RtSections]`: `< Render Viewports → end` = 12.6 ms, against shadows 7.2, depth pre-pass 5.9, opaque
  5.6, 3D setup 2.5, shadow cull 2.3 and transparent 2.0 inside the viewports.

**What it is (4.7-stable source).** `end_frame` is `RenderingDevice::swap_buffers` → `_end_frame` →
`draw_graph.end()` (`rendering_device.cpp:8123`). Since Godot 4.3, a viewport render only records its
draws into the render graph, and the viewport CPU timer times exactly that. `RenderingDeviceGraph::end`
reorders the recorded commands (`RENDER_GRAPH_REORDER`) and only then writes every draw, bind and push
constant into the Vulkan command buffer, after the last `vp_end`. Every draw is therefore paid for twice
on the render thread: ~1.07 µs inside the viewport timer (cull, render list, graph record) and ~0.51 µs in
`end_frame`, ~1.6 µs in all. The 10.9k shadow draws cost ~9.5 ms inside the viewport (shadow render 7.2 +
cull 2.3) plus ~5.6 ms in `end_frame`, ~15 ms of the cycle.

**Consequence.** The render thread is the limit at Sirens. It is draw-bound, and nothing outside the draw
path (commands, probes, GPU) is worth chasing. Each draw removed saves ~1.6 µs on the render thread, ~50 %
more than `renderCpuMs` suggests, so 10k fewer draws ≈ 16 ms ≈ the render-thread wait in `tailMs`.
The levers are the draw-count tasks: FEAT-PERF-14 (small-object cull), FEAT-PERF-20 (shared materials),
FEAT-PERF-21, and fewer shadow casters or cascades (the shadow pass is almost half the draws).

`drawSplit` counts every bracketed draw whose captures came back, so while the region loads it can
differ from `drawCallMs` (fixed in v0.27.67: a late end marker now drops the draw from both). The
loading window at 10:19:47 also shows `renderCpuMs=6148914983294305`: Godot's measured viewport time
underflowed once. That is an existing `RenderTimes` read, not this change.

## Phase 0: the measurement probe

### P0-a: split `postFlushMs` in Separate mode (`--diag` only, FrameTimeline)

Two extra cut points, both taken in `FrameTimelineEnd._Process`, the last script of the frame:

1. **Flush marker.** `GetTree().CreateTimer(0)` with a `Timeout` handler that stores a timestamp. A
   timer created during `_process` fires in the same frame's `process_timers` (`scene_tree.cpp:729`,
   `793-823`). That is after the deferred calls and transform notifications (`:719-723`) and before
   the delete queue (`:735`).
2. **Render-thread marker.** `RenderingServer.CallOnRenderThread(marker)` with one `Callable` made
   once on the main thread, the same mechanism FEAT-PERF-13 uses for its mode probe. The render thread
   runs it once it has finished the previous frame and the commands queued before it.

New `[Perf]` fields:
- `flushMs` = flush marker − scripts end (deferred calls and transforms);
- `tailMs` = `frame_pre_draw` − flush marker (delete queue plus `sync()` wait);
- `rtBusyMs` = render marker − scripts end (how long the render side was still busy);
- `rtLagMs` = a second render marker, queued at the flush marker, − the flush marker (how far
  behind the render side still was when the main thread's flush ended);
- `frees` (per frame) from a counter in `RemoveVisual`.

*As built (v0.27.64):* `flushMs` / `tailMs` are always in `[Perf]` (one zero-length timer per frame
costs microseconds); only the two render markers need `--diag` and separate mode, and read `-`
otherwise. Both markers have run by `frame_pre_draw`, because the `sync()` before it waits for
them, so they are read in the same frame. The self test checks that ordering against a
synchronous call (`render-thread markers run before a sync returns`).

If `tailMs ≈ rtBusyMs − flushMs`, the tail is waiting. A self test asserts that the markers arrive in
order.

### P0-b: one Safe-mode run at the baseline spot

`--render-thread safe`, same spot and settings, 2 minutes, `--diag`. In Safe mode `postFlushMs` has no
wait in it, so it is the real flush. Read it next to P0-a.

### P0-c: a synthetic bench, nodes against RIDs

`--selftest --prim-bench` (opt-in, not part of the normal pass) builds **30,000 prims** on a grid over
256 × 256 m. They share one 6-surface box `ArrayMesh` (instancing off) and one `ConcavePolygonShape3D`.
Each prim gets its **own 6 `ShaderMaterial`s**, as `BuildFaceMaterialAsync` makes today
(ObjectRenderer.cs:3936).

- **Backend A (nodes):** exactly `CreateVisual`'s three nodes (ObjectRenderer.cs:1369-1383).
- **Backend B (RIDs):** `InstanceCreate2(mesh, scenario)` plus 6 `InstanceSetSurfaceOverrideMaterial`,
  then `BodyCreate`, `BodyAddShape`, `BodySetSpace`, `BodySetState(Transform)`.

Each scenario runs 600 frames. The bench reports mean/p99 of `scriptsMs`, `flushMs`, `tailMs`,
`ObjectNodeCount`, gen0/gen1 GCs per second and static memory.

| # | Scenario | What it answers |
|---|---|---|
| S0 | idle, nothing changes | Do idle nodes cost anything? (Expected ~0 for both.) |
| S1 | move 300 prims per frame (5× the ObjectUpdate rate seen at the spot) | cost per moved prim |
| S2 | toggle `Visible` on 2,000 per frame | worst case of the cull sweep |
| S3 | swap mesh and re-send 6 overrides on 200 per frame | LOD-swap cost |
| S4 | create 30k under a 2 ms/frame budget | region load: frames and total ms |
| S5 | remove 30k under the same budget | region leave: total ms **including the delete queue** |
| S6 | windowed, Separate mode, camera on 15k of them | `renderCpuMs`/`draws` must be equal for A and B (confirms "no render-side gain") |

Run S0–S5 headless, where the dummy renderer isolates the scene-tree cost, and again windowed. Also
cast 1,000 rays at scaled prims: A and B must give the same hits (physics parity for Phase 3).
Memory traps: use `--log-file user://logs/selftest/...`. A headless run rewrites `project.godot`, so
restore it afterwards.

### Gate: go or no-go for Phase 2

**Go** if any one of these holds:
1. P0-a or P0-b at the spot shows ≥ 3 ms of real flush, and the bench attributes at least half of that
   kind of cost to prim nodes.
2. The bench shows RID create + remove at least 3× cheaper, and the region-leave and teleport frames
   at the spot carry at least 50 ms of `RemoveVisual` plus delete queue.
3. The maintainer schedules FEAT-PERF-20 (merged cells). It switches "drawn by" on ~20k prims, which is
   simpler without nodes.

**No-go:** ship Phase 1, do FEAT-PERF-14/20/21 on nodes (each works there, see below), and park
Phase 2+. Record the decision in this spec.

## What hangs on a prim node today, and what it becomes

Line numbers are `app/scripts/ObjectRenderer.cs` unless another file is named.

| # | Feature | Today | With RIDs | Node? |
|---|---|---|---|---|
| 1 | Geometry | `VisualState.MeshInstance` (:44), made in `CreateVisual` (:1369-1383), set in `AssignSharedMesh` (:5161) | `InstanceCreate2(mesh.GetRid(), scenario)` / `InstanceSetBase`. `ArrayMesh` stays in GpuCache, and `VisualState` holds a strong ref. | no |
| 2 | Per-face materials | `SetSurfaceOverrideMaterial` (:3764-3773), `MaterialOverride` (:3711, :3729), one `ShaderMaterial` per face | `InstanceSetSurfaceOverrideMaterial` / `InstanceGeometrySetMaterialOverride`, plus a managed `ShaderMaterial?[]`. **Re-send every override after a base change:** RS clears them (`renderer_scene_cull.cpp:689`), while `MeshInstance3D._mesh_changed` re-sends them (`mesh_instance_3d.cpp:412-436`). Without this, every LOD swap flashes untextured. | no |
| 3 | Transform, scale | `Scale` (:3005), `Position` (:3095), `Quaternion` (:3099) | `InstanceSetTransform` with the composed basis. Managed `Pos`/`Scl` exist (:147-149); add `Rot`. | no |
| 4 | Draw-distance visibility | `Visible` in the cull sweep (:913-914) | `InstanceSetVisible`. Later a visibility range (FEAT-PERF-14). | no |
| 5 | Shadow casting | size rule (:5169-5176), distance cull (:930-950, which reads `CastShadow` back at :939) | `InstanceGeometrySetCastShadowsSetting`, with the setting mirrored | no |
| 6 | Split sorted surfaces (BUG-RENDER-16) | child `MeshInstance3D` per surface, Hidden material on the parent (:5396-5454) | one extra RS instance per split surface, using the one-surface copy (`MeshSurfaceCache`) and the same transform | no |
| 7 | Sort offset, tie-break, hysteresis | `SortingOffset` (:2518, :2645, :2702, :5448), centre from `GetAabb`/`GlobalTransform` (:2630-2631, :2689) | `InstanceSetPivotData(rid, offset, true)`, the same call `visual_instance_3d.cpp:157-159` makes. Centre from the cached local AABB × managed transform. | no |
| 8 | Alpha census | once a second reads `Visible`, `Mesh`, `Position` and every surface material through interop (:2479-2507): **13.7 ms/s in one burst** ([PhaseCost], 2026-10-10) | reads a managed per-surface kind (Opaque / Cutout / Sorted / Hidden / Mirror), set wherever a shader is assigned: material build, `ApplyAlphaCutout`, mirror swap, viewer-spec variant | no |
| 9 | LOD swap (prim tessellation, mesh LOD) | sweep (:1098-1139) → `LoadAndApply*Async` → `AssignSharedMesh` | `InstanceSetBase`, then re-send overrides (row 2) | no |
| 10 | MultiMesh groups (FEAT-PERF-06) | member's node `Mesh` nulled and restored (ObjectInstanceGroups.cs:102, :162, :226); a group is a `MultiMeshInstance3D` (:270-345) | while grouped, the member's instance is hidden; groups (~220) stay nodes in Phase 2 | group: yes (few) |
| 11 | Texture animation | shader params on face materials (:2399-2436, :2800-2838) | unchanged (materials are resources); FEAT-PERF-16 moves it into the shader | no |
| 12 | Prim-scale uniform | through the node's surfaces (:5291-5305) | the managed material array | no |
| 13 | Light | `OmniLight3D` child (:3046-3068) | a satellite node under a flat `PrimLights` root, written with the prim's **full** global transform | yes (rare) |
| 14 | Particles | `ObjectParticles : CpuParticles3D` child (:3070-3086). It reads its own world scale and uses `LocalCoords` for follow-source (ObjectParticles.cs:232, :313). | satellite under `PrimParticles`, with the exact global transform including scale, so ObjectParticles does not change | yes (rare) |
| 15 | Collision | `StaticBody3D` + `CollisionShape3D` children (:1373-1380), layer (:3019), shapes (:5679-5973), metas `EntityId`/`LocalId` (:1377-1378, :636) | Phase 2: a flat `StaticBody3D` node, so callers do not change. Phase 3: a `PhysicsServer3D` body with the same transform `CollisionObject3D` sends, scale included (`collision_object_3d.cpp:96-104`). | Phase 2 yes, Phase 3 no |
| 16 | Click pick, touch face/ST | metas (ObjectSelectionController.cs:567-575); `TrySurfacePick` uses `GlobalTransform` (:1428, :1471) | ray results carry `rid` and `face_index` (`physics_server_3d.cpp:375-381`) → `PickResolver`; `TrySurfacePick` uses the managed transform | no |
| 17 | Hover cursor | metas (Input/CursorManager.cs:143-177) | `PickResolver`. Hover never promotes. | no |
| 18 | Avatar ground ray, phantom | `collider.CollisionLayer` (AvatarController.cs:495-530), collider name for diagnostics (:1255-1340) | `PickResolver` gives the layer from the managed mirror | no |
| 19 | Depth-of-field focus | hit position only (DepthOfFieldController.cs:198) | unchanged | no |
| 20 | Selection outline | child `MeshInstance3D` (SelectionOutline.cs:225-279; :2231-2274) | **promotion** (below) | while selected |
| 21 | Edit gizmo | works from `TransformComponent` + `NotifyComponentUpdated` (UI/SelectionGizmo3D.cs:452-459, :1023) | unchanged; the drag reaches the prim through `UpdateVisual` | no |
| 22 | Mirrors | layer swap (:5023-5058), shader swap (:4986-5021), position/extents/normal from `GetAabb`/`GlobalTransform` (:800-824, :4912-4949) | `InstanceSetLayerMask` on the prim and its split instances; geometry from the cached AABB × managed transform | no |
| 23 | Cull sweep (BUG-PERF-11) | still reads `CastShadow` (:939), bounds through `EffectiveBoundingRadius` (:969, :1071, :5547-5566), `ComputeTextureLod` (:1030 → :4660-4668), `EvaluateInstancing` (:5626-5633) | cached radius, local AABB, surface count and per-surface kind/fingerprint: no interop | no |
| 24 | MOAP media faces | albedo swap on the material (:3821-3838) | unchanged | no |
| 25 | Animesh | the prim node keeps no mesh but anchors children (:193-197) | no instance base; satellites carry light/particles; control avatars stay in AvatarRenderer | no |
| 26 | Diagnostics | `TickTraceObject`, `LogLiveMaterials` read node state (:1297-1352, :1614ff) | read the managed table; the trace line says `drawable=rid\|node` | no |
| 27 | Rekey | `SetMeta("LocalId")` (:631-638) | update the managed LocalId / the RID→entity map | no |

## Design

### Where it lives

- **App layer, next to ObjectRenderer.** The bridge needs both `World` and the engine, so per AGENTS.md
  it belongs in `app`. `src/` does not change, and no RID type enters `SLNG.Core`.
- **"Owned by the ECS"** means: keyed by the world's entity id and driven only by world events
  (`EntityAdded/Removed/ComponentUpdated`, :579-584), as today. What changes is that the per-entity
  record becomes plain managed data plus RIDs instead of a node tree.
- **One seam: `IPrimDrawable`.** It covers `SetMesh`, `SetSurfaceMaterial(i)`, `SetOverride`,
  `SetTransform`, `SetVisible`, `SetCastShadow`, `SetLayers`, `SetSortOffset`, split instances, `Free`.
  It has two implementations:
  - `NodePrimDrawable`: today's behaviour, also used for promotion;
  - `RidPrimDrawable`.

  ObjectRenderer writes only through the seam and **reads only managed fields**.
- **New `VisualState` fields:** `ArrayMesh? Mesh`, `ShaderMaterial?[] Surfaces`,
  `ShaderMaterial? Override`, `Aabb LocalAabb` (from MeshData, computed on the worker), `Quaternion Rot`,
  `ShadowCastingSetting Cast`, `uint Layers`, `float SortOffset`, `SurfaceKind[] Kinds`.
- **Scenario and physics space** come from `GetWorld3D()` of the ObjectRenderer node. The `World3D`
  property may not be the world in use (the SubViewport trap). The mirror's SubViewport culls by layer
  (MirrorReflection.cs:95) in the same world, so RID instances show in it as nodes do. Phase 2 checks it.

### Resource lifetime

- An RS instance does **not** keep its mesh or materials alive. Today the node does, through `Ref`.
  `VisualState` must hold strong C# refs to the `ArrayMesh` and to every `ShaderMaterial`. Otherwise
  the GC frees them under a live instance, and the result is missing geometry or default materials.
  GpuCache ref-counting does not change.
- RIDs are freed explicitly (`RenderingServer.FreeRid`, `PhysicsServer3D.FreeRid`) in `RemoveVisual`
  and `_ExitTree`. The removal budget then covers the whole cost, with nothing left over for the delete
  queue. A self test counts live RIDs after create/remove cycles.

### Threading and batching

- All `RenderingServer` and `PhysicsServer3D` calls run **on the main thread**, from `_Process` and the
  `MainThreadWorkQueue` lanes. The physics server is not thread-safe here. In Safe mode, RS calls from
  other threads are deferred to the main flush, which would reorder them against the main thread's
  writes to the same instance.
- **Setters only.** Every instance call we need is an async `FUNCn`
  (`rendering_server_default.h:935-968`; `instance` is `FUNCRIDSPLIT`, so the RID is allocated on the
  caller). Getters are round trips in Separate mode. The managed mirror is the only source of truth
  for reads, and a `--diag` counter flags any getter in the sweep.
- **Prepare on workers:** local AABB and radius from MeshData, trimesh faces (already done), merged-cell
  arrays (FEAT-PERF-20), occluder vertices (FEAT-PERF-21).
- **Batching:** RS has no bulk instance call, but each setter is a cheap queue push. `UpdateVisual` is
  already coalesced per entity. MultiMesh groups move to one `MultimeshSetBuffer` per changed group
  instead of a `SetInstanceTransform` per member.

### Physics

- **Phase 2:** a flat `StaticBody3D` + `CollisionShape3D` under a `PrimBodies` root, with its global
  transform written where the node transform is written today. The four callers (rows 16–18) do not
  change. This keeps Phase 2's A/B about rendering only.
- **Phase 3:** `PhysicsServer3D` bodies. `BodyCreate`, `BodySetMode(Static)`,
  `BodyAddShape(sharedShape.GetRid())`, `BodySetSpace`, `BodySetState(Transform)`,
  `BodySetCollisionLayer` (phantom). `BodyAttachObjectInstanceId(ObjectRenderer)`, so `collider` is
  never null. A `Dictionary<Rid, Guid>` maps bodies to entities. `PickResolver.TryResolve(hit, ...)`
  replaces meta reads in all four callers and also understands the avatar nodes AvatarRenderer keeps.
  The ray parity test from P0-c guards scaled prims, phantom and backface behaviour.

### Promotion to a node, and demotion

- **Triggers:** a linkset is selected or highlighted (`HighlightVisual`, :2137-2192, already loops the
  linkset and calls `SuppressInstancing`), or the edit window holds it.
  **Not** hover, and **not** a touch click: `PickResolver` and `TrySurfacePick` need no node.
- **Promote:** build a `NodePrimDrawable` from the managed state, with the same `ArrayMesh`, the same
  `ShaderMaterial` objects, transform, layers, shadow, sort offset and split instances. Then
  `InstanceSetVisible(rid, false)` in the same frame. Both commands are queued in order, so nothing
  flickers. The outline code (SelectionOutline) works unchanged on that node.
- **While promoted,** the seam has exactly one live drawable, so no write can land on the hidden one.
- **Demote** 2 s after deselect: re-selecting within that time costs nothing. Copy back, show the RID,
  `QueueFree` the node. At most 200 promotions per frame, so a 2,000-prim linkset spreads its highlight
  over a few frames.
- **The A/B toggle uses the same code:** switching the backend at runtime promotes or demotes
  everything, under the same budget. No restart is needed.

### Satellites (lights, particles)

They are rare (tens to hundreds). They stay nodes, under flat roots, and get the prim's full global
transform including scale. That transform is written at the same place `Position`/`Quaternion`/`Scale`
are written today (:2999-3013, :3090-3105), so ObjectParticles' world-scale logic sees what it sees
today. Instancing still excludes prims with satellites (:5607-5608).

## What builds on it

- **FEAT-PERF-14 (cull by size):**
  - `InstanceGeometrySetVisibilityRange(rid, 0, end, 0, margin, Disabled)`, with `end` from the bounding
    radius per FEAT-PERF-14's rule, capped at draw distance × 1.15 (the sweep's hide edge, :855).
  - Set at mesh assign and on scale change; a draw-distance change re-sets them over a few frames.
  - The engine checks ranges in C++ per instance (`VIS_RANGE_CHECK` in `_scene_cull`,
    `renderer_scene_cull.cpp:2924`; per-viewport bins `:3319-3341`). The sweep stops writing visibility
    for those decisions.
  - The sweep keeps release/reload (VRAM) at min(avatar, camera) distance (BUG-NET-01), because engine
    ranges measure from the camera only.
  - **This works on nodes too** (`GeometryInstance3D.VisibilityRangeEnd`, `visual_instance_3d.cpp:262-264`),
    so FEAT-PERF-14 need not wait for this task.
  - **MultiMesh groups need a cell key first.** A group has one AABB and one range, so a group spread
    over the region is never culled. Key groups by 32 m cell + mesh + material + shadow; then a group
    range from the member radius means something.
- **FEAT-PERF-20 (merged cells):**
  - Per (cell, material fingerprint, shadow flag), a worker builds one `ArrayMesh` from members'
    MeshData × transforms. We already hold that CPU data, so there is no read-back. The main thread
    commits one RS instance and hides the members' own instances.
  - Alternatively HLOD: the merged instance is visible beyond D and the members within D, via
    `InstanceSetVisibilityParent` (`renderer_scene_cull.cpp:1402`).
  - **Eligible:** prims with no update for 30 s, not selected, no texture animation, no media, no
    satellites, not sorted-transparent.
  - Any update or selection puts the member back on its own instance at once. The cell rebuild is
    debounced (2 s) on a worker. Physics bodies stay per prim, so picking does not change.
  - Draws then scale with cells × materials instead of faces. With RIDs, "drawn by" is one enum
    (`Own | Group | Cell | Hidden`) instead of nulling and restoring `node.Mesh`.
- **FEAT-PERF-21 (occluders):**
  - `OccluderCreate` + `OccluderSetMesh` (`renderer_scene_cull.cpp:166-170`) +
    `InstanceCreate2(occluder, scenario)`, one per cell, with vertices built on a worker from large
    static opaque prims: every face opaque, extent ≥ ~2 m in two axes.
  - Never from alpha, cutout or media faces: that is the false-occlusion risk.
  - Needs `rendering/occlusion_culling/use_occlusion_culling`. Check first that the occlusion culler is
    present in the export templates.

## Expected numbers (crowded spot)

Estimates, to be replaced by the Phase 0 numbers.

| Measure | Today (v0.27.31) | After Phase 1 | After Phase 2–3 | Plus 14 / 20 / 21 |
|---|---|---|---|---|
| Scene nodes | ~56k | ~56k | **~4–6k** (+ promoted + satellites) | same |
| Prim C# node wrappers (GC) | ~52k | same | ~0 | same |
| `obj-ticks.alpha-census` | 13.7 ms/s, one ~14 ms burst per second | **< 1 ms/s** | same | same |
| `cull.scan` per slice | 4.6 ms avg (of which `res` 2.1 stays) | −~1 ms | same | less (the engine culls) |
| Real flush (Safe `postFlushMs`) | 12–17 ms (v0.27.28) | same | −0…2 ms (prims rarely move; avatar share untouched) | same |
| Region leave / teleport | `QueueFree` tail, unbudgeted | same | `FreeRid` inside the budget, est. 3–5× cheaper | same |
| Draws / `renderCpuMs` / `renderGpuMs` | 14–15k / 18–26 / 28–100+ | unchanged | **unchanged** | draws est. −40…70 % |
| fps at the spot | 14–16 | +0…1 | +0…1 | where the gain is |

## Risks and known unknowns

1. **The gain is smaller than the ADR hoped.** The source reading says so. Phase 0 gates it.
2. **Parity hidden in the node API:**
   - override re-send on mesh change (row 2);
   - `GeometryInstance3D` defaults vs RS instance defaults: layers, cast shadow, LOD bias, transparency;
   - `instance_attach_object_instance_id` (`visual_instance_3d.cpp:209`), which we do not rely on.

   A parity self test builds one prim on each backend and compares every value we set.
3. **Lifetime:** a resource freed under a live instance. Strong refs plus the RID-leak self test.
4. **Two drawables alive during promotion.** The seam allows only one live writer.
5. **Physics parity (Phase 3):** scaled transforms, phantom layer, `BackfaceCollision`, the avatar's
   ground ray. The ray parity test covers these.
6. **Mirror and hero probe:** layer masks and the SubViewport's world. Phase 2 checks them visually.
7. **Debuggability:** prims vanish from the remote scene tree. `--trace-object` reads the table instead.
8. **P0-a runs managed code on the render thread every frame.** It is `--diag` only. If it misbehaves,
   P0-b (Safe mode) alone still answers the question.
9. **File ownership.** This task owns `ObjectRenderer*.cs`, `ObjectInstanceGroups.cs` and, in Phase 3,
   the pick callers while it runs. FEAT-PERF-14/16/20/21 touch the same files (MVP6-1 "Objects" set):
   one writer at a time.

## Phased plan

| Phase | Content | Setting | Size |
|---|---|---|---|
| **0 Measure** | P0-a split, P0-b Safe-mode run, P0-c bench; numbers and gate decision written here | `--diag`, `--prim-bench` | ~1 day |
| **1 Decouple** | managed render state + `IPrimDrawable` with `NodePrimDrawable` only; every node **read** in the per-frame paths removed (census, sweep, hysteresis, texture LOD, mirror scan, `TrySurfacePick`, diagnostics). No behaviour change; existing self tests plus a "no interop read in the sweep" counter | none (pure refactor) | 2–3 days |
| **2 RID backend** (only if the gate passes) | `RidPrimDrawable` for prims without satellites; split surfaces as extra instances; promotion and demotion; flat `StaticBody3D` nodes; live switch | `--prim-backend nodes\|rids`, Preferences › Graphics › Advanced; default `nodes` | ~1 week |
| **3 Bodies and satellites** | `PhysicsServer3D` bodies + `PickResolver` in 4 callers; flat light and particle satellites; animesh prims; trace | same setting | 3–4 days |
| **4 A/B and default** | in-world A/B in both thread modes; flip the default after sign-off. `NodePrimDrawable` stays: it is the promotion drawable. | — | — |

**In-world A/B** (the FEAT-PERF-13 procedure):
1. Baseline spot, same settings, `--diag`, wait for `queue=0`, then stand 2 minutes. Once with `nodes`,
   once with `rids` (the live toggle is fine).
2. Compare:
   - `[Perf]`: `nodes=`, fps, low1%, `scriptsMs`, `postFlushMs` with `flushMs`/`tailMs`/`rtBusyMs`,
     `renderCpuMs`, `renderGpuMs`, draws;
   - `[PhaseCost]`: `cull`, `obj-ticks.alpha-census`;
   - `[WorkCost]`: `visual.create`.
3. Teleport away and back: worst frame, `[RegionLoad]` scene nodes.
4. Parity checklist:
   - selection outline (root and child colour), edit drag, touch face and ST, right-click menus, hover
     cursor;
   - walking on prims and through phantom ones;
   - planar mirror and hero probe, foliage alpha order;
   - LOD swaps while walking, `[Instancing]` groups;
   - lights, particles, MOAP faces, animesh.

## Acceptance Criteria

- [ ] Phase 0 numbers are in this spec: the P0-a split at the spot in both thread modes, and the P0-c
      table for S0–S6, nodes vs RIDs. The gate decision is written down with its reason.
- [ ] Phase 1: no node property reads in ObjectRenderer's per-frame paths. `obj-ticks.alpha-census`
      ≤ 1 ms/s at the spot. All existing self tests pass.
- [ ] Phase 2–3 (if go): with `rids`, `nodes=` ≤ 10k at the spot, and no visual or interaction regression
      on the parity checklist. Parity self tests run both backends, and no RID leaks after 3
      create/remove cycles.
- [ ] Before/after `[Perf]` at the baseline spot in this spec (MVP6-1 rule).
- [ ] Both builds, `dotnet test`, `dotnet format`, `--selftest` pass (`/slng-verify`). `AppVersion` is
      bumped per phase.

## Technical Specs & Affected Files

- `app/scripts/ObjectRenderer.cs`, plus new partials `ObjectRenderer.Drawable.cs` and
  `ObjectRenderer.Promotion.cs`
- `app/scripts/PrimDrawable.cs` (`IPrimDrawable`, `NodePrimDrawable`, `RidPrimDrawable`)
- `app/scripts/PrimBodies.cs`, `app/scripts/PickResolver.cs` (Phase 3)
- `app/scripts/ObjectInstanceGroups.cs`: member hiding through the seam, `MultimeshSetBuffer`, cell key
- `app/scripts/ObjectSelectionController.cs`, `app/scripts/Input/CursorManager.cs`,
  `app/scripts/AvatarController.cs`: `PickResolver` (Phase 3)
- `app/scripts/FrameTimeline.cs`, `app/scripts/UI/StatsOverlay.cs`: P0-a fields
- `app/scripts/AvatarRenderer.cs` (`BindRiggedSkin` / `SharedOrNewSkin`, `[AvatarCost]`), `src/SLNG.Core/Avatars/SkinBindKey.cs`, `app/scripts/AvatarRenderer.SharedSkins.SelfTest.cs`: skin sharing (2026-10-16)
- `app/scripts/SelfTest.PrimBench.cs` (P0-c), `app/scripts/SelfTest.PrimBackend.cs` (parity, RID leaks)
- `app/scripts/RenderConfig.cs`, `app/scripts/Diagnostics.cs` (`--prim-backend`),
  `app/scripts/UI/GraphicsSettings.cs`, `app/scripts/UI/GraphicsPreferencesPage.cs` (toggle)
- `app/scripts/Boot.cs` (`AppVersion`)
- No change in `src/`.

## Sub-tasks / Progress

- [x] P0-a: FrameTimeline flush / tail / render-busy split (`--diag`) + ordering self test (v0.27.64)
- [x] Sirens 2026-10-16: postFlushMs explained (skin-bind commands), skin sharing built (v0.27.64), in-world A/B: 21 → 26.5 fps, flushMs 14.8 → 6.7
- [ ] P0-b: Safe-mode run at the baseline spot, numbers recorded here
- [ ] P0-c: `--prim-bench` S0–S6 + ray parity, headless and windowed, table recorded here
- [ ] Gate decision written here
- [ ] Phase 1: managed render state + `IPrimDrawable` (nodes only), node reads removed from per-frame paths
- [ ] Phase 2: `RidPrimDrawable`, split instances, promotion/demotion, flat bodies, setting + live switch
- [ ] Phase 3: `PhysicsServer3D` bodies + `PickResolver`, satellites, animesh, trace
- [ ] Phase 4: in-world A/B in both thread modes, default decision
