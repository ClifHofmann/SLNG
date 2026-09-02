using System.Threading.Tasks;
using SLNG.Core;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// "Age settings" -- Second Life's content-rating preference (General/Moderate/Adult), requested
/// via the <c>UpdateAgentInformation</c> capability and reported at login through
/// <c>agent_access_max</c> / <c>agent_region_access</c>. <see cref="MaturityAccess"/> is the wire
/// encoding both ends of that use; pinned here against the reference viewer's own mapping
/// (<c>LLViewerRegion::accessToShortString</c>/<c>shortStringToAccess</c>,
/// <c>LLAgentAccess::convertTextToMaturity</c>).
/// </summary>
public class MaturityAccessTests
{
    [Theory]
    [InlineData(MaturityLevel.General, "PG")]
    [InlineData(MaturityLevel.Moderate, "M")]
    [InlineData(MaturityLevel.Adult, "A")]
    public void Encodes_the_wire_short_string(MaturityLevel level, string expected)
    {
        Assert.Equal(expected, MaturityAccess.ToShortString(level));
    }

    [Theory]
    [InlineData("PG", MaturityLevel.General)]
    [InlineData("P", MaturityLevel.General)]     // convertTextToMaturity reads only the first char
    [InlineData("M", MaturityLevel.Moderate)]
    [InlineData("A", MaturityLevel.Adult)]
    [InlineData("a", MaturityLevel.Adult)]       // case-insensitive
    [InlineData("", MaturityLevel.General)]      // OpenSim: the field is never sent
    [InlineData(null, MaturityLevel.General)]
    [InlineData("garbage", MaturityLevel.General)] // matches convertTextToMaturity's own fallback
    public void Decodes_the_wire_short_string(string? code, MaturityLevel expected)
    {
        Assert.Equal(expected, MaturityAccess.FromShortString(code));
    }

    [Fact]
    public void Round_trips_every_level()
    {
        foreach (var level in new[] { MaturityLevel.General, MaturityLevel.Moderate, MaturityLevel.Adult })
            Assert.Equal(level, MaturityAccess.FromShortString(MaturityAccess.ToShortString(level)));
    }

    [Fact]
    public void Ordinal_order_matches_severity_for_ceiling_comparisons()
    {
        // GridSession compares levels with a plain `>` against AccountMaturityMax -- this is the
        // contract that makes that valid instead of coincidental.
        Assert.True(MaturityLevel.General < MaturityLevel.Moderate);
        Assert.True(MaturityLevel.Moderate < MaturityLevel.Adult);
    }

    [Fact]
    public void GridSession_defaults_to_General_and_no_support_before_any_login()
    {
        // Before a session has ever connected, nothing has told it otherwise -- General is the
        // only safe default (never grant a higher ceiling than the grid actually reported), and
        // SupportsMaturityPreference must not claim a capability that was never fetched.
        using var session = new GridSession();

        Assert.Equal(MaturityLevel.General, session.AccountMaturityMax);
        Assert.Equal(MaturityLevel.General, session.PreferredMaturity);
        Assert.False(session.SupportsMaturityPreference);
    }

    [Fact]
    public async Task SetPreferredMaturityAsync_refuses_when_not_connected()
    {
        using var session = new GridSession();

        var (success, actual, error) = await session.SetPreferredMaturityAsync(MaturityLevel.Adult);

        Assert.False(success);
        Assert.Equal(MaturityLevel.General, actual);
        Assert.False(string.IsNullOrEmpty(error));
    }
}
