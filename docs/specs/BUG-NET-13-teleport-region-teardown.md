# [BUG-NET-13] Teleport / sim-hop: destination region comes up nearly empty + renderer RID leak

- **Feature ID:** `BUG-NET-13`
- **Track:** `net` / `render`
- **Status:** `🚧 In Progress` — fixes applied, not yet re-verified in-world
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **Branch:** `feature/BUG-NET-13-teleport-region-teardown`
- **Depends on:** `BUG-NET-04` (the eager teleport cleanup this bug is a regression of)

## Overview & Goal

Live 2026-09-07 (Agni), found while re-testing `BUG-NET-04`:

- After a teleport the **destination** region renders only a handful of scattered
  objects — most of the scene is missing.
- Sim-hopping leaks renderer resources; at exit:
  `ERROR: 34 RID allocations of type 'N10RendererRD11MeshStorage4MeshE' were leaked`,
  `ERROR: 34 RID allocations of type 'N17RendererSceneCull8InstanceE' were leaked`,
  repeated `WARNING: Leaked instance dependency: Bug - did not call instance_notify_deleted
  when freeing`.
- Teleport-heavy log `godot2026-09-07T19.02.09.log` (v0.20.122-alpha): **9212**
  `WARNING: Vector3 cannot be normalized, the elements must be finite` warnings, plus
  `[MainThreadWork] item threw: Cannot access a disposed object. Object name:
  'Godot.ImageTexture'`.

## Evidence (from `godot2026-09-07T19.02.09.log`)

| Line | Content |
|---|---|
| 4 | `[Boot] v0.20.122-alpha` — the morph/skin NaN guards (`99c89a3`, `6d9dd85`, v0.20.108) are present |
| 10–15 | six `[AvatarBodyMeshService] Loaded …` lines — **no** `Vector3 cannot be normalized` after them (the `BUG-RENDER-04` fix holds for the load path) |
| 28, 30 | `[MainThreadWork] item threw: Cannot access a disposed object … 'Godot.ImageTexture'` — already before the first teleport, from an earlier region change |
| 35–37 | `[Teleport] … left Millenium (741070837455616) +220,-111 region-steps away -- removing eagerly` |
| 38–39 | `[SelfBake] channels (null) -- no AvatarAppearance has ever been applied to this avatar` **immediately after the teleport** |
| 43 → EOF | essentially every remaining line is `Vector3 cannot be normalized` / `at: normalize (./core/math/vector3.h:551)` — 9212 copies, never stops |

The flood starts on the **first line after** `removing eagerly` and continues to the end
of the log (process was force-killed — the log never reaches a clean exit, which is why
the RID-leak lines aren't in this particular file; they print at exit to whichever log is
current).

`[SelfBake] channels (null)` recurring after *every* teleport is the tell: the self
avatar's `AvatarComponent` (with its `BakedTextures`) is being destroyed and re-created
by the teleport, not just re-keyed.

## Root cause

Two independent defects in the `BUG-NET-04` eager-teleport path, both in
`GridSession.OnSimChanged`:

### 1. The origin sim's circuit is left connected

`OnSimChanged` raised a **synthetic** `RegionDisconnectedReceived` for the old region.
That unloads the world entities/terrain (`WorldSimulation` → `World.RemoveRegion`) but
does **not** touch LibreMetaverse's connection to that simulator. With
`Settings.Agent.MultipleSims = true` nothing gates `ObjectUpdate` / `AvatarUpdate` /
`TerseObjectUpdate` on `CurrentSim` (see memory `bug-net-03-render-stack-already-multiregion`),
so the origin sim kept streaming updates carrying `e.Simulator.Handle == oldHandle`. Those
handlers happily **re-created** entities in the region we had just removed:

- endless create → (next `DisableSimulator` or nothing) → teardown churn →
  `Mesh` / `Instance` RIDs allocated and then orphaned instead of freed through Godot
  (the leak);
- `await`-continuations for the old region's texture decodes landing after the render
  side disposed the `ImageTexture` (`[MainThreadWork] item threw: Cannot access a disposed
  object`);
- half-populated transforms (a bare terse update with no full state behind it) feeding
  the renderer — the most likely `Vector3 cannot be normalized` source.

