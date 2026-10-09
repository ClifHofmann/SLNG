using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SLNG.Assets;
using SLNG.Core;
using SLNG.Core.Components;

namespace SLNG.App;

// FEAT-ANIMESH-01 -- control avatars.
//
// An animated-mesh ("animesh") object is a rigged mesh the SIM has flagged (the Extended Mesh
// block on the linkset ROOT, see PrimitiveComponent.IsAnimatedMesh). The reference viewer gives
// such an object a CONTROL AVATAR: the stock avatar skeleton, never given an appearance, no body
// parts drawn, owned by the root prim and placed on it. Its rigged meshes are skinned to that
// skeleton exactly as a worn rigged mesh is skinned to a person's. Without it SLNG drew the mesh
// as a raw static one -- unskinned, the bind shape's axis conversion never undone -- which is why
// the first animesh seen in-world lay on its side (spec: docs/specs/FEAT-ANIMESH-01-*.md, whose
// "Viewer parity" section is the contract for everything below).
//
// This file is deliberately the only place that knows about control avatars. ObjectRenderer
// decides WHICH prims are animesh parts and hands each one's decoded mesh over through the public
// methods here; everything below reuses the worn-rigged-mesh machinery in AvatarRenderer.cs --
// SkeletonBuilder, ApplyShape, ApplyJointPositionOverrides, BuildRiggedMeshInstance,
// AddRiggedPickBody, ApplyFaceMaterialsAsync -- rather than copying any of it. A control avatar is
// an AvatarVisual that is NOT in _visuals: the per-frame avatar loops (draw distance, name tags,
// animation advance, head gaze) would otherwise treat a robot as a person.

public partial class AvatarRenderer
{
    /// <summary>One prim's share of a control avatar: the rigged mesh it was handed and the node
    /// built from it.</summary>
    private sealed class ControlAvatarPart
    {
        public Guid PrimEntityId;
        public MeshData MeshData = null!;
        public Guid MeshId;
        public FaceTexture[]? Faces;
        public FaceTexture DefaultFace;
        public bool Visible = true;
        public MeshInstance3D? Mi;
        /// <summary>Rest-pose extent as measured when the part was built (skeleton space and
        /// world axes). Kept for the self-test and for anyone asking "how big is this robot".</summary>
        public RiggedExtent? Extent;
    }

    /// <summary>One animesh object's skeleton, and every rigged prim of that object skinned to
    /// it (the root's own mesh and any rigged child prim -- one skeleton per ROOT).</summary>
    private sealed class ControlAvatar
    {
        public readonly Guid RootEntityId;
        public readonly AvatarVisual Visual;
        public readonly Dictionary<Guid, ControlAvatarPart> Parts = new();
        /// <summary>Meshes already described by an <c>[Animesh]</c> line (--diag), so a LOD
        /// re-assignment does not repeat it.</summary>
        public readonly HashSet<Guid> ReportedMeshes = new();

        /// <summary><c>bind_rot</c> of the ROOT prim's mesh; identity until the root's own rigged
        /// mesh has arrived, and again whenever the root prim has no skin (viewer:
        /// llcontrolavatar.cpp:226-235).</summary>
        public System.Numerics.Quaternion BindRotation = System.Numerics.Quaternion.Identity;

        // What was last written to the Node3D, so a stationary robot costs no interop call.
        public bool Placed;
        public bool RotationDirty = true;
        public System.Numerics.Quaternion LastObjectRotation = System.Numerics.Quaternion.Identity;
        public Godot.Quaternion GodotRotation = Godot.Quaternion.Identity;
        public Godot.Vector3 LastPosition;
        public Godot.Quaternion LastWrittenRotation;

        // FEAT-ANIMESH-02 (AvatarRenderer.ControlAvatarAnimation.cs). The skeleton, and with it
        // the player on Visual.AnimPlayer, outlives every re-rig of its meshes, so none of this is
        // touched by one.
        /// <summary>The union of what the object's prims signal, in animation-id order.</summary>
        public SignaledAnimation[] Signaled = Array.Empty<SignaledAnimation>();
        /// <summary>What the player was last handed (id and sequence id): the signalled animations
        /// whose assets have arrived.</summary>
        public SignaledAnimation[] Playing = Array.Empty<SignaledAnimation>();
        public readonly Dictionary<Guid, AnimationData> Loaded = new();
        public readonly HashSet<Guid> Fetching = new();
        /// <summary>Some prim's list changed (or the avatar is new): recompute the union on the next
        /// frame. A flag, not a recompute, so a linkset whose prims all change in one frame costs
        /// one pass.</summary>
        public bool AnimationsDirty = true;
        /// <summary>--diag: print the pose once, after the change just applied has moved a frame.</summary>
        public bool ReportPoseAfterAdvance;

        // BUG-AVATAR-10 (AvatarRenderer.WornAnimesh.cs): a control avatar of a WORN animated object
        // follows the wearer's attachment point instead of a region position, and the wearer's
        // visibility. Guid.Empty for an object that is rezzed in the world.
        /// <summary>The avatar entity wearing the object; empty for a region animesh.</summary>
        public Guid WearerEntityId;
        /// <summary>The wearer's visual as it was when this avatar was made: a different one in
        /// <c>_visuals</c> later means the wearer was rebuilt, and this avatar's skeleton is stale.</summary>
        public AvatarVisual? WearerVisual;
        public byte AttachmentPoint;
        public bool IsAttached => WearerEntityId != Guid.Empty;
        public Transform3D LastAttachedTransform;
        /// <summary>--diag: the "created" line was printed (with the first part).</summary>
        public bool CreatedLogged;

        public ControlAvatar(Guid rootEntityId, AvatarVisual visual)
        {
            RootEntityId = rootEntityId;
            Visual = visual;
        }
    }

