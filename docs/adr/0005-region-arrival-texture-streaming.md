# ADR 0005: Arriving in a crowded region: textures stream, avatars rank by size

- Status: proposed
- Date: 2026-10-31
- Refines ADR 0004 P4 (one importance scheduler) and amends P3's jelly-doll rule.
- Goal (maintainer, 2026-10-31): a significant improvement when entering a new region.

## Context

### Symptom

After arriving at a crowded region, almost every avatar stays white or grey for a long time. One
avatar (Bronte) kept a grey head. Zooming in and out brought her face back, and then the rest of her
loaded again.

### What was measured

One run per row, KinkyEvent on Agni, 69 avatars, `tools/run-client.ps1 -Diag`.

| Measure | Value |
|---|---|
| `[TexPipe] inflight` | 69 → 4783, then back to 0 over several minutes |
| `[TexWait]` p95, worst interval | 172 s waiting for a slot |
| `[TexHttp]` | 8 slots, p50 60–85 ms |
| Transferred | ~5.6 GB, ~460–485 KB per texture |
| `-TexSlots 12` | 115 MB per interval instead of 125–130 MB: no gain, higher latency per texture |
| `[AvatarLimit]` | 13 full, 56–58 reduced, cap 11 |
| `[GpuCache]` budget | 3456 → 1473 MB; cache 1678–2247 MB, all pinned, `lodBias=2` |
| Adapter | 11,089 of 12,010 MB in use |
| `mainQueue` | 14,000–17,000 items |
| Shrinks / re-decodes / parked outfits unloaded | 1206 / 5111 / 39 |
| `GetDisplayNames` | 848 of 849 failed with `capability=False` |
| Material path (LMV limiter) | p50 195–590 ms, peak 1 in flight, ~8 ids per request (max 41) |

**The log has no timestamps.** Every rate above assumes the 5–6 s report interval measured while
idle. If that holds, the transfer ran above 160 Mbit/s on a line sold as 100 Mbit/s. Either the line
is faster than that, or the interval stretches under load. We do not know which.

### What the code does (read 2026-10-31, `main` at 7adb71b6)

1. **Over-cap avatars are never textured.** A reduced avatar is drawn in the jelly-doll colour and
   its bakes are not fetched (`app/scripts/AvatarRenderer.cs:1796-1798`). `RenderConfig.cs:21-22`
   still describes the stand-in as "base body with bake textures". The code does not do that.
2. **Other avatars' textures wait behind all scenery.** The texture slots are a strict
   max-priority queue, first-in-first-out only among equal priorities (`src/SLNG.Core/PriorityGate.cs:31-34`,
   `src/SLNG.Net/GridSession.Assets.cs:643-645`).
   - Scenery asks with its apparent size, always above 0 (`ObjectRenderer.cs:5219-5224`).
   - Every avatar texture asks with 0 unless it belongs to the focus avatar
     (`AvatarRenderer.FocusPriority.cs:31-32`, call sites `AvatarRenderer.cs:2351`, `:4321`, `:4625`).
   - Only your own avatar is lifted, to 1e9 (`GpuCache.cs:483`, `:502`).

   So a bake of any other avatar gets a slot only when no scenery texture is waiting, which means at
   the end of the backlog. The priority is fixed when the request joins the queue
   (`PriorityGate.cs:19-26`).
3. **Every texture is fetched in full.** `GpuCache` always asks for discard 0 (`GpuCache.cs:1889-1907`).
   The screen size only drives a reduce during decode and a shrink after it. Network-side discard was
   built in FEAT-PERF-02 Phase 2 and switched off after a live regression
   (`docs/specs/FEAT-PERF-02-texture-loading-speed.md`, lines 58-76): Magick.NET rejects a partial
   codestream, and the CoreJ2K fallback "frequently failed" at high discard. Nobody found the root
   cause.
4. **The VRAM budget subtracts everything else the process holds** (`src/SLNG.Core/VramBudgetPolicy.cs:131-155`):
   Godot's memory outside the cache, slack up to 15 % of the OS budget, headroom, and (since
   BUG-PERF-19) every other program on the card. The `[GpuCache]` line prints only the result, not
   these terms, so we cannot say why the budget fell from 3456 to 1473 MB.

