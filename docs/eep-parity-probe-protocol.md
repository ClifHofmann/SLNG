# EEP parity probe protocol

Companion to [ADR 0003](adr/0003-eep-parameter-model-and-lighting-seam.md). This is a
measurement procedure, not a spec.

## Why

Comparing full scenes against Firestorm does not work. Twenty parameters act at
once, so any divergence has twenty candidate causes, and a change that improves
one region can silently break another — that is exactly what happened over
v0.7.15 → v0.7.23, where three consecutive lighting changes each moved the
symptom instead of fixing it.

The fix is the same one that closed the planar-UV bug in one round after six
derived hypotheses had failed: **a probe with a known answer**. One parameter at
its extreme, everything else neutral, so a difference can only come from one
place.

## The presets are pre-built — import, don't hand-edit

They live in [`tools/testassets/eep-parity/`](../tools/testassets/eep-parity/): 21
sky files and 7 water files.

They are **legacy Windlight XML**, because that is what the environment editor's
`Import…` button actually accepts — `LLFloaterFixedEnvironment::doImportFromDisk`
is commented "Load a legacy Windlight XML from disk" and routes through
`createSkyFromLegacyPreset`. Each one is the viewer's own
`app_settings/windlight/skies/Default.xml` (respectively `water/Default.xml`) with
**exactly one value changed**, so the neutral baseline is Linden's own default
rather than anything we chose.

Per probe:

1. Firestorm → **World → Environment → My Environments → New Sky** (or Water).
2. **Import…**, pick the file, then **Save** under the file's own name.
3. Apply it to the region.
4. Capture both viewers with the camera in the **same place**.

`PARITY-00-neutral.xml` and `PARITY-W0-neutral.xml` are the unmodified defaults —
import them first and confirm the two viewers agree on the baseline. If they do
not, no other probe means anything yet.

A fixed sky has no day cycle, so nothing moves between the two screenshots. That
is deliberate.

### Three notes on the legacy format

- **Sun position** comes from `sun_angle` (altitude in radians) and `east_angle`
  (azimuth, negated on import — `llsettingssky.cpp:1099-1111`). The moon is placed
  diametrically opposite the sun automatically, so the night probes get a moon
  without asking for one.
- **`cloud_scroll_rate` is stored offset by +10** in this format — the Default
  carries `10.2 / 10.011` for a real rate of `0.2 / 0.011`. That is the encoding
  our parser now converts; the files keep it, so importing them also exercises the
  viewer's own conversion.
- Water keys are **camelCase** here (`waterFogDensity`, `normScale`, `blurMultiplier`).
  Both spellings are the same setting; our parser accepts either.

### What the legacy format cannot express

These have no legacy key at all and must be set **in the editor after import**, on
the *Clouds* and *Sun and Moon* tabs:

`cloud_variance`, `moon_brightness`, `sun_scale` / `moon_scale`,
`reflection_probe_ambiance`, `moisture_level` / `droplet_radius` / `ice_level`,
and water's `transparent_texture`.

That is why probes 13 and 17 have no file: probe 13 needs `cloud_variance` at 1.0,
probe 17 needs `moon_brightness` at 0.0 on top of `PARITY-16-night`.

## Camera discipline

The comparison is worthless if the two cameras differ. Cheapest reliable method:

- Stand still. Do not move between the two screenshots.
- In Firestorm, note the position readout; in SLNG the HUD shows `<x, y, z>`.
- Point at the **horizon**, not at the ground or the zenith — haze, glow and the
  horizon gradient all vary strongest there, and it keeps some terrain in frame
  for the lighting probes.
- Include a bit of water if the probe is a water one.

## Sky probes

Values are chosen at or near the validator limits, because a parameter at its
extreme produces an unmistakable difference; a parameter at 60% produces an
argument. Ranges are the viewer's own (`llsettingssky.cpp:726-818`).

