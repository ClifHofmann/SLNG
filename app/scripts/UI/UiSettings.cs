using Godot;
using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>
/// Global UI scale (FEAT-UI-07, reworked in FEAT-UI-42), persisted to user://preferences.cfg (same
/// ConfigFile pattern and file as ToolbarSettings) under its own "display" section.
/// <para>The scale is either AUTOMATIC (follows the operating system's scale for the screen the window
/// is on) or MANUAL (the number the user chose, the same on every screen). Both are stored:
/// <c>ui_scale_auto</c> says which one is in force, <c>ui_scale</c> keeps the last manual number so
/// switching automatic off again returns to it. A <c>ui_scale</c> saved before the automatic state
/// existed has no flag and so counts as manual -- see <see cref="UiScalePolicy.IsAutomatic"/>.</para>
/// <para>The effective scale is put on the root window by <see cref="UiScale.Apply"/>.</para>
/// </summary>
public sealed class UiSettings
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string Section = "display";

    /// <summary>The scale in force: the display's own when <see cref="ScaleAutomatic"/>, otherwise
    /// <see cref="ManualScale"/>. Always inside the policy's range.</summary>
    public float Scale { get; private set; } = 1.0f;

    /// <summary>FEAT-UI-42: true while the scale follows the operating system's display scale.</summary>
    public bool ScaleAutomatic { get; private set; } = true;

    /// <summary>FEAT-UI-42: the number the user chose with the slider (what <see cref="Scale"/> is
    /// while automatic is off). Starts at the effective scale when the user first takes control, so
    /// nothing jumps.</summary>
    public float ManualScale { get; private set; } = 1.0f;

    private bool _hasManualScale;
    public string Language { get; private set; } = "en-US";
    public bool ShowTopBarFps { get; private set; } = true;

    /// <summary>FEAT-UI-31: whether an avatar's legacy username is shown under its Display
    /// Name in the nametag, in parentheses and a size smaller. Default on, matching the
    /// reference viewer: the Display Name is chosen freely and can be changed, so the username
    /// is the only stable identity and hiding it by default would make impersonation easier.
    /// An avatar that never set a Display Name has only the one line either way.</summary>
    public bool ShowLegacyNames { get; private set; } = true;

    /// <summary>FEAT-UI-30: whether group titles appear in nametags. Mirrors the reference
    /// viewer's <c>NameTagShowGroupTitles</c>, default on. Purely local -- it changes what YOU
    /// see, including above your own avatar; it cannot stop anyone else seeing your title,
    /// because the simulator broadcasts it. The only way to not have one shown is to hold no
    /// active group, or a role whose title is blank.</summary>
    public bool ShowGroupTitles { get; private set; } = true;

    /// <summary>FEAT-UI-31: whether Display Names are used at all. Mirrors the reference viewer's
    /// <c>NameTagShowDisplayNames</c>, default on. Off means every nametag shows the username,
    /// and the parenthesised second line disappears with it -- there is nothing left to
    /// disambiguate.</summary>
    public bool ShowDisplayNames { get; private set; } = true;

    /// <summary>FEAT-UI-30: hide the local agent's group title from EVERYONE. Unlike the three
    /// toggles above this is NOT a display setting -- it rides the AgentUpdate packet as
    /// AU_FLAGS_HIDETITLE and the simulator stops broadcasting the title, so other people's
    /// viewers never receive it. Mirrors the reference viewer's <c>RenderHideGroupTitle</c>,
    /// default OFF.</summary>
    public bool HideOwnGroupTitle { get; private set; }

    /// <summary>FEAT-UI-04: the build grid's cell size in metres — the spacing of the move
    /// gizmo's plane grid and of its single-axis ruler's ticks, and therefore what a snapped
    /// drag lands on. Default 1 m, SL's own build grid.</summary>
    public float BuildGridSpacing { get; private set; } = 1.0f;

    /// <summary>FEAT-UI-04: the angular snap step in degrees for a rotation drag, and the
    /// spacing of the gizmo's dial ticks. Its own setting rather than sharing the grid's,
    /// because metres and degrees are not the same choice. Default 15°.</summary>
    public float BuildRotationSnapDegrees { get; private set; } = 15.0f;

    public void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) == Error.Ok)
        {
            _hasManualScale = cfg.HasSectionKey(Section, "ui_scale");
            ManualScale = UiScalePolicy.Clamp((float)cfg.GetValue(Section, "ui_scale", 1.0));
            bool? savedAuto = cfg.HasSectionKey(Section, "ui_scale_auto")
                ? (bool)cfg.GetValue(Section, "ui_scale_auto", true)
                : null;
            ScaleAutomatic = UiScalePolicy.IsAutomatic(savedAuto, _hasManualScale);
            Language = (string)cfg.GetValue(Section, "language", "en-US");
            ShowTopBarFps = (bool)cfg.GetValue(Section, "show_top_bar_fps", true);
            ShowLegacyNames = (bool)cfg.GetValue(Section, "show_legacy_names", true);
            ShowGroupTitles = (bool)cfg.GetValue(Section, "show_group_titles", true);
            ShowDisplayNames = (bool)cfg.GetValue(Section, "show_display_names", true);
            HideOwnGroupTitle = (bool)cfg.GetValue(Section, "hide_own_group_title", false);
            BuildGridSpacing = Mathf.Clamp((float)cfg.GetValue(Section, "build_grid_spacing", 1.0), 0.01f, 64f);
            BuildRotationSnapDegrees = Mathf.Clamp((float)cfg.GetValue(Section, "build_rotation_snap_degrees", 15.0), 0.1f, 90f);
        }
        // No config file at all = a fresh install: ScaleAutomatic keeps its default (true).
        UiScale.RefreshOsScale();
        ApplyScale();
    }

    /// <summary>Resolves the scale from the current choice and the display, and puts it on the
    /// window. In memory only -- nothing is written.</summary>
    private void ApplyScale()
    {
        Scale = CommandLineScale ?? UiScalePolicy.Resolve(ScaleAutomatic, ManualScale, UiScale.OsScale);
        UiScale.Apply(Scale);
    }

    /// <summary>FEAT-UI-42, a developer aid: <c>-- --ui-scale=2</c> forces the scale for this run only,
    /// so a layout can be looked at (or screenshotted) at 200 % on a 100 % screen. It wins over both
    /// the saved choice and the display, is held in memory, and is never written to the preferences.</summary>
    private static readonly float? CommandLineScale = ParseCommandLineScale();

    private static float? ParseCommandLineScale()
    {
        const string prefix = "--ui-scale=";
        foreach (var args in new[] { OS.GetCmdlineArgs(), OS.GetCmdlineUserArgs() })
            foreach (string arg in args)
                if (arg.StartsWith(prefix, System.StringComparison.Ordinal)
                    && float.TryParse(arg.Substring(prefix.Length), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float value))
                    return UiScalePolicy.Clamp(value);
        return null;
    }

    /// <summary>FEAT-UI-42: call when the display may have changed (the window moved to another
    /// screen, the Windows scale was changed). Only has an effect while automatic.</summary>
    public void OnDisplayChanged()
    {
        if (UiScale.RefreshOsScale() && ScaleAutomatic) ApplyScale();
    }

    /// <summary>FEAT-UI-31. Raises <see cref="ShowLegacyNamesChanged"/> so the renderer can
    /// rebuild nametags immediately rather than at the next avatar update -- otherwise a
    /// motionless avatar keeps the old tag until it moves.</summary>
    public void SetShowLegacyNames(bool show)
    {
        ShowLegacyNames = show;

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath);
        cfg.SetValue(Section, "show_legacy_names", ShowLegacyNames);
        cfg.Save(ConfigPath);

        NameTagOptionsChanged?.Invoke();
    }

    /// <summary>FEAT-UI-30.</summary>
    public void SetShowGroupTitles(bool show)
    {
        ShowGroupTitles = show;

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath);
        cfg.SetValue(Section, "show_group_titles", ShowGroupTitles);
        cfg.Save(ConfigPath);

        NameTagOptionsChanged?.Invoke();
    }

    /// <summary>FEAT-UI-31.</summary>
    public void SetShowDisplayNames(bool show)
    {
        ShowDisplayNames = show;

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath);
        cfg.SetValue(Section, "show_display_names", ShowDisplayNames);
        cfg.Save(ConfigPath);

        NameTagOptionsChanged?.Invoke();
    }

    /// <summary>FEAT-UI-04.</summary>
    public void SetBuildGridSpacing(float metres)
    {
        BuildGridSpacing = Mathf.Clamp(metres, 0.01f, 64f);

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath);
        cfg.SetValue(Section, "build_grid_spacing", BuildGridSpacing);
        cfg.Save(ConfigPath);

        BuildGridSpacingChanged?.Invoke(BuildGridSpacing);
    }

    public event System.Action<float>? BuildGridSpacingChanged;

    /// <summary>FEAT-UI-04.</summary>
    public void SetBuildRotationSnapDegrees(float degrees)
    {
        BuildRotationSnapDegrees = Mathf.Clamp(degrees, 0.1f, 90f);

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath);
        cfg.SetValue(Section, "build_rotation_snap_degrees", BuildRotationSnapDegrees);
        cfg.Save(ConfigPath);

        BuildRotationSnapDegreesChanged?.Invoke(BuildRotationSnapDegrees);
    }

    public event System.Action<float>? BuildRotationSnapDegreesChanged;

    /// <summary>FEAT-UI-30. Separate event from the display toggles: this one has to reach
    /// GridSession, not the renderer, because it changes an outgoing packet.</summary>
    public void SetHideOwnGroupTitle(bool hide)
    {
        HideOwnGroupTitle = hide;

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath);
        cfg.SetValue(Section, "hide_own_group_title", HideOwnGroupTitle);
        cfg.Save(ConfigPath);

        HideOwnGroupTitleChanged?.Invoke(HideOwnGroupTitle);
    }

    public event System.Action<bool>? HideOwnGroupTitleChanged;

    /// <summary>Raised when any of the three nametag toggles changes. One event rather than three,
    /// because the renderer's reaction is the same for all of them: re-read all three and rebuild
    /// the live tags.</summary>
    public event System.Action? NameTagOptionsChanged;

    public void SetShowTopBarFps(bool show)
    {
        ShowTopBarFps = show;

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath);
        cfg.SetValue(Section, "show_top_bar_fps", ShowTopBarFps);
        cfg.Save(ConfigPath);
    }

    /// <summary>The user moved the slider: this number now wins, on every screen, until they switch
    /// back to automatic. Persisted.</summary>
    public void SetScale(float scale)
    {
        ManualScale = UiScalePolicy.Clamp(scale);
        _hasManualScale = true;
        ScaleAutomatic = false;
        PersistScale();
        ApplyScale();
    }

    /// <summary>FEAT-UI-42: the "automatic" checkbox. Turning it OFF keeps the scale the screen is
    /// at right now as the manual one (unless the user has chosen a number before, which then comes
    /// back), so the interface does not change size under the cursor.</summary>
    public void SetScaleAutomatic(bool automatic)
    {
        if (automatic == ScaleAutomatic) return;
        ScaleAutomatic = automatic;
        if (!automatic && !_hasManualScale)
        {
            ManualScale = Scale;
            _hasManualScale = true;
        }
        PersistScale();
        ApplyScale();
    }

    private void PersistScale()
    {
        var cfg = new ConfigFile();
        cfg.Load(ConfigPath); // preserve sections owned by other features (e.g. ToolbarSettings)
        cfg.SetValue(Section, "ui_scale_auto", ScaleAutomatic);
        if (_hasManualScale) cfg.SetValue(Section, "ui_scale", ManualScale);
        cfg.Save(ConfigPath);
    }

    public void SetLanguage(string language)
    {
        Language = language;

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath);
        cfg.SetValue(Section, "language", Language);
        cfg.Save(ConfigPath);
    }

}
