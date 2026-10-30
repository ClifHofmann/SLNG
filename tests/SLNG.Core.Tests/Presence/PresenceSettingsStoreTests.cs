using SLNG.Core;
using SLNG.Core.Presence;
using Xunit;

namespace SLNG.Core.Tests.Presence;

/// <summary>BUG-UI-43: Away &amp; auto-response belongs to one login (grid + agent), never to the viewer.</summary>
public class PresenceSettingsStoreTests
{
    private const string Agni = "agni";
    private const string OsGrid = "hg.osgrid.org";
    private const string A = "11111111-1111-1111-1111-111111111111";
    private const string B = "22222222-2222-2222-2222-222222222222";

    private sealed class MemoryStore : IConfigStore
    {
        public readonly Dictionary<string, Dictionary<string, object>> Sections = new();
        public int Saves;

        public bool HasSection(string section) => Sections.ContainsKey(section);

        public object? Get(string section, string key)
            => Sections.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : null;

        public void Set(string section, string key, object value)
        {
            if (!Sections.TryGetValue(section, out var s)) Sections[section] = s = new Dictionary<string, object>();
            s[key] = value;
        }

        public void Save() => Saves++;
    }

    private static PresenceSettingsData Data(string away, string busy = "busy", string auto = "auto", string strangers = "strangers",
        bool autorespond = false, bool sendAway = false, int timeout = 600, bool focus = false)
        => new(new PresenceConfig(Autorespond: autorespond, SendAwayResponse: sendAway, AwayMessage: away, BusyMessage: busy,
            AutorespondMessage: auto, NonFriendsMessage: strangers), timeout, focus);

    [Fact]
    public void Two_avatars_on_one_grid_keep_their_own_texts_and_switches()
    {
        var file = new MemoryStore();
        PresenceSettingsStore.Save(file, Agni, A, Data("A is away", autorespond: true, timeout: 120, focus: true));
        PresenceSettingsStore.Save(file, Agni, B, Data("B is away", autorespond: false, sendAway: true, timeout: 1800));

        var a = PresenceSettingsStore.Load(file, Agni, A);
        var b = PresenceSettingsStore.Load(file, Agni, B);

        Assert.Equal("A is away", a.Config.AwayMessage);
        Assert.True(a.Config.Autorespond);
        Assert.False(a.Config.SendAwayResponse);
        Assert.Equal(120, a.AwayTimeoutSeconds);
        Assert.True(a.AwayOnFocusLoss);

        Assert.Equal("B is away", b.Config.AwayMessage);
        Assert.False(b.Config.Autorespond);
        Assert.True(b.Config.SendAwayResponse);
        Assert.Equal(1800, b.AwayTimeoutSeconds);
        Assert.False(b.AwayOnFocusLoss);
    }

    [Fact]
    public void Logging_in_as_B_after_A_does_not_show_As_values()
    {
        var file = new MemoryStore();
        PresenceSettingsStore.Save(file, Agni, A, Data("A is away", busy: "A busy", auto: "A auto", strangers: "A strangers", autorespond: true));

        var b = PresenceSettingsStore.Load(file, Agni, B); // B has nothing saved and there is no shared section

        Assert.Equal(PresenceConfig.DefaultAwayMessage, b.Config.AwayMessage);
        Assert.Equal(PresenceConfig.DefaultBusyMessage, b.Config.BusyMessage);
        Assert.Equal(PresenceConfig.DefaultAutorespondMessage, b.Config.AutorespondMessage);
        Assert.Equal(PresenceConfig.DefaultAutorespondMessage, b.Config.NonFriendsMessage);
        Assert.False(b.Config.Autorespond);
        Assert.Equal(PresenceSettingsStore.DefaultAwayTimeoutSeconds, b.AwayTimeoutSeconds);
    }

    [Fact]
    public void Saving_as_one_avatar_never_writes_the_shared_section_or_the_other_avatars()
    {
        var file = new MemoryStore();
        PresenceSettingsStore.Save(file, Agni, A, Data("A is away"));
        PresenceSettingsStore.Save(file, Agni, B, Data("B is away"));

        Assert.False(file.HasSection(PresenceSettingsStore.LegacySection));
        Assert.Equal("A is away", file.Get($"presence_{Agni}_{A}", "away_message"));
        Assert.Equal("B is away", file.Get($"presence_{Agni}_{B}", "away_message"));
    }

