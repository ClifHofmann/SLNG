# [RESEARCH-CAM-ZOOM] Camera zoom: how Firestorm does it, and how SLNG differs

- **Feature ID:** `RESEARCH-CAM-ZOOM` (research note, no ticket yet; follow-ups below would become `FEAT-UI-xx`)
- **Track:** `ui` (camera)
- **Status:** `⏸️ Pending` (findings only, nothing built)
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

The maintainer says zooming (third-person camera distance) "feels much more pleasant in Firestorm".
This note establishes from source what the Linden viewer and Firestorm actually do for the mouse
wheel, the keyboard/pad zoom, the Alt-drag zoom, the minimum distance, collision and focus
targets, extracts the same from SLNG's `AvatarController.cs`, compares them, and ranks what to
change. Feel is inferred from the code: **neither viewer was run.**

Sources and how much to trust each:

| Tag | Source | Pinned to |
|---|---|---|
| **LL** | `scratch/slviewer` (vendored `secondlife/viewer`, shallow clone, HEAD `4ef9f8f14b`) | local files, quoted line numbers are this clone's |
| **FS** | `FirestormViewer/phoenix-firestorm` `master`, fetched with `gh api` on 2026-10-03 (HEAD `b3c157df37`) | line numbers are of that fetch, not a tag; Firestorm may have moved since |
| **SLNG** | `E:/Git/SLNG` at `main` (v0.26.28-alpha) | `app/scripts/AvatarController.cs` etc. |

Paths: LL = `scratch/slviewer/indra/newview/<file>`; FS = `indra/newview/<file>` in the Firestorm repo.

## Acceptance Criteria
- [x] Per-notch wheel behaviour of LL, FS and SLNG established from source, with line numbers
- [x] Smoothing constants and limits of LL/FS and SLNG established
- [x] Comparison table with the perceptual cause per row
- [x] Ranked recommendation list with numbers, target function and risk
- [ ] Anything built (out of scope for this note)
- [ ] Feel confirmed in a running session (not possible from source alone)

## Findings

### 1. Short version: the 3 things that most probably make Firestorm feel nicer

1. **Every wheel notch is a glide, not a jump.** LL/FS never set the camera distance directly: the wheel changes a
   *target* distance and the real distance chases it with a critically damped lerp, half-life 0.07 s
   (`CAMERA_ZOOM_HALF_LIFE`, LL `llagentcamera.cpp:70`, lerp at `:1894-1900`; FS `llagentcamera.cpp:70`, `:2074`),
   and the finished camera position is smoothed once more with a 0.02 s half-life (`CameraPositionSmoothing` = 1.0 x
   `SMOOTHING_HALF_LIFE` 0.02, LL `:1438`, FS `:1590`). The interpolant is `1 - 2^(-dt/halfLife)`
   (`llcommon/llcriticaldamp.cpp:calcInterpolant`), so it is frame-rate independent. SLNG changes `_zoom` instantly: one
   notch is a one-frame 0.5 m jump (`AvatarController.cs:154`, `:258-260`, camera placed at `:1454`, no lerp anywhere in the zoom path).
2. **The notch step is proportional to the distance (exponential), SLNG's is a fixed 0.5 m.** LL/FS: one notch =
   x 2^(-1/4) in (-15.9 %) and x 2^(1/4) out (+18.9 %) (`handleScrollWheel`, LL `:2080-2122`, FS `:2362-2450`;
   `ROOT_ROOT_TWO = sqrt(sqrt(2))`). SLNG: `ZoomBy(-0.5f / +0.5f)` (`AvatarController.cs:813,817`). At the 1 m distance SLNG's
   notch is 50 % of the distance (jumpy, cannot place the camera), at 10 m it is 5 % (needs 20 notches to double).
   Firestorm feels the same at every distance, and 8 notches take you from the default distance to mouselook, 12 notches
   from the default to the far limit.
