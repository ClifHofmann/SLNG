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

    /// <summary>FEAT-PERF-11: maximum decoded texture disk cache budget in megabytes (default 4 GB).</summary>
    public int DecodedCacheSizeMb { get; private set; } = 4096;

    public void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return;
        MaxBandwidthKbps = Sanitize((float)cfg.GetValue(Section, "max_bandwidth_kbps", DefaultBandwidthKbps));
        DecodedCacheEnabled = (bool)cfg.GetValue(Section, "decoded_cache_enabled", true);
        DecodedCacheSizeMb = Mathf.Max(256, (int)cfg.GetValue(Section, "decoded_cache_size_mb", 4096));
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

    /// <summary>A hand-edited NaN / infinity in the file means "never chosen", not "clamp to an edge".</summary>
    private static float Sanitize(float value)
        => float.IsFinite(value) ? Mathf.Clamp(value, MinBandwidthKbps, CeilingBandwidthKbps) : DefaultBandwidthKbps;
}
