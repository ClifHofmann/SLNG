using System.Numerics;
using System.Text;
using SLNG.Core;
using Xunit;

namespace SLNG.Assets.Tests;

/// <summary>
/// BUG-RENDER-49: a glTF material asset as Second Life stores it -- an LLSD map
/// <c>{ version, type, data }</c> in binary LLSD with the glTF JSON in <c>data</c>
/// (llmaterialeditor.cpp:1305-1315), read the way <c>LLGLTFMaterialList::onAssetLoadComplete</c> and
/// <c>LLGLTFMaterial::setFromModel</c> read it (llgltfmateriallist.cpp:559-589,
/// llgltfmaterial.cpp:220-265). LibreMetaverse parsed these bytes as bare JSON, so every material
/// failed: the v0.27.83 test logged 109 failures over 40 ids and not one material.
/// </summary>
public sealed class GltfMaterialAssetTests
{
    private static readonly Guid BaseColor = Guid.Parse("5748decc-f629-461c-9a36-a35a221fe21f");
    private static readonly Guid Normal = Guid.Parse("e2c44183-ef12-80c6-35f6-eb7c22b754ca");

    /// <summary>The glTF the viewer writes for one material (tinygltf, LLGLTFMaterial::writeToModel):
    /// images carry the texture UUIDs as their uri, textures point at images, the material at
    /// textures.</summary>
    private const string Json =
        "{\"asset\":{\"version\":\"2.0\"}," +
        "\"images\":[{\"uri\":\"5748decc-f629-461c-9a36-a35a221fe21f\"},{\"uri\":\"e2c44183-ef12-80c6-35f6-eb7c22b754ca\"}]," +
        "\"materials\":[{\"alphaCutoff\":0.35,\"alphaMode\":\"MASK\",\"doubleSided\":true," +
        "\"emissiveFactor\":[0.1,0.2,0.3],\"normalTexture\":{\"index\":1}," +
        "\"pbrMetallicRoughness\":{\"baseColorFactor\":[0.8,0.6,0.4,1.0]," +
        "\"baseColorTexture\":{\"extensions\":{\"KHR_texture_transform\":{\"offset\":[0.25,-0.5],\"rotation\":0.5,\"scale\":[2.0,3.0]}},\"index\":0}," +
        "\"metallicFactor\":0.0,\"roughnessFactor\":0.7}}]," +
        "\"textures\":[{\"source\":0},{\"source\":1}]}";

