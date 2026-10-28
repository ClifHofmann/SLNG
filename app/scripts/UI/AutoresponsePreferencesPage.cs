using System.Collections.Generic;
using Godot;
using SLNG.Core.Presence;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-UI-75: the Preferences tab for Away and the automatic IM responses -- when the viewer goes Away by itself and
/// the texts it answers a first instant message with (Firestorm: Preferences &gt; Chat &gt; Auto-response and the away
/// timeout). The modes themselves are switched from the Avatar menu; the checkboxes here are the same switches.
///
/// <para>Checkboxes and the timeout take effect the moment they are changed. Only the four texts are applied by a button,
/// because a half-typed text must never be what a stranger receives; Apply and Revert are enabled exactly while a
/// text differs from the one in use.</para>
/// </summary>
public partial class AutoresponsePreferencesPage : VBoxContainer
{
    private PresenceController? _presence;
    private OptionButton _timeout = null!;
    private CheckBox _awayOnFocusLoss = null!;
    private CheckBox _sendAway = null!;
    private CheckBox _autorespond = null!;
    private CheckBox _nonFriends = null!;
    private TextEdit _awayText = null!;
    private TextEdit _busyText = null!;
    private TextEdit _autorespondText = null!;
    private TextEdit _nonFriendsText = null!;
    private Button _apply = null!;
    private Button _revert = null!;

