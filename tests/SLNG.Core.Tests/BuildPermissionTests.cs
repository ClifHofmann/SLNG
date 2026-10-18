using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class BuildPermissionTests
{
    private readonly Guid _agentId = Guid.NewGuid();
    private readonly Guid _activeGroupId = Guid.NewGuid();
    private readonly Guid _otherGroupId = Guid.NewGuid();

    [Fact]
    public void Disconnected_or_empty_agent_cannot_build()
    {
        bool canBuild = BuildPermission.CanBuild(
            Guid.Empty,
            _activeGroupId,
            new[] { _activeGroupId },
            Guid.NewGuid(),
            false,
            Guid.Empty,
            ParcelOptions.BuildEveryone);

        Assert.False(canBuild);
    }

    [Fact]
    public void Parcel_owner_can_always_build()
    {
        bool canBuild = BuildPermission.CanBuild(
            _agentId,
            Guid.Empty,
            null,
            _agentId,
            false,
            Guid.Empty,
            ParcelOptions.None);

        Assert.True(canBuild);
    }

    [Fact]
    public void Build_everyone_allows_any_agent_to_build()
    {
        bool canBuild = BuildPermission.CanBuild(
            _agentId,
            Guid.Empty,
            null,
            Guid.NewGuid(),
            false,
            Guid.Empty,
            ParcelOptions.BuildEveryone);

        Assert.True(canBuild);
    }

    [Fact]
    public void Build_group_allows_agent_with_matching_active_group()
    {
        bool canBuild = BuildPermission.CanBuild(
            _agentId,
            _activeGroupId,
            new[] { _activeGroupId },
            Guid.NewGuid(),
            false,
            _activeGroupId,
            ParcelOptions.BuildGroup);

        Assert.True(canBuild);
    }

    [Fact]
    public void Build_group_allows_agent_who_is_member_even_if_active_group_differs()
    {
        bool canBuild = BuildPermission.CanBuild(
            _agentId,
            _otherGroupId,
            new[] { _otherGroupId, _activeGroupId },
            Guid.NewGuid(),
            false,
            _activeGroupId,
            ParcelOptions.BuildGroup);

        Assert.True(canBuild);
    }

    [Fact]
    public void Build_group_denies_agent_not_in_group()
    {
        bool canBuild = BuildPermission.CanBuild(
            _agentId,
            _otherGroupId,
            new[] { _otherGroupId },
            Guid.NewGuid(),
            false,
            _activeGroupId,
            ParcelOptions.BuildGroup);

        Assert.False(canBuild);
    }

    [Fact]
    public void Group_owned_parcel_allows_group_members()
    {
        Guid groupOwnerId = Guid.NewGuid();
        bool canBuild = BuildPermission.CanBuild(
            _agentId,
            groupOwnerId,
            new[] { groupOwnerId },
            groupOwnerId,
            true,
            groupOwnerId,
            ParcelOptions.None);

        Assert.True(canBuild);
    }

    [Fact]
    public void Non_owner_on_private_parcel_without_build_flags_is_denied()
    {
        bool canBuild = BuildPermission.CanBuild(
            _agentId,
            _activeGroupId,
            new[] { _activeGroupId },
            Guid.NewGuid(),
            false,
            Guid.NewGuid(),
            ParcelOptions.None);

        Assert.False(canBuild);
    }
}
