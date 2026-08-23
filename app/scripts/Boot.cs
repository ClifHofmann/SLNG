using Godot;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Net;
using SLNG.App;
using System.Linq;
using System.Collections.Generic;

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

    // FEAT-UI-08: circular progress ring + step checklist on the loading screen, driven by
    // real boot/login stages (see CompleteLoadingStep / OnLoginProgressStage) instead of the
    // fake setInterval-style animation the original mockup used.
    private TextureProgressBar _progressRing = null!;
    private Label _progressPercentLabel = null!;
    private VBoxContainer _stepList = null!;
    private Label[] _stepLabels = System.Array.Empty<Label>();
    private int _completedSteps;

    private static readonly string[] LoadingSteps =
    {
        "Initializing session...",
        "Connecting to login server...",
        "Authenticating...",
        "Connecting to region...",
        "Entering world...",
    };

    private ConfigFile _loginsConfig = new ConfigFile();
    private Godot.Collections.Array<string> _savedProfiles = new();

    private GridSession? _session;
    private SLNG.Core.ECS.World? _world;
    private SLNG.Core.Services.LocalizationManager _localizationManager = null!;
    private SLNG.Core.WorldSimulation _worldSimulation = null!;
    private SLNG.App.UI.DialogQueueManager? _dialogQueueManager;
    private GpuCache? _gpuCache;
    private TerrainRenderer? _terrainRenderer;
    private ObjectRenderer? _objectRenderer;
    private AvatarRenderer? _avatarRenderer;
    private SLNG.Assets.AssetService? _assetService;
    private AvatarController? _avatarController;
    private VBoxContainer _vboxContainer = null!;
    
    private WorldEnvironment? _worldEnvironment;
    private DirectionalLight3D? _sun;
    // FEAT-ENV-01 Phase D: drives sun/ambient/sky/fog/water from the region's actual environment.
    // Always constructed (not nullable) -- with no region connected yet it just evaluates
    // DayCycle.Default every frame, which is the same hardcoded-looking scene as before this
    // feature, not a special case to guard against.
    private readonly EnvironmentDriver _environmentDriver = new();
    private SLNG.App.UI.InventoryPanel? _inventoryPanel;
    private Node3D? _sunGizmo;

    private Label _hudLabel = null!;
    private double _hudAccum;
    
    private SLNG.App.UI.TopMenu _topMenu = null!;
    // Created lazily on first use -- see the Developer menu wiring below. Dev tooling only,
    // costs nothing until someone actually takes a measurement.
    private RenderBaselineSampler? _renderBaselineSampler;
    private SLNG.App.UI.StatsOverlay? _statsOverlay;
    private SLNG.App.UI.GraphicsSettings _graphicsSettings = new();
    private SLNG.App.UI.GraphicsPreferencesPage? _graphicsPage;

    /// <summary>Threshold for the [AgentGap] log. Below the 0.8 s extrapolation cutoff, so a gap
    /// shows up in the log slightly before it becomes visible as a stalled avatar.</summary>
    private const float AgentGapWarnSeconds = 0.5f;
    private bool _agentGapReported;

    /// <summary>Peak of the current gap, so the log can report how long it ACTUALLY lasted rather
    /// than the 0.5 s at which it was first noticed -- the first version reported the crossing value
    /// and made every gap look like exactly 0.5 s.</summary>
    private float _agentGapPeak;

    private readonly MainThreadWatchdog _watchdog = new();
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

    public const string AppVersion = "v0.8.2-alpha";

    // Reads res://i18n/*.json via Godot's DirAccess/FileAccess instead of System.IO +
    // ProjectSettings.GlobalizePath -- the latter only resolves to a real on-disk directory
    // when running from source. An exported build packs the JSON files into the .pck, where
    // GlobalizePath's result doesn't exist as a real file and LocalizationManager's
    // System.IO-based loader silently found nothing, leaving every UI string showing its raw
    // "[key]" fallback (confirmed live on an installed v0.3.2 build). Same failure class as
    // the avatar skeleton XML fix in AvatarRenderer.cs -- FileAccess reads both loose files
    // and packed .pck contents uniformly, so it works from source and from an export alike.
    private static SLNG.Core.Services.LocalizationManager LoadLocalizationManager()
    {
        var manager = new SLNG.Core.Services.LocalizationManager();
        using var dir = DirAccess.Open("res://i18n");
        if (dir == null)
        {
            GD.PrintErr($"[Boot] Failed to open res://i18n: {DirAccess.GetOpenError()}");
            return manager;
        }

        dir.ListDirBegin();
        for (string fileName = dir.GetNext(); fileName != ""; fileName = dir.GetNext())
        {
            if (dir.CurrentIsDir() || !fileName.EndsWith(".json")) continue;

            string localeName = fileName.Substring(0, fileName.Length - ".json".Length);
            using var file = FileAccess.Open($"res://i18n/{fileName}", FileAccess.ModeFlags.Read);
            if (file == null)
            {
                GD.PrintErr($"[Boot] Failed to open res://i18n/{fileName}: {FileAccess.GetOpenError()}");
                continue;
            }
            manager.LoadLocaleFromJson(localeName, file.GetAsText());
        }
        dir.ListDirEnd();

        return manager;
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;

        _localizationManager = LoadLocalizationManager();
        SLNG.App.UI.L10n.Initialize(_localizationManager);

        // Godot debug builds hard-code an "(DEBUG)" window-title suffix that gets applied
        // AFTER _Ready() runs, silently overwriting whatever title we set here a moment later
        // — a known engine behavior (godotengine/godot#104321), not an SLNG bug. Re-asserting
        // the title once more after the next frame renders lands after that internal logic and
        // sticks; the immediate call below just avoids a flash of the wrong title before then.
        // Before anything that logs, so the level is already right for the first line.
        Diagnostics.Initialize();

        // Before the first asset fetch: SLNG.Net/SLNG.Assets log through Console, which does not
        // reach godot.log on its own. See ConsoleToGodotLog.
        ConsoleToGodotLog.Install();

        DisplayServer.WindowSetTitle($"Puris Viewer {AppVersion}");
        RenderingServer.FramePostDraw += ReassertWindowTitleOnce;

        // Log the version at startup. Renderer BUILD MARKERs only move when that renderer
        // changes, so several rounds were spent unable to tell from a log which build had
        // actually run.
        GD.Print($"[Boot] {AppVersion}");

        _vboxContainer = GetNode<VBoxContainer>("%VBoxContainer");
        _profileDropdown = GetNode<OptionButton>("%ProfileDropdown");
        _gridDropdown = GetNode<OptionButton>("%GridDropdown");
        _gridInput = GetNode<LineEdit>("%GridInput");
        _firstInput = GetNode<LineEdit>("%FirstInput");
        _lastInput = GetNode<LineEdit>("%LastInput");

        // Populate Grid Dropdown
        _gridDropdown.AddItem("OSGrid");
        _gridDropdown.SetItemMetadata(0, "http://hg.osgrid.org/");
        _gridDropdown.AddItem("Localhost");
        _gridDropdown.SetItemMetadata(1, "http://127.0.0.1:9000/");
        
        _gridDropdown.ItemSelected += (index) => 
        {
            _gridInput.Text = (string)_gridDropdown.GetItemMetadata((int)index);
        };
        _passInput = GetNode<LineEdit>("%PassInput");
        _saveLoginCheck = GetNode<CheckBox>("%SaveLoginCheck");
        _loginButton = GetNode<Button>("%LoginButton");
        _logPanel = GetNode<RichTextLabel>("%LogPanel");

        _progressRing = GetNode<TextureProgressBar>("%ProgressRing");
        _progressPercentLabel = GetNode<Label>("%ProgressPercentLabel");
        _stepList = GetNode<VBoxContainer>("%StepList");
        BuildLoadingSteps();
        _ = SetupThemedIconsAsync();

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

        _topMenu.OnToggleStats = () => _statsOverlay?.Toggle();

        _topMenu.OnToggleWireframe = () => {
            var vp = GetViewport();
            vp.DebugDraw = vp.DebugDraw == Viewport.DebugDrawEnum.Wireframe
                ? Viewport.DebugDrawEnum.Disabled
                : Viewport.DebugDrawEnum.Wireframe;
        };

        // FEAT-RENDER-01: capture a comparable before/after render measurement -- see
        // RenderBaselineSampler for why it is manual and stationary. The label records the
        // build so two log lines can never be mixed up when comparing runs.
        _topMenu.OnMeasureRenderBaseline = () => {
            _renderBaselineSampler ??= new RenderBaselineSampler { Name = "RenderBaselineSampler" };
            if (_renderBaselineSampler.GetParent() == null) AddChild(_renderBaselineSampler);
            _renderBaselineSampler.StartSample(AppVersion);
        };

        _topMenu.OnNudgeSculptV = (step) => {
            _objectRenderer?.NudgeSculptV(step);
            LogSculptNudge();
        };

        _topMenu.OnNudgeSculptU = (step) => {
            _objectRenderer?.NudgeSculptU(step);
            LogSculptNudge();
        };

        _topMenu.OnOpenPreferences = () => {
            // Re-read on open: F2 and F3/F4 change these settings from outside the dialog, so
            // controls built once at startup would otherwise show stale values.
            _graphicsPage?.Refresh();
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

        // Same reason, one bug later: the Graphics tab builds its checkboxes and dropdowns from
        // whatever the settings object holds AT CONSTRUCTION. Loading afterwards left the world
        // correctly following the saved value while the controls still showed the defaults --
        // shadows genuinely off after login, with the checkbox ticked.
        _graphicsSettings.Load();
        
        // Apply saved language setting
        _localizationManager.CurrentLocale = _uiSettings.Language;

        // Position/altitude readout in the top-right corner, overlaying the 3D view.
        // On its own CanvasLayer so it always draws on top of the world and the login/chat
        // Controls, regardless of scene-tree order.
        // Drains the budgeted main-thread work queue every frame (FEAT-PERF-01). Added before the
        // renderer and HUD exist so any work enqueued during startup is already being drained.
        AddChild(new MainThreadWorkPump());

        // Off-thread stall detector (FEAT-PERF-01). Started here rather than in _Ready so it covers
        // the world-loading phase, which is when the client is reported to freeze. A release does not
        // carry the extra thread.
        if (Diagnostics.Enabled) _watchdog.Start();

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

        // Performance readout (FEAT-PERF-01). Lives on HudLayer so "Toggle HUD" hides it along
        // with the rest of the overlay, but it starts hidden and is opened on demand.
        _statsOverlay = new SLNG.App.UI.StatsOverlay();
        hudLayer.AddChild(_statsOverlay);

        _inventoryPanel = new SLNG.App.UI.InventoryPanel { Name = "InventoryPanel" };
        hudLayer.AddChild(_inventoryPanel);

        _inWorldContextMenu = new SLNG.App.UI.InWorldContextMenu();
        hudLayer.AddChild(_inWorldContextMenu);

        _inWorldContextMenu.OnEditClicked = (entity, localId) => OpenObjectEditWindow(hudLayer, entity, localId);
        _inWorldContextMenu.OnTouchClicked = (entity, localId) => { /* Touch logic later */ };
        _inWorldContextMenu.OnInspectClicked = (entity, localId) => { /* Inspect logic later */ };
        _inWorldContextMenu.OnDeleteClicked = (entity, localId) => { /* Delete logic later */ };
        _inWorldContextMenu.OnSitClicked = (entity, localId) => _session?.RequestSit(localId);
        _inWorldContextMenu.OnSitOnGroundClicked = (godotPos) => _session?.SitOnGround();
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

        // Applied here rather than at Load time above, because SetupEnvironment has run by now and
        // the sun and environment exist to receive it. The window-level settings (V-Sync, frame cap)
        // are applied by the same call and must hold even if Preferences is never opened.
        ApplyGraphicsSettings();
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

        _graphicsPage = new SLNG.App.UI.GraphicsPreferencesPage();
        _preferencesWindow.AddTab(SLNG.App.UI.L10n.Tr("ui.preferences.tab_graphics"), _graphicsPage);
        _graphicsPage.Initialize(_graphicsSettings, ApplyGraphicsSettings);

        var networkPage = new SLNG.App.UI.NetworkPreferencesPage();
        _preferencesWindow.AddTab(SLNG.App.UI.L10n.Tr("ui.preferences.tab_network"), networkPage);
        networkPage.Initialize(ProjectSettings.GlobalizePath("user://cache/assets"));

        // Not optional: the Second Life viewer artwork we ship is CC BY-SA 3.0, which requires the
        // attribution notice to reach the user. See app/THIRD-PARTY-NOTICES.md.
        var licensesPage = new SLNG.App.UI.LicensesPreferencesPage();
        _preferencesWindow.AddTab(SLNG.App.UI.L10n.Tr("ui.preferences.tab_licenses"), licensesPage);
        licensesPage.Initialize();
    }

    private void SetupEnvironment()
    {
        // A sky + sun so the 3D world reads as an outdoor scene instead of a grey void,
        // and so meshes get form and cast shadows.
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://materials/sky.gdshader") } },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 1.0f,
            VolumetricFogEnabled = false,

            // NO tonemapping, because the viewer does none for the skies OpenSim actually sends.
            //
            // Firestorm decides this per sky. `classic_mode = psky->canAutoAdjust() &&
            // !RenderSkyAutoAdjustLegacy` (llsettingsvo.cpp:813), where
            // `mCanAutoAdjust = !settings.has("reflection_probe_ambiance")`
            // (llsettingssky.cpp:1171) and RenderSkyAutoAdjustLegacy ships as 0 — its own settings
            // comment calls it "the opt-out button for HDR and tonemapping when coupled with a sky
            // setting that predates PBR". For such a sky getTonemapMix() returns 0.0 with the
            // comment "legacy settings do not support tonemaping" (llsettingssky.cpp:2062), and the
            // final pass is nothing but linear_to_srgb plus a clamp
            // (postDeferredGammaCorrect.glsl:47-56).
            //
            // Every legacy Windlight sky, and every EEP sky converted from one, lacks
            // reflection_probe_ambiance — the PARITY-00 capture from Howletts has no such key — so
            // classic mode is what we are actually being compared against. Running ACES on top of
            // the ported atmospherics desaturated and lifted the sky into a near-white wash: at 25
            // degrees elevation, Firestorm (0.48, 0.63, 1.00) against our (0.85, 0.90, 0.97).
            //
            // Godot's Linear mapper is `color / white` with white at 1.0, i.e. the identity, so the
            // frame reaches the screen through linear_to_srgb and a clamp exactly as the viewer's
            // does. TODO: once EnvironmentLlsdParser reads reflection_probe_ambiance, switch back
            // to Aces for skies that carry it, which is the branch this mirrors.
            TonemapMode = Godot.Environment.ToneMapper.Linear,
            
            // Post-FX (M2-5)
            SsaoEnabled = true,
            SsaoRadius = 1.0f,
            SsaoIntensity = 2.0f,
            
            SsilEnabled = true,
            
            // Glow is deliberately restrained, because the sun's atmospheric halo is ALREADY
            // rendered in sky.gdshader -- that is what the haze_glow term is, ported from SL's own
            // atmospherics. Post-process bloom on top of it double-counts the same effect, and
            // GlowBloom in particular was the expensive half: any value above 0 makes glow apply
            // BELOW the HDR threshold, i.e. across the entire bright sky rather than just the sun
            // disc. Additive at full intensity then pushed that into a white blob wide enough to
            // swallow the clouds next to the sun (reported against Firestorm, whose haze stays
            // tight because it is shader-side only). HdrThreshold set explicitly rather than left
            // at Godot's default so only genuinely bright highlights bloom, not the merely bright
            // sky behind them. All four are calibration knobs, not ported values.
            GlowEnabled = true,
            GlowNormalized = true,
            GlowIntensity = 0.4f,
            GlowBloom = 0.0f,
            GlowHdrThreshold = 1.2f,
            GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Additive
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

    /// <summary>Aims the directional light along the region's real sun direction.
    ///
    /// Until now the sun sat at a hardcoded (-50, -130, 0) that had nothing to do with the region
    /// or its time of day, while the real viewer uses the sim's sun. That is not a subtle
    /// difference: on a rotationally symmetric object the highlight simply lands on the other
    /// side, which reads exactly like a mirrored texture and sent this session's pillar
    /// investigation through five dead ends (rotation, offset, sculpt invert/mirror, base UV
    /// direction, winding) before the lighting itself became the suspect.
    ///
    /// This is not Windlight — sky colour, atmospherics and EEP are still Phase 5. It only fixes
    /// WHERE the light comes from, which is the part that changes what you see on a surface.</summary>
    private System.Numerics.Vector3 GetSunDirection()
    {
        if (_session == null) return default;
        var d = _session.SunDirection;
        if (d != System.Numerics.Vector3.Zero) return d;
        return new System.Numerics.Vector3(
            (float)System.Math.Cos(_session.SunPhase),
            0f,
            (float)System.Math.Sin(_session.SunPhase)
        );
    }

    private void UpdateSunFromRegion(System.Numerics.Vector3 d)
    {
        if (_sun == null) return;

        if (float.IsNaN(d.X) || float.IsNaN(d.Y) || float.IsNaN(d.Z) || d.LengthSquared() < 0.0001f) return;

        // SL is Z-up, Godot is Y-up: the same (x, z, -y) mapping the mesh path uses. SunDirection
        // points toward the sun, so the light travels the other way and the light node's forward
        // (-Z, which is what LookAt aims) is the negated vector.
        var toSun = new Godot.Vector3(d.X, d.Z, -d.Y);
        
        if (toSun.LengthSquared() < 0.0001f)
            return;
            
        toSun = toSun.Normalized();

        // Straight down would make LookAt's up-vector degenerate; skip that one frame rather than
        // emit a NaN basis.
        if (Mathf.Abs(toSun.Dot(Godot.Vector3.Up)) > 0.9999f) return;

        _sun.LookAt(_sun.GlobalPosition - toSun, Godot.Vector3.Up);
    }

    /// <summary>Writes the region's raw Windlight/EEP environment to <c>user://logs/</c> and
    /// summarises it in the chat log (FEAT-ENV-01 Phase A).
    ///
    /// This changes nothing on screen. It exists because the fallback chain
    /// (EEP -> legacy Windlight -> viewer default) cannot be designed against a guess: OpenSim's
    /// EEP support varies by version and need not match SL's. One login with this in place
    /// produces the fixture the parser is written and tested against, which is a great deal
    /// cheaper than discovering the shape of the data from a rendering bug later.</summary>
    private void DumpRegionEnvironment(SLNG.Core.RegionEnvironmentCapture capture)
    {
        string caps = capture switch
        {
            { HasExtEnvironmentCap: true, HasEnvironmentSettingsCap: true } => "ExtEnvironment + EnvironmentSettings",
            { HasExtEnvironmentCap: true } => "ExtEnvironment (EEP only)",
            { HasEnvironmentSettingsCap: true } => "EnvironmentSettings (legacy Windlight only)",
            _ => "NONE",
        };

        LogEnvironment($"[ENV] '{capture.RegionName}' caps: {caps}"
            + $", dayLength={capture.DayLength}s, dayOffset={capture.DayOffset}s, isDefault={capture.IsDefault}"
            + (capture.Error != null ? $", ERROR: {capture.Error}" : string.Empty));

        // Which SCOPE answered matters as much as the values. The environment is per-parcel, so a
        // parcel that overrides the region produces a completely different sky at the same
        // wall-clock time -- that is what "die Sonne ist auf der anderen Seite" turned out to be.
        // Logging both scopes side by side is what lets one screenshot settle which one applies.
        LogEnvironment(capture switch
        {
            { ParcelId: < 0 } => "[ENV] parcel: id unknown (no ParcelProperties reply) — using the region scope",
            { ParcelEnvironmentLlsd: null } p => $"[ENV] parcel {p.ParcelId}: inherits the region environment",
            var p => $"[ENV] parcel {p.ParcelId}: OWN environment, dayLength={p.ParcelDayLength}s, "
                   + $"dayOffset={p.ParcelDayOffset}s — this overrides the region",
        });

        if (capture.ExtEnvironmentLlsd == null && capture.LegacyEnvironmentLlsd == null
            && capture.ParcelEnvironmentLlsd == null)
        {
            // Not necessarily a fault: a region with no custom environment inherits the grid
            // default and legitimately returns nothing. The cap flags above say which case it is.
            LogEnvironment("[ENV] no environment settings returned — region inherits the grid default");
            return;
        }

        DirAccess.MakeDirRecursiveAbsolute("user://logs");

        // Region names carry spaces and punctuation that are fine in a name and not in a filename.
        var safeName = new string(capture.RegionName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        if (safeName.Length == 0) safeName = capture.RegionHandle.ToString();

        WriteEnvironmentDump($"user://logs/environment-{safeName}-eep.llsd", capture.ExtEnvironmentLlsd);
        WriteEnvironmentDump($"user://logs/environment-{safeName}-legacy.llsd", capture.LegacyEnvironmentLlsd);
        // Kept under its own name rather than overwriting the region dump: comparing the two curves
        // is the whole point when a parcel disagrees with its region.
        WriteEnvironmentDump($"user://logs/environment-{safeName}-parcel{capture.ParcelId}.llsd", capture.ParcelEnvironmentLlsd);
    }

    /// <summary>Logs an environment diagnostic to BOTH the on-screen panel and stdout.
    ///
    /// <see cref="LogMessage"/> alone is the wrong sink for this: the panel is capped at 200 lines
    /// and cleared when it overflows, so on a busy region the environment readout is gone long
    /// before anyone looks for it, and it never reaches <c>godot.log</c> where a post-hoc analysis
    /// would find it. That is exactly what happened on the first live capture — the LLSD dump
    /// survived, the summary saying which capabilities answered did not.</summary>
    private void LogEnvironment(string message)
    {
        // Identical repeats are dropped. The environment is re-applied many times per session and
        // logs the same six-line block every time -- measured at 13,908 of ~16,000 lines in one
        // capture, i.e. the readout this method exists to preserve was burying everything else in
        // godot.log, including the failure lines an investigation actually needs.
        //
        // Exact-match only, deliberately: a line whose numbers CHANGED (a real sky transition, a
        // different parcel taking over, a dump of a different size) still prints, because it is no
        // longer the same string. So this loses repetition, never information. Capped so a message
        // that varies every time -- a timestamp, say -- cannot grow the set without bound.
        if (_environmentLinesLogged.Count < 512 && !_environmentLinesLogged.Add(message)) return;

        LogMessage(message);
        GD.Print(message);
    }

    private readonly HashSet<string> _environmentLinesLogged = new();

    private void WriteEnvironmentDump(string path, string? llsd)
    {
        if (llsd == null) return;

        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (file == null)
        {
            LogEnvironment($"[color=orange][ENV] could not write {path}: {FileAccess.GetOpenError()}[/color]");
            return;
        }
        file.StoreString(llsd);
        LogEnvironment($"[ENV] wrote {ProjectSettings.GlobalizePath(path)} ({llsd.Length} chars)");
    }

    /// <summary>Reports the parsed environment (FEAT-ENV-01 Phase B). Still nothing on screen —
    /// this is the readout that says whether the parse produced a real sky or quietly fell back to
    /// the viewer default, which is the one failure mode a screenshot could never distinguish.</summary>
    private void LogRegionEnvironment(SLNG.Core.RegionEnvironmentEvent env)
    {
        var cycle = env.Cycle;
        var sky = cycle.EvaluateSky(System.DateTimeOffset.UtcNow);

        LogEnvironment($"[ENV] source={env.Source}, skyFrames={cycle.SkyFrames.Count}"
            + $", waterFrames={cycle.WaterFrames.Count}, dayLength={cycle.DayLengthSeconds}s"
            + $", position={cycle.PositionAt(System.DateTimeOffset.UtcNow):F3}");
        LogEnvironment($"[ENV] sky now: blueHorizon={sky.BlueHorizon}, hazeDensity={sky.HazeDensity:F3}"
            + $", cloudShadow={sky.CloudShadow:F3}");
    }

    public override void _Process(double delta)
    {
        // FEAT-ENV-02: Use the server's synced time if we have received a SimulatorViewerTimeMessage, 
        // otherwise fall back to local UtcNow.
        var simTime = _session?.SimUnixTime > 0 
            ? System.DateTimeOffset.FromUnixTimeSeconds((long)(_session.SimUnixTime / 1000000UL))
            : System.DateTimeOffset.UtcNow;
            
        var sunDir = GetSunDirection();
        _environmentDriver.Update(
            _worldEnvironment, _sun, _terrainRenderer?.WaterMaterial,
            sunDir, simTime, _assetService, _gpuCache,
            // Asset fetches abandon immediately while the session is down (AssetService checks
            // IsConnected before every attempt) and the failure is then cached for 45s, so the
            // driver must not even try before this is true -- see FetchTextureOnce.
            assetsReady: _session?.IsConnected == true);

        // FEAT-ENV-01 Phase D. Reads the SAME SunDirection EnvironmentDriver just aimed the
        // light with, so the sky dome/fog and the actual lit scene never disagree about which way
        // is day.
        // CalculatedLightDirection, not CalculatedSunDirection: after sunset the scene is lit by
        // the moon, and aiming this at the sun sent the light up through the ground.
        UpdateSunFromRegion(_environmentDriver.CalculatedLightDirection);

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
        if (Diagnostics.Enabled)
        {
            _watchdog.Beat();
            if (delta > 0.2) GD.Print($"[FrameHitch] {delta:0.###}s since last _Process frame");
        }

        // Drain queued world events on the main thread — the only place the world mutates.
        using (MainThreadPhase.Enter("world-drain")) _worldSimulation?.Pump();

        // Drain queued llDialog popups (M5-4) on the main thread, same reasoning as
        // WorldSimulation.Pump above — ScriptDialogReceived fires on a LibreMetaverse network thread.
        _dialogQueueManager?.Pump();

        // Dead-reckon avatar positions from their last known velocity between network updates
        // (mirrors the real viewer's interpolateLinearMotion) — must run after Pump() so this
        // frame's fresh Position/Velocity/TimeSinceUpdate are already applied before extrapolating.
        using (MainThreadPhase.Enter("extrapolate")) _worldSimulation?.ExtrapolateMovement((float)delta);
        if (Diagnostics.Enabled) ReportAgentPacketGaps();

        // Refresh the position HUD a few times a second (the agent lookup scans entities).
        _hudAccum += delta;
        if (_hudAccum >= 0.2)
        {
            _hudAccum = 0;
            UpdateHud();
        }
    }

    /// <summary>
    /// Logs how long the sim has left the local agent without a position packet.
    ///
    /// Walking is server-authoritative -- AvatarController only sends SetMovement at 10 Hz and the
    /// position comes back from the sim -- so "the avatar stops for about a second while the client
    /// keeps rendering at 60 fps" cannot be a frame-rate problem, and the numbers agree: zero
    /// [FrameHitch] lines (nothing over 0.2 s) with hitches=0 in the same session. What it looks
    /// like instead is WorldSimulation's deliberate extrapolation cutoff: dead reckoning stops at
    /// ExtrapolationMaxSeconds (0.8 s) and the avatar freezes in place rather than being flung along
    /// a stale heading, which is the better failure mode but is exactly what a stalled walk feels
    /// like. A ~1.4 s gap was already recorded by the 2026-07-23 [AvatarMove] investigation.
    ///
    /// Reports the work-queue depth alongside it, because the obvious suspect for a starved agent
    /// packet is the client's own asset traffic while walking into new territory -- and if the gap
    /// turns out to be independent of local load, that points at the sim or the link instead.
    /// </summary>
    private void ReportAgentPacketGaps()
    {
        if (_world == null) return;

        var t = GetLocalAgentTransform();
        if (t == null) { _agentGapReported = false; return; }

        // Edge-triggered: one line per gap, not one per frame for as long as it lasts.
        if (t.TimeSinceUpdate >= AgentGapWarnSeconds)
        {
            _agentGapReported = true;
            if (t.TimeSinceUpdate > _agentGapPeak) _agentGapPeak = t.TimeSinceUpdate;
        }
        else if (_agentGapReported)
        {
            // Reported when the gap ENDS, so the figure is the gap's real length. Reporting at the
            // moment it crossed the threshold made every gap read as 0.50-0.54 s regardless of how
            // long it went on -- which mattered, because whether it exceeded the 0.80 s
            // extrapolation cutoff is the difference between a smooth walk and a visible stall.
            GD.Print($"[AgentGap] no position packet for {_agentGapPeak:0.00}s " +
                      $"({(_agentGapPeak > 0.8f ? "OVER" : "within")} the 0.80s extrapolation cutoff) " +
                      $"queue={MainThreadWorkQueue.Depth}");
            _agentGapReported = false;
            _agentGapPeak = 0;
        }
    }

    /// <summary>Pushes the saved graphics options into the live scene. Passed to
    /// GraphicsPreferencesPage as a callback so the page never has to reach for the viewport, the
    /// environment or the sun itself -- it only knows the settings object.</summary>
    private void ApplyGraphicsSettings()
        => _graphicsSettings.Apply(GetViewport(), _worldEnvironment, _sun);

    /// <summary>
    /// The local agent's transform, with the entity cached.
    ///
    /// Finding it means scanning every entity for the one whose AvatarComponent is the local agent,
    /// and this runs once per frame -- on a 24,000-entity region that is the same class of cost that
    /// [PhaseCost] caught in ExtrapolateMovement. The agent entity is stable for the whole session,
    /// so it is looked up once and re-resolved only if it ever goes away (a disconnect replaces the
    /// world).
    /// </summary>
    private TransformComponent? GetLocalAgentTransform()
    {
        if (_world == null) return null;

        if (_localAgent == null || _world.GetEntity(_localAgent.Id) == null)
        {
            _localAgent = _world.GetAllEntities()
                .FirstOrDefault(e => e.GetComponent<AvatarComponent>()?.IsLocalAgent == true);
        }

        return _localAgent?.GetComponent<TransformComponent>();
    }

    private SLNG.Core.ECS.Entity? _localAgent;

    private void UpdateHud()
    {
        if (_world == null || _session == null) { return; }

        string region = string.IsNullOrEmpty(_session.CurrentRegionName) ? "(connecting)" : _session.CurrentRegionName;

        var t = GetLocalAgentTransform();
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
                // Routed through GraphicsSettings rather than toggling the environment directly, so
                // the shortcut and the Graphics tab's checkbox can never end up disagreeing about
                // the same four flags -- and so the state survives a restart like every other
                // graphics option does.
                _graphicsSettings.SetPostFx(!_graphicsSettings.PostFx);
                ApplyGraphicsSettings();
                LogMessage($"Post-FX {(_graphicsSettings.PostFx ? "enabled" : "disabled")}");
            }
            else if (keyEvent.Keycode == Key.F3)
            {
                // Through GraphicsSettings for the same reason as F2 above: the Graphics tab's
                // slider reads from it, and a key that wrote RenderConfig directly would leave the
                // slider showing a stale number and overwrite the change on the next apply.
                _graphicsSettings.SetDrawDistance(Mathf.Max(32f, _graphicsSettings.DrawDistance - 16f));
                ApplyGraphicsSettings();
                LogMessage($"Draw distance: {RenderConfig.DrawDistance:0} m");
            }
            else if (keyEvent.Keycode == Key.F4)
            {
                _graphicsSettings.SetDrawDistance(Mathf.Min(512f, _graphicsSettings.DrawDistance + 16f));
                ApplyGraphicsSettings();
                LogMessage($"Draw distance: {RenderConfig.DrawDistance:0} m");
            }
            else if (keyEvent.Keycode == Key.F5)
            {
                ToggleSunGizmo();
            }
            else if (keyEvent.Keycode == Key.F6)
            {
                // "What is around me, and is it being drawn?" -- the one question the click
                // diagnostics cannot answer, because clicking needs the object to be rendered and
                // the objects worth asking about are the ones that are NOT. An object missing from
                // the render and missing from the log is indistinguishable from an object the sim
                // never sent, and those need completely different fixes.
                _objectRenderer?.LogNearbyObjects(32f);
            }
            else if (keyEvent.Keycode == Key.Key1 && keyEvent.CtrlPressed && keyEvent.ShiftPressed)
            {
                // Ctrl+Shift+1 is the statistics shortcut in SL/Firestorm, so muscle memory carries
                // over. F-keys are already taken here by post-FX and the draw-distance nudges.
                _statsOverlay?.Toggle();
            }
            else if (keyEvent.Keycode == Key.I && keyEvent.CtrlPressed)
            {
                // Ctrl+I like the real viewers — plain I would fire while typing in chat.
                _inventoryPanel?.Toggle();
            }
        }
    }

    /// <summary>Rasterizes the login screen's circular progress ring and the "Save Login"
    /// checkbox's checked/unchecked glyphs from the same Material Symbols icon font already used
    /// by ButtonBar/CameraHUD/CursorManager elsewhere in the app, instead of hand-authoring new
    /// image assets (FEAT-UI-08) -- same off-screen-SubViewport-then-GetImage technique as
    /// CursorManager.SetupMagnifierCursorAsync, needed because TextureProgressBar/CheckBox icon
    /// slots take an actual Texture2D, not a font glyph directly.</summary>
    private async System.Threading.Tasks.Task SetupThemedIconsAsync()
    {
        var iconFont = GD.Load<Font>("res://assets/fonts/MaterialSymbolsOutlined.ttf");

        // One white ring, reused for both the ring's "track" and "progress" layers -- their
        // distinct colors come from TextureProgressBar's own TintUnder/TintProgress (set in
        // Boot.tscn) rather than baking two separately-colored textures.
        var ring = await RasterizeIconGlyphAsync(iconFont, "radio_button_unchecked", 128, Colors.White);
        if (ring != null)
        {
            _progressRing.TextureUnder = ring;
            _progressRing.TextureProgress = ring;
        }

        var checkedIcon = await RasterizeIconGlyphAsync(iconFont, "check_box", 20, StepColorDone);
        if (checkedIcon != null) _saveLoginCheck.AddThemeIconOverride("checked", checkedIcon);

        var uncheckedIcon = await RasterizeIconGlyphAsync(iconFont, "check_box_outline_blank", 20, StepColorPending);
        if (uncheckedIcon != null) _saveLoginCheck.AddThemeIconOverride("unchecked", uncheckedIcon);
    }

    private async System.Threading.Tasks.Task<ImageTexture?> RasterizeIconGlyphAsync(Font iconFont, string glyphName, int size, Color color)
    {
        var subViewport = new SubViewport
        {
            Size = new Vector2I(size, size),
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Once,
        };
        AddChild(subViewport);

        var label = new Label
        {
            Text = glyphName,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        label.AddThemeFontOverride("font", iconFont);
        label.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(size * 0.85f));
        label.AddThemeColorOverride("font_color", color);
        subViewport.AddChild(label);

        // Same two-frame wait as CursorManager.SetupMagnifierCursorAsync -- the SubViewport
        // needs a render pass to actually rasterize the label before GetImage() has content.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        ImageTexture? result = null;
        var viewportTexture = subViewport.GetTexture();
        var image = viewportTexture?.GetImage();
        if (image != null)
        {
            result = ImageTexture.CreateFromImage(image);
            image.Dispose();
        }
        viewportTexture?.Dispose();
        subViewport.QueueFree();

        return result;
    }

    private static readonly Color StepColorDone = new(0.176f, 0.831f, 0.749f);
    private static readonly Color StepColorActive = new(0.925f, 0.937f, 0.953f);
    private static readonly Color StepColorPending = new(0.5f, 0.55f, 0.62f);
    private static readonly Color StepColorFailed = new(0.937f, 0.267f, 0.267f);

    /// <summary>(Re)builds the step-checklist Labels under %StepList, one per <see
    /// cref="LoadingSteps"/> entry. Called once from _Ready and again by <see
    /// cref="ResetLoadingProgress"/> at the start of every login attempt.</summary>
    private void BuildLoadingSteps()
    {
        foreach (Node child in _stepList.GetChildren()) child.QueueFree();

        _stepLabels = new Label[LoadingSteps.Length];
        for (int i = 0; i < LoadingSteps.Length; i++)
        {
            var lbl = new Label { Text = LoadingSteps[i] + "  [ ]" };
            lbl.AddThemeFontSizeOverride("font_size", 13);
            lbl.AddThemeColorOverride("font_color", StepColorPending);
            _stepList.AddChild(lbl);
            _stepLabels[i] = lbl;
        }
    }

    /// <summary>Resets the loading screen's progress ring + checklist to their initial (0%,
    /// all-pending) state at the start of a new login attempt.</summary>
    private void ResetLoadingProgress()
    {
        _completedSteps = 0;
        _progressRing.Value = 0;
        _progressPercentLabel.Text = "0%";
        BuildLoadingSteps();
    }

    /// <summary>Marks every step up to and including <paramref name="index"/> complete and
    /// advances the progress ring to match -- driven by real boot/login milestones actually
    /// finishing (see call sites in OnLoginPressed / ApplyLoginStage), never by elapsed time.
    /// Monotonic and idempotent: a stage that fires out of order or repeats (e.g. LibreMetaverse
    /// not raising every intermediate LoginStage on every grid) can't move the checklist
    /// backwards or re-trigger an already-completed step.</summary>
    private void CompleteLoadingStep(int index)
    {
        if (index < 0 || index >= _stepLabels.Length) return;
        if (index + 1 <= _completedSteps) return;

        _completedSteps = index + 1;
        for (int i = 0; i < _stepLabels.Length; i++)
        {
            if (i < _completedSteps)
            {
                _stepLabels[i].Text = LoadingSteps[i] + "  [x]";
                _stepLabels[i].AddThemeColorOverride("font_color", StepColorDone);
            }
            else if (i == _completedSteps)
            {
                _stepLabels[i].Text = LoadingSteps[i] + "  [...]";
                _stepLabels[i].AddThemeColorOverride("font_color", StepColorActive);
            }
            else
            {
                _stepLabels[i].Text = LoadingSteps[i] + "  [ ]";
                _stepLabels[i].AddThemeColorOverride("font_color", StepColorPending);
            }
        }

        float pct = (float)_completedSteps / _stepLabels.Length * 100f;
        _progressRing.Value = pct;
        _progressPercentLabel.Text = $"{Mathf.RoundToInt(pct)}%";
    }

    /// <summary>Flags the current (first not-yet-completed) step as failed, e.g. on a rejected
    /// login. Leaves earlier, already-completed steps as they were -- they genuinely did
    /// complete before the failure.</summary>
    private void MarkCurrentStepFailed()
    {
        if (_completedSteps < 0 || _completedSteps >= _stepLabels.Length) return;
        _stepLabels[_completedSteps].Text = LoadingSteps[_completedSteps] + "  [!]";
        _stepLabels[_completedSteps].AddThemeColorOverride("font_color", StepColorFailed);
    }

    /// <summary>Relays real, server-driven login handshake progress (see
    /// <see cref="GridSession.LoginProgress"/>) onto the checklist. Fires on whatever thread
    /// LibreMetaverse raises it on, so marshal to the main thread before touching Controls --
    /// same pattern as OnChatMessage/OnInstantMessageReceived below.</summary>
    private void OnLoginProgressStage(object? sender, LoginProgressEvent e)
    {
        CallDeferred(nameof(ApplyLoginStage), (int)e.Stage);
    }

    private void ApplyLoginStage(int stageInt)
    {
        switch ((LoginStage)stageInt)
        {
            case LoginStage.ConnectingToLogin:
                CompleteLoadingStep(1);
                break;
            case LoginStage.ReadingResponse:
                CompleteLoadingStep(2);
                break;
            case LoginStage.Redirecting:
                // Only fires when the login server redirects to a different handler -- rare for
                // OSGrid/local OpenSim, common enough on SL. Not its own checklist row (it would
                // sit permanently unchecked on the common direct-login path, which reads as
                // broken rather than accurate) -- treat it as still within the "Connecting to
                // region" step.
                break;
            case LoginStage.ConnectingToSim:
                CompleteLoadingStep(3);
                break;
            case LoginStage.Success:
                // Step 4 ("Entering world") is completed explicitly once OnLoginPressed finishes
                // its own post-login setup below -- LoginStage.Success only means the grid's
                // login handshake itself succeeded, not that the client has finished spinning up
                // the avatar/camera/world state.
                break;
            case LoginStage.Failed:
                MarkCurrentStepFailed();
                break;
        }
    }

    private async void OnLoginPressed()
    {
        _loginButton.Disabled = true;
        LogMessage($"Connecting to {_gridInput.Text} as {_firstInput.Text} {_lastInput.Text}...");

        ResetLoadingProgress();
        GetNode<Control>("%LoginScreen").Visible = false;
        GetNode<Control>("%LoadingScreen").Visible = true;

        if (_session != null)
        {
            _session.Dispose();
        }
        if (_worldSimulation != null) _worldSimulation.Dispose();
        _dialogQueueManager?.Dispose();
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
        // M5-4: llDialog popups. DialogQueueManager buffers ScriptDialogReceived (a network-
        // thread event) itself and is drained once per frame from _Process, same as
        // _worldSimulation above -- see its Pump() doc comment.
        var hudLayer = GetNodeOrNull<CanvasLayer>("HudLayer");
        if (hudLayer != null)
            _dialogQueueManager = new SLNG.App.UI.DialogQueueManager(_session, hudLayer);
        // Real server-driven login handshake progress (FEAT-UI-08) -- subscribed before
        // LoginAsync below so the initial ConnectingToLogin/ReadingResponse/ConnectingToSim
        // stages of THIS attempt are caught, not just a later re-login's.
        _session.LoginProgress += OnLoginProgressStage;
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
        // FEAT-ENV-01 Phase A: capture the region's Windlight/EEP environment so the parser can be
        // written against what the grid actually sends. Fires on a network thread, so the write is
        // deferred like everything else that leaves that thread.
        _session.RegionEnvironmentCaptured += (s, capture) =>
            Godot.Callable.From(() => DumpRegionEnvironment(capture)).CallDeferred();
        _session.RegionEnvironmentReceived += (s, env) =>
            Godot.Callable.From(() =>
            {
                LogRegionEnvironment(env);
                // FEAT-ENV-01 Phase D: hand the parsed cycle to the driver that actually paints
                // it. Region-scoped rather than avatar-scoped: crossing into a neighbor region
                // with its own environment replaces the cycle wholesale, same as a fresh login.
                _environmentDriver.SetCycle(env.Cycle, env.Source);
            }).CallDeferred();

        var creds = new LoginCredentials
        {
            GridLoginUri = _gridInput.Text,
            FirstName = _firstInput.Text,
            LastName = _lastInput.Text,
            Password = _passInput.Text,

            // The grid records this on every login and prints it in the region log
            // ("viewer SLNG 0.1.0, teleportflags ..."). It was never set, so LoginCredentials'
            // hardcoded default shipped to every sim we ever touched while the build moved on to
            // 0.7.x -- anyone reading a sim log saw a client seven minor versions stale, which is
            // exactly the sort of thing that gets reported as "your viewer is broken".
            Version = AppVersion.TrimStart('v'),
        };

        // Step 0 ("Initializing session") is genuinely done now -- everything above this line
        // (World/GridSession/WorldSimulation/AssetService/GpuCache, renderer Initialize calls)
        // already ran synchronously on the main thread.
        CompleteLoadingStep(0);

        var result = await _session.LoginAsync(creds);

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

            LogMessage($"[color=#2dd4bf]Login SUCCESS[/color] - AgentID: {result.AgentId}");
            if (!string.IsNullOrEmpty(result.Message))
            {
                LogMessage(result.Message);
            }

            // Hide the background and show the top menu. The loading screen itself stays up a
            // little longer -- through the post-login setup below -- so its final "Entering
            // world" step actually reflects that setup finishing, not just the login handshake.
            GetNode<Control>("%Background").Visible = false;
            _topMenu.Visible = true;

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

            // Post-login setup above (avatar controller, selection/cursor, camera) has now
            // genuinely finished -- the client is actually ready to render the world.
            CompleteLoadingStep(4);
            GetNode<Control>("%LoadingScreen").Visible = false;

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

    private void LogSculptNudge() =>
        LogMessage($"Sculpt U {ObjectRenderer.SculptUNudge * 128f:0.##} / " +
                   $"V {ObjectRenderer.SculptVNudge * 128f:0.##} grid steps");

    private void LogMessage(string message)
    {
        // The environment re-poll delivers through CallDeferred from a background task, so a
        // capture already in flight when the user closes the client lands one frame after the UI
        // is gone -- an ObjectDisposedException on RichTextLabel.AppendText, thrown twice on every
        // shutdown that happened to catch one. Godot's own liveness check is the reliable test
        // here; a null check is not, because the C# wrapper outlives the native object.
        if (_logPanel == null || !IsInstanceValid(_logPanel)) return;

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
