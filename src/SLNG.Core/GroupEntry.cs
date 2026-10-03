namespace SLNG.Core;

/// <summary>
/// One group the logged-in agent belongs to, in engine- and protocol-neutral form. Produced by
/// <c>SLNG.Net.GridSession.GetGroups</c> from LibreMetaverse's <c>GroupManager</c>; no
/// LibreMetaverse type crosses that boundary (AGENTS.md layering rule) — same shape and reasoning
/// as <see cref="FriendEntry"/>.
/// </summary>
/// <param name="Id">The group's UUID.</param>
/// <param name="Name">Group name as the server reports it.</param>
/// <param name="MemberTitle">The agent's own title in this group ("everyone" role title), or
/// empty. Displayed as the secondary line on a group row.</param>
/// <param name="InsigniaId">Group insignia texture id, or <see cref="System.Guid.Empty"/>. Not
/// fetched yet — the Groups panel draws an initial badge instead.</param>
/// <param name="AcceptNotices">Whether the agent receives this group's notices. Server-side
/// membership state, distinct from SLNG's local chat mute.</param>
/// <param name="ListInProfile">Whether the agent lists this group in their profile (FEAT-UI-54).
/// Server-side too: it travels in the same <c>SetGroupAcceptNotices</c> message as
/// <paramref name="AcceptNotices"/>.</param>
public record GroupEntry(Guid Id, string Name, string MemberTitle, Guid InsigniaId, bool AcceptNotices, bool ListInProfile = false);
