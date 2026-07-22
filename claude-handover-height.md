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

---

## Round 4 (throttle bug in the round-3 diagnostic itself)

Live test of round 3's diagnostics: `[ShapeDataDiag]` confirmed Reamon's shape data is genuinely rich (`visualParamsLength=253 nonZeroBytes=182 boneModsTotal=127 nonTrivialBoneMods=120`) — rules out "hasShape=True but effectively empty" as a lingering concern; the round-2 AgentId fix delivered real, non-degenerate shape data.

But `[RemoteGroundDiag]` never printed once across several minutes with Reamon in view. Root cause (found by the coordinator reading the code, not by live testing): the per-entity throttle in `app/scripts/AvatarRenderer.cs` only ever wrote to `_timeSinceRemoteGroundLog[entity.Id]` on the FIRING branch (resetting it to 0). The non-firing path never persisted the incremented local `tRemote` back into the dictionary, so every call read back ~0 via `TryGetValue`, added one frame's delta (~0.016s), failed the `>1.0` check, and threw the incremented value away — `tRemote` could never accumulate across frames, so the log was effectively dead code (would only ever fire after a >1s single-frame stutter). Contrast with the working local-only `_timeSinceRootPosLog` throttle right above it in the same file: that one is a plain field mutated in place every call, so it doesn't have this problem — the dictionary-keyed version needed the equivalent explicit write-back in the else branch, which `TryGetValue` doesn't do for you.

**Fix**: added an `else { _timeSinceRemoteGroundLog[entity.Id] = tRemote; }` branch so the incremented value persists across frames when the 1-second threshold hasn't been hit yet. `[ShapeDataDiag]` was unaffected (it isn't throttled by this dictionary — it fires on `needsApply`, which is why it already worked).

Rebuilt clean (`dotnet build app/SLNG.App.csproj` after clearing `app/.godot/mono`), `dotnet build SLNG.sln` + `dotnet test SLNG.sln` both clean (76/76 tests pass). **Still could not trigger a live run myself** (no GUI automation for the native Godot window in this environment) — the actual `[RemoteGroundDiag]` numbers (the real `simPos.Z - remoteGroundHeight` vs. `halfBodyZ` comparison that settles hypothesis 1) still need a live session to capture. That is the concrete next step: rebuild, get a remote avatar in view for >1s, and read the `[RemoteGroundDiag]` line this time.

Note for cross-referencing: `entity.Id` in these logs is an internal ECS id (not the real SL/OpenSim AgentId/AvatarID) — for this investigation's live test, Reamon's real UUID is `bed44511-6e29-4a04-934a-f9834737b68d`, distinct from whatever `entity.Id` happens to be that session (was `f2b0bfbb-9542-4e24-8631-c85772f01caa` in the round-3 test). Don't conflate the two if checking anything against a raw network capture or OpenSim-side data.

---

## Round 5 (throttle mechanism itself was the wrong tool; found a real second data-corruption bug)

Live test of round 4's write-back fix: `[RemoteGroundDiag]` still only fired ONCE in 35+ seconds with Reamon standing still (hundreds of `[HeightDebug]` lines, one `[RemoteGroundDiag]`). Root cause (found by the coordinator reading the code): `UpdateVisual` is **not** a per-frame `_Process` callback — it fires per-entity from ECS component-update events, which for an idle remote avatar arrive in sparse bursts seconds apart. `GetProcessDeltaTime()` returns the delta since the last RENDERED FRAME, not since the last time this function ran for this entity — so a frame-delta accumulator only advances by ~one frame's worth PER ACTUAL CALL, and with calls arriving in rare bursts it could take dozens of real seconds to cross a 1.0s threshold even though wall-clock time had clearly passed. Two rounds (3 and 4) spent fixing accumulator mechanics that were never going to work for this call pattern.

**Fix**: dropped the throttle entirely, per the coordinator's explicit preference ("simplest of all, just drop the throttle... we just need a handful of real samples, not a sustained 1/sec stream"). `[RemoteGroundDiag]` now prints on every `UpdateVisual` call for a non-local avatar, unconditionally. Removed the now-unused `_timeSinceRemoteGroundLog` dictionary field. This is diagnostic-only and already known to be low-frequency for an idle remote avatar (per the very sparse-burst pattern that caused this whole detour), so log spam isn't a real concern.

