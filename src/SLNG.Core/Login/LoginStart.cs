using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SLNG.Core.Login;

/// <summary>Why <see cref="LoginStart.Resolve"/> did not start where the choice said.</summary>
public enum StartFallback
{
    /// <summary>The choice was honoured as given.</summary>
    None,

    /// <summary>A remembered favorite is no longer in the profile's favorites; "My last location" was used.</summary>
    FavoriteMissing,

    /// <summary>The typed region could not be understood; "My last location" was used (the login screen does
    /// not let this reach a login -- it keeps Login disabled -- so this is the safety net).</summary>
    RegionInvalid,
}

/// <summary>What <see cref="LoginStart.JudgeLanding"/> concluded about where the grid put the agent.</summary>
public enum LandingVerdict
{
    /// <summary>Nothing to say: the start was honoured, could not be judged, or it is the first login.</summary>
    Fine,

    /// <summary>A favorite or typed region was asked for and the agent is somewhere else.</summary>
    MovedFromPlace,

    /// <summary>Home was asked for and the grid did not start the agent there.</summary>
    MovedFromHome,

    /// <summary>The last location was asked for and the grid did not start the agent there.</summary>
    MovedFromLast,
}

/// <summary>What <see cref="LoginStart.Resolve"/> decided.</summary>
/// <param name="LoginString">The value for the login request's <c>start</c>: <c>last</c>, <c>home</c> or
/// <c>uri:Region&amp;x&amp;y&amp;z</c>.</param>
/// <param name="Effective">The choice actually used (what to remember after a successful login).</param>
/// <param name="Fallback">Why <paramref name="Effective"/> differs from what was asked, if it does.</param>
/// <param name="Target">The place, for a favorite or a typed region; null for last/home.</param>
public sealed record StartResolution(string LoginString, StartChoice Effective, StartFallback Fallback, StartTarget? Target);

/// <summary>
/// FEAT-UI-72: turns the "Start at" choice into the login request's <c>start</c> value, parses typed regions and
/// SLURLs, and holds the reference viewer's rules for judging whether the grid honoured the request.
/// </summary>
public static class LoginStart
{
    /// <summary>The middle of a region and ground level -- what the viewer assumes for a region typed with no
    /// coordinates (<c>llslurl.cpp:318</c>).</summary>
    private const int DefaultX = 128, DefaultY = 128, DefaultZ = 0;

    /// <summary>How far, in metres on X and Y, the agent may be from the requested spot before the grid is
    /// said to have put it elsewhere (<c>llstartup.cpp:3397-3405</c>, <c>dispatchURL</c>). Z is ignored: the
    /// grid may adjust height, landing point and ground.</summary>
    public const double PositionSlopMetres = 2.0;

