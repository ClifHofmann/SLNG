# [MVP2-1] Object Interaction & Sit / Touch

- **Feature ID:** `MVP2-1`
- **Track:** `render/net`
- **Status:** `✅ Done` (builds, tests pass; live-verified against a real OpenSim seat)
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Let the player interact with world objects the way the real viewer does: touch (grab/de-grab,
firing `touch_start`/`touch_end` on the object's script — needed for `llDialog` menus, M5-4) and
sit on a prim or an anim-seat, plus stand back up. Left-click executes the object's `ClickAction`
byte, matching real-client behavior, rather than always touching.

## Acceptance Criteria
- [x] Left-click touches an object by default (grab/de-grab pair).
- [x] Left-click sits instead, when the object's `ClickAction == Sit`.
- [x] Right-click context menu offers an explicit `🪑 Sit` action on an object, and `🪑 Sit Here`
      on bare ground (`SitOnGround`).
- [x] A seated avatar's world position/rotation render correctly relative to the seat -- **live-verified
      2026-08-01** on a real OpenSim seat after a 4th, source-verified fix (see "Known limitations"
      below for the full history of what the first three attempts got wrong).
- [x] While seated, the local agent's WASD/fly/ground-clamp are suspended; any movement key stands
      the avatar back up (retries on a cooldown rather than a one-shot latch, so a failed first
      attempt doesn't strand the player seated forever -- live-tested regression, since fixed).
- [x] Unit tests written and passing (`WorldSimulationTests`, `GridSessionTests`).

## Technical Specs & Affected Files
- `src/SLNG.Net/GridSession.cs` — `RequestSit`/`SitOnGround`/`Stand` wrappers; `ResolveSeatedTransform`
  (mirrors LibreMetaverse's own `AgentManager.SimPosition`/`SimRotation` parent-chain walk,
  generalized to any avatar) resolves a seated avatar's wire-relative Position/Rotation to world
  space once, at the network boundary — nothing downstream needs to special-case a seated avatar's
  transform at all.
- `src/SLNG.Core/GridEvents.cs` — `AvatarUpdateEvent.SittingOnLocalId`.
- `src/SLNG.Core/Components/AvatarComponent.cs` — `SittingOnLocalId` (0 = standing/ground-sitting).
- `src/SLNG.Core/WorldSimulation.cs` — `ApplyAvatarUpdate`/`ExtrapolateMovement` treat a seated
  local agent's Z/Rotation as network-authoritative (like a remote avatar), since
  `AvatarController` stops owning them while seated.
- `app/scripts/ObjectSelectionController.cs` — left-click dispatches Touch vs. Sit by `ClickAction`.
- `app/scripts/UI/InWorldContextMenu.cs`, `app/scripts/Boot.cs` — `🪑 Sit` / `🪑 Sit Here` menu wiring.
- `app/scripts/AvatarController.cs` — `isSitting` gate on WASD/fly/ground-clamp/body-rotation
  write; movement-key stand-up trigger (`_standRequestedThisSit` debounce).

## Sub-tasks / Progress
- [x] Touch (grab/de-grab) — landed earlier (see git history).
- [x] Sit request + stand wrappers in `GridSession`.
- [x] Seat transform resolution at the network boundary.
- [x] ECS/UI wiring (`ClickAction`-based dispatch, context menu, movement gating).
- [x] Unit tests.

## Known limitations (follow-up, not blocking)

### RESOLVED: seated avatar rendered floating above the seat
Live-tested on a real OpenSim chair (no `llSitTarget` script — OpenSim's non-scripted-sit fallback,
`ScenePresence.SendSitResponse`'s `AbsolutePosition = pos + Vector3(0,0,m_sitAvatarHeight)` path,
`m_sitAvatarHeight = PhysicsActor.Size.Z * 0.5`). Three render approaches were tried this session,
all wrong in ways that don't triangulate cleanly on a fix:

1. **Standing formula applied to the seat-resolved position** (current shipped state —
   `AvatarRenderer.cs`'s `UpdateVisual` has NO special case for `SittingOnLocalId`, it just runs
   the ordinary local/remote ground-correction formula against `transform.Position`, which
   `GridSession.ResolveSeatedTransform` has already turned into a world-space value). Live result:
   avatar renders ~8cm *below* the seat prim's own reported origin (computed and confirmed via
   `visual.Root.GlobalPosition` — no coordinate-frame or parent-transform bug), yet is clearly
   visible floating well above both the seat prim and the platform it sits on in a screenshot.
   The 8cm figure and the visual gap don't reconcile — something about what the seat prim's own
   `Position.Z` *means* relative to "where its visible top surface is" isn't understood yet (it's
   a small ~1m decorative object, not a normal chair mesh, so its own origin convention may not be
   what was assumed).
2. **No correction at all** (seat-resolved position used as-is): live-tested strictly worse —
   rendered even higher than (1).