    [Fact]
    public void The_same_avatar_on_another_grid_is_another_tenant()
    {
        var file = new MemoryStore();
        PresenceSettingsStore.Save(file, Agni, A, Data("on agni", autorespond: true));

        var other = PresenceSettingsStore.Load(file, OsGrid, A);

        Assert.Equal(PresenceConfig.DefaultAwayMessage, other.Config.AwayMessage);
        Assert.False(other.Config.Autorespond);
        Assert.NotEqual(PresenceSettingsStore.SectionFor(Agni, A), PresenceSettingsStore.SectionFor(OsGrid, A));
    }

    [Fact]
    public void The_tenant_key_ignores_letter_case_and_padding()
    {
        Assert.Equal(PresenceSettingsStore.SectionFor("AGNI", A.ToUpperInvariant()), PresenceSettingsStore.SectionFor(" agni ", A));
    }

    [Fact]
    public void Migration_copies_the_shared_values_once_and_leaves_the_shared_section_alone()
    {
        var file = new MemoryStore();
        file.Set("presence", "away_message", "old away");
        file.Set("presence", "autorespond", true);
        file.Set("presence", "away_timeout", 1800);
        file.Set("presence", "away_on_focus_loss", true);

        var a = PresenceSettingsStore.Load(file, Agni, A);

        Assert.Equal("old away", a.Config.AwayMessage);
        Assert.True(a.Config.Autorespond);
        Assert.Equal(1800, a.AwayTimeoutSeconds);
        Assert.True(a.AwayOnFocusLoss);
        Assert.Equal("old away", file.Get("presence", "away_message")); // kept: the next login starts from it too
        Assert.Equal(1, file.Saves);

        // The second load finds the login's own section and neither copies nor saves again.
        PresenceSettingsStore.Load(file, Agni, A);
        Assert.Equal(1, file.Saves);
    }

    [Fact]
    public void Migration_never_overwrites_a_logins_own_values()
    {
        var file = new MemoryStore();
        file.Set("presence", "away_message", "old shared");
        PresenceSettingsStore.Save(file, Agni, A, Data("mine"));

        var a = PresenceSettingsStore.Load(file, Agni, A);

        Assert.Equal("mine", a.Config.AwayMessage);
    }

    [Fact]
    public void A_second_login_is_seeded_from_the_shared_values_not_from_the_first_logins_edits()
    {
        var file = new MemoryStore();
        file.Set("presence", "away_message", "old shared");

        PresenceSettingsStore.Load(file, Agni, A);                          // A is migrated ...
        PresenceSettingsStore.Save(file, Agni, A, Data("A rewrote it"));   // ... and then changes its text

        var b = PresenceSettingsStore.Load(file, Agni, B);

        Assert.Equal("old shared", b.Config.AwayMessage);
        Assert.Equal("A rewrote it", PresenceSettingsStore.Load(file, Agni, A).Config.AwayMessage);
        Assert.Equal("old shared", file.Get("presence", "away_message"));
    }

    [Fact]
    public void A_blank_saved_text_stands_for_the_default()
    {
        var file = new MemoryStore();
        PresenceSettingsStore.Save(file, Agni, A, Data("   "));

        Assert.Equal(PresenceConfig.DefaultAwayMessage, PresenceSettingsStore.Load(file, Agni, A).Config.AwayMessage);
    }

    [Theory]
    [InlineData(null, A)]
    [InlineData(Agni, null)]
    [InlineData("  ", A)]
    [InlineData(Agni, "00000000-0000-0000-0000-000000000000")]
    public void Without_a_tenant_nothing_is_read_and_nothing_is_written(string? grid, string? agent)
    {
        var file = new MemoryStore();
        file.Set("presence", "away_message", "old shared");

        Assert.Null(AccountScope.Key(grid, agent));
        Assert.Equal(PresenceSettingsData.Defaults, PresenceSettingsStore.Load(file, grid, agent));
        Assert.False(PresenceSettingsStore.Save(file, grid, agent, Data("x")));
        Assert.Equal(0, file.Saves);
        Assert.Single(file.Sections); // only the shared one the test put there
    }

    [Fact]
    public void A_smoke_test_can_migrate_without_saving()
    {
        var file = new MemoryStore();
        file.Set("presence", "away_message", "old shared");

        PresenceSettingsStore.Load(file, Agni, A, persist: false);

        Assert.Equal(0, file.Saves);
    }
}