    private static readonly Regex WebSlurl = new(
        @"^https?://[^/]+/(?:secondlife|region)/(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Decides what to send. <c>Last</c> and <c>Home</c> pass through; a favorite present in
    /// <paramref name="favorites"/> becomes its <c>uri:</c> string; a favorite that is gone falls back to
    /// <c>last</c> (<see cref="StartFallback.FavoriteMissing"/>); a typed region is parsed with
    /// <see cref="TryParseTyped"/> and an invalid one falls back to <c>last</c> (<see cref="StartFallback.RegionInvalid"/>).
    /// </summary>
    /// <param name="maxXY">X/Y limit for typed text (<see cref="StartTarget.LindenMaxXY"/> on a Linden grid).</param>
    public static StartResolution Resolve(StartChoice choice, IReadOnlyList<LoginFavorite> favorites, int maxXY)
    {
        if (choice.Kind == StartKind.Home)
            return new StartResolution("home", StartChoice.Home, StartFallback.None, null);

        if (choice.Kind == StartKind.Favorite)
        {
            Guid asset = choice.FavoriteAssetId;
            foreach (var f in favorites)
            {
                if (f.AssetId == asset && asset != Guid.Empty)
                    return new StartResolution(f.Target.ToLoginString(), choice, StartFallback.None, f.Target);
            }
            return new StartResolution("last", StartChoice.Last, StartFallback.FavoriteMissing, null);
        }

        if (choice.Kind == StartKind.Region)
        {
            if (TryParseTyped(choice.Key, maxXY, out var target))
                return new StartResolution(target.ToLoginString(), choice, StartFallback.None, target);
            return new StartResolution("last", StartChoice.Last, StartFallback.RegionInvalid, null);
        }

        return new StartResolution("last", StartChoice.Last, StartFallback.None, null);
    }

    /// <summary>
    /// Reads text typed (or pasted) into the "Region..." field. Accepted, with the region percent-decoded,
    /// whitespace trimmed, one leading <c>/</c> and one trailing <c>/</c> ignored:
    /// <c>Region</c>, <c>Region/x/y</c>, <c>Region/x/y/z</c>, <c>secondlife://Region/x/y/z</c>,
    /// <c>http(s)://host/(secondlife|region)/Region/x/y/z</c> and <c>uri:Region&amp;x&amp;y&amp;z</c>. Coordinates
    /// may be decimals (rounded); missing ones are the middle of the region at ground level. Anything else --
    /// more than four path parts, a lone coordinate, a non-numeric or out-of-range coordinate, an empty region
    /// name, an <c>&amp;</c> in it -- is not a location. The grid part of a pasted SLURL is ignored: the region is
    /// looked up on the grid selected in the profile. Hypergrid addresses are not supported.
    /// </summary>
    public static bool TryParseTyped(string? text, int maxXY, [NotNullWhen(true)] out StartTarget? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();

        if (s.StartsWith("uri:", StringComparison.OrdinalIgnoreCase))
            return TryParseLoginForm(s[4..], maxXY, out target);

        const string scheme = "secondlife://";
        if (s.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            s = s[scheme.Length..];
        }
        else if (s.Contains("://", StringComparison.Ordinal))
        {
            Match m;
            try { m = WebSlurl.Match(s); }
            catch (RegexMatchTimeoutException) { return false; }
            if (!m.Success) return false;
            s = m.Groups["rest"].Value;
        }

        if (s.StartsWith('/')) s = s[1..];
        if (s.EndsWith('/')) s = s[..^1];
        if (s.Length == 0) return false;

        string[] parts = s.Split('/');
        if (parts.Length is 2 or > 4) return false;

        string region = Uri.UnescapeDataString(parts[0]);
        double x = DefaultX, y = DefaultY, z = DefaultZ;
        if (parts.Length >= 3)
        {
            if (!TryCoordinate(parts[1], out x) || !TryCoordinate(parts[2], out y)) return false;
            if (parts.Length == 4 && !TryCoordinate(parts[3], out z)) return false;
        }

        return StartTarget.TryCreate(region, x, y, z, maxXY, out target);
    }

    /// <summary>The login service's own form after the <c>uri:</c> prefix: <c>Region&amp;x&amp;y&amp;z</c>.</summary>
    private static bool TryParseLoginForm(string rest, int maxXY, [NotNullWhen(true)] out StartTarget? target)
    {
        target = null;
        string[] parts = rest.Split('&');
        if (parts.Length != 4) return false;
        if (!TryCoordinate(parts[1], out double x) || !TryCoordinate(parts[2], out double y)
            || !TryCoordinate(parts[3], out double z)) return false;
        return StartTarget.TryCreate(parts[0], x, y, z, maxXY, out target);
    }

    /// <summary>A plain decimal number: optional sign, digits, optional fraction. No exponent, no thousands
    /// separators, no <c>NaN</c>/<c>Infinity</c> (which <c>double.Parse</c> would take).</summary>
    private static bool TryCoordinate(string text, out double value)
    {
        value = 0;
        string t = text.Trim();
        if (t.Length == 0) return false;
        return double.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                   CultureInfo.InvariantCulture, out value)
               && double.IsFinite(value);
    }

