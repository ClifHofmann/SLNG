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

    public void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) == Error.Ok)
        {
            Scale = Mathf.Clamp((float)cfg.GetValue(Section, "ui_scale", 1.0), SLNGWindow.MinUiScale, SLNGWindow.MaxUiScale);
            Language = (string)cfg.GetValue(Section, "language", "en-US");
            ShowTopBarFps = (bool)cfg.GetValue(Section, "show_top_bar_fps", true);
            ShowLegacyNames = (bool)cfg.GetValue(Section, "show_legacy_names", true);
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

        ShowLegacyNamesChanged?.Invoke(ShowLegacyNames);
    }

    public event System.Action<bool>? ShowLegacyNamesChanged;

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
