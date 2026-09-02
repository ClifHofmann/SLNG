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

    /// <summary>Grid <c>reason</c> value for "must accept the Terms of Service".</summary>
    public const string TosErrorKey = "tos";

    /// <summary>Grid <c>reason</c> value for "must read a critical message".</summary>
    public const string CriticalErrorKey = "critical";

    /// <summary>Creates a successful result.</summary>
    public static LoginResult Ok(string agentId, string sessionId, string? message) =>
        new() { Success = true, AgentId = agentId, SessionId = sessionId, Message = message };

    /// <summary>Creates a failed result.</summary>
    public static LoginResult Fail(string? errorKey, string? message) =>
        new() { Success = false, ErrorKey = errorKey, Message = message };
}
