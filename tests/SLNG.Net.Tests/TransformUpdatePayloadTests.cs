using System.Numerics;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>FEAT-UI-23. A transform edit is one <c>MultipleObjectUpdate</c> whose data block the
/// simulator reads POSITIONALLY: there is no field name on the wire, only a type mask and a run
/// of bytes. A field in the wrong order, a wrong length, or a missing bit is therefore not an
/// error anywhere — it is an object that ends up somewhere nobody asked for, which is exactly how
/// this arrived: "I stretch it and it's gone."
///
/// The layout under test is the reference viewer's, from <c>LLSelectMgr::packMultipleUpdate</c>.
/// </summary>
public class TransformUpdatePayloadTests
{
    private static readonly Vector3 Position = new(1.5f, -2.25f, 3.125f);
    private static readonly Vector3 Scale = new(0.5f, 0.75f, 2f);

    private static Vector3 ReadVector(byte[] data, int offset) => new(
        System.BitConverter.ToSingle(data, offset),
        System.BitConverter.ToSingle(data, offset + 4),
        System.BitConverter.ToSingle(data, offset + 8));

    [Fact]
    public void AStretchCarriesPositionAndScaleInThatOrder()
    {
        var (type, data) = TransformUpdatePayload.Build(
            TransformFields.Position | TransformFields.Scale, singlePrim: false,
            Position, Quaternion.Identity, Scale);

        Assert.Equal(TransformUpdatePayload.UpdPosition | TransformUpdatePayload.UpdScale
                     | TransformUpdatePayload.UpdLinkedSets, type);
        Assert.Equal(24, data.Length);
        Assert.Equal(Position, ReadVector(data, 0));
        Assert.Equal(Scale, ReadVector(data, 12));
    }

    [Fact]
    public void AMoveCarriesNothingButThePosition()
    {
        var (type, data) = TransformUpdatePayload.Build(
            TransformFields.Position, singlePrim: false, Position, Quaternion.Identity, Scale);

        Assert.Equal(TransformUpdatePayload.UpdPosition | TransformUpdatePayload.UpdLinkedSets, type);
        Assert.Equal(12, data.Length);
        Assert.Equal(Position, ReadVector(data, 0));
    }

    /// <summary>All three, to pin the ORDER — position, rotation, scale. Getting rotation and
    /// scale the wrong way round would resize an object to its own quaternion.</summary>
    [Fact]
    public void AllThreeGoPositionRotationScale()
    {
        var rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.1f));

        var (type, data) = TransformUpdatePayload.Build(
            TransformFields.All, singlePrim: false, Position, rotation, Scale);

        Assert.Equal(TransformUpdatePayload.UpdPosition | TransformUpdatePayload.UpdRotation
                     | TransformUpdatePayload.UpdScale | TransformUpdatePayload.UpdLinkedSets, type);
        Assert.Equal(36, data.Length);
        Assert.Equal(Position, ReadVector(data, 0));
        Assert.Equal(new Vector3(rotation.X, rotation.Y, rotation.Z), ReadVector(data, 12));
        Assert.Equal(Scale, ReadVector(data, 24));
    }

    /// <summary>The wire form drops W and recovers it from the sign, so a quaternion whose W is
    /// negative must be sent negated — the same rotation, expressed the way the receiver can
    /// rebuild. Sent as-is, it comes back as the rotation's opposite.</summary>
    [Fact]
    public void ANegativeWQuaternionIsSentNegated()
    {
        var rotation = new Quaternion(0.2f, 0.3f, 0.4f, -0.84261f);
        var (_, data) = TransformUpdatePayload.Build(
            TransformFields.Rotation, singlePrim: false, Position, rotation, Scale);

        var packed = ReadVector(data, 0);
        Assert.True(packed.X < 0 && packed.Y < 0 && packed.Z < 0);
        // still a unit quaternion once W is put back
        float w = System.MathF.Sqrt(System.MathF.Max(0f, 1f - packed.LengthSquared()));
        var rebuilt = new Quaternion(packed.X, packed.Y, packed.Z, w);
        Assert.Equal(1f, rebuilt.Length(), 3);
    }

    /// <summary>A CORNER stretch is uniform, and the bit that says so is what makes the
    /// simulator resize a whole linkset instead of its root prim alone. OpenSim's handler spells
    /// it out: 0x0D is "group scale and position" and only affects the root, 0x1D is the uniform
    /// one (LLClientView.HandleMultipleObjUpdate). The reference viewer sets it for corner drags
    /// only -- and hides the face handles entirely once more than one prim is selected.</summary>
    [Fact]
    public void AUniformStretchCarriesTheUniformBit()
    {
        var (type, data) = TransformUpdatePayload.Build(
            TransformFields.Position | TransformFields.Scale, singlePrim: false,
            Position, Quaternion.Identity, Scale, uniformScale: true);

        Assert.Equal(0x1D, type);
        Assert.Equal(24, data.Length); // the bit carries no bytes of its own
    }

    /// <summary>...and it is meaningless without a scale, so it is not set on a move.</summary>
    [Fact]
    public void AMoveNeverCarriesTheUniformBit()
    {
        var (type, _) = TransformUpdatePayload.Build(
            TransformFields.Position, singlePrim: false,
            Position, Quaternion.Identity, Scale, uniformScale: true);

        Assert.Equal(TransformUpdatePayload.UpdPosition | TransformUpdatePayload.UpdLinkedSets, type);
    }

    /// <summary>"Edit linked parts" is the one case that does NOT carry the linked-sets bit: the
    /// update then means this prim alone, inside its linkset.</summary>
    [Fact]
    public void ASinglePrimUpdateDropsTheLinkedSetsBit()
    {
        var (type, _) = TransformUpdatePayload.Build(
            TransformFields.Position, singlePrim: true, Position, Quaternion.Identity, Scale);

        Assert.Equal(TransformUpdatePayload.UpdPosition, type);
    }

    /// <summary>Tied to LibreMetaverse's own encoder rather than to my reading of it: this is
    /// hand-rolled packing of a wire format, and the library already ships the same bytes
    /// (Quaternion.ToBytes). If the two ever disagree, the one that is wrong is ours.</summary>
    [Theory]
    [InlineData(0f, 0f, 0f, 1f)]
    [InlineData(0.2f, 0.3f, 0.4f, -0.84261f)]
    [InlineData(0f, 0.70710677f, 0f, 0.70710677f)]
    [InlineData(0.5f, -0.5f, 0.5f, -0.5f)]
    public void ThePackedRotationMatchesLibreMetaversesOwnEncoding(float x, float y, float z, float w)
    {
        var (_, data) = TransformUpdatePayload.Build(
            TransformFields.Rotation, singlePrim: false,
            Position, new Quaternion(x, y, z, w), Scale);

        var expected = new LibreMetaverse.Quaternion(x, y, z, w).GetBytes();

        Assert.Equal(expected.Length, data.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], data[i]);
    }

    [Fact]
    public void AnUnnormalisedQuaternionIsNormalisedFirst()
    {
        var rotation = new Quaternion(0.4f, 0f, 0f, 0.4f); // length 0.566, not 1
        var (_, data) = TransformUpdatePayload.Build(
            TransformFields.Rotation, singlePrim: false, Position, rotation, Scale);

        Assert.Equal(System.MathF.Sqrt(0.5f), ReadVector(data, 0).X, 4);
    }
}
