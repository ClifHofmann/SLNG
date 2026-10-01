using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.Core;

/// <summary>
/// FEAT-ANIMESH-01: is this prim part of an animated-mesh object? The one place that answers it,
/// so the renderer that hands a mesh to a control avatar and the one that places the avatar agree.
/// </summary>
/// <remarks>
/// The reference viewer reads the flag from the linkset ROOT only
/// (<c>LLVOVolume::isAnimatedObject</c>, llvovolume.cpp:3854-3863) and ignores the Extended Mesh
/// block on every child, so a child's own <see cref="PrimitiveComponent.IsAnimatedMesh"/> is not
/// the answer -- and neither is a guess when the root has not arrived yet. That last case is the
/// difference from <see cref="EditPermission.RootFor"/>, which falls back to the prim itself
/// (the conservative direction for permissions): here "root unknown" means "not animesh yet", and
/// the root's own arrival is what turns the answer into yes.
/// </remarks>
public static class AnimatedMeshLinkset
{
    /// <summary>The root prim of the linkset <paramref name="entity"/> belongs to, or null when
    /// that cannot be told: a parent that has not streamed in, a chain that does not end, or a
    /// linkset worn on an avatar (an attachment is drawn by the avatar renderer, not as a world
    /// object).</summary>
    public static Entity? RootOf(World world, Entity entity)
    {
        System.ArgumentNullException.ThrowIfNull(world);
        System.ArgumentNullException.ThrowIfNull(entity);

        var current = entity;
        // Bounded for the same reason EditPermission.RootFor is: a malformed link must not hang
        // the render thread.
        for (int hop = 0; hop < 16; hop++)
        {
            uint parentLocalId = current.GetComponent<TransformComponent>()?.ParentLocalId ?? 0;
            if (parentLocalId == 0) return current;

            var parent = world.GetEntity(current.RegionHandle, parentLocalId);
            if (parent == null || parent == current) return null;
            if (parent.GetComponent<AvatarComponent>() != null) return null;

            current = parent;
        }
        return null;
    }

    /// <summary>True when the linkset's root carries the animated-mesh flag. <paramref name="root"/>
    /// is the root prim whenever it could be resolved, flagged or not.</summary>
    public static bool IsAnimatedPart(World world, Entity entity, out Entity? root)
    {
        root = RootOf(world, entity);
        return root?.GetComponent<PrimitiveComponent>()?.IsAnimatedMesh == true;
    }
}
