namespace SLNG.Core.Input;

/// <summary>Every action id, as a constant, so the code that handles one cannot misspell it. The
/// strings are persisted - see <see cref="KeyAction"/>.</summary>
public static class KeyActionIds
{
    // Movement
    public const string MoveForward = "move.forward";
    public const string MoveBackward = "move.backward";
    public const string MoveTurnLeft = "move.turn_left";
    public const string MoveTurnRight = "move.turn_right";
    public const string MoveUp = "move.up";
    public const string MoveDown = "move.down";
    public const string MoveToggleFly = "move.toggle_fly";

    // Camera
    public const string CameraOrbitCw = "camera.alt.orbit_cw";
    public const string CameraOrbitCcw = "camera.alt.orbit_ccw";
    public const string CameraZoomIn = "camera.alt.zoom_in";
    public const string CameraZoomOut = "camera.alt.zoom_out";
    public const string CameraOrbitOver = "camera.alt.orbit_over";
    public const string CameraOrbitUnder = "camera.alt.orbit_under";
    public const string CameraPanLeft = "camera.pan_left";
    public const string CameraPanRight = "camera.pan_right";
    public const string CameraPanUp = "camera.pan_up";
    public const string CameraPanDown = "camera.pan_down";
    public const string CameraDollyIn = "camera.dolly_in";
    public const string CameraDollyOut = "camera.dolly_out";
    public const string CameraReset = "camera.reset";

    // Windows
    public const string WindowInventory = "window.inventory";
    public const string WindowOutfits = "window.outfits";
    public const string WindowStats = "window.stats";
    public const string WindowPreferences = "window.preferences";
    public const string WindowConversations = "window.conversations";
    public const string WindowNearbyChat = "window.nearby_chat";
    public const string WindowFriends = "window.friends";
    public const string WindowGroups = "window.groups";
    public const string WindowWorldMap = "window.world_map";
    public const string WindowMiniMap = "window.mini_map";
    public const string WindowSnapshot = "window.snapshot";
    public const string WindowCameraControls = "window.camera_controls";
    public const string WindowNotifications = "window.notifications";
    public const string WindowHoverHeight = "window.hover_height";
    public const string WindowClose = "window.close";
    public const string WindowCloseAll = "window.close_all";

    // Avatar
    public const string AvatarAlwaysRun = "avatar.always_run";
    public const string AvatarStopAnimations = "avatar.stop_animations";
    public const string AvatarSitStand = "avatar.sit_stand";
    public const string AvatarRebake = "avatar.rebake";

    // View
    public const string ViewHideUi = "view.hide_ui";
    public const string ViewZoomIn = "view.zoom_in";
    public const string ViewZoomOut = "view.zoom_out";
    public const string ViewZoomDefault = "view.zoom_default";
    public const string AppExit = "app.exit";

    // Chat
    public const string ChatStart = "chat.start";

    // Editing
    public const string EditCut = "edit.cut";
    public const string EditCopy = "edit.copy";
    public const string EditPaste = "edit.paste";

    // Developer
    public const string DevDrawDistanceDown = "dev.draw_distance_down";
    public const string DevDrawDistanceUp = "dev.draw_distance_up";
    public const string DevSunGizmo = "dev.sun_gizmo";
    public const string DevNearbyObjects = "dev.nearby_objects";
    public const string DevAvatarShadows = "dev.avatar_shadows";
    public const string DevTPose = "dev.tpose";
    public const string DevNdotL = "dev.ndotl";
    public const string DevWireframe = "dev.wireframe";
    public const string DevCreateTestSkin = "dev.create_test_skin";
}
