using System;
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
        if (session == null || id == Guid.Empty) return legacyName;

        session.RequestDisplayName(id);
        string? display = session.TryGetDisplayName(id, out var name) ? name : null;
        return PersonNameDisplay.Choose(legacyName, display, UseDisplayNames());
    }

    /// <summary>The legacy name for something a caller handed over as "a name". Callers pass whatever they
    /// have on screen, and that can already be a Display Name; a conversation's log file must never be
    /// named after one (it would orphan every existing log and fork the history when the name changes).
    /// So a name that is the agent's known Display Name is swapped for the cached legacy name.</summary>
    public static string LegacyFor(GridSession? session, Guid id, string passedName)
    {
        if (session == null || id == Guid.Empty) return passedName;
        if (!session.TryGetDisplayName(id, out var display)) return passedName;
        if (!string.Equals(display, passedName?.Trim(), StringComparison.OrdinalIgnoreCase)) return passedName ?? "";
        return session.TryGetCachedName(id, out var legacy) ? legacy : passedName ?? "";
    }
}
