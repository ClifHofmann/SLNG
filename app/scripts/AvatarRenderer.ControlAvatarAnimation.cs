using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SLNG.Assets;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.App;

// FEAT-ANIMESH-02 -- a control avatar plays what its object's scripts started.
//
// An animesh moves only when a script calls llStartObjectAnimation; the simulator then sends an
// ObjectAnimation message for the prim that holds the script, and SLNG carries it to the world as
// PrimitiveComponent.SignaledAnimations (WorldSimulation.ApplyObjectAnimation). The viewer plays
// the UNION of those lists over the linkset root and every child prim, on the object's own
// skeleton, with no default motions at all -- nothing signalled means the rest pose
// (LLControlAvatar::updateAnimations, llcontrolavatar.cpp:559-607; mEnableDefaultMotions=false,
// :56). The arithmetic is ControlAvatarAnimations (Core, unit-tested); this file is the Godot half:
// when to recompute, fetching the assets, handing the set to the AvatarAnimationPlayer that every
// AvatarVisual already carries, and moving it on each frame.
//
// Everything here runs on the main thread. The only thing that leaves it is the asset fetch, and a
// fetch only ever fills a cache: what is PLAYED is always derived from the current signalled set
// and that cache, never from what a fetch captured when it started, which is what makes "latest
// wins" true by construction instead of by a staleness check somebody has to remember.

public partial class AvatarRenderer
{
    /// <summary>The prims linked directly under a root: <c>WorldSimulation.ChildrenOf</c>, which
    /// is the parent-to-children index the world already keeps for re-composing a linkset. Set by
    /// Boot. Without it only the root's own list is read.</summary>
    public Func<ulong, uint, IReadOnlyCollection<Guid>>? LinksetChildren { get; set; }

    /// <summary>Replaces <c>AssetService.GetAnimationAsync</c>; for the self-test, which has no
    /// asset service and no grid.</summary>
    internal Func<Guid, Task<AnimationData?>>? AnimationLoaderOverride { get; set; }

    // Reused by every recompute so gathering a linkset's lists allocates no list of its own.
    private readonly List<IReadOnlyList<SignaledAnimation>> _linksetLists = new();

    /// <summary>A prim of this object's linkset has a new animation list, joined or left the
    /// linkset, or went away: the union has to be read again. Cheap -- a flag, acted on once on the
    /// next frame whatever number of prims said so. Nothing happens for an object without a
    /// control avatar; its first build reads everything anyway.</summary>
    public void MarkControlAvatarAnimationsDirty(Guid rootEntityId)
    {
        if (_controlAvatars.TryGetValue(rootEntityId, out var ca)) ca.AnimationsDirty = true;
    }

    // ---- recompute ------------------------------------------------------------------------------

    /// <summary>Reads the lists of the root and every child prim from the world and makes the
    /// control avatar play their union.</summary>
    private void RecomputeControlAvatarAnimations(ControlAvatar ca, Entity root)
    {
        _linksetLists.Clear();
        if (root.GetComponent<PrimitiveComponent>() is { } rootPrim) _linksetLists.Add(rootPrim.SignaledAnimations);

        var children = LinksetChildren?.Invoke(root.RegionHandle, root.LocalId);
        if (children != null && _world != null)
        {
            foreach (var childId in children)
            {
                if (childId == root.Id) continue;
                // Every child counts, rigged or not: the script that starts the animation may sit
                // in a plain prim. An id the world no longer knows (a removed child) is skipped.
                if (_world.GetEntity(childId)?.GetComponent<PrimitiveComponent>() is { } child)
                    _linksetLists.Add(child.SignaledAnimations);
            }
        }

        var wanted = ControlAvatarAnimations.Union(_linksetLists);
        _linksetLists.Clear();
        SetControlAvatarSignaledAnimations(ca, wanted);
    }

    /// <summary>Takes a new signalled set. Nothing at all happens when it is the one already held --
    /// the sim repeats a prim's list for any change to its animation state, and a recompute that
    /// finds the union unchanged must not touch what is playing.</summary>
    private void SetControlAvatarSignaledAnimations(ControlAvatar ca, SignaledAnimation[] wanted)
    {
        var change = ControlAvatarAnimations.Diff(ca.Signaled, wanted);
        if (change.IsEmpty) return;

        ca.Signaled = wanted;
        if (Diagnostics.Enabled) ReportAnimationSet(ca, change);

        FetchControlAvatarAnimations(ca);
        // What is already here is applied at once (a stop, a restart); what is not follows when
        // its asset arrives.
        ApplyControlAvatarAnimations(ca);
    }

    private static string Short(Guid id) => id.ToString("N")[..8];
    private static string Short(SignaledAnimation a) => $"{Short(a.AnimationId)}:{a.SequenceId}";

    /// <summary>Where the control avatar's skeleton root is, in SL region axes (x, y, z up) -- the
    /// root prim's position plus the pelvis fixup, which is what the skeleton was placed at. The
    /// number to compare with the floor height a viewer shows.</summary>
    private System.Numerics.Vector3 ControlAvatarRootSl(ControlAvatar ca)
    {
        ulong region = _world?.GetEntity(ca.RootEntityId)?.RegionHandle ?? 0;
        return RenderConfig.FromGodot(region, ca.Visual.Root.Position);
    }

