using LibreMetaverse;
using SLNG.Core;
using SLNG.Net;
using Xunit;
using ParcelInfo = SLNG.Core.ParcelInfo; // LibreMetaverse has a ParcelInfo too

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
