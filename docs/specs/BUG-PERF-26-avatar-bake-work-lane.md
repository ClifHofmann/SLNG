# [BUG-PERF-26] Avatar bakes wait up to 14 s in the Visual work queue on warm arrival

- **Feature ID:** `BUG-PERF-26`
- **Track:** `render` / `perf`
- **Status:** ✅ Done
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md), [ADR 0005](../adr/0005-region-arrival-texture-streaming.md)

## Overview & Goal

On warm arrivals at busy regions, all bake textures are fetched and GPU-uploaded within ~20 s, but the `[Arrival]` milestone "every full avatar has every bake" takes ~21-24 s from handshake (t ≈ 32.9 s, ADR target: 15 s).

Log analysis of baseline `baseline/20261103-0831` (warm 1–3) revealed that:
1. Network fetch and decode of bakes completes around t = 19.5–20.1 s.
2. GPU upload runs in `MainThreadWorkQueue.Lane.Upload` with < 25 ms queue wait.
3. However, applying the bake to the avatar's materials (`avatar.bake`, ~0.04 ms work cost) was enqueued to `MainThreadWorkQueue.Lane.Visual`.
4. At arrival, `Lane.Visual` is flooded with > 35,000 scenery items (`visual.create`, `visual.update`, `visual.reload`), draining under a 9 ms budget.
5. Consequently, `avatar.bake` items waited 10.2 to 14.3 s (p50: 14,173.9 ms) in `Lane.Visual` before executing.

**Goal:** Route `avatar.bake` work items to the head of `MainThreadWorkQueue.Lane.Visual` (with `priority: 100f`), removing the 14 s queue wait and bringing the warm bakes milestone down to ~9–10 s without sharing `Lane.Upload` (which must stay dedicated to `texture.upload` so texture binds are never delayed).

## Acceptance Criteria

- [x] `avatar.bake` work items are routed to the head of `MainThreadWorkQueue.Lane.Visual` (`priority: 100f`) while keeping `Lane.Upload` dedicated to `texture.upload`.
- [x] Warm arrival bakes milestone ("every full avatar has every bake") settles clearly below 21–24 s (target ≤ 15 s; verified in `baseline/20261103-1812`: warm1 +9.3s, warm2 +16.5s, warm3 +8.7s).
- [x] Cold bakes, scene settle time, and FPS/low 1% frame rate do not regress (fps 18–21, low1% 7–10).
- [x] Full `/slng-verify` passes (builds, unit tests, formatting, headless selftest).

## Technical Specs & Affected Files

- `app/scripts/MainThreadWorkQueue.cs`:
  Add `priority` parameter to `Enqueue` forwarding to `_lanes[(int)lane].Enqueue(item, subject, priority)`.
- `app/scripts/AvatarRenderer.FocusPriority.cs`:
  Forward `priority` in `EnqueueFor`.
- `app/scripts/AvatarRenderer.cs`:
  In `LoadAndApplyTextureAsync`, enqueue `avatar.bake` to `Lane.Visual` with `priority: 100f`.
- `tests/SLNG.Core.Tests/BumpQueueTests.cs`:
  Unit test `Priority_LateArrivingItemJumpsAheadOfOlderItems`.
- `app/scripts/SelfTest.FocusQueue.cs` & `app/scripts/SelfTest.cs`:
  Self-test `CheckWorkQueuePriority`.
- `app/scripts/Boot.cs`:
  `AppVersion` v0.29.1-alpha.
- `docs/ROADMAP.md`:
  Register `BUG-PERF-26`.

## Sub-tasks / Progress

- [x] Phase 1: Root cause analysis and log profiling.
- [x] Phase 2: Implementation of priority routing in `MainThreadWorkQueue` and `AvatarRenderer.cs`.
- [x] Unit test verification.
- [x] AppVersion bump (v0.29.1-alpha).
- [x] slng-verify checklist.

