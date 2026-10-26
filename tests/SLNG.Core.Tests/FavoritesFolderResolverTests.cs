using SLNG.Core;
using SLNG.Core.Landmarks;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// FEAT-UI-72: the Favorites folder is only ever a folder that really is one. LibreMetaverse's FindFolderForType
/// answers with the inventory ROOT when the type is missing, which would make the whole inventory "favorites".
/// </summary>
public class FavoritesFolderResolverTests
{
    private static readonly Guid Root = Guid.NewGuid();

    [Fact]
    public void A_root_child_of_the_favorites_type_is_the_folder()
    {
        var fav = Guid.NewGuid();
        var children = new[]
        {
            (Guid.NewGuid(), FolderTypeIds.Landmark),
            (fav, FolderTypeIds.Favorites),
            (Guid.NewGuid(), FolderTypeIds.Trash),
        };

        Assert.Equal(fav, FavoritesFolderResolver.Resolve(Root, children));
    }

    [Fact]
    public void No_such_folder_is_null_and_never_the_root()
    {
        var children = new[] { (Guid.NewGuid(), FolderTypeIds.Landmark), (Guid.NewGuid(), -1) };

        var result = FavoritesFolderResolver.Resolve(Root, children);

        Assert.Null(result);
        Assert.NotEqual(Root, result);
    }

    [Fact]
    public void A_favorites_typed_folder_below_the_roots_children_is_not_offered()
    {
        // Only the root's direct children are passed; a type-23 folder further down is simply absent from them.
        var children = new[] { (Guid.NewGuid(), -1), (Guid.NewGuid(), FolderTypeIds.Object) };

        Assert.Null(FavoritesFolderResolver.Resolve(Root, children));
    }

    [Fact]
    public void Two_favorites_folders_resolve_to_the_first()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();

        var result = FavoritesFolderResolver.Resolve(Root,
            new[] { (first, FolderTypeIds.Favorites), (second, FolderTypeIds.Favorites) });

        Assert.Equal(first, result);
    }

    [Fact]
    public void No_inventory_yet_is_null()
    {
        Assert.Null(FavoritesFolderResolver.Resolve(Guid.Empty, new[] { (Guid.NewGuid(), FolderTypeIds.Favorites) }));
        Assert.Null(FavoritesFolderResolver.Resolve(Root, Array.Empty<(Guid, int)>()));
    }

    [Fact]
    public void The_root_itself_and_empty_ids_never_qualify()
    {
        Assert.Null(FavoritesFolderResolver.Resolve(Root, new[] { (Root, FolderTypeIds.Favorites) }));
        Assert.Null(FavoritesFolderResolver.Resolve(Root, new[] { (Guid.Empty, FolderTypeIds.Favorites) }));
    }

    [Fact]
    public void The_favorites_type_is_23()
    {
        // The wire value of FolderType.Favorites; if it ever changed here the resolver would silently never match.
        Assert.Equal(23, FolderTypeIds.Favorites);
    }
}
