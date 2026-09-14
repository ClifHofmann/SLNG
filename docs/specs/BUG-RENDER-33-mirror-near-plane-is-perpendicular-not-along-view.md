# [BUG-RENDER-33] The mirror's near plane was measured perpendicular, not along the view

- **Feature ID:** `BUG-RENDER-33`
- **Track:** `render`
- **Status:** `✅ Done`
- **Owner:** `claude` (graphics-engineer)
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

First defect found in the planar mirror after it was confirmed working:

> *„Wenn ich halbwegs gerade auf den Spiegel schaue sehe ich die Spiegelung. Sobald ich etwas zur
> Seite rolle zeigt der Spiegel falsche Spiegelungen"*

Straight on, correct. From the side, the glass fills with the wall it hangs on.

This is `BUG-RENDER-32`'s own recorded limit — *"near-plane clipping, not an oblique frustum"* —
arriving exactly where it had to, and it was worse than it needed to be.

## Why

The reflection camera sits **behind** the mirror plane, so the wall the mirror hangs on is between
it and the glass and has to be clipped away. Godot's near plane is perpendicular to the **view
axis**; the mirror plane is not, unless you are looking straight at it.

The first version set `Near` to the **perpendicular** distance from the eye to the mirror plane.
The distance along the view axis is `perpendicular / cos(theta)`, so the two agree only at normal
incidence, and the gap grows as the view flattens: the wall between `perpendicular` and
`perpendicular / cos(theta)` survives the clip and fills the reflection.

## The fix

```csharp
float cosTheta  = Mathf.Max(reflectedForward.Dot(normal), 0.05f);
float alongView = distance / cosTheta;
_camera.Near    = Mathf.Max(0.05f, alongView);
```

- **Along the view axis**, which is what `Near` actually measures.
- **Cosine floored** at 0.05, so a view nearly parallel to the glass cannot push the near plane to
  infinity and empty the reflection instead — trading one failure for the other would not be a fix.

### The first attempt, and why it was worse

`v0.22.139-alpha` also subtracted the mirror's radius, reasoning that a plane perpendicular to the
view axis touches the mirror plane at one point only, so clipping at the centre's distance would
cut room geometry seen through the nearer half of the glass.

Live, that was worse than the bug it fixed: *„jetzt sieht es ganz falsch aus"*, with the wall
filling the glass at angles where `v0.22.138` had been correct. The reason is one fact the
reasoning had missed — **a mirror hangs ON a wall, so the wall is at the same distance as the
mirror plane**. Backing the near plane off by the mirror's radius therefore stopped clipping the
wall at all, head-on included.

Reverted in `v0.22.140-alpha`. The edge clipping the margin was meant to prevent is real but is the
lesser artefact: a little missing room near the glass edge at flat angles, against a wall covering
everything at every angle.

`[PlanarMirror]` now reports `near=` and `cos=`, because "the mirror shows the wall" and "the
mirror shows nothing" are the two failure modes of this one number and look nothing alike.

## What would fix it properly

An **oblique frustum** (Lengyel): skew the projection matrix so the near plane *is* the mirror
plane at every angle. Godot exposes no custom projection on `Camera3D` — `set_frustum` is off-axis,
not oblique. The reference viewer avoids the problem differently, with a clip plane evaluated in
every shader (`mirrorClip`), which needs a **per-viewport** uniform; a Godot shader global is
per-frame and would clip the main view too.

So this stays an approximation with a known shape: correct head-on, a shrinking sliver of wall as
the view flattens.

## Acceptance Criteria

- [ ] Rolling the camera to the side keeps the reflection correct, with no wall filling the glass.
- [ ] Straight-on is unchanged.
- [ ] A view nearly parallel to the glass degrades to an empty or partial reflection, not to a wall.

## Technical Specs & Affected Files

- `app/scripts/MirrorReflection.cs` — the near-plane computation, plus `LastNear` / `LastCosTheta`.
- `app/scripts/Boot.cs` — `near=` / `cos=` on the `[PlanarMirror]` line; `AppVersion`
  `v0.22.140-alpha` (`139` was the first attempt; see above).

## Sub-tasks / Progress

- [x] Recognised as the documented limit rather than re-derived from scratch.
- [x] Build (both), 711 tests, format, shader globals 28/28, selftest 39/39, `project.godot`
      untouched.
- [x] **Confirmed in-world 2026-09-14** (Agni): *„OK das funktioniert“*.
