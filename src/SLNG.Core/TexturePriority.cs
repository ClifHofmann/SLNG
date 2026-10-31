using System;

namespace SLNG.Core;

/// <summary>
/// BUG-PERF-24: the one scale a texture request's priority is on, for scenery and for other avatars alike.
///
/// <para>The fetch gate (<see cref="PriorityGate"/>) serves the highest priority first, so two scales that do not meet decide
/// who waits. Until now scenery asked with its apparent size (always above 0) and every avatar but the focus and your own
/// asked with 0: other avatars' bakes were served only when no scenery texture was waiting, which on a crowded arrival is the
/// end of a backlog of minutes. The reference viewer ranks both by the pixel area of the face (<c>llviewertexturelist.cpp</c>
/// 905-1016; the avatar bake boost <c>BOOST_AVATAR_BAKED</c> is below <c>BOOST_HIGH</c>, <c>llgltexture.h</c> 52-56) and only
/// boosts your own avatar and the UI.</para>
///
/// <para>The measure is the scenery's own, <c>radius / distance</c> (roughly 0 to 1, the small-angle form of angular size),
/// halved behind the camera, so that an avatar and an object of the same size rank alike by construction.</para>
/// </summary>
public static class TexturePriority
{
    /// <summary>Distances under this count as this far, so a texture at the camera cannot rank infinitely high.</summary>
    public const float MinDistance = 0.1f;

    /// <summary>What rank an object behind the camera keeps: turning round is instant and common, so it is halved, not dropped.</summary>
    public const float BehindCameraFactor = 0.5f;

    /// <summary>Rank of something with this bounding radius at this distance from the camera.</summary>
    public static float ForWorld(float radius, float distance, bool behindCamera)
    {
        float size = radius / MathF.Max(distance, MinDistance);
        return behindCamera ? size * BehindCameraFactor : size;
    }

    /// <summary>
    /// Rank of an avatar's texture: your own above everything, the focus avatar's above all scenery and other avatars, anyone
    /// else's by <paramref name="sizePriority"/> (their <see cref="ForWorld"/> value, measured on the main thread).
    /// </summary>
    public static float ForAvatar(bool isSelf, bool isFocus, float sizePriority)
    {
        if (isSelf) return PrioritySubject.SelfTexturePriority;
        if (isFocus) return PrioritySubject.FocusTexturePriority;
        return sizePriority;
    }
}
