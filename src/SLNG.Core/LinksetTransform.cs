using System.Numerics;

namespace SLNG.Core;

/// <summary>
/// Converts between a linked prim's own (parent-relative) transform and its world transform.
/// </summary>
/// <remarks>
/// A child prim's <c>ObjectUpdate</c> carries its position and rotation **relative to the root
/// prim**, and a <c>MultipleObjectUpdate</c> sent back for that prim is read the same way. Only a
/// root prim's own transform is in region coordinates. Getting this backwards does not look like a
/// rounding error: the child is displaced by roughly the root's position in the region, so a part
/// of a linkset simply flies off somewhere else — which is how it was found (FEAT-UI-06, reported
/// in-world as "das Objekt ist zerfallen").
///
/// Scale is deliberately absent. In SL a prim's scale is its own and does not inherit from the
/// root (see the note in the avatar joint code for the same rule on skeletons), so there is
/// nothing to convert.
/// </remarks>
public static class LinksetTransform
{
    /// <summary>The world transform of a child, given its parent-relative one.</summary>
    public static (Vector3 Position, Quaternion Rotation) ToWorld(
        Vector3 localPosition, Quaternion localRotation,
        Vector3 parentPosition, Quaternion parentRotation)
        => (parentPosition + Vector3.Transform(localPosition, parentRotation),
            parentRotation * localRotation);

    /// <summary>The parent-relative transform of a child, given where it is in the world. The
    /// exact inverse of <see cref="ToWorld"/>.</summary>
    public static (Vector3 Position, Quaternion Rotation) ToLocal(
        Vector3 worldPosition, Quaternion worldRotation,
        Vector3 parentPosition, Quaternion parentRotation)
    {
        // Conjugate rather than Inverse: these are unit quaternions, where the two agree, and
        // Inverse additionally divides by the squared length -- which turns a rotation that has
        // drifted to zero length into NaN instead of leaving it alone.
        var inverse = Quaternion.Conjugate(parentRotation);
        return (Vector3.Transform(worldPosition - parentPosition, inverse),
                inverse * worldRotation);
    }
}
