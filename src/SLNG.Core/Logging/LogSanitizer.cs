using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SLNG.Core.Logging;

/// <summary>
/// FEAT-UI-76: turns a log line into one that can be shared without handing over who the person is, who they talk to,
/// where they are or how to get into their account. Used for the opt-in debug log only; the engine's own
/// <c>godot.log</c> is not touched.
///
/// <para><b>What goes, and what it becomes.</b> Identifiers are not removed but replaced by a short stable tag
/// (<c>&lt;id-3fa91c&gt;</c>, <c>&lt;name-b04e11&gt;</c>, <c>&lt;region-7c2d90&gt;</c>), the SHA-256 of the value with a salt
/// that exists only in this process: two lines about the same avatar still read as the same avatar, which is what a
/// bug hunt needs, but the tag cannot be turned back into the value and does not match the one in another session's log.
/// Passwords, tokens and session ids become <c>&lt;secret&gt;</c>; the path of a web address after its host, e-mail
/// addresses, IP addresses, region handles and the user name inside a home-folder path are replaced by a plain marker.</para>
///
/// <para><b>What it cannot know.</b> Free text typed into the world -- an object's or a parcel's name -- has no shape to
/// recognise. Avatar names and region names are caught because the client registers the ones it learns
/// (<see cref="SetTermSource"/>, <see cref="AddTerm"/>). The client does not write chat or instant-message text to any
/// log in the first place; this class is the second line, not the first.</para>
///
/// <para>Thread-safe: log lines arrive from every thread.</para>
/// </summary>
public sealed class LogSanitizer
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly Regex Secret = new(
        @"(?<key>\b(?:password|passwd|pwd|passphrase|secret|token|session[_-]?id|secure[_-]?session[_-]?id|circuit[_-]?code|authorization|cookie|api[_-]?key|mfa[_-]?hash)\b[""']?\s*[:=]\s*)(?<value>(?:bearer\s+)?(?:""[^""]*""|'[^']*'|\S+))",
        Options | RegexOptions.IgnoreCase, MatchTimeout);

    private static readonly Regex Bearer = new(@"\bbearer\s+\S+", Options | RegexOptions.IgnoreCase, MatchTimeout);

    private static readonly Regex Url = new(
        @"(?<scheme>\b(?:https?|wss?|ftp|secondlife|hop)://)(?<host>[^\s/:?#""'<>]+)(?<port>:\d+)?(?<rest>[/?#][^\s""'<>]*)?",
        Options | RegexOptions.IgnoreCase, MatchTimeout);

    private static readonly Regex Email = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+", Options, MatchTimeout);

    private static readonly Regex Uuid = new(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", Options, MatchTimeout);

    private static readonly Regex IPv4 = new(
        @"(?<![\w.])(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?::\d{1,5})?(?![\w.])", Options, MatchTimeout);

    // Only the forms that cannot be mistaken for a clock time: eight groups, or a "::" somewhere.
    private static readonly Regex IPv6 = new(
        @"(?<![\w:])(?:(?:[0-9a-fA-F]{1,4}:){7}[0-9a-fA-F]{1,4}|(?:[0-9a-fA-F]{1,4}:){1,7}:(?:[0-9a-fA-F]{1,4}(?::[0-9a-fA-F]{1,4}){0,6})?|::(?:[0-9a-fA-F]{1,4}:){0,6}[0-9a-fA-F]{1,4})(?![\w:])",
        Options, MatchTimeout);

    private static readonly Regex HomePathWindows = new(
        @"(?<root>\b[A-Za-z]:[\\/]Users[\\/])[^\\/\s""'<>|]+", Options | RegexOptions.IgnoreCase, MatchTimeout);

    private static readonly Regex HomePathUnix = new(@"(?<root>/(?:home|Users)/)[^/\s""']+", Options, MatchTimeout);

    // A region handle is a 64-bit number; nothing else in a log line has 15 to 20 digits in a row.
    private static readonly Regex Handle = new(@"(?<![\d.])\d{15,20}(?![\d.])", Options, MatchTimeout);

    private readonly byte[] _salt;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _terms = new(StringComparer.OrdinalIgnoreCase); // term -> kind
    private readonly Dictionary<string, (string Kind, Func<IEnumerable<string>?> Source)> _sources = new();
    private Regex? _termRegex;
    private long _lastRefreshTicks = -1; // -1: never read
    private volatile bool _hasSources;

    /// <summary>How often <see cref="SetTermSource"/> sources are read again while lines flow. A name learned a moment
    /// ago can appear in a line within this time; <see cref="AddTerm"/> has no such gap.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The sanitizer the client's debug log uses, with a salt that lives as long as the process.</summary>
    public static LogSanitizer Shared { get; } = new();

    public LogSanitizer(byte[]? salt = null)
    {
        _salt = salt ?? RandomNumberGenerator.GetBytes(16);
    }

    /// <summary>Registers one string to be replaced wherever it appears (an avatar's name, a region's name).
    /// Matching ignores case and only counts whole words. Anything under three characters is ignored: it would
    /// shred every line that merely contains it.</summary>
    public void AddTerm(string term, string kind = "name")
    {
        if (string.IsNullOrWhiteSpace(term)) return;
        term = term.Trim();
        if (term.Length < 3) return;
        lock (_gate)
        {
            if (_terms.TryGetValue(term, out var existing) && existing == kind) return;
            _terms[term] = kind;
            _termRegex = null;
        }
    }

    /// <summary>Registers a list that grows while the client runs (the names it has met). Read again at most every
    /// <see cref="RefreshInterval"/>; a second call with the same <paramref name="key"/> replaces the first, so a new
    /// login does not stack sources up.</summary>
    public void SetTermSource(string key, string kind, Func<IEnumerable<string>?> source)
    {
        lock (_gate)
        {
            _sources[key] = (kind, source);
            _hasSources = true;
            _lastRefreshTicks = -1;
        }
    }

    /// <summary>Reads every registered source now.</summary>
    public void RefreshSources()
    {
        List<(string Kind, Func<IEnumerable<string>?> Source)> sources;
        lock (_gate) sources = _sources.Values.ToList();

        bool changed = false;
        var found = new List<(string Term, string Kind)>();
        foreach (var (kind, source) in sources)
        {
            IEnumerable<string>? values;
            try { values = source(); }
            catch { continue; } // a source racing with the thing it reads must never break logging
            if (values == null) continue;
            foreach (var v in values)
                if (!string.IsNullOrWhiteSpace(v) && v.Trim().Length >= 3) found.Add((v.Trim(), kind));
        }

        lock (_gate)
        {
            foreach (var (term, kind) in found)
            {
                if (_terms.TryGetValue(term, out var existing) && existing == kind) continue;
                _terms[term] = kind;
                changed = true;
            }
            if (changed) _termRegex = null;
            _lastRefreshTicks = Environment.TickCount64;
        }
    }

    /// <summary>Returns the line with everything identifying replaced; a line with nothing to hide comes back unchanged.</summary>
    public string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        try
        {
            if (_hasSources && IsRefreshDue())
                RefreshSources();

            text = ReplaceTerms(text);
            // The web address first: its path and query (capability tokens, session ids) go as a whole.
            text = Url.Replace(text, UrlEvaluator);
            text = Secret.Replace(text, "${key}<secret>");
            text = Bearer.Replace(text, "Bearer <secret>");
            text = Email.Replace(text, "<email>");
            text = Uuid.Replace(text, m => Tag("id", m.Value));
            text = IPv6.Replace(text, "<ip>");
            text = IPv4.Replace(text, "<ip>");
            text = HomePathWindows.Replace(text, "${root}<user>");
            text = HomePathUnix.Replace(text, "${root}<user>");
            text = Handle.Replace(text, "<handle>");
            return text;
        }
        catch (RegexMatchTimeoutException)
        {
            // A line the rules could not finish in time is not worth the risk of passing it on half-cleaned.
            return "<line withheld: too complex to sanitize>";
        }
    }

    private bool IsRefreshDue()
    {
        long last = Volatile.Read(ref _lastRefreshTicks);
        return last < 0 || Environment.TickCount64 - last >= RefreshInterval.TotalMilliseconds;
    }

    private string ReplaceTerms(string text)
    {
        var regex = TermRegex();
        if (regex == null) return text;
        return regex.Replace(text, m =>
        {
            string kind;
            lock (_gate) kind = _terms.TryGetValue(m.Value, out var k) ? k : "name";
            return Tag(kind, m.Value.ToLowerInvariant());
        });
    }

    private Regex? TermRegex()
    {
        lock (_gate)
        {
            if (_termRegex != null) return _termRegex;
            if (_terms.Count == 0) return null;
            // Longest first, so "Denise Resident" is taken whole before a shorter registered term inside it.
            var parts = _terms.Keys.OrderByDescending(t => t.Length).Select(Regex.Escape);
            _termRegex = new Regex(
                @"(?<![\p{L}\p{N}])(?:" + string.Join("|", parts) + @")(?![\p{L}\p{N}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
            return _termRegex;
        }
    }

    private string UrlEvaluator(Match m)
    {
        string host = m.Groups["host"].Value;
        // A bare address is as identifying as one in prose; a name is infrastructure and worth keeping for diagnosis.
        bool bare = IPv4.IsMatch(host);
        string rest = m.Groups["rest"].Success && m.Groups["rest"].Value.Length > 1 ? "/<redacted>" : m.Groups["rest"].Value;
        return m.Groups["scheme"].Value + (bare ? "<ip>" : host + m.Groups["port"].Value) + rest;
    }

    private string Tag(string kind, string value)
    {
        byte[] hash = SHA256.HashData(_salt.Concat(Encoding.UTF8.GetBytes(value)).ToArray());
        return $"<{kind}-{Convert.ToHexString(hash, 0, 3).ToLowerInvariant()}>";
    }
}
