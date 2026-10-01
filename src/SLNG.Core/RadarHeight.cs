namespace SLNG.Core;

/// <summary>Which <see cref="HeightMarker"/> an avatar gets on the radar (FEAT-UI-39).</summary>
public static class RadarHeight
{
    /// <summary>Further than this above or below the local avatar, a dot becomes a triangle. The
    /// viewer's own value (llworldmapview.cpp, the "above / below" arrows).</summary>
    public const float MarkerThresholdMetres = 7f;

    /// <summary>The marker for an avatar <paramref name="relativeZ"/> metres above (positive) the local
    /// avatar. Exactly the threshold is still level, as in the viewer, which compares with a strict
    /// greater-than. An unknown height says so instead of drawing a number that is not a height.</summary>
    public static HeightMarker MarkerFor(float relativeZ, bool heightKnown)
    {
        if (!heightKnown) return HeightMarker.Unknown;
        if (relativeZ > MarkerThresholdMetres) return HeightMarker.Above;
        if (relativeZ < -MarkerThresholdMetres) return HeightMarker.Below;
        return HeightMarker.Level;
    }
}
