using System;
using System.Numerics;
using SLNG.Assets;
using SLNG.Core;
using Xunit;

namespace SLNG.Assets.Tests;

public class PbrOverrideMergeTests
{
    private static readonly PbrMaterialData BaseMat = new(
        BaseColorTextureId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        NormalTextureId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
        MetallicRoughnessTextureId: Guid.Parse("33333333-3333-3333-3333-333333333333"),
        EmissiveTextureId: Guid.Parse("44444444-4444-4444-4444-444444444444"),
        BaseColorFactor: new Vector4(0.2f, 0.4f, 0.6f, 1f),
        MetallicFactor: 0.1f,
        RoughnessFactor: 0.9f,
        EmissiveFactor: new Vector3(0.1f, 0.2f, 0.3f),
        AlphaMode: PbrAlphaMode.Mask,
        AlphaCutoff: 0.5f,
        DoubleSided: true
    );

    [Fact]
    public void Null_Or_Empty_Override_Returns_Same_Instance()
    {
        var res1 = PbrOverrideMerge.Apply(BaseMat, null);
        Assert.Same(BaseMat, res1);

        var res2 = PbrOverrideMerge.Apply(BaseMat, new GltfMaterialOverride());
        Assert.Same(BaseMat, res2);
    }

    [Fact]
    public void Member_BaseColorTexture_Alone()
    {
        var newId = Guid.NewGuid();
        var res = PbrOverrideMerge.Apply(BaseMat, new GltfMaterialOverride { BaseColorTexture = newId });
        Assert.Equal(newId, res.BaseColorTextureId);
        Assert.Equal(BaseMat.NormalTextureId, res.NormalTextureId);
        Assert.Equal(BaseMat.MetallicRoughnessTextureId, res.MetallicRoughnessTextureId);
        Assert.Equal(BaseMat.EmissiveTextureId, res.EmissiveTextureId);
        Assert.Equal(BaseMat.BaseColorFactor, res.BaseColorFactor);
        Assert.Equal(BaseMat.MetallicFactor, res.MetallicFactor);
        Assert.Equal(BaseMat.RoughnessFactor, res.RoughnessFactor);
        Assert.Equal(BaseMat.EmissiveFactor, res.EmissiveFactor);
        Assert.Equal(BaseMat.AlphaMode, res.AlphaMode);
        Assert.Equal(BaseMat.AlphaCutoff, res.AlphaCutoff);
        Assert.Equal(BaseMat.DoubleSided, res.DoubleSided);
    }

    [Fact]
    public void Member_NormalTexture_Alone()
    {
        var newId = Guid.NewGuid();
        var res = PbrOverrideMerge.Apply(BaseMat, new GltfMaterialOverride { NormalTexture = newId });
        Assert.Equal(newId, res.NormalTextureId);
        Assert.Equal(BaseMat.BaseColorTextureId, res.BaseColorTextureId);
    }

    [Fact]
    public void Member_MetallicRoughnessTexture_Alone()
    {
        var newId = Guid.NewGuid();
        var res = PbrOverrideMerge.Apply(BaseMat, new GltfMaterialOverride { MetallicRoughnessTexture = newId });
        Assert.Equal(newId, res.MetallicRoughnessTextureId);
        Assert.Equal(BaseMat.BaseColorTextureId, res.BaseColorTextureId);
    }

    [Fact]
    public void Member_EmissiveTexture_Alone()
    {
        var newId = Guid.NewGuid();
        var res = PbrOverrideMerge.Apply(BaseMat, new GltfMaterialOverride { EmissiveTexture = newId });
        Assert.Equal(newId, res.EmissiveTextureId);
        Assert.Equal(BaseMat.BaseColorTextureId, res.BaseColorTextureId);
    }

    [Fact]
    public void Member_BaseColor_Alone()
    {
        var res = PbrOverrideMerge.Apply(BaseMat, new GltfMaterialOverride { BaseColor = new Vector4(0.8f, 0.1f, 0.2f, 0.9f) });
        Assert.Equal(new Vector4(0.8f, 0.1f, 0.2f, 0.9f), res.BaseColorFactor);
        Assert.Equal(BaseMat.RoughnessFactor, res.RoughnessFactor);
    }

    [Fact]
    public void Member_EmissiveColor_Alone()
    {
        var res = PbrOverrideMerge.Apply(BaseMat, new GltfMaterialOverride { EmissiveColor = new Vector3(0.5f, 0.5f, 0.5f) });
        Assert.Equal(new Vector3(0.5f, 0.5f, 0.5f), res.EmissiveFactor);
        Assert.Equal(BaseMat.BaseColorFactor, res.BaseColorFactor);
    }

