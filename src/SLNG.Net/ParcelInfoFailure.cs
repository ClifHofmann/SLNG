namespace SLNG.Net;

/// <summary>Raised by <see cref="GridSession.ParcelInfoFailed"/> when a requested parcel lookup was
/// refused or went unanswered. Distinct from a parcel that exists but is empty or public: that one
/// arrives as a <see cref="SLNG.Core.ParcelInfo"/>.</summary>
public sealed record ParcelInfoFailure(ulong RegionHandle, ParcelInfoFailureReason Reason);
