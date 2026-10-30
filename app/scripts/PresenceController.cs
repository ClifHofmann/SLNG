using System;
using Godot;
using SLNG.Core.Presence;
using SLNG.Net;

namespace SLNG.App;

/// <summary>
/// FEAT-UI-75: owns the agent's availability -- Away, Do Not Disturb, the autorespond modes -- and the clock that goes
/// Away by itself. Boot creates one at start-up and hands it the session at every login; every change is pushed to
/// <see cref="GridSession.Presence"/>, which sets the Away flag on the sim and decides which instant messages are
/// answered (<c>SLNG.Core.Presence</c>), and to <see cref="Changed"/> for the menu and the readout.
///
/// <para>Idle and focus follow the reference viewer: a quiet spell of <see cref="PresenceSettings.AwayTimeoutSeconds"/>
/// sends the agent Away (<c>LLAppViewer::idle</c>), losing the window's focus does too while that timeout is on, and
/// regaining it, or any input once ten seconds have passed, brings the agent back (<c>LLViewerWindow::handleActivate</c>,
/// <c>handleKey</c>). <c>_Input</c> sees every event before the UI can consume it, so a click on a window counts.</para>
///
/// <para><b>One login at a time (BUG-UI-43).</b> The setup is per account (<see cref="PresenceSettings"/>), so the controller
/// holds the one of the login it is <see cref="BindAccount">bound</see> to and nothing else: <see cref="Attach"/> (before the
/// login, when the agent is not known yet) starts from the defaults, <see cref="BindAccount"/> loads the login's own setup
/// once the login has succeeded, and <see cref="Detach"/> at logout puts the defaults back, so the next avatar can never
/// inherit what the last one had in memory. Unbound, a change is applied but never written anywhere.</para>
/// </summary>
public partial class PresenceController : Node
{
    private GridSession? _session;
    private readonly AwayTracker _away = new();
    private PresenceConfig _config = new();
    private int _timeoutSeconds = PresenceSettings.DefaultAwayTimeoutSeconds;
    private bool _awayOnFocusLoss;
    private string? _gridSlug;
    private string? _agentId;

    /// <summary>Raised on the main thread whenever the configuration changed, for any reason.</summary>
    public event Action<PresenceConfig>? Changed;

    /// <summary>Raised on the main thread when the setup of another login (or none) was put in place, before
    /// <see cref="Changed"/>: a draft typed for the previous login is not this login's to keep.</summary>
    public event Action? AccountChanged;

    public PresenceConfig Config => _config;
    public int AwayTimeoutSeconds => _timeoutSeconds;
    public bool AwayOnFocusLoss => _awayOnFocusLoss;

    private static double Now => Time.GetTicksMsec() / 1000.0;

    /// <summary>Starts a login: the session gets the defaults (nothing is answered) until <see cref="BindAccount"/> knows
    /// whose setup to use, which is only after the login has succeeded.</summary>
    public void Attach(GridSession session)
    {
        _session = session;
        Reset();
        Push(persist: false);
    }

    /// <summary>The login succeeded: from now on this grid and agent are the tenant. Loads their saved setup (Away and Do Not
    /// Disturb off) -- seeding it once from the old shared section if they have none yet -- and applies it to the session.</summary>
    public void BindAccount(string gridSlug, string agentId)
    {
        _gridSlug = gridSlug;
        _agentId = agentId;
        Reset(PresenceSettings.Load(gridSlug, agentId));
        AccountChanged?.Invoke();
        Push(persist: false);
    }

    /// <summary>Logout or a session the grid ended: forget the login completely.</summary>
    public void Detach()
    {
        _session = null;
        Reset();
        AccountChanged?.Invoke();
        Changed?.Invoke(_config);
    }

    /// <summary>Takes <paramref name="data"/> (or the defaults when there is none) as the current state, with no login bound
    /// unless <see cref="BindAccount"/> sets one right after.</summary>
    private void Reset(PresenceSettingsData? data = null)
    {
        if (data == null)
        {
            _gridSlug = null;
            _agentId = null;
        }
        data ??= PresenceSettingsData.Defaults;
        _config = data.Config;
        _timeoutSeconds = data.AwayTimeoutSeconds;
        _awayOnFocusLoss = data.AwayOnFocusLoss;
        _away.ClearAway(Now); // also starts the quiet spell from now, not from the clock's zero
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

    /// <summary>"Answer instant messages while Away" -- takes effect at once, like every switch on the page.</summary>
    public void SetSendAwayResponse(bool on)
    {
        if (_config.SendAwayResponse == on) return;
        _config = _config with { SendAwayResponse = on };
        Push(persist: true);
    }

    public void SetAwayOnFocusLoss(bool on)
    {
        if (_awayOnFocusLoss == on) return;
        _awayOnFocusLoss = on;
        Push(persist: true);
    }

    /// <summary>Seconds of quiet before going Away by itself; 0 = never.</summary>
    public void SetTimeoutSeconds(int seconds)
    {
        if (_timeoutSeconds == seconds) return;
        _timeoutSeconds = seconds;
        Push(persist: true);
    }

    /// <summary>The four response texts, applied together by the page's Apply button.</summary>
    public void SetTexts(string away, string busy, string autorespond, string nonFriends)
    {
        _config = _config with
        {
            AwayMessage = away,
            BusyMessage = busy,
            AutorespondMessage = autorespond,
            NonFriendsMessage = nonFriends,
        };
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
        if (persist) PresenceSettings.Save(_gridSlug, _agentId, new PresenceSettingsData(_config, _timeoutSeconds, _awayOnFocusLoss));
        Changed?.Invoke(_config);
    }
}
