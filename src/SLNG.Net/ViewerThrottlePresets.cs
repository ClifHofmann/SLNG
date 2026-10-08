namespace SLNG.Net;

/// <summary>
/// FEAT-NET-05: how the reference viewer turns its "Maximum bandwidth" setting into the seven
/// per-category rates of an AgentThrottle packet (indra/newview/llviewerthrottle.cpp).
///
/// The setting (<c>ThrottleBandwidthKBPS</c>, default 3000, clamped 100-10000 by
/// <c>getMaxBandwidthKbps</c>) is first multiplied by 1.5: <c>resetDynamicThrottle</c> starts the
/// dynamic throttle at <c>MAX_FRACTIONAL</c>, "under the assumption that the viewer won't receive all
/// the different message types at once". The result is clamped again to 50-6000 and looked up in a
/// four-row preset table -- interpolated between rows, extrapolated past the last two. The viewer only
/// lowers the fraction later when it measures packet loss; SLNG has no such feedback yet and always
/// sends the starting value.
///
/// SLNG left LibreMetaverse's default in place before this (1 536 000 bit/s, of which Task got
/// ≈ 360 kbit/s): about a third of the 1528 kbps of Task the viewer asks for by default.
/// </summary>
public static class ViewerThrottlePresets
{
    /// <summary>settings.xml <c>ThrottleBandwidthKBPS</c>.</summary>
    public const float DefaultBandwidthKbps = 3000f;

    /// <summary><c>getMaxBandwidthKbps</c>'s clamp on the setting.</summary>
    public const float MinBandwidthKbps = 100f;
    public const float MaxBandwidthKbps = 10000f;

    /// <summary><c>MAX_FRACTIONAL</c>: what <c>resetDynamicThrottle</c> multiplies the setting by.</summary>
    public const float Headroom = 1.5f;

    // The file-scope MIN_BANDWIDTH / MAX_BANDWIDTH getThrottleGroup clamps to -- not the same pair
    // as getMaxBandwidthKbps's (the viewer's own comment asks why).
    private const float MinGroupKbps = 50f;
    private const float MaxGroupKbps = 6000f;

    // BW_PRESET_50/300/500/1000, kbps, in bandwidth order. Each row's total is its name.
    //                                    Resend Land Wind Cloud Task Texture Asset
    private static readonly float[][] Presets =
    {
        new float[] { 5, 10, 3, 3, 10, 10, 9 },
        new float[] { 30, 40, 9, 9, 86, 86, 40 },
        new float[] { 50, 70, 14, 14, 136, 136, 80 },
        new float[] { 100, 100, 20, 20, 310, 310, 140 },
    };

    /// <summary>The setting as the viewer reads it: clamped to 100-10000 kbps; anything that is not
    /// a number is the default.</summary>
    public static float ClampBandwidth(float kbps)
        => float.IsFinite(kbps) ? Math.Clamp(kbps, MinBandwidthKbps, MaxBandwidthKbps) : DefaultBandwidthKbps;

    /// <summary>The rates the viewer sends at login for a "Maximum bandwidth" of
    /// <paramref name="settingKbps"/>.</summary>
    public static ThrottleRates ForMaxBandwidth(float settingKbps)
        => GetThrottleGroup(ClampBandwidth(settingKbps) * Headroom);

    /// <summary><c>LLViewerThrottle::getThrottleGroup</c>: the preset rows interpolated (or
    /// extrapolated) to <paramref name="bandwidthKbps"/>, returned in bits per second -- the viewer
    /// keeps kbps and multiplies by 1024 when it packs the message (<c>sendToSim</c>).</summary>
    public static ThrottleRates GetThrottleGroup(float bandwidthKbps)
    {
        float bandwidth = Math.Clamp(bandwidthKbps, MinGroupKbps, MaxGroupKbps);

        int count = Presets.Length;
        int i;
        for (i = 0; i < count; i++)
        {
            if (Total(Presets[i]) > bandwidth) break;
        }

        float[] kbps;
        if (i == 0)
            kbps = Presets[0];
        else if (i == count)
            kbps = Step(Presets[count - 1], Presets[count - 2], Presets[count - 1], bandwidth); // past the last row
        else
            kbps = Step(Presets[i - 1], Presets[i - 1], Presets[i], bandwidth);

        return new ThrottleRates(
            kbps[0] * 1024f, kbps[1] * 1024f, kbps[2] * 1024f, kbps[3] * 1024f,
            kbps[4] * 1024f, kbps[5] * 1024f, kbps[6] * 1024f);
    }

    /// <summary><c>from + (upper - lower) * ((bandwidth - from.total) / (upper - lower).total)</c>,
    /// the one formula both of getThrottleGroup's non-trivial branches use.</summary>
    private static float[] Step(float[] from, float[] lower, float[] upper, float bandwidth)
    {
        var delta = new float[from.Length];
        for (int c = 0; c < delta.Length; c++) delta[c] = upper[c] - lower[c];
        float fraction = (bandwidth - Total(from)) / Total(delta);

        var result = new float[from.Length];
        for (int c = 0; c < result.Length; c++) result[c] = from[c] + delta[c] * fraction;
        return result;
    }

    private static float Total(float[] row)
    {
        float total = 0f;
        foreach (float v in row) total += v;
        return total;
    }
}
