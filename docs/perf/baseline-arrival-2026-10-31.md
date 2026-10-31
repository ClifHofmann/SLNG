# Arrival baseline, 2026-10-31 (FEAT-PERF-28)

The reference for ADR 0005 (`docs/adr/0005-region-arrival-texture-streaming.md`): how long a region takes to
appear, measured before anything is changed. Every later variant is compared with these numbers, not with the
ADR's earlier single run.

## Setup

- Build `v0.28.78-alpha`, commit `44fe3032` (branch `feature/FEAT-PERF-28-arrival-time-base`), `-Diag`.
- Place: **Sirens**, the login region. Not KinkyEvent, which the ADR names. The first full avatar count is 12 in every run.
- 3 cold runs (asset cache and object cache empty), then 3 warm runs (cache as the last cold run left it).
- Run by `tools/baseline-run.ps1`; logs summarised by `tools/arrival_summary.py`. Per run: log in, stay still, wait for
  `[Arrival]` (about 4 to 5 minutes), log out. Raw logs and `runs.csv` are in `baseline/20261031-1412/` (not committed, 15 to 30 MB each).
- RTX 4070 (12 GB), 100 Mbit/s line as sold.

## Results

Times are seconds from the region handshake; MB are texture bytes over HTTP from the first sample.

| Measure | kalt 1 | kalt 2 | kalt 3 | warm 1 | warm 2 | warm 3 |
|---|---|---|---|---|---|---|
| Every full avatar has every bake | 30.4 s | 22.7 s | 56.2 s | 44.0 s | 24.2 s | 29.8 s |
| ... MB until then | 272 | 217 | 408 | 24 | 6.5 | 15 |
| No face waiting for a texture | **133.0 s** | **143.4 s** | **134.0 s** | **100.8 s** | **71.7 s** | **65.2 s** |
| ... MB until then | 1666 | 1655 | 1689 | 48 | 8 | 23 |
| Texture inflight first back at 0 | 19.1 s | 28.8 s | 32.8 s | 13.0 s | 13.1 s | 8.3 s |
| Texture MB over HTTP, whole run | 1722 | 1695 | 1736 | 114 | 67 | 163 |
| Network peak, 1 s / 5 s (Mbit/s) | 250 / 225 | 269 / 234 | 240 / 225 | 197 / 99 | 273 / 68 | 226 / 64 |
| Reduced avatars at the last `[AvatarLimit]` line | 38 | 18 | 13 | 41 | 39 | 43 |
| Reduced avatars without any bake, +30 s | n/a (line missing) | 18 of 18 | n/a (line missing) | 33 of 37 | 37 of 39 | 38 of 40 |
| Reduced avatars without any bake, +60 s | 35 of 39 | n/a (line missing) | 8 of 9 | 33 of 38 | 37 of 39 | 37 of 40 |
| `parked outfit unloaded under VRAM pressure` | 0 | 0 | 0 | 0 | 0 | 0 |
| Worst `[TexWait.OtherBake]` p95 (one 5 s interval) | 46.7 s | 45.2 s | 2.5 s | 0.6 s | 0.4 s | 0.4 s |
| Worst `[TexWait.Scenery]` p95 | 2.2 s | 1.3 s | 1.7 s | 0.0 s | 0.0 s | 0.0 s |

Medians for the targets of ADR 0005 ("at most half of the baseline"):

| | cold | warm | target cold | target warm |
|---|---|---|---|---|
| Every full avatar has every bake | 30.4 s | 29.8 s | 15 s | 15 s |
| No face waiting for a texture | 134.0 s | 71.7 s | 67 s | 36 s |
| MB until no face waits | 1666 | 23 | 833 | n/a (already tiny) |
| Reduced avatars without a bake at +60 s | most of them | most of them | 0 | 0 |

## What the numbers say

- The line is not the limit: 225 to 234 Mbit/s over 5 s, far above what the ADR assumed.
- Warm runs download almost nothing and still take 65 to 101 s until no face waits; thousands of faces were waiting at +30 s with
  nothing in flight (2468 to 4922). That points at the main-thread queue and decode, not the network. Cold adds about 60 s on top
  (1.67 GB at about 28 MB/s). This is an inference from these runs, not a measurement of the queue itself.
- Reduced avatars are never textured (cause 1 of the ADR): in every run nearly all of them have no bake at +30 s and at +60 s.
- Other avatars' bakes queue behind scenery (cause 2): about 45 s p95 against about 2 s for scenery in the two cold runs with many
  reduced avatars; 2.5 s in the cold run with 9 reduced. The ADR's earlier 172 s (KinkyEvent) is not reproduced here.
- No outfit was unloaded under VRAM pressure in these six runs (Sirens). A crowded PEAK Lounge run earlier the same day had 9.

## Caveats

- **The crowd varies between runs** (reduced avatars 13 to 43). Compare later runs only against runs with a similar count and note the
  `[AvatarLimit]` line of each.
- **"Texture inflight back to 0" is not a usable target.** It is reached after 8 to 33 s although more than a gigabyte follows (the pipeline
  empties between waves). Use "no face waiting" and the bytes until then.
- **"No face waiting for a texture"** counts texture requests that missed the GPU cache and are unanswered, one per asking face. It is not a
  frustum test.
- **"Every bake"** means every wanted bake is applied to the avatar's body part. It does not see missing attachments: an avatar can be
  counted as complete and still be bare (observed in PEAK Lounge, see the Bronte investigation).
- **Three of the twelve `[ArrivalCount]` lines are missing** (+30 s in kalt1 and kalt3, +60 s in kalt2); cause unknown, still to be looked at.
- Network peaks sum every adapter that is up (loopback and tunnels left out), so a VPN could count twice.
- The place is Sirens. A KinkyEvent baseline, if wanted, is a separate set of runs.
