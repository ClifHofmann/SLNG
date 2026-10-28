using System.Globalization;
using System.Text;
using System.Text.Json;
using SLNG.Core.Landmarks;

namespace SLNG.Core.Login;

/// <summary>What <see cref="LoginFavoriteBook.Reconcile"/> made of the Favorites folder.</summary>
/// <param name="Favorites">The favorites the login screen can offer, in the folder's order, each with a known target.</param>
/// <param name="Unresolved">Folder entries that still have no target (never seen, or no asset id yet). They are not in
/// <paramref name="Favorites"/>; the caller resolves them and reconciles again.</param>
public sealed record ReconcileResult(
    IReadOnlyList<LoginFavorite> Favorites,
    IReadOnlyList<LandmarkInventoryItem> Unresolved);

/// <summary>
/// FEAT-UI-72: the per-profile copy of the inventory Favorites folder that the login screen reads (the login
/// screen cannot read the inventory -- there is no session yet). Stored as one line of JSON under the
/// <c>start_favorites</c> key of the profile's <c>logins.cfg</c> section:
/// <code>{"v":1,"items":[{"asset":"&lt;guid&gt;","item":"&lt;guid&gt;","name":"Puris Town","region":"Ahern","x":128,"y":64,"z":30}]}</code>
/// The key being present at all means "this profile's favorites have been written at least once" (an empty
/// <c>items</c> array is a confirmed empty folder); it being absent means "never populated".
///
/// <para>Reading is tolerant by design: a hand-edited, truncated or future-version value must never stop a
/// login screen from showing, so bad JSON, a different <c>v</c> or a bad entry yields fewer favorites, not an
/// exception.</para>
/// </summary>
public static class LoginFavoriteBook
{
    /// <summary>Most favorites kept (the reference viewer's bar is not bounded; the local bar here is, at 64).</summary>
    public const int MaxFavorites = 64;

    /// <summary>Longest stored item name.</summary>
    public const int MaxNameLength = 255;

    /// <summary>Format version written into the JSON.</summary>
    public const int Version = 1;

    /// <summary>The one-line JSON for <paramref name="favorites"/> (at most <see cref="MaxFavorites"/>), plus -- when there are
    /// any -- an <c>offline</c> array of the favorites whose region did not answer (<see cref="LoginOfflineFavorite"/>). A file
    /// without the array reads as "none offline", so nothing written before FEAT-UI-77 changes meaning.</summary>
    public static string ToJson(IReadOnlyList<LoginFavorite> favorites, IReadOnlyList<LoginOfflineFavorite>? offline = null)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("v", Version);
            w.WriteStartArray("items");
            int written = 0;
            foreach (var f in favorites)
            {
                if (written >= MaxFavorites) break;
                w.WriteStartObject();
                w.WriteString("asset", f.AssetId.ToString("N", CultureInfo.InvariantCulture));
                w.WriteString("item", f.ItemId.ToString("N", CultureInfo.InvariantCulture));
                w.WriteString("name", f.Name);
                w.WriteString("region", f.Target.Region);
                w.WriteNumber("x", f.Target.X);
                w.WriteNumber("y", f.Target.Y);
                w.WriteNumber("z", f.Target.Z);
                w.WriteEndObject();
                written++;
            }
            w.WriteEndArray();
            if (offline is { Count: > 0 })
            {
                w.WriteStartArray("offline");
                int off = 0;
                foreach (var o in offline)
                {
                    if (off >= MaxFavorites) break;
                    w.WriteStartObject();
                    w.WriteString("asset", o.AssetId.ToString("N", CultureInfo.InvariantCulture));
                    w.WriteString("item", o.ItemId.ToString("N", CultureInfo.InvariantCulture));
                    w.WriteString("name", o.Name);
                    w.WriteEndObject();
                    off++;
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// The <c>offline</c> entries <see cref="ToJson"/> wrote. Same tolerance as <see cref="FromJson"/>: bad JSON, another
    /// version or a malformed entry gives fewer entries, never an exception; an asset that is also a resolved favorite in the
    /// same file is dropped (a place that can be started at is not offline).
    /// </summary>
    public static IReadOnlyList<LoginOfflineFavorite> OfflineFromJson(string? json)
    {
        var result = new List<LoginOfflineFavorite>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return result;
            if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number
                || !v.TryGetInt32(out int version) || version != Version) return result;
            if (!root.TryGetProperty("offline", out var items) || items.ValueKind != JsonValueKind.Array) return result;

            var resolved = new HashSet<Guid>(FromJson(json).Select(f => f.AssetId));
            var seen = new HashSet<Guid>();
            foreach (var e in items.EnumerateArray())
            {
                if (result.Count >= MaxFavorites) break;
                if (e.ValueKind != JsonValueKind.Object) continue;
                if (!TryGuid(e, "asset", out var asset) || asset == Guid.Empty) continue;
                TryGuid(e, "item", out var item);
                string? name = TryString(e, "name")?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                if (name.Length > MaxNameLength) name = name[..MaxNameLength];
                if (resolved.Contains(asset) || !seen.Add(asset)) continue;

                result.Add(new LoginOfflineFavorite(asset, item, name));
            }
        }
        catch (JsonException)
        {
            result.Clear();
        }
        return result;
    }

