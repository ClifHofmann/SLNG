using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;

namespace SLNG.Net;

/// <summary>
/// Assets a region's <c>ViewerAsset</c> capability has told us it will not hand over (BUG-ASSET-02),
/// remembered for a while. Without it a crowd that all plays the same animation asks for it once per
/// avatar and is refused every time: on a busy region that was 124 requests for ONE animation in a few
/// seconds, each ending in a second refusal through LibreMetaverse's own path and a warning on the console.
///
/// A refusal is one region's answer, so it is kept per region as well as per asset: another region may
/// well serve what this one would not. And it expires, since a refusal can be about timing (an
/// animation that is not yet "in use" there) as much as about permission.
/// </summary>
internal sealed class AssetRefusals
{
    /// <summary>How long a refusal is believed.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);

    // A cap on the table, so a region that refuses thousands of different ids cannot grow it for good.
    private const int PurgeAbove = 2048;

    private readonly ConcurrentDictionary<(ulong Region, Guid Asset), (DateTime Until, int Strikes)> _until = new();
    private readonly Func<DateTime> _utcNow;

    /// <param name="lifetime">The longest a refusal is believed.</param>
    /// <param name="firstLifetime">How long the FIRST refusal of an asset is believed. Each further refusal
    /// (one that comes after the previous has lapsed) doubles it, up to <paramref name="lifetime"/>. Omitted, it
    /// is <paramref name="lifetime"/> itself: a flat memory. A refusal can be about timing -- a pose that was
    /// just switched to is not served to other viewers for a moment -- and ten minutes of "no" then means a
    /// pose that never shows up; a short first memory still protects against a crowd asking for one asset a
    /// hundred times in a few seconds (the original reason for this class).</param>
    public AssetRefusals(TimeSpan? lifetime = null, Func<DateTime>? utcNow = null, TimeSpan? firstLifetime = null)
    {
        Lifetime = lifetime ?? DefaultLifetime;
        FirstLifetime = firstLifetime is { } first ? TimeSpan.FromTicks(Math.Min(first.Ticks, Lifetime.Ticks)) : Lifetime;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>The longest a refusal is believed.</summary>
    public TimeSpan Lifetime { get; }

    /// <summary>How long the first refusal of an asset is believed; see the constructor.</summary>
    public TimeSpan FirstLifetime { get; }

    private TimeSpan LifetimeFor(int strikes)
    {
        // 2^(strikes-1) x first, capped: 5 s, 10 s, 20 s, 40 s ... up to the longest.
        long ticks = FirstLifetime.Ticks;
        for (int i = 1; i < strikes && ticks < Lifetime.Ticks; i++) ticks = Math.Min(ticks * 2, Lifetime.Ticks);
        return TimeSpan.FromTicks(Math.Min(ticks, Lifetime.Ticks));
    }

    /// <summary>Whether the grid answering for an asset with this status means "no", as opposed to "not
    /// now": forbidden, not found and gone are decisions; a timeout, a rate limit or a server error are
    /// not, and are worth asking again (and, for the legacy path, falling back on).</summary>
    public static bool IsRefusal(HttpStatusCode status)
        => status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone;

    /// <summary>True while a refusal for this asset in this region is still believed.</summary>
    public bool IsRefused(ulong region, Guid assetId)
    {
        var key = (region, assetId);
        if (!_until.TryGetValue(key, out var entry)) return false;
        // A lapsed refusal stays in the table so that the NEXT one knows how many came before it; it is
        // swept by Purge, not here.
        return _utcNow() < entry.Until;
    }

    /// <summary>Notes a refusal and returns true if it is news, so the caller logs it once and not per
    /// request. A refusal that is still believed is only renewed.</summary>
    public bool Remember(ulong region, Guid assetId)
    {
        var key = (region, assetId);
        var now = _utcNow();
        if (_until.Count >= PurgeAbove) Purge();

        _until.TryGetValue(key, out var previous);
        bool news = previous.Strikes == 0 || now >= previous.Until;
        // The crowd behind one refusal does not count as another: only a refusal that comes after the last one
        // lapsed is a new strike.
        int strikes = previous.Strikes == 0 ? 1 : news ? previous.Strikes + 1 : previous.Strikes;
        _until[key] = (now + LifetimeFor(strikes), strikes);
        return news;
    }

    /// <summary>How many refusals the table holds, expired ones included until they are looked at.</summary>
    public int Count => _until.Count;

    private void Purge()
    {
        // A lapsed refusal is only worth keeping (for its strike count) for as long as the longest one lasts again.
        var now = _utcNow();
        foreach (var key in _until.Where(p => p.Value.Until + Lifetime <= now).Select(p => p.Key).ToList())
            _until.TryRemove(key, out _);
    }
}
