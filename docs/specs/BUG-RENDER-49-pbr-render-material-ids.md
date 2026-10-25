# [BUG-RENDER-49] PBR faces render their legacy texture: the render-material ids are never read

- **Feature ID:** `BUG-RENDER-49`
- **Track:** `net` + `render`
- **Status:** `✅ Done`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Second Life and OpenSim send a face's glTF PBR material id in the object's ExtraParams, block
**0x80** (`PARAMS_RENDER_MATERIAL`), not in the TextureEntry. SLNG never read that block, so every
PBR face rendered its legacy diffuse texture instead -- often `IMG_WHITE` or a semi-transparent
placeholder. In-world: a worn animesh pet rendered plain white and a PBR bikini see-through, both
correct in Firestorm. No `[PbrMaterial]` line in any log, although 164 glTF override messages
(FEAT-RENDER-26) arrived for faces that had no base material to apply them to.

### Wire layout (verified)

- `llprimitive.h:110` `PARAMS_RENDER_MATERIAL = 0x80`.
- `LLRenderMaterialParams::pack/unpack` (llprimitive.cpp:2336-2362): `U8 count`, then `count` x
  (`U8 te_idx`, 16-byte UUID). At most 14 entries (the block is limited to 255 bytes).
- `getMaterial(te)` (llprimitive.cpp:2418-2429) is an exact match on `te_idx`, first entry wins, null
  for an unlisted face. There is no "all faces" index: 255 is not special.
- The viewer applies it per TE in `setRenderMaterialIDs` (llviewerobject.cpp:7819-7831) and CLEARS
  every face's id when a full update omits the block (`parameterChanged(..., in_use=false)`,
  llviewerobject.cpp:1590-1598, :6846-6850).
- OpenSim: `PrimitiveBaseShape.MaterialsEP = 0x80`, payload `1 + 17*N`.
- LibreMetaverse 3.1.6: `SetExtraParamsFromBytes` steps over 0x80; `TextureEntryFace.RenderMaterialID`
  is only filled from optional TextureEntry bytes that Agni does not send.

Evidence: the Sirens object cache holds 136 objects with a 0x80 block; local 823816049 maps faces
0-4 to `e69dde89-50f8-b85f-9a26-da60314acba6` (its ExtraParams are the test fixture).

### Viewer rules for a PBR face (what the renderers must follow)

- The face's colour is the render material's base colour; the TextureEntry colour is not used
  (llface.cpp:1395-1400), and neither is its alpha for hiding the face (llvovolume.cpp:5972-5979). A
  BLEND material whose base-colour alpha is 0 is not drawn (llvovolume.cpp:6017-6022).
- The TextureEntry repeats/offset/rotation are not used; glTF textures are placed by the material's
  KHR texture transform (llface.cpp:1474 "For GLTF, transforms will be applied later").
- A Bakes-on-Mesh magic id in a material texture slot resolves to the wearer's bake
  (llviewerobject.cpp:5452-5461).

## Acceptance Criteria

- [x] `ExtraParamsScan` parses 0x80 into per-face (te index -> UUID) entries; a truncated payload
      keeps the whole entries it holds and never throws.
- [x] A fourth latch in `GridSession` (region, LocalId) holds the entries, set and cleared on every
      full, compressed and cache-replayed update, pruned on kill and region disconnect, and the
      object update is raised again when it changes.
- [x] Each face's `RenderMaterialId` is the latch entry for its index, falling back to the
      TextureEntry value.
- [x] World prims build a PBR material for such a face (the existing ObjectRenderer path; shared
      material keys already include the id and the override).
- [x] Worn rigged meshes, worn animesh and static attachments sample the material's base-colour
      texture (BoM magic ids resolved), take its factors, alpha mode/cutoff, normal, ORM and
      emissive maps; the outfit merge's material facts carry the effective texture.
- [x] xUnit: 0x80 parse from the real cached bytes, empty, truncated, clear-on-absent, compressed.
- [x] Selftest: a face with a render material id builds a PBR material on a world prim and on a
      worn rigged mesh.

## Technical Specs & Affected Files

- `src/SLNG.Net/RenderMaterialParams.cs` (new), `src/SLNG.Net/ExtraParamsScan.cs`,
  `src/SLNG.Net/GridSession.cs`, `src/SLNG.Net/GridSession.Objects.cs`
- `src/SLNG.Core/FaceTexture.cs` (`IsInvisible` is legacy-only)
- `app/scripts/ObjectRenderer.cs`, `app/scripts/AvatarRenderer.cs`,
  `app/scripts/AvatarRenderer.OutfitMerge.cs`
- `src/SLNG.Net/GridSession.Assets.cs` (under `--diag`: a texture refusal's body and Server/X-Cache
  headers, once per id; the reference viewer has no UDP texture fallback, lltexturefetch.cpp:431-444)
- Tests: `tests/SLNG.Net.Tests/RenderMaterialExtraParamsTests.cs` (20, incl. the real Agni bytes and
  an end-to-end compressed update), `tests/SLNG.Assets.Tests/RiggedMeshBuilderTests.cs`; selftests
  `app/scripts/SelfTest.PbrRenderMaterial.cs` with `ObjectRenderer.PbrRenderMaterial.SelfTest.cs` and
  `AvatarRenderer.PbrRenderMaterial.SelfTest.cs`

## Follow-up after the v0.27.83 in-world test (v0.27.84 .. v0.27.86)

