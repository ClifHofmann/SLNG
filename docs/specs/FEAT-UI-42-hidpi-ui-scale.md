# [FEAT-UI-42] The UI follows the display's own scale

- **Feature ID:** `FEAT-UI-42`
- **Track:** `ui`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Reported 2026-10-02: on a high-resolution laptop the text is tiny. The UI scale (FEAT-UI-07) defaulted
to 1.0 whatever the screen, topped out at 1.6, and was applied as `Scale` on each `SLNGWindow` only -- the
top bar, login screen, tooltips, nametags and the rest of the HUD never scaled, and `project.godot` sets no
DPI or stretch option. Goal: the scale starts at the operating system's own, the user's choice wins, and it
reaches the whole interface once, without touching the 3D render.

## Decision: root `Window.ContentScaleFactor` (candidate A), not per-window `Scale` (B)

`UiScale.Apply` sets `ContentScaleMode = Disabled` and `ContentScaleFactor = scale` on the root window (the
mechanism the Godot editor uses for its own UI). Evidence is from a throwaway Godot 4.7 project run in a real
window (a `CanvasLayer` with a `ColorRect`, `Label`, `Button`, a `PopupMenu`, a `Camera3D` and a box), at
factor 1.0 and 2.0 in an 800x600 window, with screenshots:

| Measured at factor 2.0 | Result |
|---|---|
| `Window.Size` / `DisplayServer.WindowGetSize` | 800x600 -- physical, unchanged |
| `Viewport.GetVisibleRect().Size` | 400x300 -- logical (physical / 2) |
| `Viewport.GetMousePosition()` after `Input.WarpMouse(200,200)` | (100,100): mouse is logical; `Input.WarpMouse` is physical |
| `Camera3D.ProjectRayOrigin(visible/2)` and `UnprojectPosition(origin)` | consistent with the visible rect (centre = 200,150) |
| Control in a `CanvasLayer` | drawn at 2x, canvas transform itself stays identity, root final transform carries the scale |
| Text | crisp (font oversampling follows the factor) |
| Embedded `PopupMenu` (`gui/embed_subwindows` default) | drawn at 2x and clamped to the logical window; tooltips use the same path |
| 3D box | rendered at the physical 800x600; `get_image()` of the root is 800x600 |

Why not B: it needs the scale added by hand to every UI root (TopMenu, login screen, loading screen, tooltips,
popups, nametags, every CanvasLayer), tooltips and popups are engine windows with no `Scale` at all, and it is
the reason the UI was half-scaled. A is one line on one node and covers all of them, including future ones.

What A changes for the code, all handled (see "Affected files"):

- Control coordinates, visible rect, mouse position, `ProjectRayOrigin` and `UnprojectPosition` are ALL logical
  and agree with one another. Every raycast-from-mouse (AvatarController, CursorManager, ObjectSelectionController,
  SelectionGizmo3D, DepthOfField, nametags) therefore needs no change.
- Things that must stay at the real render resolution used the visible rect and would have shrunk: the mirror's
  SubViewport (MirrorReflection), the pixels-per-radian texture LOD figure (ObjectRenderer), the snapshot size
  label, and the worn-HUD SubViewport (AvatarRenderer, which would have rendered at the logical size and been
  enlarged -- blurry). They use `UiScale.RenderSize` / a container sized to the physical pixels and scaled 1/s.
- `Input.WarpMouse` is physical: the Alt-orbit "cursor reappears where the drag started" now uses
  `Viewport.WarpMouse`. The Alt-orbit mouse delta is multiplied by the scale and the free camera reads
  `ScreenRelative`, so camera feel does not depend on the UI scale.
- `SLNGWindow` no longer sets its own `Scale` (it would scale twice). `StatsOverlay` likewise.

### Display scale detection -- the ticket's premise was wrong on Windows
`DisplayServer.ScreenGetScale` returns **1.0 on Windows at every setting** (measured here on a 2880x1800
laptop panel at 150 %: scale 1.0, `ScreenGetDpi` 144). It is only implemented on macOS, Wayland, Android and
iOS. Windows exposes its scale through the monitor DPI (96 = 100 %), so `UiScalePolicy.DetectOsScale` uses a
reported scale above 1 where there is one, else DPI / 96 on Windows only (on X11 the DPI is the panel density,
not a user setting), else 1.0; rounded to 0.05, clamped to the range. Zero, negative, NaN, infinity all fall
back to 1.0. `display/window/dpi/allow_hidpi` is Godot's default (true) and is what makes the process
per-monitor DPI aware (the 144 reading proves it is in effect); it is not written into `project.godot`
because Godot drops default-valued settings whenever it re-saves that file (every `--selftest` run does).

