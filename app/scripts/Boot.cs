using Godot;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Net;
using SLNG.App;
using System.Linq;

public partial class Boot : Control
{
    private OptionButton _profileDropdown = null!;
    private LineEdit _gridInput = null!;
    private LineEdit _firstInput = null!;
    private LineEdit _lastInput = null!;
    private LineEdit _passInput = null!;
    private CheckBox _saveLoginCheck = null!;
    private Button _loginButton = null!;
    private RichTextLabel _logPanel = null!;

    private ConfigFile _loginsConfig = new ConfigFile();
    private Godot.Collections.Array<string> _savedProfiles = new();
    
    private LineEdit _chatInput = null!;
    private Button _chatSendButton = null!;

    private GridSession? _session;
    private SLNG.Core.ECS.World? _world;
    private SLNG.Core.WorldSimulation? _worldSimulation;
    private TerrainRenderer? _terrainRenderer;
    private ObjectRenderer? _objectRenderer;
    private AvatarRenderer? _avatarRenderer;
    private SLNG.Assets.AssetService? _assetService;
    private AvatarController? _avatarController;
    private VBoxContainer _vboxContainer = null!;
    
    private WorldEnvironment? _worldEnvironment;
    private bool _postFxEnabled = true;

    private Label _hudLabel = null!;
    private double _hudAccum;

    public override void _Ready()
    {
        _vboxContainer = GetNode<VBoxContainer>("VBoxContainer");
        _profileDropdown = GetNode<OptionButton>("VBoxContainer/HBoxContainer/ProfileDropdown");
        _gridInput = GetNode<LineEdit>("VBoxContainer/HBoxContainer/GridInput");
        _firstInput = GetNode<LineEdit>("VBoxContainer/HBoxContainer/FirstInput");
        _lastInput = GetNode<LineEdit>("VBoxContainer/HBoxContainer/LastInput");
        _passInput = GetNode<LineEdit>("VBoxContainer/HBoxContainer/PassInput");
        _saveLoginCheck = GetNode<CheckBox>("VBoxContainer/HBoxContainer/SaveLoginCheck");
        _loginButton = GetNode<Button>("VBoxContainer/HBoxContainer/LoginButton");
        _logPanel = GetNode<RichTextLabel>("VBoxContainer/LogPanel");
        
        _chatInput = GetNode<LineEdit>("VBoxContainer/ChatBox/ChatInput");
        _chatSendButton = GetNode<Button>("VBoxContainer/ChatBox/ChatSendButton");

        _loginButton.Pressed += OnLoginPressed;
        _chatSendButton.Pressed += OnChatSend;
        _chatInput.TextSubmitted += (text) => OnChatSend();
        _profileDropdown.ItemSelected += OnProfileSelected;

        LoadProfiles();

        _terrainRenderer = new TerrainRenderer();
        AddChild(_terrainRenderer);

        _objectRenderer = new ObjectRenderer();
        AddChild(_objectRenderer);
        
        _avatarRenderer = new AvatarRenderer();
        AddChild(_avatarRenderer);
        
        SetupEnvironment();
        SetupHud();

        LogMessage("Ready. Enter credentials and click Login.");
    }

