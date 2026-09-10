# [FEAT-ANIM-07] T-pose / bind-pose hold

- **Feature ID:** `FEAT-ANIM-07`
- **Track:** `render`
- **Status:** `⏸️ Pending`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

A toggle that locally holds the avatar in its **bind / rest pose** ("T-pose") —
no animation clips, no hand pose, incoming animations ignored — until switched off.

Distinct from FEAT-ANIM-04 "Reset animations" (a one-shot stop that then returns to the
idle) and from FEAT-AVATAR-02 "Undeform" (clears deformation, resumes idle). T-pose is a
*sustained hold*.

Uses:
- Inspecting mesh-body / attachment fit and rigging against a neutral skeleton.
- A neutral base for posing / snapshots (Photography Studio).
- A last-resort "nothing is animating me" state when Reset isn't enough because a
  script keeps re-triggering.

Local-only render state. Other viewers still see whatever the sim reports — document
this; it is not a way to force a T-pose for others.

## Behaviour

- Toggle **on:** `AvatarAnimationPlayer` stops evaluating `_active` / `_localOverlay`
  and holds `ResetToRestPose()` every frame; the FEAT-ANIM-01 predicted locomotion is
  ignored; no hand pose applied. `SetActiveAnimations` still records the network set (so
  toggling off resumes correctly) but does not apply it.
- The rest pose here is SLNG's shape-applied skeleton with every bone at its rest
  transform — i.e. the same pose `ResetToRestPose()` already produces (shape joint
  offsets kept, all animation rotation/translation zeroed). That is the useful pose for
  a rigging check; a raw pre-shape skeleton is not needed.
- Toggle **off:** resume from the current network set + prediction on the next frame, no
  T-pose "stuck" frame.
- Self avatar for v1. "Hold T-pose on <other avatar>" is out of scope (niche; revisit if
  asked).
- Interacts cleanly with FEAT-ANIM-04 resync/reset (they are no-ops while held) and
  FEAT-ANIM-06 local play (a locally-played clip does not un-stick the hold).

## Acceptance Criteria

- [ ] Menu / hotkey toggle "T-Pose halten" holds the self avatar in bind pose within one
      frame; walking, sitting, AO, gestures have no visible effect while held.
- [ ] Toggling off resumes the correct current animation (walk if moving, AO stand if
      idle, seat pose if seated) with no stuck frame.
- [ ] A script/poseball that keeps re-triggering an animation does not break the hold.
- [ ] A second client still sees the avatar's real animations (hold is local) — verified,
      and the limitation is stated in the UI tooltip / docs.
- [ ] Unit test: `AvatarAnimationPlayer` with the hold flag set applies only the rest
      pose regardless of `_active` / `_localOverlay` / predicted-locomotion input.
- [ ] `AppVersion` bumped.
- [ ] No regression: FEAT-ANIM-01/03/04/06, BUG-ANIM-01/02.

## Technical Specs & Affected Files

- `app/scripts/AvatarAnimationPlayer.cs`
  - `HoldBindPose` bool. In `Advance` / `ApplyBonePoses`: when set, skip clip evaluation
    and just `ResetToRestPose()`; keep advancing clip `CurrentTime` optional (cheap to
    skip). Ensure no hand-pose application path runs.
- `app/scripts/AvatarRenderer.cs` — `SetSelfTPose(bool)` reaching the self visual's
  `AnimPlayer`; also gate `SetSelfPredictedLocomotion` application while held.
- `app/scripts/AvatarController.cs` — while held, still compute prediction (state stays
  live) but the renderer ignores it; no input changes.
- UI — toggle in the Avatar Health menu (shared with FEAT-AVATAR-02/03) and/or the
  animation-tools panel; optional bindable hotkey; locale strings both languages.
- `app/scripts/Boot.cs` — `AppVersion` bump.

## Sub-tasks / Progress

- [ ] `AvatarAnimationPlayer.HoldBindPose` + unit test.
- [ ] `AvatarRenderer.SetSelfTPose` + prediction gate.
- [ ] Menu toggle + optional hotkey + locale strings.
- [ ] `AppVersion` bump.
- [ ] In-world: hold across walk/sit/AO/gesture, clean release, re-trigger resistance,
      second-client visibility check.