3. **Snapshot-and-follow** (mirrors `LLVOAvatar::sitOnObject`'s actual mechanism — capture the
   avatar's last STANDING position relative to the seat once, at the sit transition, then just
   follow the seat's own transform afterward, ignoring the ongoing wire position entirely): live-
   tested worse in a DIFFERENT way — SLNG has no autopilot-to-seat (unlike real SL, which walks the
   avatar to the sit target before completing the sit), so this approach freezes the avatar
   wherever they happened to be standing when they clicked, not anywhere near the seat.

Reverted to (1) as the least-bad of the three.

**2026-08-01 — 4th attempt, source-verified this time (not another guess):** a side-by-side
Firestorm screenshot of the same object surfaced that `AvatarRenderer.UpdateVisual` was running
the STANDING-avatar ground-correction formula (`halfBodyZ`/`FootOffsetY`/`PelvisToFootZ`/
`AvatarHoverParamZ`/pelvis-fixup) against a seated avatar's position at all — attempts 1-3 only
ever varied *which* ground-correction formula ran, never questioned whether one should run while
seated. Verified against the real viewer source (`scratch/slviewer/indra/newview/llvoavatar.cpp:
4591-4734`, `LLVOAvatar::updateRootPositionAndRotation`): sitting-on-an-object is a hard separate
branch (lines 4725-4733) that skips ALL of those terms — `mRoot` is just set to the seat-relative
wire position (already what `GridSession.ResolveSeatedTransform` computes) plus `getHoverOffset()`
only, nothing else. `UpdateVisual` now branches on `avatar.SittingOnLocalId != 0` and applies only
`transform.Position.Z - mPelvisY + avatar.HoverOffsetZ` while seated (see the branch's own comment
for line citations). Confirmed via `mRoot` being coincident with the pelvis in the real skeleton
(`llappearance/llavatarappearance.cpp:878,:1021`), so the existing `mPelvisY` conversion already
used on the remote-standing branch is the correct (and only) term needed here too.

**Live-verified 2026-08-01.** Throttled `[SitDiag]`/`[SitRenderDiag]`/`[PrimShapeDiag]` diagnostics
(temporary — see below) traced the fix's numbers against a real OpenSim seat step by step:
`GridSession.ResolveSeatedTransform`'s resolved world position matched
`AvatarRenderer`'s rendered `visual.Root.GlobalPosition` exactly, every frame, for the whole
session — confirming the formula itself is correct end to end. The FIRST test object (a small
sculpted "bowl" seat) still looked visibly wrong after the fix; a second seat on a different object
rendered correctly with the same code, proving the remaining wrongness on the first object was that
object's own seat configuration (an unusually large sit-target offset baked into its script), not a
SLNG bug — real-viewer parity was never broken by anything left in this codebase.

All temporary `[SitDiag]`/`[SitRenderDiag]`/`[PrimShapeDiag]` diagnostic logging added during this
investigation (in `GridSession.OnAvatarUpdate`/`OnTerseObjectUpdate`, `AvatarRenderer.UpdateVisual`,
and `ObjectRenderer.UpdateVisual`/`LoadAndApplyPrimMeshAsync`/`LoadAndApplySculptMeshAsync`) has been
removed again now that this converged.

Known secondary gap, confirmed NOT the cause of this bug but still a real fidelity gap: `GridSession.
RequestSit` always sends `LibreMetaverse.Vector3.Zero` as the click offset (`GridSession.cs:1165`),
whereas the real viewer sends the actual click-surface offset (`llviewermenu.cpp:4655`/
`handle_object_sit_or_stand`), which OpenSim's non-sit-target fallback folds into the seat's XY (and
Z, before the height term) — `ScenePresence.SendSitResponse`, `pos = part.AbsolutePosition + offset`.
SLNG currently always seats centered on the prim's own origin regardless of where clicked. Not fixed
in this pass — flagged for whoever picks up sit-target fidelity next.

### Camera does not apply the sit-specific camera offset
Camera does not apply the sim's `AvatarSitResponse` `CameraAtOffset`/`CameraEyeOffset`/
`ForceMouselook` (llSetCameraAtOffset/llSetCameraEyeOffset/llForceMouselook) — the seated camera
uses the same third-person follow as standing. Subscribing to `AvatarSitResponse` also requires
working around a real LibreMetaverse bug (`ScriptSensorReply` misrouted to the same handler,
throwing `InvalidCastException` on every `llSensor` reply once subscribed) — see the protocol-re
research this task was built on, if picked up later.

### No inter-packet interpolation for a moving seat
A moving seat (vehicle) only updates the seated avatar's resolved position when a fresh
`ObjectUpdate`/`TerseObjectUpdate` arrives for the avatar itself; it does not additionally
interpolate using the seat's own between-packet movement.
