# [BUG-UI-08] HUD overlay: text horizontally mirrored and click raycast misalignment

- **Feature ID:** `BUG-UI-08`
- **Track:** `ui/render`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
User report and in-world testing with worn mesh HUDs (`/ HUD / lel EvoX (f) 3.1` and `Maitreya Mesh Body - Lara Add-on "Petite" V1.1 HUD`):
1. **Mirrored text/textures:** Certain HUDs (specifically the LeLUTKA EvoX HUD) render horizontally mirrored (`.HSAL` instead of `LASH.`, `MOTTOR`, `POT`, `EDOM AHPLA`, `KSAM`, `DNELE`, `SEHSAL EDIH`, `LLA EDIH`, etc.).
2. **Misaligned clicks:** Clicks on HUD controls either miss the collider, hit the wrong child prim in a multi-prim linkset, or touch an unexpected entity (e.g. logging clicks on entity `d67c2fe0...` while touching entity `040f300b...`).

The goal is to ensure all HUD attachments render with correct facing and orientation, and that mouse clicks hit the exact intended prim/face with accurate face index and UV coordinates.

## Root Cause Analysis

1. **Backface Culling on HUDs (`cull_disabled` vs `cull_back`):**
   - HUD shaders were using `cull_disabled`, rendering backfaces of prims. In SL, creators rotate inactive tabs/panels by 180° to hide them. With `cull_disabled`, Godot drew their backfaces horizontally mirrored. Switching HUD shaders to `cull_back` correctly culls them.
2. **Missing `TEXTURE_TRANSPARENT` & Alpha=0 Hiding:**
   - SL's built-in `TEXTURE_TRANSPARENT` (`8dcd4a48-2d37-4909-9f78-f7a9eb4ef903`) 403'd over HTTP and was rendered opaque white. Fast-pathing it in `AssetService` and mapping both `TEXTURE_TRANSPARENT` and face alpha $\le 0.001$ to `PrimShaderFamily.Hidden` discards geometry for invisible surfaces.
3. **Raycast Hit Backfaces & Missing FaceIndex/UV Coordinates:**
   - Godot's `IntersectRay` defaulted to `HitBackFaces = true`, causing rotated prims to intercept clicks meant for buttons behind them.
   - `TryClickHud` passed default `faceIndex = 0` and zero UVs. Scripts on multi-face HUD button bars (like LeLUTKA) could not identify which button or tab was touched.
4. **Child Prim Transform Updates Dropped in `AvatarRenderer`:**
   - In `AvatarRenderer.cs`, `OnComponentUpdated` handles `TransformComponent` by calling `UpdateVisual` (which only updates avatar body positions).
   - When a child prim of a HUD linkset arrives, its transform is initially unresolved. Once the root prim arrives, `WorldSimulation.RecomposeChildren` updates `TransformComponent` on the child prims and notifies the ECS.

## Acceptance Criteria
- [x] LeLUTKA EvoX HUD and similar mesh HUDs render with correct front-facing orientation (text readable, buttons un-mirrored).
- [x] Inactive panels rotated 180° are culled (`cull_back`) and do not intercept clicks (`HitBackFaces = false`).
- [x] Transparent textures and alpha 0 faces are collapsed/hidden.
- [x] Linkset child prims correctly follow the root prim's position and rotation when streaming in out of order.
- [x] Multi-level linksets recursively recomposed when root arrives.
- [x] Clicking on HUD buttons consumes the input event (no bleed-through to 3D world) and passes exact hit `faceIndex`, hit position, normal, and interpolated UV/ST coordinates.
- [x] Unit tests added for HUD linkset transform composition and rotation.

## Technical Specs & Affected Files
- `app/materials/prim/prim_*_hud.gdshader`:
  - Set `cull_back` on opaque, scissor, and blend HUD variants.
- `app/scripts/AvatarRenderer.cs`:
  - Handle `TransformComponent` updates for attachments in `OnComponentUpdated`.
  - Clamp `slOffset` only for root prims (`ParentLocalId == 0`).
  - Map `TEXTURE_TRANSPARENT` and alpha $\le 0.001$ to `PrimShaderFamily.Hidden`.
  - Pass `defaultFace` UV repeats/offsets in `UpdateHudAttachment`.
  - Store per-triangle face index and vertex UVs during `BuildHudArrayMesh`.
  - In `TryClickHud`, set `query.HitBackFaces = false`, compute hit `faceIndex` and barycentric UV coordinates, and pass to `ClickObjectAsync`.
  - Mark viewport input handled on HUD hit.
- `src/SLNG.Assets/AssetService.cs`:
  - Fast-path `TEXTURE_TRANSPARENT` and `WHITE_TEXTURE`.
- `src/SLNG.Core/WorldSimulation.cs`:
  - Recurse in `RecomposeChildren` for multi-level child linksets.
- `tests/SLNG.Core.Tests/WorldSimulationTests.cs`:
  - Added unit tests for nested linksets and rotated attachment children.
