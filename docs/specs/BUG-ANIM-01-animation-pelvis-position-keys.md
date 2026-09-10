# [BUG-ANIM-01] Animation position keyframes (pelvis) are discarded — seated & lying poses render wrong

- **Feature ID:** `BUG-ANIM-01`
- **Track:** `render`
- **Status:** `⏸️ Pending`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

User-reported: **other avatars do not sit correctly when they are in a pose** — they
clip into the furniture, float above it, or sit beside the actual pose position.

The sit *anchor* is not the problem. `AvatarRenderer.ResolveSeatedTransform` /
`updateRootPositionAndRotation` resolves the `ParentID` + wire-relative offset to world
space and is source-verified against the real viewer (MVP2-1).

The problem is in the animation player. [`AvatarAnimationPlayer.ApplyBonePoses`](file:///E:/Git/SLNG/app/scripts/AvatarAnimationPlayer.cs)
evaluates **only rotation keyframes** and deliberately drops **position keyframes**
(`app/scripts/AvatarAnimationPlayer.cs`, ~line 206):

> "SL animation position keys (almost always just on mPelvis) use a reference frame
> that, applied directly, drops the pelvis to the avatar root and sinks the whole body
> into the ground. … Root motion (jumps, real translation) is deferred."

Nearly every furniture / photography pose animation carries a `mPelvis` translation
relative to the seat (lying down, leaning, cross-legged, cuddle poses). With that track
zeroed, the body stays centred on the sit point while only the limbs rotate → visibly
wrong seating.

Scope: this affects **every** avatar (self included) playing any animation with pelvis
position keys, seated or not. It is most obvious on seated remote avatars because that
is what a user looks at.

## Acceptance Criteria

- [ ] A seated remote avatar playing a furniture pose with a `mPelvis` position track
      renders with the pelvis at the pose-authored offset from the seat, matching
      Firestorm within a few cm.
- [ ] A lying / reclining pose (large pelvis offset) no longer sinks into or floats
      above the surface.
- [ ] Standing / idle / built-in locomotion poses are unchanged (no regression to
      FEAT-ANIM-01 self-locomotion prediction).
- [ ] The self avatar, seated on a pose stand, renders its own pose correctly (feeds
      the same path used for the Snapshot Studio "freeze for a photo" slice).
- [ ] Priority blending still applies: a higher-priority animation's pelvis track wins
      over a lower-priority one, per bone, same rule as rotation.
- [ ] Unit test on `AvatarAnimationPlayer` covering pelvis-position evaluation +
      priority resolution with a synthetic anim.

## Technical Specs & Affected Files

- `app/scripts/AvatarAnimationPlayer.cs` — apply the evaluated position track
  (`bonePoses[*].position` / `hasPos`) via `Skeleton3D.SetBonePosePosition`, not just
  `SetBonePoseRotation`. Position keys are relative to the joint's **rest** position, in
  the animation's own units (already converted SL→Godot `(x, z, -y)` in the evaluator).
- Reference frame for `mPelvis` when seated: combine the anim pelvis delta with the
  resolved seat transform and the existing `mPelvisY` / hover / ground-offset
  conversion. The seated math is already worked out in
  [`docs/specs/MVP2-1-object-interaction-sit-touch.md`](file:///E:/Git/SLNG/docs/specs/MVP2-1-object-interaction-sit-touch.md)
  (`transform.Position.Z - mPelvisY + avatar.HoverOffsetZ`, with `llavatarappearance.cpp`
  citations) — the missing piece is feeding the anim's pelvis delta through it instead
  of zeroing it.
- `app/scripts/AvatarRenderer.cs` — verify the root-position resolution and the anim
  pelvis offset do not double-count; the animation offset is skeleton-local, the seat
  resolution is on the avatar node.
- Confirm against `scratch/slviewer` (`LLKeyframeMotion::applyKeyframes`,
  `LLJointState` position blending) via the `viewer-parity` agent before finalising the
  reference frame — this is exactly the "how does the real viewer actually do this"
  case.

## Sub-tasks / Progress

- [ ] `viewer-parity`: pin down `mPelvis` position-key reference frame (rest-relative,
      units, seated vs unseated, priority blend) against vendored viewer source.
- [ ] Apply position track in `ApplyBonePoses` with priority-wins blending.
- [ ] Seated reference-frame integration + no double-count with `ResolveSeatedTransform`.
- [ ] Regression check: FEAT-ANIM-01 locomotion, standing idle, AO walk.
- [ ] Unit tests.
- [ ] In-world: furniture pose, lying pose, pose stand (remote + self), vs Firestorm.
