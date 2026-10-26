using System;
using Godot;
using SLNG.App;
using SLNG.App.UI;
using SLNG.Core.Login;
using SLNG.Net;

// Boot, start-location part (FEAT-UI-72): the "Start at" row on the login screen, the per-profile favorites cache that feeds it,
// and the notice (plus ONE teleport attempt) when the grid does not start the agent where it was asked to.
//
// Everything the login screen shows comes from logins.cfg, read on the main thread; nothing here waits on the network. The
// favorites are written the other way round: a background resolver (LoginStartFavorites) works out region and position for each
// landmark in the inventory Favorites folder DURING a session and hands the result over as a snapshot, which _Process writes
// to the ConfigFile on the main thread (a ConfigFile is not thread-safe).
public partial class Boot
{
    /// <summary>The "Start at" row, directly under the profile dropdown.</summary>
    private LoginStartPicker _startPicker = null!;

    /// <summary>The <c>logins.cfg</c> section the picker currently shows; "" for an account the viewer has not saved.</summary>
    private string _startPickerSection = "";

    /// <summary>True from the login click until a failed login brings the form back (or the session is left): the Login button
    /// stays disabled meanwhile, whatever the start picker says.</summary>
    private bool _loginInFlight;

    private LoginStartFavorites? _loginStartFavorites;

    // What this login asked for -- set at the click, read when the grid has answered.
    private StartChoice _sessionStartChoice = StartChoice.Last;
    private StartTarget? _sessionStartTarget;
    /// <summary>How the requested place is named in a notice: the favorite's name, or the typed region's name.</summary>
    private string _sessionStartPlace = "";

    /// <summary>The <c>logins.cfg</c> section the favorites cache is written to this session: the profile's key when "Save Login"
    /// was ticked, otherwise "" (nothing is kept). Taken from the saved key, not from the form fields, which the person may have
    /// changed or which differ in whitespace.</summary>
    private string _sessionStartCacheKey = "";

    // What the grid answered, and the one-shot machinery that judges it.
    private string? _sessionReplyStartLocation;
    private bool _sessionFirstLogin;
    private bool _startLandingPending;
    private ulong _startLandingGiveUpAtMsec;
    private ulong _startLandingPositionDeadlineMsec;
    private string? _startNotice;
    private bool _startRetryWanted;
    private string _startArrivalRegion = "";
    private ulong _startRetryReadyAtMsec;
    /// <summary>Set BEFORE the single teleport attempt starts, so it is one attempt even if that attempt throws or fails.</summary>
    private bool _startRetryDone;
    /// <summary>A teleport started since this login (any reason): the person is moving about, so the retry is not made.</summary>
    private bool _teleportSeenSinceLogin;

    /// <summary>How long after the loading screen has cleared the single retry waits, so it never competes with the arrival.</summary>
    private const ulong StartRetryDelayMs = 3000;
    /// <summary>Give up judging the landing when the region never gets a name (the notice would be a guess).</summary>
    private const ulong StartLandingGiveUpMs = 90_000;
    /// <summary>How long to wait for the agent's position before judging by the region alone.</summary>
    private const ulong StartLandingPositionWaitMs = 10_000;

    // ---- the picker on the form ------------------------------------------------------------------------------------------

    /// <summary>Builds the picker and inserts it directly under the profile dropdown (the same way the About link is placed).</summary>
    private void SetupStartPicker()
    {
        _startPicker = new LoginStartPicker { Name = "StartPicker" };
        var form = _profileDropdown.GetParent();
        form.AddChild(_startPicker);
        form.MoveChild(_startPicker, _profileDropdown.GetIndex() + 1);
        _startPicker.ApplyStylesFrom(_profileDropdown, _gridInput);

        _startPicker.ChoiceChanged += ApplyLoginButtonState;
        // Picking Region... adds a field (and, for bad text, a red line): the panel may no longer fit the window.
        _startPicker.ChoiceChanged += () => CallDeferred(nameof(FitLoginPanel));
        GetTree().Root.SizeChanged += FitLoginPanel;
        _startPicker.Submitted += OnStartPickerSubmitted;

        // Changing who or where is being logged into reloads the picker for that profile. Setting a field's Text from code
        // does not raise TextChanged, so the grid dropdown (which fills the grid field) and OnProfileSelected call it themselves.
        _firstInput.TextChanged += _ => RefreshStartPicker();
        _lastInput.TextChanged += _ => RefreshStartPicker();
        _gridInput.TextChanged += _ => RefreshStartPicker();
        _gridDropdown.ItemSelected += _ => RefreshStartPicker();

        RefreshStartPicker(force: true);
        FitLoginPanel();
    }

