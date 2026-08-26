# [FEAT-UI-10] Settings UI Redesign & Footprint Reduction

- **Feature ID:** `FEAT-UI-10`
- **Track:** `ui`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
The main user interface is too space-consuming ("platzraubend") for Second Life multitasking workflows. This feature reduces the visual footprint of the UI by tightening margins and headers. Additionally, it refactors the classic `PreferencesWindow` by breaking out the Graphics and Photo tools into a dedicated, tear-off floating window (`GraphicsSettingsWindow`). This enables users to adjust settings while observing the 3D world simultaneously. Finally, it prepares `ToolbarSettings` with `ToolbarDockPosition` for Firestorm-like dockable toolbars.

## Acceptance Criteria
- [x] Margins in `SLNGWindow` headers are tightened.
- [x] `TopMenu` panel margins are reduced.
- [x] `PreferencesWindow` has a smaller default footprint.
- [x] Graphics and Quality settings are reintegrated into `PreferencesWindow` tabs for a unified settings UI.
- [x] `ToolbarSettings` supports `ToolbarDockPosition`.

## Technical Specs & Affected Files
- `app/scripts/UI/SLNGWindow.cs`
- `app/scripts/UI/TopMenu.cs`
- `app/scripts/UI/PreferencesWindow.cs`
- `app/scripts/Boot.cs`
- `app/scripts/UI/ToolbarSettings.cs`

## Sub-tasks / Progress
- [x] UI Margin reduction
- [x] Architecture split of PreferencesWindow
- [x] Implementation of `GraphicsSettingsWindow`
- [x] Wiring in `Boot.cs`
- [x] Dock Position Preparation
