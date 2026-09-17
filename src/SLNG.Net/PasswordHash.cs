namespace SLNG.Net;

/// <summary>
/// FEAT-SEC-02: turns a plaintext account password into the <c>$1$&lt;md5&gt;</c> form the login
/// endpoint accepts, so nothing has to keep the plaintext.
///
/// <para><b>What this protects and what it does not.</b> The hash is login-equivalent: whoever
/// holds it can sign in. It is not account protection. What it removes is the <i>password</i> —
/// the thing a person reuses on other services, and the realistic harm in a viewer's saved-login
/// file sitting readable on disk. This is the same trade the reference viewers make; LL and
/// Firestorm store the hash, never the plaintext.</para>
///
/// <para><b>Why LibreMetaverse's own helper and not our own MD5.</b>
/// <c>Login.cs:962-964</c> converts the password itself unless it already looks hashed:
/// <code>
/// if (loginParams.Password.Length != 35 &amp;&amp; !loginParams.Password.StartsWith("$1$"))
///     loginParams.Password = Utils.MD5(loginParams.Password);
/// </code>
/// Reusing <c>Utils.MD5</c> makes a stored hash byte-identical to what the library would have
/// produced from the same plaintext, so switching to stored hashes changes nothing on the wire.
/// Rolling our own would not: LibreMetaverse hashes the <b>whole</b> UTF-8 string, while the LL
/// viewer truncates to the first 16 characters (<c>llsechandler_basic.cpp</c>). Which of the two
/// a given account's stored credential matches is not ours to guess — matching current behaviour
/// exactly is what keeps every already-working login working.</para>
///
/// <para>Verified against the pinned LibreMetaverse: <c>Utils.MD5("hunter2")</c> returns
/// <c>$1$2ab96390c7dbe3439de74d0c9b0b1767</c> (35 characters) in both 3.1.3 and 3.1.6, and the
/// passthrough condition above is unchanged between them. Pinned by
/// <c>SLNG.Net.Tests.PasswordHashTests</c>.</para>
/// </summary>
public static class PasswordHash
{
    /// <summary>The exact length LibreMetaverse's passthrough check requires: <c>"$1$"</c> plus a
    /// 32-character hex digest.</summary>
    private const int HashedLength = 35;

    private const string HashPrefix = "$1$";

    /// <summary>True when <paramref name="password"/> is already a hash the login endpoint will
    /// take as-is. Deliberately the same two conditions, in the same order, as
    /// <c>Login.cs:963</c> — if this ever disagrees with the library, a stored hash would be
    /// hashed a second time and every saved login would silently stop working.</summary>
    public static bool IsHashed(string? password) =>
        password is { Length: HashedLength } && password.StartsWith(HashPrefix, StringComparison.Ordinal);

    /// <summary>Hashes <paramref name="password"/>, or returns it unchanged when it already is a
    /// hash. Idempotent, which is what lets the migration run over a config file repeatedly (and
    /// over one a previous run already converted) without corrupting it.</summary>
    public static string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return IsHashed(password) ? password : LibreMetaverse.Utils.MD5(password);
    }
}
