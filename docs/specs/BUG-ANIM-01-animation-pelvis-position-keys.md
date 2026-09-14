# [BUG-ANIM-01] Animation position keyframes (pelvis) are discarded — seated & lying poses render wrong

- **Feature ID:** `BUG-ANIM-01`
- **Track:** `render`
- **Status:** `✅ Done`
- **Owner:** `gemini`
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

- [x] A seated remote avatar playing a furniture pose with a `mPelvis` position track
      renders with the pelvis at the pose-authored offset from the seat, matching
      Firestorm within a few cm.
- [x] A lying / reclining pose (large pelvis offset) no longer sinks into or floats
      above the surface.
- [x] Standing / idle / built-in locomotion poses are unchanged (no regression to
      FEAT-ANIM-01 self-locomotion prediction).
- [x] The self avatar, seated on a pose stand, renders its own pose correctly (feeds
      the same path used for the Snapshot Studio "freeze for a photo" slice).
- [x] Priority blending still applies: a higher-priority animation's pelvis track wins
      over a lower-priority one, per bone, same rule as rotation.
- [x] Position channel application is strictly scoped to `mPelvis` per SL protocol (`llbvhloader.cpp:781`),
      preventing accidental mesh displacement on face/tongue bones from unnormalized BVH tracks.
- [x] Unit test on `BinBVHAnimationReader` and `UnpackPosition` decoding neutral to zero meters.

## Technical Specs & Affected Files

- `app/scripts/AvatarAnimationPlayer.cs` — apply the evaluated position track
  (`bonePositions[*].position`) via `Skeleton3D.SetBonePosePosition`, scoped to `mPelvis`.
  Position keys are relative to the joint's **rest** position (`restPos + pose.position`),
  faithfully reproducing SL's `target_joint->setPosition(blended_pos)`.
- `tests/SLNG.Assets.Tests/AnimationRotationUnpackTests.cs` — unit test for binary BVH
  keyframe unpacking and scaling parity.

## Sub-tasks / Progress

- [x] `viewer-parity`: pin down `mPelvis` position-key reference frame (rest-relative,
      units, seated vs unseated, priority blend) against vendored viewer source.
- [x] Apply position track in `ApplyBonePoses` with priority-wins blending and `mPelvis` guard.
- [x] Seated reference-frame integration + no double-count with `ResolveSeatedTransform`.
- [x] Regression check: FEAT-ANIM-01 locomotion, standing idle, AO walk, facial bones.
- [x] Unit tests.
- [x] In-world: pose stand tested and verified against Firestorm by user.

