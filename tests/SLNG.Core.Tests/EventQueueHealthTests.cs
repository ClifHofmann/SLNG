using System;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// BUG-NET-20. The rules that turn a stream of identical failures into one useful statement:
/// when a region's event queue counts as dead, and how often that may be said out loud.
/// </summary>
public class EventQueueHealthTests
{
    private static readonly DateTime T0 = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
    private const string Region = "Millenium";

    [Fact]
    public void OneFailureIsNotAStall()
    {
        // A single bad response is ordinary -- a proxy hiccup, a 502 in the body, a region
        // mid-handover -- and the queue recovers by itself. Announcing that would be noise, and
        // noise is half of what this bug is.
        var health = new EventQueueHealth();

        Assert.False(health.ReportFailure(Region, T0));
        Assert.False(health.IsStalled(Region));
    }

    [Fact]
    public void TheThresholdFailureAnnouncesOnce()
    {
        var health = new EventQueueHealth();

        for (int i = 1; i < EventQueueHealth.FailuresBeforeStalled; i++)
            Assert.False(health.ReportFailure(Region, T0.AddSeconds(i)));

        Assert.True(health.ReportFailure(Region, T0.AddSeconds(EventQueueHealth.FailuresBeforeStalled)));
        Assert.True(health.IsStalled(Region));
    }

    [Fact]
    public void FurtherFailuresStaySilent()
    {
        // The whole point: 65 identical log lines become one. A dead queue keeps failing about
        // once a second, so everything after the announcement has to be swallowed.
        var health = new EventQueueHealth();
        var now = T0;
        for (int i = 0; i < EventQueueHealth.FailuresBeforeStalled; i++)
            health.ReportFailure(Region, now = now.AddSeconds(1));

        for (int i = 0; i < 60; i++)
            Assert.False(health.ReportFailure(Region, now = now.AddSeconds(1)));

        Assert.Equal(EventQueueHealth.FailuresBeforeStalled + 60, health.FailureCount(Region));
    }

    [Fact]
    public void AStillDeadQueueIsMentionedAgainMuchLater()
    {
        // Never repeating is wrong too: ten minutes on, the user has done several things that
        // silently did not work, and deserves to be told why.
        //
        // The failures have to keep ARRIVING for that, which is what a dead queue does -- it
        // polls about once a second and fails every time. A ten-minute gap with nothing in it
        // means something else entirely, and FailuresFarApartDoNotAddUp pins that case.
        var health = new EventQueueHealth();
        var now = T0;
        int announcements = 0;

        for (int second = 0; second < (int)EventQueueHealth.RepeatAfter.TotalSeconds + 10; second++)
            if (health.ReportFailure(Region, now = now.AddSeconds(1)))
                announcements++;

        Assert.Equal(2, announcements);
    }

    [Fact]
    public void FailuresFarApartDoNotAddUp()
    {
        // Five unrelated hiccups spread over an afternoon must not become a false alarm.
        var health = new EventQueueHealth();
        var now = T0;
        for (int i = 0; i < 20; i++)
        {
            now = now.AddSeconds(EventQueueHealth.FailureWindow.TotalSeconds + 1);
            Assert.False(health.ReportFailure(Region, now));
        }

        Assert.False(health.IsStalled(Region));
        Assert.Equal(1, health.FailureCount(Region));
    }

    [Fact]
    public void RegionsAreCountedSeparately()
    {
        // A border session talks to several simulators, and one dead queue says nothing about
        // the others -- reporting the healthy neighbour would send the next investigation the
        // wrong way.
        var health = new EventQueueHealth();
        var now = T0;
        for (int i = 0; i < EventQueueHealth.FailuresBeforeStalled; i++)
            health.ReportFailure("Millenium", now = now.AddSeconds(1));

        Assert.True(health.IsStalled("Millenium"));
        Assert.False(health.IsStalled("Ahern"));
        Assert.Equal(0, health.FailureCount("Ahern"));
    }

