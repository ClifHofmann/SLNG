using Xunit;

namespace SLNG.Core.Tests;

public class TeleportFailureReasonsTests
{
    [Theory]
    [InlineData("no_host", "ui.teleport.reason_no_host")]
    [InlineData("noaccess_tport", "ui.teleport.reason_noaccess_tport")]
    [InlineData("  no_host\r\n", "ui.teleport.reason_no_host")]
    [InlineData("NoHelpIslandTP", "ui.teleport.reason_NoHelpIslandTP")]
    public void A_known_id_names_its_key(string reason, string key) =>
        Assert.Equal(key, TeleportFailureReasons.KeyFor(reason));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Teleport failed.")]
    [InlineData("NO_HOST")]
    [InlineData("Sorry, but the system was unable to complete the teleport connection.")]
    public void Anything_else_is_shown_as_it_came(string? reason) =>
        Assert.Null(TeleportFailureReasons.KeyFor(reason));
}