    /// <summary>
    /// Reads what <see cref="ToJson"/> wrote. Bad JSON or a version other than <see cref="Version"/> gives an
    /// empty list; an entry that is not a well-formed favorite (no asset id, no name, a region the login
    /// service would refuse, a coordinate out of range) is skipped; a second entry with the same asset id is
    /// dropped (the first wins); at most <see cref="MaxFavorites"/> are returned.
    /// </summary>
    public static IReadOnlyList<LoginFavorite> FromJson(string? json)
    {
        var result = new List<LoginFavorite>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return result;
            if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number
                || !v.TryGetInt32(out int version) || version != Version) return result;
            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return result;

            var seen = new HashSet<Guid>();
            foreach (var e in items.EnumerateArray())
            {
                if (result.Count >= MaxFavorites) break;
                if (e.ValueKind != JsonValueKind.Object) continue;

                if (!TryGuid(e, "asset", out var asset) || asset == Guid.Empty) continue;
                TryGuid(e, "item", out var item);
                string? name = TryString(e, "name")?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                if (name.Length > MaxNameLength) name = name[..MaxNameLength];
                if (!TryNumber(e, "x", out double x) || !TryNumber(e, "y", out double y) || !TryNumber(e, "z", out double z)) continue;
                if (!StartTarget.TryCreate(TryString(e, "region"), x, y, z, StartTarget.OpenMaxXY, out var target)) continue;
                if (!seen.Add(asset)) continue;

                result.Add(new LoginFavorite(asset, item, name, target));
            }
        }
        catch (JsonException)
        {
            result.Clear();
        }
        return result;
    }

    /// <summary>
    /// Matches the Favorites folder (the truth, in its order and with its current names) against what is already
    /// known: a favorite whose landmark asset has a known target keeps that target under the folder's current
    /// name; one without a known target -- or without an asset id yet -- is returned in
    /// <see cref="ReconcileResult.Unresolved"/> and is not offered; an entry in <paramref name="known"/> that the
    /// folder no longer has disappears. A landmark that appears twice in the folder is offered once.
    /// </summary>
    public static ReconcileResult Reconcile(IReadOnlyList<LandmarkInventoryItem> favorites, IReadOnlyList<LoginFavorite> known)
    {
        var byAsset = new Dictionary<Guid, StartTarget>();
        foreach (var k in known)
            byAsset.TryAdd(k.AssetId, k.Target);

        var resolved = new List<LoginFavorite>();
        var unresolved = new List<LandmarkInventoryItem>();
        var seen = new HashSet<Guid>();

        foreach (var item in favorites)
        {
            if (item.AssetId == Guid.Empty)
            {
                unresolved.Add(item);
                continue;
            }
            if (!seen.Add(item.AssetId)) continue;

            if (byAsset.TryGetValue(item.AssetId, out var target))
            {
                string name = (item.Name ?? "").Trim();
                if (name.Length == 0) continue;
                if (name.Length > MaxNameLength) name = name[..MaxNameLength];
                if (resolved.Count < MaxFavorites)
                    resolved.Add(new LoginFavorite(item.AssetId, item.Id, name, target));
            }
            else
            {
                unresolved.Add(item);
            }
        }

        return new ReconcileResult(resolved, unresolved);
    }

    private static string? TryString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool TryGuid(JsonElement e, string name, out Guid value)
    {
        value = Guid.Empty;
        return TryString(e, name) is { } s && Guid.TryParse(s, out value);
    }

    private static bool TryNumber(JsonElement e, string name, out double value)
    {
        value = 0;
        return e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out value);
    }
}
