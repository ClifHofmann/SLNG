using SLNG.Net;

namespace SLNG.Assets;

/// <summary>
/// FEAT-PERF-29: one step of progressive texture streaming for one texture -- fetch the bytes the face needs
/// that are not in hand yet, keep them, and decode what they cover.
///
/// <list type="bullet">
/// <item>Nothing in hand: the coarse first request (<see cref="TextureStreaming.FirstRequestBytes"/>).</item>
/// <item>A prefix in hand (<c>&lt;id&gt;_v5.part</c>): its header gives the size, and the request asks for the rest
/// of what <see cref="TextureStreaming.TargetDiscard"/> needs -- straight to that discard, not level by level.
/// When the prefix already covers it, no request at all.</item>
/// <item>The asset ends with the response: the bytes become <c>&lt;id&gt;_v5.j2c</c>, the whole-asset cache file,
/// and the caller decodes that as any cached asset.</item>
/// </list>
///
/// <para>A prefix decodes only on the reduced path (<see cref="AssetService.TryDecodeReduced"/>) at the discard its
/// bytes cover. Magick refuses every cut and CoreJ2K at full resolution reconstructs a blur flagged degraded --
/// that is what made FEAT-PERF-02 Phase 2's partial streams look broken. The result is
/// <see cref="TextureData.IsPartial"/>, never degraded.</para>
///
/// <para>Not thread-safe per texture id; <see cref="AssetService"/> runs at most one fetch per id at a time.</para>
/// </summary>
internal sealed class TextureStreamer
{
    internal delegate Task<TextureRangeResult?> RangeFetch(Guid textureId, long start, long? end, float priority);

    internal enum Outcome
    {
        /// <summary><see cref="Result.Data"/> is a reduced decode of a prefix.</summary>
        Partial,
        /// <summary>The whole asset is now in the cache file; decode it from there.</summary>
        Complete,
        /// <summary>The region refused the texture for good.</summary>
        Gone,
        /// <summary>Streaming did not work out for this texture: fetch it in full.</summary>
        FellBack,
    }

    internal readonly record struct Result(Outcome Outcome, TextureData? Data = null);

    private readonly string _cacheDir;
    private readonly RangeFetch _fetch;
    private readonly Cache.DiskCacheManager? _diskCache;

    internal TextureStreamer(string cacheDir, RangeFetch fetch, Cache.DiskCacheManager? diskCache = null)
    {
        _cacheDir = cacheDir;
        _fetch = fetch;
        _diskCache = diskCache;
    }

    internal string PartPath(Guid id) => Path.Combine(_cacheDir, id + "_v5.part");
    internal string J2cPath(Guid id) => Path.Combine(_cacheDir, id + "_v5.j2c");

    /// <summary>Drops the prefix held for <paramref name="textureId"/>: the whole asset was cached another way.</summary>
    internal void Discard(Guid textureId) => Delete(PartPath(textureId));

