using Godot;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.App.UI
{
    /// <summary>
    /// FEAT-UI-04: the in-world translate manipulator — three coloured arrows at the selected
    /// object's pivot for single-axis moves, three corner triangles for two-axis moves, and a
    /// guide line along each axis.
    ///
    /// <para><b>Axis colours are SL's, not Godot's.</b> Red/green/blue mean SL X/Y/Z, which is
    /// what every number in the edit window and every coordinate the simulator speaks is in. SL
    /// is Z-up and Godot is Y-up (<see cref="RenderConfig.ToGodot"/>), so the arrows point along
    /// Godot (1,0,0) / (0,0,-1) / (0,1,0) respectively. Getting this backwards would give a
    /// manipulator whose blue arrow moves the object sideways.</para>
    ///
    /// <para><b>Hit-testing is analytic, not physics.</b> The handles deliberately carry no
    /// collision bodies: <see cref="ObjectSelectionController"/>'s raycast intentionally hits
    /// every layer including terrain, so a gizmo body would have had to be special-cased out of
    /// that, out of the cursor manager's picking, and out of anything else that raycasts. Each
    /// axis is projected to a 2D screen segment and compared against the mouse in pixels, and
    /// each plane triangle to a 2D triangle — which is also what makes a thin arrow grabbable at
    /// a distance.</para>
    /// </summary>
    public partial class SelectionGizmo3D : Node3D
    {
        /// <summary>A single-axis handle (X/Y/Z) or a two-axis plane handle. A plane is named by
        /// the two axes it moves in; its normal is the third.</summary>
        public enum Handle { None, X, Y, Z, PlaneXY, PlaneXZ, PlaneYZ }

        /// <summary>How close, in screen pixels, the cursor must be to an axis to grab it.
        /// Generous on purpose — the arrow is a few pixels wide at distance, and the reference
        /// viewer is similarly forgiving.</summary>
        private const float GrabPixels = 14f;

        /// <summary>Length of the gizmo in pixels at the screen. The world length is derived from
        /// this every frame, which is what keeps it usable both up close and far away.</summary>
        private const float ScreenLengthPixels = 110f;

        /// <summary>Where the plane triangle sits along each of its two axes, and how big it is,
        /// as a fraction of the arrow length. Kept well clear of the origin so the three axis
        /// handles stay grabbable between them.</summary>
        private const float PlaneInner = 0.26f;
        private const float PlaneOuter = 0.52f;

        /// <summary>Half-length of the axis guide lines, in metres. Not literally infinite, but
        /// a region is 256 m across and the draw distance is far shorter, so at this length they
        /// leave the visible world in every direction and read as endless. Drawn as LINE
        /// primitives on purpose: a line renders one pixel wide whatever the distance, which is
        /// what makes a guide readable both at arm's length and across a parcel. A thin cylinder
        /// would vanish at range and look like a pipe up close.</summary>
        private const float GuideHalfLength = 512f;

        /// <summary>The drag grid's cell size: 1 m, matching SL's own metre-based build grid.
        /// Shown only on the plane actually being dragged — a grid on all three at once is
        /// unreadable, and on a plane you are not using it is noise.</summary>
        private const float GridSpacing = 1f;

        /// <summary>How far the grid reaches, as a multiple of the camera's distance to the
        /// object, clamped to this range in metres. Sized at drag start rather than fixed: a
        /// grid that ends inside the visible area looks like a rug, and one large enough for a
        /// distant view is a moiré haze up close. The reference viewer's covers the view, which
        /// is what this reproduces.</summary>
        private const float GridExtentPerDistance = 1.6f;
        private const float GridExtentMin = 12f;
        private const float GridExtentMax = 96f;

        /// <summary>Minimum world movement before an intermediate update goes to the simulator.
        /// Sub-millimetre jitter from pixel-quantised cursor input would otherwise put a packet on
        /// the wire every frame of a drag.</summary>
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

        /// <summary>The two axis indices each plane handle moves in. Index order matches
        /// <see cref="Handle.PlaneXY"/>, <c>PlaneXZ</c>, <c>PlaneYZ</c>; the remaining axis is the
        /// plane's normal and gives it its colour, which is the convention every DCC tool uses.
        /// </summary>
        private static readonly (int A, int B, int Normal)[] Planes =
        {
            (0, 1, 2), // XY, normal Z
            (0, 2, 1), // XZ, normal Y
            (1, 2, 0), // YZ, normal X
        };

        private Camera3D _camera = null!;
        private World _world = null!;
        private SLNG.Net.GridSession _session = null!;

        private readonly MeshInstance3D[] _arrows = new MeshInstance3D[3];
        private readonly StandardMaterial3D[] _materials = new StandardMaterial3D[3];
        private readonly MeshInstance3D[] _guides = new MeshInstance3D[3];
        private readonly StandardMaterial3D[] _guideMaterials = new StandardMaterial3D[3];
        private readonly MeshInstance3D[] _planeQuads = new MeshInstance3D[3];
        private readonly StandardMaterial3D[] _planeMaterials = new StandardMaterial3D[3];
        private readonly MeshInstance3D[] _grids = new MeshInstance3D[3];
        private readonly StandardMaterial3D[] _gridMaterials = new StandardMaterial3D[3];

        private Entity? _entity;
        private uint _localId;
        private ulong _regionHandle;

        private Handle _hovered = Handle.None;
        private Handle _dragging = Handle.None;
        private System.Numerics.Vector3 _dragStartSlPos;
        private float _dragStartAxisT;
        private Vector3 _dragStartPlanePoint;

        /// <summary>The gizmo's world position when the drag began. The axis has to be measured
        /// from a FIXED origin: GlobalPosition follows the object as it moves, so measuring
        /// against it would fold each frame's movement back into the next frame's delta and the
        /// object would accelerate away from the cursor.</summary>
        private Vector3 _dragAxisOrigin;

        private System.Numerics.Vector3 _lastSentSlPos;
        private double _lastSendAt;

        public bool IsDragging => _dragging != Handle.None;

        /// <summary>Which entity the handles are currently on, or null. Lets a closing edit
        /// window check whether the gizmo is still its own before retracting it.</summary>
        public System.Guid? AttachedEntityId => _entity?.Id;

        public void Initialize(World world, SLNG.Net.GridSession session, Camera3D camera)
        {
            _world = world;
            _session = session;
            _camera = camera;
            BuildHandles();
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
            _dragging = Handle.None;
            Visible = true;
        }

        public void Detach()
        {
            // Releasing ownership here as well: a Detach mid-drag (window closed, object gone)
            // would otherwise leave the flag set and that object permanently deaf to the sim.
            var transform = _entity?.GetComponent<TransformComponent>();
            if (transform != null) transform.LocallyDragged = false;

            _entity = null;
            _dragging = Handle.None;
            _hovered = Handle.None;
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

            var active = _dragging != Handle.None ? _dragging : _hovered;

            for (int i = 0; i < 3; i++)
            {
                _arrows[i].Scale = new Vector3(length, length, length);
                _planeQuads[i].Scale = new Vector3(length, length, length);

                bool axisLit = active == (Handle)(i + 1);
                _materials[i].AlbedoColor = axisLit ? Colors.White : AxisColor[i];

                // The guides are scenery until an axis is in play: faint enough not to clutter
                // the view with three full-length lines, obvious on the one being dragged.
                var guide = AxisColor[i];
                guide.A = axisLit ? 0.9f : 0.28f;
                _guideMaterials[i].AlbedoColor = guide;

                bool planeLit = active == PlaneHandle(i);
                var plane = AxisColor[Planes[i].Normal];
                plane.A = planeLit ? 0.65f : 0.30f;
                _planeMaterials[i].AlbedoColor = plane;

                // Only while actually dragging: a grid that appeared on hover would flash on and
                // off as the cursor crosses the handle.
                bool gridOn = _dragging == PlaneHandle(i);
                _grids[i].Visible = gridOn;
                if (gridOn) _grids[i].GlobalPosition = SnappedGridOrigin(i, GlobalPosition);
            }
        }

        private static Handle PlaneHandle(int i) => (Handle)((int)Handle.PlaneXY + i);

        /// <summary>Where plane <paramref name="i"/>'s grid patch should sit so that it runs
        /// through the object while its LINES stay still.
        ///
        /// <para>The patch follows the object -- a grid the object has slid off is no longer
        /// telling you anything about the object. But its offset along the two in-plane axes is
        /// snapped to whole metres, so the lines land on the same world coordinates whatever the
        /// object's fractional position: they stay put and the object moves across them, instead
        /// of the whole grid sliding with the cursor and appearing motionless.</para></summary>
        private Vector3 SnappedGridOrigin(int i, Vector3 objectPos)
        {
            var (ia, ib, inormal) = Planes[i];
            var a = AxisDirGodot[ia];
            var b = AxisDirGodot[ib];
            var n = AxisDirGodot[inormal];

            float alongA = Mathf.Round(objectPos.Dot(a) / GridSpacing) * GridSpacing;
            float alongB = Mathf.Round(objectPos.Dot(b) / GridSpacing) * GridSpacing;
            // The normal component is NOT snapped: the grid has to lie exactly in the plane the
            // object is moving in, not a metre above or below it.
            return a * alongA + b * alongB + n * objectPos.Dot(n);
        }

        /// <summary>Which handle the cursor is over, or <see cref="Handle.None"/>. Also used to
        /// paint the hover highlight, so it is called on plain mouse motion too.</summary>
        public Handle HitTest(Vector2 mouse)
        {
            if (_entity == null || !Visible || _camera == null) return Handle.None;
            if (_camera.IsPositionBehind(GlobalPosition)) return Handle.None;

            var origin2D = _camera.UnprojectPosition(GlobalPosition);
            float scale = _arrows[0].Scale.X;

            // Planes first. Their triangles sit between two axes and partly under them, and a
            // cursor inside a triangle means the user is aiming at the plane -- the reverse
            // priority makes the plane handles nearly unclickable.
            for (int i = 0; i < 3; i++)
            {
                if (!TryPlaneCorners2D(i, scale, out var p0, out var p1, out var p2)) continue;
                if (PointInTriangle(mouse, p0, p1, p2)) return PlaneHandle(i);
            }

            float bestDist = GrabPixels;
            var best = Handle.None;
            for (int i = 0; i < 3; i++)
            {
                var tipWorld = GlobalPosition + AxisDirGodot[i] * scale;
                // A tip behind the camera projects to a mirrored, meaningless point; skip rather
                // than compute a segment that does not exist on screen.
                if (_camera.IsPositionBehind(tipWorld)) continue;

                float d = DistanceToSegment(mouse, origin2D, _camera.UnprojectPosition(tipWorld));
                if (d < bestDist) { bestDist = d; best = (Handle)(i + 1); }
            }
            return best;
        }

        public void SetHover(Vector2 mouse) => _hovered = _dragging == Handle.None ? HitTest(mouse) : _hovered;

        /// <summary>Starts a drag if the cursor is on a handle. Returns false when it is not, so
        /// the caller can fall through to its ordinary click handling.</summary>
        public bool TryBeginDrag(Vector2 mouse)
        {
            var handle = HitTest(mouse);
            if (handle == Handle.None || _entity == null) return false;

            var transform = _entity.GetComponent<TransformComponent>();
            if (transform == null) return false;

            _dragAxisOrigin = GlobalPosition;

            if (IsPlane(handle))
            {
                if (!TryPlanePoint(mouse, handle, out var hit)) return false;
                _dragStartPlanePoint = hit;

                // Park the grid where the object started. TopLevel means it keeps this position
                // for the whole drag instead of being dragged along.
                int gi = (int)handle - (int)Handle.PlaneXY;
                _grids[gi].GlobalPosition = _dragAxisOrigin;
                BuildGridMesh(gi, GridExtentFor(_dragAxisOrigin));
            }
            else
            {
                if (!TryAxisParam(mouse, handle, out float t)) return false;
                _dragStartAxisT = t;
            }

            _dragging = handle;
            _hovered = handle;
            transform.LocallyDragged = true;
            _dragStartSlPos = transform.Position;
            _lastSentSlPos = transform.Position;
            _lastSendAt = Time.GetTicksMsec() / 1000.0;
            return true;
        }

        public void UpdateDrag(Vector2 mouse)
        {
            if (_dragging == Handle.None || _entity == null) return;

            var slPos = _dragStartSlPos;

            if (IsPlane(_dragging))
            {
                if (!TryPlanePoint(mouse, _dragging, out var hit)) return;
                var deltaGodot = hit - _dragStartPlanePoint;

                // Back to SL axes. Godot (x, y, z) maps to SL (x, -z, y); done componentwise
                // rather than via FromGodot so this stays a pure delta with no region origin in
                // it -- FromGodot would subtract the origin twice.
                slPos.X += deltaGodot.X;
                slPos.Y += -deltaGodot.Z;
                slPos.Z += deltaGodot.Y;
            }
            else
            {
                if (!TryAxisParam(mouse, _dragging, out float t)) return;
                float delta = t - _dragStartAxisT;
                switch (_dragging)
                {
                    case Handle.X: slPos.X += delta; break;
                    case Handle.Y: slPos.Y += delta; break;
                    case Handle.Z: slPos.Z += delta; break;
                }
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
            if (_dragging == Handle.None) { return; }
            _dragging = Handle.None;

            var transform = _entity?.GetComponent<TransformComponent>();
            if (transform != null)
            {
                Send(transform.Position);
                // Hand authority back. TargetPosition is moved with it so the very next network
                // packet does not ease the object away from where the user just dropped it.
                transform.TargetPosition = transform.Position;
                transform.LocallyDragged = false;
            }
        }

        private static bool IsPlane(Handle h) => h >= Handle.PlaneXY;

        /// <summary>Moves the object locally and tells the world, so the mesh follows the cursor
        /// without waiting for the simulator's echo. Same optimistic pattern as
        /// <c>ObjectEditWindow.ApplyTransform</c>.</summary>
        private void ApplyLocal(System.Numerics.Vector3 slPos)
        {
            if (_entity == null) return;
            var transform = _entity.GetComponent<TransformComponent>();
            if (transform == null) return;

            transform.Position = slPos;

            // LocalPosition too, not just Position: ApplyObjectUpdate recomputes Position from
            // LocalPosition via ResolveWorldTransform, so writing only the world position means
            // the very next update for this object -- a texture change, a flag toggle, anything
            // -- silently restores where it used to be. For an unparented prim the two are the
            // same value. A CHILD prim's local position is relative to its root and would need
            // the inverse compose; the gizmo does not offer that yet (neither do the numeric
            // fields), and linked-part editing is FEAT-UI-06's problem.
            if (transform.ParentLocalId == 0) transform.LocalPosition = slPos;

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
        private bool TryAxisParam(Vector2 mouse, Handle axis, out float t)
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
            // Closest point between the camera ray and the axis line. With w = ao - ro this is
            // the standard tc = (e - b*d)/(1 - b^2) with the signs already folded in -- do not
            // negate it again.
            t = (rdDotAd * w.Dot(rd) - w.Dot(ad)) / denom;
            return true;
        }

        /// <summary>Where the cursor ray meets the drag plane, in Godot world space. Returns
        /// false when the ray is near-parallel to the plane — sighting along a plane edge-on,
        /// where the intersection runs off to infinity and the object would jump.</summary>
        private bool TryPlanePoint(Vector2 mouse, Handle handle, out Vector3 hit)
        {
            hit = Vector3.Zero;
            var normal = AxisDirGodot[Planes[(int)handle - (int)Handle.PlaneXY].Normal];
            var ro = _camera.ProjectRayOrigin(mouse);
            var rd = _camera.ProjectRayNormal(mouse);

            float denom = rd.Dot(normal);
            if (Mathf.Abs(denom) < 1e-4f) return false;

            float t = (_dragAxisOrigin - ro).Dot(normal) / denom;
            if (t <= 0f) return false; // the plane is behind the camera

            hit = ro + rd * t;
            return true;
        }

        /// <summary>The plane handle's three screen-space corners, or false if any of them is
        /// behind the camera (where UnprojectPosition returns a mirrored, meaningless point).
        /// </summary>
        private bool TryPlaneCorners2D(int i, float scale, out Vector2 p0, out Vector2 p1, out Vector2 p2)
        {
            p0 = p1 = p2 = Vector2.Zero;
            var a = AxisDirGodot[Planes[i].A];
            var b = AxisDirGodot[Planes[i].B];

            var w0 = GlobalPosition + (a * PlaneInner + b * PlaneInner) * scale;
            var w1 = GlobalPosition + (a * PlaneOuter + b * PlaneInner) * scale;
            var w2 = GlobalPosition + (a * PlaneInner + b * PlaneOuter) * scale;

            if (_camera.IsPositionBehind(w0) || _camera.IsPositionBehind(w1) || _camera.IsPositionBehind(w2))
                return false;

            p0 = _camera.UnprojectPosition(w0);
            p1 = _camera.UnprojectPosition(w1);
            p2 = _camera.UnprojectPosition(w2);
            return true;
        }

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Sign(p, a, b), d2 = Sign(p, b, c), d3 = Sign(p, c, a);
            bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
            bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
            // The point is inside when it is on the same side of all three edges, i.e. the edge
            // signs are not mixed. Works for either winding, which matters because the triangle
            // flips as the camera passes its plane.
            return !(hasNeg && hasPos);

            static float Sign(Vector2 p, Vector2 a, Vector2 b)
                => (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float len2 = ab.LengthSquared();
            if (len2 < 1e-6f) return p.DistanceTo(a);
            float t = Mathf.Clamp((p - a).Dot(ab) / len2, 0f, 1f);
            return p.DistanceTo(a + ab * t);
        }

        /// <summary>Builds the arrows, plane triangles, guide lines and drag grids once.
        /// Everything is unshaded and depth-test-disabled so the gizmo stays visible through the
        /// object it is attached to -- the same choice the reference viewer makes, and without it
        /// the handles vanish inside anything solid.</summary>
        private void BuildHandles()
        {
            for (int i = 0; i < 3; i++)
            {
                var dir = AxisDirGodot[i];

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
                var holder = new MeshInstance3D { Name = $"Axis{(Handle)(i + 1)}" };

                holder.AddChild(new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = 0.012f, BottomRadius = 0.012f, Height = 0.78f, RadialSegments = 8 },
                    MaterialOverride = mat,
                    Position = new Vector3(0, 0.39f, 0),
                });

                holder.AddChild(new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.055f, Height = 0.22f, RadialSegments = 10 },
                    MaterialOverride = mat,
                    Position = new Vector3(0, 0.89f, 0),
                });

                // +Y is the mesh's own axis; rotate it onto the SL axis this arrow represents.
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
                _guides[i] = new MeshInstance3D { Name = $"Guide{(Handle)(i + 1)}", Mesh = line };
                AddChild(_guides[i]);
            }

            for (int i = 0; i < 3; i++)
            {
                BuildPlaneHandle(i);
                BuildGrid(i);
            }
        }

        /// <summary>The two-axis handle: a filled triangle in the plane's own two axes, coloured
        /// by the plane's NORMAL axis (the convention every DCC tool uses — the blue handle moves
        /// in the plane whose normal is blue). Scaled with the arrows, so it keeps its screen
        /// size.</summary>
        private void BuildPlaneHandle(int i)
        {
            var (ia, ib, inormal) = Planes[i];
            var a = AxisDirGodot[ia];
            var b = AxisDirGodot[ib];

            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = AxisColor[inormal],
                NoDepthTest = true,
                RenderPriority = 98,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            _planeMaterials[i] = mat;

            var mesh = new ImmediateMesh();
            mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, mat);
            mesh.SurfaceAddVertex(a * PlaneInner + b * PlaneInner);
            mesh.SurfaceAddVertex(a * PlaneOuter + b * PlaneInner);
            mesh.SurfaceAddVertex(a * PlaneInner + b * PlaneOuter);
            mesh.SurfaceEnd();

            _planeQuads[i] = new MeshInstance3D { Name = $"{PlaneHandle(i)}", Mesh = mesh };
            AddChild(_planeQuads[i]);
        }

        /// <summary>The metre grid shown while dragging a plane, so a two-axis move has something
        /// to judge distance against. Fixed world spacing and NOT scaled with the gizmo — a grid
        /// whose cells changed size with the camera would tell you nothing. Hidden until its own
        /// plane is being dragged.</summary>
        private void BuildGrid(int i)
        {
            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(1f, 1f, 1f, 0.5f),
                NoDepthTest = true,
                RenderPriority = 97,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            };

            _gridMaterials[i] = mat;

            // TopLevel: the grid must NOT inherit this node's transform. The gizmo follows the
            // object every frame, so a child grid would travel with the thing being dragged and
            // measure against itself. It is parked at the drag's start position instead and
            // stays there, which is the only way it can show how far the object has come.
            _grids[i] = new MeshInstance3D
            {
                Name = $"Grid{PlaneHandle(i)}",
                Mesh = new ImmediateMesh(),
                Visible = false,
                TopLevel = true,
            };
            AddChild(_grids[i]);
            BuildGridMesh(i, GridExtentMin);
        }

        /// <summary>How far the grid should reach for an object at <paramref name="at"/>, from
        /// the camera's current distance to it.</summary>
        private float GridExtentFor(Vector3 at)
            => Mathf.Clamp(_camera.GlobalPosition.DistanceTo(at) * GridExtentPerDistance,
                           GridExtentMin, GridExtentMax);

        /// <summary>(Re)builds one plane's grid lines at the given half-extent. Cheap enough to
        /// redo per drag (a few hundred vertices) and only ever called on mouse-down.</summary>
        private void BuildGridMesh(int i, float extent)
        {
            var (ia, ib, _) = Planes[i];
            var a = AxisDirGodot[ia];
            var b = AxisDirGodot[ib];

            var mesh = (ImmediateMesh)_grids[i].Mesh;
            mesh.ClearSurfaces();
            mesh.SurfaceBegin(Mesh.PrimitiveType.Lines, _gridMaterials[i]);
            int half = Mathf.Max(1, Mathf.RoundToInt(extent / GridSpacing));
            for (int n = -half; n <= half; n++)
            {
                float o = n * GridSpacing;
                mesh.SurfaceAddVertex(a * o + b * -extent);
                mesh.SurfaceAddVertex(a * o + b * extent);
                mesh.SurfaceAddVertex(a * -extent + b * o);
                mesh.SurfaceAddVertex(a * extent + b * o);
            }
            mesh.SurfaceEnd();
        }
    }
}
