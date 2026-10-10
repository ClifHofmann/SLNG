namespace SLNG.Net.ObjectCache;

/// <summary>
/// One region's object cache on disk (FEAT-NET-04, phase 2).
///
/// <para>Little-endian. Header: magic <c>SLNGOBJC</c>(8), version(4), region handle(8), cache id(16),
/// saved at Unix seconds(8), entry count(4). Then per entry: local id(4), CRC(4), update flags(4),
/// length(4), the block. Last, a 32-bit FNV-1a of everything before it.</para>
///
/// <para>A cache file is read long after it was written, by a build that may be newer, from a disk
/// that may have lost half of it. So reading is all-or-nothing: the checksum has to match, the
/// header has to name this very region and cache id, and every length has to fit the file before
/// anything is allocated for it. Whatever fails is an empty cache, never an exception and never a
/// wrong object.</para>
/// </summary>
internal static class ObjectCacheFile
{
    public const string Extension = ".slobj";

    // 2: BUG-RENDER-47 -- blocks cached before uncompressed updates evicted them can hold a stale
    // shape; bumping the version drops every cache file written before the fix, once.
    private const int Version = 2;
    private static readonly byte[] Magic = "SLNGOBJC"u8.ToArray();

    private const int HeaderLength = 8 + 4 + 8 + 16 + 8 + 4;
    private const int SavedOffset = 8 + 4 + 8 + 16;
    private const int EntryHeaderLength = 16;
    private const int TrailerLength = 4;

    /// <summary>A compressed object block is a few hundred bytes; the largest ones a couple of
    /// kilobytes. Anything past this is not one.</summary>
    private const int MaxBlockLength = 64 * 1024;

    /// <summary>More objects than any region holds; a count past it is damage, and it is checked
    /// before it can size an allocation.</summary>
    private const int MaxEntries = 4_000_000;

    private const long MaxFileLength = 512L * 1024 * 1024;

    public static string FileName(RegionKey key) => $"{key.Handle:x16}-{key.CacheId:N}{Extension}";

    public static void Write(Stream stream, RegionKey key, IReadOnlyCollection<CachedObject> objects, DateTimeOffset saved)
    {
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(key.Handle);
            writer.Write(key.CacheId.ToByteArray());
            writer.Write(saved.ToUnixTimeSeconds());
            writer.Write(objects.Count);
            foreach (var obj in objects)
            {
                writer.Write(obj.LocalId);
                writer.Write(obj.Crc);
                writer.Write(obj.UpdateFlags);
                writer.Write(obj.Block.Length);
                writer.Write(obj.Block);
            }
        }

        var bytes = body.GetBuffer().AsSpan(0, (int)body.Length);
        stream.Write(bytes);
        stream.Write(BitConverter.GetBytes(Checksum(bytes)));
    }

    /// <summary>Reads the file if, and only if, it is whole and is this region's. On failure
    /// <paramref name="objects"/> is empty.</summary>
    public static bool TryRead(Stream stream, RegionKey key, out List<CachedObject> objects)
    {
        objects = new List<CachedObject>();
        try
        {
            if (!TryReadAll(stream, out var bytes)) return false;
            if (bytes.Length < HeaderLength + TrailerLength) return false;

            int bodyLength = bytes.Length - TrailerLength;
            if (BitConverter.ToUInt32(bytes, bodyLength) != Checksum(bytes.AsSpan(0, bodyLength))) return false;
            if (!bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return false;
            if (BitConverter.ToInt32(bytes, 8) != Version) return false;
            if (BitConverter.ToUInt64(bytes, 12) != key.Handle) return false;
            if (new Guid(bytes.AsSpan(20, 16)) != key.CacheId) return false;

            int count = BitConverter.ToInt32(bytes, HeaderLength - 4);
            if (count < 0 || count > MaxEntries) return false;
            if ((long)count * EntryHeaderLength > bodyLength - HeaderLength) return false;

            var read = new List<CachedObject>(count);
            int pos = HeaderLength;
            for (int i = 0; i < count; i++)
            {
                if (bodyLength - pos < EntryHeaderLength) return false;
                uint localId = BitConverter.ToUInt32(bytes, pos);
                uint crc = BitConverter.ToUInt32(bytes, pos + 4);
                uint flags = BitConverter.ToUInt32(bytes, pos + 8);
                int length = BitConverter.ToInt32(bytes, pos + 12);
                pos += EntryHeaderLength;
                if (length < 0 || length > MaxBlockLength || length > bodyLength - pos) return false;
                read.Add(new CachedObject(localId, crc, flags, bytes.AsSpan(pos, length).ToArray()));
                pos += length;
            }
            if (pos != bodyLength) return false;

            objects = read;
            return true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or OverflowException)
        {
            objects = new List<CachedObject>();
            return false;
        }
    }

    /// <summary>When the file was written, read from the header alone: which regions to drop first
    /// when the cache is over its budget needs no objects decoded.</summary>
    public static bool TryReadSaved(Stream stream, out DateTimeOffset saved)
    {
        saved = default;
        var head = new byte[HeaderLength];
        int read = 0;
        while (read < head.Length)
        {
            int n = stream.Read(head, read, head.Length - read);
            if (n <= 0) return false;
            read += n;
        }
        if (!head.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return false;
        if (BitConverter.ToInt32(head, 8) != Version) return false;

        saved = DateTimeOffset.FromUnixTimeSeconds(BitConverter.ToInt64(head, SavedOffset));
        return true;
    }

    private static bool TryReadAll(Stream stream, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        long length = stream.CanSeek ? stream.Length - stream.Position : -1;
        if (length > MaxFileLength) return false;

        using var copy = new MemoryStream(length > 0 ? (int)length : 0);
        stream.CopyTo(copy);
        if (copy.Length > MaxFileLength) return false;
        bytes = copy.ToArray();
        return true;
    }

    /// <summary>FNV-1a, 32 bit: not a defence against anyone, a check that the file is as it was
    /// written.</summary>
    private static uint Checksum(ReadOnlySpan<byte> bytes)
    {
        uint hash = 2166136261;
        foreach (var b in bytes)
        {
            hash ^= b;
            hash *= 16777619;
        }
        return hash;
    }
}
