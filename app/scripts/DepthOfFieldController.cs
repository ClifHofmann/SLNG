using Godot;
using SLNG.App.UI;

namespace SLNG.App;

/// <summary>
/// FEAT-RENDER-07: drives Godot's depth-of-field blur from <see cref="DofSettings"/>, including
/// the auto-focus raycast.
///
/// Owns the camera's <c>CameraAttributesPractical</c> rather than letting the UI touch it, for
/// two reasons. First, Godot's DoF is expressed as two independent blur zones (everything nearer
/// than X, everything further than Y) while the user-facing model is a focal plane with a sharp
/// band around it -- that translation lives in <see cref="Apply"/> and nowhere else. Second,
/// turning the effect off has to actually detach the resource (<c>camera.Attributes = null</c>),
/// not merely zero the blur amount: a CameraAttributes left assigned keeps its exposure model in
/// the pipeline, and "toggles cleanly on and off" is an acceptance criterion of this task.
/// </summary>
public partial class DepthOfFieldController : Node
{
    private Camera3D? _camera;
    private DofSettings? _settings;
    private CameraAttributesPractical? _attributes;

    /// <summary>The focal distance actually in use this frame -- the manual setting, or the
    /// smoothed auto-focus reading. The Snapshot window shows it so auto-focus is observable.</summary>
    public float CurrentFocusDistance { get; private set; } = DofSettings.DefaultFocusDistance;

    /// <summary>Whether the last auto-focus ray actually hit something. Reported in the UI, because
    /// "auto-focus is on but there is nothing in the centre of frame" and "auto-focus is broken"
    /// look identical otherwise.</summary>
    public bool AutoFocusHasTarget { get; private set; }

    // Auto-focus raycast cadence. Not per-frame, and not for the ray's own cost -- a single
    // IntersectRay is cheap. It is the terrain heightfield: TerrainRenderer marks unstreamed
    // patches as NaN so a ray MISSES rather than reporting a floor at 0, and Godot's heightfield
    // raycast runs a normalize() over that NaN cell en route to the miss, printing "Vector3 cannot
    // be normalized" every single time. That is the flood PhysicsLayers.Terrain was split out to
    // spare CursorManager from (45,862 of one session's 45,872 warnings came from its per-frame
    // hover ray). Unlike CursorManager this query genuinely needs terrain -- focusing on open
    // ground is a normal thing to photograph -- so it pays the cost at ~20 Hz instead of ~60, and
    // only while the user has DoF and auto-focus switched on. 20 Hz is far above what the focus
    // smoothing below can resolve anyway.
    private const double RaycastIntervalSeconds = 0.05;
    private double _sinceLastRaycast;

    // How far the centre ray looks for a subject. Deliberately its own number rather than
    // RenderConfig.DrawDistance: past ~256 m the far blur zone is beyond anything the blur can
    // resolve, and a longer ray only crosses more unstreamed terrain patches (see above).
    private const float MaxFocusRayLength = 256f;

    // Exponential smoothing half-life for the focal plane, in seconds. A raw per-sample focus
    // snaps hard the moment the centre of frame crosses an edge (an avatar walks past, the camera
    // pans off a wall onto open sky), which reads as a glitch rather than as a camera focusing.
    private const float FocusSmoothingHalfLife = 0.12f;

    private float _autoFocusTarget = DofSettings.DefaultFocusDistance;

    public void Initialize(Camera3D camera, DofSettings settings)
    {
        _camera = camera;
        _settings = settings;
        CurrentFocusDistance = settings.FocusDistance;
        _autoFocusTarget = settings.FocusDistance;
        Apply();
    }

    public override void _Process(double delta)
    {
        if (_camera == null || _settings == null) return;

        if (!_settings.Enabled)
        {
            Detach();
            return;
        }

        if (_settings.AutoFocus)
        {
            _sinceLastRaycast += delta;
            if (_sinceLastRaycast >= RaycastIntervalSeconds)
            {
                _sinceLastRaycast = 0;
                SampleAutoFocus();
            }
            CurrentFocusDistance = Smooth(CurrentFocusDistance, _autoFocusTarget, (float)delta);
        }
        else
        {
            AutoFocusHasTarget = false;
            _autoFocusTarget = _settings.FocusDistance;
            CurrentFocusDistance = _settings.FocusDistance;
        }

        Apply();
    }

