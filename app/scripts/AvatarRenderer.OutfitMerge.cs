using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Godot;
using SLNG.Assets;
using SLNG.Core;
using SLNG.Core.Components;

namespace SLNG.App;

/// <summary>
/// FEAT-PERF-23 Phase 1: the outfit merge for remote avatars. Spec: docs/specs/FEAT-PERF-23-outfit-merge.md.
///
/// <para>Every worn rigged item still arrives through today's path (<see cref="CommitPreparedRig"/>):
/// its own instance and skin, called <b>staging</b> here. Once its materials are all in and nothing
/// keeps it apart (selected, volatile, texture-animated, BoM bake pending), its worn linkset's
/// <b>bucket</b> is marked dirty. After a debounce a <b>merge job</b> runs on a worker for the avatar
/// (<see cref="OutfitMerger"/>): Opaque and Scissor surfaces concatenated per material key into one
/// instance per linkset, every Blend surface on an instance of its own at today's exact sorting
/// offset, all of them on one <b>outfit skin</b> with exact (bone, inverse bind) dedupe. The main
/// thread appends the new binds, uploads the arrays into meshes that are not in the tree yet, and
/// swaps in one queue item: the bucket in, the absorbed staging instances out -- never both drawn,
/// never both missing.</para>
///
/// <para>A merged item keeps no instance of its own: <see cref="_riggedAttachments"/> has no entry for
/// it. While a newer version of it is being rigged (a face change, a LOD raise), that version's
/// staging instance is <b>held</b> -- in the tree, hidden, skin detached -- and the bucket keeps drawing
/// the old one until the re-merge swaps them. An item that has to leave (selected, volatile) is
/// re-rigged if it has no staging instance, and released at the next swap. The setting going off
/// rebuilds every merged item's own instance from what its bucket draws, in the swap that frees the
/// bucket (no re-rig); a parked avatar lets go of its outfit instead.</para>
///
/// <para><b>No read-backs.</b> The merge reads only managed arrays retained from the rig prepare
/// (<see cref="RetainedRig"/>). Items rigged while the setting was off are adopted from the arrays
/// <see cref="MeshSurfaceCache"/> already holds, converted on a worker.</para>
/// </summary>
public partial class AvatarRenderer
{
    // ---- tuning ---------------------------------------------------------------------------------

    /// <summary>A bucket's job starts after this long without a change...</summary>
    private const double MergeQuietSeconds = 0.3;

    /// <summary>...and never later than this after its first change.</summary>
    private const double MergeMaxDelaySeconds = 1.5;

    /// <summary>Face changes within <see cref="VolatileWindowSeconds"/> that make an item volatile.</summary>
    private const int VolatileChanges = 3;
    private const double VolatileWindowSeconds = 10.0;

    /// <summary>A volatile item stays staged until this long after its last change.</summary>
    private const double VolatileHoldSeconds = 30.0;

    /// <summary>A merged surface stops growing at this many vertices. Probe 2: ~35 ns per vertex for
    /// <c>AddSurfaceFromArrays</c>, so a full surface is ~1.2 ms -- one queue item, well under the
    /// 4 ms ceiling. A single item surface above it stays whole, as it was uploaded before.</summary>
    private const int MaxMergedSurfaceVertices = 32_768;

    /// <summary>Vertices one <c>avatar.merge.upload</c> queue item hands to the engine at most (one
    /// larger surface goes alone).</summary>
    private const int MergeUploadBatchVertices = 24_000;

    /// <summary>Godot's limit of surfaces per mesh (RS::MAX_MESH_SURFACES).</summary>
    private const int MaxSurfacesPerMesh = 256;

    /// <summary>Merge jobs running at once, over all avatars.</summary>
    private static readonly int MaxMergeJobs = Math.Max(1, System.Environment.ProcessorCount / 4);

    /// <summary>A slot table this much bigger than what its buckets still use is rebuilt.</summary>
    private const int CompactMinSlots = 256;

    /// <summary>Items one job takes in at most (whole buckets; at least one): the swap frees every
    /// absorbed staging instance in one queue item, so an avatar's first merge is cut into several
    /// jobs instead of one swap that removes ~150 nodes.</summary>
    private const int MaxItemsPerJob = 48;

    private const string OutfitBucketName = "OutfitBucket";
    private const string OutfitSortedName = "OutfitSorted";

    // ---- what each face material is -------------------------------------------------------------

    /// <summary>What <see cref="BuildFaceMaterialAsync"/> decided for a material: its Kind, scissor
    /// threshold, the texture it samples (the bake for BoM; a PBR face's base colour, BUG-RENDER-49),
    /// whether that texture failed (an alpha-0 card), and the Bakes-on-Mesh channel the texture came
    /// from (-1 for none). Recorded where the material is finished, which may be a pool thread.</summary>
    private sealed record FaceMaterialFacts(PrimShaderFamily.Kind Kind, float Threshold, Guid Texture, bool Failed, bool Hidden,
        int BakeChannel = -1);

    private static readonly ConditionalWeakTable<Material, FaceMaterialFacts> _faceMaterialFacts = new();

    private static void RecordFaceMaterialFacts(Material material, FaceMaterialFacts facts) =>
        _faceMaterialFacts.AddOrUpdate(material, facts);

    /// <summary>The facts of <paramref name="material"/>, or what its shader says when none were
    /// recorded (a material built before the table existed cannot be told apart from a failed one).</summary>
    private static FaceMaterialFacts FactsOf(Material material)
    {
        if (_faceMaterialFacts.TryGetValue(material, out var facts)) return facts;
        var kind = ProbeKindOf(material);
        return new FaceMaterialFacts(ShaderKindOf(kind), 0f, Guid.Empty, false, kind == ProbeKind.Hidden);
    }

    /// <summary>The spec's material key: two faces with equal keys get identical materials.
    /// <see cref="FaceTexture"/> equality already means "identical material inputs" (BUG-RENDER-12) --
    /// its RenderMaterialId and PbrOverride included, so two faces with different glTF materials never
    /// share a key (BUG-RENDER-49); the resolved texture (the bake for BoM, a PBR face's base colour),
    /// the Kind and the scissor threshold cover what is derived from the texture.</summary>
    private readonly record struct OutfitMaterialKey(FaceTexture Face, Guid Texture, PrimShaderFamily.Kind Kind, float Threshold);

    // ---- the state ------------------------------------------------------------------------------

    /// <summary>One item's geometry as the merge reads it: per surface the engine streams with ITEM
    /// slots, the SL face it was built for, and the item's slot keys. Immutable once made.</summary>
    private sealed class RetainedRig
    {
        public required OutfitMeshArrays[] Surfaces { get; init; }
        public required int[] FaceIndices { get; init; }
        public required OutfitSlotKey[] Slots { get; init; }
        public required long Bytes { get; init; }
    }

    /// <summary>Everything one merge of an item needs, frozen at the moment its staging settled.</summary>
    private sealed class MergeInput
    {
        public required RetainedRig Rig { get; init; }
        public required Material[] Materials { get; init; }
        public required FaceMaterialFacts[] Facts { get; init; }
        public required float SortingOffset { get; init; }
        public required FaceTexture[]? Faces { get; init; }
        public required FaceTexture DefaultFace { get; init; }

        /// <summary>The asset the geometry came from: what an un-merge registers its rebuilt instance with
        /// (<see cref="AvatarVisual.RiggedAttachments"/>, read again by a shape change's re-bind).</summary>
        public MeshData? MeshData { get; init; }
        public Guid MeshId { get; init; }
    }

    private sealed class MergeItem
    {
        public required Guid EntityId { get; init; }
        public required AvatarVisual Visual { get; init; }
        public long Linkset;
        public long OrderMajor;
        public long OrderMinor;

        /// <summary>The request the newest staging was rigged from: what an eject rigs again.</summary>
        public PendingRig? Request;
        public FaceTexture[]? Faces;
        public FaceTexture DefaultFace;
        public Guid MeshId;
        public float SortingOffset;

        /// <summary>The newest commit's geometry, before its materials are in.</summary>
        public RetainedRig? Rig;

        /// <summary>The newest staging instance: drawn, or held while the bucket draws this item.</summary>
        public MeshInstance3D? Staging;
        public bool Held;

        /// <summary>The staging instance has every material: it can be shown in place of the bucket.</summary>
        public bool StagingSettled;

        /// <summary>The newest staging with all its materials: what a merge takes.</summary>
        public MergeInput? Newest;

        /// <summary>What the bucket draws for this item, when it is in one.</summary>
        public MergeInput? Merged;
        public bool InBucket;

        /// <summary>Item slot to outfit slot, for what the bucket draws (the garbage check).</summary>
        public int[]? MergedRemap;

        /// <summary>Bumped by everything that changes what the merge would do with this item; a job
        /// commits only over items whose generation it saw.</summary>
        public int Generation;

        public bool Selected;
        public bool TextureAnimated;
        public double VolatileUntil = double.NegativeInfinity;
        public readonly Queue<double> Changes = new();

        /// <summary>The staging's arrays are being converted on a worker (it was rigged while the setting
        /// was off): the bucket keeps drawing the item until they are in, instead of releasing it.</summary>
        public bool Retaining;
    }

    private sealed class OutfitBucket
    {
        public required long Key { get; init; }
        public readonly List<MeshInstance3D> Instances = new();
        public readonly List<MeshInstance3D> Sorted = new();

        /// <summary>Items whose geometry the drawn instances hold.</summary>
        public readonly HashSet<Guid> Members = new();

        /// <summary>Where each member surface is drawn: the instance and its surface index.</summary>
        public readonly Dictionary<(Guid Item, int Surface), (MeshInstance3D Node, int Surface)> DrawnAt = new();

        public double FirstDirty = double.NaN;
        public double LastDirty;
        public bool Immediate;

        public bool IsDirty => !double.IsNaN(FirstDirty);
    }

    /// <summary>One avatar's merge: the outfit skin with its append-only slot table, the buckets and the
    /// items.</summary>
    private sealed class OutfitMergeAvatar
    {
        public Skin? Skin;
        public readonly List<OutfitSlotKey> Slots = new();
        public readonly Dictionary<long, OutfitBucket> Buckets = new();
        public readonly Dictionary<Guid, MergeItem> Items = new();
        public bool JobInFlight;

