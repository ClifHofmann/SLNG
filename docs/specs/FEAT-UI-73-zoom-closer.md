# [FEAT-UI-73] Zoom closer: the viewer's field-of-view range and per-focus minimum camera distances

- **Feature ID:** `FEAT-UI-73`
- **Track:** `ui` (camera; the pure rules live in `core`)
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

User request (2026-10-26): "Sometimes it would help if you could zoom in closer." The camera was focused on another
avatar (Alt+Click, then the mouse wheel) to look at a detail of their outfit and could not get close enough.

Two separate limits were in the way, and the reference viewer has neither:

1. **Field of view.** SLNG clamped it to 50-100 degrees. The viewer's real range is 5-175 degrees (narrowed by the
   window shape), and a narrow field of view is the viewer's actual "zoom in" for looking at a detail (View > Zoom
   level, `Ctrl+0`).
2. **Camera distance.** SLNG stopped every focus at 0.5 m from the pivot. The viewer keeps one minimum per kind of
   focus: an object 2 cm, the ground 15 cm, a person 50 cm.

Both are ported with the viewer's numbers, as pure rules in `SLNG.Core` with tests.

## Viewer reference (all in the vendored clone `scratch/slviewer/indra/`)

### Field of view

| Fact | Where |
|---|---|
| Vertical FOV limits for a square view: `MIN_FIELD_OF_VIEW` 5 degrees, `MAX_FIELD_OF_VIEW` 175 degrees | `llmath/llcamera.h:51-52` |
| Default 60 degrees (`DEFAULT_FIELD_OF_VIEW`); aspect accepted 0.02 .. 50 | `llcamera.h:36`, `:46`, `:41` |
| `getMinView()` = 5 degrees, or 5 / aspect when the view is narrower than tall; `getMaxView()` = 175 degrees, or 175 / aspect when the view is wider than tall | `llmath/llcamera.cpp:80-94` |
| `LLViewerCamera::setView` and `setDefaultFOV` clamp to `[getMinView(), getMaxView()]`; `LLCamera::setView` then clamps to the absolute 5-175 | `newview/llviewercamera.cpp:783-790`, `:817-819`; `llcamera.cpp:115-119` |
| `setAspect` clamps the aspect but does not re-clamp the FOV; the clamp is re-applied every frame because `updateCamera` calls `setView(default / (1 + zoomFactor))` | `llcamera.cpp:129-133`; `newview/llagentcamera.cpp:1481` |
| `LLZoomer`: View.ZoomOut x 1.2, View.ZoomIn x 1/1.2, View.ZoomDefault = 60 degrees; each stores the clamped result in the `CameraAngle` setting | `newview/llviewermenu.cpp:9947-9959`, registered at `:10006-10008`; shortcuts `Ctrl+8` / `Ctrl+0` / `Ctrl+9`, `skins/default/xui/en/menu_viewer.xml:741-763` |
| `CameraAngle`: radians, default 1.047197551, **persisted**; read back at startup | `app_settings/settings.xml:1307-1316`; `newview/llstartup.cpp:1727` |
| The Preferences slider `camera_fov` is bound to `CameraAngle`, in radians, and is given the aspect-dependent range in code (its XML bounds 0.17 and 2.97 are overridden) | `newview/llfloaterpreference.cpp:502-503`, `:618-619`; `skins/default/xui/en/panel_preferences_move.xml:23-39` |

Godot: `Camera3D.Fov` is the **vertical** angle under the default KeepHeight mode, which `AvatarController` never changes
(only the HUD camera in `AvatarRenderer` sets `KeepAspect`). So the viewer's `mView` and SLNG's `Fov` are the same
quantity and the aspect rule applies unchanged.

### Camera distance

| Fact | Where (`newview/llagentcamera.cpp`) |
|---|---|
| `LAND_MIN_ZOOM` 0.15 m, `AVATAR_MIN_ZOOM` 0.5 m, `OBJECT_MIN_ZOOM` 0.02 m | `:88`, `:90`, `:91` |
| Wheel with a focus point: `cameraOrbitIn` clamps to the per-kind minimum (land / avatar / object), then `cameraZoomIn(1)` applies it again, for an avatar through `calcCameraMinDistance` | `:1017-1032`, `:950-967`; wheel entry `handleScrollWheel` `:2080-2122` |
| A worn item as the focus is replaced by its avatar, so it gets the avatar minimum | `:2607-2614` |
| Following one's own avatar: zoom fraction limited to `[MIN_ZOOM_FRACTION, MAX_ZOOM_FRACTION]` = `[0.25, 8]` of the resting offset (3.09 m at the default `CameraOffsetRearView` (-3, 0, 0.75), `app_settings/settings.xml:1344`), i.e. 0.77 m; below it the viewer **switches to mouselook** | `:59`, `:61`, `:989-1006` (switch at `:997-1001`) |
| Final camera distance never below `MIN_CAMERA_DISTANCE` 0.1 m | `:72`, `:1894` |
| For a **non-mesh prim** the viewer does not go closer than the object's extents allow: `calcCameraFOVZoomFactor` pushes the camera back by `(1 + f)` and narrows the FOV by `1 / (1 + f)`, so the picture is the same as at 2 cm without the camera sitting inside the prim. Mesh, land and avatar focus get no such factor | `:1728-1753`, `:1357`, `:1481`; extents in `calcCameraMinDistance` `:575-722`, padding `OBJECT_EXTENTS_PADDING` 0.5 `:101` |

