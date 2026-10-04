using System;
using System.Collections.Generic;
using Godot;
using SLNG.Core;

namespace SLNG.App.UI;

/// <summary>
/// The radar's drawing surface (FEAT-UI-39), split out of <see cref="MinimapOverlay"/> like
/// <see cref="StatsOverlay"/>'s FrameGraph -- <see cref="Update"/> just stores the latest snapshot
/// and requests a repaint; all drawing happens in <see cref="_Draw"/> on the main thread.
/// Focus-centred and zoomable; zoom is mouse wheel over the canvas, reported back through
/// <see cref="OnZoom"/> since the window (not the canvas) owns the visible range, and Shift-drag
/// reports the pan the same way.
///
/// What is drawn, bottom to top: the region images, the region outline, the objects (your prims and
/// the big ones, as squares), the chat rings, the camera view wedge, the avatars' dots and the local
/// avatar's arrow. The map can be turned so the camera
/// looks up the canvas; every point, tile and click goes through the ONE <see cref="RadarProjection"/>
/// built in <see cref="Update"/>, so what is drawn, picked and teleported to cannot disagree.
/// </summary>
internal sealed partial class RadarCanvas : Control
{
    /// <summary>One region's map tile, placed in the CURRENT region's metres (so a neighbour's
    /// origin is its offset from the current one). The texture is null until it has loaded.</summary>
    public readonly record struct Tile(
        Texture2D? Texture, System.Numerics.Vector2 Origin, float Width, float Height, bool IsCurrent);

    /// <summary>One avatar on the map. The relation picks its colour, the same one its table row uses;
    /// the distance and whether the height is known are what its tooltip says and what shape it is drawn as.</summary>
    public readonly record struct Dot(
        Guid AgentId, System.Numerics.Vector3 Position, RadarRelation Relation,
        string Name, float Distance, bool HeightKnown);

    /// <summary>How close a click has to land to a dot to pick it, in canvas pixels.</summary>
    private const float DotPickRadius = 8f;
    private const float DotRadius = 3.5f;

    // Above/below markers: a triangle about as big as the round dot, pointing the way the avatar is.
    private const float TriangleHalfWidth = 4.5f;
    private const float TriangleHalfHeight = 4f;
    private const float UnknownRingRadius = 3.2f;

    // The viewer's mini-map object colours (Firestorm colors.xml, NetMap*OwnAbove/BelowWater): a prim of
    // someone else is a dark grey square that goes darker under water; one of yours is cyan.
    private static readonly Color OtherObjectColour = new(0.24f, 0.24f, 0.24f);
    private static readonly Color OtherObjectBelowWaterColour = new(0.125f, 0.125f, 0.125f);
    private static readonly Color YourObjectColour = new(0f, 1f, 1f);
    private static readonly Color YourObjectBelowWaterColour = new(0f, 0.78f, 0.78f);

    // The viewer paints them solid. Here a square is a translucent fill with a finer outline, so the
    // region image underneath stays readable on a built-up region; yours are denser, so they still stand out.
    private const float OtherObjectFillAlpha = 0.35f;
    private const float YourObjectFillAlpha = 0.6f;
    private const float ObjectOutlineAlpha = 0.85f;

    private const float RingWidth = 2f;
    private const int WedgeSegments = 24;

    // The viewer's chat-range ring colours (llnetmap.cpp / Firestorm colors.xml), all at 30 % alpha.
    private static readonly Color WhisperColour = new(0f, 0f, 1f, 0.3f);
    private static readonly Color SayColour = new(1f, 1f, 0f, 0.3f);
    private static readonly Color ShoutColour = new(1f, 0f, 0f, 0.3f);
    private static readonly Color WedgeColour = new(1f, 1f, 1f, 0.12f);

    // Firestorm tints a neighbour 0.8 grey so the region you are in reads as the brighter one.
    private static readonly Color NeighbourTint = new(0.8f, 0.8f, 0.8f);

    public Action<float>? OnZoom;

    /// <summary>Shift-drag: how far the view should move, in region metres, so the map follows the
    /// cursor. The window owns the offset and decides whether it eases back.</summary>
    public Action<System.Numerics.Vector2>? OnPan;

    /// <summary>Double-click inside the region rectangle, as region-local metres.</summary>
    public Action<System.Numerics.Vector2>? OnTeleportClick;

