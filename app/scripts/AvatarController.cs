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
    // Set by polling in _Process (Input.IsKeyPressed/IsMouseButtonPressed), NOT by this
    // button's own press/release events -- see the comment at that poll site for why.
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

    // Reference point for the manual position-polling "capture" in _Process -- see there for why
    // this replaces MouseMode.Captured's native relative-motion deltas (broken over RDP/VM).
    private Vector2 _orbitLastMousePos = Vector2.Zero;

    // Fly mode: Home toggles; pressing E (up) also engages it. While flying, gravity is
    // suspended and E/C move vertically. Landing on the ground leaves fly mode.
    private bool _flying = false;

    public void Initialize(World world, GridSession session)
    {
        _world = world;
        _session = session;
    }

    public override void _Ready()
    {
        Input.MouseMode = Input.MouseModeEnum.Visible;
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
            // Reaching _UnhandledInput at all already means no Control under the cursor claimed
            // this event (see the class comment above) -- but that's not enough on its own: the
            // cursor drifting a few pixels outside a window mid-scroll (e.g. reaching for the
            // wheel while it was still over the chat log) lands here too, and shouldn't zoom the
            // world just because a text field the user was actively using still holds focus. Same
            // hasUiFocus signal already gates WASD/orbit in _Process below.
            var focusOwner = GetViewport().GuiGetFocusOwner();
            bool hasUiFocus = focusOwner is LineEdit || focusOwner is TextEdit;

            if (!hasUiFocus && mouseBtn.ButtonIndex == MouseButton.WheelUp)
            {
                ZoomTowardCursor(-0.5f, mouseBtn.Position);
                GD.Print($"[AvatarController] Zoom: {_zoom:F1} (WheelUp)");
            }
            else if (!hasUiFocus && mouseBtn.ButtonIndex == MouseButton.WheelDown)
            {
                ZoomTowardCursor(0.5f, mouseBtn.Position);
                GD.Print($"[AvatarController] Zoom: {_zoom:F1} (WheelDown)");
            }
        }

        // Orbit rotation itself is handled by position-polling in _Process, not by
        // InputEventMouseMotion -- see the comment at that poll site for why (MouseMode.Captured's
        // native relative-motion deltas don't arrive at all over RDP/VM, confirmed live: zero
        // motion events during an entire Alt+LMB hold, then one big backlog jump on release).
    }

    public override void _Process(double delta)
    {
        if (_world == null || _session == null) return;

        var focusOwner = GetViewport().GuiGetFocusOwner();
        bool hasUiFocus = focusOwner is LineEdit || focusOwner is TextEdit;

        // Alt+LMB orbit engagement, polled every frame instead of driven by the button's own
        // discrete press/release events. Live-tested proof this was needed: holding Alt+LMB
        // produced a rapid, alternating pressed=True/False/True/False... event stream instead of
        // one press followed by a held state -- the OLD event-driven toggle treated every spurious
        // "release" as the real button-up and immediately cancelled orbit before a drag could ever
        // register, so the feature looked completely dead. Polling the actual current physical
        // state each frame is self-correcting regardless of how noisy the underlying event stream
        // is -- same reasoning as why WASD movement below is polled, not event-driven.
        bool wantOrbit = !hasUiFocus && Input.IsKeyPressed(Key.Alt) && Input.IsMouseButtonPressed(MouseButton.Left);

        if (wantOrbit && !_altOrbitActive)
        {
            _altOrbitActive = true;
            _altZoomAnchorPos = GetViewport().GetMousePosition();
            // Hidden, not Captured: Captured relies on the OS's raw-input relative-motion
            // capture, which live testing proved does not deliver ANY motion events over RDP/VM
            // (confirmed: zero InputEventMouseMotion for the entire duration of a held Alt+LMB,
            // then one large backlog jump the instant MouseMode left Captured). Hidden just hides
            // the cursor without that OS-level lock, so ordinary absolute-position polling below
            // still works everywhere Captured's relative deltas silently didn't.
            Input.MouseMode = Input.MouseModeEnum.Hidden;
            // Start tracking from wherever the cursor already is -- no warp on entry. An earlier
            // version warped to screen centre here and used that as the reference point, but
            // WarpMouse's effect on GetMousePosition() does not land synchronously (worse over
            // RDP, where it can lag by an unpredictable number of frames), so whichever LATER
            // frame the warp actually took effect on produced one huge, unpredictably-timed false
            // delta -- live-tested as a sudden jump to max zoom-out with no clear trigger.
            _orbitLastMousePos = _altZoomAnchorPos;
        }
        else if (!wantOrbit && _altOrbitActive)
        {
            _altOrbitActive = false;
            Input.MouseMode = Input.MouseModeEnum.Visible;
            Input.WarpMouse(_altZoomAnchorPos); // SL-style: cursor reappears where the drag started
            // No delta is computed again until re-engagement, so this warp's landing latency
            // (see above) can never be misread as a drag -- safe here specifically because it is.
        }
        else if (_altOrbitActive)
        {
            // Manual "capture": read the real cursor position (works over RDP, unlike
            // MouseMode.Captured's relative deltas) and derive our own delta from the last
            // OBSERVED position -- never from a hypothetical post-warp position (see above for
            // why). No periodic re-centring: the (hidden) cursor may reach the physical screen
            // edge on a long drag and simply stop contributing further delta in that direction
            // until reversed -- a minor UX limit, but a fully predictable one, unlike fighting
            // WarpMouse's landing latency.
            var currentPos = GetViewport().GetMousePosition();
            var orbitDelta = currentPos - _orbitLastMousePos;
            const float sensitivity = 0.003f;
            _orbitYaw -= orbitDelta.X * sensitivity;
            ZoomTowardCursor(orbitDelta.Y * sensitivity * 50.0f, _altZoomAnchorPos);
            _orbitLastMousePos = currentPos;
        }

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