        /// <summary>Bumped when the avatar's merge is torn down: a job from before commits nothing.</summary>
        public int Serial;
        public bool CompactPending;
    }

    private readonly Dictionary<Guid, MergeItem> _mergeItems = new();

    /// <summary>Staging instances held hidden while their item's old geometry is still in a bucket.</summary>
    private readonly HashSet<MeshInstance3D> _mergeHeld = new();

    /// <summary>Buckets that lost a member to a removal this frame (freed or re-merged at once).</summary>
    private readonly HashSet<(AvatarVisual Visual, long Key)> _mergeTouched = new();

    /// <summary>The value of <see cref="RenderConfig.OutfitMerge"/> the renderer last acted on.</summary>
    private bool _outfitMergeApplied;

    private int _mergeJobsRunning;
    private double _mergeTimedCheckAt;

    // FEAT-PERF-23 [AvatarCost]: jobs and swaps since the last line.
    private int _mergeJobsDone, _mergeSwapsDiscarded;

    /// <summary>Self test only: keep each bucket's merged managed arrays (and ranges), so the test can
    /// skin them on the CPU. Off in the client.</summary>
    private static bool _outfitMergeKeepArrays;
    private readonly Dictionary<(Guid Avatar, long Bucket), OutfitBucketResult<OutfitMaterialKey>> _outfitMergeKept = new();

    private static double MergeNow => Time.GetTicksMsec() / 1000.0;

    private bool MergeEnabledFor(AvatarVisual visual) =>
        RenderConfig.OutfitMerge && !visual.IsSelf && !visual.IsControlAvatar
        && visual.Skeleton != null && IsInstanceValid(visual.Skeleton)
        && _visuals.TryGetValue(visual.EntityId, out var live) && ReferenceEquals(live, visual);

    /// <summary>True when the entity's geometry is drawn by a bucket (with or without a held staging).</summary>
    private bool IsMergedItem(Guid entityId) => _mergeItems.TryGetValue(entityId, out var item) && item.InBucket;

    /// <summary>A rigged item on its wearer, staged or merged: what the readers of
    /// <see cref="_riggedAttachments"/> that ask "is it worn rigged" must also accept.</summary>
    private bool IsRiggedOnWearer(Guid entityId) => _riggedAttachments.ContainsKey(entityId) || IsMergedItem(entityId);

    // ---- the item commit: what the merge keeps of a rig --------------------------------------------

    /// <summary>Called by <see cref="CommitPreparedRig"/> once the staging instance is in the tree. Keeps
    /// the managed arrays of the rig for a merge, and holds the instance hidden when the bucket still
    /// draws this item.</summary>
    private void NoteRiggedCommit(Guid entityId, PendingRig req, PreparedRiggedMesh prepared, int[] slotForJoint,
        int[] faceIndices, MeshInstance3D mi)
    {
        _mergeItems.TryGetValue(entityId, out var item);
        bool wanted = MergeEnabledFor(req.Visual);
        if (!wanted && item is not { InBucket: true })
        {
            if (item != null) ForgetMergeItem(entityId);
            return;
        }

        if (item == null || !ReferenceEquals(item.Visual, req.Visual))
        {
            if (item != null) ForgetMergeItem(entityId);
            item = NewMergeItem(entityId, req.Visual);
        }

        item.Request = req;
        item.Faces = req.Faces;
        item.DefaultFace = req.DefaultFace;
        item.MeshId = req.MeshId;
        // BUG-RENDER-50: the start of its slot in the wearer's alpha order (the instance itself sits at the
        // slot's depth pass).
        item.SortingOffset = WornAlphaBaseOf(req.Visual, entityId, mi.SortingOffset);
        item.Rig = wanted ? RetainRig(prepared, slotForJoint, faceIndices, req) : null;
        long oldLinkset = item.Linkset;
        SetLinksetOrder(item);
        // Relinked while merged (rare): the old bucket lets go of it at once; the new one takes it in.
        if (item.InBucket && oldLinkset != item.Linkset) _mergeTouched.Add((item.Visual, oldLinkset));
        item.Staging = mi;
        item.StagingSettled = false;
        item.Newest = null;
        item.Generation++;

        if (item.InBucket)
        {
            // The bucket still draws this item: the new version waits, hidden, for the re-merge.
            item.Held = true;
            _mergeHeld.Add(mi);
            mi.Visible = false;
            DetachSkeleton(mi);
        }
    }

    private MergeItem NewMergeItem(Guid entityId, AvatarVisual visual)
    {
        var item = new MergeItem { EntityId = entityId, Visual = visual };
        _mergeItems[entityId] = item;
        visual.OutfitMerge ??= new OutfitMergeAvatar();
        visual.OutfitMerge.Items[entityId] = item;
        return item;
    }

    private void SetLinksetOrder(MergeItem item)
    {
        var entity = _world?.GetEntity(item.EntityId);
        if (entity == null)
        {
            // No entity (a self test, or it left a moment ago): a linkset of its own.
            item.Linkset = -1 - (long)(uint)item.EntityId.GetHashCode();
            item.OrderMajor = item.Linkset;
            item.OrderMinor = 0;
            return;
        }
        uint root = WornLinksetRoot(entity);
        item.Linkset = root;
        item.OrderMajor = root;
        item.OrderMinor = entity.LocalId;
    }

    /// <summary>The rig's managed arrays (the builder's surfaces plus the worker's tangents) and slot
    /// keys. Null when the tangents were not kept, which leaves the item staged.</summary>
    private RetainedRig? RetainRig(PreparedRiggedMesh prepared, int[] slotForJoint, int[] faceIndices, PendingRig req)
    {
        var tangents = prepared.Tangents;
        var surfaces = prepared.Geometry.Surfaces;
        if (tangents == null || tangents.Length != surfaces.Count) return null;
        var slots = SlotKeysFor(req.MeshData.Skin!, req.Skeleton, slotForJoint);
        if (slots == null) return null;

        var arrays = new OutfitMeshArrays[surfaces.Count];
        long bytes = 0;
        for (int s = 0; s < arrays.Length; s++)
        {
            var surface = surfaces[s];
            if (tangents[s].Length != surface.Positions.Length * 4) return null;
            arrays[s] = new OutfitMeshArrays(surface.Positions, surface.Normals, tangents[s], surface.UVs,
                                             surface.Bones, surface.Weights, surface.Indices);
            bytes += ArrayBytes(arrays[s]);
        }
        return new RetainedRig { Surfaces = arrays, FaceIndices = faceIndices, Slots = slots, Bytes = bytes };
    }

    private static long ArrayBytes(OutfitMeshArrays a) =>
        (long)a.Positions.Length * 12 + a.Normals.Length * 12L + a.Tangents.Length * 4L + a.UVs.Length * 8L
        + a.Bones.Length * 4L + a.Weights.Length * 4L + a.Indices.Length * 4L;

    /// <summary>The key of each skin slot of a mesh: slot s is the s-th joint that names a bone of
    /// ours, exactly as <see cref="BindRiggedSkin"/> hands them out.</summary>
    private OutfitSlotKey[]? SlotKeysFor(MeshSkin skin, Skeleton3D skeleton, int[] slotForJoint)
    {
        int count = 0;
        foreach (int slot in slotForJoint) if (slot >= 0) count++;
        if (count == 0) return null;
        var keys = new OutfitSlotKey[count];
        for (int j = 0; j < slotForJoint.Length && j < skin.JointNames.Length; j++)
        {
            int slot = slotForJoint[j];
            if (slot < 0) continue;
            string boneName = skin.JointNames[j];
            if (_avatarSkeleton != null) boneName = _avatarSkeleton.ResolveBoneName(boneName);
            int bone = skeleton.FindBone(boneName);
            if (bone < 0 || slot >= count) return null;
            keys[slot] = OutfitSlotKey.For(bone, skin.InverseBindMatrices[j]);
        }
        return keys;
    }

    // ---- readiness --------------------------------------------------------------------------------

    /// <summary>The end of <see cref="ApplyFaceMaterialsAsync"/> for a worn rigged item (the
    /// <c>avatar.split_sorted</c> item): its materials are in, so its merge input is complete.</summary>
    private void NoteStagingSettled(Guid entityId, MeshInstance3D mi)
    {
        if (!_mergeItems.TryGetValue(entityId, out var item) || !ReferenceEquals(item.Staging, mi)) return;
        if (!IsInstanceValid(mi)) return;
        if (item.Held)
        {
            mi.Visible = false;
            DetachSkeleton(mi);
        }
        item.StagingSettled = AllMaterialsIn(mi);
        if (!item.StagingSettled) return;
        // Rigged while the setting was off (no tangents kept): its arrays are converted from the ones the
        // staging was built from, and this runs again when they are in. Until then it is not mergeable, and
        // a bucket that draws it keeps doing so (SnapshotBucket) -- releasing it now would strand it staged.
        if (item.Rig == null && MergeEnabledFor(item.Visual) && BeginRetainFromStaging(item, mi)) return;
        item.Newest = item.Rig != null ? ReadMergeInput(item, mi) : null;
        item.Generation++;
        // A selected item waiting to be shown on its own goes at once; everything else is debounced.
        MarkBucketDirty(item, immediate: item.InBucket && item.Selected);
    }

    private static bool AllMaterialsIn(MeshInstance3D mi)
    {
        if (mi.Mesh is not ArrayMesh mesh) return false;
        int count = mesh.GetSurfaceCount();
        for (int s = 0; s < count; s++)
        {
            var split = mi.GetNodeOrNull<MeshInstance3D>(SortedSurfaceName(s));
            if ((split != null ? split.GetSurfaceOverrideMaterial(0) : mi.GetSurfaceOverrideMaterial(s)) == null) return false;
        }
        return true;
    }

