using System;
using System.IO;
using System.Text;
using Godot;

namespace SLNG.App;

/// <summary>
/// Routes <see cref="Console"/> output from the engine-agnostic libraries into Godot's logger, so
/// it reaches <c>godot.log</c>.
///
/// <para>Without this, everything <c>src/</c> writes is effectively invisible in a normally-started
/// client. <c>SLNG.Assets</c> and <c>SLNG.Net</c> cannot call <c>GD.Print</c> -- they must not
/// reference Godot at all (AGENTS.md) -- so they log through <see cref="Console"/>, which goes to
/// the process's stdout/stderr. Godot's own log file is written by the engine and does not capture
/// those streams, so the lines only exist when the client happens to have been launched from a
/// terminal that was capturing it.</para>
///
/// <para>That gap cost real debugging time: every <c>[TextureFetch]</c>, <c>[TextureGiveUp]</c> and
/// <c>[DecodeFail]</c> line -- the ones that say WHY an asset never arrived -- was missing from the
/// log of a session whose objects were visibly broken, while the renderer-side symptom was right
/// there. The two halves of the same failure were being written to two different places.</para>
///
/// <para>Not gated on <c>--diag</c>: these are already-sparse failure lines, not per-frame tracing.
/// The verbose per-object logging in the same libraries is level-gated at its own call sites.</para>
/// </summary>
public static class ConsoleToGodotLog
{
    private static bool _installed;

    /// <summary>Call once at startup, before anything that logs.</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        Console.SetOut(new GodotWriter(isError: false));
        Console.SetError(new GodotWriter(isError: true));
    }

    /// <summary>
    /// Buffers characters until a newline, then emits one Godot log line.
    ///
    /// The buffering is not optional: <see cref="Console.WriteLine"/> reaches a TextWriter as
    /// several Write calls (the string, then the line terminator), and GD.Print emits a whole line
    /// per call -- forwarding each Write directly would shred every message across several log
    /// lines and interleave badly with output from other threads. Asset decode and fetch both run
    /// on worker threads, so the buffer is guarded.
    /// </summary>
    private sealed class GodotWriter : TextWriter
    {
        private readonly bool _isError;
        private readonly StringBuilder _line = new();
        private readonly object _gate = new();

        public GodotWriter(bool isError) => _isError = isError;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_gate)
            {
                if (value == '\n') Flush();
                else if (value != '\r') _line.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            foreach (char c in value) Write(c);
        }

        public override void Flush()
        {
            // Called under _gate from Write, and possibly bare from a caller's own Flush().
            lock (_gate)
            {
                if (_line.Length == 0) return;
                string text = _line.ToString();
                _line.Clear();

                PerfSidecar.MaybeWrite(text);

                // GD.PrintErr is what lands in godot.log as an error entry; stderr from the
                // libraries is where the failure diagnostics live, so keep the distinction.
                if (_isError) GD.PrintErr(text);
                else GD.Print(text);
            }
        }
    }
}

/// <summary>
/// Mirrors the handful of PERFORMANCE diagnostic lines into their own always-flushed file next to
/// <c>godot.log</c>.
///
/// <para>Why this exists: godot.log is buffered by the engine, and a session that ends by anything
/// other than a clean shutdown loses whatever had not reached disk. On Windows that shows up as a
/// log whose body is one enormous run of NUL bytes with only the pre-login and post-logout lines
/// intact -- which is exactly what happened to the v0.20.50 session whose <c>[TexPipe]</c> /
/// <c>[GpuCache]</c> counters were the whole point of shipping that build (2026-09-03). Losing a
/// live measurement costs a full round-trip through the user, so the two lines that answer "where
/// is the texture pipeline actually spending its time" are written straight through instead.</para>
///
/// <para>Deliberately an allowlist of exact prefixes, not a tee of everything: these are periodic
/// summary lines (one per 200 requests), a few dozen per session, so an unbuffered write per line
/// costs nothing. Do not add per-object or per-frame tags here.</para>
/// </summary>
internal static class PerfSidecar
{
    private static readonly string[] _prefixes =
    {
        "[TexPipe]", "[GpuCache]",
        // Frame data. These used to be gated behind --diag, on the reasoning that a line every 5 s
        // would bury godot.log -- and the result was that in the entire project history NOT ONE
        // session log contained a single [Perf] line, so "why is the frame rate low" could only ever
        // be answered by asking the user to re-run with a flag they had no reason to know about.
        // The sidecar is a separate file that nothing else writes to, so the objection does not
        // apply here: it costs one flushed line per 5 s and it means the next ordinary session
        // already has the answer in it.
        "[Perf]", "[WorkCost]", "[PhaseCost]",
        // FEAT-RENDER-08: the atmosphere inputs and what they attenuate to. One line per change.
        "[SkyAtmos]",
    };

    private static StreamWriter? _writer;
    private static bool _failed;
    private static readonly object _gate = new();

    /// <summary>Writes a line that is already known to belong in the sidecar, skipping the prefix
    /// test. For callers inside the app assembly that log through Godot rather than Console.</summary>
    internal static void Write(string line) => WriteCore(line);

    internal static void MaybeWrite(string line)
    {
        bool wanted = false;
        foreach (var p in _prefixes)
        {
            if (line.StartsWith(p, StringComparison.Ordinal)) { wanted = true; break; }
        }
        if (!wanted) return;
        WriteCore(line);
    }

    private static void WriteCore(string line)
    {
        lock (_gate)
        {
            if (_failed) return;
            if (_writer == null)
            {
                try
                {
                    string dir = ProjectSettings.GlobalizePath("user://logs");
                    Directory.CreateDirectory(dir);
                    // Truncate: one file per session, so reading it never means working out which
                    // half belongs to the run being investigated.
                    _writer = new StreamWriter(
                        new FileStream(Path.Combine(dir, "slng-perf.log"), FileMode.Create,
                                       System.IO.FileAccess.Write, FileShare.ReadWrite))
                    { AutoFlush = true };
                }
                catch
                {
                    _failed = true;
                    return;
                }
            }

            try { _writer.WriteLine($"{DateTime.Now:HH:mm:ss} {line}"); }
            catch { _failed = true; }
        }
    }
}
