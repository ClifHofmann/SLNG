using System;

namespace SLNG.Core;

/// <summary>
/// Maintains the continuous heightmap for a region.
/// </summary>
public class RegionTerrain
{
    public const int DefaultRegionSize = 256;
    public const int PatchSize = 16;

    // Terrain Settings
    public Guid TerrainDetail0 { get; set; }
    public Guid TerrainDetail1 { get; set; }
    public Guid TerrainDetail2 { get; set; }
    public Guid TerrainDetail3 { get; set; }

    public float[] TerrainStartHeights { get; } = new float[4];
    public float[] TerrainHeightRanges { get; } = new float[4];

    public float WaterHeight { get; set; } = 20.0f; // Default OpenSim water height

    // The master heightmap (Y-up elevation).
    private float[] _heights;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public RegionTerrain(int width = DefaultRegionSize, int height = DefaultRegionSize)
    {
        Width = width;
        Height = height;
        _heights = new float[Width * Height];
    }

    private void Resize(int newWidth, int newHeight)
    {
        float[] newHeights = new float[newWidth * newHeight];
        for (int y = 0; y < Height; y++)
        {
            Array.Copy(_heights, y * Width, newHeights, y * newWidth, Width);
        }
        Width = newWidth;
        Height = newHeight;
        _heights = newHeights;
    }

    /// <summary>Grows the heightmap to at least the given dimensions, preserving existing data.
    /// Varregion size is learned from the terrain/settings events; growth happens here (driven by
    /// the real region size) rather than in <see cref="ApplyPatch"/>, so a spurious out-of-bounds
    /// patch can never silently enlarge — and corrupt — the map.</summary>
    public void EnsureSize(int width, int height)
    {
        if (width > Width || height > Height)
            Resize(Math.Max(Width, width), Math.Max(Height, height));
    }

    /// <summary>
    /// Applies a 16x16 patch to the master heightmap. Patches whose 16x16 block falls outside
    /// the region are ignored — the terrain is already sized to the real region dimensions.
    /// </summary>
    public void ApplyPatch(int patchX, int patchY, float[] patchHeights)
    {
        if (patchHeights == null || patchHeights.Length != PatchSize * PatchSize)
            return;
        if (patchX < 0 || patchY < 0)
            return;

        int startX = patchX * PatchSize;
        int startY = patchY * PatchSize;

        // Reject a patch whose full 16x16 block does not fit within the region.
        if (startX + PatchSize > Width || startY + PatchSize > Height)
            return;

        for (int y = 0; y < PatchSize; y++)
        {
            for (int x = 0; x < PatchSize; x++)
            {
                int localIndex = y * PatchSize + x;
                int globalIndex = (startY + y) * Width + (startX + x);
                _heights[globalIndex] = patchHeights[localIndex];
            }
        }
    }

    /// <summary>
    /// Gets a copy of the current heightmap.
    /// </summary>
    public float[] GetHeights()
    {
        float[] copy = new float[_heights.Length];
        Array.Copy(_heights, copy, _heights.Length);
        return copy;
    }
}
