using System;
using System.Numerics;

namespace SLNG.Core;

/// <summary>The radar's pan offset: how far a Shift-drag may take the view, and how it eases back
/// (FEAT-UI-39). The offset is in region metres and is added to the point the map is centred on.</summary>
public static class RadarPan
{
    /// <summary>About one region either way: far enough to look over the edge, not so far that the
    /// map is lost in empty space.</summary>
    public const float MaxMetres = 256f;

    /// <summary>The time constant of the ease back to the avatar: after this long the offset is down to
    /// a third (1/e). Short, because it is a snap with the edges taken off, not an animation.</summary>
    private const float EaseSeconds = 0.1f;

    /// <summary>Below this the offset is simply zero, so the ease ends instead of crawling forever.</summary>
    private const float SettleMetres = 0.05f;

    public static Vector2 Clamp(Vector2 pan) => new(
        Math.Clamp(pan.X, -MaxMetres, MaxMetres),
        Math.Clamp(pan.Y, -MaxMetres, MaxMetres));

    /// <summary>The offset after <paramref name="deltaSeconds"/> of easing back to zero. Exponential, so
    /// ten 10 ms frames and one 100 ms frame end in the same place: the ease looks the same at any frame
    /// rate. No time passed (or a clock that stepped back) changes nothing.</summary>
    public static Vector2 EaseToZero(Vector2 pan, float deltaSeconds)
    {
        if (!(deltaSeconds > 0f)) return pan;

        var eased = pan * MathF.Exp(-deltaSeconds / EaseSeconds);
        return eased.Length() < SettleMetres ? Vector2.Zero : eased;
    }
}
