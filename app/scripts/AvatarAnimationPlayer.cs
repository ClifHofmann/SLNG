using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SLNG.Assets;
using SLNG.Core;
using SLNG.Core.Avatars;

namespace SLNG.App;

/// <summary>
/// Manual per-avatar animation player. Evaluates SL binary BVH keyframe data each
/// frame and applies bone poses directly to a <see cref="Skeleton3D"/>.
///
/// We bypass Godot's AnimationPlayer because Second Life uses per-bone priority
/// blending that doesn't map to Godot's animation system — each bone follows the
/// animation with the highest priority that affects it.
///
/// Coordinate conversion: SL uses Z-up; Godot uses Y-up.
///   SL (X, Y, Z) → Godot (X, Z, -Y) for positions
///   Quaternion axes are swapped the same way.
/// </summary>
public sealed class AvatarAnimationPlayer
{
    private sealed class PlayingAnimation
    {
        public AnimationData Data;
        public readonly MotionState Motion;
        public Guid AnimationId;
        public string? Name;

        /// <summary>Time inside the clip the keys are read at.</summary>
        public float CurrentTime => Motion.PoseTime;

        public PlayingAnimation(AnimationData data, Guid animId, string? name = null)
        {
            Data = data;
            AnimationId = animId;
            Name = name;
            Motion = new MotionState(TimingOf(data));
        }
    }

    /// <summary>What an animation says about its loop and fades, as the motion clock wants it. The
    /// out point of a clip that does not give one is its end.</summary>
    private static MotionTiming TimingOf(AnimationData data)
        => new(data.Length, data.Loop, data.InPoint, data.OutPoint > 0f ? data.OutPoint : data.Length, data.EaseInTime, data.EaseOutTime);

    private readonly List<PlayingAnimation> _active = new();
    // Per-joint weighted layer tables, one per channel (BUG-ANIM-07 / BUG-ANIM-08: the rule lives in
    // SLNG.Core so it can be tested -- the highest priority takes the joint, a motion that is fading
    // in or out shares it with the layers below). Rotations are held as Godot quaternions; position keys as the
    // SL-space value the animation gave, because how a key becomes a bone position depends on the
    // joint (the pelvis adds the rest, every other joint takes it absolutely, scaled by its parent).
    private readonly JointBlendTable<Quaternion> _boneRots = new();
    private readonly JointBlendTable<System.Numerics.Vector3> _bonePositions = new();
    // FEAT-ANIM-06: local inventory / machinima animation overlay, independent of sim network updates
    private readonly List<PlayingAnimation> _localOverlay = new();
    private Skeleton3D? _skeleton;

    // FEAT-ANIM-01: for a short window after a predicted MOVING gait starts, force it above any
    // still-lagging AO stand so the self avatar isn't frozen in the AO's stand pose while moving.
    // After the window it drops back to its authored priority, so a real AO walk (which by then
    // has arrived) takes over -- "instant built-in walk -> AO walk", never "stand-slide -> walk".
    private Guid _locomotionBoostId;
    private float _locomotionBoostElapsed;
    private const float LocomotionBoostSeconds = 2.0f;
    private const int LocomotionBoostPriority = 6; // above SL's normal max authored priority (~4)

    private bool _isFrozen;
    private IReadOnlyList<(Guid id, AnimationData data)>? _pendingAnimations;
    private AvatarHoldMode _holdMode = AvatarHoldMode.None;
    private int _neckBoneIdx = -1;
    private int _headBoneIdx = -1;
    private int _pelvisBoneIdx = -1;

    // BUG-ANIM-07: per bone (by index), the OWN scale of its PARENT -- the factor a position key is
    // multiplied by, exactly what ApplyShape multiplies a joint's override or default offset by
    // (SL: a child's offset is scaled by its parent's scale, never by the child's or an ancestor's).
    // Filled by AvatarRenderer.ApplyShape through SetParentScales, so it follows every shape and
    // joint-override change. Null until the first ApplyShape, when every factor is 1.
    private System.Numerics.Vector3[]? _parentScales;

    /// <summary>FEAT-ANIM-10: Yaw offset (radians) for procedural head/neck gaze tracking camera.</summary>
    public float HeadGazeYaw { get; set; }

    /// <summary>FEAT-ANIM-10: Pitch offset (radians) for procedural head/neck gaze tracking camera.</summary>
    public float HeadGazePitch { get; set; }

    /// <summary>True if any animations (network or local) are currently loaded and playing.</summary>
    public bool IsPlaying => _active.Count > 0 || _localOverlay.Count > 0;

