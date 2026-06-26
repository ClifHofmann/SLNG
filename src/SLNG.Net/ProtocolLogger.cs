using System;
using System.Diagnostics;

namespace SLNG.Net;

/// <summary>
/// Subscribes to events from a GridSession and writes them as structured text to standard output and debug.
/// </summary>
public sealed class ProtocolLogger : IDisposable
{
    private readonly GridSession _session;
    private readonly Action<string> _logOutput;

    public ProtocolLogger(GridSession session, Action<string>? logOutput = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logOutput = logOutput ?? (log => { Console.WriteLine(log); Debug.WriteLine(log); });
        _session.ChatMessageReceived += OnChatMessage;
        _session.ObjectUpdateReceived += OnObjectUpdate;
    }

    private void OnChatMessage(object? sender, ChatMessageEvent e)
    {
        var log = $"[CHAT] From: '{e.FromName}', Type: {e.ChatType}, Msg: \"{e.Message}\"";
        _logOutput(log);
    }

    private void OnObjectUpdate(object? sender, ObjectUpdateEvent e)
    {
        var log = $"[OBJECT] Update LocalID: {e.LocalId}, Pos: {e.Position}, Rot: {e.Rotation}";
        _logOutput(log);
    }

    public void Dispose()
    {
        _session.ChatMessageReceived -= OnChatMessage;
        _session.ObjectUpdateReceived -= OnObjectUpdate;
    }
}
