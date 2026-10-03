using SLNG.Core.Input;
using Xunit;

namespace SLNG.Core.Tests;

public class KeyBindingTableTests
{
    private static KeyChord Ch(string text) => KeyChord.Parse(text);

    [Fact]
    public void The_shipped_catalog_is_sound()
    {
        // No duplicate id, and no default chord on two actions whose contexts overlap.
        Assert.Empty(KeyBindingTable.Validate(KeyActions.All));
    }

    [Fact]
    public void Every_default_chord_is_a_real_chord_and_every_action_has_one_or_more()
    {
        foreach (var action in KeyActions.All)
        {
            Assert.NotEmpty(action.Defaults);
            Assert.Equal(action.Defaults.Count, action.Defaults.Distinct().Count());
        }
    }

    [Fact]
    public void Action_ids_are_lowercase_dotted_and_there_are_no_two_with_the_same_label_key()
    {
        foreach (var action in KeyActions.All)
            Assert.Matches("^[a-z]+(\\.[a-z_]+)+$", action.Id);
        // The language files key an action by its id with '.' -> '_'; two ids must not collapse together.
        Assert.Equal(KeyActions.All.Count, KeyActions.All.Select(a => a.Id.Replace('.', '_')).Distinct().Count());
    }

    [Fact]
    public void Movement_keys_share_a_letter_with_the_alt_camera_keys_without_conflict()
    {
        var table = new KeyBindingTable();
        // W walks forward, Alt+W zooms the camera: different modifiers, so no conflict either way.
        Assert.Contains(Ch("W"), table.ChordsOf(KeyActionIds.MoveForward));
        Assert.Contains(Ch("Alt+W"), table.ChordsOf(KeyActionIds.CameraZoomIn));
        Assert.Empty(table.Conflicts(KeyActionIds.CameraZoomIn, Ch("Alt+W")));
        Assert.Single(table.ActionsFor(Ch("W")));
        Assert.Single(table.ActionsFor(Ch("Alt+W")));
    }

    [Fact]
    public void Contexts_that_cannot_be_live_together_do_not_conflict()
    {
        var catalog = new[]
        {
            new KeyAction("a.world", KeyCategory.Chat, KeyContext.World, KeyActionKind.Press, new[] { Ch("Enter") }),
            new KeyAction("a.chat", KeyCategory.Chat, KeyContext.ChatInput, KeyActionKind.Press, new[] { Ch("Enter") }),
            new KeyAction("a.always", KeyCategory.Chat, KeyContext.Always, KeyActionKind.Press, new[] { Ch("Ctrl+Enter") }),
            new KeyAction("a.chat2", KeyCategory.Chat, KeyContext.ChatInput, KeyActionKind.Press, new[] { Ch("Ctrl+Enter") }),
        };
        var problems = KeyBindingTable.Validate(catalog);
        // Enter on the world and on the chat bar: fine. Ctrl+Enter on Always and on the chat bar: not.
        Assert.Single(problems);
        Assert.Contains("Ctrl+Enter", problems[0]);
    }

    [Fact]
    public void A_duplicate_default_in_overlapping_contexts_is_reported()
    {
        var catalog = new[]
        {
            new KeyAction("x.one", KeyCategory.Windows, KeyContext.Always, KeyActionKind.Press, new[] { Ch("Ctrl+I") }),
            new KeyAction("x.two", KeyCategory.Windows, KeyContext.World, KeyActionKind.Press, new[] { Ch("Ctrl+I") }),
        };
        Assert.Single(KeyBindingTable.Validate(catalog));
    }

    [Fact]
    public void An_override_applies_and_reset_clears_it()
    {
        var table = new KeyBindingTable();
        int changes = 0;
        table.Changed += () => changes++;
        int v0 = table.Version;

        table.SetChords(KeyActionIds.WindowInventory, new[] { Ch("Ctrl+B") });
        Assert.Equal(new[] { Ch("Ctrl+B") }, table.ChordsOf(KeyActionIds.WindowInventory));
        Assert.False(table.IsDefault(KeyActionIds.WindowInventory));
        Assert.Empty(table.ActionsFor(Ch("Ctrl+I")));
        Assert.Single(table.ActionsFor(Ch("Ctrl+B")));
        Assert.True(table.Version > v0);
        Assert.Equal(1, changes);

        table.Reset(KeyActionIds.WindowInventory);
        Assert.True(table.IsDefault(KeyActionIds.WindowInventory));
        Assert.Single(table.ActionsFor(Ch("Ctrl+I")));
        Assert.Empty(table.ToSaved());
    }

