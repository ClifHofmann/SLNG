using System;
using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>FEAT-PERF-20: the material key. A key that called two different materials equal
/// would hand one face another face's look, so the tests that matter are "every input that
/// reaches a uniform separates two keys"; the normalisation tests guard the other direction --
/// a value with no effect on the pixels must not split a group.</summary>
public class PrimMaterialKeyTests
{
    private static readonly Guid TexA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TexB = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Vector3 Scale = new(1f, 2f, 3f);

    private static FaceTexture Face(
        Guid? texture = null, Guid? renderMaterial = null, Guid? legacyMaterial = null,
        Vector4? color = null, float repeatU = 1f, float repeatV = 1f,
        float offsetU = 0f, float offsetV = 0f, float rotation = 0f,
        byte texGen = 0, bool fullbright = false, byte shiny = 0, bool hasMedia = false,
        GltfMaterialOverride? pbrOverride = null) =>
        new(texture ?? TexA, renderMaterial ?? Guid.Empty, legacyMaterial ?? Guid.Empty,
            color ?? new Vector4(1, 1, 1, 1), repeatU, repeatV, offsetU, offsetV, rotation,
            texGen, fullbright, shiny, hasMedia, pbrOverride);

    private static PrimMaterialKey Key(FaceTexture face, Vector3? scale = null, ulong variant = 1, Vector4 texAnimA = default, Vector4 texAnimB = default) =>
        PrimMaterialKey.Create(face, scale ?? Scale, variant, texAnimA, texAnimB);

    [Fact]
    public void EqualInputs_GiveEqualKeys_AndEqualHashes()
    {
        var a = Key(Face(color: new Vector4(0.5f, 0.25f, 1f, 0.75f), repeatU: 4f, offsetV: 0.1f, shiny: 2));
        var b = Key(Face(color: new Vector4(0.5f, 0.25f, 1f, 0.75f), repeatU: 4f, offsetV: 0.1f, shiny: 2));

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Single(new HashSet<PrimMaterialKey> { a, b });
    }

    public static IEnumerable<object[]> OneInputChanged()
    {
        yield return new object[] { "texture", Face(texture: TexB) };
        yield return new object[] { "PBR material", Face(renderMaterial: TexB) };
        yield return new object[] { "legacy material", Face(legacyMaterial: TexB) };
        yield return new object[] { "tint red", Face(color: new Vector4(0.9f, 1, 1, 1)) };
        yield return new object[] { "tint green", Face(color: new Vector4(1, 0.9f, 1, 1)) };
        yield return new object[] { "tint blue", Face(color: new Vector4(1, 1, 0.9f, 1)) };
        yield return new object[] { "tint alpha", Face(color: new Vector4(1, 1, 1, 0.5f)) };
        yield return new object[] { "repeat U", Face(repeatU: 2f) };
        yield return new object[] { "repeat V", Face(repeatV: 2f) };
        yield return new object[] { "offset U", Face(offsetU: 0.25f) };
        yield return new object[] { "offset V", Face(offsetV: 0.25f) };
        yield return new object[] { "rotation", Face(rotation: 1.5f) };
        yield return new object[] { "planar mapping", Face(texGen: FaceTexture.TexGenPlanar) };
        yield return new object[] { "fullbright", Face(fullbright: true) };
        yield return new object[] { "shiny", Face(shiny: 3) };
        yield return new object[] { "media", Face(hasMedia: true) };
        yield return new object[] { "pbr override", Face(pbrOverride: new GltfMaterialOverride { Roughness = 0.5f }) };
    }

    [Theory]
    [MemberData(nameof(OneInputChanged))]
    public void AnyInputThatReachesAUniform_SeparatesTheKeys(string what, FaceTexture changed)
    {
        Assert.NotEqual(Key(Face()), Key(changed));
        Assert.False(Key(Face()) == Key(changed), what);
    }

    [Fact]
    public void DifferentVariant_SeparatesTheKeys()
    {
        Assert.NotEqual(Key(Face(), variant: 1), Key(Face(), variant: 2));
    }

    [Fact]
    public void PrimScale_IsIgnoredWithoutPlanarMapping()
    {
        // prim_scale is only sampled by the planar projection; two props of slightly different
        // size must still share a material (FEAT-PERF-06 measured what splitting them costs).
        Assert.Equal(Key(Face(), new Vector3(1, 1, 1)), Key(Face(), new Vector3(1.01f, 3, 0.5f)));
    }

    [Fact]
    public void PrimScale_SeparatesPlanarFaces()
    {
        var planar = Face(texGen: FaceTexture.TexGenPlanar);
        Assert.NotEqual(Key(planar, new Vector3(1, 1, 1)), Key(planar, new Vector3(1, 1, 2)));
        Assert.Equal(Key(planar, new Vector3(1, 1, 2)), Key(planar, new Vector3(1, 1, 2)));
    }

    [Theory]
    [InlineData((byte)4)]   // spherical
    [InlineData((byte)6)]   // cylindrical
    public void UnimplementedTexGen_KeysLikeDefault(byte texGen)
    {
        // Spherical and cylindrical fall back to the mesh's own UVs (uv_texgen = 0), so they draw
        // exactly like the default mapping -- and, like it, ignore the prim scale.
        Assert.Equal(Key(Face()), Key(Face(texGen: texGen), new Vector3(9, 9, 9)));
    }

    [Fact]
    public void ShinyOutOfRange_KeysLikeNone()
    {
        Assert.Equal(Key(Face(shiny: 0)), Key(Face(shiny: 7)));
        Assert.NotEqual(Key(Face(shiny: 0)), Key(Face(shiny: 1)));
    }

    [Fact]
    public void NegativeZero_KeysLikeZero()
    {
        var a = Key(Face(offsetU: 0f, rotation: 0f));
        var b = Key(Face(offsetU: -0f, rotation: -0f));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void TexAnim_SeparatesTheKeys()
    {
        var baseKey = Key(Face());
        var animA = Key(Face(), texAnimA: new Vector4(2, 4, 4, 0));
        var animB = Key(Face(), texAnimB: new Vector4(16, 10, 0, 1));
        Assert.NotEqual(baseKey, animA);
        Assert.NotEqual(baseKey, animB);
        Assert.NotEqual(animA, animB);
    }
}
