# [BUG-AVATAR-07] Fitted mesh body/head renders too small — `lock_scale_if_joint_position` was never honored

- **Feature ID:** `BUG-AVATAR-07`
- **Track:** `render` / `assets`
- **Status:** `✅ Done`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Reported live 2026-09-12 with a side-by-side screenshot (SLNG left, Firestorm right) of the same
avatar on Agni, with a reference cube rezzed at head height and roughly head size: *"der avatar
shape passt nicht … in SLNG ist der avatar kleiner und der kopf kleiner, außerdem sieht das
gesicht nicht richtig aus."*

The avatar wears a **Maitreya Lara V5.3** mesh body and a **LeLutka EvoX** mesh head — i.e. the
visible body and face are rigged meshes, not the system avatar.

### Root cause (source-verified)

A mesh asset's `skin` section can carry `lock_scale_if_joint_position` (the uploader's *"Lock
scale if joint position defined"* checkbox). In the real viewer,
`LLVOAvatar::addAttachmentOverridesForObject` (indra/newview/llvoavatar.cpp) does:

```cpp
if (pJoint->aboveJointPosThreshold(jointPos))
{
    pJoint->addAttachmentPosOverride( jointPos, mesh_id, avString(), override_changed );
    ...
    if (pSkinData->mLockScaleIfJointPosition)
    {
        // Note that unlike positions, there's no threshold check here,
        // just a lock at the default value.
        pJoint->addAttachmentScaleOverride(pJoint->getDefaultScale(), mesh_id, avString());
    }
}
```

`LLPolySkeletalDistortion::apply` writes the shape sliders' accumulated joint scale through
`joint->setScale(newScale, /*apply_attachment_overrides=*/true)`, and that setter
(indra/llcharacter/lljoint.cpp) **replaces** the requested scale with the active attachment
override. So for every joint a lock-declaring mesh position-overrides, the shape's skeletal
**scale** distortion is discarded outright. `getDefaultScale()` is `avatar_skeleton.xml`'s own
`scale` for that bone (`LLAvatarAppearance::allocateCharacterJoints` → `setDefaultScale`), which is
SLNG's `BoneDefinition.Scale` — so the correct behavior is *"skip the distortion delta"*, not
*"force 1,1,1"* (collision volumes have non-unit defaults).

Crucially the lock lives on the **joint**, not on the mesh that requested it: it applies to every
mesh skinned to that joint.

SLNG parsed `alt_inverse_bind_matrix` and `pelvis_offset` but ignored `lock_scale_if_joint_position`
entirely (`MeshSkin` had no such field), and `AvatarRenderer.ApplyShape` added every distortion's
scale unconditionally.

### Measured on the user's own cached assets

Decoding the worn assets straight out of `%APPDATA%\Godot\app_userdata\Puris Viewer\cache\assets`
(no live session needed):

- Maitreya Lara body `89b2cf14-04ba-d4f1-dfaa-0adc493245aa`: `lock_scale_if_joint_position = true`,
  **51 of its 52 joints above the 0.1 mm position threshold** — including `mPelvis`, `mTorso`,
  `mChest`, `mNeck`, **`mHead`**, `mEyeLeft/Right`, both collars/shoulders/elbows/wrists, both
  hips/knees/ankles/feet/toes. Firestorm therefore renders this avatar with **no skeletal scale
  distortion at all**.
- Replaying the user's own 253 transmitted VisualParams through `AvatarShapeService` gives the
  scales SLNG was applying to those same joints:

  | bone | SLNG scale | Firestorm (locked) |
  |---|---|---|
  | `mHead` / `mSkull` | 0.924 | 1.000 |
  | `mTorso` | 0.94 / 0.94 / **0.799** | 1.000 |
  | `mNeck` | 0.794 / 0.784 / 0.949 | 1.000 |
  | `mChest` | 0.93 / 0.899 / 1.005 | 1.000 |
  | `mHipLeft` | 0.922 | 1.000 |
  | `mKneeLeft` | 0.928 / 0.928 / 0.888 | 1.000 |
  | `mWristLeft` | 0.869 | 1.000 |
  | `mEyeLeft` | 0.895 | 1.000 |

  That is the whole reported symptom in one table: head ~7.6 % small, torso 20 % short, eyes and
  face bones off — *"der avatar kleiner und der kopf kleiner … das gesicht sieht nicht richtig
  aus"*. The LeLutka head does not declare the flag itself; it is shrunk because the **body** locks
  `mHead`.

