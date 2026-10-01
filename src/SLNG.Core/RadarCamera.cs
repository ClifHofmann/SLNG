using System;
using System.Numerics;

namespace SLNG.Core;

/// <summary>The camera maths behind the radar's view wedge (FEAT-UI-39). The engine's camera is read
/// in the app; only the engine-neutral numbers come here.</summary>
public static class RadarCamera
{
    /// <summary>A horizontal look-direction shorter than this is "looking straight up or down".</summary>
    private const float MinHorizontalLength = 1e-4f;

    /// <summary>The horizontal field of view, from the vertical one and the viewport's width over
    /// height. Cameras are specified by their vertical angle; it is the tangents that scale with the
    /// aspect ratio, not the angles. A zero, negative or NaN aspect is treated as square, and the result
    /// stays below a half turn so the wedge is always a convex fan.</summary>
    public static float HorizontalFov(float verticalFovRadians, float aspect)
    {
        if (!(aspect > 0f) || !float.IsFinite(aspect)) aspect = 1f;
        return 2f * MathF.Atan(MathF.Tan(verticalFovRadians / 2f) * aspect);
    }

    /// <summary>The camera's heading, counter-clockwise from east in the north-up plane (the same
    /// convention as an avatar's heading), from its horizontal look direction in region axes (x east,
    /// y north). False when the camera looks straight up or down and there is no heading to give, so
    /// the caller keeps the last one.</summary>
    public static bool TryHeading(Vector2 horizontalForward, out float heading)
    {
        if (horizontalForward.LengthSquared() < MinHorizontalLength * MinHorizontalLength)
        {
            heading = 0f;
            return false;
        }

        heading = MathF.Atan2(horizontalForward.Y, horizontalForward.X);
        return true;
    }
}