### What the reference viewer does (`scratch/slviewer`)

- **Over-cap avatars are impostors, not jelly dolls.** `shouldImpostor` ranks each avatar against
  `sMaxNonImpostors` (`llvoavatar.cpp:11001-11012`). An impostor is the avatar rendered into a texture
  and refreshed less often (`llvoavatar.cpp:10996-10999`), so its bakes still load. Solid colour is only
  for avatars that are visually muted: too complex, too slow, or on the mute list
  (`llvoavatar.cpp:3923-3962`).
- **Other avatars' bakes rank by pixel area, like scenery.** `BOOST_AVATAR_BAKED` is below `BOOST_HIGH`
  (`llrender/llgltexture.h:52-56`), so its priority comes from the face's pixel area
  (`llviewertexturelist.cpp:905-1016`, `llvoavatar.cpp:5900-5913`). Only your own avatar and the
  HUD/UI are boosted. The priority is read again on every update and passed to the fetch that is
  already running (`llviewertexture.cpp:2048-2093`).
- **Textures stream.**
  - An unknown texture is first requested as 600 bytes at the coarsest discard
    (`lltexturefetch.cpp:2587-2593`, `lltexturecache.cpp:51`, `llimage/llimage.h:64`).
  - Once the header gives the size, the next request asks for the bytes that the wanted discard
    needs (`lltexturefetch.cpp:2580-2586`).
  - Partial codestreams are decoded on purpose, with strict mode off
    (`llimagej2coj/llimagej2coj.cpp:420-421`).
- **Under memory pressure it raises a discard bias** that drops off-screen textures first
  (`llviewertexture.cpp:533-625`). It does not unload outfits.
- **Transport:** 8 connections, up to 12, pipelined (`llappcorehttp.cpp:71-74`). We use the same
  number of slots, and the `-TexSlots 12` run shows that more slots do not help.

## Decision

### 1. Ranking of the causes of "grey for a long time"

| # | Cause | Evidence | What it explains |
|---|---|---|---|
| 1 | Over-cap avatars draw no bakes | Code (proven). 56 of 69 avatars were reduced | Most grey avatars. They stay grey until promoted |
| 2 | Other avatars' textures at priority 0 | Code (proven mechanism). The effect is not measured | Full avatars stay grey until the scenery backlog drains, which takes minutes |
| 3 | Every texture at full resolution | Code, plus the measured bytes | The length of the backlog (5.6 GB). It makes the line the bottleneck |
| 4 | VRAM pressure | Measured. Why the budget falls is unknown | Reloads after arrival (Bronte), blur, re-decodes |
| 5 | The line | Indirect: 12 slots were not faster | Fixed. Causes 2–4 decide how much of it is wasted |

Causes 1 and 2 decide *who* waits. Cause 3 decides *how long* everyone waits. Cause 4 undoes work
that was already done. We cannot change the line (5), so the plan is to need less of it.

Bronte fits causes 2 and 4. She was a full avatar whose head bake waited at priority 0. Then
`full->parked` at 42 m unloaded her outfit, and `reduced->full` at 4.5 m loaded it again. Her grey
head has no log line of its own. A missing GPU resolution is plausible but not proven.

### 2. Separate tasks, one PR each

Avatar priority and texture bytes are separate tasks, for three reasons:
- They touch different files.
- They carry different risk: one is a priority function, the other a pipeline change behind a setting.
- Each must be measurable on its own.

The task ids below are proposals. They go into `docs/ROADMAP.md` when this ADR is accepted.

| Order | Task (proposed id) | What | Size |
|---|---|---|---|
| 0 | FEAT-PERF-28 Arrival time base | Measurement only | S |
| 1 | BUG-PERF-24 Avatar textures rank by size | Priority | S |
| 2 | BUG-AVATAR-19 Reduced avatars draw their bakes | Parity | S–M |
| 3 | FEAT-PERF-29 Progressive texture streaming | Spike, then build | L |
| 4 | BUG-PERF-25 The VRAM budget falls under load | Measure (in task 0), then fix | M |
| Any time | BUG-NET-38 Display names asked before the cap | Small fix | S |

