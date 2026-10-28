using Godot;

namespace SLNG.App;

/// <summary>
/// FEAT-UI-76: whether the debug log is on, kept in <c>preferences.cfg</c> section <c>debug_log</c> (the same
/// <c>ConfigFile</c> pattern as <see cref="ChatLogSettings"/>). Off by default; the switch is remembered so a bug that only
/// shows up after a restart is still logged from the first line. Nothing is written on the boot path, only when the person
/// changes it.
/// </summary>
public static class DebugLogSettings
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string Section = "debug_log";

    public static bool Enabled
    {
        get
        {
            var cfg = new ConfigFile();
            return cfg.Load(ConfigPath) == Error.Ok && (bool)cfg.GetValue(Section, "enabled", false);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        var cfg = new ConfigFile();
        cfg.Load(ConfigPath); // preserve sections owned by other settings classes
        cfg.SetValue(Section, "enabled", enabled);
        cfg.Save(ConfigPath);
        if (enabled) DebugLog.Start();
        else DebugLog.Stop();
    }
}
