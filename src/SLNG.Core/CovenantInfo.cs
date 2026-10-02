namespace SLNG.Core;

/// <summary>The estate covenant of one region, as engine- and protocol-neutral data (FEAT-LAND-05).
/// One record serves both the Land-Info "Covenant" tab and the Region/Estate "Covenant" tab: a
/// covenant belongs to the ESTATE, not to a parcel.
///
/// <para>Built from <c>EstateCovenantReply</c> (the header: estate name, owner, covenant asset id,
/// timestamp) and, when the estate has set one, the covenant notecard asset fetched by id over the
/// UDP estate transfer. <c>SLNG.Net</c> raises the record twice for a covenant that has text: first
/// with <see cref="TextState"/> <see cref="CovenantTextState.Loading"/>, then with the final state
/// once the notecard arrived or failed. Verified against the reference viewer's
/// <c>process_covenant_reply</c> / <c>onCovenantLoadComplete</c> (<c>llviewermessage.cpp</c>:6782-6947).</para></summary>
public sealed record CovenantInfo
{
    /// <summary>The region this answer is for (south-west corner, global metres). The estate
    /// covenant is the same on every parcel of a region, so this is the only key.</summary>
    public ulong RegionHandle { get; init; }

    /// <summary>The estate's name, verbatim from the sim. Empty if the sim sent none.</summary>
    public string EstateName { get; init; } = string.Empty;

    /// <summary><c>EstateOwnerID</c>. The viewer treats it as an avatar (it prints an avatar link);
    /// nil is shown as "(none)".</summary>
    public Guid EstateOwnerId { get; init; }

    /// <summary>The covenant notecard's asset id, or null when the estate has set none (the wire
    /// value is the nil UUID).</summary>
    public Guid? CovenantId { get; init; }

    /// <summary>When the covenant was last changed, UTC, or null when the sim sent 0 ("never").
    /// The wire value is Unix seconds (<c>U32</c>). The viewer prints it in the USER's local time,
    /// not SL time (<c>llstring.cpp</c>:1474, parameter <c>local</c>); that is a display matter.</summary>
    public DateTime? TimestampUtc { get; init; }

    /// <summary>Where the body stands; see <see cref="CovenantTextState"/>.</summary>
    public CovenantTextState TextState { get; init; } = CovenantTextState.Loading;

    /// <summary>The covenant text, with embedded inventory items dropped. Null unless
    /// <see cref="TextState"/> is <see cref="CovenantTextState.Loaded"/>; an empty string there means
    /// the estate saved an empty notecard, which is not the same as "no covenant".</summary>
    public string? Text { get; init; }
}
