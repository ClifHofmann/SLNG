using System;
using System.Collections.Generic;
using Godot;
using SLNG.App.UI;
using SLNG.Core.Input;

namespace SLNG.App;

/// <summary>
/// The one place a discrete key press becomes an action. Replaces the key checks that used to sit in
/// <c>Boot._Input</c>, <c>AvatarController</c>, <c>AvatarRenderer</c> and <c>InventoryPanel</c>: each of
/// those now registers a handler for an action id with <see cref="Register"/>, and the chord that
/// triggers it comes from <see cref="KeyBindings.Table"/>, so a rebinding takes effect immediately.
/// (Polled keys - walking, orbiting - are not dispatched; see <see cref="HeldKeys"/>.)
///
/// <para><b>Two phases, chosen by the action's context.</b> A <see cref="KeyContext.World"/>-only action
/// runs from <c>_UnhandledInput</c>, i.e. only if no control consumed the key - exactly where
/// <c>AvatarController</c> used to handle Home / Esc / Ctrl+R. Everything else (menu-style shortcuts,
/// F-keys, editing) runs from <c>_Input</c>, before the GUI sees the key, exactly where
/// <c>Boot._Input</c> ran.</para>
///
/// <para><b>Text fields.</b> The focus state is classified from the focused control
/// (<see cref="Classify"/>). While the person is typing, an action fires only if its context allows
/// typing (<see cref="KeyContext.Always"/>) AND the chord is not one the field keeps for itself
/// (<see cref="TextEditChords.Owns"/>): Ctrl+C in the chat bar is text, Ctrl+I still opens the
/// inventory.</para>
///
/// <para>A handler returns true when it handled the key (the event is then consumed) and false to let
/// it fall through - the inventory's Ctrl+C returns false when the pointer is not over the inventory,
/// so the key is not swallowed.</para>
/// </summary>
public partial class KeyDispatcher : Node
{
    private sealed record Registration(Node Owner, Func<bool> Handler);

    /// <summary>The dispatcher in the running client (null before Boot creates it, and in a test that
    /// builds its own).</summary>
    public static KeyDispatcher? Instance { get; private set; }

    private readonly KeyBindingTable _table;
    private readonly Dictionary<string, List<Registration>> _handlers = new(StringComparer.Ordinal);

    /// <summary>While set, no key is dispatched. The Keyboard page raises it for the moment it is
    /// listening for a new chord, so pressing the keys to rebind does not also run them.</summary>
    public bool Suspended { get; set; }

    public KeyDispatcher() : this(KeyBindings.Table) { }

    public KeyDispatcher(KeyBindingTable table)
    {
        _table = table;
        Name = "KeyDispatcher";
    }

    public override void _EnterTree() => Instance ??= this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>Registers what <paramref name="actionId"/> does. Several handlers may register for one
    /// action; they are tried in registration order until one returns true. A handler whose
    /// <paramref name="owner"/> has been freed is skipped and dropped, so a node that is rebuilt (a
    /// renderer per login) never leaves a stale handler behind.</summary>
    public void Register(Node owner, string actionId, Func<bool> handler)
    {
        if (!_handlers.TryGetValue(actionId, out var list)) _handlers[actionId] = list = new List<Registration>();
        list.Add(new Registration(owner, handler));
    }

    /// <summary>Convenience for a handler that always counts as handled.</summary>
    public void Register(Node owner, string actionId, Action handler) =>
        Register(owner, actionId, () => { handler(); return true; });

    /// <summary>The action ids that have at least one live handler (the selftest checks every Press
    /// action in the catalog is among them once the client has booted).</summary>
    public IEnumerable<string> HandledActions
    {
        get
        {
            foreach (var (id, list) in _handlers)
                if (list.Exists(r => GodotObject.IsInstanceValid(r.Owner))) yield return id;
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey key && Dispatch(key, Classify(GetViewport()?.GuiGetFocusOwner()), afterGui: false))
            GetViewport().SetInputAsHandled();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey key && Dispatch(key, Classify(GetViewport()?.GuiGetFocusOwner()), afterGui: true))
            GetViewport().SetInputAsHandled();
    }

    /// <summary>
    /// Routes one key event. <paramref name="afterGui"/> picks the phase (see the class comment).
    /// Returns true when a handler consumed it. Public and free of the scene tree so the selftest can
    /// drive it with a synthetic event and an explicit focus state.
    /// </summary>
    public bool Dispatch(InputEventKey e, KeyContext focus, bool afterGui)
    {
        if (Suspended || !e.Pressed || e.Echo) return false;
        if (!GodotKeyMap.TryGetChord(e, out var chord)) return false;

        var actions = _table.ActionsFor(chord);
        if (actions.Count == 0) return false;

        bool typing = KeyContexts.IsTyping(focus);
        foreach (var action in actions)
        {
            if (action.Kind != KeyActionKind.Press) continue;
            bool worldOnly = action.Context == KeyContext.World;
            if (worldOnly != afterGui) continue;                         // right phase for this action
            if (!KeyContexts.IsLiveIn(action.Context, focus)) continue;  // wrong place for it
            if (typing && TextEditChords.Owns(chord)) continue;          // the field's own chord

            if (!_handlers.TryGetValue(action.Id, out var list)) continue;
            for (int i = 0; i < list.Count; i++)
            {
                var reg = list[i];
                if (!GodotObject.IsInstanceValid(reg.Owner)) { list.RemoveAt(i--); continue; }
                if (reg.Handler()) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Which focus state the keyboard is in. The chat bar's input line is its own state (Enter means
    /// "send" there, "start chatting" in the world); any other LineEdit/TextEdit is a text field; a
    /// control inside a window (a slider, a dropdown, a tree) is <see cref="KeyContext.Window"/>; and
    /// nothing focused, or a HUD widget that is part of the world view, is <see cref="KeyContext.World"/>.
    /// This is the same rule <c>AvatarController</c> uses to decide that movement keys must not move the
    /// avatar, so the two cannot disagree.
    /// </summary>
    public static KeyContext Classify(Control? focusOwner)
    {
        if (focusOwner is LineEdit or TextEdit)
            return IsChatInput(focusOwner) ? KeyContext.ChatInput : KeyContext.TextField;
        for (Node? n = focusOwner; n != null; n = n.GetParent())
            if (n is SLNGWindow) return KeyContext.Window;
        return KeyContext.World;
    }

    private static bool IsChatInput(Control control) => control.IsInGroup(ChatInputGroup);

    /// <summary>Group the chat bar's input line joins, so the dispatcher can tell it from other text fields.</summary>
    public const string ChatInputGroup = "slng_chat_input";
}
