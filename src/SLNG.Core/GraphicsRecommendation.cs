namespace SLNG.Core;

/// <summary>The graphics preset a machine is recommended on its first start. Mirrors the app's
/// <c>GraphicsPreset</c> without Custom; the app maps one to the other.</summary>
public enum RecommendedGraphics
{
    Low,
    Medium,
    High,
    Ultra,
}

/// <summary>
/// FEAT-PERF-26: which graphics preset a new install starts on, from what the machine reports. The reference
/// viewer does the same on its first start (<c>gpu_table.txt</c> class, video memory, RAM, CPU); there is no
/// GPU table here, so the dedicated video memory stands in for the class. A card with 16 GB or more starts on
/// Ultra, 8 GB on High, 6 GB on Medium, anything smaller or an integrated GPU on Low. A weak CPU or little RAM
/// caps it. Unknown values never lower the result below Medium and never raise it.
/// </summary>
public static class GraphicsRecommendation
{
    /// <summary>Dedicated video memory in MB at which each preset starts. A "16 GB" card reports 16,376 MB, an
    /// "8 GB" one 8,188 MB and a "6 GB" one 6,144 MB, so the lines sit a little below the round figure.</summary>
    public const long UltraVramMb = 15_000;
    public const long HighVramMb = 7_500;
    public const long MediumVramMb = 5_500;

    /// <summary>Below these the preset is capped: fewer than 4 logical cores or 8 GB of RAM at Medium, fewer than
    /// 6 cores or 16 GB at High.</summary>
    public const int MediumCapCores = 4;
    public const long MediumCapRamMb = 8_000;
    public const int HighCapCores = 6;
    public const long HighCapRamMb = 15_000;

    /// <summary>The preset for a machine. <paramref name="dedicatedVramMb"/> 0 and <paramref name="logicalCores"/>
    /// or <paramref name="ramMb"/> at or below 0 mean "could not be read".</summary>
    public static RecommendedGraphics Recommend(long dedicatedVramMb, bool integratedGpu, int logicalCores, long ramMb)
    {
        RecommendedGraphics tier;
        if (integratedGpu) tier = RecommendedGraphics.Low;
        else if (dedicatedVramMb <= 0) tier = RecommendedGraphics.Medium;
        else if (dedicatedVramMb >= UltraVramMb) tier = RecommendedGraphics.Ultra;
        else if (dedicatedVramMb >= HighVramMb) tier = RecommendedGraphics.High;
        else if (dedicatedVramMb >= MediumVramMb) tier = RecommendedGraphics.Medium;
        else tier = RecommendedGraphics.Low;

        if (Weak(logicalCores, MediumCapCores) || Weak(ramMb, MediumCapRamMb)) tier = Cap(tier, RecommendedGraphics.Medium);
        if (Weak(logicalCores, HighCapCores) || Weak(ramMb, HighCapRamMb)) tier = Cap(tier, RecommendedGraphics.High);
        return tier;
    }

    private static bool Weak(long value, long limit) => value > 0 && value < limit;

    private static RecommendedGraphics Cap(RecommendedGraphics tier, RecommendedGraphics ceiling) =>
        tier > ceiling ? ceiling : tier;
}
