using System.Numerics;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>BUG-AVATAR-18: a TextureEntry colour of exactly (0,0,0,0) is a real, fully transparent colour. It used to be
/// indistinguishable from <c>default(FaceTexture)</c> ("no colour set"), so a face a script hides that way drew its
/// texture white -- the Sensations add-on's one-glyph-per-face text showed as white digits on the chest.
/// The viewer draws nothing for a face whose colour alpha is 0 and that has no glow (llvovolume.cpp:5976-5981: only a
/// face with alpha &gt; 0 or glow joins the alpha pool).</summary>
public class FaceTextureColorTests
{
    private static readonly System.Guid Glyphs = new("43f2f82d-0000-0000-0000-000000000001");

    private static FaceTexture Face(Vector4 color) =>
        new(Glyphs, System.Guid.Empty, System.Guid.Empty, color, 1f, 1f, 0f, 0f, 0f);

    [Fact]
    public void A_wire_colour_of_zero_alpha_is_invisible_even_when_it_is_all_zeros()
    {
        Assert.True(Face(new Vector4(0f, 0f, 0f, 0f)).IsInvisible);
        Assert.True(Face(new Vector4(1f, 1f, 1f, 0f)).IsInvisible);
    }

    [Fact]
    public void Opaque_black_stays_visible()
    {
        var face = Face(new Vector4(0f, 0f, 0f, 1f));

        Assert.False(face.IsInvisible);
        Assert.Equal(new Vector4(0f, 0f, 0f, 1f), face.EffectiveColor);
    }

    [Fact]
    public void An_unset_colour_is_white_and_visible()
    {
        var unset = default(FaceTexture);

        Assert.False(unset.IsInvisible);
        Assert.Equal(Vector4.One, unset.EffectiveColor);
        Assert.False((unset with { TextureId = Glyphs }).IsInvisible);
    }

    [Fact]
    public void The_transparent_texture_is_invisible_whatever_the_colour()
    {
        Assert.True((default(FaceTexture) with { TextureId = FaceTexture.TransparentTextureId }).IsInvisible);
        Assert.True(Face(Vector4.One) with { TextureId = FaceTexture.TransparentTextureId } is { IsInvisible: true });
    }

    [Fact]
    public void Changing_the_colour_of_a_set_face_keeps_it_set()
    {
        var hidden = Face(Vector4.One) with { Color = new Vector4(0f, 0f, 0f, 0f) };

        Assert.True(hidden.IsInvisible);
    }

    [Fact]
    public void A_prim_built_without_a_colour_is_white_and_visible_but_an_explicit_zero_colour_is_not()
    {
        var plain = new SLNG.Core.Components.PrimitiveComponent(Vector3.One, profileCurve: 0);
        var hidden = new SLNG.Core.Components.PrimitiveComponent(Vector3.One, profileCurve: 0, colorTint: new Vector4(0f, 0f, 0f, 0f));

        Assert.False(plain.DefaultFace().IsInvisible);
        Assert.Equal(Vector4.One, plain.DefaultFace().EffectiveColor);
        Assert.True(hidden.DefaultFace().IsInvisible);
    }
}
