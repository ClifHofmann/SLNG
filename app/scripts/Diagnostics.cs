using Godot;

namespace SLNG.App;

/// <summary>
/// One switch for the whole performance-diagnostic apparatus built during FEAT-PERF-01: the stats
/// overlay's log lines, the main-thread watchdog, the frame-hitch and ground-clamp traces, and the
/// per-object asset logging.
///
/// It is a switch rather than a deletion on purpose. Those tools are the only reason the stutter
/// investigation converged -- a 12-second freeze was attributed to terrain collision, and a 226
/// ms/second cost to an entity scan, because the numbers were being written down. Removing them for
/// a release would mean rebuilding them the next time something is slow, and the next report will
/// arrive from a build nobody can reproduce locally.
///
/// Off by default, so a release is quiet: no per-frame log lines, no background watchdog thread, no
/// overlay on screen. Enable with <c>--diag</c> on the command line. The overlay itself stays
/// reachable either way via Ctrl+Shift+1, because a user reporting "it stutters" can be asked to
/// look at it without being asked to relaunch from a terminal.
/// </summary>
public static class Diagnostics
{
    private const string Flag = "--diag";

    /// <summary>True when the client was started with <c>--diag</c>. Read in hot paths, so it is a
    /// plain static bool rather than anything that re-reads the command line.</summary>
    public static bool Enabled { get; private set; }

    /// <summary>Call once at startup, before anything that logs. Godot puts arguments after a bare
    /// <c>--</c> into GetCmdlineUserArgs and the rest into GetCmdlineArgs; both are checked so the
    /// flag works whether or not it is passed after the separator.</summary>
    public static void Initialize()
    {
        Enabled = HasFlag(OS.GetCmdlineArgs()) || HasFlag(OS.GetCmdlineUserArgs());

        // Info is where the per-object asset logging lives -- texture fetches, sharpen decisions,
        // mesh sites. One session of it ran to 3.4 GB before it was throttled, and even throttled it
        // is thousands of lines nobody reads unless they are chasing something.
        Logger.CurrentLevel = Enabled ? LogLevel.Info : LogLevel.Warning;

        if (Enabled) GD.Print("[Diagnostics] enabled via --diag: perf logging, watchdog and overlay on");
    }

    private static bool HasFlag(string[] args)
    {
        foreach (string arg in args)
        {
            if (arg == Flag) return true;
        }
        return false;
    }
}