    private static string Fmt(System.Numerics.Vector3 v) => $"({v.X:0.###}, {v.Y:0.###}, {v.Z:0.###})";

    /// <summary>The pelvis bone's CURRENT local position and its rest position, both relative to the
    /// skeleton root and in SL axes (Godot (x, y, z) is SL (x, -z, y)). The first is where the
    /// animation (or the rest) has put it; the second what the mesh's own override says.</summary>
    private static string PelvisReading(ControlAvatar ca)
    {
        var skeleton = ca.Visual.Skeleton;
        int pelvis = skeleton?.FindBone("mPelvis") ?? -1;
        if (skeleton == null || pelvis < 0) return "pelvis=n/a";

        var pose = skeleton.GetBonePosePosition(pelvis);
        var rest = skeleton.GetBoneRest(pelvis).Origin;
        return $"pelvis={Fmt(new System.Numerics.Vector3(pose.X, -pose.Z, pose.Y))} " +
               $"pelvisRest={Fmt(new System.Numerics.Vector3(rest.X, -rest.Z, rest.Y))}";
    }

    /// <summary>The one <c>[Animesh] anim</c> line per change of the effective set (--diag): the whole
    /// set, what the change started, stopped and restarted, and where the pelvis and the skeleton root
    /// are. The pelvis reading is taken as the change arrives, so it is where the PREVIOUS set left it;
    /// the <c>[Animesh] pose</c> line that follows once the new set has been applied and moved one
    /// frame shows where the new one put it.</summary>
    private void ReportAnimationSet(ControlAvatar ca, AnimationSetChange change)
    {
        GD.Print($"[Animesh] anim root={Short(ca.RootEntityId)} set=[{string.Join(' ', ca.Signaled.Select(a => Short(a)))}] " +
                 $"started=[{string.Join(' ', change.Start.Select(a => Short(a)))}] " +
                 $"stopped=[{string.Join(' ', change.Stop.Select(Short))}] " +
                 $"restarted=[{string.Join(' ', change.Restart.Select(a => Short(a)))}] " +
                 $"{PelvisReading(ca)} rootWorld={Fmt(ControlAvatarRootSl(ca))}");
    }

    /// <summary>--diag: the pose after a change has been applied and the player has moved one frame.</summary>
    private void ReportAnimationPose(ControlAvatar ca)
    {
        GD.Print($"[Animesh] pose root={Short(ca.RootEntityId)} playing=[{string.Join(' ', ca.Playing.Select(a => Short(a)))}] " +
                 $"{PelvisReading(ca)} rootWorld={Fmt(ControlAvatarRootSl(ca))}");
    }

    // ---- fetching ---------------------------------------------------------------------------------

    private void FetchControlAvatarAnimations(ControlAvatar ca)
    {
        var loader = AnimationLoaderOverride;
        if (loader == null && _assetService != null)
        {
            var assets = _assetService;
            // Off the main thread from the first instruction: GetAnimationAsync does its cache and
            // file checks synchronously, before its first await, on whichever thread calls it.
            loader = id => Task.Run(() => assets.GetAnimationAsync(id));
        }
        if (loader == null) return;

        foreach (var signaled in ca.Signaled)
        {
            // Already here, or already on its way: one fetch per animation however often the set
            // changes while it is in flight.
            if (ca.Loaded.ContainsKey(signaled.AnimationId) || !ca.Fetching.Add(signaled.AnimationId)) continue;
            _ = FetchControlAvatarAnimationAsync(ca, signaled.AnimationId, loader);
        }
    }

    private async Task FetchControlAvatarAnimationAsync(ControlAvatar ca, Guid animId, Func<Guid, Task<AnimationData?>> loader)
    {
        AnimationData? data = null;
        string? failure = null;
        try
        {
            data = await loader(animId).ConfigureAwait(false);
            if (data == null) failure = "asset did not resolve";
        }
        catch (Exception ex)
        {
            // The same hole the avatar path had to close: an animation that does not play and
            // says nothing about why.
            failure = $"{ex.GetType().Name}: {ex.Message}";
        }

        // The fetch ends on a worker thread; everything it learned is applied on the main one.
        MainThreadWorkQueue.Enqueue(MainThreadWorkQueue.Lane.Visual,
            () => OnControlAvatarAnimationFetched(ca, animId, data, failure), label: "controlavatar.anim");
    }

    private void OnControlAvatarAnimationFetched(ControlAvatar ca, Guid animId, AnimationData? data, string? failure)
    {
        ca.Fetching.Remove(animId);

        // The object went away (or its control avatar was rebuilt) while this was in flight: there
        // is nothing to apply it to, and the visual it was fetched for may be freed.
        if (!IsControlAvatarCurrent(ca)) return;

        if (data == null) WarnAnimationUnavailable(animId, failure ?? "asset did not resolve");
        else ca.Loaded[animId] = data;

        // Whatever is signalled NOW, not what was signalled when this fetch started. A result for
        // an animation nobody wants any more only fills the cache, and is pruned there.
        ApplyControlAvatarAnimations(ca);
    }

    private bool IsControlAvatarCurrent(ControlAvatar ca) =>
        _controlAvatars.TryGetValue(ca.RootEntityId, out var current)
        && ReferenceEquals(current, ca)
        && IsInstanceValid(ca.Visual.Root);

