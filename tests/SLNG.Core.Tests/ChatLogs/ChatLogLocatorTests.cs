using SLNG.Core.ChatLogs;
using Xunit;

namespace SLNG.Core.Tests.ChatLogs;

// FEAT-UI-41. Settings files are cut-down copies of the real settings.xml / settings_per_account.xml;
// the folders are temporary ones. Nothing here touches a real Firestorm folder.
public sealed class ChatLogLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "slng-locator-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string Profile => Path.Combine(_root, "Firestorm_x64");
    private string Slng => Path.Combine(_root, "slng-logs");

    private static string Setting(string name, string type, string value) => $"""
        <key>{name}</key>
        <map>
          <key>Comment</key><string>x</string>
          <key>Persist</key><integer>1</integer>
          <key>Type</key><string>{type}</string>
          <key>Value</key><{(type == "String" ? "string" : "integer")}>{value}</{(type == "String" ? "string" : "integer")}>
        </map>
        """;

    private static string Llsd(params string[] settings) => "<llsd><map>" + string.Concat(settings) + "</map></llsd>";

    private void WriteFile(string relative, string content)
    {
        string path = Path.Combine(Profile, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private ChatLogTarget Resolve(ChatLogMode mode, ImNamesChoice names = ImNamesChoice.Auto, string? custom = null, string account = "clifton_howlett")
        => ChatLogLocator.Resolve(mode, Profile, Slng, custom, account, names, "Second Life");

    [Fact]
    public void Without_firestorm_settings_the_account_folder_is_inside_the_profile_folder_with_legacy_im_names()
    {
        var t = Resolve(ChatLogMode.Firestorm);

        Assert.Equal(Path.Combine(Profile, "clifton_howlett"), t.Directory);
        Assert.Equal(Profile, t.BaseDirectory);
        Assert.Equal(ImLogNameStyle.Legacy, t.Naming.ImStyle);   // the viewer's built-in default is TRUE
        Assert.False(t.Naming.DateSuffix);
    }

    [Fact]
    public void The_log_folder_set_in_firestorm_for_that_account_wins_when_it_exists()
    {
        // The maintainer's Second Life account keeps its logs in a OneDrive folder, not the profile.
        string elsewhere = Path.Combine(_root, "OneDrive", "Firestorm");
        Directory.CreateDirectory(elsewhere);
        WriteFile(Path.Combine("clifton_howlett", "settings_per_account.xml"),
            Llsd(Setting("InstantMessageLogPath", "String", elsewhere)));

        var t = Resolve(ChatLogMode.Firestorm);

        Assert.Equal(Path.Combine(elsewhere, "clifton_howlett"), t.Directory);
        Assert.Equal(elsewhere, t.BaseDirectory);
    }

    [Fact]
    public void A_configured_folder_that_does_not_exist_is_ignored_as_in_the_viewer()
    {
        WriteFile(Path.Combine("clifton_howlett", "settings_per_account.xml"),
            Llsd(Setting("InstantMessageLogPath", "String", Path.Combine(_root, "gone"))));

        Assert.Equal(Path.Combine(Profile, "clifton_howlett"), Resolve(ChatLogMode.Firestorm).Directory);
    }

    [Fact]
    public void Another_account_does_not_inherit_a_folder_set_for_this_one()
    {
        string elsewhere = Path.Combine(_root, "OneDrive");
        Directory.CreateDirectory(elsewhere);
        WriteFile(Path.Combine("clifton_howlett", "settings_per_account.xml"),
            Llsd(Setting("InstantMessageLogPath", "String", elsewhere)));

        Assert.Equal(Path.Combine(Profile, "clifton_howlett.osgrid"), Resolve(ChatLogMode.Firestorm, account: "clifton_howlett.osgrid").Directory);
    }

    [Fact]
    public void An_empty_configured_folder_means_the_profile_folder()
    {
        WriteFile(Path.Combine("clifton_howlett", "settings_per_account.xml"),
            Llsd(Setting("InstantMessageLogPath", "String", "")));

        Assert.Equal(Profile, Resolve(ChatLogMode.Firestorm).BaseDirectory);
    }

    [Fact]
    public void The_file_name_options_follow_firestorms_settings()
    {
        // The real settings.xml of the maintainer: UseLegacyIMLogNames = 0 (account style names).
        WriteFile(Path.Combine("user_settings", "settings.xml"), Llsd(Setting("UseLegacyIMLogNames", "Boolean", "0")));
        WriteFile(Path.Combine("clifton_howlett", "settings_per_account.xml"), Llsd(Setting("LogFileNamewithDate", "Boolean", "1")));

        var t = Resolve(ChatLogMode.Firestorm);

        Assert.Equal(ImLogNameStyle.Account, t.Naming.ImStyle);
        Assert.True(t.Naming.DateSuffix);
    }

    [Fact]
    public void An_explicit_choice_beats_what_firestorm_has_set()
    {
        WriteFile(Path.Combine("user_settings", "settings.xml"), Llsd(Setting("UseLegacyIMLogNames", "Boolean", "0")));

        Assert.Equal(ImLogNameStyle.Legacy, Resolve(ChatLogMode.Firestorm, ImNamesChoice.Legacy).Naming.ImStyle);
        Assert.Equal(ImLogNameStyle.Account, Resolve(ChatLogMode.Firestorm, ImNamesChoice.Account).Naming.ImStyle);
    }

    [Fact]
    public void Slng_mode_uses_slngs_own_folder_and_ignores_per_account_firestorm_paths()
    {
        string elsewhere = Path.Combine(_root, "OneDrive");
        Directory.CreateDirectory(elsewhere);
        WriteFile(Path.Combine("clifton_howlett", "settings_per_account.xml"),
            Llsd(Setting("InstantMessageLogPath", "String", elsewhere), Setting("LogFileNamewithDate", "Boolean", "1")));

        var t = Resolve(ChatLogMode.Slng);

        Assert.Equal(Path.Combine(Slng, "clifton_howlett"), t.Directory);
        Assert.False(t.Naming.DateSuffix);
    }

    [Fact]
    public void Custom_mode_uses_the_chosen_folder_and_falls_back_to_firestorm_until_one_is_chosen()
    {
        string chosen = Path.Combine(_root, "mine");

        Assert.Equal(Path.Combine(chosen, "clifton_howlett"), Resolve(ChatLogMode.Custom, custom: chosen).Directory);
        Assert.Equal(Path.Combine(Profile, "clifton_howlett"), Resolve(ChatLogMode.Custom, custom: "  ").Directory);
    }

    [Fact]
    public void Resolving_a_login_creates_nothing_on_disk()
    {
        // The smoke test boots against the real user data: choosing a folder must not make one.
        Resolve(ChatLogMode.Firestorm);
        Resolve(ChatLogMode.Slng);
        Resolve(ChatLogMode.Custom, custom: Path.Combine(_root, "mine"));

        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void A_broken_settings_file_reads_as_not_set()
    {
        WriteFile(Path.Combine("user_settings", "settings.xml"), "<llsd><map><key>UseLegacyIMLogNames");
        WriteFile(Path.Combine("clifton_howlett", "settings_per_account.xml"), "garbage");

        var t = Resolve(ChatLogMode.Firestorm);

        Assert.Equal(ImLogNameStyle.Legacy, t.Naming.ImStyle);
        Assert.Equal(Profile, t.BaseDirectory);
    }
}
