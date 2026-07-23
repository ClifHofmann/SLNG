using Godot;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Net;
using SLNG.App;
using System.Linq;

public partial class Boot : Control
{
    private OptionButton _profileDropdown = null!;
    private OptionButton _gridDropdown = null!;
    private LineEdit _gridInput = null!;
    private LineEdit _firstInput = null!;
    private LineEdit _lastInput = null!;
    private LineEdit _passInput = null!;
    private CheckBox _saveLoginCheck = null!;
    private Button _loginButton = null!;
    private RichTextLabel _logPanel = null!;

    private ConfigFile _loginsConfig = new ConfigFile();
    private Godot.Collections.Array<string> _savedProfiles = new();

    private GridSession? _session;
    private SLNG.Core.ECS.World? _world;
    private SLNG.Core.Services.LocalizationManager _localizationManager = null!;
    private SLNG.Core.WorldSimulation _worldSimulation = null!;
    private GpuCache? _gpuCache;
    private TerrainRenderer? _terrainRenderer;
    private ObjectRenderer? _objectRenderer;
    private AvatarRenderer? _avatarRenderer;
    private SLNG.Assets.AssetService? _assetService;
    private AvatarController? _avatarController;
    private VBoxContainer _vboxContainer = null!;
    
    private WorldEnvironment? _worldEnvironment;
    private bool _postFxEnabled = true;
    private DirectionalLight3D? _sun;
    private SLNG.App.UI.InventoryPanel? _inventoryPanel;
    private Node3D? _sunGizmo;

    private Label _hudLabel = null!;
    private double _hudAccum;
    
    private SLNG.App.UI.TopMenu _topMenu = null!;
    private SLNG.App.UI.ButtonBar _buttonBar = null!;
    private SLNG.App.UI.PreferencesWindow _preferencesWindow = null!;
    private SLNG.App.UI.ToolbarSettings _toolbarSettings = null!;
    private SLNG.App.UI.UiSettings _uiSettings = null!;

    // M5-3 Tabbed Chat window
    private SLNG.App.UI.ChatWindow _chatWindow = null!;
    private SLNG.Core.Services.ChatLogger _chatLogger = null!;
    
    // M5-2 Object Editing UI
    private ObjectSelectionController _objectSelectionController = null!;
    private SLNG.App.CursorManager _cursorManager = null!;
    private SLNG.App.UI.InWorldContextMenu _inWorldContextMenu = null!;

    // One independent ObjectEditWindow per edited object (keyed by its ECS Entity.Id) so
    // multiple objects can be open and edited at the same time instead of sharing one floater.
    private readonly System.Collections.Generic.Dictionary<System.Guid, SLNG.App.UI.ObjectEditWindow> _objectEditWindows = new();

    public const string AppVersion = "v0.2.13-alpha";

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        
        string i18nDir = ProjectSettings.GlobalizePath("res://i18n");
        _localizationManager = new SLNG.Core.Services.LocalizationManager(i18nDir);
        SLNG.App.UI.L10n.Initialize(_localizationManager);

        // Godot debug builds hard-code an "(DEBUG)" window-title suffix that gets applied
        // AFTER _Ready() runs, silently overwriting whatever title we set here a moment later
        // — a known engine behavior (godotengine/godot#104321), not an SLNG bug. Re-asserting
        // the title once more after the next frame renders lands after that internal logic and
        // sticks; the immediate call below just avoids a flash of the wrong title before then.
        DisplayServer.WindowSetTitle($"Puris Viewer {AppVersion}");
        RenderingServer.FramePostDraw += ReassertWindowTitleOnce;

        _vboxContainer = GetNode<VBoxContainer>("%VBoxContainer");
        _profileDropdown = GetNode<OptionButton>("%ProfileDropdown");
        _gridDropdown = GetNode<OptionButton>("%GridDropdown");
        _gridInput = GetNode<LineEdit>("%GridInput");
        _firstInput = GetNode<LineEdit>("%FirstInput");
        _lastInput = GetNode<LineEdit>("%LastInput");

        // Populate Grid Dropdown
        _gridDropdown.AddItem("OSGrid");
        _gridDropdown.SetItemMetadata(0, "http://hg.osgrid.org/");
        _gridDropdown.AddItem("Second Life (Agni)");
        _gridDropdown.SetItemMetadata(1, "https://login.agni.lindenlab.com/cgi-bin/login.cgi");
        _gridDropdown.AddItem("Second Life (Aditi)");
        _gridDropdown.SetItemMetadata(2, "https://login.aditi.lindenlab.com/cgi-bin/login.cgi");
        _gridDropdown.AddItem("Localhost");
        _gridDropdown.SetItemMetadata(3, "http://127.0.0.1:9000/");
        
