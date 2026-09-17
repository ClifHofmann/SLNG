using Xunit;

namespace SLNG.Core.Tests;

/// <summary>LLVOVolume::hasMediaPermission (llvovolume.cpp:2782-2822) is an if/else-if CHAIN, not
/// a bitwise OR of independent checks. These pin the resulting quirk: with Owner|Group set, an
/// agent who owns the object but is not in its group is DENIED, because the Group branch's bit is
/// present and short-circuits before Owner is ever checked. "Fixing" this into a clean OR would
/// diverge from what the real viewer (and OpenSim's MoapModule, which mirrors the same chain)
/// actually grants.</summary>
public class MediaPermissionEvaluatorTests
{
    [Fact]
    public void None_DeniesEveryone()
    {
        Assert.False(MediaPermissionEvaluator.HasPermission(MediaPermission.None, isOwner: true, isInObjectGroup: true));
    }

    [Fact]
    public void Anyone_AlwaysPasses_RegardlessOfOwnerOrGroup()
    {
        Assert.True(MediaPermissionEvaluator.HasPermission(MediaPermission.Anyone, isOwner: false, isInObjectGroup: false));
    }

    [Fact]
    public void OwnerOnly_GrantsTheOwner_DeniesEveryoneElse()
    {
        Assert.True(MediaPermissionEvaluator.HasPermission(MediaPermission.Owner, isOwner: true, isInObjectGroup: false));
        Assert.False(MediaPermissionEvaluator.HasPermission(MediaPermission.Owner, isOwner: false, isInObjectGroup: false));
    }

    [Fact]
    public void GroupOnly_GrantsGroupMembers_DeniesEveryoneElse()
    {
        Assert.True(MediaPermissionEvaluator.HasPermission(MediaPermission.Group, isOwner: false, isInObjectGroup: true));
        Assert.False(MediaPermissionEvaluator.HasPermission(MediaPermission.Group, isOwner: true, isInObjectGroup: false));
    }

    [Fact]
    public void OwnerAndGroup_DeniesTheOwner_WhenNotInGroup()
    {
        // The quirk: the Group branch's bit is set, so it short-circuits before Owner is checked.
        var perms = MediaPermission.Owner | MediaPermission.Group;
        Assert.False(MediaPermissionEvaluator.HasPermission(perms, isOwner: true, isInObjectGroup: false));
        Assert.True(MediaPermissionEvaluator.HasPermission(perms, isOwner: true, isInObjectGroup: true));
    }

    [Fact]
    public void All_AlwaysPasses()
    {
        Assert.True(MediaPermissionEvaluator.HasPermission(MediaPermission.All, isOwner: false, isInObjectGroup: false));
    }
}
