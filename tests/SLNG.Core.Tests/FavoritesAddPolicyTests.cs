using SLNG.Core.Landmarks;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>FEAT-UI-72 phase 2: what may become a favorite.</summary>
public class FavoritesAddPolicyTests
{
    private static LandmarkInventoryItem Fav(Guid id, Guid asset) => new(id, Guid.NewGuid(), asset, "x", "Favorites", "Favorites");

    private static FavoriteAddVerdict Decide(
        bool folder = true, bool known = true, Guid? item = null, Guid? asset = null, bool copy = true,
        params LandmarkInventoryItem[] favorites) =>
        FavoritesAddPolicy.Decide(folder, known, item ?? Guid.NewGuid(), asset ?? Guid.NewGuid(), copy, favorites);

    [Fact]
    public void A_copyable_new_place_is_added()
    {
        Assert.Equal(FavoriteAddVerdict.Add, Decide());
    }

    [Fact]
    public void No_favorites_folder_means_no_add_and_wins_over_everything()
    {
        Assert.Equal(FavoriteAddVerdict.NoFolder, Decide(folder: false, known: false, copy: false));
    }

    [Fact]
    public void An_item_the_inventory_does_not_know_is_refused()
    {
        Assert.Equal(FavoriteAddVerdict.UnknownItem, Decide(known: false));
    }

    [Fact]
    public void An_item_already_in_the_folder_is_refused_even_when_it_cannot_be_copied()
    {
        var id = Guid.NewGuid();
        Assert.Equal(FavoriteAddVerdict.AlreadyInFolder, Decide(item: id, copy: false, favorites: Fav(id, Guid.NewGuid())));
    }

    [Fact]
    public void A_landmark_that_cannot_be_copied_is_refused()
    {
        Assert.Equal(FavoriteAddVerdict.NotCopyable, Decide(copy: false));
    }

    [Fact]
    public void A_second_landmark_for_the_same_place_is_refused()
    {
        var place = Guid.NewGuid();
        Assert.Equal(FavoriteAddVerdict.AssetAlreadyFavorite, Decide(asset: place, favorites: Fav(Guid.NewGuid(), place)));
    }

    [Fact]
    public void An_unknown_asset_id_is_never_a_duplicate()
    {
        // A landmark whose asset id is not known yet must not be refused because another one has no id either.
        Assert.Equal(FavoriteAddVerdict.Add, FavoritesAddPolicy.Decide(true, true, Guid.NewGuid(), Guid.Empty, true,
            new[] { Fav(Guid.NewGuid(), Guid.Empty) }));
    }

    [Fact]
    public void A_different_place_is_added_next_to_existing_favorites()
    {
        Assert.Equal(FavoriteAddVerdict.Add, Decide(favorites: Fav(Guid.NewGuid(), Guid.NewGuid())));
    }
}
