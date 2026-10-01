using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class RegionHandleTests
{
    [Fact]
    public void East_is_the_high_half_and_north_the_low_half()
    {
        ulong handle = RegionHandle.FromOrigin(256000, 256512);

        Assert.Equal(256000u, RegionHandle.OriginX(handle));
        Assert.Equal(256512u, RegionHandle.OriginY(handle));
    }

    [Fact]
    public void Grid_coordinates_are_the_origin_over_256()
    {
        ulong handle = RegionHandle.FromOrigin(256000, 256512);

        Assert.Equal(1000, RegionHandle.GridX(handle));
        Assert.Equal(1002, RegionHandle.GridY(handle));
    }

    [Fact]
    public void A_handle_survives_a_round_trip_at_the_far_corner_of_the_grid()
    {
        // A uint's worth of metres in each half: nothing may overflow into the other half.
        ulong handle = RegionHandle.FromOrigin(uint.MaxValue - 255, 0);

        Assert.Equal(uint.MaxValue - 255, RegionHandle.OriginX(handle));
        Assert.Equal(0u, RegionHandle.OriginY(handle));
    }

    [Fact]
    public void The_south_west_region_of_the_grid_is_zero_zero()
    {
        Assert.Equal(0, RegionHandle.GridX(0));
        Assert.Equal(0, RegionHandle.GridY(0));
    }
}
