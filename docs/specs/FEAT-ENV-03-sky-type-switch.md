# [FEAT-ENV-03] Switch lighting path on sky type (`reflection_probe_ambiance`)

- **Feature ID:** `FEAT-ENV-03`
- **Track:** `render`
- **Status:** `✅ Done (code) — not yet re-verified in-world on a modern-sky region`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

`EnvironmentLlsdParser` does not read `reflection_probe_ambiance`, so SLNG treats **every** sky
as legacy. On a region with a modern EEP sky we are therefore on the wrong path entirely — not
by a shade, but in the whole transfer chain.

Surfaced 2026-09-11 by the user asking whether HDR was involved in the colour work. It was the
right question for the wrong region: Azure Haven runs a legacy sky, which is why nothing looked
wrong there.

## The branch

`llsettingsvo.cpp:810`:

```cpp
bool classic_mode = psky->canAutoAdjust() && !should_auto_adjust();
if (!classic_mode) {
    psky->setTonemapMix(tonemap_mix_setting);
}
```

with `mCanAutoAdjust = !settings.has("reflection_probe_ambiance")` (`llsettingssky.cpp:1171`).
So the key's mere PRESENCE flips the sky out of classic mode.

Confirmed from Firestorm's own `settings.xml`:

| setting | value |
|---|---|
| `RenderHDREnabled` | 1 (on) |
| `RenderTonemapMix` | 0.7 |
| `RenderSkyAutoAdjustLegacy` | 0 |
| `RenderSkySunlightScale` | 1.0 |
| `RenderHDRSkySunlightScale` | 1.0 |

HDR is on by default. For a LEGACY sky it is bypassed at every point that matters — the two
sunlight scales are identical so the choice does not bite, `setTonemapMix` is skipped, and
`sky_hdr_scale` is 1.0 — which is why `FEAT-RENDER-19`'s calibration holds. For a MODERN sky
none of that is true.

## What is wrong today on a modern sky

Two things, not one:

1. **The tonemapper.** `Boot.SetupEnvironment` pins `TonemapMode = Linear` with a TODO already
   noting this. A modern sky wants tonemapping at mix 0.7.
2. **The surface lighting**, which is the part easy to miss. `FEAT-RENDER-19` implemented the
   `classic_mode > 0` branch: `sunlit * 1.35`, the `amblit*0.9 + sun*0.7` mix performed in sRGB,
   `final_scale 1.1`, and no `srgb_to_linear`/greyscale on the two lights. A modern sky takes the
   **other** branch — `srgb_to_linear` on both plus the luminance greyscale on ambient, then a
   plain linear sum with none of those four constants.

So this is not "add a tonemapper toggle"; it is making both paths selectable and picking per sky.

## Why it is worth doing before the other follow-ups

A midtone error on legacy-sky regions is a shade. This is the entire transfer path on every
region that ships a modern sky — and it is invisible in testing precisely because the region
we have been comparing against is legacy.

## Fix (2026-09-11, `v0.22.66-alpha`)

`SkySettings` gained `IsLegacy` (default `true`, so any document carrying no explicit signal —
`SkySettings.Default`, a single sky/water document, anything parsed before this field existed —
keeps rendering through the already-verified legacy path). `EnvironmentLlsdParser.ParseSky` sets
it from `!map.ContainsKey("reflection_probe_ambiance")`. Confirmed against source that this is a
TOP-LEVEL key, not nested under `legacy_haze`: `llsettingssky.cpp:1174`'s
`mCanAutoAdjust = !settings.has(SETTING_REFLECTION_PROBE_AMBIANCE)` reads `getSettings()`, the
whole document, the same map `ParseSky` receives as `map`. `DayCycle.Lerp` picks the nearer
keyframe's value (same convention as `SunTextureId`/`CloudTextureId` — not a continuous
quantity, and in practice both keyframes of one cycle share a schema anyway).

`EnvironmentDriver.ApplySun`/`ApplyAmbient` now take the evaluated `SkySettings` and branch on
`IsLegacy`:

