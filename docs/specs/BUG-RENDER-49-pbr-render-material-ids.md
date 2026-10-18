# [BUG-RENDER-49] PBR faces render their legacy texture: the render-material ids are never read

- **Feature ID:** `BUG-RENDER-49`
- **Track:** `net` + `render`
- **Status:** `🧪 Review`
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

## Known limits

- The base material asset's own KHR texture transform is not parsed (`PbrMaterialData` has none);
  a face without an override transform is placed with the identity transform.
- A PBR base colour that is a Bakes-on-Mesh id is resolved when the face is built, but such a mesh
  is not registered for the bake-arrival refresh (the viewer itself carries a TODO there).

## Sub-tasks / Progress

- [x] Net: parse, latch, per-face resolution, tests
- [x] Render: avatar PBR path, world TE-colour/UV parity, merge facts, selftest
- [ ] In-world check at the crowded region: the pet and a PBR bikini; `[FaceParams]` shows `pbr=`
