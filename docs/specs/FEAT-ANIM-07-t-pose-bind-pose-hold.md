# [FEAT-ANIM-07] Hold pose — bind pose (T-pose) / neutral stand (pose stand)

- **Feature ID:** `FEAT-ANIM-07`
- **Track:** `render`
- **Status:** `⏸️ Pending`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

A toggle that locally holds the avatar still — no clips advancing, no hand pose,
incoming animations + predicted locomotion ignored — until switched off. Two **modes**:

| Mode | Held pose | Use |
|---|---|---|
| **Bind pose (T-pose)** | Raw skeleton rest — arms out. Ugly on purpose. | Rigging / mesh-fit inspection, last-resort "nothing is animating me". |
| **Neutral stand (Pose Stand)** | A relaxed natural standing pose (the built-in `STAND` clip, held at one frame — **not** bind pose). | The clean base you layer a real pose animation on for a photo, like sitting on a scripted pose stand. |

**Why "pose stand" is not the T-pose:** you would never photograph an avatar with its
arms straight out. A pose stand is a *nice* neutral stand that (a) suppresses the AO,
(b) stops you drifting/turning with the camera, (c) gives a stable base so a pose from
FEAT-ANIM-06 sits on top cleanly. Same freeze machinery as bind-pose hold, different
held pose + a position/rotation lock.

Distinct from FEAT-ANIM-04 "Reset" (one-shot, resumes idle), FEAT-AVATAR-02 "Undeform"
(clears deformation, resumes idle), and FEAT-ANIM-09 "Freeze" (holds *the current*
animation frame, whatever it is). This is a *sustained hold at a chosen neutral pose*.

Local-only render state. Other viewers still see whatever the sim reports — document
this in the tooltip; it does not force a pose for others.

## Position / rotation lock (pose-stand mode)

- While in **Pose Stand** mode, ignore movement input and hold the avatar's world
  position and facing (don't turn with the camera). Releasing the mode restores normal
  control. Bind-pose mode does not lock position (it is a diagnostic, you may want to
  walk around and watch the skeleton).
- Optional small "lift" (a few cm up) so feet clear uneven ground for the shot — off by
  default, matches the rezzed-pose-stand convention.

## Behaviour

- Toggle **on:** `AvatarAnimationPlayer` stops evaluating `_active` / `_localOverlay`;
  the FEAT-ANIM-01 predicted locomotion is ignored; no hand pose applied.
  `SetActiveAnimations` still records the network set (so toggling off resumes correctly)
  but does not apply it.
- **Bind-pose mode:** hold `ResetToRestPose()` every frame — SLNG's shape-applied
  skeleton with every bone at its rest transform (shape joint offsets kept, all
  animation rotation/translation zeroed). No position lock.
- **Pose-stand mode:** hold the built-in `STAND` clip evaluated at a fixed frame (a
  relaxed neutral stand), plus the position/rotation lock above. The `STAND` clip is one
  of the FEAT-ANIM-01 prefetch ids, so it is already resident; if it fails to resolve on
  a grid, fall back to bind pose rather than T-posing unexpectedly.
- FEAT-ANIM-06 "play locally" **is** honoured on top of pose-stand mode (that is the
  point — pose stand is the base, the local pose layers over it by priority); it is
  ignored in bind-pose mode.
- Toggle **off:** resume from the current network set + prediction on the next frame, no
  stuck frame; pose-stand mode also restores movement control.
- Self avatar for v1. Holding a pose on another avatar is out of scope (niche).
- Interacts cleanly with FEAT-ANIM-04 resync/reset (no-ops while held) and FEAT-ANIM-09
  freeze (mutually exclusive — entering one exits the other).

## Acceptance Criteria

- [ ] "T-Pose halten" holds the self avatar in bind pose within one frame; walking,
      sitting, AO, gestures have no visible effect while held; position is not locked.
- [ ] "Pose Stand" holds a relaxed neutral stand (not arms-out), suppresses the AO,
      locks world position + facing so the camera can be swung freely, and a
      FEAT-ANIM-06 local pose layers on top of it correctly.
- [ ] Toggling either off resumes the correct current animation (walk if moving, AO
      stand if idle, seat pose if seated) with no stuck frame; Pose Stand also restores
      movement control.
- [ ] A script/poseball that keeps re-triggering an animation does not break either hold.
- [ ] A second client still sees the avatar's real animations (hold is local) — verified,
      and the limitation is stated in the UI tooltip / docs.
- [ ] Unit test: `AvatarAnimationPlayer` in bind-pose mode applies only the rest pose;
      in pose-stand mode applies the `STAND` frame plus any higher-priority local
      overlay; both ignore `_active` / predicted locomotion.
- [ ] `AppVersion` bumped.
- [ ] No regression: FEAT-ANIM-01/03/04/06/09, BUG-ANIM-01/02.

## Technical Specs & Affected Files

- `app/scripts/AvatarAnimationPlayer.cs`
  - `HoldMode { None, BindPose, PoseStand }`. In `Advance` / `ApplyBonePoses`: when not
    `None`, skip `_active` clip evaluation. `BindPose` → `ResetToRestPose()`.
    `PoseStand` → evaluate the `STAND` clip at a fixed time, then still apply the
    `_localOverlay` on top by priority. No hand-pose path runs in either.
- `app/scripts/AvatarRenderer.cs` — `SetSelfHoldMode(HoldMode)` reaching the self
  visual's `AnimPlayer`; gate `SetSelfPredictedLocomotion` application while held.
- `app/scripts/AvatarController.cs` — while `PoseStand`, ignore movement input and hold
  world position + `BodyRotation` (don't turn with the camera); restore on exit.
  While `BindPose`, input is unchanged. Prediction is still computed either way; the
  renderer ignores it.
- UI — a small mode selector (Aus / T-Pose / Pose Stand) in the Avatar Health menu
  (shared with FEAT-AVATAR-02/03) and the animation-tools panel; bindable hotkeys;
  locale strings both languages.
- `app/scripts/Boot.cs` — `AppVersion` bump.

## Sub-tasks / Progress

- [ ] `AvatarAnimationPlayer.HoldMode` (BindPose / PoseStand) + unit tests.
- [ ] `AvatarRenderer.SetSelfHoldMode` + prediction gate.
- [ ] `AvatarController` position/rotation lock for PoseStand.
- [ ] Menu mode selector + hotkeys + locale strings.
- [ ] `AppVersion` bump.
- [ ] In-world: T-pose (no lock, walk around), Pose Stand (locked, camera swing, local
      pose on top), clean release of both, re-trigger resistance, second-client check.