    [Fact]
    public void AQuietMinuteResetsTheAnnouncement()
    {
        // This is what stands in for recovery detection. The failures simply stop when the queue
        // comes back, and a queue that dies again later must be announced again rather than
        // suppressed by the earlier announcement.
        var health = new EventQueueHealth();
        var now = T0;
        for (int i = 0; i < EventQueueHealth.FailuresBeforeStalled; i++)
            health.ReportFailure(Region, now = now.AddSeconds(1));

        now = now + EventQueueHealth.FailureWindow + TimeSpan.FromSeconds(1);

        for (int i = 1; i < EventQueueHealth.FailuresBeforeStalled; i++)
            Assert.False(health.ReportFailure(Region, now = now.AddSeconds(1)));
        Assert.True(health.ReportFailure(Region, now.AddSeconds(1)));
    }

    [Fact]
    public void AnEmptyRegionKeyIsIgnored()
    {
        // The name is parsed out of a log line, so "could not tell" is a real outcome and must
        // not become a bucket that every unnamed region shares.
        var health = new EventQueueHealth();
        for (int i = 0; i < 20; i++)
            Assert.False(health.ReportFailure("", T0.AddSeconds(i)));
    }

    [Fact]
    public void ClearForgetsEverything()
    {
        var health = new EventQueueHealth();
        var now = T0;
        for (int i = 0; i < EventQueueHealth.FailuresBeforeStalled; i++)
            health.ReportFailure(Region, now = now.AddSeconds(1));

        health.Clear();

        Assert.False(health.IsStalled(Region));
        Assert.Equal(0, health.FailureCount(Region));
    }

    [Fact]
    public void TheRealLogLineIsRecognisedAndNamesItsRegion()
    {
        // Copied verbatim from the client log that produced BUG-NET-20 -- 65 of these in a row,
        // with nothing else between them.
        const string Line = "Skipping LLSD parsing; server returned non-LLSD response from " +
                            "Millenium (54.218.44.155:13037): \"cap not found: " +
                            "'33e2b970-d5d6-7e43-ac51-010050dc4c7c'\n\"";

        Assert.True(EventQueueHealth.TryReadEventQueueFailure(Line, out var sim));
        Assert.Equal("Millenium (54.218.44.155:13037)", sim);
        Assert.Equal("Millenium", EventQueueHealth.RegionNameFromSimulatorText(sim));
    }

    [Fact]
    public void TheOtherEventQueueFailureShapeIsRecognisedToo()
    {
        const string Line = "Could not parse response (1) from Da Boom (127.0.0.1:9000) " +
                            "event queue: \"<?xml version=...\"";

        Assert.True(EventQueueHealth.TryReadEventQueueFailure(Line, out var sim));
        Assert.Equal("Da Boom (127.0.0.1:9000)", sim);
    }

    [Theory]
    [InlineData("Failed to fetch inventory: Bad Request")]
    [InlineData("Could not parse response (1) from the asset server: \"…\"")]
    [InlineData("")]
    [InlineData(null)]
    public void UnrelatedLinesAreLeftAlone(string? message)
    {
        // A false positive here would swallow someone else's log line, which is the exact harm
        // this change exists to undo.
        Assert.False(EventQueueHealth.TryReadEventQueueFailure(message, out var sim));
        Assert.Equal(string.Empty, sim);
    }

    [Theory]
    [InlineData("Millenium (54.218.44.155:13037)", "Millenium")]
    [InlineData("Da Boom (127.0.0.1:9000)", "Da Boom")]
    // No address suffix: take it as the name rather than losing it.
    [InlineData("Millenium", "Millenium")]
    // A parenthesis that is not an address belongs to the name.
    [InlineData("Lusk (North)", "Lusk (North)")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void TheRegionNameIsPulledOutOfLibreMetaversesSimulatorText(string? text, string expected)
        => Assert.Equal(expected, EventQueueHealth.RegionNameFromSimulatorText(text));
}
