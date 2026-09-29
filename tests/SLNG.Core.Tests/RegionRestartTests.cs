using System;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>FEAT-UI-34: what a region-restart notice becomes, and how its countdown behaves.</summary>
public class RegionRestartTests
{
    [Fact]
    public void MinutesNotice_IsConvertedToSeconds()
    {
        bool ok = RegionRestartAlert.TryCreate("RegionRestartMinutes", "Millenium", minutes: 5, seconds: null, out var restart);

        Assert.True(ok);
        Assert.Equal("Millenium", restart.RegionName);
        Assert.Equal(300, restart.Seconds);
    }

    [Fact]
    public void SecondsNotice_IsTakenAsIs()
    {
        bool ok = RegionRestartAlert.TryCreate("RegionRestartSeconds", "Millenium", minutes: null, seconds: 45, out var restart);

        Assert.True(ok);
        Assert.Equal(45, restart.Seconds);
    }

    [Fact]
    public void EachNoticeReadsOnlyItsOwnField()
    {
        // A stray SECONDS on a minutes notice (or the reverse) must not change the meaning.
        RegionRestartAlert.TryCreate("RegionRestartMinutes", "R", minutes: 2, seconds: 999, out var minutes);
        RegionRestartAlert.TryCreate("RegionRestartSeconds", "R", minutes: 999, seconds: 30, out var seconds);

        Assert.Equal(120, minutes.Seconds);
        Assert.Equal(30, seconds.Seconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("HomePositionSet")]
    [InlineData("regionrestartminutes")] // ids are exact; a near-miss is some other alert
    public void OtherNotifications_AreNotRestarts(string? id)
    {
        Assert.False(RegionRestartAlert.IsRestartNotification(id));
        Assert.False(RegionRestartAlert.TryCreate(id, "R", minutes: 5, seconds: 5, out _));
    }

    [Fact]
    public void MissingNumber_StillWarns_AsZeroSeconds()
    {
        // Dropping the warning would hide a real restart; showing 0:00 does not.
        bool ok = RegionRestartAlert.TryCreate("RegionRestartMinutes", null, minutes: null, seconds: null, out var restart);

        Assert.True(ok);
        Assert.Equal(0, restart.Seconds);
        Assert.Equal(string.Empty, restart.RegionName);
    }

    [Theory]
    [InlineData(-5L, 0)]
    [InlineData(long.MaxValue / 120, RegionRestartAlert.MaxSeconds)]
    public void Minutes_AreClampedToARealisticRange(long minutes, int expected)
    {
        RegionRestartAlert.TryCreate("RegionRestartMinutes", "R", minutes, seconds: null, out var restart);

        Assert.Equal(expected, restart.Seconds);
    }

    // ---- countdown ------------------------------------------------------------------------

    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(100);

    [Fact]
    public void Countdown_RunsDownFromTheClock_NotFromTicks()
    {
        var c = new RegionRestartCountdown();
        c.Start("Millenium", 60, T0);

        Assert.Equal(60, c.SecondsLeft(T0));
        Assert.Equal(45, c.SecondsLeft(T0 + TimeSpan.FromSeconds(15)));
        // A frame that stalled for ten seconds does not make the display fall behind.
        Assert.Equal(35, c.SecondsLeft(T0 + TimeSpan.FromSeconds(25)));
    }

    [Fact]
    public void Countdown_RoundsUp_SoOneIsShownUntilTheEnd()
    {
        var c = new RegionRestartCountdown();
        c.Start("R", 10, T0);

        Assert.Equal(1, c.SecondsLeft(T0 + TimeSpan.FromSeconds(9.4)));
        Assert.Equal(0, c.SecondsLeft(T0 + TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Countdown_RepeatNotice_MovesTheDeadlineInsteadOfStacking()
    {
        var c = new RegionRestartCountdown();
        c.Start("R", 300, T0);

        c.Start("R", 60, T0 + TimeSpan.FromSeconds(240)); // "one minute left" arrives

        Assert.True(c.IsRunning);
        Assert.Equal(60, c.SecondsLeft(T0 + TimeSpan.FromSeconds(240)));
    }

    [Fact]
    public void Countdown_KnowsWhenItHasElapsed()
    {
        var c = new RegionRestartCountdown();
        Assert.False(c.HasElapsed(T0)); // not running

        c.Start("R", 5, T0);
        Assert.False(c.HasElapsed(T0 + TimeSpan.FromSeconds(4.9)));
        Assert.True(c.HasElapsed(T0 + TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Countdown_AfterStop_ShowsNothingLeft()
    {
        var c = new RegionRestartCountdown();
        c.Start("R", 30, T0);
        c.Stop();

        Assert.False(c.IsRunning);
        Assert.Equal(0, c.SecondsLeft(T0));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(9, "0:09")]
    [InlineData(60, "1:00")]
    [InlineData(125, "2:05")]
    [InlineData(5400, "90:00")]
    [InlineData(-3, "0:00")]
    public void Format_ReadsAsMinutesAndSeconds(int seconds, string expected)
    {
        Assert.Equal(expected, RegionRestartCountdown.Format(seconds));
    }
}
