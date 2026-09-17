using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SLNG.Assets;
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
        public float CurrentTime;
        public Guid AnimationId;
        public string? Name;

        public PlayingAnimation(AnimationData data, Guid animId, string? name = null)
        {
            Data = data;
            AnimationId = animId;
            Name = name;
            CurrentTime = data.InPoint; // start at InPoint
        }
    }

    private readonly List<PlayingAnimation> _active = new();
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

    /// <summary>FEAT-ANIM-10: Yaw offset (radians) for procedural head/neck gaze tracking camera.</summary>
    public float HeadGazeYaw { get; set; }

    /// <summary>FEAT-ANIM-10: Pitch offset (radians) for procedural head/neck gaze tracking camera.</summary>
    public float HeadGazePitch { get; set; }

    /// <summary>True if any animations (network or local) are currently loaded and playing.</summary>
    public bool IsPlaying => _active.Count > 0 || _localOverlay.Count > 0;

    /// <summary>FEAT-ANIM-06: Plays an animation as a local overlay.</summary>
    public void PlayLocal(Guid animId, AnimationData data, string? name = null)
    {
        for (int i = 0; i < _localOverlay.Count; i++)
        {
            if (_localOverlay[i].AnimationId == animId)
            {
                _localOverlay[i].CurrentTime = data.InPoint;
                _localOverlay[i].Data = data;
                if (!string.IsNullOrEmpty(name)) _localOverlay[i].Name = name;
                return;
            }
        }
        _localOverlay.Add(new PlayingAnimation(data, animId, name));
    }

    /// <summary>FEAT-ANIM-06: Stops a specific local overlay animation.</summary>
    public bool StopLocal(Guid animId)
    {
        int removed = _localOverlay.RemoveAll(a => a.AnimationId == animId);
        if (removed > 0 && _active.Count == 0 && _localOverlay.Count == 0)
        {
            ResetToRestPose();
        }
        return removed > 0;
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
        return _localOverlay.Select(a => (a.AnimationId, a.Name ?? a.AnimationId.ToString()[..8], a.Data.Priority)).ToList();
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
        _neckBoneIdx = skeleton.FindBone("mNeck");
        _headBoneIdx = skeleton.FindBone("mHead");
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

        // Remove animations no longer active.
        int removed = _active.RemoveAll(p =>
        {
            foreach (var (id, _) in animations)
            {
                if (p.AnimationId == id) return false;
            }
            if (Diagnostics.Enabled) GD.Print($"[AnimPlayer] Removing animation {p.AnimationId}");
            return true;
        });

        if (_active.Count == 0 && _localOverlay.Count == 0 && removed > 0)
        {
            if (Diagnostics.Enabled) GD.Print("[AnimPlayer] Active count is 0, resetting to rest pose");
            ResetToRestPose();
        }

        // Add new ones.
        foreach (var (id, data) in animations)
        {
            bool found = false;
            foreach (var p in _active)
            {
                if (p.AnimationId == id) { found = true; break; }
            }
            if (!found)
            {
                _active.Add(new PlayingAnimation(data, id));
            }
        }
    }

    /// <summary>Clear all playing animations and reset the skeleton to rest pose.</summary>
    public void Stop()
    {
        _isFrozen = false;
        _pendingAnimations = null;
        _active.Clear();
        _localOverlay.Clear();
        ResetToRestPose();
    }

    /// <summary>
    /// FEAT-ANIM-04: Resynchronizes all active animations by resetting their playback
    /// time to InPoint in the same frame. No-op while in a sustained hold mode or frozen.
    /// </summary>
    public void Resync()
    {
        if (_holdMode != AvatarHoldMode.None || _isFrozen) return;

        foreach (var anim in _active)
        {
            anim.CurrentTime = anim.Data.InPoint;
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

            anim.CurrentTime += dir * stepSeconds;

            if (anim.Data.Loop && loopEnd > loopStart)
            {
                while (anim.CurrentTime > loopEnd) anim.CurrentTime -= (loopEnd - loopStart);
                while (anim.CurrentTime < loopStart) anim.CurrentTime += (loopEnd - loopStart);
            }
            else
            {
                anim.CurrentTime = Mathf.Clamp(anim.CurrentTime, loopStart, loopEnd);
            }
        }

        ApplyBonePoses();
    }

    /// <summary>
    /// Returns the current playback time of the specified animation, or null if not playing.
    /// </summary>
    public float? GetAnimationTime(Guid animId)
    {
        foreach (var anim in _active)
        {
            if (anim.AnimationId == animId) return anim.CurrentTime;
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

        // Advance time for each playing animation.
        foreach (var anim in _active)
        {
            AdvanceAnimTime(anim, delta);
        }

        // FEAT-ANIM-06: Advance time for each local overlay animation. Remove non-looping ones once complete.
        for (int i = _localOverlay.Count - 1; i >= 0; i--)
        {
            var anim = _localOverlay[i];
            AdvanceAnimTime(anim, delta);

            float loopEnd = anim.Data.OutPoint > 0 ? anim.Data.OutPoint : anim.Data.Length;
            if (!anim.Data.Loop && anim.CurrentTime >= loopEnd)
            {
                _localOverlay.RemoveAt(i);
                if (_active.Count == 0 && _localOverlay.Count == 0)
                {
                    ResetToRestPose();
                }
            }
        }

        // Evaluate and apply per-bone, highest-priority-wins blending.
        ApplyBonePoses();
    }

    private static void AdvanceAnimTime(PlayingAnimation anim, float delta)
    {
        anim.CurrentTime += delta;

        float loopEnd = anim.Data.OutPoint > 0 ? anim.Data.OutPoint : anim.Data.Length;
        float loopStart = anim.Data.InPoint;

        if (anim.Data.Loop && loopEnd > loopStart)
        {
            while (anim.CurrentTime > loopEnd)
            {
                anim.CurrentTime -= (loopEnd - loopStart);
            }
        }
        else
        {
            // Clamp non-looping animations.
            if (anim.CurrentTime > loopEnd)
                anim.CurrentTime = loopEnd;
        }
    }

    private void ApplyBonePoses()
    {
        if (_skeleton == null) return;

        // Reset all bones so any bone no longer animated snaps back to its rest pose.
        // (Avatar shape deformations are baked into the bone rests, so this only clears animations).
        ResetToRestPose();

        if (!_isFrozen && HoldMode == AvatarHoldMode.BindPose)
        {
            return;
        }

        // For each bone in the skeleton, find the highest-priority animation that
        // affects it and apply that animation's value.
        // Rotation and position tracks are tracked separately so a rotation-only gesture
        // does not wipe out an underlying furniture pose's pelvis position track.
        var boneRots = new Dictionary<int, (int priority, Quaternion rotation)>();
        var bonePositions = new Dictionary<int, (int priority, Vector3 position)>();

        if (!_isFrozen && HoldMode == AvatarHoldMode.PoseStand)
        {
            if (StandAnimation != null)
            {
                ApplyAnimationToBones(StandAnimation, StandAnimation.InPoint, StandAnimation.Priority, false, boneRots, bonePositions);
            }
        }
        else
        {
            foreach (var anim in _active)
            {
                bool boosted = BoostActive(anim.AnimationId);
                int animPriority = boosted ? LocomotionBoostPriority : anim.Data.Priority;
                ApplyAnimationToBones(anim.Data, anim.CurrentTime, animPriority, boosted, boneRots, bonePositions);
            }

            // FEAT-ANIM-06: Apply local overlay animations into the bone priority blend
            foreach (var anim in _localOverlay)
            {
                ApplyAnimationToBones(anim.Data, anim.CurrentTime, anim.Data.Priority, false, boneRots, bonePositions);
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
                var existing = boneRots.TryGetValue(_neckBoneIdx, out var r) ? r.rotation : Quaternion.Identity;
                boneRots[_neckBoneIdx] = (99, neckGaze * existing);
            }
            if (_headBoneIdx >= 0)
            {
                var headGaze = Quaternion.FromEuler(new Vector3(HeadGazePitch * 0.7f, HeadGazeYaw * 0.7f, 0));
                var existing = boneRots.TryGetValue(_headBoneIdx, out var r) ? r.rotation : Quaternion.Identity;
                boneRots[_headBoneIdx] = (99, headGaze * existing);
            }
        }

        // Apply blended poses to skeleton.
        foreach (var (boneIdx, pose) in boneRots)
        {
            _skeleton!.SetBonePoseRotation(boneIdx, pose.rotation);
        }

        // Apply position channels (most commonly mPelvis offset authored into furniture/posestand/cuddle poses).
        // In Godot's Skeleton3D, SetBonePosePosition overrides the bone's local position (normally initialized
        // to bone rest). Second Life animation position keys are offsets relative to the neutral rest position,
        // so adding the bone's rest origin applies the animation offset faithfully without collapsing the bone.
        foreach (var (boneIdx, pose) in bonePositions)
        {
            var restPos = _skeleton!.GetBoneRest(boneIdx).Origin;
            _skeleton.SetBonePosePosition(boneIdx, restPos + pose.position);
        }
    }

    private void ApplyAnimationToBones(AnimationData data, float time, int animPriority, bool boosted,
        Dictionary<int, (int priority, Quaternion rotation)> boneRots,
        Dictionary<int, (int priority, Vector3 position)> bonePositions)
    {
        if (_skeleton == null) return;

        foreach (var joint in data.Joints)
        {
            int boneIdx = _skeleton.FindBone(joint.JointName);
            if (boneIdx < 0) continue;

            int effectivePriority = boosted ? LocomotionBoostPriority
                : (joint.Priority >= 0 ? joint.Priority : animPriority);

            // Rotation channel
            if (joint.RotationKeys.Length > 0)
            {
                if (!boneRots.TryGetValue(boneIdx, out var existingRot) || effectivePriority >= existingRot.priority)
                {
                    var rot = EvaluateRotation(joint.RotationKeys, time);
                    var godotRot = new Quaternion(rot.X, rot.Z, -rot.Y, rot.W);
                    boneRots[boneIdx] = (effectivePriority, godotRot);
                }
            }

            // Position channel: Second Life only animates translation for mPelvis
            // (llbvhloader.cpp:781: "Animating position (via mNumChannels = 6) is only supported for mPelvis").
            // Other joints (e.g. mFaceTongueBase) may contain unnormalized or dummy position tracks that
            // must never be applied to bone local transforms.
            if (joint.JointName == "mPelvis" && joint.PositionKeys.Length > 0)
            {
                if (!bonePositions.TryGetValue(boneIdx, out var existingPos) || effectivePriority >= existingPos.priority)
                {
                    var p = EvaluatePosition(joint.PositionKeys, time);
                    // SL pos(x, y, z) → Godot pos(x, z, -y)
                    var godotPos = new Vector3(p.X, p.Z, -p.Y);
                    bonePositions[boneIdx] = (effectivePriority, godotPos);
                }
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

    private void ResetToRestPose()
    {
        if (_skeleton == null) return;
        _skeleton.ResetBonePoses();
#pragma warning disable CS0618
        _skeleton.ForceUpdateAllBoneTransforms();
#pragma warning restore CS0618
    }
}
