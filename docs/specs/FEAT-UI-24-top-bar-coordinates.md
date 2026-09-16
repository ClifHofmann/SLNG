# [FEAT-UI-24] Coordinate and FPS Readout in the Top Bar

- **Feature ID:** `FEAT-UI-24`
- **Track:** `ui`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Provide a live region and coordinate readout in the viewer's top bar (`TopMenu`) left-aligned right next to the menu bar, displaying the Region / Parcel name and integer SL coordinates (`📍 Region / Parcel (X, Y, Z)`).
A dedicated `📋` copy button and clickable label copy the SLURL (`secondlife://<Region>/<X>/<Y>/<Z>`) to the clipboard with toast feedback.
The FPS readout is right-aligned on the far right and can be toggled on/off via the View menu (`ui.menu.show_fps_in_top_bar`), persisting the setting in `user://preferences.cfg`.

## Acceptance Criteria
- [x] Top bar displays region and parcel name with integer SL coordinates (`Region / Parcel (X, Y, Z)`) when connected inworld, left-aligned next to the menu bar.
- [x] Dedicated copy button (`📋`) and clickable location button copy the SLURL to clipboard and display toast feedback.
- [x] FPS readout is right-aligned and can be toggled on/off via the View menu ("Show FPS in Top Bar").
- [x] FPS visibility setting persists across sessions in `preferences.cfg`.
- [x] Stays empty/hidden before login and on disconnect.
- [x] Updates live while moving/walking and immediately reflects region/parcel changes.
- [x] All UI strings localized in both `en-US.json` and `de-DE.json`.
- [x] Automated and headless self-tests pass.

## Technical Specs & Affected Files
- `src/SLNG.Net/GridSession.cs`: Track `CurrentParcelName`, listen to `ParcelProperties`, provide `RequestCurrentParcelProperties`.
- `app/scripts/UI/TopMenu.cs`: Left-align location readout next to menu bar, add `_copySlurlBtn`, add expanding spacer, add View menu check item for FPS toggle, add `SetShowFps`.
- `app/scripts/UI/UiSettings.cs`: Add `ShowTopBarFps` persisted to `preferences.cfg`.
- `app/scripts/Boot.cs`: Sample local agent `TransformComponent.Position` and pass parcel name, wire FPS preference, bump `AppVersion` to `v0.22.184-alpha`.
- `app/i18n/en-US.json` & `app/i18n/de-DE.json`: Add localization keys for tooltips, copy feedback, and View menu toggle.
- `docs/ROADMAP.md`: Update task status.

## Sub-tasks / Progress
- [x] Track parcel name in `GridSession.cs`.
- [x] Add left-aligned layout with `_copySlurlBtn` and expanding spacer in `TopMenu.cs`.
- [x] Add FPS toggle in `ViewMenu` and persist in `UiSettings.cs`.
- [x] Add localization keys in `en-US.json` and `de-DE.json`.
- [x] Bump `AppVersion` to `v0.22.184-alpha`.
- [x] Verify builds, unit tests, and Godot headless self-tests.
