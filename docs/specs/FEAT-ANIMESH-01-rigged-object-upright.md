# [FEAT-ANIMESH-01] Animated-mesh objects: upright bind pose, then animation

- **Feature ID:** `FEAT-ANIMESH-01` (static, upright) and `FEAT-ANIMESH-02` (animated)
- **Track:** `render` / `net`
- **Status:** 🚧 In Progress
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Live 2026-10-01: an animesh robot rezzed in-world lies on its side and never moves.

SLNG treats every rigged mesh that is not worn as a raw static mesh. `ObjectRenderer.BuildArrayMesh`
never looks at `MeshData.Skin`, so the bind-shape matrix and the skeleton are never applied, and
nothing plays the object's animations. The reference viewer does the same for a rigged mesh that is
neither worn nor animesh; for animesh it creates a *control avatar* — an avatar-skeleton instance
owned by the object — and skins the mesh to it.

Goal: an animesh renders upright at its authored size (prim scale does not resize it, as in the viewer) and plays the animations its scripts start.

## Viewer parity (secondlife/viewer, `scratch/slviewer` @ 4ef9f8f1; paths relative to `indra/`)

**An unflagged rigged mesh on the ground is a plain static mesh in the real viewer too.**
`LLVolumeGeometryManager::rebuildGeom` (newview/llvovolume.cpp:5809-5820): `rigged = !is_animated &&
skinInfo && vobj->isAttachment()`. No bind shape, no skinning (llface.cpp:1553-1561 only applies the
bind shape when the face is `RIGGED`). So "lies on its side" is correct for an object the sim does not
flag as animesh — the flag is what changes it.

**Detection.** ExtraParams `0x70` (`PARAMS_EXTENDED_MESH`, llprimitive.h:109): `U8 count`, then per
entry `U16 type`, `S32 size`, payload; little-endian (lldatapacker.cpp:292-334). Payload = one `U32
flags` (llprimitive.cpp:2273-2285); `ANIMATED_MESH_ENABLED_FLAG = 0x1` (llprimitive.h:357). **Root prim
only**: `LLVOVolume::isAnimatedObject` (llvovolume.cpp:3854-3863) reads the root's block; children's own
blocks are ignored. The control avatar exists once the flag is set AND a rigged mesh is present in the
root or a direct child (`updateControlAvatar`, llviewerobject.cpp:3218-3266).

**Control avatar.** The stock avatar skeleton (`avatar_skeleton.xml`), never given an appearance, no
body parts drawn (llcontrolavatar.cpp:54-57; llvoavatar.cpp:5055). Destroyed with the object or when
the root is re-parented to a non-avatar (llviewerobject.cpp:3303-3320, 444-447; llvovolume.cpp:3871-3896).
The mesh's joint-position overrides, `lock_scale_if_joint_position` and `pelvis_offset` are applied
exactly as for a worn mesh (`addAttachmentOverridesForObject`, llvoavatar.cpp:6714-6850) — SLNG already
does this in `ApplyJointPositionOverrides`.

**Placement — the part that makes the robot stand up** (`LLControlAvatar::matchVolumeTransform`,
llcontrolavatar.cpp:203-246, verified against the file):
- `mRoot.worldRot = bind_rot * obj_rot`; `obj_rot` is the root prim's rotation.
- `bind_rot = LLSkinningUtil::getUnscaledQuaternion(root mesh's bindShapeMatrix)` (llskinningutil.cpp:383-406):
  take the upper 3×3, **normalise each ROW**, **invert**, convert to a quaternion. Identity if the root
  has no skin. In row-vector terms: `R_world = N⁻¹ · R_obj`, N = row-normalised 3×3 of the bind shape.
  The bind shape carries the axis conversion and the upload scale; this undoes the rotation half.
- `mRoot.pos = root prim render position` (+ a constraint fixup, see below). When the root face is rigged
  and the mesh has a pelvis offset, `getRenderPosition` adds `(0,0,pelvis_offset)` in WORLD Z, unrotated
  (llviewerobject.cpp:4777-4790).
- **Prim scale does NOT scale the skeleton.** Global scale is 1.0 unless the animated bounding box
  exceeds `AnimatedObjectsMaxLegalSize` (64 m) — then `64/box`; the position fixup applies only when
  the root is more than `AnimatedObjectsMaxLegalOffset` (3 m) from the box (llcontrolavatar.cpp:100-155,
  251-278). Both constraints are out of scope for v1.
- Re-run on every root transform update and every frame (lldrawable.cpp:731-737; llvoavatar.cpp:4713).

