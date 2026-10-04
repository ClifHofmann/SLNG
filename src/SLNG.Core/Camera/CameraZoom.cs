using System;
using System.Numerics;

namespace SLNG.Core.Camera;

/// <summary>
/// The arithmetic of the wheel zoom (FEAT-UI-55), kept free of the engine so it can be tested.
///
/// <para>The Linden viewer's third-person wheel is multiplicative (one notch scales the distance by
/// 2^(1/4)) and eased (the real distance chases a target with a 0.07 s half-life); see
/// <c>docs/specs/camera-zoom-parity-firestorm-vs-slng.md</c>. SLNG keeps zoom-to-cursor on top of
/// that, which needs the pan shift below.</para>
/// </summary>
public static class CameraZoom
{
    /// <summary>Nearest the camera may sit to its pivot, in metres.</summary>
    public const float MinZoom = 0.5f;

    /// <summary>Farthest the camera may sit from its pivot, in metres.</summary>
    public const float MaxZoom = 200f;

    /// <summary>Distance change per wheel notch, as a power of two: one notch is 2^(1/4).</summary>
    public const float NotchExponent = 0.25f;

    /// <summary>Time for the remaining zoom distance to halve (the viewer's CAMERA_ZOOM_HALF_LIFE).</summary>
    public const float DefaultHalfLifeSeconds = 0.07f;

    /// <summary>Distance below which the ease snaps to its target.</summary>
    public const float SnapDistance = 0.001f;

    /// <summary>
    /// The target distance after <paramref name="notches"/> wheel notches: positive notches move the
    /// camera away (distance grows), negative ones in (it shrinks), each by a factor of 2^(1/4).
    /// Clamped to [<paramref name="min"/>, <paramref name="max"/>].
    /// </summary>
    public static float WheelStep(float current, int notches, float min = MinZoom, float max = MaxZoom)
        => Math.Clamp(current * MathF.Pow(2f, notches * NotchExponent), min, max);

    /// <summary>
    /// One frame of the ease: moves <paramref name="current"/> toward <paramref name="target"/> by the
    /// fraction 1 - 0.5^(dt / half-life) of what is left, so the result does not depend on the frame
    /// rate, and snaps to the target once it is within <see cref="SnapDistance"/>.
    /// </summary>
    public static float EaseToward(float current, float target, float dt, float halfLifeSeconds = DefaultHalfLifeSeconds)
    {
        if (MathF.Abs(target - current) <= SnapDistance) return target;
        if (dt <= 0f) return current;
        float alpha = halfLifeSeconds <= 0f ? 1f : 1f - MathF.Pow(0.5f, dt / halfLifeSeconds);
        float next = current + (target - current) * alpha;
        return MathF.Abs(target - next) <= SnapDistance ? target : next;
    }

    /// <summary>
    /// Re-expresses the camera's current position relative to a new pivot <paramref name="point"/>, so that
    /// <c>target = point + X * panX + Y * panY</c> and <c>camera = target + Z * zoom</c> give back exactly
    /// <paramref name="cameraPos"/> (the camera neither moves nor turns when the pivot changes). The basis
    /// vectors are the camera's own right, up and back axes (unit, orthogonal). <c>zoom</c> is the camera's
    /// depth behind the point along Z; it is clamped to at least <paramref name="minZoom"/>, and when the clamp
    /// bites the camera is no longer reproduced exactly (the missing part is along Z, which a pan cannot carry).
    /// </summary>
    public static (float PanX, float PanY, float Zoom) AnchorPan(Vector3 cameraPos, Vector3 point,
        Vector3 basisX, Vector3 basisY, Vector3 basisZ, float minZoom = MinZoom)
    {
        var rel = cameraPos - point;
        float zoom = MathF.Max(Vector3.Dot(rel, basisZ), minZoom);
        var rest = rel - basisZ * zoom;
        return (Vector3.Dot(rest, basisX), Vector3.Dot(rest, basisY), zoom);
    }

    /// <summary>
    /// How far the camera's pan offset must move, in camera-local X (right) and Y (up) metres, so that
    /// whatever sits under the cursor stays under it while the zoom distance goes from
    /// <paramref name="oldZoom"/> to <paramref name="newZoom"/>.
    ///
    /// <para><paramref name="ndcX"/> and <paramref name="ndcY"/> are the cursor in [-1, 1] with +Y up.
    /// <paramref name="fovDegVertical"/> is the vertical field of view; <paramref name="aspect"/> is
    /// width / height. The cursor's offset in the pivot plane is ndc * tan(fov/2) * oldZoom (X also
    /// times the aspect), and the shift is that offset times (1 - newZoom / oldZoom): zero when the
    /// distance does not change, the whole offset when it goes to zero.</para>
    /// </summary>
    public static Vector2 CursorPanShift(float ndcX, float ndcY, float fovDegVertical, float oldZoom, float newZoom,
        float aspect = 1f)
    {
        if (oldZoom <= 0f) return Vector2.Zero;
        float halfHeight = oldZoom * MathF.Tan(fovDegVertical * MathF.PI / 180f * 0.5f);
        float halfWidth = halfHeight * aspect;
        float k = 1f - newZoom / oldZoom;
        return new Vector2(ndcX * halfWidth * k, ndcY * halfHeight * k);
    }
}
