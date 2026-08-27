# [FEAT-RENDER-04] Legacy Blinn-Phong Materials (normal + specular maps)

- **Feature ID:** `FEAT-RENDER-04`
- **Track:** `net` / `assets` / `render`
- **Status:** `🧪 Review` — phases 1-4 confirmed in-world; phase 5 (alpha modes) implemented,
  pending an in-world A/B
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

SL faces can carry a **legacy "Blinn-Phong" material** — the 2013 materials system —
which adds a **normal map** and a **specular map** on top of the diffuse texture, plus
glossiness, environment intensity and an alpha mode. SLNG reads none of it, so every
such face renders as its flat diffuse texture alone.

Found on OSGrid, The Dangazi Forest, 2026-08-23. A reef rock rendered as smooth brown
stone next to Firestorm's sharply layered strata. The diffuse texture was ruled out by
decoding it from our own cache: `79bdad31` is a directionless rock (gradient energy
45.7 across X vs 46.8 across Y — no dominant direction), and it is exactly what we
draw. Firestorm's Texture tab for that face showed the Blinn-Phong panel with a normal
map AND a specular map bound, Bumpiness and Shininess both set to "use texture",
glossiness 51. The horizontal ledges come from the normal map; the white highlights on
their upper edges from the specular map.

This is not a niche case. Legacy materials are on a large share of built content on
both SL and OpenSim — rock, brick, metal, tile — so the gap costs surface detail
everywhere, not just on this reef.

**Not to be confused with glTF PBR materials, which we already support.** A face
carries two independent material ids and LibreMetaverse exposes both:

| Field | System | SLNG today |
|---|---|---|
| `TextureEntryFace.RenderMaterialID` | glTF / PBR (documented in LMV as "PBR / GLTF render material asset UUID") | read, resolved via `AssetService.GetMaterialAsync` → `PbrMaterialData` |
| `TextureEntryFace.MaterialID` | legacy Blinn-Phong | **never read** |

Note `FaceTexture.MaterialId` already exists but carries the *RenderMaterialID*; the
legacy id needs its own field and the existing one is worth renaming for clarity.

## What the protocol actually looks like

Materials are not ordinary assets. They are fetched from the region's
**`RenderMaterials`** capability, whose request and response bodies are an LLSD map
with a single `Zipped` key holding zlib-compressed LLSD (`llmaterialmgr.cpp:47-59`):

- **GET** — every material registered in the region.
- **POST** — a specific set of ids, **max 50 per request** (`MATERIALS_GET_MAX_ENTRIES`).
- Response entries are maps of `ID` (16 raw bytes) and `Material`.

Material fields and their encoding (`llmaterial.cpp:35-61`); every float is
transmitted as an integer scaled by `MATERIALS_MULTIPLIER = 10000`:

`NormMap`, `NormOffsetX/Y`, `NormRepeatX/Y`, `NormRotation`,
`SpecMap`, `SpecOffsetX/Y`, `SpecRepeatX/Y`, `SpecRotation`,
`SpecColor`, `SpecExp`, `EnvIntensity`, `AlphaMaskCutoff`, `DiffuseAlphaMode`.

`DiffuseAlphaMode` is `None=0, Blend=1, Mask=2, Emissive=3, Default=4`
(`llmaterial.h`).

**LibreMetaverse already implements this whole path** and it does not need writing:
`LibreMetaverse.Materials.LegacyMaterial` carries every field with the `/10000`
decoding and the three ID encodings the sim may use, and `ObjectManager` exposes
`RequestMaterialsAsync(sim)` and `RequestMaterialsAsync(sim, ids)`. The work here is
therefore boundary conversion, caching and rendering — not protocol implementation.

## Acceptance Criteria

- [x] A face carrying a legacy material id resolves it and renders with its normal map.
- [x] The specular map drives a visible highlight, with glossiness and environment
      intensity mapped from SL's units.
- [x] The material's own normal/specular repeat, offset and rotation are applied —
      they are independent of the diffuse texture's placement.
- [x] No LibreMetaverse type crosses a public boundary of `SLNG.Net` / `SLNG.Assets`
      (AGENTS.md): `LegacyMaterial` is converted at the boundary.
- [x] Materials are batched and cached; a region full of them must not issue one
      request per face.
- [x] Unit tests for the boundary conversion, plus `SlSculptResolutionTests` and
      `MeshTangentsTests` for the two prerequisites this turned up.
- [x] Visually A/B'd against Firestorm on the Dangazi Forest reef rock
      (`88fe3b1a-b688-4da0-8730-de7cd1194a6c`, a known case with both maps bound).
      Confirmed 2026-08-23: shape, size and both maps match.
- [~] **Phase 5:** a face with a legacy material takes its transparency from the material's
      `DiffuseAlphaMode` (`Blend`/`Mask`+`AlphaMaskCutoff`/`None`), not from `DetectAlpha()`.
      `Default` still falls through to the pixel guess. Implemented; in-world A/B pending —
      needs a face known to declare `Mask` or `None` while carrying a diffuse texture whose
      alpha channel would make `DetectAlpha()` decide otherwise.

## Technical Specs & Affected Files

- `src/SLNG.Core/LegacyMaterialData.cs` — new, engine- and protocol-neutral DTO.
- `src/SLNG.Core/FaceTexture.cs` — add `LegacyMaterialId`; rename the existing
  `MaterialId` to `RenderMaterialId` to match what it holds.