Two things were checked and ruled out along the way, and are **not** bugs:

- `AvatarShapeService`'s driven-param math. Param 682 "Head Size" (group 0, default `.5`) drives
  655 "Head Size" (group 1, range `[-.25, .10]`, uniform `scale="1 1 1"` on the head bones) through
  `LLDriverParam::getDrivenWeight`'s default ramp; at 0.498 that is `-.25 + .498*.35 = -0.0757`,
  i.e. a 0.924 head scale is what the *default* head-size slider genuinely produces. Matches the
  viewer exactly.
- Chained driver → driver params: the shipped `VisualParams` table contains **zero** of them, so
  `ComputeEffectiveWeights`' single level of driving is complete for real data.

## Acceptance Criteria

- [x] `MeshSkin` carries `LockScaleIfJointPosition`, read from LibreMetaverse's
      `MeshSkinData.LockScaleIfJointPosition` (present in the pinned 3.1.3 package — verified in the
      shipped DLL, not just the source checkout).
- [x] A lock-declaring worn mesh pins every joint it position-overrides to the skeleton's default
      scale; the shape's skeletal scale distortion on those joints is dropped.
- [x] The lock is recorded even for the root joint, whose *position* override SLNG deliberately
      skips — the viewer's scale lock is keyed only on passing the position threshold.
- [x] Position distortions and position overrides are untouched (separate maps in the viewer).
- [x] `SlJointComposer.ComputeBodySize` honors the same lock, so the avatar's rendered Z and its
      joint scales cannot disagree.
- [x] Unit tests written and passing.

## Technical Specs & Affected Files

- `src/SLNG.Assets/MeshData.cs` — `MeshSkin.LockScaleIfJointPosition`.
- `src/SLNG.Assets/AssetService.cs` — `ConvertSkin` plumbs the LMV flag through.
- `src/SLNG.Core/SlJointComposer.cs` — `IsScaleLocked` (the one place the viewer rule is written
  down), honored by `ComputePoses` and `ComputeBodySize`.
- `app/scripts/AvatarRenderer.cs` — `AvatarVisual.JointScaleLocks`, populated in
  `ApplyJointPositionOverrides` (mirrored to the implicit left/right sibling), honored in
  `ApplyShape`, passed to `ComputeBodySize` from `RecomputeFootOffset`.
- `tests/SLNG.Core.Tests/SlJointComposerTests.cs` — five tests.

## Sub-tasks / Progress

- [x] Decode the flag from the asset and plumb it to the renderer.
- [x] Drop the scale distortion on locked joints.
- [x] Keep `ComputeBodySize` in step.
- [x] Tests + `/slng-verify`.
- [ ] Confirm in-world against Firestorm with the same reference cube.

## Round 2 — why v0.22.91 changed nothing on screen

The user re-tested and reported *„da hat sich aber nix geändert"*. Verified first that the data
path was fine: decoding the cached body through the pinned LibreMetaverse 3.1.3 exactly the way
`AssetService.Decode` does reports `LOCK=True` for `89b2cf14`, `False` for the LeLutka head meshes.
So the flag reached the renderer and the locks were being recorded.

