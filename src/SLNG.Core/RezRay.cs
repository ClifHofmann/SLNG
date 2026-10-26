using System.Numerics;

namespace SLNG.Core;

/// <summary>
/// The ray fields of a <c>RezObject</c> packet (FEAT-INV-15), worked out the way the reference viewer
/// does in <c>LLToolDragAndDrop::dropObject</c> (<c>lltooldraganddrop.cpp</c>:1846-1997). All positions
/// are region-local, in Second Life axes.
/// </summary>
/// <param name="BypassRaycast">True when the simulator is told to skip its own raycast and place the
/// object at <see cref="End"/> (a drop on land).</param>
/// <param name="Start">Where the ray starts: the camera (<c>mLastCameraPos</c>, :1908).</param>
/// <param name="End">Where it ends.</param>
/// <param name="TargetId">The one object the simulator's ray is limited to, or empty for land (:1884-1892).</param>
public readonly record struct RezRay(bool BypassRaycast, Vector3 Start, Vector3 End, Guid TargetId)
{
    /// <summary>
    /// A drop on land (<c>dad3dRezObjectOnLand</c> passes <c>bypass_sim_raycast = true</c>, :2448): the
    /// simulator is told to skip its raycast and the ray ends exactly on the surface hit point, not lifted
    /// off it. A drop on an object (<c>dad3dRezObjectOnObject</c>, :2526) leaves the raycast to the
    /// simulator, limited to that object, and DOUBLES the ray -- "the ray's end point is an
    /// approximation, and is sometimes too short" (:1913-1917).
    /// </summary>
    /// <param name="camera">Camera position.</param>
    /// <param name="hit">Where the pick ray hit the surface.</param>
    /// <param name="hitObjectId">The full UUID of the object that was hit, or empty for land.</param>
    public static RezRay For(Vector3 camera, Vector3 hit, Guid hitObjectId)
    {
        if (hitObjectId == Guid.Empty)
            return new RezRay(BypassRaycast: true, camera, hit, Guid.Empty);

        return new RezRay(BypassRaycast: false, camera, hit + (hit - camera), hitObjectId);
    }

    /// <summary>Whether a region-local point lies inside a region of the given size. NaN is outside.</summary>
    public static bool InsideRegion(Vector3 point, float regionSizeX, float regionSizeY)
        => point.X >= 0f && point.X < regionSizeX && point.Y >= 0f && point.Y < regionSizeY;
}
