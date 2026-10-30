using System.Collections.Generic;
using Godot;
using SLNG.App.UI.Prefs;
using SLNG.Core.Presence;

namespace SLNG.App.UI;

/// <summary>
/// FEAT-UI-75: the "Away &amp; auto-response" section of the Chat &amp; IM category (FEAT-UI-79) -- when the viewer goes Away
/// by itself and the texts it answers a first instant message with (Firestorm: Preferences &gt; Chat &gt; Auto-response and
/// the away timeout). The modes themselves are switched from the Avatar menu; the checkboxes here are the same switches.
///
/// <para>Checkboxes and the timeout take effect the moment they are changed. Only the four texts are applied by a button,
/// because a half-typed text must never be what a stranger receives; Apply and Revert are enabled exactly while a
/// text differs from the one in use.</para>
/// </summary>
public sealed class AutoresponsePreferencesPage
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

    /// <summary>Fills <paramref name="section"/> (Chat &amp; IM, "away"), which Boot has already put in its place, and hands
    /// the page the controller. Call once, after the section is in the window's tree.</summary>
    public void Build(PrefsSection section, PresenceController presence)
    {
        _presence = presence;
        string prefix = $"{section.CategoryId}.{section.Id}";

        var timeoutLabels = new List<string>();
        foreach (int seconds in PresenceSettings.AwayTimeoutChoices) timeoutLabels.Add(TimeoutLabel(seconds));
        _timeout = section.AddOption($"{prefix}.timeout", "ui.presence.timeout", timeoutLabels, 2,
                idx =>
                {
                    if (!_refreshing) presence.SetTimeoutSeconds(PresenceSettings.AwayTimeoutChoices[idx]);
                }, hintKey: "ui.presence.timeout_hint")
            .ControlAs<OptionButton>()!;

        _awayOnFocusLoss = AddSwitch(section, $"{prefix}.away_on_focus_loss", "ui.presence.away_on_focus_loss",
            on => presence.SetAwayOnFocusLoss(on));

        _sendAway = AddSwitch(section, $"{prefix}.send_away", "ui.presence.send_away", on => presence.SetSendAwayResponse(on));
        _awayText = AddText(section, $"{prefix}.away_text");
        _busyText = AddText(section, $"{prefix}.busy_text", "ui.presence.busy_heading");
        _autorespond = AddSwitch(section, $"{prefix}.autorespond", "ui.presence.autorespond", on => presence.SetAutorespond(on));
        _autorespondText = AddText(section, $"{prefix}.autorespond_text");
        _nonFriends = AddSwitch(section, $"{prefix}.autorespond_non_friends", "ui.presence.autorespond_non_friends",
            on => presence.SetAutorespondNonFriends(on));
        _nonFriendsText = AddText(section, $"{prefix}.non_friends_text");

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", PrefsStyle.ColumnGap);
        _apply = new Button { Text = L10n.Tr("ui.presence.apply"), FocusMode = Control.FocusModeEnum.None, Disabled = true };
        _apply.Pressed += ApplyPressed;
        _revert = new Button { Text = L10n.Tr("ui.presence.revert"), FocusMode = Control.FocusModeEnum.None, Disabled = true };
        _revert.Pressed += RevertPressed;
        buttons.AddChild(_apply);
        buttons.AddChild(_revert);
        section.AddBlock($"{prefix}.buttons", buttons, null, false, "ui.presence.apply", "ui.presence.revert");

        // A change from outside (the Avatar menu's checks) shows here without waiting for the page to reopen.
        presence.Changed += _ => Refresh();
        // BUG-UI-43: another login's setup was put in place; a text typed for the last one is not this one's to keep.
        presence.AccountChanged += () =>
        {
            ShowTexts();
            Refresh();
        };
        Refresh();
    }

    private CheckBox AddSwitch(PrefsSection section, string id, string labelKey, System.Action<bool> onChanged)
        => section.AddCheck(id, labelKey, false, on =>
            {
                if (!_refreshing) onChanged(on);
            })
            .ControlAs<CheckBox>()!;

    /// <summary>A response text; typing in it only enables Apply and Revert.</summary>
    private TextEdit AddText(PrefsSection section, string id, string? labelKey = null)
    {
        var edit = new TextEdit
        {
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            CustomMinimumSize = new Vector2(300, 64),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        edit.TextChanged += UpdateButtons;
        section.AddBlock(id, edit, labelKey);
        return edit;
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
