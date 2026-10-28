using System.IO;
using Godot;

namespace SLNG.App;

/// <summary>
/// FEAT-UI-76: feeds everything the engine logs -- <c>GD.Print</c>, <c>GD.PrintErr</c> and the console output the
/// libraries produce -- plus its warnings and errors into <see cref="DebugLog"/>. Installed once at boot like
/// <see cref="EngineWarningTap"/> and inert while the debug log is off: <see cref="DebugLog.Write"/> returns at once.
///
/// <para>The callbacks can fire on any thread and must not log themselves (that would feed the tap), and
/// <see cref="DebugLog.Write"/> neither logs nor blocks.</para>
/// </summary>
// Godot.Logger spelled out: SLNG.App has a static Logger of its own, which would win the name.
internal sealed partial class DebugLogTap : Godot.Logger
{
    public override void _LogMessage(string message, bool error)
    {
        DebugLog.Write(error ? "ERR" : "INFO", message);
    }

    public override void _LogError(string function, string file, int line, string code, string rationale,
        bool editorNotify, int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
    {
        if (!DebugLog.IsRunning) return;
        string text = string.IsNullOrWhiteSpace(rationale) ? code : rationale;
        string kind = errorType == (int)Godot.Logger.ErrorType.Warning ? "WARN" : "ERROR";
        DebugLog.Write(kind, $"{text} @ {function} ({Path.GetFileName(file)}:{line})");
    }
}
