using System;
using System.Collections.Generic;

namespace SLNG.Core;

/// <summary>
/// FEAT-PERF-25: which of the expensive render buffers to cap when the card is small.
///
/// <para>Measured 2026-10-10 at Sirens (Agni) with the budget limited to 3.5 GB: ~2.8 GB of the
/// process's video memory was NOT the texture cache -- render targets at window size with MSAA, the
/// SSIL/SSAO buffers, the reflection atlas, the planar mirror's second scene render, the shadow atlas.
/// <see cref="VramBudgetPolicy"/> gives the texture cache what is left, and what was left was 552 MB, so
/// every texture was admitted at its cheapest level. On a 4 GB card the buffers have to give way first:
/// they cost the same at every distance, where a texture budget is what keeps near objects sharp.</para>
///
/// <para>Pure decisions, no engine types. The tier is chosen from the OS budget (the card's size as
/// Windows grants it, after <c>--vram-budget</c>), with hysteresis so a budget wobbling around a
/// threshold does not flip MSAA on and off: caps tighten at once and relax only once the budget is
/// clearly past the threshold again.</para>
/// </summary>
public static class LowVramPolicy
{
    /// <summary>Below this OS budget the "small card" caps apply (an 8 GB card reports ~7 GB, a 6 GB
    /// card ~5.3 GB).</summary>
    public const long SmallBudgetBytes = 6L << 30;

    /// <summary>Below this the stricter caps apply: a 4 GB card's OS budget is ~3.5-3.8 GB.</summary>
    public const long TinyBudgetBytes = 4608L << 20;

    /// <summary>A tier is left for a looser one only once the budget is this fraction past its
    /// threshold.</summary>
    public const double RelaxHysteresis = 0.05;

    /// <summary>Viewport.Msaa values: 0 off, 1 2x, 2 4x, 3 8x.</summary>
    public const int SmallMaxMsaa = 1;
    public const int TinyMaxMsaa = 0;

    /// <summary>Directional shadow atlas side on a small card (4096 at 16 bit is 32 MB, 2048 is 8 MB).</summary>
    public const int MaxShadowAtlas = 2048;

    /// <summary>Reflection atlas side on a small card. Not a free choice: Godot 4.7 resizes the atlas of
    /// a scenario only to 256 at run time (the size it forces for real-time probes, light_storage.cpp
    /// <c>reflection_probe_instance_begin_render</c>), so the cap is that or nothing.</summary>
    public const int CappedReflectionSize = 256;

    public const int TierNone = 0;
    public const int TierSmall = 1;
    public const int TierTiny = 2;

    /// <summary>The graphics options this policy may cap, as the user set them.</summary>
    public readonly record struct Request(
        int Msaa,
        bool Ssao,
        bool SsaoHalfSize,
        bool Ssil,
        int ShadowAtlasSize,
        int ReflectionSize,
        bool PlanarMirror,
        bool HeroProbe);

    /// <summary>The tier for an OS budget. <paramref name="previousTier"/> is the tier in force (-1 when
    /// none has been chosen yet). An unknown budget (&lt;= 0) keeps the current tier.</summary>
    public static int TierFor(long osBudgetBytes, int previousTier)
    {
        if (osBudgetBytes <= 0) return Math.Max(TierNone, previousTier);

        int tight = osBudgetBytes < TinyBudgetBytes ? TierTiny
                  : osBudgetBytes < SmallBudgetBytes ? TierSmall
                  : TierNone;
        if (previousTier < 0 || tight >= previousTier) return tight;

        // Relaxing: the same thresholds, moved up by the hysteresis.
        int loose = osBudgetBytes < (long)(TinyBudgetBytes * (1 + RelaxHysteresis)) ? TierTiny
                  : osBudgetBytes < (long)(SmallBudgetBytes * (1 + RelaxHysteresis)) ? TierSmall
                  : TierNone;
        return Math.Min(previousTier, loose);
    }

    /// <summary>The options in force for <paramref name="tier"/>. Every option that changes is described
    /// in <paramref name="changes"/> (when given) as "what: from -> to", for the log line.</summary>
    public static Request Cap(Request requested, int tier, List<string>? changes = null)
    {
        if (tier <= TierNone) return requested;

        int maxMsaa = tier >= TierTiny ? TinyMaxMsaa : SmallMaxMsaa;
        var capped = requested with
        {
            Msaa = Math.Min(requested.Msaa, maxMsaa),
            SsaoHalfSize = true,
            Ssil = false,
            ShadowAtlasSize = Math.Min(requested.ShadowAtlasSize, MaxShadowAtlas),
            ReflectionSize = Math.Min(requested.ReflectionSize, CappedReflectionSize),
            PlanarMirror = false,
            HeroProbe = false,
        };

        if (changes != null)
        {
            if (capped.Msaa != requested.Msaa) changes.Add($"MSAA {MsaaName(requested.Msaa)} -> {MsaaName(capped.Msaa)}");
            if (requested.Ssao && capped.SsaoHalfSize != requested.SsaoHalfSize) changes.Add("SSAO full -> half resolution");
            if (capped.Ssil != requested.Ssil) changes.Add("SSIL on -> off");
            if (capped.ShadowAtlasSize != requested.ShadowAtlasSize) changes.Add($"shadow atlas {requested.ShadowAtlasSize} -> {capped.ShadowAtlasSize}");
            if (capped.ReflectionSize != requested.ReflectionSize) changes.Add($"reflection atlas {requested.ReflectionSize} -> {capped.ReflectionSize}");
            if (capped.PlanarMirror != requested.PlanarMirror) changes.Add("planar mirror on -> off");
            if (capped.HeroProbe != requested.HeroProbe) changes.Add("mirror probe on -> off");
        }
        return capped;
    }

    public static string TierName(int tier) => tier switch
    {
        TierTiny => "tiny",
        TierSmall => "small",
        _ => "none",
    };

    public static string MsaaName(int msaa) => msaa switch
    {
        <= 0 => "off",
        1 => "2x",
        2 => "4x",
        _ => "8x",
    };
}