    internal async Task<Result> FetchAsync(Guid textureId, float screenPixelArea, float priority)
    {
        string partFile = PartPath(textureId);
        byte[] held = Array.Empty<byte>();
        try
        {
            if (File.Exists(partFile))
            {
                held = await File.ReadAllBytesAsync(partFile).ConfigureAwait(false);
                _diskCache?.RecordAccess(partFile);
            }
        }
        catch (IOException) { held = Array.Empty<byte>(); }

        int wanted;
        bool first = !TryReadHeader(held, out int width, out int height, out int components);
        if (first)
        {
            wanted = TextureStreaming.FirstRequestBytes;
        }
        else
        {
            int target = TextureStreaming.TargetDiscard(width, height, components, screenPixelArea);
            // Discard 0 is the whole asset; only completeness covers it.
            if (target > 0 && TextureStreaming.CoveredDiscard(width, height, held.Length) <= target)
            {
                Interlocked.Increment(ref TextureStreaming.LocalDecodes);
                return Decode(textureId, held, width, height, target);
            }
            wanted = target > 0 ? J2kByteSizeEstimator.CalcDataSizeJ2C(width, height, target) : 0;
        }

        var (start, end) = TextureRange.For(held.Length, wanted);
        SLNG.Core.TexStages.Enter(textureId, SLNG.Core.TexStage.Net);
        if (first) SLNG.Core.TexStages.NotePath(SLNG.Core.TexPath.Http);
        var response = await _fetch(textureId, start, end, priority).ConfigureAwait(false);
        if (response is not { } r || r.DataOffset > held.Length)
        {
            Interlocked.Increment(ref TextureStreaming.FellBack);
            Delete(partFile);
            return new Result(Outcome.FellBack);
        }
        if (r.Gone) return new Result(Outcome.Gone);

        if (first)
        {
            Interlocked.Increment(ref TextureStreaming.FirstRequests);
            Interlocked.Add(ref TextureStreaming.FirstBytes, r.Data.Length);
        }
        else
        {
            Interlocked.Increment(ref TextureStreaming.Refinements);
            Interlocked.Add(ref TextureStreaming.RefineBytes, r.Data.Length);
        }

        // The response may start before the end of what is in hand (one byte, by design; all of it, for a 200).
        var merged = new byte[r.DataOffset + r.Data.Length];
        Buffer.BlockCopy(held, 0, merged, 0, (int)r.DataOffset);
        Buffer.BlockCopy(r.Data, 0, merged, (int)r.DataOffset, r.Data.Length);

        if (r.Complete)
        {
            if (merged.Length == 0) { Delete(partFile); return new Result(Outcome.FellBack); }
            await WriteAsync(J2cPath(textureId), merged).ConfigureAwait(false);
            Delete(partFile);
            Interlocked.Increment(ref TextureStreaming.Completed);
            return new Result(Outcome.Complete);
        }

        if (!TryReadHeader(merged, out width, out height, out components))
        {
            Interlocked.Increment(ref TextureStreaming.FellBack);
            Delete(partFile);
            return new Result(Outcome.FellBack);
        }
        await WriteAsync(partFile, merged).ConfigureAwait(false);
        return Decode(textureId, merged, width, height, TextureStreaming.TargetDiscard(width, height, components, screenPixelArea));
    }

    /// <summary>The reduced decode of a prefix, at the coarser of the face's discard and the one the bytes cover.</summary>
    private Result Decode(Guid textureId, byte[] prefix, int width, int height, int target)
    {
        SLNG.Core.TexStages.Enter(textureId, SLNG.Core.TexStage.Decode);
        int reduce = Math.Max(target, TextureStreaming.CoveredDiscard(width, height, prefix.Length));
        var data = TextureStreaming.DecodeWithRescue(prefix, reduce, AssetService.TryDecodeReduced, out bool rescued);
        if (rescued) Interlocked.Increment(ref TextureStreaming.Rescued);
        if (data == null)
        {
            Interlocked.Increment(ref TextureStreaming.FellBack);
            Delete(PartPath(textureId));
            return new Result(Outcome.FellBack);
        }
        return new Result(Outcome.Partial, data with { IsPartial = true });
    }

    private static bool TryReadHeader(byte[] bytes, out int width, out int height, out int components)
    {
        width = height = components = 0;
        return bytes.Length > 0
            && AssetService.TryReadJ2kSize(bytes, out width, out height, out components)
            && AssetService.TryReadJ2kDecompositionLevels(bytes, out _);
    }

    /// <summary>Written to a temporary name and moved into place, so a reader never sees half a file.</summary>
    private async Task WriteAsync(string path, byte[] bytes)
    {
        long before = 0;
        try { if (File.Exists(path)) before = new FileInfo(path).Length; } catch (IOException) { }
        string tmp = path + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
            _diskCache?.RecordWrite(path, bytes.Length - before);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch (Exception) { }
        }
    }

    private void Delete(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            long length = new FileInfo(path).Length;
            File.Delete(path);
            _diskCache?.RecordDelete(path, length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
