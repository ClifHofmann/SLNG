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

            Assert.Equal(new[] { "[Tag] one", "[Tag] two" }, lines);
        }
        finally
        {
            Diag.Sink = before;
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

            Assert.Contains("[Tag] fallback", captured.ToString());
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
