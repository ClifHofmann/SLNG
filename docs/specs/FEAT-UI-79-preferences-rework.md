# [FEAT-UI-79] Preferences: seven categories, one row layout, "Advanced" per section

- **Feature ID:** `FEAT-UI-79`
- **Track:** `ui`
- **Status:** ✅ Done
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md)

## Overview & Goal
Asked 2026-10-28: rework the Preferences window so it is simpler and easier to find one's way in, plus a search
(the search is [FEAT-UI-80](FEAT-UI-80-preferences-search.md), which builds on the row registry introduced here).
Decided with the user the same day: "Advanced" folds out **per section**, not one global basic/expert switch.

State at v0.28.33-alpha:
- **12 flat tabs in no order:** Toolbar, Display, Camera, Keyboard, Animation, Graphics, Network, Chat logs,
  Away & Auto-response, Age Settings, Debug log, Licences. The 12 entries (32 px + 4 px gap) need about 430 px, more
  than the window's 420 px height.
- **Display is a grab bag:** language, UI scale, names and group titles, conference chats, friend logon notices,
  build grid and rotation snap. The favorites' region check sits under Network.
- **Graphics has a third navigation level** (sub-tabs General / Hardware / Depth of Field) and two preset mechanisms
  stacked on top (named profiles, quality slider). Shadow resolution is under Hardware, the rest of the shadow settings
  under General. Two mirror switches: "Real-Time Mirrors" (`PostFxHeroProbe`) only works while "Mirrors"
  (`Mirrors`) is on.
- **Every page looks different:** CheckBox next to CheckButton (small-object shadows), explanations as tooltip on one
  page and as text below on the next, headings with and without a colon, colours and helpers (`AddHeading`, `AddCheck`,
  `AddHint`, `AddSliderRow`) duplicated per page.
- **When a change takes effect is not visible:** at once, at the next login (chat log folder), after a restart (render
  thread), after reopening windows (language), or after "Apply" (auto-response texts).
- **Dead code:** `DesignPreferencesPage.cs` and `QualityPreferencesPage.cs` (about 550 lines) are never instantiated.

Goal: fewer places to look, one look for every setting, expert options out of the way but still there. No setting
changes its value, default or storage.

## Layout
Left: the category list (FEAT-UI-80 puts the search field on top of it). Right: the selected category as one scrolling
column of sections. No sub-tabs. Default window 760 × 520, minimum 640 × 420. The last category is remembered across
restarts (`preferences.cfg`).

### Categories, sections and where every setting goes
"Main" rows are always shown; "Advanced" rows sit in the section's collapsed fold-out.

| Category | Section | Main | Advanced | From today |
|---|---|---|---|---|
| **General** | Interface | Language, UI scale (auto + slider) | | Display |
| | Bottom bar | Toolbar button checkboxes | | Toolbar |
| | Names above avatars | Display Names, username below, group titles, hide my group title | | Display |
| | Content rating | General / Moderate / Adult | | Age Settings |
| | Building | Grid, rotation snap | | Display |
| **Graphics** | *(header row)* | Quality slider Low–Ultra, reset, **Profiles ▾** (load / save / delete) | | profile header + master slider |
| | View & detail | Draw distance, object detail, max fully rendered avatars | Hide tiny objects | General sub-tab |
| | Shadows | Shadows, shadow resolution | Cascades, softness, distance, darkness, small-object shadows | General + Hardware |
| | Light & effects | Ambient occlusion, glow, mirrors | SSIL, environment reflections, probe ambient, SSR, real-time mirror probe | General |
| | Depth of field | Enable, its controls | | DoF sub-tab |
| | Performance | FPS limit, V-Sync, anti-aliasing, texture memory (auto + slider + usage), the "render thread switched off after a freeze" notice with its switch-on button (shown only after a fallback, FEAT-UI-81) | Render thread, compress textures, low-VRAM caps, merge other avatars' meshes | Hardware |
| **Camera & movement** | Camera | Field of view, rear view distance, focus height, disable camera constraints, reset | Pad speeds (orbit, pan, zoom) | Camera |
| | Movement | Always run | | Animation |
| | Avatar | Head follows camera, typing animation, furniture poses override the AO | | Animation |
| **Chat & IM** | Conversations | Conference chats as IMs | | Display |
| | Friends online | Pop-up, line in local chat | | Display |
| | Away & auto-response | today's page unchanged | | Away & Auto-response |
| | Chat logs | Account, folder, use default | IM file names, import old logs | Chat logs |
| **Keyboard** | | today's page unchanged (it has its own filter) | | Keyboard |
| **Network & cache** | Network | Max bandwidth, check favorites' regions | | Network |
| | Cache | Size readout, max cache size, clear | Decoded texture cache | Network |
| **Help & info** | Debug log | today's page unchanged | | Debug log |
| | Licences | today's page unchanged | | Licences |

**Rule for Advanced:** a row goes there when it is outside the graphics presets and needs knowledge to set
(experimental, needs a restart, a trade-off a newcomer cannot judge), or when it fine-tunes a main switch. A section
with no such row has no fold-out. Collapsed by default; open/closed is remembered per section.

