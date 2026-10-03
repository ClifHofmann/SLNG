namespace SLNG.Net;

/// <summary>Decides when to tell the simulator "I am leaving this group's chat session" for a group
/// whose chat the user switched off (FEAT-UI-54). Pure, so it is unit-tested without a connection.
///
/// <para>Why a gate and not "leave once": on Second Life the simulator invites an agent to a group
/// session when somebody speaks, and LibreMetaverse accepts every invitation on its own
/// (<c>AgentManager.ChatterBoxInvitationEventHandler</c> -> <c>ChatterBoxAcceptInviteAsync</c>),
/// before any SLNG code sees the message. So after a leave, the next line re-invites us, we are in
/// again, and a "left already" flag would keep us there for good. Firestorm leaves on every new
/// invitation for the same reason (llimview.cpp:3590-3603). SLNG cannot tell an invitation from a
/// message at that point, so it leaves again whenever a line shows up after
/// <see cref="MinInterval"/> -- which also keeps a busy ignored group from causing one leave per line.</para></summary>
internal sealed class IgnoredGroupSessionGate
{
    /// <summary>Shortest time between two leave messages for the same group.</summary>
    internal static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(20);

    private readonly Dictionary<Guid, long> _lastLeaveTicks = new();
    private readonly object _lock = new();

    /// <summary>True when a leave should be sent now for <paramref name="groupId"/>; records it.</summary>
    internal bool ShouldLeave(Guid groupId, long nowTicks)
    {
        lock (_lock)
        {
            if (_lastLeaveTicks.TryGetValue(groupId, out var last) && nowTicks - last < MinInterval.Ticks)
                return false;
            _lastLeaveTicks[groupId] = nowTicks;
            return true;
        }
    }

    /// <summary>Forget a group (its chat was switched back on), so a later switch-off leaves at once.</summary>
    internal void Reset(Guid groupId)
    {
        lock (_lock) _lastLeaveTicks.Remove(groupId);
    }
}
