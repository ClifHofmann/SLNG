using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
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
/// <para>FEAT-UI-35: five columns of rights, as in the reference viewer's list -- three the person grants (they may
/// see me online, find me on the map, edit my objects; clickable) and two the friend has granted (I may find them
/// on the map, edit their objects; read-only, only they can change them) -- and the login name stands behind the
/// Display Name instead of under it.</para>
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
    private CheckBox _onlyOnlineCheck = null!;
    private bool _onlyOnline;
    private ScrollContainer _scroll = null!;
    private Control _rightsHeaderSpacer = null!;
    private int _refreshQueued;
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
    internal void RefreshIcons() => RefreshSoon();

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
    private const int MoveCategoryUpItem = 2;
    private const int MoveCategoryDownItem = 3;

    /// <summary>What a drag of a category header carries, in front of the name: a drag from somewhere else (an
    /// inventory item, a conversation) must never be taken for one.</summary>
    private const string CategoryDragPrefix = "slng-friend-category:";

    /// <summary>Width of one rights column, and the gap between the person's own grants and the friend's.</summary>
    private const int RightsCellWidth = 32;
    private const int RightsGroupGap = 6;

    /// <summary>The rights columns, in order. <c>ByMe</c> columns are what the person grants (clickable); the others
    /// are what the friend granted (read-only). The first <see cref="RightsByMeColumns"/> are the person's own.</summary>
    private readonly record struct RightsColumn(FriendPermissions Permission, bool ByMe, string LabelKey, string TipKey);

    private static readonly RightsColumn[] RightsColumns =
    {
        new(FriendPermissions.SeeOnline, true, "ui.friend_rights.online", "ui.friend_rights.tip_my_online"),
        new(FriendPermissions.SeeOnMap, true, "ui.friend_rights.map", "ui.friend_rights.tip_my_map"),
        new(FriendPermissions.ModifyObjects, true, "ui.friend_rights.edit", "ui.friend_rights.tip_my_objects"),
        new(FriendPermissions.SeeOnMap, false, "ui.friend_rights.map", "ui.friend_rights.tip_their_map"),
        new(FriendPermissions.ModifyObjects, false, "ui.friend_rights.edit", "ui.friend_rights.tip_their_objects"),
    };

    private const int RightsByMeColumns = 3;

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

        _onlyOnlineCheck = new CheckBox
        {
            Text = L10n.Tr("ui.friend_view.only_online"),
            TooltipText = L10n.Tr("ui.friend_view.only_online_tooltip"),
            FocusMode = FocusModeEnum.None,
        };
        _onlyOnlineCheck.AddThemeFontSizeOverride("font_size", ChatWindow.LabelFontSize);
        _onlyOnlineCheck.Toggled += on =>
        {
            _onlyOnline = on;
            if (_agentId.Length > 0) FriendCategoryStore.SaveOnlyOnline(_agentId, on);
            Refresh();
        };
        leftVBox.AddChild(_onlyOnlineCheck);

        _emptyLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Visible = false,
        };
        _emptyLabel.AddThemeFontSizeOverride("font_size", ChatWindow.BodyFontSize);
        _emptyLabel.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        leftVBox.AddChild(_emptyLabel);

        leftVBox.AddChild(BuildRightsHeader());

        _scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        leftVBox.AddChild(_scroll);
        // The header sits outside the scroll area so it stays put; the rows lose the width of the scrollbar when it
        // shows, so the header gives up the same width and the columns stay under their labels.
        _scroll.GetVScrollBar().VisibilityChanged += UpdateRightsHeaderSpacer;
        UpdateRightsHeaderSpacer();

        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 2);
        _scroll.AddChild(_list);

        hbox.AddChild(BuildActionPanel());

        _categoryMenu = new PopupMenu();
        _categoryMenu.IdPressed += OnCategoryMenuIdPressed;
        AddChild(_categoryMenu);

        _sectionMenu = new PopupMenu();
        _sectionMenu.AddItem(L10n.Tr("ui.friend_category.rename"), RenameCategoryItem);
        _sectionMenu.AddItem(L10n.Tr("ui.friend_category.delete"), DeleteCategoryItem);
        _sectionMenu.AddSeparator();
        _sectionMenu.AddItem(L10n.Tr("ui.friend_category.move_up"), MoveCategoryUpItem);
        _sectionMenu.AddItem(L10n.Tr("ui.friend_category.move_down"), MoveCategoryDownItem);
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
        _onlyOnline = FriendCategoryStore.LoadOnlyOnline(_agentId);
        _onlyOnlineCheck.SetPressedNoSignal(_onlyOnline);
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

    private void OnFriendStatusChanged(object? sender, FriendStatusEvent e) => RefreshSoon();

    // BUG-NET-28: a friendship accepted (by us or by them) adds a row no presence event announces.
    private void OnFriendListChanged(object? sender, EventArgs e) => RefreshSoon();

    // A name resolving could be for anything (object owner, group, ...) -- Refresh() is a cheap
    // full rebuild from GetFriends(), so there's no need to filter to friend ids here.
    private void OnNameResolved(object? sender, NameResolvedEvent e) => RefreshSoon();

    // Many events arrive in a burst (a name per friend at login, a picture per friend), and a rebuild now creates a
    // handful of controls per friend, so they are folded into one rebuild at the end of the frame. Safe to call from
    // a network thread: only the flag and CallDeferred are touched.
    private void RefreshSoon()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0) CallDeferred(nameof(RefreshQueued));
    }

    private void RefreshQueued()
    {
        Interlocked.Exchange(ref _refreshQueued, 0);
        Refresh();
    }

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
        if (_onlyOnline) visible = visible.Where(f => f.IsOnline).ToList();

        if (friends.Count == 0)
        {
            _emptyLabel.Text = "No friends yet. Add friends in-world to see them here.";
            _emptyLabel.Visible = true;
        }
        else if (visible.Count == 0)
        {
            _emptyLabel.Text = _onlyOnline && string.IsNullOrEmpty(filterText)
                ? L10n.Tr("ui.friend_view.none_online")
                : "No friends match your filter.";
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
        // a folded block would otherwise look like no match at all. "Only online" hides the blocks nobody in is
        // online, but leaves the fold state alone: it is not a search. No category yet = no headers, a flat list.
        bool filtering = !string.IsNullOrEmpty(filterText);
        bool categorised = _book.Categories.Count > 0;
        foreach (var section in _book.Group(sorted, hideEmpty: filtering || _onlyOnline, ignoreCollapsed: filtering))
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
    /// right-click (on a real category, not the "no category" block) renames, deletes or moves it; dragging it onto
    /// another header moves it there. The "no category" header cannot be dragged but is a place to drop: that
    /// block is always last, so a category dropped on it goes to the end.</summary>
    private Control BuildSectionHeader(FriendSection section, bool filtering)
    {
        string name = section.Category ?? L10n.Tr("ui.friend_category.uncategorized");
        int online = section.Friends.Count(f => f.IsOnline);

        var button = new DragSortButton
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

        button.DragData = category == null ? null : CategoryDragPrefix + category;
        button.DragPreviewText = name;
        button.CanDrop = data => data.StartsWith(CategoryDragPrefix, StringComparison.Ordinal)
                                 && data != CategoryDragPrefix + category;
        button.Dropped = data => MoveCategory(data[CategoryDragPrefix.Length..], category);

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
                ShowSectionMenu(category);
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

    /// <summary>Opens a category's menu at the mouse. "Move up" is off for the first category and "Move down" for the
    /// last: the menu is the way to reorder without dragging, which a long list that scrolls makes hard.</summary>
    private void ShowSectionMenu(string category)
    {
        _menuCategory = category;
        int index = _book.IndexOf(category);
        _sectionMenu.SetItemDisabled(_sectionMenu.GetItemIndex(MoveCategoryUpItem), index <= 0);
        _sectionMenu.SetItemDisabled(_sectionMenu.GetItemIndex(MoveCategoryDownItem), index < 0 || index >= _book.Categories.Count - 1);

        _sectionMenu.Position = (Vector2I)GetGlobalMousePosition();
        _sectionMenu.Popup();
    }

    private void OnSectionMenuIdPressed(long id)
    {
        if (_menuCategory is not { } category) return;
        if (id == RenameCategoryItem) PromptRenameCategory(category, taken: null);
        else if (id == DeleteCategoryItem) PromptDeleteCategory(category);
        else if (id == MoveCategoryUpItem) MoveCategory(category, _book.IndexOf(category) - 1);
        else if (id == MoveCategoryDownItem) MoveCategory(category, _book.IndexOf(category) + 1);
    }

    /// <summary>A category dragged onto another header takes the place that header has now: dropped upwards it ends
    /// up above it, dropped downwards below it -- the same rule the conversation list follows. The "no category"
    /// block is not in the list (it is always last), so a drop onto it means "to the end".</summary>
    private void MoveCategory(string category, string? target)
    {
        MoveCategory(category, target == null ? _book.Categories.Count - 1 : _book.IndexOf(target));
    }

    private void MoveCategory(string category, int index)
    {
        if (index < 0 || !_book.Move(category, index)) return;
        SaveBook();
        // Deferred: this runs inside a drop on a header that the rebuild frees, and a drag's source is one too.
        RefreshSoon();
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

        var friendId = friend.Id;
        var friendName = DisplayName(friend);
        var legacyName = friend.Name;
        bool differs = !string.IsNullOrEmpty(legacyName)
                       && !string.Equals(friendName, legacyName, StringComparison.OrdinalIgnoreCase);

        // The button is the click target -- select, double-click for the IM, right-click for the category menu --
        // and the text is drawn by a label inside it: the Display Name and, behind it, the login name muted are two
        // colours on one line, which a Button's own text cannot be. BUG-UI-20 put the login name under the Display
        // Name; it now stands behind it (unless the "show usernames" preference is off), and is always in the tooltip.
        var nameBtn = new Button
        {
            Flat = true,
            ClipContents = true,
            FocusMode = FocusModeEnum.None,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 22),
            TooltipText = differs ? legacyName : "",
        };
        nameBtn.AddChild(BuildNameText(friend.IsOnline, friendName, differs && NameDisplay.ShowUsernames() ? legacyName : null));
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
        inner.AddChild(nameBtn);
        inner.AddChild(BuildRightsCells(friend));

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

    private static RichTextLabel BuildNameText(bool online, string name, string? legacyName)
    {
        var label = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollActive = false,
            FitContent = false,
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipContents = true,
            MouseFilter = MouseFilterEnum.Ignore, // the button under it takes every click
            Text = BbEscape(name) + (legacyName == null ? "" : $"  [color=#8c8c8c][font_size={ChatWindow.BodyFontSize - 2}]({BbEscape(legacyName)})[/font_size][/color]"),
        };
        label.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        label.OffsetLeft = 6;
        label.OffsetTop = 2;
        label.AddThemeFontSizeOverride("normal_font_size", ChatWindow.BodyFontSize);
        label.AddThemeColorOverride("default_color",
            online ? new Color(0.92f, 0.92f, 0.92f) : new Color(0.62f, 0.62f, 0.62f));
        return label;
    }

    /// <summary>A name can hold anything a person typed, brackets included; unescaped, "[b]" would be read as markup.</summary>
    private static string BbEscape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            if (c == '[') sb.Append("[lb]");
            else if (c == ']') sb.Append("[rb]");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The labels above the rights columns: which side the group is about, and which right it is. Each has
    /// the full explanation as its tooltip. The columns line up with <see cref="BuildRightsCells"/> -- same widths,
    /// same gap -- and the filler on the left takes whatever the name column does not.</summary>
    private Control BuildRightsHeader()
    {
        var outer = new HBoxContainer();
        outer.AddThemeConstantOverride("separation", 0);
        outer.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        outer.AddChild(new Control { CustomMinimumSize = new Vector2(8, 0) }); // the row's gap before its cells

        var columns = new VBoxContainer();
        columns.AddThemeConstantOverride("separation", 0);
        outer.AddChild(columns);

        var groups = new HBoxContainer();
        groups.AddThemeConstantOverride("separation", 0);
        groups.AddChild(HeaderLabel(L10n.Tr("ui.friend_rights.group_friend"), RightsCellWidth * RightsByMeColumns, null));
        groups.AddChild(new Control { CustomMinimumSize = new Vector2(RightsGroupGap, 0) });
        groups.AddChild(HeaderLabel(L10n.Tr("ui.friend_rights.group_me"),
            RightsCellWidth * (RightsColumns.Length - RightsByMeColumns), null));
        columns.AddChild(groups);

        var labels = new HBoxContainer();
        labels.AddThemeConstantOverride("separation", 0);
        for (int i = 0; i < RightsColumns.Length; i++)
        {
            if (i == RightsByMeColumns) labels.AddChild(new Control { CustomMinimumSize = new Vector2(RightsGroupGap, 0) });
            labels.AddChild(HeaderLabel(L10n.Tr(RightsColumns[i].LabelKey), RightsCellWidth, L10n.Tr(RightsColumns[i].TipKey)));
        }
        columns.AddChild(labels);

        // A row's right margin (4), plus the scrollbar's width while it shows -- see UpdateRightsHeaderSpacer.
        _rightsHeaderSpacer = new Control();
        outer.AddChild(_rightsHeaderSpacer);
        return outer;
    }

    private static Label HeaderLabel(string text, int width, string? tooltip)
    {
        var label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            ClipText = true,
            CustomMinimumSize = new Vector2(width, 0),
            MouseFilter = MouseFilterEnum.Pass, // a label ignores the mouse by default, which also silences its tooltip
            TooltipText = tooltip ?? "",
        };
        label.AddThemeFontSizeOverride("font_size", ChatWindow.MetaFontSize);
        label.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        return label;
    }

    private void UpdateRightsHeaderSpacer()
    {
        var bar = _scroll.GetVScrollBar();
        _rightsHeaderSpacer.CustomMinimumSize = new Vector2(4 + (bar.Visible ? bar.GetCombinedMinimumSize().X : 0), 0);
    }

    /// <summary>One friend's five rights. The person's own three are boxes to click; the friend's two are shown the
    /// same way but dimmed, because only the friend can change them.</summary>
    private Control BuildRightsCells(FriendEntry friend)
    {
        var cells = new HBoxContainer();
        cells.AddThemeConstantOverride("separation", 0);

        for (int i = 0; i < RightsColumns.Length; i++)
        {
            if (i == RightsByMeColumns) cells.AddChild(new Control { CustomMinimumSize = new Vector2(RightsGroupGap, 0) });

            var column = RightsColumns[i];
            var granted = column.ByMe ? friend.GrantedByMe : friend.GrantedToMe;
            var box = new CheckBox
            {
                ButtonPressed = granted.HasFlag(column.Permission),
                Disabled = !column.ByMe,
                FocusMode = FocusModeEnum.None,
            };
            if (column.ByMe)
            {
                var friendId = friend.Id;
                var permission = column.Permission;
                string name = DisplayName(friend);
                box.Toggled += on => OnRightToggled(friendId, name, permission, on);
            }

            var holder = new CenterContainer { CustomMinimumSize = new Vector2(RightsCellWidth, 0) };
            holder.AddChild(box);
            cells.AddChild(holder);
        }
        return cells;
    }

    /// <summary>A box in the "friend may..." columns was clicked. Switching a right off, and the two harmless ones on,
    /// go straight out. Letting someone edit, delete and take the person's objects asks first -- the reference viewer
    /// does too -- and a "no" puts the box back.</summary>
    private void OnRightToggled(Guid friendId, string name, FriendPermissions permission, bool on)
    {
        if (permission != FriendPermissions.ModifyObjects || !on)
        {
            ApplyRight(friendId, permission, on);
            return;
        }

        var win = ShowWindow<ConfirmWindow>();
        win.Initialize(
            L10n.Tr("ui.friend_rights.grant_edit_title"),
            L10n.TrFormat("ui.friend_rights.grant_edit_prompt", name),
            L10n.Tr("ui.friend_rights.grant_edit_ok"),
            danger: true);
        win.Confirmed += () => ApplyRight(friendId, permission, true);
        win.Closed += RefreshSoon; // a no (or a dismissal) leaves the box as it was drawn, so draw it again
    }

    /// <summary>Sends the friend's whole set with one right changed -- built from what the session holds now, not
    /// from what the row was drawn with, so two quick clicks cannot undo each other.</summary>
    private void ApplyRight(Guid friendId, FriendPermissions permission, bool on)
    {
        var current = _session?.GetFriends().FirstOrDefault(f => f.Id == friendId);
        if (current == null || _session!.SetFriendPermissions(friendId, current.GrantedByMe.With(permission, on))) return;
        RefreshSoon(); // not sent (offline, or no longer a friend): show what is true
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