For a distant, non-adjacent teleport the origin sim's own `DisableSimulator` is not
guaranteed to arrive at all (this is the entire reason `BUG-NET-04` exists), so the stale
stream can run for the rest of the session — matching "the flood never stops".

**Fix:** `OnSimChanged` now calls `NetworkManager.DisconnectSim(oldSim,
sendCloseCircuit: true)`. That sends `CloseCircuit` to the origin sim, removes it from
LibreMetaverse's `Simulators` list (so its packets stop being dispatched), and fires
`SimDisconnected`, which `OnSimDisconnected` already turns into the same
`RegionDisconnectedReceived` → `RemoveRegion` cleanup. This is what a real viewer does on
a long teleport instead of waiting for a server packet. Wrapped in try/catch; on failure
it falls back to the old synthetic invoke so teleport cleanup can never throw on the
network thread.

### 2. The eager `RemoveRegion` destroyed the self-agent entity

`World.RemoveRegion` removed **every** entity keyed to the old region handle — including
the local agent, which at `SimChanged` time is still keyed to the old region because the
destination sim's first local `AvatarUpdate` has not been processed yet. The local agent
is the player, not regional content; destroying it here blanks the self avatar
(`[SelfBake] (null)`), drops its skeleton/appearance, and leaves a window where
`AvatarController` / `AvatarRenderer` have no self visual to follow or rebuild from. The
sim does not reliably re-send the self `AvatarAppearance` after a teleport
(`BUG-AVATAR-04`), so the blank can persist.

`WorldSimulation.ApplyAvatarUpdate` **already** removes the stale old-region local-agent
entity when the new one arrives (its `e.IsLocalAgent` block) — so preserving it across
`RemoveRegion` just bridges the gap until that update, and is cleaned up correctly
afterwards.

**Fix:** `World.RemoveRegion` skips any entity whose `AvatarComponent.IsLocalAgent` is
true. (A genuine `DisableSimulator` for a neighbor region during walking never contains
the local agent — you are in your current region, not the one being dropped — so this is
a no-op for the `BUG-NET-03` path.)

### 3. Defensive: non-finite avatar transform reaching the renderer

`WorldSimulation.ExtrapolateMovement` slerps `Rotation` toward `TargetRotation` and eases
`Position` toward `TargetPosition` every frame for every avatar. A zero quaternion
`(0,0,0,0)` target (the `default(Quaternion)`, distinct from `Quaternion.Identity`) or a
NaN position from a degenerate update makes `Quaternion.Slerp` / the ease produce a
non-finite result that then reaches the renderer as a NaN basis — one candidate feeder of
the `vector3.h:551` warning that survives fixes 1–2 if any path still produces it.

**Fix:** `ExtrapolateMovement` sanitises each avatar's `Position` / `TargetPosition` /
`Rotation` / `TargetRotation` before use — a non-finite value is reset to the last finite
value (or `Quaternion.Identity` / `Vector3.Zero`) and logged once per entity as
`[NaNGuard] entity=<id> region=<handle> localAgent=<bool> field=<name>`. This both stops
the NaN reaching the renderer and **names the culprit** for the next in-world test, in the
project's measure-don't-guess style.

## Acceptance Criteria

