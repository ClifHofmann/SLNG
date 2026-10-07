using static SLNG.Core.Input.KeyActionIds;

namespace SLNG.Core.Input;

/// <summary>
/// The catalog: every action SLNG can run from a key, with its category, context and default chords.
/// Defaults are the Second Life viewer's own (see <see cref="KeyAction.SlRef"/> and the test that
/// reads <c>key_bindings.xml</c> / <c>menu_viewer.xml</c>), plus the SLNG extras named in
/// <see cref="SlngExtras"/>. An SL shortcut SLNG has no feature for is NOT in here - it is in
/// <see cref="SlShortcutGaps"/>, never bound and never faked.
/// </summary>
public static class KeyActions
{
    private const KeyMods C = KeyMods.Ctrl;
    private const KeyMods A = KeyMods.Alt;
    private const KeyMods S = KeyMods.Shift;

    private static KeyChord Ch(string key, KeyMods mods = KeyMods.None) => new(key, mods);

    private static KeyChord[] Chords(params KeyChord[] chords) => chords;

    public static IReadOnlyList<KeyAction> All { get; } = Build();

    /// <summary>The default chords that are SLNG's own, with the reason each is there. Every other
    /// default in the catalog is the reference viewer's.</summary>
    public static IReadOnlyList<(string ActionId, KeyChord Chord, string Why)> SlngExtras { get; } = new (string, KeyChord, string)[]
    {
        (MoveDown, new KeyChord("Q"), "Q has always crouched / descended in SLNG; kept so nobody's hands have to relearn it."),
        (CameraDollyIn, new KeyChord("Equal"), "Keyboard zoom of the camera distance, there before the table existed."),
        (CameraDollyIn, new KeyChord("KpAdd"), "Same, numeric keypad."),
        (CameraDollyOut, new KeyChord("Minus"), "Same."),
        (CameraDollyOut, new KeyChord("KpSubtract"), "Same, numeric keypad."),
        (DevCreateTestSkin, new KeyChord("T", KeyMods.Ctrl | KeyMods.Alt), "A developer tool that has always used Ctrl+Alt+T. The reference viewer binds that chord to 'Highlight Transparent', which SLNG does not have; rebind either one."),
        (DevDrawDistanceDown, new KeyChord("F3"), "Developer F-keys, not in the reference viewer."),
        (DevDrawDistanceUp, new KeyChord("F4"), "Developer F-keys, not in the reference viewer."),
        (DevSunGizmo, new KeyChord("F5"), "Developer F-keys, not in the reference viewer."),
        (DevNearbyObjects, new KeyChord("F6"), "Developer F-keys, not in the reference viewer."),
        (DevAvatarShadows, new KeyChord("F7"), "Developer F-keys, not in the reference viewer."),
        (DevTPose, new KeyChord("F8"), "Developer F-keys, not in the reference viewer."),
        (DevNdotL, new KeyChord("F9"), "Developer F-keys, not in the reference viewer."),
    };

