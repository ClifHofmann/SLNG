using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace SLNG.Core.Login;

/// <summary>
/// FEAT-UI-72: a place to log in at -- a region by name and a region-local position -- in the one shape
/// the grid's login service takes: <c>uri:Region Name&amp;x&amp;y&amp;z</c> (<see cref="ToLoginString"/>).
///
/// <para><b>Why SLNG builds the string itself.</b> LibreMetaverse 3.1.6 runs any <c>Start</c> that is not
/// <c>home</c>/<c>last</c> through its own SLURL parser, which is lossy and unvalidated (no percent
/// decoding, non-numeric coordinates silently become <c>128/128/0</c>, garbage becomes
/// <c>uri:garbage&amp;128&amp;128&amp;0</c>). A string that is already <c>uri:Region&amp;x&amp;y&amp;z</c> passes
/// through it unchanged, so a canonical string built and checked here is what reaches the wire. The
/// <c>&amp;</c> is raw on purpose: LibreMetaverse's LLSD-XML serializer escapes it on the wire, and the
/// reference viewer's own comment says not to XML-escape it here (secondlife/viewer#2395).</para>
///
/// <para>Built only through <see cref="TryCreate"/>, so a <see cref="StartTarget"/> that exists has passed
/// the rules; the constructor stays public only because it is a record.</para>
/// </summary>
/// <param name="Region">The region name as the grid knows it: trimmed, no control characters, no
/// <c>&amp;</c>.</param>
/// <param name="X">Region-local X in whole metres.</param>
/// <param name="Y">Region-local Y in whole metres.</param>
/// <param name="Z">Region-local Z in whole metres.</param>
public sealed record StartTarget(string Region, int X, int Y, int Z)
{
    /// <summary>Longest region name accepted (the viewer's UI never produces one anywhere near this; it
    /// only stops a pasted blob from being sent to the login service).</summary>
    public const int MaxRegionNameLength = 255;

    /// <summary>The highest Z the reference viewer accepts in a SLURL (<c>llcommon/indra_constants.h</c>).</summary>
    public const int MaxZ = 4096;

    /// <summary>X and Y limit for text typed on a Linden grid: a standard region is 256 m wide, and the
    /// reference viewer rejects anything outside <c>0..256</c> (<c>llslurl.cpp:305</c>).</summary>
    public const int LindenMaxXY = 256;

    /// <summary>X and Y limit everywhere else, and for values read from a landmark: OpenSim
    /// "var-regions" are larger than 256 m.</summary>
    public const int OpenMaxXY = 4096;

    /// <summary>
    /// Checks and normalises a region name and position. Region: trimmed, control characters dropped,
    /// non-empty, at most <see cref="MaxRegionNameLength"/> characters, and no <c>&amp;</c> (the
    /// <c>uri:</c> form cannot express one, so it is refused rather than mangled). X, Y and Z: finite,
    /// rounded half away from zero (the viewer's <c>ll_round</c>), X and Y in <c>0..maxXY</c>, Z in
    /// <c>0..4096</c>.
    /// </summary>
    public static bool TryCreate(string? region, double x, double y, double z, int maxXY,
        [NotNullWhen(true)] out StartTarget? target)
    {
        target = null;
        if (region is null) return false;

        string name = CleanRegionName(region);
        if (name.Length == 0 || name.Length > MaxRegionNameLength) return false;
        if (name.Contains('&')) return false;

        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) return false;
        double rx = Math.Round(x, MidpointRounding.AwayFromZero);
        double ry = Math.Round(y, MidpointRounding.AwayFromZero);
        double rz = Math.Round(z, MidpointRounding.AwayFromZero);
        if (rx < 0 || rx > maxXY || ry < 0 || ry > maxXY || rz < 0 || rz > MaxZ) return false;

        target = new StartTarget(name, (int)rx, (int)ry, (int)rz);
        return true;
    }

    /// <summary>Trims the name and drops control characters (the viewer strips non-printables, <c>llslurl.cpp:296</c>).</summary>
    internal static string CleanRegionName(string region)
    {
        var chars = new char[region.Length];
        int n = 0;
        foreach (char c in region)
            if (!char.IsControl(c)) chars[n++] = c;
        return new string(chars, 0, n).Trim();
    }

    /// <summary><c>uri:{Region}&amp;{X}&amp;{Y}&amp;{Z}</c> -- raw <c>&amp;</c>, integers, the region name unescaped.</summary>
    public string ToLoginString() =>
        string.Create(CultureInfo.InvariantCulture, $"uri:{Region}&{X}&{Y}&{Z}");
}