    private static byte[] U32(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static byte[] Binary(string version, string type, string data)
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("<? LLSD/Binary ?>\n"));
        bytes.Add((byte)'{');
        bytes.AddRange(U32(3));
        foreach (var (key, value) in new[] { ("version", version), ("type", type), ("data", data) })
        {
            var k = Encoding.UTF8.GetBytes(key);
            var v = Encoding.UTF8.GetBytes(value);
            bytes.Add((byte)'k');
            bytes.AddRange(U32(k.Length));
            bytes.AddRange(k);
            bytes.Add((byte)'s');
            bytes.AddRange(U32(v.Length));
            bytes.AddRange(v);
        }
        bytes.Add((byte)'}');
        return bytes.ToArray();
    }

    private static void AssertTheMaterial(PbrMaterialData? m, string error)
    {
        Assert.True(m != null, error);
        Assert.Equal(BaseColor, m!.BaseColorTextureId);
        Assert.Equal(Normal, m.NormalTextureId);
        Assert.Equal(Guid.Empty, m.MetallicRoughnessTextureId);
        Assert.Equal(Guid.Empty, m.EmissiveTextureId);
        Assert.Equal(new Vector4(0.8f, 0.6f, 0.4f, 1f), m.BaseColorFactor);
        Assert.Equal(0f, m.MetallicFactor);
        Assert.Equal(0.7f, m.RoughnessFactor, 5);
        Assert.Equal(new Vector3(0.1f, 0.2f, 0.3f), m.EmissiveFactor);
        Assert.Equal(PbrAlphaMode.Mask, m.AlphaMode);
        Assert.Equal(0.35f, m.AlphaCutoff, 5);
        Assert.True(m.DoubleSided);
        Assert.Equal(new GltfTextureTransform(new Vector2(0.25f, -0.5f), new Vector2(2f, 3f), 0.5f), m.BaseColorTransform);
    }

    [Fact]
    public void TheBinaryEnvelopeTheViewerWritesDecodes()
    {
        var m = GltfMaterialAsset.Decode(Binary("1.1", "GLTF 2.0", Json), out string error);
        AssertTheMaterial(m, error);
    }

    [Fact]
    public void Version10IsAcceptedToo()
    {
        Assert.NotNull(GltfMaterialAsset.Decode(Binary("1.0", "GLTF 2.0", Json), out _));
    }

    [Fact]
    public void AnXmlEnvelopeDecodes()
    {
        string xml = "<? llsd/xml ?>\n<llsd><map><key>version</key><string>1.1</string><key>type</key><string>GLTF 2.0</string>" +
                     $"<key>data</key><string>{System.Security.SecurityElement.Escape(Json)}</string></map></llsd>";
        var m = GltfMaterialAsset.Decode(Encoding.UTF8.GetBytes(xml), out string error);
        AssertTheMaterial(m, error);
    }

    [Fact]
    public void ANotationEnvelopeDecodes()
    {
        string escaped = Json.Replace("\\", "\\\\").Replace("'", "\\'");
        string notation = $"{{'version':'1.1','type':'GLTF 2.0','data':'{escaped}'}}";
        var m = GltfMaterialAsset.Decode(Encoding.UTF8.GetBytes(notation), out string error);
        AssertTheMaterial(m, error);
    }

    [Fact]
    public void BareGltfJsonIsTheLastResort()
    {
        var m = GltfMaterialAsset.Decode(Encoding.UTF8.GetBytes(Json), out string error);
        AssertTheMaterial(m, error);
    }

    [Fact]
    public void AMaterialWithNothingSetTakesTheGltfDefaults()
    {
        var m = GltfMaterialAsset.Decode(Binary("1.1", "GLTF 2.0", "{\"materials\":[{}]}"), out string error);

        Assert.True(m != null, error);
        Assert.Equal(Guid.Empty, m!.BaseColorTextureId);
        Assert.Equal(Vector4.One, m.BaseColorFactor);
        Assert.Equal(1f, m.MetallicFactor);
        Assert.Equal(1f, m.RoughnessFactor);
        Assert.Equal(PbrAlphaMode.Opaque, m.AlphaMode);
        Assert.Equal(0.5f, m.AlphaCutoff);
        Assert.False(m.DoubleSided);
        Assert.Null(m.BaseColorTransform);
    }

    [Fact]
    public void FactorsOutOfRangeAreClampedAsTheViewerClampsThem()
    {
        var m = GltfMaterialAsset.Decode(Binary("1.1", "GLTF 2.0",
            "{\"materials\":[{\"alphaCutoff\":3,\"pbrMetallicRoughness\":{\"metallicFactor\":-1,\"roughnessFactor\":7}}]}"), out _);

        Assert.Equal(0f, m!.MetallicFactor);
        Assert.Equal(1f, m.RoughnessFactor);
        Assert.Equal(1f, m.AlphaCutoff);
    }

    [Theory]
    [InlineData("1.1", "GLTF 1.0", "type")]
    [InlineData("2.0", "GLTF 2.0", "version")]
    public void AnEnvelopeTheViewerWouldRejectIsRejected(string version, string type, string why)
    {
        Assert.Null(GltfMaterialAsset.Decode(Binary(version, type, Json), out string error));
        Assert.Contains(why, error);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x00, 0x01, 0x02 })]
    [InlineData(new byte[] { (byte)'<', (byte)'?', (byte)' ', (byte)'L', (byte)'L' })]
    public void GarbageIsNullWithAReasonNeverAnException(byte[] bytes)
    {
        Assert.Null(GltfMaterialAsset.Decode(bytes, out string error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void ATextureIndexThatPointsNowhereIsNoTexture()
    {
        var m = GltfMaterialAsset.Decode(Binary("1.1", "GLTF 2.0",
            "{\"materials\":[{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":4}}}],\"textures\":[{\"source\":0}],\"images\":[{\"uri\":\"x\"}]}"), out _);

        Assert.Equal(Guid.Empty, m!.BaseColorTextureId);
    }

    [Fact]
    public void AnOverrideTransformReplacesTheMaterialsOwn()
    {
        var m = GltfMaterialAsset.Decode(Binary("1.1", "GLTF 2.0", Json), out _)!;
        var own = new GltfTextureTransform(new Vector2(0.5f, 0.5f), new Vector2(4f, 4f), 0f);

        Assert.Equal(own, PbrOverrideMerge.Apply(m, new GltfMaterialOverride { BaseColorTransform = own }).BaseColorTransform);
        Assert.Equal(m.BaseColorTransform, PbrOverrideMerge.Apply(m, new GltfMaterialOverride { Roughness = 0.1f }).BaseColorTransform);
    }
}
