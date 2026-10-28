using LibreMetaverse;
using SLNG.Core.Login;

namespace SLNG.Net;

/// <summary>FEAT-UI-72: the outcome of resolving a landmark to a place -- the place, or the reason there is none.</summary>
/// <param name="Target">Where the landmark points, or null.</param>
/// <param name="Reason">Why <paramref name="Target"/> is null (a short phrase for one log line); empty on success.</param>
/// <param name="RegionOffline">True when the landmark itself was read and names a region, but the grid has no handle or map
/// entry for that region (the region is down, or does not exist any more). Never true on success.</param>
public sealed record LandmarkTargetLookup(StartTarget? Target, string Reason, bool RegionOffline = false);

/// <summary>FEAT-UI-77: what the grid says about a region that is asked for by name.</summary>
public enum RegionProbeResult
{
    /// <summary>The grid answered with a map entry that is not marked down.</summary>
    Up,
    /// <summary>The grid marked it down, or did not answer twice in a row.</summary>
    Down,
    /// <summary>Not asked: no connection, or no name.</summary>
    Unknown,
}

// GridSession, landmark-target part (FEAT-UI-72).
public sealed partial class GridSession
{
    /// <summary>How long one landmark may take to resolve (asset transfer + region handle + map block).</summary>
    private static readonly TimeSpan LandmarkTargetTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Resolves a landmark asset to the region it points at and the region-local position, for the login screen's
    /// Favorites list. READ-ONLY and never teleports: it takes the same steps the landmark-teleport fallback takes
    /// (<see cref="TeleportToLandmarkAsync"/>) -- landmark asset, then its region id, then the region's handle, then
    /// the region's canonical name -- and does not touch the teleport guard, so it can run while the person plays.
    /// Null on any failure (not connected, asset unavailable, a region that does not answer, a name the login form
    /// cannot express), within <see cref="LandmarkTargetTimeout"/>. Use <see cref="LookupLandmarkTargetAsync"/> to
    /// learn why.
    /// </summary>
    public async Task<StartTarget?> ResolveLandmarkTargetAsync(Guid landmarkAssetId, CancellationToken ct = default) =>
        (await LookupLandmarkTargetAsync(landmarkAssetId, ct).ConfigureAwait(false)).Target;

    // A few at a time: a favorites folder can hold dozens of landmarks.
    private readonly System.Threading.SemaphoreSlim _landmarkFetchSemaphore = new(4);

    /// <summary>As <see cref="ResolveLandmarkTargetAsync"/>, but says why when there is no target.</summary>
    public async Task<LandmarkTargetLookup> LookupLandmarkTargetAsync(Guid landmarkAssetId, CancellationToken ct = default)
    {
        static LandmarkTargetLookup Fail(string reason) => new(null, reason);

        if (landmarkAssetId == Guid.Empty) return Fail("the landmark has no asset id yet");
        if (!_client.Network.Connected) return Fail("not connected");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(LandmarkTargetTimeout);
        string step = "fetching the landmark";
        try
        {
            // The way the reference viewer asks for every asset type: the region's ViewerAsset capability, {cap}/?landmark_id=
            // (LLViewerAssetStorage::getAssetURL). The legacy transfer below went unanswered for the full 15 s on a connection
            // whose UDP asset traffic does not come back, so a favorite added to the bar never reached the login list.
            LibreMetaverse.Assets.AssetLandmark? landmark = null;
            var (bytes, _) = await FetchAssetViaViewerAssetAsync(landmarkAssetId, "landmark", _landmarkFetchSemaphore, timeout.Token)
                .ConfigureAwait(false);
            if (bytes is { Length: > 0 })
                landmark = new LibreMetaverse.Assets.AssetLandmark(new UUID(landmarkAssetId), bytes);

            if (landmark == null)
            {
                var asset = await _client.Assets
                    .RequestAssetAsync(new UUID(landmarkAssetId), AssetType.Landmark, priority: false, timeout.Token)
                    .ConfigureAwait(false);
                landmark = asset as LibreMetaverse.Assets.AssetLandmark;
            }
            if (landmark == null)
                return Fail("the landmark asset could not be fetched");
            if (!landmark.Decode() || landmark.RegionID == UUID.Zero)
                return Fail("the landmark asset names no region");

            // From here on the landmark is known to be good, so a region the grid cannot place is a region that is not there:
            // a down region answers neither the handle request nor the map block (the reference viewer draws it as "down").
            step = "resolving the region";
            ulong? handle = await ResolveRegionHandleAsync(landmark.RegionID, timeout.Token).ConfigureAwait(false);
            if (!handle.HasValue) return new LandmarkTargetLookup(null, "the region did not answer (down?): its handle is unknown", RegionOffline: true);

            var region = await ResolveRegionByHandleAsync(handle.Value, timeout.Token).ConfigureAwait(false);
            if (region == null || string.IsNullOrWhiteSpace(region.Name))
                return new LandmarkTargetLookup(null, "the region did not answer (down?): its name is unknown", RegionOffline: true);

            var pos = landmark.Position;
            // 4096: a landmark's own position is real, and a var-region on OpenSim can be larger than 256 m.
            if (!StartTarget.TryCreate(region.Name, pos.X, pos.Y, pos.Z, StartTarget.OpenMaxXY, out var target))
                return Fail($"'{region.Name}' at {pos.X:0.#}, {pos.Y:0.#}, {pos.Z:0.#} cannot be used as a login start");

            return new LandmarkTargetLookup(target, "");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Only the time out of the step is certain; which region is down is not, so this is not flagged offline.
            return Fail($"no answer within {LandmarkTargetTimeout.TotalSeconds:0} s while {step}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }
    /// <summary>How long one asking of the map for a region may take before it counts as no answer.</summary>
    private static readonly TimeSpan RegionProbeTimeout = TimeSpan.FromSeconds(6);

    /// <summary>
    /// FEAT-UI-77: is the region <paramref name="regionName"/> up? Asks the grid's map by name and reads the access byte of the
    /// answer (the reference viewer draws <c>SIM_ACCESS_DOWN</c> as a down region). Always asks the grid -- never LibreMetaverse's
    /// region cache, which would repeat a stale answer forever. A missing answer is tried once more before the region counts as
    /// down, so one lost packet does not mark a region that is fine. READ-ONLY; takes no part in a teleport.
    /// </summary>
    public async Task<RegionProbeResult> ProbeRegionAsync(string regionName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(regionName) || !_client.Network.Connected) return RegionProbeResult.Unknown;

        var current = _client.Network.CurrentSim;
        if (current != null && string.Equals(current.Name, regionName, StringComparison.OrdinalIgnoreCase))
            return RegionProbeResult.Up;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            var answer = new TaskCompletionSource<SimAccess>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnRegion(object? sender, GridRegionEventArgs e)
            {
                if (string.Equals(e.Region.Name, regionName, StringComparison.OrdinalIgnoreCase))
                    answer.TrySetResult(e.Region.Access);
            }

            _client.Grid.GridRegion += OnRegion;
            try
            {
                _client.Grid.RequestMapRegion(regionName, GridLayerType.Objects);

                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(RegionProbeTimeout);
                var done = await Task.WhenAny(answer.Task, Task.Delay(Timeout.InfiniteTimeSpan, wait.Token)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                if (done == answer.Task)
                {
                    var access = answer.Task.Result;
                    return access == SimAccess.Down || access == SimAccess.NonExistent ? RegionProbeResult.Down : RegionProbeResult.Up;
                }
            }
            finally
            {
                _client.Grid.GridRegion -= OnRegion;
            }
        }
        return RegionProbeResult.Down;
    }
}
