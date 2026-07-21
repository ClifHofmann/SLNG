using Godot;
using System;
using System.Collections.Generic;
using SLNG.Core.Services;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// Unified communication window (M5-3): dual-axis tabs -- an outer horizontal Chat/Friends/Groups
/// strip along the top (a classic top tab bar) and, inside the Chat page, an inner vertical
/// sidebar list of conversations (a static "Main" local-chat entry plus dynamic per-avatar IM
/// entries added later by Phase 1c), with the message log to its right.
///
/// Phase 1 (this pass) wires up the shell and migrates local chat off the old inline
/// ChatBox/LogPanel row in Boot.tscn. Friends/Groups are placeholder pages until their net-layer
/// tasks (FriendsManager / GroupManager) land -- see docs/specs/M5-3-tabbed-chat-window.md §6.
/// </summary>
public partial class ChatWindow : SLNGWindow
{
    private const int MaxLogLines = 200;
    private const int UnreadCap = 9;

    private readonly List<(Button TabButton, Control Page)> _outerTabs = new();
    private HBoxContainer _outerTabStrip = null!;
    private Control _outerPageHost = null!;

    private VBoxContainer _conversationList = null!;
    private RichTextLabel _logView = null!;
    private Button _jumpToLatestButton = null!;
    private LineEdit _inputEdit = null!;
    private Button _sendButton = null!;
    private Font _iconFont = null!;
    private FriendsPanel _friendsPanel = null!;

    private sealed class ChatTab
    {
        public string Id = "";
        public string DisplayName = "";
        public PanelContainer RowPanel = null!;
        public Label UnreadLabel = null!;
        public ChatLogKind LogKind;
        public bool Closeable;
        public readonly List<string> Lines = new();
        public int UnreadCount;
        public bool FollowingBottom = true;
    }

    private readonly List<ChatTab> _chatTabs = new();
    private ChatTab? _activeChatTab;

    private ChatLogger _logger = null!;

    /// <summary>Wired by Boot to GridSession.SendChat -- the Main tab's send path. IM tabs get
    /// their own send routing once Phase 1c's net plumbing exists.</summary>
    public Action<string>? OnSendLocalChat;

    public void Initialize(ChatLogger logger)
    {
        _logger = logger;
    }

    /// <summary>Called by Boot after each successful login (session is a fresh instance per
    /// login, unlike ChatLogger/OnSendLocalChat which are wired once). Currently only the
    /// Friends tab needs it; Groups/IM will call in here too once their net plumbing lands.</summary>
    public void BindSession(GridSession session)
    {
        _friendsPanel.Initialize(session);
    }

    public override void _Ready()
    {
        base._Ready(); // SLNGWindow styling

        _iconFont = GD.Load<Font>("res://assets/fonts/MaterialSymbolsOutlined.ttf");

        Title = "CHAT";
        Visible = false;
        CustomMinimumSize = new Vector2(380, 320);
        Size = new Vector2(460, 420);
        Position = new Vector2(16, 220);
        OnCloseRequested = Hide;

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 0);
        ContentContainer.AddChild(vbox);

        BuildOuterTabStrip(vbox);

        AddOuterTab("Chat", BuildChatPage());
        _friendsPanel = new FriendsPanel();
        AddOuterTab("Friends", _friendsPanel);
        AddOuterTab("Groups", BuildPlaceholderPage(
            "You haven't joined any groups yet.", "Group support is planned for a follow-up pass."));

