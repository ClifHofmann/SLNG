using System.Reflection;
using LibreMetaverse;
using LibreMetaverse.Packets;
using SLNG.Core;
using SLNG.Net;
using Xunit;
using static SLNG.Net.Tests.ExtraParamsBytes;

namespace SLNG.Net.Tests;

/// <summary>
/// BUG-RENDER-49: the per-face glTF PBR material ids arrive in ExtraParams block 0x80
/// (<c>PARAMS_RENDER_MATERIAL</c>, llprimitive.h:110), not in the TextureEntry, and SLNG never read
/// it -- every PBR face on Second Life rendered its legacy diffuse texture. Payload
/// (<c>LLRenderMaterialParams::pack/unpack</c>, llprimitive.cpp:2336-2362): <c>U8 count</c>, then
/// count x (<c>U8 te_idx</c>, 16-byte UUID).
/// </summary>
public class RenderMaterialExtraParamsTests
{
    private const ulong RegionA = 1000UL;
    private const ulong RegionB = 2000UL;

    private static readonly Guid SirensMaterial = new("e69dde89-50f8-b85f-9a26-da60314acba6");
    private static readonly Guid OtherMaterial = new("35883cc3-83af-610e-a916-7ca15e82eaea");

    /// <summary>The whole ExtraParams buffer of local 823816049 as Agni sent it (from the Sirens
    /// object cache, 2026-10-18): a 0x80 block mapping faces 0-4 to one material, followed by the
    /// object's 0x60 mesh block. 116 bytes.</summary>
    private const string SirensExtraParamsHex =
        "028000560000000500e69dde8950f8b85f9a26da60314acba601e69dde8950f8b85f9a26da60314acba602e69dde89" +
        "50f8b85f9a26da60314acba603e69dde8950f8b85f9a26da60314acba604e69dde8950f8b85f9a26da60314acba660" +
        "0011000000e2b74ff10d1efa12935de12aa17fd9fd05";

    private const ushort RenderMaterial = 0x80;

    private static byte[] Payload(params (byte Face, Guid Id)[] entries) => PayloadWithCount((byte)entries.Length, entries);

    private static byte[] PayloadWithCount(byte count, params (byte Face, Guid Id)[] entries)
    {
        var bytes = new List<byte> { count };
        foreach (var (face, id) in entries)
        {
            bytes.Add(face);
            bytes.AddRange(id.ToByteArray(bigEndian: true));
        }
        return bytes.ToArray();
    }

    private static ObjectUpdatePacket.ObjectDataBlock Block(uint id, byte[]? extra) => new() { ID = id, ExtraParams = extra! };

    // --- Parsing ------------------------------------------------------------------------------

    [Fact]
    public void TheRealAgniBlockMapsFacesZeroToFourToItsMaterial()
    {
        var scan = ExtraParamsScan.Read(Convert.FromHexString(SirensExtraParamsHex));

        Assert.True(scan.Complete);
        Assert.Equal(116, scan.Length);
        Assert.NotNull(scan.RenderMaterials);
        var materials = scan.RenderMaterials!;
        Assert.Equal(5, materials.Count);
        for (int face = 0; face < 5; face++)
        {
            Assert.True(materials.TryGet(face, out var id));
            Assert.Equal(SirensMaterial, id);
        }
        Assert.False(materials.TryGet(5, out var none));
        Assert.Equal(Guid.Empty, none);
    }

    [Fact]
    public void TheUuidIsReadInNetworkOrder_TheWayLibreMetaverseReadsEveryOther()
    {
        // The bytes of the cached block, read by the library's own UUID constructor: the two must
        // agree, or a material id would be fetched under a scrambled name.
        var bytes = Convert.FromHexString(SirensExtraParamsHex);
        var viaLibrary = new UUID(bytes, 1 + 6 + 1 + 1).Guid;

        Assert.Equal(SirensMaterial, viaLibrary);
        var scan = ExtraParamsScan.Read(bytes);
        Assert.Equal(viaLibrary, scan.RenderMaterials!.Entries[0].MaterialId);
    }

