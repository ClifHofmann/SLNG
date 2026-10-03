using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SLNG.Core.Input;

namespace SLNG.App.UI;

/// <summary>
/// "Keyboard" tab content for PreferencesWindow (FEAT-UI-43): every action SLNG can run from a key,
/// grouped by category, with its current chords. Click a chord to change it and press the new keys;
/// "+" adds another chord, "x" removes one, the reset arrow restores an action's defaults, and
/// "Reset all" restores everything. A chord already used by another action in an overlapping context
/// is not silently stolen: the page names the other action and asks "Replace" or "Cancel".
///
/// <para>The page edits a <see cref="KeyBindingTable"/> it is given and calls <c>onChanged</c> after
/// each change (the app passes <see cref="KeyBindings.Save"/>), so it holds no key state of its own and
/// can be driven against an in-memory table. Only differences from the defaults are ever saved.</para>
///
/// <para><b>Listening.</b> While waiting for the new chord the page raises
/// <see cref="KeyDispatcher.Suspended"/>, so pressing e.g. Ctrl+I to bind it does not also open the
/// inventory. Esc cancels, so Esc itself cannot be captured as a new binding (it is the camera-reset
/// default, and "reset" restores it); a bare modifier is ignored until a real key follows.</para>
///
/// <para>Not on this page: SL shortcuts SLNG has no feature for (Mouselook, whisper/shout, build tool modes
/// ...). They are deliberately unbound and are listed in the manual and in
/// <see cref="SlShortcutGaps"/>, never as a fake action here.</para>
/// </summary>
public partial class KeyboardPreferencesPage : VBoxContainer
{
    private KeyBindingTable _table = null!;
    private Action? _onChanged;
    private KeyDispatcher? _dispatcher;

    private LineEdit _search = null!;
    private Button _resetAll = null!;
    private VBoxContainer _list = null!;
    private PanelContainer _statusBar = null!;
    private Label _statusLabel = null!;
    private Button _replaceButton = null!;
    private Button _cancelButton = null!;

    private readonly Dictionary<string, Control> _rows = new();
    private readonly Dictionary<KeyCategory, Control> _headings = new();

    // Listening for a chord: which action, and which of its chords to replace (-1 = add one).
    private (string ActionId, int Index)? _capture;
    // A chord that conflicts, waiting for Replace / Cancel.
    private (string ActionId, int Index, KeyChord Chord)? _pending;
    private bool _resetAllArmed;

    /// <summary>How many times the page has asked for the table to be saved (the selftest reads this).</summary>
    public int ChangeCount { get; private set; }

