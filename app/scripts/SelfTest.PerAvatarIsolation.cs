using System;
using System.Collections.Generic;
using Godot;
using SLNG.App.UI;
using SLNG.Core;

namespace SLNG.App;

public static partial class SelfTest
{
    /// <summary>
    /// BUG-GRID-02: Comprehensive per-avatar and per-grid data isolation.
    /// Group mutes, avatar hover heights, local profile notes, caches, and notifications
    /// must be strictly scoped to the grid and avatar ID, never shared, inherited, or leaked across logins.
    /// </summary>
    private static Check CheckPerAvatarDataIsolation()
    {
        const string Name = "per-avatar data isolation";
        try
        {
            var failures = new List<string>();

            // 1. GroupMuteSettings section formatting & isolation
            if (GroupMuteSettings.GetSection(null, null) != null) failures.Add("GroupMuteSettings: null grid and agent returned non-null section");
            if (GroupMuteSettings.GetSection("agni", null) != null) failures.Add("GroupMuteSettings: null agent returned non-null section");
            if (GroupMuteSettings.GetSection(null, "some-id") != null) failures.Add("GroupMuteSettings: null grid returned non-null section");
            if (GroupMuteSettings.GetSection("   ", "some-id") != null) failures.Add("GroupMuteSettings: whitespace grid returned non-null section");
            if (GroupMuteSettings.GetSection("agni", Guid.Empty.ToString()) != null) failures.Add("GroupMuteSettings: Guid.Empty agent returned non-null section");

            string user1 = "11111111-1111-1111-1111-111111111111";
            string user2 = "22222222-2222-2222-2222-222222222222";
            string? secMuteAgni1 = GroupMuteSettings.GetSection("Agni", user1);
            string? secMuteOsgrid1 = GroupMuteSettings.GetSection("osgrid", user1);
            string? secMuteAgni2 = GroupMuteSettings.GetSection("agni", user2);

            if (secMuteAgni1 != $"group_mute_agni_{user1}") failures.Add($"GroupMuteSettings: unexpected section name: {secMuteAgni1}");
            if (secMuteAgni1 == secMuteOsgrid1) failures.Add("GroupMuteSettings: agni and osgrid sections collided");
            if (secMuteAgni1 == secMuteAgni2) failures.Add("GroupMuteSettings: user1 and user2 sections collided");

            // Test in-memory isolation for GroupMuteSettings
            bool origMutePersist = GroupMuteSettings.Persist;
            GroupMuteSettings.Persist = false;
            try
            {
                var groupA = Guid.NewGuid();
                GroupMuteSettings.Initialize("agni", user1);
                GroupMuteSettings.SetMuted(groupA, true);
                if (!GroupMuteSettings.IsMuted(groupA)) failures.Add("GroupMuteSettings: groupA not muted for user1");

                // Switch to user2 on agni
                GroupMuteSettings.Initialize("agni", user2);
                if (GroupMuteSettings.IsMuted(groupA)) failures.Add("GroupMuteSettings: user2 inherited muted groupA from user1");

                // Reset
                GroupMuteSettings.Reset();
                if (GroupMuteSettings.IsMuted(groupA)) failures.Add("GroupMuteSettings: groupA still muted after Reset()");
            }
            finally
            {
                GroupMuteSettings.Persist = origMutePersist;
            }

            // 2. AvatarHoverSettings section formatting & isolation
            if (AvatarHoverSettings.GetSection(null, null) != null) failures.Add("AvatarHoverSettings: null grid and agent returned non-null section");
            if (AvatarHoverSettings.GetSection("agni", null) != null) failures.Add("AvatarHoverSettings: null agent returned non-null section");
            if (AvatarHoverSettings.GetSection(null, "some-id") != null) failures.Add("AvatarHoverSettings: null grid returned non-null section");
            if (AvatarHoverSettings.GetSection("agni", Guid.Empty.ToString()) != null) failures.Add("AvatarHoverSettings: Guid.Empty agent returned non-null section");

            string? secHoverAgni1 = AvatarHoverSettings.GetSection("Agni", user1);
            string? secHoverOsgrid1 = AvatarHoverSettings.GetSection("osgrid", user1);
            string? secHoverAgni2 = AvatarHoverSettings.GetSection("agni", user2);

            if (secHoverAgni1 != $"avatar_hover_agni_{user1}") failures.Add($"AvatarHoverSettings: unexpected section name: {secHoverAgni1}");
            if (secHoverAgni1 == secHoverOsgrid1) failures.Add("AvatarHoverSettings: agni and osgrid sections collided");
            if (secHoverAgni1 == secHoverAgni2) failures.Add("AvatarHoverSettings: user1 and user2 sections collided");

            // Test AvatarHoverSettings reset and in-memory behaviour
            bool origHoverPersist = AvatarHoverSettings.Persist;
            AvatarHoverSettings.Persist = false;
            try
            {
                var hover = new AvatarHoverSettings();
                hover.SetHoverHeight(0.75f, persist: false);
                if (Math.Abs(hover.HoverHeight - 0.75f) > 0.001f) failures.Add("AvatarHoverSettings: HoverHeight not updated");
                hover.Reset();
                if (Math.Abs(hover.HoverHeight - AvatarHoverSettings.DefaultHoverHeight) > 0.001f) failures.Add("AvatarHoverSettings: HoverHeight not reset to default");
            }
            finally
            {
                AvatarHoverSettings.Persist = origHoverPersist;
            }

            // 3. UserProfileWindow notes section formatting & isolation
            if (UserProfileWindow.GetNotesSection(null, null) != null) failures.Add("UserProfileWindow: null grid and agent returned non-null notes section");
            if (UserProfileWindow.GetNotesSection("agni", Guid.Empty.ToString()) != null) failures.Add("UserProfileWindow: Guid.Empty agent returned non-null notes section");
            if (UserProfileWindow.GetNotesImportedSection(null, null) != null) failures.Add("UserProfileWindow: null grid and agent returned non-null imported section");
            if (UserProfileWindow.GetNotesImportedSection("agni", Guid.Empty.ToString()) != null) failures.Add("UserProfileWindow: Guid.Empty agent returned non-null imported section");

            string? secNotesAgni1 = UserProfileWindow.GetNotesSection("Agni", user1);
            string? secNotesOsgrid1 = UserProfileWindow.GetNotesSection("osgrid", user1);
            string? secNotesAgni2 = UserProfileWindow.GetNotesSection("agni", user2);

            if (secNotesAgni1 != $"avatar_notes_agni_{user1}") failures.Add($"UserProfileWindow: unexpected notes section name: {secNotesAgni1}");
            if (secNotesAgni1 == secNotesOsgrid1) failures.Add("UserProfileWindow: agni and osgrid notes sections collided");
            if (secNotesAgni1 == secNotesAgni2) failures.Add("UserProfileWindow: user1 and user2 notes sections collided");

            string? secImportedAgni1 = UserProfileWindow.GetNotesImportedSection("Agni", user1);
            if (secImportedAgni1 != $"avatar_notes_imported_agni_{user1}") failures.Add($"UserProfileWindow: unexpected imported section name: {secImportedAgni1}");

            // 4. GridData inventory and display name cache directory per-grid isolation
            string invAgni = GridData.InventoryCacheDirectory("https://login.agni.lindenlab.com/cgi-bin/login.cgi");
            string invOsgrid = GridData.InventoryCacheDirectory("http://hg.osgrid.org:8002/");
            if (invAgni == invOsgrid) failures.Add("GridData: InventoryCacheDirectory collided between Agni and OSGrid");
            if (!invAgni.Contains("agni", StringComparison.OrdinalIgnoreCase)) failures.Add("GridData: InventoryCacheDirectory for Agni missing slug");

            string dnAgni = GridData.DisplayNameCacheDirectory("https://login.agni.lindenlab.com/cgi-bin/login.cgi");
            string dnOsgrid = GridData.DisplayNameCacheDirectory("http://hg.osgrid.org:8002/");
            if (dnAgni == dnOsgrid) failures.Add("GridData: DisplayNameCacheDirectory collided between Agni and OSGrid");
            if (!dnAgni.Contains("agni", StringComparison.OrdinalIgnoreCase)) failures.Add("GridData: DisplayNameCacheDirectory for Agni missing slug");

            // 5. NotificationStore DismissAll on logout/session-reset
            var notifications = new NotificationStore();
            notifications.Add(NotificationKind.System, Guid.NewGuid(), "Hello User 1");
            notifications.Add(NotificationKind.Group, Guid.NewGuid(), "Group Notice");
            if (notifications.Count != 2 || notifications.UnreadCount != 2) failures.Add("NotificationStore: expected 2 entries before DismissAll");
            notifications.DismissAll();
            if (notifications.Count != 0 || notifications.UnreadCount != 0) failures.Add("NotificationStore: entries remained after DismissAll");

            if (failures.Count > 0)
                return new Check(Name, false, string.Join("; ", failures));

            return new Check(Name, true, "group mutes, avatar hover heights, profile notes, caches, and notifications strictly isolated per avatar and grid");
        }
        catch (Exception ex)
        {
            return new Check(Name, false, $"exception: {ex.Message}");
        }
    }
}
