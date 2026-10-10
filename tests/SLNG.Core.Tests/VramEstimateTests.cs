using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// FEAT-PERF-25: the render-buffer estimates are an order-of-magnitude guide printed beside the
/// measured totals, so these tests pin the relationships that make them trustworthy (more MSAA is
/// more memory, full-resolution effects cost more than half, the documented ~270 MB / ~30 MB reflection
/// atlas sizes) rather than every constant.
/// </summary>
public class VramEstimateTests
{
    private const long MiB = 1L << 20;
    private const int W = 1920;
    private const int H = 1080;

    private static long Buffers(
        int msaa = 0, bool ssao = false, bool ssaoHalf = false, bool ssil = false, bool ssilHalf = false,
        bool ssr = false, bool glow = false, int width = W, int height = H)
        => VramEstimate.RenderBuffers(width, height, msaa, ssao, ssaoHalf, ssil, ssilHalf, ssr, glow);

    // ------------------------------------------------------------ empty sizes

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(0, 0)]
    [InlineData(-1920, 1080)]
    [InlineData(1920, -1080)]
    public void Render_buffers_of_an_empty_or_negative_viewport_cost_nothing(int width, int height)
    {
        Assert.Equal(0, Buffers(msaa: 3, ssao: true, ssil: true, ssr: true, glow: true, width: width, height: height));
        Assert.Equal(0, VramEstimate.MsaaBuffers(width, height, 2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4096)]
    public void A_shadow_atlas_of_no_size_costs_nothing(int size)
    {
        Assert.Equal(0, VramEstimate.ShadowAtlas(size));
        Assert.Equal(0, VramEstimate.ShadowAtlas(size, sixteenBits: false));
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(-256, 4)]
    [InlineData(256, 0)]
    [InlineData(256, -1)]
    public void A_reflection_atlas_with_no_size_or_no_probes_costs_nothing(int size, int count)
        => Assert.Equal(0, VramEstimate.ReflectionAtlas(size, count));

    // ------------------------------------------------------------------- MSAA

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    public void MsaaSamples_maps_the_viewport_value_to_a_sample_count(int msaa, int expected)
        => Assert.Equal(expected, VramEstimate.MsaaSamples(msaa));

    [Fact]
    public void Render_buffers_grow_with_every_msaa_step()
    {
        long off = Buffers(msaa: 0);
        long x2 = Buffers(msaa: 1);
        long x4 = Buffers(msaa: 2);
        long x8 = Buffers(msaa: 3);

        Assert.True(off > 0, "even without MSAA a viewport has colour, depth and normal buffers");
        Assert.True(off < x2, $"off {off} should be below 2x {x2}");
        Assert.True(x2 < x4, $"2x {x2} should be below 4x {x4}");
        Assert.True(x4 < x8, $"4x {x4} should be below 8x {x8}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void The_msaa_share_is_exactly_what_msaa_adds_to_the_render_buffers(int msaa)
    {
        long extra = Buffers(msaa: msaa) - Buffers(msaa: 0);

        Assert.Equal(extra, VramEstimate.MsaaBuffers(W, H, msaa));
        Assert.True(extra > 0);
    }

    [Fact]
    public void Msaa_off_adds_no_msaa_buffers()
        => Assert.Equal(0, VramEstimate.MsaaBuffers(W, H, 0));

    [Fact]
    public void Msaa_buffers_scale_with_samples_and_with_pixel_count()
    {
        long x2 = VramEstimate.MsaaBuffers(W, H, 1);
        long x4 = VramEstimate.MsaaBuffers(W, H, 2);
        long x8 = VramEstimate.MsaaBuffers(W, H, 3);

        Assert.Equal(2 * x2, x4);
        Assert.Equal(2 * x4, x8);

        // 4K is exactly four times the pixels of 1080p.
        Assert.Equal(4 * x4, VramEstimate.MsaaBuffers(3840, 2160, 2));
    }

    [Fact]
    public void Msaa_4x_at_1080p_is_a_large_share_of_a_small_cards_budget()
    {
        // The reason FEAT-PERF-25 caps MSAA: this is the price of 4x on a 1080p window, paid at every
        // distance regardless of what the texture cache holds.
        long x4 = VramEstimate.MsaaBuffers(W, H, 2);

        Assert.InRange(x4, 64 * MiB, 512 * MiB);
    }

    // ---------------------------------------------------------------- effects

    [Fact]
    public void Render_buffers_scale_with_pixel_count()
        => Assert.Equal(4 * Buffers(msaa: 2, ssao: true), Buffers(msaa: 2, ssao: true, width: 3840, height: 2160));

    [Fact]
    public void Ssil_at_full_resolution_costs_more_than_at_half_which_costs_more_than_off()
    {
        long off = Buffers(ssil: false);
        long half = Buffers(ssil: true, ssilHalf: true);
        long full = Buffers(ssil: true, ssilHalf: false);

        Assert.True(off < half, $"off {off} should be below half {half}");
        Assert.True(half < full, $"half {half} should be below full {full}");
    }

    [Fact]
    public void The_ssil_half_flag_is_ignored_while_ssil_is_off()
        => Assert.Equal(Buffers(ssil: false, ssilHalf: false), Buffers(ssil: false, ssilHalf: true));

    [Fact]
    public void Ssao_at_full_resolution_costs_more_than_at_half_which_costs_more_than_off()
    {
        long off = Buffers(ssao: false);
        long half = Buffers(ssao: true, ssaoHalf: true);
        long full = Buffers(ssao: true, ssaoHalf: false);

        Assert.True(off < half, $"off {off} should be below half {half}");
        Assert.True(half < full, $"half {half} should be below full {full}");
    }

    [Fact]
    public void The_ssao_half_flag_is_ignored_while_ssao_is_off()
        => Assert.Equal(Buffers(ssao: false, ssaoHalf: false), Buffers(ssao: false, ssaoHalf: true));

    [Fact]
    public void Ssr_and_glow_each_add_to_the_render_buffers()
    {
        long none = Buffers();

        Assert.True(Buffers(ssr: true) > none);
        Assert.True(Buffers(glow: true) > none);
        Assert.True(Buffers(ssr: true, glow: true) > Buffers(ssr: true));
        Assert.True(Buffers(ssr: true, glow: true) > Buffers(glow: true));
    }

    [Fact]
    public void Every_effect_together_costs_the_sum_of_what_each_adds_alone()
    {
        long none = Buffers();
        long parts = (Buffers(msaa: 2) - none)
                   + (Buffers(ssao: true, ssaoHalf: false) - none)
                   + (Buffers(ssil: true, ssilHalf: false) - none)
                   + (Buffers(ssr: true) - none)
                   + (Buffers(glow: true) - none);

        long all = Buffers(msaa: 2, ssao: true, ssaoHalf: false, ssil: true, ssilHalf: false, ssr: true, glow: true);

        Assert.Equal(none + parts, all);
    }

    // ------------------------------------------------------------ shadow atlas

    [Fact]
    public void A_4096_sixteen_bit_shadow_atlas_is_32_MiB()
        => Assert.Equal(32 * MiB, VramEstimate.ShadowAtlas(4096));

    [Fact]
    public void A_4096_thirty_two_bit_shadow_atlas_is_64_MiB()
        => Assert.Equal(64 * MiB, VramEstimate.ShadowAtlas(4096, sixteenBits: false));

    [Fact]
    public void Halving_the_shadow_atlas_side_quarters_its_cost()
    {
        // The cap from 4096 to 2048 (LowVramPolicy.MaxShadowAtlas) frees three quarters of it.
        Assert.Equal(8 * MiB, VramEstimate.ShadowAtlas(2048));
        Assert.Equal(VramEstimate.ShadowAtlas(4096) / 4, VramEstimate.ShadowAtlas(2048));
    }

    // -------------------------------------------------------- reflection atlas

    [Fact]
    public void A_1024_reflection_atlas_of_four_probes_is_hundreds_of_MiB()
        => Assert.True(VramEstimate.ReflectionAtlas(1024, 4) > 200 * MiB);

    [Fact]
    public void A_256_reflection_atlas_of_four_probes_is_tens_of_MiB()
        => Assert.True(VramEstimate.ReflectionAtlas(256, 4) < 40 * MiB);

    [Fact]
    public void Capping_the_reflection_atlas_to_256_frees_more_than_200_MiB()
        => Assert.True(VramEstimate.ReflectionAtlas(1024, 4) - VramEstimate.ReflectionAtlas(256, 4) > 200 * MiB);

    [Fact]
    public void More_probes_cost_more()
        => Assert.True(VramEstimate.ReflectionAtlas(256, 8) > VramEstimate.ReflectionAtlas(256, 4));

    [Fact]
    public void A_larger_probe_resolution_costs_more()
        => Assert.True(VramEstimate.ReflectionAtlas(512, 4) > VramEstimate.ReflectionAtlas(256, 4));

    [Fact]
    public void Fewer_mip_levels_cost_less()
        => Assert.True(VramEstimate.ReflectionAtlas(256, 4, mipmaps: 4) < VramEstimate.ReflectionAtlas(256, 4, mipmaps: 8));
}
