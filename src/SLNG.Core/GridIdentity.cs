using System.Security.Cryptography;
using System.Text;

namespace SLNG.Core;

/// <summary>
/// BUG-GRID-01: which grid a piece of data belongs to, as ONE filesystem-safe path segment derived
/// from the login URI -- the only thing every grid has that cannot be mistaken for another grid's.
///
/// <para>Why it exists: an account called "Clifton Howlett" exists on Second Life and on OSGrid at
/// the same time, so a store keyed by the account name alone (the login-screen background was)
/// serves one grid's data to the other. Region handles collide the same way -- Second Life and
/// OpenSim both place regions on the global grid coordinates, and OSGrid's default region sits at
/// 1000,1000, which is also a real Second Life region. Everything that is per grid goes under
/// <c>grids/&lt;slug&gt;/</c> (see <see cref="GridDataPaths"/>), so such a collision cannot happen
/// by construction instead of by somebody remembering to add the grid to a file name.</para>
///
/// <para><b>The rules.</b> The slug is a function of the host and the port; scheme, path, query,
/// user-info, letter case, a trailing dot and a default port are all ignored, so
/// <c>http://hg.osgrid.org/</c>, <c>https://HG.OSGRID.ORG/login</c> and <c>hg.osgrid.org:80</c> are
/// one grid. A Linden login host (<c>login.agni.lindenlab.com</c>) becomes its bare grid name
/// (<c>agni</c>), so the two Linden grids stay distinct and readable. Anything else is its host
/// (plus <c>~p&lt;port&gt;</c> when the port is not the scheme's default, so two OpenSim instances on
/// one machine stay apart).</para>
///
/// <para><b>Collision-free by construction, not by luck.</b> Characters outside <c>[a-z0-9.-]</c>
/// are written as <c>~</c> plus the hex of their UTF-8 bytes, and <c>~</c> itself is never passed
/// through, so the escaping is reversible and two different hosts cannot produce one slug. The
/// slug of a Linden grid is purely <c>[a-z0-9]</c>; a non-Linden host that would also be purely
/// <c>[a-z0-9]</c> (<c>localhost</c>, a LAN name, even one literally called <c>agni</c>) gets a
/// <c>~h</c> suffix, so it can never be mistaken for Second Life. A URI that cannot be read as a
/// host at all maps to <c>unknown~u&lt;hash&gt;</c> -- stable, never empty, never shared between two
/// different inputs. A slug is always exactly one path segment: never a separator, never
/// <c>.</c> or <c>..</c>, never a Windows device name, never longer than
/// <see cref="MaxSegmentLength"/>.</para>
///
/// <para>Two login URIs for the same grid (<c>login.osgrid.org</c> and <c>hg.osgrid.org</c>) are two
/// slugs. That costs a cache refill, never a wrong answer, which is the side to err on.</para>
/// </summary>
public static class GridIdentity
{
    /// <summary>Longest path segment this class returns. Well under the 255 every file system
    /// allows, and leaves room for the file name the caller puts inside.</summary>
    public const int MaxSegmentLength = 96;

    private const int TruncatedPrefixLength = 64;

