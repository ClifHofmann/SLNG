using System;
using System.Collections.Generic;
using System.Linq;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>BUG-AVATAR-19: a reduced avatar loads its system bakes and draws each part with its bake once it has arrived.</summary>
public class StandInBakePolicyTests
{
    private static Guid Id(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    [Fact]
    public void An_avatar_without_an_appearance_requests_nothing()
    {
        Assert.Empty(StandInBakePolicy.WantedBakes(null));
        Assert.Empty(StandInBakePolicy.WantedBakes(new Dictionary<int, Guid>()));
    }

    [Fact]
    public void Empty_ids_are_not_requested()
    {
        var baked = new Dictionary<int, Guid> { [8] = Guid.Empty, [9] = Id(9), [10] = Guid.Empty };

        var wanted = StandInBakePolicy.WantedBakes(baked);

        Assert.Equal(new[] { 9 }, wanted.Select(w => w.Key));
    }

    [Fact]
    public void The_five_system_channels_are_requested_in_order()
    {
        var baked = new Dictionary<int, Guid> { [20] = Id(20), [11] = Id(11), [10] = Id(10), [9] = Id(9), [8] = Id(8) };

        var wanted = StandInBakePolicy.WantedBakes(baked);

        Assert.Equal(new[] { 8, 9, 10, 11, 20 }, wanted.Select(w => w.Key));
        Assert.Equal(Id(20), wanted.Last().Value);
    }

    [Fact]
    public void Channels_a_stand_in_does_not_draw_are_ignored()
    {
        // 19 is the skirt (no part), 12 and 13 are wearable layers, not bakes, 42 is a BoM extension channel.
        var baked = new Dictionary<int, Guid> { [19] = Id(19), [12] = Id(12), [13] = Id(13), [42] = Id(42), [8] = Id(8) };

        var wanted = StandInBakePolicy.WantedBakes(baked);

        Assert.Equal(new[] { 8 }, wanted.Select(w => w.Key));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-5f)]
    [InlineData(float.NaN)]
    [InlineData(10f)]
    public void The_area_is_never_zero_or_tiny(float measured)
    {
        // Zero is what a bake asks with when it must stay full size; a stand-in must not read as that.
        Assert.Equal(StandInBakePolicy.MinPixelArea, StandInBakePolicy.PixelAreaForStandIn(measured));
    }

    [Fact]
    public void A_larger_measured_area_is_passed_through()
    {
        Assert.Equal(4000f, StandInBakePolicy.PixelAreaForStandIn(4000f));
    }

    [Fact]
    public void A_part_shows_its_bake_only_once_it_was_applied()
    {
        Assert.True(StandInBakePolicy.ShowsBake(bakeApplied: true));
        Assert.False(StandInBakePolicy.ShowsBake(bakeApplied: false));
    }

    [Theory]
    [InlineData("head", true)]
    [InlineData("upper_body", true)]
    [InlineData("lower_body", true)]
    [InlineData("eye", true)]
    [InlineData("hair", false)]
    [InlineData("eyelashes", false)]
    [InlineData("skirt", false)]
    public void Body_surfaces_fill_hidden_alpha_with_jelly_but_hair_and_lashes_keep_their_cutout(string part, bool bodySurface)
    {
        Assert.Equal(bodySurface, StandInBakePolicy.IsBodySurface(part));
    }

    [Fact]
    public void The_cutoff_matches_the_full_avatars_alpha_scissor()
    {
        Assert.Equal(0.5f, StandInBakePolicy.HiddenAlphaCutoff);
    }
}
