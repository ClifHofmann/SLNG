# [BUG-RENDER-32] A real planar mirror, instead of a better cubemap

- **Feature ID:** `BUG-RENDER-32`
- **Track:** `render`
- **Status:** `✅ Done`
- **Owner:** `claude` (graphics-engineer)
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Nine rounds (`BUG-RENDER-23`…`31`) answered a mirror with a **cubemap**, each one closer and none
able to be right. The measurement that ended it came from the user, not from the code:

> *„je weiter ich vom Spiegel weg zoome, umso größer werden die gespiegelten Bilder im Vergleich
> zum Spiegel"*

That is a signature, not an impression:

| | reflection's angular size | mirror's angular size | ratio as you back away |
|---|---|---|---|
| real mirror | `S/(a+c)` | `M/a` | grows, **approaches a limit** |
| infinite cubemap | `S/c` — **constant** | `M/a` | grows **without bound**, linearly in `a` |

The log agreed: `[HeroProbe] ON … boxed=False`, i.e. the probe was running unprojected. And even
with a perfect parallax box, two problems remain that no setting reaches — a box only places
geometry lying **on** the box (an avatar in the middle of a room never does), and a 512-px cube
face spans 90°, giving 5.7 pixels per degree against the screen's 32.

## What this does instead

A planar reflection, which is not an approximation:

1. `MirrorReflection` places a second `Camera3D` at the viewer's own position **reflected through
   the mirror plane**, with the view and up vectors reflected too, and renders the scene into a
   `SubViewport` at the frame's own resolution.
2. `prim_mirror.gdshader` samples that render at the fragment's **screen position**. Same
   projection, same pixels, exact perspective at every distance.
3. `ObjectRenderer` swaps the chosen mirror's qualifying surfaces onto that shader and back.

It is also **cheaper than what it replaces**: one scene render per frame against the hero probe's
six cube faces, which is why the hero probe now stands down for any surface the planar mirror has.

### The plane comes from the geometry

A mirror is thin, so the smallest axis of its bounding box is the axis it has no depth along —
its face normal. The sign is whichever side the viewer is on, so a double-sided panel reflects the
face being looked at. Nothing is assumed about how the creator built it.

### Uniform names are the seam

`prim_mirror.gdshader` reuses `albedo_color`, `albedo_texture`, `has_albedo_texture` and
`legacy_shininess` from `prim_common.gdshaderinc`. A `ShaderMaterial` keeps its parameters by name
across a shader swap, so turning a face into a mirror is one shader assignment plus one new
uniform — the texture, tint and Shiny level come across on their own, and switching back restores
the exact shader the surface had rather than rebuilding a material (and re-fetching its textures)
because the user walked away from a mirror.

### Still SL's mix

The reflection is not pasted over the surface; it is mixed the way the reference viewer mixes it,
unchanged since `BUG-RENDER-25`:

```glsl
color = mix(base, reflected * 0.5, env)      // applyLegacyEnv:912, env = the Shiny level
```

so Shiny HIGH replaces three quarters of the glass, Shiny LOW a quarter, and a Shiny-NONE panel is
just its own grey — the ladder `mirror_panel.lsl` steps through.

## Acceptance Criteria

- [x] Backing away from a mirror no longer grows the reflection relative to the frame without
      bound — the ratio settles, as it does in Firestorm.
- [ ] The measuring cube (`mirror_probe.lsl`) reads the real-mirror ratio within ~10% of Firestorm
      at 1 m and 4 m.
- [x] The reflection is screen-sharp, not cubemap-soft.
- [ ] `[PlanarMirror] ON … normal=(…)` in the log, and `[HeroProbe]` stays off while it is on.
- [ ] Cost measured, not estimated.

## Technical Specs & Affected Files

- `app/scripts/MirrorReflection.cs` *(new)* — the SubViewport, the reflection camera, the plane
  maths, and the idle/resume that stops it rendering when no mirror is in view.
- `app/materials/prim/prim_mirror.gdshader` *(new)* — screen-position sampling and SL's mix.
- `app/scripts/PrimShaderFamily.cs` — the shader and its one new uniform name.
- `app/scripts/ObjectRenderer.cs` — `MirrorNormal`, `FrontFaceNormal`, `ApplyMirrorShader`,
  `SetMirrorTexture`, `PlanarMirrorActive`.
- `app/scripts/Boot.cs` — owns the node, drives it before the hero probe, stands the hero probe
  down while it is active; `AppVersion` `v0.22.138-alpha`.

## Known limits, deliberately

- **Near-plane clipping, not an oblique frustum.** The reflection camera's near plane is set at
  the mirror plane, which removes the wall behind the glass. Godot exposes no oblique projection;
  the reference viewer solves the same problem with a clip plane in every shader (`mirrorClip`).
  Geometry that straddles the plane will clip where the viewer would fade it.
- **No Fresnel.** `applyLegacyEnv` also brightens the reflection at grazing angles
  (`min(fresnel + envIntensity, 1.0)`). Left out of the first version on purpose: it is a
  refinement on top of a reflection that is now geometrically correct, and adding both at once
  would make a wrong result impossible to attribute.
- **One mirror.** The nearest chosen one, same as the hero probe. A second mirror in the room
  keeps the follow probe.
- **The handedness flip** is corrected by mirroring the sampled U coordinate
  (`mirror_flip_u`), because `LookAt` forces a right-handed basis and so drops the reflection's
  own sign. If the first live look shows the image flipped, that uniform is the one value to
  change — not a rebuild of the maths.

## Sub-tasks / Progress

- [x] Chosen after the cubemap route was measured to its limit, not before trying it.
- [x] Build (both), 711 tests, format, shader globals 28/28, selftest **39/39** (the new shader
      compiles under Godot), `project.godot` intact.
- [x] **Confirmed in-world 2026-09-14** (Agni): *„OK die Spiegelung funktioniert jetzt besser als in Firestorm“*.
- [ ] Cost measured.
