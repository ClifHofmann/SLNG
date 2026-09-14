using Godot;

namespace SLNG.App;

/// <summary>
/// BUG-RENDER-32: a planar mirror -- a second camera at the viewer's own position reflected
/// through the mirror plane, rendering the scene into a <see cref="SubViewport"/> that
/// <c>prim_mirror.gdshader</c> samples by screen position.
///
/// This replaces a cubemap for the one surface a cubemap can never serve. The evidence chain that
/// led here is in BUG-RENDER-23..31, but the two facts that decide it are short:
///
///   * An infinite cubemap shows an object at the angular size it had FROM THE PROBE, so its
///     apparent size on screen does not change as the viewer moves. The mirror's does. Backing
///     away therefore grows the reflection relative to the frame WITHOUT BOUND, where a real
///     mirror's ratio approaches a limit -- measured live, and the signature that ended the
///     cubemap attempts.
///   * A 512-px cube face spans 90 degrees: 5.7 pixels per degree against the screen's 32.
///
/// A planar reflection has neither problem because it is not an approximation. It is also
/// cheaper than the hero probe it replaces: one scene render per frame instead of six cube faces.
///
/// Deliberately ONE mirror. The viewer affords exactly one real-time probe for the same reason,
/// and a second full scene render per frame is not something to spend without asking.
/// </summary>
public partial class MirrorReflection : Node
{
    /// <summary>Largest render target this will allocate, per axis. A mirror is usually a fraction
    /// of the frame, and a full 4K second pass would cost more than the feature is worth; at this
    /// cap the reflection still out-resolves the 1024 cubemap it replaces by a wide margin.</summary>
    private const int MaxDimension = 1920;

    /// <summary>Below this the mirror is a sliver on screen and not worth a scene render.</summary>
    private const float MinScreenFraction = 0.01f;

    private SubViewport? _viewport;
    private Camera3D? _camera;
    private Vector2I _size;

    /// <summary>The reflected render, or null before the first update. Fed to the mirror
    /// material's <c>mirror_texture</c>.</summary>
    public Texture2D? Texture => _viewport?.GetTexture();

    /// <summary>Whether the last <see cref="UpdateFor"/> actually produced a reflection.</summary>
    public bool Active { get; private set; }

    // BUG-RENDER-33: reported on the state line, because "the mirror shows the wall" and "the
    // mirror shows nothing" are the two failure modes of this one number and look nothing alike.
    private float _lastNear;
    private float _lastCosTheta;

    /// <summary>Near plane the reflection camera last used, and the cosine of the angle between
    /// the view axis and the mirror normal that produced it.</summary>
    public float LastNear => _lastNear;
    public float LastCosTheta => _lastCosTheta;

    public override void _Ready()
    {
        _viewport = new SubViewport
        {
            Name = "MirrorViewport",

            // Always: the reflection has to follow the camera every frame, which is the entire
            // point -- a mirror that updates on a cadence is the cubemap problem again.
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            RenderTargetClearMode = SubViewport.ClearMode.Always,

            // OwnWorld3D stays FALSE: this must render the SAME world, not a copy. With its own
            // world the viewport would render an empty scene and the mirror would go black --
            // and the World3D property would still report something, which is the trap recorded
            // in AvatarRenderer's HUD viewport (use FindWorld3D to READ the world in use).
            OwnWorld3D = false,

            // No input, no audio, no second physics tick: this viewport exists to produce pixels.
            HandleInputLocally = false,
            AudioListenerEnable3D = false,
            PhysicsObjectPicking = false,

            // MSAA off: the reflection is sampled through a linear filter at a fraction of the
            // frame's size, where edge samples cost more than they show.
            Msaa3D = Viewport.Msaa.Disabled,

            Size = new Vector2I(2, 2),
        };
        AddChild(_viewport);

        _camera = new Camera3D
        {
            Name = "MirrorCamera",

            // The mirror must not render itself -- it is the one surface on this layer, and a
            // mirror facing another mirror would otherwise recurse into a frame-rate cliff.
            CullMask = 0xFFFFF & ~ObjectRenderer.MirrorVisualLayer,
        };
        _viewport.AddChild(_camera);
        _camera.Current = true;
    }