    // ---- playing ------------------------------------------------------------------------------------

    /// <summary>Brings the player in line with the signalled set: the signalled animations whose
    /// assets have arrived play, the rest do not (yet), and the others stop. An animation that is
    /// already playing with the same sequence id is not touched.</summary>
    private void ApplyControlAvatarAnimations(ControlAvatar ca)
    {
        var entries = new List<(Guid id, AnimationData data)>(ca.Signaled.Length);
        var target = new List<SignaledAnimation>(ca.Signaled.Length);
        foreach (var signaled in ca.Signaled)
        {
            if (!ca.Loaded.TryGetValue(signaled.AnimationId, out var data)) continue;
            entries.Add((signaled.AnimationId, data));
            target.Add(signaled);
        }

        var change = ControlAvatarAnimations.Diff(ca.Playing, target);
        if (!change.IsEmpty)
        {
            var player = ca.Visual.AnimPlayer;

            // Keys on the animation id alone: stops what left the set, starts what is new, and
            // leaves everything already playing exactly where it is.
            player.SetActiveAnimations(entries);

            // A changed sequence id is a start request for an animation that is already playing.
            // The viewer sends it to LLMotionController::startMotion, which brings back to life
            // only a motion that has finished (llmotioncontroller.cpp:392-428); one that is
            // playing steadily "keeps playing" (:420-424), so a looping animation is NOT pulled
            // back to its first frame by a new sequence id. The player holds a finished one-shot on
            // its last pose, which is the state a restart has to undo.
            foreach (var restart in change.Restart)
            {
                if (player.HasFinished(restart.AnimationId)) player.Restart(restart.AnimationId);
            }

            ca.Playing = target.ToArray();
            if (Diagnostics.Enabled) ca.ReportPoseAfterAdvance = true;
        }

        // The cache holds what the set can still ask for and what is on its way, nothing else.
        if (ca.Loaded.Count > 0)
        {
            foreach (var id in ca.Loaded.Keys.ToList())
            {
                if (!ca.Fetching.Contains(id) && !ca.Signaled.Any(s => s.AnimationId == id)) ca.Loaded.Remove(id);
            }
        }
    }

    /// <summary>Moves the playing animations on by one frame -- and only when that can be seen. An
    /// object with nothing playing costs the one check; one none of whose meshes is visible (the cull
    /// sweep has pushed them out of draw distance) is not posed at all, and picks up from the same
    /// time when it comes back rather than catching up.</summary>
    private void AdvanceControlAvatarAnimations(ControlAvatar ca, float delta)
    {
        var player = ca.Visual.AnimPlayer;
        if (!player.IsPlaying) return;

        bool visible = false;
        foreach (var part in ca.Parts.Values)
        {
            if (!part.Visible) continue;
            visible = true;
            break;
        }
        if (!visible) return;

        player.Advance(delta);

        if (ca.ReportPoseAfterAdvance)
        {
            ca.ReportPoseAfterAdvance = false;
            ReportAnimationPose(ca);
        }
    }

    // ---- self-test ------------------------------------------------------------------------------------

    internal SignaledAnimation[] SelfTestSignaled(Guid rootId) =>
        _controlAvatars.TryGetValue(rootId, out var ca) ? ca.Signaled : Array.Empty<SignaledAnimation>();

    internal SignaledAnimation[] SelfTestPlaying(Guid rootId) =>
        _controlAvatars.TryGetValue(rootId, out var ca) ? ca.Playing : Array.Empty<SignaledAnimation>();

    internal float? SelfTestAnimationTime(Guid rootId, Guid animId) =>
        _controlAvatars.TryGetValue(rootId, out var ca) ? ca.Visual.AnimPlayer.GetAnimationTime(animId) : null;

    /// <summary>One frame of the per-frame pass, for a world-driven self-test that has no frames.</summary>
    internal void SelfTestTick(float delta) => UpdateControlAvatars(delta);

    /// <summary>Is the named bone at the pose this animation key sets? (SL key in, compared as the
    /// Godot rotation the player writes; q and -q are the same rotation.)</summary>
    internal static bool SelfTestAtKey(Skeleton3D skeleton, string bone, System.Numerics.Quaternion slKey)
    {
        int idx = skeleton.FindBone(bone);
        if (idx < 0) return false;
        var want = new Godot.Quaternion(slKey.X, slKey.Z, -slKey.Y, slKey.W).Normalized();
        var have = skeleton.GetBonePoseRotation(idx);
        return have.IsEqualApprox(want) || have.IsEqualApprox(-want);
    }

    /// <summary>Is the named bone at its rest rotation?</summary>
    internal static bool SelfTestAtRest(Skeleton3D skeleton, string bone)
    {
        int idx = skeleton.FindBone(bone);
        if (idx < 0) return false;
        var rest = skeleton.GetBoneRest(idx).Basis.GetRotationQuaternion();
        var have = skeleton.GetBonePoseRotation(idx);
        return have.IsEqualApprox(rest) || have.IsEqualApprox(-rest);
    }

    /// <summary>Is the named bone at its rest POSITION?</summary>
    internal static bool SelfTestAtRestPosition(Skeleton3D skeleton, string bone)
    {
        int idx = skeleton.FindBone(bone);
        return idx >= 0 && skeleton.GetBonePosePosition(idx).IsEqualApprox(skeleton.GetBoneRest(idx).Origin);
    }

