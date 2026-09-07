# [BUG-RENDER-06] Double-sided GLTF materials were always back-face culled — flickering foliage on rotate/zoom

- **Feature ID:** `BUG-RENDER-06`
- **Track:** `render`
- **Status:** `✅ Done` — confirmed in-world 2026-09-07: foliage no longer pops in/out with view angle while orbiting/zooming a double-sided mesh-tree.
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **Branch:** `feature/FEAT-SL-01-second-life-readiness`
- **Version:** `v0.20.7-alpha`

## Overview & Goal

User, after `BUG-RENDER-05` fixed the water/canopy cut: *"pass jetzt mit dem wasser"* — but flagged
a second, pre-existing issue: *"die texturen flackern (haben sie auch vorher schon)... vor allem
wenn texturen noch laden oder wenn man sich bewegt/zoomt"* (textures flicker, especially while
still loading or while moving/zooming the camera). Two screenshots of the same tree were meant to
show the leaves differing, though the camera position also differed between them, which limited
a direct pixel diff. A follow-up question (`AskUserQuestion`) narrowed the dominant trigger to
camera movement specifically, not a stationary idle flicker.

## Root cause

Confirmed end-to-end, not guessed: SLNG never implements glTF's `doubleSided` material flag,
despite already having every other piece of it in place.

- `ObjectRenderer.cs` already carried a comment describing the real viewer's exact exception —
  *"the viewer... lifts [culling] ONLY for particles... and for a GLTF material that declares
  mDoubleSided... never blanket for prim faces, and notably not for alpha-blended ones either"* —
  but nothing in the codebase implemented the exception itself.
- LibreMetaverse already parses it: `AssetMaterial.DoubleSided` is a real, populated `bool` on the
  decoded GLTF material.
- `SLNG.Assets.PbrMaterialData` — the neutral DTO that crosses the `SLNG.Assets` boundary and is
  the ONLY thing `ObjectRenderer`/`AvatarRenderer` ever see of a material — never carried the field
  forward. It was decoded upstream and then silently dropped before reaching rendering code.
- Every `PrimShaderFamily` WorldPrim variant (`prim_opaque`/`prim_scissor`/`prim_blend.gdshader`)
  hardcodes `cull_back` in `render_mode`, which is compile-time in Godot — there was no variant to
  route a double-sided face to even if the flag had reached the renderer.

For a mesh tree whose leaf-card material is authored double-sided (near-universal practice for
foliage — a one-sided leaf card shows gaps from the back), every leaf triangle was back-face culled
exactly like an ordinary one-sided opaque face. As the camera orbits or zooms, which side of each
individual leaf triangle currently faces the camera keeps changing — so leaves keep popping in and
out of visibility purely as a function of view angle, reading as the canopy "flickering." Confirmed
by the user's own Firestorm Build-tool screenshot of the tree's leaf material: `Alpha-Modus:
Alpha-Blending` — a real glTF-materialed alpha-blend face, exactly the case the existing
`ObjectRenderer.cs` comment already flagged as needing the double-sided exception and never got it.

(The "while textures are still loading" trigger the user also mentioned is a separate, expected,
one-time transition — a placeholder material swapping to the real texture once — not addressed by
this spec, which is scoped to the confirmed camera-movement mechanism.)

## Acceptance Criteria

- [x] `PbrMaterialData` carries `DoubleSided` (mirrored 1:1 from `AssetMaterial.DoubleSided`) across
      the `SLNG.Assets` boundary.
- [x] Three new WorldPrim shader variants (`prim_opaque_doublesided`, `prim_scissor_doublesided`,
      `prim_blend_doublesided.gdshader`) — each identical to its base variant except
      `cull_back` → `cull_disabled` — so the existing, deliberately-defaulted `cull_back` behaviour
      for every ordinary face is completely untouched.
- [x] `PrimShaderFamily.Select` gains a `doubleSided` parameter, routing ONLY `Surface.WorldPrim`
      to its cull_disabled twin — a no-op for `Surface.Avatar`/`Surface.Hud`, which are already
      cull_disabled unconditionally.
