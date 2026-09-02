using SLNG.Core;

namespace SLNG.Net;

/// <summary>Wire encoding for <see cref="MaturityLevel"/>: the short codes SL's login response
/// and the <c>UpdateAgentInformation</c> capability both use ("PG"/"M"/"A"), matched exactly
/// against the reference viewer (<c>LLViewerRegion::accessToShortString</c> /
/// <c>shortStringToAccess</c>, <c>LLAgentAccess::convertTextToMaturity</c> -- the latter reads
/// only the FIRST character of a field, which is why <see cref="FromShortString"/> does too:
/// <c>agent_access_max</c> arrives as the 2-letter "PG", not "P").</summary>
internal static class MaturityAccess
{
    /// <summary>Encodes for the <c>access_prefs.max</c> field of an <c>UpdateAgentInformation</c>
    /// POST.</summary>
    internal static string ToShortString(MaturityLevel level) => level switch
    {
        MaturityLevel.Adult => "A",
        MaturityLevel.Moderate => "M",
        _ => "PG",
    };

    /// <summary>Decodes a login-response field (<c>agent_access_max</c>, <c>agent_region_access</c>)
    /// or a capability response's <c>access_prefs.max</c>. Empty, missing, or unrecognised input
    /// is <see cref="MaturityLevel.General"/> -- the same fallback <c>LLAgentAccess::
    /// convertTextToMaturity</c> uses for anything that isn't 'A', 'M', or 'P'.</summary>
    internal static MaturityLevel FromShortString(string? code)
    {
        if (string.IsNullOrEmpty(code)) return MaturityLevel.General;
        return char.ToUpperInvariant(code[0]) switch
        {
            'A' => MaturityLevel.Adult,
            'M' => MaturityLevel.Moderate,
            _ => MaturityLevel.General,
        };
    }
}
