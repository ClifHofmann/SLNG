namespace SLNG.Core;

/// <summary>
/// One entry in the logged-in agent's friends list, in engine- and protocol-neutral form.
/// Produced by <c>SLNG.Net.GridSession.GetFriends</c> from LibreMetaverse's FriendsManager; no
/// LibreMetaverse type crosses that boundary (AGENTS.md layering rule).
/// </summary>
/// <param name="Id">The friend's agent UUID.</param>
/// <param name="Name">Display name, or empty if not resolved yet -- callers should fall back to
/// <c>GridSession.RequestAvatarName</c>/<c>NameResolved</c> the same way other UUID-keyed names
/// in this codebase resolve.</param>
/// <param name="IsOnline">Current online/offline presence.</param>
public record FriendEntry(Guid Id, string Name, bool IsOnline);
