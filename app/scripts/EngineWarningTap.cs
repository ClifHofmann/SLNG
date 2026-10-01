using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Godot;

namespace SLNG.App;

/// <summary>
/// Accounts for what the ENGINE warns about (BUG-RENDER-40): Godot's own "Vector3 cannot be normalized"
/// and the like. The engine prints those with no hint of what caused them -- a flood of 600 to 1,400 of
/// them in the seconds after a busy region loads said nothing about which object, which thread or which
/// piece of the client's own work was running -- so this sits on the engine's log (<c>OS.AddLogger</c>),
/// counts every warning by what it said, where in the engine it came from, which main-thread work item
/// was running, whether it was the main thread, and the first frame of OUR code on its stack when the
/// engine supplies one, and prints one short summary every few seconds instead.
///
/// <para>It only counts: the engine's own console lines are untouched, and a quiet client prints
/// nothing. The callback can fire on any thread and must not log (that would feed itself), so it
/// records and the reporting is done from <see cref="Flush"/> on the main thread.</para>
/// </summary>
// Godot.Logger spelled out: SLNG.App has a static Logger of its own, which would win the name.
internal sealed partial class EngineWarningTap : Godot.Logger
{
    private const double ReportEverySeconds = 5.0;
    private const int MaxLines = 6;

    private readonly ConcurrentDictionary<string, int> _counts = new();
    private readonly ConcurrentDictionary<ulong, byte> _frames = new();
    private readonly ulong _mainThread = OS.GetMainThreadId();
    private long _total;
    private double _sinceReport;

    /// <summary>Every warning counted since the client started.</summary>
    public long Total => Interlocked.Read(ref _total);

    public override void _LogMessage(string message, bool error)
    {
        // The plain console lines are not interesting here; errors and warnings arrive in _LogError.
    }

    public override void _LogError(string function, string file, int line, string code, string rationale,
        bool editorNotify, int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
    {
        try
        {
            string text = FirstLine(string.IsNullOrWhiteSpace(rationale) ? code : rationale);
            string kind = errorType == (int)Godot.Logger.ErrorType.Warning ? "warning" : "error";
            string during = MainThreadWorkQueue.CurrentLabel ?? "no work-queue item";
            string thread = OS.GetThreadCallerId() == _mainThread ? "main thread" : "worker thread";

            string key = $"{kind}: {text} @ {function} ({ShortFile(file)}:{line}) | during: {during} | {thread}{OurFrame(scriptBacktraces)}";
            _counts.AddOrUpdate(key, 1, (_, n) => n + 1);
            _frames[Engine.GetProcessFrames()] = 0;
            Interlocked.Increment(ref _total);
        }
        catch
        {
            // A diagnostic must never be the thing that throws inside the engine's logger.
        }
    }

    /// <summary>Call once a frame from the main thread; prints a summary every few seconds if the engine
    /// warned in between.</summary>
    public void Flush(double deltaSeconds)
    {
        _sinceReport += deltaSeconds;
        if (_sinceReport < ReportEverySeconds) return;
        _sinceReport = 0;
        if (_counts.IsEmpty) return;

        var snapshot = _counts.ToArray();
        _counts.Clear();
        int frames = _frames.Count;
        _frames.Clear();

        int all = snapshot.Sum(p => p.Value);
        GD.Print($"[EngineWarnings] {all} in the last {ReportEverySeconds:0} s, over {frames} frame(s), {snapshot.Length} kind(s):");
        foreach (var (key, count) in snapshot.OrderByDescending(p => p.Value).Take(MaxLines))
            GD.Print($"[EngineWarnings]   {count}x {key}");
        if (snapshot.Length > MaxLines)
            GD.Print($"[EngineWarnings]   ... and {snapshot.Length - MaxLines} more kind(s)");
    }

    private static string FirstLine(string text)
    {
        int end = text.IndexOfAny(new[] { '\r', '\n' });
        return (end < 0 ? text : text[..end]).Trim();
    }

    private static string ShortFile(string file)
    {
        int slash = Math.Max(file.LastIndexOf('/'), file.LastIndexOf('\\'));
        return slash < 0 ? file : file[(slash + 1)..];
    }

    /// <summary>The first frame of the client's own C# code on the stack, if the engine gave a C# stack at
    /// all -- it does not for a warning raised from the engine's own frame processing, which is itself
    /// worth knowing.</summary>
    private static string OurFrame(Godot.Collections.Array<ScriptBacktrace> backtraces)
    {
        foreach (var trace in backtraces)
        {
            if (trace.IsEmpty() || trace.GetLanguageName() != "C#") continue;
            for (int i = 0; i < trace.GetFrameCount(); i++)
            {
                string file = trace.GetFrameFile(i);
                string function = trace.GetFrameFunction(i);
                if (file.StartsWith("/root/godot", StringComparison.Ordinal) || function.StartsWith("Godot.", StringComparison.Ordinal))
                    continue; // engine glue, not ours
                return $" | in our code: {function} ({ShortFile(file)}:{trace.GetFrameLine(i)})";
            }
        }
        return " | no C# frame";
    }
}
