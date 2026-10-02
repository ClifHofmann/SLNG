using LibreMetaverse;
using LibreMetaverse.Packets;
using SLNG.Core;
using SLNG.Net;
using Xunit;
using ParcelInfo = SLNG.Core.ParcelInfo; // LibreMetaverse has a ParcelInfo too

namespace SLNG.Net.Tests;

// FEAT-LAND-03: the Land-Info Objects tab. No network: the owner-list reply is built by hand in both forms it
// arrives in (the UDP packet OpenSim sends, the LibreMetaverse event Second Life's EventQueue form becomes) and
// mapped through the same pure functions GridSession uses; the request/reply rules are driven on the tracker, and
// the prim counts through the same mapper as every other parcel field.
public class ObjectOwnersTests
{
    private const ulong Region = 281474976710656UL;
    private const ulong OtherRegion = 281474976710912UL;

    private static OwnerRow Row(Guid id, int count = 1, bool group = false, bool online = false, DateTime? newest = null)
        => new(id, group, count, online, newest);

    // ---- decoding the reply ----------------------------------------------------------------------

    [Fact]
    public void Udp_packet_rows_map_every_field()
    {
        var a = UUID.Random();
        var g = UUID.Random();
        var packet = new ParcelObjectOwnersReplyPacket
        {
            Data = new[]
            {
                new ParcelObjectOwnersReplyPacket.DataBlock { OwnerID = a, IsGroupOwned = false, Count = 12, OnlineStatus = true },
                new ParcelObjectOwnersReplyPacket.DataBlock { OwnerID = g, IsGroupOwned = true, Count = 3, OnlineStatus = false },
            },
        };

        var (owners, withheld) = ParcelObjectOwnersMapper.Decode(ParcelObjectOwnersMapper.RowsFrom(packet));

        Assert.False(withheld);
        Assert.Equal(2, owners.Count);
        Assert.Equal(new ParcelObjectOwner(a.Guid, false, 12, true, null), owners[0]);
        Assert.Equal(new ParcelObjectOwner(g.Guid, true, 3, false, null), owners[1]);
    }

    [Fact]
    public void Udp_packet_survives_a_wire_round_trip()
    {
        // The packet is built, serialised and parsed again: what the sim sends is what is decoded.
        var a = UUID.Random();
        var packet = new ParcelObjectOwnersReplyPacket
        {
            Data = new[] { new ParcelObjectOwnersReplyPacket.DataBlock { OwnerID = a, IsGroupOwned = true, Count = 99, OnlineStatus = true } },
        };
        packet.Header.Zerocoded = false;
        packet.Header.Sequence = 1;
        byte[] sent = packet.ToBytes();
        byte[] buffer = new byte[4096]; // the receive buffer is bigger than the datagram
        Array.Copy(sent, buffer, sent.Length);
        int len = sent.Length;
        var parsed = (ParcelObjectOwnersReplyPacket)Packet.BuildPacket(buffer, ref len, new byte[4096]);

        var (owners, _) = ParcelObjectOwnersMapper.Decode(ParcelObjectOwnersMapper.RowsFrom(parsed));

        Assert.Single(owners);
        Assert.Equal(a.Guid, owners[0].OwnerId);
        Assert.True(owners[0].IsGroupOwned);
        Assert.Equal(99, owners[0].Count);
    }

    [Fact]
    public void Packet_without_rows_is_an_empty_list_not_an_error()
    {
        var empty = new ParcelObjectOwnersReplyPacket { Data = Array.Empty<ParcelObjectOwnersReplyPacket.DataBlock>() };
        var (owners, withheld) = ParcelObjectOwnersMapper.Decode(ParcelObjectOwnersMapper.RowsFrom(empty));
        Assert.Empty(owners);
        Assert.False(withheld);

        var nullData = new ParcelObjectOwnersReplyPacket { Data = null! };
        Assert.Empty(ParcelObjectOwnersMapper.RowsFrom(nullData));
    }

    [Fact]
    public void Event_rows_carry_the_most_recent_time_as_utc()
    {
        var a = UUID.Random();
        var stamp = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Unspecified); // LibreMetaverse's unix-seconds dates
        var rows = ParcelObjectOwnersMapper.RowsFrom(new[]
        {
            new ParcelManager.ParcelPrimOwners { OwnerID = a, Count = 5, IsGroupOwned = false, OnlineStatus = false, NewestPrim = stamp },
        });

