using System;

namespace SLNG.Core;

public static class VramBudgetPolicy
{
    public const long MinBudgetBytes = 256L << 20;
    public const long MinHeadroomBytes = 256L << 20;
    public const double HeadroomFraction = 0.10;

    /// <summary>The budget never falls below this share of the OS budget (BUG-PERF-18). The policy's
    /// own arithmetic can end up at nothing -- in-world 2026-10-11 it sat at the old 256 MB floor on a
    /// card with 8.7 GB, and every world texture was cut to a sixteenth. A transient misreading must
    /// not be able to do that: below a fifth of what the OS grants us, the budget is wrong, not the card
    /// full. Manual mode still honours a smaller user cap (down to <see cref="MinBudgetBytes"/>).</summary>
    public const double FloorFraction = 0.20;

    /// <summary>The share of the OS budget that memory the process holds but Godot no longer accounts for
    /// (<c>osUsage - godotUsage</c>: freed textures the allocator pools, the driver's own overhead) counts
    /// as non-cache usage while the OS is not asking us to trim. See <see cref="Compute(long,long,long,long,long?,long,double,out bool)"/>.</summary>
    public const double SlackFraction = 0.15;

    /// <summary>BUG-PERF-19: the share of the card's dedicated memory the whole machine may fill before
    /// we hold back. Above it Windows starts demoting somebody's allocations to system memory, and in
    /// the one case measured that somebody was us (see <see cref="CardBudget"/>).</summary>
    public const double AdapterUsageLimit = 0.92;

    /// <summary>The adapter reading is only believed this recent; a stale or missing one leaves the budget
    /// to the per-process figure alone, exactly as before BUG-PERF-19.</summary>
    public const double AdapterReadingMaxAgeSeconds = 15;

    public const double DropThreshold = 0.97;     // a fall of more than 3 % applies at once
    public const double RiseThreshold = 1.03;     // a rise of more than 3 % is considered...
    public const double MaxRiseFactor = 1.10;     // ...but grows by at most 10 % per step
    public const double RiseIntervalSeconds = 10; // and at most once every 10 s

    /// <summary>
    /// Computes the GPU texture cache budget based on OS video memory budget and usage, for a caller that
    /// has no figure of Godot's own: everything the process holds beyond the cache counts as non-cache usage.
    /// </summary>
    public static long Compute(
        long osBudget,
        long osUsage,
        long cacheBytes,
        long? manualCapBytes,
        long previousBudget,
        double secondsSinceLastRise,
        out bool rose)
        => Compute(osBudget, osUsage, osUsage, cacheBytes, manualCapBytes, previousBudget, secondsSinceLastRise, out rose);

    /// <summary>
    /// Computes the GPU texture cache budget based on OS video memory budget and usage.
    /// <list type="bullet">
    /// <item><paramref name="osBudget"/>: what Windows grants this process (bytes), already capped by --vram-budget.</item>
    /// <item><paramref name="osUsage"/>: what this process currently uses on the card (bytes).</item>
    /// <item><paramref name="godotUsage"/>: what Godot itself says it holds on the card (bytes) -- its
    /// <c>RenderVideoMemUsed</c> monitor; the same figure the stats overlay shows. Pass <paramref name="osUsage"/>
    /// when unknown.</item>
    /// <item><paramref name="cacheBytes"/>: current cache size in bytes.</item>
    /// <item><paramref name="manualCapBytes"/>: manual budget cap in bytes, or null in Auto mode.</item>
    /// </list>
    ///
    /// <para><b>Why two usage figures (BUG-PERF-18).</b> In-world 2026-10-11 (RTX 4070, 12 GB), Millenium ->
    /// Sirens -> Millenium: DXGI said the process used 8.4 GB while Godot's own counters said 2.6 GB.
    /// The other ~6 GB were the old region's freed textures, which the allocator pools or the driver has
    /// not given back. Counted as "someone else's memory" (<c>osUsage - cacheBytes</c>), they pushed the
    /// budget to its floor, the 356 MB the cache then held was all pinned, and the world came back at a
    /// sixteenth of its size with textures that never arrived. That memory is not another program's: it is
    /// ours, and the allocator reuses it for the next texture. So the non-cache usage is Godot's own
    /// figure minus the cache, plus the part Godot cannot account for -- <i>bounded</i> to
    /// <see cref="SlackFraction"/> of the OS budget (the driver's real, steady overhead is a few hundred MB)
    /// while the OS is not complaining, and in full once <c>osUsage &gt; osBudget</c>, because then
    /// Windows is demoting us and the memory is no longer something to hope for.</para>
    /// </summary>
    /// <returns>The new GpuCache budget in bytes.</returns>
    public static long Compute(
        long osBudget,
        long osUsage,
        long godotUsage,
        long cacheBytes,
        long? manualCapBytes,
        long previousBudget,
        double secondsSinceLastRise,
        out bool rose)
        => Compute(osBudget, osUsage, godotUsage, 0, 0, cacheBytes, manualCapBytes, previousBudget, secondsSinceLastRise, out rose);