    /// <summary>
    /// The login panel is as tall as its rows and the window can be shorter: Godot's default window is 648 px high and the panel
    /// was 636 before the "Start at" row, 685 with it and ~750 with a typed region and its red line -- which crops the top (the
    /// logo) and the bottom (the Login button). The logo is the one row nobody needs to log in, so it is dropped when the panel
    /// would not fit and comes back as soon as it does.
    /// </summary>
    private void FitLoginPanel()
    {
        var panel = GetNodeOrNull<Control>("%LoginScreen/Panel");
        var logo = panel?.GetNodeOrNull<Control>("Margin/VBox/Logo");
        if (panel == null || logo == null) return;

        logo.Visible = true; // measure with it
        logo.Visible = panel.GetCombinedMinimumSize().Y <= GetViewportRect().Size.Y;
    }

    /// <summary>X/Y limit for typed text: a Linden grid's regions are 256 m wide, an OpenSim var-region can be larger.</summary>
    private static int MaxStartCoordinate(string gridLoginUri)
        => GridSession.IsLindenLabUri(gridLoginUri) ? StartTarget.LindenMaxXY : StartTarget.OpenMaxXY;

    /// <summary>The saved profile the form currently names, or "" when the account is not saved.</summary>
    private string FindProfileSection(string first, string last, string gridLoginUri)
    {
        foreach (string candidate in new[] { ProfileKey(first, last, gridLoginUri), ProfileKey(first.Trim(), last.Trim(), gridLoginUri) })
        {
            if (candidate == "Settings" || candidate == "Window") continue;
            if (_loginsConfig.HasSection(candidate)) return candidate;
        }
        return "";
    }

    /// <summary>
    /// Shows the favorites and the remembered choice of the profile the form names. Reads <c>_loginsConfig</c> only and never
    /// writes (the self-test boots with it empty and must leave <c>user://</c> alone). Unless <paramref name="force"/>, a form
    /// that still names the same profile is left alone, so a pick the person just made survives their editing another field.
    /// </summary>
    /// <param name="profileSection">The profile when the caller knows it (the person picked it from the dropdown); otherwise it
    /// is found from the first name, last name and grid on the form.</param>
    private void RefreshStartPicker(bool force = false, string? profileSection = null)
    {
        if (_startPicker == null) return;

        _startPicker.RegionMaxXY = MaxStartCoordinate(_gridInput.Text);

        string section = profileSection ?? FindProfileSection(_firstInput.Text, _lastInput.Text, _gridInput.Text);
        if (!force && section == _startPickerSection)
        {
            ApplyLoginButtonState();
            return;
        }
        _startPickerSection = section;

        var previous = _startPicker.Choice;
        if (section.Length > 0)
        {
            _startPicker.SetFavorites(
                LoginStartStore.ReadFavorites(_loginsConfig, section),
                neverPopulated: !LoginStartStore.FavoritesPopulated(_loginsConfig, section));
            _startPicker.SetChoice(LoginStartStore.ReadChoice(_loginsConfig, section));
        }
        else
        {
            _startPicker.SetFavorites(Array.Empty<LoginFavorite>(), neverPopulated: false);
            // An account the viewer has not saved: a Home or Region pick can stay, but a favorite belongs to one profile's list.
            _startPicker.SetChoice(previous.Kind == StartKind.Favorite ? StartChoice.Last : previous);
        }

        if (_startPicker.FallbackReason == StartFallback.FavoriteMissing)
            GD.Print("[LoginStart] the remembered favorite is no longer in this profile's list; showing My last location");

        ApplyLoginButtonState();
    }

