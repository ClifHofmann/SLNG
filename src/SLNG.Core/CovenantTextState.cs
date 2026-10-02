namespace SLNG.Core;

/// <summary>Where the covenant BODY stands, separate from the estate header (name, owner, timestamp)
/// that arrives first (FEAT-LAND-05). A viewer shows four different things for the four states, and
/// none of them may pass for another: "no covenant set" is a statement by the estate, "failed" is
/// ours.</summary>
public enum CovenantTextState
{
    /// <summary>The header is known and the text is being fetched from the sim as an asset. The
    /// text is not yet known: <see cref="CovenantInfo.Text"/> is null.</summary>
    Loading,

    /// <summary>The estate has set no covenant (nil <see cref="CovenantInfo.CovenantId"/>). There is
    /// nothing to fetch; the viewer prints a fixed "there is no covenant" sentence of its own.</summary>
    None,

    /// <summary>The text arrived and decoded. <see cref="CovenantInfo.Text"/> is set, and may be an
    /// empty string if the estate saved an empty notecard.</summary>
    Loaded,

    /// <summary>The covenant has an id, but its text could not be fetched or decoded (no answer, the
    /// asset is missing, not a notecard). Never shown as "no covenant".</summary>
    Failed,
}