    [Fact]
    public void ABlockWithNoEntriesIsPresentAndEmpty()
    {
        // Agni sends these (count 0, size 1): the block is there and names no face.
        var scan = ExtraParamsScan.Read(Envelope((RenderMaterial, new byte[] { 0 })));

        Assert.True(scan.Complete);
        Assert.NotNull(scan.RenderMaterials);
        Assert.Equal(0, scan.RenderMaterials!.Count);
        Assert.True(scan.RenderMaterialsKnown);
    }

    [Fact]
    public void APayloadWithoutEvenItsCountByteNamesNoFace()
    {
        var scan = ExtraParamsScan.Read(Envelope((RenderMaterial, Array.Empty<byte>())));

        Assert.True(scan.Complete);
        Assert.Equal(0, scan.RenderMaterials!.Count);
    }

    [Fact]
    public void APayloadShorterThanItsCountKeepsOnlyTheWholeEntries()
    {
        // Count 3, two whole entries and half of a third: the third is not invented.
        var two = PayloadWithCount(3, (0, SirensMaterial), (1, OtherMaterial));
        var truncated = two.Concat(new byte[] { 2, 0xAA, 0xBB }).ToArray();

        var scan = ExtraParamsScan.Read(Envelope((RenderMaterial, truncated)));

        Assert.True(scan.Complete);
        var materials = scan.RenderMaterials!;
        Assert.Equal(2, materials.Count);
        Assert.True(materials.TryGet(1, out var second));
        Assert.Equal(OtherMaterial, second);
        Assert.False(materials.TryGet(2, out _));
    }

    [Fact]
    public void AnEnvelopeThatLiesAboutItsSizeSaysNothingAboutTheBlock()
    {
        // The entry header promises 86 bytes and 10 follow: the walk breaks off, nothing is known.
        var bytes = Raw(1, EntryWithDeclaredSize(RenderMaterial, 86, new byte[10]));

        var scan = ExtraParamsScan.Read(bytes);

        Assert.False(scan.Complete);
        Assert.Null(scan.RenderMaterials);
        Assert.False(scan.RenderMaterialsKnown);
    }

    [Fact]
    public void FaceIndex255IsJustAFaceNotAllFaces()
    {
        // LLRenderMaterialParams::getMaterial is an exact match; the viewer has no "all faces" index.
        var materials = RenderMaterialParams.Read(Payload((255, SirensMaterial)));

        Assert.True(materials.TryGet(255, out _));
        Assert.False(materials.TryGet(0, out _));
    }

    [Fact]
    public void TheFirstEntryForAFaceWins()
    {
        var materials = RenderMaterialParams.Read(Payload((2, SirensMaterial), (2, OtherMaterial)));

        Assert.True(materials.TryGet(2, out var id));
        Assert.Equal(SirensMaterial, id);
    }

    [Fact]
    public void EqualityIsTheViewersFaceByFaceTest_NotTheEntryOrder()
    {
        var a = RenderMaterialParams.Read(Payload((0, SirensMaterial), (1, OtherMaterial)));
        var b = RenderMaterialParams.Read(Payload((1, OtherMaterial), (0, SirensMaterial)));
        var c = RenderMaterialParams.Read(Payload((0, SirensMaterial), (1, SirensMaterial)));

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
    }

    // --- Per-face resolution --------------------------------------------------------------------

    [Fact]
    public void AFaceTakesTheBlocksIdAndFallsBackToTheTextureEntry()
    {
        var latched = RenderMaterialParams.Read(Payload((0, SirensMaterial), (3, Guid.Empty)));
        var fromTextureEntry = Guid.NewGuid();

        Assert.Equal(SirensMaterial, GridSession.ResolveRenderMaterial(latched, 0, fromTextureEntry));
        // Listed with a null id: a firm "no material", not a fallback.
        Assert.Equal(Guid.Empty, GridSession.ResolveRenderMaterial(latched, 3, fromTextureEntry));
        // Not listed, or no block at all: whatever the TextureEntry carried (empty on Second Life).
        Assert.Equal(fromTextureEntry, GridSession.ResolveRenderMaterial(latched, 1, fromTextureEntry));
        Assert.Equal(fromTextureEntry, GridSession.ResolveRenderMaterial(null, 0, fromTextureEntry));
    }

    // --- The latch -------------------------------------------------------------------------------

