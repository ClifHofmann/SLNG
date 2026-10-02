using LibreMetaverse;
using SLNG.Core;
using SLNG.Net;
using Xunit;
using ParcelCategory = SLNG.Core.ParcelCategory; // LibreMetaverse has these too
using ParcelInfo = SLNG.Core.ParcelInfo;
using ParcelMedia = SLNG.Core.ParcelMedia;

namespace SLNG.Net.Tests;

// FEAT-LAND-01: the Land-Info General tab's data. No network: LibreMetaverse's Parcel is built by
// hand, mapped through the same pure functions GridSession uses, and the sequence-id / dwell-merge
// rules are driven directly.
public class ParcelInfoTests
{
    private const ulong Region = 281474976710656UL; // an arbitrary handle

    private static Parcel Owned(int localId = 7) => new(localId)
    {
        Name = "My Parcel",
        Desc = "Hello",
        OwnerID = UUID.Random(),
        GroupID = UUID.Zero,
        Area = 512,
        ClaimDate = new DateTime(2024, 3, 5, 12, 30, 0, DateTimeKind.Unspecified),
        Status = ParcelStatus.Leased,
        Flags = ParcelFlags.None,
    };

    private static ParcelInfo Map(Parcel p, SimAccess access = SimAccess.PG, string product = "") =>
        ParcelInfoMapper.From(p, Region, access, product);

    // ---- mapping ---------------------------------------------------------------------------------

    [Fact]
    public void Plain_owned_parcel_maps_identity_area_and_claim_date()
    {
        var p = Owned();
        var info = Map(p);

        Assert.Equal(Region, info.RegionHandle);
        Assert.Equal(7, info.LocalId);
        Assert.Equal("My Parcel", info.Name);
        Assert.Equal("Hello", info.Description);
        Assert.Equal(p.OwnerID.Guid, info.OwnerId);
        Assert.False(info.IsGroupOwned);
        Assert.Equal(512, info.AreaSqm);
        Assert.Equal(DateTimeKind.Utc, info.ClaimDateUtc!.Value.Kind);
        Assert.Equal(new DateTime(2024, 3, 5, 12, 30, 0, DateTimeKind.Utc), info.ClaimDateUtc);
        Assert.Equal(ParcelOwnership.Leased, info.Ownership);
        Assert.Equal(Guid.Empty, info.ParcelId);
        Assert.Null(info.Dwell);
        Assert.False(info.ForSale);
        Assert.Equal(0, info.SalePriceL);
        Assert.Null(info.AuthorizedBuyerId);
    }

    [Fact]
    public void Local_claim_date_is_converted_to_utc()
    {
        var p = Owned();
        p.ClaimDate = new DateTime(2024, 3, 5, 12, 0, 0, DateTimeKind.Utc).ToLocalTime();
        Assert.Equal(new DateTime(2024, 3, 5, 12, 0, 0, DateTimeKind.Utc), Map(p).ClaimDateUtc);
    }

    [Fact]
    public void Public_land_has_no_claim_date_even_if_the_sim_sent_one()
    {
        var p = Owned();
        p.OwnerID = UUID.Zero;
        Assert.Null(Map(p).ClaimDateUtc);
        Assert.Equal(Guid.Empty, Map(p).OwnerId);
    }

    [Fact]
    public void A_zero_claim_date_is_unknown_not_1970()
    {
        var p = Owned();
        p.ClaimDate = new DateTime(1970, 1, 1);
        Assert.Null(Map(p).ClaimDateUtc);
    }

    [Fact]
    public void For_sale_carries_price_buyer_and_sell_with_objects()
    {
        var buyer = UUID.Random();
        var p = Owned();
        p.Flags = ParcelFlags.ForSale | ParcelFlags.SellParcelObjects;
        p.SalePrice = 1250;
        p.AuthBuyerID = buyer;

        var info = Map(p);

        Assert.True(info.ForSale);
        Assert.Equal(1250, info.SalePriceL);
        Assert.Equal(buyer.Guid, info.AuthorizedBuyerId);
        Assert.True(info.SellWithObjects);
    }

