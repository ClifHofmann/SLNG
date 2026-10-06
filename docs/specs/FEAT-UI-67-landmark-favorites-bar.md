# [FEAT-UI-67] Landmark Favorites Bar & Dedicated Landmarks Window

- **Feature ID:** `FEAT-UI-67`
- **Track:** `ui`
- **Status:** `🧪 Review`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Firestorm-parity landmark favorites bar docked horizontally beneath the top menu bar, alongside a dedicated Landmarks floating window (`SLNGWindow`) for managing, filtering and teleporting to saved landmarks. Allows dragging landmarks directly to the favorites bar, context-menu actions, and persistent storage of favorites per resident.

## Acceptance Criteria
- [x] `LandmarkFavoritesList` model in `SLNG.Core` with JSON persistence, reordering, duplicate prevention, and unit tests in `SLNG.Core.Tests`.
- [x] `LandmarkFavoritesStore` in `SLNG.App.UI` persisting favorites under `[favorites_bar_{agentId}]` in `user://preferences.cfg`.
- [x] Dedicated `LandmarksWindow` inheriting `SLNGWindow` with filter search, refresh, creation trigger, list display, double-click teleportation, and "Add to Favorites Bar" affordance.
- [x] `FavoritesBar` control docked below top menu in `TopMenu`, showing favorite landmark buttons, tooltip, right-click menu (Teleport / Copy SLurl / Remove), drag & drop acceptance, and toggleable via View menu.
- [x] Full localization in `en-US.json` and `de-DE.json`.
- [x] AppVersion incremented in `Boot.cs`.

## Technical Specs & Affected Files
- `src/SLNG.Core/Landmarks/LandmarkFavoritesList.cs`
- `tests/SLNG.Core.Tests/LandmarkFavoritesTests.cs`
- `app/scripts/UI/LandmarkFavoritesStore.cs`
- `app/scripts/UI/FavoritesBar.cs`
- `app/scripts/UI/LandmarksWindow.cs`
- `app/scripts/UI/TopMenu.cs`
- `app/scripts/Boot.cs`
- `app/scripts/UI/InventoryPanel.cs`
- `app/i18n/en-US.json`
- `app/i18n/de-DE.json`
- `docs/ROADMAP.md`

## Sub-tasks / Progress
- [x] Create spec file and update ROADMAP.md
- [x] Implement `LandmarkFavoriteItem` and `LandmarkFavoritesList` in `SLNG.Core`
- [x] Write unit tests in `SLNG.Core.Tests`
- [x] Implement `LandmarkFavoritesStore` in `app/scripts/UI`
- [x] Implement `FavoritesBar` control in `app/scripts/UI`
- [x] Implement `LandmarksWindow` in `app/scripts/UI`
- [x] Integrate into `TopMenu.cs`, `Boot.cs`, and `InventoryPanel.cs`
- [x] Add localization keys
- [x] Bump `AppVersion` and verify all tests / self-test
