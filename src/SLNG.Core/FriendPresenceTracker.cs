using System.Collections.Concurrent;

namespace SLNG.Core;

/// <summary>
/// Tells a friend who really went online or offline from one the grid merely mentioned again.
/// </summary>
/// <remarks>
/// The library reports every presence message, including an offline friend "going offline" and an
/// online one "coming online" a second time, and a friendship that was just formed is asked about
/// twice on purpose. A notification that fires for each of those is noise, so this remembers the last
/// state per friend and says "changed" only when the new one differs. A friend nobody has heard of
/// counts as offline, which is what the friends list starts with at login. Safe to call from the
/// network thread, where the events arrive.
/// </remarks>
public sealed class FriendPresenceTracker
{
    private readonly ConcurrentDictionary<Guid, bool> _online = new();

    /// <summary>Records the friend's state. True when it differs from the last one recorded.</summary>
    public bool Update(Guid friendId, bool online)
    {
        bool before = _online.TryGetValue(friendId, out var was) && was;
        _online[friendId] = online;
        return before != online;
    }

    /// <summary>Forgets everyone -- a new login starts from an empty friends list.</summary>
    public void Reset() => _online.Clear();
}