    [Fact]
    public void Not_for_sale_zeroes_a_leftover_price()
    {
        var p = Owned();
        p.SalePrice = 999;
        p.Flags = ParcelFlags.None;
        Assert.Equal(0, Map(p).SalePriceL);
        Assert.False(Map(p).ForSale);
    }

    [Fact]
    public void ForSaleObjects_flag_alone_does_not_mean_for_sale()
    {
        var p = Owned();
        p.Flags = ParcelFlags.ForSaleObjects; // PF_FOR_SALE_OBJECTS is a different bit from PF_FOR_SALE
        Assert.False(Map(p).ForSale);
        Assert.False(Map(p).SellWithObjects);
    }

    [Fact]
    public void Group_owned_keeps_the_group_id_as_owner()
    {
        var group = UUID.Random();
        var p = Owned();
        p.IsGroupOwned = true;
        p.OwnerID = group;
        p.GroupID = group;

        var info = Map(p);

        Assert.True(info.IsGroupOwned);
        Assert.Equal(group.Guid, info.OwnerId);
        Assert.Equal(group.Guid, info.GroupId);
    }

    [Theory]
    [InlineData(ParcelStatus.Leased, ParcelOwnership.Leased)]
    [InlineData(ParcelStatus.LeasePending, ParcelOwnership.LeasePending)]
    [InlineData(ParcelStatus.Abandoned, ParcelOwnership.Abandoned)]
    [InlineData(ParcelStatus.None, ParcelOwnership.Unknown)]
    [InlineData((ParcelStatus)77, ParcelOwnership.Unknown)]
    public void Ownership_status_maps(ParcelStatus wire, ParcelOwnership expected)
    {
        var p = Owned();
        p.Status = wire;
        Assert.Equal(expected, Map(p).Ownership);
    }

    [Theory]
    [InlineData(SimAccess.PG, MaturityLevel.General)]
    [InlineData(SimAccess.Mature, MaturityLevel.Moderate)]
    [InlineData(SimAccess.Adult, MaturityLevel.Adult)]
    public void Rating_comes_from_the_region_access_byte(SimAccess access, MaturityLevel expected)
        => Assert.Equal(expected, Map(Owned(), access).Rating);

    [Theory]
    [InlineData(SimAccess.Unknown)]
    [InlineData(SimAccess.Down)]
    [InlineData(SimAccess.NonExistent)]
    public void An_unrecognised_region_access_is_unknown_not_General(SimAccess access)
        => Assert.Null(Map(Owned(), access).Rating);

    [Fact]
    public void Land_type_is_the_region_product_name()
        => Assert.Equal("Mainland / Homestead", Map(Owned(), product: "Mainland / Homestead").LandType);

    [Fact]
    public void Auction_id_is_carried()
    {
        var p = Owned();
        p.AuctionID = 4711;
        Assert.Equal(4711u, Map(p).AuctionId);
    }

    // ---- sequence ids ---------------------------------------------------------------------------

    [Fact]
    public void Our_request_id_is_the_viewers_selected_parcel_id()
        => Assert.Equal(-10000, ParcelInfoMapper.RequestSequenceId); // llparcel.h:91 SELECTED_PARCEL_SEQ_ID

    [Theory]
    [InlineData(-10000, "Requested")]
    [InlineData(0, "AgentPush")]        // OpenSim's push id
    [InlineData(1, "AgentPush")]
    [InlineData(123456, "AgentPush")]   // a long-lived SL counter
    [InlineData(-20000, "Ignore")]      // collision: not in group
    [InlineData(-30000, "Ignore")]      // collision: banned
    [InlineData(-40000, "Ignore")]      // collision: not on list
    [InlineData(-50000, "Ignore")]      // hover
    [InlineData(-1, "Ignore")]
    public void Sequence_ids_are_classified_like_the_viewer(int sequenceId, string expected)
        => Assert.Equal(expected, ParcelInfoMapper.Classify(sequenceId).ToString()); // string: the enum is internal

