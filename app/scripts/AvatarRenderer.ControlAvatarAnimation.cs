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

    /// <summary>The one <c>[Animesh]</c> line per change of the effective set (--diag): the whole
    /// set, and what the change started, stopped and restarted.</summary>
    private static void ReportAnimationSet(ControlAvatar ca, AnimationSetChange change)
    {
        GD.Print($"[Animesh] anim root={Short(ca.RootEntityId)} set=[{string.Join(' ', ca.Signaled.Select(a => Short(a)))}] " +
                 $"started=[{string.Join(' ', change.Start.Select(a => Short(a)))}] " +
                 $"stopped=[{string.Join(' ', change.Stop.Select(Short))}] " +
                 $"restarted=[{string.Join(' ', change.Restart.Select(a => Short(a)))}]");
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

    /// <summary>Is the named bone of this object's skeleton at the pose this animation key sets?
    /// (SL key in, compared as the Godot rotation the player writes.)</summary>
    internal bool SelfTestBoneAtKey(Guid rootId, string bone, System.Numerics.Quaternion slKey)
    {
        if (!_controlAvatars.TryGetValue(rootId, out var ca) || ca.Visual.Skeleton is not { } skeleton) return false;
        int idx = skeleton.FindBone(bone);
        if (idx < 0) return false;
        var want = new Godot.Quaternion(slKey.X, slKey.Z, -slKey.Y, slKey.W).Normalized();
        var have = skeleton.GetBonePoseRotation(idx);
        return have.IsEqualApprox(want) || have.IsEqualApprox(-want);
    }

    /// <summary>Is the named bone at its rest rotation?</summary>
    internal bool SelfTestBoneAtRest(Guid rootId, string bone)
    {
        if (!_controlAvatars.TryGetValue(rootId, out var ca) || ca.Visual.Skeleton is not { } skeleton) return false;
        int idx = skeleton.FindBone(bone);
        if (idx < 0) return false;
        var rest = skeleton.GetBoneRest(idx).Basis.GetRotationQuaternion();
        var have = skeleton.GetBonePoseRotation(idx);
        return have.IsEqualApprox(rest) || have.IsEqualApprox(-rest);
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
        var gates = new Dictionary<Guid, TaskCompletionSource<AnimationData?>>();

        AnimationLoaderOverride = id =>
        {
            if (gates.TryGetValue(id, out var gate)) return gate.Task;
            if (id == idA) return Task.FromResult<AnimationData?>(animA);
            if (id == idB) return Task.FromResult<AnimationData?>(animB);
            if (id == idP) return Task.FromResult<AnimationData?>(animP);
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

            // An emptied set: everything stops and the skeleton goes back to rest.
            Set();
            Expect(!player.IsPlaying && SelfTestBoneAtRest(rootId, Shoulder) && ca.Playing.Length == 0,
                "an emptied set must stop everything and return the skeleton to rest");

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
            Expect(_animationsReportedUnavailable.Contains(idE), "an animation that will not load must be reported");

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

            // A mesh that moves the pelvis (a robot): the animation's pelvis offset is added to the
            // OVERRIDDEN rest position, not to the stock one -- the authored joint positions survive.
            {
                var lowRoot = Guid.NewGuid();
                var lowCa = GetOrCreateControlAvatar(lowRoot);
                if (lowCa == null || !InstallControlAvatarPart(lowCa, lowRoot, true,
                        SelfTestRiggedBar(0.5f, out _, pelvisHeight: 0.3f), Guid.NewGuid(), null, default, true, false,
                        new Godot.Vector3(10f, 5f, -20f), System.Numerics.Quaternion.Identity))
                {
                    failures.Add("the low-pelvis bar produced no geometry");
                }
                else
                {
                    var lowSkeleton = lowCa.Visual.Skeleton!;
                    int pelvisBone = lowSkeleton.FindBone("mPelvis");
                    float restY = lowSkeleton.GetBoneRest(pelvisBone).Origin.Y;
                    SetControlAvatarSignaledAnimations(lowCa, new[] { new SignaledAnimation(idP, 1) });
                    Settle();
                    AdvanceControlAvatarAnimations(lowCa, 0.05f);
                    float posedY = lowSkeleton.GetBonePosePosition(pelvisBone).Y;
                    Expect(MathF.Abs(restY - 0.3f) < 1e-3f && MathF.Abs(posedY - (restY + 0.1f)) < 1e-3f,
                        $"an animated pelvis must keep the mesh's authored joint position (rest {restY}, posed {posedY}, want {restY + 0.1f})");
                }
                FreeControlAvatar(lowRoot);
                _controlAvatarOfPrim.Remove(lowRoot);
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
                     "an emptied set returns to rest, a late fetch loses to the newer set, one bad animation spares the rest, " +
                     "a re-rig and a hidden object leave the clock alone, an animated pelvis keeps its authored position, " +
                     "a late fetch after the avatar is freed applies to nothing")
            : (false, string.Join("; ", failures));
    }
}