    // True while Refresh pushes the live state INTO the controls, so that does not loop back out as a user change.
    private bool _refreshing;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 6);
    }

    /// <summary>Builds the page and hands it the controller. Call once, right after PreferencesWindow.AddTab.</summary>
    public void Initialize(PresenceController presence)
    {
        _presence = presence;

        var timeoutRow = new HBoxContainer();
        timeoutRow.AddThemeConstantOverride("separation", 8);
        timeoutRow.AddChild(new Label { Text = L10n.Tr("ui.presence.timeout") });
        _timeout = new OptionButton { FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (int seconds in PresenceSettings.AwayTimeoutChoices)
            _timeout.AddItem(TimeoutLabel(seconds));
        _timeout.ItemSelected += idx =>
        {
            if (!_refreshing) _presence.SetTimeoutSeconds(PresenceSettings.AwayTimeoutChoices[(int)idx]);
        };
        timeoutRow.AddChild(_timeout);
        AddChild(timeoutRow);

        AddChild(Hint(L10n.Tr("ui.presence.timeout_hint")));

        _awayOnFocusLoss = AddSwitch(L10n.Tr("ui.presence.away_on_focus_loss"), on => _presence.SetAwayOnFocusLoss(on));

        _sendAway = AddSwitch(L10n.Tr("ui.presence.send_away"), on => _presence.SetSendAwayResponse(on));
        AddText(out _awayText);
        AddChild(new Label { Text = L10n.Tr("ui.presence.busy_heading") });
        AddText(out _busyText);
        _autorespond = AddSwitch(L10n.Tr("ui.presence.autorespond"), on => _presence.SetAutorespond(on));
        AddText(out _autorespondText);
        _nonFriends = AddSwitch(L10n.Tr("ui.presence.autorespond_non_friends"), on => _presence.SetAutorespondNonFriends(on));
        AddText(out _nonFriendsText);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        _apply = new Button { Text = L10n.Tr("ui.presence.apply"), FocusMode = FocusModeEnum.None, Disabled = true };
        _apply.Pressed += ApplyPressed;
        _revert = new Button { Text = L10n.Tr("ui.presence.revert"), FocusMode = FocusModeEnum.None, Disabled = true };
        _revert.Pressed += RevertPressed;
        buttons.AddChild(_apply);
        buttons.AddChild(_revert);
        AddChild(buttons);

        // A change from outside (the Avatar menu's checks) shows here without waiting for the page to reopen.
        presence.Changed += _ => Refresh();
        Refresh();
    }

    private static Label Hint(string text)
    {
        var label = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(300, 0), // a width floor: without it the wrapped height is measured at ~0 px
        };
        label.AddThemeFontSizeOverride("font_size", 11);
        label.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        return label;
    }

    private CheckBox AddSwitch(string text, System.Action<bool> onChanged)
    {
        var box = new CheckBox { Text = text, FocusMode = FocusModeEnum.None };
        box.Toggled += on =>
        {
            if (!_refreshing) onChanged(on);
        };
        AddChild(box);
        return box;
    }

    /// <summary>A response text; typing in it only enables Apply and Revert.</summary>
    private void AddText(out TextEdit text)
    {
        var edit = new TextEdit
        {
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            CustomMinimumSize = new Vector2(300, 64),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        edit.TextChanged += UpdateButtons;
        AddChild(edit);
        text = edit;
    }

    private static string TimeoutLabel(int seconds)
    {
        if (seconds == 0) return L10n.Tr("ui.presence.timeout_never");
        return seconds % 3600 == 0
            ? L10n.TrFormat("ui.presence.timeout_hours", seconds / 3600)
            : L10n.TrFormat("ui.presence.timeout_minutes", seconds / 60);
    }

    /// <summary>An emptied box stands for the default text rather than a blank line sent to strangers.</summary>
    private static string Resolve(TextEdit box, string fallback) =>
        string.IsNullOrWhiteSpace(box.Text) ? fallback : box.Text.Trim();

    private bool TextsDiffer()
    {
        if (_presence == null) return false;
        var c = _presence.Config;
        return Resolve(_awayText, PresenceConfig.DefaultAwayMessage) != c.AwayMessage
            || Resolve(_busyText, PresenceConfig.DefaultBusyMessage) != c.BusyMessage
            || Resolve(_autorespondText, PresenceConfig.DefaultAutorespondMessage) != c.AutorespondMessage
            || Resolve(_nonFriendsText, PresenceConfig.DefaultAutorespondMessage) != c.NonFriendsMessage;
    }

    private void UpdateButtons()
    {
        bool dirty = TextsDiffer();
        _apply.Disabled = !dirty;
        _revert.Disabled = !dirty;
    }

    /// <summary>Shows the live state. The switches always follow it; the texts only when nothing typed is waiting
    /// to be applied, so toggling a checkbox (or the Avatar menu) never throws a draft away.</summary>
    public void Refresh()
    {
        if (_presence == null) return;
        _refreshing = true;
        var c = _presence.Config;
        _awayOnFocusLoss.ButtonPressed = _presence.AwayOnFocusLoss;
        _sendAway.ButtonPressed = c.SendAwayResponse;
        _autorespond.ButtonPressed = c.Autorespond;
        _nonFriends.ButtonPressed = c.AutorespondNonFriends;

        int idx = new List<int>(PresenceSettings.AwayTimeoutChoices).IndexOf(_presence.AwayTimeoutSeconds);
        _timeout.Select(idx >= 0 ? idx : 2);

        if (!TextsDiffer()) ShowTexts();
        _refreshing = false;
        UpdateButtons();
    }

    private void ShowTexts()
    {
        var c = _presence!.Config;
        _awayText.Text = c.AwayMessage;
        _busyText.Text = c.BusyMessage;
        _autorespondText.Text = c.AutorespondMessage;
        _nonFriendsText.Text = c.NonFriendsMessage;
    }

    private void ApplyPressed()
    {
        if (_presence == null) return;
        _presence.SetTexts(
            Resolve(_awayText, PresenceConfig.DefaultAwayMessage),
            Resolve(_busyText, PresenceConfig.DefaultBusyMessage),
            Resolve(_autorespondText, PresenceConfig.DefaultAutorespondMessage),
            Resolve(_nonFriendsText, PresenceConfig.DefaultAutorespondMessage));
        ShowTexts(); // shows an emptied box as the default text it now stands for
        UpdateButtons();
    }

    private void RevertPressed()
    {
        if (_presence == null) return;
        ShowTexts();
        UpdateButtons();
    }
}
