using System.Collections.Generic;
using Godot;
using SLNG.App.UI;
using SLNG.Core.Input;

namespace SLNG.App;

/// <summary>
/// The app's one live <see cref="KeyBindingTable"/> and how it is stored. The dispatcher, the polled
/// movement / camera keys, the menu hints and the Keyboard page all read this table; nothing else in
/// the client decides what a key does.
///
/// <para><b>Persistence.</b> <c>preferences.cfg</c>, section <c>key_bindings</c>, one key per
/// customised action: <c>window.inventory="Ctrl+B;Ctrl+Shift+B"</c> (chords joined by <c>;</c>, an
/// empty string = deliberately unbound). Only DIFFERENCES from the defaults are written, so an
/// untouched install has no section at all. <see cref="Load"/> only reads - the file is written by
/// <see cref="Save"/>, which the Keyboard page calls when the person changes something, and by
/// nothing on the boot path (a selftest boots on the maintainer's real <c>user://</c>).</para>
/// </summary>
public static class KeyBindings
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string Section = "key_bindings";

    /// <summary>The live table. Main thread only.</summary>
    public static KeyBindingTable Table { get; } = new();

    /// <summary>Reads the saved overrides into <see cref="Table"/>. Reads only; a missing file or
    /// section leaves the defaults.</summary>
    public static void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return;
        var result = ReadFrom(cfg, Table);
        if (result.UnknownActions > 0 || result.InvalidEntries > 0)
            GD.Print($"[KeyBindings] ignored {result.UnknownActions} unknown action(s) and {result.InvalidEntries} unusable chord(s) in preferences.cfg");
    }

    /// <summary>Writes the table's differences from the defaults to <c>preferences.cfg</c>, keeping
    /// every other section. Call after the person changes something - never at startup.</summary>
    public static void Save()
    {
        var cfg = new ConfigFile();
        cfg.Load(ConfigPath); // preserve the sections other settings classes own
        WriteTo(cfg, Table);
        cfg.Save(ConfigPath);
    }

    /// <summary>Applies the <c>key_bindings</c> section of <paramref name="cfg"/> to
    /// <paramref name="table"/>. Pure with respect to disk, so the selftest can round-trip in memory.</summary>
    public static KeyBindingLoadResult ReadFrom(ConfigFile cfg, KeyBindingTable table)
    {
        var saved = new Dictionary<string, string>();
        if (cfg.HasSection(Section))
        {
            foreach (var key in cfg.GetSectionKeys(Section))
            {
                // A value of the wrong type (a hand-edited file) is just an unusable entry.
                if (cfg.GetValue(Section, key).VariantType == Variant.Type.String)
                    saved[key] = (string)cfg.GetValue(Section, key);
            }
        }
        return table.ApplySaved(saved);
    }

    /// <summary>Replaces the <c>key_bindings</c> section of <paramref name="cfg"/> with the table's
    /// differences from the defaults (no section at all when there are none).</summary>
    public static void WriteTo(ConfigFile cfg, KeyBindingTable table)
    {
        if (cfg.HasSection(Section)) cfg.EraseSection(Section);
        foreach (var (id, chords) in table.ToSaved())
            cfg.SetValue(Section, id, chords);
    }

    /// <summary>The first chord of an action as the person would read it ("Ctrl+Shift+1" / "Strg+Umschalt+1"),
    /// or null when the action is unbound. For menu hints.</summary>
    public static string? HintFor(string actionId)
    {
        var chords = Table.ChordsOf(actionId);
        return chords.Count == 0 ? null : KeyChordText.Format(chords[0]);
    }
}

/// <summary>Chord text in the current language: modifier and key names come from the language
/// files (<c>ui.keys.mod.*</c>, <c>ui.keys.key.*</c>); a key with no entry shows its canonical name.
/// Display only - the saved file always holds the canonical English form.</summary>
public static class KeyChordText
{
    public static string Format(KeyChord chord)
    {
        var text = string.Empty;
        if (chord.Ctrl) text += L10n.Tr("ui.keys.mod.ctrl") + "+";
        if (chord.Alt) text += L10n.Tr("ui.keys.mod.alt") + "+";
        if (chord.Shift) text += L10n.Tr("ui.keys.mod.shift") + "+";
        return text + KeyLabel(chord.Key);
    }

    private static string KeyLabel(string key)
    {
        var translated = L10n.Tr("ui.keys.key." + key.ToLowerInvariant());
        return translated.StartsWith('[') ? key : translated;
    }

    /// <summary>The label of an action in the current language.</summary>
    public static string ActionLabel(string actionId) => L10n.Tr(ActionLabelKey(actionId));

    public static string ActionLabelKey(string actionId) => "ui.keys.action." + actionId.Replace('.', '_');

    public static string CategoryLabelKey(KeyCategory category) => "ui.keys.category." + category.ToString().ToLowerInvariant();
}
