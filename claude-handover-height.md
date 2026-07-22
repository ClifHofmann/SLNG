# Avatar Height Calculation Handover

**Target:** Claude
**Context:** SLNG Viewer Avatar Ground Sinking / Floating Issue

## Status Quo
We are trying to correctly place both the local and the remote avatar exactly on the physical ground. 
The current implementation in `AvatarRenderer.cs` uses a unified formula (ported from the Linden viewer `LLVOAvatar::updateRootPositionAndRotation`) to place the avatar root:

```csharp
float halfBodyZ = 0.5f * visual.BodySizeZ;
rootPos.Y -= (halfBodyZ + visual.FootOffsetY);
rootPos.Y += 0.025f; // shoe offset
```

- `simPos.Z` comes from the `Transform.Position.Z` (which for the local avatar is clamped to `groundHeight + halfBodyZ`, and for remote avatars comes directly from OpenSim).
- `BodySizeZ` is derived from `SlJointComposer.ComputeBodySize(_avatarSkeleton, distortions)`.
- `visual.FootOffsetY` is derived via `GetBoneRootRelativeY(visual.Skeleton, footBone)`, which measures the Godot bone rest Y position of `mFootLeft` (typically around `-0.198` due to the folded rest pose of the SL skeleton).

## The Problem
Mathematically, the above formula places the `mFootLeft` Godot bone *exactly* on the ground (`groundHeight + 0.025m`).
However, visually, the user reports that the avatars are **floating** (in earlier iterations, the remote avatar floated by ~0.8m, and most recently both seem to be floating).

## What I've tried & discovered:
1. **Changing FootOffsetY to use PelvisToFoot (straight-line distance):**
   - I tried setting `visual.FootOffsetY = -body.PelvisToFoot;` (which is typically around `-0.979`).
   - *Result:* This caused BOTH avatars to fly ~1 meter in the air. This implies that the mesh vertices for the feet are NOT actually rendered at `-0.979` relative to the root in Godot, but rather somewhere closer to the folded bone rest position (`-0.198`).
2. **Investigating JointPosOverrides (Fitted Mesh):**
   - I suspected that `visual.JointPosOverrides` (from fitted mesh attachments like boots) were leaking into `ComputeBodySize`.
   - While they *were* being passed to `ComputeBodySize`, I realized that `SlJointComposer` mostly ignores position overrides for legs (it relies on scale), and removing it didn't change `BodySizeZ = 2.020` in the user's logs. The size `2.020` comes entirely from the user's `distortions` (shape sliders).
3. **The Bind Pose vs. Animation Pose Mystery:**
   - Godot's `Skeleton3D` uses `SkinBindMatrix` which we compute as the inverse of the SL `avatar_skeleton.xml` rest pose.
   - The SL rest pose has the legs severely folded (foot at `-0.198`).
   - If no animation plays, the bone transform is the rest pose, so `BoneTransform * SkinBindMatrix` cancels out, drawing the mesh at its original `.llm` position (which is `-0.979`).
   - **Contradiction:** If the mesh is drawn at `-0.979`, but `FootOffsetY` is `-0.198`, the avatar *should* sink by ~0.78m. But the user reports them *floating*!
   - This strongly suggests either:
     a) My assumption about where the `.llm` vertices actually are is wrong.
     b) The active animations (`stand.anim` or the user's AO) rotate the bones in a way that lifts the mesh up significantly.
     c) Godot is scaling the avatar root/mesh differently than expected.

## Next Steps for Claude
1. Re-evaluate the connection between Godot's visual mesh vertex positions and the mathematical `FootOffsetY`. Why does mathematically placing the Godot `mFootLeft` bone on the ground result in the visual mesh floating?
2. Check if the SL viewer's `mPelvisToFoot` measurement (which is the straight-line vertical distance) needs to be mapped differently to Godot's coordinate space.
3. Investigate if the local `clampTargetZ` in `AvatarController.cs` is perfectly synchronized with what OpenSim considers the ground collision cylinder.
4. If `FootOffsetY` should indeed be `-body.PelvisToFoot`, find out what is pushing the mesh 1 meter up (perhaps a double-application of a position offset or a missing scale inversion).

---

## Claude's read (picking this up live, current uncommitted diff + user screenshot in hand)

