namespace SLNG.Core;

/// <summary>
/// Evaluates whether an agent has permission to rez/build objects on a parcel (FEAT-INV-15).
/// </summary>
public static class BuildPermission
{
    /// <summary>
    /// Checks whether an agent with the given IDs and group memberships can build on a parcel.
    /// </summary>
    public static bool CanBuild(
        Guid agentId,
        Guid activeGroupId,
        IReadOnlyCollection<Guid>? agentGroupIds,
        Guid parcelOwnerId,
        bool isGroupOwned,
        Guid parcelGroupId,
        ParcelOptions parcelOptions)
    {
        if (agentId == Guid.Empty) return false;

        // 1. Direct landowner always has build rights
        if (parcelOwnerId == agentId) return true;

        // 2. Public sandbox / "Build: Everyone" (PF_CREATE_OBJECTS)
        if ((parcelOptions & ParcelOptions.BuildEveryone) != 0) return true;

        // 3. Group build enabled ("Build: Group" / PF_CREATE_GROUP_OBJECTS)
        if (parcelGroupId != Guid.Empty && (parcelOptions & ParcelOptions.BuildGroup) != 0)
        {
            if (activeGroupId != Guid.Empty && activeGroupId == parcelGroupId)
                return true;

            if (agentGroupIds != null && agentGroupIds.Contains(parcelGroupId))
                return true;
        }

        // 4. Group-owned land: group members have build rights
        if (isGroupOwned && parcelOwnerId != Guid.Empty)
        {
            if (activeGroupId != Guid.Empty && activeGroupId == parcelOwnerId)
                return true;

            if (agentGroupIds != null && agentGroupIds.Contains(parcelOwnerId))
                return true;
        }

        return false;
    }
}
