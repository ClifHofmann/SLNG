# Handover — Avatar ground-height / scale investigation (2026-07-22)

- **Branch:** `fix/avatar-pelvis-offset` (based on `main`, currently 8 commits ahead, tip `1a6c260` at time of writing — check `git log main..fix/avatar-pelvis-offset` for the current tip, since a background agent may still be mid-edit, see "Live/uncommitted state" below).
- **Not** the same as `feature/M4-7-base-mesh-hiding` (a separate, postponed branch — see bottom of this doc).
- **Owner so far:** claude (this session). Handing off — pick up here rather than re-discovering from scratch.

## How this started

User did a side-by-side screenshot comparison of their avatar in SLNG vs. Firestorm (same account, same outfit — a dark coat + boots) and noticed the SLNG avatar stood visibly lower / sunk into the ground compared to Firestorm. That one visual bug report fanned out into several distinct, now mostly-fixed issues, plus one still-open root cause. Read this in order — each finding corrected an earlier wrong theory.

## Fixed and verified (safe to build on top of)

1. **`3858a38`** — Legacy "fitted mesh" pelvis-fixup (`skinData.PelvisOffset`) was decoded but only logged, never applied. Verified against real viewer source (`scratch/slviewer/indra/llappearance/llavatarappearance.cpp` `addPelvisFixup`, `llvoavatar.cpp` `getRenderPosition`). Now applied via `AvatarVisual.PelvisFixups`, keyed by mesh id, cleared on unwear.
2. **`a877f58`** — Ported the real viewer's general per-frame root correction (`root_pos.z -= 0.5*mBodySize.z - mPelvisToFoot`, from `LLVOAvatar::updateRootPositionAndRotation` / `LLAvatarAppearance::computeBodySize`) as `SlJointComposer.ComputeBodySize` (`SLNG.Core`, engine-agnostic) + `AvatarRenderer.RecomputeRootOffset`. **Superseded in practice — see "Open problem" below: this mechanism turned out to be a no-op for the local avatar once `3756119` was added.** Keep the `ComputeBodySize` math/tests, but the consumption side needs the rework described below.
3. **`f049e9e`** — Coat + boots both carry a **malformed** `AltInverseBindMatrices` override on `mPelvis` (the skeleton root — no parent), decoded translation `(0,0,10.67)` vs. rest `(0,0,1.067)`: exactly 10x too large, wrong sign. Verified byte-for-byte against `scratch/slviewer` that our decode is correct and this is bad **asset data**, not a decode bug — a root/pelvis joint can't have a meaningful "local position override" in the first place (the sanctioned channel for that is `pelvis_offset`, correctly 0 on this asset). Fix: `ApplyJointPositionOverrides` now skips any joint with no `ParentName`. Regression test added (`SlJointComposerTests.ComputeBodySize_ignores_mPelvis_position_override_entirely`).
4. **`1736f42`** — Unrelated bug, found while chasing the above: a 50%-transparent reference cube (Object > Features > Transparency slider) rendered fully opaque in SLNG. Root cause: `ObjectRenderer.cs`'s re-apply gate only compared `TextureId`/`RenderMaterialId`, never `ColorTint`/per-face data, so live-editing the Transparency slider never re-triggered `ApplyFaceMaterialsAsync`. Fixed by tracking `LoadedColorTint`/`LoadedFaces` in `VisualState`. **Confirmed working live by the user.**
5. **`b2aad47`** — Also unrelated, found via a separate bug report (open seam on a large flat platform prim, visible in SLNG, not Firestorm). Root cause: the vendored `LibreMetaverse.Rendering.MeshFoundry` NuGet package never generates the **bottom** end-cap for any straight-extruded (`PathCurve` Line/Flexible) primtype — box, cylinder, prism/wedge — only the top. Invisible at ~1m scale, a visible gap once scaled up into a platform. Fixed at the `SLNG.Assets` boundary (`PrimMeshService.Generate` → `RepairMissingEndCap`, reconstructs the missing cap from the side-wall vertices, not by mirroring — works for tapered/sheared shapes too). Test added (`PrimMeshServiceTests.Generate_box_closes_both_top_and_bottom_caps`). **Fully done, no known follow-up.**

