using System.Xml.Linq;
using SLNG.Core.Input;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// The defaults are "the reference viewer's own", so they are checked against the viewer's own files:
/// <c>key_bindings.xml</c> (movement and camera, per mode) and <c>menu_viewer.xml</c> (menu
/// shortcuts). Every action that names an <see cref="KeyAction.SlRef"/> must contain the chords the
/// file gives for that command or menu item - SLNG may add chords, never drop or change an SL one.
///
/// <para>The viewer is vendored under <c>scratch/slviewer</c>, which is git-ignored (a shallow clone
/// the maintainer keeps locally), so a fresh clone or CI has no such files. In that case the file
/// checks cannot run and return early, and the embedded copy of the facts below is what keeps the
/// catalog honest: it is the same data, written down once, from those files.</para>
/// </summary>
public class KeyBindingsViewerParityTests
{
    private static string? ViewerDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "scratch", "slviewer", "indra", "newview");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static KeyMods ParseMask(string mask) => mask switch
    {
        "NONE" => KeyMods.None,
        "SHIFT" => KeyMods.Shift,
        "ALT" => KeyMods.Alt,
        "CTL" or "CONTROL" => KeyMods.Ctrl,
        "CTL_ALT" => KeyMods.Ctrl | KeyMods.Alt,
        "CTL_SHIFT" => KeyMods.Ctrl | KeyMods.Shift,
        "ALT_SHIFT" => KeyMods.Alt | KeyMods.Shift,
        "CTL_ALT_SHIFT" => KeyMods.Ctrl | KeyMods.Alt | KeyMods.Shift,
        _ => throw new FormatException("unknown mask " + mask),
    };

    private static KeyChord ParseMenuShortcut(string shortcut)
    {
        var parts = shortcut.Split('|');
        var mods = KeyMods.None;
        foreach (var p in parts[..^1])
            mods |= p.ToLowerInvariant() switch
            {
                "control" => KeyMods.Ctrl,
                "alt" => KeyMods.Alt,
                "shift" => KeyMods.Shift,
                _ => throw new FormatException("unknown modifier " + p),
            };
        return new KeyChord(parts[^1], mods);
    }

    [Fact]
    public void Defaults_match_key_bindings_xml_third_person()
    {
        var dir = ViewerDir();
        if (dir == null) return; // no vendored viewer on this machine; see the class comment

        var doc = XDocument.Load(Path.Combine(dir, "app_settings", "key_bindings.xml"));
        int checkedCount = 0;
        foreach (var action in KeyActions.All.Where(a => a.SlRef?.StartsWith("key:", StringComparison.Ordinal) == true))
        {
            var parts = action.SlRef!.Split(':');
            string mode = parts[1], command = parts[2];
            var bindings = doc.Root!.Element(mode)!.Elements("binding")
                .Where(b => (string?)b.Attribute("command") == command && (string?)b.Attribute("mouse") == null)
                .Select(b => new KeyChord((string)b.Attribute("key")!, ParseMask((string)b.Attribute("mask")!)))
                .ToList();

            Assert.True(bindings.Count > 0, $"{action.Id}: no '{command}' binding in <{mode}> of key_bindings.xml");
            foreach (var chord in bindings)
            {
                Assert.True(action.Defaults.Contains(chord), $"{action.Id}: key_bindings.xml binds {chord} to {command}, but it is not a default");
                checkedCount++;
            }
        }
        Assert.True(checkedCount > 20);
    }

    [Fact]
    public void Defaults_match_menu_viewer_xml_shortcuts()
    {
        var dir = ViewerDir();
        if (dir == null) return;

        // menu_viewer.xml plus the Edit menu it embeds (Cut/Copy/Paste live in menu_edit.xml).
        var items = new List<(string Name, string Shortcut)>();
        foreach (var file in new[] { "menu_viewer.xml", "menu_edit.xml" })
        {
            var doc = XDocument.Load(Path.Combine(dir, "skins", "default", "xui", "en", file));
            items.AddRange(doc.Descendants()
                .Where(e => e.Attribute("shortcut") != null && e.Attribute("name") != null)
                .Select(e => ((string)e.Attribute("name")!, (string)e.Attribute("shortcut")!)));
        }

        int checkedCount = 0;
        foreach (var action in KeyActions.All.Where(a => a.SlRef?.StartsWith("menu:", StringComparison.Ordinal) == true))
        {
            var name = action.SlRef!["menu:".Length..];
            // A few names are used by more than one item (a Develop-menu twin); the default only has to
            // be one of the chords the file gives that name.
            var candidates = items.Where(i => i.Name == name).Select(i => ParseMenuShortcut(i.Shortcut)).ToList();
            Assert.True(candidates.Count > 0, $"{action.Id}: no menu item named '{name}' with a shortcut");
            Assert.True(candidates.Any(c => action.Defaults.Contains(c)),
                $"{action.Id}: menu item '{name}' has {string.Join(" / ", candidates)}, defaults are {string.Join(" / ", action.Defaults)}");
            checkedCount++;
        }
        Assert.True(checkedCount > 25);
    }

    [Fact]
    public void Every_listed_gap_is_really_a_viewer_shortcut()
    {
        var dir = ViewerDir();
        if (dir == null) return;

        var shortcuts = new HashSet<KeyChord>();
        foreach (var file in new[] { "menu_viewer.xml", "menu_edit.xml" })
        {
            var doc = XDocument.Load(Path.Combine(dir, "skins", "default", "xui", "en", file));
            foreach (var e in doc.Descendants().Where(e => e.Attribute("shortcut") != null))
                shortcuts.Add(ParseMenuShortcut((string)e.Attribute("shortcut")!));
        }
        var keyDoc = XDocument.Load(Path.Combine(dir, "app_settings", "key_bindings.xml"));
        foreach (var b in keyDoc.Descendants("binding").Where(b => (string?)b.Attribute("mouse") == null && ((string)b.Attribute("key")!).Length > 0))
        {
            try { shortcuts.Add(new KeyChord((string)b.Attribute("key")!, ParseMask((string)b.Attribute("mask")!))); }
            catch (ArgumentException) { /* a key SLNG cannot bind, e.g. DIVIDE */ }
        }

        // Chat-bar chords are handled in the viewer's C++ (llfloaterimnearbychat.cpp, llchatentry.cpp),
        // not in these files; Space is "stop_moving" in key_bindings.xml and is covered above.
        var handledInCode = new HashSet<string> { "Shift+Enter", "Ctrl+Enter", "Ctrl+Up", "Ctrl+Down" };

        foreach (var gap in SlShortcutGaps.All)
        {
            if (handledInCode.Contains(gap.Chord)) continue;
            Assert.True(shortcuts.Contains(KeyChord.Parse(gap.Chord)), $"gap '{gap.SlName}' ({gap.Chord}) is not a shortcut in the vendored viewer");
        }
    }

    /// <summary>The same facts, embedded, so they are checked on a machine without the viewer source.
    /// Each row was read from the files named in the class comment.</summary>
    [Theory]
    [InlineData(KeyActionIds.WindowInventory, "Ctrl+I")]
    [InlineData(KeyActionIds.WindowOutfits, "Ctrl+O")]
    [InlineData(KeyActionIds.WindowStats, "Ctrl+Shift+1")]
    [InlineData(KeyActionIds.WindowConversations, "Ctrl+T")]
    [InlineData(KeyActionIds.WindowNearbyChat, "Ctrl+H")]
    [InlineData(KeyActionIds.WindowWorldMap, "Ctrl+M")]
    [InlineData(KeyActionIds.WindowMiniMap, "Ctrl+Shift+M")]
    [InlineData(KeyActionIds.WindowLandmarks, "Ctrl+L")]
    [InlineData(KeyActionIds.WindowSnapshot, "Ctrl+Shift+S")]
    [InlineData(KeyActionIds.AvatarAlwaysRun, "Ctrl+R")]
    [InlineData(KeyActionIds.AvatarRebake, "Ctrl+Alt+R")]
    [InlineData(KeyActionIds.MoveToggleFly, "Home")]
    [InlineData(KeyActionIds.MoveToggleFly, "F")]
    [InlineData(KeyActionIds.MoveForward, "W")]
    [InlineData(KeyActionIds.MoveForward, "Up")]
    [InlineData(KeyActionIds.MoveUp, "PageUp")]
    [InlineData(KeyActionIds.MoveDown, "C")]
    [InlineData(KeyActionIds.CameraZoomIn, "Alt+W")]
    [InlineData(KeyActionIds.CameraZoomOut, "Alt+Down")]
    [InlineData(KeyActionIds.CameraOrbitCw, "Alt+A")]
    [InlineData(KeyActionIds.CameraOrbitCcw, "Alt+Right")]
    [InlineData(KeyActionIds.CameraOrbitOver, "Ctrl+Alt+W")]
    [InlineData(KeyActionIds.CameraOrbitUnder, "Ctrl+Alt+Down")]
    [InlineData(KeyActionIds.CameraPanUp, "Ctrl+Alt+Shift+W")]
    [InlineData(KeyActionIds.CameraReset, "Escape")]
    [InlineData(KeyActionIds.EditCopy, "Ctrl+C")]
    [InlineData(KeyActionIds.WindowClose, "Ctrl+W")]
    [InlineData(KeyActionIds.WindowCloseAll, "Ctrl+Shift+W")]
    [InlineData(KeyActionIds.ViewZoomIn, "Ctrl+0")]
    [InlineData(KeyActionIds.ViewZoomOut, "Ctrl+8")]
    [InlineData(KeyActionIds.ViewZoomDefault, "Ctrl+9")]
    public void Embedded_viewer_defaults_are_present(string actionId, string chord)
    {
        var table = new KeyBindingTable();
        Assert.Contains(KeyChord.Parse(chord), table.ChordsOf(actionId));
    }

    [Fact]
    public void The_wiki_and_the_source_disagree_on_these_and_the_source_wins()
    {
        // The wiki lists Ctrl+Alt+Left/Right/Up/Down as orbit keys. key_bindings.xml binds Ctrl+Alt only
        // to W/S/Up/Down (spin over / under); Ctrl+Alt+Left/Right are unbound. The table follows the source.
        var table = new KeyBindingTable();
        Assert.Empty(table.ActionsFor(KeyChord.Parse("Ctrl+Alt+Left")));
        Assert.Empty(table.ActionsFor(KeyChord.Parse("Ctrl+Alt+Right")));
        Assert.Equal(KeyActionIds.CameraOrbitOver, table.ActionsFor(KeyChord.Parse("Ctrl+Alt+Up")).Single().Id);
    }
}