    /// <summary>The Login button is usable when no login is under way and the start choice can be sent. An invalid typed region
    /// disables it -- with the reason as its tooltip -- instead of silently logging in somewhere else.</summary>
    private void ApplyLoginButtonState()
    {
        if (_loginButton == null) return;
        bool startInvalid = _startPicker != null && !_startPicker.IsValid;
        _loginButton.Disabled = _loginInFlight || startInvalid;
        _loginButton.TooltipText = startInvalid && !_loginInFlight ? L10n.Tr("ui.login.start_region_invalid") : "";
    }

    private void OnStartPickerSubmitted()
    {
        if (!_loginButton.Disabled) OnLoginPressed();
    }

    // ---- one login -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the picker at the click and forgets what the previous login did. Called FIRST in <c>OnLoginPressed</c>, before the
    /// button is disabled and before any await, while the form is still what the person filled in. Returns null (after saying
    /// why) when the typed region is not a location, in which case the login does not start.
    /// </summary>
    private StartResolution? BeginStartRequest()
    {
        if (!_startPicker.IsValid)
        {
            LogMessage($"[color=orange]{L10n.Tr("ui.login.start_region_invalid")}[/color]");
            return null;
        }

        var start = _startPicker.Resolve(MaxStartCoordinate(_gridInput.Text));
        if (start.Fallback == StartFallback.FavoriteMissing)
            GD.Print("[LoginStart] the picked favorite is no longer in the list; starting at the last location");

        _sessionStartChoice = start.Effective;
        _sessionStartTarget = start.Target;
        _sessionStartPlace = _startPicker.SelectedFavorite?.Name ?? start.Target?.Region ?? "";
        _sessionStartCacheKey = "";

        _sessionReplyStartLocation = null;
        _sessionFirstLogin = false;
        _startLandingPending = false;
        _startNotice = null;
        _startRetryWanted = false;
        _startRetryReadyAtMsec = 0;
        _startRetryDone = false;
        _teleportSeenSinceLogin = false;
        _startArrivalRegion = "";

        GD.Print($"[LoginStart] asking the grid to start at {start.LoginString}");
        return start;
    }

    /// <summary>
    /// The login worked: remember the choice with the profile (when "Save Login" is ticked) and decide where the favorites cache
    /// is written. Unticked, the choice and the cache are erased with the rest of what the profile kept -- unticking says "stop
    /// keeping this". Does not save the file; the caller saves once. <paramref name="profileName"/> is the key the profile is
    /// stored under.
    /// </summary>
    private void OnStartLoginSucceeded(LoginResult result, string profileName, bool saveLogin)
    {
        if (saveLogin)
        {
            LoginStartStore.WriteChoice(_loginsConfig, profileName, _sessionStartChoice);
            _sessionStartCacheKey = profileName;
        }
        else
        {
            LoginStartStore.Erase(_loginsConfig, profileName);
            _sessionStartCacheKey = "";
        }

        // What the grid says about where it started us, judged once the region has a name (TickStartLanding).
        _sessionReplyStartLocation = result.StartLocation;
        _sessionFirstLogin = result.FirstLogin;
        _startLandingPending = !result.FirstLogin;
        _startLandingGiveUpAtMsec = Time.GetTicksMsec() + StartLandingGiveUpMs;
        _startLandingPositionDeadlineMsec = 0;
    }

    /// <summary>Starts keeping the profile's favorites cache current for this session, if the profile is being saved. Nothing is
    /// fetched until <see cref="PokeLoginStartFavorites"/> (after the loading screen clears).</summary>
    private void BeginLoginStartFavorites()
    {
        DisposeLoginStartFavorites();
        if (_sessionStartCacheKey.Length == 0 || _session == null) return;
        _loginStartFavorites = new LoginStartFavorites(
            new GridSessionLoginStartSource(_session),
            LoginStartStore.ReadFavorites(_loginsConfig, _sessionStartCacheKey),
            LoginStartStore.FavoritesPopulated(_loginsConfig, _sessionStartCacheKey));
    }