### Mirrors: one switch in the main rows
`Mirrors` (FEAT-PERF-15, the viewer's `RenderMirrors`) decides whether mirrors exist at all. `PostFxHeroProbe`
(FEAT-RENDER-22) is the real-time probe, which needs it. Main row: Mirrors. The probe moves to Light & effects ›
Advanced as "Real-time mirror probe", disabled while Mirrors is off. Both settings keep their values and preset
semantics; only the UI changes.

### One row building block
`PrefsSection` / `PrefsRow` in `app/scripts/UI/Prefs/`:
- Row: label in a fixed left column, control on the right, an optional one-sentence hint below (12 px, grey). A long
  explanation stays a tooltip. Both are searched by FEAT-UI-80.
- Optional badge for a row that does not apply at once: **Restart**, **Next login**, **Reopen windows**. Text fields that
  need confirming (auto-response) keep their Apply / Revert buttons.
- The builder takes **L10n keys**, not translated strings, so FEAT-UI-80 can index every language from the key.
- Every row registers with the window: category, section, key, control, a visibility check, and `DependsOn(row)` for a
  row that depends on another one (shadow controls, DoF controls, the mirror probe).
- **A dependent row is disabled (dimmed), never hidden, while its switch is off** (decided during the build, confirmed by the maintainer 2026-10-28): the
  person sees what the switch unlocks, and nothing moves when it is flipped.
- One checkbox type, `CheckBox`. The `CheckButton` for small-object shadows goes.
- The graphics reset ↺ gets its own tooltip key instead of borrowing `camera_speed_reset`.
- Colours and sizes come from one place, not from each page.

### Clean-up
- Delete `DesignPreferencesPage.cs` and `QualityPreferencesPage.cs`, and the reference to them in a comment in
  `MaturityPreferencesPage.cs`.
- Delete the i18n keys no longer referenced after the move (old `tab_*`, `subtab_*`, `design_heading`, `theme_heading`, ...)
  from both language files. Grep `app/` and `src/` before deleting each one.

## Acceptance Criteria
- [x] Seven categories in the order of the table. The category list fits the default window without scrolling.
- [x] Every setting reachable at v0.28.33-alpha is reachable in exactly one place, as in the table.
- [x] Every page is built from `PrefsSection` / `PrefsRow`. No page-local `AddHeading` / `AddCheck` / `AddHint` left.
- [x] Advanced is collapsed by default in each section that has one, and its state is remembered.
- [x] Rows that do not apply at once show their badge.
- [x] The real-time mirror probe row is disabled while Mirrors is off.
- [x] The last category is remembered across a restart. Default size 760 × 520.
- [x] Both language files have every new key, and no key that nothing uses.
- [x] No setting changes value, default or storage (`preferences.cfg` sections unchanged, no migration).
- [x] `AppVersion` bumped; `/slng-verify` clean.

## Tests
- Selftest "preferences complete": the expected inventory (every setting id from the table) against the rows the window
  registered. Each one exactly once, each label non-empty in de-DE and en-US. It also fails on a registered row that the
  inventory does not list, and on an eighth category: that is what holds later changes to this plan (rule in
  `.claude/skills/app-rules/SKILL.md`, "Preferences follow the plan").
- Selftest: the mirror probe row is disabled with Mirrors off and enabled with it on.
- The existing `W<UI.PreferencesWindow>` window check in `SelfTest.cs` keeps passing.

## Technical Specs & Affected Files
- `app/scripts/UI/PreferencesWindow.cs`: category list, section host, remembered category, default size.
- `app/scripts/UI/Prefs/PrefsSection.cs`, `PrefsRow.cs` (new).
- Every `app/scripts/UI/*PreferencesPage.cs`: rebuilt on the builder. Display is split up; Graphics loses its sub-tab bar.
- `app/scripts/Boot.cs` (`SetupHud`): the `AddTab` calls become categories and sections.
- `app/i18n/de-DE.json`, `app/i18n/en-US.json`.
- `app/scripts/SelfTest*.cs`.

## Sub-tasks / Progress
- [x] Builder and window shell (categories + sections), Camera migrated as the proof.
- [x] Migrate the other pages, move rows as in the table, one mirror switch, profiles menu.
- [x] Dead pages and keys removed, selftests, `AppVersion`.

## Built (v0.28.41 – v0.28.43-alpha)
- Steps A/B/C as above, 107 rows in 23 sections. Only the Keyboard table and the licence text are embedded blocks.
- Graphics header: quality slider, reset with its own tooltip, a Profiles menu (click a profile to load it, "Save current
  as…", Delete submenu) in place of the dropdown + Load/Save/Delete buttons.
- Every Graphics callback now respects the refresh guard. Before, opening Preferences could switch DoF auto-focus off and
  write quantised shadow/DoF slider values back into the settings.
- The render-thread row keeps its hint and shows a separate "restart" notice only while a change is pending.
- `LocalizationManager.GetStringIn(locale, key)` (no fallback) added for the selftest and for FEAT-UI-80.
- 41 unused keys removed from both language files; none is left unused under `ui.preferences.`.
- Confirmed in-world by the maintainer 2026-10-28; released in v0.28.45-alpha.

## Out of scope
The search (FEAT-UI-80). New settings. Changed defaults.
