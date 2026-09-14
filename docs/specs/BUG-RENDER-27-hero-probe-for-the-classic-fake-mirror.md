# [BUG-RENDER-27] Give the one real-time probe to the mirror, not to the camera

- **Feature ID:** `BUG-RENDER-27`
- **Track:** `render`
- **Status:** `✅ Done`
- **Owner:** `claude` (graphics-engineer)
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Third live look at the same wall mirror, `v0.22.132-alpha`: *„das sieht nun komisch aus, ich sehe
verschwommen die Ecke vom Bett und ich kann Wolken sehen (die nicht sichtbar sein sollten, da ich
ja im Haus bin)"*.

`BUG-RENDER-26` tried to fix the reflection's scale by parallax-correcting the camera-following
probe against a measured room box. The measurement in this house came back
`room=(7,5x4,1x12,1)` — and that 12,1 is the clamp, meaning that axis **found no wall at all**.
The box therefore claimed a plane that does not exist, reflections were mapped onto it, and the
open side let the sky in. Smeared image, clouds indoors.

Two conclusions, and the second is the real one.

## 1. A box is only as good as its measurement

`RoomProbeMinWallHits` goes from three to four. Three was a deliberate choice — "a room with an
open doorway is still a room" — and it was wrong in a specific way: it did not treat the open axis
as open, it invented a wall at the clamp distance. Now a box is used only where all six directions
were actually measured. An open-plan interior falls back to the infinite cubemap, which is wrong in
a known and stable way rather than wrong in a way that fabricates geometry.

## 2. The one real-time probe belongs on the mirror

The deeper error is spending the probe on the camera at all. A camera probe can only ever
approximate a mirror, whatever box is wrapped around it; a probe **at the mirror** is what a mirror
is. `FEAT-RENDER-22` already built exactly that — one `UpdateMode.Always` hero probe, sized to the
surface, switched off entirely when nothing is in range — and then spent it on a candidate test
that matches almost no SL content: glTF materials with roughness ≤ 0.25.

Candidacy now also includes SL's **classic fake mirror**: a face that is both **FULLBRIGHT** and
**Shiny HIGH**. That is not a guess about what looks mirror-like, it is the exact recipe the
reference viewer routes to `fullbrightShinyF.glsl` (see `BUG-RENDER-25`), and every mirror sold in
SL that predates PBR is built this way — including this one, which the click dump reported as
`45 faces tex=740da84d shiny=3 fullbright`.

Both conditions together, plus a minimum bounding radius of 0.4 m: fullbright alone is signs and
screens, shiny alone is half the metal in any build, and a small fullbright-shiny face is trim or
jewellery. Ranking is unchanged — an object the sim *flags* as a mirror (`BUG-RENDER-24`) still
outranks any inferred one, at any distance.

### Where this deliberately departs from the reference viewer

The viewer would not give this face a hero probe; it answers it from a dense field of automatic
probes, each with its own influence volume. We afford one probe. Spending it on the surface whose
entire purpose is to reflect gets closer to the reference *result* than spending it on the camera
does — which is the trade `BUG-RENDER-26` measured the hard way.

## Acceptance Criteria

- [ ] The reported mirror shows the room from the mirror's own viewpoint, at Firestorm's scale.
- [ ] `[HeroProbe] ON` appears in the log when approaching it, `off` when leaving.
- [ ] An ordinary fullbright sign or a small shiny bead does not claim the probe.
- [ ] Open-plan interiors report `boxed=False`; only fully enclosed rooms box.

## Technical Specs & Affected Files

- `app/scripts/ObjectRenderer.cs` — `_legacyMirrors`, maintained in `UpdateVisual` where the faces
  are already in hand; the cull sweep adds it to the candidate test behind the size check.
- `app/scripts/Boot.cs` — `RoomProbeMinWallHits` 3 → 4 with the measurement that disproved three;
  `AppVersion` `v0.22.133-alpha`.

## Sub-tasks / Progress

- [x] Criterion taken from the viewer's own pass selection, not from appearance.
- [x] Build (both), 711 tests, format, shader globals 28/28, selftest 38/38, `project.godot`
      untouched.
- [x] **Confirmed in-world 2026-09-14** (Agni): *„OK die Spiegelung funktioniert jetzt besser als in Firestorm“*.
- [ ] Cost re-measured with a fake mirror in range (FEAT-RENDER-22 measured +2.2 ms/frame for a
      glTF one; the capture is the same, but fake mirrors are far more common than PBR ones, so
      how often the probe is armed is a different question).
