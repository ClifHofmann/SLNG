using LibreMetaverse;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-SL-01 — the Third-Party Viewer Policy §1.f contract: the viewer must not tell a grid the
/// user accepted its Terms of Service unless the user actually did.
///
/// The reason these are worth pinning is that the wrong behaviour is the DEFAULT. LibreMetaverse's
/// <c>LoginParams()</c> constructor sets <c>AgreeToTos = true</c> and <c>ReadCritical = true</c>
/// (Login.cs), so a viewer that simply builds a LoginParams and logs in is already asserting
/// acceptance on the user's behalf, with nothing in its own source saying so. The first test here
/// is the guard against that regression silently returning on a package bump.
/// </summary>
public class LoginTermsGateTests
{
    private static LoginCredentials Creds() => new()
    {
        FirstName = "Test",
        LastName = "User",
        Password = "secret",
        GridLoginUri = LoginCredentials.OpenSimLocalLoginUri,
    };

    [Fact]
    public void LibreMetaverse_defaults_both_consent_flags_to_true()
    {
        // Not testing our code -- documenting the trap the rest of this file exists for. If this
        // ever fails, LibreMetaverse changed its default and the comments explaining why we
        // overwrite it need revisiting.
        using var client = new GridClient();
        var raw = new LoginParams(client, "Test", "User", "secret", "SLNG", "1.0",
            LoginCredentials.OpenSimLocalLoginUri);

        Assert.True(raw.AgreeToTos);
        Assert.True(raw.ReadCritical);
    }

    [Fact]
    public void Credentials_default_to_not_having_accepted_anything()
    {
        var creds = Creds();

        Assert.False(creds.AgreeToTos);
        Assert.False(creds.ReadCritical);
    }

    [Fact]
    public void BuildLoginParams_overwrites_LibreMetaverses_true_defaults()
    {
        using var client = new GridClient();

        var login = GridSession.BuildLoginParams(client, Creds());

        Assert.False(login.AgreeToTos);
        Assert.False(login.ReadCritical);
    }

    [Fact]
    public void BuildLoginParams_forwards_a_real_acceptance()
    {
        using var client = new GridClient();
        var creds = Creds() with { AgreeToTos = true, ReadCritical = true };

        var login = GridSession.BuildLoginParams(client, creds);

        Assert.True(login.AgreeToTos);
        Assert.True(login.ReadCritical);
    }

    [Fact]
    public void BuildLoginParams_carries_identity_and_start_location()
    {
        using var client = new GridClient();
        var creds = Creds() with { Channel = "Puris", Version = "0.20.0-alpha", StartLocation = "home" };

        var login = GridSession.BuildLoginParams(client, creds);

        Assert.Equal("Test", login.FirstName);
        Assert.Equal("User", login.LastName);
        Assert.Equal("home", login.Start);
        Assert.Contains("Puris", login.Channel);
    }

    [Theory]
    [InlineData("tos", true, false)]
    [InlineData("critical", false, true)]
    [InlineData("key", false, false)]      // wrong password -- not answerable, just a failure
    [InlineData("presence", false, false)] // still logged in elsewhere
    public void Failure_reasons_that_are_answerable_are_told_apart(string reason, bool tos, bool critical)
    {
        var result = LoginResult.Fail(reason, "text from the grid");

        Assert.Equal(tos, result.RequiresTermsAcceptance);
        Assert.Equal(critical, result.RequiresCriticalAcknowledgement);
    }

    [Fact]
    public void A_successful_login_never_asks_for_acceptance()
    {
        // Guard against the gate helpers keying off ErrorKey alone: a success carries no reason,
        // but a future refactor that populated one must not put the login screen into a ToS loop.
        var result = LoginResult.Ok("agent", "session", "welcome");

        Assert.False(result.RequiresTermsAcceptance);
        Assert.False(result.RequiresCriticalAcknowledgement);
    }

    [Fact]
    public void Beta_grid_uri_is_aditi_over_https()
    {
        Assert.Equal("https://login.aditi.lindenlab.com/cgi-bin/login.cgi",
            LoginCredentials.SecondLifeBetaLoginUri);
        Assert.NotEqual(LoginCredentials.SecondLifeLoginUri, LoginCredentials.SecondLifeBetaLoginUri);
    }

    [Theory]
    [InlineData(LoginCredentials.SecondLifeLoginUri, true)]
    [InlineData(LoginCredentials.SecondLifeBetaLoginUri, true)]
    [InlineData(LoginCredentials.OpenSimLocalLoginUri, false)]
    [InlineData("http://hg.osgrid.org/", false)]
    [InlineData(null, false)]
    public void Linden_grids_are_told_apart_from_OpenSim(string? uri, bool expected)
    {
        // Gates two things this audit found: BakeAvatarAsync's client-side-bake refusal on a
        // real SL region, and DumpPreview's refusal to export other creators' decoded wearable
        // textures to disk (TPV Policy §2.b). Both must fire on Agni AND Aditi, and neither must
        // fire on OpenSim, which has no such restriction.
        Assert.Equal(expected, GridSession.IsLindenLabUri(uri));
    }

    [Fact]
    public void Channel_default_carries_no_Linden_trademark_fragment()
    {
        // TPV Policy §5.b: "Your Third-Party Viewer name must not be ... use any part of a
        // Linden Lab trademark, including 'Second,' 'Life,' 'SL,' or 'Linden.'" The channel is
        // the viewer identifier a sim log actually shows, so it is held to the same rule as the
        // displayed name (AboutWindow.ViewerName / ViewerChannel).
        var channel = Creds().Channel;

        foreach (var forbidden in new[] { "second", "life", "sl", "linden" })
            Assert.DoesNotContain(forbidden, channel, System.StringComparison.OrdinalIgnoreCase);
    }
}
