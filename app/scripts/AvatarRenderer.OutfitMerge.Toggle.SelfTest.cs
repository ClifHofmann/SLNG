using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Godot;
using SLNG.Assets;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.App;

public partial class AvatarRenderer
{
    /// <summary>
    /// FEAT-PERF-23 fix: "outfit merge switch off, on, off". A full avatar A (three items, two linksets) and an
    /// avatar P that is merged and then parked.
    /// <list type="number">
    /// <item>Off: P lets go of its outfit at once (no merged node, nothing drawn); A's items get their own
    /// instances back without a single re-rig -- skinned like before the merge, the same materials and split
    /// sorted surfaces -- and no merged node or merge state is left.</item>
    /// <item>On: every item of A is merged again (adopted from its rebuilt instance).</item>
    /// <item>Off and straight back on while the un-merge job is in flight: every item ends merged.</item>
    /// <item>Off, a new version of an item rigged while off (no arrays kept, held), then on before its bucket let
    /// go of it: it ends merged, as the new version.</item>
    /// <item>Off at the end: no merged node anywhere, every item of A drawn on its own instance.</item>
    /// </list>
    /// </summary>
    internal (bool Passed, string Detail) SelfTestOutfitMergeToggle()
    {
        LoadAvatarSkeleton();
        if (_avatarSkeleton == null) return (false, "the avatar skeleton did not load");

        const ulong Region = 11437119954698752UL;
        var problems = new List<string>();
        var priorWorld = _world;
        var priorCache = _gpuCache;
        bool priorSetting = RenderConfig.OutfitMerge;
        bool priorApplied = _outfitMergeApplied;
        var world = new World();
        _world = world;
        _gpuCache = new GpuCache(64L << 20);
        var visuals = new List<AvatarVisual>();
        var worn = new Dictionary<AvatarVisual, List<Guid>>();
        string timing = "";

        try
        {
            RenderConfig.OutfitMerge = true;
            _outfitMergeApplied = true;

            FaceTexture Face(float alpha) =>
                new(Guid.NewGuid(), Guid.Empty, Guid.Empty, new System.Numerics.Vector4(1f, 1f, 1f, alpha), 1f, 1f, 0f, 0f, 0f);
            var ibmTorso = System.Numerics.Matrix4x4.CreateTranslation(0f, 0f, -1.2f);
            var ibmChest = System.Numerics.Matrix4x4.CreateRotationZ(0.1f) * System.Numerics.Matrix4x4.CreateTranslation(0.01f, 0f, -1.35f);
            var ibmNeck = System.Numerics.Matrix4x4.CreateTranslation(0f, 0.02f, -1.55f);
            var meshA = TestMesh(new[] { "mPelvis", "mTorso", "mChest" }, new[] { System.Numerics.Matrix4x4.Identity, ibmTorso, ibmChest }, faces: 2, x: -0.2f);
            var meshB = TestMesh(new[] { "mChest", "mNeck", "mTorso" }, new[] { ibmChest, ibmNeck, ibmTorso }, faces: 3, x: 0f);
            var meshC = TestMesh(new[] { "mNeck", "mPelvis" }, new[] { ibmNeck, System.Numerics.Matrix4x4.Identity }, faces: 2, x: 0.2f);

            AvatarVisual Wearer(uint localId, float distance)
            {
                var wearer = world.GetOrCreateEntity(Region, localId);
                wearer.SetComponent(new AvatarComponent(Guid.NewGuid(), "Toggle", "Test", false));
                var visual = NewSelfTestWearer(wearer.Id, wearer.GetComponent<AvatarComponent>()!.AgentId);
                visual.GodotPos = new Godot.Vector3(distance, 0f, 0f);
                visual.BoneOwnScale["mChest"] = new System.Numerics.Vector3(1.1f, 0.9f, 1.05f);
                visuals.Add(visual);
                worn[visual] = new List<Guid>();
                return visual;
            }

            Guid Worn(AvatarVisual visual, uint localId, uint parentLocalId)
            {
                var e = world.GetOrCreateEntity(Region, localId);
                e.SetComponent(new TransformComponent { ParentLocalId = parentLocalId });
                e.SetComponent(new PrimitiveComponent(System.Numerics.Vector3.One, profileCurve: 0));
                e.SetComponent(new AttachmentComponent(visual.EntityId, 1));
                worn[visual].Add(e.Id);
                return e.Id;
            }

            var a = Wearer(1, 2f);
            var p = Wearer(2, 3f);
            // A: B (root) and A1 in one linkset, C in another. P: one item.
            var aB = Worn(a, 100, parentLocalId: 1);
            var aA = Worn(a, 101, parentLocalId: 100);
            var aC = Worn(a, 200, parentLocalId: 1);
            var pX = Worn(p, 300, parentLocalId: 2);
            var meshes = new Dictionary<Guid, (MeshData Data, FaceTexture[] Faces)>
            {
                [aA] = (meshA, new[] { Face(1f), Face(1f) }),
                [aB] = (meshB, new[] { Face(1f), Face(0.5f), Face(0.5f) }),
                [aC] = (meshC, new[] { Face(1f), Face(0.5f) }),
                [pX] = (meshA, new[] { Face(1f), Face(1f) }),
            };
            foreach (var (id, (data, faces)) in meshes)
                CommitSelfTestRig(visuals.First(v => worn[v].Contains(id)), id, data, faces, keepTangents: true);
            DrainQueue();
            TickWornAlphaOrder(); // BUG-RENDER-50: what _Process does every frame

            // Today's reference: each item's own instance before any merge.
            var reference = new Dictionary<Guid, (Transform3D[] Palette, Material?[] Materials, float[] Offsets, int Splits)>();
            foreach (var id in worn[a])
                if (_riggedAttachments.TryGetValue(id, out var mi)) reference[id] = Snapshot(a, mi);

            bool AllMerged(AvatarVisual v) => worn[v].All(id => _mergeItems.TryGetValue(id, out var it) && it.InBucket && !_riggedAttachments.ContainsKey(id));
            if (!WaitAll(() => AllMerged(a) && AllMerged(p))) problems.Add("the first merge did not complete within 10 s");

            // P is parked while merged.
            _avatarLimitIgnoreDwell = false;
            SetAvatarReduced(p);
            if (!p.IsParked) problems.Add("P was not parked");

            // --- 1. off ------------------------------------------------------------------------------------
            long rigsBefore = Interlocked.Read(ref _rigsPreparedCount);
            var clock = Stopwatch.StartNew();
            RenderConfig.OutfitMerge = false;
            TickOutfitMerge();
            CheckParkedClean(p, "right after switching off");
            int rounds = 0;
            bool unmerged = WaitAll(() => a.OutfitMerge == null && worn[a].All(id => _riggedAttachments.ContainsKey(id)), () => rounds++);
            timing = $"un-merge {clock.ElapsedMilliseconds} ms / {rounds} tick(s)";
            if (!unmerged) problems.Add("switching off did not un-merge A within 10 s");
            if (Interlocked.Read(ref _rigsPreparedCount) != rigsBefore || !_pendingRigs.IsEmpty)
                problems.Add($"the un-merge re-rigged {Interlocked.Read(ref _rigsPreparedCount) - rigsBefore} item(s) instead of rebuilding them");
            foreach (var id in worn[a])
            {
                if (!_riggedAttachments.TryGetValue(id, out var mi) || !reference.TryGetValue(id, out var before)) continue;
                var now = Snapshot(a, mi);
                float worst = 0f;
                for (int i = 0; i < Math.Min(now.Palette.Length, before.Palette.Length); i++)
                {
                    worst = Math.Max(worst, (now.Palette[i].Origin - before.Palette[i].Origin).Length());
                    for (int c = 0; c < 3; c++) worst = Math.Max(worst, (now.Palette[i].Basis[c] - before.Palette[i].Basis[c]).Length());
                }
                if (now.Palette.Length != before.Palette.Length || worst > 1e-6f)
                    problems.Add($"rebuilt {MergeShort(id)} is skinned differently ({now.Palette.Length} vs {before.Palette.Length} slots, worst {worst:E1})");
                if (!now.Materials.SequenceEqual(before.Materials)) problems.Add($"rebuilt {MergeShort(id)} wears other materials than before the merge");
                if (!now.Offsets.SequenceEqual(before.Offsets)) problems.Add($"rebuilt {MergeShort(id)} sorts at other offsets than before the merge");
                if (now.Splits != before.Splits) problems.Add($"rebuilt {MergeShort(id)} has {now.Splits} split surface(s), before {before.Splits}");
                if (!mi.Visible || mi.Skeleton.IsEmpty) problems.Add($"rebuilt {MergeShort(id)} is not drawn on its own instance");
            }
            CheckNoMerge("after switching off");
            CheckParkedClean(p, "after the un-merge");

            // --- 2. on: everything merges again ---------------------------------------------------------------
            RenderConfig.OutfitMerge = true;
            if (!WaitAll(() => AllMerged(a))) problems.Add("switching on again did not merge every item of A within 10 s");

            // --- 3. off and straight back on while the un-merge job is in flight ------------------------------
            RenderConfig.OutfitMerge = false;
            TickOutfitMerge();
            if (a.OutfitMerge is not { JobInFlight: true }) problems.Add("switching off started no un-merge job");
            RenderConfig.OutfitMerge = true;
            TickOutfitMerge();
            if (!WaitAll(() => AllMerged(a))) problems.Add("switching on during the un-merge did not end with every item merged");

            // --- 4. a new version rigged while off (no arrays kept), then on before its bucket let it go -------
            RenderConfig.OutfitMerge = false;
            TickOutfitMerge();
            var facesC2 = new[] { Face(1f), Face(0.5f) };
            CommitSelfTestRig(a, aC, meshC, facesC2, keepTangents: false);
            if (!_mergeItems.TryGetValue(aC, out var cItem) || !cItem.InBucket || !cItem.Held)
                problems.Add("C's version rigged while off is not held while its bucket draws the old one");
            RenderConfig.OutfitMerge = true;
            TickOutfitMerge();
            if (!WaitAll(() => AllMerged(a) && _mergeItems.TryGetValue(aC, out var it) && it.Merged?.Faces == facesC2))
                problems.Add("C's version rigged while the merge was off did not end merged after switching on");

            // --- 5. off at the end -------------------------------------------------------------------------
            RenderConfig.OutfitMerge = false;
            if (!WaitAll(() => a.OutfitMerge == null && worn[a].All(id => _riggedAttachments.ContainsKey(id))))
                problems.Add("the final switch-off did not un-merge A within 10 s");
            foreach (var id in worn[a])
                if (!_riggedAttachments.TryGetValue(id, out var mi) || !mi.Visible || mi.Skeleton.IsEmpty)
                    problems.Add($"after the final switch-off {MergeShort(id)} is not drawn on its own instance");
            CheckNoMerge("after the final switch-off");
            CheckParkedClean(p, "at the end");
        }
        catch (Exception ex)
        {
            problems.Add($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            RenderConfig.OutfitMerge = false;
            WaitRigWorkersIdle();
            DrainQueue();
            foreach (var visual in visuals)
            {
                UnloadOutfitMerge(visual);
                ReleaseSelfTestWearer(visual, worn.GetValueOrDefault(visual) ?? new List<Guid>());
            }
            _mergeItems.Clear();
            _mergeHeld.Clear();
            _mergeTouched.Clear();
            RenderConfig.OutfitMerge = priorSetting;
            _outfitMergeApplied = priorApplied;
            _world = priorWorld;
            _gpuCache = priorCache;
        }

        return problems.Count == 0
            ? (true, $"off: parked avatar unloaded at once, the full one's items rebuilt without a re-rig (skinned, " +
                     $"materials and split surfaces as before; {timing}); on re-merges all; on mid-un-merge and on after " +
                     "an item was rigged while off both end fully merged; off leaves no merged node")
            : (false, string.Join("; ", problems.Distinct()));

        void CheckNoMerge(string when)
        {
            foreach (var visual in visuals)
            {
                if (visual.Skeleton == null) continue;
                if (visual.Skeleton.GetChildren().OfType<MeshInstance3D>().Any(n => n.Name.ToString().StartsWith(OutfitBucketName, StringComparison.Ordinal)
                                                                                    || n.Name.ToString().StartsWith(OutfitSortedName, StringComparison.Ordinal)))
                    problems.Add($"{when}: merged instances are left in the tree");
                if (visual.OutfitMerge != null) problems.Add($"{when}: an avatar keeps its merge state");
            }
            if (_mergeItems.Count != 0 || _mergeHeld.Count != 0)
                problems.Add($"{when}: {_mergeItems.Count} merge item(s) / {_mergeHeld.Count} held instance(s) left");
        }

        void CheckParkedClean(AvatarVisual parked, string when)
        {
            if (parked.OutfitMerge != null) problems.Add($"{when}: the parked avatar keeps its merge state");
            if (parked.Skeleton != null && parked.Skeleton.GetChildren().OfType<MeshInstance3D>().Any(n =>
                    IsInstanceValid(n) && (n.Name.ToString().StartsWith(OutfitBucketName, StringComparison.Ordinal)
                                           || n.Name.ToString().StartsWith(OutfitSortedName, StringComparison.Ordinal))))
                problems.Add($"{when}: the parked avatar has merged instances in its skeleton");
            if (VisibleSkinnedChildren(parked) > 0) problems.Add($"{when}: the parked avatar draws a worn instance");
        }

        (Transform3D[] Palette, Material?[] Materials, float[] Offsets, int Splits) Snapshot(AvatarVisual visual, MeshInstance3D mi)
        {
            var mesh = (ArrayMesh)mi.Mesh;
            var materials = new Material?[mesh.GetSurfaceCount()];
            var offsets = new float[mesh.GetSurfaceCount()];
            int splits = 0;
            for (int s = 0; s < materials.Length; s++)
            {
                var split = mi.GetNodeOrNull<MeshInstance3D>(SortedSurfaceName(s));
                if (split != null) splits++;
                materials[s] = split != null ? split.GetSurfaceOverrideMaterial(0) : mi.GetSurfaceOverrideMaterial(s);
                offsets[s] = split?.SortingOffset ?? mi.SortingOffset;
            }
            return (Palette(visual.Skeleton!, mi.Skin), materials, offsets, splits);
        }
    }

    /// <summary>The merge tick over every avatar with its dirty buckets due now, pumping the queue, until
    /// <paramref name="done"/> holds and nothing is in flight (10 s at most).</summary>
    private bool WaitAll(Func<bool> done, Action? eachRound = null)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 10_000)
        {
            foreach (var visual in _visuals.Values)
                if (visual.OutfitMerge is { } state)
                    foreach (var bucket in state.Buckets.Values)
                        if (bucket.IsDirty) bucket.Immediate = true;
            TickOutfitMerge();
            eachRound?.Invoke();
            DrainQueue();
            TickWornAlphaOrder();
            bool idle = _mergeJobsRunning == 0 && _pendingRigs.IsEmpty && _activeRigWorkers == 0
                        && _mergeItems.Values.All(i => !i.Retaining);
            bool settled = _visuals.Values.All(v => v.OutfitMerge == null || v.OutfitMerge.Buckets.Values.All(b => !b.IsDirty));
            if (idle && settled && done()) return true;
            Thread.Sleep(5);
        }
        return false;
    }
}