### Near clip

The viewer's near plane is a **fixed 0.1 m**: `setNear(0.1f)` with the comment "Leave at 0.1 meters until we have real
near clip management" (`llagentcamera.cpp:207-208`), reset to `MIN_NEAR_PLANE` (0.1) every frame
(`newview/llviewerdisplay.cpp:658`) and never allowed below it (`llmath/llcamera.h:47`, `llcamera.cpp:136-139`).
That is exactly why it needs the back-off trick above to get close to a prim. SLNG's camera ran on Godot's default 0.05 m.

## What was built

| Rule | SLNG | Where |
|---|---|---|
| FOV range 5 .. 175, narrowed by the window shape, viewer clamp order | `CameraFovLimits.MinDegrees / MaxDegrees / Clamp` | `src/SLNG.Core/Camera/CameraFovLimits.cs` |
| Zoom step 1.2, Zoom default 60 | `CameraFovLimits.ZoomIn / ZoomOut / Default`; `Boot.ChangeFieldOfView` for `Ctrl+0 / Ctrl+8 / Ctrl+9`, persisted through `CameraSettings.SetFov` as before | `app/scripts/Boot.KeyActions.cs` |
| The camera shows the clamp for the CURRENT window every frame; the stored value is untouched | `AvatarController.ApplyFieldOfView` | `app/scripts/AvatarController.cs` |
| Preferences slider has the same range for the current window | `CameraPreferencesPage.RefreshFovRange` (on creation and whenever the page is shown); re-ranging does not write the stored value | `app/scripts/UI/CameraPreferencesPage.cs` |
| Absolute stored bounds 5 .. 175 (was 50 .. 100) | `CameraSettings.MinFov / MaxFov` | `app/scripts/UI/CameraSettings.cs` |
| Minimum distance per kind of focus: object 0.02, land 0.15, person 0.5, own avatar 0.5 | `CameraZoomLimits.MinDistance(CameraFocusKind)` | `src/SLNG.Core/Camera/CameraZoomLimits.cs`, `CameraFocusKind.cs` |
| Which kind an Alt+Click landed on | `AvatarController.ClassifyFocus`: terrain layer or no body = land; avatar layer, or any node under a `Skeleton3D` (a worn item) = person; else object. The roster double-click is a person. `SetPresetView` (Esc) returns to own avatar | `app/scripts/AvatarController.cs` |
| Every distance clamp (wheel, Alt+drag, keys, pad, transitions) uses the kind's minimum | `AvatarController.MinZoom`; `CameraZoom.MinZoom` is gone, `WheelStep` and `AnchorPan` take the floor explicitly | `app/scripts/AvatarController.cs`, `src/SLNG.Core/Camera/CameraZoom.cs` |
| Near plane follows the distance | `CameraZoomLimits.NearPlane(distance)` applied by `AvatarController.ApplyNearPlane` | see below |

### Decisions

- **The old flat clamp was 0.5 m, not `CameraSettings.MinDistance`.** `CameraSettings.MinDistance` (1.0) is only the range of
  the "Rear view distance" slider (where the camera rests after a reset). The real orbit clamp was `CameraZoom.MinZoom`
  (0.5 m), used by the wheel, Alt+drag, the Alt+W/S and `=`/`-` keys, the pad and the transitions. The slider keeps 1 m
  to 20 m: it is a resting distance, and the viewer's own equivalent (`CameraOffsetScale`, 0.5 to 3 times 3.09 m,
  `panel_preferences_move.xml:40-57`) bottoms out at 1.55 m, so 1 m is already closer than the viewer lets it be.
- **A person and the default view stay at 0.5 m.** That is the viewer's `AVATAR_MIN_ZOOM` for a focused person, so for the
  case in the request nothing gets closer than before: the gain there is the field of view (5 degrees instead of 50 shows
  a patch about 10 times smaller, from the same distance). For the default third-person view the viewer stops at 0.77 m
  and then switches to mouselook; SLNG has no mouselook (`FEAT-RENDER-23`, `SlShortcutGaps`), so there is nothing to hand
  over to, and the floor stays at SLNG's existing 0.5 m, which is already closer than 0.77 m.
- **A worn item counts as a person** (the viewer replaces it by its wearer, `:2607-2614`), so an Alt+Click on a necklace
  still stops at 0.5 m. The detail is reached with the field of view.
