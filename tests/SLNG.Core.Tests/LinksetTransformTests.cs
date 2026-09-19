using System;
using System.Numerics;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class LinksetTransformTests
{
    private static readonly Quaternion ParentRotation =
        Quaternion.CreateFromYawPitchRoll(0.7f, -0.3f, 1.1f);
    private static readonly Vector3 ParentPosition = new(128f, 64f, 27.5f);

    private static void AssertClose(Vector3 expected, Vector3 actual, float tolerance = 1e-4f)
    {
        Assert.True(Vector3.Distance(expected, actual) < tolerance,
            $"expected {expected}, got {actual}");
    }

    [Fact]
    public void ToLocalIsTheExactInverseOfToWorld()
    {
        var localPosition = new Vector3(0.25f, -1.5f, 3f);
        var localRotation = Quaternion.CreateFromYawPitchRoll(-0.2f, 0.9f, 0.4f);

        var world = LinksetTransform.ToWorld(localPosition, localRotation, ParentPosition, ParentRotation);
        var back = LinksetTransform.ToLocal(world.Position, world.Rotation, ParentPosition, ParentRotation);

        AssertClose(localPosition, back.Position);
        // Compare the rotations by what they do, not by their components: q and -q are the same
        // rotation and a component-wise check would fail on the sign alone.
        AssertClose(Vector3.Transform(Vector3.UnitX, localRotation), Vector3.Transform(Vector3.UnitX, back.Rotation));
        AssertClose(Vector3.Transform(Vector3.UnitZ, localRotation), Vector3.Transform(Vector3.UnitZ, back.Rotation));
    }

    [Fact]
    public void AnUnrotatedParentIsAPlainOffset()
    {
        var world = LinksetTransform.ToWorld(new Vector3(1f, 2f, 3f), Quaternion.Identity,
            new Vector3(10f, 20f, 30f), Quaternion.Identity);

        AssertClose(new Vector3(11f, 22f, 33f), world.Position);
    }

    [Fact]
    public void AQuarterTurnAboutZSwingsTheOffsetSideways()
    {
        // A child one metre out along the root's X. Turn the root 90 degrees about Z and the
        // child must end up one metre out along Y instead -- this is the term that a naive
        // "world minus parent" subtraction gets wrong the moment a linkset is rotated at all.
        var parentRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);

        var world = LinksetTransform.ToWorld(Vector3.UnitX, Quaternion.Identity, Vector3.Zero, parentRotation);

        AssertClose(Vector3.UnitY, world.Position);
        AssertClose(Vector3.UnitX, LinksetTransform.ToLocal(world.Position, world.Rotation, Vector3.Zero, parentRotation).Position);
    }

    [Fact]
    public void TheWorldPositionOfAChildIsNotItsLocalOne()
    {
        // The regression this type exists for. Sending a child's WORLD position as its
        // parent-relative one displaces it by the root's position in the region -- over a
        // hundred metres on an ordinary build, which reads in-world as the linkset falling apart.
        var local = new Vector3(0.5f, 0f, 0.2f);
        var world = LinksetTransform.ToWorld(local, Quaternion.Identity, ParentPosition, Quaternion.Identity);

        Assert.True(Vector3.Distance(world.Position, local) > 100f);
    }
}
