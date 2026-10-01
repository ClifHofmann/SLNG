using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>FEAT-ANIMESH-01. Only the ROOT's flag decides, and a root that is not known yet is
/// "not animesh yet" -- never a fall back to the child's own block.</summary>
public class AnimatedMeshLinksetTests
{
    private const ulong Region = 11437119954698752UL;

    private static Entity Prim(World world, uint localId, uint parentLocalId = 0, bool animated = false)
    {
        var e = world.GetOrCreateEntity(Region, localId);
        e.SetComponent(new TransformComponent { ParentLocalId = parentLocalId });
        e.SetComponent(new PrimitiveComponent(System.Numerics.Vector3.One, profileCurve: 0)
        {
            IsAnimatedMesh = animated,
        });
        return e;
    }

    [Fact]
    public void AFlaggedRootIsAnimated()
    {
        var world = new World();
        var root = Prim(world, 1, animated: true);

        Assert.True(AnimatedMeshLinkset.IsAnimatedPart(world, root, out var found));
        Assert.Same(root, found);
    }

    [Fact]
    public void AnUnflaggedRootIsNot()
    {
        var world = new World();
        var root = Prim(world, 1, animated: false);

        Assert.False(AnimatedMeshLinkset.IsAnimatedPart(world, root, out var found));
        Assert.Same(root, found);
    }

    [Fact]
    public void AChildFollowsItsRootsFlag()
    {
        var world = new World();
        var root = Prim(world, 1, animated: true);
        var child = Prim(world, 2, parentLocalId: 1, animated: false);

        Assert.True(AnimatedMeshLinkset.IsAnimatedPart(world, child, out var found));
        Assert.Same(root, found);
    }

    [Fact]
    public void AChildsOwnFlagCountsForNothing()
    {
        // The viewer ignores the block on a child. A root that is not animesh makes the whole
        // linkset an ordinary one, whatever a child's ExtraParams said.
        var world = new World();
        Prim(world, 1, animated: false);
        var child = Prim(world, 2, parentLocalId: 1, animated: true);

        Assert.False(AnimatedMeshLinkset.IsAnimatedPart(world, child, out _));
    }

    [Fact]
    public void AChildWhoseRootHasNotArrivedIsNotAnimatedYet()
    {
        var world = new World();
        var child = Prim(world, 2, parentLocalId: 1, animated: true);

        Assert.False(AnimatedMeshLinkset.IsAnimatedPart(world, child, out var found));
        Assert.Null(found);

        // ...and becomes one the moment the root shows up flagged.
        Prim(world, 1, animated: true);
        Assert.True(AnimatedMeshLinkset.IsAnimatedPart(world, child, out _));
    }

    [Fact]
    public void AWornLinksetIsNeverAnimatedWorldObject()
    {
        var world = new World();
        var avatar = world.GetOrCreateEntity(Region, 9);
        avatar.SetComponent(new AvatarComponent(System.Guid.NewGuid(), "Test", "Avatar", false));
        var attached = Prim(world, 10, parentLocalId: 9, animated: true);

        Assert.False(AnimatedMeshLinkset.IsAnimatedPart(world, attached, out var found));
        Assert.Null(found);
    }

    [Fact]
    public void ACycleDoesNotHangAndAnswersNo()
    {
        var world = new World();
        var a = Prim(world, 1, parentLocalId: 2, animated: true);
        Prim(world, 2, parentLocalId: 1, animated: true);

        Assert.False(AnimatedMeshLinkset.IsAnimatedPart(world, a, out var found));
        Assert.Null(found);
    }
}
