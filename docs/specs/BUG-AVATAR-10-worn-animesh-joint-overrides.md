# [BUG-AVATAR-10] A worn animesh pet folds its wearer into its own skeleton, and nothing gives a mesh's joint overrides back

- **Feature ID:** `BUG-AVATAR-10`
- **Track:** `render`
- **Status:** `🧪 Review` — implemented and covered by unit tests and a `--selftest` check; not yet confirmed in-world.
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Found 2026-10-09 from a remote avatar (Cat diArcono, `ab0665e3`) holding a dragon pet: her head sat on
top of her shoes. After she took the pet off and we relogged, she looked fine.

The dragon mesh (`035ac8fd-74da-19bb-6bc9-2f0de7744f60`, plus the part `86f1e621-acbc-2ead-86df-8a20cf76dcb5`),
worn on the chest, carries **84 joint-position overrides** that form a ~0.35 m creature skeleton: pelvis
0.145 m, knees 5 cm below the hips, `lock_scale_if_joint_position`. SLNG's worn-rigged-mesh path called
`ApplyJointPositionOverrides` for every rigged attachment, so the wearer's own skeleton became the
dragon's. `ObjectAnimation` messages for object `c490ab6b` began the moment the dragon loaded, which
fits an attached animesh, but SLNG did not log the animated-mesh flag on attachments, so that was
unconfirmed (step 1 below makes it checkable).

Two defects, one symptom:

1. **An animated object worn on an avatar must never change that avatar's skeleton.**
2. **Joint overrides were never taken back.** `AvatarVisual.JointPosOverrides` was one flat joint map,
   last writer wins, and `JointScaleLocks` a flat set. Nothing removed an entry when the mesh that set it
   was detached, so a detached pet *or mesh body* left the avatar deformed until a relog rebuilt the
   maps from whatever was still worn.

The goal is the viewer's behaviour for both: an attached animated object affects joints in its control
avatar, not its wearer's; and overrides belong to the mesh that carried them.

## Reference viewer

All in `scratch/slviewer/indra`:

| Fact | Where |
|---|---|
| Attached animated objects are left out of the wearer's override rebuild | `newview/llvoavatar.cpp:6573-6575` (`rebuildAttachmentOverrides`: "Attached animated objects affect joints in their control avs, not the avs to which they are attached"), `:6624` (`updateAttachmentOverrides`) |
| ...and out of the update on attach and detach | `llvoavatar.cpp:7607` (`attachObject`), `detachObject` (`is_animated_object` → no `updateAttachmentOverrides`) |
| Whether an object is animated is the **root's** flag | `LLVOVolume::isAnimatedObject`, `llvovolume.cpp:3854-3863` (already ported as `AnimatedMeshLinkset`, FEAT-ANIMESH-01) |
| Overrides are per joint, per mesh id | `LLVector3OverrideMap`, `llcharacter/lljoint.h`; `LLJoint::addAttachmentPosOverride` / `addAttachmentScaleOverride`, `lljoint.cpp:412` / `:619` |
| The active override is the entry with the **greatest mesh id** | `LLVector3OverrideMap::findActiveOverride`, `lljoint.cpp:46` (`std::max_element` over the key) |
| A joint's scale is locked while **any** mesh holds a scale override for it | `lljoint.cpp:619-640`, `hasAttachmentScaleOverride` |
| Detach erases the mesh from every joint and its pelvis fixup | `LLVOAvatar::removeAttachmentOverridesForObject`, `llvoavatar.cpp:6968-7020` |

The viewer keys everything by **mesh id**, not by attachment: two worn items that wear the same mesh share
one entry, and one of them coming off does not remove it (the rebuild path keeps every mesh it still
sees). SLNG reproduces that with a contributor map (attachment → mesh).

## Design

### `SLNG.Core.JointOverrideSet` (new, engine-agnostic)

Holds, per mesh id, the joint positions and the scale locks that mesh contributes, and exposes the
**effective** view the shape code consumes:

