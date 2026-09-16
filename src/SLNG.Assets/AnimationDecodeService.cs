using System;
using System.Numerics;
using System.Text;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

namespace SLNG.Assets;

/// <summary>
/// Decodes raw binary BVH animation data (Second Life .anim format) into an
/// engine-neutral <see cref="AnimationData"/> structure.
///
/// Rotation keys in the SL binary BVH format store compressed quaternions as
/// Vector3 (X, Y, Z) — the W component is derived: W = √(1 − x² − y² − z²).
/// Keyframe timestamps are quantized across [0.0, Length] (matching LLKeyframeMotion).
/// </summary>
public static class AnimationDecodeService
{
    private const float LL_MAX_PELVIS_OFFSET = 5.0f;

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
            int i = 0;
            ushort version = ReadUInt16(animationBytes, ref i);
            ushort subVersion = ReadUInt16(animationBytes, ref i);
            int priority = ReadInt32(animationBytes, ref i);
            float length = ReadSingle(animationBytes, ref i);
            string emoteName = ReadNullTerminatedString(animationBytes, ref i);
            float inPoint = ReadSingle(animationBytes, ref i);
            float outPoint = ReadSingle(animationBytes, ref i);
            bool loop = ReadInt32(animationBytes, ref i) != 0;
            float easeIn = ReadSingle(animationBytes, ref i);
            float easeOut = ReadSingle(animationBytes, ref i);
            uint handPose = ReadUInt32(animationBytes, ref i);
            uint jointCount = ReadUInt32(animationBytes, ref i);

            if (jointCount > 1000) return null;

            var joints = new AnimationJointData[jointCount];
            for (int j = 0; j < jointCount; j++)
            {
                string jointName = ReadNullTerminatedString(animationBytes, ref i);
                int jointPriority = ReadInt32(animationBytes, ref i);

                int rotKeyCount = ReadInt32(animationBytes, ref i);
                if (rotKeyCount < 0 || rotKeyCount > 100000) return null;

                var rotKeys = new RotationKeyframe[rotKeyCount];
                for (int k = 0; k < rotKeyCount; k++)
                {
                    ushort tShort = ReadUInt16(animationBytes, ref i);
                    ushort rx = ReadUInt16(animationBytes, ref i);
                    ushort ry = ReadUInt16(animationBytes, ref i);
                    ushort rz = ReadUInt16(animationBytes, ref i);

                    // Real SL viewer (llkeyframemotion.cpp:1589): time = U16_to_F32(time_short, 0.f, duration).
                    // LibreMetaverse had a bug mapping across [InPoint, OutPoint], which collapsed all keys
                    // to InPoint whenever InPoint == OutPoint (common on pose stands and holds).
                    float t = length > 0f ? U16ToF32(tShort, 0.0f, length) : 0.0f;
                    float x = U16ToF32(rx, -1.0f, 1.0f);
                    float y = U16ToF32(ry, -1.0f, 1.0f);
                    float z = U16ToF32(rz, -1.0f, 1.0f);

                    rotKeys[k] = new RotationKeyframe(t, UnpackRotation(x, y, z));
                }

                int posKeyCount = ReadInt32(animationBytes, ref i);
                if (posKeyCount < 0 || posKeyCount > 100000) return null;

                var posKeys = new PositionKeyframe[posKeyCount];
                for (int k = 0; k < posKeyCount; k++)
                {
                    ushort tShort = ReadUInt16(animationBytes, ref i);
                    ushort px = ReadUInt16(animationBytes, ref i);
                    ushort py = ReadUInt16(animationBytes, ref i);
                    ushort pz = ReadUInt16(animationBytes, ref i);

                    // Real SL viewer (llkeyframemotion.cpp:1720, 1761-1763):
                    // pos_key.mTime = U16_to_F32(time_short, 0.f, duration);
                    // pos_key.mPosition = U16_to_F32(p, -LL_MAX_PELVIS_OFFSET, LL_MAX_PELVIS_OFFSET);
                    float t = length > 0f ? U16ToF32(tShort, 0.0f, length) : 0.0f;
                    float rawX = U16ToF32(px, -0.5f, 1.5f);
                    float rawY = U16ToF32(py, -0.5f, 1.5f);
                    float rawZ = U16ToF32(pz, -0.5f, 1.5f);

                    posKeys[k] = new PositionKeyframe(t, UnpackPosition(rawX, rawY, rawZ));
                }

                joints[j] = new AnimationJointData
                {
                    JointName = jointName,
                    Priority = jointPriority,
                    RotationKeys = rotKeys,
                    PositionKeys = posKeys,
                };
            }

            return new AnimationData
            {
                Length = length,
                Loop = loop,
                InPoint = inPoint,
                OutPoint = outPoint,
                EaseInTime = easeIn,
                EaseOutTime = easeOut,
                Priority = priority,
                Joints = joints,
            };
        }
        catch (Exception)
        {
            // Malformed animation data — swallow and return null.
            return null;
        }
    }

    private static ushort ReadUInt16(byte[] data, ref int offset)
    {
        ushort val = (ushort)(data[offset] | (data[offset + 1] << 8));
        offset += 2;
        return val;
    }

    private static int ReadInt32(byte[] data, ref int offset)
    {
        int val = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);
        offset += 4;
        return val;
    }

    private static uint ReadUInt32(byte[] data, ref int offset)
    {
        uint val = (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
        offset += 4;
        return val;
    }

    private static float ReadSingle(byte[] data, ref int offset)
    {
        float val = BitConverter.ToSingle(data, offset);
        offset += 4;
        return val;
    }

    private static string ReadNullTerminatedString(byte[] data, ref int offset)
    {
        int start = offset;
        while (offset < data.Length && data[offset] != 0)
        {
            offset++;
        }
        string result = Encoding.UTF8.GetString(data, start, offset - start);
        if (offset < data.Length) offset++; // skip null byte
        return result;
    }

    internal static float U16ToF32(ushort val, float lower, float upper)
    {
        const float ONE_OVER_U16_MAX = 1.0f / (float)ushort.MaxValue;
        float fval = val * ONE_OVER_U16_MAX;
        float delta = upper - lower;
        fval *= delta;
        fval += lower;

        float maxError = delta * ONE_OVER_U16_MAX;
        if (MathF.Abs(fval) < maxError)
            fval = 0.0f;

        return fval;
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

    /// <summary>
    /// Converts a raw position vector decoded with range [-0.5, 1.5], mapping neutral 0m to 0.5,
    /// back to Second Life meters with range [-5.0, +5.0] meters (LL_MAX_PELVIS_OFFSET).
    /// Formula: meters = (val - 0.5f) * 5.0f.
    /// </summary>
    internal static Vector3 UnpackPosition(float x, float y, float z)
    {
        return new Vector3(
            (x - 0.5f) * 5.0f,
            (y - 0.5f) * 5.0f,
            (z - 0.5f) * 5.0f
        );
    }
}