**Vertex chain** (identical to a worn rigged mesh, so SLNG's `BuildRiggedMeshInstance` already has it):
`p_agent = p_asset · BSM · Σ w_k (IBM_k · W_k)`; BSM baked into positions, normals through the
inverse-transpose; `W_k` uses only the joint's OWN scale. Rigged meshes ignore the prim model matrix
entirely (llvovolume.cpp:5396-5400).

**Animations** (FEAT-ANIMESH-02). `ObjectAnimation` (message_template.msg:7421-7434): `Sender{ID}`, then
`AnimationList{AnimID, AnimSequenceID}`; one message per prim. The control avatar plays the UNION of the
root's and all child prims' lists, keeping the larger sequence id; a changed sequence id restarts that
animation (llcontrolavatar.cpp:559-607; llvoavatar.cpp:6094-6104). Assets come through the normal
animation path. **With nothing signalled the object stands in the rest pose** — no stand, no breathing
(`mEnableDefaultMotions=false`, llcontrolavatar.cpp:56) — and once something has played it stays in the
last pose it left (see "Joints nothing drives"). The viewer never reads task inventory to
play anything; an animesh only moves when a script calls `llStartObjectAnimation`.

**Pelvis position keys.** An animation's pelvis position key is the pelvis' ABSOLUTE local position, measured
from the skeleton root, not an offset on its rest. `LLKeyframeMotion::applyKeyframes` hands the curve value
straight to the joint state (llkeyframemotion.cpp:414-417; `PositionCurve::getValue` only interpolates the
keys, :313-358); keyframe motions are `NORMAL_BLEND` (llkeyframemotion.h:127), so
`LLJointStateBlender::blendJointStates` copies or lerps the joint states' positions and never adds a rest
(llpose.cpp:322-329), and applies them with `setPosition(blended_pos + added_pos)` (:388), whose
`apply_attachment_overrides` defaults to false (lljoint.h:233) -- the mesh's joint-position override does not
apply while a key is active. What the key is measured from differs between the two kinds of avatar: a real
avatar's root sits at pelvis height (`root_pos.z -= 0.5*bodySize - mPelvisToFoot`, llvoavatar.cpp:4677), so a
stand animation's pelvis key is ~0 (the cached stand 2408fe9e keys (-0.028, 0.074, 0.016)); SLNG's avatar
skeleton is rooted at the feet, which is why `rest + key` is the same place there. A control avatar is never
lifted that way: `updateRootPositionAndRotation` hands it to `matchVolumeTransform` instead
(llvoavatar.cpp:4713), which puts the root AT the root prim (`mRoot->setPosition(vol_pos + fixup)`,
llcontrolavatar.cpp:244). So for a control avatar the key is the pelvis height above the root prim, and with
`rest + key` every key displaces the mesh by the pelvis' own rest height too much (Paul: 0.295 m too high
while crouching). `AvatarAnimationPlayer.AbsolutePelvisPosition` is the switch; ordinary avatars keep
`rest + key`. With no pelvis key active the pelvis keeps what it had (below).

**Joints nothing drives.** The viewer never resets a joint when a motion ends. `blendJointStates` starts
from the joint's CURRENT position and rotation (llpose.cpp:257-258) and writes them back (:388-390); with no
joint state it returns early, "instead of resetting joint state to default, just leave it unchanged from
last frame" (:242-244); `LLPoseBlender::blendAndApply` visits only the blenders of joints that have an
active joint state (:512-522); `LLMotion::deactivate` only zeroes the motion's pose weight
(llmotion.cpp:157-170); `LLKeyframeMotion::onDeactivate` only releases constraints (llkeyframemotion.cpp:789-795);
and llmotioncontroller.cpp resets joint SIGNATURES (:486-492), never joints. A control avatar has no default
motions to take over (`mEnableDefaultMotions = false`, llcontrolavatar.cpp:56), so a joint no active animation
drives KEEPS its last rotation (and, for the pelvis, position), also after the last animation stops; a joint
that was never driven is at rest. `AvatarAnimationPlayer.HoldUndrivenBones` is the switch (control avatars
only; an ordinary avatar always has a stand animation and keeps resetting). A held pose is restored after
`ApplyJointPositionOverrides` resets the skeleton's poses for a second mesh (`ReapplyHeldPose`).

**Linkset.** Any rigged child of an animesh root is skinned by the root's control avatar and its own prim
transform is irrelevant; non-rigged children are rigid and follow the root prim.

**Caveats.** (1) OpenSim only sends `ObjectAnimation` to clients that list the capability in their
seed request; LibreMetaverse 3.1.6's capability list contains the string `ObjectAnimation` (checked in the
assembly) — verify on the wire in -02 rather than adding it blindly. (2) Not traced: whether default-weight
skeletal-distortion deltas are ever applied to a control avatar. v1 uses the undistorted skeleton; if a
robot's limbs are off against Firestorm, look here first.

