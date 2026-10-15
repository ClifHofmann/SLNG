using System.Security.Cryptography;
using Xunit;

namespace SLNG.Assets.Tests;

public class DecodedTextureCacheTests : IDisposable
{
    private readonly string _testDir;
    private readonly DecodedTextureCache _cache;

    public DecodedTextureCacheTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "slng_dec_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _cache = new DecodedTextureCache(_testDir, maxSizeBytes: 10 * 1024 * 1024);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch { }
    }

    private static TextureData CreateDummyTexture(int width, int height, byte fill = 0xAA)
    {
        byte[] rgba = new byte[width * height * 4];
        Array.Fill(rgba, fill);
        return new TextureData(width, height, rgba, IsDegraded: false, SourceWidth: width, SourceHeight: height);
    }

    [Fact]
    public async Task WriteAndReadBack_RoundTripExact()
    {
        var id = Guid.NewGuid();
        var original = CreateDummyTexture(64, 64, fill: 128);

        await _cache.PutAsync(id, reduceLevel: 0, original);

        var retrieved = await _cache.TryGetAsync(id, desiredReduce: 0);
        Assert.NotNull(retrieved);
        Assert.Equal(64, retrieved.Width);
        Assert.Equal(64, retrieved.Height);
        Assert.Equal(64, retrieved.SourceWidth);
        Assert.Equal(64, retrieved.SourceHeight);
        Assert.False(retrieved.IsDegraded);
        Assert.Equal(original.Rgba, retrieved.Rgba);
    }

    [Fact]
    public async Task HigherResolution_ServesLowerResolutionRequest()
    {
        var id = Guid.NewGuid();
        var fullRes = CreateDummyTexture(128, 128, fill: 200);

        // Store only r0 (full resolution)
        await _cache.PutAsync(id, reduceLevel: 0, fullRes);

        // Caller asks for reduce=2 (e.g. 32x32), but cache has r0.
        // It should return r0 since r0 is higher resolution and strictly better.
        var retrieved = await _cache.TryGetAsync(id, desiredReduce: 2);
        Assert.NotNull(retrieved);
        Assert.Equal(128, retrieved.Width);
        Assert.Equal(128, retrieved.Height);
    }

    [Fact]
    public async Task StoringFullResolution_PrunesLowerResolutions()
    {
        var id = Guid.NewGuid();
        var r1 = CreateDummyTexture(64, 64, fill: 10);
        var r0 = CreateDummyTexture(128, 128, fill: 20);

        // First store r1
        await _cache.PutAsync(id, reduceLevel: 1, r1);
        string r1Path = _cache.GetCachePath(id, 1);
        Assert.True(File.Exists(r1Path));

        // Now full resolution r0 arrives
        await _cache.PutAsync(id, reduceLevel: 0, r0);
        string r0Path = _cache.GetCachePath(id, 0);
        Assert.True(File.Exists(r0Path));

        // r1 should now be pruned to conserve disk space
        Assert.False(File.Exists(r1Path));
    }

    [Fact]
    public async Task SourceJ2cModified_InvalidatesDecodedCache()
    {
        var id = Guid.NewGuid();
        string dummyJ2c = Path.Combine(_testDir, $"{id}_v5.j2c");
        await File.WriteAllBytesAsync(dummyJ2c, new byte[] { 1, 2, 3 });
        File.SetLastWriteTimeUtc(dummyJ2c, DateTime.UtcNow.AddMinutes(-5));

        var tex = CreateDummyTexture(32, 32);
        await _cache.PutAsync(id, 0, tex);

        // Touch J2C so it's newer than the decoded cache
        File.SetLastWriteTimeUtc(dummyJ2c, DateTime.UtcNow.AddMinutes(5));

        var retrieved = await _cache.TryGetAsync(id, 0, dummyJ2c);
        Assert.Null(retrieved);
        Assert.False(File.Exists(_cache.GetCachePath(id, 0)));
    }

    [Fact]
    public async Task SculptRoundTrip_AndInvalidate()
    {
        var id = Guid.NewGuid();
        var sculpt = CreateDummyTexture(64, 64, fill: 55);

        await _cache.PutSculptAsync(id, sculpt);
        var retrieved = await _cache.TryGetSculptAsync(id);
        Assert.NotNull(retrieved);
        Assert.Equal(sculpt.Rgba, retrieved.Rgba);

        // Invalidate
        _cache.Invalidate(id);
        Assert.Null(await _cache.TryGetSculptAsync(id));
    }

    [Fact]
    public async Task LruEviction_PrunesOldestEntriesWhenOverBudget()
    {
        // Cache limited to 100 KB
        var tightCache = new DecodedTextureCache(_testDir, maxSizeBytes: 100 * 1024);

        // Write 10 textures of 64x64 RGBA (approx 16 KB uncompressed each, ~8 KB LZ4 each)
        // 10 * 8 KB = ~80 KB, then write 10 more to exceed 100 KB limit
        var ids = new List<Guid>();
        for (int i = 0; i < 20; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            // Write distinct non-trivial content so LZ4 doesn't compress to 10 bytes
            byte[] rgba = new byte[64 * 64 * 4];
            RandomNumberGenerator.Fill(rgba);
            var tex = new TextureData(64, 64, rgba, false, 64, 64);
            await tightCache.PutAsync(id, 0, tex);
            // Delay slightly so timestamps vary
            await Task.Delay(5);
        }

        tightCache.EnforceBudget();

        // Assert size was maintained below or around budget
        Assert.True(tightCache.CurrentSizeBytes <= tightCache.MaxCacheSizeBytes);

        // The oldest entry should have been evicted
        var oldest = await tightCache.TryGetAsync(ids.First(), 0);
        Assert.Null(oldest);

        // The newest entry should still be in cache
        var newest = await tightCache.TryGetAsync(ids.Last(), 0);
        Assert.NotNull(newest);
    }

    // ---- FEAT-PERF-17 Part B: the block-compressed copy -----------------------------------------

    private static CompressedTextureData Compressed(int size, GpuTextureFormat format, bool mipmaps = true)
    {
        var rgba = new byte[BcLayout.ChainBytes(GpuTextureFormat.Rgba8, size, size, mipmaps)];
        RandomNumberGenerator.Fill(rgba);
        var blocks = BcEncoder.EncodeChain(format, rgba, size, size, mipmaps);
        return new CompressedTextureData(format, size, size, mipmaps, blocks, 1024, 1024,
            new TextureAlphaInfo(2, true, 3, 0.25f, 0.5f));
    }

    [Fact]
    public async Task Gpu_WriteAndReadBack_RoundTripExact()
    {
        var id = Guid.NewGuid();
        var original = Compressed(128, GpuTextureFormat.Bc3);
        await _cache.PutGpuAsync(id, original);

        var back = await _cache.TryGetGpuAsync(id);
        Assert.NotNull(back);
        Assert.Equal(original.Format, back!.Format);
        Assert.Equal((128, 128, true), (back.Width, back.Height, back.Mipmaps));
        Assert.Equal((1024, 1024), (back.SourceWidth, back.SourceHeight));
        Assert.Equal(original.Alpha, back.Alpha);
        Assert.Equal(original.Data, back.Data);
        Assert.Equal((128, 128), _cache.PeekGpuSize(id));
    }

    [Fact]
    public async Task Gpu_KeepsTheLargerCopy_AndServesSmallerLevelsFromIt()
    {
        var id = Guid.NewGuid();
        await _cache.PutGpuAsync(id, Compressed(256, GpuTextureFormat.Bc1));
        await _cache.PutGpuAsync(id, Compressed(64, GpuTextureFormat.Bc1));   // a far object's smaller encode

        var back = await _cache.TryGetGpuAsync(id);
        Assert.Equal(256, back!.Width);
        var level = back.Slice(64, 64);
        Assert.NotNull(level);
        Assert.Equal(BcLayout.ChainBytes(GpuTextureFormat.Bc1, 64, 64, true), level!.Data.Length);

        await _cache.PutGpuAsync(id, Compressed(512, GpuTextureFormat.Bc1));  // a closer look replaces it
        Assert.Equal((512, 512), _cache.PeekGpuSize(id));
    }

    [Fact]
    public async Task Gpu_StaleAgainstTheJ2c_AndGoneOnInvalidate()
    {
        var id = Guid.NewGuid();
        await _cache.PutGpuAsync(id, Compressed(64, GpuTextureFormat.Bc1));
        string j2c = Path.Combine(_testDir, "src.j2c");
        File.WriteAllBytes(j2c, new byte[] { 1 });
        File.SetLastWriteTimeUtc(j2c, DateTime.UtcNow.AddMinutes(5));
        Assert.Null(await _cache.TryGetGpuAsync(id, j2c));
        Assert.False(File.Exists(_cache.GetGpuCachePath(id)));

        await _cache.PutGpuAsync(id, Compressed(64, GpuTextureFormat.Bc1));
        _cache.Invalidate(id);
        Assert.Null(await _cache.TryGetGpuAsync(id));
    }

    [Fact]
    public async Task Gpu_CorruptFile_IsDeleted_AndFormatsDoNotMix()
    {
        var id = Guid.NewGuid();
        await _cache.PutGpuAsync(id, Compressed(64, GpuTextureFormat.Bc1));
        string path = _cache.GetGpuCachePath(id);
        var bytes = File.ReadAllBytes(path);
        bytes[36] ^= 0x7F; // the payload length no longer matches the chain
        File.WriteAllBytes(path, bytes);
        Assert.Null(await _cache.TryGetGpuAsync(id));
        Assert.False(File.Exists(path));

        // An RGBA entry is not a compressed one, and the reverse.
        await _cache.PutAsync(id, 0, CreateDummyTexture(64, 64));
        Assert.Null(await _cache.TryGetGpuAsync(id));
        await _cache.PutGpuAsync(id, Compressed(64, GpuTextureFormat.Bc1));
        Assert.NotNull(await _cache.TryGetAsync(id, 0));
    }

    [Fact]
    public async Task Gpu_WrongLengthOrRgba_IsNotStored()
    {
        var id = Guid.NewGuid();
        var good = Compressed(64, GpuTextureFormat.Bc1);
        await _cache.PutGpuAsync(id, good with { Data = good.Data[..^8] });
        await _cache.PutGpuAsync(id, good with { Format = GpuTextureFormat.Rgba8 });
        Assert.False(File.Exists(_cache.GetGpuCachePath(id)));
    }

    [Fact]
    public async Task Gpu_BusyFile_IsKeptForTheNextRead()
    {
        var id = Guid.NewGuid();
        await _cache.PutGpuAsync(id, Compressed(64, GpuTextureFormat.Bc1));
        string path = _cache.GetGpuCachePath(id);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(await _cache.TryGetGpuAsync(id));   // a sharing violation is not corruption
        }
        Assert.True(File.Exists(path));
        Assert.NotNull(await _cache.TryGetGpuAsync(id));
    }

    [Fact]
    public async Task Gpu_WriteThatCannotReplaceTheFile_LeavesNoTempFile()
    {
        var id = Guid.NewGuid();
        await _cache.PutGpuAsync(id, Compressed(64, GpuTextureFormat.Bc1));
        string path = _cache.GetGpuCachePath(id);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await _cache.PutGpuAsync(id, Compressed(128, GpuTextureFormat.Bc1));  // the move cannot replace an open file
        }
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp*"));
    }
}
