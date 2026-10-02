namespace SLNG.Net;

/// <summary>Why a <c>RequestParcelInfo*</c> call produced no <see cref="SLNG.Core.ParcelInfo"/>.</summary>
public enum ParcelInfoFailureReason
{
    /// <summary>The sim answered but had no parcel data for that spot (<c>PARCEL_RESULT_NO_DATA</c>,
    /// or a multi-parcel answer to what was a single-point question).</summary>
    NoData,

    /// <summary>The sim did not answer within <see cref="GridSession.ParcelInfoTimeout"/>.</summary>
    TimedOut,
}