    internal bool SelfTestBoneAtKey(Guid rootId, string bone, System.Numerics.Quaternion slKey) =>
        _controlAvatars.TryGetValue(rootId, out var ca) && ca.Visual.Skeleton is { } skeleton
        && SelfTestAtKey(skeleton, bone, slKey);

    internal bool SelfTestBoneAtRest(Guid rootId, string bone) =>
        _controlAvatars.TryGetValue(rootId, out var ca) && ca.Visual.Skeleton is { } skeleton
        && SelfTestAtRest(skeleton, bone);

    /// <summary>The skinned extent of a part of the synthetic bar in the skeleton's CURRENT pose, in
    /// world axes relative to the control avatar's root: the same product Godot skins with
    /// (global bone pose times bind), where <see cref="BuildRiggedMeshInstance"/> measures the rest
    /// pose from the global REST. At rest the two agree, which the caller can check.</summary>
    private (Godot.Vector3 Min, Godot.Vector3 Max) SelfTestPosedExtent(ControlAvatar ca, Guid primId,
        System.Numerics.Vector3[] corners, System.Numerics.Matrix4x4 bindShape)
    {
        var skeleton = ca.Visual.Skeleton!;
        var skin = ca.Parts[primId].Mi!.Skin!;
        var frame = new Basis(ca.Visual.Root.Quaternion);
        var palette = skeleton.GetBoneGlobalPose(skin.GetBindBone(0)) * skin.GetBindPose(0);

        var min = new Godot.Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Godot.Vector3(float.MinValue, float.MinValue, float.MinValue);
        foreach (var corner in corners)
        {
            var p = System.Numerics.Vector3.Transform(corner, bindShape);
            var world = frame * (palette * new Godot.Vector3(p.X, p.Z, -p.Y));
            min = new Godot.Vector3(Mathf.Min(min.X, world.X), Mathf.Min(min.Y, world.Y), Mathf.Min(min.Z, world.Z));
            max = new Godot.Vector3(Mathf.Max(max.X, world.X), Mathf.Max(max.Y, world.Y), Mathf.Max(max.Z, world.Z));
        }
        return (min, max);
    }

    /// <summary>A one-joint rotation-only animation: <paramref name="slKey"/> held for its whole
    /// length.</summary>
    internal static AnimationData SelfTestAnimation(string joint, System.Numerics.Quaternion slKey, bool loop, float length) =>
        new()
        {
            Length = length,
            Loop = loop,
            InPoint = 0f,
            OutPoint = length,
            Priority = 3,
            Joints = new[]
            {
                new AnimationJointData
                {
                    JointName = joint,
                    Priority = 3,
                    RotationKeys = new[] { new RotationKeyframe(0f, slKey) },
                },
            },
        };

