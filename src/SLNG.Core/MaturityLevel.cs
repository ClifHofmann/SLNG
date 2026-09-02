namespace SLNG.Core;

/// <summary>Second Life content-rating levels a viewer can ask to see -- "Age settings" in
/// everyday terms, though nothing here is actually about age; it is the grid's own maturity
/// rating system (General/Moderate/Adult), which gates what regions and content an account
/// receives. Engine- and protocol-neutral: the wire codes ("PG"/"M"/"A", <c>SIM_ACCESS_PG</c>
/// <c>=13</c>/<c>SIM_ACCESS_MATURE=21</c>/<c>SIM_ACCESS_ADULT=42</c>) are <c>SLNG.Net</c>'s
/// concern.
///
/// Ordinal order matters: <c>General &lt; Moderate &lt; Adult</c> is used directly to compare a
/// requested level against an account's verified ceiling (<c>GridSession.AccountMaturityMax</c>)
/// without a separate severity table.
///
/// OpenSim has no equivalent of this system -- there is no capability, and a region's own
/// maturity flag (when a grid sets one at all) is not something an account can raise or lower
/// its own view of. This only does anything on a real Second Life grid.</summary>
public enum MaturityLevel
{
    /// <summary>"General" -- SL's default, all-audiences rating. Wire code "PG".</summary>
    General = 0,

    /// <summary>"Moderate" -- SL calls this sim-access level "Mature" on the wire (code "M") but
    /// renamed it "Moderate" in the viewer UI years ago; both names refer to the same thing.</summary>
    Moderate = 1,

    /// <summary>"Adult". Wire code "A". Requires age verification on the account itself --
    /// <c>GridSession.AccountMaturityMax</c> reflects whether that verification exists; asking
    /// for this level without it is refused server-side, not silently granted.</summary>
    Adult = 2,
}
