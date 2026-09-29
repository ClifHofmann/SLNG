namespace SLNG.Core;

/// <summary>
/// FEAT-UI-34: the countdown behind the region-restart popup.
///
/// <para>A deadline, not a ticking number: the remaining time is always derived from the clock,
/// so a stalled frame or a hidden window cannot make it drift. A repeat notice for the same
/// restart simply moves the deadline (<see cref="Start"/> is idempotent in that sense), which is
/// what the reference viewer's <c>updateTime</c> does instead of opening a second window.</para>
///
/// <para>The clock is passed in as a <see cref="TimeSpan"/> from any monotonic source, so nothing
/// here depends on Godot or on wall-clock time.</para>
/// </summary>
public sealed class RegionRestartCountdown
{
    private TimeSpan _deadline;

    /// <summary>The region that is about to restart; empty when the simulator did not name it.</summary>
    public string RegionName { get; private set; } = string.Empty;

    public bool IsRunning { get; private set; }

    /// <summary>Starts the countdown, or moves its deadline when one is already running.</summary>
    public void Start(string regionName, int seconds, TimeSpan now)
    {
        RegionName = regionName;
        _deadline = now + TimeSpan.FromSeconds(Math.Max(0, seconds));
        IsRunning = true;
    }

    /// <summary>Whole seconds left, rounded up so "0:01" is shown until the very last moment
    /// rather than "0:00" a second early. Zero when not running or already past.</summary>
    public int SecondsLeft(TimeSpan now)
    {
        if (!IsRunning) return 0;
        return (int)Math.Max(0, Math.Ceiling((_deadline - now).TotalSeconds));
    }

    /// <summary>The deadline has passed — the region should be restarting.</summary>
    public bool HasElapsed(TimeSpan now) => IsRunning && now >= _deadline;

    public void Stop() => IsRunning = false;

    /// <summary>"m:ss", the way a countdown is read. Minutes are not capped at 59: a 90-minute
    /// notice reads "90:00", which is what people expect from a restart timer.</summary>
    public static string Format(int seconds)
    {
        seconds = Math.Max(0, seconds);
        return $"{seconds / 60}:{seconds % 60:00}";
    }
}