    /// <summary>A single click on (within <see cref="DotPickRadius"/> of) an avatar's dot. A click
    /// that hits no dot reports nothing, so it never clears or changes the selection.</summary>
    public Action<Guid>? OnDotClicked;

    /// <summary>Right-click on a dot: the avatar and where the click was, in screen coordinates.</summary>
    public Action<Guid, Vector2>? OnDotContextMenu;

    /// <summary>Right-click on the map away from any dot: where the click was, in screen coordinates.</summary>
    public Action<Vector2>? OnMapContextMenu;

    /// <summary>What is shown and how: the chat rings and the orientation. Owned by the window, which
    /// changes it from its menus; read here every frame.</summary>
    public RadarViewSettings View { get; set; } = new();

    /// <summary>The current region's name, for the tooltip.</summary>
    public string RegionName { get; set; } = "";

    /// <summary>True from a Shift-press until the button is released. The window holds a panned view
    /// where it is meanwhile, and only eases it back afterwards.</summary>
    public bool IsPanning { get; private set; }

    private int _regionWidth = RegionTerrain.DefaultRegionSize;
    private int _regionHeight = RegionTerrain.DefaultRegionSize;
    private System.Numerics.Vector3? _center;
    private System.Numerics.Vector3? _ownPos;
    private float _heading;
    private float _visibleRangeMeters = 64f;
    private readonly List<Dot> _dots = new();
    private readonly List<Tile> _tiles = new();
    private readonly List<RadarObject> _objects = new();
    private int _objectsVersion = -1;
    private Guid? _selectedAgentId;
    private bool _hasData;

    // The camera as of the last Update: its heading and horizontal field of view, in the radar's
    // axes. Sampled once per frame there and used by drawing AND input, so a click is mapped through
    // the same turned map that was drawn. The heading is kept when the camera looks straight down.
    private bool _hasCamera;
    private bool _hasWedge;
    private float _cameraHeading = MathF.PI / 2f;
    private float _cameraHFov;
    private float _upHeading = MathF.PI / 2f;

    // Reused every frame: the wedge's fan and a dot's triangle.
    private readonly Vector2[] _wedge = new Vector2[WedgeSegments + 2];
    private readonly Vector2[] _triangle = new Vector2[3];

    public void Update(int regionWidth, int regionHeight, System.Numerics.Vector3? center,
        System.Numerics.Vector3? ownPos, float heading, float visibleRangeMeters,
        List<Dot> dots, Guid? selectedAgentId, List<Tile> tiles, List<RadarObject> objects, int objectsVersion)
    {
        _regionWidth = Math.Max(1, regionWidth);
        _regionHeight = Math.Max(1, regionHeight);
        _center = center;
        _ownPos = ownPos;
        _heading = heading;
        _visibleRangeMeters = Math.Max(1f, visibleRangeMeters);
        _dots.Clear();
        _dots.AddRange(dots);
        _selectedAgentId = selectedAgentId;
        _tiles.Clear();
        _tiles.AddRange(tiles);
        // The objects change when the layer is scanned again, not per frame: copied and painted only then.
        if (objectsVersion != _objectsVersion)
        {
            _objectsVersion = objectsVersion;
            _objects.Clear();
            _objects.AddRange(objects);
            if (View.ShowObjects) RebuildObjectLayer();
        }
        _hasData = true;

        // A release outside the window or a lost focus can swallow the button-up: do not stay panning.
        if (IsPanning && !Input.IsMouseButtonPressed(MouseButton.Left)) EndPan();

        SampleCamera();
        _upHeading = View.CameraUp && _hasCamera ? _cameraHeading : MathF.PI / 2f;
        QueueRedraw();
    }

    public void Clear()
    {
        _hasData = false;
        QueueRedraw();
    }

