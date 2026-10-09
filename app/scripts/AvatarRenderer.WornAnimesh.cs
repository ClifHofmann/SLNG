using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.App;

// BUG-AVATAR-10, second half -- a control avatar for an animated object that is WORN.
//
// The first half stopped an attached animesh (a pet, a companion) from reshaping its wearer's skeleton,
// which left the pet itself skinned to a normal-size human skeleton without its own joint overrides:
// a dragon sitting inside the person holding it. The reference viewer gives an attached animated
// object a control avatar exactly as it does a rezzed one (LLViewerObject::updateControlAvatar ->
// linkControlAvatar -> LLControlAvatar::createControlAvatar, llviewerobject.cpp:3218-3278); only two
// things differ, and they are all this file adds to AvatarRenderer.ControlAvatar.cs:
//
//   * PLACEMENT. The skeleton root is not put at a region position; it follows the wearer's
//     attachment point (LLControlAvatar::matchVolumeTransform, llcontrolavatar.cpp:176-197): the
//     root prim's local position, rotated by the attachment point's world rotation, plus the point's
//     world position, turned by obj_rot * joint_rot. No bind_rot in that branch (the region branch
//     has one, :217-232), and no pelvis fixup (the root is set directly, not through
//     getRenderPosition).
//   * VISIBILITY. shouldRenderRigged() and isImpostor() are the wearer's (:682-697): hide the wearer
//     and the pet goes with it.
//
// The mesh reaches the control avatar from CommitPreparedRig, the one place that has both the decoded
// mesh and a live world to ask "is this linkset's root animated mesh?" -- on the main thread, with the
// answer as it is when the rig is committed, not as it was when the mesh was requested.

public partial class AvatarRenderer
{
    /// <summary>Roots whose animated-mesh flag was last seen set, so a flip reaches the linkset's
    /// children: nothing about a child changes when only its root's block does, and no update reaches
    /// it. Only animated roots are here; an ordinary attachment never gets an entry.</summary>
    private readonly HashSet<Guid> _wornAnimatedRootsSeen = new();

    /// <summary>Root entities whose control avatar was dropped because the wearer's visual was
    /// rebuilt: their parts are asked for again once the drop is done (see UpdateControlAvatars).
    /// </summary>
    private readonly HashSet<Guid> _controlAvatarRerouteRoots = new();

    /// <summary>The control avatar skeleton's world transform for a worn animated object:
    /// <c>attachment frame * (root prim's local position and rotation)</c>, which is the viewer's
    /// <c>obj_pos.rotVec(joint_rot) + joint_pos</c> and <c>obj_rot * joint_rot</c>
    /// (llcontrolavatar.cpp:182-193) in Godot's column-vector terms. Never scaled: prim scale does not
    /// scale a control avatar's skeleton, and the attachment frame carries none.</summary>
    /// <param name="attachFrame">The attachment point's world transform
    /// (<see cref="TryGetAttachmentFrame"/>): the joint plus the point's own offset.</param>
    internal static Transform3D AttachedControlAvatarTransform(Transform3D attachFrame,
        System.Numerics.Vector3 localPosition, System.Numerics.Quaternion localRotation)
    {
        // SL -> Godot, the same two conversions a static attachment gets (AttachWorker) and a region
        // control avatar's rotation gets (ApplyControlAvatarPlacement).
        var rotation = new Godot.Quaternion(localRotation.X, localRotation.Z, -localRotation.Y, localRotation.W);
        var local = new Transform3D(
            new Basis(rotation.IsFinite() && rotation.LengthSquared() > 1e-12f ? rotation.Normalized() : Godot.Quaternion.Identity),
            new Godot.Vector3(localPosition.X, localPosition.Z, -localPosition.Y));

        var world = attachFrame * local;
        world.Basis = world.Basis.Orthonormalized();
        return world;
    }

