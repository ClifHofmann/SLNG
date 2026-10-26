using System;

namespace SLNG.Core.Camera;

/// <summary>
/// The zoom slider of the Camera Controls window: the camera's distance to its focus as a fraction, 1 = as close
/// as it may get and 0 = as far (FEAT-UI-74). Engine-free so it can be tested.
///
/// <para>Logarithmic, not the viewer's linear <c>clamp_rescale</c> (<c>LLAgentCamera::getCameraZoomFraction</c>,
/// llagentcamera.cpp:741-788). The viewer can afford linear because with the camera on the avatar its slider spans
/// only a small multiple of the default offset; ours spans 0.5 to 200 m, where linear puts every everyday
/// distance (2 to 10 m) in the top 5 % of the track and the handle never visibly moves. On a log scale each
/// doubling of the distance is the same stretch of track: 4 m sits at about two thirds.</para>
/// </summary>
public static class CameraZoomFraction
{
    /// <summary>Floor for the logarithm: a minimum at or below zero is treated as this.</summary>
    private const float SmallestMin = 0.001f;

    /// <summary>The fraction for <paramref name="distance"/> between <paramref name="min"/> (1) and
    /// <paramref name="max"/> (0), clamped to 0..1. A range with no width reads as fully in.</summary>
    public static float ToFraction(float distance, float min, float max)
    {
        if (!(max > min)) return 1f;
        float lo = MathF.Max(min, SmallestMin);
        if (!(max > lo)) return 1f;
        float d = Math.Clamp(distance, lo, max);
        return Math.Clamp(1f - MathF.Log(d / lo) / MathF.Log(max / lo), 0f, 1f);
    }

    /// <summary>The distance for <paramref name="fraction"/> (clamped to 0..1): <paramref name="min"/> at 1,
    /// <paramref name="max"/> at 0. The inverse of <see cref="ToFraction"/> within the range.</summary>
    public static float ToDistance(float fraction, float min, float max)
    {
        float f = Math.Clamp(fraction, 0f, 1f);
        float lo = MathF.Max(min, SmallestMin);
        if (!(max > lo)) return min;
        if (f >= 1f) return min; // exact ends, not a few ULP off through exp/log
        if (f <= 0f) return max;
        return lo * MathF.Exp((1f - f) * MathF.Log(max / lo));
    }
}
