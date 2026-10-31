using Microsoft.Extensions.Logging;
using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>Where a LibreMetaverse log line goes: errors on the console, everything below on the verbose sink only.</summary>
[Collection("Diag static state")]
public class LibreMetaverseLogRelayTests
{
    [Theory]
    [InlineData(LogLevel.Trace, false)]
    [InlineData(LogLevel.Debug, false)]
    [InlineData(LogLevel.Information, false)]
    [InlineData(LogLevel.Warning, false)]
    [InlineData(LogLevel.Error, true)]
    [InlineData(LogLevel.Critical, true)]
    public void Only_errors_are_printed_on_the_console(LogLevel level, bool onConsole) =>
        Assert.Equal(onConsole, LibreMetaverseLogRelay.PrintsOnConsole(level));

    [Fact]
    public void A_warning_reaches_the_verbose_sink_only_while_verbose_is_on()
    {
        var sinkBefore = Diag.Sink;
        bool verboseBefore = Diag.Verbose;
        try
        {
            var lines = new List<string>();
            Diag.Sink = lines.Add;
            using var provider = new LibreMetaverseLogRelay("SLNG", (_, _) => true);
            var logger = provider.CreateLogger("x");

            Diag.Verbose = false;
            logger.LogWarning("quiet");
            Assert.Empty(lines);

            Diag.Verbose = true;
            logger.LogWarning("loud");
            // Every verbose line carries the monotonic time (FEAT-PERF-28); this one has no [Tag], so it goes in front.
            var line = Assert.Single(lines);
            Assert.Matches(@"^t=\d+\.\d warn: SLNG\[0\] loud$", line);
        }
        finally
        {
            Diag.Sink = sinkBefore;
            Diag.Verbose = verboseBefore;
        }
    }
}

[CollectionDefinition("Diag static state", DisableParallelization = true)]
public class DiagStaticStateCollection { }
