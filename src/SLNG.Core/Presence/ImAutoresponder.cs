using System.Collections.Concurrent;

namespace SLNG.Core.Presence;

/// <summary>
/// FEAT-UI-75: decides, per incoming 1:1 instant message, whether the viewer answers it on the agent's behalf.
/// Firestorm answers only the message that opens a conversation (<c>!gIMMgr->hasSession(session_id)</c>): once there
/// is a session the sender has already been told, and answering every line would be spam. Here "a session" is a peer
/// that has written to us or been written to since login. Thread-safe -- the messages arrive on a network thread.
/// </summary>
public sealed class ImAutoresponder
{
    private readonly ConcurrentDictionary<Guid, byte> _peers = new();
    private volatile PresenceConfig _config = new();

    public PresenceConfig Config
    {
        get => _config;
        set => _config = value ?? new PresenceConfig();
    }

    /// <summary>We wrote to this person, so a conversation exists and their reply is not a first message.</summary>
    public void NoteOutgoing(Guid peer) => _peers.TryAdd(peer, 0);

    /// <summary>A new login starts without conversations.</summary>
    public void Reset() => _peers.Clear();

    /// <summary>Records an incoming message and returns the answer to send, or null.</summary>
    /// <param name="online">False for a message the grid stored while we were away: the sender is long gone and,
    /// like the reference viewer, we do not answer into the past (<c>offline == IM_ONLINE</c>).</param>
    public string? OnIncoming(Guid from, Guid self, bool online, bool isFriend, bool isMuted)
    {
        if (from == Guid.Empty || from == self) return null;
        bool first = _peers.TryAdd(from, 0);
        if (!first || !online) return null;
        return AutoresponsePolicy.Choose(_config, isFriend, isMuted);
    }
}
