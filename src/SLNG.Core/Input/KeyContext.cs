namespace SLNG.Core.Input;

/// <summary>
/// WHERE a shortcut may fire, expressed as the set of keyboard-focus states it is live in. The
/// focus states are what the dispatcher can actually tell apart: the world has focus (nothing
/// focused, or a HUD widget that is part of the world view), an ordinary window control has focus,
/// a text field has focus, or the chat bar's own input line has focus.
///
/// <para>Two actions on the same chord only conflict when their contexts share a state
/// (<see cref="Overlaps"/>). That is what lets <c>Enter</c> mean "start chatting" with the world
/// focused and "send" inside the chat bar, or lets a future per-window shortcut reuse a letter.</para>
/// </summary>
[Flags]
public enum KeyContext
{
    None = 0,

    /// <summary>State: nothing is focused that wants the keyboard. Movement and camera live here.</summary>
    World = 1,

    /// <summary>State: a non-text control inside a window has focus (a slider, a dropdown, a tree).</summary>
    Window = 2,

    /// <summary>State: a LineEdit / TextEdit other than the chat bar's input has focus.</summary>
    TextField = 4,

    /// <summary>State: the chat bar's input line has focus.</summary>
    ChatInput = 8,

    /// <summary>Menu-style shortcuts (Ctrl+I, F3): fire wherever focus is, including while typing,
    /// except for the chords a text field owns for its own editing (see <see cref="TextEditChords"/>).</summary>
    Always = World | Window | TextField | ChatInput,

    /// <summary>Shortcuts that must never fire while the person is typing (Ctrl+X/C/V on the inventory).</summary>
    NotInTextField = World | Window,
}

public static class KeyContexts
{
    /// <summary>Whether two contexts can both be live at once, i.e. whether one chord on two
    /// actions would be ambiguous.</summary>
    public static bool Overlaps(KeyContext a, KeyContext b) => (a & b) != 0;

    /// <summary>Whether an action with context <paramref name="actionContext"/> may fire while
    /// <paramref name="focus"/> (a single state) is current.</summary>
    public static bool IsLiveIn(KeyContext actionContext, KeyContext focus) => (actionContext & focus) != 0;

    /// <summary>True for the two focus states in which the person is typing into a text field.</summary>
    public static bool IsTyping(KeyContext focus) => (focus & (KeyContext.TextField | KeyContext.ChatInput)) != 0;
}
