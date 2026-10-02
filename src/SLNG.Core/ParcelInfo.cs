using System.Numerics;

namespace SLNG.Core;

/// <summary>Everything the Land-Info "General", "Options", "Media" and "Sound" tabs show about one
/// parcel, as engine- and protocol-neutral data (FEAT-LAND-01, FEAT-LAND-02). Built from one <c>ParcelProperties</c> message plus,
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

    // ---- Options / Media / Sound tabs (FEAT-LAND-02) -----------------------------------------
    // Sources: LLPanelLandOptions::refresh (llfloaterland.cpp:1996), LLPanelLandMedia::refresh
    // (llpanellandmedia.cpp:120), LLPanelLandAudio::refresh (llpanellandaudio.cpp:108) and the parcel
    // decode LLParcel::unpackMessage (llparcel.cpp:549). Like everything above, these are what the sim
    // SAID, never what the agent may do: the sim stays the authority on rights.

    /// <summary>Every on/off setting of the Options and Sound tabs; see <see cref="ParcelOptions"/> for
    /// which control each bit is and where the viewer derives a displayed value from it.</summary>
    public ParcelOptions Options { get; init; }

    /// <summary>Options tab "Teleport Routing" combo (<c>LLParcel::getLandingType</c>). Null when the sim
    /// sent a value outside 0..2, which the viewer shows as no selection (<c>llfloaterland.cpp</c>:2095).</summary>
    public ParcelLandingType? TeleportRouting { get; init; }

    /// <summary>Options tab "Landing Point": the region-local spot (metres) arrivals are sent to when
    /// <see cref="TeleportRouting"/> is <see cref="ParcelLandingType.LandingPoint"/>. Null when the
    /// parcel has none: the sim sends (0,0,0), which the viewer prints as "(none)"
    /// (<c>llfloaterland.cpp</c>:2109; the Clear button writes that same zero, :2368).</summary>
    public Vector3? LandingPoint { get; init; }

    /// <summary>The direction the agent faces on arrival (<c>UserLookAt</c>), a vector in the region's
    /// frame. Meaningless when <see cref="LandingPoint"/> is null; see <see cref="LandingHeadingDegrees"/>.</summary>
    public Vector3 LandingLookAt { get; init; }

    /// <summary>The compass heading the Options tab prints after the landing point, in whole degrees
    /// 0..359 (0 = north, 90 = east), computed exactly as the viewer does from
    /// <see cref="LandingLookAt"/> (<c>llfloaterland.cpp</c>:2106). Null when there is no landing point.</summary>
    public int? LandingHeadingDegrees
    {
        get
        {
            if (LandingPoint is null) return null;
            // atan2(y, -x) + 2*pi is always in [pi, 3*pi], so the degree count is >= 180 and the
            // viewer's unsigned "- 90" never wraps.
            double deg = (Math.Atan2(LandingLookAt.Y, -LandingLookAt.X) + Math.PI * 2) * (180.0 / Math.PI);
            return (int)(((uint)(deg + 0.5) - 90u) % 360u);
        }
    }

    /// <summary>Options tab search category (<c>LLParcel::getCategory</c>), only meaningful while
    /// <see cref="ParcelOptions.ShowInSearch"/> is set. Null for a wire value that is not a known
    /// category.</summary>
    public ParcelCategory? Category { get; init; }

    /// <summary>Options tab "Snapshot": the parcel picture (<c>SnapshotID</c>); null when nil, which the
    /// viewer shows as the default land picture.</summary>
    public Guid? SnapshotId { get; init; }

    /// <summary>Media tab; <see cref="ParcelMedia.None"/> when the parcel has no media.</summary>
    public ParcelMedia Media { get; init; } = ParcelMedia.None;

    /// <summary>Sound tab "Music URL" (<c>MusicURL</c>). Empty when none.</summary>
    public string MusicUrl { get; init; } = string.Empty;

    /// <summary>Sound tab voice box: whether the REGION allows voice at all (<c>REGION_FLAGS_ALLOW_VOICE</c>
    /// from the region handshake, <c>LLViewerRegion::isVoiceEnabled</c>). When false the viewer replaces
    /// "Enable Voice" with a disabled "Enable Voice (established by the Estate)" and greys the
    /// restrict-voice box (<c>llpanellandaudio.cpp</c>:129-148). Null when the region flags are not
    /// known yet; show the plain "Enable Voice" box then.</summary>
    public bool? RegionVoiceEnabled { get; init; }

    /// <summary>Sound tab "Media: Restrict MOAP to this parcel" (the parcel's obscure-media-on-a-prim
    /// flag, <c>ParcelExtendedFlags.Flags</c> in the parcel message,
    /// <c>llviewerparcelmgr.cpp</c>:1681-1684 and :1748). ALWAYS null for now: LibreMetaverse 3.1.6 drops
    /// that block when it decodes the message and exposes no per-parcel value, so SLNG cannot read it. A UI
    /// must show the box as "unknown" (or leave it out), never as unticked.</summary>
    public bool? ObscureMoap { get; init; }
}
