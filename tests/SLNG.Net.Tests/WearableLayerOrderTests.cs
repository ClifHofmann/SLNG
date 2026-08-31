using System.Collections.Generic;
using System.Linq;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-AVATAR-01: pins the layer order Second Life stacks same-type wearables in, taken from
/// <c>llappearancemgr.cpp</c>'s <c>build_order_string</c> and <c>WearablesOrderComparator</c>.
///
/// <para>This decides what an avatar's face looks like, not just a draw order. Measured in-world
/// 2026-08-31: five worn Tattoo layers, two of them fully opaque head skins. LibreMetaverse's
/// <c>GetWearables()</c> put the one a working viewer shows at the <i>bottom</i>, so the bake came
/// out as two different faces blended together.</para>
/// </summary>
public class WearableLayerOrderTests
{
    private const int Tattoo = 20;

    private sealed record Item(string Name, string? Description);

    private static List<string> Order(int type, params Item[] items)
        => WearableLayerOrder.Sort(items, type, i => i.Description, i => i.Name)
            .Select(i => i.Name).ToList();

    /// <summary>The token the viewer writes: '@' followed by <c>type * 100 + index</c>.</summary>
    [Theory]
    [InlineData(Tattoo, 0, "@2000")]
    [InlineData(Tattoo, 3, "@2003")]
    [InlineData(0, 0, "@0")]      // Shape
    [InlineData(5, 2, "@502")]    // Jacket
    public void Order_string_matches_the_viewers_format(int type, int index, string expected)
        => Assert.Equal(expected, WearableLayerOrder.BuildOrderString(type, index));

    /// <summary>Layers come out bottom-first in token order — and index 0 is the bottom, per
    /// <c>LLTexLayerTemplate</c>: "For rendering morph masks, we only want to use the top wearable"
    /// reads <c>num_wearables - 1</c>. So the last entry here is what wins where layers overlap.</summary>
    [Fact]
    public void Valid_tokens_sort_bottom_layer_first()
    {
        var order = Order(Tattoo,
            new Item("third", "@2002"),
            new Item("first", "@2000"),
            new Item("second", "@2001"));

        Assert.Equal(new[] { "first", "second", "third" }, order);
    }

    /// <summary>The comparator requires the token to be exactly as long as the type's own — that
    /// length check is what stops a token belonging to another wearable type from being compared as
    /// if it were this one's.</summary>
    [Fact]
    public void A_token_of_the_wrong_length_is_not_valid_for_this_type()
    {
        Assert.True(WearableLayerOrder.IsValidOrderString("@2000", Tattoo));
        Assert.False(WearableLayerOrder.IsValidOrderString("@500", Tattoo));   // too short
        Assert.False(WearableLayerOrder.IsValidOrderString("@20000", Tattoo)); // too long
        Assert.False(WearableLayerOrder.IsValidOrderString("2000", Tattoo));   // no separator
        Assert.False(WearableLayerOrder.IsValidOrderString("", Tattoo));
        Assert.False(WearableLayerOrder.IsValidOrderString(null, Tattoo));
    }

    /// <summary>"we need to sink down invalid items": anything without a usable token goes below
    /// everything that has one, whatever its name.</summary>
    [Fact]
    public void Untokened_layers_sink_below_tokened_ones()
    {
        var order = Order(Tattoo,
            new Item("aaa-no-token", null),
            new Item("zzz-tokened", "@2001"),
            new Item("bbb-broken", "Broken link"),
            new Item("yyy-tokened", "@2000"));

        Assert.Equal(new[] { "yyy-tokened", "zzz-tokened", "aaa-no-token", "bbb-broken" }, order);
    }

    /// <summary>With no usable tokens the viewer falls back to the item name.</summary>
    [Fact]
    public void Untokened_layers_fall_back_to_name_order()
    {
        var order = Order(Tattoo,
            new Item("charlie", ""),
            new Item("alpha", null),
            new Item("bravo", "nonsense"));

        Assert.Equal(new[] { "alpha", "bravo", "charlie" }, order);
    }

    /// <summary>Ordinal comparison, as the viewer's <c>std::string</c> comparison is. A
    /// culture-aware sort can order these differently on some machines, which would silently change
    /// which face an avatar wears depending on where the client runs.</summary>
    [Fact]
    public void Sorting_is_ordinal_not_culture_sensitive()
    {
        var order = Order(Tattoo,
            new Item("b", null),
            new Item("A", null),
            new Item("a", null));

        // Ordinal puts uppercase before lowercase; a culture-aware sort would interleave them.
        Assert.Equal(new[] { "A", "a", "b" }, order);
    }

    /// <summary>String comparison is safe for a high-numbered type: Tattoo is 20, so every layer
    /// from 0 to 99 yields a five-character token and sorting them as text matches sorting them as
    /// numbers.</summary>
    [Fact]
    public void Token_order_is_numeric_while_the_digit_count_holds()
    {
        var order = Order(Tattoo,
            new Item("layer10", WearableLayerOrder.BuildOrderString(Tattoo, 10)),
            new Item("layer2", WearableLayerOrder.BuildOrderString(Tattoo, 2)));

        Assert.Equal(new[] { "layer2", "layer10" }, order);
    }

    /// <summary>Where the digit count does change, the viewer's own length check takes over: for
    /// Shape (type 0) the control token is "@0", so "@10" is the wrong length and sinks below the
    /// valid ones instead of sorting among them. Recorded rather than corrected — matching the
    /// viewer means inheriting this, and a "fix" here would order layers differently than the grid
    /// does.</summary>
    [Fact]
    public void A_type_whose_token_changes_width_sinks_the_longer_ones()
    {
        const int shape = 0;
        Assert.True(WearableLayerOrder.IsValidOrderString(WearableLayerOrder.BuildOrderString(shape, 1), shape));
        Assert.False(WearableLayerOrder.IsValidOrderString(WearableLayerOrder.BuildOrderString(shape, 10), shape));

        var order = Order(shape,
            new Item("eleventh", WearableLayerOrder.BuildOrderString(shape, 10)),
            new Item("second", WearableLayerOrder.BuildOrderString(shape, 1)));

        Assert.Equal(new[] { "second", "eleventh" }, order);
    }

    [Fact]
    public void Sorting_keeps_every_layer()
    {
        var items = Enumerable.Range(0, 5)
            .Select(i => new Item($"item{i}", i % 2 == 0 ? $"@200{i}" : null)).ToArray();

        Assert.Equal(5, Order(Tattoo, items).Count);
    }
}
