using Godot;
using SLNG.Assets;
using System;
using System.Collections.Generic;

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

        // Apply to skeleton.
        foreach (var (boneIdx, pose) in bonePoses)
        {
            _skeleton.SetBonePoseRotation(boneIdx, pose.rotation);
            if (pose.hasPos)
            {
                _skeleton.SetBonePosePosition(boneIdx, pose.position);
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
                return a.Slerp(b, t);
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
    private static Quaternion ToGodotQuat(System.Numerics.Quaternion q)
        => new Quaternion(q.X, q.Y, q.Z, q.W);

    private void ResetToRestPose()
    {
        if (_skeleton == null) return;
        for (int i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            _skeleton.SetBonePoseRotation(i, Quaternion.Identity);
            _skeleton.SetBonePosePosition(i, Vector3.Zero);
        }
    }
}
