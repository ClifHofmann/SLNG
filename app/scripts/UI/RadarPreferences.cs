using System;
using Godot;
using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>
/// Reads and writes the radar's settings (FEAT-UI-39): which columns show, how the table is sorted
/// and where the map/table divider sits. They live in the <c>radar</c> section of
/// <c>user://preferences.cfg</c>, the file the window geometry already uses, so the file is loaded
/// before it is written and every other section survives. Failure to read or write is not an error
/// worth interrupting the player for: the radar just opens on its defaults.
/// </summary>
internal static class RadarPreferences
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string Section = "radar";

    /// <summary>Loads the saved settings. Returns false when none were ever saved (the section is
    /// absent), which is how the window knows it is meeting the table layout for the first time.</summary>
    public static bool Load(out RadarColumnSettings settings, out int splitOffset)
    {
        settings = RadarColumnSettings.CreateDefault();
        splitOffset = 0;
        try
        {
            var cfg = new ConfigFile();
            if (cfg.Load(ConfigPath) != Error.Ok || !cfg.HasSection(Section)) return false;

            bool? ascending = cfg.HasSectionKey(Section, "asc") ? cfg.GetValue(Section, "asc").AsBool() : null;
            settings = RadarColumnSettings.Parse(
                cfg.GetValue(Section, "shown", "").AsString(),
                cfg.GetValue(Section, "hidden", "").AsString(),
                cfg.GetValue(Section, "sort", "").AsString(),
                ascending);
            splitOffset = cfg.GetValue(Section, "split", 0).AsInt32();
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Radar] could not read preferences: {ex.Message}");
            return false;
        }
    }

    public static void Save(RadarColumnSettings settings, int splitOffset)
    {
        try
        {
            var cfg = new ConfigFile();
            cfg.Load(ConfigPath); // keep the sections other features own (window geometry, UI settings)
            cfg.SetValue(Section, "shown", settings.SerializeShown());
            cfg.SetValue(Section, "hidden", settings.SerializeHidden());
            cfg.SetValue(Section, "sort", settings.SortId ?? "");
            cfg.SetValue(Section, "asc", settings.SortAscending);
            cfg.SetValue(Section, "split", splitOffset);
            cfg.Save(ConfigPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Radar] could not save preferences: {ex.Message}");
        }
    }
}
