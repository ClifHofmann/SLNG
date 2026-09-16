# [FEAT-UI-24] Coordinate and FPS Readout in the Top Bar

- **Feature ID:** `FEAT-UI-24`
- **Track:** `ui`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Provide a live region and coordinate readout in the viewer's top bar (`TopMenu`) alongside a dedicated FPS performance indicator, matching standard SL/Firestorm viewer usability.
Clicking the coordinate readout copies the SLURL (`secondlife://<Region>/<X>/<Y>/<Z>`) to the clipboard with visual toast feedback.
The FPS readout provides instant framerate feedback and allows opening the Performance Stats overlay upon clicking.

## Acceptance Criteria
- [x] Top bar displays region name and integer SL coordinates (`Region (X, Y, Z)`) when connected inworld.
- [x] Stays empty/hidden before login and on disconnect.
- [x] Updates live while moving/walking and immediately reflects region changes after teleport or region crossings.
- [x] Clicking on the coordinate readout copies the SLURL to clipboard and shows a feedback toast.
- [x] Dedicated FPS indicator is integrated into the top bar, color-coded by performance tier, and clicking it toggles the Performance Stats overlay.
- [x] All UI strings localized in both `en-US.json` and `de-DE.json`.
- [x] Automated and headless self-tests pass.

## Technical Specs & Affected Files
- `app/scripts/UI/TopMenu.cs`: Add `_locationBtn`, `_fpsBtn`, separators, SLURL copy handler, and public update methods.
- `app/scripts/Boot.cs`: Sample local agent `TransformComponent.Position` and FPS at ~5 Hz tick via `UpdateHud()`, wire SLURL copy toast, and bump `AppVersion`.
- `app/i18n/en-US.json` & `app/i18n/de-DE.json`: Add localization keys for tooltips and copy feedback.
- `docs/ROADMAP.md`: Update task status.

## Sub-tasks / Progress
- [x] Create feature branch and spec.
- [x] Add UI elements and logic to `TopMenu.cs`.
- [x] Wire periodic ~5 Hz tick in `Boot.cs`.
- [x] Add localization keys in `en-US.json` and `de-DE.json`.
- [x] Bump `AppVersion` to `v0.22.183-alpha`.
- [x] Verify builds, unit tests, and Godot headless self-tests.