        AddChatTab("main", "Main", ChatLogKind.Local, closeable: false);
        SelectChatTab(_chatTabs[0]);
    }

    public override void _Process(double delta)
    {
        // Detect the user scrolling away from (or back to) the bottom of the live log so we can
        // pause auto-follow while they're reading history, per the M5-3 UX decision -- there's no
        // scroll signal that only fires on user-driven movement, so this polls once a frame like
        // ButtonBar's own drag safety-net does.
        if (_activeChatTab == null) return;
        var vscroll = _logView.GetVScrollBar();
        if (vscroll == null || vscroll.MaxValue <= vscroll.Page) return;

        bool atBottom = vscroll.Value >= vscroll.MaxValue - vscroll.Page - 2.0;
        if (atBottom && !_activeChatTab.FollowingBottom)
        {
            _activeChatTab.FollowingBottom = true;
            ShowJumpToLatest(false);
        }
        else if (!atBottom && _activeChatTab.FollowingBottom)
        {
            _activeChatTab.FollowingBottom = false;
        }
    }

    /// <summary>Appends one local-chat line (Main tab) and persists it via the logger. Called by
    /// Boot on GridSession.ChatMessageReceived, marshalled to the main thread first.</summary>
    public void AppendLocalChatMessage(string sender, string message)
    {
        var tab = _chatTabs.Find(t => t.Id == "main");
        if (tab == null) return;

        var now = DateTime.Now;
        AppendLineToTab(tab, FormatChatLine(now, sender, message));
        _ = _logger.AppendAsync(ChatLogKind.Local, tab.DisplayName, sender, message, now);
    }

    private static string FormatChatLine(DateTime timestamp, string sender, string message) =>
        $"[color=#888888]{timestamp:HH:mm}[/color] [b]{BbEscape(sender)}[/b]: {BbEscape(message)}";

    private static string BbEscape(string s) => s.Replace("[", "[lb]");

    // ---- Chat page: vertical conversation list (left) + message log/input (right) ----------

    private Control BuildChatPage()
    {
        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 0);

        var listPanel = new PanelContainer { CustomMinimumSize = new Vector2(120, 0) };
        var listStyle = new StyleBoxFlat
        {
            BgColor = new Color(1, 1, 1, 0.03f),
            BorderWidthRight = 1,
            BorderColor = new Color(1, 1, 1, 0.08f),
            ContentMarginTop = 6,
            ContentMarginBottom = 6,
            ContentMarginLeft = 4,
            ContentMarginRight = 4,
        };
        listPanel.AddThemeStyleboxOverride("panel", listStyle);
        hbox.AddChild(listPanel);

        var listScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        listPanel.AddChild(listScroll);

        _conversationList = new VBoxContainer();
        _conversationList.AddThemeConstantOverride("separation", 2);
        listScroll.AddChild(_conversationList);

        var rightMargin = new MarginContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        rightMargin.AddThemeConstantOverride("margin_left", 10);
        rightMargin.AddThemeConstantOverride("margin_right", 6);
        rightMargin.AddThemeConstantOverride("margin_top", 6);
        rightMargin.AddThemeConstantOverride("margin_bottom", 6);
        hbox.AddChild(rightMargin);

        var rightVBox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        rightVBox.AddThemeConstantOverride("separation", 6);
        rightMargin.AddChild(rightVBox);

        rightVBox.AddChild(BuildActionIconRow());

        _logView = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollFollowing = false, // manual pause/follow control -- see _Process
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        rightVBox.AddChild(_logView);

        _jumpToLatestButton = new Button
        {
            Text = "▼ Jump to latest",
            Visible = false,
            FocusMode = FocusModeEnum.None,
        };
        _jumpToLatestButton.Pressed += () =>
        {
            if (_activeChatTab == null) return;
            _activeChatTab.FollowingBottom = true;
            ScrollLogToBottom();
            ShowJumpToLatest(false);
        };
        rightVBox.AddChild(_jumpToLatestButton);

        var inputRow = new HBoxContainer();
        inputRow.AddThemeConstantOverride("separation", 6);
        rightVBox.AddChild(inputRow);

        _inputEdit = new LineEdit
        {
            PlaceholderText = "Type your message here...",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _inputEdit.TextSubmitted += (_) => OnSendPressed();
        inputRow.AddChild(_inputEdit);

        _sendButton = new Button { Text = "Send", FocusMode = FocusModeEnum.None };
        _sendButton.Pressed += OnSendPressed;
        inputRow.AddChild(_sendButton);

        return hbox;
    }

    /// <summary>Per-conversation action icons above the message log. Only History is wired up
    /// today; Give Item / Voice Call / Search are placeholders captured from the M5-3 UX
    /// proposal §9 (Gemini Canvas mockup review) -- shown now with a "(not implemented)" tooltip
    /// so the intended affordance isn't lost, rather than added silently later.</summary>
    private Control BuildActionIconRow()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 2);

        row.AddChild(BuildIconButton("history", "History", OnHistoryPressed));
        row.AddChild(BuildIconButton("card_giftcard", "Give Item (not implemented)", null));
        row.AddChild(BuildIconButton("call", "Voice Call (not implemented)", null));
        row.AddChild(BuildIconButton("search", "Search (not implemented)", null));

        return row;
    }

    private Button BuildIconButton(string glyph, string tooltip, Action? onPressed)
    {
        var btn = new Button
        {
            Text = glyph,
            Flat = true,
            FocusMode = FocusModeEnum.None,
            TooltipText = tooltip,
            Disabled = onPressed == null,
            CustomMinimumSize = new Vector2(28, 28),
        };
        btn.AddThemeFontOverride("font", _iconFont);
        btn.AddThemeFontSizeOverride("font_size", 18);
        btn.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        btn.AddThemeColorOverride("font_hover_color", new Color(1, 1, 1));
        btn.AddThemeColorOverride("font_disabled_color", new Color(0.4f, 0.4f, 0.4f));
        if (onPressed != null) btn.Pressed += onPressed;
        return btn;
    }

    private void OnSendPressed()
    {
        var text = _inputEdit.Text;
        if (string.IsNullOrWhiteSpace(text) || _activeChatTab == null) return;

        if (_activeChatTab.Id == "main")
            OnSendLocalChat?.Invoke(text);
        // IM send routing lands with Phase 1c's net plumbing (dynamic tabs aren't created yet).

        _inputEdit.Text = "";
    }

    private void OnHistoryPressed()
    {
        if (_activeChatTab == null) return;

        var win = new ChatHistoryWindow();
        GetParent().AddChild(win);
        win.Open(_logger, _activeChatTab.LogKind, _activeChatTab.DisplayName, _activeChatTab.DisplayName);
    }

    private void ScrollLogToBottom()
    {
        var vscroll = _logView.GetVScrollBar();
        if (vscroll != null) vscroll.Value = vscroll.MaxValue;
    }

    private void ShowJumpToLatest(bool show) => _jumpToLatestButton.Visible = show;

    // ---- Conversation list (inner vertical axis: Main + dynamic IM rows) --------------------

    private ChatTab AddChatTab(string id, string displayName, ChatLogKind kind, bool closeable)
    {
        var row = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _conversationList.AddChild(row);

        var inner = new HBoxContainer();
        inner.AddThemeConstantOverride("separation", 4);
        row.AddChild(inner);

        var label = new Button
        {
            Text = displayName,
            Flat = true,
            FocusMode = FocusModeEnum.None,
            Alignment = HorizontalAlignment.Left,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 18),
        };
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", new Color(0.75f, 0.75f, 0.75f));
        label.AddThemeColorOverride("font_hover_color", new Color(1, 1, 1));
        inner.AddChild(label);

        var unreadLabel = new Label { Visible = false };
        unreadLabel.AddThemeColorOverride("font_color", new Color(0.85f, 0.25f, 0.2f, 0.9f));
        unreadLabel.AddThemeFontSizeOverride("font_size", 10);
        inner.AddChild(unreadLabel);

        var tab = new ChatTab
        {
            Id = id,
            DisplayName = displayName,
            RowPanel = row,
            UnreadLabel = unreadLabel,
            LogKind = kind,
            Closeable = closeable,
        };
        _chatTabs.Add(tab);

        label.Pressed += () => SelectChatTab(tab);

        if (closeable)
        {
            var closeBtn = new Button
            {
                Text = "×",
                Flat = true,
                FocusMode = FocusModeEnum.None,
                CustomMinimumSize = new Vector2(14, 14),
            };
            closeBtn.AddThemeFontSizeOverride("font_size", 12);
            closeBtn.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
            closeBtn.AddThemeColorOverride("font_hover_color", new Color(1f, 0.4f, 0.4f));
            closeBtn.Pressed += () => CloseChatTab(tab);
            inner.AddChild(closeBtn);
        }

        ApplyRowStyle(tab, selected: false);
        return tab;
    }

    private void CloseChatTab(ChatTab tab)
    {
        if (!tab.Closeable) return;
        bool wasActive = tab == _activeChatTab;
        _chatTabs.Remove(tab);
        tab.RowPanel.QueueFree();
        if (wasActive) SelectChatTab(_chatTabs[0]); // "Main" is never closeable, always index-safe
    }

    private static void ApplyRowStyle(ChatTab tab, bool selected)
    {
        var style = new StyleBoxFlat
        {
            BgColor = selected ? new Color(0.3f, 0.6f, 0.9f, 0.25f) : new Color(0, 0, 0, 0),
            CornerRadiusTopLeft = 4,
            CornerRadiusBottomLeft = 4,
            ContentMarginLeft = 6,
            ContentMarginRight = 4,
            ContentMarginTop = 1,
            ContentMarginBottom = 1,
        };
        tab.RowPanel.AddThemeStyleboxOverride("panel", style);
    }

    private void SelectChatTab(ChatTab tab)
    {
        if (_activeChatTab == tab) return;
        if (_activeChatTab != null) ApplyRowStyle(_activeChatTab, selected: false);
        _activeChatTab = tab;
        ApplyRowStyle(tab, selected: true);

        tab.UnreadCount = 0;
        UpdateUnreadBadge(tab);

        RebuildLogContent(tab);
        // Switching tabs always resumes following at the bottom -- simplest behavior, and matches
        // "opening a conversation" reading as looking at its latest state, not a frozen scroll spot.
        tab.FollowingBottom = true;
        ScrollLogToBottom();
        ShowJumpToLatest(false);
    }

    private void AppendLineToTab(ChatTab tab, string bbcodeLine)
    {
        bool overflowed = tab.Lines.Count >= MaxLogLines;
        tab.Lines.Add(bbcodeLine);
        if (overflowed) tab.Lines.RemoveAt(0);

        if (tab != _activeChatTab)
        {
            tab.UnreadCount++;
            UpdateUnreadBadge(tab);
            return;
        }

        if (overflowed)
            RebuildLogContent(tab); // full rebuild -- capped at MaxLogLines, cheap, and rare
        else
            _logView.AppendText(bbcodeLine + "\n");

        if (tab.FollowingBottom) ScrollLogToBottom();
        else ShowJumpToLatest(true);
    }

    private void RebuildLogContent(ChatTab tab)
    {
        _logView.Clear();
        foreach (var line in tab.Lines)
            _logView.AppendText(line + "\n");
    }

    private static void UpdateUnreadBadge(ChatTab tab)
    {
        if (tab.UnreadCount <= 0) { tab.UnreadLabel.Visible = false; return; }
        tab.UnreadLabel.Visible = true;
        tab.UnreadLabel.Text = tab.UnreadCount > UnreadCap ? "9+" : tab.UnreadCount.ToString();
    }

    // ---- Outer tab strip (Chat / Friends / Groups, horizontal along the top) ----------------

    private void BuildOuterTabStrip(Control parent)
    {
        var stripPanel = new PanelContainer { CustomMinimumSize = new Vector2(0, 34) };
        var stripStyle = new StyleBoxFlat
        {
            BgColor = new Color(1, 1, 1, 0.03f),
            BorderWidthBottom = 1,
            BorderColor = new Color(1, 1, 1, 0.08f),
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 4,
        };
        stripPanel.AddThemeStyleboxOverride("panel", stripStyle);
        parent.AddChild(stripPanel);

        _outerTabStrip = new HBoxContainer();
        _outerTabStrip.AddThemeConstantOverride("separation", 4);
        stripPanel.AddChild(_outerTabStrip);

        _outerPageHost = new Control
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        parent.AddChild(_outerPageHost);
    }

    private void AddOuterTab(string tabName, Control page)
    {
        bool isFirst = _outerTabs.Count == 0;
        page.Visible = isFirst;
        page.SetAnchorsPreset(LayoutPreset.FullRect);
        _outerPageHost.AddChild(page);

        var tabButton = new Button
        {
            Text = tabName,
            ToggleMode = true,
            ButtonPressed = isFirst,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(70, 30),
        };
        StyleOuterTabButton(tabButton);
        tabButton.Pressed += () => SelectOuterTab(page);
        _outerTabStrip.AddChild(tabButton);

        _outerTabs.Add((tabButton, page));
    }

    // Rounded-top "pill" tabs -- the classic top tab bar look, distinct from the conversation
    // list's rounded-left rows so the two axes read as visually different kinds of navigation.
    private static void StyleOuterTabButton(Button btn)
    {
        var normalStyle = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.03f), CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6 };
        var hoverStyle = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.08f), CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6 };
        var pressedStyle = new StyleBoxFlat { BgColor = new Color(0.3f, 0.6f, 0.9f, 0.25f), CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6 };
        btn.AddThemeStyleboxOverride("normal", normalStyle);
        btn.AddThemeStyleboxOverride("hover", hoverStyle);
        btn.AddThemeStyleboxOverride("pressed", pressedStyle);
        btn.AddThemeStyleboxOverride("hover_pressed", pressedStyle);
        btn.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        btn.AddThemeColorOverride("font_pressed_color", new Color(1, 1, 1));
    }

    private void SelectOuterTab(Control selectedPage)
    {
        foreach (var (tabButton, page) in _outerTabs)
        {
            bool selected = page == selectedPage;
            page.Visible = selected;
            tabButton.ButtonPressed = selected;
        }
    }

    private static Control BuildPlaceholderPage(string headline, string subtext)
    {
        var box = new VBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };

        var headlineLabel = new Label
        {
            Text = headline,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        headlineLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
        box.AddChild(headlineLabel);

        var subLabel = new Label
        {
            Text = subtext,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        subLabel.AddThemeFontSizeOverride("font_size", 11);
        subLabel.AddThemeColorOverride("font_color", new Color(0.4f, 0.4f, 0.4f));
        box.AddChild(subLabel);

        return box;
    }
}