    private static readonly string[] WindowsDeviceNames =
    {
        "con", "prn", "aux", "nul",
        "com0", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt0", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <summary>The path segment for the grid a login URI points at. Never null, never empty. See
    /// the class remarks for the rules.</summary>
    public static string Slug(string? loginUri)
    {
        string raw = loginUri?.Trim() ?? "";
        if (raw.Length == 0) return Unknown(raw);

        // A bare "hg.osgrid.org:80" is what a user types into the grid box; Uri needs a scheme to
        // read it as host:port instead of scheme:path.
        string candidate = raw.Contains("://", StringComparison.Ordinal) ? raw : "http://" + raw;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return Unknown(raw);
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return Unknown(raw);

        string host;
        try { host = uri.IdnHost.ToLowerInvariant().TrimEnd('.'); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return Unknown(raw); }
        if (host.Length == 0) return Unknown(raw);

        if (uri.IsDefaultPort && TryLindenGridName(host, out string linden)) return Limit(linden);

        string slug = EscapeHost(host);
        if (!uri.IsDefaultPort)
            slug += "~p" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        else if (IsPlainAlphanumeric(slug))
            slug += "~h"; // keeps "agni" (a host) apart from agni (the Linden grid)

        return Limit(AvoidDeviceName(slug));
    }

    /// <summary>The path segment for an account on a grid: <c>first_last</c>, lower case, so
    /// "Clifton Howlett" and "clifton howlett" are one account (grids treat names that way). Always
    /// non-empty and always contains the <c>_</c> separator -- which is what keeps an account
    /// directory from ever equal to <c>cache</c> beside it. Anything outside <c>[a-z0-9-]</c> is
    /// escaped as in <see cref="Slug"/>, so <c>a_b</c> + <c>c</c> and <c>a</c> + <c>b_c</c> differ.</summary>
    public static string AccountSlug(string? firstName, string? lastName)
        => Limit(Escape((firstName ?? "").Trim().ToLowerInvariant(), keepDot: false)
                 + "_" + Escape((lastName ?? "").Trim().ToLowerInvariant(), keepDot: false));

    /// <summary>Matches <c>login.&lt;name&gt;.lindenlab.com</c> and returns <c>&lt;name&gt;</c>, but
    /// only for a name that is purely <c>[a-z0-9]</c> -- the property that lets a Linden slug be told
    /// from every other slug without a lookup.</summary>
    private static bool TryLindenGridName(string host, out string name)
    {
        name = "";
        string[] labels = host.Split('.');
        if (labels.Length != 4 || labels[0] != "login" || labels[2] != "lindenlab" || labels[3] != "com")
            return false;
        if (labels[1].Length is 0 or > 16 || !IsPlainAlphanumeric(labels[1])) return false;

        name = labels[1];
        return true;
    }

    private static string EscapeHost(string host)
    {
        string escaped = Escape(host, keepDot: true);

        // "." and ".." mean something to a file system. A host cannot be either (Uri refuses), but a
        // path segment must never be them, whatever a future parser lets through.
        return escaped.StartsWith('.') ? "~2e" + escaped[1..] : escaped;
    }

    /// <summary>Windows refuses a file or directory whose name, before the first dot, is a device
    /// name (<c>con</c>, <c>nul.example.com</c>, ...). Escaping the first letter keeps the slug
    /// legal and distinct.</summary>
    private static string AvoidDeviceName(string slug)
    {
        int dot = slug.IndexOf('.');
        string firstComponent = dot < 0 ? slug : slug[..dot];
        return Array.IndexOf(WindowsDeviceNames, firstComponent) >= 0
            ? "~" + ((int)slug[0]).ToString("x2", System.Globalization.CultureInfo.InvariantCulture) + slug[1..]
            : slug;
    }

    /// <summary>Keeps <c>[a-z0-9-]</c> (and <c>.</c> when asked); writes every other character as
    /// <c>~</c> plus the lower-case hex of each of its UTF-8 bytes. The input is expected lower case.</summary>
    private static string Escape(string text, bool keepDot)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' || (keepDot && c == '.'))
            {
                sb.Append(c);
                continue;
            }

            foreach (byte b in Encoding.UTF8.GetBytes(c.ToString()))
                sb.Append('~').Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static bool IsPlainAlphanumeric(string text)
    {
        if (text.Length == 0) return false;
        foreach (char c in text)
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9'))) return false;
        return true;
    }

    /// <summary>Cuts an over-long segment to a prefix plus a hash of the whole, so the result is
    /// still deterministic and still different for different inputs. Hostile input only: no real
    /// host or name comes near the limit.</summary>
    private static string Limit(string segment)
    {
        if (segment.Length <= MaxSegmentLength) return segment;
        return segment[..TruncatedPrefixLength] + "~t" + HashSuffix(segment);
    }

    private static string Unknown(string raw) => "unknown~u" + HashSuffix(raw.ToLowerInvariant());

    private static string HashSuffix(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
