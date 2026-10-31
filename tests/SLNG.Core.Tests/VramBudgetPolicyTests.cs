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

    // ---- BUG-PERF-18: Godot's own figure, the bounded slack, the proportional floor ----

    // In-world 2026-10-11, RTX 4070, Millenium -> Sirens -> Millenium: DXGI 8427 of 8698 MB, Godot's overlay
    // 2567 MB, the texture cache 356 MB.
    private const long TeleportBackBudget = 8698L * Mb;
    private const long TeleportBackOsUsage = 8427L * Mb;
    private const long TeleportBackGodot = 2567L * Mb;
    private const long TeleportBackCache = 356L * Mb;

    [Fact]
    public void Teleport_back_does_not_leave_the_budget_at_the_floor_when_Godot_holds_2_5GB_of_8_7GB()
    {
        long result = VramBudgetPolicy.Compute(
            TeleportBackBudget, TeleportBackOsUsage, TeleportBackGodot, TeleportBackCache,
            manualCapBytes: null, previousBudget: 0, secondsSinceLastRise: 0, out bool rose);

        // non-cache = Godot's 2211 MB + the slack allowance (15 % of 8698 MB); headroom = 10 %.
        long expected = TeleportBackBudget
            - (TeleportBackGodot - TeleportBackCache)
            - (long)(TeleportBackBudget * VramBudgetPolicy.SlackFraction)
            - (long)(TeleportBackBudget * VramBudgetPolicy.HeadroomFraction);
        Assert.Equal(expected, result);
        Assert.True(result > 4L * Gb, $"budget {result / Mb} MB should leave real room for the world");
        Assert.True(result > VramBudgetPolicy.MinBudgetBytes * 10);
        Assert.False(rose);
    }

    [Fact]
    public void Without_Godots_figure_the_same_numbers_fall_to_the_proportional_floor_not_256MB()
    {
        // The old behaviour (osUsage - cache = 8071 MB of "someone else's memory") bottoms out, but at the
        // floor derived from the OS budget, not at a size that blurs everything.
        long result = VramBudgetPolicy.Compute(
            TeleportBackBudget, TeleportBackOsUsage, TeleportBackCache,
            manualCapBytes: null, previousBudget: 0, secondsSinceLastRise: 0, out _);

        Assert.Equal((long)(TeleportBackBudget * VramBudgetPolicy.FloorFraction), result);
        Assert.True(result > 1L * Gb);
    }

    [Fact]
    public void Unknown_Godot_figure_behaves_like_the_old_overload()
    {
        long a = VramBudgetPolicy.Compute(
            4000L * Mb, 1700L * Mb, 0L, 500L * Mb, null, 0, 0, out _);
        long b = VramBudgetPolicy.Compute(
            4000L * Mb, 1700L * Mb, 500L * Mb, null, 0, 0, out _);
        Assert.Equal(b, a);
    }

    [Fact]
    public void Slack_within_the_allowance_counts_in_full_like_before()
    {
        // Login at Millenium, 60 s in: DXGI 6365 MB, Godot 5928 MB (437 MB of driver overhead), 3879 MB cache,
        // 11242 MB budget. The allowance (1686 MB) covers the slack, so nothing changes from the old maths.
        long withGodot = VramBudgetPolicy.Compute(
            11242L * Mb, 6365L * Mb, 5928L * Mb, 3879L * Mb, null, 0, 0, out _);
        long osOnly = VramBudgetPolicy.Compute(
            11242L * Mb, 6365L * Mb, 3879L * Mb, null, 0, 0, out _);
        Assert.Equal(osOnly, withGodot);
    }

    [Fact]
    public void Slack_beyond_the_allowance_counts_only_up_to_the_allowance_while_the_OS_is_content()
    {
        long osBudget = 8000L * Mb;
        long cache = 300L * Mb;
        long godot = 2000L * Mb;
        long osUsage = 7000L * Mb; // 5000 MB of slack, usage below the budget

        long result = VramBudgetPolicy.Compute(osBudget, osUsage, godot, cache, null, 0, 0, out _);

        long counted = (long)(osBudget * VramBudgetPolicy.SlackFraction);
        long expected = osBudget - (godot - cache) - counted - (long)(osBudget * VramBudgetPolicy.HeadroomFraction);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Usage_above_the_OS_budget_counts_all_the_slack()
    {
        // Windows is demoting the process: the pooled memory is not something to hope for any more.
        long osBudget = 8000L * Mb;
        long result = VramBudgetPolicy.Compute(osBudget, 8500L * Mb, 2000L * Mb, 300L * Mb, null, 0, 0, out _);

        // 1700 MB (Godot) + 6500 MB (slack) of non-cache leaves nothing: the floor.
        Assert.Equal((long)(osBudget * VramBudgetPolicy.FloorFraction), result);
    }

    [Fact]
    public void Godot_figure_above_the_OS_figure_never_lowers_the_budget_below_the_OS_only_reading()
    {
        long osOnly = VramBudgetPolicy.Compute(
            6000L * Mb, 3000L * Mb, 1000L * Mb, null, 0, 0, out _);
        long clamped = VramBudgetPolicy.Compute(
            6000L * Mb, 3000L * Mb, 4000L * Mb, 1000L * Mb, null, 0, 0, out _);
        Assert.Equal(osOnly, clamped);
    }

    [Fact]
    public void Floor_is_a_fraction_of_the_OS_budget_and_a_manual_cap_below_it_still_wins()
    {
        long osBudget = 8000L * Mb;
        long floor = (long)(osBudget * VramBudgetPolicy.FloorFraction);
        Assert.True(floor > VramBudgetPolicy.MinBudgetBytes);

        long auto = VramBudgetPolicy.Compute(osBudget, 7900L * Mb, 7900L * Mb, 0L, null, 0, 0, out _);
        Assert.Equal(floor, auto);

        long manual = VramBudgetPolicy.Compute(osBudget, 7900L * Mb, 7900L * Mb, 0L, 500L * Mb, 0, 0, out _);
        Assert.Equal(500L * Mb, manual);
    }

    [Fact]
    public void A_tiny_OS_budget_keeps_the_absolute_floor()
    {
        long result = VramBudgetPolicy.Compute(
            1000L * Mb, 900L * Mb, 900L * Mb, 0L, null, 0, 0, out _);
        Assert.Equal(VramBudgetPolicy.MinBudgetBytes, result);
    }

    // ---- BUG-PERF-19: the whole card ----

    // In-world 2026-10-11, RTX 4070 12 GB, after the switch Ultra -> High: 7-8 fps. The card held 11.3 GB:
    // Puris 7.25 GB, Sunshine 2.0, a slicer 1.36, Edge 0.55 and the rest; DXGI still granted us 9.0 GB.
    private const long CardDedicated = 12288L * Mb;
    private const long CardAdapterUsage = 11571L * Mb;   // 11.3 GiB, all programs
    private const long CardProcessUsage = 7424L * Mb;    // 7.25 GiB, ours
    private const long CardOsBudget = 9216L * Mb;        // 9.0 GiB, what DXGI granted
    private const long CardGodot = 5400L * Mb;
    private const long CardCache = 3600L * Mb;

    private static long CardNonCache()
    {
        long slack = CardProcessUsage - CardGodot;
        long counted = System.Math.Min(slack, (long)(CardOsBudget * VramBudgetPolicy.SlackFraction));
        return (CardGodot - CardCache) + counted;
    }

    [Fact]
    public void Card_budget_is_the_limit_share_of_the_dedicated_memory_less_everybody_elses()
    {
        long others = CardAdapterUsage - CardProcessUsage;
        long expected = (long)(CardDedicated * VramBudgetPolicy.AdapterUsageLimit) - others;
        Assert.Equal(expected, VramBudgetPolicy.CardBudget(CardDedicated, CardAdapterUsage, CardProcessUsage));
    }

    [Fact]
    public void Card_budget_is_unlimited_when_an_adapter_figure_is_unknown()
    {
        Assert.Equal(long.MaxValue, VramBudgetPolicy.CardBudget(0, CardAdapterUsage, CardProcessUsage));
        Assert.Equal(long.MaxValue, VramBudgetPolicy.CardBudget(CardDedicated, 0, CardProcessUsage));
    }

    [Fact]
    public void A_full_card_with_other_programs_on_it_lowers_the_budget_the_per_process_figure_would_allow()
    {
        long withoutCard = VramBudgetPolicy.Compute(
            CardOsBudget, CardProcessUsage, CardGodot, CardCache, null, 0, 0, out _);
        long withCard = VramBudgetPolicy.Compute(
            CardOsBudget, CardProcessUsage, CardGodot, CardDedicated, CardAdapterUsage, CardCache, null, 0, 0, out bool rose);

        // per-process: 9216 - 3182 - 922 = 5112 MB; card: 0.92 * 12288 - 4147 - 3182 = 3976 MB.
        long expectedCard = VramBudgetPolicy.CardBudget(CardDedicated, CardAdapterUsage, CardProcessUsage) - CardNonCache();
        Assert.Equal(expectedCard, withCard);
        Assert.True(withCard < withoutCard, $"{withCard / Mb} MB must be below {withoutCard / Mb} MB");
        Assert.True(withoutCard - withCard > 1000L * Mb, "the incident's card was 1.1 GB short of room for the per-process target");
        Assert.True(withCard > 2L * Gb, "still a usable budget, not the floor");
        Assert.False(rose);
    }

    [Fact]
    public void A_card_with_room_leaves_the_per_process_budget_alone()
    {
        long adapterUsage = 6000L * Mb; // others hold 2 GB of a 12 GB card
        long process = 4000L * Mb;
        long without = VramBudgetPolicy.Compute(9000L * Mb, process, 3000L * Mb, 1500L * Mb, null, 0, 0, out _);
        long with = VramBudgetPolicy.Compute(9000L * Mb, process, 3000L * Mb, CardDedicated, adapterUsage, 1500L * Mb, null, 0, 0, out _);
        Assert.Equal(without, with);
    }

    [Fact]
    public void No_adapter_reading_behaves_exactly_like_before()
    {
        long a = VramBudgetPolicy.Compute(CardOsBudget, CardProcessUsage, CardGodot, CardCache, null, 0, 0, out _);
        long b = VramBudgetPolicy.Compute(CardOsBudget, CardProcessUsage, CardGodot, 0, 0, CardCache, null, 0, 0, out _);
        Assert.Equal(a, b);
    }

    [Fact]
    public void A_card_that_others_have_filled_cannot_push_the_budget_below_the_floor()
    {
        // The other programs alone hold 11 of 12 GB: the card has nothing left for us, the floor is what we keep.
        long result = VramBudgetPolicy.Compute(
            CardOsBudget, 1000L * Mb, 800L * Mb, CardDedicated, 12000L * Mb, 300L * Mb, null, 0, 0, out _);
        Assert.Equal((long)(CardOsBudget * VramBudgetPolicy.FloorFraction), result);
    }

    [Fact]
    public void A_manual_cap_below_the_card_budget_still_wins()
    {
        long result = VramBudgetPolicy.Compute(
            CardOsBudget, CardProcessUsage, CardGodot, CardDedicated, CardAdapterUsage, CardCache, 1500L * Mb, 0, 0, out _);
        Assert.Equal(1500L * Mb, result);
    }

    [Fact]
    public void Memory_the_process_keeps_after_the_cache_shrinks_does_not_drive_the_card_budget_down_again()
    {
        // The cache gives back 1 GB, Godot's own figure falls with it, the process keeps the memory (the
        // allocator slack of BUG-PERF-18): usage and the card stay as they were. The slack allowance is already
        // used up, so the budget must not fall by the 1 GB a second time and again on every tick.
        long first = VramBudgetPolicy.Compute(
            CardOsBudget, CardProcessUsage, CardGodot, CardDedicated, CardAdapterUsage, CardCache, null, 0, 0, out _);
        long after = VramBudgetPolicy.Compute(
            CardOsBudget, CardProcessUsage, CardGodot - 1000L * Mb, CardDedicated, CardAdapterUsage, CardCache - 1000L * Mb, null, 0, 0, out _);
        Assert.Equal(first, after);
    }

    [Fact]
    public void Other_programs_leaving_the_card_raise_the_budget_slowly_but_taking_room_lowers_it_at_once()
    {
        long previous = 5000L * Mb;

        // Others grow: the card target (~3976 MB) is far below the previous budget -> applied at once.
        long lowered = VramBudgetPolicy.Compute(
            CardOsBudget, CardProcessUsage, CardGodot, CardDedicated, CardAdapterUsage, CardCache, null, previous, 0, out bool roseDown);
        Assert.True(lowered < previous * VramBudgetPolicy.DropThreshold);
        Assert.False(roseDown);

        // Others leave: the target is the per-process one (~5112 MB), and it is reached in a capped step.
        long lowGodotRise = VramBudgetPolicy.Compute(
            CardOsBudget, CardProcessUsage, CardGodot, CardDedicated, CardProcessUsage + 500L * Mb, CardCache, null, lowered, 20.0, out bool roseUp);
        Assert.True(roseUp);
        Assert.True(lowGodotRise <= (long)(lowered * VramBudgetPolicy.MaxRiseFactor));
        Assert.True(lowGodotRise > lowered);
    }

    [Fact]
    public void An_adapter_reading_lower_than_the_process_own_usage_counts_nobody_else()
    {
        // Two counters sampled at different moments can disagree by a few hundred MB; never a negative "others".
        long result = VramBudgetPolicy.CardBudget(CardDedicated, 5000L * Mb, 5500L * Mb);
        Assert.Equal((long)(CardDedicated * VramBudgetPolicy.AdapterUsageLimit), result);
    }

    // FEAT-PERF-28: the terms behind a budget, so a falling budget can be explained from the log.

    [Fact]
    public void Explain_names_the_terms_the_per_process_target_is_made_of()
    {
        long osBudget = 8000L * Mb, osUsage = 3000L * Mb, godot = 2600L * Mb, cache = 1500L * Mb;
        // godotNonCache = 1100, slack = 400 (under 15 % of 8000 = 1200, so all counted), headroom = 800
        var t = VramBudgetPolicy.Explain(osBudget, osUsage, godot, 0, 0, cache, null);

        Assert.Equal(osBudget, t.OsBudget);
        Assert.Equal(osUsage, t.OsUsage);
        Assert.Equal(godot, t.GodotUsage);
        Assert.Equal(cache, t.CacheBytes);
        Assert.Equal(400L * Mb, t.Slack);
        Assert.Equal(400L * Mb, t.SlackCounted);
        Assert.Equal(1500L * Mb, t.NonCache);
        Assert.Equal(800L * Mb, t.Headroom);
        Assert.Equal(0, t.OthersOnCard);
        Assert.Equal(long.MaxValue, t.CardBudget);
        Assert.Equal(8000L * Mb - 1500L * Mb - 800L * Mb, t.Target);
    }

    [Fact]
    public void Explain_counts_only_a_bounded_share_of_the_slack_while_the_os_is_not_asking_us_to_trim()
    {
        long osBudget = 8000L * Mb;
        // 6 GB the allocator holds that Godot does not account for: only 15 % of the budget is counted.
        var t = VramBudgetPolicy.Explain(osBudget, 7000L * Mb, 1000L * Mb, 0, 0, 500L * Mb, null);

        Assert.Equal(6000L * Mb, t.Slack);
        Assert.Equal(1200L * Mb, t.SlackCounted);
    }

    [Fact]
    public void Explain_shows_the_other_programs_on_the_card_and_the_card_budget()
    {
        long dedicated = 12000L * Mb, adapterUsage = 11000L * Mb, osUsage = 7000L * Mb;
        var t = VramBudgetPolicy.Explain(9000L * Mb, osUsage, 5000L * Mb, dedicated, adapterUsage, 2000L * Mb, null);

        Assert.Equal(4000L * Mb, t.OthersOnCard);
        Assert.Equal(VramBudgetPolicy.CardBudget(dedicated, adapterUsage, osUsage), t.CardBudget);
    }

    [Theory]
    [InlineData(8000, 3000, 2600, 0, 0, 1500, null)]
    [InlineData(8000, 7000, 1000, 0, 0, 500, null)]
    [InlineData(9000, 7000, 5000, 12000, 11000, 2000, null)]
    [InlineData(9000, 7000, 5000, 12000, 11800, 2000, 3000)]
    [InlineData(500, 480, 400, 0, 0, 100, null)]
    public void Explain_target_is_what_Compute_returns_on_its_first_call(
        int osBudgetMb, int osUsageMb, int godotMb, int adapterDedicatedMb, int adapterUsageMb, int cacheMb, int? manualMb)
    {
        long? manual = manualMb.HasValue ? manualMb.Value * Mb : (long?)null;
        var t = VramBudgetPolicy.Explain(osBudgetMb * Mb, osUsageMb * Mb, godotMb * Mb,
            adapterDedicatedMb * Mb, adapterUsageMb * Mb, cacheMb * Mb, manual);

        long first = VramBudgetPolicy.Compute(osBudgetMb * Mb, osUsageMb * Mb, godotMb * Mb,
            adapterDedicatedMb * Mb, adapterUsageMb * Mb, cacheMb * Mb, manual, 0, 0, out _);

        Assert.Equal(first, t.Target);
    }

    [Fact]
    public void The_formatted_terms_carry_every_figure_the_budget_is_made_of()
    {
        var t = VramBudgetPolicy.Explain(9000L * Mb, 7000L * Mb, 5000L * Mb, 12000L * Mb, 11000L * Mb, 2000L * Mb, null);

        string line = VramBudgetPolicy.FormatTerms(t, budget: 1473L * Mb, previous: 3456L * Mb);

        Assert.StartsWith("[VramTerms]", line);
        Assert.Contains("budget=1473MB", line);
        Assert.Contains("was=3456MB", line);
        Assert.Contains("osBudget=9000MB", line);
        Assert.Contains("osUsage=7000MB", line);
        Assert.Contains("godot=5000MB", line);
        Assert.Contains("cache=2000MB", line);
        Assert.Contains("slackCounted=", line);
        Assert.Contains("others=4000MB", line);
        Assert.Contains("cardBudget=", line);
    }

    [Fact]
    public void An_unknown_card_budget_prints_as_none_not_as_a_huge_number()
    {
        var t = VramBudgetPolicy.Explain(9000L * Mb, 7000L * Mb, 5000L * Mb, 0, 0, 2000L * Mb, null);

        string line = VramBudgetPolicy.FormatTerms(t, budget: 1000L * Mb, previous: 0);

        Assert.Contains("cardBudget=none", line);
        Assert.Contains("was=none", line);
    }
}
