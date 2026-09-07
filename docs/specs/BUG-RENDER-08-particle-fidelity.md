# [BUG-RENDER-08] Particle system — width / strobe / turnover fixes, plus known fidelity gaps

- **Feature ID:** `BUG-RENDER-08`
- **Track:** `render`
- **Status:** `✅ Done` — the three fixes (width, strobe, morph turnover) shipped `v0.20.31-alpha` and are confirmed in-world. Closed 2026-09-07 (doc triage); the remaining low-priority fidelity gaps below are split out as `FEAT-RENDER-10` (GPU-driven particle path).
- **Priority:** the shipped fixes were user-driven; the remaining gaps are **LOW** — decorative
  content, no crash / data / correctness impact.
- **Owner:** `claude`
- **Version:** `v0.20.31-alpha`
- **File:** `app/scripts/ObjectParticles.cs` (net ~290 lines), plus a one-line `ObjectRenderer.cs`
  wire-through and the `AppVersion` bump.

## Overview

Reported live on Agni across a long session, testing against Firestorm with two flame emitters:
an orange lantern flame and a blue-flame prim running the classic **"SLS Particle Script 0.3" by
Ama Omega** (a *static* one-shot `llParticleSystem()` — no jitter, no re-send loop; the user
supplied the full script, which is what finally pinned the remaining issues as SLNG-side rather
than script-side).

Three separate defects were found and fixed; two gaps remain that `CpuParticles3D` cannot close
without a different particle implementation.

## Fixed

### 1. "zu breit" on a square-ish particle — `ScaleCurveZ` pinned at 1.0
`ConfigureScale` set `ScaleCurveZ = BuildScaleCurve(1f, 1f)` while `ScaleCurveX`/`Y` carried the
real metre sizes (~0.06). The flat `QuadMesh` billboard has zero Z extent, so in principle this is
inert — but `BillboardKeepScale` multiplies the MODELVIEW by
`diag(len(col0), len(col1), len(col2))`, and a col2 length of 1 against col0/col1 lengths of
~0.06 is a ~16:1 anisotropy that any shear or non-XY quad interaction turns into a visible
horizontal stretch. `ScaleCurveZ` now tracks `ScaleCurveX`. **Confirmed in-world** on the orange
lantern: *"die flamme sieht jetzt gut aus."*

### 2. Whole-emitter "an/aus" strobe — `Restart()` + source-age expiry per re-send
`Apply` unconditionally called `Restart()` (clears + refills the pool) on every non-identical
re-send, and `_sourceAge` (checked against `PSYS_SRC_MAX_AGE` in `_Process` to stop `Emitting`)
was only reset on a structural change. A flame script re-sending ~10×/s with a short
`PSYS_SRC_MAX_AGE` therefore expired between re-sends — `_Process` flips `Emitting` off, the next
`Apply` flips it on — an on/off strobe. Fixes: `Restart()` now fires **only on the first Apply**
(for the initial `Preprocess` pre-warm); the viewer keeps every already-emitted particle alive
across a re-send (`LLViewerObject::setParticleSource` = `deleteParticleSource` + `createPSS`, only
the *source* is rebuilt). `_sourceAge` is re-armed on **every** Apply, matching that fresh-source
behaviour, so it still stops correctly once the script stops re-sending.

### 3. "in FS one morph takes ~1 s, in SLNG it has already looped 5 times" — `Amount` reassigned every re-send
`Apply` re-assigned `CpuParticles3D.Amount` (and `Lifetime`, `Preprocess`, re-ran
`ConfigureEmission`) on every ~10–45 Hz re-send. Assigning `Amount` in Godot rebuilds the particle
buffer and deactivates every live particle — so the whole flame was wiped and re-seeded that many
times a second. These pool-shaping properties are now set **only on a structural change**
(lifetime / particle count / pattern / flag word / emission geometry). A pure scale/colour re-send
only re-points the persistent scale curves and colour ramp in place (`SetPointValue` / `SetColor`
— Godot samples those live, no reset). **Confirmed in-world:** *"es morpht jetzt langsam."*

Also in this pass: `FixedFps = 0` (Godot's default 30 batches emission AND death into 30
discrete steps/s — a density pulse); persistent `Curve`/`Gradient` objects re-pointed instead of
reallocated per frame; a gentle time-based (`_Process`, τ ≈ 1.2 s) ease of the shared curve
endpoint toward the latest script value so a jittering script's shape drift reads as a slow morph
rather than a per-packet snap; `[Particles]` diagnostic (always on, one line per structural state)
carrying scale/colour/flags/accel/srcMaxAge/curve values, plus an `albedo resolved WxH` line.

## Still open — LOW priority

### A. Blue-flame (`4a548641`, non-square `endSize`) renders wider than Firestorm
The SLS script's `endSize = <.5, 1.0, .1>` is a NARROW, TALL particle (SL scale is
`<width, height, _>`). `[Particles]` logs `curveX->0.5` and `curveY->1.0` **correctly**; the
texture decodes 1024×1024 with ~square content; `nodeWorldScale=(1,1,1)`; `ScaleAmount=1`. Yet the
flame renders visibly wider than tall. **Swapping the X and Y scale curves had zero visible
effect** — so the width is not coming from the split curves at all. Unresolved `CpuParticles3D` +
`BILLBOARD_PARTICLES` + `billboard_keep_scale` interaction; needs hands-on Godot debugging
(RenderDoc, live property inspection) or the rewrite in C below. Not reproducible / inspectable
from logs alone. Also worth checking there: particle-quad UV V-origin (SL is bottom-origin, Godot
top — `[[sl-texture-v-origin-flip]]`; `ObjectParticles` uses raw `QuadMesh` UVs, no flip) and the
`PSYS_SRC_OMEGA` node-spin path for a non-ANGLE pattern.

### B. No per-particle X/Y morph (Firestorm's subtle independent width/height breathing)
Architectural. The viewer captures `PART_START/END_SCALE` **per particle at birth** and each
particle interpolates its own snapshot; with the script jittering X and Y independently, 200
particles average into a soft, independently-wandering aspect. Godot's `CpuParticles3D` shares one
scale curve across the whole pool — there is no per-particle start/end. The current τ ≈ 1.2 s
shared-endpoint ease approximates it pool-wide (all particles in lockstep) but cannot reproduce
the per-particle independence.

### C. The real fix for A and B: a dedicated particle path
`GpuParticles3D` with a `ParticleProcessMaterial` / custom process shader (per-particle end values
in `CUSTOM`), or a hand-managed `MultiMesh` pool with a per-spawn snapshot and a `_Process`
interpolation. Either is a scoped task, not a tweak, and would also let `PSYS_PART_FOLLOW_VELOCITY`
(the screen-space roll, currently unimplemented — see `BUG-RENDER-06`'s notes) be done properly.

## Tests

None new — particle rendering has no unit-test surface in `SLNG.*.Tests` (it is all Godot node
behaviour in `app/`). `--selftest` 29/29 unchanged, `dotnet build app/` clean (0 warnings).