    private static List<KeyAction> Build()
    {
        const KeyActionKind held = KeyActionKind.Held;
        const KeyActionKind press = KeyActionKind.Press;
        const KeyContext world = KeyContext.World;
        const KeyContext always = KeyContext.Always;
        const KeyContext notTyping = KeyContext.NotInTextField;

        return new List<KeyAction>
        {
            // ---- Movement. Polled each frame by AvatarController; World focus only. Shift is the run
            // modifier, so these keep working with it held. SL's slide (Shift+A/D) is in SlShortcutGaps.
            new(MoveForward, KeyCategory.Movement, world, held, Chords(Ch("W"), Ch("Up")), "key:third_person:push_forward", IgnoreExtraShift: true),
            new(MoveBackward, KeyCategory.Movement, world, held, Chords(Ch("S"), Ch("Down")), "key:third_person:push_backward", IgnoreExtraShift: true),
            new(MoveTurnLeft, KeyCategory.Movement, world, held, Chords(Ch("A"), Ch("Left")), "key:third_person:turn_left", IgnoreExtraShift: true),
            new(MoveTurnRight, KeyCategory.Movement, world, held, Chords(Ch("D"), Ch("Right")), "key:third_person:turn_right", IgnoreExtraShift: true),
            new(MoveUp, KeyCategory.Movement, world, held, Chords(Ch("E"), Ch("PageUp")), "key:third_person:jump", IgnoreExtraShift: true),
            new(MoveDown, KeyCategory.Movement, world, held, Chords(Ch("C"), Ch("PageDown"), Ch("Q")), "key:third_person:push_down", IgnoreExtraShift: true),
            new(MoveToggleFly, KeyCategory.Movement, world, press, Chords(Ch("F"), Ch("Home")), "key:third_person:toggle_fly"),

            // ---- Camera on Alt (key_bindings.xml "Camera controls in third person on Alt"). Polled.
            new(CameraOrbitCw, KeyCategory.Camera, world, held, Chords(Ch("A", A), Ch("Left", A)), "key:third_person:spin_around_cw"),
            new(CameraOrbitCcw, KeyCategory.Camera, world, held, Chords(Ch("D", A), Ch("Right", A)), "key:third_person:spin_around_ccw"),
            new(CameraZoomIn, KeyCategory.Camera, world, held, Chords(Ch("W", A), Ch("Up", A)), "key:third_person:move_forward"),
            new(CameraZoomOut, KeyCategory.Camera, world, held, Chords(Ch("S", A), Ch("Down", A)), "key:third_person:move_backward"),
            new(CameraOrbitOver, KeyCategory.Camera, world, held, Chords(Ch("E", A), Ch("PageUp", A), Ch("W", C | A), Ch("Up", C | A)), "key:third_person:spin_over"),
            new(CameraOrbitUnder, KeyCategory.Camera, world, held, Chords(Ch("C", A), Ch("PageDown", A), Ch("S", C | A), Ch("Down", C | A)), "key:third_person:spin_under"),
            new(CameraPanLeft, KeyCategory.Camera, world, held, Chords(Ch("A", C | A | S), Ch("Left", C | A | S)), "key:third_person:pan_left"),
            new(CameraPanRight, KeyCategory.Camera, world, held, Chords(Ch("D", C | A | S), Ch("Right", C | A | S)), "key:third_person:pan_right"),
            new(CameraPanUp, KeyCategory.Camera, world, held, Chords(Ch("W", C | A | S), Ch("Up", C | A | S)), "key:third_person:pan_up"),
            new(CameraPanDown, KeyCategory.Camera, world, held, Chords(Ch("S", C | A | S), Ch("Down", C | A | S)), "key:third_person:pan_down"),
            new(CameraDollyIn, KeyCategory.Camera, world, held, Chords(Ch("Equal"), Ch("KpAdd"))),
            new(CameraDollyOut, KeyCategory.Camera, world, held, Chords(Ch("Minus"), Ch("KpSubtract"))),
            new(CameraReset, KeyCategory.Camera, world, press, Chords(Ch("Escape")), "menu:Reset View"),

            // ---- Windows. Menu-style shortcuts: they fire wherever focus is (also while typing).
            new(WindowInventory, KeyCategory.Windows, always, press, Chords(Ch("I", C)), "menu:Inventory"),
            new(WindowOutfits, KeyCategory.Windows, always, press, Chords(Ch("O", C)), "menu:NowWearing"),
            new(WindowStats, KeyCategory.Windows, always, press, Chords(Ch("1", C | S)), "menu:Statistics Bar"),
            new(WindowPreferences, KeyCategory.Windows, always, press, Chords(Ch("P", C)), "menu:Preferences"),
            new(WindowConversations, KeyCategory.Windows, always, press, Chords(Ch("T", C)), "menu:Conversations"),
            new(WindowNearbyChat, KeyCategory.Windows, always, press, Chords(Ch("H", C)), "menu:Nearby Chat"),
            new(WindowFriends, KeyCategory.Windows, always, press, Chords(Ch("F", C | S)), "menu:My Friends"),
            new(WindowGroups, KeyCategory.Windows, always, press, Chords(Ch("G", C | S)), "menu:My Groups"),
            new(WindowWorldMap, KeyCategory.Windows, always, press, Chords(Ch("M", C)), "menu:World Map"),
            new(WindowMiniMap, KeyCategory.Windows, always, press, Chords(Ch("M", C | S)), "menu:Mini-Map"),
            new(WindowLandmarks, KeyCategory.Windows, always, press, Chords(Ch("L", C)), "menu:Places"),
            new(WindowSnapshot, KeyCategory.Windows, always, press, Chords(Ch("S", C | S)), "menu:Take Snapshot"),
            new(WindowCameraControls, KeyCategory.Windows, always, press, Chords(Ch("K", C)), "menu:Camera Controls"),
            new(WindowNotifications, KeyCategory.Windows, always, press, Chords(Ch("N", A | S)), "menu:Notifications"),
            new(WindowHoverHeight, KeyCategory.Windows, always, press, Chords(Ch("H", C | A)), "menu:Hover Height"),
            new(WindowClose, KeyCategory.Windows, always, press, Chords(Ch("W", C)), "menu:Close Window"),
            new(WindowCloseAll, KeyCategory.Windows, always, press, Chords(Ch("W", C | S)), "menu:Close All Windows"),

            // ---- Avatar.
            new(AvatarAlwaysRun, KeyCategory.Avatar, always, press, Chords(Ch("R", C)), "menu:Always Run"),
            new(AvatarStopAnimations, KeyCategory.Avatar, always, press, Chords(Ch("A", A | S)), "menu:Stop Animating My Avatar"),
            new(AvatarSitStand, KeyCategory.Avatar, always, press, Chords(Ch("S", A | S)), "menu:Sit stand"),
            new(AvatarRebake, KeyCategory.Avatar, always, press, Chords(Ch("R", C | A)), "menu:Rebake Texture"),

            // ---- View.
            new(ViewHideUi, KeyCategory.View, always, press, Chords(Ch("U", C | S)), "menu:Hide UI"),
            new(ViewZoomIn, KeyCategory.View, always, press, Chords(Ch("0", C)), "menu:Zoom In"),
            new(ViewZoomOut, KeyCategory.View, always, press, Chords(Ch("8", C)), "menu:Zoom Out"),
            new(ViewZoomDefault, KeyCategory.View, always, press, Chords(Ch("9", C)), "menu:Zoom Default"),
            new(AppExit, KeyCategory.View, always, press, Chords(Ch("Q", C)), "menu:Quit"),

            // ---- Chat. Enter with the world focused starts typing; Enter inside the chat bar sends and
            // is the field's own (not in this table).
            new(ChatStart, KeyCategory.Chat, world, press, Chords(Ch("Enter")), "key:third_person:start_chat"),

            // ---- Editing. Never while typing: there Ctrl+X/C/V are the field's own clipboard.
            new(EditCut, KeyCategory.Editing, notTyping, press, Chords(Ch("X", C)), "menu:Cut"),
            new(EditCopy, KeyCategory.Editing, notTyping, press, Chords(Ch("C", C)), "menu:Copy"),
            new(EditPaste, KeyCategory.Editing, notTyping, press, Chords(Ch("V", C)), "menu:Paste"),

            // ---- Developer. The F-keys are SLNG's own; they have always fired while typing.
            new(DevDrawDistanceDown, KeyCategory.Developer, always, press, Chords(Ch("F3"))),
            new(DevDrawDistanceUp, KeyCategory.Developer, always, press, Chords(Ch("F4"))),
            new(DevSunGizmo, KeyCategory.Developer, always, press, Chords(Ch("F5"))),
            new(DevNearbyObjects, KeyCategory.Developer, always, press, Chords(Ch("F6"))),
            new(DevAvatarShadows, KeyCategory.Developer, always, press, Chords(Ch("F7"))),
            new(DevTPose, KeyCategory.Developer, always, press, Chords(Ch("F8"))),
            new(DevNdotL, KeyCategory.Developer, always, press, Chords(Ch("F9"))),
            new(DevWireframe, KeyCategory.Developer, always, press, Chords(Ch("R", C | S)), "menu:Wireframe"),
            new(DevCreateTestSkin, KeyCategory.Developer, always, press, Chords(Ch("T", C | A))),
        };
    }
}