Live test just now (screenshot, current uncommitted `AvatarRenderer.cs`/`Boot.cs` state, unified formula for both local+remote): **local avatar (self) now stands correctly, matching Firestorm. The remote avatar ("Reamon Bullmer") floats, with one foot visibly lifted/mid-air** — not just "hovering a fixed amount," it looks like a mid-stride walking pose, foot raised well above the platform while the other stays down.

### Why "local now looks right" is weaker evidence than it seems

`AvatarController.cs:436` clamps the LOCAL avatar's `transform.Position.Z` to `groundHeight + halfBodyZ` (where `halfBodyZ = 0.5 * BodySizeZ`, code we wrote ourselves). `AvatarRenderer` then subtracts that same `halfBodyZ` (+ `FootOffsetY`, + the 0.025 shoe fudge) back out at render time. **Both sides of that equation are under our own control** — so the local avatar rendering "correctly" doesn't independently confirm the formula is right, only that our own clamp and our own render-offset agree with each other (the exact same tautology trap documented earlier in this file's git history / the other now-merged branch, `3c57312`). The only genuine test of correctness is the REMOTE avatar, whose `transform.Position.Z` comes from OpenSim — external ground truth we don't control — and that's the one that's currently wrong.

### Two live hypotheses, both worth checking, possibly both true at once

1. **Remote avatar network Z might not be capsule-center at all.** Before the last "unify" commit, there was a split formula specifically because "OpenSim sends ground/feet level for remote agents" (per `e5a0640`'s own message, later reverted again). This needs to stop being asserted from vibes and get checked against actual OpenSim/LibreMetaverse source: does `TerseObjectUpdate`/`ObjectUpdate` deliver the SAME position semantic (simulator physics collision-cylinder center) for every avatar regardless of who's "self," or does OpenSim's implementation genuinely diverge from that for other agents' avatars specifically? This is a protocol question, not a rendering-math question — don't keep guessing at it from the render side.

2. **`FootOffsetY` is measured from the REST pose, not the live/animated pose** (per this doc's own description: "measures the Godot bone rest Y position of `mFootLeft`"). A static rest-pose offset is only valid while the character is actually standing in a neutral rest-like pose. The screenshot shows Reamon apparently mid-walk-cycle (one foot lifted) — if that's a real animation currently playing, no fixed `FootOffsetY` constant can be right for that frame; the actual foot position swings well away from rest pose during a step. **This branch (`fix/remote-avatar-animations`) was originally created for remote-avatar animation dispatch/entity-lookup issues** (`6bcf31e`, `c5d2d4d`) — worth checking whether Reamon is stuck in / incorrectly playing a walk animation rather than idling, as a possible root cause of what LOOKS like a pure height bug but is actually (at least partly) an animation-state bug wearing a height-bug costume. If so, the fix isn't a better height formula at all — it's fixing why the remote avatar isn't idling.

### Recommendation

Don't keep iterating on the height formula in isolation. First determine: is Reamon actually idling right now, or playing/stuck-in a non-idle animation? If the latter, chase that (this branch's original purpose) before touching the height math further — a "correct" static offset can never look right against a moving pose. If Reamon genuinely is idling and still floats, then it's hypothesis 1 (protocol semantics) and needs verification against real OpenSim/LibreMetaverse source, not another guessed constant.

---

## Round 2 (Claude, same session, after a close-up screenshot correction)

The coordinator relayed a close-up screenshot: the floating foot is a **static, fixed** offset — visible gap + shadow under one shoe, no stride/swing. This rules out "stuck in a walk cycle" (hypothesis 2a) as the dominant explanation and points squarely at hypothesis 2b: the remote avatar's height inputs themselves are wrong, most likely because its real shape never arrived.

### What I checked and ruled out

- **`RecomputeFootOffset`/`GetBoneRootRelativeY` are NOT stale/rest-vs-posed bugs.** Both call sites (`UpdateVisual`'s shape-change block and `ApplyJointPositionOverrides`) call `skeleton.ResetBonePoses()` immediately before measuring, so the manual `GetBoneRest` parent-chain walk is mathematically identical to `GetBoneGlobalPose` at that exact instant (pose == rest right then). This measurement is a **shape-derived constant, recomputed only on shape change** — which is also what the real viewer does (`LLVOAvatar::updateRootPositionAndRotation` uses `mBodySize`/`mPelvisToFoot`, both static per-shape values, not a per-frame animated-bone read). Continuously re-measuring the live/posed foot bone every frame would actually be a parity *regression* (real idle stances are rarely perfectly symmetric between both feet — jumps/sits/flying would also fight a per-frame "nail this foot to the ground" rule). So I did **not** change this to live per-frame tracking, despite the original task brief suggesting it — the earlier "GetBoneGlobalPose read after pose applied" description in this doc is accurate for what it does at measurement time, just not literally implemented via that named API call.

### What I found (the actual bug)

`WorldSimulation.ApplyAvatarAppearance` (`src/SLNG.Core/WorldSimulation.cs`) matched the target entity by **exact** `AvatarComponent.AgentId == e.AgentId`. `ApplyAvatarAnimation` had already been patched (commit `6bcf31e`) with a fallback: if no exact match, find a non-local avatar entity whose `AgentId` is still `Guid.Empty` (i.e. not yet resolved) and heal it to the incoming `AgentId`. **`ApplyAvatarAppearance` never got the same fallback.**

Why this matters: a remote avatar's ECS entity can be created from a bare `TerseObjectUpdate` (LocalId only) before LibreMetaverse's `ObjectsAvatars` cache has resolved the full `AgentID` — `GridSession`'s own fix for this exact race is right next to the animation fallback in the same commit (`6bcf31e`, `GridSession.cs`). If the entity's `AvatarComponent.AgentId` is still `Guid.Empty` when the (separate, one-shot) `AvatarAppearance` message arrives with the avatar's real `VisualParams`, the exact-match lookup fails, and — unlike `ObjectUpdate`, which resends every tick and self-heals `AgentId` eventually — `AvatarAppearance` doesn't get resent. The avatar's `VisualParams` is silently dropped **for the rest of the session**, leaving `AvatarVisual.BodySizeZ`/`FootOffsetY` frozen at their generic field defaults (`1.90f` / `0f`) instead of that avatar's real shape-derived values. That's a wrong, but *static and avatar-specific*, height offset — exactly matching "one avatar floats by a fixed, non-animated amount while the local avatar (whose own AvatarComponent was populated at login, no race) looks fine."

(This doesn't by itself explain why only ONE foot floats rather than the whole body uniformly — but a natural standing/idle pose is rarely perfectly symmetric between both feet at the rest-measured reference point, so a root-height error combined with an ordinary asymmetric idle stance can easily look like "one foot floats, the other looks planted" without needing a separate bug.)

### Fix applied

- Factored the fallback lookup out of `ApplyAvatarAnimation` into a shared `WorldSimulation.FindAvatarEntityByAgentId`, and use it in **both** `ApplyAvatarAppearance` and `ApplyAvatarAnimation` (`src/SLNG.Core/WorldSimulation.cs`).
- Added a regression test, `AvatarAppearanceEvent_ResolvesEntity_WhenAgentIdWasStillEmpty` (`tests/SLNG.Core.Tests/WorldSimulationTests.cs`), which creates an entity with `AgentId == Guid.Empty` (simulating the race) then raises an `AvatarAppearanceEvent` with a real `AgentId` — confirmed this **fails without the fix** (`avatar.VisualParams` stays `null`) and passes with it.
- Extended `AvatarRenderer`'s `[HeightDebug]` log with `hasShape={avatar.VisualParams != null}` so a future live session can immediately see whether a floating remote avatar still has `hasShape=false` (meaning this exact bug, or a variant of it, is recurring) versus `hasShape=true` with a wrong value (meaning look elsewhere).

### Still unverified live

I don't have GUI automation for the native Godot window in this environment (only browser automation), so I could not click through login and get a screenshot with both avatars in frame myself this round. **Next live session should**: rebuild clean (`tools/run-client.ps1` or the manual `dotnet build` + `godot --path app` sequence), get Reamon in view, and check the console for `[HeightDebug] entity=... (isLocal=False) hasShape=...`. If `hasShape=true` and the float persists, this fix didn't address the real cause and hypothesis 1 (remote-avatar network Z reference frame vs OpenSim protocol semantics) needs to be checked next — don't guess another constant, verify against real OpenSim/LibreMetaverse avatar update source.

---

## Round 3 (coordinator tested live, pointing back at hypothesis 1)

The AgentId-fallback fix from round 2 worked exactly as intended — the coordinator confirmed `hasShape=True` for Reamon now. **This is a real, standalone fix and stays regardless of what follows** (a remote avatar's appearance data was genuinely being dropped by the AgentId race; that's fixed).

But the float didn't go away: `simPos.Z=26.122`, `rootPos.Y=25.288`, `BodySizeZ=1.707`, `FootOffsetY=0.005`. Comparing against the local avatar's own `groundHeight=25.0035` logged earlier at the same platform: `25.288 - 25.0035 ≈ 0.28m` — Reamon renders ~28cm above ground. Real, consistent, non-trivial, and now clearly NOT (or not only) a missing-appearance-data problem, since the shape data is confirmed present.

This is hypothesis 1 from the very first version of this doc: **nothing in the code ever queried ground height at a REMOTE avatar's own X/Y to check whether `simPos.Z` means the same thing (collision-cylinder-center) as the local avatar's `transform.Position.Z`** — which, unlike Reamon's, is OUR OWN construction (built by `AvatarController`'s `clampTargetZ = groundHeight + halfBodyZ`, using a raycast at the LOCAL avatar's position only). The formula in `AvatarRenderer.UpdateVisual` was applying that same "capsule-center" assumption to Reamon's raw network Z with zero verification.

### What I added (diagnostic only, NOT wired into the render path)

Two new logs in `app/scripts/AvatarRenderer.cs`, both requested by the coordinator before touching the formula again:

1. **`[RemoteGroundDiag]`** — for every non-local avatar, throttled to ~1/sec per entity (new `_timeSinceRemoteGroundLog` dictionary), casts the SAME kind of ground raycast `AvatarController` already uses for the local avatar (Layer-1-only, from 5m above down to 100m below), but at the REMOTE avatar's own X/Y. Logs `simPos.Z`, `remoteGroundHeight` (the raycast hit's Godot Y — which is directly comparable to SL Z; `RenderConfig.ToGodot`/`FromGodot` only shift X/Y for the floating origin, height passes through unchanged), their difference, and `halfBodyZ` for comparison. This is the number that settles hypothesis 1: if `simPos.Z - remoteGroundHeight` comes out close to `halfBodyZ`, `simPos.Z` really is capsule-center and something else in how the formula gets applied is off by ~0.28m; if it comes out as something else entirely (e.g. close to 0, or close to a full body height), OpenSim genuinely sends something structurally different for remote agents.
2. **`[ShapeDataDiag]`** — logged once per shape-apply (same `needsApply` gate `ApplyShape` already uses), reports `visualParamsLength`, `nonZeroBytes` (how much of the raw VisualParams byte array is non-default), and `boneModsTotal`/`nonTrivialBoneMods` (how many of the computed skeletal distortions actually came out non-trivial vs. zero). This answers the coordinator's sanity check directly: `BodySizeZ=1.707`/`FootOffsetY=0.005` are what an undistorted skeleton measures, so this distinguishes "Reamon's shape genuinely is close to default" (VisualParams populated, distortions correctly near-zero) from "hasShape=True but the array is still effectively empty" (a lingering variant of the same class of bug) (`nonZeroBytes`/`nonTrivialBoneMods` near 0 despite a full-length array would indicate the latter).

Also completed a pre-existing half-written diagnostic in the same method: the local-avatar `[RootApply]` block computed a `footInfo` string every throttled tick but never printed it (dead code) — added the missing `GD.Print`.

### Next step for whoever picks this up

Rebuild clean, get Reamon in view again, and read the `[RemoteGroundDiag]` and `[ShapeDataDiag]` lines. Per the coordinator's framing: get the real `simPos.Z - remoteGroundHeight` number FIRST, then decide whether the fix is "the formula is right, something else contributes the missing ~0.28m" (keep digging in the render-side math) or "OpenSim really does send a different Z reference for remote avatars" (needs verification against real OpenSim/LibreMetaverse avatar-update source, per this project's house rule — don't guess another constant).
