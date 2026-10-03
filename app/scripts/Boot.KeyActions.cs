using Godot;
using SLNG.App;
using SLNG.Core.Components;
using SLNG.Core.Input;
using static SLNG.Core.Input.KeyActionIds;

/// <summary>
/// FEAT-UI-43: what the keys that belong to Boot DO. The key checks that used to sit in
/// <c>Boot._Input</c> are gone; each is now a handler registered with the <see cref="KeyDispatcher"/>
/// under an action id, and the chord comes from <see cref="KeyBindings.Table"/>. The bodies are the
/// old ones, unchanged, except where a comment says otherwise.
/// </summary>
public partial class Boot
{
    private KeyDispatcher _keyDispatcher = null!;

    /// <summary>Loads the saved bindings (read only) and creates the dispatcher. Must run before the
    /// renderers and controllers are created, because they register their own handlers from their
    /// <c>_Ready</c>.</summary>
    private void SetupKeyBindings()
    {
        KeyBindings.Load();
        _keyDispatcher = new KeyDispatcher();
        AddChild(_keyDispatcher);
        var keys = _keyDispatcher;

        // ---- Developer keys. The F-keys fire while typing, as they always did.
        keys.Register(this, DevDrawDistanceDown, () =>
        {
            // Through GraphicsSettings rather than writing RenderConfig directly: the Graphics
            // tab's slider reads from it, and a key that wrote RenderConfig directly would
            // leave the slider showing a stale number and overwrite the change on the next apply.
            _graphicsSettings.SetDrawDistance(Mathf.Max(32f, _graphicsSettings.DrawDistance - 16f));
            ApplyGraphicsSettings();
            LogMessage($"Draw distance: {RenderConfig.DrawDistance:0} m");
        });
        keys.Register(this, DevDrawDistanceUp, () =>
        {
            _graphicsSettings.SetDrawDistance(Mathf.Min(512f, _graphicsSettings.DrawDistance + 16f));
            ApplyGraphicsSettings();
            LogMessage($"Draw distance: {RenderConfig.DrawDistance:0} m");
        });
        keys.Register(this, DevSunGizmo, ToggleSunGizmo);
        // "What is around me, and is it being drawn?" -- the one question the click diagnostics cannot
        // answer, because clicking needs the object to be rendered and the objects worth asking about are
        // the ones that are NOT.
        keys.Register(this, DevNearbyObjects, () => _objectRenderer?.LogNearbyObjects(32f));
        keys.Register(this, DevWireframe, () => _topMenu.OnToggleWireframe?.Invoke());
        // Creates items and uploads assets, so it stays a deliberate keystroke (FEAT-AVATAR-01).
        keys.Register(this, DevCreateTestSkin, CreateTestSkin);

        // ---- Windows.
        // Statistics: Ctrl+Shift+1 is the statistics shortcut in SL/Firestorm.
        keys.Register(this, WindowStats, () => _statsOverlay?.Toggle());
        // Ctrl+I like the real viewers; a bare I would fire while typing in chat.
        keys.Register(this, WindowInventory, () => _inventoryPanel?.Toggle());
        // Ctrl+O -- the inventory straight on the Outfits tab (FEAT-INV-04).
        keys.Register(this, WindowOutfits, () => _inventoryPanel?.OpenOnOutfits());
        keys.Register(this, WindowPreferences, TogglePreferencesWindow);
        // The windows live on the HUD layer, which is hidden until a login succeeds: before that these keys do
        // nothing (and the key is not consumed), so a window never opens invisibly behind the login screen.
        keys.Register(this, WindowConversations, () => Launch("chat"));
        keys.Register(this, WindowNearbyChat, () => InWorld && ToggleChatPage(SLNG.App.UI.ChatWindow.Page.Chat));
        keys.Register(this, WindowFriends, () => InWorld && ToggleChatPage(SLNG.App.UI.ChatWindow.Page.Friends));
        keys.Register(this, WindowGroups, () => InWorld && ToggleChatPage(SLNG.App.UI.ChatWindow.Page.Groups));
        keys.Register(this, WindowWorldMap, () => Launch("worldmap"));
        keys.Register(this, WindowMiniMap, () => Launch("minimap"));
        keys.Register(this, WindowSnapshot, () => Launch("snapshot"));
        keys.Register(this, WindowCameraControls, () => Launch("camera"));
        keys.Register(this, WindowNotifications, () => Launch("notifications"));
        keys.Register(this, WindowHoverHeight, () =>
        {
            if (!InWorld) return false;
            _topMenu.OnOpenHoverHeight?.Invoke();
            return true;
        });
        keys.Register(this, WindowClose, () => CloseWindows(all: false));
        keys.Register(this, WindowCloseAll, () => CloseWindows(all: true));

        // ---- Avatar.
        // Ctrl+Alt+R -- rebake the avatar, the same shortcut the real viewer uses (FEAT-AVATAR-01).
        keys.Register(this, AvatarRebake, RebakeAvatar);
        keys.Register(this, AvatarStopAnimations, () => _topMenu.OnStopAnimations?.Invoke());
        keys.Register(this, AvatarSitStand, ToggleSitStand);

        // ---- View.
        keys.Register(this, ViewHideUi, () =>
        {
            if (!InWorld) return false; // would otherwise show the (hidden) HUD over the login screen
            _topMenu.OnToggleHud?.Invoke();
            return true;
        });
        keys.Register(this, ViewZoomIn, () => ZoomFieldOfView(1f / FovZoomStep));
        keys.Register(this, ViewZoomOut, () => ZoomFieldOfView(FovZoomStep));
        keys.Register(this, ViewZoomDefault, () => ZoomFieldOfView(null));
        keys.Register(this, AppExit, () => QuitGracefully(true));

        // ---- Chat.
        keys.Register(this, ChatStart, StartChatting);
    }

