namespace SLNG.Core.Input;

/// <summary>Where an action is listed on the Keyboard page. Also the language-independent
/// grouping key for the localized category headings.</summary>
public enum KeyCategory
{
    Movement,
    Camera,
    Windows,
    Avatar,
    View,
    Chat,
    Editing,
    Developer,
}

/// <summary>How an action is consumed.</summary>
public enum KeyActionKind
{
    /// <summary>Fires once per key press (never on key repeat). Handled by the dispatcher.</summary>
    Press,

    /// <summary>Is a state for as long as the key is down: walking, orbiting. NOT dispatched - the
    /// controller asks the table every frame whether it is held.</summary>
    Held,
}

/// <summary>
/// One thing a shortcut can do. The id is a stable string (<c>window.inventory</c>) that appears in
/// the saved file, the code and the tests, and is never renamed; the label is looked up in the
/// language files under <c>ui.keys.action.&lt;id with '.' replaced by '_'&gt;</c>, so Core stays free
/// of any language.
///
/// <para><see cref="SlRef"/> names where the default comes from in the reference viewer:
/// <c>menu:&lt;item name&gt;</c> is an item of <c>menu_viewer.xml</c> and
/// <c>key:&lt;mode&gt;:&lt;command&gt;</c> is a command in <c>key_bindings.xml</c>. A test checks the
/// defaults against those files, so a typo here cannot pass as "like Second Life". Null means the
/// action (or its default) is SLNG's own.</para>
/// </summary>
public sealed record KeyAction(
    string Id,
    KeyCategory Category,
    KeyContext Context,
    KeyActionKind Kind,
    IReadOnlyList<KeyChord> Defaults,
    string? SlRef = null,
    bool IgnoreExtraShift = false)
{
    /// <summary>
    /// Whether this action is triggered by <paramref name="pressed"/> given the chord
    /// <paramref name="bound"/> is one of its bindings. Modifiers must match exactly - W walks, Alt+W
    /// zooms the camera, Ctrl+W is a shortcut and walks nobody - with one deliberate exception:
    /// <see cref="IgnoreExtraShift"/>, for the avatar movement keys, where Shift is the run modifier
    /// and W+Shift is still "forward".
    /// </summary>
    public bool Claims(KeyChord bound, KeyChord pressed)
    {
        if (bound == pressed) return true;
        return IgnoreExtraShift && !bound.Shift && pressed.Shift
            && bound.Key == pressed.Key && bound.Mods == (pressed.Mods & ~KeyMods.Shift);
    }
}
