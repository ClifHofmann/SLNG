namespace SLNG.Core;

/// <summary>
/// The reference viewer's answer to "may this agent build on this parcel?" (FEAT-INV-15). Used only as a
/// HINT -- the live warning on the rez drag card. Nothing is blocked on it: the viewer does not check
/// parcel rights when an object is dropped either, the simulator decides.
/// </summary>
public static class BuildPermission
{
    /// <summary>
    /// <c>LLViewerParcelMgr::allowAgentBuild()</c> (<c>llviewerparcelmgr.cpp</c>:688-700) for one parcel:
    /// <c>allowModifyBy(agent, active group) || isParcelOwnedByAgent(parcel, GP_LAND_ALLOW_CREATE)</c>.
    /// The god-mode shortcut is left out. The overload that takes a parcel (:703) is only
    /// <c>getAllowModify()</c>, i.e. "Build: Everyone", a subset of this.
    /// </summary>
    /// <remarks>
    /// <para><c>LLParcel::allowModifyBy</c> (<c>llparcel.cpp</c>:368-395), in this order: a null agent can
    /// (the system); public land (no owner) can; the owner can; <c>PF_CREATE_OBJECTS</c> lets everyone;
    /// <c>PF_CREATE_GROUP_OBJECTS</c> lets the agent <b>only if the group the agent has ACTIVE is set and is
    /// the parcel's group</b> -- being a member of the parcel's group while another group is active does not
    /// count.</para>
    /// <para><c>LLViewerParcelMgr::isParcelOwnedByAgent</c> (<c>llviewerparcelmgr.cpp</c>:2638-2666): the
    /// owner can; public land is never "owned" this way; otherwise the agent needs
    /// <c>GP_LAND_ALLOW_CREATE</c> (<c>roles_constants.h</c>:101, 1 &lt;&lt; 25) in the group that OWNS the
    /// parcel (<c>hasPowerInGroup(parcel->getOwnerID(), ...)</c> -- the owner id is the group id on a
    /// group-owned parcel, and no group the agent is in has an agent's id, so no <c>isGroupOwned</c> test
    /// is needed). This is a role power, which is why membership alone is not enough.</para>
    /// </remarks>
    /// <param name="agentId">The agent. Empty means "not logged in" and builds nothing.</param>
    /// <param name="activeGroupId">The group the agent has active, or empty for none.</param>
    /// <param name="parcelOwnerId">The parcel's owner: an agent, a group, or empty for public land.</param>
    /// <param name="parcelGroupId">The parcel's group (<c>LLParcel::getGroupID</c>).</param>
    /// <param name="parcelOptions">The parcel's Build: Everyone / Build: Group switches.</param>
    /// <param name="hasLandCreatePowerInGroup">Whether the agent holds <c>GP_LAND_ALLOW_CREATE</c> in the
    /// given group (<c>LLAgent::hasPowerInGroup</c>). Null means the powers are not known: false.</param>
    public static bool CanBuild(
        Guid agentId,
        Guid activeGroupId,
        Guid parcelOwnerId,
        Guid parcelGroupId,
        ParcelOptions parcelOptions,
        Func<Guid, bool>? hasLandCreatePowerInGroup)
    {
        if (agentId == Guid.Empty) return false;

        // allowModifyBy
        if (parcelOwnerId == Guid.Empty) return true; // isPublic()
        if (parcelOwnerId == agentId) return true;
        if ((parcelOptions & ParcelOptions.BuildEveryone) != 0) return true;
        if ((parcelOptions & ParcelOptions.BuildGroup) != 0
            && activeGroupId != Guid.Empty
            && parcelGroupId == activeGroupId)
        {
            return true;
        }

        // isParcelOwnedByAgent(parcel, GP_LAND_ALLOW_CREATE); the owner and public cases are above.
        return hasLandCreatePowerInGroup?.Invoke(parcelOwnerId) == true;
    }
}
