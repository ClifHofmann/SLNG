using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// FEAT-SEC-04. What is worth pinning here is not arithmetic — there is none — but the three
/// decisions that are easy to get wrong the next time someone touches this: that a child answers
/// with its root's flags, that Modify and Move are separate questions, and that an unknown
/// object answers no.
/// </summary>
public class EditPermissionTests
{
    private const ulong Region = 11437119954698752UL;

    private static Entity Prim(
        World world, uint localId, uint parentLocalId = 0,
        bool canModify = false, bool canMove = false, bool isOwner = false)
    {
        var e = world.GetOrCreateEntity(Region, localId);
        e.SetComponent(new TransformComponent { ParentLocalId = parentLocalId });
        e.SetComponent(new PrimitiveComponent(System.Numerics.Vector3.One, profileCurve: 0)
        {
            YouCanModify = canModify,
            YouCanMove = canMove,
            YouAreOwner = isOwner,
        });
        return e;
    }

    [Fact]
    public void ReadsTheObjectsOwnFlagWhenItIsNotLinked()
    {
        var world = new World();
        var e = Prim(world, 1, canModify: true);

        Assert.True(EditPermission.CanModify(world, e));
    }

    [Fact]
    public void RefusesWhenTheSimSaysNo()
    {
        var world = new World();
        var e = Prim(world, 1, canModify: false);

        Assert.False(EditPermission.CanModify(world, e));
    }

    /// <summary>The viewer recurses through getParent() for permModify. A child prim's own flag
    /// is not the answer — an "edit linked parts" UI that asked the child would let go of the
    /// rule the moment someone selected one.</summary>
    [Fact]
    public void AChildAnswersWithItsRootsFlag()
    {
        var world = new World();
        Prim(world, 1, canModify: true);                       // root: allowed
        var child = Prim(world, 2, parentLocalId: 1, canModify: false);  // child's own flag: denied

        Assert.True(EditPermission.CanModify(world, child));
        Assert.Equal(1u, EditPermission.RootFor(world, child).LocalId);
    }

    [Fact]
    public void AChildOfADeniedRootIsDenied()
    {
        var world = new World();
        Prim(world, 1, canModify: false);
        var child = Prim(world, 2, parentLocalId: 1, canModify: true);

        Assert.False(EditPermission.CanModify(world, child));
    }

    [Fact]
    public void WalksMoreThanOneLevel()
    {
        var world = new World();
        Prim(world, 1, canModify: true);
        Prim(world, 2, parentLocalId: 1);
        var grandchild = Prim(world, 3, parentLocalId: 2);

        Assert.Equal(1u, EditPermission.RootFor(world, grandchild).LocalId);
        Assert.True(EditPermission.CanModify(world, grandchild));
    }

    /// <summary>A child whose root has not arrived yet, which is ordinary during region entry.
    /// Falling back to the child is the conservative direction: the sim sets a child's flags too,
    /// so the cost is staler data, not a wrongly granted edit.</summary>
    [Fact]
    public void FallsBackToTheObjectItselfWhenTheRootIsMissing()
    {
        var world = new World();
        var orphan = Prim(world, 2, parentLocalId: 999, canModify: true);

        Assert.Equal(2u, EditPermission.RootFor(world, orphan).LocalId);
        Assert.True(EditPermission.CanModify(world, orphan));
    }

    /// <summary>Updates can arrive out of order and a malformed linkset can point at itself.
    /// Neither may hang the caller — this runs on the render thread.</summary>
    [Fact]
    public void SelfParentDoesNotLoop()
    {
        var world = new World();
        var e = Prim(world, 1, parentLocalId: 1, canModify: true);

        Assert.Equal(1u, EditPermission.RootFor(world, e).LocalId);
        Assert.True(EditPermission.CanModify(world, e));
    }

    [Fact]
    public void TwoPrimsPointingAtEachOtherDoNotLoop()
    {
        var world = new World();
        Prim(world, 1, parentLocalId: 2);
        var b = Prim(world, 2, parentLocalId: 1);

        // The value does not matter; not hanging does.
        _ = EditPermission.RootFor(world, b);
        _ = EditPermission.CanModify(world, b);
    }

    /// <summary>FEAT-UI-23: a worn item's root prim hangs off the AVATAR wearing it. The walk
    /// has to stop there -- an avatar carries no PrimitiveComponent, so continuing into it
    /// answered "no" to every permission question about anything worn: no gizmo on your own hat,
    /// disabled size fields, "NOT yours" over an object you created. Viewer parity:
    /// getRootEdit() walks up "while (mParent && !mParent->isAvatar())".</summary>
    [Fact]
    public void AWornItemAnswersWithItsOwnRootNotTheAvatar()
    {
        var world = new World();

        var avatar = world.GetOrCreateEntity(Region, 100);
        avatar.SetComponent(new TransformComponent());
        avatar.SetComponent(new AvatarComponent(System.Guid.NewGuid(), "Test", "Resident", isLocalAgent: true));

        // the attachment's root prim, worn: its parent is the avatar
        var worn = Prim(world, 1, parentLocalId: 100, canModify: true, canMove: true, isOwner: true);
        // and a linked part of that same worn object
        var wornChild = Prim(world, 2, parentLocalId: 1);

        Assert.Equal(1u, EditPermission.RootFor(world, worn).LocalId);
        Assert.Equal(1u, EditPermission.RootFor(world, wornChild).LocalId);
        Assert.True(EditPermission.CanModify(world, worn));
        Assert.True(EditPermission.CanMove(world, wornChild));
        Assert.True(EditPermission.IsOwner(world, wornChild));
    }

    /// <summary>Modify and Move are distinct bits on the wire because a no-modify object still
    /// accepts transform edits. A UI gating both on one of them is wrong in one direction.</summary>
    [Fact]
    public void ModifyAndMoveAreIndependent()
    {
        var world = new World();
        var noModifyButMovable = Prim(world, 1, canModify: false, canMove: true);

        Assert.False(EditPermission.CanModify(world, noModifyButMovable));
        Assert.True(EditPermission.CanMove(world, noModifyButMovable));
    }

    [Fact]
    public void OwnershipComesFromTheSimsOwnBit()
    {
        var world = new World();
        var mine = Prim(world, 1, isOwner: true);
        var theirs = Prim(world, 2, isOwner: false);

        Assert.True(EditPermission.IsOwner(world, mine));
        Assert.False(EditPermission.IsOwner(world, theirs));
    }

    [Fact]
    public void UnknownObjectIsRefusedRatherThanAssumedEditable()
    {
        var world = new World();

        Assert.False(EditPermission.CanModify(world, null));
        Assert.False(EditPermission.CanMove(world, null));
        Assert.False(EditPermission.IsOwner(world, null));
    }

    /// <summary>An entity with no PrimitiveComponent at all -- e.g. seen only through a terse
    /// update so far. Refusing is the only defensible answer.</summary>
    [Fact]
    public void EntityWithoutAPrimitiveComponentIsRefused()
    {
        var world = new World();
        var bare = world.GetOrCreateEntity(Region, 7);

        Assert.False(EditPermission.CanModify(world, bare));
    }
}
