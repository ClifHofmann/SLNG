using System.Text;
using SLNG.Core.Landmarks;
using SLNG.Core.Login;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>FEAT-UI-72: the per-profile copy of the Favorites folder the login screen reads.</summary>
public class LoginFavoriteBookTests
{
    private static LoginFavorite Fav(string name, string region = "Ahern", int x = 1, int y = 2, int z = 3, Guid? asset = null, Guid? item = null)
    {
        Assert.True(StartTarget.TryCreate(region, x, y, z, 4096, out var t));
        return new LoginFavorite(asset ?? Guid.NewGuid(), item ?? Guid.NewGuid(), name, t!);
    }

    private static LandmarkInventoryItem Lm(string name, Guid asset, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), Guid.NewGuid(), asset, name, "Favorites", "Favorites");

    [Fact]
    public void Json_round_trips_names_regions_and_positions()
    {
        var list = new[]
        {
            Fav("Puris Town", "Ahern", 128, 64, 30),
            Fav("Café \"München\"", "Ahern Park", 1, 2, 3),
        };

        var back = LoginFavoriteBook.FromJson(LoginFavoriteBook.ToJson(list));

        Assert.Equal(list, back);
    }

    [Fact]
    public void The_json_is_one_line_with_a_version()
    {
        string json = LoginFavoriteBook.ToJson(new[] { Fav("A") });
        Assert.DoesNotContain('\n', json);
        Assert.StartsWith("{\"v\":1,\"items\":[", json);
        Assert.Equal("{\"v\":1,\"items\":[]}", LoginFavoriteBook.ToJson(Array.Empty<LoginFavorite>()));
        Assert.Empty(LoginFavoriteBook.FromJson("{\"v\":1,\"items\":[]}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"v\":2,\"items\":[]}")]
    [InlineData("{\"items\":[]}")]
    [InlineData("[]")]
    [InlineData("{\"v\":1}")]
    [InlineData("{\"v\":1,\"items\":5}")]
    [InlineData("{\"v\":\"1\",\"items\":[]}")]
    public void Unreadable_json_is_an_empty_list(string? json) =>
        Assert.Empty(LoginFavoriteBook.FromJson(json));

    [Fact]
    public void A_bad_entry_is_skipped_and_the_rest_survive()
    {
        string asset = Guid.NewGuid().ToString("N");
        string json = "{\"v\":1,\"items\":[" +
            "{\"asset\":\"" + asset + "\",\"item\":\"\",\"name\":\"No region\",\"x\":1,\"y\":2,\"z\":3}," +   // no region
            "{\"asset\":\"nope\",\"item\":\"\",\"name\":\"Bad asset\",\"region\":\"R\",\"x\":1,\"y\":2,\"z\":3}," +
            "{\"asset\":\"" + Guid.NewGuid().ToString("N") + "\",\"item\":\"\",\"name\":\"\",\"region\":\"R\",\"x\":1,\"y\":2,\"z\":3}," + // no name
            "{\"asset\":\"" + Guid.NewGuid().ToString("N") + "\",\"item\":\"\",\"name\":\"Out of range\",\"region\":\"R\",\"x\":99999,\"y\":2,\"z\":3}," +
            "{\"asset\":\"" + Guid.NewGuid().ToString("N") + "\",\"item\":\"\",\"name\":\"Amp\",\"region\":\"A&B\",\"x\":1,\"y\":2,\"z\":3}," +
            "42,null,\"text\"," +
            "{\"asset\":\"" + asset + "\",\"item\":\"" + Guid.NewGuid().ToString("N") + "\",\"name\":\"Good\",\"region\":\"Ahern\",\"x\":10,\"y\":20,\"z\":30}" +
            "]}";

        var list = LoginFavoriteBook.FromJson(json);

        var only = Assert.Single(list);
        Assert.Equal("Good", only.Name);
        Assert.Equal("uri:Ahern&10&20&30", only.Target.ToLoginString());
    }

    [Fact]
    public void More_than_64_entries_are_cut_to_64()
    {
        var many = Enumerable.Range(0, 70).Select(i => Fav("F" + i)).ToList();

        // ToJson itself writes at most 64 ...
        Assert.Equal(64, LoginFavoriteBook.FromJson(LoginFavoriteBook.ToJson(many)).Count);

        // ... and so does reading a hand-made file with 70.
        var sb = new StringBuilder("{\"v\":1,\"items\":[");
        for (int i = 0; i < 70; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"asset\":\"").Append(Guid.NewGuid().ToString("N")).Append("\",\"item\":\"\",\"name\":\"F").Append(i)
              .Append("\",\"region\":\"R\",\"x\":1,\"y\":2,\"z\":3}");
        }
        sb.Append("]}");
        var list = LoginFavoriteBook.FromJson(sb.ToString());
        Assert.Equal(64, list.Count);
        Assert.Equal("F0", list[0].Name);
        Assert.Equal("F63", list[63].Name);
    }

    [Fact]
    public void Duplicate_asset_ids_collapse_and_the_first_wins()
    {
        string asset = Guid.NewGuid().ToString("N");
        string json = "{\"v\":1,\"items\":[" +
            "{\"asset\":\"" + asset + "\",\"item\":\"\",\"name\":\"First\",\"region\":\"A\",\"x\":1,\"y\":2,\"z\":3}," +
            "{\"asset\":\"" + asset + "\",\"item\":\"\",\"name\":\"Second\",\"region\":\"B\",\"x\":4,\"y\":5,\"z\":6}]}";

        var only = Assert.Single(LoginFavoriteBook.FromJson(json));
        Assert.Equal("First", only.Name);
        Assert.Equal("A", only.Target.Region);
    }

    // ---- Reconcile ----

    [Fact]
    public void The_folders_order_wins()
    {
        var a = Fav("A"); var b = Fav("B"); var c = Fav("C");
        var folder = new[] { Lm("C", c.AssetId), Lm("A", a.AssetId), Lm("B", b.AssetId) };

        var r = LoginFavoriteBook.Reconcile(folder, new[] { a, b, c });

        Assert.Equal(new[] { "C", "A", "B" }, r.Favorites.Select(f => f.Name));
        Assert.Empty(r.Unresolved);
    }

    [Fact]
    public void A_renamed_item_keeps_its_old_target_under_the_new_name()
    {
        var a = Fav("Old name", "Ahern", 7, 8, 9);
        var itemId = Guid.NewGuid();

        var r = LoginFavoriteBook.Reconcile(new[] { Lm("New name", a.AssetId, itemId) }, new[] { a });

        var only = Assert.Single(r.Favorites);
        Assert.Equal("New name", only.Name);
        Assert.Equal(a.Target, only.Target);
        Assert.Equal(itemId, only.ItemId);
        Assert.Empty(r.Unresolved);
    }

    [Fact]
    public void A_removed_item_disappears()
    {
        var a = Fav("A"); var b = Fav("B");

        var r = LoginFavoriteBook.Reconcile(new[] { Lm("A", a.AssetId) }, new[] { a, b });

        Assert.Equal(new[] { "A" }, r.Favorites.Select(f => f.Name));
    }

    [Fact]
    public void A_new_item_is_unresolved_and_not_offered()
    {
        var a = Fav("A");
        var fresh = Lm("Fresh", Guid.NewGuid());

        var r = LoginFavoriteBook.Reconcile(new[] { Lm("A", a.AssetId), fresh }, new[] { a });

        Assert.Equal(new[] { "A" }, r.Favorites.Select(f => f.Name));
        Assert.Equal(new[] { fresh }, r.Unresolved);
    }

    [Fact]
    public void An_item_without_an_asset_id_is_unresolved()
    {
        var pending = Lm("Pending", Guid.Empty);

        var r = LoginFavoriteBook.Reconcile(new[] { pending }, Array.Empty<LoginFavorite>());

        Assert.Empty(r.Favorites);
        Assert.Equal(new[] { pending }, r.Unresolved);
    }

    [Fact]
    public void Nothing_with_an_unknown_target_is_in_the_favorites()
    {
        var folder = Enumerable.Range(0, 5).Select(i => Lm("L" + i, Guid.NewGuid())).ToList();

        var r = LoginFavoriteBook.Reconcile(folder, Array.Empty<LoginFavorite>());

        Assert.Empty(r.Favorites);
        Assert.Equal(5, r.Unresolved.Count);
    }

    [Fact]
    public void The_same_landmark_twice_in_the_folder_is_offered_once()
    {
        var a = Fav("A");

        var r = LoginFavoriteBook.Reconcile(
            new[] { Lm("A", a.AssetId), Lm("A copy", a.AssetId) }, new[] { a });

        Assert.Equal(new[] { "A" }, r.Favorites.Select(f => f.Name));
    }
}
