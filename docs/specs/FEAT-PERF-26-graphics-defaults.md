# [FEAT-PERF-26] Graphics defaults from the machine; four shadow cascades only on Ultra

- **Feature ID:** `FEAT-PERF-26`
- **Track:** `render` / `ui`
- **Status:** 🧪 Review
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md), measurements in [FEAT-PERF-24](FEAT-PERF-24-ecs-renderingserver-prims.md)
- **Branch:** `feature/FEAT-PERF-26-graphics-defaults`

## Overview & Goal

Two requests from the maintainer, 2026-10-29:

1. **Four shadow cascades only on the highest preset.** The saved value `shadow_splits=2` means four cascades (0 one,
   1 two, 2 four). High and Ultra both used four. At Sirens, four cascades drew 13,050 shadow draws against 4,400 for
   two, 29.9k draws against 21.1k, 22 against 27 fps (render-thread bound). Now Low, Medium and High use two; Ultra
   keeps four.
2. **A new install picks its defaults from the machine, as the reference viewer does on its first start.** Before,
   every new install started on the field defaults (about High) whatever the card.

## Design

- `SLNG.Core.GraphicsRecommendation.Recommend(dedicatedVramMb, integratedGpu, logicalCores, ramMb)` is a pure function
  with xUnit tests. There is no GPU table, so dedicated video memory stands in for the reference viewer's GPU class:

  | Dedicated VRAM | Preset |
  |---|---|
  | integrated GPU | Low |
  | unknown (0, not Windows or no DXGI) | Medium |
  | < 5.5 GB | Low |
  | 5.5 GB or more (a 6 GB card) | Medium |
  | 7.5 GB or more (8 GB) | High |
  | 15 GB or more (16 GB) | Ultra |

  Capped at Medium below 4 logical cores or 8 GB RAM, at High below 6 cores or 16 GB. Unreadable CPU and RAM never cap.
  The VRAM lines sit under the round figures because cards report 6,144 / 8,188 / 16,376 MB; they follow
  `LowVramPolicy` (small card below 6 GB budget, tiny below 4.5 GB).
- `GraphicsSettings.IsFirstRun`: the last `Load()` found no preferences file, or no `[graphics]` section in it.
  `Boot.PickFirstRunGraphics` (right after `Load()`) reads the adapter name and type, dedicated VRAM through
  `DxgiVideoMemory`, the core count and `OS.GetMemoryInfo()`, applies the preset (`ApplyRecommended`, which saves it)
  and logs `[Graphics] first start: ... -> <preset>`. Anyone who saved a graphics setting keeps it; nothing is migrated.
- Preferences window plan (FEAT-UI-79/80): no new setting, no new control. A preset's cascade count changes, the
  setting stays in Shadows.

## Acceptance Criteria

- [x] `GraphicsRecommendation` xUnit tests (16 cases).
- [x] Self test `graphics: first start takes the machine's preset, only Ultra has 4 cascades` (temp file): no file ->
      first run; applying a recommendation saves and clears it; Low/Medium/High -> 2 cascades, Ultra -> 4; each preset
      still detects as itself. Headless `--selftest` 182 ok.
- [ ] In-world first start: delete the `[graphics]` section of `preferences.cfg` (or start with a clean user dir),
      read `[Graphics] first start:` in godot.log, check the preset and the Graphics page against the machine.

## Not covered

- Linux and macOS report no dedicated VRAM here, so they start on Medium (and Low on an integrated GPU).
- An existing install keeps its saved cascades; High users who saved four stay on four until they pick a preset.
- No "Recommended" hint in the Graphics page (a new control, which the plan would need to name first).