- [x] `ObjectRenderer.cs`'s PBR-material branch swaps to the double-sided variant of whichever
      `Kind` (Opaque/Scissor/Blend) the existing `alphaMode` logic already selected, when
      `pbr.DoubleSided` is true — a face's `DoubleSided` flag is read ONLY from its own material,
      never assumed as a default.
- [x] `dotnet build SLNG.sln` / `app/SLNG.App.csproj`: 0 warnings. `dotnet test`: 563/563.
      `dotnet format` clean, shader-globals clean, `--selftest` 29/29 (26 + 3 new shaders, all
      compiling with uniform counts matching their base variants: 34/34/35).

## Technical Specs & Affected Files

| File | Change |
|---|---|
| `app/materials/prim/prim_opaque_doublesided.gdshader` | New — `prim_opaque.gdshader` with `cull_disabled`. |
| `app/materials/prim/prim_scissor_doublesided.gdshader` | New — `prim_scissor.gdshader` with `cull_disabled` (keeps `alpha_to_coverage`). |
| `app/materials/prim/prim_blend_doublesided.gdshader` | New — `prim_blend.gdshader` with `cull_disabled`. This is the variant the report was actually about. |
| `app/scripts/PrimShaderFamily.cs` | Three new lazy-loaded shader fields; `Select` gains `bool doubleSided = false`; `Preload` loads the three new variants too. |
| `app/scripts/ObjectRenderer.cs` | New `DoubleSidedTwin(Shader)` helper (reference-equality lookup against the WorldPrim base shaders); called right after the PBR `alphaMode` branch when `pbr.DoubleSided` is true. |
| `src/SLNG.Assets/PbrMaterialData.cs` | New `bool DoubleSided = false` field. |
| `src/SLNG.Assets/AssetService.cs` | `FetchMaterialAsync` passes `asset.DoubleSided` into the constructed `PbrMaterialData`. |

### Design notes

- **New variants, not a modified default.** `prim_opaque.gdshader`'s own comment explicitly warns
  against ever making `cull_back` conditional there — "everything rendering double-sided is
  precisely the bug that made solid objects look like glassy shells." Adding three NEW,
  narrowly-routed variants respects that warning completely: the default `cull_back` path for the
  overwhelming majority of ordinary faces is untouched byte-for-byte.
- **Only WorldPrim needed new variants.** `Surface.Avatar` and `Surface.Hud` are already
  `cull_disabled` unconditionally (their own doc comments in `PrimShaderFamily.cs` say so), so worn
  mesh attachments never had this bug and don't need `AvatarRenderer.cs` touched — `Select`'s
  `doubleSided` parameter is simply a no-op for those two surfaces by construction.
- **Legacy Blinn-Phong materials have no equivalent flag.** `DoubleSided` only ever gets populated
  from a real glTF/PBR material (`ft.RenderMaterialId`), so the fix is scoped to that one branch —
  the legacy-material branch above it is untouched and correctly never sees a `DoubleSided` value.
- **`DoubleSidedTwin` uses reference equality on purpose.** `PrimShaderFamily.Opaque`/`.Scissor`/
  `.Blend` are `Lazy<Shader>`-backed singletons — the same `Shader` resource instance every time —
  so comparing `material.Shader == PrimShaderFamily.Scissor` is a correct, cheap way to recover
  "which `Kind` did the alpha-mode logic above just choose" without threading an extra `Kind`
  variable through the whole face-material method.

## What the tests guarantee

