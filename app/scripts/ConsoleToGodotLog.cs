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

                // GD.PrintErr is what lands in godot.log as an error entry; stderr from the
                // libraries is where the failure diagnostics live, so keep the distinction.
                if (_isError) GD.PrintErr(text);
                else GD.Print(text);
            }
        }
    }
}
