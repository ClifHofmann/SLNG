# ADR 0005: Arriving in a crowded region: textures stream, avatars rank by size

- Status: proposed
- Date: 2026-10-31
- Updated: 2026-11-01 with the FEAT-PERF-28 baseline (`docs/perf/baseline-arrival-2026-10-31.md`), and
  again that evening with the results of tasks 1, 2, 3 and 5 ("Where it stands" below); on 2026-11-03 with
  the BoM-probe finding and a baseline without the probe
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
is faster than that, or the interval stretches under load. We do not know which. (The baseline
below settles it: the line is faster.)

### The baseline (FEAT-PERF-28, 2026-10-31)

Sirens (the login region), `v0.28.78-alpha`, 3 cold and 3 warm runs, RTX 4070 12 GB, with the
task-0 time base. The full numbers and caveats are in `docs/perf/baseline-arrival-2026-10-31.md`.

| Measure | Cold | Warm |
|---|---|---|
| Every full avatar has every bake (median) | 30.4 s | 29.8 s |
| No face waiting for a texture (median) | 134.0 s | 71.7 s |
| MB until no face waits (median) | 1666 | 23 |
| Worst `[TexWait.OtherBake]` p95 | ~46 s in 2 runs, 2.5 s in 1 | 0.4–0.6 s |
| Worst `[TexWait.Scenery]` p95 | 1.3–2.2 s | 0 s |
| Reduced avatars without a bake at +60 s | nearly all | nearly all |
| Network peak over 5 s | 225–234 Mbit/s | 64–99 Mbit/s |

What it changes:
- **The line carries about 230 Mbit/s, not 100.** A cold texture download runs at about 28 MB/s,
  which is that peak. So the line is full while the cold bytes come in, and the bytes (cause 4 below)
  still cost about 60 s of a cold arrival.
- **A second bottleneck sits after the network.** Warm runs download 8–48 MB and still take 65–101 s
  until no face waits. At +30 s, 2468–4922 faces were waiting with nothing in flight. That is the path
  from a cached asset to a bound texture: disk read, decode, main-thread queue, upload. This is an
  inference from the runs; nobody has measured the stages yet. It is roughly half of a cold arrival
  and all of a warm one.
- **Causes 1 and 2 are confirmed.** Nearly all reduced avatars had no bake at +30 s and +60 s. In the
  two cold runs with many reduced avatars, other avatars' bakes waited ~45 s p95, scenery ~2 s.
- **VRAM unloading did not show at Sirens** (0 in six runs). A crowded PEAK Lounge run the same day
  had 9. The budget still falls: from 9680 to 4556–4742 MB in the BUG-AVATAR-19 runs of 2026-11-01.

### Where it stands (2026-11-01, evening)

Sirens, 3 cold and 3 warm runs per set, with `tools/baseline-run.ps1`:
- `baseline/20261101-1636`, v0.28.82: tasks 1, 2 and 5 merged, plus FEAT-PERF-31 (below).
- `baseline/20261101-1745`, v0.28.83 with `--tex-stream`: task 3's build.

Medians, ranges in brackets. "Settled" is new (see the findings).

| Measure | Baseline (31.10.) | v0.28.82 | v0.28.83, `--tex-stream` |
|---|---|---|---|
| Every full avatar has every bake, cold | 30.4 s | 19.0 s | 15.6 s |
| Every full avatar has every bake, warm | 29.8 s | 24.2 s | 23.7 s |
| Settled, cold | 134.0 s | 135.8 s (116–136) | 106.5 s (96–116) |
| Settled, warm | 71.7 s | 10.9 s (9–11) | 10.1 s (7–16) |
| First moment no face waits (`noFaceWithoutTexture`), cold | 134.0 s | 36.4 s | 30.1 s |
| Texture MB over HTTP, cold, whole run | 1722–1736 | 1927–1995 | **749–841** |
| MB until settled, cold | 1666 | 1852 | 724 |
| Reduced avatars without a bake at +60 s | nearly all | 0–1 | 0–2 |
| Worst `Net` stage p50, cold | not measured | 30–42 s | 8–19 s |
| `parked outfit unloaded under VRAM pressure` | 0 | 0 | 0 |

