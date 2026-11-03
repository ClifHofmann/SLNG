# [FEAT-PERF-32] Queue wait per label; the --diag BoM probes are what made avatar work expensive (avatar lane tried and dropped)

- **Feature ID:** `FEAT-PERF-32`
- **Track:** `render` / `perf` (measurement)
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md), [ADR 0005](file:///E:/Git/SLNG/docs/adr/0005-region-arrival-texture-streaming.md)

## Overview

ADR 0005: at v0.28.84 a cold arrival at Sirens settles in about 107 s against a target of 67 s, held by a late second wave of scenery texture
requests. A texture is requested when the object's geometry is applied (`mesh.apply`, `primmesh.apply`, `sculpt.apply`), and every object
passes the main-thread work queue twice, in the same first-in first-out `Visual` lane as the avatars' items. `avatar.face_material` was 3-13 ms
an item against the scenery's 0.05-0.3 ms, so the plan was to give avatar work a lane of its own so that scenery geometry stops waiting behind it.

**Outcome:** the lane did not work and was dropped (below). What remains is what found out why: the per-label wait, the switch for the probes, and a
baseline script that leaves the probes out.

## What remains

1. **Queue wait per label (`--diag`).** Each queue item carries its enqueue time; `[WorkCost]` gets `waitP50Ms=` and `waitMaxMs=` of the reporting
   interval beside `n / totalMs / avgMs / maxMs`. cost = what running the items took, wait = what the person paid for the lane. Nothing is recorded
   without `--diag`. (`MainThreadWorkQueue`, `SampleWindow`, 16,384 samples a label; self test "work queue: wait from enqueue to run is measured per label".)
2. **`--no-bom-probe`** (`-NoBomProbe` in `tools/run-client.ps1`): under `--diag` the Bakes-on-Mesh probes `[BomBound]` / `[BomAlpha]` (BUG-RENDER-50 follow-up)
   and the bake alpha map capture they read are not run. Without the switch an ordinary `-Diag` session is unchanged; without `--diag` the probes never run.
3. **`tools/baseline-run.ps1` leaves the probes out by default** (passes `-NoBomProbe`); `-BomProbe` puts them back. `info.txt` records `bomProbe : off (default)` or
   `on (-BomProbe)`. Baselines whose `info.txt` has no `bomProbe` line, or says `on`, carry the probes.
4. **`tools/ab_compare.py`** (read-only): one row per run of `baseline/<folder>` (geom50/90, wave2, settled, fps, low1%, and the new waits) and the arm from `info.txt`.

## Finding: the probes are 98 % of `avatar.face_material` under `--diag`

The queue item is `SetSurfaceOverrideMaterial` plus, **under `--diag` only**, `LogBomAlpha`: for a worn rigged face with a Bakes-on-Mesh bake it calls `LogBomBound`
(`texture.GetImage()`: a synchronous read-back of the bound texture from the GPU on the main thread, `Decompress`/`Convert`, a pass over the face's UVs) and measures
the face against the bake (`BomAlphaProbe`; `ReadBackBakeAlpha`, a second read-back, as a last resort), once per (mesh, face, bake).

Over 12 runs (`20261101-1745` and `20261102-1244`, cold and warm; `[WorkCost]` in `<run>.perf.txt`, `[BomBound]` lines in `<run>.log`):

- 653-1,355 probe lines a run, and `avatar.face_material` took 6.7-18.9 s a run **regardless of its item count** (3,035 items: 17.1 s; 11,689 items: 6.7 s).
- Least squares: **12.6 ms per probe line + 0.045 ms per other item** (R² 0.55). 12.6 ms x the probe lines is 98 % of the `face_material` time of all 12 runs (77-122 % per run).
  Without the probe the same item is 0.2-0.5 s a run, not 14-19 s.
- Every baseline up to now was made with `-Diag`, so every baseline carries the probes. **Earlier `-Diag` measurements overstate avatar work** by about that much: the
  "avatar items hold the lane" of FEAT-PERF-31, the 14 s of avatar work in the second-wave analysis, and the settle times of ADR 0005 all include it. A normal session pays none of it.
- Not shown: the same arrival without `--diag` (the arrival timeline needs it). `--no-bom-probe` is the nearest thing.

## What was tried and why it was dropped

**Version 1, a fixed share (1/3) of the frame budget for a new avatar lane** (A/B `baseline/20261102-1244`, v0.28.85, against `baseline/20261101-1745`, v0.28.83):
cold settled 96/106/116 -> 41/76/76 s, but the bakes milestone 12-17 s -> 98 s, 143 s, never; avatar-lane wait p50 8-33 s, max 49-75 s; fps 20-25 -> 16-20, low1% 10-12 -> 7-8.
Causes: a share small enough to protect the scenery starves items that cost 12 ms each; a bake apply that waits past the texture cache's 10 s fresh grace (`GpuCache.FreshGraceMs`)
finds its texture evicted and is lost (BUG-PERF-21: the "never"); two lanes with a floor and a slice each ran longer frames than the one lane did.

**Version 2, one budget and one clock, the lane picked per item by age** (`WorkLanePicker`: an item older than a bound goes first, the oldest of those; until then the scenery;
bound 1 s; `--avatar-lane-wait=MS`). A model of the frame loop (60,000 scenery items, 1,050 probe items of 12.6 ms, 12 bakes) said: a bound of 1 s or less is the shared lane to the
millisecond, 2 s costs the bakes 0.4-1.0 s, 10 s about 7-9 s; the order cannot buy the scenery anything under load, only the cost of the items does (probe at 0.05 ms: scenery
90 % at 46.5 s instead of 61.5 s). A/B of v0.28.86, 3 cold runs each, seconds after the handshake:

| | arm A `20261102-2022`: no probes, shared lane | arm C `20261102-2042`: probes, 1 s lane | baseline `20261101-1745` (v0.28.83) |
|---|---|---|---|
| `face_material` wait p50 | 3.1 / 2.2 / 2.3 s | 10.5 / 17.2 / 15.0 s | - |
| `face_material` wait max | 25.4 / 18.3 / 17.3 s | 22.3 / 38.8 / 30.2 s | - |
| geom50 | 68 / 52 / 62 | 81 / 85 / 86 | 67 / 79 / 78 |
| settled | 131 / 85 / 20 (median 85) | 115 / 136 / 115 (median 115) | 96 / 106 / 116 |
| bakes | 24.9 / 14.8 / 28.6 s | 14.2 / 15.2 / 46.6 s | 15.6 / 16.9 / 11.7 s |
| fps / low1% | 16, 19, 18 / 7, 9, 8 | 18, 22, 20 / 7, 10, 8 | 24, 25, 20 / 12, 11, 10 |

Removing the probe is what moves the avatar waits (10-17 s -> 2-3 s p50) and the geometry (geom50 81-86 s -> 52-68 s). Bakes and fps show no clear difference between the
arms; the cold runs scatter widely (arm A kalt1 had `Net` p50 at 35 s) and the machine was loaded (items this change does not touch cost 2-10x more in the 1244 runs).
The arms differ in two things at once (probe and lane), and the lane's own effect cannot be told from the probe's; the model expects about 0. So the lane was **dropped**:
`Lane.Avatar`, `WorkLanePicker`, `BumpQueue.TryPeek`, the simulation, `RenderConfig.AvatarLaneMaxWaitMs`, `--avatar-lane-wait` and the 22 call-site changes are not in the
change (never committed); the model's and the A/B's numbers above are the record.

## Not covered / proposed

- **Proposed, not built:** make the probes opt-in (`--bom-probe`) in the client too, so that a plain `--diag` session stops carrying them. That changes what `--diag` shows
  (the `[BomBound]` / `[BomAlpha]` lines); the maintainer's call.
- **Proposed:** re-take the ADR 0005 baselines with `tools/baseline-run.ps1`'s new default (no probes) before judging any further arrival work; the earlier ones include ~14-19 s of
  probe time on the main thread.
- A run without `--diag` is not measured anywhere.
- Other `--diag`-only main-thread work (`diag.batch`, `[WornFaces]`) is not switched.
- Attachment prims of other avatars that `ObjectRenderer` builds stay in the `Visual` lane; the queue cannot tell them from scenery. Still true, now irrelevant.

## Acceptance Criteria

- [x] `[WorkCost]` shows `waitP50Ms` / `waitMaxMs` per label under `--diag`, none without it (self test).
- [x] `--no-bom-probe` leaves out `[BomBound]` / `[BomAlpha]`; `baseline-run.ps1` passes it by default and records it; `-BomProbe` turns it back on.
- [x] The finding and both A/B results are written down (above).
- [ ] The next baseline (no probes by default) is taken by the maintainer.

## Technical Specs & Affected Files

- `app/scripts/MainThreadWorkQueue.cs`: enqueue time, wait per label, `TakeWaits` seam. `Pump` is as on `main`.
- `app/scripts/Diagnostics.cs`: `--no-bom-probe`; `app/scripts/AvatarRenderer.BomAlpha.cs`, `app/scripts/AvatarRenderer.cs`: the three places that honour it
- `app/scripts/SelfTest.WorkWait.cs` and one line in `app/scripts/SelfTest.cs`
- `tools/run-client.ps1` (`-NoBomProbe`), `tools/baseline-run.ps1` (`-BomProbe`, `info.txt`), `tools/ab_compare.py` (new)
- `app/scripts/Boot.cs`: `AppVersion` v0.28.87-alpha

## Tests

Self test "work queue: wait from enqueue to run is measured per label" (the real `MainThreadWorkQueue`: 3 items waited p50 41 ms, nothing filed without `--diag`).
`tools/ab_compare.py` was checked against the numbers of the earlier analysis (settled 96/106/116 for 1745) and, for the wait columns, on a made-up `[WorkCost]` line.