| Preset name | Parameter | Set to | What it should change |
|---|---|---|---|
| `PARITY-01-haze-max` | Haze density | 5.0 (max) | Thick horizon haze; sky should wash toward the haze colour |
| `PARITY-02-haze-min` | Haze density | 0.0 | Crystal-clear air, hard horizon |
| `PARITY-03-bluedens-max` | Blue density | 3.0 all channels | Deep saturated sky, strong transmittance falloff |
| `PARITY-04-densmult-max` | Density multiplier | 2.0 (max) | Very short optical path — everything hazes out close in |
| `PARITY-05-distmult-max` | Distance multiplier | 100 | Same axis, different exponent term — should differ from 04 |
| `PARITY-06-maxy-low` | Max altitude | 500 | Sky dome ray terminates low; affects both haze and light attenuation |
| `PARITY-07-glow-tight` | Glow size | x = 40 (max) | Small tight sun hotspot |
| `PARITY-08-glow-wide` | Glow size | x = 0.2 (min) | Large diffuse sun halo |
| `PARITY-09-sun-low` | Sun position | elevation ≈ +2° | Maximum atmospheric attenuation — deep red sun |
| `PARITY-10-sun-high` | Sun position | elevation ≈ +85° | Minimum attenuation — near-white sun |
| `PARITY-11-cloudcover-max` | Cloud coverage | 1.0 | Full overcast. **Tests `cloud_shadow` as cover** |
| `PARITY-12-cloudcover-min` | Cloud coverage | 0.25 | `2 * (0.25 - 0.25) = 0` — should be cloudless |
| `PARITY-13-cloudvar-max` *(no file — set in editor)* | Cloud variance | 1.0 | Strong domain warp; ragged cloud edges |
| `PARITY-14-cloudscale-small` | Cloud scale | 0.1 | Small tight cloud cells |
| `PARITY-15-stars-max` | Star brightness + sun below horizon | 500 (max), `sun_angle` 3.4 | Star density and brightness. Tests the /500 conversion |
| `PARITY-16-night` | `sun_angle` 3.4 (≈15° below horizon) | | **The lighting probe.** Ground brightness is the measurement, not the sky |
| `PARITY-17-night-moondark` *(no file — set in editor)* | `PARITY-16` + moon brightness 0.0 | | Should be darker than 16 and carry no solar halo at all |
| `PARITY-18-scroll-fast` | Cloud scroll rate X | 30 legacy → 20 real | Fast drift. Confirms drift reaches the shader at the right rate |
| `PARITY-19-scroll-off` | `enable_cloud_scroll` | both false | Clouds must be completely still |
| `PARITY-20-cover-000` | Cloud colour WHITE + cover 0.25 | | **Read coverage, not colour.** Density is exactly 0, so any cloud at all is a coverage bug |
| `PARITY-21-cover-050` | Same, cover 0.50 | | Density 0.5 — expect heavy but not total |
| `PARITY-22-cover-100` | Same, cover 1.00 | | Density 1.5 forces alpha to 1, so BOTH viewers must be solid white overhead |

### Why the coverage probes change exactly two keys

Probes 11 and 12 were run and could not be read. At the default `cloud_color` of
0.41 grey, a soft cloud layer over a pale sky is indistinguishable from no cloud at
screenshot resolution — two verdicts were called wrong before that became clear.

Two designs were tried and both failed, for reasons worth keeping:

**A black `cloud_color` does not produce black clouds.** `oHazeColorBelowCloud`
(cloudsF.glsl:182) is added AFTER the cloud-colour multiply, so zeroing the colour
leaves the cloud rendering in the haze colour — which is the sky's own colour. Both
viewers went blank. That term is a faithful part of the port; the probe was at
fault.

**Blacking out the sky broke the isolation.** The next attempt zeroed `ambient`,
`blue_horizon`, `blue_density`, `haze_horizon` and `haze_density` to get a dark
backdrop. Firestorm then drew no clouds at all at any cover value — and with six
parameters changed at once there is no way to tell a real divergence from a side
effect of flattening its sky. That is precisely the failure mode this whole
protocol exists to avoid, so it does not count as a result.

What survives both traps is a WHITE cloud colour on the untouched Default sky: it
is unaffected by the haze-bleed term and still reads clearly against pale blue.
Two changed keys, nothing else.

Measured reference: the default cloud noise texture (`1dc1368f-…`, 512×512
greyscale) has mean 0.453 with **36.6% of texels above 0.5**, and the disc our UV
samples yields **39.7%**. At density 0 the formula reduces to
`min(max(noise - 0.5, 0) * 10 * density1.z, 1)`, predicting roughly that fraction
at full opacity — and our own render, measured against a black backdrop before the
isolation problem was noticed, matched it: about 40% at density 0 rising to about
95% at density 0.5.

## Results so far (2026-08-21)

| Probe | Verdict |
|---|---|
| `PARITY-14-cloudscale-small` | **Match.** Cloud cell size agrees. |
| `PARITY-11-cloudcover-max` (1.00) | **Match.** Both viewers saturate to full cover. |
| `PARITY-22-cover-100` (1.00, white) | **Match.** Both solid overhead. |
| `PARITY-21-cover-050` (0.50, white) | **Match.** Both heavily covered. |
| `PARITY-20-cover-000` (0.25, white) | **Match.** Both show cloud. |