    /// <summary>The loading screen has cleared: let the resolver start (after its own debounce), so it never competes with the
    /// initial region load.</summary>
    private void PokeLoginStartFavorites() => _loginStartFavorites?.Poke();

    private void DisposeLoginStartFavorites()
    {
        _loginStartFavorites?.Dispose();
        _loginStartFavorites = null;
    }

    /// <summary>One last write of what is known, then stop. For just before the logout call: reads the local inventory store only,
    /// so no network is involved and nothing is awaited.</summary>
    private void FlushAndStopLoginStartFavorites()
    {
        var favorites = _loginStartFavorites;
        _loginStartFavorites = null;
        if (favorites == null) return;
        try
        {
            if (favorites.TryFlush(out var update)) ApplyStartFavoritesUpdate(update);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[LoginStart] the final favorites flush failed: {ex.Message}");
        }
        favorites.Dispose();
    }

    /// <summary>Writes a snapshot the resolver published. Main thread only (it is the one that owns the ConfigFile). Only into the
    /// profile this session was saved under, and only while that profile still exists.</summary>
    private void ApplyStartFavoritesUpdate(LoginStartFavoritesUpdate update)
    {
        if (_sessionStartCacheKey.Length == 0 || !_loginsConfig.HasSection(_sessionStartCacheKey)) return;
        if (LoginStartStore.WriteFavorites(_loginsConfig, _sessionStartCacheKey, update.Favorites))
            _loginsConfig.Save("user://logins.cfg");
    }

    // ---- per frame -------------------------------------------------------------------------------------------------------

    /// <summary>Called every frame from <c>_Process</c>: takes what the resolver published, and moves the landing check along.</summary>
    private void TickStartLocation()
    {
        if (_loginStartFavorites != null && _loginStartFavorites.TryTakeUpdate(out var update))
            ApplyStartFavoritesUpdate(update);

        if (_startLandingPending || _startNotice != null || _startRetryReadyAtMsec != 0)
            TickStartLanding();
    }

    private void ResetStartLanding()
    {
        _startLandingPending = false;
        _startNotice = null;
        _startRetryWanted = false;
        _startRetryReadyAtMsec = 0;
    }

    /// <summary>
    /// Judges, once, whether the grid started the agent where it was asked to (<see cref="LoginStart.JudgeLanding"/>); says so
    /// when it did not; and, for a favorite or a typed region, makes ONE attempt to teleport there. Three steps, each waiting for
    /// what it needs: the region's name (and, for a place, the agent's position) to judge; the loading screen to clear to announce
    /// -- a toast under a loading screen is not seen -- and a few seconds more to retry.
    /// </summary>
    private void TickStartLanding()
    {
        if (_session == null || !_session.IsConnected)
        {
            ResetStartLanding();
            return;
        }

        ulong now = Time.GetTicksMsec();

        if (_startLandingPending)
            DetectStartLanding(now);

        if (_startNotice != null && !_waitingForWorldLoad && !IsLoadingScreenVisible)
        {
            string text = _startNotice;
            _startNotice = null;
            ShowToast(text, 8f);
            _notifications.Add(SLNG.Core.NotificationKind.System, Guid.Empty, text);
            GD.Print($"[LoginStart] {text}");
            if (_startRetryWanted) _startRetryReadyAtMsec = now + StartRetryDelayMs;
        }

        if (_startRetryReadyAtMsec != 0 && now >= _startRetryReadyAtMsec)
        {
            _startRetryReadyAtMsec = 0;
            _ = RetryStartPlaceAsync();
        }
    }

