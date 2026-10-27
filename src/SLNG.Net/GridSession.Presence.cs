using LibreMetaverse;
using SLNG.Core;
using SLNG.Core.Presence;

namespace SLNG.Net;

/// <summary>FEAT-UI-75: Away, Do Not Disturb and the automatic answer to a first instant message.</summary>
public sealed partial class GridSession
{
    private readonly ImAutoresponder _autoresponder = new();

    // What the sim was last told, so a settings change that leaves Away alone sends nothing.
    private bool _awaySent;

    /// <summary>An automatic answer went out (a network-thread event): the UI shows it in the conversation, as the
    /// reference viewer logs it after the message it answers.</summary>
    public event EventHandler<AutoresponseSentEvent>? AutoresponseSent;

    /// <summary>The agent's availability and response texts. Setting it applies the Away flag to the sim; Do Not
    /// Disturb and the autorespond modes are viewer-side only -- they decide what we answer, the sim never sees them.</summary>
    public PresenceConfig Presence
    {
        get => _autoresponder.Config;
        set
        {
            _autoresponder.Config = value;
            ApplyAway(value.Away);
        }
    }

    /// <summary>
    /// Marks the agent away or back: the <c>AGENT_CONTROL_AWAY</c> flag on every <c>AgentUpdate</c> (what makes other
    /// viewers show "(Away)") and the away animation, both as <c>LLAgent::setAFK/clearAFK</c> does.
    /// </summary>
    private void ApplyAway(bool away)
    {
        if (!_client.Network.Connected) return;
        if (away == _awaySent) return;
        _awaySent = away;

        _client.Self.Movement.Away = away;
        // Reliable, so a lost datagram cannot leave the sim showing the opposite of what the menu says.
        if (away) _client.Self.AnimationStart(Animations.AWAY, true);
        else _client.Self.AnimationStop(Animations.AWAY, true);
        _client.Self.Movement.SendUpdate(true);
    }

    /// <summary>Whether this first message from <paramref name="from"/> is answered, and if so sends the answer.</summary>
    private void TryAutorespond(Guid from, string fromName, Guid sessionId, bool online)
    {
        string? text = _autoresponder.OnIncoming(
            from, _client.Self.AgentID.Guid, online,
            isFriend: _client.Friends.FriendList.ContainsKey(new UUID(from)),
            isMuted: IsAvatarMuted(from));
        if (text == null || !_client.Network.Connected) return;

        // Same packing as any session line; the dialog (IM_DO_NOT_DISTURB_AUTO_RESPONSE, 20) is what tells the other
        // side's viewer this is an automatic answer, and what keeps two busy people from answering each other forever:
        // only a plain message (IM_NOTHING_SPECIAL) is ever answered.
        SendSessionPacket(from, sessionId, text, InstantMessageDialog.BusyAutoResponse);
        Console.WriteLine($"[Presence] answered a first message from {fromName} ({from}) automatically");
        AutoresponseSent?.Invoke(this, new AutoresponseSentEvent(from, text));
    }
}
