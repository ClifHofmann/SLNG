using System.Linq;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class RegionTerrainTests
{
    [Fact]
    public void RegionTerrain_ApplyPatch_UpdatesHeightmapCorrectly()
    {
        var terrain = new RegionTerrain();
        float[] patchHeights = new float[16 * 16];

        // Fill the patch with a specific value
        for (int i = 0; i < patchHeights.Length; i++)
            patchHeights[i] = 15.5f;

        // Apply to patch coordinate (1, 1), which means world coordinates X=16..31, Y=16..31
        terrain.ApplyPatch(1, 1, patchHeights);

        var masterHeights = terrain.GetHeights();

        // Verify a point inside the patch
        int indexInside = (16 + 5) * 256 + (16 + 5);
        Assert.Equal(15.5f, masterHeights[indexInside]);

        // Verify a point outside the patch
        int indexOutside = (0) * 256 + (0);
        Assert.Equal(0.0f, masterHeights[indexOutside]);
    }

    [Fact]
    public void RegionTerrain_ApplyPatch_IgnoresOutOfBounds()
    {
        var terrain = new RegionTerrain(256, 256);
        float[] patchHeights = new float[16 * 16];

        for (int i = 0; i < patchHeights.Length; i++)
            patchHeights[i] = 20.0f;

        // Apply to patch coordinate (20, 20), which is outside 256x256 (max is 15,15)
        terrain.ApplyPatch(20, 20, patchHeights);

        var masterHeights = terrain.GetHeights();
        // The whole map should remain 0
        Assert.All(masterHeights, h => Assert.Equal(0.0f, h));
    }
}
