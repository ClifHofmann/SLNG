using System;
using System.Collections.Generic;
using Godot;
using SLNG.App.UI;
using SLNG.Core;

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

            // 3. FavoritesBar control state
            var bar = new FavoritesBar();
            if (bar.AddFavorite(Guid.NewGuid(), Guid.NewGuid(), "Unauthenticated"))
                failures.Add("unauthenticated bar accepted AddFavorite");

            bar.Reset();
            if (bar.FavoritesList.Items.Count != 0 || bar.Visible)
                failures.Add("bar.Reset() did not clear list or visibility");
            bar.QueueFree();

            return failures.Count == 0
                ? new Check(Name, true, "grid and user isolation enforced, legacy global section ignored, unauthenticated drops rejected")
                : new Check(Name, false, string.Join("; ", failures));
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
    }
}
