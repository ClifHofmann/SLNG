using System;
using System.Collections.Generic;
using System.Numerics;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>BUG-AVATAR-10. A worn mesh's joint overrides are its own: the joint uses the entry of
/// the greatest mesh id (LLVector3OverrideMap::findActiveOverride, lljoint.cpp:46), and taking the
/// mesh off takes its entries back (removeAttachmentOverridesForObject, llvoavatar.cpp:6996).</summary>
public class JointOverrideSetTests
{
    // Mesh ids that compare in a known order: Guid.CompareTo orders by the trailing bytes first, so
    // build them from the last byte, where the order is the one the numbers say.
    private static Guid Mesh(byte rank)
    {
        var bytes = new byte[16];
        bytes[15] = rank;
        return new Guid(bytes);
    }

    private static KeyValuePair<string, Vector3> At(string joint, float x, float y, float z) => new(joint, new Vector3(x, y, z));

    [Fact]
    public void AMeshesOverridesAreInTheEffectiveView()
    {
        var set = new JointOverrideSet();

        Assert.True(set.Apply(Mesh(1), new[] { At("mKneeLeft", 0.1f, 0f, -0.05f) }, Array.Empty<string>()));

        Assert.Equal(new Vector3(0.1f, 0f, -0.05f), set.Positions["mKneeLeft"]);
    }

    [Fact]
    public void RemovingTheMeshTakesItsOverridesAndLocksBack()
    {
        // The reported bug: a pet's skeleton stayed on the wearer after she took the pet off.
        var set = new JointOverrideSet();
        set.Apply(Mesh(1), new[] { At("mHipLeft", 0f, 0.05f, 0f), At("mChest", 0f, 0f, 0.1f) }, new[] { "mHipLeft", "mChest" });
        int before = set.Version;

        Assert.True(set.Remove(Mesh(1)));

        Assert.Empty(set.Positions);
        Assert.Empty(set.ScaleLocks);
        Assert.True(set.Version > before);
        Assert.False(set.Contains(Mesh(1)));
    }

    [Fact]
    public void RemovingAMeshThatNeverContributedChangesNothing()
    {
        var set = new JointOverrideSet();
        set.Apply(Mesh(1), new[] { At("mChest", 0f, 0f, 0.1f) }, Array.Empty<string>());
        int version = set.Version;

        Assert.False(set.Remove(Mesh(2)));

        Assert.Equal(version, set.Version);
        Assert.Equal(new Vector3(0f, 0f, 0.1f), set.Positions["mChest"]);
    }

    [Fact]
    public void TheGreatestMeshIdWinsAJointRegardlessOfArrivalOrder()
    {
        var low = At("mChest", 1f, 0f, 0f);
        var high = At("mChest", 2f, 0f, 0f);

        var lowFirst = new JointOverrideSet();
        lowFirst.Apply(Mesh(1), new[] { low }, Array.Empty<string>());
        lowFirst.Apply(Mesh(9), new[] { high }, Array.Empty<string>());

        var highFirst = new JointOverrideSet();
        highFirst.Apply(Mesh(9), new[] { high }, Array.Empty<string>());
        highFirst.Apply(Mesh(1), new[] { low }, Array.Empty<string>());

        Assert.Equal(high.Value, lowFirst.Positions["mChest"]);
        Assert.Equal(high.Value, highFirst.Positions["mChest"]);
        Assert.True(highFirst.TryGetActiveMesh("mChest", out var owner));
        Assert.Equal(Mesh(9), owner);
    }

    [Fact]
    public void WhenTheWinnerLeavesTheNextGreatestTakesOver()
    {
        var set = new JointOverrideSet();
        set.Apply(Mesh(1), new[] { At("mChest", 1f, 0f, 0f) }, Array.Empty<string>());
        set.Apply(Mesh(9), new[] { At("mChest", 2f, 0f, 0f) }, Array.Empty<string>());

        Assert.True(set.Remove(Mesh(9)));

        Assert.Equal(new Vector3(1f, 0f, 0f), set.Positions["mChest"]);
    }

    [Fact]
    public void AMeshThatLosesEveryJointLeavingChangesNothingVisible()
    {
        // The losing mesh's removal moves no joint, so it must not claim the skeleton changed --
        // that is what spares a reskin of the whole avatar.
        var set = new JointOverrideSet();
        set.Apply(Mesh(1), new[] { At("mChest", 1f, 0f, 0f) }, Array.Empty<string>());
        set.Apply(Mesh(9), new[] { At("mChest", 2f, 0f, 0f) }, Array.Empty<string>());

        Assert.False(set.Remove(Mesh(1)));
    }

