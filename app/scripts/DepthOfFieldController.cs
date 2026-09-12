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
    private SLNG.Core.ECS.World? _world;

    // Set the first time DoF actually switches on. Godot's default DoF kernel is Box-shaped at
    // Very Low quality with no jitter -- on a high-contrast round object (a cartwheel against
    // bright grass) that reads as a stair-stepped double edge / halo, not a soft blur.
    private bool _bokehConfigured;

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

    // --- Focus-point marker (BUG-RENDER-22 diagnostic) ---------------------------------------
    // A blurred frame cannot tell you WHERE the focal plane is, only that the subject is not on
    // it, so "auto-focus picked the hillside behind the avatar" and "the blur is misconfigured"
    // look identical. These draw the answer. Two markers on purpose:
    //   cyan  -- the focal plane itself: camera position + view axis * CurrentFocusDistance. It
    //            is by construction the one thing in frame that MUST be sharp, so if the cyan
    //            sphere is crisp while the avatar is not, the focal plane is simply elsewhere.
    //   amber -- the camera's own look-at target (AvatarController.CameraTargetPoint): the
    //            avatar's head while following, or the Alt+LMB focus point. The gap between the
    //            two markers is the bug, measured.
    // Unshaded and depth-test-free so they stay visible inside geometry, and never given a
    // collider -- SampleAutoFocus raycasts Objects|Avatars, and a marker that focused the camera
    // on itself would be a very fine feedback loop.
    private Node3D? _markerRoot;
    private MeshInstance3D? _focusMarker;
    private MeshInstance3D? _targetMarker;
    private Label3D? _markerLabel;

    // Apparent size: the markers scale with distance so they stay readable at 2 m and at 200 m,
    // clamped so they neither vanish up close nor swallow the frame far away.
    private const float MarkerAngularSize = 0.014f;
    private const float MarkerMinRadius = 0.03f;
    private const float MarkerMaxRadius = 1.2f;

    public void Initialize(Camera3D camera, DofSettings settings, SLNG.Core.ECS.World? world = null)
    {
        _camera = camera;
        _settings = settings;
        _world = world;
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
            SetMarkerVisible(false);
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
        UpdateFocusMarker();
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
        // PhysicsLayers.Terrain removed: Godot's HeightMapShape3D normalizes a NaN cross-product
        // when an angled ray crosses an unstreamed cell, printing "Vector3 cannot be normalized"
        // every single time. At 20 Hz this flooded the console. We raycast objects/avatars
        // natively, then manually trace the terrain heightmap below.
        query.CollisionMask = PhysicsLayers.Objects | PhysicsLayers.Avatars;

        var hit = spaceState.IntersectRay(query);
        float depth = MaxFocusRayLength;
        bool hitSomething = false;

        if (hit.Count > 0)
        {
            hitSomething = true;
            // The blur is keyed off VIEW-SPACE depth, so the focal plane wants the hit's distance
            // ALONG the view axis, not its euclidean distance from the camera. The two are identical
            // dead ahead and diverge toward the edges of a wide FOV -- which is exactly where a
            // euclidean reading would put the plane slightly too far and soften the subject it just
            // focused on.
            depth = (hit["position"].AsVector3() - origin).Dot(normal);
        }

        if (_world != null)
        {
            float step = 2.0f; // 2m resolution is plenty for DoF
            for (float d = 0f; d < depth; d += step)
            {
                var pos = origin + normal * d;
                double gx = pos.X + RenderConfig.OriginX;
                double gy = -pos.Z + RenderConfig.OriginY;

                bool hitTerrain = false;
                foreach (var kvp in _world.Terrains)
                {
                    var t = kvp.Value;
                    uint rx = (uint)(kvp.Key >> 32);
                    uint ry = (uint)(kvp.Key & 0xFFFFFFFF);
                    
                    if (gx >= rx && gx < rx + t.Width && gy >= ry && gy < ry + t.Height)
                    {
                        if (t.TryGetKnownHeight((int)(gx - rx), (int)(gy - ry), out float h))
                        {
                            if (pos.Y <= h)
                            {
                                depth = d;
                                hitSomething = true;
                                hitTerrain = true;
                                break;
                            }
                        }
                        break; // Found the region, no need to check others
                    }
                }
                if (hitTerrain) break;
            }
        }

        if (!hitSomething)
        {
            AutoFocusHasTarget = false;
            _autoFocusTarget = _settings.FocusDistance;
            return;
        }

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

        if (!_bokehConfigured)
        {
            // Circle kernel + a real sample count + jitter: the fix for the halo/double-edge on
            // round high-contrast objects. Jitter trades faint residual ringing for a slight
            // fuzziness, which is what a photographic bokeh looks like anyway. This is global
            // render state, but it only has any effect while a CameraAttributes with DoF is on
            // the active camera -- which is exactly this controller and nothing else -- so there
            // is nothing to put back when DoF turns off.
            RenderingServer.CameraAttributesSetDofBlurBokehShape(RenderingServer.DofBokehShape.Circle);
            RenderingServer.CameraAttributesSetDofBlurQuality(RenderingServer.DofBlurQuality.High, useJitter: true);
            _bokehConfigured = true;
        }

        float focus = _settings.AutoFocus ? CurrentFocusDistance : _settings.FocusDistance;
        float half = Mathf.Max(_settings.FocusRange * 0.5f, 0.05f);

        _attributes.DofBlurAmount = _settings.BlurAmount;

        // A real lens's circle of confusion keeps growing with distance from the focal plane;
        // CameraAttributesPractical only offers ONE linear ramp (blur climbs from `far_distance`
        // over `far_transition` metres, then flat) to stand in for that whole curve. The
        // "Falloff" slider is how long that ramp is, as a multiple of the focal distance:
        //   0   -> 0.15x  -- full blur almost immediately past the sharp band (a hard cut)
        //   1   -> 30x    -- the ramp outruns any normal view, so within frame the blur is
        //                    always still climbing and never flattens into a uniform wash
        // Scaling by the focal distance keeps a near focus tighter than a far one at the same
        // slider value. (A true f-stop model would be CameraAttributesPhysical, which also takes
        // over exposure -- deliberately not going there.)
        float rampMult = Mathf.Lerp(0.15f, 30f, _settings.Falloff);
        float falloff = Mathf.Max(focus * rampMult, 0.3f);

        // Far zone: sharp out to focus+half, then the blur ramps in across `falloff` metres.
        _attributes.DofBlurFarEnabled = true;
        _attributes.DofBlurFarDistance = focus + half;
        _attributes.DofBlurFarTransition = falloff;

        // Near zone: mirrors it, but the foreground only has focus-half metres of room before the
        // camera, so the ramp is capped to that. Clamped above zero because a near distance of 0
        // puts the ramp behind the camera and Godot then blurs the whole frame.
        float near = Mathf.Max(focus - half, 0.05f);
        _attributes.DofBlurNearEnabled = _settings.NearBlur;
        _attributes.DofBlurNearDistance = near;
        _attributes.DofBlurNearTransition = Mathf.Max(Mathf.Min(falloff, near), 0.1f);
    }

    /// <summary>Places the focal-plane and camera-target markers for this frame, building them on
    /// first use. Cheap enough to run per frame (two transform writes and a label string), and it
    /// only runs at all while the user has the toggle on.</summary>
    private void UpdateFocusMarker()
    {
        if (_camera == null || _settings == null) return;

        if (!_settings.ShowFocusMarker)
        {
            SetMarkerVisible(false);
            return;
        }

        EnsureMarkers();
        if (_markerRoot == null || _focusMarker == null || _targetMarker == null || _markerLabel == null) return;
        _markerRoot.Visible = true;

        var camXform = _camera.GlobalTransform;
        var origin = camXform.Origin;
        var forward = -camXform.Basis.Z;

        float focus = _settings.AutoFocus ? CurrentFocusDistance : _settings.FocusDistance;
        var focusPoint = origin + forward * focus;
        _focusMarker.GlobalPosition = focusPoint;
        _focusMarker.Scale = Vector3.One * MarkerRadius(focus);

        // The camera target only exists in the third-person rig. In free-camera mode, or before
        // login, AvatarController never writes it, and a marker parked at the world origin would
        // be a lie -- hide it instead.
        float targetDistance = -1f;
        if (_camera is AvatarController rig && rig.CameraTargetPoint != Vector3.Zero)
        {
            var targetPoint = rig.CameraTargetPoint;
            targetDistance = (targetPoint - origin).Dot(forward);
            _targetMarker.Visible = true;
            _targetMarker.GlobalPosition = targetPoint;
            _targetMarker.Scale = Vector3.One * (MarkerRadius(Mathf.Max(targetDistance, 0.1f)) * 0.6f);
        }
        else
        {
            _targetMarker.Visible = false;
        }

        // Just in front of the focal plane, so the readout is never the first thing the blur eats.
        _markerLabel.GlobalPosition = focusPoint - forward * (MarkerRadius(focus) * 2.5f);

        string mode = _settings.AutoFocus
            ? (AutoFocusHasTarget ? L10n.Tr("ui.snapshot.dof_marker_auto") : L10n.Tr("ui.snapshot.dof_no_target"))
            : L10n.Tr("ui.snapshot.dof_marker_manual");
        string text = $"{L10n.Tr("ui.snapshot.dof_marker_focus")}: {focus:0.0} m  ({mode})";
        if (targetDistance > 0f)
        {
            text += "\n"
                  + $"{L10n.Tr("ui.snapshot.dof_marker_target")}: {targetDistance:0.0} m"
                  + $"  (delta {focus - targetDistance:+0.0;-0.0;0.0} m)";
        }
        if (_markerLabel.Text != text) _markerLabel.Text = text;
    }

    private static float MarkerRadius(float distance)
        => Mathf.Clamp(distance * MarkerAngularSize, MarkerMinRadius, MarkerMaxRadius);

    private void SetMarkerVisible(bool visible)
    {
        if (_markerRoot != null) _markerRoot.Visible = visible;
    }

    private void EnsureMarkers()
    {
        if (_markerRoot != null) return;

        // TopLevel: the markers are positioned in world space, and this controller is a plain Node
        // under Boot (a Control), so there is no Node3D ancestor to inherit from anyway -- saying
        // so explicitly keeps that true if the tree ever changes.
        _markerRoot = new Node3D { Name = "DofFocusMarker", TopLevel = true };
        AddChild(_markerRoot);

        _focusMarker = BuildSphere("DofFocalPlane", new Color(0.1f, 0.9f, 1f));
        _markerRoot.AddChild(_focusMarker);

        _targetMarker = BuildSphere("DofCameraTarget", new Color(1f, 0.65f, 0.1f));
        _markerRoot.AddChild(_targetMarker);

        _markerLabel = new Label3D
        {
            Name = "DofFocusReadout",
            TopLevel = true,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FixedSize = true,
            NoDepthTest = true,
            PixelSize = 0.0006f,
            FontSize = 48,
            OutlineSize = 12,
            Modulate = new Color(0.1f, 0.9f, 1f),
            OutlineModulate = new Color(0f, 0f, 0f, 0.8f),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _markerRoot.AddChild(_markerLabel);
    }

    private static MeshInstance3D BuildSphere(string name, Color color)
    {
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = color,
            NoDepthTest = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        return new MeshInstance3D
        {
            Name = name,
            TopLevel = true,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // Radius 1 / height 2 -- the unit sphere the per-frame Scale above works on in metres.
            Mesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 16, Rings = 8, Material = material },
        };
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
