using System.Collections.Generic;
using System.IO;
using Godot;
using SLNG.App.UI.Prefs;

namespace SLNG.App.UI;

/// <summary>
/// The "Network" and "Cache" sections of the Network &amp; cache category (FEAT-UI-79): the maximum bandwidth (FEAT-NET-05),
/// the favorites' region check (FEAT-UI-77), the size of the asset and object caches with the button that clears them
/// (FEAT-NET-04), and under Advanced the decoded texture cache (FEAT-PERF-11).
/// </summary>
public sealed class NetworkPreferencesPage
{
    private string _cacheDir = null!;
    private System.Func<IReadOnlyList<string>> _objectCacheDirs = () => System.Array.Empty<string>();
    private System.Action? _clearObjectCache;
    private Label _sizeLabel = null!;
    private NetworkSettings _networkSettings = null!;
    private System.Action<float>? _onMaxBandwidthChanged;
    private System.Action<int>? _onCacheSizeLimitChanged;
    private System.Action? _clearDecodedCache;
    private bool _dragging;
    private bool _cacheLimitDragging;

    /// <summary>Fills <paramref name="network"/> and <paramref name="cache"/> (Network &amp; cache), which Boot has already
    /// put in their places. Call once, after the sections are in the window's tree.</summary>
    /// <param name="cacheDir">The asset cache directory.</param>
    /// <param name="objectCacheDirs">FEAT-NET-04: where the object cache keeps one file per region.
    /// BUG-GRID-01: there is one such directory per grid, so this is asked each time rather than
    /// fixed at startup, before any login has said which grid.</param>
    /// <param name="clearObjectCache">Forgets what the running session holds of the object cache in
    /// memory (there may be no session yet); the files are removed here either way.</param>
    /// <param name="networkSettings">FEAT-NET-05: holds the persisted maximum bandwidth.</param>
    /// <param name="onMaxBandwidthChanged">Applies a new maximum bandwidth to the running session (there
    /// may be none yet). Every call makes the viewer resend its throttle to every simulator, so the
    /// slider only calls it once a drag is over, not on each tick.</param>
    /// <param name="onDecodedCacheToggled">FEAT-PERF-11: toggles decoded-texture disk caching.</param>
    /// <param name="clearDecodedCache">FEAT-PERF-11: clears in-memory decoded-texture state and stats.</param>
    /// <param name="onCheckFavoriteRegionsToggled">FEAT-UI-77: told when the favorites' region check is switched.</param>
    /// <param name="onCacheSizeLimitChanged">FEAT-CACHE-01: told when maximum disk cache size limit changes.</param>
    public void Build(PrefsSection network, PrefsSection cache, string cacheDir, NetworkSettings networkSettings,
        System.Action<float>? onMaxBandwidthChanged = null,
        System.Func<IReadOnlyList<string>>? objectCacheDirs = null,
        System.Action? clearObjectCache = null,
        System.Action<bool>? onDecodedCacheToggled = null,
        System.Action? clearDecodedCache = null,
        System.Action<bool>? onCheckFavoriteRegionsToggled = null,
        System.Action<int>? onCacheSizeLimitChanged = null)
    {
        _cacheDir = cacheDir;
        _networkSettings = networkSettings;
        _onMaxBandwidthChanged = onMaxBandwidthChanged;
        _onCacheSizeLimitChanged = onCacheSizeLimitChanged;
        _clearDecodedCache = clearDecodedCache;
        if (objectCacheDirs != null) _objectCacheDirs = objectCacheDirs;
        _clearObjectCache = clearObjectCache;

        string net = $"{network.CategoryId}.{network.Id}";
        AddMaxBandwidthRow(network, $"{net}.max_bandwidth");

        // FEAT-UI-77: a checkbox takes effect at once, like the others.
        network.AddCheck($"{net}.check_favorite_regions", "ui.preferences.check_favorite_regions",
            _networkSettings.CheckFavoriteRegions,
            toggled =>
            {
                _networkSettings.SetCheckFavoriteRegions(toggled);
                onCheckFavoriteRegionsToggled?.Invoke(toggled);
            },
            hintKey: "ui.preferences.check_favorite_regions_hint");

        string cch = $"{cache.CategoryId}.{cache.Id}";
        var clearBtn = new Button { Text = L10n.Tr("ui.preferences.clear_cache"), FocusMode = Control.FocusModeEnum.None };
        clearBtn.Pressed += ClearCache;
        _sizeLabel = PrefsStyle.MakeLabel(GetCacheSizeText());
        var sizeRow = new HBoxContainer();
        sizeRow.AddThemeConstantOverride("separation", PrefsStyle.ColumnGap);
        sizeRow.AddChild(clearBtn);
        sizeRow.AddChild(_sizeLabel);
        cache.AddBlock($"{cch}.size", sizeRow, null, false, "ui.preferences.clear_cache");

        AddCacheSizeLimitRow(cache, $"{cch}.cache_size_limit");

        cache.AddCheck($"{cch}.decoded_cache_enabled", "ui.preferences.decoded_cache_enabled",
            _networkSettings.DecodedCacheEnabled,
            toggled =>
            {
                _networkSettings.SetDecodedCacheEnabled(toggled);
                onDecodedCacheToggled?.Invoke(toggled);
            },
            advanced: true);
    }

