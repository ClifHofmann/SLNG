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
    // Set by Boot.cs — no longer used in the ground-clamp's own math (see that block's doc
    // comment), only to cross-verify AvatarRenderer's measured FootOffsetY/pelvis-fixup in the
    // [GroundClamp] diagnostic log against AvatarRenderer's own [RootApply] log.
    private AvatarRenderer? _avatarRenderer;
    private float _pitch = 0f;
    private float _yaw = 0f;
    private double _timeSinceLastUpdate = 0;

    // We store the last sent movement to avoid spamming the network
    private bool _lastFwd, _lastBack, _lastLeft, _lastRight, _lastUp, _lastDown;

    // MVP2-1: throttles GridSession.Stand() re-sends while a movement key is held seated (Stand()
    // pulses two real AgentUpdate packets, so every frame would spam the network) WITHOUT
    // permanently latching -- a fixed one-shot-per-sit flag (the original design) meant that if
    // the very first Stand() attempt didn't actually register server-side for any reason (packet
    // loss, a transient race), the player could never stand up again for the rest of that sit, no
    // matter how many more times they pressed a movement key (live-tested: reported exactly this).
    // Retrying on a short cooldown instead means a held/repeated key keeps trying until it works.
    private double _timeSinceLastStandRequest = double.MaxValue;
    private const double StandRequestCooldownSeconds = 1.0;
    private Vector3 _lastCameraRot;
    private float _zoom = 4.0f;
    private Vector3 _panOffset = Vector3.Zero;

    // BUG-NET-01: bounds for the AgentUpdate interest radius (Camera.Far). Min matches
    // LibreMetaverse's AgentCamera default so un-zoomed play is unchanged; max is the SL cap.
    private const float SlInterestFarMin = 128f;
    private const float SlInterestFarMax = 512f;

    // FEAT-UI-12: persisted FOV / rear distance / focus height from Preferences > Camera.
    private SLNG.App.UI.CameraSettings? _cameraSettings;
    // Last RearDistance applied to _zoom -- so a slider move re-snaps the resting distance live
    // (see _Process) without fighting wheel/pad zoom, which never changes this value.
    private float _lastAppliedRearDistance = SLNG.App.UI.CameraSettings.DefaultDistance;

    /// <summary>Wire the persisted camera view settings. FOV and the resting third-person distance
    /// apply at once; <see cref="_Process"/> then re-applies each whenever the setting itself
    /// changes (so the Preferences sliders are live), and <see cref="ResetCamera"/> uses the
    /// distance on every reset.</summary>
    public void SetCameraSettings(SLNG.App.UI.CameraSettings settings)
    {
        _cameraSettings = settings;
        Fov = settings.Fov;
        _zoom = settings.RearDistance;
        _lastAppliedRearDistance = settings.RearDistance;
    }

    // Public API for CameraHUD
    public void RotateCamera(Vector2 delta)
    {
        _transitioning = false; // manual input always wins over an in-progress camera pan
        _orbitYaw -= delta.X;
        _orbitPitch -= delta.Y;
        _orbitPitch = Mathf.Clamp(_orbitPitch, -1.5f, 1.5f);
    }

    /// <summary>Camera-Controls pan pad. <paramref name="delta"/> is a camera-local screen offset:
    /// +X moves the camera screen-right, +Y screen-up (see the consumer in <see cref="_Process"/>,
    /// which does <c>targetPos += Basis.X * _panOffset.X + Basis.Y * _panOffset.Y</c>). Accumulates
    /// -- it is a persistent framing offset, cleared by <see cref="ResetCamera"/> (Escape).</summary>
    public void PanCamera(Vector2 delta)
    {
        _transitioning = false;
        _panOffset += new Vector3(delta.X, delta.Y, 0);
    }

    public void ZoomCamera(float delta)
    {
        _transitioning = false;
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
        _transitioning = false;
        float oldZoom = _zoom;
        _zoom = Mathf.Clamp(_zoom + zoomDelta, 0.5f, 200.0f);
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

    /// <summary>Single entry point for every wheel/drag zoom. Picks the right strategy for the
    /// current camera mode (BUG-UI-05):
    /// <list type="bullet">
    /// <item>When an Alt+Click focus point is active (<see cref="_orbitTarget"/> set, or a
    /// non-avatar-follow glide toward one still running), the camera is aimed at a KNOWN 3D point,
    /// so zoom is a pure dolly along the camera-&gt;target line: just change <see cref="_zoom"/>.
    /// The old code always ran <see cref="ZoomTowardCursor"/>'s screen-space <see cref="_panOffset"/>
    /// nudge here too, which -- combined with it killing the focus glide mid-interpolation -- left
    /// the pivot frozen at a half-way Lerp point offset by an accumulating pan, i.e. every scroll
    /// zoomed toward a point that was neither the clicked object nor the screen centre (the exact
    /// bug reported). If a glide is still in flight, the whole zoom ramp is shifted by the same
    /// delta so the pan keeps running toward the target and simply rests nearer/further, rather
    /// than the wheel event snapping it dead.</item>
    /// <item>With no focus point (ordinary avatar-follow), there is no stored 3D point to zoom at,
    /// so the screen-space "keep whatever's under the cursor anchored" approximation
    /// (<see cref="ZoomTowardCursor"/>) is still the right behaviour -- unchanged.</item>
    /// </list></summary>
    private void ZoomBy(float zoomDelta, Vector2 mousePos)
    {
        bool glidingToFocus = _transitioning && !_transitionEndIsAvatarFollow;
        if (!_orbitTarget.HasValue && !glidingToFocus)
        {
            ZoomTowardCursor(zoomDelta, mousePos);
            return;
        }

        float clamped = Mathf.Clamp(_zoom + zoomDelta, 0.5f, 200f);
        float applied = clamped - _zoom;
        _zoom = clamped;

        if (glidingToFocus)
        {
            // Slide the entire in-progress zoom interpolation by the applied delta: the glide
            // still eases toward the focus point, it just ends at the new distance. Leaving
            // _transitioning alone (unlike ZoomTowardCursor) is the whole point -- the pivot must
            // still arrive at the clicked object.
            _transitionStartZoom = Mathf.Clamp(_transitionStartZoom + applied, 0.5f, 200f);
            _transitionEndZoom = Mathf.Clamp(_transitionEndZoom + applied, 0.5f, 200f);
        }
    }

    /// <summary>Smoothly pans/zooms back to directly behind the avatar (Escape) -- exactly
    /// <see cref="SetPresetView"/>'s "rear" case, so the two share one implementation.</summary>
    public void ResetCamera() => SetPresetView("rear");

    /// <summary>Turns the orbit camera to face this avatar AND pulls in to a close, frontal
    /// "portrait" distance -- unlike <see cref="FocusOn"/>'s "stay where I am, just re-aim"
    /// behaviour (used for Alt+Click on whatever's already on screen), a double-click on the
    /// minimap's roster genuinely wants to zoom IN on someone, not just point at wherever they
    /// happen to be from however far away the camera already sits (live-tested 2026-08-28: the
    /// first revision reused <see cref="FocusOn"/> as-is and the tester correctly called that out
    /// as "not just swing the camera -- zoom in, frontal view"). When the avatar's facing is
    /// known (<paramref name="avatarForwardGodot"/>, already Godot-space -- null for a
    /// CoarseLocationUpdate-only avatar outside draw distance, which carries no orientation at
    /// all), this approaches from the direction they're facing so the shot is actually frontal
    /// (their face toward the camera, not their back or side); otherwise falls back to
    /// approaching from wherever the camera already was.</summary>
    public void FocusOnAvatarFrontal(Vector3 targetPosition, Vector3? avatarForwardGodot)
    {
        const float portraitDistance = 3.5f;

        // Aim roughly at head height, not their feet/pelvis -- the same FocusHeight the
        // local-avatar follow camera already uses, for the identical reason (framing a face, not
        // the ground they're standing on).
        var target = targetPosition + Godot.Vector3.Up * (_cameraSettings?.FocusHeight ?? 1.8f);

        var approachDir = avatarForwardGodot is { } fwd && fwd.LengthSquared() > 0.0001f
            ? fwd
            : Position - target; // fall back to "approach from wherever the camera already was"
        if (!approachDir.IsFinite() || approachDir.LengthSquared() < 0.0001f) approachDir = Godot.Vector3.Back;
        approachDir = approachDir.Normalized();

        AimOrbitAt(target, target + approachDir * portraitDistance, portraitDistance);
    }

    private void FocusOn(Vector3 target)
    {
        // Keep the camera in the exact same physical spot, but look at the new target.
        AimOrbitAt(target, Position, Position.DistanceTo(target));
    }

    /// <summary>Shared by <see cref="FocusOn"/> (Alt+Click: stay put, re-aim) and
    /// <see cref="FocusOnAvatarFrontal"/> (double-click a roster avatar: jump to a close shot) --
    /// both boil down to "the camera ends up <paramref name="zoom"/> metres from <paramref
    /// name="target"/>, along the direction implied by treating <paramref name="fromPos"/> as
    /// where the camera is looking FROM." Sets <see cref="_orbitTarget"/> so <see cref="_Process"/>'s
    /// third-person camera math orbits this point instead of the local avatar.</summary>
    private void AimOrbitAt(Vector3 target, Vector3 fromPos, float zoom)
    {
        zoom = Mathf.Clamp(zoom, 0.5f, 200.0f);

        if (fromPos.DistanceSquaredTo(target) <= 0.0001f)
        {
            // No meaningful direction to derive an angle from -- move to this target/zoom without
            // changing which way the camera is currently pointed.
            StartTransition(target, _yaw + _orbitYaw, _pitch + _orbitPitch, zoom);
            return;
        }

        // Up vector must not be parallel to the look direction.
        var lookDir = (target - fromPos).Normalized();
        var cameraUp = Godot.Vector3.Up;
        if (Mathf.Abs(lookDir.Dot(cameraUp)) > 0.99f) cameraUp = Godot.Vector3.Forward;

        var lookTransform = new Transform3D(Basis.Identity, fromPos).LookingAt(target, cameraUp);
        var euler = lookTransform.Basis.GetEuler(Godot.EulerOrder.Yxz);

        StartTransition(target, euler.Y, euler.X, zoom);
    }

    /// <summary>Captures where the camera's orbit actually is RIGHT NOW (absolute yaw/pitch, not
    /// just the <see cref="_orbitYaw"/>/<see cref="_orbitPitch"/> offset, since <see cref="_yaw"/>/
    /// <see cref="_pitch"/> can keep changing during the transition as the local avatar turns) and
    /// starts a smooth interpolation toward the new framing, applied each frame in
    /// <see cref="_Process"/>. <paramref name="endYawAbs"/>/<paramref name="endPitchAbs"/> are
    /// ABSOLUTE angles (as <c>Transform.Basis.GetEuler</c> returns), matching what
    /// <see cref="_transitionStartYaw"/>/<see cref="_transitionStartPitch"/> capture.</summary>
    private void StartTransition(Godot.Vector3 endTarget, float endYawAbs, float endPitchAbs, float endZoom,
        bool endIsAvatarFollow = false)
    {
        // Position = effectiveTarget + Basis.Z * zoom (see _Process) -- inverting that recovers
        // the current effective orbit centre regardless of whether _orbitTarget was set at all,
        // so a transition starting from "orbiting the local avatar" (_orbitTarget null) still has
        // a real point in space to pan FROM.
        _transitionStartTarget = Position - Transform.Basis.Z * _zoom;
        _transitionStartYaw = _yaw + _orbitYaw;
        _transitionStartPitch = _pitch + _orbitPitch;
        _transitionStartZoom = _zoom;

        // Shortest angular path, same wraparound AimOrbitAt used to apply this instantly before.
        float yawDelta = endYawAbs - _transitionStartYaw;
        while (yawDelta > Mathf.Pi) yawDelta -= Mathf.Tau;
        while (yawDelta < -Mathf.Pi) yawDelta += Mathf.Tau;

        _transitionEndTarget = endTarget;
        _transitionEndYaw = _transitionStartYaw + yawDelta;
        _transitionEndPitch = endPitchAbs;
        _transitionEndZoom = endZoom;
        _transitionEndIsAvatarFollow = endIsAvatarFollow;

        _transitionElapsed = 0f;
        _transitioning = true;
        _panOffset = Godot.Vector3.Zero;
    }

    /// <summary>Advances an in-progress <see cref="StartTransition"/> and applies this frame's
    /// interpolated framing to <see cref="_orbitTarget"/>/<see cref="_orbitYaw"/>/
    /// <see cref="_orbitPitch"/>/<see cref="_zoom"/> -- called from <see cref="_Process"/>, before
    /// those are read to compute <c>Position</c>. No-op (and leaves those fields alone) once no
    /// transition is active, whether it finished normally or was cancelled by manual input.</summary>
    private void UpdateTransition(double delta)
    {
        if (!_transitioning) return;

        _transitionElapsed += (float)delta;
        float t = Mathf.Clamp(_transitionElapsed / TransitionDuration, 0f, 1f);
        float eased = t * t * (3f - 2f * t); // smoothstep -- eases in and out, not a linear pan

        // ResetCamera's transition ends by returning to the DYNAMIC avatar-follow target, not a
        // fixed point -- if the avatar is walking while Escape is pressed, panning toward a stale
        // snapshot of where they were half a second ago would visibly lag behind them. Re-read it
        // every frame of the transition instead (falls back to the fixed end target if the local
        // avatar can't be found for some reason, e.g. mid-relog).
        var endTarget = _transitionEndIsAvatarFollow
            ? (GetLocalAvatarFollowTarget() ?? _transitionEndTarget)
            : _transitionEndTarget;

        _orbitTarget = _transitionStartTarget.Lerp(endTarget, eased);
        float yaw = Mathf.Lerp(_transitionStartYaw, _transitionEndYaw, eased);
        float pitch = Mathf.Lerp(_transitionStartPitch, _transitionEndPitch, eased);
        _zoom = Mathf.Lerp(_transitionStartZoom, _transitionEndZoom, eased);
        _orbitYaw = yaw - _yaw;
        _orbitPitch = pitch - _pitch;

        if (t >= 1f)
        {
            _transitioning = false;
            // Hand back to _Process's own dynamic avatar-follow branch (_orbitTarget == null)
            // instead of leaving the camera permanently pinned to wherever this snapshot ended up.
            if (_transitionEndIsAvatarFollow) _orbitTarget = null;
        }
    }

    /// <summary>The point the camera would be aimed at RIGHT NOW if nothing (Alt+Click, the
    /// minimap jump, an in-progress reset) had it pinned to a fixed <see cref="_orbitTarget"/> --
    /// i.e. the exact target <see cref="_Process"/>'s own avatar-follow branch computes. Used by
    /// <see cref="ResetCamera"/> (as the transition's destination) and by
    /// <see cref="UpdateTransition"/> (re-read every frame while returning to follow-mode, so a
    /// moving avatar doesn't leave the camera panning toward a stale snapshot). Deliberately a
    /// SEPARATE lookup from <see cref="_Process"/>'s own inline copy of this formula rather than a
    /// shared helper called from both: that one already has <c>localAgent</c>/<c>transform</c>
    /// resolved from its own per-frame entity scan, and this is only ever called occasionally
    /// (once from <see cref="ResetCamera"/>) or for a transition's few hundred milliseconds, never
    /// from the steady-state per-frame hot path -- an extra scan there would be a real, needless
    /// per-frame cost this one isn't.</summary>
    private Godot.Vector3? GetLocalAvatarFollowTarget()
    {
        if (_world == null) return null;
        var localAgent = _world.GetAllEntities().FirstOrDefault(e => e.GetComponent<AvatarComponent>()?.IsLocalAgent == true);
        var transform = localAgent?.GetComponent<TransformComponent>();
        if (localAgent == null || transform == null) return null;

        var pos = RenderConfig.ToGodot(localAgent.RegionHandle, transform.Position);
        pos.Y += _cameraSettings?.FocusHeight ?? 1.8f;
        return pos;
    }

    /// <summary>Smoothly pans/zooms to a named preset orbit angle directly behind/beside/in front
    /// of the avatar, at the default rear-view zoom -- requested 2026-08-28 alongside the minimap
    /// jump's own smooth pan, rather than the instant snap this (and <see cref="ResetCamera"/>,
    /// which is just this with "rear") used to be. Ends by returning to the DYNAMIC avatar-follow
    /// target (<see cref="_orbitTarget"/> back to <c>null</c>), not a fixed point frozen at
    /// whatever the transition's destination snapshot was -- see <see cref="UpdateTransition"/>'s
    /// doc comment.</summary>
    public void SetPresetView(string preset)
    {
        float orbitYawPreset = preset.ToLower() switch
        {
            "front" => Mathf.Pi, // 180 degrees
            "side" => Mathf.Pi / 2.0f, // 90 degrees
            _ => 0f, // "rear" and default
        };

        float endZoom = _cameraSettings?.RearDistance ?? 4.0f;
        var endTarget = GetLocalAvatarFollowTarget() ?? (Position - Transform.Basis.Z * _zoom);
        // Absolute end yaw = the avatar's own current facing plus the preset's orbit offset;
        // UpdateTransition derives _orbitYaw from this (interpolated absolute minus current _yaw)
        // every frame, so it stays correct even if the avatar keeps turning mid-transition.
        StartTransition(endTarget, _yaw + orbitYawPreset, _pitch, endZoom, endIsAvatarFollow: true);
    }

    // Alt+LMB orbit state. The orbit offsets rotate the CAMERA around the avatar
    // without changing the avatar's facing (_yaw/_pitch). They snap back to 0 when
    // the avatar moves, so the camera returns behind the avatar — SL-style.
    // Set by polling in _Process (Input.IsKeyPressed/IsMouseButtonPressed), NOT by this
    // button's own press/release events -- see the comment at that poll site for why.
    private bool _altOrbitActive = false;
    private float _orbitYaw = 0f;
    private float _orbitPitch = 0f;
    private Godot.Vector3? _orbitTarget = null;

    // Smooth camera transition (FocusOn/FocusOnAvatarFrontal) -- live-tested 2026-08-28: an
    // instant snap to the new framing read as a jarring hard cut, not the "pan there" feel a
    // "jump to this person" gesture should have. Interpolates orbit yaw/pitch/zoom/target from
    // wherever the camera actually was toward the new framing over TransitionDuration seconds.
    // Cancelled by ANY manual camera input (drag/scroll/wheel/WASD) -- see each of those methods
    // -- so the user always regains immediate control rather than fighting an in-progress pan.
    private const float TransitionDuration = 0.5f;
    private bool _transitioning;
    private float _transitionElapsed;
    private float _transitionStartYaw, _transitionStartPitch, _transitionStartZoom;
    private Godot.Vector3 _transitionStartTarget;
    private float _transitionEndYaw, _transitionEndPitch, _transitionEndZoom;
    private Godot.Vector3 _transitionEndTarget;
    // True only for ResetCamera's transition -- see UpdateTransition's doc comment.
    private bool _transitionEndIsAvatarFollow;

    // Set by AbortCameraDrag: blocks orbit re-engagement until the left button is physically
    // released once. Without it, a focus-out / mouse-exit that fires mid-drag (X11, some Windows
    // edge cases) while Alt+LMB are still held re-satisfies `wantOrbit && !_altOrbitActive` on the
    // very next frame and re-engages from the cursor's edge position -- the BUG-UI-03 jump, moved.
    private bool _suppressOrbitUntilRelease = false;

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

    // Throttles the ground-clamp diagnostic print below to ~1/sec instead of every frame.
    private double _timeSinceGroundLog = 0;

    /// <summary>Last ground source seen, so the diagnostic above fires on transitions instead of
    /// every frame. "collider" collapses the per-object detail -- which object it is matters far
    /// less than whether an object was hit at all.</summary>
    private bool _lastGroundWasObject;
    private string _lastGroundKind = "";

    /// <summary>Ground height at the previous sample, so a transition can report the DROP rather
    /// than just the new value -- stepping between two surfaces at the same height is normal, and
    /// only a transition that loses height is a fall.</summary>
    private float _lastGroundZ;

    // Bump alongside every fix so a fresh log line proves this exact build is running (see
    // AvatarRenderer.BuildMarker's doc comment — same stale-assembly hazard applies here).
    private const string BuildMarker = "2026-07-22-groundclamp-reverted-to-simple-clamp";

    public void Initialize(World world, GridSession session, AvatarRenderer? avatarRenderer = null)
    {
        // Build marker only under --diag: it exists to prove which assembly is actually loaded
        // when a fix appears not to have taken (see the stale-assembly note in the repo docs).
        if (Diagnostics.Enabled) GD.Print($"[AvatarController] BUILD MARKER: {BuildMarker}");
        _world = world;
        _session = session;
        _avatarRenderer = avatarRenderer;
    }

    public override void _Ready()
    {
        Input.MouseMode = Input.MouseModeEnum.Visible;
        ProcessPriority = 100; // Run after Boot.cs (0) to read freshly extrapolated positions
        PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off; // Prevent stutter from _Process updates
    }

    /// <summary>BUG-UI-03: force-cancel an in-progress Alt+LMB orbit/zoom drag when the window or
    /// the mouse leaves the app. The drag runs with <see cref="Input.MouseModeEnum.Hidden"/> (not
    /// Captured -- see the engage site), so a button release that happens outside the window is
    /// never delivered; the per-frame <c>Input.IsMouseButtonPressed</c> poll that drives
    /// <see cref="_altOrbitActive"/> then stays stuck "pressed" until focus returns, leaving the
    /// camera orbiting to the unfocused mouse and jumping the next time Alt is pressed.</summary>
    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationWMWindowFocusOut
            || what == NotificationApplicationFocusOut
            || what == NotificationWMMouseExit)
        {
            AbortCameraDrag();
        }
    }

    private void AbortCameraDrag()
    {
        if (!_altOrbitActive) return;
        _altOrbitActive = false;
        // Hold off re-engaging until Left is physically released once -- the button may still be
        // down (window/mouse just left the app), and _Process would otherwise re-engage next frame.
        _suppressOrbitUntilRelease = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        // Re-engage re-seeds this from the live cursor position; zero it so a stale value can
        // never be misread as a one-frame delta if the drag branch runs before the next engage.
        _orbitLastMousePos = Vector2.Zero;
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
                if (Diagnostics.Enabled) GD.Print($"[AvatarController] Flying: {(_flying ? "ON" : "off")}");
            }
            else if (keyEvt.Keycode == Key.Escape)
            {
                // Was inlining just 2 of ResetCamera()'s 5 resets (zoom, orbit yaw/pitch) --
                // missing _panOffset and, critically, _orbitTarget. With either still set (any
                // prior Alt+LMB orbit-around-a-clicked-point, or a pan), Escape reset the zoom
                // distance/angle but the camera stayed aimed at that stale pan/orbit point instead
                // of snapping cleanly back to directly behind the avatar -- reported as "zooming
                // back on Esc doesn't work cleanly, should jump to rear view."
                ResetCamera();
            }
        }

        if (@event is InputEventMouseButton mouseBtn)
        {
            // Reaching _UnhandledInput at all is SUPPOSED to mean no Control under the cursor
            // claimed this event -- live testing showed that assumption doesn't hold in every
            // case (mouse-wheel scroll over the Inventory Tree still reached here and zoomed the
            // world at the same time), so this checks GuiGetHoveredControl() directly rather than
            // trusting Godot's own consumption bookkeeping: if the mouse is over ANY Control right
            // now, this is a UI scroll/interaction, full stop, regardless of whether that Control
            // itself marked the event handled. Same hasUiFocus signal (focus-based) still also
            // gates WASD/orbit in _Process below; hover is checked here in addition, specifically
            // for wheel-zoom, since a scroll is defined by where the cursor sits, not by focus.
            var focusOwner = GetViewport().GuiGetFocusOwner();
            bool hasUiFocus = BlocksMovement(focusOwner)
                || GetViewport().GuiGetHoveredControl() != null;

            // A click that reaches _UnhandledInput at all landed in the 3D viewport, not on any
            // Control (see the method-level comment) -- so a stale LineEdit/TextEdit focus owner
            // here means the user clicked into a text field earlier (chat, a search box, ...),
            // then clicked back into the world without ever submitting/dismissing it. hasUiFocus
            // gates WASD/orbit for as long as that focus sits there (see _Process below), which
            // otherwise locks movement out until something else happens to steal focus. Release
            // it on any click that actually reaches here so movement resumes immediately, same as
            // clicking into the 3D view in every other viewer. ChatWindow.OnSendPressed already
            // releases focus on submit; this covers every other way it can be left behind.
            if (mouseBtn.Pressed && BlocksMovement(focusOwner))
            {
                GetViewport().GuiReleaseFocus();
                hasUiFocus = GetViewport().GuiGetHoveredControl() != null;
            }

            if (!hasUiFocus && mouseBtn.ButtonIndex == MouseButton.WheelUp)
            {
                ZoomBy(-0.5f, mouseBtn.Position);
            }
            else if (!hasUiFocus && mouseBtn.ButtonIndex == MouseButton.WheelDown)
            {
                ZoomBy(0.5f, mouseBtn.Position);
            }
        }

        // Orbit rotation itself is handled by position-polling in _Process, not by
        // InputEventMouseMotion -- see the comment at that poll site for why (MouseMode.Captured's
        // native relative-motion deltas don't arrive at all over RDP/VM, confirmed live: zero
        // motion events during an entire Alt+LMB hold, then one big backlog jump on release).
    }

    public override void _Process(double delta)
    {
        using var _phase = MainThreadPhase.Enter("avatar-control");

        if (_world == null || _session == null) return;

        // FEAT-UI-12: keep FOV in sync with the persisted preference so a slider move applies live.
        if (_cameraSettings != null && !Mathf.IsEqualApprox(Fov, _cameraSettings.Fov))
            Fov = _cameraSettings.Fov;

        // FEAT-UI-12: re-snap the resting distance when the "Rear view distance" slider moves.
        // Only fires on an actual change, so wheel/pad zoom (which move _zoom, never RearDistance)
        // are left alone.
        if (_cameraSettings != null && !Mathf.IsEqualApprox(_cameraSettings.RearDistance, _lastAppliedRearDistance))
        {
            _zoom = _cameraSettings.RearDistance;
            _lastAppliedRearDistance = _cameraSettings.RearDistance;
        }

        var focusOwner = GetViewport().GuiGetFocusOwner();
        bool hasUiFocus = BlocksMovement(focusOwner);

        // BUG-NET-01: captured once the render-camera pose is finalised (inside the localAgent /
        // transform null-checks) and fed to the 10 Hz AgentUpdate below, which is sent OUTSIDE
        // those checks. Region-local SL coords / Z-up axes -- the boundary to LibreMetaverse.
        System.Numerics.Vector3? camSimPos = null;
        System.Numerics.Vector3? camSimForward = null;
        float camFar = 0f;

        // Alt+LMB orbit engagement, polled every frame instead of driven by the button's own
        // discrete press/release events. Live-tested proof this was needed: holding Alt+LMB
        // produced a rapid, alternating pressed=True/False/True/False... event stream instead of
        // one press followed by a held state -- the OLD event-driven toggle treated every spurious
        // "release" as the real button-up and immediately cancelled orbit before a drag could ever
        // register, so the feature looked completely dead. Polling the actual current physical
        // state each frame is self-correcting regardless of how noisy the underlying event stream
        // is -- same reasoning as why WASD movement below is polled, not event-driven.
        bool wantOrbit = !hasUiFocus && Input.IsKeyPressed(Key.Alt) && Input.IsMouseButtonPressed(MouseButton.Left);

        // After AbortCameraDrag the physical LMB may still be held; don't re-engage until it has
        // been released at least once (see _suppressOrbitUntilRelease).
        if (_suppressOrbitUntilRelease)
        {
            if (Input.IsMouseButtonPressed(MouseButton.Left)) wantOrbit = false;
            else _suppressOrbitUntilRelease = false;
        }

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

            var spaceState = GetWorld3D().DirectSpaceState;
            var rayOrigin = ProjectRayOrigin(_altZoomAnchorPos);
            var rayEnd = rayOrigin + ProjectRayNormal(_altZoomAnchorPos) * 1000f;
            var query = PhysicsRayQueryParameters3D.Create(rayOrigin, rayEnd);
            // Terrain moved to its own layer (PhysicsLayers.Terrain) so CursorManager's
            // hover raycast could stop paying its console-warning cost on unstreamed
            // patches; this orbit-target raycast still needs terrain, so it's listed
            // explicitly alongside objects and avatars rather than relying on a shared bit.
            query.CollisionMask = PhysicsLayers.Objects | PhysicsLayers.Terrain | PhysicsLayers.Avatars;
            var result = spaceState.IntersectRay(query);
            if (result.Count > 0)
            {
                FocusOn(result["position"].AsVector3());
            }
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
            // BUG-UI-05: dolly toward the Alt+Click hit point (set by FocusOn on engage) rather
            // than the screen-space _panOffset nudge, which drifted the pivot off the object.
            ZoomBy(orbitDelta.Y * sensitivity * 50.0f, _altZoomAnchorPos);
            _orbitLastMousePos = currentPos;
        }

        // 1. Follow the Avatar
        var localAgent = _world.GetAllEntities()
            .FirstOrDefault(e => e.GetComponent<AvatarComponent>()?.IsLocalAgent == true);

        // MVP2-1: while sitting, the seat (not player input) owns facing/position -- WASD
        // turning, fly, and the ground-clamp below are all suspended, and transform.Rotation is
        // no longer written here at all (WorldSimulation.ApplyAvatarUpdate/ExtrapolateMovement
        // pick up ownership instead, see their own isSeatedLocalAgent gates). The camera still
        // follows the resolved seat position/zoom/orbit exactly as before -- GridSession already
        // resolves a seated avatar's wire-relative Position/Rotation to world space, so nothing
        // else here needs to change to "look at the seat" versus "look at standing avatar."
        bool isSitting = localAgent?.GetComponent<AvatarComponent>()?.SittingOnLocalId != 0;
        if (!isSitting) _timeSinceLastStandRequest = double.MaxValue;

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
                // Home toggles it off. See _Input. Suspended while sitting -- see isSitting's
                // doc comment above.
                if (isUp && !_flying && !isSitting) _flying = true;

                // In SL/Firestorm, A and D turn the avatar when not strafing -- not while
                // sitting, where facing is the seat's, not the player's.
                if (!isSitting)
                {
                    if (isLeft) _yaw += 2.5f * (float)delta;
                    if (isRight) _yaw -= 2.5f * (float)delta;
                }

                // Any movement/turn snaps the orbit camera back behind the avatar.
                if (!isSitting && (isFwd || isBack || isLeft || isRight))
                {
                    _transitioning = false;
                    _orbitYaw = 0f;
                    _orbitPitch = 0f;
                    _orbitTarget = null;
                }

                // Camera rotation = avatar facing (_yaw/_pitch) plus the orbit offset.
                // The orbit offset moves the camera around the avatar without turning it.
                Rotation = new Vector3(_pitch + _orbitPitch, _yaw + _orbitYaw, 0);

                // Horizontal (X/Y) movement is NOT client-predicted here. It used to be (WASD dead
                // reckoning added directly to transform.Position), but that fought the sim's own
                // echo of our avatar's position -- which streams continuously while we're
                // physically moving (see GridSession.OnTerseObjectUpdate) -- every time a packet
                // landed, popping the avatar sideways mid-stride (worst on a diagonal heading,
                // where the correction lands on both axes at once instead of just one). The real
                // viewer doesn't predict its own position either: LLAgent::getPositionAgent()
                // mirrors LLVOAvatarSelf's network-driven position, and smoothness between packets
                // comes from velocity dead-reckoning (WorldSimulation.ExtrapolateMovement mirrors
                // LLViewerObject::interpolateLinearMotion), not from a second local authority. W/A/
                // S/D still drive movement -- via _session.SetMovement's control flags below, which
                // the sim actually simulates; this block only used to add a purely cosmetic (and
                // ultimately incorrect) local head start on top of that.

                // Ground-clamp, fly, and the local body-rotation write below all assume the
                // player is standing -- while sitting, the seat's own network transform (resolved
                // by GridSession, applied by WorldSimulation) is the sole authority instead. See
                // isSitting's doc comment.
                if (!isSitting)
                {

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
                if (!godotPos.IsFinite()) return;

                
                // Cast from 2 meters above the avatar's feet, down to 100 meters below
                var rayFrom = godotPos + new Godot.Vector3(0, 2.0f, 0);
                var rayTo = godotPos - new Godot.Vector3(0, 100.0f, 0);
                if (rayFrom == rayTo || !rayFrom.IsFinite() || !rayTo.IsFinite()) return;
                
                var query = PhysicsRayQueryParameters3D.Create(rayFrom, rayTo);
                // Ground detection genuinely needs terrain (PhysicsLayers.Terrain), which now
                // lives on its own bit rather than sharing Objects -- see that constant's doc
                // comment. Layer 2 (avatars) stays excluded so standing on someone doesn't read
                // as ground.
                query.CollisionMask = PhysicsLayers.Objects | PhysicsLayers.Terrain;
                
                var result = spaceState.IntersectRay(query);

                float groundHeight = 0;
                bool hasGround = false;

                // Whether this ground reading is allowed to pull the avatar DOWN.
                //
                // Only a reading that actually knows what is underneath may do that. The terrain
                // heightmap does not: it describes the land, and says nothing about the prim the
                // avatar is standing on. Believing it while an object collider had not streamed in
                // yet is the whole login/teleport fall -- the avatar was dragged off the prim, at
                // 9.81 m/s^2, down to the terrain, against a position the simulator had already
                // got right.
                bool groundMayLower = true;
                // What surface groundHeight actually came from — needed to tell a terrain-vs-object
                // collision mismatch apart from an avatar-side offset bug (2026-07-22 ground-
                // sinking investigation, round 4: the user is standing on a rezzed wooden platform
                // object, not raw terrain, so "which collider did the ray actually hit, and is its
                // reported Y the platform's real top surface" is now a live, separate hypothesis).
                string groundSource = "none";

                // Whether the support under the avatar is an OBJECT rather than terrain, tracked as
                // a flag instead of being recovered from groundSource's text later.
                //
                // It used to be recovered from the text, and it never worked: the check was
                // groundSource.StartsWith("collider:Obj"), but groundSource is built from the
                // collider node's NAME, and ObjectRenderer names that node "StaticBody" -- the
                // Obj_<uuid> id is one level up, in the PATH. So the real string is always
                // "collider:StaticBody(path=.../Obj_<uuid>/StaticBody)" and the prefix test could
                // never be true. `grep "FELL THROUGH"` returns zero across every log ever captured,
                // including sessions whose logs plainly contain object colliders and in which the
                // user was falling off prims on every login. A diagnostic that cannot fire is worse
                // than none: its silence was read as evidence.
                bool groundIsObject = false;

                // The simulator's own answer, and the one the real viewer uses. SL ships a
                // collision plane in the avatar's update, computed by its Havok physics, and
                // LLWorld::resolveStepHeightGlobal (llworld.cpp:532) corrects the land height with
                // it rather than raycasting object geometry -- the viewer never decides what an
                // avatar stands on by probing the scene, because the server already decided.
                //
                // It needs none of our colliders to have streamed in, which is exactly why it fixes
                // login and teleport: the plane arrives with the first avatar update, long before
                // the prim underneath has a CollisionShape3D.
                var avatarComponent = localAgent.GetComponent<SLNG.Core.Components.AvatarComponent>();
                if (avatarComponent?.SupportPlane is { } supportPlane
                    && SLNG.Core.AvatarSupport.SupportHeight(supportPlane, transform.Position)
                        is { } planeSupportZ)
                {
                    groundHeight = planeSupportZ;
                    hasGround = true;
                    // Counts as an object surface: the plane is what the simulator says the avatar
                    // rests on, which on a prim IS the prim. Leaving this false would make the
                    // fell-through diagnostic blind again in exactly the case it was built for.
                    groundIsObject = true;
                    groundSource = "sim-collision-plane";
                }

                if (!hasGround && result.Count > 0)
                {
                    groundHeight = result["position"].AsVector3().Y;
                    hasGround = true;
                    if (result.ContainsKey("collider"))
                    {
                        var colliderNode = result["collider"].AsGodotObject() as Node;
                        groundSource = colliderNode != null
                            ? $"collider:{colliderNode.Name}(path={colliderNode.GetPath()})"
                            : "collider:<unnamed>";
                        groundIsObject = colliderNode != null
                            && colliderNode.GetPath().ToString().Contains("/Obj_");
                    }
                }
                else if (!hasGround)
                {
                    // Fallback to terrain heightmap if raycast misses. Right after a landmark
                    // teleport, the physics raycast reliably misses for up to ~0.75s -- the new
                    // region's collider isn't built yet (TerrainRenderer coalesces rebuilds, see
                    // its _rebuildAccum). Trusting GetHeights() blindly here doesn't help: a cell
                    // whose 16x16 patch hasn't streamed in yet defaults to 0.0f, which used to
                    // read as "hasGround = true, ground is at Z=0" and drop the avatar toward it
                    // -- a visible free-fall from the real (often ~20-25m) spawn height down to
                    // ~1m, i.e. exactly "falls through the floor before it's there". Only trust a
                    // cell that has actually received a real patch.
                    int rawX = (int)transform.Position.X;
                    int rawY = (int)transform.Position.Y;
                    if (_world.Terrains.TryGetValue(localAgent.RegionHandle, out var terrain)
                        && terrain.TryGetKnownHeight(rawX, rawY, out float knownHeight))
                    {
                        groundHeight = knownHeight;
                        hasGround = true;
                        groundSource = "terrain-heightmap-fallback";
                        // Push-up only. See groundMayLower above: this reading cannot see prims,
                        // so it may rescue an avatar that has sunk into the land, and must never
                        // be the reason one leaves a surface.
                        groundMayLower = false;
                    }
                }

                // Ground-source diagnostic, re-armed for FEAT-PERF-01. groundSource was already being
                // computed here but never printed, so "collision doesn't work" had no evidence
                // behind it either way -- and the cost tables prove the shapes ARE being built
                // (collision.shape n=810, collision.urgent n=62, no exceptions), which means the
                // question is not whether they exist but whether this ray finds them.
                //
                // Logged on CHANGE rather than periodically: the interesting event is the moment the
                // ray stops hitting an object collider and falls through to the terrain heightmap
                // (or nothing at all), and a periodic line would either miss it or bury it.
                if (Diagnostics.Enabled)
                {
                _timeSinceGroundLog += delta;

                // Compared on the FULL source, not a collapsed "collider" kind. The first version
                // collapsed every object collider to one word, which hid the transition that matters:
                // the ray moving from a specific prim to the terrain underneath it. That transition
                // IS the report -- "I fall through prims" is the ground under your feet swapping from
                // Obj_<id> to TerrainPhysics with the height dropping at the same moment.
                if (groundSource != _lastGroundKind || _timeSinceGroundLog >= 10.0)
                {
                    // A drop while stepping off an object collider is the fall itself, so it is called
                    // out separately rather than left to be spotted by comparing two log lines.
                    bool fellOffObject = _lastGroundWasObject
                                         && !groundIsObject
                                         && groundHeight < _lastGroundZ - 0.15f;

                    GD.Print($"[GroundClamp] {(fellOffObject ? "FELL THROUGH " : "")}" +
                              $"source={groundSource} hasGround={hasGround} " +
                              $"groundZ={groundHeight:0.00} (was {_lastGroundZ:0.00} on {_lastGroundKind}) " +
                              $"agentZ={transform.Position.Z:0.00}");

                    _lastGroundKind = groundSource;
                    _timeSinceGroundLog = 0;
                }
                if (hasGround)
                {
                    _lastGroundZ = groundHeight;
                    _lastGroundWasObject = groundIsObject;
                }
                }

                if (hasGround)
                {
                    // Reverted to a plain "network Z == feet at ground" clamp (2026-07-22, round 5
                    // of the ground-sinking investigation). A previous version of this block
                    // subtracted AvatarRenderer's RootOffsetZ here so AvatarRenderer could add it
                    // back at render time — that design was provably a no-op: clampTargetZ =
                    // groundHeight - correction, then Root.Y = clampTargetZ + correction, which
                    float halfBodyZ = 0.95f;
                    if (_avatarRenderer != null && _avatarRenderer.TryGetBodySizeZ(localAgent.Id, out float bodySizeZ))
                    {
                        halfBodyZ = 0.5f * bodySizeZ;
                    }

                    // Second Life physics model: transform.Position.Z is the collision cylinder center.
                    // For a standing avatar whose feet sit at groundHeight, the cylinder center is groundHeight + halfBodyZ.
                    float clampTargetZ = groundHeight + halfBodyZ;



                    if (_flying)
                    {
                        if (transform.Position.Z < clampTargetZ)
                        {
                            // Touched down — land and leave fly mode.
                            transform.Position = new System.Numerics.Vector3(transform.Position.X, transform.Position.Y, clampTargetZ);
                            _flying = false;
                        }
                    }
                    else if (transform.Position.Z < clampTargetZ)
                    {
                        // Push up out of terrain/object
                        transform.Position = new System.Numerics.Vector3(transform.Position.X, transform.Position.Y, clampTargetZ);
                    }
                    else if (transform.Position.Z > clampTargetZ && groundMayLower)
                    {
                        // Fall down to terrain/object
                        float fallSpeed = 9.81f * (float)delta;
                        transform.Position = new System.Numerics.Vector3(transform.Position.X, transform.Position.Y, System.Math.Max(clampTargetZ, transform.Position.Z - fallSpeed));
                    }
                }

                // Avatar body faces _yaw, updated EVERY FRAME -- not just at the 10 Hz AgentUpdate
                // send rate below. The body rotation is pure local camera-yaw input (no network
                // round-trip involved, same as local Z), so it must render at frame rate. Writing it
                // only at 10 Hz made the body snap ~14 deg per step during a turn (2.5 rad/s * 0.1s)
                // while the camera panned smoothly every frame -- the avatar appearing to "restart"
                // every few degrees when turning, and the same stepping during walk-with-steering
                // reading as left/right jitter. Same yaw-only quaternion the SetMovement send uses
                // (see the 10 Hz block); WorldSimulation.ExtrapolateMovement deliberately skips the
                // rotation slerp for the local agent so this per-frame write is the sole authority.
                transform.Rotation = ComputeBodyRotation();

                } // !isSitting

                _world.NotifyComponentUpdated(localAgent, transform);
                
                // Execute UpdateVisual synchronously on the main thread so the avatar mesh
                // position matches the camera position exactly this frame, eliminating jitter.
                // AvatarRenderer's CallDeferred will also run later, but it will be a no-op.
                _avatarRenderer?.UpdateVisual(localAgent.Id.ToString());

                // Keyboard zoom polling (+ and - keys)
                if (Input.IsKeyPressed(Key.Equal) || Input.IsKeyPressed(Key.KpAdd))
                {
                    _transitioning = false;
                    _zoom = Mathf.Max(0.5f, _zoom - 15.0f * (float)delta);
                }
                if (Input.IsKeyPressed(Key.Minus) || Input.IsKeyPressed(Key.KpSubtract))
                {
                    _transitioning = false;
                    _zoom = Mathf.Min(200.0f, _zoom + 15.0f * (float)delta);
                }

                UpdateTransition(delta);

                Godot.Vector3 targetPos;
                if (_orbitTarget.HasValue)
                {
                    targetPos = _orbitTarget.Value;
                }
                else
                {
                    // Floating-origin-relative world position (see RenderConfig), plus the focus
                    // height (FEAT-UI-12: was a hardcoded 1.8, now the persisted preference).
                    targetPos = RenderConfig.ToGodot(localAgent.RegionHandle, transform.Position);
                    targetPos.Y += _cameraSettings?.FocusHeight ?? 1.8f;
                }

                // BUG-UI-02: apply the Camera-Controls pan-pad offset for BOTH the avatar-follow
                // and the Alt-click focus target. It used to live inside the else above, so the
                // moment a focus target was set the pan pad did nothing at all -- the "stops
                // working when zoomed in / after focusing on an object" half of the bug. Camera-
                // local axes, so "pan left" stays screen-left regardless of where the camera looks.
                targetPos += Transform.Basis.X * _panOffset.X;
                targetPos += Transform.Basis.Y * _panOffset.Y;

                // Third-person camera: pull back along the camera's Z axis
                Position = targetPos + Transform.Basis.Z * _zoom;

                // BUG-NET-01: tell the sim where the render camera actually is. Its interest list
                // is centred on CameraCenter; SLNG never set it, so it defaulted to region centre
                // (LibreMetaverse's AgentCamera 128,128,20) and alt-zooming to a distant point
                // never streamed the objects there. Godot world -> region-local SL (Z-up); for a
                // direction the origin/region offsets cancel, only the axis swap remains.
                if (Position.IsFinite())
                {
                    camSimPos = RenderConfig.FromGodot(localAgent.RegionHandle, Position);
                    var fwdGodot = -Transform.Basis.Z;
                    camSimForward = new System.Numerics.Vector3(fwdGodot.X, -fwdGodot.Z, fwdGodot.Y);
                    // Interest radius: never below today's effective floor, grow to still cover the
                    // avatar's surroundings when the camera has moved away, capped at the SL max.
                    var avatarGodot = RenderConfig.ToGodot(localAgent.RegionHandle, transform.Position);
                    camFar = Mathf.Min(SlInterestFarMax, Mathf.Max(SlInterestFarMin,
                        Position.DistanceTo(avatarGodot) + RenderConfig.DrawDistance));
                }
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

        // MVP2-1: any movement key stands the seated avatar up, matching the real viewer's
        // convention. Retries on a cooldown rather than a permanent per-sit latch -- see
        // _timeSinceLastStandRequest's doc comment for why a one-shot flag left the player unable
        // to ever stand again if the first attempt didn't take.
        _timeSinceLastStandRequest += delta;
        if (isSitting)
        {
            if ((fwd || back || left || right || up || down) && _timeSinceLastStandRequest >= StandRequestCooldownSeconds)
            {
                _timeSinceLastStandRequest = 0;
                _session.Stand();
            }
        }

        // Send AgentUpdate at 10 Hz (every 0.1s)
        if (_timeSinceLastUpdate >= 0.1)
        {
            _timeSinceLastUpdate = 0;

            // Same body-facing quaternion applied per-frame to transform.Rotation above -- here it
            // only goes to the sim in the AgentUpdate. The rendered rotation is NOT set here anymore
            // (that write moved to the per-frame follow block so turning renders smoothly instead of
            // in 10 Hz steps).
            // We pass false for left/right because A/D are turning now, not strafing. While seated,
            // the walk/fly flags are meaningless (the seat, not agent locomotion, owns position) --
            // suppress them so a held key doesn't keep telling the sim we're trying to walk.
            _session.SetMovement(
                !isSitting && fwd, !isSitting && back, false, false,
                !isSitting && up, !isSitting && down,
                ComputeBodyRotation(), !isSitting && _flying,
                camSimPos, camSimForward, camFar);
        }
    }

    /// <summary>
    /// Whether the focused Control should swallow the movement keys.
    ///
    /// Text fields have always qualified -- typing "was" in chat must not walk the avatar. What was
    /// missing is everything else that reads the arrow keys: an OptionButton, HSlider or CheckBox
    /// inside an open settings window changes value on Left/Right/Up/Down, so with a dropdown
    /// focused the same key press both altered the setting AND moved the avatar.
    ///
    /// Scoped to Controls inside an <see cref="SLNGWindow"/> rather than "any focused Control at
    /// all". Widgets that live directly on the HUD -- the button bar, the camera controls -- are
    /// part of the world view and must not lock movement out just because one was clicked once.
    /// A window is the thing that is supposed to take over the keyboard while it is open.
    /// </summary>
    private static bool BlocksMovement(Control? focusOwner)
    {
        if (focusOwner is LineEdit || focusOwner is TextEdit) return true;
        if (focusOwner == null) return false;

        for (Node? n = focusOwner; n != null; n = n.GetParent())
        {
            if (n is SLNG.App.UI.SLNGWindow) return true;
        }
        return false;
    }

    /// <summary>The avatar's body-facing orientation from <see cref="_yaw"/> ONLY -- never the
    /// camera's full orientation, which would fold the orbit offset and pitch into the facing and
    /// make the avatar spin/tilt while orbiting. A yaw-only quaternion keeps it upright and facing
    /// where the player aims. Shared by the per-frame rendered-rotation write and the 10 Hz
    /// AgentUpdate send so the two can never diverge.</summary>
    private System.Numerics.Quaternion ComputeBodyRotation()
    {
        var godotQuat = Quaternion.FromEuler(new Vector3(0, _yaw, 0));
        var slQuat = new System.Numerics.Quaternion(godotQuat.X, -godotQuat.Z, godotQuat.Y, godotQuat.W);
        var offset = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, (float)System.Math.PI / 2.0f);
        return offset * slQuat;
    }
}
