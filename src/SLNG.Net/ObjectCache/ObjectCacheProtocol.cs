namespace SLNG.Net.ObjectCache;

/// <summary>Why an object has to be asked for, as <c>RequestMultipleObjects.CacheMissType</c> says it.
/// Numbering is the reference viewer's (<c>llviewerregion.h:356</c>).</summary>
internal enum CacheMissType : byte { Total = 0, Crc = 1 }

internal readonly record struct CacheMiss(uint LocalId, CacheMissType Type);

/// <summary>The numbers of the object-cache conversation between viewer and simulator.</summary>
internal static class ObjectCacheProtocol
{
    /// <summary>"Send me all cacheable objects" -- as probes, for the ones I may hold.</summary>
    public const uint HandshakeCulling = 0x1;

    /// <summary>"My cache is empty, don't bother probing": send everything in full.</summary>
    public const uint HandshakeCacheEmpty = 0x2;

    /// <summary>"I can show my own appearance as the simulator baked it"
    /// (<c>REGION_HANDSHAKE_SUPPORTS_SELF_APPEARANCE</c>).</summary>
    public const uint HandshakeSelfAppearance = 0x4;

    /// <summary>A <c>RequestMultipleObjects</c> message holds at most this many blocks.</summary>
    public const int MaxBlocksPerRequest = 255;

    /// <summary>The <c>RegionHandshakeReply</c> flags for a region whose cache does or does not hold
    /// anything yet. LibreMetaverse always sends <c>0x7</c> -- empty -- which is what the simulator
    /// has heard from SLNG so far.</summary>
    public static uint HandshakeFlags(bool cacheIsEmpty)
        => HandshakeCulling | HandshakeSelfAppearance | (cacheIsEmpty ? HandshakeCacheEmpty : 0u);

    /// <summary>The misses cut into request messages, in the order they were found.</summary>
    public static IEnumerable<IReadOnlyList<CacheMiss>> Messages(IReadOnlyList<CacheMiss> misses)
    {
        for (int i = 0; i < misses.Count; i += MaxBlocksPerRequest)
        {
            int count = Math.Min(MaxBlocksPerRequest, misses.Count - i);
            var message = new CacheMiss[count];
            for (int j = 0; j < count; j++) message[j] = misses[i + j];
            yield return message;
        }
    }
}