## Status update — a fix landed after this doc was first written (UNTESTED live, verify first)

**`3c57312`** (after the rest of this section was written — read the rest of this section for full context first, then come back here): replaced the `RootOffsetZ` formula-based mechanism with a direct measurement, per the "Proposed fix direction" below. `AvatarVisual.FootOffsetY` is now measured live via `Skeleton3D.GetBoneGlobalPose("mFootLeft").Origin.Y` right after `ApplyShape`/`ApplyJointPositionOverrides` reset bone poses, and `UpdateVisual` applies `rootPos.Y -= visual.FootOffsetY` exactly once, uniformly for every avatar (local and remote — no entity-specific gating). `AvatarController`'s ground-clamp was reverted to the simple, original `clampTargetZ = groundHeight` (no longer needs to know about the render-side correction at all — the no-op-by-cancellation bug is gone by construction, not by careful bookkeeping). `SlJointComposer.ComputeBodySize` and its tests were left in place (still valid, just no longer used for this purpose).

Build/tests are clean and the build markers are confirmed present in the DLL, but **this has not yet been verified live** (rebuild via `tools/run-client.ps1`, check `[RootApply]`'s `mFootLeft` SL-Z against `groundHeight` — NOT `Root.Y`, that check is meaningless per the tautology explained below — and check whether the remote-avatar floating case is also resolved). Do that verification first before assuming this is actually done.

## Original open-problem writeup (context for the above fix — read this to understand WHY)

This is the thread that led to the fix above. Read it fully before trusting the fix blindly — it took 4 rounds to get here, and the same mistake (checking `Root.Y` instead of `mFootLeft`) is easy to repeat.

### The key finding: `RootOffsetZ` is currently a mathematical no-op for the local avatar

`3756119` tried to fix the sink by making `AvatarController`'s ground-collision clamp target `groundHeight - RootOffsetZ` instead of `groundHeight`, expecting `AvatarRenderer.UpdateVisual`'s `rootPos.Y += RootOffsetZ` to add it back and land exactly on `groundHeight`.

**This cancels out algebraically no matter what `RootOffsetZ` is**: `Root.Y = (groundHeight - RootOffsetZ) + RootOffsetZ = groundHeight`, always. Confirmed live via added diagnostics (`[RootApply]` in `AvatarRenderer.cs`, `[GroundClamp]` in `AvatarController.cs`, both ~1/sec, both carry `entity=`) — `Root.GlobalTransform.Origin.Y` matched `groundHeight` exactly in every test, which felt like validation but was actually a tautology. **This mechanism has had zero effect on the avatar's actual rendered position since it was introduced.** Don't be fooled by "the numbers match perfectly" again — check the FOOT bone, not `Root`.

### The real number that matters

Added `mFootLeft.GlobalTransform.Origin.Y` to the `[RootApply]` log line. Live measurement for the test avatar (real shape + real worn-mesh joint overrides, not the simplified default skeleton used in earlier unit-test-only analysis):

```
groundHeight=25.0035  (confirmed: raycast correctly hits the platform's own StaticBody collider, not terrain fallback)
Root.GlobalTransform.Origin.Y=25.0035        (matches groundHeight — but see above, this is a tautology)
mFootLeft.GlobalTransform.Origin.Y=24.812    (the ACTUAL foot bone — 0.1915m BELOW ground)
```

0.1915m matches the visually observed sink almost exactly (user screenshot: boots buried roughly ankle/lower-shaft deep, not knee-deep or hip-deep — rules out an early wrong theory that `Root` might actually be the pelvis with the whole leg chain dangling ~1m below it; it isn't, `Root` ≈ foot-level in the *rest* pose, but shape distortion + these two worn items' joint overrides pull the actual foot bone ~19cm further down than that).

### Proposed fix direction (this is what `3c57312` above implemented — kept here as the rationale)

Abandon the indirect `mBodySize`/`mPelvisToFoot` formula for this purpose (the real viewer uses it for its own historical/architectural reasons — no cheap access to live bone global poses at that point in its pipeline). SLNG *does* have cheap access via `Skeleton3D.GetBoneGlobalPose`, so use it directly:

1. Whenever shape/joint-overrides change (same trigger points already wired for `RecomputeRootOffset`), measure `Skeleton3D.GetBoneGlobalPose(footBoneIdx).Origin.Y` — this is already relative to the Skeleton's own local origin (i.e. relative to `Root`, independent of `Root`'s current world position).
2. Apply that measured "foot is N meters below Root" gap **exactly once** in the pipeline — either bake it into `AvatarController`'s `clampTargetZ` computation directly (and delete the now-provably-inert add-back in `AvatarRenderer`), or keep the add-back in `AvatarRenderer` and make the ground-clamp target `groundHeight + gap` instead of `groundHeight - RootOffsetZ`. Pick ONE application site, not both — that's exactly the bug that was just found.
3. Verify by checking `mFootLeft.GlobalTransform.Origin.Y` against `groundHeight` (NOT `Root.Y` against `groundHeight` — that check is meaningless, see above).
4. Firestorm reference (user-provided screenshot): the real viewer doesn't put feet exactly flush with the ground — there's a small (~2-5cm) hover gap with a soft contact shadow. Treat "feet at `groundHeight`" as the near-term correctness target; matching that small hover gap exactly is optional follow-up polish, not blocking.

