namespace SLNG.Core;

/// <summary>Everything the Land-Info "General" tab shows about one parcel, as engine- and
/// protocol-neutral data (FEAT-LAND-01). Built from one <c>ParcelProperties</c> message plus,
/// later, the <c>ParcelDwellReply</c> for the same parcel; <c>SLNG.Net</c> raises the record twice
/// when the dwell arrives (second time with <see cref="Dwell"/> and <see cref="ParcelId"/> filled).
///
/// Every field is verified against the reference viewer's <c>LLPanelLandGeneral::refresh</c>
/// (<c>llfloaterland.cpp</c>:564) and <c>LLViewerParcelMgr::processParcelProperties</c>
/// (<c>llviewerparcelmgr.cpp</c>:1545). No rights are exposed: <c>ParcelProperties</c> does not
/// carry what the agent may do (the viewer derives it from group powers it already holds), so the
/// sim stays the only authority and a write is simply refused there.</summary>
public sealed record ParcelInfo
{
    /// <summary>Region the parcel is in (south-west corner, global metres; see
    /// <see cref="RegionHandle"/>). <see cref="LocalId"/> is only unique within it.</summary>
    public ulong RegionHandle { get; init; }

    /// <summary>The parcel's id inside its region (<c>LocalID</c>). 0 is "public land" in a
    /// multi-parcel selection; the viewer never asks for dwell on it (<c>llviewerparcelmgr.cpp</c>:1882).</summary>
    public int LocalId { get; init; }

    /// <summary>The parcel's global UUID. <c>ParcelProperties</c> does not carry it; it arrives with
    /// the <c>ParcelDwellReply</c>, so this is <see cref="Guid.Empty"/> until then.</summary>
    public Guid ParcelId { get; init; }

    /// <summary>Parcel name, verbatim from the sim.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Parcel description, verbatim from the sim.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary><c>OwnerID</c>. Nil on public (unowned) land -- the viewer's <c>isPublic()</c>
    /// is exactly "owner is null" (<c>llparcel.cpp</c>:1072). When <see cref="IsGroupOwned"/> this
    /// is the GROUP's id, not an avatar's (<c>isParcelOwnedByAgent</c> passes it to
    /// <c>hasPowerInGroup</c>, <c>llviewerparcelmgr.cpp</c>:2665).</summary>
    public Guid OwnerId { get; init; }

    /// <summary>True when a group owns the parcel; the tab then shows "(Group Owned)" instead of an
    /// avatar name (<c>llfloaterland.cpp</c>:874).</summary>
    public bool IsGroupOwned { get; init; }

    /// <summary>The parcel's group (<c>GroupID</c>): the group the land is set to, which for a
    /// group-owned parcel equals the owner. Nil when none ("(none)" in the viewer).</summary>
    public Guid GroupId { get; init; }

    /// <summary>When the current owner claimed it, UTC. Null on public land, and when the sim sent
    /// 0 (no claim). The wire value is Unix seconds (<c>S32</c>); the viewer renders it in SL time
    /// (Pacific), which is a display matter (<c>llfloaterland.cpp</c>:710).</summary>
    public DateTime? ClaimDateUtc { get; init; }

    /// <summary>True when the parcel is for sale (<c>PF_FOR_SALE</c>, <c>llparcel.h</c>:476).</summary>
    public bool ForSale { get; init; }

    /// <summary>Sale price in L$ when <see cref="ForSale"/>, otherwise 0: the viewer reads the price
    /// only for a parcel that is for sale (<c>llfloaterland.cpp</c>:811).</summary>
    public int SalePriceL { get; init; }

    /// <summary>The one avatar the parcel is for sale to; null means "Anyone"
    /// (<c>llfloaterland.cpp</c>:897).</summary>
    public Guid? AuthorizedBuyerId { get; init; }

    /// <summary>True when the owner's objects are sold along with the land
    /// (<c>PF_SELL_PARCEL_OBJECTS</c>, "Sell with landowners objects in parcel").</summary>
    public bool SellWithObjects { get; init; }

    /// <summary>Auction id, 0 when not at auction (the tab shows "Auction ID: n" otherwise).</summary>
    public uint AuctionId { get; init; }

    /// <summary>Parcel area in square metres (<c>Area</c>); shown as "N m²".</summary>
    public int AreaSqm { get; init; }

    /// <summary>Lease state; see <see cref="ParcelOwnership"/>.</summary>
    public ParcelOwnership Ownership { get; init; }

    /// <summary>The REGION's maturity rating -- a parcel has none of its own, the viewer shows
    /// <c>getSimAccess()</c> of the selection's region (<c>llfloaterland.cpp</c>:3148). Null when the
    /// region's access byte is not PG/Mature/Adult (not yet received, or a down/unknown region).</summary>
    public MaturityLevel? Rating { get; init; }

    /// <summary>The REGION's product name from the handshake ("Mainland / Homestead", "Estate / Full
    /// Region"...), the "Type:" row (<c>llfloaterland.cpp</c>:644). Empty when the grid sends none
    /// (OpenSim sends its own region-type string). Raw: the viewer translates known names.</summary>
    public string LandType { get; init; } = string.Empty;

    /// <summary>"Traffic": the sim's visitor score for the parcel, a float the viewer prints with
    /// <c>%.0f</c> (<c>llfloaterland.cpp</c>:786). Null until the <c>ParcelDwellReply</c> arrives
    /// (the viewer shows "Loading..."), and null for local id 0, which is never asked.</summary>
    public float? Dwell { get; init; }
}
