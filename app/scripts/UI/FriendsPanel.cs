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
/// Teleport and Pay were placeholders here from the days when the net layer had neither;
/// both exist now (GridSession.OfferTeleport, MVP5-2's PayAvatarWindow) and are wired. Remove
/// is wired too (it asks first). Add is not and keeps the "(not implemented)" tooltip rather than
/// being silently omitted.
/// <para>FEAT-UI-65: the person can file friends into categories of their own and fold them up. The list is
/// flat until the first category exists. The filing lives in <see cref="FriendCategoryBook"/>, saved per account
/// by <see cref="FriendCategoryStore"/>.</para>
/// </summary>
public partial class FriendsPanel : Control
{
    private GridSession? _session;
    private LineEdit _filterEdit = null!;
    private VBoxContainer _list = null!;
    private Label _emptyLabel = null!;
    private Label _countLabel = null!;
    private FriendCategoryBook _book = new();
    private string _agentId = "";
    private PopupMenu _categoryMenu = null!;
    private PopupMenu _sectionMenu = null!;
    // What the open menu acts on, and the categories its items stand for (item id - 1).
    private Guid _menuFriendId;
    private string? _menuCategory;
    private List<string> _menuCategories = new();
    private Guid? _selectedFriendId;
    private string _selectedFriendName = "";
    // The legacy name of the same friend: an IM tab and its log file are named after it, never after a Display Name.
    private string _selectedFriendLegacyName = "";

    /// <summary>Wired by ChatWindow to ChatWindow.OpenOrFocusImTab -- fired by the "IM / Call"
    /// action button and by double-clicking a friend row.</summary>
    public Action<Guid, string>? OnOpenImRequested;

    /// <summary>Wired by ChatWindow: the friend's profile picture, or null while it has none. The row shows the
    /// generic person symbol until then, as the reference viewer's avatar icon does.</summary>
    public Func<Guid, Texture2D?>? IconFor;

    /// <summary>Redraws the rows, e.g. because a profile picture arrived.</summary>
    internal void RefreshIcons() => Refresh();

    /// <summary>FEAT-UI-13: wired (through ChatWindow) to Boot's profile-window opener -- fired by
    /// the "Profile" action button.</summary>
    public Action<Guid, string>? OnOpenProfileRequested;

    /// <summary>MVP5-2: the "Pay..." action. The button sat here unwired long enough that it read
    /// as broken rather than unbuilt.</summary>
    public Action<Guid, string>? OnPayRequested;

    /// <summary>Offers this friend a teleport to where we are standing.</summary>
    public Action<Guid, string>? OnOfferTeleportRequested;

    // Menu item ids. A category's item is its index in _menuCategories plus CategoryItemBase.
    private const int NoCategoryItem = 0;
    private const int CategoryItemBase = 1;
    private const int NewCategoryItem = 100000;
    private const int RenameCategoryItem = 0;
    private const int DeleteCategoryItem = 1;

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

        var filterRow = new HBoxContainer();
        filterRow.AddThemeConstantOverride("separation", 4);
        leftVBox.AddChild(filterRow);

        _filterEdit = new LineEdit { PlaceholderText = "Filter friends...", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _filterEdit.AddThemeFontSizeOverride("font_size", ChatWindow.BodyFontSize);
        _filterEdit.TextChanged += (_) => Refresh();
        filterRow.AddChild(_filterEdit);

        var newCategoryButton = new Button
        {
            Text = "+",
            TooltipText = L10n.Tr("ui.friend_category.new_tooltip"),
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(28, 0),
        };
        newCategoryButton.AddThemeFontSizeOverride("font_size", ChatWindow.BodyFontSize);
        newCategoryButton.Pressed += () => PromptNewCategory(null);
        filterRow.AddChild(newCategoryButton);

        _emptyLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Visible = false,
        };
        _emptyLabel.AddThemeFontSizeOverride("font_size", ChatWindow.BodyFontSize);
        _emptyLabel.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
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

        _categoryMenu = new PopupMenu();
        _categoryMenu.IdPressed += OnCategoryMenuIdPressed;
        AddChild(_categoryMenu);

