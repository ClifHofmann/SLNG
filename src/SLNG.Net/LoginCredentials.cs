namespace SLNG.Net;

/// <summary>
/// Credentials and grid target for a login attempt. Engine-agnostic input to
/// <see cref="GridSession.LoginAsync"/>.
/// </summary>
public sealed record LoginCredentials
{
    /// <summary>Avatar first name.</summary>
    public required string FirstName { get; init; }

    /// <summary>Avatar last name. Use "Resident" for single-name SL accounts.</summary>
    public required string LastName { get; init; }

    /// <summary>Account password.</summary>
    public required string Password { get; init; }

    /// <summary>Grid login endpoint (LLSD/XML-RPC login URI).</summary>
    public required string GridLoginUri { get; init; }

    /// <summary>Viewer channel reported to the grid.</summary>
    public string Channel { get; init; } = "SLNG";

    /// <summary>Viewer version reported to the grid.</summary>
    public string Version { get; init; } = "0.1.0";

    /// <summary>Starting location (e.g. 'last', 'home', or 'RegionName/128/128/30').</summary>
    public string StartLocation { get; init; } = "last";

    /// <summary>Second Life main grid login URI.</summary>
    public const string SecondLifeLoginUri = "https://login.agni.lindenlab.com/cgi-bin/login.cgi";

    /// <summary>Default login URI for a local OpenSim standalone grid.</summary>
    public const string OpenSimLocalLoginUri = "http://127.0.0.1:9000/";
}
