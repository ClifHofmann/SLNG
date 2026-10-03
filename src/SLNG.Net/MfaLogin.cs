namespace SLNG.Net;

/// <summary>What to do with a profile's saved <c>mfa_hash</c> after a login attempt.</summary>
public enum MfaHashAction
{
    /// <summary>Leave whatever is stored exactly as it is.</summary>
    Keep,

    /// <summary>Write the hash the grid just returned.</summary>
    Store,

    /// <summary>Delete the stored hash.</summary>
    Erase,
}

/// <summary>
/// FEAT-SL-02: the pure rules of the multi-factor login, kept apart from the network and the UI so
/// they can be tested without either. The wire behaviour they encode is the reference viewer's
/// (<c>lllogininstance.cpp</c>, <c>llstartup.cpp</c>); the full account with line numbers is in
/// <c>docs/specs/FEAT-SL-02-mfa-login.md</c>.
/// </summary>
public static class MfaLogin
{
    /// <summary>Digits in a Second Life authenticator code (a standard 6-digit TOTP).</summary>
    public const int TokenLength = 6;

    /// <summary>Upper bound for a remembered hash we are willing to write to disk. The real value is a
    /// short opaque string; anything far beyond that, or containing a line break, is not something to
    /// put in a config file that is read back line by line.</summary>
    public const int MaxHashLength = 512;

    /// <summary>The code with every whitespace character removed. People paste "123 456" from their
    /// authenticator app; the reference viewer strips whitespace the same way
    /// (<c>lllogininstance.cpp:536</c>, "SL-17034").</summary>
    public static string NormalizeToken(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (char c in raw)
            if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>True when <paramref name="raw"/>, once normalised, is exactly six ASCII digits. The
    /// reference viewer sends any non-empty string and lets the grid judge it; the window insists on
    /// the six digits instead, so a mistyped letter never costs a round trip -- and, because a code is
    /// good for one use, never burns the one the person has.</summary>
    public static bool IsPlausibleToken(string? raw)
    {
        string t = NormalizeToken(raw);
        if (t.Length != TokenLength) return false;
        foreach (char c in t)
            if (c < '0' || c > '9') return false;
        return true;
    }

    /// <summary>True when the grid's <c>reason</c> asks for a code.</summary>
    public static bool IsChallenge(string? errorKey) =>
        string.Equals(errorKey, LoginResult.MfaChallengeErrorKey, StringComparison.Ordinal);

    /// <summary>Whether an <c>mfa_challenge</c> means "that code was wrong or too old". It does when
    /// this attempt SENT a code: the grid then asked again. The wire carries no separate reason for it
    /// -- the reference viewer re-shows the same prompt for every challenge
    /// (<c>lllogininstance.cpp:409-418</c>) -- so the difference is known only from what was sent.
    /// A challenge in answer to no code (or to a remembered hash the grid no longer accepts) is simply
    /// the first ask.</summary>
    public static bool IsRejectedCode(bool attemptSentToken, string? errorKey) =>
        attemptSentToken && IsChallenge(errorKey);

    /// <summary>True when <paramref name="hash"/> may be kept: non-empty, no longer than
    /// <see cref="MaxHashLength"/>, no control characters. A hash that fails this is treated as if the
    /// grid had sent none.</summary>
    public static bool IsStorableHash(string? hash)
    {
        if (string.IsNullOrEmpty(hash) || hash.Length > MaxHashLength) return false;
        foreach (char c in hash)
            if (char.IsControl(c)) return false;
        return true;
    }

    /// <summary>The hash worth keeping from <paramref name="grid"/>, or null.</summary>
    public static string? StorableHashOrNull(string? grid) => IsStorableHash(grid) ? grid : null;

    /// <summary>What to do with the stored hash after a SUCCESSFUL login.
    ///
    /// <para>The reference viewer keeps the hash only when the person chose to be remembered and its
    /// "remember user" option is on (<c>lllogininstance.cpp:557</c>), and deletes it when the person
    /// answered a challenge without ticking "remember this computer" (<c>:561</c>). On top of that,
    /// this drops a hash the grid rejected: if the attempt was challenged, the stored value is stale,
    /// and unless a fresh one came back with the answer it must go.</para></summary>
    /// <param name="saveLogin">The "Save Login" box. Off means keep nothing about this profile.</param>
    /// <param name="wasChallenged">The grid asked for a code during this login.</param>
    /// <param name="remember">The person ticked "remember this computer" (only meaningful when
    /// <paramref name="wasChallenged"/>; when no challenge happened the stored hash stayed valid and
    /// the viewer's default, remember, applies).</param>
    /// <param name="returnedHash">The <c>mfa_hash</c> of the success response, or null.</param>
    public static MfaHashAction DecideStorage(bool saveLogin, bool wasChallenged, bool remember, string? returnedHash)
    {
        if (!saveLogin) return MfaHashAction.Erase;
        bool fresh = IsStorableHash(returnedHash);
        if (wasChallenged)
            return remember && fresh ? MfaHashAction.Store : MfaHashAction.Erase;
        return fresh ? MfaHashAction.Store : MfaHashAction.Keep;
    }
}
