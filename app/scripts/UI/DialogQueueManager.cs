using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Godot;
using SLNG.Core;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// Buffers <see cref="GridSession.ScriptDialogReceived"/> (M5-4) -- raised on a LibreMetaverse
/// network thread -- and drains it once per frame onto the Godot main thread (Boot.cs._Process,
/// next to WorldSimulation.Pump), where it spawns/cascades <see cref="ScriptDialogWindow"/>
/// popups. Multiple concurrent dialogs are tracked in <see cref="_activeDialogs"/> and cascaded
/// diagonally, same idiom as Boot.cs's _objectEditWindows.
/// </summary>
public sealed class DialogQueueManager : IDisposable
{
    private const int MaxCascade = 8;

    private readonly ConcurrentQueue<ScriptDialogEvent> _pending = new();
    private readonly List<ScriptDialogWindow> _activeDialogs = new();
    private readonly CanvasLayer _hudLayer;
    private readonly GridSession _session;

    public DialogQueueManager(GridSession session, CanvasLayer hudLayer)
    {
        _session = session;
        _hudLayer = hudLayer;
        _session.ScriptDialogReceived += OnScriptDialogReceived;
    }

    private void OnScriptDialogReceived(object? sender, ScriptDialogEvent e) => _pending.Enqueue(e);

    /// <summary>Call once per frame from the main thread. The only place popups are created.</summary>
    public void Pump()
    {
        while (_pending.TryDequeue(out var e))
            ShowDialog(e);
    }

    private void ShowDialog(ScriptDialogEvent e)
    {
        var win = new ScriptDialogWindow();
        _hudLayer.AddChild(win);
        win.CascadeIndex = _activeDialogs.Count % MaxCascade;
        win.Closed += () => _activeDialogs.Remove(win);
        _activeDialogs.Add(win);
        win.Initialize(_session, e);
    }

    public void Dispose()
    {
        _session.ScriptDialogReceived -= OnScriptDialogReceived;
        foreach (var win in _activeDialogs) win.QueueFree();
        _activeDialogs.Clear();
    }
}
