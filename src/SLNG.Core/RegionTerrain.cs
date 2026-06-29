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

    /// <summary>
    /// Applies a 16x16 patch to the master heightmap.
    /// </summary>
    public void ApplyPatch(int patchX, int patchY, float[] patchHeights)
    {
        if (patchHeights == null || patchHeights.Length != PatchSize * PatchSize)
            return;

        int requiredWidth = (patchX + 1) * PatchSize;
        int requiredHeight = (patchY + 1) * PatchSize;

        if (requiredWidth > Width || requiredHeight > Height)
        {
            // Snap to multiples of 256 for standard varregion sizes (256, 512, 1024, etc.)
            int newW = (int)Math.Ceiling(requiredWidth / 256.0) * 256;
            int newH = (int)Math.Ceiling(requiredHeight / 256.0) * 256;
            Resize(Math.Max(Width, newW), Math.Max(Height, newH));
        }

        int startX = patchX * PatchSize;
        int startY = patchY * PatchSize;

        for (int y = 0; y < PatchSize; y++)
        {
            for (int x = 0; x < PatchSize; x++)
            {
                int localIndex = y * PatchSize + x;
                int globalIndex = (startY + y) * Width + (startX + x);

                if (globalIndex < _heights.Length)
                {
                    _heights[globalIndex] = patchHeights[localIndex];
                }
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
