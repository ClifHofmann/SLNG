using System;
using System.Collections.Generic;
using Godot;
using SLNG.Core;

namespace SLNG.App;

/// <summary>The Windlight presets shipped with the client (FEAT-ENV-02).
///
/// These are the legacy <c>.xml</c> settings files from the Second Life viewer's
/// <c>app_settings/windlight</c> — the same library Firestorm offers under "Sky presets" /
/// "Water presets", because Firestorm ships the very same files (plus a few of its own). They are
/// plain LLSD documents, so nothing here needs the grid: a preset can be applied at any time,
/// including before login.
///
/// The index is the DIRECTORY LISTING, not a manifest: dropping another <c>.xml</c> into
/// <c>res://assets/windlight/skies</c> makes it appear in the picker with no code change. Files
/// are parsed on demand rather than up front — a preset is ~4 KB of XML and the user picks one at
/// a time, so parsing all forty at startup would buy nothing.</summary>
public sealed class WindlightPresetLibrary
{
    private const string SkyDirectory = "res://assets/windlight/skies";
    private const string WaterDirectory = "res://assets/windlight/water";

    private readonly List<string> _skyNames = new();
    private readonly List<string> _waterNames = new();

    /// <summary>Sky preset names, alphabetically. The name is the file name without extension —
    /// the same thing the viewer shows.</summary>
    public IReadOnlyList<string> SkyNames => _skyNames;

    public IReadOnlyList<string> WaterNames => _waterNames;

    public void Load()
    {
        _skyNames.Clear();
        _waterNames.Clear();
        _skyNames.AddRange(ListPresets(SkyDirectory));
        _waterNames.AddRange(ListPresets(WaterDirectory));
    }

    public SkySettings? LoadSky(string name)
    {
        string? text = ReadPreset(SkyDirectory, name);
        return text != null ? SLNG.Net.WindlightPresetParser.ParseSky(text) : null;
    }

    public WaterSettings? LoadWater(string name)
    {
        string? text = ReadPreset(WaterDirectory, name);
        return text != null ? SLNG.Net.WindlightPresetParser.ParseWater(text) : null;
    }

    private static List<string> ListPresets(string directory)
    {
        var names = new List<string>();

        using var dir = DirAccess.Open(directory);
        if (dir == null)
        {
            GD.PushWarning($"[WL] preset directory missing: {directory}");
            return names;
        }

        foreach (var file in dir.GetFiles())
        {
            // An exported build can hand back the ".remap" name of a file the packer rewrote;
            // stripping it back to the real name keeps the same code working in both.
            string name = file.EndsWith(".remap", StringComparison.OrdinalIgnoreCase)
                ? file[..^".remap".Length]
                : file;

            if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
            name = name[..^".xml".Length];
            if (!names.Contains(name)) names.Add(name);
        }

        names.Sort(StringComparer.CurrentCultureIgnoreCase);
        return names;
    }

    private static string? ReadPreset(string directory, string name)
    {
        string path = $"{directory}/{name}.xml";
        if (!FileAccess.FileExists(path))
        {
            GD.PushWarning($"[WL] preset not found: {path}");
            return null;
        }

        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        return file?.GetAsText();
    }
}
