using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using SLNG.Core;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// Friends tab content for <see cref="ChatWindow"/> (M5-3 Phase 1b): a presence list backed by
/// GridSession.GetFriends() / FriendsManager. Display-only for now -- double-click-to-IM (per
/// the spec) is deferred to Phase 1c alongside real IM send/receive, since opening a
/// conversation tab that can't actually transmit yet would be a confusing half-feature.
/// </summary>
public partial class FriendsPanel : Control
{
    private GridSession? _session;
    private VBoxContainer _list = null!;
    private Label _emptyLabel = null!;

    public override void _Ready()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        var vbox = new VBoxContainer();
        vbox.SetAnchorsPreset(LayoutPreset.FullRect);
        vbox.AddThemeConstantOverride("separation", 2);
        AddChild(vbox);

        _emptyLabel = new Label
        {
            Text = "No friends yet. Add friends in-world to see them here.",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Visible = false,
        };
        _emptyLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
        vbox.AddChild(_emptyLabel);

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        vbox.AddChild(scroll);

        _list = new VBoxContainer();
        _list.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_list);
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
        _emptyLabel.Visible = friends.Count == 0;

        // Online first, then alphabetical within each group -- standard SL/Firestorm behavior.
        var sorted = friends
            .OrderByDescending(f => f.IsOnline)
            .ThenBy(f => string.IsNullOrEmpty(f.Name) ? f.Id.ToString() : f.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var friend in sorted)
            _list.AddChild(BuildRow(friend));
    }

    private static Control BuildRow(FriendEntry friend)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);

        var dot = new ColorRect
        {
            CustomMinimumSize = new Vector2(8, 8),
            Color = friend.IsOnline ? new Color(0.3f, 0.85f, 0.3f) : new Color(0.4f, 0.4f, 0.4f),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        row.AddChild(dot);

        var name = new Label
        {
            Text = string.IsNullOrEmpty(friend.Name) ? friend.Id.ToString() : friend.Name,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        name.AddThemeColorOverride("font_color",
            friend.IsOnline ? new Color(0.9f, 0.9f, 0.9f) : new Color(0.55f, 0.55f, 0.55f));
        row.AddChild(name);

        return row;
    }
}
