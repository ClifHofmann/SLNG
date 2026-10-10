using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// FEAT-PERF-25: on a small card the render buffers give way before the texture cache does.
/// These tests pin which tier a budget lands in (with hysteresis, so a wobbling budget does not flip
/// MSAA on and off) and exactly which graphics options each tier caps.
/// </summary>
public class LowVramPolicyTests
{
    private const long Mb = 1L << 20;
    private const long Gb = 1L << 30;

    // Everything the user could have turned up, so every cap has something to bite on.
    private static LowVramPolicy.Request Maxed() => new(
        Msaa: 2,
        Ssao: true,
        SsaoHalfSize: false,
        Ssil: true,
        ShadowAtlasSize: 4096,
        ReflectionSize: 1024,
        PlanarMirror: true,
        HeroProbe: true);

    // Everything already at or below the small-card caps, so there is nothing to change.
    private static LowVramPolicy.Request Frugal() => new(
        Msaa: 0,
        Ssao: true,
        SsaoHalfSize: true,
        Ssil: false,
        ShadowAtlasSize: 1024,
        ReflectionSize: 256,
        PlanarMirror: false,
        HeroProbe: false);

    // ---------------------------------------------------------------- TierFor

    [Fact]
    public void A_12GB_card_needs_no_caps()
        => Assert.Equal(LowVramPolicy.TierNone, LowVramPolicy.TierFor(12L * Gb, previousTier: -1));

    [Fact]
    public void A_budget_between_the_thresholds_is_the_small_tier()
        => Assert.Equal(LowVramPolicy.TierSmall, LowVramPolicy.TierFor(5L * Gb, previousTier: -1));

    [Fact]
    public void A_4GB_card_is_the_tiny_tier()
        => Assert.Equal(LowVramPolicy.TierTiny, LowVramPolicy.TierFor(3500L * Mb, previousTier: -1));

    [Theory]
    // Exactly on a threshold is NOT below it: the looser tier applies.
    [InlineData(4608L, LowVramPolicy.TierSmall)]
    [InlineData(4607L, LowVramPolicy.TierTiny)]
    [InlineData(6144L, LowVramPolicy.TierNone)]
    [InlineData(6143L, LowVramPolicy.TierSmall)]
    public void Thresholds_are_exclusive_upper_bounds(long budgetMb, int expected)
        => Assert.Equal(expected, LowVramPolicy.TierFor(budgetMb * Mb, previousTier: -1));

