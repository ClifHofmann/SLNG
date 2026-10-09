# [BUG-AVATAR-10] A worn animesh pet folds its wearer into its own skeleton, and nothing gives a mesh's joint overrides back

- **Feature ID:** `BUG-AVATAR-10`
- **Track:** `render`
- **Status:** `🧪 Review` — implemented (part 1 v0.27.13, part 2 v0.27.14, part 3 v0.27.15) and covered by unit tests and `--selftest` checks; part 1 confirmed in-world (wearer no longer deformed), part 2 not yet.
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
handed back. In part 1 the mesh was still bound to the wearer's skeleton; part 2 (below) gives it a skeleton of its own, and this path then only sees a race (the flag arriving between the routing check and here).

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
4. (Parts 2 and 3) The pet is **outside the body, at the attachment point**, in its own shape (the 0.35 m creature,
   not squashed into the wearer), playing **its own animation** (the `ObjectAnimation` stream for the root),
   and moves with the wearer's animation. Hide the wearer (draw distance) and the pet goes too; detach it and
   the skeleton goes (`[AttachAnimesh] control avatar released …` under `--diag`); a relog or a fresh attach
   brings it back (`… created …`). It stands where Firestorm draws it for the same avatar and moment, with the
   same orientation. Anything else worn on Chest or Spine with a non-zero position or rotation now sits where it
   does in Firestorm too. `--diag` prints the `[AttachAnimesh] placement` numbers to compare.

## Technical Specs & Affected Files

- `src/SLNG.Core/JointOverrideSet.cs` (new), `src/SLNG.Core/AnimatedMeshLinkset.cs`, `src/SLNG.Core/AttachmentPointRotation.cs` (new, part 3)
- `app/scripts/AvatarRenderer.cs` — `AvatarVisual.JointOverrides`/`OverrideContributors`,
  `ApplyJointPositionOverrides`, `ApplyWornJointOverrides`, `ReleaseWornJointOverrides`,
  `RemoveVisual`, `UpdateAttachment`, `UpdateHudAttachment`
- `app/scripts/AvatarRenderer.WornAnimesh.cs` (new, part 2), `app/scripts/AvatarRenderer.ControlAvatar.cs`
- `app/scripts/AvatarRenderer.WornOverrides.SelfTest.cs` (new), `app/scripts/SelfTest.cs`
- `tests/SLNG.Core.Tests/JointOverrideSetTests.cs` (new), `tests/SLNG.Core.Tests/AnimatedMeshLinksetTests.cs`
- `app/scripts/Boot.cs` — `AppVersion`

## Part 2: a control avatar for *attached* animesh (v0.27.14)

Part 1 (v0.27.13) kept the pet's overrides off the wearer. Tested in-world, the wearer was no longer
deformed, **and the dragon sat inside her**: still skinned to the wearer's normal-size skeleton, without
its own overrides, and not animated by the `ObjectAnimation` stream. Confirmed from the log
(`[AttachAnimesh] … mesh=035ac8fd root=d74624c8 animatedRoot=True`, and the same for `86f1e621`), so the
premise holds. Part 2 gives it what the viewer gives it: a control avatar of its own.

### Viewer

| Fact | Where |
|---|---|
| An animated attachment gets a control avatar like a rezzed one | `LLViewerObject::updateControlAvatar` → `linkControlAvatar` → `LLControlAvatar::createControlAvatar`, llviewerobject.cpp:3218-3278, llcontrolavatar.cpp:336 |
| Placement of an attached one | `LLControlAvatar::matchVolumeTransform`, llcontrolavatar.cpp:176-197: `mRoot->setWorldPosition(obj_pos.rotVec(joint_rot) + joint_pos)`, `mRoot->setWorldRotation(obj_rot * joint_rot)`, with the attachment point's `getWorldPosition()` / `getWorldRotation()` and the root prim's drawable-local position and rotation. **No `bind_rot`** (the region branch, :217-232, has one) and **no pelvis fixup** (the root joint is set directly, not through `getRenderPosition`) |
| Its visibility is the wearer's | `shouldRenderRigged()` / `isImpostor()`, llcontrolavatar.cpp:682-697 |

