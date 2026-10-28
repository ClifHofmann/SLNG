using LibreMetaverse;
using SLNG.Core.Login;

namespace SLNG.Net;

/// <summary>FEAT-UI-72: the outcome of resolving a landmark to a place -- the place, or the reason there is none.</summary>
/// <param name="Target">Where the landmark points, or null.</param>
/// <param name="Reason">Why <paramref name="Target"/> is null (a short phrase for one log line); empty on success.</param>
/// <param name="RegionOffline">True when the landmark itself was read and names a region, but the grid has no handle or map
/// entry for that region (the region is down, or does not exist any more). Never true on success.</param>
public sealed record LandmarkTargetLookup(StartTarget? Target, string Reason, bool RegionOffline = false);

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
}
