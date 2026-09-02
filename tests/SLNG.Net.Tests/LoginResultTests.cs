using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

public class LoginResultTests
{
    [Fact]
    public void Ok_sets_success_and_ids()
    {
        var r = LoginResult.Ok("agent-1", "session-1", "welcome");

        Assert.True(r.Success);
        Assert.Equal("agent-1", r.AgentId);
        Assert.Equal("session-1", r.SessionId);
        Assert.Equal("welcome", r.Message);
        Assert.Null(r.ErrorKey);
    }

    [Fact]
    public void Fail_sets_failure_and_error()
    {
        var r = LoginResult.Fail("key", "bad password");

        Assert.False(r.Success);
        Assert.Equal("key", r.ErrorKey);
        Assert.Equal("bad password", r.Message);
        Assert.Null(r.AgentId);
        Assert.Null(r.SessionId);
    }

    [Fact]
    public void Credentials_default_channel_and_version()
    {
        var c = new LoginCredentials
        {
            FirstName = "Test",
            LastName = "User",
            Password = "secret",
            GridLoginUri = LoginCredentials.OpenSimLocalLoginUri,
        };

        // TPV Policy §5.b: the channel must not carry any part of a Linden Lab trademark,
        // including "SL" -- "SLNG" (the codebase's internal name) fails on the letter.
        Assert.Equal("Puris", c.Channel);
        Assert.DoesNotContain("SL", c.Channel, System.StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(c.Version));
    }
}
