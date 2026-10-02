using System.Globalization;
using System.Xml.Linq;

namespace SLNG.Core.ChatLogs;

/// <summary>
/// FEAT-UI-41: the grid LABEL Firestorm puts after the account name in a folder
/// (<c>clifton_howlett.osgrid</c>). It is the grid's <c>gridname</c> -- NOT its nick -- as
/// <c>fsgridhandler.cpp</c> stores it (<c>GRID_LABEL_VALUE = gridname</c> from <c>get_grid_info</c>)
/// and <c>llstartup.cpp</c> passes <c>getGridLabel()</c> to <c>setPerAccountChatLogsDir</c>.
///
/// <para>SLNG knows only the login URI, so it finds the label the way the viewer found it, in order:
/// (1) the two Linden grids by host (<c>Second Life</c>, <c>Second Life Beta</c> -- the names in
/// Firestorm's own <c>grids.remote.xml</c>); (2) Firestorm's own grid list
/// (<c>user_settings/grids.user.xml</c> and <c>grids.remote.xml</c>) when it has an entry whose
/// login URI, gatekeeper or name is this host and port -- exact, offline, and what the folder was
/// really named; (3) the grid's own <c>get_grid_info</c> answer, fetched once at login
/// (<see cref="ParseGridInfoName"/>); (4) the host and port, which is what the viewer itself falls
/// back to when a grid reports no name (<c>"No gridname found in grid info, setting to"</c>).
/// Only (4) can be wrong for a grid Firestorm knows, and only when both (2) and (3) are unavailable.</para>
/// </summary>
public static class GridLabels
{
    /// <summary>The label for a Linden grid's login URI, or null when it is not one.</summary>
    public static string? LindenLabel(string? loginUri)
        => GridIdentity.Slug(loginUri) switch
        {
            "agni" => FirestormLogLayout.SecondLifeLabel,
            "aditi" => FirestormLogLayout.SecondLifeBetaLabel,
            _ => null,
        };

    /// <summary>The label from Firestorm's grid lists (LLSD XML texts, any number), or null.</summary>
    public static string? FindInGridLists(string? loginUri, IEnumerable<string?> gridListXml)
    {
        string? want = HostKey(loginUri);
        if (want is null) return null;

        foreach (string? xml in gridListXml)
        {
            if (string.IsNullOrWhiteSpace(xml)) continue;

            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch (System.Xml.XmlException) { continue; }

            XElement? top = doc.Root?.Element("map");
            if (top is null) continue;

            foreach (XElement key in top.Elements("key"))
            {
                if (key.NextNode is not XElement { Name.LocalName: "map" } entry) continue;

                string? label = null;
                var hosts = new List<string?> { HostKey(key.Value) };
                foreach (XElement k in entry.Elements("key"))
                {
                    if (k.NextNode is not XElement v) continue;
                    switch (k.Value)
                    {
                        case "gridname": label = v.Value.Trim(); break;
                        case "gatekeeper": hosts.Add(HostKey(v.Value)); break;
                        case "name": hosts.Add(HostKey(v.Value)); break;
                        case "loginuri":
                            foreach (XElement s in v.Elements("string")) hosts.Add(HostKey(s.Value));
                            break;
                    }
                }

                if (!string.IsNullOrEmpty(label) && hosts.Contains(want)) return label;
            }
        }
        return null;
    }

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

    /// <summary>True when the label cannot be had without asking the grid: it is not a Linden grid
    /// and Firestorm's lists have no entry for it.</summary>
    public static bool NeedsProbe(string? loginUri, IEnumerable<string?> gridListXml)
        => LindenLabel(loginUri) is null && FindInGridLists(loginUri, gridListXml) is null;

    /// <summary>The label to use, by the order in the class remarks. <paramref name="probedName"/> is
    /// the <c>gridname</c> the grid itself reported, or null if it was not asked or did not answer.</summary>
    public static string Resolve(string? loginUri, IEnumerable<string?> gridListXml, string? probedName)
    {
        string? label = LindenLabel(loginUri)
                        ?? FindInGridLists(loginUri, gridListXml)
                        ?? (string.IsNullOrWhiteSpace(probedName) ? null : probedName.Trim());
        return label ?? FallbackLabel(loginUri);
    }

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
    /// http(s). Never used for a Linden grid.</summary>
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
    /// loopback spellings (<c>127.0.0.1</c>, <c>::1</c>, <c>localhost</c>) as one -- Firestorm's list
    /// says <c>localhost:9000</c> where SLNG's login box says <c>127.0.0.1:9000</c>. Null when it is
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
