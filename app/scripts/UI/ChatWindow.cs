using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SLNG.Core;
using SLNG.Core.ChatLogs;
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
    private const int PreloadHistoryLines = 50; // "recent chat" tail loaded from disk when a tab opens

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
        public Label Label = null!;
        /// <summary>The red count after the name -- used by the Chat tab, hidden on the others.</summary>
        public Label Badge = null!;
        public Control Page = null!;
        public bool Selected;
        public bool Hover;
        /// <summary>Something new is waiting behind this tab: it is drawn warm, in front of the grey of the others.</summary>
        public bool HasUnread;
    }

    /// <summary>The total last put on the Chat tab's badge, so the per-frame check only touches the controls when it changes.</summary>
    private int _chatTabBadgeTotal = -1;

    private readonly List<OuterTab> _outerTabs = new();
    private HBoxContainer _outerTabStrip = null!;
    private Control _outerPageHost = null!;

    private Control _chatPageControl = null!;
    private Button _profileButton = null!;
    private TextureRect _headerAvatarIcon = null!;
    private Button _headerTitleButton = null!;
    private PopupMenu _tabContextMenu = null!;
    private ChatTab? _contextMenuTab;
    private Button _participantsButton = null!;
    private PanelContainer _participantsPanel = null!;
    private Label _participantsHeader = null!;
    private ItemList _participantsList = null!;
    private PopupMenu _participantsMenu = null!;
    private readonly List<Guid> _participantIds = new();
    private int _participantMenuIndex = -1;
    private VBoxContainer _conversationList = null!;
    private RichTextLabel _logView = null!;
    internal RichTextLabel LogView => _logView;
    private Button _jumpToLatestButton = null!;
    private double _lastLogPage;
    private LineEdit _inputEdit = null!;
    private Button _sendButton = null!;
    private Font _iconFont = null!;
    private FriendsPanel _friendsPanel = null!;
    private GroupsPanel _groupsPanel = null!;
    private RecentPanel _recentPanel = null!;
    // FEAT-UI-15: the Recent tab's data. One list per chat-log folder (= per account and grid), kept in
    // user:// rather than in the log folder, which is shared with Firestorm.
    private readonly RecentConversationList _recent = new();
    private string? _recentPath;

    /// <summary>False keeps the Recent list in memory only. <c>--selftest</c> runs against the developer's
    /// real <c>user://</c>, so a check that feeds a ChatWindow messages switches this off.</summary>
    internal static bool PersistRecent { get; set; } = true;
    private Timer? _typingDebounceTimer;

    private sealed class ChatTab
    {
        public string Id = "";
        // What the row and the messages SHOW: the Display Name when the person has one. Changes when it
        // resolves (RefreshNames).
        public string DisplayName = "";
        // What the conversation IS: the name its log file, its History window and its Recent entry use --
        // the legacy name, never a Display Name (those change, and would fork the history).
        public string LogName = "";
        public Button Label = null!;
        // The mini profile picture before the name (IM tabs only); null for Main, groups, conferences.
        public TextureRect? IconRect;
        // The row's green/grey presence dot (friends only), the row it sits in, and the state it shows. A dot is
        // created the first time a friend's status is heard, so a tab opened before they were a friend gets one.
        public Label? PresenceDot;
        public HBoxContainer Row = null!;
        public bool? Online;
        // The other side is typing to us (IM tabs): until when (Time.GetTicksMsec; a stop that never arrives must
        // not leave the indicator on for ever), and the mark in the row.
        public ulong PeerTypingUntilMsec;
        public Label? TypingMark;
        public PanelContainer RowPanel = null!;
        public Label UnreadLabel = null!;
        public ChatLogKind LogKind;
        public bool Closeable;
        public readonly List<string> Lines = new();
        public int UnreadCount;
        public bool FollowingBottom = true;
        // Set for IM tabs only -- who OnSendPressed routes to via GridSession.SendInstantMessage.
        // Null for "Main" (routes through OnSendLocalChat instead).
        public Guid? TargetAgentId;
        // Set for GROUP tabs only -- routes through GridSession.SendGroupMessage instead. A tab
        // has at most one of TargetAgentId / TargetGroupId; "Main" has neither.
        public Guid? TargetGroupId;
        // Set for CONFERENCE tabs only (several people, no group): the session lines go to and come from.
        public Guid? TargetConferenceId;
        // Own lines the grid has sent back through a conference session -- see OnSendPressed's fallback.
        public int OwnConferenceEchoes;
        // Whether the conference's member list is open beside the log (a per-tab switch, off until asked for).
        public bool ParticipantsShown;
        // Shown once per tab per session -- see WarnIfTargetOffline.
        public bool OfflineNoticeShown;
    }

    private readonly List<ChatTab> _chatTabs = new();
    private ChatTab? _activeChatTab;

    private ChatLogger _logger = null!;
    private GridSession? _session;

    private ChatMentionPicker? _mentionPicker;
    private EmojiPickerWindow? _emojiPicker;
    private int _mentionQueryStartIndex = -1;
    private readonly HashSet<string> _recentSpeakers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Guid> _nearbyAgentIds = new();

    private partial class ChatLineEdit : LineEdit
    {
        public ChatWindow? OwnerWindow;

        // Enter keeps focus in the chat bar (see OnSendPressed), so Escape is the explicit way back
        // to the world -- same as every SL viewer. AcceptEvent() stops it from also reaching
        // AvatarController._UnhandledInput, which would otherwise reset the camera in the same
        // keypress that merely left the chat bar.
        public override void _GuiInput(InputEvent @event)
        {
            if (OwnerWindow?._mentionPicker is { IsActive: true } picker)
            {
                if (@event is InputEventKey { Pressed: true, Echo: false } keyEvent)
                {
                    if (keyEvent.Keycode == Key.Up)
                    {
                        picker.SelectPrevious();
                        AcceptEvent();
                        return;
                    }
                    if (keyEvent.Keycode == Key.Down)
                    {
                        picker.SelectNext();
                        AcceptEvent();
                        return;
                    }
                    if (keyEvent.Keycode == Key.Enter || keyEvent.Keycode == Key.KpEnter || keyEvent.Keycode == Key.Tab)
                    {
                        if (picker.ConfirmSelected())
                        {
                            AcceptEvent();
                            return;
                        }
                    }
                    if (keyEvent.Keycode == Key.Escape)
                    {
                        picker.HidePicker();
                        AcceptEvent();
                        return;
                    }
                }
            }

            if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
            {
                ReleaseFocus();
                AcceptEvent();
                OwnerWindow?.StopTyping();
                return;
            }
            base._GuiInput(@event);
        }
        public override bool _CanDropData(Vector2 atPosition, Variant data)
        {
            if (OwnerWindow != null && OwnerWindow.IsInventoryItemData(data)) return true;
            return base._CanDropData(atPosition, data);
        }
        public override void _DropData(Vector2 atPosition, Variant data)
        {
            if (OwnerWindow != null && OwnerWindow.IsInventoryItemData(data))
            {
                OwnerWindow.HandleInventoryDrop(data);
                return;
            }
            base._DropData(atPosition, data);
        }
    }

    private partial class ChatLogRichTextLabel : RichTextLabel
    {
        public ChatWindow? OwnerWindow;
        public override bool _CanDropData(Vector2 atPosition, Variant data)
        {
            if (OwnerWindow != null && OwnerWindow.IsInventoryItemData(data)) return true;
            return base._CanDropData(atPosition, data);
        }
        public override void _DropData(Vector2 atPosition, Variant data)
        {
            if (OwnerWindow != null && OwnerWindow.IsInventoryItemData(data))
            {
                OwnerWindow.HandleInventoryDrop(data);
                return;
            }
            base._DropData(atPosition, data);
        }
    }

    private partial class ChatDropPanel : PanelContainer
    {
        public ChatWindow? OwnerWindow;
        public override bool _CanDropData(Vector2 atPosition, Variant data) => OwnerWindow != null && OwnerWindow.IsInventoryItemData(data);
        public override void _DropData(Vector2 atPosition, Variant data) => OwnerWindow?.HandleInventoryDrop(data);
    }

    private partial class ChatDropVBox : VBoxContainer
    {
        public ChatWindow? OwnerWindow;
        public override bool _CanDropData(Vector2 atPosition, Variant data) => OwnerWindow != null && OwnerWindow.IsInventoryItemData(data);
        public override void _DropData(Vector2 atPosition, Variant data) => OwnerWindow?.HandleInventoryDrop(data);
    }

    /// <summary>Wired by Boot to GridSession.SendChat -- the Main tab's send path. IM tabs get
    /// their own send routing once Phase 1c's net plumbing exists.</summary>
    public Action<string>? OnSendLocalChat;

    /// <summary>FEAT-UI-13: wired by Boot to its profile-window opener. Fired by clicking a
    /// resident's (linked) name in the chat log and by the Friends tab's "Profile" button.</summary>
    /// <summary>
    /// Unread messages across every conversation, for the badge on the toolbar's chat button.
    /// </summary>
    /// <remarks>
    /// A plain sum of the per-tab counters the window already keeps for its own badges — no
    /// special case for "the window is open", because selecting a tab zeroes its counter anyway.
    /// A conversation you are looking at therefore contributes nothing on its own.
    /// </remarks>
    public int TotalUnread
    {
        get
        {
            int total = 0;
            foreach (var tab in _chatTabs) total += tab.UnreadCount;
            return total;
        }
    }

    public Action<Guid, string>? OnOpenProfileRequested;

    /// <summary>FEAT-UI-54: the Groups tab's "Profile" button -- Boot owns the group info windows.</summary>
    public Action<Guid, string>? OnOpenGroupInfoRequested;

    /// <summary>MVP5-2: the Friends tab's "Pay..." action, forwarded to whoever owns the pay
    /// window.</summary>
    public Action<Guid, string>? OnPayRequested;

    /// <summary>Invoked when a teleport link or SLurl in chat is clicked.</summary>
    public Action<string, System.Numerics.Vector3>? OnTeleportRequested;

    public void Initialize(ChatLogger logger)
    {
        _logger = logger;
        AttachRecentList();

        // "Main" was already created in _Ready(), before _logger existed -- preload its recent
        // history now that logging is available, same as every IM tab does at creation time.
        var mainTab = _chatTabs.Find(t => t.Id == "main");
        if (mainTab == null) return;
        PreloadRecentHistory(mainTab);
        if (mainTab != _activeChatTab) return;
        RebuildLogContent(mainTab);
        ScrollLogToBottom(); // was missing -- left the log sitting at the top after preload
    }

    /// <summary>BUG-GRID-01: forgets the previous session's conversations and shows the new
    /// account's own history. A chat log now belongs to one account on one grid, and everything
    /// this window holds in memory -- the Main tab's lines, every IM and group tab -- came from the
    /// session before, which may have been another account on another grid. Call it after
    /// <see cref="ChatLogger.UseDirectory"/> pointed the logger at the new account's directory.
    /// Group chat is not left explicitly: the old session is already gone, and leaving would be sent
    /// through the new one.</summary>
    public void ResetForNewSession()
    {
        AttachRecentList(); // the logger now points at the new account's folder
        var main = _chatTabs.Find(t => t.Id == "main");
        if (main == null) return;

        foreach (var tab in _chatTabs.ToArray())
        {
            if (tab == main) continue;
            _chatTabs.Remove(tab);
            tab.RowPanel.QueueFree();
        }

        main.Lines.Clear();
        main.UnreadCount = 0;
        UpdateUnreadBadge(main);
        PreloadRecentHistory(main);
        main.FollowingBottom = true;

        if (_activeChatTab == main)
        {
            RebuildLogContent(main);
            ScrollLogToBottom();
        }
        else
        {
            SelectChatTab(main);
        }
    }

    /// <summary>Called by Boot after each successful login (session is a fresh instance per
    /// login, unlike ChatLogger/OnSendLocalChat which are wired once). Also used to tell the
    /// local agent's own chat lines apart by name (see FormatChatLine).</summary>
    public void BindSession(GridSession session)
    {
        if (_session != null)
        {
            _session.DisplayNameResolved -= OnDisplayNameResolved;
            _session.NameResolved -= OnDisplayNameResolved;
            _session.GroupsUpdated -= OnGroupsUpdatedForNames;
            _session.FriendStatusChanged -= OnFriendStatusChanged;
            _session.InstantMessageTyping -= OnPeerTypingEvent;
            _session.NearbyAvatarsUpdated -= OnNearbyAvatarsUpdated;
        }
        _session = session;
        BindIcons(session);
        _session.DisplayNameResolved += OnDisplayNameResolved;
        _session.NameResolved += OnDisplayNameResolved; // a tab opened before its name was known
        _session.GroupsUpdated += OnGroupsUpdatedForNames; // ...or before the membership list, which carries group names, arrived
        _session.FriendStatusChanged += OnFriendStatusChanged;
        _session.InstantMessageTyping += OnPeerTypingEvent;
        _session.NearbyAvatarsUpdated += OnNearbyAvatarsUpdated;
        if (Guid.TryParse(session.AgentId, out var ownId)) session.RequestDisplayName(ownId); // own lines show it too
        _friendsPanel.Initialize(session);
        _groupsPanel.Initialize(session);

        // FEAT-UI-54: the network thread asks this for every group line, so a group whose chat is
        // switched off is dropped before anything can show, count or log it. Primed here, on the
        // main thread, so the first network-thread read does not open preferences.cfg.
        GroupMuteSettings.Prime();
        session.GroupChatIgnored = GroupMuteSettings.IsMuted;
    }

    public override void _ExitTree()
    {
        _icons?.Dispose();
        if (_session != null)
        {
            _session.DisplayNameResolved -= OnDisplayNameResolved;
            _session.NameResolved -= OnDisplayNameResolved;
            _session.GroupsUpdated -= OnGroupsUpdatedForNames;
            _session.FriendStatusChanged -= OnFriendStatusChanged;
            _session.InstantMessageTyping -= OnPeerTypingEvent;
            _session.NearbyAvatarsUpdated -= OnNearbyAvatarsUpdated;
        }
        GroupMuteSettings.MuteChanged -= OnGroupMuteChanged;
        base._ExitTree();
    }

    public override void _Ready()
    {
        base._Ready(); // SLNGWindow styling
        GroupMuteSettings.MuteChanged += OnGroupMuteChanged;

        PersistId = "chat"; // FEAT-UI-11: remember position/size across sessions

        _iconFont = GD.Load<Font>("res://assets/fonts/MaterialSymbolsOutlined.ttf");

        Title = "COMMUNICATION";
        Visible = false;
        CustomMinimumSize = new Vector2(400, 340);
        Size = new Vector2(480, 430);
        Position = new Vector2(16, 220);
        OnCloseRequested = () =>
        {
            _mentionPicker?.HidePicker();
            Hide();
        };

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 0);
        ContentContainer.AddChild(vbox);

        BuildOuterTabStrip(vbox);

        _chatPageControl = BuildChatPage();
        AddOuterTab(L10n.Tr("ui.chat.tab_chat"), "forum", _chatPageControl);
        _friendsPanel = new FriendsPanel();
        _friendsPanel.OnOpenImRequested = OpenOrFocusImTab;
        _friendsPanel.IconFor = id => _icons?.Get(id);
        _friendsPanel.OnOpenProfileRequested = (id, name) => OnOpenProfileRequested?.Invoke(id, name);
        _friendsPanel.OnPayRequested = (id, name) => OnPayRequested?.Invoke(id, name);
        _friendsPanel.OnOfferTeleportRequested = (id, _) => _session?.OfferTeleport(id);
        AddOuterTab(L10n.Tr("ui.chat.tab_friends"), "person", _friendsPanel);
        _groupsPanel = new GroupsPanel();
        _groupsPanel.OnOpenGroupChatRequested = OpenOrFocusGroupTab;
        _groupsPanel.OnOpenGroupInfoRequested = (id, name) => OnOpenGroupInfoRequested?.Invoke(id, name);
        AddOuterTab(L10n.Tr("ui.chat.tab_groups"), "group", _groupsPanel);
        _recentPanel = new RecentPanel();
        _recentPanel.IconFor = item => item.Kind == ChatLogKind.Im ? _icons?.Get(item.Id) : null;
        _recentPanel.ShownName = item => item.Kind == ChatLogKind.Im ? NameDisplay.For(_session, item.Id, item.Name) : item.Name;
        _recentPanel.OnOpenRequested = OpenRecentConversation;
        _recentPanel.OnHistoryRequested = OpenHistoryFor;
        _recentPanel.OnRemoveRequested = (kind, id) => { if (_recent.Remove(kind, id)) SaveAndRefreshRecent(); };
        _recentPanel.OnClearRequested = () => { _recent.Clear(); SaveAndRefreshRecent(); };
        AddOuterTab(L10n.Tr("ui.recent.tab"), "history", _recentPanel);

        AddChatTab("main", L10n.Tr("ui.chat.tab_main"), ChatLogKind.Local, closeable: false);
        _tabContextMenu = new PopupMenu();
        _tabContextMenu.IdPressed += OnTabContextMenuPressed;
        AddChild(_tabContextMenu);
        SelectChatTab(_chatTabs[0]);
    }

    public override void _Process(double delta)
    {
        using var _phase = MainThreadPhase.Enter("ui.chat"); // BUG-PERF-05
        ExpirePeerTyping();
        UpdateChatTabBadge();

        // Detect the user scrolling away from (or back to) the bottom of the live log so we can
        // pause auto-follow while they're reading history, per the M5-3 UX decision -- there's no
        // scroll signal that only fires on user-driven movement, so this polls once a frame like
        // ButtonBar's own drag safety-net does.
        if (_activeChatTab == null) return;
        var vscroll = _logView.GetVScrollBar();
        if (vscroll == null || vscroll.MaxValue <= vscroll.Page) return;

        // The log's visible height changes by itself (the "is typing" line appearing under it, a window resize).
        // That moves the bottom away from the scroll position without the user scrolling, so it must not end
        // auto-follow: keep following by scrolling down again.
        bool pageChanged = Math.Abs(vscroll.Page - _lastLogPage) > 0.5;
        _lastLogPage = vscroll.Page;
        if (pageChanged && _activeChatTab.FollowingBottom)
        {
            ScrollLogToBottom();
            return;
        }

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
    public void AppendLocalChatMessage(string sender, string message, Guid senderAgentId = default)
    {
        var tab = _chatTabs.Find(t => t.Id == "main");
        if (tab != null) AppendMessageToTab(tab, sender, message, senderAgentId);
    }

    /// <summary>FEAT-UI-69: Appends a friend online/offline notification to the Main (local chat) tab
    /// when enabled in preferences. Avatar name is a clickable profile link.
    /// Does not increment unread counter or write to chat history logs.</summary>
    public void AppendFriendPresenceToMain(Guid friendId, string shownName, bool online)
    {
        var mainTab = _chatTabs.Find(t => t.Id == "main");
        if (mainTab == null) return;

        string safeName = BbEscape(shownName);
        string name = friendId != Guid.Empty
            ? $"[url=avatar:{friendId}][color=#A0B0C0]{safeName}[/color][/url]"
            : $"[color=#A0B0C0]{safeName}[/color]";

        string text = L10n.TrFormat(online ? "ui.chat.friend_online" : "ui.chat.friend_offline", name);
        AppendLineToTab(mainTab, $"[color=#888888][lb]{DateTime.Now:HH:mm}][/color] [color=#888888][i]{text}[/i][/color]",
            countUnread: false);
    }

    /// <summary>Appends an incoming 1:1 IM, opening a new closeable tab keyed by the sender's
    /// agent id if this is the first message from them this session. Called by Boot on
    /// GridSession.InstantMessageReceived, marshalled to the main thread first.</summary>
    public void AppendIncomingInstantMessage(Guid fromAgentId, string fromAgentName, string message)
    {
        var tab = GetOrCreateImTab(fromAgentId, fromAgentName);
        SetPeerTyping(tab, false); // a message is the end of typing, whether or not the stop arrived
        AppendMessageToTab(tab, fromAgentName, message, fromAgentId);
    }

    /// <summary>Switches to the Chat tab and opens (or focuses) an IM conversation with one
    /// friend -- wired to FriendsPanel's "IM / Call" button and double-clicking a friend row.</summary>
    public void OpenOrFocusImTab(Guid friendId, string friendName)
    {
        if (!Visible)
        {
            Visible = true;
            EnsureOnScreen();
        }
        if (IsMinimized) Unminimize();
        var tab = GetOrCreateImTab(friendId, friendName);
        SelectOuterTab(_chatPageControl);
        SelectChatTab(tab);
    }

    private ChatTab GetOrCreateImTab(Guid agentId, string displayName)
    {
        var existing = _chatTabs.Find(t => t.Id == agentId.ToString());
        if (existing != null)
        {
            string legacy = NameDisplay.LegacyFor(_session, agentId, displayName);
            bool repairedLog = false;
            if (string.IsNullOrWhiteSpace(existing.LogName) || existing.LogName == existing.Id)
            {
                if (!string.IsNullOrWhiteSpace(legacy) && legacy != existing.Id)
                {
                    existing.LogName = legacy;
                    repairedLog = true;
                }
            }
            if ((existing.DisplayName == existing.Id || string.IsNullOrWhiteSpace(existing.DisplayName))
                && !string.IsNullOrWhiteSpace(displayName) && displayName != existing.Id)
            {
                existing.DisplayName = NameDisplay.For(_session, agentId, legacy);
                existing.Label.Text = existing.DisplayName;
            }
            if (repairedLog && existing.Lines.Count == 0)
            {
                PreloadRecentHistory(existing);
                if (existing == _activeChatTab)
                {
                    RebuildLogContent(existing);
                    ScrollLogToBottom();
                }
            }
            return existing;
        }

        // Presence is only known if this person happens to be a friend -- a stranger IMing you
        // isn't in GetFriends(), so the row gets no dot at all (see AddChatTab's isOnline param).
        bool? isOnline = _session?.GetFriends().FirstOrDefault(f => f.Id == agentId)?.IsOnline;
        // The conversation is named after the legacy name (its log file, its History, its Recent entry);
        // only the row shows the Display Name. A caller may hand over either, so normalise.
        string legacyName = NameDisplay.LegacyFor(_session, agentId, displayName);
        var tab = AddChatTab(agentId.ToString(), NameDisplay.For(_session, agentId, legacyName), ChatLogKind.Im, closeable: true, isOnline,
            iconAgentId: agentId);
        tab.LogName = legacyName;
        tab.TargetAgentId = agentId;
        RefreshIcons();
        PreloadRecentHistory(tab);
        return tab;
    }

    // ---- M5-3 Phase 2: group chat ----------------------------------------------------------

    /// <summary>Switches to the Chat tab and opens (or focuses) a group's chat -- wired to
    /// GroupsPanel's "Group Chat" button and double-clicking a group row. Joining the session is
    /// a server round trip (see GridSession.JoinGroupChat); the tab opens immediately and the
    /// join result arrives on GroupChatJoined.</summary>
    public void OpenOrFocusGroupTab(Guid groupId, string groupName)
    {
        // Asking to open a group's chat is a clearer statement of intent than the switch, so it turns
        // the chat back on first -- the same thing Firestorm does (llgroupactions.cpp:655-657). The
        // switch's own handler (OnGroupMuteChanged) joins the session, so the tab must not join twice.
        bool wasOff = GroupMuteSettings.IsMuted(groupId);
        if (wasOff) GroupMuteSettings.SetMuted(groupId, false);

        var tab = GetOrCreateGroupTab(groupId, groupName, joinSession: !wasOff);
        SelectOuterTab(_chatPageControl);
        SelectChatTab(tab);
    }

    /// <summary>True when a chat tab for this group exists. For the selftest.</summary>
    public bool HasGroupTab(Guid groupId) => _chatTabs.Exists(t => t.TargetGroupId == groupId);

    /// <summary>True when an IM chat tab for this avatar exists. For the selftest.</summary>
    public bool HasImTab(Guid agentId) => _chatTabs.Exists(t => t.TargetAgentId == agentId);

    internal void DrainPeerTypingForSelfTest(Guid agentId, string name, bool typing)
    {
        _peerTypingQueue.Enqueue(new InstantMessageTypingEvent(agentId, name, typing));
        DrainPeerTyping();
    }

    /// <summary>Appends an incoming group-chat line, opening the group's tab if this is the first
    /// message from it this session. Called by Boot on GridSession.GroupChatMessageReceived,
    /// marshalled to the main thread first.</summary>
    /// <summary>Wired by Boot to the "Show conference chats as IMs" preference.</summary>
    public Func<bool>? ConferenceAsIm;

    /// <summary>A line of an ad-hoc conference (several people, no group). By default the conference is one
    /// conversation of its own, in which everyone's lines appear. With <see cref="ConferenceAsIm"/> on, each
    /// speaker's lines go to a plain IM conversation with them instead -- the way Firestorm lists them among
    /// the IMs -- and what has no speaker to attach to (the grid's own lines, our own echo) is not shown.
    /// Called by Boot on GridSession.ConferenceChatMessageReceived, marshalled to the main thread first.</summary>
    public void AppendConferenceMessage(Guid sessionId, string sessionName, Guid fromAgentId, string fromAgentName, string message)
    {
        if (ConferenceAsIm?.Invoke() == true)
        {
            bool own = Guid.TryParse(_session?.AgentId, out var ownId) && fromAgentId == ownId;
            if (fromAgentId == Guid.Empty || own) return;

            var imTab = GetOrCreateImTab(fromAgentId, fromAgentName);
            AppendMessageToTab(imTab, fromAgentName, message, fromAgentId);
            return;
        }

        var tab = GetOrCreateConferenceTab(sessionId, sessionName, fromAgentId == Guid.Empty ? "" : fromAgentName);
        if (Guid.TryParse(_session?.AgentId, out var selfId) && fromAgentId == selfId) tab.OwnConferenceEchoes++;
        AppendMessageToTab(tab, fromAgentName, message, fromAgentId);
    }

    private ChatTab GetOrCreateConferenceTab(Guid sessionId, string sessionName, string firstSpeaker)
    {
        var existing = _chatTabs.Find(t => t.Id == sessionId.ToString());
        if (existing != null) return existing;

        // The grid's own name for the session when it gave one; else "Conference: <whoever spoke first>".
        string title = !string.IsNullOrWhiteSpace(sessionName)
            ? sessionName
            : string.IsNullOrWhiteSpace(firstSpeaker)
                ? L10n.Tr("ui.chat.conference")
                : $"{L10n.Tr("ui.chat.conference")}: {firstSpeaker}";

        var tab = AddChatTab(sessionId.ToString(), title, ChatLogKind.Im, closeable: true);
        tab.TargetConferenceId = sessionId;
        PreloadRecentHistory(tab);
        return tab;
    }

    public void AppendGroupChatMessage(Guid groupId, string groupName, Guid fromAgentId, string fromAgentName, string message)
    {
        // A group whose chat is switched off gets nothing: no tab, no unread badge, no focus, no log
        // line (FEAT-UI-54). The network layer already drops these before they get here
        // (GridSession.GroupChatIgnored); this is the second line, for a line already in flight on
        // the main-thread queue when the switch was flipped. Open tabs do not exempt it any more --
        // turning the chat off closes the tab (OnGroupMuteChanged).
        if (GroupMuteSettings.IsMuted(groupId)) return;

        var tab = GetOrCreateGroupTab(groupId, groupName, joinSession: false);
        AppendMessageToTab(tab, fromAgentName, message, fromAgentId);
    }

    /// <summary>The "Receive group chat" switch of one group changed (group info checkbox or the Groups
    /// tab's Mute button -- one setting). Off: the group's tab goes away and its chat session is left, so
    /// the simulator stops sending. The chat log on disk stays. On: the session is joined again.</summary>
    private void OnGroupMuteChanged(Guid groupId, bool muted)
    {
        if (muted)
        {
            var tab = _chatTabs.Find(t => t.TargetGroupId == groupId);
            if (tab != null) CloseChatTab(tab, leaveSession: false); // leaving is done once, below
        }
        _session?.SetGroupChatReceiving(groupId, receive: !muted);
    }

    /// <summary>Reports the outcome of a group-chat join into the group's own tab, so a failure
    /// is visible where the user is looking rather than only in the log. Called by Boot on
    /// GridSession.GroupChatJoined, marshalled to the main thread first.</summary>
    public void OnGroupChatJoinResult(Guid groupId, bool success)
    {
        var tab = _chatTabs.Find(t => t.TargetGroupId == groupId);
        if (tab == null || success) return;
        AppendLineToTab(tab, $"[color=#E0A030][i]{BbEscape(L10n.Tr("ui.groups.join_failed"))}[/i][/color]");
    }

    private ChatTab GetOrCreateGroupTab(Guid groupId, string groupName, bool joinSession)
    {
        var existing = _chatTabs.Find(t => t.Id == groupId.ToString());
        if (existing != null) return existing;

        // Fall back to the shared name cache, then the raw id: an incoming message names the
        // speaker, not the group, so groupName can be empty when a tab is opened by a message.
        string display = groupName;
        if (display == groupId.ToString()) display = ""; // the id's text is what "unknown" looks like, not a name
        if (string.IsNullOrWhiteSpace(display) && _session != null)
        {
            // The membership list first, then the name cache. A miss answers the id's text, which is not a name.
            display = _session.TryGetGroupName(groupId, out var known) ? known : "";
        }
        if (string.IsNullOrWhiteSpace(display))
        {
            display = groupId.ToString();
            _session?.RequestGroupName(groupId); // the tab is re-titled when the answer arrives (RefreshNames)
        }

        var tab = AddChatTab(groupId.ToString(), display, ChatLogKind.Group, closeable: true);
        tab.TargetGroupId = groupId;
        PreloadRecentHistory(tab);

        // Sending needs a joined session; receiving does not (the sim delivers to members
        // regardless), so only an explicitly opened tab asks to join.
        if (joinSession) _session?.JoinGroupChat(groupId);
        return tab;
    }

    /// <summary>Seeds a freshly (re-)opened tab with the tail of its on-disk log, so opening a
    /// conversation that already has history shows it immediately instead of starting blank --
    /// the live view is a rolling window, not a from-scratch buffer, per the M5-3 UX decision.
    /// Rendered dim/italic so it visually reads as "loaded from before", distinct from anything
    /// sent/received live in this session. The full log (beyond this tail) is one History click
    /// away rather than preloaded in full, to keep tab-open cheap.</summary>
    private void PreloadRecentHistory(ChatTab tab)
    {
        // FEAT-UI-41: the last N messages from the end of the file (real logs are tens of megabytes), in
        // the file's own, Firestorm-compatible, format. This used to ask for "the last page", which is
        // not the last N lines but whatever partial page happens to be at the end.
        var lines = _logger.GetTail(tab.LogKind, tab.LogName, PreloadHistoryLines);
        foreach (var line in lines)
            tab.Lines.Add($"[color=#777777][i]{BbEscape(line)}[/i][/color]");
    }

    // The grid's own "the other person is offline" notices, word for word as the reference viewer matches
    // them (llimprocessing.cpp:138-139). Second Life sends one into the conversation after an IM to an
    // offline resident; when it has, our own guess below must stay quiet or the tab says it twice.
    private const string GridNotOnlineMessage = "User not online - message will be stored and delivered later.";
    private const string GridNotOnlineInventory = "User not online - inventory has been saved.";

    private void AppendMessageToTab(ChatTab tab, string sender, string message, Guid senderAgentId = default)
    {
        if (message == GridNotOnlineMessage || message == GridNotOnlineInventory) tab.OfflineNoticeShown = true;
        if (!string.IsNullOrWhiteSpace(sender) && sender != "You" && sender != _session?.AgentName)
        {
            _recentSpeakers.Add(sender);
        }
        var now = DateTime.Now;
        AppendLineToTab(tab, FormatChatLine(now, sender, message, senderAgentId));
        _ = _logger.AppendAsync(tab.LogKind, tab.LogName, sender, message, now);
        TrackRecent(tab, now);
    }

    // ---- FEAT-UI-15: Recent conversations --------------------------------------------------------

    /// <summary>Points the Recent list at the file for the logger's current folder and loads it. Called
    /// at login (the folder changes with the account); with no folder yet the list is simply empty.</summary>
    private void AttachRecentList()
    {
        _recentPath = PersistRecent ? RecentFilePath(_logger.RootDirectory) : null;
        _recent.Load(_recentPath);
        RefreshRecentPanel();
    }

    private static string? RecentFilePath(string? logDirectory)
    {
        if (string.IsNullOrWhiteSpace(logDirectory)) return null;
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(logDirectory));
        return System.IO.Path.Combine(ProjectSettings.GlobalizePath("user://"), "recent_conversations",
            Convert.ToHexString(hash, 0, 8) + ".json");
    }

    private void TrackRecent(ChatTab tab, DateTime nowLocal)
    {
        Guid? id = tab.TargetAgentId ?? tab.TargetGroupId;
        if (id == null) return; // nearby chat

        // Saved only when the order or a name changed -- not for every line of a running chat.
        if (_recent.Touch(tab.LogKind, id.Value, tab.LogName, nowLocal.ToUniversalTime()))
            SaveAndRefreshRecent();
        else if (_recentPanel.IsVisibleInTree())
            RefreshRecentPanel();
    }

    private void SaveAndRefreshRecent()
    {
        _recent.Save(_recentPath);
        RefreshRecentPanel();
    }

    private void RefreshRecentPanel()
    {
        if (_recentPanel != null && _logger != null) _recentPanel.SetItems(_recent.Items, _logger);
    }

    private void OpenRecentConversation(ChatLogKind kind, Guid id, string name)
    {
        if (kind == ChatLogKind.Group) OpenOrFocusGroupTab(id, name);
        else OpenOrFocusImTab(id, name);
    }

    private void OpenHistoryFor(ChatLogKind kind, string name)
    {
        var win = new ChatHistoryWindow();
        win.OnOpenProfileRequested = (id, n) => OnOpenProfileRequested?.Invoke(id, n);
        win.OnTeleportRequested = (region, pos) => OnTeleportRequested?.Invoke(region, pos);
        GetParent().AddChild(win);
        win.Position = new Vector2(
            Mathf.Max(20f, Position.X + 30f),
            Mathf.Max(SLNGWindow.TopInset + 10f, Position.Y + 30f));
        win.EnsureOnScreen();
        win.Open(_logger, kind, name, name);
    }

    // ---- Display Names ---------------------------------------------------------------------------

    private int _namesRefreshQueued;

    /// <summary>Fires on a network thread for every Display Name the grid answers -- usually many in a
    /// burst. One deferred refresh per burst, not one per name.</summary>
    private void OnDisplayNameResolved(object? sender, NameResolvedEvent e) => QueueNamesRefresh();

    /// <summary>The membership list arrived or changed (network thread): group names come with it.</summary>
    private void OnGroupsUpdatedForNames(object? sender, GroupsUpdatedEvent e) => QueueNamesRefresh();

    private void QueueNamesRefresh()
    {
        if (System.Threading.Interlocked.Exchange(ref _namesRefreshQueued, 1) == 0)
            CallDeferred(nameof(RefreshNames));
    }

    /// <summary>A Display Name arrived (or changed): re-title the IM rows and the Recent list. Lines already
    /// in the log view keep the name they were shown with; new lines use the new one.</summary>
    private void RefreshNames()
    {
        System.Threading.Volatile.Write(ref _namesRefreshQueued, 0);
        RefreshParticipants(); // member names too

        foreach (var tab in _chatTabs)
        {
            // A tab opened before its name was known is titled with the raw id; now that the name may have
            // arrived, name the conversation properly (this also fixes where its log goes from now on).
            if (tab.Closeable && _session != null)
            {
                string? resolved = null;
                if (tab.TargetGroupId is { } gid && _session.TryGetGroupName(gid, out var groupName)) resolved = groupName;
                else if (tab.TargetAgentId is { } aid)
                {
                    if (_session.TryGetCachedName(aid, out var agentName) && !string.IsNullOrWhiteSpace(agentName) && agentName != aid.ToString())
                        resolved = agentName;
                    else
                    {
                        var friend = _session.GetFriends().FirstOrDefault(f => f.Id == aid);
                        if (friend != null && !string.IsNullOrWhiteSpace(friend.Name))
                            resolved = friend.Name;
                    }
                }

                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    bool hadEmptyLog = string.IsNullOrWhiteSpace(tab.LogName) || tab.LogName == tab.Id;
                    if (hadEmptyLog) tab.LogName = resolved;
                    if (tab.DisplayName == tab.Id)
                    {
                        tab.DisplayName = resolved;
                        tab.Label.Text = resolved;
                    }
                    if (hadEmptyLog && tab.Lines.Count == 0)
                    {
                        PreloadRecentHistory(tab);
                        if (tab == _activeChatTab)
                        {
                            RebuildLogContent(tab);
                            ScrollLogToBottom();
                        }
                    }
                }
            }

            if (tab.TargetAgentId is not { } id) continue;
            string shown = NameDisplay.For(_session, id, tab.LogName);
            if (shown == tab.DisplayName) continue;
            tab.DisplayName = shown;
            tab.Label.Text = shown;
        }

        if (_recentPanel.IsVisibleInTree()) RefreshRecentPanel();
    }

    // ---- mini profile pictures -----------------------------------------------------------------------

    /// <summary>Wired by Boot: where the picture textures come from (the GPU cache and the asset service,
    /// which only exist once the world is set up). Null or a null cache means no pictures.</summary>
    public Func<(GpuCache? Gpu, SLNG.Assets.AssetService? Assets)>? IconSources;

    private AvatarIcons? _icons;
    private int _iconsRefreshQueued;

    private void BindIcons(GridSession session)
    {
        _icons?.Dispose();
        _icons = null;

        var sources = IconSources?.Invoke();
        var gpu = sources?.Gpu;
        var assets = sources?.Assets;
        if (gpu == null) return;

        // Fires from any thread (network, asset worker): one deferred refresh per burst.
        _icons = new AvatarIcons(gpu, assets, _ =>
        {
            if (System.Threading.Interlocked.Exchange(ref _iconsRefreshQueued, 1) == 0)
                CallDeferred(nameof(RefreshIcons));
        });
        _icons.Bind(session);
        RefreshIcons();
    }

    /// <summary>Puts the pictures that have arrived on the IM rows and into the Recent list. Main thread.</summary>
    private void RefreshIcons()
    {
        System.Threading.Volatile.Write(ref _iconsRefreshQueued, 0);
        if (_icons == null) return;

        _icons.Drain();
        foreach (var tab in _chatTabs)
        {
            if (tab.IconRect == null || tab.TargetAgentId is not { } id) continue;
            var tex = _icons.Get(id);
            if (tex != null && tab.IconRect.Texture != tex) tab.IconRect.Texture = tex;
        }

        if (_recentPanel != null && _recentPanel.IsVisibleInTree()) RefreshRecentPanel();
        if (_friendsPanel != null && _friendsPanel.IsVisibleInTree()) _friendsPanel.RefreshIcons();
        UpdateHeaderForTab(_activeChatTab);
    }

    /// <summary>Selftest: the Recent list as the window holds it.</summary>
    internal IReadOnlyList<RecentConversation> RecentForSelfTest => _recent.Items;
    internal RecentPanel RecentPanelForSelfTest => _recentPanel;

    /// <summary>FEAT-UI-13: a resident's name in the log is a [url=avatar:<guid>] link;
    /// clicking it opens their profile. Also handles Web URLs and SLurls.</summary>
    private void OnLogMetaClicked(Variant meta)
    {
        var s = meta.AsString();
        const string prefix = "avatar:";
        if (s.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParse(s.AsSpan(prefix.Length), out var id))
        {
            string name = "";
            if (_session != null)
            {
                if (_session.TryGetDisplayName(id, out var dn) && !string.IsNullOrWhiteSpace(dn))
                    name = dn;
                else if (_session.TryGetCachedName(id, out var cn) && !string.IsNullOrWhiteSpace(cn) && cn != id.ToString())
                    name = cn;
                else
                {
                    var friend = _session.GetFriends().FirstOrDefault(f => f.Id == id);
                    if (friend != null && !string.IsNullOrWhiteSpace(friend.Name))
                        name = friend.Name;
                }
            }
            OnOpenProfileRequested?.Invoke(id, name);
            return;
        }

        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            OS.ShellOpen(s);
            return;
        }

        const string slPrefix = "secondlife:///app/";
        if (s.StartsWith(slPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string rest = s[slPrefix.Length..];
            string[] parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && string.Equals(parts[0], "agent", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(parts[1], out var agentId))
            {
                if (parts.Length >= 3 && string.Equals(parts[2], "im", StringComparison.OrdinalIgnoreCase))
                {
                    OpenOrFocusImTab(agentId, agentId.ToString());
                }
                else
                {
                    OnOpenProfileRequested?.Invoke(agentId, "");
                }
                return;
            }
            if (parts.Length >= 2 && string.Equals(parts[0], "group", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(parts[1], out var groupId))
            {
                OnOpenGroupInfoRequested?.Invoke(groupId, "");
                return;
            }
            if (parts.Length >= 5 && string.Equals(parts[0], "teleport", StringComparison.OrdinalIgnoreCase))
            {
                string region = Uri.UnescapeDataString(parts[1]);
                if (float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
                    && float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y)
                    && float.TryParse(parts[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
                {
                    OnTeleportRequested?.Invoke(region, new System.Numerics.Vector3(x, y, z));
                }
                return;
            }
        }
    }

    /// <summary>Client-side "they're offline" notice, shown once per tab per session right after
    /// sending. There's no reliable wire-protocol signal for this to key off -- OpenSim only
    /// stores/forwards offline IMs if the grid runs an offline-message module, which most test
    /// grids don't, so a real server round trip can't be trusted either way. Presence from
    /// GetFriends() (already tracked for the Friends tab) is the one thing we always know
    /// locally, so that's what this checks -- silently does nothing for a non-friend target,
    /// since their online status isn't known at all in that case.</summary>
    /// <summary>How long to give the grid to send its own offline notice before ours is shown.</summary>
    private const double OfflineNoticeGraceSeconds = 2.5;

    /// <summary>How long a conference line waits for the grid's echo before the tab shows it itself.</summary>
    private const double ConferenceEchoGraceSeconds = 2.0;

    private void WarnIfTargetOffline(ChatTab tab, Guid targetId)
    {
        if (tab.OfflineNoticeShown) return;
        bool? isOnline = _session?.GetFriends().FirstOrDefault(f => f.Id == targetId)?.IsOnline;
        if (isOnline != false) return;

        tab.OfflineNoticeShown = true;
        string notice = $"{tab.DisplayName} is offline. They'll see this message next time they log in.";
        AppendLineToTab(tab, $"[color=#E0A030][i]{BbEscape(notice)}[/i][/color]");
        // FEAT-UI-41: an empty sender is a system line -- written under the grid's system name
        // ("Second Life" / "Grid"), as Firestorm writes its own.
        _ = _logger.AppendAsync(tab.LogKind, tab.LogName, "", notice, DateTime.Now);
    }

    // Own messages get the same blue accent used for "selected" elsewhere in this window, so a
    // glance at the sender-name color tells the two apart -- matches the reviewed mockup's own-
    // vs-others distinction, without introducing a new accent color to the rest of the UI.
    private string FormatChatLine(DateTime timestamp, string sender, string message, Guid senderAgentId = default)
    {
        bool isOwn = !string.IsNullOrEmpty(_session?.AgentName) && sender == _session!.AgentName;
        string senderColor = isOwn ? "#79B8F0" : "#E0E0E0";
        string stamp = $"[color=#888888][lb]{timestamp:HH:mm}][/color]";

        // The Display Name when there is one. Only what is shown: the log line (AppendMessageToTab) and
        // the own-message test above keep the legacy name the network gave us.
        Guid nameId = senderAgentId;
        if (isOwn && nameId == Guid.Empty && Guid.TryParse(_session!.AgentId, out var ownId)) nameId = ownId;
        string shownSender = nameId != Guid.Empty ? NameDisplay.For(_session, nameId, sender) : sender;
        string name = $"[color={senderColor}][b]{BbEscape(shownSender)}[/b][/color]";
        // FEAT-UI-13: make a real resident's name a click target that opens their profile.
        if (senderAgentId != Guid.Empty)
            name = $"[url=avatar:{senderAgentId}]{name}[/url]";

        string myName = _session?.AgentName ?? string.Empty;
        string myDisplay = "";
        if (Guid.TryParse(_session?.AgentId, out var myGuid))
            myDisplay = NameDisplay.For(_session, myGuid, myName);

        Func<string, (Guid? AvatarId, string DisplayName)?>? resolveMention = null;
        if (_session != null)
        {
            resolveMention = mention =>
            {
                var friend = _session.GetFriends().FirstOrDefault(f =>
                    f.Name.Equals(mention, StringComparison.OrdinalIgnoreCase) ||
                    f.Name.Replace(" ", "").Equals(mention, StringComparison.OrdinalIgnoreCase));
                if (friend != null)
                {
                    string disp = NameDisplay.For(_session, friend.Id, friend.Name);
                    return (friend.Id, disp);
                }
                return null;
            };
        }

        string body = ChatTextParser.FormatMessageToBbCode(
            IsEmote(message) ? message[3..] : message,
            currentUserName: myName,
            currentDisplayName: myDisplay,
            resolveMention: resolveMention);

        // Emotes were being rendered like any other line, i.e. "Clif: /me waves" -- the literal
        // command text, with the colon still there. The sim doesn't transform "/me": it broadcasts
        // the message verbatim and every viewer formats it locally. Matching llchathistory.cpp
        // (appendMessage): the "/me " / "/me'" prefix marks an IRC-style message, the name/body
        // delimiter is dropped entirely, the body is italic, and exactly 3 characters are stripped
        // -- not 4 -- so the space in "/me waves" survives as the separator and "/me's hat"
        // renders as "Clif's hat".
        if (IsEmote(message))
            return $"{stamp} {name}[i]{body}[/i]";

        // [lb] escapes the literal "[" so Godot's BBCode parser doesn't try to read "[15:28]" as
        // a tag -- matches the bracketed timestamp style of preloaded lines from the log file.
        return $"{stamp} {name}: {body}";
    }

    private static bool IsEmote(string message) => ChatEmote.IsEmote(message);

    /// <summary>Neutralises BBCode in text that came from the network or from a translation.
    /// Uses domain-level ChatTextParser.EscapeBbCode.</summary>
    internal static string BbEscape(string s) => ChatTextParser.EscapeBbCode(s);

    // ---- Chat page: vertical conversation list (left) + message log/input (right) ----------

    // ---- resizable name list ---------------------------------------------------------------------

    private const float MinListWidth = 100f;
    private const float MaxListWidth = 420f;
    private const float MinConversationWidth = 240f;
    private const string ListWidthSection = "chat_window";
    private const string ListWidthKey = "list_width";

    private PanelContainer _listPanel = null!;
    private float _listWidth = 120f;

    /// <summary>The name list's width, from preferences.cfg (its own section, like the other per-feature
    /// settings), or the old fixed 120 px.</summary>
    private static float LoadListWidth()
    {
        var cfg = new ConfigFile();
        if (cfg.Load("user://preferences.cfg") != Error.Ok) return 120f;
        return Mathf.Clamp((float)cfg.GetValue(ListWidthSection, ListWidthKey, 120.0), MinListWidth, MaxListWidth);
    }

    private void SetListWidth(float width)
    {
        // The conversation keeps a usable width however far the handle is dragged.
        float max = Mathf.Max(MinListWidth, Mathf.Min(MaxListWidth, Size.X - MinConversationWidth));
        _listWidth = Mathf.Clamp(width, MinListWidth, max);
        _listPanel.CustomMinimumSize = new Vector2(_listWidth, 0);
    }

    private void SaveListWidth()
    {
        var cfg = new ConfigFile();
        cfg.Load("user://preferences.cfg"); // keep the sections other features own
        cfg.SetValue(ListWidthSection, ListWidthKey, _listWidth);
        cfg.Save("user://preferences.cfg");
    }

    /// <summary>The strip between the name list and the conversation. Dragging it resizes the list; the
    /// width is taken from the mouse position, not from accumulated motion, so it cannot drift.</summary>
    private partial class ListResizeHandle : Control
    {
        public Action<float>? Dragged;
        public Action? Released;
        private bool _dragging;

        public override void _GuiInput(InputEvent @event)
        {
            if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
            {
                _dragging = button.Pressed;
                if (!button.Pressed) Released?.Invoke();
                AcceptEvent();
            }
            else if (@event is InputEventMouseMotion && _dragging)
            {
                Dragged?.Invoke(GetGlobalMousePosition().X);
                AcceptEvent();
            }
        }
    }

    private Control BuildChatPage()
    {
        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 0);

        _listWidth = LoadListWidth();
        var listPanel = new PanelContainer { CustomMinimumSize = new Vector2(_listWidth, 0) };
        _listPanel = listPanel;
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

        // A drag handle between the name list and the conversation: long names do not fit the default width.
        var handle = new ListResizeHandle
        {
            CustomMinimumSize = new Vector2(8, 0),
            MouseDefaultCursorShape = CursorShape.Hsize,
            TooltipText = L10n.Tr("ui.chat.resize_list_tip"),
        };
        handle.Dragged = mouseX => SetListWidth(mouseX - _listPanel.GlobalPosition.X);
        handle.Released = SaveListWidth;
        hbox.AddChild(handle);

        var listVBox = new VBoxContainer();
        listVBox.AddThemeConstantOverride("separation", 4);
        listPanel.AddChild(listVBox);

        var sectionLabel = new Label { Text = L10n.Tr("ui.chat.contacts") };
        sectionLabel.AddThemeFontSizeOverride("font_size", MetaFontSize);
        sectionLabel.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
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
        // Left is the gap to the contact list and top the gap under the tab strip; right and
        // bottom are the window frame, whose inset comes from SLNGWindow.
        rightMargin.AddThemeConstantOverride("margin_left", 2);
        rightMargin.AddThemeConstantOverride("margin_top", 6);
        hbox.AddChild(rightMargin);

        var rightVBox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        rightVBox.AddThemeConstantOverride("separation", 6);
        rightMargin.AddChild(rightVBox);

        rightVBox.AddChild(BuildActionIconRow());

        _logView = new ChatLogRichTextLabel
        {
            OwnerWindow = this,
            BbcodeEnabled = true,
            ScrollFollowing = false, // manual pause/follow control -- see _Process
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            // RichTextLabel is non-selectable by default, so the chat log was read-only in the
            // literal sense -- you couldn't drag-select a line to copy a name/URL out of it.
            // Ctrl+C needs the label to be focusable too (ShortcutKeysEnabled only fires on the
            // focused control), and ContextMenuEnabled adds the right-click Copy entry.
            SelectionEnabled = true,
            ContextMenuEnabled = true,
            FocusMode = FocusModeEnum.Click,
        };
        // RichTextLabel keys normal/bold/italics/bold_italics separately -- missing any of them
        // falls back to the ~16px theme default, which is exactly what happened to the preloaded
        // history lines (they're wrapped in [i]...[/i] and only normal/bold were set here).
        _logView.AddThemeFontSizeOverride("normal_font_size", BodyFontSize);
        _logView.AddThemeFontSizeOverride("bold_font_size", BodyFontSize);
        _logView.AddThemeFontSizeOverride("italics_font_size", BodyFontSize);
        _logView.AddThemeFontSizeOverride("bold_italics_font_size", BodyFontSize);
        // FEAT-UI-13: resident names are emitted as [url=avatar:<guid>] links -- open the profile.
        _logView.MetaClicked += OnLogMetaClicked;
        var logRow = new HBoxContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        logRow.AddThemeConstantOverride("separation", 6);
        logRow.AddChild(_logView);
        logRow.AddChild(BuildParticipantsPanel());
        rightVBox.AddChild(logRow);

        _jumpToLatestButton = new Button
        {
            Text = L10n.Tr("ui.chat.jump_to_latest"),
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

        // "Name is typing…" -- the other side's typing indicator, between the log and the input.
        _peerTypingLabel = new Label { Visible = false, ClipText = true };
        _peerTypingLabel.AddThemeFontSizeOverride("font_size", MetaFontSize);
        _peerTypingLabel.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        rightVBox.AddChild(_peerTypingLabel);

        var inputRow = new HBoxContainer();
        inputRow.AddThemeConstantOverride("separation", 4);
        rightVBox.AddChild(inputRow);

        inputRow.AddChild(BuildIconButton("attach_file", L10n.Tr("ui.chat.attach"), null));

        _typingDebounceTimer = new Timer
        {
            WaitTime = 5.0,
            OneShot = true,
        };
        _typingDebounceTimer.Timeout += () => _session?.StopTyping();
        AddChild(_typingDebounceTimer);

        _inputEdit = new ChatLineEdit
        {
            OwnerWindow = this,
            PlaceholderText = "Write a message...",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            // Without this Godot ends the field's "editing" state the moment Enter submits: it keeps the focus (so
            // the avatar does not walk off) but ignores every key until the person clicks into it again --
            // LineEdit.unhandled_key_input returns at once while it is not editing. Reported as "the chat box goes
            // inactive after writing". OnSendPressed's GrabFocus cannot help: the field still has the focus, so
            // that call does nothing.
            KeepEditingOnTextSubmit = true,
        };
        _inputEdit.AddThemeFontSizeOverride("font_size", BodyFontSize);
        // FEAT-UI-43: the key dispatcher tells the chat bar from other text fields (Enter sends here).
        _inputEdit.AddToGroup(SLNG.App.KeyDispatcher.ChatInputGroup);
        _inputEdit.TextChanged += OnInputTextChanged;
        _inputEdit.FocusExited += () => StopTyping();
        _inputEdit.TextSubmitted += (_) => OnSendPressed();
        inputRow.AddChild(_inputEdit);

        Button? moodBtn = null;
        moodBtn = BuildIconButton("mood", L10n.Tr("ui.chat.emoji"), () => ToggleEmojiPicker(moodBtn!));
        inputRow.AddChild(moodBtn);

        Button? mentionBtn = null;
        mentionBtn = BuildIconButton("alternate_email", L10n.Tr("ui.chat.mention"), () =>
        {
            var candidates = GetMentionCandidates("");
            if (candidates.Count > 0)
            {
                _mentionQueryStartIndex = _inputEdit.CaretColumn;
                var editPos = _inputEdit.GetGlobalPosition();
                _mentionPicker?.ShowCandidates(new Vector2(editPos.X, editPos.Y), candidates);
            }
        });
        inputRow.AddChild(mentionBtn);

        _sendButton = BuildIconButton("send", L10n.Tr("ui.chat.send"), OnSendPressed);
        inputRow.AddChild(_sendButton);

        _mentionPicker = new ChatMentionPicker();
        _mentionPicker.OnMentionSelected = OnMentionCandidateSelected;
        AddChild(_mentionPicker);

        return hbox;
    }

    private void ToggleEmojiPicker(Button anchorButton)
    {
        if (_emojiPicker == null)
        {
            _emojiPicker = new EmojiPickerWindow();
            _emojiPicker.OnEmojiSelected = InsertEmoji;
            AddChild(_emojiPicker);
        }

        var globalPos = anchorButton.GetGlobalPosition();
        var targetPos = new Vector2(
            Mathf.Max(20f, globalPos.X - 260f),
            Mathf.Max(SLNGWindow.TopInset, globalPos.Y - 340f)
        );
        _emojiPicker.Toggle(targetPos);
    }

    private void InsertEmoji(string emoji)
    {
        int col = _inputEdit.CaretColumn;
        string text = _inputEdit.Text;
        if (col >= 0 && col <= text.Length)
        {
            _inputEdit.Text = text.Insert(col, emoji);
            _inputEdit.CaretColumn = col + emoji.Length;
        }
        else
        {
            _inputEdit.Text += emoji;
            _inputEdit.CaretColumn = _inputEdit.Text.Length;
        }
        _inputEdit.GrabFocus();
    }

    /// <summary>The conference member list beside the log: a header with the count, the names, and a right-click menu per
    /// member (profile, IM). Hidden until the people button of a conference tab is pressed.</summary>
    private Control BuildParticipantsPanel()
    {
        _participantsPanel = new PanelContainer
        {
            Visible = false,
            CustomMinimumSize = new Vector2(150, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        var box = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 4);
        _participantsPanel.AddChild(box);

        _participantsHeader = new Label();
        _participantsHeader.AddThemeFontSizeOverride("font_size", MetaFontSize);
        _participantsHeader.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        box.AddChild(_participantsHeader);

        _participantsList = new ItemList
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SelectMode = ItemList.SelectModeEnum.Single,
        };
        _participantsList.AddThemeFontSizeOverride("font_size", BodyFontSize);
        _participantsList.ItemClicked += OnParticipantClicked;
        box.AddChild(_participantsList);

        _participantsMenu = new PopupMenu();
        _participantsMenu.AddItem(L10n.Tr("ui.chat.participant_profile"), 0);
        _participantsMenu.AddItem(L10n.Tr("ui.chat.participant_im"), 1);
        _participantsMenu.IdPressed += OnParticipantMenuPressed;
        _participantsPanel.AddChild(_participantsMenu);
        return _participantsPanel;
    }

    private void ToggleParticipants()
    {
        if (_activeChatTab?.TargetConferenceId == null) return;
        _activeChatTab.ParticipantsShown = !_activeChatTab.ParticipantsShown;
        RefreshParticipants();
    }

    /// <summary>Shows or hides the people button and the member list for the selected tab and fills the list. Called when
    /// the tab changes, when the member list changes and when a name arrives.</summary>
    private void RefreshParticipants()
    {
        var tab = _activeChatTab;
        var conferenceId = tab?.TargetConferenceId;
        _participantsButton.Visible = conferenceId != null;
        bool show = conferenceId != null && tab!.ParticipantsShown;
        _participantsPanel.Visible = show;
        if (!show || _session == null) return;

        string self = _session.AgentId ?? "";
        var entries = _session.GetConferenceMembers(conferenceId!.Value)
            .Select(id => (Id: id, Name: NameDisplay.For(_session, id, "")))
            .Select(e => (e.Id, Name: string.IsNullOrWhiteSpace(e.Name) ? e.Id.ToString()[..8] : e.Name))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _participantsHeader.Text = string.Format(L10n.Tr("ui.chat.participants"), entries.Count);
        _participantsList.Clear();
        _participantIds.Clear();
        foreach (var (id, name) in entries)
        {
            // An id we know no legacy name for yet is asked for; the answer re-runs this through RefreshNames.
            if (!_session.TryGetCachedName(id, out _)) _session.RequestAvatarName(id);
            _participantsList.AddItem(id.ToString() == self ? $"{name} ({L10n.Tr("ui.chat.participant_you")})" : name);
            _participantIds.Add(id);
        }
    }

    private void OnParticipantClicked(long index, Vector2 atPosition, long mouseButtonIndex)
    {
        if (mouseButtonIndex != (long)MouseButton.Right || index < 0 || index >= _participantIds.Count) return;

        _participantMenuIndex = (int)index;
        bool isSelf = _participantIds[(int)index].ToString() == _session?.AgentId;
        _participantsMenu.SetItemDisabled(1, isSelf); // an IM to ourselves makes no sense
        _participantsMenu.Position = (Vector2I)(_participantsList.GetScreenPosition() + atPosition);
        _participantsMenu.Popup();
    }

    private void OnParticipantMenuPressed(long menuId)
    {
        if (_participantMenuIndex < 0 || _participantMenuIndex >= _participantIds.Count || _session == null) return;

        Guid id = _participantIds[_participantMenuIndex];
        string name = NameDisplay.For(_session, id, "");
        if (menuId == 0) OnOpenProfileRequested?.Invoke(id, name);
        else if (menuId == 1) OpenOrFocusImTab(id, NameDisplay.LegacyFor(_session, id, name));
    }

    /// <summary>The member list of a conference changed (somebody joined, left or spoke for the first time). Called by Boot
    /// on GridSession.ConferenceMembersChanged, marshalled to the main thread first.</summary>
    public void OnConferenceMembersChanged(Guid sessionId)
    {
        if (_activeChatTab?.TargetConferenceId == sessionId) RefreshParticipants();
    }

    /// <summary>Per-conversation action icons above the message log, plus conversation partner header
    /// allowing one-click profile viewing for IMs and Groups.</summary>
    private Control BuildActionIconRow()
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 6);

        _headerAvatarIcon = new TextureRect
        {
            CustomMinimumSize = new Vector2(24, 24),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Stop,
            TooltipText = L10n.Tr("ui.chat.participant_profile"),
            Texture = AvatarIcons.Placeholder,
            Visible = false,
        };
        _headerAvatarIcon.GuiInput += @event =>
        {
            if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                OnProfilePressed();
            }
        };
        row.AddChild(_headerAvatarIcon);

        _headerTitleButton = new Button
        {
            Flat = true,
            FocusMode = FocusModeEnum.None,
            Alignment = HorizontalAlignment.Left,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            TooltipText = L10n.Tr("ui.chat.participant_profile"),
            Visible = false,
        };
        _headerTitleButton.AddThemeFontSizeOverride("font_size", BodyFontSize);
        _headerTitleButton.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.9f));
        _headerTitleButton.AddThemeColorOverride("font_hover_color", new Color(1f, 1f, 1f));
        _headerTitleButton.Pressed += OnProfilePressed;
        row.AddChild(_headerTitleButton);

        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(spacer);

        _profileButton = BuildIconButton("person", L10n.Tr("ui.chat.participant_profile"), OnProfilePressed);
        _profileButton.Visible = false;
        row.AddChild(_profileButton);

        // Conference tabs only (see RefreshParticipants): who is in this conversation.
        _participantsButton = BuildIconButton("group", L10n.Tr("ui.chat.participants_toggle"), ToggleParticipants);
        _participantsButton.Visible = false;
        row.AddChild(_participantsButton);
        row.AddChild(BuildIconButton("history", L10n.Tr("ui.chat.history"), OnHistoryPressed));
        row.AddChild(BuildIconButton("card_giftcard", L10n.Tr("ui.chat.give_item"), OnGiveItemIconPressed));
        row.AddChild(BuildIconButton("call", L10n.Tr("ui.chat.voice_call"), null));
        row.AddChild(BuildIconButton("search", L10n.Tr("ui.chat.search"), null));

        return row;
    }

    private void OnProfilePressed()
    {
        if (_activeChatTab?.TargetAgentId is { } agentId)
        {
            OnOpenProfileRequested?.Invoke(agentId, _activeChatTab.DisplayName);
        }
        else if (_activeChatTab?.TargetGroupId is { } groupId)
        {
            OnOpenGroupInfoRequested?.Invoke(groupId, _activeChatTab.DisplayName);
        }
    }

    private void UpdateHeaderForTab(ChatTab? tab)
    {
        if (tab == null || !IsInstanceValid(_headerTitleButton)) return;

        if (tab.TargetAgentId != null)
        {
            _headerAvatarIcon.Visible = true;
            _headerAvatarIcon.Texture = tab.IconRect?.Texture ?? AvatarIcons.Placeholder;
            _headerTitleButton.Visible = true;
            _headerTitleButton.Text = tab.DisplayName;
            _headerTitleButton.Disabled = false;
            _headerTitleButton.TooltipText = L10n.Tr("ui.chat.participant_profile");
            _profileButton.Visible = true;
            _profileButton.TooltipText = L10n.Tr("ui.chat.participant_profile");
        }
        else if (tab.TargetGroupId != null)
        {
            _headerAvatarIcon.Visible = false;
            _headerTitleButton.Visible = true;
            _headerTitleButton.Text = tab.DisplayName;
            _headerTitleButton.Disabled = false;
            _headerTitleButton.TooltipText = L10n.Tr("ui.groups.action_profile");
            _profileButton.Visible = true;
            _profileButton.TooltipText = L10n.Tr("ui.groups.action_profile");
        }
        else
        {
            _headerAvatarIcon.Visible = false;
            _headerTitleButton.Visible = true;
            _headerTitleButton.Text = tab.DisplayName;
            _headerTitleButton.Disabled = true;
            _headerTitleButton.TooltipText = "";
            _profileButton.Visible = false;
        }
    }

    public bool HasActiveImTab() => _activeChatTab?.TargetAgentId != null;

    private void OnGiveItemIconPressed()
    {
        if (_activeChatTab?.TargetAgentId == null)
        {
            AppendSystemNotice(L10n.Tr("ui.chat.give_item_select_im"));
            return;
        }

        var invPanel = GetTree().Root.FindChild("InventoryPanel", true, false) as InventoryPanel;
        if (invPanel != null && !invPanel.Visible)
        {
            invPanel.Toggle();
        }
        AppendSystemNotice(L10n.TrFormat("ui.chat.give_item_instructions", _activeChatTab.DisplayName));
    }

    public void GiveInventoryItemToActiveTab(Guid itemId, string itemName, int assetType, bool isFolder)
    {
        if (_session == null || _activeChatTab?.TargetAgentId is not { } recipientId)
        {
            AppendSystemNotice(L10n.Tr("ui.chat.give_item_open_im"));
            return;
        }

        if (isFolder)
        {
            _ = _session.GiveFolderAsync(itemId, itemName, recipientId);
        }
        else
        {
            _ = _session.GiveItemAsync(itemId, itemName, assetType, recipientId);
        }

        AppendSystemNotice($"[System] '{itemName}' an {_activeChatTab.DisplayName} angeboten.");
    }

    private void AppendSystemNotice(string noticeText)
    {
        if (_activeChatTab != null)
        {
            AppendLineToTab(_activeChatTab, $"[color=#80c0ff]{noticeText}[/color]");
        }
    }

    public bool IsInventoryItemData(Variant data)
    {
        string str = data.VariantType == Variant.Type.String ? data.AsString() : (data.Obj?.ToString() ?? "");
        return str.StartsWith("slng_item|");
    }

    public void HandleInventoryDrop(Variant data)
    {
        string str = data.VariantType == Variant.Type.String ? data.AsString() : (data.Obj?.ToString() ?? "");
        if (!str.StartsWith("slng_item|")) return;

        var parts = str.Split('|');
        if (parts.Length < 6) return;

        if (!Guid.TryParse(parts[1], out var itemId)) return;
        string itemName = parts[2];
        bool canTransfer = bool.Parse(parts[3]);
        bool isFolder = bool.Parse(parts[4]);
        int assetType = int.Parse(parts[5]);

        if (_activeChatTab?.TargetAgentId is not { } recipientId)
        {
            AppendSystemNotice("[System] Bitte wähle zuerst einen IM-Tab mit dem Empfänger aus.");
            return;
        }

        if (!canTransfer)
        {
            AppendSystemNotice($"[System] '{itemName}' kann nicht übertragen werden (keine Transfer-Rechte).");
            return;
        }

        GiveInventoryItemToActiveTab(itemId, itemName, assetType, isFolder);
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data) => IsInventoryItemData(data);

    public override void _DropData(Vector2 atPosition, Variant data) => HandleInventoryDrop(data);

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

        // Sending is always an intentional "show me this" action -- jump back to the bottom even
        // if the user had scrolled up to read history, unlike an incoming message, which respects
        // wherever they currently are (see AppendLineToTab's FollowingBottom check). Main's own
        // message arrives asynchronously via the sim's echo (OnSendLocalChat doesn't append
        // anything itself), so this has to be set now rather than after some append call.
        _activeChatTab.FollowingBottom = true;
        ScrollLogToBottom();
        ShowJumpToLatest(false);

        if (_activeChatTab.Id == "main")
        {
            OnSendLocalChat?.Invoke(text);
        }
        else if (_activeChatTab.TargetAgentId is { } targetId)
        {
            _session?.SendInstantMessage(targetId, text);
            // The sim doesn't echo your own outgoing IM back through InstantMessageReceived --
            // every other viewer locally echoes what it just sent, so match that here.
            AppendMessageToTab(_activeChatTab, _session?.AgentName ?? "You", text);
            // Later, not now: where the grid says it itself (Second Life does, within a moment) that
            // notice is the real one and ours would only repeat it. Ours is the fallback for a grid
            // that stays silent (an OpenSim without an offline-message module).
            var warnTab = _activeChatTab;
            GetTree().CreateTimer(OfflineNoticeGraceSeconds).Timeout += () =>
            {
                if (IsInstanceValid(this) && _chatTabs.Contains(warnTab)) WarnIfTargetOffline(warnTab, targetId);
            };
        }
        else if (_activeChatTab.TargetConferenceId is { } conferenceId)
        {
            // Like group chat, the line comes back through the session and is shown then (the viewer echoes
            // locally only for a 1:1 IM), so nothing is appended here -- unless the grid stays silent
            // (BUG-NET-32: "I see the others but my own lines never show"): then ours is the fallback, and a
            // line that did not leave at all says so instead of vanishing.
            var confTab = _activeChatTab;
            if (_session?.SendConferenceMessage(conferenceId, text) != true)
            {
                AppendSystemNotice("[System] Nachricht nicht gesendet (keine Verbindung).");
            }
            else
            {
                int echoesBefore = confTab.OwnConferenceEchoes;
                GetTree().CreateTimer(ConferenceEchoGraceSeconds).Timeout += () =>
                {
                    if (!IsInstanceValid(this) || !_chatTabs.Contains(confTab) || confTab.OwnConferenceEchoes != echoesBefore) return;
                    AppendMessageToTab(confTab, _session?.AgentName ?? "You", text,
                        Guid.TryParse(_session?.AgentId, out var me) ? me : default);
                };
            }
        }
        else if (_activeChatTab.TargetGroupId is { } groupId)
        {
            _session?.SendGroupMessage(groupId, text);
            // Group chat DOES echo back to the sender (the session broadcasts to every member,
            // including us), so unlike the IM branch above this must not append locally -- doing
            // so would print every outgoing line twice.
        }

        _inputEdit.Text = "";
        StopTyping();

        // Keep keyboard focus in the input after sending (and, with KeepEditingOnTextSubmit on the field, keep it
        // taking text -- see where _inputEdit is built). AvatarController disables movement and
        // camera rotation while a LineEdit/TextEdit holds Godot's control focus (hasUiFocus), so
        // this used to ReleaseFocus() here to make sure movement came back after a send. But that
        // drops the user out of the chat bar mid-conversation: typing the next line then walks the
        // avatar instead of appending text -- reported live as "wenn ich schreibe und enter druecke
        // verliert das fenster den focus, wenn ich weiter schreibe laeuft der avatar los". Firestorm
        // behaves the same way as this does now: Enter sends and the chat bar stays focused, and you
        // leave it explicitly with Escape (handled in ChatLineEdit) or by clicking into the world
        // (handled in AvatarController._UnhandledInput).
        _inputEdit.GrabFocus();
    }

    private void OnHistoryPressed()
    {
        if (_activeChatTab == null) return;

        var win = new ChatHistoryWindow();
        win.OnOpenProfileRequested = (id, n) => OnOpenProfileRequested?.Invoke(id, n);
        win.OnTeleportRequested = (region, pos) => OnTeleportRequested?.Invoke(region, pos);
        GetParent().AddChild(win);
        win.Position = new Vector2(
            Mathf.Max(20f, Position.X + 30f),
            Mathf.Max(SLNGWindow.TopInset + 10f, Position.Y + 30f));
        win.EnsureOnScreen();
        win.Open(_logger, _activeChatTab.LogKind, _activeChatTab.LogName, _activeChatTab.DisplayName);
    }

    // RichTextLabel's word-wrapped line count / VScrollBar.MaxValue isn't final synchronously
    // inside AppendText/Clear, and a same-frame CallDeferred still isn't reliably late enough --
    // live-tested proof: a CallDeferred'd scroll after a multi-line bulk load (history preload)
    // landed partway down the log, not at the true bottom, because RichTextLabel hadn't finished
    // reflowing yet when the deferred call ran. Awaiting an actual ProcessFrame guarantees at
    // least one full render/layout pass has completed first; ScrollToLine (line-index based)
    // over the VScrollBar's pixel MaxValue sidesteps needing that value to be correct at all.
    private void ScrollLogToBottom() => _ = ScrollLogToBottomAsync();

    private async Task ScrollLogToBottomAsync()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        _logView.ScrollToLine(Math.Max(0, _logView.GetLineCount() - 1));
    }

    private void ShowJumpToLatest(bool show) => _jumpToLatestButton.Visible = show;

    // ---- Conversation list (inner vertical axis: Main + dynamic IM rows) --------------------

    /// <param name="isOnline">Presence dot next to the name -- green/grey like FriendsPanel's,
    /// omitted entirely when null (e.g. "Main" isn't a person, so it gets no dot). IM rows pass
    /// a value once Phase 1c wires them up to a friend/contact's live status.</param>
    // ---- manual sorting of the conversation list -----------------------------------------------------

    private const string ConversationDragPrefix = "slng-conversation:";

    // The conversation's name button is also what you grab to drag it: a <see cref="DragSortButton"/>, whose
    // delegates <see cref="AddChatTab"/> sets. DragData null means this row cannot be dragged ("Main").

    /// <summary>Moves a conversation to where another one is. The list keeps that order for the rest of the
    /// session (the open conversations are not restored at the next login, so there is nothing to save).
    /// "Main" always stays on top: a drop onto it puts the conversation right below it.</summary>
    private void MoveConversation(string draggedId, string targetId)
    {
        var dragged = _chatTabs.Find(t => t.Id == draggedId);
        var target = _chatTabs.Find(t => t.Id == targetId);
        if (dragged == null || target == null || dragged == target || !dragged.Closeable) return;

        // The dragged conversation takes the place the target has now: dropped upwards it ends up above the
        // target, dropped downwards below it. Both indices are read before anything moves; "Main" is not a
        // place to land, so a drop onto it means "first below Main".
        int tabIndex = Math.Max(1, _chatTabs.IndexOf(target));
        int rowIndex = target.Closeable ? target.RowPanel.GetIndex() : target.RowPanel.GetIndex() + 1;
        _chatTabs.Remove(dragged);
        _chatTabs.Insert(Math.Min(tabIndex, _chatTabs.Count), dragged);
        _conversationList.MoveChild(dragged.RowPanel, rowIndex);
    }

    private void OnNearbyAvatarsUpdated(object? sender, NearbyAvatarsEvent e)
    {
        lock (_nearbyAgentIds)
        {
            _nearbyAgentIds.Clear();
            if (e.Avatars != null)
            {
                foreach (var av in e.Avatars)
                {
                    if (av.AgentId != Guid.Empty)
                    {
                        _nearbyAgentIds.Add(av.AgentId);
                    }
                }
            }
        }
        CallDeferred(nameof(UpdateActiveTabHeader));
    }

    private void UpdateActiveTabHeader() => UpdateHeaderForTab(_activeChatTab);

    // ---- the other side is typing ----------------------------------------------------------------------

    private Label _peerTypingLabel = null!;
    private readonly System.Collections.Concurrent.ConcurrentQueue<InstantMessageTypingEvent> _peerTypingQueue = new();
    private int _peerTypingDrainQueued;

    /// <summary>How long a "typing" is believed without a refresh or a stop (the viewer re-sends while typing;
    /// a lost stop must not leave the indicator on).</summary>
    private const ulong PeerTypingTimeoutMsec = 10_000;

    /// <summary>A typing start/stop arrived (network thread): queued, applied on the main thread.</summary>
    private void OnPeerTypingEvent(object? sender, InstantMessageTypingEvent e)
    {
        _peerTypingQueue.Enqueue(e);
        if (System.Threading.Interlocked.Exchange(ref _peerTypingDrainQueued, 1) == 0)
            CallDeferred(nameof(DrainPeerTyping));
    }

    private void DrainPeerTyping()
    {
        System.Threading.Volatile.Write(ref _peerTypingDrainQueued, 0);
        while (_peerTypingQueue.TryDequeue(out var e))
        {
            if (e.Typing)
            {
                // As the reference viewer does: the conversation opens the moment somebody starts typing, before
                // anything has been sent. It does not take focus or raise the unread badge.
                string name = e.FromAgentName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    // The indicator may not carry a name; the cache may know it, else the tab is titled with the id
                    // and RefreshNames names it when the answer to this request arrives.
                    if (_session == null || !_session.TryGetCachedName(e.FromAgentId, out name!))
                    {
                        name = e.FromAgentId.ToString();
                        _session?.RequestAvatarName(e.FromAgentId);
                    }
                }
                var tab = GetOrCreateImTab(e.FromAgentId, name);
                SetPeerTyping(tab, true);
                if (!Visible)
                {
                    Visible = true;
                    EnsureOnScreen();
                }
                if (IsMinimized) Unminimize();
                SelectOuterTab(_chatPageControl);
                SelectChatTab(tab);
            }
            else
            {
                var tab = _chatTabs.Find(t => t.TargetAgentId == e.FromAgentId);
                if (tab != null) SetPeerTyping(tab, false);
            }
        }
    }

    private void SetPeerTyping(ChatTab tab, bool typing)
    {
        bool was = tab.PeerTypingUntilMsec != 0;
        tab.PeerTypingUntilMsec = typing ? Time.GetTicksMsec() + PeerTypingTimeoutMsec : 0;
        if (was == typing) { if (typing) UpdatePeerTypingLabel(); return; } // a refresh only moves the deadline

        // The mark in the row: a small "…" after the name, which also shows for a conversation that is not on screen.
        if (typing && tab.TypingMark == null)
        {
            tab.TypingMark = new Label { Text = "…", TooltipText = L10n.TrFormat("ui.chat.peer_typing", tab.DisplayName) };
            tab.TypingMark.AddThemeFontSizeOverride("font_size", MetaFontSize);
            tab.TypingMark.AddThemeColorOverride("font_color", new Color(0.45f, 0.72f, 0.95f));
            tab.Row.AddChild(tab.TypingMark);
            tab.Row.MoveChild(tab.TypingMark, tab.Label.GetIndex() + 1);
        }
        if (tab.TypingMark != null) tab.TypingMark.Visible = typing;
        UpdatePeerTypingLabel();
    }

    /// <summary>"Name is typing…" under the log of the conversation on screen.</summary>
    private void UpdatePeerTypingLabel()
    {
        if (_peerTypingLabel == null) return;
        var tab = _activeChatTab;
        bool show = tab != null && tab.PeerTypingUntilMsec != 0;
        _peerTypingLabel.Visible = show;
        if (show) _peerTypingLabel.Text = L10n.TrFormat("ui.chat.peer_typing", tab!.DisplayName);
    }

    private void ExpirePeerTyping()
    {
        ulong now = Time.GetTicksMsec();
        foreach (var tab in _chatTabs)
        {
            if (tab.PeerTypingUntilMsec != 0 && now >= tab.PeerTypingUntilMsec) SetPeerTyping(tab, false);
        }
    }

    private static Label MakePresenceDot(bool online)
    {
        var dot = new Label { Text = "●", VerticalAlignment = VerticalAlignment.Center };
        dot.AddThemeFontSizeOverride("font_size", 8);
        SetPresenceDotColor(dot, online);
        return dot;
    }

    private static void SetPresenceDotColor(Label dot, bool online)
        => dot.AddThemeColorOverride("font_color", online ? new Color(0.3f, 0.85f, 0.3f) : new Color(0.4f, 0.4f, 0.4f));

    // ---- friends coming and going ---------------------------------------------------------------------

    private readonly System.Collections.Concurrent.ConcurrentQueue<FriendStatusEvent> _friendStatusQueue = new();
    private int _friendStatusDrainQueued;

    /// <summary>A friend logged in or out (network thread): queued, applied on the main thread.</summary>
    private void OnFriendStatusChanged(object? sender, FriendStatusEvent e)
    {
        _friendStatusQueue.Enqueue(e);
        if (System.Threading.Interlocked.Exchange(ref _friendStatusDrainQueued, 1) == 0)
            CallDeferred(nameof(DrainFriendStatus));
    }

    private void DrainFriendStatus()
    {
        System.Threading.Volatile.Write(ref _friendStatusDrainQueued, 0);
        while (_friendStatusQueue.TryDequeue(out var e))
        {
            foreach (var tab in _chatTabs)
            {
                if (tab.TargetAgentId == e.FriendId) ApplyPresence(tab, e.IsOnline);
            }
        }
    }

    /// <summary>Updates an IM row's dot and, like the reference viewer, says so in the conversation: "Name is
    /// offline." / "Name is online." (<c>FriendOfflineNotification</c> / <c>FriendOnlineNotification</c> in its
    /// strings.xml). Only a real change counts -- the library also reports an offline friend going offline. The
    /// line is not a message: it does not raise the unread badge and is not written to the log.</summary>
    private void ApplyPresence(ChatTab tab, bool online)
    {
        if (tab.Online == online) return;
        tab.Online = online;

        if (tab.PresenceDot == null)
        {
            tab.PresenceDot = MakePresenceDot(online);
            tab.Row.AddChild(tab.PresenceDot);
            tab.Row.MoveChild(tab.PresenceDot, 0);
        }
        else
        {
            SetPresenceDotColor(tab.PresenceDot, online);
        }

        // Back online: the "is offline, they will see this later" notice may be shown again next time.
        if (online) tab.OfflineNoticeShown = false;

        string text = L10n.TrFormat(online ? "ui.chat.friend_online" : "ui.chat.friend_offline", tab.DisplayName);
        AppendLineToTab(tab, $"[color=#888888][lb]{DateTime.Now:HH:mm}][/color] [color=#888888][i]{BbEscape(text)}[/i][/color]",
            countUnread: false);
    }

    private ChatTab AddChatTab(string id, string displayName, ChatLogKind kind, bool closeable, bool? isOnline = null,
        Guid? iconAgentId = null)
    {
        var row = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _conversationList.AddChild(row);

        var inner = new HBoxContainer();
        inner.AddThemeConstantOverride("separation", 4);
        row.AddChild(inner);

        Label? presenceDot = null;
        if (isOnline.HasValue)
        {
            presenceDot = MakePresenceDot(isOnline.Value);
            inner.AddChild(presenceDot);
        }

        // The avatar's profile picture, as Firestorm's conversation list shows. The space is reserved at once
        // so the row does not jump when the picture arrives.
        TextureRect? iconRect = null;
        if (iconAgentId.HasValue)
        {
            iconRect = new TextureRect
            {
                CustomMinimumSize = new Vector2(AvatarIcons.IconSize, AvatarIcons.IconSize),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                MouseFilter = MouseFilterEnum.Stop,
                TooltipText = L10n.Tr("ui.chat.participant_profile"),
                Texture = AvatarIcons.Placeholder, // until the real picture arrives, and for an avatar with none
            };
            inner.AddChild(iconRect);
        }

        var label = new DragSortButton
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
            LogName = displayName,
            Label = label,
            IconRect = iconRect,
            PresenceDot = presenceDot,
            Row = inner,
            Online = isOnline,
            RowPanel = row,
            UnreadLabel = unreadLabel,
            LogKind = kind,
            Closeable = closeable,
        };
        _chatTabs.Add(tab);

        if (iconRect != null)
        {
            iconRect.GuiInput += @event =>
            {
                if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                {
                    if (tab.TargetAgentId is { } aid)
                    {
                        OnOpenProfileRequested?.Invoke(aid, tab.DisplayName);
                    }
                }
            };
        }

        label.Pressed += () => SelectChatTab(tab);
        label.GuiInput += @event =>
        {
            if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right })
            {
                ShowTabContextMenu(tab, (Vector2I)label.GetGlobalMousePosition());
            }
        };

        // Manual sorting: drag a conversation onto another to put it there. "Main" stays first and cannot be
        // dragged; everything else can.
        label.DragData = closeable ? ConversationDragPrefix + id : null;
        label.CanDrop = data => data.StartsWith(ConversationDragPrefix, StringComparison.Ordinal) && data != ConversationDragPrefix + id;
        label.Dropped = data => MoveConversation(data[ConversationDragPrefix.Length..], tab.Id);

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

    private void CloseChatTab(ChatTab tab, bool leaveSession = true)
    {
        if (!tab.Closeable) return;
        // Closing a group tab leaves its chat session, so the sim stops delivering it -- the
        // viewer's own behaviour, and without it a "closed" group would keep re-opening its tab
        // on the next message.
        if (leaveSession && tab.TargetGroupId is { } groupId) _session?.LeaveGroupChat(groupId);
        // Same for a conference (BUG-NET-32): a tab closed without leaving leaves a ghost membership on the grid.
        if (leaveSession && tab.TargetConferenceId is { } conferenceId) _session?.LeaveConference(conferenceId);

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
        StopTyping();
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
        UpdatePeerTypingLabel();
        RefreshParticipants();
        UpdateHeaderForTab(tab);
    }

    private void ShowTabContextMenu(ChatTab tab, Vector2I screenPos)
    {
        _contextMenuTab = tab;
        _tabContextMenu.Clear();

        if (tab.TargetAgentId != null)
        {
            _tabContextMenu.AddItem(L10n.Tr("ui.chat.participant_profile"), 0);
            _tabContextMenu.AddItem(L10n.Tr("ui.chat.history"), 1);
            _tabContextMenu.AddItem(L10n.Tr("ui.chat.give_item"), 2);
            if (tab.Closeable)
            {
                _tabContextMenu.AddSeparator();
                _tabContextMenu.AddItem(L10n.Tr("common.close"), 3);
            }
        }
        else if (tab.TargetGroupId != null)
        {
            _tabContextMenu.AddItem(L10n.Tr("ui.groups.action_profile"), 0);
            _tabContextMenu.AddItem(L10n.Tr("ui.chat.history"), 1);
            if (tab.Closeable)
            {
                _tabContextMenu.AddSeparator();
                _tabContextMenu.AddItem(L10n.Tr("common.close"), 3);
            }
        }
        else
        {
            _tabContextMenu.AddItem(L10n.Tr("ui.chat.history"), 1);
            if (tab.Closeable)
            {
                _tabContextMenu.AddSeparator();
                _tabContextMenu.AddItem(L10n.Tr("common.close"), 3);
            }
        }

        _tabContextMenu.Position = screenPos;
        _tabContextMenu.Popup();
    }

    private void OnTabContextMenuPressed(long id)
    {
        if (_contextMenuTab == null) return;
        if (id == 0)
        {
            if (_contextMenuTab.TargetAgentId is { } aid)
                OnOpenProfileRequested?.Invoke(aid, _contextMenuTab.DisplayName);
            else if (_contextMenuTab.TargetGroupId is { } gid)
                OnOpenGroupInfoRequested?.Invoke(gid, _contextMenuTab.DisplayName);
        }
        else if (id == 1)
        {
            OpenHistoryFor(_contextMenuTab.LogKind, _contextMenuTab.LogName);
        }
        else if (id == 2)
        {
            SelectChatTab(_contextMenuTab);
            OnGiveItemIconPressed();
        }
        else if (id == 3 && _contextMenuTab.Closeable)
        {
            CloseChatTab(_contextMenuTab);
        }
    }

    private void AppendLineToTab(ChatTab tab, string bbcodeLine, bool countUnread = true)
    {
        bool overflowed = tab.Lines.Count >= MaxLogLines;
        tab.Lines.Add(bbcodeLine);
        if (overflowed) tab.Lines.RemoveAt(0);

        // Unread when nobody is looking: another conversation, or the selected one while the window is open on another
        // page (Friends...) -- see ChatUnreadPolicy.
        bool selected = tab == _activeChatTab;
        if (ChatUnreadPolicy.CountsAsUnread(countUnread, selected, ChatPageHiddenBehindAnotherPage))
        {
            tab.UnreadCount++;
            UpdateUnreadBadge(tab);
        }
        if (!selected) return;

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

        // Only the gap under the tab strip. The sides and the bottom are the window's standard
        // content inset already (SLNGWindow), so a margin there would be a second inset on top of
        // it -- which is what this used to be (8 px each side and below).
        var pageMargin = new MarginContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        pageMargin.AddThemeConstantOverride("margin_top", 8);
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

        // The whole pill is the button: icon, name, badge and the padding around them. It used to be only the name
        // (a flat Button inside the pill), so a click on the icon or the edge of the tab did nothing and the tabs
        // were hard to hit.
        var pill = new PanelContainer { MouseDefaultCursorShape = CursorShape.PointingHand };
        _outerTabStrip.AddChild(pill);

        var inner = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        inner.AddThemeConstantOverride("separation", 6);
        pill.AddChild(inner);

        var icon = new Label { Text = iconGlyph, VerticalAlignment = VerticalAlignment.Center };
        icon.AddThemeFontOverride("font", _iconFont);
        icon.AddThemeFontSizeOverride("font_size", 15);
        inner.AddChild(icon);

        var label = new Label
        {
            Text = tabName,
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(0, 24),
        };
        label.AddThemeFontSizeOverride("font_size", LabelFontSize);
        inner.AddChild(label);

        var badge = new Label { Visible = false, VerticalAlignment = VerticalAlignment.Center };
        badge.AddThemeColorOverride("font_color", new Color(0.95f, 0.3f, 0.25f));
        badge.AddThemeFontSizeOverride("font_size", MetaFontSize + 1);
        inner.AddChild(badge);

        var tab = new OuterTab { Pill = pill, Icon = icon, Label = label, Badge = badge, Page = page, Selected = isFirst };
        _outerTabs.Add(tab);
        pill.GuiInput += @event =>
        {
            if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }) SelectOuterTab(page);
        };
        pill.MouseEntered += () => { tab.Hover = true; ApplyOuterTabStyle(tab); };
        pill.MouseExited += () => { tab.Hover = false; ApplyOuterTabStyle(tab); };

        ApplyOuterTabStyle(tab);
    }

    /// <summary>Keeps the Chat tab's badge at the number of unread messages across every conversation -- what the
    /// toolbar's chat button shows -- so a message that arrives while the Friends page is open is not invisible.
    /// Polled each frame (cheap: a few tabs, and the controls are only touched when the number changes), because the
    /// count changes in many places: a line, a tab selected, a tab closed.</summary>
    private void UpdateChatTabBadge()
    {
        int total = TotalUnread;
        if (total == _chatTabBadgeTotal) return;
        _chatTabBadgeTotal = total;

        var tab = _outerTabs.Find(t => t.Page == _chatPageControl);
        if (tab == null) return;
        tab.HasUnread = total > 0;
        tab.Badge.Visible = total > 0;
        tab.Badge.Text = total > UnreadCap ? "9+" : total.ToString();
        ApplyOuterTabStyle(tab);
    }

    /// <summary>True while the window is open and showing some other page (Friends, Groups, Recent) than the Chat
    /// page -- the one case where the conversation selected on the Chat page is not being looked at although it is
    /// the selected one.</summary>
    private bool ChatPageHiddenBehindAnotherPage => Visible && !IsMinimized && !_chatPageControl.Visible;

    // Rounded "pill" tabs -- the classic top tab bar look, distinct from the conversation list's
    // rounded-left rows so the two axes read as visually different kinds of navigation.
    private static void ApplyOuterTabStyle(OuterTab tab)
    {
        bool selected = tab.Selected;
        var style = new StyleBoxFlat
        {
            BgColor = selected ? new Color(1, 1, 1, 0.10f)
                : tab.Hover ? new Color(1, 1, 1, 0.06f)
                : new Color(0, 0, 0, 0),
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

        // Something waiting behind a tab you are not on: warm instead of grey, next to the badge's count.
        var fg = selected ? new Color(1, 1, 1)
            : tab.HasUnread ? new Color(1f, 0.78f, 0.55f)
            : tab.Hover ? new Color(1, 1, 1)
            : new Color(0.72f, 0.72f, 0.72f);
        tab.Icon.AddThemeColorOverride("font_color", fg);
        tab.Label.AddThemeColorOverride("font_color", fg);
    }

    private void SelectOuterTab(Control selectedPage)
    {
        foreach (var tab in _outerTabs)
        {
            bool selected = tab.Page == selectedPage;
            tab.Page.Visible = selected;
            tab.Selected = selected;
            ApplyOuterTabStyle(tab);
        }
        if (selectedPage != _chatPageControl) _mentionPicker?.HidePicker();
        // Back on the Chat page: what came in for the selected conversation meanwhile is on screen now.
        if (selectedPage == _chatPageControl && _activeChatTab is { UnreadCount: > 0 } active)
        {
            active.UnreadCount = 0;
            UpdateUnreadBadge(active);
        }
        if (selectedPage == _recentPanel) RefreshRecentPanel(); // previews are read when the page is shown
        if (selectedPage == _friendsPanel) _friendsPanel.RefreshIcons(); // pictures that arrived while it was hidden
        if (selectedPage == _groupsPanel) _session?.RequestGroups();
    }

    /// <summary>FEAT-UI-43: which of the window's three pages the keyboard shortcuts address. The
    /// window is one frame with an outer tab strip, so "Nearby Chat", "Friends" and "Groups" are
    /// pages of it, not windows of their own.</summary>
    public enum Page { Chat, Friends, Groups }

    private Control PageControl(Page page)
    {
        if (page == Page.Friends) return _friendsPanel;
        if (page == Page.Groups) return _groupsPanel;
        return _chatPageControl;
    }

    /// <summary>True while the window is open (not merely minimized) on <paramref name="page"/>.</summary>
    public bool IsShowing(Page page) => Visible && !IsMinimized && PageControl(page).Visible;

    /// <summary>Selects a page. The caller makes the window itself visible (the launcher does, so a
    /// minimized or hidden window takes the usual path).</summary>
    public void ShowPage(Page page) => SelectOuterTab(PageControl(page));

    /// <summary>For the selftest: whether the chat bar goes on taking text after Enter has sent a line.</summary>
    internal bool InputKeepsEditingOnSubmit => _inputEdit.KeepEditingOnTextSubmit;

    /// <summary>Puts the cursor in the chat bar on the Chat page - the "start typing" shortcut.</summary>
    public void FocusChatInput()
    {
        SelectOuterTab(_chatPageControl);
        _inputEdit.GrabFocus();
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
        headlineLabel.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
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

    private void OnInputTextChanged(string newText)
    {
        if (_activeChatTab?.Id == "main")
        {
            if (!string.IsNullOrEmpty(newText))
            {
                _session?.StartTyping();
                _typingDebounceTimer?.Start();
            }
            else
            {
                StopTyping();
            }
        }

        UpdateMentionAutocomplete(newText);
    }

    private void UpdateMentionAutocomplete(string newText)
    {
        if (string.IsNullOrEmpty(newText) || _inputEdit.CaretColumn <= 0)
        {
            _mentionPicker?.HidePicker();
            _mentionQueryStartIndex = -1;
            return;
        }

        int caret = _inputEdit.CaretColumn;
        int atIndex = -1;
        for (int i = caret - 1; i >= 0; i--)
        {
            char c = newText[i];
            if (c == '@')
            {
                if (i == 0 || char.IsWhiteSpace(newText[i - 1]))
                {
                    atIndex = i;
                }
                break;
            }
            if (char.IsWhiteSpace(c))
            {
                break;
            }
        }

        if (atIndex < 0)
        {
            _mentionPicker?.HidePicker();
            _mentionQueryStartIndex = -1;
            return;
        }

        _mentionQueryStartIndex = atIndex;
        string query = newText.Substring(atIndex + 1, caret - (atIndex + 1));

        var candidates = GetMentionCandidates(query);
        if (candidates.Count > 0)
        {
            var editPos = _inputEdit.GetGlobalPosition();
            var pickerPos = new Vector2(editPos.X + Mathf.Max(0, atIndex * 7), editPos.Y);
            _mentionPicker?.ShowCandidates(pickerPos, candidates);
        }
        else
        {
            _mentionPicker?.HidePicker();
        }
    }

    private List<string> GetMentionCandidates(string query)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        List<Guid> nearbyCopy;
        lock (_nearbyAgentIds)
        {
            nearbyCopy = new List<Guid>(_nearbyAgentIds);
        }

        foreach (var agentId in nearbyCopy)
        {
            if (agentId == Guid.Empty) continue;
            string name = NameDisplay.For(_session, agentId, "");
            if (string.IsNullOrWhiteSpace(name) && _session != null && _session.TryGetCachedName(agentId, out var cached))
            {
                name = cached;
            }
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
            {
                if (string.IsNullOrEmpty(query) || name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(name);
                }
            }
        }

        foreach (var speaker in _recentSpeakers)
        {
            if (!string.IsNullOrWhiteSpace(speaker) && seen.Add(speaker))
            {
                if (string.IsNullOrEmpty(query) || speaker.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(speaker);
                }
            }
        }

        if (_session != null)
        {
            foreach (var friend in _session.GetFriends())
            {
                string name = NameDisplay.For(_session, friend.Id, friend.Name);
                if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                {
                    if (string.IsNullOrEmpty(query) || name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(name);
                    }
                }
            }
        }

        return result;
    }

    private void OnMentionCandidateSelected(string name)
    {
        int col = _inputEdit.CaretColumn;
        string text = _inputEdit.Text;
        string insertName = name.Contains(' ') ? name.Replace(" ", ".") : name;
        string mention = $"@{insertName} ";

        if (_mentionQueryStartIndex >= 0 && _mentionQueryStartIndex <= col && _mentionQueryStartIndex <= text.Length)
        {
            string before = text[.._mentionQueryStartIndex];
            string after = col <= text.Length ? text[col..] : "";
            _inputEdit.Text = before + mention + after;
            _inputEdit.CaretColumn = before.Length + mention.Length;
        }
        else
        {
            if (col >= 0 && col <= text.Length)
            {
                _inputEdit.Text = text.Insert(col, mention);
                _inputEdit.CaretColumn = col + mention.Length;
            }
            else
            {
                _inputEdit.Text += mention;
                _inputEdit.CaretColumn = _inputEdit.Text.Length;
            }
        }

        _mentionQueryStartIndex = -1;
        _mentionPicker?.HidePicker();
        _inputEdit.GrabFocus();
    }

    public void StopTyping()
    {
        _typingDebounceTimer?.Stop();
        _session?.StopTyping();
    }

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationVisibilityChanged && !IsVisibleInTree())
        {
            StopTyping();
        }
    }
}
