using System.Collections.Generic;
using System.Linq;
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
    /// <param name="scaleLockedBones">Bones whose SCALE is pinned to the skeleton's default by a
    /// worn rigged mesh that declares <c>lock_scale_if_joint_position</c> — see
    /// <see cref="IsScaleLocked"/>.</param>
    public static Dictionary<string, JointPose> ComputePoses(
        AvatarSkeleton skeleton,
        IReadOnlyDictionary<string, (Vector3 Scale, Vector3 Position)>? distortions = null,
        IReadOnlyDictionary<string, Vector3>? positionOverrides = null,
        IReadOnlyCollection<string>? scaleLockedBones = null)
    {
        var poses = new Dictionary<string, JointPose>(skeleton.Bones.Count);

        foreach (var bone in skeleton.Bones)
        {
            Vector3 localPos = bone.Position;
            Vector3 localScale = bone.Scale;
            if (distortions != null && distortions.TryGetValue(bone.Name, out var dist))
            {
                if (!IsScaleLocked(scaleLockedBones, bone.Name)) localScale += dist.Scale;
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

    /// <summary>True when <paramref name="boneName"/>'s SCALE must stay at the skeleton's default,
    /// i.e. every shape slider's skeletal scale distortion on it is discarded (BUG-AVATAR-07).
    ///
    /// <para>Viewer rule, verified in source: a worn rigged mesh whose skin section sets
    /// <c>lock_scale_if_joint_position</c> makes <c>LLVOAvatar::addAttachmentOverridesForObject</c>
    /// (indra/newview/llvoavatar.cpp) call <c>pJoint-&gt;addAttachmentScaleOverride(
    /// pJoint-&gt;getDefaultScale(), mesh_id, ...)</c> for every joint it also gives an
    /// above-threshold POSITION override. <c>LLPolySkeletalDistortion::apply</c> then writes the
    /// shape's accumulated scale through <c>LLJoint::setScale(newScale, /*apply_attachment_
    /// overrides=*/true)</c> (indra/llcharacter/lljoint.cpp), and that setter REPLACES the
    /// requested scale with the active override — so the slider loses. <c>getDefaultScale()</c> is
    /// <c>avatar_skeleton.xml</c>'s own <c>scale</c> for the bone (set once in
    /// <c>LLAvatarAppearance::allocateCharacterJoints</c>), which is exactly this port's
    /// <c>BoneDefinition.Scale</c> — hence "skip the distortion delta", not "force 1,1,1".</para>
    ///
    /// <para>The lock is a property of the JOINT, not of the mesh that asked for it: it applies to
    /// every mesh skinned to that joint. A fitted mesh body (Maitreya Lara measured live: 51 of its
    /// 52 joints above threshold, including mHead/mNeck/mChest/mTorso) therefore also freezes the
    /// scale a worn mesh HEAD renders at.</para></summary>
    public static bool IsScaleLocked(IReadOnlyCollection<string>? scaleLockedBones, string boneName)
    {
        if (scaleLockedBones == null || scaleLockedBones.Count == 0) return false;
        // Prefer the O(1) path when the caller passed a real set; fall back to a scan otherwise.
        return scaleLockedBones is IReadOnlySet<string> set ? set.Contains(boneName)
                                                           : scaleLockedBones.Contains(boneName);
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

    /// <summary>Pelvis-to-foot height and total body height, computed EXACTLY like
    /// <c>LLAvatarAppearance::computeBodySize()</c> (indra/llappearance/llavatarappearance.cpp,
    /// ~471-556) — verified against source line-by-line, including a genuine asymmetry the real
    /// formula has that a "geometrically clean" telescoped chain sum does NOT reproduce: the first
    /// term of <see cref="PelvisToFoot"/> is ADDED while the other three are SUBTRACTED. (Sanity
    /// check performed while implementing this: composing the same chain through
    /// <see cref="ComputePoses"/> — which correctly telescopes position through each parent's own
    /// scale — gives a materially different number for the default skeleton, ~1.061 m vs. this
    /// method's ~0.979 m. That is not a bug in <see cref="ComputePoses"/>; it means LL's own
    /// <c>mPelvisToFoot</c> is NOT a true telescoped chain distance, and viewer parity requires
    /// reproducing its literal per-term signs, not "fixing" them.) Used every frame by the real
    /// viewer to correct the avatar root's rendered Z so the pelvis (not geometric mid-body) lines
    /// up with the network position — see LLVOAvatar::updateRootPositionAndRotation.</summary>
    public readonly record struct BodySize(float PelvisToFoot, float BodySizeZ);

    /// <summary>Computes <see cref="BodySize"/> from the SAME per-bone base position/scale +
    /// shape distortion + joint-position-override inputs <see cref="ComputePoses"/> takes — no new
    /// data source. SL uses the LEFT leg (mHipLeft/mKneeLeft/mAnkleLeft/mFootLeft) for this; the
    /// right leg is assumed symmetric and never consulted by the real viewer either.</summary>
    public static BodySize ComputeBodySize(
        AvatarSkeleton skeleton,
        IReadOnlyDictionary<string, (Vector3 Scale, Vector3 Position)>? distortions = null,
        IReadOnlyDictionary<string, Vector3>? positionOverrides = null,
        IReadOnlyCollection<string>? scaleLockedBones = null)
    {
        float LocalZ(string name)
        {
            var bone = skeleton.GetBone(name);
            if (bone == null) return 0f;
            var pos = bone.Position;
            if (distortions != null && distortions.TryGetValue(name, out var d)) pos += d.Position;
            if (positionOverrides != null && positionOverrides.TryGetValue(name, out var ov)) pos = ov;
            return pos.Z;
        }

        float OwnScaleZ(string name)
        {
            var bone = skeleton.GetBone(name);
            if (bone == null) return 1f;
            var scale = bone.Scale;
            // Same lock the render path honors (IsScaleLocked) — the viewer measures mBodySize off
            // joint->getScale(), which an attachment scale override has already replaced by then.
            if (distortions != null && !IsScaleLocked(scaleLockedBones, name) &&
                distortions.TryGetValue(name, out var d)) scale += d.Scale;
            return scale.Z;
        }

        float pelvisScaleZ = OwnScaleZ("mPelvis");
        float hipScaleZ = OwnScaleZ("mHipLeft");
        float kneeScaleZ = OwnScaleZ("mKneeLeft");
        float ankleScaleZ = OwnScaleZ("mAnkleLeft");

        float hipZ = LocalZ("mHipLeft");
        float kneeZ = LocalZ("mKneeLeft");
        float ankleZ = LocalZ("mAnkleLeft");
        float footZ = LocalZ("mFootLeft");

        // Literal LL formula (llavatarappearance.cpp:528-531) — first term added, rest
        // subtracted. Do not "symmetrize" this; see BodySize's doc comment.
        float pelvisToFoot = hipZ * pelvisScaleZ
                            - kneeZ * hipScaleZ
                            - ankleZ * kneeScaleZ
                            - footZ * ankleScaleZ;

        float headScaleZ = OwnScaleZ("mHead");
        float neckScaleZ = OwnScaleZ("mNeck");
        float chestScaleZ = OwnScaleZ("mChest");
        float torsoScaleZ = OwnScaleZ("mTorso");

        float skullZ = LocalZ("mSkull");
        float headZ = LocalZ("mHead");
        float neckZ = LocalZ("mNeck");
        float chestZ = LocalZ("mChest");
        float torsoZ = LocalZ("mTorso");

        const float Sqrt2 = 1.41421356f; // F_SQRT2 — approximate correction to top of head.
        float bodySizeZ = pelvisToFoot
                         + Sqrt2 * (skullZ * headScaleZ)
                         + headZ * neckScaleZ
                         + neckZ * chestScaleZ
                         + chestZ * torsoScaleZ
                         + torsoZ * pelvisScaleZ;

        return new BodySize(pelvisToFoot, bodySizeZ);
    }
}
