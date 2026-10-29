using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class GraphicsRecommendationTests
{
    private const long Gb = 1024;

    [Theory]
    [InlineData(24 * Gb, RecommendedGraphics.Ultra)]
    [InlineData(16376, RecommendedGraphics.Ultra)]
    [InlineData(12 * Gb, RecommendedGraphics.High)]
    [InlineData(8188, RecommendedGraphics.High)]
    [InlineData(6 * Gb, RecommendedGraphics.Medium)]
    [InlineData(4 * Gb, RecommendedGraphics.Low)]
    [InlineData(2 * Gb, RecommendedGraphics.Low)]
    public void Dedicated_video_memory_sets_the_preset_on_a_strong_machine(long vramMb, RecommendedGraphics expected)
    {
        Assert.Equal(expected, GraphicsRecommendation.Recommend(vramMb, false, 16, 32 * Gb));
    }

    [Fact]
    public void An_integrated_gpu_starts_on_low_whatever_it_reports()
    {
        Assert.Equal(RecommendedGraphics.Low, GraphicsRecommendation.Recommend(16 * Gb, true, 16, 32 * Gb));
    }

    [Fact]
    public void Unknown_video_memory_is_medium()
    {
        Assert.Equal(RecommendedGraphics.Medium, GraphicsRecommendation.Recommend(0, false, 16, 32 * Gb));
    }

    [Theory]
    [InlineData(3, 32 * Gb)]
    [InlineData(16, 6 * Gb)]
    public void A_weak_cpu_or_little_ram_caps_at_medium(int cores, long ramMb)
    {
        Assert.Equal(RecommendedGraphics.Medium, GraphicsRecommendation.Recommend(24 * Gb, false, cores, ramMb));
    }

    [Theory]
    [InlineData(4, 32 * Gb)]
    [InlineData(5, 32 * Gb)]
    [InlineData(16, 8 * Gb)]
    public void A_modest_cpu_or_ram_caps_at_high(int cores, long ramMb)
    {
        Assert.Equal(RecommendedGraphics.High, GraphicsRecommendation.Recommend(24 * Gb, false, cores, ramMb));
    }

    [Fact]
    public void Caps_never_raise_a_low_result()
    {
        Assert.Equal(RecommendedGraphics.Low, GraphicsRecommendation.Recommend(2 * Gb, false, 3, 4 * Gb));
    }

    [Fact]
    public void Unreadable_cpu_and_ram_do_not_cap()
    {
        Assert.Equal(RecommendedGraphics.Ultra, GraphicsRecommendation.Recommend(24 * Gb, false, 0, -1));
    }
}
