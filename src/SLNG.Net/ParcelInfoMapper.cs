using LibreMetaverse;
using SLNG.Core;
using ParcelCategory = SLNG.Core.ParcelCategory; // LibreMetaverse has these too
using ParcelInfo = SLNG.Core.ParcelInfo;
using ParcelMedia = SLNG.Core.ParcelMedia;
using Vector3 = System.Numerics.Vector3; // and an OpenMetaverse-style Vector3

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

    /// <summary>Builds the record for one parcel. <paramref name="access"/>, <paramref name="productName"/>
    /// and <paramref name="regionFlags"/> are the REGION's (the parcel message carries none of them).
    /// <paramref name="obscureMoap"/> is the parcel's "Restrict MOAP" flag, which LibreMetaverse's typed
    /// message drops (see <see cref="ParcelInfo.ObscureMoap"/>); a caller that can read it some other way
    /// passes it, everyone else leaves it null. <paramref name="selectedPrims"/> is the message's
    /// <c>SelectedPrims</c>, which LibreMetaverse does not store in <see cref="Parcel"/> but hands to the
    /// event separately; <paramref name="regionObjectCapacity"/> is the region's <c>SimStats</c> value, 0
    /// when not known (both Objects tab, FEAT-LAND-03).</summary>
    internal static ParcelInfo From(
        Parcel parcel, ulong regionHandle, SimAccess access, string? productName,
        RegionFlags regionFlags = RegionFlags.None, bool? obscureMoap = null,
        int selectedPrims = 0, int regionObjectCapacity = 0)
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

            // Options / Media / Sound tabs (FEAT-LAND-02)
            Options = OptionsFrom(parcel),
            TeleportRouting = LandingFrom(parcel.Landing),
            LandingPoint = IsZero(parcel.UserLocation) ? null : ToNumerics(parcel.UserLocation),
            LandingLookAt = ToNumerics(parcel.UserLookAt),
            Category = CategoryFrom((int)parcel.Category),
            SnapshotId = parcel.SnapshotID == UUID.Zero ? null : parcel.SnapshotID.Guid,
            Media = MediaFrom(parcel.Media),
            MusicUrl = parcel.MusicURL ?? string.Empty,
            RegionVoiceEnabled = regionFlags == RegionFlags.None
                ? null
                : (regionFlags & RegionFlags.AllowVoice) != 0, // REGION_FLAGS_ALLOW_VOICE, llregionflags.h:87
            ObscureMoap = obscureMoap,

            // Objects tab (FEAT-LAND-03)
            Prims = PrimsFrom(parcel, selectedPrims, regionObjectCapacity),
        };
    }

    /// <summary>The Objects tab's counts, field for field as <c>LLViewerParcelMgr::processParcelProperties</c>
    /// stores them (<c>llviewerparcelmgr.cpp</c>:1731-1739, :1747). The wire's <c>TotalPrims</c> is left out:
    /// the viewer never uses it (see <see cref="ParcelPrimCounts"/>). Negative counts, which a sim should never
    /// send, are shown as 0 rather than as a negative number of objects.</summary>
    internal static ParcelPrimCounts PrimsFrom(Parcel parcel, int selectedPrims, int regionObjectCapacity) => new()
    {
        OwnerPrims = Math.Max(0, parcel.OwnerPrims),
        GroupPrims = Math.Max(0, parcel.GroupPrims),
        OtherPrims = Math.Max(0, parcel.OtherPrims),
        SelectedPrims = Math.Max(0, selectedPrims),
        MaxPrims = Math.Max(0, parcel.MaxPrims),
        // A bonus of 0 (an absent field decodes to 0) would zero the capacity; the viewer's own default is 1.
        ParcelPrimBonus = parcel.ParcelPrimBonus > 0f ? parcel.ParcelPrimBonus : 1f,
        SimWideMaxPrims = Math.Max(0, parcel.SimWideMaxPrims),
        SimWideTotalPrims = Math.Max(0, parcel.SimWideTotalPrims),
        AutoReturnMinutes = Math.Max(0, parcel.OtherCleanTime),
        RegionObjectCapacity = Math.Max(0, regionObjectCapacity),
    };

    /// <summary>The on/off settings of the Options and Sound tabs. One bit per control, raw: the
    /// "Group implied by Everyone" and inverted-box rules are display matters (see
    /// <see cref="ParcelOptions"/>). Flag values are checked against <c>llparcelflags.h</c>:32-63.</summary>
    internal static ParcelOptions OptionsFrom(Parcel parcel)
    {
        var f = parcel.Flags;
        var o = ParcelOptions.None;
        void Set(bool on, ParcelOptions bit) { if (on) o |= bit; }

        Set((f & ParcelFlags.AllowFly) != 0, ParcelOptions.AllowFly);                          // PF_ALLOW_FLY
        Set((f & ParcelFlags.CreateObjects) != 0, ParcelOptions.BuildEveryone);                // PF_CREATE_OBJECTS
        Set((f & ParcelFlags.CreateGroupObjects) != 0, ParcelOptions.BuildGroup);              // PF_CREATE_GROUP_OBJECTS
        Set((f & ParcelFlags.AllowAPrimitiveEntry) != 0, ParcelOptions.ObjectEntryEveryone);   // PF_ALLOW_ALL_OBJECT_ENTRY
        Set((f & ParcelFlags.AllowGroupObjectEntry) != 0, ParcelOptions.ObjectEntryGroup);     // PF_ALLOW_GROUP_OBJECT_ENTRY
        Set((f & ParcelFlags.AllowOtherScripts) != 0, ParcelOptions.ScriptsEveryone);          // PF_ALLOW_OTHER_SCRIPTS
        Set((f & ParcelFlags.AllowGroupScripts) != 0, ParcelOptions.ScriptsGroup);             // PF_ALLOW_GROUP_SCRIPTS
        Set((f & ParcelFlags.AllowDamage) != 0, ParcelOptions.AllowDamage);                    // PF_ALLOW_DAMAGE
        Set((f & ParcelFlags.RestrictPushObject) != 0, ParcelOptions.RestrictPush);            // PF_RESTRICT_PUSHOBJECT
        Set(parcel.RegionPushOverride, ParcelOptions.RegionPushOverride);                      // message field, not a flag
        Set((f & ParcelFlags.ShowDirectory) != 0, ParcelOptions.ShowInSearch);                 // PF_SHOW_DIRECTORY
        Set((f & ParcelFlags.MaturePublish) != 0, ParcelOptions.MaturePublish);                // PF_MATURE_PUBLISH
        Set(parcel.SeeAVs, ParcelOptions.SeeAvatars);                                          // message field SeeAVs
        Set((f & ParcelFlags.SoundLocal) != 0, ParcelOptions.SoundLocal);                      // PF_SOUND_LOCAL
        Set((f & ParcelFlags.AllowVoiceChat) != 0, ParcelOptions.AllowVoice);                  // PF_ALLOW_VOICE_CHAT
        Set((f & ParcelFlags.UseEstateVoiceChan) != 0, ParcelOptions.UseEstateVoiceChannel);   // PF_USE_ESTATE_VOICE_CHAN
        Set(parcel.AnyAVSounds, ParcelOptions.AvatarSoundsEveryone);                           // message field AnyAVSounds
        Set(parcel.GroupAVSounds, ParcelOptions.AvatarSoundsGroup);                            // message field GroupAVSounds
        return o;
    }

    /// <summary>LibreMetaverse's <c>LandingType</c> byte to the neutral enum; a value the viewer has no
    /// combo item for (outside 0..2) is null (<c>LLParcel::ELandingType</c>, <c>llparcel.h</c>:201).</summary>
    internal static ParcelLandingType? LandingFrom(LandingType landing) => landing switch
    {
        LandingType.None => ParcelLandingType.Blocked,
        LandingType.LandingPoint => ParcelLandingType.LandingPoint,
        LandingType.Direct => ParcelLandingType.Anywhere,
        _ => null,
    };

    /// <summary>The wire category number to the neutral enum. Done on the NUMBER because LibreMetaverse's
    /// own <c>ParcelCategory</c> has no "Rental" (14) member, which the viewer does
    /// (<c>llparcel.h</c>:166-182); anything outside 0..14 (the viewer's -1 "any" is query-only) is null.</summary>
    internal static ParcelCategory? CategoryFrom(int wire) =>
        wire is >= (int)ParcelCategory.None and <= (int)ParcelCategory.Rental ? (ParcelCategory)wire : null;

    /// <summary>The Media tab's data. Null strings (a default-constructed LibreMetaverse struct) become
    /// empty; an empty MIME type, or the viewer's own "none/none" placeholder, becomes null, exactly as
    /// <c>LLPanelLandMedia::refresh</c> folds both into "None" (<c>llpanellandmedia.cpp</c>:142). The
    /// <c>MediaData</c> block (type, description, size, loop) is absent on an old sim, which leaves those
    /// at their empty defaults here; the viewer then invents "video/vnd.secondlife.qt.legacy" and a loop
    /// flag (<c>llparcel.cpp</c>:613-618), which is deliberately not copied: it is a placeholder, not data.</summary>
    internal static ParcelMedia MediaFrom(LibreMetaverse.ParcelMedia media)
    {
        string? mime = string.IsNullOrWhiteSpace(media.MediaType) || media.MediaType == NoMimeType
            ? null
            : media.MediaType;
        return new ParcelMedia
        {
            Url = media.MediaURL ?? string.Empty,
            MimeType = mime,
            Description = media.MediaDesc ?? string.Empty,
            TextureId = media.MediaID == UUID.Zero ? null : media.MediaID.Guid,
            Width = media.MediaWidth,
            Height = media.MediaHeight,
            AutoScale = media.MediaAutoScale,
            Loop = media.MediaLoop,
        };
    }

    /// <summary>The viewer's placeholder for "no media type" (<c>DEFAULT_MIME_TYPE</c>, <c>llmimetypes.cpp</c>:47).</summary>
    private const string NoMimeType = "none/none";

    private static bool IsZero(LibreMetaverse.Vector3 v) => v.X == 0f && v.Y == 0f && v.Z == 0f; // isExactlyZero

    private static Vector3 ToNumerics(LibreMetaverse.Vector3 v) => new(v.X, v.Y, v.Z);

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
