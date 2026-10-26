using System.Reflection;
using LibreMetaverse;
using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// BUG-NET-35: instant messages the grid stored while we were offline. Two things are pinned: the time a
/// stored message was sent survives LibreMetaverse's <c>new DateTime(unixSeconds)</c> (seconds read as
/// ticks), and it reaches the IM event only for a stored message, never for a live one.
/// </summary>
public class OfflineMessageTests
{
    private static readonly DateTime Sent = new(2026, 10, 20, 18, 30, 15, DateTimeKind.Utc);
    private static long UnixSeconds => (long)(Sent - DateTime.UnixEpoch).TotalSeconds;

    [Fact]
    public void Seconds_read_as_ticks_are_turned_back_into_the_send_time()
    {
        Assert.Equal(Sent, OfflineMessages.SentUtc(new DateTime(UnixSeconds)));
    }

    [Fact]
    public void A_real_date_is_used_as_it_is()
    {
        var real = OfflineMessages.SentUtc(new DateTime(2026, 10, 20, 18, 30, 15, DateTimeKind.Unspecified));
        Assert.Equal(Sent, real);
        Assert.Equal(DateTimeKind.Utc, real!.Value.Kind);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    public void No_time_from_the_grid_is_null_not_1970(long ticks)
    {
        Assert.Null(OfflineMessages.SentUtc(new DateTime(Math.Max(ticks, 0))));
    }

    [Fact]
    public void An_absurd_seconds_count_is_refused_instead_of_throwing()
    {
        Assert.Null(OfflineMessages.SentUtc(new DateTime(600_000_000_000_000_000L)));
    }

    private static InstantMessageEvent? Receive(InstantMessageOnline offline, DateTime timestamp)
    {
        using var session = new GridSession();
        InstantMessageEvent? received = null;
        session.InstantMessageReceived += (s, e) => received = e;

        var from = UUID.Random();
        var im = new InstantMessage
        {
            Dialog = InstantMessageDialog.MessageFromAgent,
            // An offline message carries the sender's own id as its session id (SessionIds.IsForeign).
            IMSessionID = from,
            FromAgentID = from,
            FromAgentName = "Denise Resident",
            Message = "are you there?",
            Offline = offline,
            Timestamp = timestamp,
            BinaryBucket = Array.Empty<byte>(),
        };
        typeof(GridSession).GetMethod("OnInstantMessage", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(session, new object?[] { null, new InstantMessageEventArgs(im, null) });
        return received;
    }

    [Fact]
    public void A_stored_message_carries_when_it_was_sent()
    {
        var e = Receive(InstantMessageOnline.Offline, new DateTime(UnixSeconds));
        Assert.NotNull(e);
        Assert.Equal("are you there?", e!.Message);
        Assert.Equal(Sent, e.SentUtc);
    }

    [Fact]
    public void A_live_message_carries_no_send_time()
    {
        var e = Receive(InstantMessageOnline.Online, new DateTime(UnixSeconds));
        Assert.NotNull(e);
        Assert.Null(e!.SentUtc);
    }
}
