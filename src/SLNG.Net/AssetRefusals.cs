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

    private readonly ConcurrentDictionary<(ulong Region, Guid Asset), DateTime> _until = new();
    private readonly Func<DateTime> _utcNow;

    public AssetRefusals(TimeSpan? lifetime = null, Func<DateTime>? utcNow = null)
    {
        Lifetime = lifetime ?? DefaultLifetime;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public TimeSpan Lifetime { get; }

    /// <summary>Whether the grid answering for an asset with this status means "no", as opposed to "not
    /// now": forbidden, not found and gone are decisions; a timeout, a rate limit or a server error are
    /// not, and are worth asking again (and, for the legacy path, falling back on).</summary>
    public static bool IsRefusal(HttpStatusCode status)
        => status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone;

    /// <summary>True while a refusal for this asset in this region is still believed.</summary>
    public bool IsRefused(ulong region, Guid assetId)
    {
        var key = (region, assetId);
        if (!_until.TryGetValue(key, out var until)) return false;
        if (_utcNow() < until) return true;
        _until.TryRemove(key, out _);
        return false;
    }

    /// <summary>Notes a refusal and returns true if it is news, so the caller logs it once and not per
    /// request. A refusal that is still believed is only renewed.</summary>
    public bool Remember(ulong region, Guid assetId)
    {
        bool news = !IsRefused(region, assetId);
        if (_until.Count >= PurgeAbove) Purge();
        _until[(region, assetId)] = _utcNow() + Lifetime;
        return news;
    }

    /// <summary>How many refusals the table holds, expired ones included until they are looked at.</summary>
    public int Count => _until.Count;

    private void Purge()
    {
        var now = _utcNow();
        foreach (var key in _until.Where(p => p.Value <= now).Select(p => p.Key).ToList())
            _until.TryRemove(key, out _);
    }
}
