# Handoff (2026-06-29) — OSGrid rendering

Active branch: **`feat/sculpt-rendering`** → open PR **#17**. Build 0/0, tests 31/31.
(PR #16 prim-shapes was closed — it's an ancestor of #17. #13/#14 closed earlier.)

## Done this session
World now renders OSGrid (`hg.osgrid.org`, "The Dangazi Forest") far more coherently:
- **Sculpts**: real geometry via MeshFoundry `GenerateFacetedSculptMesh` (sculpt map → SKBitmap). Pkg `LibreMetaverse.Rendering.MeshFoundry` 3.0.0 + `SkiaSharp`.
- **Linksets**: child prims composed to world space in `WorldSimulation` (`TransformComponent.Local*`/`ParentLocalId` + children index). Was the main "shattered builds".
- **Floating origin** (`RenderConfig.SetRegionOrigin`/`ToGodot`): render relative to current region — OSGrid global coords are ~millions and overflow float32 → jitter/Z-fight. All renderers use `ToGodot` now.
- **HTTP textures** (`Settings.TexturePipeline.UseHttpTextures`) — fixes truncated J2C / white objects. Texture cache self-heals + retries; `Settings.LogLevel=Warning` quiets console.
- **Per-face textures**: `FaceTexture[]` per prim face → `SetSurfaceOverrideMaterial`; `MeshSubmesh.FaceIndex`; alpha-cutout for foliage.
- **HUD** (top-right: region/coords/alt/draw), **fly** (E up/Q down/Home), draw-distance F3/F4, VRAM budget via GpuCache.

## Recently fixed (verify in-world)
- **Varregion terrain** (commit 1fa3a1e): terrain now sized to `Simulator.SizeX/SizeY` (region size flows through TerrainPatch/Settings events → `World.GetOrCreateTerrain`). Rebuild throttled to ~0.75s. Should restore ground + landing beyond 256 m. If a 1024² region still stutters on load, chunk the terrain mesh (currently one mesh + trimesh collider per region).

## Still broken (what the user sees)
1. **Prims still look "kaputt"** even after floating origin. Unconfirmed cause — investigate next, likely candidates:
   - **Back-face culling**: object materials cull back faces (avatar uses `CullMode.Disabled`); inside-out prims / viewing from inside look broken. Quick test: set `CullMode.Disabled` on prim materials.
   - Specific sculpt/mesh assets decoding with bad geometry.
   - Possible normal/winding issues from SurfaceTool.

## How to run / verify
`. tools/dev-env.ps1` or `& $env:USERPROFILE\.dotnet\dotnet.exe`. Build `app/SLNG.App.csproj` (Godot uses `app/.godot/mono/temp/bin/Debug/`, NOT `dotnet build SLNG.sln`). Verify DLL fresh: read as UTF-8 for method names, UTF-16 for string literals. Log: `%APPDATA%\Godot\app_userdata\SLNG\logs`. Visual changes need a human login (headless can't render).

## Rigged mesh (commit 1360db2) — NEEDS IN-WORLD TEST
Worn mesh (mesh bodies/clothing) was rendered as a static blob bolted to one bone (the "yellow blob" at the avatar's legs). Now: `AssetService.Decode` extracts the LLMesh skin section (`FacetedMesh.SkinData`, `Face.Weights`) into neutral `MeshSkin`/`VertexBoneWeights` (MeshData.cs). `AvatarRenderer.BuildRiggedMeshInstance` skins worn mesh to the avatar `Skeleton3D` (joint name → bone global-rest inverse, bind-shape matrix applied to verts), parented as a direct child of the skeleton. Static attachments unchanged.
- If rigged mesh is mis-scaled/offset: suspect bind-shape handling or fitted-mesh collision-volume joints (BUTT/PELVIS/BELLY etc.) missing from `avatar_skeleton.xml` → their weights get dropped+renormalized (fallback may distort). Next refinement: use the asset's own `InverseBindMatrices` (decoded, currently unused) instead of skeleton rest, and/or add collision-volume bones.
- Per-face textures on rigged mesh not done — uses single `prim.TextureId`.

## Known failing test (pre-existing, not mine)
`RegionTerrainTests.RegionTerrain_ApplyPatch_IgnoresOutOfBounds` — out-of-bounds patch leaks one 16×16 block. Spawned task task_4da63285.

## Next step
**Prim "kaputt" investigation** — start with the back-face culling test (set `CullMode.Disabled` on object prim materials in `ObjectRenderer.BuildFaceMaterialAsync`). If that doesn't fix it, add a one-shot diagnostic logging a sample prim's submesh/vertex/normal counts to find bad geometry.
