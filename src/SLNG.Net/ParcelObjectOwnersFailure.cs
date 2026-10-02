namespace SLNG.Net;

/// <summary>Raised by <see cref="GridSession.ParcelObjectOwnersFailed"/> when
/// <see cref="GridSession.RequestParcelObjectOwners"/> got no <c>ParcelObjectOwnersReply</c> in time. Distinct from
/// a parcel with no objects, which arrives as a <see cref="SLNG.Core.ParcelObjectOwners"/> with no owners.
///
/// <para>The sim gives the list only to an agent who may manage the parcel, and OpenSim refuses by saying
/// nothing, so on OpenSim "no answer" is the refusal (it can also be a lost packet or a slow grid; the wire does
/// not say which). The request is for the parcel (<paramref name="RegionHandle"/>, <paramref name="LocalId"/>).</para></summary>
public sealed record ParcelObjectOwnersFailure(ulong RegionHandle, int LocalId);