    /// <summary>The newest request for a prim whose rig is waiting for its queued turn. Same shape
    /// as <see cref="PendingRig"/> (BUG-PERF-01): repeats collapse to one queue item that rigs the
    /// LATEST request rather than the first.</summary>
    private sealed record PendingControlPart(
        MeshData MeshData, Guid MeshId, Guid RootEntityId,
        FaceTexture[]? Faces, FaceTexture DefaultFace, bool Visible);

    /// <summary>The rest-pose extent of a skinned mesh, measured while it is built.</summary>
    private sealed class RiggedExtent
    {
        /// <summary>Rotation from the skeleton's space into world axes.</summary>
        public Basis Frame = Basis.Identity;
        public Godot.Vector3 LocalMin = new(float.MaxValue, float.MaxValue, float.MaxValue);
        public Godot.Vector3 LocalMax = new(float.MinValue, float.MinValue, float.MinValue);
        public Godot.Vector3 WorldMin = new(float.MaxValue, float.MaxValue, float.MaxValue);
        public Godot.Vector3 WorldMax = new(float.MinValue, float.MinValue, float.MinValue);
        public bool Valid;

        public void Add(Godot.Vector3 local)
        {
            var world = Frame * local;
            LocalMin = new Godot.Vector3(Mathf.Min(LocalMin.X, local.X), Mathf.Min(LocalMin.Y, local.Y), Mathf.Min(LocalMin.Z, local.Z));
            LocalMax = new Godot.Vector3(Mathf.Max(LocalMax.X, local.X), Mathf.Max(LocalMax.Y, local.Y), Mathf.Max(LocalMax.Z, local.Z));
            WorldMin = new Godot.Vector3(Mathf.Min(WorldMin.X, world.X), Mathf.Min(WorldMin.Y, world.Y), Mathf.Min(WorldMin.Z, world.Z));
            WorldMax = new Godot.Vector3(Mathf.Max(WorldMax.X, world.X), Mathf.Max(WorldMax.Y, world.Y), Mathf.Max(WorldMax.Z, world.Z));
            Valid = true;
        }
    }

    /// <summary>Keyed by the ROOT prim's entity id.</summary>
    private readonly Dictionary<Guid, ControlAvatar> _controlAvatars = new();

    /// <summary>Prim entity id -> the root whose control avatar it is (or is about to be) skinned
    /// to. Present from the moment a mesh is accepted, i.e. before its rig has been built.</summary>
    private readonly Dictionary<Guid, Guid> _controlAvatarOfPrim = new();

    private readonly Dictionary<Guid, PendingControlPart> _pendingControlParts = new();

    /// <summary>Scratch for <see cref="UpdateControlAvatars"/>, so the per-frame pass allocates
    /// nothing.</summary>
    private readonly List<Guid> _controlAvatarsToDrop = new();

    private bool _controlAvatarSkeletonWarned;

    // ---- public API (main thread only; called by ObjectRenderer) --------------------------------

