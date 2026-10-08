using System.Buffers;
using System.Diagnostics;
using System.IO;
using K4os.Compression.LZ4;

namespace SLNG.Assets;

/// <summary>
/// Persistent disk cache of decoded textures (LZ4-compressed RGBA8) to avoid repeating
/// expensive JPEG-2000 decodes on region revisits and teleports (FEAT-PERF-11).
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
            for (int r = 1; r <= 3; r++)
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
        for (int r = 0; r <= 3; r++)
        {
            TryDelete(GetCachePath(textureId, r));
        }
        TryDelete(GetSculptCachePath(textureId));
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