`--selftest` compiles all three new shaders and confirms their uniform counts match their base
variants exactly (34/34/35) — the only mechanical guarantee available for a GPU rasterization
setting; nothing in `SLNG.*.Tests` exercises `render_mode`/cull behaviour. The full 563-test suite
passing unmodified confirms the `PbrMaterialData`/`AssetService` changes (the one piece that IS
`src/`, in `tests-rules`' scope) didn't alter any other decode behaviour — `DoubleSided` defaults
false and is purely additive.

## Actual root cause of the reported flicker (v0.20.15-alpha) — the RenderMaterials query was malformed

The double-sided fix above is real and kept, but it was NOT what made the reported conifer flicker.
Re-investigated live on Agni 2026-09-02 with new diagnostics (`[FaceAlpha]` per world-prim face,
`mat=`/`pbr=` added to `[FaceParams]`, `[LegacyMat]` on the fetch):

- The clicked tree's 45 leaf faces all carry a real legacy material id
  (`[FaceParams] … faces: [0]5825da35 mat=6ad7601a …`) — the net side parses it fine.
- But **every** `[FaceAlpha]` line on the whole region said `noMat`, and every `[LegacyMat]` line
  said `requested N -> resolved 0 (RenderMaterials cap present)`. The cap is there; the fetch just
  never returns anything, silently.
- The leaf diffuse `5825da35` then goes through `Image.DetectAlpha()` → `Blend` (soft pine-edge
  alpha) → `prim_blend` → the **sorted transparent pass**. Godot depth-sorts overlapping
  transparent surfaces per-object by approximate centroid distance, so as the camera orbits, the
  draw order of the tree's dozens of leaf-card surfaces keeps flipping → the flicker. Firestorm
  uses the real material (Alpha-Masking, cutoff 100) → opaque/tested pass, depth write, no sort →
  stable.

**Why the fetch returned nothing:** LibreMetaverse 3.1.3's `ObjectManager.RequestMaterialsAsync`
builds the `RenderMaterials` query with `array.Add(uuid)`, serialising each id as an LLSD `uuid`
element. The real viewer sends each id as an LLSD **binary(16)** — `llmaterialmgr.cpp`
`processGetQueue` does `materialsData.append((*itMaterial).asLLSD())` and `LLMaterialID::asLLSD()`
(`llmaterialid.cpp`) is a 16-byte `LLSD::Binary`. The sim unzips the request and reads each entry
with `.asBinary()`, which yields nothing for a `uuid`-typed element, so LMV's request matched zero
materials and the cap returned an empty result with **no error**. Almost certainly worked on
OpenSim (lenient parser) the whole time, which is why it was never caught.

**Fix:** `GridSession.BuildRenderMaterialsQuery` builds the zipped query itself with each id as
`OSD.FromBinary(id.GetBytes())` and POSTs it directly via `HttpCapsClient` (same pattern as
`BUG-NET-07`'s environment-cap fix). The response shape is unchanged, so LMV's own
`LegacyMaterial(OSDMap)` still parses each returned entry; `FetchOneBatchAsync` now logs
`[LegacyMat] POST <status>: N ids -> M materials`. 3 new tests
(`RenderMaterialsQueryTests`) pin that the ids go out as binary(16), not `uuid`.
`v0.20.15-alpha`. **Confirmed in-world 2026-09-02** — the reporting user, orbiting the same conifer:
*"yay es blinkt nicht mehr!!!"*. The canopy is stable.

## Still open

- **Not yet re-verified in-world.** Needs the exact reported scenario — orbiting/zooming a camera
  around the reporting tree — checked again, ideally confirming both that leaves no longer pop in
  and out AND that an ordinary (non-double-sided) prim's interior still stays hidden (the
  `prim_opaque.gdshader` regression this whole cull_back convention exists to prevent).
- **The broader latent issue stands even with materials working:** a genuinely material-free
  soft-alpha face still lands in `prim_blend` / the sorted transparent pass with default
  `RenderPriority`, so overlapping such faces can still sort-flicker under rotation. The
  `[FaceAlpha]` log shows many `-> Blend (SORTED transparent pass)` faces region-wide. Only water
  (`BUG-RENDER-05`, `RenderPriority = -1`) is currently protected. Worth a follow-up if it shows up
  on non-materialled content after this fix.
- **The "flicker while textures are still loading" trigger the user also mentioned is not covered
  here** — that's a one-time placeholder→real-texture transition, a different mechanism from the
  sustained camera-movement flicker this spec addresses. Worth a follow-up only if it's still
  visible/objectionable after this fix.
