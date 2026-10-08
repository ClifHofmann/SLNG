using System.Buffers.Binary;

namespace SLNG.Net;

/// <summary>
/// FEAT-NET-05: the seven AgentThrottle categories in bits per second, in the order the simulator
/// reads them (llviewerthrottle.cpp sNames: Resend, Land, Wind, Cloud, Task, Texture, Asset).
/// <c>Task</c> is the one that carries <c>ObjectUpdate*</c>, so it sets how fast a region's objects
/// stream in. A record (a reference) so the session can swap it whole while a network thread reads it.
/// </summary>
public sealed record ThrottleRates(float Resend, float Land, float Wind, float Cloud, float Task, float Texture, float Asset)
{
    public float Total => Resend + Land + Wind + Cloud + Task + Texture + Asset;

    /// <summary>The packet's <c>Throttles</c> field: seven little-endian floats, unclamped -- unlike
    /// LibreMetaverse's <c>AgentThrottle.ToBytes</c>, whose setters cap each category well below what
    /// the reference viewer asks for.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[7 * sizeof(float)];
        float[] values = { Resend, Land, Wind, Cloud, Task, Texture, Asset };
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), values[i]);
        return bytes;
    }
}
