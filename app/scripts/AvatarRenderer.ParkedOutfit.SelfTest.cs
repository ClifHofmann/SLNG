using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Godot;
using SLNG.Assets;
using SLNG.Core;
using SLNG.Core.ECS;

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
}
