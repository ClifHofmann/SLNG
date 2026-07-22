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

    public void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) == Error.Ok)
        {
            Scale = Mathf.Clamp((float)cfg.GetValue(Section, "ui_scale", 1.0), SLNGWindow.MinUiScale, SLNGWindow.MaxUiScale);
            Language = (string)cfg.GetValue(Section, "language", "en-US");
        }
        SLNGWindow.SetGlobalUiScale(Scale);
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