    /// <summary>The materials a staging instance draws with, one per surface: on the surface, or on
    /// its split child (BUG-RENDER-38). Null while any is missing.</summary>
    private MergeInput? ReadMergeInput(MergeItem item, MeshInstance3D mi)
    {
        var rig = item.Rig!;
        if (mi.Mesh is not ArrayMesh mesh || mesh.GetSurfaceCount() != rig.Surfaces.Length) return null;
        var materials = new Material[rig.Surfaces.Length];
        var facts = new FaceMaterialFacts[rig.Surfaces.Length];
        for (int s = 0; s < materials.Length; s++)
        {
            var split = mi.GetNodeOrNull<MeshInstance3D>(SortedSurfaceName(s));
            var material = split != null ? split.GetSurfaceOverrideMaterial(0) : mi.GetSurfaceOverrideMaterial(s);
            if (material == null) return null;
            materials[s] = material;
            facts[s] = FactsOf(material);
        }
        return new MergeInput
        {
            Rig = rig,
            Materials = materials,
            Facts = facts,
            SortingOffset = item.SortingOffset,
            Faces = item.Faces,
            DefaultFace = item.DefaultFace,
            MeshData = item.Request?.MeshData,
            MeshId = item.MeshId,
        };
    }

    /// <summary>Whether the item's newest version may be merged now.</summary>
    private bool IsMergeEligible(MergeItem item, double now)
    {
        var input = item.Newest;
        if (input == null || item.Selected || now < item.VolatileUntil) return false;
        if (!MergeEnabledFor(item.Visual) || item.Visual.IsReduced) return false;
        if (_world?.GetEntity(item.EntityId)?.GetComponent<PrimitiveComponent>()?.TextureAnim != null) return false;
        // A Bakes-on-Mesh face waits for its bake -- the bake decides the Kind -- and for a material
        // built from THAT bake (one built before the bake id arrived is untextured).
        var faceIndices = input.Rig.FaceIndices;
        for (int s = 0; s < faceIndices.Length && s < input.Facts.Length; s++)
        {
            int bake = BakeChannelOf(input, faceIndices[s], s);
            if (bake < 0) continue;
            if (!item.Visual.LoadedTextures.TryGetValue(bake, out var bakeId) || input.Facts[s].Texture != bakeId)
                return false;
        }
        return true;
    }

    /// <summary>The Bakes-on-Mesh channel surface <paramref name="surface"/> samples, or -1. The facts
    /// know it when the material recorded them -- for a PBR face the channel comes from its base colour,
    /// not its legacy texture (BUG-RENDER-49). A material without facts falls back on the legacy texture,
    /// as before.</summary>
    private static int BakeChannelOf(MergeInput input, int faceIndex, int surface)
    {
        var facts = input.Facts[surface];
        if (facts.BakeChannel >= 0) return facts.BakeChannel;
        if (_faceMaterialFacts.TryGetValue(input.Materials[surface], out _)) return -1;
        var face = ResolveFaceTexture(input.Faces, input.DefaultFace, faceIndex);
        return SLNG.Assets.BakedTextureIds.TryGetBakeIndex(face.TextureId, out int bake) ? bake : -1;
    }

    private void MarkBucketDirty(MergeItem item, bool immediate)
    {
        var state = item.Visual.OutfitMerge;
        if (state == null) return;
        // Nothing to do for an item that is staged and stays staged.
        if (!item.InBucket && !IsMergeEligible(item, MergeNow)) return;
        if (!state.Buckets.TryGetValue(item.Linkset, out var bucket))
        {
            bucket = new OutfitBucket { Key = item.Linkset };
            state.Buckets[item.Linkset] = bucket;
        }
        MarkDirty(bucket, immediate);
    }

    private static void MarkDirty(OutfitBucket bucket, bool immediate)
    {
        double now = MergeNow;
        if (!bucket.IsDirty) bucket.FirstDirty = now;
        bucket.LastDirty = now;
        bucket.Immediate |= immediate;
    }

    /// <summary>The item's staging instance is being thrown away (<see cref="DiscardRiggedAttachment"/>: a
    /// reload, a reduce). A staged item has nothing to merge until its next commit; a merged one goes on
    /// drawing what its bucket holds.</summary>
    private void DropMergeStaging(MergeItem item)
    {
        if (item.Staging != null) _mergeHeld.Remove(item.Staging);
        item.Staging = null;
        item.Held = false;
        item.StagingSettled = false;
        item.Newest = item.InBucket ? item.Merged : null;
        item.Generation++;
    }

    // ---- items leaving ------------------------------------------------------------------------------

    /// <summary>The entity is no longer a rigged item on this wearer (it left, moved to a HUD point,
    /// became static or animated). Its geometry leaves its bucket at once: the bucket is freed when no
    /// member is left, re-merged otherwise.</summary>
    private void ForgetMergeItem(Guid entityId)
    {
        if (!_mergeItems.Remove(entityId, out var item)) return;
        item.Visual.OutfitMerge?.Items.Remove(entityId);
        item.Generation++;
        if (item.Staging != null) _mergeHeld.Remove(item.Staging);
        item.Staging = null;
        item.Held = false;
        if (item.InBucket) _mergeTouched.Add((item.Visual, item.Linkset));
    }

    /// <summary>Puts a selected, volatile or texture-animated merged item back on its own instance: it is
    /// released at the next swap of its bucket, once it has a staging instance to show. One that has
    /// none is rigged again from its last request. (The setting going off does not come here:
    /// <see cref="UnmergeAll"/>.)</summary>
    private void EjectMergeItem(MergeItem item, bool immediate)
    {
        item.Generation++;
        if (!item.InBucket) return;
        if (item.Staging == null || !IsInstanceValid(item.Staging))
        {
            if (!_pendingRigs.ContainsKey(item.EntityId) && item.Request is { } request
                && IsInstanceValid(request.Skeleton) && ReferenceEquals(request.Visual, item.Visual))
                RequestRig(item.EntityId, request);
            return;
        }
        // A held staging that has not settled yet is released when it does (NoteStagingSettled).
        if (item.StagingSettled) MarkBucketDirty(item, immediate);
    }

    /// <summary>The selection outline is drawn on an item's own instance (Phase 1), so a selected merged
    /// item goes back to staging, and comes back into its bucket on deselect.</summary>
    private void SetMergeItemSelected(Guid entityId, bool selected)
    {
        if (!_mergeItems.TryGetValue(entityId, out var item) || item.Selected == selected) return;
        item.Selected = selected;
        if (selected) EjectMergeItem(item, immediate: true);
        else
        {
            item.Generation++;
            MarkBucketDirty(item, immediate: false);
        }
    }

    /// <summary>A reload of a worn rigged item. A face change (not a pure LOD raise) counts towards the
    /// volatile rule.</summary>
    private void NoteMergeItemReload(Guid entityId, bool faceChange)
    {
        if (!faceChange || !_mergeItems.TryGetValue(entityId, out var item)) return;
        double now = MergeNow;
        item.Changes.Enqueue(now);
        while (item.Changes.Count > 0 && now - item.Changes.Peek() > VolatileWindowSeconds) item.Changes.Dequeue();
        if (item.Changes.Count >= VolatileChanges || now < item.VolatileUntil)
        {
            bool wasVolatile = now < item.VolatileUntil;
            item.VolatileUntil = now + VolatileHoldSeconds;
            if (!wasVolatile)
            {
                item.Generation++;
                if (item.InBucket) EjectMergeItem(item, immediate: false);
            }
        }
    }

    /// <summary>Called for every update of a worn item: a texture animation that starts on a merged item
    /// sends it back to staging; one that stops lets a staged one merge.</summary>
    private void NoteMergeItemUpdate(Guid entityId, PrimitiveComponent? prim)
    {
        if (!_mergeItems.TryGetValue(entityId, out var item)) return;
        bool animated = prim?.TextureAnim != null;
        if (animated == item.TextureAnimated) return; // the common case: nothing about it changed
        item.TextureAnimated = animated;
        if (animated) EjectMergeItem(item, immediate: true);
        else
        {
            item.Generation++;
            MarkBucketDirty(item, immediate: false);
        }
    }

    // ---- the per-frame tick: toggle, removals, timers, jobs -------------------------------------------

    /// <summary>Runs from <see cref="_Process"/> after the removals: the setting switch, buckets that
    /// lost members, items whose volatile hold ran out, and starting due jobs.</summary>
    private void TickOutfitMerge()
    {
        if (RenderConfig.OutfitMerge != _outfitMergeApplied)
        {
            _outfitMergeApplied = RenderConfig.OutfitMerge;
            // A job in flight decided under the other setting: its swap is discarded and its buckets are
            // snapshot again under this one. Switched on mid-un-merge, that keeps every item still in a
            // bucket where it is instead of finishing the un-merge first.
            foreach (var item in _mergeItems.Values) item.Generation++;
            if (_outfitMergeApplied) AdoptAllStagedItems();
            else UnmergeAll();
            EmitOutfitProbeLine($"[OutfitMerge] {(_outfitMergeApplied ? "on" : "off")}: " +
                                $"{_mergeItems.Count} item(s) tracked over {CountMergedAvatars()} avatar(s)");
        }

        if (_mergeTouched.Count > 0) ProcessTouchedBuckets();
        if (_mergeItems.Count == 0) return;

        double now = MergeNow;
        if (now >= _mergeTimedCheckAt)
        {
            _mergeTimedCheckAt = now + 0.5;
            foreach (var item in _mergeItems.Values)
            {
                // A volatile hold that ran out: the staged item may merge again.
                if (!item.InBucket && item.Newest != null && double.IsFinite(item.VolatileUntil)
                    && now >= item.VolatileUntil && now - item.VolatileUntil < 1.0 && IsMergeEligible(item, now))
                    MarkBucketDirty(item, immediate: false);
            }
        }

        ScheduleMergeJobs(now);
    }

    private int CountMergedAvatars()
    {
        int n = 0;
        foreach (var visual in _visuals.Values) if (visual.OutfitMerge is { Buckets.Count: > 0 }) n++;
        return n;
    }

    /// <summary>Buckets that lost a member: freed at once when none is left (the whole linkset was taken
    /// off, the common case, no job), re-merged at once otherwise.</summary>
    private void ProcessTouchedBuckets()
    {
        foreach (var (visual, key) in _mergeTouched)
        {
            var state = visual.OutfitMerge;
            if (state == null || !state.Buckets.TryGetValue(key, out var bucket)) continue;
            bool anyLeft = false;
            foreach (var id in bucket.Members)
                if (state.Items.ContainsKey(id)) { anyLeft = true; break; }
            if (!anyLeft)
            {
                FreeBucketNodes(bucket);
                bucket.Members.Clear();
                bucket.DrawnAt.Clear();
                // Staged items of the same linkset that want in start a new bucket the normal way.
                bool wanted = false;
                foreach (var item in state.Items.Values)
                    if (item.Linkset == key && IsMergeEligible(item, MergeNow)) { wanted = true; break; }
                if (wanted) MarkDirty(bucket, immediate: false);
                else state.Buckets.Remove(key);
            }
            else
            {
                MarkDirty(bucket, immediate: true);
            }
            DropMergeStateIfEmpty(visual);
        }
        _mergeTouched.Clear();
    }

