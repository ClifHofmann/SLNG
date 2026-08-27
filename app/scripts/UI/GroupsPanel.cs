using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using SLNG.Core;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// Groups tab content for <see cref="ChatWindow"/> (M5-3 Phase 2): a filterable membership list
/// (left) plus a fixed action panel (right), deliberately the same two-pane shape as
/// <see cref="FriendsPanel"/> so the two "social" tabs read as siblings.
///
/// Each row is the spec's §4 layout — a 24px badge carrying the group's first initial (there is
/// no group-insignia asset path yet, and the id is carried in <see cref="GroupEntry.InsigniaId"/>
/// for when there is), the group name, and the agent's own title in that group as a muted
/// secondary line.
///
/// "Group Chat" and "Mute chat" are functional. Leave/Profile need net-layer work that does not
/// exist yet (GroupManager leave + a group profile window), so they stay disabled with a
/// "(not implemented)" tooltip rather than being silently omitted — same convention as
/// FriendsPanel's placeholders.
/// </summary>
public partial class GroupsPanel : Control
{
    private GridSession? _session;
    private LineEdit _filterEdit = null!;
    private VBoxContainer _list = null!;
    private Label _emptyLabel = null!;
    private Label _countLabel = null!;
    private Button _chatButton = null!;
    private Button _muteButton = null!;
    private Guid _selectedGroupId;
    private string _selectedGroupName = "";

    /// <summary>Wired by ChatWindow to its group-chat tab opener — fired by the "Group Chat"
    /// action button and by double-clicking a group row.</summary>
    public Action<Guid, string>? OnOpenGroupChatRequested;

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

        _filterEdit = new LineEdit { PlaceholderText = L10n.Tr("ui.groups.filter") };
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

