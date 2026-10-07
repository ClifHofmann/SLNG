using Godot;
using System;
using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-UI-54: one group's info window -- the read-only profile the simulator sends
/// (<c>GroupProfileReply</c>) plus the <b>My settings</b> block with the three switches Firestorm's
/// group info has:
/// <list type="bullet">
/// <item><b>Receive group notices</b> and <b>List group in my profile</b>: server-side, both in the one
/// <c>SetGroupAcceptNotices</c> message, so they follow the account to every viewer. Shown from the
/// membership data (<see cref="ApplyMembership"/>) -- the profile reply does not carry them.</item>
/// <item><b>Receive group chat</b>: the single local switch, <see cref="GroupMuteSettings"/> (the
/// inverse of the Groups tab's Mute button -- one setting, so each follows the other).</item>
/// </list>
///
/// The window owns no network code: Boot wires the delegates below (profile request, notice flags,
/// name lookup) and feeds the answers back through the <c>Apply*</c> methods on the main thread. That
/// keeps it testable without a <c>GridSession</c> and keeps every network-thread hop in one place.
///
/// The profile has three states that must not be confused: still loading, loaded, and failed (the
/// simulator never answered). A failed request used to be indistinguishable from an empty group -- an
/// empty charter and zero members -- so the failed state says so and offers a retry.
/// </summary>
public partial class GroupInfoWindow : SLNGWindow
{
    /// <summary>How long to wait for <c>GroupProfileReply</c> before calling the request failed.</summary>
    public const double ProfileTimeoutSeconds = 10.0;

    public enum ProfileState { Loading, Loaded, Failed }

    /// <summary>Fired once this window has closed and freed itself, so the owner can drop it.</summary>
    public event Action? Closed;

    /// <summary>Set by the owner before <see cref="Initialize"/> so several windows cascade.</summary>
    public int CascadeIndex { get; set; }

    /// <summary>Sends the profile request; returns false when nothing could be sent (not connected).</summary>
    public Func<bool>? RequestProfile;

    /// <summary>Sends the two server-side switches (notices, list in profile); returns false when
    /// nothing was sent.</summary>
    public Func<bool, bool, bool>? SetNoticeFlags;

    /// <summary>Resolves an agent id to a name, or null when it is not known yet.</summary>
    public Func<Guid, string?>? TryGetName;

    /// <summary>Asks for an agent's name; the answer arrives through <see cref="OnNameResolved"/>.</summary>
    public Action<Guid>? RequestName;

    private Guid _groupId;
    private bool _closing;

    private Label _nameLabel = null!;
    private Label _stateLabel = null!;
    private Button _retryButton = null!;
    private Label _founderValue = null!;
    private Label _membersValue = null!;
    private Label _feeValue = null!;
    private Label _enrolmentValue = null!;
    private Label _maturityValue = null!;
    private Label _titleValue = null!;
    private Label _activeValue = null!;
    private LineEdit _insigniaEdit = null!;
    private TextEdit _charterEdit = null!;
    private CheckBox _receiveNotices = null!;
    private CheckBox _listInProfile = null!;
    private CheckBox _receiveChat = null!;
    private Label _settingsStatus = null!;

    private ProfileState _state = ProfileState.Loading;
    private double _waited;
    private GroupProfileInfo? _profile;
    private GroupEntry? _membership;
    private Guid _activeGroupId;
    private Guid _founderId;
    // What the server last confirmed (or what we last sent successfully): a failed send puts the
    // checkboxes back to this rather than leaving them showing a state that never happened.
    private bool _savedNotices;
    private bool _savedList;

    // ---- read by the selftest -----------------------------------------------------------------
    internal ProfileState State => _state;
    internal bool ReceiveNoticesChecked => _receiveNotices.ButtonPressed;
    internal bool ListInProfileChecked => _listInProfile.ButtonPressed;
    internal bool ReceiveChatChecked => _receiveChat.ButtonPressed;
    internal bool ReceiveNoticesEnabled => !_receiveNotices.Disabled;
    internal bool RetryVisible => _retryButton.Visible;
    internal string StateText => _stateLabel.Visible ? _stateLabel.Text : "";
    internal string FounderText => _founderValue.Text;
    internal string MembersText => _membersValue.Text;
    internal string FeeText => _feeValue.Text;
    internal string EnrolmentText => _enrolmentValue.Text;
    internal string MaturityText => _maturityValue.Text;
    internal string TitleText => _titleValue.Text;
    internal string ActiveText => _activeValue.Text;
    internal string InsigniaText => _insigniaEdit.Text;
    internal string CharterText => _charterEdit.Text;
    internal bool CharterEditable => _charterEdit.Editable;
    internal string SettingsStatusText => _settingsStatus.Visible ? _settingsStatus.Text : "";
    internal void PressReceiveNotices(bool on) => TogglePressed(_receiveNotices, on);
    internal void PressListInProfile(bool on) => TogglePressed(_listInProfile, on);
    internal void PressReceiveChat(bool on) => TogglePressed(_receiveChat, on);
    internal void PressRetry() => OnRetryPressed();

