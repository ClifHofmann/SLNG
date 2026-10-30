namespace SLNG.Core.Presence;

/// <summary>What survives a restart of the Away / autorespond setup for one login: the configuration and the two idle
/// settings the viewer keeps with it. Away and Do Not Disturb are deliberately not part of it.</summary>
public sealed record PresenceSettingsData(
    PresenceConfig Config,
    int AwayTimeoutSeconds = PresenceSettingsStore.DefaultAwayTimeoutSeconds,
    bool AwayOnFocusLoss = false)
{
    public static PresenceSettingsData Defaults => new(new PresenceConfig());
}

/// <summary>
/// BUG-UI-43: Away &amp; auto-response is per account (Firestorm keeps <c>FSAutorespondMode</c>, the four response texts and
/// <c>AFKTimeout</c> in <c>gSavedPerAccountSettings</c>), so it lives in the section <c>presence_&lt;grid&gt;_&lt;agentId&gt;</c>
/// (<see cref="AccountScope"/>). Until v0.28.69 FEAT-UI-75 kept one shared section <c>presence</c> and every avatar read and
/// wrote it, so a text saved as one avatar answered as another.
///
/// <para><b>Migration.</b> The first time a login has no section of its own, the shared one is copied into it, once, and the
/// shared one is left in place: the next login that has no section of its own starts from the same values (which are the
/// ones everybody was using), and nothing is ever written to the shared section again. A login that already has its own
/// section is never touched, whatever the shared one holds.</para>
///
/// <para>With no tenant key (<see cref="AccountScope.Key"/> is null) nothing is read and nothing is written: the defaults
/// are returned, never a shared section.</para>
/// </summary>
public static class PresenceSettingsStore
{
    /// <summary>The viewer's <c>AFKTimeout</c> default (settings.xml): five minutes.</summary>
    public const int DefaultAwayTimeoutSeconds = 300;

    /// <summary>The section every avatar shared before BUG-UI-43. Only ever read, to seed a login's own section.</summary>
    public const string LegacySection = "presence";

    public const string SectionPrefix = "presence_";

    private static readonly string[] Keys =
    {
        "away_timeout", "away_on_focus_loss", "autorespond", "autorespond_non_friends", "send_away_response",
        "away_message", "busy_message", "autorespond_message", "non_friends_message",
    };

    public static string? SectionFor(string? gridSlug, string? agentId) => AccountScope.Section(SectionPrefix, gridSlug, agentId);

    /// <summary>The saved setup of this login, seeded from the shared section the first time. Defaults when the login is
    /// not known or has nothing saved. <paramref name="persist"/> false keeps the seeding in memory (a smoke test).</summary>
    public static PresenceSettingsData Load(IConfigStore store, string? gridSlug, string? agentId, bool persist = true)
    {
        string? section = SectionFor(gridSlug, agentId);
        if (section == null) return PresenceSettingsData.Defaults;

        if (!store.HasSection(section) && store.HasSection(LegacySection))
        {
            bool copied = false;
            foreach (string key in Keys)
            {
                object? value = store.Get(LegacySection, key);
                if (value == null) continue;
                store.Set(section, key, value);
                copied = true;
            }
            if (copied && persist) store.Save();
        }

        if (!store.HasSection(section)) return PresenceSettingsData.Defaults;

        string Text(string key, string fallback)
            => store.Get(section, key) is string v && !string.IsNullOrWhiteSpace(v) ? v : fallback;

        return new PresenceSettingsData(
            new PresenceConfig(
                Autorespond: Flag(store, section, "autorespond"),
                AutorespondNonFriends: Flag(store, section, "autorespond_non_friends"),
                SendAwayResponse: Flag(store, section, "send_away_response"),
                AwayMessage: Text("away_message", PresenceConfig.DefaultAwayMessage),
                BusyMessage: Text("busy_message", PresenceConfig.DefaultBusyMessage),
                AutorespondMessage: Text("autorespond_message", PresenceConfig.DefaultAutorespondMessage),
                NonFriendsMessage: Text("non_friends_message", PresenceConfig.DefaultAutorespondMessage)),
            store.Get(section, "away_timeout") switch
            {
                int i => i,
                long l => (int)l,
                _ => DefaultAwayTimeoutSeconds,
            },
            Flag(store, section, "away_on_focus_loss"));
    }

    /// <summary>Writes this login's setup. Without a tenant key it writes nothing and says so, so a controller that is not
    /// bound to a login cannot put anything into a file every login reads.</summary>
    public static bool Save(IConfigStore store, string? gridSlug, string? agentId, PresenceSettingsData data)
    {
        string? section = SectionFor(gridSlug, agentId);
        if (section == null) return false;

        var c = data.Config;
        store.Set(section, "away_timeout", data.AwayTimeoutSeconds);
        store.Set(section, "away_on_focus_loss", data.AwayOnFocusLoss);
        store.Set(section, "autorespond", c.Autorespond);
        store.Set(section, "autorespond_non_friends", c.AutorespondNonFriends);
        store.Set(section, "send_away_response", c.SendAwayResponse);
        store.Set(section, "away_message", c.AwayMessage);
        store.Set(section, "busy_message", c.BusyMessage);
        store.Set(section, "autorespond_message", c.AutorespondMessage);
        store.Set(section, "non_friends_message", c.NonFriendsMessage);
        store.Save();
        return true;
    }

    private static bool Flag(IConfigStore store, string section, string key) => store.Get(section, key) is true;
}
