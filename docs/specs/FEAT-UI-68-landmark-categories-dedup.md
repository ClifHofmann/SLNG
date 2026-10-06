# [FEAT-UI-68] Landmark Categorization & Duplicate Cleaner

- **Feature ID:** `FEAT-UI-68`
- **Track:** `ui`
- **Status:** `🧪 Review`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
For avatars with large numbers of landmarks (1,100+), provide folder-based categorization in the dedicated Landmarks window (`LandmarksWindow`) matching their Second Life inventory hierarchy, alongside an intelligent duplicate detection and batch cleanup dialog (`LandmarkDedupWindow`) that safely moves redundant landmark copies to the Trash folder (`MoveToTrashAsync`).

## Acceptance Criteria
- [x] `LandmarkInventoryItem` model and `LandmarkDuplicateDetector` in `SLNG.Core.Landmarks` with detection by exact `AssetId` and canonical `Name`, scoring for keep/delete recommendations, and 100% unit test coverage in `tests/SLNG.Core.Tests/LandmarkDuplicateDetectorTests.cs`.
- [x] `GridSession.GetLandmarksWithFoldersAsync()` in `SLNG.Net` collecting landmark items along with their parent folder names and paths from LibreMetaverse's inventory store.
- [x] Folder-tree view mode in `LandmarksWindow` alongside flat view mode, allowing collapsible folder browsing and filtering.
- [x] Dedicated `LandmarkDedupWindow` (`SLNGWindow`) listing duplicate groups, previewing folder paths and creation dates, allowing manual review and batch "Move selected to Trash" with confirmation.
- [x] Single landmark "Move to Trash" context menu action in `LandmarksWindow`.
- [x] Full localization in `en-US.json` and `de-DE.json`.
- [x] AppVersion incremented in `Boot.cs`.

## Technical Specs & Affected Files
- `src/SLNG.Core/Landmarks/LandmarkInventoryItem.cs`
- `src/SLNG.Core/Landmarks/LandmarkDuplicateDetector.cs`
- `tests/SLNG.Core.Tests/LandmarkDuplicateDetectorTests.cs`
- `src/SLNG.Net/GridSession.Inventory.cs`
- `app/scripts/UI/LandmarksWindow.cs`
- `app/scripts/UI/LandmarkDedupWindow.cs`
- `app/scripts/Boot.cs`
- `app/scripts/SelfTest.cs`
- `app/i18n/en-US.json`
- `app/i18n/de-DE.json`
- `docs/ROADMAP.md`

## Sub-tasks / Progress
- [x] Implement `LandmarkInventoryItem` and `LandmarkDuplicateDetector` in `SLNG.Core`
- [x] Write unit tests in `SLNG.Core.Tests`
- [x] Implement `GetLandmarksWithFoldersAsync()` in `SLNG.Net`
- [x] Implement folder-tree categorization in `LandmarksWindow`
- [x] Implement `LandmarkDedupWindow` with smart keep/trash preview and batch delete
- [x] Add localization keys in `en-US.json` and `de-DE.json`
- [x] Bump `AppVersion` and verify tests and self-test