## Protocol facts
- LibreMetaverse 3.1.6 does not parse `0x70` into `Primitive`; read it out of the raw ObjectUpdate bytes
  the way the Light and Reflection-Probe blocks are.
- LibreMetaverse raises `ObjectAnimationEventArgs` (`ObjectID` + animation list) from `ObjectManager`.

## Acceptance Criteria

### FEAT-ANIMESH-01
- [x] A raw-ExtraParams parser reads the `0x70` block's flags; unit-tested against hand-built byte
      sequences, including a block that is not first, a short payload and a missing block.
- [x] `PrimitiveComponent` carries `IsAnimatedMesh`, set from the event and cleared when the block
      disappears (same latch discipline as the Light block); only the ROOT prim's block counts.
- [x] An animesh root with a rigged mesh gets a control-avatar skeleton (default shape, no body
      parts), placed on the root prim per the viewer's `LLControlAvatar::matchVolumeTransform`.
      (`ControlAvatarPlacement` + `AvatarRenderer.ControlAvatar.cs`; checked headless by the
      `control avatar (animesh)` selftest against a known answer, not yet in-world.)
- [x] The rigged mesh is skinned to it through the same bind path worn rigged mesh uses
      (`BuildRiggedMeshInstance`), so it stands in its bind pose.
- [x] A non-animesh rigged mesh keeps today's behaviour (raw static mesh).
- [x] Moving or rotating the root prim moves/rotates the control avatar with it (prim SCALE does not
      scale it — viewer parity). Re-placed every frame from the root's `TransformComponent`.
- [x] No control avatar outlives its object (derez, region change, out of draw distance) -- headless self-test `animesh hand-over`.
      Implemented on all three paths (`RemoveVisual` in both renderers, `ReleaseResources`, and a
      per-frame sweep for a root that left the world); only "last mesh released frees the skeleton"
      is exercised by the selftest, the rest waits for the in-world check.
- [x] `dotnet build SLNG.sln`, `dotnet build app/SLNG.App.csproj`, `dotnet test`, `dotnet format` clean.

### FEAT-ANIMESH-02
- [ ] `GridSession` forwards `ObjectAnimation` to the world as a neutral event; `Core` holds the
      object's current animation list.
- [x] The control avatar's `AvatarAnimationPlayer` plays that list, fetching assets through
      `AssetService.GetAnimationAsync`. The list is the UNION over the root and every child prim
      (`ControlAvatarAnimations.Union`, larger sequence id wins); the headless selftests
      `control avatar animation (animesh)` and `animesh animation hand-off` drive it with a
      synthetic loader. Not yet seen with a real animesh.
- [x] A removed animation stops; an object with no animation stays in its bind pose.

## Technical Specs & Affected Files

- `src/SLNG.Net/GridSession.Objects.cs` — `0x70` scan next to `ExtraParamsReflectionProbe`.
- `src/SLNG.Core/GridEvents.cs`, `src/SLNG.Core/Components/PrimitiveComponent.cs`,
  `src/SLNG.Core/WorldSimulation.cs` — carry the flag.
- `app/scripts/AvatarRenderer.cs` (+ a partial file) — a control avatar is an `AvatarVisual`
  without body parts: `SkeletonBuilder.Build` + `ApplyShape` with empty distortions, then
  `BuildRiggedMeshInstance`. `AvatarAnimationPlayer` is already a standalone class.
  The new code is `app/scripts/AvatarRenderer.ControlAvatar.cs`; `AvatarVisual.IsControlAvatar`
  widens only the two `IsSelf`-only gates (rest-pose extent, rigged pick bodies).
- `app/scripts/ObjectRenderer.cs` — hands an animesh root's mesh to the control avatar instead of
  assigning it to its own `MeshInstance3D`. Boot wires `ObjectRenderer.ControlAvatars`.
- `src/SLNG.Core/ControlAvatarPlacement.cs` (the placement rotation, unit-tested) and
  `src/SLNG.Core/AnimatedMeshLinkset.cs` (root-only flag, "root unknown" = not animesh yet).
- FEAT-ANIMESH-02, render half: `src/SLNG.Core/ControlAvatarAnimations.cs` (union + diff, unit-tested),
  `app/scripts/AvatarRenderer.ControlAvatarAnimation.cs` (recompute, fetch, apply, per-frame advance),
  `ObjectRenderer.SyncSignaledAnimations` (the trigger), `AvatarAnimationPlayer.Restart` /
  `HasFinished` (additive). Boot wires `AvatarRenderer.LinksetChildren` to `WorldSimulation.ChildrenOf`.

## Sub-tasks / Progress
- [x] 0x70 parse + tests
- [x] Carry the flag into the world
- [x] Control avatar + upright bind pose (awaiting the in-world check)
- [ ] FEAT-ANIMESH-02: ObjectAnimation → playback
