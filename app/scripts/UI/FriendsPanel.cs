using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using SLNG.Core;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// Friends tab content for <see cref="ChatWindow"/> (M5-3 Phase 1b/1c): a filterable presence
/// list (left) plus a fixed per-friend action panel (right), matching the reviewed mockup
/// (docs/specs/M5-3-tabbed-chat-window.md §1). Filtering, row selection, and "IM / Call" (the IM
/// half only -- voice is a separate, much larger, unbuilt subsystem) are functional. Profile,
/// Teleport, Pay, Remove, and Add all still need net-layer plumbing that doesn't exist yet
/// (teleport requests, payments, friendship management), so they stay disabled with a "(not
/// implemented)" tooltip rather than silently omitted.
/// </summary>
public partial class FriendsPanel : Control
{
    private GridSession? _session;
    private LineEdit _filterEdit = null!;
    private VBoxContainer _list = null!;
    private Label _emptyLabel = null!;
    private Label _countLabel = null!;
    private Guid? _selectedFriendId;
    private string _selectedFriendName = "";

    /// <summary>Wired by ChatWindow to ChatWindow.OpenOrFocusImTab -- fired by the "IM / Call"
    /// action button and by double-clicking a friend row.</summary>
    public Action<Guid, string>? OnOpenImRequested;

    public override void _Ready()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        var hbox = new HBoxContainer();
        hbox.SetAnchorsPreset(LayoutPreset.FullRect);
        hbox.AddThemeConstantOverride("separation", 8);
        AddChild(hbox);

        var leftVBox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        leftVBox.AddThemeConstantOverride("separation", 6);
        hbox.AddChild(leftVBox);

        _filterEdit = new LineEdit { PlaceholderText = "Filter friends..." };
        _filterEdit.AddThemeFontSizeOverride("font_size", ChatWindow.BodyFontSize);
        _filterEdit.TextChanged += (_) => Refresh();
        leftVBox.AddChild(_filterEdit);