    /// <summary>The wearer's attachment point in world space, from the skeleton's pose AS IT IS NOW.
    /// <see cref="TryGetAttachmentFrame"/> reads the scene node a static attachment hangs from, and a
    /// <c>BoneAttachment3D</c> only catches up with the bone when the engine updates the skeleton at
    /// the end of the frame -- one frame behind the pose the animation player has just written, which
    /// on a swinging arm or a running body is a visible gap between hand and pet. Same product the
    /// node ends up with: the bone's global pose, then the point's own offset on that bone.</summary>
    private bool TryGetPosedAttachmentFrame(AvatarVisual wearer, Guid attachmentId, out Transform3D frame)
    {
        frame = Transform3D.Identity;
        var skeleton = wearer.Skeleton;
        if (skeleton == null || !IsInstanceValid(skeleton) || !skeleton.IsInsideTree()) return false;
        if (!_attachmentNodes.TryGetValue(attachmentId, out var boneAttach) || !IsInstanceValid(boneAttach)) return false;
        if (!boneAttach.HasMeta("AttachPoint") || !boneAttach.HasMeta("BoneName")) return false;

        string boneName = boneAttach.GetMeta("BoneName").AsString();
        int boneIdx = skeleton.FindBone(boneName);
        if (boneIdx < 0) return false;

        var pose = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(boneIdx);
        frame = AttachmentPointMap.GetPoint((byte)boneAttach.GetMeta("AttachPoint").AsInt32()) is { } point
            ? pose * AttachPointOffset(point, wearer, boneName)
            : pose;
        return true;
    }

    // ---- routing ------------------------------------------------------------------------------------

    /// <summary>Called when a worn rigged mesh's rig is committed: if the attachment belongs to an
    /// ANIMATED object, the mesh is skinned to the object's own control avatar and the wearer gets
    /// nothing from it -- no joint overrides, no skin, no entry in its rigged list. True means "taken
    /// care of"; the caller must not rig it onto the wearer.</summary>
    /// <remarks>
    /// Whatever the wearer already had of this attachment (it was rigged onto the wearer before the
    /// root's flag arrived) is taken off first, joint overrides included. A mesh that cannot be built
    /// is still "taken care of": drawing it on the wearer would put the creature's weights on a
    /// human skeleton, which is the bug this replaces.
    /// </remarks>
    private bool TryRouteWornRigToControlAvatar(Guid entityId, PendingRig req)
    {
        if (_world == null || _avatarSkeleton == null || req.Visual.IsControlAvatar) return false;

        var entity = _world.GetEntity(entityId);
        if (entity == null) return false;
        if (!AnimatedMeshLinkset.IsAnimatedAttachment(_world, entity, out var root) || root == null) return false;

        var rootTransform = root.GetComponent<TransformComponent>();
        var attachment = root.GetComponent<AttachmentComponent>() ?? entity.GetComponent<AttachmentComponent>();
        if (rootTransform == null || attachment == null) return false;

        // The wearer gives it up: node, pick bodies, overrides, locks, fixup.
        DiscardRiggedAttachment(entityId, req.Visual);
        ReleaseWornJointOverrides(req.Visual, entityId);

        var ca = GetOrCreateControlAvatar(root.Id, attachment.AvatarEntityId, req.Visual, attachment.AttachmentPoint);
        if (ca == null) return true;

        _controlAvatarOfPrim[entityId] = root.Id;
        if (!InstallControlAvatarPart(ca, entityId, entityId == root.Id, req.MeshData, req.MeshId, req.Faces,
                req.DefaultFace, visible: true, applyMaterials: true, Godot.Vector3.Zero, rootTransform.Rotation))
        {
            _controlAvatarOfPrim.Remove(entityId);
            if (ca.Parts.Count == 0) FreeControlAvatar(root.Id);
            return true;
        }

        // Re-asserted: freeing a stale avatar while this one was being made drops the claims of the
        // parts it held, and this prim's was among them (the same trap BuildPendingControlPart has).
        _controlAvatarOfPrim[entityId] = root.Id;
        return true;
    }

    // ---- placement and lifecycle, per frame -----------------------------------------------------------

