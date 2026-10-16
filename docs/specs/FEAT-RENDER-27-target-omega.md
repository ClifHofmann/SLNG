# [FEAT-RENDER-27] Client-side target omega (llTargetOmega)

- **Feature ID:** `FEAT-RENDER-27`
- **Track:** `render` (with `net` and `core`)
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Reported in-world 2026-10-16 at Secret Love: the ".:revival:. boho hammock (swing)" rocks gently
back and forth in Firestorm and stands still in Puris. Fans, rotating planes and carousels did not
turn either: SLNG ignored the AngularVelocity an object update carries. The Secret Love object
cache alone holds about 40 objects with a non-zero one.

A non-physical spinner is turned by the **viewer**, not streamed by the simulator. The simulator
sends the omega (`llTargetOmega` axis times rate) once, and the viewer turns the object every frame
on its own. This feature does the same, including the sitters on a turning seat.

## Reference behaviour (scratch/slviewer/indra/newview)

- `LLViewerObject::idleUpdate` (llviewerobject.cpp:2503-2510): every frame, for an object that is
  not static, not selected, with velocity interpolation on, `applyAngularVelocity(dt)` with
  `dt` scaled by the region's time dilation.
- `applyAngularVelocity` (:7305): if `|omega|^2 > 0.00001`, turn by `|omega| * dt` about omega's
  axis: `mAngularVelocityRot *= dQ`, `setRotation(getRotation() * dQ)`.
- `processUpdateMessage` (:2349-2370): when the rotation or the omega changed,
  - a server rotation different from the last one -> `resetRot()` (drop the turn);
  - else a changed omega -> physical: `resetRot()`, non-physical: `resetRotTime()` (keep the turn);
  - then `setRotation(new_rot * mAngularVelocityRot)`.
- OUT_FULL (uncompressed ObjectUpdate, :1416): an omega of exactly zero -> `resetRot()`. The
  compressed and terse paths do not do this. A compressed update without the omega flag leaves the
  omega as it was (:1810), which LibreMetaverse does too.
- LL's quaternion product is the reverse of the Hamilton product (llquaternion.cpp:534), so
  `getRotation() * dQ` turns **after** the object's own rotation: the omega axis is in the
  **parent's** frame (the region for a root, the root for a child). In System.Numerics the drawn
  rotation is `turn * serverRotation`.
- A sitter's drawable hangs under its seat's (`LLVOAvatar::sitOnObject`), so a turning seat (its own
  omega or its root's) carries the sitter rigidly. A control avatar (animesh) is placed from the root
  drawable's rotation (llcontrolavatar.cpp:216-219), which includes the turn.

## Acceptance Criteria

- [ ] The hammock at Secret Love (OSGrid) and at Sirens (Agni) rocks with its sitters like in Firestorm.
- [ ] Other llTargetOmega objects spin (fans, carousels, rotating planes).
- [ ] fps on a busy region is not measurably worse (only turning prims get per-frame work, no per-frame allocation).
- [x] Unit tests pin the reset and accumulate rules (`tests/SLNG.Core.Tests/TargetOmegaTests.cs`, 19 cases).
- [x] `--selftest` checks the renderer part (`target omega (llTargetOmega) objects`, `target omega carries sitters`).

## Technical Specs & Affected Files

- `src/SLNG.Net/GridSession.Objects.cs` — omega from the full (`Primitive.AngularVelocity`), compressed
  (same) and terse (`ObjectMovementUpdate.AngularVelocity`) paths into `ObjectUpdateEvent.AngularVelocity`.
  `ResetsSpin` marks an uncompressed update with a zero omega for an object seen turning; LibreMetaverse
  raises one `ObjectUpdate` event for both kinds, so the difference is read off `ObjectDataBlockUpdate`,
  which only the uncompressed handler raises, synchronously, before it queues that event.
  Full updates now pass their time dilation too.
- `src/SLNG.Core/Components/SpinComponent.cs` — the viewer's rules: `ApplyUpdate` (resets),
  `Advance` (accumulate, kept as base * angle so an hour of frames does not drift), `Apply`.
  Present only on an object that has turned; removed again once nothing is turned and nothing turns.
- `src/SLNG.Core/WorldSimulation.cs` — `ApplySpin` per update, `AdvanceSpins` once per frame (time
  dilation, not while selected), `SpinDrivenEntities`: the turning prims and every prim linked under
  them, rebuilt (not allocated) when it may have changed and at least once a second.
- `src/SLNG.Core/TargetOmega.cs` — the drawn transform (`TryGetRenderTransform`, every spin up the
  linkset) and the rigid seat move for sitters (`TryGetSeatTurn`). The world model keeps the
  simulator's transforms: the turn is visual only, as in the viewer.
- `app/scripts/ObjectRenderer.TargetOmega.cs` — `TickTargetOmega`: per frame, only the spin-driven
  nodes (position + rotation). `UpdateVisual` draws the same transform, so an update never snaps a
  prim back for a frame. A spin-driven prim leaves its MultiMesh group and `EvaluateInstancing` keeps
  it out; collider, light, particles and split surfaces are children of its node and turn with it.
- `app/scripts/AvatarRenderer.TargetOmega.cs` — `CarryWithTurningSeat`: the seat's rigid move put on
  the placed Root of a seated avatar (runs every frame for seated avatars). Placed after the
  BUG-AVATAR-12 seated pose, so it composes with that fix when both are merged.
- `app/scripts/AvatarRenderer.ControlAvatar.cs` — an animesh is placed on its turned root.
- `app/scripts/Boot.cs` — `AdvanceSpins` after `ExtrapolateMovement`; `SpinDriven` wired at login.

Diagnostics (`--diag`): `[TargetOmega] turning localId=… id=… root|child of … at=… omega=… rate=…
period=… dilation=…` once per object that turns by itself, and `[TargetOmega] sitter avatar=…
carried by turning seat localId=…` once per sitter and seat. The per-frame pass shows up as
`obj-ticks.spin` and the advance as `spin` in `[PhaseCost]`.

## Sub-tasks / Progress

- [x] Wire format: omega through all three update kinds, the uncompressed zero-omega reset.
- [x] Core rules + per-frame advance + drawn transform, unit-tested.
- [x] Renderer: per-frame pass, instancing kept out, consistent `UpdateVisual`.
- [x] Sitters and animesh follow a turning root/seat.
- [ ] In-world check at Secret Love and Sirens (hammock with sitters), and an fps comparison on a busy region.

## Not done

- Worn attachments that spin (the viewer turns them too); they are placed on bones by `AvatarRenderer`
  and are left as they are.
- The local agent's camera does not turn with a turning seat (it follows the avatar's position).
