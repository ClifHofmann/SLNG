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

## Still broken (what the user sees)
1. **No ground** beyond 256 m — region is a **varregion**; terrain is hardcoded 256×256. → spawned task **task_2cad84f0** "Varregion terrain": size to `Simulator.SizeX/SizeY`, chunk the mesh, then re-enable full collision (the in-bounds guard in `AvatarController._Process`, commit 77d870d).
2. **Prims still look "kaputt"** even after floating origin. Unconfirmed cause — investigate next, likely candidates:
   - **Back-face culling**: object materials cull back faces (avatar uses `CullMode.Disabled`); inside-out prims / viewing from inside look broken. Quick test: set `CullMode.Disabled` on prim materials.
   - Specific sculpt/mesh assets decoding with bad geometry.
   - Possible normal/winding issues from SurfaceTool.

## How to run / verify
`. tools/dev-env.ps1` or `& $env:USERPROFILE\.dotnet\dotnet.exe`. Build `app/SLNG.App.csproj` (Godot uses `app/.godot/mono/temp/bin/Debug/`, NOT `dotnet build SLNG.sln`). Verify DLL fresh: read as UTF-8 for method names, UTF-16 for string literals. Log: `%APPDATA%\Godot\app_userdata\SLNG\logs`. Visual changes need a human login (headless can't render).

## Next step
Recommend: **varregion terrain** (task_2cad84f0) — brings the ground back + fixes landing everywhere. Then the prim back-face/culling check.
