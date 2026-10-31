using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SLNG.Assets.Cache;

/// <summary>
/// Engine-agnostic manager for the on-disk asset cache (FEAT-CACHE-01).
/// Bounds total disk usage across raw assets (*.j2c, *.mesh, *.anim, *.mat, *.baddecode, *.degraded)
/// and decoded texture cache files (decoded/*.dec) to a user-configured limit.
/// When usage reaches the high watermark (100%), an LRU eviction pass reclaims space down to
/// the low watermark (90%), protecting files accessed in the active session.
/// </summary>
public sealed class DiskCacheManager : IDisposable
{
    public const long DefaultMaxCacheSizeBytes = 20480L * 1024 * 1024; // 20 GB (Firestorm parity)
    public const long MinMaxCacheSizeBytes = 1024; // 1 KB floor for testing and extreme limits

    private readonly string _baseCacheDir;
    private readonly string _decodedCacheDir;
    private long _maxCacheSizeBytes;
    private long _currentSizeBytes;
    private long _cumulativeBytesWrittenSinceLastSweep;
    private int _isEvicting;
    private long _lastEvictionTick;
    private readonly Timer? _periodicTimer;
    private readonly ConcurrentDictionary<string, DateTime> _sessionAccessedFiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>Base asset cache directory (e.g. user://cache/assets).</summary>
    public string BaseCacheDir => _baseCacheDir;

    /// <summary>Decoded textures sub-directory (decoded/).</summary>
    public string DecodedCacheDir => _decodedCacheDir;

    /// <summary>Maximum cache budget in bytes.</summary>
    public long MaxCacheSizeBytes
    {
        get => Interlocked.Read(ref _maxCacheSizeBytes);
        set
        {
            long clamped = Math.Max(MinMaxCacheSizeBytes, value);
            Interlocked.Exchange(ref _maxCacheSizeBytes, clamped);
            if (CurrentSizeBytes > clamped)
            {
                ScheduleEviction();
            }
        }
    }

    /// <summary>Target watermark ratio to prune down to during eviction (default 0.90 = 90%).</summary>
    public double LowWatermarkRatio { get; set; } = 0.90;

    /// <summary>Duration since last read/write during which a file is considered protected from eviction.</summary>
    public TimeSpan SessionGracePeriod { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Threshold of cumulative writes that triggers an eviction check (default 128 MB).</summary>
    public long CumulativeWriteThresholdBytes { get; set; } = 128L * 1024 * 1024;

    /// <summary>Currently measured cache size in bytes across raw assets and decoded textures.</summary>
    public long CurrentSizeBytes => Interlocked.Read(ref _currentSizeBytes);

    /// <summary>Completes when the scan of the cache folders started by the constructor has set the tracked size.</summary>
    public Task InitialScan { get; }

    /// <summary>Blocks until <see cref="InitialScan"/> is done (tests and tools; never call it on the main thread).</summary>
    public void WaitForInitialScan() => InitialScan.GetAwaiter().GetResult();

    /// <summary>Whether an eviction pass is currently executing.</summary>
    public bool IsEvicting => Interlocked.CompareExchange(ref _isEvicting, 0, 0) == 1;

    public DiskCacheManager(string baseCacheDir, long maxSizeBytes = DefaultMaxCacheSizeBytes, bool startPeriodicTimer = true)
    {
        _baseCacheDir = baseCacheDir;
        _decodedCacheDir = Path.Combine(baseCacheDir, "decoded");
        _maxCacheSizeBytes = Math.Max(MinMaxCacheSizeBytes, maxSizeBytes);

        try
        {
            if (!string.IsNullOrEmpty(_baseCacheDir) && !Directory.Exists(_baseCacheDir))
            {
                Directory.CreateDirectory(_baseCacheDir);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DiskCache] Failed to ensure base cache dir '{_baseCacheDir}': {ex.Message}");
        }

        // Initialize size asynchronously so constructor does not stall. The scan REPLACES the tracked size with a
        // snapshot of the disk, so a RecordWrite/RecordDelete that lands before it finishes can be overwritten or
        // counted twice: InitialScan lets a caller (and the tests) wait for it before relying on the delta methods.
        InitialScan = Task.Run(RefreshSize);

        if (startPeriodicTimer)
        {
            // Periodic maintenance sweep every 5 minutes
            _periodicTimer = new Timer(_ =>
            {
                if (!_disposed && CurrentSizeBytes > MaxCacheSizeBytes)
                {
                    ScheduleEviction();
                }
            }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }
    }

    /// <summary>Records access to a cached asset file, updating LastAccessTime and session protection.</summary>
    public void RecordAccess(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        DateTime now = DateTime.UtcNow;
        _sessionAccessedFiles[filePath] = now;
        try
        {
            File.SetLastAccessTimeUtc(filePath, now);
        }
        catch { }
    }

    /// <summary>Records a write to the cache, updating tracked size and triggering eviction if over budget.</summary>
    public void RecordWrite(string filePath, long bytesWritten)
    {
        if (bytesWritten <= 0) return;
        RecordAccess(filePath);
        Interlocked.Add(ref _currentSizeBytes, bytesWritten);
        long cum = Interlocked.Add(ref _cumulativeBytesWrittenSinceLastSweep, bytesWritten);

        if (cum >= CumulativeWriteThresholdBytes || CurrentSizeBytes > MaxCacheSizeBytes)
        {
            ScheduleEviction();
        }
    }

    /// <summary>Records removal of a file from cache.</summary>
    public void RecordDelete(string filePath, long bytesDeleted)
    {
        if (!string.IsNullOrEmpty(filePath))
        {
            _sessionAccessedFiles.TryRemove(filePath, out _);
        }
        if (bytesDeleted > 0)
        {
            Interlocked.Add(ref _currentSizeBytes, -bytesDeleted);
        }
    }

    /// <summary>Checks whether a file is currently protected by the session grace period.</summary>
    public bool IsProtected(string filePath, DateTime now, DateTime fileLastAccessUtc)
    {
        if (now - fileLastAccessUtc < SessionGracePeriod) return true;
        if (_sessionAccessedFiles.TryGetValue(filePath, out var sessionTouch) && (now - sessionTouch < SessionGracePeriod)) return true;
        return false;
    }

    /// <summary>Schedules a background eviction pass if not already running.</summary>
    public void ScheduleEviction(bool force = false)
    {
        if (_disposed) return;

        long now = Stopwatch.GetTimestamp();
        // Cooldown: at most once every 5 seconds unless forced
        if (!force && _lastEvictionTick != 0 && Stopwatch.GetElapsedTime(_lastEvictionTick, now).TotalSeconds < 5)
            return;

        if (Interlocked.CompareExchange(ref _isEvicting, 1, 0) == 0)
        {
            _lastEvictionTick = now;
            Task.Run(() =>
            {
                try
                {
                    RunEviction(force);
                }
                finally
                {
                    Interlocked.Exchange(ref _isEvicting, 0);
                }
            });
        }
    }

    /// <summary>Runs eviction synchronously or blocks until completed (useful for testing/manual flush).</summary>
    public void EnforceBudget()
    {
        RunEviction(force: true);
    }

    /// <summary>Recalculates current size from disk.</summary>
    public void RefreshSize()
    {
        long total = 0;
        try
        {
            if (Directory.Exists(_baseCacheDir))
            {
                var dir = new DirectoryInfo(_baseCacheDir);
                foreach (var fi in dir.EnumerateFiles())
                {
                    total += fi.Length;
                }
            }
            if (Directory.Exists(_decodedCacheDir))
            {
                var decDir = new DirectoryInfo(_decodedCacheDir);
                foreach (var fi in decDir.EnumerateFiles("*.dec"))
                {
                    total += fi.Length;
                }
            }
        }
        catch { }
        Interlocked.Exchange(ref _currentSizeBytes, total);
    }

    /// <summary>Returns detailed breakdown of cache usage (total, raw assets, decoded textures).</summary>
    public (long TotalBytes, long RawBytes, long DecodedBytes) GetDetailedSize()
    {
        long raw = 0;
        long dec = 0;
        try
        {
            if (Directory.Exists(_baseCacheDir))
            {
                var dir = new DirectoryInfo(_baseCacheDir);
                foreach (var fi in dir.EnumerateFiles())
                {
                    raw += fi.Length;
                }
            }
            if (Directory.Exists(_decodedCacheDir))
            {
                var decDir = new DirectoryInfo(_decodedCacheDir);
                foreach (var fi in decDir.EnumerateFiles("*.dec"))
                {
                    dec += fi.Length;
                }
            }
        }
        catch { }
        return (raw + dec, raw, dec);
    }

    /// <summary>Clears all asset and decoded cache files.</summary>
    public void Clear()
    {
        try
        {
            if (Directory.Exists(_baseCacheDir))
            {
                foreach (var f in Directory.GetFiles(_baseCacheDir))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            if (Directory.Exists(_decodedCacheDir))
            {
                foreach (var f in Directory.GetFiles(_decodedCacheDir, "*.dec"))
                {
                    try { File.Delete(f); } catch { }
                }
                foreach (var f in Directory.GetFiles(_decodedCacheDir, "*.tmp*"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            _sessionAccessedFiles.Clear();
            Interlocked.Exchange(ref _currentSizeBytes, 0);
            Interlocked.Exchange(ref _cumulativeBytesWrittenSinceLastSweep, 0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DiskCache] Error clearing cache: {ex.Message}");
        }
    }

    private void RunEviction(bool force)
    {
        try
        {
            if (!Directory.Exists(_baseCacheDir)) return;

            long limit = MaxCacheSizeBytes;
            long targetBytes = (long)(limit * LowWatermarkRatio);

            var rawFiles = new List<FileInfo>();
            var decFiles = new List<FileInfo>();
            var diagFiles = new List<FileInfo>();

            long total = 0;

            try
            {
                var dir = new DirectoryInfo(_baseCacheDir);
                foreach (var fi in dir.EnumerateFiles())
                {
                    total += fi.Length;
                    string ext = fi.Extension.ToLowerInvariant();
                    if (ext is ".baddecode" or ".degraded" || fi.Name.Contains(".tmp."))
                    {
                        diagFiles.Add(fi);
                    }
                    else
                    {
                        rawFiles.Add(fi);
                    }
                }
            }
            catch { }

            try
            {
                if (Directory.Exists(_decodedCacheDir))
                {
                    var decDir = new DirectoryInfo(_decodedCacheDir);
                    foreach (var fi in decDir.EnumerateFiles())
                    {
                        total += fi.Length;
                        if (fi.Name.Contains(".tmp."))
                        {
                            diagFiles.Add(fi);
                        }
                        else if (fi.Extension.Equals(".dec", StringComparison.OrdinalIgnoreCase))
                        {
                            decFiles.Add(fi);
                        }
                    }
                }
            }
            catch { }

            Interlocked.Exchange(ref _currentSizeBytes, total);

            if (!force && total <= limit)
            {
                Interlocked.Exchange(ref _cumulativeBytesWrittenSinceLastSweep, 0);
                return;
            }

            if (total <= targetBytes && !force) return;

            long current = total;
            DateTime now = DateTime.UtcNow;
            int filesDeleted = 0;
            long bytesReclaimed = 0;

            // Phase 1: Prune temporary and diagnostic artifacts first
            foreach (var file in diagFiles.OrderBy(f => f.LastAccessTimeUtc))
            {
                if (current <= targetBytes) break;
                if (IsProtected(file.FullName, now, file.LastAccessTimeUtc)) continue;
                try
                {
                    long len = file.Length;
                    file.Delete();
                    current -= len;
                    bytesReclaimed += len;
                    filesDeleted++;
                    _sessionAccessedFiles.TryRemove(file.FullName, out _);
                }
                catch { }
            }

            // Phase 2: Prune decoded cache files (*.dec) in LRU order
            // (Re-decodable from raw assets, freeing space without requiring network re-download)
            foreach (var file in decFiles.OrderBy(f => f.LastAccessTimeUtc))
            {
                if (current <= targetBytes) break;
                if (IsProtected(file.FullName, now, file.LastAccessTimeUtc)) continue;
                try
                {
                    long len = file.Length;
                    file.Delete();
                    current -= len;
                    bytesReclaimed += len;
                    filesDeleted++;
                    _sessionAccessedFiles.TryRemove(file.FullName, out _);
                }
                catch { }
            }

            // Phase 3: Prune raw assets (*.j2c, *.mesh, *.anim, *.mat) if still over target budget
            if (current > targetBytes)
            {
                foreach (var file in rawFiles.OrderBy(f => f.LastAccessTimeUtc))
                {
                    if (current <= targetBytes) break;
                    if (IsProtected(file.FullName, now, file.LastAccessTimeUtc)) continue;
                    try
                    {
                        long len = file.Length;
                        string fileName = file.Name;
                        file.Delete();
                        current -= len;
                        bytesReclaimed += len;
                        filesDeleted++;
                        _sessionAccessedFiles.TryRemove(file.FullName, out _);

                        // If deleting a texture (<id>_v5.j2c), also remove any leftover decoded files
                        int v5Idx = fileName.IndexOf("_v5.j2c", StringComparison.OrdinalIgnoreCase);
                        if (v5Idx > 0 && Directory.Exists(_decodedCacheDir))
                        {
                            string idStr = fileName[..v5Idx];
                            foreach (var leftover in Directory.GetFiles(_decodedCacheDir, $"{idStr}*.dec"))
                            {
                                try
                                {
                                    var leftoverFi = new FileInfo(leftover);
                                    long leftoverLen = leftoverFi.Length;
                                    leftoverFi.Delete();
                                    current -= leftoverLen;
                                    bytesReclaimed += leftoverLen;
                                    filesDeleted++;
                                    _sessionAccessedFiles.TryRemove(leftover, out _);
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            }

            Interlocked.Exchange(ref _currentSizeBytes, Math.Max(0, current));
            Interlocked.Exchange(ref _cumulativeBytesWrittenSinceLastSweep, 0);

            if (filesDeleted > 0)
            {
                Console.WriteLine($"[DiskCache] Eviction finished: freed {bytesReclaimed / (1024 * 1024)} MB ({filesDeleted} files). " +
                                  $"Current size: {current / (1024 * 1024)} MB / {limit / (1024 * 1024)} MB (target: {targetBytes / (1024 * 1024)} MB).");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DiskCache] Error during cache eviction: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _periodicTimer?.Dispose();
    }
}
