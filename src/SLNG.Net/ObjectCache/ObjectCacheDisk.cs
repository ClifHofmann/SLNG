namespace SLNG.Net.ObjectCache;

/// <summary>
/// The directory the object caches live in (FEAT-NET-04, phase 2): one <see cref="ObjectCacheFile"/>
/// per region, written whole and renamed into place so a crash leaves the old file or the new one,
/// never half of either, and held to a size budget by dropping the regions visited longest ago.
///
/// <para>None of it is allowed to hurt the session: every method swallows its own I/O failure and
/// reports it, because a cache that cannot be read is an empty cache and one that cannot be written
/// is a cache that does not persist -- neither is a reason to lose a teleport.</para>
/// </summary>
internal sealed class ObjectCacheDisk
{
    public const long DefaultMaxBytes = 512L * 1024 * 1024;

    private readonly string _directory;
    private readonly long _maxBytes;

    public ObjectCacheDisk(string directory, long maxBytes = DefaultMaxBytes)
    {
        _directory = directory;
        _maxBytes = maxBytes;
    }

    private string PathFor(RegionKey key) => Path.Combine(_directory, ObjectCacheFile.FileName(key));

    /// <summary>The objects saved for this region under this cache id. A file that does not read
    /// back whole is deleted, so it is not tried again and again.</summary>
    public bool TryLoad(RegionKey key, out List<CachedObject> objects)
    {
        objects = new List<CachedObject>();
        var path = PathFor(key);
        try
        {
            if (!File.Exists(path)) return false;

            using var stream = File.OpenRead(path);
            if (ObjectCacheFile.TryRead(stream, key, out objects)) return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        TryDelete(path);
        return false;
    }

    /// <summary>Writes the region's objects, replacing the file, then keeps the directory within its
    /// budget. Files for the same region under an older cache id are void and go with it.</summary>
    public bool TrySave(RegionKey key, IReadOnlyCollection<CachedObject> objects)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var path = PathFor(key);
            var temp = path + ".tmp";
            using (var stream = File.Create(temp))
                ObjectCacheFile.Write(stream, key, objects, DateTimeOffset.UtcNow);
            File.Move(temp, path, overwrite: true);

            DeleteOtherGenerations(key);
            TrimToBudget();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Removes every cache file.</summary>
    public void Clear()
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var file in Directory.EnumerateFiles(_directory, "*" + ObjectCacheFile.Extension + "*"))
            TryDelete(file);
    }

    public long TotalBytes()
    {
        if (!Directory.Exists(_directory)) return 0;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(_directory, "*" + ObjectCacheFile.Extension))
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException) { }
        }
        return total;
    }

    /// <summary>Drops the files of the regions written longest ago until the directory fits.</summary>
    public void TrimToBudget()
    {
        if (!Directory.Exists(_directory)) return;

        var files = Directory.EnumerateFiles(_directory, "*" + ObjectCacheFile.Extension)
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();
        long total = files.Sum(f => f.Length);
        foreach (var file in files)
        {
            if (total <= _maxBytes) break;
            total -= file.Length;
            TryDelete(file.FullName);
        }
    }

    private void DeleteOtherGenerations(RegionKey key)
    {
        var prefix = $"{key.Handle:x16}-";
        var keep = ObjectCacheFile.FileName(key);
        foreach (var file in Directory.EnumerateFiles(_directory, prefix + "*" + ObjectCacheFile.Extension))
            if (!string.Equals(Path.GetFileName(file), keep, StringComparison.OrdinalIgnoreCase))
                TryDelete(file);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