    private void ScheduleMergeJobs(double now)
    {
        if (_mergeJobsRunning >= MaxMergeJobs) return;
        List<(AvatarVisual Visual, float Priority)>? due = null;
        foreach (var visual in _visuals.Values)
        {
            var state = visual.OutfitMerge;
            if (state == null || state.JobInFlight || visual.IsReduced) continue;
            if (!HasDueBucket(state, now)) continue;
            // The rig workers' order: near first, hidden last. Self is never merged.
            float priority = 1f + visual.GodotPos.DistanceSquaredTo(_lastCamPos);
            if (!visual.Shown) priority += 1_000_000f;
            (due ??= new()).Add((visual, priority));
        }
        if (due == null) return;
        due.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        foreach (var (visual, _) in due)
        {
            if (_mergeJobsRunning >= MaxMergeJobs) break;
            StartMergeJob(visual, now);
        }
    }

    private static bool HasDueBucket(OutfitMergeAvatar state, double now)
    {
        if (state.CompactPending) return true;
        foreach (var bucket in state.Buckets.Values)
            if (IsDue(bucket, now)) return true;
        return false;
    }

    private static bool IsDue(OutfitBucket bucket, double now) =>
        bucket.IsDirty && (bucket.Immediate || now - bucket.LastDirty >= MergeQuietSeconds
                           || now - bucket.FirstDirty >= MergeMaxDelaySeconds);

    // ---- the job ----------------------------------------------------------------------------------

    private sealed class JobBucket
    {
        public required long Key { get; init; }
        public readonly List<(MergeItem Item, MergeInput Input, bool AbsorbStaging)> Included = new();
        public readonly List<MergeItem> Released = new();

        /// <summary>Items the swap gives an instance of their own again, built from what the bucket draws
        /// for them (the setting went off).</summary>
        public readonly List<RebuiltItem> Rebuilt = new();
        public readonly Dictionary<MergeItem, int> Stamps = new();
        public OutfitBucketResult<OutfitMaterialKey>? Result;
        public Godot.Collections.Array[]? MergedArrays;
        public Godot.Collections.Array[]? SortedArrays;
        public readonly List<ArrayMesh> Meshes = new();
        public readonly List<ArrayMesh> SortedMeshes = new();
    }

    /// <summary>One item an un-merge gives its own instance back: its engine arrays (packed on the worker,
    /// item slots as the rig made them) and the mesh they are uploaded into.</summary>
    private sealed class RebuiltItem
    {
        public RebuiltItem(MergeItem item, MergeInput input)
        {
            Item = item;
            Input = input;
        }

        public MergeItem Item { get; }
        public MergeInput Input { get; }
        public Godot.Collections.Array[]? Arrays;
        public ArrayMesh? Mesh;
    }

    private enum UploadKind { Merged, Sorted, Rebuilt }

    private sealed class MergeJob
    {
        public required AvatarVisual Visual { get; init; }
        public required OutfitMergeAvatar State { get; init; }
        public required int Serial { get; init; }
        public required bool NewSkin { get; init; }
        public required List<JobBucket> Buckets { get; init; }
        public required List<OutfitBucketInput<OutfitMaterialKey>> Inputs { get; init; }
        public required OutfitSlotKey[] ExistingSlots { get; init; }
        public OutfitMergeResult<OutfitMaterialKey>? Result;
        public double WorkerMs;
        public Skin? BuiltSkin;
    }

    private void StartMergeJob(AvatarVisual visual, double now)
    {
        var state = visual.OutfitMerge!;
        bool compact = state.CompactPending;
        state.CompactPending = false;

        // The avatar's items by linkset, once: each is looked at once per job, not once per bucket.
        var byLinkset = new Dictionary<long, List<MergeItem>>();
        foreach (var item in state.Items.Values)
        {
            if (!byLinkset.TryGetValue(item.Linkset, out var list)) byLinkset[item.Linkset] = list = new();
            list.Add(item);
        }

        var buckets = new List<JobBucket>();
        var inputs = new List<OutfitBucketInput<OutfitMaterialKey>>();
        int items = 0;
        foreach (var bucket in state.Buckets.Values)
        {
            if (!compact && !IsDue(bucket, now)) continue;
            // A compaction must cover every bucket (they all move to the new skin together).
            if (!compact && buckets.Count > 0 && items >= MaxItemsPerJob) break;
            var job = SnapshotBucket(bucket, byLinkset.GetValueOrDefault(bucket.Key), now);
            bucket.FirstDirty = double.NaN;
            bucket.Immediate = false;
            buckets.Add(job.Bucket);
            inputs.Add(job.Input);
            // A rebuilt item costs its swap an instance to build, as an absorbed one costs one to free.
            items += job.Bucket.Included.Count + job.Bucket.Rebuilt.Count;
        }
        if (buckets.Count == 0) return;

        var mergeJob = new MergeJob
        {
            Visual = visual,
            State = state,
            Serial = state.Serial,
            NewSkin = compact,
            Buckets = buckets,
            Inputs = inputs,
            ExistingSlots = compact ? Array.Empty<OutfitSlotKey>() : state.Slots.ToArray(),
        };
        state.JobInFlight = true;
        _mergeJobsRunning++;
        _ = Task.Run(() => RunMergeJob(mergeJob));
    }

    /// <summary>Freezes what one bucket's merge reads: per item of the linkset, its newest input when it
    /// may merge, its current merged input when it is in the bucket and has nothing to be shown instead
    /// yet, nothing otherwise (released or never in).</summary>
    private (JobBucket Bucket, OutfitBucketInput<OutfitMaterialKey> Input) SnapshotBucket(OutfitBucket bucket,
        List<MergeItem>? linkset, double now)
    {
        var job = new JobBucket { Key = bucket.Key };
        var items = new List<OutfitItemInput<OutfitMaterialKey>>();
        foreach (var item in linkset ?? new List<MergeItem>())
        {
            MergeInput? input = null;
            bool absorb = false;
            bool rebuild = false;
            if (IsMergeEligible(item, now))
            {
                input = item.Newest;
                // Its staging (shown, or held) is replaced by the bucket.
                absorb = item.Staging != null;
            }
            else if (item.InBucket && item.Merged != null)
            {
                bool enabled = MergeEnabledFor(item.Visual);
                if (HasShowableStaging(item) && !(enabled && item.Retaining))
                {
                    // Released below: its own instance is shown at the swap.
                }
                else if (!enabled && item.Staging == null)
                {
                    // Switched off: its own instance is rebuilt from what the bucket draws -- the arrays and
                    // materials are all here, so no re-rig, no material pass, and it goes in this swap.
                    rebuild = true;
                }
                else
                {
                    input = item.Merged; // keeps drawing its old self until it has something to show
                }
            }

            if (input != null)
            {
                job.Included.Add((item, input, absorb));
                items.Add(ToMergerInput(item, input));
            }
            else if (rebuild)
            {
                job.Rebuilt.Add(new RebuiltItem(item, item.Merged!));
            }
            else if (item.InBucket)
            {
                job.Released.Add(item);
            }
            job.Stamps[item] = item.Generation;
        }
        // Members that are gone (removed) are neither: their geometry simply is not in the new bucket.
        return (job, new OutfitBucketInput<OutfitMaterialKey> { Key = bucket.Key, Items = items });
    }

    private static bool HasShowableStaging(MergeItem item) =>
        item.Staging != null && IsInstanceValid(item.Staging) && item.StagingSettled;

    private static OutfitItemInput<OutfitMaterialKey> ToMergerInput(MergeItem item, MergeInput input)
    {
        var rig = input.Rig;
        var surfaces = new OutfitSurfaceInput<OutfitMaterialKey>[rig.Surfaces.Length];
        for (int s = 0; s < surfaces.Length; s++)
        {
            var facts = input.Facts[s];
            int faceIndex = s < rig.FaceIndices.Length ? rig.FaceIndices[s] : 0;
            var face = ResolveFaceTexture(input.Faces, input.DefaultFace, faceIndex);
            var role = facts.Hidden || facts.Failed ? OutfitSurfaceRole.Skip
                : facts.Kind == PrimShaderFamily.Kind.Blend ? OutfitSurfaceRole.Sorted
                : OutfitSurfaceRole.Merge;
            float threshold = facts.Kind == PrimShaderFamily.Kind.Scissor ? facts.Threshold : 0f;
            surfaces[s] = new OutfitSurfaceInput<OutfitMaterialKey>
            {
                MaterialKey = new OutfitMaterialKey(face, facts.Texture, facts.Kind, threshold),
                Role = role,
                // Exactly the split child's (SplitSortedSurfaces): the surface's slot within the item's
                // (BUG-RENDER-50). The swap places it again from the wearer's current ranks.
                SortingOffset = WornAlphaOrder.SurfaceOffset(input.SortingOffset, s),
                Arrays = rig.Surfaces[s],
            };
        }
        return new OutfitItemInput<OutfitMaterialKey>
        {
            Id = item.EntityId,
            OrderMajor = item.OrderMajor,
            OrderMinor = item.OrderMinor,
            Slots = rig.Slots,
            Surfaces = surfaces,
        };
    }

