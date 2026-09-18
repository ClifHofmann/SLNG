using Godot;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.App.UI
{
    /// <summary>
    /// FEAT-UI-04: the in-world translate manipulator — three coloured arrows at the selected
    /// object's pivot, draggable to move it along one world axis.
    ///
    /// <para><b>Axis colours are SL's, not Godot's.</b> Red/green/blue mean SL X/Y/Z, which is
    /// what every number in the edit window and every coordinate the simulator speaks is in. SL
    /// is Z-up and Godot is Y-up (<see cref="RenderConfig.ToGodot"/>), so the arrows point along
    /// Godot (1,0,0) / (0,0,-1) / (0,1,0) respectively. Getting this backwards would give a
    /// manipulator whose blue arrow moves the object sideways.</para>
    ///
    /// <para><b>Hit-testing is analytic, not physics.</b> The arrows deliberately carry no
    /// collision bodies: <see cref="ObjectSelectionController"/>'s raycast intentionally hits
    /// every layer including terrain, so a gizmo body would have had to be special-cased out of
    /// that, out of the cursor manager's picking, and out of anything else that raycasts. Instead
    /// each axis is projected to a 2D screen segment and compared against the mouse in pixels,
    /// which is also what makes a thin arrow grabbable at a distance.</para>
    /// </summary>
    public partial class SelectionGizmo3D : Node3D
    {
        public enum Axis { None, X, Y, Z }

        /// <summary>How close, in screen pixels, the cursor must be to an axis to grab it.
        /// Generous on purpose — the arrow is a few pixels wide at distance, and the reference
        /// viewer is similarly forgiving.</summary>
        private const float GrabPixels = 14f;

        /// <summary>Length of the gizmo in pixels at the screen. The world length is derived from
        /// this every frame, which is what keeps it usable both up close and far away.</summary>
        private const float ScreenLengthPixels = 110f;

        /// <summary>Minimum world movement before an intermediate update goes to the simulator.
        /// Sub-millimetre jitter from pixel-quantised cursor input would otherwise put a packet on
        /// the wire every frame of a drag.</summary>
        /// <summary>Half-length of the axis guide lines, in metres. Not literally infinite, but
        /// a region is 256 m across and the draw distance is far shorter, so at this length they
        /// leave the visible world in every direction and read as endless. Drawn as LINE
        /// primitives on purpose: a line renders one pixel wide whatever the distance, which is
        /// what makes a guide readable both at arm's length and across a parcel. A thin cylinder
        /// would vanish at range and look like a pipe up close.</summary>
        private const float GuideHalfLength = 512f;

        private const float SendEpsilon = 0.01f;

        /// <summary>Minimum gap between intermediate sends. The final position is always sent on
        /// release regardless, so this only bounds the live feedback other people see.</summary>
        private const double SendIntervalSeconds = 0.1;

        private static readonly Vector3[] AxisDirGodot =
        {
            new(1, 0, 0),   // SL +X
            new(0, 0, -1),  // SL +Y
            new(0, 1, 0),   // SL +Z
        };

        private static readonly Color[] AxisColor =
        {
            new(0.95f, 0.25f, 0.25f),
            new(0.25f, 0.90f, 0.35f),
            new(0.35f, 0.55f, 1.00f),
        };

        private Camera3D _camera = null!;
        private World _world = null!;
        private SLNG.Net.GridSession _session = null!;

        private readonly MeshInstance3D[] _arrows = new MeshInstance3D[3];
        private readonly StandardMaterial3D[] _materials = new StandardMaterial3D[3];
        private readonly MeshInstance3D[] _guides = new MeshInstance3D[3];
        private readonly StandardMaterial3D[] _guideMaterials = new StandardMaterial3D[3];

        private Entity? _entity;
        private uint _localId;
        private ulong _regionHandle;

        private Axis _hovered = Axis.None;
        private Axis _dragging = Axis.None;
        private System.Numerics.Vector3 _dragStartSlPos;
        private float _dragStartAxisT;
        /// <summary>The gizmo's world position when the drag began. The axis has to be
        /// measured from a FIXED origin: GlobalPosition follows the object as it moves, so
        /// measuring against it would fold each frame's movement back into the next frame's
        /// delta and the object would accelerate away from the cursor.</summary>
        private Vector3 _dragAxisOrigin;
        private System.Numerics.Vector3 _lastSentSlPos;
        private double _lastSendAt;

        public bool IsDragging => _dragging != Axis.None;

        /// <summary>Which entity the handles are currently on, or null. Lets a closing edit
        /// window check whether the gizmo is still its own before retracting it.</summary>
        public System.Guid? AttachedEntityId => _entity?.Id;

        public void Initialize(World world, SLNG.Net.GridSession session, Camera3D camera)
        {
            _world = world;
            _session = session;
            _camera = camera;
            BuildArrows();
            Visible = false;
            SetProcess(true);
        }

        /// <summary>Shows the gizmo on <paramref name="entity"/>, or hides it when the agent may
        /// not move that object. The permission question is the sim's own per-agent answer
        /// (FEAT-SEC-04), not the owner's mask — the same source the Position fields are gated
        /// on, so the two can never disagree.</summary>
        public void Attach(Entity? entity)
        {
            if (entity == null || !SLNG.Core.EditPermission.CanMove(_world, entity))
            {
                Detach();
                return;
            }

            _entity = entity;
            _localId = entity.LocalId;
            _regionHandle = entity.RegionHandle;
            _dragging = Axis.None;
            Visible = true;
        }

        public void Detach()
        {
            _entity = null;
            _dragging = Axis.None;
            _hovered = Axis.None;
            Visible = false;
        }

        public override void _Process(double delta)
        {
            if (_entity == null || _camera == null) { Visible = false; return; }

            // The entity can be removed from the world under us (region change, object deleted
            // by its owner) -- the gizmo must not keep floating where it was.
            var transform = _entity.GetComponent<TransformComponent>();
            if (transform == null) { Detach(); return; }

            GlobalPosition = RenderConfig.ToGodot(_regionHandle, transform.Position);

            // Constant screen size. Uses the vertical FOV and the distance along the camera's
            // forward axis rather than the straight-line distance, so the gizmo does not swell
            // as it moves towards the edge of a wide viewport.
            float depth = Mathf.Max(0.05f,
                (GlobalPosition - _camera.GlobalPosition).Dot(-_camera.GlobalBasis.Z));
            float viewportH = Mathf.Max(1f, GetViewport().GetVisibleRect().Size.Y);
            float worldPerPixel = 2f * depth * Mathf.Tan(Mathf.DegToRad(_camera.Fov) * 0.5f) / viewportH;
            float length = ScreenLengthPixels * worldPerPixel;

            for (int i = 0; i < 3; i++)
            {
                _arrows[i].Scale = new Vector3(length, length, length);
                bool lit = _dragging == (Axis)(i + 1) || (_dragging == Axis.None && _hovered == (Axis)(i + 1));
                _materials[i].AlbedoColor = lit ? Colors.White : AxisColor[i];

                // The guides are scenery until an axis is in play: faint enough not to clutter
                // the view with three full-length lines, obvious on the one being dragged.
                var guide = AxisColor[i];
                guide.A = lit ? 0.9f : 0.28f;
                _guideMaterials[i].AlbedoColor = guide;
            }
        }

        /// <summary>Which axis the cursor is over, or <see cref="Axis.None"/>. Also used to paint
        /// the hover highlight, so it is called on plain mouse motion too.</summary>
        public Axis HitTest(Vector2 mouse)
        {
            if (_entity == null || !Visible || _camera == null) return Axis.None;
            if (_camera.IsPositionBehind(GlobalPosition)) return Axis.None;

            var origin2D = _camera.UnprojectPosition(GlobalPosition);
            float bestDist = GrabPixels;
            var best = Axis.None;

            for (int i = 0; i < 3; i++)
            {
                var tipWorld = GlobalPosition + AxisDirGodot[i] * _arrows[i].Scale.X;
                // A tip behind the camera projects to a mirrored, meaningless point; skip rather
                // than compute a segment that does not exist on screen.
                if (_camera.IsPositionBehind(tipWorld)) continue;

                float d = DistanceToSegment(mouse, origin2D, _camera.UnprojectPosition(tipWorld));
                if (d < bestDist) { bestDist = d; best = (Axis)(i + 1); }
            }
            return best;
        }

        public void SetHover(Vector2 mouse) => _hovered = _dragging == Axis.None ? HitTest(mouse) : _hovered;

        /// <summary>Starts a drag if the cursor is on an axis. Returns false when it is not, so
        /// the caller can fall through to its ordinary click handling.</summary>
        public bool TryBeginDrag(Vector2 mouse)
        {
            var axis = HitTest(mouse);
            if (axis == Axis.None || _entity == null) return false;

            var transform = _entity.GetComponent<TransformComponent>();
            if (transform == null) return false;

            _dragAxisOrigin = GlobalPosition;
            if (!TryAxisParam(mouse, axis, out float t)) return false;

            _dragging = axis;
            _hovered = axis;
            _dragStartSlPos = transform.Position;
            _dragStartAxisT = t;
            _lastSentSlPos = transform.Position;
            _lastSendAt = Time.GetTicksMsec() / 1000.0;
            return true;
        }

        public void UpdateDrag(Vector2 mouse)
        {
            if (_dragging == Axis.None || _entity == null) return;
            if (!TryAxisParam(mouse, _dragging, out float t)) return;

            float delta = t - _dragStartAxisT;
            var slPos = _dragStartSlPos;
            switch (_dragging)
            {
                case Axis.X: slPos.X += delta; break;
                case Axis.Y: slPos.Y += delta; break;
                case Axis.Z: slPos.Z += delta; break;
            }

            ApplyLocal(slPos);

            double now = Time.GetTicksMsec() / 1000.0;
            if (System.Numerics.Vector3.Distance(slPos, _lastSentSlPos) >= SendEpsilon
                && now - _lastSendAt >= SendIntervalSeconds)
            {
                Send(slPos);
                _lastSendAt = now;
            }
        }

        /// <summary>Ends the drag and sends the final position unconditionally -- the throttle
        /// above can otherwise leave the simulator holding a position one interval stale, and on
        /// a short drag it may have sent nothing at all.</summary>
        public void EndDrag()
        {
            if (_dragging == Axis.None) { return; }
            _dragging = Axis.None;

            var transform = _entity?.GetComponent<TransformComponent>();
            if (transform != null) Send(transform.Position);
        }

        /// <summary>Moves the object locally and tells the world, so the mesh follows the cursor
        /// without waiting for the simulator's echo. Same optimistic pattern as
        /// <c>ObjectEditWindow.ApplyTransform</c>.</summary>
        private void ApplyLocal(System.Numerics.Vector3 slPos)
        {
            if (_entity == null) return;
            var transform = _entity.GetComponent<TransformComponent>();
            if (transform == null) return;

            transform.Position = slPos;
            _world.NotifyComponentUpdated(_entity, transform);
        }

        private void Send(System.Numerics.Vector3 slPos)
        {
            var transform = _entity?.GetComponent<TransformComponent>();
            var prim = _entity?.GetComponent<PrimitiveComponent>();
            if (transform == null) return;

            // UpdateObjectTransform writes position, rotation AND scale in one go, so the other
            // two have to be passed through unchanged rather than defaulted -- passing a default
            // scale here would resize the object on every drag frame.
            _session.UpdateObjectTransform(_localId, slPos, transform.Rotation,
                prim?.Scale ?? System.Numerics.Vector3.One);
            _lastSentSlPos = slPos;
        }

        /// <summary>Where the cursor is along the drag axis, as a distance in metres from the
        /// object's pivot.
        ///
        /// <para>Closest point between the camera ray and the axis line, NOT a raycast against a
        /// plane containing the axis: a plane becomes degenerate exactly when you look down the
        /// axis you are dragging, which is a normal thing to do and would make the object shoot
        /// off. Returns false in the one case this cannot answer either -- the ray and the axis
        /// being near-parallel, where the closest point is arbitrarily far away.</para></summary>
        private bool TryAxisParam(Vector2 mouse, Axis axis, out float t)
        {
            t = 0f;
            var ro = _camera.ProjectRayOrigin(mouse);
            var rd = _camera.ProjectRayNormal(mouse);
            var ao = _dragAxisOrigin;
            var ad = AxisDirGodot[(int)axis - 1];

            float rdDotAd = rd.Dot(ad);
            float denom = 1f - rdDotAd * rdDotAd;
            if (Mathf.Abs(denom) < 1e-5f) return false;

            var w = ao - ro;
            // Godot metres along the axis; SL and Godot agree on scale, and each axis direction
            // is a unit vector, so this is directly the SL-axis delta the caller wants.
            // Closest point between the camera ray and the axis line. With w = ao - ro this is
            // the standard tc = (e - b*d)/(1 - b^2) with the signs already folded in -- do not
            // negate it again.
            t = (rdDotAd * w.Dot(rd) - w.Dot(ad)) / denom;
            return true;
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float len2 = ab.LengthSquared();
            if (len2 < 1e-6f) return p.DistanceTo(a);
            float t = Mathf.Clamp((p - a).Dot(ab) / len2, 0f, 1f);
            return p.DistanceTo(a + ab * t);
        }

        /// <summary>One shaft + one head per axis, built once. Unshaded and depth-test-disabled so
        /// the gizmo is visible through the object it is attached to -- the same choice the
        /// reference viewer makes, and without it the handles vanish inside anything solid.</summary>
        private void BuildArrows()
        {
            for (int i = 0; i < 3; i++)
            {
                var mat = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoColor = AxisColor[i],
                    NoDepthTest = true,
                    RenderPriority = 100,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                };
                _materials[i] = mat;

                // Unit-length along +Y, then rotated onto the axis: the whole arrow is one node
                // scaled uniformly in _Process, so the screen-size maths has a single knob.
                var holder = new MeshInstance3D { Name = $"Axis{(Axis)(i + 1)}" };

                var shaft = new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = 0.012f, BottomRadius = 0.012f, Height = 0.78f, RadialSegments = 8 },
                    MaterialOverride = mat,
                    Position = new Vector3(0, 0.39f, 0),
                };
                holder.AddChild(shaft);

                var head = new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.055f, Height = 0.22f, RadialSegments = 10 },
                    MaterialOverride = mat,
                    Position = new Vector3(0, 0.89f, 0),
                };
                holder.AddChild(head);

                // +Y is the mesh's own axis; rotate it onto the SL axis this arrow represents.
                var dir = AxisDirGodot[i];
                if (dir != Vector3.Up)
                {
                    var rotAxis = Vector3.Up.Cross(dir);
                    if (rotAxis.LengthSquared() < 1e-6f) rotAxis = Vector3.Right;
                    holder.Basis = new Basis(rotAxis.Normalized(), Vector3.Up.AngleTo(dir));
                }

                _arrows[i] = holder;
                AddChild(holder);

                var guideMat = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoColor = AxisColor[i],
                    NoDepthTest = true,
                    RenderPriority = 99,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                };
                _guideMaterials[i] = guideMat;

                var line = new ImmediateMesh();
                line.SurfaceBegin(Mesh.PrimitiveType.Lines, guideMat);
                line.SurfaceAddVertex(dir * -GuideHalfLength);
                line.SurfaceAddVertex(dir * GuideHalfLength);
                line.SurfaceEnd();

                // NOT a child of the arrow holder: the holder is rescaled every frame for the
                // constant-screen-size arrows, and the guides must keep their fixed world length.
                _guides[i] = new MeshInstance3D { Name = $"Guide{(Axis)(i + 1)}", Mesh = line };
                AddChild(_guides[i]);
            }
        }
    }
}