    /// <summary>FEAT-ANIMESH-02: apply an animation's pelvis POSITION key as the bone's absolute local
    /// position instead of an offset on its rest position. Off (an ordinary avatar) it is exactly
    /// what it always was: <c>rest + key</c>. On (a control avatar) the pose position IS the key.
    ///
    /// <para>The viewer treats the key as absolute: <c>LLKeyframeMotion::applyKeyframes</c> hands the
    /// curve value straight to the joint state (llkeyframemotion.cpp:414-417); keyframe motions are
    /// <c>NORMAL_BLEND</c> (llkeyframemotion.h:127), so <c>LLJointStateBlender::blendJointStates</c>
    /// copies or lerps the joint states' positions and never adds a rest
    /// (llpose.cpp:322-329) and applies the result with <c>setPosition(blended_pos + added_pos)</c>
    /// (:388), whose <c>apply_attachment_overrides</c> defaults to false (lljoint.h:233) -- so a
    /// mesh's joint-position override does not apply while a key is active. Where the key is
    /// measured FROM is the skeleton root. A real avatar's root sits at pelvis height
    /// (llvoavatar.cpp:4677), so its stand animation's pelvis key is ~0, and SLNG's avatar skeleton
    /// has its root at the feet, which is why <c>rest + key</c> (1.067 + ~0) is the same place there.
    /// A control avatar's root is the root prim itself (llcontrolavatar.cpp:244; llvoavatar.cpp:4713
    /// sends it there instead of to the lifted position), so the key IS the pelvis height, and adding
    /// the rest to it displaces the whole mesh by the pelvis' own rest height.</para></summary>
    public bool AbsolutePelvisPosition { get; set; }

    /// <summary>FEAT-ANIMESH-02: a bone that no ACTIVE animation drives keeps whatever it was last
    /// given -- rotation and, for the pelvis, position -- instead of snapping back to the rest pose
    /// every frame. Off (an ordinary avatar) it is exactly what it always was: every frame starts
    /// from the rest pose and only the driven bones are written. On (a control avatar) a bone that
    /// has never been driven stays at rest, and every other bone holds its last value, including
    /// after the last animation stops.
    ///
    /// <para>That is the viewer's behaviour. <c>LLJointStateBlender::blendJointStates</c> starts from
    /// the joint's CURRENT position and rotation (llpose.cpp:257-258) and writes them back
    /// (:388-390); with no joint state at all it returns early, "instead of resetting joint state to
    /// default, just leave it unchanged from last frame" (:242-244). <c>LLPoseBlender::blendAndApply</c>
    /// visits only the blenders of joints that have an active joint state (llpose.cpp:512-522), a
    /// stopped motion only drops its pose weight to zero (<c>LLMotion::deactivate</c>,
    /// llmotion.cpp:157-170) and releases its constraints (<c>LLKeyframeMotion::onDeactivate</c>,
    /// llkeyframemotion.cpp:789-795), and the motion controller resets joint SIGNATURES, never joints
    /// (llmotioncontroller.cpp:486-492). Nothing puts the body back: an ordinary avatar always has a
    /// stand animation driving it, and a control avatar has no default motions
    /// (<c>mEnableDefaultMotions = false</c>, llcontrolavatar.cpp:56).</para></summary>
    public bool HoldUndrivenBones { get; set; }

    // What a bone was last given while HoldUndrivenBones is on, so a reset that is not the player's
    // own (a skeleton rebuilt around a held pose) can be undone by ReapplyHeldPose.
    private readonly Dictionary<int, Quaternion> _heldRotations = new();
    private readonly Dictionary<int, Vector3> _heldPositions = new();
    // The same for every joint but the pelvis, kept as the SL-space KEY and converted again on
    // reapply: the parent's scale may have changed in between (that is why the reset happened).
    private readonly Dictionary<int, System.Numerics.Vector3> _heldKeys = new();

    /// <summary>Writes the held pose back onto the skeleton. For a caller that has just reset the
    /// skeleton's poses for a reason of its own (<c>Skeleton3D.ResetBonePoses</c> after a joint
    /// override changed) and must not lose what the animations had left there. Only the bones the
    /// animations drove are touched, so every other bone keeps the fresh rest the reset gave it.
    /// A no-op unless <see cref="HoldUndrivenBones"/> is on.</summary>
    public void ReapplyHeldPose()
    {
        if (_skeleton == null || !HoldUndrivenBones) return;

        foreach (var (boneIdx, rotation) in _heldRotations) _skeleton.SetBonePoseRotation(boneIdx, rotation);
        foreach (var (boneIdx, position) in _heldPositions) _skeleton.SetBonePosePosition(boneIdx, position);
        foreach (var (boneIdx, key) in _heldKeys) _skeleton.SetBonePosePosition(boneIdx, KeyToPosePosition(boneIdx, key));
    }

    /// <summary>BUG-ANIM-07: hands the player, for every bone of its skeleton (by index), the OWN scale
    /// of that bone's parent -- <c>AvatarVisual.BoneOwnScale</c> of the parent, 1 for a bone without
    /// one. An animation's position key on a joint is its absolute local position, and a child's
    /// offset is scaled by its parent's scale (<c>ApplyShape</c> does the same to the rest). The
    /// array is kept, not copied: call this again after every <c>ApplyShape</c>, which is when the
    /// scales change.</summary>
    public void SetParentScales(System.Numerics.Vector3[] parentScaleByBone) => _parentScales = parentScaleByBone;

    private System.Numerics.Vector3 ParentScaleOf(int boneIdx)
        => _parentScales != null && (uint)boneIdx < (uint)_parentScales.Length ? _parentScales[boneIdx] : System.Numerics.Vector3.One;