**Task 0: FEAT-PERF-28, the time base.** It changes no behaviour.
- Every `Diag.Write` line gets `t=<seconds>` from a monotonic clock (`src/SLNG.Core/Diag.cs:34`), so
  rates no longer depend on an assumed interval.
- One `[Arrival]` line per region entry gives milestones from the handshake:
  - every full avatar has every bake at some resolution
  - no face on screen is without a texture
  - texture inflight is back to 0
  - the bytes transferred until each of these
- `[TexWait]` is split by class: own avatar, focus avatar, other avatars' bakes, other avatars' worn
  textures, scenery.
- Every budget change logs the `VramBudgetPolicy` terms: OS budget, OS usage, Godot usage, cache,
  slack counted, other programs on the card, card budget.
- At +30 s and +60 s, a count of reduced avatars and of full avatars without bakes. This separates
  cause 1 from cause 2.
- Check the line in Task Manager once.

**Task 1: BUG-PERF-24.** Another avatar's textures ask with a priority on the same scale as
scenery's (apparent size, roughly 0 to 1). The input is the pixel area `AvatarRenderer` already keeps
per avatar (`AvatarVisual.ScreenPixelArea`, set at `AvatarRenderer.cs:1668` and `:7975`); a raw pixel
area would outrank all scenery, so it is converted, not passed through. The focus avatar and your own
avatar keep their boosts. The pure function lives in `SLNG.Core`, with
tests:
- an avatar and scenery of equal size rank equally
- the focus avatar ranks above both
- your own avatar ranks above everything

