using System;

namespace SLNG.Core;

public static class VramBudgetPolicy
{
    public const long MinBudgetBytes = 256L << 20;
    public const long MinHeadroomBytes = 256L << 20;
    public const double HeadroomFraction = 0.10;
    public const double DropThreshold = 0.97;     // a fall of more than 3 % applies at once
    public const double RiseThreshold = 1.03;     // a rise of more than 3 % is considered...
    public const double MaxRiseFactor = 1.10;     // ...but grows by at most 10 % per step
    public const double RiseIntervalSeconds = 10; // and at most once every 10 s

    /// <summary>
    /// Computes the GPU texture cache budget based on OS video memory budget and usage.
    /// <list type="bullet">
    /// <item><paramref name="osBudget"/>: what Windows grants this process (bytes), already capped by --vram-budget.</item>
    /// <item><paramref name="osUsage"/>: what this process currently uses on the card (bytes).</item>
    /// <item><paramref name="cacheBytes"/>: current cache size in bytes.</item>
    /// <item><paramref name="manualCapBytes"/>: manual budget cap in bytes, or null in Auto mode.</item>
    /// </list>
    /// </summary>
    /// <returns>The new GpuCache budget in bytes.</returns>
    public static long Compute(
        long osBudget,
        long osUsage,
        long cacheBytes,
        long? manualCapBytes,
        long previousBudget,
        double secondsSinceLastRise,
        out bool rose)
    {
        rose = false;

        // 1. nonCache = max(0, osUsage - cacheBytes).
        long nonCache = Math.Max(0L, osUsage - cacheBytes);

        // 2. headroom = max(MinHeadroomBytes, osBudget * HeadroomFraction).
        long headroom = Math.Max(MinHeadroomBytes, (long)(osBudget * HeadroomFraction));

        // 3. target = max(MinBudgetBytes, osBudget - nonCache - headroom).
        long target = Math.Max(MinBudgetBytes, osBudget - nonCache - headroom);

        // 4. If manualCapBytes has a value: target = min(target, manualCapBytes). In Manual mode the OS budget is still a hard safety limit.
        if (manualCapBytes.HasValue)
        {
            target = Math.Min(target, manualCapBytes.Value);
            target = Math.Max(MinBudgetBytes, target);
        }

        // 5. Smoothing against previousBudget. On the very first call pass previousBudget = 0 and return target directly.
        if (previousBudget <= 0)
        {
            return target;
        }

        // a fall of more than 3 % applies at once
        if (target < previousBudget * DropThreshold)
        {
            return target;
        }

        // a rise of more than 3 % is considered, but grows by at most 10 % per step and at most once every 10 s
        if (target > previousBudget * RiseThreshold && secondsSinceLastRise >= RiseIntervalSeconds)
        {
            rose = true;
            return Math.Min(target, (long)(previousBudget * MaxRiseFactor));
        }

        // Otherwise return previousBudget (dead band, no change).
        return previousBudget;
    }
}