- [ ] After a distant teleport, the origin sim's circuit is closed (`[Teleport] … closing
      the stale circuit`), no further `ObjectUpdate` churn from the old region.
- [ ] The destination region streams in normally (no "nearly empty" scene).
- [ ] No `Vector3 cannot be normalized` flood after a teleport (or, if one remains, a
      `[NaNGuard]` line identifying it).
- [ ] The self avatar is not blanked by a teleport — no `[SelfBake] channels (null)`
      caused purely by the region unload.
- [ ] No `Mesh` / `Instance` RID-leak or `Leaked instance dependency` lines at exit after
      a sim-hopping session.
- [ ] No `[MainThreadWork] item threw: Cannot access a disposed object` burst after a
      teleport.
- [x] Unit tests: `RemoveRegion` preserves the local agent while removing everything else
      + terrain; the transform sanitiser repairs and logs a non-finite avatar transform.

## Technical Specs & Affected Files

| File | Change |
|---|---|
| `src/SLNG.Net/GridSession.cs` | `OnSimChanged` → `DisconnectSim(oldSim, sendCloseCircuit: true)` (try/catch, fall back to the synthetic invoke) |
| `src/SLNG.Core/ECS/World.cs` | `RemoveRegion` skips `AvatarComponent.IsLocalAgent` entities; `using SLNG.Core.Components;` |
| `src/SLNG.Core/WorldSimulation.cs` | `ExtrapolateMovement` non-finite transform sanitiser + `[NaNGuard]` dedupe log |
| `tests/SLNG.Core.Tests/…` | `RemoveRegion` local-agent preservation; sanitiser repair/log |
| `app/scripts/Boot.cs` | `AppVersion` bump |

## Round 2 (v0.21.1 → v0.21.2) — the flood is gone, the region still comes up sparse

`godot.log` at v0.21.1-alpha, two teleports (Millenium → Secret Love → Millenium), both via
the new path (`[Teleport] left … closing the stale circuit` + `[Neighbor] disconnected …`):

- **No `Vector3 cannot be normalized`, no `[NaNGuard]`, no RID-leak, no
  `Cannot access a disposed object` — anywhere.** Fixes 1–3 hold.
- But the **teleport back into a region we previously tore down comes up nearly empty**
  (screenshot: water + a handful of distant objects, no terrain). Reported as
  *"beim Rücksprung … fehlt viel z.B. der sim ground"*.

### Root cause of the sparse re-entry

`AvatarController._Process` sends an `AgentUpdate` (`SetMovement`) at 10 Hz with a camera
centre (`camSimPos`) and interest radius (`camFar`) computed from
`localAgent.RegionHandle`. Right after a teleport that handle is still the region we
**left** — `WorldSimulation.ApplyAvatarUpdate` only re-keys the local-agent entity when the
new sim's first local `AvatarUpdate` is processed. Meanwhile `RenderConfig`'s floating
origin has already recentred on the **new** region (`ApplyRegionOrigin` on `RegionConnected`),
so `RenderConfig.FromGodot(oldHandle, cameraGodotPos)` returns an SL position tens of
thousands of metres outside the new region. The sim's interest manager centres on that
point → it streams nothing back until a good `AgentUpdate` arrives.

Before fix 2 this was masked: `RemoveRegion` deleted the local agent, so
`AvatarController` had `localAgent == null` and skipped `SetMovement` entirely during the
gap. Fix 2 keeps the agent (correct — it must not be blanked), which exposed the stale-frame
send.

**Fix (v0.21.2):** `AvatarController._Process` skips the `SetMovement` send while
`localAgent.RegionHandle != _session.CurrentRegionHandle` (and the current region is
known). The sim sends us our own `AvatarUpdate` regardless of camera, so the gap self-clears
within a packet or two, after which the send resumes with correct coordinates.

**Diagnostics added** so the next test is conclusive rather than reasoned:
`[RegionEnter] <name> (<handle>) is now the current region` (GridSession) paired with
`[RegionData] first terrain patch for region <handle>` / `[RegionData] first object update
for region <handle>` (WorldSimulation). `[RegionEnter] X` with no following `[RegionData] …
for X` = the sim is not streaming (interest list / camera), not a render bug.

## Round 3 (v0.21.2 → v0.21.3) — sparse re-entry fixed; NaN flood is NOT an avatar transform

v0.21.2 log (`godot.log`, 23k lines, one teleport Millenium → Secret Love):

- **Sparse re-entry: fixed.** `[RegionData] first object update for region <B>` appears right
  after the teleport, the scene populates — user: *"Das ging jetzt."*
- **NaN flood: back, and `[NaNGuard]` never fired** (0 lines in 23k). So the non-finite
  vector is **not** in any avatar `TransformComponent` — `SanitizeAvatarTransform` would
  have caught and named it. The v0.21.1 "no flood" was a false negative: that log was only
  98 lines, the session ended seconds after the teleport.
- The flood is **~1 warning per frame**, continuous from the first post-teleport frame to
  the end of the session — a single per-frame call site with a permanently-latched bad
  input.

### Leading hypothesis for the flood

`AvatarController.PublishViewSpaceSunDirection` runs once per frame, unconditionally, and
ends in `viewDir.Normalized()` where `viewDir = GlobalTransform.Basis.Inverse() *
dir.Normalized()`. `dir` (the sun direction) is already length-guarded, so the NaN must be
the **camera's own Basis**: if the orbit / transition camera state (`_yaw`, `_pitch`,
`_orbitYaw`, `_orbitPitch`, `_orbitTarget`) latches a non-finite value, it flows into
`Rotation`/`Position`, the Basis stops being invertible, and `Inverse()` yields NaN — every
frame, forever. A transition or orbit value captured from a camera position computed
against a stale region handle / pre-recenter floating origin right after a teleport is the
most likely way it latches.

### Fixes (v0.21.3)

- `AvatarController.SanitizeCameraState()` at the top of `_Process`: any non-finite `_yaw` /
  `_pitch` / `_orbitYaw` / `_orbitPitch` / `_orbitTarget` is reset to a sane default,
  `_transitioning` cleared, and the bad values logged **once** as
  `[NaNGuard] camera-state repaired: …`.
- `PublishViewSpaceSunDirection` now checks the camera Basis is finite before inverting it;
  if not, it logs `[NaNGuard] sun-view-dir: camera Basis is non-finite` once and skips the
  frame. It also drops a non-finite `viewDir` silently.
- The camera `Position` write in `_Process` is guarded: a non-finite `targetPos + Basis.Z *
  zoom` is not applied.

These stop the flood and repair the state regardless of which input latched it; the
`[NaNGuard] camera-state repaired: …` line names the exact fields that were bad so the
root can be traced if it recurs.

### Round 3 (v0.21.3) — camera hypothesis was WRONG, reverted

Added `SanitizeCameraState()` + a camera-Basis guard in `PublishViewSpaceSunDirection` on
the theory that the orbit/transition state had latched a NaN. v0.21.3 log: **`[NaNGuard]`
still 0** — the camera state is finite, the camera Basis is finite. The camera is not the
source. Worse, the round-3 `Position` write-guard (`if (newPosition.IsFinite())`) turned
"NaN camera → sim ignores it → region streams" into "finite-but-stale camera → sim's
interest manager centres far away → region stays empty", regressing the v0.21.2 win
("sim wieder nicht sichtbar nach dem Rücksprung"). **All of round 3 reverted.**

### Round 4 (v0.21.3 → v0.21.4) — stop resetting the self avatar on teleport

The flood starts on the exact frame `[SelfBake] channels (null)` is logged after every
teleport — the self avatar is being **rebuilt from a reset `AvatarComponent`**.
`ApplyAvatarUpdate`'s local-agent block removed the old-region self entity and
`GetOrCreateEntity`'d a fresh one with `VisualParams == null` / `BakedTextures == null`, so
`AvatarRenderer` rebuilds the skeleton from the **default shape**. That path can leave a
non-finite bone Rest in the `Skeleton3D`, which the engine then re-normalizes every frame —
the flood — and `[NaNGuard]` (which only checks the ECS `TransformComponent`) can't see a
bad `Skeleton3D` bone.

- `WorldSimulation.ApplyAvatarUpdate`: when a teleport re-keys the self entity, **carry the
  old `AvatarComponent` forward** onto the new-region entity instead of dropping it. The
  identity/name/scale fields still update from the fresh event; VisualParams, BakedTextures,
  hover and active anims are preserved. Fixes the blank-avatar-after-teleport
  (`[SelfBake] (null)`) too.
- `AvatarRenderer.ApplyShape`: last-net guard before `SetBoneRest` — a non-finite `rest`
  is replaced with the bone's base rest and logged once as
  `[NaNGuard] bone-rest non-finite for '<bone>' … slPos=… slScale=… rot=…`. Names the
  source if round 4 doesn't fully fix it.

## Still open / next in-world test

- **Confirm v0.21.4 stops the flood** and that the return region is visible again. If
  `[NaNGuard] bone-rest …` appears, its `slPos`/`slScale`/`rot` values point at the bad
  shape input.
- **RID leak** — still needs confirming from a clean exit log of a sim-hopping session.
- **`[RegionData] first terrain patch`** never logs (only "first object update") — cosmetic:
  `OnSimConnected` raises `TerrainSettings` first and that creates the terrain entry, so the
  first *patch* isn't "first" any more. Not load-bearing.