        _emptyLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Visible = false,
        };
        _emptyLabel.AddThemeFontSizeOverride("font_size", ChatWindow.BodyFontSize);
        _emptyLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        leftVBox.AddChild(_emptyLabel);

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        leftVBox.AddChild(scroll);

        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 2);
        scroll.AddChild(_list);

        hbox.AddChild(BuildActionPanel());
    }

    /// <summary>(Re-)binds to a GridSession -- safe to call again after a re-login, when Boot
    /// hands over a freshly constructed session (the old one is being disposed).</summary>
    public void Initialize(GridSession session)
    {
        if (_session != null)
        {
            _session.FriendStatusChanged -= OnFriendStatusChanged;
            _session.NameResolved -= OnNameResolved;
        }

        _session = session;
        _session.FriendStatusChanged += OnFriendStatusChanged;
        _session.NameResolved += OnNameResolved;

        Refresh();
    }

    private void OnFriendStatusChanged(object? sender, FriendStatusEvent e) => CallDeferred(nameof(Refresh));

    // A name resolving could be for anything (object owner, group, ...) -- Refresh() is a cheap
    // full rebuild from GetFriends(), so there's no need to filter to friend ids here.
    private void OnNameResolved(object? sender, NameResolvedEvent e) => CallDeferred(nameof(Refresh));

    private void Refresh()
    {
        foreach (Node child in _list.GetChildren())
        {
            _list.RemoveChild(child);
            child.QueueFree();
        }

        var friends = _session?.GetFriends() ?? Array.Empty<FriendEntry>();
        _countLabel.Text = $"Friends: {friends.Count}";

        string filterText = _filterEdit.Text.Trim();
        var visible = string.IsNullOrEmpty(filterText)
            ? friends
            : friends.Where(f => DisplayName(f).Contains(filterText, StringComparison.OrdinalIgnoreCase)).ToList();

        if (friends.Count == 0)
        {
            _emptyLabel.Text = "No friends yet. Add friends in-world to see them here.";
            _emptyLabel.Visible = true;
        }
        else if (visible.Count == 0)
        {
            _emptyLabel.Text = "No friends match your filter.";
            _emptyLabel.Visible = true;
        }
        else
        {
            _emptyLabel.Visible = false;
        }

        // Online first, then alphabetical within each group -- standard SL/Firestorm behavior.
        var sorted = visible
            .OrderByDescending(f => f.IsOnline)
            .ThenBy(DisplayName, StringComparer.OrdinalIgnoreCase);

        foreach (var friend in sorted)
            _list.AddChild(BuildRow(friend));
    }

    private static string DisplayName(FriendEntry friend) =>
        string.IsNullOrEmpty(friend.Name) ? friend.Id.ToString() : friend.Name;

    private Control BuildRow(FriendEntry friend)
    {
        var row = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };

        var inner = new HBoxContainer();
        inner.AddThemeConstantOverride("separation", 8);
        row.AddChild(inner);

        var dot = new Label
        {
            Text = "●",
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        dot.AddThemeFontSizeOverride("font_size", 9);
        dot.AddThemeColorOverride("font_color",
            friend.IsOnline ? new Color(0.3f, 0.85f, 0.3f) : new Color(0.4f, 0.4f, 0.4f));
        inner.AddChild(dot);

        var nameBtn = new Button
        {
            Text = DisplayName(friend),
            Flat = true,
            ClipText = true,
            FocusMode = FocusModeEnum.None,
            Alignment = HorizontalAlignment.Left,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        nameBtn.AddThemeFontSizeOverride("font_size", ChatWindow.BodyFontSize);
        nameBtn.AddThemeColorOverride("font_color",
            friend.IsOnline ? new Color(0.92f, 0.92f, 0.92f) : new Color(0.62f, 0.62f, 0.62f));
        var friendId = friend.Id;
        var friendName = DisplayName(friend);
        nameBtn.Pressed += () => { _selectedFriendId = friendId; _selectedFriendName = friendName; Refresh(); };
        // Double-clicking a friend opens their IM directly (per the M5-3 spec), rather than
        // requiring a select-then-click-"IM / Call" round trip.
        nameBtn.GuiInput += (@event) =>
        {
            if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, DoubleClick: true })
                OnOpenImRequested?.Invoke(friendId, friendName);
        };
        inner.AddChild(nameBtn);

        var style = new StyleBoxFlat
        {
            BgColor = friend.Id == _selectedFriendId ? new Color(0.3f, 0.6f, 0.9f, 0.25f) : new Color(0, 0, 0, 0),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 4,
            ContentMarginRight = 4,
            ContentMarginTop = 2,
            ContentMarginBottom = 2,
        };
        row.AddThemeStyleboxOverride("panel", style);

        return row;
    }

    /// <summary>Per-friend actions, fixed to the right of the filter/list column. All disabled
    /// placeholders for now -- see the class doc comment for why.</summary>
    private Control BuildActionPanel()
    {
        var panel = new VBoxContainer { CustomMinimumSize = new Vector2(92, 0) };
        panel.AddThemeConstantOverride("separation", 4);

        var imButton = BuildActionButton("IM / Call", accent: true);
        imButton.TooltipText = "Open IM (voice call not implemented)";
        imButton.Pressed += () =>
        {
            if (_selectedFriendId is { } id) OnOpenImRequested?.Invoke(id, _selectedFriendName);
        };
        panel.AddChild(imButton);

        panel.AddChild(BuildActionButton("Profile"));
        panel.AddChild(BuildActionButton("Teleport..."));
        panel.AddChild(BuildActionButton("Pay..."));
        panel.AddChild(BuildActionButton("Remove...", warn: true));
        panel.AddChild(BuildActionButton("Add..."));

        panel.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill }); // pushes the count to the bottom

        _countLabel = new Label { HorizontalAlignment = HorizontalAlignment.Right, Text = "Friends: 0" };
        _countLabel.AddThemeFontSizeOverride("font_size", ChatWindow.MetaFontSize);
        _countLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        panel.AddChild(_countLabel);

        return panel;
    }

    // Deliberately NOT using Button.Disabled here -- see the matching comment on
    // ChatWindow.BuildIconButton for why (it silently kills tooltip hover).
    private static Button BuildActionButton(string text, bool accent = false, bool warn = false)
    {
        var btn = new Button
        {
            Text = text,
            ClipText = true,
            TooltipText = $"{text.TrimEnd('.')} (not implemented)",
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0, 26),
        };
        btn.AddThemeFontSizeOverride("font_size", ChatWindow.LabelFontSize);

        var style = new StyleBoxFlat
        {
            BgColor = accent ? new Color(0.3f, 0.6f, 0.9f, 0.35f) : new Color(1, 1, 1, 0.05f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
        };
        btn.AddThemeStyleboxOverride("normal", style);
        btn.AddThemeColorOverride("font_color", warn ? new Color(0.95f, 0.45f, 0.45f, 0.95f) : new Color(0.9f, 0.9f, 0.9f));

        return btn;
    }
}
