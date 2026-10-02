using LibreMetaverse;
using SLNG.Core;
using ParcelInfo = SLNG.Core.ParcelInfo; // LibreMetaverse has a ParcelInfo too

namespace SLNG.Net;

/// <summary>Pure mapping from LibreMetaverse's parsed <c>ParcelProperties</c> to the neutral
/// <see cref="ParcelInfo"/> (FEAT-LAND-01). Nothing here touches the network or a session, so it is
/// unit-tested directly. LibreMetaverse types appear in the signatures but the class is internal:
/// they never cross <c>SLNG.Net</c>'s public boundary.</summary>
internal static class ParcelInfoMapper
{
    /// <summary>The sequence id our own lookups carry. It is the viewer's own id for "the parcel
    /// the user selected" (<c>SELECTED_PARCEL_SEQ_ID</c>, <c>llparcel.h</c>:91), which the sim
    /// merely echoes. A dedicated id keeps the reply apart from <c>GridSession</c>'s positive
    /// counter (environment lookups) without a second counter to keep in step.</summary>
    internal const int RequestSequenceId = -10000;

    /// <summary>Decides what a <c>ParcelProperties</c> reply is for, mirroring the branch in
    /// <c>LLViewerParcelMgr::processParcelProperties</c> (<c>llviewerparcelmgr.cpp</c>:1607-1636).
    ///
    /// The viewer treats id 0 or any id above the last agent-parcel id as "the agent's parcel
    /// changed", and every other negative id as a special lookup (selection -10000, collisions
    /// -20000/-30000/-40000, hover -50000, <c>llparcel.h</c>:91-95). The "out of order" rejection
    /// is deliberately not copied: <c>GridSession</c> also issues positive ids of its own, and mixing
    /// the two counters would let ours shadow the sim's.</summary>
    internal static ParcelReplyKind Classify(int sequenceId)
    {
        if (sequenceId == RequestSequenceId) return ParcelReplyKind.Requested;
        return sequenceId >= 0 ? ParcelReplyKind.AgentPush : ParcelReplyKind.Ignore;
    }

    /// <summary>Edge of the parcel grid: parcels are made of 4 m x 4 m cells
    /// (<c>PARCEL_GRID_STEP_METERS</c>).</summary>
    internal const float ParcelGridStepMetres = 4f;

    /// <summary>The box to send in <c>ParcelPropertiesRequest</c> to ask "which parcel is at this
    /// spot". Copies what the viewer sends for a point (<c>LLViewerParcelMgr::selectParcelAt</c>,
    /// <c>llviewerparcelmgr.cpp</c>:430-441): the spot -/+ half a cell, each corner rounded to the
    /// 4 m grid. That is exactly the one cell under the spot, so the answer is always a single
    /// parcel (a degenerate zero-area box, which the environment lookup sends, is not what the viewer
    /// sends). Clamped to the region.</summary>
    internal static (float North, float East, float South, float West) RequestBox(
        float localX, float localY, float regionSizeX = 256f, float regionSizeY = 256f)
    {
        static float Round(float v) => MathF.Floor(v / ParcelGridStepMetres + 0.5f) * ParcelGridStepMetres;
        const float half = ParcelGridStepMetres / 2f;

        float west = Math.Clamp(Round(localX - half), 0f, regionSizeX);
        float east = Math.Clamp(Round(localX + half), 0f, regionSizeX);
        float south = Math.Clamp(Round(localY - half), 0f, regionSizeY);
        float north = Math.Clamp(Round(localY + half), 0f, regionSizeY);
        return (north, east, south, west);
    }

    /// <summary>The region's maturity from its <c>SimAccess</c> byte, null when it is not one of the
    /// three ratings a viewer can show (<c>llfloaterland.cpp</c>:3150 switches on exactly these).</summary>
    internal static MaturityLevel? RatingFrom(SimAccess access) => access switch
    {
        SimAccess.PG => MaturityLevel.General,
        SimAccess.Mature => MaturityLevel.Moderate,
        SimAccess.Adult => MaturityLevel.Adult,
        _ => null,
    };

    internal static ParcelOwnership OwnershipFrom(ParcelStatus status) => status switch
    {
        ParcelStatus.Leased => ParcelOwnership.Leased,
        ParcelStatus.LeasePending => ParcelOwnership.LeasePending,
        ParcelStatus.Abandoned => ParcelOwnership.Abandoned,
        _ => ParcelOwnership.Unknown,
    };

    /// <summary>Builds the record for one parcel. <paramref name="access"/> and
    /// <paramref name="productName"/> are the REGION's (the parcel message carries neither).</summary>
    internal static ParcelInfo From(Parcel parcel, ulong regionHandle, SimAccess access, string? productName)
    {
        bool isPublic = parcel.OwnerID == UUID.Zero; // LLParcel::isPublic, llparcel.cpp:1072
        bool forSale = (parcel.Flags & ParcelFlags.ForSale) != 0; // PF_FOR_SALE, llparcelflags.h:34

        return new ParcelInfo
        {
            RegionHandle = regionHandle,
            LocalId = parcel.LocalID,
            Name = parcel.Name ?? string.Empty,
            Description = parcel.Desc ?? string.Empty,
            OwnerId = parcel.OwnerID.Guid,
            IsGroupOwned = parcel.IsGroupOwned,
            GroupId = parcel.GroupID.Guid,
            ClaimDateUtc = isPublic ? null : ClaimDateUtc(parcel.ClaimDate),
            ForSale = forSale,
            SalePriceL = forSale ? parcel.SalePrice : 0,
            AuthorizedBuyerId = parcel.AuthBuyerID == UUID.Zero ? null : parcel.AuthBuyerID.Guid,
            SellWithObjects = (parcel.Flags & ParcelFlags.SellParcelObjects) != 0, // PF_SELL_PARCEL_OBJECTS
            AuctionId = parcel.AuctionID,
            AreaSqm = parcel.Area,
            Ownership = OwnershipFrom(parcel.Status),
            Rating = RatingFrom(access),
            LandType = productName ?? string.Empty,
        };
    }

    /// <summary>LibreMetaverse hands back the Unix-seconds claim date as an epoch-based
    /// <see cref="DateTime"/> of unspecified kind (it is UTC). Zero means "no claim".</summary>
    private static DateTime? ClaimDateUtc(DateTime claimed)
    {
        if (claimed <= DateTime.UnixEpoch) return null;
        return claimed.Kind switch
        {
            DateTimeKind.Local => claimed.ToUniversalTime(),
            DateTimeKind.Utc => claimed,
            _ => DateTime.SpecifyKind(claimed, DateTimeKind.Utc),
        };
    }
}