    /// <summary>
    /// FEAT-ANIMESH-02: what the control avatar does with a signalled set, headless and without
    /// assets -- a synthetic animation per id, handed over through the loader seam.
    ///
    /// <para>It pins the viewer's rules at the player: the same set again leaves a running animation
    /// alone; a changed sequence id restarts one that has finished and leaves one that is playing;
    /// an emptied set returns the skeleton to rest; a fetch that finishes after the set moved on does
    /// not play; an animation that will not load does not stop the others; a re-rig of the meshes
    /// does not touch the animations; a hidden object is not advanced; and a fetch that finishes
    /// after the avatar was freed applies to nothing.</para>
    /// </summary>
    internal (bool Passed, string Detail) SelfTestControlAvatarAnimation()
    {
        LoadAvatarSkeleton();
        if (_avatarSkeleton == null) return (false, "the avatar skeleton did not load");

        var failures = new List<string>();
        void Expect(bool ok, string what) { if (!ok) failures.Add(what); }
        void Settle() => MainThreadWorkQueue.Pump(double.MaxValue);
        // A gated fetch finishes on a worker thread, so "complete it and look" races: wait until
        // its result has been through the main-thread queue (bounded, so a bug fails instead of
        // hanging).
        void Finish(ControlAvatar target, TaskCompletionSource<AnimationData?> gate, AnimationData? result, Guid id)
        {
            gate.SetResult(result);
            for (int i = 0; i < 1000; i++)
            {
                Settle();
                if (!target.Fetching.Contains(id)) return;
                System.Threading.Thread.Sleep(2);
            }
        }

        const string Shoulder = "mShoulderLeft";
        var keyA = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, MathF.PI / 3f);
        var keyB = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, -MathF.PI / 4f);
        var idA = Guid.NewGuid();   // loops, shoulder
        var idB = Guid.NewGuid();   // one shot of half a second, elbow
        var idC = Guid.NewGuid();   // gated: finishes when the test says so
        var idD = Guid.NewGuid();   // gated
        var idE = Guid.NewGuid();   // never loads
        var idP = Guid.NewGuid();   // moves the pelvis
        var idHA = Guid.NewGuid();  // drives the pelvis (rotation and position), the shoulder and the elbow
        var idHB = Guid.NewGuid();  // drives the elbow only: the "4-joint head wobble" of the real robot
        var animA = SelfTestAnimation(Shoulder, keyA, loop: true, length: 2f);
        var animB = SelfTestAnimation("mElbowLeft", keyB, loop: false, length: 0.5f);
        // A pelvis position key: the one place the player writes a POSITION, as an offset on the rest.
        var animP = new AnimationData
        {
            Length = 1f,
            Loop = true,
            InPoint = 0f,
            OutPoint = 1f,
            Priority = 3,
            Joints = new[]
            {
                new AnimationJointData
                {
                    JointName = "mPelvis", Priority = 3,
                    PositionKeys = new[] { new PositionKeyframe(0f, new System.Numerics.Vector3(0f, 0f, 0.1f)) },
                },
            },
        };
        var pelvisTurn = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitZ, MathF.PI / 9f);
        var elbowBend = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitX, MathF.PI / 6f);
        var animHA = new AnimationData
        {
            Length = 2f,
            Loop = true,
            InPoint = 0f,
            OutPoint = 2f,
            Priority = 4,
            Joints = new[]
            {
                new AnimationJointData
                {
                    JointName = "mPelvis", Priority = 4,
                    RotationKeys = new[] { new RotationKeyframe(0f, pelvisTurn) },
                    PositionKeys = new[] { new PositionKeyframe(0f, new System.Numerics.Vector3(0f, 0f, -0.75f)) },
                },
                new AnimationJointData { JointName = Shoulder, Priority = 4, RotationKeys = new[] { new RotationKeyframe(0f, keyA) } },
                new AnimationJointData { JointName = "mElbowLeft", Priority = 4, RotationKeys = new[] { new RotationKeyframe(0f, elbowBend) } },
            },
        };
        var animHB = SelfTestAnimation("mElbowLeft", keyB, loop: true, length: 0.67f);
        var gates = new Dictionary<Guid, TaskCompletionSource<AnimationData?>>();

        AnimationLoaderOverride = id =>
        {
            if (gates.TryGetValue(id, out var gate)) return gate.Task;
            if (id == idA) return Task.FromResult<AnimationData?>(animA);
            if (id == idB) return Task.FromResult<AnimationData?>(animB);
            if (id == idP) return Task.FromResult<AnimationData?>(animP);
            if (id == idHA) return Task.FromResult<AnimationData?>(animHA);
            if (id == idHB) return Task.FromResult<AnimationData?>(animHB);
            return Task.FromResult<AnimationData?>(null);
        };

        var rootId = Guid.NewGuid();
        var meshId = Guid.NewGuid();
        try
        {
            var data = SelfTestRiggedBar(0.5f, out _);
            var ca = GetOrCreateControlAvatar(rootId);
            if (ca == null) return (false, "no control avatar could be created");
            if (!InstallControlAvatarPart(ca, rootId, true, data, meshId, null, default, true, false,
                    new Godot.Vector3(10f, 5f, -20f), System.Numerics.Quaternion.Identity))
                return (false, "the rigged bar produced no geometry");

            var player = ca.Visual.AnimPlayer;
            float Time(Guid id) => player.GetAnimationTime(id) ?? -1f;
            void Frame(float dt) => AdvanceControlAvatarAnimations(ca, dt);
            void Set(params (Guid id, int seq)[] items)
            {
                SetControlAvatarSignaledAnimations(ca,
                    items.Select(i => new SignaledAnimation(i.id, i.seq)).OrderBy(a => a.AnimationId).ToArray());
                Settle();
            }

            // Nothing signalled: the rest pose, and nothing to advance.
            Expect(!player.IsPlaying && SelfTestBoneAtRest(rootId, Shoulder), "with nothing signalled the skeleton must stay at rest");

            // The first set: the animation plays, and the bone is at the key.
            Set((idA, 1));
            Frame(0.1f);
            Expect(player.IsPlaying && SelfTestBoneAtKey(rootId, Shoulder, keyA) && !SelfTestBoneAtRest(rootId, Shoulder),
                "a signalled animation must pose its bone at the key, off the rest pose");
            Expect(MathF.Abs(Time(idA) - 0.1f) < 1e-4f, $"playback time after one 0.1 s frame (got {Time(idA)})");

            // The same set again changes nothing.
            Frame(0.2f);
            Set((idA, 1));
            Expect(MathF.Abs(Time(idA) - 0.3f) < 1e-4f, $"the same set again must not restart a running animation (time {Time(idA)}, want 0.3)");

            // A changed sequence id on a looping animation that is playing: it keeps playing.
            Set((idA, 2));
            Expect(MathF.Abs(Time(idA) - 0.3f) < 1e-4f && ca.Playing.Length == 1 && ca.Playing[0].SequenceId == 2,
                $"a new sequence id must not pull a steadily playing loop back to its start (time {Time(idA)}), but is recorded");

            // A one-shot that has run out: a changed sequence id brings it back to its first frame.
            Set((idA, 2), (idB, 1));
            Frame(1.0f);
            Expect(player.HasFinished(idB), "the half-second one-shot must have finished after a second");
            Set((idA, 2), (idB, 2));
            Expect(MathF.Abs(Time(idB)) < 1e-4f, $"a changed sequence id must restart a finished animation (time {Time(idB)})");
            Expect(Time(idA) > 1.0f, $"restarting one animation must leave the others alone (A at {Time(idA)})");
            // ... but not one that is still on its way.
            Frame(0.1f);
            Set((idA, 2), (idB, 3));
            Expect(MathF.Abs(Time(idB) - 0.1f) < 1e-4f, $"a one-shot still playing is not restarted (time {Time(idB)})");

            // An emptied set: every animation stops -- and the body stays where they left it. The
            // viewer resets no joint when a motion ends, and an object has no default motion to take
            // over; a joint that was never driven is still at rest.
            Set();
            Expect(!player.IsPlaying && ca.Playing.Length == 0
                   && SelfTestBoneAtKey(rootId, Shoulder, keyA) && SelfTestBoneAtKey(rootId, "mElbowLeft", keyB)
                   && SelfTestBoneAtRest(rootId, "mHipLeft"),
                "an emptied set must stop everything but leave the pose where the animations left it, " +
                "with a joint nothing ever drove still at rest");

            // Latest wins. C is slow: the set moves on to D, then C arrives -- and must not play.
            gates[idC] = new TaskCompletionSource<AnimationData?>();
            gates[idD] = new TaskCompletionSource<AnimationData?>();
            Set((idC, 1));
            Set((idD, 1));
            Finish(ca, gates[idD], animA, idD);
            Expect(player.GetAnimationTime(idD) != null && player.GetAnimationTime(idC) == null,
                "the animation the set moved on to must play while the one it left is still loading");
            Finish(ca, gates[idC], animB, idC);
            Expect(player.GetAnimationTime(idC) == null && ca.Playing.Length == 1,
                "a fetch that finishes after the set moved on must not play");
            // ... and a set that is emptied while its animation is in flight stays empty.
            gates[idC] = new TaskCompletionSource<AnimationData?>();
            Set((idC, 2));
            Set();
            Finish(ca, gates[idC], animB, idC);
            Expect(!player.IsPlaying && ca.Playing.Length == 0, "a fetch that finishes into an emptied set must not start anything");

            // One animation that will not load does not stop the others, and says so once.
            Set((idA, 1), (idE, 1));
            Expect(player.GetAnimationTime(idA) != null && player.GetAnimationTime(idE) == null,
                "an animation that will not load must not stop the others");
            Expect(_animationsReportedUnavailable.ContainsKey(idE), "an animation that will not load must be reported");

            // A re-rig (a LOD swap) replaces the meshes and keeps the skeleton: nothing restarts.
            Frame(0.3f);
            float before = Time(idA);
            var lod = SelfTestRiggedBar(0.5f, out _);
            bool rerigged = InstallControlAvatarPart(ca, rootId, true, lod, meshId, null, default, true, false,
                new Godot.Vector3(10f, 5f, -20f), System.Numerics.Quaternion.Identity);
            Expect(rerigged && player.IsPlaying && MathF.Abs(Time(idA) - before) < 1e-4f && ca.Parts.Count == 1,
                $"a re-rig must not touch the animations (time {before} -> {Time(idA)})");
            Frame(0.05f);
            Expect(SelfTestBoneAtKey(rootId, Shoulder, keyA), "the pose must carry on across a re-rig");

            // Out of sight: not posed, and no catching up afterwards.
            SetPartVisible(ca.Parts[rootId], false);
            float hidden = Time(idA);
            Frame(0.5f);
            Expect(MathF.Abs(Time(idA) - hidden) < 1e-6f, "an object none of whose meshes is visible must not be advanced");
            SetPartVisible(ca.Parts[rootId], true);
            Frame(0.1f);
            Expect(MathF.Abs(Time(idA) - hidden - 0.1f) < 1e-4f, "it must pick up where it stopped, not catch up");

            // A mesh that moves the pelvis (a robot, authored 0.3 m up): the animation's pelvis key is the
            // pelvis' ABSOLUTE position below the skeleton root, not an offset on that override. So the
            // bone sits AT the key, and the skinned mesh moves by (key - override) from where it stood
            // at rest -- not by the key, which is what rest + key would have given.
            {
                var lowRoot = Guid.NewGuid();
                var lowCa = GetOrCreateControlAvatar(lowRoot);
                var barScale = System.Numerics.Matrix4x4.CreateScale(0.5f) * System.Numerics.Matrix4x4.CreateRotationX(MathF.PI / 2f);
                var lowData = SelfTestRiggedBar(0.5f, out var lowCorners, pelvisHeight: 0.3f);
                if (lowCa == null || !InstallControlAvatarPart(lowCa, lowRoot, true, lowData, Guid.NewGuid(), null, default,
                        true, false, new Godot.Vector3(10f, 5f, -20f), System.Numerics.Quaternion.Identity))
                {
                    failures.Add("the low-pelvis bar produced no geometry");
                }
                else
                {
                    var lowSkeleton = lowCa.Visual.Skeleton!;
                    int pelvisBone = lowSkeleton.FindBone("mPelvis");
                    float restY = lowSkeleton.GetBoneRest(pelvisBone).Origin.Y;

                    var (restMin, restMax) = SelfTestPosedExtent(lowCa, lowRoot, lowCorners, barScale);
                    var measured = lowCa.Parts[lowRoot].Extent!;
                    Expect((restMin - measured.WorldMin).Length() < 2e-3f && (restMax - measured.WorldMax).Length() < 2e-3f,
                        "the posed-extent probe must agree with the rest-pose extent before anything is animated");

                    SetControlAvatarSignaledAnimations(lowCa, new[] { new SignaledAnimation(idP, 1) });
                    Settle();
                    AdvanceControlAvatarAnimations(lowCa, 0.05f);
                    float posedY = lowSkeleton.GetBonePosePosition(pelvisBone).Y;
                    Expect(MathF.Abs(restY - 0.3f) < 1e-3f && MathF.Abs(posedY - 0.1f) < 1e-3f,
                        $"a pelvis key is an absolute position (override {restY}, key 0.1, bone at {posedY}; rest + key would be {restY + 0.1f})");

                    // The skinned mesh moved by (key - override) along the skeleton's up, turned into world
                    // axes by the avatar's own rotation.
                    var (posedMin, posedMax) = SelfTestPosedExtent(lowCa, lowRoot, lowCorners, barScale);
                    var shift = new Basis(lowCa.Visual.Root.Quaternion) * new Godot.Vector3(0f, 0.1f - 0.3f, 0f);
                    Expect(((posedMin - restMin) - shift).Length() < 2e-3f && ((posedMax - restMax) - shift).Length() < 2e-3f,
                        $"the skinned mesh must move by (key - override) = -0.2 m, not by the key " +
                        $"(min moved by {posedMin - restMin}, want {shift})");
                }
                FreeControlAvatar(lowRoot);
                _controlAvatarOfPrim.Remove(lowRoot);
            }

            // A bone nothing drives this frame keeps its LAST value -- the pelvis POSITION included -- and
            // an ordinary avatar still goes back to rest in the same sequence. One animation drives the
            // pelvis (rotation and a position key), the shoulder and the elbow; the next drives the elbow
            // alone, as the real robot's head wobble does; then nothing at all.
            {
                var holdRoot = Guid.NewGuid();
                var holdCa = GetOrCreateControlAvatar(holdRoot);
                var rigData = SelfTestRiggedBar(0.5f, out _);
                if (holdCa == null || !InstallControlAvatarPart(holdCa, holdRoot, true, rigData, Guid.NewGuid(), null, default,
                        true, false, new Godot.Vector3(10f, 5f, -20f), System.Numerics.Quaternion.Identity))
                {
                    failures.Add("the hold rig produced no geometry");
                }
                else
                {
                    var sk = holdCa.Visual.Skeleton!;
                    int pelvisIdx = sk.FindBone("mPelvis");
                    var heldPelvisPosition = new Godot.Vector3(0f, -0.75f, 0f);
                    void Hold(params Guid[] ids)
                    {
                        SetControlAvatarSignaledAnimations(holdCa, ids.Select(i => new SignaledAnimation(i, 1)).OrderBy(a => a.AnimationId).ToArray());
                        Settle();
                        AdvanceControlAvatarAnimations(holdCa, 0.1f);
                    }
                    bool Nothing(Skeleton3D skeleton) =>
                        SelfTestAtRest(skeleton, "mPelvis") && SelfTestAtRest(skeleton, Shoulder)
                        && SelfTestAtRest(skeleton, "mElbowLeft") && SelfTestAtRestPosition(skeleton, "mPelvis");

                    Expect(Nothing(sk), "before anything plays the control avatar must be at rest");

                    Hold(idHA);
                    Expect(SelfTestAtKey(sk, "mPelvis", pelvisTurn) && SelfTestAtKey(sk, Shoulder, keyA)
                           && SelfTestAtKey(sk, "mElbowLeft", elbowBend)
                           && sk.GetBonePosePosition(pelvisIdx).IsEqualApprox(heldPelvisPosition),
                        "the first animation must drive the pelvis (rotation and position), the shoulder and the elbow " +
                        $"(pelvis at {sk.GetBonePosePosition(pelvisIdx)}, want {heldPelvisPosition})");

                    Hold(idHB);
                    Expect(SelfTestAtKey(sk, "mElbowLeft", keyB), "the second animation must drive the elbow");
                    Expect(SelfTestAtKey(sk, Shoulder, keyA) && SelfTestAtKey(sk, "mPelvis", pelvisTurn)
                           && sk.GetBonePosePosition(pelvisIdx).IsEqualApprox(heldPelvisPosition)
                           && !SelfTestAtRest(sk, Shoulder) && !SelfTestAtRest(sk, "mPelvis"),
                        "bones the playing animation does not drive must keep the first one's LAST values -- " +
                        $"rotation and the pelvis position (pelvis at {sk.GetBonePosePosition(pelvisIdx)}, want {heldPelvisPosition})");

                    Hold();
                    Expect(!holdCa.Visual.AnimPlayer.IsPlaying && SelfTestAtKey(sk, "mElbowLeft", keyB)
                           && SelfTestAtKey(sk, Shoulder, keyA) && SelfTestAtKey(sk, "mPelvis", pelvisTurn)
                           && sk.GetBonePosePosition(pelvisIdx).IsEqualApprox(heldPelvisPosition)
                           && SelfTestAtRest(sk, "mHipLeft") && SelfTestAtRestPosition(sk, "mHipLeft"),
                        "an emptied set must hold the whole pose, with a joint nothing ever drove still at rest");

                    // A LOD re-rig keeps the skeleton and so the pose.
                    InstallControlAvatarPart(holdCa, holdRoot, true, SelfTestRiggedBar(0.5f, out _), Guid.NewGuid(), null, default,
                        true, false, new Godot.Vector3(10f, 5f, -20f), System.Numerics.Quaternion.Identity);
                    Expect(SelfTestAtKey(sk, Shoulder, keyA) && sk.GetBonePosePosition(pelvisIdx).IsEqualApprox(heldPelvisPosition),
                        "a re-rig must not disturb the held pose");

                    // A SECOND mesh with a joint-position override the skeleton does not have yet rewrites every
                    // rest and resets the poses (ApplyJointPositionOverrides); the held pose is put back.
                    float restBefore = sk.GetBoneRest(pelvisIdx).Origin.Y;
                    InstallControlAvatarPart(holdCa, Guid.NewGuid(), false,
                        SelfTestRiggedBar(0.5f, out _, pelvisHeight: 0.4f), Guid.NewGuid(), null, default,
                        true, false, new Godot.Vector3(10f, 5f, -20f), System.Numerics.Quaternion.Identity);
                    Expect(MathF.Abs(sk.GetBoneRest(pelvisIdx).Origin.Y - 0.4f) < 1e-3f && MathF.Abs(restBefore - 0.4f) > 1e-3f,
                        "the second mesh's pelvis override must have rewritten the rest (otherwise this check proves nothing)");
                    Expect(SelfTestAtKey(sk, "mElbowLeft", keyB) && SelfTestAtKey(sk, Shoulder, keyA)
                           && SelfTestAtKey(sk, "mPelvis", pelvisTurn)
                           && sk.GetBonePosePosition(pelvisIdx).IsEqualApprox(heldPelvisPosition)
                           && SelfTestAtRest(sk, "mHipLeft"),
                        "a second mesh that resets the skeleton's poses must not wipe the held pose");

                    // The same sequence on an ordinary avatar's player: rest + key, and everything the
                    // playing animation does not drive snaps back to rest.
                    var plainSkeleton = SkeletonBuilder.Build(_avatarSkeleton);
                    AddChild(plainSkeleton);
                    plainSkeleton.ResetBonePoses();
                    var plainPlayer = new AvatarAnimationPlayer();
                    plainPlayer.SetSkeleton(plainSkeleton);
                    int plainPelvis = plainSkeleton.FindBone("mPelvis");
                    var plainRest = plainSkeleton.GetBoneRest(plainPelvis).Origin;

                    plainPlayer.SetActiveAnimations(new List<(Guid, AnimationData)> { (idHA, animHA) });
                    plainPlayer.Advance(0.1f);
                    Expect(plainSkeleton.GetBonePosePosition(plainPelvis).IsEqualApprox(plainRest + new Godot.Vector3(0f, -0.75f, 0f))
                           && SelfTestAtKey(plainSkeleton, Shoulder, keyA),
                        $"an ordinary avatar must still read a pelvis key as an offset on the rest (rest {plainRest}, pelvis at {plainSkeleton.GetBonePosePosition(plainPelvis)})");

                    plainPlayer.SetActiveAnimations(new List<(Guid, AnimationData)> { (idHB, animHB) });
                    plainPlayer.Advance(0.1f);
                    Expect(SelfTestAtKey(plainSkeleton, "mElbowLeft", keyB)
                           && SelfTestAtRest(plainSkeleton, Shoulder) && SelfTestAtRest(plainSkeleton, "mPelvis")
                           && SelfTestAtRestPosition(plainSkeleton, "mPelvis"),
                        "an ordinary avatar must still snap every bone its animations do not drive back to rest");

                    plainPlayer.SetActiveAnimations(new List<(Guid, AnimationData)>());
                    Expect(Nothing(plainSkeleton), "an ordinary avatar whose last animation stops must still return to rest");
                    plainSkeleton.QueueFree();
                }
                FreeControlAvatar(holdRoot);
                _controlAvatarOfPrim.Remove(holdRoot);
            }

            // Freed while a fetch is in flight: the late result applies to nothing.
            Set();
            gates[idC] = new TaskCompletionSource<AnimationData?>();
            Set((idC, 3));
            FreeControlAvatar(rootId);
            _controlAvatarOfPrim.Remove(rootId);
            Finish(ca, gates[idC], animA, idC);
            Expect(_controlAvatars.Count == 0 && !player.IsPlaying,
                "a fetch that finishes after its avatar was freed must apply to nothing");
        }
        finally
        {
            AnimationLoaderOverride = null;
            FreeControlAvatar(rootId);
            _controlAvatarOfPrim.Remove(rootId);
        }

        return failures.Count == 0
            ? (true, "plays the signalled set, same set leaves it running, a new sequence id restarts a finished one-shot but not a playing loop, " +
                     "an emptied set stops everything but holds the pose, a late fetch loses to the newer set, one bad animation spares the rest, " +
                     "a re-rig and a hidden object leave the clock alone, a pelvis key is absolute, an undriven bone holds its last pose (an ordinary avatar still resets), " +
                     "a second mesh's skeleton reset leaves the held pose alone, " +
                     "a late fetch after the avatar is freed applies to nothing")
            : (false, string.Join("; ", failures));
    }
}
