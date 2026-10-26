using SLNG.Core.Landmarks;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>FEAT-UI-72: the Favorites order, a port of the reference viewer's LLFavoritesSort.</summary>
public class FavoritesOrderTests
{
    private static readonly DateTime Day1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static LandmarkInventoryItem Lm(string name, DateTime? created = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, "Favorites", "Favorites", created ?? Day1);

    private static string[] Names(IEnumerable<LandmarkInventoryItem> items) => items.Select(i => i.Name).ToArray();

    [Fact]
    public void Indexed_items_sort_by_their_index()
    {
        var a = Lm("A"); var b = Lm("B"); var c = Lm("C");

        var sorted = FavoritesOrder.Sort(new[] { a, b, c }, new[] { c.Id, a.Id, b.Id });

        Assert.Equal(new[] { "C", "A", "B" }, Names(sorted));
    }

    [Fact]
    public void An_unindexed_item_sorts_before_the_indexed_ones()
    {
        var a = Lm("A"); var b = Lm("B"); var fresh = Lm("Fresh");

        var sorted = FavoritesOrder.Sort(new[] { a, b, fresh }, new[] { a.Id, b.Id });

        Assert.Equal(new[] { "Fresh", "A", "B" }, Names(sorted));
    }

    [Fact]
    public void Two_unindexed_items_sort_newest_first_then_by_name_ignoring_case()
    {
        var older = Lm("Zed", Day1);
        var newer = Lm("Alpha", Day1.AddDays(5));
        var sameB = Lm("bravo", Day1.AddDays(2));
        var sameA = Lm("Alpha2", Day1.AddDays(2));
        var sameC = Lm("Charlie", Day1.AddDays(2));

        var sorted = FavoritesOrder.Sort(new[] { older, sameC, newer, sameB, sameA }, Array.Empty<Guid>());

        // newest first; the three created on the same day by name, case-insensitively
        Assert.Equal(new[] { "Alpha", "Alpha2", "bravo", "Charlie", "Zed" }, Names(sorted));
    }

    [Fact]
    public void Entries_of_the_stored_order_that_no_longer_exist_are_ignored()
    {
        var a = Lm("A"); var b = Lm("B");
        var gone = Guid.NewGuid();

        var sorted = FavoritesOrder.Sort(new[] { b, a }, new[] { gone, a.Id, b.Id });

        Assert.Equal(new[] { "A", "B" }, Names(sorted));
    }

    [Fact]
    public void The_result_does_not_depend_on_the_input_order()
    {
        var items = Enumerable.Range(0, 8).Select(i => Lm("same", Day1)).ToList();

        var forward = FavoritesOrder.Sort(items, Array.Empty<Guid>()).Select(i => i.Id).ToList();
        var backward = FavoritesOrder.Sort(Enumerable.Reverse(items), Array.Empty<Guid>()).Select(i => i.Id).ToList();

        Assert.Equal(forward, backward);
    }

    [Fact]
    public void Pruned_drops_what_is_gone_and_repeats()
    {
        var a = Lm("A"); var b = Lm("B");
        var gone = Guid.NewGuid();

        var pruned = FavoritesOrder.Pruned(new[] { a.Id, gone, b.Id, a.Id }, new[] { a, b });

        Assert.Equal(new[] { a.Id, b.Id }, pruned);
    }

    [Fact]
    public void WithAdded_appends_once()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();

        Assert.Equal(new[] { a, b }, FavoritesOrder.WithAdded(new[] { a }, b));
        Assert.Equal(new[] { a, b }, FavoritesOrder.WithAdded(new[] { a, b }, b));
        Assert.Equal(new[] { a }, FavoritesOrder.WithAdded(new[] { a }, Guid.Empty));
    }

    [Fact]
    public void The_order_is_saved_when_the_shown_list_has_an_item_it_does_not_know()
    {
        var a = Lm("A"); var b = Lm("B");

        Assert.False(FavoritesOrder.NeedsToSaveOrder(new[] { a, b }, new[] { a.Id, b.Id }));
        Assert.False(FavoritesOrder.NeedsToSaveOrder(new[] { a }, new[] { a.Id, b.Id }));
        Assert.True(FavoritesOrder.NeedsToSaveOrder(new[] { a, b }, new[] { a.Id }));
        Assert.True(FavoritesOrder.NeedsToSaveOrder(new[] { a }, Array.Empty<Guid>()));
        Assert.False(FavoritesOrder.NeedsToSaveOrder(Array.Empty<LandmarkInventoryItem>(), Array.Empty<Guid>()));
    }
}