### Second bug found while investigating the coordinator's BodySizeZ-regression report

The coordinator separately noticed Reamon's `BodySizeZ`/`FootOffsetY` changed mid-session with no shape edit on his end (`1.991`/`-0.198` → `1.707`/`0.005`, the latter looking exactly like an undistorted default skeleton), while `simPos.Z` stayed constant — suggesting a later `AvatarAppearance` event overwrote the good data the round-2 AgentId fix had delivered, i.e. possibly the same bug class showing up as "good data gets clobbered" instead of "never arrives."

Traced this to `WorldSimulation.ApplyAvatarUpdate` (`src/SLNG.Core/WorldSimulation.cs`): the existing-entity branch unconditionally overwrote `avatar.AgentId = e.AgentId` (and `FirstName`/`LastName`) on **every** `AvatarUpdateEvent`, even when `e.AgentId` was `Guid.Empty` (e.g. a bare `TerseObjectUpdate` whose `Prim` isn't an `Avatar`, or whose `LocalID` missed LibreMetaverse's `ObjectsAvatars` cache that round — the same resolution GridSession's own fix, commit `6bcf31e`, only does on a best-effort basis, not guaranteed on every packet). So a previously-*resolved* `AgentId` could silently regress back to `Guid.Empty` mid-session from a later, less-informative update for the SAME entity. Once that happens, the entity re-qualifies for `FindAvatarEntityByAgentId`'s "any non-local avatar with `AgentId == Guid.Empty`" fallback branch — meaning a **later, unrelated** avatar's `AvatarAppearance`/`AvatarAnimation` event (for a different agent, itself still unresolved) could get matched via `FirstOrDefault()` onto Reamon's already-resolved entity and overwrite his real shape data with someone else's (or a fresh, still-default one). This exactly fits the observed symptom: real values regressing to what looks like an undistorted default, with no action from Reamon himself.

**Fix**: guarded all three fields in `ApplyAvatarUpdate`'s existing-entity branch so a later update can only ADD information, never blank out what's already known — `if (e.AgentId != Guid.Empty) avatar.AgentId = e.AgentId;` and the equivalent `IsNullOrEmpty` guards for `FirstName`/`LastName`. Position/rotation still update unconditionally every time (that's supposed to change every update).

Added regression test `AvatarUpdateEvent_DoesNotRegressAlreadyResolvedAgentId` (`tests/SLNG.Core.Tests/WorldSimulationTests.cs`) — confirmed failing before this fix (`AgentId` reverted to `Guid.Empty` as expected), passing after.

**Known residual limitation, not fixed this round**: even with this fix, if TWO avatars are simultaneously unresolved (`AgentId == Guid.Empty`) at the same instant — e.g. both rez in the same frame before either's real AgentID resolves — `FindAvatarEntityByAgentId`'s fallback still can't tell them apart (`AvatarAppearance`/`AvatarAnimation` events carry only an `AgentId`, no `LocalId`/region, so there's no way to disambiguate which of several equally-unresolved candidates a given event is actually about) and `FirstOrDefault()` would pick one arbitrarily. This is a narrower window than the bug just fixed (requires simultaneity, not just "eventually regresses"), and closing it fully would need plumbing a `LocalId`/region hint through `AvatarAppearanceEvent`/`AvatarAnimationEvent` from `GridSession` — a bigger change than this round's scope. Flagging for whoever picks this up next if cross-avatar data contamination is still observed after this fix.

### Still the actual next step

Rebuild clean, get Reamon in view, and read the now-unthrottled `[RemoteGroundDiag]` lines — should print on every `UpdateVisual` call for him now, giving the real `simPos.Z - remoteGroundHeight` vs. `halfBodyZ` comparison needed to settle hypothesis 1. Also worth a look once there are more samples: the one sample captured in round 4 said "ray missed ground" at `simPos.Z=26.122` — is that a genuine collision gap near where remote avatars land on the platform, or just bad luck on that one frame (position mid-transition, platform collider not yet built, etc.)? Can't tell from one sample; the round-5 fix should now produce enough of them to see a pattern.
