using System.Collections.Generic;
using System.Numerics;

namespace SLNG.Core;

/// <summary>
/// Composes avatar joint world poses EXACTLY the way the real SL viewer does — verified against
/// <c>LLXformMatrix::update()</c>/<c>updateMatrix()</c> (indra/llmath/xform.cpp) and
/// <c>LLMatrix4::initAll</c> (indra/llmath/m4math.cpp). This is the one property that matters and
/// that a standard hierarchical-transform engine (Godot's Skeleton3D included) gets wrong for SL
/// avatars: <b>scale does NOT inherit down the joint chain</b>. A joint's own world matrix uses
/// ONLY its own local scale; a parent's scale affects only how far the child's position is offset
/// (one level, not compounded), never the child's own scale. Position and rotation DO inherit
/// normally. Getting this wrong compounds bone-chain scale multiplicatively (plus shear from any
/// rotation mixed in) — for a real asset this measured as a rigged mesh landing 1.2–2.7 m from its
/// own dominant joint and rendering 4–6x too large, while the identical shape data renders
/// correctly in the real viewer.
/// </summary>
public static class SlJointComposer
{
    /// <summary>One joint's composed world pose. <see cref="OwnScale"/> is deliberately NOT
    /// multiplied by any ancestor's scale — see the type's remarks.</summary>
    public readonly record struct JointPose(Vector3 WorldPosition, Quaternion WorldRotation, Vector3 OwnScale);

    /// <summary>
    /// Computes every bone's SL-accurate world pose. <paramref name="skeleton"/>'s bone list MUST
    /// have each parent appear before its children (true for <see cref="AvatarSkeleton"/>, which
    /// parses depth-first and adds each element before recursing into its children).
    /// </summary>
    /// <param name="distortions">Per-bone shape distortion (scale/position delta), e.g. from
    /// AvatarShapeService.ComputeDistortions — added onto the bone's own base scale/position,
    /// exactly like LLPolySkeletalDistortion::apply accumulating onto joint->getScale()/Position().</param>
    /// <param name="positionOverrides">Per-bone LOCAL position override from a worn rigged mesh's
    /// alternate bind matrices — replaces the local position outright (viewer: LLJoint::updatePos),
    /// same rule the existing ApplyShape already applies.</param>
    public static Dictionary<string, JointPose> ComputePoses(
        AvatarSkeleton skeleton,
        IReadOnlyDictionary<string, (Vector3 Scale, Vector3 Position)>? distortions = null,
        IReadOnlyDictionary<string, Vector3>? positionOverrides = null)
    {
        var poses = new Dictionary<string, JointPose>(skeleton.Bones.Count);

        foreach (var bone in skeleton.Bones)
        {
            Vector3 localPos = bone.Position;
            Vector3 localScale = bone.Scale;
            if (distortions != null && distortions.TryGetValue(bone.Name, out var dist))
            {
                localScale += dist.Scale;
                localPos += dist.Position;
            }
            if (positionOverrides != null && positionOverrides.TryGetValue(bone.Name, out var ov))
                localPos = ov;

            var localRot = SlEulerDegToQuaternion(bone.Rotation);

            if (bone.ParentName != null && poses.TryGetValue(bone.ParentName, out var parent))
            {
                // LLXformMatrix::update(): mWorldPosition = mPosition; if (scaleChildOffset)
                // mWorldPosition.scaleVec(parentScale); mWorldPosition *= parentWorldRotation;
                // mWorldPosition += parentWorldPosition. setScaleChildOffset(true) always holds
                // for avatar joints (LLJoint's constructor) — parent scale offsets the CHILD'S
                // position by exactly one level; it never touches the child's OWN scale.
                var scaledLocalPos = localPos * parent.OwnScale;
                var worldPos = Vector3.Transform(scaledLocalPos, parent.WorldRotation) + parent.WorldPosition;
                // mWorldRotation = mRotation * parent->getWorldRotation() under SL's row-vector
                // convention (v*q, leftmost applies first) means: this joint's own rotation
                // applies first, then the parent's. System.Numerics composes the opposite way
                // (Vector3.Transform(v, q1*q2) applies q2 first) — so the equivalent order here
                // is parent.WorldRotation * localRot (parent second is really parent-applied-
                // second-to-the-CHILD-space-vector, i.e. child rotates first in its own frame,
                // then the whole thing is placed into the parent's already-rotated frame).
                var worldRot = Quaternion.Normalize(parent.WorldRotation * localRot);
                poses[bone.Name] = new JointPose(worldPos, worldRot, localScale);
            }
            else
            {
                poses[bone.Name] = new JointPose(localPos, localRot, localScale);
            }
        }

        return poses;
    }

    /// <summary>The world matrix a skinning bind should use for this pose — row-vector convention
    /// (v' = v * M), matching LLMatrix4::initAll(scale, rotation, position): SCALE first (in the
    /// joint's own local axes), THEN rotate, THEN translate. NOT the same as
    /// Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateScale(scale) — order matters.</summary>
    public static Matrix4x4 ToMatrix(JointPose pose)
    {
        var m = Matrix4x4.CreateScale(pose.OwnScale) * Matrix4x4.CreateFromQuaternion(pose.WorldRotation);
        m.Translation = pose.WorldPosition;
        return m;
    }

    /// <summary>SL's <c>mayaQ(x, y, z, LLQuaternion::XYZ)</c> (indra/llmath/llquaternion.cpp):
    /// <c>xQ * yQ * zQ</c> under SL's row-vector convention (v*q, leftmost applies first) means
    /// rotate about SL's X axis FIRST, then Y, then Z LAST. System.Numerics.Quaternion composes
    /// oppositely (Vector3.Transform(v, q1*q2) applies q2 first, then q1) — so the equivalent
    /// order here is zQ * yQ * xQ. Verified consistent with SkeletonBuilder.SlEulerDegToGodotBasis
    /// (same source rule, conjugated into Godot's axis convention) — see
    /// SlJointComposerTests.MatchesGodotBasisConversion.</summary>
    private static Quaternion SlEulerDegToQuaternion(Vector3 slRotDeg)
    {
        float a = DegToRad(slRotDeg.X), b = DegToRad(slRotDeg.Y), c = DegToRad(slRotDeg.Z);
        var xQ = Quaternion.CreateFromAxisAngle(Vector3.UnitX, a);
        var yQ = Quaternion.CreateFromAxisAngle(Vector3.UnitY, b);
        var zQ = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, c);
        return Quaternion.Normalize(zQ * yQ * xQ);
    }

    private static float DegToRad(float deg) => deg * (float)(System.Math.PI / 180.0);
}
