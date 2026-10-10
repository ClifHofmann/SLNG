using System;
using System.Collections.Generic;
using Godot;
using SLNG.App.UI;
using SLNG.Core;

namespace SLNG.App;

public static partial class SelfTest
{
    /// <summary>
    /// BUG-UI-41: Friend categories user and grid isolation.
    /// Categories, assignments, and view preferences must be strictly scoped to both the grid and the agent id.
    /// Never share, inherit, or leak friend categories across different users or different grids.
    /// </summary>
    private static Check CheckFriendCategoryStore()
    {
        const string Name = "friend categories user/grid isolation";
        try
        {
            var failures = new List<string>();

            // 1. Section formatting
            if (FriendCategoryStore.GetSection(null, null) != null) failures.Add("null grid and agent returned non-null section");
            if (FriendCategoryStore.GetSection("agni", null) != null) failures.Add("null agent returned non-null section");
            if (FriendCategoryStore.GetSection(null, "some-id") != null) failures.Add("null grid returned non-null section");
            if (FriendCategoryStore.GetSection("   ", "some-id") != null) failures.Add("whitespace grid returned non-null section");
            if (FriendCategoryStore.GetSection("agni", Guid.Empty.ToString()) != null) failures.Add("Guid.Empty agent returned non-null section");

            string user1 = "11111111-1111-1111-1111-111111111111";
            string user2 = "22222222-2222-2222-2222-222222222222";
            string? secAgniUser1 = FriendCategoryStore.GetSection("Agni", user1);
            string? secOsgridUser1 = FriendCategoryStore.GetSection("osgrid", user1);
            string? secAgniUser2 = FriendCategoryStore.GetSection("agni", user2);

            if (secAgniUser1 != $"friend_categories_agni_{user1}") failures.Add($"unexpected section name: {secAgniUser1}");
            if (secAgniUser1 == secOsgridUser1) failures.Add("agni and osgrid sections collided");
            if (secAgniUser1 == secAgniUser2) failures.Add("user1 and user2 sections collided");

            // 2. Storage and isolation in memory ConfigFile
            var cfg = new ConfigFile();

            // Inject legacy corrupted section to ensure it is NEVER read or leaked
            cfg.SetValue("friend_categories_00000000-0000-0000-0000-000000000000", "data", "{\"Categories\":[\"Corrupted Global\"]}");

            // Populate user1 on Agni
            var bookAgni1 = new FriendCategoryBook();
            bookAgni1.Add("Family");
            FriendCategoryStore.SaveToConfig(cfg, secAgniUser1!, bookAgni1);
            FriendCategoryStore.SaveOnlyOnlineToConfig(cfg, secAgniUser1!, true);
            FriendCategoryStore.SaveShowCategoriesToConfig(cfg, secAgniUser1!, true);

            // Populate user1 on OSGrid
            var bookOsgrid1 = new FriendCategoryBook();
            bookOsgrid1.Add("Builders");
            FriendCategoryStore.SaveToConfig(cfg, secOsgridUser1!, bookOsgrid1);
            FriendCategoryStore.SaveOnlyOnlineToConfig(cfg, secOsgridUser1!, false);
            FriendCategoryStore.SaveShowCategoriesToConfig(cfg, secOsgridUser1!, false);

            // Populate user2 on Agni
            var bookAgni2 = new FriendCategoryBook();
            bookAgni2.Add("Friends");
            FriendCategoryStore.SaveToConfig(cfg, secAgniUser2!, bookAgni2);
            FriendCategoryStore.SaveOnlyOnlineToConfig(cfg, secAgniUser2!, false);
            FriendCategoryStore.SaveShowCategoriesToConfig(cfg, secAgniUser2!, true);

            // Read back User 1 on Agni
            var loadedAgni1 = FriendCategoryStore.LoadFromConfig(cfg, secAgniUser1!);
            if (loadedAgni1.Categories.Count != 1 || loadedAgni1.Categories[0] != "Family")
                failures.Add("Agni User 1 categories not read back correctly");
            if (!FriendCategoryStore.LoadOnlyOnlineFromConfig(cfg, secAgniUser1!))
                failures.Add("Agni User 1 only_online was not true");
            if (!FriendCategoryStore.LoadShowCategoriesFromConfig(cfg, secAgniUser1!))
                failures.Add("Agni User 1 show_categories was not true");

            // Read back User 1 on OSGrid
            var loadedOsgrid1 = FriendCategoryStore.LoadFromConfig(cfg, secOsgridUser1!);
            if (loadedOsgrid1.Categories.Count != 1 || loadedOsgrid1.Categories[0] != "Builders")
                failures.Add("OSGrid User 1 categories not read back correctly");
            if (FriendCategoryStore.LoadOnlyOnlineFromConfig(cfg, secOsgridUser1!))
                failures.Add("OSGrid User 1 only_online was true, expected false");
            if (FriendCategoryStore.LoadShowCategoriesFromConfig(cfg, secOsgridUser1!))
                failures.Add("OSGrid User 1 show_categories was true, expected false");

            // Read back User 2 on Agni
            var loadedAgni2 = FriendCategoryStore.LoadFromConfig(cfg, secAgniUser2!);
            if (loadedAgni2.Categories.Count != 1 || loadedAgni2.Categories[0] != "Friends")
                failures.Add("Agni User 2 categories not read back correctly");

            // Verify a completely new account starts empty and does not inherit corrupted section
            string userNew = "99999999-9999-9999-9999-999999999999";
            string? secNew = FriendCategoryStore.GetSection("agni", userNew);
            var loadedNew = FriendCategoryStore.LoadFromConfig(cfg, secNew!);
            if (loadedNew.Categories.Count != 0)
                failures.Add("New user leaked categories from existing sections");

            return failures.Count == 0
                ? new Check(Name, true, "friend categories strictly isolated per grid and agent id")
                : new Check(Name, false, string.Join("; ", failures));
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"unhandled exception: {ex.Message}");
        }
    }
}