    /// <summary>Presses a check box the way a click does: change the state and emit the signal.</summary>
    private static void TogglePressed(CheckBox box, bool on)
    {
        box.ButtonPressed = on; // emits Toggled, which is what a click ends up in
    }

    public override void _Ready()
    {
        base._Ready();

        CustomMinimumSize = new Vector2(380, 0); // height 0 -> shrink-wraps its contents
        Size = CustomMinimumSize;

        // No PersistId: several groups can be open, and a window per group reopening at the last one's
        // spot is worse than cascading from the centre (same reasoning as GroupInvitationWindow).
        OnCloseRequested = Close;

        var vbox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        vbox.AddThemeConstantOverride("separation", 6);
        ContentContainer.AddChild(vbox);

        // Width floor on every autowrap label: a wrapped Label with no floor measures ~0 px wide and
        // makes the whole window hundreds of px too tall (the selftest fails any window over 900 px).
        _nameLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(220, 0),
        };
        _nameLabel.AddThemeFontSizeOverride("font_size", 16);
        vbox.AddChild(_nameLabel);

        var stateRow = new HBoxContainer();
        stateRow.AddThemeConstantOverride("separation", 8);
        _stateLabel = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(220, 0),
        };
        _stateLabel.AddThemeFontSizeOverride("font_size", 12);
        _stateLabel.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        stateRow.AddChild(_stateLabel);
        _retryButton = new Button { Text = L10n.Tr("ui.group_info.retry"), Visible = false, FocusMode = FocusModeEnum.None };
        _retryButton.Pressed += OnRetryPressed;
        stateRow.AddChild(_retryButton);
        vbox.AddChild(stateRow);

        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 3);
        _founderValue = AddRow(grid, "ui.group_info.founder");
        _membersValue = AddRow(grid, "ui.group_info.members");
        _feeValue = AddRow(grid, "ui.group_info.fee");
        _enrolmentValue = AddRow(grid, "ui.group_info.enrolment");
        _maturityValue = AddRow(grid, "ui.group_info.maturity");
        _titleValue = AddRow(grid, "ui.group_info.your_title");
        _activeValue = AddRow(grid, "ui.group_info.active");
        vbox.AddChild(grid);

        // The insignia is a texture id. Showing the picture would need the asset path (fetch, decode,
        // GPU cache pinning) that only the profile window has, privately; the id is selectable text.
        var insigniaRow = new HBoxContainer();
        insigniaRow.AddThemeConstantOverride("separation", 12);
        insigniaRow.AddChild(RowCaption("ui.group_info.insignia"));
        _insigniaEdit = new LineEdit { Editable = false, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(200, 0) };
        _insigniaEdit.AddThemeFontSizeOverride("font_size", 11);
        insigniaRow.AddChild(_insigniaEdit);
        vbox.AddChild(insigniaRow);

        vbox.AddChild(SectionCaption("ui.group_info.charter"));
        _charterEdit = new TextEdit
        {
            Editable = false,
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(220, 90),
        };
        _charterEdit.AddThemeFontSizeOverride("font_size", 12);
        vbox.AddChild(_charterEdit);

        vbox.AddChild(new HSeparator());
        vbox.AddChild(SectionCaption("ui.group_info.my_settings"));

        _receiveNotices = AddCheck(vbox, "ui.group_info.receive_notices", "ui.group_info.receive_notices_tip");
        _listInProfile = AddCheck(vbox, "ui.group_info.list_in_profile", "ui.group_info.list_in_profile_tip");
        _receiveChat = AddCheck(vbox, "ui.group_info.receive_chat", "ui.group_info.receive_chat_tip");
        _receiveNotices.Toggled += _ => OnNoticeFlagsToggled();
        _listInProfile.Toggled += _ => OnNoticeFlagsToggled();
        _receiveChat.Toggled += on => GroupMuteSettings.SetMuted(_groupId, !on);

        _settingsStatus = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(220, 0),
            Visible = false,
        };
        _settingsStatus.AddThemeFontSizeOverride("font_size", 11);
        _settingsStatus.AddThemeColorOverride("font_color", new Color(0.95f, 0.75f, 0.35f));
        vbox.AddChild(_settingsStatus);

        GroupMuteSettings.MuteChanged += OnMuteChanged;
    }

    public override void _ExitTree()
    {
        GroupMuteSettings.MuteChanged -= OnMuteChanged;
        base._ExitTree();
    }

    private static Label RowCaption(string key)
    {
        var l = new Label { Text = L10n.Tr(key), CustomMinimumSize = new Vector2(110, 0), SizeFlagsVertical = SizeFlags.ShrinkBegin };
        l.AddThemeFontSizeOverride("font_size", 12);
        l.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        return l;
    }

    private static Label SectionCaption(string key)
    {
        var l = new Label { Text = L10n.Tr(key) };
        l.AddThemeFontSizeOverride("font_size", 13);
        l.AddThemeColorOverride("font_color", UiTheme.SecondaryText);
        return l;
    }

    private static Label AddRow(GridContainer grid, string captionKey)
    {
        grid.AddChild(RowCaption(captionKey));
        var value = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(140, 0),
            Text = "—",
        };
        value.AddThemeFontSizeOverride("font_size", 12);
        grid.AddChild(value);
        return value;
    }

    private static CheckBox AddCheck(VBoxContainer parent, string textKey, string tipKey)
    {
        var box = new CheckBox { Text = L10n.Tr(textKey), TooltipText = L10n.Tr(tipKey), FocusMode = FocusModeEnum.None };
        box.AddThemeFontSizeOverride("font_size", 12);
        parent.AddChild(box);
        return box;
    }

    /// <summary>Shows the window for one group and asks for its profile. The group name is what the Groups
    /// list already knows; the profile replaces it when it arrives.</summary>
    public void Initialize(Guid groupId, string groupName)
    {
        _groupId = groupId;
        Title = L10n.Tr("ui.group_info.title");
        _nameLabel.Text = string.IsNullOrWhiteSpace(groupName) ? L10n.Tr("ui.group_info.unknown") : groupName;
        _insigniaEdit.Text = "";
        _charterEdit.Text = "";

        // The chat switch needs nothing from the network, so it is right from the first frame.
        _receiveChat.SetPressedNoSignal(!GroupMuteSettings.IsMuted(groupId));
        ApplyMembership(_membership); // no membership yet: the two server-side boxes start disabled

        BeginLoading();
        PositionWindow();
    }

    private void BeginLoading()
    {
        _state = ProfileState.Loading;
        _waited = 0;
        _stateLabel.Text = L10n.Tr("ui.group_info.loading");
        _stateLabel.Visible = true;
        _retryButton.Visible = false;

        if (RequestProfile?.Invoke() == false) Fail();
    }

    private void OnRetryPressed() => BeginLoading();

    private void Fail()
    {
        _state = ProfileState.Failed;
        _stateLabel.Text = L10n.Tr("ui.group_info.failed");
        _stateLabel.Visible = true;
        _retryButton.Visible = true;
    }

    public override void _Process(double delta)
    {
        if (_state != ProfileState.Loading) return;
        _waited += delta;
        if (_waited >= ProfileTimeoutSeconds) Fail();
    }

    /// <summary>The simulator's answer. Ignored when it is for another group (the library raises the
    /// event for every group profile reply).</summary>
    public void ApplyProfile(GroupProfileInfo p)
    {
        if (p.Id != _groupId) return;
        _profile = p;
        _state = ProfileState.Loaded;
        _stateLabel.Visible = false;
        _retryButton.Visible = false;

        if (!string.IsNullOrWhiteSpace(p.Name)) _nameLabel.Text = p.Name;
        _membersValue.Text = p.MemberCount.ToString();
        _feeValue.Text = p.MembershipFee > 0
            ? L10n.TrFormat("ui.group_info.fee_value", p.MembershipFee)
            : L10n.Tr("ui.group_info.fee_free");
        _enrolmentValue.Text = p.OpenEnrollment
            ? L10n.Tr("ui.group_info.enrolment_open")
            : L10n.Tr("ui.group_info.enrolment_invite");
        _maturityValue.Text = p.Mature
            ? L10n.Tr("ui.group_info.maturity_moderate")
            : L10n.Tr("ui.group_info.maturity_general");
        _insigniaEdit.Text = p.InsigniaId == Guid.Empty ? L10n.Tr("ui.group_info.insignia_none") : p.InsigniaId.ToString();
        _charterEdit.Text = string.IsNullOrWhiteSpace(p.Charter) ? L10n.Tr("ui.group_info.charter_empty") : p.Charter;
        ShrinkToContent(); // the status line and retry button just went away

        _founderId = p.FounderId;
        UpdateFounder();
        UpdateTitle();
        UpdateActive();
    }

    /// <summary>The agent's membership row for this group (null when not a member or not known yet).
    /// This is where the two server-side switches come from; the profile reply does not carry them.</summary>
    public void ApplyMembership(GroupEntry? entry)
    {
        _membership = entry;
        bool member = entry != null;
        _receiveNotices.Disabled = !member;
        _listInProfile.Disabled = !member;
        if (entry != null)
        {
            _savedNotices = entry.AcceptNotices;
            _savedList = entry.ListInProfile;
            _receiveNotices.SetPressedNoSignal(entry.AcceptNotices);
            _listInProfile.SetPressedNoSignal(entry.ListInProfile);
            _settingsStatus.Visible = false;
            ShrinkToContent();
        }
        else
        {
            _receiveNotices.SetPressedNoSignal(false);
            _listInProfile.SetPressedNoSignal(false);
        }
        UpdateTitle();
    }

    public void ApplyActiveGroup(Guid activeGroupId)
    {
        _activeGroupId = activeGroupId;
        UpdateActive();
    }

    /// <summary>Boot relays name replies here so the founder's name fills in when it arrives.</summary>
    public void OnNameResolved(Guid id, string name)
    {
        if (id != Guid.Empty && id == _founderId && !string.IsNullOrWhiteSpace(name)) _founderValue.Text = name;
    }

    private void UpdateFounder()
    {
        if (_founderId == Guid.Empty)
        {
            _founderValue.Text = L10n.Tr("ui.group_info.unknown");
            return;
        }
        string? name = TryGetName?.Invoke(_founderId);
        if (!string.IsNullOrWhiteSpace(name))
        {
            _founderValue.Text = name;
            return;
        }
        _founderValue.Text = L10n.Tr("ui.group_info.loading_name");
        RequestName?.Invoke(_founderId);
    }

    private void UpdateTitle()
    {
        // The membership row's title when it has one, else whatever the profile reply sent.
        string title = !string.IsNullOrWhiteSpace(_membership?.MemberTitle)
            ? _membership!.MemberTitle
            : _profile?.MemberTitle ?? "";
        _titleValue.Text = string.IsNullOrWhiteSpace(title) ? "—" : title;
    }

    private void UpdateActive()
    {
        _activeValue.Text = _activeGroupId != Guid.Empty && _activeGroupId == _groupId
            ? L10n.Tr("ui.group_info.yes")
            : L10n.Tr("ui.group_info.no");
    }

    private void OnNoticeFlagsToggled()
    {
        bool notices = _receiveNotices.ButtonPressed;
        bool list = _listInProfile.ButtonPressed;
        bool sent = SetNoticeFlags?.Invoke(notices, list) ?? false;
        if (sent)
        {
            _savedNotices = notices;
            _savedList = list;
            _settingsStatus.Visible = false;
            ShrinkToContent();
            return;
        }

        // Nothing was sent, so the boxes must not keep showing a state that never happened.
        _receiveNotices.SetPressedNoSignal(_savedNotices);
        _listInProfile.SetPressedNoSignal(_savedList);
        _settingsStatus.Text = L10n.Tr("ui.group_info.settings_failed");
        _settingsStatus.Visible = true;
    }

    /// <summary>The Receive-chat switch changed from somewhere else (the Groups tab's Mute button).</summary>
    private void OnMuteChanged(Guid groupId, bool muted)
    {
        if (groupId == _groupId) _receiveChat.SetPressedNoSignal(!muted);
    }

    /// <summary>A window only grows by itself, so a line that disappeared (loading, retry, the save
    /// error) would leave an empty strip at the bottom. Size below the content's minimum is clamped up,
    /// so asking for height 0 gives exactly the content.</summary>
    private void ShrinkToContent() => Size = new Vector2(Size.X, 0);

    private void PositionWindow()
    {
        var viewportSize = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280, 720);
        Position = new Vector2(
            Mathf.Max(0, (viewportSize.X - Size.X) / 2) + CascadeIndex * 28,
            Mathf.Max(TopInset + 10f, (viewportSize.Y - Size.Y) / 3) + CascadeIndex * 28);
        ClampToViewport();
    }

    private void Close()
    {
        if (_closing) return;
        _closing = true;
        Closed?.Invoke();
        QueueFree();
    }
}