    // ---- request box ----------------------------------------------------------------------------

    [Theory]
    [InlineData(10f, 10f, 12f, 12f, 8f, 8f)]     // north, east, south, west
    [InlineData(11.9f, 11.9f, 12f, 12f, 8f, 8f)]
    [InlineData(12.1f, 12.1f, 16f, 16f, 12f, 12f)]
    [InlineData(0f, 0f, 4f, 4f, 0f, 0f)]
    [InlineData(255.9f, 255.9f, 256f, 256f, 252f, 252f)] // clamped to the region edge
    public void Request_box_is_the_viewers_one_cell_under_the_spot(
        float x, float y, float north, float east, float south, float west)
    {
        var box = ParcelInfoMapper.RequestBox(x, y);
        Assert.Equal((north, east, south, west), (box.North, box.East, box.South, box.West));
    }

    [Fact]
    public void Request_box_x_is_east_west_and_y_is_north_south()
    {
        var box = ParcelInfoMapper.RequestBox(100f, 20f);
        Assert.True(box.East > 90f && box.West > 90f);
        Assert.True(box.North < 30f && box.South < 30f);
    }

    // ---- tracker: dwell merge and de-duplication -------------------------------------------------

    [Fact]
    public void Dwell_reply_merges_into_the_same_parcel_and_fills_the_parcel_id()
    {
        var tracker = new ParcelInfoTracker();
        var id = Guid.NewGuid();
        tracker.OnProperties(Map(Owned(7)), solicited: true);

        var merged = tracker.OnDwell(Region, 7, id, 312.6f);

        Assert.NotNull(merged);
        Assert.Equal(312.6f, merged!.Dwell);
        Assert.Equal(id, merged.ParcelId);
        Assert.Equal("My Parcel", merged.Name);
    }

    [Fact]
    public void Dwell_reply_for_another_parcel_or_region_is_dropped()
    {
        var tracker = new ParcelInfoTracker();
        tracker.OnProperties(Map(Owned(7)), solicited: true);

        Assert.Null(tracker.OnDwell(Region, 8, Guid.NewGuid(), 5f));
        Assert.Null(tracker.OnDwell(Region + 1, 7, Guid.NewGuid(), 5f));
        Assert.Null(tracker.Current!.Dwell);
    }

    [Fact]
    public void Dwell_reply_before_any_parcel_is_dropped()
        => Assert.Null(new ParcelInfoTracker().OnDwell(Region, 7, Guid.NewGuid(), 5f));

