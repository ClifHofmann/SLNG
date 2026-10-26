namespace SLNG.Core;

/// <summary>
/// Pure decisions behind rezzing an inventory object (FEAT-INV-15), kept apart from the network so they
/// can be pinned by tests.
/// </summary>
public static class RezRules
{
    /// <summary><c>PERM_COPY</c> (<c>llpermissionsflags.h</c>:47).</summary>
    public const uint PermCopy = 0x00008000;

    /// <summary>
    /// <c>LLPermissions::allowCopyBy(agent)</c> (<c>llpermissions.cpp</c>:475-499, called with no group):
    /// a null agent can; a group-owned item is the group's; the owner is held to the OWNER mask; anyone
    /// else (a Library item, say) is held to the EVERYONE mask.
    /// </summary>
    public static bool AgentCanCopy(
        Guid agentId, Guid itemOwnerId, Guid itemGroupId, bool itemGroupOwned, uint ownerMask, uint everyoneMask)
    {
        if (agentId == Guid.Empty) return true;
        if (itemGroupOwned && itemGroupId == agentId) return (ownerMask & PermCopy) != 0;
        if (!itemGroupOwned && itemOwnerId == agentId) return (ownerMask & PermCopy) != 0;
        return (everyoneMask & PermCopy) != 0;
    }

    /// <summary>
    /// Whether the rez asks the simulator to take the item out of the inventory (<c>RemoveItem</c>):
    /// exactly when the agent cannot copy it (<c>lltooldraganddrop.cpp</c>:1870-1878). Rezzing it then
    /// uses it up, as it does in the reference viewer.
    /// </summary>
    public static bool ShouldRemoveItem(bool agentCanCopy) => !agentCanCopy;
}
