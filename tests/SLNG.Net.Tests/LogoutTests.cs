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
