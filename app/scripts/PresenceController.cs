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

    /// <summary>Raised on the main thread whenever the configuration changed, for any reason.</summary>
    public event Action<PresenceConfig>? Changed;

    public PresenceConfig Config => _config;
    public int AwayTimeoutSeconds => _timeoutSeconds;

    private static double Now => Time.GetTicksMsec() / 1000.0;

    /// <summary>Starts from the saved setup (Away and Do Not Disturb off) and applies it to the session.</summary>
    public void Attach(GridSession session)
    {
        _session = session;
        _config = PresenceSettings.Load();
        _timeoutSeconds = PresenceSettings.AwayTimeoutSeconds;
        _away.ClearAway(Now); // also starts the quiet spell from now, not from the clock's zero
        Push(persist: false);
    }

    public void SetAway(bool away)
    {
        bool changed = away ? _away.SetAway(Now) : _away.ClearAway(Now);
        if (changed) Sync();
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

    /// <summary>The settings window's Apply: texts, the "answer when Away" switch and the timeout.</summary>
    public void Apply(PresenceConfig edited, int timeoutSeconds)
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
        Push(persist: true);
    }

    /// <summary>Back to available: leaves Away and Do Not Disturb (the readout's click).</summary>
    public void ClearStatus()
    {
        _away.ClearAway(Now);
        _config = _config with { Away = false, DoNotDisturb = false };
        Push(persist: false);
    }

    public override void _Input(InputEvent @event)
    {
        if (_session == null) return;
        bool sign = @event is InputEventMouseMotion
            || @event is InputEventKey { Pressed: true }
            || @event is InputEventMouseButton { Pressed: true };
        if (sign && _away.OnActivity(Now)) Sync();
    }

    public override void _Process(double delta)
    {
        if (_session != null && _away.Tick(Now, _timeoutSeconds)) Sync();
    }

    public override void _Notification(int what)
    {
        if (_session == null) return;
        if (what == NotificationApplicationFocusOut)
        {
            // The reference viewer goes Away when it loses focus, but only when the idle timeout is on.
            if (_timeoutSeconds > 0 && _away.SetAway(Now)) Sync();
        }
        else if (what == NotificationApplicationFocusIn)
        {
            if (_away.ClearAway(Now)) Sync();
        }
    }

    /// <summary>The tracker changed Away: copy it into the configuration.</summary>
    private void Sync()
    {
        if (_config.Away == _away.IsAway) return;
        _config = _config with { Away = _away.IsAway };
        Push(persist: false);
    }

    private void Push(bool persist)
    {
        if (_session != null) _session.Presence = _config;
        if (persist) PresenceSettings.Save(_config, _timeoutSeconds);
        Changed?.Invoke(_config);
    }
}
