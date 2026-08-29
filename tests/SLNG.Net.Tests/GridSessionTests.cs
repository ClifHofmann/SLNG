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

    // MVP2-3: minimap radar. OnCoarseLocationUpdate is private (same LMV-boundary reasoning as
    // OnScriptDialog above), invoked via reflection with a hand-built CoarseLocationUpdateEventArgs
    // -- the same shape GridManager.CoarseLocationHandler raises off the wire packet.
    [Fact]
    public void OnCoarseLocationUpdate_maps_wire_event_to_NearbyAvatarsEvent()
    {
        using var session = new GridSession();
        NearbyAvatarsEvent? received = null;
        session.NearbyAvatarsUpdated += (s, e) => received = e;

        using var client = new GridClient();
        var sim = new Simulator(client, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 9000), 1234UL);
        var agentId = UUID.Random();
        var positions = new Dictionary<UUID, LibreMetaverse.Vector3> { [agentId] = new LibreMetaverse.Vector3(10, 20, 30) };
        var args = new CoarseLocationUpdateEventArgs(sim, positions, new List<UUID> { agentId }, new List<UUID>());

        var method = typeof(GridSession).GetMethod("OnCoarseLocationUpdate", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(session, new object?[] { null, args });

        Assert.NotNull(received);
        Assert.Equal(sim.Handle, received!.RegionHandle);
        var avatar = Assert.Single(received.Avatars);
        Assert.Equal(agentId.Guid, avatar.AgentId);
        Assert.Equal(10f, avatar.Position.X);
        Assert.Equal(20f, avatar.Position.Y);
        Assert.Equal(30f, avatar.Position.Z);
    }

    // MVP2-3: grid-map tile resolution. OnGridRegion converts LibreMetaverse's GridRegion struct
    // to the neutral MapRegionInfo DTO -- no LMV type may cross this boundary (AGENTS.md).
    [Fact]
    public void OnGridRegion_maps_wire_event_to_MapRegionInfo()
    {
        using var session = new GridSession();
        MapRegionInfo? received = null;
        session.RegionDiscovered += (s, e) => received = e;

        var imageId = UUID.Random();
        var region = new GridRegion
        {
            X = 1000,
            Y = 1000,
            Name = "Test Region",
            MapImageID = imageId,
            RegionHandle = ((ulong)(1000u * 256) << 32) | (1000u * 256),
        };

        var method = typeof(GridSession).GetMethod("OnGridRegion", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(session, new object?[] { null, new GridRegionEventArgs(region) });

        Assert.NotNull(received);
        Assert.Equal("Test Region", received!.Name);
        Assert.Equal(1000, received.GridX);
        Assert.Equal(1000, received.GridY);
        Assert.Equal(256000d, received.GlobalX);
        Assert.Equal(256000d, received.GlobalY);
        Assert.Equal(imageId.Guid, received.MapImageId);
        Assert.Equal(region.RegionHandle, received.RegionHandle);
    }

    [Fact]
    public async Task ResolveRegionByNameAsync_without_connection_returns_null()
    {
        using var session = new GridSession();
        Assert.Null(await session.ResolveRegionByNameAsync("Some Region"));
    }

    [Fact]
    public async Task ResolveRegionByHandleAsync_without_connection_returns_null()
    {
        using var session = new GridSession();
        Assert.Null(await session.ResolveRegionByHandleAsync(12345UL));
    }

    [Fact]
    public async Task TeleportToAsync_without_connection_fails_gracefully()
    {
        using var session = new GridSession();

        var result = await session.TeleportToAsync(12345UL, new System.Numerics.Vector3(1, 2, 3));

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public void RequestMapBlocks_without_connection_does_not_throw()
    {
        using var session = new GridSession();
        Assert.Null(Record.Exception(() => session.RequestMapBlocks(0, 0, 10, 10)));
    }

    [Fact]
    public void TeleportToGlobalPosition_without_connection_does_not_throw()
    {
        using var session = new GridSession();
        Assert.Null(Record.Exception(() =>
            session.TeleportToGlobalPosition("Some Region", 256000.0, 256000.0, 25.0)));
    }

    // BUG-NET-03: neighbor-region visibility hinges entirely on this flag. LibreMetaverse 3.1.3
    // defaults Agent.MultipleSims to FALSE, and with it off NetworkManager.EnableSimulatorHandler
    // drops every EnableSimulator the grid sends -- no neighbor circuit is ever opened and the
    // world ends at the current region's border. Pin it so a future settings cleanup can't
    // silently regress cross-sim rendering.
    [Fact]
    public void GridSession_enables_multiple_sims_for_neighbor_regions()
    {
        using var session = new GridSession();

        var client = typeof(GridSession)
            .GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(session)!;
        var settings = client.GetType().GetProperty("Settings")!.GetValue(client)!;
        var agent = settings.GetType().GetProperty("Agent")!.GetValue(settings)!;
        var multipleSims = (bool)agent.GetType().GetField("MultipleSims")!.GetValue(agent)!;

        Assert.True(multipleSims, "Agent.MultipleSims must stay true (BUG-NET-03).");
    }

    // FEAT-UI-18: the loading overlay is driven off GridSession.TeleportProgress, a neutral event
    // mapped from LibreMetaverse's TeleportEventArgs by the private OnLmvTeleportProgress handler
    // (no LMV type crosses the boundary -- AGENTS.md). Same reflection pattern as OnScriptDialog
    // above: hand-build the wire event args and invoke the handler directly.
    [Theory]
    [InlineData(TeleportStatus.Start, TeleportStage.Started)]
    [InlineData(TeleportStatus.Progress, TeleportStage.Progress)]
    [InlineData(TeleportStatus.Failed, TeleportStage.Failed)]
    [InlineData(TeleportStatus.Finished, TeleportStage.Finished)]
    [InlineData(TeleportStatus.Cancelled, TeleportStage.Cancelled)]
    public void OnLmvTeleportProgress_maps_status_to_stage(TeleportStatus status, TeleportStage expected)
    {
        using var session = new GridSession();
        TeleportProgressEvent? received = null;
        session.TeleportProgress += (s, e) => received = e;

        var args = new TeleportEventArgs("Arriving...", status, (TeleportFlags)0);
        var method = typeof(GridSession).GetMethod("OnLmvTeleportProgress", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(session, new object?[] { null, args });

        Assert.NotNull(received);
        Assert.Equal(expected, received!.Stage);
        Assert.Equal("Arriving...", received.Message);
    }

    // BUG-NET-03: with MultipleSims connecting neighbor circuits, avatar updates arrive from
    // neighbor sims too -- including our own child-agent copy with a foreign LocalId. Those must
    // NOT reach WorldSimulation (they churned the local agent entity -> skeleton rebuild ->
    // ObjectDisposedException in AvatarRenderer.UpdateAttachment). The current sim is the sole
    // authority for avatars, so an update from any simulator that isn't CurrentSim is dropped.
    [Fact]
    public void OnAvatarUpdate_from_non_current_sim_is_dropped()
    {
        using var session = new GridSession();
        bool raised = false;
        session.AvatarUpdateReceived += (s, e) => raised = true;

        using var client = new GridClient();
        // A fabricated simulator that is not (and cannot be) client.Network.CurrentSim.
        var sim = new Simulator(client, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 9000), 1234UL);
        var avatar = new Avatar { ID = UUID.Random(), LocalID = 42 };
        var args = new AvatarUpdateEventArgs(sim, avatar, timeDilation: 0, isNew: false);

        var method = typeof(GridSession).GetMethod("OnAvatarUpdate", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(session, new object?[] { null, args });

        Assert.False(raised, "avatar updates from a non-current sim must be dropped (BUG-NET-03)");
    }

    // TeleportStatus.None is not a real in-flight stage and must be dropped, not surfaced --
    // mirrors the LoginStatus.None handling in the login-stage mapping.
    [Fact]
    public void OnLmvTeleportProgress_drops_None_status()
    {
        using var session = new GridSession();
        bool raised = false;
        session.TeleportProgress += (s, e) => raised = true;

        var args = new TeleportEventArgs("", TeleportStatus.None, (TeleportFlags)0);
        var method = typeof(GridSession).GetMethod("OnLmvTeleportProgress", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(session, new object?[] { null, args });

        Assert.False(raised);
    }

    // ---- FEAT-AVATAR-01: system-wearable remove/add routing ---------------------------------

    // The routing decision: a system wearable (typed InventoryWearable, or asset type Clothing/
    // Bodypart) goes through AppearanceManager.AddToOutfit/RemoveFromOutfit; everything else is an
    // attachment and keeps the Attach/Detach path. This is the "picks the right LibreMetaverse
    // call" acceptance criterion, extracted as a pure function so it needs no live client.
    [Theory]
    [InlineData(true, 0, true)]     // typed as InventoryWearable
    [InlineData(false, 5, true)]    // AssetType.Clothing
    [InlineData(false, 13, true)]   // AssetType.Bodypart
    [InlineData(false, 6, false)]   // AssetType.Object
    [InlineData(false, 3, false)]   // AssetType.Landmark
    [InlineData(false, -1, false)]  // unknown
    public void ClassifyItem_routes_wearables_and_attachments(bool isWearable, int assetType, bool expectWearable)
    {
        var expected = expectWearable ? GridSession.WearableKind.Wearable : GridSession.WearableKind.Attachment;
        Assert.Equal(expected, GridSession.ClassifyItem(isWearable, assetType));
    }

    // The gate the FEAT-AVATAR-01 landmine calls for: a wearable edit (each schedules an
    // AgentSetAppearance) is refused unless LibreMetaverse holds a full, non-default VisualParams
    // set. Empty / too-short / all-default (0 or 128) must all read as unsafe.
    [Fact]
    public void VisualParamsHealthy_rejects_empty_short_and_default_sets()
    {
        Assert.False(GridSession.VisualParamsHealthy(null));
        Assert.False(GridSession.VisualParamsHealthy(System.Array.Empty<byte>()));
        Assert.False(GridSession.VisualParamsHealthy(new byte[GridSession.MinHealthyVisualParams - 1]));

        var allZero = new byte[218];
        Assert.False(GridSession.VisualParamsHealthy(allZero));

        var allMid = new byte[218];
        System.Array.Fill(allMid, (byte)128);
        Assert.False(GridSession.VisualParamsHealthy(allMid));
    }

    [Fact]
    public void VisualParamsHealthy_accepts_a_full_varied_set()
    {
        var real = new byte[218];
        for (int i = 0; i < real.Length; i++) real[i] = (byte)(i % 7 + 1); // 1..7, never 0 or 128
        Assert.True(GridSession.VisualParamsHealthy(real));

        // A realistic mix (some sliders genuinely at 0/128, most not) is still healthy.
        var mixed = new byte[218];
        for (int i = 0; i < mixed.Length; i++) mixed[i] = (byte)(i % 3 == 0 ? 0 : 40 + i % 90);
        Assert.True(GridSession.VisualParamsHealthy(mixed));
    }

    // TrySeedVisualParams copies the sim's self-appearance shape into LMV when it holds nothing
    // better -- the only place MyVisualParameters gets a truthful value while the appearance
    // workflow is disabled. It must seed once from a healthy relay and then leave it alone.
    [Fact]
    public void OnAvatarAppearance_seeds_MyVisualParameters_from_a_healthy_self_relay()
    {
        using var session = new GridSession();
        var client = (GridClient)typeof(GridSession)
            .GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(session)!;

        Assert.Empty(client.Appearance.MyVisualParameters); // starts empty (workflow off)

        var relay = new byte[218];
        for (int i = 0; i < relay.Length; i++) relay[i] = (byte)(i % 5 + 3);

        var sim = new Simulator(client, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 9000), 1234UL);

        AvatarAppearanceEventArgs Relay(byte[] vp) => new(
            sim, client.Self.AgentID, false,
            new Primitive.TextureEntryFace(null),
            System.Array.Empty<Primitive.TextureEntryFace>(),
            new System.Collections.Generic.List<byte>(vp),
            0, 0, (AppearanceFlags)0, 0);

        var method = typeof(GridSession).GetMethod("OnAvatarAppearance", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(session, new object?[] { null, Relay(relay) });

        Assert.Equal(relay, client.Appearance.MyVisualParameters);

        // A later (shorter) relay must not clobber the seeded set.
        method.Invoke(session, new object?[] { null, Relay(new byte[10]) });
        Assert.Equal(relay, client.Appearance.MyVisualParameters);
    }
}
