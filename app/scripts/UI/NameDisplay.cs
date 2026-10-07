using System;
using System.Linq;
using SLNG.Core;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// The one place that turns "this agent, whose legacy name is X" into the name chat, the Friends list and
/// the Recent list show: the Display Name when there is one and the person wants Display Names
/// (Preferences > Display, the same switch as the nametags), the legacy name otherwise. The rule itself is
/// <see cref="PersonNameDisplay.Choose"/>; this adds the session lookup and asks for names not yet known.
///
/// Presentation only. A conversation's log file, its Recent entry and everything sent to the grid stay on
/// the legacy name -- see <see cref="LegacyFor"/>.
/// </summary>
public static class NameDisplay
{
    /// <summary>Wired by Boot to the Display Names preference. Defaults to on, like the preference.</summary>
    public static Func<bool> UseDisplayNames { get; set; } = () => true;

    /// <summary>Wired by Boot to the "show usernames" preference (the nametag's second line). Lists that show
    /// both names under each other follow it.</summary>
    public static Func<bool> ShowUsernames { get; set; } = () => true;

    /// <summary>The name to show for <paramref name="id"/>. Asks the grid for the Display Name the first
    /// time an id is seen (deduped inside the session); the answer arrives later through
    /// <c>DisplayNameResolved</c> and the caller refreshes then.</summary>
    public static string For(GridSession? session, Guid id, string legacyName)
    {
        if (session == null || id == Guid.Empty) return legacyName ?? "";

        session.RequestDisplayName(id);
        string? display = session.TryGetDisplayName(id, out var name) ? name : null;
        if (string.IsNullOrWhiteSpace(legacyName))
        {
            if (session.TryGetCachedName(id, out var cn) && !string.IsNullOrWhiteSpace(cn) && cn != id.ToString())
                legacyName = cn;
            else
            {
                var friend = session.GetFriends().FirstOrDefault(f => f.Id == id);
                if (friend != null && !string.IsNullOrWhiteSpace(friend.Name))
                    legacyName = friend.Name;
            }
        }
        string cleanLegacy = AvatarNames.WithoutDefaultLastName(legacyName ?? "");
        return PersonNameDisplay.Choose(cleanLegacy, display, UseDisplayNames());
    }

    /// <summary>The legacy name for something a caller handed over as "a name". Callers pass whatever they
    /// have on screen, and that can already be a Display Name; a conversation's log file must never be
    /// named after one (it would orphan every existing log and fork the history when the name changes).
    /// So a name that is the agent's known Display Name is swapped for the cached legacy name.</summary>
    public static string LegacyFor(GridSession? session, Guid id, string passedName)
    {
        if (session == null || id == Guid.Empty) return passedName ?? "";

        // 1. If we have a cached legacy name for this agent, check whether passedName is empty or equals the Display Name
        if (session.TryGetCachedName(id, out var cachedLegacy) && !string.IsNullOrWhiteSpace(cachedLegacy) && cachedLegacy != id.ToString())
        {
            if (string.IsNullOrWhiteSpace(passedName)) return cachedLegacy;
            if (session.TryGetDisplayName(id, out var display) && string.Equals(display, passedName.Trim(), StringComparison.OrdinalIgnoreCase))
                return cachedLegacy;
            if (string.Equals(cachedLegacy, passedName.Trim(), StringComparison.OrdinalIgnoreCase))
                return cachedLegacy;
        }

        // 2. Check friends list for this agent (friends carry their legacy username in FriendEntry.Name)
        var friend = session.GetFriends().FirstOrDefault(f => f.Id == id);
        if (friend != null && !string.IsNullOrWhiteSpace(friend.Name))
        {
            if (string.IsNullOrWhiteSpace(passedName)) return friend.Name;
            if (session.TryGetDisplayName(id, out var display) && string.Equals(display, passedName.Trim(), StringComparison.OrdinalIgnoreCase))
                return friend.Name;
            if (string.Equals(friend.Name, passedName.Trim(), StringComparison.OrdinalIgnoreCase))
                return friend.Name;
        }

        // 3. If passedName is non-empty, check if it was actually a display name
        if (!string.IsNullOrWhiteSpace(passedName))
        {
            if (session.TryGetDisplayName(id, out var disp) && string.Equals(disp, passedName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                if (session.TryGetCachedName(id, out var leg) && !string.IsNullOrWhiteSpace(leg) && leg != id.ToString())
                    return leg;
            }
            return passedName.Trim();
        }

        return id.ToString();
    }
}