    /// <summary>Hands an animated-mesh prim's decoded RIGGED mesh to its object's control avatar,
    /// creating the avatar if this is its first mesh. Returns false when it cannot take it (the
    /// mesh has no skin, the skeleton definition did not load, the prim is not part of an animesh
    /// object after all), in which case the caller keeps drawing the prim as a plain static mesh.
    /// </summary>
    /// <remarks>
    /// Cheap to call again with the same arguments -- an unchanged request is recognised and
    /// dropped, the way <c>_attachmentMeshIds</c> swallows the repeats a worn item sends. A
    /// different <paramref name="meshData"/> instance (another LOD of the same asset) or changed
    /// face records REPLACE the prim's previous node; they never add a second one.
    ///
    /// <para>The rig itself is built later, on the Visual lane of
    /// <see cref="MainThreadWorkQueue"/>: it is the most expensive single unit of main-thread work
    /// the renderer has (BUG-PERF-01), so it gets the same coalescing the worn path does.</para>
    /// </remarks>
    public bool SetControlAvatarMesh(Guid primEntityId, MeshData meshData, Guid meshId, bool visible)
    {
        if (_world == null || meshData.Skin == null) return false;

        if (_avatarSkeleton == null)
        {
            if (!_controlAvatarSkeletonWarned)
            {
                _controlAvatarSkeletonWarned = true;
                Logger.Warn("[Animesh] the avatar skeleton definition (res://assets/avatar/avatar_skeleton.xml) " +
                            "did not load -- animated meshes cannot be skinned and stay static");
            }
            return false;
        }

        var entity = _world.GetEntity(primEntityId);
        var prim = entity?.GetComponent<PrimitiveComponent>();
        if (entity == null || prim == null) return false;
        if (!AnimatedMeshLinkset.IsAnimatedPart(_world, entity, out var root) || root == null) return false;

        var faces = prim.Faces;
        var defaultFace = DefaultFaceOf(prim);

        if (_controlAvatarOfPrim.TryGetValue(primEntityId, out var knownRoot))
        {
            if (knownRoot == root.Id)
            {
                // Same request as the one already built, or already waiting its turn: nothing to do.
                if (_pendingControlParts.TryGetValue(primEntityId, out var pending))
                {
                    if (ReferenceEquals(pending.MeshData, meshData) && pending.DefaultFace == defaultFace
                        && SameFaces(pending.Faces, faces))
                    {
                        _pendingControlParts[primEntityId] = pending with { Visible = visible };
                        return true;
                    }
                }
                else if (_controlAvatars.TryGetValue(knownRoot, out var existing)
                         && existing.Parts.TryGetValue(primEntityId, out var part)
                         && ReferenceEquals(part.MeshData, meshData) && part.DefaultFace == defaultFace
                         && SameFaces(part.Faces, faces))
                {
                    SetPartVisible(part, visible);
                    return true;
                }
            }
            else
            {
                // Re-linked under another root: leave the old object's skeleton first.
                ReleaseControlAvatarMesh(primEntityId);
            }
        }

        _controlAvatarOfPrim[primEntityId] = root.Id;
        _pendingControlParts[primEntityId] = new PendingControlPart(meshData, meshId, root.Id, faces, defaultFace, visible);

        MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual,
            () => BuildPendingControlPart(primEntityId),
            coalesceKey: $"controlavatar.rig:{primEntityId}", label: "controlavatar.rig");
        return true;
    }

    /// <summary>Takes a prim's mesh out of its control avatar -- and the skeleton with it when that
    /// was the last one. Safe to call for a prim that has none, and more than once.</summary>
    public void ReleaseControlAvatarMesh(Guid primEntityId)
    {
        _pendingControlParts.Remove(primEntityId);
        if (!_controlAvatarOfPrim.Remove(primEntityId, out var rootId)) return;
        if (!_controlAvatars.TryGetValue(rootId, out var ca)) return;

        if (ca.Parts.Remove(primEntityId, out var part))
            DiscardControlAvatarPart(ca, part, keepFixupForMesh: Guid.Empty);

        if (primEntityId == rootId)
        {
            // The root prim's own mesh is what supplies the bind rotation.
            ca.BindRotation = System.Numerics.Quaternion.Identity;
            ca.RotationDirty = true;
        }
        if (ca.Parts.Count == 0) FreeControlAvatar(rootId);
    }

    /// <summary>Follows the prim's own draw-distance visibility, which ObjectRenderer's cull sweep
    /// decides: a control avatar is not an entry in <c>_visuals</c> and gets none of its own.</summary>
    public void SetControlAvatarMeshVisible(Guid primEntityId, bool visible)
    {
        if (_pendingControlParts.TryGetValue(primEntityId, out var pending) && pending.Visible != visible)
            _pendingControlParts[primEntityId] = pending with { Visible = visible };

        if (!_controlAvatarOfPrim.TryGetValue(primEntityId, out var rootId)) return;
        if (!_controlAvatars.TryGetValue(rootId, out var ca)) return;
        if (ca.Parts.TryGetValue(primEntityId, out var part)) SetPartVisible(part, visible);
    }

    // ---- building -------------------------------------------------------------------------------

    private static FaceTexture DefaultFaceOf(PrimitiveComponent prim) =>
        new(prim.TextureId, prim.RenderMaterialId, prim.LegacyMaterialId, prim.ColorTint,
            1.0f, 1.0f, 0.0f, 0.0f, 0.0f, Fullbright: prim.Fullbright);

    private static bool SameFaces(FaceTexture[]? a, FaceTexture[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Length != b.Length) return false;
        return a.AsSpan().SequenceEqual(b);
    }

    private static void SetPartVisible(ControlAvatarPart part, bool visible)
    {
        part.Visible = visible;
        if (part.Mi != null && IsInstanceValid(part.Mi) && part.Mi.Visible != visible) part.Mi.Visible = visible;
    }

    private void BuildPendingControlPart(Guid primId)
    {
        if (!_pendingControlParts.Remove(primId, out var req)) return;
        if (_world == null) return;

        // Gone, or claimed by another root, while it waited.
        if (_world.GetEntity(primId) == null)
        {
            _controlAvatarOfPrim.Remove(primId);
            return;
        }
        if (!_controlAvatarOfPrim.TryGetValue(primId, out var rootId) || rootId != req.RootEntityId) return;

        // No root, no placement: the object this belongs to is not in the world (any more).
        var rootEntity = _world.GetEntity(rootId);
        var rootTransform = rootEntity?.GetComponent<TransformComponent>();
        if (rootEntity == null || rootTransform == null)
        {
            _controlAvatarOfPrim.Remove(primId);
            return;
        }

        var ca = GetOrCreateControlAvatar(rootId);
        if (ca == null)
        {
            _controlAvatarOfPrim.Remove(primId);
            return;
        }

        if (!InstallControlAvatarPart(ca, primId, primId == rootId, req.MeshData, req.MeshId, req.Faces,
                req.DefaultFace, req.Visible, applyMaterials: true,
                RenderConfig.ToGodot(rootEntity.RegionHandle, rootTransform.Position), rootTransform.Rotation))
        {
            _controlAvatarOfPrim.Remove(primId);
            if (ca.Parts.Count == 0) FreeControlAvatar(rootId);
            return;
        }

        // Re-asserted: freeing a stale avatar while this one was being made drops the claims of the
        // parts it held, and this prim's was among them.
        _controlAvatarOfPrim[primId] = rootId;
    }

    /// <summary>Everything that happens to a control avatar when one of its prims' meshes is
    /// rigged, minus the world lookups: the root's mesh supplies the bind rotation, the avatar is
    /// placed on its root prim, and only THEN is the mesh skinned and measured, so the extent is
    /// taken in the frame it will stand in.</summary>
    private bool InstallControlAvatarPart(ControlAvatar ca, Guid primId, bool isRoot, MeshData meshData, Guid meshId,
        FaceTexture[]? faces, FaceTexture defaultFace, bool visible, bool applyMaterials,
        Godot.Vector3 rootRenderPosition, System.Numerics.Quaternion rootRotation)
    {
        if (isRoot)
        {
            ca.BindRotation = ControlAvatarPlacement.BindRotation(meshData.Skin!.BindShapeMatrix);
            ca.RotationDirty = true;
        }
        // A worn object follows its attachment point (BUG-AVATAR-10), a rezzed one its root prim.
        if (ca.IsAttached) PlaceAttachedControlAvatar(ca);
        else ApplyControlAvatarPlacement(ca, rootRenderPosition, rootRotation);

        return BuildControlAvatarPart(ca, primId, meshData, meshId, faces, defaultFace, isRoot, visible, applyMaterials);
    }

    private ControlAvatar? GetOrCreateControlAvatar(Guid rootId, Guid wearerEntityId = default,
        AvatarVisual? wearerVisual = null, byte attachmentPoint = 0)
    {
        if (_controlAvatars.TryGetValue(rootId, out var existing))
        {
            // The same object can be rezzed one moment and worn the next (or worn by someone else,
            // or by the same wearer after a rebuild): a skeleton made for the other situation is not
            // reused.
            bool sameOwner = existing.WearerEntityId == wearerEntityId && ReferenceEquals(existing.WearerVisual, wearerVisual);
            if (sameOwner && IsInstanceValid(existing.Visual.Root) && existing.Visual.Skeleton != null
                && IsInstanceValid(existing.Visual.Skeleton))
                return existing;
            FreeControlAvatar(rootId);
        }

        if (_avatarSkeleton == null) return null;

        var visual = new AvatarVisual { IsControlAvatar = true };
        visual.Root.Name = $"ControlAvatar_{rootId:N}";
        // A child of the renderer, NOT of the prim's node: that node carries the prim's SCALE,
        // and prim scale does not scale a control avatar's skeleton (llcontrolavatar.cpp:100-155
        // sets the global scale to 1 unless the animated bounding box is over 64 m).
        AddChild(visual.Root);

        var skeleton = SkeletonBuilder.Build(_avatarSkeleton);
        skeleton.Name = "Skeleton3D";
        visual.Root.AddChild(skeleton);
        visual.Skeleton = skeleton;

        // The default, undistorted skeleton (spec caveat 2: whether default-weight skeletal
        // distortion deltas are ever applied to a control avatar was not traced). LastDistortions
        // is the visual's own empty map, which later joint-override passes re-use.
        ApplyShape(visual, skeleton, _avatarSkeleton, visual.LastDistortions);
        skeleton.ResetBonePoses();

        // The player is bound once, here: it lives as long as the skeleton does. A control avatar's
        // skeleton root is the root prim, not the pelvis-height point a real avatar's is, so a pelvis
        // position key is an ABSOLUTE position here (AvatarAnimationPlayer.AbsolutePelvisPosition).
        visual.AnimPlayer.SetSkeleton(skeleton);
        visual.AnimPlayer.AbsolutePelvisPosition = true;
        // And a bone no active animation drives keeps its last value: nothing else moves the body of
        // an object with no default motions (AvatarAnimationPlayer.HoldUndrivenBones).
        visual.AnimPlayer.HoldUndrivenBones = true;

        var ca = new ControlAvatar(rootId, visual)
        {
            WearerEntityId = wearerEntityId,
            WearerVisual = wearerVisual,
            AttachmentPoint = attachmentPoint,
        };
        // Nothing to follow until the first placement: a worn skeleton at the origin for a frame is a
        // creature in the middle of the region.
        if (ca.IsAttached) visual.Root.Visible = false;
        _controlAvatars[rootId] = ca;
        return ca;
    }

    /// <summary>Skins one prim's mesh to a control avatar's skeleton and replaces whatever that
    /// prim had there before. Everything but the world lookups, so the self-test can drive it.
    /// </summary>
    private bool BuildControlAvatarPart(ControlAvatar ca, Guid primId, MeshData meshData, Guid meshId,
        FaceTexture[]? faces, FaceTexture defaultFace, bool isRoot, bool visible, bool applyMaterials)
    {
        var visual = ca.Visual;
        var skeleton = visual.Skeleton!;
        var skin = meshData.Skin!;

        // The mesh may be rigged to shifted joint positions; apply them BEFORE binding, as the
        // worn path does, so invBind * jointWorld cancels at the intended pose. They accumulate
        // across the object's meshes in visual.JointPosOverrides.
        ApplyJointPositionOverrides(visual, skeleton, skin, meshId);
        // A mesh that changes the skeleton (a new or different joint-position override, a newly
        // locked scale) makes ApplyJointPositionOverrides rewrite every rest and call
        // Skeleton3D.ResetBonePoses, which puts EVERY bone at its new rest -- including the ones a
        // held animation pose was standing on. A second mesh arriving mid-animation would otherwise
        // drop the robot into its T-pose for good (nothing re-drives a bone that no animation keys).
        // A re-rig with the same overrides never gets that far. Only the bones the animations drove
        // are put back; everything else keeps the fresh rest.
        visual.AnimPlayer.ReapplyHeldPose();

        var extent = new RiggedExtent { Frame = new Basis(visual.Root.Quaternion) };
        var mi = BuildRiggedMeshInstance(meshData, skeleton, meshId, visual, faces, defaultFace, out var faceIndices,
                                         extentSink: extent);
        if (mi == null)
        {
            Logger.Warn($"[Animesh] mesh {meshId.ToString("N")[..8]} on prim {primId.ToString("N")[..8]} " +
                        "produced no geometry once skinned -- nothing is drawn for it");
            return false;
        }
        mi.Name = $"AnimeshPart_{primId:N}";

        // The generous box a worn mesh gets (a bind pose can be parked far from where it renders),
        // grown to the measured rest-pose extent: an animesh can be much bigger than a person, and
        // a box that stops at 4 m would cull a large robot while half of it is still on screen.
        var box = new Aabb(new Godot.Vector3(-4, -4, -4), new Godot.Vector3(8, 8, 8));
        if (extent.Valid)
        {
            var measured = new Aabb(extent.LocalMin, extent.LocalMax - extent.LocalMin);
            float margin = Mathf.Max(1f, 0.25f * Mathf.Max(measured.Size.X, Mathf.Max(measured.Size.Y, measured.Size.Z)));
            box = box.Merge(measured.Grow(margin));
        }
        mi.CustomAabb = box;
        mi.SortingOffset = RiggedAlphaSortTieBreak(primId, faces, defaultFace, faceIndices);
        mi.Visible = visible;

        // Replace, never add: a LOD change (or an edited face) arrives as a second rig for the
        // same prim, and a stacked duplicate would draw twice for as long as the object lives.
        if (ca.Parts.Remove(primId, out var old))
            DiscardControlAvatarPart(ca, old, keepFixupForMesh: meshId);

        skeleton.AddChild(mi);
        AddRiggedPickBody(mi, visual, skeleton, primId);
        // The skeleton path must be set after the node is in the tree so Godot can resolve it.
        mi.Skeleton = mi.GetPathTo(skeleton);
        visual.RiggedAttachments.Add((mi, meshData, meshId));

        ca.Parts[primId] = new ControlAvatarPart
        {
            PrimEntityId = primId,
            MeshData = meshData,
            MeshId = meshId,
            Faces = faces,
            DefaultFace = defaultFace,
            Visible = visible,
            Mi = mi,
            Extent = extent,
        };

        if (applyMaterials)
            _ = ApplyFaceMaterialsAsync(mi, faceIndices, faces, defaultFace, visual, meshId);

        if (Diagnostics.Enabled && ca.ReportedMeshes.Add(meshId))
            ReportAnimeshRig(ca, primId, meshId, skin, mi, extent, isRoot);
        if (ca.IsAttached) LogAttachedControlAvatarCreated(ca);

        return true;
    }

    /// <summary>The one <c>[Animesh]</c> line per root + mesh: how the skeleton was turned and how
    /// big the mesh ends up at rest, in metres, in world axes -- so "does it stand up, and at the
    /// authored size?" has a number to be checked against instead of a screenshot.</summary>
    private void ReportAnimeshRig(ControlAvatar ca, Guid primId, Guid meshId, MeshSkin skin,
        MeshInstance3D mi, RiggedExtent extent, bool isRoot)
    {
        int joints = skin.JointNames.Length;
        int resolved = mi.Skin?.GetBindCount() ?? 0;
        var ownBind = ControlAvatarPlacement.EulerDegrees(ControlAvatarPlacement.BindRotation(skin.BindShapeMatrix));
        var placed = ControlAvatarPlacement.EulerDegrees(ca.BindRotation);

        string size = "extent unavailable";
        if (extent.Valid)
        {
            // SL axes (Z up) rather than Godot's, since that is what everything else a creator
            // sees is in: SL (x, y, z) = Godot (x, -z, y).
            var min = extent.WorldMin;
            var max = extent.WorldMax;
            float height = max.Y - min.Y;
            float width = Mathf.Max(max.X - min.X, max.Z - min.Z);
            size = $"extent(world axes, rest pose, from the root prim) min=({min.X:0.###}, {-max.Z:0.###}, {min.Y:0.###}) " +
                   $"max=({max.X:0.###}, {-min.Z:0.###}, {max.Y:0.###}) m  " +
                   $"height(up)={height:0.###} width={width:0.###}";
        }

        // The internal entity id is what the other logs use; the local id and the object's own UUID
        // are what an object inspector in another viewer shows, so the line can be matched to one.
        var rootEntity = _world?.GetEntity(ca.RootEntityId);
        string ids = rootEntity == null
            ? ""
            : $" local={rootEntity.LocalId} uuid={(rootEntity.GetComponent<MetadataComponent>()?.Id ?? Guid.Empty).ToString("N")[..8]}";

        var rootWorld = ControlAvatarRootSl(ca);
        GD.Print($"[Animesh] root={ca.RootEntityId.ToString("N")[..8]}{ids} mesh={meshId.ToString("N")[..8]} " +
                 $"rootWorld=({rootWorld.X:0.###}, {rootWorld.Y:0.###}, {rootWorld.Z:0.###}) " +
                 $"prim={primId.ToString("N")[..8]} ({(isRoot ? "root" : "child")}) " +
                 $"joints={joints} resolved={resolved} " +
                 $"bindRot=({ownBind.X:0.#}, {ownBind.Y:0.#}, {ownBind.Z:0.#})deg " +
                 $"placementBindRot=({placed.X:0.#}, {placed.Y:0.#}, {placed.Z:0.#})deg " +
                 $"rootSkin={(ca.Parts.ContainsKey(ca.RootEntityId) ? "yes" : "no")} " +
                 $"pelvisOffset={skin.PelvisOffset:0.###} {size}");
    }

    /// <summary>Takes one part's node, pick colliders and bookkeeping off the skeleton. The pelvis
    /// fixup it contributed goes too (viewer parity: removeAttachmentOverridesForObject calls
    /// removePelvisFixup), unless the mesh replacing it is the same asset and has just re-set it.
    /// </summary>
    private void DiscardControlAvatarPart(ControlAvatar ca, ControlAvatarPart part, Guid keepFixupForMesh)
    {
        ClearRiggedPickBodies(part.PrimEntityId);
        var mi = part.Mi;
        part.Mi = null;
        if (mi != null)
        {
            ca.Visual.RiggedAttachments.RemoveAll(r => r.Mi == mi);
            if (IsInstanceValid(mi))
            {
                // RemoveChild first: QueueFree only schedules the deletion, and the replacement
                // is added in the same frame (see UpdateAttachment's identical trap).
                mi.GetParent()?.RemoveChild(mi);
                mi.QueueFree();
            }
        }
        if (part.MeshId != keepFixupForMesh) ca.Visual.PelvisFixups.Remove(part.MeshId);
    }

    private void FreeControlAvatar(Guid rootId)
    {
        if (!_controlAvatars.Remove(rootId, out var ca)) return;

        if (ca.IsAttached) LogAttachedControlAvatarReleased(ca);
        foreach (var primId in ca.Parts.Keys)
        {
            // The pick bodies hang off the skeleton and go with it; only the dictionary entry is ours.
            _riggedPickBodies.Remove(primId);
            _controlAvatarOfPrim.Remove(primId);
        }
        ca.Parts.Clear();
        ca.Visual.QueueFree();
    }

    // ---- placement -------------------------------------------------------------------------------

    /// <summary>Re-places every control avatar on its root prim. Called from <c>_Process</c>: an
    /// animesh can be moved or turned by a script or by physics at any time, and the viewer re-runs
    /// the same match on every root transform update and every frame (lldrawable.cpp:731-737,
    /// llvoavatar.cpp:4713). One dictionary walk, two component lookups per avatar, and a Node3D
    /// write only when something moved.</summary>
    private void UpdateControlAvatars(float delta)
    {
        if (_controlAvatars.Count == 0 || _world == null) return;

        foreach (var (rootId, ca) in _controlAvatars)
        {
            var root = _world.GetEntity(rootId);
            var transform = root?.GetComponent<TransformComponent>();
            if (root == null || transform == null || !IsInstanceValid(ca.Visual.Root))
            {
                // A control avatar never outlives its object.
                _controlAvatarsToDrop.Add(rootId);
                continue;
            }
            // BUG-AVATAR-10: a worn object follows its attachment point and its wearer's visibility.
            if (ca.IsAttached)
            {
                if (!UpdateAttachedControlAvatar(ca, root)) continue;
            }
            else
            {
                ApplyControlAvatarPlacement(ca, RenderConfig.ToGodot(root.RegionHandle, transform.Position),
                    transform.Rotation);
            }

            // FEAT-ANIMESH-02: re-read the signalled animations when a prim of the object said they
            // changed, then move whatever is playing on. Both are a flag and a bool for an object
            // with nothing signalled.
            if (ca.AnimationsDirty)
            {
                ca.AnimationsDirty = false;
                RecomputeControlAvatarAnimations(ca, root);
            }
            AdvanceControlAvatarAnimations(ca, delta);
        }

        if (_controlAvatarsToDrop.Count == 0) return;
        foreach (var rootId in _controlAvatarsToDrop)
        {
            List<Guid>? reroute = null;
            if (_controlAvatars.TryGetValue(rootId, out var gone))
            {
                foreach (var primId in gone.Parts.Keys) _pendingControlParts.Remove(primId);
                if (_controlAvatarRerouteRoots.Remove(rootId)) reroute = gone.Parts.Keys.ToList();
            }
            FreeControlAvatar(rootId);
            if (reroute != null) RerouteDroppedControlAvatar(reroute);
        }
        _controlAvatarsToDrop.Clear();
    }

    /// <summary>The viewer's <c>LLControlAvatar::matchVolumeTransform</c> for an object that is not
    /// an attachment (llcontrolavatar.cpp:203-246): the skeleton root sits at the root prim's render
    /// position and is turned by <c>bind_rot * obj_rot</c>. Prim scale plays no part.</summary>
    private void ApplyControlAvatarPlacement(ControlAvatar ca, Godot.Vector3 renderPosition,
        System.Numerics.Quaternion objectRotation)
    {
        if (!renderPosition.IsFinite()) return;

        if (ca.RotationDirty || objectRotation != ca.LastObjectRotation)
        {
            var world = ControlAvatarPlacement.Compose(ca.BindRotation, objectRotation);
            // SL -> Godot, the same conversion ObjectRenderer applies to a prim's rotation.
            var q = new Godot.Quaternion(world.X, world.Z, -world.Y, world.W);
            if (q.IsFinite()) ca.GodotRotation = q.Normalized();
            ca.LastObjectRotation = objectRotation;
            ca.RotationDirty = false;
        }

        // The active pelvis fixup, along WORLD up and unrotated -- getRenderPosition adds
        // (0, 0, fixup) to the root's position in region axes (llviewerobject.cpp:4777-4790).
        var position = renderPosition;
        if (ca.Visual.PelvisFixups.Count > 0 && TryGetActivePelvisFixup(ca.Visual, out float fixup))
            position.Y += fixup;

        if (ca.Placed && position == ca.LastPosition && ca.GodotRotation == ca.LastWrittenRotation) return;

        ca.Visual.Root.Position = position;
        ca.Visual.Root.Quaternion = ca.GodotRotation;
        ca.LastPosition = position;
        ca.LastWrittenRotation = ca.GodotRotation;
        ca.Placed = true;
    }

    // ---- self-test ---------------------------------------------------------------------------------

    /// <summary>A synthetic rigged mesh for the self-tests: a bar 0.4 m (x) by 0.1 m (y) by 2 m (z),
    /// AUTHORED upright and standing on the origin, weighted entirely to mPelvis, whose bind shape
    /// turns it 90 degrees about X and scales it by <paramref name="scale"/>. The inverse bind
    /// matrix is the pelvis's own, so the chain <c>p * BSM * invBind * jointWorld</c> reduces to
    /// <c>p * BSM * (skeleton rotation)</c>.</summary>
    internal MeshData SelfTestRiggedBar(float scale, out System.Numerics.Vector3[] corners, float? pelvisHeight = null)
    {
        var pelvis = _avatarSkeleton!.GetBone("mPelvis")!;
        corners = new System.Numerics.Vector3[8];
        for (int i = 0; i < 8; i++)
            corners[i] = new System.Numerics.Vector3((i & 1) == 0 ? -0.2f : 0.2f, (i & 2) == 0 ? -0.05f : 0.05f, (i & 4) == 0 ? 0f : 2f);
        // Distinct, non-collinear UVs on every face, so tangent generation has nothing degenerate.
        var uvs = corners.Select(c => new System.Numerics.Vector2(c.X + c.Y * 4f, c.Z * 0.5f + c.Y * 3f)).ToArray();
        var normals = corners.Select(_ => System.Numerics.Vector3.UnitZ).ToArray();
        var weights = corners.Select(_ => new VertexBoneWeights(0, 0, 0, 0, 1f, 0f, 0f, 0f)).ToArray();
        int[] indices =
        {
            0, 1, 3,  0, 3, 2,   4, 5, 7,  4, 7, 6,
            0, 1, 5,  0, 5, 4,   1, 3, 7,  1, 7, 5,
            3, 2, 6,  3, 6, 7,   2, 0, 4,  2, 4, 6,
        };
        var submesh = new MeshSubmesh(corners, normals, uvs, indices, 0, weights);

        var bindShape = System.Numerics.Matrix4x4.CreateScale(scale) * System.Numerics.Matrix4x4.CreateRotationX(MathF.PI / 2f);
        // pelvisHeight: a creator whose rig stands the pelvis somewhere other than the stock
        // 1.067 m authors the inverse bind matrix against THAT height and states it as a joint
        // position override (the alternate bind matrix's translation). The mesh then has to land
        // exactly where it does with the stock pelvis -- but only if the override is honoured.
        float authoredPelvisZ = pelvisHeight ?? pelvis.Position.Z;
        var skin = new MeshSkin(new[] { "mPelvis" },
            new[] { System.Numerics.Matrix4x4.CreateTranslation(0f, 0f, -authoredPelvisZ) }, bindShape, 0f,
            pelvisHeight.HasValue
                ? new[] { System.Numerics.Matrix4x4.CreateTranslation(0f, 0f, authoredPelvisZ) }
                : null);
        return new MeshData(new[] { submesh }, skin);
    }

    /// <summary>How many control avatars exist right now. For the self-test.</summary>
    internal int SelfTestControlAvatarCount => _controlAvatars.Count;

    /// <summary>How many prims have a BUILT part on <paramref name="rootId"/>'s control avatar, or -1
    /// when that object has no control avatar. For the self-test.</summary>
    internal int SelfTestPartCount(Guid rootId) =>
        _controlAvatars.TryGetValue(rootId, out var ca) ? ca.Parts.Count : -1;

    /// <summary>
    /// FEAT-ANIMESH-01: the whole control-avatar chain, headless, against a known answer.
    ///
    /// <para>A rigged bar is built from synthetic data (one joint, mPelvis) whose bind shape turns
    /// it 90 degrees about X -- the kind of axis conversion that made the first animesh seen
    /// in-world lie on its side -- and shrinks it to half size. The viewer's chain,
    /// <c>p * BSM * invBind * jointWorld</c> with the skeleton turned by <c>bind_rot * obj_rot</c>,
    /// cancels the bind shape's rotation and leaves the bar as AUTHORED, scaled, and turned by the
    /// object's own rotation. That answer is worked out below from the authored corners alone,
    /// with no reference to the bind shape or to <see cref="ControlAvatarPlacement"/>, and the
    /// skinned rest extent this renderer measures has to match it.</para>
    ///
    /// <para>Then the rest of what a control avatar promises: a second mesh of the same object
    /// shares the skeleton, a re-rig replaces rather than adds, moving the root moves the avatar
    /// (and prim scale has no part in it), the pelvis fixup lifts along world up, and releasing
    /// the last mesh frees the skeleton.</para>
    ///
    /// <para>Needs the renderer in a scene tree (node paths are resolved) but no world.</para>
    /// </summary>
    internal (bool Passed, string Detail) SelfTestControlAvatar()
    {
        LoadAvatarSkeleton();
        if (_avatarSkeleton == null) return (false, "the avatar skeleton did not load");
        var pelvis = _avatarSkeleton.GetBone("mPelvis");
        if (pelvis == null || pelvis.ParentName != null || pelvis.Rotation != System.Numerics.Vector3.Zero)
            return (false, "mPelvis is not the unrotated root joint this check is written against");

        const float Scale = 0.5f;
        var data = SelfTestRiggedBar(Scale, out var corners);

        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var meshId = Guid.NewGuid();
        var objectRotation = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, MathF.PI / 2f);
        var rootPos = new Godot.Vector3(10f, 5f, -20f);

        try
        {
            var ca = GetOrCreateControlAvatar(rootId);
            if (ca == null) return (false, "no control avatar could be created");

            if (!InstallControlAvatarPart(ca, rootId, true, data, meshId, null, default, true, false,
                    rootPos, objectRotation))
                return (false, "the rigged bar produced no geometry");

            // 1. The skinned rest extent against the known answer: authored corners, scaled, turned
            //    by the object rotation. The bind shape's X rotation must have no trace in it.
            var expMin = new System.Numerics.Vector3(float.MaxValue);
            var expMax = new System.Numerics.Vector3(float.MinValue);
            foreach (var c in corners)
            {
                var e = System.Numerics.Vector3.Transform(c * Scale, objectRotation);
                expMin = System.Numerics.Vector3.Min(expMin, e);
                expMax = System.Numerics.Vector3.Max(expMax, e);
            }
            var ext = ca.Parts[rootId].Extent;
            if (ext == null || !ext.Valid) return (false, "no rest extent was measured");
            // Godot (x, y, z) -> SL (x, -z, y)
            var gotMin = new System.Numerics.Vector3(ext.WorldMin.X, -ext.WorldMax.Z, ext.WorldMin.Y);
            var gotMax = new System.Numerics.Vector3(ext.WorldMax.X, -ext.WorldMin.Z, ext.WorldMax.Y);
            bool extentOk = (gotMin - expMin).Length() < 2e-3f && (gotMax - expMax).Length() < 2e-3f;
            string extentText = $"extent min=({gotMin.X:0.###}, {gotMin.Y:0.###}, {gotMin.Z:0.###}) " +
                                $"max=({gotMax.X:0.###}, {gotMax.Y:0.###}, {gotMax.Z:0.###}), wanted " +
                                $"min=({expMin.X:0.###}, {expMin.Y:0.###}, {expMin.Z:0.###}) " +
                                $"max=({expMax.X:0.###}, {expMax.Y:0.###}, {expMax.Z:0.###})";

            // 2. A second mesh of the same object shares the skeleton; a re-rig of one replaces it.
            if (!InstallControlAvatarPart(ca, childId, false, data, meshId, null, default, true, false, rootPos, objectRotation))
                return (false, "the child mesh produced no geometry");
            if (!InstallControlAvatarPart(ca, rootId, true, data, meshId, null, default, true, false, rootPos, objectRotation))
                return (false, "the re-rig produced no geometry");
            int parts = ca.Visual.Skeleton!.GetChildren().OfType<MeshInstance3D>()
                .Count(m => m.Name.ToString().StartsWith("AnimeshPart_", StringComparison.Ordinal));
            bool shareOk = _controlAvatars.Count == 1 && ca.Parts.Count == 2 && parts == 2
                           && ca.Visual.RiggedAttachments.Count == 2;

            // 3. Placement: the skeleton's own up axis ends up where bind_rot * obj_rot puts it, the
            //    root follows the prim, and nothing scales.
            var up = ca.Visual.Root.Basis * new Godot.Vector3(0f, 1f, 0f);
            var gotUp = new System.Numerics.Vector3(up.X, -up.Z, up.Y);
            var wantUp = System.Numerics.Vector3.Transform(
                System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitZ,
                    System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitX, -MathF.PI / 2f)),
                objectRotation);
            bool upOk = (gotUp - wantUp).Length() < 1e-3f && ca.Visual.Root.Scale.IsEqualApprox(Godot.Vector3.One)
                        && ca.Visual.Root.Position.IsEqualApprox(rootPos);

            var moved = new Godot.Vector3(100f, 20f, -50f);
            ApplyControlAvatarPlacement(ca, moved, objectRotation);
            bool followsOk = ca.Visual.Root.Position.IsEqualApprox(moved);

            ca.Visual.PelvisFixups[meshId] = 0.25f;
            ApplyControlAvatarPlacement(ca, moved, objectRotation);
            bool fixupOk = ca.Visual.Root.Position.IsEqualApprox(new Godot.Vector3(moved.X, moved.Y + 0.25f, moved.Z));
            ca.Visual.PelvisFixups.Clear();

            // 4. Releasing the meshes frees the skeleton with the last one.
            _controlAvatarOfPrim[rootId] = rootId;
            _controlAvatarOfPrim[childId] = rootId;
            ReleaseControlAvatarMesh(childId);
            bool oneLeft = _controlAvatars.Count == 1;
            ReleaseControlAvatarMesh(rootId);
            bool freed = _controlAvatars.Count == 0 && _controlAvatarOfPrim.Count == 0
                         && ca.Visual.Root.IsQueuedForDeletion();

            // 5. A rig whose pelvis is NOT at the stock height (a robot): the pelvis position override
            //    must be applied to a control avatar, or the whole mesh rides higher by the difference.
            //    Same bar, same known answer as step 1 -- the override cancels against the inverse bind.
            bool pelvisOk;
            string pelvisText;
            {
                var lowData = SelfTestRiggedBar(Scale, out _, pelvisHeight: 0.3f);
                var lowMeshId = Guid.NewGuid();
                var lowRoot = Guid.NewGuid();
                var lowCa = GetOrCreateControlAvatar(lowRoot);
                if (lowCa == null || !InstallControlAvatarPart(lowCa, lowRoot, true, lowData, lowMeshId, null, default,
                        true, false, rootPos, objectRotation))
                {
                    FreeControlAvatar(lowRoot);
                    return (false, "the low-pelvis bar produced no geometry");
                }
                var lowExt = lowCa.Parts[lowRoot].Extent;
                bool lowOk = lowExt != null && lowExt.Valid;
                var lowMin = lowOk ? new System.Numerics.Vector3(lowExt!.WorldMin.X, -lowExt.WorldMax.Z, lowExt.WorldMin.Y) : default;
                var lowMax = lowOk ? new System.Numerics.Vector3(lowExt!.WorldMax.X, -lowExt.WorldMin.Z, lowExt.WorldMax.Y) : default;
                pelvisOk = lowOk && (lowMin - expMin).Length() < 2e-3f && (lowMax - expMax).Length() < 2e-3f;
                pelvisText = lowOk
                    ? $"low-pelvis extent min=({lowMin.X:0.###}, {lowMin.Y:0.###}, {lowMin.Z:0.###}) max=({lowMax.X:0.###}, {lowMax.Y:0.###}, {lowMax.Z:0.###})"
                    : "no extent measured for the low-pelvis bar";
                FreeControlAvatar(lowRoot);
                _controlAvatarOfPrim.Remove(lowRoot);
            }

            bool ok = extentOk && shareOk && upOk && followsOk && fixupOk && oneLeft && freed && pelvisOk;
            return (ok, ok
                ? $"bind-shape X turn cancelled, bar stays upright and scaled ({extentText}); one skeleton for 2 meshes, " +
                  "re-rig replaces, root follows, pelvis fixup lifts along world up, last release frees, " +
                  "a pelvis override at a non-stock height cancels against its inverse bind"
                : $"extent {(extentOk ? "ok" : "WRONG")} [{extentText}]; shared skeleton {(shareOk ? "ok" : $"WRONG ({_controlAvatars.Count} avatar(s), {ca.Parts.Count} part(s), {parts} node(s))")}; " +
                  $"up axis {(upOk ? "ok" : $"WRONG (got {gotUp}, want {wantUp})")}; follows root {(followsOk ? "ok" : "WRONG")}; " +
                  $"pelvis fixup {(fixupOk ? "ok" : "WRONG")}; pelvis override {(pelvisOk ? "ok" : $"WRONG [{pelvisText}]")}; one skeleton left after the first release {(oneLeft ? "ok" : "WRONG")}; freed {(freed ? "ok" : "WRONG")}");
        }
        finally
        {
            FreeControlAvatar(rootId);
            _controlAvatarOfPrim.Remove(rootId);
            _controlAvatarOfPrim.Remove(childId);
        }
    }
}