    /// <summary>
    /// The card-wide ceiling on what this process may hold, in bytes: <see cref="AdapterUsageLimit"/> of the
    /// adapter's dedicated memory, minus what every OTHER program holds on it
    /// (<c>adapterUsage - osUsage</c>). <see cref="long.MaxValue"/> when either adapter figure is unknown.
    ///
    /// <para><b>Why this exists (BUG-PERF-19).</b> In-world 2026-10-11 (RTX 4070, 12 GB) the viewer ran at 40+ fps on
    /// Ultra, then dropped to 7-8 fps for good after a switch to High: renderGpuMs 125-274 ms with ~8.5 k draws,
    /// i.e. the GPU waiting on memory. The card held 11.3 of 12 GB -- Puris 7.25, Sunshine 2.0, a slicer 1.36,
    /// Edge 0.55 -- and DXGI's per-process budget for us still read 9.0 GB. That figure is a share Windows is
    /// willing to grant when it is asked, not a promise that the card has the room: three other programs were
    /// already on it, and nothing in the per-process numbers shows them.</para>
    ///
    /// <para>The ceiling is measured at the target, not at the present usage. "Lower the budget by how far the
    /// adapter is over 92 % right now" is zero until the card is already full, and subtracting it from a target
    /// the cache has not reached changes nothing; this form holds the cache back while the card still has room,
    /// which is the only time shrinking it helps. It does not depend on the cache's current size, so freed memory
    /// that the allocator keeps (the BUG-PERF-18 slack) cannot make it chase its own tail down to the floor.</para>
    /// </summary>
    public static long CardBudget(long adapterDedicated, long adapterUsage, long osUsage)
    {
        if (adapterDedicated <= 0 || adapterUsage <= 0) return long.MaxValue;
        long others = Math.Max(0L, adapterUsage - Math.Max(0L, osUsage));
        return (long)(adapterDedicated * AdapterUsageLimit) - others;
    }

    /// <summary>
    /// FEAT-PERF-28: the figures a budget is made of, before smoothing. <c>[GpuCache]</c> printed only the result, so nobody could say
    /// why a budget fell from 3456 to 1473 MB (ADR 0005, cause 4). <see cref="Compute(long,long,long,long,long,long,long?,long,double,out bool)"/>
    /// takes its target from <see cref="Explain"/>, so the logged terms cannot drift from the arithmetic.
    /// </summary>
    /// <param name="OsBudget">What Windows grants the process.</param>
    /// <param name="OsUsage">What the process holds on the card.</param>
    /// <param name="GodotUsage">What Godot says it holds (the OS figure when unknown), never above <paramref name="OsUsage"/>.</param>
    /// <param name="CacheBytes">The texture cache's own size.</param>
    /// <param name="Slack">Held by the process but not accounted for by Godot (<c>OsUsage - GodotUsage</c>).</param>
    /// <param name="SlackCounted">The part of the slack the target reserves room for (bounded to <see cref="SlackFraction"/> unless the OS is over budget).</param>
    /// <param name="NonCache">Godot's memory besides the cache plus <paramref name="SlackCounted"/>.</param>
    /// <param name="Headroom">The reserve kept free.</param>
    /// <param name="Floor">The lowest budget the policy's own arithmetic may end at.</param>
    /// <param name="OthersOnCard">What every other program holds on the card; 0 when no adapter reading.</param>
    /// <param name="CardBudget">What the card can afford for this process (<see cref="long.MaxValue"/> when unknown).</param>
    /// <param name="Target">The budget the terms lead to, before the smoothing against the previous one.</param>
    public readonly record struct Terms(
        long OsBudget, long OsUsage, long GodotUsage, long CacheBytes,
        long Slack, long SlackCounted, long NonCache, long Headroom, long Floor,
        long OthersOnCard, long CardBudget, long Target);

