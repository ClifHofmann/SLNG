using Xunit;

namespace SLNG.Core.Tests;

/// <summary>The verbose-line sink: <c>app/</c> decides where a verbose line goes (console under <c>--diag</c>, the debug file alone otherwise).</summary>
[Collection("Diag static state")]
public class DiagTests
{
    [Fact]
    public void A_verbose_line_goes_to_the_sink_when_one_is_set()
    {
        var before = Diag.Sink;
        try
        {
            var lines = new List<string>();
            Diag.Sink = lines.Add;

            Diag.Write("[Tag] one");
            Diag.Write("[Tag] two");

            Assert.Equal(2, lines.Count);
            Assert.StartsWith("[Tag] t=", lines[0]);
            Assert.EndsWith(" one", lines[0]);
            Assert.EndsWith(" two", lines[1]);
        }
        finally
        {
            Diag.Sink = before;
        }
    }

    [Fact]
    public void A_verbose_line_carries_the_monotonic_time_right_after_its_tag()
    {
        var sinkBefore = Diag.Sink;
        var clockBefore = Diag.Clock;
        try
        {
            var lines = new List<string>();
            Diag.Sink = lines.Add;
            double now = 12.34;
            Diag.Clock = () => now;

            Diag.Write("[TexWait] req=5 inflight=2");
            now = 130.0;
            Diag.Write("[TexWait] req=9 inflight=0");

            // The tag stays first so a filter on the line start keeps working; the time is the next token.
            Assert.Equal("[TexWait] t=12.3 req=5 inflight=2", lines[0]);
            Assert.Equal("[TexWait] t=130.0 req=9 inflight=0", lines[1]);
        }
        finally
        {
            Diag.Sink = sinkBefore;
            Diag.Clock = clockBefore;
        }
    }

    [Fact]
    public void A_line_without_a_tag_gets_the_time_in_front()
    {
        var clockBefore = Diag.Clock;
        try
        {
            Diag.Clock = () => 2.0;
            Assert.Equal("t=2.0 plain text", Diag.Stamp("plain text"));
            Assert.Equal("t=2.0 [", Diag.Stamp("["));
        }
        finally
        {
            Diag.Clock = clockBefore;
        }
    }

    [Fact]
    public void The_default_clock_never_runs_backwards()
    {
        var clockBefore = Diag.Clock;
        try
        {
            Diag.Clock = null;
            double a = Diag.NowSeconds;
            double b = Diag.NowSeconds;
            Assert.True(a >= 0);
            Assert.True(b >= a);
        }
        finally
        {
            Diag.Clock = clockBefore;
        }
    }

    [Fact]
    public void Without_a_sink_a_verbose_line_does_not_throw()
    {
        var before = Diag.Sink;
        var err = Console.Error;
        try
        {
            Diag.Sink = null;
            var captured = new StringWriter();
            Console.SetError(captured);

            Diag.Write("[Tag] fallback");

            Assert.Contains("[Tag] t=", captured.ToString());
            Assert.Contains(" fallback", captured.ToString());
        }
        finally
        {
            Console.SetError(err);
            Diag.Sink = before;
        }
    }
}

[CollectionDefinition("Diag static state", DisableParallelization = true)]
public class DiagStaticStateCollection { }