    public string StatusText => _statusLabel.Text;
    public bool IsListening => _capture != null;
    public bool IsAwaitingConflictChoice => _pending != null;
    public IReadOnlyCollection<string> ListedActionIds => _rows.Keys;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 8);
    }

    /// <summary>Builds the page. Call once, right after this page has been added via
    /// PreferencesWindow.AddTab (mirrors the other preference pages).</summary>
    public void Initialize(KeyBindingTable table, Action? onChanged, KeyDispatcher? dispatcher)
    {
        _table = table;
        _onChanged = onChanged;
        _dispatcher = dispatcher;

        var hint = new Label { Text = L10n.Tr("ui.keys.hint"), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        hint.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
        hint.AddThemeFontSizeOverride("font_size", 12);
        AddChild(hint);

        var toolbar = new HBoxContainer();
        toolbar.AddThemeConstantOverride("separation", 8);
        AddChild(toolbar);

        _search = new LineEdit
        {
            PlaceholderText = L10n.Tr("ui.keys.search_placeholder"),
            ClearButtonEnabled = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _search.TextChanged += _ => ApplyFilter();
        toolbar.AddChild(_search);

        _resetAll = new Button { Text = L10n.Tr("ui.keys.reset_all"), FocusMode = FocusModeEnum.None };
        _resetAll.Pressed += OnResetAllPressed;
        toolbar.AddChild(_resetAll);

        // Status / conflict bar: hidden until there is something to say.
        _statusBar = new PanelContainer { Visible = false };
        _statusBar.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.3f, 0.6f, 0.9f, 0.18f),
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6,
        });
        AddChild(_statusBar);
        var statusBox = new VBoxContainer();
        statusBox.AddThemeConstantOverride("separation", 6);
        _statusBar.AddChild(statusBox);
        _statusLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _statusLabel.AddThemeFontSizeOverride("font_size", 13);
        statusBox.AddChild(_statusLabel);
        var statusButtons = new HBoxContainer();
        statusButtons.AddThemeConstantOverride("separation", 8);
        statusBox.AddChild(statusButtons);
        _replaceButton = new Button { Text = L10n.Tr("ui.keys.replace"), Visible = false, FocusMode = FocusModeEnum.None };
        _replaceButton.Pressed += () => ResolveConflict(replace: true);
        statusButtons.AddChild(_replaceButton);
        _cancelButton = new Button { Text = L10n.Tr("ui.keys.cancel"), FocusMode = FocusModeEnum.None };
        _cancelButton.Pressed += CancelAll;
        statusButtons.AddChild(_cancelButton);

        _list = new VBoxContainer();
        _list.AddThemeConstantOverride("separation", 2);
        AddChild(_list);

        var gaps = new Label { Text = L10n.Tr("ui.keys.unavailable_note"), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        gaps.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
        gaps.AddThemeFontSizeOverride("font_size", 12);
        AddChild(gaps);

        BuildList();
        VisibilityChanged += () => { if (!IsVisibleInTree()) CancelAll(); };
    }

    // ---- list ------------------------------------------------------------------------------------

    private void BuildList()
    {
        // Removed first, freed after: a QueueFree alone would leave the old rows in the tree (and drawn
        // twice) until the end of the frame.
        foreach (var child in _list.GetChildren())
        {
            _list.RemoveChild(child);
            child.QueueFree();
        }
        _rows.Clear();
        _headings.Clear();

        foreach (var group in _table.Actions.GroupBy(a => a.Category).OrderBy(g => g.Key))
        {
            var heading = new Label { Text = L10n.Tr(KeyChordText.CategoryLabelKey(group.Key)) };
            heading.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
            heading.AddThemeConstantOverride("line_spacing", 0);
            var headingBox = new VBoxContainer();
            headingBox.AddThemeConstantOverride("separation", 2);
            headingBox.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
            headingBox.AddChild(heading);
            headingBox.AddChild(new HSeparator());
            _list.AddChild(headingBox);
            _headings[group.Key] = headingBox;

            foreach (var action in group)
            {
                var row = BuildRow(action);
                _list.AddChild(row);
                _rows[action.Id] = row;
            }
        }
        ApplyFilter();
    }

    private Control BuildRow(KeyAction action)
    {
        var box = new VBoxContainer { Name = "Row_" + action.Id.Replace('.', '_') };
        box.AddThemeConstantOverride("separation", 2);

        var name = new Label { Text = KeyChordText.ActionLabel(action.Id) };
        name.AddThemeColorOverride("font_color", new Color(0.78f, 0.78f, 0.78f));
        name.AddThemeFontSizeOverride("font_size", 13);
        box.AddChild(name);

        var chordRow = new HFlowContainer();
        chordRow.AddThemeConstantOverride("h_separation", 6);
        chordRow.AddThemeConstantOverride("v_separation", 2);
        box.AddChild(chordRow);

        var chords = _table.ChordsOf(action.Id);
        for (int i = 0; i < chords.Count; i++)
        {
            int index = i;
            var pair = new HBoxContainer();
            pair.AddThemeConstantOverride("separation", 0);
            var chordButton = new Button
            {
                Text = KeyChordText.Format(chords[i]),
                TooltipText = L10n.Tr("ui.keys.change_tooltip"),
                FocusMode = FocusModeEnum.None,
            };
            chordButton.AddThemeFontSizeOverride("font_size", 13);
            chordButton.Pressed += () => BeginCapture(action.Id, index);
            pair.AddChild(chordButton);
            var remove = new Button
            {
                Text = "×",
                Flat = true,
                TooltipText = L10n.Tr("ui.keys.remove_tooltip"),
                FocusMode = FocusModeEnum.None,
            };
            remove.Pressed += () => RemoveChord(action.Id, index);
            pair.AddChild(remove);
            chordRow.AddChild(pair);
        }

        if (chords.Count == 0)
        {
            var unbound = new Label { Text = L10n.Tr("ui.keys.unbound") };
            unbound.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
            unbound.AddThemeFontSizeOverride("font_size", 13);
            chordRow.AddChild(unbound);
        }

        var add = new Button { Text = "+", TooltipText = L10n.Tr("ui.keys.add_tooltip"), FocusMode = FocusModeEnum.None };
        add.Pressed += () => BeginCapture(action.Id, -1);
        chordRow.AddChild(add);

        var reset = new Button
        {
            Text = "↺",
            Flat = true,
            TooltipText = L10n.Tr("ui.keys.reset_one_tooltip"),
            Disabled = _table.IsDefault(action.Id),
            FocusMode = FocusModeEnum.None,
        };
        reset.Pressed += () => ResetAction(action.Id);
        chordRow.AddChild(reset);

        return box;
    }

    /// <summary>Hides the rows (and category headings) that do not match the search text, which is
    /// matched against the localized label, the id and every current chord.</summary>
    private void ApplyFilter()
    {
        string needle = _search.Text.Trim().ToLowerInvariant();
        var visibleCategories = new HashSet<KeyCategory>();
        foreach (var action in _table.Actions)
        {
            if (!_rows.TryGetValue(action.Id, out var row)) continue;
            bool match = needle.Length == 0
                || KeyChordText.ActionLabel(action.Id).ToLowerInvariant().Contains(needle)
                || action.Id.Contains(needle, StringComparison.Ordinal)
                || _table.ChordsOf(action.Id).Any(c =>
                    KeyChordText.Format(c).ToLowerInvariant().Contains(needle)
                    || c.ToString().ToLowerInvariant().Contains(needle));
            row.Visible = match;
            if (match) visibleCategories.Add(action.Category);
        }
        foreach (var (category, heading) in _headings) heading.Visible = visibleCategories.Contains(category);
    }

    // ---- editing ---------------------------------------------------------------------------------

    /// <summary>Starts listening for a chord. <paramref name="index"/> is the chord to replace, or -1
    /// to add another one.</summary>
    public void BeginCapture(string actionId, int index)
    {
        CancelAll();
        if (_table.Find(actionId) == null) return;
        _capture = (actionId, index);
        if (_dispatcher != null) _dispatcher.Suspended = true;
        ShowStatus(L10n.TrFormat("ui.keys.listening", KeyChordText.ActionLabel(actionId)), showReplace: false);
    }

    /// <summary>Feeds one key event to the page while it is listening. Returns true when the event was
    /// used (the caller then marks it handled). Public so the selftest can drive a rebinding with
    /// synthetic events.</summary>
    public bool OfferKey(InputEventKey e)
    {
        if (_capture == null || !e.Pressed || e.Echo) return false;

        var key = e.Keycode;
        if (key is Key.Ctrl or Key.Shift or Key.Alt or Key.Meta or Key.Capslock) return true; // wait for the real key

        if (key == Key.Escape && !e.CtrlPressed && !e.AltPressed && !e.ShiftPressed)
        {
            CancelAll();
            return true;
        }

        if (!GodotKeyMap.TryGetChord(e, out var chord))
        {
            ShowStatus(L10n.Tr("ui.keys.cannot_use"), showReplace: false);
            return true;
        }
        OfferChord(chord);
        return true;
    }

    /// <summary>The chord the person pressed: applied at once when free, otherwise held back behind a
    /// Replace / Cancel question that names the other action(s).</summary>
    public void OfferChord(KeyChord chord)
    {
        if (_capture is not { } capture) return;
        var chords = _table.ChordsOf(capture.ActionId);

        // The same chord on the slot being edited: nothing to do.
        if (capture.Index >= 0 && capture.Index < chords.Count && chords[capture.Index] == chord)
        {
            CancelAll();
            return;
        }

        var conflicts = _table.Conflicts(capture.ActionId, chord);
        if (conflicts.Count == 0)
        {
            Apply(capture.ActionId, capture.Index, chord, replaceConflicts: false);
            return;
        }

        _capture = null;
        if (_dispatcher != null) _dispatcher.Suspended = false;
        _pending = (capture.ActionId, capture.Index, chord);
        ShowStatus(L10n.TrFormat("ui.keys.conflict", KeyChordText.Format(chord),
            string.Join(", ", conflicts.Select(c => KeyChordText.ActionLabel(c.Id)))), showReplace: true);
    }

    /// <summary>The Replace / Cancel answer to a conflict.</summary>
    public void ResolveConflict(bool replace)
    {
        if (_pending is not { } pending) return;
        _pending = null;
        if (!replace) { CancelAll(); return; }
        Apply(pending.ActionId, pending.Index, pending.Chord, replaceConflicts: true);
    }

    private void Apply(string actionId, int index, KeyChord chord, bool replaceConflicts)
    {
        _table.Assign(actionId, chord, replaceConflicts, index);
        EndListening();

        // A chord a text field keeps for editing never reaches a menu-style action while typing.
        var action = _table.Find(actionId);
        bool typingNote = action != null && KeyContexts.IsTyping(action.Context) && TextEditChords.Owns(chord);
        Changed();
        if (typingNote) ShowStatus(L10n.Tr("ui.keys.note_typing"), showReplace: false);
    }

    private void RemoveChord(string actionId, int index)
    {
        var chords = _table.ChordsOf(actionId);
        if (index < 0 || index >= chords.Count) return;
        CancelAll();
        _table.Remove(actionId, chords[index]);
        Changed();
    }

    private void ResetAction(string actionId)
    {
        CancelAll();
        _table.Reset(actionId);
        Changed();
    }

    private void OnResetAllPressed()
    {
        CancelAll();
        if (!_resetAllArmed)
        {
            // Two clicks, so one stray click does not throw away every customisation.
            _resetAllArmed = true;
            _resetAll.Text = L10n.Tr("ui.keys.reset_all_confirm");
            GetTree().CreateTimer(3.0).Timeout += () =>
            {
                if (!IsInstanceValid(this)) return;
                _resetAllArmed = false;
                _resetAll.Text = L10n.Tr("ui.keys.reset_all");
            };
            return;
        }
        _resetAllArmed = false;
        _resetAll.Text = L10n.Tr("ui.keys.reset_all");
        _table.ResetAll();
        Changed();
    }

    private void Changed()
    {
        ChangeCount++;
        _onChanged?.Invoke();
        BuildList();
    }

    // ---- listening state -------------------------------------------------------------------------

    private void CancelAll()
    {
        _pending = null;
        EndListening();
        HideStatus();
    }

    private void EndListening()
    {
        _capture = null;
        if (_dispatcher != null) _dispatcher.Suspended = false;
        HideStatus();
    }

    private void ShowStatus(string text, bool showReplace)
    {
        _statusLabel.Text = text;
        _replaceButton.Visible = showReplace;
        _statusBar.Visible = true;
    }

    private void HideStatus()
    {
        _statusBar.Visible = false;
        _replaceButton.Visible = false;
    }

    /// <summary>Takes the key presses while listening, before anything else sees them.</summary>
    public override void _Input(InputEvent @event)
    {
        if (_capture == null) return;
        if (@event is InputEventKey key && OfferKey(key)) GetViewport().SetInputAsHandled();
    }

    public override void _ExitTree()
    {
        if (_dispatcher != null) _dispatcher.Suspended = false;
        base._ExitTree();
    }
}
