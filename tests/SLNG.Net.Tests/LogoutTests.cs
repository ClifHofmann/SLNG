using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

public class LogoutTests
{
    [Fact]
    public async Task LogoutAsync_WithoutAConnection_ReturnsAtOnce()
    {
        using var session = new GridSession();
        var started = System.Diagnostics.Stopwatch.StartNew();

        bool done = await session.LogoutAsync(TimeSpan.FromSeconds(30));

        Assert.True(done);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Logout_WithoutAConnection_DoesNothing()
    {
        using var session = new GridSession();

        session.Logout(); // must not throw or block
    }
}

public class LoginFailureMessageTests
{
    [Fact]
    public void The_no_response_message_carries_the_reason_and_tells_the_person_what_to_check()
    {
        string text = GridSession.NoLoginResponseMessage(" (The SSL connection could not be established)");

        Assert.Contains("no login response (The SSL connection could not be established)", text);
        Assert.Contains("firewall", text);
        Assert.Contains("date and time", text);
    }

    [Fact]
    public void Without_a_reason_the_message_still_reads_cleanly()
    {
        string text = GridSession.NoLoginResponseMessage("");

        Assert.StartsWith("Grid returned no login response.", text);
    }
}
