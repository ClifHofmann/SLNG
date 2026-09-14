# [BUG-RENDER-28] Godot averages overlapping probes; the viewer replaces

- **Feature ID:** `BUG-RENDER-28`
- **Track:** `render`
- **Status:** `✅ Done`
- **Owner:** `claude` (graphics-engineer)
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

`BUG-RENDER-27` put the real-time hero probe on the mirror, and the log confirms it arms:

```
[HeroProbe] ON at (156,7,24,1,-165,7) radius=1,21 size=3 | mirrorFound=True setting=True
```

The mirror still looked wrong: *„immer noch komisch, es sieht aus als zoomt der Spiegel"*.

## Why

Godot's clustered renderer **blends** every reflection probe whose box contains a surface —
`reflection_process` accumulates each probe's contribution and divides by the accumulated weight.
The follow probe's box is 40 m and contains everything, including a mirror standing inside the
3 m hero box. So the mirror was rendering the *average* of two captures: one taken at the mirror
(correct) and one taken at the camera (magnified, per `BUG-RENDER-26`). Half a right answer and
half a wrong one reads as a reflection that zooms.

The reference viewer does not average. Its hero tap is a replace:

```glsl
// reflectionProbeF.glsl:698-726, called at :888 with weight 1.0
void tapHeroProbe(inout vec3 glossenv, vec3 pos, vec3 norm, float glossiness)
{   ... w = boxIntersect(...) / sphereWeight(...);
    glossenv = mix(glossenv, tapped, w * clipDist); }
```

Inside the hero volume `w` is 1 and the hero sample simply wins.

## The fix

Godot exposes no per-probe weight to a spatial shader, but it does expose which **instances** a
probe may light: `ReflectionProbe.ReflectionMask`, matched against `VisualInstance3D.Layers`.

- the chosen mirror is moved onto a dedicated visual layer (`ObjectRenderer.MirrorVisualLayer`,
  layer 20), and its split surfaces with it
- the hero probe's `ReflectionMask` is that layer alone — it lights the mirror and nothing else,
  so it is not averaged into the wall behind it or the floor under it with a capture taken from
  inside the mirror
- while the hero probe is live, the follow probe's mask is everything *except* that layer

Only the **chosen** mirror moves. A second mirror across the room keeps the follow probe, because
the hero box does not reach it and a surface on the mirror layer with no probe in range would
render with no reflection at all. For the same reason the follow probe's mask goes back to
everything the moment the hero probe switches off.

The layer assignment is re-applied on every sweep rather than only on change: a visual that is
released and rebuilt comes back with a fresh `MeshInstance` at the default layer, and writing an
unchanged property is free where silently losing the layer is not.

No camera in this project sets a cull mask, so moving an instance off layer 1 does not hide it
from anything (checked, not assumed).

## Acceptance Criteria

- [ ] The mirror shows the room from its own viewpoint, at Firestorm's scale, and stops "zooming".
- [ ] The wall and floor around the mirror are unaffected by the hero probe.
- [ ] Switching "Echtzeit-Spiegel" off in Preferences returns the mirror to the follow probe
      rather than leaving it unreflective.

## Technical Specs & Affected Files

- `app/scripts/ObjectRenderer.cs` — `MirrorVisualLayer`, `SetActiveMirror`, called from the sweep's
  publish step.
- `app/scripts/Boot.cs` — `ReflectionMask` on both probes; `AppVersion` `v0.22.134-alpha`.

## Sub-tasks / Progress

- [x] Cause identified from Godot's probe blending, with the viewer's replace semantics as the
      reference.
- [x] Build (both), 711 tests, format, shader globals 28/28, selftest 38/38, `project.godot`
      untouched.
- [x] **Confirmed in-world 2026-09-14** (Agni): *„OK die Spiegelung funktioniert jetzt besser als in Firestorm“*.
