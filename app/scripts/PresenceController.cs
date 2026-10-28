using System;
using Godot;
using SLNG.Core.Presence;
using SLNG.Net;

namespace SLNG.App;

/// <summary>
/// FEAT-UI-75: owns the agent's availability -- Away, Do Not Disturb, the autorespond modes -- and the clock that goes
/// Away by itself. Boot creates one after login and hands it the session; every change is pushed to
/// <see cref="GridSession.Presence"/>, which sets the Away flag on the sim and decides which instant messages are
/// answered (<c>SLNG.Core.Presence</c>), and to <see cref="Changed"/> for the menu and the readout.
///
/// <para>Idle and focus follow the reference viewer: a quiet spell of <see cref="PresenceSettings.AwayTimeoutSeconds"/>
/// sends the agent Away (<c>LLAppViewer::idle</c>), losing the window's focus does too while that timeout is on, and
/// regaining it, or any input once ten seconds have passed, brings the agent back (<c>LLViewerWindow::handleActivate</c>,
/// <c>handleKey</c>). <c>_Input</c> sees every event before the UI can consume it, so a click on a window counts.</para>
/// </summary>
public partial class PresenceController : Node
{
    private GridSession? _session;
    private readonly AwayTracker _away = new();
    private PresenceConfig _config = new();
    private int _timeoutSeconds = PresenceSettings.DefaultAwayTimeoutSeconds;
    private bool _awayOnFocusLoss;

    /// <summary>Raised on the main thread whenever the configuration changed, for any reason.</summary>
    public event Action<PresenceConfig>? Changed;

    public PresenceConfig Config => _config;
    public int AwayTimeoutSeconds => _timeoutSeconds;
    public bool AwayOnFocusLoss => _awayOnFocusLoss;

    private static double Now => Time.GetTicksMsec() / 1000.0;

    /// <summary>Starts from the saved setup (Away and Do Not Disturb off) and applies it to the session.</summary>
    public void Attach(GridSession session)
    {
        _session = session;
        _config = PresenceSettings.Load();
        _timeoutSeconds = PresenceSettings.AwayTimeoutSeconds;
        _awayOnFocusLoss = PresenceSettings.AwayOnFocusLoss;
        _away.ClearAway(Now); // also starts the quiet spell from now, not from the clock's zero
        Push(persist: false);
    }

    public void SetAway(bool away)
    {
        bool changed = away ? _away.SetAway(Now) : _away.ClearAway(Now);
        if (changed) Sync("menu");
    }

    public void SetDoNotDisturb(bool on)
    {
        if (_config.DoNotDisturb == on) return;
        _config = _config with { DoNotDisturb = on };
        Push(persist: false);
    }

    public void SetAutorespond(bool on)
    {
        if (_config.Autorespond == on) return;
        _config = _config with { Autorespond = on };
        Push(persist: true);
    }

    public void SetAutorespondNonFriends(bool on)
    {
        if (_config.AutorespondNonFriends == on) return;
        _config = _config with { AutorespondNonFriends = on };
        Push(persist: true);
    }

    /// <summary>The settings page's Apply: texts, the "answer when Away" switch, the timeout and the focus-loss switch.</summary>
    public void Apply(PresenceConfig edited, int timeoutSeconds, bool awayOnFocusLoss)
    {
        _config = _config with
        {
            SendAwayResponse = edited.SendAwayResponse,
            AwayMessage = edited.AwayMessage,
            BusyMessage = edited.BusyMessage,
            AutorespondMessage = edited.AutorespondMessage,
            NonFriendsMessage = edited.NonFriendsMessage,
            Autorespond = edited.Autorespond,
            AutorespondNonFriends = edited.AutorespondNonFriends,
        };
        _timeoutSeconds = timeoutSeconds;
        _awayOnFocusLoss = awayOnFocusLoss;
        Push(persist: true);
    }

    /// <summary>Back to available: leaves Away and Do Not Disturb (the readout's click).</summary>
    public void ClearStatus()
    {
        _away.ClearAway(Now);
        _config = _config with { Away = false, DoNotDisturb = false };
        GD.Print("[Presence] away off (status readout)");
        Push(persist: false);
    }

    public override void _Input(InputEvent @event)
    {
        if (_session == null) return;
        bool sign = @event is InputEventMouseMotion
            || @event is InputEventKey { Pressed: true }
            || @event is InputEventMouseButton { Pressed: true };
        if (sign && _away.OnActivity(Now)) Sync("input");
    }

    public override void _Process(double delta)
    {
        if (_session != null && _away.Tick(Now, _timeoutSeconds)) Sync("idle");
    }

    public override void _Notification(int what)
    {
        if (_session == null) return;
        if (what == NotificationApplicationFocusOut)
        {
            // The reference viewer goes Away when it loses focus, but only when the idle timeout is on. Here it is
            // a separate, default-off choice (see PresenceSettings.AwayOnFocusLoss).
            if (_awayOnFocusLoss && _timeoutSeconds > 0 && _away.SetAway(Now)) Sync("focus lost");
        }
        else if (what == NotificationApplicationFocusIn)
        {
            if (_away.ClearAway(Now)) Sync("focus regained");
        }
    }

    /// <summary>The tracker changed Away: copy it into the configuration. The reason is logged: an avatar that
    /// shows Away "by itself" is otherwise impossible to account for.</summary>
    private void Sync(string reason)
    {
        if (_config.Away == _away.IsAway) return;
        _config = _config with { Away = _away.IsAway };
        GD.Print($"[Presence] away {(_away.IsAway ? "on" : "off")} ({reason})");
        Push(persist: false);
    }

    private void Push(bool persist)
    {
        if (_session != null) _session.Presence = _config;
        if (persist) PresenceSettings.Save(_config, _timeoutSeconds, _awayOnFocusLoss);
        Changed?.Invoke(_config);
    }
}
