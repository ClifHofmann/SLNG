using System;
using System.Numerics;

namespace SLNG.Assets;

/// <summary>
/// Engine-neutral representation of a decoded Second Life animation.
/// All coordinate-system conversions (SL Z-up → neutral) are applied at decode time.
/// No LibreMetaverse types cross this boundary.
/// </summary>
public sealed class AnimationData
{
    public float Length { get; init; }
    public bool Loop { get; init; }
    public float InPoint { get; init; }
    public float OutPoint { get; init; }
    public float EaseInTime { get; init; }
    public float EaseOutTime { get; init; }
    public int Priority { get; init; }
    public AnimationJointData[] Joints { get; init; } = Array.Empty<AnimationJointData>();
}

/// <summary>
/// Keyframe data for a single joint within an animation.
/// </summary>
public sealed class AnimationJointData
{
    public string JointName { get; init; } = string.Empty;
    public int Priority { get; init; }
    public RotationKeyframe[] RotationKeys { get; init; } = Array.Empty<RotationKeyframe>();
    public PositionKeyframe[] PositionKeys { get; init; } = Array.Empty<PositionKeyframe>();
}

/// <summary>Rotation keyframe — fully expanded quaternion (W is precomputed).</summary>
public readonly record struct RotationKeyframe(float Time, Quaternion Rotation);

/// <summary>Position keyframe -- XYZ in Second Life metres. For every joint this is the joint's ABSOLUTE
/// position relative to its parent (the viewer hands the key straight to the joint,
/// llkeyframemotion.cpp:414-417); only mPelvis' is measured from the skeleton root. Not an offset
/// on the rest position -- see BUG-ANIM-07.</summary>
public readonly record struct PositionKeyframe(float Time, Vector3 Position);