    /// <summary>BUG-ANIM-07: the pose position a bone takes while an animation's position key
    /// (SL space, as keyed) drives it. The pelvis keeps what it always had -- <c>rest + key</c>, or the
    /// bare key for a control avatar (<see cref="AbsolutePelvisPosition"/>) -- every other joint takes
    /// the key as its absolute local position scaled by its parent (<see cref="AnimatedJointPosition"/>),
    /// replacing the rest, which is where a worn mesh's joint-position override lives.</summary>
    private Vector3 KeyToPosePosition(int boneIdx, System.Numerics.Vector3 slKey)
    {
        bool isPelvis = boneIdx == _pelvisBoneIdx;
        var rest = System.Numerics.Vector3.Zero;
        if (isPelvis && !AbsolutePelvisPosition)
        {
            // Godot (x, y, z) is SL (x, z, -y); the pelvis' rest origin back into SL space.
            var origin = _skeleton!.GetBoneRest(boneIdx).Origin;
            rest = new System.Numerics.Vector3(origin.X, -origin.Z, origin.Y);
        }

        var slPosition = AnimatedJointPosition.Resolve(
            isPelvis,
            AbsolutePelvisPosition ? AnimatedJointPosition.PelvisMode.Absolute : AnimatedJointPosition.PelvisMode.RestPlusKey,
            rest, slKey, ParentScaleOf(boneIdx));

        // SL pos(x, y, z) -> Godot pos(x, z, -y)
        return new Vector3(slPosition.X, slPosition.Z, -slPosition.Y);
    }

    /// <summary>FEAT-ANIM-06: Plays an animation as a local overlay.</summary>
    public void PlayLocal(Guid animId, AnimationData data, string? name = null)
    {
        for (int i = 0; i < _localOverlay.Count; i++)
        {
            if (_localOverlay[i].AnimationId == animId)
            {
                var again = _localOverlay[i];
                again.Data = data;
                again.Motion.Timing = TimingOf(data);
                again.Motion.Activate();
                if (!string.IsNullOrEmpty(name)) again.Name = name;
                // Started again, so the newest: the end of the list.
                _localOverlay.RemoveAt(i);
                _localOverlay.Add(again);
                return;
            }
        }
        _localOverlay.Add(new PlayingAnimation(data, animId, name));
    }

    /// <summary>FEAT-ANIM-06: Stops a specific local overlay animation. It fades out over its ease-out
    /// (BUG-ANIM-08) and is gone from <see cref="GetLocalAnimationInfos"/> at once.</summary>
    public bool StopLocal(Guid animId)
    {
        bool stopped = false;
        foreach (var anim in _localOverlay)
        {
            if (anim.AnimationId != animId || anim.Motion.Stopped) continue;
            anim.Motion.Stop();
            stopped = true;
        }
        return stopped;
    }

    /// <summary>FEAT-ANIM-06: Clears all local overlay animations.</summary>
    public void ClearLocal()
    {
        _localOverlay.Clear();
        if (_active.Count == 0)
        {
            ResetToRestPose();
        }
    }

    /// <summary>FEAT-ANIM-06: Returns info on all currently active local overlay animations.</summary>
    public IReadOnlyList<(Guid id, string name, int priority)> GetLocalAnimationInfos()
    {
        return _localOverlay.Where(a => !a.Motion.Stopped)
            .Select(a => (a.AnimationId, a.Name ?? a.AnimationId.ToString()[..8], a.Data.Priority)).ToList();
    }

    /// <summary>FEAT-ANIM-09: True if animation playback is frozen at its current frame.</summary>
    public bool IsFrozen
    {
        get => _isFrozen;
        set
        {
            if (_isFrozen == value) return;
            _isFrozen = value;
            if (!_isFrozen && _pendingAnimations != null)
            {
                var pending = _pendingAnimations;
                _pendingAnimations = null;
                SetActiveAnimations(pending);
            }
        }
    }

    /// <summary>FEAT-ANIM-07: Current sustained hold mode (None, BindPose, PoseStand).</summary>
    public AvatarHoldMode HoldMode
    {
        get => _holdMode;
        set
        {
            if (_isFrozen) return;
            _holdMode = value;
        }
    }

    /// <summary>FEAT-ANIM-07: Built-in STAND clip evaluated when in <see cref="AvatarHoldMode.PoseStand"/>.</summary>
    public AnimationData? StandAnimation { get; set; }

    /// <summary>Bind this player to a skeleton. Must be called before Advance.</summary>
    public void SetSkeleton(Skeleton3D skeleton)
    {
        _skeleton = skeleton;
        _heldRotations.Clear();
        _heldPositions.Clear();
        _heldKeys.Clear();
        _neckBoneIdx = skeleton.FindBone("mNeck");
        _headBoneIdx = skeleton.FindBone("mHead");
        _pelvisBoneIdx = skeleton.FindBone(AnimatedJointPosition.PelvisJointName);
        // Scales made for another skeleton are not this one's.
        if (_parentScales != null && _parentScales.Length != skeleton.GetBoneCount()) _parentScales = null;
    }

