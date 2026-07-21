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

    // Shared type scale for this window and its sub-components (FriendsPanel, ChatHistoryWindow)
    // -- every text element used to pick its own font size ad hoc and they'd drifted all over the
    // place (list items rendering at the ~16px theme default right next to 10-12px labels).
    // Centralizing it here is what keeps that from silently happening again.
    public const int BodyFontSize = 13;  // primary content: chat log, list item names, input fields
    public const int LabelFontSize = 12; // tab labels, action buttons
    public const int MetaFontSize = 10;  // section headers, counts, unread badges

    private sealed class OuterTab
    {
        public PanelContainer Pill = null!;
        public Label Icon = null!;
        public Button Label = null!;
        public Control Page = null!;
    }

    private readonly List<OuterTab> _outerTabs = new();
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
    private GridSession? _session;

    /// <summary>Wired by Boot to GridSession.SendChat -- the Main tab's send path. IM tabs get
    /// their own send routing once Phase 1c's net plumbing exists.</summary>
    public Action<string>? OnSendLocalChat;

    public void Initialize(ChatLogger logger)
    {
        _logger = logger;
    }

    /// <summary>Called by Boot after each successful login (session is a fresh instance per
    /// login, unlike ChatLogger/OnSendLocalChat which are wired once). Also used to tell the
    /// local agent's own chat lines apart by name (see FormatChatLine).</summary>
    public void BindSession(GridSession session)
    {
        _session = session;
        _friendsPanel.Initialize(session);
    }

    public override void _Ready()
    {
        base._Ready(); // SLNGWindow styling

        _iconFont = GD.Load<Font>("res://assets/fonts/MaterialSymbolsOutlined.ttf");

        Title = "COMMUNICATION";
        Visible = false;
        CustomMinimumSize = new Vector2(400, 340);
        Size = new Vector2(480, 430);
        Position = new Vector2(16, 220);
        OnCloseRequested = Hide;

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 0);
        ContentContainer.AddChild(vbox);

        BuildOuterTabStrip(vbox);

        AddOuterTab("Chat", "forum", BuildChatPage());
        _friendsPanel = new FriendsPanel();
        AddOuterTab("Friends", "person", _friendsPanel);
        AddOuterTab("Groups", "group", BuildPlaceholderPage(
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

    // Own messages get the same blue accent used for "selected" elsewhere in this window, so a
    // glance at the sender-name color tells the two apart -- matches the reviewed mockup's own-
    // vs-others distinction, without introducing a new accent color to the rest of the UI.
    private string FormatChatLine(DateTime timestamp, string sender, string message)
    {
        bool isOwn = !string.IsNullOrEmpty(_session?.AgentName) && sender == _session!.AgentName;
        string senderColor = isOwn ? "#79B8F0" : "#E0E0E0";
        return $"[color=#888888]{timestamp:HH:mm}[/color] [color={senderColor}][b]{BbEscape(sender)}[/b][/color]: {BbEscape(message)}";
    }

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

        var listVBox = new VBoxContainer();
        listVBox.AddThemeConstantOverride("separation", 4);
        listPanel.AddChild(listVBox);

        var sectionLabel = new Label { Text = "CONTACTS" };
        sectionLabel.AddThemeFontSizeOverride("font_size", MetaFontSize);
        sectionLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        listVBox.AddChild(sectionLabel);

        var listScroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        listVBox.AddChild(listScroll);

        _conversationList = new VBoxContainer
        {
            // A ScrollContainer sizes its child to its own *natural* minimum size even with
            // horizontal scrolling disabled -- without ExpandFill here the row pills only ever
            // stretch as wide as their text, leaving a dead strip down the right side of the
            // sidebar instead of filling the column.
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
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
        _logView.AddThemeFontSizeOverride("normal_font_size", BodyFontSize);
        _logView.AddThemeFontSizeOverride("bold_font_size", BodyFontSize);
        rightVBox.AddChild(_logView);

        _jumpToLatestButton = new Button
        {
            Text = "▼ Jump to latest",
            Visible = false,
            FocusMode = FocusModeEnum.None,
        };
        _jumpToLatestButton.AddThemeFontSizeOverride("font_size", LabelFontSize);
        _jumpToLatestButton.Pressed += () =>
        {
            if (_activeChatTab == null) return;
            _activeChatTab.FollowingBottom = true;
            ScrollLogToBottom();
            ShowJumpToLatest(false);
        };
        rightVBox.AddChild(_jumpToLatestButton);

        var inputRow = new HBoxContainer();
        inputRow.AddThemeConstantOverride("separation", 4);
        rightVBox.AddChild(inputRow);

        inputRow.AddChild(BuildIconButton("attach_file", "Attach (not implemented)", null));

        _inputEdit = new LineEdit
        {
            PlaceholderText = "Write a message...",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _inputEdit.AddThemeFontSizeOverride("font_size", BodyFontSize);
        _inputEdit.TextSubmitted += (_) => OnSendPressed();
        inputRow.AddChild(_inputEdit);

        inputRow.AddChild(BuildIconButton("mood", "Emoji (not implemented)", null));

        _sendButton = BuildIconButton("send", "Send", OnSendPressed);
        inputRow.AddChild(_sendButton);

        return hbox;
    }

    /// <summary>Per-conversation action icons above the message log. Only History is wired up
    /// today; Give Item / Voice Call / Search are placeholders captured from the M5-3 UX
    /// proposal §9 (Gemini Canvas mockup review) -- shown now with a "(not implemented)" tooltip
    /// so the intended affordance isn't lost, rather than added silently later.</summary>
    private Control BuildActionIconRow()
    {
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 2);

        row.AddChild(BuildIconButton("history", "History", OnHistoryPressed));
        row.AddChild(BuildIconButton("card_giftcard", "Give Item (not implemented)", null));
        row.AddChild(BuildIconButton("call", "Voice Call (not implemented)", null));
        row.AddChild(BuildIconButton("search", "Search (not implemented)", null));

        return row;
    }

    // Deliberately NOT using Button.Disabled for "not implemented yet" icons: a disabled
    // BaseButton in Godot 4 stops receiving hover/tooltip processing, which would silently
    // defeat the whole point of the "(not implemented)" tooltip. Leaving it enabled with no
    // Pressed handler gets normal hover/tooltip feedback and a harmless no-op click instead.
    private Button BuildIconButton(string glyph, string tooltip, Action? onPressed)
    {
        var btn = new Button
        {
            Text = glyph,
            Flat = true,
            FocusMode = FocusModeEnum.None,
            TooltipText = tooltip,
            CustomMinimumSize = new Vector2(28, 28),
        };
        btn.AddThemeFontOverride("font", _iconFont);
        btn.AddThemeFontSizeOverride("font_size", 18);
        btn.AddThemeColorOverride("font_color", new Color(0.78f, 0.78f, 0.78f));
        btn.AddThemeColorOverride("font_hover_color", new Color(1, 1, 1));
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

    /// <param name="isOnline">Presence dot next to the name -- green/grey like FriendsPanel's,
    /// omitted entirely when null (e.g. "Main" isn't a person, so it gets no dot). IM rows pass
    /// a value once Phase 1c wires them up to a friend/contact's live status.</param>
    private ChatTab AddChatTab(string id, string displayName, ChatLogKind kind, bool closeable, bool? isOnline = null)
    {
        var row = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _conversationList.AddChild(row);

        var inner = new HBoxContainer();
        inner.AddThemeConstantOverride("separation", 4);
        row.AddChild(inner);

        if (isOnline.HasValue)
        {
            var dot = new Label { Text = "●", VerticalAlignment = VerticalAlignment.Center };
            dot.AddThemeFontSizeOverride("font_size", 8);
            dot.AddThemeColorOverride("font_color",
                isOnline.Value ? new Color(0.3f, 0.85f, 0.3f) : new Color(0.4f, 0.4f, 0.4f));
            inner.AddChild(dot);
        }

        var label = new Button
        {
            Text = displayName,
            Flat = true,
            ClipText = true,
            FocusMode = FocusModeEnum.None,
            Alignment = HorizontalAlignment.Left,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 18),
        };
        label.AddThemeFontSizeOverride("font_size", BodyFontSize);
        label.AddThemeColorOverride("font_color", new Color(0.85f, 0.85f, 0.85f));
        label.AddThemeColorOverride("font_hover_color", new Color(1, 1, 1));
        inner.AddChild(label);

        var unreadLabel = new Label { Visible = false };
        unreadLabel.AddThemeColorOverride("font_color", new Color(0.85f, 0.25f, 0.2f, 0.9f));
        unreadLabel.AddThemeFontSizeOverride("font_size", MetaFontSize);
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
            closeBtn.AddThemeFontSizeOverride("font_size", LabelFontSize);
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
        var stripPanel = new PanelContainer { CustomMinimumSize = new Vector2(0, 40) };
        var stripStyle = new StyleBoxFlat
        {
            BgColor = new Color(1, 1, 1, 0.03f),
            BorderWidthBottom = 1,
            BorderColor = new Color(1, 1, 1, 0.08f),
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 6,
            ContentMarginBottom = 6,
        };
        stripPanel.AddThemeStyleboxOverride("panel", stripStyle);
        parent.AddChild(stripPanel);

        _outerTabStrip = new HBoxContainer();
        _outerTabStrip.AddThemeConstantOverride("separation", 4);
        stripPanel.AddChild(_outerTabStrip);

        // Without this margin, a tab page's last row (e.g. FriendsPanel's "Friends: N" count)
        // sits flush against the window's bottom edge/resize handle -- give every page the same
        // breathing room instead of margin-ing each one individually.
        var pageMargin = new MarginContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        pageMargin.AddThemeConstantOverride("margin_left", 8);
        pageMargin.AddThemeConstantOverride("margin_right", 8);
        pageMargin.AddThemeConstantOverride("margin_top", 8);
        pageMargin.AddThemeConstantOverride("margin_bottom", 8);
        parent.AddChild(pageMargin);

        _outerPageHost = new Control
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        pageMargin.AddChild(_outerPageHost);
    }

    private void AddOuterTab(string tabName, string iconGlyph, Control page)
    {
        bool isFirst = _outerTabs.Count == 0;
        page.Visible = isFirst;
        page.SetAnchorsPreset(LayoutPreset.FullRect);
        _outerPageHost.AddChild(page);

        var pill = new PanelContainer();
        _outerTabStrip.AddChild(pill);

        var inner = new HBoxContainer();
        inner.AddThemeConstantOverride("separation", 6);
        pill.AddChild(inner);

        var icon = new Label { Text = iconGlyph, VerticalAlignment = VerticalAlignment.Center };
        icon.AddThemeFontOverride("font", _iconFont);
        icon.AddThemeFontSizeOverride("font_size", 15);
        inner.AddChild(icon);

        var label = new Button
        {
            Text = tabName,
            Flat = true,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0, 24),
        };
        label.AddThemeFontSizeOverride("font_size", LabelFontSize);
        inner.AddChild(label);

        var tab = new OuterTab { Pill = pill, Icon = icon, Label = label, Page = page };
        _outerTabs.Add(tab);
        label.Pressed += () => SelectOuterTab(page);

        ApplyOuterTabStyle(tab, selected: isFirst);
    }

    // Rounded "pill" tabs -- the classic top tab bar look, distinct from the conversation list's
    // rounded-left rows so the two axes read as visually different kinds of navigation.
    private static void ApplyOuterTabStyle(OuterTab tab, bool selected)
    {
        var style = new StyleBoxFlat
        {
            BgColor = selected ? new Color(1, 1, 1, 0.10f) : new Color(0, 0, 0, 0),
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 5,
            ContentMarginBottom = 5,
        };
        tab.Pill.AddThemeStyleboxOverride("panel", style);

        var fg = selected ? new Color(1, 1, 1) : new Color(0.72f, 0.72f, 0.72f);
        tab.Icon.AddThemeColorOverride("font_color", fg);
        tab.Label.AddThemeColorOverride("font_color", fg);
        tab.Label.AddThemeColorOverride("font_hover_color", new Color(1, 1, 1));
    }

    private void SelectOuterTab(Control selectedPage)
    {
        foreach (var tab in _outerTabs)
        {
            bool selected = tab.Page == selectedPage;
            tab.Page.Visible = selected;
            ApplyOuterTabStyle(tab, selected);
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
        headlineLabel.AddThemeFontSizeOverride("font_size", BodyFontSize);
        headlineLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        box.AddChild(headlineLabel);

        var subLabel = new Label
        {
            Text = subtext,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        subLabel.AddThemeFontSizeOverride("font_size", MetaFontSize);
        subLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.5f, 0.5f));
        box.AddChild(subLabel);

        return box;
    }
}
