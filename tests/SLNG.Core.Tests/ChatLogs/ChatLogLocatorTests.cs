using SLNG.Core.ChatLogs;
using Xunit;

namespace SLNG.Core.Tests.ChatLogs;

// FEAT-UI-41. The base folder is the one chosen for the account, else SLNG's default; the account's own
// folder is Firestorm's name for it. Nothing is read from disk and nothing is created -- SLNG does not
// look at any other viewer to decide.
public sealed class ChatLogLocatorTests
{
    private static readonly string Own = Path.Combine(Path.GetTempPath(), "slng-locator-" + Guid.NewGuid().ToString("N"), "own");
    private static readonly string Chosen = Path.Combine(Path.GetTempPath(), "slng-locator-" + Guid.NewGuid().ToString("N"), "chosen");

    private static ChatLogTarget Resolve(string? chosen, ImLogNameStyle style = ImLogNameStyle.Legacy, string account = "clifton_howlett")
        => ChatLogLocator.Resolve(chosen, Own, account, style, "Second Life");

    [Fact]
    public void An_account_with_nothing_chosen_uses_slngs_default_folder()
    {
        var t = Resolve(null);

        Assert.Equal(Path.Combine(Own, "clifton_howlett"), t.Directory);
        Assert.Equal(Own, t.BaseDirectory);
        Assert.Contains("default", t.Why);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_choice_is_no_choice(string blank)
    {
        Assert.Equal(Own, Resolve(blank).BaseDirectory);
    }

    [Fact]
    public void A_chosen_folder_is_the_base_and_the_account_folder_goes_inside_it()
    {
        // Choosing the folder Firestorm uses for an account puts SLNG into the very folder Firestorm reads.
        var t = Resolve(Chosen, account: "clifton_howlett.osgrid");

        Assert.Equal(Path.Combine(Chosen, "clifton_howlett.osgrid"), t.Directory);
        Assert.Equal(Chosen, t.BaseDirectory);
        Assert.Contains("chosen", t.Why);
    }

    [Fact]
    public void The_im_name_style_and_system_name_are_passed_through_and_the_date_suffix_is_off()
    {
        var t = ChatLogLocator.Resolve(null, Own, "x", ImLogNameStyle.Account, "Grid");

        Assert.Equal(new ChatLogNaming(ImLogNameStyle.Account, false, "Grid"), t.Naming);
    }

    [Fact]
    public void Resolving_a_login_creates_nothing_on_disk()
    {
        Resolve(null);
        Resolve(Chosen);

        Assert.False(Directory.Exists(Path.GetDirectoryName(Own)));
        Assert.False(Directory.Exists(Path.GetDirectoryName(Chosen)));
    }
}
