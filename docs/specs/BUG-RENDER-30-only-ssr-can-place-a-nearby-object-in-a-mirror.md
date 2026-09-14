# [BUG-RENDER-30] Only SSR can place a nearby object in a mirror — and ours faded out at 2 m

- **Feature ID:** `BUG-RENDER-30`
- **Track:** `render`
- **Status:** `✅ Done`
- **Owner:** `claude` (graphics-engineer)
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

First side-by-side with a subject in the mirror rather than furniture: the avatar standing in front
of it. Firestorm shows her small, sharp, at a plausible distance. SLNG shows her filling the glass
and blurred — *„besser, aber immer noch komisch, viel unschärfer und der Avatar ist überdimensional
größer"*.

## Why no cubemap could have fixed this

`BUG-RENDER-29` added parallax to the hero probe, and parallax against a box only places geometry
that lies **on that box**. The room's walls do; an avatar standing in the middle of the room does
not. Her reflection is therefore projected onto the far wall's distance no matter how well the room
is measured — too big, and blurred by the cubemap's angular resolution stretched over that distance.

No arrangement of cubemap probes fixes that. The technique that does is screen-space reflection,
because it traces the **actual rendered frame** instead of a capture from one point: an object that
is on screen is reflected at its true size and perspective.

That is where Firestorm's correct avatar comes from. The viewer lets SSR *replace* the environment
sample wherever it hits, in the legacy path, with no glossiness gate:

```glsl
// reflectionProbeF.glsl:867-885, inside sampleReflectionProbesLegacy
tapScreenSpaceReflection(1, tc, pos, norm, ssr, sceneMap, glossiness);
glossenv  = mix(glossenv,  ssr.rgb, ssr.a);
legacyenv = mix(legacyenv, ssr.rgb, ssr.a);
```

## What was wrong with ours

`FEAT-RENDER-21` enabled SSR with settings chosen for an outdoor scene, and one of them was doing
real damage:

```csharp
SsrMaxSteps = 32,
SsrFadeOut  = 2.0f,   // reflection is gone beyond two metres of ray travel
```

An avatar a metre in front of a mirror is more than two metres away along the reflected ray, so SSR
faded out before it could contribute anything and the whole mirror was left to the cubemap — the
one technique that structurally cannot place her.

`SsrMaxSteps` 32 → 96, `SsrFadeOut` 2.0 → 24.0. `SsrFadeIn` stays at 0.15, which is what keeps the
surface itself from smearing into its own reflection.

Cost: longer traces cost per pixel. `FEAT-RENDER-21` measured +0.19 ms/frame at 32 steps, so this
is roughly +0.5 ms on that scene — an estimate, not a measurement, and it is recorded as one. The
whole feature stays behind the `PostFxSsr` toggle ("Spiegelungen (Bildschirmraum)").

## The measurement this should have had from the start

`tools/testassets/mirror_probe.lsl`: a **1.00 m cube** that steps itself through 0.5 / 1 / 2 / 4 /
6 m from a mirror and announces where it is. One metre means a pixel measurement *is* the scale,
and the cube's silhouette makes a stretched reflection distinguishable from a merely scaled one.

The ratio to read off two screenshots:

| model | apparent height in the mirror |
|---|---|
| real mirror (Firestorm is the control) | `h · d_direct / (a + c)` |
| cubemap captured at the mirror | `h / c` — too big by `(a + c) / c` |

with `a` the viewer's distance to the glass and `c` the object's. Every round from `BUG-RENDER-23`
to here was argued from screenshots of shop furniture at unknown distances; this turns the next one
into a quotient.

## Acceptance Criteria

- [ ] With the probe cube at 1 m, the mirrored/direct height ratio matches Firestorm's within ~10%.
- [ ] An avatar in front of a mirror is reflected at the size Firestorm shows.
- [ ] Turning "Spiegelungen (Bildschirmraum)" off visibly returns the mirror to the cubemap, which
      is the A/B that attributes any remaining error to one technique or the other.
- [ ] SSR cost re-measured rather than estimated.

## Technical Specs & Affected Files

- `app/scripts/Boot.cs` — SSR steps and fade-out; `AppVersion` `v0.22.136-alpha`.
- `tools/testassets/mirror_probe.lsl` *(new)*.

## Sub-tasks / Progress

- [x] Cause identified structurally (what a box projection can and cannot place), not by tuning.
- [x] Build (both), 711 tests, format, shader globals 28/28, selftest 38/38, `project.godot`
      untouched.
- [x] **Confirmed in-world 2026-09-14** (Agni): *„OK die Spiegelung funktioniert jetzt besser als in Firestorm“*.
- [ ] Cost measured.