    [Fact]
    public void AnUpdateWithTheBlockLatchesItAndNamesTheObject()
    {
        using var session = new GridSession();

        var changed = session.LatchObjectUpdateBlocks(RegionA, new[] { Block(9, Convert.FromHexString(SirensExtraParamsHex)) });

        Assert.Equal(new uint[] { 9 }, changed);
        Assert.Equal(5, session.RenderMaterialsLatched(RegionA, 9)!.Count);
    }

    [Fact]
    public void TheSameBlockAgainIsNoChange_ADifferentOneIs()
    {
        using var session = new GridSession();
        var first = Envelope((RenderMaterial, Payload((0, SirensMaterial))));
        session.LatchObjectUpdateBlocks(RegionA, new[] { Block(9, first) });

        Assert.Empty(session.LatchObjectUpdateBlocks(RegionA, new[] { Block(9, first) }));

        var changed = session.LatchObjectUpdateBlocks(RegionA, new[] { Block(9, Envelope((RenderMaterial, Payload((0, OtherMaterial))))) });
        Assert.Equal(new uint[] { 9 }, changed);
        Assert.True(session.RenderMaterialsLatched(RegionA, 9)!.TryGet(0, out var id));
        Assert.Equal(OtherMaterial, id);
    }

    [Fact]
    public void AFullUpdateWithoutTheBlockClearsIt()
    {
        // The viewer marks every ExtraParams entry unused before unpacking a full update and clears
        // the render material ids of any the update did not carry (llviewerobject.cpp:1555-1598).
        using var session = new GridSession();
        session.LatchObjectUpdateBlocks(RegionA, new[] { Block(9, Envelope((RenderMaterial, Payload((0, SirensMaterial))))) });

        var lightOnly = Envelope((Light, LightPayload()));
        var changed = session.LatchObjectUpdateBlocks(RegionA, new[] { Block(9, lightOnly) });

        Assert.Equal(new uint[] { 9 }, changed);
        Assert.Null(session.RenderMaterialsLatched(RegionA, 9));
        Assert.Empty(session.LatchObjectUpdateBlocks(RegionA, new[] { Block(9, lightOnly) })); // gone already
    }

    [Fact]
    public void ANullExtraParamsFieldAlsoClearsIt()
    {
        using var session = new GridSession();
        session.LatchObjectUpdateBlocks(RegionA, new[] { Block(9, Envelope((RenderMaterial, Payload((0, SirensMaterial))))) });

        Assert.Equal(new uint[] { 9 }, session.LatchObjectUpdateBlocks(RegionA, new[] { Block(9, null) }));
        Assert.Null(session.RenderMaterialsLatched(RegionA, 9));
    }

    [Fact]
    public void AMalformedBufferLeavesTheMaterialsAlone()
    {
        using var session = new GridSession();
        session.LatchObjectUpdateBlocks(RegionA, new[] { Block(9, Envelope((RenderMaterial, Payload((0, SirensMaterial))))) });

        var changed = session.LatchObjectUpdateBlocks(RegionA, new[] { Block(9, Raw(1, EntryWithDeclaredSize(0x55, int.MinValue))) });

        Assert.Empty(changed);
        Assert.NotNull(session.RenderMaterialsLatched(RegionA, 9));
    }

    [Fact]
    public void AMalformedBufferBehindTheBlockStillLatchesWhatItShowed()
    {
        // Found before the walk broke: known. Same rule as the probe and the light.
        using var session = new GridSession();
        var bytes = Raw(2,
            Entry(RenderMaterial, Payload((0, SirensMaterial))),
            EntryWithDeclaredSize(0x55, int.MinValue));

        Assert.Equal(new uint[] { 9 }, session.LatchObjectUpdateBlocks(RegionA, new[] { Block(9, bytes) }));
        Assert.NotNull(session.RenderMaterialsLatched(RegionA, 9));
    }

    [Fact]
    public void TheSameLocalIdInTwoRegionsHoldsTwoDifferentBlocks()
    {
        using var session = new GridSession();
        session.LatchRenderMaterials(RegionA, 9, RenderMaterialParams.Read(Payload((0, SirensMaterial))));
        session.LatchRenderMaterials(RegionB, 9, RenderMaterialParams.Read(Payload((0, OtherMaterial))));

        Assert.True(session.RenderMaterialsLatched(RegionA, 9)!.TryGet(0, out var a));
        Assert.True(session.RenderMaterialsLatched(RegionB, 9)!.TryGet(0, out var b));
        Assert.Equal(SirensMaterial, a);
        Assert.Equal(OtherMaterial, b);
    }

