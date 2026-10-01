using System;
using System.Numerics;

namespace SLNG.Core;

/// <summary>
/// The north-up mapping between region-local metres and a radar canvas, in both directions
/// (FEAT-UI-39). One type for drawing AND clicking, so a double-click always lands on the spot
/// that was drawn there -- the inverse is where a sign slip would otherwise hide.
///
/// The focus point sits at the canvas centre. Region +Y is north and goes UP the canvas, whose own
/// Y grows downward. The scale is the shorter canvas side over the visible range, so the whole
/// range is always on screen.
/// </summary>
public readonly struct RadarProjection
{
    /// <summary>Visible ranges below this are treated as this, so a zero never divides.</summary>
    public const float MinVisibleRangeMetres = 1f;

    private readonly Vector2 _canvasCentre;
    private readonly Vector2 _focus;

    public float PixelsPerMetre { get; }

    public RadarProjection(Vector2 canvasSize, Vector2 focus, float visibleRangeMetres)
    {
        _canvasCentre = canvasSize / 2f;
        _focus = focus;
        PixelsPerMetre = MathF.Min(canvasSize.X, canvasSize.Y) / MathF.Max(MinVisibleRangeMetres, visibleRangeMetres);
    }

    /// <summary>Region-local (x east, y north) metres to canvas pixels.</summary>
    public Vector2 ToCanvas(Vector2 region) => new(
        _canvasCentre.X + (region.X - _focus.X) * PixelsPerMetre,
        _canvasCentre.Y - (region.Y - _focus.Y) * PixelsPerMetre);

    /// <summary>Canvas pixels back to region-local metres. A canvas with no area has no scale to
    /// invert, so it answers with the focus rather than NaN or infinity.</summary>
    public Vector2 ToRegion(Vector2 canvas)
    {
        if (PixelsPerMetre <= 0f) return _focus;
        return new Vector2(
            _focus.X + (canvas.X - _canvasCentre.X) / PixelsPerMetre,
            _focus.Y - (canvas.Y - _canvasCentre.Y) / PixelsPerMetre);
    }

    /// <summary>True if the point is inside a region of the given size. Half-open: the far edge
    /// belongs to the neighbour.</summary>
    public static bool Within(Vector2 region, float width, float height)
        => region.X >= 0f && region.Y >= 0f && region.X < width && region.Y < height;
}