    [Fact]
    public void Dwell_reply_with_nothing_new_is_not_raised_again()
    {
        var tracker = new ParcelInfoTracker();
        var id = Guid.NewGuid();
        tracker.OnProperties(Map(Owned(7)), solicited: true);
        Assert.NotNull(tracker.OnDwell(Region, 7, id, 10f));

        // LibreMetaverse's own AlwaysRequestDwell asks as well, so the same answer can arrive twice
        Assert.Null(tracker.OnDwell(Region, 7, id, 10f));
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void A_negative_or_nan_dwell_is_not_a_value(float dwell)
    {
        var tracker = new ParcelInfoTracker();
        tracker.OnProperties(Map(Owned(7)), solicited: true);
        Assert.Null(tracker.OnDwell(Region, 7, Guid.NewGuid(), dwell));
        Assert.Null(tracker.Current!.Dwell);
    }

    [Fact]
    public void Zero_dwell_is_a_value_not_unknown()
    {
        var tracker = new ParcelInfoTracker();
        tracker.OnProperties(Map(Owned(7)), solicited: true);
        Assert.Equal(0f, tracker.OnDwell(Region, 7, Guid.NewGuid(), 0f)!.Dwell);
    }

    [Fact]
    public void An_empty_parcel_id_in_the_dwell_reply_keeps_the_known_one()
    {
        var tracker = new ParcelInfoTracker();
        var id = Guid.NewGuid();
        tracker.OnProperties(Map(Owned(7)), solicited: true);
        tracker.OnDwell(Region, 7, id, 1f);

        Assert.Equal(id, tracker.OnDwell(Region, 7, Guid.Empty, 2f)!.ParcelId);
    }

    [Fact]
    public void A_repeated_push_of_an_unchanged_parcel_is_suppressed_and_keeps_the_dwell()
    {
        var tracker = new ParcelInfoTracker();
        var p = Owned(7);
        Assert.NotNull(tracker.OnProperties(Map(p), solicited: false));
        tracker.OnDwell(Region, 7, Guid.NewGuid(), 42f);

        // the environment poll asks for the agent's parcel every 30 s and the answer arrives as a push
        Assert.Null(tracker.OnProperties(Map(p), solicited: false));
        Assert.Equal(42f, tracker.Current!.Dwell);
    }

    [Fact]
    public void A_requested_reply_is_always_raised_even_when_unchanged()
    {
        var tracker = new ParcelInfoTracker();
        var p = Owned(7);

        Assert.NotNull(tracker.OnProperties(Map(p), solicited: true));
        Assert.NotNull(tracker.OnProperties(Map(p), solicited: true));
    }

    [Fact]
    public void A_different_parcel_starts_without_dwell_or_parcel_id()
    {
        var tracker = new ParcelInfoTracker();
        tracker.OnProperties(Map(Owned(7)), solicited: false);
        tracker.OnDwell(Region, 7, Guid.NewGuid(), 42f);

        var next = tracker.OnProperties(Map(Owned(9)), solicited: false);

        Assert.Equal(9, next!.LocalId);
        Assert.Null(next.Dwell);
        Assert.Equal(Guid.Empty, next.ParcelId);
    }

    [Fact]
    public void An_edit_to_the_same_parcel_is_raised_and_keeps_the_dwell()
    {
        var tracker = new ParcelInfoTracker();
        var p = Owned(7);
        tracker.OnProperties(Map(p), solicited: false);
        tracker.OnDwell(Region, 7, Guid.NewGuid(), 42f);

        p.Name = "Renamed";
        var next = tracker.OnProperties(Map(p), solicited: false);

        Assert.Equal("Renamed", next!.Name);
        Assert.Equal(42f, next.Dwell);
    }

    // ---- FEAT-LAND-02: Options / Media / Sound -----------------------------------------------------
    // Flag values are llparcelflags.h:32-63; each test builds the LibreMetaverse Parcel by hand and
    // maps it through the same pure function GridSession uses.

    [Theory]
    [InlineData(ParcelFlags.AllowFly, ParcelOptions.AllowFly)]
    [InlineData(ParcelFlags.CreateObjects, ParcelOptions.BuildEveryone)]
    [InlineData(ParcelFlags.CreateGroupObjects, ParcelOptions.BuildGroup)]
    [InlineData(ParcelFlags.AllowAPrimitiveEntry, ParcelOptions.ObjectEntryEveryone)]
    [InlineData(ParcelFlags.AllowGroupObjectEntry, ParcelOptions.ObjectEntryGroup)]
    [InlineData(ParcelFlags.AllowOtherScripts, ParcelOptions.ScriptsEveryone)]
    [InlineData(ParcelFlags.AllowGroupScripts, ParcelOptions.ScriptsGroup)]
    [InlineData(ParcelFlags.AllowDamage, ParcelOptions.AllowDamage)]
    [InlineData(ParcelFlags.RestrictPushObject, ParcelOptions.RestrictPush)]
    [InlineData(ParcelFlags.ShowDirectory, ParcelOptions.ShowInSearch)]
    [InlineData(ParcelFlags.MaturePublish, ParcelOptions.MaturePublish)]
    [InlineData(ParcelFlags.SoundLocal, ParcelOptions.SoundLocal)]
    [InlineData(ParcelFlags.AllowVoiceChat, ParcelOptions.AllowVoice)]
    [InlineData(ParcelFlags.UseEstateVoiceChan, ParcelOptions.UseEstateVoiceChannel)]
    public void Each_parcel_flag_maps_to_exactly_its_own_option_bit(ParcelFlags flag, ParcelOptions expected)
    {
        var p = Owned();
        p.Flags = flag;
        Assert.Equal(expected, Map(p).Options);
    }

    [Theory]
    [InlineData(ParcelFlags.ForSale)]
    [InlineData(ParcelFlags.ForSaleObjects)]
    [InlineData(ParcelFlags.AllowLandmark)]
    [InlineData(ParcelFlags.AllowTerraform)]
    [InlineData(ParcelFlags.UseAccessGroup)]
    [InlineData(ParcelFlags.UseAccessList)]
    [InlineData(ParcelFlags.UseBanList)]
    [InlineData(ParcelFlags.UsePassList)]
    [InlineData(ParcelFlags.AllowDeedToGroup)]
    [InlineData(ParcelFlags.ContributeWithDeed)]
    [InlineData(ParcelFlags.SellParcelObjects)]
    [InlineData(ParcelFlags.AllowPublish)]
    [InlineData(ParcelFlags.UrlWebPage)]
    [InlineData(ParcelFlags.UrlRawHtml)]
    [InlineData(ParcelFlags.DenyAnonymous)]
    [InlineData(ParcelFlags.DenyAgeUnverified)]
    public void Parcel_flags_the_three_tabs_do_not_show_set_no_option_bit(ParcelFlags flag)
    {
        var p = Owned();
        p.Flags = flag;
        Assert.Equal(ParcelOptions.None, Map(p).Options);
    }

    [Fact]
    public void Every_parcel_flag_together_sets_every_flag_backed_option_and_nothing_else()
    {
        var p = Owned();
        p.Flags = (ParcelFlags)uint.MaxValue;

        const ParcelOptions notFlagBacked = ParcelOptions.RegionPushOverride | ParcelOptions.SeeAvatars
            | ParcelOptions.AvatarSoundsEveryone | ParcelOptions.AvatarSoundsGroup;
        var all = Enum.GetValues<ParcelOptions>().Aggregate(ParcelOptions.None, (a, b) => a | b);

        Assert.Equal(all & ~notFlagBacked, Map(p).Options);
    }

    [Fact]
    public void The_message_booleans_that_are_not_parcel_flags_map_to_their_own_bits()
    {
        var p = Owned();
        Assert.Equal(ParcelOptions.None, Map(p).Options);

        p.RegionPushOverride = true;
        Assert.Equal(ParcelOptions.RegionPushOverride, Map(p).Options);
        p.RegionPushOverride = false;

        p.SeeAVs = true;
        Assert.Equal(ParcelOptions.SeeAvatars, Map(p).Options);
        p.SeeAVs = false;

        p.AnyAVSounds = true;
        Assert.Equal(ParcelOptions.AvatarSoundsEveryone, Map(p).Options);
        p.AnyAVSounds = false;

        p.GroupAVSounds = true;
        Assert.Equal(ParcelOptions.AvatarSoundsGroup, Map(p).Options);
    }

    [Fact]
    public void Option_bits_are_distinct_single_bits()
    {
        var seen = ParcelOptions.None;
        foreach (var bit in Enum.GetValues<ParcelOptions>().Where(o => o != ParcelOptions.None))
        {
            Assert.Equal(1, System.Numerics.BitOperations.PopCount((uint)bit));
            Assert.Equal(ParcelOptions.None, seen & bit);
            seen |= bit;
        }
    }

    [Fact]
    public void Group_and_everyone_stay_independent_bits()
    {
        // the "Group implied by Everyone" display rule is the UI's; the record keeps what the sim sent
        var p = Owned();
        p.Flags = ParcelFlags.CreateObjects | ParcelFlags.AllowOtherScripts;
        var o = Map(p).Options;
        Assert.True(o.HasFlag(ParcelOptions.BuildEveryone));
        Assert.False(o.HasFlag(ParcelOptions.BuildGroup));
        Assert.True(o.HasFlag(ParcelOptions.ScriptsEveryone));
        Assert.False(o.HasFlag(ParcelOptions.ScriptsGroup));
    }

    // landing point and routing

    [Fact]
    public void A_zero_user_location_means_no_landing_point()
    {
        var p = Owned();
        p.UserLocation = LibreMetaverse.Vector3.Zero;
        var info = Map(p);
        Assert.Null(info.LandingPoint);
        Assert.Null(info.LandingHeadingDegrees);
    }

    [Fact]
    public void A_user_location_becomes_a_numerics_landing_point_and_look_at()
    {
        var p = Owned();
        p.UserLocation = new LibreMetaverse.Vector3(128.5f, 64f, 22.25f);
        p.UserLookAt = new LibreMetaverse.Vector3(0f, 1f, 0f);
        var info = Map(p);
        Assert.Equal(new System.Numerics.Vector3(128.5f, 64f, 22.25f), info.LandingPoint);
        Assert.Equal(new System.Numerics.Vector3(0f, 1f, 0f), info.LandingLookAt);
    }

    [Theory] // the compass angle of llfloaterland.cpp:2106: 0 = north, 90 = east
    [InlineData(0f, 1f, 0)]
    [InlineData(1f, 0f, 90)]
    [InlineData(0f, -1f, 180)]
    [InlineData(-1f, 0f, 270)]
    [InlineData(1f, 1f, 45)]
    public void Landing_heading_is_the_compass_angle_the_viewer_prints(float x, float y, int degrees)
    {
        var p = Owned();
        p.UserLocation = new LibreMetaverse.Vector3(10f, 10f, 10f);
        p.UserLookAt = new LibreMetaverse.Vector3(x, y, 0f);
        Assert.Equal(degrees, Map(p).LandingHeadingDegrees);
    }

    [Theory]
    [InlineData(LandingType.None, ParcelLandingType.Blocked)]
    [InlineData(LandingType.LandingPoint, ParcelLandingType.LandingPoint)]
    [InlineData(LandingType.Direct, ParcelLandingType.Anywhere)]
    public void Teleport_routing_maps(LandingType wire, ParcelLandingType expected)
    {
        var p = Owned();
        p.Landing = wire;
        Assert.Equal(expected, Map(p).TeleportRouting);
    }

    [Fact]
    public void An_unknown_landing_type_is_unknown_not_blocked()
    {
        var p = Owned();
        p.Landing = (LandingType)9;
        Assert.Null(Map(p).TeleportRouting);
    }

    // category and snapshot

    [Theory]
    [InlineData(0, ParcelCategory.None)]
    [InlineData(1, ParcelCategory.Linden)]
    [InlineData(2, ParcelCategory.Adult)]
    [InlineData(3, ParcelCategory.Arts)]
    [InlineData(4, ParcelCategory.Business)]
    [InlineData(5, ParcelCategory.Educational)]
    [InlineData(6, ParcelCategory.Gaming)]
    [InlineData(7, ParcelCategory.Hangout)]
    [InlineData(8, ParcelCategory.Newcomer)]
    [InlineData(9, ParcelCategory.Park)]
    [InlineData(10, ParcelCategory.Residential)]
    [InlineData(11, ParcelCategory.Shopping)]
    [InlineData(12, ParcelCategory.Stage)]
    [InlineData(13, ParcelCategory.Other)]
    [InlineData(14, ParcelCategory.Rental)] // LibreMetaverse's own enum stops at 13
    public void Category_maps_from_the_wire_number(int wire, ParcelCategory expected)
    {
        var p = Owned();
        p.Category = (LibreMetaverse.ParcelCategory)wire;
        Assert.Equal(expected, Map(p).Category);
    }

    [Theory]
    [InlineData(-1)] // C_ANY, a query value only
    [InlineData(15)]
    [InlineData(100)]
    public void A_category_outside_the_known_range_is_unknown(int wire)
    {
        var p = Owned();
        p.Category = (LibreMetaverse.ParcelCategory)wire;
        Assert.Null(Map(p).Category);
    }

    [Fact]
    public void Snapshot_id_is_null_when_nil_and_the_guid_otherwise()
    {
        var p = Owned();
        Assert.Null(Map(p).SnapshotId);
        var id = UUID.Random();
        p.SnapshotID = id;
        Assert.Equal(id.Guid, Map(p).SnapshotId);
    }

    // media

    [Fact]
    public void A_parcel_with_no_media_maps_to_the_empty_media_record()
    {
        var info = Map(Owned());
        Assert.Equal(ParcelMedia.None, info.Media);
        Assert.False(info.Media.HasMedia);
        Assert.Null(info.Media.MimeType);
        Assert.Equal(string.Empty, info.Media.Url);
        Assert.Null(info.Media.TextureId);
    }

    [Fact]
    public void A_parcel_built_with_a_default_media_struct_does_not_throw_on_null_strings()
    {
        var p = Owned();
        p.Media = default; // what the Parcel constructor leaves: null string members
        p.MusicURL = null!;
        var info = Map(p);
        Assert.Equal(string.Empty, info.Media.Url);
        Assert.Equal(string.Empty, info.Media.Description);
        Assert.Equal(string.Empty, info.MusicUrl);
    }

    [Fact]
    public void Media_fields_are_all_mapped()
    {
        var tex = UUID.Random();
        var p = Owned();
        p.Media = new LibreMetaverse.ParcelMedia
        {
            MediaURL = "https://example.org/stream.html",
            MediaType = "text/html",
            MediaDesc = "Our page",
            MediaID = tex,
            MediaWidth = 800,
            MediaHeight = 600,
            MediaAutoScale = true,
            MediaLoop = true,
        };

        var m = Map(p).Media;

        Assert.True(m.HasMedia);
        Assert.Equal("https://example.org/stream.html", m.Url);
        Assert.Equal("text/html", m.MimeType);
        Assert.Equal("Our page", m.Description);
        Assert.Equal(tex.Guid, m.TextureId);
        Assert.Equal(800, m.Width);
        Assert.Equal(600, m.Height);
        Assert.True(m.AutoScale);
        Assert.True(m.Loop);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("none/none")] // llmimetypes.cpp:47, the viewer's own "no type"
    public void An_empty_or_placeholder_mime_type_is_none(string wire)
    {
        var p = Owned();
        p.Media = new LibreMetaverse.ParcelMedia { MediaURL = "x", MediaType = wire };
        Assert.Null(Map(p).Media.MimeType);
    }

    [Fact]
    public void A_texture_alone_is_not_media_but_is_still_carried()
    {
        var tex = UUID.Random();
        var p = Owned();
        p.Media = new LibreMetaverse.ParcelMedia { MediaID = tex, MediaURL = string.Empty };
        var m = Map(p).Media;
        Assert.False(m.HasMedia);
        Assert.Equal(tex.Guid, m.TextureId);
    }

    // sound

    [Fact]
    public void Music_url_is_carried_verbatim()
    {
        var p = Owned();
        p.MusicURL = "http://radio.example:8000/live";
        Assert.Equal("http://radio.example:8000/live", Map(p).MusicUrl);
        p.MusicURL = string.Empty;
        Assert.Equal(string.Empty, Map(p).MusicUrl);
    }

    [Fact]
    public void Region_voice_is_unknown_until_region_flags_are_known()
        => Assert.Null(Map(Owned()).RegionVoiceEnabled);

    [Fact]
    public void Region_voice_follows_the_region_allow_voice_flag()
    {
        Assert.True(ParcelInfoMapper.From(Owned(), Region, SimAccess.PG, "", RegionFlags.AllowVoice).RegionVoiceEnabled);
        Assert.True(ParcelInfoMapper.From(Owned(), Region, SimAccess.PG, "",
            RegionFlags.AllowVoice | RegionFlags.BlockParcelSearch).RegionVoiceEnabled);
        Assert.False(ParcelInfoMapper.From(Owned(), Region, SimAccess.PG, "", RegionFlags.BlockParcelSearch).RegionVoiceEnabled);
    }

    [Fact]
    public void Obscure_moap_is_unknown_because_libremetaverse_drops_it()
    {
        Assert.Null(Map(Owned()).ObscureMoap);
        Assert.True(ParcelInfoMapper.From(Owned(), Region, SimAccess.PG, "", RegionFlags.None, obscureMoap: true).ObscureMoap);
    }

    // tracker: the new fields take part in de-duplication

    [Theory]
    [InlineData("flag")]
    [InlineData("see-avs")]
    [InlineData("route")]
    [InlineData("landing-point")]
    [InlineData("category")]
    [InlineData("snapshot")]
    [InlineData("media-url")]
    [InlineData("media-loop")]
    [InlineData("music")]
    public void A_change_to_any_new_field_is_not_swallowed_as_an_unchanged_repeat(string change)
    {
        var tracker = new ParcelInfoTracker();
        var p = Owned(7);
        Assert.NotNull(tracker.OnProperties(Map(p), solicited: false));
        Assert.Null(tracker.OnProperties(Map(p), solicited: false)); // sanity: an identical repeat IS swallowed

        switch (change)
        {
            case "flag": p.Flags |= ParcelFlags.AllowFly; break;
            case "see-avs": p.SeeAVs = !p.SeeAVs; break;
            case "route": p.Landing = LandingType.Direct; break;
            case "landing-point": p.UserLocation = new LibreMetaverse.Vector3(1f, 2f, 3f); break;
            case "category": p.Category = LibreMetaverse.ParcelCategory.Gaming; break;
            case "snapshot": p.SnapshotID = UUID.Random(); break;
            case "media-url": p.Media = new LibreMetaverse.ParcelMedia { MediaURL = "https://example.org" }; break;
            case "media-loop": p.Media = new LibreMetaverse.ParcelMedia { MediaLoop = true }; break;
            case "music": p.MusicURL = "http://radio.example"; break;
        }

        Assert.NotNull(tracker.OnProperties(Map(p), solicited: false));
    }

    [Fact]
    public void A_pure_flag_change_keeps_the_dwell_and_is_raised()
    {
        var tracker = new ParcelInfoTracker();
        var p = Owned(7);
        tracker.OnProperties(Map(p), solicited: false);
        tracker.OnDwell(Region, 7, Guid.NewGuid(), 42f);

        p.Flags = ParcelFlags.SoundLocal;
        var next = tracker.OnProperties(Map(p), solicited: false);

        Assert.NotNull(next);
        Assert.Equal(ParcelOptions.SoundLocal, next!.Options);
        Assert.Equal(42f, next.Dwell);
    }

    // ---- session without a connection ------------------------------------------------------------

    [Fact]
    public void Requests_on_a_session_that_never_logged_in_send_nothing_and_say_so()
    {
        using var session = new GridSession();
        var raised = new List<object>();
        session.ParcelInfoReceived += (_, i) => raised.Add(i);
        session.ParcelInfoFailed += (_, f) => raised.Add(f);

        Assert.False(session.RequestParcelInfoAt(10f, 10f));
        Assert.False(session.RequestParcelInfoHere());
        Assert.Null(session.LastParcelInfo);
        Assert.Empty(raised);
    }
}
