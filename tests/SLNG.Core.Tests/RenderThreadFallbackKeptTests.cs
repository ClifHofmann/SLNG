using System;
using System.IO;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class RenderThreadFallbackKeptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "slng-fallback-" + Guid.NewGuid().ToString("N"));

    public RenderThreadFallbackKeptTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Consuming_a_marker_can_keep_a_copy_for_the_preferences_page()
    {
        RenderThreadFallback.WriteMarker(P("marker.json"), 10.3);

        Assert.True(RenderThreadFallback.TryConsumeMarker(P("marker.json"), out var marker, P("kept.json")));
        Assert.Equal(10.3, marker!.StalledSeconds);
        Assert.False(File.Exists(P("marker.json")));

        var kept = RenderThreadFallback.ReadKept(P("kept.json"));
        Assert.NotNull(kept);
        Assert.Equal(10.3, kept!.StalledSeconds);
        Assert.Equal(marker.Timestamp, kept.Timestamp);
    }

    [Fact]
    public void Without_a_keep_path_nothing_is_kept()
    {
        RenderThreadFallback.WriteMarker(P("marker.json"), 4);

        Assert.True(RenderThreadFallback.TryConsumeMarker(P("marker.json"), out _));
        Assert.Null(RenderThreadFallback.ReadKept(P("kept.json")));
    }

    [Fact]
    public void A_missing_or_damaged_kept_copy_reads_as_none()
    {
        Assert.Null(RenderThreadFallback.ReadKept(P("absent.json")));

        File.WriteAllText(P("bad.json"), "{ not json");
        Assert.Null(RenderThreadFallback.ReadKept(P("bad.json")));
    }

    [Fact]
    public void Clearing_removes_the_kept_copy_and_tolerates_none()
    {
        RenderThreadFallback.WriteMarker(P("marker.json"), 7);
        RenderThreadFallback.TryConsumeMarker(P("marker.json"), out _, P("kept.json"));

        RenderThreadFallback.ClearKept(P("kept.json"));
        Assert.Null(RenderThreadFallback.ReadKept(P("kept.json")));

        RenderThreadFallback.ClearKept(P("kept.json"));
    }

    [Fact]
    public void A_damaged_marker_keeps_nothing()
    {
        File.WriteAllText(P("marker.json"), "{ not json");

        Assert.False(RenderThreadFallback.TryConsumeMarker(P("marker.json"), out _, P("kept.json")));
        Assert.Null(RenderThreadFallback.ReadKept(P("kept.json")));
    }
}