        _gridDropdown.ItemSelected += (index) => 
        {
            _gridInput.Text = (string)_gridDropdown.GetItemMetadata((int)index);
        };
        _passInput = GetNode<LineEdit>("%PassInput");
        _saveLoginCheck = GetNode<CheckBox>("%SaveLoginCheck");
        _loginButton = GetNode<Button>("%LoginButton");
        _logPanel = GetNode<RichTextLabel>("%LogPanel");

        var versionLabel = GetNodeOrNull<Label>("%VersionLabel");
        if (versionLabel != null) versionLabel.Text = AppVersion;

        var loginVersionText = GetNodeOrNull<Label>("%VersionText");
        if (loginVersionText != null) loginVersionText.Text = AppVersion;

        var loadingVersionText = GetNodeOrNull<Label>("%LoadingVersionText");
        if (loadingVersionText != null) loadingVersionText.Text = AppVersion;

        _loginButton.Pressed += OnLoginPressed;
        _profileDropdown.ItemSelected += OnProfileSelected;
        GetTree().Root.SizeChanged += OnWindowSizeChanged;

        LoadProfiles();

        _terrainRenderer = new TerrainRenderer();
        AddChild(_terrainRenderer);

        _objectRenderer = new ObjectRenderer();
        AddChild(_objectRenderer);
        
        _avatarRenderer = new AvatarRenderer();
        AddChild(_avatarRenderer);
        
        SetupEnvironment();
        SetupHud();
        SetupTopMenu();

