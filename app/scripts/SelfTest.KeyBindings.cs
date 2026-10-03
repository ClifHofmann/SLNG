using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SLNG.Core.Input;

namespace SLNG.App;

/// <summary>
/// FEAT-UI-43 checks. Everything runs on IN-MEMORY state: fresh <see cref="KeyBindingTable"/>s, a
/// dispatcher that is never added to the tree, a fake keyboard for <see cref="HeldKeys"/>, and a
/// <see cref="ConfigFile"/> that is never saved. The boot path of a selftest runs on the maintainer's real
/// <c>user://</c>, so nothing here may touch <c>preferences.cfg</c> - the "user data untouched" check
/// that runs last fails the run if it does.
///
/// <para>What these cannot show: that a real key press on a real keyboard reaches the dispatcher and the
/// right handler does the right thing in a running session. They drive synthetic <c>InputEventKey</c>s
/// straight into <see cref="KeyDispatcher.Dispatch"/>, which is the same code the engine's
/// <c>_Input</c>/<c>_UnhandledInput</c> call, but not the engine's own delivery.</para>
/// </summary>
public static partial class SelfTest
{
    private static IEnumerable<Check> CheckKeyBindings(SceneTree tree)
    {
        var results = new List<Check>
        {
            CheckKeyCatalog(),
            CheckKeyLabelsInBothLocales(),
            CheckKeyDispatcher(),
            CheckKeyFocusClassification(),
            CheckKeyHeldActions(),
            CheckKeyboardPage(tree),
            CheckKeyPersistenceInMemory(),
        };
        return results;
    }

    private static InputEventKey Press(string chord) => GodotKeyMap.MakeEvent(KeyChord.Parse(chord));

