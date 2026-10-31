using Godot;
using System;
using SLNG.Core;

namespace SLNG.App;

public partial class AvatarRenderer
{
    // FEAT-PERF-28: feeds SLNG.Core.ArrivalTimeline once a second under --diag. Measurement only -- nothing here changes what is
    // loaded, drawn or queued. See ADR 0005.

    private const double ArrivalSampleIntervalSeconds = 1.0;
    private double _arrivalSampleAccum;

    private void TickArrival(double delta)
    {
        if (!Diag.Verbose || !ArrivalTimeline.Shared.Active) return;
        _arrivalSampleAccum += delta;
        if (_arrivalSampleAccum < ArrivalSampleIntervalSeconds) return;
        _arrivalSampleAccum = 0;
        ArrivalTimeline.Shared.Update(TakeArrivalSample(), Diag.NowSeconds);
    }

    /// <summary>One look at the shown avatars and the texture pipeline. A loop over the visuals, once a second.</summary>
    private ArrivalSample TakeArrivalSample()
    {
        int full = 0, fullAll = 0, fullNone = 0, reduced = 0, reducedNone = 0;
        foreach (var visual in _visuals.Values)
        {
            if (visual.IsControlAvatar || !visual.Shown) continue;
            bool none = visual.BakesApplied.Count == 0;
            if (visual.IsReduced)
            {
                reduced++;
                if (none) reducedNone++;
                continue;
            }
            full++;
            if (none) fullNone++;
            else if (HasEveryWantedBake(visual)) fullAll++;
        }

        return new ArrivalSample(full, fullAll, fullNone, reduced, reducedNone,
            _gpuCache?.FacesWaiting ?? 0,
            _assetService?.InflightTextureCount ?? 0,
            SLNG.Net.GridSession.TextureBytesFetched);
    }

    /// <summary>True when the avatar has at least one bake wanted and each wanted bake is applied. An avatar whose appearance has not
    /// arrived wants nothing yet and is not complete.</summary>
    private static bool HasEveryWantedBake(AvatarVisual visual)
    {
        if (visual.LoadedTextures.Count == 0) return false;
        foreach (var (index, wanted) in visual.LoadedTextures)
        {
            if (!visual.BakesApplied.TryGetValue(index, out var applied) || applied != wanted) return false;
        }
        return true;
    }
}