    /// <summary>FEAT-ANIM-01: mark <paramref name="animId"/> (a locally-predicted moving gait) to
    /// win over everything for <see cref="LocomotionBoostSeconds"/>. Re-marking the same id is a
    /// no-op (the timer is not reset), so holding a walk key does not keep the boost alive
    /// forever.</summary>
    public void SetLocomotionBoost(Guid animId)
    {
        if (animId == _locomotionBoostId) return;
        _locomotionBoostId = animId;
        _locomotionBoostElapsed = 0f;
    }

    /// <summary>FEAT-ANIM-01: stop boosting (predicted gait is now a resting pose, or the self
    /// avatar sat down).</summary>
    public void ClearLocomotionBoost() => _locomotionBoostId = System.Guid.Empty;

    private bool BoostActive(Guid animId)
        => animId != System.Guid.Empty && animId == _locomotionBoostId
        && _locomotionBoostElapsed < LocomotionBoostSeconds;

    /// <summary>
    /// Replace the entire set of active animations. Animations not in the new set
    /// are stopped; new ones are started at InPoint.
    /// If frozen, the incoming animation set is buffered and applied upon unfreezing.
    /// </summary>
    public void SetActiveAnimations(IReadOnlyList<(Guid id, AnimationData data)> animations)
    {
        if (_isFrozen)
        {
            _pendingAnimations = animations.ToArray();
            return;
        }

        // BUG-ANIM-08: an animation that left the set is STOPPED, not dropped -- it fades out over its
        // ease-out under whatever replaced it, and Advance drops it once it has (the viewer's
        // LLMotionController::stopMotionInstance -> setStopTime, then deactivate after the ease-out). One
        // that comes back while it is still fading is started again from its current weight.
        bool changed = false;
        foreach (var p in _active)
        {
            bool wanted = false;
            foreach (var (id, _) in animations)
            {
                if (p.AnimationId == id) { wanted = true; break; }
            }

            if (!wanted && !p.Motion.Stopped)
            {
                p.Motion.Stop();
                changed = true;
                if (Diagnostics.Enabled) GD.Print($"[AnimPlayer] Stopping animation {p.AnimationId} (ease out {p.Data.EaseOutTime:0.##} s, weight {p.Motion.Weight:0.##})");
            }
        }

        // Add new ones.
        foreach (var (id, data) in animations)
        {
            int at = _active.FindIndex(p => p.AnimationId == id);
            if (at < 0)
            {
                _active.Add(new PlayingAnimation(data, id));
                changed = true;
                if (Diagnostics.Enabled) GD.Print($"[AnimPlayer] Starting animation {id} (ease in {data.EaseInTime:0.##} s, priority {data.Priority}, loop {data.Loop})");
            }
            else if (_active[at].Motion.Stopped)
            {
                // Newest again: the end of the list.
                var back = _active[at];
                back.Motion.Activate();
                _active.RemoveAt(at);
                _active.Add(back);
                changed = true;
                if (Diagnostics.Enabled) GD.Print($"[AnimPlayer] Restarting animation {id} from weight {back.Motion.Weight:0.##}");
            }
        }

        if (changed) _traceFramesLeft = TraceFramesPerChange;

        // One with no ease-out is gone at once, with nothing left to fade.
        if (RemoveFinished() && _active.Count == 0 && _localOverlay.Count == 0 && !HoldUndrivenBones)
        {
            if (Diagnostics.Enabled) GD.Print("[AnimPlayer] Active count is 0, resetting to rest pose");
            ResetToRestPose();
        }
    }

    /// <summary>Drops every motion that has faded all the way out. True when it dropped any.</summary>
    private bool RemoveFinished()
    {
        bool droppedAny = false;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            if (!_active[i].Motion.IsFinished) continue;

            if (Diagnostics.Enabled) GD.Print($"[AnimPlayer] Animation {_active[i].AnimationId} finished fading out");
            _active.RemoveAt(i);
            droppedAny = true;
        }

