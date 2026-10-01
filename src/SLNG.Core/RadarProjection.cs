using System;
using System.Numerics;

namespace SLNG.Core;

/// <summary>
/// The mapping between region-local metres and a radar canvas, in both directions (FEAT-UI-39).
/// One type for drawing AND clicking, so a double-click always lands on the spot that was drawn
/// there -- the inverse is where a sign slip would otherwise hide.
///
/// The focus point sits at the canvas centre. By default region +Y is north and goes UP the canvas,
/// whose own Y grows downward; a different <c>upHeadingRadians</c> turns the map so that direction
/// is at the top instead (the radar's "camera up" mode). The scale is the shorter canvas side over
/// the visible range, so the whole range is always on screen, and turning the map never changes it.
/// </summary>
public readonly struct RadarProjection
{
    /// <summary>Visible ranges below this are treated as this, so a zero never divides.</summary>
    public const float MinVisibleRangeMetres = 1f;

    /// <summary>North, as a heading (counter-clockwise from east): the default "up".</summary>
    private const float NorthHeadingRadians = MathF.PI / 2f;

    private readonly Vector2 _canvasCentre;
    private readonly Vector2 _focus;

    // The region direction that points to the top of the canvas, and the one that points right.
    private readonly Vector2 _up;
    private readonly Vector2 _right;

    public float PixelsPerMetre { get; }

    /// <summary>The region direction at the top of the canvas, as a heading measured counter-clockwise
    /// from east in the north-up plane -- the convention an avatar's heading uses. Pi/2 is north.</summary>
    public float UpHeadingRadians { get; }

    /// <summary>The angle a NORTH-UP drawing must be turned about the canvas centre to match
    /// <see cref="ToCanvas"/>, in the sense Godot's <c>DrawSetTransform</c> takes (positive is
    /// clockwise on screen, whose Y grows down). Zero for north up; facing east it is a quarter turn
    /// counter-clockwise, which lifts east to the top and sends north to the left. It lets the app
    /// rotate tiles and the region outline with one call instead of projecting every corner.</summary>
    public float CanvasRotationRadians => UpHeadingRadians - NorthHeadingRadians;

    public RadarProjection(Vector2 canvasSize, Vector2 focus, float visibleRangeMetres,
        float upHeadingRadians = NorthHeadingRadians)
    {
        _canvasCentre = canvasSize / 2f;
        _focus = focus;
        PixelsPerMetre = MathF.Min(canvasSize.X, canvasSize.Y) / MathF.Max(MinVisibleRangeMetres, visibleRangeMetres);
        UpHeadingRadians = upHeadingRadians;

        // Orthonormal: right is up turned a quarter turn clockwise, so (up, right) never skew or flip
        // the map, and the scale above is the scale in every direction.
        _up = new Vector2(Snap(MathF.Cos(upHeadingRadians)), Snap(MathF.Sin(upHeadingRadians)));
        _right = new Vector2(_up.Y, -_up.X);
    }

    /// <summary>Region-local (x east, y north) metres to canvas pixels.</summary>
    public Vector2 ToCanvas(Vector2 region)
    {
        var d = region - _focus;
        return new Vector2(
            _canvasCentre.X + Vector2.Dot(d, _right) * PixelsPerMetre,
            _canvasCentre.Y - Vector2.Dot(d, _up) * PixelsPerMetre);
    }

    /// <summary>Canvas pixels back to region-local metres. A canvas with no area has no scale to
    /// invert, so it answers with the focus rather than NaN or infinity.</summary>
    public Vector2 ToRegion(Vector2 canvas)
    {
        if (PixelsPerMetre <= 0f) return _focus;
        float right = (canvas.X - _canvasCentre.X) / PixelsPerMetre;
        float up = -(canvas.Y - _canvasCentre.Y) / PixelsPerMetre;
        // _up and _right are orthonormal, so the point is just its two components laid back out.
        return _focus + _right * right + _up * up;
    }

    /// <summary>Float pi/2 is not exactly pi/2, so the cosine of "north" would come out as -4e-8 and
    /// every north-up result would be a hair off the old mapping. Snapping the rounding noise away
    /// keeps the four axis headings exact.</summary>
    private static float Snap(float v) => MathF.Abs(v) < 1e-6f ? 0f : v;

    /// <summary>How far from the focus anything can still be on the canvas, in metres: the canvas's
    /// corner is further from its centre than its sides are, and a turned map shows what lies under its
    /// corners. A square around the focus this far each way therefore covers the view at any orientation
    /// and any canvas shape, wide or tall, which is what a "could this reach the view?" test needs.</summary>
    public static float ViewReachMetres(Vector2 canvasSize, float visibleRangeMetres)
    {
        float range = MathF.Max(MinVisibleRangeMetres, visibleRangeMetres);
        float shorter = MathF.Min(canvasSize.X, canvasSize.Y);
        float halfDiagonal = shorter > 0f ? range * 0.5f * canvasSize.Length() / shorter : range;
        return MathF.Max(range, halfDiagonal);
    }

    /// <summary>True if the point is inside a region of the given size. Half-open: the far edge
    /// belongs to the neighbour.</summary>
    public static bool Within(Vector2 region, float width, float height)
        => region.X >= 0f && region.Y >= 0f && region.X < width && region.Y < height;
}
