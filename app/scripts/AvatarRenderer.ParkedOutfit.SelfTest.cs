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
using SLNG.Net;

namespace SLNG.App;

public partial class AvatarRenderer
{
    /// <summary>
    /// FEAT-PERF-23 fix, setting OFF: "parked avatars draw no outfit and the cap holds". Four remote avatars on
    /// the real Bento skeleton, each wearing two rigged items through the real commit path, cap 2. The cap is
    /// evaluated while every item's material pass is still queued -- the state a busy sim is in for seconds --
    /// and only then does the queue run. Checks: exactly the two nearest are full; every parked avatar's worn
    /// instances (and their split children) are hidden and detached from the skeleton although their material
    /// pass ended after the park; the full ones draw theirs. Then a promotion and a demotion swap the two
    /// states over, and a late material pass on a parked item (a re-texture) still leaves it hidden.
    /// </summary>
    internal (bool Passed, string Detail) SelfTestParkedOutfitHidden()
    {
        LoadAvatarSkeleton();
        if (_avatarSkeleton == null) return (false, "the avatar skeleton did not load");

        var problems = new List<string>();
        var priorWorld = _world;
        var priorCache = _gpuCache;
        bool priorSetting = RenderConfig.OutfitMerge;
        bool priorApplied = _outfitMergeApplied;
        int priorCap = RenderConfig.MaxFullyRenderedAvatars;
        _world = new World();
        _gpuCache = new GpuCache(64L << 20);
        var visuals = new List<AvatarVisual>();
        var worn = new Dictionary<AvatarVisual, List<Guid>>();

        try
        {
            RenderConfig.OutfitMerge = false;
            _outfitMergeApplied = false;
            RenderConfig.MaxFullyRenderedAvatars = 2;

            FaceTexture Face(float alpha) =>
                new(Guid.NewGuid(), Guid.Empty, Guid.Empty, new System.Numerics.Vector4(1f, 1f, 1f, alpha), 1f, 1f, 0f, 0f, 0f);
            var ibmTorso = System.Numerics.Matrix4x4.CreateTranslation(0f, 0f, -1.2f);
            var ibmChest = System.Numerics.Matrix4x4.CreateTranslation(0.01f, 0f, -1.35f);
            var body = TestMesh(new[] { "mPelvis", "mTorso", "mChest" }, new[] { System.Numerics.Matrix4x4.Identity, ibmTorso, ibmChest }, faces: 2, x: -0.2f);
            var hair = TestMesh(new[] { "mChest", "mTorso" }, new[] { ibmChest, ibmTorso }, faces: 3, x: 0f);

            for (int n = 0; n < 4; n++)
            {
                var visual = NewSelfTestWearer(Guid.NewGuid(), Guid.NewGuid());
                visual.GodotPos = new Godot.Vector3(10f + n, 0f, 0f); // well past the 2 m incumbent margin
                visuals.Add(visual);
                worn[visual] = new List<Guid>();
                // A body (opaque faces) and a hair with two blended faces (split onto their own instances).
                foreach (var (mesh, faces) in new[] { (body, new[] { Face(1f), Face(1f) }), (hair, new[] { Face(1f), Face(0.5f), Face(0.5f) }) })
                {
                    var id = Guid.NewGuid();
                    worn[visual].Add(id);
                    CommitSelfTestRig(visual, id, mesh, faces, keepTangents: false);
                }
            }

            // The cap, with every material pass still waiting in the queue.
            _avatarLimitIgnoreDwell = true;
            EvaluateAvatarLimit(Godot.Vector3.Zero);
            PumpUntil(() => visuals.All(v => worn[v].All(id => !_riggedAttachments.TryGetValue(id, out var mi) || AllMaterialsIn(mi))));
            DrainQueue();

            CheckCap(visuals, expectedFull: new[] { visuals[0], visuals[1] }, "after the first evaluation");
            CheckOutfits("after the late material passes");

            // A promotion and a demotion: the farthest comes nearest.
            visuals[3].GodotPos = new Godot.Vector3(1f, 0f, 0f);
            _avatarLimitIgnoreDwell = true;
            EvaluateAvatarLimit(Godot.Vector3.Zero);
            DrainQueue();
            CheckCap(visuals, expectedFull: new[] { visuals[3], visuals[0] }, "after the swap");
            if (visuals[1].IsReduced && !visuals[1].IsParked) problems.Add("the demoted avatar was not parked");
            CheckOutfits("after the swap");

            // A material pass that ends on a parked item (a re-texture or bake while parked).
            var parked = visuals.First(v => v.IsReduced);
            var parkedItem = worn[parked].Select(id => _riggedAttachments.GetValueOrDefault(id)).First(mi => mi != null)!;
            var faces2 = new[] { Face(1f), Face(1f) };
            _ = ApplyFaceMaterialsAsync(parkedItem, new[] { 0, 1 }, faces2, default, parked, Guid.NewGuid(), Guid.Empty);
            PumpUntil(() => MainThreadWorkQueue.Depth == 0);
            DrainQueue();
            CheckOutfits("after a material pass on a parked item");
        }
        catch (Exception ex)
        {
            problems.Add($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _avatarLimitIgnoreDwell = false;
            _avatarLimitTransitions = 0;
            WaitRigWorkersIdle();
            DrainQueue();
            foreach (var visual in visuals) ReleaseSelfTestWearer(visual, worn.GetValueOrDefault(visual) ?? new List<Guid>());
            RenderConfig.OutfitMerge = priorSetting;
            _outfitMergeApplied = priorApplied;
            RenderConfig.MaxFullyRenderedAvatars = priorCap;
            _world = priorWorld;
            _gpuCache = priorCache;
        }

        return problems.Count == 0
            ? (true, "merge off, cap 2 of 4: the two nearest full, both parked avatars draw no worn instance (also after material " +
                     "passes that ended after the park), a promotion/demotion swaps that over")
            : (false, string.Join("; ", problems.Distinct()));

        void CheckCap(List<AvatarVisual> all, AvatarVisual[] expectedFull, string when)
        {
            var full = all.Where(v => !v.IsReduced).ToList();
            if (full.Count != RenderConfig.MaxFullyRenderedAvatars)
                problems.Add($"{when}: {full.Count} full avatars, the cap is {RenderConfig.MaxFullyRenderedAvatars}");
            foreach (var v in expectedFull)
                if (v.IsReduced) problems.Add($"{when}: an avatar at {v.GodotPos.X:0.0} m is reduced although it is among the nearest");
        }

        void CheckOutfits(string when)
        {
            foreach (var visual in visuals)
            {
                foreach (var id in worn[visual])
                {
                    if (!_riggedAttachments.TryGetValue(id, out var mi) || !IsInstanceValid(mi))
                    {
                        problems.Add($"{when}: an item lost its instance");
                        continue;
                    }
                    var nodes = new[] { mi }.Concat(mi.GetChildren().OfType<MeshInstance3D>()).ToList();
                    if (visual.IsReduced)
                    {
                        if (mi.Visible) problems.Add($"{when}: a parked avatar's worn instance is drawn");
                        if (nodes.Any(n => n.Skin != null && !n.Skeleton.IsEmpty)) problems.Add($"{when}: a parked avatar's worn instance is bound to its skeleton");
                    }
                    else
                    {
                        if (!mi.Visible) problems.Add($"{when}: a full avatar's worn instance is hidden");
                        if (nodes.Any(n => n.Skin != null && n.Skeleton.IsEmpty)) problems.Add($"{when}: a full avatar's worn instance is not bound");
                    }
                }
                if (visual.IsReduced && VisibleSkinnedChildren(visual) > 0)
                    problems.Add($"{when}: a parked avatar's skeleton still has drawn skinned instances");
            }
        }
    }

    /// <summary>A remote avatar on the real Bento skeleton, in <see cref="_visuals"/>, with no body parts.</summary>
    private AvatarVisual NewSelfTestWearer(Guid entityId, Guid agentId)
    {
        var visual = new AvatarVisual { AgentId = agentId, EntityId = entityId, Shown = true };
        AddChild(visual.Root);
        var skeleton = SkeletonBuilder.Build(_avatarSkeleton!);
        skeleton.Name = "Skeleton3D";
        visual.Root.AddChild(skeleton);
        visual.Skeleton = skeleton;
        ApplyShape(visual, skeleton, _avatarSkeleton!, visual.LastDistortions);
        skeleton.ResetBonePoses();
        _visuals[entityId] = visual;
        return visual;
    }

    private void ReleaseSelfTestWearer(AvatarVisual visual, List<Guid> worn)
    {
        foreach (var id in worn)
        {
            _pendingRigs.TryRemove(id, out _);
            _preparedRigs.TryRemove(id, out _);
            _riggedAttachments.Remove(id);
            _attachmentToAvatar.Remove(id);
            _attachmentMeshIds.Remove(id);
            _mergeItems.Remove(id);
        }
        _visuals.Remove(visual.EntityId);
        if (IsInstanceValid(visual.Root))
        {
            visual.Root.GetParent()?.RemoveChild(visual.Root);
            visual.Root.Free();
        }
    }

    /// <summary>A worn rigged item through the real commit path (<see cref="CommitPreparedRig"/>), prepared here
    /// instead of on a rig worker; <paramref name="keepTangents"/> is what the worker decides from the setting.</summary>
    private void CommitSelfTestRig(AvatarVisual visual, Guid id, MeshData data, FaceTexture[] faces, bool keepTangents)
    {
        var meshId = Guid.NewGuid();
        _attachmentToAvatar[id] = visual.EntityId;
        visual.WornAttachmentEntities.Add(id);
        _attachmentMeshIds[id] = (meshId, faces, default, visual.EntityId, MeshDetailLevel.High);
        visual.WornAttachmentFaces[id] = (faces, default);
        var req = new PendingRig(data, visual, visual.Skeleton!, meshId, faces, default, _avatarSkeleton, false);
        var definition = _avatarSkeleton!;
        var slots = RiggedMeshBuilder.SlotsForJoints(data.Skin!.JointNames,
            n => definition.GetBone(definition.ResolveBoneName(n)) != null);
        var prepared = PrepareRiggedMesh(data, slots, faces, default, wantPickChunks: false, keepTangents: keepTangents);
        _pendingRigs[id] = req;
        _preparedRigs[id] = new PreparedRig(req, prepared, 0);
        CommitPreparedRig(id);
    }

    /// <summary>Skinned instances under the avatar's skeleton (and their split children) that are drawn or still
    /// bound -- what a parked avatar must have none of. System parts are not counted.</summary>
    private static int VisibleSkinnedChildren(AvatarVisual visual)
    {
        if (visual.Skeleton == null || !IsInstanceValid(visual.Skeleton)) return 0;
        var parts = new HashSet<MeshInstance3D>(visual.Parts.Values.OfType<MeshInstance3D>());
        int n = 0;
        foreach (var node in visual.Skeleton.GetChildren())
        {
            if (node is not MeshInstance3D mi || mi.Skin == null || parts.Contains(mi) || mi.IsQueuedForDeletion()) continue;
            if (mi.Visible || !mi.Skeleton.IsEmpty) n++;
            foreach (var child in mi.GetChildren())
                if (child is MeshInstance3D split && split.Skin != null && !split.Skeleton.IsEmpty) n++;
        }
        return n;
    }

    /// <summary>Pumps the queue until <paramref name="done"/> holds (5 s at most).</summary>
    private static void PumpUntil(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < 5_000)
        {
            DrainQueue();
            Thread.Sleep(2);
        }
    }

