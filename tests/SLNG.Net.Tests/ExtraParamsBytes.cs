namespace SLNG.Net.Tests;

/// <summary>
/// Hand-built ExtraParams byte sequences for the latch tests (BUG-NET-25): <c>U8 count</c>, then
/// per entry <c>U16 type</c>, <c>S32 size</c>, <c>size</c> payload bytes, all little-endian
/// (lldatapacker.cpp:292-334). Shared so the three block kinds are assembled one way only.
/// </summary>
internal static class ExtraParamsBytes
{
    internal const ushort Flexible = 0x10;
    internal const ushort Light = 0x20;
    internal const ushort ExtendedMesh = 0x70;
    internal const ushort ReflectionProbe = 0x90;

    internal static byte[] Envelope(params (ushort Type, byte[] Payload)[] blocks)
    {
        var bytes = new List<byte> { (byte)blocks.Length };
        foreach (var (type, payload) in blocks)
        {
            bytes.AddRange(BitConverter.GetBytes(type));
            bytes.AddRange(BitConverter.GetBytes(payload.Length)); // S32
            bytes.AddRange(payload);
        }
        return bytes.ToArray();
    }

    /// <summary>An entry header that promises <paramref name="declaredSize"/> payload bytes and is
    /// followed by only <paramref name="actualPayload"/> of them -- the lie a corrupt or hostile
    /// packet tells. Counted as one entry by <paramref name="count"/>.</summary>
    internal static byte[] EntryWithDeclaredSize(ushort type, int declaredSize, params byte[] actualPayload)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(type));
        bytes.AddRange(BitConverter.GetBytes(declaredSize));
        bytes.AddRange(actualPayload);
        return bytes.ToArray();
    }

    /// <summary>A raw envelope: the count byte followed by already-encoded entries.</summary>
    internal static byte[] Raw(byte count, params byte[][] entries)
    {
        var bytes = new List<byte> { count };
        foreach (var entry in entries) bytes.AddRange(entry);
        return bytes.ToArray();
    }

    internal static byte[] Entry(ushort type, byte[] payload) => EntryWithDeclaredSize(type, payload.Length, payload);

    internal static byte[] LightPayload() => new byte[16];

    internal static byte[] ProbePayload(float ambiance, float clipDistance, byte flags)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(ambiance));
        bytes.AddRange(BitConverter.GetBytes(clipDistance));
        bytes.Add(flags);
        return bytes.ToArray();
    }

    internal static byte[] MeshFlags(uint flags) => BitConverter.GetBytes(flags);
}