    [Fact]
    public void Member_Metallic_Alone()
    {
        var res = PbrOverrideMerge.Apply(BaseMat, new GltfMaterialOverride { Metallic = 0.85f });
        Assert.Equal(0.85f, res.MetallicFactor);
        Assert.Equal(BaseMat.RoughnessFactor, res.RoughnessFactor);
    }

    [Fact]
    public void Member_Roughness_Alone()
    {
        var res = PbrOverrideMerge.Apply(BaseMat, new GltfMaterialOverride { Roughness = 0.25f });
        Assert.Equal(0.25f, res.RoughnessFactor);
        Assert.Equal(BaseMat.MetallicFactor, res.MetallicFactor);
    }

    [Fact]
    public void Member_AlphaMode_Alone()
    {
        var res = PbrOverrideMerge.Apply(BaseMat, new GltfMaterialOverride { AlphaMode = GltfAlphaMode.Blend });
        Assert.Equal(PbrAlphaMode.Blend, res.AlphaMode);
        Assert.Equal(BaseMat.AlphaCutoff, res.AlphaCutoff);
    }

    [Fact]
    public void Member_AlphaCutoff_Alone()
    {
        var res = PbrOverrideMerge.Apply(BaseMat, new GltfMaterialOverride { AlphaCutoff = 0.75f });
        Assert.Equal(0.75f, res.AlphaCutoff);
        Assert.Equal(BaseMat.AlphaMode, res.AlphaMode);
    }

    [Fact]
    public void Member_DoubleSided_Alone()
    {
        var res = PbrOverrideMerge.Apply(BaseMat, new GltfMaterialOverride { DoubleSided = false });
        Assert.False(res.DoubleSided);
        Assert.Equal(BaseMat.AlphaMode, res.AlphaMode);
    }

    [Fact]
    public void Texture_Remove_With_GuidEmpty()
    {
        var res = PbrOverrideMerge.Apply(BaseMat, new GltfMaterialOverride { EmissiveTexture = Guid.Empty });
        Assert.Equal(Guid.Empty, res.EmissiveTextureId);
        Assert.Equal(BaseMat.BaseColorTextureId, res.BaseColorTextureId);
    }

    [Theory]
    [InlineData(GltfAlphaMode.Blend, PbrAlphaMode.Blend)]
    [InlineData(GltfAlphaMode.Mask, PbrAlphaMode.Mask)]
    [InlineData(GltfAlphaMode.Opaque, PbrAlphaMode.Opaque)]
    public void AlphaMode_Mapping(GltfAlphaMode wireMode, PbrAlphaMode expectedMode)
    {
        var res = PbrOverrideMerge.Apply(BaseMat, new GltfMaterialOverride { AlphaMode = wireMode });
        Assert.Equal(expectedMode, res.AlphaMode);
    }

    [Fact]
    public void Default_Valued_Overrides_Apply()
    {
        var o = new GltfMaterialOverride
        {
            BaseColor = Vector4.One,
            Roughness = 1f,
            DoubleSided = false,
            AlphaMode = GltfAlphaMode.Opaque
        };
        var res = PbrOverrideMerge.Apply(BaseMat, o);
        Assert.Equal(Vector4.One, res.BaseColorFactor);
        Assert.Equal(1f, res.RoughnessFactor);
        Assert.False(res.DoubleSided);
        Assert.Equal(PbrAlphaMode.Opaque, res.AlphaMode);
    }

    [Fact]
    public void Clamping_Values()
    {
        var o = new GltfMaterialOverride
        {
            BaseColor = new Vector4(2f, -1f, 0.5f, 9f),
            Roughness = 5f,
            Metallic = -1f,
            AlphaCutoff = 3f,
            EmissiveColor = new Vector3(-2f, 1.5f, 0.3f)
        };
        var res = PbrOverrideMerge.Apply(BaseMat, o);
        Assert.Equal(new Vector4(1f, 0f, 0.5f, 1f), res.BaseColorFactor);
        Assert.Equal(1f, res.RoughnessFactor);
        Assert.Equal(0f, res.MetallicFactor);
        Assert.Equal(1f, res.AlphaCutoff);
        Assert.Equal(new Vector3(0f, 1f, 0.3f), res.EmissiveFactor);
    }

    [Fact]
    public void Transforms_Not_In_PbrMaterialData_Returns_Unchanged_Fields()
    {
        var o = new GltfMaterialOverride
        {
            BaseColorTransform = new GltfTextureTransform(new Vector2(1f, 2f), new Vector2(3f, 4f), 0.5f)
        };
        var res = PbrOverrideMerge.Apply(BaseMat, o);
        Assert.Equal(BaseMat.BaseColorTextureId, res.BaseColorTextureId);
        Assert.Equal(BaseMat.BaseColorFactor, res.BaseColorFactor);
        Assert.Equal(BaseMat.RoughnessFactor, res.RoughnessFactor);
    }
}
