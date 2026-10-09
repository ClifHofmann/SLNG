using System;
using System.Numerics;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>BUG-AVATAR-10. An attachment point's rotation is <c>LLQuaternion::setQuat(roll, pitch, yaw)</c>
/// (Z first, then Y, then X), not the joints' <c>mayaQ(XYZ)</c> order. Only Chest and Spine turn about two
/// axes, which is why nothing else showed it.</summary>
public class AttachmentPointRotationTests
{
    private static Vector3 Rotate(Quaternion q, Vector3 v) => Vector3.Transform(v, q);

    private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance = 1e-5f)
        => Assert.True((expected - actual).Length() < tolerance, $"expected {expected}, got {actual}");

    [Fact]
    public void NoRotationIsIdentity()
    {
        var q = AttachmentPointRotation.FromEulerDegrees(Vector3.Zero);
        Assert.True(MathF.Abs(Quaternion.Dot(q, Quaternion.Identity)) > 0.99999f);
    }

    [Fact]
    public void ASingleAxisIsThatRotation()
    {
        // Skull: 0 0 90 -- the only kind of point the old order was ever checked against.
        var q = AttachmentPointRotation.FromEulerDegrees(new Vector3(0f, 0f, 90f));
        AssertNear(Vector3.UnitY, Rotate(q, Vector3.UnitX));
        AssertNear(-Vector3.UnitX, Rotate(q, Vector3.UnitY));
        AssertNear(Vector3.UnitZ, Rotate(q, Vector3.UnitZ));
    }

    [Fact]
    public void ChestIsOneThirdTurnAboutTheDiagonal()
    {
        // avatar_lad.xml: Chest rotation="0 90 90". setQuat gives (0.5, 0.5, 0.5, 0.5) -- 120 degrees about
        // (1, 1, 1) -- so the point's X axis lies along the joint's Y, its Y along Z and its Z along X.
        var q = AttachmentPointRotation.FromEulerDegrees(new Vector3(0f, 90f, 90f));

        Assert.True((new Vector4(q.X, q.Y, q.Z, q.W) - new Vector4(0.5f, 0.5f, 0.5f, 0.5f)).Length() < 1e-5f);
        AssertNear(Vector3.UnitY, Rotate(q, Vector3.UnitX));
        AssertNear(Vector3.UnitZ, Rotate(q, Vector3.UnitY));
        AssertNear(Vector3.UnitX, Rotate(q, Vector3.UnitZ));
    }

    [Fact]
    public void SpineIsTheMirrorOfIt()
    {
        // Spine: rotation="0 -90 90" -> (-0.5, -0.5, 0.5, 0.5).
        var q = AttachmentPointRotation.FromEulerDegrees(new Vector3(0f, -90f, 90f));

        Assert.True((new Vector4(q.X, q.Y, q.Z, q.W) - new Vector4(-0.5f, -0.5f, 0.5f, 0.5f)).Length() < 1e-5f);
        AssertNear(Vector3.UnitY, Rotate(q, Vector3.UnitX));
        AssertNear(-Vector3.UnitZ, Rotate(q, Vector3.UnitY));
        AssertNear(-Vector3.UnitX, Rotate(q, Vector3.UnitZ));
    }

    [Fact]
    public void TwoAxesTurnZFirstThenYThenX()
    {
        // Spelled out as three single-axis turns, so the order is read off the test and not the formula.
        // Z by 90 first: X -> Y. Then Y by 90 (X -> -Z, Z -> X, Y stays): Y stays Y.
        var q = AttachmentPointRotation.FromEulerDegrees(new Vector3(0f, 90f, 90f));
        var zFirst = Quaternion.Concatenate(
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f));

        foreach (var v in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new Vector3(0.3f, -0.2f, 0.5f) })
            AssertNear(Rotate(zFirst, v), Rotate(q, v));
    }

    [Fact]
    public void TheWornDragonLandsWhereFirestormDrawsIt()
    {
        // Ground truth from Firestorm's Edit floater on the worn dragon root (worn on Chest):
        //   Pos <-0.26352, 0.18030, 0.17475>, Rot <279.1, 359.4, 3.6>
        // The Edit floater's Euler display is LLQuaternion::getEulerAngles, the inverse of setQuat, so the
        // rotation is rebuilt with the same function. The root's place in the chest JOINT's frame is the
        // point's own offset plus the object's position turned by the point's rotation (llcontrolavatar.cpp:
        // 182-193); the rotation is obj_rot, then the point's.
        var pointPosition = new Vector3(0.15f, 0f, -0.1f);
        var pointRotation = AttachmentPointRotation.FromEulerDegrees(new Vector3(0f, 90f, 90f));
        var objectPosition = new Vector3(-0.26352f, 0.18030f, 0.17475f);
        var objectRotation = AttachmentPointRotation.FromEulerDegrees(new Vector3(279.1f, 359.4f, 3.6f));

        var rootPosition = pointPosition + Rotate(pointRotation, objectPosition);
        var rootRotation = Quaternion.Concatenate(objectRotation, pointRotation);

        // 32 cm forward, 26 cm to her RIGHT (-Y), at chest height: the dragon in front of her right side...
        AssertNear(new Vector3(0.325f, -0.264f, 0.080f), rootPosition, 2e-3f);
        // ...standing upright (its up is the chest's up), facing the chest's left.
        var up = Rotate(rootRotation, Vector3.UnitZ);
        Assert.True(up.Z > 0.98f, $"root up {up}");
    }

    [Fact]
    public void TheJointOrderWouldHaveLaidItFlatOverTheLeftShoulder()
    {
        // The same inputs with the order the skeleton's joints use (X first, then Y, then Z) -- what
        // AttachPointOffset did -- put the root at the LEFT shoulder with its up pointing backwards.
        var pointPosition = new Vector3(0.15f, 0f, -0.1f);
        var jointOrder = Quaternion.Concatenate(
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f));
        var objectPosition = new Vector3(-0.26352f, 0.18030f, 0.17475f);
        var objectRotation = AttachmentPointRotation.FromEulerDegrees(new Vector3(279.1f, 359.4f, 3.6f));

        var rootPosition = pointPosition + Rotate(jointOrder, objectPosition);
        var up = Rotate(Quaternion.Concatenate(objectRotation, jointOrder), Vector3.UnitZ);

        AssertNear(new Vector3(-0.030f, 0.175f, 0.164f), rootPosition, 2e-3f);
        Assert.True(up.X < -0.9f, $"root up {up}");
    }

    [Fact]
    public void ItIsNotTheJointOrder()
    {
        // The order the skeleton's joints use (X first, then Y, then Z) gives a different frame for Chest.
        var attachment = AttachmentPointRotation.FromEulerDegrees(new Vector3(0f, 90f, 90f));
        var jointOrder = Quaternion.Concatenate(
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f));

        Assert.True(MathF.Abs(Quaternion.Dot(attachment, jointOrder)) < 0.99f);
    }
}
