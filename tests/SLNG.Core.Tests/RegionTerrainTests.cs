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
    public void RegionTerrain_ApplyPatch_GrowsToFitOutOfBoundsPatch()
    {
        // Regression test: LibreMetaverse 3.0.0 never threads the real varregion size into
        // Simulator.SizeX/SizeY for either the login or TeleportFinish connect paths (both always
        // fall back to the 256x256 default -- see ApplyPatch's doc comment), so a varregion's
        // TerrainSettingsEvent/TerrainPatchEvent can report a bogus 256x256 size even though OpenSim
        // is sending 16x16 patches for the sim's real, larger footprint. ApplyPatch must trust the
        // patch coordinates it actually receives and grow to fit them instead of silently dropping
        // terrain data outside the (possibly wrong) initial size.
        var terrain = new RegionTerrain(256, 256);
        float[] patchHeights = new float[16 * 16];

        for (int i = 0; i < patchHeights.Length; i++)
            patchHeights[i] = 20.0f;

        // Patch coordinate (20, 20) covers world X/Y 320..335 -- outside the initial 256x256 map,
        // but well within a legitimate varregion (e.g. a 3x3 var is 768x768).
        terrain.ApplyPatch(20, 20, patchHeights);

        Assert.True(terrain.Width >= 336);
        Assert.True(terrain.Height >= 336);

        var masterHeights = terrain.GetHeights();
        int indexInside = 325 * terrain.Width + 325;
        Assert.Equal(20.0f, masterHeights[indexInside]);

        // Original 0..255 data must survive the grow/copy untouched.
        int indexOriginalCorner = 0 * terrain.Width + 0;
        Assert.Equal(0.0f, masterHeights[indexOriginalCorner]);
    }

    [Fact]
    public void RegionTerrain_ApplyPatch_DropsPatchBeyondMaxRegionSize()
    {
        // A patch coordinate that would require growing past the legal OpenSim varregion ceiling
        // (32x32 standard regions = 8192x8192m) is corrupt/spoofed, not a legitimate region -- it
        // must still be rejected so a bogus coordinate can't force an unbounded allocation.
        var terrain = new RegionTerrain(256, 256);
        float[] patchHeights = new float[16 * 16];
        for (int i = 0; i < patchHeights.Length; i++)
            patchHeights[i] = 99.0f;

        int patchIndex = RegionTerrain.MaxRegionSize / RegionTerrain.PatchSize; // one past the ceiling
        terrain.ApplyPatch(patchIndex, patchIndex, patchHeights);

        Assert.Equal(256, terrain.Width);
        Assert.Equal(256, terrain.Height);
    }

    [Fact]
    public void RegionTerrain_EnsureSize_ClampsToMaxRegionSize()
    {
        var terrain = new RegionTerrain(256, 256);

        terrain.EnsureSize(RegionTerrain.MaxRegionSize * 2, RegionTerrain.MaxRegionSize * 2);

        Assert.Equal(RegionTerrain.MaxRegionSize, terrain.Width);
        Assert.Equal(RegionTerrain.MaxRegionSize, terrain.Height);
    }

    [Fact]
    public void RegionTerrain_TryGetKnownHeight_FalseForCellWithoutAPatch()
    {
        // Regression test for the landmark-teleport "falls through the floor" bug: a freshly
        // constructed heightmap defaults every cell to 0.0f before any patch arrives, which must
        // NOT be reported as a known/trustworthy height -- see AvatarController's ground-clamp
        // fallback, which used to treat this as "ground is at Z=0" and drop the avatar toward it.
        var terrain = new RegionTerrain();

        bool known = terrain.TryGetKnownHeight(5, 5, out float height);

        Assert.False(known);
        Assert.Equal(0f, height);
    }

    [Fact]
    public void RegionTerrain_TryGetKnownHeight_TrueOnlyInsideAppliedPatch()
    {
        var terrain = new RegionTerrain();
        float[] patchHeights = new float[16 * 16];
        for (int i = 0; i < patchHeights.Length; i++)
            patchHeights[i] = 22.5f;

        // Patch (1, 1) covers world X/Y 16..31.
        terrain.ApplyPatch(1, 1, patchHeights);

        Assert.True(terrain.TryGetKnownHeight(20, 20, out float insideHeight));
        Assert.Equal(22.5f, insideHeight);

        Assert.False(terrain.TryGetKnownHeight(0, 0, out float outsideHeight));
        Assert.Equal(0f, outsideHeight);
    }

    [Fact]
    public void RegionTerrain_TryGetKnownHeight_FalseOutOfBounds()
    {
        var terrain = new RegionTerrain(256, 256);

        Assert.False(terrain.TryGetKnownHeight(-1, 0, out _));
        Assert.False(terrain.TryGetKnownHeight(0, -1, out _));
        Assert.False(terrain.TryGetKnownHeight(256, 0, out _));
        Assert.False(terrain.TryGetKnownHeight(0, 256, out _));
    }

    [Fact]
    public void RegionTerrain_TryGetKnownHeight_SurvivesResize()
    {
        // Resize() (via a growing ApplyPatch) must carry the loaded-mask along with the height
        // data, not just reallocate a fresh all-false mask that would un-mark already-loaded
        // ground as unknown.
        var terrain = new RegionTerrain(256, 256);
        float[] patchHeights = new float[16 * 16];
        for (int i = 0; i < patchHeights.Length; i++)
            patchHeights[i] = 10.0f;
        terrain.ApplyPatch(1, 1, patchHeights);

        // Grow it via an out-of-bounds patch elsewhere.
        float[] farPatchHeights = new float[16 * 16];
        for (int i = 0; i < farPatchHeights.Length; i++)
            farPatchHeights[i] = 30.0f;
        terrain.ApplyPatch(20, 20, farPatchHeights);

        Assert.True(terrain.TryGetKnownHeight(20, 20, out float survivedHeight));
        Assert.Equal(10.0f, survivedHeight);
    }
}
