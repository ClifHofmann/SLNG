namespace SLNG.Core;

/// <summary>Where a parcel stands in Linden Lab's land-lease lifecycle -- the <c>Status</c> byte of
/// <c>ParcelProperties</c>, <c>LLParcel::EOwnershipStatus</c> (<c>llparcel.h</c>:160). Only Second
/// Life uses it meaningfully; OpenSim sends <see cref="Leased"/> (0) for owned land.</summary>
public enum ParcelOwnership
{
    /// <summary>Not sent, or a value the viewer does not know (<c>OS_NONE</c>, -1).</summary>
    Unknown = 0,

    /// <summary>The normal state of owned land (<c>OS_LEASED</c>, 0).</summary>
    Leased,

    /// <summary>A sale is pending; the General tab appends "(Sale Pending)" to the owner
    /// (<c>OS_LEASE_PENDING</c>, 1; <c>llfloaterland.cpp</c>:884).</summary>
    LeasePending,

    /// <summary>The land has been abandoned (<c>OS_ABANDONED</c>, 2).</summary>
    Abandoned,
}