- `src/SLNG.Net/GridSession.cs` — read `TextureEntryFace.MaterialID`; add
  `FetchLegacyMaterialsAsync(ids)` converting `LegacyMaterial` → `LegacyMaterialData`.
- `src/SLNG.Assets/AssetService.cs` — `GetLegacyMaterialAsync(id)`: batching (≤50 ids
  per request), single-flight, memory cache.
- `app/scripts/ObjectRenderer.cs` — bind the normal and specular textures and their
  own UV placement.
- `app/materials/prim/prim_common.gdshaderinc` — the shader already has
  `normal_texture` / `normal_scale`; specular needs a mapping decision (see below).

## Settled: how SL's glossiness becomes a roughness

This was flagged up front as the one part that could look plausible and be wrong, and the first
attempt was wrong exactly as predicted -- `roughness = 1 - glossiness` gave 0.8 for the reef
rock's glossiness of 51/255 and produced no visible highlight at all.

The relation is in the code that BUILDS the viewer's specular lookup texture, not in the shader
that samples it (`LLPipeline::createLUTBuffers`, pipeline.cpp:1447):

    n    = glossiness * glossiness * RenderSpecularExponent    // default 368
    spec = pow(N dot H, n)

Then the standard Blinn-Phong to GGX conversion, `alpha = sqrt(2/(n+2))`, and one more square
root because Godot's ROUGHNESS is perceptual (`alpha = roughness^2`). For the rock: 0.59.

Two details found in the same pass: the specular map scales the highlight's STRENGTH, not its
roughness (Godot's SPECULAR), and glossiness is modulated per texel by the NORMAL map's alpha
channel (materialF.glsl:227).

## Original open question, kept for the record

Godot is metallic-roughness; SL Blinn-Phong is specular-glossiness. The conversion is
NOT obvious and is the one part of this that can look plausible and be wrong. The
viewer's own shading is the reference (`lldrawpoolmaterials.cpp`, the `materialF.glsl`
family), and `SpecExp`/`EnvIntensity` have defined roles there —
`DEFAULT_SPECULAR_LIGHT_EXPONENT` is `0.2 * 255` and `DEFAULT_ENV_INTENSITY` is 0
(`llmaterial.h`). Port from those rather than fitting a curve by eye.

## Sub-tasks / Progress

- [x] **Phase 1 — wire path.** Read `MaterialID` per face, carry it through
      `FaceTexture` / `PrimitiveComponent` / `ObjectUpdateEvent`. No visual change.
- [x] **Phase 2 — capability fetch.** Neutral `LegacyMaterialData`, boundary
      conversion in `GridSession`, batched + cached resolve in `AssetService`. No
      visual change; a log line proves materials resolve.
- [x] **Phase 3 — normal maps.** Bind the normal map with its own placement.
- [x] **Phase 4 — specular.** Map `SpecMap`/`SpecColor`/`SpecExp`/`EnvIntensity` onto
      the shader family, ported from the viewer's material shaders.
- [x] **Phase 5 — alpha modes.** `BuildFaceMaterialAsync` now reads the legacy material's
      `DiffuseAlphaMode` and picks the shader family directly — `Blend`→Blend,
      `Mask`→Scissor with `AlphaMaskCutoff/255` as the threshold, `None`→Opaque (without
      downgrading a translucent per-face tint), `Emissive`→Blend (fullbright proper is
      FEAT-RENDER-06). A `legacyAlphaModeResolved` flag then skips `ApplyAlphaCutout` for that
      face so the `DetectAlpha()` guess can't override it. `Default` keeps the old behaviour.
      Mirrors the glTF branch's `pbr.AlphaMode` handling. Render-only; build + 322 tests +
      selftest green. `v0.9.52-alpha`.

## Found along the way (not in the original plan)

- **Mesh tangents did not exist.** `SurfaceTool.GenerateTangents()` had been disabled with a
  comment about a Vulkan NaN crash on degenerate triangles, so Godot had no tangent basis and
  could not apply ANY normal map -- including the ones the glTF PBR path already bound.
  `SLNG.Assets.MeshTangents` computes them with finite output as its defining property.
- **Sculpts were sampled on the wrong grid.** PrimMesher halves both map dimensions together;
  the viewer spends its vertex budget in the map's actual proportion. On the reef rock's 512x16
  map that was 645 vertices against the viewer's 1230 -- half the resolution along the axis the
  strata run along, which is why a reference cube rested on the surface here and sank into it in
  Firestorm. `SlSculptResolution` ports `sculpt_calc_mesh_resolution`; the extents now match the
  viewer to the last decimal. Square maps were never affected, and the existing parity test could
  not see it because it compares the surface with a 5% Hausdorff tolerance.
- **Two dead, misleading sculpt samplers** in `SculptMesh` (one box-filtering the map, one
  dividing by 256 instead of 255) were deleted; both read like the live path and neither was.

## Phase 4's approximation, stated plainly

Godot's metallic-roughness model has no per-texel specular COLOUR, so the viewer's
`spec = specularMap.rgb * specular_color.rgb` cannot be ported exactly. What is implemented:
the map's Rec. 709 luminance drives how polished the surface is, `SpecExp/255` caps how polished
the brightest texel can be, and `EnvIntensity/255` folds into metalness scaled by the same
luminance. A greyscale specular map -- what almost all SL content ships -- is reproduced
faithfully; a coloured one loses its tint. This is the part of the feature most likely to need
adjusting after an A/B, and it is deliberately conservative rather than tuned by eye.
