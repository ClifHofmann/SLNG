using System.Linq;
using Godot;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using SLNG.Net;

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
    private Vector3 _panOffset = Vector3.Zero;

    // Public API for CameraHUD
    public void RotateCamera(Vector2 delta)
    {
        _orbitYaw -= delta.X;
        _orbitPitch -= delta.Y;
        _orbitPitch = Mathf.Clamp(_orbitPitch, -1.5f, 1.5f);
    }

    public void PanCamera(Vector2 delta)
    {
        _panOffset += new Vector3(delta.X, delta.Y, 0);
    }

    public void ZoomCamera(float delta)
    {
        _zoom += delta;
        _zoom = Mathf.Clamp(_zoom, 0.5f, 50.0f);
    }

    /// <summary>Zooms by <paramref name="zoomDelta"/> while keeping whatever is currently under
    /// the mouse cursor visually anchored on screen, instead of always re-centring on the avatar.
    /// Approximates a raycast-based "zoom to point" without needing a physics query: projects the
    /// cursor's screen offset onto the world-space plane at the pivot's current depth (using this
    /// camera's own FOV/aspect — the same perspective math the renderer already applies) and shifts
    /// _panOffset toward that point in proportion to how much closer the zoom just got. At
    /// zoom-ratio 1 (no change) this is a no-op; it converges fully on the cursor's point as
    /// zoom approaches 0, same as the classic "zoom to mouse position" editor convention.</summary>
    private void ZoomTowardCursor(float zoomDelta, Vector2 mousePos)
    {
        float oldZoom = _zoom;
        _zoom = Mathf.Clamp(_zoom + zoomDelta, 0.5f, 50.0f);
        if (Mathf.IsEqualApprox(_zoom, oldZoom)) return;

        var vpSize = GetViewport().GetVisibleRect().Size;
        if (vpSize.X <= 0 || vpSize.Y <= 0) return;

        // Cursor offset from screen centre, normalized to [-1, 1] per axis. Screen Y grows down;
        // flip it so a cursor ABOVE centre maps to a positive camera-local Up offset.
        var ndc = new Vector2(
            (mousePos.X / vpSize.X) * 2f - 1f,
            -((mousePos.Y / vpSize.Y) * 2f - 1f));

        // World-space half-extent of the view frustum at the pivot's depth (oldZoom back from the
        // avatar). Fov is vertical (Camera3D's default KeepHeight aspect mode).
        float halfHeight = oldZoom * Mathf.Tan(Mathf.DegToRad(Fov) / 2f);
        float halfWidth = halfHeight * (vpSize.X / vpSize.Y);

        // The point under the cursor, in the pivot plane, as a camera-local X/Y offset — the same
        // units _panOffset already uses (Transform.Basis.X/.Y are unit vectors).
        var cursorOffset = new Vector2(ndc.X * halfWidth, ndc.Y * halfHeight);

        float ratio = _zoom / oldZoom;
        _panOffset.X += cursorOffset.X * (1f - ratio);
        _panOffset.Y += cursorOffset.Y * (1f - ratio);
    }
    public void ResetCamera()
    {
        _orbitYaw = 0f;
        _orbitPitch = 0f;
        _panOffset = Vector3.Zero;
        _zoom = 4.0f;
    }

    public void SetPresetView(string preset)
    {
        ResetCamera();
        switch (preset.ToLower())
        {
            case "front":
                _orbitYaw = Mathf.Pi; // 180 degrees
                break;
            case "side":
                _orbitYaw = Mathf.Pi / 2.0f; // 90 degrees
                break;
            case "rear":
            default:
                _orbitYaw = 0f;
                break;
        }
    }

    // Alt+LMB orbit state. The orbit offsets rotate the CAMERA around the avatar
    // without changing the avatar's facing (_yaw/_pitch). They snap back to 0 when
    // the avatar moves, so the camera returns behind the avatar — SL-style.
    private bool _altOrbitActive = false;
    private float _orbitYaw = 0f;
    private float _orbitPitch = 0f;

    // The cursor's viewport position at the moment Alt+LMB was pressed, captured BEFORE
    // Input.MouseMode switches to Captured. Captured mode hides and re-centres the cursor, so
    // event.Position during the drag itself no longer reflects where the user actually clicked —
    // reading it live made ZoomTowardCursor always converge on screen centre instead. Anchoring
    // once at press-time and reusing it for the whole drag is what actually zooms toward the
    // point the user aimed at.
    private Vector2 _altZoomAnchorPos = Vector2.Zero;

    // Fly mode: Home toggles; pressing E (up) also engages it. While flying, gravity is
    // suspended and E/C move vertically. Landing on the ground leaves fly mode.
    private bool _flying = false;

    public void Initialize(World world, GridSession session)
    {
        _world = world;
        _session = session;
    }

    private PopupMenu _contextMenu = null!;
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
        _contextMenu.AddItem("Dump Object Data", 3);
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
        else if (id == 3)
        {
            if (System.Guid.TryParse(_lastClickedEntityId, out var guid))
            {
                var entity = _world?.GetEntity(guid);
                if (entity != null)
                {
                    var prim = entity.GetComponent<SLNG.Core.Components.PrimitiveComponent>();
                    var transform = entity.GetComponent<SLNG.Core.Components.TransformComponent>();
                    GD.Print($"\n=== [Dump Object Data] ===");
                    GD.Print($"EntityId: {entity.Id}");
                    if (transform != null)
                    {
                        GD.Print($"Position: {transform.Position}");
                        GD.Print($"Rotation: {transform.Rotation}");
                    }
                    if (prim != null)
                    {
                        GD.Print($"Scale: {prim.Scale}");
                        GD.Print($"TextureId: {prim.TextureId}");
                        GD.Print($"MaterialId: {prim.RenderMaterialId}");
                        GD.Print($"IsSculpt: {prim.IsSculpt}");
                        GD.Print($"SculptId: {prim.SculptId}");
                        GD.Print($"SculptType: {prim.SculptType}");
                        GD.Print($"Shape: {prim.Shape}");

                    }
                    GD.Print($"==========================\n");
                }
            }
        }
        else if (id == 2)
        {
            GD.Print($"[Touch] Triggering touch on object {_lastClickedLocalId} (Not fully implemented yet)");
            // _session.TouchObject(_lastClickedLocalId);
        }
    }

    // _UnhandledInput, not _Input: Control nodes (the inventory Tree, LineEdits, etc.) stop
    // mouse/keyboard events from reaching this method once they've consumed them, whereas
    // _Input fires unconditionally — that's why scrolling the inventory window used to also
    // zoom the world camera (Tree's own scroll handling never got a chance to be "the" consumer).
    public override void _UnhandledInput(InputEvent @event)
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
                    _altZoomAnchorPos = mouseBtn.Position;
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
                ZoomTowardCursor(-0.5f, mouseBtn.Position);
                GD.Print($"[AvatarController] Zoom: {_zoom:F1} (WheelUp)");
            }
            else if (mouseBtn.ButtonIndex == MouseButton.WheelDown)
            {
                ZoomTowardCursor(0.5f, mouseBtn.Position);
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
                ZoomTowardCursor(mouseMotion.Relative.Y * sensitivity * 50.0f, _altZoomAnchorPos);
            }
            else
            {
                // RMB free-look: turns the avatar with the camera.
                _yaw -= mouseMotion.Relative.X * sensitivity;
                _pitch -= mouseMotion.Relative.Y * sensitivity;
                _pitch = Mathf.Clamp(_pitch, -1.5f, 1.5f);
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

                // Physics-based floor detection. We cast a ray straight down from above the avatar
                // to find the highest floor point (terrain or object) on Layer 1.
                var spaceState = GetWorld3D().DirectSpaceState;
                var godotPos = RenderConfig.ToGodot(localAgent.RegionHandle, transform.Position);
                
                // Cast from 2 meters above the avatar's feet, down to 100 meters below
                var rayFrom = godotPos + new Godot.Vector3(0, 2.0f, 0);
                var rayTo = godotPos - new Godot.Vector3(0, 100.0f, 0);
                
                var query = PhysicsRayQueryParameters3D.Create(rayFrom, rayTo);
                query.CollisionMask = 1; // Only hit Layer 1 (terrain/objects), ignore Layer 2 (avatar)
                
                var result = spaceState.IntersectRay(query);
                
                float groundHeight = 0;
                bool hasGround = false;
                
                if (result.Count > 0)
                {
                    groundHeight = result["position"].AsVector3().Y;
                    hasGround = true;
                }
                else
                {
                    // Fallback to terrain heightmap if raycast misses
                    int rawX = (int)transform.Position.X;
                    int rawY = (int)transform.Position.Y;
                    if (_world.Terrains.TryGetValue(localAgent.RegionHandle, out var terrain)
                        && rawX >= 0 && rawX < terrain.Width && rawY >= 0 && rawY < terrain.Height)
                    {
                        groundHeight = terrain.GetHeights()[rawY * terrain.Width + rawX];
                        hasGround = true;
                    }
                }

                if (hasGround)
                {
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
                        // Push up out of terrain/object
                        transform.Position = new System.Numerics.Vector3(transform.Position.X, transform.Position.Y, groundHeight);
                    }
                    else if (transform.Position.Z > groundHeight)
                    {
                        // Fall down to terrain/object
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

                // Apply pan offset relative to camera's orientation
                targetPos += Transform.Basis.X * _panOffset.X;
                targetPos += Transform.Basis.Y * _panOffset.Y;

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
