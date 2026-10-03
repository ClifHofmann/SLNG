using System;
using System.Collections.Generic;
using Godot;
using SLNG.Core.Input;

namespace SLNG.App;

/// <summary>
/// "Is this action's key held right now?" for the polled actions - walking, orbiting, panning - read
/// from the same <see cref="KeyBindings.Table"/> the dispatcher uses. The controllers call
/// <see cref="IsHeld(string)"/> every frame, so it must be cheap: a dictionary lookup, a couple of
/// native key-state reads, no allocation. The per-action chord list is cached and rebuilt only when
/// <see cref="KeyBindingTable.Version"/> moves (a rebinding), and the Ctrl/Alt/Shift state is read once
/// per frame, not once per action.
///
/// <para>Modifiers must match exactly: W walks, Alt+W moves the camera in, Ctrl+W is a shortcut that
/// walks nobody. The one exception is an action flagged <see cref="KeyAction.IgnoreExtraShift"/> (the
/// avatar movement keys), where Shift is the run modifier and Shift+W is still "forward".</para>
///
/// <para>Focus is NOT checked here. Held actions are World-context; the caller already has the
/// "a text field or window control has the keyboard" gate (<c>hasUiFocus</c>) and applies it.</para>
/// </summary>
public static class HeldKeys
{
    private readonly record struct Bound(Key Key, bool Ctrl, bool Alt, bool Shift);

    private sealed record Entry(Bound[] Chords, bool IgnoreExtraShift);

    private static readonly Dictionary<string, Entry> Cache = new(StringComparer.Ordinal);
    private static KeyBindingTable? _cachedFor;
    private static int _cachedVersion = -1;

    private static ulong _modFrame = ulong.MaxValue;
    private static bool _ctrl, _alt, _shift;

    /// <summary>Where key state comes from. The engine's keyboard by default; the selftest swaps in
    /// a fake so "W held" can be asserted without a real key press.</summary>
    public static Func<Key, bool> KeyDown { get; set; } = key => Input.IsKeyPressed(key);

    /// <summary>The table read from. <see cref="KeyBindings.Table"/> unless a test says otherwise.</summary>
    public static KeyBindingTable Table { get; set; } = KeyBindings.Table;

    /// <summary>True while any chord bound to <paramref name="actionId"/> is held.</summary>
    public static bool IsHeld(string actionId)
    {
        // While the Keyboard page is listening for a new chord, a held key is being bound, not used.
        if (KeyDispatcher.Instance is { Suspended: true }) return false;

        var table = Table;
        if (!ReferenceEquals(_cachedFor, table) || _cachedVersion != table.Version) Rebuild(table);
        if (!Cache.TryGetValue(actionId, out var entry)) return false;

        ReadModifiers();
        foreach (var b in entry.Chords)
        {
            if (b.Ctrl != _ctrl || b.Alt != _alt) continue;
            if (b.Shift != _shift && !(entry.IgnoreExtraShift && _shift && !b.Shift)) continue;
            if (KeyDown(b.Key)) return true;
        }
        return false;
    }

    /// <summary>Forgets the cached modifier state (a test that changes the fake key state within one frame).</summary>
    public static void InvalidateFrame() => _modFrame = ulong.MaxValue;

    private static void ReadModifiers()
    {
        ulong frame = Engine.GetProcessFrames();
        if (frame == _modFrame) return;
        _modFrame = frame;
        _ctrl = KeyDown(Key.Ctrl);
        _alt = KeyDown(Key.Alt);
        _shift = KeyDown(Key.Shift);
    }

    private static void Rebuild(KeyBindingTable table)
    {
        Cache.Clear();
        foreach (var action in table.Actions)
        {
            if (action.Kind != KeyActionKind.Held) continue;
            var chords = table.ChordsOf(action.Id);
            var bound = new List<Bound>(chords.Count);
            foreach (var chord in chords)
            {
                var key = GodotKeyMap.KeyOf(chord.Key);
                if (key != Key.None) bound.Add(new Bound(key, chord.Ctrl, chord.Alt, chord.Shift));
            }
            Cache[action.Id] = new Entry(bound.ToArray(), action.IgnoreExtraShift);
        }
        _cachedFor = table;
        _cachedVersion = table.Version;
        InvalidateFrame();
    }
}