### Bonus finding: remote avatars float too high — likely addressed by `3c57312`, verify

User screenshot: a second, remote avatar ("Reamon Bullmer") renders floating roughly a body-height or more above the ground in SLNG, while standing normally in Firestorm. Explanation confirmed: `AvatarController`'s ground-clamp only ever ran for the local agent (`IsLocalAgent == true`); remote avatars got the formula-based addition with **nothing** to compensate it, so for them it was NOT a no-op — a pure, uncancelled lift. `3c57312`'s fix applies `FootOffsetY` uniformly per-entity (no local/remote distinction), which should fix this by construction — but this specific case (a second, differently-shaped avatar) has not been re-tested since the fix landed. Check it.

### Live/uncommitted state at handoff time

Resolved — the background agent that was mid-edit when this doc was first written finished its round and committed as `3c57312`. `git status` should be clean on `fix/avatar-pelvis-offset` as of that commit; double check before assuming so, in case anything changed since.

### How to verify (the technique that actually worked this session)

The user's own testing method was more effective than pure code/log analysis: rez a plain box prim, scale it up large as a fixed visual ruler, stand next to it, and compare screenshots between SLNG and Firestorm at matching camera angle/distance. Recommend the same approach for verifying the eventual fix — plus checking the `[RootApply]`/`[GroundClamp]` log lines in `%APPDATA%\Godot\app_userdata\SLNG\logs\godot.log` for the actual numbers rather than trusting a visual impression alone.

**Stale-assembly trap** (see project memory `godot-stale-assembly`): after any `app/`-side C# change, you MUST do a clean rebuild (`tools/run-client.ps1`, which deletes `app/.godot/mono` first) and confirm the new `BuildMarker` string appears in the log before trusting any in-game test — a hot-reload or incremental build can silently keep running old code. If the mono-cache delete fails with "access denied" on `libSkiaSharp.dll`, a Godot process is still holding the file locked — close all SLNG client windows (check `tasklist | grep -i godot`, there have been many orphaned instances this session) and retry.

## Separate, unrelated, deliberately not touched this session

- **`feature/M4-7-base-mesh-hiding`** — a *different* roadmap task (Base-mesh Hiding Under Worn Mesh, M4-7). Claimed (`Owner: claude` set in `docs/ROADMAP.md`) then explicitly postponed by the user in favor of this ground-height investigation. Branch has exactly one commit (the ownership claim), otherwise untouched — free to pick up or reassign.
- A pre-existing, still-unfixed bug was noted but NOT fixed (out of scope for this session): `AvatarVisual.JointPosOverrides` (bone position overrides from a worn mesh's `AltInverseBindMatrices`) is never removed when that mesh is un-worn — same class of bug as the pelvis-fixup leak that WAS fixed, but for per-joint overrides instead of the single pelvis float. Needs its own fix (per-mesh bone tracking, not just a per-mesh-id float like the pelvis fixup).