    /// <summary>Reads the 3D camera: where it looks and how wide. Camera3D.Fov is vertical unless the
    /// camera keeps its width, so the horizontal angle is derived from the viewport's aspect. The
    /// engine's axes are Y-up with SL(X, Y, Z) = Godot(X, Z, -Y) (RenderConfig.ToGodot), which is a
    /// pure axis permutation, so a direction converts the same way a position does.</summary>
    private void SampleCamera()
    {
        _hasCamera = false;
        _hasWedge = false;
        var viewport = GetViewport();
        var camera = viewport?.GetCamera3D();
        if (viewport == null || camera == null) return;

        var forward = -camera.GlobalTransform.Basis.Z;
        if (RadarCamera.TryHeading(new System.Numerics.Vector2(forward.X, -forward.Z), out float heading))
            _cameraHeading = heading;
        _hasCamera = true;

        if (camera.Projection != Camera3D.ProjectionType.Perspective) return;
        float fov = Mathf.DegToRad(camera.Fov);
        if (camera.KeepAspect == Camera3D.KeepAspectEnum.Width)
        {
            _cameraHFov = fov; // the angle is already the horizontal one
        }
        else
        {
            var size = viewport.GetVisibleRect().Size;
            _cameraHFov = RadarCamera.HorizontalFov(fov, size.Y > 0f ? size.X / size.Y : 1f);
        }
        _hasWedge = true;
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion motion)
        {
            if (IsPanning) PanBy(motion.Position, motion.Relative);
            return;
        }
        if (@event is not InputEventMouseButton mb) return;

        if (!mb.Pressed)
        {
            if (mb.ButtonIndex == MouseButton.Left) EndPan();
            return;
        }