    [Fact]
    public void The_first_call_takes_the_tight_tier_with_no_hysteresis()
    {
        // 4700 MB is within the relax margin of the tiny threshold. With a tier already in force it
        // would stay tiny (see below); with none chosen yet there is nothing to be sticky about.
        Assert.Equal(LowVramPolicy.TierSmall, LowVramPolicy.TierFor(4700L * Mb, previousTier: -1));
        Assert.Equal(LowVramPolicy.TierNone, LowVramPolicy.TierFor((long)(6.2 * Gb), previousTier: -1));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void An_unknown_budget_keeps_the_previous_tier(long unknownBudget)
    {
        Assert.Equal(LowVramPolicy.TierTiny, LowVramPolicy.TierFor(unknownBudget, LowVramPolicy.TierTiny));
        Assert.Equal(LowVramPolicy.TierSmall, LowVramPolicy.TierFor(unknownBudget, LowVramPolicy.TierSmall));
        Assert.Equal(LowVramPolicy.TierNone, LowVramPolicy.TierFor(unknownBudget, LowVramPolicy.TierNone));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void An_unknown_budget_with_no_tier_chosen_yet_means_no_caps(long unknownBudget)
        => Assert.Equal(LowVramPolicy.TierNone, LowVramPolicy.TierFor(unknownBudget, previousTier: -1));

    // ------------------------------------------------------------- hysteresis

    [Fact]
    public void Tiny_stays_tiny_until_the_budget_is_clearly_past_the_threshold()
    {
        // 4608 MB * 1.05 = 4838.4 MB. 4700 MB is above the threshold but inside the margin...
        Assert.Equal(LowVramPolicy.TierTiny, LowVramPolicy.TierFor(4700L * Mb, LowVramPolicy.TierTiny));
        // ...4900 MB is clearly past it.
        Assert.Equal(LowVramPolicy.TierSmall, LowVramPolicy.TierFor(4900L * Mb, LowVramPolicy.TierTiny));
    }

    [Fact]
    public void Small_stays_small_until_the_budget_is_clearly_past_the_threshold()
    {
        // 6 GB * 1.05 = 6.3 GB.
        Assert.Equal(LowVramPolicy.TierSmall, LowVramPolicy.TierFor((long)(6.2 * Gb), LowVramPolicy.TierSmall));
        Assert.Equal(LowVramPolicy.TierNone, LowVramPolicy.TierFor((long)(6.5 * Gb), LowVramPolicy.TierSmall));
    }

    [Fact]
    public void Tightening_is_immediate()
    {
        Assert.Equal(LowVramPolicy.TierTiny, LowVramPolicy.TierFor(3500L * Mb, LowVramPolicy.TierNone));
        Assert.Equal(LowVramPolicy.TierSmall, LowVramPolicy.TierFor(5L * Gb, LowVramPolicy.TierNone));
        Assert.Equal(LowVramPolicy.TierTiny, LowVramPolicy.TierFor(3500L * Mb, LowVramPolicy.TierSmall));
    }

    [Fact]
    public void A_budget_that_jumps_far_up_relaxes_across_both_tiers_at_once()
        => Assert.Equal(LowVramPolicy.TierNone, LowVramPolicy.TierFor(12L * Gb, LowVramPolicy.TierTiny));

    [Fact]
    public void A_wobbling_budget_around_a_threshold_does_not_flip_the_tier_back_and_forth()
    {
        // A 4 GB card's budget drifting 4500 -> 4700 -> 4500 -> 4700 MB around the 4608 MB threshold.
        // Without hysteresis this would toggle small/tiny (MSAA on/off) on every sample.
        int tier = LowVramPolicy.TierFor(4500L * Mb, previousTier: -1);
        Assert.Equal(LowVramPolicy.TierTiny, tier);

        foreach (long budgetMb in new[] { 4700L, 4500L, 4700L, 4550L, 4750L })
        {
            tier = LowVramPolicy.TierFor(budgetMb * Mb, tier);
            Assert.Equal(LowVramPolicy.TierTiny, tier);
        }
    }

    // -------------------------------------------------------------------- Cap

    [Fact]
    public void Tier_none_returns_the_request_unchanged_and_records_no_changes()
    {
        var requested = Maxed();
        var changes = new List<string>();

        var result = LowVramPolicy.Cap(requested, LowVramPolicy.TierNone, changes);

        Assert.Equal(requested, result);
        Assert.Empty(changes);
    }

    [Fact]
    public void Small_tier_caps_every_expensive_buffer()
    {
        var changes = new List<string>();

        var result = LowVramPolicy.Cap(Maxed(), LowVramPolicy.TierSmall, changes);

        Assert.Equal(1, result.Msaa);                       // 4x -> 2x
        Assert.True(result.Ssao);                           // SSAO itself stays on...
        Assert.True(result.SsaoHalfSize);                   // ...at half resolution
        Assert.False(result.Ssil);
        Assert.Equal(2048, result.ShadowAtlasSize);
        Assert.Equal(256, result.ReflectionSize);
        Assert.False(result.PlanarMirror);
        Assert.False(result.HeroProbe);

        Assert.Equal(
            new[]
            {
                "MSAA 4x -> 2x",
                "SSAO full -> half resolution",
                "SSIL on -> off",
                "shadow atlas 4096 -> 2048",
                "reflection atlas 1024 -> 256",
                "planar mirror on -> off",
                "mirror probe on -> off",
            },
            changes);
    }

    [Fact]
    public void Tiny_tier_additionally_turns_msaa_off()
    {
        var changes = new List<string>();

        var result = LowVramPolicy.Cap(Maxed(), LowVramPolicy.TierTiny, changes);

        Assert.Equal(0, result.Msaa);
        Assert.True(result.SsaoHalfSize);
        Assert.False(result.Ssil);
        Assert.Equal(2048, result.ShadowAtlasSize);
        Assert.Equal(256, result.ReflectionSize);
        Assert.False(result.PlanarMirror);
        Assert.False(result.HeroProbe);
        Assert.Contains(changes, c => c.StartsWith("MSAA 4x -> off"));
    }

    [Theory]
    // MSAA values: 0 off, 1 2x, 2 4x, 3 8x.
    [InlineData(LowVramPolicy.TierSmall, 0, 0)]
    [InlineData(LowVramPolicy.TierSmall, 1, 1)]
    [InlineData(LowVramPolicy.TierSmall, 2, 1)]
    [InlineData(LowVramPolicy.TierSmall, 3, 1)]
    [InlineData(LowVramPolicy.TierTiny, 0, 0)]
    [InlineData(LowVramPolicy.TierTiny, 1, 0)]
    [InlineData(LowVramPolicy.TierTiny, 2, 0)]
    [InlineData(LowVramPolicy.TierTiny, 3, 0)]
    public void Msaa_is_capped_per_tier_and_never_raised(int tier, int requestedMsaa, int expectedMsaa)
    {
        var result = LowVramPolicy.Cap(Maxed() with { Msaa = requestedMsaa }, tier);

        Assert.Equal(expectedMsaa, result.Msaa);
    }

    [Fact]
    public void Msaa_that_is_already_off_is_not_reported_as_a_change()
    {
        foreach (int tier in new[] { LowVramPolicy.TierSmall, LowVramPolicy.TierTiny })
        {
            var changes = new List<string>();

            LowVramPolicy.Cap(Maxed() with { Msaa = 0 }, tier, changes);

            Assert.DoesNotContain(changes, c => c.StartsWith("MSAA"));
        }
    }

    [Fact]
    public void Msaa_within_the_small_cap_is_not_reported_as_a_change()
    {
        var changes = new List<string>();

        var result = LowVramPolicy.Cap(Maxed() with { Msaa = 1 }, LowVramPolicy.TierSmall, changes);

        Assert.Equal(1, result.Msaa);
        Assert.DoesNotContain(changes, c => c.StartsWith("MSAA"));
    }

    [Fact]
    public void Msaa_2x_is_named_in_the_change_when_tiny_turns_it_off()
    {
        var changes = new List<string>();

        LowVramPolicy.Cap(Maxed() with { Msaa = 1 }, LowVramPolicy.TierTiny, changes);

        Assert.Contains("MSAA 2x -> off", changes);
    }

    [Theory]
    [InlineData(8192, 2048, true)]
    [InlineData(4096, 2048, true)]
    [InlineData(2048, 2048, false)]
    // A smaller atlas is the user's choice and must not be raised to the cap.
    [InlineData(1024, 1024, false)]
    public void Shadow_atlas_is_capped_but_never_raised(int requested, int expected, bool reported)
    {
        var changes = new List<string>();

        var result = LowVramPolicy.Cap(Maxed() with { ShadowAtlasSize = requested }, LowVramPolicy.TierSmall, changes);

        Assert.Equal(expected, result.ShadowAtlasSize);
        Assert.Equal(reported, changes.Any(c => c.StartsWith("shadow atlas")));
    }

    [Theory]
    [InlineData(2048, 256, true)]
    [InlineData(1024, 256, true)]
    [InlineData(256, 256, false)]
    // A smaller reflection atlas is not raised to the cap.
    [InlineData(128, 128, false)]
    public void Reflection_atlas_is_capped_but_never_raised(int requested, int expected, bool reported)
    {
        var changes = new List<string>();

        var result = LowVramPolicy.Cap(Maxed() with { ReflectionSize = requested }, LowVramPolicy.TierSmall, changes);

        Assert.Equal(expected, result.ReflectionSize);
        Assert.Equal(reported, changes.Any(c => c.StartsWith("reflection atlas")));
    }

    [Fact]
    public void Ssao_half_resolution_is_reported_only_when_ssao_is_on_and_was_full_size()
    {
        // On, full size: forced to half and reported.
        var onFull = new List<string>();
        var forced = LowVramPolicy.Cap(Maxed() with { Ssao = true, SsaoHalfSize = false }, LowVramPolicy.TierSmall, onFull);
        Assert.True(forced.SsaoHalfSize);
        Assert.Contains("SSAO full -> half resolution", onFull);

        // On, already half: nothing to report.
        var onHalf = new List<string>();
        LowVramPolicy.Cap(Maxed() with { Ssao = true, SsaoHalfSize = true }, LowVramPolicy.TierSmall, onHalf);
        Assert.DoesNotContain(onHalf, c => c.StartsWith("SSAO"));

        // Off: there is no SSAO buffer to shrink, so there is nothing to say about it, and the cap does
        // not switch it on.
        var off = new List<string>();
        var offResult = LowVramPolicy.Cap(Maxed() with { Ssao = false, SsaoHalfSize = false }, LowVramPolicy.TierSmall, off);
        Assert.False(offResult.Ssao);
        Assert.DoesNotContain(off, c => c.StartsWith("SSAO"));
    }

    [Fact]
    public void Ssil_off_planar_mirror_off_and_probe_off_are_not_reported()
    {
        var changes = new List<string>();

        LowVramPolicy.Cap(Maxed() with { Ssil = false, PlanarMirror = false, HeroProbe = false },
            LowVramPolicy.TierSmall, changes);

        Assert.DoesNotContain(changes, c => c.StartsWith("SSIL"));
        Assert.DoesNotContain(changes, c => c.StartsWith("planar mirror"));
        Assert.DoesNotContain(changes, c => c.StartsWith("mirror probe"));
    }

    [Theory]
    [InlineData(LowVramPolicy.TierSmall)]
    [InlineData(LowVramPolicy.TierTiny)]
    public void A_request_already_within_the_caps_comes_back_unchanged_with_no_changes(int tier)
    {
        var requested = Frugal();
        var changes = new List<string>();

        var result = LowVramPolicy.Cap(requested, tier, changes);

        Assert.Equal(requested, result);
        Assert.Empty(changes);
    }

    [Theory]
    [InlineData(LowVramPolicy.TierSmall)]
    [InlineData(LowVramPolicy.TierTiny)]
    public void The_changes_list_is_optional_and_does_not_alter_the_result(int tier)
        => Assert.Equal(
            LowVramPolicy.Cap(Maxed(), tier, new List<string>()),
            LowVramPolicy.Cap(Maxed(), tier));

    [Fact]
    public void Changes_are_appended_to_the_callers_list()
    {
        var changes = new List<string> { "already there" };

        LowVramPolicy.Cap(Maxed(), LowVramPolicy.TierSmall, changes);

        Assert.Equal("already there", changes[0]);
        Assert.True(changes.Count > 1);
    }

    [Fact]
    public void Capping_is_idempotent()
    {
        foreach (int tier in new[] { LowVramPolicy.TierSmall, LowVramPolicy.TierTiny })
        {
            var once = LowVramPolicy.Cap(Maxed(), tier);
            var changes = new List<string>();

            var twice = LowVramPolicy.Cap(once, tier, changes);

            Assert.Equal(once, twice);
            Assert.Empty(changes);
        }
    }

    // ------------------------------------------------------------------ names

    [Theory]
    [InlineData(-1, "off")]
    [InlineData(0, "off")]
    [InlineData(1, "2x")]
    [InlineData(2, "4x")]
    [InlineData(3, "8x")]
    public void MsaaName_names_the_viewport_msaa_values(int msaa, string expected)
        => Assert.Equal(expected, LowVramPolicy.MsaaName(msaa));

    [Theory]
    [InlineData(LowVramPolicy.TierNone, "none")]
    [InlineData(LowVramPolicy.TierSmall, "small")]
    [InlineData(LowVramPolicy.TierTiny, "tiny")]
    // The "no tier chosen yet" marker reads as none rather than throwing.
    [InlineData(-1, "none")]
    public void TierName_names_the_tiers(int tier, string expected)
        => Assert.Equal(expected, LowVramPolicy.TierName(tier));
}
