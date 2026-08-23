using System.Numerics;

namespace SLNG.Core;

/// <summary>Turns SL's avatar collision plane into a support height.
///
/// Engine-agnostic on purpose: this is a port of viewer maths, it belongs where it can be tested,
/// and the renderer should not be the only place it exists.</summary>
public static class AvatarSupport
{
    /// <summary>The viewer's own tolerance on the plane, from <c>LLWorld::resolveStepHeightGlobal</c>
    /// (llworld.cpp:575), whose comment reads "added 0.05 meters to compensate for error in foot
    /// plane reported by Havok".</summary>
    public const float HavokPlaneTolerance = 0.05f;

    /// <summary>Height of the surface the simulator says this avatar is supported by.
    ///
    /// <paramref name="plane"/> is SL's collision plane: xyz the normal in region space, w the
    /// constant, so <c>dot(position, normal) - w</c> is how far the avatar's collision-cylinder
    /// centre sits above it. Subtracting that from the avatar's own Z gives the surface.
    ///
    /// Returns null when the plane cannot support a sane answer, which the caller must treat as
    /// "not told", never as "nothing underneath": a degenerate normal, or one that has been tipped
    /// so far from vertical that a straight-down reading is meaningless. A wall's plane is a real
    /// thing the simulator can send, and reading it as a floor would place the avatar's feet
    /// somewhere absurd.</summary>
    public static float? SupportHeight(Vector4 plane, Vector3 position)
    {
        var normal = new Vector3(plane.X, plane.Y, plane.Z);

        float lengthSquared = normal.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared < 1e-6f) return null;

        // Normalised so a plane that arrives unnormalised cannot scale the distance. The viewer
        // does not do this because Havok hands it a unit normal; we cannot assume the same after a
        // decode and an interpolation, and the failure would be a silent vertical offset.
        normal /= MathF.Sqrt(lengthSquared);
        float constant = plane.W / MathF.Sqrt(lengthSquared);

        // A near-vertical plane is not a floor. cos(60 deg) = 0.5 keeps ordinary sloped ground and
        // ramps while rejecting walls.
        if (normal.Z < 0.5f) return null;

        float distanceAbove = Vector3.Dot(position, normal) - constant + HavokPlaneTolerance;
        if (!float.IsFinite(distanceAbove)) return null;

        return position.Z - distanceAbove;
    }
}
