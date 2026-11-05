# [BUG-AVATAR-23] Avatars stay invisible after the cap parks them mid-build

- **Feature ID:** `BUG-AVATAR-23`
- **Track:** `avatar` / `render`
- **Status:** 🧪 Review
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md)

## Overview & Goal

When another avatar arrives and is partially loaded, promotion and cap limit adjustments can swap it out to "reduced/parked" state mid-build.
When later promoted back to "full", the unpark logic previously inspected scene presence to guess if items finished, causing false positives and false negatives (animesh, failed fetches, or newer LOD rigs interrupted).

Goal:
Track unfinished worn builds explicitly per avatar via `visual.WornBuildsInProgress` and sequence tracking. On unpark, re-request exactly what is still in progress (clearing `_attachmentMeshIds` and `_attachmentPrimSignatures`), while already-finished visuals remain instant without rebuilds. Also log `[AvatarLimit] <id> unpark re-requested <n> worn items` under `--diag` when count > 0.
Also fix the `[WornRemoved]` summary line reporting a mismatched wearer ID compared to per-item lines.

## Acceptance Criteria

- [x] Unfinished worn builds explicitly tracked via `visual.WornBuildsInProgress` and sequence numbers (`_wornBuildActiveSeq`).
- [x] On promotion from parked (`IsParked`), items in `WornBuildsInProgress` (or unrequested items arriving while parked) are re-requested.
- [x] Finished items are not rebuilt/re-requested on unpark (preserving BUG-PERF-16 instant unpark).
- [x] Re-requesting clears cached `_attachmentMeshIds` and `_attachmentPrimSignatures`.
- [x] Interrupted in-flight mesh fetches do not duplicate builds or overwrite newer requests.
- [x] Under `--diag`, log `[AvatarLimit] <id> unpark re-requested <n> worn items` only when count > 0.
- [x] `[WornRemoved]` summary line uses the same wearer ID as per-item lines (avatar agent UUID).
- [x] Self-test in `AvatarRenderer.ParkedOutfit.SelfTest.cs` verifies all 5 promotion cases through the real promotion path (waiting for deferred calls in isolated scratch cache).
- [x] AppVersion bumped (v0.29.7-alpha).
- [x] `/slng-verify` checks pass (both solutions build clean, tests pass, dotnet format clean, headless selftest passes).

## Technical Specs & Affected Files

- `src/SLNG.Core/...`
- `tests/SLNG.Core.Tests/...`
- `app/scripts/AvatarRenderer.cs`
- `app/scripts/AvatarRenderer.ParkedOutfit.SelfTest.cs`
- `app/scripts/Boot.cs`
- `docs/ROADMAP.md`

