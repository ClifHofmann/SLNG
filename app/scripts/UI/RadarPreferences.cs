using System;
using Godot;
using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>
/// Reads and writes the radar's settings (FEAT-UI-39): which columns show, how the table is sorted,
/// where the map/table divider sits, and how the map looks (chat rings, objects, orientation,
/// auto-centre, zoom). They live in the <c>radar</c> section of <c>user://preferences.cfg</c>, the file the window
/// geometry already uses, so the file is loaded before it is written and every other section
/// survives. Failure to read or write is not an error worth interrupting the player for: the radar
/// just opens on its defaults.
/// </summary>
internal static class RadarPreferences
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string Section = "radar";

    /// <summary>Loads the saved settings. Returns false when none were ever saved (the section is
    /// absent), which is how the window knows it is meeting the table layout for the first time.</summary>
    public static bool Load(out RadarColumnSettings settings, out RadarViewSettings view, out int splitOffset)
    {
        settings = RadarColumnSettings.CreateDefault();
        view = new RadarViewSettings();
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

            // The map's keys are tolerant one by one: a file from before they existed, or one edited
            // by hand, keeps the defaults for whatever is missing or not the right kind of value.
            view.ChatRings = ReadBool(cfg, "rings", view.ChatRings);
            view.WhisperRing = ReadBool(cfg, "ring_whisper", view.WhisperRing);
            view.SayRing = ReadBool(cfg, "ring_say", view.SayRing);
            view.ShoutRing = ReadBool(cfg, "ring_shout", view.ShoutRing);
            view.ShowObjects = ReadBool(cfg, "objects", view.ShowObjects);
            view.ObjectMinSizeMetres = ReadFloat(cfg, "object_min", view.ObjectMinSizeMetres);
            view.CameraUp = ReadBool(cfg, "camera_up", view.CameraUp);
            view.AutoCenter = ReadBool(cfg, "auto_center", view.AutoCenter);
            view.VisibleRangeMetres = ReadFloat(cfg, "zoom", view.VisibleRangeMetres);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Radar] could not read preferences: {ex.Message}");
            return false;
        }
    }

    public static void Save(RadarColumnSettings settings, RadarViewSettings view, int splitOffset)
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
            cfg.SetValue(Section, "rings", view.ChatRings);
            cfg.SetValue(Section, "ring_whisper", view.WhisperRing);
            cfg.SetValue(Section, "ring_say", view.SayRing);
            cfg.SetValue(Section, "ring_shout", view.ShoutRing);
            cfg.SetValue(Section, "objects", view.ShowObjects);
            cfg.SetValue(Section, "object_min", view.ObjectMinSizeMetres);
            cfg.SetValue(Section, "camera_up", view.CameraUp);
            cfg.SetValue(Section, "auto_center", view.AutoCenter);
            cfg.SetValue(Section, "zoom", view.VisibleRangeMetres);
            cfg.Save(ConfigPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Radar] could not save preferences: {ex.Message}");
        }
    }

    private static bool ReadBool(ConfigFile cfg, string key, bool fallback)
    {
        if (!cfg.HasSectionKey(Section, key)) return fallback;
        var value = cfg.GetValue(Section, key);
        return value.VariantType switch
        {
            Variant.Type.Bool => value.AsBool(),
            Variant.Type.Int => value.AsInt64() != 0,
            _ => fallback,
        };
    }

    private static float ReadFloat(ConfigFile cfg, string key, float fallback)
    {
        if (!cfg.HasSectionKey(Section, key)) return fallback;
        var value = cfg.GetValue(Section, key);
        return value.VariantType is Variant.Type.Float or Variant.Type.Int ? value.AsSingle() : fallback;
    }
}
