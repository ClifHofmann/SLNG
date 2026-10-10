# [BUG-PERF-15] No synchronous RenderingServer readbacks on hot paths

- **Feature ID:** `BUG-PERF-15`
- **Track:** `render`
- **Status:** `✅ Done`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md) -- follows FEAT-PERF-13 and ADR 0004 (P1 prerequisite)

## Overview & Goal

Under the separate render thread (FEAT-PERF-13), any synchronous RenderingServer getter or GPU buffer download (`ArrayMesh.SurfaceGetArrays`, `CreateTrimeshShape`, `ImageTexture.GetImage`) blocks the calling main thread waiting for the render thread to flush its command queue and read back from VRAM. On hot paths—such as splitting sorted-transparent surfaces (`SplitSortedSurfaces`), constructing pick colliders, or shrinking resident textures under VRAM budget pressure—these round trips caused severe hitches and freezes ("Hänger").

BUG-PERF-15 eliminates synchronous read-backs on hot paths:
1. `MeshSurfaceCache`: Caches prepared CPU vertex/index arrays for committed `ArrayMesh` instances via `ConditionalWeakTable<ArrayMesh, Godot.Collections.Array[]>`.
2. `ObjectRenderer.SplitSortedSurfaces` & `AvatarRenderer.SplitSortedSurfaces`: Clone submeshes using cached CPU arrays instead of calling `mesh.SurfaceGetArrays(s)`.
3. `AvatarRenderer.AddRiggedPickBody` & `AddAttachmentPickBody`: Construct collision hulls from cached CPU arrays instead of `SurfaceGetArrays` or `mesh.CreateTrimeshShape()`.
4. `SelectionOutline.BuildOutlineHull`: Use cached CPU surface arrays when available.
5. `GpuCache.StartShrink`: Banish `QueueReadBackShrink` and `GetImage` fallbacks on hot paths.
6. `AssetService.TryGetLocalDecodedAsync`: Fall back to decoding local `.j2c` disk cache files on thread pool workers when decoded cache misses, ensuring shrinks have CPU image data.
7. `AvatarRenderer.ClassifyAlpha` & `ObjectRenderer.ApplyAlphaCutout`: Avoid `GetImage()` stalls under separate render thread.

## Acceptance Criteria
- [x] `MeshSurfaceCache` retains CPU surface arrays for committed meshes and serves surface cloning without `SurfaceGetArrays`.
- [x] `SplitSortedSurfaces` on both objects and avatars uses cached CPU arrays.
- [x] `AddRiggedPickBody` and attachment pick hulls use cached CPU arrays without VRAM readback stalls.
- [x] Texture shrink does not perform synchronous VRAM readbacks (`tex.GetImage()`) on the main thread.
- [x] `dotnet test` and headless `--selftest` pass cleanly.

## Technical Specs & Affected Files
- `app/scripts/MeshSurfaceCache.cs`
- `app/scripts/SelfTest.MeshSurfaceCache.cs`
- `app/scripts/ObjectRenderer.MeshPrepare.cs`
- `app/scripts/ObjectRenderer.cs`
- `app/scripts/AvatarRenderer.AttachWorker.cs`
- `app/scripts/AvatarRenderer.cs`
- `app/scripts/SelectionOutline.cs`
- `app/scripts/GpuCache.cs`
- `src/SLNG.Assets/AssetService.cs`
- `app/scripts/Boot.cs`
