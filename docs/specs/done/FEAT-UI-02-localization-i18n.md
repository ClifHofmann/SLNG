# [FEAT-UI-02] Client Localization & Multi-Language Support (i18n)

- **Feature ID:** `FEAT-UI-02`
- **Track:** `ui` / `core`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Implement a lightweight, extensible localization (i18n) framework for SLNG. All UI strings, error messages, and system notifications must be localized via external, human-editable translation files (JSON/CSV) without hardcoded text in C# scripts or Godot scene files.

## Functional Requirements

### 1. Translation File Standard & Schema
- **File Format:** UTF-8 encoded JSON files located in `app/i18n/` (e.g., `en-US.json`, `de-DE.json`, `es-ES.json`).
- **Naming Convention:** Standard IETF BCP 47 language tags (`<language>-<REGION>.json`).
- **Key Hierarchy Standard:** Dot-separated hierarchical keys (`<area>.<component>.<key>`):
  ```json
  {
    "ui": {
      "chat": {
        "title": "Chat",
        "main_tab": "Main",
        "send_button": "Send"
      },
      "friends": {
        "title": "Friends",
        "status_online": "Online",
        "status_offline": "Offline"
      }
    },
    "preferences": {
      "language": "Language"
    }
  }
  ```

### 2. Localization Engine & Fallback Logic
- **Default & Fallback Language:** `en-US` is the primary fallback.
- **Key Resolution Order:**
  1. Selected Locale (e.g. `de-DE`)
  2. Primary Fallback (`en-US`)
  3. Raw key format `[area.component.key]` if missing in all language packs.
- **Pluralization & Parameter Formatting:** String interpolation with `{0}`, `{1}` positional tokens (e.g., `"{0} friends online"`).

### 3. Integration & Runtime Switching
- **Godot `TranslationServer` & `LocalizationManager` Integration:**
  - `LocalizationManager.cs` service handles loading and parsing of translation files.
  - Runtime language switching in Preferences updates all UI components instantly without client restart via Godot signals.
- **Developer API:** String lookup via `L10n.Tr("ui.chat.send_button")` or `L10n.TrFormat("ui.friends.count", count)`.

## Acceptance Criteria
- [ ] Translation file format specified as JSON under `app/i18n/<locale>.json` with `en-US.json` provided as baseline.
- [ ] `LocalizationManager` loads translation files and falls back gracefully to `en-US` / raw key if keys are missing.
- [ ] UI components use `L10n.Tr(...)` / Godot translation keys instead of hardcoded strings.
- [ ] Runtime switching of active language updates UI text without requiring a client restart.
- [ ] Unit tests pass for string lookup, fallback behavior, and token formatting.

## Technical Specs & Affected Files
- `app/i18n/en-US.json` — Default English dictionary.
- `app/i18n/de-DE.json` — German translation pack example.
- `src/SLNG.Core/Services/LocalizationManager.cs` — Engine-neutral localization & fallback logic.
- `app/scripts/UI/L10n.cs` — Godot bridge helper for UI components.
- `docs/specs/FEAT-UI-02-localization-i18n.md` — Feature specification.

## Sub-tasks / Progress
- [x] Create `FEAT-UI-02` spec & update `ROADMAP.md`
- [x] Create `en-US.json` baseline dictionary and schema specification
- [x] Implement `LocalizationManager` in `SLNG.Core` with fallback & formatting support
- [x] Implement `L10n` Godot helper and wire into `Preferences` language selector
- [x] Add unit tests for `LocalizationManager`
