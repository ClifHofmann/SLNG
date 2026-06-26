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

    /// <summary>Creates a successful result.</summary>
    public static LoginResult Ok(string agentId, string sessionId, string? message) =>
        new() { Success = true, AgentId = agentId, SessionId = sessionId, Message = message };

    /// <summary>Creates a failed result.</summary>
    public static LoginResult Fail(string? errorKey, string? message) =>
        new() { Success = false, ErrorKey = errorKey, Message = message };
}
