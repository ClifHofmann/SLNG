using Godot;

namespace SLNG.App.UI;

/// <summary>
/// Global UI scale (FEAT-UI-07), persisted to user://preferences.cfg (same ConfigFile pattern
/// and file as ToolbarSettings) under its own "display" section. Single source of truth for
/// SLNGWindow.GlobalUiScale -- Load() pushes the saved value into every SLNGWindow via the
/// static scale broadcast, SetScale() persists and rebroadcasts on change.
/// </summary>
public sealed class UiSettings
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string Section = "display";

    public float Scale { get; private set; } = 1.0f;
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

    public void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) == Error.Ok)
        {
            Scale = Mathf.Clamp((float)cfg.GetValue(Section, "ui_scale", 1.0), SLNGWindow.MinUiScale, SLNGWindow.MaxUiScale);
            Language = (string)cfg.GetValue(Section, "language", "en-US");
            ShowTopBarFps = (bool)cfg.GetValue(Section, "show_top_bar_fps", true);
            ShowLegacyNames = (bool)cfg.GetValue(Section, "show_legacy_names", true);
            ShowGroupTitles = (bool)cfg.GetValue(Section, "show_group_titles", true);
            ShowDisplayNames = (bool)cfg.GetValue(Section, "show_display_names", true);
            HideOwnGroupTitle = (bool)cfg.GetValue(Section, "hide_own_group_title", false);
            BuildGridSpacing = Mathf.Clamp((float)cfg.GetValue(Section, "build_grid_spacing", 1.0), 0.01f, 64f);
        }
        SLNGWindow.SetGlobalUiScale(Scale);
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

    public void SetScale(float scale)
    {
        Scale = Mathf.Clamp(scale, SLNGWindow.MinUiScale, SLNGWindow.MaxUiScale);

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath); // preserve sections owned by other features (e.g. ToolbarSettings)
        cfg.SetValue(Section, "ui_scale", Scale);
        cfg.Save(ConfigPath);

        SLNGWindow.SetGlobalUiScale(Scale);
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
