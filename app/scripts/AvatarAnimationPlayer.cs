using System;
using System.Collections.Generic;
using Godot;
using SLNG.Assets;

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

        public PlayingAnimation(AnimationData data, Guid animId)
        {
            Data = data;
            AnimationId = animId;
            CurrentTime = data.InPoint; // start at InPoint
        }
    }

    private readonly List<PlayingAnimation> _active = new();
    private Skeleton3D? _skeleton;

    /// <summary>True if any animations are currently loaded and playing.</summary>
    public bool IsPlaying => _active.Count > 0;

    /// <summary>Bind this player to a skeleton. Must be called before Advance.</summary>
    public void SetSkeleton(Skeleton3D skeleton) => _skeleton = skeleton;

    /// <summary>
    /// Replace the entire set of active animations. Animations not in the new set
    /// are stopped; new ones are started at InPoint.
    /// </summary>
    public void SetActiveAnimations(IReadOnlyList<(Guid id, AnimationData data)> animations)
    {
        // Remove animations no longer active.
        _active.RemoveAll(p =>
        {
            foreach (var (id, _) in animations)
            {
                if (p.AnimationId == id) return false;
            }
            return true;
        });

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
        _active.Clear();
        ResetToRestPose();
    }

    /// <summary>
    /// Advance all playing animations by <paramref name="delta"/> seconds and apply
    /// the blended result to the skeleton.
    /// </summary>
    public void Advance(float delta)
    {
        if (_skeleton == null || _active.Count == 0) return;

        // Advance time for each playing animation.
        foreach (var anim in _active)
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

        // Evaluate and apply per-bone, highest-priority-wins blending.
        ApplyBonePoses();
    }

    private void ApplyBonePoses()
    {
        if (_skeleton == null) return;

        // Reset all bones so any bone no longer animated snaps back to its rest pose.
        // (Avatar shape deformations are baked into the bone rests, so this only clears animations).
        ResetToRestPose();

        // For each bone in the skeleton, find the highest-priority animation that
        // affects it and apply that animation's value.
        //
        // Key: bone index → (priority, rotation, position, hasPosition)
        var bonePoses = new Dictionary<int, (int priority, Quaternion rotation, Vector3 position, bool hasPos)>();

        foreach (var anim in _active)
        {
            int animPriority = anim.Data.Priority;

            foreach (var joint in anim.Data.Joints)
            {
                int boneIdx = _skeleton.FindBone(joint.JointName);
                if (boneIdx < 0) continue;

                int effectivePriority = joint.Priority >= 0 ? joint.Priority : animPriority;

                if (bonePoses.TryGetValue(boneIdx, out var existing) && existing.priority > effectivePriority)
                    continue;

                // Evaluate rotation keyframes.
                var rot = EvaluateRotation(joint.RotationKeys, anim.CurrentTime);

                // Convert from SL space (Z-up) to Godot space (Y-up).
                // SL quat(x, y, z, w) → Godot quat(x, z, -y, w)
                var godotRot = new Quaternion(rot.X, rot.Z, -rot.Y, rot.W);

                Vector3 pos = Vector3.Zero;
                bool hasPos = false;
                if (joint.PositionKeys.Length > 0)
                {
                    var p = EvaluatePosition(joint.PositionKeys, anim.CurrentTime);
                    // SL pos(x, y, z) → Godot pos(x, z, -y)
                    pos = new Vector3(p.X, p.Z, -p.Y);
                    hasPos = true;
                }

                bonePoses[boneIdx] = (effectivePriority, godotRot, pos, hasPos);
            }
        }

        // Apply to skeleton. Rotation is applied as an override.
        // Position is applied as an offset from the bone's rest position,
        // because SL position keys are relative to the SL parent bone, and Godot's
        // skeleton has different rest offsets (e.g. mPelvis is 1.04m above Godot's Root,
        // while in SL mPelvis is coincident with mRoot).
        foreach (var (boneIdx, pose) in bonePoses)
        {
            _skeleton.SetBonePoseRotation(boneIdx, pose.rotation);
            if (pose.hasPos)
            {
                var restPos = _skeleton.GetBoneRest(boneIdx).Origin;
                _skeleton.SetBonePosePosition(boneIdx, restPos + pose.position);
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
        if (time <= keys[0].Time) return ToGodotQuat(keys[0].Rotation);
        if (time >= keys[^1].Time) return ToGodotQuat(keys[^1].Rotation);

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

        if (time <= keys[0].Time) return keys[0].Position;
        if (time >= keys[^1].Time) return keys[^1].Position;

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
    }
}
