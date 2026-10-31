using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SLNG.Assets.Cache;
using Xunit;

namespace SLNG.Assets.Tests;

public class DiskCacheManagerTests : IDisposable
{
    private readonly string _testDir;

    public DiskCacheManagerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "slng_disk_cache_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
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

    private string CreateFile(string dir, string fileName, int sizeBytes, DateTime? lastAccessTimeUtc = null)
    {
        string path = Path.Combine(dir, fileName);
        byte[] data = new byte[sizeBytes];
        Array.Fill(data, (byte)0x55);
        File.WriteAllBytes(path, data);
        if (lastAccessTimeUtc.HasValue)
        {
            File.SetLastAccessTimeUtc(path, lastAccessTimeUtc.Value);
            File.SetLastWriteTimeUtc(path, lastAccessTimeUtc.Value);
        }
        return path;
    }

    [Fact]
    public void Initialization_ScansExistingFiles_CalculatesCurrentSize()
    {
        string decodedDir = Path.Combine(_testDir, "decoded");
        Directory.CreateDirectory(decodedDir);

        CreateFile(_testDir, "asset1.j2c", 1024);
        CreateFile(_testDir, "mesh1.mesh", 2048);
        CreateFile(decodedDir, "tex1.dec", 4096);

        using var manager = new DiskCacheManager(_testDir, maxSizeBytes: 1024 * 1024, startPeriodicTimer: false);
        manager.WaitForInitialScan();
        manager.RefreshSize();

        Assert.Equal(1024 + 2048 + 4096, manager.CurrentSizeBytes);
        var (total, raw, dec) = manager.GetDetailedSize();
        Assert.Equal(1024 + 2048 + 4096, total);
        Assert.Equal(1024 + 2048, raw);
        Assert.Equal(4096, dec);
    }

    [Fact]
    public void RecordWrite_UpdatesTrackedSize()
    {
        using var manager = new DiskCacheManager(_testDir, maxSizeBytes: 1024 * 1024, startPeriodicTimer: false);
        manager.WaitForInitialScan();
        string file = Path.Combine(_testDir, "test.j2c");
        CreateFile(_testDir, "test.j2c", 5000);

        manager.RecordWrite(file, 5000);
        Assert.Equal(5000, manager.CurrentSizeBytes);

        manager.RecordDelete(file, 5000);
        Assert.Equal(0, manager.CurrentSizeBytes);
    }

    [Fact]
    public void LruEviction_PrunesOldestEntriesDownToLowWatermark()
    {
        // Limit: 100 KB, Low watermark: 90% (90 KB)
        long limit = 100 * 1024;
        using var manager = new DiskCacheManager(_testDir, maxSizeBytes: limit, startPeriodicTimer: false)
        {
            SessionGracePeriod = TimeSpan.Zero // Allow immediate eviction for test
        };
        manager.WaitForInitialScan();

        // Create 15 files of 10 KB each = 150 KB (exceeds 100 KB limit)
        DateTime baseTime = DateTime.UtcNow.AddHours(-10);
        var files = new List<string>();
        for (int i = 0; i < 15; i++)
        {
            string path = CreateFile(_testDir, $"asset_{i:D2}.j2c", 10 * 1024, baseTime.AddMinutes(i * 10));
            files.Add(path);
        }

        manager.RefreshSize();
        Assert.Equal(150 * 1024, manager.CurrentSizeBytes);

        manager.EnforceBudget();

        // Should be pruned down to low watermark (<= 90 KB)
        Assert.True(manager.CurrentSizeBytes <= (long)(limit * 0.90),
            $"Expected current size {manager.CurrentSizeBytes} <= {(long)(limit * 0.90)}");

        // Oldest files (asset_00, asset_01, ...) should be deleted
        Assert.False(File.Exists(files[0]));
        Assert.False(File.Exists(files[1]));

        // Newest files (asset_13, asset_14) should still exist
        Assert.True(File.Exists(files[^1]));
        Assert.True(File.Exists(files[^2]));
    }

    [Fact]
    public void HolisticEviction_PrunesDecodedCacheBeforeRawAssets()
    {
        // Limit: 100 KB, target: 90 KB
        long limit = 100 * 1024;
        string decodedDir = Path.Combine(_testDir, "decoded");
        Directory.CreateDirectory(decodedDir);

        using var manager = new DiskCacheManager(_testDir, maxSizeBytes: limit, startPeriodicTimer: false)
        {
            SessionGracePeriod = TimeSpan.Zero
        };
        manager.WaitForInitialScan();

        DateTime baseTime = DateTime.UtcNow.AddDays(-2);

        // 60 KB raw assets (*.j2c)
        var rawFiles = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            rawFiles.Add(CreateFile(_testDir, $"raw_{i:D2}_v5.j2c", 10 * 1024, baseTime.AddHours(i)));
        }

