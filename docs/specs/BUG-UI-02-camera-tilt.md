# Bug: BUG-UI-02 (Camera Tools Pan Pad — inverted + dead while focused)

## Context
The Camera-Controls window ("Camera Controls" button in the bottom toolbar, `CameraHUD`)
has three pads side by side: a round **orbit** pad, `+`/`−` **zoom** buttons, and a square
**pan** pad ("schieben" — shift the camera sideways / up / down). The user reported the
pan pad as "tilt": it moved the wrong way and stopped working after zooming.

The round orbit pad was **not** broken. The bug is entirely in the square pan pad.

## Technical detail

1. **Inverted left/right.** `CameraHUD` fed `pan_left` a `+X` step. The consumer in
   `AvatarController._Process` applies `targetPos += Transform.Basis.X * _panOffset.X`, and
   `+Basis.X` is screen-**right** — so "pan left" moved the camera right. Up/down were correct.

2. **Dead once a focus target was active.** `AvatarController._Process` applied `_panOffset`
   only inside the `else` of `if (_orbitTarget.HasValue)`. Any Alt-click focus sets
   `_orbitTarget`, after which `targetPos = _orbitTarget.Value` and the pan offset was never
   added — the pad did nothing at all. This is the "stops working when zoomed in / after
   focusing on an object" report.

3. **Far too fast.** The per-frame pan step was `1.0` (`_panOffset += 1` while held ≈ 60 m/s
   at 60 fps), so a tap threw the avatar off-screen and the camera looked "stuck".

## Fix (`fix/BUG-UI-02-03-camera-input`)

- `CameraHUD`: flipped `pan_left` / `pan_right` sign.
- `AvatarController`: moved `targetPos += Basis.X*_panOffset.X + Basis.Y*_panOffset.Y` out of
  the `else` so it applies with a focus target too.
- Pan step `1.0` → `0.1` (`CameraHUD.PanStep`).
- Follow-up on user request: a **"Camera" tab in Preferences** (`CameraSettings`,
  `CameraPreferencesPage`, `preferences.cfg [camera]`) with live Orbit / Pan / Zoom speed
  multipliers (10 %–300 %, default 100 %) and a "reset to 100 %" button; `CameraHUD` reads
  them per frame. Orbit base step lowered `0.05` → `0.035` after in-world feedback that it
  felt fast even at default.

## Acceptance criteria

- [x] Pan pad left/right map to the correct screen directions (up/down already did).
- [x] The pan pad works while the camera is focused/zoomed on an object, not just when
      following the avatar.
- [x] Pan speed is controllable (no off-screen overshoot on a tap) and adjustable in
      Preferences → Camera.
- [x] Confirmed in-world 2026-08-27.
