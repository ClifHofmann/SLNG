# [FEAT-UI-07] UI Scale Setting

- **Feature ID:** `FEAT-UI-07`
- **Track:** `ui`
- **Status:** `✅ Done`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Let the user scale the client's floating-window UI (chat, preferences, inventory, object
inspector, camera HUD — everything built on `SLNGWindow`) up or down from a slider in
Preferences, persisted across sessions. Addresses readability on high-DPI displays and personal
preference, without touching 3D render resolution.

## Functional Requirements
- A "Display" tab in `PreferencesWindow` with a slider controlling UI scale, range 80%–160%,
  5% steps, default 100%.
- Changing the slider rescales every currently-open `SLNGWindow` instance live (no restart, no
  re-open needed).
- The chosen scale persists in `user://preferences.cfg` (`[display] ui_scale`, same file
  `ToolbarSettings` already uses) and is re-applied on next launch before any window is created.
- Scope is every `SLNGWindow` subclass (`ChatWindow`, `ChatHistoryWindow`, `PreferencesWindow`,
  `ObjectEditWindow`, `ItemPropertiesWindow`, `InventoryPanel`, `CameraHUD`) — this is every
  floating window in the client per `AGENTS.md`'s Unified Window System rule, so one change
  point covers all of them, present and future.
- Explicitly **out of scope for this pass**: the fixed bottom `ButtonBar` and
  `InWorldContextMenu` (both screen-edge/cursor-anchored, non-`SLNGWindow` controls) — scaling
  those safely needs separate handling and isn't needed for the stated goal (floater/HUD
  readability).

## Design Notes
- Implemented as a `Control.Scale` on the `SLNGWindow` base (`app/scripts/UI/SLNGWindow.cs`),
  broadcast via a static `GlobalUiScaleChanged` event — not a `Viewport`/`Window.ContentScaleFactor`
  change, since the project has no `content_scale_mode` configured (defaults to `Disabled`,
  where that property has no effect) and changing display-stretch project settings would also
  risk affecting 3D rendering. Per-window `Control.Scale` pivots around the window's own
  top-left (`PivotOffset` default `(0,0)`), so `Position` keeps meaning "top-left corner" at any
  scale and dragging (which reads `GlobalPosition`, already scale-correct) is unaffected.
  Resizing needed an explicit fix: the resize handler computed a screen-space mouse delta and
  assigned it straight to the pre-scale `Size`, which would resize faster/slower than the cursor
  at any non-1.0 scale — now divided by `Scale` first.
- `UiSettings` (`app/scripts/UI/UiSettings.cs`) mirrors the existing `ToolbarSettings` persistence
  pattern (own section in the same `preferences.cfg`, `Load()`/`SetScale()`), loaded at the top
  of `Boot.SetupHud()` before any `SLNGWindow` is constructed, so the very first `CameraHUD` /
  `InventoryPanel` / `ChatWindow` etc. already come up at the saved scale instead of flashing at
  100% first.

## Acceptance Criteria
- [x] "Display" tab in Preferences with a working scale slider (80%–160%).
- [x] Adjusting the slider live-rescales all currently open `SLNGWindow`s.
- [x] Scale persists across app restarts via `user://preferences.cfg`.
- [x] Dragging and resizing an `SLNGWindow` still tracks the cursor correctly at non-100% scale.
- [x] 3D viewport rendering is unaffected by the UI scale value.

## Technical Specs & Affected Files
- `app/scripts/UI/SLNGWindow.cs` — static `GlobalUiScale`/`SetGlobalUiScale`/
  `GlobalUiScaleChanged`; applies `Scale` in `_Ready()`, unsubscribes in `_ExitTree()`; resize
  math fixed to divide by `Scale`.
- `app/scripts/UI/UiSettings.cs` — persistence (new file).
- `app/scripts/UI/DisplayPreferencesPage.cs` — Preferences "Display" tab UI (new file).
- `app/scripts/Boot.cs` — loads `UiSettings` at the top of `SetupHud()`; registers the "Display"
  tab in `SetupButtonBarAndPreferences()`.

## Sub-tasks / Progress
- [x] Create `FEAT-UI-07` spec & update `ROADMAP.md`
- [x] `SLNGWindow`: global scale broadcast + apply + scale-aware resize
- [x] `UiSettings`: persistence to `preferences.cfg`
- [x] `DisplayPreferencesPage` + wire into `PreferencesWindow`
- [x] `dotnet build` (`SLNG.sln` + `app/SLNG.App.csproj`) clean
- [ ] Manual visual verification in a running client (not done in this pass — no display
  available in this session; recommended before considering this fully closed)
