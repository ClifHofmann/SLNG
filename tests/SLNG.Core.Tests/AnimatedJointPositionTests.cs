using System.Numerics;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>BUG-ANIM-07. What a position key does to a joint: an ABSOLUTE local position for every
/// joint (llkeyframemotion.cpp:414-417, llpose.cpp:388, lljoint.h:233) beating a worn mesh's
/// joint-position override while it drives, scaled by the parent's own scale; the pelvis alone is
/// measured from the skeleton root.</summary>
public class AnimatedJointPositionTests
{
    private const float Tolerance = 1e-6f;
    private static readonly Vector3 One = Vector3.One;

    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, Tolerance);
        Assert.Equal(expected.Y, actual.Y, Tolerance);
        Assert.Equal(expected.Z, actual.Z, Tolerance);
    }

    [Fact]
    public void AnyJointButThePelvis_TakesTheKeyAsItsAbsolutePosition()
    {
        // mHindLimb2Left from the add-on HUD pose: the key IS the local position.
        var rest = new Vector3(0.1f, 0f, -0.2f);
        var key = new Vector3(0f, 0.006f, -0.023f);

        var result = AnimatedJointPosition.Resolve(false, AnimatedJointPosition.PelvisMode.RestPlusKey, rest, key, One);

        AssertVector(key, result);
    }

    [Fact]
    public void AKeyBeatsAMeshJointOverride()
    {
        // The worn mesh moved mHindLimb1Left to (0.30, 0.05, -0.10) (its override is the rest).
        // While the HUD pose drives the joint, its key (0.214, 0, -0.138) is where it sits.
        var meshOverride = new Vector3(0.30f, 0.05f, -0.10f);
        var key = new Vector3(0.214f, 0f, -0.138f);

        var result = AnimatedJointPosition.Resolve(false, AnimatedJointPosition.PelvisMode.RestPlusKey, meshOverride, key, One);

        AssertVector(key, result);
        Assert.NotEqual(meshOverride, result);
    }

    [Fact]
    public void TheRestIsNeverAddedToAKey()
    {
        // A tongue key equals the skeleton default (mFaceTongueTip 0.022, 0, 0.007). rest + key would
        // double it -- BUG-ANIM-01's "dummy tracks" -- absolute puts the joint where it already was.
        var defaultPosition = new Vector3(0.022f, 0f, 0.007f);

        var result = AnimatedJointPosition.Resolve(false, AnimatedJointPosition.PelvisMode.RestPlusKey, defaultPosition, defaultPosition, One);

        AssertVector(defaultPosition, result);
        Assert.NotEqual(defaultPosition + defaultPosition, result);
    }

    [Fact]
    public void TheParentsOwnScaleScalesTheKeyPerAxis()
    {
        // ApplyShape scales a child's offset by its parent's scale (slPos *= parentScale): the key
        // gets the same treatment, axis by axis in SL space.
        var key = new Vector3(0.2f, 0.4f, -0.1f);
        var parentScale = new Vector3(2f, 0.5f, 3f);

        var result = AnimatedJointPosition.Resolve(false, AnimatedJointPosition.PelvisMode.RestPlusKey, Vector3.Zero, key, parentScale);

        AssertVector(new Vector3(0.4f, 0.2f, -0.3f), result);
    }

    [Fact]
    public void ThePelvis_IsStillRestPlusKey()
    {
        var rest = new Vector3(0f, 0f, 1.067f);
        var key = new Vector3(0.05f, -0.1f, -0.4f);

        var result = AnimatedJointPosition.Resolve(true, AnimatedJointPosition.PelvisMode.RestPlusKey, rest, key, new Vector3(9f, 9f, 9f));

        AssertVector(rest + key, result);
    }

    [Fact]
    public void ThePelvisOfAControlAvatar_IsTheKeyAlone()
    {
        var rest = new Vector3(0f, 0f, 1.067f);
        var key = new Vector3(0f, 0f, 0.145f);

        var result = AnimatedJointPosition.Resolve(true, AnimatedJointPosition.PelvisMode.Absolute, rest, key, One);

        AssertVector(key, result);
    }

    [Fact]
    public void ThePelvisIgnoresTheParentScale()
    {
        // The pelvis has no parent joint; its path must not pick up a stray table entry.
        var rest = new Vector3(0f, 0f, 1f);
        var key = new Vector3(0.1f, 0.2f, 0.3f);

        AssertVector(rest + key, AnimatedJointPosition.Resolve(true, AnimatedJointPosition.PelvisMode.RestPlusKey, rest, key, new Vector3(0.1f, 0.1f, 0.1f)));
    }

    [Theory]
    [InlineData("mPelvis", true)]
    [InlineData("mTorso", false)]
    [InlineData("mHindLimb1Left", false)]
    [InlineData("mpelvis", false)]
    [InlineData("", false)]
    public void OnlyMPelvisIsThePelvis(string name, bool expected)
    {
        Assert.Equal(expected, AnimatedJointPosition.IsPelvis(name));
    }
}