        for (int i = _localOverlay.Count - 1; i >= 0; i--)
        {
            if (!_localOverlay[i].Motion.IsFinished) continue;
            _localOverlay.RemoveAt(i);
            droppedAny = true;
        }
        return droppedAny;
    }

    /// <summary>Clear all playing animations and reset the skeleton to rest pose.</summary>
    public void Stop()
    {
        _isFrozen = false;
        _pendingAnimations = null;
        _active.Clear();
        _localOverlay.Clear();
        // An explicit stop is the one thing that lets go of a held pose.
        _heldRotations.Clear();
        _heldPositions.Clear();
        _heldKeys.Clear();
        ResetToRestPose();
    }

    /// <summary>
    /// FEAT-ANIM-04: Resynchronizes all active animations by starting their clocks again from 0 in
    /// the same frame (each keeps the weight it has, so none dips). No-op while in a sustained hold
    /// mode or frozen.
    /// </summary>
    public void Resync()
    {
        if (_holdMode != AvatarHoldMode.None || _isFrozen) return;

        foreach (var anim in _active)
        {
            if (!anim.Motion.Stopped) anim.Motion.Activate();
        }
        ApplyBonePoses();
    }

    /// <summary>
    /// FEAT-ANIM-09: Advances or rewinds the frozen playback time by <paramref name="dir"/> frames
    /// (default 1/30 s per frame) and applies the new pose to the skeleton immediately.
    /// Only active while <see cref="IsFrozen"/> is true.
    /// </summary>
    public void StepFrame(int dir, float stepSeconds = 1f / 30f)
    {
        if (!_isFrozen || _active.Count == 0) return;

        foreach (var anim in _active)
        {
            float loopEnd = anim.Data.OutPoint > 0 ? anim.Data.OutPoint : anim.Data.Length;
            float loopStart = anim.Data.InPoint;

            float time = anim.CurrentTime + dir * stepSeconds;

            if (anim.Data.Loop && loopEnd > loopStart)
            {
                while (time > loopEnd) time -= (loopEnd - loopStart);
                while (time < loopStart) time += (loopEnd - loopStart);
            }
            else
            {
                time = Mathf.Clamp(time, 0f, loopEnd);
            }

            anim.Motion.Seek(time);
        }

        ApplyBonePoses();
    }

    /// <summary>FEAT-ANIMESH-02: starts one active animation again from the beginning, leaving every
    /// other animation alone. <see cref="SetActiveAnimations"/> keys on the animation id alone, so
    /// an id that is already playing is never touched by it -- this is how a changed sequence id
    /// reaches a running animation. False when <paramref name="animId"/> is not playing.</summary>
    public bool Restart(Guid animId)
    {
        foreach (var anim in _active)
        {
            if (anim.AnimationId != animId) continue;
            anim.Motion.Activate();
            return true;
        }
        return false;
    }

    /// <summary>FEAT-ANIMESH-02: true when <paramref name="animId"/> is playing, does not loop, and has
    /// run to its end -- where <see cref="AdvanceAnimTime"/> clamps it and it holds its last pose.
    /// The reference viewer's equivalent is a keyframe motion that has stopped itself and been
    /// deactivated, the only steady state a new start request brings back to life.</summary>
    public bool HasFinished(Guid animId)
    {
        foreach (var anim in _active)
        {
            if (anim.AnimationId != animId) continue;

            float loopEnd = anim.Data.OutPoint > 0 ? anim.Data.OutPoint : anim.Data.Length;
            bool loops = anim.Data.Loop && loopEnd > anim.Data.InPoint;
            return !loops && anim.CurrentTime >= loopEnd;
        }
        return false;
    }

    /// <summary>
    /// Returns the current playback time of the specified animation, or null if not playing (one that
    /// has been stopped and is only fading out no longer counts as playing).
    /// </summary>
    public float? GetAnimationTime(Guid animId)
    {
        foreach (var anim in _active)
        {
            if (anim.AnimationId == animId && !anim.Motion.Stopped) return anim.CurrentTime;
        }
        return null;
    }

    /// <summary>
    /// Advance all playing animations by <paramref name="delta"/> seconds and apply
    /// the blended result to the skeleton.
    /// </summary>
    public void Advance(float delta)
    {
        if (_skeleton == null) return;

        // FEAT-ANIM-09: While frozen, time does not advance, but we evaluate bone poses to hold the pose.
        if (_isFrozen)
        {
            ApplyBonePoses();
            return;
        }

        // FEAT-ANIM-07: Bind pose holds raw skeleton rest every frame.
        if (_holdMode == AvatarHoldMode.BindPose)
        {
            ResetToRestPose();
            return;
        }

        // FEAT-ANIM-07: Pose stand evaluates the fixed stand frame without advancing time.
        if (_holdMode == AvatarHoldMode.PoseStand)
        {
            ApplyBonePoses();
            return;
        }

        if (_active.Count == 0 && _localOverlay.Count == 0) return;

        if (_locomotionBoostId != System.Guid.Empty) _locomotionBoostElapsed += delta;

        // Advance the clock and the fade of each playing animation, and drop the ones that have
        // finished fading out (BUG-ANIM-08).
        foreach (var anim in _active) anim.Motion.Advance(delta);

        // FEAT-ANIM-06: the same for each local overlay animation. A non-looping one that has run to its
        // end stops itself, as the viewer's motions do, and fades out.
        for (int i = _localOverlay.Count - 1; i >= 0; i--)
        {
            var anim = _localOverlay[i];
            anim.Motion.Advance(delta);

            float loopEnd = anim.Data.OutPoint > 0 ? anim.Data.OutPoint : anim.Data.Length;
            if (!anim.Data.Loop && !anim.Motion.Stopped && anim.CurrentTime >= loopEnd) anim.Motion.Stop();
        }

        bool droppedAny = RemoveFinished();

        // (A holding player keeps the last pose when the last animation stops: see HoldUndrivenBones.)
        if (droppedAny && _active.Count == 0 && _localOverlay.Count == 0 && !HoldUndrivenBones)
        {
            if (Diagnostics.Enabled) GD.Print("[AnimPlayer] Active count is 0, resetting to rest pose");
            ResetToRestPose();
            return;
        }

        // Evaluate and apply the per-bone, weighted priority blend.
        ApplyBonePoses();
        if (Diagnostics.Enabled) TraceBlend(delta);
    }

    private void ApplyBonePoses()
    {
        if (_skeleton == null) return;

        // Reset all bones so any bone no longer animated snaps back to its rest pose.
        // (Avatar shape deformations are baked into the bone rests, so this only clears animations).
        // A holding player (a control avatar, see HoldUndrivenBones) does not: a bone nothing drives
        // this frame stays as it is, and only the driven ones are written below.
        if (!HoldUndrivenBones) ResetToRestPose();

        if (!_isFrozen && HoldMode == AvatarHoldMode.BindPose)
        {
            return;
        }

        // For each bone in the skeleton, the animations that affect it are layered by priority and each
        // counts by its fade weight (BUG-ANIM-08): the highest priority takes the bone once it is fully
        // in, while one that is fading in or out shares it with the layers below. They are offered
        // NEWEST first -- the viewer lists its motions that way, so a newer motion of equal priority
        // is on top -- which also lets a layer that cannot show be skipped unevaluated.
        // Rotation and position tracks are tracked separately so a rotation-only gesture
        // does not wipe out an underlying furniture pose's pelvis position track.
        // The two work tables are reused: this runs every frame for every animating avatar, and a
        // fresh pair of dictionaries per call was ~7 KB of garbage per avatar per frame (measured).
        // Cleared here and only ever used inside this method, on the main thread.
        var boneRots = _boneRots;
        var bonePositions = _bonePositions;
        boneRots.Clear();
        bonePositions.Clear();

        if (!_isFrozen && HoldMode == AvatarHoldMode.PoseStand)
        {
            if (StandAnimation != null)
            {
                ApplyAnimationToBones(StandAnimation, StandAnimation.InPoint, StandAnimation.Priority, false, 1f, boneRots, bonePositions);
            }
        }
        else
        {
            // FEAT-ANIM-06: the local overlay is the newest, so it goes first.
            for (int i = _localOverlay.Count - 1; i >= 0; i--)
            {
                var anim = _localOverlay[i];
                ApplyAnimationToBones(anim.Data, anim.CurrentTime, anim.Data.Priority, false, anim.Motion.Weight, boneRots, bonePositions);
            }

            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var anim = _active[i];
                bool boosted = BoostActive(anim.AnimationId);
                int animPriority = boosted ? LocomotionBoostPriority : anim.Data.Priority;
                ApplyAnimationToBones(anim.Data, anim.CurrentTime, animPriority, boosted, anim.Motion.Weight, boneRots, bonePositions);
            }
        }

        // FEAT-ANIM-10: Procedural head and neck gaze (head follows camera)
        if (Mathf.Abs(HeadGazeYaw) > 0.001f || Mathf.Abs(HeadGazePitch) > 0.001f)
        {
            if (_neckBoneIdx < 0 && _skeleton != null) _neckBoneIdx = _skeleton.FindBone("mNeck");
            if (_headBoneIdx < 0 && _skeleton != null) _headBoneIdx = _skeleton.FindBone("mHead");

            if (_neckBoneIdx >= 0)
            {
                var neckGaze = Quaternion.FromEuler(new Vector3(HeadGazePitch * 0.3f, HeadGazeYaw * 0.3f, 0));
                var existing = boneRots.TryResolve(_neckBoneIdx, BlendRotation, out var r) ? r : Quaternion.Identity;
                boneRots.Offer(_neckBoneIdx, 99, 1f, neckGaze * existing);
            }
            if (_headBoneIdx >= 0)
            {
                var headGaze = Quaternion.FromEuler(new Vector3(HeadGazePitch * 0.7f, HeadGazeYaw * 0.7f, 0));
                var existing = boneRots.TryResolve(_headBoneIdx, BlendRotation, out var r) ? r : Quaternion.Identity;
                boneRots.Offer(_headBoneIdx, 99, 1f, headGaze * existing);
            }
        }

        // Apply blended poses to skeleton.
        for (int i = 0; i < boneRots.TouchedCount; i++)
        {
            int boneIdx = boneRots.TouchedJoint(i);
            if (!boneRots.TryResolve(boneIdx, BlendRotation, out var rotation)) continue;

            _skeleton!.SetBonePoseRotation(boneIdx, rotation);
            if (HoldUndrivenBones) _heldRotations[boneIdx] = rotation;
        }

        // Apply position channels. In Godot's Skeleton3D, SetBonePosePosition replaces the bone's local
        // position (which ResetBonePoses initialises to the bone's rest -- the skeleton default, plus the
        // shape's distortion, or a worn mesh's joint-position override). Every joint may carry a key, not
        // only the pelvis: the viewer reads and applies them for all (llkeyframemotion.cpp:414-417,
        // 1680-1782), as an absolute local position that beats an override while it drives
        // (llpose.cpp:388, lljoint.h:233) -- BUG-ANIM-07, add-on HUDs shape their add-on this way.
        // KeyToPosePosition says how each is turned into a position: the pelvis is measured from the
        // skeleton root (rest + key, or the bare key for a control avatar), every other joint is
        // absolute and scaled by its parent. A bone no longer driven goes back to its rest on the
        // next frame's reset, i.e. back to its override / default.
        for (int i = 0; i < bonePositions.TouchedCount; i++)
        {
            int boneIdx = bonePositions.TouchedJoint(i);
            if (!bonePositions.TryResolve(boneIdx, BlendPosition, out var key)) continue;

            var written = KeyToPosePosition(boneIdx, key);
            _skeleton!.SetBonePosePosition(boneIdx, written);
            if (HoldUndrivenBones)
            {
                if (boneIdx == _pelvisBoneIdx) _heldPositions[boneIdx] = written;
                else _heldKeys[boneIdx] = key;
            }
        }
    }

    // The viewer blends a lower layer UNDER what is accumulated: nlerp(t, layer, accumulated) for
    // rotations, lerp for positions (llpose.cpp:322, 360). Cached, so a frame allocates no delegate.
    private static readonly Func<float, Quaternion, Quaternion, Quaternion> BlendRotation = NlerpSl;
    private static readonly Func<float, System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3> BlendPosition
        = (t, layer, accumulated) => System.Numerics.Vector3.Lerp(layer, accumulated, t);

    private void ApplyAnimationToBones(AnimationData data, float time, int animPriority, bool boosted, float weight,
        JointBlendTable<Quaternion> boneRots,
        JointBlendTable<System.Numerics.Vector3> bonePositions)
    {
        if (_skeleton == null || !(weight > 0f)) return;

        foreach (var joint in data.Joints)
        {
            int boneIdx = _skeleton.FindBone(joint.JointName);
            if (boneIdx < 0) continue;

            int effectivePriority = JointPriority.Effective(boosted, LocomotionBoostPriority, joint.Priority, animPriority);

            // Rotation channel
            if (joint.RotationKeys.Length > 0 && !boneRots.Saturated(boneIdx, effectivePriority))
            {
                var rot = EvaluateRotation(joint.RotationKeys, time);
                var godotRot = new Quaternion(rot.X, rot.Z, -rot.Y, rot.W);
                boneRots.Offer(boneIdx, effectivePriority, weight, godotRot);
            }

            // Position channel: any joint, same per-joint priority rule as rotation (BUG-ANIM-07).
            // This used to take mPelvis only, on the strength of llbvhloader.cpp:781 ("Animating
            // position ... is only supported for mPelvis") -- but that line is the BVH IMPORTER's
            // channel parsing, not .anim playback. The viewer keeps and applies position keys for
            // every joint (llkeyframemotion.cpp:1680-1782, :414-417). The tongue joints that looked
            // like "dummy tracks" in BUG-ANIM-01 were an absolute key, equal to the skeleton default,
            // added to the rest a second time; KeyToPosePosition does not add it.
            if (joint.PositionKeys.Length > 0 && !bonePositions.Saturated(boneIdx, effectivePriority))
            {
                bonePositions.Offer(boneIdx, effectivePriority, weight, EvaluatePosition(joint.PositionKeys, time));
            }
        }
    }

    /// <summary>
    /// Evaluate rotation keyframes at the given time using spherical interpolation.
    /// Returns an SL-space quaternion (not yet converted to Godot).
    /// </summary>
    private static Quaternion EvaluateRotation(RotationKeyframe[] keys, float time)
    {
        if (keys.Length == 0) return Quaternion.Identity;
        if (keys.Length == 1) return ToGodotQuat(keys[0].Rotation);

        // Find the two bounding keyframes.
        if (time >= keys[^1].Time) return ToGodotQuat(keys[^1].Rotation);
        if (time <= keys[0].Time) return ToGodotQuat(keys[0].Rotation);

        for (int i = 0; i < keys.Length - 1; i++)
        {
            if (time >= keys[i].Time && time <= keys[i + 1].Time)
            {
                float span = keys[i + 1].Time - keys[i].Time;
                float t = span > 0 ? (time - keys[i].Time) / span : 0f;
                var a = ToGodotQuat(keys[i].Rotation);
                var b = ToGodotQuat(keys[i + 1].Rotation);
                return NlerpSl(t, a, b);
            }
        }

        return ToGodotQuat(keys[^1].Rotation);
    }

    /// <summary>
    /// Evaluate position keyframes at the given time using linear interpolation.
    /// Returns an SL-space position (not yet converted to Godot).
    /// </summary>
    private static System.Numerics.Vector3 EvaluatePosition(PositionKeyframe[] keys, float time)
    {
        if (keys.Length == 0) return System.Numerics.Vector3.Zero;
        if (keys.Length == 1) return keys[0].Position;

        if (time >= keys[^1].Time) return keys[^1].Position;
        if (time <= keys[0].Time) return keys[0].Position;

        for (int i = 0; i < keys.Length - 1; i++)
        {
            if (time >= keys[i].Time && time <= keys[i + 1].Time)
            {
                float span = keys[i + 1].Time - keys[i].Time;
                float t = span > 0 ? (time - keys[i].Time) / span : 0f;
                return System.Numerics.Vector3.Lerp(keys[i].Position, keys[i + 1].Position, t);
            }
        }

        return keys[^1].Position;
    }

    /// <summary>
    /// Convert a System.Numerics.Quaternion to a Godot.Quaternion.
    /// This is a straight copy — coordinate-system conversion happens later.
    /// </summary>
    /// <summary>The viewer's own interpolation for keyframe rotations, ported from <c>nlerp</c>
    /// (llquaternion.cpp:694), which is what
    /// <c>LLKeyframeMotion::RotationCurve::interp</c> calls (llkeyframemotion.cpp:277).
    ///
    /// It is NOT slerp, which is what this used to do. The viewer reaches for slerp only when the
    /// two keys point into opposite hemispheres (dot &lt; 0) and otherwise takes a normalized
    /// componentwise lerp. Both curves agree at the endpoints and differ in between — lerp eases
    /// toward the nearer key instead of sweeping at constant angular velocity — so the wrong one
    /// reads as animation timing that drifts from the real viewer, worst on widely spaced
    /// keys.</summary>
    private static Quaternion NlerpSl(float t, Quaternion a, Quaternion b)
    {
        if (a.Dot(b) < 0f) return a.Slerp(b, t);

        float inv = 1f - t;
        var r = new Quaternion(
            inv * a.X + t * b.X,
            inv * a.Y + t * b.Y,
            inv * a.Z + t * b.Z,
            inv * a.W + t * b.W);

        // Cannot reach zero length while dot >= 0 — two same-hemisphere unit quaternions always
        // blend to at least cos(theta/2) — but a NaN keyframe would, and a NaN bone pose is much
        // harder to trace back than a frozen one.
        float len = r.Length();
        return len > 0.0001f ? r / len : a;
    }

    /// <summary>An SL keyframe rotation as a Godot quaternion, normalized.
    ///
    /// The magnitude here is quantization noise, not data. SL stores a keyframe rotation as three
    /// U16-quantized components and reconstructs w as sqrt(1 - |xyz|²)
    /// (<c>LLQuaternion::unpackFromVector3</c>, llquaternion.cpp:943). When quantization pushes
    /// |xyz| just past 1 the viewer clamps the negative radicand to w = 0, which leaves a
    /// slightly over-unit quaternion — and <c>AnimationDecodeService</c> ports that clamp
    /// faithfully, so the same values arrive here.
    ///
    /// LLQuaternion's math shrugs that off. Godot's does not, in two different ways:
    /// <c>Quaternion.Slerp</c> throws <c>ArgumentException</c> on a non-unit operand (27 of them
    /// in one session on Lbsa Plaza), and a non-unit bone pose SCALES the bone — the quieter and
    /// more misleading half of the same bug. Normalizing at the single point where SL rotations
    /// become Godot ones closes both.</summary>
    private static Quaternion ToGodotQuat(System.Numerics.Quaternion q)
    {
        var g = new Quaternion(q.X, q.Y, q.Z, q.W);
        float len = g.Length();
        return len > 0.0001f ? g / len : Quaternion.Identity;
    }

    // BUG-ANIM-08 trace (--diag): for ~TraceFramesPerChange frames after a motion starts or stops, one
    // line per frame with every motion's weight, so a change between two loops can be read as a
    // crossfade (weights summing to ~1 and moving) or as a jolt (a weight jumping to 1 or 0).
    private const int TraceFramesPerChange = 40;
    private int _traceFramesLeft;
    private float _traceClock;

    /// <summary>Names this player in the <c>[AnimBlend]</c> trace; the skeleton's instance id when unset.</summary>
    public string? TraceLabel { get; set; }

    private void TraceBlend(float delta)
    {
        if (_traceFramesLeft <= 0) return;

        _traceClock += delta;
        var line = new System.Text.StringBuilder();
        bool blending = false;
        for (int i = _localOverlay.Count - 1; i >= 0; i--) AppendTrace(line, _localOverlay[i], "local", ref blending);
        for (int i = _active.Count - 1; i >= 0; i--) AppendTrace(line, _active[i], "", ref blending);

        // Nothing fading: the change has settled, stop talking.
        if (!blending) { _traceFramesLeft = 0; return; }

        _traceFramesLeft--;
        GD.Print($"[AnimBlend] {TraceLabel ?? "sk#" + _skeleton!.GetInstanceId()} dt={delta * 1000f:0} ms{line}");
    }

    private static void AppendTrace(System.Text.StringBuilder line, PlayingAnimation anim, string kind, ref bool blending)
    {
        var m = anim.Motion;
        if (m.Weight < 0.999f || m.Stopped) blending = true;
        line.Append($" | {anim.AnimationId.ToString()[..8]}{(kind.Length > 0 ? " " + kind : "")} pri={anim.Data.Priority} ")
            .Append(m.Stopped ? "out" : m.Weight < 0.999f ? "in" : "on")
            .Append($" w={m.Weight:0.00} t={m.PoseTime:0.00}");
    }

    private void ResetToRestPose()
    {
        if (_skeleton == null) return;
        _skeleton.ResetBonePoses();
#pragma warning disable CS0618
        _skeleton.ForceUpdateAllBoneTransforms();
#pragma warning restore CS0618
    }
}