    /// <summary>The worker half: the merge, then the engine arrays of every output surface.</summary>
    private void RunMergeJob(MergeJob job)
    {
        var clock = Stopwatch.StartNew();
        bool ok = false;
        try
        {
            job.Result = OutfitMerger.Merge(job.Inputs, job.ExistingSlots, MaxMergedSurfaceVertices);
            if (EngineWorkerGate.TryEnter())
            {
                try
                {
                    for (int b = 0; b < job.Buckets.Count; b++)
                    {
                        var jb = job.Buckets[b];
                        // An un-merged item's own arrays: the rig's streams, item slots and all.
                        foreach (var rebuilt in jb.Rebuilt)
                        {
                            var surfaces = rebuilt.Input.Rig.Surfaces;
                            rebuilt.Arrays = new Godot.Collections.Array[surfaces.Length];
                            for (int s = 0; s < surfaces.Length; s++) rebuilt.Arrays[s] = PackOutfitArrays(surfaces[s]);
                        }
                        jb.Result = FindBucketResult(job.Result, jb.Key);
                        if (jb.Result == null) continue;
                        jb.MergedArrays = new Godot.Collections.Array[jb.Result.Surfaces.Count];
                        for (int s = 0; s < jb.MergedArrays.Length; s++)
                            jb.MergedArrays[s] = PackOutfitArrays(jb.Result.Surfaces[s].Arrays);
                        jb.SortedArrays = new Godot.Collections.Array[jb.Result.Sorted.Count];
                        for (int s = 0; s < jb.SortedArrays.Length; s++)
                            jb.SortedArrays[s] = PackOutfitArrays(jb.Result.Sorted[s].Arrays);
                    }
                    ok = true;
                }
                finally
                {
                    EngineWorkerGate.Exit();
                }
            }
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[OutfitMerge] merge job failed: {ex.Message}");
        }
        job.WorkerMs = clock.Elapsed.TotalMilliseconds;

        if (!ok)
        {
            // Nothing to swap in (the gate was closed, or the merge threw): FinishMergeJob tries the buckets again.
            job.Result = null;
            MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () => FinishMergeJob(job), label: "avatar.merge.swap");
            return;
        }