        GroupMuteSettings.MuteChanged += OnMuteChanged;
    }

    public override void _ExitTree()
    {
        GroupMuteSettings.MuteChanged -= OnMuteChanged;
        if (_session != null) _session.GroupsUpdated -= OnGroupsUpdated;
        base._ExitTree();
    }

    /// <summary>(Re-)binds to a GridSession — safe to call again after a re-login, when Boot hands
    /// over a freshly constructed session (the old one is being disposed).</summary>
    public void Initialize(GridSession session)
    {
        if (_session != null) _session.GroupsUpdated -= OnGroupsUpdated;

        _session = session;
        _session.GroupsUpdated += OnGroupsUpdated;

        // Memberships are not pushed by the sim -- nothing arrives until we ask.
        _session.RequestGroups();
        Refresh();
    }

    private void OnGroupsUpdated(object? sender, GroupsUpdatedEvent e) => CallDeferred(nameof(Refresh));

    private void OnMuteChanged(Guid groupId, bool muted)
    {
        if (groupId == _selectedGroupId) UpdateMuteButton();
        CallDeferred(nameof(Refresh));
    }

    private void Refresh()
    {
        foreach (Node child in _list.GetChildren())
        {
            _list.RemoveChild(child);
            child.QueueFree();
        }

        var groups = _session?.GetGroups() ?? (IReadOnlyList<GroupEntry>)Array.Empty<GroupEntry>();
        _countLabel.Text = L10n.TrFormat("ui.groups.count", groups.Count);

        string filterText = _filterEdit.Text.Trim();
        var visible = string.IsNullOrEmpty(filterText)
            ? groups
            : groups.Where(g => g.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase)).ToList();

        if (groups.Count == 0)
        {
            _emptyLabel.Text = L10n.Tr("ui.groups.empty");
            _emptyLabel.Visible = true;
        }
        else if (visible.Count == 0)
        {
            _emptyLabel.Text = L10n.Tr("ui.groups.no_match");
            _emptyLabel.Visible = true;
        }
        else
        {
            _emptyLabel.Visible = false;
        }

        // GetGroups() already sorts by name; the filter preserves that order.
        foreach (var group in visible)
            _list.AddChild(BuildRow(group));

        UpdateMuteButton();
    }

    private Control BuildRow(GroupEntry group)
    {
        var row = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };

        var inner = new HBoxContainer();
        inner.AddThemeConstantOverride("separation", 8);
        row.AddChild(inner);

        inner.AddChild(BuildBadge(group));

        var textCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        textCol.AddThemeConstantOverride("separation", 0);
        inner.AddChild(textCol);

        var groupId = group.Id;
        var groupName = string.IsNullOrEmpty(group.Name) ? group.Id.ToString() : group.Name;

        var nameBtn = new Button
        {
            Text = GroupMuteSettings.IsMuted(groupId) ? $"🔇  {groupName}" : groupName,
            Flat = true,
            ClipText = true,
            FocusMode = FocusModeEnum.None,
            Alignment = HorizontalAlignment.Left,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        nameBtn.AddThemeFontSizeOverride("font_size", ChatWindow.BodyFontSize);
        nameBtn.AddThemeColorOverride("font_color",
            GroupMuteSettings.IsMuted(groupId) ? new Color(0.62f, 0.62f, 0.62f) : new Color(0.92f, 0.92f, 0.92f));
        nameBtn.Pressed += () => { _selectedGroupId = groupId; _selectedGroupName = groupName; Refresh(); };
        // Double-click opens the group's chat directly, mirroring FriendsPanel's double-click-to-IM.
        nameBtn.GuiInput += (@event) =>
        {
            if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, DoubleClick: true })
                OnOpenGroupChatRequested?.Invoke(groupId, groupName);
        };
        textCol.AddChild(nameBtn);

        if (!string.IsNullOrWhiteSpace(group.MemberTitle))
        {
            var title = new Label { Text = group.MemberTitle, ClipText = true };
            title.AddThemeFontSizeOverride("font_size", ChatWindow.MetaFontSize);
            title.AddThemeColorOverride("font_color", new Color(0.55f, 0.55f, 0.55f));
            textCol.AddChild(title);
        }

        var style = new StyleBoxFlat
        {
            BgColor = group.Id == _selectedGroupId ? new Color(0.3f, 0.6f, 0.9f, 0.25f) : new Color(0, 0, 0, 0),
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

    /// <summary>Spec §4: a 24px badge with the group's first initial, standing in for the
    /// insignia texture until there is an asset path for it. Colour is derived from the group id
    /// so the same group always gets the same badge, which makes a long list scannable.</summary>
    private static Control BuildBadge(GroupEntry group)
    {
        var hue = Math.Abs(group.Id.GetHashCode() % 360) / 360f;
        var panel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(24, 24),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = Color.FromHsv(hue, 0.45f, 0.55f),
            CornerRadiusTopLeft = 12,
            CornerRadiusTopRight = 12,
            CornerRadiusBottomLeft = 12,
            CornerRadiusBottomRight = 12,
        });

        string initial = string.IsNullOrWhiteSpace(group.Name) ? "?" : group.Name.Trim()[..1].ToUpperInvariant();
        var label = new Label
        {
            Text = initial,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.AddThemeFontSizeOverride("font_size", ChatWindow.LabelFontSize);
        label.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.95f));
        panel.AddChild(label);
        return panel;
    }

    private Control BuildActionPanel()
    {
        var panel = new VBoxContainer { CustomMinimumSize = new Vector2(92, 0) };
        panel.AddThemeConstantOverride("separation", 4);

        _chatButton = BuildActionButton(L10n.Tr("ui.groups.action_chat"), accent: true);
        _chatButton.TooltipText = L10n.Tr("ui.groups.action_chat_tooltip");
        _chatButton.Pressed += () =>
        {
            if (_selectedGroupId != Guid.Empty)
                OnOpenGroupChatRequested?.Invoke(_selectedGroupId, _selectedGroupName);
        };
        panel.AddChild(_chatButton);

        _muteButton = BuildActionButton(L10n.Tr("ui.groups.action_mute"));
        _muteButton.TooltipText = L10n.Tr("ui.groups.action_mute_tooltip");
        _muteButton.Pressed += () =>
        {
            if (_selectedGroupId == Guid.Empty) return;
            GroupMuteSettings.SetMuted(_selectedGroupId, !GroupMuteSettings.IsMuted(_selectedGroupId));
        };
        panel.AddChild(_muteButton);

        panel.AddChild(BuildPlaceholderButton(L10n.Tr("ui.groups.action_profile")));
        panel.AddChild(BuildPlaceholderButton(L10n.Tr("ui.groups.action_leave"), warn: true));

        panel.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill }); // pushes the count down

        _countLabel = new Label { HorizontalAlignment = HorizontalAlignment.Right, Text = L10n.TrFormat("ui.groups.count", 0) };
        _countLabel.AddThemeFontSizeOverride("font_size", ChatWindow.MetaFontSize);
        _countLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        panel.AddChild(_countLabel);

        return panel;
    }

    private void UpdateMuteButton()
    {
        if (_muteButton == null) return;
        bool muted = _selectedGroupId != Guid.Empty && GroupMuteSettings.IsMuted(_selectedGroupId);
        _muteButton.Text = muted ? L10n.Tr("ui.groups.action_unmute") : L10n.Tr("ui.groups.action_mute");
    }

    private static Button BuildActionButton(string text, bool accent = false)
    {
        var btn = new Button
        {
            Text = text,
            ClipText = true,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0, 26),
        };
        btn.AddThemeFontSizeOverride("font_size", ChatWindow.LabelFontSize);
        btn.AddThemeStyleboxOverride("normal", new StyleBoxFlat
        {
            BgColor = accent ? new Color(0.3f, 0.6f, 0.9f, 0.35f) : new Color(1, 1, 1, 0.05f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
        });
        btn.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.9f));
        return btn;
    }

    // Deliberately NOT using Button.Disabled -- see FriendsPanel.BuildActionButton for why it
    // silently kills tooltip hover, which is the only thing explaining the button.
    private static Button BuildPlaceholderButton(string text, bool warn = false)
    {
        var btn = BuildActionButton(text);
        btn.TooltipText = L10n.TrFormat("ui.groups.not_implemented", text.TrimEnd('.', '…'));
        if (warn) btn.AddThemeColorOverride("font_color", new Color(0.95f, 0.45f, 0.45f, 0.95f));
        return btn;
    }
}
