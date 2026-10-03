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

    [Theory]
    [InlineData("Canceled", false)]
    [InlineData("Login canceled", false)]
    [InlineData("  canceled ", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("No such host is known.", true)]
    [InlineData("The SSL connection could not be established, see inner exception.", true)]
    public void Only_a_message_that_says_something_counts_as_the_reason(string? message, bool expected)
    {
        Assert.Equal(expected, GridSession.IsRealLoginFailureReason(message));
    }
}
