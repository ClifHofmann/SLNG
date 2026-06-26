using Godot;
using SLNG.Core.ECS;
using SLNG.Core.Components;
using SLNG.Net;
using System.Linq;

namespace SLNG.App;

public partial class AvatarController : Camera3D
{
    private World? _world;
    private GridSession? _session;
    private float _pitch = 0f;
    private float _yaw = 0f;
    private double _timeSinceLastUpdate = 0;

    // We store the last sent movement to avoid spamming the network
    private bool _lastFwd, _lastBack, _lastLeft, _lastRight, _lastUp, _lastDown;
    private Vector3 _lastCameraRot;

    public void Initialize(World world, GridSession session)
    {
        _world = world;
        _session = session;
    }

    public override void _Ready()
    {
        // Capture mouse so we can look around like a typical first-person/third-person game
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion mouseMotion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            // Mouse look
            float sensitivity = 0.003f;
            _yaw -= mouseMotion.Relative.X * sensitivity;
            _pitch -= mouseMotion.Relative.Y * sensitivity;

            // Clamp pitch to avoid flipping over
            _pitch = Mathf.Clamp(_pitch, -1.5f, 1.5f);

            Rotation = new Vector3(_pitch, _yaw, 0);
        }

        if (@event is InputEventKey keyEvent && keyEvent.Pressed && keyEvent.Keycode == Key.Escape)
        {
            // Free the mouse if user presses Escape
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
        else if (@event is InputEventMouseButton mouseBtn && mouseBtn.Pressed && mouseBtn.ButtonIndex == MouseButton.Left)
        {
            // Capture mouse if user clicks
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }
    }

    public override void _Process(double delta)
    {
        if (_world == null || _session == null) return;

        // 1. Follow the Avatar
        var localAgent = _world.GetAllEntities()
            .FirstOrDefault(e => e.GetComponent<AvatarComponent>()?.IsLocalAgent == true);

        if (localAgent != null)
        {
            var transform = localAgent.GetComponent<TransformComponent>();
            if (transform != null)
            {
                // Local movement prediction
                bool isFwd = Input.IsActionPressed("ui_up") || Input.IsKeyPressed(Key.W);
                bool isBack = Input.IsActionPressed("ui_down") || Input.IsKeyPressed(Key.S);
                bool isLeft = Input.IsActionPressed("ui_left") || Input.IsKeyPressed(Key.A);
                bool isRight = Input.IsActionPressed("ui_right") || Input.IsKeyPressed(Key.D);

                var godotMoveDir = new Vector3();
                if (isFwd) godotMoveDir += -Transform.Basis.Z;
                if (isBack) godotMoveDir += Transform.Basis.Z;
                if (isLeft) godotMoveDir += -Transform.Basis.X;
                if (isRight) godotMoveDir += Transform.Basis.X;

                godotMoveDir.Y = 0; // Constrain to Godot's ground plane
                godotMoveDir = godotMoveDir.Normalized();

                if (godotMoveDir.LengthSquared() > 0)
                {
                    float speed = 4.0f; // SL walk speed is roughly 3-4 m/s
                    float slDx = godotMoveDir.X * speed * (float)delta; // Godot Right (+X) is SL East (+X)
                    float slDy = -godotMoveDir.Z * speed * (float)delta; // Godot Forward (-Z) is SL North (+Y)
                    float slDz = godotMoveDir.Y * speed * (float)delta;

                    transform.Position += new System.Numerics.Vector3(slDx, slDy, slDz);
                    _world.NotifyComponentUpdated(localAgent, transform);
                }

                uint regionX = (uint)(localAgent.RegionHandle >> 32);
                uint regionY = (uint)(localAgent.RegionHandle & 0xFFFFFFFF);

                // OpenSim/Godot coordinate mapping
                var targetPos = new Vector3(
                    regionX + transform.Position.X,
                    transform.Position.Z + 1.8f, // Eye height
                    -(regionY + transform.Position.Y)
                );

                // Third-person camera: pull back along the camera's Z axis
                Position = targetPos + Transform.Basis.Z * 4.0f;
            }
        }

        // 2. Handle Movement Input
        bool fwd = Input.IsActionPressed("ui_up") || Input.IsKeyPressed(Key.W);
        bool back = Input.IsActionPressed("ui_down") || Input.IsKeyPressed(Key.S);
        bool left = Input.IsActionPressed("ui_left") || Input.IsKeyPressed(Key.A);
        bool right = Input.IsActionPressed("ui_right") || Input.IsKeyPressed(Key.D);
        bool up = Input.IsKeyPressed(Key.E) || Input.IsActionPressed("ui_page_up");
        bool down = Input.IsKeyPressed(Key.C) || Input.IsActionPressed("ui_page_down");

        var curRot = Rotation;
        
        _timeSinceLastUpdate += delta;

        // Send AgentUpdate at 10 Hz (every 0.1s)
        if (_timeSinceLastUpdate >= 0.1)
        {
            _timeSinceLastUpdate = 0;

            // The camera's Quaternion is Godot's space. We need SL space.
            // SL uses Z up, X forward, Y left. Godot uses Y up, -Z forward, X right.
            // Godot's default forward (-Z) maps to SL's Y axis (North).
            // But SL's default forward is the X axis (East).
            // So we must rotate the SL quaternion by +90 degrees around Z to align them.
            var godotQuat = Quaternion;
            var slQuat = new System.Numerics.Quaternion(godotQuat.X, -godotQuat.Z, godotQuat.Y, godotQuat.W);
            var offset = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, (float)System.Math.PI / 2.0f);
            var finalQuat = offset * slQuat;

            _session.SetMovement(fwd, back, left, right, up, down, finalQuat);
        }
    }
}