    /// <summary>Aims the reflection camera for this frame. <paramref name="normal"/> must point
    /// out of the mirror's front face; <paramref name="point"/> is any point on its plane.
    /// Returns false when there is nothing to render, in which case the caller should leave the
    /// mirror material on its ordinary shader.</summary>
    public bool UpdateFor(Camera3D main, Vector3 point, Vector3 normal, float mirrorRadius)
    {
        Active = false;
        if (_viewport == null || _camera == null || !IsInstanceValid(main)) return false;
        if (normal.LengthSquared() < 0.0001f) return false;

        normal = normal.Normalized();
        var eye = main.GlobalPosition;

        // Behind the mirror there is nothing to reflect. Signed distance, so this also covers the
        // camera having passed through the wall.
        float distance = normal.Dot(eye - point);
        if (distance <= 0.01f) return false;

        // How much of the frame does this mirror occupy? A mirror across the sim is a few pixels
        // and does not deserve a scene render of its own.
        float fraction = mirrorRadius / Mathf.Max(eye.DistanceTo(point), 0.1f);
        if (fraction < MinScreenFraction) return false;

        var frame = main.GetViewport().GetVisibleRect().Size;
        var wanted = new Vector2I(
            Mathf.Clamp((int)frame.X, 2, MaxDimension),
            Mathf.Clamp((int)frame.Y, 2, MaxDimension));
        if (wanted != _size)
        {
            _size = wanted;
            _viewport.Size = wanted;
        }

        // Reflect the eye through the plane, and the view and up vectors with it.
        var reflectedEye = eye - 2f * distance * normal;
        var forward = -main.GlobalTransform.Basis.Z;
        var up = main.GlobalTransform.Basis.Y;
        var reflectedForward = forward - 2f * forward.Dot(normal) * normal;
        var reflectedUp = up - 2f * up.Dot(normal) * normal;

        // Degenerate when the view direction is exactly the mirror normal and up collapses onto
        // it; LookAt would throw. Nudge rather than skip, so a mirror does not blink off while
        // the camera passes through that one orientation.
        if (reflectedUp.Cross(reflectedForward).LengthSquared() < 0.0001f)
        {
            reflectedUp = Mathf.Abs(normal.Y) > 0.9f ? Vector3.Forward : Vector3.Up;
        }

        _camera.GlobalPosition = reflectedEye;
        _camera.LookAt(reflectedEye + reflectedForward, reflectedUp);

        _camera.Fov = main.Fov;
        _camera.Far = main.Far;

        // BUG-RENDER-33: the near clip, measured ALONG THE VIEW AXIS rather than perpendicular.
        //
        // Godot's near plane is perpendicular to the camera's view direction; the mirror plane is
        // not, unless you happen to be looking straight at it. The first version used the
        // PERPENDICULAR distance, so the two coincided only at normal incidence -- and the further
        // the view swung to the side, the more wall was left between the reflected camera and the
        // glass. Reported exactly that way: straight on the reflection is right, "sobald ich etwas
        // zur Seite rolle zeigt der Spiegel falsche Spiegelungen" -- the wall he hangs on, filling
        // the frame.
        //
        // The distance from the reflected camera to the mirror plane along the view axis is
        // `perpendicular / cos(theta)`, which grows without limit as the view flattens; the
        // cosine is floored so a view parallel to the glass cannot push the near plane to
        // infinity and empty the reflection instead.
        //
        // NO radius margin, and that was the mistake in the first attempt at this: a mirror hangs
        // ON a wall, so the wall is at the SAME distance as the mirror plane. Backing the near
        // plane off by the mirror's radius therefore stopped clipping the wall at all -- including
        // head-on, where the previous version had been correct -- and the glass filled with wall
        // from every angle. Measured live, reverted immediately.
        //
        // Clipping exactly at the centre's along-view distance does cut some room geometry seen
        // through the nearer half of the glass at flat angles, because a plane perpendicular to
        // the view axis touches the mirror plane at one point only. That is the lesser artefact:
        // a little missing near the edge against a wall covering everything.
        //
        // The real fix is an oblique frustum, which Godot does not expose; the reference viewer
        // solves the same problem with a clip plane in every shader (`mirrorClip`, and note that
        // needs a per-VIEWPORT uniform, which a Godot shader global is not).
        float cosTheta = Mathf.Max(reflectedForward.Dot(normal), 0.05f);
        float alongView = distance / cosTheta;
        _camera.Near = Mathf.Max(0.05f, alongView);
        _lastNear = _camera.Near;
        _lastCosTheta = cosTheta;

        Active = true;
        return true;
    }

    /// <summary>Stops the render when no mirror is in view. An Always-mode viewport keeps drawing
    /// the scene every frame whether or not anything samples it, so this is not cosmetic.</summary>
    public void Idle()
    {
        Active = false;
        if (_viewport != null)
        {
            _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        }
    }

    /// <summary>Resumes rendering after <see cref="Idle"/>.</summary>
    public void Resume()
    {
        if (_viewport != null)
        {
            _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        }
    }
}
