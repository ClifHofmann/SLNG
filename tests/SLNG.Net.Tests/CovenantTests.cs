using System.Text;
using LibreMetaverse;
using LibreMetaverse.Packets;
using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

// FEAT-LAND-05: the estate covenant. No network: the reply packet is built by hand and mapped through
// the same pure functions GridSession uses, the notecard asset is a byte array in the format the
// viewer's LLNotecard::importStream reads, and the merge rules are driven on the tracker directly.
public class CovenantTests
{
    private const ulong Region = 281474976710656UL;
    private const ulong OtherRegion = 281474976710912UL;

    private static byte[] Name(string s) => Encoding.UTF8.GetBytes(s + "\0"); // a Variable 1 string ends in NUL

    // ---- mapping ---------------------------------------------------------------------------------

    [Fact]
    public void Reply_with_a_covenant_maps_every_field_and_waits_for_the_text()
    {
        var covenant = Guid.NewGuid();
        var owner = Guid.NewGuid();

        var info = CovenantInfoMapper.From(Region, covenant, 1_700_000_000u, Name("My Estate"), owner);

        Assert.Equal(Region, info.RegionHandle);
        Assert.Equal("My Estate", info.EstateName);
        Assert.Equal(owner, info.EstateOwnerId);
        Assert.Equal(covenant, info.CovenantId);
        Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), info.TimestampUtc);
        Assert.Equal(DateTimeKind.Utc, info.TimestampUtc!.Value.Kind);
        Assert.Equal(CovenantTextState.Loading, info.TextState);
        Assert.Null(info.Text);
    }

    [Fact]
    public void Nil_covenant_id_means_none_set_and_there_is_nothing_to_load()
    {
        var info = CovenantInfoMapper.From(Region, Guid.Empty, 5u, Name("mainland"), Guid.NewGuid());

        Assert.Null(info.CovenantId);
        Assert.Equal(CovenantTextState.None, info.TextState);
        Assert.Null(info.Text);
    }

    [Fact]
    public void Timestamp_zero_is_never_not_the_unix_epoch()
    {
        var info = CovenantInfoMapper.From(Region, Guid.NewGuid(), 0u, Name("e"), Guid.NewGuid());
        Assert.Null(info.TimestampUtc);
    }

    [Fact]
    public void Timestamp_above_the_signed_range_is_still_a_date()
    {
        // The wire field is U32; the viewer casts it to S32 (which would go negative). Ours must not.
        var info = CovenantInfoMapper.From(Region, Guid.NewGuid(), uint.MaxValue, Name("e"), Guid.NewGuid());
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(uint.MaxValue), info.TimestampUtc);
    }

    [Fact]
    public void Estate_name_is_decoded_without_its_terminator_and_may_be_empty()
    {
        Assert.Equal("Östate ☆", CovenantInfoMapper.From(Region, Guid.Empty, 0, Name("Östate ☆"), Guid.Empty).EstateName);
        Assert.Equal(string.Empty, CovenantInfoMapper.From(Region, Guid.Empty, 0, Array.Empty<byte>(), Guid.Empty).EstateName);
        Assert.Equal(string.Empty, CovenantInfoMapper.From(Region, Guid.Empty, 0, null, Guid.Empty).EstateName);
    }

    [Fact]
    public void Mapping_from_the_real_packet_class_reads_the_same_fields()
    {
        var covenant = UUID.Random();
        var owner = UUID.Random();
        var packet = new EstateCovenantReplyPacket
        {
            Data =
            {
                CovenantID = covenant,
                CovenantTimestamp = 1_700_000_000u,
                EstateName = Name("Packet Estate"),
                EstateOwnerID = owner,
            },
        };

        var info = CovenantInfoMapper.From(packet, Region);

        Assert.Equal("Packet Estate", info.EstateName);
        Assert.Equal(covenant.Guid, info.CovenantId);
        Assert.Equal(owner.Guid, info.EstateOwnerId);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1_700_000_000), info.TimestampUtc);
    }

    // ---- the notecard asset ------------------------------------------------------------------------

    private static byte[] Notecard(string text, int version = 2, string embedded = "count 0\n") =>
        Encoding.UTF8.GetBytes(
            $"Linden text version {version}\n{{\nLLEmbeddedItems version 1\n{{\n{embedded}}}\nText length {Encoding.UTF8.GetByteCount(text)}\n{text}}}\n");

    [Fact]
    public void Plain_notecard_yields_its_text()
    {
        Assert.Equal("No fences.\nNo mining.", CovenantNotecard.Extract(Notecard("No fences.\nNo mining.")));
    }

    [Fact]
    public void Text_length_counts_bytes_not_characters()
    {
        Assert.Equal("Größe: 20 m × 20 m — Ünïcode ☆", CovenantNotecard.Extract(Notecard("Größe: 20 m × 20 m — Ünïcode ☆")));
    }

    [Fact]
    public void Empty_notecard_is_an_empty_string_not_a_failure()
    {
        Assert.Equal(string.Empty, CovenantNotecard.Extract(Notecard(string.Empty)));
    }

    [Fact]
    public void Embedded_items_are_skipped_and_their_placeholder_characters_dropped()
    {
        const string item =
            "{\next char index 0\ninv_item\t0\n{\nitem_id\t11111111-1111-1111-1111-111111111111\nname\tA landmark|\n}\n}\n";
        string text = "See \U00100000 for the map.";

        Assert.Equal("See  for the map.", CovenantNotecard.Extract(Notecard(text, embedded: "count 1\n" + item)));
    }

    [Fact]
    public void Version_1_header_is_accepted_like_the_viewer_does()
    {
        Assert.Equal("old", CovenantNotecard.Extract(Notecard("old", version: 1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a notecard at all")]
    [InlineData("Linden text version 3\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length 1\nx}\n")]
    [InlineData("Linden text version 2\n{\nLLEmbeddedItems version 2\n{\ncount 0\n}\nText length 1\nx}\n")]
    [InlineData("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length 99\nshort}\n")] // viewer: "Invalid text length"
    [InlineData("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText nonsense\nx}\n")]
    [InlineData("Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 2\n{\next char index 0\n")] // truncated
    public void Anything_else_is_a_format_error_not_a_guess(string body)
    {
        Assert.Null(CovenantNotecard.Extract(Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public void Null_or_empty_bytes_are_a_format_error()
    {
        Assert.Null(CovenantNotecard.Extract(null));
        Assert.Null(CovenantNotecard.Extract(Array.Empty<byte>()));
    }

    // ---- merge rules ---------------------------------------------------------------------------------

    private static CovenantInfo Header(Guid? covenant, ulong region = Region, uint stamp = 100u, string estate = "E") =>
        CovenantInfoMapper.From(region, covenant ?? Guid.Empty, stamp, Name(estate), Guid.Parse("00000000-0000-0000-0000-0000000000a1"));

    [Fact]
    public void A_header_with_a_covenant_is_raised_loading_and_asks_for_the_text()
    {
        var tracker = new CovenantInfoTracker();
        var cov = Guid.NewGuid();

        var (raised, needsText) = tracker.OnReply(Header(cov), solicited: true);

        Assert.Equal(CovenantTextState.Loading, raised!.TextState);
        Assert.True(needsText);
        Assert.Equal(raised, tracker.Current);
    }

    [Fact]
    public void A_header_without_a_covenant_needs_no_text()
    {
        var tracker = new CovenantInfoTracker();

        var (raised, needsText) = tracker.OnReply(Header(null), solicited: true);

        Assert.Equal(CovenantTextState.None, raised!.TextState);
        Assert.False(needsText);
    }

    [Fact]
    public void The_text_completes_the_record_it_belongs_to()
    {
        var tracker = new CovenantInfoTracker();
        var cov = Guid.NewGuid();
        tracker.OnReply(Header(cov), true);

        var done = tracker.OnText(Region, cov, "The rules.");

        Assert.Equal(CovenantTextState.Loaded, done!.TextState);
        Assert.Equal("The rules.", done.Text);
        Assert.Equal("E", done.EstateName);
        Assert.Equal(done, tracker.Current);
    }

    [Fact]
    public void A_failed_fetch_is_recorded_as_failed_not_as_empty()
    {
        var tracker = new CovenantInfoTracker();
        var cov = Guid.NewGuid();
        tracker.OnReply(Header(cov), true);

        var done = tracker.OnText(Region, cov, null);

        Assert.Equal(CovenantTextState.Failed, done!.TextState);
        Assert.Null(done.Text);
    }

    [Fact]
    public void A_late_text_for_another_covenant_or_region_is_dropped()
    {
        var tracker = new CovenantInfoTracker();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        tracker.OnReply(Header(first), true);
        tracker.OnReply(Header(second), true); // the estate changed its covenant meanwhile

        Assert.Null(tracker.OnText(Region, first, "stale"));
        Assert.Null(tracker.OnText(OtherRegion, second, "wrong region"));
        Assert.Equal(CovenantTextState.Loading, tracker.Current!.TextState);
    }

    [Fact]
    public void A_repeat_of_a_loaded_covenant_keeps_the_text_and_does_not_fetch_again()
    {
        var tracker = new CovenantInfoTracker();
        var cov = Guid.NewGuid();
        tracker.OnReply(Header(cov), true);
        tracker.OnText(Region, cov, "Kept.");

        var (raised, needsText) = tracker.OnReply(Header(cov), solicited: true);

        Assert.Equal(CovenantTextState.Loaded, raised!.TextState);
        Assert.Equal("Kept.", raised.Text);
        Assert.False(needsText);
    }

    [Fact]
    public void An_unsolicited_repeat_of_what_is_known_raises_nothing()
    {
        var tracker = new CovenantInfoTracker();
        var cov = Guid.NewGuid();
        tracker.OnReply(Header(cov), true);
        tracker.OnText(Region, cov, "Kept.");

        var (raised, needsText) = tracker.OnReply(Header(cov), solicited: false);

        Assert.Null(raised);
        Assert.False(needsText);
    }

    [Fact]
    public void A_new_timestamp_or_covenant_is_news_even_unsolicited()
    {
        var tracker = new CovenantInfoTracker();
        var cov = Guid.NewGuid();
        tracker.OnReply(Header(cov, stamp: 100u), true);
        tracker.OnText(Region, cov, "Old.");

        var (changed, needsText) = tracker.OnReply(Header(Guid.NewGuid(), stamp: 200u), solicited: false);

        Assert.NotNull(changed);
        Assert.True(needsText);
        Assert.Equal(CovenantTextState.Loading, changed!.TextState);
        Assert.Null(changed.Text);
    }

    [Fact]
    public void A_second_reply_while_the_text_is_on_its_way_does_not_start_a_second_fetch()
    {
        var tracker = new CovenantInfoTracker();
        var cov = Guid.NewGuid();
        Assert.True(tracker.OnReply(Header(cov), true).NeedsText);

        var (raised, needsText) = tracker.OnReply(Header(cov), solicited: true);

        Assert.NotNull(raised); // a request is always answered...
        Assert.False(needsText); // ...but the running fetch is not duplicated
    }

    [Fact]
    public void A_refresh_after_a_failed_fetch_tries_again()
    {
        var tracker = new CovenantInfoTracker();
        var cov = Guid.NewGuid();
        tracker.OnReply(Header(cov), true);
        tracker.OnText(Region, cov, null);

        var (raised, needsText) = tracker.OnReply(Header(cov), solicited: true);

        Assert.Equal(CovenantTextState.Loading, raised!.TextState);
        Assert.True(needsText);
    }

    [Fact]
    public void Reset_forgets_everything()
    {
        var tracker = new CovenantInfoTracker();
        tracker.OnReply(Header(Guid.NewGuid()), true);

        tracker.Reset();

        Assert.Null(tracker.Current);
    }

    // ---- the session ---------------------------------------------------------------------------------

    [Fact]
    public void Without_a_connection_nothing_is_sent_raised_or_remembered()
    {
        var session = new GridSession();
        var raised = new List<object>();
        session.CovenantReceived += (_, i) => raised.Add(i);
        session.CovenantFailed += (_, f) => raised.Add(f);

        Assert.False(session.RequestCovenant());
        Assert.Null(session.LastCovenant);
        Assert.Empty(raised);
    }
}