- **Near plane: dynamic, not lower.** Fixed at Godot's 0.05 m the focus point would be clipped from 0.05 m down, i.e. for
  every object closer than that (the minimum is 0.02 m). `NearPlane(distance)` returns `min(0.05, distance / 2)`, never below
  `0.01`: 0.05 m exactly as before for every distance of 0.1 m and more (so nothing changes in normal play), 0.01 m at the
  2 cm floor. The point being looked at therefore always sits at least twice as far away as the near plane. A permanently
  smaller plane would cost depth precision for the whole scene in every frame for the sake of a rare close-up; the camera
  node's `Near` is only written when the value changes. (Forward+ in Godot 4.3 and later uses a reversed floating-point
  depth buffer, where the near plane hardly affects precision; this was not re-verified on 4.7, which is one more reason
  not to depend on it.) The viewer's fixed 0.1 m is not matched: it would clip the focus point at every distance under
  0.1 m, which the viewer avoids only by the back-off described above.
- **The field of view feeds texture and object LOD**, as in the viewer (`pixelsPerRadian = viewport height / FOV` in
  `ObjectRenderer.ComputeTextureLod` and the range epoch). A 5-degree view therefore asks for the sharper textures it
  actually shows; this is the existing, intended behaviour, not something this ticket added.

## Not ported (deliberately)

- The viewer's FOV back-off for a prim (`calcCameraFOVZoomFactor`): replaced by going straight to 2 cm and lowering the
  near plane. It would matter if the camera ever ended up inside a large prim's bounds; the focus point is on the
  surface, the camera is outside it.
- `calcCameraMinDistance` for a person (`llagentcamera.cpp:575-722`): pushes the minimum out to the edge of the avatar's
  shrunk bounding box when the camera is on the far side of the focus point. From the side the focus was set on the box term
  is below the 0.5 m floor, so it only shows when the camera has been orbited round behind the body.
- Mouselook (the end of the viewer's third-person range). Out of scope until `FEAT-RENDER-23`.
- Key and Alt+drag zoom are still linear in metres (6 m/s, 15 m/s, 0.15 m per pixel), the wheel is multiplicative. With
  floors of 2 cm they are coarse near the end of the range. The viewer's equivalent is proportional (`:1288-1294`);
  recommendation 5 of `camera-zoom-parity-firestorm-vs-slng.md`.
- Camera collision and the 0.5 m ground clamp are unchanged (still none).

## Acceptance Criteria
- [x] `Ctrl+0` repeatedly narrows the field of view from 60 down to 5 degrees (14 steps at 1.2), `Ctrl+8` widens it up to the limit of the window shape (98.4 degrees at 16:9), `Ctrl+9` restores 60; all persist
- [x] The Camera preferences slider has the same range for the current window shape and does not rewrite the stored value on open
- [x] The camera renders the clamped value for the current window; a resize back restores the stored one
- [x] An Alt+Click focus allows 0.02 m on an object, 0.15 m on the ground, 0.5 m on a person or a worn item; the default view keeps 0.5 m
- [x] The near plane is 0.05 m at every distance of 0.1 m and more, and half the distance below it (0.01 m at the object floor)
- [x] Pure rules in `SLNG.Core` with xUnit tests (`CameraFovLimitsTests`, `CameraZoomLimitsTests`); `--selftest` check "camera focus kinds"
- [ ] Seen in a running session (not possible headless): the close-up of an object at 2 cm, a worn item at 0.5 m with a 5-degree view, no clipped focus point

## Technical Specs & Affected Files
- `src/SLNG.Core/Camera/CameraFovLimits.cs`, `CameraZoomLimits.cs`, `CameraFocusKind.cs` (new); `CameraZoom.cs` (`MinZoom` removed)
- `tests/SLNG.Core.Tests/Camera/CameraFovLimitsTests.cs`, `CameraZoomLimitsTests.cs` (new); `CameraZoomTests.cs` (explicit floor)
- `app/scripts/AvatarController.cs` (kind, `MinZoom`, `ClassifyFocus`, `ApplyFieldOfView`, `ApplyNearPlane`)
- `app/scripts/Boot.KeyActions.cs`, `app/scripts/UI/CameraSettings.cs`, `app/scripts/UI/CameraPreferencesPage.cs`
- `app/scripts/SelfTest.CameraZoomLimits.cs`, `app/scripts/SelfTest.cs`; `app/i18n/en-US.json`, `de-DE.json` (`camera_view_hint`)
- `docs/BENUTZERHANDBUCH.md`, `docs/ROADMAP.md`, `app/scripts/Boot.cs` (`AppVersion` v0.27.98-alpha)

## Sub-tasks / Progress
- [x] Read the viewer's FOV, zoom, distance and near-plane code
- [x] Core rules and tests
- [x] Camera controller, key actions, preferences page
- [x] Selftest, manual, roadmap
- [ ] Look at it in a running session