        var (owners, _) = ParcelObjectOwnersMapper.Decode(rows);

        Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc), owners[0].NewestUtc);
        Assert.Equal(DateTimeKind.Utc, owners[0].NewestUtc!.Value.Kind);
    }

    [Theory]
    [InlineData(0)]   // the epoch: "DataExtended" carried a zero
    [InlineData(-1)]  // default(DateTime): LibreMetaverse's value when there was no DataExtended block
    public void No_most_recent_time_is_null_not_1970(int marker)
    {
        var stamp = marker == 0 ? DateTime.UnixEpoch : default;
        Assert.Null(ParcelObjectOwnersMapper.NewestUtc(stamp));
    }

    [Fact]
    public void Nil_owner_rows_are_dropped_but_remembered_as_withheld()
    {
        // LibreMetaverse's documented Second Life answer to an agent without the right: one row, nil owner.
        var (owners, withheld) = ParcelObjectOwnersMapper.Decode(new[] { Row(Guid.Empty, 0) });

        Assert.Empty(owners);
        Assert.True(withheld);
    }

    [Fact]
    public void A_nil_row_next_to_real_ones_is_dropped_without_hiding_them()
    {
        var a = Guid.NewGuid();
        var (owners, withheld) = ParcelObjectOwnersMapper.Decode(new[] { Row(Guid.Empty), Row(a, 4) });

        Assert.Single(owners);
        Assert.Equal(a, owners[0].OwnerId);
        Assert.True(withheld);
    }

    [Fact]
    public void A_negative_count_is_shown_as_zero()
    {
        var (owners, _) = ParcelObjectOwnersMapper.Decode(new[] { Row(Guid.NewGuid(), -5) });
        Assert.Equal(0, owners[0].Count);
    }

    [Fact]
    public void The_same_owner_twice_in_one_reply_keeps_one_row_with_the_last_values()
    {
        var a = Guid.NewGuid();
        var (owners, _) = ParcelObjectOwnersMapper.Decode(new[] { Row(a, 3), Row(Guid.NewGuid(), 1), Row(a, 9) });

        Assert.Equal(2, owners.Count);
        Assert.Equal(a, owners[0].OwnerId); // kept its place
        Assert.Equal(9, owners[0].Count);
    }

    // ---- merging replies --------------------------------------------------------------------------

    [Fact]
    public void First_reply_starts_the_list_over_and_later_ones_append()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var stale = ParcelObjectOwnersMapper.Merge(null, Region, 7, new[] { Row(Guid.NewGuid(), 50) }, replace: true);

        var first = ParcelObjectOwnersMapper.Merge(stale, Region, 7, new[] { Row(a, 1) }, replace: true);
        Assert.Equal(new[] { a }, first.Owners.Select(o => o.OwnerId));

        var second = ParcelObjectOwnersMapper.Merge(first, Region, 7, new[] { Row(b, 2) }, replace: false);
        Assert.Equal(new[] { a, b }, second.Owners.Select(o => o.OwnerId));
        Assert.Equal(7, second.LocalId);
        Assert.Equal(Region, second.RegionHandle);
    }

    [Fact]
    public void A_reply_that_arrives_twice_changes_nothing()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var rows = new[] { Row(a, 1), Row(b, 2) };
        var once = ParcelObjectOwnersMapper.Merge(null, Region, 7, rows, replace: true);

        var twice = ParcelObjectOwnersMapper.Merge(once, Region, 7, rows, replace: false);

        Assert.Equal(once.Owners, twice.Owners);
    }

    [Fact]
    public void A_later_row_for_a_known_owner_takes_the_newer_count_in_the_same_place()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var held = ParcelObjectOwnersMapper.Merge(null, Region, 7, new[] { Row(a, 1), Row(b, 2) }, replace: true);

        var merged = ParcelObjectOwnersMapper.Merge(held, Region, 7, new[] { Row(a, 8) }, replace: false);

        Assert.Equal(new[] { a, b }, merged.Owners.Select(o => o.OwnerId));
        Assert.Equal(8, merged.Owners[0].Count);
    }

    [Fact]
    public void Withheld_survives_a_later_reply()
    {
        var held = ParcelObjectOwnersMapper.Merge(null, Region, 7, new[] { Row(Guid.Empty, 0) }, replace: true);
        var merged = ParcelObjectOwnersMapper.Merge(held, Region, 7, new[] { Row(Guid.NewGuid(), 2) }, replace: false);
        Assert.True(merged.OwnersWithheld);
    }

    // ---- tracker: request, reply, timeout ---------------------------------------------------------

    [Fact]
    public void A_request_is_sent_and_a_second_for_the_same_parcel_while_waiting_is_not()
    {
        var t = new ParcelObjectOwnersTracker();

        var (send1, serial1) = t.Begin(Region, 7);
        var (send2, serial2) = t.Begin(Region, 7);

        Assert.True(send1);
        Assert.False(send2);       // Refresh pressed twice quickly: one request
        Assert.Equal(serial1, serial2);
    }

    [Fact]
    public void After_the_first_reply_the_next_request_is_sent_again()
    {
        var t = new ParcelObjectOwnersTracker();
        t.Begin(Region, 7);
        t.OnReply(Region, new[] { Row(Guid.NewGuid()) });

        Assert.True(t.Begin(Region, 7).Send);
    }

    [Fact]
    public void After_a_timeout_the_next_request_is_sent_again()
    {
        var t = new ParcelObjectOwnersTracker();
        int serial = t.Begin(Region, 7).Serial;
        Assert.NotNull(t.OnTimeout(serial));

        Assert.True(t.Begin(Region, 7).Send);
    }

    [Fact]
    public void A_request_for_another_parcel_is_sent_even_while_one_waits_and_orphans_the_old_timeout()
    {
        var t = new ParcelObjectOwnersTracker();
        int old = t.Begin(Region, 7).Serial;

        var (send, serial) = t.Begin(Region, 8);

        Assert.True(send);
        Assert.NotEqual(old, serial);
        Assert.Null(t.OnTimeout(old));            // the old wait no longer reports a failure
        Assert.NotNull(t.OnTimeout(serial));
    }

    [Fact]
    public void Replies_are_filed_under_the_requested_parcel_with_first_replacing_and_later_appending()
    {
        var t = new ParcelObjectOwnersTracker();
        t.Begin(Region, 7);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var first = t.OnReply(Region, new[] { Row(a, 1) });
        var second = t.OnReply(Region, new[] { Row(b, 2) });

        Assert.Equal((Region, 7), (first!.RegionHandle, first.LocalId));
        Assert.Single(first.Owners);
        Assert.Equal(new[] { a, b }, second!.Owners.Select(o => o.OwnerId)); // cumulative
        Assert.Same(second, t.Current);
    }

    [Fact]
    public void A_new_request_clears_the_list_of_the_old_one()
    {
        var t = new ParcelObjectOwnersTracker();
        t.Begin(Region, 7);
        t.OnReply(Region, new[] { Row(Guid.NewGuid()) });

        t.Begin(Region, 7);
        Assert.Null(t.Current);

        var again = t.OnReply(Region, new[] { Row(Guid.NewGuid(), 3) });
        Assert.Single(again!.Owners); // not appended to the previous request's list
    }

    [Fact]
    public void A_reply_with_no_request_is_dropped()
        => Assert.Null(new ParcelObjectOwnersTracker().OnReply(Region, new[] { Row(Guid.NewGuid()) }));

    [Fact]
    public void A_reply_from_another_region_is_dropped()
    {
        var t = new ParcelObjectOwnersTracker();
        t.Begin(Region, 7);

        Assert.Null(t.OnReply(OtherRegion, new[] { Row(Guid.NewGuid()) }));
        Assert.Null(t.Current);
    }

    [Fact]
    public void A_timeout_after_the_reply_does_nothing()
    {
        var t = new ParcelObjectOwnersTracker();
        int serial = t.Begin(Region, 7).Serial;
        t.OnReply(Region, Array.Empty<OwnerRow>());

        Assert.Null(t.OnTimeout(serial)); // answered: no failure
    }

    [Fact]
    public void An_empty_reply_is_an_answer_not_a_failure()
    {
        var t = new ParcelObjectOwnersTracker();
        int serial = t.Begin(Region, 7).Serial;

        var list = t.OnReply(Region, Array.Empty<OwnerRow>());

        Assert.NotNull(list);
        Assert.Empty(list!.Owners);
        Assert.False(list.OwnersWithheld);
        Assert.Null(t.OnTimeout(serial));
    }

    [Fact]
    public void A_timeout_reports_the_parcel_that_was_asked_about_and_only_once()
    {
        var t = new ParcelObjectOwnersTracker();
        int serial = t.Begin(Region, 7).Serial;

        Assert.Equal((Region, 7), t.OnTimeout(serial));
        Assert.Null(t.OnTimeout(serial));
    }

    [Fact]
    public void A_late_reply_after_the_timeout_still_lands_as_the_list()
    {
        var t = new ParcelObjectOwnersTracker();
        int serial = t.Begin(Region, 7).Serial;
        t.OnTimeout(serial);

        var a = Guid.NewGuid();
        var list = t.OnReply(Region, new[] { Row(a, 2) });

        Assert.Equal(new[] { a }, list!.Owners.Select(o => o.OwnerId));
    }

    [Fact]
    public void Reset_forgets_the_request_and_the_list()
    {
        var t = new ParcelObjectOwnersTracker();
        t.Begin(Region, 7);
        t.OnReply(Region, new[] { Row(Guid.NewGuid()) });

        t.Reset();

        Assert.Null(t.Current);
        Assert.Null(t.OnReply(Region, new[] { Row(Guid.NewGuid()) }));
    }

    // ---- the counts that ride in ParcelProperties --------------------------------------------------

    private static readonly UUID Landlord = UUID.Random();

    private static Parcel Counted() => new(7)
    {
        Name = "P",
        OwnerID = Landlord,
        Area = 4096,
        OwnerPrims = 10,
        GroupPrims = 20,
        OtherPrims = 30,
        MaxPrims = 100,
        ParcelPrimBonus = 1.5f,
        SimWideMaxPrims = 400,
        SimWideTotalPrims = 120,
        OtherCleanTime = 15,
    };

    [Fact]
    public void Counts_are_mapped_field_for_field_with_selected_prims_from_the_event()
    {
        var info = ParcelInfoMapper.From(Counted(), Region, SimAccess.PG, "", selectedPrims: 4);

        var c = info.Prims;
        Assert.Equal(10, c.OwnerPrims);
        Assert.Equal(20, c.GroupPrims);
        Assert.Equal(30, c.OtherPrims);
        Assert.Equal(4, c.SelectedPrims);
        Assert.Equal(100, c.MaxPrims);
        Assert.Equal(1.5f, c.ParcelPrimBonus);
        Assert.Equal(400, c.SimWideMaxPrims);
        Assert.Equal(120, c.SimWideTotalPrims);
        Assert.Equal(15, c.AutoReturnMinutes);
        Assert.Equal(0, c.RegionObjectCapacity);
    }

    [Fact]
    public void Total_is_owner_group_other_and_selected_not_the_wire_total()
    {
        var p = Counted();
        p.TotalPrims = 9999; // the viewer ignores this field (llviewerparcelmgr.cpp:1660)

        var c = ParcelInfoMapper.From(p, Region, SimAccess.PG, "", selectedPrims: 4).Prims;

        Assert.Equal(10 + 20 + 30 + 4, c.TotalPrims);
    }

    [Fact]
    public void Parcel_capacity_is_the_allowance_times_the_bonus_rounded_half_up()
    {
        var p = Counted();
        p.MaxPrims = 117;
        p.ParcelPrimBonus = 1.5f; // 175.5 -> 176 (ll_round), the example in LibreMetaverse's own docs says 175
        Assert.Equal(176, ParcelInfoMapper.From(p, Region, SimAccess.PG, "").Prims.ParcelCapacity);

        p.ParcelPrimBonus = 1f;
        Assert.Equal(117, ParcelInfoMapper.From(p, Region, SimAccess.PG, "").Prims.ParcelCapacity);
    }

    [Fact]
    public void Capacities_are_cut_to_the_regions_object_capacity_when_it_is_known()
    {
        var p = Counted();
        p.MaxPrims = 15000;
        p.ParcelPrimBonus = 2f;
        p.SimWideMaxPrims = 30000;

        var unknown = ParcelInfoMapper.From(p, Region, SimAccess.PG, "").Prims;
        var known = ParcelInfoMapper.From(p, Region, SimAccess.PG, "", regionObjectCapacity: 20000).Prims;

        Assert.Equal(30000, unknown.ParcelCapacity); // no ceiling while it is not known
        Assert.Equal(30000, unknown.RegionCapacity);
        Assert.Equal(20000, known.ParcelCapacity);
        Assert.Equal(20000, known.RegionCapacity);
    }

    [Fact]
    public void Region_available_and_over_by_follow_the_viewers_two_sentences()
    {
        var under = new ParcelPrimCounts { SimWideMaxPrims = 400, SimWideTotalPrims = 120 };
        Assert.Equal(280, under.RegionAvailable);
        Assert.Equal(0, under.RegionOverBy);

        var over = new ParcelPrimCounts { SimWideMaxPrims = 400, SimWideTotalPrims = 450 };
        Assert.Equal(0, over.RegionAvailable);
        Assert.Equal(50, over.RegionOverBy); // "[COUNT] out of [MAX] ([DELETED] will be deleted)"
    }

    [Fact]
    public void An_absent_bonus_is_one_and_negative_counts_are_zero()
    {
        var p = Counted();
        p.ParcelPrimBonus = 0f; // a field the sim left out decodes to 0
        p.OwnerPrims = -3;

        var c = ParcelInfoMapper.From(p, Region, SimAccess.PG, "").Prims;

        Assert.Equal(1f, c.ParcelPrimBonus);
        Assert.False(c.HasBonus);
        Assert.Equal(0, c.OwnerPrims);
        Assert.True(ParcelInfoMapper.From(Counted(), Region, SimAccess.PG, "").Prims.HasBonus);
    }

    [Fact]
    public void A_changed_count_is_not_swallowed_as_an_unchanged_repeat()
    {
        // An identical unsolicited push is dropped...
        var tracker = new ParcelInfoTracker();
        Assert.NotNull(tracker.OnProperties(ParcelInfoMapper.From(Counted(), Region, SimAccess.PG, ""), solicited: false));
        Assert.Null(tracker.OnProperties(ParcelInfoMapper.From(Counted(), Region, SimAccess.PG, ""), solicited: false));

        // ...but one whose ONLY difference is a count is raised, for each count.
        foreach (var change in new Action<Parcel>[]
        {
            p => p.OwnerPrims++, p => p.GroupPrims++, p => p.OtherPrims++, p => p.MaxPrims++,
            p => p.SimWideTotalPrims++, p => p.SimWideMaxPrims++, p => p.OtherCleanTime++, p => p.ParcelPrimBonus = 2f,
        })
        {
            var t = new ParcelInfoTracker();
            t.OnProperties(ParcelInfoMapper.From(Counted(), Region, SimAccess.PG, ""), solicited: false);

            var changed = Counted();
            change(changed);
            Assert.NotNull(t.OnProperties(ParcelInfoMapper.From(changed, Region, SimAccess.PG, ""), solicited: false));
        }

        // Selected prims are not in LibreMetaverse's Parcel; they come beside it, and they count too.
        var s = new ParcelInfoTracker();
        s.OnProperties(ParcelInfoMapper.From(Counted(), Region, SimAccess.PG, "", selectedPrims: 0), solicited: false);
        Assert.NotNull(s.OnProperties(ParcelInfoMapper.From(Counted(), Region, SimAccess.PG, "", selectedPrims: 1), solicited: false));
    }

    [Fact]
    public void Region_capacity_arriving_later_updates_the_held_parcel_once()
    {
        var tracker = new ParcelInfoTracker();
        tracker.OnProperties(ParcelInfoMapper.From(Counted(), Region, SimAccess.PG, ""), solicited: true);

        var merged = tracker.OnRegionCapacity(Region, 15000);

        Assert.NotNull(merged);
        Assert.Equal(15000, merged!.Prims.RegionObjectCapacity);
        Assert.Equal(10, merged.Prims.OwnerPrims); // everything else is kept
        Assert.Null(tracker.OnRegionCapacity(Region, 15000));   // SimStats repeats every second: not raised again
        Assert.Null(tracker.OnRegionCapacity(OtherRegion, 20000)); // another region
        Assert.Null(tracker.OnRegionCapacity(Region, 0));       // 0 is "unknown"
        Assert.Equal(15000, tracker.Current!.Prims.RegionObjectCapacity);
    }

    [Fact]
    public void A_parcel_info_without_counts_has_none_and_a_bonus_of_one()
    {
        var info = new ParcelInfo();
        Assert.Equal(ParcelPrimCounts.None, info.Prims);
        Assert.Equal(1f, info.Prims.ParcelPrimBonus);
        Assert.Equal(0, info.Prims.TotalPrims);
    }
}
