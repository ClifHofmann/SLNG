using System.Text.Json;

namespace SLNG.Core;

/// <summary>
/// Display Names remembered between sessions, so a nametag can show the right name the moment an
/// avatar appears instead of after a round trip to the grid.
///
/// <para>The grid answers a name lookup in a second or more, and at login the first ones cannot
/// even be asked yet (the capability is not resolved). The reference viewer avoids the wait the
/// same way: it keeps an avatar-name cache on disk and shows what it has while it refreshes.
/// Names also change rarely (the grid enforces a wait of days between changes), so a cached name
/// is almost always still right.</para>
///
/// <para><b>Three states, not two.</b> <see cref="Freshness.Fresh"/> is used as it stands.
/// <see cref="Freshness.Stale"/> is shown immediately AND looked up again, so a name that changed
/// while we were away corrects itself within a moment. <see cref="Freshness.Miss"/> is a normal
/// lookup. An entry whose name is empty is a remembered "no Display Name set" — most residents
/// never set one, and asking again on every login for every one of them is the bulk of the
/// traffic this cache exists to remove.</para>
///
/// <para>Lives in <c>SLNG.Core</c> so the rules (expiry, tolerance of a damaged file, size cap)
/// are tested without a grid; <c>GridSession</c> owns reading and writing the file. Thread-safe:
/// lookups arrive from network threads.</para>
/// </summary>
public sealed class DisplayNameCache
{
    /// <summary>How long a cached answer is trusted without asking again.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(24);

    /// <summary>Upper bound on remembered residents. A busy region visited for months would
    /// otherwise grow the file without limit; the oldest answers are the ones dropped.</summary>
    public const int MaxEntries = 20_000;

    private const int FormatVersion = 1;

    public enum Freshness { Miss, Fresh, Stale }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, (string Name, DateTime FetchedUtc)> _entries = new();

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>Looks an agent up. <paramref name="name"/> is empty for a miss AND for a
    /// remembered "no Display Name".</summary>
    public Freshness Lookup(Guid id, DateTime nowUtc, out string name)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                name = string.Empty;
                return Freshness.Miss;
            }

            name = entry.Name;
            return nowUtc - entry.FetchedUtc < FreshFor ? Freshness.Fresh : Freshness.Stale;
        }
    }

    /// <summary>Remembers an answer. A null or empty <paramref name="name"/> records that the
    /// resident has no Display Name of their own.</summary>
    public void Set(Guid id, string? name, DateTime nowUtc)
    {
        if (id == Guid.Empty) return;
        lock (_gate) _entries[id] = (name ?? string.Empty, nowUtc);
    }

    /// <summary>Takes over another cache's entries, keeping whichever answer is newer per agent.
    /// Used when the file is loaded after some lookups have already been answered this
    /// session.</summary>
    public void Absorb(DisplayNameCache other)
    {
        if (ReferenceEquals(other, this)) return;
        List<KeyValuePair<Guid, (string Name, DateTime FetchedUtc)>> incoming;
        lock (other._gate) incoming = other._entries.ToList();

        lock (_gate)
        {
            foreach (var (id, entry) in incoming)
            {
                if (!_entries.TryGetValue(id, out var mine) || entry.FetchedUtc > mine.FetchedUtc)
                    _entries[id] = entry;
            }
        }
    }

    /// <summary>Every agent id currently remembered with a real Display Name, for replaying to a
    /// listener that subscribed late.</summary>
    public List<(Guid Id, string Name)> NamedEntries()
    {
        lock (_gate)
        {
            return _entries
                .Where(e => e.Value.Name.Length > 0)
                .Select(e => (e.Key, e.Value.Name))
                .ToList();
        }
    }

    private sealed record Row(Guid Id, string Name, DateTime At);

    private sealed record FileShape(int Version, List<Row> Entries);

    /// <summary>Serialises the cache, newest answers first, cut to <see cref="MaxEntries"/>.</summary>
    public string ToJson()
    {
        List<Row> rows;
        lock (_gate)
        {
            rows = _entries
                .OrderByDescending(e => e.Value.FetchedUtc)
                .Take(MaxEntries)
                .Select(e => new Row(e.Key, e.Value.Name, e.Value.FetchedUtc))
                .ToList();
        }

        return JsonSerializer.Serialize(new FileShape(FormatVersion, rows));
    }

    /// <summary>Reads a cache back. Anything wrong with the text -- empty, truncated by a crash,
    /// a newer format, not JSON at all -- gives an EMPTY cache rather than an exception: the cache
    /// is an optimisation, and a damaged one must cost one lookup round, never a login.</summary>
    public static DisplayNameCache FromJson(string? json)
    {
        var cache = new DisplayNameCache();
        if (string.IsNullOrWhiteSpace(json)) return cache;

        try
        {
            var shape = JsonSerializer.Deserialize<FileShape>(json);
            if (shape == null || shape.Version != FormatVersion || shape.Entries == null) return cache;

            foreach (var row in shape.Entries.Take(MaxEntries))
            {
                if (row == null || row.Id == Guid.Empty) continue;
                cache._entries[row.Id] = (row.Name ?? string.Empty, row.At);
            }
        }
        catch (JsonException)
        {
            return new DisplayNameCache();
        }

        return cache;
    }
}
