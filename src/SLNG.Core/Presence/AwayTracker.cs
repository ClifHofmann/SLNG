namespace SLNG.Core.Presence;

/// <summary>
/// FEAT-UI-75: the timing half of "Away": going away by itself after a quiet spell, and coming back on the next sign
/// of life. The rules are the reference viewer's (<c>LLAgent::setAFK/clearAFK</c>, the idle check in
/// <c>LLAppViewer::idle</c>, <c>LLViewerWindow::handleKey/handleMouseMove</c>): input clears Away only when it has
/// lasted longer than <see cref="MinAwaySeconds"/> -- so the click that chose "Away" from a menu does not cancel it --
/// and a quiet spell is measured from the last input. Times are seconds on any monotonic clock the caller keeps.
/// </summary>
public sealed class AwayTracker
{
    /// <summary><c>LLAgent::MIN_AFK_TIME</c>.</summary>
    public const double MinAwaySeconds = 10.0;

    private double _awaySince;
    private double _lastInput;

    public bool IsAway { get; private set; }

    /// <summary>Goes Away. True when the state changed.</summary>
    public bool SetAway(double now)
    {
        if (IsAway) return false;
        IsAway = true;
        _awaySince = now;
        return true;
    }

    /// <summary>Comes back, whatever the cause (the menu, the window gaining focus). True when the state changed.</summary>
    public bool ClearAway(double now)
    {
        _lastInput = now;
        if (!IsAway) return false;
        IsAway = false;
        return true;
    }

    /// <summary>A key, a click or the mouse moved. True when this brought the agent back from Away.</summary>
    public bool OnActivity(double now)
    {
        if (IsAway)
        {
            // Too soon after going away: this input is the one that chose it, so it is not a sign of life.
            if (now - _awaySince <= MinAwaySeconds) return false;
            return ClearAway(now);
        }
        _lastInput = now;
        return false;
    }

    /// <summary>Called every frame. True when the quiet spell just made the agent go Away.</summary>
    /// <param name="timeoutSeconds">0 = never.</param>
    public bool Tick(double now, int timeoutSeconds)
    {
        if (IsAway || timeoutSeconds <= 0 || now - _lastInput <= timeoutSeconds) return false;
        return SetAway(now);
    }
}
