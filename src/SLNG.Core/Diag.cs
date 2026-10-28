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

    /// <summary>Writes one verbose line. Call it behind <c>if (Diag.Verbose)</c>, so the string is not built when the switch is off.</summary>
    public static void Write(string line)
    {
        var sink = Sink;
        if (sink != null) sink(line);
        else Console.Error.WriteLine(line);
    }
}