- `Positions` — one entry per joint: the entry of the greatest mesh id, in `Guid.CompareTo` order (the
  same tie-break `TryGetActivePelvisFixup` already uses; arbitrary but deterministic, like the viewer's).
- `ScaleLocks` — the union of every mesh's locks.
- The existing left/right **mirror** (a mesh that overrides only `mFootLeft` is applied to `mFootRight`,
  Y negated) moved here and is *derived* on every change, so it leaves with the mesh that caused it
  instead of becoming a permanent entry. A side some mesh overrides itself is never mirrored over.
- `Apply(meshId, positions, locks)` replaces what a mesh contributes (a rig rebuilt at another detail
  level applies identical data and is not a change); `Remove(meshId)` takes it back. Both return whether
  the **effective** view changed, which is what decides whether the skeleton and every skin must be
  rebuilt — a mesh whose every joint another mesh wins costs nothing to remove.

`AvatarVisual.JointPosOverrides` / `JointScaleLocks` are now read-only views onto it, so `ApplyShape`,
`ComputeBodySize` and the scale-lock reads in `ApplyShape` are unchanged apart from taking the read-only
types.

### Animated-root skip (`AvatarRenderer.ApplyWornJointOverrides`)

`AnimatedMeshLinkset.AttachedRootOf` / `IsAnimatedAttachment` (new) find the root of a **worn** linkset —
the prim whose parent is the avatar — and read its `IsAnimatedMesh` flag. `RootOf` / `IsAnimatedPart`
deliberately stop at an avatar, because they answer for world objects; this is the other half.

The check runs when the rig is **committed** (`CommitPreparedRig`), not when it was requested: the answer
comes from the live world and a mesh takes seconds to arrive. For an animated root the whole
`ApplyJointPositionOverrides` call is skipped — positions, scale locks and the pelvis fixup alike — and
anything the same attachment had put there before (the root's flag arriving after the first rig) is
handed back. The mesh is still bound to the wearer's skeleton (see Follow-up).

The root/pelvis skip *inside* `ApplyJointPositionOverrides` for ordinary (non-control) avatars is
unchanged.

### Revert on detach (`AvatarRenderer.ReleaseWornJointOverrides`)

`AvatarVisual.OverrideContributors` (attachment entity → mesh id) is written when an attachment's
overrides are applied. Releasing an attachment:

1. drops it from the contributors; if another worn item still wears the same mesh, stops there;
2. removes the mesh's pelvis fixup (this used to be unconditional at three call sites);
3. removes the mesh from `JointOverrideSet`, and **only if the effective view changed** runs the same
   rebuild a change in the other direction runs — `ApplyShape` + `ResetBonePoses`,
   `RebuildRiggedAttachmentSkins`, `RefreshBodyPartSkins`, `RefreshStaticAttachmentOffsets`,
   `RecomputeFootOffset` (now the two helpers `RebuildSkeletonAfterJointChange` and
   `RefreshFootAfterJointChange`, shared with `ApplyJointPositionOverrides`).

Called from: `RemoveVisual` (detach / derez / re-parent into a world linkset), `UpdateHudAttachment`
(moved to a HUD point), and `UpdateAttachment` when the same attachment now carries a **different** mesh
or no mesh at all. The same mesh again (another LOD, new face textures) deliberately keeps its overrides.
The detached node comes out of `AvatarVisual.RiggedAttachments` first, since `QueueFree` is deferred and
the rebind would otherwise skin a mesh that is about to go (`UpdateAttachment` now uses
`DiscardRiggedAttachment` for the same reason).

Control avatars are untouched: they call `ApplyJointPositionOverrides` directly, own their visual, and
their parts' overrides still live as long as the skeleton does.

### Diagnostics (`--diag`)

`[AttachAnimesh] <SELF|agent8> attachment=<obj8> mesh=<mesh8> root=<obj8|unresolved> animatedRoot=<bool>`
once per attachment (and again if the answer changes, or when the rig commit withholds the overrides:
`… -- joint overrides NOT applied to the wearer`). The object ids are the SL object UUIDs
(`MetadataComponent.Id`), i.e. the same eight characters `[ObjectAnimation] object=` prints, so a stream
of those can be matched to what is worn. A `[JointOverride] … taken off` line (ungated for
non-control avatars, like `[ScaleLock]`) reports each revert.

## Acceptance Criteria

- [x] `--diag` logs, once per attachment, whether its linkset root is animated mesh and which object the root is.
- [x] A rigged attachment whose root is animated mesh applies no joint-position override, scale lock or
      pelvis fixup to the wearer (root *and* child prims; only the root's flag counts).
- [x] Taking a rigged mesh off puts the skeleton, skins and foot offset back, with no relog; two worn
      items of one mesh keep the mesh's overrides until the last one is off.
- [x] The viewer's tie-break (greatest mesh id wins a joint) and the left/right mirror hold per mesh and
      leave with the mesh.
- [x] The root/pelvis skip for non-control avatars is unchanged.
- [x] Unit tests (`JointOverrideSetTests`, `AnimatedMeshLinksetTests`) and a `--selftest` check
      (`worn joint overrides`) — the check fails with either half of the fix removed (verified by
      disabling each in turn: the animated-root skip leaves the hip 300 mm off; the release on detach
      leaves it there after the item is gone).
- [ ] Confirmed in-world (below).

## In-world check

1. Wear an animesh pet (a dragon, a robot, a companion): **the wearer looks exactly as she did without
   it**. With `--diag`, the log has an `[AttachAnimesh] … animatedRoot=True` line and the
   `[ObjectAnimation] object=` id for that pet matches its `root=`.
2. Detach a mesh body (or any fitted mesh that overrides joints): the skeleton returns to normal
   **without a relog** — a `[JointOverride] … taken off` line appears and the avatar's height and feet are
   re-measured.
3. Swap one fitted mesh for another (an applier): no leftover from the first.

## Technical Specs & Affected Files

- `src/SLNG.Core/JointOverrideSet.cs` (new), `src/SLNG.Core/AnimatedMeshLinkset.cs`
- `app/scripts/AvatarRenderer.cs` — `AvatarVisual.JointOverrides`/`OverrideContributors`,
  `ApplyJointPositionOverrides`, `ApplyWornJointOverrides`, `ReleaseWornJointOverrides`,
  `RemoveVisual`, `UpdateAttachment`, `UpdateHudAttachment`
- `app/scripts/AvatarRenderer.WornOverrides.SelfTest.cs` (new), `app/scripts/SelfTest.cs`
- `tests/SLNG.Core.Tests/JointOverrideSetTests.cs` (new), `tests/SLNG.Core.Tests/AnimatedMeshLinksetTests.cs`
- `app/scripts/Boot.cs` — `AppVersion`

## Follow-up (not in this task): a control avatar for *attached* animesh

This task only keeps the pet's overrides **off the wearer**. The pet itself is still skinned to the
wearer's skeleton, *without* its own overrides, so it renders in the wrong shape (a creature rigged to a
0.35 m skeleton, bound to a human-sized one) and is not animated by the `ObjectAnimation` stream. The
viewer gives an attached animated object a control avatar of its own (`LLViewerObject::updateControlAvatar`
→ `linkControlAvatar`, llviewerobject.cpp:3218-3278 → `LLControlAvatar::createControlAvatar`,
llcontrolavatar.cpp:336) and applies the object's overrides and animations to *that* skeleton; for an
attachment the control avatar is positioned from the wearer's target attachment point
(llcontrolavatar.cpp:84-95 and 176-181).

SLNG already has the pieces for region animesh (`AvatarRenderer.ControlAvatar.cs`,
`ObjectRenderer.TryHandOverToControlAvatar`, FEAT-ANIMESH-01/02). What an attached one still needs,
each to be verified against the viewer source before building: the hand-over from the worn path in
`CommitPreparedRig` instead of `BuildControlAvatarPart` for a region prim; **placement** — the control
avatar follows the attachment point's bone, not a region position (`ApplyControlAvatarPlacement` takes a
region render position today); and routing `ObjectAnimation` events for a *worn* root, which
`ObjectRenderer` currently receives only for prims it draws itself. Proposed ID: `FEAT-ANIMESH-04`.

## Sub-tasks / Progress

- [x] `JointOverrideSet` + tests
- [x] `AnimatedMeshLinkset.AttachedRootOf` / `IsAnimatedAttachment` + tests
- [x] Worn path: skip for animated roots, per-mesh apply, contributors
- [x] Release on detach / re-mesh / HUD move, shared rebuild helpers
- [x] `--diag` `[AttachAnimesh]` line
- [x] `--selftest` check `worn joint overrides`
- [x] `AppVersion` bump
- [ ] In-world confirmation
