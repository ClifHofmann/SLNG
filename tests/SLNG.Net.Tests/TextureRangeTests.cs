using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-PERF-29: the Range request a streamed texture asks for, and what a response says about the
/// asset. "Complete" decides whether the bytes are promoted to the whole-asset cache file, so a
/// wrong answer either keeps refetching an asset that is all there or caches a prefix as a whole one.
/// </summary>
public class TextureRangeTests
{
    [Fact]
    public void First_request_starts_at_zero_and_asks_for_the_wanted_bytes()
    {
        var (start, end) = TextureRange.For(heldBytes: 0, wantedBytes: 18840);

        Assert.Equal(0, start);
        Assert.Equal(18839, end);
    }

    [Fact]
    public void Refinement_starts_one_byte_before_the_held_bytes()
    {
        // The reference viewer asks from the current size minus one, so the range is always partly
        // satisfiable and a cache such as Varnish never answers with the whole asset
        // (lltexturefetch.cpp:1428-1441).
        var (start, end) = TextureRange.For(heldBytes: 18840, wantedBytes: 49560);

        Assert.Equal(18839, start);
        Assert.Equal(49559, end);
    }

    [Fact]
    public void Wanting_everything_leaves_the_range_open()
    {
        var (start, end) = TextureRange.For(heldBytes: 49560, wantedBytes: 0);

        Assert.Equal(49559, start);
        Assert.Null(end);
    }

    [Fact]
    public void Partial_content_with_a_total_is_incomplete_until_the_total_is_reached()
    {
        var partial = TextureRange.Interpret(206, rangeStart: 0, rangeTotal: 400_000, requestStart: 0, requestEnd: 18839, new byte[18840]);
        Assert.NotNull(partial);
        Assert.False(partial!.Value.Complete);
        Assert.Equal(0, partial.Value.DataOffset);
        Assert.Equal(400_000, partial.Value.TotalSize);

        var last = TextureRange.Interpret(206, rangeStart: 399_999, rangeTotal: 400_000, requestStart: 399_999, requestEnd: 500_000, new byte[1]);
        Assert.True(last!.Value.Complete);
        Assert.Equal(399_999, last.Value.DataOffset);
    }

    [Fact]
    public void Partial_content_without_a_range_header_is_complete_only_when_it_came_back_short()
    {
        var full = TextureRange.Interpret(206, rangeStart: null, rangeTotal: null, requestStart: 0, requestEnd: 18839, new byte[18840]);
        Assert.False(full!.Value.Complete);
        Assert.Equal(-1, full.Value.TotalSize);

        var shortBody = TextureRange.Interpret(206, rangeStart: null, rangeTotal: null, requestStart: 0, requestEnd: 18839, new byte[9000]);
        Assert.True(shortBody!.Value.Complete);
    }

    [Fact]
    public void An_open_ended_request_answered_without_a_total_is_complete()
    {
        var rest = TextureRange.Interpret(206, rangeStart: 49559, rangeTotal: null, requestStart: 49559, requestEnd: null, new byte[300_000]);

        Assert.True(rest!.Value.Complete);
        Assert.Equal(49559, rest.Value.DataOffset);
    }

    [Fact]
    public void A_200_is_the_whole_asset_from_byte_zero_whatever_was_asked()
    {
        // A server that ignores Range answers 200 with everything: complete, and it replaces what was held.
        var whole = TextureRange.Interpret(200, rangeStart: null, rangeTotal: null, requestStart: 18839, requestEnd: 49559, new byte[123_456]);

        Assert.True(whole!.Value.Complete);
        Assert.Equal(0, whole.Value.DataOffset);
        Assert.Equal(123_456, whole.Value.TotalSize);
    }

    [Fact]
    public void Range_not_satisfiable_means_the_held_bytes_were_already_everything()
    {
        var none = TextureRange.Interpret(416, rangeStart: null, rangeTotal: 18840, requestStart: 18840, requestEnd: 49559, System.Array.Empty<byte>());

        Assert.True(none!.Value.Complete);
        Assert.Empty(none.Value.Data);
        Assert.Equal(18840, none.Value.DataOffset);
        Assert.Equal(18840, none.Value.TotalSize);
    }

    [Fact]
    public void Any_other_status_is_not_a_range_answer()
    {
        Assert.Null(TextureRange.Interpret(404, null, null, 0, 18839, System.Array.Empty<byte>()));
        Assert.Null(TextureRange.Interpret(503, null, null, 0, 18839, System.Array.Empty<byte>()));
    }
}