The 0x80 ids arrived, but every material fetch failed (109 failures over 40 ids, zero
`[PbrMaterial]` lines), and hair showed layers a HUD had switched off.

### The material asset (asset type 57, "material")

- **Fetch:** `{ViewerAsset cap}/?material_id={id}` (`LLViewerAssetStorage::getAssetURL`,
  llviewerassetstorage.cpp:640-645; type name from llassettype.cpp:100). 403/404/410 is a refusal
  (`AssetRefusals`, no legacy retry); no capability / timeout / 5xx tries the legacy transfer once
  for at most 20 s. `GridSession.FetchMaterialAssetAsync` returns bytes; no LibreMetaverse type
  crosses the boundary any more.
- **Envelope:** LLSD, written BINARY by the viewer (llmaterialeditor.cpp:1305-1315):
  `<? LLSD/Binary ?>\n` then a map `{ version: "1.1", type: "GLTF 2.0", data: "<glTF JSON>" }`.
  Accepted versions "1.0"/"1.1" (llgltfmaterial.cpp:38-40). The viewer also accepts XML and notation
  (llsdserialize.cpp:150-240); so does `SLNG.Assets.GltfMaterialAsset`. Binary LLSD (all big-endian):
  `{` U32 n, n x (`k` U32 len key, value), `}`; `[` U32 n ... `]`; `s`/`l` U32 len UTF-8; `i` S32;
  `r` F64; `u` 16 bytes; `1`/`0`/`!` -- `SLNG.Core.LlsdBinary`.
- **glTF:** material 0, as `LLGLTFMaterial::setFromModel` (llgltfmaterial.cpp:220-265): a slot's
  texture id is `images[textures[index].source].uri`; optional `KHR_texture_transform`
  (offset, scale, rotation); glTF defaults when absent; metallic, roughness, cutoff clamped 0..1.
- **Why it failed:** LibreMetaverse 3.1.6 `AssetMaterial.Decode` parses the bytes as bare glTF JSON.

### Visibility without the material

A face with a render material id whose material is NOT loaded is exactly its legacy self: geometry
(TE alpha 0 / transparent texture drops a rigged submesh) and material. Only a LOADED material
decides by itself (BLEND at base alpha 0, or MASK below its cutoff, draws nothing). Worn meshes fetch
their faces' materials alongside the mesh and wait up to 5 s before rigging.

### A material that arrives later

`AssetService.MaterialLoaded` -> a renderer that built a face WITHOUT that material rebuilds:
world prims retire the stale shared materials (unique retired key, holders re-keyed) and re-apply;
avatars rebuild every worn item and animesh part naming it (geometry, materials and outfit-merge
facts/keys together).

## Follow-ups after the v0.27.86 test (v0.27.89-91)

- **Colour space (v0.27.89).** glTF base-colour and emissive factors are linear; `albedo_color` and
  `emission_color` are `source_color` uniforms that Godot decodes from sRGB, so `ApplyPbrUniforms` wrote a
  linear factor that was linearised a second time -- every PBR tint darker and more saturated. The viewer
  multiplies the linearised texel by the linear base colour (pbropaqueF.glsl:73-75, llface.cpp:1392-1400).
  Factors are now handed over sRGB-encoded (`ObjectRenderer.PbrLinearFactor`), alpha untouched.
- **Dielectric specular (v0.27.90).** prim_common zeroes specular for every face (SL's Shiny gate); a glTF
  dielectric reflects 4 % in the viewer (deferredUtil.glsl:565-571). A `pbr_material` flag gives PBR faces
  Godot's 0.5 (= F0 0.04); legacy faces unchanged.
- **`[PbrFace]` diagnostic (v0.27.91).** For a wearer's PBR hair that renders hard-edged and untinted (soft and
  rose in Firestorm): one line per (material, override, kind) with the base asset's mode/cutoff/colour, the
  face's override fields, the effective mode and the shader kind; avatar lines carry the item's LocalId to
  match `[GltfOverride] local=`. No loss path was proven: overrides are stamped on every object update and a
  late one re-rigs the item. Noted divergence, not acted on: `WorldSimulation.OnEntityRemoved` drops an
  object's overrides on every kill, while the viewer drops them only with a valid VO-cache entry
  (llviewerregion.cpp:1115-1165), i.e. keeps them for attachments across a kill.

## Known limits

- A PBR base colour that is a Bakes-on-Mesh id is resolved when the face is built, but such a mesh
  is not registered for the bake-arrival refresh (the viewer itself carries a TODO there).
- No real material asset was available offline (none in either asset cache); the decoder is tested
  against envelopes built to the viewer's layout.

## Sub-tasks / Progress

- [x] Net: parse, latch, per-face resolution, tests
- [x] Render: avatar PBR path, world TE-colour/UV parity, merge facts, selftest
- [x] Material fetch over ViewerAsset + LLSD envelope decode (v0.27.84)
- [x] Legacy fallback when the material is unavailable, hair regression (v0.27.85)
- [x] Late material re-applies world prims and rebuilds worn items (v0.27.86)
- [x] PBR factors linear into the shader, dielectric specular, `[PbrFace]` diagnostic (v0.27.89-91)
- [ ] In-world check at the crowded region: the pet and a PBR bikini; `[FaceParams]` shows `pbr=`
- [ ] In-world: the PBR hair's `[PbrFace]` lines -- override present and BLEND, or not
