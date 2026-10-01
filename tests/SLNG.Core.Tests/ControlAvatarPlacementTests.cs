using System;
using System.Numerics;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// FEAT-ANIMESH-01: the rotation a control avatar is placed with, ported from
/// <c>LLControlAvatar::matchVolumeTransform</c> + <c>LLSkinningUtil::getUnscaledQuaternion</c>.
///
/// The expected values below are NOT read back from the implementation. Each one is either a
/// property that has to hold (a rotation brings a vector back), a hand derivation, or a number
/// worked out separately from the viewer's own algorithm (see the non-uniform case).
/// </summary>
public class ControlAvatarPlacementTests
{
    private const float Tol = 1e-4f;

    private static void AssertClose(Vector3 expected, Vector3 actual, float tolerance = Tol)
    {
        Assert.True(Vector3.Distance(expected, actual) < tolerance,
            $"expected {expected}, got {actual}");
    }

    /// <summary>The effect of a rotation, which is what matters: q and -q are the same rotation.</summary>
    private static void AssertSameRotation(Quaternion expected, Quaternion actual)
    {
        foreach (var v in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new Vector3(0.3f, -0.8f, 0.5f) })
            AssertClose(Vector3.Transform(v, expected), Vector3.Transform(v, actual));
    }

    [Fact]
    public void IdentityBindShapeLeavesTheObjectRotationAlone()
    {
        var obj = Quaternion.CreateFromYawPitchRoll(0.7f, -0.3f, 1.1f);

        AssertSameRotation(obj, ControlAvatarPlacement.WorldRotation(Matrix4x4.Identity, obj));
        AssertSameRotation(Quaternion.Identity, ControlAvatarPlacement.BindRotation(Matrix4x4.Identity));
    }

    [Fact]
    public void AUniformScaleCarriesNoRotation()
    {
        // A mesh uploaded at a different unit scale: the bind shape is 0.01 * I and nothing else.
        var bindShape = Matrix4x4.CreateScale(0.01f);

        AssertSameRotation(Quaternion.Identity, ControlAvatarPlacement.BindRotation(bindShape));
    }

    [Fact]
    public void ARotationWithUniformScaleIsUndoneByTheBindRotation()
    {
        // p * S * R: scale, then rotate (row-vector convention, as in the viewer and in
        // System.Numerics). The rows of the 3x3 are R's rows times the scale, so normalising each
        // row recovers R and bind_rot is its inverse.
        var rotation = Quaternion.CreateFromYawPitchRoll(0.9f, 0.4f, -0.6f);
        var bindShape = Matrix4x4.CreateScale(0.01f) * Matrix4x4.CreateFromQuaternion(rotation);

        var bindRot = ControlAvatarPlacement.BindRotation(bindShape);

        AssertSameRotation(Quaternion.Inverse(rotation), bindRot);
        // And therefore: a vector taken through the bind rotation and then back out of the world
        // rotation lands where it started.
        var v = new Vector3(1f, 2f, 3f);
        AssertClose(v, Vector3.Transform(Vector3.Transform(v, rotation), bindRot));
        AssertClose(v, Vector3.Transform(Vector3.Transform(v, rotation),
            ControlAvatarPlacement.WorldRotation(bindShape, Quaternion.Identity)));
    }

    [Fact]
    public void NonUniformScaleWithARotationIsNormalisedByROW()
    {
        // Rotate 45 degrees about Z, THEN stretch world X by 2:  B = R * S, so column j of R is
        // scaled by s_j and the rows of B are NOT R's rows scaled -- row- and column-normalising
        // give different answers, which is the whole point of this case.
        float h = MathF.Sqrt(0.5f);
        var bindShape = new Matrix4x4(
            2f * h, h, 0f, 0f,
           -2f * h, h, 0f, 0f,
            0f, 0f, 1f, 0f,
            0f, 0f, 0f, 1f);

        var q = ControlAvatarPlacement.BindRotation(bindShape);

        // Worked by hand from the viewer's algorithm: rows -> (2,1,0)/sqrt5, (-2,1,0)/sqrt5, (0,0,1);
        // invert that 3x3 -> [[sqrt5/4, -sqrt5/4, 0], [sqrt5/2, sqrt5/2, 0], [0, 0, 1]]; trace-based
        // extraction gives w = 0.9587950, z = -0.4372680 before normalising, and 1.053797 is the
        // length. (Not a rotation matrix any more, so the extraction is an approximation -- by the
        // viewer's own code, which is what is being matched.)
        Assert.Equal(0f, MathF.Abs(q.X), 5);
        Assert.Equal(0f, MathF.Abs(q.Y), 5);
        float sign = q.W < 0 ? -1f : 1f; // q and -q are the same rotation
        Assert.Equal(0.909838f, sign * q.W, 4);
        Assert.Equal(-0.414964f, sign * q.Z, 4);

        // Column-normalising would have produced exactly -45 degrees (w = 0.92388, z = -0.38268).
        Assert.True(MathF.Abs(sign * q.Z - -0.382683f) > 0.02f,
            "this is the column-normalised answer; the viewer normalises rows");
    }

    [Fact]
    public void ABlenderStyleYUpToZUpBindShapeStandsTheSkeletonUp()
    {
        // The typical animesh upload: authored Y-up, converted to Z-up by a 90 degree turn about X,
        // at 0.01 scale. Row-vector: p * S * Rx(+90) sends the authored up axis (Y) to Z.
        var bindShape = Matrix4x4.CreateScale(0.01f) * Matrix4x4.CreateRotationX(MathF.PI / 2f);
        AssertClose(Vector3.UnitZ, Vector3.Transform(Vector3.UnitY, Matrix4x4.CreateRotationX(MathF.PI / 2f)));

        var world = ControlAvatarPlacement.WorldRotation(bindShape, Quaternion.Identity);

        // bind_rot is Rx(-90): the skeleton's up axis (Z) is turned onto Y, where the mesh's
        // authored up axis was.
        AssertClose(Vector3.UnitY, Vector3.Transform(Vector3.UnitZ, world));
        AssertClose(Vector3.UnitX, Vector3.Transform(Vector3.UnitX, world));
        AssertClose(-Vector3.UnitZ, Vector3.Transform(Vector3.UnitY, world));
    }

    [Theory]
    [InlineData(0)] // all zero
    [InlineData(1)] // one zero-length row
    [InlineData(2)] // two parallel rows: the normalised matrix has no inverse
    [InlineData(3)] // not a number
    public void ASingularBindShapeFallsBackToIdentity(int variant)
    {
        var bindShape = variant switch
        {
            0 => new Matrix4x4(),
            1 => new Matrix4x4(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1),
            2 => new Matrix4x4(1, 0, 0, 0, 2, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1),
            _ => new Matrix4x4(float.NaN, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1),
        };

        AssertSameRotation(Quaternion.Identity, ControlAvatarPlacement.BindRotation(bindShape));

        var obj = Quaternion.CreateFromYawPitchRoll(0.5f, 0.2f, -0.1f);
        AssertSameRotation(obj, ControlAvatarPlacement.WorldRotation(bindShape, obj));
    }

    [Fact]
    public void TheBindRotationIsAppliedFirstThenTheObjectRotation()
    {
        // bind_rot * obj_rot in the viewer's notation: bind first. Chosen so the order shows: a
        // quarter turn about X takes Z to -Y, and a quarter turn about Z then leaves a vector on
        // the Y axis on the X axis. Done the other way round, Z stays on Z.
        var bindShape = Matrix4x4.CreateScale(0.01f) * Matrix4x4.CreateRotationX(-MathF.PI / 2f); // bind_rot = Rx(+90)
        var obj = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);

        var world = ControlAvatarPlacement.WorldRotation(bindShape, obj);

        AssertClose(Vector3.UnitX, Vector3.Transform(Vector3.UnitZ, world));
        AssertSameRotation(world, ControlAvatarPlacement.Compose(
            ControlAvatarPlacement.BindRotation(bindShape), obj));
    }

    [Fact]
    public void ComposesWithAnArbitraryObjectRotationStepByStep()
    {
        var rotation = Quaternion.CreateFromYawPitchRoll(-0.4f, 0.8f, 0.3f);
        var bindShape = Matrix4x4.CreateScale(0.05f, 0.05f, 0.05f) * Matrix4x4.CreateFromQuaternion(rotation);
        var obj = Quaternion.CreateFromYawPitchRoll(2.1f, -0.5f, 0.9f);

        var world = ControlAvatarPlacement.WorldRotation(bindShape, obj);

        var bindRot = ControlAvatarPlacement.BindRotation(bindShape);
        foreach (var v in new[] { Vector3.UnitX, Vector3.UnitY, new Vector3(-1f, 0.5f, 2f) })
        {
            // bind first, then the object's own rotation
            AssertClose(Vector3.Transform(Vector3.Transform(v, bindRot), obj), Vector3.Transform(v, world));
        }
        // The pre-rotation by R undone, what is left is the object's rotation alone.
        AssertClose(Vector3.Transform(Vector3.UnitX, obj),
            Vector3.Transform(Vector3.Transform(Vector3.UnitX, rotation), world));
    }

    [Fact]
    public void EulerDegreesReadsASingleAxisTurnAsThatAxis()
    {
        AssertClose(new Vector3(30f, 0f, 0f), ControlAvatarPlacement.EulerDegrees(
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 6f)), 1e-3f);
        AssertClose(new Vector3(0f, 20f, 0f), ControlAvatarPlacement.EulerDegrees(
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 20f * MathF.PI / 180f)), 1e-3f);
        AssertClose(new Vector3(0f, 0f, 90f), ControlAvatarPlacement.EulerDegrees(
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f)), 1e-3f);
        AssertClose(Vector3.Zero, ControlAvatarPlacement.EulerDegrees(Quaternion.Identity));
    }

    [Fact]
    public void EulerDegreesInvertsTheViewersRollPitchYawOrder()
    {
        // LLQuaternion::getEulerAngles inverts rot = Rz(yaw) * Ry(pitch) * Rx(roll) in the
        // viewer's row-vector order -- LLMatrix3(roll, pitch, yaw) turns about X, then about the
        // OLD Y, then about the ORIGINAL Z, which is Z first when written as fixed-axis turns. The
        // single-axis test above fixes the signs; this one fixes the order, with three distinct
        // angles so a swapped pair cannot hide.
        float d = MathF.PI / 180f;
        var q = ControlAvatarPlacement.Compose(
            ControlAvatarPlacement.Compose(
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 40f * d),
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, 30f * d)),
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, 20f * d));

        AssertClose(new Vector3(20f, 30f, 40f), ControlAvatarPlacement.EulerDegrees(q), 1e-2f);
    }

    [Fact]
    public void TheResultIsAUnitQuaternion()
    {
        var bindShape = Matrix4x4.CreateScale(0.3f, 0.7f, 1.2f) * Matrix4x4.CreateRotationY(0.6f);
        var q = ControlAvatarPlacement.WorldRotation(bindShape, Quaternion.CreateFromYawPitchRoll(1f, 2f, 3f));

        Assert.Equal(1f, q.Length(), 4);
    }
}
