using Godot;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-UI-17 (partial): the user-chosen snapshot output folder, persisted to
/// user://preferences.cfg under a "snapshot" section -- same ConfigFile file and pattern as
/// DofSettings / UiSettings, so all of them coexist without knowing about each other.
///
/// An empty <see cref="OutputDir"/> means "use the built-in default"
/// (SnapshotWindow.DefaultSnapshotDir), not "save nowhere" -- SnapshotWindow resolves that
/// fallback itself so this class does not need to know the default path.
/// </summary>
public sealed class SnapshotSettings
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string Section = "snapshot";

    /// <summary>Absolute OS folder path chosen via the folder picker, or "" for the default.</summary>
    public string OutputDir { get; private set; } = "";

    public void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return;
        OutputDir = (string)cfg.GetValue(Section, "output_dir", "");
    }

    public void SetOutputDir(string dir)
    {
        OutputDir = dir;

        var cfg = new ConfigFile();
        cfg.Load(ConfigPath); // preserve sections owned by other features (DofSettings, UiSettings, ...)
        cfg.SetValue(Section, "output_dir", OutputDir);
        cfg.Save(ConfigPath);
    }
}