What it says:
- **Cause 3 was the main-thread queue.** FEAT-PERF-30 timed every stage. Texture binds waited 23–52 s
  in `QueueWait`, behind ~13 ms `avatar.face_material` items in the shared lane, while the bind itself
  takes 1–5 ms. FEAT-PERF-31 (#176) gave them their own lane with a 4 ms budget: `QueueWait` p50 fell
  to 0.1–1.6 s, and a warm arrival settles in about 10 s instead of 65–101 s.
- **A cold arrival comes in two waves.** About 65–80 s after the handshake, roughly 2,000 new textures
  for 15,000–29,000 faces are requested and wait on the network until +115–136 s. The likely source is
  objects the sim sends late: cold, the world holds about 8,000 entities at +15 s and 30,000 at +60 s.
  That is not verified.
- **The `[Arrival]` milestone fires between the waves.** `noFaceWithoutTexture` is the *first* moment
  no face waits, so it flatters cold runs (36 s, with a gigabyte still to come). `tools/arrival_summary.py`
  now also prints **settled**: the first `[TexStage.Total]` report after the last one with more than 50
  faces waiting, and the MB until then. The 31.10. baseline had no stage reports; its 134 s is taken as
  settled, because its queue never emptied in between (2,468–4,922 faces waiting at +30 s).
- **Streaming cuts the cold bytes by about 58 %** and settles about 30 s earlier. A cold arrival is still
  bound by the network (`Net` holds everything after the first wave), so the cold target is not met yet.
- **Bakes:** cold is near the target, warm is not. Warm bakes take 24 s although almost everything else
  settles in 10 s. Not looked into.
- **The VRAM budget** still falls from 9680 to 3840–4272 MB, with and without streaming, and nothing was
  unloaded at Sirens.
- **Streaming is off by default** (`--tex-stream`, honoured only with `-Diag`). The maintainer's visual
  check with it on found nothing wrong. Another avatar showed only its name tag for a while and then
  appeared. After a restart it was there at once, with and without streaming, so nothing ties
  it to streaming.
- The network sampler of `baseline-run.ps1` read 1.0–1.4 Gbit/s peaks in these runs, against 225–273
  Mbit/s on 31.10. It probably counts another adapter; the peaks are not used here.

### Where it stands (2026-11-03)

Sirens, `baseline/20261103-0831`, v0.28.87, 3 cold and 3 warm runs, streaming on (the default since
v0.28.84), **BoM probe off** (the new default of `tools/baseline-run.ps1`).

**The probe finding (FEAT-PERF-32).** Under `--diag`, the Bakes-on-Mesh probes (`[BomBound]`,
`[BomAlpha]`) read the bound texture back from the GPU on the main thread, inside the
`avatar.face_material` queue item. In twelve runs (`20261101-1745`, `20261102-1244`) that was 12.6 ms
per `[BomBound]` line, 653–1,355 lines a run, and 98 % of all `avatar.face_material` time (7–19 s a
run; without the probe the item costs about 0.05 ms). Every earlier `-Diag` run carries it, so
**every earlier baseline overstates avatar work**, and with it the late second wave. A normal session
never pays it. FEAT-PERF-31 stays right (a bind should not queue behind anything), but the "~13 ms
items" it names were the probe.

**The A/B.** v0.28.86, 3 cold runs per arm (`docs/specs/FEAT-PERF-32-avatar-work-lane.md`):
- `20261102-2022`, probe off, avatars in the shared lane: `avatar.face_material` waited 2–3 s (p50);
  geometry half applied at +52–68 s.
- `20261102-2042`, probe on, avatars in a bounded lane of their own: it waited 10–17 s; half applied
  at +81–86 s.
- So the probe moved the waits, not the lane. The lane's own effect could not be told from the
  probe's, a model of the frame loop expected about 0, and a first version with a fixed share had
  starved the bakes (98 s, 143 s, never). The lane was dropped.

The honest baseline, medians with ranges:

| Measure | v0.28.83, probe on (11-01) | v0.28.87, probe off (11-03) |
|---|---|---|
| Every full avatar has every bake, cold | 15.6 s (11.7–16.9) | 23.7 s (17.6–23.9) |
| Every full avatar has every bake, warm | 23.7 s | 23.0 s (20.6–24.1) |
| Settled, cold | 106.5 s (96–116) | 59.9 s (30.5–65.4) |
| Settled, warm | 10.1 s (7–16) | 11.5 s (10.2–14.6) |
| MB until settled, cold | 724 | 551 (280–576) |
| Texture MB over HTTP, cold, whole run | 749–841 | 766–796 |
| Second wave (more than 1,500 faces in `Net`), cold | 2 of 3 runs, at +91 s | none |
| Worst `Net` stage p50, cold | 8–19 s | 0.2–2.3 s |
| Reduced avatars without a bake at +60 s | 0–2 | 0–1 |
| `[Perf]` fps / low1%, first 120 s | 20–25 / 10–12 | 18–21 / 7–9 |

What it means:
- **The second wave did not show without the probe**, and `Net` stayed short. Two caveats: in the
  evening arms of 11-02 one run in each still had a late wave (arm A's kalt1 had `Net` p50 at 35 s),
  and a calmer network that morning may have helped. One morning is not proof.
- **Both settle targets are met at Sirens:** cold 60 s against 67 s (all three runs 30–65 s), warm
  11.5 s against 36 s. Zero faces waiting is later, cold at +82–101 s: a tail of at most 50 faces.
- **Bakes are still open**, about 21–24 s against 15. Cold was 15.6 s on 11-01 with the probe on, so
  removing it did not help them, and the runs scatter widely (14.8–28.6 s and 14.2–46.6 s in the two
  arms of 11-02). Not looked into.
- **fps is lower than on 11-01** (18–21 against 20–25, low1% 7–9 against 10–12). The runs of 11-02 with
  the probe on were low too, so the probe is not the cause. Not checked: machine load, driver, a later change.
- **The VRAM target** still needs a crowded place.

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

Revised 2026-11-01 with the baseline. Cause 3 is new; the old causes 3–5 moved down one place.

| # | Cause | Evidence | What it explains |
|---|---|---|---|
| 1 | Over-cap avatars draw no bakes | Code, and the baseline: nearly all reduced avatars had no bake at +60 s | Most grey avatars. They stay grey until promoted |
| 2 | Other avatars' textures at priority 0 | Code, and the baseline: other bakes ~45 s p95 against ~2 s for scenery | Full avatars stay grey until the scenery backlog drains |
| 3 | The path from a cached asset to a bound texture | Baseline, inferred: warm arrivals take 65–101 s with almost nothing downloaded | Roughly half of a cold arrival and all of a warm one, until no face waits |
| 4 | Every texture at full resolution | Code, plus the bytes: 1.67 GB at Sirens, 5.6 GB at KinkyEvent | About 60 s of a cold arrival at Sirens, because the line is full while they come in |
| 5 | VRAM pressure | Measured at KinkyEvent and PEAK Lounge, not at Sirens. Why the budget falls is unknown | Reloads after arrival (Bronte), blur, re-decodes |
| 6 | The line | Baseline: about 230 Mbit/s, full during a cold download | Fixed. Cause 4 decides how much of it we need |

Causes 1 and 2 decide *who* waits; the baseline confirms both. Causes 3 and 4 decide *how long*
everyone waits: cause 4 for the bytes of a cold arrival, cause 3 for everything after the bytes,
cold or warm. Cause 5 undoes work that was already done. We cannot change the line (6), and it is
faster than assumed.

*Status, 2026-11-01 evening:* causes 1, 2 and 3 are fixed (#173, #172, and FEAT-PERF-31 in #176).
Cause 4 is built behind a setting (#178). What is left of a cold arrival is the bytes of its second
wave on the line (4 and 6); cause 5 is the next task.

Bronte fits causes 2 and 5. She was a full avatar whose head bake waited at priority 0. Then
`full->parked` at 42 m unloaded her outfit, and `reduced->full` at 4.5 m loaded it again. Her grey
head has no log line of its own. A missing GPU resolution is plausible but not proven.

### 2. Separate tasks, one PR each

Avatar priority and texture bytes are separate tasks, for three reasons:
- They touch different files.
- They carry different risk: one is a priority function, the other a pipeline change behind a setting.
- Each must be measurable on its own.

Tasks 0–3 and 5 are in `docs/ROADMAP.md`. BUG-PERF-25 and BUG-NET-38 are still proposals. The table
is in the order the tasks run; task 5 came with the baseline and ran before task 3's build.

| Task | Id and title | What | Size | Status (2026-11-01 evening) |
|---|---|---|---|---|
| 0 | FEAT-PERF-28 Arrival time base | Measurement only | S | Merged (#171); baseline in `docs/perf` |
| 1 | BUG-PERF-24 Avatar textures rank by size | Priority | S | Merged (#172) |
| 2 | BUG-AVATAR-19 Reduced avatars draw their bakes | Parity | S–M | Merged (#173) |
| 5 | FEAT-PERF-30 Where a face waits after the network | Measurement only | S–M | Merged (#176) |
| 5, fix | FEAT-PERF-31 Texture binds get their own main-thread lane | Found by task 5 | S | Merged (#176) |
| 3 | FEAT-PERF-29 Progressive texture streaming | Spike, then build | L | Spike merged (#177), build merged (#178), off by default |
| 5, fix 2 | FEAT-PERF-32 Queue wait per label; baselines without the BoM probe | Measurement; an avatar lane was tried and dropped | S | Built 2026-11-03 |
| 4 | BUG-PERF-25 The VRAM budget falls under load | Read the logged terms, then fix | M | Next; the terms are logged |
| Any time | BUG-NET-38 Display names asked before the cap | Small fix | S | Not started |

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

*Result:* done; `tools/baseline-run.ps1` samples the network itself, so the Task Manager check is
covered. The baseline found three limits of the measurement:
- "Texture inflight back to 0" is not usable. It is reached between waves, 8–33 s in, with more than
  a gigabyte still to come.
- "Every bake" does not see missing attachments. An avatar can count as complete and still be bare.
- 3 of 12 `[ArrivalCount]` lines were missing. The cause is unknown.

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

*Result:* merged (#172). With tasks 1 and 2 together (3 cold runs at Sirens, 2026-11-01), the worst
`[TexWait.OtherBake]` p95 was 4–10 s instead of ~46 s. Scenery's rose to ~10 s in two of the three
runs, because the slots are now shared and reduced avatars ask for their bakes too. Worth watching,
not a regression by itself.

**Task 2: BUG-AVATAR-19.** A reduced avatar draws the base body with its bakes, as
`RenderConfig.cs:21-22` already says. The jelly-doll colour stays until the first bake arrives, and
for the viewer's visually-muted cases. This amends ADR 0004 P3, which kept the jelly doll as the
fallback "for avatars never loaded".
- Rough cost: 56 avatars × about 5 base-body bakes. The spec measures it.
- Open point for the spec: a bake's alpha (from alpha layers) must not cut holes in a stand-in that
  has no mesh body over it.

*Result:* merged (#173). In the twelve runs of 2026-11-01, 0–2 reduced avatars had no bake at +60 s,
against nearly all in the baseline.

**Task 5: FEAT-PERF-30, where a face waits after the network.** Added with the baseline. It changes no
behaviour. For each texture a face asks for, it logs the time spent in each stage between the request
and the bound texture:
- GPU-cache hit or miss, and whether the block-compressed warm copy was used (`GpuCache.cs:1888-1908`
  on `main` at ac501dbe)
- disk-cache read, raw and decoded (FEAT-PERF-11)
- the wait for a decode slot (`AssetService.cs:964-968`, same commit), and the decode itself
- the wait in the main-thread work queue (label `texture.upload`), and the upload

It reports p50/p95 per stage, and how many faces wait in each stage at +30 s, warm and cold. It is
done when one warm run says which stage holds the 65–101 s. That result decides whether task 3's build
can afford its extra uploads, or whether that stage is fixed first.

*Result:* merged (#176), together with the fix it found. `QueueWait` held the time: texture binds sat
23–52 s behind avatar material items in the shared lane. FEAT-PERF-31 gives them a lane of their own,
served first, with a 4 ms budget (`RenderConfig.TextureUploadBudgetMs`). Warm, the block-compressed copy
was found for 4,985–5,269 of 7,885–8,496 tries.

**Task 3: FEAT-PERF-29, progressive streaming.** Two PRs. The spike can run now. The build waits for
task 5: streaming shortens the ~60 s of cold bytes, but it adds uploads to the stage the baseline
points at.
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

*Result:* both merged. The spike (#177, `docs/specs/FEAT-PERF-29-progressive-streaming.md`) found that
Phase 2 failed because CoreJ2K could not yet produce an image in the client (until 2026-08-02), and that
today the degraded branch was the blocker. The build (#178) asks first for the discard-5 size (18,840 B),
decodes through the reduce path, refines through `GpuCache`'s sharpen path, and keeps partial bytes as
`.part`. Bakes, sculpt maps and the own avatar's textures are fetched in full. Off by default; the A/B is
in "Where it stands". Open: alpha is classified on the coarse image and not again after refinement, and
the extra uploads' main-thread time is not measured on its own.

**Task 4: BUG-PERF-25.** Its own task. The fix waits for the terms that task 0 logs. Measure again
after task 3, because streaming changes how much texture memory there is.

*Status:* the terms are logged now (`[VramTerms]`). At Sirens the budget fell from 9680 to about
4.6 GB without unloading anything, and to 3.8–4.3 GB in the runs of 2026-11-01 evening, with and without
streaming. The unloading needs a crowded place (PEAK Lounge, KinkyEvent) to reproduce.

### 3. VRAM pressure is its own task, measurement first

Yes, as task 4. It is the most likely cause of Bronte's reload. The terms are logged now; the
analysis reads them before anything is changed.

### 4. FEAT-NET-07 step 1, variant A (batching material ids): not built now

The material path is not on the critical path:
- p50 is 0.2–0.6 s, with one request in flight.
- Batching saves about 0.4 s median per batch.
- LMV lets a request through even when its queue is full.

Build variant A only if, after tasks 1–5, the `[Arrival]` trace shows materials among the last
things to finish. Reading `RenderMaterialsCapability` and `MaxMaterialsPerTransaction` from
`Simulator.Features` would be part of it then.

This ADR does not judge steps 2–4 (own LLMesh decoder, inventory over AIS, the plan for the object
decoder). None of them showed up on arrival.

## What "significant" means

Measure with the task-0 time base and `tools/baseline-run.ps1`, 3 cold and 3 warm runs per variant.
The baseline is Sirens, not KinkyEvent. The crowd varies between runs (13 to 43 reduced avatars), so
compare only runs with a similar `[AvatarLimit]` count. The VRAM target needs a crowded place,
because Sirens unloaded nothing.

The targets are half the baseline (revised 2026-11-01 from the relative targets of the first version).
"No face waiting" now means **settled** (see "Where it stands"), not the first quiet moment.

| Metric | Baseline cold / warm (median) | Now: v0.28.87, no probe (2026-11-03) | Target cold / warm |
|---|---|---|---|
| Arrival → every full avatar has every bake (any resolution) | 30.4 s / 29.8 s | 23.7 s / 23.0 s | 15 s / 15 s |
| Arrival → settled (no more than 50 faces waiting from then on) | 134.0 s / 71.7 s | 59.9 s / 11.5 s | 67 s / 36 s |
| MB until settled | 1666 / 23 | 551 / 157 | 833 / none (already small) |
| Reduced avatars without any bake at +60 s | nearly all / nearly all | 0–1 / 0–1 | 0 / 0 |
| `parked outfit unloaded under VRAM pressure`, first 5 min, crowded place, 12 GB card | 9 (PEAK Lounge, one run) | not measured yet | 0 |

Met (2026-11-03, one morning): cold settled (60 s against 67 s) and warm settled, the cold MB, reduced
avatars with bakes (0–1). Open: bakes, cold and warm (24 s and 23 s against 15 s), and the VRAM target,
which needs a crowded place. fps is lower than on 11-01 (cause unchecked). The earlier "Now" column,
v0.28.83 with the probe on, is in "Where it stands" (2026-11-03).

"Bytes until texture inflight is back to 0" is dropped as a target (see task 0's result). "Every
bake" does not see missing attachments, so a bare avatar can still count as complete; that needs a
milestone of its own before it can be a target.

## Consequences

- **Streaming uploads a texture more than once:** coarse first, then refined. That adds
  `texture.upload` work on the main thread. `mainQueue` held 14–17k items at KinkyEvent (memory
  `mainthread-workqueue-is-the-bottleneck`), and the baseline points at the stages after the network
  as the larger bottleneck (task 5). So:
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
| A larger main-thread upload budget while a backlog exists | Partly done by FEAT-PERF-31 (own lane, 4 ms). Warm runs still show `QueueWait` p95 of 2.5–4.7 s in the first seconds, when everything arrives at once | Whether those first seconds matter once the rest is fast |
| Warm start from the compressed copy | Task 5 measured it: found for 4,985–5,269 of 7,885–8,496 warm tries. Why the rest misses is unknown | The misses by reason |
| ~~Find what the second cold wave is~~ Answered 2026-11-03 | Mostly the BoM probe: its main-thread time (98 % of `avatar.face_material` under `--diag`) held the scenery geometry back, and the textures are asked when the geometry is applied. No cold run of 11-03 showed a wave | Re-check if it returns: `wave2` in `tools/ab_compare.py` (more than 1,500 faces in `Net`) |
| Streaming on by default | The A/B and the visual check are good. Today it is honoured only with `-Diag` | A small PR: on by default, off by a setting; then a run in a crowded place |
| Why warm bakes take 24 s | Everything else settles in about 10 s warm | The bake fetches' stage times in a warm run |
| Fetch only the mesh LOD in use | The viewer fetches only the LOD it needs. Whether our mesh fetch does is unmeasured | `[MeshPipe]` bytes per mesh compared with the LOD in use |
| Pre-warm from the object cache | On a return visit, the `.slobj` cache knows the texture ids before ObjectUpdates arrive. Coarse fetches could start at the handshake | Helps only on a return visit, not in a new region |
| Coarse textures for neighbour regions | Child regions already stream. Coarse textures for them would make a crossing cheaper | Region-crossing runs |
| Real impostors | The viewer's over-cap avatar is a rendered snapshot that includes the mesh body and attachments | After BUG-AVATAR-19, if the base-body stand-in looks too wrong |
| Stream bakes or fetch them in full | The viewer streams J2C bakes. It fetches in full only server bakes that are not J2C (`lltexturefetch.cpp:2553-2564`). The FEAT-PERF-29 build fetches them in full; whether SL's bake host answers Range requests is unchecked | Whether bakes are among the last things to finish once streaming is on |
| HTTP/2 to the asset host | Many streams over one connection instead of 8 connections | Ruled out by the baseline: the line is full during a cold download, so more streams add no bandwidth |
| Grow the draw distance after arrival | Start near, then widen. Firestorm has a setting for this (not checked in its source) | Whether arrival is still bound by object count after tasks 1–5 |
| Batch material ids (variant A) | See decision 4 | The `[Arrival]` trace |
| Deduplicate display-name requests | 848 requests failed. Whether the same ids were asked again is unknown | BUG-NET-38 |
