# [FEAT-UI-80] Search in Preferences, the way Firestorm does it

- **Feature ID:** `FEAT-UI-80`
- **Track:** `ui` / `core`
- **Status:** ⏸️ Pending
- **Owner:**
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md)
- **Depends on:** [FEAT-UI-79](FEAT-UI-79-preferences-rework.md) (rows registered with L10n keys and `DependsOn`)

## Overview & Goal
Asked 2026-10-28 together with FEAT-UI-79. Decided with the user the same day: filter in place "wie Firestorm", not a
separate result list.

### What the reference viewer does
The preferences search came from Firestorm into the Linden viewer (`<FS:ND/>` at `llfloaterpreference.cpp:369`).
- On every keystroke (`mFilterEdit->setKeystrokeCallback`, line 510), `LLFloaterPreference::onUpdateFilterTerm`
  (line 3432) lowercases the term and calls `hightlightAndHide` on the root tab container (line 3451).
- A control matches when its search text contains the term (`llsearchableui.cpp:56`). A match is highlighted (line 58).
- A tab with no match anywhere below it is hidden (`setTabVisibility`, line 113). The floater then selects the first
  visible tab (`selectFirstTab`, line 3455).
- A control that is not visible never matches (line 47): a setting hidden behind an unticked switch cannot be found.

## Behaviour
- **Search field** on top of the category list, focused when the window opens. Esc in the field clears it.
- **Every keystroke filters:**
  - Categories without a hit are hidden. If the selected category has none, the first one with a hit is selected
    (as `selectFirstTab`).
  - In the shown category, sections without a hit are hidden (the viewer hides sub-tabs). Rows of a shown section stay,
    hits get a tinted background, and the view scrolls to the first hit.
  - Clearing the field restores every category and section and removes the highlights. The selected category stays.
- **Two improvements over the viewer:**
  1. A hit inside a collapsed Advanced fold-out opens it while the filter is on. It folds back when the filter is cleared,
     unless the user had opened it.
  2. A hit on a row hidden by its switch (shadow distance with shadows off, the DoF controls, the mirror probe) is not
     lost. Its controlling row (`DependsOn`, FEAT-UI-79) is highlighted, with a short note naming the hidden setting.
- **Keyboard:** its action names are searched too. When it has a hit, its own filter box (`ui.keys.search_placeholder`)
  is set to the term.
- **No hit anywhere:** one line, "No setting matches".

## Matching
- **Search text per row:** label, hint, tooltip, the option names of a dropdown, and keywords. All of them in the current
  language **and** in English, so a German user can follow an English SL guide. `LocalizationManager` already holds every
  locale; add `GetStringIn(locale, key)`.
- **Keywords:** the SL debug-setting name where one maps. Checked against `scratch/slviewer/.../app_settings/settings.xml`:
  `RenderFarClip`, `RenderVolumeLODFactor`, `RenderAvatarMaxNonImpostors`, `RenderShadowDetail`,
  `RenderShadowResolutionScale`, `RenderDepthOfField`, `RenderMirrors`, `RenderGlow`, `RenderScreenSpaceReflections`,
  `RenderFSAASamples`, `RenderVSyncEnable`, `RenderCompressTextures`, `MaxFPS`, `ThrottleBandwidthKBPS`, `CameraAngle`,
  `DisableCameraConstraints`, `PlayTypingAnim`, `UseDisplayNames`, `NameTagShowUsernames`, `UIScaleFactor`,
  `GridResolution`. Check any further name against that file before adding it. Plus common words: fps, vram, lod, ao,
  bandwidth.
- **Normalisation:** lowercase invariant, diacritics folded (ä → a), ß → ss. One word = substring, as in the viewer.
  Several words: every word must occur.
- **Where the code lives:** the matcher is pure (`SLNG.Core.Settings.SettingsSearch`: entry texts + query → match), so it
  is unit-testable, like `KeyBindingTable`. The index and the UI stay in `app/`.
- The index is built when the window first opens and rebuilt on `LanguageChanged`.

## Acceptance Criteria
- [ ] "schatten" shows only Graphics › Shadows. Its rows are highlighted, and Advanced is open because cascades and
      softness match.
- [ ] "RenderFarClip" finds draw distance, "fps" finds the FPS limit, "bandwidth" finds max bandwidth while the UI is
      German.
- [ ] A search for shadow distance with shadows off highlights the Shadows switch with the note.
- [ ] A term that matches a key action fills the Keyboard page's own filter.
- [ ] Esc clears the field; clearing restores everything and keeps the selected category.
- [ ] Filtering on every keystroke causes no visible hitch.
- [ ] `AppVersion` bumped; `/slng-verify` clean.

## Tests
- xUnit `SettingsSearchTests`: case and diacritic folding (`schärfe` / `scharfe`), ß, several words (AND), substring,
  empty query matches everything, an English text matches while the current language is German.
- Selftest "preferences search": the queries from the acceptance criteria against the real window.

## Technical Specs & Affected Files
- `src/SLNG.Core/Settings/SettingsSearch.cs` (new).
- `src/SLNG.Core/Services/LocalizationManager.cs`: `GetStringIn(locale, key)`.
- `app/scripts/UI/PreferencesWindow.cs`: search field, filter, highlight, select first hit.
- `app/scripts/UI/Prefs/PrefsRow.cs`, `PrefsSection.cs`: keywords, highlight, fold-out opening.
- `app/scripts/UI/KeyboardPreferencesPage.cs`: a way to set its filter from outside.
- `app/i18n/de-DE.json`, `app/i18n/en-US.json`.
- `tests/SLNG.Core.Tests/...`, `app/scripts/SelfTest*.cs`.

## Out of scope
- The viewer-wide search (Ctrl+F, FEAT-UI-49). This field takes no global shortcut; it has focus when the window opens.
- A separate result list (considered and rejected 2026-10-28).
