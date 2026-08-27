# [FEAT-RENDER-06] Fullbright Material Property

- **Feature ID:** `FEAT-RENDER-06`
- **Track:** `render`
- **Status:** `✅ Done` — confirmed in-world 2026-08-27 (a fullbright object renders at full
  colour regardless of time of day, non-fullbright faces unchanged)
- **Owner:** `claude`
- **Agent:** `graphics-engineer`
- **Dep:** `FEAT-RENDER-01` (the custom spatial-shader family — already live)

## Goal
Read SL's per-face **fullbright** flag (`LLTextureEntry::getFullbright`) and reproduce it in the
custom prim shader: a fullbright face ignores scene lighting and renders at its full unlit
texture colour. Signs, screens, neon, holo-displays — anything meant to look self-lit. Very
common on both SL and OpenSim content; without it those faces render darkened by the scene's
lighting.

Not to be confused with **glow** (a bloom radius, separate `TextureEntryFace.Glow`) or with
**glTF emissive** (`PbrMaterialData.EmissiveFactor`). A face can be fullbright *and* carry a
glTF emissive; they add.

## What the viewer does
A fullbright face goes into `LLDrawPoolFullbright`, whose shader outputs the diffuse texture ×
vertex colour with no lighting term. It still receives atmospherics/fog
(`fullbrightAtmosTransportFrag`), so a distant fullbright sign still hazes with distance.

## Implementation

**Wire → world model (`src/`):**
- `FaceTexture.Fullbright` (new bool, defaulted) — the per-face flag.
- `PrimitiveComponent.Fullbright` (new) — the **default** face's flag, for prims that send no
  per-face entries (same pattern as `TexGen`).
- `ObjectUpdateEvent.Fullbright` (new, trailing, defaulted) — carries the default-face flag.
- `GridSession`: per-face `new FaceTexture(...)` passes `f.Fullbright`; the `ObjectUpdateEvent`
  gets `defaultFace?.Fullbright ?? false`.
- `WorldSimulation`: passes it into `PrimitiveComponent` on create and on the field-by-field
  update path.

**Render (`app/`):**
- `prim_common.gdshaderinc`: `uniform bool fullbright = false;`. At the end of `slng_shade`,
  after atmospherics and glTF emission are folded into `out_albedo` / `out_emission`:
  ```glsl
  if (fullbright) { out_emission += out_albedo; out_albedo = vec3(0.0); }
  ```
  Routing the (already atmospherics-applied) albedo through EMISSION and zeroing ALBEDO makes
  clustered lighting, ambient and GI contribute nothing while keeping fog. It's a **no-op for
  the `unshaded` HUD variants**, whose output is `ALBEDO + EMISSION` regardless of which one
  holds the value — so the one shared code path is correct for every variant.
- `PrimShaderFamily.Fullbright` StringName.
- `ObjectRenderer.BuildFaceMaterialAsync` and `AvatarRenderer.BuildFaceMaterialAsync` (worn
  mesh faces can be fullbright too) set the parameter per face; the default-face constructions
  pass `prim.Fullbright`.

## Acceptance criteria
- [x] A fullbright face renders at full unlit colour — unaffected by scene lighting / time of day.
- [x] Non-fullbright faces are unchanged.
- [x] Per-face: face 3 fullbright while face 4 is not, on the same prim, both correct.
- [x] Fog/atmospherics still apply to a fullbright face (no shader path bypasses
      `slng_apply_atmospherics`).
- [x] Worn-mesh attachment faces honour it too (shared `BuildFaceMaterialAsync`).
- [x] Unit tests pin the wire → `PrimitiveComponent` / `FaceTexture` plumbing
      (`FullbrightTests`, 3 cases).
- [x] In-world 2026-08-27: a fullbright object renders at full colour regardless of scene
      lighting / time of day; non-fullbright faces unchanged.

## Verification
`dotnet build SLNG.sln` + `app/` clean; `dotnet test` 325 green; `dotnet format` clean;
`--selftest` 24/24 (prim shaders 33→34 / 34→35 uniforms, variants still match). `v0.9.54-alpha`.
