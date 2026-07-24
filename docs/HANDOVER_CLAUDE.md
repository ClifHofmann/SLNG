# Handover to Claude

> **Superseded** — the "Open Suspects" below were all investigated and resolved (or ruled out)
> in a later session. See `docs/HANDOVER_TREE_BUG.md` for the current, authoritative status:
> the sculpt-resize and cache-collision bugs are fixed and confirmed; the tree's remaining
> visual mismatch vs. Firestorm is a separate, still-open, lower-priority item (investigation
> paused, not a general SLNG bug). Kept here for historical context only.

## Goal
Fix the incorrect rendering of sculpted prims (Sculpts), specifically for an organic tree object (`9ee5633a-b360-4d48-86c9-deaae89be0bb`).

## Current Status
- I successfully fixed the texture-swapping issue on procedural multi-face prims by mapping the `trueFaceId` back to the original `TextureEntry` using `object.ReferenceEquals` on the `GetFace(i)` objects in `PrimMeshService.cs`.
- However, the user reported that a sculpt object (a tree) is rendering entirely wrong. It looks spiky, jagged, and deformed.
- I discovered that `PrimMeshService.cs` had `Scale = new LMVector3(10f, 10f, 10f)` accidentally hardcoded which baked a 10x multiplier into the vertices. I reverted this to `1, 1, 1`. The user tested again, but stated it still looks wrong.

## Open Suspects & Clues

### 1. AssetService Resizing (Nearest Neighbor)
In `AssetService.cs`, when a J2C sculpt texture is not exactly 64x64, it forces a resize using `ImageMagick.FilterType.Point` (Nearest Neighbor):
```csharp
if (width != 64 || height != 64) {
    image.FilterType = ImageMagick.FilterType.Point;
    image.Resize(new ImageMagick.MagickGeometry("64x64!") { IgnoreAspectRatio = true });
}
```
In SL, sculpt maps can be 128x128. If we downsample a 128x128 vertex grid to 64x64 using Nearest Neighbor, we skip 75% of the vertices instead of interpolating them. This creates severe stair-stepping and jagged spikes on organic shapes. LibreMetaverse's `SculptMap` class already implements proper linear downscaling (`ScaleImage`), so this premature `Point` resize in `AssetService` might be mangling the coordinate data before the mesher even sees it.

### 2. Degenerate Triangle Filtering
In `PrimMeshService.cs` inside the `Convert` method, I recently added a hack to filter out triangles with zero-length normals:
```csharp
if (n1.LengthSquared() < 0.0001f || n2.LengthSquared() < 0.0001f || n3.LengthSquared() < 0.0001f) {
    continue; // Skip degenerate triangle
}
```
If normal generation for sculpts produces some flat or zero normals at the poles, this filter will aggressively delete triangles, leaving holes and jagged disconnected floating vertices that look like "spikes".

### 3. Channel Ordering (RGB vs BGR)
`ImageMagick` `GetPixels().GetValues()` extracts raw bytes. We map them linearly `rgba[d] = raw[s]`. `PrimMeshService` uses `SKColorType.Rgba8888`. This seems logically correct, but if there is any mismatch in the byte order (e.g., ImageMagick actually yielding BGR in memory, or Skia expecting BGRA), the X and Z axes of the sculpt will be swapped, turning the tree inside out.

### 4. Sculpt Types
SL supports spherical, cylindrical, planar, and torus sculpts. The tree might be a specific sculpt type. Ensure that the `SculptType` byte passed to `GenerateSculpt` is correctly mapping to the `LibreMetaverse.SculptType` enum (some masks might be required if it contains mirroring flags: e.g., `sculptType & 0x3F`).

## Next Steps for Claude
1. Inspect the sculpt texture for the tree (`9ee5633a-b360-4d48-86c9-deaae89be0bb`) in Godot or via a debug dump to see if it is corrupted or stair-stepped.
2. Try removing the `ImageMagick.FilterType.Point` resize block in `AssetService.cs` and let `MeshFoundry` handle the 128x128 -> 64x64 scaling natively using its linear interpolation.
3. Review the degenerate triangle filter in `PrimMeshService.Convert` and potentially remove it.
4. Verify whether `sculptType` needs masking (e.g. `Type = (SculptType)(sculptType & 0x07)`).
