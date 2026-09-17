using Xunit;

namespace SLNG.Core.Tests;

/// <summary>Ports <c>LLMediaEntry::checkUrlAgainstWhitelist</c> (llmediaentry.cpp:474-511) --
/// these pin the behaviour against real cases from that source rather than a guessed reading of
/// "wildcards", including the deliberately-preserved quirk that a schemeless filter always passes
/// the scheme check.</summary>
public class MediaWhitelistTests
{
    [Fact]
    public void EmptyWhitelist_AllowsEverything()
    {
        Assert.True(MediaWhitelist.IsAllowed("https://example.com/page", Array.Empty<string>()));
        Assert.True(MediaWhitelist.IsAllowed("https://example.com/page", null));
    }

    [Fact]
    public void ExactMatch_Passes()
    {
        Assert.True(MediaWhitelist.IsAllowed(
            "https://example.com/page", new[] { "https://example.com/page" }));
    }

    [Fact]
    public void DifferentAuthority_IsRejected()
    {
        Assert.False(MediaWhitelist.IsAllowed(
            "https://evil.example.com/page", new[] { "https://example.com/page" }));
    }

    [Fact]
    public void WildcardSubdomain_Matches()
    {
        Assert.True(MediaWhitelist.IsAllowed(
            "https://www.example.com/page", new[] { "https://*.example.com/*" }));
    }

    [Fact]
    public void WildcardPath_RestrictsToPathPrefix()
    {
        Assert.True(MediaWhitelist.IsAllowed(
            "https://example.com/videos/1", new[] { "https://example.com/videos/*" }));
        Assert.False(MediaWhitelist.IsAllowed(
            "https://example.com/other/1", new[] { "https://example.com/videos/*" }));
    }

    [Fact]
    public void SchemelessFilter_AlwaysPassesTheSchemeCheck()
    {
        // llmediaentry.cpp:495-499: scheme_passes is computed from the filter's OWN (empty)
        // scheme BEFORE it is reparsed with the "https://" default for authority/path -- so a
        // schemeless filter matches http AND https candidates against the same host/path, not
        // just https.
        Assert.True(MediaWhitelist.IsAllowed(
            "http://example.com/page", new[] { "example.com/page" }));
        Assert.True(MediaWhitelist.IsAllowed(
            "https://example.com/page", new[] { "example.com/page" }));
    }

    [Fact]
    public void WrongScheme_IsRejectedWhenFilterHasAnExplicitOne()
    {
        Assert.False(MediaWhitelist.IsAllowed(
            "http://example.com/page", new[] { "https://example.com/page" }));
    }

    [Fact]
    public void SecondEntry_CanStillMatch_AfterAnEarlierMiss()
    {
        Assert.True(MediaWhitelist.IsAllowed(
            "https://good.example.com/page",
            new[] { "https://bad.example.com/*", "https://good.example.com/*" }));
    }

    [Fact]
    public void UnparseableCandidate_IsRejected()
    {
        Assert.False(MediaWhitelist.IsAllowed("not a url", new[] { "https://example.com/*" }));
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        Assert.True(MediaWhitelist.IsAllowed(
            "https://EXAMPLE.com/PAGE", new[] { "https://example.com/page" }));
    }
}
