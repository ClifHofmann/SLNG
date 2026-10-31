using System;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class FetchPipeStatsTests
{
    [Fact]
    public void Idle_FormatsNothing()
    {
        var s = new FetchPipeStats("MeshPipe");

        Assert.Null(s.TakeLine());
    }

    [Fact]
    public void CountsRequestsCompletedFailedAndBytes()
    {
        var s = new FetchPipeStats("MeshPipe");
        s.Begin(); s.Begin(); s.Begin();
        s.EndTimed(10, ok: true, bytes: 2048);
        s.EndTimed(20, ok: true, bytes: 1024);
        s.EndTimed(30, ok: false, bytes: 0);

        var snap = s.TakeInterval();

        Assert.Equal(3, snap.Requests);
        Assert.Equal(2, snap.Completed);
        Assert.Equal(1, snap.Failed);
        Assert.Equal(3072, snap.Bytes);
        Assert.Equal(0, snap.InFlight);
    }

    [Fact]
    public void InFlight_TracksOpenRequests_AndPeakIsThePeakOfTheInterval()
    {
        var s = new FetchPipeStats("TexHttp");
        s.Begin(); s.Begin(); s.Begin(); s.Begin();
        s.EndTimed(5, true, 1);
        s.EndTimed(5, true, 1);

        var first = s.TakeInterval();
        Assert.Equal(2, first.InFlight);
        Assert.Equal(4, first.PeakInFlight);

        // The next interval starts at what is still open, and its peak cannot be below that.
        var second = s.TakeInterval();
        Assert.Equal(2, second.InFlight);
        Assert.Equal(2, second.PeakInFlight);
    }

    [Fact]
    public void Percentiles_AreOfTheIntervalOnly()
    {
        var s = new FetchPipeStats("MeshPipe");
        for (int i = 1; i <= 100; i++)
        {
            s.Begin();
            s.EndTimed(i, true, 1);
        }

        var snap = s.TakeInterval();
        Assert.InRange(snap.P50Ms, 49, 52);
        Assert.InRange(snap.P95Ms, 94, 97);

        s.Begin();
        s.EndTimed(1000, true, 1);
        var next = s.TakeInterval();
        Assert.Equal(1000, next.P50Ms);
        Assert.Equal(1, next.IntervalRequests);
    }

    [Fact]
    public void Totals_AreCumulative_IntervalCountersReset()
    {
        var s = new FetchPipeStats("MatPipe");
        s.Begin(); s.EndTimed(1, true, 10);
        s.TakeInterval();
        s.Begin(); s.EndTimed(1, false, 0);

        var snap = s.TakeInterval();

        Assert.Equal(2, snap.Requests);
        Assert.Equal(1, snap.IntervalRequests);
        Assert.Equal(1, snap.IntervalFailed);
        Assert.Equal(10, snap.Bytes);
    }

    [Fact]
    public void TakeLine_NamesThePipe_AndGoesQuietAgainWhenNothingHappens()
    {
        var s = new FetchPipeStats("MeshPipe");
        s.Begin(); s.EndTimed(12, true, 4096);

        string? line = s.TakeLine();

        Assert.NotNull(line);
        Assert.StartsWith("[MeshPipe] ", line);
        Assert.Contains("req=1", line);
        Assert.Contains("done=1", line);
        Assert.Contains("failed=0", line);
        Assert.Contains("p50=12ms", line);
        Assert.Null(s.TakeLine());
    }

    [Fact]
    public void MoreSamplesThanTheBuffer_StillGivesSanePercentiles()
    {
        var s = new FetchPipeStats("TexHttp", sampleCapacity: 64);
        for (int i = 0; i < 1000; i++)
        {
            s.Begin();
            s.EndTimed(i < 900 ? 10 : 500, true, 1);
        }

        var snap = s.TakeInterval();

        Assert.Equal(1000, snap.IntervalRequests);
        Assert.True(snap.P50Ms is 10 or 500);
    }
}
