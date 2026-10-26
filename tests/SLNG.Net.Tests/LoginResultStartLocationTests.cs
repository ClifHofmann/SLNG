using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-UI-72 -- the login reply's <c>start_location</c> and <c>first_login</c> reach the app on the
/// <see cref="LoginResult"/>, where the "your start was not honoured" notice is decided.
/// </summary>
public class LoginResultStartLocationTests
{
    [Fact]
    public void Ok_carries_the_grids_start_location_and_first_login()
    {
        var r = LoginResult.Ok("agent", "session", "welcome", mfaHash: null, startLocation: "safe", firstLogin: true);

        Assert.Equal("safe", r.StartLocation);
        Assert.True(r.FirstLogin);
        Assert.Contains("StartLocation = safe", r.ToString());
        Assert.Contains("FirstLogin = True", r.ToString());
    }

    [Fact]
    public void Ok_without_them_is_unknown_and_not_a_first_login()
    {
        var r = LoginResult.Ok("agent", "session", "welcome");

        Assert.Null(r.StartLocation);
        Assert.False(r.FirstLogin);
        Assert.Null(LoginResult.Ok("a", "s", "m", null, "", false).StartLocation);
    }

    [Fact]
    public void A_failure_has_no_start_location()
    {
        var r = LoginResult.Fail("key", "bad password");

        Assert.Null(r.StartLocation);
        Assert.False(r.FirstLogin);
    }
}