- **Legacy branch: the EXISTING code, moved inside the branch UNCHANGED.** Byte-for-byte the
  same computation `FEAT-RENDER-19` calibrated and verified — nothing here was rewritten.
- **New non-legacy branch**, porting `atmosphericsFuncs.glsl`'s `classic_mode<1` path: convert
  both `sunlit` and `amblit` with `srgb_to_linear`, luminance-greyscale the ambient
  (`ModernAmbientLinear`), then sum directly — none of the classic branch's `1.35` sun boost,
  `0.9`/`0.7` sRGB mix, or `1.1` final scale. This turned out simpler to implement than the
  classic branch, not harder: the modern branch is already linear-additive, which is exactly
  Godot's own lighting model (ambient + N·L·sun, summed in linear space), so unlike the classic
  branch it needs no endpoint-subtraction trick — the sun light and the ambient can each just
  carry their own linear radiance straight through.

`EnvironmentDriver.Update` also sets `env.TonemapMode` every frame from `sky.IsLegacy`: `Linear`
for legacy (an exact target — `getTonemapMix()` really does return 0 there,
`llsettingssky.cpp:2062`) vs `Aces` for modern (Godot has no continuous mix control between
tonemap curves, so this stands in for Firestorm's partial 0.7 blend rather than reproducing it
exactly). `Boot.SetupEnvironment`'s `TonemapMode = Linear` is now documented as only the
one-frame startup default before any sky has been evaluated; its stale TODO now points at the
driver.

2 new parser tests (`ParseSky_NoReflectionProbeAmbianceKey_IsLegacy`,
`ParseSky_ReflectionProbeAmbianceKeyPresent_IsNotLegacy`). Build (solution + `app/`) + 692 tests
+ `dotnet format` + shader-globals + `--selftest` 38/38 green.

**Not yet re-verified in-world** — needs a region that actually ships a modern sky; a parity
claim made only against Azure Haven's legacy sky would be exactly the blind spot this task
exists to remove. The tonemap-mix approximation (`Aces` full-strength standing in for a
continuous 0.7 blend) is unverified pixel-for-pixel and may want its own follow-up once such a
region is found — a custom post-pass could reproduce the mix itself instead of approximating it.

## Acceptance Criteria

- [x] `EnvironmentLlsdParser` reads `reflection_probe_ambiance` and exposes "is this sky legacy"
- [x] A parser test covers a capture WITH and one WITHOUT the key
- [x] Tonemapper follows the sky: `Linear` for legacy, tonemapped otherwise — using `Aces` as the
      closest Godot has to the real 0.7 mix, since Godot has no continuous blend control
- [x] `EnvironmentDriver` selects the matching surface-lighting branch, both derived from source
- [x] Legacy-sky regions are BYTE-UNCHANGED — the existing code was moved, not edited, and all
      692 tests (including every pre-existing one) still pass
- [ ] Verified with the probe sphere on a region of each kind — **still needs a live modern-sky
      region**; only the legacy side (Azure Haven) has been available so far

## Technical Specs & Affected Files

- `src/SLNG.Net/EnvironmentLlsdParser.cs`
- `src/SLNG.Core/SkySettings.cs`, `SkyLighting.cs`
- `app/scripts/EnvironmentDriver.cs`, `app/scripts/Boot.cs`
- `tests/SLNG.Net.Tests/EnvironmentLlsdParserTests.cs`
- Reference: `llsettingsvo.cpp:795-822`, `llsettingssky.cpp:1171` and `:2062`,
  `atmosphericsFuncs.glsl:147`, `softenLightF.glsl:226`
- Background: [ADR 0003](file:///E:/Git/SLNG/docs/adr/0003-eep-parameter-model-and-lighting-seam.md),
  which called this consequence out and left it open

## Note

A region needs to be found that actually ships a modern sky — the acceptance criteria cannot be
met on Azure Haven, and a parity claim made only against a legacy region would be exactly the
blind spot this task exists to remove.