        _sectionMenu = new PopupMenu();
        _sectionMenu.AddItem(L10n.Tr("ui.friend_category.rename"), RenameCategoryItem);
        _sectionMenu.AddItem(L10n.Tr("ui.friend_category.delete"), DeleteCategoryItem);
        _sectionMenu.IdPressed += OnSectionMenuIdPressed;
        AddChild(_sectionMenu);
    }

    /// <summary>(Re-)binds to a GridSession -- safe to call again after a re-login, when Boot
    /// hands over a freshly constructed session (the old one is being disposed).</summary>
    public void Initialize(GridSession session)
    {
        if (_session != null)
        {
            _session.FriendStatusChanged -= OnFriendStatusChanged;
            _session.FriendListChanged -= OnFriendListChanged;
            _session.NameResolved -= OnNameResolved;
            _session.DisplayNameResolved -= OnNameResolved;
        }

        _session = session;
        _agentId = session.AgentId;
        _book = FriendCategoryStore.Load(_agentId);
        _session.FriendStatusChanged += OnFriendStatusChanged;
        _session.FriendListChanged += OnFriendListChanged;
        _session.NameResolved += OnNameResolved;
        _session.DisplayNameResolved += OnNameResolved; // the list shows Display Names

        Refresh();
    }

    // Asks first: ending a friendship is mirrored on the other side and cannot be taken back with
    // one click. The window lives on the HUD layer, not under this panel, so closing the chat window
    // does not take the question with it.
    private void PromptRemoveFriend(Guid friendId, string name)
    {
        if (_session == null) return;
        var win = ShowWindow<ConfirmWindow>();
        win.Initialize(
            L10n.Tr("ui.friend_remove.title"),
            L10n.TrFormat("ui.friend_remove.prompt", name),
            L10n.Tr("ui.friend_remove.ok"),
            danger: true);
        win.Confirmed += () =>
        {
            if (_selectedFriendId == friendId) _selectedFriendId = null;
            _session?.RemoveFriend(friendId);
            // The filing is ours, not the grid's: without this the id would sit in the file for good.
            if (_book.Assign(friendId, null)) SaveBook();
        };
    }

    /// <summary>Puts a window on the HUD layer, which is where every floating window in this client lives --
    /// parenting it to the panel would take it away when the chat window closes.</summary>
    private T ShowWindow<T>() where T : SLNGWindow, new()
    {
        var win = new T();
        var host = GetTree()?.Root?.GetNodeOrNull<CanvasLayer>("Boot/HudLayer")
                   ?? (Node?)GetParent() ?? this;
        host.AddChild(win);
        return win;
    }

    private void OnFriendStatusChanged(object? sender, FriendStatusEvent e) => CallDeferred(nameof(Refresh));

    // BUG-NET-28: a friendship accepted (by us or by them) adds a row no presence event announces.
    private void OnFriendListChanged(object? sender, EventArgs e) => CallDeferred(nameof(Refresh));

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
            : friends.Where(f => DisplayName(f).Contains(filterText, StringComparison.OrdinalIgnoreCase)
                          || f.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase)).ToList();

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

        // FEAT-UI-65: with a filter typed, show only the blocks that have a hit and show them open -- a match in
        // a folded block would otherwise look like no match at all. No category yet = no headers, a flat list.
        bool filtering = !string.IsNullOrEmpty(filterText);
        bool categorised = _book.Categories.Count > 0;
        foreach (var section in _book.Group(sorted, hideEmpty: filtering, ignoreCollapsed: filtering))
        {
            if (categorised) _list.AddChild(BuildSectionHeader(section, filtering));
            if (section.Collapsed) continue;
            foreach (var friend in section.Friends)
                _list.AddChild(BuildRow(friend));
        }
    }

    // No account yet (nobody is logged in) means no section to write to.
    private void SaveBook()
    {
        if (_agentId.Length > 0) FriendCategoryStore.Save(_agentId, _book);
    }

    /// <summary>The line above a block of friends: fold arrow, name and "online/total". Click folds or opens it;
    /// right-click (on a real category, not the "no category" block) renames or deletes it.</summary>
    private Control BuildSectionHeader(FriendSection section, bool filtering)
    {
        string name = section.Category ?? L10n.Tr("ui.friend_category.uncategorized");
        int online = section.Friends.Count(f => f.IsOnline);

        var button = new Button
        {
            Text = $"{(section.Collapsed ? "▶" : "▼")}  {name}  ({online}/{section.Friends.Count})",
            Flat = true,
            ClipText = true,
            FocusMode = FocusModeEnum.None,
            Alignment = HorizontalAlignment.Left,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = section.Category == null ? "" : L10n.Tr("ui.friend_category.header_tooltip"),
        };
        button.AddThemeFontSizeOverride("font_size", ChatWindow.LabelFontSize);
        button.AddThemeColorOverride("font_color", new Color(0.82f, 0.9f, 1f));

        var category = section.Category;
        bool collapsed = section.Collapsed;
        button.Pressed += () =>
        {
            if (filtering) return; // everything is open while a filter is typed; the saved fold state waits
            if (category != null) _book.SetCollapsed(category, !collapsed);
            else _book.UncategorizedCollapsed = !collapsed;
            SaveBook();
            Refresh();
        };
        button.GuiInput += (@event) =>
        {
            if (category != null && @event is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
            {
                _menuCategory = category;
                _sectionMenu.Position = (Vector2I)GetGlobalMousePosition();
                _sectionMenu.Popup();
            }
        };

        var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(1, 1, 1, 0.07f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = 4,
            ContentMarginRight = 4,
        });
        panel.AddChild(button);
        return panel;
    }

    private void SelectFriend(Guid friendId, string displayName, string legacyName)
    {
        _selectedFriendId = friendId;
        _selectedFriendName = displayName;
        _selectedFriendLegacyName = legacyName;
        Refresh();
    }

    /// <summary>Opens the "move to category" menu for a friend at the mouse: "no category", one entry per category
    /// (the current one ticked), and "new category...".</summary>
    private void ShowCategoryMenu(Guid friendId)
    {
        _menuFriendId = friendId;
        _menuCategories = _book.Categories.ToList();
        string? current = _book.CategoryOf(friendId);

        _categoryMenu.Clear();
        _categoryMenu.AddRadioCheckItem(L10n.Tr("ui.friend_category.none"), NoCategoryItem);
        _categoryMenu.SetItemChecked(_categoryMenu.GetItemIndex(NoCategoryItem), current == null);
        for (int i = 0; i < _menuCategories.Count; i++)
        {
            _categoryMenu.AddRadioCheckItem(_menuCategories[i], CategoryItemBase + i);
            _categoryMenu.SetItemChecked(_categoryMenu.GetItemIndex(CategoryItemBase + i), _menuCategories[i] == current);
        }
        _categoryMenu.AddSeparator();
        _categoryMenu.AddItem(L10n.Tr("ui.friend_category.new"), NewCategoryItem);

        _categoryMenu.Position = (Vector2I)GetGlobalMousePosition();
        _categoryMenu.Popup();
    }

    private void OnCategoryMenuIdPressed(long id)
    {
        if (id == NewCategoryItem)
        {
            PromptNewCategory(_menuFriendId);
            return;
        }

        string? category = null;
        if (id != NoCategoryItem)
        {
            int index = (int)id - CategoryItemBase;
            if (index < 0 || index >= _menuCategories.Count) return;
            category = _menuCategories[index];
        }
        if (!_book.Assign(_menuFriendId, category)) return;
        SaveBook();
        Refresh();
    }

    private void OnSectionMenuIdPressed(long id)
    {
        if (_menuCategory is not { } category) return;
        if (id == RenameCategoryItem) PromptRenameCategory(category, taken: null);
        else if (id == DeleteCategoryItem) PromptDeleteCategory(category);
    }

    /// <summary>Asks for a name and creates the category; with <paramref name="assignTo"/> the friend goes into it
    /// straight away (the "new category..." entry of a friend's menu). A name that already exists is taken to mean
    /// that category rather than refused.</summary>
    private void PromptNewCategory(Guid? assignTo)
    {
        var win = ShowWindow<TextPromptWindow>();
        win.Initialize(
            L10n.Tr("ui.friend_category.new_title"),
            L10n.Tr("ui.friend_category.new_prompt"),
            "",
            L10n.Tr("ui.friend_category.create"));
        win.Confirmed += name =>
        {
            string? category = _book.Add(name) ?? _book.Find(name);
            if (category == null) return;
            if (assignTo is { } friend) _book.Assign(friend, category);
            SaveBook();
            Refresh();
        };
    }

    /// <summary>Asks for a new name. One that belongs to another category is not silently dropped: the prompt comes
    /// back saying so (<paramref name="taken"/> is the name that clashed).</summary>
    private void PromptRenameCategory(string category, string? taken)
    {
        var win = ShowWindow<TextPromptWindow>();
        win.Initialize(
            L10n.Tr("ui.friend_category.rename_title"),
            taken == null
                ? L10n.TrFormat("ui.friend_category.rename_prompt", category)
                : L10n.TrFormat("ui.friend_category.rename_taken", taken, category),
            taken ?? category,
            L10n.Tr("ui.friend_category.rename_ok"));
        win.Confirmed += name =>
        {
            if (_book.Rename(category, name))
            {
                SaveBook();
                Refresh();
            }
            else if (_book.Find(name) is { } clash && !string.Equals(clash, category, StringComparison.Ordinal))
            {
                PromptRenameCategory(category, clash);
            }
        };
    }

    private void PromptDeleteCategory(string category)
    {
        var win = ShowWindow<ConfirmWindow>();
        win.Initialize(
            L10n.Tr("ui.friend_category.delete_title"),
            L10n.TrFormat("ui.friend_category.delete_prompt", category),
            L10n.Tr("ui.friend_category.delete_ok"),
            danger: true);
        win.Confirmed += () =>
        {
            if (!_book.Remove(category)) return;
            SaveBook();
            Refresh();
        };
    }

    /// <summary>The name shown for a friend: their Display Name when they have one (and the preference is
    /// on), else the legacy name. Only what is shown -- IMs and logs keep the legacy name.</summary>
    private string DisplayName(FriendEntry friend) =>
        string.IsNullOrEmpty(friend.Name) ? friend.Id.ToString() : NameDisplay.For(_session, friend.Id, friend.Name);

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

        inner.AddChild(new TextureRect
        {
            Texture = IconFor?.Invoke(friend.Id) ?? AvatarIcons.Placeholder,
            CustomMinimumSize = new Vector2(AvatarIcons.IconSize, AvatarIcons.IconSize),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
        });

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
        var legacyName = friend.Name;
        nameBtn.Pressed += () => SelectFriend(friendId, friendName, legacyName);
        // Double-clicking a friend opens their IM directly (per the M5-3 spec), rather than
        // requiring a select-then-click-"IM / Call" round trip. Right-click files them into a category.
        nameBtn.GuiInput += (@event) =>
        {
            if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, DoubleClick: true })
                OnOpenImRequested?.Invoke(friendId, legacyName);
            else if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
            {
                SelectFriend(friendId, friendName, legacyName);
                ShowCategoryMenu(friendId);
            }
        };

        // BUG-UI-20: with a Display Name on show, the login name stays visible the way the nametag does it
        // -- muted, in brackets, under it -- unless the "show usernames" preference is off; it is always
        // in the tooltip.
        var textCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        textCol.AddThemeConstantOverride("separation", 0);
        inner.AddChild(textCol);
        textCol.AddChild(nameBtn);
        if (!string.IsNullOrEmpty(legacyName) && !string.Equals(friendName, legacyName, StringComparison.OrdinalIgnoreCase))
        {
            nameBtn.TooltipText = legacyName;
            if (NameDisplay.ShowUsernames())
            {
                var legacyLabel = new Label { Text = $"({legacyName})", ClipText = true };
                legacyLabel.AddThemeFontSizeOverride("font_size", ChatWindow.MetaFontSize);
                legacyLabel.AddThemeColorOverride("font_color", new Color(0.55f, 0.55f, 0.55f));
                textCol.AddChild(legacyLabel);
            }
        }

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
            if (_selectedFriendId is { } id) OnOpenImRequested?.Invoke(id, _selectedFriendLegacyName);
        };
        panel.AddChild(imButton);

        var profileButton = BuildActionButton("Profile");
        profileButton.TooltipText = "Open this avatar's profile";
        profileButton.Pressed += () =>
        {
            if (_selectedFriendId is { } id) OnOpenProfileRequested?.Invoke(id, _selectedFriendName);
        };
        panel.AddChild(profileButton);

        var teleportButton = BuildActionButton("Teleport...", implemented: true);
        teleportButton.TooltipText = "Offer this friend a teleport to your location";
        teleportButton.Pressed += () =>
        {
            if (_selectedFriendId is { } id) OnOfferTeleportRequested?.Invoke(id, _selectedFriendName);
        };
        panel.AddChild(teleportButton);

        var payButton = BuildActionButton("Pay...", implemented: true);
        payButton.TooltipText = "Send this friend L$";
        payButton.Pressed += () =>
        {
            if (_selectedFriendId is { } id) OnPayRequested?.Invoke(id, _selectedFriendName);
        };
        panel.AddChild(payButton);

        var categoryButton = BuildActionButton(L10n.Tr("ui.friend_category.button"), implemented: true);
        categoryButton.TooltipText = L10n.Tr("ui.friend_category.button_tooltip");
        categoryButton.Pressed += () =>
        {
            if (_selectedFriendId is { } id) ShowCategoryMenu(id);
        };
        panel.AddChild(categoryButton);

        var removeButton = BuildActionButton("Remove...", warn: true, implemented: true);
        removeButton.TooltipText = "Remove this friend";
        removeButton.Pressed += () =>
        {
            if (_selectedFriendId is { } id) PromptRemoveFriend(id, _selectedFriendName);
        };
        panel.AddChild(removeButton);
        panel.AddChild(BuildActionButton("Add..."));

        panel.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill }); // pushes the count to the bottom

        _countLabel = new Label { HorizontalAlignment = HorizontalAlignment.Right, Text = "Friends: 0" };
        _countLabel.AddThemeFontSizeOverride("font_size", ChatWindow.MetaFontSize);
        _countLabel.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        panel.AddChild(_countLabel);

        return panel;
    }

    // Deliberately NOT using Button.Disabled here -- see the matching comment on
    // ChatWindow.BuildIconButton for why (it silently kills tooltip hover).
    private static Button BuildActionButton(string text, bool accent = false, bool warn = false, bool implemented = false)
    {
        var btn = new Button
        {
            Text = text,
            ClipText = true,
            // The caller replaces this for anything that works. A button that does nothing must
            // SAY it does nothing -- silence reads as a bug, which is how this one was reported.
            TooltipText = implemented ? text.TrimEnd('.') : $"{text.TrimEnd('.')} (not implemented)",
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
