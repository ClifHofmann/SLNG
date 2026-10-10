using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

public class VramBudgetPolicyTests
{
    private const long Mb = 1L << 20;
    private const long Gb = 1L << 30;

    [Fact]
    public void First_call_returns_the_target()
    {
        long osBudget = 4000L * Mb;
        long osUsage = 500L * Mb;
        long cacheBytes = 0;
        // nonCache = 500 MB
        // headroom = max(256 MB, 4000 * 0.10 = 400 MB) = 400 MB
        // target = 4000 - 500 - 400 = 3100 MB
        long result = VramBudgetPolicy.Compute(
            osBudget, osUsage, cacheBytes,
            manualCapBytes: null,
            previousBudget: 0,
            secondsSinceLastRise: 0,
            out bool rose);

        Assert.Equal(3100L * Mb, result);
        Assert.False(rose);
    }

    [Fact]
    public void Card_12GB_little_other_usage_returns_a_large_budget()
    {
        long osBudget = 12L * Gb;
        long osUsage = 500L * Mb;
        long cacheBytes = 200L * Mb;
        // nonCache = 300 MB
        // headroom = 12 GB * 0.10 = 1.2 GB
        // target = 12 GB - 300 MB - 1.2 GB = 10.5 GB
        long result = VramBudgetPolicy.Compute(
            osBudget, osUsage, cacheBytes,
            manualCapBytes: null,
            previousBudget: 0,
            secondsSinceLastRise: 0,
            out bool rose);

        Assert.True(result >= 10L * Gb);
        Assert.False(rose);
    }

    [Fact]
    public void Simulated_3_5GB_budget_with_1_2GB_non_cache_usage_yields_about_1_95GB()
    {
        long osBudget = 3500L * Mb;
        long cacheBytes = 500L * Mb;
        long osUsage = 1200L * Mb + cacheBytes; // nonCache = 1200 MB (~1.2 GB)

        // headroom = max(256 MB, 3500 * 0.10 = 350 MB) = 350 MB
        // target = 3500 - 1200 - 350 = 1950 MB (1.95 GB)
        long result = VramBudgetPolicy.Compute(
            osBudget, osUsage, cacheBytes,
            manualCapBytes: null,
            previousBudget: 0,
            secondsSinceLastRise: 0,
            out bool rose);

        Assert.Equal(1950L * Mb, result);
        Assert.False(rose);
    }

    [Fact]
    public void Usage_above_budget_shrinks_immediately()
    {
        long previousBudget = 2000L * Mb;
        long osBudget = 3000L * Mb;
        long cacheBytes = 1500L * Mb;
        long osUsage = 3500L * Mb; // nonCache = 2000 MB

        // headroom = max(256 MB, 3000 * 0.10 = 300 MB) = 300 MB
        // target = 3000 - 2000 - 300 = 700 MB
        // 700 MB < 2000 MB * 0.97 (1940 MB) -> shrinks immediately
        long result = VramBudgetPolicy.Compute(
            osBudget, osUsage, cacheBytes,
            manualCapBytes: null,
            previousBudget,
            secondsSinceLastRise: 1.0,
            out bool rose);

        Assert.Equal(700L * Mb, result);
        Assert.False(rose);
    }

    [Fact]
    public void Small_changes_stay_in_the_dead_band()
    {
        long previousBudget = 1000L * Mb;

        // Small rise: +1% (within 1.03 dead band)
        // target = 1010 MB
        // osBudget = 1010 + nonCache (0) + headroom (256) = 1266 MB
        long osBudget1 = 1266L * Mb;
        long result1 = VramBudgetPolicy.Compute(
            osBudget1, osUsage: 0, cacheBytes: 0,
            manualCapBytes: null,
            previousBudget,
            secondsSinceLastRise: 20.0,
            out bool rose1);

        Assert.Equal(previousBudget, result1);
        Assert.False(rose1);

        // Small drop: -2% (within 0.97 dead band)
        // target = 980 MB
        long osBudget2 = (980L + 256L) * Mb;
        long result2 = VramBudgetPolicy.Compute(
            osBudget2, osUsage: 0, cacheBytes: 0,
            manualCapBytes: null,
            previousBudget,
            secondsSinceLastRise: 20.0,
            out bool rose2);

        Assert.Equal(previousBudget, result2);
        Assert.False(rose2);
    }