    /// <summary>Puts the skeleton where the wearer's attachment point has it. False while the point is
    /// not there to follow yet (the root's attachment node is built by UpdateAttachment, which may not
    /// have run for the root when a child's mesh arrives first): the avatar stays hidden until it is.
    /// </summary>
    private bool PlaceAttachedControlAvatar(ControlAvatar ca)
    {
        var root = _world?.GetEntity(ca.RootEntityId);
        var transform = root?.GetComponent<TransformComponent>();
        if (root == null || transform == null) return false;
        if (ca.WearerVisual == null || !TryGetPosedAttachmentFrame(ca.WearerVisual, ca.RootEntityId, out var frame)
            && !TryGetAttachmentFrame(ca.RootEntityId, out frame))
            return false;

        var world = AttachedControlAvatarTransform(frame, transform.Position, transform.Rotation);
        if (!world.Origin.IsFinite() || !world.Basis.X.IsFinite() || !world.Basis.Y.IsFinite() || !world.Basis.Z.IsFinite())
            return false;

        if (ca.Placed && world == ca.LastAttachedTransform) return true;
        if (IsInstanceValid(ca.Visual.Root) && ca.Visual.Root.IsInsideTree())
            ca.Visual.Root.GlobalTransform = world;
        ca.LastAttachedTransform = world;
        ca.Placed = true;
        return true;
    }

    /// <summary>One frame of a worn control avatar: still the wearer's, still worn -- else it goes --
    /// then follow the attachment point and the wearer's visibility. False when it must be dropped
    /// (the caller has queued it).</summary>
    private bool UpdateAttachedControlAvatar(ControlAvatar ca, Entity root)
    {
        // The wearer's visual is what its parts were skinned beside; a rebuilt or removed one takes
        // the control avatar with it (llcontrolavatar.cpp getAttachedAvatar() would be a different
        // avatar, or none). Rebuilt means the attachments are asked for again; gone means they follow
        // their own removal.
        bool wearerHere = _visuals.TryGetValue(ca.WearerEntityId, out var wearer);
        bool sameWearer = wearerHere && ReferenceEquals(wearer, ca.WearerVisual) && IsInstanceValid(wearer!.Root);
        bool stillWorn = root.GetComponent<AttachmentComponent>() is { } worn && worn.AvatarEntityId == ca.WearerEntityId;
        if (!sameWearer || !stillWorn)
        {
            if (wearerHere && !sameWearer && stillWorn) _controlAvatarRerouteRoots.Add(ca.RootEntityId);
            _controlAvatarsToDrop.Add(ca.RootEntityId);
            return false;
        }

        bool placed = PlaceAttachedControlAvatar(ca);
        // shouldRenderRigged() / isImpostor() are the wearer's: hide or cull the wearer and the pet is
        // gone with it (and stops animating -- AdvanceControlAvatarAnimations skips hidden parts).
        bool shown = placed && wearer!.Root.Visible;
        foreach (var part in ca.Parts.Values) SetPartVisible(part, shown);
        if (ca.Visual.Root.Visible != shown) ca.Visual.Root.Visible = shown;
        return true;
    }

    /// <summary>After the control avatars of wearers whose visual was rebuilt are freed: ask for their
    /// parts again, so the new visual gets a control avatar of its own. The guard in
    /// <see cref="UpdateAttachment"/> would swallow the repeat (same mesh, same faces), so the record
    /// of what was loaded goes first.</summary>
    private void RerouteDroppedControlAvatar(IEnumerable<Guid> primIds)
    {
        foreach (var primId in primIds)
        {
            _attachmentMeshIds.Remove(primId);
            CallDeferred(nameof(UpdateAttachment), primId.ToString());
        }
    }

    // ---- what a worn prim's update tells the control avatar ---------------------------------------------

