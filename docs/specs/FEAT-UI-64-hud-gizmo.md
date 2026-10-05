# [FEAT-UI-64] Edit a worn HUD on the HUD plane

- **Feature ID:** `FEAT-UI-64`
- **Track:** `ui` / `render`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Asked 2026-10-05: editing a HUD should mean moving, stretching and turning it on the HUD plane, like in the other
viewers. After FEAT-UI-61 a worn HUD can be selected and opens the edit window, but the handles only existed in
the main 3D world.

## What the reference viewer does (read from `secondlife/viewer`, `indra/newview`)
- `llselectmgr.cpp:180` -- a selection containing a HUD attachment is `SELECT_TYPE_HUD`.
- `llmaniptranslate.cpp:1790`, `:1832-1835` -- for a HUD the "camera direction" is the HUD's X axis, and the arrow
  length in HUD units is `arrowPixels / worldViewHeight / HUDCurZoom`: constant on screen at any HUD zoom.
- `llmaniprotate.cpp:1199-1206` -- the same substitution for the rotation rings (`mCenterToCam = (-1/zoom, 0, 0)`).
- `llmanipscale.cpp:1736` -- the stretch snap guide uses a 0.25-unit grid resolution for a HUD instead of the build grid (no snap guide is drawn here, see below).
- `llagentcamera.cpp:940-943` -- the wheel in build mode zooms the HUD (done in FEAT-UI-23).

## Design here
One `SelectionGizmo3D`, two homes. `Attach` asks `IsHudEntity`; for a HUD prim it re-parents the gizmo under the
HUD overlay's root and swaps its camera for the overlay's orthographic one (`EnterSpace`). `Detach` puts it back
(`LeaveHudSpace`), so it is never left behind in the overlay.

- **Frame.** A HUD prim stores its offset from its attach point's anchor. `AvatarRenderer.TryGetHudFrame` returns that
  anchor (unrotated) in the overlay's world, the same map `PositionHudNode` uses, so the gizmo's existing worn-item
  maths (`TryGetWornFrame`, `StoreSlWorldPosition`, `Send`) turns an offset into a position and back unchanged.
- **Camera.** Orthographic: world length per pixel is `camera.Size / viewport height`. The overlay viewport is in
  physical pixels and the mouse in UI units, so the mouse, the grab radius and the handle length are multiplied by
  `UiScale.Current` (`PixelScale`), the conversion `AvatarRenderer.TryHitHud` already makes.
- **Handles offered.** Seen down SL X, the X arrow is a dot, the XY/XZ planes are edge-on lines and the Y/Z rings are
  lines, so only these exist: Y and Z arrows and the YZ plane (move), the X ring (turn in the screen plane), the
  Y/Z face and corner handles (stretch).
- **Off in a HUD:** metre grid, ruler and linear snapping (a HUD is one unit tall).
- **Stretch needed a renderer fix.** `_hudContent` is the "this HUD prim is already built" signature; it left out
  the scale, which is baked into the vertices, so a stretch never rebuilt the mesh. It now includes it, and a build
  that has been overtaken by a newer size is dropped.

## Acceptance Criteria
- [ ] Right-click a worn HUD, Edit: move arrows show on the HUD prim (and nowhere in the world).
- [ ] Dragging the Y or Z arrow, or the YZ square, moves it with the cursor; the place survives a relog.
- [ ] Ctrl: the screen-plane ring turns it; Ctrl+Shift: it stretches and visibly grows or shrinks.
- [ ] The handles keep their on-screen size when the wheel zooms the HUD view, at UI scale 100 % and 200 %.
- [ ] Closing the edit window removes the handles from the HUD; selecting a world object puts them back in the world.
- [ ] A linked HUD (root + children): moving the root takes the children along.

## Not in scope
- The reference's snap guides for a HUD (0.25-unit grid), free rotation inside the ring, and auto-framing of the HUD on selection.
- Gizmo for a HUD while the HUD layer is hidden.

## Technical Specs & Affected Files
- `app/scripts/UI/SelectionGizmo3D.cs` -- `IsHudEntity`, `HudSpace`, `EnterSpace`, `LeaveHudSpace`, `PixelScale`,
  orthographic sizing, handle filtering, snapping off.
- `app/scripts/AvatarRenderer.cs` -- `IsHudAttachment`, `TryGetHudFrame`, `TryGetHudSpace`; scale in `_hudContent`;
  stale-build guard in `LoadHudContentAsync`.
- `app/scripts/Boot.cs` -- wiring; `AppVersion` v0.26.66-alpha.

## Sub-tasks / Progress
- [x] gizmo lives in the overlay for a HUD selection
- [x] HUD frame and space from `AvatarRenderer`
- [x] stretch rebuilds the HUD mesh
- [ ] built and run (needs .NET 8 + Godot 4.7; not available in the session that wrote this)
- [ ] confirmed in-world