Following the window to another screen: `Window.dpi_changed` is not relied on for Windows, so `UiScaleWatcher`
re-reads the display scale once a second and only acts while the scale is automatic. Not testable on the one
screen available here.

## Behaviour
- `ui_scale_auto` (new) + `ui_scale` (existing) in `[display]` of `preferences.cfg`. Effective scale =
  automatic ? OS scale : `ui_scale`, clamped to 0.8..4.0 (was 0.8..1.6).
- Fresh install: no keys -> automatic. **Existing `ui_scale` with no `ui_scale_auto` key -> manual**, so a
  saved 1.25 stays exactly 1.25. Unticking "automatic" the first time keeps the scale in force as the manual
  value, so nothing jumps; ticking it again leaves the last manual number saved for later.
- Applied at the top of `Boot._Ready`, before any login-screen Control exists, so the first frame is already at
  the right size. Preferences > Display: "Automatic (follows the display)" checkbox, slider (applied on release,
  because applying while dragging resizes the dragged control and the handle chases the cursor), the effective
  percentage and "Display scale detected: N %".
- Developer aid: `-- --ui-scale=2` forces a scale for one run, in memory only.

## Window geometry -- no migration needed
`window_geometry` positions were always PHYSICAL pixels (a scaled window's `Position` was never divided by its
`Scale`) and sizes were UI units. They are still stored that way: restore divides the position by the current
scale, save multiplies by it. A window saved at 1.25 under the old mechanism lands on the same physical spot,
and a later scale change never makes the saved value stale. `ClampToViewport` (FEAT-UI-11) works on the logical
visible rect and still pulls strays back. On a live scale change each top-level window keeps its physical place
(`Position *= old/new` before the viewport resizes; windows nested in another window are left alone).

## What is scaled, what is not
- Scaled: every Control, popup and tooltip (top bar, login and loading screens, all `SLNGWindow`s, menus, HUD
  parts, the stats overlay, the radar/map Controls) and **nametags** (Controls on a CanvasLayer positioned with
  `UnprojectPosition`).
- Not scaled, deliberately: the 3D render and everything in it, the worn-HUD attachments' render (stays at
  physical resolution, drawn 1:1), the build gizmo's ruler/dial `Label3D`s (world-space text with a hand-tuned
  fixed pixel size; the gizmo's handle size and picking are already in UI units and do scale).
- Known limit, not new: the login panel needs about 640 UI units of height; a small window at a high scale
  clips it (a 1152x648 window at 2.0 is 324 units high). On the laptop panel in question (2880x1800 at 150 %)
  it fits.

## Acceptance Criteria
- [x] (1) No saved choice: starts at the OS scale (Windows via DPI; 1.5 measured here), 1.0 fallback.
- [x] (2) The user's slider choice wins and is saved; explicit automatic state; range 0.8..4.0.
- [x] (3) The scale reaches the whole interface through the root window, without double scaling (selftest).
- [x] (4) Selftest passes at any scale; window insets verified at 1.0, 1.25 and 2.0 in memory.
- [x] (5) 3D untouched: Disabled stretch mode; mirror, LOD figure, HUD viewport and snapshots stay physical.
- [ ] Seen on the actual high-DPI laptop (not possible from this session).

## Technical Specs & Affected Files
- `src/SLNG.Core/UiScalePolicy.cs` + `tests/SLNG.Core.Tests/UiScalePolicyTests.cs` -- pure rules.
- `app/scripts/UI/UiScale.cs` (apply, detect, `RenderSize`), `UiScaleWatcher.cs`, `UiSettings.cs`,
  `DisplayPreferencesPage.cs`, `SLNGWindow.cs`, `StatsOverlay.cs`, `Boot.cs`, `SelfTest.cs`.
- Physical-pixel consumers: `AvatarRenderer.cs` (HUD container, click), `MirrorReflection.cs`,
  `ObjectRenderer.cs`, `SnapshotWindow.cs`, `AvatarController.cs`, `FreeCamera.cs`.
- `app/i18n/en-US.json`, `de-DE.json`; `docs/BENUTZERHANDBUCH.md`.