3. **Zooming all the way in goes to mouselook and never moves the view centre; SLNG zooms toward the cursor and
   accumulates a pan offset.** LL/FS: once the zoom fraction drops below 0.25 the wheel calls `changeCameraToMouselook`
   (LL `cameraOrbitIn` `:997-1001`), and the third-person look-at point is fixed to the avatar, so the avatar never drifts
   off the framing. SLNG has no mouselook (clamp stops at 0.5 m, no switch), and with no focus target
   `ZoomTowardCursor` (`AvatarController.cs:197-225`) shifts `_panOffset` toward whatever is under the cursor on every
   notch; that offset is persistent (only Esc clears it) and does not cancel if the cursor moved between notches, so the avatar slides
   off-centre with use. LL/FS have no zoom-to-cursor at all.

Smaller but real: SLNG has no camera collision and no ground clamp, LL/FS have both (sim-sent collision plane, 0.5 m
above ground).

### 2. LL/FS behaviour in detail (source)

**Where the wheel goes.** `LLViewerWindow::handleScrollWheel` (LL `llviewerwindow.cpp:3295-3343`, FS identical): mouse capture
first, then the top control, then the whole UI root view; only if no UI view consumed it **and** the cursor is inside
the world view rect, `gAgentCamera.handleScrollWheel(clicks)` runs. So over any floater/UI the wheel scrolls the UI and
never zooms (SLNG does the same with `GuiGetHoveredControl`, `AvatarController.cs:788-790`). On Windows one "click" is
`z_delta / WHEEL_DELTA`, with deltas summed until a full 120 is reached, and a fast spin delivers several clicks at once
(`llwindowwin32.cpp:3236-3274`); the exponent in `handleScrollWheel` takes `clicks`, so N clicks in one event equal N single
notches. Wheel up = `clicks < 0` = zoom in.

**Third person, following the avatar (the normal case)** (`handleScrollWheel` `:2109-2116`):
`fraction = mTargetCameraDistance / (|CameraOffsetRearView| * CameraOffsetScale)`;
`fraction *= 1 - 2^(clicks/4)`; `cameraOrbitIn(fraction * |offset| * scale)`; inside `cameraOrbitIn` (`:989-1006`)
`mCameraZoomFraction = (mTargetCameraDistance - meters) / (|offset|*scale)`, mouselook if `< MIN_ZOOM_FRACTION` while zooming in,
then clamp to `[MIN_ZOOM_FRACTION, MAX_ZOOM_FRACTION]` = **[0.25, 8]** (LL `:59-61`).
- Default offset `CameraOffsetRearView` = (-3, 0, 0.75), magnitude 3.09 m (LL `app_settings/settings.xml:1344`; FS `settings.xml:3056`, FS Rear preset `app_settings/camera/Rear.xml` has the same). `CameraOffsetScale` = 1.0 (LL `:1359`, FS `:3071`).
  Third-person head offset (0,0,1) (`llagentcamera.cpp:148`). So distance limits are 0.77 m .. 24.7 m at defaults, measured from root+1 m.
- The base for the next notch is `mTargetCameraDistance`, which is the distance **after** the sim's collision plane shortened it
  (`:1855-1894`), so zooming out while pressed against a wall starts from the shortened distance, not from the remembered one.
- Wheel input is dropped while a camera animation runs (`if (mCameraAnimating) return;`, LL `:2098-2102`; `ZoomTime` = 0.4 s, LL `settings.xml:14146`, FS `:18643`).
- The distance eases with half-life 0.07 s (`:1894-1900`); then `updateCamera` (`:1431-1463`) smooths the camera position again
  (`CameraPositionSmoothing` default 1.0, LL `settings.xml:1407`, FS `:3119`), in avatar-relative space when following the avatar, in global space
  when a focus object is set; skipped for jumps > 50 m (`MAX_CAMERA_SMOOTH_DISTANCE`) and in build mode.
  Fraction of the remaining distance left after t seconds: 0.1 s -> 37 %, 0.2 s -> 14 %, 0.3 s -> 5 % (distance stage alone; the 0.02 s stage adds a little).
- Camera constraints: pushed to stay above ground by `getCameraMinOffGround()` = 0.5 m (LL `:2128-2141`), kept within draw distance of the agent
  (`:1982-2000`), plus the collision plane from the simulator (`process_camera_constraint`, LL `llviewermessage.cpp:4192-4198`; `CAMERA_COLLIDE_EPSILON` 0.1). The viewer itself does not ray-cast the scene for the third-person camera; I found no such code in `llagentcamera.cpp`.