So cloud coverage, the `2 * (cloud_shadow - 0.25)` density mapping, the noise texture
and the UV are all confirmed against Firestorm.

Everything earlier that looked like a coverage divergence was an artefact of three
broken probe designs in a row — grey cloud on a pale sky being unreadable, a black
cloud colour picking up the haze colour, and a six-parameter version that destroyed
the isolation. None of those were shader bugs. Worth remembering before concluding
a divergence from a probe that has not been sanity-checked as readable.

### What the coverage probes exposed instead: the sky itself

With the clouds retired from suspicion, the bare sky was visible — and it read pale
grey where Firestorm's is blue with a horizon gradient. Chasing that found four
divergences, three small and one that dominated:

| # | Divergence | Source | Weight |
|---|---|---|---|
| 1 | Sky value treated as linear radiance and then ACES-tonemapped | it is display-referred, and legacy skies are never tonemapped | **almost all of it** |
| 2 | Sky dome fed `SkyLighting.SunDiffuse`, then attenuated again | `llsettingsvo.cpp:782-785` binds the RAW `sunlight_color` | moderate |
| 3 | `haze_glow` used the surface path's squared form | `skyV.glsl:142` / `cloudsV.glsl:142` are plain `1 - dot` | flattened the azimuth gradient |
| 4 | `distance_multiplier` applied to the dome and clouds | neither sky shader reads it | mild over-transparency |

Modelled against the captured `PARITY-00` frame at 25° elevation: Firestorm
`(0.48, 0.63, 1.00)`, ours `(0.85, 0.90, 0.97)`. Fixing 2–4 alone moved it to
`(0.89, 0.93, 1.00)` — i.e. nowhere. Only #1 closed it. **Worth generalising: a
parameter probe cannot detect a wrong transfer function, because it scales every
probe equally.** All twenty-two sky probes would have read "matches in direction,
too pale in magnitude", which is indistinguishable from twenty-two separate
calibration errors.

The full derivation is in [ADR 0003](adr/0003-eep-parameter-model-and-lighting-seam.md)
under *"The sky is display-referred"*. With all four corrected the model reproduces
the whole dome to within 0.0000 per channel, so the remaining sky work is
verification rather than search.

## Water probes

Ranges from `llsettingswater.cpp:316-356`. Point at open water with the sun low
enough to give a specular track.

| Preset name | Parameter | Set to | What it should change |
|---|---|---|---|
| `PARITY-W1-fogdens-max` | Water fog density | 100 (max) | Opaque water |
| `PARITY-W2-fogdens-min` | Water fog density | 0.001 | Near-clear water |
| `PARITY-W3-fresnel-max` | Fresnel scale | 1.0 | Strong grazing-angle reflection |
| `PARITY-W4-blur-neg` | Reflection blur | −0.5 (min) | **`blur_multiplier` is SIGNED** — this is where treating it as roughness breaks |
| `PARITY-W5-normscale-max` | Normal scale | 10 on all axes | Very fine wave tiling |
| `PARITY-W6-wave-fast` | Wave directions | (20, 20) and (−20, 20) | Fast, sharply crossed wave motion |
| `PARITY-W7-underwater` *(no file — reuse `W0` and swim under)* | Camera below the surface | — | Underwater fog. **Known unimplemented in SLNG today**: `water_fog_density` and `underwater_fog_mod` are parsed and never applied, so expect no fog at all rather than the wrong amount |

## Reading a result

For each pair, one of three verdicts:

- **Match** — record it and move on. This is the useful half; a matching probe
  retires a whole parameter from suspicion.
- **Differs in magnitude** — our value is too strong or too weak but responds in
  the right direction. Usually a units or scale bug, like `star_brightness`
  needing `/500`.
- **Does not respond at all** — the parameter is parsed and dropped somewhere, or
  never parsed. Usually the cheapest class to fix, like `cloud_scroll_rate`
  reaching no shader.

Write the verdict next to the row. A probe run is only worth doing once if its
outcome is written down.

## What this protocol cannot settle

The night lighting probes (16, 17) will differ, and that is expected — per ADR
0003, scene lighting parity needs the per-fragment shader seam and cannot be
reached by tuning the current one-directional-light mapping. Run them anyway: they
establish the size of the gap before Phase E and become the acceptance test after
it.
