using System.Text;

namespace SLNG.Net;

/// <summary>
/// Outcome of a login attempt. Engine-agnostic: identifiers are strings so no
/// LibreMetaverse type crosses the <c>SLNG.Net</c> boundary.
/// </summary>
public sealed record LoginResult
{
    /// <summary>Whether the login succeeded and the session is connected.</summary>
    public required bool Success { get; init; }

    /// <summary>Agent (avatar) UUID, when <see cref="Success"/> is true.</summary>
    public string? AgentId { get; init; }

    /// <summary>Session UUID, when <see cref="Success"/> is true.</summary>
    public string? SessionId { get; init; }

    /// <summary>Human-readable message from the grid (welcome text or error reason).</summary>
    public string? Message { get; init; }

    /// <summary>Machine-readable error key from the grid, when login failed.</summary>
    public string? ErrorKey { get; init; }

    /// <summary>The grid's <c>reason: "tos"</c> -- the login was refused only because the account
    /// has not accepted the Terms of Service. <see cref="Message"/> carries the text the grid wants
    /// shown. Not a failure to report and forget: TPV Policy §1.f requires presenting it and
    /// retrying with <see cref="LoginCredentials.AgreeToTos"/> once the user accepts.</summary>
    public bool RequiresTermsAcceptance => !Success && ErrorKey == TosErrorKey;

    /// <summary>The grid's <c>reason: "critical"</c> -- a message the user must read before the
    /// account may log in. Same flow as <see cref="RequiresTermsAcceptance"/>, retried with
    /// <see cref="LoginCredentials.ReadCritical"/>; the reference viewer drives both through one
    /// dialog (<c>lllogininstance.cpp</c>, <c>handleTOSResponse</c>).</summary>
    public bool RequiresCriticalAcknowledgement => !Success && ErrorKey == CriticalErrorKey;

    /// <summary>The grid's <c>reason: "mfa_challenge"</c> -- the account has multi-factor authentication
    /// and the login carried no valid code (or no valid remembered hash). FEAT-SL-02. Its own outcome:
    /// not a wrong password (<c>key</c>) and not a dead connection (<c>no-response</c>). Answered by
    /// asking the person for the current code and retrying with <see cref="LoginCredentials.MfaToken"/>;
    /// <c>llstartup.cpp:1307</c> and <c>lllogininstance.cpp:409</c> in the reference viewer.</summary>
    public bool RequiresMfaToken => !Success && ErrorKey == MfaChallengeErrorKey;

    /// <summary>Grid <c>reason</c> value for "enter your multi-factor code".</summary>
    public const string MfaChallengeErrorKey = "mfa_challenge";

    /// <summary>FEAT-SL-02: the grid's <c>mfa_hash</c>. On a success it is the "remember this computer"
    /// value to keep for later logins; on a <c>mfa_challenge</c> it is the value the reference viewer
    /// sends back together with the code (<c>lllogininstance.cpp:324</c>). Null when the grid sent none.
    /// A SECRET -- <see cref="PrintMembers"/> keeps it out of the record's text form.</summary>
    public string? MfaHash { get; init; }

    /// <summary>Grid <c>reason</c> value for "must accept the Terms of Service".</summary>
    public const string TosErrorKey = "tos";

    /// <summary>Grid <c>reason</c> value for "must read a critical message".</summary>
    public const string CriticalErrorKey = "critical";

    /// <summary>Creates a successful result.</summary>
    public static LoginResult Ok(string agentId, string sessionId, string? message, string? mfaHash = null) =>
        new() { Success = true, AgentId = agentId, SessionId = sessionId, Message = message, MfaHash = EmptyToNull(mfaHash) };

    /// <summary>Creates a failed result.</summary>
    public static LoginResult Fail(string? errorKey, string? message, string? mfaHash = null) =>
        new() { Success = false, ErrorKey = errorKey, Message = message, MfaHash = EmptyToNull(mfaHash) };

    private static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>The compiler-generated <c>ToString()</c> would print <see cref="MfaHash"/>; this prints
    /// everything else and only whether a hash is present.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Success = ").Append(Success)
            .Append(", AgentId = ").Append(AgentId)
            .Append(", SessionId = ").Append(SessionId)
            .Append(", Message = ").Append(Message)
            .Append(", ErrorKey = ").Append(ErrorKey)
            .Append(", MfaHash = ").Append(MfaHash is null ? "[none]" : "[set]");
        return true;
    }
}