    /// <summary>FEAT-NET-05: the bandwidth slider. The value label follows the drag, but the setting
    /// is saved and applied only when a drag ends (or on a keyboard / wheel step, which has no drag):
    /// applying it resends the throttle to every simulator, which must not happen on every tick.</summary>
    private void AddMaxBandwidthRow(PrefsSection section, string id)
    {
        var row = section.AddSlider(id, "ui.preferences.max_bandwidth",
                NetworkSettings.MinBandwidthKbps, NetworkSettings.CeilingBandwidthKbps, 100, _networkSettings.MaxBandwidthKbps,
                FormatKbps, v =>
                {
                    if (!_dragging) ApplyMaxBandwidth((float)v);
                },
                hintKey: "ui.preferences.max_bandwidth_hint")
            .WithKeywords("ThrottleBandwidthKBPS");

        var slider = row.ControlAs<HSlider>()!;
        slider.DragStarted += () => _dragging = true;
        slider.DragEnded += valueChanged =>
        {
            _dragging = false;
            if (valueChanged) ApplyMaxBandwidth((float)slider.Value);
        };
    }

    private void ApplyMaxBandwidth(float kbps)
    {
        _networkSettings.SetMaxBandwidthKbps(kbps);
        _onMaxBandwidthChanged?.Invoke(_networkSettings.MaxBandwidthKbps);
    }

    private static string FormatKbps(double kbps) => $"{kbps:0} kbps";

    private void AddCacheSizeLimitRow(PrefsSection section, string id)
    {
        var row = section.AddSlider(id, "ui.preferences.cache_size_limit",
                NetworkSettings.MinCacheSizeLimitMb, NetworkSettings.MaxCacheSizeLimitMb, 1024, _networkSettings.CacheSizeLimitMb,
                FormatGb, v =>
                {
                    if (!_cacheLimitDragging) ApplyCacheSizeLimit((int)v);
                },
                hintKey: "ui.preferences.cache_size_limit_hint")
            .WithKeywords("CacheSize", "DiskCache");

        var slider = row.ControlAs<HSlider>()!;
        slider.DragStarted += () => _cacheLimitDragging = true;
        slider.DragEnded += valueChanged =>
        {
            _cacheLimitDragging = false;
            if (valueChanged) ApplyCacheSizeLimit((int)slider.Value);
        };
    }

    private void ApplyCacheSizeLimit(int mb)
    {
        _networkSettings.SetCacheSizeLimitMb(mb);
        _onCacheSizeLimitChanged?.Invoke(_networkSettings.CacheSizeLimitMb);
        if (_sizeLabel != null) _sizeLabel.Text = GetCacheSizeText();
    }

    private static string FormatGb(double mb) => $"{mb / 1024:0} GB";

    private void ClearCache()
    {
        try
        {
            DeleteFiles(_cacheDir, "*");
            string decodedDir = Path.Combine(_cacheDir, "decoded");
            DeleteFiles(decodedDir, "*.dec");
            DeleteFiles(decodedDir, "*.tmp*");

            // FEAT-PERF-11: clear decoded texture cache
            _clearDecodedCache?.Invoke();

            // FEAT-NET-04: the object cache goes with it. The running session first, so it does not
            // write back what it still holds in memory; then whatever is on disk.
            _clearObjectCache?.Invoke();
            foreach (var dir in _objectCacheDirs())
                DeleteFiles(dir, "*.slobj*");

            _sizeLabel.Text = GetCacheSizeText();
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[NetworkPreferences] Failed to clear cache: {ex.Message}");
        }
    }

    private static void DeleteFiles(string directory, string pattern)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

        foreach (var file in Directory.GetFiles(directory, pattern))
        {
            try { File.Delete(file); } catch { }
        }
    }

    private string GetCacheSizeText()
    {
        long assetCacheSize = DirectorySize(_cacheDir, "*") + DirectorySize(Path.Combine(_cacheDir, "decoded"), "*.dec");
        long limitBytes = (long)_networkSettings.CacheSizeLimitMb * 1024L * 1024L;
        return $"{FormatSize(assetCacheSize)} / {FormatSize(limitBytes)} ({L10n.TrFormat("ui.preferences.cache_size", FormatSize(assetCacheSize), FormatSize(ObjectCacheSize()))})";
    }

    private long ObjectCacheSize()
    {
        long total = 0;
        foreach (var dir in _objectCacheDirs())
            total += DirectorySize(dir, "*.slobj");
        return total;
    }

    private static long DirectorySize(string directory, string pattern)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return 0;

        long size = 0;
        try
        {
            foreach (var file in Directory.GetFiles(directory, pattern))
                size += new FileInfo(file).Length;
        }
        catch { }
        return size;
    }

    /// <summary>Formats bytes into human-readable MB or GB string.</summary>
    private static string FormatSize(long bytes)
        => bytes < 10L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):0.0} MB"
         : bytes < 1024L * 1024 * 1024 ? $"{bytes / (1024 * 1024)} MB"
         : $"{bytes / (1024.0 * 1024.0 * 1024.0):0.0} GB";
}
