# Feature: FEAT-AVATAR-02 (Avatar Health & Troubleshooting Tools)

- **Feature ID:** `FEAT-AVATAR-02`
- **Track:** `render/ui`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Context
Second Life and OpenSim avatars frequently suffer from visual glitches—such as stuck animations, corrupted texture bakes, or permanently deformed skeletons caused by broken animation assets. The user requested a suite of recovery tools analogous to Firestorm's "Avatar Health" (Avatar Befinden) options to allow users to fix these issues locally without needing to relog.

## Requirements
Implement recovery commands under `TopMenu` -> `Avatar`:

1. **Force Texture Rebake ("Rebake Textures" / Texturen neu backen):**
   - Invalidate current BoM composites, force fresh rebake (`AvatarRenderer.ForceRebakeSelf` + SSB cap POST). Already available via FEAT-AVATAR-01 / FEAT-AVATAR-03.
2. **Stop All Animations ("Stop Animating Me" / Animationen stoppen):**
   - Halt all currently playing animations on the local avatar (`AvatarRenderer.StopSelfAnimations` -> `AnimPlayer.Stop()`, reset bone poses).
   - Send `AnimationStop` for all active animations server-side (`GridSession.StopAllSelfAnimations`).
3. **Reset Skeleton & Undeform ("Undeform Avatar" / Skelett zurücksetzen):**
   - Reset avatar bone poses, clear stuck deformations, and re-apply current shape parameters cleanly (`AvatarRenderer.ResetSelfSkeleton` -> `ApplyShape`, `RebuildRiggedAttachmentSkins`, `RefreshBodyPartSkins`, `RefreshStaticAttachmentOffsets`, `RecomputeFootOffset`).
4. **Resync Animations ("Animationen synchronisieren"):**
   - Resync active looping animations on self or all avatars (`AvatarRenderer.ResyncSelfAnimations` / `ResyncAllAnimations`).

## Acceptance Criteria
- [x] The viewer provides accessible "Avatar Health" tools in the TopMenu `Avatar` menu.
- [x] Triggering a "Rebake" successfully refreshes the avatar's composite textures.
- [x] "Stop Animations" immediately halts stuck poses and returns the avatar to the default rest state.
- [x] "Reset Skeleton" successfully clears bone deformations and restores rest transforms without requiring a client restart.
- [x] Full localization in en-US and de-DE with selftest parity.
