using System.Globalization;
using System.Xml.Linq;

namespace SLNG.Core.ChatLogs;

/// <summary>
/// FEAT-UI-41: the grid LABEL Firestorm puts after the account name in a folder
/// (<c>clifton_howlett.osgrid</c>). It is the grid's <c>gridname</c> -- NOT its nick -- as
/// <c>fsgridhandler.cpp</c> stores it (<c>GRID_LABEL_VALUE = gridname</c> from <c>get_grid_info</c>)
/// and <c>llstartup.cpp</c> passes <c>getGridLabel()</c> to <c>setPerAccountChatLogsDir</c>.
///
/// <para>SLNG finds it WITHOUT looking at any Firestorm file, from the login URI, in this order:
/// (1) a short built-in table of grids whose label is known -- the two Linden grids (<c>Second Life</c>,
/// <c>Second Life Beta</c>), OSGrid (<c>OSGrid</c>) and a local OpenSim on port 9000
/// (<c>localhost</c>, the label Firestorm's own default grid list gives it); (2) the grid's own
/// <c>get_grid_info</c> answer, fetched once at login (<see cref="ParseGridInfoName"/>), which is how
/// Firestorm learns the label of every other grid; (3) the host and port, the viewer's own fallback
/// when a grid reports no name. The built-in table is not a read of Firestorm's data: it is four
/// constants, each checked against the grid's own answer (OSGrid) or Firestorm's source (the others).</para>
/// </summary>
public static class GridLabels
{
    public const string OsGridLabel = "OSGrid";
    public const string LocalhostLabel = "localhost";

    /// <summary>The label for a login URI whose label is known without asking anybody, or null.</summary>
    public static string? BuiltInLabel(string? loginUri)
    {
        string? linden = GridIdentity.Slug(loginUri) switch
        {
            "agni" => FirestormLogLayout.SecondLifeLabel,
            "aditi" => FirestormLogLayout.SecondLifeBetaLabel,
            _ => null,
        };
        if (linden is not null) return linden;

        return HostKey(loginUri) switch
        {
            "hg.osgrid.org:80" or "login.osgrid.org:80" => OsGridLabel,
            "localhost:9000" => LocalhostLabel,
            _ => null,
        };
    }

    /// <summary>True for the two Linden grids (system lines are then sent by "Second Life", elsewhere by "Grid").</summary>
    public static bool IsLindenGrid(string? loginUri) => GridIdentity.Slug(loginUri) is "agni" or "aditi";

    /// <summary>The <c>&lt;gridname&gt;</c> of a <c>get_grid_info</c> answer, or null.</summary>
    public static string? ParseGridInfoName(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            string? name = XDocument.Parse(xml).Descendants("gridname").FirstOrDefault()?.Value.Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>True when the label cannot be had without asking the grid.</summary>
    public static bool NeedsProbe(string? loginUri) => BuiltInLabel(loginUri) is null;

    /// <summary>The label to use, by the order in the class remarks. <paramref name="probedName"/> is
    /// the <c>gridname</c> the grid itself reported, or null if it was not asked or did not answer.</summary>
    public static string Resolve(string? loginUri, string? probedName)
        => BuiltInLabel(loginUri)
           ?? (string.IsNullOrWhiteSpace(probedName) ? null : probedName.Trim())
           ?? FallbackLabel(loginUri);

    // The viewer's last resort is the grid text it was given, lower-cased. A default port is not part
    // of what a person types, so it is left off.
    private static string FallbackLabel(string? loginUri)
    {
        string? key = HostKey(loginUri);
        if (key is null) return "unknown";
        return key.EndsWith(":80", StringComparison.Ordinal) || key.EndsWith(":443", StringComparison.Ordinal)
            ? key[..key.LastIndexOf(':')]
            : key;
    }

    /// <summary>The URL <c>get_grid_info</c> lives at for a login URI -- the login URI with
    /// <c>get_grid_info</c> appended (<c>fsgridhandler.cpp</c>), or null for a URI that is not
    /// http(s). Never used for a grid with a built-in label.</summary>
    public static Uri? GridInfoUri(string? loginUri)
    {
        string raw = loginUri?.Trim() ?? "";
        if (raw.Length == 0) return null;
        string candidate = raw.Contains("://", StringComparison.Ordinal) ? raw : "http://" + raw;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

        string path = uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/";
        return new UriBuilder(uri) { Path = path + "get_grid_info", Query = "", Fragment = "" }.Uri;
    }

    /// <summary><c>host:port</c> lower-case with the scheme's default port filled in, and the
    /// loopback spellings (<c>127.0.0.1</c>, <c>::1</c>, <c>localhost</c>) as one. Null when it is
    /// not a host.</summary>
    internal static string? HostKey(string? text)
    {
        string raw = text?.Trim() ?? "";
        if (raw.Length == 0) return null;
        string candidate = raw.Contains("://", StringComparison.Ordinal) ? raw : "http://" + raw;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)) return null;

        string host;
        try { host = uri.IdnHost.ToLowerInvariant().TrimEnd('.'); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return null; }
        if (host.Length == 0) return null;

        if (host is "127.0.0.1" or "::1" or "[::1]") host = "localhost";
        return host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
    }
}