    /// <summary>Casts through the centre of the viewport and records the distance to whatever is
    /// there. With no hit the target eases back to the manual <see cref="DofSettings.FocusDistance"/>
    /// rather than jumping to infinity -- pointing the camera at empty sky should not throw the
    /// whole frame out of focus.</summary>
    private void SampleAutoFocus()
    {
        if (_camera == null || _settings == null) return;

        var world = _camera.GetWorld3D();
        if (world?.DirectSpaceState is not { } spaceState)
        {
            AutoFocusHasTarget = false;
            return;
        }

        var centre = _camera.GetViewport().GetVisibleRect().Size * 0.5f;
        var origin = _camera.ProjectRayOrigin(centre);
        var normal = _camera.ProjectRayNormal(centre);
        if (!origin.IsFinite() || !normal.IsFinite())
        {
            AutoFocusHasTarget = false;
            return;
        }

        var query = PhysicsRayQueryParameters3D.Create(origin, origin + normal * MaxFocusRayLength);
        // Same mask as AvatarController's Alt-click focus ray: anything you would want to point a
        // camera at. Terrain is on its own bit and has to be named explicitly.
        query.CollisionMask = PhysicsLayers.Objects | PhysicsLayers.Terrain | PhysicsLayers.Avatars;

        var hit = spaceState.IntersectRay(query);
        if (hit.Count == 0)
        {
            AutoFocusHasTarget = false;
            _autoFocusTarget = _settings.FocusDistance;
            return;
        }

        // The blur is keyed off VIEW-SPACE depth, so the focal plane wants the hit's distance
        // ALONG the view axis, not its euclidean distance from the camera. The two are identical
        // dead ahead and diverge toward the edges of a wide FOV -- which is exactly where a
        // euclidean reading would put the plane slightly too far and soften the subject it just
        // focused on.
        float depth = (hit["position"].AsVector3() - origin).Dot(normal);
        AutoFocusHasTarget = true;
        _autoFocusTarget = Mathf.Clamp(depth, DofSettings.MinFocusDistance, DofSettings.MaxFocusDistance);
    }

    /// <summary>Frame-rate independent exponential ease, expressed as a half-life so the feel does
    /// not change with the frame rate (a plain <c>Lerp(a, b, k)</c> per frame does).</summary>
    private static float Smooth(float current, float target, float delta)
    {
        if (delta <= 0f) return current;
        float t = 1f - Mathf.Pow(0.5f, delta / FocusSmoothingHalfLife);
        return Mathf.Lerp(current, target, Mathf.Clamp(t, 0f, 1f));
    }

    /// <summary>Translates the focal-plane model into Godot's two blur zones and pushes it onto
    /// the camera. Called every frame while enabled, and once on any settings change, so a slider
    /// move is live.</summary>
    public void Apply()
    {
        if (_camera == null || _settings == null) return;

        if (!_settings.Enabled)
        {
            Detach();
            return;
        }

        _attributes ??= new CameraAttributesPractical();
        if (_camera.Attributes != _attributes) _camera.Attributes = _attributes;

        float focus = _settings.AutoFocus ? CurrentFocusDistance : _settings.FocusDistance;
        float half = Mathf.Max(_settings.FocusRange * 0.5f, 0.05f);

        _attributes.DofBlurAmount = _settings.BlurAmount;

        // Far zone: everything beyond the sharp band, fading in over a distance equal to the band's
        // own half-width so a narrow focus also has a tight falloff.
        _attributes.DofBlurFarEnabled = true;
        _attributes.DofBlurFarDistance = focus + half;
        _attributes.DofBlurFarTransition = Mathf.Max(half, 0.1f);

        // Near zone: mirrors the far one. Clamped above zero because a near distance of 0 puts the
        // blur's ramp behind the camera and Godot then blurs the whole frame.
        _attributes.DofBlurNearEnabled = _settings.NearBlur;
        _attributes.DofBlurNearDistance = Mathf.Max(focus - half, 0.05f);
        _attributes.DofBlurNearTransition = Mathf.Max(half, 0.1f);
    }

    private void Detach()
    {
        if (_camera != null && _attributes != null && _camera.Attributes == _attributes)
            _camera.Attributes = null;
    }

    public override void _ExitTree()
    {
        Detach();
        base._ExitTree();
    }
}
