using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.Core;

/// <summary>
/// FEAT-SEC-04: may <b>this agent</b> edit this object? The one place anything gating an in-world
/// edit should ask.
///
/// <para><b>Not permission arithmetic.</b> The obvious implementation — take the object's
/// permission masks, work out whether the agent is the owner or in its group, and evaluate — is
/// not what the reference viewer does, and reimplementing it would be both more code and more
/// wrong. <c>LLViewerObject::permModify()</c> is <c>flagObjectModify()</c>
/// (llviewerobject.cpp:6988), a single bit test against the ObjectUpdate flags: the simulator has
/// already evaluated the masks against the receiving agent and shipped the answer.
/// <c>LLPermissions::allowOperationBy</c> — which does do the arithmetic — is for inventory
/// items, not for live objects. Two same-sounding predicates, different jobs.</para>
///
/// <para><b>Modify and Move are separate questions.</b> A no-modify object still accepts
/// position, rotation and scale edits; SL sends FLAGS_OBJECT_MODIFY and FLAGS_OBJECT_MOVE as
/// distinct bits for exactly that reason, and OpenSim enforces them separately
/// (<c>SceneGraph.UpdatePrimFlags</c> wants Modify, a transform wants Move). A UI that gates
/// everything on one of them is wrong in one direction or the other.</para>
/// </summary>
public static class EditPermission
{
    /// <summary>The prim whose flags decide, which for a child is the linkset's root -- and for
    /// a WORN item the prim that hangs off the avatar, never the avatar itself. The viewer
    /// recurses through <c>getParent()</c> for exactly this; a child's own flags are not the
    /// answer, and an "edit linked parts" UI asking the child would let go of the rule the moment
    /// someone selected a child prim.
    ///
    /// <para>Falls back to the entity itself when the root cannot be resolved — a child whose
    /// root has not arrived yet, or a link the world model has not caught up with. Falling back
    /// to the child is the conservative direction: the sim sets a child's flags too, so the worst
    /// case is answering from slightly staler data, not answering "yes" to something it should
    /// not.</para></summary>
    public static Entity RootFor(World world, Entity entity)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(entity);

        var current = entity;
        // Bounded rather than while(true): a malformed linkset that points at itself, or a cycle
        // assembled from updates that arrived out of order, must not hang the render thread.
        for (int hop = 0; hop < 16; hop++)
        {
            uint parentLocalId = current.GetComponent<TransformComponent>()?.ParentLocalId ?? 0;
            if (parentLocalId == 0) return current;

            var parent = world.GetEntity(current.RegionHandle, parentLocalId);
            if (parent == null || parent == current) return current;

            // A WORN linkset's root hangs off the avatar wearing it, and an avatar is not a
            // prim. Walking into it lands on an entity with no PrimitiveComponent at all, so
            // every permission question about an attachment answered "no": no gizmo on a worn
            // item, the build window's fields disabled, and "NOT yours" printed over the user's
            // own hat. The viewer stops at exactly the same place -- permModify() recurses only
            // while !isRootEdit(), and getRootEdit() walks up "while (mParent && !mParent->
            // isAvatar())" (llviewerobject.cpp:5061).
            if (parent.GetComponent<AvatarComponent>() != null) return current;

            current = parent;
        }
        return current;
    }

    /// <summary>True when the simulator has told us this agent may modify the object — its
    /// shape, textures, flags and contents. See the class remarks for why this is a flag read.
    ///
    /// <para>False when the object is unknown to the world model or carries no primitive
    /// component: refusing an edit we cannot justify is the only safe default, and the sim would
    /// reject it anyway.</para></summary>
    public static bool CanModify(World world, Entity? entity)
    {
        if (world == null || entity == null) return false;
        return RootFor(world, entity).GetComponent<PrimitiveComponent>()?.YouCanModify ?? false;
    }

    /// <summary>True when this agent may move, rotate or rescale the object. Separate from
    /// <see cref="CanModify"/> — see the class remarks.</summary>
    public static bool CanMove(World world, Entity? entity)
    {
        if (world == null || entity == null) return false;
        return RootFor(world, entity).GetComponent<PrimitiveComponent>()?.YouCanMove ?? false;
    }

    /// <summary>True when the simulator says this agent owns the object. Preferred over comparing
    /// <see cref="MetadataComponent.OwnerId"/> to the agent id, which is wrong for a group-owned
    /// object — there the owner id is the group's.</summary>
    public static bool IsOwner(World world, Entity? entity)
    {
        if (world == null || entity == null) return false;
        return RootFor(world, entity).GetComponent<PrimitiveComponent>()?.YouAreOwner ?? false;
    }
}
