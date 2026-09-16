using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreMetaverse.StructuredData;
using SLNG.Assets;
using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Assets.Tests;

/// <summary>
/// FEAT-PERF-07: an uploaded SL mesh asset carries FOUR independently-baked geometry blocks, and
/// <see cref="AssetService.GetMeshAsync"/> used to hardcode the largest of them. Measured across
/// the 3643 assets one real session cached, that is 28.2M triangles where <c>medium_lod</c> holds
/// 6.9M and <c>low_lod</c> 0.82M -- so the level chosen is worth up to two orders of magnitude of
/// geometry, on the GPU and in the renderer's own main-thread mesh build alike.
///
/// <para>The fixtures here are synthesized LLMesh assets rather than captured ones, because the
/// property under test is "which block did we read", and that is only decidable if every block
/// holds a KNOWN, DIFFERENT triangle count. A real asset cannot promise that.</para>
/// </summary>
public class MeshLodTests
{
    // ----------------------------------------------------------------------------------------
    // Fixture: a minimal but genuinely valid LLMesh asset.
    //
    // Header is LLSD-binary, no <?LLSD/Binary?> prefix (SL's own assets have none -- their first
    // byte is '{'), each *_lod entry giving {offset, size} relative to the END of the header.
    // Each block is a zlib-compressed LLSD-binary array of submesh maps.
    //
    // Only "Position" and "TriangleList" are written. "Normal" and "TexCoord0" are genuinely
    // optional in the format (LibreMetaverse's reader TryGetValue's both), and "TexCoord0"
    // additionally requires a "TexCoord0Domain" it would then have to agree with -- more surface
    // for a fixture bug to hide in than the tests get back.
    // ----------------------------------------------------------------------------------------

    /// <summary>One submesh with <paramref name="triangles"/> triangles over 3x that many
    /// vertices. Positions are uint16 in the default [-0.5, 0.5] domain; the actual coordinates
    /// do not matter, only that they are finite (AssetService rejects NaN/Infinity outright).</summary>
    private static OSDMap Submesh(int triangles)
    {
        int vertices = triangles * 3;
        var position = new byte[vertices * 6];
        for (int i = 0; i < position.Length; i++) position[i] = (byte)(i * 7 % 251);

        var triangleList = new byte[triangles * 6];
        for (int t = 0; t < triangles; t++)
        {
            for (int c = 0; c < 3; c++)
            {
                ushort index = (ushort)(t * 3 + c);
                triangleList[t * 6 + c * 2] = (byte)(index & 0xFF);
                triangleList[t * 6 + c * 2 + 1] = (byte)(index >> 8);
            }
        }

        return new OSDMap
        {
            ["Position"] = OSD.FromBinary(position),
            ["TriangleList"] = OSD.FromBinary(triangleList),
        };
    }

    private static OSDMap NoGeometrySubmesh() => new() { ["NoGeometry"] = OSD.FromBoolean(true) };

    /// <summary>A block holding one submesh per entry. An empty list means "this LOD exists in the
    /// header but carries no bytes", which is how a single-LOD upload reaches a viewer.</summary>
    private static OSDArray Block(params OSDMap[] submeshes)
    {
        var array = new OSDArray();
        foreach (var s in submeshes) array.Add(s);
        return array;
    }

    /// <summary>Assembles the four blocks into asset bytes. A null block is written as
    /// <c>size: 0</c> — present in the header, no payload — exactly as an upload with fewer than
    /// four LODs arrives.</summary>
    private static byte[] BuildMeshAsset(OSDArray? highest, OSDArray? high, OSDArray? medium, OSDArray? low)
    {
        var blocks = new (string Key, OSDArray? Content)[]
        {
            ("high_lod", highest),
            ("medium_lod", high),
            ("low_lod", medium),
            ("lowest_lod", low),
        };

        var header = new OSDMap();
        var payload = new List<byte>();
        foreach (var (key, content) in blocks)
        {
            byte[] compressed = content == null
                ? System.Array.Empty<byte>()
                : LibreMetaverse.Helpers.ZCompressOSD(content);
            header[key] = new OSDMap
            {
                ["offset"] = OSD.FromInteger(compressed.Length == 0 ? 0 : payload.Count),
                ["size"] = OSD.FromInteger(compressed.Length),
            };
            payload.AddRange(compressed);
        }

        var bytes = new List<byte>(OSDParser.SerializeLLSDBinary(header, false));
        bytes.AddRange(payload);
        return bytes.ToArray();
    }

