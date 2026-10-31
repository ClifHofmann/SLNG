namespace SLNG.Core;

/// <summary>
/// The verbose-logging switch, in <c>Core</c> so both halves of the project can read it.
///
/// <para>The client's console log had grown to the point where the lines that matter — a failed
/// texture, a refused inventory move, a region change — were buried under per-mesh and per-click
/// diagnostics. Those diagnostics are not deleted: they are the reason several investigations in
/// this project converged at all, and rebuilding them the next time something is wrong would be
/// worse than a noisy log. They are simply off unless asked for.</para>
///
/// <para><c>app/</c> owns the command line and sets this from <c>--diag</c> at startup
/// (<c>SLNG.App.Diagnostics.Initialize</c>), which is also the flag the performance overlay and
/// the main-thread watchdog already use — one switch, not two. <c>src/</c> only reads it.</para>
///
/// <para>Guard at the call site rather than inside a logging helper, so the interpolated string is
/// never built when the switch is off:
/// <code>if (Diag.Verbose) Console.Error.WriteLine($"[Tag] {expensive}");</code></para>
/// </summary>
public static class Diag
{
    /// <summary>True when the client was started with <c>--diag</c>. False in a normal run, which
    /// is what keeps the log readable.</summary>
    public static bool Verbose { get; set; }

    /// <summary>
    /// Where a verbose line goes. <c>app/</c> sets it: with <c>--diag</c> to the console, otherwise (the debug log switched
    /// <see cref="Verbose"/> on) to the debug file alone, so a person who only wants the debug log does not also get every
    /// per-object line in the console and <c>godot.log</c>. Null (a test, or before startup) means the console.
    /// </summary>
    public static Action<string>? Sink { get; set; }

    private static readonly long StartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>Replaces the clock <see cref="NowSeconds"/> reads. Null (the default) is the monotonic one. A test sets it.</summary>
    public static Func<double>? Clock { get; set; }

    /// <summary>
    /// Seconds on a monotonic clock since the process started (FEAT-PERF-28). Wall-clock time can jump; this cannot, so two
    /// lines of one log always give a true interval, and a rate no longer depends on assuming the report interval held.
    /// </summary>
    public static double NowSeconds
    {
        get
        {
            var clock = Clock;
            return clock != null ? clock() : System.Diagnostics.Stopwatch.GetElapsedTime(StartTimestamp).TotalSeconds;
        }
    }

    /// <summary>
    /// <paramref name="line"/> with <c>t=&lt;seconds&gt;</c> from <see cref="NowSeconds"/>. After a leading <c>[Tag]</c> when there is
    /// one -- so a filter on the start of the line keeps matching -- otherwise in front.
    /// </summary>
    public static string Stamp(string line)
    {
        string t = "t=" + NowSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        if (line.Length > 1 && line[0] == '[')
        {
            int close = line.IndexOf(']');
            if (close > 0) return line.Insert(close + 1, " " + t);
        }
        return t + " " + line;
    }

    /// <summary>Writes one verbose line, stamped with <see cref="NowSeconds"/>. Call it behind <c>if (Diag.Verbose)</c>, so the string is not built when the switch is off.</summary>
    public static void Write(string line)
    {
        line = Stamp(line);
        var sink = Sink;
        if (sink != null) sink(line);
        else Console.Error.WriteLine(line);
    }
}
