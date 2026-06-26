using Godot;
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

    private GridSession? _session;
    private SLNG.Core.ECS.World? _world;
    private SLNG.Core.WorldSimulation? _worldSimulation;
    private TerrainRenderer? _terrainRenderer;

    public override void _Ready()
    {
        _gridInput = GetNode<LineEdit>("VBoxContainer/HBoxContainer/GridInput");
        _firstInput = GetNode<LineEdit>("VBoxContainer/HBoxContainer/FirstInput");
        _lastInput = GetNode<LineEdit>("VBoxContainer/HBoxContainer/LastInput");
        _passInput = GetNode<LineEdit>("VBoxContainer/HBoxContainer/PassInput");
        _loginButton = GetNode<Button>("VBoxContainer/HBoxContainer/LoginButton");
        _logPanel = GetNode<RichTextLabel>("VBoxContainer/LogPanel");

        _loginButton.Pressed += OnLoginPressed;

        _terrainRenderer = new TerrainRenderer();
        AddChild(_terrainRenderer);
        
        LogMessage("Ready. Enter credentials and click Login.");
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

        _terrainRenderer?.Initialize(_world);

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
        }
        else
        {
            LogMessage($"[color=red]Login FAILED[/color]: {result.Message}");
            _loginButton.Disabled = false;
        }
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
