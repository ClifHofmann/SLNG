# [FEAT-UI-74] Camera Controls like the reference viewer

- **Feature ID:** `FEAT-UI-74`
- **Track:** `ui`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

User report 2026-10-26: the Camera window is hard to use; Firestorm's is simpler, and its toolbar
button is an eye. Today the window opens at its minimum size, where the whole cluster runs at half
scale (`CameraHUD.MinContentScale` 0.5), so every target is ~14 px. The pads are nine tiny arrow
buttons, the speeds are per frame (fps-dependent) and fixed, and zoom has no slider.

Model: the reference viewer's `floater_camera.xml` (Firestorm and LL share the controls):
preset buttons in a column on the left, one round orbit trackball (`joystick_rotate`), a zoom
column of + / slider / - (`zoom_plus_btn`, `zoom_slider`, `zoom_minus_btn`), and one square pan
pad (`joystick_track`). The toolbar button that opens it is `Command_View_Icon`
(`toolbar_icons/view.png`), an eye.

## Behaviour (from the viewer source)

**Trackball / pan pad** — `lljoystickbutton.cpp`:
- Mouse down anywhere in the control. The quadrant of the press (split on the diagonals,
  `LLJoystickCameraRotate::handleMouseDown`) sets an initial offset of 24 px in that direction
  (`(slopNear 16 + slopFar 32) / 2`).
- While held, the effective vector is `(mouse - firstMouse) + initialOffset`; each axis is active
  when its component exceeds 16 px (`updateSlop`), so dragging while held turns a press into a
  diagonal or reverses it. The mouse stays captured outside the control while held.
- Rate ramps from 0.05 to 1.0 linearly over the first 0.25 s held (`getOrbitRate`,
  `ORBIT_NUDGE_RATE`, `NUDGE_TIME`), so a tap nudges and a hold moves.
- Pressing the centre dot (radius 7 px at 1x) resets: orbit -> `resetCameraOrbit`, pan ->
  `resetCameraPan`.
- The pressed quadrant(s) are drawn highlighted.

**Rates** — `llagentcamera.cpp:1271-1273`: orbit over and around 90 deg/s, pan 5 m/s, all
times the ramp; zoom buttons move at the current focus distance per second (`cameraOrbitIn(rate *
distance_to_focus / fps)`). Our Preferences speed multipliers (`CameraSettings.OrbitSpeed`,
`PanSpeed`, `ZoomSpeed`) still multiply these. Per second, scaled by delta — never per frame.

**Zoom slider** — `LLAgentCamera::getCameraZoomFraction` rescales the focus distance between the
minimum (per focus kind, `CameraZoomLimits.MinDistance`) and the maximum distance: 1 = closest
(top), 0 = farthest. The viewer is linear; ours is **logarithmic** (deliberate deviation): our
range is 0.5..200 m, and linear put every everyday distance in the top 5 % of the track. The
slider follows the camera every frame while not dragged; dragging it sets the distance.

## Acceptance Criteria

- [ ] Layout: preset column (Front, Side, Rear, Reset) | round orbit trackball | zoom column
      (+, vertical slider, -) | square pan pad; "Show focus point" checkbox below. No 10 px
      captions; each control has a tooltip that says what it does and that the centre resets.
- [ ] Opens at content scale 1.0 with targets of at least 24 px; the trackball and pan pad are
      ~100 px. A saved size smaller than the new minimum is not restored smaller.
- [ ] Trackball and pan pad behave as above (quadrant press, drag while held, slop, ramp,
      centre reset, highlight, capture).
- [ ] Rates per second as above, times the Preferences multipliers; identical at 30 and 144 fps.
- [ ] Zoom +/- held repeat; the slider reflects and sets the distance.
- [ ] Toolbar button icon is the eye (`visibility`), label unchanged.
- [ ] Unit tests for the pure maths (quadrant pick, slop axes, ramp, zoom fraction <-> distance).

## Technical Specs & Affected Files

- `src/SLNG.Core/Camera/CameraJoystick.cs` (new, pure): quadrant from press offset, initial
  offset, active axes from the effective vector, ramp(t).
- `src/SLNG.Core/Camera/CameraZoomFraction.cs` (new, pure): fraction <-> distance.
- `app/scripts/UI/CameraJoystick.cs` (new Control, `_Draw`): round or square, mouse capture,
  emits the per-frame axis rates; shared by trackball and pan pad.
- `app/scripts/UI/CameraHUD.cs`: new layout, per-second rates, slider, scale defaults.
- `app/scripts/AvatarController.cs`: `ZoomDistance` / `ZoomFraction` get/set, `ResetPan()`.
- `app/scripts/Boot.cs`: button bar icon `photo_camera` -> `visibility`.
- i18n en-US / de-DE: tooltips, reset.

## Sub-tasks / Progress

- [x] Core maths + tests
- [x] Joystick control + window layout
- [x] Controller API, toolbar icon, i18n, AppVersion

## Implementation notes (v0.27.101-alpha)

- `JoystickQuadrant` is its own file beside `CameraJoystick` (one public type per file). Rates are per second
  (`CameraHUD`): orbit 90 deg/s, pan 5 m/s, zoom buttons one distance per second as `exp(rate * dt)`, so the same
  at any frame rate; the Preferences multipliers still apply.
- The zoom buttons use the same 0.05 -> 1.0 ramp over 0.25 s as the pads (`LLPanelCameraZoom::getOrbitRate`,
  llfloatercamera.cpp), so a tap nudges.
- The window opens at scale 1.0 (`Size`), the minimum is scale 0.8 (`CustomMinimumSize`); `SLNGWindow` already
  clamps a saved size to `CustomMinimumSize` on restore.
- Until the camera controller is found the cluster is dimmed with "Waiting for the camera" over it, instead of a
  status line that shifted the layout when it went away.
- Selftest "camera controls (FEAT-UI-74)": sizes at default and minimum, pad press/drag/reversal/centre/scaling,
  slider <-> distance.