    /// <summary>
    /// BUG-AVATAR-23: When an avatar is parked mid-build (items in _pendingRigs with BoneAttachment3D
    /// anchors created), on promotion from parked all unfinished items must be re-requested and finish building,
    /// while items that were already finished must NOT be rebuilt (preserving BUG-PERF-16 instant unpark).
    /// Covers 5 cases through the real promotion path (waiting for deferred calls):
    /// (a) pending rig interrupted: ends up built;
    /// (b) built item: same instance, not rebuilt;
    /// (c) built item with a pending newer LOD interrupted: the newer one ends up built;
    /// (d) worn animesh part: not rebuilt;
    /// (e) failed mesh: not requested again.
    /// </summary>
    internal async System.Threading.Tasks.Task<(bool Passed, string Detail)> SelfTestParkedRigsRecovered(SceneTree tree)
    {
        LoadAvatarSkeleton();
        if (_avatarSkeleton == null) return (false, "the avatar skeleton did not load");

        var problems = new List<string>();
        var priorWorld = _world;
        var priorCache = _gpuCache;
        var priorAssetService = _assetService;
        bool priorSetting = RenderConfig.OutfitMerge;
        bool priorApplied = _outfitMergeApplied;

        string scratchDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "slng_selftest_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(scratchDir);

        _world = new World();
        _gpuCache = new GpuCache(64L << 20);
        _assetService = new AssetService(new GridSession(), scratchDir);

        var worn = new List<Guid>();
        AvatarVisual? visual = null;

        try
        {
            RenderConfig.OutfitMerge = false;
            _outfitMergeApplied = false;

            visual = NewSelfTestWearer(Guid.NewGuid(), Guid.NewGuid());

            var ibmTorso = System.Numerics.Matrix4x4.CreateTranslation(0f, 0f, -1.2f);
            var ibmChest = System.Numerics.Matrix4x4.CreateTranslation(0.01f, 0f, -1.35f);
            var bodyMesh = TestMesh(new[] { "mPelvis", "mTorso", "mChest" }, new[] { System.Numerics.Matrix4x4.Identity, ibmTorso, ibmChest }, faces: 2, x: -0.2f);
            var hairMesh = TestMesh(new[] { "mChest", "mTorso" }, new[] { ibmChest, ibmTorso }, faces: 2, x: 0f);
            var newerHairMesh = TestMesh(new[] { "mChest", "mTorso" }, new[] { ibmChest, ibmTorso }, faces: 2, x: 0.1f);

            var builtMeshId = Guid.NewGuid();
            var pendingMeshId = Guid.NewGuid();
            var newerHairMeshId = Guid.NewGuid();
            var failedMeshId = Guid.NewGuid(); // deliberately not in cache -> GetMeshAsync returns null

            _assetService.CacheMeshInMemory(builtMeshId, bodyMesh);
            _assetService.CacheMeshInMemory(pendingMeshId, hairMesh);
            _assetService.CacheMeshInMemory(newerHairMeshId, newerHairMesh);

            var face = new FaceTexture(Guid.NewGuid(), Guid.Empty, Guid.Empty, new System.Numerics.Vector4(1f, 1f, 1f, 1f), 1f, 1f, 0f, 0f, 0f);
            const ulong TestRegion = 0xFEEDBEEF00000001UL;

            // (b) Item 1: Already built item
            var e1 = _world.GetOrCreateEntity(TestRegion, 1001);
            e1.SetComponent(new AttachmentComponent(visual.EntityId, 1));
            e1.SetComponent(new PrimitiveComponent(System.Numerics.Vector3.One, 0, isMesh: true, meshId: builtMeshId));
            e1.SetComponent(new TransformComponent());
            var builtId = e1.Id;
            worn.Add(builtId);

            CommitSelfTestRig(visual, builtId, bodyMesh, new[] { face, face }, keepTangents: false);

            var builtAnchor = new BoneAttachment3D { Name = $"WornItem_{builtId:N}", BoneName = "mChest" };
            visual.Skeleton!.AddChild(builtAnchor);
            _attachmentNodes[builtId] = builtAnchor;
            builtAnchor.AddChild(new Node3D { Name = "PointOffset" });

            if (!_riggedAttachments.TryGetValue(builtId, out var originalBuiltMi))
                return (false, "item 1 was not rigged");

            // (a) Item 2: Pending rig interrupted mid-build
            var e2 = _world.GetOrCreateEntity(TestRegion, 1002);
            e2.SetComponent(new AttachmentComponent(visual.EntityId, 2));
            e2.SetComponent(new PrimitiveComponent(System.Numerics.Vector3.One, 0, isMesh: true, meshId: pendingMeshId));
            e2.SetComponent(new TransformComponent());
            var pendingId = e2.Id;
            worn.Add(pendingId);

            visual.WornAttachmentEntities.Add(pendingId);
            _attachmentToAvatar[pendingId] = visual.EntityId;
            var pendingAnchor = new BoneAttachment3D { Name = $"WornItem_{pendingId:N}", BoneName = "mHead" };
            visual.Skeleton.AddChild(pendingAnchor);
            _attachmentNodes[pendingId] = pendingAnchor;
            pendingAnchor.AddChild(new Node3D { Name = "PointOffset" });

            _attachmentMeshIds[pendingId] = (pendingMeshId, new[] { face, face }, default, visual.EntityId, MeshDetailLevel.High);
            visual.WornBuildsInProgress.Add(pendingId);
            var pendingReq = new PendingRig(hairMesh, visual, visual.Skeleton, pendingMeshId, new[] { face, face }, default, _avatarSkeleton, false);
            _pendingRigs[pendingId] = pendingReq;
            visual.HasLoadedOutfit = true;

            // (c) Item 3: Built item with a pending newer LOD interrupted
            var e3 = _world.GetOrCreateEntity(TestRegion, 1003);
            e3.SetComponent(new AttachmentComponent(visual.EntityId, 3));
            e3.SetComponent(new PrimitiveComponent(System.Numerics.Vector3.One, 0, isMesh: true, meshId: newerHairMeshId));
            e3.SetComponent(new TransformComponent());
            var lodId = e3.Id;
            worn.Add(lodId);

            CommitSelfTestRig(visual, lodId, hairMesh, new[] { face, face }, keepTangents: false);
            var lodAnchor = new BoneAttachment3D { Name = $"WornItem_{lodId:N}", BoneName = "mChest" };
            visual.Skeleton.AddChild(lodAnchor);
            _attachmentNodes[lodId] = lodAnchor;
            lodAnchor.AddChild(new Node3D { Name = "PointOffset" });

            // Older rig is committed, but a newer LOD request was started before park
            _attachmentMeshIds[lodId] = (newerHairMeshId, new[] { face, face }, default, visual.EntityId, MeshDetailLevel.Highest);
            visual.WornBuildsInProgress.Add(lodId);
            _pendingRigs[lodId] = new PendingRig(newerHairMesh, visual, visual.Skeleton, newerHairMeshId, new[] { face, face }, default, _avatarSkeleton, false);

            // (d) Item 4: Worn animesh part routed to control avatar (not in WornBuildsInProgress)
            var e4 = _world.GetOrCreateEntity(TestRegion, 1004);
            e4.SetComponent(new AttachmentComponent(visual.EntityId, 4));
            e4.SetComponent(new PrimitiveComponent(System.Numerics.Vector3.One, 0, isMesh: true, meshId: Guid.NewGuid()));
            e4.SetComponent(new TransformComponent());
            var animeshId = e4.Id;
            worn.Add(animeshId);

            visual.WornAttachmentEntities.Add(animeshId);
            _attachmentToAvatar[animeshId] = visual.EntityId;
            var animeshAnchor = new BoneAttachment3D { Name = $"WornItem_{animeshId:N}", BoneName = "mChest" };
            visual.Skeleton.AddChild(animeshAnchor);
            _attachmentNodes[animeshId] = animeshAnchor;
            visual.WornBuildsInProgress.Remove(animeshId);

            // (e) Item 5: Failed mesh fetch (not in cache, removed from WornBuildsInProgress)
            var e5 = _world.GetOrCreateEntity(TestRegion, 1005);
            e5.SetComponent(new AttachmentComponent(visual.EntityId, 5));
            e5.SetComponent(new PrimitiveComponent(System.Numerics.Vector3.One, 0, isMesh: true, meshId: failedMeshId));
            e5.SetComponent(new TransformComponent());
            var failedId = e5.Id;
            worn.Add(failedId);

            visual.WornAttachmentEntities.Add(failedId);
            _attachmentToAvatar[failedId] = visual.EntityId;
            var failedAnchor = new BoneAttachment3D { Name = $"WornItem_{failedId:N}", BoneName = "mChest" };
            visual.Skeleton.AddChild(failedAnchor);
            _attachmentNodes[failedId] = failedAnchor;
            visual.WornBuildsInProgress.Remove(failedId);
            _attachmentMeshIds[failedId] = (failedMeshId, new[] { face, face }, default, visual.EntityId, MeshDetailLevel.High);

            // Park avatar (mid-build swap-out by cap)
            SetAvatarReduced(visual);

            if (!visual.IsParked) problems.Add("avatar was not parked");
            if (_pendingRigs.ContainsKey(pendingId)) problems.Add("pending rig was not purged on park");
            if (_pendingRigs.ContainsKey(lodId)) problems.Add("newer LOD pending rig was not purged on park");
            if (originalBuiltMi.Visible) problems.Add("built rig was not hidden on park");

            // Promote avatar back to full (unpark)
            SetAvatarFull(visual);

            if (visual.IsParked) problems.Add("avatar was not unparked");
            if (visual.IsReduced) problems.Add("avatar is still reduced after promotion");

            // Wait for deferred CallDeferred(nameof(UpdateAttachment)) through real engine frame
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

            PumpUntil(() => _riggedAttachments.ContainsKey(pendingId));
            DrainQueue();

            // (a) Verify pending rig interrupted mid-build: ends up built and visible
            if (!_riggedAttachments.TryGetValue(pendingId, out var newPendingMi) || !IsInstanceValid(newPendingMi))
            {
                problems.Add("(a) pending rig was not built after unpark");
            }
            else if (!newPendingMi.Visible)
            {
                problems.Add("(a) recovered pending rig is not visible");
            }

            // (b) Verify built item: same instance, not rebuilt
            if (!_riggedAttachments.TryGetValue(builtId, out var currentBuiltMi))
            {
                problems.Add("(b) built item lost its instance");
            }
            else if (!ReferenceEquals(currentBuiltMi, originalBuiltMi))
            {
                problems.Add("(b) built item was rebuilt instead of instantaneous unpark");
            }
            else if (!currentBuiltMi.Visible)
            {
                problems.Add("(b) built item was not made visible on unpark");
            }

            // (c) Verify built item with a pending newer LOD interrupted: the newer one ends up built
            if (!_riggedAttachments.TryGetValue(lodId, out var currentLodMi) || !IsInstanceValid(currentLodMi))
            {
                problems.Add("(c) newer LOD item lost its instance");
            }
            else if (!currentLodMi.Visible)
            {
                problems.Add("(c) newer LOD item is not visible");
            }
            else if (!visual.RiggedAttachments.Any(r => r.Mi == currentLodMi && r.MeshId == newerHairMeshId))
            {
                problems.Add("(c) newer LOD was not built for item");
            }

            // (d) Verify worn animesh part: not rebuilt
            if (_riggedAttachments.ContainsKey(animeshId))
            {
                problems.Add("(d) worn animesh part erroneously rigged on wearer");
            }

            // (e) Verify failed mesh: not requested again (remains in dedupe, not built)
            if (_riggedAttachments.ContainsKey(failedId))
            {
                problems.Add("(e) failed mesh item was erroneously rigged");
            }
            if (!_attachmentMeshIds.ContainsKey(failedId))
            {
                problems.Add("(e) failed mesh dedupe record was erroneously cleared for re-request");
            }
        }
        catch (Exception ex)
        {
            problems.Add($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            WaitRigWorkersIdle();
            DrainQueue();
            if (visual != null) ReleaseSelfTestWearer(visual, worn);
            RenderConfig.OutfitMerge = priorSetting;
            _outfitMergeApplied = priorApplied;
            _world = priorWorld;
            _gpuCache = priorCache;
            _assetService = priorAssetService;
            try
            {
                if (System.IO.Directory.Exists(scratchDir))
                    System.IO.Directory.Delete(scratchDir, recursive: true);
            }
            catch { }
        }

        return problems.Count == 0
            ? (true, "parked rigs recovered through real promotion: interrupted rig built, built item untouched, newer LOD built, animesh and failed mesh not rebuilt")
            : (false, string.Join("; ", problems.Distinct()));
    }
}
