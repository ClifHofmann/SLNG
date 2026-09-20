namespace SLNG.Net;

/// <summary>Builds the one <c>MultipleObjectUpdate</c> data block a transform edit travels in.</summary>
/// <remarks>
/// Split out of <see cref="GridSession.UpdateObjectTransform"/> because the whole correctness of
/// an in-world edit sits in these few bytes and nothing else about them is observable: the
/// simulator reads the block positionally, so a field in the wrong order or a missing bit in the
/// type mask is not an error anywhere -- it is an object that moves somewhere nobody asked for.
///
/// The layout is the reference viewer's, from <c>LLSelectMgr::packMultipleUpdate</c>
/// (llselectmgr.cpp): position, then rotation, then scale, twelve bytes each, and only the ones
/// the type mask names. The mask values are its <c>UPD_*</c> constants.
/// </remarks>
public static class TransformUpdatePayload
{
    public const byte UpdPosition = 0x01;
    public const byte UpdRotation = 0x02;
    public const byte UpdScale = 0x04;

    /// <summary>"Apply this to the whole linked set." The viewer sets it whenever "edit linked
    /// parts" is off, which includes an attachment -- its root is the attachment, not the
    /// avatar.</summary>
    public const byte UpdLinkedSets = 0x08;

    /// <summary>"The scale is uniform." Carries no bytes of its own, and it is not cosmetic: a
    /// GROUP scale without it resizes the root prim ALONE, and only with it does the simulator
    /// scale every part of the linkset. OpenSim's handler says so in as many words -- case 0x0C
    /// is commented "only afects root prim and only sent by viewer editor object tab scaling /
    /// mouse edition only allows uniform scaling", while 0x1C and 0x1D are the uniform group
    /// cases (LLClientView.HandleMultipleObjUpdate). The reference viewer sets it for a CORNER
    /// drag and never for a face one, and correspondingly refuses to show face handles at all
    /// once more than one prim is selected (LLManipScale::renderFaces).</summary>
    public const byte UpdUniform = 0x10;

    public static (byte Type, byte[] Data) Build(
        TransformFields fields, bool singlePrim,
        System.Numerics.Vector3 position, System.Numerics.Quaternion rotation, System.Numerics.Vector3 scale,
        bool uniformScale = false)
    {
        byte type = 0;
        if (fields.HasFlag(TransformFields.Position)) type |= UpdPosition;
        if (fields.HasFlag(TransformFields.Rotation)) type |= UpdRotation;
        if (fields.HasFlag(TransformFields.Scale)) type |= UpdScale;
        if (!singlePrim) type |= UpdLinkedSets;
        if (uniformScale && fields.HasFlag(TransformFields.Scale)) type |= UpdUniform;

        var data = new System.Collections.Generic.List<byte>(36);
        if (fields.HasFlag(TransformFields.Position)) data.AddRange(Vector(position));
        // Twelve bytes, not sixteen: the quaternion is normalised and its W dropped, to be
        // recovered from the sign -- what the viewer writes with packToVector3().
        if (fields.HasFlag(TransformFields.Rotation)) data.AddRange(PackedRotation(rotation));
        if (fields.HasFlag(TransformFields.Scale)) data.AddRange(Vector(scale));

        return (type, data.ToArray());
    }

    private static byte[] Vector(System.Numerics.Vector3 v)
    {
        var bytes = new byte[12];
        System.BitConverter.TryWriteBytes(bytes.AsSpan(0), v.X);
        System.BitConverter.TryWriteBytes(bytes.AsSpan(4), v.Y);
        System.BitConverter.TryWriteBytes(bytes.AsSpan(8), v.Z);
        return bytes;
    }

    private static byte[] PackedRotation(System.Numerics.Quaternion q)
    {
        float norm = System.MathF.Sqrt((q.X * q.X) + (q.Y * q.Y) + (q.Z * q.Z) + (q.W * q.W));
        var packed = System.Numerics.Vector3.Zero;
        if (norm > 0f)
        {
            float inverse = 1f / norm;
            packed = new System.Numerics.Vector3(q.X * inverse, q.Y * inverse, q.Z * inverse);
            // A quaternion and its negation are the same rotation; the wire form keeps the one
            // with a non-negative W so the receiver can rebuild W from the other three.
            if (q.W * inverse < 0f) packed = -packed;
        }
        return Vector(packed);
    }
}
