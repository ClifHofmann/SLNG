namespace SLNG.Core;

/// <summary>
/// One group's public profile as the simulator's <c>GroupProfileReply</c> sends it, in engine- and
/// protocol-neutral form (FEAT-UI-54). Produced by <c>SLNG.Net.GridSession</c> from LibreMetaverse's
/// <c>Group</c>; no LibreMetaverse type crosses that boundary, same reasoning as <see cref="GroupEntry"/>.
///
/// Read-only: it is what the group info window shows. The agent's own per-group switches
/// (<see cref="GroupEntry.AcceptNotices"/>, <see cref="GroupEntry.ListInProfile"/>) are NOT in this
/// reply -- they come from the membership list (<c>AgentGroupDataUpdate</c>), and the reference viewer
/// reads them from there too (llpanelgroupgeneral.cpp: <c>gAgent.getGroupData</c>).
/// </summary>
/// <param name="Id">The group's UUID.</param>
/// <param name="Name">Group name.</param>
/// <param name="Charter">The group's charter text, possibly empty or multi-line.</param>
/// <param name="FounderId">Founder's agent id, or <see cref="System.Guid.Empty"/>.</param>
/// <param name="InsigniaId">Insignia texture id, or <see cref="System.Guid.Empty"/> for none.</param>
/// <param name="MembershipFee">Fee to join, in L$ (0 = free).</param>
/// <param name="OpenEnrollment">Anyone may join without an invitation.</param>
/// <param name="ShowInList">The group appears in search.</param>
/// <param name="AllowPublish">The group may publish itself in search (the viewer's "publish" flag).</param>
/// <param name="Mature">The group is rated Moderate (<c>MaturePublish</c>); false = General.</param>
/// <param name="MemberCount">Number of members, as the sim reports it.</param>
/// <param name="RoleCount">Number of roles the sim reports (not counting the implicit Everyone role).</param>
/// <param name="MemberTitle">The title the sim sends for the agent in this group; may be empty.</param>
/// <param name="Powers">The agent's group powers bit mask, as sent.</param>
public record GroupProfileInfo(
    Guid Id,
    string Name,
    string Charter,
    Guid FounderId,
    Guid InsigniaId,
    int MembershipFee,
    bool OpenEnrollment,
    bool ShowInList,
    bool AllowPublish,
    bool Mature,
    int MemberCount,
    int RoleCount,
    string MemberTitle,
    ulong Powers);
