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

    // Fly mode: Home toggles; pressing E (up) also engages it. While flying, gravity is
    // suspended and E/C move vertically. Landing on the ground leaves fly mode.
    private bool _flying = false;

    public void Initialize(World world, GridSession session)
    {
        _world = world;
        _session = session;
    }

        private PopupMenu _contextMenu;
        private string _lastClickedEntityId = "";
        private string _lastClickedLocalId = "";

        public override void _Ready()
        {
            // Default to visible mouse for UI interaction
            Input.MouseMode = Input.MouseModeEnum.Visible;

            _contextMenu = new PopupMenu();
            _contextMenu.Name = "ContextMenu";
            _contextMenu.AddItem("Inspect (Print IDs to Console)", 0);
            _contextMenu.AddItem("Copy Entity ID", 1);
            _contextMenu.AddItem("Touch / Interact", 2);
            _contextMenu.IdPressed += OnContextMenuIdPressed;
            AddChild(_contextMenu);
        }

        private void OnContextMenuIdPressed(long id)
        {
            if (id == 0)
            {
                GD.Print($"\n=== [Inspect Object] ===\nEntityId: {_lastClickedEntityId}\nLocalId: {_lastClickedLocalId}\n========================\n");
            }
            else if (id == 1)
            {
                DisplayServer.ClipboardSet(_lastClickedEntityId);
                GD.Print($"Copied {_lastClickedEntityId} to clipboard!");
            }
            else if (id == 2)
            {
                GD.Print($"[Touch] Triggering touch on object {_lastClickedLocalId} (Not fully implemented yet)");
                // _session.TouchObject(_lastClickedLocalId);
            }
        }

        public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey keyEvt && keyEvt.Pressed && !keyEvt.Echo)
        {
            if (keyEvt.Keycode == Key.Home)
            {
                _flying = !_flying;
                GD.Print($"[AvatarController] Flying: {(_flying ? "ON" : "off")}");
            }
            else if (keyEvt.Keycode == Key.Escape)
            {
                _zoom = 4.0f;
                _orbitYaw = 0f;
                _orbitPitch = 0f;
                GD.Print("[AvatarController] Camera reset");
            }
        }

        if (@event is InputEventMouseButton mouseBtn)
        {
            if (mouseBtn.ButtonIndex == MouseButton.Right)
            {
                if (mouseBtn.Pressed)
                {
                    // Raycast to identify clicked object
                    var spaceState = GetWorld3D().DirectSpaceState;
                    var mpos = mouseBtn.Position;
                    var from = ProjectRayOrigin(mpos);
                    var to = from + ProjectRayNormal(mpos) * 1000f;
                    
                    var query = PhysicsRayQueryParameters3D.Create(from, to);
                    var result = spaceState.IntersectRay(query);
                    
                    if (result.Count > 0)
                    {
                        var collider = result["collider"].AsGodotObject();
                        if (collider is Node colliderNode)
                        {
                            _lastClickedEntityId = colliderNode.HasMeta("EntityId") ? colliderNode.GetMeta("EntityId").AsString() : "None";
                            _lastClickedLocalId = colliderNode.HasMeta("LocalId") ? colliderNode.GetMeta("LocalId").AsString() : "None";
                            
                            // Uncapture mouse if we were orbiting
                            Input.MouseMode = Input.MouseModeEnum.Visible;
                            
                            // Show popup menu at mouse position
                            _contextMenu.Position = new Vector2I((int)mpos.X, (int)mpos.Y);
                            _contextMenu.Popup();
                        }
                    }
                }
            }
            else if (mouseBtn.ButtonIndex == MouseButton.Left)
            {
                // Alt+LMB: orbit around avatar (SL-style).
                // Use the event's AltPressed flag — Input.IsKeyPressed(Key.Alt) is
                // unreliable inside _UnhandledInput on some platforms.
                if (mouseBtn.Pressed && (mouseBtn.AltPressed || Input.IsKeyPressed(Key.Alt)))
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
                GD.Print($"[AvatarController] Zoom: {_zoom:F1} (WheelUp)");
            }
            else if (mouseBtn.ButtonIndex == MouseButton.WheelDown)
            {
                _zoom = Mathf.Min(50.0f, _zoom + 0.5f);
                GD.Print($"[AvatarController] Zoom: {_zoom:F1} (WheelDown)");
            }
        }

        if (@event is InputEventMouseMotion mouseMotion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            float sensitivity = 0.003f;
            if (_altOrbitActive)
            {
                // Stays active until the mouse button is released (handled on button-up).
                // Horizontal = orbit yaw, vertical = zoom (SL standard)
                _orbitYaw -= mouseMotion.Relative.X * sensitivity;
                _zoom += mouseMotion.Relative.Y * 0.05f;
                _zoom = Mathf.Max(0.5f, _zoom);
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

        var focusOwner = GetViewport().GuiGetFocusOwner();
        bool hasUiFocus = focusOwner is LineEdit || focusOwner is TextEdit;

        // 1. Follow the Avatar
        var localAgent = _world.GetAllEntities()
            .FirstOrDefault(e => e.GetComponent<AvatarComponent>()?.IsLocalAgent == true);

        if (localAgent != null)
        {
            var transform = localAgent.GetComponent<TransformComponent>();
            if (transform != null)
            {
                // Local movement prediction
                bool isFwd = (Input.IsActionPressed("ui_up") || Input.IsKeyPressed(Key.W)) && !hasUiFocus;
                bool isBack = (Input.IsActionPressed("ui_down") || Input.IsKeyPressed(Key.S)) && !hasUiFocus;
                bool isLeft = (Input.IsActionPressed("ui_left") || Input.IsKeyPressed(Key.A)) && !hasUiFocus;
                bool isRight = (Input.IsActionPressed("ui_right") || Input.IsKeyPressed(Key.D)) && !hasUiFocus;
                bool isUp = (Input.IsKeyPressed(Key.E) || Input.IsActionPressed("ui_page_up")) && !hasUiFocus;
                bool isDown = (Input.IsKeyPressed(Key.Q) || Input.IsKeyPressed(Key.C) || Input.IsActionPressed("ui_page_down")) && !hasUiFocus;

                // Pressing up engages fly automatically (matches the "E = go up" instinct);
                // Home toggles it off. See _Input.
                if (isUp && !_flying) _flying = true;

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
                    float speed = _flying ? 12.0f : 4.0f; // fly faster than walk
                    float slDx = godotMoveDir.X * speed * (float)delta; // Godot Right (+X) is SL East (+X)
                    float slDy = -godotMoveDir.Z * speed * (float)delta; // Godot Forward (-Z) is SL North (+Y)
                    float slDz = godotMoveDir.Y * speed * (float)delta;

                    transform.Position += new System.Numerics.Vector3(slDx, slDy, slDz);
                }

                // Vertical movement while flying (E up / C down).
                if (_flying && (isUp || isDown))
                {
                    float vspeed = 8.0f;
                    float dz = (isUp ? vspeed : 0f) - (isDown ? vspeed : 0f);
                    transform.Position = new System.Numerics.Vector3(
                        transform.Position.X, transform.Position.Y, transform.Position.Z + dz * (float)delta);
                }

                // Terrain floor. Walking snaps to the ground and applies gravity; flying just
                // refuses to sink below the ground (so it holds altitude instead of falling).
                // Only when we actually have heightmap data under the avatar: on a varregion
                // (coords > our 256 heightmap) there is no local ground, so applying gravity
                // would yank the avatar to a clamped corner height — that was the "falling on
                // landing". Outside the map we leave Z to the server / fly controls.
                int rawX = (int)transform.Position.X;
                int rawY = (int)transform.Position.Y;
                if (_world.Terrains.TryGetValue(localAgent.RegionHandle, out var terrain)
                    && rawX >= 0 && rawX < terrain.Width && rawY >= 0 && rawY < terrain.Height)
                {
                    int tx = rawX;
                    int ty = rawY;
                    float groundHeight = terrain.GetHeights()[ty * terrain.Width + tx];

                    if (_flying)
                    {
                        if (transform.Position.Z < groundHeight)
                        {
                            // Touched down — land and leave fly mode.
                            transform.Position = new System.Numerics.Vector3(transform.Position.X, transform.Position.Y, groundHeight);
                            _flying = false;
                        }
                    }
                    else if (transform.Position.Z < groundHeight)
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

                // Keyboard zoom polling (+ and - keys)
                if (Input.IsKeyPressed(Key.Equal) || Input.IsKeyPressed(Key.KpAdd))
                {
                    _zoom = Mathf.Max(0.5f, _zoom - 15.0f * (float)delta);
                }
                if (Input.IsKeyPressed(Key.Minus) || Input.IsKeyPressed(Key.KpSubtract))
                {
                    _zoom = Mathf.Min(50.0f, _zoom + 15.0f * (float)delta);
                }

                // Floating-origin-relative world position (see RenderConfig), plus eye height.
                var targetPos = RenderConfig.ToGodot(localAgent.RegionHandle, transform.Position);
                targetPos.Y += 1.8f;

                // Third-person camera: pull back along the camera's Z axis
                Position = targetPos + Transform.Basis.Z * _zoom;
            }
        }

        // 2. Handle Movement Input
        bool fwd = (Input.IsActionPressed("ui_up") || Input.IsKeyPressed(Key.W)) && !hasUiFocus;
        bool back = (Input.IsActionPressed("ui_down") || Input.IsKeyPressed(Key.S)) && !hasUiFocus;
        bool left = (Input.IsActionPressed("ui_left") || Input.IsKeyPressed(Key.A)) && !hasUiFocus;
        bool right = (Input.IsActionPressed("ui_right") || Input.IsKeyPressed(Key.D)) && !hasUiFocus;
        bool up = (Input.IsKeyPressed(Key.E) || Input.IsActionPressed("ui_page_up")) && !hasUiFocus;
        bool down = (Input.IsKeyPressed(Key.Q) || Input.IsKeyPressed(Key.C) || Input.IsActionPressed("ui_page_down")) && !hasUiFocus;

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
            _session.SetMovement(fwd, back, false, false, up, down, finalQuat, _flying);
        }
    }
}
