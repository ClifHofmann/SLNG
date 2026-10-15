using System;
using System.Numerics;
using System.Text;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class GltfOverrideParserTests
{
    private static bool TryParse(string s, out GltfOverrideMessage? msg, out string? err)
    {
        return GltfOverrideParser.TryParse(Encoding.UTF8.GetBytes(s), out msg, out err);
    }

    [Fact]
    public void F1_Recolour()
    {
        const string payload = "{'id':i4242,'od':[{'bc':[r0.8,r0.1,r0.1,r1]}],'te':[i1]}";
        Assert.True(TryParse(payload, out var msg, out var err), err);
        Assert.NotNull(msg);
        Assert.Equal(4242u, msg!.LocalId);
        Assert.Single(msg.Faces);
        var f1 = msg.Faces[1];
        Assert.Equal(new Vector4(0.8f, 0.1f, 0.1f, 1f), f1.BaseColor);
        Assert.Null(f1.BaseColorTexture);
        Assert.Null(f1.NormalTexture);
        Assert.Null(f1.MetallicRoughnessTexture);
        Assert.Null(f1.EmissiveTexture);
        Assert.Null(f1.EmissiveColor);
        Assert.Null(f1.Metallic);
        Assert.Null(f1.Roughness);
        Assert.Null(f1.AlphaMode);
        Assert.Null(f1.AlphaCutoff);
        Assert.Null(f1.DoubleSided);
        Assert.Null(f1.BaseColorTransform);
    }

    [Fact]
    public void F2_TwoFaces()
    {
        const string payload = "{'id':i77,'od':[{'bc':[r1,r1,r1,r0.5],'mf':r0,'rf':r0.35,'tex':[u11111111-2222-3333-4444-555555555555]},{'am':i2,'ac':r0.4,'ds':1}],'te':[i0,i2]}";
        Assert.True(TryParse(payload, out var msg, out var err), err);
        Assert.NotNull(msg);
        Assert.Equal(77u, msg!.LocalId);
        Assert.Equal(2, msg.Faces.Count);

        var f0 = msg.Faces[0];
        Assert.Equal(new Vector4(1f, 1f, 1f, 0.5f), f0.BaseColor);
        Assert.Equal(0f, f0.Metallic);
        Assert.Equal(0.35f, f0.Roughness);
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), f0.BaseColorTexture);

        var f2 = msg.Faces[2];
        Assert.Equal(GltfAlphaMode.Mask, f2.AlphaMode);
        Assert.Equal(0.4f, f2.AlphaCutoff);
        Assert.True(f2.DoubleSided);
    }

    [Fact]
    public void F3_TextureRemoved()
    {
        const string payload = "{'id':i9,'od':[{'tex':[!,!,!,uffffffff-ffff-ffff-ffff-ffffffffffff]}],'te':[i3]}";
        Assert.True(TryParse(payload, out var msg, out var err), err);
        Assert.NotNull(msg);
        Assert.Equal(9u, msg!.LocalId);
        var f3 = msg.Faces[3];
        Assert.Equal(Guid.Empty, f3.EmissiveTexture);
        Assert.Null(f3.BaseColorTexture);
        Assert.Null(f3.NormalTexture);
        Assert.Null(f3.MetallicRoughnessTexture);
    }

    [Fact]
    public void F4_Transform()
    {
        const string payload = "{'id':i5,'od':[{'ti':[{'o':[r0.25,r-0.5],'s':[r2,r3],'r':r0.5236}]}],'te':[i0]}";
        Assert.True(TryParse(payload, out var msg, out var err), err);
        Assert.NotNull(msg);
        var f0 = msg!.Faces[0];
        Assert.NotNull(f0.BaseColorTransform);
        var t = f0.BaseColorTransform!.Value;
        Assert.Equal(new Vector2(0.25f, -0.5f), t.Offset);
        Assert.Equal(new Vector2(2f, 3f), t.Scale);
        Assert.Equal(0.5236f, t.Rotation, 4);
        Assert.Null(f0.NormalTransform);
        Assert.Null(f0.MetallicRoughnessTransform);
        Assert.Null(f0.EmissiveTransform);
    }

    [Fact]
    public void F4b_Sparse()
    {
        const string payload = "{'id':i5,'od':[{'ti':[!,!,{'s':[r4,r4]}]}],'te':[i0]}";
        Assert.True(TryParse(payload, out var msg, out var err), err);
        Assert.NotNull(msg);
        var f0 = msg!.Faces[0];
        Assert.Null(f0.BaseColorTransform);
        Assert.Null(f0.NormalTransform);
        Assert.NotNull(f0.MetallicRoughnessTransform);
        var t = f0.MetallicRoughnessTransform!.Value;
        Assert.Equal(Vector2.Zero, t.Offset);
        Assert.Equal(new Vector2(4f, 4f), t.Scale);
        Assert.Equal(0f, t.Rotation);
    }

    [Fact]
    public void F5_ClearAll_LmvRejects()
    {
        const string payload = "{'id':i4242,'od':[],'te':[]}";
        Assert.True(TryParse(payload, out var msg, out var err), err);
        Assert.NotNull(msg);
        Assert.Equal(4242u, msg!.LocalId);
        Assert.Empty(msg.Faces);
    }

    [Fact]
    public void F5b_ClearAll_Variants()
    {
        Assert.True(TryParse("{'id':i4242,'te':[],'od':[]}", out var msg1, out _));
        Assert.Empty(msg1!.Faces);

        Assert.True(TryParse("{'id':i4242,'te':[]}", out var msg2, out _));
        Assert.Empty(msg2!.Faces);
    }

    [Fact]
    public void F6_EmptyEntry_LmvRejects()
    {
        const string payload = "{'id':i4242,'od':[{}],'te':[i0]}";
        Assert.True(TryParse(payload, out var msg, out var err), err);
        Assert.NotNull(msg);
        Assert.Empty(msg!.Faces);
    }

    [Fact]
    public void F7_Mixed_LmvRejects()
    {
        const string payload = "{'id':i4242,'od':[{},{'bc':[r1,r0,r0,r1]}],'te':[i0,i1]}";
        Assert.True(TryParse(payload, out var msg, out var err), err);
        Assert.NotNull(msg);
        Assert.Single(msg!.Faces);
        Assert.True(msg.Faces.ContainsKey(1));
        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), msg.Faces[1].BaseColor);
    }

    [Fact]
    public void F8_DefaultValuesAreOverrides()
    {
        const string payload = "{'id':i1,'od':[{'bc':[r1,r1,r1,r1],'rf':r1,'mf':r1,'ac':r0.5,'am':i0,'ds':0}],'te':[i0]}";
        Assert.True(TryParse(payload, out var msg, out var err), err);
        Assert.NotNull(msg);
        var f0 = msg!.Faces[0];
        Assert.Equal(new Vector4(1f, 1f, 1f, 1f), f0.BaseColor);
        Assert.Equal(1f, f0.Roughness);
        Assert.Equal(1f, f0.Metallic);
        Assert.Equal(0.5f, f0.AlphaCutoff);
        Assert.Equal(GltfAlphaMode.Opaque, f0.AlphaMode);
        Assert.False(f0.DoubleSided);
    }

    [Fact]
    public void F9_WrongTypesIgnored()
    {
        const string payload = "{'id':i1,'od':[{'mf':i1,'rf':'0.2','am':r2.0,'ds':i1}],'te':[i0]}";
        Assert.True(TryParse(payload, out var msg, out var err), err);
        Assert.NotNull(msg);
        Assert.Empty(msg!.Faces);
    }

    [Fact]
    public void F10_FaceRange()
    {
        const string payload = "{'id':i1,'te':[i-1,i44,i45,i3],'od':[{'bc':[r1,r0,r0,r1]},{'bc':[r0,r1,r0,r1]},{'bc':[r0,r0,r1,r1]},{'bc':[r1,r1,r0,r1]}]}";
        Assert.True(TryParse(payload, out var msg, out var err), err);
        Assert.NotNull(msg);
        Assert.Equal(2, msg!.Faces.Count);
        Assert.True(msg.Faces.ContainsKey(44));
        Assert.True(msg.Faces.ContainsKey(3));
        Assert.False(msg.Faces.ContainsKey(-1));
        Assert.False(msg.Faces.ContainsKey(45));
    }

    [Fact]
    public void F11_OdShorter()
    {
        const string payload = "{'id':i1,'od':[{'bc':[r1,r0,r0,r1]}],'te':[i0,i2]}";
        Assert.True(TryParse(payload, out var msg, out var err), err);
        Assert.NotNull(msg);
        Assert.Single(msg!.Faces);
        Assert.True(msg.Faces.ContainsKey(0));
        Assert.False(msg.Faces.ContainsKey(2));
    }

    [Fact]
    public void F12_OrderAndLayout()
    {
        const string payload = "  {\n    'te' : [ i1 ],\n    'od' : [ { 'bc' : [ r0, r1, r0, r1 ] } ],\n    'id' : i2\n  }\0";
        Assert.True(TryParse(payload, out var msg, out var err), err);
        Assert.NotNull(msg);
        Assert.Equal(2u, msg!.LocalId);
        Assert.Single(msg.Faces);
        Assert.Equal(new Vector4(0f, 1f, 0f, 1f), msg.Faces[1].BaseColor);
    }

    [Fact]
    public void F13_DuplicateFace_LaterWins()
    {
        const string payload = "{'id':i1,'te':[i1,i1],'od':[{'bc':[r1,r0,r0,r1]},{}]}";
        Assert.True(TryParse(payload, out var msg, out var err), err);
        Assert.NotNull(msg);
        Assert.Empty(msg!.Faces);
    }

    [Fact]
    public void F14_NonFinite_Ignored()
    {
        const string payload = "{'id':i1,'od':[{'bc':[rnan,r0,r0,r1],'rf':rinf}],'te':[i0]}";
        Assert.True(TryParse(payload, out var msg, out var err), err);
        Assert.NotNull(msg);
        Assert.Empty(msg!.Faces);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{'od':[],'te':[]}")]
    [InlineData("{'id':r1,'te':[]}")]
    [InlineData("{'id':i1}")]
    [InlineData("{'id':i1,'te':{}}")]
    [InlineData("{'id':i1,'te':[i0")]
    [InlineData("{'id':i1,'te':?, 'od':[]}")]
    public void Failures_ReturnFalse(string bad)
    {
        bool ok = TryParse(bad, out var msg, out var err);
        Assert.False(ok);
        Assert.Null(msg);
        Assert.False(string.IsNullOrWhiteSpace(err));
    }
}
