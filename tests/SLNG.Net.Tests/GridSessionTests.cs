using System.Reflection;
using LibreMetaverse;
using SLNG.Core;
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

    [Fact]
    public async Task TeleportToLandmarkAsync_without_connection_fails_gracefully()
    {
        using var session = new GridSession();

        var result = await session.TeleportToLandmarkAsync(Guid.NewGuid());

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task CreateLandmarkHereAsync_without_connection_fails_gracefully()
    {
        using var session = new GridSession();

        var result = await session.CreateLandmarkHereAsync("Test Landmark", "notes", Guid.NewGuid());

        Assert.False(result.Success);
        Assert.Null(result.ItemId);
        Assert.Null(result.AssetId);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public void IsInLandmarksSubtree_returns_false_when_not_connected()
    {
        using var session = new GridSession();
        Assert.False(session.IsInLandmarksSubtree(Guid.NewGuid()));
    }

    // M5-4: llDialog packet parsing. GridSession's OnScriptDialog handler is private (mirrors
    // every other LibreMetaverse-facing handler in this class -- no LibreMetaverse type may
    // cross the SLNG.Net boundary, AGENTS.md's layering rule), so it's invoked via reflection
    // with a hand-built ScriptDialogEventArgs, the same shape LibreMetaverse's ScriptDialogHandler
    // raises off the wire packet.
    [Fact]
    public void OnScriptDialog_maps_wire_event_to_ScriptDialogEvent()
    {
        using var session = new GridSession();
        ScriptDialogEvent? received = null;
        session.ScriptDialogReceived += (s, e) => received = e;

        var objectId = UUID.Random();
        var ownerId = UUID.Random();
        var args = new ScriptDialogEventArgs(
            message: "Pick a color",
            objectName: "Color Picker",
            imageID: UUID.Zero,
            objectID: objectId,
            firstName: "Jane",
            lastName: "Doe",
            chatChannel: -1234,
            buttons: new List<string> { "Red", "Green", "Blue" },
            ownerID: ownerId);

        var method = typeof(GridSession).GetMethod("OnScriptDialog", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(session, new object?[] { null, args });

        Assert.NotNull(received);
        Assert.Equal(objectId.Guid, received!.ObjectId);
        Assert.Equal("Color Picker", received.ObjectName);
        Assert.Equal(ownerId.Guid, received.OwnerId);
        Assert.Equal("Jane Doe", received.OwnerName);
        Assert.Equal("Pick a color", received.Message);
        Assert.Equal(-1234, received.Channel);
        Assert.Equal(new[] { "Red", "Green", "Blue" }, received.ButtonLabels);
    }

    // Channel-reply formatting: ReplyToScriptDialog must not throw when there's no live
    // connection to send the ScriptDialogReply packet over -- same "no-op gracefully while
    // disconnected" contract as every other GridSession send path in this file.
    [Fact]
    public void ReplyToScriptDialog_without_connection_does_not_throw()
    {
        using var session = new GridSession();
        var exception = Record.Exception(() =>
            session.ReplyToScriptDialog(Guid.NewGuid(), channel: -1234, buttonIndex: 1, buttonLabel: "Green"));

        Assert.Null(exception);
    }
}