    private void DetectStartLanding(ulong now)
    {
        if (now > _startLandingGiveUpAtMsec)
        {
            _startLandingPending = false;
            return;
        }

        string region = _session!.CurrentRegionName;
        if (string.IsNullOrEmpty(region)) return; // the handshake has not named it yet

        bool place = _sessionStartTarget != null && _sessionStartChoice.Kind is StartKind.Favorite or StartKind.Region;
        double? x = null, y = null;
        if (place)
        {
            var transform = GetLocalAgentTransform();
            if (transform != null)
            {
                x = transform.Position.X;
                y = transform.Position.Y;
            }
            else
            {
                if (_startLandingPositionDeadlineMsec == 0) _startLandingPositionDeadlineMsec = now + StartLandingPositionWaitMs;
                if (now < _startLandingPositionDeadlineMsec) return; // wait for the avatar to appear; then judge by the region alone
            }
        }

        _startLandingPending = false;

        var verdict = LoginStart.JudgeLanding(
            _sessionStartChoice.Kind, _sessionStartTarget, _sessionReplyStartLocation, _sessionFirstLogin, region, x, y);
        if (verdict == LandingVerdict.Fine) return;

        _startArrivalRegion = region;
        switch (verdict)
        {
            case LandingVerdict.MovedFromPlace:
                string text = L10n.TrFormat("ui.login.start_fallback_place", _sessionStartPlace, region);
                // ONE attempt per login, never a loop.
                _startRetryWanted = !_startRetryDone && _sessionStartTarget != null;
                if (_startRetryWanted)
                    text += " " + L10n.TrFormat("ui.login.start_fallback_retry", _sessionStartPlace);
                _startNotice = text;
                break;

            case LandingVerdict.MovedFromHome:
                _startNotice = L10n.TrFormat("ui.login.start_fallback_home", region);
                break;

            case LandingVerdict.MovedFromLast:
                _startNotice = L10n.TrFormat("ui.login.start_fallback_last", region);
                break;
        }
    }

    /// <summary>
    /// The single teleport attempt to the place that was asked for (a favorite or a typed region; never for a Home or Last that was
    /// refused -- teleporting to them would fail the same way). SLNG's own behaviour: the reference viewer shows only the notice.
    /// Whatever comes of it, there is no second try.
    /// </summary>
    private async System.Threading.Tasks.Task RetryStartPlaceAsync()
    {
        _startRetryDone = true; // before anything else: one attempt, even if this throws or fails
        var target = _sessionStartTarget;
        if (target == null) return;

        bool ShouldStop(out string reason)
        {
            reason = "";
            if (_session == null || !_session.IsConnected) reason = "the session ended";
            else if (_teleportActive || _teleportSeenSinceLogin) reason = "a teleport is under way or has happened";
            else if (!string.Equals(_session.CurrentRegionName, _startArrivalRegion, StringComparison.OrdinalIgnoreCase))
                reason = "the person has left the arrival region";
            return reason.Length > 0;
        }

        try
        {
            if (ShouldStop(out var why))
            {
                GD.Print($"[LoginStart] no retry: {why}");
                return;
            }

            // Not ConfigureAwait(false): the continuations touch Boot's state and must run on the main thread.
            var region = await _session!.ResolveRegionByNameAsync(target.Region);
            if (region == null)
            {
                GD.Print($"[LoginStart] no retry: the grid does not know a region named '{target.Region}'");
                return;
            }
            if (ShouldStop(out why))
            {
                GD.Print($"[LoginStart] no retry: {why}");
                return;
            }

            GD.Print($"[LoginStart] one attempt to teleport to {target.Region} ({target.X}, {target.Y}, {target.Z})");
            var result = await _session.TeleportToAsync(
                region.RegionHandle, new System.Numerics.Vector3(target.X, target.Y, target.Z));
            // The outcome is the ordinary teleport UI (progress overlay, failure text); nothing more is said here.
            GD.Print($"[LoginStart] the attempt {(result.Success ? "arrived" : "did not arrive: " + result.Message)}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[LoginStart] the teleport attempt failed: {ex.Message}");
        }
    }
}
