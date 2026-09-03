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

    /// <summary>Objects already reported by <see cref="OnParticleWireDiagnostic"/>, so a busy
    /// region logs one line per emitter instead of one per update. Touched from network threads.</summary>
    private readonly HashSet<uint> _particleSourcesLogged = new();
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

    // GridSession raises RegionEnvironmentReceived from a background task -- the CAPS environment
    // fetch resumes on a threadpool thread. Its payload (DayCycle / EnvironmentSource) isn't
    // Variant-safe, so it can't ride Node.CallDeferred(nameof(...)); and wrapping it in
    // Callable.From(lambda).CallDeferred() crashes the process when called off the main thread --
    // a custom (delegate-backed) Callable's deferred dispatch is main-thread-only in Godot .NET
    // (observed as a fatal AccessViolationException inside godotsharp_callable_call_deferred).
    // So the handler just parks the latest event here and _Process applies it on the main thread,
    // per AGENTS.md's "buffer incoming events, drain once per frame" rule.
    private RegionEnvironmentEvent? _pendingRegionEnvironment;

    // MVP2-3 Phase 4: see the _Process drain next to _pendingGroupInvites for why this waits
    // for CurrentRegionName instead of reading it directly off RegionConnected.
    private volatile bool _pendingArrivalToast;
    private string _lastArrivalRegionShown = "";

    private SLNG.App.UI.InventoryPanel? _inventoryPanel;
    private Node3D? _sunGizmo;

    private double _hudAccum;
    
    private SLNG.App.UI.TopMenu _topMenu = null!;

    /// <summary>FEAT-SL-01: always-present layer for windows that must work before there is a
    /// session -- the Terms-of-Service gate and About. See where it is created in _Ready.</summary>
    private CanvasLayer _dialogLayer = null!;

    /// <summary>The one About window, created on first open and hidden rather than freed so it
    /// keeps its position (PersistId "about_window").</summary>
    private SLNG.App.UI.AboutWindow? _aboutWindow;
    // Created lazily on first use -- see the Developer menu wiring below. Dev tooling only,
    // costs nothing until someone actually takes a measurement.
    private RenderBaselineSampler? _renderBaselineSampler;
    private SLNG.App.UI.StatsOverlay? _statsOverlay;
    private SLNG.App.UI.GraphicsSettings _graphicsSettings = new();
    private SLNG.App.UI.QualityPreferencesPage? _qualityPage;
    private SLNG.App.UI.DesignPreferencesPage? _designPage;
    private SLNG.App.UI.MaturityPreferencesPage? _maturityPage;

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
    private SLNG.App.UI.CameraSettings _cameraSettings = null!;

    // M5-3 Tabbed Chat window
    private SLNG.App.UI.ChatWindow _chatWindow = null!;
    private SLNG.App.UI.SnapshotWindow _snapshotWindow = null!;
    private SLNG.App.UI.EnvironmentWindow _environmentWindow = null!;
    // MVP2-3: minimap radar overlay + world map/search window.
    private SLNG.App.UI.MinimapOverlay _minimapOverlay = null!;
    private SLNG.App.UI.WorldMapWindow _worldMapWindow = null!;
    // FEAT-UI-18: teleport loading overlay. Fed by GridSession.TeleportProgress events buffered
    // off the network thread into _pendingTeleportProgress and drained in _Process.
    private SLNG.App.UI.TeleportOverlay _teleportOverlay = null!;
    private readonly System.Collections.Concurrent.ConcurrentQueue<SLNG.Core.TeleportProgressEvent> _pendingTeleportProgress = new();
    private readonly WindlightPresetLibrary _windlightPresets = new();
    private SLNG.Core.Services.ChatLogger _chatLogger = null!;
    
    // M5-2 Object Editing UI
    private ObjectSelectionController _objectSelectionController = null!;
    private SLNG.App.CursorManager _cursorManager = null!;
    private SLNG.App.UI.InWorldContextMenu _inWorldContextMenu = null!;
    private Godot.Button _standUpButton = null!;

    public static bool IsLoadingScreenVisible { get; private set; } = false;

    private bool _waitingForWorldLoad = false;
    private System.Guid _myAgentId = System.Guid.Empty;
    private double _worldLoadWaitTime = 0.0;

    // One independent ObjectEditWindow per edited object (keyed by its ECS Entity.Id) so
    // multiple objects can be open and edited at the same time instead of sharing one floater.
    private readonly System.Collections.Generic.Dictionary<System.Guid, SLNG.App.UI.ObjectEditWindow> _objectEditWindows = new();

    // FEAT-UI-13: one UserProfileWindow per avatar, keyed by agent id -- same multi-instance
    // pattern as _objectEditWindows. _userProfileWindows is only touched on the main thread;
    // _openProfileWindows mirrors its count for the network-thread event handlers to gate on
    // without racing the dictionary itself.
    private readonly System.Collections.Generic.Dictionary<System.Guid, SLNG.App.UI.UserProfileWindow> _userProfileWindows = new();
    private volatile int _openProfileWindows;

    public const string AppVersion = "v0.20.49-alpha";

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

        // FEAT-PERF-01: J2K decoding is CPU-heavy. The default .NET ThreadPool scales up slowly 
        // (1-2 threads/sec) causing massive queues when entering a region. We bump MinThreads 
        // to immediately utilize all available processor cores.
        System.Threading.ThreadPool.SetMinThreads(System.Environment.ProcessorCount, System.Environment.ProcessorCount);

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

        // Populate Grid Dropdown. FEAT-SL-01 adds the two Linden grids: Aditi (the BETA grid) is
        // listed FIRST of the two on purpose, because it is the one to test on -- it is a periodic
        // copy of the main grid, so nothing done there can damage a real account or real content.
        // Aditi has its OWN password: whatever the account's password was at copy time, not
        // necessarily today's Agni password.
        _gridDropdown.AddItem("OSGrid");
        _gridDropdown.SetItemMetadata(0, "http://hg.osgrid.org/");
        _gridDropdown.AddItem("Localhost");
        _gridDropdown.SetItemMetadata(1, "http://127.0.0.1:9000/");
        _gridDropdown.AddItem(SLNG.App.UI.L10n.Tr("ui.login.grid_sl_beta"));
        _gridDropdown.SetItemMetadata(2, LoginCredentials.SecondLifeBetaLoginUri);
        _gridDropdown.AddItem(SLNG.App.UI.L10n.Tr("ui.login.grid_sl"));
        _gridDropdown.SetItemMetadata(3, LoginCredentials.SecondLifeLoginUri);
        
        _gridDropdown.ItemSelected += (index) => 
        {
            _gridInput.Text = (string)_gridDropdown.GetItemMetadata((int)index);
        };
        _passInput = GetNode<LineEdit>("%PassInput");
        _saveLoginCheck = GetNode<CheckBox>("%SaveLoginCheck");
        _loginButton = GetNode<Button>("%LoginButton");
        _logPanel = GetNode<RichTextLabel>("%LogPanel");

        // A passive readout must never stop anyone clicking through it, not even while it is on
        // screen during login. Ignore on the container does not affect its children, so the login
        // form stays clickable. The boot log is also hidden outright once the world comes up --
        // see where the loading screen is dismissed.
        _logPanel.MouseFilter = Control.MouseFilterEnum.Ignore;
        _vboxContainer.MouseFilter = Control.MouseFilterEnum.Ignore;

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

        // FEAT-SL-01: a CanvasLayer that is up from the first frame and never hidden, unlike
        // HudLayer (created hidden, shown only after a successful login). The two windows that
        // must be reachable BEFORE there is a session live here: the Terms-of-Service gate, which
        // by definition appears while a login is being refused, and About, which the TPV Policy
        // wants reachable, not buried behind a successful connection. Above TopMenu's layer 100.
        _dialogLayer = new CanvasLayer { Name = "DialogLayer", Layer = 110 };
        AddChild(_dialogLayer);

        // The login screen's own way in to About (§1.g) -- the menu bar only exists in-world.
        var aboutLink = new Button
        {
            Text = SLNG.App.UI.L10n.Tr("ui.about.title"),
            Flat = true,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
        };
        aboutLink.AddThemeFontSizeOverride("font_size", 12);
        aboutLink.Pressed += ShowAboutWindow;
        loginVersionText?.GetParent()?.AddChild(aboutLink);
        if (loginVersionText != null)
            loginVersionText.GetParent().MoveChild(aboutLink, loginVersionText.GetIndex() + 1);

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

        // --selftest: the smoke test AGENTS.md has documented since the first commit (see
        // SelfTest). Deferred rather than called inline so the rest of _Ready -- renderers,
        // environment, HUD -- has finished constructing first: the point is to check the client as
        // it stands at the login screen, not half-built. Callable.From instead of
        // CallDeferred(nameof(...)) so it needs no method registration on this class.
        if (SelfTest.Requested)
        {
            Callable.From(() => SelfTest.Run(GetTree())).CallDeferred();
        }
    }

    /// <summary>Opens (or re-raises) the About window — TPV Policy §1.g. Reachable from the login
    /// screen and from App -> About, so it does not depend on being connected.</summary>
    private void ShowAboutWindow()
    {
        if (_aboutWindow == null || !IsInstanceValid(_aboutWindow))
        {
            _aboutWindow = new SLNG.App.UI.AboutWindow();
            _dialogLayer.AddChild(_aboutWindow);
        }

        _aboutWindow.Visible = true;
        _aboutWindow.MoveToFront();
    }

    /// <summary>Shows the grid's Terms of Service (or its critical message) and resolves to the
    /// user's answer — TPV Policy §1.f. Awaited by the login flow, which only retries with
    /// <c>agree_to_tos</c> / <c>read_critical</c> set once this returns true.
    ///
    /// The loading screen comes down for the duration, mirroring the reference viewer's
    /// <c>gViewerWindow->setShowProgress(false)</c> before it raises the same dialog: the login is
    /// not progressing, it is waiting on a decision.</summary>
    private System.Threading.Tasks.Task<bool> ShowTermsGateAsync(string gridLoginUri, string message, bool critical)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();

        GetNode<Control>("%LoadingScreenBlur").Visible = false;
        GetNode<Control>("%LoadingScreen").Visible = false;
        IsLoadingScreenVisible = false;

        var window = new SLNG.App.UI.TermsOfServiceWindow();
        _dialogLayer.AddChild(window);
        window.Answered += accepted => tcs.TrySetResult(accepted);
        window.Initialize(gridLoginUri, message, critical);

        return tcs.Task;
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
                _teleportActive = false;
                _teleportCameraResetPending = false;
                _teleportOverlay?.ForceHide();
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


        _topMenu.OnOpenPreferences = () => {
            // Re-read on open: F2 and F3/F4 change quality/design settings from outside the
            // dialog, so controls built once at startup would otherwise show stale values.
            // MaturityPreferencesPage has a different reason for the same fix -- see its own
            // Refresh() doc comment: SupportsMaturityPreference can read false right after login,
            // before the region's capability seed has actually resolved, and nothing was asking
            // it again once that settled. Measured live on Aditi.
            _qualityPage?.Refresh();
            _designPage?.Refresh();
            _maturityPage?.Refresh();
            _preferencesWindow.Visible = true;
        };

        _topMenu.OnOpenAbout = ShowAboutWindow;

        _topMenu.OnOpenEnvironment = () => _environmentWindow?.Toggle();
        _topMenu.OnOpenWorldMap = () => _worldMapWindow?.Toggle();
        _topMenu.OnOpenMinimap = () => _minimapOverlay?.Toggle();

        _topMenu.OnCreateLandmark = () => {
            var hudLayer = GetNodeOrNull<CanvasLayer>("HudLayer");
            if (hudLayer != null) OpenCreateLandmarkWindow(hudLayer);
        };

        _topMenu.OnRebakeAvatar = RebakeAvatar;
        _topMenu.OnCreateTestSkin = CreateTestSkin;
        _topMenu.OnBakeTestPattern = BakeTestPattern;

        // Escape hatch for a stuck attachment/HUD that inventory "Detach"
        // (DetachAttachmentIntoInv) can't shift -- ObjectDetach by localId instead.
        _topMenu.OnDetachAttachments = (hudOnly) => {
            int n = _session?.DetachAllAttachments(hudOnly) ?? 0;
            string msg = $"Detach: sent ObjectDetach for {n} {(hudOnly ? "HUD " : "")}attachment(s).";
            // NOT LogMessage: that writes to the boot LogPanel, which FEAT-UI-09 hides once the
            // loading screen goes away -- so the first version of this reported into a control
            // nobody can see, and the console had nothing either. A user-initiated, rare action
            // needs to land somewhere both the user and a later log read can find it.
            GD.Print($"[Detach] {msg}");
            _chatWindow?.AppendLocalChatMessage("System", msg);
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

        var cameraHud = new SLNG.App.UI.CameraHUD();
        cameraHud.Name = "CameraHUD";
        hudLayer.AddChild(cameraHud);

        var standUpMargin = new MarginContainer();
        standUpMargin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        standUpMargin.MouseFilter = Control.MouseFilterEnum.Ignore;
        standUpMargin.Visible = false; // Hide the container by default
        
        var vBox = new VBoxContainer();
        vBox.Alignment = BoxContainer.AlignmentMode.End;
        vBox.MouseFilter = Control.MouseFilterEnum.Ignore;
        
        var hBox = new HBoxContainer();
        hBox.Alignment = BoxContainer.AlignmentMode.Center;
        hBox.MouseFilter = Control.MouseFilterEnum.Ignore;
        
        var paddingMargin = new MarginContainer();
        paddingMargin.AddThemeConstantOverride("margin_bottom", 60);
        paddingMargin.MouseFilter = Control.MouseFilterEnum.Ignore;
        
        vBox.AddChild(hBox);
        hBox.AddChild(paddingMargin);
        
        _standUpButton = new Button 
        { 
            Text = SLNG.App.UI.L10n.Tr("ui.hud.stand_up"),
            CustomMinimumSize = new Godot.Vector2(120, 32)
        };
        _standUpButton.AddThemeFontSizeOverride("font_size", 16);
        paddingMargin.AddChild(_standUpButton);
        standUpMargin.AddChild(vBox);
        hudLayer.AddChild(standUpMargin);

        _standUpButton.Pressed += () => 
        {
            if (_localAgent != null)
            {
                var avatarComp = _localAgent.GetComponent<SLNG.Core.Components.AvatarComponent>();
                if (avatarComp != null && avatarComp.ActiveAnimations != null)
                {
                    // Forcefully stop all playing animations (including custom sit animations
                    // from chair scripts) so they don't get stuck when standing up. The server
                    // will automatically restart the default STAND/WALK animations.
                    foreach (var animId in avatarComp.ActiveAnimations)
                    {
                        _session?.StopAnimation(animId);
                    }
                }
            }
            _session?.Stand();
        };

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
        // FEAT-UI-13: right-click an avatar -> Profile / IM.
        _inWorldContextMenu.OnAvatarProfileClicked = (agentId, name) => OpenUserProfileWindow(hudLayer, agentId, name);
        _inWorldContextMenu.OnAvatarImClicked = (agentId, name) => _chatWindow.OpenOrFocusImTab(agentId, name);
        _inWorldContextMenu.OnAvatarOfferTeleportClicked = (agentId, name) => _session?.OfferTeleport(agentId);
        _inWorldContextMenu.OnAvatarMuteToggleClicked = (agentId, name) =>
            _session?.SetAvatarMuted(agentId, name, !_session.IsAvatarMuted(agentId));
        _inWorldContextMenu.OnCreatePrimClicked = (godotPos, type) =>
        {
            if (_session == null) return;
            _session.CreatePrim(type, RenderConfig.FromGodot(_session.CurrentRegionHandle, godotPos));
        };

        _snapshotWindow = new SLNG.App.UI.SnapshotWindow { Name = "SnapshotWindow" };
        hudLayer.AddChild(_snapshotWindow);
        _snapshotWindow.Initialize(hudLayer);

        // FEAT-ENV-02: the shipped Windlight presets. Loaded here (a directory listing, no
        // parsing) so the picker has its index before it is ever opened.
        _windlightPresets.Load();
        _environmentWindow = new SLNG.App.UI.EnvironmentWindow { Name = "EnvironmentWindow" };
        hudLayer.AddChild(_environmentWindow);
        _environmentWindow.Initialize(_windlightPresets, _environmentDriver);

        // MVP2-3: constructed here like every other panel (always present, hidden until
        // toggled); Initialize(...) happens later in OnLoginPressed once session/world/asset
        // plumbing actually exists (see that call site's comment).
        _minimapOverlay = new SLNG.App.UI.MinimapOverlay { Name = "MinimapOverlay" };
        hudLayer.AddChild(_minimapOverlay);
        // Double-click a roster row -> turn the real 3D camera to look at them (the radar's own
        // pan/zoom is separate, see MinimapOverlay's doc comment). Right-click -> the SAME shared
        // avatar context menu (Profile/IM/Offer Teleport/Mute) the in-world right-click gesture
        // shows -- never self (the roster excludes the local avatar by construction).
        _minimapOverlay.OnFocusAvatarRequested = (pos, forward) => _avatarController?.FocusOnAvatarFrontal(pos, forward);
        _minimapOverlay.OnAvatarContextMenuRequested = (screenPos, agentId, name) =>
            _inWorldContextMenu.ShowAvatarMenu(screenPos, agentId, name, isSelf: false, _session?.IsAvatarMuted(agentId) ?? false);
        _worldMapWindow = new SLNG.App.UI.WorldMapWindow { Name = "WorldMapWindow" };
        hudLayer.AddChild(_worldMapWindow);

        _chatLogger = new SLNG.Core.Services.ChatLogger();
        _chatWindow = new SLNG.App.UI.ChatWindow { Name = "ChatWindow" };
        hudLayer.AddChild(_chatWindow);
        _chatWindow.Initialize(_chatLogger);
        // Captures _session by reference (not by value at wiring time) so this keeps working
        // across the session getting replaced on re-login, same pattern as OnCreatePrimClicked above.
        _chatWindow.OnSendLocalChat = (text) => _session?.SendChat(text);
        // FEAT-UI-13: clicking a resident's name in chat, or the Friends tab's "Profile" button.
        _chatWindow.OnOpenProfileRequested = (agentId, name) => OpenUserProfileWindow(hudLayer, agentId, name);

        // FEAT-UI-18: modal teleport loading overlay. Its own CanvasLayer (Layer 100), added to
        // Boot rather than hudLayer so it covers the HUD and every window and stays up even if
        // "Toggle HUD" hid hudLayer. No session/world dependency -- safe to build here.
        _teleportOverlay = new SLNG.App.UI.TeleportOverlay { Name = "TeleportOverlay" };
        AddChild(_teleportOverlay);

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

        // Ensure the object is selected on the grid and visually (since right-click no longer auto-selects outside edit mode).
        _world.SelectEntity(entity);
        _session.SelectObject(localId);

        win.Closed += () =>
        {
            _objectEditWindows.Remove(entity.Id);
            _objectSelectionController.Unpin(entity.Id);
        };
        _objectEditWindows[entity.Id] = win;

        win.EditObject(entity, localId, _world);
    }

    /// <summary>
    /// FEAT-UI-13: opens (or refocuses) the profile window for one avatar. Keyed by agent id so a
    /// second open of the same avatar just raises the existing window, mirroring
    /// <see cref="OpenObjectEditWindow"/>. Boot forwards GridSession's profile-reply events to the
    /// matching open window (see <see cref="OnAvatarProfilePropertiesReceived"/> and siblings).
    /// </summary>
    private void OpenUserProfileWindow(CanvasLayer hudLayer, System.Guid agentId, string name)
    {
        if (_session == null || agentId == System.Guid.Empty) return;

        if (_userProfileWindows.TryGetValue(agentId, out var existing))
        {
            existing.Visible = true;
            existing.MoveToFront();
            return;
        }

        var win = new SLNG.App.UI.UserProfileWindow();
        hudLayer.AddChild(win);
        win.CascadeIndex = _userProfileWindows.Count % 8;
        win.OnOpenImRequested = (id, n) => _chatWindow.OpenOrFocusImTab(id, n);
        win.Closed += () =>
        {
            if (_userProfileWindows.Remove(agentId)) _openProfileWindows = _userProfileWindows.Count;
        };
        _userProfileWindows[agentId] = win;
        _openProfileWindows = _userProfileWindows.Count;
        win.Initialize(agentId, name, _session, _gpuCache, _assetService);
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
            new("snapshot", "Snapshot", "add_a_photo", () => _snapshotWindow.Toggle(), () => _snapshotWindow.Visible),
            new("environment", "Environment", "wb_sunny", () => _environmentWindow.Toggle(), () => _environmentWindow.Visible),
            new("minimap", "Minimap", "radar", () => _minimapOverlay.Toggle(), () => _minimapOverlay.Visible),
            new("worldmap", "World Map", "map", () => _worldMapWindow.Toggle(), () => _worldMapWindow.Visible),
        };

        _toolbarSettings = new SLNG.App.UI.ToolbarSettings();
        _toolbarSettings.EnsureDefaults(toolbarItems.ConvertAll(i => i.Id));
        _toolbarSettings.Load();

        _buttonBar = new SLNG.App.UI.ButtonBar { Name = "ButtonBar" };
        hudLayer.AddChild(_buttonBar);
        _buttonBar.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _buttonBar.Initialize(toolbarItems, _toolbarSettings);

        _preferencesWindow = new SLNG.App.UI.PreferencesWindow { Name = "PreferencesWindow" };
        hudLayer.AddChild(_preferencesWindow);
        var toolbarPage = new SLNG.App.UI.ToolbarPreferencesPage();
        _preferencesWindow.AddTab(SLNG.App.UI.L10n.Tr("ui.preferences.tab_toolbar"), toolbarPage);
        toolbarPage.Initialize(toolbarItems, _toolbarSettings);

        var displayPage = new SLNG.App.UI.DisplayPreferencesPage();
        _preferencesWindow.AddTab(SLNG.App.UI.L10n.Tr("ui.preferences.tab_display"), displayPage);
        displayPage.Initialize(_uiSettings, _localizationManager);

        _cameraSettings = new SLNG.App.UI.CameraSettings();
        _cameraSettings.Load();
        cameraHud.SetCameraSettings(_cameraSettings);
        var cameraPage = new SLNG.App.UI.CameraPreferencesPage();
        _preferencesWindow.AddTab(SLNG.App.UI.L10n.Tr("ui.preferences.tab_camera"), cameraPage);
        cameraPage.Initialize(_cameraSettings);

        _qualityPage = new SLNG.App.UI.QualityPreferencesPage { Name = SLNG.App.UI.L10n.Tr("ui.preferences.tab_quality") };
        _preferencesWindow.AddTab(SLNG.App.UI.L10n.Tr("ui.preferences.tab_quality"), _qualityPage);
        _qualityPage.Initialize(_graphicsSettings, ApplyGraphicsSettings);

        _designPage = new SLNG.App.UI.DesignPreferencesPage { Name = SLNG.App.UI.L10n.Tr("ui.preferences.tab_design") };
        _preferencesWindow.AddTab(SLNG.App.UI.L10n.Tr("ui.preferences.tab_design"), _designPage);
        _designPage.Initialize(_graphicsSettings, ApplyGraphicsSettings);

        var networkPage = new SLNG.App.UI.NetworkPreferencesPage();
        _preferencesWindow.AddTab(SLNG.App.UI.L10n.Tr("ui.preferences.tab_network"), networkPage);
        networkPage.Initialize(ProjectSettings.GlobalizePath("user://cache/assets"));

        // "Age settings" -- Second Life's content-rating preference (General/Moderate/Adult).
        // Rebound to the live session per login in BindSession, once a session exists to read
        // GridSession.AccountMaturityMax/PreferredMaturity from -- see _maturityPage's own class
        // doc and the OnLoginPressed call site below.
        _maturityPage = new SLNG.App.UI.MaturityPreferencesPage();
        _preferencesWindow.AddTab(SLNG.App.UI.L10n.Tr("ui.preferences.tab_maturity"), _maturityPage);
        _maturityPage.Initialize();

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
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
            DirectionalShadowBlendSplits = true,
            DirectionalShadowSplit1 = 0.08f,
            DirectionalShadowSplit2 = 0.22f,
            DirectionalShadowSplit3 = 0.50f,
            DirectionalShadowMaxDistance = 150.0f,
            ShadowBias = 0.015f,
            ShadowNormalBias = 1.0f,
            ShadowOpacity = 0.88f,
            ShadowBlur = 1.8f,
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


    public override void _Process(double delta)
    {
        // Drain the region-environment event buffered off-thread (see _pendingRegionEnvironment).
        // FEAT-ENV-01 Phase D: region-scoped -- crossing into a neighbor region with its own
        // environment replaces the cycle wholesale, same as a fresh login.
        var pendingEnv = System.Threading.Interlocked.Exchange(ref _pendingRegionEnvironment, null);
        if (pendingEnv != null)
            _environmentDriver.SetCycle(pendingEnv.Cycle, pendingEnv.Source);

        // FEAT-UI-13: apply avatar-profile replies buffered off the network thread.
        while (_profileUiWork.TryDequeue(out var profileWork)) profileWork();

        // M5-3: group invitations, same off-thread buffering reason.
        while (_pendingGroupInvites.TryDequeue(out var invite)) ShowGroupInvitation(invite);

        // MVP2-3 Phase 4: "Arrived in <region>" toast. RegionConnected only flags that we
        // arrived somewhere NEW -- the region's name usually isn't known yet at that exact
        // moment (it arrives via a later RegionHandshake), so this waits here until
        // CurrentRegionName is actually populated and different from the last one shown, rather
        // than risking an "Arrived in ''" toast from reading it too early.
        if (_pendingArrivalToast && _session != null)
        {
            var arrivedName = _session.CurrentRegionName;
            if (!string.IsNullOrEmpty(arrivedName) && arrivedName != _lastArrivalRegionShown)
            {
                _lastArrivalRegionShown = arrivedName;
                _pendingArrivalToast = false;
                LogMessage($"[color=lightgreen]{SLNG.App.UI.L10n.TrFormat("ui.map.arrived_in", arrivedName)}[/color]");
            }
        }

        // FEAT-UI-18: teleport loading overlay. Drain every buffered stage this frame -- the last
        // one wins for what's displayed, and a terminal stage still gets to dismiss it.
        while (_pendingTeleportProgress.TryDequeue(out var tp)) ApplyTeleportProgress(tp);
        if (_teleportCameraResetPending) TryApplyTeleportCameraReset();

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

        if (_waitingForWorldLoad && _world != null && _session != null)
        {
            _worldLoadWaitTime += delta;
            var myAgent = _world.GetEntity(_myAgentId);
            var avatar = myAgent?.GetComponent<SLNG.Core.Components.AvatarComponent>();
            bool agentReady = avatar?.VisualParams != null;
            // Wait for agent appearance, plus an extra 1.5s for meshes, OR timeout after 10s.
            if ((agentReady && _worldLoadWaitTime > 1.5) || _worldLoadWaitTime > 10.0)
            {
                _waitingForWorldLoad = false;
                CompleteLoadingStep(4);
                IsLoadingScreenVisible = false;
                GetNode<Control>("%LoadingScreenBlur").Visible = false;
                GetNode<Control>("%LoadingScreen").Visible = false;
            }
        }

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
        if (_standUpButton != null)
        {
            GetLocalAgentTransform(); // Ensure _localAgent is populated if available
            if (_localAgent != null)
            {
                var avatarComp = _localAgent.GetComponent<SLNG.Core.Components.AvatarComponent>();
                if (avatarComp != null)
                {
                    bool isSitting = avatarComp.SittingOnLocalId != 0;
                    // The container we want to toggle is standUpMargin
                    var container = _standUpButton.GetParent().GetParent().GetParent().GetParent<Control>();
                    if (container != null && container.Visible != isSitting)
                    {
                        GD.Print($"[HUD] Toggling StandUp button. SittingOnLocalId={avatarComp.SittingOnLocalId}");
                        container.Visible = isSitting;
                    }
                }
            }
            else
            {
                var container = _standUpButton.GetParent().GetParent().GetParent().GetParent<Control>();
                if (container != null) container.Visible = false;
            }
        }
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

    /// <summary>Maps a login URI to the short grid name GridDropdown already uses for it
    /// ("Second Life", "OSGrid", ...), so the same wording shows up everywhere a grid is named.
    /// Falls back to the URI's host for anything not one of GridDropdown's known entries -- a
    /// custom OpenSim grid the user typed by hand -- rather than the whole login.cgi URL.</summary>
    private string GetGridDisplayName(string? gridLoginUri)
    {
        if (string.IsNullOrEmpty(gridLoginUri)) return "?";
        for (int i = 0; i < _gridDropdown.ItemCount; i++)
        {
            if ((string)_gridDropdown.GetItemMetadata(i) == gridLoginUri)
                return _gridDropdown.GetItemText(i);
        }
        return System.Uri.TryCreate(gridLoginUri, System.UriKind.Absolute, out var uri) ? uri.Host : gridLoginUri;
    }

    /// <summary>Selects the GridDropdown entry matching a login URI, or clears the selection when
    /// it doesn't match one of the known grids (a custom grid) -- so the dropdown never shows a
    /// grid other than the one actually loaded into GridInput (found live: selecting a saved
    /// Second Life profile left the dropdown showing whatever it last had, "OSGrid" by default,
    /// while the login URL underneath it was really agni's).</summary>
    private void SyncGridDropdownToUri(string? gridLoginUri)
    {
        for (int i = 0; i < _gridDropdown.ItemCount; i++)
        {
            if ((string)_gridDropdown.GetItemMetadata(i) == gridLoginUri)
            {
                _gridDropdown.Select(i);
                return;
            }
        }
        _gridDropdown.Selected = -1;
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

                // The saved profile's storage KEY is its own thing (see the login-success handler
                // below) and stays whatever it always was for backward compatibility with an
                // existing logins.cfg -- only the dropdown's TEXT changes here, to "first last /
                // grid" instead of the raw key, which for an older save is "first last @
                // https://login.agni.lindenlab.com/cgi-bin/login.cgi" (a URL, not a grid name).
                string first = (string)_loginsConfig.GetValue(profile, "first", "");
                string last = (string)_loginsConfig.GetValue(profile, "last", "");
                string grid = (string)_loginsConfig.GetValue(profile, "grid", "");
                string display = $"{first} {last} / {GetGridDisplayName(grid)}";

                _profileDropdown.AddItem(display);
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
        SyncGridDropdownToUri(_gridInput.Text);

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
                bool anyOn = _graphicsSettings.PostFxSsao || _graphicsSettings.PostFxSsil || _graphicsSettings.PostFxGlow || _graphicsSettings.PostFxVolumetricFog;
                bool target = !anyOn;
                _graphicsSettings.SetPostFxSsao(target);
                _graphicsSettings.SetPostFxSsil(target);
                _graphicsSettings.SetPostFxGlow(target);
                _graphicsSettings.SetPostFxVolumetricFog(target);
                ApplyGraphicsSettings();
                LogMessage($"Post-FX {(target ? "enabled" : "disabled")}");
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
            else if (keyEvent.Keycode == Key.O && keyEvent.CtrlPressed)
            {
                // Ctrl+O — open the inventory straight on the Outfits tab (FEAT-INV-04).
                _inventoryPanel?.OpenOnOutfits();
            }
            else if (keyEvent.Keycode == Key.R && keyEvent.CtrlPressed && keyEvent.AltPressed)
            {
                // Ctrl+Alt+R — rebake the avatar, the same shortcut the real viewer uses
                // (FEAT-AVATAR-01). Also in the World menu.
                RebakeAvatar();
            }
            else if (keyEvent.Keycode == Key.T && keyEvent.CtrlPressed && keyEvent.AltPressed)
            {
                // Ctrl+Alt+T — put a known-answer test skin in the inventory (FEAT-AVATAR-01).
                // Creates items and uploads assets, so it stays a deliberate keystroke.
                CreateTestSkin();
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

    // FEAT-UI-18: true between a teleport's Started and its terminal stage. Gates the mid-flight
    // Progress updates so a stray late event can't revive the overlay's text after it has already
    // shown "Arrived" / a failure and begun its fade-out.
    private bool _teleportActive;

    // FEAT-UI-18 review feedback: after arriving, pan the camera back behind the avatar (a
    // teleport should not leave it aimed at whatever it was focused on before). Deferred via
    // _Process until the local avatar entity has actually been re-placed into the destination
    // region, so ResetCamera's avatar-follow target isn't a stale pre-teleport position.
    private bool _teleportCameraResetPending;
    private double _teleportCameraResetDeadlineMsec;

    /// <summary>Drives the teleport loading overlay from a buffered <see cref="TeleportProgressEvent"/>
    /// (see the drain in _Process). Text is our own localised per-stage string rather than
    /// LibreMetaverse's inconsistent English progress narration; a real failure reason (timeout,
    /// rejection) is passed through verbatim since it is the actionable part.</summary>
    private void ApplyTeleportProgress(SLNG.Core.TeleportProgressEvent e)
    {
        switch (e.Stage)
        {
            case SLNG.Core.TeleportStage.Started:
                _teleportActive = true;
                _teleportOverlay.ShowProgress(SLNG.App.UI.L10n.Tr("ui.teleport.requesting"));
                break;
            case SLNG.Core.TeleportStage.Progress:
                if (_teleportActive)
                    _teleportOverlay.SetStatus(SLNG.App.UI.L10n.Tr("ui.teleport.in_progress"));
                break;
            case SLNG.Core.TeleportStage.Finished:
                _teleportActive = false;
                _teleportOverlay.Finish(true, SLNG.App.UI.L10n.Tr("ui.teleport.arrived"));
                _teleportCameraResetPending = true;
                _teleportCameraResetDeadlineMsec = Time.GetTicksMsec() + 4000; // fire anyway if the avatar never confirms
                break;
            case SLNG.Core.TeleportStage.Failed:
                _teleportActive = false;
                _teleportOverlay.Finish(false, string.IsNullOrWhiteSpace(e.Message)
                    ? SLNG.App.UI.L10n.Tr("ui.teleport.failed")
                    : e.Message);
                break;
            case SLNG.Core.TeleportStage.Cancelled:
                _teleportActive = false;
                _teleportOverlay.Finish(false, SLNG.App.UI.L10n.Tr("ui.teleport.cancelled"));
                break;
        }
    }

    /// <summary>FEAT-UI-18 review feedback: snap the camera back behind the avatar once a teleport
    /// lands. Waits until the local avatar entity's region handle catches up to the session's
    /// current region (the first AvatarUpdate from the destination sim, applied by
    /// WorldSimulation.Pump) so ResetCamera's follow target isn't a stale pre-teleport position;
    /// a hard deadline stops the flag from ever stranding.</summary>
    private void TryApplyTeleportCameraReset()
    {
        bool avatarInDestination = false;
        if (_world != null && _session != null && _session.CurrentRegionHandle != 0)
        {
            foreach (var e in _world.GetAllEntities())
            {
                if (e.GetComponent<SLNG.Core.Components.AvatarComponent>()?.IsLocalAgent != true) continue;
                avatarInDestination = e.RegionHandle == _session.CurrentRegionHandle;
                break;
            }
        }

        if (avatarInDestination || Time.GetTicksMsec() >= _teleportCameraResetDeadlineMsec)
        {
            _teleportCameraResetPending = false;
            _avatarController?.ResetCamera();
        }
    }

    private async void OnLoginPressed()
    {
        _loginButton.Disabled = true;
        LogMessage($"Connecting to {_gridInput.Text} as {_firstInput.Text} {_lastInput.Text}...");

        // Switch UI views
        GetNode<Control>("%LoginScreen").Visible = false;
        GetNode<Control>("%LoadingScreenBlur").Visible = true;
        GetNode<Control>("%LoadingScreen").Visible = true;
        IsLoadingScreenVisible = true;
        // FEAT-UI-18: a teleport overlay left up from the previous session has no more progress
        // events coming -- drop it so the login loading screen isn't stacked under it.
        _teleportActive = false;
        _teleportCameraResetPending = false;
        _teleportOverlay?.ForceHide();

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

        // Same for open profile windows -- FEAT-UI-13.
        foreach (var win in _userProfileWindows.Values) win.QueueFree();
        _userProfileWindows.Clear();
        _openProfileWindows = 0;

        // ...and any pending group invitation, which is bound to the session it arrived on.
        foreach (var win in _groupInviteWindows.Values) win.QueueFree();
        _groupInviteWindows.Clear();
        while (_pendingGroupInvites.TryDequeue(out _)) { }

        _lastArrivalRegionShown = ""; // MVP2-3: a relogin into the same region must still toast
        _world = new SLNG.Core.ECS.World();
        _session = new GridSession();
        // FEAT-AVATAR-01: the JPEG2000 codec lives in SLNG.Assets and SLNG.Net may not reference it,
        // so the composition root supplies it. Without this a bake composites correctly and then
        // encodes to a few hundred bytes of nothing.
        _session.UseBakeEncoder(new SLNG.Assets.J2KBakeTextureEncoder());
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
        _maturityPage?.BindSession(_session);
        _chatWindow.BindSession(_session);
        // MVP2-3: needs the session (map/radar protocol calls), the world (own-avatar position
        // for the marker/heading), and the asset plumbing (map tile textures) -- all three only
        // exist from here on, so this can't happen alongside the other window construction in
        // SetupHud().
        _minimapOverlay.Initialize(_world, _session);
        _worldMapWindow.Initialize(_session, _gpuCache, _assetService, _world);

        _session.ChatMessageReceived += OnChatMessage;
        _session.InstantMessageReceived += OnInstantMessageReceived;
        // M5-3 Phase 2: group chat. Same network-thread marshalling reason as the IM handlers.
        _session.GroupChatMessageReceived += OnGroupChatMessageReceived;
        _session.GroupChatJoined += OnGroupChatJoinedResult;
        _session.GroupInvitationReceived += OnGroupInvitationReceived;
        // FEAT-UI-13: profile replies + name resolution, routed to whichever profile window is open
        // for that avatar. All fire on a network thread -- marshal before touching the Control tree.
        _session.AvatarPropertiesReceived += OnAvatarProfilePropertiesReceived;
        _session.AvatarInterestsReceived += OnAvatarProfileInterestsReceived;
        _session.AvatarGroupsReceived += OnAvatarProfileGroupsReceived;
        _session.AvatarPicksReceived += OnAvatarProfilePicksReceived;
        _session.AvatarPickDetailReceived += OnAvatarProfilePickDetailReceived;
        _session.AvatarClassifiedsReceived += OnAvatarProfileClassifiedsReceived;
        _session.NameResolved += OnProfileNameResolved;
        _session.DisplayNameResolved += OnProfileNameResolved;
        // A particle system can vanish at three separate places between the wire and the screen
        // -- no block in the ObjectUpdate, a CRC of 0, or an update that is not full -- and all
        // three look identical in-world: no particles. This says whether one ever arrived at all,
        // which is the half ObjectParticles' own --diag line cannot report.
        if (Diagnostics.Enabled)
        {
            _session.ObjectUpdateReceived += OnParticleWireDiagnostic;
        }
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
        // RegionConnected fires on a LibreMetaverse network thread; RegionEnvironmentReceived on a
        // threadpool continuation. Neither may call a Godot API directly, and Callable.From(lambda)
        // .CallDeferred() is itself unsafe off the main thread (see _pendingRegionEnvironment) --
        // route through the Node.CallDeferred(nameof(...)) path / a per-frame drain instead.
        _session.RegionConnected += (s, regionHandle) =>
            CallDeferred(nameof(ApplyRegionOrigin), regionHandle.ToString());
        // MVP2-3 Phase 4: flip the flag the _Process drain above watches. A plain bool write is
        // fine here -- worst case the toast is a frame late, same tolerance as every other
        // "parked" flag in this class.
        _session.RegionConnected += (s, regionHandle) => _pendingArrivalToast = true;

        _session.RegionEnvironmentReceived += (s, env) => _pendingRegionEnvironment = env;

        // FEAT-UI-18: teleport progress -> loading overlay. Raised on a LibreMetaverse network
        // thread, so buffer here and apply on the main thread in _Process, same pattern as the
        // arrival toast / region environment above.
        _session.TeleportProgress += (s, e) => _pendingTeleportProgress.Enqueue(e);

        // FEAT-AVATAR-01: a wear/detach of a system wearable was refused because this region has no
        // server-side baking. Tell the user in nearby chat (fires on a network thread — marshal).
        _session.WearableEditUnavailable += (s, name) => CallDeferred(nameof(NotifyWearableEditUnavailable), name);
        // A refusal is not a failure -- it already carries the explanation, so it is shown as-is.
        _session.WearableEditRefused += (s, reason) => CallDeferred(nameof(NotifyBakeResult), reason);

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

        // FEAT-SL-01 / TPV Policy §1.f: a grid may refuse the login with reason "tos" (accept the
        // Terms of Service first) or "critical" (read this message first). Both are answerable, and
        // the ONLY legitimate answer is the user's own -- LibreMetaverse's LoginParams defaults
        // agree_to_tos and read_critical to true, which would accept on their behalf, so
        // LoginCredentials defaults both to false and they are set here and nowhere else.
        //
        // Loop rather than a single retry because a grid can demand both in turn: accept the ToS,
        // and the next attempt comes back asking for the critical message. The reference viewer's
        // handleTOSResponse -> reconnect() has the same shape.
        while (result.RequiresTermsAcceptance || result.RequiresCriticalAcknowledgement)
        {
            bool critical = result.RequiresCriticalAcknowledgement;
            bool accepted = await ShowTermsGateAsync(creds.GridLoginUri, result.Message ?? "", critical);
            if (!accepted)
            {
                result = LoginResult.Fail("tos-declined", SLNG.App.UI.L10n.Tr("ui.tos.declined"));
                break;
            }

            creds = critical ? creds with { ReadCritical = true } : creds with { AgreeToTos = true };

            // Put the loading screen back for the retry -- ShowTermsGateAsync took it down.
            GetNode<Control>("%LoadingScreenBlur").Visible = true;
            GetNode<Control>("%LoadingScreen").Visible = true;
            IsLoadingScreenVisible = true;

            LogMessage($"Accepted, retrying login to {creds.GridLoginUri}...");
            result = await _session.LoginAsync(creds);
        }

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
            if (_cameraSettings != null)
                _avatarController.SetCameraSettings(_cameraSettings); // FEAT-UI-12: persisted FOV / distance / focus height

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
            _myAgentId = result.AgentId != null ? System.Guid.Parse(result.AgentId) : System.Guid.Empty;
            _waitingForWorldLoad = true;
            _worldLoadWaitTime = 0.0;

            // FEAT-UI-13: pull the account mute list once so a profile window's Mute/Unmute button
            // opens showing the right state.
            _session.RequestMuteList();

            // The boot log goes with it. It is not inside %LoginScreen, so it used to survive the
            // login and sit in-world as a bottom-anchored, full-width, 150 px strip of [ENV]
            // spam -- over the avatar, over the world, and over any worn HUD. It also SWALLOWED
            // every click landing in that strip, which is how an AO HUD came to work along the
            // top of the window and not along the bottom ("[HUD] click at (1884, 1004) swallowed
            // by GUI control 'LogPanel'"). It is a login-progress readout; in-world it is clutter
            // that happened to also eat input.
            //
            // Hidden rather than freed: LogMessage still writes to it, a failed login puts the
            // login screen back, and everything it prints also goes to godot.log.
            _vboxContainer.Visible = false;

            LogMessage($"[System] Login succeeded! Agent: {result.AgentId}");
        }
        else
        {
            LogMessage($"[System] Login failed: {result.Message}");
            GetNode<Control>("%LoadingScreenBlur").Visible = false;
            GetNode<Control>("%LoadingScreen").Visible = false;
            GetNode<Control>("%LoginScreen").Visible = true;
            _vboxContainer.Visible = true;   // back with the login screen, where it is the point
            _loginButton.Disabled = false;
        }
    }

    /// <summary>
    /// Reports, once per object, that a particle system reached the neutral event layer. Raised
    /// on a LibreMetaverse network thread, so it touches nothing but its own set.
    /// </summary>
    private void OnParticleWireDiagnostic(object? sender, ObjectUpdateEvent e)
    {
        if (e.Particles is null)
        {
            return;
        }

        lock (_particleSourcesLogged)
        {
            if (!_particleSourcesLogged.Add(e.LocalId))
            {
                return;
            }
        }

        GD.Print($"[ParticleWire] localId={e.LocalId} pattern={e.Particles.Pattern} "
            + $"partMaxAge={e.Particles.PartMaxAge:0.###}s srcMaxAge={e.Particles.SourceMaxAge:0.###}s "
            + $"burst={e.Particles.BurstPartCount}/{e.Particles.BurstRate:0.###}s "
            + $"flags={e.Particles.PartDataFlags} full={e.IsFullUpdate}");
    }

    private void OnChatMessage(object? sender, ChatMessageEvent e)
    {
        // ChatMessageReceived fires on a LibreMetaverse network thread -- marshal to the main
        // thread before touching ChatWindow's Control tree. FEAT-UI-13: pass the speaker's agent
        // id through (as a string -- Guid isn't a Variant CallDeferred arg) only when the sim
        // tagged the source as a real avatar, so the name becomes a profile link.
        string sourceId = e.FromAgent && e.SourceId != System.Guid.Empty ? e.SourceId.ToString() : "";
        CallDeferred(nameof(AppendChatMessage), e.FromName, e.Message, sourceId);
    }

    private void AppendChatMessage(string fromName, string message, string sourceAgentId)
    {
        var id = System.Guid.TryParse(sourceAgentId, out var g) ? g : System.Guid.Empty;
        _chatWindow.AppendLocalChatMessage(fromName, message, id);
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

    private void OnGroupChatMessageReceived(object? sender, SLNG.Core.GroupChatMessageEvent e)
    {
        // Same marshalling reason as OnChatMessage/OnInstantMessageReceived -- fires on a
        // LibreMetaverse network thread. Guids travel as strings (not Variant-safe). The group
        // NAME is resolved here rather than in ChatWindow because the shared name cache lives on
        // GridSession; an incoming group message names only the speaker.
        string groupName = "";
        _session?.TryGetCachedName(e.GroupId, out groupName);
        CallDeferred(nameof(AppendGroupChatMessage), e.GroupId.ToString(), groupName ?? "",
            e.FromAgentId.ToString(), e.FromAgentName, e.Message);
    }

    private void AppendGroupChatMessage(string groupId, string groupName, string fromAgentId, string fromAgentName, string message)
    {
        _chatWindow.AppendGroupChatMessage(
            System.Guid.Parse(groupId), groupName,
            System.Guid.TryParse(fromAgentId, out var from) ? from : System.Guid.Empty,
            fromAgentName, message);
    }

    private void OnGroupChatJoinedResult(object? sender, SLNG.Core.GroupChatJoinedEvent e)
        => CallDeferred(nameof(ApplyGroupChatJoined), e.GroupId.ToString(), e.Success);

    private void ApplyGroupChatJoined(string groupId, bool success)
        => _chatWindow.OnGroupChatJoinResult(System.Guid.Parse(groupId), success);

    /// <summary>Open "Join group?" prompts, keyed by group id so a repeated invitation raises the
    /// existing window instead of stacking a second one.</summary>
    private readonly System.Collections.Generic.Dictionary<System.Guid, SLNG.App.UI.GroupInvitationWindow> _groupInviteWindows = new();

    /// <summary>Invitations buffered off the network thread. A GroupInvitationEvent is a plain
    /// record and so not Variant-safe for CallDeferred — same reason the profile events ride a
    /// queue rather than a deferred call.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<SLNG.Core.GroupInvitationEvent> _pendingGroupInvites = new();

    private void OnGroupInvitationReceived(object? sender, SLNG.Core.GroupInvitationEvent e)
        => _pendingGroupInvites.Enqueue(e);

    private void ShowGroupInvitation(SLNG.Core.GroupInvitationEvent e)
    {
        if (_session == null) return;
        var hudLayer = GetNodeOrNull<CanvasLayer>("HudLayer");
        if (hudLayer == null) return;

        if (_groupInviteWindows.TryGetValue(e.GroupId, out var existing) && IsInstanceValid(existing))
        {
            existing.MoveToFront();
            return;
        }

        var win = new SLNG.App.UI.GroupInvitationWindow();
        hudLayer.AddChild(win);
        win.CascadeIndex = _groupInviteWindows.Count % 8;
        win.Closed += () => _groupInviteWindows.Remove(e.GroupId);
        _groupInviteWindows[e.GroupId] = win;
        win.Initialize(_session, e);

        GD.Print($"[GroupInvite] {e.FromName} -> group {e.GroupId} fee L${e.MembershipFee}");
    }

    // ---- FEAT-UI-13: avatar profile events -----------------------------------------------------
    // The reply payloads are plain C# records (not Variant-safe), so instead of CallDeferred they
    // ride a concurrent queue drained on the main thread in _Process. Each closure targets the one
    // open profile window for that avatar, if any.
    private readonly System.Collections.Concurrent.ConcurrentQueue<System.Action> _profileUiWork = new();

    private void EnqueueProfileWork(System.Guid agentId, System.Action<SLNG.App.UI.UserProfileWindow> apply)
    {
        if (_openProfileWindows == 0) return; // network thread -- see _openProfileWindows
        _profileUiWork.Enqueue(() =>
        {
            if (_userProfileWindows.TryGetValue(agentId, out var win) && Godot.GodotObject.IsInstanceValid(win))
                apply(win);
        });
    }

    private void OnAvatarProfilePropertiesReceived(object? sender, SLNG.Core.AvatarPropertiesEvent e)
        => EnqueueProfileWork(e.Properties.AgentId, w => w.ApplyProperties(e.Properties));

    private void OnAvatarProfileInterestsReceived(object? sender, SLNG.Core.AvatarInterestsEvent e)
        => EnqueueProfileWork(e.Interests.AgentId, w => w.ApplyInterests(e.Interests));

    private void OnAvatarProfileGroupsReceived(object? sender, SLNG.Core.AvatarGroupsEvent e)
        => EnqueueProfileWork(e.AgentId, w => w.ApplyGroups(e.Groups));

    private void OnAvatarProfilePicksReceived(object? sender, SLNG.Core.AvatarPicksEvent e)
        => EnqueueProfileWork(e.AgentId, w => w.ApplyPicks(e.Picks));

    private void OnAvatarProfilePickDetailReceived(object? sender, SLNG.Core.AvatarPickDetailEvent e)
    {
        // A pick detail isn't keyed by avatar id -- route it to every open profile window; each
        // one ignores a pick id it didn't ask for.
        if (_openProfileWindows == 0) return;
        var pick = e.Pick;
        _profileUiWork.Enqueue(() =>
        {
            foreach (var win in _userProfileWindows.Values)
                if (Godot.GodotObject.IsInstanceValid(win)) win.ApplyPickDetail(pick);
        });
    }

    private void OnAvatarProfileClassifiedsReceived(object? sender, SLNG.Core.AvatarClassifiedsEvent e)
        => EnqueueProfileWork(e.AgentId, w => w.ApplyClassifieds(e.Classifieds));

    private void OnProfileNameResolved(object? sender, SLNG.Core.NameResolvedEvent e)
    {
        if (_openProfileWindows == 0) return; // network thread -- see _openProfileWindows
        var id = e.Id;
        var name = e.Name;
        _profileUiWork.Enqueue(() =>
        {
            foreach (var win in _userProfileWindows.Values)
                if (Godot.GodotObject.IsInstanceValid(win)) win.OnNameResolved(id, name);
        });
    }

    // Deferred target for GridSession.RegionConnected. The handle travels as a string because a
    // region handle can exceed long.MaxValue and ulong is not a Variant-safe CallDeferred arg.
    private void ApplyRegionOrigin(string regionHandle)
    {
        var handle = ulong.Parse(regionHandle);
        RenderConfig.SetRegionOrigin(handle);
        // BUG-NET-03: after the origin moves, tell the terrain renderer which region we're in so
        // its void-water plane sits at this region's water height (order matters -- it reads the
        // origin we just set).
        _terrainRenderer?.SetPrimaryRegion(handle);
    }

    /// <summary>FEAT-AVATAR-01: manual avatar rebake — World menu entry and Ctrl+Alt+R, the same
    /// shortcut the real viewer uses. Recomposites the bakes from the worn set and re-sends the
    /// corrected appearance; the escape hatch when a wearable change did not visibly take.</summary>
    private void RebakeAvatar()
    {
        if (_session == null) return;
        // FEAT-AVATAR-01: what this does depends on SLNG_BAKE_UPLOAD / SLNG_BAKE_SEND, so the
        // outcome is reported by the bake itself once it finishes. Announcing "nothing is sent" up
        // front was wrong from the moment sending started working, and a stale reassurance about a
        // write to the user's account is the worst kind to leave standing.
        _session.RebakeAvatar();
        _chatWindow?.AppendLocalChatMessage("System", "Avatar wird neu gebacken …");

        _ = BakeAvatarAndReportAsync();
    }

    // FEAT-AVATAR-01: bakes the generated test pattern on top of every channel instead of an
    // inventory item. The generated skin could not answer whether the bake works -- as a Skin it
    // sits under the worn tattoo layers, two of which are opaque -- and this route depends on no
    // inventory item, no COF link and no layer ordering.
    private void BakeTestPattern()
    {
        if (_session == null) return;
        _chatWindow?.AppendLocalChatMessage("System", "Testmuster wird gebacken …");
        _ = BakeAvatarAndReportAsync(testPattern: true);
    }

    private async System.Threading.Tasks.Task BakeAvatarAndReportAsync(bool testPattern = false)
    {
        if (_session == null) return;

        string result;
        try { result = await _session.BakeAvatarAsync(testPattern).ConfigureAwait(false); }
        catch (System.Exception ex) { result = $"Bake fehlgeschlagen: {ex.Message}"; }

        CallDeferred(nameof(NotifyBakeResult), result);
    }

    private void NotifyBakeResult(string result)
        => _chatWindow?.AppendLocalChatMessage("System", result);

    // FEAT-AVATAR-01: a generated skin with one known colour per bake channel, so "did the right
    // layer reach the right channel, the right way up" is answerable by looking.
    private void CreateTestSkin()
    {
        if (_session == null) return;
        _chatWindow?.AppendLocalChatMessage("System", "Testhaut wird erzeugt und hochgeladen …");
        _ = CreateTestSkinAndReportAsync();
    }

    private async System.Threading.Tasks.Task CreateTestSkinAndReportAsync()
    {
        if (_session == null) return;

        string result;
        try { result = await _session.CreateTestSkinAsync().ConfigureAwait(false); }
        catch (System.Exception ex) { result = $"Testhaut fehlgeschlagen: {ex.Message}"; }

        CallDeferred(nameof(NotifyTestSkinCreated), result);
    }

    // The item exists on the grid the moment the create call returns, but an already-expanded
    // folder in the tree is showing a cached listing -- so being told "it is in Body Parts" while
    // Body Parts visibly does not contain it is worse than not being told at all.
    private void NotifyTestSkinCreated(string result)
    {
        NotifyBakeResult(result);

        if (_session?.BodyPartsFolderId is { } bodyParts) _inventoryPanel?.RefreshFolder(bodyParts);
        if (_session?.TexturesFolderId is { } textures) _inventoryPanel?.RefreshFolder(textures);
    }

    // FEAT-AVATAR-01: deferred target for GridSession.WearableEditUnavailable.
    private void NotifyWearableEditUnavailable(string reason)
        => _chatWindow?.AppendLocalChatMessage("System",
            $"'{reason}' konnte nicht geändert werden — die Änderung wurde verworfen.");

    private int _logLineCount;


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