        switch (mb.ButtonIndex)
        {
            case MouseButton.WheelUp:
                OnZoom?.Invoke(0.8f);
                break;
            case MouseButton.WheelDown:
                OnZoom?.Invoke(1.25f);
                break;
            case MouseButton.Left when mb.ShiftPressed:
                // Shift-drag pans, as in Firestorm. The press itself selects and teleports nothing, and
                // neither does the drag: those are only for a plain click.
                IsPanning = true;
                break;
            case MouseButton.Left:
                // The first press of a double-click arrives as a plain click, so it may pick a dot;
                // the second one (DoubleClick) is the teleport.
                if (mb.DoubleClick) TeleportAt(mb.Position);
                else PickDotAt(mb.Position);
                break;
            case MouseButton.Right:
                OpenContextMenu(mb.Position);
                break;
        }
    }

    private void EndPan() => IsPanning = false;

    /// <summary>Moves the view so the map point that was under the cursor stays under it. Taken as the
    /// difference of two projected points, so it is right for any scale and any turn of the map.</summary>
    private void PanBy(Vector2 at, Vector2 relative)
    {
        if (!_hasData || Projection() is not { } projection) return;
        var before = projection.ToRegion(new System.Numerics.Vector2(at.X - relative.X, at.Y - relative.Y));
        var after = projection.ToRegion(new System.Numerics.Vector2(at.X, at.Y));
        OnPan?.Invoke(before - after);
    }

    /// <summary>Inverts exactly what <see cref="_Draw"/> drew (same <see cref="RadarProjection"/>),
    /// so the spot clicked is the spot shown. A click outside the region rectangle -- the radar
    /// can be zoomed out past the region edge -- does nothing.</summary>
    private void TeleportAt(Vector2 canvasPos)
    {
        if (!_hasData || Projection() is not { } projection) return;
        var local = projection.ToRegion(new System.Numerics.Vector2(canvasPos.X, canvasPos.Y));
        if (RadarProjection.Within(local, _regionWidth, _regionHeight)) OnTeleportClick?.Invoke(local);
    }

    private void PickDotAt(Vector2 canvasPos)
    {
        if (DotAt(canvasPos) is { } dot) OnDotClicked?.Invoke(dot.AgentId);
    }

    private void OpenContextMenu(Vector2 canvasPos)
    {
        var screen = GetGlobalTransformWithCanvas() * canvasPos;
        if (DotAt(canvasPos) is { } dot) OnDotContextMenu?.Invoke(dot.AgentId, screen);
        else OnMapContextMenu?.Invoke(screen);
    }

    /// <summary>The nearest dot within <see cref="DotPickRadius"/> of the point, if any.</summary>
    private Dot? DotAt(Vector2 canvasPos)
    {
        if (!_hasData || Projection() is not { } projection) return null;

        Dot? best = null;
        float bestSq = DotPickRadius * DotPickRadius;
        foreach (var dot in _dots)
        {
            var c = projection.ToCanvas(Xy(dot.Position));
            float dx = c.X - canvasPos.X, dy = c.Y - canvasPos.Y;
            float sq = dx * dx + dy * dy;
            if (sq > bestSq) continue;
            bestSq = sq;
            best = dot;
        }

        return best;
    }

    /// <summary>Over a dot: the avatar and how far away. Elsewhere inside the region: the region and
    /// the spot under the cursor, with what a double-click does there. Outside it: nothing of its own,
    /// so the control's general hint shows.</summary>
    public override string _GetTooltip(Vector2 atPosition)
    {
        if (!_hasData || Projection() is not { } projection) return "";

        if (DotAt(atPosition) is { } dot)
        {
            string range = RadarTable.FormatRange(dot.Distance, dot.HeightKnown, RenderConfig.DrawDistance);
            return $"{dot.Name}\n{L10n.Tr("ui.radar.distance")}: {range} m";
        }

        var local = projection.ToRegion(new System.Numerics.Vector2(atPosition.X, atPosition.Y));
        if (!RadarProjection.Within(local, _regionWidth, _regionHeight)) return "";
        return $"{RegionName} ({(int)local.X}, {(int)local.Y})\n{L10n.Tr("ui.minimap.teleport_hint")}";
    }

    private RadarProjection? Projection()
    {
        if (_center is not { } focus) return null;
        var size = Size;
        return new RadarProjection(
            new System.Numerics.Vector2(size.X, size.Y),
            new System.Numerics.Vector2(focus.X, focus.Y),
            _visibleRangeMeters,
            _upHeading);
    }

    private static System.Numerics.Vector2 Xy(System.Numerics.Vector3 p) => new(p.X, p.Y);

    public override void _Draw()
    {
        using (MainThreadPhase.Enter("radar.draw")) DrawRadar();
    }

    private void DrawRadar()
    {
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.02f, 0.05f, 0.03f, 0.9f));
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.3f, 0.9f, 0.5f, 0.4f), false, 1.5f);
        if (!_hasData || Projection() is not { } projection) return;

        // Projected relative to the FOCUS point, which is the local avatar by default but can be a
        // double-clicked table row (or a panned view) instead -- so the local avatar is not assumed to
        // sit at the canvas centre; it is drawn wherever it actually is relative to whatever the
        // radar is centred on. The same RadarProjection turns a click back into region metres.
        Vector2 ToCanvas(System.Numerics.Vector2 p)
        {
            var c = projection.ToCanvas(p);
            return new Vector2(c.X, c.Y);
        }

        DrawRegionImages(projection, size);

        // Everything below is projected point by point, so turning the map needs nothing more.
        if (_ownPos is { } own)
        {
            var ownScreen = ToCanvas(Xy(own));
            DrawChatRings(ownScreen, projection.PixelsPerMetre);
            DrawViewWedge(own, projection);
        }

        float ownZ = _ownPos?.Z ?? 0f;
        foreach (var dot in _dots)
        {
            var p = ToCanvas(Xy(dot.Position));
            // The selection is a white ring: the dots are red, green or grey now, so a coloured
            // ring would vanish into one of them.
            if (_selectedAgentId.HasValue && dot.AgentId == _selectedAgentId.Value)
            {
                DrawCircle(p, 7f, new Color(1f, 1f, 1f, 0.2f));
                DrawArc(p, 7f, 0, Mathf.Tau, 20, Colors.White, 2f);
            }

            var colour = RadarIcons.RelationColor(dot.Relation);
            DrawDotMarker(p, new Color(colour.R, colour.G, colour.B, 0.95f),
                RadarHeight.MarkerFor(dot.Position.Z - ownZ, dot.HeightKnown && _ownPos != null));
        }

        if (_ownPos is not { } me) return;
        var ownPoint = ToCanvas(Xy(me));
        DrawCircle(ownPoint, 4f, new Color(0.3f, 0.75f, 1f, 1f));

        // The arrow points where the avatar faces ON THE MAP: found by projecting a point ahead of it,
        // so it stays right when the map is turned, instead of assuming north is up.
        var ahead = ToCanvas(Xy(me) + new System.Numerics.Vector2(MathF.Cos(_heading), MathF.Sin(_heading)));
        var dir = (ahead - ownPoint).Normalized();
        var tip = ownPoint + dir * 10f;
        var left = ownPoint + dir.Rotated(Mathf.DegToRad(140)) * 6f;
        var right = ownPoint + dir.Rotated(Mathf.DegToRad(-140)) * 6f;
        DrawPolygon(new[] { tip, left, right }, new[] { new Color(0.3f, 0.75f, 1f, 1f) });
    }

    /// <summary>The region images and the region outline. Drawn north-up and turned as a whole by
    /// <see cref="RadarProjection.CanvasRotationRadians"/> about the canvas centre (where the focus is),
    /// which lands each corner exactly where <see cref="RadarProjection.ToCanvas"/> puts it -- one
    /// transform instead of a rotated rectangle per tile.</summary>
    private void DrawRegionImages(RadarProjection projection, Vector2 size)
    {
        var northUp = new RadarProjection(
            new System.Numerics.Vector2(size.X, size.Y),
            new System.Numerics.Vector2(_center!.Value.X, _center.Value.Y),
            _visibleRangeMeters);
        var centre = size / 2f;
        float scale = projection.PixelsPerMetre;

        // Points are given relative to the centre, which is the transform's origin.
        Vector2 NorthUp(float x, float y)
        {
            var c = northUp.ToCanvas(new System.Numerics.Vector2(x, y));
            return new Vector2(c.X, c.Y) - centre;
        }

        DrawSetTransform(centre, projection.CanvasRotationRadians, Vector2.One);

        // The region images go first, so everything else is drawn over them. Where no region
        // exists -- or its tile has not arrived -- only the background shows.
        foreach (var tile in _tiles)
        {
            if (tile.Texture == null) continue;
            // A map tile's top edge is north, and north-up puts north at the top, so the
            // north-west corner is where the rectangle starts and no flip is needed.
            DrawTextureRect(tile.Texture,
                new Rect2(NorthUp(tile.Origin.X, tile.Origin.Y + tile.Height), new Vector2(tile.Width, tile.Height) * scale),
                false, tile.IsCurrent ? Colors.White : NeighbourTint);
        }

        // Region boundary, relative to the focus point -- only ever partly visible unless zoomed
        // out past the region size, same as a real minimap's edge-of-region behaviour.
        DrawRect(new Rect2(NorthUp(0, _regionHeight), new Vector2(_regionWidth, _regionHeight) * scale),
            new Color(0.3f, 0.9f, 0.5f, 0.25f), false, 1f);

        if (View.ShowObjects)
        {
            using (MainThreadPhase.Enter("radar.draw.objects")) DrawObjects(northUp, centre, scale);
        }

        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
    }

    /// <summary>The prims, as ONE picture drawn like a region's map tile (turned and scaled with the map, so
    /// they stay on the ground they stand on). The squares themselves are painted into a pixel buffer by
    /// <see cref="RadarObjectRaster"/> when the layer changes -- every half second at the soonest -- instead of two
    /// draw calls per prim on every redraw, which measured at 52 ms per second of wall clock for 600 prims.</summary>
    private void DrawObjects(RadarProjection northUp, Vector2 centre, float pixelsPerMetre)
    {
        if (_objects.Count == 0 || _objectLayer == null) return;

        // Same placement as a tile: the layer's north-west corner, then its size in pixels.
        var corner = northUp.ToCanvas(new System.Numerics.Vector2(
            RadarObjectRaster.OriginX, RadarObjectRaster.OriginY + RadarObjectRaster.SizeMetres));
        var topLeft = new Vector2(corner.X, corner.Y) - centre;
        DrawTextureRect(_objectLayer,
            new Rect2(topLeft, new Vector2(RadarObjectRaster.SizeMetres, RadarObjectRaster.SizeMetres) * pixelsPerMetre),
            false);
    }

    // ---- the object layer's picture --------------------------------------------------------------------

    private static readonly RadarObjectPalette ObjectPalette = new(
        new System.Numerics.Vector3(OtherObjectColour.R, OtherObjectColour.G, OtherObjectColour.B),
        new System.Numerics.Vector3(OtherObjectBelowWaterColour.R, OtherObjectBelowWaterColour.G, OtherObjectBelowWaterColour.B),
        new System.Numerics.Vector3(YourObjectColour.R, YourObjectColour.G, YourObjectColour.B),
        new System.Numerics.Vector3(YourObjectBelowWaterColour.R, YourObjectBelowWaterColour.G, YourObjectBelowWaterColour.B),
        OtherObjectFillAlpha, YourObjectFillAlpha, ObjectOutlineAlpha);

    private byte[]? _objectPixels;
    private Image? _objectImage;
    private ImageTexture? _objectLayer;

    /// <summary>Paints the current objects into the layer's texture. Called when the list changed, not per frame.</summary>
    private void RebuildObjectLayer()
    {
        _objectPixels ??= new byte[RadarObjectRaster.BufferLength];
        RadarObjectRaster.Render(_objects, _objectPixels, ObjectPalette, RadarObjects.PhantomOpacity);

        if (_objectImage == null || _objectLayer == null)
        {
            _objectImage = Image.CreateFromData(RadarObjectRaster.SizeMetres, RadarObjectRaster.SizeMetres,
                false, Image.Format.Rgba8, _objectPixels);
            _objectLayer = ImageTexture.CreateFromImage(_objectImage);
        }
        else
        {
            _objectImage.SetData(RadarObjectRaster.SizeMetres, RadarObjectRaster.SizeMetres,
                false, Image.Format.Rgba8, _objectPixels);
            _objectLayer.Update(_objectImage);
        }
    }

    /// <summary>Whisper, say and shout as full circles around the LOCAL avatar -- not the focus, so a
    /// panned or focused map still shows who can hear you. A circle looks the same whichever way the
    /// map is turned.</summary>
    private void DrawChatRings(Vector2 centre, float pixelsPerMetre)
    {
        if (!View.ChatRings) return;
        if (View.WhisperRing) DrawRing(centre, RadarChatRings.WhisperMetres * pixelsPerMetre, WhisperColour);
        if (View.SayRing) DrawRing(centre, RadarChatRings.SayMetres * pixelsPerMetre, SayColour);
        if (View.ShoutRing) DrawRing(centre, RadarChatRings.ShoutMetres * pixelsPerMetre, ShoutColour);
    }

    private void DrawRing(Vector2 centre, float radiusPixels, Color colour)
    {
        // About one point per two pixels of circumference's arc length keeps a big ring smooth.
        int points = Math.Clamp((int)(radiusPixels * 0.75f), 32, 180);
        DrawArc(centre, radiusPixels, 0f, Mathf.Tau, points, colour, RingWidth, true);
    }

    /// <summary>What the 3D camera sees: a fan from the local avatar, as far as the draw distance,
    /// as wide as the camera's horizontal field of view, centred on where it looks. Projected point by
    /// point, so it follows a turned map.</summary>
    private void DrawViewWedge(System.Numerics.Vector3 own, RadarProjection projection)
    {
        if (!_hasWedge) return;

        var apex = Xy(own);
        float radius = RenderConfig.DrawDistance;
        float start = _cameraHeading - _cameraHFov / 2f;

        var apexCanvas = projection.ToCanvas(apex);
        _wedge[0] = new Vector2(apexCanvas.X, apexCanvas.Y);
        for (int i = 0; i <= WedgeSegments; i++)
        {
            float a = start + _cameraHFov * i / WedgeSegments;
            var edge = projection.ToCanvas(apex + new System.Numerics.Vector2(MathF.Cos(a), MathF.Sin(a)) * radius);
            _wedge[i + 1] = new Vector2(edge.X, edge.Y);
        }

        DrawColoredPolygon(_wedge, WedgeColour);
    }

    /// <summary>Level is a disc, above and below are triangles pointing that way, an unknown height is
    /// a hollow ring. Markers stay upright on screen however the map is turned: "up" means higher.</summary>
    private void DrawDotMarker(Vector2 p, Color colour, HeightMarker marker)
    {
        switch (marker)
        {
            case HeightMarker.Above:
                _triangle[0] = p + new Vector2(0f, -TriangleHalfHeight - 1f);
                _triangle[1] = p + new Vector2(-TriangleHalfWidth, TriangleHalfHeight);
                _triangle[2] = p + new Vector2(TriangleHalfWidth, TriangleHalfHeight);
                DrawColoredPolygon(_triangle, colour);
                break;
            case HeightMarker.Below:
                _triangle[0] = p + new Vector2(0f, TriangleHalfHeight + 1f);
                _triangle[1] = p + new Vector2(TriangleHalfWidth, -TriangleHalfHeight);
                _triangle[2] = p + new Vector2(-TriangleHalfWidth, -TriangleHalfHeight);
                DrawColoredPolygon(_triangle, colour);
                break;
            case HeightMarker.Unknown:
                DrawArc(p, UnknownRingRadius, 0f, Mathf.Tau, 16, colour, 1.6f, true);
                break;
            default:
                DrawCircle(p, DotRadius, colour);
                break;
        }
    }
}