        // 60 KB decoded textures (*.dec)
        var decFiles = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            decFiles.Add(CreateFile(decodedDir, $"dec_{i:D2}.dec", 10 * 1024, baseTime.AddHours(i)));
        }

        // Total = 120 KB (exceeds 100 KB). Pruning 30 KB of .dec brings total to 90 KB!
        manager.RefreshSize();
        Assert.Equal(120 * 1024, manager.CurrentSizeBytes);

        manager.EnforceBudget();

        Assert.True(manager.CurrentSizeBytes <= (long)(limit * 0.90));

        // .dec files should have been pruned first
        Assert.False(File.Exists(decFiles[0]));
        Assert.False(File.Exists(decFiles[1]));
        Assert.False(File.Exists(decFiles[2]));

        // ALL raw files should have been spared because .dec eviction reclaimed enough space!
        foreach (var raw in rawFiles)
        {
            Assert.True(File.Exists(raw), $"Expected raw file {raw} to be spared");
        }
    }

    [Fact]
    public void SessionGracePeriod_ProtectsRecentlyAccessedFiles()
    {
        // Limit: 50 KB
        long limit = 50 * 1024;
        using var manager = new DiskCacheManager(_testDir, maxSizeBytes: limit, startPeriodicTimer: false)
        {
            SessionGracePeriod = TimeSpan.FromMinutes(30)
        };
        manager.WaitForInitialScan();

        DateTime oldTime = DateTime.UtcNow.AddHours(-2);
        // Create 8 old files of 10 KB = 80 KB
        var oldFiles = new List<string>();
        for (int i = 0; i < 8; i++)
        {
            oldFiles.Add(CreateFile(_testDir, $"old_{i}.j2c", 10 * 1024, oldTime.AddMinutes(i)));
        }

        // Mark the very oldest file (old_0.j2c) as active in this session!
        manager.RecordAccess(oldFiles[0]);

        manager.RefreshSize();
        manager.EnforceBudget();

        // old_0 must be protected and NOT deleted despite being the oldest on disk
        Assert.True(File.Exists(oldFiles[0]), "Active session file should be protected from eviction");

        // Other unprotected old files (old_1, old_2) should be deleted
        Assert.False(File.Exists(oldFiles[1]));
        Assert.False(File.Exists(oldFiles[2]));
    }

    [Fact]
    public void RawTextureEviction_AlsoCleansUpAssociatedDecodedFiles()
    {
        // Limit 10 KB (target 9 KB). With 40 KB total, both .dec and .raw must be pruned
        long limit = 10 * 1024;
        string decodedDir = Path.Combine(_testDir, "decoded");
        Directory.CreateDirectory(decodedDir);

        using var manager = new DiskCacheManager(_testDir, maxSizeBytes: limit, startPeriodicTimer: false)
        {
            SessionGracePeriod = TimeSpan.Zero
        };
        manager.WaitForInitialScan();

        Guid textureId = Guid.NewGuid();
        DateTime oldTime = DateTime.UtcNow.AddDays(-5);

        // Raw file
        string raw = CreateFile(_testDir, $"{textureId:D}_v5.j2c", 20 * 1024, oldTime);
        // Associated dec file
        string dec = CreateFile(decodedDir, $"{textureId:D}_r0_v1.dec", 20 * 1024, oldTime.AddMinutes(1));

        manager.RefreshSize();
        Assert.Equal(40 * 1024, manager.CurrentSizeBytes);

        manager.EnforceBudget();

        // Both raw and associated decoded file must be gone
        Assert.False(File.Exists(raw));
        Assert.False(File.Exists(dec));
    }

    [Fact]
    public void Clear_RemovesAllFilesAndResetsSize()
    {
        string decodedDir = Path.Combine(_testDir, "decoded");
        Directory.CreateDirectory(decodedDir);

        CreateFile(_testDir, "test1.j2c", 1024);
        CreateFile(_testDir, "test2.mesh", 2048);
        CreateFile(decodedDir, "test3.dec", 4096);

        using var manager = new DiskCacheManager(_testDir, maxSizeBytes: 1024 * 1024, startPeriodicTimer: false);
        manager.WaitForInitialScan();
        manager.RefreshSize();
        Assert.Equal(1024 + 2048 + 4096, manager.CurrentSizeBytes);

        manager.Clear();

        Assert.Equal(0, manager.CurrentSizeBytes);
        Assert.Empty(Directory.GetFiles(_testDir));
        Assert.Empty(Directory.GetFiles(decodedDir));
    }
}
