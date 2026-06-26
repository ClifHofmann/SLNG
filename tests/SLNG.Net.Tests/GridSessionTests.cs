using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

public class GridSessionTests
{
    [Fact]
    public async Task LoginAsync_to_unreachable_grid_fails_gracefully()
    {
        using var session = new GridSession();
        var creds = new LoginCredentials
        {
            FirstName = "Test",
            LastName = "User",
            Password = "secret",
            GridLoginUri = "http://127.0.0.1:1/", // connection refused -> fails fast
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await session.LoginAsync(creds, cts.Token);

        Assert.False(result.Success);
        Assert.False(session.IsConnected);
        // We must surface a diagnostic, not swallow the failure.
        Assert.True(
            !string.IsNullOrWhiteSpace(result.Message) || !string.IsNullOrWhiteSpace(result.ErrorKey),
            "A failed login must report a message or an error key.");
    }
}
