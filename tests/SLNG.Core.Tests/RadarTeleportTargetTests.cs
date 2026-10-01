using System.Numerics;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class RadarTeleportTargetTests
{
    private static RegionTerrain FlatTerrain(float height)
    {
        var terrain = new RegionTerrain();
        var patch = new float[RegionTerrain.PatchSize * RegionTerrain.PatchSize];
        System.Array.Fill(patch, height);
        for (int py = 0; py < RegionTerrain.DefaultRegionSize / RegionTerrain.PatchSize; py++)
            for (int px = 0; px < RegionTerrain.DefaultRegionSize / RegionTerrain.PatchSize; px++)
                terrain.ApplyPatch(px, py, patch);
        return terrain;
    }

    [Fact]
    public void Known_ground_gives_ground_height_plus_clearance()
    {
        var target = RadarTeleportTarget.Resolve(FlatTerrain(22f), new Vector2(100.4f, 50.9f), fallbackZ: 99f);

        Assert.Equal(100.4f, target.X, 3);
        Assert.Equal(50.9f, target.Y, 3);
        Assert.Equal(22f + RadarTeleportTarget.ClearanceMetres, target.Z, 3);
    }

    [Fact]
    public void Unloaded_terrain_falls_back_to_the_given_height()
    {
        var target = RadarTeleportTarget.Resolve(new RegionTerrain(), new Vector2(10, 10), fallbackZ: 31f);

        Assert.Equal(31f, target.Z, 3);
    }

    [Fact]
    public void No_terrain_at_all_falls_back_to_the_given_height()
    {
        var target = RadarTeleportTarget.Resolve(null, new Vector2(10, 10), fallbackZ: 31f);

        Assert.Equal(31f, target.Z, 3);
    }

    [Fact]
    public void Nothing_known_gives_zero_and_lets_the_simulator_place_the_avatar()
    {
        var target = RadarTeleportTarget.Resolve(null, new Vector2(10, 10), fallbackZ: null);

        Assert.Equal(0f, target.Z, 3);
    }
}
