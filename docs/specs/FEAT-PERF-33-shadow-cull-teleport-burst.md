# [FEAT-PERF-33] Shadow caster culling & teleport work-queue burst

- **Feature ID:** `FEAT-PERF-33`
- **Track:** `render` / `perf`
- **Status:** 🧪 Review
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md), [ADR 0004](../adr/0004-performance-architecture.md)

## Overview & Goal

Performance telemetry from dense sims (e.g. Maui with ~66k entities and 210k nodes, and region returns) identified two critical bottlenecks:
1. **Excessive Shadow Draw Calls:** On Maui, 5,300 to 8,600 draw calls (>50% of all draws) were spent on directional shadow depth passes.
   - Root Cause 1: `SmallObjectCull.LodScale(lodFactor)` exploded with user preferences like `VolumeLodFactor = 8.0` (factor 8 -> LodScale 36.0x), pushing `ShadowEndDistance` past 1600m and making `ShadowEnd < scd` fail. Small objects never stopped casting shadows (`shadowCut = 0`).
   - Root Cause 2: In `ObjectRenderer.cs`, when a small object (<0.5m radius) was created beyond distance-cull range, `state.ShadowSettingBeforeDistanceCull` was initialized to `On`. When the camera approached, line 1087 restored `CastShadow` to `state.ShadowSettingBeforeDistanceCull` (`On`), overriding `SmallObjectShadows = false`.
   - Root Cause 3: `SmallObjectCull.ShadowEndDistance` scaled with `VolumeLodFactor`. Directional shadow cascades are low-resolution soft-filtered depth maps and should not scale with mesh polycount detail.
2. **Teleport Work-Queue Grinding:** After region changes or teleports, up to 50,000 visual node instantiations queued into `MainThreadWorkQueue.Lane.Visual`. Because the adaptive budget was hard-capped at 9.0 ms (`MainThreadWorkBudgetMaxMs`), draining 50,000 items took 30–40 seconds at 15–30 FPS while the user stared at a half-loaded sim.

**Goal:**
1. Fix shadow culling so small objects (<0.5m) and distant small casters do not cast directional shadows, cutting thousands of shadow draw calls on dense sims.
2. Introduce a temporary `TeleportBurst` budget (up to 28.0 ms) during teleports and initial login arrival, draining the visual queue in 5–10 s instead of 30–40 s, without arming on region crossings.

## Acceptance Criteria

- [x] `SmallObjectCull.ShadowEndDistance` computes shadow culling from physical screen size without blowing up under high `VolumeLodFactor` preferences.
- [x] `RenderConfig.ShadowCasterDistance` dynamically tracks `GraphicsSettings.ShadowDistance` (`Math.Clamp(ShadowDistance * 0.7f, 30f, 60f)`).
- [x] `RenderConfig.ArmTeleportBurst` provides a temporary high-throughput work budget (`MainThreadWorkBudgetBurstMs = 28.0 ms`) during teleport and initial login arrival (scoped away from region border crossings).
- [x] Full verify pass (`dotnet test` green, `--selftest` checks green, `dotnet format` clean).

## Technical Specs & Affected Files

- `src/SLNG.Core/SmallObjectCull.cs`:
  - Decouple `ShadowEndDistance` from `lodFactor`.
  - Clamp `f` in `LodScale` to `[0.1f, 4f]` to prevent `36.0x` explosion at LOD factor 8.
- `tests/SLNG.Core.Tests/SmallObjectCullTests.cs`:
  - Unit tests for `ShadowEnd_DoesNotExplodeWithHighLodFactor` and `LodScale_CapsAtExtremeFactors_PreventingDivisionByZero`.
- `app/scripts/RenderConfig.cs`:
  - Add `MainThreadWorkBudgetBurstMs = 28.0`, `ArmTeleportBurst(float seconds = 8f)`, `DisarmTeleportBurst()`, `IsTeleportBurstActive`.
  - Adapt `MainThreadWorkBudgetFor` to burst and extreme queue depth.
- `app/scripts/MainThreadWorkQueue.cs`:
  - Allow `TextureUploadBudgetMs` to expand (2x) during active teleport burst.
- `app/scripts/ObjectRenderer.cs`:
  - In `AssignSharedMesh`, keep `ShadowSettingBeforeDistanceCull` aligned with `desiredShadow`.
  - In sweep, enforce `smallCasterDisabled` when `!SmallObjectShadows && EffectiveBoundingRadius < 0.5f`.
- `app/scripts/UI/GraphicsSettings.cs`:
  - In `Apply`, scale `RenderConfig.ShadowCasterDistance` with `ShadowDistance`.
- `app/scripts/Boot.cs`:
  - Wire `RenderConfig.ArmTeleportBurst` on `TeleportStage.Started`, `TeleportStage.Finished`, and `ApplyRegionOrigin`.
  - Bump `AppVersion` to `v0.29.5-alpha`.
- `app/scripts/SelfTest.cs`:
  - Add `CheckTeleportBurstBudget()`.
