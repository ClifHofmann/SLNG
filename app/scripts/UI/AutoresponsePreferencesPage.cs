using System.Collections.Generic;
using Godot;
using SLNG.Core.Presence;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-UI-75: the Preferences tab for Away and the automatic IM responses -- when the viewer goes Away by itself and
/// the texts it answers a first instant message with (Firestorm: Preferences &gt; Chat &gt; Auto-response and the away
/// timeout). The modes themselves are switched from the Avatar menu; the checkboxes here are the same switches.
/// Nothing is applied until Apply, so a half-typed text is never what a stranger receives.
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
        timeoutRow.AddChild(_timeout);
        AddChild(timeoutRow);

        AddChild(Hint(L10n.Tr("ui.presence.timeout_hint")));

        _awayOnFocusLoss = new CheckBox { Text = L10n.Tr("ui.presence.away_on_focus_loss"), FocusMode = FocusModeEnum.None };
        AddChild(_awayOnFocusLoss);

        _sendAway = AddSection(L10n.Tr("ui.presence.send_away"), out _awayText, withSwitch: true);
        AddSection(L10n.Tr("ui.presence.busy_heading"), out _busyText, withSwitch: false);
        _autorespond = AddSection(L10n.Tr("ui.presence.autorespond"), out _autorespondText, withSwitch: true);
        _nonFriends = AddSection(L10n.Tr("ui.presence.autorespond_non_friends"), out _nonFriendsText, withSwitch: true);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        var apply = new Button { Text = L10n.Tr("ui.presence.apply"), FocusMode = FocusModeEnum.None };
        apply.Pressed += ApplyPressed;
        var defaults = new Button { Text = L10n.Tr("ui.presence.defaults"), FocusMode = FocusModeEnum.None };
        defaults.Pressed += DefaultsPressed;
        buttons.AddChild(apply);
        buttons.AddChild(defaults);
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

    private CheckBox AddSection(string heading, out TextEdit text, bool withSwitch)
    {
        var sw = new CheckBox { Text = heading, FocusMode = FocusModeEnum.None };
        if (withSwitch) AddChild(sw);
        else AddChild(new Label { Text = heading });

        text = new TextEdit
        {
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            CustomMinimumSize = new Vector2(300, 64),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        AddChild(text);
        return sw;
    }

    private static string TimeoutLabel(int seconds)
    {
        if (seconds == 0) return L10n.Tr("ui.presence.timeout_never");
        return seconds % 3600 == 0
            ? L10n.TrFormat("ui.presence.timeout_hours", seconds / 3600)
            : L10n.TrFormat("ui.presence.timeout_minutes", seconds / 60);
    }

    /// <summary>Pushes the live configuration into the controls, dropping whatever was typed and not applied.
    /// Called when Preferences opens, so it never shows a stale state.</summary>
    public void Refresh()
    {
        if (_presence == null) return;
        var c = _presence.Config;
        _awayOnFocusLoss.ButtonPressed = _presence.AwayOnFocusLoss;
        _sendAway.ButtonPressed = c.SendAwayResponse;
        _autorespond.ButtonPressed = c.Autorespond;
        _nonFriends.ButtonPressed = c.AutorespondNonFriends;
        _awayText.Text = c.AwayMessage;
        _busyText.Text = c.BusyMessage;
        _autorespondText.Text = c.AutorespondMessage;
        _nonFriendsText.Text = c.NonFriendsMessage;

        int idx = new List<int>(PresenceSettings.AwayTimeoutChoices).IndexOf(_presence.AwayTimeoutSeconds);
        _timeout.Select(idx >= 0 ? idx : 2);
    }

    private void ApplyPressed()
    {
        if (_presence == null) return;
        // An emptied box falls back to the default text rather than sending a blank line to strangers.
        string Or(TextEdit box, string fallback) => string.IsNullOrWhiteSpace(box.Text) ? fallback : box.Text.Trim();
        var edited = _presence.Config with
        {
            SendAwayResponse = _sendAway.ButtonPressed,
            Autorespond = _autorespond.ButtonPressed,
            AutorespondNonFriends = _nonFriends.ButtonPressed,
            AwayMessage = Or(_awayText, PresenceConfig.DefaultAwayMessage),
            BusyMessage = Or(_busyText, PresenceConfig.DefaultBusyMessage),
            AutorespondMessage = Or(_autorespondText, PresenceConfig.DefaultAutorespondMessage),
            NonFriendsMessage = Or(_nonFriendsText, PresenceConfig.DefaultAutorespondMessage),
        };
        _presence.Apply(edited, PresenceSettings.AwayTimeoutChoices[_timeout.Selected], _awayOnFocusLoss.ButtonPressed);
    }

    private void DefaultsPressed()
    {
        _awayText.Text = PresenceConfig.DefaultAwayMessage;
        _busyText.Text = PresenceConfig.DefaultBusyMessage;
        _autorespondText.Text = PresenceConfig.DefaultAutorespondMessage;
        _nonFriendsText.Text = PresenceConfig.DefaultAutorespondMessage;
    }
}