- Following camera lag only when in the air (`DynamicCameraStrength` 2.0, LL `settings.xml:2648`), none while walking/standing.
- Look-at point while following the avatar is constant: agent + `FocusOffsetRearView` (1,0,1) (LL `calcThirdPersonFocusOffset` `:1681-1696`, `settings.xml:3204`), so zooming moves the camera along the offset line, the view target does not move.

**With a focus point (Alt+Click on object/ground, `mFocusOnAvatar == false`)** (`handleScrollWheel` else-branch `:2117-2121`):
`meters = |mCameraFocusOffsetTarget| * (1 - 2^(clicks/4))` -> same x0.841 / x1.189 per notch, relative to the camera-to-focus distance. Limits in `cameraOrbitIn` else-branch / `cameraZoomIn` (`:928-985`):
min = `OBJECT_MIN_ZOOM` **0.02 m** for a prim/object, `AVATAR_MIN_ZOOM` 0.5 m or the avatar-bounding-box value from `calcCameraMinDistance` (`:573-722`) for an avatar, `LAND_MIN_ZOOM` **0.15 m** for ground;
max = min(`MAX_CAMERA_DISTANCE_FROM_OBJECT` 496, draw distance - 1, region width - 16) and **at most 4x the current distance per call** (`:969-971`, MAINT-3154).
`mCameraFocusOffset` follows its target with `CAMERA_FOCUS_HALF_LIFE` = 0 (`:67`, comment shows 0.02 was tried), i.e. instantly, so only the 0.02 s position smoothing eases it.
For non-avatar objects there is also an FOV zoom: `calcCameraFOVZoomFactor` (`:1726-1752`) pushes the camera back and narrows the field of view (`setView(default / (1+factor))`, `:1480`), smoothed with half-life 0.07 s, so you can look at a prim's surface from inside its bounding volume without the camera clipping through it. The factor is "kept until the user changes focus" for land/avatars.
Alt-drag zoom (`LLToolCamera::handleHover`, LL `lltoolfocus.cpp:420-445`): `cameraZoomIn(0.99^dy)`, i.e. 1 % of the distance per pixel, plus 360 deg per view width orbit. Note this path scales `mCameraFocusOffsetTarget`, which the avatar-follow third person does not read; I did not trace what Alt+drag zoom does there and **did not verify** it.

**Keyboard zoom.** The Alt+W/S (or Up/Down) camera keys call `cameraOrbitIn(input * distance_to_focus / gFPSClamped)` once per frame (LL `:1288-1294`): movement of 100 % of the distance per second, so it decelerates exponentially toward the target (about e^-1 per second) and goes through the same smoothing/mouselook/limits as the wheel.

