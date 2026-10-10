using Godot;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-AVATAR-03: the local hover-height offset, persisted to user://preferences.cfg under a
/// "avatar" section -- same ConfigFile file and pattern as <see cref="DofSettings"/> /
/// <see cref="CameraSettings"/>, so all of them coexist without knowing about each other.
///
/// This holds only the user's chosen value and its persistence; it does not talk to the network
/// or the renderer. Boot.cs owns pushing a changed value onto both the outbound
/// GridSession.SetHoverHeight call and the local self AvatarComponent.HoverOffsetZ so the render
/// updates immediately instead of waiting for the sim to echo it back.
/// </summary>
public sealed class AvatarHoverSettings
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string SectionPrefix = "avatar_hover_";
    private const string LegacySection = "avatar";

    public const float MinHoverHeight = -2.0f;
    public const float MaxHoverHeight = 2.0f;
    public const float DefaultHoverHeight = 0f;

    private string? _gridSlug;
    private string? _agentId;

    /// <summary>False keeps changes in memory only. Used by selftest to keep user preferences untouched.</summary>
    public static bool Persist { get; set; } = true;

    public float HoverHeight { get; private set; } = DefaultHoverHeight;

    public static string? GetSection(string? gridSlug, string? agentId)
    {
        if (string.IsNullOrWhiteSpace(gridSlug) || string.IsNullOrWhiteSpace(agentId))
            return null;

        if (System.Guid.TryParse(agentId, out var guid) && guid == System.Guid.Empty)
            return null;

        return $"{SectionPrefix}{gridSlug.Trim().ToLowerInvariant()}_{agentId.Trim().ToLowerInvariant()}";
    }

    public void Load(string? gridSlug = null, string? agentId = null)
    {
        if (gridSlug != null || agentId != null)
        {
            _gridSlug = gridSlug;
            _agentId = agentId;
        }

        string? section = GetSection(_gridSlug, _agentId);
        if (section == null)
        {
            HoverHeight = DefaultHoverHeight;
            return;
        }

        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok)
        {
            HoverHeight = DefaultHoverHeight;
            return;
        }

        // One-time cleanup of legacy/corrupted empty-guid sections
        bool dirty = false;
        if (cfg.HasSection("avatar_hover_00000000-0000-0000-0000-000000000000"))
        {
            cfg.EraseSection("avatar_hover_00000000-0000-0000-0000-000000000000");
            dirty = true;
        }
        foreach (var s in cfg.GetSections())
        {
            if (s.StartsWith(SectionPrefix) && s.EndsWith("00000000-0000-0000-0000-000000000000"))
            {
                cfg.EraseSection(s);
                dirty = true;
            }
        }

        // Migrate legacy un-scoped [avatar] hover_height if target section does not exist yet
        if (!cfg.HasSection(section) && cfg.HasSection(LegacySection) && cfg.HasSectionKey(LegacySection, "hover_height"))
        {
            cfg.SetValue(section, "hover_height", cfg.GetValue(LegacySection, "hover_height"));
            cfg.EraseSection(LegacySection);
            dirty = true;
        }

        if (dirty && Persist)
        {
            cfg.Save(ConfigPath);
        }

        HoverHeight = Clamp((float)cfg.GetValue(section, "hover_height", DefaultHoverHeight));
    }

    public void Reset()
    {
        _gridSlug = null;
        _agentId = null;
        HoverHeight = DefaultHoverHeight;
    }

    // Same persist-on-release pattern as DofSettings: an HSlider fires ValueChanged on every step
    // of a drag, so the caller applies live with persist:false while dragging and writes once on
    // DragEnded -- otherwise every pixel of drag would be a synchronous ConfigFile.Save().
    public void SetHoverHeight(float value, bool persist = true)
    {
        float clamped = Clamp(value);
        HoverHeight = clamped;
        if (!persist || !Persist) return;

        string? section = GetSection(_gridSlug, _agentId);
        if (section == null) return;

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath); // preserve sections owned by other features (DofSettings, CameraSettings, ...)
        cfg.SetValue(section, "hover_height", clamped);
        cfg.Save(ConfigPath);
    }

    public void ResetToDefault() => SetHoverHeight(DefaultHoverHeight);

    private static float Clamp(float v) => Mathf.Clamp(v, MinHoverHeight, MaxHoverHeight);
}
