# ADR 0002 — Custom spatial shader family for world surfaces

- Status: accepted
- Date: 2026-08-01

## Context

Every world surface SLNG draws today — prims, avatars — is a `StandardMaterial3D`.
Terrain and water already run hand-written `.gdshader` files, but they are isolated
one-offs that share nothing with the rest of the scene. Two findings this session made
that split untenable.

**1. UV rotation is unrepresentable.** `StandardMaterial3D` exposes only `Uv1Scale` and
`Uv1Offset` — no rotation, and no swizzle that could stand in for a 90° turn.
`ObjectRenderer.BuildFaceMaterialAsync` consequently handles 0 and ±π (a point-mirror,
expressible as negated repeats) and *drops* everything else, rendering the face
unrotated. This is not an edge case: 49 faces at exactly π/2 were measured in a single
view of OSGrid's Dangazi Forest, where it visibly mis-places textures — a pillar's light
plaster patch rendered top-right where Firestorm puts it top-left.

**2. Windlight / EEP is the real forcing function.** SL applies atmospheric scattering
*per fragment on every surface*; the real viewer pulls `atmospherics.glsl` into its prim
shaders, with blue-horizon, blue-density, haze-horizon, haze-density and
distance-multiplier feeding the albedo computation. There is no seam in
`StandardMaterial3D` to inject that. Godot's built-in fog is a different model — fine as
an early approximation, not parity with Firestorm.

Point 2 is what kills the obvious compromise. A hybrid — `StandardMaterial3D` for most
faces, a custom shader only for rotated ones — is a dead end, because the
`StandardMaterial3D` half could never receive atmospherics and neighbouring surfaces
would visibly diverge at sunset. For the same reason the scope is all four renderers:
`ObjectRenderer`, `AvatarRenderer`, terrain and water. Anything less produces the same
break at a different seam.

## Options considered

1. **Hybrid** — keep `StandardMaterial3D`, add a custom shader only for rotated faces.
   Smallest change, but structurally incapable of Windlight; rejected per the argument
   above.
2. **Bake rotation into mesh UVs** at build time. Breaks the `_primShapeKeys` mesh
   sharing SL content depends on (identical prim shapes stop sharing a mesh the moment
   their face rotations differ) and spends VRAM against an already-bounded 256 MB
   `GpuCache` budget. Solves nothing for Windlight.
3. **Pre-rotate the texture at upload** (`Image.Rotate90`). Zero runtime cost, but
   duplicates textures against that same budget, only covers 90° multiples, and again
   does nothing for Windlight.
4. **One custom spatial-shader family** shared by all four renderers.

## Decision

Adopt option 4: a single custom Godot **spatial** shader family, used by prims,
avatars, terrain and water.

- The full UV transform — scale, offset **and rotation** — is applied in the **vertex**
  shader. Per-vertex costs less than per-fragment, and the gap widens with resolution.
- The shader carries an **atmospherics `#include` seam** that ships as a no-op. Filling
  it is what later delivers Windlight/EEP, and it is the reason this is one family
  rather than several.
- Windlight parameters live in Godot **global shader uniforms**
  (`RenderingServer.GlobalShaderParameterSet`), set once per frame — not per-material
  properties, which would mean CPU-side churn across thousands of objects every frame.
  The same mechanism later makes `llSetTextureAnim` and media-on-a-prim cheap: a uniform
  update instead of a material rebuild.

**A custom spatial shader is not a PBR reimplementation.** We assign `ALBEDO`, `ALPHA`,
`NORMAL_MAP`, `METALLIC`, `ROUGHNESS`, `EMISSION`; Godot's clustered lighting, shadows,
GI and post-processing still apply unchanged. Stating this explicitly because the
opposite assumption — "custom shader means writing our own lighting" — is the usual
objection, and it is what makes this decision affordable.

This stays inside `app/`. `src/` is untouched: shaders are an engine detail behind the
world model, exactly as ADR 0001 intends.

## Consequences

- **Positive:** unblocks correct SL face rotation; opens the only viable path to
  Windlight/EEP parity; one place to add per-surface behaviour instead of four;
  uniform-driven animation for `llSetTextureAnim` / MOAP later.
- **Negative / risks:** this has the **highest blast radius of any change so far** — it
  touches every surface in view. Live-testing rounds in this project are expensive (this
  session needed many), so the migration must be staged such that each step is
  independently verifiable; that staging is `FEAT-RENDER-01`'s job, and Phase 1 is
  deliberately a visually-identical swap so any difference is by definition a regression.
- Godot's `render_mode` is compile-time, so a small family of variants is needed
  (opaque + back-culled, alpha-blend, alpha-scissor). `StandardMaterial3D` does exactly
  the same internally — this is not new cost, only newly visible.
- A feature-parity checklist must survive the migration, all of it in use today:
  `AlbedoColor` + `AlbedoTexture`; per-face `Uv1Scale`/`Uv1Offset` with SL's centred
  convention `u' = (u-0.5)*repeat + 0.5 + off`; `TextureFilter` including anisotropic;
  `CullMode`; the three transparency modes with `AlphaScissorThreshold` and
  `AlphaAntialiasingMode`; `Metallic`/`Roughness`/`Emission` and the PBR texture set; and
  `AvatarRenderer`'s `AlphaHash` path.
- **Revisit if:** Godot gains a first-class UV-rotation and per-surface atmospherics hook
  on `StandardMaterial3D` (it would not retire the family, but would shrink it), or if
  the shader family's variant count grows past a handful — at which point the split is
  wrong and wants rethinking rather than another variant.
