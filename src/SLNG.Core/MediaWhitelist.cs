using System.Text.RegularExpressions;

namespace SLNG.Core;

/// <summary>Ports <c>LLMediaEntry::checkUrlAgainstWhitelist</c> (llmediaentry.cpp:474-511) and its
/// <c>pattern_match</c> helper (:443-459) byte-for-byte in behaviour, verified against the real
/// secondlife/viewer source, not guessed from the SL wiki's informal description of "wildcards".
///
/// Each whitelist entry is matched against the candidate URL's scheme, authority (host[:port])
/// and path INDEPENDENTLY -- all three must match for that entry to pass. A pattern is a glob
/// with '*' as the only wildcard, matched case-insensitively as a FULL match (not a substring).
/// An entry with no scheme (no "xxx://") always passes the scheme check (the viewer computes
/// scheme_passes from the filter's OWN empty scheme -- pattern_match("", "") is unconditionally
/// true -- BEFORE defaulting the filter to "https://" for authority/path parsing; that default
/// never feeds back into scheme_passes). An empty whitelist means "no restriction" (SL convention:
/// the whitelist only takes effect when <see cref="MediaFace.EnableWhiteList"/> is also set).</summary>
public static class MediaWhitelist
{
    private const string DefaultUrlPrefix = "https://";

    public static bool IsAllowed(string candidateUrl, IReadOnlyList<string>? whitelist)
    {
        if (whitelist == null || whitelist.Count == 0) return true;
        if (!Uri.TryCreate(candidateUrl, UriKind.Absolute, out var candidate)) return false;

        foreach (var filter in whitelist)
        {
            var (filterScheme, _, _) = Split(filter);
            bool schemePasses = PatternMatch(candidate.Scheme, filterScheme);

            var effective = filterScheme.Length == 0 ? DefaultUrlPrefix + filter : filter;
            var (_, filterAuthority, filterPath) = Split(effective);

            bool authorityPasses = PatternMatch(candidate.Authority, filterAuthority);
            bool pathPasses = PatternMatch(candidate.AbsolutePath, filterPath);

            if (schemePasses && authorityPasses && pathPasses) return true;
        }
        return false;
    }

    /// <summary>A minimal split matching LLURI's own permissive parsing (scheme up to the first
    /// "://", authority up to the next '/', path is the rest) -- NOT <see cref="Uri"/>, which
    /// rejects '*' in several of its strict-mode positions and would throw on most real filter
    /// patterns (e.g. "*.example.com/*").</summary>
    private static (string Scheme, string Authority, string Path) Split(string uri)
    {
        string scheme = "";
        string rest = uri;
        int schemeEnd = uri.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            scheme = uri[..schemeEnd];
            rest = uri[(schemeEnd + 3)..];
        }

        int pathStart = rest.IndexOf('/');
        string authority = pathStart >= 0 ? rest[..pathStart] : rest;
        string path = pathStart >= 0 ? rest[pathStart..] : "";
        return (scheme, authority, path);
    }

    /// <summary>An empty pattern matches anything (llmediaentry.cpp:446); otherwise every regex
    /// metacharacter is escaped and '*' alone is restored to '.*', then matched full-string,
    /// case-insensitive -- exactly <c>pattern_match</c>'s escape-then-un-escape-the-star trick.</summary>
    private static bool PatternMatch(string candidate, string pattern)
    {
        if (pattern.Length == 0) return true;
        string escaped = Regex.Escape(pattern).Replace("\\*", ".*");
        return Regex.IsMatch(candidate, $"^{escaped}$", RegexOptions.IgnoreCase);
    }
}
