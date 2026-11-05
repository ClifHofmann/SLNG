# [BUG-AVATAR-23] Avatars stay invisible after the cap parks them mid-build

- **Feature ID:** `BUG-AVATAR-23`
- **Track:** `avatar` / `render`
- **Status:** 🧪 Review
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md)

## Overview & Goal

When another avatar arrives and is partially loaded, promotion and cap limit adjustments can swap it out to "reduced/parked" state mid-build.
When later promoted back to "full", the unpark logic assumes that worn items already having a `BoneAttachment3D` anchor in `_attachmentNodes` are complete, skipping mesh/rig requests. However:
1. `UpdateAttachment` creates `BoneAttachment3D`, records it in `_attachmentNodes`, and sets `HasLoadedOutfit = true` before the rigged mesh is committed (`_pendingRigs`).
2. `SetAvatarReduced` purges `_pendingRigs`, `_preparedRigs`, `_rigsPreparing`, and `_attachNewest`.
3. On promotion from parked, only items where `!IsRiggedOnWearer(id) && !_attachmentNodes.ContainsKey(id)` are re-requested. The anchor node exists, so unfinished rigged or rigid meshes are never requested again.
4. `UpdateAttachment` could also skip processing if `_attachmentMeshIds` already cached the asset ID.
5. In addition, `CommitPreparedRig` drops items if `req.Visual.IsReduced`, and mesh fetch returns while reduced are ignored.
6. The wearer's alpha layers hide the system body, leaving only the name tag visible permanently.

Goal:
Ensure that upon unparking, any worn item lacking a finished visual (rigged instance / merged bucket or non-rigged built mesh under its anchor) is re-requested, while already-finished visuals remain instant without rebuilds. Also log `[AvatarLimit] <id> unpark re-requested <n> worn items` under `--diag`.
Also fix the `[WornRemoved]` summary line reporting a mismatched wearer ID compared to per-item lines.

## Acceptance Criteria

- [x] Core unpark/item-completion decision logic placed in `SLNG.Core` with xUnit tests.
- [x] On promotion from parked (`IsParked`), worn items without a finished visual are re-requested.
- [x] Finished items (`IsRiggedOnWearer` or anchor with mesh child) are not rebuilt/re-requested on unpark (preserving BUG-PERF-16 instant unpark).
- [x] Re-requesting is not blocked by cached `_attachmentMeshIds`.
- [x] Dropped rigs from `CommitPreparedRig` or in-flight mesh fetches during reduced state are recovered on unpark.
- [x] Under `--diag`, log `[AvatarLimit] <id> unpark re-requested <n> worn items`.
- [x] `[WornRemoved]` summary line uses the same wearer ID as per-item lines (avatar agent UUID).
- [x] Self-test in `AvatarRenderer.ParkedOutfit.SelfTest.cs` verifies:
  - pending rig parked then unparked finishes building.
  - already-built item is not rebuilt on unpark.
- [x] AppVersion bumped (v0.29.4-alpha).
- [x] `/slng-verify` checks pass (both solutions build clean, tests pass, dotnet format clean, headless selftest passes).

## Technical Specs & Affected Files

- `src/SLNG.Core/...`
- `tests/SLNG.Core.Tests/...`
- `app/scripts/AvatarRenderer.cs`
- `app/scripts/AvatarRenderer.ParkedOutfit.SelfTest.cs`
- `app/scripts/Boot.cs`
- `docs/ROADMAP.md`

