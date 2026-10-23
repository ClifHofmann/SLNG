using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>BUG-RENDER-50: a legacy material's diffuse alpha mode overrides the texture's alpha for a worn
/// face too (pipeline.cpp:1801-1814, llvovolume.cpp:6766-6790).</summary>
public class LegacyMaterialAlphaTests
{
    [Theory]
    [InlineData(LegacyDiffuseAlphaMode.None)]
    [InlineData(LegacyDiffuseAlphaMode.Emissive)]
    public void None_and_emissive_ignore_the_texture_alpha(LegacyDiffuseAlphaMode mode)
        => Assert.Equal(LegacyAlphaRoute.Opaque, LegacyMaterialAlpha.Route(mode, 0, tintTranslucent: false).Route);

    [Fact]
    public void Mask_uses_the_material_cutoff_out_of_255_less_half_a_step()
    {
        var (route, cutoff) = LegacyMaterialAlpha.Route(LegacyDiffuseAlphaMode.Mask, 128, tintTranslucent: false);
        Assert.Equal(LegacyAlphaRoute.Mask, route);
        Assert.Equal(128f / 255f - 1f / 512f, cutoff, 6);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(128)]
    [InlineData(255)]
    public void A_texel_exactly_at_the_cutoff_is_kept_and_one_step_below_is_cut(byte cutoff)
    {
        // materialF.glsl:257-260: discard if alpha < minimum_alpha - 1/512.
        float threshold = LegacyMaterialAlpha.MaskThreshold(cutoff);
        Assert.False(cutoff / 255f < threshold);
        Assert.True((cutoff - 1) / 255f < threshold);
    }

    [Fact]
    public void Cutoff_zero_discards_nothing()
        => Assert.Equal(0f, LegacyMaterialAlpha.MaskThreshold(0));

    [Fact]
    public void Blend_blends()
        => Assert.Equal(LegacyAlphaRoute.Blend, LegacyMaterialAlpha.Route(LegacyDiffuseAlphaMode.Blend, 0, false).Route);

    [Theory]
    [InlineData(LegacyDiffuseAlphaMode.None)]
    [InlineData(LegacyDiffuseAlphaMode.Emissive)]
    [InlineData(LegacyDiffuseAlphaMode.Mask)]
    public void A_translucent_face_colour_still_blends(LegacyDiffuseAlphaMode mode)
        => Assert.Equal(LegacyAlphaRoute.Blend, LegacyMaterialAlpha.Route(mode, 200, tintTranslucent: true).Route);

    [Fact]
    public void Default_leaves_it_to_the_texture()
        => Assert.Equal(LegacyAlphaRoute.Pixels, LegacyMaterialAlpha.Route(LegacyDiffuseAlphaMode.Default, 0, false).Route);
}