    [Fact]
    public void ForgettingAnObjectOrARegionDropsItsMaterials()
    {
        using var session = new GridSession();
        var block = RenderMaterialParams.Read(Payload((0, SirensMaterial)));
        session.LatchRenderMaterials(RegionA, 9, block);
        session.LatchRenderMaterials(RegionA, 10, block);
        session.LatchRenderMaterials(RegionB, 9, block);

        session.ForgetObjectLatches(RegionA, 9);
        Assert.Null(session.RenderMaterialsLatched(RegionA, 9));
        Assert.NotNull(session.RenderMaterialsLatched(RegionA, 10));

        session.ForgetObjectLatchesRegion(RegionA);
        Assert.Null(session.RenderMaterialsLatched(RegionA, 10));
        Assert.NotNull(session.RenderMaterialsLatched(RegionB, 9));
    }

    // --- Compressed updates and the object cache --------------------------------------------------

    [Fact]
    public void ACompressedObjectsBlockIsLatched_SoTheCacheReplayRestoresIt()
    {
        // The object cache stores raw ObjectUpdateCompressed blobs and replays them through the same
        // handler, so this is also what brings a cached object's materials back after a relog.
        using var session = new GridSession();
        var blob = CompressedExtendedMeshTests.Compressed(0, Convert.FromHexString(SirensExtraParamsHex), out _);

        Assert.True(session.LatchCompressedExtraParams(RegionA, 77, blob));
        Assert.Equal(5, session.RenderMaterialsLatched(RegionA, 77)!.Count);

        var without = CompressedExtendedMeshTests.Compressed(0, new byte[] { 0 }, out _);
        Assert.True(session.LatchCompressedExtraParams(RegionA, 77, without));
        Assert.Null(session.RenderMaterialsLatched(RegionA, 77));
    }

    [Fact]
    public void ACompressedPbrObjectReachesTheWorldWithItsMaterialOnEachFace()
    {
        // End to end through both handlers, in the order the network runs them: LibreMetaverse's own
        // compressed handler decodes the object and raises its update before the block is latched,
        // then ours latches it and raises the update again -- the one that carries the materials.
        using var session = new GridSession();
        var lmvHandler = session.FindLibreMetaverseCompressedHandler();
        Assert.NotNull(lmvHandler);
        var rawHandler = typeof(GridSession).GetMethod("OnObjectUpdateCompressedRaw", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var textureEntry = new Primitive.TextureEntry(new UUID(Guid.NewGuid())).GetBytes();
        var blob = CompressedExtendedMeshTests.Compressed(0, Convert.FromHexString(SirensExtraParamsHex), out _, textureEntry: textureEntry);
        Guid.NewGuid().ToByteArray().CopyTo(blob, 0); // a real FullID: an all-zero one is dropped as unpopulated

        var client = (GridClient)typeof(GridSession).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        var sim = new Simulator(client, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1234), RegionA);
        var packet = new ObjectUpdateCompressedPacket
        {
            RegionData = { RegionHandle = RegionA, TimeDilation = ushort.MaxValue },
            ObjectData = new[] { new ObjectUpdateCompressedPacket.ObjectDataBlock { UpdateFlags = 0, Data = blob } },
        };
        var args = new PacketReceivedEventArgs(packet, sim);
        var events = new List<ObjectUpdateEvent>();
        session.ObjectUpdateReceived += (_, e) => { lock (events) events.Add(e); };

        lmvHandler!(session, args);
        rawHandler.Invoke(session, new object[] { session, args });

        ObjectUpdateEvent last;
        lock (events)
        {
            Assert.NotEmpty(events);
            last = events[^1];
        }
        Assert.NotNull(last.Faces);
        for (int face = 0; face < 5; face++) Assert.Equal(SirensMaterial, last.Faces![face].RenderMaterialId);
        Assert.Equal(Guid.Empty, last.Faces![5].RenderMaterialId);
        Assert.Equal(SirensMaterial, last.RenderMaterialId); // face 0 stands in for the default face
    }
}
