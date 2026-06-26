using System;

namespace SLNG.Core;

/// <summary>
/// Maintains the continuous heightmap for a region.
/// </summary>
public class RegionTerrain
{
    public const int DefaultRegionSize = 256;
    public const int PatchSize = 16;

    // The master heightmap (Y-up elevation).
    private float[] _heights;

    public int Width { get; }
    public int Height { get; }

    public RegionTerrain(int width = DefaultRegionSize, int height = DefaultRegionSize)
    {
        Width = width;
        Height = height;
        _heights = new float[Width * Height];
    }

    /// <summary>
    /// Applies a 16x16 patch to the master heightmap.
    /// </summary>
    public void ApplyPatch(int patchX, int patchY, float[] patchHeights)
    {
        if (patchHeights == null || patchHeights.Length != PatchSize * PatchSize)
            return;

        int startX = patchX * PatchSize;
        int startY = patchY * PatchSize;

        // If the patch exceeds our current terrain size, we ignore it for now.
        // A dynamic resizing system could be implemented here for megaregions.
        if (startX >= Width || startY >= Height)
            return;

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