    [Fact]
    public void Rises_are_capped_at_10_percent_and_spaced_by_interval()
    {
        long previousBudget = 1000L * Mb;
        // Large target: 1500 MB (+50%)
        long osBudget = (1500L + 256L) * Mb;

        // Before interval (5s < 10s) -> no rise, stays in dead band
        long resultWait = VramBudgetPolicy.Compute(
            osBudget, osUsage: 0, cacheBytes: 0,
            manualCapBytes: null,
            previousBudget,
            secondsSinceLastRise: 5.0,
            out bool roseWait);

        Assert.Equal(previousBudget, resultWait);
        Assert.False(roseWait);

        // After interval (10s >= 10s) -> rises by at most +10% (1100 MB)
        long resultRise = VramBudgetPolicy.Compute(
            osBudget, osUsage: 0, cacheBytes: 0,
            manualCapBytes: null,
            previousBudget,
            secondsSinceLastRise: 10.0,
            out bool roseRise);

        Assert.Equal(1100L * Mb, resultRise);
        Assert.True(roseRise);

        // Moderate rise (+5%, within 10% cap) -> rises to target
        long osBudgetMod = (1050L + 256L) * Mb;
        long resultMod = VramBudgetPolicy.Compute(
            osBudgetMod, osUsage: 0, cacheBytes: 0,
            manualCapBytes: null,
            previousBudget,
            secondsSinceLastRise: 15.0,
            out bool roseMod);

        Assert.Equal(1050L * Mb, resultMod);
        Assert.True(roseMod);
    }

    [Fact]
    public void Manual_cap_clamps_below_target_and_preserves_target_when_above()
    {
        // Raw target without cap = 2000 MB
        long osBudget = (2000L + 256L) * Mb;

        // Cap below target (1500 MB) -> returns 1500 MB
        long resultBelow = VramBudgetPolicy.Compute(
            osBudget, osUsage: 0, cacheBytes: 0,
            manualCapBytes: 1500L * Mb,
            previousBudget: 0,
            secondsSinceLastRise: 0,
            out bool roseBelow);

        Assert.Equal(1500L * Mb, resultBelow);
        Assert.False(roseBelow);

        // Cap above target (2500 MB) -> returns raw target (2000 MB)
        long resultAbove = VramBudgetPolicy.Compute(
            osBudget, osUsage: 0, cacheBytes: 0,
            manualCapBytes: 2500L * Mb,
            previousBudget: 0,
            secondsSinceLastRise: 0,
            out bool roseAbove);

        Assert.Equal(2000L * Mb, resultAbove);
        Assert.False(roseAbove);
    }

    [Fact]
    public void Floor_never_goes_below_256MB()
    {
        // Even when osBudget is very low or negative after nonCache/headroom
        long osBudget = 100L * Mb;
        long osUsage = 500L * Mb;
        long cacheBytes = 0;

        long result = VramBudgetPolicy.Compute(
            osBudget, osUsage, cacheBytes,
            manualCapBytes: null,
            previousBudget: 0,
            secondsSinceLastRise: 0,
            out bool rose);

        Assert.Equal(VramBudgetPolicy.MinBudgetBytes, result);
        Assert.Equal(256L * Mb, result);
        Assert.False(rose);

        // Even with manual cap below 256 MB
        long resultCap = VramBudgetPolicy.Compute(
            osBudget, osUsage, cacheBytes,
            manualCapBytes: 128L * Mb,
            previousBudget: 0,
            secondsSinceLastRise: 0,
            out bool roseCap);

        Assert.Equal(VramBudgetPolicy.MinBudgetBytes, resultCap);
        Assert.False(roseCap);
    }
}
