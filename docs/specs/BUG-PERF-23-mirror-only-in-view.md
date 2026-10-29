# [BUG-PERF-23] A planar mirror the camera is not looking at still costs a second scene render

- **Feature ID:** `BUG-PERF-23`
- **Track:** `render` / `perf`
- **Status:** 🧪 Review
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md), related [FEAT-PERF-15](../ROADMAP.md)
- **Branch:** `fix/BUG-PERF-23-mirror-in-view`

## Overview & Goal

Found 2026-10-29 at Sirens Beach (v0.28.48, Mirrors setting on). `[Perf]` carried `views=mirror:13.1/44.4,main:...`
for 7.5 minutes (84 five-second windows, 07:20:04-07:27:47) while no mirror was in sight: 34k draws instead of 17k
and ~44 ms of GPU for the mirror view alone (`renderGpuMs` 116 ms against 17 ms, 8 fps against 18). The log line:

    [PlanarMirror] ON at (180.2,32.8,-116.6) normal=(-1,0,0) radius=1.82 near=84.25 cos=0.05

The avatar stood at (176, 33); the mirror was ~84 m away on a wall whose plane was ~4 m to the side of the camera,
and the view ran parallel to that plane (`cos=0.05` is the floor of `Max(forward.normal, 0.05)`).

## Cause

`MirrorReflection.UpdateFor` had three gates: the camera is in front of the mirror plane, and the mirror's
`radius / distance` is at least `MinScreenFraction` (0.01). Nothing asked whether the mirror is inside the camera's
field of view, so a mirror to the side of, or behind, the view that was merely in front of its plane and within
~180 m rendered the whole scene a second time.

## Fix

**Second attempt (v0.28.50-alpha), the one that matters.** The first fix (v0.28.49) was a frustum test and did not
help: in-world, with it, the mirror view stayed on (9 of 18 windows, 30k draws, 18-19 fps). The mirror was *in*
the frustum: the camera looked along the wall (`cos=0.05`), so the mirror sat dead ahead at ~82 m, seen edge-on.
Edge-on, it has no width. `MirrorReflection.ScreenFraction` now measures the narrow side:
`radius * (planeDistance / lineOfSight) / lineOfSight`, i.e. the radius times how squarely the line of sight meets
the glass. 1.82 m at 84 m, plane 4.2 m from the eye: 0.0011, under `MinScreenFraction` (0.01), so no render.
Head-on at 84 m is 0.0217 and still renders; that is a quality call for the maintainer.

**First attempt (v0.28.49-alpha), kept because it is correct for a mirror behind or beside the view:**
`UpdateFor` returns false (the caller idles the mirror viewport) when a sphere of the mirror's radius around its
centre does not touch the view frustum: `MirrorReflection.InView`, a pure function in camera space
(`depth + radius <= 0`, or `|x|`/`|y|` beyond `depth * tan(fov/2) + radius * sqrt(1 + tan^2)`). The half-FOV
tangents come from the camera's `Fov`, `KeepAspect` and the viewport's aspect ratio. Errs towards "in view".

Not changed: `MinScreenFraction`. A tiny mirror straight ahead at 90 m is still rendered; whether that deserves a
higher threshold is a quality call for the maintainer.

## Acceptance Criteria

- [x] `InView` self test: ahead, behind, far to the side, far below, edge touching, clear of the frustum, radius
      reaching in front of the camera, wholly behind (`planar mirror: only rendered while in view`).
- [x] `dotnet build app/SLNG.App.csproj` clean, headless `--selftest` 182 ok (the check now also covers 6 sizes, among them the Sirens case).
- [ ] In-world, at the spot above with `-Diag`: no `views=mirror` while the mirror is out of view; the mirror still
      works when it is in view (look at it from 3 m and from 15 m, then turn away and back).

## Sub-tasks / Progress

- [x] `InView` and the gate in `UpdateFor` (v0.28.49-alpha)
- [x] `ScreenFraction`, edge-on mirrors (v0.28.50-alpha)
- [x] Self test
- [ ] In-world check
