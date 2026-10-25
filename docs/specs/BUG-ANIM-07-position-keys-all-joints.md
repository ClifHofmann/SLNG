# [BUG-ANIM-07] A pose animation's position keys on any joint but the pelvis are dropped

- **Feature ID:** `BUG-ANIM-07`
- **Track:** `render`
- **Status:** `✅ Done`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Genital / add-on HUDs shape their add-on with pose animations that set only **positions**, on
`mHindLimb1-4` (Left/Right), `mHindLimbsRoot` and `mGroin`. SLNG discards them, so the add-on
keeps whatever the skeleton default or the worn mesh's own joint override says:

- one avatar showed the add-on's "normal" shape instead of the HUD's "clothing" shape (3.4 mm
  through her bikini);
- another avatar's add-on hung as a strip to mid-thigh (bones left at skeleton defaults, up to
  0.39 m off the posed place).

**Cause.** `AvatarAnimationPlayer.ApplyAnimationToBones` accepts a position key only when
`joint.JointName == "mPelvis"`, citing `llbvhloader.cpp:781` ("Animating position ... is only
supported for mPelvis"). That line is the BVH **importer's** channel parsing, not `.anim`
**playback**. The rule came in with BUG-ANIM-01 (`137300a6`, `v0.22.125-alpha`).

What the viewer does (vendored under `scratch/slviewer/indra/llcharacter/`):

- reads position keys for **every** joint (`llkeyframemotion.cpp:1680-1782`; the joint name
  matters only for the pelvis bounding box, :1695) and applies them to every joint with a
  position curve (:414-417) as an **absolute local position**, not an offset;
- the blender writes `target_joint->setPosition(blended_pos + added_pos)` (`llpose.cpp:388`) with
  `apply_attachment_overrides` defaulting to **false** (`lljoint.h:233`, `lljoint.cpp:333-357`),
  so while a position animation runs its key **beats** a worn mesh's joint-position override;
  overrides are only written on attach/detach (`lljoint.cpp:412-446, 567-587`,
  `llvoavatar.cpp:6802`);
- per joint the highest priority wins (`llpose.cpp:200-230, 237-330`).

**Why BUG-ANIM-01 narrowed it to the pelvis.** Commit `0c1c790a` first applied position keys on
every joint as `rest + key`. Face joints then moved ("dummy tracks on mFaceTongueBase"). That
was the arithmetic, not the data: a real animation's tongue keys equal the skeleton defaults
exactly (`mFaceTongueTip` 0.022, 0, 0.007), i.e. the key is the **absolute** local position and
`rest + key` counted it twice. Written as an absolute position the same key lands where the
joint already was, which is what the tongue needs.

## Acceptance Criteria

- [x] A position key on any joint is applied, by the same per-joint priority rule as rotation
      (highest wins; equal priority: the later animation wins, as today).
- [x] `mPelvis` is exactly as before: `rest + key` (or the absolute key for a control avatar,
      `AbsolutePelvisPosition`).
- [x] Every other joint takes the key as its absolute local position, scaled by the **parent's**
      own scale -- `godot = (k.X*ps.X, k.Z*ps.Z, -k.Y*ps.Y)` with `ps = BoneOwnScale[parent]`, the
      conversion `AvatarRenderer.ApplyShape` uses for overrides. The rest origin is never added.
- [x] While the animation drives the joint's position, its key beats a worn mesh's joint-position
      override; when nothing drives it any more it returns to the override / default position.
- [x] A decoded v1.0 `.anim` with position keys on `mHindLimb1Left`, `mHindLimb2Left`, `mGroin`
      comes out of `AnimationDecodeService` with those positions (to the format's 1.5e-4 m
      quantisation).
- [ ] Face / tongue / hand / stand animations look unchanged (in-world; the tongue case is in the
      selftest).
- [x] `--selftest` ("animation position keys"): a position-keyed animation on `mHindLimb2Left` moves
      that bone to the keyed position (scaled by the parent's scale), a worn override does not win
      while it runs, the bone goes back when the animation stops; pelvis behaviour unchanged.

## Technical Specs & Affected Files

- `src/SLNG.Core/JointPriorityTable.cs` -- the per-joint priority-wins table both channels
  (rotation, position) use; `src/SLNG.Core/JointPriority.cs` -- the effective-priority rule
  (locomotion boost, per-joint priority, animation priority).
- `src/SLNG.Core/AnimatedJointPosition.cs` -- how a joint's position key becomes the position it
  takes: pelvis `rest + key` / absolute, any other joint the absolute key scaled by the parent.
- `app/scripts/AvatarAnimationPlayer.cs` -- accept position keys on every joint; apply them
  through `AnimatedJointPosition`; keep a parent-scale table (`SetParentScales`).
- `app/scripts/AvatarRenderer.cs` -- `ApplyShape` hands the player the per-bone parent scale it
  just computed, so it is refreshed after every shape / override change.
- `tests/SLNG.Assets.Tests/AnimationPositionKeyDecodeTests.cs`, `tests/SLNG.Core.Tests/{JointPriorityTable,JointPriority,AnimatedJointPosition}Tests.cs`,
  `app/scripts/AvatarRenderer.PositionKeys.SelfTest.cs`. `--diag` shows `positioned=N` (joints with
  position keys) on the `[AnimPlayer] loaded` line.

Interplay checked:

- **Joint-position overrides / `JointOverrideSet` / lock_scale_if_joint_position** -- overrides
  and scale locks live in the bone *rest*; a position key replaces the rest only while it drives.
  Scale locks change `BoneOwnScale`, hence the parent scale the key is multiplied by, so the
  table is refreshed in `ApplyShape`.
- **Outfit merge re-bind (FEAT-PERF-23)** -- skin binds come from the rests; a position key
  changes the pose, not the binds, so no re-bind is needed.
- **Animesh / control avatars** -- the same player. They hold undriven bones
  (`HoldUndrivenBones`); a held non-pelvis position is kept as the SL-space key and converted
  again with the current parent scale on `ReapplyHeldPose`.
- **Self and seated avatars** -- same path; `SeatPoseResolver` / `SlJointComposer` read rests and
  shape, never animation keys.

## Not done

- The viewer leaves a joint at its last blended position when the animation that drove it
  stops (nothing resets it until the next attach/detach). SLNG returns to the override / default
  position. Left as is: it is the existing behaviour for every other bone.

## Sub-tasks / Progress

- [x] Root cause and viewer citations.
- [x] `SLNG.Core` helpers + tests.
- [x] `.anim` decode test with position keys on `mHindLimb*` / `mGroin`.
- [x] Player + `ApplyShape` parent scales (`v0.27.96-alpha`).
- [x] Selftest on a real skeleton.
- [x] `/slng-verify`.
- [ ] In-world: add-on shaped by a HUD pose; faces / tongue / hands / stand unchanged.
