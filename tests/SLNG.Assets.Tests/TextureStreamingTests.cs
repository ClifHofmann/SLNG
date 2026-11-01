using SLNG.Net;
using Xunit;

namespace SLNG.Assets.Tests;

/// <summary>
/// FEAT-PERF-29: the pure rules of progressive texture streaming -- which discard a face needs, which
/// discard the bytes in hand cover, what a coarse upload is "built for", and the decode retry that works
/// around CoreJ2K failing at some cut points.
/// </summary>
// TextureLod.DiscardLevelFor reads the static GlobalLodBias; see ReduceLevelDecodeTests.
[Collection("TextureLod.GlobalLodBias")]
public class TextureStreamingTests
{
    [Fact]
    public void Streaming_is_on_unless_the_command_line_switches_it_off()
    {
        // FEAT-PERF-29: on by default since v0.28.84 (the A/B: cold texture bytes 1.94 -> 0.83 GB), and no
        // longer tied to --diag. --tex-stream stays accepted as a no-op so older scripts keep working.
        Assert.True(TextureStreaming.EnabledByArgs(System.Array.Empty<string>()));
        Assert.True(TextureStreaming.EnabledByArgs(new[] { "--tex-stream" }));
        Assert.True(TextureStreaming.EnabledByArgs(new[] { "--diag", "--tex-stream-first=600" }));
        Assert.False(TextureStreaming.EnabledByArgs(new[] { "--no-tex-stream" }));
        Assert.False(TextureStreaming.EnabledByArgs(new[] { "--tex-stream", "--no-tex-stream" }));
    }

    [Fact]
    public void First_request_is_the_estimators_discard_5_size_for_an_unknown_size()
    {
        Assert.Equal(J2kByteSizeEstimator.CalcDataSizeJ2C(0, 0, 5), TextureStreaming.DefaultFirstRequestBytes);
        Assert.Equal(18840, TextureStreaming.DefaultFirstRequestBytes);
    }

    [Fact]
    public void Opaque_target_is_the_discard_the_pixel_area_needs()
    {
        // 1024^2 texels on 1,500 px^2: log4(699) = 4.7 -> discard 4.
        Assert.Equal(TextureLod.DiscardLevelFor(1024, 1024, 1500f), TextureStreaming.TargetDiscard(1024, 1024, 3, 1500f));
        Assert.Equal(4, TextureStreaming.TargetDiscard(1024, 1024, 3, 1500f));
        Assert.Equal(5, TextureStreaming.TargetDiscard(1024, 1024, 3, 10f));
    }

    [Fact]
    public void Alpha_target_never_goes_below_64_px_on_the_long_side()
    {
        // BUG-RENDER-15: an alpha channel carries a silhouette, and 32 px of it has none left.
        Assert.Equal(4, TextureStreaming.TargetDiscard(1024, 1024, 4, 10f));   // 1024 >> 4 = 64
        Assert.Equal(1, TextureStreaming.TargetDiscard(128, 128, 4, 10f));     // 128 >> 1 = 64
        Assert.Equal(0, TextureStreaming.TargetDiscard(64, 64, 4, 10f));
        Assert.Equal(3, TextureStreaming.TargetDiscard(512, 128, 4, 10f));     // long side 512 >> 3 = 64
        // Opaque ones are not floored.
        Assert.Equal(5, TextureStreaming.TargetDiscard(128, 128, 3, 10f));
    }

    [Theory]
    [InlineData(600, 5)]       // below the discard-5 estimate is still only good for discard 5
    [InlineData(16792, 5)]
    [InlineData(49559, 5)]
    [InlineData(49560, 4)]
    [InlineData(180632, 3)]
    [InlineData(704920, 2)]
    public void Held_bytes_cover_the_finest_discard_whose_estimate_they_reach(int held, int discard)
    {
        Assert.Equal(discard, TextureStreaming.CoveredDiscard(1024, 1024, held));
    }

    [Fact]
    public void A_coarse_upload_is_built_for_a_quarter_of_its_texels_so_the_next_level_sharpens_it()
    {
        // 32x32 serves areas up to 1024 px^2; GpuCache sharpens at 4x the built-for area.
        Assert.Equal(256f, TextureStreaming.BuiltForArea(100_000f, 32, 32));
        // When the face is smaller than that, the request's own area stands.
        Assert.Equal(100f, TextureStreaming.BuiltForArea(100f, 32, 32));
        Assert.Equal(1f, TextureStreaming.BuiltForArea(100f, 1, 1));
    }

    [Fact]
    public void A_cut_that_fails_to_decode_is_retried_256_bytes_and_then_1_KB_shorter()
    {
        var asked = new System.Collections.Generic.List<int>();
        var good = new TextureData(4, 4, new byte[64]);

        // Fails at the cut point, decodes 256 B earlier.
        var rescued = TextureStreaming.DecodeWithRescue(new byte[5000], 3, (b, r) =>
        {
            asked.Add(b.Length);
            return b.Length == 5000 - 256 ? good : null;
        }, out bool wasRescued);
        Assert.Same(good, rescued);
        Assert.True(wasRescued);
        Assert.Equal(new[] { 5000, 4744 }, asked);

        asked.Clear();
        var none = TextureStreaming.DecodeWithRescue(new byte[5000], 3, (b, r) => { asked.Add(b.Length); return null; }, out wasRescued);
        Assert.Null(none);
        Assert.False(wasRescued);
        Assert.Equal(new[] { 5000, 4744, 3976 }, asked);

        var first = TextureStreaming.DecodeWithRescue(new byte[5000], 3, (b, r) => good, out wasRescued);
        Assert.Same(good, first);
        Assert.False(wasRescued);
    }
}
