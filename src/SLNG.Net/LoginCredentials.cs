using System.Text;

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

    /// <summary>FEAT-SL-02: the one-time code from the person's authenticator app, typed in answer to
    /// the grid's <c>mfa_challenge</c>. Sent as <c>token</c>. Empty on every attempt that is not
    /// answering a challenge -- the reference viewer sends <c>token: ""</c> the same way
    /// (<c>lllogininstance.cpp:239</c>). Single-use and about 30 seconds old at best, so it is never
    /// stored and never carried over to a later attempt. A SECRET: it must not reach a log line, a
    /// notification or an exception message -- <see cref="PrintMembers"/> redacts it.</summary>
    public string MfaToken { get; init; } = "";

    /// <summary>FEAT-SL-02: the opaque <c>mfa_hash</c> a grid returned after an earlier answered
    /// challenge ("remember this computer"), sent back as <c>mfa_hash</c> so the grid can skip the
    /// challenge. Empty when there is none. Login-equivalent for the MFA step, so it is a SECRET like
    /// <see cref="Password"/> and is redacted by <see cref="PrintMembers"/>.</summary>
    public string MfaHash { get; init; } = "";

    /// <summary>Whether this attempt carries any MFA material (a code or a remembered hash). Only
    /// then does <see cref="GridSession.BuildLoginParams"/> switch LibreMetaverse's MFA fields on;
    /// otherwise the request is byte-for-byte what it was before MFA existed.</summary>
    public bool HasMfaMaterial => MfaToken.Length > 0 || MfaHash.Length > 0;

    /// <summary>The compiler-generated <c>ToString()</c> of a record prints every property -- that
    /// would write the password, the MFA code and the MFA hash into any log line or exception message
    /// that interpolates a <see cref="LoginCredentials"/>. This prints the non-secret fields and only
    /// says whether each secret is set.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("FirstName = ").Append(FirstName)
            .Append(", LastName = ").Append(LastName)
            .Append(", GridLoginUri = ").Append(GridLoginUri)
            .Append(", Channel = ").Append(Channel)
            .Append(", Version = ").Append(Version)
            .Append(", StartLocation = ").Append(StartLocation)
            .Append(", AgreeToTos = ").Append(AgreeToTos)
            .Append(", ReadCritical = ").Append(ReadCritical)
            .Append(", Password = ").Append(Password.Length > 0 ? "[set]" : "[none]")
            .Append(", MfaToken = ").Append(MfaToken.Length > 0 ? "[set]" : "[none]")
            .Append(", MfaHash = ").Append(MfaHash.Length > 0 ? "[set]" : "[none]");
        return true;
    }

    /// <summary>Second Life main grid ("Agni") login URI.</summary>
    public const string SecondLifeLoginUri = "https://login.agni.lindenlab.com/cgi-bin/login.cgi";

    /// <summary>Second Life BETA grid ("Aditi") login URI -- the grid to test on before ever
    /// touching Agni. Aditi accounts are periodic copies of the main grid, and the password is
    /// whatever it was at copy time, so a working Agni password is not necessarily the Aditi one.</summary>
    public const string SecondLifeBetaLoginUri = "https://login.aditi.lindenlab.com/cgi-bin/login.cgi";

    /// <summary>Default login URI for a local OpenSim standalone grid.</summary>
    public const string OpenSimLocalLoginUri = "http://127.0.0.1:9000/";
}