Re-queueing on camera movement stays out (see `PriorityGate`'s note). With streaming, each request is
small, so a stale rank costs one small request, not a whole texture.

**Task 2: BUG-AVATAR-19.** A reduced avatar draws the base body with its bakes, as
`RenderConfig.cs:21-22` already says. The jelly-doll colour stays until the first bake arrives, and
for the viewer's visually-muted cases. This amends ADR 0004 P3, which kept the jelly doll as the
fallback "for avatars never loaded".
- Rough cost: 56 avatars × about 5 base-body bakes. The spec measures it.
- Open point for the spec: a bake's alpha (from alpha layers) must not cut holes in a stand-in that
  has no mesh body over it.

**Task 3: FEAT-PERF-29, progressive streaming.** Two PRs.
- *Spike, offline from the cache.* Cut real cached codestreams at every discard boundary that
  `J2kByteSizeEstimator` gives, and decode them on the path the pipeline would take. This answers why
  Phase 2 failed. `AssetService.cs:1270-1285` has already measured two things: CoreJ2K decodes partial
  streams (mean error 4.2/255 with 2 % of the bytes), and Magick throws on them. The suspects:
  - the Magick-first path
  - the degraded / `J2cCodestream.IsTruncated` handling, which treats an intended partial stream as
    broken
  - the byte estimate itself
- *Build, behind a setting, with an in-world A/B.*
  - The first request asks for the coarsest discard: the viewer's 600 bytes, or the estimator's
    discard-5 size.
  - Refinement then asks for the discard the pixel area needs, at the same priority.
  - The disk cache keeps partial bytes together with their discard.
  - An intended partial decode is not "degraded".
  - In passing, fix the stale comment at `GridSession.Assets.cs:275-277`, which still says MeshPipe
    runs through LibreMetaverse (untrue since FEAT-NET-07).

**Task 4: BUG-PERF-25.** Its own task. The fix waits for the terms that task 0 logs. Measure again
after task 3, because streaming changes how much texture memory there is.

### 3. VRAM pressure is its own task, measurement first

Yes, as task 4. It is the most likely cause of Bronte's reload. But until the terms are logged, any
fix is a guess at which term grew.

### 4. FEAT-NET-07 step 1, variant A (batching material ids): not built now

The material path is not on the critical path:
- p50 is 0.2–0.6 s, with one request in flight.
- Batching saves about 0.4 s median per batch.
- LMV lets a request through even when its queue is full.

Build variant A only if, after tasks 1–3, the `[Arrival]` trace shows materials among the last
things to finish. Reading `RenderMaterialsCapability` and `MaxMaterialsPerTransaction` from
`Simulator.Features` would be part of it then.

This ADR does not judge steps 2–4 (own LLMesh decoder, inventory over AIS, the plan for the object
decoder). None of them showed up on arrival.

## What "significant" means

Measure at KinkyEvent (or the MVP6-1 spot) with the task-0 time base:
- cold and warm cache
- at least 3 runs per variant

Take the baseline first. The targets are relative to it:

| Metric | Target |
|---|---|
| Arrival → every full avatar has every bake (any resolution) | At most half the baseline |
| Arrival → no face on screen without a texture | At most half the baseline |
| Bytes until texture inflight is back to 0 | At most half the baseline |
| Reduced avatars without any bake at +60 s | 0 |
| `parked outfit unloaded under VRAM pressure` in the first 5 min, 12 GB card | 0 |

## Consequences

- **Streaming uploads a texture more than once:** coarse first, then refined. That adds
  `texture.upload` work on the main thread, while `mainQueue` already holds 14–17k items (memory
  `mainthread-workqueue-is-the-bottleneck`). So:
  - refinement replaces the texture in place
  - it runs only when the needed discard is below the loaded one
  - the A/B measures main-thread time, not just bytes
- **The disk caches learn partial entries** (the raw cache, and the decoded cache from FEAT-PERF-11).
  A warm start may show a coarse texture first. Refinement must not download again what is already
  cached.
- **Coarse alpha textures lose their silhouette** (BUG-RENDER-15). The streaming spec sets the floor.
- **Sculpt maps keep full fetches.** They already skip the reduce (`AssetService.cs:1852`).
- **TPV:** nothing here touches permissions or export. Streaming asks the same caps for fewer bytes.
- **Every task:** test first, an `AppVersion` bump, `/slng-verify`, one PR. Nothing is committed or
  merged without the maintainer's yes.

## Open ideas (not decided)

Each one needs a task-0 measurement before it becomes a task.

| Idea | Why it might help | What decides it |
|---|---|---|
| Cancel fetches nobody wants any more | The viewer drops a request whose priority stayed 0 for 5 s (`llviewertexture.cpp:2088-2093`). Our queue keeps textures of objects and avatars that are gone or left behind | The share of served textures whose owner is already gone |
| Re-rank queued requests | The viewer re-reads the priority on every update. Ours is fixed when the request joins the queue | Only if, after streaming, `[TexWait]` by class still shows stale ranks |
| Discard bias while the backlog is long | The viewer raises a discard bias under memory pressure (`llviewertexture.cpp:533-625`). We could also raise it for low-rank textures while inflight is large | The backlog length after task 3 |
| Fetch only the mesh LOD in use | The viewer fetches only the LOD it needs. Whether our mesh fetch does is unmeasured | `[MeshPipe]` bytes per mesh compared with the LOD in use |
| Pre-warm from the object cache | On a return visit, the `.slobj` cache knows the texture ids before ObjectUpdates arrive. Coarse fetches could start at the handshake | Helps only on a return visit, not in a new region |
| Coarse textures for neighbour regions | Child regions already stream. Coarse textures for them would make a crossing cheaper | Region-crossing runs |
| Real impostors | The viewer's over-cap avatar is a rendered snapshot that includes the mesh body and attachments | After BUG-AVATAR-19, if the base-body stand-in looks too wrong |
| Stream bakes or fetch them in full | The viewer streams J2C bakes. It fetches in full only server bakes that are not J2C (`lltexturefetch.cpp:2553-2564`) | Which case SL's bake host is. The FEAT-PERF-29 spec decides |
| HTTP/2 to the asset host | Many streams over one connection instead of 8 connections | Only if Task Manager shows the line is *not* full |
| Grow the draw distance after arrival | Start near, then widen. Firestorm has a setting for this (not checked in its source) | Whether arrival is still bound by object count after tasks 1–3 |
| Batch material ids (variant A) | See decision 4 | The `[Arrival]` trace |
| Deduplicate display-name requests | 848 requests failed. Whether the same ids were asked again is unknown | BUG-NET-38 |
