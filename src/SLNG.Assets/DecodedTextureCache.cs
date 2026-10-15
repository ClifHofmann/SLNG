using System.Buffers;
using System.Diagnostics;
using System.IO;
using K4os.Compression.LZ4;

namespace SLNG.Assets;

/// <summary>
/// Persistent disk cache of decoded textures (LZ4-compressed RGBA8) to avoid repeating
/// expensive JPEG-2000 decodes on region revisits and teleports (FEAT-PERF-11), and of their
/// block-compressed GPU copies (FEAT-PERF-17), so a warm start uploads without encoding again.
/// </summary>
public sealed class DecodedTextureCache
{
    private const uint Magic = 0x43444C53; // "SLDC" in little-endian
    private const ushort FormatVersion = 1;
    private const int HeaderSize = 28;

    private readonly string _cacheDir;
    private long _maxCacheSizeBytes;
    private long _currentSizeBytes;
    private int _isEvicting;
    private long _lastEvictionTick;

    /// <summary>Whether the decoded texture cache is active (defaults to true).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Maximum cache size in bytes (defaults to 4 GB).</summary>
    public long MaxCacheSizeBytes
    {
        get => _maxCacheSizeBytes;
        set => _maxCacheSizeBytes = Math.Max(64L * 1024 * 1024, value);
    }

    /// <summary>Currently used cache size in bytes.</summary>
    public long CurrentSizeBytes => Interlocked.Read(ref _currentSizeBytes);

