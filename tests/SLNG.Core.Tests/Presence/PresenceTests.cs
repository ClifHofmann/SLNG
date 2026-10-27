using SLNG.Core.Presence;
using Xunit;

namespace SLNG.Core.Tests.Presence;

/// <summary>FEAT-UI-75: Away / Do Not Disturb / Autorespond, as Firestorm decides them.</summary>
public class PresenceTests
{
    private static readonly Guid Self = Guid.NewGuid();
    private static readonly Guid Peer = Guid.NewGuid();

    private static PresenceConfig Cfg(bool away = false, bool dnd = false, bool all = false, bool nonFriends = false, bool sendAway = false) =>
        new(Away: away, DoNotDisturb: dnd, Autorespond: all, AutorespondNonFriends: nonFriends, SendAwayResponse: sendAway,
            AwayMessage: "away", BusyMessage: "busy", AutorespondMessage: "all", NonFriendsMessage: "strangers");

    [Fact]
    public void Nothing_set_means_no_answer()
    {
        Assert.Null(AutoresponsePolicy.Choose(Cfg(), isFriend: false, isMuted: false));
    }

    [Fact]
    public void Do_not_disturb_answers_with_the_busy_text_and_wins_over_everything()
    {
        var c = Cfg(away: true, dnd: true, all: true, nonFriends: true, sendAway: true);
        Assert.Equal("busy", AutoresponsePolicy.Choose(c, isFriend: false, isMuted: false));
        Assert.Equal("busy", AutoresponsePolicy.Choose(c, isFriend: true, isMuted: false));
    }

    [Fact]
    public void Non_friends_mode_answers_strangers_only_and_a_friend_falls_through_to_autorespond()
    {
        var c = Cfg(all: true, nonFriends: true);
        Assert.Equal("strangers", AutoresponsePolicy.Choose(c, isFriend: false, isMuted: false));
        Assert.Equal("all", AutoresponsePolicy.Choose(c, isFriend: true, isMuted: false));
        Assert.Null(AutoresponsePolicy.Choose(Cfg(nonFriends: true), isFriend: true, isMuted: false));
    }

    [Fact]
    public void Away_answers_only_when_sending_the_away_response_is_on()
    {
        Assert.Null(AutoresponsePolicy.Choose(Cfg(away: true), isFriend: false, isMuted: false));
        Assert.Equal("away", AutoresponsePolicy.Choose(Cfg(away: true, sendAway: true), isFriend: false, isMuted: false));
        Assert.Null(AutoresponsePolicy.Choose(Cfg(sendAway: true), isFriend: false, isMuted: false));
    }

    [Fact]
    public void A_muted_sender_is_never_answered()
    {
        Assert.Null(AutoresponsePolicy.Choose(Cfg(dnd: true), isFriend: false, isMuted: true));
    }

    [Fact]
    public void An_empty_text_is_not_sent()
    {
        var c = Cfg(dnd: true) with { BusyMessage = "  " };
        Assert.Null(AutoresponsePolicy.Choose(c, isFriend: false, isMuted: false));
    }

    [Fact]
    public void Only_the_message_that_opens_a_conversation_is_answered()
    {
        var r = new ImAutoresponder { Config = Cfg(dnd: true) };
        Assert.Equal("busy", r.OnIncoming(Peer, Self, online: true, isFriend: false, isMuted: false));
        Assert.Null(r.OnIncoming(Peer, Self, online: true, isFriend: false, isMuted: false));
    }

    [Fact]
    public void A_conversation_we_started_is_not_answered()
    {
        var r = new ImAutoresponder { Config = Cfg(dnd: true) };
        r.NoteOutgoing(Peer);
        Assert.Null(r.OnIncoming(Peer, Self, online: true, isFriend: false, isMuted: false));
    }

    [Fact]
    public void A_stored_message_is_not_answered_but_still_opens_the_conversation()
    {
        var r = new ImAutoresponder { Config = Cfg(dnd: true) };
        Assert.Null(r.OnIncoming(Peer, Self, online: false, isFriend: false, isMuted: false));
        Assert.Null(r.OnIncoming(Peer, Self, online: true, isFriend: false, isMuted: false));
    }

    [Fact]
    public void A_message_while_available_still_opens_the_conversation()
    {
        // Firestorm: once the session exists the sender is not told later when the status changes.
        var r = new ImAutoresponder();
        Assert.Null(r.OnIncoming(Peer, Self, online: true, isFriend: false, isMuted: false));
        r.Config = Cfg(dnd: true);
        Assert.Null(r.OnIncoming(Peer, Self, online: true, isFriend: false, isMuted: false));
    }

    [Fact]
    public void Ourselves_and_the_null_sender_are_never_answered()
    {
        var r = new ImAutoresponder { Config = Cfg(dnd: true) };
        Assert.Null(r.OnIncoming(Self, Self, online: true, isFriend: false, isMuted: false));
        Assert.Null(r.OnIncoming(Guid.Empty, Self, online: true, isFriend: false, isMuted: false));
    }

    [Fact]
    public void Reset_forgets_the_conversations()
    {
        var r = new ImAutoresponder { Config = Cfg(dnd: true) };
        r.OnIncoming(Peer, Self, true, false, false);
        r.Reset();
        Assert.Equal("busy", r.OnIncoming(Peer, Self, true, false, false));
    }

    [Theory]
    [InlineData(false, false, PresenceStatus.Available)]
    [InlineData(true, false, PresenceStatus.Away)]
    [InlineData(false, true, PresenceStatus.DoNotDisturb)]
    [InlineData(true, true, PresenceStatus.DoNotDisturb)]
    public void Status_prefers_do_not_disturb(bool away, bool dnd, PresenceStatus expected)
    {
        Assert.Equal(expected, Cfg(away: away, dnd: dnd).Status);
    }

    [Fact]
    public void A_quiet_spell_sends_the_agent_away_once()
    {
        var t = new AwayTracker();
        t.OnActivity(0);
        Assert.False(t.Tick(299, 300));
        Assert.True(t.Tick(301, 300));
        Assert.True(t.IsAway);
        Assert.False(t.Tick(400, 300));
    }

    [Fact]
    public void A_timeout_of_zero_never_goes_away()
    {
        var t = new AwayTracker();
        t.OnActivity(0);
        Assert.False(t.Tick(100000, 0));
    }

    [Fact]
    public void Input_resets_the_quiet_spell()
    {
        var t = new AwayTracker();
        t.OnActivity(0);
        t.OnActivity(250);
        Assert.False(t.Tick(400, 300));
        Assert.True(t.Tick(551, 300));
    }

    [Fact]
    public void Input_right_after_choosing_away_does_not_cancel_it()
    {
        var t = new AwayTracker();
        t.SetAway(100);
        Assert.False(t.OnActivity(105));
        Assert.True(t.IsAway);
    }

    [Fact]
    public void Input_after_the_grace_period_brings_the_agent_back()
    {
        var t = new AwayTracker();
        t.SetAway(100);
        Assert.True(t.OnActivity(111));
        Assert.False(t.IsAway);
    }

    [Fact]
    public void Coming_back_restarts_the_quiet_spell()
    {
        var t = new AwayTracker();
        t.OnActivity(0);
        t.Tick(400, 300);
        Assert.True(t.OnActivity(1000));
        Assert.False(t.Tick(1200, 300));
        Assert.True(t.Tick(1301, 300));
    }
}
