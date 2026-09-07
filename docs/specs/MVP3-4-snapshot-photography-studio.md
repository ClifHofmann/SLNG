# [MVP3-4] Snapshot & Photography Studio

- **Feature ID:** `MVP3-4`
- **Track:** `ui` / `render`
- **Status:** `✅ Done` — Phase 1 (capture + save) shipped `v0.9.53-alpha` and confirmed
  in-world 2026-08-27. Closed 2026-09-07 (doc triage): the remaining phases are split into
  their own tracked work — `FEAT-RENDER-07` (DoF), `FEAT-UI-17` (save destination/format/
  upload), `FEAT-UI-22` (resolution multiplier, FOV, EEP presets).
- **Owner:** `claude`
- **Agent:** `ux-designer` (window) + `graphics-engineer` (capture / DoF / FOV)
- **Dep:** `M2-5` (lighting/post-fx)

## Goal
A floating "Snapshot" window for taking in-world photos: capture the 3D view without UI, save
it to disk at high resolution, with FOV control, depth-of-field, and the ability to override the
region day cycle with a chosen environment preset. `FEAT-RENDER-07` (DoF) is the depth-of-field
slice of this feature.

## Phasing

### Phase 1 — capture & save (landed, `v0.9.53-alpha`)
- `SnapshotWindow : SLNGWindow`, toolbar entry (`"snapshot"`, glyph `photo_camera`; the Camera
  Controls entry moves to a different glyph so the two don't collide).
- **Capture:** hide the `HudLayer` CanvasLayer, wait one rendered frame
  (`RenderingServer.FramePostDraw`), `GetViewport().GetTexture().GetImage()`, restore the layer.
  The shot is world-only — no windows, no toolbar, no name tags-layer chrome.
- **Preview:** the last capture shown in a `TextureRect` inside the window.
- **Save:** PNG to `user://snapshots/snapshot_yyyy-MM-dd_HH-mm-ss.png` (dir auto-created); a
  status line shows the globalized path. No overwrite — the timestamp is unique to the second,
  and a `_n` suffix guards the sub-second case.
- Resolution label = current main-viewport size (capture is at native size in Phase 1).
- Strings are hardcoded English, matching `ChatHistoryWindow` / `CreateLandmarkWindow`; i18n
  can follow once the window's copy settles.

### Phase 2 — resolution multiplier
Render the capture through a dedicated `SubViewport` with a camera mirroring the main one
(transform, FOV, near/far), sized to `main × {1,2,4}`, `RenderTargetUpdateMode.Once`, then read
its texture. The main viewport can't just be resized — it would reflow the whole UI and the
Godot window. Needs its own `World3D`? No — share the main `World3D` so the same scene renders
(see the `godot-subviewport-ownworld3d` note: `FindWorld3D()`), just a second camera into it.

### Phase 3 — FOV + Depth of Field (`FEAT-RENDER-07`)
- **FOV:** slider (20–120°) on the capture camera; live preview via the main camera while the
  window is open, restored on close.
- **DoF:** `CameraAttributesPractical.DofBlur{Far,Near}Enabled` + distance/transition, or
  `CameraAttributesPhysical` for an f-stop model. Toggle + Focus Distance + Blur Amount sliders.
- **Auto-focus:** raycast from viewport centre (or cursor), set focus distance to the hit.
- **Manual focus:** the sliders, when auto-focus is off.
- Restore the camera's attributes on window close / capture-done so normal play is unaffected.

### Phase 4 — environment presets
Dropdown of fixed day-cycle presets (Midday / Golden Hour / Sunset / Midnight / Overcast …)
that override `EnvironmentDriver`'s current cycle for the duration; "Region default" restores
the live one. Reuses the `SkySettings` / `DayCycle` model from `FEAT-ENV-01`; presets are
built-in `DayCycle` values, not fetched.

## Acceptance criteria
- [x] Toolbar button opens/closes the Snapshot window.
- [x] "Capture" produces a world-only image (no UI) and shows it as a preview.
- [x] "Save" writes a PNG under `user://snapshots/` and reports the path; repeated saves don't
      overwrite.
- [ ] Resolution multiplier (1×/2×/4×) produces a correspondingly larger PNG. *(Phase 2)*
- [ ] FOV slider changes the captured framing and is restored afterwards. *(Phase 3)*
- [ ] DoF toggles cleanly; auto-focus finds the looked-at subject; manual sliders are smooth.
      *(Phase 3 / FEAT-RENDER-07)*
- [ ] Environment presets override the day cycle for the shot and restore the region default.
      *(Phase 4)*

## Affected files
- `app/scripts/UI/SnapshotWindow.cs` — the window (new).
- `app/scripts/Boot.cs` — construct it, add it to `HudLayer`, register the toolbar item, hand
  it the `HudLayer` reference for clean capture.
- Phase 3: `app/scripts/AvatarController.cs` (or wherever the play camera lives) for FOV/DoF
  apply+restore; Phase 4: `app/scripts/EnvironmentDriver.cs` for a preset override hook.
