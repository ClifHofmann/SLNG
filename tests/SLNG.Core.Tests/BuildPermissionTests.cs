using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// FEAT-INV-15: the build-rights HINT, pinned to the reference viewer's rule --
/// <c>LLParcel::allowModifyBy</c> (llparcel.cpp:368-395) or
/// <c>LLViewerParcelMgr::isParcelOwnedByAgent(parcel, GP_LAND_ALLOW_CREATE)</c> (llviewerparcelmgr.cpp:2638-2666),
/// as combined by <c>allowAgentBuild()</c> (:688-700).
/// </summary>
public class BuildPermissionTests
{
    private readonly Guid _agent = Guid.NewGuid();
    private readonly Guid _someoneElse = Guid.NewGuid();
    private readonly Guid _groupA = Guid.NewGuid();
    private readonly Guid _groupB = Guid.NewGuid();

    private static Func<Guid, bool> PowerIn(params Guid[] groups) => g => groups.Contains(g);

    [Fact]
    public void Empty_agent_builds_nothing_not_even_on_public_land_or_build_everyone()
    {
        Assert.False(BuildPermission.CanBuild(Guid.Empty, _groupA, Guid.Empty, Guid.Empty, ParcelOptions.None, null));
        Assert.False(BuildPermission.CanBuild(Guid.Empty, _groupA, _someoneElse, Guid.Empty, ParcelOptions.BuildEveryone, null));
    }

    [Fact]
    public void Public_land_with_no_owner_allows_anyone()
    {
        Assert.True(BuildPermission.CanBuild(_agent, Guid.Empty, Guid.Empty, Guid.Empty, ParcelOptions.None, null));
    }

    [Fact]
    public void Parcel_owner_can_always_build()
    {
        Assert.True(BuildPermission.CanBuild(_agent, Guid.Empty, _agent, Guid.Empty, ParcelOptions.None, null));
    }

    [Fact]
    public void Build_everyone_allows_any_agent()
    {
        Assert.True(BuildPermission.CanBuild(
            _agent, Guid.Empty, _someoneElse, Guid.Empty, ParcelOptions.BuildEveryone, null));
    }

    [Fact]
    public void Build_group_allows_the_agent_whose_ACTIVE_group_is_the_parcels_group()
    {
        Assert.True(BuildPermission.CanBuild(
            _agent, _groupA, _someoneElse, _groupA, ParcelOptions.BuildGroup, null));
    }

    [Fact]
    public void Build_group_does_not_count_membership_in_a_group_that_is_not_active()
    {
        // The agent may well be in group A, but with group B active the viewer says no:
        // allowModifyBy compares the ACTIVE group (gAgent.getGroupID()) with the parcel's.
        Assert.False(BuildPermission.CanBuild(
            _agent, _groupB, _someoneElse, _groupA, ParcelOptions.BuildGroup, null));
    }

    [Fact]
    public void Build_group_needs_an_active_group_at_all()
    {
        // group_id.notNull() -- two empty groups are not "the same group".
        Assert.False(BuildPermission.CanBuild(
            _agent, Guid.Empty, _someoneElse, Guid.Empty, ParcelOptions.BuildGroup, null));
    }

    [Fact]
    public void Matching_active_group_is_not_enough_when_the_parcel_does_not_allow_group_building()
    {
        Assert.False(BuildPermission.CanBuild(
            _agent, _groupA, _someoneElse, _groupA, ParcelOptions.None, null));
    }

    [Fact]
    public void Group_owned_parcel_allows_an_agent_with_the_create_power_in_the_owning_group()
    {
        // The owner id of a group-owned parcel is the group.
        Assert.True(BuildPermission.CanBuild(
            _agent, Guid.Empty, _groupA, _groupA, ParcelOptions.None, PowerIn(_groupA)));
    }

    [Fact]
    public void Group_owned_parcel_denies_a_member_who_lacks_the_create_power()
    {
        // Membership, even with the group active, is not the power.
        Assert.False(BuildPermission.CanBuild(
            _agent, _groupA, _groupA, _groupA, ParcelOptions.None, PowerIn()));
        Assert.False(BuildPermission.CanBuild(
            _agent, _groupA, _groupA, _groupA, ParcelOptions.None, null));
    }

    [Fact]
    public void The_create_power_must_be_in_the_group_that_owns_the_parcel()
    {
        Assert.False(BuildPermission.CanBuild(
            _agent, Guid.Empty, _groupA, _groupA, ParcelOptions.None, PowerIn(_groupB)));
    }

    [Fact]
    public void Non_owner_on_private_parcel_without_build_flags_is_denied()
    {
        Assert.False(BuildPermission.CanBuild(
            _agent, _groupA, _someoneElse, Guid.NewGuid(), ParcelOptions.None, PowerIn(_groupA)));
    }
}
