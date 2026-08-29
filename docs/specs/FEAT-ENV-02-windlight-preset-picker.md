# [FEAT-ENV-02] Windlight preset picker

- **Feature ID:** `FEAT-ENV-02`
- **Track:** `ui` / `net` / `render`
- **Status:** `✅ Done` — confirmed in-world 2026-08-29
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

FEAT-ENV-01 made the sky follow the REGION. This adds the other half every viewer has: let the
user pick a **Windlight preset** of their own — the sky library Firestorm offers — and get back to
the sim's environment with one click.

The preset library is the legacy `.xml` set from the viewer's `app_settings/windlight`
(36 skies, 7 water settings), shipped in `app/assets/windlight`. Firestorm ships the very same
files, so the names in the picker are the ones users already know. They are plain LLSD, need no
grid, and can be applied before login.

## Acceptance Criteria

- [x] `World ▸ Umgebung (Windlight)…` and a toolbar button open a picker window.
- [x] Selecting a sky or water preset applies it immediately (no "Apply" button).
- [x] A preset moves the SUN, not just the colours (legacy `sun_angle`/`east_angle` conversion).
- [x] "Region-Einstellung verwenden" drops every preset and hands the scene back to the sim, with
      no refetch — the region cycle was never discarded.
- [x] Sky and water override independently: picking a sky keeps the region's water.
- [x] The status line names what is active, and which capability the region's own environment came
      from (EEP / Windlight / none).
- [x] Unit tests parse the REAL shipped files, not fixtures; `--selftest` loads the whole library
      through Godot's filesystem so a missing export include shows up as a failed check.

## Technical Specs & Affected Files

- `src/SLNG.Net/WindlightPresetParser.cs` — **new.** Public boundary for preset FILES (the way
  `EnvironmentLlsdParser` is for what the simulator sends): text in, `SLNG.Core` records out, no
  `OSD` above `SLNG.Net`.
- `src/SLNG.Net/EnvironmentLlsdParser.cs` — the legacy conversions the viewer does in
  `translateLegacySettings` (llsettingssky.cpp:988-1113) and that a preset needs:
  - **Array-wrapped scalars.** Legacy stores every scalar as `[value, 0, 0, 1]`. `OSD.AsReal()` on
    an array returns **0** — silently. Without the unwrap, `cloud_scale`, `cloud_shadow`, `gamma`,
    `max_y` and the whole haze block (`haze_density`, `haze_horizon`, `density_multiplier`,
    `distance_multiplier`) read as zero. This also fixes the LIVE legacy-Windlight region path,
    which had the same bug.
  - **`sun_angle` / `east_angle` → `sun_rotation`.** A legacy setting has no sun quaternion at all.
    Port of `convert_azimuth_and_altitude_to_quat` (llsettingssky.cpp:48-70), azimuth negated
    (legacy east angle is clockwise), moon diametrically opposed. Verified against the `lightnorm`
    each preset file carries — the direction it was authored with.
  - **`star_brightness` ×250** (legacy 0..2 → EEP 0..500).
  - **`normalMap`** accepted as an alias of `normal_map` for legacy water.
- `app/scripts/WindlightPresetLibrary.cs` — **new.** Directory listing + on-demand parse via
  DirAccess/FileAccess, so it works from source and from an exported `.pck` alike. The index is the
  listing, not a manifest: dropping another `.xml` in makes it appear with no code change.
- `app/scripts/EnvironmentDriver.cs` — `SetSkyPreset` / `SetWaterPreset` / `ClearPresets`. Sky and
  water are held as separate overrides substituted at the one point the cycle would have been
  evaluated; the region cycle is kept untouched underneath. A sky override also takes over the sun
  direction, which is what makes "Midnight" actually be night.
- `app/scripts/UI/EnvironmentWindow.cs` — **new** `SLNGWindow`, two lists + status + reset.
- `app/scripts/Boot.cs`, `app/scripts/UI/TopMenu.cs` — window, toolbar item (`wb_sunny`), menu entry.
- `app/scripts/SelfTest.cs` — the library lists and every preset parses (2 new checks, 26 total).
- `app/assets/windlight/**` + `app/THIRD-PARTY-NOTICES.md` — the preset files and their licence.

## Deliberately out of scope

- **Day-cycle presets** (`app_settings/windlight/days`). They reference sky presets by name and
  interpolate between them; the picker applies one fixed sky, which is what "choose a Windlight"
  means to a user.
- **Editing / saving presets.** Read-only library for now.
- **Persisting the choice across restarts.** A preset lives for the session; the region is one
  click away, same contract as Firestorm's floater.
- **Firestorm's own extra presets.** Only the Linden library is vendored here; Firestorm's
  additions are not in the viewer source tree.