    /// <summary>The terms and target for these readings. See <see cref="Terms"/>.</summary>
    public static Terms Explain(
        long osBudget,
        long osUsage,
        long godotUsage,
        long adapterDedicated,
        long adapterUsage,
        long cacheBytes,
        long? manualCapBytes)
    {
        // 1. What Godot holds besides the cache, plus the slack the process holds that Godot does not
        //    account for. Godot reading higher than the OS never makes the budget smaller than the
        //    OS-only reading would have.
        long godot = Math.Min(godotUsage > 0 ? godotUsage : osUsage, osUsage);
        long godotNonCache = Math.Max(0L, godot - cacheBytes);
        long slack = Math.Max(0L, osUsage - godot);
        long countedSlack = osUsage > osBudget
            ? slack
            : Math.Min(slack, (long)(osBudget * SlackFraction));
        long nonCache = godotNonCache + countedSlack;

        // 2. headroom = max(MinHeadroomBytes, osBudget * HeadroomFraction).
        long headroom = Math.Max(MinHeadroomBytes, (long)(osBudget * HeadroomFraction));

        // 3. target = max(floor, osBudget - nonCache - headroom).
        long floor = Math.Max(MinBudgetBytes, (long)(osBudget * FloorFraction));
        long target = Math.Max(floor, osBudget - nonCache - headroom);

        // 3b. The whole card (BUG-PERF-19): what is left of it after everybody else, less what we hold besides the cache.
        long cardBudget = CardBudget(adapterDedicated, adapterUsage, osUsage);
        long others = cardBudget == long.MaxValue ? 0L : Math.Max(0L, adapterUsage - Math.Max(0L, osUsage));
        if (cardBudget != long.MaxValue)
            target = Math.Min(target, Math.Max(floor, cardBudget - nonCache));

        // 4. If manualCapBytes has a value: target = min(target, manualCapBytes). In Manual mode the OS budget is still a hard safety limit.
        if (manualCapBytes.HasValue)
        {
            target = Math.Min(target, manualCapBytes.Value);
            target = Math.Max(MinBudgetBytes, target);
        }

        return new Terms(osBudget, osUsage, godot, cacheBytes, slack, countedSlack, nonCache, headroom, floor, others, cardBudget, target);
    }

    /// <summary>One <c>[VramTerms]</c> line: the new budget, the one before it (0 = none) and every term it is made of.</summary>
    public static string FormatTerms(Terms t, long budget, long previous)
    {
        static string Mb(long bytes) => (bytes >> 20).ToString(System.Globalization.CultureInfo.InvariantCulture) + "MB";
        return $"[VramTerms] budget={Mb(budget)} was={(previous > 0 ? Mb(previous) : "none")} target={Mb(t.Target)} " +
               $"osBudget={Mb(t.OsBudget)} osUsage={Mb(t.OsUsage)} godot={Mb(t.GodotUsage)} cache={Mb(t.CacheBytes)} " +
               $"slack={Mb(t.Slack)} slackCounted={Mb(t.SlackCounted)} nonCache={Mb(t.NonCache)} headroom={Mb(t.Headroom)} " +
               $"floor={Mb(t.Floor)} others={Mb(t.OthersOnCard)} cardBudget={(t.CardBudget == long.MaxValue ? "none" : Mb(t.CardBudget))}";
    }

    /// <summary>
    /// <see cref="Compute(long,long,long,long,long?,long,double,out bool)"/> that also respects the whole card
    /// (BUG-PERF-19): <paramref name="adapterDedicated"/> is the adapter's dedicated video memory and
    /// <paramref name="adapterUsage"/> what ALL processes hold on it right now (the "GPU Adapter Memory" counter);
    /// pass 0 for either when unknown and the result is the per-process one. The target is the smaller of the
    /// per-process target and the one the card can afford (<see cref="CardBudget"/> - non-cache - nothing more:
    /// the 8 % the limit leaves free is the headroom), never below the floor.
    /// </summary>
    public static long Compute(
        long osBudget,
        long osUsage,
        long godotUsage,
        long adapterDedicated,
        long adapterUsage,
        long cacheBytes,
        long? manualCapBytes,
        long previousBudget,
        double secondsSinceLastRise,
        out bool rose)
    {
        rose = false;

        long target = Explain(osBudget, osUsage, godotUsage, adapterDedicated, adapterUsage, cacheBytes, manualCapBytes).Target;

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
