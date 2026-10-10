using System.Reflection;
using LibreMetaverse;
using LibreMetaverse.Packets;
using SLNG.Core;
using SLNG.Net.ObjectCache;
using Xunit;

using Xunit.Abstractions;

namespace SLNG.Net.Tests;

public class PrimShapeRoundTripTests
{
    private readonly ITestOutputHelper _output;

    public PrimShapeRoundTripTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private const ulong RegionHandle = 1000UL;

    [Fact]
    public void CompressedUpdate_DecodesAllNonDefaultShapeFields()
    {
        using var session = new GridSession();
        var handler = session.FindLibreMetaverseCompressedHandler();
        Assert.NotNull(handler);

        // Build a prim with non-default values for ALL shape fields
        byte profileCurveRaw = 0x23; // EquilateralTriangle (0x03) | Square hole (0x20)
        byte pathCurve = (byte)PathCurve.Line;
        float wantPathBegin = 0.10f;
        float wantPathEnd = 0.85f;
        float wantPathScaleX = 0.40f;
        float wantPathScaleY = 0.60f;
        float wantPathShearX = 0.25f;
        float wantPathShearY = -0.30f;
        float wantPathTwist = 0.40f;
        float wantPathTwistBegin = -0.50f;
        float wantPathRadiusOffset = 0.20f;
        float wantPathTaperX = 0.50f;
        float wantPathTaperY = -0.50f;
        float wantPathRevolutions = 2.0f;
        float wantPathSkew = -0.35f;
        float wantProfileBegin = 0.05f;
        float wantProfileEnd = 0.95f;
        float wantProfileHollow = 0.30f;

        // Pack them wire-style
        var blockData = new List<byte>();
        blockData.AddRange(Guid.NewGuid().ToByteArray()); // FullID (16)
        blockData.AddRange(BitConverter.GetBytes(12345u)); // LocalID (4)
        blockData.Add((byte)PCode.Prim);                  // PCode (1)
        blockData.Add(0);                                 // State (1)
        blockData.AddRange(BitConverter.GetBytes(9999u)); // CRC (4)
        blockData.Add(0);                                 // Material (1)
        blockData.Add(0);                                 // ClickAction (1)
        blockData.AddRange(new byte[12]);                 // Scale (12)
        blockData.AddRange(new byte[12]);                 // Position (12)
        blockData.AddRange(new byte[12]);                 // Rotation (12)
        blockData.AddRange(BitConverter.GetBytes(0u));    // Flags (4)
        blockData.AddRange(Guid.NewGuid().ToByteArray()); // OwnerID (16)

        // No optional sections (angular velocity, parent, tree, text, media, particles)
        // ExtraParams: 0 blocks
        blockData.Add(0);

        // Path params (16 bytes)
        blockData.Add(pathCurve);
        blockData.AddRange(BitConverter.GetBytes(Primitive.PackBeginCut(wantPathBegin)));
        blockData.AddRange(BitConverter.GetBytes(Primitive.PackEndCut(wantPathEnd)));
        blockData.Add(Primitive.PackPathScale(wantPathScaleX));
        blockData.Add(Primitive.PackPathScale(wantPathScaleY));
        blockData.Add((byte)Primitive.PackPathShear(wantPathShearX));
        blockData.Add((byte)Primitive.PackPathShear(wantPathShearY));
        blockData.Add((byte)Primitive.PackPathTwist(wantPathTwist));
        blockData.Add((byte)Primitive.PackPathTwist(wantPathTwistBegin));
        blockData.Add((byte)Primitive.PackPathTwist(wantPathRadiusOffset));
        blockData.Add((byte)Primitive.PackPathTaper(wantPathTaperX));
        blockData.Add((byte)Primitive.PackPathTaper(wantPathTaperY));
        blockData.Add(Primitive.PackPathRevolutions(wantPathRevolutions));
        blockData.Add((byte)Primitive.PackPathTwist(wantPathSkew));

        // Profile params (7 bytes)
        blockData.Add(profileCurveRaw);
        blockData.AddRange(BitConverter.GetBytes(Primitive.PackBeginCut(wantProfileBegin)));
        blockData.AddRange(BitConverter.GetBytes(Primitive.PackEndCut(wantProfileEnd)));
        blockData.AddRange(BitConverter.GetBytes(Primitive.PackProfileHollow(wantProfileHollow)));

        // Texture entry length: 0
        blockData.AddRange(BitConverter.GetBytes(0u));

        // Wire it into an ObjectUpdateCompressedPacket
        var packet = new ObjectUpdateCompressedPacket
        {
            RegionData = { RegionHandle = RegionHandle, TimeDilation = ushort.MaxValue },
            ObjectData = new[]
            {
                new ObjectUpdateCompressedPacket.ObjectDataBlock
                {
                    UpdateFlags = 0,
                    Data = blockData.ToArray()
                }
            }
        };

        var clientField = typeof(GridSession).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic);
        var client = (GridClient)clientField!.GetValue(session)!;
        var sim = new Simulator(client, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1234), RegionHandle);

        ObjectUpdateEvent? received = null;
        session.ObjectUpdateReceived += (s, e) => received = e;

        handler!(session, new PacketReceivedEventArgs(packet, sim));

        Assert.NotNull(received);
        var shape = received!.Shape;

        Assert.Equal(profileCurveRaw, shape.ProfileCurve);
        Assert.Equal(pathCurve, shape.PathCurve);
        Assert.True(Math.Abs(wantPathBegin - shape.PathBegin) < 0.01f);
        Assert.True(Math.Abs(wantPathEnd - shape.PathEnd) < 0.01f);
        Assert.True(Math.Abs(wantPathScaleX - shape.PathScaleX) < 0.01f);
        Assert.True(Math.Abs(wantPathScaleY - shape.PathScaleY) < 0.01f);
        Assert.True(Math.Abs(wantPathShearX - shape.PathShearX) < 0.02f);
        Assert.True(Math.Abs(wantPathShearY - shape.PathShearY) < 0.02f);
        Assert.True(Math.Abs(wantPathTwist - shape.PathTwist) < 0.02f);
        Assert.True(Math.Abs(wantPathTwistBegin - shape.PathTwistBegin) < 0.02f);
        Assert.True(Math.Abs(wantPathRadiusOffset - shape.PathRadiusOffset) < 0.02f);
        Assert.True(Math.Abs(wantPathTaperX - shape.PathTaperX) < 0.02f);
        Assert.True(Math.Abs(wantPathTaperY - shape.PathTaperY) < 0.02f);
        Assert.True(Math.Abs(wantPathRevolutions - shape.PathRevolutions) < 0.02f);
        Assert.True(Math.Abs(wantPathSkew - shape.PathSkew) < 0.02f);
        Assert.True(Math.Abs(wantProfileBegin - shape.ProfileBegin) < 0.01f);
        Assert.True(Math.Abs(wantProfileEnd - shape.ProfileEnd) < 0.01f);
        Assert.True(Math.Abs(wantProfileHollow - shape.ProfileHollow) < 0.01f);
    }

    private static byte[] BuildBlockWithShape(byte profileCurve, byte pathCurve, float pathScale, float profileBegin, float profileEnd, float profileHollow)
    {
        var blockData = new List<byte>();
        blockData.AddRange(Guid.NewGuid().ToByteArray()); // FullID (16)
        blockData.AddRange(BitConverter.GetBytes(12345u)); // LocalID (4)
        blockData.Add((byte)PCode.Prim);                  // PCode (1)
        blockData.Add(0);                                 // State (1)
        blockData.AddRange(BitConverter.GetBytes(9999u)); // CRC (4)
        blockData.Add(0);                                 // Material (1)
        blockData.Add(0);                                 // ClickAction (1)
        blockData.AddRange(new byte[12]);                 // Scale (12)
        blockData.AddRange(new byte[12]);                 // Position (12)
        blockData.AddRange(new byte[12]);                 // Rotation (12)
        blockData.AddRange(BitConverter.GetBytes(0u));    // Flags (4)
        blockData.AddRange(Guid.NewGuid().ToByteArray()); // OwnerID (16)
        blockData.Add(0);                                 // ExtraParams count (0)

        // Path params (16 bytes)
        blockData.Add(pathCurve);
        blockData.AddRange(BitConverter.GetBytes(Primitive.PackBeginCut(0f)));
        blockData.AddRange(BitConverter.GetBytes(Primitive.PackEndCut(1f)));
        blockData.Add(Primitive.PackPathScale(pathScale));
        blockData.Add(Primitive.PackPathScale(pathScale));
        blockData.AddRange(new byte[9]);                  // Shear, twist, taper, etc.

        // Profile params (7 bytes)
        blockData.Add(profileCurve);
        blockData.AddRange(BitConverter.GetBytes(Primitive.PackBeginCut(profileBegin)));
        blockData.AddRange(BitConverter.GetBytes(Primitive.PackEndCut(profileEnd)));
        blockData.AddRange(BitConverter.GetBytes(Primitive.PackProfileHollow(profileHollow)));

        // Texture entry length
        blockData.AddRange(BitConverter.GetBytes(0u));

        return blockData.ToArray();
    }

    [Fact]
    public void IsUntrustedShape_NullOrTruncated_ReturnsTrue()
    {
        Assert.True(CompressedObjectBlock.IsUntrustedShape(null));
        Assert.True(CompressedObjectBlock.IsUntrustedShape(Array.Empty<byte>()));
        Assert.True(CompressedObjectBlock.IsUntrustedShape(new byte[40]));
    }

    [Fact]
    public void IsUntrustedShape_ZeroPathCurve_ReturnsTrue()
    {
        var zeroPath = BuildBlockWithShape(profileCurve: 0x01, pathCurve: 0, pathScale: 1f, profileBegin: 0f, profileEnd: 1f, profileHollow: 0f);
        Assert.True(CompressedObjectBlock.IsUntrustedShape(zeroPath));
    }

    [Fact]
    public void IsUntrustedShape_ValidShapesThatLookDefault_ReturnFalse()
    {
        // Profile 0 is LL_PCODE_PROFILE_CIRCLE (llvolume.h:143): a cylinder, not an unpopulated shape.
        var cylinder = BuildBlockWithShape(profileCurve: 0x00, pathCurve: (byte)PathCurve.Line, pathScale: 1f, profileBegin: 0f, profileEnd: 1f, profileHollow: 0f);
        Assert.False(CompressedObjectBlock.IsUntrustedShape(cylinder));

        // A plain uncut prism is a real prism.
        var prism = BuildBlockWithShape(profileCurve: 0x03, pathCurve: (byte)PathCurve.Line, pathScale: 1f, profileBegin: 0f, profileEnd: 1f, profileHollow: 0f);
        Assert.False(CompressedObjectBlock.IsUntrustedShape(prism));

        // A hole type with no hollow is a valid state.
        var holeTypeNoHollow = BuildBlockWithShape(profileCurve: 0x21, pathCurve: (byte)PathCurve.Line, pathScale: 1f, profileBegin: 0f, profileEnd: 1f, profileHollow: 0f);
        Assert.False(CompressedObjectBlock.IsUntrustedShape(holeTypeNoHollow));
    }

    [Fact]
    public void IsUntrustedShape_ConfiguredShapes_ReturnFalse()
    {
        // Standard box
        var standardBox = BuildBlockWithShape(profileCurve: 0x01, pathCurve: (byte)PathCurve.Line, pathScale: 1f, profileBegin: 0f, profileEnd: 1f, profileHollow: 0f);
        Assert.False(CompressedObjectBlock.IsUntrustedShape(standardBox));

        // Chalkboard glyph: triangle profile with cut and hollow
        var chalkboardGlyph = BuildBlockWithShape(profileCurve: 0x23, pathCurve: (byte)PathCurve.Line, pathScale: 1f, profileBegin: 0.2f, profileEnd: 0.8f, profileHollow: 0.3f);
        Assert.False(CompressedObjectBlock.IsUntrustedShape(chalkboardGlyph));

        // Cut triangle (e.g. roof or prism)
        var cutTriangle = BuildBlockWithShape(profileCurve: 0x03, pathCurve: (byte)PathCurve.Line, pathScale: 1f, profileBegin: 0.1f, profileEnd: 0.9f, profileHollow: 0f);
        Assert.False(CompressedObjectBlock.IsUntrustedShape(cutTriangle));
    }
}

