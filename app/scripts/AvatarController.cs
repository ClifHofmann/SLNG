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
    private float _zoom = 4.0f;

    // Alt+LMB orbit state. The orbit offsets rotate the CAMERA around the avatar
    // without changing the avatar's facing (_yaw/_pitch). They snap back to 0 when
    // the avatar moves, so the camera returns behind the avatar — SL-style.
    private bool _altOrbitActive = false;
    private float _orbitYaw = 0f;
    private float _orbitPitch = 0f;

    public void Initialize(World world, GridSession session)
    {
        _world = world;
        _session = session;
    }

    public override void _Ready()
    {
        // Default to visible mouse for UI interaction
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseBtn)
        {
            if (mouseBtn.ButtonIndex == MouseButton.Right)
            {
                // RMB: orbit (existing behaviour)
                if (mouseBtn.Pressed)
                    Input.MouseMode = Input.MouseModeEnum.Captured;
                else if (!_altOrbitActive)
                    Input.MouseMode = Input.MouseModeEnum.Visible;
            }
            else if (mouseBtn.ButtonIndex == MouseButton.Left)
            {
                // Alt+LMB: orbit around avatar (SL-style).
                // Use the event's AltPressed flag — Input.IsKeyPressed(Key.Alt) is
                // unreliable inside _UnhandledInput on some platforms.
                if (mouseBtn.Pressed && mouseBtn.AltPressed)
                {
                    _altOrbitActive = true;
                    Input.MouseMode = Input.MouseModeEnum.Captured;
                }
                else if (!mouseBtn.Pressed && _altOrbitActive)
                {
                    _altOrbitActive = false;
                    Input.MouseMode = Input.MouseModeEnum.Visible;
                }
            }
            else if (mouseBtn.ButtonIndex == MouseButton.WheelUp)
            {
                _zoom = Mathf.Max(0.5f, _zoom - 0.5f);
            }
            else if (mouseBtn.ButtonIndex == MouseButton.WheelDown)
            {
                _zoom = Mathf.Min(20.0f, _zoom + 0.5f);
            }
        }

        if (@event is InputEventMouseMotion mouseMotion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            float sensitivity = 0.003f;
            if (_altOrbitActive)
            {
                // Exit alt-orbit if Alt is no longer held during motion
                if (!mouseMotion.AltPressed)
                {
                    _altOrbitActive = false;
                    Input.MouseMode = Input.MouseModeEnum.Visible;
                    return;
                }
                // Orbit the camera only — do NOT touch _yaw/_pitch so the avatar
                // keeps facing where it was.
                _orbitYaw   -= mouseMotion.Relative.X * sensitivity;
                _orbitPitch -= mouseMotion.Relative.Y * sensitivity;
                _orbitPitch  = Mathf.Clamp(_orbitPitch, -1.4f, 1.4f);
            }
            else
            {
                // RMB free-look: turns the avatar with the camera.
                _yaw   -= mouseMotion.Relative.X * sensitivity;
                _pitch -= mouseMotion.Relative.Y * sensitivity;
                _pitch  = Mathf.Clamp(_pitch, -1.5f, 1.5f);
            }
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

                // In SL/Firestorm, A and D turn the avatar when not strafing
                if (isLeft) _yaw += 2.5f * (float)delta;
                if (isRight) _yaw -= 2.5f * (float)delta;

                // Any movement/turn snaps the orbit camera back behind the avatar.
                if (isFwd || isBack || isLeft || isRight)
                {
                    _orbitYaw = 0f;
                    _orbitPitch = 0f;
                }

                // Camera rotation = avatar facing (_yaw/_pitch) plus the orbit offset.
                // The orbit offset moves the camera around the avatar without turning it.
                Rotation = new Vector3(_pitch + _orbitPitch, _yaw + _orbitYaw, 0);

                var godotMoveDir = new Vector3();
                if (isFwd) godotMoveDir += -Transform.Basis.Z;
                if (isBack) godotMoveDir += Transform.Basis.Z;

                godotMoveDir.Y = 0; // Constrain to Godot's ground plane
                godotMoveDir = godotMoveDir.Normalized();

                if (godotMoveDir.LengthSquared() > 0)
                {
                    float speed = 4.0f; // SL walk speed is roughly 3-4 m/s
                    float slDx = godotMoveDir.X * speed * (float)delta; // Godot Right (+X) is SL East (+X)
                    float slDy = -godotMoveDir.Z * speed * (float)delta; // Godot Forward (-Z) is SL North (+Y)
                    float slDz = godotMoveDir.Y * speed * (float)delta;

                    transform.Position += new System.Numerics.Vector3(slDx, slDy, slDz);
                }

                // Terrain collision and gravity (apply regardless of input)
                if (_world.Terrains.TryGetValue(localAgent.RegionHandle, out var terrain))
                {
                    int tx = (int)Mathf.Clamp(transform.Position.X, 0, terrain.Width - 1);
                    int ty = (int)Mathf.Clamp(transform.Position.Y, 0, terrain.Height - 1);
                    float groundHeight = terrain.GetHeights()[ty * terrain.Width + tx];
                    
                    if (transform.Position.Z < groundHeight)
                    {
                        // Push up out of terrain
                        transform.Position = new System.Numerics.Vector3(transform.Position.X, transform.Position.Y, groundHeight);
                    }
                    else if (transform.Position.Z > groundHeight)
                    {
                        // Fall down to terrain
                        float fallSpeed = 9.81f * (float)delta;
                        transform.Position = new System.Numerics.Vector3(transform.Position.X, transform.Position.Y, System.Math.Max(groundHeight, transform.Position.Z - fallSpeed));
                    }
                }

                _world.NotifyComponentUpdated(localAgent, transform);

                uint regionX = (uint)(localAgent.RegionHandle >> 32);
                uint regionY = (uint)(localAgent.RegionHandle & 0xFFFFFFFF);

                // OpenSim/Godot coordinate mapping
                var targetPos = new Vector3(
                    regionX + transform.Position.X,
                    transform.Position.Z + 1.8f, // Eye height
                    -(regionY + transform.Position.Y)
                );

                // Third-person camera: pull back along the camera's Z axis
                Position = targetPos + Transform.Basis.Z * _zoom;
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

            // Avatar facing comes from _yaw ONLY — never the camera's full orientation.
            // Using the camera quaternion would fold the orbit offset and pitch into the
            // avatar's facing, making it spin/tilt while orbiting. A yaw-only quaternion
            // keeps the avatar upright and facing where the player aims.
            var godotQuat = Quaternion.FromEuler(new Vector3(0, _yaw, 0));
            var slQuat = new System.Numerics.Quaternion(godotQuat.X, -godotQuat.Z, godotQuat.Y, godotQuat.W);
            var offset = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, (float)System.Math.PI / 2.0f);
            var finalQuat = offset * slQuat;

            // Locally update the avatar's rotation so it visibly turns
            if (localAgent != null)
            {
                var transform = localAgent.GetComponent<TransformComponent>();
                if (transform != null)
                {
                    transform.Rotation = finalQuat;
                    _world.NotifyComponentUpdated(localAgent, transform);
                }
            }

            // We pass false for left/right because A/D are turning now, not strafing
            _session.SetMovement(fwd, back, false, false, up, down, finalQuat);
        }
    }
}
