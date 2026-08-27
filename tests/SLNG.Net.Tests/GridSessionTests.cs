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

    // MVP2-1: RequestSit resolves the local id via sim.ObjectsPrimitives before sending anything,
    // so with no connection (CurrentSim is null) it must early-return rather than throw.
    [Fact]
    public void RequestSit_without_connection_does_not_throw()
    {
        using var session = new GridSession();
        var exception = Record.Exception(() => session.RequestSit(localId: 12345));

        Assert.Null(exception);
    }

    // MVP2-1: SitOnGround/Stand forward straight to AgentManager, which LibreMetaverse's own
    // NetworkManager.SendPacket already guards against a null CurrentSim (logs a warning instead
    // of throwing) -- same "no-op gracefully while disconnected" contract as every other send path.
    [Fact]
    public void SitOnGround_without_connection_does_not_throw()
    {
        using var session = new GridSession();
        var exception = Record.Exception(() => session.SitOnGround());

        Assert.Null(exception);
    }

    [Fact]
    public void Stand_without_connection_does_not_throw()
    {
        using var session = new GridSession();
        var exception = Record.Exception(() => session.Stand());

        Assert.Null(exception);
    }

    // Detaching something that is not attached must report that fact rather than claiming success
    // -- DetachAttachmentIntoInv is matched server-side against live attachments, so for an item
    // that only has a stale Current-Outfit link it is a silent no-op ("I click Detach and nothing
    // happens"). With no connection there is no attachment and no COF, which is the same shape.
    [Fact]
    public async Task DetachItemAsync_reports_not_attached_when_nothing_is_worn()
    {
        using var session = new GridSession();

        var result = await session.DetachItemAsync(Guid.NewGuid());

        Assert.False(result.WasAttached);
        Assert.Equal(0, result.StaleLinksRemoved);
    }

    // ObjectDetach-by-localId is the escape hatch for an attachment inventory cannot address.
    // Both entry points must no-op gracefully while disconnected, same as every other send path.
    [Fact]
    public void DetachByLocalId_without_connection_does_not_throw()
    {
        using var session = new GridSession();
        Assert.Null(Record.Exception(() => session.DetachByLocalId(12345)));
    }

    [Fact]
    public void DetachAllAttachments_without_connection_returns_zero()
    {
        using var session = new GridSession();

        Assert.Equal(0, session.DetachAllAttachments(hudOnly: true));
        Assert.Equal(0, session.DetachAllAttachments(hudOnly: false));
    }
}