    /// <summary>The catalog is sound, every Press action has a handler in the running client (bar the three the
    /// avatar controller registers once there is an avatar), and the key map covers every bindable name.</summary>
    private static Check CheckKeyCatalog()
    {
        const string Name = "key bindings: catalog";
        try
        {
            var problems = new List<string>(KeyBindingTable.Validate(KeyActions.All));

            var unmapped = KeyNames.All.Where(n => GodotKeyMap.KeyOf(n) == Key.None).ToList();
            if (unmapped.Count > 0) problems.Add("no Godot key for: " + string.Join(", ", unmapped));
            foreach (var n in KeyNames.All)
            {
                var key = GodotKeyMap.KeyOf(n);
                if (key != Key.None && GodotKeyMap.NameOf(key) != n) problems.Add($"key map does not round-trip {n}");
            }

            // Registered when AvatarController enters the scene, i.e. at login - not at the login screen.
            var lateBound = new HashSet<string> { KeyActionIds.MoveToggleFly, KeyActionIds.CameraReset, KeyActionIds.AvatarAlwaysRun };
            var dispatcher = KeyDispatcher.Instance;
            if (dispatcher == null) problems.Add("the client has no KeyDispatcher");
            else
            {
                var handled = dispatcher.HandledActions.ToHashSet();
                var unhandled = KeyActions.All
                    .Where(a => a.Kind == KeyActionKind.Press && !lateBound.Contains(a.Id) && !handled.Contains(a.Id))
                    .Select(a => a.Id).ToList();
                if (unhandled.Count > 0) problems.Add("Press actions with no handler: " + string.Join(", ", unhandled));
            }

            int press = KeyActions.All.Count(a => a.Kind == KeyActionKind.Press);
            int held = KeyActions.All.Count - press;
            return problems.Count == 0
                ? new Check(Name, true, $"{KeyActions.All.Count} actions ({press} press, {held} held), no duplicate chords in overlapping contexts, every key name maps to a Godot key, every non-avatar Press action has a handler")
                : new Check(Name, false, string.Join("; ", problems));
        }
        catch (Exception ex) { return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>Every action, category, modifier and key name has text in both language files, and the keyboard
    /// page's labels come from them.</summary>
    private static Check CheckKeyLabelsInBothLocales()
    {
        const string Name = "key bindings: labels in en-US and de-DE";
        try
        {
            var problems = new List<string>();
            foreach (var locale in new[] { "en-US", "de-DE" })
            {
                using var file = FileAccess.Open($"res://i18n/{locale}.json", FileAccess.ModeFlags.Read);
                if (file == null) { problems.Add($"cannot open {locale}"); continue; }
                var manager = new SLNG.Core.Services.LocalizationManager { FallbackLocale = locale, CurrentLocale = locale };
                manager.LoadLocaleFromJson(locale, file.GetAsText());
                var dict = manager.GetDictionaryForLocale(locale);

                var keys = new List<string> { "ui.preferences.tab_keyboard" };
                keys.AddRange(KeyActions.All.Select(a => KeyChordText.ActionLabelKey(a.Id)));
                keys.AddRange(Enum.GetValues<KeyCategory>().Select(KeyChordText.CategoryLabelKey));
                keys.AddRange(new[] { "ctrl", "alt", "shift" }.Select(m => "ui.keys.mod." + m));
                keys.AddRange(new[] { "hint", "search_placeholder", "reset_all", "reset_all_confirm", "reset_one_tooltip", "add_tooltip",
                    "remove_tooltip", "change_tooltip", "unbound", "listening", "cannot_use", "conflict", "replace", "cancel",
                    "note_typing", "unavailable_note" }.Select(k => "ui.keys." + k));

                var missing = keys.Where(k => !dict.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)).ToList();
                if (missing.Count > 0) problems.Add($"{locale} missing {missing.Count}: {string.Join(", ", missing.Take(6))}");
            }
            return problems.Count == 0
                ? new Check(Name, true, $"{KeyActions.All.Count} action labels, {Enum.GetValues<KeyCategory>().Length} categories, modifiers and page text in both locales")
                : new Check(Name, false, string.Join("; ", problems));
        }
        catch (Exception ex) { return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>The dispatcher routes a synthetic key event to the right registered action, in the right phase and
    /// the right focus state, ignores it while a text field has focus (except chords the field does not own), and
    /// follows a rebinding.</summary>
    private static Check CheckKeyDispatcher()
    {
        const string Name = "key bindings: dispatcher";
        var owner = new Node();
        try
        {
            var table = new KeyBindingTable();
            var dispatcher = new KeyDispatcher(table);
            var calls = new Dictionary<string, int>();
            void Count(string id) => calls[id] = calls.GetValueOrDefault(id) + 1;
            int Calls(string id) => calls.GetValueOrDefault(id);

            foreach (var id in new[] { KeyActionIds.WindowInventory, KeyActionIds.EditCopy, KeyActionIds.CameraReset, KeyActionIds.ChatStart, KeyActionIds.MoveToggleFly, KeyActionIds.DevTPose })
            {
                var captured = id;
                dispatcher.Register(owner, captured, () => { Count(captured); return true; });
            }

            var problems = new List<string>();
            void Expect(bool condition, string what) { if (!condition) problems.Add(what); }

            // A menu-style chord: right chord -> the right action, before the GUI.
            Expect(dispatcher.Dispatch(Press("Ctrl+I"), KeyContext.World, afterGui: false), "Ctrl+I in the world was not handled");
            Expect(Calls(KeyActionIds.WindowInventory) == 1, "Ctrl+I did not reach window.inventory exactly once");
            // ... and the wrong phase does nothing (it is not a World-only action).
            Expect(!dispatcher.Dispatch(Press("Ctrl+I"), KeyContext.World, afterGui: true), "Ctrl+I fired in the after-GUI phase");

            // While typing: a command chord still fires, a chord the field owns does not.
            Expect(dispatcher.Dispatch(Press("Ctrl+I"), KeyContext.TextField, afterGui: false), "Ctrl+I was swallowed by a text field");
            Expect(dispatcher.Dispatch(Press("Ctrl+I"), KeyContext.ChatInput, afterGui: false), "Ctrl+I was swallowed by the chat bar");
            Expect(Calls(KeyActionIds.WindowInventory) == 3, "Ctrl+I from the text fields did not count");
            Expect(!dispatcher.Dispatch(Press("Ctrl+C"), KeyContext.TextField, afterGui: false), "Ctrl+C fired edit.copy inside a text field");
            Expect(!dispatcher.Dispatch(Press("Ctrl+C"), KeyContext.ChatInput, afterGui: false), "Ctrl+C fired edit.copy inside the chat bar");
            Expect(Calls(KeyActionIds.EditCopy) == 0, "edit.copy ran while typing");
            Expect(dispatcher.Dispatch(Press("Ctrl+C"), KeyContext.Window, afterGui: false), "Ctrl+C outside a text field was not handled");
            Expect(Calls(KeyActionIds.EditCopy) == 1, "Ctrl+C did not reach edit.copy");

            // A World-only action: after the GUI, only with the world focused.
            Expect(!dispatcher.Dispatch(Press("Escape"), KeyContext.World, afterGui: false), "Esc fired before the GUI");
            Expect(!dispatcher.Dispatch(Press("Escape"), KeyContext.Window, afterGui: true), "Esc fired with a window control focused");
            Expect(!dispatcher.Dispatch(Press("Escape"), KeyContext.TextField, afterGui: true), "Esc fired from a text field");
            Expect(dispatcher.Dispatch(Press("Escape"), KeyContext.World, afterGui: true), "Esc in the world was not handled");
            Expect(Calls(KeyActionIds.CameraReset) == 1, "Esc did not reach camera.reset exactly once");

            // The same key is two different things in two contexts: Enter starts chatting in the world, and is the
            // chat bar's own (not an action) inside it.
            Expect(dispatcher.Dispatch(Press("Enter"), KeyContext.World, afterGui: true), "Enter in the world did not start chatting");
            Expect(!dispatcher.Dispatch(Press("Enter"), KeyContext.ChatInput, afterGui: true), "Enter fired the world action inside the chat bar");
            Expect(!dispatcher.Dispatch(Press("F"), KeyContext.TextField, afterGui: true), "plain F fired while typing");
            Expect(dispatcher.Dispatch(Press("F"), KeyContext.World, afterGui: true), "F in the world did not toggle fly");

            // Not a press, a repeat, or suspended.
            Expect(!dispatcher.Dispatch(GodotKeyMap.MakeEvent(KeyChord.Parse("F8"), pressed: false), KeyContext.World, afterGui: false), "a key release fired an action");
            var echo = Press("F8"); echo.Echo = true;
            Expect(!dispatcher.Dispatch(echo, KeyContext.World, afterGui: false), "a key repeat fired an action");
            dispatcher.Suspended = true;
            Expect(!dispatcher.Dispatch(Press("F8"), KeyContext.World, afterGui: false), "an action fired while the dispatcher was suspended");
            dispatcher.Suspended = false;
            Expect(dispatcher.Dispatch(Press("F8"), KeyContext.World, afterGui: false) && Calls(KeyActionIds.DevTPose) == 1, "F8 did not reach dev.tpose after resuming");

            // Meta chords are out of scope: never matched.
            var meta = Press("Ctrl+I"); meta.MetaPressed = true;
            Expect(!dispatcher.Dispatch(meta, KeyContext.World, afterGui: false), "a Meta chord matched");

            // A rebinding takes effect at once: the old chord goes quiet, the new one fires.
            table.SetChords(KeyActionIds.WindowInventory, new[] { KeyChord.Parse("Ctrl+B") });
            Expect(!dispatcher.Dispatch(Press("Ctrl+I"), KeyContext.World, afterGui: false), "the old chord still fires after a rebinding");
            Expect(dispatcher.Dispatch(Press("Ctrl+B"), KeyContext.World, afterGui: false), "the new chord does not fire after a rebinding");

            // A handler that declines (the inventory's Ctrl+C away from the panel) does not consume the key.
            dispatcher.Register(owner, KeyActionIds.WindowStats, () => false);
            Expect(!dispatcher.Dispatch(Press("Ctrl+Shift+1"), KeyContext.World, afterGui: false), "a declining handler consumed the key");

            // A freed owner's handler is dropped, not called.
            var gone = new Node();
            int goneCalls = 0;
            dispatcher.Register(gone, KeyActionIds.WindowOutfits, () => { goneCalls++; return true; });
            gone.Free();
            Expect(!dispatcher.Dispatch(Press("Ctrl+O"), KeyContext.World, afterGui: false) && goneCalls == 0, "a freed owner's handler ran");

            dispatcher.Free();
            return problems.Count == 0
                ? new Check(Name, true, "synthetic key events reach the registered action in the right phase and focus state, are ignored while typing unless the field does not own the chord, follow a rebinding, and a declining or freed handler is skipped")
                : new Check(Name, false, string.Join("; ", problems));
        }
        catch (Exception ex) { return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}"); }
        finally { owner.Free(); }
    }

    /// <summary>The focus state is read the way AvatarController's movement gate reads it.</summary>
    private static Check CheckKeyFocusClassification()
    {
        const string Name = "key bindings: focus classification";
        var nodes = new List<Node>();
        try
        {
            var problems = new List<string>();
            void Expect(KeyContext actual, KeyContext expected, string what)
            {
                if (actual != expected) problems.Add($"{what}: {actual}, expected {expected}");
            }

            var line = new LineEdit(); nodes.Add(line);
            var text = new TextEdit(); nodes.Add(text);
            var chat = new LineEdit(); chat.AddToGroup(KeyDispatcher.ChatInputGroup); nodes.Add(chat);
            var window = new UI.SLNGWindow(); nodes.Add(window);
            var inWindow = new Button(); window.AddChild(inWindow);
            var hudWidget = new Button(); nodes.Add(hudWidget);

            Expect(KeyDispatcher.Classify(null), KeyContext.World, "nothing focused");
            Expect(KeyDispatcher.Classify(hudWidget), KeyContext.World, "a HUD widget outside any window");
            Expect(KeyDispatcher.Classify(inWindow), KeyContext.Window, "a button inside a window");
            Expect(KeyDispatcher.Classify(line), KeyContext.TextField, "a LineEdit");
            Expect(KeyDispatcher.Classify(text), KeyContext.TextField, "a TextEdit");
            Expect(KeyDispatcher.Classify(chat), KeyContext.ChatInput, "the chat input");

            return problems.Count == 0
                ? new Check(Name, true, "world / window / text field / chat bar are told apart")
                : new Check(Name, false, string.Join("; ", problems));
        }
        catch (Exception ex) { return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}"); }
        finally { foreach (var n in nodes) n.Free(); }
    }

    /// <summary>WASD and the Alt camera keys still resolve through the table: exact modifiers, Shift as the run
    /// modifier, a rebinding is followed, no real keyboard involved.</summary>
    private static Check CheckKeyHeldActions()
    {
        const string Name = "key bindings: held keys resolve through the table";
        var savedKeyDown = HeldKeys.KeyDown;
        var savedTable = HeldKeys.Table;
        try
        {
            var table = new KeyBindingTable();
            var down = new HashSet<Key>();
            HeldKeys.Table = table;
            HeldKeys.KeyDown = down.Contains;

            var problems = new List<string>();
            bool Held(string id) { HeldKeys.InvalidateFrame(); return HeldKeys.IsHeld(id); }
            void Hold(params Key[] keys) { down.Clear(); foreach (var k in keys) down.Add(k); }
            void Expect(bool condition, string what) { if (!condition) problems.Add(what); }

            Hold(Key.W);
            Expect(Held(KeyActionIds.MoveForward), "W is not forward");
            Expect(!Held(KeyActionIds.CameraZoomIn), "W alone moves the camera");
            Hold(Key.Up);
            Expect(Held(KeyActionIds.MoveForward), "Up is not forward");
            Hold(Key.A); Expect(Held(KeyActionIds.MoveTurnLeft), "A is not turn left");
            Hold(Key.D); Expect(Held(KeyActionIds.MoveTurnRight), "D is not turn right");
            Hold(Key.S); Expect(Held(KeyActionIds.MoveBackward), "S is not backward");
            Hold(Key.E); Expect(Held(KeyActionIds.MoveUp), "E is not up");
            Hold(Key.Pageup); Expect(Held(KeyActionIds.MoveUp), "PageUp is not up");
            Hold(Key.C); Expect(Held(KeyActionIds.MoveDown), "C is not down");
            Hold(Key.Q); Expect(Held(KeyActionIds.MoveDown), "Q is not down");
            Hold(Key.Pagedown); Expect(Held(KeyActionIds.MoveDown), "PageDown is not down");

            // Shift is the run modifier: Shift+W is still forward.
            Hold(Key.Shift, Key.W);
            Expect(Held(KeyActionIds.MoveForward), "Shift+W is not forward (run)");

            // Ctrl or Alt turn a movement key into somebody's shortcut.
            Hold(Key.Ctrl, Key.W); Expect(!Held(KeyActionIds.MoveForward), "Ctrl+W walks");
            Hold(Key.Ctrl, Key.C); Expect(!Held(KeyActionIds.MoveDown), "Ctrl+C crouches");
            Hold(Key.Alt, Key.W);
            Expect(!Held(KeyActionIds.MoveForward), "Alt+W walks");
            Expect(Held(KeyActionIds.CameraZoomIn), "Alt+W does not move the camera in");

            // The Alt camera keys of FEAT-UI-40.
            Hold(Key.Alt, Key.S); Expect(Held(KeyActionIds.CameraZoomOut), "Alt+S");
            Hold(Key.Alt, Key.Up); Expect(Held(KeyActionIds.CameraZoomIn), "Alt+Up");
            Hold(Key.Alt, Key.Down); Expect(Held(KeyActionIds.CameraZoomOut), "Alt+Down");
            Hold(Key.Alt, Key.A); Expect(Held(KeyActionIds.CameraOrbitCw), "Alt+A");
            Hold(Key.Alt, Key.Left); Expect(Held(KeyActionIds.CameraOrbitCw), "Alt+Left");
            Hold(Key.Alt, Key.D); Expect(Held(KeyActionIds.CameraOrbitCcw), "Alt+D");
            Hold(Key.Alt, Key.Right); Expect(Held(KeyActionIds.CameraOrbitCcw), "Alt+Right");
            Hold(Key.Alt, Key.E); Expect(Held(KeyActionIds.CameraOrbitOver), "Alt+E");
            Hold(Key.Alt, Key.Pageup); Expect(Held(KeyActionIds.CameraOrbitOver), "Alt+PageUp");
            Hold(Key.Alt, Key.C); Expect(Held(KeyActionIds.CameraOrbitUnder), "Alt+C");
            Hold(Key.Alt, Key.Pagedown); Expect(Held(KeyActionIds.CameraOrbitUnder), "Alt+PageDown");
            Hold(Key.Ctrl, Key.Alt, Key.W);
            Expect(Held(KeyActionIds.CameraOrbitOver) && !Held(KeyActionIds.CameraZoomIn), "Ctrl+Alt+W is not orbit-over");
            Hold(Key.Ctrl, Key.Alt, Key.S);
            Expect(Held(KeyActionIds.CameraOrbitUnder) && !Held(KeyActionIds.CameraZoomOut), "Ctrl+Alt+S is not orbit-under");
            Hold(Key.Ctrl, Key.Alt, Key.Up); Expect(Held(KeyActionIds.CameraOrbitOver), "Ctrl+Alt+Up");
            Hold(Key.Ctrl, Key.Alt, Key.Down); Expect(Held(KeyActionIds.CameraOrbitUnder), "Ctrl+Alt+Down");
            Hold(Key.Ctrl, Key.Alt, Key.Shift, Key.A); Expect(Held(KeyActionIds.CameraPanLeft), "Ctrl+Alt+Shift+A");
            Hold(Key.Ctrl, Key.Alt, Key.Shift, Key.Up); Expect(Held(KeyActionIds.CameraPanUp), "Ctrl+Alt+Shift+Up");
            Hold(Key.Alt, Key.Shift, Key.W); Expect(!Held(KeyActionIds.CameraZoomIn), "Alt+Shift+W zooms the camera (the camera keys are exact)");

            // The +/- keyboard zoom of the camera distance.
            Hold(Key.Equal); Expect(Held(KeyActionIds.CameraDollyIn), "= is not dolly in");
            Hold(Key.KpSubtract); Expect(Held(KeyActionIds.CameraDollyOut), "Num - is not dolly out");

            // Rebinding is followed at once, and the old key stops.
            table.SetChords(KeyActionIds.MoveForward, new[] { KeyChord.Parse("I") });
            Hold(Key.I); Expect(Held(KeyActionIds.MoveForward), "a rebinding to I is not followed");
            Hold(Key.W); Expect(!Held(KeyActionIds.MoveForward), "W still walks after being unbound from forward");
            Hold(Key.Alt, Key.W); Expect(Held(KeyActionIds.CameraZoomIn), "Alt+W stopped moving the camera after forward was rebound");

            // Nothing held = nothing active, for every held action.
            Hold();
            foreach (var a in table.Actions.Where(a => a.Kind == KeyActionKind.Held))
                Expect(!Held(a.Id), $"{a.Id} active with no key down");

            return problems.Count == 0
                ? new Check(Name, true, "WASD / arrows / E C Q PageUp PageDown, the Alt camera keys (Ctrl+Alt spin, Ctrl+Alt+Shift pan) and = / - resolve through the table with exact modifiers (Shift ignored only on avatar movement) and follow a rebinding")
                : new Check(Name, false, string.Join("; ", problems));
        }
        catch (Exception ex) { return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            HeldKeys.KeyDown = savedKeyDown;
            HeldKeys.Table = savedTable;
            HeldKeys.InvalidateFrame();
        }
    }

    /// <summary>The Keyboard page builds, lists every action, and rebinds from synthetic key events - with conflict
    /// handling - against an in-memory table, reporting each change through a counter instead of saving.</summary>
    private static Check CheckKeyboardPage(SceneTree tree)
    {
        const string Name = "key bindings: keyboard page";
        var page = new UI.KeyboardPreferencesPage();
        try
        {
            var table = new KeyBindingTable();
            tree.Root.AddChild(page);
            int saves = 0;
            page.Initialize(table, () => saves++, dispatcher: null);

            var problems = new List<string>();
            void Expect(bool condition, string what) { if (!condition) problems.Add(what); }

            Expect(page.ListedActionIds.Count == table.Actions.Count,
                $"the page lists {page.ListedActionIds.Count} of {table.Actions.Count} actions");
            Expect(!page.ListedActionIds.Except(table.Actions.Select(a => a.Id)).Any(), "the page lists an action that is not in the catalog");
            Expect(!KeyChordText.ActionLabel(KeyActionIds.WindowInventory).StartsWith('['), "an action label did not resolve in the current language");
            Expect(KeyChordText.Format(KeyChord.Parse("Ctrl+Shift+1")).EndsWith("1", StringComparison.Ordinal), "chord text does not end with its key");

            // Change a chord by pressing keys.
            page.BeginCapture(KeyActionIds.WindowStats, 0);
            Expect(page.IsListening, "BeginCapture did not start listening");
            page.OfferKey(Press("Ctrl+Shift+2"));
            Expect(!page.IsListening, "still listening after a free chord");
            Expect(table.ChordsOf(KeyActionIds.WindowStats).SequenceEqual(new[] { KeyChord.Parse("Ctrl+Shift+2") }), "the new chord was not applied");
            Expect(saves == 1, $"a change saved {saves} times");

            // Esc cancels, a bare modifier is ignored.
            page.BeginCapture(KeyActionIds.WindowStats, 0);
            page.OfferKey(new InputEventKey { Keycode = Key.Shift, Pressed = true, ShiftPressed = true });
            Expect(page.IsListening, "a bare modifier ended the listening");
            page.OfferKey(Press("Escape"));
            Expect(!page.IsListening && saves == 1, "Esc did not cancel cleanly");
            Expect(table.ChordsOf(KeyActionIds.WindowStats).Count == 1, "Esc changed the binding");

            // Add a second chord to an action.
            page.BeginCapture(KeyActionIds.WindowStats, -1);
            page.OfferKey(Press("F10"));
            Expect(table.ChordsOf(KeyActionIds.WindowStats).Count == 2, "the add button did not add a chord");

            // A conflict is held back and names the other action; Cancel leaves everything alone.
            page.BeginCapture(KeyActionIds.WindowStats, 0);
            page.OfferKey(Press("Ctrl+I"));
            Expect(page.IsAwaitingConflictChoice, "a conflicting chord was not held back");
            Expect(page.StatusText.Contains(KeyChordText.ActionLabel(KeyActionIds.WindowInventory)), "the conflict message does not name the other action");
            Expect(table.IsDefault(KeyActionIds.WindowInventory), "the conflicting action lost its chord before the answer");
            page.ResolveConflict(replace: false);
            Expect(!page.IsAwaitingConflictChoice && table.IsDefault(KeyActionIds.WindowInventory), "Cancel changed something");

            // Replace takes the chord over.
            page.BeginCapture(KeyActionIds.WindowStats, 0);
            page.OfferKey(Press("Ctrl+I"));
            page.ResolveConflict(replace: true);
            Expect(table.ChordsOf(KeyActionIds.WindowStats).Contains(KeyChord.Parse("Ctrl+I")), "Replace did not give the chord to the new action");
            Expect(table.ChordsOf(KeyActionIds.WindowInventory).Count == 0, "Replace did not take the chord from the old action");
            Expect(table.Conflicts(KeyActionIds.WindowStats, KeyChord.Parse("Ctrl+I")).Count == 0, "a conflict is left after Replace");

            return problems.Count == 0
                ? new Check(Name, true, $"lists all {table.Actions.Count} actions; rebinds from key presses, add / Esc / bare-modifier / conflict (cancel and replace) behave; {saves} change notifications")
                : new Check(Name, false, string.Join("; ", problems));
        }
        catch (Exception ex) { return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}"); }
        finally { if (GodotObject.IsInstanceValid(page)) { page.GetParent()?.RemoveChild(page); page.Free(); } }
    }

    /// <summary>A customised table survives writing to a config file and reading it back; an untouched one writes
    /// nothing; a file with unknown ids, unknown keys or the wrong value type does not break loading. In memory:
    /// the <see cref="ConfigFile"/> is encoded to text and parsed, never saved.</summary>
    private static Check CheckKeyPersistenceInMemory()
    {
        const string Name = "key bindings: persistence (in memory)";
        try
        {
            var problems = new List<string>();
            void Expect(bool condition, string what) { if (!condition) problems.Add(what); }

            var a = new KeyBindingTable();
            var untouched = new ConfigFile();
            untouched.SetValue("camera", "fov", 60.0);
            KeyBindings.WriteTo(untouched, a);
            Expect(!untouched.HasSection("key_bindings"), "an untouched table wrote a key_bindings section");
            Expect(untouched.HasSection("camera"), "writing the key bindings dropped another section");

            a.SetChords(KeyActionIds.WindowStats, new[] { KeyChord.Parse("Ctrl+Shift+2"), KeyChord.Parse("F10") });
            a.SetChords(KeyActionIds.DevTPose, Array.Empty<KeyChord>());
            var cfg = new ConfigFile();
            cfg.SetValue("camera", "fov", 60.0);
            KeyBindings.WriteTo(cfg, a);
            Expect(cfg.GetSectionKeys("key_bindings").Length == 2, "more than the two differences were written");

            var parsed = new ConfigFile();
            var err = parsed.Parse(cfg.EncodeToText());
            Expect(err == Error.Ok, $"the saved text did not parse: {err}");
            var b = new KeyBindingTable();
            var result = KeyBindings.ReadFrom(parsed, b);
            Expect(result.Applied == 2, $"{result.Applied} overrides applied, expected 2");
            foreach (var action in a.Actions)
                Expect(a.ChordsOf(action.Id).SequenceEqual(b.ChordsOf(action.Id)), $"{action.Id} did not round-trip");

            // A file from some other version.
            var odd = new ConfigFile();
            odd.SetValue("key_bindings", "window.from_the_future", "Ctrl+Shift+F13");
            odd.SetValue("key_bindings", KeyActionIds.WindowInventory, "Hyper+Nothing");
            odd.SetValue("key_bindings", KeyActionIds.WindowStats, 42);
            odd.SetValue("key_bindings", KeyActionIds.WindowSnapshot, "Ctrl+Alt+Shift+F12");
            var c = new KeyBindingTable();
            var oddResult = KeyBindings.ReadFrom(odd, c);
            Expect(oddResult.UnknownActions == 1, "an unknown action id was not counted as ignored");
            Expect(c.IsDefault(KeyActionIds.WindowInventory), "an unusable chord replaced the default");
            Expect(c.IsDefault(KeyActionIds.WindowStats), "a wrongly typed value replaced the default");
            Expect(c.ChordsOf(KeyActionIds.WindowSnapshot).Count == 1, "the one good entry in a strange file was not applied");

            return problems.Count == 0
                ? new Check(Name, true, "only differences are written, none when untouched, other sections kept; round-trips through ConfigFile text; unknown ids, unusable chords and wrong value types are ignored")
                : new Check(Name, false, string.Join("; ", problems));
        }
        catch (Exception ex) { return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}"); }
    }
}
