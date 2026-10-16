using System.Numerics;
using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.Core;

/// <summary>FEAT-RENDER-27: where a prim is DRAWN once client-side target omega is laid on top of
/// where the simulator put it. See <see cref="SpinComponent"/> for the rules.
///
/// <para>The world model keeps the simulator's transforms (<see cref="TransformComponent.Position"/>
/// and <see cref="TransformComponent.Rotation"/>); the turn is visual only, as in the viewer, where
/// the simulator never hears of it. A spin is carried down a linkset the way the viewer's drawables
/// carry it (<c>LLXformMatrix::updateMatrix</c>): a turning root turns every child and every avatar
/// sitting on it, and a turning child turns about its own centre with its axis in the root's
/// frame.</para></summary>
public static class TargetOmega
{
    /// <summary>The drawn transform of <paramref name="entity"/>: its world transform with every
    /// spin on the way up the linkset applied. False, with the outputs left at the world transform,
    /// when neither the prim nor any prim above it carries a <see cref="SpinComponent"/> -- the
    /// common case, which costs one component lookup per level.</summary>
    public static bool TryGetRenderTransform(World world, Entity entity, TransformComponent transform,
        out Vector3 position, out Quaternion rotation)
    {
        position = transform.Position;
        rotation = transform.Rotation;
        var spin = entity.GetComponent<SpinComponent>();

        if (transform.ParentLocalId == 0)
        {
            if (spin == null) return false;
            rotation = spin.Apply(transform.Rotation);
            return true;
        }

        // A child: only a prim parent carries a spin down. An avatar parent is an attachment's
        // wearer, placed on a bone elsewhere; a parent not seen yet leaves the child where
        // WorldSimulation put it.
        var parent = world.GetEntity(entity.RegionHandle, transform.ParentLocalId);
        var parentTransform = parent?.GetComponent<TransformComponent>();
        if (parent == null || parentTransform == null || parent.HasComponent<AvatarComponent>())
        {
            if (spin == null) return false;
            rotation = spin.Apply(transform.Rotation);
            return true;
        }

        bool parentTurned = TryGetRenderTransform(world, parent, parentTransform, out var parentPosition, out var parentRotation);
        if (!parentTurned && spin == null) return false;

        var localRotation = spin?.Apply(transform.LocalRotation) ?? transform.LocalRotation;
        (position, rotation) = LinksetTransform.ToWorld(transform.LocalPosition, localRotation, parentPosition, parentRotation);
        return true;
    }

    /// <summary>The rigid move that takes <paramref name="entity"/> from where the simulator put it
    /// to where it is drawn: a point <c>p</c> on it goes to
    /// <c>drawnPosition + Transform(p - worldPosition, turn)</c>, an orientation <c>q</c> to
    /// <c>turn * q</c>. This is what an avatar sitting on the prim gets, since the viewer hangs a
    /// sitter's drawable under its seat's (<c>LLVOAvatar::sitOnObject</c>). False when the prim is
    /// not turned at all.</summary>
    public static bool TryGetSeatTurn(World world, Entity entity, TransformComponent transform,
        out Vector3 worldPosition, out Vector3 drawnPosition, out Quaternion turn)
    {
        worldPosition = transform.Position;
        drawnPosition = transform.Position;
        turn = Quaternion.Identity;
        if (!TryGetRenderTransform(world, entity, transform, out drawnPosition, out var drawnRotation)) return false;

        // Conjugate, not Inverse: unit quaternions, and Inverse turns a zero-length wire value into NaN.
        // Normalised because a wire rotation is not always exactly unit length.
        var product = drawnRotation * Quaternion.Conjugate(transform.Rotation);
        float lengthSquared = product.LengthSquared();
        turn = lengthSquared > 1e-12f ? Quaternion.Normalize(product) : Quaternion.Identity;
        return true;
    }
}
