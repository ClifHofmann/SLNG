using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SLNG.App.UI;
using SLNG.Core;
using SLNG.Core.Landmarks;

namespace SLNG.App;

public static partial class SelfTest
{
    /// <summary>
    /// BUG-UI-33: Favorites bar user and grid isolation.
    /// Favorites must be strictly scoped to both the grid and the agent id.
    /// Never share, inherit or leak favorites across different users or different grids.
    /// </summary>
    private static Check CheckLandmarkFavoritesStore()
    {
        const string Name = "favorites bar user/grid isolation";
        try
        {
            var failures = new List<string>();

            // 1. Section formatting
            if (LandmarkFavoritesStore.GetSection(null, null) != null) failures.Add("null grid and agent returned non-null section");
            if (LandmarkFavoritesStore.GetSection("agni", null) != null) failures.Add("null agent returned non-null section");
            if (LandmarkFavoritesStore.GetSection(null, "some-id") != null) failures.Add("null grid returned non-null section");
            if (LandmarkFavoritesStore.GetSection("   ", "some-id") != null) failures.Add("whitespace grid returned non-null section");
            if (LandmarkFavoritesStore.GetSection("agni", Guid.Empty.ToString()) != null) failures.Add("Guid.Empty agent returned non-null section");

            string user1 = "11111111-1111-1111-1111-111111111111";
            string user2 = "22222222-2222-2222-2222-222222222222";
            string? secAgniUser1 = LandmarkFavoritesStore.GetSection("Agni", user1);
            string? secOsgridUser1 = LandmarkFavoritesStore.GetSection("osgrid", user1);
            string? secAgniUser2 = LandmarkFavoritesStore.GetSection("agni", user2);

            if (secAgniUser1 != $"favorites_bar_agni_{user1}") failures.Add($"unexpected section name: {secAgniUser1}");
            if (secAgniUser1 == secOsgridUser1) failures.Add("agni and osgrid sections collided");
            if (secAgniUser1 == secAgniUser2) failures.Add("user1 and user2 sections collided");

            // 2. Storage and isolation in memory ConfigFile
            var cfg = new ConfigFile();

            // Inject legacy global section to ensure it is NEVER read or leaked
            cfg.SetValue("favorites_bar", "data", "{\"Items\":[{\"ItemId\":\"33333333-3333-3333-3333-333333333333\",\"AssetId\":\"44444444-4444-4444-4444-444444444444\",\"Name\":\"Legacy Leaked LM\"}]}");

            // Populate user1 on Agni
            var listAgni1 = new LandmarkFavoritesList();
            listAgni1.Add(new LandmarkFavoriteItem(Guid.NewGuid(), Guid.NewGuid(), "SL Club"));
            LandmarkFavoritesStore.SaveToConfig(cfg, secAgniUser1!, listAgni1);

            // Populate user1 on OSGrid
            var listOsgrid1 = new LandmarkFavoritesList();
            listOsgrid1.Add(new LandmarkFavoriteItem(Guid.NewGuid(), Guid.NewGuid(), "OS Plaza"));
            LandmarkFavoritesStore.SaveToConfig(cfg, secOsgridUser1!, listOsgrid1);

            // Read back User 1 on Agni
            var loadedAgni1 = LandmarkFavoritesStore.LoadFromConfig(cfg, secAgniUser1!);
            if (loadedAgni1.Items.Count != 1 || loadedAgni1.Items[0].Name != "SL Club")
                failures.Add("Agni User 1 landmarks not read back correctly");

            // Read back User 1 on OSGrid
            var loadedOsgrid1 = LandmarkFavoritesStore.LoadFromConfig(cfg, secOsgridUser1!);
            if (loadedOsgrid1.Items.Count != 1 || loadedOsgrid1.Items[0].Name != "OS Plaza")
                failures.Add("OSGrid User 1 landmarks not read back correctly");

            // Read back User 2 on Agni (has no data saved yet)
            var loadedAgni2 = LandmarkFavoritesStore.LoadFromConfig(cfg, secAgniUser2!);
            if (loadedAgni2.Items.Count != 0)
                failures.Add("Agni User 2 (no data) leaked landmarks from legacy or other users");

            // 3. The bar's order is kept per grid and user too, and a damaged value reads as "no order".
            var order = new List<Guid> { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            LandmarkFavoritesStore.SaveOrderToConfig(cfg, secAgniUser1!, order);
            if (!LandmarkFavoritesStore.LoadOrderFromConfig(cfg, secAgniUser1!).SequenceEqual(order))
                failures.Add("the stored order did not read back as written");
            if (LandmarkFavoritesStore.LoadOrderFromConfig(cfg, secOsgridUser1!).Count != 0)
                failures.Add("another grid's section shows this order");
            cfg.SetValue(secAgniUser2!, "order", "not json");
            if (LandmarkFavoritesStore.LoadOrderFromConfig(cfg, secAgniUser2!).Count != 0)
                failures.Add("a damaged order value read as an order");

            // 4. A bar with no service has nothing to add to and shows nothing.
            var bar = new FavoritesBar();
            bar.Reset();
            if (bar.Visible) failures.Add("bar.Reset() left the bar visible");
            bar.QueueFree();

            return failures.Count == 0
                ? new Check(Name, true, "grid and user isolation enforced for the bar's visibility and order, legacy global section ignored")
                : new Check(Name, false, string.Join("; ", failures));
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>FEAT-UI-72 phase 2: the favorites service shows the Favorites folder in the viewer's order, never believes an
    /// unconfirmed read, says so when there is no folder, freezes the order once shown, and forgets everything on logout.</summary>
    private static Check CheckFavoritesService()
    {
        const string Name = "favorites service";
        var failures = new List<string>();
        var service = new FavoritesService();
        try
        {
            LandmarkInventoryItem Item(string name, int ageDays) =>
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, "Favorites", "Favorites", new DateTime(2026, 1, 1).AddDays(-ageDays));

            var a = Item("Alpha", 3);
            var b = Item("Beta", 1);
            var c = Item("Gamma", 2);

            var source = new FakeFavoritesSource { Folder = true };
            var saved = new List<IReadOnlyList<Guid>>();
            service.Initialize(source, Array.Empty<Guid>(), o => saved.Add(o));

            if (service.Mode != FavoritesMode.Loading) failures.Add("a fresh service is not Loading");

            // An unconfirmed first answer is never believed: the bar says "unavailable", not "empty".
            service.ApplyForTest(new FavoritesSnapshot(false, Array.Empty<LandmarkInventoryItem>()));
            if (service.Mode != FavoritesMode.Unavailable || service.Items.Count != 0) failures.Add("an unconfirmed first read was believed");

            // Confirmed: newest first, because nothing has a stored place yet.
            service.ApplyForTest(new FavoritesSnapshot(true, new[] { a, b, c }));
            if (service.Mode != FavoritesMode.Folder) failures.Add("a confirmed read did not give Folder");
            if (!service.Items.Select(i => i.Name).SequenceEqual(new[] { "Beta", "Gamma", "Alpha" }))
                failures.Add("unindexed favorites are not newest first: " + string.Join(",", service.Items.Select(i => i.Name)));
            if (saved.Count != 1 || !saved[0].SequenceEqual(service.Items.Select(i => i.Id)))
                failures.Add("the shown order was not stored once");

            // A later unconfirmed read keeps what is shown.
            service.ApplyForTest(new FavoritesSnapshot(false, Array.Empty<LandmarkInventoryItem>()));
            if (service.Mode != FavoritesMode.Folder || service.Items.Count != 3) failures.Add("an unconfirmed refresh wiped the bar");

            // A new favorite arrives: it shows first, and the order is stored again.
            var d = Item("Delta", 0);
            service.ApplyForTest(new FavoritesSnapshot(true, new[] { a, b, c, d }));
            if (service.Items[0].Name != "Delta") failures.Add("a new favorite did not show first");
            if (saved.Count != 2) failures.Add("the order was not stored again for the new favorite");

            // The stored order wins over age after that.
            service.ApplyForTest(new FavoritesSnapshot(true, new[] { d, c, b, a }));
            if (!service.Items.Select(i => i.Name).SequenceEqual(new[] { "Delta", "Beta", "Gamma", "Alpha" }))
                failures.Add("the stored order was not kept");

            // A favorite that is gone is pruned from the stored order.
            service.ApplyForTest(new FavoritesSnapshot(true, new[] { d, b, a }));
            if (service.Items.Count != 3 || saved[^1].Contains(c.Id)) failures.Add("a removed favorite stayed in the stored order");

            // The cleaner's two sets.
            if (!service.KnownIds().Contains(a.AssetId) || !service.KnownIds().Contains(a.Id)) failures.Add("KnownIds lacks an item or asset id");
            if (service.ItemIds().Contains(a.AssetId) || !service.ItemIds().Contains(a.Id)) failures.Add("ItemIds is not just the item ids");

            // No Favorites folder.
            source.Folder = false;
            service.ApplyForTest(new FavoritesSnapshot(false, Array.Empty<LandmarkInventoryItem>()));
            if (service.Mode != FavoritesMode.NoFolder || service.Items.Count != 0) failures.Add("a grid without the folder did not give NoFolder");

            service.Reset();
            if (service.Mode != FavoritesMode.Loading || service.Items.Count != 0) failures.Add("Reset did not forget the session");

            return failures.Count == 0
                ? new Check(Name, true, "viewer order, unconfirmed reads ignored, order frozen and pruned, no-folder and reset handled")
                : new Check(Name, false, string.Join("; ", failures));
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            service.QueueFree();
        }
    }

    /// <summary>FEAT-UI-72 phase 2: what the drag card says while a landmark is over the bar -- add, already a favorite (by item and by
    /// place), and that nothing can be added without a folder or before the folder is read.</summary>
    private static Check CheckFavoritesBarDropStatus(SceneTree tree)
    {
        const string Name = "favorites bar drop status";
        var failures = new List<string>();
        var service = new FavoritesService();
        var bar = new FavoritesBar();
        try
        {
            tree.Root.AddChild(service);
            tree.Root.AddChild(bar);

            var item = new LandmarkInventoryItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Home", "Favorites", "Favorites", DateTime.UtcNow);
            var source = new FakeFavoritesSource { Folder = true };
            service.Initialize(source, Array.Empty<Guid>(), _ => { });
            bar.Initialize(service, null, null);

            string add = L10n.Tr("ui.favorites_bar.drop_add");
            string already = L10n.Tr("ui.favorites_bar.drop_already");
            string notReady = L10n.Tr("ui.favorites_bar.drop_not_ready");
            string noFolder = L10n.Tr("ui.favorites_bar.drop_no_folder");
            if (new[] { add, already, notReady, noFolder }.Any(t => t.StartsWith('['))) failures.Add("a drop text is missing from the locale files");

            if (bar.DropStatusFor(Guid.NewGuid(), Guid.Empty).Text != notReady) failures.Add("before the folder was read the card does not say so");

            service.ApplyForTest(new FavoritesSnapshot(true, new[] { item }));
            if (bar.DropStatusFor(Guid.NewGuid(), Guid.NewGuid()).Text != add) failures.Add("a new landmark is not offered as an add");
            if (bar.DropStatusFor(item.Id, Guid.Empty).Text != already) failures.Add("a landmark already in the folder is offered as an add");
            if (bar.DropStatusFor(Guid.NewGuid(), item.AssetId).Text != already) failures.Add("a second landmark for the same place is offered as an add");

            source.Folder = false;
            service.ApplyForTest(new FavoritesSnapshot(false, Array.Empty<LandmarkInventoryItem>()));
            if (bar.DropStatusFor(Guid.NewGuid(), Guid.NewGuid()).Text != noFolder) failures.Add("a grid without the folder is offered an add");

            return failures.Count == 0
                ? new Check(Name, true, "add, already a favorite (item and place), not read yet and no folder are told apart")
                : new Check(Name, false, string.Join("; ", failures));
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            foreach (var n in new Node[] { bar, service })
            {
                if (!GodotObject.IsInstanceValid(n)) continue;
                n.GetParent()?.RemoveChild(n);
                n.QueueFree();
            }
        }
    }

    private sealed class FakeFavoritesSource : IFavoritesSource
    {
        public bool Folder { get; set; }
        public bool HasFolder => Folder;
        public FavoritesSnapshot ReadFromStore() => FavoritesSnapshot.Unconfirmed;
        public System.Threading.Tasks.Task<FavoritesSnapshot> FetchAsync(System.Threading.CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(FavoritesSnapshot.Unconfirmed);
        public System.Threading.Tasks.Task<FavoriteAddVerdict> AddAsync(Guid itemId, System.Threading.CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(FavoriteAddVerdict.Add);
        public System.Threading.Tasks.Task RemoveAsync(Guid itemId) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task<bool?> AddCurrentLocationAsync(string name, System.Threading.CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult<bool?>(true);
        public event EventHandler? Changed { add { } remove { } }
    }
}
