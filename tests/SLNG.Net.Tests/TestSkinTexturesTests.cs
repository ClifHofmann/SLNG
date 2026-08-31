using LibreMetaverse;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-AVATAR-01: checks that the generated test skin really is a known answer.
///
/// <para>Its whole value is that a wrong bake cannot look plausible — so the properties it relies on
/// have to actually hold. A texture that is uniform, symmetric top-to-bottom, or the same colour as
/// another channel would let exactly the defects this task spent a day on slip through again.</para>
/// </summary>
public class TestSkinTexturesTests
{
    private const int Size = TestSkinTextures.Size;

    private static (byte B, byte G, byte R, byte A) Pixel(byte[] px, int x, int y)
    {
        int i = (y * Size + x) * 4;
        return (px[i], px[i + 1], px[i + 2], px[i + 3]);
    }

    [Theory]
    [InlineData(AvatarTextureIndex.HeadBodypaint)]
    [InlineData(AvatarTextureIndex.UpperBodypaint)]
    [InlineData(AvatarTextureIndex.LowerBodypaint)]
    public void Texture_is_bake_sized_and_fully_opaque(AvatarTextureIndex slot)
    {
        var px = TestSkinTextures.Build(slot);

        Assert.Equal(Size * Size * 4, px.Length);
        for (int i = 3; i < px.Length; i += 4 * 997) Assert.Equal(255, px[i]);
    }

    /// <summary>The three channels must be unmistakably different, so a layer landing in the wrong
    /// channel reads as a wrong colour rather than as a lighting difference.</summary>
    [Fact]
    public void Each_channel_has_its_own_colour()
    {
        var head = TestSkinTextures.BaseColor(AvatarTextureIndex.HeadBodypaint);
        var upper = TestSkinTextures.BaseColor(AvatarTextureIndex.UpperBodypaint);
        var lower = TestSkinTextures.BaseColor(AvatarTextureIndex.LowerBodypaint);

        Assert.True(head.G > head.B + 100 && head.G > head.R + 100, "head should read as green");
        Assert.True(upper.B > upper.G + 100 && upper.B > upper.R + 100, "upper body should read as blue");
        Assert.True(lower.R > lower.B + 100 && lower.R > lower.G + 100, "lower body should read as red");
    }

    /// <summary>THE POINT. A vertical flip is the defect that hid longest in the head bake, and a
    /// symmetric texture cannot reveal one — so the top has to be visibly brighter than the bottom.</summary>
    [Fact]
    public void Texture_is_brighter_at_the_top_so_a_flip_shows()
    {
        var px = TestSkinTextures.Build(AvatarTextureIndex.UpperBodypaint);

        // Sample a column clear of the grid lines, the diagonal and the corner markers.
        var top = Pixel(px, 301, 200);
        var bottom = Pixel(px, 301, Size - 200);

        Assert.True(top.B > bottom.B + 40,
            $"top should be clearly brighter than bottom, got {top.B} vs {bottom.B}");
    }

    /// <summary>A grid at a known spacing: scaling changes it and shearing bends it. Both were real
    /// defects here, and both are invisible on a photographic skin.</summary>
    [Fact]
    public void Grid_lines_sit_every_64_pixels()
    {
        var px = TestSkinTextures.Build(AvatarTextureIndex.LowerBodypaint);

        Assert.True(Pixel(px, 320, 300).R < 40, "expected a dark grid line at x=320");
        Assert.False(Pixel(px, 333, 300).R < 40, "did not expect a grid line at x=333");
    }

    /// <summary>Differently sized corner markers, so orientation is never ambiguous — a rotation or
    /// mirror shows up as the big square being in the wrong corner.</summary>
    [Fact]
    public void Corners_are_marked_with_different_sizes()
    {
        var px = TestSkinTextures.Build(AvatarTextureIndex.HeadBodypaint);

        Assert.Equal(255, Pixel(px, 130, 130).R);          // top-left marker is the largest
        Assert.NotEqual(255, Pixel(px, 130, 130 + 60).R);  // …and does not reach further down
        Assert.Equal(255, Pixel(px, Size - 10, Size - 10).R); // bottom-right is the smallest
        Assert.NotEqual(255, Pixel(px, Size - 50, Size - 10).R);
    }

    /// <summary>The diagonal survives no row-stride or row-order mistake — the shape the broken head
    /// bake took, where 512 source pixels were laid across 1024-pixel rows.</summary>
    [Fact]
    public void A_diagonal_runs_corner_to_corner()
    {
        var px = TestSkinTextures.Build(AvatarTextureIndex.HeadBodypaint);

        Assert.Equal(245, Pixel(px, 500, 500).R);
        Assert.NotEqual(245, Pixel(px, 500, 540).R);
    }
}
