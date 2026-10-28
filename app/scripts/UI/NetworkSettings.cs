using Godot;

namespace SLNG.App.UI;

/// <summary>
/// Network preferences, persisted to user://preferences.cfg under a "network" section -- same
/// ConfigFile pattern and file as <see cref="CameraSettings"/> / <see cref="UiSettings"/>.
///
/// FEAT-NET-05: the most downstream bandwidth the viewer asks each simulator for. This is the
/// reference viewer's <c>ThrottleBandwidthKBPS</c> (app_settings/settings.xml, default 3000); the
/// viewer's own slider runs 100-10000 in steps of 100 (panel_preferences_setup.xml). SLNG's slider
/// starts at <see cref="MinBandwidthKbps"/> = 500 -- a floor chosen for the setting, not measured:
/// the low end is for a slow line, and 100 kbps would leave a busy region loading for minutes. The
/// page applies a change to <c>GridSession.MaxBandwidthKbps</c>, which resends the AgentThrottle
/// packet to every connected simulator.
/// </summary>
public sealed class NetworkSettings
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string Section = "network";

    public const float MinBandwidthKbps = 500f;

    /// <summary>Upper end of the range. Not called <c>MaxBandwidthKbps</c>: that is the setting itself.</summary>
    public const float CeilingBandwidthKbps = 10000f;
    public const float DefaultBandwidthKbps = SLNG.Net.ViewerThrottlePresets.DefaultBandwidthKbps;

    /// <summary>Maximum downstream bandwidth in kbps -- see <see cref="DefaultBandwidthKbps"/>.</summary>
    public float MaxBandwidthKbps { get; private set; } = DefaultBandwidthKbps;

    /// <summary>FEAT-PERF-11: whether decoded textures are cached to disk to skip J2K decodes on revisits.</summary>
    public bool DecodedCacheEnabled { get; private set; } = true;

    /// <summary>FEAT-PERF-11: maximum decoded texture disk cache budget in megabytes (default 16 GB).</summary>
    // BUG-PERF-20: 4 GB held about one region's decoded textures (RGBA + BC copies share it), so a region hop evicted the last one.
    public int DecodedCacheSizeMb { get; private set; } = 16384;

    /// <summary>FEAT-CACHE-01: maximum total asset disk cache size in megabytes (default 20 GB = 20,480 MB).</summary>
    public const int DefaultCacheSizeLimitMb = 20480;
    public const int MinCacheSizeLimitMb = 1024;
    public const int MaxCacheSizeLimitMb = 65536;

    public int CacheSizeLimitMb { get; private set; } = DefaultCacheSizeLimitMb;

    /// <summary>FEAT-UI-77: whether the regions of the favorites are checked every few minutes while playing. Off by default: it
    /// is a question to the grid per region that nobody asked for.</summary>
    public bool CheckFavoriteRegions { get; private set; }

    public void SetCheckFavoriteRegions(bool enabled)
    {
        CheckFavoriteRegions = enabled;

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath);
        cfg.SetValue(Section, "check_favorite_regions", enabled);
        cfg.Save(ConfigPath);
    }

    public void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return;
        CheckFavoriteRegions = (bool)cfg.GetValue(Section, "check_favorite_regions", false);
        MaxBandwidthKbps = Sanitize((float)cfg.GetValue(Section, "max_bandwidth_kbps", DefaultBandwidthKbps));
        DecodedCacheEnabled = (bool)cfg.GetValue(Section, "decoded_cache_enabled", true);
        DecodedCacheSizeMb = Mathf.Max(256, (int)cfg.GetValue(Section, "decoded_cache_size_mb", 16384));
        CacheSizeLimitMb = Mathf.Clamp((int)cfg.GetValue(Section, "cache_size_limit_mb", DefaultCacheSizeLimitMb), MinCacheSizeLimitMb, MaxCacheSizeLimitMb);
    }

    public void SetMaxBandwidthKbps(float value)
    {
        var clamped = Sanitize(value);
        MaxBandwidthKbps = clamped;

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath); // preserve sections owned by other features (UiSettings, CameraSettings, ...)
        cfg.SetValue(Section, "max_bandwidth_kbps", clamped);
        cfg.Save(ConfigPath);
    }

    public void SetDecodedCacheEnabled(bool enabled)
    {
        DecodedCacheEnabled = enabled;

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath);
        cfg.SetValue(Section, "decoded_cache_enabled", enabled);
        cfg.Save(ConfigPath);
    }

    public void SetCacheSizeLimitMb(int valueMb)
    {
        var clamped = Mathf.Clamp(valueMb, MinCacheSizeLimitMb, MaxCacheSizeLimitMb);
        CacheSizeLimitMb = clamped;

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath);
        cfg.SetValue(Section, "cache_size_limit_mb", clamped);
        cfg.Save(ConfigPath);
    }

    /// <summary>A hand-edited NaN / infinity in the file means "never chosen", not "clamp to an edge".</summary>
    private static float Sanitize(float value)
        => float.IsFinite(value) ? Mathf.Clamp(value, MinBandwidthKbps, CeilingBandwidthKbps) : DefaultBandwidthKbps;
}