    [Fact]
    public void Setting_the_same_chords_again_is_not_a_change()
    {
        var table = new KeyBindingTable();
        int changes = 0;
        table.Changed += () => changes++;
        table.SetChords(KeyActionIds.WindowInventory, table.ChordsOf(KeyActionIds.WindowInventory).ToList());
        table.Reset(KeyActionIds.WindowInventory);
        table.ResetAll();
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Only_the_differences_from_the_defaults_are_saved()
    {
        var table = new KeyBindingTable();
        Assert.Empty(table.ToSaved());

        table.SetChords(KeyActionIds.WindowStats, new[] { Ch("Ctrl+Shift+2") });
        table.SetChords(KeyActionIds.DevTPose, Array.Empty<KeyChord>());                       // deliberately unbound
        table.SetChords(KeyActionIds.MoveForward, new[] { Ch("Up"), Ch("W") });                // same chords, other order
        var saved = table.ToSaved();

        Assert.Equal(2, saved.Count);
        Assert.Equal("Ctrl+Shift+2", saved[KeyActionIds.WindowStats]);
        Assert.Equal("", saved[KeyActionIds.DevTPose]);
        Assert.False(saved.ContainsKey(KeyActionIds.MoveForward));
    }

    [Fact]
    public void A_saved_file_round_trips()
    {
        var a = new KeyBindingTable();
        a.SetChords(KeyActionIds.WindowStats, new[] { Ch("Ctrl+Shift+2"), Ch("F10") });
        a.SetChords(KeyActionIds.DevTPose, Array.Empty<KeyChord>());
        a.Assign(KeyActionIds.MoveForward, Ch("I"), replaceConflicts: false);

        var b = new KeyBindingTable();
        var result = b.ApplySaved(a.ToSaved());

        Assert.Equal(3, result.Applied);
        foreach (var action in a.Actions)
            Assert.Equal(a.ChordsOf(action.Id), b.ChordsOf(action.Id));
        Assert.Equal(a.ToSaved(), b.ToSaved());
    }

    [Fact]
    public void Unknown_ids_and_unknown_keys_in_a_saved_file_are_ignored_not_fatal()
    {
        var table = new KeyBindingTable();
        var saved = new Dictionary<string, string>
        {
            ["window.from_the_future"] = "Ctrl+Shift+F13",       // an id a later version added
            [KeyActionIds.WindowStats] = "Ctrl+Shift+2;Hyper+ZZ", // one good chord, one this build does not know
            [KeyActionIds.WindowInventory] = "NoSuchKey",         // nothing usable: keep the default
            [KeyActionIds.DevTPose] = "",                         // an explicit unbind
        };

        var result = table.ApplySaved(saved);

        Assert.Equal(1, result.UnknownActions);
        Assert.Equal(2, result.InvalidEntries);
        Assert.Equal(2, result.Applied);
        Assert.Equal(new[] { Ch("Ctrl+Shift+2") }, table.ChordsOf(KeyActionIds.WindowStats));
        Assert.True(table.IsDefault(KeyActionIds.WindowInventory));
        Assert.Empty(table.ChordsOf(KeyActionIds.DevTPose));
    }

    [Fact]
    public void Applying_a_file_twice_does_not_accumulate_and_an_empty_file_restores_the_defaults()
    {
        var table = new KeyBindingTable();
        var saved = new Dictionary<string, string> { [KeyActionIds.WindowStats] = "Ctrl+Shift+2" };
        table.ApplySaved(saved);
        table.ApplySaved(saved);
        Assert.Single(table.ChordsOf(KeyActionIds.WindowStats));

        table.ApplySaved(new Dictionary<string, string>());
        Assert.True(table.IsDefault(KeyActionIds.WindowStats));
    }

    [Fact]
    public void A_default_equal_to_the_saved_entry_is_not_kept_as_an_override()
    {
        var table = new KeyBindingTable();
        var result = table.ApplySaved(new Dictionary<string, string> { [KeyActionIds.WindowInventory] = "Ctrl+I" });
        Assert.Equal(0, result.Applied);
        Assert.Empty(table.ToSaved());
    }

    [Fact]
    public void Conflicts_name_the_other_action_and_replace_takes_the_chord_over()
    {
        var table = new KeyBindingTable();

        var conflicts = table.Conflicts(KeyActionIds.WindowStats, Ch("Ctrl+I"));
        Assert.Single(conflicts);
        Assert.Equal(KeyActionIds.WindowInventory, conflicts[0].Id);

        // Refused (the caller never assigns): nothing changed.
        Assert.True(table.IsDefault(KeyActionIds.WindowInventory));

        var taken = table.Assign(KeyActionIds.WindowStats, Ch("Ctrl+I"), replaceConflicts: true);
        Assert.Single(taken);
        Assert.Empty(table.ChordsOf(KeyActionIds.WindowInventory));
        Assert.Contains(Ch("Ctrl+I"), table.ChordsOf(KeyActionIds.WindowStats));
        Assert.Empty(table.Conflicts(KeyActionIds.WindowStats, Ch("Ctrl+I")));
    }

    [Fact]
    public void Assign_can_replace_one_chord_in_place_or_add_another()
    {
        var table = new KeyBindingTable();

        // Change the second of move.forward's chords (Up) to I: order kept, the first chord untouched.
        table.Assign(KeyActionIds.MoveForward, Ch("I"), replaceConflicts: false, replaceIndex: 1);
        Assert.Equal(new[] { Ch("W"), Ch("I") }, table.ChordsOf(KeyActionIds.MoveForward));

        // Add a third.
        table.Assign(KeyActionIds.MoveForward, Ch("K"), replaceConflicts: false);
        Assert.Equal(3, table.ChordsOf(KeyActionIds.MoveForward).Count);

        // Replacing with a chord the action already has does not duplicate it.
        table.Assign(KeyActionIds.MoveForward, Ch("W"), replaceConflicts: false, replaceIndex: 1);
        Assert.Equal(new[] { Ch("W"), Ch("K") }, table.ChordsOf(KeyActionIds.MoveForward));
    }

    [Fact]
    public void Removing_the_last_chord_leaves_the_action_unbound_and_that_is_saved_as_empty()
    {
        var table = new KeyBindingTable();
        table.Remove(KeyActionIds.WindowInventory, Ch("Ctrl+I"));
        Assert.Empty(table.ChordsOf(KeyActionIds.WindowInventory));
        Assert.Equal("", table.ToSaved()[KeyActionIds.WindowInventory]);
        Assert.Empty(table.ActionsFor(Ch("Ctrl+I")));
    }

    [Fact]
    public void A_chord_does_not_conflict_with_the_action_it_is_already_on()
    {
        var table = new KeyBindingTable();
        Assert.Empty(table.Conflicts(KeyActionIds.WindowInventory, Ch("Ctrl+I")));
    }

    [Fact]
    public void Movement_keys_ignore_an_extra_Shift_so_the_run_modifier_still_walks()
    {
        var table = new KeyBindingTable();
        var turnLeft = table.Find(KeyActionIds.MoveTurnLeft)!;
        Assert.True(turnLeft.Claims(Ch("A"), Ch("A")));
        Assert.True(turnLeft.Claims(Ch("A"), Ch("Shift+A")));
        Assert.False(turnLeft.Claims(Ch("A"), Ch("Ctrl+A")));     // a movement key with Ctrl is a shortcut
        Assert.False(turnLeft.Claims(Ch("A"), Ch("Alt+A")));      // and with Alt it is the camera

        // ... and binding Shift+A to something else collides with it, because it would also turn.
        Assert.Contains(turnLeft, table.Conflicts(KeyActionIds.WindowStats, Ch("Shift+A")));

        // The camera actions are exact.
        var orbit = table.Find(KeyActionIds.CameraOrbitCw)!;
        Assert.False(orbit.Claims(Ch("Alt+A"), Ch("Alt+Shift+A")));
    }

    [Fact]
    public void ActionsFor_does_not_allocate_for_a_key_nothing_is_bound_to()
    {
        var table = new KeyBindingTable();
        var a = table.ActionsFor(Ch("Ctrl+Alt+Shift+F12"));
        var b = table.ActionsFor(Ch("Ctrl+Alt+Shift+F11"));
        Assert.Same(a, b); // the shared empty array
    }

    [Fact]
    public void Held_actions_are_only_ever_in_the_world_context()
    {
        foreach (var held in KeyActions.All.Where(a => a.Kind == KeyActionKind.Held))
            Assert.True((held.Context & KeyContext.World) != 0, held.Id);
    }

    [Fact]
    public void The_listed_SL_gaps_parse_and_are_not_also_bound_by_default_to_the_same_meaning()
    {
        foreach (var gap in SlShortcutGaps.All)
            Assert.True(KeyChord.TryParse(gap.Chord, out _), gap.Chord);

        // A gap's chord is free for SLNG's own use, but a gap must never be an action in the catalog:
        // that would be the faked binding this list exists to prevent.
        var names = new HashSet<string>(KeyActions.All.Select(a => a.SlRef ?? ""));
        foreach (var gap in SlShortcutGaps.All)
            Assert.DoesNotContain("menu:" + gap.SlName, names);
    }
}
