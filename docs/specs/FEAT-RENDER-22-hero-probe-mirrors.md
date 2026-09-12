# [FEAT-RENDER-22] Hero probe — real-time mirrors

- **Feature ID:** `FEAT-RENDER-22`
- **Track:** `render`
- **Status:** `✅ Done (code) — not yet confirmed in-world`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Third and last gap found by walking the same live A/B down from a shiny sphere to real content.
With `FEAT-RENDER-20` (reflection probe) and `FEAT-RENDER-21` (SSR) both in, the user stood in
front of a **PBR mirror**: Firestorm showed the room, a picture, a sofa, the floor. SLNG showed a
near-black surface with one vague blurred blob.

Neither existing feature can close that, and both failures are structural rather than tuning:

- **SSR cannot.** A mirror facing you reflects what is *behind the camera*. That is by definition
  not in the rendered frame, and SSR only knows the rendered frame.
- **The follow probe cannot.** It sits at the camera with `BoxProjection = false`, so its cubemap
  is treated as infinitely distant — right for sky, wrong for a room, and wrong for the mirror's
  own viewpoint.

## What the reference viewer does

A dedicated, continuously re-rendered **hero probe** (`reflectionProbeF.glsl:695-724`):

```glsl
uniform samplerCubeArray heroProbes;

void tapHeroProbe(inout vec3 glossenv, vec3 pos, vec3 norm, float glossiness)
{
    float clipDist = dot(pos.xyz, clipPlane.xyz) + clipPlane.w;
    ...
    w = mix(0, w, clamp(glossiness - 0.75, 0, 1) * 4);
    glossenv = mix(glossenv, textureLod(heroProbes, vec4(env_mat * refnormpersp, 0), (1.0-glossiness)*heroMipCount).xyz, w);
}
```

Three things to read off it, all of which this implementation follows:

1. It is a **cubemap** sampled by the reflection vector — not a screen-space planar texture. So
   Godot's `ReflectionProbe` is the right primitive, not a second camera into a viewport.
2. There is **exactly one**. `heroBox`, `heroSphere` and `heroShape` are single uniforms and
   `heroProbes` is read at a fixed index `0`. The reference viewer deliberately affords one
   mirror, because each one costs a full extra render of the scene.
3. It fades in over `clamp(glossiness - 0.75, 0, 1) * 4` — nothing below glossiness 0.75, i.e.
   **roughness 0.25**, which is the threshold used here to decide what counts as a mirror.

(The `clipPlane` term is the planar-mirror clip, used when rendering the probe so a mirror does
not capture what is behind it. Not reproduced here — see Known limits.)

## What was built

**Finding the mirror** (`ObjectRenderer`): glTF materials with `RoughnessFactor <= 0.25` are
recorded in `_mirrorMaterials` as they are resolved. The existing draw-distance cull sweep — which
already walks every visual on a spread cadence and already carries each one's `LoadedMaterialId` —
tests membership with one hash lookup per visual, no entity or component access, and only when a
mirror-grade material has been seen at all (for most scenes, never). The nearest visible candidate
is published as `MirrorPosition`/`MirrorRadius` when a sweep *completes*, since "nearest" is only
meaningful once every visual has been visited.

**Spending the probe** (`Boot.UpdateHeroProbe`): one `ReflectionProbe` with
`UpdateMode.Always`, parked on that surface, sized to `MirrorRadius * 2.5` (clamped 2–16 m) so a
mirror re-lights itself and not the whole room, and hidden entirely when no mirror is within
`HeroProbeMaxDistanceMeters` (24 m). Starts hidden, so a scene without mirrors pays nothing.

`UpdateMode.Always` is the exact setting `FEAT-RENDER-20` measured and rejected for the follow
probe — and it is the right choice here for the same reason the reference viewer accepts it: a
mirror that updates twice a second is not a mirror. The cost is bounded by scarcity (one probe,
nearest only, distance-gated, toggleable) rather than by cadence.

Toggle: `PostFxHeroProbe` ("Real-Time Mirrors" / "Echtzeit-Spiegel"), its own switch alongside the
probe and SSR ones — it is the only reflection feature here that re-renders continuously, so it is
the first thing to turn off if a mirror-heavy parcel ever costs too much.

## Measured cost

900 shiny instances + follow probe + SSR, 1280x720, VSync off, 60 warmup frames discarded, 240
measured, each configuration twice:

| configuration | avg frame |
|---|---|
| no hero probe | 4.512 ms |
| hero probe | 6.334 ms |
| no hero probe (repeat) | 3.652 ms |
| hero probe (repeat) | 6.256 ms |

≈ **+2.2 ms/frame, ~1.5x** while a mirror is in range — consistent with `FEAT-RENDER-20`'s
independent finding that `UpdateMode.Always` costs ~1.8x, and the reason it is gated as tightly as
it is. The baseline is noticeably noisier than the hero figures (3.65 vs 4.51); the hero runs
themselves agree closely (6.33 / 6.26), so the delta is not in doubt even if the baseline is.

## Acceptance Criteria

- [x] Mirror-grade surfaces detected by the reference viewer's own threshold, cited
- [x] Exactly one real-time probe, matching the reference viewer's single-hero design
- [x] Costs nothing when no mirror is in range (probe hidden, no capture)
- [x] Cost measured with repeats, recorded above
- [x] Toggleable and persisted, separately from the other two reflection features
- [x] Transitions logged (`[HeroProbe] ON/off`), capped, since "no mirror found" and "mirror
      reflecting a dark room" look identical in a screenshot
- [ ] Confirmed in-world against Firestorm — **not yet done**

## Technical Specs & Affected Files

- `app/scripts/ObjectRenderer.cs` — `_mirrorMaterials`, the sweep-time candidate scan,
  `MirrorPosition`/`MirrorRadius`
- `app/scripts/Boot.cs` — the hero `ReflectionProbe`, `UpdateHeroProbe`
- `app/scripts/UI/GraphicsSettings.cs`, `DesignPreferencesPage.cs`, both `app/i18n/*.json`
- Reference: `scratch/slviewer/.../class3/deferred/reflectionProbeF.glsl:695-724`

## Known limits

- **No clip plane.** The reference viewer clips the hero probe's render against the mirror plane
  so the mirror cannot capture what is behind itself. Godot's `ReflectionProbe` has no equivalent,
  so a thin mirror against a wall may pick up the wall behind it. Watch for it in-world; if it
  bites, the fix is a real planar reflection (second camera + clip) rather than a cubemap probe.
- **Cubemap, not planar.** As in the reference viewer, the reflection is a cubemap centred on the
  surface, which approximates a flat mirror well at a distance and less well close up.
- **One mirror.** Two mirrors in view means the nearer one reflects and the other does not —
  the same trade the reference viewer makes.
- The `MirrorRoughnessThreshold` uses the glTF material's roughness FACTOR only; a material whose
  roughness comes from an ORM texture is not considered.
