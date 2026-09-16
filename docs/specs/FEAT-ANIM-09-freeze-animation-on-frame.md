# [FEAT-ANIM-09] Freeze / pause the current animation on a frame

- **Feature ID:** `FEAT-ANIM-09`
- **Track:** `render`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

A toggle that holds whatever animation is playing at its **current frame** — you are
mid-dance, you hit "Einfrieren", and the avatar holds that exact pose for a photo.

Distinct from the neighbours:
- **FEAT-ANIM-07 T-pose** holds the *bind pose* (no animation).
- **FEAT-ANIM-04 resync** *restarts* looping clips from `InPoint`.
- This freezes time in place — every active clip keeps its current per-bone value.

Primary use is the Media Studio / photography. Local render state only; other viewers
still see the animation running.

## Behaviour

- Toggle **on:** `AvatarAnimationPlayer` stops advancing `CurrentTime` for all active
  clips **and** the FEAT-ANIM-06 local overlay. `ApplyBonePoses` keeps evaluating at the
  frozen time, so the pose holds exactly.
- Incoming network `AvatarAnimation` set changes are **buffered** while frozen (the clip
  set is not swapped — a mid-freeze swap would snap the pose) and applied on resume.
- **Frame step** (optional, while frozen): nudge the frozen `CurrentTime` by ±one frame
  (clip frame rate, fallback 1/30 s) to fine-tune the held pose. Buttons + hotkeys.
- Toggle **off:** resume advancing from the frozen time (not from `InPoint`), then apply
  any buffered set change on the next frame.
- Self avatar for v1; a "**alle Avatare einfrieren**" variant (iterate every visual's
  `AnimPlayer`) for group shots, mirroring FEAT-ANIM-04's "resync all".
- Interacts cleanly: FEAT-ANIM-07 hold and FEAT-ANIM-04 reset/resync are no-ops while
  frozen; FEAT-ANIM-01 predicted locomotion is also held.

## Acceptance Criteria

- [x] Playing a looping animation, toggling "Einfrieren" holds the exact current pose;
      it does not snap to the start or to bind pose.
- [x] Frame-step moves the held pose by a small increment in each direction.
- [x] A network animation update arriving while frozen does not change the visible pose;
      it takes effect on unfreeze.
- [x] Unfreeze resumes smoothly from the held frame (no jump to `InPoint`).
- [x] "Alle Avatare einfrieren" holds every visible avatar's pose together.
- [x] A second client still sees the animation running (local-only) — verified.
- [x] Unit test: `AvatarAnimationPlayer` with the freeze flag set does not advance
      `CurrentTime` on `Advance(delta)`; frame-step changes it by the expected amount;
      `SetActiveAnimations` while frozen buffers rather than swaps.
- [x] `AppVersion` bumped (`v0.22.174-alpha`).
- [x] No regression: FEAT-ANIM-01/03/04/06/07, BUG-ANIM-01/02.

## Technical Specs & Affected Files

- `app/scripts/AvatarAnimationPlayer.cs`
  - `IsFrozen` flag; `Advance(delta)` early-returns the time-advance step when set (still runs `ApplyBonePoses`).
  - `StepFrame(int dir)` — only while `IsFrozen`; clamps within the clip's loop window.
  - `SetActiveAnimations` while `IsFrozen`: stash the incoming list in a pending field,
    apply it when `IsFrozen` becomes false.
- `app/scripts/AvatarRenderer.cs` — `FreezeSelfAnimation(bool)`, `StepSelfFrame(int)`,
  `FreezeAllAvatars(bool)`, `StepAllFrames(int)`.
- UI — button + frame-step controls in `SnapshotWindow` (framing tool) and `TopMenu` Avatar submenu;
  locale strings both languages.
- `app/scripts/Boot.cs` — `AppVersion` bump (`v0.22.174-alpha`).

## Sub-tasks / Progress

- [x] `AvatarAnimationPlayer` freeze/step/buffer + unit tests.
- [x] `AvatarRenderer` entry points (self + all-avatars).
- [x] `SnapshotWindow` controls + menu + locale strings.
- [x] `AppVersion` bump.
- [x] Self-test automated test suite passed.