        // In order, on the FIFO lane: binds, the uploads in batches, the swap.
        MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () => CommitMergeBinds(job), label: "avatar.merge.binds");
        var batch = new List<(JobBucket Bucket, UploadKind Kind, int Index, int Surface)>();
        int batchVertices = 0;
        foreach (var jb in job.Buckets)
        {
            for (int r = 0; r < jb.Rebuilt.Count; r++)
            {
                var surfaces = jb.Rebuilt[r].Input.Rig.Surfaces;
                for (int s = 0; s < surfaces.Length; s++) AddToBatch(jb, UploadKind.Rebuilt, r, s, surfaces[s].VertexCount);
            }
            if (jb.Result == null) continue;
            for (int s = 0; s < jb.Result.Surfaces.Count; s++)
                AddToBatch(jb, UploadKind.Merged, s, 0, jb.Result.Surfaces[s].Arrays.VertexCount);
            for (int s = 0; s < jb.Result.Sorted.Count; s++)
                AddToBatch(jb, UploadKind.Sorted, s, 0, jb.Result.Sorted[s].Arrays.VertexCount);
        }
        FlushBatch();
        MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () => SwapMergeJob(job), label: "avatar.merge.swap");

        void AddToBatch(JobBucket jb, UploadKind kind, int index, int surface, int vertices)
        {
            if (batch.Count > 0 && batchVertices + vertices > MergeUploadBatchVertices) FlushBatch();
            batch.Add((jb, kind, index, surface));
            batchVertices += vertices;
        }

        void FlushBatch()
        {
            if (batch.Count == 0) return;
            var work = batch.ToArray();
            batch.Clear();
            batchVertices = 0;
            MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () => UploadMergeBatch(job, work), label: "avatar.merge.upload");
        }
    }

    private static OutfitBucketResult<OutfitMaterialKey>? FindBucketResult(OutfitMergeResult<OutfitMaterialKey> result, long key)
    {
        foreach (var b in result.Buckets) if (b.Key == key) return b;
        return null;
    }

    /// <summary>The engine arrays of one merged surface. On a worker, as the rig prepare packs its own.</summary>
    private static Godot.Collections.Array PackOutfitArrays(OutfitMeshArrays a)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = Variant.CreateFrom(MemoryMarshal.Cast<System.Numerics.Vector3, Godot.Vector3>(a.Positions.AsSpan()));
        arrays[(int)Mesh.ArrayType.Normal] = Variant.CreateFrom(MemoryMarshal.Cast<System.Numerics.Vector3, Godot.Vector3>(a.Normals.AsSpan()));
        arrays[(int)Mesh.ArrayType.Tangent] = Variant.CreateFrom(a.Tangents);
        arrays[(int)Mesh.ArrayType.TexUV] = Variant.CreateFrom(MemoryMarshal.Cast<System.Numerics.Vector2, Godot.Vector2>(a.UVs.AsSpan()));
        arrays[(int)Mesh.ArrayType.Bones] = Variant.CreateFrom(a.Bones);
        arrays[(int)Mesh.ArrayType.Weights] = Variant.CreateFrom(a.Weights);
        arrays[(int)Mesh.ArrayType.Index] = Variant.CreateFrom(a.Indices);
        return arrays;
    }

    private bool MergeJobStillValid(MergeJob job) =>
        job.State.Serial == job.Serial && ReferenceEquals(job.Visual.OutfitMerge, job.State)
        && _visuals.TryGetValue(job.Visual.EntityId, out var live) && ReferenceEquals(live, job.Visual)
        && job.Visual.Skeleton != null && IsInstanceValid(job.Visual.Skeleton);

    /// <summary>Main thread, first: the new slots go on the end of the outfit skin, with binds computed
    /// exactly as <see cref="BindRiggedSkin"/> computes them. A compaction builds a new skin.</summary>
    private void CommitMergeBinds(MergeJob job)
    {
        if (!MergeJobStillValid(job) || job.Result == null) return;
        var skeleton = job.Visual.Skeleton!;
        if (job.NewSkin)
        {
            // The binds go on at the swap, so a shape change in between cannot leave them stale.
            job.BuiltSkin = new Skin();
            return;
        }
        var state = job.State;
        if (state.Slots.Count != job.Result.FirstNewSlot) return; // cannot happen: jobs are serial per avatar
        state.Skin ??= new Skin();
        foreach (var key in job.Result.NewSlots)
        {
            state.Skin.AddBind(key.Bone, ComputeOutfitBind(skeleton, job.Visual, key));
            state.Slots.Add(key);
        }
        job.BuiltSkin = state.Skin;
    }

    /// <summary>Main thread: hands a batch of merged surfaces to the engine, into meshes that are not in
    /// the tree yet.</summary>
    private void UploadMergeBatch(MergeJob job, (JobBucket Bucket, UploadKind Kind, int Index, int Surface)[] work)
    {
        if (!MergeJobStillValid(job)) return;
        foreach (var (jb, kind, index, surface) in work)
        {
            if (kind == UploadKind.Rebuilt)
            {
                // An un-merged item's surfaces go into its own mesh, in order (batches run in order).
                var rebuilt = jb.Rebuilt[index];
                rebuilt.Mesh ??= new ArrayMesh();
                rebuilt.Mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, rebuilt.Arrays![surface]);
            }
            else if (kind == UploadKind.Sorted)
            {
                var mesh = new ArrayMesh();
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, jb.SortedArrays![index]);
                jb.SortedMeshes.Add(mesh);
            }
            else
            {
                // A mesh holds at most MaxSurfacesPerMesh surfaces; past that a second instance.
                int meshIndex = index / MaxSurfacesPerMesh;
                while (jb.Meshes.Count <= meshIndex) jb.Meshes.Add(new ArrayMesh());
                jb.Meshes[meshIndex].AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, jb.MergedArrays![index]);
            }
        }
    }

    /// <summary>Main thread, last: the swap. Per bucket, only when no item it involves changed since the
    /// snapshot: the new instances in, the old ones and the absorbed staging instances out, the released
    /// staging instances shown -- in this one queue item.</summary>
    private void SwapMergeJob(MergeJob job)
    {
        try
        {
            if (!MergeJobStillValid(job) || job.Result == null || job.BuiltSkin == null || job.Visual.IsReduced)
            {
                RequeueJobBuckets(job);
                return;
            }

            var state = job.State;
            bool allValid = true;
            foreach (var jb in job.Buckets)
                if (!BucketStillValid(jb)) { allValid = false; break; }
            if (job.NewSkin && !allValid)
            {
                // A compaction swaps every bucket onto the new skin together, or none.
                state.CompactPending = true;
                RequeueJobBuckets(job);
                return;
            }
            if (job.NewSkin)
            {
                var skeleton = job.Visual.Skeleton!;
                foreach (var key in job.Result.NewSlots) job.BuiltSkin.AddBind(key.Bone, ComputeOutfitBind(skeleton, job.Visual, key));
            }

            foreach (var jb in job.Buckets)
            {
                bool valid = BucketStillValid(jb);
                if (!state.Buckets.TryGetValue(jb.Key, out var bucket))
                {
                    // Freed meanwhile (its linkset was taken off): nothing to put back.
                    if (!valid) continue;
                    bucket = state.Buckets[jb.Key] = new OutfitBucket { Key = jb.Key };
                }
                if (!valid)
                {
                    _mergeSwapsDiscarded++;
                    MarkDirty(bucket, immediate: false);
                    continue;
                }
                SwapBucket(job, state, bucket, jb);
                if (bucket.Instances.Count == 0 && bucket.Sorted.Count == 0 && bucket.Members.Count == 0 && !bucket.IsDirty)
                    state.Buckets.Remove(jb.Key);
            }

            if (job.NewSkin)
            {
                state.Skin = job.BuiltSkin;
                state.Slots.Clear();
                state.Slots.AddRange(job.Result.NewSlots);
            }
            _mergeJobsDone++;
            CheckSlotGarbage(state);
            if (Diagnostics.Enabled)
                GD.Print($"[OutfitMerge] avatar={job.Visual.AgentId.ToString("N")[..8]} buckets={job.Buckets.Count} " +
                         $"surfaces {job.Result.SurfacesIn} -> {job.Result.SurfacesOut} verts={job.Result.Vertices} " +
                         $"slots={state.Slots.Count} (+{job.Result.NewSlots.Count}) worker={job.WorkerMs:F1} ms");
        }
        finally
        {
            // Worker time, filed here because only the main thread writes the cost table; deliberately not
            // under avatar.merge.*, which are the main-thread items.
            MainThreadWorkQueue.RecordExternal("worker.outfitmerge", job.WorkerMs);
            FinishMergeJob(job);
        }
    }

    private void FinishMergeJob(MergeJob job)
    {
        if (ReferenceEquals(job.Visual.OutfitMerge, job.State) && job.State.Serial == job.Serial)
        {
            job.State.JobInFlight = false;
            // Switched off and every item released: the avatar's merge state (and outfit skin) goes.
            DropMergeStateIfEmpty(job.Visual);
        }
        _mergeJobsRunning = Math.Max(0, _mergeJobsRunning - 1);
        // A job that failed or was dropped: its buckets are tried again.
        if (job.Result == null) RequeueJobBuckets(job);
    }

    private static void RequeueJobBuckets(MergeJob job)
    {
        foreach (var jb in job.Buckets)
            if (job.State.Buckets.TryGetValue(jb.Key, out var bucket)) MarkDirty(bucket, immediate: false);
    }

    /// <summary>No item the bucket's job looked at changed since (a removal bumps the generation too),
    /// and every staging instance it is to absorb is still there.</summary>
    private static bool BucketStillValid(JobBucket jb)
    {
        foreach (var (item, generation) in jb.Stamps)
            if (item.Generation != generation) return false;
        foreach (var (item, _, absorb) in jb.Included)
            if (absorb && (item.Staging == null || !IsInstanceValid(item.Staging))) return false;
        return true;
    }

    private void SwapBucket(MergeJob job, OutfitMergeAvatar state, OutfitBucket bucket, JobBucket jb)
    {
        var skeleton = job.Visual.Skeleton!;
        var skin = job.BuiltSkin!;
        var result = jb.Result;
        var inputs = new Dictionary<Guid, MergeInput>();
        foreach (var (item, input, _) in jb.Included) inputs[item.EntityId] = input;

        // The old instances out first, so the new ones keep their names.
        FreeBucketNodes(bucket);
        bucket.DrawnAt.Clear();

        if (result != null)
        {
            var box = new Aabb(new Godot.Vector3(-4, -4, -4), new Godot.Vector3(8, 8, 8));
            for (int m = 0; m < jb.Meshes.Count; m++)
            {
                var mi = new MeshInstance3D
                {
                    Name = $"{OutfitBucketName}_{jb.Key}_{m}",
                    Mesh = jb.Meshes[m],
                    Skin = skin,
                    CustomAabb = box,
                };
                int first = m * MaxSurfacesPerMesh;
                int count = jb.Meshes[m].GetSurfaceCount();
                for (int s = 0; s < count; s++)
                {
                    var surface = result.Surfaces[first + s];
                    mi.SetSurfaceOverrideMaterial(s, MaterialFor(inputs, surface.Ranges[0].ItemId, surface.Ranges[0].SurfaceIndex));
                    foreach (var range in surface.Ranges) bucket.DrawnAt[(range.ItemId, range.SurfaceIndex)] = (mi, s);
                }
                if (_shadowsDisabled) mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                skeleton.AddChild(mi);
                mi.Skeleton = mi.GetPathTo(skeleton);
                bucket.Instances.Add(mi);
            }
            for (int s = 0; s < jb.SortedMeshes.Count && s < result.Sorted.Count; s++)
            {
                var sorted = result.Sorted[s];
                var mi = new MeshInstance3D
                {
                    Name = $"{OutfitSortedName}_{jb.Key}_{s}",
                    Mesh = jb.SortedMeshes[s],
                    Skin = skin,
                    CustomAabb = box,
                    // A blended surface casts no shadow (no depth write), as on today's split children.
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };
                var material = MaterialFor(inputs, sorted.ItemId, sorted.SurfaceIndex);
                mi.SetSurfaceOverrideMaterial(0, material);
                // BUG-RENDER-50: placed by the wearer's ranks as they are NOW (an attach or detach may have
                // moved them since the job read its inputs), with the item's depth pass after it.
                PlaceSortedInstance(mi, WornAlphaBaseOf(job.Visual, sorted.ItemId,
                    sorted.SortingOffset - WornAlphaOrder.SurfaceOffset(0f, sorted.SurfaceIndex)), sorted.SurfaceIndex);
                skeleton.AddChild(mi);
                mi.Skeleton = mi.GetPathTo(skeleton);
                SetSortedDepthPass(mi, material);
                bucket.Sorted.Add(mi);
                bucket.DrawnAt[(sorted.ItemId, sorted.SurfaceIndex)] = (mi, 0);
            }
            if (_outfitMergeKeepArrays) _outfitMergeKept[(job.Visual.EntityId, jb.Key)] = result;
        }

        // Released: shown again, now that the bucket no longer draws them.
        foreach (var item in jb.Released)
        {
            item.InBucket = false;
            item.Merged = null;
            item.MergedRemap = null;
            ShowStaging(item);
            // Out of the merge for good when the setting is off; otherwise it may come back (deselect).
            if (!RenderConfig.OutfitMerge) ForgetStagedItem(item);
        }

        // Rebuilt: an instance of its own again, from the arrays and materials the bucket drew it with.
        foreach (var rebuilt in jb.Rebuilt)
        {
            var item = rebuilt.Item;
            item.InBucket = false;
            item.Merged = null;
            item.MergedRemap = null;
            if (rebuilt.Mesh != null && rebuilt.Arrays != null && rebuilt.Mesh.GetSurfaceCount() == rebuilt.Arrays.Length)
                CommitRebuiltStaging(item, rebuilt.Input, rebuilt.Mesh, rebuilt.Arrays, skeleton);
            else if (item.Request is { } request && IsInstanceValid(request.Skeleton) && !_pendingRigs.ContainsKey(item.EntityId))
                RequestRig(item.EntityId, request); // not expected: the uploads ran before this swap
            if (!RenderConfig.OutfitMerge) ForgetStagedItem(item);
        }

        bucket.Members.Clear();
        var contributors = result != null ? new HashSet<Guid>(result.Items) : new HashSet<Guid>();
        foreach (var (item, input, absorb) in jb.Included)
        {
            // An item that draws nothing here (every face hidden or failed) stays on its own instance.
            bool contributed = contributors.Contains(item.EntityId);
            if (contributed && absorb && item.Staging is { } staging && IsInstanceValid(staging))
                AbsorbStaging(item, staging);
            else if (!contributed && item.Held) ShowStaging(item);
            item.InBucket = contributed;
            item.Merged = contributed ? input : null;
            item.MergedRemap = contributed && job.Result!.Remap.TryGetValue(item.EntityId, out var remap) ? remap : null;
            if (contributed) bucket.Members.Add(item.EntityId);
        }
    }

    /// <summary>A held staging instance is drawn again: the bucket no longer draws its item.</summary>
    private void ShowStaging(MergeItem item)
    {
        item.Held = false;
        if (item.Staging is not { } staging || !IsInstanceValid(staging)) return;
        _mergeHeld.Remove(staging);
        ReattachSkeleton(staging);
        staging.Visible = true;
        UpdateWornMeshVisibility(staging, item.Visual);
    }

    /// <summary>The un-merge of one item (the setting went off): an instance of its own, made the way
    /// <see cref="CommitPreparedRig"/> makes one, from what the bucket drew -- the rig's arrays (uploaded by
    /// the job into <paramref name="mesh"/>), its materials, its sorting offset -- with a skin of its own
    /// slots, bound exactly as <see cref="BindRiggedSkin"/> binds them. Nothing is fetched, rigged or built
    /// again, so the swap that frees the bucket shows it in the same queue item.</summary>
    private void CommitRebuiltStaging(MergeItem item, MergeInput input, ArrayMesh mesh, Godot.Collections.Array[] arrays,
        Skeleton3D skeleton)
    {
        var visual = item.Visual;
        var rig = input.Rig;
        var bones = new List<int>(rig.Slots.Length);
        var binds = new List<Transform3D>(rig.Slots.Length);
        foreach (var key in rig.Slots)
        {
            bones.Add(key.Bone);
            binds.Add(ComputeOutfitBind(skeleton, visual, key));
        }
        var skin = SharedOrNewSkin(visual, skeleton, bones, binds);

        // What the rig path leaves for a later split, an adoption (the setting on again) or a pick.
        MeshSurfaceCache.Store(mesh, arrays);
        RememberRigSurfaceFaces(mesh, rig.FaceIndices);

        var mi = new MeshInstance3D
        {
            Name = "RiggedMesh",
            Mesh = mesh,
            Skin = skin,
            CustomAabb = new Aabb(new Godot.Vector3(-4, -4, -4), new Godot.Vector3(8, 8, 8)),
        };
        for (int s = 0; s < input.Materials.Length && s < mesh.GetSurfaceCount(); s++)
            mi.SetSurfaceOverrideMaterial(s, input.Materials[s]);
        // BUG-RENDER-50: back in its wearer's alpha order on an instance of its own (placed before the split
        // below, which reads the slot from it).
        RegisterWornAlphaItem(visual, item.EntityId, mi, WearsBake(input.Faces, input.DefaultFace, rig.FaceIndices));
        skeleton.AddChild(mi);
        mi.Skeleton = mi.GetPathTo(skeleton);

        _riggedAttachments[item.EntityId] = mi;
        if (input.MeshData != null) visual.RiggedAttachments.Add((mi, input.MeshData, input.MeshId));
        if (visual.BomAttachments.TryGetValue(item.EntityId, out var bom))
            visual.BomAttachments[item.EntityId] = bom with { Mi = mi };

        // The blended surfaces onto their own instances again (BUG-RENDER-38), from the cached arrays; then
        // shown, or hidden when every face is (or the wearer was parked meanwhile).
        SplitSortedSurfaces(mi, input.MeshId);
        UpdateWornMeshVisibility(mi, visual);

        item.Staging = mi;
        item.StagingSettled = true;
        item.Held = false;
        item.SortingOffset = WornAlphaBaseOf(visual, item.EntityId, input.SortingOffset);
        RestoreWornHighlight(item.EntityId);
    }

    private static Material MaterialFor(Dictionary<Guid, MergeInput> inputs, Guid item, int surface) =>
        inputs[item].Materials[surface];

    /// <summary>The bucket draws the item now: its own instance goes, and with it the item's entries in
    /// the per-instance bookkeeping. Its BoM registration stays, keyed by entity.</summary>
    private void AbsorbStaging(MergeItem item, MeshInstance3D staging)
    {
        var visual = item.Visual;
        if (_riggedAttachments.TryGetValue(item.EntityId, out var current) && current == staging)
            _riggedAttachments.Remove(item.EntityId);
        visual.RiggedAttachments.RemoveAll(r => r.Mi == staging);
        if (visual.BomAttachments.TryGetValue(item.EntityId, out var bom) && bom.Mi == staging)
            visual.BomAttachments[item.EntityId] = bom with { Mi = null };
        ClearRiggedPickBodies(item.EntityId);
        _mergeHeld.Remove(staging);
        DetachSkeleton(staging);
        staging.GetParent()?.RemoveChild(staging);
        staging.QueueFree();
        item.Staging = null;
        item.Held = false;
    }

    /// <summary>A staged item the merge has no use for any more (the setting went off).</summary>
    private void ForgetStagedItem(MergeItem item)
    {
        if (item.InBucket) return;
        _mergeItems.Remove(item.EntityId);
        item.Visual.OutfitMerge?.Items.Remove(item.EntityId);
        if (item.Staging != null) _mergeHeld.Remove(item.Staging);
        DropMergeStateIfEmpty(item.Visual);
    }

    /// <summary>An avatar whose merge holds no item any more lets go of the state and its outfit skin. A
    /// bucket without items draws nothing that is still worn, so its instances (if any) go too.</summary>
    private void DropMergeStateIfEmpty(AvatarVisual visual)
    {
        var state = visual.OutfitMerge;
        if (state == null || state.Items.Count > 0 || state.JobInFlight) return;
        foreach (var bucket in state.Buckets.Values) FreeBucketNodes(bucket);
        state.Buckets.Clear();
        state.Serial++;
        visual.OutfitMerge = null;
    }

    private static void FreeBucketNodes(OutfitBucket bucket)
    {
        foreach (var node in bucket.Instances) FreeMergedNode(node);
        foreach (var node in bucket.Sorted) FreeMergedNode(node);
        bucket.Instances.Clear();
        bucket.Sorted.Clear();

        static void FreeMergedNode(MeshInstance3D node)
        {
            if (!IsInstanceValid(node)) return;
            // Out of the tree now, not at the end of the frame: its skin binding goes at once and a new
            // instance of the same name keeps its name.
            node.Skeleton = new NodePath();
            // BUG-RENDER-50: a sorted instance's depth pass shares the outfit skin too.
            foreach (var child in node.GetChildren())
                if (child is MeshInstance3D depth) depth.Skeleton = new NodePath();
            node.GetParent()?.RemoveChild(node);
            node.QueueFree();
        }
    }

    /// <summary>The slot table is append-only; once it is more than twice what the buckets still point
    /// at, the next job rebuilds it (and the skin) over every bucket.</summary>
    private static void CheckSlotGarbage(OutfitMergeAvatar state)
    {
        if (state.Slots.Count < CompactMinSlots) return;
        var used = new HashSet<int>();
        foreach (var item in state.Items.Values)
            if (item.InBucket && item.MergedRemap != null)
                foreach (int slot in item.MergedRemap) used.Add(slot);
        if (state.Slots.Count > 2 * used.Count) state.CompactPending = true;
    }

    // ---- the skin -----------------------------------------------------------------------------------

    /// <summary>The bind of one outfit slot, computed exactly as <see cref="BindRiggedSkin"/> computes a
    /// per-item one, so the merged skinning is bit-identical to the per-item skins.</summary>
    private Transform3D ComputeOutfitBind(Skeleton3D skeleton, AvatarVisual visual, OutfitSlotKey key) =>
        ComputeRiggedBind(skeleton, visual, key.Bone, key.IsRest ? System.Numerics.Matrix4x4.Identity : key.InverseBind);

    /// <summary>Shape change or joint-override change: every slot's bind again, in place. One pass over
    /// the outfit skin instead of a new <see cref="Skin"/> per item.</summary>
    private void RebindOutfitSkin(AvatarVisual visual)
    {
        var state = visual.OutfitMerge;
        if (state?.Skin == null || visual.Skeleton == null || !IsInstanceValid(visual.Skeleton)) return;
        var skeleton = visual.Skeleton;
        for (int i = 0; i < state.Slots.Count && i < state.Skin.GetBindCount(); i++)
            state.Skin.SetBindPose(i, ComputeOutfitBind(skeleton, visual, state.Slots[i]));
    }

    // ---- BoM: a new bake on merged surfaces -----------------------------------------------------------

    /// <summary>A new bake for a merged item's Bakes-on-Mesh faces: the material is rebuilt and put on the
    /// surface that draws the face, in place. Only a Kind that crosses the Blend line re-merges.</summary>
    private async Task RefreshMergedBomMaterialsAsync(MergeItem item)
    {
        var input = item.Merged;
        if (input == null) return;
        var visual = item.Visual;
        var rig = input.Rig;
        var rebuilt = new List<(int Surface, ShaderMaterial Material)>();
        for (int s = 0; s < rig.Surfaces.Length; s++)
        {
            int faceIndex = s < rig.FaceIndices.Length ? rig.FaceIndices[s] : 0;
            var face = ResolveFaceTexture(input.Faces, input.DefaultFace, faceIndex);
            // BUG-RENDER-49: by what the material sampled -- a PBR face's base colour can be the bake.
            if (s >= input.Facts.Length || BakeChannelOf(input, faceIndex, s) < 0) continue;
            var material = await BuildFaceMaterialAsync(face, visual, item.MeshId, faceIndex).ConfigureAwait(false);
            rebuilt.Add((s, material));
        }
        if (rebuilt.Count == 0) return;
        MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () => ApplyMergedBomMaterials(item, input, rebuilt),
            label: "avatar.merge.bom");
    }

    private void ApplyMergedBomMaterials(MergeItem item, MergeInput input, List<(int Surface, ShaderMaterial Material)> rebuilt)
    {
        // Still drawing exactly what the materials were rebuilt for (a re-merge since would have them).
        if (!_mergeItems.TryGetValue(item.EntityId, out var live) || !ReferenceEquals(live, item)
            || !ReferenceEquals(item.Merged, input))
            return;
        var state = item.Visual.OutfitMerge;
        var bucket = state != null && state.Buckets.TryGetValue(item.Linkset, out var b) ? b : null;

        var materials = (Material[])input.Materials.Clone();
        var facts = (FaceMaterialFacts[])input.Facts.Clone();
        bool roleChanged = false;
        foreach (var (s, material) in rebuilt)
        {
            var now = FactsOf(material);
            var was = facts[s];
            bool wasSorted = was.Kind == PrimShaderFamily.Kind.Blend && !was.Failed && !was.Hidden;
            bool isSorted = now.Kind == PrimShaderFamily.Kind.Blend && !now.Failed && !now.Hidden;
            bool wasSkip = was.Failed || was.Hidden;
            bool isSkip = now.Failed || now.Hidden;
            if (wasSorted != isSorted || wasSkip != isSkip) roleChanged = true;
            else if (bucket != null && bucket.DrawnAt.TryGetValue((item.EntityId, s), out var at) && IsInstanceValid(at.Node))
            {
                at.Node.SetSurfaceOverrideMaterial(at.Surface, material);
                // BUG-RENDER-50: a sorted instance's depth pass samples the new bake too.
                if (bucket.Sorted.Contains(at.Node)) SetSortedDepthPass(at.Node, material);
            }
            materials[s] = material;
            facts[s] = now;
        }

        var updated = new MergeInput
        {
            Rig = input.Rig,
            Materials = materials,
            Facts = facts,
            SortingOffset = input.SortingOffset,
            Faces = input.Faces,
            DefaultFace = input.DefaultFace,
            MeshData = input.MeshData,
            MeshId = input.MeshId,
        };
        item.Merged = updated;
        // The newest version is this one only when no newer rig is on its way.
        if (ReferenceEquals(item.Newest, input)) item.Newest = updated;
        // A job in flight read the old materials: it must not swap them back in.
        item.Generation++;
        if (roleChanged) MarkBucketDirty(item, immediate: true);
    }

    // ---- park, unpark, unload ---------------------------------------------------------------------

    /// <summary>BUG-PERF-16 park: the buckets and sorted instances are hidden and leave the skeleton --
    /// one skin binding instead of one per worn item.</summary>
    private static void ParkOutfitMerge(AvatarVisual visual)
    {
        var state = visual.OutfitMerge;
        if (state == null) return;
        foreach (var bucket in state.Buckets.Values)
        {
            foreach (var node in bucket.Instances) Park(node);
            foreach (var node in bucket.Sorted) Park(node);
        }

        static void Park(MeshInstance3D node)
        {
            if (!IsInstanceValid(node)) return;
            DetachSkeleton(node);
            node.Visible = false;
        }
    }

    private static void UnparkOutfitMerge(AvatarVisual visual)
    {
        var state = visual.OutfitMerge;
        if (state == null) return;
        foreach (var bucket in state.Buckets.Values)
        {
            foreach (var node in bucket.Instances) Unpark(node);
            foreach (var node in bucket.Sorted) Unpark(node);
        }

        static void Unpark(MeshInstance3D node)
        {
            if (!IsInstanceValid(node)) return;
            ReattachSkeleton(node);
            node.Visible = true;
        }
    }

    /// <summary>The avatar lets go of its outfit (<see cref="UnloadAvatarOutfit"/>, or it left): buckets,
    /// sorted instances, the outfit skin and every item's retained arrays go. A job in flight commits
    /// nothing.</summary>
    private void UnloadOutfitMerge(AvatarVisual visual)
    {
        var state = visual.OutfitMerge;
        if (state == null) return;
        foreach (var bucket in state.Buckets.Values) FreeBucketNodes(bucket);
        foreach (var id in state.Items.Keys)
        {
            if (_mergeItems.Remove(id, out var item) && item.Staging != null) _mergeHeld.Remove(item.Staging);
        }
        state.Items.Clear();
        state.Buckets.Clear();
        state.Slots.Clear();
        state.Skin = null;
        state.Serial++;
        if (state.JobInFlight)
        {
            // The job's own commit sees the serial and lets go of its slot; the state goes with it.
            state.JobInFlight = false;
        }
        visual.OutfitMerge = null;
        _outfitMergeKept.Clear();
    }

    // ---- the setting switch ---------------------------------------------------------------------------

    /// <summary>Switched off. A parked avatar lets go of its outfit (<see cref="UnloadAvatarOutfit"/>): its
    /// buckets are never drawn and a job never runs for it, so un-merging it would leave merged nodes behind
    /// until it is promoted; promotion loads the outfit again. A full avatar's buckets are un-merged at once
    /// by their next job, which rebuilds each item's own instance from what the bucket draws (no re-rig) and
    /// swaps it in with the bucket's removal. Staged items are simply let go.</summary>
    private void UnmergeAll()
    {
        foreach (var visual in new List<AvatarVisual>(_visuals.Values))
        {
            var state = visual.OutfitMerge;
            if (state == null) continue;
            if (visual.IsReduced)
            {
                UnloadAvatarOutfit(visual);
                continue;
            }
            foreach (var item in new List<MergeItem>(state.Items.Values))
                if (!item.InBucket) ForgetStagedItem(item);
            if (visual.OutfitMerge == null) continue;
            foreach (var bucket in state.Buckets.Values) MarkDirty(bucket, immediate: true);
            DropMergeStateIfEmpty(visual);
        }
        // Items of avatars that are gone or never had a state: nothing draws them from a bucket.
        foreach (var item in new List<MergeItem>(_mergeItems.Values))
            if (!item.InBucket) ForgetStagedItem(item);
    }

    /// <summary>Switched on: items rigged while it was off have no retained arrays. They are adopted
    /// from the arrays <see cref="MeshSurfaceCache"/> already holds for their instance, converted to
    /// managed ones on a worker -- also those still tracked (a newer version rigged while it was off waits,
    /// held, for its bucket). Parked avatars are adopted when they are promoted (<see cref="SetAvatarFull"/>).</summary>
    private void AdoptAllStagedItems()
    {
        foreach (var visual in _visuals.Values)
            if (!visual.IsReduced) AdoptStagedItemsOf(visual);
    }

    /// <summary>Every worn rigged item of <paramref name="visual"/> that has its own instance and no retained
    /// arrays starts its adoption.</summary>
    private void AdoptStagedItemsOf(AvatarVisual visual)
    {
        if (!MergeEnabledFor(visual) || visual.IsReduced) return;
        foreach (var entityId in visual.WornAttachmentEntities)
        {
            if (!_riggedAttachments.TryGetValue(entityId, out var mi)) continue;
            if (_mergeItems.TryGetValue(entityId, out var tracked))
            {
                // A settled staging without arrays: converted now. An unsettled one does it when it settles.
                if (tracked.Rig == null && ReferenceEquals(tracked.Staging, mi) && tracked.StagingSettled)
                    BeginRetainFromStaging(tracked, mi);
                continue;
            }
            AdoptStagedItem(entityId, mi, visual);
        }
    }

    private void AdoptStagedItem(Guid entityId, MeshInstance3D mi, AvatarVisual visual)
    {
        if (!IsInstanceValid(mi) || mi.Mesh is not ArrayMesh || mi.GetParent() != visual.Skeleton) return;
        if (!_attachmentMeshIds.TryGetValue(entityId, out var meshInfo)) return;
        var found = RiggedRecordOf(visual, mi);
        if (found?.MeshData.Skin == null) return;

        var item = NewMergeItem(entityId, visual);
        item.Request = new PendingRig(found.Value.MeshData, visual, visual.Skeleton!, found.Value.MeshId, meshInfo.Faces,
                                      meshInfo.DefaultFace, _avatarSkeleton, WantPick: false);
        item.Faces = meshInfo.Faces;
        item.DefaultFace = meshInfo.DefaultFace;
        item.MeshId = found.Value.MeshId;
        item.SortingOffset = WornAlphaBaseOf(visual, entityId, mi.SortingOffset);
        item.Staging = mi;
        SetLinksetOrder(item);
        item.Generation++;
        if (!BeginRetainFromStaging(item, mi)) ForgetStagedItem(item);
    }

    private static (MeshInstance3D Mi, MeshData MeshData, Guid MeshId)? RiggedRecordOf(AvatarVisual visual, MeshInstance3D mi)
    {
        foreach (var r in visual.RiggedAttachments) if (r.Mi == mi) return r;
        return null;
    }

    /// <summary>Starts converting the arrays <paramref name="mi"/> was built from (kept by
    /// <see cref="MeshSurfaceCache"/>) into the item's retained ones, on a worker; the item's staging then
    /// settles again and can merge. False when there is nothing to convert from.</summary>
    private bool BeginRetainFromStaging(MergeItem item, MeshInstance3D mi)
    {
        if (item.Retaining) return true;
        var visual = item.Visual;
        if (!IsInstanceValid(mi) || mi.Mesh is not ArrayMesh mesh || visual.Skeleton == null || mi.GetParent() != visual.Skeleton)
            return false;
        if (!MeshSurfaceCache.TryGetSurfaces(mesh, out var arrays) || !_rigSurfaceFaces.TryGetValue(mesh, out var faceIndices))
            return false;
        var skinData = (RiggedRecordOf(visual, mi)?.MeshData ?? item.Request?.MeshData)?.Skin;
        if (skinData == null) return false;

        var skeleton = visual.Skeleton;
        // The joint-to-slot map BindRiggedSkin made for this skin: bound joints in order.
        var slotForJoint = new int[skinData.JointNames.Length];
        int next = 0;
        for (int j = 0; j < slotForJoint.Length; j++)
        {
            string name = _avatarSkeleton != null ? _avatarSkeleton.ResolveBoneName(skinData.JointNames[j]) : skinData.JointNames[j];
            slotForJoint[j] = skeleton.FindBone(name) >= 0 ? next++ : -1;
        }
        var slots = SlotKeysFor(skinData, skeleton, slotForJoint);
        if (slots == null) return false;

        item.Retaining = true;
        var entityId = item.EntityId;
        var surfaces = (Godot.Collections.Array[])arrays.Clone();
        _ = Task.Run(() =>
        {
            RetainedRig? rig = null;
            if (EngineWorkerGate.TryEnter())
            {
                try { rig = ConvertSurfaceArrays(surfaces, faceIndices, slots); }
                catch (Exception ex) { GD.PushWarning($"[OutfitMerge] adopting {entityId:N} failed: {ex.Message}"); }
                finally { EngineWorkerGate.Exit(); }
            }
            MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual, () =>
            {
                item.Retaining = false;
                if (!_mergeItems.TryGetValue(entityId, out var live) || !ReferenceEquals(live, item)
                    || !ReferenceEquals(item.Staging, mi) || item.Rig != null)
                    return;
                if (rig == null)
                {
                    // Nothing to merge it from: it stays on its own instance, and a bucket lets it go.
                    item.Generation++;
                    MarkBucketDirty(item, immediate: false);
                    return;
                }
                item.Rig = rig;
                NoteStagingSettled(entityId, mi);
            }, label: "avatar.merge.adopt");
        });
        return true;
    }

    /// <summary>Worker: a staging mesh's retained engine arrays as managed ones. Read-only on arrays
    /// nothing writes to any more.</summary>
    private static RetainedRig? ConvertSurfaceArrays(Godot.Collections.Array[] surfaces, int[] faceIndices, OutfitSlotKey[] slots)
    {
        var converted = new OutfitMeshArrays[surfaces.Length];
        long bytes = 0;
        for (int s = 0; s < surfaces.Length; s++)
        {
            var a = surfaces[s];
            var positions = MemoryMarshal.Cast<Godot.Vector3, System.Numerics.Vector3>(a[(int)Mesh.ArrayType.Vertex].AsVector3Array()).ToArray();
            var normals = MemoryMarshal.Cast<Godot.Vector3, System.Numerics.Vector3>(a[(int)Mesh.ArrayType.Normal].AsVector3Array()).ToArray();
            var tangents = a[(int)Mesh.ArrayType.Tangent].AsFloat32Array();
            var uvs = MemoryMarshal.Cast<Godot.Vector2, System.Numerics.Vector2>(a[(int)Mesh.ArrayType.TexUV].AsVector2Array()).ToArray();
            var bones = a[(int)Mesh.ArrayType.Bones].AsInt32Array();
            var weights = a[(int)Mesh.ArrayType.Weights].AsFloat32Array();
            var index = a[(int)Mesh.ArrayType.Index];
            var indices = index.VariantType == Variant.Type.Nil ? Array.Empty<int>() : index.AsInt32Array();
            converted[s] = new OutfitMeshArrays(positions, normals, tangents, uvs, bones, weights, indices);
            bytes += ArrayBytes(converted[s]);
        }
        return new RetainedRig { Surfaces = converted, FaceIndices = faceIndices, Slots = slots, Bytes = bytes };
    }

    // ---- [AvatarCost] -----------------------------------------------------------------------------

    /// <summary>The merge's part of <c>[AvatarCost]</c>.</summary>
    private string DescribeOutfitMergeCost()
    {
        int avatars = 0, buckets = 0, sorted = 0, merged = 0, staged = 0, slots = 0;
        var rigs = new HashSet<RetainedRig>(ReferenceEqualityComparer.Instance);
        long bytes = 0;
        foreach (var visual in _visuals.Values)
        {
            var state = visual.OutfitMerge;
            if (state != null)
            {
                if (state.Buckets.Count > 0) avatars++;
                foreach (var bucket in state.Buckets.Values)
                {
                    buckets += bucket.Instances.Count;
                    sorted += bucket.Sorted.Count;
                }
                slots += state.Slots.Count;
                foreach (var item in state.Items.Values)
                {
                    if (item.InBucket) merged++;
                    foreach (var rig in new[] { item.Rig, item.Newest?.Rig, item.Merged?.Rig })
                        if (rig != null && rigs.Add(rig)) bytes += rig.Bytes;
                }
            }
        }
        // Staged: worn rigged items of full remote avatars drawn on their own instance.
        foreach (var (entityId, mi) in _riggedAttachments)
        {
            if (!IsInstanceValid(mi) || !mi.Visible) continue;
            if (_attachmentToAvatar.TryGetValue(entityId, out var wearer) && _visuals.TryGetValue(wearer, out var v)
                && !v.IsSelf && !v.IsReduced)
                staged++;
        }
        string line = $"outfitMerge={(RenderConfig.OutfitMerge ? "on" : "off")} mergedAvatars={avatars} buckets={buckets} " +
                      $"sortedInstances={sorted} stagedItems={staged} mergedItems={merged} outfitSlots={slots} " +
                      $"mergeCpuMB={bytes / (1024.0 * 1024.0):F1} mergeJobs={_mergeJobsDone} mergeDiscarded={_mergeSwapsDiscarded}";
        _mergeJobsDone = _mergeSwapsDiscarded = 0;
        return line;
    }
}