**While sitting.** If a script set a sit camera (`mSitCameraEnabled`), the camera position comes from that and ignores the zoom fraction (`:1820-1829`), so the wheel changes nothing visible. Otherwise a seated avatar zooms like a standing one (the offset is just rotated into the seat's frame, `:1842-1850`).

**Firestorm-specific changes (verified by diffing FS `llagentcamera.cpp`, `lltoolfocus.cpp`, `llviewerwindow.cpp` against the vendored LL copy and reading FS `settings.xml`).**
Constants (all of `:66-108`), the 2^(1/4) step, the 0.07 s / 0.02 s smoothing and the default values are **identical to LL**. Firestorm adds only:
- `FSDisableMouseWheelCameraZoom` (default false): turns wheel zoom off (FS `llagentcamera.cpp:2362-2406`, `settings.xml:23500`).
- Shift+wheel changes `FocusOffsetRearView.z` by 0.1 m per click and Ctrl+wheel changes `CameraOffsetRearView.z` by 0.1 m per click (third person, avatar follow) instead of zooming (FS `:2409-2425`, tag `FS:Zi`).
- `getCameraMaxZoomDistance(allow_disabled_constraints)` (FIRE-23470) so the camera-controls floater zoom respects the draw-distance cap (FS `:2331-2350`); `DisableCameraConstraints` defaults to false in both (LL `:2483`, FS `:4980`).
- RLVa wrappers (`getCameraOffsetScale`, `allowFocusOffsetChange`), `FSResetCameraOnMovement` (default true: movement resets the camera, like SLNG), save/load camera position (FIRE-7758), Singularity right-click mouse-walk in `lltoolfocus.cpp`.
- Settings that look like zoom knobs but are not wired to the wheel: `CameraMouseWheelZoom` (S32, default 5) and `CameraZoomFraction` (0.9) exist in `settings.xml:3008,3082`; I read no consumer for the first in `llagentcamera.cpp` and did not search the whole tree for either (GitHub code search for them was inconclusive), so **treat them as unverified/probably legacy.** `CameraZoomDistance` 2.5 / `CameraZoomFocusZOffset` 0.6 / `CameraZoomEyeZOffset` 1.0 (`FS:Zi`, `settings.xml:24702-24724`) are for "zoom to look at an avatar's face" (the radar double-click, which SLNG already has as `FocusOnAvatarFrontal`, 3.5 m), not for the wheel.
- I found **no** extra smoothing, easing, speed or step-size setting added by Firestorm for the wheel. Its feel comes from the unchanged LL mechanism above; any difference you perceive between "Firestorm" and "SLNG" is therefore the LL mechanism vs. SLNG, not a Firestorm tweak. (Not checked: Firestorm's `ui`-side defaults in `settings_per_account` / user overrides, such as a changed `CameraPositionSmoothing`.)

### 3. SLNG behaviour in detail (source, `app/scripts/AvatarController.cs` unless stated)

- **Wheel notch:** `_UnhandledInput` -> `ZoomBy(-0.5f)` for WheelUp, `ZoomBy(+0.5f)` for WheelDown (`:811-818`). `InputEventMouseButton.Factor` is ignored, so a high-resolution wheel or a touchpad that emits many small events zooms 0.5 m per event (**not verified** how Godot delivers these on this machine).
  `WheelOverride` (HUD editing) can consume the wheel first (`:813`).
- **Additive, instant:** `_zoom += delta` then `Position = targetPos + Basis.Z * _zoom` the same frame (`:154`, `:258-260`, `:1454`). No lerp, no target distance, nothing frame-rate dependent in the wheel/zoom path.
- **Limits:** `ZoomBy` and `ZoomTowardCursor` clamp to **0.5 .. 200 m** (`:201`, `:258`); `ZoomCamera` (pad buttons, Alt+W/S keys, seated W/S) clamps to **0.5 .. 50 m** (`:155`) - inconsistent (a camera at 100 m jumps to 50 m on the first pad/key zoom); the dolly keys `=`/`-` clamp 0.5 .. 200 (`:1411,1416`). Resting distance `RearDistance` default 4.0 m, slider 1..20 (`UI/CameraSettings.cs:51-53`); FOV default 60 deg (`:40`).
- **At the minimum:** the camera just stops 0.5 m from the pivot; no mouselook, no switch, no near-plane handling in this file. 0.5 m from a pivot at `FocusHeight` 1.8 is behind the head.
- **Pivot / framing:** the camera looks at the pivot (avatar position + `FocusHeight` 1.8 m, `:1431-1432`) plus `_panOffset`; the zoom moves it along the camera's back axis. So the avatar's head sits on the screen centre at every zoom (LL: look-at is the fixed point 1 m ahead / 1 m up, so the avatar sits below the middle). Inferred; not compared on screen.
- **Zoom toward cursor** (no focus target): `ZoomTowardCursor` adds `cursorOffset * (1 - newZoom/oldZoom)` to `_panOffset` on every notch (`:222-224`). In and out at the same cursor position cancel; moving the cursor in between does not, so `_panOffset` drifts. It is cleared only by `ResetCamera`/Esc (`:513` via `StartTransition`) and never by walking (`:1040-1045` resets orbit angles only).
- **With an Alt+Click focus target:** pure dolly along camera->target (`ZoomBy`, `:249-271`); if the 0.5 s glide is still running the whole ramp is shifted by the applied delta instead of being cancelled (LL/FS instead drop the notch while animating). Distance clamp is the same 0.5 .. 200, no per-object minimum, no FOV zoom, so an object or the ground cannot be approached closer than 0.5 m (LL: 0.02 m object / 0.15 m ground).
- **Glide on focus change:** `TransitionDuration` 0.5 s, smoothstep, from `StartTransition` (`:625`, `:521-552`); LL's `ZoomTime` is 0.4 s with the same smoothstep (LL `:1396`).
- **Alt-drag zoom:** `ZoomBy(orbitDelta.Y * 0.003 * 50)` = 0.15 m per pixel, additive (`:972-976`). LL: 1 % of the distance per pixel.
- **Keys:** Alt+W/S (`CameraZoomIn/Out`) 6 m/s additive (`:49,132-133`, `src/SLNG.Core/Input/KeyActions.cs:66-67`); `=`/`-` 15 m/s additive (`:1411,1416`); seated W/S 6 m/s (`:1060-1061`). Per-second, so frame-rate independent, but linear. LL: 100 % of the distance per second, decelerating.
- **Camera pad (`UI/CameraHUD.cs`):** `ZoomStep` 0.5 m **per frame** x `ZoomSpeed` (0.1..3) while a pad button is held (`:28`, `:345-354`) = 30 m/s at 60 fps, frame-rate dependent, and it hits the clamp within a second.
- **Collision / ground:** none. No ray-cast, no sim camera-constraint handling and no terrain clamp in `_Process` (`:1405-1471`); a repo grep for camera collision/terrain/spring-arm found nothing. The camera can pass through walls and the ground.
- **Sitting:** the movement keys are repurposed (A/D swing, W/S zoom, `:1055-1062`); the wheel zooms like standing; a script's sit camera is not implemented here (I did not search elsewhere for it).
- **Interest radius side effect:** `camFar = max(128, camera-to-avatar + draw distance)` capped at 512 is sent to the sim (`:1466-1470`), so a very far camera streams more.

### 4. Comparison

| Aspect | Firestorm (value, source) | SLNG (value, source) | Felt difference / likely cause |
|---|---|---|---|
| Wheel step | x 2^(-1/4) = 0.841 in, x 2^(1/4) = 1.189 out per notch (LL/FS `handleScrollWheel`; FS `:2362`) | -0.5 m / +0.5 m (`:813,817`) | SLNG is too coarse near the avatar (1 m -> 50 %) and too slow far away (10 m -> 5 %); FS feels uniform at every distance |
| Easing | Distance lerp half-life 0.07 s (LL `:70,1900`, FS `:2074`) + position smoothing half-life 0.02 s (LL `:1438`, FS `:1590`), dt-based | none, instant (`:154`, `:1454`) | SLNG snaps in one frame per notch, which reads as stepping; FS glides ~0.25 s. Probably the largest single factor |
| Near limit | zoom fraction 0.25 = 0.77 m at defaults, then mouselook (LL `:59,997-1001`) | clamp 0.5 m, nothing happens (`:155,201,258`) | FS has a natural end of the range; SLNG's wheel dead-ends and, 0.5 m behind a head pivot, looks at the back of the head |
| Far limit | 8 x 3.09 = 24.7 m third person (LL `:61`); with focus min(496, draw dist-1, region width-16) and <= 4x per notch (LL `:969-971,2066`) | 200 m wheel, 50 m pad/keys (`:155,201,258`) | SLNG's range is huge and the useful part is 40 notches deep; FS is 12 notches deep |
| Notches default -> near / -> far | 8 / 12 (2^(n/4)) | ~7 / ~39 to 24.7 m (0.5 m steps from 4 m) | |
| Default distance | 3.09 m from root+1 m (settings 1344) | 4.0 m from pivot 1.8 m (`UI/CameraSettings.cs:53`) | different framing from the start; SLNG shows the avatar centred, FS lower in the frame (inferred) |
| Zoom centre | fixed look-at (agent + (1,0,1)); no zoom-to-cursor (LL `:1681-1696`) | pivot + persistent `_panOffset`, wheel zooms toward cursor (`:197-225`) | SLNG's view slides off the avatar with use; FS stays predictable |
| Wheel over UI | UI root view consumes first, camera only if cursor in world view (LL `llviewerwindow.cpp:3295-3343`) | `GuiGetHoveredControl` gate (`:788-790`) | same |
| Wheel while camera animating | dropped (LL `:2098`) | ramp shifted, still applied (`:262-270`) | SLNG feels more responsive here; FS's is a deliberate lockout for 0.4 s |
| With focus (Alt+Click) | x0.841 / x1.189 of camera-focus distance; min 0.02 m object / 0.15 m land / 0.5 m avatar; FOV zoom on objects (LL `:928-985,1726-1752`) | 0.5 m additive, min 0.5 m, no FOV zoom (`:258`) | FS lets you go to the surface of small things; SLNG cannot get closer than 0.5 m |
| Alt-drag zoom | 0.99^dy (1 % per pixel) (LL `lltoolfocus.cpp:437-443`) | 0.15 m per pixel additive (`:976`) | at 3 m a 20 px drag moves 3 m in SLNG vs 0.4 m in FS; SLNG is jumpy |
| Key zoom (Alt+W/S) | 100 % of distance per second, decelerating (LL `:1288-1294`) | 6 m/s linear (`:49,132`) | similar speed at 6 m, but linear vs eased |
| Pad zoom | n/a (floater uses the same path, FS FIRE-23470) | 0.5 m per frame x ZoomSpeed (`CameraHUD.cs:28`) | frame-rate dependent in SLNG |
| Collision | sim collision plane shortens the distance; wheel base is the shortened distance (LL `:1855-1894`, `llviewermessage.cpp:4192`) | none | camera clips through walls in SLNG |
| Ground | camera >= 0.5 m above land (LL `:2128-2141`) | none | camera can go under terrain |
| While sitting | same wheel; a script sit camera overrides it (LL `:1820-1829`) | same wheel, A/D/W/S camera keys (`:1055-1062`) | similar |
| Firestorm-only | Shift/Ctrl+wheel: offset z +-0.1 m/click; `FSDisableMouseWheelCameraZoom`; no wheel-feel changes (FS `:2362-2425`) | none | nothing in FS explains a better feel beyond LL's own mechanism |
| Frame-rate dependence | none (dt-based interpolants, `llcriticaldamp.cpp`) | wheel none (instant); pad zoom per frame (`CameraHUD.cs:28`) | |

### 5. Recommendations, ranked

1. **Make the wheel multiplicative: x 2^(-1/4) in, x 2^(1/4) out per notch.** Do it in `ZoomBy` / `ZoomTowardCursor`
   (`AvatarController.cs:249-271`, `:197-225`) by replacing the `+-0.5f` call sites at `:813,817` with a factor (compute `delta = _zoom * (f - 1)`).
   Optional: scale by `InputEventMouseButton.Factor` and the exponent, so touchpads do not over-zoom. Risk: very small distances need the
   new minimum (rec. 4), otherwise the last notches crawl; the `RearDistance` slider range 1..20 stays valid. Smallest change, large effect.
2. **Ease the distance toward a target.** Keep the wheel/keys writing `_zoomTarget`; each frame `_zoom += (_zoomTarget - _zoom) * (1 - 2^(-dt/0.07))`,
   in `_Process` before `Position` is computed (`:1454`); `UpdateTransition` (`:541`), `StartTransition` (`:498`) and `SetCameraSettings`/Rear slider re-snap (`:90,873`) must set both `_zoom` and `_zoomTarget`.
   Optionally add LL's second 0.02 s position stage (low value, extra complexity; skip first). Risk: every place that reads or writes `_zoom`
   (about 15, grep `_zoom`) must be audited; the focus glide's "shift the ramp" trick (`:262-270`) must also shift the target; `FocusOn` uses `Position.DistanceTo` so it must use the eased value.
3. **Stop zoom-to-cursor accumulating an offset in the ordinary follow mode** (or drop it): in `ZoomBy` (`:252-256`) call a pure dolly there too, as LL/FS do;
   if cursor zoom is wanted keep it only for Ctrl+wheel or decay `_panOffset` back to 0 with the same 0.07 s lerp when walking. Risk: this removes a behaviour the maintainer may have asked for earlier (the `ZoomTowardCursor` doc says it approximates the classic editor convention); ask before removing.
4. **Align limits and the minimum:** wheel/pad/keys all 0.5 .. 200 (fix `ZoomCamera` clamp, `:155`) or, to match LL, cap the follow-mode wheel at 8 x `RearDistance` (about 32 m at 4 m) and below ~0.25 x `RearDistance` (1 m) hide the avatar / treat as the end of the range. Per-focus-type minimums: 0.02 m for an Alt+Click on an object, 0.15 m on terrain, 0.5 m on an avatar (replace the `0.5f` in `ZoomBy`, `AimOrbitAt`). Risk: with Godot's default near plane (0.05) a 0.02 m distance needs the near plane lowered or the FOV-zoom trick (LL `calcCameraFOVZoomFactor`); do the minimum cut first and the FOV zoom only if wanted.
5. **Frame-rate independent pad and key zoom:** `CameraHUD` `ZoomStep` is per frame (`:28`); multiply by `delta`, and make key zoom proportional to distance (`_zoom * k * dt`, k about 1/s as LL's `distance / FPS`) in `ApplyCameraKeys` (`:132-133`) and the dolly keys (`:1408-1417`). Risk: tuning only; existing persisted `ZoomSpeed` values keep their meaning if the base is chosen to match the current feel at 4 m.
6. **Lock the wheel for 0.4 s while a focus glide runs** (LL `:2098`) - optional; SLNG's current shift-the-ramp behaviour is arguably nicer. Skip unless the glide + wheel combination feels odd in testing.
7. **Camera collision and ground clamp:** a short ray-cast from the pivot to the desired camera position (Godot physics, layers `Objects | Terrain`, shrink `_zoom` by the hit distance minus 0.1 m, as `CAMERA_COLLIDE_EPSILON`) and a 0.5 m terrain clamp. Largest change (needs the physics layers, interacts with rec. 2 because LL bases the next notch on the shortened distance) and is a different feature ("camera clips through walls"), so only if the maintainer asks. LL gets this from the sim's `CameraConstraint` plane; whether SLNG's `GridSession` receives that packet was **not checked**.
8. **Mouselook at the end of the range** is the true LL behaviour but SLNG has no mouselook (`KeyboardPreferencesPage` lists it as missing); out of scope here.

Suggested order if one ticket: 1 + 2 together (they define the feel), then 4 (clamp consistency and minimum), then 5, then 3 after asking.

### 6. Could NOT verify

- Actual feel: neither viewer was run; the perceptual causes above are inferred from constants and structure.
- Whether Alt+drag zoom works at all in LL/FS avatar-follow mode (it scales `mCameraFocusOffsetTarget`, which avatar-follow does not read); not traced.
- Whether `CameraMouseWheelZoom` / `CameraZoomFraction` have any consumer in Firestorm (search inconclusive, probably legacy).
- Any user-side Firestorm preference (e.g. Move & View camera sliders) that overrides `CameraPositionSmoothing` or the offset; only defaults were read.
- How Godot 4.7 delivers wheel events on this machine (`Factor`, event count per physical notch).
- Whether SLNG receives/uses the sim's `CameraConstraint` packet.
- The Firestorm lines are from `master` of 2026-10-03, not a release tag; a given Firestorm release may differ.

## Technical Specs & Affected Files (for the follow-up, if approved)
- `app/scripts/AvatarController.cs` (`ZoomBy`, `ZoomTowardCursor`, `ZoomCamera`, `_UnhandledInput`, `_Process` camera block, `UpdateTransition`, `SetCameraSettings`)
- `app/scripts/UI/CameraHUD.cs` (`ZoomStep`)
- `app/scripts/UI/CameraSettings.cs` (limits, maybe a "zoom smoothing" setting)
- `src/SLNG.Core/Input/KeyActions.cs` (only if key behaviour changes)
- A pure helper in `SLNG.Core` (notch factor, eased step) with xUnit tests, as the repo convention asks.

## Sub-tasks / Progress
- [x] Read LL zoom path, FS differences, SLNG zoom path
- [ ] Decide which recommendations to build (maintainer)
- [ ] Implement 1 + 2 with tests; AppVersion bump per the app rules
