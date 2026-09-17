using Xunit;

namespace SLNG.Core.Tests;

/// <summary>SL's MOAP doorbell format, <c>x-mv:&lt;10-digit sequence&gt;/&lt;agent-uuid&gt;</c>
/// (lltextureentry.cpp:53, :745-797) -- the only signal on the wire that a prim's media changed;
/// the actual content always requires a separate <c>ObjectMedia</c> fetch.</summary>
public class MediaVersionStringTests
{
    [Fact]
    public void IsMediaVersion_TrueOnlyForTheXMvPrefix()
    {
        Assert.True(MediaVersionString.IsMediaVersion("x-mv:0000000001/11111111-1111-1111-1111-111111111111"));
        Assert.False(MediaVersionString.IsMediaVersion("http://example.com/media.html"));
        Assert.False(MediaVersionString.IsMediaVersion(null));
        Assert.False(MediaVersionString.IsMediaVersion(""));
    }

    [Fact]
    public void TryParse_SplitsSequenceAndAgentId()
    {
        bool ok = MediaVersionString.TryParse(
            "x-mv:0000000042/11111111-2222-3333-4444-555555555555", out int sequence, out Guid agentId);

        Assert.True(ok);
        Assert.Equal(42, sequence);
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), agentId);
    }

    [Fact]
    public void TryParse_FailsForALegacyParcelMediaUrl()
    {
        // The x-mv: guard exists precisely so a legacy (non-MOAP) MediaURL never triggers a fetch
        // (llvovolume.cpp:552-554).
        bool ok = MediaVersionString.TryParse("http://example.com/media.html", out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryParse_FailsWithoutASlashSeparator()
    {
        Assert.False(MediaVersionString.TryParse("x-mv:0000000001", out _, out _));
    }
}
