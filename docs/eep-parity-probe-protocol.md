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

## Setup, once

You need a region you can set the environment on. Howletts qualifies.

1. In Firestorm: **World → Environment → My Environments → New Sky**.
2. Start from **Default** (not from the region's current sky) so every value is
   the viewer's own baseline. This is the "neutral" reference.
3. Save it as `PARITY-00-neutral`.
4. For each row of the table below, duplicate the neutral sky, change **only** the
   named parameter to the given value, and save it under the given name.

Then per probe: apply it to the region, and capture both viewers with the camera
in the **same place** and the day cycle **not moving** (a single-frame sky has no
cycle, which is why the neutral base is a fixed sky rather than a day cycle).

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
| `PARITY-13-cloudvar-max` | Cloud variance | 1.0 | Strong domain warp; ragged cloud edges |
| `PARITY-14-cloudscale-small` | Cloud scale | 0.1 | Small tight cloud cells |
| `PARITY-15-stars-max` | Star brightness | 500 (max) | Only visible at night — set the sun below the horizon too |
| `PARITY-16-night-moonbright` | Sun below horizon, moon up, moon brightness 1.0 | | **The lighting probe.** Ground brightness is the measurement, not the sky |
| `PARITY-17-night-moondark` | Same, moon brightness 0.0 | | Should be darker than 16 and carry no solar halo |

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
| `PARITY-W7-underwater` | Camera below the surface | any | Underwater fog — **known unimplemented in SLNG today** |

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
