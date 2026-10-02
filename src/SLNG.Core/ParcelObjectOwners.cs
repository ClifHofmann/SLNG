namespace SLNG.Core;

/// <summary>The answer to "who owns objects on this parcel" (FEAT-LAND-03): the owners with their counts,
/// for one parcel of one region.
///
/// <para><b>Which parcel.</b> <c>ParcelObjectOwnersReply</c> carries no parcel id at all, so the record
/// says what the REQUEST was for: the sim answers in the order it was asked and the session files the
/// reply under the last request it made (region and local id). A window showing another parcel compares
/// <see cref="LocalId"/> / <see cref="RegionHandle"/> and ignores the rest.</para>
///
/// <para><b>One reply or several.</b> The reply is a <c>Variable</c> block and a UDP packet holds only
/// ~54 rows, so a long list can arrive as several replies with no "last one" marker on the wire. The
/// viewer clears the list on the first reply after a request and appends the later ones
/// (<c>mFirstReply</c>, <c>llfloaterland.cpp</c>:1613). The session does the same and raises the CUMULATIVE
/// record after every reply, so a consumer simply replaces its list. There is therefore no "complete"
/// flag: nothing on the wire says when the list is.</para>
///
/// <para><b>Refused versus empty.</b> Nothing on the wire tells them apart reliably; see
/// <see cref="OwnersWithheld"/>.</para></summary>
public sealed record ParcelObjectOwners
{
    /// <summary>The region of the parcel that was asked about.</summary>
    public ulong RegionHandle { get; init; }

    /// <summary>The parcel that was asked about (<c>LocalID</c> of the request).</summary>
    public int LocalId { get; init; }

    /// <summary>The owners, in no particular order (the sim's order, with replies appended). Sort for
    /// display with <c>LandInfoFormat</c>. Empty both for a parcel with no objects and for a list the
    /// sim declined to give.</summary>
    public IReadOnlyList<ParcelObjectOwner> Owners { get; init; } = Array.Empty<ParcelObjectOwner>();

    /// <summary>True when the reply held at least one row with a NIL owner id. LibreMetaverse documents
    /// that Second Life answers an agent without the right to see the list with exactly such a row
    /// ("If agent does not have proper permission the OwnerID will be UUID.Zero"), and the viewer skips
    /// the row and prints "None found." The same row may also be how an EMPTY parcel is answered, which
    /// this cannot tell apart; so it is a hint, not a verdict. OpenSim does not use it: it stays silent
    /// instead (no reply, so a <c>ParcelObjectOwnersFailed</c> after the timeout).</summary>
    public bool OwnersWithheld { get; init; }
}