    private void SetupHud()
    {
        // Position/altitude readout in the top-right corner, overlaying the 3D view.
        // On its own CanvasLayer so it always draws on top of the world and the login/chat
        // Controls, regardless of scene-tree order.
        var hudLayer = new CanvasLayer { Name = "HudLayer", Layer = 10 };
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
            RotationDegrees = new Godot.Vector3(-50f, -130f, 0f),
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
            DirectionalShadowBlendSplits = true,
            ShadowBias = 0.02f,
            ShadowNormalBias = 1.0f,
            ShadowOpacity = 0.9f,
        };
        AddChild(sun);
    }

    public override void _Process(double delta)
    {
        // Drain queued world events on the main thread — the only place the world mutates.
        _worldSimulation?.Pump();

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
                _profileDropdown.AddItem(profile);
                _savedProfiles.Add(profile);
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

        _world = new SLNG.Core.ECS.World();
        _session = new GridSession();
        _worldSimulation = new SLNG.Core.WorldSimulation(_world, _session);

        string cacheDir = ProjectSettings.GlobalizePath("user://cache/assets");
        _assetService = new SLNG.Assets.AssetService(_session, cacheDir);

        // GPU budget shared by meshes and textures. Sized for the nearby working set on a
        // 12 GB card with headroom for post-FX; out-of-range content is released so the LRU
        // can reclaim under this cap.
        var gpuCache = new GpuCache(1536L * 1024 * 1024);

        _terrainRenderer?.Initialize(_world, _assetService, gpuCache);
        _objectRenderer?.Initialize(_world, _assetService, gpuCache);
        _avatarRenderer?.Initialize(_world, _assetService, gpuCache);

        _session.ChatMessageReceived += OnChatMessage;

        var creds = new LoginCredentials
        {
            GridLoginUri = _gridInput.Text,
            FirstName = _firstInput.Text,
            LastName = _lastInput.Text,
            Password = _passInput.Text
        };

        var result = await _session.LoginAsync(creds);

        if (result.Success)
        {
            if (_saveLoginCheck.ButtonPressed)
            {
                string profileName = $"{creds.FirstName} {creds.LastName} @ {creds.GridLoginUri}";
                _loginsConfig.SetValue(profileName, "grid", creds.GridLoginUri);
                _loginsConfig.SetValue(profileName, "first", creds.FirstName);
                _loginsConfig.SetValue(profileName, "last", creds.LastName);
                _loginsConfig.SetValue(profileName, "pass", creds.Password);
                _loginsConfig.Save("user://logins.cfg");
            }

            LogMessage($"[color=green]Login SUCCESS[/color] - AgentID: {result.AgentId}");
            if (!string.IsNullOrEmpty(result.Message))
            {
                LogMessage(result.Message);
            }
            
            // Hide the login form but keep chat/logs visible
            GetNode<HBoxContainer>("VBoxContainer/HBoxContainer").Visible = false;

            // Spawn the avatar controller (camera)
            _avatarController = new AvatarController();
            _avatarController.Name = "AvatarController";
            _avatarController.Initialize(_world, _session);
            
            // Set the floating origin to this region so everything renders near 0 (OSGrid
            // global coordinates are in the millions and overflow float precision otherwise).
            ulong regionHandle = _session.CurrentRegionHandle;
            RenderConfig.SetRegionOrigin(regionHandle);

            // Start near the region centre at a reasonable height (before AvatarUpdate arrives).
            _avatarController.Position = RenderConfig.ToGodot(regionHandle, new System.Numerics.Vector3(128f, 128f, 50f));
            
            // Assign the environment directly to the camera to ensure the sky renders
            var worldEnv = GetNodeOrNull<WorldEnvironment>("WorldEnvironment");
            if (worldEnv != null)
            {
                _avatarController.Environment = worldEnv.Environment;
            }

            AddChild(_avatarController);
            _avatarController.MakeCurrent();

            LogMessage($"[System] Login succeeded! Agent: {result.AgentId}");
            _chatInput.Editable = true;
            _chatSendButton.Disabled = false;
        }
        else
        {
            LogMessage($"[System] Login failed: {result.Message}");
            _loginButton.Disabled = false;
        }
    }

    private void OnChatSend()
    {
        var text = _chatInput.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        
        _session?.SendChat(text);
        _chatInput.Text = "";
    }

    private void OnChatMessage(object? sender, ChatMessageEvent e)
    {
        CallDeferred(nameof(LogMessage), $"[CHAT] {e.FromName}: {e.Message}");
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
        _worldSimulation?.Dispose();
        _session?.Dispose();
    }
}
