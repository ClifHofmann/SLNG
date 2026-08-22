using System;
using System.Numerics;
using LibreMetaverse;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace SLNG.Assets;

/// <summary>
/// Decodes raw binary BVH animation data (Second Life .anim format) into an
/// engine-neutral <see cref="AnimationData"/> structure.
///
/// Uses LibreMetaverse's <see cref="BinBVHAnimationReader"/> internally, but no
/// LibreMetaverse type crosses the public boundary.
///
/// Rotation keys in the SL binary BVH format store compressed quaternions as
/// Vector3 (X, Y, Z) — the W component is derived: W = √(1 − x² − y² − z²).
/// </summary>
public static class AnimationDecodeService
{
    /// <summary>
    /// Decodes raw animation asset bytes into engine-neutral keyframe data.
    /// Returns null if the data is invalid or cannot be parsed.
    /// </summary>
    public static AnimationData? Decode(byte[] animationBytes)
    {
        if (animationBytes == null || animationBytes.Length < 4)
            return null;

        try
        {
            var reader = new BinBVHAnimationReader(animationBytes);
            return Convert(reader);
        }
        catch (Exception)
        {
            // Malformed animation data — swallow and return null.
            return null;
        }
    }

    private static AnimationData Convert(BinBVHAnimationReader reader)
    {
        var joints = new AnimationJointData[reader.joints.Length];
        for (int i = 0; i < reader.joints.Length; i++)
        {
            joints[i] = ConvertJoint(reader.joints[i]);
        }

        return new AnimationData
        {
            Length = reader.Length,
            Loop = reader.Loop,
            InPoint = reader.InPoint,
            OutPoint = reader.OutPoint,
            EaseInTime = reader.EaseInTime,
            EaseOutTime = reader.EaseOutTime,
            Priority = reader.Priority,
            Joints = joints,
        };
    }

    /// <summary>Rebuilds a keyframe rotation from the three components SL actually stores, a
    /// literal port of <c>LLQuaternion::unpackFromVector3</c> (llquaternion.cpp:943).
    ///
    /// SL saves space by keeping only x, y and z — each quantized to a U16 mapped onto [-1, 1] —
    /// and recovering w as sqrt(1 - |xyz|²) on the strength of the quaternion being a unit one.
    ///
    /// <para><b>The result is therefore not always a unit quaternion.</b> Quantization can push
    /// |xyz| just past 1, making the radicand negative; the viewer clamps that case to w = 0
    /// rather than producing a NaN, and so does this. What comes back is then slightly longer
    /// than 1. That is faithful, not a defect — but it means no consumer may assume normality.
    /// <c>AvatarAnimationPlayer.ToGodotQuat</c> is where that is dealt with, because Godot both
    /// throws on a non-unit Slerp operand and silently SCALES a bone given a non-unit
    /// pose.</para></summary>
    internal static Quaternion UnpackRotation(float x, float y, float z)
    {
        float wSq = 1.0f - (x * x + y * y + z * z);
        return new Quaternion(x, y, z, wSq > 0f ? MathF.Sqrt(wSq) : 0f);
    }

    private static AnimationJointData ConvertJoint(binBVHJoint joint)
    {
        var rotKeys = new RotationKeyframe[joint.rotationkeys.Length];
        for (int i = 0; i < joint.rotationkeys.Length; i++)
        {
            ref var key = ref joint.rotationkeys[i];
            rotKeys[i] = new RotationKeyframe(
                key.time,
                UnpackRotation(key.key_element.X, key.key_element.Y, key.key_element.Z));
        }

        var posKeys = new PositionKeyframe[joint.positionkeys.Length];
        for (int i = 0; i < joint.positionkeys.Length; i++)
        {
            ref var key = ref joint.positionkeys[i];
            posKeys[i] = new PositionKeyframe(
                key.time,
                new Vector3(key.key_element.X, key.key_element.Y, key.key_element.Z)
            );
        }

        return new AnimationJointData
        {
            JointName = joint.Name,
            Priority = joint.Priority,
            RotationKeys = rotKeys,
            PositionKeys = posKeys,
        };
    }
}
