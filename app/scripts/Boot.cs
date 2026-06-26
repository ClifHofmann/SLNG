using Godot;
using SLNG.Core;
using SLNG.Net;
using SLNG.App;

public partial class Boot : Control
{
    private LineEdit _gridInput = null!;
    private LineEdit _firstInput = null!;
    private LineEdit _lastInput = null!;
    private LineEdit _passInput = null!;
    private Button _loginButton = null!;
    private RichTextLabel _logPanel = null!;
    
    private LineEdit _chatInput = null!;
    private Button _chatSendButton = null!;

    private GridSession? _session;
    private SLNG.Core.ECS.World? _world;
    private SLNG.Core.WorldSimulation? _worldSimulation;
    private TerrainRenderer? _terrainRenderer;
    private ObjectRenderer? _objectRenderer;
    private AvatarRenderer? _avatarRenderer;
    private SLNG.Assets.AssetService? _assetService;
    private FreeCamera? _freeCamera;
    private VBoxContainer _vboxContainer = null!;
    
    private WorldEnvironment? _worldEnvironment;
    private bool _postFxEnabled = true;

    public override void _Ready()
    {
        _vboxContainer = GetNode<VBoxContainer>("VBoxContainer");
        _gridInput = GetNode<LineEdit>("VBoxContainer/HBoxContainer/GridInput");
        _firstInput = GetNode<LineEdit>("VBoxContainer/HBoxContainer/FirstInput");
        _lastInput = GetNode<LineEdit>("VBoxContainer/HBoxContainer/LastInput");
        _passInput = GetNode<LineEdit>("VBoxContainer/HBoxContainer/PassInput");
        _loginButton = GetNode<Button>("VBoxContainer/HBoxContainer/LoginButton");
        _logPanel = GetNode<RichTextLabel>("VBoxContainer/LogPanel");
        
        _chatInput = GetNode<LineEdit>("VBoxContainer/ChatBox/ChatInput");
        _chatSendButton = GetNode<Button>("VBoxContainer/ChatBox/ChatSendButton");

        _loginButton.Pressed += OnLoginPressed;
        _chatSendButton.Pressed += OnChatSend;
        _chatInput.TextSubmitted += (text) => OnChatSend();

        _terrainRenderer = new TerrainRenderer();
        AddChild(_terrainRenderer);

        _objectRenderer = new ObjectRenderer();
        AddChild(_objectRenderer);
        
        _avatarRenderer = new AvatarRenderer();
        AddChild(_avatarRenderer);
        
        SetupEnvironment();

        LogMessage("Ready. Enter credentials and click Login.");
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

        var gpuCache = new GpuCache();

        _terrainRenderer?.Initialize(_world);
        _objectRenderer?.Initialize(_world, _assetService, gpuCache);
        _avatarRenderer?.Initialize(_world);

        _session.ChatMessageReceived += OnChatMessage;
        _session.ObjectUpdateReceived += OnObjectUpdate;

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
            LogMessage($"[color=green]Login SUCCESS[/color] - AgentID: {result.AgentId}");
            if (!string.IsNullOrEmpty(result.Message))
            {
                LogMessage(result.Message);
            }
            
            // Hide the UI to show the 3D scene
            _vboxContainer.Visible = false;

            // Spawn the free camera
            _freeCamera = new FreeCamera();
            
            // Get current region global coordinates
            ulong regionHandle = _session.CurrentRegionHandle;
            uint regionX = (uint)(regionHandle >> 32);
            uint regionY = (uint)(regionHandle & 0xFFFFFFFF);

            // Start at a reasonable height in the middle of a 256x256 region
            _freeCamera.Position = new Godot.Vector3(regionX + 128f, 50f, -(regionY + 128f));
            
            // Assign the environment directly to the camera to ensure the sky renders
            var worldEnv = GetNodeOrNull<WorldEnvironment>("WorldEnvironment");
            if (worldEnv != null)
            {
                _freeCamera.Environment = worldEnv.Environment;
            }

            AddChild(_freeCamera);
            _freeCamera.MakeCurrent();

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

    private void OnObjectUpdate(object? sender, ObjectUpdateEvent e)
    {
        CallDeferred(nameof(LogMessage), $"[OBJECT] {e.LocalId} at {e.Position}");
    }

    private void LogMessage(string message)
    {
        _logPanel.AppendText(message + "\n");
    }

    public override void _ExitTree()
    {
        _worldSimulation?.Dispose();
        _session?.Dispose();
    }
}
