# [BUG-RENDER-26] The reflection has no parallax, so everything in it is too big

- **Feature ID:** `BUG-RENDER-26`
- **Track:** `render`
- **Status:** `✅ Done`
- **Owner:** `claude` (graphics-engineer)
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Immediately after `BUG-RENDER-25` gave the fake mirror its reflection back, the user reported the
next thing: *„der Zoom scheint irgendwie zu groß zu sein — links sieht man das Fenster recht klein
(kommt mir realistisch vor), rechts sieht man das Fenster riesig"*. The content is right now; the
scale is not.

## Why

With `BoxProjection` off, Godot treats a captured cubemap as **infinitely distant**: the sample
depends only on the reflection direction, so every object appears at the angular size it had *from
the probe*. Our probe rides the avatar.

- cubemap: the window at its distance from the avatar, `d_obj`
- a real mirror: the image travels eye → mirror → object, roughly `2·d_mirror + d_obj`

Standing an arm's length from a mirror in a small room that is about a **2× magnification**, which
is what "riesig" measures.

The reference viewer never has this problem, because every one of its probes carries an influence
**volume** and the sample is intersected against it before being used — `boxIntersect` and
`sphereWeight`, reflectionProbeF.glsl:705-717. The reflected point therefore lands on real geometry
at its real distance. Godot's equivalent is `BoxProjection` plus a box that matches the room.

## The fix

The room is something that can be **measured** rather than guessed. Six axis-aligned rays from the
capture point, on the existing re-bake cadence (twice a second at most, and only on a frame that is
already re-capturing):

- solid geometry only (mask 1) — phantom prims are the region's curtains, foliage and
  click-catchers, and letting one define a wall would put the box inside the room's own decoration
- a hit gives that axis's half-extent, clamped to [1 m, 12 m]; a miss gives the maximum
- it counts as a room only with a **ceiling**, a **floor**, and at least **three of four** walls —
  three, because a room with an open doorway or a floor-to-ceiling window is still a room
- the probe node then sits at the room's centre with `Size` = the measured box, and
  `OriginOffset` = capture point − centre, since Godot captures at `global_position + origin_offset`

Outdoors nothing encloses the rays, the test fails, and the probe stays exactly as it was — which
matters: parallax against a fictional 12 m box would bend the sky reflection `BUG-RENDER-23` just
got right.

Six rays rather than a sampled sphere because the box the correction needs *is* axis-aligned
(Godot has no oriented probe volume), so a diagonal hit would have to be flattened onto these six
axes anyway.

## Round 2: the decision was flipping

First live look, `v0.22.131-alpha`: *„beim Rauszoomen sieht das komisch aus"*. The `[ReflProbe]`
line says why, and it is not the parallax — it is the **decision**:

```
bake #200  boxed=False
bake #300  boxed=True  room=(7,5x4,1x12,1) offset=(-2,2,-0,8,1,9)
bake #400  boxed=False
bake #500  boxed=False
```

The room test is a six-ray sample of a world built by hand out of prims: a step under a beam, past
an open doorway or behind a sofa flips one ray. With a one-sample decision that flipped the entire
reflection between parallax-corrected and infinite — and because Godot's probe `Size` is *also* its
influence volume, each flip changed both the SCALE of every reflection and WHICH objects had one.

Two guards, both about stability rather than accuracy:

- **Hysteresis.** Three consecutive disagreeing measurements before the state flips, about 1.5 s
  at the re-bake cadence.
- **No breathing box.** A box already in use is only replaced when the new measurement differs by
  more than 25% on an axis (or its centre moves that far), so walking around a room does not drag
  every reflection in it along.

Transitions are logged (`[ReflProbe] parallax ON/off at bake #N`), capped at 20, so the next report
says how often it actually switches instead of leaving it to a 1-in-100 sample.

Note in that measurement: `room=(7,5x4,1x12,1)` — the 12,1 is the clamp, i.e. that axis found no
wall at all. A room open on one side still counts (three of four walls), and the parallax on the
open axis is then placed at the clamp distance. That is an approximation, deliberately: requiring
four walls rejected exactly the interiors this exists for.

## Acceptance Criteria

- [ ] Indoors, objects in a mirror are the size Firestorm shows them, A/B from one camera position.
- [ ] Moving and zooming inside a room does not snap the reflection between two scales.
- [ ] Outdoors the probe is unchanged — `boxed=False` in the `[ReflProbe]` line, sky reflection as
      before.
- [x] The measurement runs only on a re-bake, not per frame.

## Technical Specs & Affected Files

- `app/scripts/Boot.cs` — `TryMeasureRoomBox`, the box/no-box branch in `UpdateReflectionProbe`,
  and `boxed=`/`room=`/`offset=` on the `[ReflProbe]` diagnostic line.
- `app/scripts/Boot.cs` — hysteresis, box-change threshold, transition logging; `AppVersion`
  `v0.22.132-alpha`.

### Known trade-off

A probe's `Size` is also its **influence volume**: while the box is the room, objects outside the
room stop receiving this probe. The reference viewer answers that with many probes at once; we
afford one. Indoors, what is outside the room is mostly not on screen, which is why this is
acceptable — but it is a real difference, and it is the argument for eventually placing several
probes rather than one that follows.

An axis-aligned box also only matches an axis-aligned room. A round or diagonally-built interior
gets an approximation, which is still far closer than infinity.

## Sub-tasks / Progress

- [x] Ported against the viewer's own parallax (`boxIntersect`/`sphereWeight`), not invented.
- [x] Build (both), 711 tests, format, shader globals 28/28, selftest 38/38, `project.godot`
      untouched.
- [x] **Confirmed in-world 2026-09-14** (Agni): *„OK die Spiegelung funktioniert jetzt besser als in Firestorm“*.
