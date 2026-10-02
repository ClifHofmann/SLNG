namespace SLNG.Core;

/// <summary>The object (prim) counts the Land-Info "Objects" tab shows for one parcel, as engine- and
/// protocol-neutral data (FEAT-LAND-03). Built from the <c>ParcelProperties</c> message that
/// <see cref="ParcelInfo"/> comes from, so they arrive with it and a changed count is a changed
/// <see cref="ParcelInfo"/> (record equality), never a swallowed repeat.
///
/// <para>Every rule below is the reference viewer's, <c>LLPanelLandObjects::refresh</c>
/// (<c>llfloaterland.cpp</c>:1247-1357) over <c>LLViewerParcelMgr::processParcelProperties</c>
/// (<c>llviewerparcelmgr.cpp</c>:1655-1738). The wire's own <c>TotalPrims</c> field is NOT used by the
/// viewer (it is read into a local and dropped, :1660), so it is not carried here: the "total" is
/// derived, <see cref="TotalPrims"/>. No rights are exposed: what the agent may do with these objects
/// is the sim's decision.</para></summary>
public sealed record ParcelPrimCounts
{
    /// <summary>No counts known: all zero, bonus 1. What a <see cref="ParcelInfo"/> carries until the
    /// sim's message has been mapped.</summary>
    public static ParcelPrimCounts None { get; } = new();

    /// <summary><c>OwnerPrims</c>: "Owned by parcel owner". For a group-owned parcel, the prims
    /// deeded to the group.</summary>
    public int OwnerPrims { get; init; }

    /// <summary><c>GroupPrims</c>: "Set to group".</summary>
    public int GroupPrims { get; init; }

    /// <summary><c>OtherPrims</c>: "Owned by others".</summary>
    public int OtherPrims { get; init; }

    /// <summary><c>SelectedPrims</c>: "Selected / sat upon".</summary>
    public int SelectedPrims { get; init; }

    /// <summary><c>MaxPrims</c>: the parcel's allowance WITHOUT the bonus
    /// (<c>llparcel.h</c>:530 "Does not include prim bonus").</summary>
    public int MaxPrims { get; init; }

    /// <summary><c>ParcelPrimBonus</c>: the region's object bonus factor; 1 means none (the viewer
    /// hides its "Region Object Bonus Factor" line then, <c>llfloaterland.cpp</c>:1313).</summary>
    public float ParcelPrimBonus { get; init; } = 1f;

    /// <summary><c>SimWideMaxPrims</c>: the region-wide allowance for this parcel's owner.</summary>
    public int SimWideMaxPrims { get; init; }

    /// <summary><c>SimWideTotalPrims</c>: how many of those are in use.</summary>
    public int SimWideTotalPrims { get; init; }

    /// <summary><c>OtherCleanTime</c>: the "Auto return other residents' objects" delay in minutes;
    /// 0 is off (<c>llfloaterland.cpp</c>, the label "minutes, 0 for off").</summary>
    public int AutoReturnMinutes { get; init; }

    /// <summary>The REGION's object capacity from its <c>SimStats</c> (<c>ObjectCapacity</c>,
    /// <c>llviewermessage.cpp</c>:3963), which the viewer uses as a ceiling for both capacities
    /// (<c>llfloaterland.cpp</c>:1295-1301). 0 while it is not known; no ceiling is applied then. (The
    /// viewer applies a built-in 15000 until the stats arrive; that default is deliberately not copied,
    /// it would clip a bonus region for a moment.)</summary>
    public int RegionObjectCapacity { get; init; }

    /// <summary>"Parcel land impact": owner + group + other + selected
    /// (<c>LLParcel::getPrimCount</c>, <c>llparcel.h</c>:531).</summary>
    public int TotalPrims => OwnerPrims + GroupPrims + OtherPrims + SelectedPrims;

    /// <summary>"Parcel land capacity": the allowance times the bonus, rounded half up
    /// (<c>ll_round</c>), then cut to <see cref="RegionObjectCapacity"/> when that is known
    /// (<c>llfloaterland.cpp</c>:1277, :1296-1300).</summary>
    public int ParcelCapacity => Clamp(RoundHalfUp(MaxPrims * ParcelPrimBonus));

    /// <summary>"Region capacity" maximum: <see cref="SimWideMaxPrims"/>, cut to
    /// <see cref="RegionObjectCapacity"/> when known (<c>llfloaterland.cpp</c>:1295-1299).</summary>
    public int RegionCapacity => Clamp(SimWideMaxPrims);

    /// <summary>Region objects in use (<see cref="SimWideTotalPrims"/>).</summary>
    public int RegionTotalPrims => SimWideTotalPrims;

    /// <summary>How many more objects the region takes; 0 when it is over (see
    /// <see cref="RegionOverBy"/>).</summary>
    public int RegionAvailable => Math.Max(0, RegionCapacity - RegionTotalPrims);

    /// <summary>How many objects are over the region's capacity and "will be deleted"; 0 when none
    /// (<c>llfloaterland.cpp</c>:1318-1327: the viewer switches to that sentence when total > max).</summary>
    public int RegionOverBy => Math.Max(0, RegionTotalPrims - RegionCapacity);

    /// <summary>True when the bonus line is shown (the factor is not 1).</summary>
    public bool HasBonus => ParcelPrimBonus != 1f;

    private int Clamp(int value) => RegionObjectCapacity > 0 ? Math.Min(value, RegionObjectCapacity) : value;

    // ll_round for a float: (S32)floor(x + 0.5).
    private static int RoundHalfUp(float x) => (int)MathF.Floor(x + 0.5f);
}