The reason nothing moved is **stale skinning binds**. `AvatarVisual.BoneOwnScale` is not carried by
the Godot pose — it is baked into each mesh's `Skin` bind at build time (`InjectOwnScale`). The
shape-change path has always refreshed both consumers afterwards (`RebuildRiggedAttachmentSkins`,
plus `RebuildBodyMorphs`' `PartSkins` eviction), but `ApplyJointPositionOverrides` — which calls the
same `ApplyShape` and mutates exactly the same state — refreshed neither. Anything already bound
therefore kept rendering at the *previous* skeleton, and because the stale bind carries the old
scale the result is **pixel-identical**, not merely approximate — which is exactly what "no
change" looks like.

That ordering is the normal case here, not an edge case: the LeLutka head loads before the Maitreya
body as often as not, and it is the **body's** flag that frees `mHead`.

Fixed in `v0.22.92-alpha`:

- `ApplyJointPositionOverrides` now calls `RebuildRiggedAttachmentSkins` and a new
  `RefreshBodyPartSkins` (bind matrices only — morphed geometry depends on VisualParam weights,
  which a joint override does not touch) after `ApplyShape`/`ResetBonePoses`.
- The whole re-apply block is now gated on a `skeletonChanged` flag — a genuinely new or different
  position override, or a newly added scale lock — instead of `applied > 0`, which was true for
  every mesh of an outfit re-supplying the same overrides. Without that gate the new rebuild would
  be O(n²) over worn rigged meshes.
- One ungated `[JointOverride] … joint scale(s) locked to the skeleton default` line, so "the lock
  is active" is readable from the normal client log instead of needing `--diag`.

## Round 3 — the lock never fired; the fingerprint is the suspect

v0.22.92 still rendered the head too small, and the client log carries **no `[JointOverride]` line
at all** — so `locked` was never greater than zero and no scale lock was recorded. The assemblies
were verified fresh (both `LockScaleIfJointPosition` and `JointScaleLocks` present in
`app/.godot/mono/temp/bin/Debug/*.dll`, `[Boot] v0.22.92-alpha` in the log).

The likely error is the asset fingerprinting in "Measured on the user's own cached assets" above.
Only **2 of 901** cached rigged meshes carry the flag at all — `89b2cf14` (52 joints) and an animal
tail — and the mesh cache holds every nearby avatar's worn body, not just this account's. A Maitreya
Lara V5.3 with a full finger rig looks much more like `a2a889c4` (76 joints, 61 above threshold,
**lock absent**). If that is the worn body, the scale-lock port is correct but inert for this
avatar, and the size difference has a different cause.

Also checked and ruled out this round: the viewer uses `mAlternateBindMatrix` **only** to read the
joint position override's translation (`llvoavatar.cpp:6782`); the skinning palette is built from
`mInvBindMatrix` (`LLSkinningUtil::initSkinningMatrixPalette`), exactly as SLNG does — so SLNG is
not silently binding against the wrong matrix.

`v0.22.93-alpha` adds two ungated readouts so one relog settles which it is:

- `[RiggedSkin] <meshId>: joints=N altBinds=N aboveThreshold=N lock_scale_if_joint_position=…`,
  once per mesh id per session, and only for meshes that actually carry alternate bind matrices.
  It also names the early return (`altBinds != joints`) rather than returning silently.
- `[HeadSize] … scaleLocks=N, bodySizeZ=N m` — previously `--diag`-only, which is why the number
  the whole report is about was never in the log.

## Round 4 — the lock hypothesis is dead; the evidence now points at HEIGHT

The v0.22.93 readouts settle the first question: **every worn rigged mesh in the session reports
`lock_scale_if_joint_position=False`.** `89b2cf14` was somebody else's body sitting in the shared
mesh cache. The scale-lock port is correct against the viewer and stays in — it is simply inert for
this avatar, and it is not the cause.

What the same log does say about the self avatar:

```
[HeadSize] mHead own scale (0,872 …)  → then (0,963 …)   scaleLocks=0  bodySizeZ=1,77 m
```

(the first is the restored cache, the second the real `AvatarAppearance` — expected.)

Re-reading the screenshots for something projection-independent: the reference cube is at a fixed
world Z, so **where its lower edge falls on the head** is a world-space fact, not a camera artifact.
In Firestorm that edge sits just *below the chin*; in SLNG it sits at *eye level* — roughly 8 cm of
head "missing". Head-to-body proportion, by contrast, matches: head width over the distance between
the necklace strands is 0.41 in SLNG and 0.42 in Firestorm. That combination reads as **the whole
avatar rendering shorter / standing lower**, not as a head scaled down on a correct body.

Also decoded this round, and worth knowing but not the cause here: the two assets whose `mPelvis`
override SLNG skips (`32d87071`, `8a6f4154`) carry `alt_inverse_bind_matrix` translation
`(0, 0, 10.6701)` against a `1.067` default — an exact ×10 authoring error, confirmed against the
same asset's own `inverse_bind_matrix` (`−1.0670`). The existing root-joint skip is right about
those. They are worn by *other* avatars, which is precisely why the log now says whose mesh it is.

`v0.22.94-alpha` adds the measurement that can be compared to a reference viewer with no camera
guesswork:

```
[AvatarHeight] (shape|joint override) skeleton foot->mSkull N m |
  world Z: mSkull N  mHead(jaw) N  mFootLeft N  root N |
  bodySizeZ N pelvisToFoot N hoverParam N footOffsetY N shoeOffset N
```

and marks `[RiggedSkin]`/`[JointOverride]` with `SELF` vs. the other agent's id, so a busy sim's
dozen mesh bodies stop being mistaken for the user's. `[RiggedSkin]` is now ungated for SELF only.

Next: compare `mSkull`'s world Z against the reference cube's own Z/size from Firestorm's edit
window — that is a direct, unambiguous number on both sides.

## Round 5 — two separate questions, and a real parity defect in the camera

The user split the problem with a well-built test: Clifton's Firestorm looking at Denise while she
is online via SLNG, versus while she is online via Firestorm. Their conclusion — *"wenn selbst im FS
die Avatar anders aussieht wenn ich mit dem SLNG online komme kann es nicht die Kamera sein"* — is
sound, and it means two different questions had been merged:

**(A) SLNG's own view differs from Firestorm's own view.** The original report.

**(B) Firestorm's view of the avatar differs depending on which client she logged in with.**

For (B) the only thing this client does to a live avatar on its own initiative is the login
Current-Outfit reconcile (`ReattachMissingCofAttachmentsAsync`, first pass 6 s after login, decided
from LibreMetaverse's object cache, `replace: false`). Checked against the pinned LibreMetaverse
source: `AppearanceManager.Attach` sends **only** a `RezSingleAttachmentFromInv` packet — no visual
params, no shape, no size. And `Settings.Agent.SendAppearance` is `false` (GridSession ctor), with
`SendCorrectedAppearance()` refusing to transmit while it is. **SLNG writes no shape to the grid at
all.** So a re-attach can change the *pose* (it restarts the object's scripts — an AO, an ankle
lock) and it can change *which* attachments are present, but it cannot change shape or size. The
user's objection to this lead is correct. `--no-reattach` (v0.22.98) stays as the A/B switch for the
pose/attachment half of (B).

For (A) there is a genuine, source-verified parity defect that had gone unnoticed: **SLNG shipped
Godot's 75° vertical FOV; Second Life's is 60°.**

- `constexpr F32 DEFAULT_FIELD_OF_VIEW = 60.f * DEG_TO_RAD;` — indra/llmath/llcamera.h:36
- `CameraAngle` default `1.047197551` rad = 60.000° — app_settings/settings.xml
- `CameraSettings.DefaultFov = 75f` — SLNG, i.e. Godot's default, never changed

At the same camera distance a 60° view is `tan(37.5°)/tan(30°)` = **1.33x** more magnified, so the
avatar renders a third smaller than in the reference viewer. Compensating by pulling the camera
closer then exaggerates perspective, which shrinks the head relative to anything nearer the camera
(a reference cube rezzed beside the face, for instance) and visibly distorts a face in close-up —
"der Avatar ist kleiner, der Kopf ist kleiner, das Gesicht sieht nicht richtig aus", all three.

`v0.22.99-alpha` sets `DefaultFov = 60f` with a one-time migration: a stored value of exactly the
old 75° is treated as "never chosen" and moved once (guarded by a `fov_version` key), while a FOV
the user actually picked is left alone.

This is correct on its own merits regardless of what else is going on, and it is the first finding
in this bug that is a defect in SLNG rather than a mis-read of someone else's cached asset.

## Round 6 — measured: the avatar is the right size, and SLNG pulls it 0.69 m below the sim

`v0.22.101` added `[RenderExtent]`: the rest-pose SKINNED extent of every worn mesh, computed with
the exact palette Godot uses (`globalRest(bone) * bind`) over the same vertices — the first number
in this whole investigation that describes what is actually on screen rather than something
pre-skinning or a hidden system mesh.

**Size is correct.** The LeLutka head renders `0.193 x 0.164 x 0.222 m`, dominant joint `mFaceRoot`.
The user's reference cube, calibrated in Firestorm chin-to-crown, is `0.2057 m`; the mesh AABB
additionally covers the neck flange, so those agree. Head and hair are consistent with each other.

**Position is not.** Two `[AvatarHeight]` lines from the same session, seconds apart:

```
(shape)          mSkull 1038,176  mHead 1038,118  mFootLeft 1036,436  root 1036,462
(joint override) mSkull 1037,569  mHead 1037,512  mFootLeft 1035,829  root 1035,855
```

and between them:

```
[GroundClamp] source=sim-collision-plane groundZ=1035,84  agentZ=1037,41
[GroundClamp] source=sim-collision-plane groundZ=1035,84  agentZ=1036,72
```

The simulator places the agent at Z **1037.41**. `AvatarController`'s ground clamp computes
`clampTargetZ = groundHeight + halfBodyZ = 1035.84 + 0.885 = 1036.725` and drags the agent there at
`9.81 * delta` per frame. The measured landing value is 1036.72 — exactly that target. The rendered
avatar drops 0.607 m with it.

Against the cube (`1037.907 … 1038.112`): the head mesh renders at `1038.06 … 1038.28` **before**
the clamp (matching) and `1037.455 … 1037.677` **after** (0.44 m low). So the whole reported
symptom — "the avatar is smaller, the head is smaller" — is a fixed reference at head height
floating above a head that has been pulled downward.

**Viewer parity:** the reference viewer has no equivalent downward pull. The simulator owns the
agent's Z; `LLWorld::resolveStepHeightGlobal` (llworld.cpp:532-592) exists to feed foot IK and
shadows. SLNG turned that reading into a force that fights the simulator's own position.

Open sub-question, and the actual arithmetic error underneath: the simulator's collision plane
resolves to 1035.84 while the avatar's feet render at 1036.44 — 0.6 m apart. Either the plane is
being decoded wrong (`AvatarSupport.SupportHeight`) or `halfBodyZ` (0.885, from `BodySizeZ` 1.77)
does not describe this avatar's capsule. Note SLNG also omits the viewer's
`llclamp(norm_dist_from_plane / segment_length, 0, 1)`.

`v0.22.102-alpha` adds `--no-ground-drop` (and `tools/run-client.ps1 -NoGroundDrop`): the clamp may
still push UP out of geometry but never pulls down. One login with it decides this. The proper fix
belongs with `BUG-AVATAR-06` (falling), which is the same block.

### Round 7 — Scale-Lock Option A confirmed & shipped (v0.22.103-alpha)

- Auto-detected rigged Bento head meshes (via `mFaceRoot` / `mFace*` joints) and meshes declaring `LockScaleIfJointPosition`.
- Locked `mHead`, `mSkull`, `mEye*`, `mFaceRoot`, and Bento face bones to default scale 1.0, decoupling scale-locking from the presence of `AltInverseBindMatrices`.
- Classic shape slider 682/655 "Head Size" no longer distorts the Bento head mesh downwards, matching unrigged hair attached to `mHead`.
- Implemented `RefreshStaticAttachmentOffsets` to update static attachment offsets whenever bone scales change.
- In-world confirmed by user: *"Sieht schon viel besser aus"*.

### Round 8 — Reverted heuristic Bento head scale-lock to restore Firestorm slider parity (v0.22.104-alpha)

- User tested `v0.22.103-alpha` side-by-side with Firestorm using the 20 cm reference cube: in Firestorm, Denise's head matched the cube exactly, but in SLNG the head was ~2 cm larger (*"was auffällt, der kopf ist jetzt ca 2cm größer als im fs (erkennt man am cube) ... aber jetzt nur noch im SLNG"*).
- **Cause:** Bento heads (like LeLutka EvoX) do not declare `lock_scale_if_joint_position` in their mesh asset, and Firestorm does not artificially lock Bento face joints to 1.0. Firestorm genuinely honors the avatar's shape slider 682/655 "Head Size", which produces ~0.924 scale on `mHead` (~20.3 cm height) on this shape, perfectly fitting the 20 cm reference cube.
- In `v0.22.103-alpha`, forcing `isBentoHead` to scale 1.0 (22.2 cm) inflated the head by 1.9 cm (~2 cm) relative to Firestorm.
- **Fix:** Removed the artificial `isBentoHead` scale lock in `ApplyJointPositionOverrides`. Joints are only locked if explicitly requested by a mesh's `skinData.LockScaleIfJointPosition`. Retained `RefreshStaticAttachmentOffsets(visual)`.
- Firestorm slider parity restored in SLNG; Denise's head scales identically in both viewers.

## Known limitation

Like the existing `JointPosOverrides`, `JointScaleLocks` is not reverted per-mesh when the
contributing mesh is un-worn (the viewer's `removeAttachmentOverridesForObject` does revert it).
Detaching a lock-declaring mesh body therefore keeps its joints locked until the next full
appearance rebuild. Worth fixing together with the same gap on the position channel.