    /// <summary>An AssetService whose disk cache already holds <paramref name="asset"/>, so
    /// GetMeshAsync serves it without a grid connection. This is the same path a returning viewer
    /// takes for anything it has seen before -- which is also why a second LOD of an
    /// already-cached mesh costs a decode and no network.</summary>
    private static (AssetService Service, System.Guid MeshId) NewServiceWithCachedMesh(byte[] asset)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "slng-meshlod-tests-" + System.Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        var meshId = System.Guid.NewGuid();
        File.WriteAllBytes(Path.Combine(tempDir, meshId + ".mesh"), asset);
        return (new AssetService(new GridSession(), tempDir), meshId);
    }

    private static int TriangleCount(MeshData mesh) => mesh.Submeshes.Sum(s => s.Indices.Length) / 3;

    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// The headline behaviour: four levels, four different geometries. Before FEAT-PERF-07 every
    /// one of these returned the <c>high_lod</c> block.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task GetMeshAsync_ReadsTheBlockTheRequestedLevelNames()
    {
        var (service, meshId) = NewServiceWithCachedMesh(BuildMeshAsset(
            highest: Block(Submesh(64)),
            high: Block(Submesh(16)),
            medium: Block(Submesh(4)),
            low: Block(Submesh(1))));

        var highest = await service.GetMeshAsync(meshId, MeshDetailLevel.Highest);
        var high = await service.GetMeshAsync(meshId, MeshDetailLevel.High);
        var medium = await service.GetMeshAsync(meshId, MeshDetailLevel.Medium);
        var low = await service.GetMeshAsync(meshId, MeshDetailLevel.Low);

        Assert.NotNull(highest);
        Assert.NotNull(high);
        Assert.NotNull(medium);
        Assert.NotNull(low);

        Assert.Equal(64, TriangleCount(highest!));
        Assert.Equal(16, TriangleCount(high!));
        Assert.Equal(4, TriangleCount(medium!));
        Assert.Equal(1, TriangleCount(low!));
    }

    /// <summary>
    /// The default must stay <c>Highest</c>. Every caller that has not been taught about LOD --
    /// the avatar/attachment path, for one, which FEAT-PERF-07 Phase 1 deliberately leaves alone
    /// -- relies on this, and a default that silently dropped detail would degrade worn mesh
    /// bodies as a side effect of an object-rendering change.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task GetMeshAsync_DefaultLevel_IsHighest()
    {
        var (service, meshId) = NewServiceWithCachedMesh(BuildMeshAsset(
            highest: Block(Submesh(64)),
            high: Block(Submesh(16)),
            medium: Block(Submesh(4)),
            low: Block(Submesh(1))));

        var byDefault = await service.GetMeshAsync(meshId);

        Assert.NotNull(byDefault);
        Assert.Equal(64, TriangleCount(byDefault!));
    }

    /// <summary>
    /// Two levels of one asset must not collide in the cache. The old key was the bare mesh id,
    /// which cannot survive per-level decoding: whichever level loaded first would answer every
    /// later request for any other, so an object that walked into range would keep the LOD some
    /// unrelated distant object happened to ask for.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task GetMeshAsync_DifferentLevels_DoNotShareACacheEntry()
    {
        var (service, meshId) = NewServiceWithCachedMesh(BuildMeshAsset(
            highest: Block(Submesh(64)),
            high: Block(Submesh(16)),
            medium: Block(Submesh(4)),
            low: Block(Submesh(1))));

        // Deliberately low first: the failure this guards is the FIRST request poisoning the rest.
        var low = await service.GetMeshAsync(meshId, MeshDetailLevel.Low);
        var highest = await service.GetMeshAsync(meshId, MeshDetailLevel.Highest);
        var lowAgain = await service.GetMeshAsync(meshId, MeshDetailLevel.Low);

        Assert.Equal(1, TriangleCount(low!));
        Assert.Equal(64, TriangleCount(highest!));
        Assert.Equal(1, TriangleCount(lowAgain!));
        Assert.Same(low, lowAgain); // still cached, not re-decoded
    }

    /// <summary>
    /// Not every creator uploads four LODs. A block can be declared in the header with
    /// <c>size: 0</c>, and the request has to fall back UPWARD rather than render the object as
    /// nothing -- which is the one failure mode <c>[MeshFallback]</c> exists to make visible, and
    /// which mesh LOD would otherwise reach from a routine distance change.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task GetMeshAsync_EmptyLowerBlock_FallsBackUpward()
    {
        var (service, meshId) = NewServiceWithCachedMesh(BuildMeshAsset(
            highest: Block(Submesh(64)),
            high: Block(Submesh(16)),
            medium: null,
            low: null));

        var low = await service.GetMeshAsync(meshId, MeshDetailLevel.Low);
        var medium = await service.GetMeshAsync(meshId, MeshDetailLevel.Medium);

        // Low and Medium are both absent, so both land on the next level that exists: High.
        Assert.Equal(16, TriangleCount(low!));
        Assert.Equal(16, TriangleCount(medium!));
    }

    /// <summary>
    /// The other shape of "this level has nothing": the block exists and parses, but every submesh
    /// in it is the format's <c>NoGeometry</c> marker. That decodes fine and yields zero submeshes,
    /// which is indistinguishable from a dead asset unless the fallback covers it too.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task GetMeshAsync_AllNoGeometrySubmeshes_FallsBackUpward()
    {
        var (service, meshId) = NewServiceWithCachedMesh(BuildMeshAsset(
            highest: Block(Submesh(64)),
            high: Block(Submesh(16)),
            medium: Block(Submesh(4)),
            low: Block(NoGeometrySubmesh(), NoGeometrySubmesh())));

        var low = await service.GetMeshAsync(meshId, MeshDetailLevel.Low);

        Assert.Equal(4, TriangleCount(low!));
    }

    /// <summary>
    /// Never downward. An asset whose ONLY block is the lowest one must not quietly hand a
    /// <c>Highest</c> request something coarser -- that direction is a visual regression, not a
    /// saving, and would under-detail an object the camera is standing next to.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task GetMeshAsync_MissingHighestBlock_DoesNotFallBackDownward()
    {
        var (service, meshId) = NewServiceWithCachedMesh(BuildMeshAsset(
            highest: null,
            high: null,
            medium: null,
            low: Block(Submesh(1))));

        var highest = await service.GetMeshAsync(meshId, MeshDetailLevel.Highest);

        Assert.Null(highest);
    }

    /// <summary>
    /// A lower LOD can drop submeshes the higher one has, which shifts every later submesh's
    /// POSITION in the array while leaving its SL face number alone. The renderer keys per-face
    /// materials off <see cref="MeshSubmesh.FaceIndex"/>, so if that followed the position instead
    /// of the face number, walking away from an object would silently rotate its textures onto the
    /// wrong faces.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task GetMeshAsync_LowerLevelWithSkippedSubmeshes_KeepsTheSlFaceNumbers()
    {
        var (service, meshId) = NewServiceWithCachedMesh(BuildMeshAsset(
            highest: Block(Submesh(8), Submesh(8), Submesh(8)),
            high: Block(NoGeometrySubmesh(), Submesh(2), Submesh(2)),
            medium: Block(Submesh(1)),
            low: Block(Submesh(1))));

        var high = await service.GetMeshAsync(meshId, MeshDetailLevel.High);

        Assert.NotNull(high);
        // Face 0 was dropped, so the two surviving submeshes are faces 1 and 2 -- NOT 0 and 1.
        Assert.Equal(new[] { 1, 2 }, high!.Submeshes.Select(s => s.FaceIndex).ToArray());
    }

    /// <summary>
    /// Pins the off-by-one that makes this whole area dangerous: the block names are shifted one
    /// step from the enum's own spelling, so <see cref="MeshDetailLevel.High"/> is the asset's
    /// <c>medium_lod</c>. The skin-weight re-decode reads the raw block by NAME while the geometry
    /// is decoded by LEVEL, and the two disagreeing is silent -- every vertex of one LOD would get
    /// the weights of a differently-sized vertex array, shredding a rigged mesh with no error
    /// anywhere. "Correcting" these names to line up with the enum is exactly the plausible edit
    /// this test exists to stop.
    /// </summary>
    [Theory]
    [InlineData(MeshDetailLevel.Highest, "high_lod")]
    [InlineData(MeshDetailLevel.High, "medium_lod")]
    [InlineData(MeshDetailLevel.Medium, "low_lod")]
    [InlineData(MeshDetailLevel.Low, "lowest_lod")]
    public void LodKeyFor_MapsEachLevelToItsAssetBlock(MeshDetailLevel lod, string expectedKey)
    {
        Assert.Equal(expectedKey, AssetService.LodKeyFor(lod));
    }
}