    /// <summary>Connected to a grid, i.e. the HUD layer is the thing on screen.</summary>
    private bool InWorld => _session?.IsConnected == true;

    /// <summary>Toggles a launcher window by its toolbar id, in the world only.</summary>
    private bool Launch(string launcherId)
    {
        if (!InWorld) return false;
        InvokeLauncher(launcherId);
        return true;
    }

    /// <summary>The reference viewer's View.ZoomIn/ZoomOut step (llviewermenu.cpp, <c>LLZoomer(1.2f)</c> and
    /// <c>LLZoomer(1/1.2f)</c>): the field of view is multiplied by this, or by its inverse.</summary>
    private const float FovZoomStep = 1.2f;

    /// <summary>View -> Zoom in / out / default: changes the camera's field of view, like the viewer
    /// (NOT the camera distance, which the wheel and the Alt keys move). <paramref name="factor"/> null
    /// = back to the default angle. Persists, as the Camera preferences slider does.</summary>
    private void ZoomFieldOfView(float? factor)
    {
        if (_cameraSettings == null) return;
        _cameraSettings.SetFov(factor is { } f
            ? Mathf.Clamp(_cameraSettings.Fov * f, SLNG.App.UI.CameraSettings.MinFov, SLNG.App.UI.CameraSettings.MaxFov)
            : SLNG.App.UI.CameraSettings.DefaultFov);
    }

    private void TogglePreferencesWindow()
    {
        if (_preferencesWindow.Visible && !_preferencesWindow.IsMinimized) _preferencesWindow.Visible = false;
        else _topMenu.OnOpenPreferences?.Invoke();
    }

    /// <summary>Nearby Chat / Friends / Groups are pages of the one Communication window. Open it on that
    /// page; if it is already showing that page, close it - the viewer's shortcuts toggle.</summary>
    private bool ToggleChatPage(SLNG.App.UI.ChatWindow.Page page)
    {
        if (_chatWindow.IsShowing(page))
        {
            InvokeLauncher("chat"); // shown on this page: toggles it closed
            return true;
        }
        if (!_chatWindow.Visible || _chatWindow.IsMinimized) InvokeLauncher("chat"); // opens / un-minimizes
        _chatWindow.ShowPage(page);
        return true;
    }

    /// <summary>Enter with the world focused: open the chat bar and start typing (key_bindings.xml
    /// <c>start_chat</c>). Only while in the world - at the login screen Enter belongs to the form.</summary>
    private bool StartChatting()
    {
        if (!InWorld) return false;
        if (!_chatWindow.Visible || _chatWindow.IsMinimized) InvokeLauncher("chat");
        _chatWindow.FocusChatInput();
        return true;
    }

    /// <summary>Close Window / Close All Windows: the frontmost visible window of the HUD layer (the last
    /// child is drawn on top), or every one of them. Closed exactly as the title bar's x does.</summary>
    private bool CloseWindows(bool all)
    {
        var hud = GetNodeOrNull<CanvasLayer>("HudLayer");
        if (hud == null || !hud.Visible) return false;

        bool closedAny = false;
        for (int i = hud.GetChildCount() - 1; i >= 0; i--)
        {
            if (hud.GetChild(i) is not SLNG.App.UI.SLNGWindow { Visible: true } window) continue;
            window.CloseFromTitleBar();
            closedAny = true;
            if (!all) break;
        }
        return closedAny;
    }

    /// <summary>Sit / stand: standing up takes the Stand button's own path (which also stops the seat's
    /// animations); otherwise sit on the ground, as "Sit Here" does.</summary>
    private bool ToggleSitStand()
    {
        if (_session == null) return false;
        GetLocalAgentTransform(); // populates _localAgent if the world has the agent
        var avatar = _localAgent?.GetComponent<AvatarComponent>();
        if (avatar == null) return false;

        if (avatar.SittingOnLocalId != 0) _standUpButton.EmitSignal(BaseButton.SignalName.Pressed);
        else _session.SitOnGround();
        return true;
    }
}