    /// <summary>Called from <see cref="UpdateAttachment"/> for every update of a worn prim. Three
    /// things, all cheap for the ordinary attachment (a flag read, a dictionary miss):</summary>
    /// <list type="bullet">
    /// <item>an animation list changed somewhere in a worn animated object: its control avatar reads
    /// the union again on the next frame (the ObjectAnimation message names the ROOT, but every prim
    /// of the linkset counts);</item>
    /// <item>a root's animated-mesh flag flipped: the rest of the linkset is asked to look again,
    /// because no update reaches a child when only its root's block changed;</item>
    /// <item>this prim's mesh is on the wrong side of the flag now -- worn on the wearer but animated,
    /// or on a control avatar of an object that is not animated any more -- and is let go of; the
    /// caller reloads it, which is what routes it afresh. True in that case.</item>
    /// </list>
    private bool SyncWornAnimesh(Guid entityId, Entity entity, PrimitiveComponent? prim)
    {
        if (_world == null) return false;

        // The ordinary attachment -- not rigged, not an animated root, no control avatar in sight --
        // costs these three reads and nothing more.
        bool onControlAvatar = _controlAvatarOfPrim.ContainsKey(entityId);
        bool rigged = onControlAvatar || _riggedAttachments.ContainsKey(entityId);
        bool flagKnown = prim != null && (prim.IsAnimatedMesh || _wornAnimatedRootsSeen.Contains(entityId));
        if (_controlAvatars.Count == 0 && !rigged && !flagKnown) return false;

        var wornRoot = AnimatedMeshLinkset.AttachedRootOf(_world, entity);

        if (wornRoot != null && _controlAvatars.Count > 0
            && _controlAvatars.TryGetValue(wornRoot.Id, out var ca) && ca.IsAttached)
            ca.AnimationsDirty = true;

        if (wornRoot == entity && prim != null && prim.IsAnimatedMesh != _wornAnimatedRootsSeen.Contains(entityId))
        {
            if (prim.IsAnimatedMesh) _wornAnimatedRootsSeen.Add(entityId);
            else _wornAnimatedRootsSeen.Remove(entityId);

            var children = LinksetChildren?.Invoke(entity.RegionHandle, entity.LocalId);
            if (children != null)
                foreach (var childId in children)
                    if (childId != entityId) CallDeferred(nameof(UpdateAttachment), childId.ToString());
        }

        if (!rigged) return false;

        bool animated = wornRoot?.GetComponent<PrimitiveComponent>()?.IsAnimatedMesh == true;
        if (animated == onControlAvatar) return false;

        if (onControlAvatar) ReleaseControlAvatarMesh(entityId);
        return true;
    }

    // ---- diagnostics ----------------------------------------------------------------------------------

    private string WearerLabel(ControlAvatar ca) =>
        ca.WearerVisual == null ? "?" : ca.WearerVisual.IsSelf ? "SELF" : ca.WearerVisual.AgentId.ToString()[..8];

    /// <summary>--diag: a worn control avatar was made. Printed with its first part, so the part count
    /// is the number the avatar has when it first has something to draw.</summary>
    private void LogAttachedControlAvatarCreated(ControlAvatar ca)
    {
        if (!Diagnostics.Enabled || ca.CreatedLogged) return;
        ca.CreatedLogged = true;
        GD.Print($"[AttachAnimesh] control avatar created root={ObjectIdLabel(ca.RootEntityId)} wearer={WearerLabel(ca)} " +
                 $"point={ca.AttachmentPoint} parts={ca.Parts.Count}");
    }

    /// <summary>--diag: a worn control avatar was released, with the parts it still had.</summary>
    private void LogAttachedControlAvatarReleased(ControlAvatar ca)
    {
        if (!Diagnostics.Enabled || !ca.CreatedLogged) return;
        GD.Print($"[AttachAnimesh] control avatar released root={ObjectIdLabel(ca.RootEntityId)} wearer={WearerLabel(ca)} " +
                 $"point={ca.AttachmentPoint} parts={ca.Parts.Count}");
    }

    /// <summary>The SL object UUID of an entity, 8 characters -- what an <c>[ObjectAnimation]
    /// object=</c> line prints.</summary>
    private string ObjectIdLabel(Guid entityId)
    {
        var uuid = _world?.GetEntity(entityId)?.GetComponent<MetadataComponent>()?.Id ?? Guid.Empty;
        return uuid == Guid.Empty ? "--------" : uuid.ToString("N")[..8];
    }
}