    [Fact]
    public void ApplyingTheSameMeshAgainIsNotAChange()
    {
        var set = new JointOverrideSet();
        var overrides = new[] { At("mChest", 0f, 0f, 0.1f) };
        set.Apply(Mesh(1), overrides, new[] { "mChest" });
        int version = set.Version;

        Assert.False(set.Apply(Mesh(1), overrides, new[] { "mChest" }));

        Assert.Equal(version, set.Version);
    }

    [Fact]
    public void AScaleLockHoldsWhileAnyMeshStillAsksForIt()
    {
        var set = new JointOverrideSet();
        set.Apply(Mesh(1), new[] { At("mChest", 0f, 0f, 0.1f) }, new[] { "mChest" });
        set.Apply(Mesh(2), new[] { At("mTorso", 0f, 0f, 0.1f) }, new[] { "mChest" });

        Assert.True(set.Remove(Mesh(1)));            // its position override goes...
        Assert.DoesNotContain("mChest", set.Positions.Keys);
        Assert.Contains("mChest", set.ScaleLocks);   // ...but mesh 2 still pins the scale

        Assert.True(set.Remove(Mesh(2)));
        Assert.DoesNotContain("mChest", set.ScaleLocks);
    }

    [Fact]
    public void AMeshWithOnlyLocksContributesNoPositions()
    {
        // ApplyJointPositionOverrides' path for a skin whose alternate binds do not match its joints.
        var set = new JointOverrideSet();

        Assert.True(set.Apply(Mesh(1), Array.Empty<KeyValuePair<string, Vector3>>(), new[] { "mHead" }));

        Assert.Empty(set.Positions);
        Assert.Contains("mHead", set.ScaleLocks);
    }

    [Fact]
    public void AMeshThatNowSaysNothingStopsContributing()
    {
        var set = new JointOverrideSet();
        set.Apply(Mesh(1), new[] { At("mChest", 0f, 0f, 0.1f) }, Array.Empty<string>());

        Assert.True(set.Apply(Mesh(1), Array.Empty<KeyValuePair<string, Vector3>>(), Array.Empty<string>()));

        Assert.Empty(set.Positions);
        Assert.False(set.Contains(Mesh(1)));
    }

    [Fact]
    public void ALeftOnlyOverrideIsMirroredToTheRightAndLeavesWithItsMesh()
    {
        var set = new JointOverrideSet();
        set.Apply(Mesh(1), new[] { At("mFootLeft", 0.02f, 0.03f, -0.01f) }, new[] { "mFootLeft" });

        Assert.Equal(new Vector3(0.02f, -0.03f, -0.01f), set.Positions["mFootRight"]);
        Assert.Contains("mFootRight", set.ScaleLocks);
        Assert.False(set.TryGetActiveMesh("mFootRight", out _));

        set.Remove(Mesh(1));

        Assert.Empty(set.Positions);
        Assert.Empty(set.ScaleLocks);
    }

    [Fact]
    public void ASideSomeMeshOverridesItselfIsNotMirrored()
    {
        var set = new JointOverrideSet();
        set.Apply(Mesh(1), new[] { At("mFootLeft", 0.02f, 0.03f, -0.01f) }, Array.Empty<string>());
        set.Apply(Mesh(2), new[] { At("mFootRight", 0.5f, -0.5f, 0.5f) }, Array.Empty<string>());

        Assert.Equal(new Vector3(0.5f, -0.5f, 0.5f), set.Positions["mFootRight"]);
        Assert.Equal(new Vector3(0.02f, 0.03f, -0.01f), set.Positions["mFootLeft"]);

        // ...and when the mesh that supplied the real right side leaves, the mirror comes back.
        set.Remove(Mesh(2));

        Assert.Equal(new Vector3(0.02f, -0.03f, -0.01f), set.Positions["mFootRight"]);
    }

    [Fact]
    public void ClearForgetsEveryMesh()
    {
        var set = new JointOverrideSet();
        set.Apply(Mesh(1), new[] { At("mChest", 0f, 0f, 0.1f) }, new[] { "mChest" });
        set.Apply(Mesh(2), new[] { At("mTorso", 0f, 0f, 0.1f) }, Array.Empty<string>());

        Assert.True(set.Clear());

        Assert.Empty(set.Positions);
        Assert.Empty(set.ScaleLocks);
        Assert.Equal(0, set.MeshCount);
        Assert.False(set.Clear());
    }
}
