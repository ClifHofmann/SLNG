# [FEAT-PERF-08] Avatar Cap: Part of Graphics Presets

- **Feature ID:** `FEAT-PERF-08`
- **Track:** `render` / `ui`
- **Status:** `🚧 In Progress`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md)

## Overview & Goal

On crowded regions (e.g. Second Life Agni region "sirens", measured 2026-10-09 19:57), SLNG runs at 10–13 fps.
The bottleneck is CPU, not GPU (GPU 12 ms). Diagnostics log:
`[AvatarCost] avatars=53 shown=30 skinnedMeshes=5355 skinBinds=305209`.
Frame time ~90 ms consists of:
- scripts ~40 ms (`[PhaseCost] avatar-render ~160 ms/s`),
- postFlush ~26 ms (Godot's per-frame skin-bind transform updates sent to RenderingServer),
- draw ~20–45 ms.

With ~178 skinned meshes and ~10,000 skin binds per avatar across 30 avatars in view, the number of fully rendered avatars is the dominant lever.
The reference viewer (Second Life / Firestorm) addresses this with two knobs:
1. `RenderAvatarMaxNonImpostors` (featuretable.txt:98, 185, 269, 351: Low 3, Mid 7, High 11, Ultra 16; default 16).
2. `RenderAvatarMaxComplexity` (jelly dolls based on Linden ARC formula in `llvoavatar.cpp ~11442`).

This feature implements the first cut of FEAT-PERF-08: a cap on fully rendered avatars (nearest first with distance hysteresis), integrated into SLNG's graphics presets and user settings.

### Choice & Justification of Cheap Stand-In

Avatars beyond the cap receive a cheap stand-in rather than full worn mesh rigging.
**Decision: Base body with its baked textures (BoM/system bakes) and no worn meshes.**
- **Visuals & Identity:** A flat monochrome silhouette ("jelly doll") turns every distant avatar into an anonymous featureless mannequin. In contrast, the SL base avatar body with its baked textures preserves skin tone, facial features, makeup, and system clothing/tattoos. It looks completely natural — identical to classic Second Life avatars.
- **Resource Savings:** The base body consists of only 5 standard body part meshes (`head`, `upper_body`, `lower_body`, `eye`, `eyelashes`) and ~130 total skin binds, compared to ~178 meshes and ~10,000 skin binds for a modern rigged mesh avatar with clothes, hair, and accessories.
- **Immediate CPU/GPU relief:** No attachment meshes are downloaded, decoded, prepared by worker threads, or bound by the main thread. When an avatar moves outside the cap, its rigged meshes, skins, and attachment texture references are released, directly lowering CPU pose updates, draw calls, skin binds, and VRAM.
- **Interaction Intact:** Name tags, distance fading, and clickability (selection capsule and right-click context menu) remain fully intact.

## Acceptance Criteria

- [x] `AvatarLimitPolicy`: Pure evaluation function with distance hysteresis (15% distance discount for incumbent full avatars) to prevent boundary flickering.
- [x] Unit tests for `AvatarLimitPolicy` covering unlimited cap, boundary transitions, hysteresis stability, and exempt handling.
- [x] Presets in `GraphicsSettings`: Low 3, Medium 7, High 11, Ultra 16 (matching reference viewer featuretable.txt).
- [x] Manual change switches preset to Custom; setting persisted under `[graphics]` section in `user://preferences.cfg`.
- [x] Range 1–50 plus "unbegrenzt" (0 = unlimited).
- [x] UI control on the Graphics settings page ("Max. voll dargestellte Avatare") with German text and English i18n key.
- [x] Right-click context menu override: "Always render fully" / "Immer voll darstellen" toggle on avatars (self is always exempt).
- [x] `[AvatarCost]` extended with `reduced={reduced}` count.
- [x] Diagnostics line logged on change: `[AvatarLimit] shown N full, M reduced (cap C)`.
- [x] `docs/BENUTZERHANDBUCH.md` updated in German.
- [x] All tests pass (`dotnet test`), code builds clean, `dotnet format` clean, and `AppVersion` bumped.

## Technical Specs & Affected Files

- `src/SLNG.Core/AvatarLimitPolicy.cs`: Pure domain policy for sorting candidates, applying hysteresis, and partitioning into Full vs Reduced.
- `tests/SLNG.Core.Tests/AvatarLimitPolicyTests.cs`: Unit tests for policy & preset values.
- `app/scripts/RenderConfig.cs`: `MaxFullyRenderedAvatars` constant and runtime holder.
- `app/scripts/UI/GraphicsSettings.cs`: `MaxFullyRenderedAvatars` property, preset mapping (3/7/11/16), persistence, profile loading/saving.
- `app/scripts/UI/GraphicsPreferencesPage.cs`: Slider and labels for "Max. voll dargestellte Avatare" (1–50 + unbegrenzt).
- `app/scripts/UI/InWorldContextMenu.cs`: Menu button for "Always render fully" on avatars.
- `app/scripts/ObjectSelectionController.cs` & `app/scripts/Boot.cs`: Wiring of avatar context menu override.
- `app/scripts/AvatarRenderer.cs`: Integration in 4 Hz culling pass, avatar reduction / restoration methods, release of rigged meshes and texture refs, extension of `[AvatarCost]`.
- `app/scripts/AvatarRenderer.RigWorker.cs`: Rig workers skip rig requests for reduced avatars.
- `app/i18n/de-DE.json` & `app/i18n/en-US.json`: UI and menu localization.
- `docs/BENUTZERHANDBUCH.md`: User manual documentation in German.

## Sub-tasks / Progress

- [x] Create branch `feature/FEAT-PERF-08-avatar-limit` and update `ROADMAP.md`
- [x] Implement `AvatarLimitPolicy` in `SLNG.Core`
- [x] Write unit tests in `SLNG.Core.Tests`
- [x] Implement `GraphicsSettings` & `RenderConfig` integration
- [x] Implement UI slider in `GraphicsPreferencesPage`
- [x] Implement avatar context menu toggle "Always render fully"
- [x] Implement avatar reduction/restore in `AvatarRenderer` & `RigWorker`
- [x] Localize strings in `de-DE.json` and `en-US.json`
- [x] Update `docs/BENUTZERHANDBUCH.md`
- [x] Bump `AppVersion` in `Boot.cs` and run `/slng-verify`