    /// <summary>
    /// The reference viewer's test of whether the login reply honours the request (<c>llstartup.cpp:2354-2356</c>):
    /// a place (favorite or typed region) wants <c>"url"</c>, last wants <c>"last"</c>, home wants <c>"home"</c>.
    /// Returns <c>null</c> when the reply carries no <c>start_location</c> at all (OpenSim may send none), so
    /// the caller can fall back to comparing the region it landed in.
    /// </summary>
    public static bool? HonoursStart(StartKind asked, string? replyStartLocation)
    {
        if (string.IsNullOrWhiteSpace(replyStartLocation)) return null;
        string reply = replyStartLocation.Trim();
        string expected = asked switch
        {
            StartKind.Home => "home",
            StartKind.Last => "last",
            _ => "url",
        };
        return string.Equals(reply, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the grid started the agent where it was asked to, by the reference viewer's rules. All of it is judged
    /// from facts known on arrival: the reply's <c>start_location</c>, whether this is the account's first login, and where
    /// the agent stands.
    /// <list type="number">
    /// <item>A first login is never reported (the viewer is silent then, <c>llstartup.cpp:2348</c>).</item>
    /// <item>Last or Home: reported when the reply says something other than what was asked. A reply with no
    /// <c>start_location</c> is unknown, so nothing is said.</item>
    /// <item>A favorite or typed region (<paramref name="requested"/>): the reply must say <c>url</c>. When it says
    /// otherwise the agent is judged by <see cref="NeedsMove"/> -- a different region, or more than 2 m away on X or Y --
    /// so a grid that merely adjusted height or landing point is not reported. When the reply carries no
    /// <c>start_location</c> at all (OpenSim may send none), only the region's name is compared.</item>
    /// </list>
    /// </summary>
    /// <param name="arrivedX">The agent's region-local X, or null when it is not known yet.</param>
    /// <param name="arrivedY">The agent's region-local Y, or null when it is not known yet.</param>
    public static LandingVerdict JudgeLanding(StartKind asked, StartTarget? requested, string? replyStartLocation,
        bool firstLogin, string? arrivedRegion, double? arrivedX, double? arrivedY)
    {
        if (firstLogin) return LandingVerdict.Fine;

        bool? honoured = HonoursStart(asked, replyStartLocation);

        switch (asked)
        {
            case StartKind.Last:
                return honoured == false ? LandingVerdict.MovedFromLast : LandingVerdict.Fine;

            case StartKind.Home:
                return honoured == false ? LandingVerdict.MovedFromHome : LandingVerdict.Fine;

            default:
                if (requested == null || honoured == true) return LandingVerdict.Fine;

                bool sameRegion = string.Equals(requested.Region, arrivedRegion?.Trim(), StringComparison.OrdinalIgnoreCase);
                if (honoured == null)
                    return sameRegion ? LandingVerdict.Fine : LandingVerdict.MovedFromPlace;

                // The reply says it was not honoured: only a real difference in place counts.
                if (!sameRegion) return LandingVerdict.MovedFromPlace;
                if (arrivedX is not { } x || arrivedY is not { } y) return LandingVerdict.Fine; // same region, position unknown
                return NeedsMove(requested, arrivedRegion, x, y) ? LandingVerdict.MovedFromPlace : LandingVerdict.Fine;
        }
    }

    /// <summary>
    /// Whether the agent is somewhere other than the requested place: a different region (name compared
    /// ignoring case), or more than <see cref="PositionSlopMetres"/> away on X or Y. Z is ignored.
    /// </summary>
    public static bool NeedsMove(StartTarget requested, string? arrivedRegion, double arrivedX, double arrivedY)
    {
        if (!string.Equals(requested.Region, arrivedRegion?.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        return Math.Abs(arrivedX - requested.X) > PositionSlopMetres
            || Math.Abs(arrivedY - requested.Y) > PositionSlopMetres;
    }
}
