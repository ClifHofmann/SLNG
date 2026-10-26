using System.Globalization;

namespace SLNG.Core.Login;

/// <summary>FEAT-UI-72: what the person picked on the login screen's "Start at" dropdown.</summary>
public enum StartKind
{
    /// <summary>"My last location" -- the default, and what every login did before this feature.</summary>
    Last,

    /// <summary>"My home".</summary>
    Home,

    /// <summary>One of the landmarks in the inventory Favorites folder.</summary>
    Favorite,

    /// <summary>"Region..." -- a region (or SLURL) typed by the person.</summary>
    Region,
}

/// <summary>
/// FEAT-UI-72: the remembered "Start at" choice, and its one-line text form in <c>logins.cfg</c>:
/// <c>last</c> | <c>home</c> | <c>fav:&lt;landmark asset guid, N format&gt;</c> | <c>region:&lt;typed text&gt;</c>.
/// <see cref="Key"/> is the favorite's asset id or the typed text, empty for last/home.
///
/// <para>A favorite is keyed by its <b>landmark asset id</b>, not its inventory item id: the asset is
/// immutable (region id and local position), so the key survives a rename and a re-copy of the same
/// landmark.</para>
/// </summary>
public readonly record struct StartChoice(StartKind Kind, string Key)
{
    /// <summary>Longest serialised choice that is read back. A hostile or corrupted <c>logins.cfg</c> value
    /// (a megabyte of text) reads as <see cref="Last"/> instead of being carried around.</summary>
    public const int MaxSerializedLength = 2048;

    private const string FavoritePrefix = "fav:";
    private const string RegionPrefix = "region:";

    /// <summary>My last location.</summary>
    public static StartChoice Last => new(StartKind.Last, "");

    /// <summary>My home.</summary>
    public static StartChoice Home => new(StartKind.Home, "");

    /// <summary>A favorite, by the landmark's asset id.</summary>
    public static StartChoice Favorite(Guid landmarkAssetId) =>
        new(StartKind.Favorite, landmarkAssetId.ToString("N", CultureInfo.InvariantCulture));

    /// <summary>A region (or SLURL) as typed. The text is kept as typed; it is only interpreted by
    /// <see cref="LoginStart.TryParseTyped"/>.</summary>
    public static StartChoice Region(string typedText) => new(StartKind.Region, typedText ?? "");

    /// <summary>The favorite's landmark asset id, or <see cref="Guid.Empty"/> for any other kind.</summary>
    public Guid FavoriteAssetId =>
        Kind == StartKind.Favorite && Guid.TryParseExact(Key, "N", out var id) ? id : Guid.Empty;

    /// <summary>The text stored in <c>logins.cfg</c>. A region choice with no text serialises as
    /// <c>region:</c>, which <see cref="Parse"/> reads back as <see cref="Last"/>.</summary>
    public string Serialize() => Kind switch
    {
        StartKind.Home => "home",
        StartKind.Favorite => FavoritePrefix + Key,
        StartKind.Region => RegionPrefix + Key,
        _ => "last",
    };

    /// <summary>Reads a stored choice. Anything unreadable -- null, empty, an unknown word, a favorite
    /// that is not a GUID, an empty region, an absurdly long value -- is <see cref="Last"/>.</summary>
    public static StartChoice Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxSerializedLength) return Last;

        if (text == "last") return Last;
        if (text == "home") return Home;

        if (text.StartsWith(FavoritePrefix, StringComparison.Ordinal))
        {
            string key = text[FavoritePrefix.Length..];
            if (Guid.TryParse(key, out var id) && id != Guid.Empty) return Favorite(id);
            return Last;
        }

        if (text.StartsWith(RegionPrefix, StringComparison.Ordinal))
        {
            string typed = text[RegionPrefix.Length..];
            return string.IsNullOrWhiteSpace(typed) ? Last : Region(typed);
        }

        return Last;
    }
}
