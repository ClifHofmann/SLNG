# [BUG-RENDER-29] The hero probe needed the parallax too

- **Feature ID:** `BUG-RENDER-29`
- **Track:** `render`
- **Status:** `✅ Done`
- **Owner:** `claude` (graphics-engineer)
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

With the hero probe on the mirror (`BUG-RENDER-27`) and no longer averaged with the follow probe
(`BUG-RENDER-28`), the reflection was still magnified: *„die Vase ist im Spiegel viel zu groß und
ich sehe auf einer 2. Ebene Wolken"*.

## Why — and why the reference source is misleading if read alone

The viewer's hero tap samples with the plain reflection vector and **no correction whatsoever**:

```glsl
// reflectionProbeF.glsl:724
glossenv = mix(glossenv,
               textureLod(heroProbes, vec4(env_mat * refnormpersp, 0), (1.0-glossiness)*heroMipCount).xyz,
               w);
```

Reading that line and copying it is what `BUG-RENDER-27` did. The trap is that the viewer only ever
grants a hero probe to an object a creator **flagged** as a mirror. Everything else — this fake
mirror included — is answered from its dense field of automatic probes, and those *are*
parallax-corrected (`boxIntersect` / `sphereWeight`, :705-717). Copying the hero path alone copied
the half that has no correction.

The arithmetic of the remaining error: an infinite cubemap captured at the mirror shows an object
at the angular size it has **from the mirror**, `S/c`, where the true mirror image is `S/(a+c)`
with `a` the viewer's own distance to the glass. Standing 1.5 m from a mirror with a vase 2 m
behind it, that is **1.75× too big** — which is what the vase measured.

## The fix

Give the hero probe the same treatment `BUG-RENDER-26` gave the follow probe, but measured **from
the mirror** instead of from the avatar: six rays, box projection, node at the room's centre with
`OriginOffset` keeping the capture at the mirror.

Two differences from the follow probe's version, both consequences of `BUG-RENDER-28`'s mask:

- **Two walls suffice, not four.** A mirror hangs on a wall (one ray answered at arm's length) and
  the rooms it hangs in are routinely open on a side. An axis that finds nothing keeps the clamp
  distance, and on an open side that is roughly right — what is out there really is far away.
- **The box is a pure parallax volume.** For the follow probe, `Size` is also its reach, so a
  wrong box silently unlights half a room. Here the mask has already decided what this probe
  lights (the chosen mirror, alone), so the box carries no second meaning.

No measurable room falls back to the previous behaviour: an infinite cubemap at the mirror, which
is what the viewer's hero probe does and still far closer than a capture taken at the camera.

### Also fixed here

`BUG-RENDER-28`'s follow-probe mask assignment had landed **inside** the `_heroProbeStateLogs < 12`
block, so it would have stopped being maintained after the twelfth transition. It is now outside,
where it belongs, and the transition log gained `boxed=`.

## The clouds

Not a defect: the room is open on one side, and a mirror facing it reflects what is out there. The
"second level" they appeared on is the parallax box's far plane on that open axis. Firestorm shows
sky in the same mirror. Worth re-checking against it once the scale is right, since the two are
easy to confuse while everything is the wrong size.

## Acceptance Criteria

- [ ] The vase in the mirror is the size Firestorm shows it, A/B from one camera position.
- [ ] `[HeroProbe] ON ... boxed=True` in the log inside a room.
- [ ] Outside a measurable room, `boxed=False` and the mirror behaves as in `v0.22.134-alpha`.

## Technical Specs & Affected Files

- `app/scripts/Boot.cs` — `TryMeasureRoomBox` takes the wall requirement as a parameter;
  `UpdateHeroProbe` rewritten around it; mask assignment moved out of the log-capped block;
  `AppVersion` `v0.22.135-alpha`.

## Sub-tasks / Progress

- [x] Root-caused from the angular-size arithmetic, with the viewer's two probe paths separated.
- [x] Build (both), 711 tests, format, shader globals 28/28, selftest 38/38, `project.godot`
      untouched.
- [x] **Confirmed in-world 2026-09-14** (Agni): *„OK die Spiegelung funktioniert jetzt besser als in Firestorm“*.
