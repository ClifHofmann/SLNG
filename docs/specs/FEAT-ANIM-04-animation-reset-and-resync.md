# [FEAT-ANIM-04] Animation reset & resync

- **Feature ID:** `FEAT-ANIM-04`
- **Track:** `render` (+ `net`)
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Two operator-facing animation controls, both standard in reference viewers:

1. **Reset animations** ("Animationen zurücksetzen" / Firestorm "Stop Animating Me") —
   an escape hatch when the avatar is stuck in a poseball loop, a broken AO state, or a
   deformer animation. Stop everything that can be stopped, clear the local pose, rebuild
   the skeleton rests.
2. **Resync animations** ("Animationen synchronisieren" / Firestorm "Resync Animations")
   — restart all currently-playing looping animations in phase, so a desynced
   couples-dance / group-dance / poseball pair snaps back together.

Both are viewer-local visual operations (no protocol circumvention). SLNG owns its own
per-bone blender (`AvatarAnimationPlayer`), which makes both cheap and makes a
"resync **everyone** in the region at once" variant possible — useful for group photos.

The **UI surface** is the same "Avatar Health" menu as FEAT-AVATAR-02 (which already
lists "Stop All Animations" and "Reset Skeleton & Undeform" as bullets — this spec is
the engine-level implementation those menu items call).

## Reset animations — behaviour

- **Stop what we can, server-side:** for every animation currently signalled on the self
  agent (`Self.SignaledAnimations`), send an `AgentAnimation` stop
  (`Client.Self.AnimationStop`). Object-triggered loops may re-trigger — that is
  expected; the user then stands / detaches. This still clears gestures, self-started
  anims, and anything the object has since stopped looping.
- **Local:** `AvatarAnimationPlayer.Stop()` → clear `_active`, `ResetToRestPose()`.
- **Undeform / reset skeleton:** rebuild the bone rest transforms from the current shape
  params (the same path that builds them at avatar creation) and re-apply, then reset to
  rest. This clears bone **position** deltas a deformer left behind — once BUG-ANIM-01
  makes position tracks apply, reset must explicitly zero them, not just rotations.
- **Per-animation stop (debug view):** a small list of currently-playing animations with
  the **source object name** (from the source-aware event in FEAT-ANIM-03) and a stop
  button per row — "which anim is deforming me → stop just that one". Optional but cheap
  once FEAT-ANIM-03 lands.

## Resync animations — behaviour

- **Local rephase:** set `CurrentTime = InPoint` for every active `PlayingAnimation` on
  the self avatar in the same frame → looping clips realign. New
  `AvatarAnimationPlayer.Resync()`.
- **Server-side for self-started anims:** for animations SLNG itself started (gestures,
  a future built-in AO), stop + restart on the sim so other viewers see the resync too.
  Object-triggered anims: local rephase only (no permission to restart them) — document
  this, it matches Firestorm.
- **"Resync all avatars in region" variant:** iterate every avatar visual's `AnimPlayer`
  and `Resync()` them together. Viewer-only, no packets. Menu item + optional hotkey.

## Acceptance Criteria

- [x] "Animationen zurücksetzen" halts stuck poses and returns the self avatar to the
      default idle within one frame; a looping poseball anim that has genuinely stopped
      server-side does not come back.
- [x] After reset, a deformer that had shifted bone **positions** is cleared (no
      lingering limb distortion) without a relog.
- [x] "Animationen synchronisieren" restarts all playing looping anims on the self
      avatar in phase; a deliberately desynced two-person dance visually realigns.
- [x] "Resync all avatars" rephases every visible avatar's looping anims together.
- [x] Both commands are reachable from the Avatar Health menu (shared with
      FEAT-AVATAR-02); locale strings both languages (selftest locale parity).
- [x] `AppVersion` bumped.
- [x] Unit tests: `AvatarAnimationPlayer.Resync()` resets all `CurrentTime` to `InPoint`;
      `Stop()` clears `_active` and requests a rest-pose reset.
- [x] No regression: FEAT-ANIM-01 locomotion, BUG-ANIM-01 pelvis track, BUG-ANIM-02
      tie-break, FEAT-ANIM-03 seat/AO resolver.

## Technical Specs & Affected Files

- `app/scripts/AvatarAnimationPlayer.cs`
  - `Resync()` — one pass setting every `PlayingAnimation.CurrentTime = Data.InPoint`.
  - `Stop()` clears `_active` and resets bone poses to rest.
- `app/scripts/AvatarRenderer.cs`
  - `ResyncSelfAnimations()` / `ResyncAllAnimations()` / `StopSelfAnimations()` / `ResetSelfSkeleton()`.
  - rest-rebuild: re-runs `ApplyShape`, `ResetBonePoses`, `RebuildRiggedAttachmentSkins`, `RefreshBodyPartSkins`, `RefreshStaticAttachmentOffsets`, `RecomputeFootOffset`.
- `src/SLNG.Net/GridSession.cs`
  - `StopAllSelfAnimations()` — iterates `_client.Self.SignaledAnimations.Keys`,
    `_client.Self.AnimationStop(id, true)` for each.
- `app` Avatar menu (shared with FEAT-AVATAR-02) — "Animationen stoppen", "Skelett zurücksetzen (Undeform)",
  "Animationen synchronisieren", "Alle Avatare synchronisieren". Locale strings in en-US and de-DE.
- `app/scripts/Boot.cs` — `AppVersion` bumped to `v0.22.171-alpha`.

## Sub-tasks / Progress

- [x] `AvatarAnimationPlayer.Resync()` + `Stop()` + smoke test in `SelfTest.cs`.
- [x] `AvatarRenderer` resync/reset entry points (self + all-avatars).
- [x] `GridSession.StopAllSelfAnimations()` + unit test.
- [x] Avatar menu items + locale strings in `en-US.json` and `de-DE.json` + `AppVersion` bump.
