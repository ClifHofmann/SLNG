using System;
using System.Numerics;

namespace SLNG.Core;

/// <summary>
/// BUG-AVATAR-10: the rotation of an avatar attachment point on its joint, from the Euler angles
/// <c>avatar_lad.xml</c> gives it. A worn item's own position and rotation are expressed in THIS frame,
/// so getting it wrong turns and displaces everything worn on the point.
/// </summary>
/// <remarks>
/// <para>The viewer builds an attachment point's rotation with <c>LLQuaternion::setQuat(roll, pitch,
/// yaw)</c> (llvoavatar.cpp:7198-7205, <c>attachment-&gt;setRotation</c>), whose formula is
/// llquaternion.cpp:295-311. That product is <c>qx * qy * qz</c> in Hamilton terms, i.e. as a rotation:
/// <b>Z first, then Y, then X</b>, about fixed axes. The skeleton's JOINT rotations are built another
/// way, <c>mayaQ(x, y, z, XYZ)</c> = <c>xQ * yQ * zQ</c> in the viewer's "first, then" notation, which
/// is X first, then Y, then Z (llavatarappearance.cpp:642; <c>SkeletonBuilder.SlEulerDegToGodotBasis</c>).
/// The two agree for a rotation about one axis and disagree for two, and attachment points were being
/// built with the joint order.</para>
///
/// <para>Of the 55 points in <c>avatar_lad.xml</c> exactly two turn about more than one axis, both on
/// the torso: Chest (id 1, <c>0 90 90</c>) and Spine (id 9, <c>0 -90 90</c>). For those the old order was
/// 120 degrees off, which turned everything worn there and moved it by the distance it sat from the
/// point -- a pet worn on the chest, offset towards the hand, ended up near the shoulder and a metre from
/// where the real viewer draws it.</para>
/// </remarks>
public static class AttachmentPointRotation
{
    /// <summary><c>LLQuaternion::setQuat(roll, pitch, yaw)</c> (llquaternion.cpp:295-311), line for line.
    /// <paramref name="degrees"/> is (roll about X, pitch about Y, yaw about Z), as written in
    /// <c>avatar_lad.xml</c>. The result is a standard active rotation of SL vectors (<c>q v q*</c>).</summary>
    public static Quaternion FromEulerDegrees(Vector3 degrees)
    {
        const float DegToRad = MathF.PI / 180f;
        float roll = degrees.X * DegToRad * 0.5f;
        float pitch = degrees.Y * DegToRad * 0.5f;
        float yaw = degrees.Z * DegToRad * 0.5f;

        float sinX = MathF.Sin(roll), cosX = MathF.Cos(roll);
        float sinY = MathF.Sin(pitch), cosY = MathF.Cos(pitch);
        float sinZ = MathF.Sin(yaw), cosZ = MathF.Cos(yaw);

        return new Quaternion(
            x: sinX * cosY * cosZ + cosX * sinY * sinZ,
            y: cosX * sinY * cosZ - sinX * cosY * sinZ,
            z: cosX * cosY * sinZ + sinX * sinY * cosZ,
            w: cosX * cosY * cosZ - sinX * sinY * sinZ);
    }
}
