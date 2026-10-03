using LibreMetaverse;
using SLNG.Core;

namespace SLNG.Net;

/// <summary>Pure mapping from LibreMetaverse's <c>Group</c> struct to SLNG's neutral group records
/// (FEAT-UI-54). Nothing here touches the network, so it is unit-tested directly. Internal: the
/// LibreMetaverse type never crosses <c>SLNG.Net</c>'s public boundary.
///
/// <para>The same <c>Group</c> struct carries two different things depending on where it came from,
/// and LibreMetaverse fills a different half each time:</para>
/// <list type="bullet">
/// <item><b>Profile</b> (<c>GroupProfileReply</c>, <c>GroupManager.GroupProfileReplyHandler</c>): name,
/// charter, founder, insignia, fee, open enrolment, maturity, member count, powers. Never
/// <c>AcceptNotices</c> / <c>ListInProfile</c> / <c>Contribution</c>.</item>
/// <item><b>Membership</b> (<c>AgentGroupDataUpdate</c>, <c>AgentGroupDataUpdateMessageHandler</c>):
/// id, name, insignia, contribution, accept-notices, list-in-profile, powers. Never the charter, the
/// fee or the title.</item>
/// </list></summary>
internal static class GroupProfileMapper
{
    /// <summary>Profile half of a <c>Group</c> (an answer to <c>GroupProfileRequest</c>).</summary>
    internal static GroupProfileInfo ToProfile(Group g) => new(
        g.ID.Guid,
        g.Name ?? string.Empty,
        g.Charter ?? string.Empty,
        g.FounderID.Guid,
        g.InsigniaID.Guid,
        g.MembershipFee,
        g.OpenEnrollment,
        g.ShowInList,
        g.AllowPublish,
        g.MaturePublish,
        g.GroupMembershipCount,
        g.GroupRolesCount,
        g.MemberTitle ?? string.Empty,
        (ulong)g.Powers);

    /// <summary>Membership half of a <c>Group</c> (one row of <c>AgentGroupDataUpdate</c>).</summary>
    internal static GroupEntry ToEntry(Group g) => new(
        g.ID.Guid,
        g.Name ?? string.Empty,
        g.MemberTitle ?? string.Empty,
        g.InsigniaID.Guid,
        g.AcceptNotices,
        g.ListInProfile);
}