    public DecodedTextureCache(string baseCacheDir, long maxSizeBytes = 4L * 1024 * 1024 * 1024)
    {
        _cacheDir = Path.Combine(baseCacheDir, "decoded");
        _maxCacheSizeBytes = maxSizeBytes;

        try
        {
            if (!Directory.Exists(_cacheDir))
            {
                Directory.CreateDirectory(_cacheDir);
            }
            InitSize();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DecodedCache] Failed to initialize cache directory '{_cacheDir}': {ex.Message}");
        }
    }

    private void InitSize()
    {
        if (!Directory.Exists(_cacheDir)) return;
        long total = 0;
        try
        {
            var dir = new DirectoryInfo(_cacheDir);
            foreach (var fi in dir.EnumerateFiles("*.dec"))
            {
                total += fi.Length;
            }
        }
        catch { }
        Interlocked.Exchange(ref _currentSizeBytes, total);
    }

    /// <summary>Gets the file path for a cached texture at the given reduce level.</summary>
    public string GetCachePath(Guid textureId, int reduceLevel)
        => Path.Combine(_cacheDir, $"{textureId:D}_r{reduceLevel}_v{FormatVersion}.dec");

    /// <summary>Gets the file path for a cached sculpt map.</summary>
    public string GetSculptCachePath(Guid sculptId)
        => Path.Combine(_cacheDir, $"{sculptId:D}_sculpt_v{FormatVersion}.dec");

    // FEAT-PERF-17 Part B: the GPU-ready copy of a texture -- block-compressed, with its mip chain, and
    // the alpha verdicts measured on the RGBA pixels before compression. One file per texture holding
    // the LARGEST level encoded so far: every smaller level is the tail of its mip chain
    // (CompressedTextureData.Slice), so it serves any reduce level at or below its own without a decode
    // or an encode. A format of its own (magic and version), so neither reader mistakes the other's file.
    private const uint GpuMagic = 0x42474C53; // "SLGB" in little-endian
    private const ushort GpuFormatVersion = 1;
    private const int GpuHeaderSize = 40;

    /// <summary>FEAT-PERF-17: the file path of a texture's block-compressed copy.</summary>
    public string GetGpuCachePath(Guid textureId)
        => Path.Combine(_cacheDir, $"{textureId:D}_gpu_v{GpuFormatVersion}.dec");

    /// <summary>
    /// FEAT-PERF-17: the texture's block-compressed copy, or null. Stale when the source .j2c is newer,
    /// and deleted when it does not parse -- the same rules as the RGBA entries.
    /// </summary>
    public async Task<CompressedTextureData?> TryGetGpuAsync(Guid textureId, string? sourceJ2cPath = null)
    {
        if (!Enabled) return null;
        string filePath = GetGpuCachePath(textureId);
        if (!File.Exists(filePath)) return null;
        try
        {
            if (sourceJ2cPath != null && File.Exists(sourceJ2cPath)
                && new FileInfo(sourceJ2cPath).LastWriteTimeUtc > new FileInfo(filePath).LastWriteTimeUtc)
            {
                TryDelete(filePath);
                return null;
            }

            byte[] fileBytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
            var data = ParseGpu(fileBytes);
            if (data == null)
            {
                TryDelete(filePath);
                return null;
            }
            try { File.SetLastAccessTimeUtc(filePath, DateTime.UtcNow); } catch { }
            return data;
        }
        catch
        {
            TryDelete(filePath);
            return null;
        }
    }

    /// <summary>The size of the stored compressed copy without reading its blocks, or (0, 0).</summary>
    public (int Width, int Height) PeekGpuSize(Guid textureId)
    {
        try
        {
            string filePath = GetGpuCachePath(textureId);
            if (!File.Exists(filePath)) return (0, 0);
            Span<byte> head = stackalloc byte[GpuHeaderSize];
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Read(head) < GpuHeaderSize) return (0, 0);
            if (BitConverter.ToUInt32(head) != GpuMagic || BitConverter.ToUInt16(head.Slice(4)) != GpuFormatVersion) return (0, 0);
            return (BitConverter.ToInt32(head.Slice(8)), BitConverter.ToInt32(head.Slice(12)));
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// FEAT-PERF-17: stores a texture's block-compressed copy, unless a copy at least as large (and with a
    /// mip chain if this one has one) is already there -- a far object must not replace the copy a near
    /// one paid for. Does not throw on I/O failures.
    /// </summary>
    public async Task PutGpuAsync(Guid textureId, CompressedTextureData data)
    {
        if (!Enabled || data == null || data.Data == null || data.Format == GpuTextureFormat.Rgba8) return;
        if (data.Data.Length != BcLayout.ChainBytes(data.Format, data.Width, data.Height, data.Mipmaps)) return;

        string targetPath = GetGpuCachePath(textureId);
        var (oldW, oldH) = PeekGpuSize(textureId);
        if (oldW >= data.Width && oldH >= data.Height && (oldW > data.Width || oldH > data.Height || !data.Mipmaps)) return;
        long oldLength = 0;
        try { if (File.Exists(targetPath)) oldLength = new FileInfo(targetPath).Length; } catch { }

        int maxLz4Len = LZ4Codec.MaximumOutputSize(data.Data.Length);
        byte[]? rented = null;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(GpuHeaderSize + maxLz4Len);
            WriteGpuHeader(rented.AsSpan(0, GpuHeaderSize), data);

            int compressedLen = LZ4Codec.Encode(data.Data, 0, data.Data.Length, rented, GpuHeaderSize, maxLz4Len, LZ4Level.L00_FAST);
            int totalFileLen = GpuHeaderSize + compressedLen;

            string tempPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N");
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await stream.WriteAsync(rented.AsMemory(0, totalFileLen)).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            File.Move(tempPath, targetPath, overwrite: true);
            Interlocked.Add(ref _currentSizeBytes, totalFileLen - oldLength);

            if (Interlocked.Read(ref _currentSizeBytes) > _maxCacheSizeBytes) ScheduleEviction();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DecodedCache] Failed to write cache file '{targetPath}': {ex.Message}");
        }
        finally
        {
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void WriteGpuHeader(Span<byte> head, CompressedTextureData data)
    {
        head.Clear();
        BitConverter.TryWriteBytes(head.Slice(0, 4), GpuMagic);
        BitConverter.TryWriteBytes(head.Slice(4, 2), GpuFormatVersion);
        head[6] = (byte)data.Format;
        head[7] = (byte)((data.Mipmaps ? 1 : 0) | (data.Alpha.Maskable ? 2 : 0));
        BitConverter.TryWriteBytes(head.Slice(8, 4), data.Width);
        BitConverter.TryWriteBytes(head.Slice(12, 4), data.Height);
        BitConverter.TryWriteBytes(head.Slice(16, 4), data.SourceWidth);
        BitConverter.TryWriteBytes(head.Slice(20, 4), data.SourceHeight);
        head[24] = data.Alpha.Mode;
        head[25] = (byte)Math.Clamp(data.Alpha.MinAlpha, 0, 255);
        BitConverter.TryWriteBytes(head.Slice(28, 4), data.Alpha.FracMid);
        BitConverter.TryWriteBytes(head.Slice(32, 4), data.Alpha.FracClear);
        BitConverter.TryWriteBytes(head.Slice(36, 4), data.Data.Length);
    }

    private static CompressedTextureData? ParseGpu(byte[] fileBytes)
    {
        if (fileBytes.Length < GpuHeaderSize) return null;
        if (BitConverter.ToUInt32(fileBytes, 0) != GpuMagic) return null;
        if (BitConverter.ToUInt16(fileBytes, 4) != GpuFormatVersion) return null;
        var format = (GpuTextureFormat)fileBytes[6];
        if (format != GpuTextureFormat.Bc1 && format != GpuTextureFormat.Bc3) return null;
        bool mipmaps = (fileBytes[7] & 1) != 0;
        bool maskable = (fileBytes[7] & 2) != 0;
        int width = BitConverter.ToInt32(fileBytes, 8);
        int height = BitConverter.ToInt32(fileBytes, 12);
        int sourceWidth = BitConverter.ToInt32(fileBytes, 16);
        int sourceHeight = BitConverter.ToInt32(fileBytes, 20);
        byte alphaMode = fileBytes[24];
        int minAlpha = fileBytes[25];
        float fracMid = BitConverter.ToSingle(fileBytes, 28);
        float fracClear = BitConverter.ToSingle(fileBytes, 32);
        int dataLen = BitConverter.ToInt32(fileBytes, 36);
        if (width <= 0 || height <= 0 || alphaMode > 2) return null;
        if (dataLen != BcLayout.ChainBytes(format, width, height, mipmaps)) return null;

        var blocks = new byte[dataLen];
        int decoded = LZ4Codec.Decode(fileBytes, GpuHeaderSize, fileBytes.Length - GpuHeaderSize, blocks, 0, dataLen);
        if (decoded != dataLen) return null;
        return new CompressedTextureData(format, width, height, mipmaps, blocks, sourceWidth, sourceHeight,
            new TextureAlphaInfo(alphaMode, maskable, minAlpha, fracMid, fracClear));
    }

    /// <summary>
    /// Attempts to read a cached decoded texture. If the exact reduce level is not present,
    /// checks whether any higher-resolution level (lower reduce index) is cached and returns it.
    /// </summary>
    public async Task<TextureData?> TryGetAsync(Guid textureId, int desiredReduce, string? sourceJ2cPath = null)
    {
        if (!Enabled) return null;

        // 1. Try exact requested level
        string path = GetCachePath(textureId, desiredReduce);
        var result = await ReadFileAsync(path, sourceJ2cPath).ConfigureAwait(false);
        if (result != null) return result;

        // 2. Try higher-resolution cached levels (e.g. if reduce=2 was requested, check r0 or r1)
        for (int r = 0; r < desiredReduce; r++)
        {
            string higherPath = GetCachePath(textureId, r);
            result = await ReadFileAsync(higherPath, sourceJ2cPath).ConfigureAwait(false);
            if (result != null) return result;
        }

        return null;
    }

    /// <summary>Attempts to read a cached sculpt texture.</summary>
    public async Task<TextureData?> TryGetSculptAsync(Guid sculptId, string? sourceJ2cPath = null)
    {
        if (!Enabled) return null;
        string path = GetSculptCachePath(sculptId);
        return await ReadFileAsync(path, sourceJ2cPath).ConfigureAwait(false);
    }

    private async Task<TextureData?> ReadFileAsync(string filePath, string? sourceJ2cPath)
    {
        if (!File.Exists(filePath)) return null;

        try
        {
            var fileInfo = new FileInfo(filePath);
            if (sourceJ2cPath != null && File.Exists(sourceJ2cPath))
            {
                var j2cInfo = new FileInfo(sourceJ2cPath);
                if (j2cInfo.LastWriteTimeUtc > fileInfo.LastWriteTimeUtc)
                {
                    // Source J2C was modified or replaced after decoded cache was written
                    TryDelete(filePath);
                    return null;
                }
            }

            byte[] fileBytes;
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
            {
                if (stream.Length < HeaderSize)
                {
                    TryDelete(filePath);
                    return null;
                }

                fileBytes = new byte[stream.Length];
                int read = 0;
                while (read < fileBytes.Length)
                {
                    int chunk = await stream.ReadAsync(fileBytes.AsMemory(read, fileBytes.Length - read)).ConfigureAwait(false);
                    if (chunk == 0) break;
                    read += chunk;
                }
            }

            // Parse header
            uint magic = BitConverter.ToUInt32(fileBytes, 0);
            if (magic != Magic)
            {
                TryDelete(filePath);
                return null;
            }

            ushort version = BitConverter.ToUInt16(fileBytes, 4);
            if (version != FormatVersion)
            {
                TryDelete(filePath);
                return null;
            }

            ushort flags = BitConverter.ToUInt16(fileBytes, 6);
            bool isDegraded = (flags & 1) != 0;
            int width = BitConverter.ToInt32(fileBytes, 8);
            int height = BitConverter.ToInt32(fileBytes, 12);
            int sourceWidth = BitConverter.ToInt32(fileBytes, 16);
            int sourceHeight = BitConverter.ToInt32(fileBytes, 20);
            int uncompressedLen = BitConverter.ToInt32(fileBytes, 24);

            int expectedRgbaLen = width * height * 4;
            if (uncompressedLen != expectedRgbaLen || width <= 0 || height <= 0)
            {
                TryDelete(filePath);
                return null;
            }

            int payloadOffset = HeaderSize;
            int payloadLen = fileBytes.Length - payloadOffset;

            byte[] rgba = new byte[uncompressedLen];
            int decodedBytes = LZ4Codec.Decode(fileBytes, payloadOffset, payloadLen, rgba, 0, uncompressedLen);
            if (decodedBytes != uncompressedLen)
            {
                TryDelete(filePath);
                return null;
            }

            // Update access timestamp for LRU
            try { File.SetLastAccessTimeUtc(filePath, DateTime.UtcNow); } catch { }

            return new TextureData(width, height, rgba, isDegraded, sourceWidth, sourceHeight);
        }
        catch
        {
            TryDelete(filePath);
            return null;
        }
    }

    /// <summary>
    /// Stores a decoded texture into the cache asynchronously on worker threads.
    /// Does not throw on I/O failures.
    /// </summary>
    public async Task PutAsync(Guid textureId, int reduceLevel, TextureData textureData)
    {
        if (!Enabled || textureData == null || textureData.Rgba == null || textureData.IsDegraded)
            return;

        string targetPath = GetCachePath(textureId, reduceLevel);
        await WriteFileAsync(targetPath, textureData).ConfigureAwait(false);

        // If storing full resolution (r0), prune inferior levels (r1, r2, ...) for this UUID
        if (reduceLevel == 0)
        {
            for (int r = 1; r <= TextureLod.MaxDiscardLevel; r++)
            {
                TryDelete(GetCachePath(textureId, r));
            }
        }
    }

    /// <summary>Stores a decoded sculpt map into the cache.</summary>
    public async Task PutSculptAsync(Guid sculptId, TextureData textureData)
    {
        if (!Enabled || textureData == null || textureData.Rgba == null || textureData.IsDegraded)
            return;

        string targetPath = GetSculptCachePath(sculptId);
        await WriteFileAsync(targetPath, textureData).ConfigureAwait(false);
    }

    private async Task WriteFileAsync(string targetPath, TextureData textureData)
    {
        byte[] rgba = textureData.Rgba;
        int uncompressedLen = rgba.Length;
        int maxLz4Len = LZ4Codec.MaximumOutputSize(uncompressedLen);

        byte[]? rented = null;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(HeaderSize + maxLz4Len);
            // Write header
            BitConverter.TryWriteBytes(rented.AsSpan(0, 4), Magic);
            BitConverter.TryWriteBytes(rented.AsSpan(4, 2), FormatVersion);
            ushort flags = (ushort)(textureData.IsDegraded ? 1 : 0);
            BitConverter.TryWriteBytes(rented.AsSpan(6, 2), flags);
            BitConverter.TryWriteBytes(rented.AsSpan(8, 4), textureData.Width);
            BitConverter.TryWriteBytes(rented.AsSpan(12, 4), textureData.Height);
            BitConverter.TryWriteBytes(rented.AsSpan(16, 4), textureData.SourceWidth);
            BitConverter.TryWriteBytes(rented.AsSpan(20, 4), textureData.SourceHeight);
            BitConverter.TryWriteBytes(rented.AsSpan(24, 4), uncompressedLen);

            int compressedLen = LZ4Codec.Encode(rgba, 0, uncompressedLen, rented, HeaderSize, maxLz4Len, LZ4Level.L00_FAST);
            int totalFileLen = HeaderSize + compressedLen;

            string tempPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N");
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await stream.WriteAsync(rented.AsMemory(0, totalFileLen)).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }

            File.Move(tempPath, targetPath, overwrite: true);
            Interlocked.Add(ref _currentSizeBytes, totalFileLen);

            if (Interlocked.Read(ref _currentSizeBytes) > _maxCacheSizeBytes)
            {
                ScheduleEviction();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DecodedCache] Failed to write cache file '{targetPath}': {ex.Message}");
        }
        finally
        {
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                long len = new FileInfo(path).Length;
                File.Delete(path);
                Interlocked.Add(ref _currentSizeBytes, -len);
            }
        }
        catch { }
    }

    /// <summary>Invalidates all cached decoded resolutions and sculpts for a given texture UUID.</summary>
    public void Invalidate(Guid textureId)
    {
        for (int r = 0; r <= TextureLod.MaxDiscardLevel; r++)
        {
            TryDelete(GetCachePath(textureId, r));
        }
        TryDelete(GetSculptCachePath(textureId));
        TryDelete(GetGpuCachePath(textureId));
    }

    /// <summary>Deletes all files in the decoded cache directory.</summary>
    public void Clear()
    {
        try
        {
            if (Directory.Exists(_cacheDir))
            {
                foreach (var f in Directory.GetFiles(_cacheDir, "*.dec"))
                {
                    try { File.Delete(f); } catch { }
                }
                foreach (var f in Directory.GetFiles(_cacheDir, "*.tmp*"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            Interlocked.Exchange(ref _currentSizeBytes, 0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DecodedCache] Error clearing cache: {ex.Message}");
        }
    }

    /// <summary>Forces an eviction pass to prune cache files within the budget.</summary>
    public void EnforceBudget()
    {
        RunEviction();
    }

    private void ScheduleEviction()
    {
        long now = Stopwatch.GetTimestamp();
        // Cooldown: at most once every 5 seconds
        if (_lastEvictionTick != 0 && Stopwatch.GetElapsedTime(_lastEvictionTick, now).TotalSeconds < 5)
            return;

        if (Interlocked.CompareExchange(ref _isEvicting, 1, 0) == 0)
        {
            _lastEvictionTick = now;
            Task.Run(() =>
            {
                try
                {
                    RunEviction();
                }
                finally
                {
                    Interlocked.Exchange(ref _isEvicting, 0);
                }
            });
        }
    }

    private void RunEviction()
    {
        try
        {
            if (!Directory.Exists(_cacheDir)) return;

            long targetBytes = (long)(_maxCacheSizeBytes * 0.85); // Prune to 85%
            var dir = new DirectoryInfo(_cacheDir);
            var files = dir.EnumerateFiles("*.dec")
                .Select(f => new { f.FullName, f.Length, LastAccess = f.LastAccessTimeUtc })
                .OrderBy(f => f.LastAccess) // Oldest first
                .ToList();

            long current = files.Sum(f => f.Length);
            if (current <= _maxCacheSizeBytes)
            {
                Interlocked.Exchange(ref _currentSizeBytes, current);
                return;
            }

            foreach (var file in files)
            {
                if (current <= targetBytes) break;
                try
                {
                    File.Delete(file.FullName);
                    current -= file.Length;
                }
                catch { }
            }

            Interlocked.Exchange(ref _currentSizeBytes, Math.Max(0, current));
        }
        catch { }
    }
}
