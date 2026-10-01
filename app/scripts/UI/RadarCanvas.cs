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
/// <see cref="OnZoom"/> since the window (not the canvas) owns the visible range.
/// </summary>
internal sealed partial class RadarCanvas : Control
{
    /// <summary>One region's map tile, placed in the CURRENT region's metres (so a neighbour's
    /// origin is its offset from the current one). The texture is null until it has loaded.</summary>
    public readonly record struct Tile(
        Texture2D? Texture, System.Numerics.Vector2 Origin, float Width, float Height, bool IsCurrent);

    /// <summary>One avatar on the map; the relation picks its colour, the same one its table row uses.</summary>
    public readonly record struct Dot(Guid AgentId, System.Numerics.Vector3 Position, RadarRelation Relation);

    /// <summary>How close a click has to land to a dot to pick it, in canvas pixels.</summary>
    private const float DotPickRadius = 8f;
    private const float DotRadius = 3.5f;

    public Action<float>? OnZoom;

    /// <summary>Double-click inside the region rectangle, as region-local metres.</summary>
    public Action<System.Numerics.Vector2>? OnTeleportClick;

    /// <summary>A single click on (within <see cref="DotPickRadius"/> of) an avatar's dot. A click
    /// that hits no dot reports nothing, so it never clears or changes the selection.</summary>
    public Action<Guid>? OnDotClicked;

    private int _regionWidth = RegionTerrain.DefaultRegionSize;
    private int _regionHeight = RegionTerrain.DefaultRegionSize;
    private System.Numerics.Vector3? _center;
    private System.Numerics.Vector3? _ownPos;
    private float _heading;
    private float _visibleRangeMeters = 64f;
    private readonly List<Dot> _dots = new();
    private readonly List<Tile> _tiles = new();
    private Guid? _selectedAgentId;
    private bool _hasData;

    // Firestorm tints a neighbour 0.8 grey so the region you are in reads as the brighter one.
    private static readonly Color NeighbourTint = new(0.8f, 0.8f, 0.8f);

    public void Update(int regionWidth, int regionHeight, System.Numerics.Vector3? center,
        System.Numerics.Vector3? ownPos, float heading, float visibleRangeMeters,
        List<Dot> dots, Guid? selectedAgentId, List<Tile> tiles)
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
        _hasData = true;
        QueueRedraw();
    }

    public void Clear()
    {
        _hasData = false;
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { Pressed: true } mb) return;

        if (mb.ButtonIndex == MouseButton.WheelUp) OnZoom?.Invoke(0.8f);
        else if (mb.ButtonIndex == MouseButton.WheelDown) OnZoom?.Invoke(1.25f);
        else if (mb.ButtonIndex == MouseButton.Left)
        {
            // The first press of a double-click arrives as a plain click, so it may pick a dot;
            // the second one (DoubleClick) is the teleport.
            if (mb.DoubleClick) TeleportAt(mb.Position);
            else PickDotAt(mb.Position);
        }
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

    /// <summary>The nearest dot within <see cref="DotPickRadius"/> of the click, if any.</summary>
    private void PickDotAt(Vector2 canvasPos)
    {
        if (!_hasData || Projection() is not { } projection) return;

        Guid? best = null;
        float bestSq = DotPickRadius * DotPickRadius;
        foreach (var dot in _dots)
        {
            var c = projection.ToCanvas(new System.Numerics.Vector2(dot.Position.X, dot.Position.Y));
            float dx = c.X - canvasPos.X, dy = c.Y - canvasPos.Y;
            float sq = dx * dx + dy * dy;
            if (sq > bestSq) continue;
            bestSq = sq;
            best = dot.AgentId;
        }

        if (best is { } id) OnDotClicked?.Invoke(id);
    }

    private RadarProjection? Projection()
    {
        if (_center is not { } focus) return null;
        var size = Size;
        return new RadarProjection(
            new System.Numerics.Vector2(size.X, size.Y),
            new System.Numerics.Vector2(focus.X, focus.Y),
            _visibleRangeMeters);
    }

    public override void _Draw()
    {
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.02f, 0.05f, 0.03f, 0.9f));
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.3f, 0.9f, 0.5f, 0.4f), false, 1.5f);
        if (!_hasData || Projection() is not { } projection) return;

        // Projected relative to the FOCUS point, which is the local avatar by default but can be a
        // double-clicked table row instead (MinimapOverlay._Process) -- so the local avatar's own
        // dot is not assumed to sit at the canvas centre; it is drawn wherever it actually is
        // relative to whatever the radar is centred on. The same RadarProjection turns a click
        // back into region metres.
        Vector2 ToCanvas(System.Numerics.Vector3 p)
        {
            var c = projection.ToCanvas(new System.Numerics.Vector2(p.X, p.Y));
            return new Vector2(c.X, c.Y);
        }

        // The region images go first, so everything else is drawn over them. Where no region
        // exists -- or its tile has not arrived -- only the background shows.
        foreach (var tile in _tiles)
        {
            if (tile.Texture == null) continue;
            // A map tile's top edge is north, and ToCanvas puts north at the top, so the
            // north-west corner is where the rectangle starts and no flip is needed.
            var northWest = projection.ToCanvas(new System.Numerics.Vector2(tile.Origin.X, tile.Origin.Y + tile.Height));
            var extent = new Vector2(tile.Width, tile.Height) * projection.PixelsPerMetre;
            DrawTextureRect(tile.Texture, new Rect2(new Vector2(northWest.X, northWest.Y), extent), false,
                tile.IsCurrent ? Colors.White : NeighbourTint);
        }

        // Region boundary, relative to the focus point -- only ever partly visible unless zoomed
        // out past the region size, same as a real minimap's edge-of-region behaviour.
        var c0 = ToCanvas(new System.Numerics.Vector3(0, 0, 0));
        var c1 = ToCanvas(new System.Numerics.Vector3(_regionWidth, _regionHeight, 0));
        DrawRect(new Rect2(
            new Vector2(Math.Min(c0.X, c1.X), Math.Min(c0.Y, c1.Y)),
            new Vector2(Math.Abs(c1.X - c0.X), Math.Abs(c1.Y - c0.Y))),
            new Color(0.3f, 0.9f, 0.5f, 0.25f), false, 1f);

        foreach (var dot in _dots)
        {
            var p = ToCanvas(dot.Position);
            // The selection is a white ring: the dots are red, green or grey now, so a coloured
            // ring would vanish into one of them.
            if (_selectedAgentId.HasValue && dot.AgentId == _selectedAgentId.Value)
            {
                DrawCircle(p, 7f, new Color(1f, 1f, 1f, 0.2f));
                DrawArc(p, 7f, 0, Mathf.Tau, 20, Colors.White, 2f);
            }
            var colour = RadarIcons.RelationColor(dot.Relation);
            DrawCircle(p, DotRadius, new Color(colour.R, colour.G, colour.B, 0.95f));
        }

        if (_ownPos is not { } own) return;
        var ownScreen = ToCanvas(own);
        DrawCircle(ownScreen, 4f, new Color(0.3f, 0.75f, 1f, 1f));
        var dir = new Vector2(MathF.Cos(_heading), -MathF.Sin(_heading));
        var tip = ownScreen + dir * 10f;
        var left = ownScreen + dir.Rotated(Mathf.DegToRad(140)) * 6f;
        var right = ownScreen + dir.Rotated(Mathf.DegToRad(-140)) * 6f;
        DrawPolygon(new[] { tip, left, right }, new[] { new Color(0.3f, 0.75f, 1f, 1f) });
    }
}
