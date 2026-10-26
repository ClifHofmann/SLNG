using LibreMetaverse;
using SLNG.Core.Login;

namespace SLNG.Net;

/// <summary>FEAT-UI-72: the outcome of resolving a landmark to a place -- the place, or the reason there is none.</summary>
/// <param name="Target">Where the landmark points, or null.</param>
/// <param name="Reason">Why <paramref name="Target"/> is null (a short phrase for one log line); empty on success.</param>
public sealed record LandmarkTargetLookup(StartTarget? Target, string Reason);

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

    /// <summary>As <see cref="ResolveLandmarkTargetAsync"/>, but says why when there is no target.</summary>
    public async Task<LandmarkTargetLookup> LookupLandmarkTargetAsync(Guid landmarkAssetId, CancellationToken ct = default)
    {
        static LandmarkTargetLookup Fail(string reason) => new(null, reason);

        if (landmarkAssetId == Guid.Empty) return Fail("the landmark has no asset id yet");
        if (!_client.Network.Connected) return Fail("not connected");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(LandmarkTargetTimeout);
        try
        {
            var asset = await _client.Assets
                .RequestAssetAsync(new UUID(landmarkAssetId), AssetType.Landmark, priority: false, timeout.Token)
                .ConfigureAwait(false);
            if (asset is not LibreMetaverse.Assets.AssetLandmark landmark)
                return Fail("the landmark asset could not be fetched");
            if (!landmark.Decode() || landmark.RegionID == UUID.Zero)
                return Fail("the landmark asset names no region");

            ulong? handle = await ResolveRegionHandleAsync(landmark.RegionID, timeout.Token).ConfigureAwait(false);
            if (!handle.HasValue) return Fail("the region's handle is unknown");

            var region = await ResolveRegionByHandleAsync(handle.Value, timeout.Token).ConfigureAwait(false);
            if (region == null || string.IsNullOrWhiteSpace(region.Name)) return Fail("the region's name is unknown");

            var pos = landmark.Position;
            // 4096: a landmark's own position is real, and a var-region on OpenSim can be larger than 256 m.
            if (!StartTarget.TryCreate(region.Name, pos.X, pos.Y, pos.Z, StartTarget.OpenMaxXY, out var target))
                return Fail($"'{region.Name}' at {pos.X:0.#}, {pos.Y:0.#}, {pos.Z:0.#} cannot be used as a login start");

            return new LandmarkTargetLookup(target, "");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail($"no answer within {LandmarkTargetTimeout.TotalSeconds:0} s");
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