        LogMessage("Ready. Enter credentials and click Login.");
    }

    private void ReassertWindowTitleOnce()
    {
        RenderingServer.FramePostDraw -= ReassertWindowTitleOnce;
        DisplayServer.WindowSetTitle($"Puris Viewer {AppVersion}", GetWindow().GetWindowId());
    }

    private void SetupTopMenu()
    {
        _topMenu = new SLNG.App.UI.TopMenu();
        _topMenu.Visible = false; // Hide until logged in
        AddChild(_topMenu);

        _topMenu.OnDisconnect = () => {
            if (_session != null)
            {
                LogMessage("Disconnecting...");
                _session.Dispose();
                _session = null;
                GetNode<Control>("%LoginScreen").Visible = true;
                GetNode<Control>("%Background").Visible = true;
                _topMenu.Visible = false;
                var hudLayer = GetNodeOrNull<CanvasLayer>("HudLayer");
                if (hudLayer != null) hudLayer.Visible = false;
                _chatWindow.Visible = false;
                if (_inventoryPanel != null) { _inventoryPanel.QueueFree(); _inventoryPanel = null; }
                Input.MouseMode = Input.MouseModeEnum.Visible;
            }
        };

        _topMenu.OnExit = () => {
            GetTree().Quit();
        };

        _topMenu.OnToggleHud = () => {
            var hudLayer = GetNodeOrNull<CanvasLayer>("HudLayer");
            if (hudLayer != null) hudLayer.Visible = !hudLayer.Visible;
        };

        _topMenu.OnToggleCameraHud = () => {
            var hudLayer = GetNodeOrNull<CanvasLayer>("HudLayer");
            if (hudLayer != null)
            {
                var cameraHud = hudLayer.GetNodeOrNull<Control>("CameraHUD");
                if (cameraHud != null) cameraHud.Visible = !cameraHud.Visible;
            }
        };

        _topMenu.OnCameraMode = (mode) => {
            // Future integration with FreeCamera/AvatarController
            LogMessage($"Camera mode changed to {mode}");
        };

        _topMenu.OnToggleWireframe = () => {
            var vp = GetViewport();
            vp.DebugDraw = vp.DebugDraw == Viewport.DebugDrawEnum.Wireframe 
                ? Viewport.DebugDrawEnum.Disabled 
                : Viewport.DebugDrawEnum.Wireframe;
        };

        _topMenu.OnOpenPreferences = () => {
            _preferencesWindow.Visible = true;
        };

        _topMenu.OnCreateLandmark = () => {
            var hudLayer = GetNodeOrNull<CanvasLayer>("HudLayer");
            if (hudLayer != null) OpenCreateLandmarkWindow(hudLayer);
        };
    }

    private void SetupHud()
    {
        // Loaded before any SLNGWindow is constructed below, so CameraHUD/InventoryPanel/
        // ChatWindow etc. all pick up the saved scale in their own _Ready() instead of
        // flashing at 1.0x first (FEAT-UI-07).
        _uiSettings = new SLNG.App.UI.UiSettings();
        _uiSettings.Load();
        
        // Apply saved language setting
        _localizationManager.CurrentLocale = _uiSettings.Language;

        // Position/altitude readout in the top-right corner, overlaying the 3D view.
        // On its own CanvasLayer so it always draws on top of the world and the login/chat
        // Controls, regardless of scene-tree order.
        var hudLayer = new CanvasLayer { Name = "HudLayer", Layer = 10, Visible = false };
        AddChild(hudLayer);

        _hudLabel = new Label
        {
            Name = "PositionHud",
            HorizontalAlignment = HorizontalAlignment.Right,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Text = "connecting…",
        };
        // Anchor to the bottom-right corner to avoid overlapping the Login UI at the top.
        _hudLabel.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        _hudLabel.GrowHorizontal = Control.GrowDirection.Begin;
        _hudLabel.GrowVertical = Control.GrowDirection.Begin;
        _hudLabel.OffsetBottom = -12;
        _hudLabel.OffsetRight = -12;
        // Dark outline + larger font so white text stays legible over bright sky or pale objects.
        _hudLabel.AddThemeColorOverride("font_color", new Color(1, 1, 1));
        _hudLabel.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        _hudLabel.AddThemeConstantOverride("outline_size", 5);
        _hudLabel.AddThemeFontSizeOverride("font_size", 18);
        hudLayer.AddChild(_hudLabel);
        GD.Print("[HUD] position label created on CanvasLayer");

        var cameraHud = new SLNG.App.UI.CameraHUD();
        cameraHud.Name = "CameraHUD";
        hudLayer.AddChild(cameraHud);

        _inventoryPanel = new SLNG.App.UI.InventoryPanel { Name = "InventoryPanel" };
        hudLayer.AddChild(_inventoryPanel);

        _inWorldContextMenu = new SLNG.App.UI.InWorldContextMenu();
        hudLayer.AddChild(_inWorldContextMenu);

        _inWorldContextMenu.OnEditClicked = (entity, localId) => OpenObjectEditWindow(hudLayer, entity, localId);
        _inWorldContextMenu.OnTouchClicked = (entity, localId) => { /* Touch logic later */ };
        _inWorldContextMenu.OnInspectClicked = (entity, localId) => { /* Inspect logic later */ };
        _inWorldContextMenu.OnDeleteClicked = (entity, localId) => { /* Delete logic later */ };
        _inWorldContextMenu.OnCreatePrimClicked = (godotPos, type) =>
        {
            if (_session == null) return;
            _session.CreatePrim(type, RenderConfig.FromGodot(_session.CurrentRegionHandle, godotPos));
        };

        _chatLogger = new SLNG.Core.Services.ChatLogger();
        _chatWindow = new SLNG.App.UI.ChatWindow { Name = "ChatWindow" };
        hudLayer.AddChild(_chatWindow);
        _chatWindow.Initialize(_chatLogger);
        // Captures _session by reference (not by value at wiring time) so this keeps working
        // across the session getting replaced on re-login, same pattern as OnCreatePrimClicked above.
        _chatWindow.OnSendLocalChat = (text) => _session?.SendChat(text);

        SetupButtonBarAndPreferences(hudLayer, cameraHud);
    }

    /// <summary>
    /// Opens (or refocuses) the Build/Inspector window for one object. Each object gets its own
    /// independent window instance -- keyed by ECS Entity.Id, not a single shared floater -- so
    /// several objects can be open and edited at the same time without one click stealing the
    /// window another object was using.
    /// </summary>
    private void OpenObjectEditWindow(CanvasLayer hudLayer, SLNG.Core.ECS.Entity entity, uint localId)
    {
        if (_session == null || _world == null) return; // only reachable post-login, where both are set

        if (_objectEditWindows.TryGetValue(entity.Id, out var existing))
        {
            existing.MoveToFront();
            return;
        }

        var win = new SLNG.App.UI.ObjectEditWindow();
        hudLayer.AddChild(win);
        win.Initialize(_session, _world);
        // Cascade new windows diagonally so opening several doesn't stack them exactly on top
        // of each other -- wraps every 8 so it doesn't walk off-screen over a long session.
        win.CascadeIndex = _objectEditWindows.Count % 8;
        // Pinned so ObjectSelectionController won't drop this entity's selection/highlight just
        // because the user clicked a different object elsewhere -- see ObjectSelectionController
        // for why plain clicks otherwise replace the previous highlight.
        _objectSelectionController.Pin(entity.Id);
        win.Closed += () =>
        {
            _objectEditWindows.Remove(entity.Id);
            _objectSelectionController.Unpin(entity.Id);
        };
        _objectEditWindows[entity.Id] = win;

        win.EditObject(entity, localId, _world);
    }

    /// <summary>
    /// Opens a fresh "Create Landmark" dialog for the agent's current location. Each press gets
    /// its own instance (freed on close) rather than a persisted one, same one-shot pattern as
    /// <see cref="OpenObjectEditWindow"/> -- there's no state to keep between uses, and re-reading
    /// <c>_session</c> here (not captured at toolbar-wiring time) keeps this working across
    /// re-login, same reasoning as the OnCreatePrimClicked wiring above.
    /// </summary>
    private void OpenCreateLandmarkWindow(CanvasLayer hudLayer)
    {
        var win = new SLNG.App.UI.CreateLandmarkWindow();
        hudLayer.AddChild(win);
        win.Initialize(_session);
        // If the Landmarks folder (or the subfolder just created into) happens to already be
        // expanded in the Inventory panel, refresh it so the new item shows up immediately --
        // otherwise it's invisible until the user manually collapses/re-expands that folder.
        win.OnLandmarkCreated = (folderId, itemId, assetId) => _inventoryPanel?.RefreshFolder(folderId, itemId, assetId);
        win.OpenForCurrentLocation();
    }

    /// <summary>
    /// Bottom button bar (Camera Controls / Inventory toggle icons) plus the Preferences
    /// window that lets the user pick which buttons are enabled. ToolbarItemDefinition is
    /// the single registry both widgets work from -- adding a third toggleable panel later
    /// means adding one more entry to toolbarItems here, nothing else.
    /// </summary>
    private void SetupButtonBarAndPreferences(CanvasLayer hudLayer, SLNG.App.UI.CameraHUD cameraHud)
    {
        var toolbarItems = new System.Collections.Generic.List<SLNG.App.UI.ToolbarItemDefinition>
        {
            new("chat", "Chat", "chat", () => _chatWindow.Visible = !_chatWindow.Visible, () => _chatWindow.Visible),
            new("camera", "Camera Controls", "photo_camera", () => cameraHud.Toggle(), () => cameraHud.Visible),
            new("inventory", "Inventory", "inventory_2", () => _inventoryPanel?.Toggle(), () => _inventoryPanel?.Visible ?? false),
        };

        _toolbarSettings = new SLNG.App.UI.ToolbarSettings();
        _toolbarSettings.EnsureDefaults(toolbarItems.ConvertAll(i => i.Id));
        _toolbarSettings.Load();

        _buttonBar = new SLNG.App.UI.ButtonBar { Name = "ButtonBar" };
        _buttonBar.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        hudLayer.AddChild(_buttonBar);
        _buttonBar.Initialize(toolbarItems, _toolbarSettings);

        _preferencesWindow = new SLNG.App.UI.PreferencesWindow { Name = "PreferencesWindow" };
        hudLayer.AddChild(_preferencesWindow);
        var toolbarPage = new SLNG.App.UI.ToolbarPreferencesPage();
        _preferencesWindow.AddTab(SLNG.App.UI.L10n.Tr("ui.preferences.tab_toolbar"), toolbarPage);
        toolbarPage.Initialize(toolbarItems, _toolbarSettings);

        var displayPage = new SLNG.App.UI.DisplayPreferencesPage();
        _preferencesWindow.AddTab(SLNG.App.UI.L10n.Tr("ui.preferences.tab_display"), displayPage);
        displayPage.Initialize(_uiSettings, _localizationManager);
    }

    private void SetupEnvironment()
    {
        // A sky + sun so the 3D world reads as an outdoor scene instead of a grey void,
        // and so meshes get form and cast shadows.
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial() },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 1.0f,
            TonemapMode = Godot.Environment.ToneMapper.Aces,
            
            // Post-FX (M2-5)
            SsaoEnabled = true,
            SsaoRadius = 1.0f,
            SsaoIntensity = 2.0f,
            
            SsilEnabled = true,
            
            GlowEnabled = true,
            GlowNormalized = true,
            GlowIntensity = 1.0f,
            GlowBloom = 0.1f,
            GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Additive,
            
            VolumetricFogEnabled = true,
            VolumetricFogDensity = 0.005f,
        };
        _worldEnvironment = new WorldEnvironment { Name = "WorldEnvironment", Environment = environment };
        AddChild(_worldEnvironment);

        var sun = new DirectionalLight3D
        {
            Name = "DirectionalLight3D",
            RotationDegrees = new Godot.Vector3(-50f, -130f, 0f),
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
            DirectionalShadowBlendSplits = true,
            ShadowBias = 0.02f,
            ShadowNormalBias = 1.0f,
            ShadowOpacity = 0.9f,
        };
        AddChild(sun);
        _sun = sun;
    }

    /// <summary>Standing dev tool (F5): renders the sun's actual direction into the scene as an
    /// emissive beam + sphere anchored at the local avatar. Screen-space reasoning about "which
    /// side should be lit" from a screenshot is unreliable — the light can sit well outside the
    /// frame (e.g. ~50° elevation), and nothing in the sky necessarily marks its position (the
    /// procedural sky's horizon glow doesn't move with the actual DirectionalLight3D). Cross-check
    /// for any future lighting bug: lit surfaces (GREEN under AvatarRenderer's F9 debug material)
    /// must face the sphere, and cast shadows must run exactly opposite the beam. Also a reminder
    /// that the sun direction is hardcoded in SetupEnvironment, unrelated to the region's real
    /// environment (Firestorm drives it from region WindLight/EEP, which SLNG doesn't fetch yet).</summary>
    private void ToggleSunGizmo()
    {
        if (_sunGizmo != null)
        {
            _sunGizmo.QueueFree();
            _sunGizmo = null;
            LogMessage("Sun gizmo OFF");
            return;
        }
        if (_sun == null) return;

        // +Z of the light's basis = direction FROM a lit surface TOWARD the sun (a
        // DirectionalLight3D shines along its local -Z, like all "forward" in Godot).
        var towardLight = _sun.GlobalTransform.Basis.Z.Normalized();

        var anchor = _avatarController?.Position ?? Godot.Vector3.Zero;
        if (_world != null && RenderConfig.TryGetLocalAgentGodotPos(_world, out var agentPos))
            anchor = agentPos;

        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(1f, 0.85f, 0.1f),
        };

        _sunGizmo = new Node3D { Name = "SunGizmo", Position = anchor };
        AddChild(_sunGizmo);

        const float beamLen = 25f;
        var beam = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.06f, BottomRadius = 0.06f, Height = beamLen },
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // CylinderMesh's axis is local +Y — rotate +Y onto the sun direction (shortest arc),
            // then push out by half the length so the beam starts at the avatar.
            Quaternion = new Quaternion(Godot.Vector3.Up, towardLight),
            Position = towardLight * (beamLen / 2f),
        };
        _sunGizmo.AddChild(beam);

        var ball = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 1.2f, Height = 2.4f },
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = towardLight * (beamLen + 1.5f),
        };
        _sunGizmo.AddChild(ball);

        LogMessage($"Sun gizmo ON: beam/sphere point TOWARD the sun, dir={towardLight}");
    }

    public override void _Process(double delta)
    {
        // TEMPORARY diagnostic (2026-07-23, OSGrid movement-judder live-test round): delta is
        // Godot's own measured wall-clock time since the last _Process call -- a large value here
        // means the main thread itself stalled (GC pause, a synchronous decode/build slipping onto
        // this thread, anything blocking _Process from running), not that the network had nothing
        // to send. This directly distinguishes "our client hitched, so queued world events and
        // ExtrapolateMovement sat unprocessed for that long" from "the sim genuinely didn't send us
        // anything for that long" -- the two have identical symptoms in the [AvatarMove] correction
        // log alone. User reports Firestorm looks smooth on the same OSGrid region, which points at
        // a client-side stall rather than a real network/server characteristic. Remove once the
        // cause is confirmed.
        if (delta > 0.2)
        {
            GD.Print($"[FrameHitch] {delta:0.###}s since last _Process frame");
        }

        // Drain queued world events on the main thread — the only place the world mutates.
        _worldSimulation?.Pump();

        // Dead-reckon avatar positions from their last known velocity between network updates
        // (mirrors the real viewer's interpolateLinearMotion) — must run after Pump() so this
        // frame's fresh Position/Velocity/TimeSinceUpdate are already applied before extrapolating.
        _worldSimulation?.ExtrapolateMovement((float)delta);

        // Refresh the position HUD a few times a second (the agent lookup scans entities).
        _hudAccum += delta;
        if (_hudAccum >= 0.2)
        {
            _hudAccum = 0;
            UpdateHud();
        }
    }

    private void UpdateHud()
    {
        if (_world == null || _session == null) { return; }

        string region = string.IsNullOrEmpty(_session.CurrentRegionName) ? "(connecting)" : _session.CurrentRegionName;

        var agent = _world.GetAllEntities()
            .FirstOrDefault(e => e.GetComponent<AvatarComponent>()?.IsLocalAgent == true);
        var t = agent?.GetComponent<TransformComponent>();
        if (t == null)
        {
            _hudLabel.Text = $"{region}\nawaiting position…   ·   Draw {RenderConfig.DrawDistance:0} m";
            return;
        }

        _hudLabel.Text =
            $"{region}\n" +
            $"<{t.Position.X:0.0}, {t.Position.Y:0.0}, {t.Position.Z:0.0}>\n" +
            $"Alt {t.Position.Z:0.0} m   ·   Draw {RenderConfig.DrawDistance:0} m";
    }

    private void LoadWindowSettings()
    {
        int width = (int)_loginsConfig.GetValue("Settings", "width", 0);
        int height = (int)_loginsConfig.GetValue("Settings", "height", 0);
        bool maximized = (bool)_loginsConfig.GetValue("Settings", "maximized", false);

        if (width > 0 && height > 0)
        {
            DisplayServer.WindowSetSize(new Vector2I(width, height));
        }

        if (maximized)
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Maximized);
        }
    }

    private void SaveWindowSettings()
    {
        var mode = DisplayServer.WindowGetMode();
        bool isMaximized = mode == DisplayServer.WindowMode.Maximized;

        _loginsConfig.SetValue("Settings", "maximized", isMaximized);

        if (!isMaximized)
        {
            var size = DisplayServer.WindowGetSize();
            _loginsConfig.SetValue("Settings", "width", size.X);
            _loginsConfig.SetValue("Settings", "height", size.Y);
        }

        _loginsConfig.Save("user://logins.cfg");
    }

    private void OnWindowSizeChanged()
    {
        SaveWindowSettings();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
        {
            SaveWindowSettings();
        }
    }

    private void LoadProfiles()
    {
        _profileDropdown.Clear();
        _savedProfiles.Clear();
        _profileDropdown.AddItem("--- Select Profile ---");
        
        if (_loginsConfig.Load("user://logins.cfg") == Error.Ok)
        {
            var sections = _loginsConfig.GetSections();
            foreach (var profile in sections)
            {
                if (profile == "Settings" || profile == "Window") continue;
                _profileDropdown.AddItem(profile);
                _savedProfiles.Add(profile);
            }

            LoadWindowSettings();

            string lastProfile = (string)_loginsConfig.GetValue("Settings", "last_profile", "");
            if (!string.IsNullOrEmpty(lastProfile))
            {
                int profileIndex = _savedProfiles.IndexOf(lastProfile);
                if (profileIndex >= 0)
                {
                    int dropdownIndex = profileIndex + 1;
                    _profileDropdown.Select(dropdownIndex);
                    OnProfileSelected(dropdownIndex);
                }
            }
        }
    }

    private void OnProfileSelected(long index)
    {
        if (index == 0) return; // The "--- Select Profile ---" placeholder
        
        string profile = _savedProfiles[(int)index - 1];
        _gridInput.Text = (string)_loginsConfig.GetValue(profile, "grid", "");
        _firstInput.Text = (string)_loginsConfig.GetValue(profile, "first", "");
        _lastInput.Text = (string)_loginsConfig.GetValue(profile, "last", "");
        _passInput.Text = (string)_loginsConfig.GetValue(profile, "pass", "");

        _loginsConfig.SetValue("Settings", "last_profile", profile);
        _loginsConfig.Save("user://logins.cfg");
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo)
        {
            if (keyEvent.Keycode == Key.F2)
            {
                _postFxEnabled = !_postFxEnabled;
                if (_worldEnvironment?.Environment != null)
                {
                    _worldEnvironment.Environment.SsaoEnabled = _postFxEnabled;
                    _worldEnvironment.Environment.SsilEnabled = _postFxEnabled;
                    _worldEnvironment.Environment.GlowEnabled = _postFxEnabled;
                    _worldEnvironment.Environment.VolumetricFogEnabled = _postFxEnabled;
                    LogMessage($"Post-FX {(_postFxEnabled ? "enabled" : "disabled")}");
                }
            }
            else if (keyEvent.Keycode == Key.F3)
            {
                RenderConfig.DrawDistance = Mathf.Max(16f, RenderConfig.DrawDistance - 16f);
                LogMessage($"Draw distance: {RenderConfig.DrawDistance:0} m");
            }
            else if (keyEvent.Keycode == Key.F4)
            {
                RenderConfig.DrawDistance = Mathf.Min(512f, RenderConfig.DrawDistance + 16f);
                LogMessage($"Draw distance: {RenderConfig.DrawDistance:0} m");
            }
            else if (keyEvent.Keycode == Key.F5)
            {
                ToggleSunGizmo();
            }
            else if (keyEvent.Keycode == Key.I && keyEvent.CtrlPressed)
            {
                // Ctrl+I like the real viewers — plain I would fire while typing in chat.
                _inventoryPanel?.Toggle();
            }
        }
    }

    private async System.Threading.Tasks.Task SimulateLoadingAnimation()
    {
        GetNode<Control>("%LoginScreen").Visible = false;
        var loadingScreen = GetNode<Control>("%LoadingScreen");
        loadingScreen.Visible = true;
        
        var spinnerLabel = GetNode<Label>("%SpinnerLabel");
        var progressLabel = GetNode<Label>("%ProgressLabel");
        var tasksBox = GetNode<VBoxContainer>("%TasksBox");
        
        string[] tasks = {
            "Stelle Grid-Verbindung her...",
            "Lade Welt-Assets herunter...",
            "Initialisiere Physik-Engine...",
            "Synchronisiere Profildaten...",
            "Optimiere visuelle Darstellung..."
        };
        
        // Clear tasksBox and add new labels
        foreach (Node child in tasksBox.GetChildren()) child.QueueFree();
        var taskLabels = new System.Collections.Generic.List<Label>();
        foreach (var t in tasks)
        {
            var lbl = new Label { Text = t + " [-]", Modulate = new Color(0.5f, 0.5f, 0.5f) };
            lbl.AddThemeFontSizeOverride("font_size", 14);
            tasksBox.AddChild(lbl);
            taskLabels.Add(lbl);
        }
        
        int totalMs = 3000;
        int steps = 60;
        int interval = totalMs / steps;
        
        for (int i = 0; i <= steps; i++)
        {
            float progress = (float)i / steps;
            progressLabel.Text = $"{Mathf.FloorToInt(progress * 100)}%";
            
            spinnerLabel.PivotOffset = spinnerLabel.Size / 2;
            spinnerLabel.RotationDegrees += 15;
            
            int currentTaskIndex = Mathf.FloorToInt(progress * tasks.Length);
            currentTaskIndex = Mathf.Min(currentTaskIndex, tasks.Length - 1);
            
            for (int j = 0; j < taskLabels.Count; j++)
            {
                if (j < currentTaskIndex)
                {
                    taskLabels[j].Text = tasks[j] + " [✓]";
                    taskLabels[j].Modulate = new Color(0.4f, 1f, 0.4f);
                }
                else if (j == currentTaskIndex)
                {
                    taskLabels[j].Text = tasks[j] + " [...]";
                    taskLabels[j].Modulate = new Color(0.4f, 0.8f, 1f);
                }
                else
                {
                    taskLabels[j].Text = tasks[j] + " [-]";
                    taskLabels[j].Modulate = new Color(0.5f, 0.5f, 0.5f);
                }
            }
            
            await ToSignal(GetTree().CreateTimer(interval / 1000f), SceneTreeTimer.SignalName.Timeout);
        }
    }

    private async void OnLoginPressed()
    {
        _loginButton.Disabled = true;
        LogMessage($"Connecting to {_gridInput.Text} as {_firstInput.Text} {_lastInput.Text}...");

        if (_session != null)
        {
            _session.Dispose();
        }
        if (_worldSimulation != null) _worldSimulation.Dispose();
        // Relogging discards the whole cached GPU working set (new session, new region) -- dispose
        // explicitly rather than dropping the reference, same reasoning as DisposeAll's own doc
        // comment: leaving cleanup to the .NET GC risks a finalizer touching RenderingServer late.
        _gpuCache?.DisposeAll();

        // Any ObjectEditWindows still open are bound to the session/world we're about to replace
        // (Initialize() is called once at creation, not re-bindable) -- free them rather than
        // leave them holding references to a disposed GridSession.
        foreach (var win in _objectEditWindows.Values) win.QueueFree();
        _objectEditWindows.Clear();

        _world = new SLNG.Core.ECS.World();
        _session = new GridSession();
        _worldSimulation = new SLNG.Core.WorldSimulation(_world, _session);

        // Server-side deselect (M5-2 acceptance criterion): fires on any client-side
        // deselect path -- clicking empty space, selecting a different object, or
        // closing the edit window -- so ObjectSelectionController and ObjectEditWindow
        // don't each need to remember to notify the sim.
        _world.EntityDeselected += (s, e) => _session?.DeselectObject(e.Entity.LocalId);

        string cacheDir = ProjectSettings.GlobalizePath("user://cache/assets");
        _assetService = new SLNG.Assets.AssetService(_session, cacheDir);

        // GPU budget shared by meshes and textures. Sized for the nearby working set on a
        // 12 GB card with headroom for post-FX; out-of-range content is released so the LRU
        // can reclaim under this cap.
        _gpuCache = new GpuCache(1536L * 1024 * 1024);

        _terrainRenderer?.Initialize(_world, _assetService, _gpuCache);
        _objectRenderer?.Initialize(_world, _assetService, _gpuCache);
        _avatarRenderer?.Initialize(_world, _assetService, _gpuCache, _session);
        _inventoryPanel?.Initialize(_session);
        _chatWindow.BindSession(_session);

        _session.ChatMessageReceived += OnChatMessage;
        _session.InstantMessageReceived += OnInstantMessageReceived;
        // Surfaces sim-side rejections that otherwise fail silently, e.g. "Object physics
        // cancelled because it exceeds limits for physical prims" when a Physical toggle is denied.
        _session.AlertMessageReceived += (s, e) => CallDeferred(MethodName.LogMessage, $"[color=orange][Alert] {e.Message}[/color]");
        // Recenter the floating origin every time we actually move to a new region -- login AND
        // every subsequent teleport/region-crossing (GridSession.RegionConnected only fires for the
        // primary sim, not neighbor sims connected near a border). Previously this was a single
        // RenderConfig.SetRegionOrigin call made once right after login below; nothing ever
        // recentered it again, so after teleporting to any other region OriginX/Y still reflected
        // the LOGIN region's global coordinates -- ToGodot() then computed raw (often
        // multi-thousand-metre) differences between the new region's global coords and the stale
        // origin, blowing float32 precision exactly the way this whole mechanism exists to avoid.
        // Reported symptom: judder/instability on any region other than the one first logged into,
        // clearing up again on returning to it. Subscribed BEFORE LoginAsync below so the initial
        // login's own connection is also caught by this, not just later teleports.
        _session.RegionConnected += (s, regionHandle) =>
            Godot.Callable.From(() => RenderConfig.SetRegionOrigin(regionHandle)).CallDeferred();

        var creds = new LoginCredentials
        {
            GridLoginUri = _gridInput.Text,
            FirstName = _firstInput.Text,
            LastName = _lastInput.Text,
            Password = _passInput.Text
        };

        var animTask = SimulateLoadingAnimation();

        var result = await _session.LoginAsync(creds);

        await animTask;

        if (result.Success)
        {
            string profileName = $"{creds.FirstName} {creds.LastName} @ {creds.GridLoginUri}";
            if (_saveLoginCheck.ButtonPressed)
            {
                _loginsConfig.SetValue(profileName, "grid", creds.GridLoginUri);
                _loginsConfig.SetValue(profileName, "first", creds.FirstName);
                _loginsConfig.SetValue(profileName, "last", creds.LastName);
                _loginsConfig.SetValue(profileName, "pass", creds.Password);
            }
            _loginsConfig.SetValue("Settings", "last_profile", profileName);
            _loginsConfig.Save("user://logins.cfg");

            LogMessage($"[color=green]Login SUCCESS[/color] - AgentID: {result.AgentId}");
            if (!string.IsNullOrEmpty(result.Message))
            {
                LogMessage(result.Message);
            }
            
            // Hide the loading screen, background, and show the top menu
            GetNode<Control>("%LoadingScreen").Visible = false;
            GetNode<Control>("%Background").Visible = false;
            _topMenu.Visible = true;
            
            var hudLayer = GetNodeOrNull<CanvasLayer>("HudLayer");
            if (hudLayer != null) hudLayer.Visible = true;
            _chatWindow.Visible = true;

            _avatarController = new AvatarController();
            _avatarController.Name = "AvatarController";
            AddChild(_avatarController);
            if (_avatarRenderer != null)
                _avatarController.Initialize(_world, _session, _avatarRenderer);

            _objectSelectionController = new ObjectSelectionController();
            AddChild(_objectSelectionController);
            _objectSelectionController.Initialize(_world, _session, _avatarController, _inWorldContextMenu);

            _cursorManager = new SLNG.App.CursorManager();
            AddChild(_cursorManager);
            _cursorManager.Initialize(_world, _avatarController);

            ulong regionHandle = _session.CurrentRegionHandle;
            
            // Start near the region centre at a reasonable height (before AvatarUpdate arrives).
            _avatarController.Position = RenderConfig.ToGodot(regionHandle, new System.Numerics.Vector3(128f, 128f, 50f));
            
            // Assign the environment directly to the camera to ensure the sky renders
            var worldEnv = GetNodeOrNull<WorldEnvironment>("WorldEnvironment");
            if (worldEnv != null)
            {
                _avatarController.Environment = worldEnv.Environment;
            }

            _avatarController.MakeCurrent();

            LogMessage($"[System] Login succeeded! Agent: {result.AgentId}");
        }
        else
        {
            LogMessage($"[System] Login failed: {result.Message}");
            GetNode<Control>("%LoadingScreen").Visible = false;
            GetNode<Control>("%LoginScreen").Visible = true;
            _loginButton.Disabled = false;
        }
    }

    private void OnChatMessage(object? sender, ChatMessageEvent e)
    {
        // ChatMessageReceived fires on a LibreMetaverse network thread -- marshal to the main
        // thread before touching ChatWindow's Control tree.
        CallDeferred(nameof(AppendChatMessage), e.FromName, e.Message);
    }

    private void AppendChatMessage(string fromName, string message)
    {
        _chatWindow.AppendLocalChatMessage(fromName, message);
    }

    private void OnInstantMessageReceived(object? sender, InstantMessageEvent e)
    {
        // Same marshalling reason as OnChatMessage -- fires on a LibreMetaverse network thread.
        // Guid isn't a Variant-safe CallDeferred argument, so it travels as a string.
        CallDeferred(nameof(AppendInstantMessage), e.FromAgentId.ToString(), e.FromAgentName, e.Message);
    }

    private void AppendInstantMessage(string fromAgentId, string fromAgentName, string message)
    {
        _chatWindow.AppendIncomingInstantMessage(System.Guid.Parse(fromAgentId), fromAgentName, message);
    }

    private int _logLineCount;

    private void LogMessage(string message)
    {
        // Cap the panel — an unbounded RichTextLabel re-layouts everything on every append
        // and tanks the frame rate once it holds thousands of lines.
        if (++_logLineCount > 200)
        {
            _logPanel.Clear();
            _logLineCount = 1;
        }
        _logPanel.AppendText(message + "\n");
    }

    public override void _ExitTree()
    {
        // Dispose the GPU cache's Resources explicitly, before the engine's own shutdown teardown
        // -- see GpuCache.DisposeAll's doc comment for why (a late .NET GC finalizer touching an
        // already-destroyed RenderingServer is the documented cause of the "N RID allocations...
        // leaked at exit" / "RenderingServer::get_singleton() is null" pair seen at close).
        _gpuCache?.DisposeAll();
        _worldSimulation?.Dispose();
        _session?.Dispose();
        
        // Force GC now while the RenderingServer is still alive, so any floating Godot wrappers
        // (like evicted cache entries) run their finalizers safely.
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
    }
}
