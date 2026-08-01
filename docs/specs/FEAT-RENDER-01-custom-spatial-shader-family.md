# [FEAT-RENDER-01] Custom Spatial Shader Family for World Surfaces

- **Feature ID:** `FEAT-RENDER-01`
- **Track:** `render`
- **Status:** `⏸️ Pending`
- **Owner:** _(unclaimed)_
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **ADR:** [0002-custom-spatial-shader-family.md](file:///E:/Git/SLNG/docs/adr/0002-custom-spatial-shader-family.md)

## Overview & Goal

Replace `StandardMaterial3D` with one custom Godot **spatial** shader family shared by
`ObjectRenderer`, `AvatarRenderer`, terrain and water. Two things force it (full argument
in ADR 0002):

- **SL face rotation cannot be expressed today.** `StandardMaterial3D` has `Uv1Scale` and
  `Uv1Offset` only, so `ObjectRenderer.BuildFaceMaterialAsync` drops any rotation that is
  not 0 or ±π. 49 faces at exactly π/2 were measured in a single view of OSGrid's Dangazi
  Forest, visibly mis-placing textures against Firestorm.
- **Windlight / EEP needs a per-fragment atmospherics hook on every surface.** There is
  no such seam in `StandardMaterial3D`, and a hybrid (custom shader only where needed)
  would leave neighbouring surfaces visibly diverging at sunset.

The UV transform (scale, offset, rotation) goes in the **vertex** shader. The shader
carries an atmospherics `#include` seam that starts as a no-op. Windlight parameters go
in Godot **global shader uniforms** (`RenderingServer.GlobalShaderParameterSet`), set
once per frame, never per-material.

**This is not a PBR reimplementation.** The shader assigns `ALBEDO`, `ALPHA`,
`NORMAL_MAP`, `METALLIC`, `ROUGHNESS`, `EMISSION`; Godot's lighting, shadows, GI and
post-processing continue to apply. Do not write lighting code.

Scope note: this is the highest-blast-radius change in the project so far — it touches
every surface in view, and live-test rounds are expensive. The phasing below exists
specifically so each step is independently verifiable. **Do not collapse phases.**

## Functional Requirements

- **One shader family, not one shader.** `render_mode` is compile-time in Godot, so the
  family needs a small set of variants: opaque + back-culled, alpha-blend, alpha-scissor.
  Keep the count minimal; `StandardMaterial3D` does the same internally. Any pressure to
  add a fourth/fifth variant is a signal to re-read ADR 0002's revisit condition.
- **Vertex-shader UV transform.** SL's centred convention must be preserved exactly:
  `u' = (u - 0.5) * repeat + 0.5 + off`, with rotation applied about the face centre
  (0.5, 0.5). ±π must remain equivalent to the current negated-repeat handling.
- **Atmospherics seam.** A single include point in the fragment path, shipping as a
  no-op that does not alter output. Phase 1 must be byte-for-byte indistinguishable with
  the seam present.
- **Global uniforms for anything frame-varying.** Windlight (blue-horizon, blue-density,
  haze-horizon, haze-density, distance-multiplier and friends) is set once per frame via
  `RenderingServer.GlobalShaderParameterSet`. Per-material properties for these would
  mean CPU-side churn across thousands of objects every frame — explicitly rejected.
- **Feature parity checklist.** All of the following are in use today and must survive:
  `AlbedoColor` + `AlbedoTexture`; per-face `Uv1Scale`/`Uv1Offset`; `TextureFilter`
  including `LinearWithMipmapsAnisotropic`; `CullMode.Back`; the three transparency modes
  (`Alpha`, `AlphaScissor` with `AlphaScissorThreshold` + `AlphaAntialiasingMode`,
  `AlphaHash` with `AlphaHashScale`); `Metallic`/`Roughness`/`EmissionEnabled`/`Emission`
  and the PBR texture set (base colour, normal, metallic-roughness, emission);
  `AvatarRenderer`'s `AlphaHash` path.
- **Do not disturb mesh sharing.** `ObjectRenderer._primShapeKeys` shares meshes between
  identical prim shapes. Rotation must not be baked into UVs and must not become part of
  the shape key — that was rejected in ADR 0002 on both VRAM (256 MB `GpuCache` budget)
  and correctness grounds.
- **Threading unchanged.** Material construction stays on the existing async path;
  resource assignment and scene mutation stay on the main thread via the current
  `Callable.From` / deferred pattern. This task changes *what* material is built, not
  *where*.

## Pre-migration performance baseline

Captured 2026-08-01 on `v0.3.71-alpha`, i.e. the last build before any shader work, via
**Developer → Measure Render Baseline (10s)** (`app/scripts/RenderBaselineSampler.cs`).
The after-measurement must use the same menu action, the same build machine, the same
place and the same camera transform — a comparison is meaningless otherwise.

```
frames=835 medianMs=11.46 p95Ms=14.81 worstMs=91.45
drawCalls=5867 primitives=32262752 objects=6698 videoMemMB=2573.6
```

Scene: OSGrid, The Dangazi Forest — the foreground-pillar view also used as the Phase 2
rotation reference. (Record the exact position readout alongside this before re-measuring.)

Notes on reading these:
- The sampler disables V-Sync for its window on purpose. An earlier baseline taken with
  V-Sync on reported `medianMs=31.25` — exactly two refresh periods on this ~64 Hz display.
  Frame times snap to whole refresh intervals in that mode, so a change of ±40% can report
  an identical figure. Any measurement showing a suspiciously round median is invalid.
- Judge Phase 1 on **median and p95**, not on `worstMs`: the 91 ms outlier here is a
  one-off hitch (asset upload or GC), and a single sample of it says nothing about the
  shader swap either way.
- `drawCalls` was byte-identical across two independent runs, so it is the most sensitive
  regression signal available here: material-batch fragmentation would show up there long
  before it moved a noisy timing figure.
- `videoMemMB=2573` against the `GpuCache`'s nominal 256 MB budget is a pre-existing
  finding, unrelated to this task and not caused by it. Recorded so a later reading of the
  same number is not mistaken for a regression introduced by the shader family.

## Acceptance Criteria

### Phase 1 — Swap `ObjectRenderer` to the shader family, visually identical

- [ ] `ObjectRenderer` builds `ShaderMaterial`s from the new family instead of
      `StandardMaterial3D`; no `StandardMaterial3D` remains in its face path.
- [ ] **The scene looks identical to today.** Any visible difference is by definition a
      regression. Verified by side-by-side comparison at the same camera transform on the
      OpenSim test grid and on OSGrid's Dangazi Forest (the measured rotation site).
- [ ] Every item on the parity checklist above is demonstrably still applied — including
      anisotropic filtering (the sculpt-pole grain case) and back-face culling (the
      glassy-shell case). Both were live-verified fixes; do not regress them.
- [ ] Alpha behaviour unchanged: translucent tints, glTF `BLEND`/`MASK` alpha modes, and
      the `ApplyAlphaCutout` scissor path all render as before.
- [ ] The atmospherics `#include` seam exists and is a no-op.
- [ ] Frame time on a busy scene is no worse than the `StandardMaterial3D` baseline
      (measure before the swap, compare after — same view, same camera).
- [ ] `dotnet build` + `dotnet test` clean, `dotnet format` clean, `AppVersion` bumped.

### Phase 2 — UV rotation (first real gain)

- [ ] The vertex shader applies arbitrary face rotation about the face centre.
- [ ] The Dangazi Forest pillar's plaster patch renders **top-left**, matching Firestorm.
- [ ] The `[FaceTex] unsupported face rotation` debug log in
      `ObjectRenderer.BuildFaceMaterialAsync` is removed — no rotation is unsupported.
- [ ] 0 and ±π render exactly as they did in Phase 1 (no regression on the cases that
      already worked).
- [ ] `_primShapeKeys` mesh sharing is unchanged: two prims of identical shape with
      different face rotations still share one mesh.

### Phase 3 — `AvatarRenderer` onto the same family

- [ ] `AvatarRenderer` face materials use the family; its `AlphaHash` path is preserved
      exactly (unconditional `AlphaHash` for the bake/attachment path — *not* the
      `ApplyAlphaCutout` scissor choice; the two are deliberately different).
- [ ] Own avatar and other avatars render identically to Phase 2, including worn mesh
      attachments and BoM-baked faces.
- [ ] Face rotation now works on avatar attachment faces too.

### Phase 4 — Terrain and water onto the same family

- [ ] `terrain.gdshader` and `water.gdshader` are refactored onto the shared family's
      structure and share the same atmospherics seam and global uniforms.
- [ ] Terrain detail-texture blending, height ranges and the water plane render
      identically to Phase 3.

### Phase 5 — Fill the atmospherics seam (Windlight / EEP)

- [ ] Windlight parameters are published once per frame as global shader uniforms; no
      per-material updates for them.
- [ ] All four renderers receive the same atmospherics — no visible seam between a prim,
      an avatar, terrain and water at sunset (the failure mode a hybrid would have had).
- [ ] Godot's built-in fog approximation is retired or explicitly reconciled with the new
      model, not left double-applying.
- [ ] Compared against Firestorm at matching sun positions on the same region.

## Technical Specs & Affected Files

- `app/materials/` (new `.gdshader` files) — the shader family and its shared includes,
  alongside the existing `terrain.gdshader` / `water.gdshader`.
- `app/scripts/ObjectRenderer.cs` — `BuildFaceMaterialAsync` and its callers; the
  `StandardMaterial3D` return type, `ApplyAlphaCutout`, and the PBR texture assignment
  continuations. `_primShapeKeys` must not change shape.
- `app/scripts/AvatarRenderer.cs` — its own `BuildFaceMaterialAsync`, the `AlphaHash`
  path, and the debug/placeholder `StandardMaterial3D`s.
- `app/scripts/TerrainRenderer.cs` — `_terrainMaterial` / `_waterMaterial` and the
  `SetShaderParameter` calls.
- `app/scripts/Boot.cs` — `AppVersion` bump per phase; likely home for the once-per-frame
  global-uniform publish in Phase 5.
- `src/` — **untouched.** Shaders are an `app/` concern; the layering rule stands.

## Sub-tasks / Progress

- [ ] Phase 1 — `ObjectRenderer` on the family, visually identical, atmospherics seam
      stubbed.
- [ ] Phase 2 — arbitrary UV rotation in the vertex shader.
- [ ] Phase 3 — `AvatarRenderer` migration.
- [ ] Phase 4 — terrain + water refactor onto the family.
- [ ] Phase 5 — Windlight / EEP atmospherics via global shader uniforms.
- [ ] Follow-up (not this spec): `llSetTextureAnim` and media-on-a-prim as uniform
      updates instead of material rebuilds — enabled by, but not part of, this work.
