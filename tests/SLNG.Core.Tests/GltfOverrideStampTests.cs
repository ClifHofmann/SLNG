using System;
using System.Collections.Generic;
using System.Numerics;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class GltfOverrideStampTests
{
    private static readonly FaceTexture DefaultFace = new(
        TextureId: Guid.NewGuid(),
        RenderMaterialId: Guid.NewGuid(),
        LegacyMaterialId: Guid.Empty,
        Color: Vector4.One,
        RepeatU: 1f, RepeatV: 1f,
        OffsetU: 0f, OffsetV: 0f,
        Rotation: 0f, TexGen: 0, Fullbright: false
    );

    [Fact]
    public void Face_Gets_Override_Others_Stay_Null()
    {
        var faces = new[] { DefaultFace, DefaultFace };
        var ov = new GltfMaterialOverride { Metallic = 0.5f };
        var dict = new Dictionary<int, GltfMaterialOverride> { [1] = ov };

        var res = GltfOverrideStamp.Apply(faces, DefaultFace, dict);
        Assert.NotNull(res);
        Assert.Equal(2, res!.Length);
        Assert.Null(res[0].PbrOverride);
        Assert.Equal(ov, res[1].PbrOverride);
    }

    [Fact]
    public void Replacing_Set_Removes_Old_Overrides()
    {
        var ov1 = new GltfMaterialOverride { Metallic = 0.5f };
        var ov2 = new GltfMaterialOverride { Roughness = 0.8f };
        var faces = new[] { DefaultFace with { PbrOverride = ov1 }, DefaultFace };

        var newDict = new Dictionary<int, GltfMaterialOverride> { [1] = ov2 };
        var res = GltfOverrideStamp.Apply(faces, DefaultFace, newDict);
        Assert.NotNull(res);
        Assert.Null(res![0].PbrOverride);
        Assert.Equal(ov2, res[1].PbrOverride);
    }

    [Fact]
    public void Same_Input_Twice_Returns_Same_Reference()
    {
        var ov = new GltfMaterialOverride { Metallic = 0.5f };
        var dict = new Dictionary<int, GltfMaterialOverride> { [0] = ov };

        var res1 = GltfOverrideStamp.Apply(new[] { DefaultFace }, DefaultFace, dict);
        var res2 = GltfOverrideStamp.Apply(res1, DefaultFace, dict);
        Assert.Same(res1, res2);
    }

    [Fact]
    public void Null_Or_Short_Array_Grows_With_DefaultFace_Copies()
    {
        var ov = new GltfMaterialOverride { Metallic = 0.5f };
        var dict = new Dictionary<int, GltfMaterialOverride> { [3] = ov };

        var resFromNull = GltfOverrideStamp.Apply(null, DefaultFace, dict);
        Assert.NotNull(resFromNull);
        Assert.Equal(4, resFromNull!.Length);
        Assert.Equal(DefaultFace with { PbrOverride = null }, resFromNull[0]);
        Assert.Equal(DefaultFace with { PbrOverride = null }, resFromNull[1]);
        Assert.Equal(DefaultFace with { PbrOverride = null }, resFromNull[2]);
        Assert.Equal(DefaultFace with { PbrOverride = ov }, resFromNull[3]);

        var shortFaces = new[] { DefaultFace };
        var resFromShort = GltfOverrideStamp.Apply(shortFaces, DefaultFace, dict);
        Assert.Equal(4, resFromShort!.Length);
        Assert.Equal(ov, resFromShort[3].PbrOverride);
    }

    [Fact]
    public void Empty_Or_Null_Overrides_Strips_Every_Override()
    {
        var ov = new GltfMaterialOverride { Metallic = 0.5f };
        var facesWithOv = new[] { DefaultFace with { PbrOverride = ov } };

        var resNull = GltfOverrideStamp.Apply(facesWithOv, DefaultFace, null);
        Assert.NotNull(resNull);
        Assert.Null(resNull![0].PbrOverride);

        var resEmpty = GltfOverrideStamp.Apply(facesWithOv, DefaultFace, new Dictionary<int, GltfMaterialOverride>());
        Assert.NotNull(resEmpty);
        Assert.Null(resEmpty![0].PbrOverride);

        // When array already has no overrides, same reference returned
        var cleanFaces = new[] { DefaultFace };
        var resClean = GltfOverrideStamp.Apply(cleanFaces, DefaultFace, null);
        Assert.Same(cleanFaces, resClean);
    }

    [Fact]
    public void Stamping_Then_FaceSurfaceMerge_Respects_Overrides()
    {
        var ov1 = new GltfMaterialOverride { Metallic = 0.2f };
        var ov2 = new GltfMaterialOverride { Metallic = 0.8f };

        // Different overrides -> run break
        var diffDict = new Dictionary<int, GltfMaterialOverride> { [0] = ov1, [1] = ov2 };
        var facesDiff = GltfOverrideStamp.Apply(new[] { DefaultFace, DefaultFace }, DefaultFace, diffDict)!;
        var runStartDiff = new bool[2];
        int surfacesDiff = FaceSurfaceMerge.Plan(new[] { 0, 1 }, facesDiff, DefaultFace, FaceSurfaceMerge.NoAnimatedFace, runStartDiff);
        Assert.Equal(2, surfacesDiff);

        // Equal overrides -> no run break (merged into 1 surface)
        var eqDict = new Dictionary<int, GltfMaterialOverride> { [0] = ov1, [1] = ov1 };
        var facesEq = GltfOverrideStamp.Apply(new[] { DefaultFace, DefaultFace }, DefaultFace, eqDict)!;
        var runStartEq = new bool[2];
        int surfacesEq = FaceSurfaceMerge.Plan(new[] { 0, 1 }, facesEq, DefaultFace, FaceSurfaceMerge.NoAnimatedFace, runStartEq);
        Assert.Equal(1, surfacesEq);
    }

    [Fact]
    public void PrimMaterialKey_Considers_PbrOverride()
    {
        var ov1 = new GltfMaterialOverride { Metallic = 0.2f };
        var ov2 = new GltfMaterialOverride { Metallic = 0.8f };
        var ovTransform = new GltfMaterialOverride
        {
            BaseColorTransform = new GltfTextureTransform(Vector2.Zero, new Vector2(2f, 2f), 0f)
        };

        var f1 = DefaultFace with { PbrOverride = ov1 };
        var f1Clone = DefaultFace with { PbrOverride = new GltfMaterialOverride { Metallic = 0.2f } };
        var f2 = DefaultFace with { PbrOverride = ov2 };
        var fTrans = DefaultFace with { PbrOverride = ovTransform };

        var k1 = PrimMaterialKey.Create(f1, Vector3.One, 1);
        var k1Clone = PrimMaterialKey.Create(f1Clone, Vector3.One, 1);
        var k2 = PrimMaterialKey.Create(f2, Vector3.One, 1);
        var kTrans = PrimMaterialKey.Create(fTrans, Vector3.One, 1);

        Assert.Equal(k1, k1Clone);
        Assert.Equal(k1.GetHashCode(), k1Clone.GetHashCode());
        Assert.NotEqual(k1, k2);
        Assert.NotEqual(k1, kTrans);
    }
}
