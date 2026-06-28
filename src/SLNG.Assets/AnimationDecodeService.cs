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
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine($"[AnimationDecodeService] Failed to decode animation: {ex.Message}");
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

    private static AnimationJointData ConvertJoint(binBVHJoint joint)
    {
        var rotKeys = new RotationKeyframe[joint.rotationkeys.Length];
        for (int i = 0; i < joint.rotationkeys.Length; i++)
        {
            ref var key = ref joint.rotationkeys[i];
            // SL binary BVH stores compressed quaternion: X, Y, Z
            // Derive W = sqrt(1 - x² - y² - z²), clamped to avoid NaN.
            float x = key.key_element.X;
            float y = key.key_element.Y;
            float z = key.key_element.Z;
            float wSq = 1.0f - (x * x + y * y + z * z);
            float w = wSq > 0f ? MathF.Sqrt(wSq) : 0f;
            rotKeys[i] = new RotationKeyframe(key.time, new Quaternion(x, y, z, w));
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
