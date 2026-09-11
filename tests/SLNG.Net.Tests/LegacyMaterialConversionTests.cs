using LibreMetaverse;
using LibreMetaverse.Materials;
using LibreMetaverse.StructuredData;
using SLNG.Core;
using SLNG.Net;
using Xunit;
using Vector2 = System.Numerics.Vector2;

namespace SLNG.Net.Tests;

/// <summary>
/// Conversion of LibreMetaverse's <c>LegacyMaterial</c> to the neutral
/// <see cref="LegacyMaterialData"/> at the SLNG.Net boundary (AGENTS.md: no LibreMetaverse type
/// crosses a public boundary).
///
/// <para>The inputs are built the way the wire delivers them -- an OSD map with the capability's
/// own field names and every float as an integer scaled by 10000 (llmaterial.cpp:35-61) -- rather
/// than by setting the properties directly, so the test also pins that our understanding of the
/// encoding matches LibreMetaverse's decoding.</para>
/// </summary>
public class LegacyMaterialConversionTests
{
    private const float Tol = 1e-4f;
    private const double Mul = 10000.0;

    private static OSDMap WireMaterial(
        UUID id,
        UUID normal = default, UUID specular = default,
        double normOffsetX = 0, double normOffsetY = 0,
        double normRepeatX = 1, double normRepeatY = 1, double normRotation = 0,
        double specOffsetX = 0, double specOffsetY = 0,
        double specRepeatX = 1, double specRepeatY = 1, double specRotation = 0,
        int specExp = 51, int envIntensity = 0, int alphaCutoff = 0, int alphaMode = 1)
    {
        var material = new OSDMap
        {
            ["NormMap"] = OSD.FromUUID(normal),
            ["NormOffsetX"] = OSD.FromInteger((int)(normOffsetX * Mul)),
            ["NormOffsetY"] = OSD.FromInteger((int)(normOffsetY * Mul)),
            ["NormRepeatX"] = OSD.FromInteger((int)(normRepeatX * Mul)),
            ["NormRepeatY"] = OSD.FromInteger((int)(normRepeatY * Mul)),
            ["NormRotation"] = OSD.FromInteger((int)(normRotation * Mul)),
            ["SpecMap"] = OSD.FromUUID(specular),
            ["SpecOffsetX"] = OSD.FromInteger((int)(specOffsetX * Mul)),
            ["SpecOffsetY"] = OSD.FromInteger((int)(specOffsetY * Mul)),
            ["SpecRepeatX"] = OSD.FromInteger((int)(specRepeatX * Mul)),
            ["SpecRepeatY"] = OSD.FromInteger((int)(specRepeatY * Mul)),
            ["SpecRotation"] = OSD.FromInteger((int)(specRotation * Mul)),
            ["SpecColor"] = new OSDArray { OSD.FromInteger(255), OSD.FromInteger(255), OSD.FromInteger(255), OSD.FromInteger(255) },
            ["SpecExp"] = OSD.FromInteger(specExp),
            ["EnvIntensity"] = OSD.FromInteger(envIntensity),
            ["AlphaMaskCutoff"] = OSD.FromInteger(alphaCutoff),
            ["DiffuseAlphaMode"] = OSD.FromInteger(alphaMode),
        };
        return new OSDMap { ["ID"] = OSD.FromUUID(id), ["Material"] = material };
    }

    [Fact]
    public void CarriesBothMapsAndTheirOwnPlacement()
    {
        var id = new UUID("88fe3b1a-b688-4da0-8730-de7cd1194a6c");
        var normal = new UUID("11111111-2222-3333-4444-555555555555");
        var specular = new UUID("66666666-7777-8888-9999-aaaaaaaaaaaa");

        var wire = new LegacyMaterial(WireMaterial(id, normal, specular,
            normRepeatX: 4, normRepeatY: 2, normOffsetX: 0.25, normRotation: 1.5,
            specRepeatX: 3, specRepeatY: 3, specOffsetY: -0.5, specRotation: -0.75));

        var data = GridSession.ToLegacyMaterialData(wire);

        Assert.Equal(id.Guid, data.Id);
        Assert.Equal(normal.Guid, data.NormalMap);
        Assert.Equal(specular.Guid, data.SpecularMap);

        // The maps have placement independent of the diffuse texture AND of each other, which is
        // the reason they cannot be folded into the face's own UV transform.
        Assert.Equal(new Vector2(4f, 2f), data.NormalRepeat);
        Assert.Equal(0.25f, data.NormalOffset.X, Tol);
        Assert.Equal(1.5f, data.NormalRotation, Tol);
        Assert.Equal(new Vector2(3f, 3f), data.SpecularRepeat);
        Assert.Equal(-0.5f, data.SpecularOffset.Y, Tol);
        Assert.Equal(-0.75f, data.SpecularRotation, Tol);
    }