### Design

The mesh is routed in `CommitPreparedRig` (`AvatarRenderer.WornAnimesh.cs`, `TryRouteWornRigToControlAvatar`,
called before anything touches the wearer): when the attachment's linkset root is animated mesh
(`AnimatedMeshLinkset.IsAnimatedAttachment`), the rig is built on a **control avatar keyed by that root**
(`ControlAvatar`, `AvatarRenderer.ControlAvatar.cs`, with `WearerEntityId` / `WearerVisual` /
`AttachmentPoint`), via the same `InstallControlAvatarPart` → `BuildControlAvatarPart` a rezzed animesh
uses. `ApplyJointPositionOverrides` therefore runs on the **control** visual, where the pelvis override
*is* applied. The wearer gets nothing from these meshes: no overrides, no skin, no `RiggedAttachments`
entry, no `_riggedAttachments` entry. Only rigged prims route; the plain prims of the same linkset stay
ordinary attachments at the attachment point. The prepare done for the wearer's skeleton on the rig worker
is wasted for these meshes (two per pet); the commit simply does not use it.

- **Placement, per frame** (`PlaceAttachedControlAvatar`): world transform = attachment frame × (root
  prim's local position and rotation), SL → Godot like a static attachment's. The frame is the wearer's
  attachment point **as the skeleton is posed now** (`TryGetPosedAttachmentFrame`: bone global pose, then
  the point's own offset) rather than the `BoneAttachment3D` node, which only catches up with the bone at
  the end of the frame; `UpdateControlAvatars` therefore runs at the **end** of `AvatarRenderer._Process`,
  after the wearers' animation was advanced. The node-based `TryGetAttachmentFrame` is the fallback. No
  `bind_rot`, no pelvis fixup, no scale (as the viewer). The skeleton stays hidden until it could be
  placed once.
- **Animations:** the same machinery as a rezzed animesh (`ControlAvatarAnimation`), driven by the signalled
  lists of the **root and every child prim** of the worn linkset; `ObjectAnimation` names the root and
  lands on its `PrimitiveComponent`, whose component-updated event runs `UpdateAttachment`, which raises the
  control avatar's dirty flag (`SyncWornAnimesh`). The wearer's `AnimPlayer` is never involved.
- **Visibility:** each frame the pet's parts and root follow `wearer.Root.Visible` (the draw-distance cull),
  and a hidden pet is not advanced.
- **Lifecycle:** detach (`RemoveVisual` → `ReleaseControlAvatarMesh`), a re-mesh or HUD move, the root
  leaving the world, the wearer's visual being **rebuilt** (the control avatar is freed and its parts are
  asked for again, so the new visual gets a fresh one), the wearer's visual **gone**, or the root no longer
  being worn: each frees the skeleton (`FreeControlAvatar` → `Visual.QueueFree`, which takes the part nodes
  and pick bodies with it). A new object id on re-attach is simply a new root. A root's animated flag
  flipping is passed to the rest of the linkset (`UpdateAttachment` is called for the children), and a mesh
  on the wrong side of the flag is released and reloaded.
- **`--diag`:** the `[AttachAnimesh]` line stays; `[AttachAnimesh] control avatar created|released
  root=<obj8> wearer=<SELF|agent8> point=<n> parts=<k>` marks the control avatar's life.

### Known limits (unchanged by this part)

- A worn pet is not selectable/outlined or highlighted as a worn item (the same open items as region
  animesh: FEAT-ANIMESH-03), and a root that is itself not rigged and has no attachment node yet keeps the
  pet hidden until its attachment point exists.
- The rotation has no `bind_rot` because the viewer's attached branch has none. If a pet whose mesh was
  authored with a rotated bind shape ever shows lying on its side while worn, that is the first thing to
  compare with Firestorm.
- Per frame, the animesh update-rate reduction for distance (FEAT-ANIMESH-03) does not exist for worn ones either.

## Part 3: the attachment point's own frame was wrong for Chest and Spine (v0.27.15)

In-world with part 2 the pet had its own shape and animation but stood in the wrong place and turned:
Firestorm (same avatar, same moment) drew the dragon at the wearer's right hand at hip height, wings
hanging; SLNG drew it at the right shoulder / behind the head about 0.5 m higher, wings spread flat, "turned
by roughly 90 degrees". The log gave the inputs: `point=1` (Chest, `mChest`), root a plain prim, mesh a child.

**Cause: the rotation of the Chest attachment point.** A worn item's position and rotation are expressed in
the attachment point's frame: the joint, then the point's own offset and rotation from `avatar_lad.xml`
(`AttachPointOffset`). The viewer builds that rotation with `LLQuaternion::setQuat(roll, pitch, yaw)`
(llvoavatar.cpp:7198-7205; the formula is llquaternion.cpp:295-311), which is `qx * qy * qz` in Hamilton
terms, i.e. as a rotation **Z first, then Y, then X**. SLNG built it with `SlEulerDegToGodotBasis`, the
order for a *joint*'s rotation, `mayaQ(x, y, z, XYZ)` = **X first, then Y, then Z** (llavatarappearance.cpp:642).
The two agree for a rotation about one axis and disagree for two. Of the 55 points in `avatar_lad.xml` exactly two
turn about more than one axis, both on the torso: **Chest (id 1, `0 90 90`) and Spine (id 9, `0 -90 90`)**.
For those the frame was **120 degrees off** (the viewer's Chest frame is `(0.5, 0.5, 0.5, 0.5)`, a third of a turn about the diagonal; the old order produced another one), which
turns everything worn there and moves it by the distance it sits from the point: an offset of (0.3, -0.3, -0.5)
lands 1.1 m from where the viewer puts it. Nothing showed it before because every point checked was
single-axis (Skull, shoulders, wrists) and rigged mesh clothing ignores the point entirely.

Each input of the pet's placement was checked against the viewer:

| Input | Result |
|---|---|
| Point id | `LibreMetaverse` `PrimData.AttachmentPoint = SwapWords(State)`, the nibble swap of `ATTACHMENT_ID_FROM_STATE`: same as the viewer. State byte of Chest = `0x10` |
| Chest's offset and rotation | Transcribed correctly: `position="0.15 0 -0.1" rotation="0 90 90"` |
| `AttachPointOffset` applying that rotation | Applied, **with the joint Euler order**: the defect |
| Root prim transform | Attach-point-local, not world: `WorldSimulation.ResolveWorldTransform` leaves an avatar attachment's `Position`/`Rotation` at the received local values |
| SL → Godot of the control avatar's frame | The same `(x, z, -y, w)` that `ApplyControlAvatarPlacement` (region: `bind_rot * obj_rot`) and the static attachment path use; composing `attachFrame * local` is the conjugation of the viewer's `obj_rot * joint_rot` and nothing is skipped. Replayed in the selftest in SL terms |
| The static path | Uses the same `AttachPointOffset`, so a static attachment on Chest or Spine was displaced and turned the same way |

**Fix:** `SLNG.Core.AttachmentPointRotation.FromEulerDegrees` is `setQuat(roll, pitch, yaw)` line for line, and
`AttachPointOffset` uses it. This changes **every** attachment worn on Chest or Spine, static prims and meshes
included, and the edit gizmo's frame (`TryGetAttachmentFrame`), which share the function.

**Ground truth (Firestorm, Edit on the worn dragon root, worn on Chest):** Pos `<-0.26352, 0.18030, 0.17475>`,
Rot `<279.1, 359.4, 3.6>` (the Edit floater shows `getEulerAngles`, the inverse of `setQuat`). Replayed by hand and in
`AttachmentPointRotationTests` / the `worn animesh` selftest, in the chest joint's frame (SL axes):

| Order | Root position | Root up | Reads as |
|---|---|---|---|
| `setQuat` (the viewer; now) | (0.325, -0.264, 0.080) | (0.16, -0.01, 0.99) = chest up | 32 cm forward, 26 cm to her RIGHT, chest height, standing upright: Firestorm's picture |
| `mayaQ` XYZ (SLNG until v0.27.15) | (-0.030, 0.175, 0.164) | (-0.99, 0.16, 0.01) = backwards | at her LEFT shoulder, lying flat with its wings spread level above her head: the SLNG screenshot |

A single-axis point (Skull `0 0 90`) is asserted unchanged.

**Diagnostics:** `[AttachAnimesh] placement root=… point=<id> (<bone>, state=0x.., pointRot=(..)) local pos=… rot=…
frame[posed] … frame[node] … -> root pos=… rot=…`, once per change of the point or the root's local transform
(not every frame): the attachment point as decoded and the State byte it came from, the root prim's local
transform as received, the attachment frame from the skeleton's pose and from the scene node, and the resulting
root, all in SL axes.

**Tests:** `AttachmentPointRotationTests` pins the formula (Chest = `(0.5, 0.5, 0.5, 0.5)`, Spine = `(-0.5, -0.5,
0.5, 0.5)`, Z-then-Y-then-X read off three single-axis turns, and that it is not the joint order). The `worn
animesh` selftest gained a check that the Chest point's frame maps the point's axes onto the joint's as the viewer
does; it fails with the old order restored. (The placement check there takes the attachment frame as given, which
is why it passed while the frame itself was wrong.)

### Merge with `fix/BUG-PERF-12-crowded-avatar-load` (not merged here)

That branch (v0.27.13 to v0.27.16, unmerged) also changed `CommitPreparedRig`:

1. It added an early branch for rigs whose surfaces are all invisible (`ready.Mesh.SurfaceArrays.Length == 0`)
   **after** `DiscardRiggedAttachment`, which calls `ApplyJointPositionOverrides` directly. After the merge
   that call site must go through the animesh routing too: `TryRouteWornRigToControlAvatar(entityId, req)`
   has to run **before** `DiscardRiggedAttachment` (so before that branch), and the branch's raw
   `ApplyJointPositionOverrides` must become `ApplyWornJointOverrides(entityId, req)` — otherwise an
   invisible-surface rig of an animated object puts its overrides on the wearer again, and the overrides of
   an ordinary one are never registered as contributed (and so never given back on detach).
2. `AppVersion` has to be **renumbered**: both branches claim `v0.27.13-alpha` (this one is now
   `v0.27.15-alpha`, so it also collides with PERF-12's `v0.27.15`). Whichever merges second takes the next
   free patch number.

## Sub-tasks / Progress

- [x] `JointOverrideSet` + tests
- [x] `AnimatedMeshLinkset.AttachedRootOf` / `IsAnimatedAttachment` + tests
- [x] Worn path: skip for animated roots, per-mesh apply, contributors
- [x] Release on detach / re-mesh / HUD move, shared rebuild helpers
- [x] `--diag` `[AttachAnimesh]` line
- [x] `--selftest` check `worn joint overrides`
- [x] `AppVersion` bump
- [x] Part 2: worn animesh control avatar (routing, placement, animations, visibility, lifecycle, `--diag`)
- [x] `--selftest` check `worn animesh` (wearer unchanged; pet skeleton carries the overrides; placement against
      the viewer's formula; follows the wearer; own animation; hides with the wearer; freed on rebuild, detach and
      wearer loss) — fails with the routing hook, the placement or the visibility rule removed (verified)
- [x] Part 3: the attachment point's Euler order (`AttachmentPointRotation`), `[AttachAnimesh] placement` diagnostics, selftest regression check
- [ ] In-world confirmation
