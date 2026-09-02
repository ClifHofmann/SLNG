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

    /// <summary>Viewer channel reported to the grid -- the "viewer identifier" TPV Policy §1.b
    /// requires to be unique, and what §5.b requires not to use any part of a Linden Lab
    /// trademark ("including 'Second,' 'Life,' 'SL,' or 'Linden'"). "SLNG" (the codebase's own
    /// internal name, from before Second Life was a stated target) starts with "SL" and fails
    /// that on the letter, even though nothing about it was ever meant to imply a connection to
    /// Linden Lab. "Puris" is the viewer's actual public name (<c>AboutWindow.ViewerName</c>,
    /// the login screen) and carries none of the four forbidden fragments.</summary>
    public string Channel { get; init; } = "Puris";

    /// <summary>Viewer version reported to the grid.</summary>
    public string Version { get; init; } = "0.1.0";

    /// <summary>Starting location (e.g. 'last', 'home', or 'RegionName/128/128/30').</summary>
    public string StartLocation { get; init; } = "last";

    /// <summary>Whether the user has been SHOWN the grid's Terms of Service and accepted them for
    /// THIS attempt. Sent as <c>agree_to_tos</c>.
    ///
    /// Defaults to <c>false</c> and must never be set except by a retry that follows a real
    /// acceptance: TPV Policy §1.f requires the viewer to present the ToS and obtain acceptance,
    /// and asserting it blindly accepts on the user's behalf, sight unseen. LibreMetaverse's
    /// <c>LoginParams</c> default constructor sets <c>AgreeToTos = true</c> (verified by reflection
    /// against the pinned 3.1.3 assembly), so <see cref="GridSession.LoginAsync"/> overwrites it
    /// from here on every attempt rather than leaving it alone.
    ///
    /// The reference viewer does exactly this: <c>lllogininstance.cpp</c> writes
    /// <c>request_params["agree_to_tos"] = false; // Always false here. Set true in
    /// handleTOSResponse</c>, and only <c>handleTOSResponse(accepted)</c> flips it before
    /// <c>reconnect()</c>.</summary>
    public bool AgreeToTos { get; init; }

    /// <summary>Whether the user has been shown the grid's <c>critical_message</c> and
    /// acknowledged it. Sent as <c>read_critical</c>. Same rule and same default as
    /// <see cref="AgreeToTos"/> -- the reference viewer drives both through one dialog and one
    /// callback, keyed by which flag to set.</summary>
    public bool ReadCritical { get; init; }

    /// <summary>Second Life main grid ("Agni") login URI.</summary>
    public const string SecondLifeLoginUri = "https://login.agni.lindenlab.com/cgi-bin/login.cgi";

    /// <summary>Second Life BETA grid ("Aditi") login URI -- the grid to test on before ever
    /// touching Agni. Aditi accounts are periodic copies of the main grid, and the password is
    /// whatever it was at copy time, so a working Agni password is not necessarily the Aditi one.</summary>
    public const string SecondLifeBetaLoginUri = "https://login.aditi.lindenlab.com/cgi-bin/login.cgi";

    /// <summary>Default login URI for a local OpenSim standalone grid.</summary>
    public const string OpenSimLocalLoginUri = "http://127.0.0.1:9000/";
}