    [Fact]
    public void ReadsGlossinessEnvironmentAndAlphaMode()
    {
        // 51 is the viewer's own default glossiness (DEFAULT_SPECULAR_LIGHT_EXPONENT = 0.2*255,
        // llmaterial.h) and the value Firestorm reported for the reef rock that motivated this.
        var wire = new LegacyMaterial(WireMaterial(UUID.Random(),
            specExp: 51, envIntensity: 33, alphaCutoff: 128, alphaMode: 2));

        var data = GridSession.ToLegacyMaterialData(wire);

        Assert.Equal(51, data.SpecularExponent);
        Assert.Equal(33, data.EnvironmentIntensity);
        Assert.Equal(128, data.AlphaMaskCutoff);
        Assert.Equal(LegacyDiffuseAlphaMode.Mask, data.DiffuseAlphaMode);
    }

    [Fact]
    public void AMaterialWithoutMapsIsReportedAsCarryingNone()
    {
        // A material id can resolve to a record with no maps at all -- it still carries alpha mode
        // and specular colour, but nothing that needs a texture fetch, and the renderer has to be
        // able to tell those apart without loading anything.
        var bare = GridSession.ToLegacyMaterialData(new LegacyMaterial(WireMaterial(UUID.Random())));
        var withNormal = GridSession.ToLegacyMaterialData(new LegacyMaterial(
            WireMaterial(UUID.Random(), normal: UUID.Random())));

        Assert.False(bare.HasMaps);
        Assert.True(withNormal.HasMaps);
    }

    [Fact]
    public void SpecularColourIsNormalisedToZeroOne()
    {
        // The wire carries the specular tint as 0-255 per channel; everything downstream of this
        // boundary works in 0-1, like every other colour in SLNG.Core.
        var data = GridSession.ToLegacyMaterialData(new LegacyMaterial(WireMaterial(UUID.Random())));

        Assert.Equal(1f, data.SpecularColor.X, Tol);
        Assert.Equal(1f, data.SpecularColor.W, Tol);
    }

    [Theory]
    [InlineData(Shininess.None, (byte)0, 0.00f)]
    [InlineData(Shininess.Low, (byte)1, 0.25f)]
    [InlineData(Shininess.Medium, (byte)2, 0.50f)]
    [InlineData(Shininess.High, (byte)3, 0.75f)]
    public void ShininessIsShiftedOutOfItsProtocolBits(Shininess wire, byte expected, float glossiness)
    {
        // LibreMetaverse's enum holds the value still packed in the top two bits of the
        // bump/shiny byte -- Low is 0x40, not 1 -- while the viewer reads it as `mBump >> 6` and
        // FaceTexture.Shiny is documented as that 0-3 value. A plain cast therefore fed 64, 128
        // and 192 into a 0-3 lookup, every level missed, and FEAT-RENDER-19 shipped inert: no
        // highlight and no environment reflection anywhere. The shift is the whole fix, and a
        // wrong one fails silently as "matte", so it is pinned here rather than left to review.
        Assert.Equal(expected, (byte)((byte)wire >> 6));

        var face = new FaceTexture(
            System.Guid.Empty, System.Guid.Empty, System.Guid.Empty,
            new System.Numerics.Vector4(1, 1, 1, 1), 1f, 1f, 0f, 0f, 0f,
            FaceTexture.TexGenDefault, false, expected);
        Assert.Equal(glossiness, face.ShinyGlossiness, Tol);
    }
}
